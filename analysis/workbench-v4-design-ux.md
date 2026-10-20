# Workbench V4 — UX, accessibility and interaction review

Commit reviewed: `a6292e9d3f84f97cd51a1c343aa4769814c95532` (integration worktree).
Review worktree: `C:\projects\ai_work\agent-wt\workbench-v4-design-ux`, branch `agents/workbench-v4/design-ux`.
Reference: `C:\Users\julia\Downloads\knx-workbench-v4-bundle` (`ANFORDERUNGEN_UND_USER_STORIES.md`, `KONZEPT.md`, `DATENMODELL_UND_ZUSTAENDE.md`), `docs/manual.md`, `analysis/workbench-v4-acceptance.md`, actual code.
Scope: whole product shell (`WorkbenchWindow`), its dialogs, embedded legacy panes, monitor/programming tools, theme and existing docs. No product edits, no dependencies, no bus/router/private catalogue access, no fixes.

## Method and what was executed

| Check | Result |
|---|---|
| `dotnet build src/Knx.App/Knx.App.csproj -c Release` | PASS, 0 errors (23 obsolete-API warnings: `Watermark`, `SystemDecorations`) |
| `BusChord.dll --workbench-selftest` | PASS, all 46 routed checks incl. tab-local filter/selection/scroll, layout restore, hidden-session lifetime, 10 027-row virtualization |
| `BusChord.dll --screenshot-selftest` | PASS (`SCREENSHOT OK`) |
| `Knx.Workbench.Tests` | PASS |
| `Knx.WorkbenchUiState.Tests` | PASS (17) |
| `Knx.WorkbenchSessions.Tests` | PASS (56) |
| `Knx.WorkbenchTheme.Tests` | PASS (source contract only, no rendering) |
| Native UX probe (external temp harness, synthetic `WorkbenchJson.CreateDemo()`, no product change) | Automation peers, focus, 900-wide layout, tool-tab geometry, palette key handling, link dialog Escape, monitor mode transition, empty-queue button states |
| Screenshots | `demo`, `demoparams`, `demoinspector`, `demomonitor`, `demoprogramming`, `democatalog`, `demolinks`, `demo-small` (1024×700), `demoinspector-small` under `%TEMP%\opencode\wb-ux-shots` |
| Contrast | WCAG 2.x sRGB contrast computed from the literal theme colors |

Legend: **native** = observed at runtime; **source** = code path read and traced, not executed; **visual** = screenshot; **computed** = arithmetic, not eyeballed.

## P1 — breaks a stated V4 promise or blocks core use

### UX-01 — Screen-reader names of navigator and table rows are raw debug records (native)

Every `ListBoxItem` in the navigator and the object table exposes the record's `ToString()` as its accessible name because `NavEntry`/`WorkbenchRow` are records used as `Content` and no `AutomationProperties.Name` is set on the row or on the item container.

Observed with the automation peer:

```
nav row peer name:   "NavEntry { Node = WorkbenchNavNode { Key = project:villa-linden, Name = Villa Linden,
                      Meta = , Model = Knx.Project, Target = WorkbenchTarget { Kind = project, ... } }, Depth = 0 }"
object row peer name:"WorkbenchRow { Key = device:taster-living, Kind = device, Name = Tastsensor Wohnzimmer,
                      Address = 1.1.11, Device = Knx.DeviceInstance, Object = , Depth = 0, ... }"
AutomationProperties.GetName(row): ""
```

- Files: `src/Knx.App/WorkbenchWindow.axaml.cs:82-83` (templates), `:482-542` (row visuals), `src/Knx.App/WorkbenchWindow.axaml:56,90` (ListBoxes), `src/Knx.App/Workbench/WorkbenchProjection.cs:170-176` (records), `src/Knx.App/WorkbenchWindow.axaml.cs:1115` (`NavEntry` record).
- Repro: start the app, focus the navigator, read the selected item with any screen reader (or call `ControlAutomationPeer.CreatePeerForElement(item).GetName()`).
- Expected: "Wohnzimmer, 3 Geräte" / "1.1.11 Tastsensor Wohnzimmer, Gerät" / "#0 Schalten, DPT 1.001, 1 Bit, CT, 1/0/1".
- Fix (one place each): add a display property (`DisplayName`) and bind it via `AutomationProperties.Name` in the row template; or override `ToString()` on `NavEntry`/`WorkbenchRow`. Add a selftest assertion on the peer name so the debug dump cannot come back.

### UX-02 — The "Beziehungen" tab bypasses every V4 link rule and hides the sending address (source)

