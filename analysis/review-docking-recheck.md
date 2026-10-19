# Docking/workspace recheck — E2

## Verdict

**Not ready for general docking use.** Real-window integration runs reproduced two crash paths, silent parameter-draft loss, and a keyboard-command gap in floated documents. Default workspace selection and the tested float-X/main-X shutdown paths work. No application source was changed.

- Base: `f81c90e59a4b554eda07e360e12ef6df1c591687` (verified before worktree creation and after testing).
- Branch: `agents/etscheck/e2-validation`.
- Worktree: `C:\projects\ai_work\agent-wt\etscheck-e2`.
- Evidence root (all paths below relative to this): `C:\Users\julia\AppData\Local\Temp\opencode\etscheck-e2`.
- Read `FEATURE.md`, all three files in `plans/`, and applicable workspace/global instructions. No repository-local `AGENTS.md` was found.

## Method and limits

Built the unmodified integration and exercised its actual `Knx.App.App`, `MainWindow`, Dock factory, desktop lifetime and native floating windows, using a temporary C# harness referencing the App project. Screenshots are actual Avalonia `RenderTargetBitmap` renders, not mockups or source-only conclusions. They omit OS title bars.

The harness selects real `TreeView` items, edits bound parameter row models, invokes Dock's factory operations, raises routed keyboard/click events, and calls actual `Window.Close()`. **This is real UI integration observation, not an end-to-end mouse/physical-key test.** Native title-bar clicking, pointer drag/drop hit testing, Tab traversal, multiple monitors and DPI changes were not validated. Explicit cross-window move findings below require a follow-up pointer-drop reproduction; do not equate a factory-call failure with a verified drag gesture.

Only a generated two-device project with one embedded synthetic application XML and one group address was used. `BUSCHORD_SETTINGS` points to an existing `{}` file in the evidence root. The harness replaces the shared catalogue store with an empty temporary store before creating the app; it does not inspect private catalogue contents. Production parameter/link screenshots use their built-in in-memory synthetic demo, never the catalogue screen. No discovery/connect/send/download commands were executed; no router was accessed. Only processes launched for this validation exited/crashed; an existing BusChord process was not stopped.

## Prioritized defects

### P1 — Floating either tool crashes the application

**Runtime reproduced independently for both tools, from a fresh layout.**

1. Load the synthetic project in the normal window.
2. Invoke the current factory's `FloatDockable(propertiesTool)` or `FloatDockable(topologyTool)`.
3. The process terminates on the next layout pass with exit `-532462766`:
   - `InvalidOperationException: The control Border (Name = PropertiesPanel) already has a visual parent ContentPresenter ... while trying to add it as a child of ContentPresenter ...`
   - Same error naming `TopologyPanel` for Topology.

Consequently tool redock and tool-float X could **not** be validated: neither tool survives floating. This also occurred after a successful document float/X/redock, but does not require that earlier sequence.

Relevant ownership/integration sites: `src/Knx.App/MainWindow.axaml.cs:100-125` (existing live controls assigned as Tool contexts), `:173-176` (host creation), and `src/Knx.App/App.axaml:12-17` (Document/Tool templates present those contexts). Stack traces show `Dock.Controls.DeferredContentControl.DeferredContentPresenter.MeasureOverride` and Avalonia visual-parent validation. The evidence establishes duplicate visual attachment, not which application/library change should fix it.

Evidence: `fresh-properties.log`, `fresh-topology.log`, `crash-tool-float.log`, `runtime-crash.log`. Reproducer modes: `E2_TOOL=Properties` and `E2_TOOL=Topology`.

### P1 — Explicit cross-window document move crashes during layout

**Real-window factory-level reproducer; native pointer-drop confirmation remains open.**

1. Load/select Device 1; apply Brightness `42`.
2. Float the communication-object document into a real HostWindow.
3. Exercise floated/main Ctrl+Z as described below, restoring the original parameter value.
4. Invoke `MoveDockable((IDock)links.Owner, originalDocumentDock, links, parameterDocument)` to return the document to the main dock.
5. The process terminates during layout: `ArgumentException: Attempt to call InvalidateArrange on wrong LayoutManager`, exit `-532462766`.

