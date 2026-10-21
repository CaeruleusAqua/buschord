# KNX bus read-test plan (real hardware)

Status: investigation + plan, 2026-10-21. Branch `agents/bus/read-test-plan`,
base `95f842b` (verified). Scope: plan real, read-only bus tests and run only
state-neutral smoke tests. No product, test, CI or existing report was changed.

Authorization: the user explicitly allowed **reading** on their KNX bus. That
does not include downloads, programming, individual-address writes, group-value
writes, resets, secure commissioning or any FDSK/key operation. Every test below
is classified against that boundary.

## 1. Result in one paragraph

A real KNXnet/IP gateway is present and answers read-only discovery. A tunnel
opens and closes cleanly, survives a 60 s heartbeat, and forwards live group
traffic. However, the repository's cEMI codec mis-decodes that traffic: all 49
forwarded frames in a 75 s capture were group frames, but 35 of them were
classified as individual frames, every `GroupValueWrite` was decoded as
`GroupValueRead` with a shifted payload, and `L_Data.con` is fed into the
telegram stream. As a result the MCP `bus_read` tool returns the tunnel's own
confirmation echo as the "value" after 43 ms instead of waiting for a
`GroupValueResponse`. Real read testing is therefore **not useful yet**: the
transport works, the telegram layer does not. Descriptor reads, scans and all
writes must stay blocked until the codec and the read matching are fixed.

## 2. Environment and what was executed

- Gateway: one MDT SCN-IP100.03 IP Router mit Sec on the Wi-Fi segment
  (addresses and MAC redacted here; known locally). Individual address `1.0.0`,
  KNXnet/IP routing multicast `224.0.23.12:3671`, supported families
  2, 3, 4, 5, 7.
- Local settings (`%APPDATA%\BusChord\settings.json`): no `BusInterface`
  configured, `TunnelAddress = 1.1.250`. No password or token exists in that
  file; none was read or printed.
- One local adapter (Ethernet) could not join the routing multicast group
  (`SocketException`); the KNX segment is reachable over the Wi-Fi adapter. The
  VPN adapter joined the group but carries no KNX traffic.

Executed (all state-neutral):

| # | Action | Result | Timing |
|---|--------|--------|--------|
| E1 | Passive routing listen, join `224.0.23.12:3671`, no sends | 6 distinct routing frames from the gateway | 15 s |
| E2 | Unicast `DESCRIPTION_REQUEST` to the gateway | `DESCRIPTION_RESPONSE`, MDT SCN-IP100.03, IA 1.0.0 | < 1 s |
| E3 | Multicast `SEARCH_REQUEST` on the KNX segment | `SEARCH_RESPONSE` with HPAI of the gateway, families 2/3/4/5/7 | < 4 s |
| E4 | Repository probe: `dotnet run --project src/Knx.Mcp -- --bus-probe=<gw>` | 1 datagram, DIB parsed as SCN-IP100.03 / 1.0.0 | 4 s |
| E5 | Repository live probe: `dotnet run --project tests/Knx.Bus.Tests -- --live <gw>` | CONNECT ch 16, assigned `1.0.1`, one read of `0/0/1`, `L_Data.con`, clean DISCONNECT | 22 ms connect |
| E6 | Tunnel listen, no cEMI sent | 5 forwarded telegrams, clean disconnect | 20 s |
| E7 | Tunnel listen with 60 s heartbeat | 49 forwarded telegrams, `CONNECTIONSTATE_RESPONSE 0x00`, still connected, clean disconnect | 75 s |
| E8 | Reproduce MCP `bus_read` matching on `0/0/1` | returned `L_Data.con` echo as value in 43 ms | 43 ms |
| E9 | Offline encode check of the repository's own frames | write and descriptor frames do not match the wire format observed in E6/E7 | — |

Two independent decoders were used: the repository's `Cemi.ParseLData` and a
small external decoder that treats `ctrl2` bit 7 as the group flag and reads the
APCI from APDU octets 7-8. The external decoder agrees with every captured frame
and with the gateway's own `L_Data.con`; the repository decoder does not.

## 3. What the bus stack really supports

