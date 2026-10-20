# Workbench V4: acceptance inventory and theme contract

Baseline: `a1f2a7483a455225d8a2f285b74345e7dd832165`, inspected 2026-10-20.
Canonical scope: approved OpenChamber plan `1792528033708-buschord-vollst-ndige-umsetzung-des-knx-workbench-v4-konzepts.md`, sections 1–9. Reference root: `C:\Users\julia\Downloads\knx-workbench-v4-bundle` (below, `bundle/`).

**This change delivers resources and an inventory, not the V4 application.** Existing source was inspected, not changed. All native functional and visual acceptance below remains **PENDING** unless a concrete result is stated. Proposed command/test names are contracts for implementation, not claims that those members or tests exist. Shell/domain/session owners may rename them while preserving every inventory ID. Historical completion marks in `FEATURE.md` are not V4 acceptance evidence.

## Reference integrity and reproduced checks

All **18/18** entries of `bundle/checksums.sha256` matched SHA-256; zero failures. Manifest SHA-256: `aa7ce905cf12a1ce98e8709d6f423da579a4b1ccc86e0f208f5492950742af6d`.

| Source | SHA-256 | Role |
|---|---|---|
| `knx-workbench-v4.html` | `fcf806adb632324d6bb0a4fd01eee420875822b4b26680e4784032c96817315f` | Generated interactive reference |
| `src/shell.html` | `33fae3852733ab92ab92705f9340a28058aa6fd6ded111725293a701789081cf` | CSS measurements and fixed-zone structure |
| `src/app.js` | `54b020f9b7d7bbff6dbf21e6826538e83c53ea8c61407899b2863fa50427da60` | All actions/events/state transitions |
| `src/domain.js` | `607cd212c34cece1686aa4548bf4c2abc6e0c13d81ff31e1919a369ac049d874` | Demo invariants, projections, validator |
| `examples/demo-projekt.json` | `0a18b2785ab41e27db8ff4bfc3d8d8eb05f8e2e1f689b019daf5c49880264410` | Explicit `knx-studio-demo`, version 4 envelope |

Read together: `docs/KONZEPT.md`, `ANFORDERUNGEN_UND_USER_STORIES.md`, `DATENMODELL_UND_ZUSTAENDE.md`, `AVALONIA_UMSETZUNG.md`, `PRUEFPROTOKOLL.md`; source `shell.html`, `app.js`, `domain.js`, and example envelope. Bundle checksums cover the remaining documents and tests without copying the bundle into the repository.

| Reproduced command (in bundle root) | Actual result |
|---|---|
| `node tests/domain.test.cjs` | PASS, 13 groups |
| `node tests/ui.test.cjs` | PASS, 10 integration cases, 0 failures; **simulated DOM only** |
| `py tools/build.py --check` | PASS, generated HTML, concept HTML and Markdown identical; no installation required |

The reference was served using a read-only Node HTTP server bound to **127.0.0.1:48164**, serving only `/`, and opened in OpenChamber's browser. Initial room/object view was read and Parameter clicked successfully. Browser inspection of the actual `Programmieren` button reported `rgb(8,126,129)` background, white text, 30px height, 12px text, weight 550, 5px radius, `0 10px` padding, 7px content gap and Inter/Segoe UI/Arial family. This corroborates `shell.html:9–10`; it is **not** an Avalonia visual sign-off. Browser reported desktop 1440×900 but snapshot bounds were inconsistent with that nominal viewport (responsive rules appeared active); no scale/layout conclusions are based on it. No OS mouse/keyboard automation, router access, real writes or downloads were performed. Reference screenshots and four complete browser walkthrough recordings: **PENDING**.

## Native baseline capability ledger (read-only evidence)

Paths are relative to the repository. Codes in the action inventory refer to this ledger; they mean reusable capability, **not** completed V4 wiring.

