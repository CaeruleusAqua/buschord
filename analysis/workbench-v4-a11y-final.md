# Workbench V4 – final accessibility and DPI validation

> **Post-review fixes:** navigator Left/Right now uses a tunnel handler, Settings focuses
> the theme field and closes with Escape, status text is live and named, tabs/tool tabs
> carry explicit names and selected status, the Help button is 35×35, and the inspector
> overlay disables tab navigation in the covered editor. Remaining: real UIA/Narrator,
> real 150/200% DPI, full tab-control semantics, filter Ctrl+Z and the high-contrast pass.

> Validation-only report. No product source was changed. The only commit on this
> branch contains this document.

- Base commit: `569f18c0bbd526a3d888b7229c71ddc556e20c4a`
- Branch: `agents/plan/a11y-dpi`
- Worktree: `C:\projects\ai_work\agent-wt\plan-a11y-dpi`
- Product build: `dotnet build src/Knx.App/Knx.App.csproj -c Release` — **PASS**,
  0 errors, 23 pre-existing warnings (obsolete Avalonia APIs, nullable notes).
- Environment: Windows, 96 dpi / 100 % (`GetDpiForWindow` and
  `GetDpiForSystem` = 96), single monitor, Avalonia 12.1.4, net9.0.
- Result: **45 PASS, 13 FAIL, 2 INFO** (60 assertions; see
  `%TEMP%\opencode\wb-v4-a11y\harness\artifacts\results.txt`).

This run revalidates the old `agents/workbench-v4/a11y-dpi` report (base
`12fc904`). Old high findings that are now fixed: Ctrl+Tab, palette Down, focus
restore after tab/representation/close, ConfirmDialog focus and Escape, and
dark/light tooltip contrast. Navigator/table key handlers were added, but the
navigator path still does not change state (F2) and the table path is
unverified (F12). Remaining failures are listed below with exact locations.

## Method

External harness in `%TEMP%\opencode\wb-v4-a11y\harness` (not committed). It
referenced the freshly built Release assemblies (`BusChord.dll` = `Knx.App`,
`Knx.Bus/Core/ProjectCompare/Reports`) and constructed the real
`WorkbenchWindow` through the existing `App.TestWindowFactory` hook
(`src/Knx.App/App.axaml.cs:16`) with `WorkbenchJson.CreateDemo()`.

Measured through:

- Avalonia routed `KeyDown` events on the focused control or window, the same
  route platform keys use after the window layer;
- genuine OS `SendInput` for Ctrl+Tab and text undo when the harness window owned
  the foreground (`GetForegroundWindow` == HWND);
- automation peers (`ControlAutomationPeer.CreatePeerForElement`) for name, role,
  control/content element, keyboard focusability and live setting;
- `RenderTargetBitmap` at 1×, 1.5× and 2×;
- visual-tree geometry for clipping, toolbar overlap and inspector coverage.

The two extra harness-only checks (settings dialog, tooltip contrast) use the
product's own `SettingsWindow`, `AppSettings` and theme resources. No product
assembly or file was modified.

## Pass/fail summary

### Passed (measured)

