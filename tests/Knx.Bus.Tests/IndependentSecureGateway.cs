using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;
internal sealed class IndependentSecureGateway(IPEndPoint? endpoint = null) : IUdpTransport
    {
    private static readonly IPEndPoint Gateway = new(IPAddress.Loopback,3671);
    private static readonly byte[] Bob = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
    private static readonly byte[] UserKey = Enumerable.Range(16,16).Select(i=>(byte)i).ToArray(), DeviceKey=Enumerable.Range(32,16).Select(i=>(byte)i).ToArray();
    private static readonly byte[] PeerSerial=Convert.FromHexString("aabbccddeeff");
    private static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
        private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
        private readonly IPEndPoint _endpoint = endpoint ?? Gateway;
        private byte[] _xor = [], _key = [];
        private ulong _sequence;
        public bool BadGateway, RejectUser, AuthenticatedUser, HoldReceiveUnwind, HoldSendUnwind, SuppressResponse, FailKeepalive;
        public int TotalSends, PlainSends, ApplicationSends, Disposals, Keepalives;
        public byte[]? UserKeyOverride, DeviceKeyOverride;
        public byte ExpectedUserId = 2;
        public IndividualAddress? AssignedAddressOverride;
        public Func<byte[], IReadOnlyList<byte[]>?>? ApplicationPacket;
        public TaskCompletionSource ReceiveStarted { get; } = Gate(); public TaskCompletionSource ReceiveCancelled { get; } = Gate(); public TaskCompletionSource ReleaseReceive { get; } = Gate();
        public TaskCompletionSource SendStarted { get; } = Gate(); public TaskCompletionSource SendCancelled { get; } = Gate(); public TaskCompletionSource ReleaseSend { get; } = Gate(); public TaskCompletionSource KeepaliveSeen { get; } = Gate();
        public async Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        {
            if (HoldSendUnwind)
            {
                SendStarted.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { SendCancelled.TrySetResult(); await ReleaseSend.Task; throw; }
            }
            foreach (var response in Process(data)) Push(response, _endpoint);
        }
        public IReadOnlyList<byte[]> Process(byte[] data)
        {
            Interlocked.Increment(ref TotalSends);
            var service = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (service == 0x951)
            {
                PlainSends++; Require(data.Length == 46, "Invalid request size."); var client = data.AsSpan(14, 32).ToArray();
                using var bob = OsX25519KeyAgreement.ImportPrivateKey(Bob); var raw = bob.DeriveRawSecret(client); _key = SHA256.HashData(raw)[..16]; CryptographicOperations.ZeroMemory(raw);
                _xor = client.Zip(bob.PublicKey, (a, b) => (byte)(a ^ b)).ToArray();
                var response = Frame(0x952, [0x12, 0x34, .. bob.PublicKey, .. new byte[16]]); Tag(DeviceKeyOverride ?? DeviceKey, [.. response.AsSpan(0, 8).ToArray(), .. _xor]).CopyTo(response, 40);
                if (BadGateway) response[^1] ^= 1; return SuppressResponse ? [] : [response];
            }
            Require(service == 0x950 && KnxIpSecureCodec.TryUnwrap(data, _key, out _), "Plaintext fallback or invalid secure wrapper.");
            KnxIpSecureCodec.TryUnwrap(data, _key, out var packet); var payload = packet!.Payload; service = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
            if (service == 0x953)
            {
                Require(payload.Length == 24 && payload[6] == 0 && payload[7] == ExpectedUserId && CryptographicOperations.FixedTimeEquals(payload.AsSpan(8), Tag(UserKeyOverride ?? UserKey, [.. payload.AsSpan(0, 8).ToArray(), .. _xor])), "Invalid independent user authentication.");
                AuthenticatedUser = !RejectUser; return [Wrap(Frame(0x954, [RejectUser ? (byte)1 : (byte)0, 0]))];
            }
            Require(AuthenticatedUser, "Application sent before authentication."); ApplicationSends++;
            var custom = ApplicationPacket?.Invoke(payload);
            if (custom is not null) return custom.Select(Wrap).ToArray();
            return service switch
            {
                0x205 => [Wrap(Frame(0x206, [7, 0, .. Hpai.Control(_endpoint), .. Crd.Tunnel(AssignedAddressOverride ?? new(1, 1, 240))]))],
                0x207 => [Wrap(Frame(0x208, [7, 0]))],
                0x209 => [Wrap(Frame(0x20A, [7, 0]))],
                0x420 => [Wrap(Frame(0x421, [4, 7, payload[8], 0]))],
                0x954 => Status(payload),
                _ => []
            };
        }
        private IReadOnlyList<byte[]> Status(byte[] payload) { if (payload[6] == 4) { Keepalives++; KeepaliveSeen.TrySetResult(); if (FailKeepalive) throw new IOException("Synthetic keepalive failure."); } return []; }
        private byte[] Wrap(byte[] payload) => KnxIpSecureCodec.Wrap(0x1234, _sequence++, PeerSerial, payload, _key);
        public void PushPayload(byte[] payload) => Push(Wrap(payload), _endpoint);
        public void PushPlain(byte[] frame) => Push(frame, _endpoint);
        public void Push(byte[] frame, IPEndPoint endpoint) => _incoming.Writer.TryWrite(new(frame, endpoint));
        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
        {
            if (HoldReceiveUnwind) ReceiveStarted.TrySetResult();
            try { return await _incoming.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { ReceiveCancelled.TrySetResult(); if (HoldReceiveUnwind) await ReleaseReceive.Task; throw; }
        }
        public void Dispose() { Disposals++; CryptographicOperations.ZeroMemory(_key); }
    private static byte[] Frame(ushort service, byte[] body) => KnxNetIpFrame.Encode((ServiceType)service, body);
    private static byte[] Tag(byte[] key, byte[] additional)
    {
        // Independent server-side handshake reference: B0=0, two-byte AAD length, zero padding, AES CBC then CTR block zero.
        var length = 18 + additional.Length; var data = new byte[(length + 15) / 16 * 16]; BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)additional.Length); additional.CopyTo(data, 18);
        using var aes = Aes.Create(); aes.Key = key; var mac = aes.EncryptCbc(data, new byte[16], PaddingMode.None)[^16..]; var counter = new byte[16]; counter[14] = 255;
        var stream = aes.EncryptEcb(counter, PaddingMode.None); for (var i = 0; i < 16; i++) mac[i] ^= stream[i]; return mac;
    }
}