| Area | File | Real interface? | Notes |
|------|------|-----------------|-------|
| Discovery | `Discovery.cs` | yes | SEARCH + DESCRIPTION, per-interface sockets, DIB parsing. Verified live (E2-E4). |
| Tunnel | `KnxTunnel.cs` | yes | CONNECT/ACK/sequence/heartbeat/DISCONNECT verified live (E5-E7). |
| Routing | `KnxRouting.cs` | **no** | `OpenAsync` only sets `Connected` and starts a receive loop on a socket bound to `0.0.0.0:0`; it never joins `224.0.23.12` and never binds 3671. Real routing frames were only received by an external socket (E1). |
| cEMI codec | `Cemi.cs` | **no** | APCI/group-flag/payload decoding wrong for real frames; see section 4. |
| Management frames | `Management.cs` | **no** | Descriptor read, memory read/write, IA write and restart exist, but their APCI encoding does not match the wire. Descriptor type cannot be expressed at all. |
| Diagnostics | `Diagnostics.cs` | transport yes, frames no | Scan/descriptor logic is read-only by design, but every probe depends on the broken descriptor encoding. Only MCP exposes it. |
| Recorder | `TelegramRecorder.cs` | transport yes | Records whatever the tunnel emits, including `L_Data.con`, and writes APCI/data as decoded (currently wrong). |
| Download | `DownloadSession.cs` | **no** | Raw memory writes to a fixed address; gated in `KnxBus.DownloadAsync` by `SafeDownloadImplemented = false`. Do not enable. |
| Security | `Security/*` | no live use | Keyring/FDSK/secure wrapper are offline code. No secure commissioning attempted. |

### UI gating (Workbench)

The product window is `WorkbenchWindow`. It never creates a `KnxBus` and never
calls discovery, connect or read:

- Monitor: `sessions.Monitor.Configure(..., SessionOrigin.Demo, "demo-ip")`
  hardcoded; badge "DEMO-Simulation - keine reale Busaufzeichnung". Real group
  mode is possible through `ConfigureReal`, but nothing calls it. Real bus mode
  is explicitly rejected by `MonitorSession.ConfigureCore`.
- Scan: `LineScanSession` only has `RunDemoAsync`; it simulates one row per
  project device with a 20 ms delay.
- Programming: the Workbench injects only a demo executor; real jobs are
  rejected at enqueue because `realExecutor` is null.
- No discovery list, no connect/disconnect, no group read/write, no interface
  selector in the Workbench. `MainWindowViewModel` still contains working
  discovery/connect/read/write/download commands, but the Workbench UI does not
  bind them.
- Diagnostics only exists as MCP tools (`bus_scan`, `bus_device_info`) and as
  the project-level `BuildDiagnosticSummary`, which does not touch the bus.
- `docs/manual.md` sections 7.1-7.4 still describe a Suchen/Verbinden/Monitor/
  Recorder UI that the shipped Workbench window does not provide.

## 4. Defects that block real read testing

B1-B3 were reproduced against the real gateway. B4 was verified offline on the
generated frames and deliberately not sent. B5 follows from the code and from
the fact that only an external socket received the routing frames (E1). Each is
a product bug, not a test-environment problem.

### B1 - group/individual flag uses the wrong bits

`Cemi.ParseLData` classifies a group frame with
`(ctrl2 & 0xF0) == 0xE0`. `ctrl2` bit 7 is the group flag; bits 6..4 are the hop
count, which routers decrement. Real frames therefore arrive with `ctrl2` `E0`
(hop 6), `D0` (hop 5) and `C0` (hop 4). In the 75 s capture: 49/49 frames were
group frames, the repository classified 14 as group and 35 as individual.
Consequences: monitor rows show an individual address instead of a group
address, filters and metadata do not match, and `IsGroupAddress` is false for
most real traffic.

Fix: `(ctrl2 & 0x80) != 0`.

### B2 - APCI and payload are read from the wrong octets

`Cemi.BuildApdu`/`ParseLData` put the 4-bit APCI service in the high nibble of
APDU octet 7 and start payload at octet 8. On the wire the service code is
formed from octet 7 bits 1..0 and octet 8 bits 7..6
(`APCI = ((o7 & 0x03) << 8) | (o8 & 0xC0)`); for payloads of up to 6 bits the
value occupies octet 8 bits 5..0, otherwise the payload starts at octet 9.
Every real group frame in
the capture had APDU octet 7 = `00` and octet 8 = `80` (write, no inline data)
or `81` (write, 1-bit value 1). The repository decoded all of them as
`GroupValueRead`, with data `80 ..` or `81`.

