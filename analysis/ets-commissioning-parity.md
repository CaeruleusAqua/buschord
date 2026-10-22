# BusChord ↔ ETS6 parity for real commissioning — download, diagnostics, bus

Read-only assessment. Branch `agents/assess/ets-commissioning`, base `08a0f6a`
(verified: `08a0f6a58b1a51716c59e0f233837aa28b3d9e46`, worktree
`C:\projects\ai_work\agent-wt\assess-ets-commissioning`). No product, test, CI,
docs or existing report was changed; this file is the only repository change.

Method: static reading of the current bus stack (`src/Knx.Bus`,
`src/Knx.Mcp`, `src/Knx.App/Workbench/Sessions`), the recorded live evidence
(`analysis/knx-bus-read-test-plan.md`, `analysis/router-discovery.md`,
`analysis/workbench-v4-completion-plan.md` §H8) and the ETS6 reconstruction
(`analysis/ets/commissioning.md`, `analysis/ets/project.md`,
`analysis/ets/addresses.md`). Offline test binaries were run at this base:
`tests/Knx.Bus.Tests` (`ALLE CHECKS OK`), `tests/Knx.Mcp.Tests`
(`ALL TESTS PASSED`), `tests/Knx.WorkbenchSessions.Tests` (`128 checks passed`).
**No frame was sent to a bus, no device was contacted, no write or download was
run anywhere in this assessment.**

## 1. Verdict

**NO-GO for using BusChord to commission a real installation.**
**Conditional GO as a read-only companion** (discovery, tunnel/routing monitor,
group reads, descriptor/scan diagnostics) — after the three live verifications
in §5 P0 and only with ETS6 kept as the download/fallback tool.

Reasoning in one paragraph: every write and download path is correctly disabled
by policy (`KnxBus.cs:15` `WritesEnabled => false`; MCP `bus_write` refuses;
the Workbench programming queue is demo-only and rejects real jobs), so the
commissioning act itself — full/partial download, individual-address write,
parameters/group-communication load, programming mode, secure commissioning —
cannot be performed at all. The read side is materially better than at the time
of the read-test plan: the cEMI/APCI/descriptor/routing defects B1–B5 are fixed
in code (`f3e9468`, an ancestor of this base), tunnel and routing are recorded
as live-verified read-only, and the offline wire checks pass. But no live
group-value response, no live descriptor read/scan and no second-tunnel
conflict have been demonstrated yet, so even read-only use on a real
installation is not yet warranted without those verifications.

Top blockers, in order:

1. **No download pipeline.** `DownloadSession.cs` writes a raw image to a fixed
   start address, reads the descriptor without using it, and has no mask/memory
   map, no parameters/group-table separation, no unload/compare, no download
   log. It is additionally gated off.
2. **No programming-mode handling** anywhere: no query, no scan, no set/clear,
   no "press programming button" state.
3. **No safe-load semantics**: no image build from the project, no AP2/mask/
   manufacturer verification, no partial/differential download, no restart
   completion wait.
4. **No secure commissioning**: the keyring reader is best-effort and not
   validated against an ETS `.knxkeys`; no certificate/FDSK store, no secure
   download, no secure tunnelling; the crypto primitives are offline-only and
   marked "verify against a real secure installation".
5. **No write frame has ever been validated live.** Group write, IA write,
   memory write and restart exist as frames with offline fixtures only; the
   first real write test must happen on an isolated bench device.

## 2. Capability matrix

Status vocabulary: **done** = usable for its purpose; **partial** = present but
incomplete or unproven; **missing** = not implemented; **deliberately
unsupported** = consciously out of scope and documented as such.

