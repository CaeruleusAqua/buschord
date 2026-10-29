using System.Threading.Channels;
using Knx;

namespace Knx.Bus;

public interface IManagementTransport
{
    IndividualAddress AssignedAddress { get; }
    bool IsConnected { get; }
    event Action<ManagementTransportFrame>? FrameReceived;
    Task SendAsync(IndividualAddress target, byte[] tpdu, CancellationToken ct);
}

/// <summary>One addressed, connection-oriented management session. Failed requests invalidate the session; writes are never retried.</summary>
public sealed class KnxManagementClient : IAsyncDisposable
{
    private readonly IManagementTransport _transport;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly Channel<ManagementTransportFrame> _incoming = Channel.CreateBounded<ManagementTransportFrame>(128);
    private readonly Channel<byte[]> _responses = Channel.CreateBounded<byte[]>(32);
    private readonly Task _pump;
    private volatile TaskCompletionSource<byte>? _ack;
    private Exception? _fault;
    private byte _sendSequence, _receiveSequence;
    private bool _haveReceived, _connected, _disposed;

    public IndividualAddress Target { get; }
    public TimeSpan ResponseTimeout { get; }

    public KnxManagementClient(KnxTunnel tunnel, IndividualAddress target, TimeSpan? responseTimeout = null)
        : this(new TunnelTransport(tunnel), target, responseTimeout) { }

