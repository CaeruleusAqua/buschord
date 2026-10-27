using System.Net;
using System.Net.Sockets;
using Knx;

namespace Knx.Bus;

/// <summary>Connection state of a KNXnet/IP link.</summary>
public enum BusConnectionState { Disconnected, Connecting, Connected, Closing, Closed }

/// <summary>A telegram observed on the bus, tagged with its transport and direction.</summary>
public sealed class BusTelegram
{
    public required Telegram Telegram { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Transport that delivered the telegram ("tunnel", "routing"); never a bus address.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// Matches the answer to a group value read: a GroupValueResponse for the requested group
/// address from a real device, never our own confirmation echo or an empty source.
/// </summary>
public static class GroupValueReadResponse
{
    public static bool Matches(BusTelegram telegram, Knx.GroupAddress address, IndividualAddress ownAddress)
    {
        var inner = telegram.Telegram;
        return inner.IsGroupAddress
               && inner.Apci == Apci.GroupValueResponse
               && inner.Destination.Raw == address.Raw
               && inner.Source != ownAddress
               && inner.Source != IndividualAddress.Zero;
    }
}

/// <summary>
/// KNXnet/IP tunnelling connection: CONNECT/CONNECTIONSTATE/DISCONNECT handshake,
/// TUNNELLING_REQUEST/ACK and both sequence counters, plus group value access and the
/// raw telegram feed. Wire layouts follow KNXnet/IP Core 3/8/2 and Tunnelling 3/8/4 and
/// were cross-checked against XKNX and Calimero; see <see cref="Connection"/> and
/// <see cref="Tunnelling"/>.
/// </summary>
public sealed class KnxTunnel : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(1); // tunnelling 3/8/4: repeat after 1 s
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(2);
    private const int HeartbeatAttempts = 3; // Core 3/8/2 §5.4

    private readonly IUdpTransport _transport;
    private readonly IPEndPoint _controlEndpoint;
    private readonly IndividualAddress _individualAddress;
    private readonly IPEndPoint? _configuredLocalEndpoint;
    private readonly TimeSpan _heartbeatInterval;
    private readonly object _gate = new();
    private readonly Dictionary<byte, TaskCompletionSource<byte>> _ackWaiters = new();

    private TaskCompletionSource<byte>? _stateWaiter;
    private TaskCompletionSource<byte>? _disconnectWaiter;
    private CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private IPEndPoint _localEndpoint = new(IPAddress.Any, 0);
    private IPEndPoint _dataEndpoint = new(IPAddress.Any, 0);
    private byte _sequence;
    private byte _expectedSequence;
    private bool _established;

    public KnxTunnel(IUdpTransport transport, IPEndPoint controlEndpoint, IndividualAddress individualAddress,
        IPEndPoint? localEndpoint = null, TimeSpan? heartbeatInterval = null)
    {
        _transport = transport;
        _controlEndpoint = controlEndpoint;
        _individualAddress = individualAddress;
        _configuredLocalEndpoint = localEndpoint;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(60);
    }

    public BusConnectionState State { get; private set; } = BusConnectionState.Disconnected;
    public byte Channel { get; private set; }

    /// <summary>Individual address the server assigned to this tunnel (source of our telegrams).</summary>
    public IndividualAddress AssignedAddress { get; private set; }

    /// <summary>Endpoints of the tunnelling data channel advertised by the server.</summary>
    public IPEndPoint DataEndpoint => _dataEndpoint;

    public event Action<BusTelegram>? TelegramReceived;

    /// <summary>
    /// L_Data.con confirmations (echoes of frames we sent). Kept out of
    /// <see cref="TelegramReceived"/> so a monitor shows bus traffic, not our own echo.
    /// </summary>
    public event Action<BusTelegram>? ConfirmationReceived;
    public event Action<string>? LogLine;
    public event Action? Disconnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State != BusConnectionState.Disconnected) throw new InvalidOperationException("already connected");
        State = BusConnectionState.Connecting;
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _localEndpoint = _configuredLocalEndpoint ?? ResolveLocalEndpoint();
        _dataEndpoint = _controlEndpoint;