| Code | Inspected implementation / existing test project | Actual capability and V4 gap |
|---|---|---|
| B0 | `src/Knx.App/MainWindow.axaml.cs:104–173`, `App.axaml`; `tests/Knx.Docking.Tests` | Activity documents (Dashboard, GA, Catalog, Parameters, Links, Check, Monitor, Recorder), topology/properties tools; controls retained in Context. No V4 object-tabs, local view/filter/selection model or fresh-view lifetime guarantee. Float/redock mouse acceptance pending. |
| B1 | `src/Knx.Core/Model.cs:81–116`, `Buildings.cs`; `Views/BuildingPane.axaml.cs:41–160`; `tests/Knx.Buildings.Tests` | Real building tree/device ID references and assignment, first-installation building editor; topology from `MainWindowViewModel.Refresh:772–800`. No three independent V4 perspectives or effect-room GA/channel projection. |
| B2 | `src/Knx.Core/GroupLinks.cs:7–33`, `Parameters.cs`; `MainWindowViewModel.ChangeGroupLink:492–503`; `Views/GroupLinksPane.axaml.cs`; `tests/Knx.Linking.Tests` | Single-KO append/deduplicate/remove with membership/reference validation and undo. Links are string IDs. No explicit per-link sending metadata, atomic multi-KO DPT/length/Secure validation, frozen inspector selection, or GA counterview. Pane currently derives displayed DPT from linked GA, not independent KO definition. |
| B3 | `MainWindowViewModel.LoadParametersFor:529–575`, `ApplyParameterValues:613–639`; `Views/ParameterPane.axaml.cs:18–156`, `Services/ParameterItemViewModel.cs`, `src/Knx.Core/DynamicParameters.cs:59–80`, `Ksxml.cs:54–55`; `tests/Knx.Parameter.Tests`, `Knx.ParameterUi.Tests` | Existing typed manufacturer editors, validation, project overrides, undo, unsupported-feature notice. KSXML applies an initial bounded choose/when/assign parameter/block visibility evaluation; not a full dynamic KO engine or live impact preview. Drafts owned by ParameterPane and keyed by object instance: selection switching supported within that view, but not recreation-safe session drafts. Unknown dynamic/DCA behavior must stay unsupported and preserved. |
| B4 | `Services/GroupMonitor.cs:42–220`, `MonitorRefreshQueue.cs`; `MainWindowViewModel.OnTelegram`, recording methods; `src/Knx.Bus/TelegramRecorder.cs`; `tests/Knx.Monitor.Tests` | Shared UI-free thread-safe monitor, bounded 5000 display rows, filters/pause/clear/CSV, recorder and queued UI refresh. No V4 pinned room/GA/KO-all-links filter contract or coordinated bus/group/queue resource lease. Native transport APIs exist, not a hardware acceptance claim. |
| B5 | `MainWindowViewModel.DownloadAsync:698–705`, `MainWindow.axaml.cs:934–946`; `src/Knx.Bus/DownloadSession.cs:39–98`; `tests/Knx.Bus.Tests` | Existing selected-device/file-image download route; not V4 frozen queue with retry and persistent successful signatures. Real interoperability unverified; no real operation authorized here. |
| B6 | `MainWindowViewModel.RunCheck:463–474`, `src/Knx.Core/ProjectCheck.cs`; `tests/Knx.Diagnostics.Tests` | Existing project findings reusable. No V4 scope-local issues, scan simulation UI or change audit tool. Secure primitives are not a complete usable key-management product. |
| B7 | `MainWindowViewModel.LoadProject/CreateProject/SaveProject/SaveProjectAs:239–311`, `Undo/Redo:334–351`; `src/Knx.Core/Knxproj.cs`, `Undo.cs`; `tests/Knx.Archive.Tests`, `Knx.Shell.Tests` | Native archive and undo machinery, preserved unknown XML/archive data; guarded project lifecycle and drafts prompts in MainWindow. Preserve error/picker-cancel behavior. No V4 JSON validator/import adapter or demo reset policy. |
| B8 | `Services/CatalogLibrary.cs`, `CatalogBrowser.cs`, `Views/CatalogPane.axaml.cs`; `src/Knx.Core/Catalog`; `tests/Knx.Catalog.Tests`, `Knx.CatalogUi.Tests` | Real product library/background load/search/preview/insertion; integrate selected room/line, retain definitions, add clearly labeled synthetic demo templates separately. |
| B9 | `MainWindow.axaml.cs:527–605`, `src/Knx.Reports/ProjectCsvExporter.cs`, `src/Knx.Reports`; `tests/Knx.Core.Tests` | Existing GA/device/topology CSV, reports, semantic and label exports. Need V4 assignment CSV including explicit sending, demo JSON roundtrip and current-state HTML report fields. Do not remove other exports. |
| B10 | `Views/SearchPane.axaml.cs`, `src/Knx.Core/ProjectSearch.cs`, `AppSettings.cs`; `tests/Knx.Search.Tests` | Existing project search/settings/theme selection; no object/action command palette or independently validated V4 layout storage/reset. |
| B11 | NEW `src/Knx.App/Workbench/WorkbenchTheme.axaml`; NEW `tests/Knx.WorkbenchTheme.Tests` | Opt-in V4 geometry and palettes; compiled and externally included without startup edits. Theme loading/wiring belongs to shell owner. Native visual acceptance pending. |

Existing test-project names above denote available regressions, **not tests rerun in this resource-only change**. Exact source file names can be followed through their owning project; no new Core APIs are implied.

## Theme usage contract for shell/views

Include **after** existing Fluent/legacy styles, once in the intended scope:

```xml
<StyleInclude Source="avares://BusChord/Workbench/WorkbenchTheme.axaml" />
```

This is a **Styles** document: use `StyleInclude`, not `ResourceInclude`. Assembly is `BusChord`, not `Knx.App`. All semantic keys start `Wb`; Light/Dark dictionaries have identical keys. Use `DynamicResource` for palette brushes so theme switching works. Geometry can use `StaticResource`. No global Ets/Fluent/Dock resource override and no blanket descendant styling: embedding a legacy panel does not opt its controls into V4. Shell owner must apply explicit classes to new controls, including TextBlocks, and include this Styles document in detached hosts if it is not application-scoped. Keep existing theme selection and BusChord brand assets; do not copy the HTML `kx` brand mark.

