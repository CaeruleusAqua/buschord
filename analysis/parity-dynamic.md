# G4: manufacturer dynamic parameters (choose/when/assign)

## Evidence (read-only MDT corpus)

Corpus: `<project-path>\sample-catalogs\mdt`, 99 `.knxprod` archives (276 application-program
XML parts; the catalogue reader reports 273 unique program ids). Inspected by parsing the shipped
XML directly; no ETS execution.

Observed shapes:

- `choose` has `ParamRefId`; 2 occurrences also carry a decorative `InternalDescription`.
- `when` has either `test` (1,295,654 occurrences across all XML parts; 1,070,353 in application
  programs) or `default="true"` (10,608; 9,352 application), plus decorative
  `InternalDescription` attributes (`""`, `","`, `"#"`, spaces). Branches appear in document order,
  with the default branch last where present.
- `test` has 379 distinct values in this corpus (327 in application programs), all of the supported
  grammar: exact values (`7`), space-separated alternatives (`1 2`), and one comparison token
  (`!=0`, `<16`, `>0`, `>=4`, `<=90`). No `a..b` ranges, no mixed comparison alternatives and no
  text-ordering comparison occur; those shapes are nevertheless reachable from other manufacturers,
  see Limits.
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
- Not every declared `ParameterRef` is placed: **2,880 of 401,416** application-level refs (0.7 %)
  have no `ParameterRefRef` anywhere and stay hidden under this model. At the default selections,
  **376,463 of 401,416** refs (93.8 %) are hidden because only one branch of each `choose` is active;
  no program hides all of its refs. The document-order check found 155 choose/assign pairs where the
  dependent choose precedes an assign in 21 files, which is why evaluation iterates to a stable
  value map instead of trusting one pass.

## Implemented behavior

New file `src/Knx.Core/DynamicParameters.cs`:

- `choose` evaluates its `ParamRefId` against the current effective value map.
- The first matching `when` wins; a `default="true"` branch is used only when no test matches.
- `test` supports exact values, space-separated alternatives, and a single comparison token.
  Comparisons are invariant decimal; exact equality falls back to ordinal text (enum labels are
  never compared, storage values are).
- `assign Value` sets the effective value; `assign SourceParamRefRef` copies the source's effective
  value. Targets/sources must be declared `ParameterRef` ids; anything else is reported.
- `Apply` validates assigned values against the target's declared type/range. An invalid assignment
  leaves the declared value on the row and reports the refId in `InvalidAssignments`; it is not
  silently written. Values without a known parameter type are not rejected by `Apply`.
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
// Initial evaluation of the parsed ApplicationProgram root.
DynamicEvaluation state = DynamicParameters.Evaluate(programRoot);

// Re-evaluation: pass explicit user edits only, keyed by ParameterRef id.
// Never pass rows' effective Values; they already contain assignment results from the previous
// Apply, and re-feeding them pins a value after its assigning branch has deactivated.
DynamicEvaluation edited = DynamicParameters.Evaluate(programRoot,
    new Dictionary<string, string?> { ["R1"] = "5" });

// Write the result onto parsed rows/blocks (safe to re-apply after an edit).
DynamicParameters.Apply(edited, instances, blocks);
if (edited.InvalidAssignments.Count > 0) { /* report the rejected refIds */ }
```

`DynamicEvaluation`:

- `Parameters`: `ParameterRef` id -> `DynamicParameterState { RefId, DeclaredValue, Value,
  IsVisible, IsEnabled }`.
- `KnownBlocks` and `VisibleBlocks`: block ids authored inside the application `Dynamic`.
- `Unsupported`: sorted, deduplicated construct/condition/ref messages.
- `InvalidAssignments`: sorted refIds whose assignment was rejected by `Apply`; empty after
  `Evaluate` alone, because type information is only available on parsed rows.
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
2. Keep the user's edits in a separate dictionary keyed by `ParameterRef` id. On a value change,
   call `DynamicParameters.Evaluate(root, userEdits)` and then
   `DynamicParameters.Apply(state, instances, blocks)`; rebuild/filter the row list from `IsVisible`
   and show `Unsupported` as the static-editor warning.
3. Persist edits keyed by `ParameterRef` id as before; do not persist assignment results as user
   edits unless product behavior is confirmed against ETS.

Current UI gap: the App does not re-evaluate at all yet. Values loaded from a `knxproj` device are
stored on the rows but are not passed to `Evaluate` as user edits, and no value change triggers a
new evaluation. Until that wiring exists, a loaded device shows the catalog/evaluation-time values
and visibility, not the device's own parameter set, and editing a value leaves dependent rows
stale.

## Limits

- Conditions on a `ParameterRef` with no declared value never match; the default branch (if any)
  applies. No default is manufactured.
- Unsupported condition shapes behave differently and the difference is user-visible:
  - a pure range token (`1..3`) is reported, the branch never matches, and the default branch
    applies; a mixed range+alternative (`1..3 5`) still reports the range but a sibling alternative
    such as `5` can match (partial evaluation);
  - mixed comparison alternatives (`>=1 <=5`) are rejected and reported, no token matches;
  - a text-ordering comparison such as `a<b` on a non-numeric value silently never matches: operand
    parsing fails without adding an `Unsupported` entry. Do not rely on this for non-numeric order.
- Not implemented and reported: `ParameterCalculation` JavaScript, module instantiation/arguments,
  `Repeat`, `Button`, `Rename`/`ParameterBlockRename`, load procedures, external/unresolved refs.
- `Cell`, `AliasName`, `IndentLevel`, icons and help contexts are not applied to layout or text.
- `ComObjectRefRef` visibility and communication-object state are out of scope; only parameter rows
  and blocks are evaluated.
- Assignment effects are an initial/edited-form computation, not a download image. No values are
  written to the project.
- Only the application `Dynamic` is evaluated; `ModuleDef` parameters remain excluded as before.
- The App still shows the legacy `UnsupportedFeatures` list (choose/when/assign present). Changing
  that warning is a UI task outside this change.

## Verification

From `<project-path>/agent-wt/fix-dyn`:

```powershell
dotnet build tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Core.Tests
```

with `KNX_CATALOG_SAMPLES=<project-path>\sample-catalogs\mdt`.

- Build succeeds; only pre-existing warnings in unrelated projects remain.
- `Knx.Parameter.Tests`: **PASS 1078 parameter checks** (existing checks unchanged and still pass).
  New synthetic coverage: equality, alternatives, inequality, `<`, `<=`, `>`, `>=`, default branch,
  nested choose, `assign` value/source, visibility, block visibility, read-only enable, branch
  deactivation restoring declared values, idempotent repeated evaluation with explicit edits,
  rejected invalid assignment reported in `InvalidAssignments`, hidden refs non-editable, 16-pass
  cycle report, 64-level depth limit, unsupported range, `Repeat`, `ParameterCalculation` and
  unresolved external ref.
- `Knx.Core.Tests`: **ALLE CHECKS OK**.
- `Knx.ParameterUi.Tests` (sanity, not required): **PARAMETER UI CHECKS OK**.
- Real corpus evaluation: **99 archives, 276 program parts**; final pass **12,716 chooses,
  6,870 selected branches, 555 assignments, 24,953 visible refs, 376,463 branch-hidden refs
  (93.8 %), 2,880 unplaced refs (0.7 %), 0 all-hidden programs**, max **2 passes**, **0**
  non-convergent programs, **7** unsupported kinds:
  `Module`, `Repeat`, `Button`, `Rename`, `ParameterBlockRename`, module definitions and parameter
  calculations. No `test` string in the corpus was rejected.
