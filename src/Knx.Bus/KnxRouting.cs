using System.Net;
using System.Net.Sockets;
using Knx;

namespace Knx.Bus;

/// <summary>
/// KNXnet/IP routing connection: joins the multicast routing group and
/// receives/sends group traffic as ROUTING_INDICATION frames.
/// </summary>
public sealed class KnxRouting : IAsyncDisposable
{
    private readonly IUdpTransport _transport;
    private readonly IPEndPoint _multicastEndpoint;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    public KnxRouting(IUdpTransport transport)
    {
        _transport = transport;
        _multicastEndpoint = new IPEndPoint(IPAddress.Parse(Discovery.MulticastIp), Discovery.MulticastPort);
    }

    public BusConnectionState State { get; private set; } = BusConnectionState.Disconnected;
    public event Action<BusTelegram>? TelegramReceived;
    public event Action<string>? LogLine;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        State = BusConnectionState.Connected;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        LogLine?.Invoke("routing geöffnet");
    }

    public async Task GroupValueWriteAsync(Knx.GroupAddress address, byte[] data, CancellationToken ct = default)
    {
        var cemi = Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = new IndividualAddress(address.Raw),
            IsGroupAddress = true,
            Apci = Apci.GroupValueWrite,
            Data = data,
        });
        await _transport.SendAsync(_multicastEndpoint, KnxNetIpFrame.Encode(ServiceType.RoutingIndication, cemi), ct);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _transport.ReceiveAsync(_cts.Token);
                var frame = KnxNetIpFrame.Decode(result.Buffer);
                if (frame.Service != ServiceType.RoutingIndication) continue;
                if (frame.Body.Length == 0 || frame.Body[0] != (byte)CemiMessageCode.LDataInd) continue;
                var telegram = Cemi.ParseLData(frame.Body);
                TelegramReceived?.Invoke(new BusTelegram { Telegram = telegram, Source = "routing" });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogLine?.Invoke($"routing loop: {ex.Message}");
            State = BusConnectionState.Disconnected;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
        _transport.Dispose();
    }
}
