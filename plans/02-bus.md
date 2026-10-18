# Plan Phase 2 — KNXnet/IP-Bus

## Steps

- [x] Knx.Bus net9.0: Frame-Codec (SEARCH/CONNECT/TUNNELLING/ROUTING), HPAI, CRD, cEMI L_Data
- [x] Discovery (SEARCH_REQUEST/RESPONSE + DIB_DEVICE_INFO)
- [x] KnxTunnel: Connect-Handshake, Heartbeat, Sequence-Counter, GroupValue read/write, raw Telegram-Feed
- [x] KnxRouting: Multicast-Listen + ROUTING_INDICATION send/receive
- [x] Management-Frames + DownloadSession-Zustandsmaschine (Descriptor → Chunked MemoryWrite + Verify → Restart)
- [x] DPT-Codec (DPST-1-1, 5-1, 9-1, 9-7 + Hex)
- [x] Self-Check (Byte-Vektoren aus der KNXnet/IP-Spezifikation + Loopback-Fake) → **ALLE CHECKS OK**

## Inferenz-Flags (an echtem Gerät zu validieren)

- HPAI-Länge (9 Byte) und CRD-Layout (`[len 2][type 1][channel 1][res 1][ia 2]`)
- Extended-Frame-Längenfeld für Memory-Write/Read (Oktett 6)
- APCI-Nibble-Mapping (`apci >> 6`) für Standard-Frames
