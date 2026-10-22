# BusChord readiness for complete KNX projects

Read-only assessment. Base `08a0f6a` (`Refresh plan header and rewrite references`,
2026-10-22), branch `agents/assess/project-readiness`, worktree
`C:\projects\ai_work\agent-wt\assess-project-readiness`. Verified: `08a0f6a` is an
ancestor of `development`, the reachable history is linear (0 merge commits,
226 commits). No product, test, CI, docs or existing report was changed; this file
is the only addition.

**Question.** Is BusChord suitable today to execute a complete KNX project
end-to-end — acquisition, project creation, topology, hardware selection, group
addresses, links, parameters, documentation, on-site installation, physical
addressing, download/commissioning, diagnostics, acceptance, handover,
maintenance?

**Method.** Static reading of the product sources, the MCP tool surface, the
report/export layer, the bus stack and the existing analysis reports
(`analysis/ets/*`, `analysis/workbench-v4-*`, `analysis/knx-bus-read-test-plan.md`,
`analysis/router-discovery.md`, `analysis/release-notes-v4.md`,
`analysis/security-final.md`, `README.md`, `docs/manual.md`,
`docs/workbench-domain.md`). No bus was contacted, no build was run, no ETS
installation was inspected for this report.

**What this report does not claim.** No live bus write, no download, no secure
commissioning and no ETS round-trip was executed or observed. Every "done" below
means "present in the product path and covered by a test, self-check or a
documented read-only live run" — never "verified against a real installation".
Where a claim rests on another report, the report is cited; where a claim rests on
code, `file:line` is cited.

**Post-report live note (2026-10-22).** A later read-only run against the real
gateway is documented in `analysis/knx-live-read-acceptance.md`: tunnel connect,
12 group frames, descriptor reads for 1.0.0/1.1.29/1.3.3 and one real
GroupValueResponse on 3/1/15 passed; no write/download/secure frame was sent.
The P12 evidence is therefore no longer descriptor-unverified, while P10/P11
(physical addressing, download, commissioning) remain blocked by policy and
implementation and the overall NO-GO for real commissioning is unchanged.

Status vocabulary: **done** = present and evidenced; **partial** = present with a
missing step, affordance or unverified wire path; **missing** = not reachable.

---

## 1. The phases of a complete KNX project

A complete commercial KNX project, as ETS6 and the installer trade understand it,
has these phases. They are used unchanged in §2 and §5.

| # | Phase | Typical deliverable |
|---|---|---|
| P1 | Acquisition / requirements | Function list, room book, device counts, budget, bus topology sketch |
| P2 | Project creation | Project file with name/ID/type, group-address style, default topology |
| P3 | Topology (area/line/segment) | Areas, lines, segments, couplers, medium types, domain addresses |
| P4 | Device/hardware selection | Products from the catalogue, hardware/program choice, placement, bus power check |
| P5 | Group addresses | Ranges, addresses, names, datapoint types, styles |
| P6 | Links (associations) | Communication objects linked to group addresses, flags, sending address |
| P7 | Parameter configuration | Per-device application parameters, channels, visibility |
| P8 | Documentation / export | Reports, CSV, labels, semantic export, project archive |
| P9 | On-site installation support | Label sheets, device lists, topology plans, wiring/placement help |
| P10 | Physical addressing | Write/read individual addresses, programming mode, device identification |
| P11 | Download / commissioning | Device image, parameters, group tables, restart, per-device state |
| P12 | Diagnostics / monitoring | Line scan, bus/group monitor, recorder, error statistics, device check |
| P13 | Acceptance / test protocol | Signed per-device and per-function test record, defect list |
| P14 | Handover / as-built | Final documentation matching the installed state, revision record |
| P15 | Maintenance / versioning | Backups, versions, archive, change history, re-commissioning after change |

---

## 2. Phase-by-phase coverage

### P1 Acquisition / requirements — **missing**

There is no requirements capture of any kind: no room book, no function list, no
device-count/budget model, no scope or trade structure. `Project` carries only
`Id`, `Name`, `Comment`, `ProjectStart`, `Guid`, master data and installations
(`src/Knx.Core/Model.cs:98-118`). The ETS counterpart (project type, project
number, tags, completion status, project image, attachments, trade structure) is
absent, as the REA comparison already states
(`analysis/ets/project.md:650-654`, `:604-608`).

**A professional installer cannot** document what the customer asked for, cannot
trace a delivered device back to a requirement, and cannot produce a scope or
quantity take-off from the tool.

