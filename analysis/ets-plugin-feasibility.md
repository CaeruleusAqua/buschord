# ETS-Apps/DCA in einem unabhängigen Tool: Vertrag und Machbarkeit

Stand: 2026-10-18 · Basis `77c78f9` · Methode: statische REA-Analyse, **keine** Ausführung von ETS- oder
Plug-in-Assemblies, kein Buszugriff. Aussagen ohne Belegmarke sind Inferenz und als solche gekennzeichnet.

## Kurzurteil

**Nicht machbar als Host.** Eine DCA/App ist kein austauschbares UI-Modul, sondern Code, der gegen das
ETS-eigene .NET-Framework-4.8-x86-WPF-Objektmodell gebunden ist (`Knx.Ets.Sdk`,
`Knx.Ets.ObjectModel`, `Knx.Ets.HawkNET`, WPF, teils `System.AddIn`). Sie erhält bei `Initialize` ein
ETS-`IInitializationContext` mit Projekt, Gerät, Parameter-Instanz-Referenzen und Bus-Management und
liefert ein WPF-`Window`/`FrameworkElement` zurück. BusChord (Avalonia 11, `net9.0`) kann weder WPF
in-process hosten noch den ETS-Objektgraphen erzeugen, ohne ETS-Interna nachzubauen und ETS-DLLs unter
einer ETS-Lizenz zu verwenden. Realistisch erreichbar ist **Erkennen, Anzeigen und Übergeben**
(Option A/B), nicht Ausführen.

## Begriffe (Quelle: KNX-Help-Center, abgerufen 2026-10-18)

| Begriff | Definition | Für uns relevant |
|---|---|---|
| App | seit ETS4, ergänzt ETS projektweit (nicht gerätespezifisch), nur bei geöffnetem Projekt nutzbar | hostet eigene UI, nicht gerätegebunden |
| DCA | seit ETS5, gerätespezifische App, **nur Typ „User Interface“**, eingebettet als zusätzlicher Tab neben Parametern/Gruppenobjekten; Mehrfachbearbeitung mehrerer Geräte möglich | das ist der „Plug-in-Dialog“ |
| Plug-in | seit ETS2, gerätespezifisch, **nicht** eingebettet, Typen UI / Download (Hawk) / Data Handler | Altbestand, weiterhin in `plugins.json` |

Lizenz (wörtlich): „Each individual ETS App requires a license … ETS Apps can be installed without license,
but in this case they cannot be invoked.“ und „DCAs are however always free of charge.“
Ebenso: „ETS6 Demo does not support App installation.“
Quellen: `support.knx.org/hc/en-us/articles/360000047899` (App principles),
`…/360000228850` (About Apps, DCAs and Plug-ins), KNX-FAQ zu ETS6.

## Der Vertrag, wie er im Artefakt steht

### 1. UI-/DCA-Schnittstellen (`Knx.Ets.Sdk.AddIns.AddInViews.dll`, 53 Typen)

Beobachtet (Member-Evidenz `ev_0447f422…`, Artefakt `ev_98728558…`, SHA-256 `5a59b733…`):

- `IDeviceConfigurationAddIn.GetDcaUi(…)` → WPF-Rückgabetyp, Parameter ist eine generische
  Geräte-Collection (TypeRef-Namen sind ohne CLR nicht auflösbar; die Assembly referenziert ausschließlich
  `PresentationFramework`/`PresentationCore` und `Knx.Ets.Sdk`).
- `IDeviceConfigurationLifeCycleEvent.OnDeviceAffectingOperationIsStarting(Reason, Geräte-Collection)` —
  Host ruft die DCA vor geräteändernden Operationen.
- `IEts4AddIn` (Alt-Vertrag, weiterhin implementiert): `AppId`, `Configuration`, `UserConfiguration`,
  `GetAddInUI`, `Initialize(IInitializationContext)`, `Ets4SelectionChanged`, `ToolbarItemClick`,
  `GetSidebarControl`, `OnProjectOpened`/`OnProjectClosing`, `ShowConfigurationDialog`.
