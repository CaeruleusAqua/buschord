# Workbench V4 — accessibility, focus and DPI validation

> Validation-only report. No product source was changed. The only commit is this
> document.

Base commit: `12fc904ee05089976eac93426141c6fdb348ffe1`
Branch: `agents/workbench-v4/a11y-dpi`
Worktree: `C:\projects\ai_work\agent-wt\workbench-v4-a11y-dpi`
Product build: `dotnet build ets6.sln -c Release` → **PASS**, 0 errors,
24 warnings (pre-existing obsolete Avalonia APIs).

Environment: Windows, primary display 1920×1200, `GetDpiForWindow`/`GetDpiForSystem`
= 96 dpi (100 %), Avalonia 12.1.4, target framework net9.0.

Legend: **native** = observed at runtime through Avalonia automation peers,
routed input and rendered frames; **source** = traced in code only;
**inferred** = conclusion drawn from observed flags/behaviour plus source, not
directly measured.

## Method

An external harness in `%TEMP%\opencode\wb-v4-a11y\harness` (not in the repository)
referenced the Release assemblies read-only and used the product's existing
`App.TestWindowFactory` hook (`src/Knx.App/App.axaml.cs:13`) to construct a real
`WorkbenchWindow` with `WorkbenchJson.CreateDemo()` (synthetic Villa Linden).
It drove:

- Avalonia routed `KeyDown` events on the focused control / window (`RaiseEvent`),
  the same route a real key uses after the platform layer;
- genuine OS keyboard input (`SendInput`, unicode chars and Ctrl+Z / Ctrl+Tab) when
  `GetForegroundWindow()` matched the harness window;
- automation peers (`ControlAutomationPeer.CreatePeerForElement`) for name, role,
  control/content element, keyboard focusability and live setting;
- `RenderTargetBitmap` at 1×, 1.5× and 2× for screenshots.

Exact repro:

```powershell
cd C:\projects\ai_work\agent-wt\workbench-v4-a11y-dpi
dotnet build ets6.sln -c Release

cd $env:TEMP\opencode\wb-v4-a11y\harness
dotnet build -c Release
.\bin\Release\net9.0\win-x64\A11yHarness.exe   # exit code 1 = failures recorded

# evidence (temporary, not committed)
dir $env:TEMP\opencode\wb-v4-a11y\harness\artifacts
Get-Content $env:TEMP\opencode\wb-v4-a11y\harness\artifacts\results.txt
```

Result: **31 passed, 21 failed, 1 informational** check (53 assertions plus the
failure counter line). Measurements below are from the final run; the artifacts
directory contains the tab-order dumps, automation-role dump, layout dumps, tree
dump, screenshots and observation log.

## Pass/fail summary