| Area | Result |
|---|---|
| Window activation / OS foreground | PASS (this run; foreground lock can reject it) |
| Tab order at 1440×900, tools closed | PASS, 36 distinct stops |
| Tab order at 1440×900, tools + inspector open | PASS, 66 distinct stops; header 7, rail 8, navigator 5, editor 22, dock 18, inspector 9 |
| Ctrl+B / Ctrl+J / Ctrl+K | PASS, including Ctrl+B from a focused text box; Ctrl+J hides the dock |
| Ctrl+Tab | PASS routed and genuine OS: active tab changed Wohnzimmer → Villa Linden |
| Palette open focus | PASS 5/5 runs, query focused |
| Palette Down / Tab / Up / Down | PASS: Down moves focus to the named result list; arrows change selection |
| Palette Enter | PASS: activates “Projektprüfung” |
| Palette Escape | PASS: closes and restores `ObjectSearch` focus |
| Palette accessible names | PASS: query “Arbeitsgegenstand oder Befehl suchen”, list “Suchergebnisse” |
| Text-field undo precedence | PASS: Ctrl+Z in `ObjectSearch` is handled by the field, project unchanged; shell undo outside fields replaces the project |
| Focus after tab switch | PASS: focus moves to the new tab button |
| Focus after representation switch | PASS: focus restored to the active tab button |
| Focus after project open/replace | PASS: `ObjectSearch` keeps focus |
| Focus after tab close | PASS: focus moves to the next tab button |
| ConfirmDialog initial focus / Escape / owner focus | PASS: “Schließen” focused; Escape closes; `ObjectSearch` regains focus |
| Navigator/table row accessible names | PASS (with noise, see F11) |
| Document, representation and rail names | PASS; tool-tab names fail (F7) |
| 900×600, 1024×700, 1440×900 layout | PASS: 0 unscrollable clips at all three sizes |
| 900×600 with inspector and/or tools | PASS for clipping/scroll: 0 unscrollable clips; table/dock have working scrollers |
| Table keyboard scroll | PASS: End/PageDown reached offset 796 of extent 896, viewport 100 |
| Inspector close button reachable | PASS |
| Toolbar overlap | PASS: no child overlap at 900×600; star column collapses between x=463 and x=511 |
| Settings 620×560 | PASS layout: 0 unscrollable clips; Cancel/Save inside the window at (432,518) and (520,518) |
| Settings 1024×700 | PASS layout: 0 unscrollable clips; Cancel/Save inside at (836,658)/(924,658) |
| Tooltip light | PASS: text `#202C3C` on `#FFFFFF`, 14.12:1 |
| Tooltip dark | PASS: text `#E5EBF3` on `#25292E`, 12.20:1 (measured in the open `PopupRoot`) |
| Focus ring | PASS: 2 px accent ring on the `ContentPresenter` for primary and quiet buttons |
| Rail icon targets | PASS: 35×35 DIP |
| Settings action targets | PASS: all visible buttons at least 24×24 DIP |

### Failed / not measured

| ID | Check | Result | Measurement |
|---|---|---|---|
| F1 | Inspector overlay at 900×600 | **FAIL** | 21 focusable controls intersect the inspector overlay; both coverage checks fail (`inspector-coverage-900x600-inspector.txt`) |
| F2 | Navigator expand/collapse by keyboard | **FAIL** | selected `NavEntry` = Erdgeschoss, `Right` reports `Handled=true`, `ExpandedIds` 6→6; `Left` 6→6 |
| F3 | Settings dialog initial focus | **FAIL** | `FocusManager.GetFocusedElement()` is `null` after open |
| F4 | Settings dialog Escape | **FAIL** | Escape leaves the dialog open |
| F5 | Status live regions | **FAIL** | status message and footnote `LiveSetting=Off` |
| F6 | Tab role / selection | **FAIL** | document, representation and tool tabs expose role `Button`, no selected/ItemStatus state |
| F7 | Tool-tab accessible name | **FAIL** | every tool tab peer name is `Avalonia.Controls.StackPanel`, not “Monitor”, “Programmierung”, … |
| F8 | Search/field/list accessible names | **FAIL** | `NavSearch`, `ObjectSearch`, inspector device-name field, monitor mode/filter combos and both list containers have empty peer names |
| F9 | Help button target size | **FAIL** | help button is 1×30 DIP at (45,843) |
| F10 | Object filter Ctrl+Z | **FAIL** | genuine OS input: typed `abc`, `CanUndo=False`, Ctrl+Z leaves `abc`; project correctly unchanged |
| F11 | Channel row name noise | **FAIL** | channel rows carry a trailing comma and empty address: `A ▸ Deckenlicht, ` |
| F12 | Table expand/collapse by keyboard | **INFO / not measured** | harness selection event was rejected by the product’s session-owner context guard; handler exists in source at `WorkbenchWindow.axaml.cs:627-635` |
| F13 | Real 150 % / 200 % DPI | **INFO / not measured** | `RenderScaling=1`, no public setter; only 1.5×/2× raster captures and a DIP layout audit |