| Control / classes | Exact contract |
|---|---|
| `UserControl.wb-root` | Inter (existing installed Avalonia.Fonts.Inter), 13px base, WbText/Background; does not automatically recolor all child controls |
| `Border.wb-paper`, `.wb-panel` | Paper/panel fill and WbLine border brush; view chooses actual border sides/thickness |
| `TextBlock.wb-text` | Explicit base family/size/color; combine with `wb-muted`, `wb-title`, `wb-heading`, `wb-mono`, `wb-column-header`, `wb-error`, `wb-warning`, `wb-success` as appropriate |
| `Button.wb-button` | 30px / 12px / Medium / 10px horizontal padding / 5px radius; native Button semantics |
| modifiers on `wb-button` | `wb-primary` accent; `wb-quiet` transparent; `wb-danger` error text; `wb-compact` 26px/11px/8px padding; `wb-icon` 28px square/zero padding/4px radius (use quiet for icon-only actions) |
| button content | String content or TextBlock/Icon children; foreground follows Button. For icon+label use a StackPanel with `Spacing="{StaticResource WbButtonContentGap}"` (7). Icon-only actions need `AutomationProperties.Name` **and** `ToolTip.Tip` on the Button. |
| `TabItem.wb-tab` | Native TabItem with compact header-only template, 39px document tab, selected top 2px accent edge, WbPaper; put close Button (`wb-button wb-quiet wb-icon`, local 20×20) in Header; view/VM owns closing, focus and last-tab policy |
| `TabItem.wb-tab.wb-view-tab` | Local representation tab, 34px, 12px text, selected bottom accent edge; view owns 22px inter-tab gap. Native TabControl still owns selected content. |
| `ListBoxItem.wb-row` | 32px minimum, 8×7 padding, 11px; hover, selected teal, focus and disabled states; apply class through item-container theme/style in row projection. Not a table implementation. |
| `TextBox.wb-field`, `ComboBox.wb-field` | 30px compact controls, WbPaper/Text/Line; TextBox error/focus states. Multi-line parameter editors must explicitly override Height. Preserve native validation and editor undo. |
| `views:Icon.wb-icon` | Existing `Knx.App.Views.Icon` Material Symbols control, 17px; add `wb-small` for 14px. Symbol names remain `Assets/Icons.axaml` keys, no extra icon package. |
| pseudo-classes | Button `:pointerover/:pressed/:focus-visible/:disabled`; TabItem `:selected/:pointerover/:focus-visible/:disabled`; row `:selected/:pointerover/:focus-visible/:disabled`; field `:focus/:focus-visible/:error/:disabled`. Button/row focus is a 2px accent border; disabled root opacity .46. |

Palette keys: `WbBackground`, `WbPaper`, `WbPanel`, `WbLine`, `WbText`, `WbMuted`, `WbSubtle`, `WbAccent`, `WbAccentHover`, `WbOnAccent`, `WbTint`, `WbHover`, `WbSelected`, `WbSelectedText`, `WbRail`, `WbTabbar`, `WbStatusbar`, `WbTableHeader`, `WbError`, `WbWarning`, `WbSuccess`, `WbInfo`, `WbDisabled`, `WbDisabledText` (all brushes).

Geometry keys (all prefixed `Wb`): `FontFamily`, `MonoFontFamily`; `FontSize=13`, `FontSizeControl=12`, `FontSizeSmall=11`, `FontSizeCaption=10`, `FontSizeHeading=17`, `FontSizeTitle=22`; `ButtonHeight=30`, `CompactHeight=26`, `IconButtonSize=28`, `IconSize=17`, `SmallIconSize=14`; `RowHeight=32`, `TableHeaderHeight=31`, `TreeRowHeight=29`; `TopbarHeight=49`, `StatusbarHeight=27`, `DocumentTabHeight=39`, `ViewTabHeight=34`, `ToolTabHeight=35`, `RailWidth=47`, `RailButtonSize=35`, `NavigatorWidth=272`, `InspectorWidth=350`, `ToolHeight=242`, `SplitterSize=5`; `GapSmall=5`, `ButtonContentGap=7`, `Gap=8`, `GapLarge=20`, `ViewTabGap=22`; `ButtonPadding=10,0`, `CompactPadding=8,0`, `EditorPadding=24,16,24,0`, `CellPadding=8,7`, `FocusThickness=2`; `Radius=6`, `ButtonRadius=5`, `SmallRadius=4`, `DialogRadius=11`.

Measurements are source-derived, not all measured in a browser: `shell.html:9–10` palette/type/button/focus/disabled; `:11–18` shell, navigator, tabs, tools; `:19–22` parameter/dialog spacing. Reference rows are **content-derived**, not a universal 32px fixed height: `WbRowHeight=32` is a native compact minimum, deliberately requiring shared height/virtualization and scale acceptance. CSS weight 550/650 maps to Medium/SemiBold, not pixel-identical font rendering. Reference nav bounds 200–400, inspector 290–480, tool minimum 140/max 60% window; these are layout validation rules, not forced theme sizes. Native dark palette is newly designed; reference defines no dark mode. No claim of exact dark parity or contrast sign-off.

### Resource checks performed

- `dotnet build src/Knx.App/Knx.App.csproj --nologo --no-restore`: PASS; Avalonia **12.1.4**, Dock **12.1.0.12**, existing obsolete API warnings only, no theme warnings/errors.
- `dotnet run --project tests/Knx.WorkbenchTheme.Tests`: PASS; no dependencies, validates canonical colors/geometry, key parity and opt-in selectors. This is intentionally a source-contract test, not rendering coverage.
- External temp harness at `C:\Users\julia\AppData\Local\Temp\opencode\wb-v4-theme-harness`: references App, calls `SetupWithoutStarting`, loads `StyleInclude` using the actual URI, measures/arranges a synthetic control tree without showing a window. Light/Dark button 30/26, tab 39, row min32, field30, primary foreground, root colors, .46 disabled and legacy Button28 verified. Button `PART_ContentPresenter` and tab `PART_LayoutRoot` resolved. Injected pseudo-class checks additionally passed primary hover, 2px focus border, disabled-primary surface and selected-row palette; changing the application theme updated an existing root live in both directions. No App startup modification, new package, bus connection or user settings access. This is a compile/resource/style smoke check, not keyboard/visual desktop acceptance. Harness is deliberately not committed.
- Mechanical inventory comparison against the reference: **77 dispatch cases, 78 matrix action rows (including delegated confirm-import), zero missing**.

