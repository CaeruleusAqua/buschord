# Clean-code review: tests, CI and documentation (V4)

Range: `git diff 12fc904...HEAD -- tests docs .github ets6.sln analysis/workbench-v4-*.md`.
Base of this review branch: `55ff6e1`. Verified by reading the diff and surrounding
sources, building `ets6.sln -c Release` (0 errors) and running the eight changed
console suites on Windows (Workbench, WorkbenchSessions, Archive, Catalog,
CatalogUi, Bus, Linking, WorkbenchUiState): all pass.

## P1

1. **`analysis/workbench-v4-status-matrix.md` contradicts itself.**
   Lines 11–13 claim "Neu **done**" for 18, 19, 24, 32, 37, 38/39, 44, 51–53,
   57, 59, 76/77, but the table still says 18 `missing` (:39), 19 `partial`
   (:40), 24 `missing` (:45), 32 `missing` (:53), 51–53 `missing` (:72–74),
   57 `missing` (:78), 59 `missing` (:80), 76/77 `missing` (:97–98), and
   "Nächste Implementierungswellen" (:134–135) schedules the same items again.
   The file is added by this range, so the reader gets two answers and cannot
   tell which was verified. Header `Basis 428a7d9` (:3) is also stale — that is
   a pre-wave commit, not the reviewed tree.
   *Fix:* update the eight rows to `done`, delete them from the wave list, set
   the base to `12fc904`.

2. **The device detail edit — including the segment fix — has no check at all.**
   `src/Knx.App/MainWindowViewModel.cs:433` (`UpdateDeviceDetails`) is called
   only from `src/Knx.App/WorkbenchWindow.axaml.cs:892`; neither `tests/` nor
   `WorkbenchCheck.cs` references it. Commit `43cff3c` changed its
   segment-preservation branch (`:443–446`, `:470–472`) and nothing fails if
   that branch is removed again, so the "failing-before" rule in
   `CONTRIBUTING.md:54` is unmet for a data-loss-adjacent edit.
   *Fix:* one check in `WorkbenchCheck.cs` (device in `S-1`, keep the same line,
   assert it is still in `S-1`, not in `line.Devices`); it fails before `43cff3c`.

## P2

3. **Search-result navigation is untested.** `WorkbenchWindow.axaml.cs:1019`
   subscribes `NavigateRequested` to `OpenSearchHit` (`:1023`), which branches
   per hit kind. Commit `e8d83b5` fixed dead search hits; `tests/Knx.Search.Tests`
   only covers the domain query. No check exercises any branch.
   *Fix:* add one selftest per hit kind in `WorkbenchCheck.cs`.

4. **`docs/project-backup.md` claims the opposite of the manual and the code.**
   `:29` ("UI-Verdrahtung ist offen") and `:92` ("Keine Verdrahtung in
   `WorkbenchWindow`, Öffnen/Speichern") versus `docs/manual.md:115–116` and
   `WorkbenchWindow.Commands.cs:127,132,217,251` (backup before save, Create,
   Backups dialog, Capture in the Projekt menu).
   *Fix:* replace both "offen" bullets with the actual wiring and state the
   failure rule below.

5. **Backup-before-save is unchecked and its failure mode is undocumented.**
   `ProjectBackup.Create` throws for corrupt/empty/oversized targets
   (`tests/Knx.Archive.Tests/Program.cs:332–336`) and is called inside the save
   `try` (`Commands.cs:127,132`), so a bad backup aborts the save. No test or doc
   states that intent.
   *Fix:* decide (`try/catch` around the backup, or document "save fails when the
   backup fails"), then one check with a temp backup root.

6. **A test locks untranslated German prose.** `tests/Knx.WorkbenchSessions.Tests:421`
   asserts `Detail.Contains("keine reale", OrdinalIgnoreCase)`; the string is
   hard-coded in `src/Knx.App/Workbench/Sessions/LineScanSession.cs:43,49,52`
   while the shell was localized (`fae368b`). The assertion breaks on the next
   localization pass and hides the gap.
   *Fix:* assert `Origin`/`Status` only; move the three details to
   `WorkbenchStrings`.

7. **`CONTRIBUTING.md:37–41` test inventory is stale.** It lists ten suites and
   omits `Knx.Workbench*` (5), `Knx.Docking.Tests`, `Knx.Shell.Tests` — exactly
   the suites this range changes.
   *Fix:* append them to the list.

8. **Two assertions cannot fail for the reason their name implies.**
   `tests/Knx.Workbench.Tests/Program.cs:608` calls `ParseSeverities`, which is
   `Severity is Info or Warning or Error` over a three-value enum
   (`:615`); `Knx.WorkbenchSessions.Tests:423` calls `Check(name, true)`.
   *Fix:* drop `ParseSeverities` (the counter check above it is the real one);
   for the line scan rely on the thrown `ArgumentException`.

## P3

9. **`diagnostics report sees a fully loaded device` is asserted twice** with two
   fixtures (`Knx.Workbench.Tests/Program.cs:394`,
   `Knx.WorkbenchSessions.Tests/Program.cs:354`); a signature change must be
   edited in both.
10. **Two inline `.knxprod` fixtures encode the same gating contract**
    (`Knx.Catalog.Tests/Program.cs:274–290`, `Knx.CatalogUi.Tests/Program.cs:210–240`);
    parser changes need two edits.
11. **CI omits two projects silently.** `ci.yml:17–41` lacks
    `tests/Knx.Docking.Tests` and `tests/Knx.Shell.Tests`; they run only as
    Windows steps (`:124–131`, one without `--no-build`), and unlike
    `Knx.Icon.Tests` (`:81–83`) the exclusion is not announced. The Linux job
    never runs the Shell checks this range rewrote.
    *Fix:* add a `::notice::` skip line for both, as done for Icon.
12. **`analysis/workbench-v4-completion-plan.md` disagrees with itself:** step 7
    is `[x]` (`:17`) while every B7 item is `[ ]` (`:102–107`); "Fortschritt"
    repeats the same implemented list twice (`:156`, `:158`); `:3` calls
    `12fc904` the `development` base, but `development` is `55ff6e1`.