The two failure rows for the inspector overlay (F1) are the same defect reported
twice: `inspector-does-not-obscure-focusables` and
`inspector-covered-focus-targets`. “FAILURES 13” therefore corresponds to 12
distinct failing checks.

## Findings and exact fixes

### F1 (High) — narrow inspector overlay obscures 21 focusable controls

Measured at 900×600 with the inspector open. The inspector occupies
x=550…900 (350 DIP) while the editor toolbar and table remain live underneath.
Covered controls include three document-tab buttons, the watch/add-device/
properties actions, both toolbar checkboxes, `ObjectSearch`, the expand-all
button, representation tabs and seven table rows. Tab order still reaches them.

Source: `src/Knx.App/WorkbenchWindow.axaml.cs:1529-1539` moves `Inspector` into
the editor column, sets a fixed width and a shadow, but leaves the editor
hit-testable and in the tab order.

Fix: at narrow widths reserve a real column instead of overlaying
(`Zones.ColumnDefinitions[4/5]`), or while the overlay is open collapse the
editor toolbar and remove covered controls from hit testing/focus. Acceptance:
no focusable element intersects the inspector bounds (WCAG 2.4.11).

### F2 (High) — navigator Left/Right does not expand or collapse

Measured: focus/selection on “Erdgeschoss” (selected type `NavEntry`, index 3);
`Right` on the item and on `NavTree` both leave `ExpandedIds` at 6, with
`Handled=true`. Realized rows stay 10.

Source: handler registered as `NavTree.KeyDown += OnNavigatorKeyDown` at
`src/Knx.App/WorkbenchWindow.axaml.cs:153`; handler at `:619-625`. The event is
already handled when the instance handler would run, so the added handler is
ineffective (observed `Handled=true`, no state change).

Fix: register in the tunnel phase:
`NavTree.AddHandler(KeyDownEvent, OnNavigatorKeyDown, RoutingStrategies.Tunnel);`
or override `OnKeyDown` on a `NavTree` subclass before `ListBox` navigation.
Then verify `Right`/`Left` on the focused row changes `ExpandedIds`.

The table handler has the same registration shape
(`WorkbenchWindow.axaml.cs:154`, handler `:627-635`); table keyboard expansion
was not measurable in this harness (F12), so treat it as unverified, not
passing.

### F3 (Medium) — Settings dialog opens without focus

Measured: modal `SettingsWindow` active, `FocusManager` = `null`.

Source: `src/Knx.App/SettingsWindow.axaml.cs:19-28` has no `Opened` focus
handoff.

Fix: after `InitializeComponent()` add
`Opened += (_, _) => ThemeBox.Focus();`.

### F4 (Medium) — Settings dialog ignores Escape

Measured: Escape leaves the dialog open.

Source: `src/Knx.App/SettingsWindow.axaml:85` Cancel button has no
`IsCancel`.

Fix: set `IsCancel="True"` on the Cancel button (or add a `KeyDown` handler
that calls `Close(false)`).

### F5 (Medium) — status and footnote are not live regions

Measured: `TableFootnote`, the status message and “Lokal · keine
Busverbindung” all expose `LiveSetting=Off`.

Source: `src/Knx.App/WorkbenchWindow.axaml:99` (footnote), `:129` (status
message), `:130` (local-only status).

Fix: set `AutomationProperties.LiveSetting="Polite"` on the status message and
footnote; give them names (`AutomationProperties.Name="Statusmeldung"` /
`"Tabellenhinweis"`). Announce only errors assertively if every status change
would be too noisy.

### F6 (Medium) — tab strips expose Button role and no selected state

