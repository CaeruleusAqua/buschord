# D3: docking crash root-cause investigation

2026-10-19. Base `d473b4a19c604c45bd26a5aea1c520a1455fa727`; branch `agents/etsfix/d3-rootcause`; worktree `C:\projects\ai_work\agent-wt\etsfix-d3`. Investigation only: no product edits, dependency changes, merges, rebases, pushes, exception suppression, or native mouse-gesture tests. Stopped at the bounded investigation limit. Read D1's failed detach/drain experiments; did not repeat them as repairs.

## Conclusion

**Two distinct ownership failures interact.**

1. **Duplicate visual parent: confirmed application presentation-lifetime mismatch.** `App.axaml:12–17` creates new `ContentControl` wrappers around a singleton live control in `Document.Context`/`Tool.Context`. Removing a tool's collapsible source dock can detach the entire old view subtree without releasing its internal parent-child links. A new wrapper then tries to adopt the same inner control. Being detached from a window is not equivalent to having no visual parent. Reproduced without BusChord, including direct observation of the detached, still-parented control.
2. **Wrong LayoutManager: confirmed stale per-root work in Avalonia 11.3.12, exposed by moving live view subtrees.** The throwing floating window's pending arrange-after-measure list contains `TextPresenter` instances now attached to the main window. Source and installed DLL agree that this list is resubmitted to the old manager without checking current root. This is a framework queue ownership weakness, not proof that Dock's model collections have two owners. Its exact minimal timing trigger is not isolated: a small standalone transfer succeeds. Thus a universal Dock/Avalonia version incompatibility is **not** established.

Both behaviors depend on retaining/reparenting live visuals. Fresh presentation controls avoid the tested failure paths without upgrading packages. They are an experimentally supported architectural seam, **not a production patch or full interaction regression result**.

## Reproduction before theory

Private diagnostic area: `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-d3` (abbreviated `$r`). Copied only original E2 `Program.cs`/`Harness.csproj`, redirected project reference and all generated artifacts/settings to D3. Original harness and other workers were not edited. Product source remained unchanged. Harness later explicitly limits Compile items to Program.cs because nested diagnostic projects otherwise enter SDK globs; the downloaded source was moved to sibling `etsfix-d3-source` to exclude XAML resource globs.

Run each original-repro command in a fresh PowerShell environment:

```powershell
$r='C:\Users\julia\AppData\Local\Temp\opencode\etsfix-d3'
$env:E2_TOOL='Topology'
dotnet run --project "$r\Harness.csproj"
# baseline-topology.log: unhandled InvalidOperationException, TopologyPanel already has a visual parent
```

```powershell
$r='C:\Users\julia\AppData\Local\Temp\opencode\etsfix-d3'
$env:E2_EXTRA='1'
dotnet run --no-build --project "$r\Harness.csproj"
# baseline-move.log: unhandled ArgumentException, InvalidateArrange on wrong LayoutManager
```

Also ran `E2_TOOL=Properties`: `baseline-properties.log` contains the corresponding PropertiesPanel duplicate-parent stack. D1 had observed timing-dependent success for Properties; this run failed. Some shell waits timed out after the exception had already been captured; the exception, not the shell timeout, is the failure evidence.

### Exact stack paths

Tool float: `Factory.FloatDockable` schedules/model-transfers content; the later render/layout pass throws through:

`MediaContext.Render → LayoutManager.ExecuteQueuedLayoutPass → ExecuteMeasurePass → Measure → DeferredContentPresenter.MeasureOverride → ContentPresenter.ApplyTemplate → UpdateChild → AvaloniaList<Visual>.Add → Visual.IAvaloniaListItemValidator.Validate`.

The exception names two different `PART_ContentPresenter` instances hosting `ContentControl`, with the same `TopologyPanel`/`PropertiesPanel` as child. The float API is no longer on the stack because presentation happens later.

Explicit move: after `Factory.MoveDockable` returns, the queued render/layout pass throws through:

`MediaContext.Render → LayoutManager.ExecuteQueuedLayoutPass → ExecuteLayoutPass → InnerLayoutPass → ExecuteArrangePass → InvalidateArrange`.

