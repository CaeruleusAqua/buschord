# Workbench V4 — accessibility and DPI recheck 2

> Validation-only report. No product source, dependency or test was changed. The only
> commit on this branch contains this document.

- Base / HEAD: `bd51a8e6b2c8eb09788df4b14a8ffd66377ce65a`
- Branch: `agents/plan2/a11y-recheck`
- Worktree: `C:\projects\ai_work\agent-wt\plan2-a11y-recheck`
- Product build: `dotnet build src/Knx.App/Knx.App.csproj -c Release` — **PASS**,
  0 errors, 25 pre-existing warnings.
- Harness: external, `%TEMP%\opencode\wb-v4-a11y\harness` (not committed).
- Environment: Windows, 96 dpi / 100 % (`GetDpiForWindow` = 96), single monitor,
  Avalonia 12.1.4, net9.0.
- Result: **58 PASS, 13 FAIL, 2 INFO** (73 assertions). The 13 failure rows cover
  **12 distinct checks** because the inspector-overlay defect is reported twice.

This run rechecks the fixes recorded in `analysis/workbench-v4-a11y-final.md` and
the failures that remained there. The short version:

- Fixed and measured: navigator Left/Right, Settings initial focus and Escape,
  status-bar live region, document/tool-tab names, Help target, editor tab
  suppression under the narrow inspector, dark/light tooltip contrast, all
  three window sizes, table keyboard expand/collapse.
- Still open: inspector overlay still covers 23 focusables and six covered
  document-tab stops remain in the tab order; table footnote is not live; tabs
  still expose `Button` instead of `TabItem`; inspector fields and dock combos
  are unnamed; object-filter Ctrl+Z still fails; row names still have a trailing
  comma; source `MinHeight=540` cannot fit 150/200 % work areas.
- Not proven: real OS screen-reader/UIA, real 150/200 % DPI, high contrast.

## Method

The external harness referenced the freshly built Release assemblies
(`BusChord.dll` = `Knx.App`, `Knx.Bus/Core/ProjectCompare/Reports`) and
constructed the real `WorkbenchWindow` through `App.TestWindowFactory`
(`src/Knx.App/App.axaml.cs:16`) with `WorkbenchJson.CreateDemo()`.

Measured through:

- Avalonia routed `KeyDown` events on the focused control or window;
- genuine OS `SendInput` for Ctrl+Tab and text undo when the harness window owned
  the foreground (`GetForegroundWindow` == HWND);
- automation peers (`ControlAutomationPeer.CreatePeerForElement`) for name, role,
  control/content element, focusability, live setting and item status;
- `RenderTargetBitmap` at 1×, 1.5× and 2×;
- visual-tree geometry for clipping, toolbar overlap, inspector coverage and
  covered tab stops.

Harness-only changes for this run (no product file touched):

- Navigator test made deterministic: collapse `Erdgeschoss` (Left), expand
  (Right), collapse again (Left), all on freshly realized rows.
- Added a 900×600 inspector tab walk and a Shift+Tab check from the inspector
  close button.
- Added a source-level min-size audit (`WB_REPO` points at this worktree).
- The monitor session's captured `SynchronizationContext` was set to `null` in
  the harness process only, so `SelectionChanged` could run and F12 became
  measurable. The product's owner guard is unchanged.

## Recheck of the claimed fixes