Measured: all document, representation and tool tabs use role `Button`;
active state is only the visual `active` class. There is no `TabItem` role,
`ItemStatus` or selection information. Avalonia 12.1.4's `AutomationPeer`
surface has no public SelectionItem pattern, so a plain `ButtonAutomationPeer`
cannot expose selection.

Source: view/tool tabs built at `src/Knx.App/WorkbenchWindow.axaml.cs:129-144`;
document tabs rebuilt at `:421-437`.

Fix (complete): replace the three strips with `TabControl`/`TabItem`, which
emits `TabItemAutomationPeer` and selection state. Short-term: a custom
`ButtonAutomationPeer` overriding `GetAutomationControlType()` to `TabItem`
plus `AutomationProperties.ItemStatus="aktiv"/"inaktiv"`; this fixes role/status
but not a full UIA SelectionItemPattern.

### F7 (Medium) — tool-tab accessible name is the CLR type name

Measured every tool tab peer name as `Avalonia.Controls.StackPanel`. The
existing “has names” assertion passes only because the name is non-empty.

Source: `src/Knx.App/WorkbenchWindow.axaml.cs:136` creates the button with
`ActionButton(title, …)`; `:139-143` replaces `Content` with an
`IconLabel` `StackPanel`, so the default peer derives the name from the
StackPanel's `ToString()`.

Fix: pass the accessible name explicitly after replacing the content, e.g.
`AutomationProperties.SetName(button, title);` (or
`ActionButton(title, …, "quiet", title)`).

### F8 (Medium) — unlabelled fields and list containers

Measured peer names are empty for:

| Control | Source |
|---|---|
| navigator search | `src/Knx.App/WorkbenchWindow.axaml:59` |
| object filter | `src/Knx.App/WorkbenchWindow.axaml:84` |
| navigator list | `src/Knx.App/WorkbenchWindow.axaml:60` |
| object table | `src/Knx.App/WorkbenchWindow.axaml:98` |
| inspector device name | `src/Knx.App/WorkbenchWindow.axaml.cs:802` |
| inspector line/room combos | `src/Knx.App/WorkbenchWindow.axaml.cs:808,812` |
| monitor mode combo | `src/Knx.App/WorkbenchWindow.axaml.cs:1112` |
| monitor filter combo | `src/Knx.App/WorkbenchWindow.axaml.cs:1135` |

Fix: add `AutomationProperties.Name` to each control, e.g. “Navigator
durchsuchen”, “Objekte filtern”, “Navigator”, “Kommunikationsobjekte”,
“Gerätename”, “Linie”, “Raum”, “Monitorart”, “Telegrammfilter”. Visible
`TextBlock` labels are not associated with the fields.

### F9 (Low) — help button is 1×30 DIP

Measured at (45,843), size 1×30 DIP; peer name is
`Knx.App.Views.ConceptIcon`. It is a tab stop but effectively invisible and
hard to click.

Source: `src/Knx.App/WorkbenchWindow.axaml:53` uses a `DockPanel` whose last
child (the help `Button`, `DockPanel.Dock="Bottom"`) is the fill child, so it
receives the ~1 DIP remainder after the rail `StackPanel`.

Fix: replace the `DockPanel` with
`<Grid RowDefinitions="*,Auto">` (rail stack row 0, help button row 1), or set
`LastChildFill="False"` plus a bottom-docked help row with explicit height.
Give it `AutomationProperties.Name="Hilfe"`.

### F10 (Low) — object filter Ctrl+Z does nothing

Measured with genuine OS input: type `abc` in `ObjectSearch`,
`CanUndo=False`, Ctrl+Z leaves `abc`; the project is not undone (shell guard
works). Cause: every keystroke calls `OnObjectSearch` → `RefreshEditor`, which
unconditionally re-assigns `ObjectSearch.Text` at
`src/Knx.App/WorkbenchWindow.axaml.cs:492`, resetting Avalonia's undo stack.
`NavSearch.Text` is written the same way at `:374`.

