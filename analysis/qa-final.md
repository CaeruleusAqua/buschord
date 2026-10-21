# Workbench V4 — independent final QA at integration HEAD

Read-only validation. The only repository change is this report. No product source, test,
CI or existing analysis file was modified.

- Base: `5183a0fc456acbe3a475ab221b123068f0ab07c5` (`development`, verified)
- Branch: `agents/final/qa`
- Worktree: `C:\projects\ai_work\agent-wt\final-qa`
- Diff reviewed against `bd51a8e6b2c8eb09788df4b14a8ffd66377ce65a` (37 commits)
- Date: 2026-10-21
- Environment: Windows, .NET SDK 9.0.302, Avalonia 12.1.4, 96 dpi (100 %), single monitor
- Full run repeated from a cleaned solution; all commands and logs under
  `%TEMP%\qa-rerun\` (tests in `tests\`, screenshots in `shots\`)

**Result: no P1 reproduced.** The local Release gate is green: build 0 errors,
24 console test projects, 3 native window suites, MCP self-test, app smoke,
screenshot self-test and the Workbench self-check in all four language/theme
combinations pass. Remaining issues are P2/P3; the two new P2 findings are the
catalog product list at 900×600 and missing initial focus in three dialogs.

## 1. Build and test run

### Build

```text
dotnet clean ets6.sln -c Release --nologo          exit 0
dotnet restore ets6.sln --nologo                   exit 0
dotnet build ets6.sln -c Release --no-restore --nologo
   0 errors, 21 warnings, 31.5 s                    exit 0
```

The 21 warnings are pre-existing (obsolete `TextBox.Watermark`,
`Window.SystemDecorations`, `Bitmap.Save(string, int?)`, CS1998 in `KnxRouting`,
CS8602/CS8604 nullability). None is new in this range.

### Console test projects (all `dotnet run --project <p> -c Release --no-build`, exit 0)

| Project | Checks | Final line |
| --- | ---: | --- |
| tests/Knx.Core.Tests | 243 PASS | `ALLE CHECKS OK` |
| tests/Knx.Bus.Tests | 112 PASS | `ALLE CHECKS OK` |
| tests/Knx.Owl.Tests | 56 PASS | `ALLE CHECKS OK` |
| tests/Knx.Catalog.Tests | 85 PASS | `ALLE CHECKS OK` |
| tests/Knx.Mcp.Tests | 107 PASS | `ALL TESTS PASSED` |
| tests/Knx.Address.Tests | 19 PASS | `ADDRESS CHECKS OK` |
| tests/Knx.Linking.Tests | 33 PASS | `ALL LINKING TESTS PASSED` |
| tests/Knx.SmartLink.Tests | 88 checks | `SmartLinking: 88 checks passed` |
| tests/Knx.CatalogUi.Tests | 100 checks | `CATALOG UI OK (100 checks; headless, no visual validation)` |
| tests/Knx.Archive.Tests | 143 checks | `ALL 143 ARCHIVE CHECKS PASSED` |
| tests/Knx.Icon.Tests | 410 checks | `OK 410 checks` |
| tests/Knx.Monitor.Tests | 51 PASS | `ALL PASS` |
| tests/Knx.Diagnostics.Tests | 41 PASS | `ALLE CHECKS OK` |
| tests/Knx.Search.Tests | 39 PASS | `ALL TESTS PASSED` |
| tests/Knx.Buildings.Tests | 105 PASS | `ALLE BUILDING CHECKS OK` |
| tests/Knx.Encryption.Tests | 30 checks | `ALL 30 ENCRYPTION CHECKS PASSED` |
| tests/Knx.Parameter.Tests | 78 checks | `PASS 78 parameter checks` (MDT corpus skipped) |
| tests/Knx.ParameterUi.Tests | 88 PASS | `PARAMETER UI CHECKS OK` |
| tests/Knx.Workbench.Tests | 9 PASS | `Workbench checks OK: …` |
| tests/Knx.WorkbenchUiState.Tests | 38 PASS | `WORKBENCH UI STATE OK (38)` |
| tests/Knx.WorkbenchSessions.Tests | 128 checks | `128 checks passed.` |
| tests/Knx.WorkbenchTheme.Tests | 1 PASS | `PASS theme source contract and text contrast (not a native rendering test)` |
| tests/Knx.WorkbenchPerformance.Tests | 1 PASS | `WORKBENCH LARGE-PROJECTION CHECKS OK` |
| src/Knx.App.Tests | 34 PASS | `REPORTS OK` |

### Windows-native suites and app gates

```text
dotnet run --project tests/Knx.Docking.Tests -c Release --no-build
   15 PASS, WORKBENCH DOCKING OK                                  exit 0
