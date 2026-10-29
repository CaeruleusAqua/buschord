using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Knx.Bus.Security;

/// <summary>
/// Owns one individual context and optional explicit policy for all other traffic. The target's data never falls back
/// to plain traffic. Only defined transport controls are passed, and they are not represented as authenticated data.
/// Client initialization is connectionless Sync before T_Connect; broadcast and device/server roles are unsupported.
/// </summary>
public sealed class KnxIndividualSecureCemiProcessor : ICemiSecurityProcessor, ICemiSecurityInitializer
{
    private readonly KnxIndividualSecureContext _context;
    private readonly ICemiSecurityProcessor? _otherPolicy;
    private readonly object _gate = new();
    private readonly LinkedList<Prepared> _prepared = [];
    private readonly CancellationTokenSource _lifetime = new();
    private object _epoch = new();
    private int _operations;
    private TaskCompletionSource _drained = Completed();
    private bool _disposed;
    private Task? _disposal;
    public bool IsInitialized => _context.State == KnxIndividualSecureState.Ready;
    public KnxIndividualSecureCemiProcessor(KnxIndividualSecureContext context, ICemiSecurityProcessor? otherTrafficPolicy = null)
    { _context = context ?? throw new ArgumentNullException(nameof(context)); _otherPolicy = otherTrafficPolicy; }