| ID | Check | Result | Evidence |
|---|---|---|---|
| A1 | Window activates, OS foreground reached | **PASS** (observed; one earlier attempt was rejected by the foreground lock) | `Activate()`; `GetForegroundWindow` == HWND; `observations.txt:2` |
| A2 | Tab order reaches header, rail, navigator, editor/table/toolbar, dock, inspector | **PASS** | 58 distinct stops, region counts 7/8/5/22/14/5; `tab-order-full.txt` |
| A3 | Shift+Tab reverses direction | **PASS** | Tab returns to `ObjectSearch`; `results.txt` |
| A4 | Ctrl+B / Ctrl+J / Ctrl+K | **PASS** (also with a `TextBox` focused) | `results.txt`; palette opens focused 5/5 |
| A5 | Ctrl+Tab cycles work items (documented shortcut) | **FAIL** | handled=true, active tab unchanged, routed **and** genuine OS input; `results.txt:8-9` |
| A6 | Palette Down moves focus to results | **FAIL** | query has focus, `Handled=true`, focus stays; `observations.txt` |
| A7 | Palette list arrows / Enter / Escape / focus restore | **PASS** | Tab reaches first result; ↑/↓ change index 0↔1; Enter opens "Projektprüfung"; Escape closes and restores `ObjectSearch` |
| A8 | Text-field undo precedence (shell must not undo project) | **PASS for the shell guard; FAIL for the field** | Ctrl+Z outside fields undoes the project; inside `ObjectSearch` Ctrl+Z is swallowed and does nothing (F11) |
| A9 | Navigator/table expand–collapse by keyboard | **FAIL** | Right/Left do nothing; peers have no expand state; `results.txt` |
| A10 | Focus survives tab switch | **FAIL** | focus `null` |
| A11 | Focus survives representation switch | **FAIL** | focus `null` |
| A12 | Focus survives tab close | **FAIL** | focus `null` |
| A13 | Focus after project open/replace | **PASS** | `ObjectSearch` keeps focus |
| A14 | Focus after dialog close | **FAIL** | `null`; dialog itself opens with no focus |
| A15 | Dialog initial focus / Escape | **FAIL** | initial focus `null`, Escape does not close |
| A16 | Nav/table row names / tab names / icon names / tool names | **PASS** | see automation section |
| A17 | Status and footnote are live regions | **FAIL** | `LiveSetting=Off` for both |
| A18 | Search fields, inspector field, dock combos have names | **FAIL** | all peer names empty |
| A19 | Tab strips expose tab role/selection | **FAIL** | role `Button` for all doc/view/tool tabs |
| A20 | Target size of icon-only controls | **FAIL** | help `?` is 1×30 DIP; nav/table chevrons 5–9×30 DIP |
| A21 | Layout 900×600, 1024×700, 1440×900 | **PASS** for clipping/scroll | 0 unscrollable clips at all sizes; table `Auto` scroller |
| A22 | Layout 900×600 with inspector / tools open | **FAIL** for overlay | 21 focusable controls under the inspector overlay |
| A23 | 150 % / 200 % scaling | **NOT MEASURABLE at runtime** | `RenderScaling=1`, no public setter; DIP layout + 1.5×/2× raster captures (see DPI) |
| A24 | Keyboard scrolling of table | **PASS** | End at 900×600 → offset 796 of extent 896, viewport 100 |
| A25 | Focus ring visible | **PASS** | 2 px accent ring on `ContentPresenter` for primary and quiet buttons |

## Findings, ordered by severity

### F1 (High) — Ctrl+Tab is documented but does nothing

Observed: `Ctrl+Tab` with focus on the window and with focus on a child sets
`KeyEventArgs.Handled=true` but leaves `Workspace.ActiveTab` unchanged. A genuine
OS `Ctrl+Tab` (foreground confirmed) behaves identically. Tabs count 2→2.
`analysis/workbench-v4-shell-milestone.md` and the in-app help advertise the
shortcut (`src/Knx.App/WorkbenchWindow.Commands.cs:70`).

Cause (**inferred** from the handled flag and source): Avalonia's top-level
control-tab navigation consumes `Ctrl+Tab` before the window's bubble handler
runs. The handler is registered as `KeyDown += OnShellKeyDown`
(`src/Knx.App/WorkbenchWindow.axaml.cs:128`) and begins with
`if (e.Handled) return;` (`:1186`), so the `case Key.Tab` branch at `:1201` is
never reached.

Fix (source): register the shell shortcut handler in the tunnel phase, e.g.
`AddHandler(InputElement.KeyDownEvent, OnShellKeyDown, RoutingStrategies.Tunnel)`
at `:128`, and set `e.Handled = true` after cycling. Alternatively drop the
shortcut from `Commands.cs:70` and the help text. Verify with a real
`Ctrl+Tab` and with `FocusManager.GetFocusedElement()` unchanged.

### F2 (High) — focus is lost on every structural editor refresh

Observed, all with a focused control before the action:

| Action | Focus before | Focus after |
|---|---|---|
| open another work item (tab switch) | doc tab button | `null` |
| switch "Kommunikationsobjekte" → "Parameter" | `ObjectTable` | `null` |
| close the active tab via its × | close button | `null` |
| open/replace project | `ObjectSearch` | `ObjectSearch` (pass) |

Cause (source): `RefreshTabs()` clears `ObjectTabs.Children` and recreates every
tab button (`src/Knx.App/WorkbenchWindow.axaml.cs:369-385`); `RefreshEditor()`
reassigns `ObjectTable.ItemsSource` (`:446`) and rebuilds the inspector
(`:616-702`) on every representation switch. The focused element is detached
without a focus handoff. Switching `Workspace.ActiveTab` triggers
`OnWorkspaceChanged` → `RefreshWorkspace` synchronously (`:255-258`, `:301`).

