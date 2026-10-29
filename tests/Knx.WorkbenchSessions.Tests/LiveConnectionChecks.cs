using System.Net;
using Knx;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.App.Services;
using Knx.Bus;
using Knx.Catalog;

internal static class LiveConnectionChecks
{
    public static async Task Run(Action<string, bool> check)
    {
        static ReadOnlyConnectionOptions Options(int port = 3671) => new(IPAddress.Parse("192.0.2.190"), port, new(1,0,250));
        static async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        static async Task Until(Func<bool> condition)
        { for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(5); if (!condition()) throw new Exception("Live connection test deadline."); }

        var lifetime = new object(); object current = lifetime;
        var created = 0; var backend = new FakeBackend();
        await using (var owner = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => { created++; return backend; }))
        {
            check("connection construction and subscriptions open no network backend", created == 0 && !owner.IsConnected);
            var received = 0; owner.TelegramReceived += _ => received++;
            await owner.ConnectAsync(Options());
            check("explicit connection publishes actual assigned source and readonly status", created == 1 && owner.IsConnected
                && owner.Snapshot.IsReadOnly && owner.Snapshot.AssignedAddress == new IndividualAddress(1,0,251));
            var late = backend.ReceiveSnapshot;
            backend.Send(); check("connected source forwards real telegram once", received == 1);
            current = new object(); // Same project ID may still have a different owning lifetime.
            backend.Send(); late?.Invoke(Frame());
            check("same-id replacement lifetime rejects old and captured late callbacks", !owner.IsConnected && received == 1);
            await owner.DisconnectAsync();
            check("explicit disconnect joins backend cleanup", backend.Disposals == 1 && backend.Subscribers == 0);
        }

