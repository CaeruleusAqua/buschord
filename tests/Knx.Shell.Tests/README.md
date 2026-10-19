# Draft lifecycle regression check

Run `dotnet run --project tests/Knx.Shell.Tests` on Windows with a desktop session.
Uses native windows, routed button/menu events, synthetic archive, isolated settings/catalogue;
only file-picker results are substituted through Avalonia's private storage-provider field.

Draft consent always comes first (Cancel/Discard only), then the existing project
Save/Discard/Cancel prompt. Neither consent discards drafts. Close commits discard
only after both confirmations; replacement does so only after successful lifecycle publication.
Ordinary Save/Save As retains drafts and explicitly says they were not saved.

## Red/green evidence (2026-10-19)

Temporarily restored the exact original affected methods/condition, then restored fixes:

- Original `CreateProject`: `dotnet run --project tests/Knx.Shell.Tests -- --transaction`
  exits 1: `FAIL unwritable New preserves project/selection/path/undo/draft`.
- Original clean-close condition: `dotnet run --project tests/Knx.Shell.Tests --no-build -- --close-only`
  exits 1: `FAIL prompt exists Nicht übernommene Parameter`.
- Original `LoadProject`: `dotnet run --project tests/Knx.Shell.Tests -- --transaction`
  exits 1: `FAIL settings failure Open preserves state`.
- Fixed full suite: `SHELL DRAFT LIFECYCLE OK` (exit 0).
- `dotnet build ets6.sln --nologo`: succeeds, zero errors (existing warning in bus test).
- `dotnet run --project tests/Knx.ParameterUi.Tests`: `PARAMETER UI CHECKS OK`.
- `dotnet run --project tests/Knx.Docking.Tests`: `DOCKING KEYBOARD OK`.
- `dotnet run --project src/Knx.App --no-build -- --smoke`: `SMOKE OK`.

Coverage includes both confirmation cancellation positions, project Save failure,
cancelled Save As, cancelled Open/New pickers and New wizard, failed routed Open/New,
successful Open/New/recents, draft-only close/Exit (including a hidden draft), ordinary Save/Save As, corrupt archives,
unwritable project target and settings failure preserving project/selection/path/undo
object identity and draft values. Recents memory is rolled back on settings failure.

## Boundaries

- A failed settings write after a successful New project write may leave the new file
  on disk, but never replaces the current in-memory project or clears drafts. No
  attempt is made to roll back an already-written destination file/settings bytes.
- Forced `RequestShutdown()` remains the pre-existing bypass used by desktop lifetime
  shutdown and test cleanup; normal window X and File/Exit use confirmations.
- No docking attachment changes, pane/parameter edits, packages, physical input,
  router, or bus access. Localized draft text follows the shell's existing German
  dynamic-message convention.