Fix: guard both writes, e.g.
`if (ObjectSearch.Text != tab.Filter) ObjectSearch.Text = tab.Filter;`
and the equivalent for `NavSearch`. Verify with OS Ctrl+Z that the text
restores and the project remains unchanged.

### F11 (Low) — empty channel address leaves a trailing “, ” in the row name

Measured table row names include `A ▸ Deckenlicht, ` and
`B ▸ Stehleuchte, ` (empty address, trailing comma).

Source: `src/Knx.App/WorkbenchWindow.axaml.cs:574-577` always appends
`, {row.Address}` when `row.Object` is null.

Fix: omit the separator when `string.IsNullOrEmpty(row.Address)`.

### F12 (INFO) — table keyboard expand/collapse not measured

The handler exists at `src/Knx.App/WorkbenchWindow.axaml.cs:627-635` and is
registered at `:154`. The harness could not select a row: setting
`ObjectTable.SelectedIndex` raises `OnObjectSelected` →
`MonitorSession.SelectionChanged`, which throws
`Session mutation must run on its owning UI context`
(`src/Knx.App/Workbench/WorkbenchSessions.cs:78-82`) because the harness
synchronization context differs from the session owner. Fix the harness or run
the real app, then test Left/Right on a device row.

### F13 (INFO) — real 150 % / 200 % DPI not measured

See the DPI section. `RenderScaling` was 1, the session is 96 dpi, and the
property has no public setter. The 1.5×/2× images are raster captures of a 1×
window, not a real high-DPI monitor run.

## Layout / DPI measurements

| Window | Inspector/tools | Unscrollable clips | Table viewport | Notes |
|---|---|---|---|---|
| 900×600 | no / no | 0 | 590×303, extent 896 | 87 clips are table rows inside the list `ScrollViewer` (expected) |
| 1024×700 | no / no | 0 | 714×403 | 2 scroll-viewer chrome clips (expected) |
| 1440×900 | no / no | 0 | 1068×603 | 2 scroll-viewer chrome clips (expected) |
| 900×600 | yes / no | 0 | 590×303 | 21 focusables under overlay (F1) |
| 900×600 | yes / yes | 0 | 590×158 / list 100 | 93 clips inside scrollers |
| Settings 620×560 | — | 0 | — | 2 scrollbar clips inside the settings scroller; Cancel/Save inside |
| Settings 1024×700 | — | 0 | — | 0 clips; Cancel/Save inside |

No toolbar child overlap at 900×600. The star column collapses to 48 DIP
between “Eigenschaften” (right 463) and “Kanal” checkbox (left 511).

DPI: the session is 96 dpi / 1×. `TopLevel.RenderScaling` and
`Window.DesktopScaling` are read-only in Avalonia 12.1.4; there is no supported
per-window override. 150 %/200 % was validated only as DIP layout plus 1.5×/2×
raster captures (`layout-1440x900-scale150.png`, `...scale200.png`,
`layout-900x600-scale200.png`). Text, borders and icons stay sharp; no new
clipping. **This is not a real 150/200 % monitor run.**

Source-level DPI risk remains: `MinWidth=900`, `MinHeight=540` DIPs
(`src/Knx.App/WorkbenchWindow.axaml:4`). A 1920×1080 display at 200 % has a
960×540 DIP work area before the taskbar, so the minimum height cannot fit; a
1366×768 display at 150 % gives about 911×512 DIPs. The window may be clipped or
forced off-screen. Lower `MinHeight` to about 480 and keep vertical regions
scrollable/collapsible, then verify on real hardware.

## Unverified / residual areas

- **Shift+Tab was not separately re-measured in this run.** Only forward tab
  order was recorded; the previous report's Shift+Tab result is not carried
  forward as a current measurement.
- **No OS screen-reader run.** Automation peers were inspected in-process, not
  through Narrator/UIA. Focus-change announcements, live-region speech,
  selection/expand patterns and the actual accessibility tree exposed to a UIA
  client are not proven. Do not claim screen-reader support from this report.
