# ETS 6 — Katalog, Produkte, Parameter, Plugins/DCA: REA-Analyse

Read-only-Untersuchung gegen die installierte ETS 6.4.8718.0
(`C:\Program Files (x86)\ETS6`). Keine Ausführung von ETS, keine GUI-Klicks,
kein Bus-/Netzwerkzugriff. Basis: `12fc904ee05089976eac93426141c6fdb348ffe1`.
Geändert wird ausschließlich diese Datei.

Statuslegende: **observed** = aus Metadaten/Call-Edges gelesen;
**derived** = aus mehreren Beobachtungen geschlossen; **inferred** = begründete
Annahme, nicht belegt. Tokens/Offsets sind build-lokal an SHA-256 + MVID gebunden.

## Artefakt-Identität

| Kürzel | Datei | SHA-256 | MVID | Typen | REA-Evidence |
|---|---|---|---|---|---|
| ETS | `ETS6C.exe` (x86, PE machine `0x014C`) | `3d490d0bfc3e555dac2864f73df761879494ad36877b41efe34cb915b8b52da6` | `237e7ec3-6a31-406a-9c4f-1b3927a31b34` | 167 | `ev_353dd7d4a66ad79b59903747d6ddf9a408157f24fa2f8976eace495c7b5946d9` |
| OM | `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` | `484ff6a5-f1b3-4f33-ad19-2dd22dc9ae04` | 4024 | `ev_f2a1befdb5c100bc51a00161598f36789fe1e764b36034ec9a339d23f0cd180d` |
| XML | `Knx.Ets.Xml.ObjectModel.dll` | `db6f21679d046eaef4909121a553af52fa377fa582552faca6ec0fed5f7f51e1` | `2bf71cd1-85bd-435a-a0ce-63293b9c8296` | 832 | `ev_33fb545cc0e025fe56720a77f53856455d35e556ff0e0732c5ee9027f756450e` |
| VM | `Knx.Ets.ViewModel.dll` | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` | `b557be5c-0e7f-4bc3-aa94-958fba394aba` | 5360 | `ev_8e5d5386a5e0468631e2d39bc82bb8b20fe9ee5d904b30515bb9336e94e4de7e` |
| PD | `Knx.Ets.UI.ParameterDialog.dll` | `54e28d224543e3c14d04dfd7359fc5552d7472959e695bb89aed1defbff440b0` | `a2c70dcc-9efa-4b9c-9331-7764dc60e63d` | 213 | `ev_d94d4e1a25d0728b75c556777581a8d64681540ce141d89aba63e1e38327cc59` |
| PDT | `Knx.Ets.UI.ParameterDialogTypes.dll` | `bab684d46051c8dbeddadcdf32a51142c0cffb087993dece28137f3754552ee2` | — | 107 | `ev_2630ab40c55643047bb428fc68b5552a13c094a3bdd545c776aa3d5f4b07ae5e` |
| SDK | `Knx.Ets.Sdk.dll` | `7d28a8d549dd25b08fe798b8e0ee072eb5d5916713808f7ceffa1c35eaf7d989` | `040b6d5f-d46e-4092-bf40-c3311c12902b` | 793 | `ev_ba22f3e8abb209c4809d1849feaabe49553082efe2919a59d4c451101248d756` |
| PR | `Knx.Ets.PluginRepository.dll` | `4133a276dd37f4f7e6c03e9942026322cfe0e16ce2d6ff79b4e28f609bb4c91d` | — | 68 | `ev_5c626d50780d9520ecc2ad17bc362a3110099c6f1fa40d7e5afe5d233f9e22af` |
| COMPAT | `Knx.Ets.Compatibility.dll` | `3a4e4ef40bbb9bb566fdb3d5948584b2278dc8fea774be75091d0e9d812e9268` | — | 103 | `ev_4bef8d215bbaffa11752eab44204634e6d8ca8899562293c598898af51e895e0` |
| OC | `Knx.Ets.OnlineCatalog.Download.dll` | `55f6675ca3c441da326bcfa0c5c47907dc94f81bb0583fe323e294f366abcdc1` | — | 132 | `ev_ef0835c7a7c8eef80e93eeef3e3123f924d3565f51610c165006e5b7525d2e73` |
| DYN | `Knx.Ets.DynamicData.dll` | `29862fe6678d099706d4d40556e98f62c1a8f9dd532551ef445f4cc56fed3426` | — | 179 | `ev_65fd90837a5a423e4a43c48a040024134c841575c89f55f57d7e2313dc17c1d2` |
| CALC | `Knx.Ets.CalculatedParameters.dll` | `d0e7732d4a340ed9ee2d6d62323430a851c8e277c86e5efb14ebeff64cadeb5b` | — | 174 | `ev_e661c72cfc3175129a03dffaa303f1dda8e175a5857fd078f42c9ae1046af945` |
| LIST | `plugins.json` | `9e62597f1cdf022139c954ff160bd2fdce8661e438f51119a4040204b1db5485` | — | 235 Einträge | (JSON gelesen, kein REA-Artefakt) |

### Methode und Grenzen

- Statische CLI-Metadatenanalyse (Typen, Felder, Methodensignaturen, Call-Edges,
  `#US`-Literale, Manifest-Ressourcen) über REA; keine Ausführung, keine
  Decompilation, keine beobachteten Klickfolgen.
- Die Assemblies sind Dotfuscator-behandelt: interne Feld-/Hilfsmethodennamen
  sind unkenntlich; **String-Literale sind verschlüsselt**. `PD` enthält genau
  ein `ldstr`-Literal (`#US`-Offset `459789`:
  `"Knx.Ets.UI.ParameterDialog.ViewModel.ParameterDialogViewModel"`), `CALC`
  genau eines (`""`). Sichtbare UI-Texte kommen aus `.resources`
  (`Knx.Ets.UI.ParameterDialog.g.resources`, 114 127 B, Row-Offset `391250`;
  `...Properties.Resources.resources`, 1 511 B, Offset `391264`) und aus
  Satelliten (`de-DE\Knx.Ets.UI.ParameterDialog.resources.dll`,
  `de-DE\Knx.Ets.ViewModel.resources.dll`).
- Die ViewModel-Brücke (`Knx.Ets.ViewModel.ParameterDialog.*`) ließ sich wegen
  der assembly-weiten `member_refs` nicht als vollständiges REA-Member-Dokument
  abrufen (Transport-Budget 10 MB; retained:
  `ev_2c578ecdb2feea1c641a924b6f0b1c3e3dbf7ca6b66881ebe97fcabe8e9363a5`,
  `ev_bb36188b7e2adaef454bb1e26f98ab4faca6ae3bc71a925cccc24e2f331777b3`).
  Für diese Typen wurden Typ-/Methodennamen lokal aus den Metadaten desselben
  SHA gelesen; Aussagen dazu sind als **derived** markiert. Alle
  verhaltensrelevanten Verträge (`PDT`, `PD`, `XML`, `OM`, `SDK`, `CALC`,
  `DYN`) sind mit REA-Evidence belegt.
- Keine Aussage über Laufzeitverhalten, Dateiformate jenseits der Typmodelle
  oder über Bus-Downloads. Ein negatives Ergebnis ist nur innerhalb der
  genannten Typauswahl gültig.

---

## 1. Katalog: Import, Suche/Vorschau, Update

### 1.1 Sichtbarer Arbeitsablauf (derived aus VM-Metadaten + observed OM-Verträgen)

`Knx.Ets.ViewModel.Application.Workspace.CatalogPanelViewModel` besitzt
`DownloadCommand`, `ImportCommand`, `ExportCommand`, `AddCommand…`,
`SelectedAddTarget`, `AddTargets`, `SectionPath`, `DeviceInformation`,
`AddItemsCount`, `SelectedAddOption`, `OnSearchStringUpdated`,
`OnSelectedTreeItemChanged`, `IsBusy`, `IsSupportLicensePresent`,
`IsMyProductsLicensed`, `OnlineCatalogSettingsViewModel`.
`CatalogViewModel`/`ICatalogService` liefern `Manufacturers`, `ShowOnlineCatalog`,
`ToggleOnlineCatalogCommand`, `RefreshAsync`, `AddDeviceToInstallation`,
`RecentProductsManager`, `RecentImportsManager`, `ProviderRefreshed`.
`CatalogUpdateService` aktualisiert mit `UpdateInterval`, `ProgressChanged`,
`Finished`, `IsUpdatingCatalog`, `IsUpdatingMarkets`, `LastUpdatedHasFailed`,
`UpdateMarketsAsync`, `UpdateManufacturersAsync`, `ClearOnlineCatalogData`,
`UpdateIfRequiredAsync`.

Damit sind fünf getrennte Abläufe belegt: **lokaler Import**, **Online-Download**,
**Export**, **Hinzufügen zur Installation** und **Aktualisieren/Löschen** von
Store-Einträgen. Suche existiert als Textfeld-Ereignis; ihr Suchfeldumfang ist
aus den (verschlüsselten) Strings nicht bestimmbar (**inferred**: Name,
Bestellnummer, Beschreibung, Pfad).

### 1.2 UI-Ressourcen und Typen

