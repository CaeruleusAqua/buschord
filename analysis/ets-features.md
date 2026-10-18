# ETS6-Feature-Recovery: Semantic Export, Label Creator, Telegram Recorder, KIM

Zielinstallation: `C:\Program Files (x86)\ETS6`
Methode: REA-Metadatenanalyse (Typen, Felder, Methodensignaturen, Call-Edges, Member-Refs, Manifest-Ressourcen). Die Assemblies sind Dotfuscator-behandelt: **alle String-Literale sind verschlüsselt**, Namen von Feldern/Methoden sind umbenannt. Öffentliche Typnamen, Signaturen, Call-Edges und Manifest-Ressourcen sind lesbar; String-Inhalte (Dateinamen, IRIs, Spaltennamen) nur dort, wo sie als .NET-Ressource (`.resources`) vorliegen.

Alle Aussagen unten tragen eine Evidenz-Marke. `ev_…` = REA-Evidence-ID, `sha256:…` = Artefakt-Präfix.

## Evidenz-Register

| Marke | Quelle | Evidence |
|---|---|---|
| EV-SE-ART | `Knx.Ets.SemanticExport.dll`, 464848 B, `sha256:4bd60de4bac1b37f…`, Assembly 6.4.8718.0, .NET Framework 4.8, 303 TypeDefs, 2164 MethodDefs, 34 User-Strings (alle Obfuskator-Stub) | `ev_7af1296edbf28acc23a05472eb25f6d8fe108934309d4f171a982948d6dff487` |
| EV-SE-TYPES | Typliste SemanticExport | `ev_f8d66cfad1800f3857549fd5fc444a5b5fddbea1a02af8f7e00059841b30fec7` |
| EV-SE-EXPORTER | Members `Exporter` | `ev_63e39d0e4098fa078dce5042aaecf4fbc0fda2db0ccac2c508004de5f3bf279d` |
| EV-SE-STATE | Members + Call-Edges `KimVisitorState`, `KimProjectVisitorProvider`, `SemanticSelectionV1`, `KimReverseCheck`, `ProgressPhase` | `ev_80dab7398ef275d6aab816e5590792d9d87f3afe6436cb5796fceb7b2ab733d5` |
| EV-SE-VISITOR | Members `KimVisitor`, `EtsProjectVisitorExtensions`, `Progress`, `LoggingVisitor` + Call-Edges `<ExportAsync>d__30` | `ev_a6e835e1b913b779266c63c2b1209050fd59e991bf8693b88dcf64160056a19b` |
| EV-LC-ART | `Knx.Ets.LabelCreator.dll`, 104912 B, `sha256:aaffa3a13068b3c4…`, 6.4.8718.0, .NET FW 4.8, 59 Typen | `ev_fd2405c5f53b4468e642cd95d72e8313668a5fb3bd443de8584c7780144b29e9` |
| EV-LC-MEM | Members + Call-Edges LabelCreator | `ev_2b620cde866fcd57a9c6dc1262d5d74e5d3804c614362371e34d85db4b4cc7d0` |
| EV-TR-MEM | Members `Knx.Ets.TelegramRecorderView.dll`, 612304 B, `sha256:a8326a90ef15deb4…`, 195 Typen, 2021 MemberRefs | `ev_546dd50fc3729f89094455691dba70af61f4e9e32cb5c0e8e9088618ab296a65` |
| EV-KIM-KIM | `Knx.Kim.Kim.dll`, 1045504 B, `sha256:ad124912d295deb6…` (1.4.0.0, netstandard2.0, 439 Typen) | `ev_8faa4b0bb98093882286a2acb42d35e08982e1a012a35ab8ad53cb163239dd0a` |
| EV-KIM-PARSE | `Knx.Kim.KimParsing.dll`, 345600 B, `sha256:c216d03fb0ca3147…`, 256 Typen | `ev_986ab60d1823f7a0269f9a16a82420428097dca43aa2ba742285167fabf5bfaf` |
| EV-KIM-OWL | `Knx.Kim.Owl2Reasoner.dll`, 354304 B, `sha256:0b4692e460730852…`, 456 Typen | `ev_8b273051c5dd7bde6df5dca3c5e2566cc1b63a96cf5213ad26ce0b28e789334a` |
| EV-KIM-REASON | Members `IOWLReasoner`, `BasicKimLibReasoner`, `KimOntology`, `RDFFileFormat` | `ev_f748011d2ad6f9d8f0f0d56a32f8a3099651924424aa7e171f56c5a07d3e8264` |
| EV-KIM-CORE | `Knx.Kim.KimClassesCore.dll`, 235520 B, `sha256:97f6be0fc3076261…`, 124 Typen | `ev_476b7ae2e0467c77787fd001f65c97af523153bff21716ed32203cb079e2f7e1` |
| EV-KIM-GEN | `Knx.Kim.KimGenerator.dll`, 183808 B, `sha256:d4db814d1a68e1e4…`, 96 Typen | `ev_c01d1acf2ba50238ff6ca3f29af4d36bf32487f0e0a4275fe6a89726048bf72b` |
| EV-KIM-WRITE | `Knx.Kim.KimWriters.dll`, 62464 B, `sha256:f05a01bc8dffb0fa…`, 33 Typen | `ev_f1673cd31c29f526e0ac69ed5d4c52c200bd6257c7b8d415cdc43c1ed8660e20` |
| EV-KIM-VOCAB | `Knx.Kim.Vocabularies.dll`, 297472 B, `sha256:1aca18f40da7834a…`, 54 Typen | `ev_a8e1f5a5c210208edbfcf99673cd40d332cbef6dd8ca607614aabfbcb0d4c33a` |
| EV-KIM-DP | `Knx.Kim.DataProvider.dll`, 282064 B, `sha256:1158afb2d6c1bb18…`, 191 Typen | `ev_69e0e64cf417370a7435a70ad36d18d8ba31b5c62de982fe03ba724ee3f67e44` |
| EV-KIM-EXT | `Knx.Kim.Ext.dll`, 126976 B, `sha256:f9a6108122948e5c…`, 75 Typen | `ev_9c7597f2386d5112134885cf8135fc05e946d511956b683b4937eaa5b3c84a2c` |
| EV-CSI | `Knx.Ets.Common.Semantics.CurrentSemanticsInformation` | `ev_ad63717a96e04c406e5bec713c46a8b272acc66cc77b0fbc024fa4d629355bdd` |
| EV-RES-TR | `Knx.Ets.TelegramRecorderView.Properties.Resources`, 206 Einträge, per ResourceReader ausgelesen | `%TEMP%\opencode\etsres\Knx.Ets.TelegramRecorderView.Properties.Resources.resources.txt` |
| EV-RES-VM | `Knx.Ets.ViewModel.Properties.Resources`, 2812 Einträge | `…\Knx.Ets.ViewModel.dll.Knx.Ets.ViewModel.Properties.Resources.resources.txt` |
| EV-RES-LOC | `Knx.Ets.Localization.Properties.Resources`, 1405 Einträge | `…\Knx.Ets.Localization.Properties.Resources.resources.txt` |
| EV-TTL | `C:\Program Files (x86)\ETS6\ontology.ttl`, 3200364 B, 55911 Zeilen, 16 `@prefix`, 2480 `a owl:Class` | Datei, Zeilennummern unten |
| EV-KIMRES | Eingebettete Ontologien aus `Knx.Kim.KimContentProvider.dll` extrahiert: `…ontologies.knx_v1.ttl` (925042 B, `sha256:ebed450d…`), `…ontologies.original_schema.ttl` (4285011 B, `sha256:941c162f…`) | Manifest-Ressourcen, `%TEMP%\opencode\kimres\` |

---

## 1. Knx.Ets.SemanticExport.dll — Semantischer Export

### 1.1 Typinventar (EV-SE-TYPES)

Namespaces und öffentliche Typen (Auszug, vollständig 303 TypeDefs):

- `Operations.Export`: `Exporter`, `EtsProjectConfiguration`, `ProjectElementsEnumerator`, `SemanticDataRetrieval`, `KimExtFactory`
- `Operations.Visitor`: `IEtsProjectVisitor`, `EtsProjectVisitorExtensions`, `KimVisitor`, `KimVisitorState`, `ProjectVisitorProcessState`
- `Operations.Visitor.Provider`: `IEtsProjectVisitorProvider`, `KimProjectVisitorProvider`, `LoggingProjectVisitorProvider`
- `Operations.Visitor.Mapper` (exakt 17 Mapper + 2 Abstraktklassen + `IMapper<T>`): `SiteMapper`, `BuildingMapper`, `StairwayMapper`, `CorridorMapper`, `FloorMapper`, `RoomMapper`, `SpaceMapper`, `InstallationMapper`, `DeviceMapper`, `ChannelMapper`, `GroupAddressMapper`, `GroupObjectMapper`, `ProductMapper`, `ApplicationProgramMapper`, `ApplicationFunctionMapper`, `FunctionalityMapper`, `TradeMapper`; Basisklassen `Abstract.AbstractLocationMapper<T>`, `Abstract.AbstractThingMapper<T>`, Interface `IMapper<T>` mit `AddRelations`, `Result`, `RuntimeKimClassManager`. **Kein** Mapper für Line/Segment/Area — diese Ebenen werden über `ProjectElementsEnumerator` (`VisitLinesAsync`, `VisitSegmentsAsync`, `VisitAreasAsync`) und die Location/Thing-Mapper abgedeckt.
- `Operations.SemanticSelection`: `ISemanticSelection`, `SemanticSelectionV1`
- `Operations.KimReverseCheck`: `KimReverseCheck`, `GroupObjectWithPossibleDpas`, `MatchScore`, `ProjectBased.ChannelParameterBlockAnalyzer`, `ProjectBased.DpaGuess`
- `Operations.Utility`: `Progress`, `ProgressPhase`, `ProgressReport`, `DptConversion`, `CollectionUtil`, `Convert`, `LoggingVisitor`
- `Operations.Logging`: `ILogger`, `ILogEntry`, `Log`, `LogEntry`, `Logger`
- `Model.Project` / `Model.Product` / `Model.MasterData`: eigene Abstraktionen (`IProject`, `IArea`, `IBuildingPart`, `IDevice`, `IGroupAddress`, `IComObjectInstanceRef`, …) mit drei Implementierungsfamilien `Sdk.*`, `ObjectModel.*`, `Xml.*` (MasterData)

**Einstiegspunkt** (EV-SE-EXPORTER, EV-SE-STATE, EV-SE-VISITOR):

```
Exporter.ExportSemanticDataAsync(...)        // async state machine <ExportSemanticDataAsync>d__16
Exporter.CreateSemanticDataAsync(...)        // <CreateSemanticDataAsync>d__15
Exporter.LoadOntologyAsync(...)              // <LoadOntologyAsync>d__14
  └─ KimProjectVisitorProvider.LoadKimClassManagerFromOntologyAsync(ct, progress)
