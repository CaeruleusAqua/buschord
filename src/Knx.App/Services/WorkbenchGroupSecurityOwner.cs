using System.Text.Json.Serialization;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.App.Services;

/// <summary>Per-window registry ownership, partitioned by actual project lifetime. Construction performs no I/O.</summary>
public sealed class WorkbenchGroupSecurityOwner(IGroupIntegrityCapabilitySource? integrity = null,
    Func<string, IDataSecureMonotonicAnchor?>? independentAnchor = null) : IAsyncDisposable
{
    private sealed class ProjectState
    {
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly SemaphoreSlim Serial = new(1, 1);
        internal readonly Dictionary<string, KnxGroupHostProfiles> Registries = new(StringComparer.Ordinal);
        internal int Operations;
        internal TaskCompletionSource Drained = Completed();
        internal Task? Disposal;
    }
    private readonly object _gate = new();
    private readonly Dictionary<object, ProjectState> _projects = new(ReferenceEqualityComparer.Instance);
    private Task? _disposal;
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    public bool HasProject(object? projectLifetime) { lock (_gate) return projectLifetime is not null && _projects.ContainsKey(projectLifetime); }
    private Operation Admit(object project, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposal is not null) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
            if (!_projects.TryGetValue(project, out var state)) _projects.Add(project, state = new());
            if (state.Disposal is not null) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
            if (state.Operations++ == 0) state.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return new(this, state, CancellationTokenSource.CreateLinkedTokenSource(ct, state.Lifetime.Token)); }
            catch { if (--state.Operations == 0) state.Drained.TrySetResult(); throw; }
        }
    }
    public async Task<KnxGroupHostCandidates> ReadCandidatesAsync(object projectLifetime, string path, CancellationToken ct)
    {
        using var operation = Admit(projectLifetime, ct);
        operation.Token.ThrowIfCancellationRequested();
        var result = await Task.Run(() => KnxGroupHostProfiles.ReadCandidates(path)).ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested(); return result;
    }
    public async Task<WorkbenchGroupSelection> CreateSelectionAsync(object projectLifetime, KnxGroupHostCandidates candidates, string referenceId,
        KnxConnectionProfile explicitConnection, bool confirmedCurrentUnanchored, CancellationToken ct)
    {
        using var operation = Admit(projectLifetime, ct); var state = operation.State;
        await state.Serial.WaitAsync(operation.Token).ConfigureAwait(false);
        KnxGroupHostProfiles? created = null;
        try
        {
            var candidate = candidates.Candidates.SingleOrDefault(item => item.Id == referenceId)
                ?? throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            if (candidate.RequiresCurrentUnanchoredConfirmation && !confirmedCurrentUnanchored)
                throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityStateNotReady);
            var cacheKey = candidates.Path.Length + ":" + candidates.Path + candidates.SourceFingerprint + ":" + referenceId;
            if (!state.Registries.TryGetValue(cacheKey, out var registry))
            {
                if (state.Registries.Count >= 64) throw new KnxConnectionException(KnxConnectionFailure.Occupied);
                created = registry = await Task.Run(() => KnxGroupHostProfiles.LoadPinnedSelection(candidates.Path, candidates.SourceFingerprint,
                    referenceId, confirmedCurrentUnanchored, integrity, independentAnchor?.Invoke(referenceId))).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
            }
            var policy = await registry.ResolveForExplicitPasswordSourceAsync(referenceId, explicitConnection, operation.Token, state.Lifetime.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (created is not null) { state.Registries.Add(cacheKey, created); created = null; }
            return new(registry, candidate, explicitConnection, policy, candidates.Path, candidates.SourceFingerprint, state.Lifetime.Token);
        }
        finally { if (created is not null) await created.DisposeAsync().ConfigureAwait(false); state.Serial.Release(); }
    }
    /// <summary>Immediately invalidates this project; releases registry keys only after selection and connection/scan owners joined.</summary>
    public Task ReleaseProjectAfterAsync(object? projectLifetime, Task consumersEnded)
    {
        if (projectLifetime is null) return consumersEnded;
        TaskCompletionSource? owner = null; ProjectState? state; Task pending;
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectLifetime, out state)) _projects.Add(projectLifetime, state = new());
            if (state.Disposal is null || state.Disposal.IsFaulted) { owner = new(TaskCreationOptions.RunContinuationsAsynchronously); state.Disposal = owner.Task; }
            pending = state.Disposal;
        }
        if (owner is not null) { try { state.Lifetime.Cancel(); } catch { } finally { _ = FinishProjectAsync(state, consumersEnded, owner); } }
        return pending;
    }
    private async Task FinishProjectAsync(ProjectState state, Task consumersEnded, TaskCompletionSource owner)
    {
        try
        {
            Task drained; lock (_gate) drained = state.Drained.Task;
            await Task.WhenAll(drained, consumersEnded).ConfigureAwait(false);
            foreach (var registry in state.Registries.Values) await registry.DisposeAsync().ConfigureAwait(false);
            state.Registries.Clear(); state.Lifetime.Dispose(); state.Serial.Dispose(); owner.TrySetResult();
        }
        catch { owner.TrySetException(new IOException("Group connection cleanup failed.")); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; object[] projects = []; Task pending;
        lock (_gate)
        {
            if (_disposal is null || _disposal.IsFaulted) { owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; projects = _projects.Keys.ToArray(); }
            pending = _disposal;
        }
        if (owner is not null) _ = FinishOwnerAsync(projects, owner);
        return new(pending);
    }
    private async Task FinishOwnerAsync(object[] projects, TaskCompletionSource owner)
    {
        try { await Task.WhenAll(projects.Select(project => ReleaseProjectAfterAsync(project, Task.CompletedTask))).ConfigureAwait(false); owner.TrySetResult(); }
        catch { owner.TrySetException(new IOException("Group connection cleanup failed.")); }
    }
    private sealed class Operation(WorkbenchGroupSecurityOwner owner, ProjectState state, CancellationTokenSource cancellation) : IDisposable
    {
        internal ProjectState State => state;
        internal CancellationToken Token => cancellation.Token;
        public void Dispose() { cancellation.Dispose(); lock (owner._gate) if (--state.Operations == 0) state.Drained.TrySetResult(); }
    }
    public override string ToString() => "[workbench group security owner]";
}