| # | ETS6 capability | BusChord status | One-line evidence |
|---|---|---|---|
| 1 | Interface discovery/connection | done (KNXnet/IP); partial overall | `Discovery.cs`, `KnxTunnel.cs`, `KnxRouting.cs`; live-proven |
| 2 | Individual addressing | partial | IA read only via broadcast scan; IA write frame exists, unreachable |
| 3 | Full/partial download | missing (gated stub) | `DownloadSession.cs:39-102`; `KnxBus.cs:15,99-112` |
| 4 | Application program/parameters download | missing | no image builder, no parameter/AP2 handling in `src` |
| 5 | Group address download | missing | no group-communication table code anywhere |
| 6 | Device descriptor reads | partial | `Management.cs:17-32`, `Diagnostics.cs:147-271`; not live-verified |
| 7 | Programming-mode handling | missing | no prog-mode code in `src` |
| 8 | Mask/management model checks | missing | name table only, `Diagnostics.cs:88-103`; descriptor unused by download |
| 9 | Download confirmation/error handling | partial (gated) | `DownloadSession.cs` stages/verify; no log, no user-decision states |
| 10 | Secure commissioning (FDSK/keyring/tunnelling) | partial (offline only) | `Security/*`; not wired; `Sessions/README.md:53` |
| 11 | Line scan/diagnostics | partial | `Diagnostics.cs`; MCP `bus_scan`/`bus_device_info`; not live-verified |
| 12 | Bus/group monitor | partial | MCP recorder + `bus_statistics`; Workbench monitor demo-only |
| 13 | Project comparison/signature | partial | `ProjectComparer`; `ConfigurationSignature` + `NeedsProgramming` |
| 14 | Backup/restore | partial | `ProjectBackup` (local `.knxproj` copies); no ETS archive, no keyring export |
| 15 | Retry/queue semantics | partial (demo-only) | `ProgrammingSession` queue; tunnel ACK/heartbeat retries |

### 2.1 Interface discovery/connection — done (KNXnet/IP)

ETS6: connector-parameter model (tunnel/routing/USB/RDAC/unicast/IoT/cloud),
discovery → editable connection rows, capability model, reconnect policy
(`analysis/ets/commissioning.md` §1).

BusChord: SEARCH (multicast) + DESCRIPTION (unicast) with DIB parsing
(DeviceInfo, SuppSvcFamilies, IpConfig, IpCurConfig, KnxAddress) and
per-interface sockets (`Discovery.cs:28-116,171-207`); tunnel with
CONNECT/CONNECTIONSTATE/DISCONNECT, both sequence counters, ACK repeat-once and
3-attempt heartbeat (`KnxTunnel.cs:44-79,240-285,481-503`); routing joins
224.0.23.12, binds 3671 and fails loudly when the join fails
(`KnxRouting.cs:33-62,80-127`); one configured host + tunnel address in
settings (`SettingsWindow.axaml.cs:23-38`), used by the legacy window
(`MainWindowViewModel.cs:784-813`), not by the Workbench.

Live-proven: targeted SEARCH/DESCRIPTION (E2–E4), tunnel connect/60 s
heartbeat/clean disconnect (E5–E7) in `analysis/knx-bus-read-test-plan.md`
§2; post-fix re-verification of tunnel and routing recorded in
`analysis/workbench-v4-completion-plan.md:396-398`.

Gaps vs ETS6: no connector-parameter object model / connection-string
persistence / profile list, no auto-reconnect or connect-fail-fast policy, no
extended search, no tunnelling-slot DIB (free/authorized/usable), no USB
HID/EMI, no device-management (RDAC) connector, no IoT/cloud, no secure
service-family negotiation. For commissioning, the KNXnet/IP subset is
sufficient *if* the gateway is KNXnet/IP; USB-only installations are out.

### 2.2 Individual addressing — partial

ETS6: IA read/write, address check, free/occupied, serial-number and domain
addressing, park/renumber strategies (`analysis/ets/commissioning.md` §6,
`analysis/ets/addresses.md` §4).

BusChord: `Management.IndividualAddressRead()` (`Management.cs:34-41`) is used
by the broadcast scan (`Diagnostics.cs:173-211`); `Management.
IndividualAddressWrite` exists (`Management.cs:43-50`) but has no caller, no
tool and no UI. No serial-number lookup, no domain address, no address
uniqueness re-check after a load. The IA read was **never live-run**
(read-test-plan R8 blocked: broadcast management frame + missing authorization).

Before commissioning: implement a confirmed IA-write device service with
per-device undo notes, address-occupied detection and post-write verification;
verify the IA read live on an approved line first.

### 2.3 Full/partial download — missing (gated stub)

ETS6: `LoadDeviceOptions` with Adr/Prg/Par/Grp/Cfg variants, differential
image/stamp, load-procedure pipeline, per-device programming state
(`analysis/ets/commissioning.md` §2).

