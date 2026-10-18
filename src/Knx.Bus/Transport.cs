using System.Net;
using System.Net.Sockets;

namespace Knx.Bus;

/// <summary>UDP transport seam: real sockets in production, loopback fake in tests.</summary>
public interface IUdpTransport : IDisposable
{
    Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default);
    Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default);
}

public sealed class UdpTransport : IUdpTransport
{
    private readonly UdpClient _client;

    public UdpTransport(int localPort) : this(IPAddress.Any, localPort) { }

    /// <summary>Binds to a specific local address so multicast/broadcast leaves that interface.</summary>
    public UdpTransport(IPAddress bindAddress, int localPort)
    {
        _client = new UdpClient(new IPEndPoint(bindAddress, localPort));
        _client.EnableBroadcast = true;
        _client.MulticastLoopback = false;
    }

    public static UdpTransport Broadcast() => new(0);

    /// <summary>Local endpoint the socket is bound to (HPAI address of a search request).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_client.Client.LocalEndPoint!;

    /// <summary>
    /// Local address the OS would use to reach <paramref name="remote"/>; needed for a correct
    /// HPAI when the socket is bound to 0.0.0.0.
    /// </summary>
    public static IPAddress ResolveLocalAddress(IPEndPoint remote)
    {
        try
        {
            using var probe = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            probe.Connect(remote);
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return IPAddress.Any;
        }
    }

    public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        => _client.SendAsync(data, data.Length, destination).WaitAsync(ct);

    public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
        => _client.ReceiveAsync(ct).AsTask();

    public void Dispose() => _client.Dispose();
}