    public KnxManagementClient(IManagementTransport transport, IndividualAddress target, TimeSpan? responseTimeout = null)
    {
        if (target.Device is 0 or 255) throw new ArgumentOutOfRangeException(nameof(target));
        _transport = transport;
        Target = target;
        ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(3);
        if (ResponseTimeout <= TimeSpan.Zero || ResponseTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        _transport.FrameReceived += OnFrame;
        _pump = PumpAsync();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _requests.WaitAsync(ct);
        try
        {
            if (_disposed || _connected || _fault is not null) throw new InvalidOperationException("Management session cannot connect.");
            if (!_transport.IsConnected || _transport.AssignedAddress == Target)
                throw new InvalidOperationException("A connected tunnel with a distinct assigned address is required.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(ResponseTimeout);
            await _transport.SendAsync(Target, [0x80], deadline.Token);
            _connected = true;
        }
        finally { _requests.Release(); }
    }

    public async Task<byte[]> ReadDescriptorAsync(CancellationToken ct = default)
    {
        var response = await RequestAsync([0x03, 0], 0x340,
            a => a.Length == 4 && (a[1] & 0x3F) == 0, ct);
        return response[2..];
    }

    public async Task<byte[]> ReadMemoryAsync(ushort address, byte count, CancellationToken ct = default)
    {
        var request = ManagementApdu.ReadMemory(address, count);
        var response = await RequestAsync(request, ManagementApdu.MemoryResponse,
            a => a.Length >= 4 && a[2] == request[2] && a[3] == request[3]
                 && ((a[1] & 0x3F) == 0 || ((a[1] & 0x3F) == count && a.Length == 4 + count)), ct);
        if ((response[1] & 0x3F) == 0) throw new IOException("Device refused the memory read.");
        return response[4..];
    }

    public async Task<byte[]> ReadPropertyAsync(byte objectIndex, byte propertyId, ushort start = 1, byte count = 1,
        CancellationToken ct = default)
    {
        var request = ManagementApdu.ReadProperty(objectIndex, propertyId, start, count);
        var response = await PropertyRequestAsync(request, ct);
        if ((response[4] >> 4) == 0) throw new IOException($"Device refused property {objectIndex}/{propertyId}.");
        return response[6..];
    }

    public async Task<ManagementDeviceIdentity> ReadIdentityAsync(CancellationToken ct = default)
    {
        var descriptor = await ReadDescriptorAsync(ct);
        var manufacturer = await ReadPropertyAsync(0, 12, ct: ct);
        var serial = await ReadPropertyAsync(0, 11, ct: ct);
        var application = await ReadPropertyAsync(3, 13, ct: ct);
        if (descriptor.Length != 2 || manufacturer.Length != 2 || serial.Length != 6 || application.Length != 5)
            throw new IOException("Device identity has unsupported field lengths.");
        return new(Target.ToString(), (ushort)((descriptor[0] << 8) | descriptor[1]),
            Convert.ToHexString(manufacturer), Convert.ToHexString(serial), Convert.ToHexString(application));
    }

    internal async Task WriteProgrammingStatusAsync(byte value, bool serverVerify, CancellationToken ct)
    {
        byte[] request = [0x02, 0x81, 0, 0x60, value];
        await RequestAsync(request, serverVerify ? ManagementApdu.MemoryResponse : null,
            a => a.Length == 5 && (a[1] & 0x3F) == 1 && a[2] == 0 && a[3] == 0x60 && a[4] == value, ct);
    }

    private Task<byte[]> PropertyRequestAsync(byte[] request, CancellationToken ct)
        => RequestAsync(request, ManagementApdu.PropertyResponse, a => a.Length >= 6
            && a[2] == request[2] && a[3] == request[3] && a[5] == request[5]
            && (a[4] & 0x0F) == (request[4] & 0x0F)
            && (a[4] >> 4 == 0 || a[4] >> 4 == request[4] >> 4), ct);

    private async Task<byte[]> RequestAsync(byte[] apdu, int? responseService, Func<byte[], bool> matches, CancellationToken ct)
    {
        await _requests.WaitAsync(ct);
        try
        {
            if (_disposed || !_connected || _fault is not null) throw new IOException("Management session is not connected.", _fault);
            if (!_transport.IsConnected) throw new IOException("Tunnel is no longer connected.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(ResponseTimeout);
            var ack = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ack = ack;
            apdu = (byte[])apdu.Clone();
            apdu[0] = (byte)((apdu[0] & 3) | 0x40 | (_sendSequence << 2));
            await _transport.SendAsync(Target, apdu, deadline.Token);
            await ack.Task.WaitAsync(deadline.Token);
            _sendSequence = (byte)((_sendSequence + 1) & 15);
            _ack = null;
            if (responseService is null) return [];
            while (true)
            {
                var response = await _responses.Reader.ReadAsync(deadline.Token);
                if (ManagementApdu.Service(response) == responseService && matches(response)) return response;
            }
        }
        catch (Exception ex) when (_connected)
        {
            await DisconnectTransportAsync();
            Fail(ex);
            throw;
        }
        finally { _ack = null; _requests.Release(); }
    }

    private void OnFrame(ManagementTransportFrame frame)
    {
        if (frame.Source != Target || frame.Destination != _transport.AssignedAddress || _disposed) return;
        if (!_incoming.Writer.TryWrite(frame)) Fail(new IOException("Management receive queue overflow."));
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var frame in _incoming.Reader.ReadAllAsync(_lifetime.Token))
            {
                var tpdu = frame.Tpdu;
                if (tpdu.Length == 0) continue;
                if (tpdu.Length == 1)
                {
                    if (tpdu[0] == 0x81) throw new IOException("Device disconnected its management session.");
                    if ((tpdu[0] & 0xC3) == 0xC2 && ((tpdu[0] >> 2) & 15) == _sendSequence)
                        _ack?.TrySetResult(tpdu[0]);
                    else if ((tpdu[0] & 0xC3) == 0xC3 && ((tpdu[0] >> 2) & 15) == _sendSequence)
                        throw new IOException("Device rejected the management sequence (T_NAK).");
                    continue;
                }
                if ((tpdu[0] & 0xC0) != 0x40) continue;
                var sequence = (byte)((tpdu[0] >> 2) & 15);
                var duplicate = _haveReceived && sequence == ((_receiveSequence + 15) & 15);
                if (sequence != _receiveSequence && !duplicate) throw new IOException("Unexpected management receive sequence.");
                await _transport.SendAsync(Target, [(byte)(0xC2 | (sequence << 2))], _lifetime.Token);
                if (duplicate) continue;
                _receiveSequence = (byte)((_receiveSequence + 1) & 15);
                _haveReceived = true;
                if (!_responses.Writer.TryWrite(tpdu)) throw new IOException("Management response queue overflow.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        _fault ??= ex;
        _connected = false;
        _ack?.TrySetException(ex);
        _incoming.Writer.TryComplete(ex);
        _responses.Writer.TryComplete(ex);
        _lifetime.Cancel();
    }

    private async Task DisconnectTransportAsync()
    {
        if (!_connected || !_transport.IsConnected) return;
        _connected = false;
        using var deadline = new CancellationTokenSource(ResponseTimeout);
        try { await _transport.SendAsync(Target, [0x81], deadline.Token); }
        catch (Exception) { /* Cleanup cannot confirm an outstanding operation. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.FrameReceived -= OnFrame;
        await DisconnectTransportAsync();
        await _lifetime.CancelAsync();
        _incoming.Writer.TryComplete();
        await _pump;
        _lifetime.Dispose();
    }

    private sealed class TunnelTransport(KnxTunnel tunnel) : IManagementTransport
    {
        public IndividualAddress AssignedAddress => tunnel.AssignedAddress;
        public bool IsConnected => tunnel.State == BusConnectionState.Connected;
        public event Action<ManagementTransportFrame>? FrameReceived
        {
            add => tunnel.ManagementFrameReceived += value;
            remove => tunnel.ManagementFrameReceived -= value;
        }
        public Task SendAsync(IndividualAddress target, byte[] tpdu, CancellationToken ct)
            => tunnel.SendManagementTransportAsync(target, tpdu, ct);
    }
}