### P2 Project creation — **done (offline)**

`NewProjectWindow` asks for name, comment, group-address style and area/line
counts (`docs/manual.md:56-61`); `Knxproj.NewProject` writes the native archive
immediately. The ETS project-creation **wizard** (templates, function/group
generation, save-as-template, project type) does not exist
(`analysis/ets/project.md:102-105`).

**Cannot:** create a project from a reusable template, or from the function/room
structure a customer brief implies; no project password at creation.

### P3 Topology (area/line/segment) — **partial**

Model: `Installation → Area → Line → Segment → DeviceInstance` with 4-bit
area/line addresses, medium reference per line
(`src/Knx.Core/Model.cs:29-56`, `:82-95`). Create/rename/delete/move/copy and
address change exist with undo (`analysis/release-notes-v4.md:63-66`).
`AddressStrategy.TryNextIndividual` allocates the lowest free device number per
line (`src/Knx.Core/AddressStrategy.cs:15-30`).

Missing, and explicitly recorded as not modelled: segment `Number`, `MediumTypeId`,
`DomainAddress`, `CouplerDevice`; area `IsBackbone`/`MainLine`; coupler capability
flags (`docs/workbench-domain.md:113-116`, `analysis/ets/addresses.md:578-582`).
`Segment.Address` is a byte, whereas ETS6 segment number is an independent `i4`
(`analysis/ets/addresses.md:581`). Free/old-style group ranges and style conversion
are not implemented (`analysis/ets/addresses.md:590-594`).

**Cannot:** model a segment coupler, a line coupler, a backbone line or a
medium-type mixture; therefore cannot validate or document a multi-segment or
multi-area installation with couplers.

### P4 Device/hardware selection — **partial**

Local `.knxprod` import, catalogue library, search, product details, insert into a
selected room/line/segment with address suggestion and undo
(`analysis/release-notes-v4.md:89-95`); catalogue status gating (broken /
incomplete / DCA read-only) exists (`:85-88`). Hardware planning data is **not**
used: no `BusCurrent` is parsed anywhere (`git grep -i buscurrent -- src` → no
hit), and the model has no power-supply, repeater, choke or coupler fields, so the
ETS `PowerConsumptionCheck`, `RepeaterCheck`, `SegmentCouplerCheck`,
`RouterTopologyCheck`, `BackboneLineIpCheck` and `BrokerForBatteryPoweredDevicesCheck`
stay silent instead of guessing (`src/Knx.Core/ProjectCheck.cs:16-23`).

**Cannot:** plan bus power, count repeaters/chokes, size power supplies, or prove
that the selected hardware is electrically admissible. This is a first-class
hardware-planning gap, not a convenience gap.

### P5 Group addresses — **done (offline)**

Group addresses and ranges with add/update/delete/import-CSV (MCP), duplicate and
range checks, style enum, `AddressStrategy.TryNextGroup`
(`src/Knx.Core/AddressStrategy.cs:56-`), validated "new group address" form and
linkable DPT bit size (`analysis/release-notes-v4.md:67-68`). Free-style and
16-bit addresses are unsupported by design (`analysis/ets/addresses.md:590-594`).

**Cannot:** use free/old-style address structures, or the ETS CSV column layouts
per style (`analysis/ets/addresses.md:609-612`).

### P6 Links (associations) — **done (offline)**

Bulk multi-assignment with a frozen selection, preview (already linked / ready /
incompatible), duplicate handling, atomic rejection of incompatible batches, one
undo step (`analysis/release-notes-v4.md:69-72`); explicit sending address with a
warning instead of a silent replacement (`:71-72`); per-link connector facts
(`Send`, `Acknowledge`, `Priority`, `Slot`) with a single-sending invariant
(`docs/workbench-domain.md:33-38`). Validation distinguishes inactive object, bit
size, full DPT, long main type and secure capability
(`docs/workbench-domain.md:25-29`). DPT/flags on the object side are still thinner
than ETS6's 36-value error taxonomy and six flag operations
(`analysis/ets/addresses.md:595-604`).

**Cannot:** edit KO flags (communication/read/write/transmit/update/read-on-init)
or priority as first-class data, and cannot get ETS6-equivalent multi-reason link
diagnostics.

### P7 Parameter configuration — **partial**

