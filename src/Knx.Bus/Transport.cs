using System.Net;
using System.Net.Sockets;

namespace Knx.Bus;

/// <summary>UDP transport seam: real sockets in production, loopback fake in tests.</summary>
public interface IUdpTransport : IDisposable
{
    Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default);
    Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>
/// A transport that can join an IPv4 multicast group. Routing needs this; a plain
/// <see cref="IUdpTransport"/> must be rejected instead of pretending to be connected.
/// </summary>
public interface IMulticastUdpTransport : IUdpTransport
{
    void JoinMulticastGroup(IPAddress group, IPAddress? localAddress = null);
    void LeaveMulticastGroup(IPAddress group);
}

public sealed class UdpTransport : IUdpTransport, IMulticastUdpTransport
{
    private readonly UdpClient _client;
    private readonly HashSet<IPAddress> _joinedGroups = new();

    public UdpTransport(int localPort) : this(IPAddress.Any, localPort) { }

    /// <summary>Binds to a specific local address so multicast/broadcast leaves that interface.</summary>
    public UdpTransport(IPAddress bindAddress, int localPort, bool reuseAddress = false)
    {
        if (reuseAddress)
        {
            // KNXnet/IP routing listens on 3671 next to other tools (ETS, monitors); a shared bind
            // is what makes a passive listener possible on Windows.
            _client = new UdpClient(bindAddress.AddressFamily);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(bindAddress, localPort));
        }
        else
        {
            _client = new UdpClient(new IPEndPoint(bindAddress, localPort));
        }
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

    /// <summary>Joins a multicast group, optionally on one interface (needed on multi-homed hosts).</summary>
    public void JoinMulticastGroup(IPAddress group, IPAddress? localAddress = null)
    {
        if (localAddress is null || localAddress.Equals(IPAddress.Any)) _client.JoinMulticastGroup(group);
        else _client.JoinMulticastGroup(group, localAddress);
        _joinedGroups.Add(group);
    }

    public void LeaveMulticastGroup(IPAddress group)
    {
        if (!_joinedGroups.Remove(group)) return;
        try
        {
            _client.DropMulticastGroup(group);
        }
        catch (SocketException)
        {
            // The socket is already gone; leaving an unreachable group has no further effect.
        }
    }

    public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        => _client.SendAsync(data, data.Length, destination).WaitAsync(ct);

    public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
        => _client.ReceiveAsync(ct).AsTask();

    public void Dispose() => _client.Dispose();
}
