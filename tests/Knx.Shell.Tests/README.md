# Workbench draft lifecycle regression check

Run `dotnet run --project tests/Knx.Shell.Tests -c Release` on Windows with a desktop session.

Uses a native `WorkbenchWindow` created after application setup through
`App.TestWindowFactory`, with isolated temporary settings, catalogue store and a
synthetic archive. Only native file-picker results are substituted through
Avalonia's private storage-provider field. Menu actions are routed
`MenuItem.Click` events and dialogs are answered through their
`ConfirmDialog` buttons; no OS keyboard/mouse input, docking, router or bus
access is involved.

Draft consent always comes first (Cancel/Discard only), then the saved-project
Save/Discard/Cancel prompt. Neither consent discards drafts. Close commits
discard only after both confirmations; project replacement does so only after a
successful load.

Coverage:

- ordinary Save and Save As retain drafts, report that drafts were not saved,
  and leave the archive free of draft values;
- draft-only close Cancel; dirty project plus draft with Cancel at each prompt;
- project Save failure (locked file) keeps window, project, dirty state and draft;
- both confirmations followed by Save close without applying drafts;
- cancelled Open picker, corrupt-archive Open with its error dialog;
- successful Open replaces the project and clears drafts;
- hidden (stashed) draft close with Discard;
- transaction checks direct to the view model: corrupt Open, unwritable New and
  settings-write failures preserve project, selection, path, undo identity and
  recents, including Save As.

Optional modes: `--transaction` stops after the view-model transaction checks;
`--close-only` checks only the draft-only close cancellation.

## Verified

Release build succeeds. On Windows, 2026-10-21:

- full run: `SHELL DRAFT LIFECYCLE OK` (exit 0);
- `--transaction`: `TRANSACTION OK` (exit 0);
- `--close-only`: `PASS draft-only close cancelled` (exit 0).

## Boundaries

- A failed settings write after a successful New project write may leave the
  new file on disk, but never replaces the current in-memory project or clears
  drafts. No attempt is made to roll back an already-written destination
  file/settings bytes.
- Forced `RequestShutdown()` remains the pre-existing bypass used by desktop
  lifetime shutdown and test cleanup; normal window X and File/Exit use
  confirmations.
- No docking/MainWindow shell, pane attachment, packages, physical input,
  router, or bus access. Localized labels are read from the shell's own
  `WorkbenchStrings`/`Strings` keys.
