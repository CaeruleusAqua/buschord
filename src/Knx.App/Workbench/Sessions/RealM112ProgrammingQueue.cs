using System.Security.Cryptography;
using System.Text.Json;
using Knx.Bus;

namespace Knx.App.Workbench;

public enum RealM112JobState { Reviewed, Ready, Running, Restoring, Verified, VerifiedStale, VerifiedUnrecorded, Restored, RecoveryRequired, Blocked, Cancelled }
public sealed record RealM112JobView(Guid Id, string DeviceId, string Address, string Name, RealM112JobState State,
    string? Fingerprint, ManagementDeviceIdentity? Identity, bool Confirmed, string? Issue, M112DownloadResult? Result,
    M112CommissioningPhase Phase, IReadOnlyList<M112ReviewRange> Ranges, string EndpointId, string TunnelAddress, string BaselineFingerprint, string JournalRoot);
public sealed record RealM112QueueView(bool Busy, bool RecoveryRequired, bool CancelRequested, string BatchFingerprint,
    IReadOnlyList<RealM112JobView> Jobs, string? Issue);
public sealed record RealM112RecoveryEvidence(Guid Job, string Address, string? Path, string? FileFingerprint, bool Available);
public sealed record RealM112ExitReview(string Fingerprint, IReadOnlyList<RealM112RecoveryEvidence> Evidence);