## Ranked falsifiable hypotheses and results

Ranking was stated before standalone testing: (1) retained singleton content under old wrappers, (2) stale root-specific layout queues, (3) Dock model/template contract misuse. The following predictions separate them.

| Hypothesis | Prediction | Evidence / confidence |
|---|---|---|
| Shared live control survives old wrapper teardown | A plain Tool model and shared Border reproduce; fresh view per template build does not | Confirmed, high. `collapse-live-tool.log`, `collapse-fresh-tool.log`, `mini-parent-tool.log` |
| Collapsible source dock lifetime matters | Keeping source ToolDock non-collapsible prevents the standalone float crash | Confirmed for this float scenario, high. `collapse-live-tool-nocollapse.log`; not a general fix or multi-cycle claim |
| Old LayoutManager retains transferred controls | At first-chance throw, old manager has pending controls whose current root is different | Confirmed, high. `instrumented-move.log`; three TextPresenters, HostWindow manager versus MainWindow actual root |
| Intrinsic failure for ordinary fresh-view Dock transfers / mismatched typed models | Fresh-view tool/document transfers also fail | Not supported by tested standalone cases; all pass. Does not prove every framework path correct |
| Duplicate IDs / tool-versus-document contract is the primary float cause | Unique IDs and ordinary Tool/Document models remove crash | Falsified as a necessary cause: standalone uses unique Tool/Tools IDs and still fails when collapsible |

## Standalone minimization (no BusChord reference)

`$r\mini\Mini.csproj` references only installed Avalonia Desktop/Fluent **11.3.12**, Dock Avalonia/Fluent/Mvvm **11.3.12.1**. `Program.cs` creates a native Window, Factory, RootDock, horizontal ProportionalDock, ToolDock and DocumentDock; HostWindowLocator returns ordinary HostWindow. There are no custom closing handlers, project model, application styles, BusChord services or keyboard handlers.

Essential failing contract:

```csharp
var content = new Border { Child = new TextBox { Text = "shared text" } };
var tool = new Tool { Id = "Tool", Context = content };
// ToolDock Id = "Tools", normal default IsCollapsable = true.
// App template creates a new ContentControl bound to the Tool.Context:
var wrapper = new ContentControl();
wrapper.Bind(ContentControl.ContentProperty, new Binding("Context"));
// After initial native-window layout:
factory.FloatDockable(tool);
```

Actual diagnostic output immediately before failed second presentation:

```text
FLOAT
BUILD Tool live-tool
CONTEXT parent=ContentPresenter root= attached=False
Unhandled exception ... Border already has a visual parent ContentPresenter ...
```

This conclusively distinguishes retained **internal visual ownership** from simultaneous attachment to two native windows.

Commands (final harness accepts these modes):

```powershell
$env:D3_MODE='live-tool' # duplicate-parent failure
dotnet run --project "$r\mini\Mini.csproj"
$env:D3_MODE='fresh-tool' # fresh Border/TextBox per build: PASS
dotnet run --no-build --project "$r\mini\Mini.csproj"
$env:D3_MODE='live-tool-nocollapse' # same singleton, no source collapse: PASS
dotnet run --no-build --project "$r\mini\Mini.csproj"
$env:D3_MODE='live-document-move' # Float then direct MoveDockable: PASS
dotnet run --no-build --project "$r\mini\Mini.csproj"
$env:D3_MODE='fresh-document-move' # PASS
dotnet run --no-build --project "$r\mini\Mini.csproj"
```

Other exact runs: `fresh-tool-move` passed; `live-document-list-move` with 200 ListBox items passed; `live-document-queued-move`, explicitly invalidating live descendant measure/arrange before transfer, passed. Thus merely having pending invalidations is insufficient to reproduce the second failure. Negative results are retained, not represented as coverage of the failing application layout.

