# Plan Phase 1 — Kern

## Steps

- [x] Solution + Projects scaffolden (Knx.Core net9.0, Knx.Core.Tests)
- [x] Projektmodell: Project/Topology (Area, Line, Segment), Device, Channel, GroupAddress-Baum
- [x] knxproj read/write: ZIP-Container, XML-Mapping (0.xml) gemäß knxproj-Elementen der ETS6-Assembly
- [x] MasterData: knx_master.xml Parsing (Manufacturers, DatapointTypes, DatapointSubtypes)
- [x] Self-Check: Round-Trip-Test + echter Katalog-Check → **ALLE CHECKS OK** (58 DPTs, 774 Hersteller)

## Abnahme (offen, braucht echte Projektdaten)

- [ ] Nutzer exportiert aus lizenziertem ETS6 ein `.knxproj` → Diff unserer 0.xml gegen ETS-Original,
      Element-/Attribut-Namen und -Reihenfolge nachziehen (XmlNames.cs ist die einzige Stelle).
- [ ] Geräte-XML-Dateien (`M-xxxx/...`) im ZIP-Container: Namensschema + DeviceInstance-Referenzierung.

## Danach (Phase 2+)

- [ ] Knx.Bus: KNXnet/IP-Client (Discover, Tunneling, Routing), GroupValue read/write, Busmonitor
- [ ] Knx.App: Avalonia-UI (Projektbaum + Gruppenadress-Tabelle + Busmonitor)
- [ ] Phase 3: Hawk-Download, KNX Data Security, Plugin-API

## Constraints

- Ziel-Format bleibt ETS6-knaxproj (project/23-Schema), damit reale ETS-Projekte lesbar sind.
- Keine Fremdpakete im Kern (nur BCL); UI später bei Bedarf.
- Jede nicht-triviale Logik hat genau einen runnbaren Check.