## Full action inventory

`bundle/src/app.js:550–640` has **77 switch cases**; all are listed verbatim below. `confirm-import` is a separate delegated handler at `:722–728` and is item 78. No generic dispatcher is proposed for production: bind each command to its owning VM/session and use shared validated domain edits. All rows have native test execution **PENDING**, native visual evidence **PENDING**. `T#` identifies a proposed runnable C# acceptance case specified below; multiple commands intentionally share an end-to-end test. `S/N/L/P/M/Q/F/H` are proposed owners under `src/Knx.App/Workbench`: Shell / Navigator / Links-Inspector / Parameters / Monitor session / Queue-Diagnostics / File-Export / Help-Palette. They are not existing source files.

| # | Exact action | Planned native owner.member (`Command` unless noted) | Test | Baseline / remaining work |
|---|---|---|---|---|
| 01 | `open-scope` | S.OpenScope(type,id) | T01,T02 | B1 identities reusable; V4 tabs missing |
| 02 | `open-device` | S.OpenDevice(id) | T01 | B1; no object tab |
| 03 | `open-ga` | S.OpenGroupAddress(id) | T04 | B2; no counterview tab |
| 04 | `open-ko` | S.OpenObject(id) | T04 | B2; reveal correct device/row including inactive missing |
| 05 | `search-open` | H.OpenResult(scope) | T12 | B10 search reusable, palette and modal dismissal missing |
| 06 | `open-project` | S.OpenProjectScope | T02 | B7 lifecycle differs from opening a project-scope tab |
| 07 | `open-docs` | S.OpenDocumentation | T11 | B9 exports reusable; V4 docs view missing |
| 08 | `open-tools` | S.OpenTools | T10 | B0 no V4 tool overview |
| 09 | `nav-kind` | N.SelectPerspective(kind) | T01,T02 | B1 no independent three-tree state |
| 10 | `toggle-nav` | S.ToggleNavigator | T13 | B0 fixed layout replacement needed |
| 11 | `activate-tab` | S.ActivateTab(key) | T02 | B0 activity docs are not scope tabs |
| 12 | `close-tab` | S.CloseTab(key) | T02 | B0; close/focus/last-tab policy missing |
| 13 | `view` | S.SetRepresentation(view) | T02,T06 | B3 existing editors, local representation missing |
| 14 | `collapse-device` | S.ActiveTab.ToggleExpanded(id) | T03 | B0 hierarchical shared-column projection missing |
| 15 | `expand-all` | S.ActiveTab.ToggleAllExpanded | T03 | B0 whole-model expansion/filter semantics missing |
| 16 | `select-ko` | S.ActiveTab.SelectObject(id,additive) | T03 | B2 single-KO pane not tab-local multiselect |
| 17 | `check-ko` | S.ActiveTab.ToggleObjectSelection(id) | T03 | B2 missing |
| 18 | `select-all` | S.ActiveTab.SelectAllFiltered | T03 | B2 include collapsed/unrealized rows missing |
| 19 | `clear-selection` | S.ActiveTab.ClearSelection | T03 | B0 no per-tab selection |
| 20 | `inspect-ko` | L.InspectObject(id) | T04 | B2 pane reusable; new inspector missing |
| 21 | `inspect-device` | L.InspectDevice(id) | T01 | B1/B0 details reusable; room/line/address editor needed |
| 22 | `inspect-selected` | L.InspectSelection(snapshot) | T04 | B2 frozen multi-selection inspector missing |
| 23 | `close-side` | S.CloseInspector | T02 | B0 visibility without state loss missing |
| 24 | `compare` | L.ToggleGroupCounterview | T04 | B2 pinned independent GA inspector missing |
| 25 | `link-selected` | L.BeginAssignment(tabSelection) | T04 | B2 atomic multi-target workflow missing |
| 26 | `link-side-selection` | L.BeginAssignment(inspectorSnapshot) | T04 | B2 preserve frozen selection across navigation |
| 27 | `link-ko` | L.BeginAssignment([id]) | T04 | B2 add path reusable; preview missing |
| 28 | `choose-ga` | L.SelectTargetGroupAddress(id) | T04 | B2 no preflight reason/duplicate count |
| 29 | `apply-links` | L.ApplyAssignments | T04 | B2 add atomic all-target validation + one undo transaction |
| 30 | `remove-link` | L.RemoveAssignment(ko,ga) | T05 | B2 remove exists; sending-removal warning missing |
| 31 | `sending-ga` | L.SetSendingAddress(ko,ga) | T05 | B2 explicit sending metadata absent |
| 32 | `reveal-topology` | N.RevealDeviceInTopology(id) | T01 | B1 reveal/expand/focus missing |
| 33 | `device-params` | S.OpenDeviceParameters(id) | T06 | B3 editor reusable; correct subject tab missing |
| 34 | `discard-params` | P.DiscardDraft(id) | T06 | B3 pane-only drafts; per-device durable service needed |
| 35 | `review-params` | P.ReviewDraft(id) | T06 | B3 validation reusable, nonmutating effect preview missing |
| 36 | `apply-params` | P.ApplyReviewedDraft(id) | T06 | B3 atomic values exists, KO effects and retained links missing |
| 37 | `save-device` | L.ApplyDeviceProperties(id) | T01,T07 | B1 rename exists; full room/line/address validated edit needed |
| 38 | `new-ga` | L.PrepareNewGroupAddress | T04 | B2 basic creation exists, explicit form defaults needed |
| 39 | `create-ga` | L.CreateGroupAddress | T04 | B2/B7 add address/name/DPT/room validation and target selection |
| 40 | `catalog` | F.ShowCatalog(targetScope) | T01 | B8 real catalog reusable; synthetic templates labeled separately |
| 41 | `insert-device` | F.InsertCatalogDevice(target) | T01 | B8 insertion exists; room+line identity and free address verify |
| 42 | `toggle-monitor` | S.ToggleTool(Monitor) | T09 | B4 session exists, V4 hide/show contract missing |
| 43 | `close-dock` | S.HideToolArea | T09 | B0 hide must not stop sessions |
| 44 | `dock-tool` | S.SelectTool(kind) | T09,T10 | B0 new bottom tool area needed |
| 45 | `maximize-dock` | S.ToggleToolMaximized | T13 | B0 layout state missing |
| 46 | `watch-ga` | M.WatchGroupAddress(id) | T09 | B4 pin filter and start/hide lifetime needed |
| 47 | `watch-scope` | M.WatchScope(scope,selection) | T09 | B4 project/room/GA policy needed |
| 48 | `monitor-toggle` | M.ToggleCapture | T09,T10 | B4 real monitor/recorder differ; explicit demo mode and lease needed |
| 49 | `monitor-clear` | M.ClearDisplay | T09 | B4 Clear exists; full recording must not silently be discarded |
| 50 | `monitor-export` | M.ExportVisibleCsv | T09,T11 | B4 CSV exists; scope filter and full recording distinction needed |
| 51 | `monitor-value` | M.OpenValueDialog | T09 | B4 real read/send APIs exist; explicit simulation dialog needed |
| 52 | `simulate-read` | M.SimulateRead(ga) | T09 | B4 fake transport required; R/sending response semantics verify |
| 53 | `simulate-write` | M.SimulateWrite(ga,value) | T09 | B4 validate finite typed range; no real bus write |
| 54 | `program` | Q.PrepareProgramming | T08 | B5 no frozen job dialog |
| 55 | `start-program` | Q.StartSimulatedJobs(snapshot) | T08 | B5 explicit simulation, frozen signature + config, running locks needed |
| 56 | `retry-job` | Q.RetryJob(id) | T08 | B5 replay same failed frozen job, not current device config |
| 57 | `run-scan` | Q.RunSimulatedLineScan(line) | T10 | B6 no V4 scan; lease conflict checks required |
| 58 | `all-issues` | Q.ShowProjectIssues | T10 | B6 existing findings reusable; tool UI missing |
| 59 | `show-changes` | Q.ShowChangeHistory | T07 | B7 undo available, readable bounded audit missing |
| 60 | `tool` | S.OpenTool(kind) | T10,T12 | B0 route all 10 tool kinds explicitly (see below) |
| 61 | `undo` | S.UndoProject | T04,T06,T08,T14 | B7 preserve editor-local undo and successful device baseline |
| 62 | `redo` | S.RedoProject | T04,T06,T14 | B7 redo exists; refresh identity and V4 drafts policy needed |
| 63 | `menu-project` | S.OpenProjectMenu | T12 | B7 retain native new/open/save/as/recent in V4 menu |
| 64 | `menu-edit` | S.OpenEditMenu | T12 | B7 undo/selection commands to wire |
| 65 | `menu-view` | S.OpenViewMenu | T13 | B0 new panel/focus/reset actions |
| 66 | `focus-layout` | S.FocusEditorLayout | T13 | B0 hide UI only, no project/session changes |
| 67 | `reset-layout` | S.ResetLayout | T13 | B10 validate defaults; reset inspector width too, no project reset |
| 68 | `palette` | H.ShowPalette | T12,T14 | B10 search reusable; object and action results needed |
| 69 | `concept` | H.ShowWorkbenchHelp | T12 | B0 integrated help/limitations/source links needed |
| 70 | `close-modal` | S.DismissDialog | T14 | B0 native dialog focus restore, no implicit apply |
| 71 | `export-project` | F.ExportDemoProjectJson | T11 | B7 no V4 JSON adapter; distinguish native archive save |
| 72 | `export-ga` | F.ExportGroupAddressesCsv | T11 | B9 reuse exporter, include effect-room and Secure if supported |
| 73 | `export-links` | F.ExportAssignmentsCsv | T11 | B9 explicit sending+active metadata columns missing |
| 74 | `export-report` | F.ExportProjectHtml | T11 | B9 current V4 state/report and escaping verify |
| 75 | `import-project` | F.SelectDemoImport | T11 | B7 native load differs; prepare and validate before replacement |
| 76 | `reset` | F.ConfirmDemoReset | T11,T14 | B7 separate destructive demo confirmation, export option |
| 77 | `confirm-reset` | F.ResetDemoProject | T08,T11 | B7 running-job guard; drafts/project/session reset policy missing |
| 78 | `confirm-import` | F.ApplyValidatedDemoImport | T11 | `app.js:722–728`, outside switch; atomic publish, no trusted downloaded baseline |

