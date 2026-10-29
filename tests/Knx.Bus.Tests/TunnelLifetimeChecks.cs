using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Knx;
using Knx.Bus;

/// <summary>Deterministic shutdown checks: cancellation alone is not evidence that an owner has drained.</summary>
internal static class TunnelLifetimeChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("concurrent close and dispose join blocked receive unwind", ConcurrentDrainAsync),
            ("remote disconnect drains without awaiting its receive task", RemoteDisconnectAsync),
            ("negative heartbeat status drains both loops", () => HeartbeatAsync(false)),
            ("heartbeat transport failure drains both loops", () => HeartbeatAsync(true)),
            ("close cancels running sends, queued sends and response waiters", PendingOperationsAsync),
            ("an admitted send cancels while shutdown callbacks are still pending", ClosingAdmissionAsync),
            ("close signalled by a log observer prevents the transport send", CloseBeforeSendAsync),
            ("close drains a pending connect transport send", ConnectingAsync),
            ("disconnect send is bounded and drained before disposal", DisconnectSendAsync),
            ("throwing observers cannot prevent cleanup or other observers", BrokenObserversAsync),
            ("close is retryable and disposal is idempotent", ReconnectAsync),
            ("real loopback UDP endpoint is released after disposal", UdpLoopbackAsync),
        })
        {
            try { await run().WaitAsync(Deadline); Console.WriteLine("PASS TunnelLifetime " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL TunnelLifetime " + name + ": " + ex); }
        }
        return failures;
    }

    private static async Task ConcurrentDrainAsync()
    {
        var transport = new ControlledTransport { HoldReceiveUnwind = true, ReturnFrameAfterCancellation = true };
        var tunnel = await ConnectAsync(transport);
        var callbacks = 0;
        tunnel.TelegramReceived += _ => Interlocked.Increment(ref callbacks);
        var closing = tunnel.CloseAsync();
        await transport.ReceiveCancelled.Task.WaitAsync(Deadline);
        var disposals = Enumerable.Range(0, 12).Select(_ => tunnel.DisposeAsync().AsTask()).ToArray();
        Require(ReferenceEquals(closing, tunnel.CloseAsync()), "Close callers did not share completion.");
        Require(disposals.All(t => ReferenceEquals(t, disposals[0])), "Dispose callers did not share completion.");
        Require(!closing.IsCompleted && disposals.All(t => !t.IsCompleted) && transport.Disposals == 0,
            "Shutdown completed or disposed transport before receive unwind.");
        transport.ReleaseReceive.TrySetResult();
        await Task.WhenAll(disposals).WaitAsync(Deadline);
        await closing;
        Require(tunnel.State == BusConnectionState.Closed && transport.Disposals == 1 && !transport.DisposedWhileActive,
            "Shutdown did not join before resource disposal.");
        Require(callbacks == 0, "A cancelled receive dispatched a late telegram.");
        transport.Push(ServiceType.TunnellingRequest, Tunnelling.Request(7, 0, Indication()));
        await Task.Delay(25);
        Require(callbacks == 0 && transport.AccessAfterDisposal == 0, "Callbacks or transport access continued after disposal.");
        await tunnel.CloseAsync(); await tunnel.DisposeAsync();
        Require(transport.Disposals == 1, "Repeated disposal released transport twice.");
    }

    private static async Task RemoteDisconnectAsync()
    {
        var transport = new ControlledTransport { HoldReceiveUnwind = true };
        var tunnel = await ConnectAsync(transport);
        transport.Push(ServiceType.DisconnectRequest, Connection.DisconnectRequest(7, transport.Endpoint));
        await transport.RemoteResponse.Task.WaitAsync(Deadline);
        await transport.ReceiveCancelled.Task.WaitAsync(Deadline);
        var closing = tunnel.CloseAsync();
        Require(!closing.IsCompleted && transport.DisconnectRequests == 0, "Remote close self-joined or sent another disconnect request.");
        transport.ReleaseReceive.TrySetResult();
        await closing.WaitAsync(Deadline);
        await tunnel.DisposeAsync();
        Require(transport.RemoteResponses == 1 && !transport.DisposedWhileActive, "Remote disconnect did not drain cleanly.");
    }

    private static async Task HeartbeatAsync(bool sendFailure)
    {
        var transport = new ControlledTransport { HoldReceiveUnwind = true, FailHeartbeatSend = sendFailure, HeartbeatStatus = 0x21 };
        var tunnel = await ConnectAsync(transport, TimeSpan.FromMilliseconds(10));
        await transport.ReceiveCancelled.Task.WaitAsync(Deadline);
        var dispose = tunnel.DisposeAsync().AsTask();
        Require(!dispose.IsCompleted && transport.Disposals == 0, "Heartbeat close did not wait for receive unwind.");
        transport.ReleaseReceive.TrySetResult();
        await dispose.WaitAsync(Deadline);
        Require(transport.Heartbeats == (sendFailure ? 1 : 3) && !transport.DisposedWhileActive,
            "Heartbeat failure did not stop and join its owner.");
    }

    private static async Task PendingOperationsAsync()
    {
        var transport = new ControlledTransport { HoldDataSend = true };
        var tunnel = await ConnectAsync(transport);
        var send = tunnel.GroupValueReadAsync(new GroupAddress(1));
        await transport.DataSendEntered.Task.WaitAsync(Deadline);
        var queued = tunnel.GroupValueReadAsync(new GroupAddress(2));
        var response = tunnel.ReadGroupValueAsync(new GroupAddress(3), TimeSpan.FromMinutes(1));
        var request = tunnel.RequestAsync(Cemi.LDataRequest(new Telegram
        {
            Source = new(1, 1, 250), Destination = new(1, 1, 2), Apci = Apci.MemoryRead, Data = [],
        }), Apci.MemoryResponse, new(1, 1, 2), TimeSpan.FromMinutes(1));
        var close = tunnel.CloseAsync();
        await transport.DataSendCancelled.Task.WaitAsync(Deadline);
        Require(!close.IsCompleted && transport.Disposals == 0, "Close returned while transport send was unwinding.");
        transport.ReleaseDataSend.TrySetResult();
        await close.WaitAsync(Deadline);
        await IgnoreCancellationAsync(send); await IgnoreCancellationAsync(queued);
        await IgnoreCancellationAsync(response); await IgnoreCancellationAsync(request);
        Require(transport.DataSends == 1, "A queued operation sent after close began.");
        await tunnel.DisposeAsync();
        Require(!transport.DisposedWhileActive && transport.AccessAfterDisposal == 0, "Send outlived disposed resources.");

        // Also cancel readers which have already sent and are now waiting for a bus response.
        transport = new ControlledTransport(); tunnel = await ConnectAsync(transport);
        var waitingRead = tunnel.ReadGroupValueAsync(new GroupAddress(4), TimeSpan.FromMinutes(1));
        await transport.FirstDataAck.Task.WaitAsync(Deadline);
        var waitingRequest = tunnel.RequestAsync(Cemi.LDataRequest(new Telegram
        {
            Source = new(1, 1, 250), Destination = new(1, 1, 2), Apci = Apci.MemoryRead, Data = [],
        }), Apci.MemoryResponse, new(1, 1, 2), TimeSpan.FromMinutes(1));
        await UntilAsync(() => transport.DataSends == 2);
        await tunnel.DisposeAsync().AsTask().WaitAsync(Deadline);
        await IgnoreCancellationAsync(waitingRead); await IgnoreCancellationAsync(waitingRequest);
    }

    private static async Task ClosingAdmissionAsync()
    {
        var transport = new ControlledTransport(); var tunnel = await ConnectAsync(transport);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = (SemaphoreSlim)typeof(KnxTunnel).GetField("_sendGate", flags)!.GetValue(tunnel)!;
        var lifetime = (CancellationTokenSource)typeof(KnxTunnel).GetField("_operationsCts", flags)!.GetValue(tunnel)!;
        await gate.WaitAsync();
        var send = tunnel.GroupValueReadAsync(new GroupAddress(1));
        using var releaseCancellation = new ManualResetEventSlim();
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocker = lifetime.Token.Register(() => { cancelling.TrySetResult(); releaseCancellation.Wait(Deadline); });
        var close = tunnel.CloseAsync(); var released = false; Exception? error = null;
        try
        {
            await cancelling.Task.WaitAsync(Deadline);
            gate.Release(); released = true;
            try { await send.WaitAsync(Deadline); } catch (Exception ex) { error = ex; }
        }
        finally
        {
            if (!released) gate.Release();
            releaseCancellation.Set();
            await tunnel.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        await close;
        Require(error is OperationCanceledException && transport.DataSends == 0,
            "An admitted send did not cancel before transport transmission: " + error?.GetType().Name);
    }

    private static async Task CloseBeforeSendAsync()
    {
        var transport = new ControlledTransport(); var tunnel = await ConnectAsync(transport);
        tunnel.LogLine += line => { if (line.StartsWith("-> 06100420", StringComparison.Ordinal)) _ = tunnel.CloseAsync(); };
        await IgnoreCancellationAsync(tunnel.GroupValueReadAsync(new GroupAddress(1)));
        await tunnel.DisposeAsync();
        Require(transport.DataSends == 0 && !transport.DisposedWhileActive, "The transport was invoked after a close signal.");
    }

    private static async Task ConnectingAsync()
    {
        var transport = new ControlledTransport { HoldConnectSend = true };
        var tunnel = Create(transport);
        var connecting = tunnel.ConnectAsync();
        await transport.ConnectSendEntered.Task.WaitAsync(Deadline);
        var dispose = tunnel.DisposeAsync().AsTask();
        await transport.ConnectSendCancelled.Task.WaitAsync(Deadline);
        Require(!dispose.IsCompleted && transport.Disposals == 0, "Connecting send was disposed before unwind.");
        transport.ReleaseConnectSend.TrySetResult();
        await IgnoreCancellationAsync(connecting);
        await dispose.WaitAsync(Deadline);
        Require(tunnel.State == BusConnectionState.Closed && !transport.DisposedWhileActive, "Connect published work after disposal.");
    }

    private static async Task BrokenObserversAsync()
    {
        var transport = new ControlledTransport(); var tunnel = Create(transport);
        var messages = 0; var disconnected = 0;
        tunnel.LogLine += _ => throw new InvalidOperationException("broken log observer");
        tunnel.TelegramReceived += _ => throw new InvalidOperationException("broken telegram observer");
        tunnel.TelegramReceived += _ => Interlocked.Increment(ref messages);
        tunnel.Disconnected += () => throw new InvalidOperationException("broken disconnect observer");
        tunnel.Disconnected += () => Interlocked.Increment(ref disconnected);
        await tunnel.ConnectAsync();
        transport.Push(ServiceType.TunnellingRequest, Tunnelling.Request(7, 0, Indication()));
        await UntilAsync(() => Volatile.Read(ref messages) == 1);
        await tunnel.DisposeAsync();
        Require(disconnected == 1 && transport.Disposals == 1, "A broken observer prevented another observer or cleanup.");
    }

    private static async Task DisconnectSendAsync()
    {
        var transport = new ControlledTransport { HoldDisconnectSend = true };
        var tunnel = await ConnectAsync(transport);
        var disposing = tunnel.DisposeAsync().AsTask();
        await transport.DisconnectSendCancelled.Task.WaitAsync(Deadline);
        Require(!disposing.IsCompleted && transport.Disposals == 0, "Disconnect timeout abandoned its transport send.");
        transport.ReleaseDisconnectSend.TrySetResult();
        await disposing.WaitAsync(Deadline);
        Require(!transport.DisposedWhileActive && transport.Disposals == 1, "Timed out disconnect did not finish before disposal.");
    }

    private static async Task ReconnectAsync()
    {
        var transport = new ControlledTransport(); var tunnel = await ConnectAsync(transport);
        await tunnel.CloseAsync();
        Require(tunnel.State == BusConnectionState.Disconnected && transport.Disposals == 0, "Close unexpectedly disposed the reusable transport.");
        await tunnel.ConnectAsync();
        await tunnel.GroupValueReadAsync(new GroupAddress(1));
        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync(); await tunnel.CloseAsync();
        var refused = false;
        try { await tunnel.ConnectAsync(); } catch (InvalidOperationException) { refused = true; }
        Require(refused && transport.Disposals == 1 && !transport.DisposedWhileActive, "Disposed tunnel reopened or double-disposed resources.");
    }

    private static async Task UdpLoopbackAsync()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource();
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var transport = new UdpTransport(IPAddress.Loopback, 0);
        var local = transport.LocalEndPoint;
        var serverLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await server.ReceiveAsync(stop.Token);
                    var frame = KnxNetIpFrame.Decode(packet.Buffer);
                    var response = frame.Service switch
                    {
                        ServiceType.ConnectRequest => KnxNetIpFrame.Encode(ServiceType.ConnectResponse,
                            [7, 0, .. Hpai.Data(endpoint), .. Crd.Tunnel(new IndividualAddress(1, 1, 250))]),
                        ServiceType.DisconnectRequest => KnxNetIpFrame.Encode(ServiceType.DisconnectResponse, Connection.Response(7)),
                        _ => null,
                    };
                    if (response is not null) await server.SendAsync(response, packet.RemoteEndPoint, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        var tunnel = new KnxTunnel(transport, endpoint, new(1, 1, 250));
        try
        {
            await tunnel.ConnectAsync();
            await tunnel.DisposeAsync();
            using var rebound = new UdpClient(local);
            Require(tunnel.State == BusConnectionState.Closed, "UDP endpoint was released before final state.");
        }
        finally { await tunnel.DisposeAsync(); await stop.CancelAsync(); await serverLoop; }
    }

    private static KnxTunnel Create(ControlledTransport transport, TimeSpan? heartbeat = null)
        => new(transport, transport.Endpoint, new(1, 1, 250), heartbeatInterval: heartbeat);

    private static async Task<KnxTunnel> ConnectAsync(ControlledTransport transport, TimeSpan? heartbeat = null)
    {
        var tunnel = Create(transport, heartbeat); await tunnel.ConnectAsync();
        await transport.ReceiveEntered.Task.WaitAsync(Deadline); return tunnel;
    }

    private static byte[] Indication() => Convert.FromHexString("2900BCD011050902010041");
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static async Task UntilAsync(Func<bool> condition)
    { while (!condition()) await Task.Delay(5); }
    private static async Task IgnoreCancellationAsync(Task task)
    { try { await task.WaitAsync(Deadline); } catch (OperationCanceledException) { } }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledTransport : IUdpTransport
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private int _receives, _sends, _receiveCalls, _disposed;
        internal readonly IPEndPoint Endpoint = new(IPAddress.Loopback, 3671);
        internal readonly TaskCompletionSource ReceiveEntered = Signal(), ReceiveCancelled = Signal(), ReleaseReceive = Signal();
        internal readonly TaskCompletionSource DataSendEntered = Signal(), DataSendCancelled = Signal(), ReleaseDataSend = Signal();
        internal readonly TaskCompletionSource ConnectSendEntered = Signal(), ConnectSendCancelled = Signal(), ReleaseConnectSend = Signal();
        internal readonly TaskCompletionSource DisconnectSendEntered = Signal(), DisconnectSendCancelled = Signal(), ReleaseDisconnectSend = Signal();
        internal readonly TaskCompletionSource RemoteResponse = Signal(), FirstDataAck = Signal();
        internal bool HoldReceiveUnwind, HoldDataSend, HoldConnectSend, HoldDisconnectSend, ReturnFrameAfterCancellation, FailHeartbeatSend;
        internal byte HeartbeatStatus;
        internal int Disposals, AccessAfterDisposal, DataSends, DisconnectRequests, RemoteResponses, Heartbeats;
        internal bool DisposedWhileActive;

        public async Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        {
            Access(); Interlocked.Increment(ref _sends);
            try
            {
                ct.ThrowIfCancellationRequested();
                var frame = KnxNetIpFrame.Decode(data);
                switch (frame.Service)
                {
                    case ServiceType.ConnectRequest:
                        if (HoldConnectSend) await HoldSendAsync(ConnectSendEntered, ConnectSendCancelled, ReleaseConnectSend, ct);
                        Push(ServiceType.ConnectResponse, [7, 0, .. Hpai.Data(Endpoint), .. Crd.Tunnel(new IndividualAddress(1, 1, 250))]); break;
                    case ServiceType.TunnellingRequest:
                        Interlocked.Increment(ref DataSends);
                        if (HoldDataSend) await HoldSendAsync(DataSendEntered, DataSendCancelled, ReleaseDataSend, ct);
                        var (channel, sequence, _) = Tunnelling.ReadRequest(frame.Body);
                        Push(ServiceType.TunnellingAck, Tunnelling.Ack(channel, sequence)); FirstDataAck.TrySetResult(); break;
                    case ServiceType.ConnectionStateRequest:
                        Interlocked.Increment(ref Heartbeats);
                        if (FailHeartbeatSend) throw new IOException("injected heartbeat send failure");
                        Push(ServiceType.ConnectionStateResponse, Connection.Response(7, HeartbeatStatus)); break;
                    case ServiceType.DisconnectRequest:
                        Interlocked.Increment(ref DisconnectRequests);
                        if (HoldDisconnectSend) await HoldSendAsync(DisconnectSendEntered, DisconnectSendCancelled, ReleaseDisconnectSend, ct);
                        Push(ServiceType.DisconnectResponse, Connection.Response(7)); break;
                    case ServiceType.DisconnectResponse:
                        Interlocked.Increment(ref RemoteResponses); RemoteResponse.TrySetResult(); break;
                }
            }
            finally { Interlocked.Decrement(ref _sends); }
        }

        private static async Task HoldSendAsync(TaskCompletionSource entered, TaskCompletionSource cancelled,
            TaskCompletionSource release, CancellationToken ct)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
        }

        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
        {
            Access(); Interlocked.Increment(ref _receives);
            if (Interlocked.Increment(ref _receiveCalls) > 1) ReceiveEntered.TrySetResult();
            try { return new(await _incoming.Reader.ReadAsync(ct), Endpoint); }
            catch (OperationCanceledException)
            {
                ReceiveCancelled.TrySetResult();
                if (HoldReceiveUnwind) await ReleaseReceive.Task;
                if (ReturnFrameAfterCancellation) return new(KnxNetIpFrame.Encode(ServiceType.TunnellingRequest,
                    Tunnelling.Request(7, 0, Indication())), Endpoint);
                throw;
            }
            finally { Interlocked.Decrement(ref _receives); }
        }

        internal void Push(ServiceType service, byte[] body) => _incoming.Writer.TryWrite(KnxNetIpFrame.Encode(service, body));
        private void Access() { if (Volatile.Read(ref _disposed) != 0) Interlocked.Increment(ref AccessAfterDisposal); }
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            DisposedWhileActive |= Volatile.Read(ref _receives) != 0 || Volatile.Read(ref _sends) != 0;
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