EtsProjectVisitorExtensions.ExportStateAsync(visitor, string, ct)
  └─ KimVisitorState.ExportAsync(string, ct) // <ExportAsync>d__30
       ├─ KimVisitorState.GetProjectGraph()  -> IGraph
       ├─ KimVisitorState.SerializeToTurtle(IGraph)  -> string
       ├─ KimVisitorState.SerializeToJsonLd(IGraph)  -> string
       └─ <WriteFileAsync>d__39 (Datei-Schreiben)
```

`Exporter` ist eine öffentliche Fassade; `KimVisitor` ist ein Visitor über das ETS-Projektmodell mit `BeginVisit`/`Visit`/`EndVisit` und einem `KimVisitorState` (EV-SE-VISITOR). Die UI-Seite bestätigt die drei Schritte wörtlich (EV-RES-VM):

```
TurtleFileExporterStepLoadOntology      = Loading ontology
TurtleFileExporterStepCreateSemanticData= Creating semantic data
TurtleFileExporterStepExportSemanticData= Exporting semantic data
ExportWizardPageTurtleExportTitle       = Exporting Turtle File...
TTLExportFile                           = Semantic Turtle Export
JSONLDExportFile                        = Semantic JSON-LD Export
Label_OntologyVersion                   = Ontology
```

### 1.2 Erzeugte Artefakte (belegt)

`KimVisitorState` erzeugt **einen** Projektgraphen und serialisiert ihn **zweimal** (EV-SE-STATE Call-Edges):

- Turtle: `IGraph.Merge(...)` + `VDS.RDF.Writing.CompressingTurtleWriter..ctor` + `BaseRdfWriter.Save`
- JSON-LD: `TripleStore..ctor` + `BaseTripleStore.Add` + `JsonLdWriter..ctor` + `JsonLdWriter.SerializeStore`, danach `JsonLdProcessorOptions..ctor` + `JsonLdProcessor.Compact` und Nachbearbeitung mit `Newtonsoft.Json.Linq.JObject` (Parse/Properties/Serialize)

⇒ Der Export liefert **Turtle (.ttl)** *und* **JSON-LD** derselben Daten, nicht nur Turtle. Der Wizard-Name „TurtleFileExporter“ ist historisch; `JSONLDExportFile` belegt den zweiten Exporttyp.

**Kopie der Ontologie: ja, für Turtle belegt.** `SerializeToTurtle` ruft `IGraph.Merge` auf, und `KimVisitorState` lädt die Ontologie in einen separaten Graphen:

- `.ctor` → `Knx.Kim.KimContentProvider.KimContentProvider::get_OntologyContent`
- `kRkDxA5Lwyv` → `Graph..ctor` + `GraphExtensions.LoadFromString` + `StreamReader..ctor` (Ontologie aus String/Stream)
- `wfvDxems0Qn` → `Graph..ctor` + `INamespaceMapper.AddNamespace` (legt die Präfixe des Ontologie-Graphen an)
- `TeFDxZwTcUA` → `INamespaceMapper.get_Prefixes` / `GetNamespaceUri` (Präfix-Mapping lesen)

Für den JSON-LD-Pfad wird der Graph in einen `TripleStore` gelegt (`BaseTripleStore.Add`); dass dort derselbe (gemergte) Graph hinzugefügt wird, ist **Inferenz** (siehe Abschnitt 5).

### 1.3 Ontologiequelle

- Laufzeit: `Knx.Ets.Common.Semantics.CurrentSemanticsInformation` bietet `CurrentEtsSemanticsVersion`, `CurrentETSSemanticsVersionOntologyDirectory`, `CurrentETSSemanticsVersionOntologyFile` (EV-CSI); SemanticExport referenziert genau diese Properties (EV-SE-TYPES Member-Refs: `Knx.Ets.Common.Semantics.CurrentSemanticsInformation::get_CurrentETSSemanticsVersionOntologyFile`, `Knx.Ets.Common.EtsFolders::get_EtsProgramAbsolutePath`).
- Ausgelieferte Datei: `C:\Program Files (x86)\ETS6\ontology.ttl` (3,2 MB, EV-TTL) — die einzige `.ttl` im Installationsverzeichnis.
- Zusätzlich bringt `Knx.Kim.KimContentProvider` zwei eingebettete Ontologien mit (EV-KIMRES):
  - `Knx.Kim.KimContentProvider.ontologies.knx_v1.ttl` (925042 B): Präfixe `knx:` 2020, `loc:` **2020**, `dic:`, `dic-tag:`, QUDT, schema.org — die **ältere KIM-v1**-Fassung.
  - `Knx.Kim.KimContentProvider.ontologies.original_schema.ttl` (4285011 B): Präfixe `core:`/`loc:`/`tag:` **2023/en50090-6-2**, `knx:` 2020, `mac:` 2020 — dieselbe Präfixwelt wie `ontology.ttl`.
- Der Code liest `KimContentProvider.Default` / `.OntologyContent` (EV-SE-STATE) und `RuntimeKimClassManager.CreateFromOntology` — d. h. Klassenmodell aus TTL, nicht aus generierten Assemblies.

### 1.4 Graphstruktur (belegt über Member-Refs, EV-SE-TYPES)

Der Export baut KIM-Instanzen der generierten Namespaces `En5009062.Core`, `En5009062.Loc`, `Ontology.Knx` und setzt folgende Eigenschaften/Relationen:

| KIM-Klasse (Code) | gesetzte Relationen (Code) |
|---|---|
| `En5009062.Loc.MutableSite` | `HasBuilding`, `HasLocation` |
| `En5009062.Loc.MutableBuilding` | `HasFloor`, `HasRoom`, `HasSpace`, `HasLocation` |
| `En5009062.Loc.MutableFloor` | `HasRoom`, `HasSpace`, `HasLocation` |
| `En5009062.Loc.MutableRoom` | `HasRoom`, `HasSpace`, `HasLocation`, `HasLocationUsage` |
| `En5009062.Loc.MutableSpace` | `HasFloor`, `HasRoom`, `HasSpace`, `HasLocation` |
| `En5009062.Loc.IMutableLocation` | `ContainsEquipment`, `HasApplicationFunction`, `Groups` |
| `En5009062.Core.MutableDevice` | `HasChannel`, `Hosts`, `SetHasProduct` |
| `En5009062.Core.MutableInstallation` | `ContainsAsset` |
| `En5009062.Core.MutableFunctionality` | `HasPoint` |
| `En5009062.Core.IMutableApplicationFunction` | `HasFunctionPoint` |
| `En5009062.Core.MutableDatapoint` | `DatapointType`, `Template`, `_iri`, `_additionalProperties`, `Title`, `Description`, `Readable`, `Writable` |
| `En5009062.Core.MutableApplicationProgram` | `Implements` |
| `En5009062.Core.IThing` | `_iri` (jede Entität bekommt eine IRI) |
| `Ontology.Knx.MutableChannel` | `HasPoint` |
| `Ontology.Knx.MutableDatapointType` | `DptMajorNumber`, `DptMinorNumber` |
| `Ontology.Knx.MutableFunctionPoint` | (ctor) |

IRI-Bildung: `KimVisitorState.GenerateId(...)`, `GenerateIri(string)`, `ProjectGuid`, `ProjectNameSpace` (string), `RenderKim()` (EV-SE-STATE).

Der ontologische Gegencheck bestätigt exakt diese Property-Namen (EV-TTL): `loc:hasBuilding` (Z. 53964), `loc:hasFloor` (Z. 53973), `loc:hasRoom` (Z. 56298), `loc:hasSpace` (Z. 56770), `loc:hasLocation` (Z. 57095), `loc:containsEquipment` (Z. 37700), `loc:hasApplicationFunction` (Z. 37719), `core:hasProduct` (Z. 53914), `core:hasPoint` (Z. 56254), `core:containsAsset` (Z. 37576), `core:groups` (Z. 37585), `core:hosts` (Z. 37618), `knx:hasChannel` (Z. 16498).

### 1.5 Side-Car „Semantic Selection“

`SemanticSelectionV1` (EV-SE-STATE):
- Felder/Props: `Associations : Dictionary<string,string>`, `StorageVersion : int` (const, `static literal`)
- Methoden: `GetAssociation(IGroupAddress) : string`, `SetAssociation(IGroupAddress, string) : bool`, `From(string) : ISemanticSelection`, `ToString()`, statisch gecacht
- Serialisierung: `JsonConvert.SerializeObject` / `DeserializeObject`

Zusätzlich: `Knx.Ets.ObjectModel.dll` und `Knx.Ets.ViewModel.dll` referenzieren `Knx.Ets.SemanticExport` (String-Scan im Installationsverzeichnis: Treffer in `Knx.Ets.ObjectModel.dll`, `Knx.Ets.ObjectModelWindowsBase.dll`, `Knx.Ets.Ui.UserControls.dll`, `Knx.Ets.ViewModel.dll`, `Knx.Ets.SemanticExport.dll`).

⇒ ETS merkt sich pro Projekt, welche ETS-Gruppenadresse bereits einer KIM-Entität zugeordnet wurde (JSON, versioniert über `StorageVersion`), und die Zuordnung wird im Projekt gehalten (ObjectModel-Referenz), nicht nur im Export.

### 1.6 Weitere Beobachtungen

- `KimReverseCheck.DeterminePossibleDpasForGroupObject(IComObjectInstanceRef) : IReadOnlyCollection<…>` + `GroupObjectWithPossibleDpas` + `MatchScore` + `ChannelParameterBlockAnalyzer`/`DpaGuess`: Rückprüfung, welche Datenpunktzuweisungen („DPA“) ein Gruppenobjekt haben könnte — inkl. Score. Also nicht nur Export, sondern auch Import-/Prüfpfad.
- `DptConversion.From(IDatapointType, …)` verbindet ETS-DPTs mit den `Ontology.Knx.MutableDatapointType`-Instanzen (`DptMajorNumber`/`DptMinorNumber`).
- `ProgressPhase` = `Unspecified, Initializing, Processing, Finalizing`; `Progress(Current, Total, Ratio)`.
- `LoggingVisitor` und `KimVisitor` implementieren dasselbe `IEtsProjectVisitor`-Interface (Trockenlauf/Diagnose ohne Schreiben).
- `EtsProjectConfiguration` ctor nimmt 9 Parameter vom Typ `IReadOnlyCollection<…>`-artiger Generics (Signaturen `class:877<class:20,valuetype:212,valuetype:161,class:157>`), also die je Phase vorgefilterten Elementmengen (Bereiche, Geräte, Gruppenadressen, …).

### 1.7 Konsequenz für unsere Implementierung

1. Zwei Serialisierungen aus **einem** Graph erzeugen: Turtle (komprimiertes Turtle, Präfixe aus der Ontologie) und JSON-LD (kompakt, mit `@context`).
2. Die Ontologie wird in den Ausgabegraph **gemerged** (mindestens im Turtle-Pfad). Also: Ontologie-Tripel + Projekt-Tripel in einer Datei. Für unseren Klon heißt das: `ontology.ttl` (bzw. die eingebettete `original_schema.ttl`) laden, Namespace-Map übernehmen, dann Projektgraph dazu mergen.
3. IRI-Schema: pro Projekt ein Namensraum (`ProjectNameSpace`) + deterministische IDs (`GenerateId`/`GenerateIri`, `ProjectGuid` als Anker). Nicht Hash pro Lauf.
4. Keine „Pages“: der Export ist **ein** Projektgraph, gegliedert über die KIM-Containment-Kette Site → Building → Floor → Room/Space → Equipment/Function → Point.
5. Eine Zuordnungstabelle ETS-Gruppenadresse ↔ KIM-Entität persistent führen (`SemanticSelectionV1`: `Dictionary<string,string>` + `StorageVersion`), JSON-serialisiert.

---

## 2. Knx.Ets.LabelCreator.dll — Label-Erzeugung

### 2.1 Typen (EV-LC-ART, EV-LC-MEM)

| Typ | Rolle |
|---|---|
| `Label` | Ein Etikett. Öffentliche Properties: `Id`, `Description`, `IndividualAddress`, `Manufacturer`, `Product`, `Program`, `Room`, `Trade`, `ImageName`, `IsAddressEmpty` |
| `LabelCollection : List<Label>` | Ctor `(string)`, `(IEnumerable<Label>, string)`; `CanCreateForType(…)`, `Sort()` |
| `LabelCreatorBase` | `Labels`, `TemplatePath` (get/set), `IsMultiSelect` (get/set), Ctor `(List<Label>, string, bool)`, `Run()` |
| `LabelCreatorMsWord : LabelCreatorBase` | Ctor `(List<Label>, string, int, int, bool)`, `Run()` |
| `DataSourceCreator` | Ctor `(string, int, int)`, `Create(List<Label>)`, `File.WriteAllText`, Feld `Dictionary<string,int>` + `List<string>` |
| `LabelSanitizer` | `Sanitize(string) : string` (nur diese eine öffentliche Methode) |
| `Properties.Resources` | einziger Eintrag: `LabelCreatorSubPath = Label Creator` |
| `Exceptions.LabelCreatorException` | — |

`Label` hat 10 String-Felder (9 öffentliche Properties + `ImageName`), 1 Bool und 1 Objektfeld; die Property-Namen sind **nicht** obfuskiert (öffentliche API). ⇒ Das ist die Etikett-Datenstruktur, die ein Template als Serienbrieffelder erwartet.

### 2.2 Geometrie / Sheet-Modell

Belegt:
- `LabelCreatorMsWord..ctor(List<Label>, string templatePath, int, int, bool isMultiSelect)` — zwei `int` zusätzlich zur Basisklasse (EV-LC-MEM). `DataSourceCreator..ctor(string, int, int)` dieselben zwei `int`.
- UI-Texte (EV-RES-LOC): `LabelRows = Rows`, `ColumnsLabel = Columns`, `LabelNumberOfItems = Number of copies`, `LabelNumberOfLabelsPerDevice = Number of labels per device`, `LabelSkipLabels = Skip labels`, `MarginsGroupBox = Margins`, `TopMarginLabel = Top`, `BottomMarginLabel = Bottom`, `LeftMarginLabel = Left`, `RightMarginLabel = Right`, `TextLabelLabelCreatorTemplatePath = Template Path`, `TextLabelLabelCreatorLogoPath = Logo File`, `TextBlockLabelCreator = Create Labels`, `TextBlockLabelCreatorCurrentLogo = Preview`, `TemplateLabel = Use template`.
- **Keine** Maßangabe (mm) in den Ressourcen; **keine** Vorlagen-Datei (`.dotx`/`.docx`) im Installationsverzeichnis (rekursive Suche nach `*.dotx,*.docx,*.dot,*.xlsx,*.csv` → 0 Treffer).

⇒ Blattgeometrie (Zeilen, Spalten, Ränder) lebt **im Word-Template**, nicht im Code; der Code reicht nur Zeilen/Spalten/Anzahl durch. `TemplatePath` kommt vom Aufrufer (UI-Einstellung), Vorlagen sind nutzerseitig.

### 2.3 Erzeugungsweg (Call-Edges, EV-LC-MEM)

```
LabelCreatorMsWord..ctor: Path.GetDirectoryName, Path.Combine, Directory.CreateDirectory,
                          new DataSourceCreator(...)