### Non-dispatch input, lifecycle and keyboard inventory

These events are equally binding; counting switch cases alone misses them.

| ID | Reference | Planned native binding / test | Baseline and required behavior (all acceptance PENDING) |
|---|---|---|---|
| E01 | `app.js:653–658`, `nav-filter` | N.Perspective.Query / T02 | B1 independent query per building/topology/GA, preserve focus/caret |
| E02 | `object-filter` | S.ActiveTab.Query / T03 | B0 model-wide search incl device/name/address/DPT/KO/GA, not realized Controls |
| E03 | `link-filter` | L.TargetQuery / T04 | B2 target filter independent of editor selection |
| E04 | `palette-query` | H.Query / T12 | B10 result search, no-result state, Enter first result |
| E05 | `data-param`, `:659–663` | P.SetDraftValue(device,key,value) / T06 | B3 typed validation, empty numeric value remains invalid draft, no project mutation |
| E06 | `related`, `:667` | S.ActiveTab.IncludeRelated / T03 | B1 effect-channel projection; clear only that tab selection |
| E07 | `unlinked`, `:668` | S.ActiveTab.OnlyUnlinked / T03 | B2 whole model filter, reset local selection |
| E08 | `parameter-device`, `:669` | P.SelectedDeviceId / T06 | B3 draft switching without loss |
| E09 | `compare-ga`, `:670` | L.PinnedGroupAddressId / T04 | B2 counterview selection survives editor navigation |
| E10 | `monitor-mode`, `monitor-interface`, `:671–672` | M.Mode/M.Interface / T09,T10 | B4 reject changes while running; visible SIMULATION indicator |
| E11 | `monitor-filter`, `:673` | M.PinFilter / T09 | B4 manual choice disables following; fixed scope survives tabs |
| E12 | `monitor-follow`, `:674` | M.FollowSelection / T09 | B4 every linked GA for selected KO; no selection yields empty filter, not all |
| E13 | `scan-line`, `:675` | Q.ScanLineId / T10 | B6 switch line clears stale observations |
| E14 | details `toggle`, `:677–679` | N.SetExpanded(perspective,id) / T02 | B1 independent persisted expansion |
| E15 | `keydown`, `:680–686` | S.KeyBindings / T14 | Ctrl/Cmd K always palette; B/J outside editors toggle nav/tool; Z/ShiftZ outside editors project undo/redo. Local text undo takes precedence. |
| E16 | Escape, `:687–691` | S.DismissTopTransient / T14 | Dialog then popup then inspector; restore originating focus |
| E17 | Enter/Space tab, Enter palette, Tab modal, `:692–697` | native tab/dialog input / T14 | Native roles, focus trapping, ShiftTab, close-button labeling; never emulate with mouse-only Border |
| E18 | divider arrows, `:698–701` | S.ResizePanel / T13,T14 | keyboard alternative to pointer; 20 DIP steps, validated bounds |
| E19 | divider pointer events, `:703–710` | native GridSplitter + S.Layout / T13 | clamp sizes; persist after drag; no shared desktop automation in this change |
| E20 | `project-import.change`, `:711–721` | F.PrepareDemoImport / T11 | size ≤2,500,000 bytes; format/version/schema/ref validation; cancel/error keeps current project, drafts, undo, path |
| E21 | initialization/storage, `:61–74`, `:89–93` | F.ProjectStore + S.LayoutStore / T11,T13 | native archive and separate validated settings; corrupt layout fallback; invalid tabs pruned; errors observable, no silent data loss |
| E22 | render scroll/focus, `:365–375` | S.TabViewState / T02,T14 | keep per-tab scroll/expanded/filter/caret without retaining/reparenting live Controls |
| E23 | timer, `:337–355` | M.FakeTransportTick / T09 | explicit simulated source; bounded display vs full recording; UI batches; no duplicate session on reopen |
| E24 | async queue, `:469–480`, mutation guard `:95–112` | Q.Session.Run + mutation guard / T08 | hidden operation continues, device lock, fail never updates baseline; undo does not roll successful transfer back |
| E25 | popup/outside click, `:641–651`, modal `:377–384` | native popup/dialog dismissal / T14 | disabled cannot invoke, outside dismiss no apply; restore focus |
| E26 | `tool(id)`, `:534–545`, tool cards `:255–268` | S.OpenTool / T10,T12 | all kinds: `monitor`, `bus`, `scan`, `check`, `queue`, `secure`, `catalog`, `docs`, `changes`, `concept`; no placeholder silently dropped |

