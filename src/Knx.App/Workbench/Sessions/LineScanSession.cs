using System.Collections.ObjectModel;
using Knx.App.Localization;
using Knx.Bus;
using Knx.Bus.Connections;

namespace Knx.App.Workbench;

/// <summary>Explicit demo or read-only real scans; the owner cancels and joins active work before disposal.</summary>
public sealed class LineScanSession : SessionState, IAsyncDisposable
{
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly ObservableCollection<LineScanResult> _results = [];
    private bool _running;
    private string? _lastError;
    private string? _lastLineId;
    private DateTimeOffset? _lastScanAt;
    private CancellationTokenSource? _cancellation;
    private Task? _operation;
    private bool _disposed;
    private object? _runIdentity;
    public int CompletedTargets { get; private set; }
    public int TotalTargets { get; private set; }

    public LineScanSession(InterfaceCoordinator interfaces, Func<Project?> project)
    {
        _interfaces = interfaces;
        _project = new SessionProject(project).Get;
        Results = new(_results);
    }

    public ReadOnlyObservableCollection<LineScanResult> Results { get; }
    public bool IsRunning => _running;
    public string? LastError => _lastError;
    /// <summary>Line of the most recent scan; null before the first run or when the device has no line.</summary>
    public string? LastLineId => _lastLineId;
    public DateTimeOffset? LastScanAt => _lastScanAt;

    public async Task RunDemoAsync(string lineId, string interfaceId = "demo-ip", CancellationToken ct = default)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        if (_running) throw new InvalidOperationException("A scan is already running.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var line = project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines)
            .SingleOrDefault(l => l.Id == lineId)
            ?? throw new ArgumentException("Line is not in the current project.", nameof(lineId));
        var devices = line.Devices.Concat(line.Segments.SelectMany(s => s.Devices))
            .OrderBy(d => d.Address.Device).Select(d => (Device: d, LineId: (string?)line.Id)).ToList();
        await StartOwned(() => RunDemoCoreAsync(devices, line.Id, interfaceId, ct));
    }

    /// <summary>Simulated scan of one project device, reported with its individual address and ids.</summary>
    public async Task RunDemoDeviceAsync(string deviceId, string interfaceId = "demo-ip", CancellationToken ct = default)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (_running) throw new InvalidOperationException("A scan is already running.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var located = ProjectQuery.Devices(project).FirstOrDefault(d => d.Device.Id == deviceId);
        if (located.Device is null) throw new ArgumentException("Device is not in the current project.", nameof(deviceId));
        await StartOwned(() => RunDemoCoreAsync([(located.Device, located.Line?.Id)], located.Line?.Id, interfaceId, ct));
    }

