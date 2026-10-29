using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Threading;
using Knx.Recovery;

namespace Knx.App.Services;

public interface IRecoveryDispatcher { Task<T> InvokeAsync<T>(Func<T> action); }
public interface IProjectRecoveryReader
{
    Task<RecoveryListPage> ListAsync(string? continuation, CancellationToken token);
    Task<RecoveryCandidate> LoadCandidateAsync(string id, RecoverySource source, IRecoveryDependencyResolver? dependencies, CancellationToken token);
}
public sealed class ProjectRecoveryReader(WorkbenchRecoveryStore store) : IProjectRecoveryReader
{
    public Task<RecoveryListPage> ListAsync(string? continuation, CancellationToken token) => store.ListAsync(50, continuation, cancellationToken: token);
    public Task<RecoveryCandidate> LoadCandidateAsync(string id, RecoverySource source, IRecoveryDependencyResolver? dependencies, CancellationToken token)
        => store.LoadCandidateAsync(id, source, dependencies, token);
}
public sealed class RecoveryDispatcher : IRecoveryDispatcher
{
    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return action();
        return await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
    }
}

public sealed class RecoveryAdoptionTicket
{
    internal string CheckpointId { get; }
    internal RecoverySource Source { get; }
    internal object UiSession { get; }
    internal string? Fingerprint { get; }
    internal RecoverySource? CurrentSource { get; }
    internal long DependencyGeneration { get; }
    internal long Revision { get; }
    internal long CatalogGeneration { get; }
    internal IReadOnlyList<string> Notices { get; }
    private int _consumed;
    internal bool Consume() => Interlocked.Exchange(ref _consumed, 1) == 0;
    internal RecoveryAdoptionTicket(string id, RecoverySource source, object session, string? fingerprint,
        RecoverySource? currentSource, long generation, long revision, long catalogGeneration, IReadOnlyList<string> notices)
    { CheckpointId = id; Source = source; UiSession = session; Fingerprint = fingerprint; CurrentSource = currentSource; DependencyGeneration = generation; Revision = revision; CatalogGeneration = catalogGeneration; Notices = notices.ToArray(); }
}

