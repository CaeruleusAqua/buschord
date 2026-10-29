using Knx.Bus.Connections;

namespace Knx.App.Services;

public sealed record GroupSecuritySelectionSnapshot(bool Busy, string Status, KnxGroupHostCandidates? Candidates,
    KnxGroupHostCandidate? Candidate, bool ConfirmedCurrentState, WorkbenchGroupSelection? Selection);

/// <summary>Explicit metadata/consent session. Every source/choice change invalidates and cancels the pending generation.</summary>
public sealed class GroupSecuritySelectionSession(WorkbenchGroupSecurityOwner owner, object projectLifetime,
    KnxConnectionProfile explicitConnection) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private KnxConnectionProfile _connection = explicitConnection;
    private string _path = "", _status = "Unverified";
    private KnxGroupHostCandidates? _candidates;
    private KnxGroupHostCandidate? _candidate;
    private WorkbenchGroupSelection? _selection;
    private bool _confirmed, _busy;
    private long _generation;
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private CancellationTokenSource? _request;
    public event Action? Changed;
    public GroupSecuritySelectionSnapshot Snapshot { get { lock (_gate) return new(_busy, _status, _candidates, _candidate, _confirmed, _selection); } }
    public Task OperationCompletion { get { lock (_gate) return _operation; } }
    public void SetSource(string path)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposal is not null || _path == path) return; _path = path; _generation++; _candidates = null; _candidate = null;
            _confirmed = false; _selection = null; _status = "Unverified"; cancel = _request;
        }
        Cancel(cancel); Notify();
    }
    public void SetConnection(KnxConnectionProfile connection)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposal is not null || _connection.EquivalentTo(connection) && ReferenceEquals(_connection.PasswordSource, connection.PasswordSource)) return;
            _connection = connection; _generation++; _selection = null; _confirmed = false; _status = "Unverified"; cancel = _request;
        }
        Cancel(cancel); Notify();
    }
    public void Select(KnxGroupHostCandidate? candidate)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposal is not null || ReferenceEquals(_candidate, candidate)) return;
            if (candidate is not null && _candidates?.Candidates.Contains(candidate) != true) throw new InvalidOperationException("Unknown group profile candidate.");
            _candidate = candidate; _generation++; _selection = null; _confirmed = false; _status = "Unverified"; cancel = _request;
        }
        Cancel(cancel); Notify();
    }
    public void ConfirmCurrentState(bool confirmed)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposal is not null || _confirmed == confirmed) return; _confirmed = confirmed; _generation++; _selection = null; _status = "Unverified"; cancel = _request;
        }
        Cancel(cancel); Notify();
    }
    public Task ReadCandidatesAsync() => Start(async (generation, ct) =>
    {
        string path; lock (_gate) path = _path;
        var candidates = await owner.ReadCandidatesAsync(projectLifetime, path, ct).ConfigureAwait(false);
        lock (_gate) { Current(generation, ct); _candidates = candidates; _candidate = candidates.Candidates.FirstOrDefault(); _confirmed = false; _selection = null; _status = "Unverified"; }
    });
    public Task BindAsync() => Start(async (generation, ct) =>
    {
        KnxGroupHostCandidates candidates; KnxGroupHostCandidate candidate; bool confirmed; KnxConnectionProfile connection;
        lock (_gate)
        {
            candidates = _candidates ?? throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            candidate = _candidate ?? throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); confirmed = _confirmed; connection = _connection;
        }
        var selection = await owner.CreateSelectionAsync(projectLifetime, candidates, candidate.Id, connection, confirmed, ct).ConfigureAwait(false);
        lock (_gate) { Current(generation, ct); _selection = selection; _status = "Bound"; }
    });
    private void Current(long generation, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (_disposal is not null || generation != _generation) throw new OperationCanceledException(ct); }
    private Task Start(Func<long, CancellationToken, Task> action)
    {
        TaskCompletionSource completion; CancellationTokenSource request; long generation;
        lock (_gate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(GroupSecuritySelectionSession));
            if (_busy) throw new InvalidOperationException("A group profile operation is already running.");
            request = _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _busy = true; generation = ++_generation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task;
        }
        Notify(); _ = ExecuteAsync(action, generation, request, completion); return completion.Task;
    }
    private async Task ExecuteAsync(Func<long, CancellationToken, Task> action, long generation, CancellationTokenSource request, TaskCompletionSource completion)
    {
        try { await action(generation, request.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { lock (_gate) if (generation == _generation && _disposal is null) { _status = "Cancelled"; _selection = null; } }
        catch (KnxConnectionException error) { lock (_gate) if (generation == _generation && _disposal is null) { _status = error.Failure.ToString(); _selection = null; } }
        catch { lock (_gate) if (generation == _generation && _disposal is null) { _status = "InvalidProfile"; _selection = null; } }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_request, request)) _request = null; _busy = false; } request.Dispose(); Notify(); completion.TrySetResult();
        }
    }
    private static void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (AggregateException) { } catch (ObjectDisposedException) { } }
    private void Notify() { foreach (var callback in Changed?.GetInvocationList() ?? []) try { ((Action)callback)(); } catch { } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task pending;
        lock (_gate) { if (_disposal is null) { owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; _generation++; _selection = null; } pending = _disposal; }
        if (owner is not null) { try { _lifetime.Cancel(); } catch { } finally { _ = FinishDisposeAsync(owner); } }
        return new(pending);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource owner)
    {
        try { await OperationCompletion.ConfigureAwait(false); _lifetime.Dispose(); owner.TrySetResult(); }
        catch { owner.TrySetException(new IOException("Group profile cleanup failed.")); }
    }
}