## Requirements, stories and four acceptance flows

Each row: reference → proposed native implementation → runnable test → baseline capability/gap. **Native automated result and visual evidence for every row: PENDING.** B11 alone is not evidence of any functional criterion.

| ID | Binding acceptance / reference | Native owner and proposed test | Actual baseline / gap |
|---|---|---|---|
| A01 | Three trees, same entities (`ANFORDERUNGEN:13`) | N projections, T01,T02 | B1 refs exist; V4 three-tree state missing |
| A02 | Room/line/device/GA independent tabs (`:14`) | S.ScopeTab state, T02 | B0 activity tabs cannot meet this |
| A03 | Effect ≠ installation (`:15`) | N.RoomObjectProjection, T03 | B1 installation exists; room→GA→external-channel metadata absent |
| A04 | Hierarchical shared-column central table (`:16`) | S.RowProjection + inspector/tool layout, T03,T13,T15 | B0 no hierarchical KO grid; wide/virtualized projection and shared columns required, flat list not equivalent |
| A05 | Validated changes, preview, undo (`:17`) | L/P edit service, T04–T06 | B2/B3/B7 partial reuse; all-target preflight/effects missing |
| A06 | Session lifetime independent (`:18`) | M/Q session services, T08–T10 | B4 partially shared; recreation/lease/frozen queue missing |
| A07 | Native AXAML/C#/MVVM (`:19`) | S/new view models, T13,T16 | B0 native app; Avalonia12.1.4/Dock12.1.0.12 adopted. No commercial TreeDataGrid assumed. Implement own virtualized hierarchy; TableView allowed for read-only lists. |
| US01 | Insert product in room, find SAME device under line (`:25`) | F.Insert + N.Reveal, T01 | B8/B1 real insert/refs reusable; V4 workflow pending |
| US02 | Living-room external channel A; NOT kitchen B (`:26`) | N.ScopeObjects, T03 | B1 missing effect-room metadata/projection |
| US03 | Atomic multi-KO reject on incompatible target (`:27`) | L.ApplyAssignments, T04 | B2 only single-link ref validation; full DPT/length/capability reasons pending |
| US04 | Append, retain prior GA, suppress duplicates (`:28`) | L.ApplyAssignments, T04 | B2 single-link duplicate handling present; batch transaction pending |
| US05 | Explicit unique sending, removal warns no replacement (`:29`) | L.SetSending/Remove, T05 | B2 string links contain no sending field; T-flag is not sending address |
| US06 | Nonmutating switch/dim preview; retain inactive links (`:30`) | P.Review/Apply, T06 | B3 real static editor reuse, demo effects and supported dynamic impact adapter pending |
| US07 | Dirty by bus config, not room/name/revision (`:31`) | Q.ConfigurationSignature, T07 | B5 baseline/signature absent; DO NOT use Undo revision as hardware truth |
| US08 | Fail/retry frozen job, success survives undo (`:32`) | Q.Job/session baseline, T08 | B5 queue absent; real download APIs not equivalence |
| US09 | Pin GA during navigation; follow all KO GA; hide keeps run (`:33`) | M.ScopeFilter/session, T09 | B4 bounded monitor reusable; all-links filter/session recreation pending |
| US10 | Current CSV/HTML, validated demo JSON roundtrip (`:34`) | F exports/import adapter, T11 | B7/B9 partial; preserve native archives independently |
| FLOW1 | `KONZEPT:55`: room→catalog insert→topology same device | T01, screenshots room/catalog/device/line | Native execution and screenshots pending |
| FLOW2 | `KONZEPT:56`: multiselect→assign→send→GA view→reject→undo | T04,T05, inspector/counterview screenshots | Native execution and screenshots pending |
| FLOW3 | `KONZEPT:57`: switch/dim preview→apply→dirty→queue fail/retry→undo | T06–T08, preview/queue screenshots | Native execution and screenshots pending |
| FLOW4 | `KONZEPT:58`: GA→monitor pin→navigate→hide/reopen→back-reference | T09, monitor/alternate-tab screenshots | Native execution and screenshots pending |

