# Parameter draft regressions

```powershell
dotnet run --project tests/Knx.ParameterUi.Tests -c Release
dotnet run --project tests/Knx.ParameterUi.Tests -c Release -- --native
```

The default checks use synthetic archive and imported catalogue fixtures. `--native`
requires a graphical desktop: it opens an actual Avalonia window, exercises TextBox
undo back to baseline, and switches from A to B synchronously inside Apply's
`CanUndo` notification (including duplicate device IDs). No bus/router access.
