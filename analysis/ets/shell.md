# ETS6-Shell/UX: evidenzbasierte Bestandsaufnahme

Stand: 2026-10-21. Auftrag ETS-5 (REA-Analyse, read-only Produkt).
Basis: `12fc904ee05089976eac93426141c6fdb348ffe1` (`development`),
Branch `agents/ets-rea/shell`, Worktree `C:\projects\ai_work\agent-wt\ets-rea-shell`.
Geändert wird ausschließlich diese Datei. Kein Build, kein Produktstart, kein
Netzwerk-, Bus- oder Lizenzzugriff.

**Methode.** Statische Analyse der installierten ETS6 6.4.8718.0
(`C:\Program Files (x86)\ETS6`): REA `inspect_managed_artifact` /
`inspect_managed_members` (Metadaten, Typ-/Methoden-Tokens, Datei-Offsets,
Call-Edges) sowie read-only Auslesen der eingebetteten `.g.resources` und der
Satelliten-Ressourcen (`System.Resources.ResourceReader`, keine Ausführung von
Produktcode). Zusätzlich wurden die BAML-Eintragsnamen und -Rohbytes
(Token-Scan) gelesen. Vorhandene Dokumente (`analysis/ux/*`,
`analysis/ets-features.md`, `analysis/workbench-v4-*`) dienten als Vorlauf; die
hier zitierten Aussagen sind – soweit nicht anders markiert – in dieser
Untersuchung neu belegt.

**Grenzen.** Keine Laufzeitbeobachtung, keine Decompilation, keine
Klickfolgen. „Sichtbarer Workflow" ist aus Ressourcen, Strings, Typen und
Call-Edges *rekonstruiert*, nicht beobachtet. BAML wurde nicht vollständig
dekompiliert; Aussagen zu XAML-Attributen stützen sich auf das Vorkommen der
Attributnamen in den BAML-Rohbytes (Trefferzahlen, keine Struktur). Einige
Typen fehlen im lokalen Reflection-Inventar (Loader-Fehler); sie wurden über
REA nachgewiesen (`PanelContentViewModel`, `PanelListViewModel`,
`IPanelContentViewModel`, `AboutSettingsPageViewModel`).

---

## 1. Ergebnis in Kurzform

1. **Die Shell ist ein Telerik-`RadTabbedWindow`-Fenster pro Projekt/Ansicht.**
   `ETS.Services.RadTabbedWindowAdapter` kapselt `RadTabbedWindow`
   (Titel, `Close`, `BringToFront`, `Activated`/`Closed`/`Closing`);
   `windows/mainwindow.baml` und `resourcedictionaries/radtabbedwindowdictionary.baml`
   (34 KB) liefern die Optik. Ein Ribbon kommt in den BAML-Ressourcen nicht vor
   (0 Treffer für `RibbonBar`/`RadRibbonView`).
2. **Drei Tab-Ebenen, sauber getrennt:** Projekt-/Ansichts-Tabs
   (`MainWindowViewModel.Tabs`, `TabId`/`ProjectTabId`), darin **Arbeitsfenster**
   (Content-Panels, `PanelId`, höchstens **sechs** offene Panels,
   `MoreThanSixWorkPanelsNotAllowed`), darin **Detail-Reiter** der Listenansicht
   (`SelectedDetailTab`, `DetailTabItems`). Die rechte Sidebar ist ein eigener
   Container-Typ (`SidebarContentTypes`), kein Panel.
3. **Panelauswahl ist ein Modell, kein Menü:** `PanelListViewModel` +
   `PanelContentDescriptor` + `PanelsButtonViewModel`; Aktionen „Open as New
   Tab", „Open as New Window", „Open in a new Panel", „All Panels".
