# P6 — Smart-link DPT compatibility

Base: `98bb80a8ed26fafda71f97c9c38e11b9e7c60a8d`; branch: `agents/parity/p6-smartlinks`.

## Scope and evidence

Addresses the first-digit DPT collision and incompatible-name suggestions reported in
`analysis/parity-rea-audit.md` on `agents/parity/p1-audit` (`07097bf6`, inspected with `git show`).
This is a source-level safety correction informed by that bounded REA audit, not an
ETS compatibility or hardware-validation claim. No new REA, bus, or network operations.

Inspected every caller: App `MainWindowViewModel.SuggestLinks` / `ApplyLinks`,
`MainWindow.OnSmartLink`, MCP `SmartLinkSuggest` / `SmartLinkApply`, and existing Core tests.
App selection policy remains the integrating session's responsibility; MCP currently
selects its highest-scoring suggestion without supplying DPT metadata.

## Implemented rules

- Private invariant integer parser accepts `DPT-<main>`, `DPST-<main>-<sub>`,
  `<main>.<sub>`, and numeric `<main>`. Leading zeros normalize numerically;
  main must be positive and subtype nonnegative. Prefixes are case-insensitive.
- Known main types must match completely: 1, 10, and 16 are distinct.
- Two known subtypes must match; main-only metadata permits any subtype of that main.
- Known conflicts are excluded even with identical names or a zero minimum score.
- Missing/malformed metadata permits the existing name heuristic, without DPT bonus
  or a claimed DPT match. Identical malformed strings do not establish compatibility.
- Existing score weighting, deterministic ordering, limits, linked-object skipping,
  and `Apply` semantics are unchanged. Explicit multiple suggestions remain applicable;
  repeated links remain idempotent.

No shared Core normalization helper exists at the assigned base. The private parser
was explicitly authorized instead of a new shared API, Bus dependency, or abstraction.

## Offline validation

Restore used only local source `<project-path>/AppData/Local/Temp/opencode` with
`-p:NuGetAudit=false`; neither test project has package dependencies.

```powershell
dotnet run --project tests/Knx.SmartLink.Tests --no-restore
dotnet run --project tests/Knx.Core.Tests --no-restore
```

Results: **88 SmartLinking checks passed**; existing Core tests: **ALLE CHECKS OK**.
Coverage includes equivalent subtype IDs, conflicting mains/subtypes, main-only matching
in both directions, malformed/overflow metadata, unknown-name heuristics, DPT-only
scoring, limits/tie ordering, duplicate links, explicit multi-apply, and device-specific lookup.
Existing unrelated nullable warning: `Catalog/KnxProductCatalog.cs:208` (CS8620).

Limits: DPT matching is not bit-size, security, flags, or product compatibility validation.
No object metadata cache or UI/MCP changes are included. Existing tests were not modified.