The object tab's `relations` representation embeds the legacy `GroupLinksPane`. It calls `MainWindowViewModel.ChangeGroupLink` → `GroupLinks.Link`, which validates only object/GA identity (`src/Knx.Core/GroupLinks.cs:7-13`). The batch dialog and domain (`WorkbenchProject.ValidateLink`, `WorkbenchWindow.Commands.cs:302`) reject DPT mismatch, bit-length mismatch and missing Secure capability. The same user, in the same tab, gets opposite answers; the pane also has no "sendend" marker even though sending is the US05 headline.

- Files: `src/Knx.App/WorkbenchWindow.axaml.cs:445-450`; `src/Knx.App/Views/GroupLinksPane.axaml.cs:187-215`; `src/Knx.App/MainWindowViewModel.cs:492-503`; `src/Knx.Core/GroupLinks.cs:7-13`.
- Repro: open demo room → Tastsensor → tab "Beziehungen" → object "Relativ dimmen" (DPT 3.007) → GA "1/0/1 · Wohnzimmer Decke schalten" (1.001) → "Verknüpfen": accepted with only "Verknüpfung angelegt". The same pair via "Verknüpfen" (batch) is refused as unverträglich.
- Expected: both paths apply `WorkbenchProject.AddLinks`/`ValidateLink`, reject atomically, and show which link is `sendend`; or the legacy pane is not offered inside V4 object tabs at all.
- Fix: make the relations representation a thin view over the batch link service (single validation seam) and render the sending flag; keep the old pane only in the compatibility window.

### UX-03 — Monitor shows "Busmonitor" while it records group traffic; "Auswahl folgen" never follows (native + source)

`Starten` always reconfigures the session to `Group`/`Demo`, discarding the mode chosen in the stopped state. The combo keeps displaying "Busmonitor" because it is not re-read. Separately, `OnObjectSelected` never calls `Monitor.SelectionChanged`, so "Auswahl folgen" freezes on whatever was selected when the filter was picked; and "Beobachten" on a device tab pins the whole project.

Observed:

```
monitor mode after choosing Bus: Bus
monitor mode after Starten:     Group
monitor running:                True
monitor mode combo shown:       1  (Busmonitor)
```

- Files: `src/Knx.App/WorkbenchWindow.axaml.cs:723-743` (Start always `Configure(Group, …)`), `:745-773` (filter mapping), `:567-575` (selection handler), `:1046-1056` (Watch maps device → Project), `:34-35,179,747` (`_monitorScope` never updated after a choice).
- Repro: Monitor → "Stoppen" → Monitorart "Busmonitor" → "Starten". Repro 2: open a device tab, select object #0, Monitor → "Auswahl folgen", select object #1 → display unchanged.
- Expected: starting uses the selected mode (or the control is disabled/reverted); filter label always equals the active filter; follow mode re-pins on every selection; "Beobachten" on a device pins that device's objects.
- Fix: store the selected mode/filter on the workspace (not in control locals); call `SelectionChanged(SelectedComObjectId())` from `OnObjectSelected`; map `device` to `ComObject` scope.

### UX-04 — Parameter drafts are silently discarded by project undo and demo edits, with no draft marker (source)

`Undo`/`Redo` replace the `Project` instance (`MainWindowViewModel.cs:335-356`). `ParameterEditingSession.Load` clears all drafts whenever `_project` is not the current project (`ParameterEditingSession.cs:73-80`). `RefreshWorkspace` calls `_parameters.Refresh()` on the new project, so the drafts disappear without prompt. Demo-mode parameter controls are rebuilt from `ParameterValues` on every `RefreshEditor`, so typing a value then filtering/selecting/tab-switching silently resets it. Nothing on the tab, rail or window body indicates unapplied drafts; only `SaveAsync` writes one status line.

- Files: `src/Knx.App/Services/ParameterEditingSession.cs:71-112`, `src/Knx.App/WorkbenchWindow.axaml.cs:299-304,393-480,870-960`, `src/Knx.App/MainWindowViewModel.cs:335-356`, `src/Knx.App/WorkbenchWindow.Commands.cs:116`.
- Repro: device tab → Parameter → change "Entprellzeit" → `Ctrl+Z` (project undo of an earlier unrelated edit) → draft gone. Repro 2: Parameter (demo) → change "Entprellzeit" → type in "Objekte filtern" or switch to another tab and back → the value is back to the stored one.
- Expected: drafts survive project changes or are confirmed before discarding; a visible draft marker (tab dot/toolbar) and an explicit "Verwerfen".
- Fix: key drafts by device ID rather than instance identity, keep them across undo (signature check already exists), and add a dirty/draft indicator to the tab header.

