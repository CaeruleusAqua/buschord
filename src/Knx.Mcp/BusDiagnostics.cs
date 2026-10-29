using System.Net;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;

namespace Knx.Mcp;

/// <summary>
/// Read-only diagnostics use either the active bus tunnel or one complete factory
/// operation with its own tunnel and gateway reservation.
/// </summary>
internal static class BusDiagnostics
{
    public static async Task<T> WithSessionAsync<T>(KnxConnectionFactory factory, KnxConnectionProfile profile,
        Func<IBusDiagnosticSession, CancellationToken, Task<T>> action, CancellationToken ct = default,
        Action<KnxConnectionSecurity>? connected = null)
    {
        await using var operation = await factory.AcquireOperationAsync(profile, ct).ConfigureAwait(false);
        await using var connection = await operation.OpenTunnelAsync(ct).ConfigureAwait(false);
        connected?.Invoke(connection.Security);
        return await action(new TunnelDiagnosticSession(connection.Tunnel), ct).ConfigureAwait(false);
    }
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
        return await WithSessionAsync(new KnxConnectionFactory(), KnxConnectionProfile.Plain(device.ControlEndpoint, OwnAddress),
            (session, _) => action(session), ct).ConfigureAwait(false);
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