    public async Task InitializeAsync(IndividualAddress assignedAddress, Func<ReadOnlyMemory<byte>, CancellationToken, Task> boundedControlSend, CancellationToken ct = default)
    {
        using var operation = Admit(ct); RequireAddress(assignedAddress);
        await _context.InitializeAsync(boundedControlSend, operation.Token).ConfigureAwait(false); EnsureEpoch(operation);
    }
    public async Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        using var operation = Admit(ct); RequireAddress(assignedAddress); if (cemi.Length > 519) throw new ArgumentOutOfRangeException(nameof(cemi));
        var owned = cemi.ToArray(); var frame = Cemi.ParseLDataFrame(owned);
        if (frame.MessageCode != CemiMessageCode.LDataReq || frame.Source != assignedAddress) throw new InvalidOperationException("Outgoing cEMI must carry the actual assigned source.");
        if ((frame.Control2 & 0x80) != 0 || frame.DestinationRaw != _context.Peer.Raw)
        {
            if (_otherPolicy is null) throw new InvalidOperationException("No explicit policy admits this destination.");
            var result = await _otherPolicy.PrepareOutgoingAsync(owned, assignedAddress, operation.Token).ConfigureAwait(false); EnsureEpoch(operation); return result.ToArray();
        }
        KnxIndividualSecureCodec.ValidateEnvelope(frame); var tpdu = frame.Apdu;
        if (tpdu.Length == 1)
        {
            if (tpdu[0] != 0x81 && (!IsInitialized || tpdu[0] != 0x80 && !AckOrNak(tpdu[0]))) throw new InvalidOperationException("Transport control is not admitted in the current secure state.");
            EnsureEpoch(operation); return owned;
        }
        var pending = await _context.MatchPendingSynchronizationAsync(owned, operation.Token).ConfigureAwait(false);
        var wire = pending ?? await _context.ProtectDataAsync(owned, operation.Token).ConfigureAwait(false);
        var protectedFrame = Cemi.ParseLDataFrame(wire); EnsureEpoch(operation);
        lock (_gate)
        {
            EnsureEpochLocked(operation); _prepared.AddLast(new Prepared(Digest(protectedFrame), pending is null ? tpdu : null));
            while (_prepared.Count > 64) Remove(_prepared.First!);
        }
        return wire.ToArray();
    }
    public async Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        using var operation = Admit(ct); RequireAddress(assignedAddress); if (cemi.Length > 519) return null; var owned = cemi.ToArray(); CemiLDataFrame frame;
        try { frame = Cemi.ParseLDataFrame(owned); } catch (Exception ex) when (ex is ArgumentException or FormatException) { return null; }
        if (frame.MessageCode == CemiMessageCode.LDataCon && frame.Source == assignedAddress && frame.DestinationRaw == _context.Peer.Raw && (frame.Control2 & 0x80) == 0)
        {
            var digest = Digest(frame); lock (_gate)
            {
                EnsureEpochLocked(operation); var node = _prepared.First; while (node is not null && node.Value.Digest != digest) node = node.Next;
                if (node is null) return null; var plain = node.Value.Plain?.ToArray(); Remove(node);
                if (plain is null) return null;
                try { return Cemi.EncodeLDataFrame(new(frame.MessageCode, frame.Control1, frame.Control2, frame.Source, frame.DestinationRaw, plain, frame.AdditionalInformation)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        if ((frame.Control2 & 0x80) != 0 || frame.Source != _context.Peer || frame.DestinationRaw != assignedAddress.Raw)
        {
            if (_otherPolicy is null) return null; var other = await _otherPolicy.ProcessIncomingAsync(owned, assignedAddress, operation.Token).ConfigureAwait(false); EnsureEpoch(operation); return other?.ToArray();
        }
        if (frame.MessageCode != CemiMessageCode.LDataInd) return null;
        try { KnxIndividualSecureCodec.ValidateEnvelope(frame); } catch (NotSupportedException) { return null; }
        var tpdu = frame.Apdu;
        if (tpdu.Length == 1)
        {
            if (tpdu[0] == 0x81) { ResetConnection(); return owned; }
            if (!IsInitialized || !AckOrNak(tpdu[0])) return null; EnsureEpoch(operation); return owned;
        }
        if (tpdu.Length >= 3 && (tpdu[0] & 3) == 3 && tpdu[1] == 0xF1 && (tpdu[2] & 7) == 3)
        { await _context.HandleSynchronizationResponseAsync(owned, operation.Token).ConfigureAwait(false); EnsureEpoch(operation); return null; }
        var received = await _context.ReceiveDataAsync(owned, operation.Token).ConfigureAwait(false);
        if (!received.Accepted) return null;
        try { EnsureEpoch(operation); return received.Data!.ToPlainCemi(); }
        finally { CryptographicOperations.ZeroMemory(received.Data!.Tpdu); }
    }
    private void RequireAddress(IndividualAddress address) { if (address != _context.LocalAddress) throw new InvalidOperationException("The actual assigned address differs from the provisioned secure sender."); }
    private static bool AckOrNak(byte value) => (value & 0xC3) is 0xC2 or 0xC3;
    private static string Digest(CemiLDataFrame frame)
    {
        var tpdu = frame.Apdu; var bytes = new byte[5 + tpdu.Length]; BinaryPrimitives.WriteUInt16BigEndian(bytes, frame.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), frame.DestinationRaw); bytes[4] = (byte)(frame.Control2 & 0x8F); tpdu.CopyTo(bytes, 5);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private sealed record Prepared(string Digest, byte[]? Plain);
    private void Remove(LinkedListNode<Prepared> node) { if (node.Value.Plain is not null) CryptographicOperations.ZeroMemory(node.Value.Plain); _prepared.Remove(node); }
    public void ResetConnection()
    {
        lock (_gate) { _epoch = new(); while (_prepared.First is { } node) Remove(node); }
        _context.ResetConnection(); _otherPolicy?.ResetConnection();
    }
    private void EnsureEpoch(Operation operation) { operation.Token.ThrowIfCancellationRequested(); lock (_gate) EnsureEpochLocked(operation); }
    private void EnsureEpochLocked(Operation operation) { if (_disposed || !ReferenceEquals(operation.Epoch, _epoch)) throw new OperationCanceledException("The individual cEMI connection lifetime changed.", operation.Token); }
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
    private sealed class Operation(KnxIndividualSecureCemiProcessor owner, CancellationTokenSource cancellation, object epoch) : IDisposable
    { public CancellationToken Token => cancellation.Token; public object Epoch => epoch; public void Dispose() { cancellation.Dispose(); lock (owner._gate) if (--owner._operations == 0) owner._drained.TrySetResult(); } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task task;
        lock (_gate) { if (_disposal is null) { _disposed = true; owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; } task = _disposal; }
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
            lock (_gate) while (_prepared.First is { } node) Remove(node);
            try { await _context.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (_otherPolicy is not null) await _otherPolicy.DisposeAsync().ConfigureAwait(false); }
                finally { _lifetime.Dispose(); }
            }
            if (cancellationFailure is not null) completion.TrySetException(cancellationFailure); else completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