LabelCreatorMsWord.Run:   Word._Application.Visible/WindowState, Documents.Add,
                          F00017owhlD(...), HT301G7rDVn(...), K8y01WlSCUq(...), Word Build
HT301G7rDVn:              DataSourceCreator.Create(...)
K8y01WlSCUq:              _Document.MailMerge.OpenDataSource(...)
F00017owhlD:              MailMerge.Destination = <WdMailMergeDestination>,
                          MailMerge.Execute(...), Document.Fields.Update(),
                          Document.Close(...), Application.Visible
DataSourceCreator.Create:  File.WriteAllText(...)   (Datensource-Datei im Temp-Ordner)
```

⇒ Der Label Creator ist ein **Word-Serienbrief-Generator**: Etiketten werden als Datensource (Textdatei, `WriteAllText`) in einen temporären Ordner geschrieben, ein Word-Template wird geöffnet, `MailMerge.OpenDataSource` + `MailMerge.Execute` mit Ziel „neues Dokument“, Felder aktualisiert. Ausgabeformat = **Word-Dokument** (kein PDF, kein CSV-Export, kein eingebauter Renderer). `WdMailMergeDestination` ist als Interop-Enum im Assembly (TypeDefs `WdMailMergeDestination`, `WdWindowState`).

`IsMultiSelect` + `LabelCollection.CanCreateForType` + UI-Text „Number of labels per device“ ⇒ Etiketten werden für ausgewählte Geräte (Mehrfachauswahl) erzeugt, pro Gerät N Kopien.

### 2.4 Konsequenz für unsere Implementierung

1. Datenmodell `Label` 1:1 übernehmen (9 Textfelder + `IsAddressEmpty` + Bildname); Sanitizer für Word-taugliche Feldwerte vorsehen.
2. Blattgeometrie nicht im Code modellieren: `TemplatePath` + `Rows` + `Columns` + `Copies` an ein Template geben. Wer Geometrie braucht, definiert sie in der Vorlage (unsere Vorlage: Platzhalterfelder = Property-Namen).
3. Ausgabe als Dokument (bei uns DOCX statt Word-Automation; kein Interop-Zwang), Datensource als einfache Text-/CSV-Datei.
4. Wenn wir mm-Ränder brauchen: als Template-Parameter, nicht als Codekonstante (die Original-Anwendung kennt keine mm-Felder).

---

## 3. Knx.Ets.TelegramRecorderView.dll — Telegram Recorder / Monitor

### 3.1 Typen (EV-TR-MEM)

- ViewModels: `MonitorViewModelBase<T>` (Riesen-API: `ExecuteStartRecordingCommandAsync`, `ExecuteStopRecordingCommandAsync`, `ExecuteStartReplayCommandAsync`, `ExecuteStopReplayCommandAsync`, `ReplayAsync`, `SaveFileAsync`, `OpenFilesAsync`, `MergeTelegramsAsync`, `OnGroupMessageReceived`, `OnRecordingStateChanged`, `SendGroupValueReadAsync`, `SendGroupValueWriteAsync`, `ReadValueAsync`, `WriteValueAsync`), `GroupMonitorViewModel`, `BusMonitorViewModel`, `BusActivityMonitorViewModel`, `MonitorLoadOptionsDialogViewModel`, `MonitorDisplayContext`, `MonitorEventViewModel`, `TelegramPaginator`, `BoundedObservableRangeCollection<T>` (`Limit`, `Discarded`, `SmartAdd`), `LiveLogger`, `CsvExporter`, `AddressResolver`, `DiagnoseService`, `GroupMessageSender`, `SecureKeyService`, `MonitorConnectionService`
- Options: `TelegramMonitorSettings`, `TelegramMonitorSettingsCollection`, `TelegramFormatting`, `TelegramMonitorTimeFormat`, `LongTimeRecordingSettings`, `LongTimeRecordingRotationInterval`, `LongTimeRecordingFilenamePostfix`, `LongTimeRecordingDeleteAfter`, `Options.MonitorOptions*ViewModel` (General/Recording/Coloring/Conditions/TextFormat), `ComparisonViewModel`, `ConditionViewModel`, `ConditionalFormatViewModel`
- Statistik: `Statistics.StatisticReport`, `GroupStatistics`
- Konverter: `FrameToColorConverter`, `FrameToColorConversionContext`, `DetailLevel`, `GroupAddressFormatter`, `DateTimeFormatter`, `ConnectionStateToIconConverter/TextConverter`

### 3.2 Datei-IO liegt **nicht** in dieser Assembly

Alle Datei-Operationen laufen über `Knx.Falcon.Monitor.Serialization` (EV-TR-MEM Member-Refs; Assembly-Referenz `Knx.Falcon.Monitor 6.4.8718.0`):

```
Knx.Falcon.Monitor.Serialization.IFileReader.ReadEventsAsync(...)
Knx.Falcon.Monitor.Serialization.IFileWriter.StartIncrementalWriter(...)   // inkrementell schreiben
Knx.Falcon.Monitor.Serialization.IFileWriter.WriteEventsAsync(...)
Knx.Falcon.Monitor.Serialization.IIncrementalFileWriter.WriteEventAsync(...)
Knx.Falcon.Monitor.Serialization.XmlFileWriter..ctor / XmlFileReader..ctor
Knx.Falcon.Monitor.Serialization.CsvFileWriter..ctor / DefaultColumns / DefaultSeparator
```

⇒ **Dateiformat = XML** (XmlFileWriter/XmlFileReader), **CSV nur als Export** (`CsvExporter`, `CsvFileWriter`, UI `ListExportToCsv = List export to CSV`).

Belege für die Endung direkt aus den Ressourcen (EV-RES-TR):

```
MonitorSaveFileExtension   = xml
MessageInvalidXmlLogFileFormat = Invalid XML log file format: {0}
MessageUnknownFileFormat   = (Fehlermeldung für unbekanntes Format)
TelegramFiles              = Telegram files
AllFiles                   = All files
OpenFileDialogTitle_OpenTimeFile = Open Time File
```

### 3.3 Ereignis-/Spaltenmodell

Events (Member-Refs, EV-TR-MEM) — `Knx.Falcon.Monitor.Events.*`:

| Typ | Properties |
|---|---|
| `EventBase` | `Timestamp`, `TimestampLocal`, `Service`, `Message`, `Number`, `Info`, `Interface`, `GetProperties()` |
| `ConnectionEvent` | `State` |
| `RecordStartEvent` | `ConnectionName`, `ConnectionOptions`, `ConnectorType`, `Host`, `Mode` |
| `KnxMessageEvent` | `FalconTelegram`, `EnclosedTelegram`, `DecryptedEnclosedTelegram`, `HopCount`, `Priority`, `IsDestinationGroup`, `IsDestinationBroadcast` |
| `MessageEvent` | `SourceAddress`, `SourceName`, `SourceDescription`, `DestinationAddress`, `DestinationName`, `DestinationDescription`, `Type`, `DatapointType`, `Flags`, `GroupDataFormattedValue`, `RawData`, `IsDataSecured`, `IsTransportSecured`, `IsRepeated`, `LinkLayerAck`, `MediumInfo`, `BuildingPart`, `BuildingFunction`, `DestinationGroupMetaData`, `Comment` |
| `IoTMessageEvent` | `ResourcePath`, `GetGroupEventType()`, `GetGroupValue()` |
| `Metadata.*` | `GroupMetaData` (Name, Reference, DatapointType, FunctionPoint), `DeviceMetaData`, `BuildingPartMetaData`, `FunctionMetaData`, `FunctionPointMetaData`, `IAddressResolver` |

CSV-Exportspalten (EV-RES-TR, Schlüssel + Werte):

| Schlüssel | Wert |
|---|---|
| `CsvHeaderTime` | Time |
| `CsvHeaderService` | Service |
| `CsvHeaderFlags` | Flags |
| `CsvHeaderPrio` | Prio |
| `CsvHeaderSourceAddress` | Source Address |
| `CsvHeaderSourceName` | Source Name |
| `CsvHeaderDestinationAddress` | Destination Address |
| `CsvHeaderDestinationName` | Destination Name |
| `CsvHeaderRout` | Hop Count |
| `CsvHeaderType` | Type |
| `CsvHeaderDpt` | DPT |
| `CsvHeaderIack` | Iack |
| `CsvHeaderInfo` | Info |

⇒ 13 CSV-Spalten. Zeitformate: `DateTimeFormat = yyyy-MM-dd HH:mm:ss.fff`, `TimeOnlyFormat = HH:mm:ss.fff`.

Druck-/Monitoransicht-Spalten (`PrintHeader_*`, EV-RES-TR): Timestamp, Service, Source, SourceName, SourceDescription, Destination, DestinationName, DestinationDescription, Type, Dpt, Iack, Info, Route, Number, Flags, Priority, MediumInfo, Security, Interface.

Frame-Properties (Detailansicht, `FrameProperty_*`): SourceAddress, SourceName, DestinationAddress, DestinationName, BuildingPart, BuildingFunction, Service, Type, Text, Info, MessageCode, RawData, RawValue, FormattedValue, DatapointType, Priority, HopCount, LinkLayerAck, IsRepeated, IsDataSecured, IsSystemBroadcast, DecryptionStatus, RFInfo, FrameFormat.

### 3.4 Aufnahme/Wiedergabe-Semantik

- Aufnahme: `ExecuteStartRecordingCommandAsync`/`ExecuteStopRecordingCommandAsync`, Statusflags `IsRecording`, `HasUnsavedTelegrams`, `PromptCloseWithUnsavedTelegrams`; im Dateistrom Marker-Events `RecordStartEvent` (Host, ConnectionName, ConnectionOptions, ConnectorType, Mode) und RecordStop (UI: `Info_RecordStart = Recording was started, Host={0}, Connection={1}, Mode={2}`, `Info_RecordStop`, `Service_RecordStart/Stop/Trigger`, `Info_RecordTrigger = Capture was triggered by the next telegram`).
- Inkrementell: `IFileWriter.StartIncrementalWriter` + `IIncrementalFileWriter.WriteEventAsync` ⇒ während der Aufnahme wird fortlaufend geschrieben (kein Sammeln bis Stop). `BoundedObservableRangeCollection` begrenzt nur die UI-Liste (`Discarded`, `TelegramLimitAchieved = ETS can only display max. {0} telegrams. Older telegrams are discarded.`).
- Wiedergabe: `ExecuteStartReplayCommandAsync`/`Stop`, `ReplayAsync`, `IsReplaying`, `Delay time[sec]` (`LabelDelayTime`), `LabelCycleTime` (`Cycle time[sec]`), Abschlussmeldung `MonitorReplayFinished = {0} telegrams have been replayed`, Sicherheitswarnung `ReplaySecureTelegram = The telegram sequence contains secure telegrams. Note that replayed secure telegrams will not be accepted by the addressed devices. Continue?` ⇒ Replay sendet Telegramme auf den Bus, ohne Secure-Replay.
- Laden: `OpenFilesAsync` (Multiselect), `MonitorLoadOptionReplace = Replace the current telegrams`, `MonitorLoadOptionMerge = Merge telegrams`, optionale Filter-/Trigger-Anwendung beim Laden (`ApplyFilterTemplate`, `ApplyTriggerTemplate`, `LabelCheckBoxUseFilterWhenLoading`, `LabelCheckBoxUseTriggerWhenLoading`), `TelegramPaginator`.
- Speichern: `MonitorSaveAllTelegrams` / `MonitorSaveSelectedTelegrams` / `MonitorSaveFilteredTelegrams` ⇒ Auswahl beim Speichern (alle/ausgewählt/gefiltert).
- Live-Logging (separat): `LiveLogger.StartNewLogFileAsync`, `WriteEventAsync`, `DisposeAsync`; Optionen `LabelCheckBoxLiveLogging = Enable live logging of monitored frames to a file`, `LabelTextLogfileName`, `LabelComboLogfilePostfix`, `LabelComboNewLogfileInterval`, `LabelComboLogfileMaxAge`, `LabelTextLogfileMaxCount`, `LabelCheckBoxLogfileSpaceLimit`, `LabelComboLogfileSpaceLimitUnit = MB`.
- Satellite-/Long-Time-Recording: `MonitorNoLicenseToLoadSatelliteRecordings = The telegram file selected is recorded with the ETS Recorder Satellite. To open these files, the Long Time Recording license is required!`, `LabelLongTimeRecordingFileLabel = Currently recording to`, `LongTimeRecording*`-Optionen (Rotation, Postfix, DeleteAfter).
- Statistikbericht (Ressourcen-Schlüssel EV-RES-TR): Overview (Record start/end, Duration, Host, Connection name/mode/type/options, Events), Bus load (avg/max/min), Telegrammzahlen (total, read, write/response, repeated/unconfirmed, negative confirmed, busy), Cycle time (avg/max/min), Top-Sender/-Receiver/-Groups, Group statistics, inactive groups/devices, disconnected duration.

### 3.5 Konsequenz für unsere Implementierung

1. Aufnahmedatei = **XML** mit Endung `.xml`; pro Aufnahme RecordStart-Metadaten (Host, Verbindungsname, Verbindungsoptionen, Connector-Typ, Modus) + Eventstrom; inkrementell schreiben, nicht am Ende am Stück.
2. CSV ist reiner Export mit den 13 Spalten oben; Zeitformat `yyyy-MM-dd HH:mm:ss.fff`.
3. Replay = erneutes Senden der Events in Reihenfolge mit optionaler Verzögerung; Secure-Telegramme werden wiederholt, aber Geräte akzeptieren sie nicht → Warnung statt Sonderbehandlung.
4. Die Datei-IO-Schicht als eigene, testbare Komponente schneiden (`IFileWriter`/`IFileReader`/`IIncrementalFileWriter`), wie im Original — inkl. `StartIncrementalWriter`.
5. Satelliten-/Long-Time-Recording-Dateien als eigenes Lizenz-/Format-Thema behandeln, nicht als Teil des normalen Monitors.

---

## 4. KIM — KNX Information Model (Knx.Kim.*)

### 4.1 Was KIM ist

KIM = KNX Information Model: eine OWL-2-Ontologie (EN 50090-6-2) plus .NET-Toolchain, um ETS-Projektdaten als RDF-Graph zu modellieren, zu validieren und zu serialisieren. Schichten (Assembly-Referenzen, je `normalized_result.references`):

```
Knx.Kim.Common            (Basis: Rfc3987.Iri, TaggedString.LanguageTaggedString, ProgressReporting, Functions)
  └ Knx.Kim.Vocabularies  (Präfix-/IRI-Klassen, DynamicVocabulary, Predefined.Vocabularies)
      └ Knx.Kim.Owl2Reasoner   (+ dotNetRdf, + KimContentProvider)   OWL2-Strukturmodell + Reasoner
          └ Knx.Kim.KimParsing (KimParser, OntologyLoader, Extractor)
              ├ Knx.Kim.Kim            (generierte KIM-Klassen: En5009062.*, Ontology.Knx, Schema.Qudt, Vcard)
              └ Knx.Kim.KimClassesCore (RuntimeKimClassManager, Templates, RuntimeAnnotations)
                  ├ Knx.Kim.KimGenerator (Scriban-Codegen aus der Ontologie)
                  └ Knx.Kim.KimWriters   (KimWriter<T>, Traversal, Turtle-Visitor)
                      ├ Knx.Kim.Ext         (KimBroker, KimTemplateBroker, SemanticAnnotationSerializer)
                      └ Knx.Kim.DataProvider(KimDataProvider, KnxInformationModel, ApplicationFunctions.*)