| Ebene | Typen (Auszug) | Zweck |
|---|---|---|
| Panel | `CatalogPanelViewModel`, `CatalogViewModel`, `CatalogManufacturerNodeViewModel`, `CatalogSectionNodeViewModel`, `CatalogChildrenCollectionProvider` | Baum Hersteller → Abschnitt → Produkt, Suche, Abschnittspfad |
| Detail | `CatalogItemDetailViewModel`, `CatalogExtraInfoViewModel`, `ICatalogItemDetailViewModel`, `RecentCatalogItemDetailViewModel` | Vorschau/Tabs, Online-Zusatzinfos |
| Aktionen | `CatalogContextMenuViewModel`, `CatalogAddOptions`, `CatalogItemDragData`, `ProductTemplateDragData`, `UpdateCatalogItemCommandHandlerBase`, `UpdateCatalogItemFromOnlineCatalogCommandHandler`, `UpdateCatalogItemFromProductStoreCommandHandler` | Kontextmenü, Drag&Drop, Update |
| Dienste | `ICatalogService`, `CatalogUpdateService`, `ICatalogUpdateService`, `OpenCatalogsPanelOperation`, `ProductsImportedMessage`, `CatalogRefreshMessage` | Öffnen, Fortschritt, Refresh |
| Provider (OM) | `CatalogProvider.CatalogsProvider`, `CatalogTreeBuilder`, `CatalogItemsRefresh`, `AggregateCatalogItemCollection`, `CatalogManufacturerCollection`, `CatalogItemTree` | Zusammenführung mehrerer Quellen |
| Verträge (OM) | `ICatalogsProvider`, `ICatalogFilter`, `ICatalogManufacturerCollection`, `ICatalogPanelSettings`, `IOcProductStore`, `IOnlineCatalogFileProvider`, `IOnlineCatalogUpdater` | Quellen, Filter, Online |
| Online (OM) | `OnlineCatalogStore`, `OnlineCatalogUpdater`, `OnlineCatalogDownloader`, `OnlineCatalogFilter`, `OnlineCatalogMarket(s)`, `OnlineCatalogSettings`, `OnlineCatalogExtraInfo` | Markt-/Hersteller-Caches, Downloads |

In den Typnamen von `PDT`/`PD` findet sich kein Katalogbezug; Katalog-UI und
Parameterdialog liegen in getrennten Assemblies (**derived**).

### 1.3 Datenmodell

**Quellen** (`Knx.Ets.ObjectModel.CatalogProvider.CatalogItemSource`, observed):
`Online`, `ProductCache`, `Local`, `None`. `ICatalogsProvider` bietet
`Manufacturers`, `Sources`, `UseOnlineCatalog`, `IsLatestEtsVersion`, `Project`;
`Refresh`/`RefreshAfterDelete` in mehreren Überladungen.
`CatalogsProviderRegistry` registriert Provider global
(`Register`, `GetProvider`, `GetGlobalCacheProvider`, `ForEach`, `Unregister`,
`Clear`), `CatalogTreeBuilder` baut daraus den Baum
(`WithOnlineCatalog`, `WithProductStore`, `Rebuild`, `Build`, `Refresh`).

**Filter** (`ICatalogFilter`, observed): `ManufacturerAvailable`,
`IsAvailableInMarket`, `HasRequestedTranslation`, `HasCorrectSchemaVersion`.
`OnlineCatalogFilter` hält dazu eine Map `Manufacturer → CatalogId → bool` und
`CatalogId → string[]` (Sprachen). `OnlineCatalogSettings`: `SelectedMarket`,
`ShowOnlyProductsContainingSelectedProductLanguage`,
`ShowOnlySelectedManufacturers`, `ManufacturerFilter`, `BaseUrl`.

**Produktknoten** (`CatalogTreeCatalogItem`, observed) trägt die gesamte
Vorschau: `Name`, `Number`, `Description`, `MediumTypeNames`, `MediumTypeIds`,
`IsCoupler`, `IsSecure`, `Security`, `Flags`, `SchemaVersion`,
`RequiresHigherSchemaVersionThanEts`, `SlReady`, `HasPlugin`,
`PluginInstallationStateString`, `IsRegistered`, `ProductInfo`, `Source`,
`Path`, `PathIdentifier`, `ParentManufacturer`, `ParentCatalogSection`,
`OnlineCatalogCatalogItemId`, `OnlineCatalogUpdateAvailableForProject`,
`ProductStoreUpdateAvailableForProject`, `NonRegRelevantDataVersion`.
`CatalogTreeManufacturer` ergänzt `KnxManufacturerId`, `CompatibilityGroup`,
`DefaultLanguage`, `OrderNumberFormattingScript`; `CatalogTreeCatalogSection`
`CatalogItems`, `CatalogSections`, `AllCatalogItems`, `Path`.

**Store-Modell** (`Knx.Ets.Sdk.Product.*`, observed): `Manufacturer`,
`CatalogSection`, `CatalogItem`, `Product`, `Hardware`, `Hardware2Program`,
`ApplicationProgram`, `Parameter`, `ParameterRef`, `ParameterType*`, `Union`,
`ModuleDef`, `ComObject(Ref)`, `Baggage`, `ApplicationProgramExtension`.
`OM.Product.ICatalogItem` spiegelt die Knotenfelder und ergänzt
`RemoveSource`, `AddInfo`.

**Online-Index** (`OC`, observed): `IndexFile` (`Version`, `ManufacturerId`,
`ManufacturerName`, `DefaultLanguage`, `Sections`, `Entries`, `Markets`,
`IndexFileMarkets`, `Merge`, `AddSection`, `AddMarket`, `GetEntryById`,
`GetAllCatalogItemIds`, `FilterByMarket`, `FilterByItemsWithPlugin`, `Save`,
`Load`), `IndexFileSection` (`ChildSections`, `IndexFileEntries`, `Path`,
`Translations`, `AddIndexFileEntry`, `RemoveDeletedIndexFileEntries`),
`IndexFileEntry` (u. a. `ProductName`, `OrderNumber`, `ApplicationProgramName`,
`ApplicationIdentifier`, `SchemaVersion`, `RequiresExternalSoftware`,
`PlugInInformation`, `NoDownloadWithoutPlugin`, `DownloadInfoIncomplete`,
`SecurityInfo`, `ReplacesVersions`, `AdditionalCatalogInfo`, `LifeCycleType`,
`SlReady`, `Translations`), `ApplicationProgramInfo`
(`FingerPrint`, `DownloadInfoIncomplete`, `Broken`, `SupportsSecurity`,
`HasPlugIn`, `RequiresExternalSoftware`, `ReplacesVersions`, `Version`,
`ApplicationNumber`, `SlReady`, `NonRegRelevantDataVersion`),
`Hardware2ProgramInfo` (`NoDownloadWithoutPlugin`, `ApplicationProgramId`,
`BusCurrent`, `MediumTypeIds`), `ProductInfo` (inkl. `WidthInMillimeter`,
`RailMounted`, `AttributesColor/Series`, Übersetzungen).
`CatalogLifeType` = `Default`, `NewProduct`, `EndOfLife`.

`Downloader` (OC): `DownloadIndex`, `DownloadIndexByMarket`, `DownloadMarketFile`,
`DownloadProductBySchema`, `DownloadProduct`, `HasNewerIndexFile`,
`HasNewerMarketFile`, `DownloadCatalogInfo`, `…TechnicalInfo`,
`…ExtraInfo`, `…Picture`, `DownloadBulkUploadExcelSheet`,
`DownloadExportAsync`; `DownloadProductRequest` (`CatalogIds`, `LanguageIds`),
`DownloadExportRequest` (`MarketId`, `ManufacturerIds`).

### 1.4 Import- und Update-Pipeline

**Produktimport** (`OM.Import`, observed): `IProductImporter.Import/5` sammelt
über `ImportInformationCollector`:
`DevicesWithImportHandlers`, `DevicesWithExternalSoftware`,
`RequiresExternalSoftware`, `BaggagesToInstall`, `ApplicationPrograms`,
`CatalogItems`, `KnxManufacturers`, `FailedBaggageInstallations`,
`DevicesWithFailedImportHandlers`, `HasAnyPlugin`,
`Hardware2ProgramsMustHaveDownloadPlugin`.
Collector/Merger: `CatalogItemCollector`, `HardwareCollector`,
`ApplicationProgramCollector`, `BaggageCollector`, `DeviceCollector`,
`CatalogMerger`, `HardwareMerger`, `ApplicationProgramsMerger`,
`BaggageMerger`, `InstallationMerger`, `AddinDataMerger`,
`AdditionalProjectDataMerger`, `BcuKeyScrambler`, `Encryption`.
`BaggageInstaller.Install` prüft Versionen; `BaggageInstallationState` =
`NoBaggagesAttached`, `NotInstalled`, `InstalledWithIdenticalVersion`,
`InstalledWithLowerVersion`, `InstalledWithHigherVersion`.
`BrokenShapeExchanger` (Undo-fähige Operation, `WithProject`) tauscht beim
Import unbekannte/beschädigte Elemente gegen bekannte Formen — das ist ETS'
expliziter Umgang mit **fremden/opaken Shapes**.

**Store-Update** (`OM.Operations.StoreUpdate`, observed):
`UpdateGlobalProductStoreOperation` und `UpdateProjectStoreOperation` teilen
den Vertrag von `UpdateStoreOperationBase` (**derived** aus identischen
Membern) mit `SpecializedValidate`, `BeforeExecute`,
`SpecializedExecute`, `AfterExecute`, `SpecializedUndo`, `SpecializedRedo`,
`HashFiles/HashDirectory/HashFile`, `BackupTempFolder`, `WorkTempFolder`,
`Store`, `FinalizeMessage`. Belegte Schritte und Ressourcen-Keys:
`StoreUpdatePrepareUpdate`, `StoreUpdateValidatingInputFiles`,
`StoreUpdateReadingStoreData`, `StoreUpdateRebuildingCaches`,
`StoreUpdateCopyingStore`, `StoreUpdateConvertingMasterData`,
`StoreUpdateConvertingManufacturerData`, `StoreUpdateConvertingCatalog`,
`StoreUpdateConvertingHardware`, `StoreUpdateConvertingApplicationProgram`,
`StoreUpdateConvertingBaggage`, `StoreUpdateConvertingProject`,
`StoreUpdateStartingLegacyConversion`, `StoreUpdateFinalizingLegacyConversion`,
`StoreUpdateReimportingApplicationProgramCounter`,
`StoreUpdateExportingApplicationProgramCounter`,
`StoreUpdateUpdateingManufacturerDataCounter`, `StoreUpdateUpdatingProjectData`,
`StoreUpdateConversionFinished`, `StoreUpdateWritingStoreData`.
Technisch: `Knx.Ets.CvNext.CvNextFacade.ConvertStore`,
`CurrentSchemaInformation.EtsSchemaVersion`,
`FileProductStore.SchemaVersion`/`ToolVersion`,
`CreateInfoDuringSchemaUpdateInTransaction`,
`IProductStore.ApplyMasterHash` (Signatur-/Hash-Abgleich), Transaktion über
`TransactionRootPaths` + Work-/Backup-Ordner.

