# KNX live read acceptance (H8.4) - post-fix attempt, 2026-10-22

Status: **BLOCKED by environment, no live bus check executed.** Branch
`agents/bus/live-read-acceptance`, base `2ee25bb` (verified). Scope: read-only
live acceptance plus analysis; no product edits. Authorization: reading on the
user's KNX bus only - no writes, downloads, programming, individual-address
writes, memory writes, resets, secure commissioning or key/FDSK operations.

Gateway, MAC and local addresses are redacted below (`<gateway>`, `<local>`).
They are the same devices as in `analysis/knx-bus-read-test-plan.md` and are
known locally.

## 1. Result in one paragraph

The post-fix codec and read matching are verified **offline** against the
captured real frames and the emulator, but **no live acceptance item could be
executed on 2026-10-22**: this host has no interface in the gateway subnet, the
gateway does not answer unicast KNXnet/IP discovery, tunnel CONNECT times out,
and both passive routing listens received zero frames. The UDP path itself is
healthy (a DNS query to the site router over the same tunnel answered), so the
gateway is powered off, re-addressed or blocking KNXnet/IP - not a local socket
problem. No cEMI frame reached the bus, therefore the descriptor read and the
group reads were **not sent** and must not be reported as passed.

## 2. Environment at attempt time (2026-10-22, ~17:00-17:30 +02:00)

| Item | Observation |
|------|-------------|
| Local interfaces | VPN `<local>` and one unrelated lab LAN; **no address in the gateway subnet** |
| Route to gateway subnet | present via the VPN next hop; site router answers ICMP |
| ETS | not running (`Get-Process` found no ETS/Hawk process) |
| Settings | `TunnelAddress = 1.1.250`, same file as 2026-10-21, no secrets read |
| Gateway unicast discovery | no datagram in 4 s, sent from both local addresses |
| Gateway ICMP / TCP 80 | timeout / no connection |
| UDP path to the site | working: DNS query to the site router answered |
| Directed-broadcast SEARCH_REQUEST | one 14-byte, read-only datagram to the subnet broadcast; no reply in 5 s |

The directed broadcast was sent once, deliberately documented here, only to
rule out a re-addressed gateway. No bus-level frame (cEMI) was sent at any
point; a `GroupValueRead` or descriptor read would have required a tunnel.

## 3. Attempted live checks and exact results

Commands run from the worktree, Release build, gateway address redacted:

| # | Check | Command | Result |
|---|-------|---------|--------|
| L1 | Discovery/description, unicast | `dotnet run --project src/Knx.Mcp -c Release -- --bus-probe=<gateway>` | `0 Datagramm(e) empfangen in 4000 ms`, exit 1 |
| L2 | Tunnel connect + passive listen + read | `dotnet run --project tests/Knx.Bus.Tests -c Release -- --live <gateway> 10` | `CONNECT fehlgeschlagen: OperationCanceledException` - no channel, no cEMI |
| L3 | Passive routing listen, VPN interface | `... --routing-live <local> 15` | routing opened, `0 Telegramm(e)`, clean leave |
| L4 | Passive routing listen, lab LAN | `... --routing-live <lab-local> 10` | routing opened, `0 Telegramm(e)`, clean leave |

Consequences:

- **No tunnel** was opened (no channel, no assigned address), so the
  connect/disconnect acceptance and the frame-role check could not be repeated.
- **No `DeviceDescriptorRead`** was sent to IA 1.0.0 or to any device. The
  request never left the host because `CONNECT` failed.
- **No `GroupValueRead`** was sent. The earlier `0/0/1` read result from the
  pre-fix session is prior evidence only and is not reproduced here.
- **No second tunnel / interface conflict test**: without a first tunnel there
  is nothing to conflict with; ETS was closed but irrelevant.
- Reconnect/timeout behaviour after a live disconnect is likewise untested in
  this run.

## 4. Offline verification of the post-fix read path (not live acceptance)

These checks prove the code that *would* be exercised, not the bus:

| Suite | Result | Relevant coverage |
|-------|--------|-------------------|
| `tests/Knx.Bus.Tests` | `ALLE CHECKS OK` | captured hop 4/5/6 frames decode with group flag and correct APCI; byte-identical re-encode; 1-bit inline value; `L_Data.con stays out of the telegram feed`; `read waits for GroupValueResponse from the device`; `confirmation echo alone does not satisfy a read`; descriptor read wire frame `11 00 BC 60 ... 01 03 00` (type in the APCI parameter) |
| `tests/Knx.Diagnostics.Tests` | `ALLE CHECKS OK` | descriptor scan/timeout paths over the session seam |
| `tests/Knx.Mcp.Tests` | `ALL TESTS PASSED` | `bus_read` waits for a real response; read-only policy blocks writes/downloads |

