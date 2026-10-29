using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;

internal static class ToolAccessChecks
{
    private static readonly IndividualAddress Local = new(0x120A), Target = new(0x1200);
    private static readonly byte[] Integrity = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();
    // Published ToolKey plaintext from the existing, independently signed synthetic keyring fixture.
    private static readonly byte[] ToolKey = Convert.FromHexString("AEAC47C4653ED0B25249B4AB3F474479");
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Failure<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Code(KnxConnectionFailure expected, Func<Task> action)
    {
        var error = await Failure<KnxConnectionException>(action);
        Require(error.Failure == expected && error.Message == "KNX connection failed: " + expected && error.InnerException is null, "Failure lost its typed/redacted contract: " + error.Failure);
    }
    private static byte[][] SecretBuffers(object value) => value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Where(field => field.FieldType == typeof(byte[])).Select(field => (byte[])field.GetValue(value)!).ToArray();
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("existing public constructors retain their exact signatures", () => { Require(typeof(KnxBus).GetConstructor(Type.EmptyTypes) is not null && typeof(KnxConnectionFactory).GetConstructors().Any(c => c.GetParameters().Length == 5), "Old constructor ABI changed."); return Task.CompletedTask; }),
            ("missing existing state is refused before transport and password", MissingState),
            ("explicit source pin, secure transport and capacity are mandatory", ProfileBounds),
            ("independent anchor is required or explicitly absent, never silently bypassed", AnchorTrust),
            ("corrupt local send pages are refused before transport", CorruptState),
            ("partial valid-page restore cannot lower the local send highwater", PartialRestore),
            ("restore gate is refused before transport", RestoreBlocked),
            ("wrong stable counter domain is refused before transport", WrongStateBinding),
            ("exhausted send highwater is refused before transport", Exhausted),
            ("exact signed Target ToolKey and password ownership", CredentialSelection),
            ("wrong password and absent device key never open transport", CredentialRefusals),
            ("ordinary P2P key reuse is explicitly refused", KeyDomainSeparation),
            ("signed ordinary group-key reuse is refused", GroupKeyDomainSeparation),
            ("Tool profile forbids raw tunnels and another management target", ManagementOnly),
            ("changed state is rechecked before a fresh transport", StateDrift),
            ("authenticated fake gateway Sync precedes management and recovery", Recovery),
            ("wrong assigned IA sends no Tool Sync", AssignedMismatch),
            ("gateway authentication failure never falls back", AuthenticationFailure),
            ("invalid Sync authentication cannot publish management", InvalidSync),
            ("cancelled Sync joins before keys and endpoint lease release", CancelledSync),
            ("actual UDP authenticates, synchronizes, reads and reopens", RealUdp),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine("PASS ToolAccess " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL ToolAccess " + name + ": " + error); }
        }
        return failures;
    }
    private static async Task MissingState()
    {
        using var fixture = new Fixture(); var factory = fixture.Factory();
        await Code(KnxConnectionFailure.ToolAccessStateUnavailable, async () => await factory.AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0 && fixture.Password.Reads == 0 && !File.Exists(fixture.StatePath), "Missing state reached I/O, requested a password or bootstrapped a file.");
    }
    private static async Task ProfileBounds()
    {
        using var fixture = new Fixture(); var factory = fixture.Factory();
        await Code(KnxConnectionFailure.InvalidProfile, async () => await factory.AcquireToolAccessOperationAsync(KnxConnectionProfile.Plain(new(IPAddress.Loopback, 3671), Local), fixture.Profile()));
        var unpinned = KnxConnectionProfile.IpSecure(new(IPAddress.Loopback, 3671), fixture.Reference(includePin: false), fixture.Password);
        await Code(KnxConnectionFailure.InvalidProfile, async () => await factory.AcquireToolAccessOperationAsync(unpinned, fixture.Profile()));
        foreach (var capacity in new[] { 24, 256 })
            await Code(KnxConnectionFailure.InvalidProfile, () => Task.Run(() => new KnxToolAccessProfile(Target, "Synthetic physical installation", "tool-v1", capacity, fixture.StateSource, [])));
        await Code(KnxConnectionFailure.InvalidProfile, () => Task.Run(() => ExistingToolCounterStateSource.FromTrustedUnanchoredState(fixture.Directory + "\0private-source", Integrity)));
        await Code(KnxConnectionFailure.InvalidProfile, () => Task.Run(() => new ExistingToolCounterStateSource(fixture.StatePath, Integrity, null!)));
        Require(fixture.Opens == 0 && fixture.Password.Reads == 0, "Invalid profile reached network/password input.");
    }
    private static async Task CorruptState()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        var bytes = await File.ReadAllBytesAsync(fixture.StatePath);
        // State2 header=4096, each counter has A/B 4096-byte pages; index1 is the send highwater.
        bytes[4096 + 8192 + 100] ^= 1; bytes[4096 + 8192 + 4096 + 100] ^= 1;
        await File.WriteAllBytesAsync(fixture.StatePath, bytes);
        await Code(KnxConnectionFailure.ToolAccessStateUnavailable, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0 && fixture.Password.Reads == 0, "Corrupt send pages escaped the offline preflight.");
    }
    private static async Task AnchorTrust()
    {
        foreach (var missing in new[] { false, true })
        {
            using var fixture = new Fixture(); var anchor = new Anchor(); await fixture.Provision(anchor: anchor); anchor.Missing = missing;
            using var source = new ExistingToolCounterStateSource(fixture.StatePath, Integrity, anchor);
            var factory = fixture.Factory();
            if (missing)
                await Code(KnxConnectionFailure.ToolAccessStateNotReady, async () => await factory.AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile(source: source)));
            else
            {
                await using var operation = await factory.AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile(source: source));
                Require(fixture.Password.Reads == 1, "Anchored credentials were not acquired once.");
            }
            Require(fixture.Opens == 0 && (!missing || fixture.Password.Reads == 0), "Uncertain anchor silently fell back or preflight emitted network frames.");
        }
    }
    private static async Task RestoreBlocked()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        await using (var store = await fixture.OpenState()) await store.MarkSynchronizationRequiredAsync();
        await Code(KnxConnectionFailure.ToolAccessStateNotReady, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0 && fixture.Password.Reads == 0, "Restore-blocked state reached transport.");
    }
    private static async Task PartialRestore()
    {
        using var fixture = new Fixture(); await fixture.Provision(includeOwnFloor: true);
        var previous = await File.ReadAllBytesAsync(fixture.StatePath);
        await using (var store = await fixture.OpenState()) Require(await store.TryAcceptReceiveAsync(Local, 50), "Could not establish the trusted own receive floor.");
        var restored = await File.ReadAllBytesAsync(fixture.StatePath);
        previous.AsSpan(4096 + 8192, 8192).CopyTo(restored.AsSpan(4096 + 8192));
        await File.WriteAllBytesAsync(fixture.StatePath, restored);
        await Code(KnxConnectionFailure.ToolAccessStateNotReady, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0, "A semantically inconsistent partial restore reached networking.");
    }
    private static async Task WrongStateBinding()
    {
        using var fixture = new Fixture(); await fixture.Provision(epoch: "other-epoch");
        await Code(KnxConnectionFailure.ToolAccessBindingMismatch, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0, "A foreign counter epoch reached transport.");
    }
    private static async Task Exhausted()
    {
        using var fixture = new Fixture(); await fixture.Provision(next: KnxDataSecureCodec.MaximumSequence + 1);
        await Code(KnxConnectionFailure.ToolAccessCounterExhausted, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0, "An exhausted send counter reached transport.");
    }
    private static async Task CredentialSelection()
    {
        using var fixture = new Fixture();
        var provider = new ProtectedToolAccessCredentialProvider();
        using var scope = await provider.AcquireAsync(fixture.Reference(), fixture.Password, Target, [], default);
        Require(scope.Target == Target && scope.Gateway.ProtectedGroups.Count == 2 && SecretBuffers(scope).Single().SequenceEqual(ToolKey), "Selected another key/device or lost signed protected groups.");
        Require(fixture.Password.Owned!.All(value => value == '\0'), "Password copy survived import.");
        var buffers = SecretBuffers(scope).Concat(SecretBuffers(scope.Gateway)).ToArray(); scope.Dispose();
        Require(buffers.All(bytes => bytes.All(value => value == 0)), "Credential capability was not zeroed.");
    }
    private static async Task CredentialRefusals()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        fixture.Password.Value = "synthetic-wrong-password";
        await Code(KnxConnectionFailure.KeyringRejected, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        fixture.Password.Value = "pwd";
        var provider = new ProtectedToolAccessCredentialProvider();
        await Code(KnxConnectionFailure.ToolAccessKeyUnavailable, async () => await provider.AcquireAsync(fixture.Reference(), fixture.Password, new(0x1201), [], default));
        await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await provider.AcquireAsync(fixture.Reference(fingerprint: new string('0', 64)), fixture.Password, Target, [], default));
        Require(fixture.Opens == 0, "Rejected credentials reached transport.");
    }
    private static async Task KeyDomainSeparation()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        var profile = fixture.Profile([Convert.ToHexString(SHA256.HashData(ToolKey))]);
        await Code(KnxConnectionFailure.ToolAccessBindingMismatch, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), profile));
        Require(fixture.Opens == 0, "A Tool/P2P key-domain collision reached transport.");
    }
    private static async Task GroupKeyDomainSeparation()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        var root = XElement.Parse(File.ReadAllText(fixture.KeyringFile)); var ns = root.Name.Namespace;
        root.Element(ns + "GroupAddresses")!.Elements().First().SetAttributeValue("Key", root.Element(ns + "Devices")!.Element(ns + "Device")!.Attribute("ToolKey")!.Value);
        var signed = (string)typeof(ProtectedKnxKeyringChecks).GetMethod("SignForNegativeCase", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root])!;
        File.WriteAllText(fixture.KeyringFile, signed);
        await Code(KnxConnectionFailure.ToolAccessBindingMismatch, async () => await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile()));
        Require(fixture.Opens == 0, "A Tool/ordinary group key collision reached networking.");
    }
    private static async Task ManagementOnly()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        await using var operation = await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile());
        await Code(KnxConnectionFailure.InvalidProfile, () => operation.OpenTunnelAsync());
        await Code(KnxConnectionFailure.ToolAccessBindingMismatch, () => operation.OpenManagementAsync(new(0x1201), true));
        Require(fixture.Opens == 0, "Management-only target guard was applied after networking.");
    }
    private static async Task StateDrift()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        await using var operation = await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile());
        await using (var store = await fixture.OpenState()) await store.MarkSynchronizationRequiredAsync();
        await Code(KnxConnectionFailure.ToolAccessStateNotReady, () => operation.OpenManagementAsync(Target, true));
        Require(fixture.Opens == 0, "A stale operation skipped its fresh-open state guard.");
    }
    private static async Task Recovery()
    {
        using var fixture = new Fixture(); await fixture.Provision(); var provider = new RecordingProvider();
        var operation = await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile(), provider);
        fixture.StateSource.Dispose(); // The job owns independent store/key capabilities now.
        var capability = operation.GetType().GetField("_toolAccess", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operation)!;
        var state = capability.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(field => field.FieldType == typeof(DataSecureCounterStore)).GetValue(capability)!;
        var buffers = SecretBuffers(provider.Scope!).Concat(SecretBuffers(provider.Scope!.Gateway)).Concat(SecretBuffers(state)).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var management = await operation.OpenManagementAsync(Target, true);
            Require((await management.Client.ReadDescriptorAsync()).SequenceEqual(new byte[] { 0, 0x12 }), "Read did not traverse Tool Access.");
            var owned = (KnxConnectedTunnel)management.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(field => field.FieldType == typeof(KnxConnectedTunnel)).GetValue(management)!;
            foreach (var group in new ushort[] { 4660, 17185 })
                await Failure<InvalidOperationException>(() => owned.Tunnel.GroupValueReadAsync(new(group)));
        }
        Require(fixture.Device.SyncSequences.Count == 2 && fixture.Device.SyncSequences[0] == 10 && fixture.Device.SyncSequences.Distinct().Count() == 2
            && fixture.Device.DataSequences.Zip(fixture.Device.DataSequences.Skip(1), (a, b) => a < b).All(value => value)
            && fixture.Password.Reads == 1 && provider.Calls == 1 && buffers.Any(bytes => bytes.Any(value => value != 0)), "Fresh recovery reused a nonce, reimported credentials or used export sequence108.");
        await operation.DisposeAsync(); await operation.DisposeAsync();
        Require(buffers.All(bytes => bytes.All(value => value == 0)) && fixture.Peers.All(peer => peer.Gateway.Disposals == 1), "Job cleanup leaked keys or fresh transports.");
    }
    private static async Task AssignedMismatch()
    {
        using var fixture = new Fixture(); await fixture.Provision(); fixture.Assigned = new(0x120B);
        await using var operation = await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile());
        await Code(KnxConnectionFailure.AssignedAddressMismatch, () => operation.OpenManagementAsync(Target, true));
        Require(fixture.Device.SyncSequences.Count == 0, "Wrong AssignedIA sent a Tool Sync.");
    }
    private static async Task AuthenticationFailure()
    {
        using var fixture = new Fixture(); await fixture.Provision(); fixture.BadGateway = true;
        await using var operation = await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile());
        await Code(KnxConnectionFailure.AuthenticationFailed, () => operation.OpenManagementAsync(Target, true));
        Require(fixture.Peers.Single().Gateway.PlainSends == 1 && fixture.Device.SyncSequences.Count == 0, "Authentication failure retried plaintext or emitted Tool data.");
    }
    private static async Task InvalidSync()
    {
        using var fixture = new Fixture(); await fixture.Provision(); fixture.Device.TamperSync = true;
        await using var operation = await fixture.Factory().AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile());
        await Code(KnxConnectionFailure.ToolAccessSynchronizationFailed, () => operation.OpenManagementAsync(Target, true));
        Require(fixture.Device.ManagementConnects == 0 && fixture.Device.DataSequences.Count == 0, "Unauthenticated Sync published a management client.");
    }
    private static async Task CancelledSync()
    {
        using var fixture = new Fixture(); await fixture.Provision(); fixture.Device.HoldSync = true; var provider = new RecordingProvider(); var factory = fixture.Factory();
        var operation = await factory.AcquireToolAccessOperationAsync(fixture.Connection(), fixture.Profile(), provider);
        var opening = operation.OpenManagementAsync(Target, true); await fixture.Device.SyncSeen.Task;
        fixture.Peers.Single().Gateway.HoldReceiveUnwind = true;
        var first = operation.DisposeAsync().AsTask(); var second = operation.DisposeAsync().AsTask(); await fixture.Peers.Single().Gateway.ReceiveCancelled.Task;
        await Code(KnxConnectionFailure.Occupied, async () => await factory.AcquireOperationAsync(fixture.Connection()));
        var buffers = SecretBuffers(provider.Scope!).Concat(SecretBuffers(provider.Scope!.Gateway)).ToArray();
        Require(!first.IsCompleted && buffers.Any(bytes => bytes.Any(value => value != 0)), "Endpoint/key owners escaped before receive joined.");
        fixture.Peers.Single().Gateway.ReleaseReceive.TrySetResult();
        await Failure<OperationCanceledException>(() => opening); await Task.WhenAll(first, second);
        Require(buffers.All(bytes => bytes.All(value => value == 0)), "Cancelled Sync kept credentials after drain.");
    }
    private static async Task RealUdp()
    {
        using var fixture = new Fixture(); await fixture.Provision(); using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!; var peer = new ToolPeer(fixture.Device, endpoint, Local); using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () => { try { while (true) { var packet = await server.ReceiveAsync(stop.Token); foreach (var response in peer.Gateway.Process(packet.Buffer)) await server.SendAsync(response, packet.RemoteEndPoint, stop.Token); } } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } });
        var factory = new KnxConnectionFactory(new(Path.Combine(fixture.Directory, "udp-leases")), serial: new Serial(),
            createTransport: _ => new UdpTransport(IPAddress.Loopback, 0), localEndpoint: (transport, _) => ((UdpTransport)transport).LocalEndPoint);
        try
        {
            await using var operation = await factory.AcquireToolAccessOperationAsync(fixture.Connection(endpoint), fixture.Profile());
            for (var index = 0; index < 2; index++)
            {
                await using var management = await operation.OpenManagementAsync(Target, true);
                Require((await management.Client.ReadDescriptorAsync()).SequenceEqual(new byte[] { 0, 0x12 }), "UDP secure Tool read failed.");
            }
            Require(fixture.Device.SyncSequences.Count == 2 && fixture.Device.ManagementConnects == 2, "UDP recovery did not authenticate and Sync before each management connection.");
        }
        finally { stop.Cancel(); await serving; peer.Gateway.Dispose(); }
    }
    private sealed class Password : ISecurePasswordSource
    { public string Value = "pwd"; public int Reads; public char[]? Owned; public ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(Owned = Value.ToCharArray()); } }
    private sealed class Serial : IKnxClientSerialProvider { public byte[] ReadOrCreate() => [1, 2, 3, 4, 5, 6]; }
    private sealed class RecordingProvider : IKnxToolAccessCredentialProvider
    {
        public int Calls; public KnxToolAccessCredentialScope? Scope;
        public async Task<KnxToolAccessCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? password, IndividualAddress target, IReadOnlyList<string> inventory, CancellationToken ct)
        { Calls++; return Scope = await new ProtectedToolAccessCredentialProvider().AcquireAsync(reference, password, target, inventory, ct); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "buschord-tool-access-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(Directory, "state.bin"); public string KeyringFile => Path.Combine(Directory, "synthetic.knxkeys");
        public Password Password { get; } = new(); public Device Device { get; } = new(); public List<ToolPeer> Peers { get; } = []; public int Opens;
        public bool BadGateway; public IndividualAddress Assigned = Local;
        public ExistingToolCounterStateSource StateSource { get; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(KeyringFile, (string)typeof(ProtectedKnxKeyringChecks).GetField("Rich", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
            StateSource = ExistingToolCounterStateSource.FromTrustedUnanchoredState(StatePath, Integrity);
        }
        public SecureKeyringReference Reference(string? fingerprint = null, bool includePin = true) => new(KeyringFile, "BusChord synthetic keyring", Target, 4, Local,
            expectedSourceFingerprint: includePin ? fingerprint ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(KeyringFile))) : null);
        public KnxConnectionProfile Connection(IPEndPoint? endpoint = null) => KnxConnectionProfile.IpSecure(endpoint ?? new(IPAddress.Loopback, 3671), Reference(), Password);
        public KnxToolAccessProfile Profile(IReadOnlyCollection<string>? inventory = null, IKnxToolCounterStateSource? source = null) => new(Target, "Synthetic physical installation", "tool-v1", 64, source ?? StateSource, inventory ?? []);
        public async Task Provision(string epoch = "tool-v1", ulong next = 10, bool includeOwnFloor = false, IDataSecureMonotonicAnchor? anchor = null)
        {
            var floors = new Dictionary<IndividualAddress, ulong> { [Target] = 1 }; if (includeOwnFloor) floors.Add(Local, 0);
            await using var store = await DataSecureCounterStore.InitializeAsync(StatePath, KnxIndividualSecureCounterBinding.ToolAccess("Synthetic physical installation", epoch, Local).Identity, Integrity, new(next, floors), anchor);
        }
        public Task<DataSecureCounterStore> OpenState() => DataSecureCounterStore.OpenAsync(StatePath, KnxIndividualSecureCounterBinding.ToolAccess("Synthetic physical installation", "tool-v1", Local).Identity, Integrity);
        public KnxConnectionFactory Factory() => new(new(Path.Combine(Directory, "leases")), serial: new Serial(), createTransport: _ =>
        { Opens++; var peer = new ToolPeer(Device, new(IPAddress.Loopback, 3671), Assigned); peer.Gateway.BadGateway = BadGateway; Peers.Add(peer); return peer.Gateway; },
            localEndpoint: (_, _) => new(IPAddress.Loopback, 43123));
        public void Dispose() { StateSource.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Device
    { public List<ulong> SyncSequences { get; } = []; public List<ulong> DataSequences { get; } = []; public ulong NextRemote = 30; public int ManagementConnects; public bool HoldSync, TamperSync; public TaskCompletionSource SyncSeen { get; } = Gate(); }
    private sealed class Anchor : IDataSecureMonotonicAnchor
    {
        private readonly Dictionary<string, DataSecureCounterCheckpoint> _values = [];
        public bool Missing;
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Missing ? null : _values.GetValueOrDefault(context)); }
        public Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (_values.GetValueOrDefault(context) != expected) return Task.FromResult(false); _values[context] = proposed; return Task.FromResult(true); }
    }
    private sealed class ToolPeer
    {
        public IndependentSecureGateway Gateway { get; }
        private readonly Device _device; private byte _incoming, _transport; private bool _synchronized;
        public ToolPeer(Device device, IPEndPoint endpoint, IndividualAddress assigned)
        {
            _device = device; Gateway = new(endpoint)
            {
                ExpectedUserId = 4, AssignedAddressOverride = assigned,
                UserKeyOverride = Rfc2898DeriveBytes.Pbkdf2("user4", Encoding.ASCII.GetBytes("user-password.1.secure.ip.knx.org"), 65536, HashAlgorithmName.SHA256, 16),
                DeviceKeyOverride = Rfc2898DeriveBytes.Pbkdf2("dev", Encoding.ASCII.GetBytes("device-authentication-code.1.secure.ip.knx.org"), 65536, HashAlgorithmName.SHA256, 16),
                ApplicationPacket = Process,
            };
        }
        private static byte[] Frame(ServiceType service, byte[] body) => KnxNetIpFrame.Encode(service, body);
        private byte[] Incoming(byte[] cemi) => Frame(ServiceType.TunnellingRequest, Tunnelling.Request(7, _incoming++, cemi));
        private IReadOnlyList<byte[]>? Process(byte[] packet)
        {
            var frame = KnxNetIpFrame.Decode(packet);
            if (frame.Service == ServiceType.ConnectRequest) { _synchronized = false; _incoming = 0; _transport = 0; return null; }
            if (frame.Service != ServiceType.TunnellingRequest) return null;
            var (_, sequence, raw) = Tunnelling.ReadRequest(frame.Body); var cemi = Cemi.ParseLDataFrame(raw); var tpdu = cemi.Apdu;
            var result = new List<byte[]> { Frame(ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence)) };
            if (tpdu.Length == 1)
            { if (tpdu[0] == 0x80) { Require(_synchronized, "Management T_CONNECT preceded authenticated Tool Sync."); _device.ManagementConnects++; } return result; }
            if (tpdu.Length > 2 && tpdu[2] == 0x92)
            {
                Require(KnxIndividualSecureCodec.TryUnprotectSyncRequest(raw, ToolKey, KnxIndividualSecureKeyKind.ToolAccess, out var sync, out _), "Wrong key or plaintext Sync.");
                _device.SyncSequences.Add(sync!.Sequence); _device.SyncSeen.TrySetResult();
                try
                {
                    if (!_device.HoldSync)
                    {
                        var response = KnxIndividualSecureCodec.ProtectSyncResponse(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw, [0, 0]), sync.Challenge,
                            0xAABBCCDDEEFF, _device.NextRemote, 50, ToolKey, KnxIndividualSecureKeyKind.ToolAccess);
                        if (_device.TamperSync) response[^1] ^= 1;
                        result.Add(Incoming(response)); _synchronized = !_device.TamperSync;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(sync.Challenge); }
                return result;
            }
            var verified = KnxIndividualSecureCodec.TryUnprotectData(raw, ToolKey, KnxIndividualSecureKeyKind.ToolAccess, out var decoded, out _);
            Require(_synchronized && verified, "Management data was plain, used another key or preceded Sync.");
            _device.DataSequences.Add(decoded!.Sequence);
            try
            {
                var transportSequence = (decoded.Tpdu[0] >> 2) & 15;
                result.Add(Incoming(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw, [(byte)(0xC2 | transportSequence << 2)]))));
                result.Add(Incoming(KnxIndividualSecureCodec.ProtectData(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw,
                    [(byte)(0x43 | (_transport++ & 15) << 2), 0x40, 0, 0x12]), _device.NextRemote++, ToolKey, KnxIndividualSecureKeyKind.ToolAccess)));
            }
            finally { CryptographicOperations.ZeroMemory(decoded.Tpdu); }
            return result;
        }
    }
}
