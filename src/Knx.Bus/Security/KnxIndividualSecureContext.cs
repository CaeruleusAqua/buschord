using System.Security.Cryptography;

namespace Knx.Bus.Security;

public enum KnxIndividualSecureState { SynchronizationRequired, Synchronizing, Ready, Faulted, Disposed }
public enum KnxIndividualSecureReceiveFailure { None, InvalidFrame, ContextMismatch, SynchronizationRequired, AuthenticationFailed, Replay, StateUnavailable }
public sealed record KnxIndividualSecureReceiveResult(KnxIndividualSecureReceiveFailure Failure, KnxIndividualSecureData? Data)
{ public bool Accepted => Failure == KnxIndividualSecureReceiveFailure.None && Data is not null; }

/// <summary>One owned wire request; retransmit only identical bytes while its context still admits this pending operation.</summary>
public sealed class KnxIndividualSynchronization
{
    private readonly byte[] _frame;
    internal object Token { get; } = new();
    public ulong Sequence { get; }
    public Task Completion { get; }
    internal KnxIndividualSynchronization(byte[] frame, ulong sequence, Task completion) { _frame = frame; Sequence = sequence; Completion = completion; }
    public byte[] CopyFrame() => _frame.ToArray();
}

/// <summary>
/// Explicit single-peer client context, auth-conf only. Owns a copy of one Tool/P2P key and borrows a provisioned counter
/// store. Requires fresh authenticated unicast Sync after construction/reset, even with retained valid counter state.
/// Missing/restore-blocked state cannot send Sync; a clock or peer floor cannot replace a lost trusted send highwater.
/// No key fallback, provisioning writes, broadcast/server role or Tool-Key transition is performed.
/// </summary>
public sealed class KnxIndividualSecureContext : IAsyncDisposable
{
    public static TimeSpan SynchronizationTimeout => TimeSpan.FromSeconds(6);
    private readonly DataSecureCounterStore _store;
    private readonly byte[] _key;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _drained = Completed();
    private object _epoch = new();
    private Pending? _pending;
    private int _operations;
    private bool _disposed;
    private Task? _disposal;
    private KnxIndividualSecureState _state;
    public IndividualAddress LocalAddress { get; }
    public IndividualAddress Peer { get; }
    public KnxIndividualSecureKeyKind KeyKind { get; }
    public int MaximumWireTpduLength { get; }
    public KnxIndividualSecureState State { get { lock (_gate) return _state; } }