**Löschen** (`DeleteCatalogItemsFromProductStoreOperation`, observed): entfernt
Einträge direkt aus `IProductStore.Catalog`, `CatalogTranslation`, `Hardware`,
`Baggages` und `DeleteApplicationProgram` (XDocument `Descendants`, `Remove`,
`Save`), danach `ApplyMasterHash` und `ICatalogsProvider.RefreshAfterDelete`.

**Nicht-registrierungsrelevante Daten** (`UpdateNonRegRelevantDataOperation`):
`IDeviceProductImporter.UpdateNonRegRelevantData` + `Refresh` — erlaubt
Produktdaten-Update ohne erneute Registrierung.

### 1.5 Rand- und Fehlerfälle

| Fall | Beleg | Verhalten |
|---|---|---|
| Katalog-Schema neuer als ETS | `CatalogTreeCatalogItem.RequiresHigherSchemaVersionThanEts`, `SchemaVersion` | Eintrag wird markiert; Import/Öffnen muss ablehnen (UI-Text nicht lesbar) |
| Produkt „broken“ | `ApplicationProgramInfo.Broken`, `DownloadInfoIncomplete` | Kennzeichnung, keine stillschweigende Nutzung |
| Download nur mit Plugin | `Hardware2ProgramInfo.NoDownloadWithoutPlugin`, `IndexFileEntry.PlugInInformation` | Gerät ohne Plugin nicht programmierbar |
| Externe Software nötig | `RequiresExternalSoftware`, Import-Collector `DevicesWithExternalSoftware` | Hinweis/Blockade |
| Baggage fehlt/älter/neuer | `BaggageInstallationState` (5 Werte), `FailedBaggageInstallations` | Installation wird nachgeholt bzw. als Fehler gesammelt |
| Plugin entfernt | `plugins.json`, Attribut `PluginRemovedByConverter` = `1.4` (6 Einträge) | Altplugin wird nicht mehr gehostet |
| Veralteter Store | `ProductStoreUpdateAvailableForProject`, `OnlineCatalogUpdateAvailableForProject` | Update-Aktion pro Eintrag |
| Markt/Sprache | `ICatalogFilter`, `OnlineCatalogSettings` | Filter, kein Fehler |
| Signatur/Hash | `IProductStore.ApplyMasterHash`, `SignatureMismatch` (ErrorCode); `OnlineCatalogStore.CheckStoreHmac`/`CheckMasterHash` | Integritätsprüfung |
| End-of-Life | `CatalogLifeType.EndOfLife` | reine Kennzeichnung |

### 1.6 BusChord-Lücke

Vorhanden: lokaler Import von `.knxprod`/Vendor-ZIP, Content-Hash-Store,
Programm-/Parameter-Parsing, Bibliothek, Einfügen, Suche über lokale Felder
(`src/Knx.Core/Catalog/*`, `src/Knx.App/Services/CatalogBrowser.cs`).

Fehlt vollständig: Online-Katalog (Index/Markt/Sprache), Quellenzusammenführung
(`Online`/`ProductCache`/`Local`), Store-Update/-Löschung als Operation,
Baggage-Versionierung/-Installation, Import-Handler-Bericht
(`DevicesWithImportHandlers`), externer-Software-/Plugin-Gating,
Schema-Gating jenseits Parserfehlern, Lizenz-Gating (`IsMyProductsLicensed`),
`Broken`/`SlReady`/`RegistrationStatus`-Semantik, Hersteller-Order-Number-Skript.
BusChord ersetzt Katalogbytes durch neue Identität statt zu aktualisieren; das
ist bewusst, macht aber „Katalog aktualisieren“ unmöglich.

**Empfehlung:** Stufe 1 (billig, hoher Nutzen): `SchemaVersion`/`Broken`/
`RequiresExternalSoftware`/`HasPlugin` aus dem bereits geparsten XML in
`CatalogModel` aufnehmen und im UI als Statuszeile zeigen; Import-Bericht
(Produkte, Programme, fehlende Baggages, Handler) als Text zurückgeben.
Stufe 2: Baggage-Vergleich + Ablage, Produkt-Update per ID (nicht per Hash).
Online-Katalog erst, wenn Sprach-/Marktmodell und Signaturprüfung stehen.
**Confidence:** Datenmodell observed; Ablauf derived; UI-Texte unknown.

---

## 2. Applikationsprogramme

`ApplicationProgram` (XML, observed) ist die Klammer um statische Deklaration
und dynamische Instanzen:

- **Statisch** (`Static` → `ApplicationProgramStatic`): `ParameterTypes`,
  `Parameters` (+ `ParametersAndUnionParameters`), `ParameterRefs`,
  `ParameterCalculations`, `ParameterValidations`, `ComObjectTable`,
  `ComObjectRefs`, `AddressTable`, `AssociationTable`, `FixupList`,
  `LoadProcedures`, `Extension`, `BinaryData`, `DeviceCompare`, `Messages`,
  `Script`, `SecurityRoles`, `BusInterfaces`, `Allocators`, `Options`, `Code`.
- **Dynamisch** (`Dynamic`, `ModuleDefs`, `ChildModuleDefs`): instanziierte
  `ParameterInstance`s, `ChannelInstance`s, `ComObjectInstanceRef`s,
  `ModuleInstance`s, Pages/Blöcke.
- **Metadaten**: `Id`, `ApplicationNumber`, `ApplicationVersion`, `ProgramType`,
  `MaskVersion`, `ManagementModel`, `RegistrationStatus`, `MinEtsVersion`,
  `OriginalManufacturer`, `PreEts4Style`, `ConvertedFromPreEts4Data`,
  `CreatedFromLegacySchemaVersion`, `ReplacesVersions`, `Hash`,
  `RegRelevantDataShortHash`, `NonRegRelevantDataVersion`, `Broken`,
  `DownloadInfoIncomplete`, `IsSecureEnabled`, `IPConfig`,
  `MaxUserEntries`/`MaxTunnelingUserEntries` und vier Security-Tabellenlimits,
  `AdditionalAddressesCount`, `VisibleDescription`, `HelpFile`,
  `ContextHelpFile`, `IconFile`, `DefaultLanguage`, `Linkable`,
  `DynamicTableManagement`, `Semantics`.
- **Kanäle** (`ApplicationProgramChannel`): `Items`, `Name`, `Number`, `Text`,
  `TextParameterRefId`, `Icon`, `HelpContext`, `Semantics`.

Die Parameter eines Programms sind damit **nicht** eine flache Liste: Seiten
ergeben sich aus Kanälen, `ChannelIndependentBlock`, `ComObjectParameterBlock`
(Rows/Columns/Layout/Cell) und `ParameterSeparator` (siehe 3.3).

**BusChord-Lücke:** `Ksxml.Parse` liest Typen, Parameter, Refs, Blöcke;
`Model.cs`/`Knxproj.cs` speichern Geräteparameter nicht. Es fehlen:
`RegistrationStatus`, `MaskVersion`/Management-Modell, Security-Limits,
`MinEtsVersion`, Module, Allocators, Load Procedures, `Code`/`BinaryData`,
`Extension`, `Script`, `Options`, `DeviceCompare`, `Messages`, `Semantics`,
`Broken`/`DownloadInfoIncomplete`, Kanal-/Seitenmodell.

**Empfehlung:** Diese Felder zuerst nur **erhaltend** behandeln (Raw-XML beim
Gerät), nicht interpretieren. `RegistrationStatus` + `MinEtsVersion` + `Broken`
sind billig und verhindern falsche Bearbeitung. **Confidence:** observed.

---

## 3. Parameterdialog

### 3.1 Aufbau

`Knx.Ets.UI.ParameterDialog.ViewModel.ParameterDialogViewModel` (105 Methoden,
`ev_a762772ad9f4423ffef9a3ade05c7b75eed2ab2b33ae933699b0518b4f1ab644`):

| Gruppe | Mitglieder |
|---|---|
| Seiten | `PageTree`, `ParameterTrees`, `MainTree`, `SelectedPages`, `SelectedPageViewModel`, `LastSelectedPage`, `SelectPage`, `GetPageNode`, `GetExpandedNodes`, `UpdateParameterTrees`, `ScrollViewerOffset` |
| Geräte | `IsMultipleSelection`, `ContainsDevices`, `HasOptionalApplicationProgram`, `HasNoApplicationProgram`, `HasSingleApplicationProgram`, `HasMultipleApplicationPrograms` |
| Sperren | `IsLockedForEditing`, `IsBrokenForEditing`, `IsLockedBySplitAndMerge`, `IsDefect`, `AreCustomerAdjustableValuesLocked` |
| Aktionen | `ResetToDefaults`, `get_ResetToDefaults`, `RefreshParameterDialogCommand`, `PrintCommand`, `PrintDataGridCallBack`, `OpenParameterPluginDialogCommand`, `SelectCommand`, `MarkAsUserPreferredCommand`, `UserPreferredDetailTab` |
| Darstellung | `TabName`, `IconKey`, `HeaderText`, `HelpText`, `ItemsCount`, `SelectedItemsCount`, `IsVisible`, `ShowGrantAccess`, `ShowContextHelp`, `FocusedParameter`, `DialogState`, `Type`, `IndentWidth` |
| Plugin | `PluginInstallationState`, `ForceParameterDialog`, `NoParametersCheckAccessText` |

Container: `ParameterDialogDetailViewModel` (Detailansicht) und
`ParameterDialogModalWrapper` (`RequestClose`, `Closed`, `Result`, `Ok`,
`Cancel`) hosten den Dialog. Der Dialog wird also sowohl eingebettet als auch
modal verwendet (**derived**).

### 3.2 Seiten-, Listen- und Rastermodell

- `PageNodeViewModel`: `Page`, `Pages`, `Path`, `PathHistory`,
  `ShortPathHistory`, `GetPageById`, `IsRoot`, `IsSelectable`, `CanSelect`,
  `IsSelected`, `IsExpanded`, `ShowGrantAccess`.
- `LayoutElementViewModel`: `LayoutElement`, `ParentLayout`, `Cell`, `Icon`.
- `ParameterListViewModel`: `StructureElement`, `Text`, `Children`,
  `AfterValueSet`-Event, `VerifyAndPersistValueCommand`, `NotifyParent`.
