using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>A private project's local sender and explicit key epoch. Changing an epoch requires trusted provisioning, never an automatic reset.</summary>
public sealed class DataSecureStateIdentity
{
    public string Fingerprint { get; }
    public IndividualAddress LocalAddress { get; }
    public DataSecureStateIdentity(string projectScopeToken, string keyEpoch, IndividualAddress localAddress)
    {
        if (string.IsNullOrWhiteSpace(projectScopeToken) || projectScopeToken.Length > 128 || projectScopeToken.Contains('\0')
            || string.IsNullOrWhiteSpace(keyEpoch) || keyEpoch.Length > 128 || keyEpoch.Contains('\0')
            || localAddress.Raw == 0 || localAddress.Device is 0 or 255) throw new ArgumentException("An explicit project scope, key epoch and local device address are required.");
        LocalAddress = localAddress;
        var source = $"BusChord.DataSecureState2\0{projectScopeToken.Length}:{projectScopeToken}\0{keyEpoch.Length}:{keyEpoch}\0{localAddress.Raw}";
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}

/// <summary>Trusted provisioning or authenticated synchronization results, explicitly supplied by the caller.</summary>
public sealed class DataSecureCounterBootstrap
{
    public ulong TrustedNextSendSequence { get; }
    public IReadOnlyDictionary<IndividualAddress, ulong> TrustedReceiveFloors { get; }
    public DataSecureCounterBootstrap(ulong trustedNextSendSequence, IReadOnlyDictionary<IndividualAddress, ulong> trustedReceiveFloors)
    {
        ArgumentNullException.ThrowIfNull(trustedReceiveFloors);
        if (trustedNextSendSequence is 0 or > KnxDataSecureCodec.MaximumSequence + 1 || trustedReceiveFloors.Count > 65534)
            throw new ArgumentOutOfRangeException(nameof(trustedNextSendSequence));
        var copy = new Dictionary<IndividualAddress, ulong>();
        foreach (var (source, floor) in trustedReceiveFloors)
        {
            if (source.Raw == 0 || source.Device == 255 || floor > KnxDataSecureCodec.MaximumSequence || !copy.TryAdd(source, floor))
                throw new ArgumentException("Invalid trusted sender floor.", nameof(trustedReceiveFloors));
        }
        TrustedNextSendSequence = trustedNextSendSequence;
        TrustedReceiveFloors = new System.Collections.ObjectModel.ReadOnlyDictionary<IndividualAddress, ulong>(copy);
    }
}

public sealed record DataSecureCounterCheckpoint(Guid StoreId, ulong Revision, string Digest);

/// <summary>
/// Optional independent, linearizable, monotonic checkpoint storage. Implementations must not silently roll back,
/// erase a checkpoint, or report a successful compare/exchange before its own durable commit. An ordinary backup file is not such an anchor.
/// </summary>
public interface IDataSecureMonotonicAnchor
{
    Task<DataSecureCounterCheckpoint?> ReadAsync(string counterContext, CancellationToken ct);
    Task<bool> TryAdvanceAsync(string counterContext, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct);
}

public enum DataSecureCounterFailure { MissingState, CorruptState, ContextMismatch, SynchronizationRequired, UncertainAnchor, Exhausted, Occupied, UnsupportedPath }

public sealed class DataSecureCounterStateException(DataSecureCounterFailure failure, string message) : IOException(message)
{
    public DataSecureCounterFailure Failure { get; } = failure;
}

public sealed record DataSecureCounterRange(ulong First, ulong Last);