**Template-contract correction during minimization:** initial FuncDataTemplate probes used `supportsRecycling:false`, which is not equivalent to XAML DataTemplate. Avalonia XAML DataTemplate implements IRecyclingDataTemplate and returns `existing ?? loadedTemplate`. Corrected probes use `supportsRecycling:true`; the collapsible-tool failure remains. Initial `mini-live-*` logs precede this correction; `recycle-*`, `collapse-*`, `mini-parent-tool.log` and later runs use the corrected contract. Non-recycling standalone live transfers can additionally fail duplicate-parent; these are not evidence that every document transfer crashes.

## Temporal ownership and installed implementation

Public source checked at the **installed Dock package repository commit** `f891bdb589411a26439261e29ae6f321f5f0e234` from its `.nuspec`, not current main. Downloaded read-only clone at `$r-source` (full path `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-d3-source`). NuGet dependencies explicitly target Avalonia 11.3.12, so there is no observed accidental major/minor mismatch.

- [Dock FactoryBase.Dockable.cs](https://github.com/wieslawsoltes/Dock/blob/f891bdb589411a26439261e29ae6f321f5f0e234/src/Dock.Model/FactoryBase.Dockable.cs): `FloatDockable` lines 1001–1042 delegates to SplitToWindow. Cross-owner `MoveDockable`, lines 347–358, synchronously removes from source, inserts into target, initializes owner and activates target. Collection ownership updates do not imply all deferred presenters/layout queues have finished.
- [ToolControl theme](https://github.com/wieslawsoltes/Dock/blob/f891bdb589411a26439261e29ae6f321f5f0e234/src/Dock.Avalonia.Themes.Fluent/Controls/ToolControl.axaml), lines 56–64: active dockable goes through DeferredContentControl and ControlRecyclingDataTemplate. An ordinary typed Tool/Document plus application data template is supported; there is no evidence a different model base class is required.
- [ControlRecycling](https://github.com/wieslawsoltes/Dock/blob/f891bdb589411a26439261e29ae6f321f5f0e234/src/Dock.Controls.Recycling/ControlRecycling.cs), lines 80–118: detaches the **template result** when reusing it. BusChord's template result is the outer ContentControl, not the singleton nested Context control. A new outer result can therefore still contain content retained by another result. Caching is an optimization, not a safe singleton-view ownership contract.
- [DeferredContentControl](https://github.com/wieslawsoltes/Dock/blob/f891bdb589411a26439261e29ae6f321f5f0e234/src/Dock.Controls.DeferredContentControl/DeferredContentControl.cs), lines 346–350 and 525–529: detach removes scheduled presentation, but does not promise recursively clearing every child ContentControl.Content. Detached subtrees legally retain their children.
- [Avalonia XAML DataTemplate](https://github.com/AvaloniaUI/Avalonia/blob/11.3.12/src/Markup/Avalonia.Markup.Xaml/Templates/DataTemplate.cs): Build(data, existing) reuses existing root, otherwise builds a new one. It does not transfer arbitrary live descendants between separately built template results.
- [Avalonia LayoutManager 11.3.12](https://github.com/AvaloniaUI/Avalonia/blob/11.3.12/src/Avalonia.Base/Layout/LayoutManager.cs): InvalidateArrange rejects attached controls with `control.VisualRoot != _owner`. ExecuteArrangePass puts controls with invalid-measure ancestors into `_toArrangeAfterMeasure`, then calls its own InvalidateArrange for each without root revalidation. Measure/Arrange also recurse through a control's **current** parent without filtering that parent by manager owner. Installed `Avalonia.Base.dll` was independently decompiled using ilspycmd into `$r\LayoutManager.decompiled.cs`; the relevant implementation agrees.

All this runs on **Dispatcher.UIThread**. Each native window nevertheless owns a different LayoutManager. Rendering and deferred presentation occur after model APIs return; old pending references can outlive a detach/reattach interval. A correct single model Owner and a correct UI thread therefore do not establish correct **temporal visual-root ownership**. This explains why global dispatcher draining at an arbitrarily selected application boundary was not a demonstrated fix in D1.

### Direct observation of wrong-root work

Diagnostic-only first-chance exception hook (logs, does not handle exceptions) reflected `_toArrangeAfterMeasure` from each current window's ILayoutRoot.LayoutManager at the exact exception boundary:

```text
D3 WRONG owner=HostWindow item=TextPresenter:PART_TextPresenter actualRoot=MainWindow same=False
D3 WRONG owner=HostWindow item=TextPresenter:PART_TextPresenter actualRoot=MainWindow same=False
D3 WRONG owner=HostWindow item=TextPresenter:PART_TextPresenter actualRoot=MainWindow same=False
Unhandled exception. System.ArgumentException: Attempt to call InvalidateArrange on wrong LayoutManager.
```

The three entries do not establish three distinct instances (no identity hash was logged), nor identify which named editor owns each TextPresenter. They do establish stale HostWindow queue entries referencing the main-window visual tree. The exception originates from the manager's own retry loop, not from application code explicitly calling an old manager.

## Controlled application comparison

One diagnostic variable changed: prepend a matching data template for `Document.Id == "Links"` that returns a fresh TextBox, leaving the real parameter editor, topology, product sources and factory unchanged. This intentionally substitutes display-only content; it is not a functional Links implementation.

```powershell
# Fresh process; no E2_TOOL.
$env:E2_EXTRA='1'
$env:D3_FRESH='1'
dotnet run --project "$r\Harness.csproj"
```

`app-fresh-links.log`: PASS Parameter apply; PASS Explicit redock owner; PASS Empty float closes; PASS Cancel keeps main and float; clean lifetime exit after Discard. Removing D3_FRESH gives original wrong-manager failure (`instrumented-move.log`). This supports live-view lifetime as the application-side seam; the tiny replacement also changes layout complexity, so it does not prove fresh production views of equivalent complexity alone fix every stale-queue path.

## Potential fix seams and tradeoffs (not implemented)

1. **Preferred application seam: App.axaml templates plus pane state ownership.** Context should hold presentation-independent state; templates create a fresh view when a new presentation is required. Do not create multiple wrappers around one existing live child. Preserve parameter drafts, selection, edits and command subscriptions outside disposable view instances before doing this. Tradeoff: migrating named XAML panels and event handlers out of MainWindow is broader than a parent-detach guard, but establishes a defensible lifetime contract rather than relying on cache timing.
2. **Narrow duplicate-parent seam:** make the actual transferable visual the template result, using Dock's existing recycling/detachment contract rather than hiding it inside a new ContentControl. Requires experimental proof with existing pane subscriptions and multiple float/redock cycles. **Does not address stale Avalonia layout work by itself** and is not offered as a proven fix. Keeping source tools non-collapsible is evidence about teardown, not a general cross-window repair.
3. **Framework seam for second crash:** current-root validation/re-routing in Avalonia LayoutManager's queue processing and arrange-after-measure retry, including checking whether a control migrated during processing. An isolated source-level patch could skip stale work in the old manager while leaving the destination manager responsible for the invalidation; merely changing the public guard would conceal the invariant violation. Tradeoff: private/framework maintenance and proof that destination layout is scheduled, not just absence of an exception. No package upgrade or framework patch was performed.

No global exception handler, swallowing the invariant failure, disabling floating, or arbitrary post/drain choreography is recommended as the fix.

## Remaining uncertainty / next discriminating experiment

- Exact source-cache eviction/old-presenter scheduling order that rebuilds the tool wrapper is not instrumented end-to-end. Its required retained-parent state and collapse sensitivity are directly observed.
- Wrong-manager failure is not minimized independently of BusChord's layout. Next bounded experiment: a two-native-window harness with multiple TextBoxes and the same nested Grid/ListBox layout, logging queue insertion, transfer, and first stale dequeue with control identity/root. Compare **equivalent** fresh-view versus reparented-view layouts, then source-patch root validation to verify destination layout correctness. Do not infer proof from simply draining queues or suppressing the guard.
- No claim about drag/drop gesture timing, physical keyboard, native mouse gestures, every document type, long multi-cycle stability, or saved layouts. All transfers here used Factory APIs on real native windows.
- No product fix was made. Only this report is intended for commit/cherry-pick.
