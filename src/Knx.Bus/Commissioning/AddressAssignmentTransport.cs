using Knx.Bus.Connections;

namespace Knx.Bus.Commissioning;

/// <summary>Plain-only factory adapter. IP Secure and Tool/P2P policy do not authorize broadcast commissioning.</summary>
public sealed class AddressAssignmentTransport(KnxConnectionFactory? factory = null)
{
    private readonly KnxConnectionFactory _factory = factory ?? new();
    public async Task<IAddressAssignmentOperation> OpenAsync(KnxConnectionProfile profile, CancellationToken ct = default)
    {
        if (profile.Kind != KnxTransportKind.PlainUdp || profile.GroupSecurity is not null)
            throw new AddressAssignmentException(AddressAssignmentFailure.UnsupportedSecureProvisioning);
        return new Operation(await _factory.AcquireOperationAsync(profile, ct).ConfigureAwait(false));
    }
    private sealed class Operation(KnxGatewayOperation operation) : IAddressAssignmentOperation
    {
        public KnxConnectionProfile Profile => operation.Profile;
        private async Task<KnxConnectedTunnel> FreshAsync(CancellationToken ct)
        {
            var owned = await operation.OpenTunnelAsync(ct).ConfigureAwait(false);
            if (owned.Tunnel.AssignedAddress != Profile.RequestedAddress)
            { await owned.DisposeAsync().ConfigureAwait(false); throw new KnxConnectionException(KnxConnectionFailure.AssignedAddressMismatch); }
            return owned;
        }
        public async Task<IndividualAddressObservation> DiscoverAsync(AddressSelectionKind kind, string? serial, TimeSpan window, CancellationToken ct)
        {
            await using var owned = await FreshAsync(ct).ConfigureAwait(false);
            var session = new TunnelDiagnosticSession(owned.Tunnel);
            return kind == AddressSelectionKind.ExactSerial
                ? await IndividualAddressDiscovery.ReadSerialAsync(session, Profile.RequestedAddress, Convert.FromHexString(serial!), window, ct).ConfigureAwait(false)
                : await IndividualAddressDiscovery.ReadProgrammingModeAsync(session, Profile.RequestedAddress, window, ct).ConfigureAwait(false);
        }
        public async Task<AddressOccupancyObservation> ProbeAsync(IndividualAddress address, TimeSpan window, CancellationToken ct)
        {
            if (address == Profile.RequestedAddress) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
            await using var owned = await FreshAsync(ct).ConfigureAwait(false);
            var gate = new object(); var count = 0; var malformed = false; var closed = false;
            void Received(BusTelegram item)
            {
                var telegram = item.Telegram;
                // Any observed sender at the proposed IA is a positive conflict, even when its descriptor is unsupported.
                if (telegram.Source != address) return;
                lock (gate)
                {
                    if (closed) return;
                    if (count < 257) count++;
                    if (telegram.Apci == Apci.DeviceDescriptorResponse && (telegram.IsGroupAddress || telegram.Destination != Profile.RequestedAddress
                        || telegram.Data is not { Length: 2 } || telegram.DataBits is not null)) malformed = true;
                }
            }
            owned.Tunnel.TelegramReceived += Received;
            try { await owned.Tunnel.SendTelegramAsync(Management.DeviceDescriptorReadTelegram(address), ct).ConfigureAwait(false); await Task.Delay(window, ct).ConfigureAwait(false); }
            finally { lock (gate) closed = true; owned.Tunnel.TelegramReceived -= Received; }
            lock (gate) return new(malformed ? AddressOccupancyState.MalformedResponse : count > 0 ? AddressOccupancyState.ResponseObserved : AddressOccupancyState.NoResponseReservationRequired, count);
        }
        public async Task<AddressPhysicalIdentity> ReadIdentityAsync(IndividualAddress address, CancellationToken ct)
        {
            await using var owned = await FreshAsync(ct).ConfigureAwait(false);
            await using var client = new KnxManagementClient(owned.Tunnel, address, readOnly: true, TimeSpan.FromSeconds(2));
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var descriptor = await client.ReadDescriptorAsync(ct).ConfigureAwait(false);
            var manufacturer = await client.ReadPropertyAsync(0, 12, ct: ct).ConfigureAwait(false);
            var serial = await client.ReadPropertyAsync(0, 11, ct: ct).ConfigureAwait(false);
            if (descriptor.Length != 2 || manufacturer.Length != 2 || serial.Length != 6)
                throw new AddressAssignmentException(AddressAssignmentFailure.PhysicalBindingRequired);
            var identity = new AddressPhysicalIdentity((ushort)((descriptor[0] << 8) | descriptor[1]), Convert.ToHexString(manufacturer), Convert.ToHexString(serial));
            if (!identity.IsValid) throw new AddressAssignmentException(AddressAssignmentFailure.PhysicalBindingRequired);
            return identity;
        }
        public async Task WriteOnceAsync(AddressSelectionKind kind, string? serial, IndividualAddress address, CancellationToken ct)
        {
            await using var owned = await FreshAsync(ct).ConfigureAwait(false);
            await owned.Tunnel.SendIndividualAddressOnceAsync(address, kind == AddressSelectionKind.ExactSerial ? Convert.FromHexString(serial!) : default, ct, confirmCouplerAddress: address.Device == 0).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => operation.DisposeAsync();
    }
}
