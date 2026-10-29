using System.Security.Cryptography;
using Knx.Bus.Security;

namespace Knx.Bus.Connections;

/// <summary>Opens existing provisioned state only. The returned store is owned by the gateway operation.</summary>
public interface IKnxToolCounterStateSource
{
    Task<DataSecureCounterStore> OpenExistingAsync(KnxIndividualSecureCounterBinding binding, CancellationToken ct);
}

/// <summary>Caller-owned integrity capability; no state, directory or trusted floor is created here.</summary>
public sealed class ExistingToolCounterStateSource : IKnxToolCounterStateSource, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly byte[] _integrity;
    private readonly IDataSecureMonotonicAnchor? _anchor;
    private bool _disposed;
    public ExistingToolCounterStateSource(string path, ReadOnlySpan<byte> integrityKey, IDataSecureMonotonicAnchor anchor)
        : this(path, integrityKey, anchor, false) { }
    /// <summary>The provisioning owner explicitly vouches for current unanchored state. This cannot detect an older valid file restored across processes.</summary>
    public static ExistingToolCounterStateSource FromTrustedUnanchoredState(string path, ReadOnlySpan<byte> integrityKey)
        => new(path, integrityKey, null, true);
    private ExistingToolCounterStateSource(string path, ReadOnlySpan<byte> integrityKey, IDataSecureMonotonicAnchor? anchor, bool trustedUnanchored)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || integrityKey.Length != 32 || anchor is null && !trustedUnanchored) throw new ArgumentException();
            _path = Path.GetFullPath(path); _integrity = integrityKey.ToArray(); _anchor = anchor;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); }
    }
    public async Task<DataSecureCounterStore> OpenExistingAsync(KnxIndividualSecureCounterBinding binding, CancellationToken ct)
    {
        byte[] key;
        lock (_gate) { if (_disposed) throw new KnxConnectionException(KnxConnectionFailure.Disposed); key = _integrity.ToArray(); }
        try { return await DataSecureCounterStore.OpenAsync(_path, binding.Identity, key, _anchor, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw KnxToolAccessProfile.StateFailure(error); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_integrity); } }
    public override string ToString() => "[existing Tool-Access counter state]";
}

/// <summary>
/// Explicit management-only provisioning facts. The ordinary P2P fingerprint inventory must be complete for this
/// physical installation; an empty supplied collection explicitly declares no ordinary P2P keys. No keys are inferred.
/// </summary>
public sealed class KnxToolAccessProfile
{
    private readonly string _installation, _epoch;
    internal IKnxToolCounterStateSource StateSource { get; }
    internal IReadOnlyList<string> OrdinaryPointToPointKeyFingerprints { get; }
    public IndividualAddress Target { get; }
    public int MaximumWireTpduLength { get; }
    public KnxToolAccessProfile(IndividualAddress target, string physicalInstallationScope, string toolKeyEpoch,
        int maximumWireTpduLength, IKnxToolCounterStateSource stateSource, IReadOnlyCollection<string> ordinaryPointToPointKeyFingerprints)
    {
        if (target.Raw == 0 || target.Device == 255 || maximumWireTpduLength is < 25 or > 255 || stateSource is null
            || ordinaryPointToPointKeyFingerprints is null || ordinaryPointToPointKeyFingerprints.Count > 65534
            || ordinaryPointToPointKeyFingerprints.Any(hash => hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)))
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        if (string.IsNullOrWhiteSpace(physicalInstallationScope) || physicalInstallationScope.Length > 128 || physicalInstallationScope.Contains('\0')
            || string.IsNullOrWhiteSpace(toolKeyEpoch) || toolKeyEpoch.Length > 128 || toolKeyEpoch.Contains('\0'))
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        _installation = physicalInstallationScope; _epoch = toolKeyEpoch; Target = target; StateSource = stateSource;
        MaximumWireTpduLength = maximumWireTpduLength;
        OrdinaryPointToPointKeyFingerprints = Array.AsReadOnly(ordinaryPointToPointKeyFingerprints.Select(hash => hash.ToUpperInvariant()).Distinct().ToArray());
    }
    internal KnxIndividualSecureCounterBinding Bind(IndividualAddress local)
    {
        try { return KnxIndividualSecureCounterBinding.ToolAccess(_installation, _epoch, local); }
        catch (ArgumentException) { throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); }
    }
    internal static KnxConnectionException StateFailure(Exception error) => error is DataSecureCounterStateException state
        ? new(state.Failure switch
        {
            DataSecureCounterFailure.ContextMismatch => KnxConnectionFailure.ToolAccessBindingMismatch,
            DataSecureCounterFailure.SynchronizationRequired or DataSecureCounterFailure.UncertainAnchor => KnxConnectionFailure.ToolAccessStateNotReady,
            DataSecureCounterFailure.Exhausted => KnxConnectionFailure.ToolAccessCounterExhausted,
            _ => KnxConnectionFailure.ToolAccessStateUnavailable,
        }) : new(KnxConnectionFailure.ToolAccessStateUnavailable);
    public override string ToString() => "[explicit Tool-Access management profile]";
}