The outbound read frame happens to be correct because every APCI bit is zero:
`11 00 BC E0 <src> <ga> 01 00`. That is why E5 worked. The outbound write is
not correct: the unit test expects `... 02 20 01` where the bus uses
`... 01 00 81` (1-bit) or `... 02 00 80 A8` (1-byte). Do not write.

Fix: encode/decode per the wire layout above, keep 1/4/6-bit values in the APCI
octet, and add the captured frames as sanitized golden fixtures. The existing
loopback/unit fixtures are self-consistent and hide this bug.

### B3 - `L_Data.con` is treated as a bus telegram

`KnxTunnel.Dispatch` parses every `L_Data` message code, including `L_Data.con`
(`0x2E`), and raises `TelegramReceived`. The confirmation echo of our own read
appeared as a telegram from `1.0.1` to `0/0/1` with APCI `GroupValueRead` and
payload `DF`/`D6`. `MCP.bus_read` accepts the first telegram whose destination
matches the requested GA, without an APCI or source check, so it returns that
echo after 43 ms (E8). This would report a bogus value for every read; the
current unit/live tests only assert "no `GroupValueResponse`", which is why it
went unnoticed.

Fix: ignore `L_Data.con` for the telegram feed (or expose it as a separate
confirmation event), and make `bus_read` wait for `GroupValueResponse`,
matching GA and an allowed responder, with a caller-visible timeout.

### B4 - descriptor reads cannot be expressed

`Management.DeviceDescriptorRead` builds APCI `0x300` as octet 7 = `C0` and
appends the descriptor type as a data byte. On the wire that is not
`DeviceDescriptorRead`; descriptor type belongs in the APCI bits. Sending it
would put a malformed management frame on the bus. `Diagnostics.ScanAsync`,
`bus_scan` and `bus_device_info` all depend on it, so none of them may be used
against real hardware yet. No such frame was sent in this investigation.

Fix: encode descriptor type in the APCI low bits, add descriptor types 1-3, and
verify with one targeted device read before re-enabling scans.

### B5 - routing is a stub (secondary)

`KnxRouting.OpenAsync` reports `Connected` without joining the multicast group;
MCP `bus_connect(mode="routing")` therefore looks successful but receives
nothing. Passive routing monitoring was only possible with an external socket
(E1). Fix: join `224.0.23.12` on the selected interface, bind 3671, filter
`ROUTING_INDICATION`, and surface the selected interface.

## 5. Read-only test matrix

Safety classes:

- **P** passive: no frame sent by BusChord.
- **R-IP** read-only IP management: SEARCH/DESCRIPTION/CONNECT/DISCONNECT/
  CONNECTIONSTATE; changes no device or group state.
- **R-BUS** read-only bus: a management or group read that elicits responses but
  changes no state.
- **BLOCKED** must not run until the named defect is fixed.
- **APPROVAL** additionally needs an explicit user decision (GA, device,
  maintenance window).