## P2 — friction, misleading state, broken keyboard/accessibility paths

### UX-05 — Object tabs are buttons, not tabs; keyboard focus is destroyed on every tab activation (native + source)

`RefreshTabs` builds `Button`s (`wb-doc-tab`), not `TabItem`s; the V4 `TabItem.wb-tab` styles in the theme are unused (`doc tab class buttons: 0`, representation peer type `Button`). There is no selected-state automation, no roving focus/arrow model, and `Workspace.PropertyChanged → RefreshWorkspace → RefreshTabs()` clears and recreates the headers on every activation or close, removing the focused element.

- Files: `src/Knx.App/WorkbenchWindow.axaml.cs:105-118,249-252,356-370`, `src/Knx.App/Workbench/WorkbenchTheme.axaml:210-253` (dead styles), `WorkbenchWindow.axaml:62`.
- Repro: focus a document tab with Tab/Shift+Tab, press Enter; then press Tab — focus restarts at the top of the window. With a screen reader the tab is announced as "button", not "tab, selected".
- Expected: native `TabControl`/`TabItem` semantics or explicit `AutomationProperties.ItemStatus`/`SelectionItem` pattern, and focus restored to the newly active tab after activation/close.
- Fix: use `TabItem` (theme already defines it) or set `AutomationProperties.Name` + `ItemStatus` and keep header controls stable (update classes instead of rebuilding), then restore logical focus.

### UX-06 — Lower tool tabs collide (native, visual)

Measured gaps between "Monitor", "Programmierung", "Prüfmeldungen" are 0 px; the rendered strip reads `MonitorProgrammierungPrüfmeldungen` (also visible in `demomonitor.png`/`demoprogramming.png`).

- Files: `src/Knx.App/WorkbenchWindow.axaml:101` (`ToolTabs` has no `Spacing`), `:112-118` (buttons).
- Repro: open Monitor.
- Expected: at least 12 px between labels (the reference uses a 22 px view-tab gap, tabs visibly separated), active underline only under one label.
- Fix: `Spacing="12"` on `ToolTabs` (or per-button `Margin`), then add a geometry assertion to the workbench selftest.

### UX-07 — Command palette cannot be driven by the keyboard and has no label (native)

`ShowPalette` keeps focus in the query box and never handles Up/Down; `Down` leaves `SelectedIndex` at 0, so Enter always runs the first filtered entry. Search box and result list have no accessible name.

```
palette search automation name: ""
palette list automation name:   ""
palette selected index after Down: 0
```

- Files: `src/Knx.App/WorkbenchWindow.Commands.cs:235-269` (esp. `:259` reset to 0, `:266-268` only DoubleTapped/Escape/Enter).
- Repro: `Ctrl+K`, type "mon", press Down twice, Enter → "Monitor"? No: the first entry (project or room) opens.
- Expected: Up/Down move the selection, Enter activates it, Home/End work, the field is labelled "Arbeitsgegenstand oder Befehl suchen".
- Fix: handle `Key.Down/Up` on the dialog (move `list.SelectedIndex`) or move focus to the list while keeping the filter editable; set `AutomationProperties.Name` on field and list.

### UX-08 — Batch-link dialog ignores Escape and starts without focus (native)

At open, `FocusManager` has no focused element, so the first Tab starts at the top of the dialog instead of the GA picker; Escape does not close it, contrary to KONZEPT ("Escape schließt die aktuelle temporäre Oberfläche").

```
link dialog focused: <null>
link dialog buttons: Zuordnen | Abbrechen
link dialog open after Escape: True
```

- Files: `src/Knx.App/WorkbenchWindow.Commands.cs:271-338` (no key handling on `dialog`, no `Opened` focus), `src/Knx.App/WorkbenchWindow.axaml:73-80` (no Escape policy).
- Repro: select one object → "Weitere Zuordnungen …" → press Escape.
- Expected: Escape = Abbrechen, initial focus on the GA picker, default = "Zuordnen".
- Fix: set `IsCancel` on Abbrechen, focus the picker in `Opened`, and keep a single dialog action order across the app (see UX-33).

### UX-09 — Empty/loading states misstate what happened (native + source)