dotnet run --project tests/Knx.Shell.Tests -c Release --no-build
   49 PASS, SHELL DRAFT LIFECYCLE OK                              exit 0
dotnet run --project tests/Knx.ParameterUi.Tests -c Release --no-build -- --native
   18 PASS, NATIVE DRAFT CHECKS OK                                exit 0
dotnet run --project src/Knx.Mcp -c Release --no-build -- --selftest
   22 PASS, SELFTEST PASS (stderr "parse error" is the expected malformed-JSON case)
dotnet run --project src/Knx.App -c Release --no-build -- --smoke
   53 PASS, SMOKE OK                                              exit 0
dotnet run --project src/Knx.App -c Release --no-build -- --screenshot-selftest
   6 PASS, SCREENSHOT OK                                          exit 0
```

Workbench self-check, all four combinations, 83 PASS each, exit 0:

```text
dotnet run --project src/Knx.App -c Release --no-build -- --workbench-selftest --theme=light --lang=de
dotnet run --project src/Knx.App -c Release --no-build -- --workbench-selftest --theme=dark  --lang=de
dotnet run --project src/Knx.App -c Release --no-build -- --workbench-selftest --theme=light --lang=en
dotnet run --project src/Knx.App -c Release --no-build -- --workbench-selftest --theme=dark  --lang=en
   WORKBENCH NATIVE OK (×4)
```

CI was not run. The workflow was read and its Windows job matches the local
commands; see §6.

## 2. Screenshots

Product screenshot path (`--screenshot=<path> --screen=<name> --theme=light|dark --lang=en`),
32 PNGs, all exit 0:

- `demo`, `demoparams`, `demomonitor`, `demoinspector`, `democatalog`, `demolinks`,
  `demoprogramming`, `settings` at 1440×900 light/dark;
- `demo-small`/`demo-tiny`, `demoparams-small`, `demomonitor-small`/`-tiny`,
  `demoinspector-small`/`-tiny`, `democatalog-small` at 1024×700 / 900×600.

The product path has no tiny variants for `demoparams`/`democatalog` and no
small/tiny variants for `demolinks`/`demoprogramming`; an external harness
(below, not committed) rendered those combinations from the built
`BusChord.dll` and additionally captured the DCA read-only state and the
dialogs.

Harness (external, `%TEMP%\opencode\qa-final-harness`, exit 0 both themes):

```text
catalog products=1 selected=0 bg=#ffdff0ef (light) / #ff25484b (dark)
dca readOnly=True reason=The DCA/plug-in does not run here: parameters are read-only and download
    is unsupported. Insert remains possible. rows=1