This is separate from float-X redock, which succeeded. There is no application cross-window drop wrapper in the exercised factory path. Relevant integration sites: `src/Knx.App/App.axaml:12-17`, `src/Knx.App/MainWindow.axaml.cs:107-114,173-217`; the X path deliberately stages removal/re-add across dispatcher turns, unlike the direct cross-window factory move. That difference is a debugging lead, not a proven root cause.

Evidence: `extra.log`; `E2_EXTRA=1` (without `E2_NOREDOCK`). Do not claim this as a tested mouse-drop result or infer every document move fails.

### P2 — Changing topology selection silently discards unapplied parameter edits

**Runtime reproduced in the rendered parameter pane.**

1. Select Device 2, open Parameters; Brightness starts at `25`.
2. Change the bound Brightness draft to `41`, without Apply.
3. Select Device 1 in the actual topology TreeView, then select Device 2 again.
4. Brightness is back to `25`; there was no prompt or discarded-draft notice. The project remained clean, so main-window close also did not warn about that abandoned draft.

Relevant code: `src/Knx.App/MainWindow.axaml.cs:865-872` calls `_parameters.Load(device)` on selection changes. `src/Knx.App/Views/ParameterPane.axaml.cs:33-53,100-118` replaces rows and clears `_dirty`/message. Only `Refresh()` (`:59-65`), not selection-driven `Load()`, preserves knowledge that drafts existed and displays the discard notice.

Evidence: `harness-flow.log` (`DRAFT return value=25`), `11-draft-switch.png`, `14-draft-return.png`. The harness changes the same bound row model used by the editor; physical typing was not exercised.

### P2 — Ctrl+Z does not reach project undo from a floating document

**Runtime reproduced with routed keyboard events.**

1. Apply Brightness `42`, enabling project Undo.
2. Float Communication objects and raise Ctrl+Z on that actual HostWindow.
3. Value stays `42`.
4. Raise the identical Ctrl+Z event on MainWindow; value returns to the default.

Relevant code: `src/Knx.App/MainWindow.axaml.cs:72,310-348` attaches project shortcuts only to MainWindow. Floating HostWindows from `:173-176` have no equivalent subscription; their input route does not include MainWindow. Main-window F2 was separately observed to focus the Properties name editor. Other floating shortcuts were not individually tested.

Evidence: `extra.log`, `dirty-close.log` (`FLOAT CTRL Z value=42`, `MAIN CTRL Z value=DEFAULT`). Physical keyboard dispatch remains untested.

## Exercised behavior

| Flow | Observed result / evidence |
|---|---|
| Topology + Properties + selected document | Simultaneously rendered, not one combined tab strip: `01-parameters-selected.png`, `02-tree-device2.png`. Parameters and objects initially share one document dock and switch as tabs. |
| Parameters + objects side-by-side | `SplitToDock(..., Right)` creates separate document owners and both contents render: `04-simultaneous.png`. That split is narrow and clips/truncates content; not certified as an accessible/responsive layout. Native split/drop gesture not tested. |
| Selection Device 1 → Device 2 | TreeView selection updates VM selection, visible Properties NameBox, ParameterPane.Device and GroupLinksPane.Device to Device 2; `02-tree-device2.png`, `harness-flow.log`. |
| Select a line instead of device | Clears selected device and parameter/object device references; Properties shows nothing selected; parameter pane asks for a device: `03-line-selection.png`. |
| Document float → float X/Close → redock | Real HostWindow appears (`05-document-float.png`); Close restores document to recorded original document dock and returns to one window (`06-document-redocked.png`). Rechecked in subsequent runs. |
| Tool float/redock/X | Blocked by tool-floating crash, not passed. |
| Explicit document redock | Crashes in the exercised factory-level sequence; pointer drop untested. |
| Main X/Close with clean project and float | Before close: two windows, `IsDirty=False`; one Close ends the desktop lifetime/process: `harness-flow.log`, exit 0. |
| Main X/Close with dirty project and float | Prompt appears; Cancel leaves main + float visible (two windows). A second Close and the actual dialog's Discard button ends lifetime: `dirty-close.log`, `13-dirty-main-close.png`, exit 0. Save path not tested. |
| Main keyboard F2 | Routed F2 focuses/selects Properties NameBox: `harness-flow.log`, focused box visible in minimum-size shots. |
| Minimum declared client size | At 1180×640 the command groups wrap; Parameters Apply, object Link/Unlink and Properties action buttons remain visible. Properties details scroll and the document strip overflows with a chevron. `09-minimum-links.png`, `10-minimum-parameters.png`. No claim about smaller widths, scaled displays or tab-chevron keyboard accessibility. |
| Production screenshot modes | `parameters.png` and `links.png` inspected: correct selected tab, topology/properties visible, synthetic parameter/object/address/link rows rendered at 1440×900. No bus connection. |

