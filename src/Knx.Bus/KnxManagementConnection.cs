using System.Net;
using Knx;

namespace Knx.Bus;

/// <summary>Owns both the management session and its dedicated KNXnet/IP tunnel.</summary>
public sealed class KnxManagementConnection : IManagementConnection
{
    private readonly KnxTunnel _tunnel;
    public KnxManagementClient Client { get; }

    private KnxManagementConnection(KnxTunnel tunnel, KnxManagementClient client) { _tunnel = tunnel; Client = client; }

    public static async Task<KnxManagementConnection> OpenAsync(IPEndPoint gateway, IndividualAddress target,
        IndividualAddress requestedAddress, Action<string>? wireLog = null, CancellationToken ct = default)
    {
        var tunnel = new KnxTunnel(new UdpTransport(0), gateway, requestedAddress);
        if (wireLog is not null) tunnel.LogLine += wireLog;
        KnxManagementClient? client = null;
        try
        {
            await tunnel.ConnectAsync(ct);
            client = new(tunnel, target);
            await client.ConnectAsync(ct);
            return new(tunnel, client);
        }
        catch
        {
            try { if (client is not null) await client.DisposeAsync(); }
            finally { await tunnel.DisposeAsync(); }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); }
        finally { await _tunnel.DisposeAsync(); }
    }
}
