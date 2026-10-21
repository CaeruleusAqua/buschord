using System.Collections.ObjectModel;
using System.Text.Json;
using Knx.Workbench;

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

/// <summary>
/// Demo-only programming queue. A successful simulation records a <see cref="DeviceProgrammingState"/> in the
/// versioned side file; no real bus write or download exists here and <see cref="ProgrammingJob.Origin"/> stays Demo.
/// </summary>
public sealed class ProgrammingSession : SessionState, IAsyncDisposable
{
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly Func<Project, DeviceInstance, FrozenConfiguration> _freeze;
    private readonly Func<ProgrammingJob, CancellationToken, Task> _execute;
    private readonly ObservableCollection<ProgrammingJob> _jobs = [];
    private readonly SnapshotCollection<DownloadedDeviceState> _downloaded = new();
    private readonly Dictionary<string, DeviceProgrammingState> _states = new(StringComparer.Ordinal);
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
    /// <summary>Rich per-device programming state, including failed attempts and partial loads.</summary>
    public IReadOnlyDictionary<string, DeviceProgrammingState> DeviceStates => _states;
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

    /// <summary>Signature drift or an incomplete load makes a device need programming.</summary>
    public bool NeedsProgramming(string deviceId)
    {
        VerifyAccess(); ThrowIfDisposed();
        var project = _project();
        var device = project is null ? null : ProjectQuery.DeviceList(project).FirstOrDefault(d => d.Id == deviceId);
        return device is not null && (!_states.TryGetValue(deviceId, out var state) ||
            state.NeedsProgramming(_freeze(project!, device).Signature));
    }

    /// <summary>The recorded state of one device; null when no attempt was recorded in this session.</summary>
    public DeviceProgrammingState? DeviceState(string deviceId)
    {
        VerifyAccess(); ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return _states.GetValueOrDefault(deviceId);
    }

    /// <summary>Project-check projection using this session's signature provider; it does not mutate the project.</summary>
    public IReadOnlyList<DeviceProgrammingStatus> CheckProgramming()
    {
        VerifyAccess(); ThrowIfDisposed();
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        return DeviceProgrammingReport.Check(project, _states, (p, d) => _freeze(p, d).Signature);
    }

    /// <summary>
    /// Clears only the flags invalidated by a classified change. Pure name/room classes are no-ops.
    /// Returns false when no state exists or no flag changed.
    /// </summary>
    public bool ResetForChange(string deviceId, DeviceChangeKind change)    {
        VerifyAccess(); ThrowIfDisposed();
        _ = DeviceProgrammingState.AffectedFlags(change);
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        if (ProjectQuery.DeviceList(project).All(d => d.Id != deviceId))
            throw new ArgumentException("Device is not in the current project.", nameof(deviceId));
        if (IsDeviceBusy(deviceId))
            throw new InvalidOperationException("Cannot reset programming state while the device is programming.");
        if (!_states.TryGetValue(deviceId, out var state)) return false;
        var reset = state.ResetForChange(change);
        if (reset == state) return false;
        _states[deviceId] = reset;
        return true;
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
                        var error = ex is OperationCanceledException ? "Cancelled; no success recorded."
                            : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                        RecordFailure(job, error);
                        job.Change(ProgrammingJobStatus.Failed, error);
                        continue;
                    }
                    finally { _activeDevice = null; OnPropertyChanged(nameof(ActiveDeviceId)); }
                    RecordSuccess(job);
                    job.Change(ProgrammingJobStatus.Done);
                    PublishDownloaded();
                }
            }
        }
        finally { _running = false; OnPropertyChanged(nameof(IsRunning)); }
    }

    private void RecordSuccess(ProgrammingJob job)
    {
        var state = _states.GetValueOrDefault(job.DeviceId) ?? DeviceProgrammingState.Empty(job.DeviceId);
        _states[job.DeviceId] = state.ApplySuccess(LoadFlags(job.Operation), job.Configuration.Signature,
            DateTimeOffset.UtcNow, job.InterfaceId, OriginOf(job.Origin));
    }

    private void RecordFailure(ProgrammingJob job, string error)
    {
        var state = _states.GetValueOrDefault(job.DeviceId) ?? DeviceProgrammingState.Empty(job.DeviceId);
        _states[job.DeviceId] = state.ApplyFailure(error, DateTimeOffset.UtcNow, job.InterfaceId, OriginOf(job.Origin));
    }

    // Only explicitly named partial operations are partial; an unknown label stays a full demo load.
    private static DeviceLoadFlags LoadFlags(string operation) => operation switch
    {
        "address" or "individual-address" => DeviceLoadFlags.IndividualAddress,
        "application" or "application-program" => DeviceLoadFlags.ApplicationProgram,
        "parameters" => DeviceLoadFlags.Parameters,
        "group-addresses" or "group-communication" => DeviceLoadFlags.GroupAddresses,
        "configuration" or "medium" => DeviceLoadFlags.Configuration,
        _ => DeviceLoadFlags.All,
    };

    // Real execution is not available; the mapping stays explicit for a future real transport.
    private static ProgrammingOrigin OriginOf(SessionOrigin origin) =>
        origin == SessionOrigin.Real ? ProgrammingOrigin.Real : ProgrammingOrigin.Demo;

    public void Cancel() { VerifyAccess(); _cancellation?.Cancel(); }

    /// <summary>Persist separately from project/undo/layout. Contains flags, hashes, timestamps, counters and error text, not payloads or keys.</summary>
    public string ExportDownloadedJson()
    {
        VerifyAccess(); ThrowIfDisposed();
        return DeviceProgrammingStates.Serialize(_project()?.Id, _states.Values);
    }

    public void ImportDownloadedJson(string json)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) throw new InvalidOperationException("Cannot replace programming state during programming.");
        var states = DeviceProgrammingStates.Parse(json, _project()?.Id);
        _states.Clear();
        foreach (var (deviceId, state) in states) _states[deviceId] = state;
        PublishDownloaded();
    }

    private void PublishDownloaded()
    {
        _downloaded.Replace(_states.Values
            .Where(s => s.LastSuccessAt is not null)
            .OrderBy(s => s.DeviceId, StringComparer.Ordinal)
            .Select(s => new DownloadedDeviceState(s.DeviceId, s.ImageHash!, s.LastSuccessAt!.Value,
                s.Origin == ProgrammingOrigin.Real ? SessionOrigin.Real : SessionOrigin.Demo)));
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
