# Workbench session API

Namespace: `Knx.App.Workbench`. Create **one `WorkbenchSessions` per open project** on
the UI synchronization context; keep it in application/view-model state, never a control.
Undo may replace the project instance with the same ID. Before switching project IDs,
explicitly stop/dispose the previous sessions (and offer recording export).

- Bind `Monitor.Rows` / `FilteredRows`, `IsRunning`, `Origin`, `LastError`.
  `Configure(mode, origin, interfaceId, bus)` / `Start()` / `Stop()` are explicit.
  A real adapter borrows an already connected `KnxBus`; it does not connect or send.
  `ConfigureReal(interfaceId, IMonitorTelegramSource)` is the receive-only test/adapter seam.
  Real hardware bus-monitor mode is rejected because the existing facade does not support it.
- An **application-owned UI timer**, independent of pane visibility, calls
  `TickDemo(WorkbenchTelegram)` only in demo mode and `FlushDisplay()` around 10 Hz.
  Real reception is thread-safe; observable collection events occur only in `FlushDisplay()`.
  Demo ticks cannot perform real writes. No async-void handlers or session-owned monitor timer.
- `PinFilter(MonitorFilter)` disables selection following. `SetFollowSelection(true, koId)`
  and `SelectionChanged(koId)` cover **all** linked GAs. The optional resolver returns
  GA IDs for a `MonitorFilter` (including room/external relations); `RefreshFilter()`
  reevaluates membership and receive-side address metadata after project edits.
  Missing KO selection matches nothing.
- Display is the latest 500 telegrams, not the recording. `ExportFilteredCsv()` exports
  the current filtered display; `ExportFullCsv(Stream)` copies the independent disk-backed
  full CSV stream. `ClearDisplay()` never clears the full recording. Explicit session
  disposal deletes its temporary recording; hiding/closing/recreating panes does not.
- Bind `Programming.Jobs` and `.Downloaded`. `Enqueue(deviceId, operation, interfaceId)`
  freezes `FrozenConfiguration(Signature, PayloadJson)`, then `await RunAsync()`.
  `Retry(job)` requeues the identical frozen job; call `RunAsync()` again.
  Every job/result is explicitly `Demo`; no real programming executor is exposed.
- Inject `freezeConfiguration(Project, DeviceInstance)` to include new domain extension
  metadata (activation, flags, sending GA, security). The default is a deterministic
  SHA-256 of **known native fields**, not an application image or full ETS fingerprint.
  It excludes names/building labels and includes referenced GA addresses/DPTs.
- Gate conflicting edits with `IsDeviceBusy` / `TryEnsureEditable` and block project
  Undo while `Programming.IsRunning`. These checks belong in shared edit commands.
  A failed/cancelled executor does not change downloaded comparison state. A successful
  demo result persists independently of project Undo. Use `NeedsProgramming(deviceId)`.
- Store `ExportDownloadedJson()` separately from project/undo/layout; restore using
  `ImportDownloadedJson`. The versioned/project-bound file contains hashes/timestamps/
  origin only, never frozen payloads or keys. Layout reset must not call these methods.
- Scan/other interface consumers use the **same** `Interfaces.TryAcquire(id, activity,
  out lease)` and dispose the lease. Bus monitor excludes every other consumer;
  scans/programming exclude each other. Group monitoring may coexist with them.
- UI-context mutators are verified. Without a synchronization context (console tests),
  the caller serializes mutations/awaits. Executors receive a cancellation token;
  application shutdown `await sessions.DisposeAsync()` cancels and joins the queue.

Checks: `dotnet run --project tests/Knx.WorkbenchSessions.Tests`.