        current = lifetime;
        var firstBackend = new FakeBackend { DisposeGate = Gate() };
        await using (var first = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => firstBackend))
        await using (var second = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => new FakeBackend()))
        {
            await first.ConnectAsync(Options());
            check("duplicate connect is refused before a second backend", await Fails(() => first.ConnectAsync(Options())));
            var closing = first.DisconnectAsync();
            await firstBackend.DisposeEntered.Task;
            check("disconnect stays pending until local I/O shutdown finishes", !closing.IsCompleted);
            check("another window cannot reserve endpoint during old cleanup", await Fails(() => second.ConnectAsync(Options()))
                && second.Snapshot.Issue == ReadOnlyConnectionIssue.Occupied);
            firstBackend.DisposeGate.SetResult(); await closing;
            await second.ConnectAsync(Options());
            check("next window connects only after old cleanup released reservation", second.IsConnected && firstBackend.Disposals == 1);
        }

        current = lifetime;
        var pendingBackend = new FakeBackend { ConnectGate = Gate(), IgnoreCancellation = true };
        await using (var pending = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => pendingBackend))
        {
            var connecting = pending.ConnectAsync(Options());
            await pendingBackend.ConnectEntered.Task;
            current = new object();
            var closing = pending.DisposeAsync().AsTask();
            check("close joins a delayed in-flight connect rather than abandoning it", !closing.IsCompleted);
            pendingBackend.ConnectGate.SetResult();
            check("late successful transport connect cannot adopt replacement project", await Fails(() => connecting));
            await closing;
            check("late connect is disposed and never publishes connected", !pending.IsConnected && pendingBackend.Disposals == 1
                && pending.Snapshot.Phase == ReadOnlyConnectionPhase.Disconnected);
        }

        current = lifetime;
        var cancelledBackend = new FakeBackend { ConnectGate = Gate() };
        await using (var owner = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => cancelledBackend))
        {
            using var cancel = new CancellationTokenSource();
            var connecting = owner.ConnectAsync(Options(), cancel.Token);
            await cancelledBackend.ConnectEntered.Task; cancel.Cancel();
            check("connect cancellation cleans up its acquired backend", await Fails(() => connecting) && cancelledBackend.Disposals == 1
                && !owner.IsConnected && owner.Snapshot.Issue == ReadOnlyConnectionIssue.Cancelled);
        }

        current = lifetime;
        var remoteBackend = new FakeBackend();
        await using (var owner = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => remoteBackend))
        {
            await owner.ConnectAsync(Options());
            using var monitor = new MonitorSession(new(), () => new Project { Id = "LiveTest", Name = "Synthetic live test" });
            monitor.ConfigureReal(Options().EndpointId, owner); monitor.Start();
            remoteBackend.Send(); monitor.FlushDisplay();
            check("owned source feeds existing real monitor recorder", monitor.RecordedCount == 1 && monitor.Rows.Single().Origin == SessionOrigin.Real);
            var late = remoteBackend.ReceiveSnapshot;
            remoteBackend.LoseConnection();
            await Until(() => owner.Snapshot.Phase == ReadOnlyConnectionPhase.Disconnected);
            late?.Invoke(Frame());
            check("remote link loss stops monitor and preserves explicit link-lost status", !owner.IsConnected && !monitor.IsRunning
                && owner.Snapshot.Issue == ReadOnlyConnectionIssue.LinkLost && remoteBackend.Disposals == 1 && monitor.RecordedCount == 1);
            monitor.Stop();
            try { monitor.Start(); check("disconnected real monitor cannot silently start demo", false); }
            catch (InvalidOperationException) { check("disconnected real monitor cannot silently start demo", monitor.Origin == SessionOrigin.Real); }
        }

        current = lifetime;
        var failBackend = new FakeBackend { ConnectError = true };
        await using (var owner = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => failBackend))
        {
            check("failed connect disposes transport and releases endpoint", await Fails(() => owner.ConnectAsync(Options())) && failBackend.Disposals == 1);
            using var free = RealEndpointReservations.TryAcquire(Options().EndpointId);
            check("failed connect leaves no endpoint reservation", free is not null);
        }

        var cleanupBackend = new FakeBackend { DisposeErrorOnce = true };
        await using (var owner = new ReadOnlyWorkbenchConnection(lifetime, () => current, _ => cleanupBackend))
        {
            await owner.ConnectAsync(Options());
            check("cleanup failure remains visible", await Fails(() => owner.DisconnectAsync()) && owner.Snapshot.Issue == ReadOnlyConnectionIssue.CleanupFailed);
            using (var locked = RealEndpointReservations.TryAcquire(Options().EndpointId))
                check("failed cleanup cannot release endpoint to a new owner", locked is null);
            await owner.DisconnectAsync();
            using var free = RealEndpointReservations.TryAcquire(Options().EndpointId);
            check("explicit cleanup retry releases ownership after successful join", free is not null && cleanupBackend.Disposals == 2);
        }

        check("connection UI strings cover all eight languages", LiveConnectionStrings.Languages.Count == 8
            && LiveConnectionStrings.Languages.All(language => LiveConnectionStrings.Keys.All(key =>
                !string.IsNullOrWhiteSpace(LiveConnectionStrings.GetForLanguage(language, key)))));

        Project ScanProject() => new() { Id = "same-scan-project", Name = "Synthetic scan", Installations =
        { new Installation { Id = "I", Areas = { new Area { Id = "A", Address = 1, Lines = { new Line { Id = "L", Address = 1 } } } } } } };
        Project? scanProject = ScanProject();
        var plan = new ReadOnlyLineScanPlan(new(IPAddress.Parse("192.0.2.191"),3671), new(1,0,250), [new(1,1,0), new(1,1,1)],
            TimeSpan.FromMilliseconds(400), TimeSpan.Zero);
        var scanDisposals = 0;
        async Task<IManagementConnection> OpenDescriptor(ReadOnlyLineScanPlan options, IndividualAddress target, CancellationToken ct)
        {
            var client = new KnxManagementClient(new DescriptorTransport(target, options.RequestedTunnelAddress), target, readOnly: true);
            await client.ConnectAsync(ct); return new DescriptorConnection(client, () => scanDisposals++);
        }
        await using (var scan = new LineScanSession(new(), () => scanProject))
        {
            await scan.RunRealAsync("L", plan, new(OpenDescriptor));
            check("real scan session preserves origin and coupler descriptor evidence", scan.Results.Count == 2
                && scan.Results.All(r => r.Origin == SessionOrigin.Real) && scan.Results[0].Address == "1.1.0" && scanDisposals == 2);
        }
        using (var reservation = RealEndpointReservations.TryAcquire(plan.InterfaceId))
        {
            await using var scan = new LineScanSession(new(), () => scanProject);
            check("real scan cannot bypass another window endpoint reservation", await Fails(() => scan.RunRealAsync("L", plan, new(OpenDescriptor)))
                && scanDisposals == 2);
        }
        var scanEntered = Gate(); var scanRelease = Gate();
        var delayedScanner = new ReadOnlyLineScanner(async (options, target, ct) =>
        { scanEntered.TrySetResult(); await scanRelease.Task.WaitAsync(ct); return await OpenDescriptor(options, target, ct); });
        await using (var scan = new LineScanSession(new(), () => scanProject))
        {
            var running = scan.RunRealAsync("L", plan, delayedScanner); await scanEntered.Task;
            scanProject = ScanProject(); scanRelease.SetResult(); await running;
            check("same-id replacement cannot adopt stale real scan rows", scan.Results.Count == 0 && !scan.IsRunning);
        }
        var cancelEntered = Gate();
        var cancellableScanner = new ReadOnlyLineScanner(async (_, _, ct) =>
        { cancelEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); });
        var cancellingScan = new LineScanSession(new(), () => scanProject);
        var pendingScan = cancellingScan.RunRealAsync("L", plan, cancellableScanner); await cancelEntered.Task;
        await cancellingScan.DisposeAsync(); await pendingScan;
        using (var free = RealEndpointReservations.TryAcquire(plan.InterfaceId))
            check("scan session disposal cancels joins and releases endpoint", free is not null && !cancellingScan.IsRunning && cancellingScan.Results.Count == 0);

        var oldSettings = Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS");
        var privateRoot = Path.Combine(Path.GetTempPath(), "buschord-live-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot); Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(privateRoot, "settings.json"));
        try
        {
            var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(privateRoot, "catalog"))));
            vm.BusState = BusConnectionState.Connected;
            vm.SelectedGroupAddress = new(GroupAddress.ThreeLevel(1,1,1), "Synthetic", "1.001", "G");
            vm.SelectedDevice = new(new DeviceInstance { Id = "D", ProductRefId = "Synthetic", Address = new(1,1,1) });
            vm.SelectedDeviceOnBus = new KnxDevice { ControlEndpoint = Options().Gateway, Name = "Synthetic", Serial = [],
                MulticastAddress = "", MacAddress = "", FriendlyName = "Synthetic", KnxAddress = "1.1.1", SupportedFamilies = [] };
            check("legacy send and download stay disabled with all selections and a connected link", !vm.CanSend && !vm.CanDownload);
            var receive = typeof(MainWindowViewModel).GetMethod("OnTelegram", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var capture = typeof(MainWindowViewModel).GetProperty("WorkbenchMonitorOwnsCapture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            vm.StartRecording(); receive.Invoke(vm, [Frame()]);
            check("legacy recorder remains available for its own shell", vm.Recorder.Count == 1 && vm.Monitor.Count == 1);
            capture.SetValue(vm, true);
            for (var i = 0; i < 1000; i++) receive.Invoke(vm, [Frame()]);
            check("Workbench ownership suppresses legacy recorder and per-telegram UI flood", vm.Recorder.Count == 1 && vm.Monitor.Count == 1 && vm.Recorded.Count == 1);
        }
        finally { Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", oldSettings); }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class DescriptorConnection(KnxManagementClient client, Action closed) : IManagementConnection
    {
        public KnxManagementClient Client => client;
        public async ValueTask DisposeAsync() { await client.DisposeAsync(); closed(); }
    }
    private sealed class DescriptorTransport(IndividualAddress target, IndividualAddress own) : IManagementTransport
    {
        public IndividualAddress AssignedAddress => own;
        public bool IsConnected => true;
        public event Action<ManagementTransportFrame>? FrameReceived;
        public Task SendAsync(IndividualAddress destination, byte[] tpdu, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (destination != target) throw new IOException("Wrong scan target.");
            if (tpdu.Length == 1) return Task.CompletedTask;
            if (tpdu.Length != 2 || ManagementApdu.Service(tpdu) != 0x300) throw new IOException("Unexpected scan service.");
            FrameReceived?.Invoke(new(target, own, [(byte)(0xC2 | (tpdu[0] & 0x3C))]));
            FrameReceived?.Invoke(new(target, own, [0x43,0x40,0x07,0x01]));
            return Task.CompletedTask;
        }
    }
    private static BusTelegram Frame() => new() { Source = "synthetic readonly tunnel", Telegram = new()
    { Source = new(1,1,1), Destination = new(0x0101), IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = [1] } };
    private sealed class FakeBackend : IReadOnlyWorkbenchBackend
    {
        private Action<BusTelegram>? _received;
        private Action<BusConnectionState>? _state;
        public TaskCompletionSource? ConnectGate { get; init; }
        public TaskCompletionSource? DisposeGate { get; init; }
        public TaskCompletionSource ConnectEntered { get; } = Gate();
        public TaskCompletionSource DisposeEntered { get; } = Gate();
        public bool IgnoreCancellation { get; init; }
        public bool ConnectError { get; init; }
        public bool DisposeErrorOnce { get; init; }
        public int Disposals { get; private set; }
        public bool IsConnected { get; private set; }
        public IndividualAddress AssignedAddress => new(1,0,251);
        public int Subscribers => _received?.GetInvocationList().Length ?? 0;
        public Action<BusTelegram>? ReceiveSnapshot => _received;
        public event Action<BusTelegram> TelegramReceived { add => _received += value; remove => _received -= value; }
        public event Action<BusConnectionState> StateChanged { add => _state += value; remove => _state -= value; }
        public async Task ConnectAsync(CancellationToken ct)
        {
            ConnectEntered.TrySetResult();
            if (ConnectGate is not null) await (IgnoreCancellation ? ConnectGate.Task : ConnectGate.Task.WaitAsync(ct));
            if (ConnectError) throw new IOException("Synthetic failed connect.");
            IsConnected = true;
        }
        public void Send() => _received?.Invoke(Frame());
        public void LoseConnection() { IsConnected = false; _state?.Invoke(BusConnectionState.Disconnected); }
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            if (DisposeGate is not null) await DisposeGate.Task;
            Disposals++;
            if (DisposeErrorOnce && Disposals == 1) throw new IOException("Synthetic failed cleanup.");
            IsConnected = false;
        }
    }
}