/// <summary>Explicit serial composition of the existing reviewed M112 session; never opens a transport at enqueue.</summary>
public sealed class RealM112ProgrammingQueue : SessionState, IAsyncDisposable
{
    private sealed class Job(Project project, string deviceId, string address, string name, FrozenConfiguration configuration,
        M112CommissioningSession session, IM112CommissioningBackend backend, ReadOnlyConnectionOptions options, string baselinePath, string baselineHash, string journal)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal Project Project = project;
        internal string ModelHash = ProjectRevisions.ContentHash(project);
        internal string SourceHash = VerifiedM112ReceiptStore.ModelFingerprint(project);
        internal string DeviceId = deviceId, Address = address, Name = name, BaselinePath = baselinePath, BaselineHash = baselineHash, Journal = journal;
        internal FrozenConfiguration Configuration = configuration;
        internal M112CommissioningSession Session = session;
        internal IM112CommissioningBackend Backend = backend;
        internal ReadOnlyConnectionOptions Options = options;
        internal RealM112JobState State = RealM112JobState.Reviewed;
        internal bool Confirmed;
        internal string? Issue, Audit;
        internal RealM112JobView View() { var s = Session.Snapshot; return new(Id, DeviceId, Address, Name, State, s.Fingerprint,
            s.Identity, Confirmed, Issue ?? s.Issue ?? s.ConnectionFailure?.ToString(), s.Result, s.Phase, Session.Review?.Ranges ?? [],
            Options.EndpointId, Options.RequestedAddress.ToString(), BaselineHash, Journal); }
    }
    private readonly Func<M112LiveProject> _current;
    private readonly Func<string, IM112CommissioningBackend> _backend;
    private readonly ProgrammingSession _programming;
    private readonly VerifiedM112ReceiptStore? _receipts;
    private readonly object _lifetime;
    private readonly object _gate = new();
    private readonly List<Job> _jobs = [];
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private bool _busy, _disposed, _stop;
    private Job? _active;
    private CancellationTokenSource? _cancellation;
    private string? _issue;
    private RealM112ExitReview? _exitReview;
    private bool _allowUnresolvedExit;
    public event Action<RealM112QueueView>? Changed;
    public RealM112ProgrammingQueue(Func<M112LiveProject> current, ProgrammingSession programming,
        Func<string, IM112CommissioningBackend>? backend = null, VerifiedM112ReceiptStore? receipts = null)
    { _current = current; _programming = programming; _backend = backend ?? (_ => new M112CommissioningBackend()); _lifetime = current().SessionIdentity; _receipts = receipts; }
    public Task ReadHistoryAsync() => Start(async () =>
    {
        RequireCurrent(); if (_receipts is null) return;
        _programming.BeginVerifiedM112HistoryRevalidation();
        var states = await _receipts.ReadHistoryAsync(_current().Project!, _cancellation!.Token);
        if (_stop) return; RequireCurrent(); _programming.ImportVerifiedM112History(states);
    });
    public Task Completion { get { lock (_gate) return _operation; } }
    public bool IsBusy { get { lock (_gate) return _busy; } }
    private Job[] JobsCopy() { lock (_gate) return _jobs.ToArray(); }
    public bool RecoveryRequired => JobsCopy().Any(j => j.Session.RecoveryRequired);
    public RealM112ExitReview? ExitReview { get { lock (_gate) return _exitReview; } }
    public bool MayLeave { get { bool busy, allowed; lock (_gate) { busy = _busy; allowed = _allowUnresolvedExit; } return !busy && (!RecoveryRequired || allowed); } }
    public RealM112QueueView Snapshot
    {
        get
        {
            Job[] jobs; bool busy, stop; string? issue;
            lock (_gate) { jobs = _jobs.ToArray(); busy = _busy; stop = _stop; issue = _issue; }
            return new(busy, jobs.Any(j => j.Session.RecoveryRequired), stop, BatchFingerprint(jobs),
                Array.AsReadOnly(jobs.Select(j => j.View()).ToArray()), issue);
        }
    }
    public Task EnqueueAsync(string deviceId, M112BaselineSelection baseline, ReadOnlyConnectionOptions options, string journal)
        => Start(async () =>
        {
            RequireCurrent();
            // This first UI profile deliberately requires explicit ordinary UDP selection.
            if (options.Profile.Kind != Knx.Bus.Connections.KnxTransportKind.PlainUdp) throw new InvalidOperationException("Profile");
            var live = _current(); var desired = ProjectQuery.DeviceList(live.Project!).Single(d => d.Id == deviceId);
            if (_jobs.Any(j => j.DeviceId == deviceId && j.State is not (RealM112JobState.Verified or RealM112JobState.VerifiedStale or RealM112JobState.VerifiedUnrecorded or RealM112JobState.Restored or RealM112JobState.Cancelled)))
                throw new InvalidOperationException("AlreadyQueued");
            var root = M112PrivatePaths.ExistingDirectory(journal);
            var backend = _backend(deviceId); var session = new M112CommissioningSession(_current, deviceId, backend);
            var job = new Job(live.Project!, deviceId, desired.Address.ToString(), desired.Name ?? deviceId,
                DeviceConfigurationSnapshot.Freeze(live.Project!, desired), session, backend, options, baseline.Path, baseline.Sha256, root);
            lock (_gate) _jobs.Add(job);
            session.Changed += _ => Observe(job); _active = job;
            await session.PrepareAsync(baseline.Project, baseline.Path, baseline.Sha256, options, root);
            if (session.Snapshot.Phase != M112CommissioningPhase.Reviewed)
            { job.State = session.Snapshot.Phase == M112CommissioningPhase.Cancelled ? RealM112JobState.Cancelled : RealM112JobState.Blocked;
                job.Issue = session.Snapshot.Issue is "Stale" or "SourceChanged" or "Target" or "Cancelled" ? session.Snapshot.Issue : "UnsupportedProfile"; }
            Publish();
        });
    public Task ReadIdentityAsync(Guid id) => Start(async () =>
    {
        var job = Find(id); _active = job; job.Confirmed = false; job.Issue = null;
        if (job.Session.Snapshot.Resume is not null) await job.Session.ReadRecoveryIdentityAsync();
        else { RequireCurrent(); await job.Session.ReadIdentityAsync(); }
        var ready = job.Session.Snapshot.Phase is M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady;
        job.State = job.Session.RecoveryRequired ? RealM112JobState.RecoveryRequired : ready ? RealM112JobState.Ready : RealM112JobState.Blocked;
        Publish();
    }, allowRecovery: true);
    public void Confirm(Guid id, string target, string fingerprint, bool confirmed)
    {
        VerifyAccess();
        ThrowIfUnavailable(allowRecovery: true); var job = Find(id);
        _allowUnresolvedExit = false; _exitReview = null;
        if (job.State is not (RealM112JobState.Ready or RealM112JobState.RecoveryRequired)
            || target != job.Address || fingerprint != job.Session.Snapshot.Fingerprint) throw new InvalidOperationException("Confirmation");
        job.Session.ConfirmIdentity(confirmed); job.Confirmed = confirmed; Publish();
    }
    public void Remove(Guid id)
    {
        VerifyAccess();
        ThrowIfUnavailable(); var job = Find(id);
        if (job.Session.RecoveryRequired) throw new InvalidOperationException("RecoveryRequired");
        lock (_gate) _jobs.Remove(job);
        // There is no in-flight job while removal is allowed.
        _ = job.Session.DisposeAsync(); Publish();
    }
    public void Cancel()
    {
        M112CommissioningSession? active; CancellationTokenSource? cancellation;
        lock (_gate) { _stop = true; active = _active?.Session; cancellation = _cancellation; }
        try { cancellation?.Cancel(); } catch { }
        try { active?.Cancel(); } catch { /* Cancellation callbacks cannot skip the queued owner's join. */ }
        Publish();
    }
    public Task RunAsync(string confirmedBatchFingerprint) => Start(async () =>
    {
        RequireCurrent();
        var candidates = _jobs.Where(j => j.State is RealM112JobState.Reviewed or RealM112JobState.Ready or RealM112JobState.Blocked).ToArray();
        if (candidates.Length == 0 || candidates.Any(j => j.State != RealM112JobState.Ready || !j.Confirmed
            || j.Session.Snapshot.Resume is not null || j.Session.Snapshot.Phase != M112CommissioningPhase.Ready) || confirmedBatchFingerprint != BatchFingerprint())
            throw new InvalidOperationException("Confirmation");
        foreach (var job in candidates)
        {
            if (!CurrentMatches(job)) { job.State = RealM112JobState.Blocked; job.Issue = "Stale"; job.Confirmed = false; throw new InvalidOperationException("Stale"); }
            try { if (await M112PrivatePaths.HashAsync(job.BaselinePath, _cancellation!.Token) != job.BaselineHash) throw new IOException(); }
            catch (OperationCanceledException) { throw; }
            catch { job.State = RealM112JobState.Blocked; job.Issue = "SourceChanged"; job.Confirmed = false; throw new IOException("SourceChanged"); }
        }
        if (_stop) return;
        if (candidates.Any(job => !CurrentMatches(job))) throw new InvalidOperationException("Stale");
        // Durable, reference-only batch audit precedes network access. Core owns the full before-image journal.
        var audit = CreateAudit(candidates, confirmedBatchFingerprint);
        foreach (var job in candidates)
        {
            if (_stop) break;
            RequireCurrent(); _active = job; job.State = RealM112JobState.Running; job.Issue = null; Publish();
            job.Audit = audit;
            if (_receipts is not null) await _receipts.InvalidateBeforeAttemptAsync(job.Project, job.DeviceId, _cancellation!.Token);
            if (_stop) { job.State = RealM112JobState.Cancelled; job.Confirmed = false; break; }
            _programming.InvalidateM112Flags(job.DeviceId);
            await job.Session.ApplyAsync(job.Address, job.Session.Review!.Fingerprint);
            var s = job.Session.Snapshot; var result = s.Result;
            if (result is { DownloadedAndVerified: true, Error: null } && s.Phase == M112CommissioningPhase.Verified)
            {
                var current = CurrentMatches(job);
                if (current && _receipts is not null)
                {
                    var inspection = await job.Backend.InspectRecoveryAsync(result.RecoveryPath, CancellationToken.None);
                    var receipt = new VerifiedM112Receipt(job.DeviceId, job.Address, job.Configuration.Signature, job.Session.Review!.Fingerprint,
                        job.BaselineHash, confirmedBatchFingerprint, s.Identity!, inspection.RecoveryPath, inspection.FileSha256, job.Options.EndpointId, DateTimeOffset.UtcNow);
                    await _receipts.RecordVerifiedAsync(job.Project, ProjectQuery.DeviceList(job.Project).Single(d => d.Id == job.DeviceId), receipt, inspection);
                    current = CurrentMatches(job);
                }
                AppendAudit(audit, job, current ? "Verified" : "VerifiedStale");
                var recorded = current && _programming.RecordVerifiedM112Change(job.DeviceId, job.Configuration.Signature, job.Options.EndpointId, result);
                job.State = recorded ? RealM112JobState.Verified : RealM112JobState.VerifiedStale;
                job.Issue = recorded ? null : "Stale";
            }
            else
            {
                job.State = job.Session.RecoveryRequired ? RealM112JobState.RecoveryRequired
                    : result?.OriginalConfigurationRestoredAndVerified == true ? RealM112JobState.Restored
                    : s.Phase == M112CommissioningPhase.Cancelled ? RealM112JobState.Cancelled : RealM112JobState.Blocked;
                job.Issue = s.ConnectionFailure?.ToString() ?? s.Issue ?? "OperationFailed";
                AppendAudit(audit, job, job.State.ToString());
                if (CurrentMatches(job)) _programming.RecordRealM112Failure(job.DeviceId, job.Options.EndpointId, job.Issue);
            }
            job.Confirmed = false; Publish(); _active = null;
            // No blind retry or continuation after cancellation, rollback, uncertain state or model drift.
            if (_stop || job.State != RealM112JobState.Verified) break;
        }
    });
    public Task PrepareRecoveryAsync(Guid id) => Start(async () =>
    {
        var job = Find(id); var path = job.Session.Snapshot.Result?.RecoveryPath;
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("RecoveryUnavailable");
        job.Confirmed = false; _active = job;
        await job.Session.PrepareRecoveryAsync(path, job.Options); Publish();
    }, allowRecovery: true);
    public Task RecoverAsync(Guid id, string target, string fileFingerprint) => Start(async () =>
    {
        var job = Find(id);
        if (!job.Confirmed || target != job.Address || fileFingerprint != job.Session.Snapshot.Fingerprint) throw new InvalidOperationException("Confirmation");
        try { _receipts?.InvalidateForRecovery(job.DeviceId); } catch { _issue = "JournalUnavailable"; }
        var live = _current();
        if (ReferenceEquals(_lifetime, live.SessionIdentity)) _programming.InvalidateM112Flags(job.DeviceId);
        _active = job; await job.Session.RecoverAsync(target, fileFingerprint);
        job.Confirmed = false;
        job.State = job.Session.RecoveryRequired ? RealM112JobState.RecoveryRequired
            : job.Session.Snapshot.Result?.OriginalConfigurationRestoredAndVerified == true ? RealM112JobState.Restored : RealM112JobState.Blocked;
        Publish();
    }, allowRecovery: true);
    /// <summary>Offline evidence review for an explicit unknown-hardware-state exit; unavailable evidence never authorizes another write.</summary>
    public Task ReviewUnresolvedExitAsync() => Start(async () => { _exitReview = await InspectUnresolvedAsync(); Publish(); }, allowRecovery: true);
    public Task ConfirmUnresolvedExitAsync(string fingerprint) => Start(async () =>
    {
        var review = await InspectUnresolvedAsync(); _exitReview = review;
        if (review.Fingerprint != fingerprint) throw new InvalidOperationException("Confirmation");
        foreach (var job in _jobs.Where(j => j.Session.RecoveryRequired))
        {
            try { if (job.Audit is { } path) AppendAudit(path, job, "ExplicitExitWithUnknownHardwareState"); }
            catch { _issue = "JournalUnavailable"; }
        }
        _allowUnresolvedExit = true; Publish();
    }, allowRecovery: true);
    public void RevokeUnresolvedExit() { lock (_gate) _allowUnresolvedExit = false; Publish(); }
    private async Task<RealM112ExitReview> InspectUnresolvedAsync()
    {
        var evidence = new List<RealM112RecoveryEvidence>();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var job in _jobs.Where(j => j.Session.RecoveryRequired))
        {
            var path = job.Session.Snapshot.Result?.RecoveryPath; string? hash = null; var available = false;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var inspection = await job.Backend.InspectRecoveryAsync(path, bounded.Token);
                    available = inspection.Identity.Address == job.Address && inspection.Identity == job.Session.Snapshot.Identity;
                    if (available) hash = inspection.FileSha256;
                }
            }
            catch { /* Missing/corrupt evidence is visible as unavailable, never an exit deadlock or a recovery write capability. */ }
            evidence.Add(new(job.Id, job.Address, path, hash, available));
        }
        return new(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence))), Array.AsReadOnly(evidence.ToArray()));
    }
    private bool CurrentMatches(Job job)
    {
        try
        {
            RequireCurrent(); var project = _current().Project!;
            return ReferenceEquals(project, job.Project) && ProjectRevisions.ContentHash(project) == job.ModelHash
                && VerifiedM112ReceiptStore.ModelFingerprint(project) == job.SourceHash && job.Session.Review?.IsCurrent() == true
                && DeviceConfigurationSnapshot.Freeze(project, ProjectQuery.DeviceList(project).Single(d => d.Id == job.DeviceId)).Signature == job.Configuration.Signature;
        }
        catch { return false; }
    }
    private void RequireCurrent()
    {
        var live = _current();
        if (!ReferenceEquals(_lifetime, live.SessionIdentity) || live.Project is null || live.HasDrafts || live.ProgrammingBusy)
            throw new InvalidOperationException("Stale");
    }
    private Job Find(Guid id) { lock (_gate) return _jobs.Single(j => j.Id == id); }
    private string BatchFingerprint() => BatchFingerprint(JobsCopy());
    private static string BatchFingerprint(Job[] jobs) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(jobs
        .Where(j => j.State is RealM112JobState.Reviewed or RealM112JobState.Ready or RealM112JobState.Blocked)
        .Select(j => new { j.Id, j.DeviceId, j.Address, j.BaselineHash, j.ModelHash, j.SourceHash, j.Configuration.Signature,
            fingerprint = j.Session.Snapshot.Fingerprint, identity = j.Session.Snapshot.Identity, j.Confirmed,
            endpoint = j.Options.EndpointId, own = j.Options.RequestedAddress.ToString() }).ToArray())));
    private void Observe(Job job)
    {
        if (job.Session.Snapshot.Phase == M112CommissioningPhase.Restoring) job.State = RealM112JobState.Restoring;
        Publish();
    }
    private static string CreateAudit(Job[] jobs, string fingerprint)
    {
        var directory = Path.Combine(M112PrivatePaths.ExistingDirectory(jobs[0].Journal), "queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "batch.jsonl");
        Append(path, new { format = "BusChord.M112Queue.1", fingerprint, jobs = jobs.Select(j => new { j.Id, j.DeviceId,
            j.Address, j.BaselineHash, j.Configuration.Signature, fingerprint = j.Session.Snapshot.Fingerprint, identity = j.Session.Snapshot.Identity }) });
        return path;
    }
    private static void AppendAudit(string path, Job job, string state) => Append(path, new { job.Id, state,
        recovery = job.Session.Snapshot.Result?.RecoveryPath, scope = job.Session.Snapshot.Result?.RecoveryScope });
    private static void Append(string path, object entry)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(entry)); stream.WriteByte((byte)'\n'); stream.Flush(flushToDisk: true);
    }
    private void ThrowIfUnavailable(bool allowRecovery = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy) throw new InvalidOperationException("Busy");
        }
        if (!allowRecovery && RecoveryRequired) throw new InvalidOperationException("RecoveryRequired");
    }
    private Task Start(Func<Task> action, bool allowRecovery = false)
    {
        VerifyAccess(); ThrowIfUnavailable(allowRecovery);
        TaskCompletionSource completion; CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_busy) throw new InvalidOperationException("Busy");
            _busy = true; _stop = false; _issue = null; _allowUnresolvedExit = false; _exitReview = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task;
            cancellation = _cancellation = new(TimeSpan.FromMinutes(12));
        }
        _ = RunOwnedAsync(action, cancellation, completion); return completion.Task;
    }
    private async Task RunOwnedAsync(Func<Task> action, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { Publish(); await action(); }
        catch (Exception error)
        {
            _issue = error is OperationCanceledException ? "Cancelled" : error.Message is "Stale" or "SourceChanged" or "Profile" or "AlreadyQueued" or "Confirmation" or "RecoveryRequired" or "RecoveryUnavailable" ? error.Message : "OperationFailed";
            if (_active is { State: RealM112JobState.Running or RealM112JobState.Restoring } job)
            {
                job.State = job.Session.RecoveryRequired ? RealM112JobState.RecoveryRequired
                    : job.Session.Snapshot.Result is { DownloadedAndVerified: true, Error: null } ? RealM112JobState.VerifiedUnrecorded : RealM112JobState.Blocked;
                job.Issue = _issue is "SourceChanged" or "Cancelled" ? _issue : "JournalUnavailable"; job.Confirmed = false;
            }
        }
        finally { lock (_gate) { _busy = false; _active = null; if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; } cancellation.Dispose(); Publish(); completion.TrySetResult(); }
    }
    private void Publish()
    {
        var snapshot = Snapshot;
        if (Changed is { } callbacks) foreach (Action<RealM112QueueView> callback in callbacks.GetInvocationList()) try { callback(snapshot); } catch { }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task pending; Task result;
        lock (_gate)
        {
            if (_disposal is null) { _disposed = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; }
            result = _disposal; pending = _operation;
        }
        if (completion is not null) { Cancel(); _ = FinishDisposeAsync(pending, completion); }
        return new(result);
    }
    private async Task FinishDisposeAsync(Task pending, TaskCompletionSource completion)
    {
        try { await pending; foreach (var job in _jobs) await job.Session.DisposeAsync(); completion.TrySetResult(); }
        catch { completion.TrySetException(new IOException("Programming cleanup failed.")); }
    }
}
