using System.Net;
using System.Net.Sockets;
using Knx;
using Knx.Bus;

// Only the loopback socket is used: no discovery, multicast or hardware endpoints.
internal sealed class LocalTunnelServer : IAsyncDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<byte?> _responses;
    private readonly Task _loop;
    private IPEndPoint? _client;
    public TaskCompletionSource IgnoredConnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // null deliberately leaves the handshake unanswered, so the caller can cancel it.
    public LocalTunnelServer(params byte?[] responses)
    {
        _responses = new(responses);
        _loop = RunAsync();
    }

    public KnxDevice Device => new()
    {
        ControlEndpoint = (IPEndPoint)_socket.Client.LocalEndPoint!, Name = "Local mock",
        Serial = [], MulticastAddress = "", MacAddress = "", FriendlyName = "Local mock",
        KnxAddress = "1.1.1", SupportedFamilies = []
    };

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var packet = await _socket.ReceiveAsync(_stop.Token);
                var frame = KnxNetIpFrame.Decode(packet.Buffer);
                switch (frame.Service)
                {
                    case ServiceType.ConnectRequest:
                        _client = packet.RemoteEndPoint;
                        var status = _responses.Count > 0 ? _responses.Dequeue() : (byte?)0;
                        if (status is null)
                        {
                            IgnoredConnect.TrySetResult();
                            break;
                        }
                        byte[] body = status == 0
                            ? [7, 0, .. Hpai.Control(Device.ControlEndpoint), .. Crd.Tunnel(new IndividualAddress(1, 1, 250))]
                            : [0, status.Value];
                        await SendAsync(ServiceType.ConnectResponse, body);
                        break;
                    case ServiceType.DisconnectRequest:
                        await SendAsync(ServiceType.DisconnectResponse, Connection.Response(7));
                        break;
                    case ServiceType.ConnectionStateRequest:
                        await SendAsync(ServiceType.ConnectionStateResponse, Connection.Response(7));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task SendAsync(ServiceType service, byte[] body)
        => await _socket.SendAsync(KnxNetIpFrame.Encode(service, body), _client!);

    public Task TelegramAsync(byte sequence = 0)
        => SendAsync(ServiceType.TunnellingRequest,
            Tunnelling.Request(7, sequence, Convert.FromHexString("2900BCD010063015010081")));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _socket.Dispose();
        _stop.Dispose();
    }
}