Fix (source): record a focus key before the rebuild (tab target/close target,
selected row key, representation name) and restore focus after the new controls
exist, e.g. after `RefreshTabs()` call
`ObjectTabs.Children.OfType<Border>()...` and focus the new button for the same
target; for the table, focus the item whose `WorkbenchRow.Key` matches the
previously focused/selected row. Minimal variant: do not recreate tab headers on
every refresh — update `Classes.Set("active", …)` and the title text in place.

### F3 (High) — palette ↓ does not reach the result list

Observed: palette opens with focus in the query (5/5), the result list has 2
entries and a selected index 0; `Down` on the query yields `Handled=true` and
focus stays in the query. `Tab` from the query does reach the first
`ListBoxItem`, and list ↑/↓ then work.

Cause (source + observed): the palette handler calls `list.Focus()` on a
`ListBox` that has `Focusable=false`; the observation shows
`palette list: Focusable=False, IsFocused=False` and
`list.Focus(): returned=False`, focus stays in the query. The handler sets
`e.Handled=true`, so nothing else can react
(`src/Knx.App/WorkbenchWindow.Commands.cs:269-272`). A control probe confirmed a
`TextBox` does deliver `Down` to an instance handler (`handled=False`), so this
is not the TextBox consuming the key.

Fix (source): move focus to the realized item instead of the list:
`(list.ContainerFromIndex(list.SelectedIndex) as Control ?? list.ContainerFromIndex(0))?.Focus();`
(or set `list.Focusable = true` before `Focus()`), keeping `e.Handled = true`.
Keep the existing list handling for ↑/↓/Enter/Escape (`:273-277`). Verify with a
real `Down` key and with focus on the first `ListBoxItem`.

### F4 (High) — message dialog opens without focus, Escape does not close, owner focus is not restored

Observed with `ConfirmDialog.ShowMessageAsync` and no other window open:
`dialog.IsActive=True`, `FocusManager.GetFocusedElement()=null`; Escape leaves
the dialog open; after closing via its button, the owner window's
`FocusManager` is `null` (focus was on `ObjectSearch` before).

Cause (source): `ConfirmDialog` never focuses a control
(`src/Knx.App/ConfirmDialog.cs:24-61`), and the single "Schließen" button is
`IsDefault` but not `IsCancel` (`:99-100`), so Escape has no target. `ShowAsync`
(`:69-75`) does not restore the previous owner focus; the palette does restore
it (observed PASS), because the platform reactivates the owner and Avalonia
returns focus to the last focused element.

Fix (source): in the `ConfirmDialog` constructor after `Content = body;` focus the
last/primary button on `Opened`, set `IsCancel = true` for the close/cancel
choice in `ShowMessageAsync` (`:99-100`), and in `ShowAsync` capture
`owner.FocusManager?.GetFocusedElement()` before `ShowDialog` and re-focus it
after the awaited dialog closes (`:69-75`).

### F5 (High) — navigator and table cannot be expanded from the keyboard

Observed: focus on the "Erdgeschoss" navigator row, `Right`/`Left` leave
`ExpandedIds.Count` at 6; focus on a device row in the table, `Right` leaves
`CollapsedDeviceIds` empty. The visible chevrons are `Button`s inside the
`ListBoxItem` content (`src/Knx.App/WorkbenchWindow.axaml.cs:499-507` and
`:523-532`) and are not tab stops inside the composite list (the list has one
tab stop). Their peers expose `role=Button` with a name, but the row peer is a
plain `ListItem` with no expanded/collapsed state. Avalonia 12.1.4's
`AutomationPeer` API exposes no `ExpandCollapse` pattern (verified by reflection;
see `log.txt`).

Cause (source): no `KeyDown` handler on `NavTree`/`ObjectTable`
(`src/Knx.App/WorkbenchWindow.axaml:60`, `:98`); only the mouse-clickable
chevron mutates `ExpandedIds` / `CollapsedDeviceIds`.

Fix (source): add `NavTree.KeyDown` and `ObjectTable.KeyDown` handlers for
`Key.Right`/`Key.Left` that toggle the focused item's node/row key and call
`RefreshNavigation()` / `RefreshEditor()`, with focus restoration per F2.
Set `AutomationProperties.ItemStatus` ("erweitert"/"reduziert") on the row,
since Avalonia cannot expose the UIA pattern. Medium-term: replace the flat
`ListBox` with `TreeView`/`TreeDataGrid` for hierarchy semantics.

### F6 (High) — at 900×600 the inspector overlay covers focusable editor controls