        try
        {
            var request = KnxNetIpFrame.Encode(ServiceType.ConnectRequest,
                Connection.ConnectRequest(_localEndpoint, _localEndpoint));
            Log("-> " + Convert.ToHexString(request));
            await _transport.SendAsync(_controlEndpoint, request, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(ConnectTimeout);
            KnxNetIpFrame response;
            do
            {
                response = await ReceiveFrameAsync(timeout.Token);
                if (response.Service != ServiceType.ConnectResponse)
                    Log($"ignored {response.Service} while connecting");
            } while (response.Service != ServiceType.ConnectResponse);

            Log("<- " + Convert.ToHexString(KnxNetIpFrame.Encode(response.Service, response.Body)));
            var parsed = ConnectResponse.Parse(response.Body);
            if (!parsed.Succeeded)
                throw new IOException($"CONNECT_RESPONSE status 0x{parsed.Status:X2}");

            Channel = parsed.Channel;
            AssignedAddress = parsed.ConnectionData?.AssignedAddress ?? _individualAddress;
            if (parsed.DataEndpoint is { } server && !server.Address.Equals(IPAddress.Any) && server.Port != 0)
                _dataEndpoint = server;
            _sequence = 0;
            _expectedSequence = 0;
            _established = true;
            State = BusConnectionState.Connected;
            Log($"connected: channel {Channel}, address {AssignedAddress}, data endpoint {_dataEndpoint}");
        }
        catch
        {
            State = BusConnectionState.Disconnected;
            _established = false;
            throw;
        }

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    public Task GroupValueWriteAsync(Knx.GroupAddress address, byte[] data, int? dataBits = null,
        CancellationToken ct = default)
        => SendTelegramAsync(new Telegram
        {
            Source = AssignedAddress,
            Destination = new IndividualAddress(address.Raw),
            IsGroupAddress = true,
            Apci = Apci.GroupValueWrite,
            DataBits = dataBits,
            Data = data,
        }, ct);

    public Task GroupValueReadAsync(Knx.GroupAddress address, CancellationToken ct = default)
        => SendTelegramAsync(new Telegram
        {
            Source = AssignedAddress,
            Destination = new IndividualAddress(address.Raw),
            IsGroupAddress = true,
            Apci = Apci.GroupValueRead,
            Data = [],
        }, ct);

    public async Task SendTelegramAsync(Telegram telegram, CancellationToken ct = default)
        => await SendCemiAsync(Cemi.LDataRequest(telegram.WithSource(AssignedAddress)), ct);

    /// <summary>
    /// Sends one group value read and waits for the matching GroupValueResponse from a device.
    /// Returns null on timeout; the tunnel's own L_Data.con echo never satisfies the wait.
    /// </summary>
    public async Task<BusTelegram?> ReadGroupValueAsync(Knx.GroupAddress address, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<BusTelegram>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTelegram(BusTelegram telegram)
        {
            if (GroupValueReadResponse.Matches(telegram, address, AssignedAddress))
                completion.TrySetResult(telegram);
        }

        TelegramReceived += OnTelegram;
        try
        {
            await GroupValueReadAsync(address, ct);
            return await completion.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            TelegramReceived -= OnTelegram;
        }
    }

    /// <summary>Sends a telegram and awaits the matching response APCI from the device.</summary>
    public async Task<Telegram?> RequestAsync(byte[] cemi, Apci expectedResponse, IndividualAddress device,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<Telegram>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        void OnTelegram(BusTelegram t)
        {
            if (t.Telegram.Apci == expectedResponse && t.Telegram.Source == device)
                tcs.TrySetResult(t.Telegram);
        }
        TelegramReceived += OnTelegram;
        try
        {
            await SendCemiAsync(cemi, cts.Token);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            TelegramReceived -= OnTelegram;
        }
    }

    /// <summary>
    /// Sends one TUNNELLING_REQUEST and waits for its ACK. A missing ACK repeats the frame
    /// once with the same sequence counter; after that (or on a non-zero ACK status) the
    /// send fails. The sequence counter advances once per telegram, as the specification
    /// requires.
    /// </summary>
    private async Task SendCemiAsync(byte[] cemi, CancellationToken ct)
    {
        if (State != BusConnectionState.Connected)
            throw new InvalidOperationException($"tunnel is {State}, not connected");

        var sequence = _sequence;
        try
        {
            var frame = KnxNetIpFrame.Encode(ServiceType.TunnellingRequest,
                Tunnelling.Request(Channel, sequence, cemi));
            for (var attempt = 0; ; attempt++)
            {
                var waiter = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _ackWaiters[sequence] = waiter;
                try
                {
                    Log("-> " + Convert.ToHexString(frame));
                    await _transport.SendAsync(_dataEndpoint, frame, ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                    timeout.CancelAfter(AckTimeout);
                    byte status;
                    try
                    {
                        status = await waiter.Task.WaitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
                    {
                        if (attempt > 0) throw new TimeoutException($"no TUNNELLING_ACK for sequence {sequence}");
                        Log($"no ACK for sequence {sequence}, repeating once");
                        continue;
                    }
                    if (status != 0x00)
                        throw new IOException($"TUNNELLING_ACK status 0x{status:X2} for sequence {sequence}");
                    return;
                }
                finally
                {
                    lock (_gate) _ackWaiters.Remove(sequence);
                }
            }
        }
        finally
        {
            _sequence = (byte)(_sequence + 1);
        }
    }

    private IPEndPoint ResolveLocalEndpoint()
    {
        if (_transport is UdpTransport udp)
        {
            var bound = udp.LocalEndPoint;
            var address = bound.Address.Equals(IPAddress.Any)
                ? UdpTransport.ResolveLocalAddress(_controlEndpoint)
                : bound.Address;
            return new IPEndPoint(address, bound.Port);
        }
        // Unknown transport (loopback fake): advertise the any-address HPAI instead of lying.
        return new IPEndPoint(IPAddress.Any, 0);
    }

    private async Task<KnxNetIpFrame> ReceiveFrameAsync(CancellationToken ct)
    {
        var result = await _transport.ReceiveAsync(ct);
        return KnxNetIpFrame.Decode(result.Buffer);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _transport.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    // An ICMP port-unreachable must not kill the receive loop.
                    Log($"receive: {ex.SocketErrorCode}");
                    continue;
                }

                KnxNetIpFrame frame;
                try
                {
                    frame = KnxNetIpFrame.Decode(result.Buffer);
                }
                catch (FormatException ex)
                {
                    Log($"bad datagram from {result.RemoteEndPoint}: {ex.Message}");
                    continue;
                }

                try
                {
                    await HandleFrameAsync(frame);
                }
                catch (FormatException ex)
                {
                    Log($"malformed {frame.Service}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"receive loop: {ex.Message}");
            await CloseAsync();
        }
    }

    private async Task HandleFrameAsync(KnxNetIpFrame frame)
    {
        switch (frame.Service)
        {
            case ServiceType.TunnellingRequest:
            {
                var (channel, sequence, cemi) = Tunnelling.ReadRequest(frame.Body);
                Log("<- " + Convert.ToHexString(KnxNetIpFrame.Encode(frame.Service, frame.Body)));
                if (channel != Channel)
                {
                    Log($"ignored TUNNELLING_REQUEST for channel {channel} (ours {Channel})");
                    return;
                }
                if (sequence == _expectedSequence)
                {
                    _expectedSequence = (byte)(_expectedSequence + 1);
                    await SendTunnellingAckAsync(channel, sequence);
                    Dispatch(cemi);
                }
                else if (sequence == (byte)(_expectedSequence - 1))
                {
                    // Our previous ACK got lost: acknowledge again, but do not process twice.
                    Log($"discarded repeated TUNNELLING_REQUEST {sequence}");
                    await SendTunnellingAckAsync(channel, sequence);
                }
                else
                {
                    // Out of order: drop without an ACK so the server repeats the frame.
                    Log($"discarded TUNNELLING_REQUEST {sequence}, expected {_expectedSequence}");
                }
                return;
            }
            case ServiceType.TunnellingAck:
            {
                var (channel, sequence, status) = Tunnelling.ReadAck(frame.Body);
                Log($"<- TUNNELLING_ACK channel {channel} sequence {sequence} status 0x{status:X2}");
                if (channel != Channel)
                {
                    Log($"ignored TUNNELLING_ACK for channel {channel}");
                    return;
                }
                TaskCompletionSource<byte>? waiter;
                lock (_gate) _ackWaiters.TryGetValue(sequence, out waiter);
                if (waiter is null) Log($"unexpected TUNNELLING_ACK {sequence}");
                else waiter.TrySetResult(status);
                return;
            }
            case ServiceType.ConnectionStateResponse:
            {
                if (frame.Body.Length < 2) throw new FormatException("CONNECTIONSTATE_RESPONSE shorter than 2 bytes");
                Log($"<- CONNECTIONSTATE_RESPONSE status 0x{frame.Body[1]:X2}");
                lock (_gate) _stateWaiter?.TrySetResult(frame.Body[1]);
                return;
            }
            case ServiceType.DisconnectRequest:
            {
                if (frame.Body.Length < 1) throw new FormatException("DISCONNECT_REQUEST shorter than 1 byte");
                var channel = frame.Body[0];
                Log($"server sent DISCONNECT_REQUEST for channel {channel}");
                await _transport.SendAsync(_dataEndpoint,
                    KnxNetIpFrame.Encode(ServiceType.DisconnectResponse, Connection.Response(channel)));
                // The server already dropped the channel; do not send a DISCONNECT_REQUEST back.
                _established = false;
                await CloseAsync();
                return;
            }
            case ServiceType.DisconnectResponse:
            {
                if (frame.Body.Length < 2) throw new FormatException("DISCONNECT_RESPONSE shorter than 2 bytes");
                Log($"<- DISCONNECT_RESPONSE status 0x{frame.Body[1]:X2}");
                lock (_gate) _disconnectWaiter?.TrySetResult(frame.Body[1]);
                return;
            }
            default:
                Log($"<- {frame.Service}");
                return;
        }
    }

    private void Dispatch(byte[] cemi)
    {
        if (cemi.Length == 0) return;
        try
        {
            switch (cemi[0])
            {
                case (byte)CemiMessageCode.LDataInd:
                    TelegramReceived?.Invoke(new BusTelegram
                    {
                        Telegram = Cemi.ParseLData(cemi),
                        Source = "tunnel",
                    });
                    return;
                case (byte)CemiMessageCode.LDataCon:
                    // Our own frame was confirmed on the bus; a monitor must not show it as traffic.
                    ConfirmationReceived?.Invoke(new BusTelegram
                    {
                        Telegram = Cemi.ParseLData(cemi),
                        Source = "tunnel",
                    });
                    return;
                default:
                    Log($"ignored cEMI 0x{cemi[0]:X2}: {Convert.ToHexString(cemi)}");
                    return;
            }
        }
        catch (Exception ex)
        {
            Log($"bad cEMI {Convert.ToHexString(cemi)}: {ex.Message}");
        }
    }

    private Task SendTunnellingAckAsync(byte channel, byte sequence)
        => _transport.SendAsync(_dataEndpoint,
            KnxNetIpFrame.Encode(ServiceType.TunnellingAck, Tunnelling.Ack(channel, sequence)));

    /// <summary>Heartbeat: CONNECTIONSTATE_REQUEST, repeated three times before closing (Core §5.4).</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var ok = false;
                for (var attempt = 0; attempt < HeartbeatAttempts && !ok; attempt++)
                    ok = await ConnectionStateAsync(ct) == 0x00;
                if (ok) continue;
                Log("heartbeat failed, closing the tunnel");
                await CloseAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<byte?> ConnectionStateAsync(CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _stateWaiter = waiter;
        try
        {
            var frame = KnxNetIpFrame.Encode(ServiceType.ConnectionStateRequest,
                Connection.ConnectionStateRequest(Channel, _localEndpoint));
            Log("-> " + Convert.ToHexString(frame));
            await _transport.SendAsync(_dataEndpoint, frame, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            return await waiter.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_gate) _stateWaiter = null;
        }
    }

    public async Task CloseAsync()
    {
        if (State is BusConnectionState.Closing or BusConnectionState.Closed or BusConnectionState.Disconnected) return;
        State = BusConnectionState.Closing;

        if (_established)
        {
            _established = false;
            var waiter = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _disconnectWaiter = waiter;
            try
            {
                var frame = KnxNetIpFrame.Encode(ServiceType.DisconnectRequest,
                    Connection.DisconnectRequest(Channel, _localEndpoint));
                Log("-> " + Convert.ToHexString(frame));
                await _transport.SendAsync(_dataEndpoint, frame);
                using var timeout = new CancellationTokenSource(DisconnectTimeout);
                var status = await waiter.Task.WaitAsync(timeout.Token);
                Log($"disconnect acknowledged (status 0x{status:X2})");
            }
            catch (OperationCanceledException)
            {
                Log("no DISCONNECT_RESPONSE");
            }
            catch (Exception ex)
            {
                Log($"disconnect failed: {ex.Message}");
            }
            finally
            {
                lock (_gate) _disconnectWaiter = null;
            }
        }

        await _cts.CancelAsync();
        State = BusConnectionState.Disconnected;
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        await _cts.CancelAsync();
        _cts.Dispose();
        _transport.Dispose();
        State = BusConnectionState.Closed;
    }

    /// <summary>Logs every wire frame as hex; the first 6 bytes are the KNXnet/IP header.</summary>
    private void Log(string line)
    {
        try
        {
            LogLine?.Invoke(line);
        }
        catch
        {
            // A broken log handler must not break the protocol state machine.
        }
    }
}