## Exact proposed runnable C# acceptance cases

Targets: new native/domain console regressions under `tests/Knx.Workbench*.Tests`, reusing existing console-test conventions and synthetic data. **None of T01–T16 is implemented by this theme change.** Every row needs implementation commit, command/result, evidence path and remaining finding before sign-off. The minimal theme source check is separate from these acceptance tests.

| Test | Required assertions |
|---|---|
| T01 `IdentityAcrossPerspectivesAndInsertion` | Insert synthetic product into chosen room/line, next free valid address; room/line/GA views resolve same stable device ID; rename/location/line validation; reveal in topology expands correct ancestors. Real catalog remains available, no manufacturer fixture committed. |
| T02 `IndependentScopeTabState` | Room/line/device/GA open concurrently; switch representation/filter/selection/expanded/scroll in one without changing others; close/reopen according to policy; same model identity after undo replaces instances; fresh views do not share Control parents. |
| T03 `HierarchicalRoomProjectionAndSelection` | Shared columns for devices/channels/KO; living room includes linked external channel A, excludes kitchen B, installation unchanged; unlinked/text filtering includes collapsed/unrealized rows; select-all only active filtered rows; bounded GA summary with full count/editor list; expand/collapse all. |
| T04 `AtomicAssignmentAndCounterview` | Preflight all selected and frozen-inspector KO; reject entire batch on DPT, bits, inactive or Secure/capability mismatch; no partial edit/undo entry. Append retains old links, duplicates no-op; valid batch one undo; pinned target independent of tab; new GA validation; GA counterview updates; undo/redo all projections. Drag-drop and keyboard command same service. |
| T05 `ExplicitSendingAssociation` | Choose existing active-KO assignment; at most one sending; independent of T/R/W flags; remove sending with remaining links warns and never autochooses; nonmember rejected; save/load and undo preserve marker; inactive links inspectable. |
| T06 `DraftLifetimeAndImpactTransaction` | Device-local typed drafts/errors/baseline survive selection/tab/hide/recreate; preview changes nothing, includes value diff + supported KO effects + affected retained links; apply one validated undo; discard explicit; demo mode switch/dim only synthetic definitions; unsupported manufacturer dynamic/DCA warning with opaque data retained. |
| T07 `ConfigurationSignatureAndAudit` | Address/line/parameters/active/flags/DPT/GA address/security/links/sending affect signature; name/installation-only edits do not. Stable ordering; audit readable and bounded; signature is not undo revision. |
| T08 `FrozenQueueFailureRetryAndUndo` | Job freezes actual config plus signature; queued→running→done/failed, visible reason; failed leaves baseline untouched; retry same frozen config even after newer edits; successful baseline survives undo; hidden/recreated view leaves running job; lock relevant device edits; reset/import while running denied/coordinated. Simulation unmistakable, no hardware transport. |
| T09 `MonitorScopeAndLifetime` | Start/stop distinct from hide; mode/interface immutable while running; fixed project/room/GA survives nav; follow includes every KO GA, no selection means none; recreate no duplicate subscriptions/session; hidden ticks received; display bounded, full recorder separate; filtered CSV vs full export; validated simulation read/write, R-response uses explicit sending GA; telegram backreferences. |
| T10 `ToolRoutingDiagnosticsAndLeases` | Open each of ten tool kinds; scoped/all findings; read-only Secure limitations; scan observation distinct from project config; line switch invalidates old scan; conflicting bus monitor/scan/program cannot silently coexist; release leases on stop/failure, never just on hide. |
| T11 `ProjectImportExportAndFailureSafety` | New/open/save/as/recent/native archive unknown data and encryption boundaries preserved; corrupt/import-wrong-envelope/version/refs/duplicates/size/parameter values rejected before publication; cancelled/failed picker/storage/settings keeps project,path,selection,undo,drafts; demo roundtrip does not trust downloaded truth; CSV/HTML current values/escaping/encoding/formula-safe spreadsheet handling/error paths; demo reset own confirmation, export first, no running job data loss. |
| T12 `MenusPaletteHelpAndLegacyReachability` | Project/edit/view menus and all search object/action targets operate; no-results/Enter handling; integrated help/source links/unsupported features; all old catalog/semantic/reports/labels/project/bus functions still reachable, not just renamed placeholders. |
| T13 `LayoutPersistenceAndTheme` | Splitters resize and clamp nav/inspector/tool bounds; show/hide/focus/maximize/reset preserve project and sessions; invalid persisted layout safe fallback; small window no hidden actions; native light/dark reference comparison. Theme opt-in must not recolor embedded legacy controls. |
| T14 `KeyboardAndAccessibility` | Ctrl+B/J/K/Z/ShiftZ routing and text-editor undo priority; Tab/ShiftTab tree/tabs/multiselect/assign/review/apply/cancel; Enter/Space activation; Escape priority, focus trap/restore, icon accessible names/tooltips, keyboard splitter equivalent; real rendered light/dark focus/error/disabled contrast. |
| T15 `LargeProjectionPerformance` | >10,000 synthetic KO, expanded hierarchies/filter/multiselect/live monitor; demonstrate virtualization and equal rows/shared columns; measure load time, p95 filter/interaction, memory and UI refresh frequency. Acceptance thresholds, machine, values **PENDING**, agreed before acceptance; no HTML performance inference. |
| T16 `DetachedToolsAndRegressionGate` | Exclusive native mouse session: three float/redock cycles each supported tool, no crash/empty host/ghost window/lost selection/drafts/session; test factory calls not mouse evidence. Existing console suite + new workbench suite, Release build, smoke, screenshot-selftest, MCP selftest and CI at exact integration SHA; independent review against full matrix. |