- `ParameterGridViewModel`: `Columns`, `Rows`; `ParameterTableViewModel` als
  Tabellenvariante.
- `ParameterBuilder.FromUIParameter/4` → `ParameterViewModel`;
  `ParameterBuilder.GetLayoutStructureViewModel/5` → `LayoutElementViewModel`.
- `IParameterProvider` (PDT, observed) ist die Fabrik:
  `GetParameter`, `GetSeparator`, `GetButton`, `GetLayoutElement`,
  `GetIcon`, `GetContextHelp`, `GetTextByRefId`. In VM implementiert
  `ParameterViewModelFactory` exakt diese Methoden plus
  `GetVisibleParametersForPage` und `ShowAllocatorExceededWarning`
  (**derived**, lokale Metadaten).

Sichtbarkeit/Enable: `IUILayout.IsVisible`/`Occurance`, `IUILayoutStructureElement.IsVisible`,
`IUIParameter.Indentation`, `ParameterViewModel.IsEnabled`,
`ParameterInstance.IsActive`, `DeviceActivityCalculator.IsAccessibleInParameterDialog`.
Ein „enabled“-Flag im XML gibt es nicht; Enable folgt aus Aktivität/Zugriff
(**derived**).

### 3.3 Blöcke und Trenner

`ParameterSeparator` (XML) = `UIHint` + `Text`/`TextParameterRefId` + `Access` +
`Cell` + `Icon` + `TextAlignment`. `ParameterSeparatorUIHint` (observed):
`HorizontalRuler`, `Headline`, `Information`, `Error`.
`ComObjectParameterBlock`: `Layout` ∈ `Table`, `Grid`, `List`; `Rows`
(`CollapseIfEmpty`), `Columns` (`WidthInPercent`, `TextAlignment`), `Inline`,
`ShowInComObjectTree`, `Cell`, `Icon`, `HelpContext`, `ParamRefId`.
`ParameterBlockRow`/`Column`/`ChannelIndependentBlock.Items` liefern die
Verschachtelung. `IUIParameterSeparator.UiHint` und
`UIParameterSeparator` (VM) tragen das in die UI; `ParameterSeparatorSelector`
wählt das Template.

### 3.4 Typisierte Editoren

| XML-Typ | UI-Vertrag (PDT) | ViewModel (PD) | Felder |
|---|---|---|---|
| `TypeNumber` | `IUiParameterNumber`, `IUIParameterInt` | `NumberParameterViewModel`, `NumericParameterViewModel` | `SizeInBit`, `IsSigned`, `MinInclusive`, `MaxInclusive`, `StepWidth`, `UIHint`; VM: `MinimumValue`, `MaximumValue`, `StepWidth`, `Epsilon`, `Index`, `Precision`, `Format`, `PositiveRegex`, `NegativeRegex` |
| `TypeFloat` | `IUIParameterFloat` | `FloatParameterViewModel`, `SteplessFloatParameterViewModel` | `MinInclusive`, `MaxInclusive`, `StepWidth`, `Encoding` (`DPT_9`, `IEEE_754_Single`, `IEEE_754_Double`), `Format`, `UIHint` (`None`, `Slider`) |
| `TypeRestriction` | `IUIParameterRestriction`, `IUIParameterRestrictionEnum` | `EnumerationParameterViewModel`, `EnumerationParameterItemViewModel` | `Enumeration` (`Value` uint + `Text` + `Icon` + `PictureAlignment` + `DisplayOrder`), `Base` (`Value`, `BinaryValue`), `SizeInBit`, `UIHint` (`None`, `Text`, `DropDown`, `Buttons`, `Segmented`) |
| `TypeText` | `IUIParameterText` | `TextParameterViewModel` | `SizeInBit`, `Pattern`, `IsZeroTerminated`, `Encoding`, `MaximumChars` |
| `TypeTime` | `IUIParameterTime` | `TimeParameterViewModel`, `TimeSpanContainer` | `Unit` (`Hours`…`PackedMinutesSecondsAndMilliseconds`), `UIHint` (25 Formen: `Time_ss`, `Time_dhhmmss`, `Duration_hhmmssfff`, …), `min/max`, `HasDefaultSuffix` |
| `TypeDate` | `IUIParameterDate` | `DateParameterViewModel` | `Encoding` (`Dpt11`), `DisplayTheYear`, `MinDate`/`MaxDate`/`ZeroDate` |
| `TypeIPAddress` | `IUIParameterIPAddress` | `IpAddressParameterViewModel` | `Version` (`IPv4`/`IPv6`), `AddressType` (`HostAddress`, `GatewayAddress`, `UnicastAddress`, `BroadcastAddress`, `MulticastAddress`, `SubnetMask`) |
| `TypePicture` | `IUIParameterPicture` | — | `RefId`, `HorizontalAlignment` (`Stretch`, `Left`, `Center`, `Right`, `Repeat`) |
| `TypeColor` | `IUIParameterColor` | `ColorParameterViewModel` | `Space` |
| `TypeRawData` | — | — | `MaxSize` (kein eigener Editor belegt) |
| `TypeNone` | `IUIParameterNone` | — | — |
| Button | `IUIButton` | `ButtonParameterViewModel` | `IsAsync`, `ExecuteAsync`, `CreateTask`, `CanExecuteAction`; VM: `ExecuteEventHandler`, `CancelEventHandler`, `ExecuteProgress`, `ToolTip` |

Zahlen-/Float-Editoren rechnen über `DisplayFactor`/`DisplayOffset` (XML) bzw.
`Format`/`Precision` (VM) um; gespeichert wird der Rohwert
(**derived** aus Feldnamen und `ParameterInstanceRef.Value`).

### 3.5 Mehrfachauswahl, Zugriff, Reset

- `IUIParameterRoot.SelectedDevices`, `ResetToDefaults`, `CanResetToDefaults`,
  `QueryForMultipleSelectionConflictStrategy`-Event.
- `MultipleSelectionConflictStrategy` (Delegate),
  `MultipleSelectionParameterNotActiveStrategy` = `Cancel`, `Skip`.
- `ParameterViewModel.MultipleSelection`,
  `IsContainingDifferentValues`, `ParametersToSyncWith`.
  Ressourcen-Keys: `MultiDeviceDoesNotSupportDefaultParameters`,
  `MultipleParameterChangeUndoHistoryText`,
  `AccessGrantedToMultipleDevicesWithMultipleParameters`,
  `AccessRevokedOnMultipleDevicesWithMultipleParameters`.
- Kundenfreigabe: `IUIParameter.Access` (`None`, `Read`, `ReadWrite`),
  `GrantUseByCustomer`, `SetGrantAccess`, `ShowWhileGrantAccess`,
  `ForbidGrantingUseByCustomer`, `IsCustomerAdjustable`; Keys
  `AccessGrantedToParameter`, `AccessRevokedToParameter`,
  `ReadCustomerAdjustableParameters`.
- Reset: `IUIParameterRoot.ResetToDefaults` →
  `Informer.SuppressResetAssignsToDefault`; Key `ResetParameter`.
  `ParameterViewModel.IsDefault`/`DefaultValue` markieren Abweichungen.
- Transfer: `TransferParametersAndFlagsCommandHandler` /
  `TransferParameterAndFlagsViewModel` — Parameter und Flags auf ein anderes
  Gerät übertragen (eigener Assistent).

### 3.6 Datenwirkung

`ParameterViewModel.UpdateFromSource` liest aus `IUIParameter`/`ParameterInstance`;
`UpdateToSource`/`SetNewValue` schreiben zurück; `CoerceValue` normalisiert;
`VerifyAndPersistValueCommand` persistiert erst nach Prüfung
(`ShowMessage`, `Error`, `IUIParameter.ValidationErrorMessage`).
Persistiert wird in `ParameterInstance.Value`/`BaseValue` und
`ParameterInstanceRef.Value`/`GrantUseByCustomer` (XML, observed); der
Dialog selbst hält keinen Projektzustand (**derived**). Ein „Übernehmen“-Modell
statt Live-Speichern ist aus `DialogState`/`VerifyAndPersistValueCommand`
ableitbar, aber nicht belegt.

### 3.7 Randfälle

Kein Applikationsprogramm (`HasNoApplicationProgram`, `NoParametersCheckAccessText`),
optionales Programm, mehrere Programme, gesperrt (Split&Merge, Defekt, Broken,
kundengesperrt), `ForceParameterDialog` (Dialog trotz Plugin), Plugin-Zustand
(`PluginInstallationState`), `AllocatorExceeded`-Warnung, mehrere Geräte mit
abweichenden Werten, `IsDefault`-Reset, Druck (`PrintCommand`),
Kontext-Hilfe (`ShowContextHelp`, `ContextParameterHelp`).

### 3.8 BusChord-Lücke

Vorhanden: `ParameterEditingSession`, `ParameterItemViewModel`,
`ParameterPane`, `ParameterTemplateSelector` mit 12 Typen, Validierung,
Persistenz über `DeviceInstance.ParameterValues` (laut P3-Bericht),
`DynamicParameters`-Auswertung.

Fehlen: Seiten-/Kanalbaum mit Pfad-Historie, Listen-/Raster-/Tabellenlayout,
`CollapseIfEmpty`, Zellen-/Icon-/HelpContext-Modell, Kundenzugriff/`SetGrantAccess`,
Mehrgeräte-Synchronisation und Konfliktdialog, „Als Standard“-Markierung,
Parameter+Flags-Transfer, Plugin-Dialog-Übergabe, Druck, Kontext-Hilfe,
`AllocatorExceeded`-Warnung, Kultur-/Formatregeln der Zahlenfelder.

**Empfehlung:** (1) Kundenzugriff und Reset semantisch trennen (heute nur Wert),
(2) Seitenbaum aus Kanälen/Blöcken ableiten und Pfad stabil halten,
(3) `IsDefault` gegen deklarierten Default rendern, (4) Mehrfachauswahl nur mit
explizitem Konfliktmodell (`Cancel`/`Skip`) einführen. Kein Plugin-Dialog
nachbauen. **Confidence:** Struktur observed, Ablauf derived.