- **No real 150/200 % DPI or mixed-DPI multi-monitor run.** Only raster captures
  and DIP layout; window chrome, native menus and caret scaling were not
  measured.
- **Table keyboard expand/collapse not measured** (F12). The table handler's
  registration (`WorkbenchWindow.axaml.cs:154`) has the same shadowing risk as
  F2.
- **High contrast / forced colours, Windows text-size scaling, RTL, IME and
  mixed-language input were not tested.**
- **Tooltip measurement uses the open `PopupRoot`** (actual `TextBlock`
  foreground, `ToolTip` background) but not a screenshot of the popup; popup
  compositing and system-drawn chrome are out of scope.
- **OS foreground acquisition is non-deterministic** under the Windows
  foreground lock. OS Ctrl+Tab and OS text undo ran when foreground was
  confirmed; an earlier run had one rejected `Activate()`.
- **Programming tool is currently broken at runtime.** Calling
  `ShowTool("programming")` throws
  `InvalidOperationException: The control ListBox already has a visual parent
  DockPanel while trying to add it as a child of Grid` at
  `src/Knx.App/WorkbenchWindow.axaml.cs:1276` / `:1284` (`panel.Children.Add(list)`
  then `host.Children.Add(list)` without removing it first). This blocked
  keyboard/a11y checks of that tool; the dock shortcut test used the monitor
  view instead. Functional defect, not counted in the a11y failure total.
- Tab order was counted at 1440×900 only; the 900×600 order was not re-counted
  after the last changes. All visible controls at 900×600 were checked for
  clipping and the overlay coverage was measured.

## Evidence index

All temporary, under `%TEMP%\opencode\wb-v4-a11y\harness\artifacts`:

- `results.txt` — 45 PASS / 13 FAIL / 2 INFO
- `observations.txt` — focus, OS input, tooltip colors/ratios, settings focus
- `log.txt` — environment, DPI, automation API, screenshots, sync context
- `tab-order-default.txt` (36 stops), `tab-order-full.txt` (66 stops)
- `automation-roles.txt` — names, roles, live settings, bounds
- `tree-baseline.txt` — automation-relevant visual/peer tree
- `layout-{900x600,1024x700,1440x900}.txt`,
  `layout-900x600-inspector*.txt`, `inspector-coverage-900x600-inspector.txt`
- `layout-settings-{620x560,1024x700}.txt`
- `baseline-1440x900.png`, `layout-*.png`, `settings-*.png`,
  `theme-light.png`, `theme-dark.png`, `focus-ring-*.png`,
  `tab-order-full-focus-start.png`, `keyboard-900x600-inspector-tools.png`

Harness source (temporary, not committed):
`%TEMP%\opencode\wb-v4-a11y\harness\Program.cs` and `A11yHarness.csproj`.
It references the product Release assemblies and the product's NuGet versions
(Avalonia 12.1.4, Dock 12.1.0.12, CommunityToolkit.Mvvm 8.4.0).

## Repro

```powershell
cd C:\projects\ai_work\agent-wt\plan-a11y-dpi
dotnet build src\Knx.App\Knx.App.csproj -c Release

# refresh the harness references from the fresh product build
$src = "C:\projects\ai_work\agent-wt\plan-a11y-dpi\src\Knx.App\bin\Release\net9.0"
$dst = "$env:TEMP\opencode\wb-v4-a11y\harness\lib"
Copy-Item "$src\BusChord.dll","$src\Knx.Bus.dll","$src\Knx.Core.dll","$src\Knx.ProjectCompare.dll","$src\Knx.Reports.dll" $dst -Force

cd $env:TEMP\opencode\wb-v4-a11y\harness
dotnet build -c Release
.\bin\Release\net9.0\win-x64\A11yHarness.exe   # exit 1 because failures are recorded
Get-Content artifacts\results.txt
```
