using System.Net;
using Knx;

namespace Knx.Bus;

/// <summary>
/// High-level bus facade: device discovery, tunnelling, routing and the aggregated
/// telegram feed (bus monitor).
/// </summary>
public sealed class KnxBus : IAsyncDisposable
{
    // Read-only policy: the current writer targets a fixed memory address without descriptor/mask/
    // programming-mode verification, and group writes have not been validated on real hardware.
    // Keep every real bus write and download disabled until that is verified.
    public static bool WritesEnabled => false;

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

    /// <summary>
    /// Opens routing on the multicast group, bound to the routing port on
    /// <paramref name="localAddress"/> (the KNX segment NIC on multi-homed hosts).
    /// </summary>
    public async Task ConnectRoutingAsync(IPAddress? localAddress = null, CancellationToken ct = default)
    {
        if (_transport is not null) throw new InvalidOperationException("already connected");
        var transport = new UdpTransport(localAddress ?? IPAddress.Any, Discovery.MulticastPort, reuseAddress: true);
        var routing = new KnxRouting(transport, localAddress);
        try
        {
            routing.TelegramReceived += t => TelegramReceived?.Invoke(t);
            await routing.OpenAsync(ct);
        }
        catch
        {
            await routing.DisposeAsync();
            throw;
        }
        _transport = transport;
        _routing = routing;
        DeviceName = "Routing";
        StateChanged?.Invoke(BusConnectionState.Connected);
    }

    public async Task GroupValueWriteAsync(Knx.GroupAddress address, byte[] data, int? dataBits = null)
    {
        if (!WritesEnabled)
            throw new NotSupportedException(
                "Bus writes are disabled by the read-only policy; only discovery, connect/disconnect " +
                "and read paths are enabled in this build.");
        if (_tunnel is not null) await _tunnel.GroupValueWriteAsync(address, data, dataBits);
        else if (_routing is not null) await _routing.GroupValueWriteAsync(address, data, dataBits);
        else throw new InvalidOperationException("not connected");
    }

    public async Task GroupValueReadAsync(Knx.GroupAddress address)
    {
        if (_tunnel is null) throw new InvalidOperationException("read requires a tunnel");
        await _tunnel.GroupValueReadAsync(address);
    }

    /// <summary>
    /// Reads one group value and waits for a GroupValueResponse from a device (not the
    /// transport and not our own confirmation). Null when nothing answered in time.
    /// </summary>
    public async Task<BusTelegram?> ReadGroupValueAsync(Knx.GroupAddress address, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (_tunnel is null) throw new InvalidOperationException("read requires a tunnel");
        return await _tunnel.ReadGroupValueAsync(address, timeout, ct);
    }

    /// <summary>
    /// Flashes an application program to a device over a fresh tunnel. Disabled by policy: a future
    /// implementation must read the descriptor, build a plan with <see cref="DownloadPreflight"/> and
    /// hand a ready plan to <see cref="DownloadSession.RunAsync"/>.
    /// </summary>
    public Task<Knx.Bus.DownloadResult> DownloadAsync(KnxDevice device, IndividualAddress deviceAddress,
        IApplicationProgram program, IProgress<Knx.Bus.DownloadProgress> progress, CancellationToken ct = default)
    {
        if (!WritesEnabled)
            throw new NotSupportedException(
                "Real device download is disabled by the read-only policy: descriptor/mask read, " +
                "programming mode and verified load procedures are not implemented. A future download " +
                "must pass the read-only DownloadPreflight gate (DownloadSession.RunAsync requires a " +
                "ready plan). Use the explicit simulation instead.");

        // Not reachable while WritesEnabled is false: the old fixed-address writer is gone because it
        // never passed the gate. Rebuild the pipeline on top of DownloadPreflight and a ready plan.
        throw new NotSupportedException(
            "Real device download is not implemented: no ready DownloadPreflight plan exists; no frame was sent.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_tunnel is not null) await _tunnel.DisposeAsync();
        if (_routing is not null) await _routing.DisposeAsync();
        _tunnel = null;
        _routing = null;
        _transport = null;
        StateChanged?.Invoke(BusConnectionState.Disconnected);
    }
}
