# G4: manufacturer dynamic parameters (choose/when/assign)

## Evidence (read-only MDT corpus)

Corpus: `<project-path>\sample-catalogs\mdt`, 99 `.knxprod` archives (276 application-program
XML parts; the catalogue reader reports 273 unique program ids). Inspected by parsing the shipped
XML directly; no ETS execution.

Observed shapes:

- `choose` has `ParamRefId`; 2 occurrences also carry a decorative `InternalDescription`.
- `when` has either `test` (691,954 occurrences) or `default="true"` (6,417), plus decorative
  `InternalDescription` attributes (`""`, `","`, `"#"`, spaces). Branches appear in document order,
  with the default branch last where present.
- `test` has 375 distinct values in this corpus, all of the supported grammar:
  exact values (`7`), space-separated alternatives (`1 2`), and one comparison token
  (`!=0`, `<16`, `>0`, `>=4`, `<=90`). No `a..b` ranges and no mixed comparison alternatives occur.
- `assign` has two forms only: `TargetParamRefRef` + `Value` (2,470 raw occurrences) and
  `TargetParamRefRef` + `SourceParamRefRef` (26,800). Assigns appear inside `when` (29,176) and
  directly inside `ParameterBlock` (94).
- `ParameterRefRef` carries `RefId` plus decorative attributes (`Cell`, `IndentLevel`, `AliasName`,
  `InternalDescription`, `HelpContext`, `Icon`). `AliasName` appears only as a parameter-calculation
  argument (`ParameterCalculation/LParameters|RParameters`), not as a placement.
- Element inventory under `Dynamic`: `choose`, `when`, `ParameterRefRef`, `ParameterBlock`,
  `ParameterSeparator`, `ComObjectRefRef`, `Assign`, `Channel`, `ChannelIndependentBlock`,
  `Rows/Columns/Row/Column` (block layout), and the rejected constructs `Module`, `Repeat`, `Button`,
  `Rename`, `ParameterBlockRename`, `NumericArg`, `TextArg`. JavaScript lives outside `Dynamic` in
  `Static/ParameterCalculations/ParameterCalculation` (89 occurrences); `ModuleDef` has its own
  `Dynamic` (60), which is excluded from the application evaluation.
- Every declared `ParameterRef` in the sampled program was placed by a `ParameterRefRef`; the
  document-order check found 155 choose/assign pairs where the dependent choose precedes an assign
  in 21 files, which is why evaluation iterates to a stable value map instead of trusting one pass.

## Implemented behavior

New file `src/Knx.Core/DynamicParameters.cs`:

- `choose` evaluates its `ParamRefId` against the current effective value map.
- The first matching `when` wins; a `default="true"` branch is used only when no test matches.
- `test` supports exact values, space-separated alternatives, and a single comparison token.
  Comparisons are invariant decimal; exact equality falls back to ordinal text (enum labels are
  never compared, storage values are).
- `assign Value` sets the effective value; `assign SourceParamRefRef` copies the source's effective
  value. Targets/sources must be declared `ParameterRef` ids; anything else is reported.
- A `ParameterRefRef` in an active branch marks the row visible; a `ParameterRef` that only appears
  in inactive branches stays hidden. Static `Access`/`IsEnabled` rules from `Ksxml` still apply.
- A `ParameterBlock` in an active branch is visible; blocks in inactive branches are hidden.
- Evaluation is deterministic and bounded:
  - passes run in document order; values assigned earlier in a pass win, otherwise the previous
    pass supplies the value, so choose-before-assign patterns converge;
  - each pass starts from declared values, so stale assignments from a previously active branch do
    not persist;
  - at most 16 passes, at most 64 nesting levels; non-convergence and depth truncation are reported
    in `Unsupported`.
- No script is executed. `ParameterCalculation`, `Module`/`ModuleDef`, `Repeat`, `Button`, `Rename`,
  `ParameterBlockRename`, `LdCtrl*` and unknown elements are rejected and named in `Unsupported`.
  `ParameterSeparator`, `ComObjectRefRef` and block layout elements are ignored for parameter state.

## API

```csharp
// Initial evaluation of the parsed ApplicationProgram root. Optional current values are the UI draft.
DynamicEvaluation state = DynamicParameters.Evaluate(programRoot);
DynamicEvaluation edited = DynamicParameters.Evaluate(programRoot,
    new Dictionary<string, string?> { ["R1"] = "5" });
DynamicEvaluation fromRows = DynamicParameters.Evaluate(programRoot, instances);

// Write the result onto parsed rows/blocks (safe to re-apply after an edit).
DynamicParameters.Apply(edited, instances, blocks);
```