BusChord: `DownloadSession.RunAsync` (`DownloadSession.cs:39-102`) reads
descriptor type 0, writes `IApplicationProgram.Image` from `StartAddress` in
`ChunkSize` chunks with read-back compare, then `Restart(erase:false)`. The
descriptor value is never checked (`:44-49`), there is no segment handling, no
partial/differential path, no unload/reset/compare/device-info. The only
timeout is a fixed 3 s (`:28`). The entry points are disabled:
`KnxBus.WritesEnabled => false` (`KnxBus.cs:15`) and `DownloadAsync` throws
(`KnxBus.cs:99-112`); `e774380` additionally gated the legacy window command.
The Workbench queue is demo-only (`ProgrammingSession.cs:115-133`, real jobs
rejected at `:121-122`; UI enqueues demo at `WorkbenchWindow.axaml.cs:2171`).

Before commissioning: build a load pipeline per `analysis/ets/commissioning.md`
§11.1 (descriptor/mask gate → programming mode → memory map from master data →
staged address/parameters/group/app → readback where supported → log), keep it
behind arm/confirm + dry-run, and prove it on an isolated line.

### 2.4 Application program/parameters download — missing

ETS6: `DeviceImage.CreateImage` builds image + `LoadProcedure` with mask/
manufacturer/resource verification, `ApplyParametersTask`,
`ApplyGroupCommunicationTablesTask`, AP2 patching, IP/RF/router config
(`analysis/ets/commissioning.md` §2).

BusChord: no image builder, no load procedure, no AP2 handling, no parameter
serialization to a device image. `ConfigurationSnapshot.Freeze` produces a
JSON of native fields with a SHA-256 signature for drift detection only and
says so explicitly (`ConfigurationSnapshot.cs:9-38`); the programming state
hash is "not a downloadable device image" (`DeviceProgrammingState.cs:53-57`).
The `DeviceLoadFlags` enum mirrors Adr/Prg/Par/Grp/Cfg and the reset matrix
(`DeviceProgrammingState.cs:12-26,120-143`) — good bookkeeping, but nothing
writes a device.

### 2.5 Group address download — missing

ETS6: group communication tables are a separate load component
(`PartialLoadGroupCommunication`, `ApplyGroupCommunicationTablesTask`).

BusChord: no group-communication table code in `src` at all; only the
`DeviceLoadFlags.GroupAddresses` bookkeeping flag exists
(`DeviceProgrammingState.cs:21`). Links live in the project model and the
configuration signature, never in a device image.

### 2.6 Device descriptor reads — partial (implemented, not live-verified)

ETS6: descriptor read with type in the APCI bits, mask version from type 0,
used by scan and check.

BusChord: `Management.DeviceDescriptorReadTelegram` puts the 4-bit type in the
APCI octet (`Management.cs:17-32`, `Cemi.cs:127`), types 0–3 only;
`DeviceDescriptor.Parse`/`MaskVersion`/`MaskName` (`Diagnostics.cs:60-83`);
probe with bounded concurrency and 1–3 attempts (`Diagnostics.cs:147-158,
244-271`); MCP `bus_device_info`/`bus_scan` open their own short-lived tunnel
on 1.1.249 (`BusDiagnostics.cs:8-31`). Offline fixtures cover the encoding
(`tests/Knx.Bus.Tests/Program.cs:239-252`).

Not live-verified: both `Diagnostics.cs:139` and `BusDiagnostics.cs:11` say
"unverified live", and the completion plan explicitly records that no
descriptor live scan was run (`analysis/workbench-v4-completion-plan.md:396-398`).

Before commissioning: one approved device descriptor read, then one line scan;
capture the frames as golden fixtures.

### 2.7 Programming-mode handling — missing

ETS6: `ProgModeScan` (direct/domain/installation), read/set programming mode,
"press programming button" as a first-class user-decision state
(`ProgrammingModeAlert`), `ReadAddressesInProgModeAsync` etc.
(`analysis/ets/commissioning.md` §2, §6).