```

### 4.2 Pipeline parse → model → generate → write (belegt)

1. **Content**: `KimContentProvider` (5,26 MB) bettet `knx_v1.ttl` und `original_schema.ttl` ein; API `KimContentProvider.Default`, `.OntologyContent` (EV-SE-STATE Member-Refs; EV-KIMRES).
2. **Parse (RDF)**: `Knx.Kim.OWL2Reasoner.Parsing` — `Load`, `OntologyCache`, `OWLClassDeclarations`/`OWLObjectPropertyDeclarations`/…, `OWLAxiomParsers`, `RDFConsumer`, `Parsing.Adapters.DotnetRdf` (EV-KIM-OWL).
3. **Parse (KIM)**: `Knx.Kim.KimParsing.KimParser`, `OntologyLoader`, `OntologyReasoningHelper`, `SuperClassReasoner`, `InformationExtractors.*` (`TypeExtractor`, `Cardinality`, `NamingExtractor`, `TypeRangeExtractor`, `DefaultValue`, `AnonymousTemplate`), Modell `ParsedOwlModel.{Classes,Properties,Constraints,Annotations}` (`ParsedKimClass`, `ParsedTemplateInstance`, `CardinalityConstraint<T,TC>`, `ObjectPropertyTypeConstraint`, `LiteralOneOfConstraint`, `AnonymousTemplateConstraint`, Annotationstypen `StringAnnotation`, `TaggedStringAnnotation`, `BoolAnnotation`, `DateTimeAnnotation`, …) (EV-KIM-PARSE).
4. **Model/Runtime**: `KimClassesCore.RuntimeKimClassManager.CreateFromOntology`, `.GetInstances`, `.GetTemplates`; `RuntimeKimClass(WithTemplates)`, `RuntimeKimProperty`, `GranularCardinalityConstraint`, `KimTemplate.Bake()`, `Templates.KimTemplate<,>`, `KimTemplateCategory`, `RuntimeAnnotations.*`, `Abstract.KimThing` (`Iri`), `Exceptions.Baking*` (EV-KIM-CORE).
5. **Generate**: `KimGenerator.KimClassGenerator` + `KimGeneratorRootPath`, `Config.{NamespaceConfig, IriConfig, AdditionalPropertiesConfig}`, `CodeGeneration.CodeGenerator` + `ScribanTemplateDiskLoader` + Modelle `RealClassTemplate`, `GeneratedUnionClassTemplate`, `PropertyType`/`CardinalityType`, native Templates (`Bool`, `String`, `Int`, `UInt`, `Long`?, `Float`, `Double`, `DateTime`, `LangString`, `Object`), `And`/`Or`-Merges (EV-KIM-GEN) — d. h. der C#-Code in `Knx.Kim.Kim.dll` wird per Scriban aus der TTL erzeugt.
6. **Write**: `KimWriter<T>` mit `AddToGraph(...)`, `Traversal.KimInstanceTraversal<T>`, `Visitors.ToTurtleInstancePropertyVisitor<T>` (EV-KIM-WRITE) — Instanzen werden per Traversal in einen dotNetRdf-Graph geschrieben.

Serialisierungsformate (EV-KIM-REASON): `RDFFileFormat` = `JsonLd`, `RdfXml`, `Turtle`.

### 4.3 Generierte KIM-Klassen (EV-KIM-KIM, `Knx.Kim.Kim.dll`)

Namespaces mit Typzahlen: `En5009062.Core` (95), `En5009062.Tag` (90), `En5009062.Loc` (45), `Ontology.Knx` (20), `Schema.Qudt` (5), `Vcard.Ns` (5), `Unions` (6), `Knx.Kim.Kim.Predefined` (12) + `.Predefined.Owl` (6), `Knx.Kim.Kim.AttributeAnnotations` (`IriAttribute`, `LiteralTypeAttribute`, `OrderAttribute`).

Je Entität drei Generationen: `I<X>`, `Immutable<X>`, `Mutable<X>`, plus `IMutable<X>` (Baking-Muster). Auszug:

| Namespace | Entitäten |
|---|---|
| `En5009062.Core` | `Thing`, `Asset`, `Equipment`, `Cabinet`, `Device`, `Gateway`, `Installation`, `Location`, `Product`, `Software`, `Point` → `Datapoint`, `Actionpoint`, `Eventpoint`, `FunctionalBlock`, `Functionality`, `ApplicationFunction`, `ApplicationProgram`, `Aspect` (jeweils als `I…`/`Immutable…`/`Mutable…`) |
| `En5009062.Loc` | `Site`, `SiteSegment`, `Building`, `Floor`, `Space`, `Room`, `RoomSegment`, `Outside`, `Location` |
| `En5009062.Tag` | `Tag`, `TagSet`, `Trade`, `Locality`, `LocationUsage`, `EquipmentType`, `EquipmentQualityKind`, `OperationKind`, `Origin`, `PhenomenonType`, `PhenomenonQualityKind`, `PointFunctionType`, `PointInterface`, `PointOperation`, `Protocol`, `QualityKind`, `QuantityKind`, `StateType` |
| `Ontology.Knx` | `Channel`, `DatapointType`, `DatapointTypeField`, `FunctionPoint` |
| `Schema.Qudt` / `Vcard.Ns` / `Unions` | `Unit` / `Address` / typisierte Union-Interfaces (`IEquipmentTypeOrILocalityOrIPhenomenonTypeOrIQuantityKind`, `IOperationKindOrIQualityKind`) |

### 4.4 Owl2Reasoner — Angebot und Reasoning-Charakter

Modell (EV-KIM-OWL): `OWLClass`, `OWLObjectProperty`, `OWLDataProperty`, `OWLAnnotationProperty`, `OWLDatatype`, `OWLNamedIndividual`, `OWLLiteral`; Axiome `OWLSubClassOfAxiom`, `OWLEquivalentClassesAxiom`, `OWLClassAssertionAxiom`, `OWLObject/DataPropertyAssertionAxiom`, Domain/Range/SubProperty, `OWLObjectIntersectionOf`/`UnionOf`, `OWLObject/DataSomeValuesFrom`, `AllValuesFrom`, `HasValue`, `OneOf`, `Min/Max/ExactCardinality`; `IOWLOntology`, `IOWLOntologyManager`, `OWLOntologyIdentity`.

Reasoner-API (`BasicKimLibReasoner`, EV-KIM-REASON) — vollständige Liste:

```
ReasonerName
GetSubClasses(OWLClass, bool direct)        : IReadOnlyCollection<OWLClass>
GetSuperClasses(OWLClass, bool direct)
GetEquivalentClasses(OWLClass)
GetInstances(OWLClass, bool direct)
GetSubObjectProperties / GetSuperObjectProperties
GetSubDataProperties  / GetSuperDataProperties
GetTypesForNamedIndividual(OWLNamedIndividual, bool direct)
IsSubtype(a, b) : bool
```

Unterstützend: `PartialReasoning.{IOWLClassReasoner, IOWLObjectPropertyReasoner, IOWLDataPropertyReasoner, IOWLIndividualReasoner, IOWLDatatypeReasoner}`, `ExplicitClassHierarchyProvider`, `ExplicitObjectPropertyHierarchyProvider`, `ExplicitDataPropertyHierarchyProvider`, `KimOntology` mit festen Zugriffen `Thing`, `Location`, `Site`, `Tag`, `Trade`, `Aspect`, `Datapoint`, `DatapointType`, `Device`, `ApplicationFunction`, `QuantityKind`, `TypedPoint`.

⇒ **Reasoning-Charakter: keine Regel-Engine, kein RDFS- oder OWL-RL-Closure.** Es ist ein *explizites Hierarchie-/Assertion-Reasoning*: transitiv aufgelöste Sub-/Superklassen und -properties aus den expliziten `rdfs:subClassOf`/`owl:subPropertyOf`-Axiomen, Äquivalenzklassen, Instanzen je Klasse, Typen je Individuum. Die OWL-Restriktionen (Kardinalität, someValuesFrom, oneOf) werden **geparst** und für die Codegenerierung/Constraint-Prüfung (`KimParsing.Constraints`) ausgewertet, aber nicht als Inferenzregeln geschlossen.

### 4.5 Vokabulare und IRIs

`Knx.Kim.Vocabularies` (EV-KIM-VOCAB) — Klassen: `Predefined.Vocabularies` (mit statischem `Iri`), `DynamicVocabulary`, `DynamicVocabularyBuilder`, `Utilities`, und je ein Typ pro Vokabular: `KNXv1`, `KNXv1Loc`, `KNXv2`, `KNXv2Core`, `KNXv2Loc`, `KNXv2Mac`, `KNXv2Tag`, `KNXMTCT`, `DCTerms`, `HCTL`, `JsonSchema`, `OWL2`, `QUDT`, `QUDTQuantity`, `QUDTUnit`, `RDF`, `RDFS`, `SchemaOrg`, `ThingDescription`, `XSD`.

Die IRI-Strings selbst sind nicht lesbar (String-Heap verschlüsselt; `user_strings` enthält nur Obfuskator-Stubs, EV-KIM-VOCAB). Die IRIs ergeben sich aus `ontology.ttl` (EV-TTL, Z. 1–16):

| Präfix | IRI | im Code als |
|---|---|---|
| `knx:` | `http://schema.knx.org/2020/ontology/knx#` | `KNXv1` + `Ontology.Knx.*` |
| `core:` | `http://schema.knx.org/2023/en50090-6-2/core#` | `KNXv2Core` + `En5009062.Core.*` |
| `loc:` | `http://schema.knx.org/2023/en50090-6-2/loc#` | `KNXv2Loc` + `En5009062.Loc.*` |
| `tag:` | `http://schema.knx.org/2023/en50090-6-2/tag#` | `KNXv2Tag` + `En5009062.Tag.*` |
| `mac:` | `http://schema.knx.org/2020/ontology/mac#` | `KNXv2Mac` |
| `owl:`, `rdf:`, `rdfs:`, `xsd:`, `qudt:`, `quantitykind:`, `unit:`, `vcard:`, `dct:`, `jsonschema:`, `http:` | Standard | `OWL2`, `RDF`, `RDFS`, `XSD`, `QUDT`, `QUDTQuantity`, `QUDTUnit`, `HCTL`(http), `DCTerms`, `JsonSchema`, `ThingDescription` |

