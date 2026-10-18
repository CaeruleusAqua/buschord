# Catalogue UI parity (P5)

## Evidence and scope

- Parent-supplied REA evidence `ev_f1eebdacefd7e32fa8f4893dace4ec59f60ac788efae3c9e207be717baae3360` from `Knx.Ets.OnlineCatalog.Download.dll`: `IndexFile` / `IndexFileSection`, `GetEntryById`, `AddChildSection`, `FilterByMarket`, section and manufacturer indexing. Supports structured catalogue browsing, **not** a claim about runtime text-search behavior.
- Local case-insensitive substring search (name, order number, ID, description) is our design choice. Section options come from existing product `CatalogPaths`; exact path selection combines with search. Manufacturer selection remains the existing library list.
- Base verified before edits: `98bb80a8ed26fafda71f97c9c38e11b9e7c60a8d`; assigned worktree/branch only, no master edits.

## Implemented

- Catalogue library opens without a project. Browsing, details, result counts and explicit no-results text remain available. Insertion reports the missing project locally.
- Import picker accepts `.knxprod` and vendor `.zip`, through existing `CatalogStore.Import`. Batch errors remain separate from success/reload text; broken library entries are nonfatal and listed with filenames.
- Target choices include installation identity/display. A selected topology line is preferred on opening. Stale choices are resolved by project ID/GUID and installation/area/line IDs, requiring exactly one match; equal numeric addresses in another installation never select the first match.
- All validation precedes `Edit`, device creation, mutation and selection: valid IA components, target area/line, nonzero device, project-wide uniqueness, available automatic address and unambiguous target. Core nibble-overflow inputs are explicitly checked before packing.
- Existing `MainWindowViewModel.Edit` remains the undo boundary. Success selects the inserted device only after refresh. Communication objects come from existing `CatalogInstaller`.

## Integration API

`Knx.App.Services.CatalogBrowser`: `Filter`, `Sections`, `Lines`, `Insert`. `LineChoice` carries immutable project/installation/area/line identities rather than model references. `Insert` returns success plus localized message and updates device selection on success. It does not set status; the window displays the returned message both locally and in the VM.

No new dependencies, no Core catalogue/model changes, no solution changes. MainWindow change is only removal of the no-project guard in `OnCatalogLibrary`.

## Validation

- `dotnet build tests/Knx.CatalogUi.Tests` — passed.
- `dotnet run --project tests/Knx.CatalogUi.Tests` — passed (37 checks).
- `dotnet run --project src/Knx.App -- --smoke` — `SMOKE OK`.
- Tests cover all search fields, combined section/search filtering, no project, invalid/overflow/wrong-line/duplicate addresses without mutation, cross-installation duplicates, selected installation, selection, com objects, Undo/Redo, stale/foreign/missing/ambiguous line choices, full line, `.knxprod` and nested vendor ZIP imports, nonfatal library load errors.
- Synthetic archives use a unique scratch directory and explicitly configured `CatalogStore`; no synthetic imports into real APPDATA, no settings writes from these checks, no hardware traffic.
- **Visual validation not performed.** These are build/headless workflow checks, not screenshots or UI interaction tests.

## Remaining limits

- Browsing/import/parsing remains synchronous, matching the existing window; large libraries may pause the UI.
- Section filtering uses exact stored paths, not a tree or market filter. Search covers the selected library entry, not all manufacturers at once.
- Project identity relies on IDs/GUID supplied by the existing public model. Duplicated IDs within a target hierarchy are rejected rather than guessed.
- Parameter editing integration is main-owned follow-up. Existing parser limits/program resolution belong to the Core catalogue worker; UI reports program-load exceptions nonfatally.
