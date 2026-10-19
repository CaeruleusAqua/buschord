# V3: fresh presentation lifetime POC

2026-10-19. External-harness experiment only; no product source or package changes.

## Result

Fresh views created from presentation-independent state are viable for the tested Dock transfers and are the smallest *application-side direction* that avoids reusing a live control across hosts. This does **not** establish that the current Topology/Properties controls can be made fresh by changing templates alone, nor that it fixes the production Links explicit-move failure.

The POC replaced the actual Topology, Properties, and Links dockable contexts at runtime with small state objects. A custom template built new equivalent controls on demand, bound Topology selection and Properties edits to the existing `MainWindowViewModel`, and rendered them in the real application Dock layout. The actual dockable IDs and `Factory` were used; a float created a real native `HostWindow`.

| Check | Result |
|---|---|
| Topology: two Factory float → close/redock cycles | Pass; fresh control instance each float, root was the floating `HostWindow`, owner returned to original dock |
| Topology selection → shared VM | Pass; selected item updated `SelectedTreeNode` and `SelectedDevice` |
| Properties edit binding → shared VM | Pass; shared `EditName` update appeared in the floated TextBox |
| Properties: two float → close/redock cycles | Pass; fresh control instance and original owner restored |
| Existing real ParameterPane draft across both tools' cycles | Pass; synthetic value `42` and `HasUnappliedChanges` remained intact |
| Links explicit `MoveDockable` from float to Parameters document | Pass for a fresh **placeholder** Links view; owner returned to original document dock |

The explicit-move result is intentionally weak evidence: the replacement Links view was only a `TextBlock`, not the production nested Links view. D3 explicitly cautions that a display-only replacement changes layout complexity; D6's direct-result template with the real singleton Links pane still hit `InvalidateArrange on wrong LayoutManager`. This POC does not overturn either result or prove the production move is fixed.

## Scope and stopping point

The existing controls are not independent template products. `TopologyPanel`/`TopologyTree` and `PropertiesPanel`/`NameBox` are named fields in `MainWindow.axaml`; selection, context-menu, drag/drop, keyboard, and command handlers in `MainWindow.axaml.cs` close over that window and its fields. `ParameterPane` separately owns its draft dictionary and mutable editor state. Recreating only the visual controls would therefore lose or misroute those handlers/state. The POC preserved equivalent VM bindings and an existing parameter draft, **not** the production named-control handlers or a recreated ParameterPane draft store.

Stopped before product edits as requested. A production implementation needs a real presentation-independent pane state/command seam and fresh Topology/Properties view instances, with parameter drafts moved or deliberately shared outside disposable controls. That is a broader UI/state refactor, not a safe narrow `App.axaml` template change. No native pointer/keyboard gesture, float-X, main-window-close, production control, or equivalent-complexity explicit-move claim is made here.

## Reproduction

- Harness and log: `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-v3-fresh-view\Harness.csproj`, `Program.cs`, `fresh-view-poc.log`.
- The harness referenced this branch's `src/Knx.App/Knx.App.csproj`; it used a synthetic one-device project, private temporary settings/catalogue, actual application Dock model, and factory transfers only. No bus/router or private catalogue corpus was used.
- Command, from the harness directory: `dotnet run --project Harness.csproj` — exit 0, `V3 FRESH VIEW POC OK`. Initial build had no errors and four existing warnings (two nullable/async warnings in Core/Bus and two obsolete drag-drop API warnings in App).
- No dependency, product source, test source, or integration-branch changes were made. Only this report is intended for commit.

## Inputs and base verification

- Read `analysis/dock-crash-rootcause.md` sections 146–159 and the D6 result at `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-d6\RESULT.md`.
- V1 native evidence (`analysis/review-native-docking.md`): actual `SetCursorPos`/`SendInput` pointer drags floated the production Topology and Properties tools and both crashed with duplicate visual-parent exceptions. Its factory-only document redock and empty-host caveats remain applicable; this V3 POC did not use native pointer input.
- Requested branch is `agents/etsfix/v3-fresh-view`. HEAD is `dee0a3600f8a8f14d269cd7c26afb6b73d3b499d`, the verified base commit and initially clean checkout. The supplied base `dee0a3600f8a8f14d269cd7c26afb6b73d3b499` is missing its final `d` and is not a valid Git object.