Typed editors for number, float, text, enum, boolean; validation and draft/review
before apply; a deterministic `choose`/`when`/`assign` evaluator with explicit
`Unsupported`/`InvalidAssignments` lists (`analysis/release-notes-v4.md:100-105`,
`analysis/ets/catalog-parameters.md:515-528`). Dynamic manufacturer logic
(modules, repeat, allocators, text-param refs, com-object activation) is detected
and reported but **not** evaluated, so the displayed row set is not the
runtime-active parameter list (`analysis/release-notes-v4.md:100-103`). DCA/plugin
applications are read-only (`:85-88`). Changed values persist as BusChord
overrides in `urn:buschord:project:1`; whether ETS reads them as device parameters
is **unverified** (`:104-105`).

**Cannot:** configure a device whose parameter set depends on dynamic
manufacturer logic, and cannot be sure that a configured parameter survives an ETS
round-trip. For projects that contain DCA/plugin devices, parameters cannot be
configured at all.

### P8 Documentation / export — **done (offline)**

CSV (group addresses, devices, topology, object assignments), self-contained
printable HTML project report (title page, topology, GA table with DPT and links,
communication objects, link matrix, findings), label sheet, Turtle/JSON-LD
semantic export, compare CSV (`analysis/release-notes-v4.md:134-140`,
`mcp.md:94-100`). There is deliberately **no PDF** output; printing is done from
the browser (`mcp.md:98-100`). ETS6 has 12 report types with PDF/HTML/MHT/CSV/TXT/
RTF/DOCX/XLSX export and a print dialog (`analysis/ets/project.md:279-323`).

**Cannot:** produce a PDF directly, choose report types, or add a company logo /
page settings; report content is a single fixed template.

### P9 On-site installation support — **partial**

`LabelCreator` produces a printable A4 label sheet and a CSV variant from the same
model — group-address labels and device labels
(`src/Knx.Reports/LabelCreator.cs:27-45`, `:56-`). Device/topology CSV lists exist.
ETS6 uses Word mail-merge with a template; the BusChord HTML sheet is an
approximation (`FEATURE.md:134`).

**Cannot:** print a line-scan or device-status report (ETS6 has
`XtraLineScanReport`, `analysis/ets/commissioning.md:759-763`), and cannot produce
wiring/placement plans or an installation checklist.

### P10 Physical addressing — **missing (blocked by policy)**

`Management` can encode `IndividualAddressRead`, `IndividualAddressWrite`,
`DeviceDescriptorRead` and `Restart` (`src/Knx.Bus/Management.cs:17-59`), but
every real write path is disabled: `KnxBus.WritesEnabled => false`
(`src/Knx.Bus/KnxBus.cs:15`), `GroupValueWriteAsync` and `DownloadAsync` throw
`NotSupportedException` (`:70-79`, `:98-112`), and MCP `bus_write` throws
"bus_write is disabled: this build is read-only"
(`src/Knx.Mcp/McpServer.Tools.cs:1005-1008`). Programming-mode query/set, LED
flash, per-device check and address read/write from the UI do not exist
(`analysis/ets/commissioning.md:756-763`). The Workbench opens no bus connection at
all (`docs/manual.md:108-110`, `:485-489`).

**Cannot:** put a device into programming mode, assign or read an individual
address, identify a device by LED flash, or check a device against the project.
On site, the tool is unusable for the addressing step.

### P11 Download / commissioning — **missing (blocked by policy)**

`DownloadSession.RunAsync` is a single hard-coded path: read descriptor type 0,
write `IApplicationProgram.Image` from a fixed `StartAddress` in chunks via
`A_MemoryWrite`, read back and compare, then `Restart(erase:false)`, with a fixed
3 s response timeout (`src/Knx.Bus/DownloadSession.cs:28`, `:39-87`). It has no
programming-mode entry/test, no descriptor-driven memory map, no
address/domain/network or medium configuration load, no separation of parameters
and group tables, no partial/differential download, no access level/tool key/FDSK
or secure sync, no unload/reset/compare/device-info, no mask/manufacturer/resource
verification and no download log (`analysis/ets/commissioning.md:346-362`). It is
gated off (`src/Knx.Bus/KnxBus.cs:15,98-112`). The legacy UI entry
`MainWindowViewModel.DownloadAsync(imagePath)` writes an arbitrary file to
`0x0060` with no confirmation and no catalogue/DCA check, but has no caller
(`src/Knx.App/MainWindowViewModel.cs:839-846`, `:1176-1188`;
`analysis/security-final.md:153-170`).

