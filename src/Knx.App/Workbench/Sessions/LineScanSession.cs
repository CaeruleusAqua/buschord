using System.Collections.ObjectModel;

namespace Knx.App.Workbench;

/// <summary>One-shot simulated line scan; no transport or device write is opened.</summary>
public sealed class LineScanSession : SessionState
{
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly ObservableCollection<LineScanResult> _results = [];
    private bool _running;
    private string? _lastError;

    public LineScanSession(InterfaceCoordinator interfaces, Func<Project?> project)
    {
        _interfaces = interfaces;
        _project = new SessionProject(project).Get;
        Results = new(_results);
    }

    public ReadOnlyObservableCollection<LineScanResult> Results { get; }
    public bool IsRunning => _running;
    public string? LastError => _lastError;

    public async Task RunDemoAsync(string lineId, string interfaceId = "demo-ip", CancellationToken ct = default)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);
        if (_running) throw new InvalidOperationException("A scan is already running.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var line = project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines)
            .SingleOrDefault(l => l.Id == lineId)
            ?? throw new ArgumentException("Line is not in the current project.", nameof(lineId));
        if (!_interfaces.TryAcquire(interfaceId, InterfaceActivity.Scan, out var lease))
            throw new InvalidOperationException("The interface is occupied by a monitor or programming job.");
        _running = true; _lastError = null; _results.Clear();
        OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
        try
        {
            var devices = line.Devices.Concat(line.Segments.SelectMany(s => s.Devices))
                .OrderBy(d => d.Address.Device).ToList();
            if (devices.Count == 0)
                _results.Add(new("—", "Keine Geräte auf dieser Linie", "leer", "Demo-Scan; keine Busaktion"));
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(20, ct);
                _results.Add(new(device.Address.ToString(), device.Name ?? "(ohne Namen)",
                    "ok", "Demo-Scan; keine reale Geräteantwort", SessionOrigin.Demo));
            }
        }
        catch (OperationCanceledException) { _lastError = "Scan abgebrochen; keine realen Geräte geprüft."; }
        finally
        {
            lease?.Dispose();
            _running = false;
            OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
        }
    }
}

public sealed record LineScanResult(string Address, string Name, string Status, string Detail, SessionOrigin Origin = SessionOrigin.Demo);
