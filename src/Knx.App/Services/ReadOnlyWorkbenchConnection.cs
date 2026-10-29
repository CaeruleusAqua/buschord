using System.Net;
using System.Net.Sockets;
using Knx.Bus;

namespace Knx.App.Workbench;

public sealed class ReadOnlyConnectionOptions
{
    private readonly byte[] _address;
    public int Port { get; }
    public IndividualAddress RequestedAddress { get; }
    public IPEndPoint Gateway => new(new IPAddress(_address), Port);
    public string EndpointId => $"knx-ip:{new IPAddress(_address)}:{Port}";
    public ReadOnlyConnectionOptions(IPAddress address, int port, IndividualAddress requestedAddress)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != AddressFamily.InterNetwork || bytes[0] is 0 or >= 224
            || address.Equals(IPAddress.Broadcast) || port is < 1 or > 65535 || requestedAddress.Device is 0 or 255)
            throw new ArgumentException("Invalid explicit IPv4 tunnel endpoint or requested individual address.");
        _address = bytes.ToArray(); Port = port; RequestedAddress = requestedAddress;
    }
}

public enum ReadOnlyConnectionPhase { Disconnected, Connecting, Connected, Disconnecting, Failed }
public enum ReadOnlyConnectionIssue { None, Cancelled, LinkLost, Occupied, ConnectFailed, CleanupFailed, StaleProject }
public sealed record ReadOnlyConnectionSnapshot(ReadOnlyConnectionPhase Phase, ReadOnlyConnectionIssue Issue,
    string? EndpointId, IndividualAddress? AssignedAddress, long Generation)
{
    public bool IsReadOnly => true;
}

/// <summary>A receive-only adapter. Disposal must finish local I/O shutdown before returning.</summary>
public interface IReadOnlyWorkbenchBackend : IMonitorTelegramSource, IMonitorConnectionNotifications, IAsyncDisposable
{
    IndividualAddress AssignedAddress { get; }
    Task ConnectAsync(CancellationToken ct);
}

/// <summary>Process-wide reservations for explicit real endpoints; released only after the previous owner joined cleanup.</summary>
public static class RealEndpointReservations
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Active = new(StringComparer.Ordinal);
    public static IDisposable? TryAcquire(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (Gate) return Active.Add(endpointId) ? new Reservation(endpointId) : null;
    }
    private sealed class Reservation(string id) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { lock (Gate) { if (_disposed) return; _disposed = true; Active.Remove(id); } }
    }
}