`KNXMTCT` = KNX Micro/Template-… (nicht auflösbar; im `ontology.ttl` nicht als Präfix vorhanden) → Inferenz.

### 4.6 Zusatz-APIs

- `KimDataProvider.Load(IOntologyStreamProvider, KimDataProviderSettings)` → `RawOntology` (IGraph), `ParsedOntology`, `Model`, `KimUtilities`, `Header`, `SemanticAnnotationSerializer`; `IKimDataProvider` zusätzlich `GetAllLocationUsages`, `GetLocationUsageOrDefault`, `GetAllApplicationFunctionBlueprints`, `GetAllApplicationFunctionExtensions`, `GetAllKimFunctionPoints`, `GetAllApplicationFunctionPresets`, `StoreApplicationFunctionPreset`, `GetAllFunctionalBlocks` (EV-KIM-DP — „DPA“ = Datapoint Assignment).
- `KimDataProviderSettings`: `ApplicationFunctionLibraryPath`, `ApplicationFunctionLibrarySchemaPath` (JSON-Schema + Bibliothek), `Default`, `TestEnvironment`.
- `Knx.Kim.Ext.KimBroker`: `Associate(Identifier, Thing)`, `Associate(Identifier, Datapoint)`, `CreateDatapointBuilder`, `ToTurtle(Stream)`, `LoadTurtle(Stream)`, `BrokerIri`, `IdentifierThingMapping`; `KimTemplateBroker.Associate(Identifier, KimTemplate)`, `Write`, `Load`.
- `Knx.Kim.Ext.Serialization.SemanticAnnotationSerializer`: `Serialize(IEnumerable<Thing>)`, `Serialize(IEnumerable<…Entity>)`, `Serialize(IEnumerable<Iri>)`, `SerializeHeader()`, `GetHeaderOrDefault(string)`, `Deserialize(header, string)`, `DeserializeEntities`, `TryDeserializeEntities`; Header-Typen `ISemanticAnnotationHeader` / `SemanticAnnotationHeaderV2`, `IncompatibleSerializationFormatException` (EV-KIM-EXT). ⇒ Semantik-Annotationen werden als versionierter, header-gebundener String im Projekt gespeichert.
- `KimUtilities.GetAllDpas()`, `GetAllFunctionalBlocks()`, `GetInstancesOfType<T>()`, `ObtainCommentFor`, `ObtainLabelFor`, `Filter`.

