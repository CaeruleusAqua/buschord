# UI consistency review: panes and secondary windows

Base `bd51a8e6b2c8eb09788df4b14a8ffd66377ce65a`; branch `agents/ui-consistency/panes`; worktree `C:\projects\ai_work\agent-wt\ui-consistency-panes`. Read-only: this report is the only change. Reference: `knx-workbench-v4-bundle` (`docs/KONZEPT.md`, `src/shell.html`, `src/concept.html`).

Build: `dotnet build src\Knx.App\Knx.App.csproj -c Release` — PASS, 0 errors, 25 warnings. Captures: 30 PNGs, 1440 and 1024, light/dark (links 1440 only), plus `settings`/`new-project` at native size. There is no screenshot screen for search, compare, replace or report, so those are source-only. The Workbench review's inspector/dock findings are not re-ranked; parameter surfaces are covered below only as a pane-level split.

## Verdict

`CatalogPane` is the strongest V4 surface; `ParameterPane` is close but bypassed for demo devices; `SettingsWindow` is visually V4. `SearchPane` and every modal except Settings still use ETS/Fluent. Root cause: V4 is opt-in via `wb-*` classes, while `App.axaml:165-170` still installs Segoe UI/EtsChrome and legacy 10,5/4 px fields (`App.axaml:437-475`) for windows that do not override them. Four panes are legacy-only and not reachable from the V4 workbench; do not migrate them until re-hosted.

## Matrix

| Surface | State | P | Evidence (file:line) | Minimal fix |
|---|---|---|---|---|
| MainWindow + dashboard, group addresses, check, monitor/recorder | ETS compatibility shell, not default (`App.axaml.cs:84-90`) | — | `MainWindow.axaml:17,273-337`; `MainWindow.axaml.cs:66-68` | Keep ETS; not a V4 reference. |
| CatalogPane | V4 | P3 | `Views/CatalogPane.axaml:100-103` 26 px buttons beside 30 px fields; labels 10 px `:16-21` | Regular buttons or centre compact ones; shared label style. |
| ParameterPane | V4 partial | P2 | `Views/ParameterPane.axaml:5` no `wb-root`; `:68` labels inherit 12 px semibold; `Views/ParameterTemplateSelector.cs:45-79` no `wb-field` | Add `wb-root`, `wb-field`, one label style. |
| Demo parameter editor | Hand-built V4 | P2 | `WorkbenchWindow.axaml.cs:1727-1751` builds fields; `:1799-1802` action row in flow | Reuse `ParameterPane` for demo values. |
| SearchPane | Legacy live in V4 | **P1** | `Views/SearchPane.axaml:9-102`; `WorkbenchWindow.axaml.cs:1250,1328-1332`; 242 px dock `WorkbenchTheme.axaml:33` | Document tab, or one-panel compact tool; replace `panel/panelHeader/columnHeader`. |
| Building / GroupLinks / GroupMonitor / BusMonitor | ETS, unused or MainWindow-only | — | `BuildingPane.axaml:40-44`; `GroupLinksPane.axaml:16-18`; `GroupMonitorPane.axaml:33-42`; `BusMonitorPane.axaml:8-16`; host `MainWindow.axaml.cs:68` | No action; legacy. |
| SettingsWindow | V4, small gaps | P3 | `SettingsWindow.axaml:31,38` duplicate “Darstellung”; `:58-62` empty state as list row; `:85-86` dead `.quiet`, legacy `.primary` | `wb-button wb-quiet`/`wb-primary`; remove duplicate; proper empty state. |
| NewProjectWindow | ETS live | P2 | `NewProjectWindow.axaml:5-7,10-11`; launched `WorkbenchWindow.Commands.cs:161` | Shared V4 dialog chrome, padding, order. |
| CompareWindow | ETS live | P2 | `CompareWindow.axaml:17-24`; launched `Commands.cs:359` | V4 panel/table classes. |
| ReplaceProductWindow | ETS live | P2 | `ReplaceProductWindow.axaml:10,32-37`; launched `Commands.cs:370` | V4 chrome/table; localize German sentence. |
| ReportWindow | ETS live | P2 | `ReportWindow.axaml:9,20-26` | 22 px title; V4 table/empty state. |
| SplashWindow | ETS-brand | P3 | `SplashWindow.axaml:7,11-12` | `WbBackground`/`WbMuted`; localize tagline. |
| ConfirmDialog | ETS/Fluent | P2 | `ConfirmDialog.cs:40,56,116-119` | V4 chrome; `wb-danger` Delete. |
| Inline Relations | Hand-built V4 | P2 | `WorkbenchWindow.axaml.cs:1288-1308` | Clear actions; danger Remove. |
| Inline Monitor / Programming / Problems / Scan / Changes | V4 | cross-ref | `WorkbenchWindow.axaml.cs:1244-1252` | Covered by Workbench review. |

## P1

**Search is a full page forced into a 242 px tool.** It stacks filter, 3:2 results/replace and a second list inside the dock. At 1024 the dock already has roughly one object row (Workbench P1-2), so replace becomes a ~60 px scroll box; at 1440 it still reads as ETS because of `panel`/`panelHeader`/`columnHeader` and 22 px rows. Source-only. Fix: document tab, or compact V4 tool with only results and an expandable replace section.

## P2

- **Two parameter UIs.** `ParameterPane` declares a bottom footer (`ParameterPane.axaml:8-33`), but demo devices hide it (`WorkbenchWindow.axaml.cs:1719`) and use a hand-built form with content-flow actions and combos that hug text. Unify.
- **Dialog family.** `NewProject`, `Compare`, `Replace`, `Report` and `ConfirmDialog` inherit `Segoe UI 12.5`/`EtsChrome` from `App.axaml:165-170`, then open over the V4 shell. Add one shared dialog style/base, then map `panel`/`columnHeader` to V4 classes.
- **Label hierarchy.** Catalog captions are 10 px muted (`CatalogPane.axaml:16-21,234-259`), Parameter labels 12 px semibold (`ParameterPane.axaml:68`), Settings labels 12 px muted (`SettingsWindow.axaml:22-25`). Define `TextBlock.wb-label` and use it in every form.
- **Action order.** Settings is Cancel/Save (`SettingsWindow.axaml:85-86`); NewProject is Create/Cancel (`NewProjectWindow.axaml:10-11`); ConfirmDialog is Save/Don't Save/Cancel (`ConfirmDialog.cs:91-95`). Pick one order (recommend primary-right) and apply it.
- **Destructive confirm.** `AskDeleteAsync` gives Delete no danger styling and makes Cancel primary (`ConfirmDialog.cs:116-119`). Add `wb-danger`; keep Delete non-default.

## P3

- Catalog filter row mixes 26 px buttons with 30 px fields (`CatalogPane.axaml:100-103`); use regular height or vertical centring.
- Settings repeats “Darstellung” as subtitle and card heading (`SettingsWindow.axaml:31,38`).
- Splash tagline is English in the German UI (`SplashWindow.axaml:11-12`).
- Parameter color is a known placeholder text box and pictures print gray `[Bild: …]` (`ParameterTemplateSelector.cs:85-91`); date/time watermarks are German-only (`:20-21`).
- Report title is 16 px vs the V4 22 px scale (`ReportWindow.axaml:9`).

## Intentionally legacy

`MainWindow` and its dashboard are the ETS compatibility window, not the product start (`App.axaml.cs:84-90`). `BuildingPane`, `GroupLinksPane`, `GroupMonitorPane` and `BusMonitorPane` are ETS-styled and either unused or only reachable from `MainWindow`; that inconsistency is intentional. Migrate only if they are re-hosted in the workbench.
