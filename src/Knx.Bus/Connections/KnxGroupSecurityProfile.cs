using System.Security.Cryptography;
using System.Text.Json;
using Knx.Bus.Security;

namespace Knx.Bus.Connections;

/// <summary>Explicit receive policy; the signed interface sender list is an upper bound, not an automatic selection.</summary>
public sealed record KnxSecureGroupSelection(GroupAddress Group, KnxDataProtection Protection, IReadOnlyCollection<IndividualAddress> PermittedSenders);

/// <summary>Trusted provisioning boundary: opens existing ordinary state only, owned by the returned operation.</summary>
public interface IKnxGroupCounterStateSource
{
    Task<DataSecureCounterStore> OpenExistingAsync(DataSecureStateIdentity identity, CancellationToken ct);
}

/// <summary>Caller-owned key capability. This source never bootstraps, repairs or synchronizes counter state.</summary>
public sealed class ExistingGroupCounterStateSource : IKnxGroupCounterStateSource, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly byte[] _integrity;
    private readonly IDataSecureMonotonicAnchor? _anchor;
    private bool _disposed;
    public ExistingGroupCounterStateSource(string path, ReadOnlySpan<byte> integrityKey, IDataSecureMonotonicAnchor anchor)
        : this(path, integrityKey, anchor, false) { }
    /// <summary>Explicitly trusts the CURRENT unanchored file. Cannot detect a valid full-file restoration, even across processes.</summary>
    public static ExistingGroupCounterStateSource FromTrustedUnanchoredCurrentState(string path, ReadOnlySpan<byte> integrityKey)
        => new(path, integrityKey, null, true);
    private ExistingGroupCounterStateSource(string path, ReadOnlySpan<byte> integrityKey, IDataSecureMonotonicAnchor? anchor, bool trustCurrent)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || integrityKey.Length != 32 || anchor is null && !trustCurrent) throw new ArgumentException();
            _path = Path.GetFullPath(path); _integrity = integrityKey.ToArray(); _anchor = anchor;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); }
    }
    public async Task<DataSecureCounterStore> OpenExistingAsync(DataSecureStateIdentity identity, CancellationToken ct)
    {
        byte[] key;
        lock (_gate) { if (_disposed) throw new KnxConnectionException(KnxConnectionFailure.Disposed); key = _integrity.ToArray(); }
        try { return await DataSecureCounterStore.OpenAsync(_path, identity, key, _anchor, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw KnxGroupSecurityProfile.StateFailure(error); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_integrity); } }
    public override string ToString() => "[existing ordinary group counter state]";
}

