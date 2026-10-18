using Knx;

namespace Knx.Bus;

/// <summary>
/// High-level bus facade: device discovery, tunnelling, routing and the aggregated
/// telegram feed (bus monitor).
/// </summary>
public sealed class KnxBus : IAsyncDisposable
{
    private IUdpTransport? _transport;
    private KnxTunnel? _tunnel;
    private KnxRouting? _routing;

    public event Action<BusTelegram>? TelegramReceived;
    public event Action<BusConnectionState>? StateChanged;

    public BusConnectionState State => _tunnel?.State ?? _routing?.State ?? BusConnectionState.Disconnected;

    public string? DeviceName { get; private set; }

    public async Task<List<KnxDevice>> DiscoverAsync(TimeSpan? timeout = null, string? host = null,
        CancellationToken ct = default)
        => await Discovery.DiscoverAsync(timeout ?? TimeSpan.FromSeconds(2), host, ct);

    public async Task ConnectTunnelAsync(KnxDevice device, IndividualAddress individualAddress,
        CancellationToken ct = default)
    {
        if (_transport is not null) throw new InvalidOperationException("already connected");
        _transport = UdpTransport.Broadcast();
        DeviceName = device.Name;
        _tunnel = new KnxTunnel(_transport, device.ControlEndpoint, individualAddress);
        _tunnel.TelegramReceived += t => TelegramReceived?.Invoke(t);
        _tunnel.Disconnected += () => StateChanged?.Invoke(BusConnectionState.Disconnected);
        await _tunnel.ConnectAsync(ct);
        StateChanged?.Invoke(BusConnectionState.Connected);
    }

    public async Task ConnectRoutingAsync(CancellationToken ct = default)
    {
        if (_transport is not null) throw new InvalidOperationException("already connected");
        _transport = UdpTransport.Broadcast();
        DeviceName = "Routing";
        _routing = new KnxRouting(_transport);
        _routing.TelegramReceived += t => TelegramReceived?.Invoke(t);
        await _routing.OpenAsync(ct);
        StateChanged?.Invoke(BusConnectionState.Connected);
    }

    public async Task GroupValueWriteAsync(Knx.GroupAddress address, byte[] data)
    {
        if (_tunnel is not null) await _tunnel.GroupValueWriteAsync(address, data);
        else if (_routing is not null) await _routing.GroupValueWriteAsync(address, data);
        else throw new InvalidOperationException("not connected");
    }

    public async Task GroupValueReadAsync(Knx.GroupAddress address)
    {
        if (_tunnel is null) throw new InvalidOperationException("read requires a tunnel");
        await _tunnel.GroupValueReadAsync(address);
    }

    /// <summary>Flashes an application program to a device over a fresh tunnel.</summary>
    public async Task<Knx.Bus.DownloadResult> DownloadAsync(KnxDevice device, IndividualAddress deviceAddress,
        IApplicationProgram program, IProgress<Knx.Bus.DownloadProgress> progress, CancellationToken ct = default)
    {
        using var transport = UdpTransport.Broadcast();
        await using var tunnel = new KnxTunnel(transport, device.ControlEndpoint, new IndividualAddress(1, 1, 250));
        await tunnel.ConnectAsync(ct);
        var session = new DownloadSession(tunnel, deviceAddress, program);
        session.Progress += p => progress.Report(p);
        return await session.RunAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_tunnel is not null) await _tunnel.DisposeAsync();
        if (_routing is not null) await _routing.DisposeAsync();
        _tunnel = null;
        _routing = null;
        _transport = null;
    }
}
