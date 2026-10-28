using System.Collections.ObjectModel;
using System.Text.Json;
using Knx.Workbench;

namespace Knx.App.Workbench;

public enum ProgrammingJobStatus { Queued, Running, Done, Failed, Cancelled }

/// <summary>Bus-relevant kind of a queued programming operation.</summary>
public enum ProgrammingJobType { Full, IndividualAddress, ApplicationProgram, Parameters, GroupAddresses, Configuration }

public sealed class ProgrammingJob : SessionState
{
    internal ProgrammingJob(string deviceId, string address, string name, string operation,
        string interfaceId, FrozenConfiguration configuration, SessionOrigin origin)
    {
        DeviceId = deviceId; Address = address; Name = name; Operation = operation;
        InterfaceId = interfaceId; Configuration = configuration; Origin = origin;
        Type = TypeOf(operation);
    }
    public Guid Id { get; } = Guid.NewGuid();
    public string DeviceId { get; }
    public string Address { get; }
    public string Name { get; }
    public string Operation { get; }
    public ProgrammingJobType Type { get; }
    public string InterfaceId { get; }
    /// <summary>Execution source. Real jobs are rejected at enqueue; execution is demo-only.</summary>
    public SessionOrigin Origin { get; }
    public FrozenConfiguration Configuration { get; }
    public ProgrammingJobStatus Status { get; private set; }
    public string? Error { get; private set; }
    public int Attempts { get; private set; }
    /// <summary>Executor-reported percentage of the current attempt, 0–100.</summary>
    public int Progress { get; private set; }

    /// <summary>
    /// Called by the owning executor while the job runs. Executors receive the queue's context, so
    /// continuations report there; a background thread must marshal to that context first.
    /// </summary>
    public void ReportProgress(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (Status != ProgrammingJobStatus.Running || Progress == percent) return;
        Progress = percent;
        OnPropertyChanged(nameof(Progress));
    }

    internal void Change(ProgrammingJobStatus status, string? error = null)
    {
        Status = status; Error = error;
        if (status == ProgrammingJobStatus.Running) { Attempts++; OnPropertyChanged(nameof(Attempts)); Progress = 0; }
        if (status is ProgrammingJobStatus.Queued or ProgrammingJobStatus.Done) Progress = status == ProgrammingJobStatus.Done ? 100 : 0;
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(Progress));
    }

    internal static ProgrammingJobType TypeOf(string operation) => operation switch
    {
        "address" or "individual-address" => ProgrammingJobType.IndividualAddress,
        "application" or "application-program" => ProgrammingJobType.ApplicationProgram,
        "parameters" => ProgrammingJobType.Parameters,
        "group-addresses" or "group-communication" => ProgrammingJobType.GroupAddresses,
        "configuration" or "medium" => ProgrammingJobType.Configuration,
        _ => ProgrammingJobType.Full,
    };
}

public sealed record DownloadedDeviceState(string DeviceId, string Signature, DateTimeOffset At, SessionOrigin Origin);

/// <summary>
/// Programming queue. A successful simulation records a <see cref="DeviceProgrammingState"/> in the
/// versioned side file; no real bus write or download lives here. Real jobs require an explicitly
/// injected executor, so <see cref="ProgrammingJob.Origin"/> is always honest.
/// </summary>
public sealed class ProgrammingSession : SessionState, IAsyncDisposable
{
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly Func<Project, DeviceInstance, FrozenConfiguration> _freeze;
    private readonly Func<ProgrammingJob, CancellationToken, Task> _execute;
    private readonly Func<ProgrammingJob, CancellationToken, Task>? _realExecute;
    private readonly ObservableCollection<ProgrammingJob> _jobs = [];
    private readonly SnapshotCollection<DownloadedDeviceState> _downloaded = new();
    private readonly Dictionary<string, DeviceProgrammingState> _states = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _jobCancellation;
    private ProgrammingJob? _activeJob;
    private Task _run = Task.CompletedTask;
    private bool _running, _disposed;
    private string? _activeDevice;

    public ProgrammingSession(InterfaceCoordinator interfaces, Func<Project?> project,
        Func<Project, DeviceInstance, FrozenConfiguration>? freezeConfiguration = null,
        Func<ProgrammingJob, CancellationToken, Task>? demoExecutor = null,
        Func<ProgrammingJob, CancellationToken, Task>? realExecutor = null)
    {
        _interfaces = interfaces; _project = new SessionProject(project).Get;
        _freeze = freezeConfiguration ?? DeviceConfigurationSnapshot.Freeze;
        _execute = demoExecutor ?? ((_, token) => Task.Delay(150, token));
        _realExecute = realExecutor;
        Jobs = new(_jobs); Downloaded = new(_downloaded);
    }

    public ReadOnlyObservableCollection<ProgrammingJob> Jobs { get; }
    public ReadOnlyObservableCollection<DownloadedDeviceState> Downloaded { get; }
    /// <summary>Rich per-device programming state, including failed attempts and partial loads.</summary>
    public IReadOnlyDictionary<string, DeviceProgrammingState> DeviceStates => _states;
    public bool IsRunning => _running;
    public string? ActiveDeviceId => _activeDevice;
    public ProgrammingJob? ActiveJob => _activeJob;
    /// <summary>True only when the host supplied an explicit real executor; no transport lives in this session.</summary>
    public bool RealExecutionConfigured => _realExecute is not null;
    public event EventHandler? DownloadedChanged;