- Monitor: "Noch keine Telegramme für diesen Filter." is shown before recording has ever started (`demomonitor.png`); there is no distinction between "not started", "running with no match" and "stopped"; the "● Aufzeichnung läuft · N vollständig" counter is only refreshed on button clicks, not when telegrams arrive (`RefreshState` is never subscribed to the collection).
- Programming: empty queue renders a blank list with no hint; "Ausführen" and "Fehlgeschlagene wiederholen" stay enabled with nothing to do (native: both `True`).
- The object tab representation "Prüfung" silently runs the whole project check on every entry (`CreateProblemsView` → `RunCheck`) and presents project-wide findings under an object-scoped tab. There is no "not run yet" state and no scope label.

Files: `WorkbenchWindow.axaml.cs:699-705,713-722,788-811,817-860,457,695`.

- Repro: open Monitor (never started) → text says "Noch keine Telegramme für diesen Filter."; open Programmierung with an empty queue → blank pane, both action buttons enabled.
- Expected: "Aufzeichnung nicht gestartet", "Keine Aufträge", "Prüfung noch nicht ausgeführt" plus one clear action; check runs only on explicit request and states its scope.
- Fix: state-driven empty text; disable Execute/Retry from `Jobs.Count`/failed count and `IsRunning`; add a "Projekt prüfen" button instead of running on creation.

### UX-10 — Narrow layout hides the group-address column and clips the perspective labels (native + visual)

At 900 DIP the table viewport is 638 px while `TableHost` needs 668 px (30 px horizontal overflow); at 1024 the merged "Gruppenadressen" header and values are cut at the right edge (`demo-small.png`). The three perspective buttons are each 63 px wide in a 210 px navigator; "Topologie" and "Gruppen" render as "Topolo…"/"Gruppe…" (visible in `demo-small.png`, `demoinspector-small.png`).

```
900 table viewport: 638
900 table host:     620 (+24 px margins each side)
900 horizontal overflow: 30
900 perspective: Topologie 63x30
```

- Files: `src/Knx.App/WorkbenchWindow.axaml:47-62,75-93`; `src/Knx.App/WorkbenchWindow.axaml.cs:980-1006` (`narrow < 1100 → 210`), `:85-93` (perspective `UniformGrid Columns="3"` with no minimum).
- Expected: at ≥900 DIP the GA column is visible without horizontal scrolling (or its truncation is signalled), and perspective labels are not clipped (icons, wrapping or shorter labels).
- Fix: make the GA column the flexible one, shrink DPT/Länge/Flags at narrow widths, and give the perspectives a two-row or icon+label layout below ~1100.

### UX-11 — Contrast below AA for small text and for hard-coded dialog colors (computed)

| Pair | Ratio | Where |
|---|---|---|
| `WbSubtle #8a94a5` on `WbPaper #ffffff` | **3.06** | nav meta 9 px (`BuildNavRow`), object address 10 px, "Keine Zuordnung" |
| `WbSubtle` on `WbPanel #f9fafb` | **2.93** | nav tree background |
| `WbMuted #687487` on `WbTabbar #f1f3f6` | **4.26** | muted labels on the tab strip (theme rule; current shell uses buttons, so partly latent) |
| `WbAccent #087e81` on `WbTabbar` | **4.38** | active representation label (12 px) |
| `WbAccent` on `WbTint #e4f2f1` | **4.24** | active perspective label |
| `#b45054` on `WbPaper` dark `#202833` | **2.98** | batch-dialog error text (`Commands.cs:316`) |
| `#268175` on `WbPaper` dark | **3.17** | batch-dialog success text |

- Files: `src/Knx.App/Workbench/WorkbenchTheme.axaml:56-74`, `WorkbenchWindow.axaml.cs:496,518,530`, `WorkbenchWindow.axaml:16-19`, `WorkbenchWindow.Commands.cs:316`.
- Repro: light theme, tree addresses; or open the batch dialog in dark theme with an incompatible selection.
- Expected: ≥4.5:1 for 9–12 px text; semantic `WbError`/`WbSuccess` instead of literals.
- Fix: darken `WbSubtle` to ~#5f6b7c, use `WbMuted` for tab labels, darken `WbAccent` a step for light surfaces, and replace the literals with `DynamicResource`.

### UX-12 — Icon-only controls without accessible names (native + source)

- Tool float/maximize/hide: `↗`, `□`, `×` have tooltips only; `AutomationProperties.GetName` is `""` (`WorkbenchWindow.axaml:102-104`).
- Inspector close `×` (`:111`), help `?` (`:49`) and expand-all `↕` (`:80`) have no automation name; the `?` is announced as "?".
- The navigator/table trees put a focusable caret `Button` inside every row (22 px wide), so Tab traversal needs two stops per row and the caret is smaller than the 24×24 WCAG 2.2 target.
- Table headers are plain `TextBlock`s, so screen readers cannot associate "DPT"/"Länge"/"Flags" with cells; search fields rely on placeholder text only (native: `NavSearch`/`ObjectSearch`/palette field all `automation=''`).