Outbound frames built for the unrun checks (source replaced by the tunnel's
assigned address at send time):

```
descriptor type 0 to 1.0.0   11 00 BC 60 00 00 10 00 01 03 00
group read to <GA>           11 00 BC E0 <own IA> <GA> 01 00
```

## 5. Exact remaining H8.4 items

To be executed when the host is on the KNX segment (or has a routed path that
carries unicast UDP 3671 to the gateway). Each item is still **open**:

1. **Tunnel connect/disconnect:** connect, record channel and assigned IA,
   confirm `DISCONNECT_RESPONSE 0x00`, state `Disconnected`.
   Command: `dotnet run --project tests/Knx.Bus.Tests -c Release -- --live <gateway> 30`.
   Note: this probe sends its documented `0/0/1` read after the listen window;
   count it separately from the two observed GAs in step 4.
2. **Frame-role check:** in that 30 s passive window, every forwarded frame must
   decode as a group frame with hop 4-6, correct APCI and payload; no frame may
   originate from our own assigned IA except `L_Data.con`.
3. **Descriptor read, gateway IA 1.0.0:** one unicast
   `DeviceDescriptorRead` type 0; expect one `DeviceDescriptorResponse` from
   1.0.0 within 1 s with a known mask. Only then optionally probe one observed
   device (e.g. 1.1.25) the same way. No programming mode is involved.
   Command: MCP `bus_device_info(address="1.0.0", gateway="<gateway>")`; it opens
   its own short-lived tunnel, so run it before `bus_connect` or after
   `bus_disconnect`.
4. **Two group reads:** pick at most two GAs seen in step 2, send one read each,
   3 s timeout, no aggressive retry; record whether a real
   `GroupValueResponse` arrives and from which source IA.
   Command: MCP `bus_connect(device="<gateway>")`, then
   `bus_read(group_address="<GA>", timeout_ms=3000)` twice, then
   `bus_disconnect`.
5. **Second tunnel / interface conflict:** only with explicit confirmation that
   no ETS session is active; otherwise keep it skipped and documented.
6. **Live timeout/reconnect:** after a clean disconnect, one reconnect; record
   the observable timeout path. Covered by loopback tests today, not live.

Do not tick H8.4 until items 1-4 have real evidence and items 5-6 are either
run or explicitly waived by the user.

## 5b. Post-fix live rerun (2026-10-22 ~20:00, gateway reachable again)

The host was back on the KNX WLAN segment. All checks below were read-only;
no GroupValueWrite, download, programming, IA write, memory write, reset or
secure operation was sent.

| Check | Result |
|---|---|
| Unicast discovery / `--bus-probe` | gateway answered; `SCN-IP100.03 IP Router mit Sec`, IA 1.0.0 |
| Tunnel connect / passive listen 20 s | connected as 1.1.250; **12 telegrams**, all group frames, hop 5/6, APCI `GroupValueWrite`, **0 L_Data.con** in feed |
| Descriptor read 1.0.0 | response after 26 ms, mask `0x091A` (unknown mask in current table) |
| Descriptor read 1.1.29 | response after 155 ms, mask `0x0701` (BIM M112) |
| Descriptor read 1.3.3 | response after 128 ms, mask `0x07B0` (System B / BCU 2) |
| GroupValueRead 3/1/15 | **real GroupValueResponse**, value `01`, source 1.1.18, hop 5, 3 s timeout |
| GroupValueRead 3/0/1, 3/0/21, 3/3/3 | no GroupValueResponse within 3 s (read-only request acknowledged, no responder) |
| Reconnect after clean disconnect | second tunnel connected as 1.1.250, status connected, clean disconnect |
| Second tunnel / interface conflict | deliberately skipped: would occupy another tunnel and could disturb an ETS session; not needed for a read-only verdict |

Remaining H8.4 gaps are now limited to a second-tunnel/interface-conflict run
and a full line scan (broadcast management, explicitly not run). The read-only
acceptance is otherwise reproducible.

## 6. What was not run, and why

- No descriptor read, no group read, no scan, no cEMI at all: no tunnel could be
  established (L2).
- No second tunnel: no first tunnel, and it could disturb an ETS session.
- No multicast discovery: no local interface in the gateway subnet; the VPN
  does not carry KNX multicast (L3/L4).
- No writes, downloads, IA writes, memory access, restart, secure commissioning:
  outside the authorization.
