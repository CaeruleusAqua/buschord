using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Knx.Bus.Security;

/// <summary>
/// A tunnel's explicit mixed group policy. Protected groups come from its owned group context; every plain group or
/// individual management peer must be separately allowed. Unknown traffic is rejected. The counter store remains borrowed.
/// </summary>
public sealed class KnxDataSecureCemiProcessor : ICemiSecurityProcessor
{
    private const int MaximumPreparedConfirmations = 256;
    private readonly KnxDataSecureGroupContext _context;
    private readonly HashSet<ushort> _secureGroups, _plainGroups, _plainPeers;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LinkedList<Prepared> _prepared = [];
    private object _epoch = new();
    private int _operations;
    private TaskCompletionSource _drained = Completed();
    private bool _disposed;
    private Task? _disposal;
    private CemiSecurityRejection _lastRejection;
    public CemiSecurityRejection LastRejection { get { lock (_gate) return _lastRejection; } }

    public KnxDataSecureCemiProcessor(KnxDataSecureGroupContext context, IReadOnlyCollection<GroupAddress>? allowedPlainGroups = null,
        IReadOnlyCollection<IndividualAddress>? allowedPlainIndividualPeers = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _secureGroups = context.Groups.Select(group => group.Raw).ToHashSet();
        _plainGroups = (allowedPlainGroups ?? []).Select(group => group.Raw).ToHashSet();
        _plainPeers = (allowedPlainIndividualPeers ?? []).Select(peer => peer.Raw).ToHashSet();
        if (_plainGroups.Any(group => group == 0 || _secureGroups.Contains(group)) || _plainPeers.Any(peer => peer == 0 || (peer & 255) == 255))
            throw new ArgumentException("Plain policy cannot overlap protected groups or admit broadcast peers.");
    }