### 4.7 Konsequenz für unsere Implementierung

1. Nicht die C#-Klassen aus `Knx.Kim.Kim.dll` nachbauen, sondern **Ontologie-zentriert** arbeiten: TTL laden → Klassen/Properties/Kardinalitäten extrahieren → Instanzen als RDF schreiben. Der Original-Code generiert sich die Klassen per Scriban aus derselben TTL; wir können direkt mit generischen Knoten + Vokabular-Konstanten arbeiten.
2. Nur explizites Hierarchie-Reasoning implementieren (Sub-/Superklasse, Sub-Property, Äquivalenz, Instanzen, Typen). Kein OWL-RL-Closure — sonst weichen Ergebnisgraphen vom Original ab.
3. Präfixe/IRIs zentral als Konstanten führen (Tabelle 4.5), `knx:` bleibt 2020er-Namespace, `core:`/`loc:`/`tag:` sind 2023er.
4. Annotationen (`IriAttribute`, `LiteralTypeAttribute`, `OrderAttribute`) und Templates (`KimTemplate.Bake`) als Konzepte übernehmen: Instanz + Template + Kardinalitätsconstraint statt starrer C#-Klassen.
5. `RDFFileFormat` = {Turtle, RdfXml, JsonLd} als Enum vorsehen, auch wenn der Export nur Turtle + JsonLd nutzt.

