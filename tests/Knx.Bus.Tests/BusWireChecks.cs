using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Knx;
using Knx.Bus;

/// <summary>
/// Wire-level checks for the items that need a connected tunnel or a multicast seam:
/// L_Data.con filtering, group-read response matching and routing join/leave.
/// </summary>
internal static class BusWireChecks
{
    /// <summary>L_Data.con is a confirmation of our own frame, not bus traffic.</summary>
    public static async Task<(bool Ok, string Detail)> ConfirmationFilterAsync()
    {
        var (_, fake, tunnel) = await StartAsync();
        try
        {
            var telegrams = 0;
            var confirmations = 0;
            Telegram? last = null;
            tunnel.TelegramReceived += t => { Interlocked.Increment(ref telegrams); last = t.Telegram; };
            tunnel.ConfirmationReceived += _ => Interlocked.Increment(ref confirmations);

            await fake.PushAsync(0, Convert.FromHexString("2E00BCE0100100010100D6"));
            await WaitUntilAsync(() => confirmations == 1);
            await fake.PushAsync(1, Convert.FromHexString("2900BCD010063015010081"));
            await WaitUntilAsync(() => telegrams == 1);

            var ok = telegrams == 1 && confirmations == 1
                     && last is { IsGroupAddress: true, Apci: Apci.GroupValueWrite, HopCount: 5 };
            return (ok, $"telegrams={telegrams} confirmations={confirmations} last={last?.Apci} hop={last?.HopCount}");
        }
        finally
        {
            fake.Stop();
            await tunnel.DisposeAsync();
        }
    }

    /// <summary>Read matching: our own confirmation echo must not answer, a device response must.</summary>
    public static async Task<(bool Ok, string Detail)> ReadGroupValueAsync(bool echoOnly)
    {
        var (_, fake, tunnel) = await StartAsync();
        try
        {
            var address = GroupAddress.ThreeLevel(1, 1, 2);
            var read = tunnel.ReadGroupValueAsync(address, TimeSpan.FromMilliseconds(echoOnly ? 300 : 2000));
            await WaitUntilAsync(() => fake.Bodies(ServiceType.TunnellingRequest).Any(b => b.Length > 4));
            var request = fake.Bodies(ServiceType.TunnellingRequest).First(b => b.Length > 4)[4..];
            var requestTelegram = Cemi.ParseLData(request);
            await fake.PushAsync(0, Convert.FromHexString("2E00BCE0100100010100D6"));
            if (!echoOnly)
                await fake.PushAsync(1, Convert.FromHexString("2900BCD011050902010041"));

            var answer = await read;
            var ok = requestTelegram.Apci == Apci.GroupValueRead
                     && requestTelegram.Source == fake.AssignedAddress
                     && (echoOnly
                         ? answer is null
                         : answer is { Source: "tunnel" }
                           && answer.Telegram.Source == new IndividualAddress(1, 1, 5)
                           && answer.Telegram.Apci == Apci.GroupValueResponse
                           && answer.Telegram.Data is [0x01]);
            return (ok, $"request={requestTelegram.Apci} src={requestTelegram.Source} answer=" +
                        (answer is null ? "none" : $"{answer.Telegram.Source} {answer.Telegram.Apci}"));
        }
        finally
        {
            fake.Stop();
            await tunnel.DisposeAsync();
        }
    }

    /// <summary>Routing opens only with a joined multicast transport and closes by leaving the group.</summary>
    public static async Task<(bool Ok, string Detail)> RoutingJoinAsync()
    {
        var transport = new FakeMulticastTransport();
        var routing = new KnxRouting(transport, IPAddress.Parse("192.0.2.12"));
        try
        {
            await routing.OpenAsync();
            var joined = transport.JoinedGroup?.ToString() == Discovery.MulticastIp
                         && transport.JoinedLocal?.ToString() == "192.0.2.12"
                         && routing.State == BusConnectionState.Connected;

            var received = new TaskCompletionSource<Telegram>(TaskCreationOptions.RunContinuationsAsynchronously);
            routing.TelegramReceived += t => received.TrySetResult(t.Telegram);
            await transport.PushRoutingAsync(Convert.FromHexString("2900BCD010063015010081"));
            var telegram = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var decoded = telegram.IsGroupAddress && telegram.HopCount == 5
                          && telegram.Apci == Apci.GroupValueWrite && telegram.Data is [0x01];

            await routing.CloseAsync();
            var left = transport.Leaves == 1 && routing.State == BusConnectionState.Disconnected;

            var plain = new KnxRouting(new LoopbackPair().Client);
            var rejected = false;
            try { await plain.OpenAsync(); }
            catch (NotSupportedException) { rejected = plain.State == BusConnectionState.Disconnected; }
            await plain.DisposeAsync();

            return (joined && decoded && left && rejected,
                $"joined={joined} decoded={decoded} left={left} rejectedWithoutMulticast={rejected}");
        }
        finally
        {
            await routing.DisposeAsync();
        }
    }

    private static async Task<(LoopbackPair Pair, FakeKnxDevice Fake, KnxTunnel Tunnel)> StartAsync(byte channel = 7)
    {
        var pair = new LoopbackPair();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 3671);
        var fake = new FakeKnxDevice(pair.Server, endpoint, channel);
        fake.Start();
        var tunnel = new KnxTunnel(pair.Client, endpoint, new IndividualAddress(15, 15, 250),
            localEndpoint: new IPEndPoint(IPAddress.Loopback, 40011));
        await tunnel.ConnectAsync();
        return (pair, fake, tunnel);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}

/// <summary>Multicast-capable transport fake: records join/leave and feeds ROUTING_INDICATION frames.</summary>
internal sealed class FakeMulticastTransport : IMulticastUdpTransport
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();

    public IPAddress? JoinedGroup { get; private set; }
    public IPAddress? JoinedLocal { get; private set; }
    public int Leaves { get; private set; }

    public void JoinMulticastGroup(IPAddress group, IPAddress? localAddress = null)
    {
        JoinedGroup = group;
        JoinedLocal = localAddress;
    }

    public void LeaveMulticastGroup(IPAddress group) => Leaves++;

    public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
        => _incoming.Reader.ReadAsync(ct).AsTask().ContinueWith(
            t => new UdpReceiveResult(t.Result, new IPEndPoint(IPAddress.Loopback, 3671)), ct);

    public Task PushRoutingAsync(byte[] cemi)
    {
        _incoming.Writer.TryWrite(KnxNetIpFrame.Encode(ServiceType.RoutingIndication, cemi));
        return Task.CompletedTask;
    }

    public void Dispose() => _incoming.Writer.TryComplete();
}