Files: `WorkbenchWindow.axaml:49,80,90,101-104,111`, `WorkbenchWindow.axaml.cs:482-542` (caret `:486-494`), `:1105-1114` (`ActionButton` only sets a name when passed).

Fix: name each icon button (it already has the wording in the tooltip), promote table headers to `AutomationProperties.HeadingLevel` or a grid/list semantic, add `AutomationProperties.Name` to search fields, and make the row caret non-focusable (`Focusable="False"`) with Space/Enter on the row toggling it.

### UX-13 — Simulation and real bus actions are not consistently distinguished (native + visual)

- Monitor runs `SessionOrigin.Demo` and labels itself "● Aufzeichnung läuft" with no "Simulation"/"Demo" marker and no origin column, although every row carries `Origin` (`MonitorSession.cs:12-14`); the status bar simultaneously says "Lokal · keine Busverbindung" (`WorkbenchWindow.axaml:118`).
- The primary top-bar action is labelled "Programmieren" (`:43`); only the tool body says "SIMULATION · kein realer Download" (`WorkbenchWindow.axaml.cs:853`).
- "OFFLINE" (`:42`) is a static text; the product shell has no connect/disconnect control, so the state can never change.

- Repro: Monitor → Starten; the screen shows live-looking telegram rows without a simulation badge.
- Expected: every simulated surface carries a persistent "SIMULATION" badge and an origin column; the top action says "Programmieren (Simulation)"; connection state is bound, not hard-coded.
- Fix: add an origin column and a session badge from `Monitor.Origin`; bind the top-bar state to the bus state; relabel the primary action.

### UX-14 — Documented product actions are unreachable from the product shell (source + visual)

`docs/manual.md` and the bundle describe bus discovery/connect/record, project compare, product replacement, smart linking, GA CSV import and Turtle/JSON-LD/label exports. The Workbench menu only offers Projekt/Bearbeiten/Ansicht/Werkzeuge with a subset (`WorkbenchWindow.Commands.cs:19-57`); the legacy `MainWindow` is compatibility-only (`docs/manual.md:99-100`). The menu item "Aufzeichnung exportieren …" is bound to the legacy `ViewModel.Recorder`, which the Workbench monitor never feeds, so it always reports "Keine Aufzeichnung vorhanden" even while the monitor is recording (`Commands.cs:166-171` vs `:774-775`).

- Repro: open the menu bar; no Bus menu. Start the Workbench monitor, then Werkzeuge → "Aufzeichnung exportieren …".
- Expected: product surface exposes the documented flows (or the manual drops them); recording export uses `MonitorSession.ExportFullCsv`/`ExportFilteredCsv`.
- Fix: either port the actions into the Workbench menus or mark them as compatibility-only in the manual; wire the export to the Workbench session.

### UX-15 — US07 (Programmierbedarf) is not surfaced anywhere (source)

`ProgrammingSession.NeedsProgramming`/`Downloaded` exist and are tested, but neither the device tab, the inspector, nor the programming list shows "Programmierbedarf"/"zuletzt simuliert geladen". A simulated success is only visible as a job row; after closing the tool there is no trace of the comparison state.

- Files: `src/Knx.App/Workbench/Sessions/ProgrammingSession.cs:83-98,156-183`; no call site in `src/Knx.App` (grep `NeedsProgramming`/`Downloaded` is clean outside the session/tests).
- Repro: queue and execute a job, switch to another device tab → no indication whether it needs programming.
- Expected: a per-device state chip in the table/inspector and a queue column "Abweichung zum letzten simulierten Download".
- Fix: bind `NeedsProgramming(device.Id)` per row and expose `Downloaded` in the inspector; add it to the selftest.

### UX-16 — The room option "externe Kanäle einbeziehen" has no control (source)

US02/A03 require the linked external channels only "bei aktivierter Option". `WorkbenchTab.IncludeRelated` defaults to `true`, is persisted and restored (tests cover it), and is used by `WorkbenchProjection.Rows` — but no view ever sets it: the object toolbar only has "Ohne GA", filter and expand-all.

