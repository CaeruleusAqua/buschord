# Workbench V4 — independent accessibility, DPI and high-contrast acceptance (final)

> Validation-only report. No product source, test, CI file or existing analysis file was
> changed. The only commit on this branch contains this document.

- Validated product: `fd0729d54899f216c24d4108551c6e33ded752c6` ("Record live language,
  catalogue and bus verification"), branch `agents/qa/a11y-dpi-final`, worktree
  `C:\projects\ai_work\agent-wt\a11y-dpi-final`.
- Current `development` HEAD `ae78907` = `fd0729d` + one docs-only commit
  (`analysis/workbench-v4-completion-plan.md`); `git diff --stat fd0729d..ae78907` shows that
  file only, so the validated product tree is the current HEAD product tree.
- Build: `dotnet build src\Knx.App\Knx.App.csproj -c Release` — **PASS**, 0 errors,
  24 pre-existing warnings (obsolete Avalonia APIs, nullable notes).
- Environment: Windows 11 (10.0.26100), .NET SDK 9, Avalonia 12.1.4, Inter, 96 dpi (100 %),
  single monitor, no real high-DPI display.
- Result: **102 PASS, 4 FAIL, 7 INFO** (113 assertions). The harness's own counter prints
  `FAILURES 3` because the layout-dump path does not increment it; the four `FAIL` lines are
  listed below. On top of the suite:
  - a **real Windows UIA probe** (`UIAutomationClient`, out-of-process) attached to the live
    window: 255 elements, correct roles/names/patterns, real `SetFocus` round-trip;
  - a **contrast matrix**: 50 pairs per theme (100 total), all passing;
  - a **layout matrix**: 900×600 / 1024×700 / 1440×900 light+dark, inspector states, and
    simulated high-DPI work areas 960×540 (1920×1080 @200 %) and 911×512 (1366×768 @150 %).
- Distinct failing defects: **3** (the overlay coverage is reported by two checks; the
  layout clip and the overlay coverage are two facets of the same narrow state, the third is
  the Compare dialog focus).

## 1. Method

The external harness lives in `%TEMP%\opencode\wb-v4-a11y\harness` (not committed). It
references the freshly built Release assemblies (`BusChord.dll` = `Knx.App`,
`Knx.Bus/Core/ProjectCompare/Reports`) and constructs the real `WorkbenchWindow` through the
product's own `App.TestWindowFactory` hook (`src/Knx.App/App.axaml.cs:16`) with
`WorkbenchJson.CreateDemo()`.

Measured through:

- Avalonia routed `KeyDown`/`KeyUp` events on the focused control or window;
- genuine OS `SendInput` for Ctrl+Tab and text undo when the harness window owned the
  foreground (see §10 — foreground was not reached in the final runs);
- automation peers (`ControlAutomationPeer.CreatePeerForElement`) for name, role, item
  status, live setting, focusability;
- a **real UIA client**: Windows PowerShell 5.1 `UIAutomationClient` attached to the live
  window by process id, dumping the element tree, patterns and focus, and calling
  `SetFocus()` on a control (`uia-probe.ps1`, `artifacts/uia-probe.txt`);
- `RenderTargetBitmap` at 1×, 1.5× and 2×;
- visual-tree geometry with **ancestor-clip-aware** rectangles (window bounds and every
  `ClipToBounds`/ScrollViewer viewport) plus `InputHitTest` for overlay coverage;
- a contrast matrix computed from the runtime resource dictionaries of the window
  (`Wb*` tokens from `WorkbenchTheme.axaml`, `Ets*` tokens from `App.axaml`) and from
  rendered controls (foreground/background brushes of realized elements).

Commands:

```powershell
cd C:\projects\ai_work\agent-wt\a11y-dpi-final
dotnet build src\Knx.App\Knx.App.csproj -c Release

$src = "C:\projects\ai_work\agent-wt\a11y-dpi-final\src\Knx.App\bin\Release\net9.0"
$dst = "$env:TEMP\opencode\wb-v4-a11y\harness\lib"
Copy-Item "$src\BusChord.dll","$src\Knx.Bus.dll","$src\Knx.Core.dll","$src\Knx.ProjectCompare.dll","$src\Knx.Reports.dll" $dst -Force

cd $env:TEMP\opencode\wb-v4-a11y\harness
dotnet build -c Release
$env:WB_REPO = "C:\projects\ai_work\agent-wt\a11y-dpi-final"
.\bin\Release\net9.0\win-x64\A11yHarness.exe        # exit 1 because failures are recorded

# real UIA probe (runs while the harness holds the window, WB_HOLD_UIA=150)
powershell -NoProfile -ExecutionPolicy Bypass -File .\uia-probe.ps1
```

The harness was extended for this run (external only): native `TabItem` strips, clip-aware
overlay coverage, the contrast matrix, keyboard paths for menus/help/pinned compare/floating
tools/table checkboxes/language switch/dialog focus, the theme layout matrix, the DPI
work-area layouts, the catalogue-at-900 probe and the UIA hold. One harness-only workaround
was extended: all three workbench sessions (`Monitor`, `Programming`, `LineScan`) get their
`_owner` synchronization context neutralised in the harness process, because the harness
installs its own Avalonia context after the product captured one (`SessionState.VerifyAccess`).
This is a harness process property, not a product change.

## 2. Result matrix

| Area | Result |
|---|---|
| OS foreground / OS input | **INFO** — the extended runs did not own the foreground; OS Ctrl+Tab and OS Ctrl+Z PASS in the baseline run at the same HEAD (see §10) |
| Tab order 1440×900 default | PASS, 38 distinct stops |
| Tab order 1440×900 tools+inspector | PASS, 73 distinct stops; header 6, rail 8, navigator 5, editor/table/toolbar 25, dock 21, inspector 11 |
| Shift+Tab reverses | PASS (returns to `ObjectSearch`) |
| Ctrl+B / Ctrl+J / Ctrl+K / Ctrl+Tab | PASS routed; Ctrl+B from a text box PASS |
| Palette: focus, Down to results, arrows, Enter, Escape, focus restore | PASS (5/5 focus attempts) |
| Text-field undo precedence / shell undo | PASS |
| Navigator Left/Right expand/collapse | PASS (6→5→6→5) |
| Table Left/Right expand/collapse | PASS (collapse 1, expand 0) |
| Focus survives tab switch / representation switch / project open / tab close | PASS (representation switch keeps the focused `TabItem`) |
| Confirm dialog focus + Escape + focus restore | PASS |
| Settings focus + Escape + actions inside | PASS |
| Help (F1) focus + Escape | PASS |
| NewProject focus + Escape | PASS |
| Compare (file compare) initial focus | **FAIL** — focus `<null>` |
| Menu keyboard (open/close) | PASS |
| Pinned compare (Ctrl+Shift+G) | PASS — companion window "0/0/1 · Zentral · Aus · Nebenfenster" opens |
| Floating tool (Space on the float button) | PASS — opens, closing re-docks |
| Table selection (Down), row checkbox (Space), select-all (Space) | PASS (1 row, 1→2, 28/28) |
| Language switch live (Settings, keyboard Save) | PASS — menu `Projekt` → `Project`, restores |
| Accessible names: nav/table rows, doc/view/tool/rail tabs, search fields, inspector fields, dock combos, footnote, status | PASS |
| Tab role/selection | PASS — role `TabItem`, `ItemStatus=Ausgewählt` on the selected tab of each strip |
| Status + footnote live regions | PASS (`Polite`) |
| Target sizes | PASS for help/rail/doc-tab close (35×35, 24×24); **nav chevron 16×30 and table chevron 20×32 < 24×24** (observation, §9) |
| Layout 900×600 / 1024×700 / 1440×900 (light+dark) | PASS, 0 unscrollable clips |
| Layout 900×600 + inspector (light+dark) | PASS, 0 clips, 0 visible focusables under the overlay |
| Layout 1024×700 + inspector (light+dark) | PASS, 0 clips, 0 visible focusables under the overlay |
| Layout 900×600 + inspector + tools | **FAIL** — 1 unscrollable clip (monitor empty state below the window) and 11 visible focusables under the overlay |
| DPI work areas 960×540 / 911×512 | PASS, 0 unscrollable clips |
| Inspector Tab navigation at 900×600 | PASS — 0 editor stops, 11 inspector stops, 0 covered stops; Shift+Tab stays out of the editor |
| Table keyboard scroll | PASS |
| Tooltip contrast light/dark | PASS — 14.12:1 / 12.20:1 |
| Contrast matrix, 50 pairs × 2 themes | PASS (see §3) |
| Catalogue at 900×600 | PASS — product name column 65 px, name visible (`DCA panel`), not the empty "order number only" state of the old base |
| Real Windows UIA | PASS — window + 255 descendants exposed; see §6 |

## 3. Contrast

Computed WCAG 2.x ratios from the runtime resources (window dictionaries), plus measured
pairs from rendered controls. All normal-text pairs use the AA threshold 4.5:1.

### 3.1 `Wb*` token pairs

| Pair (foreground on background) | Light | Dark | Min |
|---|---:|---:|---:|
| WbText on WbBackground | 13.06 | 14.13 | 4.5 |
| WbText on WbPaper | 14.12 | 12.39 | 4.5 |
| WbText on WbPanel | 13.51 | 13.19 | 4.5 |
| WbText on WbHover (hover state) | 12.75 | 10.09 | 4.5 |
| WbMuted on WbBackground | 5.00 | 8.89 | 4.5 |
| WbMuted on WbPaper | 5.40 | 7.80 | 4.5 |
| WbMuted on WbPanel | 5.17 | 8.30 | 4.5 |
| WbMuted on WbRail (rail text) | 4.73 | 8.51 | 4.5 |
| WbMuted on WbStatusbar (status bar) | 4.95 | 8.31 | 4.5 |
| WbMuted on WbTabbar (inactive tabs) | 4.86 | 8.31 | 4.5 |
| WbSubtle on WbPaper (row addresses) | 5.40 | 7.29 | 4.5 |
| WbSelectedText on WbTint (active perspective/rail) | 5.43 | 8.66 | 4.5 |
| WbSelectedText on WbSelected (selected row) | 5.30 | 7.66 | 4.5 |
| WbAccent on WbPaper (accent text) | 4.87 | 8.16 | 4.5 |
| WbAccent on WbTint — graphic only, no text usage | 4.24 | 6.18 | 3.0 |
| WbOnAccent on WbAccent (primary button) | 4.87 | 8.37 | 4.5 |
| WbOnAccent on WbAccentHover (primary hover) | 6.20 | 9.95 | 4.5 |
| WbDisabledText on WbDisabled (disabled) | 4.74 | 5.47 | 4.5 |
| WbError on WbPaper (error) | 5.38 | 7.51 | 4.5 |
| WbError on WbBackground | 4.98 | 8.56 | 4.5 |
| WbWarning on WbPaper | 5.01 | 8.93 | 4.5 |
| WbSuccess on WbPaper | 4.69 | 8.84 | 4.5 |
| WbInfo on WbPaper | 6.45 | 8.17 | 4.5 |
| WbText on WbTableHeader | 14.12 | 11.29 | 4.5 |

`WbAccent` on `WbTint` is the only pair below 4.5:1 (4.24:1 light). It is currently used for
graphics only (`ProgressBar` foreground, border accents); the active perspective/rail button
uses `WbSelectedText` (5.43:1 measured). It is recorded as a latent text pair (§9).

### 3.2 `Ets*` legacy token pairs (quick dialogs, legacy chrome)

| Pair | Light | Dark | Min |
|---|---:|---:|---:|
| EtsText on EtsPanel | 14.12 | 12.20 | 4.5 |
| EtsText on EtsChrome | 12.47 | 13.79 | 4.5 |
| EtsMuted on EtsPanel | 7.57 | 8.04 | 4.5 |
| EtsMuted on EtsChrome | 6.68 | 9.08 | 4.5 |
| EtsMuted on EtsHeaderRow | 6.19 | 6.81 | 4.5 |
| EtsText on EtsButtonHover (legacy hover) | 11.88 | 7.55 | 4.5 |
| EtsOnAccent on EtsAccent | 4.87 | 10.15 | 4.5 |
| EtsDisabledText on EtsDisabledBackground | 4.82 | 4.97 | 4.5 |
| EtsError on EtsPanel | 7.71 | 7.16 | 4.5 |
| EtsWarning on EtsPanel | 6.92 | 8.86 | 4.5 |
| EtsOk on EtsPanel | 7.60 | 8.05 | 4.5 |
| EtsText on EtsStatusBackground | 11.17 | 11.39 | 4.5 |
| EtsMuted on EtsStatusBackground | 5.98 | 7.50 | 4.5 |

### 3.3 Measured live controls (rendered foreground/background)

| Control | Light | Dark |
|---|---:|---:|
| Active perspective button (`Button.active`) | 5.43 | 8.66 |
| Selected navigator row | 12.29 | 9.40 |
| Status bar text (`StatusText` on the status border) | 5.00 | 8.89 |
| Active rail button (`rail-active`) | 5.43 | 8.66 |
| Active document tab / inactive | 14.12 / 4.86 | 12.39 / 8.31 |
| Active view tab / inactive | 4.87 / 5.00 | 8.16 / 8.89 |
| Active tool tab / inactive | 14.12 / 4.86 | 12.39 / 8.31 |
| Primary button (`WbOnAccent` on `WbAccent`) | 4.87 | 8.37 |
| Disabled button (realized) | 4.74 | 5.47 |
| Monitor empty state text | 13.06 | 14.13 |
| Selected table row | not realized in this pass; same `ListBoxItem` style as the navigator row, token pair 5.30 / 7.66 |
| Error text (`wb-error`) | not realized; token pairs 5.38/7.51 |

Dialogs (light, measured): Settings first text 13.06:1, Help 14.12:1, NewProject 13.06:1,
primary button in each 4.87:1. Tooltips: light 14.12:1, dark 12.20:1.

## 4. DPI and layout

| State | Unscrollable clips (light / dark) | Notes |
|---|---|---|
| 900×600 default | 0 / 0 | table viewport 638×289, toolbar collapses its star column to 48 DIP, no child overlap |
| 1024×700 default | 0 / 0 | table viewport 762×403 |
| 1440×900 default | 0 / 0 | table viewport 1116×603 |
| 900×600 + inspector | 0 / 0 | overlay reflows tab strip + editor; 0 visible focusables under it |
| 1024×700 + inspector | 0 / 0 | same |
| 900×600 + inspector + tools | **1** (light) | monitor empty state at (476,606)…(685,621), bottom 621 > 600; table body collapses (`TableHost` 27 px, `ObjectTable` viewport 0) |
| 960×540 = 1920×1080 @200 % DIP | 0 | simulated work area before the taskbar |
| 911×512 = 1366×768 @150 % DIP | 0 | simulated work area before the taskbar |
| Settings 620×560 / 1024×700 | 0 / 0 | Cancel/Save inside; actions ≥24×24 |
| Raster 1.5× / 2× | — | `layout-*-scale150/200.png`; text/borders stay sharp |

Source minimum: `MinWidth=900`, `MinHeight=480` (`src/Knx.App/WorkbenchWindow.axaml:4`).
Both simulated work areas fit the minimum. `TopLevel.RenderScaling` is read-only in
Avalonia 12.1.4 and this session is 96 dpi, so 150/200 % was validated as DIP layout plus
raster captures, **not** as a real high-DPI monitor run.

## 5. Keyboard paths

All paths below were driven through routed key events on the focused control; the four
failures are marked. Genuine OS input (Ctrl+Tab, Ctrl+Z) is covered in §10.

- **Menus**: focus `Projekt` → Down opens the submenu (focus `Neu …`), Down moves to `Öffnen …`, Escape closes and returns focus to the top-level item. PASS.
- **Tabs**: doc/view/tool tabs are native `TabItem`s with names, `ItemStatus=Ausgewählt` for the active tab and `SelectionItem` patterns in real UIA; Enter/Space activates doc/view/tool tabs; Ctrl+Tab cycles work items; focus survives tab switch, representation switch and tab close. PASS.
- **Table**: Down selects a row (selection set 1), Space on a device row selects (1→2), Space on a row checkbox toggles it, Space on the header tri-state from the empty state selects all 28 rows, Left/Right collapse/expand devices, End/PageDown scroll. PASS.
- **Inspector**: 11 stops at 900×600, editor/tab strip suppressed while the overlay is open (0 covered stops), close button reachable, Shift+Tab stays out of the editor. PASS.
- **Dialogs**: Confirm (focus `Schließen`, Escape, focus restore), Settings (theme focused, Escape), Help (F1, `Schließen` focused, Escape), NewProject (`Neues Projekt` focused, Escape) — PASS. **Compare opens with `FocusManager.GetFocusedElement() = null`** — FAIL (`src/Knx.App/CompareWindow.axaml.cs` has no `Opened` focus handoff; the other dialogs set one).
- **Help**: F1 opens `BusChord · Dokumentation`, Escape closes. After close, focus is `<null>` when the invoker was a menu item (observation, §9).
- **Language switch**: Settings → Language `English` → keyboard Save; the running workbench relabels live (first menu `Projekt` → `Project`, focused control reads `Select all objects`) and switches back to German. PASS.
- **Compare**: Ctrl+Shift+G opens the pinned companion for the active device (`Vergleich: 0/0/1 im Nebenfenster`); the companion window opens without initial focus (observation). PASS for the shortcut; focus observation §9.
- **Floating tools**: Space on "Werkzeug auslagern" opens `BusChord · Monitor` (1000×380), closing it re-docks; the floating window opens without initial focus (observation). PASS for the flow.

## 6. Real Windows UIA

A Windows PowerShell 5.1 `UIAutomationClient` probe attached to the live harness window
(`BusChord – Villa Linden`, class `WorkbenchWindow`) while the harness held it open:

- the window and **255 descendants** are exposed; names are the localized UI strings;
- roles and patterns seen: `Menu`/`MenuItem` (`ExpandCollapse`), `Edit` (`NavSearch`,
  `ObjectSearch`, inspector fields), `List`/`ListItem` (`Selection`/`SelectionItem`),
  `TabItem` (`SelectionItem` — doc, view **and** tool strips), `CheckBox` (`Toggle`,
  including the row checkboxes `Objekt auswählen: …` and the tri-state header),
  `ComboBox` (`ExpandCollapse`+`Selection`, `Monitorart`/`Telegrammfilter`),
  `Button` (`Invoke`, e.g. `Werkzeug auslagern`, `Eigenschaften ausblenden`, `Vergleich`),
  `Text` for labels and `Thumb` for splitters;
- automation ids are present where set (`NavSearch`, `ObjectSearch`, `SelectAllBox`,
  `inspector.device.name/address/line/room`, `CatalogRoot`, …);
- a real UIA interaction works: `SetFocus()` on `NavSearch` moved the OS-level focused
  element to that control, and the harness observed the same focused element afterwards
  (`uia-hold-end.txt`).

Honest gaps in the UIA surface: the navigator and table rows do **not** expose an
`ExpandCollapse` pattern; expansion is a separate `Invoke` button
(`Auf-/zuklappen: <row>`). A screen reader can activate it but cannot read an expanded/
collapsed state from the item itself. Narrator speech itself was not run.

## 7. Overlay coverage at 900×600 (clip-aware)

The earlier reports' "N focusables under the overlay" counts were naive geometry. This run
clips every candidate against its clipping ancestors (ScrollViewer viewports, window
bounds) before intersecting with the inspector rectangle, and hit-tests the overlap:

| State | naive geometry | clip-aware visible overlap | pointer hit |
|---|---:|---:|---|
| 900×600 + inspector | 10 | **0** | — |
| 900×600 + inspector + tools | 21 | **11** | 11 × inspector |
| 1024×700 + inspector | 12 | **0** | — |
| 900×600 + inspector (light / dark) | 12 / 12 | 0 / 0 | — |
| 1024×700 + inspector (light / dark) | 14 / 14 | 0 / 0 | — |

The inspector-only reflow (`WorkbenchWindow.axaml.cs:2531-2532` sets a right margin on
`TabStrip` and `Editor`) is correct: tab-strip and table content that "overlaps" in naive
geometry is clipped by its own scroller. The residual defect is the **dock**: `Tools` is not
reflowed, so at 900×600 with the dock open these 11 controls are painted under the overlay
and every pointer hit lands on the inspector:

`ToolsSplitter`; tool tabs `Änderungen`, `Prüfmeldungen`; float/maximize/hide buttons;
`Anzeige leeren`, `Wert`, `Monitorart`, `CSV Filter`, `CSV alle`.

The tab-order suppression is correct (0 covered stops at 900×600 + inspector; `MainColumn`
tab navigation is set to `None` while covered), so the dock is not keyboard-reachable while
the overlay is open — but it is still drawn and hit-blocked. Fix: apply the same reflow to
`Tools` (or hide the dock while the overlay is open). Acceptance: 0 clip-aware covered
focusables.

## 8. Reconciliation of the named reports

Status legend: **fixed** = verified at this HEAD (measured or source), **partial** = reduced
but reproduced, **open** = unchanged, **accepted** = documented decision, **superseded** =
replaced by later work and re-verified here where applicable.

### 8.1 `analysis/ui-consistency/tokens.md` (T1–T14)

| ID | Item | Status | Evidence at this HEAD |
|---|---|---|---|
| T1 | Selected rows miss the selection tokens | **fixed** | Measured selected navigator row paints `WbTint` (`#e4f2f1` / `#233f43`), 12.29:1 / 9.40:1; table/catalog rows use the same `ListBoxItem` style (`WorkbenchWindow.axaml:16`). |
| T2 | Disabled buttons unreadable | **fixed** | Measured disabled button 4.74:1 light / 5.47:1 dark; no `Opacity 0.46` left (`WorkbenchTheme.axaml:320-328`). |
| T3 | `Button.quiet` undefined in dialogs | **fixed** | `App.axaml:194`; Settings Cancel is `wb-button wb-quiet`. |
| T4 | Settings mixes both skins | **fixed** | `SettingsWindow.axaml:40,47` use `wb-field`; measured dialog text 13.06:1. |
| T5 | Pane-header heights differ | accepted | 22/35/40 px by surface; cosmetic. |
| T6 | `.muted` 10 vs 11 px | **open (P3)** | `WorkbenchWindow.axaml:14` still forces 10 px; contrast passes (4.73–5.40:1). |
| T7 | Legacy `panel` borders | superseded/accepted | Only `SearchPane` remains legacy and is forced maximized. |
| T8 | Empty-state icon sizes | accepted | Cosmetic. |
| T9 | Toolbar mixes 30/26 px | **open (P3)** | Monitor toolbar: primary 30 px, neighbours 26 px. |
| T10 | No width breakpoints | **fixed** | Overlay + compact tool tabs + two-row object toolbar; 0 clips at 900/1024 in both themes. |
| T11 | NumericUpDown spinners grey | **fixed (source)** | `App.axaml:553-593`; not re-measured in this run. |
| T12–T13 | Radius/row-height/tooltip literals | accepted | Tooltip contrast measured 14.12/12.20. |
| T14 | Doc-tab font 12.5 | **fixed** | Native tabs 11–12 px. |

### 8.2 `analysis/ui-consistency/workbench.md` (W1–W12)

| ID | Item | Status | Evidence at this HEAD |
|---|---|---|---|
| W1 | Programming tool crash | **fixed** | `ShowTool("programming")` renders; the dock/tool checks and the earlier `demoprogramming` screenshot pass. (A harness-process-only crash on a foreign session context is documented in §10.) |
| W2 | Inspector clips primary UI at narrow widths | **partial (P2)** | Inspector-only states: 0 clip-aware covered focusables at 900/1024, 0 covered tab stops. Residual: with the dock open at 900×600 the 11 dock controls in §7 are painted under the overlay and pointer-hit the inspector; the dock is not reflowed. |
| W3 | Tool tabs collide with window controls | **fixed** | Icon-only below 1100 + `ScrollViewer`/`ClipToBounds`; real UIA shows all six named tool tabs; no overlap in the layout dumps. |
| W4 | Perspective selector overlaps | **fixed** | Text-only buttons; UIA shows three named perspective buttons. |
| W5 | Rail loses V4 state language | **fixed/partial** | `UpdateRail` sets `rail-active` tint + accent bar + item status; measured 5.43:1 / 8.66:1. No separate white surface (accepted simplification). |
| W6 | Active doc-tab rule on the bottom | **fixed** | `WorkbenchTheme.axaml:239-243` `0,2,0,0` (top). |
| W7 | Shell controls use legacy ETS chrome | **open (P3)** | Topbar/object-toolbar inherit `Ets*` defaults; coherent in captures. |
| W8 | Dialog chrome is split | **fixed** | Settings/Help/NewProject/Compare/Replace/Report all use `wb-dialog`; measured dialog contrast passes. |
| W9 | English leaks German | **open (P2)** | Not re-measured here; the leak list (ProjectCheck texts, assignments/topology CSV headers, HTML report, `Cancel review`) stands from `qa-final.md`. |
| W10 | Dock toolbar heavy / body lost at 900 | **partial (P2/P3)** | Toolbar compacts to 26 px, but with the inspector open at 900×600 the table body collapses (`TableHost` 27 px, viewport 0) and the monitor empty state clips (1 layout FAIL). |
| W11 | Table lacks V4 affordances | **fixed** | Checkbox column + tri-state header (UIA `Toggle`), GA chips, row context menu; keyboard toggles verified. |
| W12 | Help is an unscrollable message box | **fixed** | `HelpWindow` with scroller, initial focus, Escape; measured. |

### 8.3 `analysis/ui-consistency/panes.md` (P1–P11)

| ID | Item | Status | Evidence at this HEAD |
|---|---|---|---|
| P1 | Search full page in a 242 px dock | **fixed (superseded)** | Compact V4 page + forced maximized (`WorkbenchWindow.axaml.cs`); not re-measured. |
| P2 | Two parameter UIs | **open (P2)** | Real devices use `ParameterPane`; demo devices use the hand-built form. |
| P3 | Dialog family | **fixed** | See W8. |
| P4 | Label hierarchy | **open (P3)** | Catalog 10 px / Parameter 12 semibold / Settings 12 muted. |
| P5 | Action order | **fixed** | Cancel/quiet before primary (measured in Settings/NewProject/Help; source for the rest). |
| P6 | Destructive confirm | **fixed** | `ConfirmDialog` `wb-danger`, safe default. |
| P7 | Catalog compact buttons beside fields | **open (P3)** | `CatalogPane.axaml:100-103`. |
| P8 | Settings duplicate "Darstellung" | **fixed** | Page "Options", card "Appearance". |
| P9 | Splash tagline English | **fixed (source)** | `SplashWindow.axaml:14` localized. |
| P10 | Placeholder/watermarks | accepted | Documented. |
| P11 | Report title 16 vs 22 | **fixed** | `ReportWindow.axaml:12` uses `wb-title`. |

### 8.4 Accessibility reports

`workbench-v4-a11y-dpi.md` (base `12fc904`, A5–A25) — superseded by the later reports and
re-verified here: Ctrl+Tab, palette Down, field undo, navigator/table expand, focus after
tab switch/representation/close, dialog focus/Escape, live regions, names, tab role/selection
are **fixed and measured**. Still open from that list: icon target sizes (partial, §9),
narrow-inspector overlay (partial, §7), real 150/200 % DPI (not measurable at runtime).

`workbench-v4-a11y-final.md` / `workbench-v4-a11y-recheck2.md` (F1–F14):

| ID | Item | Status at this HEAD |
|---|---|---|
| F1 / F1b | Inspector overlay covers focusables / covered tab stops | **partial** — inspector-only: fixed (0 clip-aware covered, 0 covered stops); with the dock open: 11 covered (new precise measurement, §7). |
| F2 | Navigator Left/Right | **fixed** (measured 6→5→6→5). |
| F3 / F4 | Settings initial focus / Escape | **fixed** (measured). |
| F5 | Table footnote live region | **fixed** (`LiveSetting=Polite`, name `Tabellenhinweis`). |
| F6 | Tab role/selection | **fixed** — native `TabItem`, `ItemStatus` on the selected tab of every strip, real UIA `SelectionItem`. |
| F7 | Tool-tab accessible names | **fixed** (measured: Monitor, Programmierung, …). |
| F8 | Inspector/dock field names | **fixed** — all inspector `TextBox`/`ComboBox` fields named (`inspector-all-fields-named` PASS; the old failure was a stale direct-child lookup). |
| F9 | Help target size | **fixed** (35×35). |
| F10 | Object filter Ctrl+Z | **fixed** — routed PASS; OS Ctrl+Z PASS in the baseline run. |
| F11 | Row-name trailing comma | **fixed** (`table-row-names-clean` PASS). |
| F12 | Table keyboard expand/collapse | **fixed** (measured, without the old context neutralisation for the table itself). |
| F13 | Min size cannot fit 150/200 % | **fixed (source)** — `MinHeight=480`; 960×540 and 911×512 layouts pass; real high-DPI still unverified. |
| F14 | Perspective peers expose the CLR type | **fixed** (measured: three named buttons). |

### 8.5 QA reports

`analysis/qa-final.md` §5 (P2 list) at this HEAD:

1. **English completeness (R05/W9)** — **open (P2)**, not re-measured (source/earlier evidence).
2. **Catalog product list unusable at 900×600** — **fixed** by `26f596f` (responsive columns below 1000 DIP: library 200→150, details 290→220). Measured at 900×600 with the navigator open: `CatalogRoot` columns `[150, 1*, 220]`, product row `[*,90,45]`, product name column 65 px and the name visible (`DCA panel`); screenshot `catalog-900x600.png`. Long names still ellipsize (accepted at 1024 in the QA report).
3. **Dialogs open without initial focus (Help/NewProject/Replace)** — **fixed** (`26f596f`): Help focuses `Schließen`, NewProject focuses `Neues Projekt`, Replace focuses its old-product field; measured for Help/NewProject. **New:** `CompareWindow` still opens with no focus (measured FAIL).
4. **Two parameter UIs (R06)** — **open (P2)**.
5. **Pinned compare (R02)** — **fixed/implemented**: `Vergleich` button + Ctrl+Shift+G open a companion counterview that follows the active group address (`WorkbenchWindow.Commands.cs:654`, `OpenPinnedCompare`); measured. Companion opens without initial focus (observation).

`analysis/workbench-v4-acceptance-final.md` §7 (R-list) at this HEAD:

1. R05 English — **open (P2)**.
2. R07 tab semantics — **fixed** (role/status + real UIA `SelectionItem`).
3. R07 narrow inspector — **partial** (dock residual, §7).
4. R07 active perspective contrast — **fixed**: `Button.active` uses `WbSelectedText` on `WbTint`, measured 5.43:1 light / 8.66:1 dark; `WbAccent` on `WbTint` (4.24) is graphic-only.
5. R02 pinned compare — **fixed/implemented**.
6. R06 two parameter UIs — **open (P2)**.
7. R04 real DPI / screen reader — **improved**: a real Windows UIA client now sees the full tree and can drive focus (§6); Narrator speech and real 150/200 % hardware still not run.
8. Residual action/event gaps — unchanged P3s (docs page accepted, GA picker filter, stale scan results, splitter keyboard).
9. R08 rebuild — done (this report).
10. P3 cleanup (English caption clip, `Cancel review`, unused `DownloadAsync`, DCA program cache) — unchanged.

## 9. Remaining issues, prioritized

**P2**

1. **Dock under the inspector overlay at 900×600** — 11 controls (tool tabs, float/maximize/
   hide, monitor toolbar, combos) are painted under the overlay; every pointer hit lands on
   the inspector. `Tools` does not get the reflow margin that `TabStrip`/`Editor` get
   (`src/Knx.App/WorkbenchWindow.axaml.cs:2531-2532`). Fix: reflow or hide the dock while the
   overlay is open; acceptance: 0 clip-aware covered focusables (WCAG 2.4.11).
2. **900×600 with inspector + tools: the object table collapses and the monitor empty state
   clips** — `TableHost` 27 px / `ObjectTable` viewport 0; empty text at y=606…621 in a 600 px
   window. Fix: minimum body height or a scrollable dock body.
3. **Two parameter UIs (R06)** and **English completeness (R05/W9)** — unchanged from the QA
   reports; not re-measured here.

**P3**

4. **CompareWindow initial focus** — `FocusManager` is `null` after opening; every other
   dialog focuses a control (`CompareWindow.axaml.cs` has no `Opened` handoff).
5. **Companion and floating tool windows open without initial focus** (`OpenCompanion`,
   `OnFloatTool`).
6. **Expand/collapse targets below 24×24** — navigator chevron 16×30, table row chevron
   20×32 (WCAG 2.5.8 target size; help/rail 35×35, doc-tab close 24×24).
7. **Navigator/table expand state is not a UIA `ExpandCollapse` pattern** — only a separate
   `Invoke` button; screen readers cannot read the state from the item.
8. **Help closes with focus `<null>` when invoked from a menu item** (focus restore not
   proven from that context).
9. **Latent contrast**: `WbAccent` on `WbTint` is 4.24:1 light — below AA for text; currently
   graphic-only, but any future accent text on a tint surface fails.
10. Existing P3 list: `.muted` 10 px, 30/26 px toolbar mix, label hierarchy, catalog compact
    buttons, splash, `Cancel review`, unused `DownloadAsync`, DCA program cache, docs page,
    GA picker without text filter, stale scan results, splitter keyboard.

**No P1 was reproduced at this HEAD.**

## 10. Honest limitations and residual risk

- **No screen-reader speech run.** A real UIA client sees and drives the tree (§6), but
  Narrator announcements (focus speech, live regions, selection/expand announcements) were
  not verified. Do not claim screen-reader support beyond "UIA tree and focus interop work".
- **No real 150/200 % or mixed-DPI run.** `RenderScaling` is 1 on this 96 dpi session;
  150/200 % was validated as DIP work areas (960×540, 911×512) plus 1.5×/2× raster captures.
  Window chrome, native menus and caret scaling were not measured on a real high-DPI monitor.
- **Windows high contrast / forced colours were not tested.** The workbench palette is fixed
  hex tokens (`WorkbenchTheme.axaml`), so it would keep its own colours under an OS
  high-contrast theme; Fluent-based chrome may follow system colours. Both app themes were
  contrast-verified instead (§3).
- **OS-level input** (OS Ctrl+Tab, OS Ctrl+Z, OS foreground) passed in the baseline run at
  this HEAD (`artifacts-old-harness-baseline`), but the two extended runs could not claim the
  foreground (Windows foreground lock plus desktop activity); their keyboard results are
  routed events. The UIA `SetFocus` round-trip independently proves OS-level focus interop.
- **Harness-process-only crash (run 2).** A genuine OS mouse click on "Programmieren" while
  the harness had neutralised only the monitor session hit
  `ProgrammingSession.NeedsProgramming` → `SessionState.VerifyAccess` and killed the harness
  process with an unhandled `InvalidOperationException`. The guard is intentional
  (programmer-error check) and the harness now neutralises all three sessions; this is not
  reproducible as a product defect in a normal app context, but it shows the guard is fatal
  rather than reported.
- **Harness counter bug.** The layout-dump path adds `FAIL` result lines without incrementing
  the failure counter, so the final line says `FAILURES 3` for four failing checks.
- **Tab order at 900×600** was counted only for the inspector walk (28 stops), not for the
  full shell; the full 73-stop order was recorded at 1440×900.
- **Tooltip measurement** uses the open `PopupRoot`; popup compositing is out of scope.
- **Not verified:** RTL, IME, Windows text-size scaling, mixed-language input, and the
  deliberate platform limits documented in `workbench-v4-status-matrix.md`.

## 11. Evidence index

All temporary, under `%TEMP%\opencode\wb-v4-a11y\harness\artifacts` (final run) and
`artifacts-extended-run1`, `artifacts-crash-run2`, `artifacts-probe-mixed`:

- `results.txt` — final run, 102 PASS / 4 FAIL / 7 INFO
- `contrast-matrix.txt` — 100 pairs + measured controls + dialog surfaces
- `observations.txt` — focus, keyboard, overlay, DPI, dialog and language observations
- `log.txt`, `progress.txt`
- `uia-probe.txt` — real UIA tree (255 elements), patterns, `SetFocus` round-trip
- `uia-hold-ready.txt` / `uia-hold-end.txt` — the window state the probe attached to
- `layout-*.txt/.png` — 900×600, 1024×700, 1440×900 in light+dark, inspector states,
  `layout-dpi200-960x540`, `layout-dpi150-911x512`, 1.5×/2× rasters
- `inspector-coverage-*.txt` — clip-aware covered controls per state
- `tab-order-default.txt` (38 stops), `tab-order-full.txt` (73 stops),
  `tab-order-900x600-inspector.txt` (28 stops)
- `automation-roles.txt`, `tree-baseline.txt`
- `catalog-probe.txt`, `catalog-900x600.png` — catalogue at 900×600 with a product row
- `settings-*.png`, `theme-light/dark.png`, `focus-ring-*.png`,
  `keyboard-900x600-inspector-tools.png`
- Harness source (temporary, not committed): `Program.cs`, `A11yHarness.csproj`,
  `uia-probe.ps1`

## 12. Repro

```powershell
cd C:\projects\ai_work\agent-wt\a11y-dpi-final
dotnet build src\Knx.App\Knx.App.csproj -c Release

$src = "C:\projects\ai_work\agent-wt\a11y-dpi-final\src\Knx.App\bin\Release\net9.0"
$dst = "$env:TEMP\opencode\wb-v4-a11y\harness\lib"
Copy-Item "$src\BusChord.dll","$src\Knx.Bus.dll","$src\Knx.Core.dll","$src\Knx.ProjectCompare.dll","$src\Knx.Reports.dll" $dst -Force

cd $env:TEMP\opencode\wb-v4-a11y\harness
dotnet build -c Release
$env:WB_REPO = "C:\projects\ai_work\agent-wt\a11y-dpi-final"
.\bin\Release\net9.0\win-x64\A11yHarness.exe          # exit 1: 4 failing checks
Get-Content artifacts\results.txt

# real Windows UIA (second shell, while the harness holds the window):
$env:WB_HOLD_UIA = "150"; .\bin\Release\net9.0\win-x64\A11yHarness.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\uia-probe.ps1
Get-Content artifacts\uia-probe.txt
```

## Addendum — targeted recheck at development HEAD `bb68664` (2026-10-22)

Re-ran only the four failing checks plus the target-size observations against current
`development` HEAD `bb686645330550ff030f67d37630ffff56368529` (includes `587b332` shell polish
and `bb68664` "Give the compare dialog an initial focus target"). Built in a detached
worktree at that commit; harness mode `WB_RECHECK=1`; product unmodified. Artifacts:
`%TEMP%\opencode\wb-v4-a11y\recheck-bb68664`.

| Re-run check | fd0729d | bb68664 | Measurement at bb68664 |
|---|---|---|---|
| `compare-initial-focus` | FAIL (`<null>`) | **still FAIL** | report loaded (1 diff row): `DiffGrid.Focusable=False`, `DiffGrid.IsFocused=False`, `FocusManager=<null>`; `DiffGrid.Focus()` at `CompareWindow.axaml.cs:17` is a no-op. The first row can take focus (`ListBoxItem.Focus()=True`); fix with `DiffGrid.ContainerFromIndex(0)?.Focus()` or a fallback button. |
| `layout-900x600-inspector-tools-unscrollable-clipping` | FAIL (1) | **still FAIL** | same clip: monitor empty state at (476,606)…(685,621) in a 600 px window. |
| `inspector-obscures-focusables-900x600-inspector-tools` | FAIL (11) | **still FAIL** | 11 dock controls under the overlay, all pointer hits on the inspector; `Tools` is still not reflowed (`WorkbenchWindow.axaml.cs:2531-2532` sets only `TabStrip`/`Editor`). |
| `inspector-covered-focus-targets` | FAIL (11) | **still FAIL** | the same 11 controls. |
| Nav chevron target | 16×30 / 4×30 | **still 16×30 / 4×30** | `Auf-/zuklappen: Villa Linden` / `Erdgeschoss` 16×30; leaf `Ohne Einbauort` 4×30. |
| Table row chevron target | 20×32 (see correction) | **still 16×30** | `Auf-/zuklappen: Tastsensor Wohnzimmer` 16×30. |
| Table row checkbox | not separately reported | 20×32 | `Objekt auswählen: Tastsensor Wohnzimmer` 20×32. |
| Doc-tab close / rail / help / inspector close | PASS | PASS | 24×24 / 35×35 / 35×35 / 37×30. |

Correction from the recheck: the earlier "table row chevron 20×32" measurement had picked the
row checkbox (a `ToggleButton`); the actual expand chevron is **16×30**, the checkbox is
20×32. Both remain below 24×24 in width.

**Verdict at bb68664: none of the four failures and none of the target-size observations is
fixed.** The CompareWindow focus fix is present but ineffective because a `ListBox` is not
focusable; shell polish did not change the narrow overlay reflow or the chevron geometry.

### Second recheck — `c333937` (2026-10-22, later)

Re-ran the same three areas against `c333937fcb045ad07b618fb8bb107517f558a4eb` ("Keep the
narrow inspector above the dock and enlarge row chevrons", plus `0226a33` "Focus the compare
report's first row"). Built at that commit; artifacts:
`%TEMP%\opencode\wb-v4-a11y\recheck-c333937`.

| Item | bb68664 | c333937 | Measurement at c333937 |
|---|---|---|---|
| (a) overlay coverage, 900×600 + inspector + tools | 11 covered | **fixed** | clip-aware 0 visible focusables under the overlay (naive geometry 3); the inspector now stops 5 px above the dock (`WorkbenchWindow.axaml.cs:2537-2543`). |
| (a) clipping, same state | 1 clip | **still open (3)** | inspector ends at y=428, dock 433…573 (140 DIP); the monitor empty-state icon + heading + hint overflow the dock/window: bottoms 603 / 625 / 646 vs 600. The dock body still needs a scroller or a content minimum. |
| (b) target sizes | nav 16×30 (leaf 4×30), table 16×30, checkbox 20×32 | **fixed** | nav chevrons 24×24 (including the leaf `Ohne Einbauort`), table row chevron 24×24, row checkbox 24×32; doc-tab close 24×24, rail/help 35×35, inspector close 37×30. |
| (c) compare initial focus | FAIL (`<null>`) | **fixed** | `DiffGrid.Focusable=True`; the first `DiffRow` `ListBoxItem` is focused on open (`FocusManager` in window); Escape closes. |

**Verdict at c333937:** the overlay coverage, all target sizes and the compare focus are
fixed. The 900×600 inspector+tools **clipping is still open**, now with three dock
empty-state controls below the window bottom (the harness counter shows `FAILURES 0` because
the layout path does not increment it; the layout line is the one `FAIL`). Remaining P2 from
this addendum: dock body overflow at 900×600 with the inspector open.

### Third recheck — `9691f24` (2026-10-22, final)

Re-ran the 900×600 inspector+tools state at `9691f24a83e9e1316a575d8c9bdca2b469469bd6`
("Keep short dock empty states scrollable"). Built at that commit; artifacts:
`%TEMP%\opencode\wb-v4-a11y\recheck-9691f24`.

| Check | c333937 | 9691f24 | Measurement |
|---|---|---|---|
| unscrollable clipping, 900×600 + inspector + tools | FAIL (3) | **PASS (0)** | layout dump `clipped-without-scroll=0`; the empty state is now wrapped in a vertical `ScrollViewer` (`EmptyState`, `WorkbenchWindow.axaml.cs:2776`). |
| empty state actually scrollable | — | **no** | the scroller is **638×0 DIP** at (262,573): `Viewport=0`, `Extent=73`. The dock (140 DIP) spends its 105 DIP content area on three wrapped toolbar rows + the 28 DIP table header, so the fill row gets 0 DIP. Programmatic `Offset=73` moves the heading's layout position (y 609 → 536) but it stays invisible in the capture; there is no visible scrollbar or wheel area to reach it. |
| overlay coverage / target sizes / compare focus | fixed | unchanged | 0 visible focusables under the overlay; targets and compare focus still pass. |

**Answer to the targeted question:** the *unscrollable overflow* is gone from the layout
audit, but the monitor empty state does **not** scroll for the user at 900×600 with the
inspector and tools open — its scroller is collapsed to zero height and the state remains
invisible. A usable fix needs vertical room for the dock body (content minimum, a taller or
collapsible dock, or hiding the monitor table header while empty); wrapping alone is not
enough at 140 DIP.