| Item | Result | Measurement |
|---|---|---|
| Navigator Left/Right | **PASS** | `ExpandedIds` 6 → 5 (Left) → 6 (Right) → 5 (Left); all three events `Handled=true`; realized rows 10 → 7. Tunnel registration at `WorkbenchWindow.axaml.cs:154`, handler `:858-864`. |
| Settings initial focus | **PASS** | Theme `ComboBox` focused inside the modal (`inWindow=true`). `SettingsWindow.axaml.cs:28`. |
| Settings Escape | **PASS** | Escape closes; `SettingsWindow.axaml:85` has `IsCancel="True"`. |
| Status live | **PARTIAL** | `StatusText` has name “Status” and `LiveSetting=Polite` (`WorkbenchWindow.axaml:129`). `TableFootnote` is still `Off`; its attached name is the column header “Objekt / Funktion” (`:99`). |
| Document/tool tab names | **PASS** | Document tabs: “Arbeitsgegenstand öffnen: …” and “… schließen”; tool tabs: Monitor, Programmierung, Scan, Suche, Änderungen, Prüfmeldungen (`WorkbenchWindow.axaml.cs:144`). View tabs get their name from content. |
| Help button target | **PASS** | 35×35 DIP at (28,538), name “Bedienung und Grenzen” (`WorkbenchWindow.axaml:53`). |
| Inspector overlay tab navigation | **PARTIAL** | At 900×600: 0 editor stops, 9 inspector stops, but **6 covered document-tab stops** remain (see F1 below). Shift+Tab from the inspector close goes to the inspector combo, not into the editor. |
| Tooltip contrast light/dark | **PASS** | Light `#202C3C` on `#FFFFFF` = 14.12:1; dark `#E5EBF3` on `#25292E` = 12.20:1. Measured in the open `PopupRoot`. |
| Screenshot / DPI layout | **PASS / source FAIL** | 0 unscrollable clips at 900×600, 1024×700 and 1440×900, with inspector and inspector+tools; 1.5×/2× captures are sharp. Runtime 150/200 % is unavailable; source `MinHeight=540` fails both target work areas (F13). |

## Remaining failures

| ID | Check | Result | Measurement |
|---|---|---|---|
| F1 | Inspector overlay at 900×600 | **FAIL** | 23 focusable controls intersect the 350 DIP overlay at x=550…900; both coverage checks fail. |
| F1b | Overlay tab navigation | **FAIL** | Editor is skipped (0 stops), but six covered document-tab stops (document open/close buttons and the add-tab button) are still reached. |
| F5 | Table footnote live region | **FAIL** | `TableFootnote` `LiveSetting=Off`, name “Objekt / Funktion”. Status bar itself is now live. |
| F6 | Tab role / selection | **FAIL** | Document, view and tool tabs expose role `Button`; only the active document tab has `ItemStatus="Ausgewählt"`, all view/tool statuses are empty. |
| F8 | Inspector and dock field names | **FAIL** | Inspector device name, address and line/room combos have empty peer names; both monitor combos are unnamed. Navigator/object search, navigator list and object table now pass. |
| F10 | Object filter Ctrl+Z | **FAIL** | Genuine OS input: typed `abc`, `CanUndo=False`, Ctrl+Z leaves `abc`; project correctly unchanged. |
| F11 | Row name noise | **FAIL** | Channel rows: `A ▸ Deckenlicht, `, `B ▸ Stehleuchte, `; unlinked object rows also end with `, ` (`Relativ dimmen … Flags CT, `). |
| F12 | Table keyboard expand/collapse | **PASS** (harness) | Left collapses (`CollapsedDeviceIds` = 1), Right expands (0), after the harness neutralized the session-owner guard. Source registration `WorkbenchWindow.axaml.cs:155`, handler `:866-874`. Still not a real-app run. |
| F13 | 150/200 % DPI | **FAIL** (source) | `MinWidth=900`, `MinHeight=540` (`WorkbenchWindow.axaml:4`). 1920×1080 at 200 % = 960×<540 DIP; 1366×768 at 150 % = 911×<512 DIP before the taskbar. Runtime `RenderScaling=1`; no public setter. |
| F14 | Perspective names (new, low) | **FAIL** | The three perspective buttons replace their content with `IconLabel` without setting a name; peers are `Avalonia.Controls.StackPanel` (`WorkbenchWindow.axaml.cs:105-107`). |

## Findings and exact fixes

### F1 (High) — inspector overlay still covers focusables and six tab stops