/// <summary>Owning-thread capture and guarded adoption. All persistence runs on the global writer.</summary>
public sealed class ProjectRecoveryController : IAsyncDisposable
{
    private readonly MainWindowViewModel _vm;
    private readonly ProjectRecoveryRecorder _recorder;
    private readonly IProjectRecoveryReader _store;
    private readonly IRecoveryDispatcher _ui;
    private readonly Func<bool> _drafts, _programming;
    private readonly IRecoveryDependencyResolver? _dependencies;
    private readonly Func<Project, RecoveryDependencyInventory>? _dependencyRecorder;
    private readonly object _operationsGate = new();
    private Task _operations = Task.CompletedTask, _capture = Task.CompletedTask;
    private CancellationTokenSource? _debounce;
    private int _queued;
    private long _revision;
    private long _editRevision, _saveRevision;
    private bool _closed, _closing, _operationFailed, _captureRequested, _closeDrained;
    private string _session = Guid.NewGuid().ToString("N");
    private RecoverySource? _source;
    private object? _boundUiSession;
    private RecoveryExport? _lastQueuedExport;
    public RecoverySource? Source => _source;
    public string SessionId => _session;
    public string StatusCode { get; private set; } = "Recovery.NotBound";
    public double LastCaptureMilliseconds { get; private set; }
    public long CaptureCount { get; private set; }
    public long CommitRevision => _revision;
    public bool RecordingEnabled { get; private set; } = true;
    public void InvalidateBinding()
    {
        _debounce?.Cancel(); _captureRequested = false; _source = null; _boundUiSession = null;
        _lastQueuedExport = null; _session = Guid.NewGuid().ToString("N"); _operationFailed = false;
        State(RecordingEnabled ? "Recovery.NotBound" : "Recovery.Disabled");
    }
    public void UnbindForUnsupportedProject(string reason)
    {
        SetRecordingEnabled(false); InvalidateBinding();
        _operationFailed = false; State(reason);
    }
    public void SetRecordingEnabled(bool enabled)
    {
        RecordingEnabled = enabled; _vm.RecoveryFingerprintEnabled = enabled;
        if (!enabled) { _debounce?.Cancel(); _captureRequested = false; if (_source?.Kind == RecoveryBindingKind.SavedArchive) { _source = null; _boundUiSession = null; } State("Recovery.Disabled"); }
        else { _vm.ResetRecoveryFingerprint(); RequestCapture(); }
    }
    public event EventHandler? Changed;
    public ProjectRecoveryController(MainWindowViewModel vm, ProjectRecoveryRecorder recorder, WorkbenchRecoveryStore store,
        IRecoveryDispatcher ui, Func<bool> hasPrivateDrafts, Func<bool> programmingBusy,
        IRecoveryDependencyResolver? dependencies = null, Func<Project, RecoveryDependencyInventory>? dependencyRecorder = null)
        : this(vm, recorder, new ProjectRecoveryReader(store), ui, hasPrivateDrafts, programmingBusy, dependencies, dependencyRecorder) { }
    public ProjectRecoveryController(MainWindowViewModel vm, ProjectRecoveryRecorder recorder, IProjectRecoveryReader store,
        IRecoveryDispatcher ui, Func<bool> hasPrivateDrafts, Func<bool> programmingBusy,
        IRecoveryDependencyResolver? dependencies = null, Func<Project, RecoveryDependencyInventory>? dependencyRecorder = null)
    {
        _vm = vm; _recorder = recorder; _store = store; _ui = ui; _drafts = hasPrivateDrafts; _programming = programmingBusy;
        _dependencies = dependencies; _dependencyRecorder = dependencyRecorder;
        _vm.RecoveryDependencyRecorder = dependencyRecorder;
        _vm.RecoveryUndo.SetRecoveryDependencyRecorder(dependencyRecorder);
        _vm.RecoveryCommitted += OnCommitted;
        _vm.RecoveryCommitFailed += OnCommitFailed;
        _vm.RecoverySessionReplaced += OnSessionReplaced;
        _recorder.Changed += OnRecorderChanged;
    }
    private void OnCommitFailed(object? sender, EventArgs e) { _revision++; _editRevision++; _operationFailed = true; State("Recovery.WriterFailed"); }
    private void OnSessionReplaced(object? sender, EventArgs e) => InvalidateBinding();
    private void OnRecorderChanged(object? sender, RecoveryRecorderStatus status)
    {
        _ = _ui.InvokeAsync(() =>
        {
            if (_closed || status.SessionId is not null && status.SessionId != _session) return false;
            if (status.Code is not ("Recovery.Saved" or "Recovery.Idle")) { _operationFailed = true; State(status.Code); }
            else if (!_operationFailed && StatusCode == "Recovery.Queued" && status.CompletedExport is not null
                && ReferenceEquals(status.CompletedExport, _lastQueuedExport)) State("Recovery.Saved");
            return true;
        }).ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    private void State(string code) { StatusCode = code; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task BindAsync(RecoverySource source)
    {
        await _ui.InvokeAsync(() =>
        {
            var project = _vm.Project ?? throw new InvalidOperationException("Recovery.NoProject");
            var session = source.Kind == RecoveryBindingKind.UnsavedProject ? source.UnsavedSessionId! : Guid.NewGuid().ToString("N");
            RecoveryContractValidation.ValidateBinding(source, session);
            if (project.Id != source.ProjectId || project.Guid != source.ProjectGuid) throw new InvalidOperationException("Recovery.SourceChanged");
            if (_closed || _closing) throw new IOException("Recovery.StaleSession");
            _session = session; _source = source; _boundUiSession = _vm.ProjectSessionIdentity; _operationFailed = false; _lastQueuedExport = null; _vm.ResetRecoveryFingerprint();
            _vm.RecoveryUndo.SetRecoveryDependencyRecorder(_dependencyRecorder); State(RecordingEnabled ? "Recovery.Idle" : "Recovery.Disabled"); RequestCapture(); return true;
        });
    }
    public static RecoverySource Unsaved(Project project, string? sessionId = null)
    {
        if (!Guid.TryParse(project.Guid, out var guid) || guid == Guid.Empty) throw new InvalidDataException("Recovery.ProjectGuidRequired");
        return new(RecoveryBindingKind.UnsavedProject, project.Id, project.Guid, null, null, null, sessionId ?? Guid.NewGuid().ToString("N"));
    }
    /// <summary>Only a path explicitly selected by the user or supplied by the completed load/save operation.</summary>
    public static Task<RecoverySource> ReadSelectedSourceAsync(string path, CancellationToken token = default)
        => RecoverySourceProbe.ReadSelectedAsync(Path.GetFullPath(path), token);
    private void OnCommitted(object? sender, RecoveryCommittedOperation operation)
    {
        if (_closed || _closing) return;
        _revision++;
        if (operation.Kind is RecoveryOperationKind.Save or RecoveryOperationKind.SaveAs) _saveRevision++;
        else _editRevision++;
        if (!RecordingEnabled)
        {
            if (operation.Kind is RecoveryOperationKind.Save or RecoveryOperationKind.SaveAs) { _source = null; _boundUiSession = null; }
            return;
        }
        var identity = _vm.ProjectSessionIdentity;
        var eventSession = _session;
        RecoveryAuditContext frozen;
        try
        {
            if (_source is null || _vm.Project is null || !ReferenceEquals(_boundUiSession, identity)) throw new IOException("Recovery.NotBound");
            // Freeze this transaction's historical protection proof before any continuation or later edit.
            frozen = _vm.RecoveryUndo.ExportRecoveryAuditContext(_vm.Project, eventSession, _source);
        }
        catch (RecoveryRejectedException ex) when (ex.Failure == RecoveryFailure.ProtectedSource)
        { _recorder.OmitProtected(eventSession); State("Recovery.ProtectedSource"); return; }
        catch (Exception) { _operationFailed = true; State("Recovery.NotBound"); return; }
        var draft = new RecoveryAuditDraft(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, operation.Kind,
            operation.Description.Length <= 256 ? operation.Description : operation.Description[..256], operation.Before, operation.After);
        if (Interlocked.Increment(ref _queued) > ProjectRecoveryRecorder.MaximumAuditBacklog)
        { Interlocked.Decrement(ref _queued); _operationFailed = true; State("Recovery.AuditBacklogFull"); return; }
        lock (_operationsGate)
        {
            var previous = _operations;
            _operations = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (operation.Kind is RecoveryOperationKind.Save or RecoveryOperationKind.SaveAs)
                    {
                        var path = await _ui.InvokeAsync(() => ReferenceEquals(identity, _vm.ProjectSessionIdentity) ? _vm.ProjectPath : throw new IOException("Recovery.StaleSession"));
                        var binding = await ReadSelectedSourceAsync(path).ConfigureAwait(false);
                        await _ui.InvokeAsync(() => { if (!ReferenceEquals(identity, _vm.ProjectSessionIdentity)) throw new IOException("Recovery.StaleSession"); _source = binding; return true; });
                    }
                    var context = await _ui.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(identity, _vm.ProjectSessionIdentity) || _vm.Project is null || _source is null)
                            throw new IOException("Recovery.NotBound");
                        return frozen.WithSource(_source);
                    });
                    if (!_recorder.Audit(context, draft)) _operationFailed = true;
                }
                catch (RecoveryRejectedException ex) when (ex.Failure == RecoveryFailure.ProtectedSource)
                { _recorder.OmitProtected(eventSession); await _ui.InvokeAsync(() => { State("Recovery.ProtectedSource"); return true; }); }
                catch (Exception) { _operationFailed = true; await _ui.InvokeAsync(() => { State("Recovery.WriterFailed"); return true; }); }
                finally { Interlocked.Decrement(ref _queued); }
            });
        }
        RequestCapture();
    }
    public void RequestCapture()
    {
        if (_closed || _closing || !RecordingEnabled) return;
        _captureRequested = true;
        _debounce?.Cancel(); _debounce?.Dispose(); _debounce = new(); var token = _debounce.Token;
        _capture = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                Task operations; lock (_operationsGate) operations = _operations;
                await operations.ConfigureAwait(false); token.ThrowIfCancellationRequested();
                await _ui.InvokeAsync(() => { if (!token.IsCancellationRequested && !_closed) Capture(); return true; });
            }
            catch (OperationCanceledException) { }
        });
    }
    private void Capture()
    {
        if (!_captureRequested || !RecordingEnabled) return;
        if (_operationFailed) { State("Recovery.WriterFailed"); return; }
        if (_source is null || _vm.Project is null || !ReferenceEquals(_boundUiSession, _vm.ProjectSessionIdentity)) { State("Recovery.NotBound"); return; }
        if (_drafts()) { State("Recovery.PrivateDraftsSkipped"); return; }
        if (_programming()) { State("Recovery.ProgrammingBusy"); return; }
        var clock = Stopwatch.StartNew();
        try
        {
            var export = _vm.RecoveryUndo.ExportRecovery(_vm.Project, _session, _source);
            if (_recorder.Checkpoint(export)) { _lastQueuedExport = export; _captureRequested = false; CaptureCount++; State("Recovery.Queued"); }
            else State("Recovery.WriterFailed");
        }
        catch (RecoveryRejectedException ex) when (ex.Failure == RecoveryFailure.ProtectedSource)
        { _recorder.OmitProtected(_session); State("Recovery.ProtectedSource"); }
        catch (Exception) { State("Recovery.WriterFailed"); }
        finally { LastCaptureMilliseconds = clock.Elapsed.TotalMilliseconds; }
    }
    public async Task DrainAsync()
    {
        _debounce?.Cancel();
        Task operations; lock (_operationsGate) operations = _operations;
        await operations.ConfigureAwait(false); await _capture.ConfigureAwait(false);
        await _ui.InvokeAsync(() => { Capture(); return true; });
        await _recorder.DrainAsync(_session).ConfigureAwait(false);
        if (_operationFailed) throw new IOException("Recovery.WriterFailed");
    }
    public async Task RefreshSavedBindingAsync()
    {
        var state = await _ui.InvokeAsync(() => (Identity: _vm.ProjectSessionIdentity, Path: _vm.ProjectPath,
            Id: _vm.Project?.Id, Guid: _vm.Project?.Guid));
        if (state.Id is null || state.Path.Length == 0) return;
        var stamp = await RecoverySourceStamp.ReadAsync(state.Path);
        await _ui.InvokeAsync(() =>
        {
            if (!ReferenceEquals(state.Identity, _vm.ProjectSessionIdentity)) throw new IOException("Recovery.StaleSession");
            _source = new(RecoveryBindingKind.SavedArchive, state.Id, state.Guid, stamp.Path,
                RecoveryContractValidation.PathIdentity(stamp.Path), stamp.File, null); return true;
        });
    }
    public async Task CloseAndDrainAsync() { _closing = true; try { await DrainAsync().ConfigureAwait(false); } finally { _closeDrained = true; } }
    public void CancelCloseAttempt() { _closing = false; _closeDrained = false; }
    public Task<RecoveryListPage> ListAsync(string? continuation = null, CancellationToken token = default)
        => _store.ListAsync(continuation, token);
    public Task<RecoveryCandidate> ValidateAsync(string id, RecoverySource source, CancellationToken token = default)
        => _store.LoadCandidateAsync(id, source, _dependencies, token);
    public async Task<RecoveryAdoptionTicket> PrepareAdoptionAsync(string id, RecoverySource expectedSource,
        Func<Task<bool>> existingDiscardGate, IReadOnlyList<string> acknowledgedNotices, CancellationToken token = default)
    {
        // Pending private state is not part of the Core format: keep it until explicitly applied/discarded in its editor.
        var beforeGate = await _ui.InvokeAsync(() =>
        {
            if (_drafts() || _programming()) throw new IOException("Recovery.PrivateDraftsOrProgramming");
            return (Session: _vm.ProjectSessionIdentity, Edit: _editRevision, Save: _saveRevision, Catalog: _vm.RecoveryCatalogGeneration,
                LogicalSession: _session, Fingerprint: _vm.Project is null ? null : _vm.RecoveryUndo.GetRecoveryFingerprint());
        });
        if (!await existingDiscardGate()) throw new OperationCanceledException();
        // A successful Save in the existing gate completes its detached source-hash pipeline.
        // Edits/session/catalogue changes during this await are still compared with beforeGate.
        Task committed; lock (_operationsGate) committed = _operations;
        await committed.ConfigureAwait(false);
        // Approval binds this exact live state before any subsequent background work.
        var baseline = await _ui.InvokeAsync(() =>
        {
            if (_drafts() || _programming()) throw new IOException("Recovery.PrivateDraftsOrProgramming");
            if (!ReferenceEquals(beforeGate.Session, _vm.ProjectSessionIdentity) || beforeGate.Edit != _editRevision
                || beforeGate.Catalog != _vm.RecoveryCatalogGeneration || beforeGate.LogicalSession != _session
                || beforeGate.Save == _saveRevision && beforeGate.Fingerprint != (_vm.Project is null ? null : _vm.RecoveryUndo.GetRecoveryFingerprint()))
                throw new IOException("Recovery.StaleSession");
            return new RecoveryAdoptionTicket(id, expectedSource, _vm.ProjectSessionIdentity,
                _vm.Project is null ? null : _vm.RecoveryUndo.GetRecoveryFingerprint(), _source, _dependencies?.Generation ?? 0,
                _revision, _vm.RecoveryCatalogGeneration, acknowledgedNotices);
        });
        try { await DrainAsync(); }
        catch (IOException) when (_operationFailed) { /* Explicit restore starts a fresh journal; failed old journal is retained. */ }
        await _ui.InvokeAsync(() => { VerifyLive(baseline); return true; });
        var candidate = await ValidateAsync(id, expectedSource, token);
        if (!candidate.Notices.SequenceEqual(acknowledgedNotices)) throw new IOException("Recovery.StaleSession");
        return await _ui.InvokeAsync(() =>
        {
            VerifyLive(baseline);
            if (candidate.DependencyGeneration != baseline.DependencyGeneration) throw new IOException("Recovery.StaleSession");
            return baseline;
        });
    }
    private void VerifyLive(RecoveryAdoptionTicket ticket)
    {
        if (!ReferenceEquals(ticket.UiSession, _vm.ProjectSessionIdentity) || ticket.CurrentSource != _source
            || (_vm.Project is null ? null : _vm.RecoveryUndo.GetRecoveryFingerprint()) != ticket.Fingerprint
            || _drafts() || _programming() || (_dependencies?.Generation ?? 0) != ticket.DependencyGeneration
            || ticket.Revision != _revision || ticket.CatalogGeneration != _vm.RecoveryCatalogGeneration)
            throw new IOException("Recovery.StaleSession");
    }
    public async Task AdoptAsync(RecoveryAdoptionTicket ticket, CancellationToken token = default)
    {
        // Full revalidation immediately precedes the owning-thread one-use adoption, never a manifest path open.
        var candidate = await _store.LoadCandidateAsync(ticket.CheckpointId, ticket.Source, _dependencies, token).ConfigureAwait(false);
        if (ticket.CurrentSource is { Kind: RecoveryBindingKind.SavedArchive } active)
        {
            await using var file = new FileStream(active.FullPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if ((File.GetAttributes(active.FullPath!) & FileAttributes.ReparsePoint) != 0 || file.Length != active.SavedFile!.Length
                || Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)) != active.SavedFile.Sha256)
                throw new IOException("Recovery.SourceChanged");
        }
        await _ui.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            VerifyLive(ticket);
            if (candidate.DependencyGeneration != ticket.DependencyGeneration || !candidate.Notices.SequenceEqual(ticket.Notices))
                throw new IOException("Recovery.StaleSession");
            if (!ticket.Consume()) throw new IOException("Recovery.CandidateAlreadyConsumed");
            var restored = candidate.Take(); var newSession = Guid.NewGuid().ToString("N");
            var source = restored.Source.Kind == RecoveryBindingKind.UnsavedProject
                ? restored.Source with { UnsavedSessionId = newSession } : restored.Source;
            _vm.AdoptRecovery(restored with { Source = source }, _dependencyRecorder, identity =>
            { _session = newSession; _source = source; _boundUiSession = identity; });
            _vm.ResetRecoveryFingerprint();
            _operationFailed = false;
            var fingerprint = _vm.RecoveryUndo.GetRecoveryFingerprint();
            var context = _vm.RecoveryUndo.ExportRecoveryAuditContext(_vm.Project!, _session, source);
            if (RecordingEnabled && !_recorder.Audit(context, new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, RecoveryOperationKind.Restore,
                "Recovery.RestoreParent:" + ticket.CheckpointId, fingerprint, fingerprint))) _operationFailed = true;
            State(_operationFailed ? "Recovery.WriterFailed" : "Recovery.Restored"); RequestCapture(); return true;
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        try { if (!_closeDrained) await CloseAndDrainAsync().ConfigureAwait(false); }
        finally { _closed = true; _vm.RecoveryCommitted -= OnCommitted; _vm.RecoveryCommitFailed -= OnCommitFailed; _vm.RecoverySessionReplaced -= OnSessionReplaced; _recorder.Changed -= OnRecorderChanged; _debounce?.Dispose(); }
    }
}