---

## 4. Dynamische Parameter (Choose/When/Assign)

### 4.1 Deklarationsmodell (XML, observed)

`Choose`-Familien: `ChannelChoose`, `ComObjectParameterChoose`,
`DependentChannelChoose`, `LdCtrlChoose`, `ModuleDefLdCtrlChoose` — jeweils mit
`ParamRefId`, `WhenCollection`, `InternalDescription`; `IChoose` fasst das
zusammen. `When`: `test` (string), `default` (bool), `ItemCollection`.
`WhenCondition`: `ConditionType` ∈ `Equal`, `NotEqual`, `Less`, `LessOrEqual`,
`Greater`, `GreaterOrEqual`, `List`; `SingleValue`, `Values`; `Parse(string)`.
`WhenConditionType` ist der stabile Enum-Vertrag.
`ParameterRef` (Ziel von Assign) hat `RefId`, `Value`, `InitialValue`, `Access`,
`TextParameterRefId`, `Semantics`, `Tag`, `ForbidGrantingUseByCustomer`,
`CustomerAdjustable` (jeweils mit `…Specified`-Flag).

### 4.2 Auswertung (DYN, observed)

`ActivityCalculatorBase` (9-Parameter-Ctor) mit
`CalculateActivityForChoose`, `PerformRepeatCalculationCycle`,
`PerformInitialParameterCalculationOnDynamicModuleDefinition`,
`FinishCalculationCycle`, `GetEnforceScope`, `DynamicModuleRootStates`,
`RepeatRootStates`, `ModuleDefinitionCache`, `TextParamRefUpdater`.
`DeviceActivityCalculator`:
`PerformInitialActivityCalculation`,
`UpdateDependenciesOnParameterValueChange/3`,
`GetParameterActivity`, `IsAccessibleInParameterDialog`, `GetActiveChannels`,
`GetCurrentText`/`GetCurrentTextForTextParamRef`/`GetDefaultText`,
`GetCurrentAllocatorValue`, `InternalParameterUpdate`, `Invalidate`/`EnsureValid`,
`MaxAdditionalCalculationIterations`, `LastCalculationIterationDepth`,
`ParamInstanceDeActivatedByMap`, `ComObjInstanceDeActivatedByMap`,
`ModuleInstanceActivatedByMap`, `SuppressResetAssignsToDefault`,
`PrintDynamicStructure` (Debug).
Fehlerpfade: `AbortActivityCalculationException` (mit `ComObjectInstance`) und
`ActivityCalculationMaxIterationDepthException` — Berechnung ist **begrenzt**
und kann kontrolliert abbrechen.
`Informer`: `ChangeAssignState`, `StateChanged`,
`ProcessChangedParametersWithSanityCheck`, `UpdateComObjectChannel`,
`UpdateAllocator`, `ForEachParamWhereActivityOrValueChanged`,
`ResetActivityOrValueChangeLists`.
`Chooser.RegisterChoose`/`GetChooses`/`ResetState`; `ChooseState.RawSetActive`;
`AssignState`/`AssignSourceParameter` (`ActivateAssign`, `DeactivateAssignment`,
`ExecuteAssign`, `ResetState`, `GetActiveAssigns`); `ChannelState`
(`Number`, `Name`, `Text`, `HelpContext`, `IsChannelIndependentBlock`,
`ModuleState`); `ComObjectRefRefState` (`RefId`, `Text`, `ModuleState`);
`RepeatCalculator`, `ModuleActivityCalculator`, `TextParamRefUpdater`
(`TextParameterChanged`, `GetCurrentText`, `GetDefaultext`, `UpdateTextCache`,
`SynchronizeTextParamInfosWithActiveModuleInstances`).

Facaden: `IApplicationProgramDynamicFacade`
(`GetCommunicationObjectReference`, `GetAllComObjectRef`,
`GetParameterObjectReference`, `GetAllParamRef`,
`GetModuleDefinitionReference`, `GetAllocatorDefinitionReference`,
`GetDynamicForModule`), `IParameterInstanceRefDynamicFacade`
(`Id`, `ParameterRefFacade`, `IsActive`, `Value`, `InitialValue`,
`Indentation`, `UniqueId`, `HasDefaultValues`, `BaseValue`),
`IParameterReferenceDynamicFacade`
(`Id`, `Name`, `Access`, `DisplayOrder`, `DefaultValue`, `UniqueId`,
`CanonicalType`).

### 4.3 Wirkung und opake Daten

Inaktive Parameter bleiben im Modell (`IsActive=false`), werden aber nicht
angezeigt und nicht heruntergeladen; Aktivität wirkt zusätzlich auf
Kommunikationsobjekte, Kanäle, Module und Allocators. Assigns setzen Werte in
Refs und können dabei Typ-/Bereichsfehler erzeugen (`AssignSourceParamRefRefXorValue`,
`AssignSourceParamRefRefType`, `AssignTargetParamRefAsSourceParamRefRef`).
Text-ParamRefs erzeugen dynamische Texte (Suffixe/Beschriftungen).
`PrintDynamicStructure` belegt einen Debug-/Supportpfad.

### 4.4 BusChord-Lücke

BusChord hat einen begrenzten, deterministischen Evaluator
(`src/Knx.Core/DynamicParameters.cs`): `choose`/`when`/`assign`,
Vergleiche/Alternativen, Default-Zweig, Pass- und Tiefenlimit, `Unsupported`-
Liste, `InvalidAssignments`. Nicht unterstützt und explizit abgelehnt:
Module/`ModuleDef`, `Repeat`, Button, Load Procedures, Skripte, Allocators,
Kommunikationsobjekt-Aktivierung, Text-ParamRef-Updates, Kanal-/Modul-Zustand,
Iterations-/Abbruchmodell. `IsEnabled` spiegelt heute nur `IsVisible`.

**Empfehlung:** Zuerst `Assign`-Validierung und `InvalidAssignments` sichtbar
machen (kein stilles Verwerfen), dann Text-ParamRefs (häufig, rein textuell),
dann Modulinstanzen (großer Aufwand). `Repeat`/Button/Load Controls nur mit
echtem Bedarf. **Confidence:** observed; Ablaufreihenfolge derived.

---

## 5. Parameter-Validierung

### 5.1 XML-Regelwerk (`XML.Check`, observed)

`ICheck.Check/6` liefert einen Status; `CheckSeverity` = `Info`, `Warning`,
`Error`; `CheckContext` sammelt Meldungen. Belegte Prüfer:
`CheckAgainstXmlSchema` (XSD), `CheckIdConvention`, `CheckRefIds`,
`ChooseAndWhenCheck.CheckChooseWhen`, `AssignCheck.CheckAssign`,
`ParameterTypeCheck.CheckMinInclusiveAndMaxInclusive`,
`…CheckSizeInBits`, `…CheckTypeRestrictionAndEnumeration`,
`…CheckParameterTypeUse`, `ParameterAndUnionCheck.CheckParameterValues`,
`…CheckParameterRefExists`, `…CheckUnionParametersForUnionSize`,
`…CheckParameterOverlappingTable`, `…CheckUnreferencedParameterRefs`,
`ComObjectCheck.CheckDatapointTypesToObjectSize`, `…CheckUniqueObjectNumber`,
`ComObjectRefCheck.CheckObjectSize`, `ApplicationProgramCodeCheck.*`,
`LoadControlCheck.*`, `MaskVersionCheck{BCU1,BCU2,SystemB,System300,BimM112,0912}.*`,
`ApplicationProgramCheck.PluginReferenceRestrictions`,
`CatalogItemCheck.CheckCatalogItem`, `TranslationCheck.*`, `FixupCheck`,
`HardwareCheck`, `CheckOptions`, `CheckConfiguration`, `CheckDisposition`,
`CheckDriver`, `CheckListenerFilter`, `CheckTemplateAttribute`.

`ErrorCode` (observed, 100+ Werte) ist der vollständige Fehlerkatalog. Für
Parameter/Dynamik relevant (exakt):
`MaxInclusiveLessThanMinInclusive`, `ValueNotCompatibleToParameterType`,
`EnumerationBinaryValueShorterThanSize`, `EnumerationBinaryValueTooLong`,
`EnumerationBinaryValueMustNotBeSet`, `EnumerationValueNotUnique`,
`ParameterUnionOverlapping`, `ParameterUnionSegmentOverflow`,
`UnionParameterOverflow`, `DefaultUnionParameterOverlapping`,
`ParameterNotUsedByAnyParameterRef`, `ParameterRefReferencingItem`,
`ParameterRefInvalidReference`, `IncrementMustNotBe0`,
`ParameterOverlappingTable`, `ComObjectDatapointTypeSizeMismatch`,
`ComObjectDatapointSubtypeNotAllowed`, `ComObjectNumberNotUnique`,
`ComObjectRefSizeGreaterThanComObjectSize`,
`AssignSourceParamRefRefXorValue`, `AssignSourceParamRefRefType`,
`AssignTargetParamRefAsSourceParamRefRef`,
`ChooseInvalidParameterRefType`, `ChooseMoreThanOneDefaultWhen`,
`ChooseDefaultWithTest`, `ChooseTestSyntax`, `ChooseDuplicateConditionValue`,
`CalculationDuplicateName`, `CalculationParameterInBothSets`,
`CalculationSetEmpty`, `CalculationScriptEmpty`, `CalculationSyntaxError`,
`UnknownLanguageCode`, `IncompleteTranslation`,
`InconsistentDefaultLanguageTranslation`, `TranslatedTextTooLong`,
`InvalidPluginReference`, `BaggageFileNotFound`, `BaggageFileInfoNotUpToDate`,
`SignatureMismatch`, `MinEtsVersion`, `IdNotUnique`, `ValueNotAllowed`,
`ScriptReferenceNotFoundOrUnexpectedSignature`.

### 5.2 Skript-Validierung (`CALC`, observed)