At 900×600 the inspector occupies x=550…900 (350 DIP, 524 DIP high). The editor
is removed from tab navigation, but the document-tab strip above it is not:
`KeyboardNavigation.SetTabNavigation` is applied only to `Editor`
(`WorkbenchWindow.axaml.cs:1854-1855`), while `ObjectTabs` lives in
`MainColumn` row 0 (`WorkbenchWindow.axaml:66`), outside `Editor`. Covered
controls still include three document tabs and their close buttons, the
watch/add-device/properties actions, both toolbar checkboxes, `ObjectSearch`,
the expand-all button, representation tabs and the table rows.

Source: overlay branch `WorkbenchWindow.axaml.cs:1832-1840`; tab suppression
`:1854-1855`.

Fix (smallest): while `narrow && Workspace.InspectorVisible`, also set
`KeyboardNavigation.SetTabNavigation(MainColumn, KeyboardNavigationMode.None)`
(and restore `Continue` in the else branch), or apply the same setting to
`ObjectTabs`; additionally set `IsHitTestVisible=false` on `Editor` and
`ObjectTabs` while the overlay is open, because pointer hit testing is still
live. Acceptance: no focusable element intersects the inspector rectangle and
no Tab/Shift+Tab stop lands under it (WCAG 2.4.11). The structural alternative
remains reserving a real column instead of overlaying.

### F5 (Medium) — table footnote is not live and has the wrong name

Source: `src/Knx.App/WorkbenchWindow.axaml:99`. The attached name is currently
`{loc:Wb ColumnObjectFunction}` (the column header), not a footnote label.

Fix: add `AutomationProperties.LiveSetting="Polite"` and
`AutomationProperties.Name="Tabellenhinweis"` (or a localized key) to
`TableFootnote`. The static “Lokal · keine Busverbindung” label may stay
`Off`; it does not change. Announce only errors assertively if every status
change would be too noisy.

### F6 (Medium) — tabs still expose Button and incomplete selection state

Document tabs are built as `ActionButton` at `WorkbenchWindow.axaml.cs:440-443`;
view tabs at `:129`; tool tabs at `:136`. `AutomationProperties.ItemStatus` is
set only for document tabs at `:443`; view and tool tabs have no selected state.
The peer role remains `Button`; Avalonia 12.1.4 still offers no public
SelectionItem pattern through a plain `ButtonAutomationPeer`.

Fix (complete): replace the three strips with `TabControl`/`TabItem`, which
emits `TabItemAutomationPeer` and selection information. Short-term: keep the
buttons, override `GetAutomationControlType()` to `TabItem` in a custom
`ButtonAutomationPeer`, and set `AutomationProperties.ItemStatus` on the active
view and tool tabs as well. This fixes role/status but not a full UIA
SelectionItemPattern.

### F8 (Medium) — inspector fields and monitor combos are unnamed

Measured empty peer names:

| Control | Source |
|---|---|
| inspector device name | `src/Knx.App/WorkbenchWindow.axaml.cs:1043` |
| inspector address | `:1044` |
| inspector line combo | `:1049` |
| inspector room combo | `:1053` |
| monitor mode combo | `:1388` |
| monitor filter combo | `:1411` |

Fix: call `AutomationProperties.SetName(...)` immediately after construction,
e.g. “Gerätename”, “Adresse”, “Linie”, “Raum”, “Monitorart”,
“Telegrammfilter”. The visible `Note(...)` labels are not associated with the
fields. `NavSearch`, `ObjectSearch`, `NavTree` and `ObjectTable` already pass
(`WorkbenchWindow.axaml:59,60,84,98`).

### F10 (Low) — object filter Ctrl+Z still does nothing

Source: `WorkbenchWindow.axaml.cs:513` unconditionally assigns
`ObjectSearch.Text = tab.Filter` during `RefreshEditor`, which resets
Avalonia's undo stack; `NavSearch` is written the same way at `:393`.

Fix: guard both writes, e.g.
`if (ObjectSearch.Text != tab.Filter) ObjectSearch.Text = tab.Filter;` and the
equivalent for `NavSearch`. Verify with genuine OS Ctrl+Z that the text
restores and the project remains unchanged.

### F11 (Low) — row names keep a trailing comma