---

## 5. Inferenz — was aus den Artefakten **nicht** beweisbar ist

1. **Dateinamen und Endungen des Semantic Export** (z. B. `<Projekt>.ttl`, `<Projekt>.jsonld`) sind nicht belegt; alle String-Literale sind verschlüsselt. Belegt sind nur: zwei Serialisierungen (Turtle/JSON-LD), Wizard-Titel „Exporting Turtle File…“, Menüeinträge „Semantic Turtle Export“ / „Semantic JSON-LD Export“, Zielordner-Auswahl durch den Nutzer.
2. **Ob der JSON-LD-Graph die Ontologie enthält**, ist nicht direkt belegt. Belegt ist `Merge` nur im Turtle-Pfad; im JSON-LD-Pfad wird ein `TripleStore` befüllt (`BaseTripleStore.Add`). Vermutung: derselbe gemergte Graph.
3. **IRI-Schema** (`ProjectNameSpace`, `GenerateId`/`GenerateIri`) ist nur als API belegt, nicht als Zeichenkette (kein Präfix-/Formatstring lesbar). Ob ein `<ProjectGuid>`-basiertes `urn:uuid:` oder eine `https://…/<guid>#`-Form verwendet wird, ist offen.
4. **„Page“-Begriff**: Es gibt keinen Typ und kein Feld „Page“ im Export. Der Export ist ein Projektgraph; die frühere Annahme „Seiten/Teilgraphen“ ist durch die Artefakte **nicht** gedeckt.
5. **Label-Geometrie in mm**: Es gibt Zeilen/Spalten/Anzahl und Rand-Felder in der UI, aber keine Einheit und keine mm-Felder im Code. Die zwei `int`-Parameter sind der Position nach Zeilen/Spalten (Reihenfolge nicht bewiesen).
6. **Label-Datenquelleformat**: `File.WriteAllText` schreibt irgendeine Textdatei (CSV/TXT), Trennzeichen/Header nicht lesbar. `LabelSanitizer.Sanitize` ist die einzige öffentliche Methode; die Ersetzungsregeln sind nicht einsehbar.
7. **Das 10. String-Feld in `Label`** (neben den 9 Properties) ist nicht identifizierbar; ein zusätzliches Etikettfeld (z. B. Raumnummer/Etage) ist plausibel, aber unbewiesen.
8. **XML-Schema der Aufnahmedatei**: Root-Element, Attribut- vs. Elementnamen, Zeitstempelformat im XML, Versionsangabe und Secure-Felder sind nicht belegt. Belegt sind nur der Schreiber/Leser (`XmlFileWriter`/`XmlFileReader`), die Endung `.xml` und das Event-/Property-Modell.
9. **CSV-Spaltenreihenfolge** und `CsvFileWriter.DefaultSeparator` (Zeichen) sind nicht lesbar; nur die 13 Spaltennamen.
10. **Ob die Aufnahme bereits beim Start in die Zieldatei schreibt oder erst nach „Save“** (`StartIncrementalWriter` vs. `WriteEventsAsync`) ist offen; beide APIs existieren.
11. **Reasoning-Flavour**: „kein OWL-RL/RDFS-Closure“ ist aus der API abgeleitet (nur Hierarchie-/Assertion-Abfragen, `Explicit*HierarchyProvider`, `PartialReasoning`), nicht aus einem expliziten Statement. Ein internes Closure-Verfahren kann nicht ausgeschlossen werden.
12. **`KNXMTCT`-Vokabular-IRI** ist nicht belegt (nicht in `ontology.ttl`).
13. **`kim_v1` vs. `original_schema` vs. `ontology.ttl`**: Welche der drei Fassungen ETS zur Laufzeit für den Export nimmt, ist nur über `CurrentSemanticsInformation.CurrentETSSemanticsVersionOntologyFile` (→ `ontology.ttl`) bzw. `KimContentProvider.Default` (→ eingebettete TTLs) indiziert; die Auflösungsreihenfolge ist nicht belegt.
14. **Ob `KimReverseCheck` im Export oder nur im Import/Assistenten läuft**, ist nicht belegt (nur Typ + Methode vorhanden).

