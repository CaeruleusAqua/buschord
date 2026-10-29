using Knx.Recovery;

namespace Knx.App.Services;

public interface IProjectRecoveryPersistence
{
    Task SaveAsync(RecoveryExport export, CancellationToken token);
    Task AppendAuditAsync(RecoveryAuditContext context, IReadOnlyList<RecoveryAuditDraft> drafts, CancellationToken token);
    Task OmitProtectedAsync(string sessionId, CancellationToken token);
}

public sealed class ProjectRecoveryPersistence(WorkbenchRecoveryStore store) : IProjectRecoveryPersistence
{
    public async Task SaveAsync(RecoveryExport export, CancellationToken token) => await store.SaveAsync(export, cancellationToken: token).ConfigureAwait(false);
    public async Task AppendAuditAsync(RecoveryAuditContext context, IReadOnlyList<RecoveryAuditDraft> drafts, CancellationToken token)
        => await store.AppendAuditAsync(context, drafts, token).ConfigureAwait(false);
    public Task OmitProtectedAsync(string sessionId, CancellationToken token) => store.RecordProtectedOmissionAsync(sessionId, token);
}

public sealed record RecoveryRecorderStatus(string Code, long CompletedAudits, long CompletedCheckpoints, string? SessionId = null, RecoveryExport? CompletedExport = null);

/// <summary>Process-wide bounded serial writer. No live model or dispatcher is read by this worker.</summary>
public sealed class ProjectRecoveryRecorder : IAsyncDisposable
{
    private sealed record AuditWork(RecoveryAuditContext Context, RecoveryAuditDraft Draft);
    private readonly IProjectRecoveryPersistence _persistence;
    private readonly object _gate = new();
    private readonly Queue<AuditWork> _audit = new();
    private readonly Dictionary<string, RecoveryExport> _latest = new(StringComparer.Ordinal);
    private readonly HashSet<string> _omitted = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Dictionary<string, TaskCompletionSource> _drains = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedSessions = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private bool _stopping, _busy;
    private long _audits, _checkpoints;
    public const int MaximumAuditBacklog = 128, MaximumPendingSessions = 8;
    public event EventHandler<RecoveryRecorderStatus>? Changed;
    public ProjectRecoveryRecorder(IProjectRecoveryPersistence persistence)
    { _persistence = persistence; _worker = Task.Run(WorkAsync); }
    public RecoveryRecorderStatus Status { get; private set; } = new("Recovery.Idle", 0, 0);
    private void Publish(string code, string? sessionId = null, RecoveryExport? completedExport = null)
    {
        Status = new(code, Interlocked.Read(ref _audits), Interlocked.Read(ref _checkpoints), sessionId, completedExport);
        try { Changed?.Invoke(this, Status); } catch { /* UI observers cannot stop durable work. */ }
    }
    private void Wake() { if (_signal.CurrentCount == 0) _signal.Release(); }
    public bool Audit(RecoveryAuditContext context, RecoveryAuditDraft draft)
    {
        lock (_gate)
        {
            if (_stopping || _failedSessions.Contains(context.SessionId) || _audit.Count >= MaximumAuditBacklog)
            { _failedSessions.Add(context.SessionId); Publish("Recovery.AuditBacklogFull", context.SessionId); return false; }
            _audit.Enqueue(new(context, draft)); Wake(); return true;
        }
    }
    public bool Checkpoint(RecoveryExport export)
    {
        lock (_gate)
        {
            if (_stopping || _failedSessions.Contains(export.SessionId) || (!_latest.ContainsKey(export.SessionId) && _latest.Count >= MaximumPendingSessions))
            { Publish("Recovery.BacklogFull", export.SessionId); return false; }
            _latest[export.SessionId] = export; Wake(); return true;
        }
    }
    public void OmitProtected(string sessionId)
    {
        lock (_gate)
        {
            if (_stopping || _omitted.Count >= MaximumPendingSessions) return;
            _latest.Remove(sessionId); _omitted.Add(sessionId); Wake();
        }
    }
    public Task DrainAsync(string? sessionId = null)
    {
        lock (_gate)
        {
            if (!_busy && _audit.Count == 0 && _latest.Count == 0 && _omitted.Count == 0)
                return Failed(sessionId) ? Task.FromException(new IOException("Recovery.WriterFailed")) : Task.CompletedTask;
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = sessionId ?? "";
            if (_drains.TryGetValue(key, out var pending)) return pending.Task;
            if (_drains.Count >= MaximumPendingSessions + 1) return Task.FromException(new IOException("Recovery.DrainBacklogFull"));
            _drains.Add(key, drain); Wake(); return drain.Task;
        }
    }
    private bool Failed(string? sessionId) => sessionId is null ? _failedSessions.Count > 0 : _failedSessions.Contains(sessionId);
    private async Task WorkAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            while (true)
            {
                AuditWork[] audits; RecoveryExport[] exports; string[] omitted;
                lock (_gate)
                {
                    if (_audit.Count == 0 && _latest.Count == 0 && _omitted.Count == 0)
                    {
                        _busy = false;
                        foreach (var drain in _drains) { if (Failed(drain.Key.Length == 0 ? null : drain.Key)) drain.Value.TrySetException(new IOException("Recovery.WriterFailed")); else drain.Value.TrySetResult(); }
                        _drains.Clear(); if (_stopping) return; break;
                    }
                    _busy = true; audits = _audit.ToArray(); _audit.Clear(); exports = _latest.Values.ToArray(); _latest.Clear();
                    omitted = _omitted.ToArray(); _omitted.Clear();
                }
                try
                {
                    foreach (var id in omitted) await _persistence.OmitProtectedAsync(id, CancellationToken.None).ConfigureAwait(false);
                    foreach (var group in audits.GroupBy(a => a.Context.SessionId))
                    {
                        // Keep each event's own protection proof: do not substitute a later plaintext context.
                        foreach (var audit in group)
                        {
                            try { await _persistence.AppendAuditAsync(audit.Context, [audit.Draft], CancellationToken.None).ConfigureAwait(false); Interlocked.Increment(ref _audits); }
                            catch (RecoveryRejectedException ex) when (ex.Failure == RecoveryFailure.ProtectedSource)
                            { await _persistence.OmitProtectedAsync(audit.Context.SessionId, CancellationToken.None).ConfigureAwait(false); }
                        }
                    }
                    foreach (var export in exports)
                    {
                        try { await _persistence.SaveAsync(export, CancellationToken.None).ConfigureAwait(false); Interlocked.Increment(ref _checkpoints); Publish("Recovery.Saved", export.SessionId, export); }
                        catch (RecoveryRejectedException ex) when (ex.Failure == RecoveryFailure.ProtectedSource)
                        { await _persistence.OmitProtectedAsync(export.SessionId, CancellationToken.None).ConfigureAwait(false); }
                    }
                    if (exports.Length == 0) Publish("Recovery.Saved");
                }
                catch (Exception ex)
                {
                    var failed = audits.Select(item => item.Context.SessionId).Concat(exports.Select(item => item.SessionId)).Concat(omitted).Distinct().ToArray();
                    lock (_gate) foreach (var session in failed) _failedSessions.Add(session);
                    foreach (var session in failed) Publish(ex is RecoveryRejectedException rejected ? rejected.Code : "Recovery.WriterFailed", session);
                    // Never claim that dropped/failed audit entries were persisted. Existing heads remain intact.
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task drained = DrainAsync();
        try { await drained.ConfigureAwait(false); }
        finally { lock (_gate) { _stopping = true; Wake(); } await _worker.ConfigureAwait(false); _signal.Dispose(); }
    }
}