Observed (900×600, inspector open): the inspector occupies x=550…900 (350 DIP)
while the editor toolbar at x=286…876 and the table at x=286…876 stay live.
**21 focusable controls** intersect the overlay, including doc-tab buttons, the
editor actions, both toolbar checkboxes, `ObjectSearch`, "Alle", and seven
table rows (`inspector-coverage-900x600-inspector.txt`). Tab stops remain
reachable while visually hidden. With dock open the overlay also covers the
monitor toolbar and list columns.

Cause (source): narrow layout moves the inspector into the editor column and
aligns it right (`src/Knx.App/WorkbenchWindow.axaml.cs:1092-1101`) without
removing the covered content from hit-testing or tab order.

Fix (source): at narrow widths either reserve a real column
(`Zones.ColumnDefinitions[5].Width = InspectorWidth`, let the editor reflow) or,
while the overlay is open, collapse the editor toolbar and set
`IsHitTestVisible=false`/remove covered controls from the tab order; optionally
close the overlay when focus enters the editor. Acceptance criterion: no
focusable element has a non-empty intersection with the inspector bounds
(WCAG 2.4.11 Focus Not Obscured).

### F7 (Medium) — status and footnote are not announced

Observed: status TextBlock (`src/Knx.App/WorkbenchWindow.axaml:125`) and table
footnote (`:99`) have `AutomationProperties.LiveSetting = Off`, peer names are
the plain text and no `AutomationProperties.Name` is set. The status line changes
after undo/save/actions; assistive technology gets no live announcement.

Fix (source): set `AutomationProperties.LiveSetting="Polite"` (or `Assertive` for
errors) and `AutomationProperties.Name="Statusmeldung"` on the status TextBlock,
and `LiveSetting="Polite"` on the footnote. If every status change would be too
noisy, only announce errors via a separate hidden live region. Verify with a UIA
client, not only the in-process peer.

### F8 (Medium) — unlabelled form fields and list containers

Observed peer names (role `Edit`/`ComboBox`/`List`):

| Control | Source | peer name |
|---|---|---|
| navigator search | `WorkbenchWindow.axaml:59` | `""` |
| object filter | `WorkbenchWindow.axaml:84` | `""` |
| inspector device name | `WorkbenchWindow.axaml.cs:679` | `""` |
| monitor mode combo | `WorkbenchWindow.axaml.cs:799` | `""` |
| monitor filter combo | `WorkbenchWindow.axaml.cs:821` | `""` |
| navigator list | `WorkbenchWindow.axaml:60` | `""` |
| object table | `WorkbenchWindow.axaml:98` | `""` |
| main menu | `WorkbenchWindow.axaml:42` | `""` |

Fix (source): add `AutomationProperties.Name` at those lines, e.g.
"Navigator durchsuchen", "Objekte filtern", "Gerätename",
"Monitorart", "Telegrammfilter", "Navigator", "Kommunikationsobjekte",
"Hauptmenü". The visible `Note("Bezeichnung")` at
`WorkbenchWindow.axaml.cs:680` is not associated with the field.

### F9 (Medium) — tab strips are plain buttons, with no selected state

Observed: document tabs, representation tabs and tool tabs all expose
`role=Button` (`automation-roles.txt`). Names come from the button content or
the close-button accessible name; the active tab carries no `ItemStatus` or
selection information visible to a peer.

Cause (source): the strips are built as `Button`s in
`src/Knx.App/WorkbenchWindow.axaml.cs:109-122` and `:369-385`; the active class is
purely visual (`Classes.Set("active", …)`).

Fix (source): use `TabControl`/`TabItem` for the document, representation and
tool strips (Avalonia then emits a `TabItem` peer), or add a custom
`ButtonAutomationPeer` returning `AutomationControlType.TabItem` plus an
`ItemStatus`/`HelpText` of "aktiv"/"inaktiv"; Avalonia 12.1.4 has no
SelectionItem/ExpandCollapse pattern API, so a full UIA `SelectionItemPattern`
is not achievable without a custom peer implementation.

### F10 (Medium) — the help button is effectively invisible

Observed: `?` button in the rail is 1×30 DIP at (45,843)
(`automation-roles.txt`, `tab-order-full.txt` stop 10); it is a tab stop but has
almost no visible or clickable area. Cause (source):
`WorkbenchWindow.axaml:53` uses a `DockPanel` whose first (undocked) child is the
rail `StackPanel`; the last `Button` becomes the fill child and receives the
remaining width of 1 px.