The Workbench programming queue is a simulation: the default executor is
`Task.Delay(150)` (`src/Knx.App/Workbench/Sessions/ProgrammingSession.cs:99`) and a
job with `SessionOrigin.Real` is rejected because no real executor is injected
(`:121-122`). The per-device load state is honest but synthetic: five load flags,
a frozen-configuration signature as image hash, attempt/success timestamps, error
and origin (`src/Knx.Core/Workbench/DeviceProgrammingState.cs:12-26`, `:58-77`);
`WorkbenchDiagnostics` hard-codes `KeyManagementAvailable: false`,
`SecureDownloadAvailable: false` (`src/Knx.Core/Workbench/WorkbenchDiagnostics.cs:104-107`).

**Cannot:** commission a single device. No device image is built, no parameters or
group tables are transferred, no device state is read back.

### P12 Diagnostics / monitoring — **partial (read-only, live-unverified)**

Read-only scan, descriptor parsing, address check and statistics exist
(`src/Knx.Bus/Diagnostics.cs:141-`, `:308-`); MCP exposes `bus_scan`,
`bus_device_info`, `bus_statistics` and opens its own short-lived tunnel with own
address `1.1.249` (`mcp.md:113-124`). The Workbench monitor, scan and programming
tools are explicitly labelled simulations (`docs/manual.md:436-475`), and
`LineScanSession` only has `RunDemoAsync` (`analysis/knx-bus-read-test-plan.md:81-82`).
Recording supports CSV/JSONL/XML with replay (`FEATURE.md:119`); triggers,
retention/rotation, per-row security/decryption state, statistics report,
medium/RF info and record markers are missing
(`analysis/ets/commissioning.md:662-669`).

Live evidence is bounded: discovery and the tunnel handshake (CONNECT, one group
read on `0/0/1`, clean DISCONNECT) were verified read-only against an MDT
SCN-IP100.03 (`analysis/router-discovery.md:51-72`, `docs/manual.md:494-502`). The
cEMI codec defects B1–B4 that blocked real read testing at base `95f842b`
(`analysis/knx-bus-read-test-plan.md:58-70`) were fixed afterwards
(`analysis/release-notes-v4.md:123-128`; the group flag now uses `0x80`,
`src/Knx.Bus/Cemi.cs:80,115,216`). The later read-only run
(`analysis/knx-live-read-acceptance.md`) closed the descriptor and group-value
gap: three descriptor reads and one real GroupValueResponse succeeded. A
second-tunnel conflict test and a full line scan were deliberately not run; the
diagnostic frame layout still lacks a broader live matrix (`mcp.md:126-129`).

**Cannot:** yet prove a full working line or hand a customer a scan report; the
read-only descriptor path itself is proven (see the post-report live note).

### P13 Acceptance / test protocol — **missing**

There is no acceptance model: no per-device or per-function test record, no
sign-off, no defect list, no customer-facing test protocol. A repository-wide
search for commissioning/acceptance report concepts finds nothing
(`git grep -i "Inbetriebnahmeprotokoll|Abnahmeprotokoll|test protocol|as-built"` →
no hit in `src`, `docs`, `mcp.md`, `README.md`). What exists is a *structural*
project check (`P001`–`P073`, report-only, never repairing,
`src/Knx.Core/ProjectCheck.cs:10-25`) and, through MCP, its JSON output.

**Cannot:** document that the installation works. The project check explicitly is
not a commissioning or device diagnosis (`docs/manual.md:592-593`, `:692-693`).

### P14 Handover / as-built — **partial**

The HTML project report plus labels and CSVs are usable as handover
documentation, and the project compare window shows added/removed/changed objects
with CSV export (`docs/manual.md:617-622`). But there is no as-built concept: no
revision record, no "installed state" snapshot, no signature/version stamp, no
attachment/tag/history page (ETS6 project details: history, attachments, security,
image, tags, `analysis/ets/project.md:610-654`).

**Cannot:** prove which project revision the building was commissioned with.

### P15 Maintenance / versioning — **partial**

Local backups: atomic save, backup before overwrite, up to 10 backups per project
identity with SHA-256 validation and atomic restore
(`src/Knx.Core/ProjectBackup.cs:20-33`, `:44`, `:135`; `docs/manual.md:509-520`).
Backups are plaintext copies of the customer project in `%LOCALAPPDATA%`, kept
indefinitely, with the source path in the manifest and no "delete all"
(`analysis/security-final.md:251-269`). There is no project archive, no version
history, no lock/collaboration mode, no recovery points and no restore-point
rotation (`analysis/ets/project.md:430-435`, `:353-356`). Archive and master-data
signatures are preserved but **not recomputed**, so changed signed data may fail
ETS validation (`README.md:33-34`, `docs/manual.md:532-537`).