- `IEts4AddInV2.GetSidebarProperties`, `IEts5ClosableAddin.OnPanelClosing`,
  `IEditAddInConfiguration.ShowConfigurationDialog`, `IDialogService.ShowMessageBox`/`ShowModal`,
  `IHostNotification` (Statuszeile, Selektion, Toolbar), `IMonitorAddIn.InspectTelegram`,
  `IMonitorAddInSpecifyAdditionalColumns` + `ColumnDescriptor`, `ITelegram` (Monitorzeile),
  Toolbar-Typen `IAddInToolbarItem`/`Button`/`ToggleButton`/`MenuItem`.

Ein reales DCA-Gegenbeispiel (installierte MDT-App, `MDT_Smart_Taster.dll` 4.5.1.0, Member-Evidenz
`ev_d52c549b…`) implementiert `MyEtsApp` **den kompletten Satz** `IEts4AddIn` + `IDeviceConfigurationAddIn`
(`GetDcaUi`, `GetAddInUI`, `Initialize`, `OnProjectOpened/Closing`, `ShowConfigurationDialog`,
`GetSidebarControl`, `GetSidebarProperties`) und liefert ein WPF-Fenster (`UserInterface : Window`,
`InitializeComponent`, `IComponentConnector.Connect`). Assembly-Referenzen: `Knx.Ets.Sdk 5.7.554`,
`Knx.Ets.Sdk.AddIns.AddInViews 5.7.554`, `PresentationFramework`, `System.AddIn 4.0.0.0`
(Managed Add-in Framework, nur .NET Framework), `System.Windows.Forms`. Die Klasse trägt
`System.AddIn.AddInAttribute`. Kein Bytecode wurde ausgeführt.

### 2. Plug-in-Basisklassen (`Knx.Ets.Sdk.dll`, Namespace `Knx.Ets.Sdk.PlugIn`)

Beobachtet (`ev_c5e07351…`), Auszug:

| Typ | Kernmitglieder |
|---|---|
| `UiParameterPluginBase` | `get_Device`, `CreateDeviceManagement(bool)`, `abstract CreatePluginWindow()`, `abstract get_ContextMenuItems`, `abstract InvokeContextMenuItem(string)`, `Dispose` |
| `DownloadPluginBase` | `get_Device`, `get_ApplicationProgram`, `get_ProgressTexts`, `FormatDeviceInfo`, `CompareDevice`, `UnloadDevice`, `SpecifyNextImageRead`, `ReconstructDevice`, `CreateDeviceManagement` |
| `SinglePassDownloadPluginBase` | `GetPreDownloadProcedure`, `CreateImage` |
| `MultiPassDownloadPluginBase` | `OnError`, `NextPass`, `get_CurrentPass`, `CreateImage` |
| `DataHandlerPluginBase` | `OnDeviceAdded/Copied`, `OnAddConnector`, `OnGroupAddressChanged`, `CanAddConnector`, `TransferParameters`, `CheckDevice`, `get_DeviceReportParameters` |
| `ParameterTypePlugInBase` | `get_Device`, `get_ParameterInstanceRef`, `OnBeforeApplicationShutdown` |
| `DownloadPlugInInfoAttribute` / `UiPlugInInfoAttribute` | `PlugInCapabilities` (Flags) |
| `DownloadPlugInCapabilities` | `CompareDevice`, `DeviceInfo`, `Download`, `Unload`, `Reconstruction` |
| `UiPlugInCapabilities` | `ParameterDialog`, `ContextMenu` |

Die Basen sind stark an den Host gekoppelt: Member-Refs zeigen auf `System.Windows.Window.ShowDialog`,
`Window.set_Owner`, `Application.get_MainWindow`, `UserControl..ctor`, `Dispatcher.Invoke`,
`Knx.Ets.ObjectModel.*`, `Knx.Ets.Hawk.*`, `Knx.Ets.CalculatedParameters.ScriptingEngine` und Autofac.

### 3. Was eine DCA bekommt (Facaden in `Knx.Ets.Sdk`, `ev_b01e8795…`)