`DynamicEvaluation`:

- `Parameters`: `ParameterRef` id -> `DynamicParameterState { RefId, DeclaredValue, Value,
  IsVisible, IsEnabled }`.
- `KnownBlocks` and `VisibleBlocks`: block ids authored inside the application `Dynamic`.
- `Unsupported`: sorted, deduplicated construct/condition/ref messages.
- `TracksVisibility`: false when the program has no `ParameterRefRef`, so static visibility remains
  authoritative (synthetic/test programs).
- `Converged`, `DepthLimitReached`, `Passes`, `EvaluatedChooses`, `SelectedBranches`,
  `UnmatchedChooses`, `Assignments`. Statistics describe the final pass.

`Ksxml.Parse(XDocument, out DynamicEvaluation)` is the new overload; `Ksxml.Parse(XDocument)` keeps
its tuple shape and applies the initial evaluation internally. Integration semantics:

- `ParameterInstance.Value` is the effective value; `DefaultValue` remains the static declaration.
- `ParameterInstance.IsVisible`/`IsEnabled` combine static declarations with the initial evaluation.
- `ParameterInstance.IsStaticallyVisible`/`IsStaticallyEnabled` keep the pre-evaluation flags, so a
  later evaluation can restore rows that become visible again.
- `ParameterBlock.IsVisible`/`IsEnabled` reflect the initial evaluation for dynamic blocks; blocks
  outside the application `Dynamic` are unchanged.

### Parameter pane integration (UI not changed here)

1. Keep the program `XElement` (or reload it through `Knxproj.LoadApplicationProgram`) alongside the
   parsed rows; `CatalogApplicationProgram` does not retain it.
2. On a value change, call `DynamicParameters.Evaluate(root, instances)` and then
   `DynamicParameters.Apply(state, instances, blocks)`; rebuild/filter the row list from `IsVisible`
   and show `Unsupported` as the static-editor warning.
3. Persist edits keyed by `ParameterRef` id as before; do not persist assignment results as user
   edits unless product behavior is confirmed against ETS.

## Limits

- Conditions on a `ParameterRef` with no declared value never match; the default branch (if any)
  applies. No default is manufactured.
- Not implemented and reported: numeric/text ranges (`1..3`), mixed comparison alternatives
  (e.g. `>=1 <=5`), text ordering comparisons, `ParameterCalculation` JavaScript, module
  instantiation/arguments, `Repeat`, `Button`, `Rename`/`ParameterBlockRename`, load procedures,
  external/unresolved refs.
- `Cell`, `AliasName`, `IndentLevel`, icons and help contexts are not applied to layout or text.
- `ComObjectRefRef` visibility and communication-object state are out of scope; only parameter rows
  and blocks are evaluated.
- Assignment effects are an initial/edited-form computation, not a download image. No values are
  written to the project.
- Only the application `Dynamic` is evaluated; `ModuleDef` parameters remain excluded as before.
- The App still shows the legacy `UnsupportedFeatures` list (choose/when/assign present). Changing
  that warning is a UI task outside this change.

## Verification

From `<project-path>/agent-wt/gap-g4`:

```powershell
dotnet build tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Core.Tests
```

with `KNX_CATALOG_SAMPLES=<project-path>\sample-catalogs\mdt`.

- Build succeeds; only the pre-existing `KnxProductCatalog` nullable warning remains.
- `Knx.Parameter.Tests`: **PASS 1070 parameter checks** (existing checks unchanged and still pass).
  New synthetic coverage: equality, alternatives, inequality, `<`, `<=`, `>`, `>=`, default branch,
  nested choose, `assign` value/source, visibility, block visibility, read-only enable, re-evaluation,
  16-pass cycle report, 64-level depth limit, unsupported range, `Repeat`, `ParameterCalculation` and
  unresolved external ref.
- `Knx.Core.Tests`: **ALLE CHECKS OK**.
- `Knx.ParameterUi.Tests` (sanity, not required): **PARAMETER UI CHECKS OK**.
- Real corpus evaluation: **99 archives, 276 program parts**; final pass **12,716 chooses,
  6,870 selected branches, 555 assignments, 24,953 visible refs**, max **2 passes**, **0**
  non-convergent programs, **7** unsupported kinds:
  `Module`, `Repeat`, `Button`, `Rename`, `ParameterBlockRename`, module definitions and parameter
  calculations. No `test` string in the corpus was rejected.