**Cannot:** keep a versioned project archive, hand a version to a colleague
without conflict handling, or guarantee that an edited project still passes ETS
signature validation.

---

## 3. ETS6 and V4 comparison: deliberate limits vs. production blockers

The V4 concept deliberately scoped the product to an **offline engineering and
documentation workbench** with honest simulation badges. Those decisions are
recorded in `analysis/workbench-v4-completion-plan.md:478-485` and
`analysis/release-notes-v4.md:228-261`, and the status matrix repeats them
(`analysis/workbench-v4-status-matrix.md:161-169`).

**Deliberate limits (not gaps; they do not block production if the product stays
an offline engineering tool):**

| Limit | Where recorded |
|---|---|
| No DCA/plugin hosting (read-only detection only) | `workbench-v4-completion-plan.md:480`, `release-notes-v4.md:240-243` |
| No secure commissioning (keyring/FDSK/tool key/secure download) | `:481`, `:244-246` |
| No real downloads / address writes; writer gated | `:482`, `:233-239` |
| No USB/RDAC, cloud/IoT, ETS-local-store versioning, full multi-project | `:483`, `:247-250` |
| No DCA/script dynamics, modules, allocators, load procedures, manufacturer page layouts | `:484` |
| No online catalogue/market/signature check | `:485` |
| No PDF report, browser print instead | `mcp.md:98-100` |
| German + English only, no RTL/third language | `release-notes-v4.md:251-253` |
| Simulated monitor/scan/programming with explicit badges | `docs/manual.md:29-31`, `:436-475` |

**Gaps that block production use** (present in ETS6, absent or unusable here, and
not justified by the "offline workbench" scope once the goal is a complete
project):

1. **No hardware planning** (bus power, repeaters, chokes, couplers, IP backbone).
   ETS6 has dedicated checks; the model cannot express them
   (`src/Knx.Core/ProjectCheck.cs:16-23`). Blocking for any real line layout.
2. **No physical addressing / programming-mode workflow.** Blocking on site.
3. **No download pipeline** (image build, load procedure, partial download, device
   state read-back). Blocking for commissioning.
4. **No acceptance protocol.** Blocking for commercial handover.
5. **No as-built/versioning** beyond 10 local backups. Blocking for warranty and
   maintenance work.
6. **DCA/plugin devices are read-only and unprogrammable.** For projects that
   contain DCA devices (common in HVAC/lighting), the project cannot be completed
   in BusChord at all — it must be finished in ETS6.
7. **Parameter persistence is a BusChord extension.** Whether ETS interprets the
   overrides as device parameters is unverified
   (`analysis/release-notes-v4.md:104-105`); combined with preserved-but-not-
   recomputed signatures, the artifact's fitness for ETS is unproven.

The REA reports reach the same conclusion from the ETS side: the download writer
"is not an ETS-compatible download and must not be presented as one"
(`analysis/ets/commissioning.md:362`), the top gap is "download writes raw memory
without programming mode, descriptor map or authorization" (severity *critical*,
`analysis/ets/commissioning.md:1074`), and the recommended order is "make download
safe before making it complete" (`:1084-1101`).

---

## 4. Prerequisites