Help-focus=null
NewProject-focus=null
ReplaceProduct-focus=null
```

All screenshots: `%TEMP%\qa-rerun\shots\`.

### Findings by size

| Size | Surface | Result |
| --- | --- | --- |
| 1440×900 | demo, params, monitor, inspector, catalog, links, programming, settings | No clipping. Selected table row and catalog row use `WbSelected`/`WbSelectedText`; GA chips, checkbox column and row context menu are visible. |
| 1440×900 | inspector (EN) | **Clipping:** caption `Room reference (opti…` is cut by the fixed 104 px caption column (`WorkbenchWindow.axaml.cs:1508-1509`). Present in light and dark. |
| 1024×700 | demo, params, monitor, inspector, links | No clipping; representation tabs stay on one row, table shows object/DPT/length/flags with a horizontal scrollbar, footnote outside the scroll. |
| 1024×700 | catalog | Product name truncates to `Spannungsv…`; still usable. Details pane scrolls. |
| 900×600 | demo, params, monitor, inspector, links | No clipping; representation tabs wrap, table keeps a horizontal scrollbar, dock toolbar wraps into two rows without covering the body, inspector overlay keeps the editor reachable. |
| 900×600 | catalog | **Broken product list:** with navigator visible, the catalog grid (`CatalogPane.axaml:126`, columns `200,*,290`) leaves the product list ~100 px; its inner `*,90,45` columns leave no room for the name. The selected row shows only the order number (`DCA-1`/`75010009`), the product name is invisible. Light and dark. |
| 900×600 | DCA params | Read-only panel and reason shown, editor disabled, no Apply/Reset, values unchanged. |
| fixed dialogs | help, new project, replace product | V4 chrome, localized, no clipping; spinners are themed. **No initial focus in any of the three** (see §5). |

Contrast sampled from the rendered PNGs: disabled `Insert`/`Remove catalogue`
`#5f6b7d` on `#eef0f4` = 4.74:1; dark selected row 8.66:1; catalog selected
row uses the token, not Fluent grey.

## 3. Diff review against `bd51a8e`

Reviewed `git diff bd51a8e..HEAD`: 66 files, +5057/−3996. The range removes the
legacy `MainWindow`/Dock surface and its panes, adds the V4 dialog/help/table
work, the catalog index and the acceptance documentation.

### Unintended changes

No unintended product change found.

- `MainWindow`, the four legacy panes and the Dock packages are removed
  deliberately; no reference remains (`README.md` updated), and the solution
  builds without Dock packages.
- `Screenshot.cs` loses the legacy `MainWindow` capture and keeps the V4 path.
- CI changes are additive: explicit Linux skip notices and `--no-build` for the
  Docking/Shell steps that already build in the solution step.
- `KnxProductCatalog` moves `FindProduct`/`FindProgramById` to dictionaries
  populated on the same parse paths; duplicate handling matches the old
  `FirstOrDefault`/`Any` behavior (`KnxProductCatalog.cs:268-303,445-446,510-562`).
- `WorkbenchWorkspace` layout JSON gains `Favorites`; missing fields default to
  empty, so old layout files still load.

### Dead code

- `MainWindowViewModel.DownloadAsync` (`src/Knx.App/MainWindowViewModel.cs:839`)
  has no caller. Legacy, P3.
- `ParameterPane.axaml:63` hardcodes `Content="Cancel review"`; the runtime
  overwrites it in `ParameterPane.axaml.cs:120`, so it is a design-time fallback
  only. P3.
- The two `async` methods without `await` in `WorkbenchWindow.axaml.cs:871,975`
  are pre-existing style warnings, not new dead code.

### Secrets / manufacturer data

No secrets, credentials, tokens, user paths or sample-catalog paths were added
to the diff. The only vendor name in the range is the phrase "MDT corpus
skipped" in the acceptance report. Product code contains no manufacturer data.

### Test weakening

- No assertion was weakened; existing checks were migrated from `MainWindow` to
  `WorkbenchWindow`, and the new `WorkbenchUiState`/`WorkbenchTheme` checks were
  added (DCA read-only, favorites, drop validation, contrast).
- Coverage was **reduced** in `tests/Knx.Shell.Tests`: the previous UI-level
  flows for New wizard (cancel/create/failure), the recent-project list and the
  Exit menu are gone; the file keeps view-model transaction checks for the same
  failure states and the Save/Open/Close lifecycles. The successful New-project
  UI path is now not covered end-to-end anywhere. QA note, P3.
- `tests/Knx.Docking.Tests` now asserts that a floated tool window does **not**
  route shell Ctrl+Z/Ctrl+Y (`tests/Knx.Docking.Tests/Program.cs`, "floating tool
  window ignores the shell undo shortcut"). The old MainWindow float handled
  project undo. This is a deliberate behavior change in the new tool-window
  model; it should be confirmed as intended. P3.

### Shared-contract drift

- The workbench layout contract (`workbench-layout.json`) gains an optional
  `Favorites` array; old files remain valid.
- `CatalogPane` read-only mode is fed through `ParameterEditingSession` and
  re-evaluated on `CatalogLoaded`; no second parameter model was introduced.
- Docking/Shell tests now depend on `WorkbenchWindow` internals (menu routing,
  `ParameterPane` lookup) instead of Dock; the contract is the shell's public
  routed actions, not the old Dock factory.
- No signature or data-format break was found in `Knx.Core`/`Knx.Bus`.

## 4. Re-audit of P1/P2 from the named reports

Status values: **fixed** = verified in source and, where a run exists, in a
native run/screenshot; **open** = still reproduced; **accepted** = documented
decision, not a defect.

### `analysis/ui-consistency/tokens.md`

| ID | Item | Status at HEAD | Evidence |
| --- | --- | --- | --- |
| T1 | Selected rows miss selection tokens | **fixed** | `App.axaml:362,366` name `#PART_ContentPresenter`; `WorkbenchWindow.axaml:16`; catalog local rules `CatalogPane.axaml:43-53`. Rendered selected catalog/table rows are `#dff0ef`/`#25484b`, not Fluent grey. |
| T2 | Disabled buttons unreadable | **fixed** | `WorkbenchTheme.axaml:320-328`, `App.axaml:192`; rendered 4.74:1 light / 5.47:1 dark; `WorkbenchTheme.Tests` asserts the pair. |
| T3 | `Button.quiet` undefined in dialogs | **fixed** | `App.axaml:194`. |
| T4 | Settings mixes skins | **fixed** | `SettingsWindow.axaml:40,47,76,80` use `wb-field`. |
| T5 | Pane-header heights differ | accepted | 22/35/40 px by surface. |
| T6 | `.muted` 10 vs 11 px | **open (P3)** | `WorkbenchWindow.axaml:14` still forces 10 px in the shell. |
| T7 | Legacy `panel` borders | accepted | Only the maximized search tool remains. |
| T8 | Empty-state icon sizes | accepted | Cosmetic. |
| T9 | Toolbar mixes 30/26 px | **open (P3)** | Monitor/scan toolbars still mix a primary action with compact buttons. |
| T10 | No width breakpoints | **fixed** | `WorkbenchWindow.axaml.cs:2412-2465`; screenshots at 1024/900. |
| T11 | NumericUpDown spinners grey | **fixed** | `App.axaml:553-593`; `dialog-NewProject-light.png` shows themed spinners. |
| T12 | Radius/row-height literals | accepted | Visual polish only. |
| T13 | Tooltip radius/shadow | accepted | Radius now 7; contrast measured earlier at 14.12:1 / 12.20:1. |
| T14 | Doc-tab font 12.5 | **fixed** | Native tabs use 11–12 px. |

### `analysis/ui-consistency/workbench.md`

| ID | Item | Status at HEAD | Evidence |
| --- | --- | --- | --- |
| W1 | Programming tool crash | **fixed** | `demoprogramming-light/dark.png` renders; self-check passes. |
| W2 | Inspector clips primary UI at narrow widths | **fixed** | `WorkbenchWindow.axaml:76-77` wraps representation tabs; `:87-108` keeps the footnote outside the scroll; `axaml.cs:2421-2454` overlays and removes covered tab stops. 900/1024 captures show wrap + horizontal scrollbar; self-check "narrow inspector overlay keeps covered editor stops out of the tab order" PASS. |
| W3 | Tool tabs collide with window controls | **fixed** | `WorkbenchWindow.axaml:116` ScrollViewer/ClipToBounds; `axaml.cs:2468-2481` icon-only below 1100. |
| W4 | Perspective selector overlaps | **fixed** | `axaml.cs:163-171` text-only; all three labels visible at 900. |
| W5 | Rail loses V4 state language | **fixed** | `axaml.cs:136-211,438-479`: active tint + 3 px accent, issue badge, monitor pulse. |
| W6 | Active doc-tab rule on the bottom | **fixed** | `WorkbenchTheme.axaml:239-243` uses `0,2,0,0`. |
| W7 | Shell controls use legacy ETS chrome | **open (P3)** | `WorkbenchWindow.axaml:8-14` still inherits `Ets*` button/field styling for the topbar and object toolbar; coherent in captures. |
| W8 | Dialog chrome split | **fixed** for secondary dialogs | `Classes="wb-dialog"` in Settings/New/Compare/Replace/Report; `ConfirmDialog.cs:47`. Quick dialogs remain plain (see §5). |
| W9 | English leaks German | **open (P2)** | `ProjectCheck.cs` messages, `WorkbenchWindow.Commands.cs:271`, `ProjectCsvExporter.cs:47`, `ProjectReport.cs:71-298`. |
| W10 | Dock toolbar heavy / body lost at 900 | **fixed** | `axaml.cs:2462,2519-2524`; 900 monitor capture shows two-row toolbar and empty state. |
| W11 | Table lacks V4 affordances | **fixed** | `axaml.cs:995-1056`: checkbox column, tri-state header, GA chips, row context menu; self-check covers them. |
| W12 | Help unscrollable message box | **fixed** | `HelpWindow.axaml:28-30` scrolls; self-check opens/closes it; capture shows all six sections. |

### `analysis/ui-consistency/panes.md`

| ID | Item | Status at HEAD | Evidence |
| --- | --- | --- | --- |
| P1 | Search is a full page in a 242 px dock | **fixed** | `axaml.cs:1804-1813` forces it maximized; `SearchPane.axaml` uses the compact V4 layout. |
| P2 | Two parameter UIs | **open (P2, demo-only)** | Real devices use `ParameterPane`; demo devices use the hand-built form (`WorkbenchWindow.axaml.cs:93-108,2295-2390`). Values/validation differ. |
| P3 | Dialog family | **fixed** for secondary dialogs; quick dialogs open (P3) | See W8 and §5. |
| P4 | Label hierarchy | **open (P3)** | Catalog 10 px, Parameter 12 px semibold, Settings 12 px muted. |
| P5 | Action order | **fixed** | Cancel/quiet before primary in Settings, New, Compare, Replace, Report and `ConfirmDialog`. |
| P6 | Destructive confirm | **fixed** | `ConfirmDialog.cs:100-125,176+`: `wb-danger`, safe default, no destructive default. |
| P7 | Catalog compact buttons next to regular fields | **open (P3)** | `CatalogPane.axaml:100-103` 26 px buttons beside 30 px fields. |
| P8 | Settings duplicate "Darstellung" | **fixed** | `SettingsWindow.axaml:30,37`: page title "Options", card heading "Appearance". |
| P9 | Splash tagline English in German UI | **fixed** | `SplashWindow.axaml:14` uses `{loc:Tr SplashTagline}`. |
| P10 | Parameter placeholder colour / watermarks | accepted | Documented. |
| P11 | Report title 16 vs 22 | **fixed** | `ReportWindow.axaml:11` uses `wb-title`. |

### `analysis/workbench-v4-a11y-recheck2.md`

| ID | Item | Status at HEAD | Evidence |
| --- | --- | --- | --- |
| F1 | Inspector overlay covers focusables | **fixed** for visible/focus coverage | `axaml.cs:2431-2454` reflows the editor and sets `TabNavigation=None` on Editor/ObjectTabs/MainColumn; self-check PASS. The old harness geometry check is stale; no visible control sits under the overlay in the 900 captures. |
| F1b | Covered document-tab stops | **fixed** | Same self-check; ObjectTabs included in the suppression. |
| F5 | Table footnote not live / wrong name | **fixed** | `WorkbenchWindow.axaml:106-108` `LiveSetting=Polite` + `TableFootnoteName`; self-check PASS. |
| F6 | Tabs expose Button / incomplete selection | **fixed** (role + status) | `axaml.cs:212-221` native `TabItem`s; selected status at `:714-720` and `:1814-1819`. Harness: peer role `TabItem`, `ItemStatus=Selected`. A full UIA `SelectionItemPattern` is not exposed without a `TabControl`; not verified. |
| F8 | Inspector/dock fields unnamed | **fixed** | `Field` sets `AutomationProperties.Name` (`axaml.cs:1505-1515`); monitor combos `:1975,1999`. |
| F10 | Object filter Ctrl+Z | **fixed in source** | Guarded writes `axaml.cs:550,682`; OS undo not re-run here. |
| F11 | Row names trailing comma | **fixed** | `axaml.cs:1003` uses `RowDescriptionNoLinks`; self-check PASS. |
| F13 | Min size cannot fit 150/200 % | **fixed in source** | `WorkbenchWindow.axaml:4` `MinWidth=900 MinHeight=480`; real 150/200 % still unverified. |
| F14 | Perspective peers expose CLR type | **fixed** | `axaml.cs:170` sets the name. |

### `analysis/workbench-v4-acceptance-final.md` §7

| # | Item | Status at HEAD |
| --- | --- | --- |
| 1 | R05 English completeness | **open (P2)** — see §5 |
| 2 | R07 tab semantics | **fixed** (role/status; pattern caveat above) |
| 3 | R07 narrow inspector | **fixed**; new 900 catalog issue below |
| 4 | R07 active perspective contrast | **fixed** — `Button.active` uses `WbSelectedText` on `WbTint` (5.43:1); `WorkbenchTheme.Tests` asserts the pair |
| 5 | R02 pinned compare | **open (P2 scope gap)** — `Favorites` pin work items, but there is still no pinned GA counterview |
| 6 | R06 two parameter UIs | **open (P2)** |
| 7 | R04 real DPI / screen reader | **unverified** |
| 8 | Residual action/event gaps | docs page missing (accepted), checkbox column fixed, inspector batch action open (P3), E03 GA picker has no text filter (P3), E13 stale scan results on line switch (P3), E18 splitter keyboard (P3) |
| 9 | R08 rebuild at integration SHA | **done locally** — this report |
| 10 | P3 cleanup | partly open: English caption clip, `Cancel review`, unused `DownloadAsync`, stale DCA program cache (`MainWindowViewModel.cs:744-746`) |

## 5. Remaining issues (prioritized)

### P1

None. No crash, no data loss, no failing suite was reproduced at
`5183a0f`.

### P2

1. **English completeness (R05/W9).** In the English UI the project-check
   findings (`src/Knx.Core/ProjectCheck.cs:69,117-191,286…`), the assignments
   CSV header (`WorkbenchWindow.Commands.cs:271`), the topology CSV header
   (`ProjectCsvExporter.cs:47`) and the whole HTML report
   (`ProjectReport.cs:71-298`) remain German; the export file names
   (`projektbericht.html`, `zuordnungen.csv`, `gruppenadressen.csv`,
   `geraete.csv`, `topologie.csv`, `telegramme.csv` at
   `WorkbenchWindow.Commands.cs:30-37,265`) are German too. Decide: translate or
   explicitly scope R05 to shell chrome. CI now runs the self-check in
   de/light and en/dark (`.github/workflows/ci.yml:133-136`) but has no English
   leak assertion; add one.
2. **Catalog product list is unusable at 900×600.** With the navigator open the
   catalog content is ~590 px; the fixed columns `200,*,290`
   (`CatalogPane.axaml:126`) leave ~100 px for the product list, whose inner
   `*,90,45` columns (`:184,196`) leave no room for the product name. The
   selected row shows only the order number (`catalog-900x600-light/dark.png`,
   `DCA-1`/`75010009`). At 1024 the name truncates but remains readable. Fix:
   responsive collapse/overlay of the library or insert column below ~1000 px,
   or a minimum width with a horizontal scrollbar for the product list.
3. **Dialogs open without initial focus.** `HelpWindow` (`HelpWindow.axaml.cs:12-32`),
   `NewProjectWindow` (`NewProjectWindow.axaml.cs:10-12`) and
   `ReplaceProductWindow` (`ReplaceProductWindow.axaml.cs:16-23`) show no
   focused control (`FocusManager.GetFocusedElement()` is null after 500 ms in
   the external harness, light and dark). Keyboard users get no visible focus
   target until they press Tab; the previous message-box help focused its
   default button. `SettingsWindow` and `ConfirmDialog` focus correctly, so the
   fix is to focus the first field/default button on `Opened`.
4. **Two parameter UIs (R06/P2).** Real devices use `ParameterPane`; demo
   devices still use the hand-built form (`WorkbenchWindow.axaml.cs:93-108,2295-2390`)
   with different values and validation. Demo-only today, but it is a
   consistency and maintenance risk.
5. **Pinned compare (R02/E09).** `Favorites` pin a work item, but there is still
   no pinned group-address counterview that survives editor navigation. Either
   implement or record the written "not supported" decision.

### P3

6. **English inspector caption clipped at every width.** `Field` uses a fixed
   104 px caption column with `NoWrap`
   (`WorkbenchWindow.axaml.cs:1508-1509`); `Room reference (opti…` is cut in the
   1440×900 capture. Widen the caption column or allow wrapping.
7. **Quick dialogs keep the legacy window chrome.** `CreateDialog`
   (`WorkbenchWindow.Commands.cs:77`) is used for password, backups, tool
   overview, command palette, new group address and batch links; those windows
   do not carry `wb-dialog` and inherit the default `Ets*` window style.
8. **Shell chrome still mixes ETS and V4** (`WorkbenchWindow.axaml:8-14`,
   W7): topbar/object-toolbar buttons and fields inherit `Ets*` borders while
   primary/quiet buttons and catalog fields are V4.
9. **Test-coverage reduction** (see §3): UI-level New/Recent/Exit flows were
   dropped from `tests/Knx.Shell.Tests`; the successful New path is no longer
   covered end-to-end.
10. **Floating tool windows do not route Ctrl+Z/Ctrl+Y.** The new Docking test
    asserts this as expected; the old MainWindow float handled project undo.
    Confirm the intended contract or add the shortcut to the floating window.
11. **Minor cleanup:** `.muted` 10 px vs 11 px (`WorkbenchWindow.axaml:14`);
    monitor/scan toolbars mix 30/26 px controls; catalog filter row mixes 26 px
    buttons with 30 px fields (`CatalogPane.axaml:100-103`); label hierarchy
    (P4); `Cancel review` hardcoded fallback (`ParameterPane.axaml:63`); unused
    `MainWindowViewModel.DownloadAsync` (`MainWindowViewModel.cs:839`); DCA
    program cache is not refreshed after a catalog re-import until restart
    (`MainWindowViewModel.cs:744-746`).
12. **Other partials:** no docs page (accepted); inspector batch action only via
    the menu (22); GA picker without text filter (E03); scan results not cleared
    on line switch (E13); splitter keyboard operation not accepted (E18).

## 6. Not verified

- **No real OS screen reader / UIA client run.** Automation peers were inspected
  in-process only; Narrator/UIA speech, live-region announcements and the
  selection/expand patterns an external client sees are not proven.
- **No real 150/200 % DPI or mixed-DPI multi-monitor run.** Only DIP layout at
  96 dpi and rendered PNGs; `RenderScaling` stays 1. Source minimum is 900×480,
  which fits 1366×768@150 % and 1920×1080@200 % before the taskbar.
- **High contrast / forced colours, Windows text-size scaling, RTL, IME** were
  not tested.
- **OS keyboard undo** (F10) and the **full tab order at 900×600** were not
  re-measured; only the self-check and source guards.
- **CI parity on ubuntu-latest/windows-latest** was not run; only the local
  Windows Release gate. The workflow read confirms the Windows job runs the
  same commands and the Linux job skips Docking/Shell/Icon with explicit
  notices.
- **Real bus/ETS interop** (download, Secure commissioning, DCA host, USB/RDAC,
  cloud/IoT, multi-project tabs, manufacturer dynamics) remains outside this
  QA, as documented.

## 7. Evidence index

| Evidence | Location |
| --- | --- |
| Clean/restore/build logs | `%TEMP%\qa-rerun\clean.log`, `restore.log`, `build.log` |
| 24 console test logs | `%TEMP%\qa-rerun\tests\tests_*.log`, `src_Knx.App.Tests.log` |
| Native suites, smoke, screenshot self-test, MCP | `%TEMP%\qa-rerun\tests\{docking,shell,parameterui-native,app-smoke,app-screenshot-selftest,mcp-selftest}.log` |
| Workbench self-check ×4 | `%TEMP%\qa-rerun\tests\workbench-{de,en}-{light,dark}.log` |
| Product screenshots (32) | `%TEMP%\qa-rerun\shots\*.png` |
| Harness screenshots (catalog/links/params 900/1024, DCA read-only, table selection, dialogs) | `%TEMP%\qa-rerun\shots\{catalog,links,params}-*, dca-readonly-*, table-selected-*, dialog-*.png` |
| Harness output | `%TEMP%\qa-rerun\shots\harness4-{light,dark}.log` |
| External harness (not committed) | `%TEMP%\opencode\qa-final-harness\` |
