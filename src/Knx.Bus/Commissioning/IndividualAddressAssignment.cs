using Knx.Bus.Connections;

namespace Knx.Bus.Commissioning;

/// <summary>Reviewed one-write commissioning owner. Cancellation/close joins independent read-only outcome reconciliation, never a compensating write.</summary>
public sealed class IndividualAddressAssignment : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AddressAssignmentRequest _request;
    private readonly string _journalRoot;
    private readonly Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>> _open;
    private readonly Func<bool>? _contextIsCurrent;
    private readonly CancellationTokenSource _lifetime = new();
    private IAddressAssignmentOperation? _owned;
    private AddressAssignmentReview? _review;
    private AddressAssignmentState _state;
    private AddressAssignmentResult? _result;
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private CancellationTokenSource? _requestCancellation;
    private bool _attempted;
    public AddressAssignmentRequest Request => _request;
    public AddressAssignmentReview? Review { get { lock (_gate) return _review; } }
    public AddressAssignmentResult? Result { get { lock (_gate) return _result; } }
    public AddressAssignmentState State { get { lock (_gate) return _state; } }
    public Task Completion { get { lock (_gate) return _operation; } }
    public event Action? Changed;
    public IndividualAddressAssignment(AddressAssignmentRequest request, string privateJournalRoot,
        Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>>? open = null, Func<bool>? contextIsCurrent = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request)); _journalRoot = AddressAssignmentJournal.ValidateDirectory(privateJournalRoot);
        _open = open ?? new AddressAssignmentTransport().OpenAsync;
        _contextIsCurrent = contextIsCurrent;
    }
    public Task<AddressAssignmentReview> ReviewAsync(CancellationToken ct = default) => Start(async token =>
    {
        if (_attempted) throw Refuse(AddressAssignmentFailure.AlreadyAttempted);
        RequireCurrentContext();
        SetState(AddressAssignmentState.Reviewing); lock (_gate) _review = null;
        if (_owned is not null) { await _owned.DisposeAsync().ConfigureAwait(false); _owned = null; }
        _owned = await _open(_request.Connection, token).ConfigureAwait(false);
        if (_owned is null || !_owned.Profile.EquivalentTo(_request.Connection)) throw Refuse(AddressAssignmentFailure.InvalidSelection);
        var observation = await _owned.DiscoverAsync(_request.Selection, _request.Serial, _request.ObservationWindow, token).ConfigureAwait(false);
        var old = RequireSingleObservation(observation);
        if (old == _request.NewAddress) throw Refuse(AddressAssignmentFailure.AlreadyAssigned);
        if (old == _request.Connection.RequestedAddress) throw Refuse(AddressAssignmentFailure.InvalidSelection);
        AddressPhysicalIdentity? identity = null;
        if (_request.Selection == AddressSelectionKind.IsolatedProgrammingMode && old.Raw != ushort.MaxValue)
        {
            identity = await _owned.ReadIdentityAsync(old, token).ConfigureAwait(false);
            if (!identity.IsValid) throw Refuse(AddressAssignmentFailure.PhysicalBindingRequired);
        }
        var occupied = await _owned.ProbeAsync(_request.NewAddress, _request.ObservationWindow, token).ConfigureAwait(false);
        RequireReservationObservation(occupied); token.ThrowIfCancellationRequested();
        var provisional = new AddressAssignmentReview("", InterfaceId(_request.Connection), _request.Connection.RequestedAddress.ToString(), old.ToString(), _request.NewAddress.ToString(),
            _request.Selection, _request.Serial ?? identity?.Serial, _request.IsolatedSetup, _request.Reservation, identity,
            observation.ValidResponses, occupied.State, DateTimeOffset.UtcNow) { TargetRole = _request.TargetRole, CouplerHardwareConfirmed = _request.CouplerHardwareConfirmed,
                CouplerHardwareReference = _request.CouplerHardwareReference };
        var review = provisional with { Fingerprint = AddressAssignmentJournal.ReviewHash(provisional) };
        lock (_gate) _review = review; SetState(AddressAssignmentState.Reviewed); return review;
    }, ct);

    public Task<AddressAssignmentResult> ApplyAsync(string exactReviewFingerprint, bool confirmReservedTarget, bool confirmPhysicalBinding, CancellationToken ct = default)
        => Start(async token =>
        {
            AddressAssignmentReview review; lock (_gate) review = _review ?? throw Refuse(AddressAssignmentFailure.StaleReview);
            if (_attempted) throw Refuse(AddressAssignmentFailure.AlreadyAttempted);
            if (review.Fingerprint != exactReviewFingerprint || !confirmReservedTarget || !confirmPhysicalBinding)
                throw Refuse(AddressAssignmentFailure.ReservationRequired);
            var owned = _owned ?? throw Refuse(AddressAssignmentFailure.StaleReview);
            var current = await owned.DiscoverAsync(_request.Selection, _request.Serial, _request.ObservationWindow, token).ConfigureAwait(false);
            if (RequireSingleObservation(current).ToString() != review.OldAddress) throw Refuse(AddressAssignmentFailure.StaleReview);
            if (review.Identity is { } identity)
            {
                var actual = await owned.ReadIdentityAsync(IndividualAddress.Parse(review.OldAddress), token).ConfigureAwait(false);
                if (actual != identity) throw Refuse(AddressAssignmentFailure.PhysicalBindingRequired);
            }
            RequireReservationObservation(await owned.ProbeAsync(_request.NewAddress, _request.ObservationWindow, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested(); RequireCurrentContext();
            var intent = new AddressAssignmentIntent(1, Guid.NewGuid(), _request.Connection.Gateway.Address.ToString(), _request.Connection.Port,
                review.ClientAddress, _request.Connection.LocalAddress?.ToString(), review, (int)_request.ObservationWindow.TotalMilliseconds);
            var path = await Task.Run(() => AddressAssignmentJournal.Create(_journalRoot, intent)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Task.Run(() => AddressAssignmentJournal.ClaimAttempt(path, intent)).ConfigureAwait(false); _attempted = true;
            var writeStarted = false;
            try
            {
                // The claimed record is reread and sealed before the sole call. No loaded/cold intent can enter this method.
                if (await AddressAssignmentJournal.ReadAsync(path, token).ConfigureAwait(false) != intent) throw Refuse(AddressAssignmentFailure.InvalidJournal);
                token.ThrowIfCancellationRequested(); RequireCurrentContext(); SetState(AddressAssignmentState.Applying); writeStarted = true;
                await owned.WriteOnceAsync(_request.Selection, _request.Serial, _request.NewAddress, token).ConfigureAwait(false);
            }
            catch { /* ACK loss, cancellation and send exceptions are uncertain; the independent fresh read decides the observed outcome. */ }
            SetState(AddressAssignmentState.Reconciling);
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await ReconcileCoreAsync(owned, intent, path, _request.Selection != AddressSelectionKind.IsolatedProgrammingMode || confirmPhysicalBinding,
                true, writeStarted, recovery.Token).ConfigureAwait(false);
            try { await Task.Run(() => AddressAssignmentJournal.Record(path, result)).ConfigureAwait(false); }
            catch { result = result with { State = result.AddressVerified ? AddressAssignmentState.VerifiedUnrecorded : AddressAssignmentState.Unresolved }; }
            lock (_gate) _result = result; SetState(result.State); return result;
        }, ct);

    /// <summary>Cold resume performs reads only, even when the attempted-write record was lost. Isolation continuity needs a new explicit declaration of the same setup.</summary>
    public static async Task<AddressAssignmentResult> ReconcileAsync(string intentPath, bool reconfirmSameIsolatedDevice = false,
        string? isolatedSetup = null, Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>>? open = null, CancellationToken ct = default,
        string? expectedReviewFingerprint = null)
    {
        var intent = await AddressAssignmentJournal.ReadAsync(intentPath, ct).ConfigureAwait(false);
        if (expectedReviewFingerprint is not null && intent.Review.Fingerprint != expectedReviewFingerprint) throw Refuse(AddressAssignmentFailure.StaleReview);
        if (intent.Review.Selection == AddressSelectionKind.IsolatedProgrammingMode
            && (!reconfirmSameIsolatedDevice || isolatedSetup != intent.Review.IsolatedSetup)) throw Refuse(AddressAssignmentFailure.PhysicalBindingRequired);
        var claimed = await Task.Run(() => AddressAssignmentJournal.HasClaim(intentPath, intent)).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        await using var owned = await (open ?? new AddressAssignmentTransport().OpenAsync)(intent.Profile(), ct).ConfigureAwait(false);
        if (!owned.Profile.EquivalentTo(intent.Profile())) throw Refuse(AddressAssignmentFailure.InvalidSelection);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(20));
        var result = await ReconcileCoreAsync(owned, intent, intentPath, reconfirmSameIsolatedDevice,
            claimed, false, bounded.Token).ConfigureAwait(false);
        try { await Task.Run(() => AddressAssignmentJournal.Record(intentPath, result)).ConfigureAwait(false); }
        catch { return result with { State = result.AddressVerified ? AddressAssignmentState.VerifiedUnrecorded : AddressAssignmentState.Unresolved }; }
        return result;
    }
    public static async Task<AddressAssignmentReview> InspectIntentAsync(string intentPath, CancellationToken ct = default)
        => (await AddressAssignmentJournal.ReadAsync(intentPath, ct).ConfigureAwait(false)).Review;
    private void RequireCurrentContext() { if (_contextIsCurrent?.Invoke() == false) throw Refuse(AddressAssignmentFailure.StaleReview); }

    private static async Task<AddressAssignmentResult> ReconcileCoreAsync(IAddressAssignmentOperation owned, AddressAssignmentIntent intent, string path,
        bool isolationConfirmed, bool claimed, bool started, CancellationToken ct)
    {
        var review = intent.Review; var desired = IndividualAddress.Parse(review.NewAddress); var old = IndividualAddress.Parse(review.OldAddress);
        AddressAssignmentResult Result(AddressAssignmentState state, AddressPhysicalIdentity? identity = null)
            => new(state, path, review.Fingerprint, review.OldAddress, review.NewAddress, identity, claimed, started);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (review.Selection == AddressSelectionKind.ExactSerial)
                {
                    var observed = await owned.DiscoverAsync(AddressSelectionKind.ExactSerial, review.Serial, TimeSpan.FromMilliseconds(intent.ObservationMilliseconds), ct).ConfigureAwait(false);
                    var actualAddress = RequireSingleObservation(observed);
                    if (actualAddress == old) return Result(AddressAssignmentState.OriginalObserved);
                    if (actualAddress != desired) return Result(AddressAssignmentState.Unresolved);
                }
                else if (!isolationConfirmed) return Result(AddressAssignmentState.Unresolved);
                var identity = await owned.ReadIdentityAsync(desired, ct).ConfigureAwait(false);
                if (!identity.IsValid || review.Serial is { } expectedSerial && identity.Serial != expectedSerial
                    || review.Identity is { } expectedIdentity && identity != expectedIdentity) return Result(AddressAssignmentState.Unresolved);
                return Result(AddressAssignmentState.Verified, identity);
            }
            catch (AddressAssignmentException error) when (error.Failure == AddressAssignmentFailure.AmbiguousResponse)
            { return Result(AddressAssignmentState.Unresolved); }
            catch { if (ct.IsCancellationRequested || attempt == 2) break; }
            try { await Task.Delay(150, ct).ConfigureAwait(false); } catch { break; }
        }
        // In isolated mode a positively matching captured original identity is useful recovery evidence, never permission to rewrite.
        if (review.Selection == AddressSelectionKind.IsolatedProgrammingMode && isolationConfirmed && old.Raw != ushort.MaxValue && review.Identity is { } original && !ct.IsCancellationRequested)
        {
            try { if (await owned.ReadIdentityAsync(old, ct).ConfigureAwait(false) == original) return Result(AddressAssignmentState.OriginalObserved, original); } catch { }
        }
        return Result(AddressAssignmentState.Unresolved);
    }
    private static IndividualAddress RequireSingleObservation(IndividualAddressObservation observation)
    {
        if (observation.State != AddressObservationState.ResponsesObserved || observation.ValidResponses != 1 || observation.MalformedResponses != 0 || observation.Addresses.Count != 1)
            throw Refuse(AddressAssignmentFailure.AmbiguousResponse);
        return observation.Addresses[0];
    }
    private static void RequireReservationObservation(AddressOccupancyObservation observation)
    { if (observation.State != AddressOccupancyState.NoResponseReservationRequired || observation.Responses != 0) throw Refuse(AddressAssignmentFailure.TargetResponded); }
    private static string InterfaceId(KnxConnectionProfile profile) => $"knx-ip:{profile.Gateway.Address}:{profile.Port}";
    private Task<T> Start<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        TaskCompletionSource<T> completion; CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposal is not null) throw Refuse(AddressAssignmentFailure.Disposed);
            if (!_operation.IsCompleted) throw Refuse(AddressAssignmentFailure.Occupied);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task;
            cancellation = _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        }
        _ = RunAsync(action, cancellation, completion); return completion.Task;
    }
    private async Task RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationTokenSource cancellation, TaskCompletionSource<T> completion)
    {
        T? result = default; Exception? failure = null;
        try { result = await action(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) { failure = error; SetState(AddressAssignmentState.Cancelled); }
        catch (Exception error) { failure = error is AddressAssignmentException or KnxConnectionException ? error : Refuse(AddressAssignmentFailure.TransportFailed); SetState(AddressAssignmentState.Failed); }
        finally { lock (_gate) if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null; cancellation.Dispose(); }
        if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
        else if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(result!);
    }
    private void SetState(AddressAssignmentState state)
    { lock (_gate) _state = state; foreach (Action callback in Changed?.GetInvocationList() ?? []) try { callback(); } catch { } }
    public void Cancel() { lock (_gate) try { _requestCancellation?.Cancel(); } catch { } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task pending;
        lock (_gate) { if (_disposal is null || _disposal.IsFaulted) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; } pending = _disposal; }
        if (completion is not null) { try { _lifetime.Cancel(); } catch { } finally { _ = FinishDisposeAsync(completion); } }
        return new(pending);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        try { try { await Completion.ConfigureAwait(false); } catch { } if (_owned is not null) { await _owned.DisposeAsync().ConfigureAwait(false); _owned = null; } _lifetime.Dispose(); completion.TrySetResult(); }
        catch { completion.TrySetException(Refuse(AddressAssignmentFailure.TransportFailed)); }
    }
    private static AddressAssignmentException Refuse(AddressAssignmentFailure failure) => new(failure);
}
