using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Commissioning;
using Knx.Bus.Connections;

/// <summary>In-process datagrams through the production factory/tunnel/discovery/management adapters. No real socket, provider or hardware.</summary>
internal sealed class IndividualAddressGatewayFixture : IDisposable
{
    internal static readonly IndividualAddress Client = new(1, 1, 240), Original = new(1, 1, 7), Desired = new(1, 1, 8);
    internal const string DeviceSerial = "008301020304";
    internal string Root { get; } = Directory.CreateTempSubdirectory("BusChord.Address.Gateway.").FullName;
    internal IndividualAddress Current = Original;
    internal IndividualAddress ExpectedTarget = Desired;
    internal ushort MaskVersion = 0x0701;
    internal bool DuplicateProgramming, DuplicateSerial, OccupiedDesired, OtherTrafficAtDesired, DropWriteAck, IgnoreWrite, WrongIdentity, HoldVerification;
    internal int Writes, Transports, Disposals, DescriptorReads;
    internal TaskCompletionSource VerificationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseVerification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly List<Telegram> AssignmentWrites = [];
    internal KnxConnectionProfile Profile => KnxConnectionProfile.Plain(new(IPAddress.Loopback, 3671), Client);
    internal KnxConnectionFactory Factory() => new(new(Path.Combine(Root, "leases")), createTransport: _ => { Transports++; return new Gateway(this); }, localEndpoint: (_, _) => new(IPAddress.Loopback, 43001));
    internal IndividualAddressAssignment Create(bool isolated = false, bool coupler = false) => new(new(Profile, ExpectedTarget,
        isolated ? AddressSelectionKind.IsolatedProgrammingMode : AddressSelectionKind.ExactSerial, "reserved synthetic project device",
        isolated ? null : DeviceSerial, isolated ? "isolated synthetic device" : null, confirmPlainDevice: true, observationWindow: TimeSpan.FromMilliseconds(20),
        targetRole: coupler ? AddressDeviceRole.Coupler : AddressDeviceRole.EndDevice, confirmCouplerHardware: coupler, couplerHardwareReference: coupler ? "synthetic manufacturer hardware coupler" : null), Root, new AddressAssignmentTransport(Factory()).OpenAsync);
    public void Dispose() { ReleaseVerification.TrySetResult(); Directory.Delete(Root, true); }
    private sealed class Gateway(IndividualAddressGatewayFixture fixture) : IUdpTransport
    {
        private readonly IPEndPoint _peer = fixture.Profile.Gateway;
        private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
        private byte _networkSequence, _managementSequence;
        public async Task SendAsync(IPEndPoint destination, byte[] bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var frame = KnxNetIpFrame.Decode(bytes);
            switch (frame.Service)
            {
                case ServiceType.ConnectRequest: Push(ServiceType.ConnectResponse, [7, 0, .. Hpai.Control(_peer), .. Crd.Tunnel(Client)]); return;
                case ServiceType.ConnectionStateRequest: Push(ServiceType.ConnectionStateResponse, Connection.Response(7)); return;
                case ServiceType.DisconnectRequest: Push(ServiceType.DisconnectResponse, Connection.Response(7)); return;
                case ServiceType.TunnellingRequest: break;
                default: return;
            }
            var raw = Cemi.ParseLDataFrame(frame.Body.AsSpan(4)); var apdu = raw.Apdu;
            var telegram = apdu.Length >= 2 ? Cemi.ParseLData(frame.Body.AsSpan(4)) : null;
            var addressWrite = telegram?.Apci is Apci.IndividualAddressWrite or Apci.IndividualAddressSerialWrite;
            if (!addressWrite || !fixture.DropWriteAck) Push(ServiceType.TunnellingAck, Tunnelling.Ack(7, frame.Body[2]));
            if ((apdu[0] & 0xC0) == 0x40)
            {
                var target = new IndividualAddress(raw.DestinationRaw); if (target != fixture.Current) return;
                if (apdu.Length < 2) return;
                var sequence = (byte)((apdu[0] >> 2) & 15); EmitManagement([(byte)(0xC2 | (sequence << 2))]);
                var service = ManagementApdu.Service(apdu);
                if (service == 0x300) { fixture.DescriptorReads++; Respond([3, 0x40, (byte)(fixture.MaskVersion >> 8), (byte)fixture.MaskVersion]); }
                else if (service == ManagementApdu.PropertyRead)
                {
                    if (apdu[3] == 11 && fixture.Current == fixture.ExpectedTarget)
                    { fixture.VerificationEntered.TrySetResult(); if (fixture.HoldVerification) await fixture.ReleaseVerification.Task; ct.ThrowIfCancellationRequested(); }
                    var data = apdu[3] switch { 11 => Convert.FromHexString(fixture.WrongIdentity ? "008309090909" : DeviceSerial), 12 => new byte[] { 0, 0x83 }, _ => Array.Empty<byte>() };
                    Respond([3, 0xD6, apdu[2], apdu[3], apdu[4], apdu[5], .. data]);
                }
                return;
            }
            if (telegram is null) return;
            switch (telegram.Apci)
            {
                case Apci.IndividualAddressRead:
                    Emit(new() { Source = fixture.Current, Destination = Client, Apci = Apci.IndividualAddressResponse, Data = [] });
                    if (fixture.DuplicateProgramming) Emit(new() { Source = fixture.Current, Destination = Client, Apci = Apci.IndividualAddressResponse, Data = [] });
                    break;
                case Apci.IndividualAddressSerialRead:
                    if (!telegram.Data.SequenceEqual(Convert.FromHexString(DeviceSerial))) return;
                    Emit(new() { Source = fixture.Current, Destination = Client, Apci = Apci.IndividualAddressSerialResponse, Data = [.. telegram.Data, 0, 0] });
                    if (fixture.DuplicateSerial) Emit(new() { Source = fixture.Current, Destination = Client, Apci = Apci.IndividualAddressSerialResponse, Data = [.. telegram.Data, 0, 0] });
                    break;
                case Apci.DeviceDescriptorRead:
                    if (raw.DestinationRaw == fixture.Current.Raw || fixture.OccupiedDesired && raw.DestinationRaw == fixture.ExpectedTarget.Raw)
                        Emit(new() { Source = new(raw.DestinationRaw), Destination = Client, Apci = Apci.DeviceDescriptorResponse, Data = [7, 1] });
                    else if (fixture.OtherTrafficAtDesired && raw.DestinationRaw == fixture.ExpectedTarget.Raw)
                        Emit(new() { Source = fixture.ExpectedTarget, Destination = new(1), IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = [1], DataBits = 1 });
                    break;
                case Apci.IndividualAddressWrite:
                case Apci.IndividualAddressSerialWrite:
                    fixture.Writes++; fixture.AssignmentWrites.Add(telegram);
                    var offset = telegram.Apci == Apci.IndividualAddressWrite ? 0 : 6;
                    if (offset == 6 && !telegram.Data.AsSpan(0, 6).SequenceEqual(Convert.FromHexString(DeviceSerial))) return;
                    if (!fixture.IgnoreWrite) fixture.Current = new((ushort)((telegram.Data[offset] << 8) | telegram.Data[offset + 1]));
                    break;
            }
        }
        private void Respond(byte[] apdu) { apdu[0] = (byte)((apdu[0] & 3) | 0x40 | ((_managementSequence++ & 15) << 2)); EmitManagement(apdu); }
        private void EmitManagement(byte[] apdu) { var frame = new ManagementTransportFrame(fixture.Current, Client, apdu).ToRequest(); frame[0] = 0x29; Push(ServiceType.TunnellingRequest, Tunnelling.Request(7, _networkSequence++, frame)); }
        private void Emit(Telegram telegram) { var frame = Cemi.LDataRequest(telegram); frame[0] = 0x29; Push(ServiceType.TunnellingRequest, Tunnelling.Request(7, _networkSequence++, frame)); }
        private void Push(ServiceType service, byte[] body) => _incoming.Writer.TryWrite(new(KnxNetIpFrame.Encode(service, body), _peer));
        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);
        public void Dispose() { fixture.Disposals++; _incoming.Writer.TryComplete(); }
    }
}