    private async Task RunDemoCoreAsync(IReadOnlyList<(DeviceInstance Device, string? LineId)> devices,
        string? lineId, string interfaceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!_interfaces.TryAcquire(interfaceId, InterfaceActivity.Scan, out var lease))
            throw new InvalidOperationException("The interface is occupied by a monitor or programming job.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancellation = cancellation; ct = cancellation.Token;
        _running = true; _lastError = null; _lastLineId = lineId; _lastScanAt = null;
        _results.Clear();
        OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(LastLineId)); OnPropertyChanged(nameof(LastScanAt));
        try
        {
            if (devices.Count == 0)
                _results.Add(new("—", WorkbenchStrings.Get("ScanNoDevices"), "leer",
                    WorkbenchStrings.Get("ScanDetailEmpty"), SessionOrigin.Demo, DateTimeOffset.Now, null, lineId));
            foreach (var (device, deviceLineId) in devices)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(20, ct);
                _results.Add(new(device.Address.ToString(), device.Name ?? WorkbenchStrings.Get("Unnamed"),
                    "ok", WorkbenchStrings.Get("ScanDetailDemo"), SessionOrigin.Demo, DateTimeOffset.Now,
                    device.Id, deviceLineId));
            }
            _lastScanAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) { _lastError = WorkbenchStrings.Get("ScanCancelled"); }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            lease?.Dispose();
            _running = false;
            OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
            OnPropertyChanged(nameof(LastScanAt));
        }
    }

    public Task RunRealAsync(string lineId, ReadOnlyLineScanPlan plan, ReadOnlyLineScanner? scanner = null, CancellationToken ct = default)
        => RunRealAsyncCore(lineId, plan, scanner, null, null, ct);
    public Task RunProfileAsync(string lineId, ReadOnlyLineScanPlan plan, KnxConnectionProfile profile, KnxConnectionFactory? factory = null, CancellationToken ct = default)
    {
        if (!plan.Gateway.Equals(profile.Gateway) || plan.RequestedTunnelAddress != profile.RequestedAddress)
            throw new ArgumentException("The scan must use the selected connection profile.", nameof(profile));
        return RunRealAsyncCore(lineId, plan, null, profile, factory ?? new(), ct);
    }
    private Task RunRealAsyncCore(string lineId, ReadOnlyLineScanPlan plan, ReadOnlyLineScanner? scanner,
        KnxConnectionProfile? profile, KnxConnectionFactory? factory, CancellationToken ct)
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("A scan is already running.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        if (!project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).Any(l => l.Id == lineId))
            throw new ArgumentException("Line is not in the current project.", nameof(lineId));
        return StartOwned(() => RunRealCoreAsync(project, lineId, plan, scanner ?? new(), profile, factory, ct));
    }
    private Task StartOwned(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _ = RunOwnedAsync(action, completion);
        return completion.Task;
    }
    private static async Task RunOwnedAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action(); completion.TrySetResult(); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async Task RunRealCoreAsync(Project project, string lineId, ReadOnlyLineScanPlan plan, ReadOnlyLineScanner scanner,
        KnxConnectionProfile? profile, KnxConnectionFactory? factory, CancellationToken ct)
    {
        using var reservation = profile is null ? RealEndpointReservations.TryAcquire(plan.InterfaceId)
            ?? throw new InvalidOperationException(LiveConnectionStrings.Get("ScanDisconnect")) : null;
        if (!_interfaces.TryAcquire(plan.InterfaceId, InterfaceActivity.Scan, out var lease))
            throw new InvalidOperationException(LiveConnectionStrings.Get("Occupied"));
        using (lease)
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            _cancellation = cancellation; _running = true; _lastError = null; _lastLineId = lineId; _lastScanAt = null;
            var run = _runIdentity = new object(); CompletedTargets = 0; TotalTargets = plan.Targets.Count;
            _results.Clear(); Notify();
            var names = ProjectQuery.DeviceList(project).GroupBy(d => d.Address).ToDictionary(g => g.Key, g => (g.First().Id, g.First().Name));
            try
            {
                await using var operation = profile is null ? null : await factory!.AcquireOperationAsync(profile, cancellation.Token);
                if (operation is not null) scanner = new ReadOnlyLineScanner((_, target, token) => operation.OpenManagementAsync(target, readOnly: true, token));
                var progress = new Progress<ReadOnlyLineScanProgress>(value =>
                {
                    if (_disposed || !_running || !ReferenceEquals(run, _runIdentity) || !SameProject(project)) return;
                    CompletedTargets = value.Completed; OnPropertyChanged(nameof(CompletedTargets));
                });
                var result = await scanner.ScanAsync(plan, progress, cancellation.Token);
                if (_disposed) return;
                if (!SameProject(project)) { _lastError = LiveConnectionStrings.Get("StaleProject"); return; }
                CompletedTargets = result.Observations.Count(o => o.State != ReadOnlyLineScanState.NotProbed);
                foreach (var observation in result.Observations)
                {
                    var known = names.GetValueOrDefault(observation.Address);
                    _results.Add(new(observation.Address.ToString(), known.Name ?? "", LiveConnectionStrings.Get(observation.State.ToString()),
                        observation.MaskVersion is { } mask ? $"0x{mask:X4}" : LiveConnectionStrings.Get(observation.State.ToString()),
                        SessionOrigin.Real, observation.ObservedAtUtc ?? DateTimeOffset.UtcNow, known.Id, lineId));
                }
                _lastScanAt = result.FinishedAtUtc;
                if (result.Completion == ReadOnlyLineScanCompletion.Cancelled) _lastError = LiveConnectionStrings.Get("ScanCancelled");
                else if (result.Completion != ReadOnlyLineScanCompletion.Completed) _lastError = LiveConnectionStrings.Get("Failed");
            }
            catch (KnxConnectionException error) { _lastError = SecureConnectionStrings.Get(error.Failure.ToString()); }
            catch (OperationCanceledException) { _lastError = LiveConnectionStrings.Get("ScanCancelled"); }
            finally
            {
                _running = false;
                _runIdentity = null;
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                Notify();
            }
        }
        void Notify()
        {
            OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
            OnPropertyChanged(nameof(LastLineId)); OnPropertyChanged(nameof(LastScanAt));
            OnPropertyChanged(nameof(CompletedTargets)); OnPropertyChanged(nameof(TotalTargets));
        }
    }
    private bool SameProject(Project expected)
    { try { return ReferenceEquals(expected, _project()); } catch (InvalidOperationException) { return false; } }
    public void Cancel() { VerifyAccess(); _cancellation?.Cancel(); }
    public async ValueTask DisposeAsync()
    {
        VerifyAccess(); _disposed = true; _cancellation?.Cancel();
        if (_operation is { } operation)
            try { await operation; } catch { /* Completed scan errors do not bypass joined cleanup or prevent window close. */ }
    }
}

/// <summary>One simulated scan observation. Timestamp, project device id and line id make it bindable.</summary>
public sealed record LineScanResult(string Address, string Name, string Status, string Detail,
    SessionOrigin Origin = SessionOrigin.Demo, DateTimeOffset Timestamp = default,
    string? DeviceId = null, string? LineId = null);
