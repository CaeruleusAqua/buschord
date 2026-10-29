using System.Security.Cryptography;
using System.Text.Json;

namespace Knx.Bus.Security;

/// <summary>Caller-owned configuration input; a group context takes independent copies of key and sender data.</summary>
public sealed record DataSecureGroupConfiguration(GroupAddress Group, ReadOnlyMemory<byte> Key, KnxDataProtection Protection,
    IReadOnlyCollection<IndividualAddress> PermittedSenders);
public enum DataSecureReceiveFailure { None, InvalidFrame, UnknownGroup, UnauthorizedSender, PlaintextForbidden, UnsupportedService, AuthenticationFailed, ProtectionDowngrade, Replay, StateUnavailable, SynchronizationRequired }
public sealed record DataSecureGroupReceiveResult(bool Accepted, DataSecureReceiveFailure Failure, KnxDataSecureDecoded? Data);

/// <summary>
/// A fixed project/local-sender/key-epoch group context. It selects the key and sender policy itself, reserves durable
/// outgoing counters and commits authenticated receive floors before releasing application data. It borrows its counter store.
/// </summary>
public sealed class KnxDataSecureGroupContext : IAsyncDisposable
{
    private readonly DataSecureCounterStore _store;
    private readonly Dictionary<ushort, GroupBinding> _groups = [];
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private bool _disposed;
    private Task? _disposal;
    public string ConfigurationFingerprint { get; }
    public int GroupCount => _groups.Count;
    public IndividualAddress LocalAddress => _store.LocalAddress;
    public IReadOnlyList<GroupAddress> Groups
    { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return Array.AsReadOnly(_groups.Keys.Select(raw => new GroupAddress(raw)).ToArray()); } } }

    public KnxDataSecureGroupContext(DataSecureCounterStore store, DataSecureStateIdentity expectedIdentity, IEnumerable<DataSecureGroupConfiguration> groups)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(expectedIdentity); ArgumentNullException.ThrowIfNull(groups);
        if (store.IdentityFingerprint != expectedIdentity.Fingerprint || store.LocalAddress != expectedIdentity.LocalAddress)
            throw new ArgumentException("The counter store belongs to another project, sender or key epoch.");
        _store = store; var known = store.KnownSenders.ToHashSet();
        try
        {
            foreach (var configuration in groups)
            {
                if (configuration is null || configuration.Key.Length != 16 || configuration.Group.Raw == 0
                    || configuration.PermittedSenders is null || _groups.Count >= ushort.MaxValue
                    || configuration.Protection is not (KnxDataProtection.Authentication or KnxDataProtection.AuthenticationAndConfidentiality))
                    throw new ArgumentException("Invalid explicit secure group configuration.", nameof(groups));
                var senders = configuration.PermittedSenders.ToHashSet();
                if (senders.Count != configuration.PermittedSenders.Count || senders.Any(sender => !known.Contains(sender)))
                    throw new ArgumentException("Each permitted sender needs an explicit trusted receive floor.", nameof(groups));
                if (_groups.ContainsKey(configuration.Group.Raw)) throw new ArgumentException("Duplicate secure group configuration.", nameof(groups));
                _groups.Add(configuration.Group.Raw, new(configuration.Key.ToArray(), configuration.Protection, senders));
            }
            if (_groups.Count == 0) throw new ArgumentException("At least one secure group is required.", nameof(groups));
            ConfigurationFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                profile = "DataSecureGroupContext1", store = store.IdentityFingerprint,
                groups = _groups.OrderBy(p => p.Key).Select(p => new { group = p.Key, key = Convert.ToHexString(SHA256.HashData(p.Value.Key)),
                    protection = p.Value.Protection, senders = p.Value.Senders.Select(s => s.Raw).Order().ToArray() }).ToArray()
            })));
        }
        catch { foreach (var binding in _groups.Values) CryptographicOperations.ZeroMemory(binding.Key); _groups.Clear(); throw; }
    }

    /// <summary>Produces an L_Data.req only after its unique send sequence is durably reserved; no network operation occurs here.</summary>
    public async Task<byte[]> ProtectCemiAsync(ReadOnlyMemory<byte> plainCemi, CancellationToken ct = default)
    {
        var frame = Cemi.ParseLDataFrame(plainCemi.Span); KnxDataSecureCodec.ValidatePlainCemi(frame);
        using var cancellation = Link(ct); await _serial.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (frame.MessageCode != CemiMessageCode.LDataReq || frame.Source != _store.LocalAddress || !_groups.TryGetValue(frame.DestinationRaw, out var binding))
                throw new InvalidOperationException("The frame is outside the current project's local sender or secure group policy.");
            var counters = await _store.ReserveSendCountersAsync(ct: cancellation.Token).ConfigureAwait(false); cancellation.Token.ThrowIfCancellationRequested();
            return KnxDataSecureCodec.ProtectCemi(frame, counters.First, binding.Key, binding.Protection);
        }
        finally { _serial.Release(); }
    }

    /// <summary>Rejects unknown groups/senders, plaintext downgrades and replays. State failures release no application payload.</summary>
    public async Task<DataSecureGroupReceiveResult> ReceiveCemiAsync(ReadOnlyMemory<byte> cemi, CancellationToken ct = default)
    {
        if (cemi.Length > 519) return Rejected(DataSecureReceiveFailure.InvalidFrame);
        var owned = cemi.ToArray();
        CemiLDataFrame frame;
        try { frame = Cemi.ParseLDataFrame(owned); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return Rejected(DataSecureReceiveFailure.InvalidFrame); }
        using var cancellation = Link(ct); await _serial.WaitAsync(cancellation.Token).ConfigureAwait(false);
        KnxDataSecureDecoded? decoded = null; var released = false;
        try
        {
            ThrowIfDisposed();
            if (frame.MessageCode != CemiMessageCode.LDataInd || (frame.Control2 & 0x80) == 0) return Rejected(DataSecureReceiveFailure.InvalidFrame);
            if (!_groups.TryGetValue(frame.DestinationRaw, out var binding)) return Rejected(DataSecureReceiveFailure.UnknownGroup);
            if (!binding.Senders.Contains(frame.Source)) return Rejected(DataSecureReceiveFailure.UnauthorizedSender);
            var apdu = frame.Apdu;
            if (apdu.Length < 2 || apdu[0] != 3 || apdu[1] != 0xF1) return Rejected(DataSecureReceiveFailure.PlaintextForbidden);
            if (!KnxDataSecureCodec.TryUnprotectCemi(owned, binding.Key, out decoded, out var issue))
                return Rejected(issue == KnxDataSecureDecodeIssue.AuthenticationFailed ? DataSecureReceiveFailure.AuthenticationFailed
                    : issue is KnxDataSecureDecodeIssue.UnsupportedContext or KnxDataSecureDecodeIssue.UnsupportedService ? DataSecureReceiveFailure.UnsupportedService : DataSecureReceiveFailure.InvalidFrame);
            if (binding.Protection == KnxDataProtection.AuthenticationAndConfidentiality && decoded!.Protection != binding.Protection)
                return Rejected(DataSecureReceiveFailure.ProtectionDowngrade);
            try
            {
                if (!await _store.TryAcceptReceiveAsync(decoded!.Source, decoded.Sequence, cancellation.Token).ConfigureAwait(false))
                    return Rejected(DataSecureReceiveFailure.Replay);
            }
            catch (DataSecureCounterStateException ex)
            { return Rejected(ex.Failure == DataSecureCounterFailure.SynchronizationRequired ? DataSecureReceiveFailure.SynchronizationRequired : DataSecureReceiveFailure.StateUnavailable); }
            catch (IOException) { return Rejected(DataSecureReceiveFailure.StateUnavailable); }
            cancellation.Token.ThrowIfCancellationRequested(); released = true; return new(true, DataSecureReceiveFailure.None, decoded);
        }
        finally { if (!released && decoded is not null) CryptographicOperations.ZeroMemory(decoded.Apdu); _serial.Release(); }
    }
    private static DataSecureGroupReceiveResult Rejected(DataSecureReceiveFailure failure) => new(false, failure, null);
    private sealed record GroupBinding(byte[] Key, KnxDataProtection Protection, HashSet<IndividualAddress> Senders);
    private CancellationTokenSource Link(CancellationToken ct)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token); } }
    private void ThrowIfDisposed() { lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this); }
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
            await _serial.WaitAsync().ConfigureAwait(false);
            try { foreach (var binding in _groups.Values) CryptographicOperations.ZeroMemory(binding.Key); _groups.Clear(); }
            finally { _serial.Release(); _lifetime.Dispose(); }
            if (cancellationFailure is not null) completion.TrySetException(cancellationFailure); else completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