4. **Layouts sind benannte, dateibasierte Workspaces** (`WorkspaceSerializer`,
   `WorkspaceCreator`, `IWorkspaceCollection`, `WorkspacePath`,
   `WorkspaceFileExtension`, Import/Export-Dialoge, „Restore Default Workspace",
   „Last used/active workspace").
5. **44 konfigurierbare Tastenkürzel** in `KeyboardShortcuts` (je
   `SerializableKeyGesture`), gruppiert (`GestureGroupType`), mit
   Reserviert-Prüfung (`IsKeyGestureReserved`) und Reset
   (`ResetShortcutSettingsMessage`, „Do you want to reset all shortcuts?");
   dazu 45 Kürzel-Tooltips (inkl. nicht konfigurierbarer Befehle).
6. **Panel-Favoriten** sind ein eigener Dienst (`PanelMenuManager.SetFavorite`,
   `GetFavoriteOrderIndex`, Event `PanelFavorabilityChanged`) und werden in eine
   **Datei** persistiert (`Favorites`, `LoadFavoritesFailed`,
   `SaveFavoritesFailed`); daneben `FavoriteType.App` mit `IAppMenuManager`
   (`PinnedItems`, `ManufacturerItems`).
7. **Kontext-Hilfe existiert** (`IHelpProvider.OpenHelp`,
   `ISettingsHelpProvider.DisplayHelpText`/`HideHelpText`,
   `ContextHelp.HelpEventArgs`, Kürzel „Help", „Open a context help").
8. **Navigation hat eine echte Historie** (`NavigationHistoryManager` mit
   Backward/Forward, `NavigationState`: Paneltyp + Detail-Tab-Typ + Tree-Pfade +
   AddIn-Id, lizenzabhängig über `IsLicensed`).
9. **Options ist eine Seite, kein Dialog:** `EPages` mit 11 Seiten (About, Apps,
   ConnectionInterface, DataStorage, Labels, Language, Licensing, OnlineCatalog,
   Presentation, Shortcuts, Troubleshooting).
10. **Undo ist Store-basiert** (`IUndoManagerStoreProvider.GetStore/NewStore/RootPath`,
    `IMarker.Commit`, `LocalizedDescription`), die Sidebar zeigt die Historie als
    eigenen Container (`SidebarContentTypes.UndoHistory`, `sidebarundohistory.baml`).
11. **Barrierefreiheit ist vorhanden, aber dünn:** `AutomationProperties` in 49
    BAML-Dateien, `KeyboardNavigation` in 37, `AccessText` (Mnemonics) in 8,
    `TabIndex` in 10, `IsTabStop` in 2 – **keine** `AutomationPeer`-Typen in den
    Shell-Assemblies.
12. **ETS6C ist der Kompatibilitätsmodus-Host**: eigene 64,8-MB-Binary, eigener
    SHA, gleiche Version; Strings „This device contains plug-ins requiring ETS
    running in Compatibility Mode", „restart ETS in Compatibility Mode".

---

## 2. Artefakt- und Evidenzregister

Alle Versionen `6.4.8718.0`, .NET Framework 4.8, `x86`.

| Kürzel | Datei | SHA-256 | MVID | Typen/Methoden | REA-Evidenz |
|---|---|---|---|---|---|
| EXE | `ETS6N.exe` | `f844b9bfd1001875f2116cb3e9a9dbc37c18b4eed3abed65077f6a37dcc6bb51` | `237e7ec3-6a31-406a-9c4f-1b3927a31b34` | 167 / 1385 | `ev_614d04b27248d753fa96a1dddd02832fc5a65921b54c273f114f4c94afcb7953` (Artefakt), `ev_396058841ddfff4cab7f25c31064f2faf4374a70c4770aec3969203a98c9b65c` (App/CommandHandler/Adapter/Factory/LocalizationManager/Startup) |
| VM | `Knx.Ets.ViewModel.dll` | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` | `b557be5c-0e7f-4bc3-aa94-958fba394aba` | 5360 / 46055 | `ev_8e5d5386a5e0468631e2d39bc82bb8b20fe9ee5d904b30515bb9336e94e4de7e` (Artefakt) |
| VM-A | UISettings + Tabs | – | – | 12 Typen / 280 Methoden | `ev_0ee24ac4ecff7cd20eb4ca6842ed430708b36664ed91cd279439c75f80565143` |
| VM-B | Menü/Navigation/Enums/About | – | – | 18 Typen / 105 Methoden | `ev_18626ee087afb748825cf0117b22251b9b9ea57904f400d879f4efc1b94764f7` (Summary `ev_0f9986f56f70a480965baab5881e52376c98a4bab8f3b1aca55e3c524b9902d3`) |
| VM-C | MainWindow/PanelModel/Help | – | – | 17 Typen / 473 Methoden | `ev_e35221d1f3f62a11dfac069d271a052dffc461cf6e2722729c054f89b57efff7` (Summary `ev_4f140ea89feb93bb4ead69b56fa4537b4eed1d3a5cef06cf62166d56b04394ab`) |
| UC | `Knx.Ets.Ui.UserControls.dll` | `2488a5a951e772af6530ecdab351f2d8b1cf9fbfd079b13ec56c0b91efbbdf8a` | `85ede4c8-af1c-4751-b839-cf969c90ddac` | 1175 / 10864 | `ev_f8160644359d529fde738db7d5ceb5bb71a2a4a024751387f45b36835ec8af11` |
| PF | `Knx.Ets.Ui.PresentationFramework.dll` | `519df828e34e5f43ff1a0e4d4e65358da5a6510b2344cc009040bbb9c9e70397` | `a3846cbe-7774-4bd2-8bd7-0f59f0a7ea0c` | 157 / 1103 | `ev_ef481ed2c3fc70f6dacc8d7a65d8bb7ce9418c4e33f35f9884b29855cfa56ddc` |
| LOC | `Knx.Ets.Localization.dll` | `5aebb3f83611e9fd6f48a1e29becd598ed12ea2f42c84362cf567e7b87674e06` | `08756f91-7762-4ed9-b142-e91978512936` | 37 / 1606 | `ev_50cef6410d321ec5348b39e28486fa00430a1223547d114d7072ecdfb0519db0` |
| OM | `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` (SHA aus `parity-rea-audit.md`) | – | – | `ev_1aa1482d07f52b90569614b2c1b1085e0d9764c240feb8feb67b6da3824ac10b` (Undo/Notification), Parent `ev_2ea102357764df1b8e37b1b5d51d5ef1c5e26a40cce4f9b98c793154516b10e5` |
| EXE-C | `ETS6C.exe` | `3d490d0bfc3e555dac2864f73df761879494ad36877b41efe34cb915b8b52da6` | – | 64.801.232 B | nur `Get-FileHash`/Größe (kein REA in dieser Untersuchung) |
| EXE-Launcher | `ETS6.exe` | `1d55e10697d4af36f4d497610d43400e7029f1eeeee5332e2ead9d4a8013d6644` | – | 116.688 B | dito |

**Read-only-Ressourceninventar** (gleiche Dateien, kein Ausführen):

| Ressource | Einträge | Inhalt |
|---|---|---|
| `Knx.Ets.Ui.UserControls.g.resources` | 740 | 740 BAML-/Asset-Einträge (Views, Dictionaries, PNGs) |
| `Knx.Ets.Ui.PresentationFramework.g.resources` | 15 | Theme-/Steuerelement-Dictionaries |
| `Knx.Ets.ViewModel.Properties.Resources.resources` | 2812 Strings (en) | UI-Texte, u. a. alle Kürzel-Tooltips |
| `de-DE/Knx.Ets.ViewModel.resources.dll` | 2802 Strings (de) | deutsche Fassung |
| `de-DE/Knx.Ets.Ui.UserControls.resources.dll` | 1254 Strings (de) | deutsche View-Texte (en: 1255) |
| `de-DE/Knx.Ets.Localization.resources.dll` | 1399 Strings (de) | deutsche Fassung (en: 1405) |

Deckt nicht ab: `Knx.Ets.Reports`, `Knx.Ets.ObjectModel`-Ressourcen (nur Typen
betrachtet), Plugin-/AddIn-Ressourcen.

---

## 3. Querschnittsarchitektur

```
ETS6N.exe (ETS.App, ETS.Startup, ETS.EtsBootstrapper)
 └─ IMainWindowService ──► MainWindowViewModel ──► RadTabbedWindow (Telerik)
      ├─ Tabs: ITabViewModel (TabId | ProjectTabId)         ← Projekt-/Ansichts-Tabs
      │    └─ ITabContentViewModel
      │         └─ PanelListViewModel                       ← Arbeitsfenster-Satz eines Tabs
      │              ├─ PanelContentViewModel (Contents, PanelId)
      │              │    ├─ TreeView  (Liste)
      │              │    └─ DetailTabItems / SelectedDetailTab
      │              └─ PanelContentDescriptor (Verfügbarkeit, AddIn/Diagnose)
      ├─ Sidebar (SidebarContentTypes, SidebarExpandStatus)
      └─ Dialogschichten (IsOverlayVisible, DialogContent, FirstDialogLayerAdded …)
```

Belege: `MainWindowViewModel` `0x02000090` @6561458 (85 Methoden, 21 Felder);
`ITabViewModel` `0x02000fd4` @6631802; `PanelListViewModel` `0x02000bfe`
@6614126 (76 Methoden); `PanelContentViewModel` `0x02000bee` @6613838
(161 Methoden, 53 Felder); `IPanelContentViewModel` `0x020006e5` @6590636
(45 Methoden) – Quelle `ev_e35221d1…`.

**Shell-Host.** `ETS.App` (`0x02000005` @238296) mit `OnStartup`
(`0x06000012`/Datei-Offset 1788), `Main` (`0x06000022`/7144),
`InitiateAbnormalApplicationShutdown` (`0x0600001a`/5852). Fenstererzeugung
zentral über `IMainWindowService` (`0x02000205` @6568172): `CreateWindow()`,
`CreateWindow(WindowSetting)`, `CreateWindowWithTab(ITabViewModel[, WindowSetting])`,
`CloseWindow`, `GetActiveWindow`, `GetAllWindows`, Events
`WindowCreated`/`WindowClosing`/`WindowClosed`/`LastWindowClosed`.

**Befehlsbindung.** `ETS.ApplicationCommandHandler` (`0x0200000b` @238404,
129 Methoden) hängt sich per `Attach(Window)`/`Attach(UIElement)`
(`0x0600004a`/9576, `0x0600004b`/9784) an die Oberfläche und bedient
`ExecutedRoutedEventArgs`/`CanExecuteRoutedEventArgs`; Kontextläufe über
`RunInPanelContentContextMenu`, `RunPanelContentAction`, `Attach/Detach`.
Zustände `IsBlockedByDialog`/`IsNotBlockedByDialog` verhindern Befehle bei
offenen Dialogen. Abhängigkeiten im Konstruktor (302 IL-Befehle) u. a.
`IMainWindowService`, `ITabCreationService`, `IWorkspaceCollection`,
`IHelpProvider`, `IProjectService`, `IShutdownHandlingService`,
`IEtsUserSettings` – das ist der tatsächliche Befehlskontext der Shell.

**Ereignisbus der Shell** (`ApplicationMessages+…`): u. a.
`ClosedLastPanelOfTabPageMessage`, `MovePanelBacktoTabMessage`,
`RefreshContentPanelSizesMessage`, `SetStatusBarTextMessage`,
`SwitchToProjectOverviewMessage`, `SwitchToProjectArchiveMessage`,
`ResetShortcutSettingsMessage`, `SettingsResetMessage`,
`ApplicationClosingMessage`, `ShutdownApplicationMessage`.

---

## 4. Feature-Inventar

Legende Konfidenz: **A** = aus Typen/Strings/Ressourcen direkt belegt,
**B** = aus Typen + Strings schlüssig abgeleitet, **C** = Vermutung.

### S1 Shell-Fenster, Dialogschichten, Statusleiste

- **Workflow (B):** Ein Fenster trägt Projekt-/Ansichts-Tabs; Dialoge liegen als
  Overlay-Schicht über dem Fenster (`IsOverlayVisible` `0x06000584`/139108,
  `DialogContent` `0x06000587`/139300) und werden gestapelt
  (`PreviewFirstDialogLayerAdded`, `PreviewLastDialogLayerRemoved`).
  Statusmeldungen laufen über `SetStatusBarTextMessage` in
  `controls/projectstatusbar.baml` (+ `resourcedictionaries/statusbardictionary.baml`).
- **UI-Ressourcen:** `windows/mainwindow.baml`, `bars/menubar.baml`,
  `resourcedictionaries/radtabbedwindowdictionary.baml`,
  `resourcedictionaries/maintoolbardictionary.baml`,
  `controls/projectstatusbar.baml`, `controls/wunderbar.dictionary.baml`
  (Wunderbar = Werkzeugleiste über der Breadcrumb),
  `resourcedictionaries/breadcrumbdictionary.baml`.
- **Typen:** `MainWindowViewModel` (Titel `0x06000583`/138880, Breite/Höhe
  `0x0600058c`/140536, `0x0600058e`/140980, `IsModal`, `IsLocked`,
  `IsClosed`, `Refresh` `0x06000596`/141884), `MainWindowViewModel.Setup`
  `0x060005a2`/145308, `MainWindowServiceExtensions`,
  `EtsWindowSettings.WindowState` (`EtsWindowState`).
- **Strings:** keine eigenen Fenstertitel in den Ressourcen (Titel kommt aus
  Projektname/Tabs); Statusleiste über `SetStatusBarTextMessage` codegetrieben.
- **Persistenz:** `EtsWindowSettings.WindowState` in den Benutzereinstellungen.
- **Randfälle:** `CanClose`/`UserAllowsCloseContent` (`0x0600059e`/143984,
  `0x0600059d`/143808) Fragen vor dem Schließen ab; `LastWindowClosed` beendet
  die Anwendung; `ResetModalWindows` (String) setzt hängende Dialogschichten
  zurück – belegt, dass ein solcher Zustand real auftritt.
- **BusChord-Lücke:** kein Overlay-/Dialogschichtmodell, keine Statusbar-Senke.
- **Empfehlung:** Ein `ShellViewModel` mit Tabliste + Dialogschicht-Stack; keine
  eigene Fensterklasse pro Dialog.

### S2 Dashboard (vor dem Projekt)

- **Workflow (B):** Dashboard ist selbst ein Tab (`MainWindowViewModel.AddNewDashboardTab`
  `0x06000597`/142140, `SelectDashboardTab` `0x060005ab`/147344,
  `ITabViewModel.IsDashboardTab`). Inhalt: Projektliste (Kacheln/Liste),
  Breadcrumb-Navigation in lokale Projekte/Archiv, Quick-Actions, Suche,
  rechte Drilldown-Sidebar, Einstellungen, Hilfe.
- **UI-Ressourcen:** `dashboard/dashboard.baml`, `dashboardhomeview.baml`,
  `dashboardsimplelist.baml`, `projectlistcontrol.dictionary.baml` (19,9 KB),
  `dashboard/quickactions/quickactionbar.baml`,
  `dashboard/breadcrumb/breadcrumbbar.baml`,
  `dashboard/localprojectsdrilldownview.baml` +
  `localprojectsdrilldownsidebarview.baml`,
  `dashboard/archivedrilldownsidebarview.baml`,
  `archiveprojectversionsdrilldownview.baml`,
  `dashboard/searchresultsdrilldownsidebarview.baml`,
  `dashboard/search/nomatchingsearchresults.baml`, `dashboard/tags/tageditor.baml`,
  `dashboard/settings/*` (29 BAML-Einträge),
  `dashboard/productplacement/marketingcarousel.baml`.
- **Typen:** `Application.Tabs.TabDashboard` (Async-Zustandsmaschinen u. a. für
  `NavigateToLocalProjectsDrillDownAsync`, `NavigateToArchiveProjectsDrillDownAsync`,
  `HandleRootBreadcrumbItemClickedAsync`, `OnTileClicked`),
  `DashboardSearch`/`DashboardSearchInput`/`DashboardSearchResult`
  (`SearchText`, `ActiveTagFilterItems`, `ActiveLocationFilterItem`,
  `QuickSearchItemCounts`), `ProjectListPresentationMode` {`ListView`,
  `TileView`}, `ProjectSortStrategy`, `QuickSearchLocations`,
  `LocalProjectTileViewModel`, `ArchiveProjectTileViewModel`,
  `ArchiveProjectVersionTileViewModel`, `BreadcrumbBarViewModel<T>`.
- **Strings (en | de):** `DashboardNewProject` „New Project" | „Neues Projekt";
  `TabNameProjects` „Your Projects" | „Ihre Projekte";
  `DashboardSidebar_LocalProjects`/`_Archive`/`_ImportProject`/`_ExportProject`/
  `_NewFolder`/`_Filter`/`_All`; `Dashboard_Settings` „Settings" |
  „Einstellungen"; `Dashboard_Help` „Help" | „Hilfe".
- **Persistenz:** Präsentationsmodus in `EtsUserSettings.Presentation`
  (`ProjectListPresentationMode`), Tags in `EtsUserSettings.UserProjectTags`.
- **Randfälle:** `BrokenProjectTooltip` („Project files appear corrupted…"),
  `UpdateRequiredTooltip` („created with a newer ETS version"), Kachel-Badges
  (`dashboard/tile/badge/badge.dictionary.baml`).
- **BusChord-Lücke:** kein Dashboard; Start ist `SplashWindow` +
  `NewProjectWindow` + Recent-Liste im `MainWindow`.
- **Empfehlung:** erst nach Tab-/Panelmodell; Dashboard als regulärer Tab, nicht
  als Sonderfenster.

### S3 Projekt-/Ansichts-Tabs

- **Workflow (A/B):** Jedes Projekt und jede zusätzliche Ansicht öffnet einen
  Tab. Tab-Operationen: `AddTab` `0x06000598`/142372, `InsertTab`
  `0x06000599`/142764, `RemoveTab` `0x0600059a`/143084, `CloseTab`
  `0x0600059b`/143376, `ContainsTab` `0x0600059c`/143628, `CanClose`
  `0x0600059e`/143984, `Close` `0x0600059f`/144296,
  `OpenPanelInNewTab` `0x060005a6`/146404,
  `SelectNextTab`/`SelectPreviousTab`/`SelectDashboardTab`
  (`0x060005aa`/147076, `0x060005a9`/146804, `0x060005ab`/147344),
  `GetNewTab` `0x060005a8`/146672, `GetNewProjectTabContent` `0x060005a7`/146504,
  `AddTabMenu`/`OpenAddTabMenuCommand` (`0x06000579`/137796, `0x0600057c`/137908).
- **UI-Ressourcen:** `resourcedictionaries/projectstab.baml`,
  `resourcedictionaries/radtabbedwindowdictionary.baml`,
  `themes/sidetabcontrol.baml`, `databindings/menuitem.databindings.baml`.
- **Typen:** `ITabViewModel` (`Content`, `Header`, `IsDashboardTab`,
  `IsSelected`), `TabId` (`Guid Id`), `ProjectTabId : TabId` (+`ProjectId`),
  `PanelId` (`Guid Id`), `ITabCreationService` (`OpenProject` ×2,
  `AddAnotherProjectTab`), `ProjectTabPageDragDropBehavior` +
  `RadTabControlDragDropPeer` (Tab-Drag), `SelectedTabChangedEventArgs`,
  `NoActiveTabException`, `ClosedLastPanelOfTabPageMessage`.
- **Persistenz:** Tabzustand ist Laufzeitzustand; nur die *Fenster*-Position
  (`EtsWindowSettings`) und Workspaces überleben.
- **Randfälle:** letzter Tab/Panel schließt → `ClosedLastPanelOfTabPageMessage`;
  Schließen fragt über `CanClose`/`UserAllowsCloseContent`; die API
  `IMainWindowService.CreateWindowWithTab` erlaubt ein neues Fenster mit
  vorhandenem Tab – der Drag-vom-Tab-ins-Fenster ist nur über die
  Vorlauf-Doku (`analysis/ux/ets6-panel-recheck.md`, S3) belegt, nicht hier.
- **BusChord-Lücke:** `WorkbenchTab` existiert (Ziel-basiert, mit
  Scroll/Filter-Status), aber **keine Projekt-Tabs**, kein Tab-Drag in ein neues
  Fenster, kein `Add Tab`-Menü.
- **Empfehlung:** `TabId`/`ProjectTabId`/`PanelId` als Wertobjekte übernehmen;
  Tab-Drag erst nach stabilem Ein-Fenster-Modell.

### S4 Panelauswahl und Arbeitsfenster (Content-Panels)

- **Workflow (A/B):** Pro Tab hält `PanelListViewModel` 1..n Panels
  (`Panels`, `ContentPanels`, `CurrentPanel`, `TabIndex`). Die Panelauswahl
  (`PanelsButtonViewModel`, `SuggestContentSelectorWidth`) zeigt den Panelnamen
  und öffnet die Liste; Aktionen: „All Panels", „Open as New Tab", „Open as New
  Window", „Open in a new Panel". Panels lassen sich teilen
  (`SplitPanelHorizontal`/`Vertical`), maximieren (`MaximizePane`), schließen
  (`ClosePanel`), hoch/runter bewegen (`MovePanelUp` `0x06006b22`/2303088,
  `MovePanelDown` `0x06006b26`/2303220) und aus dem Tab lösen
  (`MainWindowViewModel.OpenPanelInNewTab`).
- **UI-Ressourcen:** `controls/panelcontentselector.baml` (6,2 KB),
  `resourcedictionaries/panelcontentselector.dictionary.baml`,
  `controls/workspace/etsworkplace.baml`, `workplace/contentpanel/contentpane.baml`
  (16,5 KB), `contentpanelcontainer.baml`, `contentpanelheader/*`,
  `assets/contentpanelheader/contentpanel.close.png`.
- **Typen:** `Contents` `0x0200079f` @6593984 – Werte `NotSet` `0x040021f0`,
  `Buildings` `0x040021f1`, `GroupAddresses` `0x040021f2`, `Topology`
  `0x040021f3`, `ProjectRoot` `0x040021f4`, `Devices` `0x040021f5`, `Reports`
  `0x040021f6`, `Catalog` `0x040021f7`, `Diagnostics` `0x040021f8`, `AddIn`
  `0x040021f9`, `RecentlyUsedGroupAddresses` `0x040021fa`.
  `PanelContentDescriptor` `0x02000bec` @6613802 mit
  `MakeAddinPanelDescriptor` (`0x060069dd`/2252004),
  `MakeDiagnosticsPanelDescriptor` (`0x060069de`/2252012), `From` ×2
  (`0x060069df`/2252020, `0x060069e0`/2252052), `RefreshAvailable`
  (`0x060069dc`/2251880). `PanelListViewModel`: `AddPanelsFromPanelSettings`
  (`0x06006b0c`/2297420), `ShowContentPanel` ×2 (`0x06006b1a`/2300580,
  `0x06006b1b`/2300592), `SetCurrentPanel` (`0x06006b18`/2300140),
  `CanClose` (`0x06006b20`/2302532), `ClearPanels` (`0x06006b21`/2302592),
  `SwitchToReportPanel`, `OpenPanel`, `ShowPanel`, `Orientation`,
  `ContainsDiagnosticsPanel`, `HasAddInPanel`, `HasGroupMonitorRunning`.
- **Strings (en | de):** `PanelsMenuText` „Panels" | „Panels";
  `AllPanelsMenuText` „All Panels" | „Alle Panels";
  `OpenAsNewTabMenuText` „Open as New Tab" | „Als neuer Tab öffnen";
  `OpenAsNewWindowTabMenuText` „Open as New Window" | „Als neues Fenster öffnen";
  `OpenInNewPanel` „Open in a new Panel" | „In einem neuen Panel öffnen";
  `MaximizePane` „Maximize Panel" | „Panel maximieren";
  `ClosePanel` „Close Panel" | „Panel schließen";
  Panels: `PanelTopology` „Panel 'Topology'" | „Panel 'Topologie'",
  `PanelBuildings`, `PanelDevices`, `PanelGroupAddresses` („Panel
  'Group addresses'" | „Panel 'Gruppenadressen'"), `PanelProjectRoot`
  („Panel 'Project Root'" | „Panel 'Ganzes Projekt'"), `PanelReports`.
- **Randfälle:** **maximal sechs** offene Panels –
  `MoreThanSixWorkPanelsNotAllowed` „The maximum number of open panels is
  reached." | „Die maximale Anzahl der offenen Panels wurde erreicht."
  (Quelle: UserControls-Ressourcen; die Meldung nennt die Bezugsebene – Tab
  oder Fenster – nicht). Nur ein Gruppenmonitor
  (`HasGroupMonitorRunning`), Diagnose-Panel nur einmal
  (`ContainsDiagnosticsPanel`), AddIn-Panel nur einmal (`HasAddInPanel`,
  `HandleSingleInstanceAddIn` `0x06006b1e`/2301696).
- **BusChord-Lücke:** Dock-basierte Panes statt Panelmodell; kein 6er-Limit,
  keine Panel-Favoriten, keine Panel-Metadaten (`ContentAvailable`).
- **Empfehlung:** `PanelDescriptor` + `PanelList` mit harter Obergrenze und
  Einzelinstanz-Regeln – das ist der Kern, an dem BusChords Dock aktuell
  scheitert.

### S5 Listenansicht und Detail-Reiter

- **Workflow (A):** Jedes Panel hat Baum/Liste (`TreeView`) und darunter bzw.
  daneben Detail-Reiter (`DetailTabItems`, `SelectedDetailTab`,
  `SetSelectedDetailTab` `0x060042f0`, `SelectDetailTabItem` `0x0600430d`,
  `PreferredDetailTabList`, `UserPreferredDetailTabChanged`). Reiter sind
  typisiert (`GetDetailsTabType`, `NavigationState.DetailsTabViewType`).
- **UI-Ressourcen:** `detailview/*.baml` (22 Views, u. a. `areadetailview`,
  `linedetailview`, `devicedetailview`, `channeldetailview`,
  `buildingpartdetailview`, `functiondetailview`, `segmentdetailview`,
  `groupaddressrefdetailview`, `activecomobjectdetailview`,
  `dynamicfoldersdetailview`); `workplace/contentpanel/contentpane.baml`;
  `themes/sidetabcontrol.baml`.
- **Strings:** `TabNameOverview` „Overview" | „Übersicht", `TabNameLines`
  „Lines" | „Linien", `TabNameConnections` „Connections" | „Verbindungen",
  `TabNameCatalogs` „Catalog" | „Katalog", `TabNameSettings`, `UndoHistory`.
- **Persistenz:** bevorzugte Detail-Reiter über `PreferredDetailTabList`
  (6 Methoden) – Ablage in den Benutzereinstellungen (abgeleitet aus
  `UserPreferredDetailTabChanged` + `EtsUserSettings`).
- **Randfälle:** `UpdateTabDependencies` (`0x060042e6`), `ReloadTabs`
  (`0x06004312`), `LastSelectionIsTreeView` (`0x060042f2`),
  `IsParameterDialogSelected` (`0x060042ee`).
- **BusChord-Lücke:** Inspector ist global, nicht panel-lokal; keine
  Reiter-Präferenz.
- **Empfehlung:** Detail-Reiter an `PanelContentViewModel` binden, nicht an das
  Fenster.

### S6 Sidebar-Container

- **Workflow (A):** Die rechte Leiste hat umschaltbare Container:
  `Properties`, `FindAndReplace`, `Workspaces`, `TodoItems`,
  `PendingOperations`, `UndoHistory`, `GroupAddresses`, `Default`
  (`SidebarContentTypes` `0x020007bf`, Felder `0x040022a7`–`0x040022ae`),
  Zustand `Expanded`/`Collapsed` (`SidebarExpandStatus` `0x020007c0`,
  `0x040022b0`/`0x040022b1`). Eigenschaften sind tab-typisiert über vier
  Factories (`SidebarPropertiesInformationTabFactory`,
  `SidebarPropertiesSettingsTabFactory`, `SidebarIpTabFactory`,
  `SidebarCommentTabFactory`, je 5 Methoden). Todos sind Drag-Ziele
  („Drop a link here").
- **UI-Ressourcen:** `sidebar/sidebarview.baml`, `sidebar/sidebarproperties.baml`,
  `sidebar/sidebarworkspaces.baml` (8,3 KB), `sidebar/sidebarundohistory.baml`,
  `sidebar/todoitemsview.baml`, `sidebar/todoitemcontrol.baml`,
  `sidebar/propertycontents/*` (29 Dateien), `themes/sidetabcontrol.baml`,
  `resourcedictionaries/sidebardictionary.baml` (12,6 KB),
  `assets/_standardui/sidebar/...` (Icons, u. a. Undo-History-Pfeile).
- **Strings (en | de):** `SidebarProperties` „Sidebar 'Properties'" |
  „Navigationsleiste 'Eigenschaften'"; `SidebarUndo` „Sidebar 'Undo/Redo'" |
  „Navigationsleiste 'Undo-Historie'"; `SidebarWorkspace` „Sidebar
  'Workspaces'" | „Navigationsleiste 'Arbeitsbereiche'"; `SidebarOperations`
  „Sidebar 'Pending Operations'" | „Navigationsleiste 'laufende Operationen'";
  `SidebarHelp` „Sidebar 'Help'" | „Navigationsleiste 'Hilfe'";
  `SidebarSection_Filter_Headline` „Filter"; `_ByTag` „Tags"; `_ByLockOwner`
  „Locked by" | „Gesperrt von"; `TodoList` „Todo list" | „Offene Aufgaben".
- **Persistenz:** Breite/Zustand über `EtsUserSettings` (u. a.
  `GroupAddressSelector`, `Presentation`); Container-Wahl je Panel (abgeleitet
  aus `SidebarContentTypes` + `SidebarExpandStatus`; exakter Ort unbekannt).
- **Randfälle:** `FindAndReplace` ist ein Vollcontainer, kein Dialog; Todo-Liste
  hängt an Projektmarkern (`AddTodoItemMarker` „Todo item added.").
- **BusChord-Lücke:** nur Inspector-Pane; keine Container-Umschaltung, keine
  Todo-/Operations-/Undo-Liste.
- **Empfehlung:** Sidebar als Container-Registry mit einem aktiven Container je
  Seite; nicht als Dock-Pane.

### S7 Benannte Workspaces / Layouts

- **Workflow (A):** Arbeitsbereiche sind benannte, serialisierte Layouts
  (`WorkspaceSerializer` `0x02000176` @6565598, 15 Methoden:
  `SaveWorkspaceToStore`, `RestoreWorkspaceFromStore`, `GetWorkspaceFromFilename`,
  `GetUniqueWorkspaceName`, `GetWorkspaceFilename`, `WorkspaceFileExtension`,
  `WorkspacePath`, `SaveGridSettingsDefault`/`GetGridSettingsDefault`).
  `WorkspaceCreator` (`0x0200016c` @6565418): `CreateDefaultWorkspace`,
  `CreateDefaultProjectWorkspace`, `bvoxESYtMTE(String,Boolean)`.
  `IWorkspaceCollection`: `Workspaces`, `DefaultWorkspace`, `Clear`.
  UI: `sidebar/sidebarworkspaces.baml`, `dashboard/settings/presentationsettings.baml`.
- **Strings:** `NewWorkspaceName` „New Workspace" | „Neuer Arbeitsbereich";
  `LastUsedWorkspaceName` „Last used workspace"; `LastActiveWorkspaceName`
  „Last active workspace"; `RestoreDefaultWorkspace` „Restore Default
  Workspace"; `FileDialogImportWorkspaceTitle` „Select a workspace to import";
  `FileDialogExportWorkspaceTitle` „Select an export file";
  `SettingsShortcutsTooltipUseDefaultWorkspace` „Use the default workspace
  instead of a specific workspace" | „Aktiviert den Standard-Arbeitsbereich".
- **Persistenz:** Datei je Workspace (`WorkspacePath` +
  `WorkspaceFileExtension`), Standard-Layout getrennt
  (`SaveGridSettingsDefault`), Auswahl „zuletzt verwendet/aktiv" in den
  Benutzereinstellungen.
- **Randfälle:** Default-Workspace ist nicht löschbar/umbenennbar
  („Der Default-Arbeitsbereich kann nicht gelöscht werden." /
  „… nicht umbenannt werden."); Import fremder Layouts möglich.
- **BusChord-Lücke:** `WorkbenchWorkspace.SaveLayout/TryRestoreLayout` schreibt
  **ein** JSON-Layout; keine benannten Workspaces, kein Import/Export, kein
  Default-Layout.
- **Empfehlung:** bestehendes JSON-Layout um `Name` + Verzeichnis erweitern,
  Default-Layout separat halten – kein neues Format.

### S8 Schweben, Andocken, mehrere Fenster

- **Workflow (A/B):** Panelzustände `ContentPanelLayout` `0x0200079e`
  (`NotSet` `0x040021ea`, `Regular` `0x040021eb`, `Minimized` `0x040021ec`,
  `Maximized` `0x040021ed`, `Floating` `0x040021ee`). Ein Panel kann in einen
  neuen Tab (`OpenPanelInNewTab`) oder ein neues Fenster
  (`IMainWindowService.CreateWindowWithTab`, `NewWindow`-Kürzel). Fenster sind
  `RadTabbedWindow` mit eigenem Tab-Satz (`RadTabbedWindowAdapter`:
  `Title`, `IsOpen`, `Close`, `BringToFront`, `RealWindow`).
- **UI-Ressourcen:** `resourcedictionaries/radtabbedwindowdictionary.baml`,
  `themes/telerik.windows.controls.navigation.baml`.
- **Strings:** `NewWindow` „New Window" | „Neues Fenster";
  `OpenAsNewWindowTabMenuText`; `WindowRestore` „Restore" | „Wiederherstellen";
  `MoreThanSixWorkPanelsNotAllowed`.
- **Persistenz:** Fensterzustand (`EtsWindowSettings.WindowState`), Layout im
  Workspace.
- **Randfälle:** `LastWindowClosed` beendet die App; `IsModal`/`IsLocked` pro
  Fenster; beim Schließen des letzten Panels eines Tabs meldet die Shell
  `ClosedLastPanelOfTabPageMessage`.
- **BusChord-Lücke:** Dock kann floaten, aber ohne Panelmodell, ohne
  Fenster-Tabs, ohne Layout-Speicherung je Fenster.
- **Empfehlung:** „Float" als Panelzustand modellieren, nicht als eigene
  Fensterklasse.

### S9 Panel-Favoriten

- **Workflow (A):** `PanelMenuManager` (`0x020005d2` @6585686, 14 Methoden)
  verwaltet Favoriten: `SetFavorite(Contents,bool)` `0x06003bf0`/1278744,
  `GetIsFavorite` `0x06003bf1`/1279420, `GetFavoriteOrderIndex`
  `0x06003bf2`/1279548, `GetPanelTypes` `0x06003bef`/1278636, Event
  `PanelFavorabilityChanged` (`OnPanelFavorabilityChanged`
  `0x06003bf3`/1279668), Felder u. a. `IEtsUserSettings` (`0x04001cba`) und
  `Contents[]` (`0x04001cbb`). `FavoriteType` `0x020005bd` unterscheidet
  `Panel` (`0x04001c96`) und `App` (`0x04001c97`); Apps haben einen eigenen
  Manager (`IAppMenuManager` `0x020005bf`: `PinnedItems`, `ManufacturerItems`).
- **UI-Ressourcen:** `resourcedictionaries/panelcontentselector.dictionary.baml`,
  `resourcedictionaries/maintoolbardictionary.baml`,
  `dashboard/settings/apps/appsgrid.baml` (App-Favoriten).
- **Strings:** `Favorites` „Favorites" | „Favoriten"; `LoadFavoritesFailed`
  „Load favorites from file '{0}' failed." | „Die Favoriten konnten nicht aus
  der Datei '{0}' gelesen werden."; `SaveFavoritesFailed` analog.
- **Persistenz:** **Datei** (Pfad im Fehlertext als `{0}`), zusätzlich
  Reihenfolge über `GetFavoriteOrderIndex`.
- **Randfälle:** Datei-Fehler sind nicht fatal (nur Log/Message); Favoriten
  gelten je Paneltyp, nicht je Panelinstanz; App- und Panel-Favoriten sind
  getrennte Typen.
- **BusChord-Lücke:** keine Favoriten, keine Pinned-Apps.
- **Empfehlung:** Favoritenliste als Teil der Benutzereinstellungen (nicht als
  Datei) – die Datei ist ETS-Altlast.

### S10 Menü, Werkzeugleisten, Kontextmenüs

- **Workflow (A):** Menüleiste (`bars/menubar.baml`), Hauptwerkzeugleiste
  (`maintoolbardictionary`, ein-/ausblendbar über
  `assets/_standardui/toolbars/menubar.hide|showmaintoolbar.png`), Kontextmenüs
  (`resourcedictionaries/contextmenuresources.baml`,
  `etscontexttoolbardictionary.baml`), Breadcrumb-Leiste (`wunderbar`),
  Statusleiste. Befehle kommen aus `IKontext`-Diensten
  (`IAppMenuManager`, `IPanelMenuManager`, `IContextMenuCommandProvider`,
  `IViewModelWithContextMenu`), Handlerfamilien in
  `Knx.Ets.ViewModel.Menu.CommandHandler` (49 Typen: Copy/Cut/Delete/
  Paste/PasteSpecial/PasteExtended/Link/Unlink/PrintLabel/SplitProject/
  SplitGroupRange/EditChannels/TransferParametersAndFlags/…).
- **UI-Ressourcen:** `bars/menubar.baml` (9,1 KB), `databindings/menuitem.databindings.baml`,
  `resourcedictionaries/{maintoolbar,contextmenuresources,etscontexttoolbar,
  contexttoolbar,dashboarddictionary,icontemplates}.baml`.
- **Typen:** `MenuItemViewModelFactory` (14 Methoden), `MenuItemBuilder`
  (15), `PanelMenuManager` (S9), `AddInToolbarMenuButton`,
  `AddInToolbarItemViewModelFactory` (Plugin-Menüs), `FavoriteType`.
- **Strings:** Menüpunkte sind überwiegend Command-Texte aus Ressourcen
  (z. B. `Import` „Opens the Import Project dialog in Dashboard and opens
  Import Product dialog inside a project"), `Print`, `Undo`, `Redo`.
- **Persistenz:** Sichtbarkeit der Hauptwerkzeugleiste (Setting, abgeleitet aus
  den Hide/Show-Assets).
- **Randfälle:** Befehle sind gesperrt, wenn ein Dialog offen ist
  (`IsBlockedByDialog`); AddIns injizieren eigene Menüpunkte.
- **BusChord-Lücke:** Avalonia-Menü vorhanden, aber ohne Befehlskontext,
  Sperrlogik und AddIn-Erweiterung.
- **Empfehlung:** `ApplicationCommandHandler`-Muster (Attach an Visual +
  CanExecute-Sperre) übernehmen, nicht Menüstruktur kopieren.

### S11 Tastenkürzel

- **Workflow (A):** `KeyboardShortcuts` `0x02000153` @6564968 (101 Methoden,
  47 Felder): **44** konfigurierbare Gesten als Eigenschaften
  (`SerializableKeyGesture`, Get/Set), u. a. `NewProject`, `NewTab`,
  `NewWindow`, `CloseTab`, `CloseProject`, `ClosePane`, `MaximizePane`,
  `SplitPanelHorizontal/Vertical`, `NextPane`, `PreviousPane`, `NextTab`,
  `PreviousTab`, `ToggleSidebar`, `SidebarProperties`, `SidebarUndo`,
  `SidebarWorkspace`, `SidebarOperations`, `ShowSettings`, `Help`, `Import`,
  `Print`, `EditParameter`, `DeviceInfo`, `DownloadAll`,
  `DownloadApplication`, `DownloadIndividualAddress`, `DownloadPartial`,
  `ResetDevice`, `UnloadDevice`, `OverwriteIndividualAddress`,
  `ShowBusMonitor`, `ShowGroupMonitor`, `PanelBuildings`, `PanelDevices`,
  `PanelGroupAddresses`, `PanelProjectRoot`, `PanelReports`, `PanelTopology`,
  `UseDefaultWorkspace`, `AddSplitButton`, `AddSplitButtonDropDown`,
  `NextMatch`, `ToggleFilter`. Nicht konfigurierbar, aber mit eigenem Tooltip:
  `NavigateBackward`, `NavigateForward`, `PasteBatch`, `GroupAddresses`,
  `ProjectRoot`, `Topology` (45 `SettingsShortcutsTooltip*`-Keys). Fünf
  Befehlslisten
  (`CustomizableCommands`, `CustomizableProjectCommands`,
  `CustomizableNavigationCommands`, `CustomizableDownloadCommands`,
  `CustomizableAppearanceCommands`) und `ReadOnlyCommands` definieren, was
  editierbar ist.
- **Prüfung/Reset:** `GetStringForKeyGesture` (`0x06000eb0`/380436,
  `0x06000eb1`/380452), `IsKeyGestureReserved` (`0x06000eb2`/380492) →
  Strings „Diese Tastenkombination ist reserviert und kann nicht gesetzt
  werden." / „Diese Tastenkombination wird bereits verwendet.";
  `KeyboardShortcuts..ctor` (`0x06000e51`/373664, 422 IL-Befehle) setzt die
  Standardbelegung, `.cctor` (`0x06000eb3`/380656) die Statik.
  Reset: `ApplicationMessages.ResetShortcutSettingsMessage`,
  „Do you want to reset all shortcuts?" (`MessageConfirmShortcutReset`).
- **UI-Ressourcen:** `dashboard/settings/shortcutsettingsview.baml`;
  `SettingsShortcuts` „Shortcuts" | „Tastaturkürzel" (Settings-Seite).
- **Strings:** 45 `SettingsShortcutsTooltip*`-Keys, **vollständig** in de
  vorhanden. Beispiele: `…Help` „Open a context help" | „Öffnet die
  Kontext-Hilfe"; `…MaximizePane` „Moves active content panel into a new tab";
  `…ToggleSidebar` „Change the sidebar state".
- **Persistenz:** `EtsUserSettings.KeyboardShortcuts` (XML-Serialisierung der
  `SerializableKeyGesture`).
- **Randfälle:** reservierte/doppelte Kombinationen werden abgelehnt;
  `GestureGroupType` (`Navigation`/`Appearance`/`Project`/`Undefined`) gruppiert
  die Einstellungsseite. **Lokalisierungsfehler:** der deutsche Tooltip für
  „Navigate Forward" lautet „Navigiert ein Element zurück".
- **BusChord-Lücke:** keine konfigurierbaren Kürzel, keine Reserviert-Prüfung.
- **Empfehlung:** Kürzel als Daten (Befehl → Geste) mit zentraler Prüfung;
  Standardbelegung nicht aus ETS-IL ableiten, sondern bewusst setzen.

### S12 Kontext-Hilfe

- **Workflow (A):** `IHelpProvider.OpenHelp` (`0x0600144c`) wird vom
  `ApplicationCommandHandler` mitgeführt; das Kürzel `Help` („Open a context
  help") ruft sie auf. In den Einstellungen zeigt `ISettingsHelpProvider`
  (`DisplayHelpText` `0x06004369`, `HideHelpText` `0x0600436a`) Hilfetexte zur
  gewählten Seite. `ContextHelp.HelpEventArgs` (4 Methoden) trägt das
  Hilfeziel; `ISettingsHelpProvider` sitzt im ViewModel, die UI in
  `Knx.Ets.Ui.UserControls.ContextHelp`.
- **Strings:** `ContextParameterHelp` „Context Help"; `Help` „Help" | „Hilfe";
  `HelpFileNotFound` „Help File Not Found" | „Hilfedatei nicht gefunden";
  `NoHelpFileFound` „No help file was found."; `SidebarHelp`.
- **Randfälle:** Hilfe wird als Datei erwartet und kann fehlen – dafür
  existieren zwei eigene Fehlerstrings; AddIns bringen eigene Hilfedateien mit
  (`Ets4AddInManifest+HelpFile`).
- **BusChord-Lücke:** `ShowHelp`-Dialog mit statischem Text, kein
  kontextsensitives Ziel, keine Fehlerbehandlung für fehlende Hilfe.
- **Empfehlung:** Hilfeziel als `(contextId)` an jedem Panel/Detail-Reiter;
  Öffnen über einen Dienst, der auch „nicht gefunden" kann.

### S13 Lokalisierung (de/en)

- **Workflow (A):** UI-Sprache und Produktsprache sind getrennt:
  `LanguageSettings` `0x020012a1` @6644708 mit `UILanguage`,
  `PreferredProductLanguage`, `FallbackLanguage` (read-only),
  `RecreateCaches`. 19 Sprachordner im Installationsverzeichnis (`cs-CZ`,
  `da-DK`, `de-DE`, `el-GR`, `es-ES`, `fi-FI`, `fr-FR`, `it-IT`, `ja-JP`,
  `nb-NO`, `nl-NL`, `pl-PL`, `pt-PT`, `ru-RU`, `sv-SE`, `tr-TR`, `uk-UA`,
  `zh-CN`, `zh-Hans`) sowie die Sonderordner `CV` und `KnxMaster`;
  `Knx.Ets.Localization.Properties.LanguageNames.resources` liefert die
  Anzeigenamen. Telerik-Texte werden über
  `ETS.Manager.CustomLocalizationManager.GetStringOverride` (`0x02000066`
  @240042) ersetzt.
- **Ressourcen:** `Knx.Ets.ViewModel` (en 2812 / de 2802 Strings),
  `Knx.Ets.Ui.UserControls` (en 1255 / de 1254),
  `Knx.Ets.Localization` (en 1405 / de 1399). Fehlende deutsche Keys:
  **13** (ViewModel), **1** (UserControls), **6** (Localization) – überwiegend
  technische Keys (`KeyEnter`, `EncodingUtf8`, `NewsUrl`, `DPTToolTip`,
  `LineScanPrint_HeaderList1`).
- **Randfälle:** Deutsche Terminologie weicht ab: `Redo` = „Wiederherstellen"
  (statt „Wiederholen"); `SidebarUndo` = „Undo/Redo" vs. „Undo-Historie";
  Forward-Tooltip falsch (s. S11). Sprache ändern erzwingt Neustart
  (`RestartOptions`, `RecreateCaches`).
- **BusChord-Lücke:** nur de/en, aber funktional (Theme/Sprache im
  `SettingsWindow`).
- **Empfehlung:** keine neue Lokalisierungsinfrastruktur; fehlende Keys sind
  kein Paritätsproblem für BusChord.

### S14 Barrierefreiheit, Fokus, Navigation

- **Befund (A, BAML-Token-Scan über 740 Einträge):**
  `AutomationProperties` in **49** BAML-Dateien (u. a.
  `workplace/contentpanel/contentpanelcontainer.baml`,
  `sidebar/todoitemsview.baml`, `dashboard/settings/shortcutsettingsview.baml`,
  `dialogs/security/*`), `KeyboardNavigation` in **37**, `AccessText`
  (Mnemonics) in **8**, `TabIndex` in **10**, `IsTabStop` in **2**,
  `ToolTip` in **123**, `ContextMenu` in **46**, `InputBinding` in **20**,
  `KeyBinding` in **0** (Gesten zentral, nicht je View).
  **Keine** `AutomationPeer`-Typen in ViewModel/UserControls/PresentationFramework.
- **Fokus/Navigation:** `NavigationHistoryManager` `0x02000cbd` @6617564:
  `Navigate` (`0x06007244`/2499216), `CanNavigate` (`0x06007245`/2499632),
  `AddHistoryStep` (`0x06007246`/2499776), `Clear` (`0x06007248`/2500108),
  `IsLicensed` (`0x0600724b`/2501032); Richtungen `NavigationDirection`
  (`Forward` `0x040035e9`, `Backward` `0x040035ea`). Zustand
  `NavigationState` `0x02000cbe`: `ContentPanelType`, `DetailsTabViewType`,
  `TreePaths`, `AddInId`; Ziel `PanelNavigationTarget` `0x02000cc2`.
  Tastaturdienst `IKeyboardService.IsCtrlPressed/IsShiftPressed`
  (`0x06004270`/`0x0600426f`) – Modifier werden manuell abgefragt.
  Dialoge können initialen Fokus setzen (`ICustomFocusedDialog`).
- **Strings:** `SettingsShortcutsTooltipNavigateBackward/Forward`; keine
  Screenreader-/Accessibility-Texte in den Ressourcen (0 Treffer für
  „screen reader"/„Vorlesen").
- **Persistenz:** Navigationshistorie ist flüchtig (Stacks im Manager, keine
  Serialisierung belegt).
- **Randfälle:** `InvalidNavigationTargetException`; `IsLicensed` schützt
  Navigation zu nicht lizenzierten Panels; Historie wird bei Panelschließung
  nicht bereinigt (nicht belegt, Risiko).
- **BusChord-Lücke:** keine Back/Forward-Historie, kein
  `AutomationProperties`-Audit bekannt, Fokusring vorhanden
  (`FocusRing`-Ressource).
- **Empfehlung:** `AutomationProperties.Name` für alle interaktiven Elemente
  ist die kleinste echte Parität; Back/Forward über einen Zustandsstack
  (Panel + Detail-Tab + Pfad).

### S15 Zuletzt verwendete Projekte

- **Workflow (A/B):** `EtsUserSettings.MostRecentProjects` (Liste),
  `AddProjectToMostRecentList(ProjectId)`, `RemoveProjectFromMostRecentList`,
  `GetMostRecentActivityDate(ProjectId)`, `MostRecentProject { Id, OpenDate }`
  (`0x02000158` @6565058). Dashboard-Kacheln und „Your Projects" greifen darauf
  zu (abgeleitet aus `DashboardSidebar_LocalProjects` + den Tile-ViewModels).
- **Strings:** `TabNameProjects` „Your Projects" | „Ihre Projekte";
  `UnpinToolTipText` „Click to stop retaining indefinitely"; `PinToolTipText`
  „Click to retain this archive version indefinitely".
- **Persistenz:** Benutzereinstellungsdatei (`EtsUserSettings.Save/Read`).
- **Randfälle:** Einträge ohne Datei bleiben als „broken" sichtbar
  (`BrokenProjectTooltip`); Pin/Unpin gilt für Archivversionen, nicht für die
  Recent-Liste.
- **BusChord-Lücke:** Recent-Liste existiert im Startbildschirm
  (`MainWindow.axaml.cs` `OnRecentSelected`), aber nicht im Workbench-Fenster.
- **Empfehlung:** Recent-Liste in die Einstellungen verschieben und im
  Dashboard/Start gemeinsam nutzen.

### S16 Optionen („ETS-Einstellungen")

- **Workflow (A):** `EPages` `0x0200124f` @6643232 – `About` `0x04004b6c`,
  `Apps` `0x04004b6d`, `ConnectionInterface` `0x04004b6e`, `DataStorage`
  `0x04004b6f`, `Labels` `0x04004b70`, `Language` `0x04004b71`, `Licensing`
  `0x04004b72`, `OnlineCatalog` `0x04004b73`, `Presentation` `0x04004b74`,
  `Shortcuts` `0x04004b75`, `Troubleshooting` `0x04004b76`. Die Seite ist ein
  Container (`dashboard/settings/settingsdialogcontainer.resources.baml`) mit
  eigener Navigation.
- **UI-Ressourcen:** `dashboard/settings/etssettings.baml` (12 KB),
  `presentationsettings.baml`, `languagesettings.baml`,
  `shortcutsettingsview.baml`, `labelsettings.baml`,
  `troubleshootingsettings.baml`, `onlinecatalogsettings.baml`,
  `datastorage/datastoragesettingsview.baml`, `apps/appspage.baml`,
  `licensing/licensingsettingspage.baml`, `aboutsettingspage.baml`.
- **Strings:** `TabNameSettings` „Settings" | „Einstellungen";
  `SettingsShortcuts` „Shortcuts" | „Tastaturkürzel";
  `SettingsAboutPageTabName` „About" | „Über";
  `SettingsAppsPageTabName` „ETS Apps"; `SettingsLicensingPageTabName`
  „Licensing" | „Lizenzierung"; `SettingsConnectionManagerTabName`
  „Connection Manager" | „Verbindungen".
- **Persistenz:** gesamte `EtsUserSettings` (`Save`, `Save(Boolean)`, `Read`,
  `ResetSettings`, Event `Saved`).
- **Randfälle:** `SettingsResetMessage` (Reset aller Einstellungen),
  `MessageResetNotifications`; Help-Texte je Seite über
  `ISettingsHelpProvider`.
- **BusChord-Lücke:** `SettingsWindow` hat **eine** Seite
  (Darstellung/Sprache/Löschbestätigung) und keinen Seitenbaum.
- **Empfehlung:** Seitenmodell (`IPage`) + Liste; keine 11 Seiten nachbauen,
  aber die Struktur.

### S17 About und Update

- **Workflow (A):** `AboutSettingsPageViewModel` `0x02001300` @6646418
  (22 Methoden): `VersionInfoViewModel` (`0x0600a793`/3866224),
  `CheckForUpdatesCommand` (`0x0600a794`/3866232),
  `InstallUpdatesCommand` (`0x0600a796`/3866336), `UpdateInProgress`
  (`0x0600a790`/3865688), `Dispose` (`0x0600a79e`/3867404).
  Updater getrennt: `Application.Update.UpdaterUtility`,
  `UpdateResource`, `Knx.Updater*`; `ETS6.exe` ist der Launcher,
  `AppConfiguration.xml` konfiguriert den Updater-Pollingpfad.
- **UI-Ressourcen:** `dashboard/settings/aboutsettingspage.baml` (8,4 KB),
  `dialogs/prenotification/*` (Release Notes/Update).
- **Strings:** `SettingsAboutPageTabName`, `StartupScreen_ProductName` „ETS6"
  (nur englisch vorhanden), Versions-/Lizenztexte aus `Knx.Ets.Localization`.
- **Randfälle:** Update-Installation läuft über einen eigenen Handler
  (`InstallEtsUpdateCommandHandler`), Update-Quelle ist in
  `AppConfiguration.xml`/`ETS6N.exe.config` konfiguriert; Update-Hinweis vor
  dem Start (`PreNotification`-Fenster).
- **BusChord-Lücke:** kein About, kein Update-Kanal (bewusst).
- **Empfehlung:** About als eine Settings-Seite mit Version + Lizenz; kein
  Updater.

### S18 Benachrichtigungen, Aufgaben, Undo-Historie

- **Workflow (A/B):** Benachrichtigungen: `INotificationBearer.NotificationCount`
  (`0x060042c4`), `INotifyNotificationsChanged`, Badge-UI
  `controls/notificationbadge.baml`, Reset über
  `MessageResetNotifications` „Reset all notifications?" | „Alle
  Benachrichtigungen zurücksetzen?". Aufgaben (Todos) sind ein eigener
  Sidebar-Container mit Projektmarkern (`Todo*`-Strings, `todoitemsview.baml`).
  Undo: Store-basiert (`IUndoManagerStoreProvider.GetStore/NewStore/RootPath`
  `0x06000af3`–`0x06000af5`; `UndoManagerStoreProvider`
  `0x02000134` @5985990), Marker mit `LocalizedDescription`/`Description`/
  `Commit`/`Timestamp`/`ShouldTrace` (`IMarker` `0x0200011e` @5985594),
  `PropertyChangedUndoCommandFactory.Create/CreateEnlist`
  (`0x06000b78`/237464, `0x06000b79`/237552), `UndoRedoEnable.IsEnabled`/
  `Disable` (`0x06000bdb`/249128, `0x06000bdc`/249140). UI:
  `sidebar/sidebarundohistory.baml`, Icons
  `assets/_standardui/sidebar/undo history/*` (normal/selected/inactive/
  forward/backward).
- **Strings:** `Undo` „Undo" | „Rückgängig"; `Redo` „Redo" |
  „Wiederherstellen"; `UndoHistory` „Undo History" | „Undo Historie";
  `SidebarUndo`; `ParameterChangeUndoHistoryText` „Parameter '{0}' of device
  '{1}' changed."
- **Persistenz:** Undo-Store auf Platte (`RootPath`), Projektmarker im Projekt;
  Benachrichtigungen flüchtig (Badge-Zähler).
- **Randfälle:** Undo-Historie ist projektweit, nicht panelweit; bei
  gesperrten/deaktivierten Zuständen `UndoRedoEnable.Disable`; „Es ist kein
  Undo verfügbar" existiert als String.
- **BusChord-Lücke:** Undo/Redo funktional, aber ohne History-UI, ohne
  beschreibende Marker, ohne Store; keine Benachrichtigungen, keine Todos.
- **Empfehlung:** Undo-Marker mit Beschreibung + Historie-Panel ist der
  sichtbarste Paritätssprung bei geringem Risiko.

---

## 5. Zustandspersistenz (Gesamtbild)

| Zustand | Ort | Beleg |
|---|---|---|
| Alle Benutzereinstellungen | `EtsUserSettings` (XML, `Save`/`Read`/`ResetSettings`, Event `Saved`) | `0x02000135`, Methoden `Save`, `Read`, `ResetSettings` |
| Fenstergröße/-zustand | `EtsWindowSettings.WindowState` | `0x02000148` |
| Tastenkürzel | `EtsUserSettings.KeyboardShortcuts` | `0x02000153` |
| Zuletzt verwendete Projekte | `EtsUserSettings.MostRecentProjects` (`MostRecentProject{Id,OpenDate}`) | `0x02000158` |
| Workspaces | Datei je Workspace (`WorkspacePath`, `WorkspaceFileExtension`), Default separat (`SaveGridSettingsDefault`) | `0x02000176`, `0x0200016c` |
| Panel-Favoriten | Datei (`LoadFavoritesFailed`/`SaveFavoritesFailed`), Reihenfolge über `GetFavoriteOrderIndex` | `0x020005d2` |
| Sprache | `LanguageSettings.{UILanguage,PreferredProductLanguage}` | `0x020012a1` |
| Undo | Store auf Platte (`IUndoManagerStoreProvider.RootPath`) | `0x02000121` |
| Projektmarker/Todos | im Projekt | `AddTodoItemMarker` |
| Navigationshistorie | flüchtig (Stacks) | `0x02000cbd` |
| Benachrichtigungszähler | flüchtig | `0x060042c4`/`INotificationBearer` |

---

## 6. Randfälle, Grenzen, Auffälligkeiten

1. **Sechs-Panel-Grenze** (`MoreThanSixWorkPanelsNotAllowed`) – harte
   Obergrenze; BusChord hat keine.
2. **Einzelinstanz-Regeln**: nur ein Gruppenmonitor, ein Diagnose-Panel, ein
   AddIn-Panel (`HasGroupMonitorRunning`, `ContainsDiagnosticsPanel`,
   `HasAddInPanel`, `HandleSingleInstanceAddIn`).
3. **Schließen** ist mehrstufig: `CanClose` → `UserAllowsCloseContent` →
   `Close`; letzter Tab/Panel meldet `ClosedLastPanelOfTabPageMessage`.
4. **Befehle blockiert bei Dialogen** (`IsBlockedByDialog`), inkl.
   `ResetModalWindows`-String als Reparaturpfad.
5. **Deutsche Terminologie inkonsistent**: `Redo` = „Wiederherstellen",
   Forward-Tooltip sagt „zurück", `SidebarUndo` = „Undo/Redo" vs.
   „Undo-Historie".
6. **Übersetzungslücken**: 13/1/6 Keys ohne deutsche Fassung
   (ViewModel/UserControls/Localization).
7. **Favoriten sind dateibasiert** und können fehlschlagen, ohne die App zu
   blockieren.
8. **Workspace-Default** ist unlöschbar/umbenennbar.
9. **Zwei Hosts**: `ETS6N.exe` (6.4.8718.0, `237e7ec3…`) und `ETS6C.exe`
   (Kompatibilitätsmodus, `3d490d0b…`), plus `ETS6.exe` als Launcher.
10. **`UIFramework`-AppSetting** verweist auf
    `Knx.Ets.UI.Deagle.Application, Knx.Ets.UI.Deagle` – diese Assembly liegt
    **nicht** im Installationsverzeichnis (Altlast oder dynamisch geladen);
    Bedeutung unbekannt.
11. **BAML ohne `KeyBinding`**: Gesten werden zentral gebunden, nicht je View –
    ein Widerspruch zu typischen WPF-UIs und ein Hinweis auf den
    `ApplicationCommandHandler` als einzige Senke.

---

## 7. BusChord-Lücken (Rangliste)

Status bezeichnet die gesamte Kette, nicht das Vorhandensein einer Klasse.
Schwere: **kritisch** = Arbeitsfluss unmöglich, **hoch** = wesentliche
Abweichung, **mittel** = Komfort/Parität.

| # | Feature | BusChord-Stand | Schwere | Kleinster nächster Schritt |
|---|---|---|---|---|
| 1 | Projekt-/Ansichts-Tabs | fehlt (nur `WorkbenchTab` Ziele) | **kritisch** | `TabId`/`ProjectTabId` + Tabliste im `WorkbenchWindow`; Close-Guard |
| 2 | Panelmodell + Panelauswahl | fehlt (Dock-Panes) | **kritisch** | `PanelDescriptor(Contents, PanelId)` + `PanelList` mit 6er-Limit |
| 3 | Sidebar-Container | fehlt (nur Inspector) | **hoch** | Container-Registry mit Properties/Undo/Todos |
| 4 | Benannte Workspaces | teilweise (ein JSON-Layout) | **hoch** | `Name` + Verzeichnis + Default-Layout |
| 5 | Tastenkürzel | fehlt | **hoch** | Befehl→Geste-Tabelle + Reserviert-Prüfung + Settings-Seite |
| 6 | Undo-Historie (UI) | Undo funktional, keine Historie | **hoch** | Marker mit Beschreibung + Historie-Pane |
| 7 | Panel-Favoriten | fehlt | mittel | Favoritenliste in Einstellungen, Sortierung |
| 8 | Detail-Reiter je Panel | teilweise (globaler Inspector) | mittel | Detail-Tab-Set an `PanelContentViewModel` |
| 9 | Dashboard | fehlt | mittel | Als regulärer Tab nach #1/#2 |
| 10 | Kontext-Hilfe | statischer Hilfe-Dialog | mittel | Hilfeziel je Panel/Detail-Tab, „nicht gefunden"-Pfad |
| 11 | Navigationshistorie | fehlt | mittel | Zustandsstack Back/Forward |
| 12 | Benachrichtigungen/Todos | fehlt | mittel | Badge + Liste; Todos erst nach Sidebar |
| 13 | Barrierefreiheit | unbekannt (Fokusring vorhanden) | mittel | `AutomationProperties.Name`-Audit + Nachtrag |
| 14 | Optionen-Seiten | eine Seite | niedrig | Seitenmodell, 3–4 Seiten |
| 15 | About | fehlt | niedrig | Eine Settings-Seite |
| 16 | Recent-Projekte | Startbildschirm only | niedrig | In Einstellungen zentralisieren |
| 17 | Floating/Fenster-Tabs | Dock-Float ohne Modell | niedrig | Float als Panelzustand |

**Top-3 für den nächsten Schritt:** #1 Tabs, #2 Panelmodell/Panelauswahl,
#3 Sidebar-Container. Alles andere hängt daran; Favoriten, Workspaces und
Kürzel sind ohne diese drei nicht sinnvoll testbar.

---

## 8. Unbekannt / nicht abgenommen

- Laufzeitverhalten (Klicken, Fokus, Drag&Drop, Dialogschichten) – nicht
  beobachtet; alle Workflows sind rekonstruiert.
- Vollständige BAML-Struktur je View (nur Eintragsnamen + Attribut-Treffer).
- Standard-Tastenbelegung (Werte entstehen im IL von
  `KeyboardShortcuts..ctor`, nicht in Ressourcen) – nicht dekodiert.
- Exakte Dateipfade von Benutzereinstellungen, Workspaces, Favoriten und
  Undo-Store (nur Eigenschaften/Fehlertexte belegt).
- Ob die Navigationshistorie serialisiert wird (keine Serialisierung belegt).
- Bedeutung des `UIFramework`-AppSettings und der `Deagle`-Assembly.
- Plugin-/AddIn-spezifische Shell-Erweiterungen (nur Menü-/Panel-Deskriptoren
  gesehen).
- Ob `AutomationProperties` flächendeckend gesetzt sind (nur Vorkommen in 49
  von 740 Einträgen).
- Ob `ETS6C.exe` funktional identisch ist (nur SHA/Größe/Version + Strings).

---

## 9. Prüfung / Reproduktion

- Branch/HEAD: `agents/ets-rea/shell` auf
  `12fc904ee05089976eac93426141c6fdb348ffe1`; Worktree
  `C:\projects\ai_work\agent-wt\ets-rea-shell`; `git status` sauber bis auf
  diese Datei.
- Artefakt-SHAs mit `Get-FileHash -Algorithm SHA256` bestätigt (Tabelle §2).
- REA-Evidenz: alle zitierten IDs in dieser Sitzung erzeugt; Typ-/Methoden-
  Tokens sind build-lokal und nur mit SHA + MVID gültig (REA-Limitation).
- Ressourcenzahlen über `System.Resources.ResourceReader` (read-only), keine
  Ausführung von Produktcode; BAML-Trefferzahlen über ASCII-Scan der
  Ressourcen-Streams (Attributnamen, keine Struktur).
- Vorlauf: `analysis/ux/ets6-panel-recheck.md` (offizielle KNX-Doku S1–S3 zu
  Tabs/Panels) deckt sich mit den hier statisch belegten Ebenen; die
  dortigen Quellen wurden in dieser Untersuchung **nicht** erneut abgerufen.