- Files: `src/Knx.App/Workbench/WorkbenchWorkspace.cs:28`, `src/Knx.App/Workbench/WorkbenchProjection.cs:44`, `src/Knx.App/WorkbenchWindow.axaml:75-81`, `WorkbenchWindow.axaml.cs:562-566`.
- Repro: open "Wohnzimmer"; the external dimmer channel appears with no way to switch it off.
- Expected: a checkbox "Externe Kanäle" on room tabs that flips `IncludeRelated` and the subtitle that already says "Einbauort und relevante externe Kanäle getrennt".
- Fix: add the toggle next to "Ohne GA" for `tab.Target.Kind == "room"`.

### UX-17 — Error feedback is a 10 px status line that is overwritten and can hide real failures (source)

All Workbench failures (link validation, monitor start/stop, programming, export, layout) go to `ViewModel.Status` (`WorkbenchWindow.axaml.cs:628,734,771,829-851,957-978`; `WorkbenchWindow.Commands.cs:156-171,222-216`) and are rendered as one 10 px muted, ellipsized line (`WorkbenchWindow.axaml:117`). `MonitorSession.LastError` (disk write failure stops recording, `MonitorSession.cs:189-201`) is never rendered. `False` results (`SetRepresentation` without a tab, `RunAsync` with an empty queue) produce no feedback at all.

- Repro: start the monitor when the interface is "occupied" (or force a CSV write failure) → the session stops; the UI shows nothing but the next status change.
- Expected: an error surface with severity that persists until acknowledged, and the specific failure reason from the session.
- Fix: render `Status` with severity classes and add a dismissible error strip bound to `Monitor.LastError`/`Programming` failures; disable actions that cannot run instead of silent no-ops.

### UX-18 — English setting leaves the V4 shell in German (source)

Settings offers Deutsch/English and applies `Strings.Language` live (`SettingsWindow.axaml.cs:30-43`). The V4 shell is almost entirely hard-coded German: top bar, rail, perspectives, tabs, table headers, inspector, monitor/programming views, menus, help text and dialogs (`WorkbenchWindow.axaml:36-118`; `WorkbenchWindow.axaml.cs:85-118,297-318,403-476,602-679,699-860,881-957`; `WorkbenchWindow.Commands.cs:19-74,235-338`). Only embedded legacy panes use `Strings.Get`. `TrExtension` resolves once at load (`Localization/TrExtension.cs:9-21`), contradicting the "switches live" comment in `Localization.cs:9,17`.

- Repro: Optionen → English → close; tabs/menus stay German, the catalogue switches to English.
- Expected: one language for the whole surface after the change (or a restart notice before applying).
- Fix: move shell strings into `Strings` and bind with a live mechanism (or document a restart requirement and apply it consistently).

### UX-19 — Start experience and recent list differ from the documented start screen (visual + source)

With no project, the Workbench editor shows three plain buttons ("Projekt öffnen …", "Neues Projekt …") followed by recent *names* only (`WorkbenchWindow.axaml.cs:468-476`); the documented three cards, folder paths and "Zuletzt geöffnet" list live in the legacy window (`docs/manual.md:38-53`, `MainWindow.axaml:205-263`). There is no project-independent catalogue card; only the rail icon reaches the catalogue.

- Repro: start without a project.
- Expected: either the documented cards or an updated manual; recents should show the folder (the start `ListBox` in the legacy window does).
- Fix: give the start panel the card layout and the recent item template (name + folder), with a catalogue card that sets `StartDismissed`.

### UX-20 — Unsaved/draft prompts miss three loss paths (source)

Prompts exist for open/new/import/close (`WorkbenchWindow.Commands.cs:122-152`). Missing:
1. project undo/redo while drafts exist (UX-04);
2. opening another project while a monitor recording or queued programming job runs — when the project is not dirty, `ConfirmDiscardAsync` returns immediately and `ResetSessionsForReplacementAsync` disposes the sessions and deletes the temp recording (`:83-102`, `MonitorSession.cs:61-71`);
3. closing the window while the monitor records or jobs run: only dirty project/drafts trigger the close prompt (`:132-152`).

- Repro: start the monitor (project unmodified), File → Projekt öffnen → choose any file: no warning, recording gone.
- Expected: warn "Aufzeichnung/Programmieraufträge werden beendet" and offer cancel; keep the session alive until the replacement is confirmed.
- Fix: extend `ConfirmDiscardAsync` with a session check (`Monitor.IsRunning || Jobs.Any(queued/running)`).

## P3 — consistency, polish, smaller accessibility items

