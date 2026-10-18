# BusChord router discovery — read-only validation

## Observed device

The router at `192.0.2.10:3671` answered a UDP DESCRIPTION_REQUEST from
`192.0.2.12`. Parsed response:

- MDT SCN-IP100.03 IP Router mit Sec
- Individual address: `1.0.0`
- MAC: `02:00:00:00:00:01`
- Serial: `00:00:00:00:00:01`
- Multicast: `224.0.23.12`
- Supported families: 2, 3, 4, 5, 7 (version 2)

Unicast SEARCH_REQUEST did not elicit a response in this test. The targeted IP
lookup now sends SEARCH and DESCRIPTION; the latter supplies the device identity
without opening a tunnel. MCP `bus_discover(host="192.0.2.10")` returned one
device. No firewall settings were changed. No bus writes, downloads, restarts,
address changes or device programming were performed.

## Corrected wire layouts

The previous synthetic tests repeated implementation mistakes:

- KNXnet/IP: service at offsets 2–3, total length at 4–5.
- UDP HPAI: eight bytes, one-byte length, protocol 1 for both endpoint types.
- DeviceInfo DIB: 54 bytes, one-byte length and type, project/installation field
  before serial; Latin-1 name. Preserve the advertised endpoint and collect all
  supported families after parsing every DIB.

Regression tests include an independent XKNX SEARCH_RESPONSE fixture and the
actual MDT DESCRIPTION_RESPONSE captured here. Multi-interface discovery uses a
socket bound to each usable IPv4 address and advertises its local HPAI.

## Reproduce

```powershell
dotnet run --project src/Knx.Mcp -- --bus-probe=192.0.2.10
dotnet run --project tests/Knx.Bus.Tests
```

The probe sends only IP-level SEARCH/DESCRIPTION requests. It does not connect a
tunnel or issue device/bus commands. In the desktop app, enter the router IP in
Options → Interface, then run discovery.

## Remaining limitations

This verifies discovery. The earlier note that tunnelling remained broken is
superseded by the live tunnel check below.

## Tunnelling live verification (2026-10-18)

After correcting CONNECT_REQUEST/CONNECT_RESPONSE/CRD/ACK layouts against XKNX
and Calimero, `dotnet run --project tests/Knx.Bus.Tests -- --live 192.0.2.10`
was repeated and produced a working session:

```
-> 06 10 02 05 001A  HPAI HPAI CRI(04 04 02 00)
<- 06 10 02 06 0014  channel 0x10, status 00, HPAI 192.0.2.10:3671, CRD 04 04 10 01
-> TUNNELLING_REQUEST 04 10 00 00 + cEMI GroupValueRead 0/0/1
<- TUNNELLING_ACK channel 16 seq 0 status 0x00
<- L_Data.con for the read
<- L_Data.ind from 1.3.3 (acked)
-> DISCONNECT_REQUEST channel 16
<- DISCONNECT_RESPONSE status 0x00
```

Channel 16, assigned address `1.0.1`, clean disconnect. No group writes,
downloads, restarts, address writes or firewall changes. A GroupValueResponse
did not arrive because no device answered `0/0/1` within 3 s; that is a bus
fact, not a protocol error. Group writes, download/programming, secure
tunnelling and >60 s heartbeat sessions remain unverified.

Evidence for independent layout checks:
- https://github.com/XKNX/xknx/blob/main/xknx/knxip/header.py
- https://github.com/XKNX/xknx/blob/main/xknx/knxip/hpai.py
- https://github.com/XKNX/xknx/blob/main/test/knxip_tests/search_response_test.py
- https://github.com/calimero-project/calimero-core/blob/master/src/io/calimero/knxnetip/util/DeviceDIB.java
