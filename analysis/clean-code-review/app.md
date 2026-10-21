# Clean-code review — V4 Workbench app slice

Base `55ff6e1`, fixed point `12fc904`, scope `src/Knx.App`. `dotnet build src/Knx.App` passes (0 errors); findings are behavioural and structural.

## P1

**1. Room assignment silently lost outside the first installation — `src/Knx.App/MainWindowViewModel.cs:466`**
`if (targetRoom is not null && ReferenceEquals(installation, p.Installations.FirstOrDefault())) installation.Locations.Find(targetRoom.Id)?.AssignDevice(device);`
Lines 464–465 already strip the device from every room, and `Locations.Find` only matches in the installation owning the room. A target room in installation 2+ therefore leaves the device roomless. Fix: drop the `ReferenceEquals` guard (`Find` returns null elsewhere) and require `n.Kind == SpaceKind.Room` at 447, as `CatalogBrowser.Insert` does. Test: two-installation `WorkbenchCheck` fixture, move a device into the second installation's room, assert `DeviceRefs`.

## P2

**2. Change history survives project replacement — `WorkbenchWindow.Commands.cs:317`** is the only `_audit.Clear()`; `OpenProjectAsync:101`, `NewProjectAsync:114`, `ImportDemoJsonAsync:333` keep entries describing the replaced project. Fix: clear inside `ResetSessionsForReplacementAsync` (`WorkbenchWindow.axaml.cs:238`), which every replacement path calls. Test: edit project A, open B, changes tool empty.

**3. A failed backup aborts saving — `Commands.cs:127/132`**
`if (File.Exists(path)) ProjectBackup.Create(path);` sits inside the save `try`. `Create` rejects archives over 128 MiB, corrupt archives and read-only backup roots, so the user cannot save and reads "save failed". Fix: catch `IOException`/`UnauthorizedAccessException`/`InvalidDataException` around the backup call, warn in status, save anyway. Test: `ProjectBackup.Create` with an unwritable `backupRoot`; save still succeeds.

**4. Tool-view factory repeated three times, tool ids six times — `WorkbenchWindow.axaml.cs:948, 1519, 1572`** repeat the same switch; the guards at `942` (note `"search" or "search"`), `345`, `357` and `WorkbenchWorkspace.cs:158` repeat the id set. Fix: one `static readonly string[] ToolIds`, one `CreateToolView(string)`, one `ToolTitle(string)`; `TryRestoreLayout` consumes `ToolIds`. Test: existing float/rebuild smoke covers regression.

**5. Five hand-built dialog shells — `Commands.cs:221, 347, 449`, `axaml.cs:710, 1328`**: the same Window/StackPanel/actions/Cancel boilerplate. `PromptTextAsync` labels its confirm button `W("Create")` although "Restore workspace" also uses it. Fix: one `ShowPanelAsync(title, controls, primaryLabel)` helper. Test: none.

**6. Localization: hard-coded German, misused keys, fourth dictionary —** `MainWindowViewModel.cs:399, 404, 410, 428, 436-449` hard-code German while the same methods call `Strings.Get`; `LineScanSession.cs:43, 48, 52` return German UI text that `axaml.cs:1292` prints verbatim; `Commands.cs:278` reports a failed semantic export as `W("CompareFailed")`; five "no project" paths use `W("ScanNeedsProject")` (`Commands.cs:253, 270, 283, 300, 450`); `Commands.cs:223-224` prints a path via `Note` and adds `W("NoBackups")` twice. `CatalogUiText` (`CatalogBrowser.cs:42`) adds another dictionary with its own `Strings.Language` check. Fix: add keys for scan/semantic/backup states, use `NoProjectLoaded` for missing projects, return a status code from the scan session, fold `CatalogUiText` into `WorkbenchStrings`. Test: `--smoke --lang=en` asserting these paths emit no German literal.

**7. `RefreshInspector` does too much — `axaml.cs:790-940`**: sidebar-mode switch, selection logic, and device/GA/project inspectors with layout and commands inline. Fix: `RefreshSidebar()` plus `BuildDeviceInspector`/`BuildGroupAddressInspector`/`BuildProjectInspector`, resolving selection once at the top. Test: existing inspector smoke plus one GA-inspector assertion.

## P3

**8. View construction mutates shared status — `axaml.cs:1010-1011`**: `CreateProblemsView` runs `RunCheck()` and writes `ViewModel.Status` on every build, including the overview tab refresh and `OnFloatTool`, overwriting the last operation message. Fix: render the count in the view; drop the `Status` write. Test: edit, then open overview; status unchanged.

**9. Dead and speculative code — `ProgrammingSession.cs:100, 108, 121`**: `DeviceState`, `CheckProgramming`, `ResetForChange` have no callers; `_ = DeviceProgrammingState.AffectedFlags(change)` discards its only purpose and repeats inside `state.ResetForChange`; `WorkbenchAudit.cs:21` `CountChanged` is raised, never subscribed. Fix: delete, or wire reset into `OnProjectEdited` and use `CountChanged` instead of rebuilding on every edit. Test: none (dead code).

**10. Cached UI state can diverge — `CatalogPane.axaml.cs:22, 342, 447`**: `_shownProgram` mirrors `FindProgram`, but `ShowDetails` returns before assigning it when `SelectedCatalog` is null (339), so `InsertBlocker` may check the previous product. Fix: call `FindProgram` in `InsertBlocker`; delete the field. Test: none (`CatalogBrowser.Insert` re-checks).

**11. Escape synthesizes a click — `ConfirmDialog.cs:65-71`**: `(cancel ?? first)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))`; the fallback is the affirmative button (destructive for `AskDeleteAsync`) and only stays harmless because every current dialog has an `IsCancel` button. Fix: remember the cancel `ConfirmChoice` and call `Choose(...)` directly. Test: `AskDeleteAsync` + Escape returns false.