/// <summary>
/// Stable physical installation/ordinary epoch, independent of project copies, chosen groups and transport sessions.
/// Must share the exact identity/store with any ordinary P2P use of this local sender. The external key inventory is
/// a caller attestation, not discoverable completeness; it must cover Tool/P2P keys absent from the signed export.
/// </summary>
public sealed class KnxGroupSecurityProfile
{
    private readonly string _installation, _epoch;
    internal IKnxGroupCounterStateSource StateSource { get; }
    public IReadOnlyList<KnxSecureGroupSelection> Groups { get; }
    public IReadOnlyList<GroupAddress> AllowedPlainGroups { get; }
    public IReadOnlyList<IndividualAddress> AllowedPlainIndividualPeers { get; }
    internal IReadOnlyList<string> ExternalIndividualKeyFingerprints { get; }
    internal string SelectionId { get; }
    public KnxGroupSecurityProfile(string physicalInstallationScope, string ordinaryKeyEpoch,
        IReadOnlyCollection<KnxSecureGroupSelection> groups, IKnxGroupCounterStateSource stateSource,
        IReadOnlyCollection<string> externalIndividualKeyFingerprints,
        IReadOnlyCollection<GroupAddress>? allowedPlainGroups = null, IReadOnlyCollection<IndividualAddress>? allowedPlainIndividualPeers = null)
    {
        if (string.IsNullOrWhiteSpace(physicalInstallationScope) || physicalInstallationScope.Length > 128 || physicalInstallationScope.Contains('\0')
            || string.IsNullOrWhiteSpace(ordinaryKeyEpoch) || ordinaryKeyEpoch.Length > 128 || ordinaryKeyEpoch.Contains('\0')
            || ordinaryKeyEpoch.StartsWith("ToolAccess:", StringComparison.OrdinalIgnoreCase)
            || stateSource is null || groups is null || groups.Count is < 1 or > 4096 || externalIndividualKeyFingerprints is null
            || externalIndividualKeyFingerprints.Count > 65534 || externalIndividualKeyFingerprints.Any(hash => hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)))
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var owned = new List<KnxSecureGroupSelection>(); var selected = new HashSet<ushort>();
        foreach (var group in groups)
        {
            if (group is null || group.Group.Raw == 0 || !selected.Add(group.Group.Raw)
                || group.Protection is not (KnxDataProtection.Authentication or KnxDataProtection.AuthenticationAndConfidentiality)
                || group.PermittedSenders is null || group.PermittedSenders.Count is < 1 or > 65534
                || group.PermittedSenders.Any(sender => sender.Raw == 0 || sender.Device == 255)
                || group.PermittedSenders.Distinct().Count() != group.PermittedSenders.Count)
                throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            owned.Add(new(group.Group, group.Protection, Array.AsReadOnly(group.PermittedSenders.OrderBy(sender => sender.Raw).ToArray())));
        }
        var plainGroups = (allowedPlainGroups ?? []).ToArray(); var plainPeers = (allowedPlainIndividualPeers ?? []).ToArray();
        if (plainGroups.Length > 4096 || plainPeers.Length > 4096 || plainGroups.Any(group => group.Raw == 0 || selected.Contains(group.Raw))
            || plainGroups.Distinct().Count() != plainGroups.Length || plainPeers.Any(peer => peer.Raw == 0 || peer.Device == 255)
            || plainPeers.Distinct().Count() != plainPeers.Length)
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        _installation = physicalInstallationScope; _epoch = ordinaryKeyEpoch; StateSource = stateSource;
        Groups = Array.AsReadOnly(owned.OrderBy(group => group.Group.Raw).ToArray());
        AllowedPlainGroups = Array.AsReadOnly(plainGroups); AllowedPlainIndividualPeers = Array.AsReadOnly(plainPeers);
        ExternalIndividualKeyFingerprints = Array.AsReadOnly(externalIndividualKeyFingerprints.Select(hash => hash.ToUpperInvariant()).Distinct().Order().ToArray());
        SelectionId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            installation = _installation, epoch = _epoch,
            groups = Groups.Select(group => new { group = group.Group.Raw, protection = group.Protection, senders = group.PermittedSenders.Select(sender => sender.Raw).ToArray() }),
            plainGroups = plainGroups.Select(group => group.Raw).Order(), plainPeers = plainPeers.Select(peer => peer.Raw).Order(), external = ExternalIndividualKeyFingerprints
        })));
    }
    internal DataSecureStateIdentity Bind(IndividualAddress local)
    { try { return new(_installation, _epoch, local); } catch (ArgumentException) { throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); } }
    internal static KnxConnectionException StateFailure(Exception error) => new(error is DataSecureCounterStateException state ? state.Failure switch
    {
        DataSecureCounterFailure.ContextMismatch => KnxConnectionFailure.GroupSecurityBindingMismatch,
        DataSecureCounterFailure.SynchronizationRequired or DataSecureCounterFailure.UncertainAnchor => KnxConnectionFailure.GroupSecurityStateNotReady,
        DataSecureCounterFailure.Exhausted => KnxConnectionFailure.GroupSecurityCounterExhausted,
        _ => KnxConnectionFailure.GroupSecurityStateUnavailable,
    } : KnxConnectionFailure.GroupSecurityStateUnavailable);
    public override string ToString() => "[explicit ordinary group security profile]";
}