    public ProgrammingJob Enqueue(string deviceId, string operation = "full", string interfaceId = "demo-ip",
        SessionOrigin origin = SessionOrigin.Demo)
    {
        VerifyAccess(); ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation); ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        if (origin == SessionOrigin.Real && _realExecute is null)
            throw new NotSupportedException("No real programming executor is configured; no bus write or download is performed.");
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var device = ProjectQuery.DeviceList(project).SingleOrDefault(d => d.Id == deviceId)
            ?? throw new ArgumentException("Device is not in the current project.", nameof(deviceId));
        var frozen = _freeze(project, device);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozen.Signature);
        using var validation = JsonDocument.Parse(frozen.PayloadJson);
        var job = new ProgrammingJob(deviceId, device.Address.ToString(), device.Name ?? deviceId,
            operation, interfaceId, frozen, origin);
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
        PublishDownloaded();
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
        ArgumentNullException.ThrowIfNull(job);
        if (IsRunning) throw new InvalidOperationException("Wait for the current queue run before retrying.");
        if (!_jobs.Contains(job) || job.Status is not (ProgrammingJobStatus.Failed or ProgrammingJobStatus.Cancelled))
            throw new InvalidOperationException("Only a failed or cancelled job from this session can be retried.");
        job.Change(ProgrammingJobStatus.Queued);
    }

    /// <summary>Removes done, failed and cancelled jobs; queued/running work is retained.</summary>
    public int ClearHistory()
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) throw new InvalidOperationException("Wait for the current queue run before clearing history.");
        var removed = 0;
        for (var i = _jobs.Count - 1; i >= 0; i--)
        {
            if (_jobs[i].Status is ProgrammingJobStatus.Queued or ProgrammingJobStatus.Running) continue;
            _jobs.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    /// <summary>Cancels one queued or running job. The queue continues with the next queued job.</summary>
    public bool CancelJob(ProgrammingJob job)
    {
        VerifyAccess(); ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(job);
        if (!_jobs.Contains(job)) throw new InvalidOperationException("Job is not in this session.");
        if (job.Status == ProgrammingJobStatus.Queued)
        {
            job.Change(ProgrammingJobStatus.Cancelled, "Cancelled before execution; no success recorded.");
            return true;
        }
        if (job.Status == ProgrammingJobStatus.Running && ReferenceEquals(_activeJob, job))
        {
            _jobCancellation?.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>Cancels every queued job; a running job is unaffected.</summary>
    public int CancelQueued()
    {
        VerifyAccess(); ThrowIfDisposed();
        var cancelled = 0;
        foreach (var job in _jobs.Where(j => j.Status == ProgrammingJobStatus.Queued).ToArray())
        {
            job.Change(ProgrammingJobStatus.Cancelled, "Cancelled before execution; no success recorded.");
            cancelled++;
        }
        return cancelled;
    }

    /// <summary>Cancels the whole queue run. The running job becomes Cancelled and queued jobs stay queued.</summary>
    public void Cancel()
    {
        VerifyAccess(); ThrowIfDisposed();
        _cancellation?.Cancel();
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
                    _activeJob = job;
                    _activeDevice = job.DeviceId;
                    OnPropertyChanged(nameof(ActiveJob)); OnPropertyChanged(nameof(ActiveDeviceId));
                    using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    _jobCancellation = jobCancellation;
                    job.Change(ProgrammingJobStatus.Running);
                    try
                    {
                        var execute = job.Origin == SessionOrigin.Real ? _realExecute! : _execute;
                        await execute(job, jobCancellation.Token);
                        jobCancellation.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        job.Change(ProgrammingJobStatus.Cancelled, "Queue cancelled; no success recorded.");
                        PublishDownloaded();
                        break;
                    }
                    catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
                    {
                        job.Change(ProgrammingJobStatus.Cancelled, "Job cancelled; no success recorded.");
                        PublishDownloaded();
                        continue;
                    }
                    catch (Exception ex)
                    {
                        var error = ex is OperationCanceledException ? "Cancelled; no success recorded."
                            : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                        RecordFailure(job, error);
                        job.Change(ProgrammingJobStatus.Failed, error);
                        PublishDownloaded();
                        continue;
                    }
                    finally
                    {
                        _jobCancellation = null;
                        _activeJob = null;
                        _activeDevice = null;
                        OnPropertyChanged(nameof(ActiveJob)); OnPropertyChanged(nameof(ActiveDeviceId));
                    }
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
        _states[job.DeviceId] = state.ApplySuccess(LoadFlags(job.Type), job.Configuration.Signature,
            DateTimeOffset.UtcNow, job.InterfaceId, OriginOf(job.Origin));
    }

    private void RecordFailure(ProgrammingJob job, string error)
    {
        var state = _states.GetValueOrDefault(job.DeviceId) ?? DeviceProgrammingState.Empty(job.DeviceId);
        _states[job.DeviceId] = state.ApplyFailure(error, DateTimeOffset.UtcNow, job.InterfaceId, OriginOf(job.Origin));
    }

    // Only explicitly named partial operations are partial; an unknown label stays a full demo load.
    private static DeviceLoadFlags LoadFlags(ProgrammingJobType type) => type switch
    {
        ProgrammingJobType.IndividualAddress => DeviceLoadFlags.IndividualAddress,
        ProgrammingJobType.ApplicationProgram => DeviceLoadFlags.ApplicationProgram,
        ProgrammingJobType.Parameters => DeviceLoadFlags.Parameters,
        ProgrammingJobType.GroupAddresses => DeviceLoadFlags.GroupAddresses,
        ProgrammingJobType.Configuration => DeviceLoadFlags.Configuration,
        _ => DeviceLoadFlags.All,
    };

    // A real origin is only reachable through an explicitly injected executor; the mapping stays honest.
    private static ProgrammingOrigin OriginOf(SessionOrigin origin) =>
        origin == SessionOrigin.Real ? ProgrammingOrigin.Real : ProgrammingOrigin.Demo;

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
