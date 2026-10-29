using Knx.Bus.Connections;

namespace Knx.Bus.Security;

/// <summary>Owned exact group policy. Incoming provenance is derived from the same admitted wire/decoded pair, never ambient last-frame state.</summary>
internal sealed class KnxGroupConnectionProcessor(KnxDataSecureCemiProcessor inner, KnxGroupSecurityProfile profile) : ICemiSecurityProcessor, ICemiSecurityProvenance
{
    public async Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        try { return await inner.PrepareOutgoingAsync(cemi, assignedAddress, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (DataSecureCounterStateException error) { throw KnxGroupSecurityProfile.StateFailure(error); }
        catch { throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityPolicyDenied); }
    }
    public Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
        => inner.ProcessIncomingAsync(cemi, assignedAddress, ct);
    public BusDataSecurity DescribeAdmittedIncoming(ReadOnlySpan<byte> wire, ReadOnlySpan<byte> admitted)
    {
        // The tunnel calls only after the inner processor completed MAC, policy, replay and durable floor commit.
        var original = Cemi.ParseLDataFrame(wire); var plain = Cemi.ParseLDataFrame(admitted);
        if (original.MessageCode != CemiMessageCode.LDataInd || (original.Control2 & 0x80) == 0
            || plain.MessageCode != original.MessageCode || plain.Source != original.Source || plain.DestinationRaw != original.DestinationRaw)
            return BusDataSecurity.Unverified;
        var selected = profile.Groups.FirstOrDefault(group => group.Group.Raw == original.DestinationRaw);
        if (selected is null) return BusDataSecurity.Unverified;
        // Do not infer verification from the secure header. Membership here is reached only after successful admission above.
        var authenticatedEnvelope = original.Apdu;
        return authenticatedEnvelope[2] == (byte)KnxDataProtection.AuthenticationAndConfidentiality
            ? BusDataSecurity.IncomingAuthenticatedAndConfidential : BusDataSecurity.IncomingAuthenticated;
    }
    public void ResetConnection() => inner.ResetConnection();
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