/// <summary>Immutable source choice. Current-unanchored confirmation is consumed once per new connection operation.</summary>
public sealed class WorkbenchGroupSelection
{
    private readonly object _gate = new();
    private readonly KnxGroupHostProfiles _registry;
    private readonly KnxConnectionProfile _base;
    private readonly KnxGroupSecurityProfile _policy;
    private readonly CancellationToken _projectLifetime;
    public KnxGroupHostCandidate Candidate { get; }
    [JsonIgnore] public string RegistryPath { get; }
    public string RegistryFingerprint { get; }
    public bool CanStartNewOperation => _policy.CanStartNewOperation;
    internal WorkbenchGroupSelection(KnxGroupHostProfiles registry, KnxGroupHostCandidate candidate, KnxConnectionProfile connection,
        KnxGroupSecurityProfile policy, string path, string fingerprint, CancellationToken projectLifetime)
    { _registry = registry; Candidate = candidate; _base = connection; _policy = policy; RegistryPath = path; RegistryFingerprint = fingerprint; _projectLifetime = projectLifetime; }
    public bool Matches(KnxConnectionProfile connection) => _base.EquivalentTo(connection) && ReferenceEquals(_base.PasswordSource, connection.PasswordSource);
    public async Task<KnxConnectionProfile> PrepareNewOperationAsync(KnxConnectionProfile connection, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_projectLifetime.IsCancellationRequested) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
            if (!Matches(connection)) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
            if (!_policy.CanStartNewOperation) throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityStateNotReady);
        }
        await _registry.VerifyExplicitSourceBindingAsync(Candidate.Id, connection, ct).ConfigureAwait(false);
        return connection.WithGroupSecurity(_policy);
    }
    /// <summary>Metadata snapshot for the parent window. The factory still enforces the single-operation grant.</summary>
    [JsonIgnore] public KnxConnectionProfile BoundProfile => _base.WithGroupSecurity(_policy);
    [JsonIgnore] public KnxConnectionProfile BaseConnection => _base;
    public async Task<WorkbenchGroupSelection> ReconfirmNewOperationAsync(KnxConnectionProfile connection, bool confirmedCurrentState, CancellationToken ct)
    {
        if (_projectLifetime.IsCancellationRequested) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
        if (!confirmedCurrentState || !Matches(connection)) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
        await _registry.VerifyExplicitSourceBindingAsync(Candidate.Id, connection, ct).ConfigureAwait(false);
        var renewed = await _registry.ResolveForExplicitPasswordSourceAsync(Candidate.Id, connection, ct, _projectLifetime).ConfigureAwait(false);
        return new(_registry, Candidate, connection, renewed, RegistryPath, RegistryFingerprint, _projectLifetime);
    }
    public override string ToString() => Candidate.Id;
}
