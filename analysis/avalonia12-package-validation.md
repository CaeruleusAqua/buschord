# V2 Avalonia 12 package validation

**Decision: adopt-ready candidate; no new native regression against the exact base.** The package upgrade fixes the minimal live-tool float case and the tested document factory redock. BusChord's native Topology/Properties float still fails because the app retains parented singleton controls; do not claim the package update fully fixes docking.

## Exact package set

| Packages | Version |
|---|---:|
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter` | `12.1.4` |
| `Dock.Avalonia`, `Dock.Avalonia.Themes.Fluent`, `Dock.Model`, `Dock.Model.Mvvm` | `12.1.0.12` |

NuGet stable indexes were fetched on 2026-10-19. Dock's `12.1.0.12` nuspec is pinned to repository commit [`4b12dc1ef877d9950e5d1c620a9261d820afef72`](https://github.com/wieslawsoltes/Dock/tree/v12.1.0.12) and declares Avalonia `12.1.4`; its model, controls, and settings dependencies resolve to `12.1.0.12`. This is the unsuffixed Avalonia 12 lane; the separately published `.v11` packages are not mixed in.

Sources/changelog: [Avalonia 12.1.4](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.4) (`2d79ba55fae1b1e71c69f93a668080c9029e6a31`) includes a fix for reattaching a child visual cleared on detach (#22334). Dock 12.1.0.8 added floating-window/nested-presenter lifetime fixes (#1161/#1162); 12.1.0.9 fixed Avalonia 12 top-level resolution across presentation hosts (#1165); 12.1.0.10 added cross-root docking support and document-preservation fixes (#1185, #1188–#1192); 12.1.0.12 itself is a JSON-generator fix. See the [Dock release notes](https://github.com/wieslawsoltes/Dock/releases).

The same package versions already appear in `agents/upgrade/u2-avalonia12` commit `2551a926879269236232fff1c8c943f0b0aaf8f2`, but that experiment started from Avalonia/Dock `11.2.0`, not this task's `11.3.12` / `11.3.12.1` base, and has no checked-in native-validation report. This run uses the exact `dee0a3600f8a8f14d269cd7c26afb6b73d3b499d` base and records the delta below. The base already contains the empty-floating-window close guard, so no separate Dock-close behavior patch was needed here.

## Migration changes

- Avalonia 12 defaults to compiled bindings. Existing app templates rely on runtime bindings and lack `x:DataType`; leaving the new default on caused XAML compilation errors. `AvaloniaUseCompiledBindingsByDefault=false` keeps the pre-upgrade binding contract rather than suppressing unrelated diagnostics.
- `ConstructorArgumentAttribute` is now imported from `Avalonia.Metadata`.
- Drag/drop migrates from `DataObject` / `DoDragDrop` to `DataTransfer` / `DoDragDropAsync`; the press args and dragged device must remain alive until the async drag finishes. A native synthetic device move to another line now works and is undoable.
- CheckBox `Checked`/`Unchecked` handlers become `IsCheckedChanged`.
- No Dock model/factory API compile breaks remained. Avalonia emits 20 deprecation warnings for existing `Watermark`, window decorations, and bitmap-save usage; these are warnings, not blockers, and were not broadened into unrelated cleanup.

## Exact-base comparison

Baseline worktree: detached `dee0a3600f8a8f14d269cd7c26afb6b73d3b499d` at `C:\projects\ai_work\agent-wt\etsfix-v2-baseline`. Candidate: `agents/etsfix/v2-avalonia12`, same base plus this experiment. Both app builds succeeded. Both `--smoke` and `--screenshot-selftest` passed; screenshot selftest produced a 1440×900 PNG. All six app-referencing console regression suites passed on both: `Knx.Docking.Tests`, `Knx.ParameterUi.Tests`, `Knx.CatalogUi.Tests`, `Knx.Icon.Tests`, `Knx.Monitor.Tests`, and `Knx.Shell.Tests` (each run with `dotnet run --project ...`).

Native harnesses used only a synthetic one-device project, isolated `BUSCHORD_SETTINGS`, an empty external catalogue directory, and the launched harness PID. The display was 1920×1200; Avalonia render scale was 1 (96-DPI equivalent). Input was real Win32 `SendInput` pointer movement, with the script refusing input unless the foreground PID matched its own process.

| Case | Exact base (`dee0a36`, 11.3.12 / 11.3.12.1) | Candidate (12.1.4 / 12.1.0.12) |
|---|---|---|
| Minimal standalone Tool float with a live view | Duplicate-parent crash | Pass; floating window created and closed |
| Minimal standalone Document float + factory move back | Pass | Pass |
| BusChord native Topology float, `(148,189) → (1650,550)` | Duplicate-parent crash | Same duplicate-parent crash |
| BusChord native Properties float, `(1279,189) → (1650,550)` | Duplicate-parent crash | Same duplicate-parent crash |
| BusChord native document float then drag back, `(837,190) → (1650,550)`, then `(1650,581) → (800,480)` | Content returned, two visible empty `HostWindow`s remained | Content returned, one visible empty `HostWindow` remained |
| BusChord factory document float/redock | Fatal CLR/LayoutManager `InvalidateArrange` during move | Explicit redock owner check passed; empty float closed; clean lifetime exit |
| Native synthetic device move from line 1.1 to 1.2 | No move; `CanUndo=False` | Device moved; `CanUndo=True` |

The app's Topology factory probe was timing-sensitive once on baseline (one pass, one failure); five repeated exact-base and five candidate runs all failed with the same duplicate-parent exception. Thus the repeated result is **5/5 failures on each**, not evidence of a newly introduced break. Native Topology and Properties gestures were repeatable failures on both. Tool redock could not be attempted after those crashes. Document redock is an improvement but still leaves one empty host in the candidate; host cleanup is not fully resolved.

## Commands and retained evidence

The comparison commands (run in each worktree) were:

```powershell
dotnet build src\Knx.App\Knx.App.csproj --no-restore
dotnet run --project src\Knx.App\Knx.App.csproj --no-build -- --smoke
dotnet run --project src\Knx.App\Knx.App.csproj --no-build -- --screenshot-selftest
dotnet run --project tests\Knx.Docking.Tests\Knx.Docking.Tests.csproj
dotnet run --project tests\Knx.ParameterUi.Tests\Knx.ParameterUi.Tests.csproj
dotnet run --project tests\Knx.CatalogUi.Tests\Knx.CatalogUi.Tests.csproj
dotnet run --project tests\Knx.Icon.Tests\Knx.Icon.Tests.csproj
dotnet run --project tests\Knx.Monitor.Tests\Knx.Monitor.Tests.csproj
dotnet run --project tests\Knx.Shell.Tests\Knx.Shell.Tests.csproj
```

Retained outside the repository at `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-v2-avalonia12\`:

- Candidate native crashes: `topology-final.log` / `topology-final-stderr.log`, `properties-final.log` / `properties-final-stderr.log`.
- Candidate document drag-back: `document-final.log`, `document-final-stderr.log`, `document-after-MainWindow-*.png`, and `document-after-HostWindow-*.png`.
- Exact-base native evidence: `baseline\topology.log` / `baseline\topology-stderr.log`, `baseline\properties.log` / `baseline\properties-stderr.log`, `baseline\document.log`, and baseline `*-before/after-*.png`.
- Minimal control: `simple\v2\live-tool.log`, `simple\v2\live-document-move.log`, `simple\baseline\live-tool.log`, `simple\baseline\live-document-move.log`.
- Factory move comparison: `factory-extra-console.log`, `factory\runtime.log`, and `baseline-factory-extra-console.log`.
- Native device move: `device-drag.log`; exact base: `baseline\device-drag.log`.
- Repeated Topology factory probes: `factory\topology-repeat-1..5.log` and `baseline\factory\topology-repeat-1..5.log`.

No bus/router, manufacturer catalogue, private project, or unrelated app process was used. No change was pushed or merged.