Fix (source): replace the `DockPanel` with a `Grid RowDefinitions="*,Auto"`
(rail stack in row 0, help button in row 1), or set
`DockPanel.Dock="Bottom"` on a non-fill layout. Also give it an
`AutomationProperties.Name="Hilfe"`.

### F11 (Medium) — the object filter's Ctrl+Z does nothing (undo history is reset)

Observed with genuine OS input, foreground confirmed:

| Field | Typed | `CanUndo` after typing | After Ctrl+Z |
|---|---|---|---|
| plain control `TextBox` | `xyz` | **true** | `xy` |
| control `TextBox` that re-applies its text on `TextChanged` | `xyz` | **false** | `xyz` |
| product `ObjectSearch` | `abc` | **false** | `abc` |

The project is **not** undone while the text field has focus
(`projectUnchanged=True`), so the shell precedence guard works
(`src/Knx.App/WorkbenchWindow.axaml.cs:1187`, `:1198`); the field itself just
cannot undo.

Cause (source, confirmed by the control probe): every keystroke raises
`OnObjectSearch` (`WorkbenchWindow.axaml.cs:573-577`) → `RefreshEditor()`, which
unconditionally writes the filter back into the field
(`WorkbenchWindow.axaml.cs:440`). Re-assigning `TextBox.Text` resets Avalonia's
undo stack even when the string is identical (probe above).

Fix (source): guard the write at `:440`:
`if (ObjectSearch.Text != tab.Filter) ObjectSearch.Text = tab.Filter;` and the
same at `:331` for `NavSearch`. Then Ctrl+Z in the field restores the text and
Ctrl+Z outside fields still reaches `ViewModel.UndoCommand` (`:1198-1199`,
verified PASS).

### F12 (Low) — splitters are unnamed tab stops

Observed: `NavSplitter` (6×824, role `Thumb`), tools and inspector splitters are
keyboard focusable and appear in the tab order; peer names are empty.
Source: `src/Knx.App/WorkbenchWindow.axaml:64`, `:107`, `:117`.

Fix: set `AutomationProperties.Name` ("Navigatorbreite ändern",
"Werkzeugbereichshöhe ändern", "Inspektionsbreite ändern") and
`AutomationProperties.HelpText="Pfeiltasten ändern die Größe"`.

### F13 (Low) — row names are good but carry noise / no hierarchy

Observed: navigator items expose names such as `"Erdgeschoss, 5"`, table items
`"Schalten, Nummer #0, DPT 1.001, 1 Bit, Flags CT, 1/0/1"` — **PASS**. Channel
rows expose `"A ▸ Deckenlicht, "` with a trailing comma and empty address
(`WorkbenchWindow.axaml.cs:517-519`). Navigator rows are `ListItem`s with no
level/expanded state; the inline expand button is a separate child peer
(`tree-baseline.txt`).

Fix (source): omit the `, {row.Address}` part when the address is empty
(`:517-519`); set `AutomationProperties.ItemStatus` on navigator rows for
erweiterbar/erweitert (F5) or move the tree to `TreeView`.

### F14 (Low) — chevrons are tiny targets

Observed: navigator chevrons are 5–9×30 DIP, table chevrons 9×30 DIP
(`observations.txt`; `tree-baseline.txt`). WCAG 2.5.8's 24×24 minimum has a
spacing exception that may technically apply since the rows are 29–32 DIP apart,
but the affordance is hard to hit and inconsistent with 30 DIP buttons
elsewhere. Fix (source): give the chevron buttons `MinWidth=24` and
`HorizontalAlignment=Stretch` in a 24-DIP first column
(`WorkbenchWindow.axaml.cs:495-507`, `:523-532`); the keyboard path from F5 is
the more important fix.

## Tab order

`tab-order-default.txt` (tools/inspector closed): **36 stops**, starts at
menu "Projekt", then palette button, "Programmieren", rail (7 buttons + help),
perspectives, navigator search, navigator list (one stop), nav splitter, three
document tabs + close buttons + "+", editor actions, representation tabs,
toolbar, one table row, then wraps.