## Open evidence and release gate

- Pending native screenshots: initial scope/object grid, multiple scope tabs, parameter editor and impact dialog, relationships, GA counterview, catalog, single/multi inspector, monitor, queue, menus/help/import/reset dialogs; hidden panels/focus/maximized tools; small window; Light/Dark at 100/150/200%.
- Pending keyboard/contrast/high-DPI and native float/redock checks, memory/timing measurements and agreed performance thresholds. Theme smoke test does not prove these.
- Pending domain/session/metadata format implementation and serialization/unknown-data preservation. Demo `domain.js.dirtyDevices` uses revisions, whereas `app.js:59,82` uses signatures: the native contract must follow the latter. `app.js` bounds retained telegrams at 500/display at 120; the approved plan deliberately requires a separate complete recording stream.
- Native project new/open/save/save-as/recent and existing exports are **additional** approved-plan obligations beyond the HTML dispatcher. Keep those and useful old shortcuts (e.g. native save/open), not just the 78 HTML actions.
- Reference simulation is not KNX conformance. No real write/download/Secure success, full dynamic manufacturer compatibility, DCA hosting, encrypted-project password recovery or new commercial dependency is authorized or claimed.
- Resource owner changes only the new theme, this inventory and optional dependency-free source check. Shell wiring, existing theme loading and all functional code remain for their designated owners. Canonical plan checkboxes are deliberately not ticked: whole steps and independent integration acceptance are not complete.
