using System.Net;
using System.Security.Cryptography;
using System.Text;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;

internal sealed class GroupConnectionFixture : IDisposable
{
    internal static readonly IndividualAddress Local = new(1, 2, 10), Sender = new(1, 2, 1), OtherSender = new(1, 2, 2);
    internal static readonly GroupAddress Group = new(4660), OtherGroup = new(17185), PlainGroup = new(0x222);
    internal static readonly byte[] Integrity = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "buschord-group-integration-" + Guid.NewGuid().ToString("N"));
    internal string State => Path.Combine(Root, "ordinary.state");
    internal string Keyring => Path.Combine(Root, "synthetic.knxkeys");
    internal DataSecureStateIdentity Identity => new("Synthetic physical group installation", "ordinary-1", Local);
    internal ExistingGroupCounterStateSource Source { get; }
    internal Password PasswordSource { get; } = new();
    internal List<Peer> Peers { get; } = [];
    internal byte[] GroupKey { get; }
    internal bool BadGateway, WrongAddress, AutoReply = true;
    internal Anchor? AnchorSource;
    internal GroupConnectionFixture()
    {
        Directory.CreateDirectory(Root); File.WriteAllText(Keyring, SecureGroupKeyringFixture.Rich);
        using var ring = ProtectedKnxKeyringImporter.Import(File.ReadAllBytes(Keyring), "pwd", "BusChord synthetic keyring");
        GroupKey = ring.GroupKeys[Group].CopyBytes(); Source = ExistingGroupCounterStateSource.FromTrustedUnanchoredCurrentState(State, Integrity);
    }
    internal SecureKeyringReference Reference(bool pinned = true) => new(Keyring, "BusChord synthetic keyring", new(1, 2, 0), 4, Local,
        expectedSourceFingerprint: pinned ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Keyring))) : null);
    internal KnxConnectionProfile Connection(KnxGroupSecurityProfile? policy = null, bool pinned = true)
        => policy is null ? KnxConnectionProfile.IpSecure(new(IPAddress.Loopback, 3671), Reference(pinned), PasswordSource)
            : KnxConnectionProfile.IpSecure(new(IPAddress.Loopback, 3671), Reference(pinned), PasswordSource).WithGroupSecurity(policy);
    internal KnxGroupSecurityProfile Policy(KnxDataProtection protection = KnxDataProtection.AuthenticationAndConfidentiality,
        IKnxGroupCounterStateSource? source = null, GroupAddress? group = null, IndividualAddress? sender = null,
        IReadOnlyCollection<GroupAddress>? plain = null, IReadOnlyCollection<string>? external = null)
        => new("Synthetic physical group installation", "ordinary-1", [new(group ?? Group, protection, new[] { sender ?? Sender })],
            source ?? Source, external ?? [], plain ?? [PlainGroup]);
    internal async Task Provision(ulong next = 10, bool ready = true, IDataSecureMonotonicAnchor? anchor = null)
    {
        await using var store = await DataSecureCounterStore.InitializeAsync(State, Identity, Integrity,
            new(next, new Dictionary<IndividualAddress, ulong> { [Sender] = 1, [OtherSender] = 1 }), anchor);
        if (!ready) await store.MarkSynchronizationRequiredAsync();
    }
    internal Task<DataSecureCounterStore> OpenState() => DataSecureCounterStore.OpenAsync(State, Identity, Integrity, AnchorSource);
    internal KnxConnectionFactory Factory() => new(new(Path.Combine(Root, "leases")), serial: new Serial(), createTransport: _ =>
    {
        var peer = new Peer(this); Peers.Add(peer); return peer.Gateway;
    }, localEndpoint: (_, _) => new(IPAddress.Loopback, 43123));
    internal byte[] Value(ulong sequence, IndividualAddress? sender = null, GroupAddress? group = null,
        KnxDataProtection protection = KnxDataProtection.AuthenticationAndConfidentiality)
        => KnxDataSecureCodec.ProtectCemi(new(CemiMessageCode.LDataInd, 0xBC, 0xE0, sender ?? Sender, (group ?? Group).Raw, [0, 0x40, 42]), sequence, GroupKey, protection);
    public void Dispose() { Source.Dispose(); CryptographicOperations.ZeroMemory(GroupKey); Directory.Delete(Root, true); }
    internal sealed class Password : ISecurePasswordSource
    {
        internal int Reads; internal char[]? Last;
        public ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(Last = "pwd".ToCharArray()); }
    }
    private sealed class Serial : IKnxClientSerialProvider { public byte[] ReadOrCreate() => [1, 2, 3, 4, 5, 6]; }
    internal sealed class Peer
    {
        private readonly GroupConnectionFixture _fixture;
        private byte _incoming;
        internal IndependentSecureGateway Gateway { get; }
        internal int Reads, Acks;
        internal byte[]? LastRead, LastValue;
        internal List<ulong> SentSequences { get; } = [];
        internal Peer(GroupConnectionFixture fixture)
        {
            _fixture = fixture; Gateway = new()
            {
                ExpectedUserId = 4, AssignedAddressOverride = fixture.WrongAddress ? new(1, 2, 11) : Local, BadGateway = fixture.BadGateway,
                UserKeyOverride = Rfc2898DeriveBytes.Pbkdf2("user4", Encoding.ASCII.GetBytes("user-password.1.secure.ip.knx.org"), 65536, HashAlgorithmName.SHA256, 16),
                DeviceKeyOverride = Rfc2898DeriveBytes.Pbkdf2("dev", Encoding.ASCII.GetBytes("device-authentication-code.1.secure.ip.knx.org"), 65536, HashAlgorithmName.SHA256, 16),
                ApplicationPacket = Process,
            };
        }
        internal void Push(byte[] cemi) => Gateway.PushPayload(Incoming(cemi));
        private byte[] Incoming(byte[] cemi) => KnxNetIpFrame.Encode(ServiceType.TunnellingRequest, Tunnelling.Request(7, _incoming++, cemi));
        private IReadOnlyList<byte[]>? Process(byte[] packet)
        {
            var frame = KnxNetIpFrame.Decode(packet);
            if (frame.Service == ServiceType.TunnellingAck) { Acks++; return []; }
            if (frame.Service != ServiceType.TunnellingRequest) return null;
            var (_, sequence, raw) = Tunnelling.ReadRequest(frame.Body); var cemi = Cemi.ParseLDataFrame(raw);
            if ((cemi.Control2 & 0x80) == 0) throw new Exception("Unexpected individual traffic.");
            var result = new List<byte[]> { KnxNetIpFrame.Encode(ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence)) };
            if (cemi.DestinationRaw == PlainGroup.Raw) return result;
            if (!KnxDataSecureCodec.TryUnprotectCemi(raw, _fixture.GroupKey, out var data, out _)) throw new Exception("Plain fallback or invalid group key.");
            try
            {
                if (data!.Apdu.Length != 2 || data.Apdu[1] != 0) throw new Exception("Unexpected group service.");
                Reads++; LastRead = raw.ToArray(); SentSequences.Add(data.Sequence);
                if (_fixture.AutoReply)
                {
                    if (_fixture.AnchorSource is { HoldOnValue: true } anchor) anchor.Hold = true;
                    LastValue = _fixture.Value((ulong)(20 + Reads)); result.Add(Incoming(LastValue));
                }
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(data!.Apdu); }
        }
    }
    internal sealed class Anchor : IDataSecureMonotonicAnchor
    {
        private readonly Dictionary<string, DataSecureCounterCheckpoint> _values = [];
        internal bool Missing, Hold, HoldOnValue;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Missing ? null : _values.GetValueOrDefault(context)); }
        public async Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        {
            if (Hold) { Entered.TrySetResult(); try { await Release.Task.WaitAsync(ct); } catch (OperationCanceledException) { Cancelled.TrySetResult(); await Release.Task; throw; } }
            ct.ThrowIfCancellationRequested(); if (_values.GetValueOrDefault(context) != expected) return false; _values[context] = proposed; return true;
        }
    }
}
