using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

internal static class IndividualSecureTunnelChecks
{
    private static readonly IndividualAddress Local = new(0x11F0), Device = new(0x1101);
    private static readonly IPEndPoint Gateway = new(IPAddress.Loopback, 3671), LocalEndpoint = new(IPAddress.Loopback, 43125);
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), Integrity = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> ready) { for (var i = 0; i < 1000 && !ready(); i++) await Task.Delay(5); Require(ready(), "Condition did not complete."); }
    private static async Task<(DataSecureCounterStore Store, KnxIndividualSecureCemiProcessor Processor)> Policy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "buschord-individual-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var binding = KnxIndividualSecureCounterBinding.ToolAccess("Synthetic wire installation", "key-v1", Local);
        var store = await DataSecureCounterStore.InitializeAsync(Path.Combine(folder, "state.bin"), binding.Identity, Integrity, new(10, new Dictionary<IndividualAddress, ulong> { [Device] = 1 }));
        return (store, new(new(store, binding, Device, Key, 64)));
    }
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("concurrent initialization shares own Sync retries before a real management client", ManagedAsync),
            ("close joins initialization and a new connection rejects old challenge responses", ClosingAsync),
            ("only the exact owned pending Sync bypasses data protection", PendingOnlyAsync),
            ("real UDP loopback initializes and reads through secure individual management", RealUdpAsync),
            ("throwing initializer cancellation still disposes its processor owners", CallbackCleanupAsync),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS IndividualSecureTunnel " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL IndividualSecureTunnel " + name + ": " + ex); }
        }
        return failures;
    }
    private static async Task ManagedAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var peer = new Peer { DropFirstSyncAck = true, EmitSyncConfirmation = true };
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); var confirmations = 0; tunnel.ConfirmationReceived += _ => confirmations++;
        await tunnel.ConnectAsync(); await ThrowsAsync<InvalidOperationException>(() => tunnel.SendManagementTransportAsync(Device, [0x80]));
        await Task.WhenAll(tunnel.InitializeCemiSecurityAsync(), tunnel.InitializeCemiSecurityAsync());
        Require(policy.Processor.IsInitialized && peer.SyncRequests.Count == 2 && peer.SyncRequests[0].SequenceEqual(peer.SyncRequests[1]) && confirmations == 0, "Initialization repeated a new challenge, changed retry ciphertext or dispatched a Sync confirmation.");
        await using var client = new KnxManagementClient(new Management(tunnel), Device, true); await client.ConnectAsync(); var descriptor = await client.ReadDescriptorAsync();
        Require(descriptor.SequenceEqual(new byte[] { 0, 0x12 }) && peer.DataSequences.SequenceEqual(new ulong[] { 50 }) && await store.ReadReceiveFloorAsync(Device) == 30, "Management did not use authenticated individual data.");
    }
    private static async Task ClosingAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var peer = new Peer { HoldSyncResponse = true };
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); await tunnel.ConnectAsync(); var opening = tunnel.InitializeCemiSecurityAsync(); await peer.SyncSeen.Task;
        var stale = peer.LastResponse!.ToArray(); await tunnel.CloseAsync(); await ThrowsAsync<OperationCanceledException>(() => opening);
        Require(!policy.Processor.IsInitialized && tunnel.State == BusConnectionState.Disconnected, "Close retained initialized state.");
        await tunnel.ConnectAsync(); var next = tunnel.InitializeCemiSecurityAsync(); await Until(() => peer.SyncRequests.Count == 2); peer.Push(stale); await Task.Delay(40);
        Require(!next.IsCompleted && !policy.Processor.IsInitialized, "An old connection's Sync response completed a new challenge."); peer.Push(peer.LastResponse!); await next;
        Require(policy.Processor.IsInitialized && peer.SyncSequences.SequenceEqual(new ulong[] { 10, 11 }), "Reconnect reused a Sync nonce.");
    }
    private static async Task PendingOnlyAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; await using var processor = policy.Processor;
        var template = new CemiLDataFrame(CemiMessageCode.LDataReq, 0xB0, 0x60, Local, Device.Raw, new byte[] { 0, 0 });
        var forged = KnxIndividualSecureCodec.ProtectSyncRequest(template, 100, new byte[6], Key, KnxIndividualSecureKeyKind.ToolAccess);
        await ThrowsAsync<ArgumentException>(() => processor.PrepareOutgoingAsync(forged, Local, default));
        var sent = Gate(); var release = Gate(); byte[]? owned = null;
        var initializing = processor.InitializeAsync(Local, async (wire, ct) => { owned = wire.ToArray(); var prepared = await processor.PrepareOutgoingAsync(wire, Local, ct); Require(prepared.SequenceEqual(owned), "Owned Sync changed."); sent.TrySetResult(); await release.Task.WaitAsync(ct); });
        await sent.Task; var changed = owned!.ToArray(); changed[^1] ^= 1; await ThrowsAsync<ArgumentException>(() => processor.PrepareOutgoingAsync(changed, Local, default));
        processor.ResetConnection(); release.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => initializing);
    }
    private static async Task RealUdpAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var peer = new Peer(endpoint); var underlying = new UdpTransport(IPAddress.Loopback, 0); var local = underlying.LocalEndPoint; using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () => { try { while (!stop.IsCancellationRequested) { var packet = await server.ReceiveAsync(stop.Token); foreach (var response in peer.Process(packet.Buffer)) await server.SendAsync(response, packet.RemoteEndPoint, stop.Token); } } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } });
        try
        {
            await using (var tunnel = new KnxTunnel(underlying, endpoint, Local, local, null, policy.Processor))
            {
                await tunnel.ConnectAsync(); await tunnel.InitializeCemiSecurityAsync(); await using var client = new KnxManagementClient(new Management(tunnel), Device, true);
                await client.ConnectAsync(); Require((await client.ReadDescriptorAsync()).SequenceEqual(new byte[] { 0, 0x12 }) && peer.DataSequences.Single() == 50, "Actual UDP bypassed individual security.");
            }
            using var rebound = new UdpClient(local); Require(rebound.Client.LocalEndPoint!.Equals(local), "Individual security retained its UDP socket.");
        }
        finally { stop.Cancel(); await serving; peer.Dispose(); }
    }
    private static async Task CallbackCleanupAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var processor = policy.Processor; var started = Gate();
        var opening = processor.InitializeAsync(Local, async (_, ct) =>
        {
            using var registration = ct.Register(() => throw new IOException("Synthetic processor cancellation callback."));
            started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await started.Task; var closing = processor.DisposeAsync().AsTask(); await ThrowsAsync<OperationCanceledException>(() => opening); await ThrowsAsync<AggregateException>(() => closing);
        await ThrowsAsync<ObjectDisposedException>(() => processor.PrepareOutgoingAsync(new byte[2], Local, default));
        Require(!processor.IsInitialized && (await store.ReserveSendCountersAsync()).First == 11, "Processor disposal released no owners or consumed another nonce.");
    }
    private sealed class Management(KnxTunnel tunnel) : IManagementTransport
    {
        public IndividualAddress AssignedAddress => tunnel.AssignedAddress; public bool IsConnected => tunnel.State == BusConnectionState.Connected;
        public event Action<ManagementTransportFrame>? FrameReceived { add => tunnel.ManagementFrameReceived += value; remove => tunnel.ManagementFrameReceived -= value; }
        public Task SendAsync(IndividualAddress target, byte[] tpdu, CancellationToken ct) => tunnel.SendManagementTransportAsync(target, tpdu, ct);
    }
    private sealed class Peer(IPEndPoint? endpoint = null) : IUdpTransport
    {
        private readonly IPEndPoint _endpoint = endpoint ?? Gateway; private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
        private readonly object _gate = new(); private readonly List<byte[]> _syncRequests = []; private readonly List<ulong> _syncSequences = [], _dataSequences = [];
        private byte _incomingSequence; public bool DropFirstSyncAck, HoldSyncResponse, EmitSyncConfirmation; public byte[]? LastResponse; public TaskCompletionSource SyncSeen { get; } = Gate();
        public IReadOnlyList<byte[]> SyncRequests { get { lock (_gate) return _syncRequests.Select(b => b.ToArray()).ToArray(); } }
        public IReadOnlyList<ulong> SyncSequences { get { lock (_gate) return _syncSequences.ToArray(); } }
        public IReadOnlyList<ulong> DataSequences { get { lock (_gate) return _dataSequences.ToArray(); } }
        private static byte[] Frame(ServiceType service, byte[] body) => KnxNetIpFrame.Encode(service, body);
        private byte[] Incoming(byte[] cemi) => Frame(ServiceType.TunnellingRequest, Tunnelling.Request(7, _incomingSequence++, cemi));
        public void Push(byte[] cemi) => _incoming.Writer.TryWrite(new(Incoming(cemi), _endpoint));
        public IReadOnlyList<byte[]> Process(byte[] packet)
        {
            var frame = KnxNetIpFrame.Decode(packet); var result = new List<byte[]>();
            switch (frame.Service)
            {
                case ServiceType.ConnectRequest: _incomingSequence = 0; return [Frame(ServiceType.ConnectResponse, [7, 0, .. Hpai.Control(_endpoint), .. Crd.Tunnel(Local)])];
                case ServiceType.DisconnectRequest: return [Frame(ServiceType.DisconnectResponse, [7, 0])];
                case ServiceType.ConnectionStateRequest: return [Frame(ServiceType.ConnectionStateResponse, [7, 0])];
                case ServiceType.TunnellingRequest:
                    var (_, sequence, raw) = Tunnelling.ReadRequest(frame.Body); var cemi = Cemi.ParseLDataFrame(raw); var tpdu = cemi.Apdu;
                    var sync = tpdu.Length > 2 && tpdu[2] == 0x92;
                    if (sync && DropFirstSyncAck) DropFirstSyncAck = false; else result.Add(Frame(ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence)));
                    if (tpdu.Length == 1) return result;
                    if (sync)
                    {
                        Require(KnxIndividualSecureCodec.TryUnprotectSyncRequest(raw, Key, KnxIndividualSecureKeyKind.ToolAccess, out var request, out _), "Peer received forged Sync.");
                        var first = true; lock (_gate) { first = !_syncSequences.Contains(request!.Sequence); _syncRequests.Add(raw.ToArray()); if (first) _syncSequences.Add(request.Sequence); }
                        if (!first) { CryptographicOperations.ZeroMemory(request!.Challenge); return result; }
                        try { LastResponse = KnxIndividualSecureCodec.ProtectSyncResponse(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Device, Local.Raw, new byte[] { 0, 0 }), request!.Challenge, 0xAABBCCDDEEFF, 30, 50, Key, KnxIndividualSecureKeyKind.ToolAccess); }
                        finally { CryptographicOperations.ZeroMemory(request!.Challenge); }
                        if (EmitSyncConfirmation) { var con = raw.ToArray(); con[0] = (byte)CemiMessageCode.LDataCon; result.Add(Incoming(con)); }
                        if (!HoldSyncResponse) result.Add(Incoming(LastResponse)); SyncSeen.TrySetResult(); return result;
                    }
                    Require(KnxIndividualSecureCodec.TryUnprotectData(raw, Key, KnxIndividualSecureKeyKind.ToolAccess, out var data, out _), "Peer received plaintext management data.");
                    lock (_gate) _dataSequences.Add(data!.Sequence);
                    result.Add(Incoming(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Device, Local.Raw, new byte[] { 0xC2 }))));
                    result.Add(Incoming(KnxIndividualSecureCodec.ProtectData(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Device, Local.Raw, new byte[] { 0x43, 0x40, 0, 0x12 }), 30, Key, KnxIndividualSecureKeyKind.ToolAccess)));
                    CryptographicOperations.ZeroMemory(data!.Tpdu); return result;
                default: return result;
            }
        }
        public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); foreach (var frame in Process(data)) _incoming.Writer.TryWrite(new(frame, _endpoint)); return Task.CompletedTask; }
        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default) => _incoming.Reader.ReadAsync(ct).AsTask(); public void Dispose() { }
    }
}