Channel/device rows always append `, {row.Address}` at
`WorkbenchWindow.axaml.cs:813-815`, even when the address is empty. Object rows
use `WorkbenchStrings.cs:116`:
`"{0}, Nummer {1}, DPT {2}, {3}, Flags {4}, {5}"`, which always emits the final
`, {5}` even when links are empty.

Fix: omit the separator when `row.Address` is empty, and build the object
description conditionally or split `RowDescription` into a version without
links. Acceptance: no peer name ends in `,` or `, `.

### F13 (Medium, source-level) — minimum window size cannot fit 150/200 % work areas

Source: `src/Knx.App/WorkbenchWindow.axaml:4` (`MinWidth="900" MinHeight="540"`).

At 200 % on 1920×1080 the DIP screen is 960×540 before the taskbar; at 150 %
on 1366×768 it is about 911×512. The minimum height of 540 cannot fit either
work area, so the window can be forced off-screen or clipped. This is a source
audit, not a real high-DPI run.

Fix: lower `MinHeight` to about 480 (and keep the vertical regions scrollable
or collapsible), or derive the minimum from the current screen work area. Then
verify on real 150/200 % hardware.

### F14 (Low, new) — perspective buttons expose the CLR type name

`WorkbenchWindow.axaml.cs:105-107` replaces the content of each perspective
button with `IconLabel(glyph, title)` without setting an accessible name, so
all three peers report `Avalonia.Controls.StackPanel`.

Fix: add `AutomationProperties.SetName(perspectiveButton, title);` after the
content replacement, as already done for tool tabs at `:144`.

## Layout / DPI measurements

| Window | Inspector/tools | Unscrollable clips | Scroll-viewer clips | Table viewport | Notes |
|---|---|---|---|---|---|
| 900×600 | no / no | 0 | 91 | 638×303 | toolbar star column collapses to 48 DIP between x=463 and x=511 |
| 1024×700 | no / no | 0 | 2 | 762×403 | expected virtualized rows |
| 1440×900 | no / no | 0 | 2 | 1116×603 | expected virtualized rows |
| 900×600 | yes / no | 0 | 97 | 638×303 | inspector 350×524 at x=550; 23 covered focusables |
| 900×600 | yes / yes | 0 | 97 | 638×158 | tools visible; same overlay coverage |
| Settings 620×560 | — | 0 | 2 | — | Cancel/Save inside at (432,518)/(520,518) |
| Settings 1024×700 | — | 0 | 0 | — | Cancel/Save inside at (836,658)/(924,658) |

DPI: the session is 96 dpi / 1×. `RenderScaling` is read-only in Avalonia
12.1.4. The 1.5×/2× images are raster captures of a 1× window
(`layout-1440x900-scale150.png` 2160×1350, `layout-1440x900-scale200.png`
2880×1800, `layout-900x600-scale200.png` 1800×1200). Text, borders and icons
stay sharp; no new clipping. **This is not a real 150/200 % monitor run.**

## Other measured passes

- Tab order at 1440×900, tools closed: 36 distinct stops; tools + inspector
  open: 71 distinct stops (header 7, rail 8, navigator 5, editor 24, dock 21,
  inspector 9). 900×600 with inspector: 35 stops, 0 in the editor, 9 in the
  inspector.
- Shift+Tab from `ObjectSearch` goes to the “Ohne GA” checkbox and Tab returns
  to `ObjectSearch`.
- Ctrl+B toggles the navigator, including from a focused text box; Ctrl+J
  toggles/hides the tools; Ctrl+K opens the palette; Ctrl+Tab cycles tabs
  routed and through genuine OS input.
- Palette: query focused 5/5 runs; Down moves focus to the result list; Up/Down
  change selection; Enter activates “Projektprüfung”; Escape closes and
  restores `ObjectSearch` focus.
- Dialogs: ConfirmDialog focuses “Schließen”, Escape closes and returns focus
  to `ObjectSearch`.
- Focus survives tab switch, representation switch, project open and tab close.
- Text-field undo takes precedence over shell undo; shell undo outside a field
  replaces the project.
- Settings 620×560 and 1024×700: 0 unscrollable clips; all visible buttons at
  least 24×24 DIP and inside the window.
