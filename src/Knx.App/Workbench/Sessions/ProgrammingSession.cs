using System.Collections.ObjectModel;
using System.Text.Json;

namespace Knx.App.Workbench;

public enum ProgrammingJobStatus { Queued, Running, Done, Failed }
public sealed class ProgrammingJob : SessionState
{
    internal ProgrammingJob(string deviceId, string address, string name, string operation,
        string interfaceId, FrozenConfiguration configuration)
    {
        DeviceId = deviceId; Address = address; Name = name; Operation = operation;
        InterfaceId = interfaceId; Configuration = configuration;
    }
    public Guid Id { get; } = Guid.NewGuid();
    public string DeviceId { get; }
    public string Address { get; }
    public string Name { get; }
    public string Operation { get; }
    public string InterfaceId { get; }
    public SessionOrigin Origin => SessionOrigin.Demo;
    public FrozenConfiguration Configuration { get; }
    public ProgrammingJobStatus Status { get; private set; }
    public string? Error { get; private set; }
    public int Attempts { get; private set; }
    internal void Change(ProgrammingJobStatus status, string? error = null)
    {
        Status = status; Error = error;
        if (status == ProgrammingJobStatus.Running) { Attempts++; OnPropertyChanged(nameof(Attempts)); }
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Error));
    }
}

public sealed record DownloadedDeviceState(string DeviceId, string Signature, DateTimeOffset At, SessionOrigin Origin);

/// <summary>Only demo execution is enabled here. Success means simulated, never a real device download.</summary>
public sealed class ProgrammingSession : SessionState, IAsyncDisposable
{
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly Func<Project, DeviceInstance, FrozenConfiguration> _freeze;
    private readonly Func<ProgrammingJob, CancellationToken, Task> _execute;
    private readonly ObservableCollection<ProgrammingJob> _jobs = [];
    private readonly SnapshotCollection<DownloadedDeviceState> _downloaded = new();
    private readonly Dictionary<string, DownloadedDeviceState> _states = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task _run = Task.CompletedTask;
    private bool _running, _disposed;
    private string? _activeDevice;

    public ProgrammingSession(InterfaceCoordinator interfaces, Func<Project?> project,
        Func<Project, DeviceInstance, FrozenConfiguration>? freezeConfiguration = null,
        Func<ProgrammingJob, CancellationToken, Task>? demoExecutor = null)
    {
        _interfaces = interfaces; _project = new SessionProject(project).Get;
        _freeze = freezeConfiguration ?? DeviceConfigurationSnapshot.Freeze;
        _execute = demoExecutor ?? ((_, token) => Task.Delay(150, token));
        Jobs = new(_jobs); Downloaded = new(_downloaded);
    }

    public ReadOnlyObservableCollection<ProgrammingJob> Jobs { get; }
    public ReadOnlyObservableCollection<DownloadedDeviceState> Downloaded { get; }
    public bool IsRunning => _running;
    public string? ActiveDeviceId => _activeDevice;
    public event EventHandler? DownloadedChanged;