- `Knx.Ets.Sdk.Root` („SdkContext“): `Projects`, `Manufacturers`, `KnxMasterData`, `CreateDeviceManagement`,
  `CreateAsyncKnxGroupCommunication`, `StartDownload`, `StartFirmwareUpdate`, `ReadDeviceInfo`,
  `NavigateToDevice`/`…ParameterDialog`, `ExportProject`, `BackupDatabase`, `ImportProductData`,
  `get_LicenseDeviceLimit`.
- `Project.Project`: `Installations`, `AddinData`, `UndoManager`, `ProjectPassword`, `GroupAddressStyle`.
- `Project.Device`: `ParameterInstanceRefs`, `ModuleInstances`, `ComObjectInstanceRefs`,
  `ActiveParameterInstanceRefs`, `Address`, `Line`/`Segment`, Security-Felder, `get_HasParameterDialog`,
  `get_HasEtsLegacyPlugins`.
- `Project.ParameterInstanceRef`: `get_Value`/`set_Value`, `ParameterRef`, `IsActive`, `IsDefault`.
- `Product.ParameterRef`: `Parameter`, `DefaultValue`, `Value`, `Access`, `Semantics`.
- `Project.AddinData`: `AddinId`, `Name`, `Data` (byte[]) — DCAs dürfen eigene Daten im Projekt ablegen.
- `Network.DeviceManagement`: vollständiger Buszugriff über den Host (Memory/Property/Resource lesen und
  schreiben, `Restart`, `MasterReset`, CoAP) — die DCA spricht **nie** selbst mit dem Bus, sie benutzt den
  Host. Genau diese Objekte kann ein Fremd-Host nicht bereitstellen.

### 4. Verpackung und Registrierung

**Im Produktdatensatz (`.knxprod`)** — aus dem in `Knx.Ets.Xml.ObjectModel.dll` eingebetteten Schema
(beobachtete XSD-Fragmente):

- `ApplicationProgram/Static/Extension` mit `EtsDownloadPlugin`, `EtsUiPlugin`, `EtsDataHandler`,
  `EtsDataHandlerCapabilities`, `RequiresExternalSoftware` und `Baggage RefId=…`.
- `ParameterRef` hat ein Attribut `Plugin` (Parameter, dessen Editor ein Plug-in stellt →
  `ParameterTypePlugInBase`).
- `Manufacturer/Baggages/Baggage` = Datei im Produktarchiv: `Id`, `Name`, `TargetPath`,
  `FileIntegrity`, `FileInfo@Version/TimeInfo` — so reisen Plug-in-DLLs im Produkt mit.
- `AddinData_t` im Projekt-XML; Fehlercodes `InvalidPluginReference`, Regel
  „Checks correct form of plugin references“.