## Commands and results

Run from the worktree unless otherwise stated. PowerShell `$r` denotes the evidence root above. All build/test output is retained there.

```powershell
git rev-parse HEAD
git branch --show-current
dotnet build ets6.sln -v minimal *> "$r\build.log"
$env:BUSCHORD_SETTINGS="$r\settings.json"  # precreated with {}
$env:TEMP=$r; $env:TMP=$r
dotnet .\src\Knx.App\bin\Debug\net9.0\BusChord.dll --smoke *> "$r\smoke.log"
dotnet .\src\Knx.App\bin\Debug\net9.0\BusChord.dll --screen=parameters --screenshot="$r\parameters.png" *> "$r\parameters.log"
dotnet .\src\Knx.App\bin\Debug\net9.0\BusChord.dll --screen=links --screenshot="$r\links.png" *> "$r\links.log"
$p=Start-Process -FilePath .\src\Knx.App\bin\Debug\net9.0\BusChord.exe -ArgumentList '--screenshot-selftest' -Wait -PassThru -RedirectStandardOutput "$r\selftest-out.log" -RedirectStandardError "$r\selftest-err.log"
foreach ($test in @('Knx.ParameterUi.Tests','Knx.Linking.Tests','Knx.CatalogUi.Tests','Knx.Monitor.Tests','Knx.Buildings.Tests')) {
  dotnet run --project "tests\$test" --no-build *> "$r\$test.log"
}
```

- Solution build: success, 0 errors, 5 warnings (`CS8620`, `CS1998`, `CS8602`, two `CS0618`), `build.log`.
- Smoke: 53 PASS, `SMOKE OK`, exit 0.
- Screenshot self-test: six PASS, `SCREENSHOT OK`, exit 0. Valid PNG 1440×900, 39,643 bytes at `buschord-screenshot/main.png`; unknown screen correctly rejected with no PNG. `selftest-out.log` / `selftest-err.log`.
- Parameter UI: 28 PASS; Linking: 31 PASS; Catalog UI: 53 PASS; Monitor: 51 PASS; Buildings: 99 PASS. Every listed regression executable exited 0. These are headless checks, not substitute visual tests. No full networking suite was run.
- Early direct WinExe invocations produced no captured console text; smoke was rerun via `dotnet` and screenshot-selftest via `Start-Process` with explicit stdout/stderr redirection. No result is inferred from the empty early logs.

Temporary harness and fixture: `Harness.csproj`, `Program.cs`, `synthetic.knxproj`; harness-generated project settings contain only that synthetic path. Reproduce each mode in a fresh process:

```powershell
# Normal selection/split/float-X/minimum-size/draft/main-X checks:
Remove-Item Env:E2_TOOL,Env:E2_EXTRA,Env:E2_NOREDOCK -ErrorAction SilentlyContinue
dotnet run --project "$r\Harness.csproj" *> "$r\harness-flow.log"
# Independent fresh tool crash (repeat with Topology):
$env:E2_TOOL='Properties'
dotnet run --project "$r\Harness.csproj" *> "$r\fresh-properties.log"
# Cross-window explicit move + floating/main keyboard comparison:
Remove-Item Env:E2_TOOL -ErrorAction SilentlyContinue
$env:E2_EXTRA='1'
dotnet run --project "$r\Harness.csproj" *> "$r\extra.log"
# Avoid that move crash and exercise dirty prompt Cancel/Discard:
$env:E2_NOREDOCK='1'
dotnet run --project "$r\Harness.csproj" *> "$r\dirty-close.log"
```

The report is the sole tracked change. Temporary evidence/harnesses are intentionally not committed. No fixes, push, router activity, private catalogue publication, or recursive delegation were performed.
