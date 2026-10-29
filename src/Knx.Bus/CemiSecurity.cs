namespace Knx.Bus;

/// <summary>
/// An owned cEMI security policy. A tunnel prepares each outgoing cEMI once, then retries those same bytes.
/// Incoming processing runs only after KNXnet/IP acknowledgement and sequence deduplication.
/// Null incoming results are deliberate rejections. Implementations must not fall back to plaintext.
/// </summary>
public interface ICemiSecurityProcessor : IAsyncDisposable
{
    Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct);
    Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct);
    /// <summary>Invalidates prepared confirmations after joined tunnel shutdown; it does not reset secure counters.</summary>
    void ResetConnection();
}

public enum CemiSecurityRejection { None, InvalidFrame, AddressMismatch, PolicyDenied, AuthenticationFailed, Replay, StateUnavailable, UnboundConfirmation }

/// <summary>Application-frame provenance. Gateway authentication and locally prepared confirmations are separate facts.</summary>
public enum BusDataSecurity { Unverified, IncomingAuthenticated, IncomingAuthenticatedAndConfidential }

/// <summary>Additive trusted processor seam, called synchronously only for the exact incoming wire/accepted pair after admission.</summary>
public interface ICemiSecurityProvenance
{
    BusDataSecurity DescribeAdmittedIncoming(ReadOnlySpan<byte> wire, ReadOnlySpan<byte> admitted);
}