    public async Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        using var operation = Admit(ct);
        if (cemi.Length > 519) throw new ArgumentOutOfRangeException(nameof(cemi));
        var owned = cemi.ToArray(); var frame = Cemi.ParseLDataFrame(owned);
        if (assignedAddress != _context.LocalAddress || frame.Source != assignedAddress)
        { Reject(CemiSecurityRejection.AddressMismatch); throw new InvalidOperationException("The configured secure sender differs from the tunnel's assigned address."); }
        if (frame.MessageCode != CemiMessageCode.LDataReq) throw new InvalidOperationException("Outgoing cEMI must be L_Data.req.");
        byte[] wire;
        if (IsGroup(frame))
        {
            if (_secureGroups.Contains(frame.DestinationRaw)) wire = await _context.ProtectCemiAsync(owned, operation.Token).ConfigureAwait(false);
            else if (_plainGroups.Contains(frame.DestinationRaw) && !IsSecure(frame)) wire = owned;
            else { Reject(CemiSecurityRejection.PolicyDenied); throw new InvalidOperationException("The destination has no explicit group security policy."); }
        }
        else if (_plainPeers.Contains(frame.DestinationRaw) && !IsSecure(frame)) wire = owned;
        else { Reject(CemiSecurityRejection.PolicyDenied); throw new NotSupportedException("Secure individual/tool traffic requires a separate device-security profile."); }
        operation.Token.ThrowIfCancellationRequested(); var protectedFrame = Cemi.ParseLDataFrame(wire);
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(operation.Epoch, _epoch)) throw new OperationCanceledException("The cEMI connection lifetime changed.", operation.Token);
            _prepared.AddLast(new Prepared(Digest(protectedFrame), frame.Apdu));
            while (_prepared.Count > MaximumPreparedConfirmations) Remove(_prepared.First!);
            _lastRejection = CemiSecurityRejection.None;
        }
        return wire.ToArray();
    }

    public async Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        using var operation = Admit(ct);
        if (cemi.Length > 519) return Reject(CemiSecurityRejection.InvalidFrame);
        var owned = cemi.ToArray(); CemiLDataFrame frame;
        try { frame = Cemi.ParseLDataFrame(owned); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return Reject(CemiSecurityRejection.InvalidFrame); }
        if (assignedAddress != _context.LocalAddress) return Reject(CemiSecurityRejection.AddressMismatch);
        if (frame.MessageCode == CemiMessageCode.LDataCon)
        {
            if (frame.Source != assignedAddress) return Reject(CemiSecurityRejection.UnboundConfirmation);
            var digest = Digest(frame);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(operation.Epoch, _epoch)) return null;
                var node = _prepared.First;
                while (node is not null && node.Value.Digest != digest) node = node.Next;
                if (node is null) { _lastRejection = CemiSecurityRejection.UnboundConfirmation; return null; }
                var plain = node.Value.PlainApdu.ToArray(); Remove(node); _lastRejection = CemiSecurityRejection.None;
                return Cemi.EncodeLDataFrame(new(frame.MessageCode, frame.Control1, frame.Control2, frame.Source, frame.DestinationRaw, plain, frame.AdditionalInformation));
            }
        }
        if (frame.MessageCode != CemiMessageCode.LDataInd) return Reject(CemiSecurityRejection.InvalidFrame);
        byte[]? result;
        if (IsGroup(frame))
        {
            if (_secureGroups.Contains(frame.DestinationRaw))
            {
                var received = await _context.ReceiveCemiAsync(owned, operation.Token).ConfigureAwait(false);
                if (!received.Accepted)
                    return Reject(received.Failure switch
                    {
                        DataSecureReceiveFailure.AuthenticationFailed => CemiSecurityRejection.AuthenticationFailed,
                        DataSecureReceiveFailure.Replay => CemiSecurityRejection.Replay,
                        DataSecureReceiveFailure.StateUnavailable or DataSecureReceiveFailure.SynchronizationRequired => CemiSecurityRejection.StateUnavailable,
                        _ => CemiSecurityRejection.PolicyDenied
                    });
                try { result = received.Data!.ToPlainCemi(); }
                finally { CryptographicOperations.ZeroMemory(received.Data!.Apdu); }
            }
            else if (_plainGroups.Contains(frame.DestinationRaw) && !IsSecure(frame)) result = owned;
            else return Reject(CemiSecurityRejection.PolicyDenied);
        }
        else if (frame.DestinationRaw == assignedAddress.Raw && _plainPeers.Contains(frame.Source.Raw) && !IsSecure(frame)) result = owned;
        else return Reject(CemiSecurityRejection.PolicyDenied);
        operation.Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(operation.Epoch, _epoch)) { CryptographicOperations.ZeroMemory(result); return null; }
            _lastRejection = CemiSecurityRejection.None;
        }
        return result;
    }
    private byte[]? Reject(CemiSecurityRejection rejection) { lock (_gate) _lastRejection = rejection; return null; }
    private static bool IsGroup(CemiLDataFrame frame) => (frame.Control2 & 0x80) != 0;
    private static bool IsSecure(CemiLDataFrame frame) { var data = frame.Apdu; return data.Length >= 2 && (data[0] & 3) == 3 && data[1] == 0xF1; }
    private static string Digest(CemiLDataFrame frame)
    {
        var apdu = frame.Apdu; var bytes = new byte[5 + apdu.Length]; BinaryPrimitives.WriteUInt16BigEndian(bytes, frame.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), frame.DestinationRaw); bytes[4] = (byte)(frame.Control2 & 0x8F); apdu.CopyTo(bytes, 5);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private sealed record Prepared(string Digest, byte[] PlainApdu);
    private void Remove(LinkedListNode<Prepared> node) { CryptographicOperations.ZeroMemory(node.Value.PlainApdu); _prepared.Remove(node); }
    public void ResetConnection() { lock (_gate) { _epoch = new(); while (_prepared.First is { } first) Remove(first); } }
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    private Operation Admit(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return new(this, CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token), _epoch); }
            catch { if (--_operations == 0) _drained.TrySetResult(); throw; }
        }
    }
    private sealed class Operation(KnxDataSecureCemiProcessor owner, CancellationTokenSource cancellation, object epoch) : IDisposable
    {
        public CancellationToken Token => cancellation.Token;
        public object Epoch => epoch;
        public void Dispose() { cancellation.Dispose(); lock (owner._gate) if (--owner._operations == 0) owner._drained.TrySetResult(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task task;
        lock (_gate) { if (_disposal is null) { _disposed = true; owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; } task = _disposal; }
        if (owner is not null) { _lifetime.Cancel(); _ = FinishDisposeAsync(owner); } return new(task);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            Task drained; lock (_gate) drained = _drained.Task; await drained.ConfigureAwait(false);
            ResetConnection(); await _context.DisposeAsync().ConfigureAwait(false); _lifetime.Dispose(); completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
