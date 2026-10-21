using System.Net;
using System.Net.Sockets;
using Knx;

namespace Knx.Bus;

/// <summary>
/// KNXnet/IP routing connection: joins the routing multicast group on the selected
/// interface, binds the routing port at the caller and receives/sends group traffic as
/// ROUTING_INDICATION frames. Opening fails loudly when the transport cannot join.
/// </summary>
public sealed class KnxRouting : IAsyncDisposable
{
    private readonly IUdpTransport _transport;
    private readonly IPEndPoint _multicastEndpoint;
    private readonly IPAddress? _localAddress;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private bool _joined;
    private bool _closed;

    public KnxRouting(IUdpTransport transport, IPAddress? localAddress = null)
    {
        _transport = transport;
        _localAddress = localAddress;
        _multicastEndpoint = new IPEndPoint(IPAddress.Parse(Discovery.MulticastIp), Discovery.MulticastPort);
    }

    public BusConnectionState State { get; private set; } = BusConnectionState.Disconnected;
    public event Action<BusTelegram>? TelegramReceived;
    public event Action<string>? LogLine;

    /// <summary>Joins 224.0.23.12 and starts the receive loop. Never reports Connected without a join.</summary>
    public Task OpenAsync(CancellationToken ct = default)
    {
        if (_closed || State != BusConnectionState.Disconnected)
            throw new InvalidOperationException($"routing is {State}, not openable");
        if (_transport is not IMulticastUdpTransport multicast)
            throw new NotSupportedException(
                "routing requires a multicast-capable UDP transport; this transport cannot join " +
                $"{_multicastEndpoint.Address}");

        State = BusConnectionState.Connecting;
        try
        {
            multicast.JoinMulticastGroup(_multicastEndpoint.Address, _localAddress);
            _joined = true;
        }
        catch (SocketException ex)
        {
            State = BusConnectionState.Disconnected;
            throw new IOException(
                $"cannot join {_multicastEndpoint.Address} on {_localAddress?.ToString() ?? "the default interface"}: {ex.Message}",
                ex);
        }

        State = BusConnectionState.Connected;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        LogLine?.Invoke($"routing geöffnet: {_multicastEndpoint.Address}:{_multicastEndpoint.Port} " +
                        $"auf {_localAddress?.ToString() ?? "Standard-Schnittstelle"}");
        return Task.CompletedTask;
    }

    public Task GroupValueWriteAsync(Knx.GroupAddress address, byte[] data, int? dataBits = null,
        CancellationToken ct = default)
    {
        var cemi = Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = new IndividualAddress(address.Raw),
            IsGroupAddress = true,
            Apci = Apci.GroupValueWrite,
            DataBits = dataBits,
            Data = data,
        });
        return _transport.SendAsync(_multicastEndpoint,
            KnxNetIpFrame.Encode(ServiceType.RoutingIndication, cemi), ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _transport.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                LogLine?.Invoke($"routing receive: {ex.SocketErrorCode}");
                continue;
            }

            try
            {
                var frame = KnxNetIpFrame.Decode(result.Buffer);
                if (frame.Service != ServiceType.RoutingIndication) continue;
                if (frame.Body.Length == 0 || frame.Body[0] != (byte)CemiMessageCode.LDataInd) continue;
                var telegram = Cemi.ParseLData(frame.Body);
                TelegramReceived?.Invoke(new BusTelegram { Telegram = telegram, Source = "routing" });
            }
            catch (FormatException ex)
            {
                LogLine?.Invoke($"routing frame: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogLine?.Invoke($"routing loop: {ex.Message}");
                if (_joined && _transport is IMulticastUdpTransport multicast)
                {
                    multicast.LeaveMulticastGroup(_multicastEndpoint.Address);
                    _joined = false;
                }
                State = BusConnectionState.Disconnected;
                return;
            }
        }
    }

    /// <summary>Stops the loop and leaves the multicast group. Safe to call more than once.</summary>
    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        State = BusConnectionState.Closing;
        await _cts.CancelAsync();
        if (_joined && _transport is IMulticastUdpTransport multicast)
        {
            multicast.LeaveMulticastGroup(_multicastEndpoint.Address);
            _joined = false;
        }
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
        State = BusConnectionState.Disconnected;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _transport.Dispose();
        _cts.Dispose();
        State = BusConnectionState.Closed;
    }
}