| ID | Test | Class | Exact command | Expected evidence | Preconditions | Abort criteria | Status |
|----|------|-------|---------------|-------------------|---------------|----------------|--------|
| R1 | Passive routing listen | P | external socket joined to `224.0.23.12:3671` (see appendix) | N routing frames, hop counts, sources | KNX segment reachable; no capture tool conflict | no frames for 60 s is a result, not a failure; stop on > 200 frames/s | done (6 frames/15 s) |
| R2 | Discovery, unicast + multicast | R-IP | `dotnet run --project src/Knx.Mcp -- --bus-probe=<gw>`; `bus_discover(host="<gw>")` | DIB: name, IA, MAC, families | gateway IP known or multicast allowed | > 3 retries or a non-KNX datagram storm | done (E2-E4) |
| R3 | Tunnel connect/disconnect | R-IP | `dotnet run --project tests/Knx.Bus.Tests -- --live <gw>` | CONNECT ch/IA, ACK 0, DISCONNECT 0 | no active tunnel from ETS; gateway has a free channel | CONNECT status != 0; no DISCONNECT_RESPONSE | done (E5, ch 16, IA 1.0.1) |
| R4 | Heartbeat > 60 s | R-IP | tunnel listen for 75 s | `CONNECTIONSTATE_RESPONSE 0x00`, state Connected | R3 | state drops, repeated heartbeat failures | done (E7) |
| R5 | Group monitor via tunnel | P | tunnel listen only, no cEMI | forwarded frames, decoded GA/APCI/DPT | R3; codec fix (B1/B2/B3) | any frame from our own assigned IA (would mean we sent) | transport done, decoding wrong |
| R6 | Group-value read, approved GA | R-BUS + APPROVAL | `bus_connect(mode="tunnel")`, then `bus_read(group_address="<GA>")`; or a 20-line harness using `KnxTunnel.RequestAsync` | `GroupValueResponse` from the device, plausible DPT value, 1-3 s | user names the GA and device; B3 fixed; ETS closed | no response in 3 s (report, do not retry more than 3x); unexpected write frame | not run (no GA approved) |
| R7 | Device descriptor read | R-BUS + APPROVAL | `bus_device_info(address="<IA>")` | `DeviceDescriptorResponse`, mask version | B4 fixed; user approves the device | malformed/negative response; timeout | blocked (B4) |
| R8 | Individual-address read | R-BUS | `Management.IndividualAddressRead()` (broadcast to `0.0.0`) | `IndividualAddressResponse` from every device | explicit approval; maintenance window | any responder outside the expected line | blocked by scope (broadcast management frame) |
| R9 | Full/line scan | R-BUS | `bus_scan(target="<area.line>")` (IA read + descriptor probes) | per-device descriptors | B4 fixed; explicit approval; maintenance window | > 50 devices or bus errors | blocked (B4 + broadcast management) |
| R10 | ACK retry / timeout | R-IP | unit loopback (already covered), plus real read with an invalid GA | one repeat with same sequence, then failure | none | repeated retries > 3 | loopback only; real path untestable without packet loss |
| R11 | Interface conflict | R-IP + APPROVAL | open a tunnel, then `bus_connect` again / start an ETS tunnel | CONNECT error status (e.g. 0x24) surfaced verbatim | user confirms ETS may be interrupted | any sign of an ETS session being dropped | not run (avoid touching a live ETS session) |
| R12 | Routing mode | R-IP | `bus_connect(mode="routing")` + listen | routing frames | B5 fixed | reports Connected but 0 frames in 60 s | blocked (B5) |

### Required user approvals

1. The exact group address(es) and device(s) for R6/R7, with the expected DPT.
2. Confirmation that no ETS session is active and that a tunnel may be opened.
3. A maintenance window for R9 (line scan) and for any descriptor read that
   could disturb a bus coupler.
4. Explicit approval before the first post-fix `GroupValueRead`, because it
   makes devices transmit (small burst, but visible in ETS logs).
5. Explicit approval for the second-tunnel test R11 if ETS may be in use.

Writes, downloads, restarts, IA writes, memory writes, secure commissioning and
key/FDSK operations are out of scope for every test in this document and need a
separate authorization.

### Manual protocol (after the blockers are fixed)

1. Prepare: note gateway IP, interface, tunnel address; close ETS; capture the
   current settings file hash (no secrets are in it).
2. R1 for 5 min: passive multicast listen. Save the raw frame log outside the
   repo. Stop if frames from our own tunnel IA appear.
3. R2: run the repository probe, compare the DIB with the gateway label.
4. R3: connect, confirm channel and assigned IA, then disconnect. Confirm
   `DISCONNECT_RESPONSE 0x00`.
5. R5 for 5 min: listen only. For every frame, compare the decoded GA/APCI/DPT
   with the raw bytes in the log. Any mismatch is a codec bug; stop.
6. R6: send exactly one read for the approved GA. Accept only a
   `GroupValueResponse` from the approved device with a DPT-plausible payload.
   At most 3 attempts, 2 s apart. Record timings.
7. R7: one descriptor read for the approved device, descriptor type 0 only.
8. Disconnect, verify the channel is released, verify no further frames from our
   IA, and compare the bus state with the pre-test snapshot.
9. Rollback: reads have nothing to roll back. If an unintended write is ever
   observed, stop, disconnect, report the frame and time to the user, and do not
   retry; restore from the ETS project/backup only on the user's instruction.

## 6. Gaps to implement before real read testing is useful

P0 (blocks every read):

1. Fix `Cemi` APCI/group-flag/payload encoding and decoding (B1, B2), including
   1/4/6-bit values and the extended-frame length field.
2. Drop or separate `L_Data.con`; make `bus_read` wait for a
   `GroupValueResponse` for the requested GA from a real device, with a visible
   timeout and the responder IA (B3).
3. Fix descriptor-read encoding and add descriptor types 0-3 (B4); keep scans
   disabled until one targeted descriptor read succeeds.

P1 (makes the UI usable):