| Prerequisite | Status | Evidence / consequence |
|---|---|---|
| **Real interface / gateway** | partial | KNXnet/IP discovery, tunnel and routing exist (`src/Knx.Bus/Discovery.cs`, `KnxTunnel.cs`, `KnxRouting.cs`). Discovery + tunnel handshake live-verified read-only (`analysis/router-discovery.md:51-72`). No USB HID/EMI, no device-management (RDAC) connector, no connector-parameter model or connection-string persistence, no auto-reconnect/connect-fail-fast, no local programming-mode query, no secure service-family negotiation (`analysis/ets/commissioning.md:199-207`). MCP diagnostics open their own tunnel with a different own address; there is no shared arbitration with the app (`analysis/knx-bus-read-test-plan.md:253-255`). |
| **Secure commissioning** | missing | Keyring/JSON/XML, tool keys, sequence numbers, secure telegram/wrapper and AES helpers exist as a foundation (`src/Knx.Bus/Security/*`), but nothing is wired into the tunnel and no key management exists (`README.md:24`, `docs/manual.md:675-677`, `src/Knx.Core/Workbench/WorkbenchDiagnostics.cs:59-61`). Missing keyring schema validation, PBKDF2 password hashing, signature creation/validation, certificate/FDSK store, secure download, sequence-counter persistence, secure tunnelling/routing (`analysis/ets/commissioning.md:967-980`). Secure installations cannot be commissioned. |
| **DCA / plug-in handling** | partial (deliberate) | Detection and read-only gating exist; no in-process host, no plug-in loading anywhere, download blocked (`analysis/security-final.md:313-318`, `analysis/release-notes-v4.md:240-243`). Two open items: the read-only reason is derived only from the catalogue, so a project-embedded application program without a matching catalogue can be edited (SEC-4, `analysis/security-final.md:127-151`), and the unused `CatalogUse.Download` gate plus the legacy raw download API are a loaded gun for the next wiring (SEC-5, `:153-170`). Feasibility analysis: hosting is rejected (32-bit .NET Framework + COM in-process, `analysis/ets/catalog-parameters.md:762-779`). |
| **Multi-project** | missing | One project at a time; no multi-project tabs or windows; recent list capped at 10 (`analysis/release-notes-v4.md:247-248`, `analysis/ets/project.md:685-687`). Blocks parallel work on several buildings and blocks the archive/dashboard story. |
| **Project archive / signature** | partial | No archive, no versions, no locks, no recovery points (`analysis/ets/project.md:430-435`). Signatures in project and master data are preserved but not recomputed, so ETS signature validation can fail after edits (`README.md:33-34`, `docs/manual.md:532-537`). Encrypted ETS6 archives are read-only; saving is refused (`docs/manual.md:545-547`). No ETS round-trip proof exists (`analysis/release-notes-v4.md:258-259`). |
| **Licensing / liability** | partial | Apache-2.0, no warranty, plus an explicit "no warranty for live bus actions" section and a rule that test suites must not perform live writes (`SECURITY.md:71-80`). Trademark note is present (`README.md:141-147`). There is **no in-product safety notice** before a bus write, and no licence-gating concept at all (ETS6 gates device counts and export by licence, `analysis/ets/project.md:99-100`, `:265-268`). Pre-release status is declared (`SECURITY.md:7-17`). |
| **Data protection** | partial | No telemetry, no cloud egress; the only network traffic is KNXnet/IP to the gateway. Catalogue and project parsers have size/depth/DTD limits, path-containment and atomic writes (`analysis/security-final.md:297-336`). Open: plaintext customer-project backups in `%LOCALAPPDATA%` kept indefinitely with the source path in the manifest and no bulk delete (SEC-9, `:251-269`); non-atomic catalogue library import with ignored `.signature` (SEC-8, `:229-249`); local file read via a poisoned cache (SEC-6/7, hardening). |
| **Offline / online workflow** | partial | The whole engineering path is offline by design and that is a strength: editing never sends telegrams (`docs/manual.md:26-28`). But there is no online catalogue, no update channel, no market/language model and no product update by ID (`analysis/ets/catalog-parameters.md:237-244`, `analysis/workbench-v4-completion-plan.md:485`); the catalogue library must be populated by hand. The Workbench has no connect/disconnect/interface state — "OFFLINE" and "Lokal · keine Busverbindung" are fixed hints, not toggles (`docs/manual.md:105-110`). |

---

## 5. Verdict

**not-ready** for executing complete KNX projects end-to-end.

The commissioning half of a KNX project (P10 physical addressing, P11 download,
P13 acceptance) is not merely incomplete — it is deliberately and correctly
disabled, and the prerequisite hardware-planning data (P4) is not modelled. No
installation can be addressed, programmed, proven or signed off with BusChord
today. Hardware planning and acceptance documentation are hard requirements of the
stated scope, not optional parity.

**pilot-ready** for the offline engineering and documentation subset (P2, P3
without couplers, P5, P6, P7 for static applications, P8), with these caveats:

- the ETS round-trip and the signature validity of an edited project are unproven;
- parameter overrides live in a BusChord namespace whose ETS interpretation is
  unverified;
- devices with dynamic manufacturer logic or DCA applications cannot be
  configured;
- couplers, segments with medium/domain data, and bus power cannot be modelled.

That subset is a real, usable engineering tool — but it produces a *plan*, not a
*commissioned building*.

---

## 6. Phase-by-phase gap list