---

## 6. Querschnitt: Ontologie-Gegenprobe (EV-TTL)

`ontology.ttl`: 55911 Zeilen, 16 `@prefix`, 2480 `a owl:Class`. Belegte Klassenzeilen: `knx:Channel` 56401, `knx:FunctionPoint` 58946, `knx:DatapointType` 59366, `knx:DatapointTypeField` 59456, `core:Thing` 57817, `core:Device` 57907, `core:Installation` 53847, `core:Datapoint` 59233, `core:Functionality` 57654, `core:ApplicationFunction` 58385, `loc:Site` 57082, `loc:Building` 57985, `loc:Room` 58003, `loc:Space` 58107, `loc:Floor` 58258, `tag:Trade` 58548, `tag:LocationUsage` 58802, `mac:Building` 14445, `mac:Floor` 14454, `mac:Room` 14463, `mac:Site` 14472.

Damit sind die im Code sichtbaren `En5009062.{Core,Loc,Tag}`- und `Ontology.Knx`-Namen vollständig gegen die ausgelieferte Ontologie gedeckt; `Ontology.Knx.MutableChannel` ↔ `knx:Channel`, `En5009062.Loc.MutableRoom` ↔ `loc:Room` usw.

„Stairway“/„Corridor“ existieren **nicht** als Klassen — nur als Beschreibungstext von `loc:Space` (Z. 54133/54135). Die Mapper `StairwayMapper`/`CorridorMapper` bilden ETS-Gebäudeteil-Typen also auf bestehende KIM-Klassen ab (vermutlich `loc:Space`/`loc:RoomSegment`) — nicht bewiesen.