- Table keyboard scroll: End/PageDown reached offset 796 of extent 896,
  viewport 100.
- Inspector close button is reachable; target 37×30 DIP.
- Focus ring: 2 px accent ring on the `ContentPresenter` for primary and quiet
  buttons.
- Rail icon targets 35×35 DIP; Help 35×35 DIP.
- Tooltip contrast: light 14.12:1, dark 12.20:1.

## Evidence index

All temporary, under `%TEMP%\opencode\wb-v4-a11y\harness\artifacts`:

- `results.txt` — 58 PASS / 13 FAIL / 2 INFO
- `observations.txt` — focus, OS input, tooltip colours/ratios, navigator/table
  keyboard, inspector tab walk, source min-size
- `log.txt` — environment, DPI, automation API, screenshots, sync context
- `progress.txt` — per-step and per-screenshot timing
- `tab-order-default.txt` (36 stops), `tab-order-full.txt` (71 stops),
  `tab-order-900x600-inspector.txt` (35 stops; 6 covered)
- `automation-roles.txt` — names, roles, live settings, item status, bounds
- `tree-baseline.txt` — automation-relevant visual/peer tree
- `inspector-coverage-900x600-inspector.txt` — 23 covered focusables
- `layout-{900x600,1024x700,1440x900}.txt`,
  `layout-900x600-inspector*.txt`, `layout-settings-{620x560,1024x700}.txt`
- `baseline-1440x900.png`, `layout-*.png`, `settings-*.png`,
  `theme-light.png`, `theme-dark.png`, `focus-ring-*.png`,
  `tab-order-full-focus-start.png`, `keyboard-900x600-inspector-tools.png`

Harness source (temporary, not committed): `Program.cs` and
`A11yHarness.csproj` in the same directory. It references the product Release
assemblies and the product's NuGet versions (Avalonia 12.1.4, Dock 12.1.0.12,
CommunityToolkit.Mvvm 8.4.0).

## Repro

```powershell
cd C:\projects\ai_work\agent-wt\plan2-a11y-recheck
dotnet build src\Knx.App\Knx.App.csproj -c Release

$src = "C:\projects\ai_work\agent-wt\plan2-a11y-recheck\src\Knx.App\bin\Release\net9.0"
$dst = "$env:TEMP\opencode\wb-v4-a11y\harness\lib"
Copy-Item "$src\BusChord.dll","$src\Knx.Bus.dll","$src\Knx.Core.dll","$src\Knx.ProjectCompare.dll","$src\Knx.Reports.dll" $dst -Force

cd $env:TEMP\opencode\wb-v4-a11y\harness
dotnet build -c Release
$env:WB_REPO = "C:\projects\ai_work\agent-wt\plan2-a11y-recheck"
.\bin\Release\net9.0\win-x64\A11yHarness.exe   # exit 1 because failures are recorded
Get-Content artifacts\results.txt
```

## Unverified / residual areas

- **No OS screen-reader run.** Automation peers were inspected in-process, not
  through Narrator/UIA. Focus announcements, live-region speech, selection and
  expand patterns, and the tree an external UIA client sees are not proven. Do
  not claim screen-reader support from this report.
- **No real 150/200 % DPI or mixed-DPI multi-monitor run.** Only DIP layout and
  1.5×/2× raster captures; window chrome, native menus and caret scaling were
  not measured.
- **High contrast / forced colours, Windows text-size scaling, RTL, IME and
  mixed-language input were not tested.**
- **Tooltip measurement uses the open `PopupRoot`**, not a screenshot of the
  popup; popup compositing and system-drawn chrome are out of scope.
- **F12 required a harness-only session-context neutralization.** The source
  handler is tunnel-registered, but this is not a real-app keyboard run.
- **Tab order at 900×600 was counted only for the inspector walk**, not for the
  full shell; the full 71-stop order was recorded at 1440×900.
- **OS foreground acquisition is non-deterministic** under the Windows
  foreground lock. OS Ctrl+Tab and OS text undo ran when foreground was
  confirmed.
