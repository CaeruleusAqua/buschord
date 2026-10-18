# BusChord — Analyse & Status

Aktueller begrenzter REA-Abgleich (2026-10-18):
[Befunde und implementierte Arbeitsabläufe](analysis/parity-implementation.md).
Die historischen Featurelisten unten sind kein vollständiger Kompatibilitätsnachweis;
insbesondere Tunnel/Download und native ETS-Parameterbearbeitung sind nicht validiert.

> BusChord ist ein unabhängiges Werkzeug. *KNX* und *ETS* sind Marken der KNX Association;
> sie werden hier nur beschreibend verwendet (Zielsystem, Dateiformate).

Ziel: ein eigener Nachbau der ETS6-Funktionalität (KNX Engineering Tool Software).
Analysiert am 2026-10-14 mit dem REA MCP (statisch, keine Ausführung).

## Produktidentität

| Erkenntnis | Evidence |
|---|---|
| Version | `ETS6N`/`ETS6C` Assembly **6.4.8718.0** |
| Haupt-Exe | `ETS6C.exe` — managed, .NET Framework **4.8**, x86, IL-only, strong-name (Token `61439873ec5e1159`), 61,8 MB |
| `ETS6.exe` | nativer x86-Launcher (116 KB), kein CLI |
| UI-Framework | `Knx.Ets.UI.Deagle.Application` (AppSettings-Key `UIFramework`) |
| DI | **Autofac 8.2**, MVVM via **CommunityToolkit.Mvvm** |
| Weitere UI-Libs | DevExpress XPF 24.1, Telerik UI, WebView2, NiL.JS |

## Komponentenlandschaft

| Assembly | Rolle | Typen | Methoden | TFM |
|---|---|---|---|---|
| Knx.Ets.ObjectModel.dll | Domänenmodell | 4024 | 38472 | net48 |
| Knx.Ets.ViewModel.dll | WPF/MVVM Shell | ~5240 | — | net48 |
| Knx.Ets.Sdk.dll | Plugin-API | 793 | 8188 | net48 |
| Knx.Falcon.dll | Bus-Kommunikation | 2214 | 17166 | net48 |
| Knx.Ets.HawkNET.dll | Download-Protokoll-Stack | 952 | — | net48 |
| Knx.Ets.Xml.ObjectModel.dll | knxproj-XML-(De-)Serialisierung | 832 | 10115 | net48 |
| EtecDOM.dll | Geräte-Objektmodell (mixed-mode) | 257 | 546 | net48 |
| Knx.Ets.ProjectArchive.dll | Projektarchiv (ZIP, .NET Standard 2.0) | 200 | 1324 | netstd2.0 |
| Knx.Bus.Cryptography.dll | KNX Data Security | 44 | 275 | net48 |
| Knx.Ets.UI.ParameterDialog.dll | Parameter-Dialog | 213 | 2063 | net48 |

## Domänenmodell (Namespaces aus Knx.Ets.ObjectModel.dll)

- `Project`, `GroupTree`, `MasterData`, `CatalogProvider`, `Product`, `Store`, `Caching`
- `Operations.*`: Devices, ChannelInstance, GroupAddress(Refs), Line/Segment/Area,
  SplitAndMerge, ProjectSync, KnxSecure, Keyring(Export|Import), Backups, Import/Export (CSV)
- `UndoRedo`, `ConsistencyRules`, `WeakEventing`, `Xml`, `Compatibility`, `CRC`

## Plugin-System (plugins.json)

- Plugin-Typen: `Download`, `DataHandler`, `UserInterface`
- Fähigkeiten: `HasParameterDialog`, `HandlesAddDeleteDevice`, `HandlesImportExport`,
  `HandlesGroupCommunicationEvents`, `ImplementsSinglePassDownload`, `ImplementsMultiPassDownload`
- Bindung an Geräte via ApplicationProgram-Ids (`M-0001_A-8027-01-0000`)
- SDK-Basisklassen: `DownloadPluginBase`, `MultiPassDownloadPluginBase`,
  `SinglePassDownloadPluginBase`, `DataHandlerPluginBase`, `UiParameterPluginBase`

## Bus-API (Knx.Falcon.dll)

`BusConnectionState`, `CommunicationPath(Builder)`, `GroupValue`, `GroupEventArgs`,
`KnxMediumType(s)`, `ICustomBusDriver`, `PropertyDescription/PropertyDataType`,
`IIoTSecurity`, `IIoTSoftwareUpdate`, `SessionType`, RfMulti-APIs

## Datenquellen

| Datei | Inhalt |
|---|---|
| `knx_master.xml` (1 MB, Version 198) | Produkt-Katalog: 774 Hersteller, 58 Datapoint-Typen (Schema `http://knx.org/xml/project/23`), signiert |
| `knx_cvexc.xml` | Product-Extensions |
| `knx_interfaces.xml` | USB-Interface-Treiberdaten |
| `plugins.json` | Plugin-Kompatibilitätsliste |
| `ontology.ttl` (3 MB) | KNX-Semantik (OWL) für Kim/Reasoning |
| Updates | `update.knx.org/software/ETS6/MainManifest.xml` |

## Nachbau-Status