BusChord: no programming-mode code anywhere in `src` (grep for
`ProgMode|ProgrammingMode` returns nothing outside this document's sources).
The download path writes without entering or checking programming mode — one
of the reasons it is gated.

### 2.8 Mask/management model checks — missing

ETS6: `VerifyCompatibleMaskVersionTask`, `VerifyCompatibleManufacturerTask`,
`VerifyAdditionalResourcesTask`, master-data memory maps, `UnknownMaskVersion
Exception`, AP2 mismatch options.

BusChord: `MaskVersions` is a 13-entry name table only
(`Diagnostics.cs:88-103`); `DownloadSession` reads the descriptor and does not
compare it against anything (`DownloadSession.cs:44-49`). There is no memory
map, no segment model, no manufacturer check, no AP2 check.

### 2.9 Download confirmation/error handling — partial (gated)

ETS6: `HawkException` taxonomy, user-decision callbacks (programming mode, key,
certificate, address occupied, prompt-before-full-download), `Warnings`
aggregation, `DownloadLogData` per attempt, metrics
(`analysis/ets/commissioning.md` §9).

BusChord: `DownloadStage`/`DownloadProgress`/`DownloadResult`
(`DownloadSession.cs:16-20`), per-chunk read-back compare and clear failure
messages (`:56-84`); `BusErrorKind` + windowed `BusStatistics`
(`Diagnostics.cs:334-456`). Missing: attempt log with interface identity and
image hash, warnings aggregation, `NeedsUser` states, download log, redaction
policy. The queue records last error/attempts only
(`DeviceProgrammingState.cs:102-117`).

### 2.10 Secure commissioning (FDSK/keyring/secure tunnelling) — partial, offline only

ETS6: keyring with PBKDF2 password hashing, signature creation/validation,
certificate collection with FDSK, secure download (tool key, access level,
secure sync, security mode), secure tunnelling/routing, sequence counters
(`analysis/ets/commissioning.md` §8).

BusChord: keyring model JSON + best-effort ETS XML (`Keyring.cs:88-203`), with
an explicit caveat that the ETS `<Keyring>` mapping is inferred and the
`.knxkeys` ZIP container is not read (`Keyring.cs:140-142`); keys parse as
32-hex only (`Keyring.cs:231-237`); no PBKDF2/signature/encrypted password
strings. `SecureTelegram`/`SecureWrapper`/`SecureKeys` are offline primitives
with "verify against a real secure installation" comments
(`SecureTelegram.cs:34-37`, `SecureWrapper.cs:40,183`). No certificate/FDSK
store, no sequence-counter persistence, no secure download, no secure
tunnelling, no wiring into MCP or Workbench; `Sessions/README.md:53` states
keyring/FDSK/secure download are unavailable.

Before commissioning any secure device: keyring read/validate only (with a
copy of a real `.knxkeys`), then certificate store, then counters, then secure
download; secure tunnelling last. Never disable backbone security
automatically.

### 2.11 Line scan/diagnostics — partial (read-only, not live-verified)

ETS6: `LineScan.ScanAsync`, `ProgModeScan`, device workbench (check against
project, set prog mode, flash LED, read addresses by prog mode), printable
scan report, support-bundle wizard (`analysis/ets/commissioning.md` §6).

BusChord: read-only line/bus scan (broadcast IA read → descriptor per
responder) and `CheckAddresses` duplicate/missing/unexpected comparison
(`Diagnostics.cs:135-331`); MCP `bus_scan`/`bus_device_info` with project
comparison (`McpServer.Tools.cs:1026-1105`); statistics
(`:1107-1121`). Missing: programming-mode scan, device check against the
project beyond addresses, set/clear programming mode, LED flash, segment-aware
scan, slow-device handling, printable report. `LineScanSession` in the
Workbench is a 20 ms-per-device simulation (`LineScanSession.cs:6-7,31-90`).

### 2.12 Bus/group monitor — partial

ETS6: bus monitor, group monitor, bus-activity monitor; trigger/conditional
recording, rotation/retention, security/decryption columns, statistics report,
record start/stop/comment markers (`analysis/ets/commissioning.md` §5).

BusChord: `TelegramRecorder` (CSV/JSONL/XML, ETS CSV, replay) and MCP
`bus_status`/`bus_statistics`; `MonitorSession` with a disk-backed full CSV,
500-row display tail, filters and pause (`MonitorSession.cs:37-402`). Real
group mode exists as a receive-only seam (`ConfigureReal`,
`MonitorSession.cs:99-123,200-216`) but nothing calls it; the Workbench
hardcodes demo (`WorkbenchWindow.axaml.cs:2051`; badge
`WorkbenchStrings.cs:253-254`); real bus-monitor mode is rejected
(`MonitorSession.cs:109-110`). Missing: trigger/retention/rotation, per-row
security/decryption state, statistics report, medium/RF info, column
persistence, and any wired real UI (interface selector, connect/disconnect).

### 2.13 Project comparison/signature — partial

ETS6: `ProjectComparer` (content and history, archive/import contexts), PDF
difference report (`analysis/ets/project.md` §2.12).

BusChord: `ProjectComparer.Compare` (two loaded projects, added/removed/
changed installations, devices, group addresses) via MCP `project_compare`
(`McpServer.Tools.cs:230-235`); for commissioning drift, the per-device
`ConfigurationSignature` + `NeedsProgramming` + `DeviceProgrammingReport`
(`ConfigurationSnapshot.cs:9-38,46-67`, `DeviceProgrammingState.cs:58-77,
239-267`) is the operative mechanism and is a genuine improvement over "no
state". Missing: archive/version comparison, PDF report, parameter/com-object
diff depth. The signature is **not** an ETS device fingerprint and must never
be treated as proof that a device is loaded.

### 2.14 Backup/restore — partial

ETS6: project archive with versions/check-in/check-out/locks, recovery points,
database backup, encrypted archives, keyring export
(`analysis/ets/project.md` §2.8-2.11).

BusChord: `ProjectBackup` local `.knxproj` copies with retention, SHA-256
manifest, restore to a caller-chosen path, and an automatic backup before each
overwrite (`docs/project-backup.md`); programming state is exported/imported
as a versioned, project-bound JSON side file without payloads or keys
(`DeviceProgrammingState.cs:183-237`, `ProgrammingSession.cs:349-364`).
Missing: ETS archive/versions/locks, recovery points, keyring export, encrypted
archive write (encrypted ETS projects remain read-only, per
`analysis/ets/project.md` §2.11). For a real commissioning, the local backup is
the minimum rollback artifact — it exists.

### 2.15 Retry/queue semantics — partial (demo-only)

ETS6: download queue with conflict check, cancellation, per-operation history,
reconnect/fail-fast, restart wait, address re-check
(`analysis/ets/commissioning.md` §4, §9).

BusChord: `ProgrammingSession` queue with `Queued|Running|Done|Failed|
Cancelled`, one job at a time, interface lease (`InterfaceActivity.Programming`,
`ProgrammingSession.cs:252-319`), per-device busy protection (`:180-186`),
manual `Retry` (`:188-196`), attempt counting and last-error state
(`DeviceProgrammingState.cs:102-117`), `ResetForChange` matrix (`:120-143`).
Execution is demo-only and there is no `NeedsUser` state, no deadline, no
bulk API, no attempt history with interface identity per run. Transport
retries: tunnel ACK repeat-once and 3-attempt heartbeat
(`KnxTunnel.cs:240-285,481-503`); scan 1–3 attempts
(`Diagnostics.cs:44-52,244-271`). No automatic retry of a partial write — and
none should exist until re-read-before-retry is implemented.

## 3. Live-proven reads vs policy-gated writes

### 3.1 Live-proven (read-only)

| Capability | Evidence |
|---|---|
| SEARCH/DESCRIPTION discovery, DIB parse | read-test-plan E2–E4; `analysis/router-discovery.md` |
| Tunnel CONNECT/ACK/sequence/DISCONNECT | read-test-plan E5; `router-discovery.md` §"Tunnelling live verification" |
| 60 s heartbeat, state stays Connected | read-test-plan E7 |
| Routing passive listen (external socket pre-fix; post-fix join/leave recorded) | read-test-plan E1; completion-plan `:396-398` |
| Timeout behaviour and `L_Data.con` filtering | completion-plan `:396-398`; offline `BusWireChecks.ConfirmationFilterAsync` |

### 3.2 Implemented, offline-verified, **not live-proven**

| Capability | Why it is not proven |
|---|---|
| Group-value read matching (`GroupValueResponse` from a real device) | no live response seen; completion plan: "kein Gruppenantwort-Livescan" |
| Descriptor read, bus/line scan, address check | never sent live post-fix; completion plan: "kein Descriptor-Livescan" |
| IA read broadcast | never run (R8 blocked) |
| Second tunnel / interface conflict | never run (R11) |
| All write frames (group write, IA write, memory write, restart) | never sent live; offline fixtures only |

### 3.3 Policy-gated write/download paths (must stay gated)

- `KnxBus.WritesEnabled => false` (`KnxBus.cs:15`); `GroupValueWriteAsync`
  throws (`:70-79`); `DownloadAsync` throws (`:99-112`).
- MCP `bus_write` refuses with an explicit policy message
  (`McpServer.Tools.cs:276-283,1005-1008`).
- Workbench programming queue rejects real jobs without an injected executor
  and the product injects none (`ProgrammingSession.cs:121-122`;
  `WorkbenchSessions.cs:23-28`); the UI enqueues `demo-ip` jobs only
  (`WorkbenchWindow.axaml.cs:2171`).
- No MCP tool or Workbench action exists for IA write, restart, memory write or
  download.

**No real writes or downloads were run in this assessment.** The read-test-plan
authorization (reading only) remains the standing boundary; the completion
plan's H8.4 write-release item is still open.

## 4. Read-test-plan status update (B1–B5)

The read-test plan was written at base `95f842b` (pre-fix). At this base:

| Plan item | Status at `08a0f6a` |
|---|---|
| B1 group/individual flag | **fixed** — `Cemi.cs:216` uses bit 7; `Control2` `:112-116` |
| B2 APCI/payload octets | **fixed** — `Cemi.cs:70-72,119-156,186-210`; offline fixtures |
| B3 `L_Data.con` as telegram / `bus_read` matching | **fixed** — `KnxTunnel.cs:24-35,180-204,443-474`; MCP waits for response `:969-1003` |
| B4 descriptor read encoding | **fixed** — `Management.cs:17-32`, `Cemi.cs:127`; offline check `Program.cs:239-252` |
| B5 routing stub | **fixed** — `KnxRouting.cs:33-62` joins or fails; `KnxBus.cs:49-68` binds port 3671 |
| R5/R6/R7/R8/R9/R12 | stale; group response, descriptor/scan, IA read and routing join still not live-proven |
| R10 ACK retry | loopback only (`KnxTunnel.cs:240-285`) |
| R11 interface conflict | not run |

The fix commits `8b2b678` (bus-cemi branch, live re-verification) and
`f3e9468` (this branch) are identical for `src/Knx.Bus`, `src/Knx.Mcp` and
`tests/Knx.Bus.Tests`; the only difference is test-fixture hygiene, so the
recorded live re-verification applies to this base.

## 5. Prioritized implementation backlog

### P0 — make the read side trustworthy; lock the write side (no writes)

| ID | Item | Acceptance test | Safety gate |
|---|---|---|---|
| P0-1 | Live descriptor read, then one line scan | `bus_device_info` returns the expected mask for an approved device; `bus_scan` finds the project addresses; frames saved as golden fixtures | user-approved device + maintenance window; read-only; abort on malformed/negative response |
| P0-2 | Live group-value read response | `bus_read` on an approved GA returns a `GroupValueResponse` from the expected device; `L_Data.con` is not accepted; timeout path reports cleanly | user names GA, device and DPT; ETS closed; ≤3 attempts |
| P0-3 | Second-tunnel/interface-conflict test | a second `bus_connect` surfaces the gateway's CONNECT error status verbatim; `InterfaceCoordinator` behaviour documented | ETS session confirmed interruptible |
| P0-4 | Wire fixtures + offline conformance for management frames | byte-vector tests for IA write, memory write/read, restart, descriptor types 1–3 match the spec | offline only |
| P0-5 | Single write gate: per-capability arm/confirm + audit log | tests prove every write API refuses by default and cannot be enabled by opening/selecting a project; enabling requires an explicit, logged operator action | review of the gate by the owner before merge |

### P1 — minimum viable safe download on an isolated bench line

| ID | Item | Acceptance test | Safety gate |
|---|---|---|---|
| P1-1 | Programming-mode state machine (query, wait, `NeedsUser` "press button", timeout) | bench device: job pauses visibly until the button is pressed, then proceeds; no silent timeout | bench device only |
| P1-2 | Descriptor/mask gate + memory map for one mask family (e.g. System B/System 7), refuse unknown | unknown mask refuses with a clear message; memory map derived from master data, not guessed | refuse rather than guess; no writes |
| P1-3 | Image builder + staged load (address → parameters → group tables → application) with AP2/mask/manufacturer checks, restart wait, readback where supported, download log | bench device is downloadable and behaves correctly; ETS project check still clean; every attempt logged with interface identity and image hash | arm/confirm + dry-run that prints image and addresses |
| P1-4 | Wire the load pipeline into the `ProgrammingSession` queue (deadline, attempt history, bulk start, per-device serialization) | queue tests: one job per device, cancel/retry semantics, deadline enforced, history survives session export | real executor stays explicit; demo stays the default |
| P1-5 | Bench end-to-end acceptance incl. rollback | download a spare device on an isolated line; verify with ETS6 group monitor and device behaviour; restore via ETS on failure | never on a live installation first; ETS backup present |
| P1-6 | Partial/differential download | only after P1-5: valid stamp → partial; mismatch → full load with explicit confirmation | separate release gate |

### P2 — diagnostics and remaining parity

| ID | Item | Acceptance test | Safety gate |
|---|---|---|---|
| P2-1 | Programming-mode scan (read-only), device check vs project, LED flash | scan lists devices in prog mode without changing them; check reports per-device differences | read-only first; set/clear prog mode and flash LED as separately confirmed device services |
| P2-2 | Real monitor UI (interface selector, connect/disconnect) + triggers/retention/security columns/statistics | live group monitor in the Workbench with the demo badge removed only when `SessionOrigin.Real` | group mode first; bus-monitor mode only with a supporting adapter |
| P2-3 | Project-check completion (power, repeater, router/coupler, battery broker, ETS3 identifier limits) | pure-function checks with per-check settings and report model; limits cite their master-data source | report-only, no auto-repair |
| P2-4 | Secure chain: `.knxkeys` validation → certificate store → sequence counters → secure download → secure tunnelling | each stage read-only first; secure download only on a secure bench device with a keyring copy | never auto-disable backbone security; keys never in logs/exports |
| P2-5 | Connector model/profiles, auto-reconnect policy; extended search + slot DIB; USB/RDAC later | profile round-trip tests; discovery shows slot availability; reconnect behaviour measured | interface reconfiguration stays a separate confirmed operation |
| P2-6 | ETS-archive-style project versions/check-in (project side, `analysis/ets/project.md` §2.9) | archive tests with two versions, lock and restore | out of the bus path; no keyring export before P2-4 |

## 6. Hardware/interface prerequisites

- KNXnet/IP gateway with free tunnelling slots — known device: MDT SCN-IP100.03
  IP Router mit Sec, IA 1.0.0, multicast `224.0.23.12:3671`, families 2/3/4/5/7
  (read-test-plan §2). A second gateway (or second host) is needed for the
  conflict test.
- A NIC on the KNX segment that can actually join the multicast group: in the
  recorded environment the Ethernet adapter failed to join, the Wi-Fi adapter
  worked, and the VPN adapter joined but carried no KNX traffic. Verify per
  host before any routing test; VPN adapters are a false positive.
- ETS6 with the real project as fallback and rollback, plus a project backup.
  ETS must be closed while BusChord holds a tunnel (channel conflicts).
- For the first write/download tests: an isolated line or a spare device, a
  maintenance window, and a way to observe the device independently (ETS group
  monitor or a second tool). Never a live installation as the first target.
- For secure tests: a Data Secure device and a **copy** of the keyring (never
  the production keyring); record the FDSK beforehand; a way to restore the
  device if security state is touched.
- For wire validation: a capture of the frames BusChord sends (the tunnel logs
  hex lines via `LogLine`) compared against the spec and against ETS-generated
  traffic on the same device.

## 7. Limits of this assessment

- Static reading plus the three offline test suites; no bus, gateway or device
  was contacted; no ETS was executed. Live claims are taken from the recorded
  evidence in `analysis/knx-bus-read-test-plan.md`,
  `analysis/router-discovery.md` and `analysis/workbench-v4-completion-plan.md`
  §H8, not re-measured here.
- ETS6 capability statements come from the REA reconstruction
  (`analysis/ets/commissioning.md`), which is metadata-based; numeric timeouts,
  retry counts and decision tables there are unknown, not guessed.
- File:line references are against base `08a0f6a` and go stale with the next
  bus change; the capability statuses are the durable part.
- The Workbench UI state was read from code, not exercised interactively.
