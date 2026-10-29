using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Knx.Bus.Security;

/// <summary>
/// A pinned-endpoint secure UDP session adapter for an ordinary KnxTunnel. Establish this session first, then
/// pass this adapter and its explicit LocalEndpoint to the tunnel. No plaintext fallback is permitted.
/// This owner disposes its underlying transport after all admitted operations and its keepalive have joined.
/// </summary>
public sealed class SecureSessionTransport : IUdpTransport, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IUdpTransport _transport;
    private readonly KnxIpSecureSession _session;
    private readonly IPEndPoint _gateway, _local;
    private IPEndPoint? _dataEndpoint;
    private readonly SemaphoreSlim _sending = new(1, 1), _receiving = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _handshakeTimeout, _keepaliveInterval;
    private Task? _keepalive, _disposal;
    private TaskCompletionSource _drained = CompleteDrain();
    private int _operations;
    private bool _connecting, _stopping;
    private Exception? _failure;
    public IPEndPoint LocalEndpoint => Copy(_local);
    public IPEndPoint GatewayEndpoint => Copy(_gateway);
    public bool IsConnected { get { lock (_gate) return !_stopping && _failure is null && _session.State == KnxIpSecureSessionState.Connected; } }

    public SecureSessionTransport(IUdpTransport transport, IPEndPoint gateway, IPEndPoint localEndpoint,
        KnxIpSecureSession session, TimeSpan? handshakeTimeout = null, TimeSpan? keepaliveInterval = null)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(localEndpoint); ArgumentNullException.ThrowIfNull(session);
        Endpoint(gateway); Endpoint(localEndpoint);
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _keepaliveInterval = keepaliveInterval ?? TimeSpan.FromSeconds(30);
        if (_handshakeTimeout <= TimeSpan.Zero || _handshakeTimeout > TimeSpan.FromSeconds(30)
            || _keepaliveInterval < TimeSpan.FromMilliseconds(10) || _keepaliveInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        _transport = transport; _gateway = Copy(gateway); _local = Copy(localEndpoint); _session = session;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using var operation = Admit(ct);
        lock (_gate)
        {
            if (_connecting || _session.State != KnxIpSecureSessionState.New) throw new InvalidOperationException("A secure session is single use.");
            _connecting = true;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token); timeout.CancelAfter(_handshakeTimeout);
        var receiving = false;
        try
        {
            await _receiving.WaitAsync(timeout.Token).ConfigureAwait(false); receiving = true;
            await SendWireAsync(_gateway, _session.CreateRequest(_local), timeout.Token).ConfigureAwait(false);
            byte[] authenticate;
            for (;;)
            {
                var result = await _transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!result.RemoteEndPoint.Equals(_gateway)) continue;
                if (result.Buffer.Length < 6 || result.Buffer[2] != 9 || result.Buffer[3] != 0x52) continue;
                authenticate = _session.AcceptResponse(result.Buffer); break;
            }
            await SendWireAsync(_gateway, authenticate, timeout.Token).ConfigureAwait(false);
            for (;;)
            {
                var result = await _transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!result.RemoteEndPoint.Equals(_gateway)) continue;
                _session.TryReceive(result.Buffer, out _);
                if (_session.State == KnxIpSecureSessionState.Failed) throw new AuthenticationException("Secure user authentication failed.");
                if (_session.State != KnxIpSecureSessionState.Connected) continue;
                lock (_gate)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    _keepalive = KeepaliveAsync();
                }
                return;
            }
        }
        catch (Exception ex)
        {
            MarkFailure(ex); _session.Dispose(); throw;
        }
        finally { if (receiving) _receiving.Release(); lock (_gate) _connecting = false; }
    }

    public async Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(data);
        using var operation = Admit(ct);
        lock (_gate)
        {
            if (_session.State != KnxIpSecureSessionState.Connected) throw new InvalidOperationException("The secure session is not authenticated.");
            if (!AllowedEndpoint(destination)) throw new InvalidOperationException("The destination is outside the authenticated secure session.");
        }
        await _sending.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var wrapped = _session.Wrap(data);
            await _transport.SendAsync(Copy(destination), wrapped, operation.Token).ConfigureAwait(false);
        }
        finally { _sending.Release(); }
    }

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
    {
        using var operation = Admit(ct);
        if (!IsConnected) throw new InvalidOperationException("The secure session is not authenticated.");
        await _receiving.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            for (;;)
            {
                var received = await _transport.ReceiveAsync(operation.Token).ConfigureAwait(false);
                lock (_gate) if (!AllowedEndpoint(received.RemoteEndPoint)) continue;
                if (!_session.TryReceive(received.Buffer, out var payload)) continue;
                if (_session.State == KnxIpSecureSessionState.Closed)
                { var error = new IOException("The gateway closed the secure session."); MarkFailure(error); throw error; }
                if (payload is null) continue;
                RememberDataEndpoint(payload);
                return new(payload, Copy(received.RemoteEndPoint));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _failure is not null)
        { throw new IOException("The secure session connection failed.", _failure); }
        finally { _receiving.Release(); }
    }

    private void RememberDataEndpoint(byte[] payload)
    {
        if (payload.Length < 6 || payload[2] != 2 || payload[3] != 6) return;
        try
        {
            var response = ConnectResponse.Parse(payload.AsSpan(6));
            if (!response.Succeeded || response.DataEndpoint is not { } endpoint) return;
            // A cryptographically authenticated CONNECT_RESPONSE may nominate only another port on this gateway.
            if (!endpoint.Address.Equals(_gateway.Address) || endpoint.Port == 0) return;
            lock (_gate) _dataEndpoint = Copy(endpoint);
        }
        catch (FormatException) { }
    }
    private async Task KeepaliveAsync()
    {
        try
        {
            for (;;)
            {
                await Task.Delay(_keepaliveInterval, _lifetime.Token).ConfigureAwait(false);
                using var operation = Admit(_lifetime.Token);
                await _sending.WaitAsync(operation.Token).ConfigureAwait(false);
                try
                {
                    operation.Token.ThrowIfCancellationRequested();
                    await _transport.SendAsync(_gateway, _session.CreateKeepalive(), operation.Token).ConfigureAwait(false);
                }
                finally { _sending.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stopping) { }
        catch (Exception ex) { MarkFailure(ex); }
    }
    private async Task SendWireAsync(IPEndPoint destination, byte[] frame, CancellationToken token)
    {
        await _sending.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); await _transport.SendAsync(destination, frame, token).ConfigureAwait(false); }
        finally { _sending.Release(); }
    }
    private void MarkFailure(Exception error)
    {
        lock (_gate) _failure ??= error;
        _lifetime.Cancel();
    }
    private Operation Admit(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_failure is not null) throw new IOException("The secure session connection failed.", _failure);
            if (_operations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return new(this, CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token)); }
            catch { if (--_operations == 0) _drained.TrySetResult(); throw; }
        }
    }
    private sealed class Operation(SecureSessionTransport owner, CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationToken Token => cancellation.Token;
        public void Dispose() { cancellation.Dispose(); lock (owner._gate) if (--owner._operations == 0) owner._drained.TrySetResult(); }
    }
    private bool AllowedEndpoint(IPEndPoint endpoint) => endpoint.Equals(_gateway) || _dataEndpoint is not null && endpoint.Equals(_dataEndpoint);
    private static void Endpoint(IPEndPoint endpoint)
    {
        var bytes = endpoint.Address.GetAddressBytes();
        if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork || bytes[0] is 0 or >= 224
            || endpoint.Port is < 1 or > 65535 || endpoint.Address.Equals(IPAddress.Broadcast)) throw new ArgumentException("An explicit unicast IPv4 endpoint and port are required.");
    }
    private static IPEndPoint Copy(IPEndPoint endpoint) => new(new IPAddress(endpoint.Address.GetAddressBytes()), endpoint.Port);
    private static TaskCompletionSource CompleteDrain() { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task task;
        lock (_gate)
        {
            if (_disposal is null) { _stopping = true; owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; }
            task = _disposal;
        }
        if (owner is not null) { _lifetime.Cancel(); _ = FinishDisposeAsync(owner); }
        return new(task);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            if (_keepalive is { } keepalive) await keepalive.ConfigureAwait(false);
            Task drained; lock (_gate) drained = _drained.Task;
            await drained.ConfigureAwait(false);
            if (_session.State == KnxIpSecureSessionState.Connected)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _transport.SendAsync(_gateway, _session.CreateClose(), closeTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { _transport.Dispose(); } catch (Exception ex) { failure ??= ex; }
            try { _session.Dispose(); } catch (Exception ex) { failure ??= ex; }
            _lifetime.Dispose();
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