| ID | Finding | Evidence |
|---|---|---|
| UX-21 | Menu items are always enabled: Undo/Redo without history, "Zuordnungen bearbeiten"/"Auswahl verknüpfen" without a tab/selection, Save/export without a project; several only write a status line or silently return. | native: `Rückgängig … enabled=True` with empty history; `Commands.cs:35-42`, `WorkbenchWindow.axaml.cs:281-287` |
| UX-22 | Keyboard model incomplete: no `Ctrl+W`/close-tab, no `Ctrl+Shift+Tab` backwards, `Ctrl+Y` (manual §10) is not bound in the shell (only `Ctrl+Shift+Z`), `Escape` closes only the inspector (not Settings, New Project, or the link dialog), and `Ctrl+Tab` appears only in the help dialog, not in menus/tooltips. | `WorkbenchWindow.axaml.cs:1079-1102`; `SettingsWindow.axaml:8-12`; `NewProjectWindow.axaml:10-11`; `Commands.cs:271-338`; `docs/manual.md:504-518` |
| UX-23 | Terminology drifts: "Gruppen" (rail) vs "Gruppenadressen" (manual), "Beziehungen" (tab) vs "Kommunikationsobjekte", "Prüfung" (tab) vs "Prüfmeldungen" (tool), "Ausschnitt"/"Projektstand" (subtitle) vs "Arbeitsgegenstand"; job rows print `QUEUED/DONE/FAILED` and operation `full` inside a German UI. | `WorkbenchWindow.axaml:79,85`; `WorkbenchWindow.axaml.cs:414-415,856-857`; `WorkbenchProjection.cs:89-92` |
| UX-24 | Target sizes: row caret button is 22 px wide, row height 34 but nav rows 29, disabled roots use opacity .46; the caret is the only way to expand with the mouse. | native geometry; `WorkbenchWindow.axaml.cs:485-494`; `WorkbenchTheme.axaml:319-332` |
| UX-25 | Labels are placeholders/siblings, not labels: "Bezeichnung" above the inspector field is a `TextBlock`, parameter labels are not associated with their editor, "Name/Adresse" filters have no accessible name. | `WorkbenchWindow.axaml.cs:657-658`; `ParameterPane.axaml:50-58`; native (`automation=''`) |
| UX-26 | Floating tool window has no theme/background, no automation name, and "close the window to redock" is undiscoverable. | `WorkbenchWindow.axaml.cs:1062-1078`; screenshot of a floated tool not captured; source |
| UX-27 | `ToolsHeight` is restored up to 1000 px without clamping to the window, and `ToolsMaximized` is not persisted; a restored height larger than the window can leave zero editor space (the reference caps tools at 60 %). | `WorkbenchWorkspace.cs:118,155`; `WorkbenchWindow.axaml.cs:1007-1016` |
| UX-28 | Scaling: `MinHeight=600` DIP needs 1200 physical px at 200 %, i.e. more than a 1080p work area; there is no outer scroll/fit strategy. No 150/200 % native acceptance exists. | `WorkbenchWindow.axaml:4`; bundle requirement A07; source |
| UX-29 | Dirty state only appears as `*` in the OS title; the status bar project label and tabs show nothing. | `MainWindowViewModel.cs:54-59,86-93`; screenshot `demo.png` |
| UX-30 | Navigator search placeholder promises "Objekt", but only node name/meta are matched; communication objects are not navigator nodes. | `WorkbenchWindow.axaml:55`; `WorkbenchProjection.cs:152-167` |
| UX-31 | "In Topologie zeigen" only navigates and writes the search field; it does not select/reveal the device or switch its tab, and palette-opened objects never sync the navigator selection. | `WorkbenchWindow.axaml.cs:667,263-272` |
| UX-32 | "Vor dem Löschen nachfragen" is still offered although the Workbench surface has no delete action at all (device/GA delete only in the compatibility window); the manual already flags the setting as ineffective. | `SettingsWindow.axaml:30-31`; `MainWindow.axaml.cs:646-688`; `docs/manual.md:545-546` |
| UX-33 | Dialog action convention differs from the reference in every native dialog: the reference puts secondary actions left and the primary action right (`app.js:409,423,459`), while `NewProjectWindow`, `SettingsWindow`, `ReplaceProductWindow`, `CompareWindow`, `ConfirmDialog` and the link dialog put the primary first/left. Cancel is not `IsCancel` in Settings/NewProject/link dialog; only `ConfirmDialog` has explicit default/cancel buttons. | `NewProjectWindow.axaml:8-12`; `SettingsWindow.axaml:8-12`; `ConfirmDialog.cs:46-57`; `WorkbenchWindow.Commands.cs:292-295`; bundle `src/app.js` |

## Manual and acceptance-document corrections (needed regardless of fixes)