4. Add a real bus session to the Workbench: interface selector, discovery list,
   connect/disconnect, state indicator, and a read button for a selected GA.
   Until then the demo badge must stay and the manual must not promise the old
   UI.
5. Implement routing properly or remove the routing option from MCP until it
   works (B5).
6. One arbitration point for tunnels: `InterfaceCoordinator` is app-local;
   MCP diagnostics open their own tunnel with a different own address. Decide
   whether a second tunnel is allowed and surface conflicts.

P2 (quality and evidence):

7. A read-only mode in `KnxBus`/MCP that rejects write APIs by policy, so a
   read test cannot accidentally call `bus_write`.
8. DPT width-aware decoding and display (1-bit, 4-bit, 8/16/32-bit, 14-bit
   float); the current DPT table assumes whole bytes.
9. Sanitized golden frames from this bus (hop 4/5/6, 1-bit and multi-byte
   writes, `L_Data.con`) as regression fixtures; they would have caught B1-B3.
10. Update `docs/manual.md` 7.1-7.5 and `analysis/router-discovery.md` after
    the fixes: heartbeat is now verified; the live UI path is not what the
    manual describes.

## 7. What was not run, and why

- No group-value write, no `bus_write`, no download, no restart, no IA write,
  no memory write, no secure commissioning, no key/FDSK access: explicitly
  outside the authorization.
- No individual-address read or scan: broadcasts a management frame and makes
  every device answer; forbidden by the task and blocked by B4 anyway.
- No device descriptor read: B4 would put a malformed frame on the bus.
- No group-value read to a specific installation GA: no GA was approved by the
  user. The only read sent was the repository's documented `0/0/1` probe, which
  cannot change a value and produced an `L_Data.con` but no response.
- No second tunnel / conflict test: would consume another channel and could
  disturb a live ETS session.
- No heartbeat beyond 75 s, no reconnect storm, no packet-loss injection: the
  ACK retry path is covered by the loopback test only.

## 8. Evidence appendix

Raw frames from E6/E7 (gateway forwarded, unchanged except the last data byte
varies per event):

```
29 00 BC C0 13 03 33 03 02 00 80 A8   group, hop 4, 1.3.3 -> 6/3/3,  GroupValueWrite 1 byte
29 00 BC D0 10 06 30 15 01 00 81      group, hop 5, 1.0.6 -> 6/0/21, GroupValueWrite 1 bit = 1
29 00 BC C0 13 03 33 0B 03 00 80 1C C2 group, hop 4, 1.3.3 -> 6/3/11, GroupValueWrite 2 bytes
29 00 BC E0 10 06 30 15 01 00 81      group, hop 6 (repo classifies this one correctly)
```

Repository decode of the same frames:

```
29 00 BC C0 ...  -> group=False, dst 3.3.3,  apci=GroupValueRead, data=80A8
29 00 BC D0 ...  -> group=False, dst 3.0.21, apci=GroupValueRead, data=81
29 00 BC C0 ...  -> group=False, dst 3.3.11, apci=GroupValueRead, data=801CC2
```

Repository read echo (`L_Data.con`) and what `bus_read` made of it:

```
wire: 2E 00 BC E0 10 01 00 01 01 00 D6
MCP-equivalent result: source=tunnel apci=GroupValueRead payload=D6, 43 ms
```

Repository outbound frames (not sent) vs the wire pattern:

```
read       11 00 BC E0 11 FA 33 03 01 00          (works: all APCI bits zero)
write A8   11 00 BC E0 11 FA 33 03 02 20 A8       (bus uses ... 02 00 80 A8)
write On   11 00 BC E0 11 FA 33 03 02 20 01       (bus uses ... 01 00 81)
descriptor 11 00 BC D0 00 00 13 03 02 C0 00       (not a descriptor read)
```

Live probe summary (E5): connect 22 ms, channel 16, assigned `1.0.1`, data
endpoint equal to the control endpoint, one `GroupValueRead` to `0/0/1`
acknowledged, `L_Data.con` received, no `GroupValueResponse` in 3 s, disconnect
status 0x00.

Reproduce:

```powershell
dotnet run --project src/Knx.Mcp -- --bus-probe=<gateway-ip>
dotnet run --project tests/Knx.Bus.Tests -- --live <gateway-ip>
```

The passive listener and the frame comparison tool were kept outside the
repository (`%TEMP%\opencode`); no capture file, key or address is committed.