### Phase 1 — Knx.Core (net9.0, BCL-only) ✅
- Projektmodell, knxproj read/write (ZIP + 0.xml), MasterData-Parser
- Self-Check: **ALLE CHECKS OK** (58 DPTs, 774 Hersteller, Round-Trip)

### Phase 2 — Knx.Bus (net9.0) ✅
- KNXnet/IP: Frame-Codec, HPAI, CRD, cEMI L_Data (Standard + Extended)
- Discovery, Tunnel (Connect/Heartbeat/GroupValue), Routing, Download (Management + State Machine)
- DPT-Codec (DPST-1-1, 5-1, 9-1, 9-7 + Hex)
- Self-Check: **ALLE CHECKS OK**

### Phase 3 — Knx.App (Avalonia 11) ✅
- MainWindow: Projektbaum, Gruppenadress-Liste, Senden-Box, Busmonitor, Geräte-Liste
- Bus-Discovery/Connect/Disconnect, GroupValue senden, Download mit Fortschritt
- Smoke-Check: **SMOKE OK**

### Phase 4 — Parameter-System (KSXML) ✅
- ParameterType, ParameterBlock, ParameterInstance, ParameterRef, ComObject, ComObjectInstanceRef, ParameterSeparator
- Ksxml-Parser für Geräte-XML-Dateien

## Stand (Phase 5, nach Subagenten-Runde 1 und 2)

| Bereich | Status | Ort |
|---|---|---|
| knxproj Lesen/Schreiben (0.xml) | ✅ | `Knx.Core/Knxproj.cs` |
| ComObject-Instanz-Referenzen + GA-Links | ✅ | `Model.cs`, `Parameters.cs`, `Knxproj.cs` |
| Master-Data-Parser | ✅ | `Knx.Core/MasterData.cs` |
| KSXML-Parameter + Parameter-Dialog (12 Typen) | ✅ | `Ksxml.cs`, `Knx.App/Views` |
| Undo/Redo (Snapshot) | ✅ | `Knx.Core/Undo.cs` |
| Projektprüfung (P001–P042) | ✅ | `Knx.Core/ProjectCheck.cs` |
| Intelligente Verknüpfung | ✅ | `Knx.Core/SmartLinking.cs` |
| Produkt austauschen | ✅ | `Knx.Core/ReplaceProduct.cs` |
| Dashboard, Optionen, Wizards (Neu/Import) | ✅ | `Dashboard.cs`, `AppSettings.cs`, `Wizards.cs` |
| Semantic Export (Turtle/JSON-LD, Ontologie eingebettet) | ✅ | `SemanticExport.cs` |
| OWL/Turtle-Parser + Ontologie + KimCheck | ✅ | `Knx.Core/Owl/*` |
| Label Creator (HTML-Bogen, CSV, Mail-Merge-Datenquelle) | ✅ | `Reports/LabelCreator.cs` |
| Telegram Recorder (XML wie ETS, CSV mit 13 Spalten, JSONL) | ✅ | `Bus/TelegramRecorder.cs` |
| KNX Data Security (Keyring, Keys, Secure Telegram, Wrapper) | ✅ Grundlage, nicht im Tunnel verdrahtet | `Bus/Security/*` |
| MCP-Server für Agenten (stdio, 38 Tools) | ✅ | `src/Knx.Mcp`, `mcp.md` |
| Produktkataloge (.knxprod) lesen, Bibliothek, Gerät einfügen | ✅ | `Knx.Core/Catalog/*`, `Views/CatalogPane` (Arbeitsbereich im Hauptfenster) |
| Mehrsprachigkeit (de/en, `{loc:Tr}`) | ✅ UI-Gerüst, dynamische Meldungen teils deutsch | `Knx.Core/Localization.cs` |
| Light/Dark-Theming | ✅ | `Knx.App/App.axaml` |
| Hawk-Download / IoT-Device-Image | ❌ offen | — |
| loc:-Relationen (Gebäude/Raum) im Semantic Export | ❌ offen (Modell hat keine Gebäudestruktur) | — |

## Weiterhin zu validieren (Inferenz)

- 0.xml-Layout gegen einen echten ETS6-Export diffen (auf dieser Maschine: innere Archive AES-verschlüsselt)
- HPAI/CRD-Layout, Extended-Frame-Längenfeld, APCI-Nibble-Mapping
- Download: Rohdatei als Image (kein BCU2-Encoding)
- Data-Security-Wire-Format (MAC-Header, S-A_Data, Wrapper) gegen eine echte sichere Anlage
- Label-Creator: ETS nutzt Word-Mail-Merge mit Template; unser HTML-Bogen + Datenquelle ist eine Annäherung

## Evidenz

- REA Evidence-ID (ETS6C): `ev_979bd0fa0ae21fcd53b5ab9f57f31cee65cb7f7bdf709befae532242c33e451d`
- ETS6C SHA-256: `3d490d0bfc3e555dac2864f73df761879494ad36877b41efe34cb915b8b52da6`
- ObjectModel SHA-256: `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a`
- Feature-Recherche (Semantic Export, Label Creator, Recorder, KIM): `analysis/ets-features.md`
- KNX-Ontologie: `C:\Program Files (x86)\ETS6\ontology.ttl` (64.791 Triples, 462 DPT-Individuen)