    public ProgrammingJob Enqueue(string deviceId, string operation = "full", string interfaceId = "demo-ip")
    {
        VerifyAccess(); ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation); ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var device = ProjectQuery.DeviceList(project).SingleOrDefault(d => d.Id == deviceId)
            ?? throw new ArgumentException("Device is not in the current project.", nameof(deviceId));
        var frozen = _freeze(project, device);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozen.Signature);
        using var validation = JsonDocument.Parse(frozen.PayloadJson);
        var job = new ProgrammingJob(deviceId, device.Address.ToString(), device.Name ?? deviceId,
            operation, interfaceId, frozen);
        _jobs.Add(job);
        return job;
    }

    public bool NeedsProgramming(string deviceId)
    {
        VerifyAccess(); ThrowIfDisposed();
        var project = _project();
        var device = project is null ? null : ProjectQuery.DeviceList(project).FirstOrDefault(d => d.Id == deviceId);
        return device is not null && (!_states.TryGetValue(deviceId, out var state) ||
            state.Signature != _freeze(project!, device).Signature);
    }

    public bool IsDeviceBusy(string deviceId) => IsRunning && ActiveDeviceId == deviceId;
    public bool TryEnsureEditable(string deviceId, out string? reason)
    {
        VerifyAccess();
        reason = IsDeviceBusy(deviceId) ? "The device is protected while its programming job is running." : null;
        return reason is null;
    }

    public void Retry(ProgrammingJob job)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) throw new InvalidOperationException("Wait for the current queue run before retrying.");
        if (!_jobs.Contains(job) || job.Status != ProgrammingJobStatus.Failed)
            throw new InvalidOperationException("Only a failed job from this session can be retried.");
        job.Change(ProgrammingJobStatus.Queued);
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) return _run;
        _ = _project() ?? throw new InvalidOperationException("No project is open.");
        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true; OnPropertyChanged(nameof(IsRunning));
        _run = RunCoreAsync(_cancellation.Token);
        return _run;
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested &&
                _jobs.FirstOrDefault(j => j.Status == ProgrammingJobStatus.Queued) is { } job)
            {
                if (!_interfaces.TryAcquire(job.InterfaceId, InterfaceActivity.Programming, out var lease))
                    throw new InvalidOperationException("The interface is occupied by a conflicting operation; queued jobs were retained.");
                using (lease)
                {
                    _activeDevice = job.DeviceId; OnPropertyChanged(nameof(ActiveDeviceId));
                    job.Change(ProgrammingJobStatus.Running);
                    try
                    {
                        await _execute(job, token);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex)
                    {
                        job.Change(ProgrammingJobStatus.Failed, ex is OperationCanceledException ? "Cancelled; no success recorded." : ex.Message);
                        continue;
                    }
                    finally { _activeDevice = null; OnPropertyChanged(nameof(ActiveDeviceId)); }
                    _states[job.DeviceId] = new(job.DeviceId, job.Configuration.Signature, DateTimeOffset.UtcNow, SessionOrigin.Demo);
                    job.Change(ProgrammingJobStatus.Done);
                    PublishDownloaded();
                }
            }
        }
        finally { _running = false; OnPropertyChanged(nameof(IsRunning)); }
    }

    public void Cancel() { VerifyAccess(); _cancellation?.Cancel(); }

    /// <summary>Persist separately from project/undo/layout. Contains signatures, not configuration payloads or keys.</summary>
    public string ExportDownloadedJson()
    {
        VerifyAccess(); ThrowIfDisposed();
        return JsonSerializer.Serialize(new ComparisonFile(1, _project()?.Id,
            _states.Values.OrderBy(s => s.DeviceId, StringComparer.Ordinal).ToArray()));
    }

    public void ImportDownloadedJson(string json)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) throw new InvalidOperationException("Cannot replace comparison state during programming.");
        var file = JsonSerializer.Deserialize<ComparisonFile>(json) ?? throw new FormatException("Missing comparison state.");
        if (file.Version != 1 || file.ProjectId != _project()?.Id || file.Devices is null ||
            file.Devices.Any(d => d is null || string.IsNullOrWhiteSpace(d.DeviceId) || string.IsNullOrWhiteSpace(d.Signature) ||
                !Enum.IsDefined(d.Origin)) || file.Devices.Select(d => d.DeviceId).Distinct().Count() != file.Devices.Length)
            throw new FormatException("Invalid comparison file or project identity.");
        _states.Clear();
        foreach (var state in file.Devices) _states.Add(state.DeviceId, state);
        PublishDownloaded();
    }

    private sealed record ComparisonFile(int Version, string? ProjectId, DownloadedDeviceState[] Devices);
    private void PublishDownloaded()
    {
        _downloaded.Replace(_states.Values.OrderBy(s => s.DeviceId, StringComparer.Ordinal));
        DownloadedChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public async ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true; _cancellation?.Cancel();
        try { await _run; }
        finally { _cancellation?.Dispose(); }
    }
}
