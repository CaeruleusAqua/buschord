using System.Net;
using Knx;
using Knx.Bus;

namespace Knx.Mcp;

/// <summary>
/// Short-lived read-only tunnel behind bus_scan / bus_device_info. KnxBus exposes no
/// management-frame send, so these tools open their own KNXnet/IP tunnelling connection,
/// send only IndividualAddressRead/DeviceDescriptorRead and close it again.
/// </summary>
internal static class BusDiagnostics
{
    // A second tunnel must not reuse the address of the main bus_connect tunnel (default 1.1.250).
    private static readonly IndividualAddress OwnAddress = new(1, 1, 249);

    public static async Task<T> WithSessionAsync<T>(string? gateway, int timeoutMs,
        Func<IBusDiagnosticSession, Task<T>> action, CancellationToken ct = default)
    {
        var host = IPAddress.TryParse(gateway, out _) ? gateway : null;
        var devices = await Discovery.DiscoverAsync(TimeSpan.FromMilliseconds(timeoutMs), host, ct);
        if (devices.Count == 0)
            throw new InvalidOperationException($"no KNXnet/IP gateway answered within {timeoutMs} ms");

        var device = Select(devices, gateway);
        var transport = new UdpTransport(0);
        await using var tunnel = new KnxTunnel(transport, device.ControlEndpoint, OwnAddress);
        await tunnel.ConnectAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 3000), ct);
        return await action(new TunnelDiagnosticSession(tunnel));
    }

    private static KnxDevice Select(IReadOnlyList<KnxDevice> devices, string? gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway)) return devices[0];
        return devices.FirstOrDefault(d =>
                   d.Name.Contains(gateway, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(d.KnxAddress, gateway, StringComparison.OrdinalIgnoreCase)
                   || (IPAddress.TryParse(gateway, out var address) && d.ControlEndpoint.Address.Equals(address))
                   || string.Equals(d.ControlEndpoint.ToString(), gateway, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"no gateway matched '{gateway}'; found: " +
                   string.Join(", ", devices.Select(d => $"{d.Name} ({d.KnxAddress}, {d.ControlEndpoint})")));
    }
}
