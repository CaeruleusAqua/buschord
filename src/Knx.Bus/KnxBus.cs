using System.Net;
using Knx;
using Knx.Bus.Connections;

namespace Knx.Bus;

/// <summary>Explicit bus facade. Configuration writes remain disabled independently of transport authentication.</summary>
public sealed class KnxBus : IAsyncDisposable
{
    public static bool WritesEnabled => false;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _transition = new(1,1), _diagnostics = new(1,1);
    private readonly KnxConnectionFactory _connections;
    private CancellationTokenSource? _connecting;
    private CancellationTokenSource _connectedLifetime = new();
    private KnxGatewayOperation? _operation;
    private KnxConnectedTunnel? _owned;
    private KnxTunnel? _tunnel;
    private KnxRouting? _routing;
    private bool _disconnecting;
    private Task? _closing;
    public KnxBus() : this(null) { }
    public KnxBus(KnxConnectionFactory? connections) => _connections = connections ?? new();
    public event Action<BusTelegram>? TelegramReceived;
    public event Action<BusConnectionState>? StateChanged;
    public BusConnectionState State => _tunnel?.State ?? _routing?.State
        ?? (_connecting is not null ? BusConnectionState.Connecting : BusConnectionState.Disconnected);
    public string? DeviceName { get; private set; }
    public KnxConnectionProfile? Profile => _operation?.Profile;
    public KnxConnectionSecurity? Security => _owned?.Security;
    public Task<List<KnxDevice>> DiscoverAsync(TimeSpan? timeout = null, string? host = null, CancellationToken ct = default)
        => Discovery.DiscoverAsync(timeout ?? TimeSpan.FromSeconds(2), host, ct);
    public async Task ConnectTunnelAsync(KnxDevice device, IndividualAddress individualAddress, CancellationToken ct = default)
    { await ConnectAsync(KnxConnectionProfile.Plain(device.ControlEndpoint, individualAddress), ct); DeviceName = device.Name; }
    public async Task ConnectAsync(KnxConnectionProfile profile, CancellationToken ct = default)
    {
        CancellationTokenSource connecting;
        lock(_sync)
        {
            if (_connecting is not null || _operation is not null || _routing is not null || _disconnecting) throw new KnxConnectionException(KnxConnectionFailure.Occupied);
            _connecting = connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        await _transition.WaitAsync().ConfigureAwait(false);
        KnxGatewayOperation? operation = null; KnxConnectedTunnel? owned = null;
        try
        {
            operation = await _connections.AcquireOperationAsync(profile, connecting.Token).ConfigureAwait(false);
            owned = await operation.OpenTunnelAsync(connecting.Token).ConfigureAwait(false);
            connecting.Token.ThrowIfCancellationRequested();
            _connectedLifetime.Dispose(); _connectedLifetime = new();
            _operation = operation; _owned = owned; _tunnel = owned.Tunnel;
            _tunnel.TelegramReceived += OnTelegram; _tunnel.Disconnected += OnDisconnected;
            DeviceName = profile.RequiresAuthentication ? "KNX IP Secure" : "KNX UDP";
            NotifyState(BusConnectionState.Connected);
        }
        catch
        {
            try { if(owned is not null)await owned.DisposeAsync().ConfigureAwait(false); }
            finally { if(operation is not null) await operation.DisposeAsync().ConfigureAwait(false); }
            _operation = null; _owned = null; _tunnel = null; DeviceName = null; throw;
        }
        finally { lock(_sync) if(ReferenceEquals(_connecting,connecting))_connecting=null; connecting.Dispose(); _transition.Release(); }
    }
    private void OnTelegram(BusTelegram value)
    { foreach (var observer in TelegramReceived?.GetInvocationList() ?? []) try { ((Action<BusTelegram>)observer)(value); } catch { } }
    private void NotifyState(BusConnectionState state)
    { foreach (var observer in StateChanged?.GetInvocationList() ?? []) try { ((Action<BusConnectionState>)observer)(state); } catch { } }
    private void OnDisconnected() { try { _connectedLifetime.Cancel(); } catch (ObjectDisposedException) { } NotifyState(BusConnectionState.Disconnected); }
    public async Task ConnectRoutingAsync(IPAddress? localAddress = null, CancellationToken ct = default)
    {
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(_sync) if(_connecting is not null || _operation is not null || _routing is not null || _disconnecting)throw new KnxConnectionException(KnxConnectionFailure.Occupied);
            var routing = new KnxRouting(new UdpTransport(localAddress ?? IPAddress.Any, Discovery.MulticastPort, reuseAddress:true), localAddress);
            try { routing.TelegramReceived += OnTelegram; await routing.OpenAsync(ct).ConfigureAwait(false); }
            catch { await routing.DisposeAsync().ConfigureAwait(false); throw; }
            _routing=routing; DeviceName="Routing"; NotifyState(BusConnectionState.Connected);
        }
        finally { _transition.Release(); }
    }
    public Task GroupValueWriteAsync(Knx.GroupAddress address, byte[] data, int? dataBits = null)
        => throw new NotSupportedException("Bus writes are disabled by the read-only policy.");
    public Task GroupValueReadAsync(Knx.GroupAddress address)
        => (_tunnel ?? throw new InvalidOperationException("Read requires a tunnel.")).GroupValueReadAsync(address, _connectedLifetime.Token);
    public Task<BusTelegram?> ReadGroupValueAsync(Knx.GroupAddress address, TimeSpan timeout, CancellationToken ct=default)
        => ReadGroupAsync(address,timeout,ct);
    private async Task<BusTelegram?> ReadGroupAsync(Knx.GroupAddress address, TimeSpan timeout, CancellationToken ct)
    {
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,_connectedLifetime.Token);
        return await (_tunnel??throw new InvalidOperationException("Read requires a tunnel.")).ReadGroupValueAsync(address,timeout,cancellation.Token).ConfigureAwait(false);
    }
    public async Task<T> WithDiagnosticsAsync<T>(Func<IBusDiagnosticSession,CancellationToken,Task<T>> action,CancellationToken ct=default)
    {
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct,_connectedLifetime.Token);
        await _diagnostics.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            var tunnel=_tunnel??throw new InvalidOperationException("Diagnostics require an explicit connected tunnel.");
            return await action(new TunnelDiagnosticSession(tunnel),cancellation.Token).ConfigureAwait(false);
        }
        finally { _diagnostics.Release(); }
    }
    public Task<DownloadResult> DownloadAsync(KnxDevice device, IndividualAddress deviceAddress, IApplicationProgram program,
        IProgress<DownloadProgress> progress, CancellationToken ct=default)
        => throw new NotSupportedException("Real device download is disabled by the read-only policy; use DownloadPreflight with an explicitly reviewed profile.");
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task pending;
        lock (_sync)
        {
            if (_closing is null) { _disconnecting = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _closing = completion.Task; }
            pending = _closing;
        }
        if (completion is not null)
        {
            try { _connecting?.Cancel(); _connectedLifetime.Cancel(); }
            catch (Exception error) when (error is AggregateException or ObjectDisposedException) { }
            finally { _ = FinishDisposeAsync(completion); }
        }
        return new(pending);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            await _diagnostics.WaitAsync().ConfigureAwait(false);
            try
            {
                if(_tunnel is {} tunnel) { tunnel.TelegramReceived-=OnTelegram; tunnel.Disconnected-=OnDisconnected; }
                try { if(_owned is not null)await _owned.DisposeAsync().ConfigureAwait(false); }
                finally { try { if(_operation is not null)await _operation.DisposeAsync().ConfigureAwait(false); }
                    finally { if(_routing is not null)await _routing.DisposeAsync().ConfigureAwait(false); } }
                _owned=null;_operation=null;_tunnel=null;_routing=null;DeviceName=null;
                NotifyState(BusConnectionState.Disconnected);
            }
            finally { _diagnostics.Release(); }
        }
        catch (Exception error) { failure = error; }
        finally { lock(_sync) { _disconnecting=false; _closing=null; } _transition.Release(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(new KnxConnectionException(KnxConnectionFailure.ConnectionFailed));
    }
}
