using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Knx;
using Knx.Bus;

/// <summary>Peer/channel isolation using in-memory datagrams, including separate control and data ports.</summary>
internal static class TunnelPeerChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("connect ignores another address and port before the selected peer replies", ConnectPeerAsync),
            ("mutable caller endpoints cannot change the pinned peer", EndpointCopiesAsync),
            ("only the negotiated data peer can dispatch and consume a sequence", DataPeerAsync),
            ("foreign, stale and malformed acknowledgements cannot complete a send", AckPeerAsync),
            ("heartbeat uses the control peer and ignores other channels and lengths", HeartbeatPeerAsync),
            ("disconnect completion requires the control peer and exact channel response", DisconnectResponseAsync),
            ("remote disconnect requires the control peer, channel and complete request", RemoteDisconnectAsync),
            ("reconnect discards control and data frames from the preceding channel", ReconnectChannelAsync),
            ("connect refuses another connection profile or trailing response body", ConnectShapeAsync),
        })
        {
            try { await run().WaitAsync(Deadline); Console.WriteLine("PASS TunnelPeer " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL TunnelPeer " + name + ": " + ex); }
        }
        return failures;
    }

    private static async Task ConnectPeerAsync()
    {
        await using var fixture = new Fixture();
        fixture.Transport.AutoConnect = false;
        var connecting = fixture.Tunnel.ConnectAsync();
        var request = await fixture.Transport.NextAsync(ServiceType.ConnectRequest);
        Require(request.Peer.Equals(Fixture.Control), "CONNECT was not sent to the selected control peer.");
        foreach (var foreign in new[] { Fixture.Other, Fixture.Data })
        {
            var ignored = fixture.WaitLogAsync("ignored datagram from " + foreign);
            fixture.Transport.Push(foreign, ServiceType.ConnectResponse, fixture.Transport.ConnectBody(99));
            await ignored;
            Require(!connecting.IsCompleted && fixture.Tunnel.State == BusConnectionState.Connecting,
                "An unselected address or port completed CONNECT.");
        }
        fixture.Transport.Push(Fixture.Control, ServiceType.ConnectResponse, fixture.Transport.ConnectBody());
        await connecting;
        Require(fixture.Tunnel.Channel == 7 && fixture.Tunnel.DataEndpoint.Equals(Fixture.Data),
            "The real peer did not establish the selected tunnel after foreign replies.");
    }

    private static async Task EndpointCopiesAsync()
    {
        var control = new IPEndPoint(Fixture.Control.Address, Fixture.Control.Port);
        var local = new IPEndPoint(IPAddress.Parse("192.0.2.30"), 49000);
        await using var fixture = new Fixture(control, local);
        control.Port++; local.Port++;
        await fixture.Tunnel.ConnectAsync();
        var request = await fixture.Transport.NextAsync(ServiceType.ConnectRequest);
        Require(request.Peer.Equals(Fixture.Control) && Hpai.Read(request.Frame.Body).Endpoint.Port == 49000,
            "Mutating constructor arguments redirected a peer or the advertised local endpoint.");
        var exposed = fixture.Tunnel.DataEndpoint; exposed.Port++;
        await fixture.Tunnel.GroupValueReadAsync(new GroupAddress(1));
        Require((await fixture.Transport.NextAsync(ServiceType.TunnellingRequest)).Peer.Equals(Fixture.Data),
            "Mutating the returned data endpoint redirected transmission.");
    }

    private static async Task DataPeerAsync()
    {
        await using var fixture = new Fixture(); await fixture.Tunnel.ConnectAsync();
        var callbacks = 0;
        fixture.Tunnel.TelegramReceived += _ => Interlocked.Increment(ref callbacks);
        foreach (var peer in new[] { Fixture.Other, Fixture.Control })
            fixture.Transport.Push(peer, ServiceType.TunnellingRequest, Tunnelling.Request(7, 0, Indication()));
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingRequest, Tunnelling.Request(6, 0, Indication()));
        var reserved = Tunnelling.Request(7, 0, Indication()); reserved[3] = 1;
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingRequest, reserved);
        await fixture.FlushAsync();
        Require(callbacks == 0 && fixture.Transport.Count(ServiceType.TunnellingAck) == 0,
            "An unselected peer, stale channel or reserved octet was dispatched or acknowledged.");
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingRequest, Tunnelling.Request(7, 0, Indication()));
        await fixture.FlushAsync();
        Require(callbacks == 1 && fixture.Transport.Count(ServiceType.TunnellingAck) == 1,
            "Foreign data consumed the valid incoming sequence.");
        Require((await fixture.Transport.NextAsync(ServiceType.TunnellingAck)).Peer.Equals(Fixture.Data),
            "The data acknowledgement used the control port.");
    }

    private static async Task AckPeerAsync()
    {
        await using var fixture = new Fixture(); await fixture.Tunnel.ConnectAsync();
        fixture.Transport.AutoDataAck = false;
        var sending = fixture.Tunnel.GroupValueReadAsync(new GroupAddress(1));
        var sent = await fixture.Transport.NextAsync(ServiceType.TunnellingRequest);
        var (_, sequence, _) = Tunnelling.ReadRequest(sent.Frame.Body);
        foreach (var peer in new[] { Fixture.Other, Fixture.Control })
            fixture.Transport.Push(peer, ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence));
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingAck, Tunnelling.Ack(6, sequence));
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingAck, [.. Tunnelling.Ack(7, sequence), 0]);
        await fixture.FlushAsync();
        Require(!sending.IsCompleted, "A foreign, old-channel or overlong ACK completed a send.");
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence));
        await sending;
        Require(fixture.Transport.Count(ServiceType.TunnellingRequest) == 1, "The valid ACK unnecessarily repeated the send.");
    }

    private static async Task HeartbeatPeerAsync()
    {
        await using var fixture = new Fixture(heartbeat: TimeSpan.FromMilliseconds(50));
        await fixture.Tunnel.ConnectAsync();
        var first = await fixture.Transport.NextAsync(ServiceType.ConnectionStateRequest);
        Require(first.Peer.Equals(Fixture.Control), "Heartbeat used the negotiated data port.");
        foreach (var peer in new[] { Fixture.Other, Fixture.Data })
            fixture.Transport.Push(peer, ServiceType.ConnectionStateResponse, Connection.Response(7, 0x21));
        foreach (var body in new byte[][] { [6, 0x21], [7], [7, 0x21, 0] })
            fixture.Transport.Push(Fixture.Control, ServiceType.ConnectionStateResponse, body);
        await fixture.FlushAsync();
        Require(fixture.Tunnel.State == BusConnectionState.Connected && fixture.Transport.Count(ServiceType.ConnectionStateRequest) == 1,
            "A foreign or malformed heartbeat reply changed the active wait.");
        fixture.Transport.Push(Fixture.Control, ServiceType.ConnectionStateResponse, Connection.Response(7));
        var next = await fixture.Transport.NextAsync(ServiceType.ConnectionStateRequest);
        Require(next.Peer.Equals(Fixture.Control) && fixture.Tunnel.State == BusConnectionState.Connected,
            "The valid control-peer response did not complete the heartbeat.");
    }

    private static async Task DisconnectResponseAsync()
    {
        await using var fixture = new Fixture(); await fixture.Tunnel.ConnectAsync();
        fixture.Transport.AutoClose = false;
        var closed = fixture.Tunnel.CloseAsync();
        Require((await fixture.Transport.NextAsync(ServiceType.DisconnectRequest)).Peer.Equals(Fixture.Control),
            "Disconnect used the data port.");
        foreach (var peer in new[] { Fixture.Other, Fixture.Data })
            fixture.Transport.Push(peer, ServiceType.DisconnectResponse, Connection.Response(7));
        foreach (var body in new byte[][] { [6, 0], [7], [7, 0, 0] })
            fixture.Transport.Push(Fixture.Control, ServiceType.DisconnectResponse, body);
        await fixture.FlushAsync();
        Require(!closed.IsCompleted && fixture.Tunnel.State == BusConnectionState.Closing,
            "An unrelated or malformed response acknowledged the current disconnect.");
        fixture.Transport.Push(Fixture.Control, ServiceType.DisconnectResponse, Connection.Response(7));
        await closed;
        Require(fixture.Tunnel.State == BusConnectionState.Disconnected, "A valid response did not complete close.");
    }

    private static async Task RemoteDisconnectAsync()
    {
        await using var fixture = new Fixture(); await fixture.Tunnel.ConnectAsync();
        foreach (var peer in new[] { Fixture.Other, Fixture.Data })
            fixture.Transport.Push(peer, ServiceType.DisconnectRequest, Connection.DisconnectRequest(7, Fixture.Control));
        var reserved = Connection.DisconnectRequest(7, Fixture.Control); reserved[1] = 1;
        var hpai = Connection.DisconnectRequest(7, Fixture.Control); hpai[3] = 2;
        foreach (var body in new byte[][] { [7, 0], reserved, hpai,
            [.. Connection.DisconnectRequest(7, Fixture.Control), 0], Connection.DisconnectRequest(6, Fixture.Control) })
            fixture.Transport.Push(Fixture.Control, ServiceType.DisconnectRequest, body);
        await fixture.FlushAsync();
        Require(fixture.Tunnel.State == BusConnectionState.Connected && fixture.Transport.Count(ServiceType.DisconnectResponse) == 0,
            "An unrelated or malformed request disconnected the current channel.");
        var disconnected = Signal(); fixture.Tunnel.Disconnected += () => disconnected.TrySetResult();
        fixture.Transport.Push(Fixture.Control, ServiceType.DisconnectRequest, Connection.DisconnectRequest(7, Fixture.Control));
        await disconnected.Task;
        Require((await fixture.Transport.NextAsync(ServiceType.DisconnectResponse)).Peer.Equals(Fixture.Control)
            && fixture.Transport.Count(ServiceType.DisconnectRequest) == 0, "The remote close response used an unselected peer or sent another request.");
    }

    private static async Task ReconnectChannelAsync()
    {
        await using var fixture = new Fixture(); await fixture.Tunnel.ConnectAsync();
        await fixture.Tunnel.CloseAsync(); fixture.Transport.ChannelId = 8;
        await fixture.Tunnel.ConnectAsync();
        var callbacks = 0; fixture.Tunnel.TelegramReceived += _ => Interlocked.Increment(ref callbacks);
        fixture.Transport.Push(Fixture.Control, ServiceType.ConnectionStateResponse, Connection.Response(7, 0x21));
        fixture.Transport.Push(Fixture.Control, ServiceType.DisconnectRequest, Connection.DisconnectRequest(7, Fixture.Control));
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingRequest, Tunnelling.Request(7, 0, Indication()));
        await fixture.FlushAsync();
        Require(fixture.Tunnel.State == BusConnectionState.Connected && fixture.Tunnel.Channel == 8 && callbacks == 0,
            "The previous channel affected the new connection.");
        fixture.Transport.Push(Fixture.Data, ServiceType.TunnellingRequest, Tunnelling.Request(8, 0, Indication()));
        await fixture.FlushAsync(); Require(callbacks == 1, "The current channel did not receive after stale frames.");
    }

    private static async Task ConnectShapeAsync()
    {
        foreach (var body in new byte[][] { [7, 0, .. Hpai.Data(Fixture.Data), 2, 3],
            [7, 0, .. Hpai.Data(Fixture.Data), .. Crd.Tunnel(new(1, 1, 250)), 0] })
        {
            await using var fixture = new Fixture(); fixture.Transport.AutoConnect = false;
            var connecting = fixture.Tunnel.ConnectAsync(); await fixture.Transport.NextAsync(ServiceType.ConnectRequest);
            fixture.Transport.Push(Fixture.Control, ServiceType.ConnectResponse, body);
            var refused = false; try { await connecting; } catch (FormatException) { refused = true; }
            Require(refused && fixture.Tunnel.State == BusConnectionState.Disconnected,
                "CONNECT accepted a different profile or trailing bytes.");
        }
    }

    private static byte[] Indication() => Convert.FromHexString("2900BCD011050902010041");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly IPEndPoint Control = new(IPAddress.Parse("192.0.2.10"), 3671);
        internal static readonly IPEndPoint Data = new(IPAddress.Parse("192.0.2.10"), 4671);
        internal static readonly IPEndPoint Other = new(IPAddress.Parse("192.0.2.11"), 3671);
        internal readonly PeerTransport Transport = new();
        internal KnxTunnel Tunnel { get; }
        internal Fixture(IPEndPoint? control = null, IPEndPoint? local = null, TimeSpan? heartbeat = null)
            => Tunnel = new(Transport, control ?? Control, new(1, 1, 250), local, heartbeat);
        internal async Task WaitLogAsync(string prefix)
        {
            var completed = Signal();
            void Observe(string line) { if (line.StartsWith(prefix, StringComparison.Ordinal)) completed.TrySetResult(); }
            Tunnel.LogLine += Observe;
            try { await completed.Task.WaitAsync(Deadline); }
            finally { Tunnel.LogLine -= Observe; }
        }
        internal async Task FlushAsync()
        {
            var flushed = WaitLogAsync("<- SearchResponse");
            Transport.Push(Control, ServiceType.SearchResponse, []);
            await flushed;
        }
        public async ValueTask DisposeAsync()
        {
            if (Tunnel.State == BusConnectionState.Closing)
                Transport.Push(Control, ServiceType.DisconnectResponse, Connection.Response(Transport.ChannelId));
            await Tunnel.DisposeAsync();
        }
    }

    private sealed record Sent(IPEndPoint Peer, KnxNetIpFrame Frame);
    private sealed class PeerTransport : IUdpTransport
    {
        private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
        private readonly Channel<Sent> _sent = Channel.CreateUnbounded<Sent>();
        private readonly ConcurrentQueue<Sent> _history = new();
        internal bool AutoConnect = true, AutoDataAck = true, AutoClose = true;
        internal byte ChannelId = 7;
        internal byte[] ConnectBody(byte? channel = null) => [channel ?? ChannelId, 0,
            .. Hpai.Data(Fixture.Data), .. Crd.Tunnel(new IndividualAddress(1, 1, 250))];
        internal void Push(IPEndPoint peer, ServiceType service, byte[] body)
            => _incoming.Writer.TryWrite(new(KnxNetIpFrame.Encode(service, body), peer));
        internal int Count(ServiceType service) => _history.Count(sent => sent.Frame.Service == service);
        internal async Task<Sent> NextAsync(ServiceType service)
        {
            while (true)
            {
                var sent = await _sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                if (sent.Frame.Service == service) return sent;
            }
        }
        public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var frame = KnxNetIpFrame.Decode(data);
            var sent = new Sent(new(destination.Address, destination.Port), frame); _history.Enqueue(sent); _sent.Writer.TryWrite(sent);
            if (frame.Service == ServiceType.ConnectRequest && AutoConnect)
                Push(Fixture.Control, ServiceType.ConnectResponse, ConnectBody());
            if (frame.Service == ServiceType.TunnellingRequest && AutoDataAck)
            {
                var (channel, sequence, _) = Tunnelling.ReadRequest(frame.Body);
                Push(Fixture.Data, ServiceType.TunnellingAck, Tunnelling.Ack(channel, sequence));
            }
            if (frame.Service == ServiceType.DisconnectRequest && AutoClose)
                Push(Fixture.Control, ServiceType.DisconnectResponse, Connection.Response(ChannelId));
            return Task.CompletedTask;
        }
        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default) => _incoming.Reader.ReadAsync(ct).AsTask();
        public void Dispose() { }
    }
}
