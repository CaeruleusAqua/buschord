using System.Collections.ObjectModel;
using Knx.App.Localization;

namespace Knx.App.Workbench;

/// <summary>One-shot simulated line or device scan; no transport or device write is opened.</summary>
public sealed class LineScanSession : SessionState
{
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly ObservableCollection<LineScanResult> _results = [];
    private bool _running;
    private string? _lastError;
    private string? _lastLineId;
    private DateTimeOffset? _lastScanAt;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        if (_running) throw new InvalidOperationException("A scan is already running.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var line = project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines)
            .SingleOrDefault(l => l.Id == lineId)
            ?? throw new ArgumentException("Line is not in the current project.", nameof(lineId));
        var devices = line.Devices.Concat(line.Segments.SelectMany(s => s.Devices))
            .OrderBy(d => d.Address.Device).Select(d => (Device: d, LineId: (string?)line.Id)).ToList();
        await RunDemoCoreAsync(devices, line.Id, interfaceId, ct);
    }

    /// <summary>Simulated scan of one project device, reported with its individual address and ids.</summary>
    public async Task RunDemoDeviceAsync(string deviceId, string interfaceId = "demo-ip", CancellationToken ct = default)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (_running) throw new InvalidOperationException("A scan is already running.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var located = ProjectQuery.Devices(project).FirstOrDefault(d => d.Device.Id == deviceId);
        if (located.Device is null) throw new ArgumentException("Device is not in the current project.", nameof(deviceId));
        await RunDemoCoreAsync([(located.Device, located.Line?.Id)], located.Line?.Id, interfaceId, ct);
    }

    private async Task RunDemoCoreAsync(IReadOnlyList<(DeviceInstance Device, string? LineId)> devices,
        string? lineId, string interfaceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!_interfaces.TryAcquire(interfaceId, InterfaceActivity.Scan, out var lease))
            throw new InvalidOperationException("The interface is occupied by a monitor or programming job.");
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
            lease?.Dispose();
            _running = false;
            OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
            OnPropertyChanged(nameof(LastScanAt));
        }
    }
}

/// <summary>One simulated scan observation. Timestamp, project device id and line id make it bindable.</summary>
public sealed record LineScanResult(string Address, string Name, string Status, string Detail,
    SessionOrigin Origin = SessionOrigin.Demo, DateTimeOffset Timestamp = default,
    string? DeviceId = null, string? LineId = null);