**Installierte App** (`C:\ProgramData\KNX\ETS6\Apps\AddIns\<AppId>\`) — beobachtet:

- `AddInManifest.xml`: `<Knxapp AppId="M0083-A0041" Icon=… AddInAssembly="MDT_Smart_Taster.dll">` mit
  `<Configuration Is64BitCompatible IsSingleInstanceAddIn HasConfigurationDialog AddToContentSwitcher
  AddToMainToolbar>`, `<DisplayNames>`, `<AddInToolbarButtons>`, `<HelpFiles>`,
  `<DeviceConfiguration><ApplicationProgram Id="M-0083_A-008B-10-3E58"/>…` (Liste der bedienten
  Applikationsprogramme).
- `_signature` (175 Byte, Base64 mit BOM) je App-Ordner und `<AppId>.signature` daneben; zusätzlich
  `Knx.Ets.XmlSigning.dll`, `Knx.AppCertification.Signing.dll`, `PluginInstaller.exe`.
- `Knx.Ets.PluginRepository.dll` modelliert das: `PluginType {Unknown, DataHandler, Download, UserInterface}`,
  `PluginInterfaces` (19 Flags: `HasParameterDialog`, `HasPropertyPages`, `HasContextMenuItems`,
  `HandlesAddDeleteDevice`, `ImplementsTransferParameters`, `ImplementsProjectCheck`,
  `ImplementsSinglePassDownload`, `ImplementsMultiPassDownload`, `ImplementsDeviceUpload`,
  `ImplementsDeviceCompare`, `ImplementsDeviceUnload`, `ImplementsDeviceReconstruction`, …),
  `PluginKey` (`IsComPlugin`/`IsDotnetPlugin`), `BaggageFileType` (u. a. `UnmanagedEts3PluginDll`,
  `ManagedEts3PluginDll`, `Ets4PluginDll`, `InstallerPackage`).
- `plugins.json` (ETS-Installation, 770 KB): 235 Einträge; 222 mit Typ (Download 58, UserInterface 87,
  DataHandler 26, Kombinationen), 73 × `HasParameterDialog`, 40 × MultiPass, 18 × SinglePass;
  13 Einträge ohne Typ = entfernte/konvertierte Plug-ins.

### 5. Lifecycle (beobachtet, soweit aus Signaturen ableitbar)

1. Host liest `AddInManifest.xml`, prüft Signatur, mappt `ApplicationProgram`-IDs auf Geräte.
2. Lädt die Assembly, findet die `[AddIn]`-Klasse, erzeugt sie (parameterloser Konstruktor) und ruft
   `Initialize(IInitializationContext)` mit `Project`, `Resources`, `Configuration`, `UserConfiguration`,
   `HostNotification`, `DialogService`.
3. Bei Geräteauswahl/Öffnen: `Ets4SelectionChanged(...)`, `GetDcaUi(<Geräte>)` → WPF-Element, das der Host
   als Tab einbettet; optional `GetSidebarControl`, Toolbar-Items, Kontextmenü.
4. Vor geräteändernden Aktionen: `IDeviceConfigurationLifeCycleEvent.OnDeviceAffectingOperationIsStarting`.
5. Beim Schließen: `IEts5ClosableAddin.OnPanelClosing` → `bool`, `OnProjectClosing`, `Dispose`.
6. Alt-Pfad Plug-ins: `UiParameterPluginBase.CreatePluginWindow()` (WPF `Window`), Download-Pfad über
   `DownloadPluginBase`/Hawk, Datenpfad über `DataHandlerPluginBase`.

Reihenfolge und Fehlerfälle sind aus Signaturen **nicht** vollständig belegbar; sie sind hier aus
Mitgliedsnamen und Help-Text abgeleitet (Inferenz).

## Warum das in BusChord nicht hostbar ist

1. **Framework-Bruch.** Alles ist .NET Framework 4.8, x86, WPF (`PresentationFramework`,
   `System.Windows.Window`, `UserControl`, `Dispatcher`). BusChord ist Avalonia 11 auf `net9.0`
   (`src/*/*.csproj`). WPF läuft nicht auf .NET 9; WPF-Fenster lassen sich nicht in Avalonia einbetten.
2. **Kein loser Vertrag.** Die DCA erwartet kein serialisiertes Modell, sondern **lebende Host-Objekte**
   (`IInitializationContext.Project`, `Device`, `ParameterInstanceRef`, `Root` mit
   `CreateDeviceManagement`). Ohne ETS-Objektmodell gibt es nichts zu übergeben.
3. **Signierte, intern verschachtelte Assemblies.** `Knx.Ets.Sdk` ist strong-named (Token
   `61439873ec5e1159`), referenziert `Knx.Ets.ObjectModel`, `Knx.Ets.HawkNET`, `Knx.Ets.Xml.ObjectModel`,
   `Knx.Ets.CalculatedParameters`, Autofac; die SDK-Typen sind Dotfuscator-behandelt. Ein Nachbau der
   Facaden wäre ein Nachbau von ETS — nicht ein Host.
4. **Prozess/MAF.** Die MDT-DCA bindet `System.AddIn` (MAF), das es nur im .NET Framework gibt und das
   Microsoft abgekündigt hat; `Is64BitCompatible` im Manifest zeigt, dass der Host die Bittigkeit steuert.
   Ein out-of-process-Embedding (Fenster in Avalonia) ist plattformseitig nicht vorgesehen (UI-Thread,
   `Owner`, Dispatcher, Modals).
5. **Lizenz.** Auch technisch gelöst bliebe: ETS-Apps dürfen nur mit gültiger Lizenz ausgeführt werden
   (Help oben); die ETS-Binaries dürften nicht mitgeliefert und ihr Objektmodell nicht als Ersatz-Host
   verwendet werden. Ein „DCA-Runner“ ohne ETS-Lizenz wäre genau die Umgehung, die ausgeschlossen ist.

## Rechtliche Grenze (klar benannt)

- DCAs sind gratis, **aber** sie laufen ausschließlich in einer lizenzierten ETS-Installation; ETS6 Demo
  unterstützt keine App-Installation.
- Die ETS-SDK-Nutzung ist an KNX-Mitgliedschaft/NDA und die ETS-Lizenz gebunden. Weder SDK-Assemblies noch
  Plug-in-DLLs dürfen in einem eigenen Produkt gebündelt, umverpackt oder als Host-Bestandteil
  weitergegeben werden.
- BusChord darf daher: Plug-in-Metadaten **lesen** (Manifest, `.knxprod`-Referenzen, `plugins.json`),
  darauf **hinweisen**, und den Nutzer an eine vorhandene ETS-Installation **übergeben**. BusChord darf
  nicht: ETS-/Plug-in-Code laden und ausführen, Lizenzen prüfen oder umgehen, ETS-DLLs mitliefern.

## Optionen

| # | Option | Was konkret | Aufwand | Risiko |
|---|---|---|---|---|
| **A** | **Erkennen & verweisen (empfohlen)** | Beim Gerät aus `.knxprod` `Extension@EtsUiPlugin`/`EtsDownloadPlugin`/`RequiresExternalSoftware` und `ParameterRef@Plugin` lesen; installierte Apps über `Apps\AddIns\*\AddInManifest.xml` + `plugins.json` indexieren (AppId, Typ, Capabilities, bediente `ApplicationProgram`-IDs). UI: Badge „DCA erforderlich (M-0083-A0041)“ + Liste der Fähigkeiten; Parameter des Geräts bleiben read-only sichtbar. | 3–4 PT | niedrig: reine Leseoperation, keine Ausführung. Grenze: keine DCA-UI, keine DCA-Validierung. |
| **B** | **Übergabe an ETS** | Zusätzlich zu A ein „In ETS öffnen“-Kommando. ETS6 hat einen Options-Parser `Knx.Ets.ViewModel.CommandLine.StartupOptions` mit `OpenProject`, `OpenApp`, `InstallApp`, `ImportProject`, `CreateProject`, `ProjectWizard`, `DeleteProject`, `ExistsProject`, `Password`, `Quit` (Member-Evidenz `ev_1f2876c0…`). Aufruf nur, wenn ETS installiert ist; Projekt vorher speichern. Exakte Schalter-Schreibweise ist string-verschlüsselt und nicht belegt → empirisch zu klären. | 2–3 PT (auf A) | mittel: undokumentierte CLI, Pfad-/Fokusprobleme, ETS-Lizenz nötig. Kein Start ohne ETS. |
| **C** | **Eigener DCA-Host** | .NET-4.8-x86-WPF-Host, der `Knx.Ets.ObjectModel`/`Sdk` lädt und einen `IInitializationContext` synthetisiert. | > 20 PT und **rechtlich blockiert** | hoch: signierte/obfuskierte Interna, undokumentierter Objektgraph, Anti-Tamper (`is tampered.` in `Knx.Ets.PluginRepository`), keine Lizenzgrundlage. **Nicht verfolgen.** |

Empfehlung: A jetzt, B nur falls Nutzer mit ETS arbeiten und der CLI-Aufruf verifiziert ist. C verwerfen.

## Was aus dieser Analyse für BusChord ohnehin wertvoll ist

- `ParameterRef@Plugin` markiert Parameter, deren Editor wir **nicht** nachbilden können — unser
  Parameter-Dialog sollte sie sichtbar, aber gesperrt darstellen statt falsch zu editieren.
- `RequiresExternalSoftware`/`ExternalSoftware` (in `plugins.json` 12 Einträge) bedeutet: das Gerät
  braucht zusätzlich Herstellersoftware. Das gehört als Warnung in den Gerätedetailbereich.
- `Project.AddinData` (byte[] je `AddinId`) muss beim knxproj-Roundtrip **erhalten** bleiben, sonst
  verlieren DCA-Nutzer ihre im Projekt gespeicherten Daten. Das ist eine konkrete Anforderung an
  `Knxproj.cs`, unabhängig von jeder Hosting-Frage.

## Evidenzregister

| Marke | Gegenstand | Evidence |
|---|---|---|
| SDK-ART | `Knx.Ets.Sdk.dll` 6.4.8718.0, net48, 793 TypeDefs / 8188 MethodDefs, 1770 CustomAttributes, SHA-256 `7d28a8d5…7d989` | `ev_1c2a4858c6d33942f2115f5ed0a5bc0daeaa6176f493d623845b79700aac3afe` |
| SDK-PLUGIN | `Knx.Ets.Sdk.PlugIn.*` (Basen, Attribute, Capabilities) | `ev_c5e073517c0f603d8ece90be0832989495aa4ea138b979339df051b5e3fe7228` |
| SDK-FACADE | `Sdk.Root`, `Project.Device/Project/ParameterInstanceRef/AddinData`, `Product.ParameterRef`, `Network.DeviceManagement` | `ev_b01e8795d22c5169f569363db4d0a282e873893ab911cdebe1cbe8337e57d401` |
| VIEWS-ART | `Knx.Ets.Sdk.AddIns.AddInViews.dll`, 53 Typen, SHA-256 `5a59b733…d87d` | `ev_98728558583c9a1976fc5984302f9589884054a8a832bbd64c458135eb6e10b3` |
| VIEWS-MEM | Interface-Signaturen DCA/AddIn | `ev_0447f422d6ad8643343ecc5883e20da83e2a325304e7219a51ab43055cb0963c` |
| REPO | `Knx.Ets.PluginRepository.dll` (PluginType/Interfaces/Key/Baggage) | `ev_fdffc8f0f876db6b5a49484218768482b2c4f62e46cf06ba7ae8413e74b43d4f` |
| LOADER | `Knx.Ets.PluginLoader.dll` (77 Typen, obfuskiert; `AppDomain`, `LoadFrom`) | `ev_8c3b5e78d8016f4c2e7b6803e3635f62c0d5357c46104d7ac83fb30803a4ef16` |
| DCA-MDT | `MDT_Smart_Taster.dll` 4.5.1.0 + Manifest (installierte DCA M0083-A0041) | `ev_d52c549ba1d2faaa86b9b67d98fffe11923d857e68781500719307d4844283fd` |
| CLI | `Knx.Ets.ViewModel.CommandLine.StartupOptions` | `ev_1f2876c05c8e64a29fc94eee8a1673778e9cdb7d30661bbf82729bd802f1b9cb` |
| PD | `Knx.Ets.UI.ParameterDialog.dll` Typpagine (20969 Zeilen) | `ev_22b1899d0bd61f82ed03f292a8434039e93383c335a0911577d35fcdf7d4902a` |

Grenzen: keine CLR-Auflösung von TypeRefs (Rückgabetypen erscheinen als `class:<row>`); keine
Decompilation; keine Laufzeitbeobachtung. Ein unselektierter `inspect_managed_members`-Aufruf für
`Knx.Ets.Sdk` und `Knx.Ets.UI.ParameterDialog` überschritt das 64-MiB-Repräsentationsbudget — daraus folgt
**keine** negative Aussage über nicht selektierte Typen. Einzelne String-Literale des SDK sind im Klartext
in der `#US`-Heap lesbar
(z. B. „Creating new Plugins is deprecated“, „Modal dialogs are deprecated in ETS6“, „ is tampered.“),
andere sind obfuskiert.