`ParameterValidation` (XML) trägt `ValidationFunc`, `ValidationParameters`,
`Parameters`; `CalculatedParameters.ParameterValidation` führt sie aus.
`ParameterCalculationSystem.ExecuteValidation/5` → bool, `GetError()`;
`ExecuteUIHandler/6` und `ExecutePlan/3` für Berechnungen.
`ParameterCalculation`: `Language` ∈ `JavaScript`, `VBScript`;
`LRTransformation`/`RLTransformation` (+ `…Func`, `…Parameters`), `LParameters`,
`RParameters`, `GetDirectionForChange`; `ParameterCalculationDirection` =
`LR`, `RL`.
Engines: `ManagedJavaScriptEngine`, `ManagedVbScriptEngine` und
`ActiveScriptingEngine`/`JScriptEngine` (COM `IActiveScript`,
`IActiveScriptParse32/64`, `IActiveScriptSite`, `IActiveScriptError`), wählbar
über `ScriptingEngine.PreferActiveScriptingEngines`.
`ScriptingEngine.ExecuteOrderNumberFormattingScript/2` erklärt
`CatalogTreeManufacturer.OrderNumberFormattingScript`.

### 5.3 UI-Validierung (`PD`, observed)

`ParameterViewModel.CoerceValue`, `SetNewValue`, `Error`, `ShowMessage`,
`ValidationErrorMessage`, `VerifyAndPersistValueCommand`; `NumericParameterViewModel`
mit `MinimumValue`/`MaximumValue`/`StepWidth`/`Epsilon`/`Index`,
`PositiveRegex`/`NegativeRegex`, `CurrentCultureInfo`;
`FloatParameterViewModel.GetValueText`/`SetValue`/`GetIndex`;
`TextParameterViewModel.MaximumChars`/`IsZeroTerminated`/`Encoding`;
`EnumerationParameterViewModel.CurrentEnum`/`Items`/`Value`;
`IpAddressParameterViewModel.ResetError`/`SaveValueCommand`;
`DateParameterViewModel.MinDate`/`MaxDate`/`ZeroDate`.

### 5.4 BusChord-Lücke

Vorhanden: `ParameterValidation` (Bereiche, Bitbreite, Enum-Mitgliedschaft,
UTF-8-Grenze), `DynamicParameters.InvalidAssignments`, Parser-Fehlerzeilen.

Fehlen: XSD-Prüfung, RefId-/ID-Konvention, Union-/Tabellenüberlappung,
ComObject-Größenabgleich, Masken-/Load-Control-Regeln, Übersetzungsprüfung,
Skript-Validierung/-Berechnung, `DisplayFactor`/`DisplayOffset`,
Kulturformat, Enum-`BinaryValue`, `Pattern`-Semantik (heute bewusst
abgelehnt), Plugin-Referenzprüfung.

**Empfehlung:** `ErrorCode` als eigene kleine Prüfliste für die **vorhandenen**
Modellteile übernehmen (Union/ComObject ausgenommen), Meldungen mit
`Info`/`Warning`/`Error` trennen. Skripte nicht evaluieren; stattdessen
`CalculationScriptEmpty`/`CalculationSyntaxError` nur als „nicht unterstützt“
markieren. **Confidence:** Regelkatalog observed, Ausführung derived.

---

## 6. Plugins / DCA

### 6.1 Erkennung

`plugins.json` (SHA oben, 235 Einträge, `version: 1`) ist die ausgelieferte
Kompatibilitätsliste. Belegtes Schema:

```json
{"version":1,"plugins":[{"key":"{GUID}","name":"…","type":"Download",
 "interfaces":"ImplementsMultiPassDownload",
 "attributes":[{"key":"ExternalSoftware","value":"…"}],
 "baggages":[{"path":"1\\…dll","version":"1.11.891.0"},{"path":"…chm","timestamp":"…"}],
 "applicationPrograms":[{"ids":["M-0001_A-8027-01-0000"]}]}]}
```

Verteilungen (gezählt): `type` `UserInterface` 87, `Download` 58,
`DataHandler` 26, Kombinationen 51, **13 ohne `type`**; `interfaces`
`HasParameterDialog` 73 (als einziger Wert), `ImplementsMultiPassDownload`
22, `ImplementsSinglePassDownload` 18, `HandlesAddDeleteDevice` 13,
`HasContextMenuItems` 7, `HandlesGroupCommunicationLimits` 1; in Kombinationen
kommen diese Flags zusätzlich vor.
217 Einträge haben `applicationPrograms`, 173 `baggages`.
Auffällig: `type` ist teils String, teils **Zahl** (`9306112`, `9043968`,
`8519680` = Bitflags), d. h. der Serializer schreibt gemischte Repräsentationen.
6 Einträge tragen `PluginRemovedByConverter=1.4` und keinen Typ — sie werden
nur noch erkannt, nicht gehostet. `ExternalSoftware` (12) verweist auf
Fremdsoftware; `IsEconPlugin` (3), `IsLTESystemPlugin` (1).

`PluginRepository` (observed) liest/schreibt dieselbe Struktur:
`Repository` (`Version` int, `Plugins` Map), `Deserialize`/`SerializeAsync`/
`Merge`/`GetPluginInfo`; `PluginInfo` (`Key`, `DataVersion`, `Name`,
`PluginType`, `ImplementedInterfaces`, `Attributes`, `Baggages`,
`PerApplicationProgramInfos`); `PluginKey` (`GuidOrType`, `IsComPlugin`,
`IsDotnetPlugin`); `ApplicationProgramKey`
(`ManufacturerId`, `OriginalManufacturerId`, `ApplicationNumber`,
`ApplicationVersion`, `Parse`/`TryParse`, Format `M-xxxx_A-xxxx-VV-II`);
`BaggageKey` (`ManufacturerId`, `Directory`, `FileName`, `ParsePath`/`ParseId`);
`AttributeKey`; `BaggageFileType` = `Unknown`, `FileNotFound`, `Error`,
`UnmanagedEts3PluginDll`, `ManagedEts3PluginDll`, `Ets4PluginDll`,
`InstallerPackage`, `AdditionalFile`.
`PluginInterfaces` (18 Flags, exakt): `HasParameterDialog`, `HasPropertyPages`,
`HasContextMenuItems`, `HasCustomDeviceReport`, `IsParameterTypePlugin`,
`HandlesAddDeleteDevice`, `HandlesGroupCommunicationEvents`,
`HandlesGroupCommunicationLimits`, `ImplementsTransferParameters`,
`ImplementsProjectCheck`, `HandlesImportExport`, `ImplementsSinglePassDownload`,
`ImplementsMultiPassDownload`, `ImplementsAdditionalDeviceInfo`,
`ImplementsDeviceUpload`, `ImplementsDeviceCompare`, `ImplementsDeviceUnload`,
`ImplementsDeviceReconstruction`.
`PluginType` = `Unknown`, `DataHandler`, `Download`, `UserInterface`.

Der **Loader** (`Knx.Ets.PluginLoader.dll`) ist bis auf `IocModule` vollständig
obfuskiert (77 Typen, keine lesbaren Namen) — Discovery-/Bindungslogik ist
damit **unknown**; belegt ist nur die Repository-/Host-Seite.

### 6.2 Hosting

`COMPAT` (observed): `PluginHostFactory` (`CreatePluginHost`,
`GetPluginInformation`, `GetCachedPluginInformation`,
`ResetAllCachedPluginInformation`, `GetPluginInstaller`),
`PluginHost` und `NestedPluginHost`, `PluginHost.Current`.
`PluginHost` adaptiert DOM → SDK: `SdkDeviceFromDomDevice`,
`SdkComObjectInstanceFromDomComObjectInstance`,
`SdkConnectorFromDomConnector`, `SdkGroupAddressFromDomGroupAddress`,
`SdkApplicationProgramFromDomDevice`; `GetPluginInformation` cached nach
CLSID (`TryGetCachedPluginInformationFromClsid`), `CreateEts3Plugin`,
`PrepareForEtecDomTests`/`FalconTestsFinished`,
`BeforeHawkPluginAccess`/`AfterHawkPluginAccess`, `GetBusConnectionAsync`.

SDK-Verträge (`SDK`, observed): `DownloadPluginBase` (`CreateDeviceManagement`,
`CreateStandardImage`, `CreateKnxCommunication`, `NextDeviceInfoRead`,
`CreateInfoReadImage`, `FormatDeviceInfo`, `CompareDevice`, `UnloadDevice`,
`SpecifyNextImageRead`, `ReconstructDevice`, `ProgressTexts`),
`SinglePassDownloadPluginBase` (`GetPreDownloadProcedure`, `CreateImage`),
`MultiPassDownloadPluginBase` (`NextPass`, `CreateImage`, `OnError`,
`CurrentPass`), `MultiPassResult` (`Percentage`, `LastCall`, `Empty`,
`DownloadProcedure`), `MultiPassErrorReason`
(`SkipErrorAndMoveToNextPass`, `LocalizedDescription`),
`DataHandlerPluginBase` (`OnDeviceAdded`, `OnDeviceCopied`, `OnDeleteDevice`,
`OnGroupAddressChanged`, `CanTransferParameters`, `TransferParameters`,
`CheckDevice`, `DeviceReportParameters`), `ParameterTypePlugInBase`
(`ParameterInstanceRef`, `OnBeforeApplicationShutdown`),
`UiParameterPluginBase` (`Show`, `CreatePluginWindow`,
`InvokeContextMenuItem`, `ContextMenuItems`).
Capability-Enums: `DownloadPlugInCapabilities` = `CompareDevice`, `DeviceInfo`,
`Download`, `Unload`, `Reconstruction`; `UiPlugInCapabilities` =
`ParameterDialog`, `ContextMenu`.
`DevicePluginExecutionContext`/`DownloadPluginExecutionContext`/
`ParameterTypePluginExecutionContext`/`DcaExecutionContext` validieren vor jeder
Aktion: `ValidateWriteAccess`, `ValidateLockedForEditing`,
`ValidateDownloadFlagWriteAccess`, `ValidateDeviceManagementAvailable`,
`ValidateNetworkManagementAvailable`,
`ValidateDeviceManagementWriteAccess`, `ValidateGroupCommunicationAvailable`,
`ValidateAsyncGroupCommunicationAvailable`,
`ValidateExportProjectAndBackupDb`, `ValidateWriteAccessWhileDeviceIsLocked`.

