# Parameter draft regressions

```powershell
dotnet run --project tests/Knx.ParameterUi.Tests -c Release
dotnet run --project tests/Knx.ParameterUi.Tests -c Release -- --module-image-build
dotnet run --project tests/Knx.ParameterUi.Tests -c Release -- --native
dotnet run --project tests/Knx.ParameterUi.Tests -c Release -- --native --shots
```

The default checks use synthetic archive and imported catalogue fixtures. `--native`
requires a graphical desktop: it opens an actual Avalonia window, exercises TextBox
undo back to baseline, and switches from A to B synchronously inside Apply's
`CanUndo` notification (including duplicate device IDs). `--shots` additionally
renders the real pane at 1440×900 and 1024×700 in light and dark under
`%TEMP%\opencode\param-shots`. No bus/router access.

`--module-image-build` runs the same bounded module/Repeat/root-allocator image
checks included in the default suite, using isolated synthetic archives without
a graphical desktop. It verifies full data/validity/write masks, latent owners,
source/carrier binding, overlap and combined expansion/validation work budgets.

## Host API

Create one `ParameterEditingSession(vm)` for the project workspace, then create
fresh `ParameterPane(session)` instances for each host. `Load(DeviceRow?)`,
`Refresh()`, `HasUnappliedChanges`, `DiscardDrafts()` and `Apply()` are available
without a control. Keep calling `Load` on selection and `Refresh` after model or
catalogue publication. The session does not subscribe to the VM or retain views.
Detached views unsubscribe; call `Dispose()` when permanently closing a view.
The parameterless and VM pane constructors and dialog API remain compatible.

The footer reads `ChangedCount`/`DraftText` for the draft pill, `ValidationErrors`/
`ValidationSummary` for the footer-wide error line and `CanReset`/`ResetToDefaults()`
for the reset-to-default action. The same core validation drives the per-field error
under each editor, so hidden or read-only rows are never reported as invalid.

`ReviewChanges()` returns `ParameterChangePreview` (device identity, immutable
diff records with before/after/error, validity and explicit limitations).
`CancelReview()` closes the review without discarding drafts; editing invalidates
the preview. `Changed` lets a future shell observe session state. `Apply()` uses
the existing VM validation and single undo path, not a second mutation engine.
KO activation is not supported by the current dynamic API (`ComObjectRefRef` is
ignored), so the native panel explicitly says effects are not evaluated and links
are preserved. No manufacturer or demo `applyMode` semantics are guessed.

Additional checks cover independent fresh/disposed views, shared native editors,
retained validation, host-only discard, project replacement, native review/cancel,
invalid previews and one-step undo. Native events are in-process only. Tests use
synthetic archives and isolated empty settings/catalogue stores.