| Place | Claim | Reality at this SHA |
|---|---|---|
| `docs/manual.md:3` | "geprüft gegen 77c78f9" | stale; UI is the V4 Workbench, many described surfaces no longer reachable |
| `docs/manual.md:38-53` | start cards, recent list with folders | plain button list, names only (`UX-19`) |
| `docs/manual.md:301-349` | Bus menu, discover/connect, Recorder tab with 2000-row monitor | no Bus menu, no recorder in the Workbench; monitor keeps 500 display rows, recording is a hidden CSV |
| `docs/manual.md:420-426` | `Strg+Y`, `Entf`, `F2`, 60 undo steps | not bound in the Workbench; 60 steps still true for the shared stack |
| `docs/manual.md:447-465` | Turtle/JSON-LD/label exports | not in the Workbench menu |
| `docs/manual.md:477-497` | compare / replace product | not in the Workbench menu |
| `docs/manual.md:74-100` | workbench description | accurate in structure, but omits the simulation-only monitor, missing delete, and draft behaviour |
| `analysis/workbench-v4-acceptance.md` | "A06 control-free monitor/programming" | true for lifetime; mode/follow defects UX-03 and missing Programmierbedarf UX-15 are not covered |
| `docs/manual.md:571` | "visuelle/interaktive Abnahme … keine Screenshot- oder Klicktests" | screenshots and routed selftests now exist; acceptance text should be updated |

## Top fixes, in order

1. **Accessible row names** (UX-01) — one display property plus a selftest assertion; largest accessibility win for the core tables.
2. **Single link-validation seam** (UX-02) — remove the legacy pane from the V4 relations tab or route it through `WorkbenchProject.AddLinks`; show sending.
3. **Monitor mode/follow correctness** (UX-03) — persist the chosen mode/filter, start with it, update follow on selection, map device scope.
4. **Draft lifetime** (UX-04/UX-20) — drafts survive undo, a visible draft marker, and prompts for undo/project replacement/close while sessions run.
5. **Native tab semantics and stable headers** (UX-05) — fixes keyboard focus loss and screen-reader role in one change.
6. **Tool-tab spacing and icon-button names** (UX-06/UX-12) — two one-liners visible in every screenshot.
7. **Palette keyboard model and dialog conventions** (UX-07/UX-08) — Up/Down + Escape + primary-last.
8. **State-driven empty texts and disabled actions** (UX-09/UX-17) — stop claiming "no telegrams" before start and "recording" without a reason.
9. **Contrast tokens** (UX-11) — palette values, no literals in dialogs.
10. **Doc truth-up** (manual sections above) — users currently follow paths that do not exist in the product shell.

## Explicitly verified as working

- Object-tab continuity for filter, selection, unlinked-only, collapsed devices and scroll offset, including across representation switches and tab switches (routed selftest).
- Layout save/restore rejects malformed/obsolete files; no project mutation.
- `Ctrl+Z`/`Ctrl+Shift+Z` correctly defer to a focused `TextBox` (project undo only when not editing); native editor undo is preserved (source-traced; focus could not be exercised in the harness).
- Atomic batch links, duplicate no-op, explicit sending, retained links for inactive objects, one undo step (domain tests and selftest).
- Monitor and programming sessions survive hiding, tool-window float/redock and tab switches; 500-row display tail separate from the full recording; retry keeps the frozen payload.
- Virtualization: 10 027 rows realized with 17 containers; header/cell column alignment within 1 px.
- Draft session survives representation switch; unsaved-project prompt exists for open/new/import/close.
- Batch dialog blocks apply on any incompatible member and explains "es wird nichts teilweise übernommen".
- Icon rail buttons, tab close buttons, caret buttons and the palette item labels have usable names/tooltips (the remaining gaps are listed above).
- Theme key parity and opt-in scoping (theme tests); light/dark switching works live.

## Limitations of this review

- Keyboard focus assertions could not be executed reliably in the harness: the probe window never received OS activation (`FocusManager` returned `null`), so focus-loss claims (UX-05, UX-08 initial focus) rest on the code path that rebuilds/opens the control, not on an observed focus ring. The role/name and geometry results are runtime-observed.
- No OS screen reader, 150/200 % scaling session or real mouse automation was run; those remain source/computed findings.
- No real bus, router or private catalogue was touched; all native runs used the synthetic demo or the isolated temp catalogue.
- Contrast figures are arithmetic (WCAG sRGB), not photometer or rendering measurements; font weight/antialiasing may shift perceived readability but not the ratios.
- The external probe lives in `%TEMP%\opencode\uxprobe` and is intentionally not committed; it references the built app assembly and changes nothing in the repository.