**DCA:** `DcaExecutionContext` (`AddinDataId`, `ContextDevices`) und
`DcaLifeCycleEventReasons` = `PanelClose`, `DeleteDevice`, `SelectionChanged`,
`TabChanged`, `DownloadDevice`. `IHostContext` gibt Plugins:
`StartDownload`, `StartOverwritingIndividualAddress`, `StartUnload`,
`StartFirmwareUpdate`, `CancelPendingOperation(s)`, `ResetDevice`,
`QueryFalconConnection`, `IsConnectionAvailable`, `ReadDeviceInfo`,
`ExportProject`, `ExportSemanticDataAsync`, `NavigateToDomObject`,
`NavigateToParameterDialog`, `ImportProductData`, `ShowMessageBox`,
`ShowModal`, `DisplayMonitorFileAsync`.
`ExportDcaAsUserFile`/`IExportDcaAsUserFile` (OM) exportiert eine DCA als
User-File.

**ETS3-Kompatibilität:** `EIBA.Interop.EtecDOM/EtecHawk` (COM):
`IEagleDomPersist` (`GetEagleObject`, `ConvertCookieToPath`,
`GetObjectByCookie`, `DumpAllEagleObjects`), `IContextMenu`
(`QueryContextMenu`, `InvokeCommand`, `GetCommandString`),
`IDeviceParameterDialogCorrected` (`SetObjects`, `ShowModal`),
`EtecRedirection` (`_controlfp`), `PluginWrapper` (`ProbePluginInterfaces`,
`ShowParameterDialog`, `PrepareHawkPluginAccess`, `CreateDownloadPluginWrapper`),
`StrongNameChecker`, `PluginInstaller` (`IsEts3Plugin`, `InstallEts3Plugins`,
`InstallDll`, `InstallMsi`, `AnyPluginRequiresElevation`,
`RegOpenKeyEx`/`RegCloseKey`), `WorkaroundsForSpecialProducts`,
`CompatibilityNotEnabledHostFactory`/`PluginHost`
(= `NoCompatibilityModePlugin`).

### 6.3 Hosting-Grenzen

| Grenze | Beleg |
|---|---|
| Prozess ist **32-bit** | `ETS6C.exe` PE machine `0x014C` (32-bit); verwaltete Assemblies sind AnyCPU, der Host ist es nicht |
| In-Process, keine Sandbox | `PluginHost`/`PluginWrapper` im ETS-Prozess, direkter DOM-Zugriff |
| .NET Framework 4.8 + COM | `EIBA.Interop.*`, `Compatibility`-Layer, `Knx.Ets.Sdk.AddIns` |
| Bindung an Applikationsprogramm | `ApplicationProgramKey` (`M-…_A-…-VV-II`), `plugins.json.applicationPrograms` |
| Baggages müssen installiert sein | `BaggageInstallationState`, `BaggageInstaller`, `PluginInstallationState.Baggages*` |
| Zustandsmodell | `PluginInstallationState` = `NotTryedYet`, `NoBaggages`, `NoBaggagesToInstall`, `BaggagesInstallError`, `CacheInitializedButBaggagesNotInstalled`, `BaggagesNotInstalled`, `BaggagesInstalled`, `CompatibilityModeRequired`, `PluginCacheMismatch`, `PluginInstantiationError` |
| Kompatibilitätsmodus kann fehlen | `ApplicationSettings.IsCompatibilityMode`; `CompatibilityNotEnabledHostFactory` |
| Upgrade-Hürde | Store-Update konvertiert und invalidiert Caches (`CacheInitializedButBaggagesNotInstalled`, `PluginCacheMismatch`) |
| Signatur | `StrongNameChecker`, `SignatureMismatch` |
| Elevation | `AnyPluginRequiresElevation`, MSI/Registry |
| UI-Thread | `ViewModel.Service.PluginService.CallActionInMainThread`/`CallBoolFuncInMainThread` |
| Kein Netz-/Datei-Sandboxing belegt | keine Capability-/Permission-API in SDK |

### 6.4 Machbarkeit für BusChord

- **Erkennen und erklären: ja.** `plugins.json` + `applicationPrograms`-IDs
  reichen, um pro Gerät „Plugin nötig / welches / Zustand“ anzuzeigen, ohne
  Plugin-Code zu laden. Das ist der größte Nutzen bei minimalem Risiko.
- **Hosting: nein, nicht in-process.** Ein 32-bit-.NET-Framework-COM-Host im
  BusChord-Prozess würde Fremdcode mit vollem Projektzugriff, UI-Thread-Zwang
  und CLSID-Registrierung bedeuten; BusChord ist net9.0/Avalonia, teils x64.
  Ein separater 32-bit-Hilfsprozess mit explizitem Auftrag (nur
  `ShowParameterDialog`, nur Geräte-ID) wäre die einzige vertretbare Variante —
  hoher Aufwand, unklarer Nutzen, Lizenzfragen offen.
- **Nicht nachbauen:** Download-/Image-Erzeugung der Plugins, DCA-Host-API,
  ETS3-COM-Brücke.
- **Empfehlung:** Plugin-Status als **read-only Warnung** implementieren
  (`HasPlugin`, `NoDownloadWithoutPlugin`, `RequiresExternalSoftware`,
  `BaggageInstallationState` aus Katalog, nicht aus Installation),
  Download für solche Geräte blockieren und das klar benennen.
  **Confidence:** Verträge observed; Laufzeitverhalten von Plugins unknown.

---

## 7. Produktaustausch und Kompatibilität

`ReplaceProductOperation` (OM, observed) hat zwei Ctors (7/8 Parameter),
`SetDescription`, `GetValidators`,
`GetCommunicationObjectsNeedingMapping`,
`CreateNewDevice`, `CopyAdditionalDeviceData`. Call-Edges belegen:
- Mapping über `ComObjectInstance.ModuleInstance.MultiDimensionalOrder`,
  `ComObjectRefInfo.Id`, `ComObjectInstance.DatapointTypes`;
- Neuanlage über `CreateDeviceFromTemplateOperation`/`CopyDeviceOperation`,
  `ExecuteReturnCreated`, `DetermineSecurityImpacts`, `Validate`;
- Übernahme von `WriteFlag`, `ReadFlag`, `UpdateFlag`, `CommunicationFlag`,
  `TransmitFlag`, `ReadOnInitFlag`, `Priority`, `DatapointTypes`, `ObjectSize`,
  Gruppenadress-Verknüpfungen (`LinkGroupAddressOperation`);
- Lizenzprüfung `ILicenseAndAppInformation.IsReplaceDeviceLicensed`;
- Logger-Warnungen bei nicht übernehmbaren Daten.

Validatoren: `ReplaceProductValidator` (Projektzugehörigkeit,
`Hardware2ProgramInfo.RegistrationStatus`),
`CheckCompatibilityValidator` (`ApplicationSettings.IsCompatibilityMode`),
`CheckCompatibilityForDeviceInfoValidator` (Plugin
`HandlesGroupCommunicationLimits`),
`CheckCompatibilityForHardware2ProgramInfoValidator`
(`Ets3DataHandler`-Guid, `ApplicationProgramCache`).

**BusChord-Lücke:** `src/Knx.Core/ReplaceProduct.cs` (43 Zeilen) tauscht
Referenzen und behält ComObjects/Links; kein Mapping, keine Flags, keine
Lizenz-/Registrierungsprüfung, kein Undo-Verbund, keine
Kompatibilitätswarnung. **Empfehlung:** bis zum Mapping als reinen
Referenzwechsel kennzeichnen, bei abweichenden ComObject-IDs/DPTs warnen und
Alternative (Gerät neu + Links manuell) anbieten. **Confidence:** observed.

---

## 8. Unbekannte/opake Daten

Folgende ETS-Modellteile haben in BusChord kein Gegenstück und dürfen beim
Speichern nicht verloren gehen:

- **XML-Programm:** `Extension`, `Script` (mit `PreProcess`), `Options`,
  `BinaryData`, `Code` (Absolute/Relative Segmente), `Allocators`, `Union`,
  `ModuleDef`/`ModuleDefUnion`/`ModuleDefParameter`, `SecurityRoles`,
  `BusInterfaces`, `LoadProcedures` (+ `LdCtrl*`), `FixupList`, `Messages`,
  `DeviceCompare`, `AddressTable`, `AssociationTable`, `Semantics`,
  `IPConfig`, `PeiType`, `MaskVersion`.
- **Parameter:** `ParameterType.Plugin` (Parameter-Typ-Plugin!),
  `ValidationErrorRef`, `ParameterRef.Semantics`/`Tag`,
  `ParameterTypeRestrictionEnumeration.BinaryValue`,
  `ParameterUnionCollection`, `ParameterTypeRawData.MaxSize`,
  `ComObjectParameterBlock.ParamRefId`/`Cell`/`Inline`,
  `ApplicationProgramScript`.
- **Katalog:** `OrderNumberFormattingScript`, `CompatibilityGroup`,
  `NonRegRelevantDataVersion`, `RegRelevantDataShortHash`, `Hash`,
  `RegistrationStatus`, `ReplacesVersions`, `SlReady`, `Broken`,
  `DownloadInfoIncomplete`, `AdditionalCatalogInfo`, Übersetzungen.
- **Plugins:** CLSID-/Typ-Bindung, Baggage-Pfade/-Versionen,
  Per-Application-Programm-Attribute, `PluginRemovedByConverter`.
- **Dotfuscator:** interne Namen/Strings sind nicht rekonstruierbar; jede
  BusChord-Implementierung, die auf Strings baut, ist nicht belegbar.

ETS-Verhalten bei unbekannten Shapes ist **nicht** „ignorieren“:
`BrokenShapeExchanger` tauscht sie beim Import explizit (und undo-fähig),
`Broken`/`DownloadInfoIncomplete` markieren Produkte, `CheckAgainstXmlSchema`
prüft gegen XSD. Für BusChord heißt das: unbekannte XML-Knoten und -Attribute
müssen erhalten und als „nicht bearbeitbar“ markiert werden, nicht normalisiert.

---

## 9. Rangliste der Lücken