/// <summary>
/// One project lifetime's explicit, receive-only tunnel owner. No source is opened by construction or monitor subscription.
/// All network operations are serialized and cancellation is joined before another owner may reserve the endpoint.
/// </summary>
public sealed class ReadOnlyWorkbenchConnection : IMonitorTelegramSource, IMonitorConnectionNotifications, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _projectLifetime;
    private readonly Func<object> _currentProjectLifetime;
    private readonly Func<ReadOnlyConnectionOptions, IReadOnlyWorkbenchBackend> _factory;
    private IReadOnlyWorkbenchBackend? _backend;
    private IDisposable? _endpointLease;
    private CancellationTokenSource? _connecting;
    private Action<BusTelegram>? _receive;
    private Action<BusConnectionState>? _state;
    private bool _disposed;
    private Task? _disposal;
    private long _generation;
    private ReadOnlyConnectionSnapshot _snapshot = new(ReadOnlyConnectionPhase.Disconnected, ReadOnlyConnectionIssue.None, null, null, 0);

    public ReadOnlyWorkbenchConnection(object projectLifetime, Func<object> currentProjectLifetime,
        Func<ReadOnlyConnectionOptions, IReadOnlyWorkbenchBackend>? factory = null)
    {
        _projectLifetime = projectLifetime ?? throw new ArgumentNullException(nameof(projectLifetime));
        _currentProjectLifetime = currentProjectLifetime ?? throw new ArgumentNullException(nameof(currentProjectLifetime));
        _factory = factory ?? (options => new TunnelBackend(options));
    }
    public ReadOnlyConnectionSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public bool IsConnected => Snapshot.Phase == ReadOnlyConnectionPhase.Connected && IsCurrentProject();
    public event Action<BusTelegram>? TelegramReceived;
    public event Action<BusConnectionState>? StateChanged;
    public event Action<ReadOnlyConnectionSnapshot>? Changed;
    private bool IsCurrentProject() => ReferenceEquals(_projectLifetime, _currentProjectLifetime());

    public Task ConnectAsync(ReadOnlyConnectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        CancellationTokenSource source; long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connecting is not null || _backend is not null || _snapshot.Phase == ReadOnlyConnectionPhase.Disconnecting)
                throw new InvalidOperationException("Connection owner is busy.");
            if (!IsCurrentProject()) throw new InvalidOperationException("Project lifetime changed.");
            source = _connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            source.CancelAfter(TimeSpan.FromSeconds(15)); generation = ++_generation;
        }
        return ConnectCoreAsync(options, source, generation);
    }

    private async Task ConnectCoreAsync(ReadOnlyConnectionOptions options, CancellationTokenSource source, long generation)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            source.Token.ThrowIfCancellationRequested();
            Publish(ReadOnlyConnectionPhase.Connecting, ReadOnlyConnectionIssue.None, options.EndpointId, null, generation);
            _endpointLease = RealEndpointReservations.TryAcquire(options.EndpointId)
                ?? throw new EndpointOccupiedException();
            var backend = _factory(options) ?? throw new InvalidOperationException("No backend.");
            lock (_sync) _backend = backend;
            _receive = telegram =>
            {
                lock (_sync)
                    if (_disposed || generation != _generation || _snapshot.Phase != ReadOnlyConnectionPhase.Connected || !IsCurrentProject()) return;
                Notify(TelegramReceived, telegram);
            };
            _state = state =>
            {
                if (state is not (BusConnectionState.Disconnected or BusConnectionState.Closed)) return;
                lock (_sync) if (_disposed || generation != _generation || !ReferenceEquals(_backend, backend)) return;
                _ = ObserveRemoteCloseAsync();
            };
            backend.TelegramReceived += _receive; backend.StateChanged += _state;
            await backend.ConnectAsync(source.Token).ConfigureAwait(false);
            source.Token.ThrowIfCancellationRequested();
            lock (_sync)
                if (generation != _generation || _disposed || !IsCurrentProject()) throw new OperationCanceledException("Stale connection.");
            if (!backend.IsConnected) throw new IOException("Tunnel did not connect.");
            Publish(ReadOnlyConnectionPhase.Connected, ReadOnlyConnectionIssue.None, options.EndpointId, backend.AssignedAddress, generation);
        }
        catch (Exception ex)
        {
            var issue = ex is EndpointOccupiedException ? ReadOnlyConnectionIssue.Occupied
                : ex is OperationCanceledException ? ReadOnlyConnectionIssue.Cancelled : ReadOnlyConnectionIssue.ConnectFailed;
            try { await CleanupAsync().ConfigureAwait(false); }
            catch { issue = ReadOnlyConnectionIssue.CleanupFailed; }
            Publish(ReadOnlyConnectionPhase.Failed, issue, options.EndpointId, null, generation);
            throw;
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_connecting, source)) _connecting = null; }
            source.Dispose(); _serial.Release();
        }
    }

    /// <summary>Invalidates callbacks immediately, then joins any pending connect and local shutdown.</summary>
    public Task DisconnectAsync() => DisconnectCoreAsync(ReadOnlyConnectionIssue.None);
    private async Task DisconnectCoreAsync(ReadOnlyConnectionIssue issue)
    {
        long generation; string? endpoint;
        lock (_sync) { _connecting?.Cancel(); generation = ++_generation; endpoint = _snapshot.EndpointId; }
        Publish(ReadOnlyConnectionPhase.Disconnecting, issue, endpoint, null, generation);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupAsync().ConfigureAwait(false);
            Publish(ReadOnlyConnectionPhase.Disconnected, issue, endpoint, null, generation);
        }
        catch
        {
            Publish(ReadOnlyConnectionPhase.Failed, ReadOnlyConnectionIssue.CleanupFailed, endpoint, null, generation);
            throw;
        }
        finally { _serial.Release(); }
    }
    private async Task ObserveRemoteCloseAsync()
    {
        try { await DisconnectCoreAsync(ReadOnlyConnectionIssue.LinkLost).ConfigureAwait(false); }
        catch { /* Snapshot retains cleanup failure and its reservation; explicit close retries cleanup. */ }
    }
    private async Task CleanupAsync()
    {
        var backend = _backend;
        if (backend is not null)
        {
            if (_receive is not null) backend.TelegramReceived -= _receive;
            if (_state is not null) backend.StateChanged -= _state;
            await backend.DisposeAsync().ConfigureAwait(false);
            lock (_sync) if (ReferenceEquals(_backend, backend)) _backend = null;
        }
        _receive = null; _state = null;
        _endpointLease?.Dispose(); _endpointLease = null;
    }
    private void Publish(ReadOnlyConnectionPhase phase, ReadOnlyConnectionIssue issue, string? endpoint, IndividualAddress? assigned, long generation)
    {
        ReadOnlyConnectionSnapshot snapshot;
        lock (_sync)
        {
            if (generation != _generation) return;
            snapshot = _snapshot = new(phase, issue, endpoint, assigned, generation);
        }
        Notify(Changed, snapshot);
        Notify(StateChanged, phase == ReadOnlyConnectionPhase.Connected ? BusConnectionState.Connected
            : phase == ReadOnlyConnectionPhase.Connecting ? BusConnectionState.Connecting
            : phase == ReadOnlyConnectionPhase.Disconnecting ? BusConnectionState.Closing : BusConnectionState.Disconnected);
    }
    private static void Notify<T>(Action<T>? callbacks, T value)
    {
        if (callbacks is null) return;
        foreach (Action<T> callback in callbacks.GetInvocationList())
            try { callback(value); } catch { /* A view cannot interrupt transport shutdown. */ }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            if (_disposal?.IsFaulted == true) _disposal = null;
            return new(_disposal ??= DisconnectCoreAsync(ReadOnlyConnectionIssue.None));
        }
    }
    private sealed class EndpointOccupiedException : InvalidOperationException;

    private sealed class TunnelBackend(ReadOnlyConnectionOptions options) : IReadOnlyWorkbenchBackend
    {
        private KnxTunnel? _tunnel;
        private UdpTransport? _transport;
        public bool IsConnected => _tunnel?.State == BusConnectionState.Connected;
        public IndividualAddress AssignedAddress => _tunnel?.AssignedAddress ?? default;
        public event Action<BusTelegram>? TelegramReceived;
        public event Action<BusConnectionState>? StateChanged;
        public async Task ConnectAsync(CancellationToken ct)
        {
            _transport = new UdpTransport(0);
            _tunnel = new(_transport, options.Gateway, options.RequestedAddress);
            _tunnel.TelegramReceived += OnTelegram;
            _tunnel.Disconnected += OnDisconnected;
            await _tunnel.ConnectAsync(ct).ConfigureAwait(false);
        }
        private void OnTelegram(BusTelegram telegram) => TelegramReceived?.Invoke(telegram);
        private void OnDisconnected() => StateChanged?.Invoke(BusConnectionState.Disconnected);
        public async ValueTask DisposeAsync()
        {
            if (_tunnel is { } tunnel)
            {
                tunnel.TelegramReceived -= OnTelegram; tunnel.Disconnected -= OnDisconnected;
                try { await tunnel.DisposeAsync().ConfigureAwait(false); }
                finally { _transport?.Dispose(); _transport = null; _tunnel = null; }
            }
            else { _transport?.Dispose(); _transport = null; }
        }
    }
}