`tab-order-full.txt` (tools + inspector open): **58 stops**, 60 lines with the
cycle. Region coverage was counted from control origin coordinates, so the
buckets are approximate at their borders (header = y<49 therefore includes the
tab row at y=48): header 7, rail 8, navigator 5, editor/table/toolbar 22, dock 14,
inspector 5.
Order is visual: header → rail → navigator → document tabs → editor actions →
view tabs → toolbar → table → tools splitter/tabs/toolbar → inspector splitter/
close/field/buttons → menu. One composite list stop per list is correct
behaviour; the selected item is the stop.

Screenshots: `tab-order-full-focus-start.png`, `focus-ring-primary.png`,
`focus-ring-quiet.png` (2 px `#62D1CD` ring on `ContentPresenter` for both a
primary and a quiet button — PASS).

## Shortcuts and dialog behaviour

| Input | Observed |
|---|---|
| Ctrl+B | toggles navigator, also from `ObjectSearch` (PASS) |
| Ctrl+J | shows/hides dock (PASS) |
| Ctrl+K | opens palette, query focused 5/5 (PASS) |
| Ctrl+Tab | handled, no tab change, routed **and** OS (FAIL F1) |
| Ctrl+Z in text field | handled by `TextBox`, project untouched; field undo broken in `ObjectSearch` (F11) |
| Ctrl+Z outside text field | project undo/replace observed (PASS) |
| Palette ↓ | does not leave query (FAIL F3) |
| Palette Tab / ↑ / ↓ / Enter / Escape | first result, index 0↔1, activates "Projektprüfung", closes + restores owner focus (PASS) |
| Message dialog Escape | does not close (FAIL F4) |

Focus matrix (same as F2/F4):

| Trigger | Before | After |
|---|---|---|
| tab switch | doc tab | `null` |
| representation switch | `ObjectTable` | `null` |
| tab close | close button | `null` |
| project open/replace | `ObjectSearch` | `ObjectSearch` |
| inspector show/hide | `ObjectSearch` | `ObjectSearch` |
| palette open | `ObjectSearch` | palette query (5/5) |
| palette Escape | query | `ObjectSearch` |
| message dialog open | `ObjectSearch` | `null` |
| message dialog close | — | `null` |

## Automation surface (observed peers)

| Element | Role | Name | Notes |
|---|---|---|---|
| navigator list | List | `""` | id `NavTree`, not keyboard-focusable itself |
| navigator rows | ListItem | `"Villa Linden"`, `"Erdgeschoss, 5"`, … | no level/expand state; inline chevron is a child Button (5–9 px) |
| object table | List | `""` | id `ObjectTable` |
| table rows | ListItem | `"Tastsensor Wohnzimmer, 1.1.11"`, `"Schalten, Nummer #0, DPT 1.001, …"` | channel rows end `", "` |
| document tabs | Button | tab title; close buttons named `"… schließen"` | no TabItem role/selected state |
| representation tabs | Button | `"Kommunikationsobjekte"` … | same |
| tool tabs | Button | `"Monitor"`, `"Programmierung"`, `"Prüfmeldungen"` | same |
| rail buttons | Button | `"Gebäude"`, `"Topologie"`, `"Gruppen"`, `"Produktkatalog"`, `"Projektprüfung"`, `"Monitor"`, `"Optionen"` | 35×35, names set in code |
| tool chrome | Button | `"Werkzeug auslagern"`, `"Werkzeug maximieren"`, `"Werkzeug ausblenden"` | pass |
| inspector close | Button | `"Eigenschaften ausblenden"` | pass |
| search fields | Edit | `""` | F8 |
| dock combos | ComboBox | `""` | F8 |
| splitters | Thumb | `""` | F12 |
| status/footnote | Text | text | `LiveSetting=Off` (F7) |

`AutomationId` is present only where `x:Name` exists (`NavTree`, `ObjectTable`,
`MainMenu`, `TableFootnote`, `RelatedBox`, `Unlinked`, splitters); most dynamic
buttons have no id, which is acceptable when the name is complete.

## Layout and DPI

Measured in DIPs (Avalonia layout unit):

| Window | inspector/tools | unscrollable clips | table viewport | toolbar overlap |
|---|---|---|---|---|
| 900×600 | no / no | 0 | 590×303 DIP, extent 638 | none; children 286…876 |
| 1024×700 | no / no | 0 | 714×403, extent 762 | none |
| 1440×900 | no / no | 0 | 1068×603, extent 1116 | none |
| 900×600 | yes / no | 0 | 590×303, covered from x=550 | overlay covers 21 focusables (F6) |
| 900×600 | yes / yes | 0 | 590×158 / list 100 | dock 638×140; overlay covers dock and table |

