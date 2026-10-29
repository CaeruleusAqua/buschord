using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

internal static class DataSecureTunnelChecks
{
    private static readonly IPEndPoint Gateway = new(IPAddress.Loopback, 3671), LocalEndpoint = new(IPAddress.Loopback, 43124);
    private static readonly IndividualAddress Local = new(1, 1, 240), Sender = new(1, 1, 1);
    private static readonly GroupAddress Group = new(0x101), PlainGroup = new(0x102);
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), Integrity = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string detail) { if (!value) throw new Exception(detail); }
    private static async Task Until(Func<bool> ready) { for (var i = 0; i < 1000 && !ready(); i++) await Task.Delay(5); Require(ready(), "Tunnel condition did not complete."); }
    private static async Task Refused<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static byte[] Secure(ulong sequence, IndividualAddress? sender = null, GroupAddress? group = null, bool confirmation = false)
    {
        var frame = new CemiLDataFrame(CemiMessageCode.LDataInd, 0xBC, 0xE0, sender ?? Sender, (group ?? Group).Raw, new byte[] { 0, 128, 1 });
        var wire = KnxDataSecureCodec.ProtectCemi(frame, sequence, Key, KnxDataProtection.AuthenticationAndConfidentiality);
        if (confirmation) wire[0] = (byte)CemiMessageCode.LDataCon; return wire;
    }
    private static byte[] Frame(ServiceType service, byte[] body) => KnxNetIpFrame.Encode(service, body);
    private static async Task<(DataSecureCounterStore Store, KnxDataSecureCemiProcessor Processor)> Policy(GatedAnchor? anchor = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "buschord-secure-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var identity = new DataSecureStateIdentity("Synthetic wire installation", "synthetic-v1", Local);
        var store = await DataSecureCounterStore.InitializeAsync(Path.Combine(folder, "state.bin"), identity, Integrity,
            new(10, new Dictionary<IndividualAddress, ulong> { [Sender] = 1, [Local] = 1 }), anchor);
        var context = new KnxDataSecureGroupContext(store, identity, [new(Group, Key, KnxDataProtection.AuthenticationAndConfidentiality, new[] { Sender, Local })]);
        return (store, new(context, new[] { PlainGroup }));
    }
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("outgoing protection is prepared once and retries identical ciphertext", RetryAsync),
            ("durable send reservation precedes the first physical transport send", SendCommitAsync),
            ("net-IP duplicates still ACK without a second security admission", DuplicateAndReplayAsync),
            ("the tunnel ACK precedes slow receive admission while data waits for its commit", ReceiveCommitAsync),
            ("own confirmations bind prepared ciphertext without moving receive floors", ConfirmationAsync),
            ("plain policy is explicit and unknown/secure-individual traffic cannot fall back", PolicyAsync),
            ("a processor's borrowed output cannot mutate retained retry bytes", BorrowedOutputAsync),
            ("queued caller buffers are snapshotted before waiting for the send gate", QueuedCallerMutationAsync),
            ("close joins a cancelled security preparation before disposing its owner", ClosingAsync),
            ("failed reset cannot reconnect and failed security disposal still releases UDP", BrokenOwnerAsync),
            ("real UDP loopback carries encrypted outgoing and authenticated incoming groups", RealUdpAsync),
        })
        {
            try { await run().WaitAsync(Deadline); Console.WriteLine("PASS DataSecureTunnel " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL DataSecureTunnel " + name + ": " + ex); }
        }
        return failures;
    }
    private static async Task RetryAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var counting = new Counted(policy.Processor);
        var peer = new Peer { DropFirstAck = true }; await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, counting);
        await tunnel.ConnectAsync(); await tunnel.GroupValueWriteAsync(Group, [1]);
        Require(counting.Prepared == 1 && peer.Requests.Count == 2 && peer.Requests[0].SequenceEqual(peer.Requests[1]), "A retry rebuilt or changed the secure frame.");
        var (_, _, cemi) = Tunnelling.ReadRequest(KnxNetIpFrame.Decode(peer.Requests[0]).Body);
        Require(KnxDataSecureCodec.TryUnprotectCemi(cemi, Key, out var decoded, out _) && decoded!.Sequence == 10 && decoded.Source == Local, "Secure source/counter did not reach the wire.");
        Require((await store.ReserveSendCountersAsync()).First == 11, "The retry consumed another Data Secure counter.");
    }
    private static async Task SendCommitAsync()
    {
        var anchor = new GatedAnchor(); var policy = await Policy(anchor); await using var store = policy.Store;
        var peer = new Peer(); await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); await tunnel.ConnectAsync();
        anchor.HoldNext = true; var sending = tunnel.GroupValueReadAsync(Group); await anchor.Started.Task;
        Require(peer.Requests.Count == 0 && !sending.IsCompleted, "An uncommitted send counter reached the transport."); anchor.Release.TrySetResult(); await sending;
        Require(peer.Requests.Count == 1, "A committed read was not protected and sent.");
    }
    private static async Task DuplicateAndReplayAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var counting = new Counted(policy.Processor); var peer = new Peer();
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, counting); var received = 0; tunnel.TelegramReceived += _ => Interlocked.Increment(ref received); await tunnel.ConnectAsync();
        var wire = Secure(2); peer.Push(0, wire); await Until(() => received == 1 && peer.Acks == 1);
        peer.Push(0, wire); await Until(() => peer.Acks == 2); Require(received == 1 && counting.Incoming == 1, "A duplicate was not ACKed or was admitted twice.");
        peer.Push(1, wire); await Until(() => peer.Acks == 3 && counting.IncomingCompleted == 2); Require(received == 1 && policy.Processor.LastRejection == CemiSecurityRejection.Replay, "A Secure replay with a fresh net-IP sequence released data.");
        peer.Push(2, Secure(3)); await Until(() => received == 2 && peer.Acks == 4); Require(await store.ReadReceiveFloorAsync(Sender) == 3, "Dropping a replay desynchronized the net-IP receive sequence.");
    }
    private static async Task ReceiveCommitAsync()
    {
        var anchor = new GatedAnchor(); var policy = await Policy(anchor); await using var store = policy.Store; var peer = new Peer();
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); var received = 0; tunnel.TelegramReceived += _ => received++; await tunnel.ConnectAsync();
        anchor.HoldNext = true; var wire = Secure(2); peer.Push(0, wire); await anchor.Started.Task;
        Require(peer.Acks == 1 && received == 0, "The ACK waited for storage, or data escaped before commit."); peer.Push(0, wire); anchor.Release.TrySetResult();
        await Until(() => peer.Acks == 2 && received == 1); Require(await store.ReadReceiveFloorAsync(Sender) == 2, "Duplicate handling changed the durable floor.");
    }
    private static async Task ConfirmationAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var peer = new Peer();
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); var confirmations = 0; var received = 0;
        tunnel.ConfirmationReceived += _ => confirmations++; tunnel.TelegramReceived += _ => received++; await tunnel.ConnectAsync(); await tunnel.GroupValueWriteAsync(Group, [1]);
        var (_, _, sent) = Tunnelling.ReadRequest(KnxNetIpFrame.Decode(peer.Requests.Single()).Body); var confirmation = sent.ToArray(); confirmation[0] = (byte)CemiMessageCode.LDataCon;
        peer.Push(0, confirmation); await Until(() => confirmations == 1); Require(received == 0 && await store.ReadReceiveFloorAsync(Local) == 1, "An own confirmation became received group data or changed its replay floor.");
        peer.Push(1, Secure(99, Local, confirmation: true)); peer.Push(2, confirmation); await Until(() => peer.Acks == 3);
        Require(confirmations == 1 && received == 0 && await store.ReadReceiveFloorAsync(Local) == 1, "A forged or repeated own confirmation was accepted.");
        await tunnel.CloseAsync(); await tunnel.ConnectAsync(); peer.Push(0, confirmation); await Until(() => peer.Acks == 4); Require(confirmations == 1, "A previous connection's confirmation cache survived close.");
    }
    private static async Task PolicyAsync()
    {
        var policy = await Policy(); await using var store = policy.Store; var peer = new Peer();
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); var received = 0; tunnel.TelegramReceived += _ => received++; await tunnel.ConnectAsync();
        await tunnel.GroupValueWriteAsync(PlainGroup, [1]); var (_, _, sent) = Tunnelling.ReadRequest(KnxNetIpFrame.Decode(peer.Requests.Single()).Body);
        Require(Cemi.ParseLDataFrame(sent).Apdu.SequenceEqual(new byte[] { 0, 128, 1 }), "An explicit plain group was changed.");
        await Refused<InvalidOperationException>(() => tunnel.GroupValueReadAsync(new(0x103))); Require(peer.Requests.Count == 1, "An unconfigured group reached the wire.");
        peer.Push(0, Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xBC, 0xE0, Sender, Group.Raw, new byte[] { 0, 128, 1 })));
        var individual = new CemiLDataFrame(CemiMessageCode.LDataInd, 0xB0, 0x60, Sender, Local.Raw, new byte[] { 3, 0xF1, 0x90, 0, 0, 0, 0, 0, 1, 1, 2, 3, 4 });
        peer.Push(1, Cemi.EncodeLDataFrame(individual)); await Until(() => peer.Acks == 2); Require(received == 0, "Plain downgrade or unsupported secure individual traffic passed.");
    }
    private static async Task BorrowedOutputAsync()
    {
        var processor = new Borrowed(); var peer = new Peer { DropFirstAck = true }; await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, processor);
        await tunnel.ConnectAsync(); var sending = tunnel.GroupValueWriteAsync(PlainGroup, [1]); await peer.FirstRequest.Task; processor.Output![^1] ^= 1; await sending;
        Require(processor.Prepared == 1 && peer.Requests.Count == 2 && peer.Requests[0].SequenceEqual(peer.Requests[1]), "A processor's borrowed output changed the retry.");
    }
    private static async Task QueuedCallerMutationAsync()
    {
        var anchor = new GatedAnchor(); var policy = await Policy(anchor); await using var store = policy.Store; var peer = new Peer();
        await using var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, policy.Processor); await tunnel.ConnectAsync();
        anchor.HoldNext = true; var first = tunnel.GroupValueWriteAsync(Group, [1]); await anchor.Started.Task;
        var caller = Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataReq, 0xBC, 0xE0, Local, Group.Raw, new byte[] { 0, 128, 3 }));
        var queued = tunnel.RequestAsync(caller, Apci.GroupValueWrite, Sender, Deadline);
        caller[^1] = 9; anchor.Release.TrySetResult(); await first; await Until(() => peer.Requests.Count == 2);
        var (_, _, wire) = Tunnelling.ReadRequest(KnxNetIpFrame.Decode(peer.Requests[1]).Body);
        Require(KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out var decoded, out _) && decoded!.Apdu.SequenceEqual(new byte[] { 0, 128, 3 }), "A queued caller edit changed the authenticated outgoing command.");
        peer.Push(0, Secure(2)); Require(await queued is not null, "The snapshotted request did not finish normally.");
    }
    private static async Task ClosingAsync()
    {
        var processor = new Blocking(); var peer = new Peer(); var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, processor); await tunnel.ConnectAsync();
        var sending = tunnel.GroupValueWriteAsync(Group, [1]); await processor.Started.Task; var closing = tunnel.DisposeAsync().AsTask(); await processor.Cancelled.Task;
        Require(!closing.IsCompleted && peer.Disposals == 0 && processor.Disposals == 0 && peer.Requests.Count == 0, "Close released resources before preparation unwound.");
        processor.Release.TrySetResult(); await closing; await Refused<OperationCanceledException>(() => sending);
        Require(processor.Disposals == 1 && peer.Disposals == 1, "Joined security/UDP owners were not disposed once.");
    }
    private static async Task BrokenOwnerAsync()
    {
        var processor = new Borrowed { ThrowReset = true, ThrowDispose = true }; var peer = new Peer(); var tunnel = new KnxTunnel(peer, Gateway, Local, LocalEndpoint, null, processor);
        await tunnel.ConnectAsync(); await tunnel.CloseAsync(); await Refused<InvalidOperationException>(() => tunnel.ConnectAsync()); await Refused<IOException>(() => tunnel.DisposeAsync().AsTask());
        Require(peer.Disposals == 1 && processor.Disposals == 1 && tunnel.State == BusConnectionState.Closed, "A failed security owner prevented UDP cleanup or left reconnect enabled.");
    }
    private static async Task RealUdpAsync()
    {
        var policy = await Policy(); await using var store = policy.Store;
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var peer = new Peer(endpoint); var underlying = new UdpTransport(IPAddress.Loopback, 0); var local = underlying.LocalEndPoint; using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var incoming = await server.ReceiveAsync(stop.Token); foreach (var response in peer.Process(incoming.Buffer)) await server.SendAsync(response, incoming.RemoteEndPoint, stop.Token); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            await using (var tunnel = new KnxTunnel(underlying, endpoint, Local, local, null, policy.Processor))
            {
                var received = new TaskCompletionSource<BusTelegram>(TaskCreationOptions.RunContinuationsAsynchronously); tunnel.TelegramReceived += frame => received.TrySetResult(frame);
                await tunnel.ConnectAsync(); await tunnel.GroupValueWriteAsync(Group, [1]);
                var (_, _, outgoing) = Tunnelling.ReadRequest(KnxNetIpFrame.Decode(peer.Requests.Single()).Body);
                Require(KnxDataSecureCodec.TryUnprotectCemi(outgoing, Key, out var decoded, out _) && decoded!.Sequence == 10, "Actual UDP sent an unprotected group value.");
                await server.SendAsync(Frame(ServiceType.TunnellingRequest, Tunnelling.Request(7, 0, Secure(2))), local);
                var telegram = await received.Task.WaitAsync(Deadline); Require(telegram.Telegram.Data.SequenceEqual(new byte[] { 1 }), "Actual UDP failed to authenticate/decrypt its group value.");
            }
            using var rebound = new UdpClient(local); Require(rebound.Client.LocalEndPoint!.Equals(local), "The Data Secure tunnel retained its UDP socket.");
        }
        finally { stop.Cancel(); await serving; peer.Dispose(); }
    }

    private sealed class Peer(IPEndPoint? endpoint = null) : IUdpTransport
    {
        private readonly IPEndPoint _endpoint = endpoint ?? Gateway;
        private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
        private readonly object _gate = new(); private readonly List<byte[]> _requests = [];
        public IReadOnlyList<byte[]> Requests { get { lock (_gate) return _requests.Select(b => b.ToArray()).ToArray(); } }
        public bool DropFirstAck; public int Acks, Disposals; public TaskCompletionSource FirstRequest { get; } = Gate();
        public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); foreach (var reply in Process(data)) _incoming.Writer.TryWrite(new(reply, _endpoint)); return Task.CompletedTask; }
        public IReadOnlyList<byte[]> Process(byte[] data)
        {
            var frame = KnxNetIpFrame.Decode(data);
            switch (frame.Service)
            {
                case ServiceType.ConnectRequest: return [Frame(ServiceType.ConnectResponse, [7, 0, .. Hpai.Control(_endpoint), .. Crd.Tunnel(Local)])];
                case ServiceType.ConnectionStateRequest: return [Frame(ServiceType.ConnectionStateResponse, [7, 0])];
                case ServiceType.DisconnectRequest: return [Frame(ServiceType.DisconnectResponse, [7, 0])];
                case ServiceType.TunnellingRequest:
                    lock (_gate) _requests.Add(data.ToArray()); FirstRequest.TrySetResult();
                    if (DropFirstAck) { DropFirstAck = false; return []; }
                    var (_, sequence, _) = Tunnelling.ReadRequest(frame.Body); return [Frame(ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence))];
                case ServiceType.TunnellingAck: Interlocked.Increment(ref Acks); return [];
                default: return [];
            }
        }
        public void Push(byte sequence, byte[] cemi) => _incoming.Writer.TryWrite(new(Frame(ServiceType.TunnellingRequest, Tunnelling.Request(7, sequence, cemi)), _endpoint));
        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default) => _incoming.Reader.ReadAsync(ct).AsTask();
        public void Dispose() => Disposals++;
    }
    private sealed class Counted(ICemiSecurityProcessor inner) : ICemiSecurityProcessor
    {
        public int Prepared, Incoming, IncomingCompleted;
        public Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assigned, CancellationToken ct) { Interlocked.Increment(ref Prepared); return inner.PrepareOutgoingAsync(cemi, assigned, ct); }
        public async Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assigned, CancellationToken ct)
        { Interlocked.Increment(ref Incoming); try { return await inner.ProcessIncomingAsync(cemi, assigned, ct); } finally { Interlocked.Increment(ref IncomingCompleted); } }
        public void ResetConnection() => inner.ResetConnection(); public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Borrowed : ICemiSecurityProcessor
    {
        public byte[]? Output; public int Prepared, Disposals; public bool ThrowReset, ThrowDispose;
        public Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assigned, CancellationToken ct) { Prepared++; Output = cemi.ToArray(); return Task.FromResult(Output); }
        public Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assigned, CancellationToken ct) => Task.FromResult<byte[]?>(cemi.ToArray());
        public void ResetConnection() { if (ThrowReset) throw new IOException("Synthetic broken reset."); }
        public ValueTask DisposeAsync() { Disposals++; return ThrowDispose ? ValueTask.FromException(new IOException("Synthetic broken processor disposal.")) : ValueTask.CompletedTask; }
    }
    private sealed class Blocking : ICemiSecurityProcessor
    {
        public TaskCompletionSource Started { get; } = Gate(); public TaskCompletionSource Cancelled { get; } = Gate(); public TaskCompletionSource Release { get; } = Gate(); public int Disposals;
        public async Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assigned, CancellationToken ct)
        { Started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Cancelled.TrySetResult(); await Release.Task; throw; } return cemi.ToArray(); }
        public Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assigned, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public void ResetConnection() { } public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class GatedAnchor : IDataSecureMonotonicAnchor
    {
        private readonly Dictionary<string, DataSecureCounterCheckpoint> _values = []; private readonly object _gate = new();
        public bool HoldNext; public TaskCompletionSource Started { get; } = Gate(); public TaskCompletionSource Release { get; } = Gate();
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_values.GetValueOrDefault(context)); }
        public async Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        {
            if (HoldNext && expected is not null) { HoldNext = false; Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
            ct.ThrowIfCancellationRequested(); lock (_gate) { if (_values.GetValueOrDefault(context) != expected) return false; _values[context] = proposed; return true; }
        }
    }
}