| # | Phase | Status | Blocking gap |
|---|---|---|---|
| P1 | Acquisition / requirements | missing | No requirements, scope, trade or function model at all |
| P2 | Project creation | done | No wizard/templates; no password at creation |
| P3 | Topology | partial | No couplers, segment medium/domain, backbone/main line, free ranges |
| P4 | Device/hardware selection | partial | No bus power / repeater / choke / coupler planning data used |
| P5 | Group addresses | done | Free/old-style ranges and 16-bit addresses unsupported |
| P6 | Links | done | KO flags/priority not first-class; thinner multi-reason diagnostics |
| P7 | Parameters | partial | No dynamic manufacturer logic; DCA read-only; ETS interpretation unverified |
| P8 | Documentation / export | done | No PDF, no report selection/settings |
| P9 | Installation support | partial | Labels only; no scan/device-status report, no placement plans |
| P10 | Physical addressing | missing | All write paths disabled; no programming-mode workflow |
| P11 | Download / commissioning | missing | No image build, no load procedure, no device read-back |
| P12 | Diagnostics / monitoring | partial | Read-only; no successful group response or descriptor read verified live |
| P13 | Acceptance / test protocol | missing | No per-device/per-function record, no sign-off |
| P14 | Handover / as-built | partial | No revision record or installed-state snapshot |
| P15 | Maintenance / versioning | partial | Backups only; no archive/versions/locks; signatures not recomputed |

---

## 7. Prioritized task backlog

Effort: **S** ≤ 2 days, **M** 3–10 days, **L** 2–4 weeks, **XL** > 1 month.
Every task must leave a runnable check behind and must not claim an unverified
live result.

### P0 — blocking any real project execution

**B1. Complete the read-only live acceptance matrix on a segregated test line**
*(S + hardware + maintenance window)*
Run R6 (group-value read to an approved GA), R7 (descriptor read to an approved
device) and, if approved, R9 (line scan) from
`analysis/knx-bus-read-test-plan.md:183-196` after the codec fixes; record raw
frames outside the repo and publish a sanitized result report.
*Acceptance:* a `GroupValueResponse` from a real device with a DPT-plausible
payload is decoded and matched to the right GA and responder; one descriptor read
returns a plausible mask version; the line scan matches the project's device list
or explains every difference; the report names frames, timings and the abort
criteria it honoured.

**B2. Safe download gate before any completion work**
*(M)*
Hard-fail the download path unless: descriptor read succeeded, the mask version is
known (`src/Knx.Bus/Diagnostics.cs:86-108`), the device is in programming mode,
the target line and address were confirmed by the user, and a dry run printed the
computed image and target addresses. Add a per-attempt log (interface identity,
target, image hash, options, duration, failure cause) and remove or mark test-only
the legacy `MainWindowViewModel.DownloadAsync`/`FileProgram`
(`analysis/security-final.md:153-170`).
*Acceptance:* unit tests prove refusal for unknown mask, missing programming mode
and missing confirmation; no frame leaves the process in those cases; the attempt
log is written for every real run.

**B3. Physical addressing / device-service workflow**
*(M)*
Read-only programming-mode discovery first (query + descriptor, never setting
flags), then a separate, explicitly confirmed device service for individual-address
write, programming mode and LED flash, each with a per-device undo note and a
warning that programming mode affects the whole line
(`analysis/ets/commissioning.md:770-775`).
*Acceptance:* a device can be identified, its address read and rewritten on an
isolated line; a test proves that no address write happens without an explicit
confirmation; the programming-mode scan is covered by the fake-session tests.

**B4. Real download pipeline: image build and load procedure**
*(XL)*
Descriptor/mask-driven memory map from master data, separation of
address/parameters/group tables/application, segment handling, verify/restart
sequencing, device read-back of the five load flags, and partial download only with
a valid stamp and an explicit fallback confirmation
(`analysis/ets/commissioning.md:370-379`, `:1086-1097`).
*Acceptance:* one real device of a supported mask version is fully commissioned on
an isolated line and its state is read back and matches; an unknown mask version is
refused; a partial-load mismatch falls back to a full load only after confirmation.

**B5. Wire the programming queue to the real executor**
*(M)*
Inject a real executor into `ProgrammingSession`, add `NeedsUser` (programming
button, key, certificate, address occupied), a per-operation deadline, a bulk start
API and a persisted attempt entry
(`analysis/ets/commissioning.md:546-572`).
*Acceptance:* a real job runs through the existing queue with visible progress,
retry re-reads device state before replaying, and a job cannot start twice on the
same device.

**B6. Hardware planning: bus power, repeaters, couplers**
*(L)*
Add the missing model fields (`BusCurrent` from hardware, segment `MediumTypeId`/
`DomainAddress`/`CouplerDevice`, area `IsBackbone`/`MainLine`) and implement the
ETS check set as pure report-only functions with their master-data source named
(`analysis/ets/commissioning.md:838-841`,
`analysis/ets/addresses.md:631-634`).
*Acceptance:* per line, consumed bus current vs. available supply is reported;
repeater/choke/coupler violations are reported with the constant's source; the
check never repairs and never invents a limit.