Clipped-without-scroll counts are 0 everywhere; 87 clipped entries at 900×600
are table rows below the viewport inside the `ListBox` `ScrollViewer` (by
design). The table has `HorizontalScrollBarVisibility=Auto` and a working
vertical scroller; `End`/`PageDown` scroll: offset 796 (extent 896, viewport 100 at
900×600 with dock + inspector open) — PASS. The inspector scroller works
(480/480 at 900×600). The toolbar does not overlap itself at 900: the star
column collapses to 17 DIP between "Eigenschaften" (right 463) and "Externe
Kanäle" (left 480).

Scaling caveat: `TopLevel.RenderScaling` and `Window.DesktopScaling` are
read-only in Avalonia 12.1.4 (reflection: `canWrite=False`), the session is
96 dpi/1×, and there is no supported per-window override. Layout is DIP-based,
so 150 %/200 % changes device pixels, not the DIP canvas. I validated the canvas at
the three sizes and additionally rasterised 1.5× and 2×
(`layout-1440x900-scale150.png`, `...-scale200.png`, `layout-900x600-scale200.png`);
text, 1 px borders and icons stay sharp, no new clipping. **This is a raster
check, not a real 150/200 % monitor run.**

DPI risk (**source inference**, not measured): `MinWidth=900`, `MinHeight=600`
DIPs (`src/Knx.App/WorkbenchWindow.axaml:3-4`). On a 1920×1080 display at 200 %
the work area is 960×540 DIPs, so the 600-DIP minimum height cannot fit (and the
taskbar reduces it further); 1366×768 at 150 % gives ~911×512. Windows may clip
or force the window off-screen. Recommendation: lower `MinHeight` to ≈520 and
make the vertical regions scroll/collapse, then verify on a real 200 % monitor.

## Residual risks / not measured

- Automation peers were inspected in-process, not through a real UIA client.
  Narrator announcement, focus-change events and selection/expand state are not
  proven; Avalonia 12.1.4 has no pattern API for expand/selection, so F5/F9 need
  custom peers or a real `TreeView`/`TabControl`.
- No high-contrast/forced-colours test. The custom `Wb*` theme brushes may not
  follow system contrast settings.
- No runtime 150/200 % monitor, no Windows text-size scaling (125–225 %), no
  mixed-DPI multi-monitor, no RTL, no IME/autocomplete. The shell closes the
  inspector on any Escape while it is visible
  (`WorkbenchWindow.axaml.cs:1207`), which may interfere with IME candidate
  windows.
- `FocusManager` is `null` after plain `Activate()` until a control is focused;
  this is not necessarily a defect but means the first screen-reader query after
  window activation has no focused element.
- OS foreground acquisition is not deterministic under the Windows foreground
  lock (1 of 5 runs was rejected); OS input checks are guarded by
  `GetForegroundWindow` and are marked INFO when unavailable.
- The 2× PNGs are raster captures of a 1× window; they do not prove DPI
  independence of system-drawn window chrome, native menus or the OS caret.

## Evidence index

All under `%TEMP%\opencode\wb-v4-a11y\harness\artifacts` (temporary):

- `results.txt` — 21 failed / 31 passed / 1 informational check, exact details
- `observations.txt` — focus, OS input, probe results, target sizes
- `log.txt` — environment probes, DPI, automation API, per-action logs
- `tab-order-default.txt` (36 stops), `tab-order-full.txt` (58 stops)
- `automation-roles.txt` — peer name/role/state dump
- `tree-baseline.txt` — automation-relevant visual/peer tree
- `layout-{900x600,1024x700,1440x900}.txt`,
  `layout-900x600-inspector*.txt`, `inspector-coverage-900x600-inspector.txt`
- `baseline-1440x900.png`, `layout-*.png`, `focus-ring-*.png`,
  `tab-order-full-focus-start.png`, `keyboard-900x600-inspector-tools.png`

Harness source (temporary, not committed):
`%TEMP%\opencode\wb-v4-a11y\harness\Program.cs` and `A11yHarness.csproj`.
It references the product's Release `BusChord.dll` and `Knx.*.dll` plus the same
NuGet versions already used by the product (`Avalonia 12.1.4`, `Dock 12.1.0.12`,
`CommunityToolkit.Mvvm 8.4.0`), copies nothing into the repository and changes no
product file.