| # | Lücke | Schwere | Aufwand | Empfehlung |
|---|---|---|---|---|
| 1 | Geräteparameter werden nicht im Projekt persistiert (nur Sitzung) | kritisch | mittel | Instanzwerte an `DeviceInstance` binden, Roundtrip-Test |
| 2 | Produktaustausch ohne Mapping/Flags/Lizenzprüfung | hoch | mittel | warnen + Mapping für gleiche ComObject-IDs |
| 3 | Kein Katalog-Update/-Löschen, nur neuer Hash-Eintrag | hoch | mittel | Update per Produkt-ID, Store-Operation |
| 4 | Keine Baggage-/Plugin-Erkennung | hoch | klein | Status aus Katalog-XML + `plugins.json` anzeigen, Download blockieren |
| 5 | Kein Seiten-/Kanal-/Blocklayout im Dialog | mittel | mittel | Kanalbaum + Blöcke + Separatoren |
| 6 | Dynamik ohne Module/Repeat/Text-Refs/Allocators | mittel | groß | Text-Refs zuerst, Module nur bei Bedarf |
| 7 | Keine Skript-Berechnung/-Validierung | mittel | groß | nicht evaluieren, klar „nicht unterstützt“ |
| 8 | Keine XML-Regelprüfung (XSD, Unions, RefIds) | mittel | klein–mittel | `ErrorCode`-Teilmenge für vorhandene Modellteile |
| 9 | Kundenzugriff/Reset/Mehrfachauswahl fehlen | mittel | klein | `Access`/`GrantUseByCustomer`/`IsDefault` semantisch trennen |
| 10 | Kein Online-Katalog (Markt/Sprache/Index) | niedrig | groß | erst nach Signatur-/Sprachmodell |
| 11 | Kein Parameter+Flags-Transfer, kein Druck, keine Kontexthilfe | niedrig | klein | nach 1–4 |

## 10. Evidenz-Index

| Aussage | Evidence |
|---|---|
| PDT-Verträge (`IUIParameter`, `IUIParameterRoot/Tree/Separator`, `IParameterProvider`, `IUILayout*`, `IUIDevice`, `IUIChannel`, `IDeviceParameterConfigurationFlags`) | `ev_2630ab40c55643047bb428fc68b5552a13c094a3bdd545c776aa3d5f4b07ae5e` |
| PDT-Enums/Editoren (`ParameterAccess`, Hints, Time/Date/IP, `MultipleSelection*`, `IUIButton`, `IIconProvider`) | `ev_b1f61861e00119cc33af501f0b7436d664b8858fdba8088c07143760c55aa39e` |
| `ParameterDialogViewModel`, `PageNodeViewModel`, `LayoutElementViewModel`, `ParameterList/Grid/TableViewModel`, `ParameterBuilder` | `ev_a762772ad9f4423ffef9a3ade05c7b75eed2ab2b33ae933699b0518b4f1ab644` |
| `ParameterViewModel` + 14 typisierte Editoren | `ev_3adba5ca128dabf4a311103f906a13cc2c18a19e3cf38e6eee0c1ca797f7df2f` |
| Katalog-Provider-Verträge, Quellen, Filter, TreeBuilder, Downloader | `ev_4650a58101a6f677871bbcf8441fddc9d56bb327f5701cc3a8c9599c7c670fc3` |
| Katalogknoten/Vorschau, Hersteller, Abschnitt, Online-Settings/-Store/-Updater | `ev_68aa67989661ff1ad76445afcbc8bccde45e452dd5cc83431fdf46afd6872ca5` |
| Store-Update-Basis (Schritte, Ressourcen-Keys, CvNext, ApplyMasterHash) | `ev_6330dd2e45f5cb82365bcb5e5d4bc59302cd922d777826098ea73c21daa2a990` |
| `UpdateGlobalProductStoreOperation`, `UpdateNonRegRelevantDataOperation` | `ev_55d0527a66feda1194c7274d414d738c3215b08a786531f94a6325f65af7716b` |
| `DeleteCatalogItemsFromProductStoreOperation` | `ev_9039feb388e3164a2223143de678d1815aaf5067d39d943ac1c0ca4fb1740558` |
| Import-Pipeline, `BaggageInstaller`, `BrokenShapeExchanger`, `ImportInformationCollector` | `ev_232a3d7d88e4c626bdb316f4f3a47587c43cab14c654cb153667399e3a50df9f` |
| Online-Index (`IndexFile*`, `Downloader`, `CatalogLifeType`) | `ev_ef0835c7a7c8eef80e93eeef3e3123f924d3565f51610c165006e5b7525d2e73` |
| XML-Parameter-/Blockmodell (`Parameter`, `ParameterRef`, `ParameterInstance`, `ParameterSeparator`, `ComObjectParameterBlock`) | `ev_1b40f074485a9047be4b80cb81b054be092d58aa7c114ea86eede6d1fa81c006` |
| Choose/When/Assign-Deklarationen, `WhenCondition`, `ParameterSeparatorUIHint`, `ParameterBlockLayout`, `CheckChooseWhen` | `ev_f8cba79e4a13bd41096366b1b81d06851c5f1b2f8f9bbfbfd5b067f0212aba88` |
| Parametertypen (`TypeNumber/Float/Text/Restriction/Time/Date/IPAddress/Picture/Color/RawData`), `ParameterCalculation`, `ParameterValidation` | `ev_7eff31c27eb097ff9883541fb10b4f23aee7b741c25f90155a1e2b5e5344871e` |
| `ApplicationProgramStatic` (Container), `UnionParameter`, `ModuleDefParameter`, `ApplicationProgramScript/Code/BusInterface` | `ev_955314a0fdbc80cf983bdaba17a87acad8e088f61a3f2a142e179746b5b17df0` |
| `ApplicationProgram` (Static/Dynamic/ModuleDefs, Limits, Status), `ApplicationProgramChannel` | `ev_38557d090a547139efe8d015c3eae226564bc88907e50b0aa98f8a075f4fd458` |
| Prüf-Framework, `CheckSeverity`, `ErrorCode`, `ICheck`, `CheckAgainstXmlSchema` | `ev_2b1540fda576d69851a0e4ad750c9fc638210b0482990a17ddba663bbce806fd` |
| Dynamik-State-Machine (`DeviceActivityCalculator`, `Informer`, `Chooser`, `Assign*`, `Repeat/Module`, Facaden) | `ev_65fd90837a5a423e4a43c48a040024134c841575c89f55f57d7e2313dc17c1d2` |
| Skript-Engine/-Validierung (`ParameterCalculationSystem`, `ScriptingEngine`, `Managed*`, `ActiveScript*`) | `ev_e661c72cfc3175129a03dffaa303f1dda8e175a5857fd078f42c9ae1046af945` |
| SDK-Plugin-Basen, Capabilities, Executions | `ev_3d74dad9784b3a84bef2e24bbe4f22ca08bfeecf5d3a815383ca9bd47edf4c2c` |
| `DcaExecutionContext`, `DcaLifeCycleEventReasons`, `IHostContext`, `UnifiedCatalog*` | `ev_c26cf2724a6fae382e2c99fc371eb67aafcbadb67c7161e2b57bc962a0121b3b` |
| `PluginRepository` (Repository/PluginInfo/Keys/Capabilities) | `ev_5c626d50780d9520ecc2ad17bc362a3110099c6f1fa40d7e5afe5d233f9e22af` |
| `PluginInterfaces`, `PluginType` (Enum-Werte) | `ev_c1a14a922148a85e57cc401e418f077f0ff8750ac36037e44ffba581c5b90762` |
| `PluginInstallationState`, `PluginInstallationError`, `ExportDcaAsUserFile` | `ev_9d1f212821b81fc18bc91ba595b6c0d4a20d3b9b6cecb15cef8d86d053d083fd` |
| `ApplicationSettings` (`IsCompatibilityMode`), `IDevicePlugin` | `ev_b0e6fd2a0839b1df6b8ccd86798dd8b8ad4b0889009533aac1f5e2d5a9feb567` |
| Plugin-Host/Bridge, `Dca`-nahe Compat-Typen, ETS3-COM | `ev_4bef8d215bbaffa11752eab44204634e6d8ca8899562293c598898af51e895e0` |
| `ReplaceProductOperation` + Kompatibilitäts-Validatoren | `ev_97444ccf1d4b4e1d37968ad511e5376de8917f38a4fb68c2f89b9a50b29dd43c` |
| `ICatalogItem`, `Manufacturer`, Import-Enums | `ev_232a3d7d88e4c626bdb316f4f3a47587c43cab14c654cb153667399e3a50df9f` |
| PD-Manifest-Ressourcen (4 Ressourcen, Offsets) | `ev_d94d4e1a25d0728b75c556777581a8d64681540ce141d89aba63e1e38327cc59` / View `ev_48734b60800b8f6cbeb91ea48bd5f1ad005286fd7a01b8462c113cb080016b8f` |
| VM-Brücke (retained, Transport-Limit) | `ev_2c578ecdb2feea1c641a924b6f0b1c3e3dbf7ca6b66881ebe97fcabe8e9363a5`, `ev_bb36188b7e2adaef454bb1e26f98ab4faca6ae3bc71a925cccc24e2f331777b3` |

Offene Punkte: Suchfeldsemantik und UI-Texte des Katalogs (Strings
verschlüsselt), Laufzeitverhalten von Plugin-Loader/-Host, tatsächliches
`knxproj`-Serialisierungsformat der Parameterinstanzen, Druck-/Hilfeablauf,
Verhalten bei mehreren Applikationsprogrammen. Diese Punkte sind mit
statischer Metadatenanalyse nicht auflösbar; dafür wären eine kontrollierte
Laufzeitbeobachtung oder echte Projektdateien nötig.

Prüfung: alle 36 oben zitierten Evidence-IDs wurden mit
`get_evidence_bundle(detail: "summary", filters.evidence_id)` nachgeschlagen —
je genau ein Record, Subjektname und SHA-256 passend zur Artefakttabelle.
Alle Datei-SHA-256 wurden zusätzlich mit `Get-FileHash -Algorithm SHA256`
bestätigt; die PE-Maschine von `ETS6C.exe` wurde direkt aus dem COFF-Header
gelesen (`0x014C`).