**B7. Acceptance / test protocol**
*(M)*
A per-device and per-function commissioning record (address, application program,
parameter hash, group tables, load flags, result, timestamp, technician) exported
as a printable document, plus a defect list.
*Acceptance:* after a (simulated or real) commissioning run, the protocol lists
every device with its load state and result; devices that need programming appear
as open items; the export is deterministic and printable.

**B8. Multi-project**
*(M–L)*
Several projects open at once (tabs or windows) with independent undo history and
sessions, and a project switch that does not lose layout state.
*Acceptance:* two projects can be edited in one session; switching keeps both undo
stacks and both session states; a test proves that a project switch does not leak
the previous project's sessions (`SessionProject` already guards identity,
`src/Knx.App/Workbench/WorkbenchSessions.cs:145-158`).

### P1 — needed for credible commercial use

**B9. Bus session in the Workbench** *(M)* — interface selector, discovery list,
connect/disconnect, state indicator and a read button for a selected GA; until
then the demo badges and the manual must stay as they are
(`analysis/knx-bus-read-test-plan.md:245-252`, `docs/manual.md:108-110`).
*Acceptance:* the status bar reflects the real connection state; the manual's §7.5
matches the shipped UI; a read-only selftest covers connect → read → disconnect.

**B10. DCA gate from the project archive** *(S)* — derive the read-only reason from
the project's embedded application program when no catalogue matches (SEC-4,
`analysis/security-final.md:127-151`). *Acceptance:* opening a foreign project with
an embedded DCA application and an empty library still marks the parameters
read-only, with a regression test.

**B11. Handover document completeness** *(M)* — device detail pages, per-line
device lists, link matrix completeness and a PDF-capable export path (print
stylesheet is already there, `mcp.md:98-100`). *Acceptance:* the handover document
contains every device with address, product, application and links; the export
carries a generation timestamp and a project identity.

**B12. Versioning / archive** *(L–XL)* — project archive with versions, hashes,
pin/restore and signature recomputation or an explicit "unsigned" mode
(`analysis/ets/project.md:430-435`). *Acceptance:* two versions of a project can be
stored, compared and restored; restoring replaces the working copy atomically; the
signature status of a saved project is stated explicitly rather than silently.

**B13. Secure commissioning, in the mandatory order** *(XL)* — keyring read/validate
first, then certificate store, then sequence counters, then secure download, then
secure tunnelling/routing. Never invert
(`analysis/ets/commissioning.md:988-996`, `:1099-1100`).
*Acceptance:* a keyring file can be validated and listed without exposing material;
certificate import is all-or-nothing with counts and undo; a secure device is
refused before any write when its certificate is missing.

### P2 — hardening, documented limits, polish

**B14. Data protection for backups** *(S)* — document location and retention, add
"delete all backups", do not claim backups are a privacy feature (SEC-9).
**B15. In-product safety notice before the first bus write** *(S)* — mirror
`SECURITY.md:71-80` in the UI, not only in the manual.
**B16. Catalogue import atomicity and signature surfacing** *(S)* — temp file +
move, keep the previous entry, surface "unsigned catalogue" (SEC-8).
**B17. `CatalogUse.Download` gate wired or removed** *(S)* — remove the dead gate or
make every download entry point consult it (SEC-5).
**B18. Real 150/200 % DPI and screen-reader acceptance** *(S + hardware)* — the
only open V4 acceptance item with a platform dependency
(`analysis/workbench-v4-status-matrix.md:196`).
**B19. Online catalogue / product update** *(L, optional)* — only after a
language/market model and signature verification exist
(`analysis/ets/catalog-parameters.md:246-251`). Explicitly out of scope for a
first production use.

---

## 8. What remains unproven after this assessment

- No bus was contacted. Everything about bus behaviour is second-hand from the
  cited reports; the last live run in this repository was read-only
  (`analysis/router-discovery.md:51-72`).
- No build, no test suite and no application was run for this report; "present and
  covered by a test" is taken from the cited reports, not re-executed.
- ETS was not inspected; all ETS6 statements come from the REA reports under
  `analysis/ets/`.
- Effort estimates are rough order-of-magnitude, based on the size of the gap
  descriptions in the cited reports, not on a measured schedule.