    public KnxIndividualSecureContext(DataSecureCounterStore store, KnxIndividualSecureCounterBinding binding,
        IndividualAddress peer, ReadOnlySpan<byte> explicitlyProvisionedKey, int maximumWireTpduLength, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(binding);
        if (store.IdentityFingerprint != binding.Identity.Fingerprint || store.LocalAddress != binding.Identity.LocalAddress
            || peer.Raw == 0 || peer.Device == 255 || peer == store.LocalAddress || !store.KnownSenders.Contains(peer))
            throw new ArgumentException("The provisioned store, domain, local address and known peer must match.");
        if (explicitlyProvisionedKey.Length != 16) throw new ArgumentException("Exactly one explicitly provisioned individual key is required.");
        if (maximumWireTpduLength is < 25 or > 255) throw new ArgumentOutOfRangeException(nameof(maximumWireTpduLength), "The explicit wire capacity must accommodate the 25-byte unicast Sync.");
        _store = store; LocalAddress = store.LocalAddress; Peer = peer; KeyKind = binding.KeyKind;
        MaximumWireTpduLength = maximumWireTpduLength;
        _key = explicitlyProvisionedKey.ToArray(); _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a connectionless unicast Sync and waits for HandleSynchronizationResponseAsync. boundedControlSend must
    /// send these raw cEMI bytes and wait for their KNXnet/IP ACK only; it must not wrap them as S-A_Data or wait for a
    /// normal application telegram. Ownership must join this initializer before publishing a management client.
    /// </summary>
    public async Task InitializeAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> boundedControlSend, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(boundedControlSend); using var operation = Admit(ct);
        using var timeout = new CancellationTokenSource(SynchronizationTimeout, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, timeout.Token);
        var template = new CemiLDataFrame(CemiMessageCode.LDataReq, 0xB0, 0x60, LocalAddress, Peer.Raw, new byte[] { 0, 0 });
        var sync = await BeginSynchronizationAsync(template, bounded.Token).ConfigureAwait(false);
        try
        {
            await boundedControlSend(sync.CopyFrame(), bounded.Token).ConfigureAwait(false);
            await sync.Completion.WaitAsync(bounded.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested(); EnsureEpoch(operation);
        }
        finally { CancelPending(sync); }
    }

    public async Task<KnxIndividualSynchronization> BeginSynchronizationAsync(CemiLDataFrame transportTemplate, CancellationToken ct = default)
    {
        KnxIndividualSecureCodec.ValidateEnvelope(transportTemplate); KnxIndividualSecureCodec.ValidateTpdu(transportTemplate.Apdu);
        if (transportTemplate.MessageCode != CemiMessageCode.LDataReq || transportTemplate.Source != LocalAddress || transportTemplate.DestinationRaw != Peer.Raw)
            throw new ArgumentException("A Sync request template must carry this exact local/peer tuple.");
        using var operation = Admit(ct); await _serial.WaitAsync(operation.Token).ConfigureAwait(false); byte[]? challenge = null; var started = false;
        try
        {
            lock (_gate)
            {
                EnsureEpochLocked(operation); ExpirePendingLocked();
                if (_pending is not null) throw new InvalidOperationException("Only one Sync challenge may be outstanding for a peer.");
                if (_state == KnxIndividualSecureState.Faulted) throw new InvalidOperationException("A faulted context requires explicit recovery and a new owner.");
                _state = KnxIndividualSecureState.Synchronizing; started = true;
            }
            // Deliberately consume this nonce even though the wire service does not advance the ordinary S-A_Data counter.
            // A new challenge or later S-A_Data must never reuse its CTR stream after failure, cancellation or restart.
            var reservation = await _store.ReserveSendCountersAsync(ct: operation.Token).ConfigureAwait(false);
            challenge = RandomNumberGenerator.GetBytes(6);
            var wire = KnxIndividualSecureCodec.ProtectSyncRequest(transportTemplate, reservation.First, challenge, _key, KeyKind);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ticket = new KnxIndividualSynchronization(wire, reservation.First, completion.Task);
            lock (_gate)
            {
                EnsureEpochLocked(operation); operation.Token.ThrowIfCancellationRequested();
                _pending = new(ticket, challenge, _time.GetTimestamp(), completion, (byte)(transportTemplate.Apdu[0] & 0x40)); challenge = null;
            }
            return ticket;
        }
        catch (Exception ex)
        {
            lock (_gate) if (started && ReferenceEquals(operation.Epoch, _epoch) && !_disposed)
                _state = ex is DataSecureCounterStateException or IOException ? KnxIndividualSecureState.Faulted : KnxIndividualSecureState.SynchronizationRequired;
            throw;
        }
        finally { if (challenge is not null) CryptographicOperations.ZeroMemory(challenge); _serial.Release(); }
    }

    public async Task<byte[]> CopyPendingRetryAsync(KnxIndividualSynchronization operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation); using var admitted = Admit(ct); await _serial.WaitAsync(admitted.Token).ConfigureAwait(false);
        try { lock (_gate) { EnsureEpochLocked(admitted); ExpirePendingLocked(); if (_pending?.Ticket.Token != operation.Token) throw new InvalidOperationException("This Sync request is no longer pending."); return operation.CopyFrame(); } }
        finally { _serial.Release(); }
    }
    internal async Task<byte[]?> MatchPendingSynchronizationAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (frame.Length > 519) return null; var owned = frame.ToArray(); using var operation = Admit(ct);
        await _serial.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                EnsureEpochLocked(operation); ExpirePendingLocked();
                if (_pending is null) return null; var expected = _pending.Ticket.CopyFrame();
                return owned.AsSpan().SequenceEqual(expected) ? expected : null;
            }
        }
        finally { _serial.Release(); }
    }

    public async Task<bool> HandleSynchronizationResponseAsync(ReadOnlyMemory<byte> cemi, CancellationToken ct = default)
    {
        if (cemi.Length > 519) return false; var owned = cemi.ToArray(); using var operation = Admit(ct);
        await _serial.WaitAsync(operation.Token).ConfigureAwait(false); byte[]? challenge = null; Pending? pending = null;
        try
        {
            lock (_gate)
            {
                EnsureEpochLocked(operation); ExpirePendingLocked(); pending = _pending;
                if (pending is null) return false; challenge = pending.Challenge.ToArray();
            }
            CemiLDataFrame frame;
            try { frame = Cemi.ParseLDataFrame(owned); } catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
            if (frame.MessageCode != CemiMessageCode.LDataInd || frame.Source != Peer || frame.DestinationRaw != LocalAddress.Raw
                || (frame.Apdu[0] & 0x40) != pending.TransportMode) return false;
            if (!KnxIndividualSecureCodec.TryUnprotectSyncResponse(owned, _key, KeyKind, challenge, out var response, out _)) return false;
            operation.Token.ThrowIfCancellationRequested(); EnsureEpoch(operation);
            await _store.MergeAuthenticatedSyncAsync(Peer, response!.RemoteNextSequence, response.LocalNextSequence, operation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                EnsureEpochLocked(operation); operation.Token.ThrowIfCancellationRequested(); ExpirePendingLocked();
                if (!ReferenceEquals(_pending, pending)) return false;
                _state = KnxIndividualSecureState.Ready; ClearPendingLocked(null);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException)
        { lock (_gate) if (!_disposed && ReferenceEquals(operation.Epoch, _epoch)) { _state = KnxIndividualSecureState.Faulted; ClearPendingLocked(ex); } throw; }
        finally { if (challenge is not null) CryptographicOperations.ZeroMemory(challenge); _serial.Release(); }
    }

    public async Task<byte[]> ProtectDataAsync(ReadOnlyMemory<byte> plainCemi, CancellationToken ct = default)
    {
        if (plainCemi.Length > 519) throw new ArgumentOutOfRangeException(nameof(plainCemi)); var owned = plainCemi.ToArray();
        var frame = Cemi.ParseLDataFrame(owned); KnxIndividualSecureCodec.ValidateEnvelope(frame); KnxIndividualSecureCodec.ValidateTpdu(frame.Apdu);
        if (frame.MessageCode != CemiMessageCode.LDataReq || frame.Source != LocalAddress || frame.DestinationRaw != Peer.Raw) throw new ArgumentException("Outgoing data must match the provisioned local/peer tuple.");
        if (frame.Apdu.Length + 13 > MaximumWireTpduLength) throw new ArgumentOutOfRangeException(nameof(plainCemi), "The secured TPDU exceeds the explicitly provisioned peer capacity.");
        using var operation = Admit(ct); await _serial.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            RequireReady(operation); var counter = await _store.ReserveSendCountersAsync(ct: operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested(); RequireReady(operation);
            return KnxIndividualSecureCodec.ProtectData(frame, counter.First, _key, KeyKind);
        }
        catch (IOException) { Fault(operation); throw; }
        finally { _serial.Release(); }
    }

    public async Task<KnxIndividualSecureReceiveResult> ReceiveDataAsync(ReadOnlyMemory<byte> securedCemi, CancellationToken ct = default)
    {
        if (securedCemi.Length > 519) return new(KnxIndividualSecureReceiveFailure.InvalidFrame, null); var owned = securedCemi.ToArray();
        using var operation = Admit(ct); await _serial.WaitAsync(operation.Token).ConfigureAwait(false); KnxIndividualSecureData? decoded = null;
        try
        {
            lock (_gate) { EnsureEpochLocked(operation); if (_state != KnxIndividualSecureState.Ready) return new(KnxIndividualSecureReceiveFailure.SynchronizationRequired, null); }
            CemiLDataFrame frame;
            try { frame = Cemi.ParseLDataFrame(owned); } catch (Exception ex) when (ex is FormatException or ArgumentException) { return new(KnxIndividualSecureReceiveFailure.InvalidFrame, null); }
            if (frame.MessageCode != CemiMessageCode.LDataInd || frame.Source != Peer || frame.DestinationRaw != LocalAddress.Raw) return new(KnxIndividualSecureReceiveFailure.ContextMismatch, null);
            if (frame.Apdu.Length > MaximumWireTpduLength) return new(KnxIndividualSecureReceiveFailure.InvalidFrame, null);
            if (!KnxIndividualSecureCodec.TryUnprotectData(owned, _key, KeyKind, out decoded, out var issue))
                return new(issue == KnxIndividualSecureDecodeIssue.AuthenticationFailed ? KnxIndividualSecureReceiveFailure.AuthenticationFailed : KnxIndividualSecureReceiveFailure.InvalidFrame, null);
            if (!await _store.TryAcceptReceiveAsync(Peer, decoded!.Sequence, operation.Token).ConfigureAwait(false)) return new(KnxIndividualSecureReceiveFailure.Replay, null);
            operation.Token.ThrowIfCancellationRequested(); RequireReady(operation); var admitted = decoded; decoded = null; return new(KnxIndividualSecureReceiveFailure.None, admitted);
        }
        catch (DataSecureCounterStateException ex) { Fault(operation); return new(ex.Failure == DataSecureCounterFailure.SynchronizationRequired ? KnxIndividualSecureReceiveFailure.SynchronizationRequired : KnxIndividualSecureReceiveFailure.StateUnavailable, null); }
        catch (IOException) { Fault(operation); return new(KnxIndividualSecureReceiveFailure.StateUnavailable, null); }
        finally { if (decoded is not null) CryptographicOperations.ZeroMemory(decoded.Tpdu); _serial.Release(); }
    }

    private void RequireReady(Operation operation) { lock (_gate) { EnsureEpochLocked(operation); if (_state != KnxIndividualSecureState.Ready) throw new InvalidOperationException("Fresh authenticated synchronization is required before individual data."); } }
    private void EnsureEpoch(Operation operation) { lock (_gate) EnsureEpochLocked(operation); }
    private void EnsureEpochLocked(Operation operation) { if (_disposed || !ReferenceEquals(operation.Epoch, _epoch)) throw new OperationCanceledException("The individual secure connection lifetime changed.", operation.Token); }
    private void Fault(Operation operation) { lock (_gate) if (!_disposed && ReferenceEquals(operation.Epoch, _epoch)) _state = KnxIndividualSecureState.Faulted; }
    private void ExpirePendingLocked()
    {
        if (_pending is not null && _time.GetElapsedTime(_pending.Started, _time.GetTimestamp()) >= SynchronizationTimeout)
        { _state = KnxIndividualSecureState.SynchronizationRequired; ClearPendingLocked(new TimeoutException("The authenticated Sync response deadline expired.")); }
    }
    private void CancelPending(KnxIndividualSynchronization ticket)
    {
        lock (_gate) if (_pending?.Ticket.Token == ticket.Token)
        { if (!_disposed && _state != KnxIndividualSecureState.Faulted) _state = KnxIndividualSecureState.SynchronizationRequired; ClearPendingLocked(new OperationCanceledException("The synchronization operation ended.")); }
    }
    private void ClearPendingLocked(Exception? failure)
    {
        if (_pending is not { } pending) return; _pending = null; CryptographicOperations.ZeroMemory(pending.Challenge);
        if (failure is OperationCanceledException) pending.Completion.TrySetCanceled();
        else if (failure is not null) pending.Completion.TrySetException(failure); else pending.Completion.TrySetResult();
    }
    public void ResetConnection()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _epoch = new(); if (_state != KnxIndividualSecureState.Faulted) _state = KnxIndividualSecureState.SynchronizationRequired; ClearPendingLocked(new OperationCanceledException("The connection was reset.")); } }
    private sealed record Pending(KnxIndividualSynchronization Ticket, byte[] Challenge, long Started, TaskCompletionSource Completion, byte TransportMode);
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    private Operation Admit(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_operations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return new(this, CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token), _epoch); }
            catch { if (--_operations == 0) _drained.TrySetResult(); throw; }
        }
    }
    private sealed class Operation(KnxIndividualSecureContext owner, CancellationTokenSource cancellation, object epoch) : IDisposable
    {
        public CancellationToken Token => cancellation.Token; public object Epoch => epoch;
        public void Dispose() { cancellation.Dispose(); lock (owner._gate) if (--owner._operations == 0) owner._drained.TrySetResult(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task task;
        lock (_gate) { if (_disposal is null) { _disposed = true; _state = KnxIndividualSecureState.Disposed; ClearPendingLocked(new OperationCanceledException("The secure context was disposed.")); owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; } task = _disposal; }
        if (owner is not null)
        {
            Exception? cancellationFailure = null; try { _lifetime.Cancel(); } catch (Exception ex) { cancellationFailure = ex; }
            _ = FinishDisposeAsync(owner, cancellationFailure);
        }
        return new(task);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion, Exception? cancellationFailure)
    {
        try
        {
            Task drained; lock (_gate) drained = _drained.Task; await drained.ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(_key); _lifetime.Dispose(); _serial.Dispose();
            if (cancellationFailure is not null) completion.TrySetException(cancellationFailure); else completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
