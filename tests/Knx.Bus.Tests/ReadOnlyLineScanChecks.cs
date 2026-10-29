using System.Net;
using System.Net.Sockets;
using Knx;
using Knx.Bus;

internal static class ReadOnlyLineScanChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool ok) => check(name, ok, null);
        static IndividualAddress Address(int device) => new(1, 1, (byte)device);
        static ReadOnlyLineScanPlan Plan(int count = 3) => new(new(IPAddress.Parse("192.0.2.10"), 3671),
            new(1, 0, 250), Enumerable.Range(1, count).Select(Address), TimeSpan.FromMilliseconds(400), TimeSpan.Zero);

        foreach (var (name, action) in new (string, Action)[]
        {
            ("empty targets", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [])),
            ("duplicate targets", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [Address(1), Address(1)])),
            ("different lines", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [Address(1), new(1,2,1)])),
            ("reserved target", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [Address(255)])),
            ("unbounded targets", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), Enumerable.Repeat(Address(1), int.MaxValue))),
            ("multicast gateway", () => new ReadOnlyLineScanPlan(new(IPAddress.Parse("224.0.23.12"), 3671), new(1,0,250), [Address(1)])),
            ("broadcast gateway", () => new ReadOnlyLineScanPlan(new(IPAddress.Broadcast, 3671), new(1,0,250), [Address(1)])),
            ("unspecified gateway", () => new ReadOnlyLineScanPlan(new(IPAddress.Any, 3671), new(1,0,250), [Address(1)])),
            ("IPv6 gateway", () => new ReadOnlyLineScanPlan(new(IPAddress.IPv6Loopback, 3671), new(1,0,250), [Address(1)])),
            ("zero gateway port", () => new ReadOnlyLineScanPlan(new(IPAddress.Loopback, 0), new(1,0,250), [Address(1)])),
            ("invalid tunnel address", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,0), [Address(1)])),
            ("too short timeout", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [Address(1)], TimeSpan.Zero)),
            ("too long timeout", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [Address(1)], TimeSpan.FromSeconds(31))),
            ("negative pacing", () => new ReadOnlyLineScanPlan(Plan().Gateway, new(1,0,250), [Address(1)], interTargetDelay: TimeSpan.FromTicks(-1)))
        })
        {
            var rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check("read-only scan rejects " + name, rejected);
        }
        var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 3671);
        var targets = new[] { Address(1), Address(2) };
        var immutable = new ReadOnlyLineScanPlan(endpoint, new(1,0,250), targets);
        endpoint.Port = 9999; targets[0] = Address(3); immutable.Gateway.Port = 9998;
        Check("read-only scan plan detaches endpoint and targets", immutable.Gateway.Port == 3671 && immutable.Targets[0] == Address(1)
            && immutable.InterfaceId == "knx-ip:192.0.2.10:3671");

        var fixture = new ScanFixture();
        var progress = new List<ReadOnlyLineScanProgress>();
        var result = await new ReadOnlyLineScanner(fixture.Open).ScanAsync(Plan(), new InlineProgress(p => progress.Add(p)));
        Check("read-only scan returns confirmed descriptors in selected order", result.Completion == ReadOnlyLineScanCompletion.Completed
            && result.RespondedCount == 3 && result.UnknownCount == 0 && result.IsReadOnly
            && result.Observations.Select(o => o.Address).SequenceEqual(Plan().Targets)
            && result.Observations.All(o => o.MaskVersion == 0x0701 && o.ObservedAtUtc?.Offset == TimeSpan.Zero));
        Check("read-only scan uses fresh sequential sessions and awaits all disposal", fixture.Opened == 3 && fixture.Disposed == 3
            && fixture.Active == 0 && fixture.MaxActive == 1);
        Check("read-only scan emits descriptor reads only", fixture.Peers.All(p => p.DataRequests.Count == 1
            && Convert.ToHexString(p.DataRequests[0]) == "4300" && p.Disconnects == 1));
        Check("read-only scan progress is ordered and bound to scan identity", progress.Count == 3
            && progress.Select(p => p.Completed).SequenceEqual(new[] {1,2,3})
            && progress.All(p => p.Total == 3 && p.ScanId == result.ScanId));

        var couplerFixture = new ScanFixture();
        var coupler = await new ReadOnlyLineScanner(couplerFixture.Open).ScanAsync(
            new(Plan().Gateway, new(1,0,250), [Address(0)], TimeSpan.FromMilliseconds(400), TimeSpan.Zero));
        Check("read-only coupler target zero returns a descriptor without writes", coupler.RespondedCount == 1
            && coupler.Observations[0].Address == Address(0) && coupler.Observations[0].MaskVersion == 0x0701
            && couplerFixture.Peers.Single().DataRequests.Count == 1 && couplerFixture.Disposed == 1);

        foreach (var target in new[] { Address(0), Address(1) })
        {
            var peer = new ScanPeer(target, new(1,0,250), null, null);
            await using var readOnlyClient = new KnxManagementClient(peer, target, readOnly: true, TimeSpan.FromMilliseconds(100));
            Func<Task> programmingStatus = () => (Task)typeof(KnxManagementClient)
                .GetMethod("WriteProgrammingStatusAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(readOnlyClient, [(byte)0, false, CancellationToken.None])!;
            var operations = new (string Name, Func<Task> Run)[]
            {
                ("memory", () => readOnlyClient.WriteMemoryAsync(0x1234, new byte[] {1}, false)),
                ("verified memory", () => readOnlyClient.WriteMemoryAsync(0x1234, new byte[] {1}, true)),
                ("property", () => readOnlyClient.WritePropertyAsync(0, 14, 1, 1, new byte[] {1})),
                ("programming status", programmingStatus),
                ("restart", () => readOnlyClient.RestartAsync())
            };
            foreach (var (name, operation) in operations)
            {
                var refused = false;
                try { await operation(); } catch (NotSupportedException) { refused = true; }
                Check($"read-only target {target} blocks {name} before any frame", refused && peer.SentFrames.Count == 0);
            }
            await readOnlyClient.ConnectAsync();
            var descriptor = await readOnlyClient.ReadDescriptorAsync();
            var sent = peer.SentFrames.Count;
            var allBlocked = true;
            foreach (var (_, operation) in operations)
            {
                try { await operation(); allBlocked = false; } catch (NotSupportedException) { }
            }
            Check($"connected read-only target {target} stays readable while writes send zero extra frames",
                readOnlyClient.IsReadOnly && descriptor.SequenceEqual(new byte[] {7,1}) && allBlocked && peer.SentFrames.Count == sent);
        }
        var legacyCouplerRejected = false;
        try { _ = new KnxManagementClient(new ScanPeer(Address(0), new(1,0,250), null, null), Address(0)); }
        catch (ArgumentOutOfRangeException) { legacyCouplerRejected = true; }
        var readOnlyReservedRejected = false;
        try { _ = new KnxManagementClient(new ScanPeer(Address(255), new(1,0,250), null, null), Address(255), readOnly: true); }
        catch (ArgumentOutOfRangeException) { readOnlyReservedRejected = true; }
        Check("legacy writer coupler restriction and reserved address restriction remain", legacyCouplerRejected && readOnlyReservedRejected);

        foreach (var failure in new[] { "no_ack", "no_response", "nak", "disconnect", "malformed", "foreign" })
        {
            var missing = new ScanFixture { FailureFor = target => target == Address(2) ? failure : null };
            var partial = await new ReadOnlyLineScanner(missing.Open).ScanAsync(Plan());
            Check($"read-only scan {failure} is Unknown rather than absent", partial.Completion == ReadOnlyLineScanCompletion.Completed
                && partial.Observations[1].State == ReadOnlyLineScanState.Unknown && partial.Observations[1].MaskVersion is null
                && partial.RespondedCount == 2 && partial.UnknownCount == 1 && missing.Opened == 3 && missing.Disposed == 3);
        }

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var unopened = new ScanFixture();
            var stopped = await new ReadOnlyLineScanner(unopened.Open).ScanAsync(Plan(), ct: cancelled.Token);
            Check("pre-cancelled scan opens nothing and retains unprobed targets", unopened.Opened == 0
                && stopped.Completion == ReadOnlyLineScanCompletion.Cancelled
                && stopped.Observations.All(o => o.State == ReadOnlyLineScanState.NotProbed && o.Reason == ReadOnlyLineScanReason.Cancelled));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var active = new ScanFixture { FailureFor = _ => "no_ack", OnRequest = cancelled.Cancel };
            var stopped = await new ReadOnlyLineScanner(active.Open).ScanAsync(Plan(), ct: cancelled.Token);
            Check("scan cancellation joins active read and disposes before returning", stopped.Completion == ReadOnlyLineScanCompletion.Cancelled
                && active.Opened == 1 && active.Disposed == 1 && active.Active == 0
                && stopped.Observations[0].State == ReadOnlyLineScanState.Unknown
                && stopped.Observations.Skip(1).All(o => o.State == ReadOnlyLineScanState.NotProbed));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var between = new ScanFixture();
            var stopped = await new ReadOnlyLineScanner(between.Open).ScanAsync(Plan(), new InlineProgress(_ => cancelled.Cancel()), cancelled.Token);
            Check("cancelled progress cannot start another target", between.Opened == 1 && between.Disposed == 1
                && stopped.Observations[0].State == ReadOnlyLineScanState.Responded
                && stopped.Observations.Skip(1).All(o => o.State == ReadOnlyLineScanState.NotProbed));
        }
        var observerFixture = new ScanFixture();
        var observed = await new ReadOnlyLineScanner(observerFixture.Open).ScanAsync(Plan(), new InlineProgress(_ => throw new IOException("Observer failure.")));
        Check("throwing progress observer cannot interrupt cleanup or evidence", observed.RespondedCount == 3 && observerFixture.Disposed == 3);

        var self = new ScanFixture();
        var selfPlan = new ReadOnlyLineScanPlan(Plan().Gateway, Address(2), Plan().Targets, TimeSpan.FromMilliseconds(400), TimeSpan.Zero);
        var selfResult = await new ReadOnlyLineScanner(self.Open).ScanAsync(selfPlan);
        Check("requested tunnel address is explicitly NotProbed", self.Opened == 2 && selfResult.RespondedCount == 2
            && selfResult.Observations[1].State == ReadOnlyLineScanState.NotProbed
            && selfResult.Observations[1].Reason == ReadOnlyLineScanReason.RequestedTunnelAddress);

        var brokenCleanup = new ScanFixture { ThrowOnDispose = true };
        var cleanup = await new ReadOnlyLineScanner(brokenCleanup.Open).ScanAsync(Plan());
        Check("cleanup failure preserves positive evidence and stops further connections", brokenCleanup.Opened == 1 && brokenCleanup.Disposed == 1
            && cleanup.Completion == ReadOnlyLineScanCompletion.CleanupFailed
            && cleanup.Observations[0].State == ReadOnlyLineScanState.Responded && cleanup.Observations[0].CleanupFailed
            && cleanup.Observations.Skip(1).All(o => o.State == ReadOnlyLineScanState.NotProbed));

        foreach (var exception in new Exception[] { new SocketException((int)SocketError.NetworkUnreachable), new IOException("Gateway failed."), new InvalidOperationException("Factory failure.") })
        {
            var opens = 0;
            var broken = await new ReadOnlyLineScanner((_, _, _) => { opens++; return Task.FromException<IManagementConnection>(exception); }).ScanAsync(Plan());
            Check($"failed open {exception.GetType().Name} stops with unknown coverage", opens == 1 && broken.Completion == ReadOnlyLineScanCompletion.TransportFailed
                && broken.Observations[0].State == ReadOnlyLineScanState.Unknown
                && broken.Observations.Skip(1).All(o => o.State == ReadOnlyLineScanState.NotProbed));
        }

        var timedOpens = 0;
        var timed = await new ReadOnlyLineScanner(async (_, _, ct) =>
        {
            timedOpens++; await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("An expired open must not return a connection.");
        }).ScanAsync(new(Plan().Gateway, new(1,0,250), Plan().Targets, TimeSpan.FromMilliseconds(50), TimeSpan.Zero));
        Check("gateway open deadline stops further probes without declaring absence", timedOpens == 1
            && timed.Completion == ReadOnlyLineScanCompletion.TransportFailed && timed.Observations[0].Reason == ReadOnlyLineScanReason.Timeout
            && timed.Observations.Skip(1).All(o => o.State == ReadOnlyLineScanState.NotProbed));

        var wrongTargetFixture = new ScanFixture();
        var wrongTarget = await new ReadOnlyLineScanner((plan, _, ct) => wrongTargetFixture.Open(plan, Address(4), ct)).ScanAsync(Plan());
        Check("factory target mismatch is closed before any descriptor read", wrongTarget.Completion == ReadOnlyLineScanCompletion.TransportFailed
            && wrongTargetFixture.Opened == 1 && wrongTargetFixture.Disposed == 1 && wrongTargetFixture.Peers.Single().DataRequests.Count == 0);

        var writerFixture = new ScanFixture { ReadOnly = false };
        var writer = await new ReadOnlyLineScanner(writerFixture.Open).ScanAsync(Plan());
        Check("scanner rejects writer-capable injected session before descriptor read", writer.Completion == ReadOnlyLineScanCompletion.TransportFailed
            && writerFixture.Opened == 1 && writerFixture.Disposed == 1 && writerFixture.Peers.Single().DataRequests.Count == 0);

        var collisionFixture = new ScanFixture(); var collisions = 0;
        var collision = await new ReadOnlyLineScanner((plan, target, ct) =>
        {
            if (target == Address(2)) { collisions++; throw new ManagementAddressCollisionException(target); }
            return collisionFixture.Open(plan, target, ct);
        }).ScanAsync(Plan());
        Check("assigned tunnel address collision is NotProbed and scanning continues", collision.Completion == ReadOnlyLineScanCompletion.Completed
            && collisions == 1 && collisionFixture.Opened == 2 && collisionFixture.Disposed == 2 && collision.RespondedCount == 2
            && collision.Observations[1].State == ReadOnlyLineScanState.NotProbed
            && collision.Observations[1].Reason == ReadOnlyLineScanReason.AssignedTunnelAddress
            && collision.Observations[2].State == ReadOnlyLineScanState.Responded);
        var mismatchedCollision = await new ReadOnlyLineScanner((_, _, _) => throw new ManagementAddressCollisionException(Address(7))).ScanAsync(Plan());
        Check("mismatched collision evidence cannot silently skip another target", mismatchedCollision.Completion == ReadOnlyLineScanCompletion.TransportFailed
            && mismatchedCollision.Observations[0].State == ReadOnlyLineScanState.Unknown);

        var fullLineFixture = new ScanFixture();
        var fullLine = await new ReadOnlyLineScanner(fullLineFixture.Open).ScanAsync(
            new(Plan().Gateway, new(1,0,250), Enumerable.Range(0, 255).Select(Address), TimeSpan.FromMilliseconds(400), TimeSpan.Zero));
        Check("full 255-target line including coupler remains read-only sequential and disposed", fullLine.RespondedCount == 255
            && fullLineFixture.Opened == 255 && fullLineFixture.Disposed == 255 && fullLineFixture.MaxActive == 1 && fullLineFixture.Active == 0
            && fullLine.Observations[0].Address == Address(0) && fullLine.Observations[^1].Address == Address(254)
            && fullLineFixture.Peers.All(p => p.DataRequests.Count == 1 && ManagementApdu.Service(p.DataRequests[0]) == 0x300));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentFixture = new ScanFixture();
        var shared = new ReadOnlyLineScanner(async (plan, target, ct) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(ct); return await concurrentFixture.Open(plan, target, ct);
        });
        using (var cancelled = new CancellationTokenSource())
        {
            var first = shared.ScanAsync(Plan(1), ct: cancelled.Token);
            await entered.Task;
            var rejected = false;
            try { await shared.ScanAsync(Plan(1)); } catch (InvalidOperationException) { rejected = true; }
            cancelled.Cancel(); await first;
            Check("one scanner refuses concurrent runs before additional opens", rejected && concurrentFixture.Opened == 0);
        }
        release.SetResult();
        var next = await shared.ScanAsync(Plan(1));
        Check("scanner can be used again after joined cancellation", next.RespondedCount == 1 && concurrentFixture.Disposed == 1);
    }

    private sealed class InlineProgress(Action<ReadOnlyLineScanProgress> receive) : IProgress<ReadOnlyLineScanProgress>
    { public void Report(ReadOnlyLineScanProgress value) => receive(value); }

    private sealed class ScanFixture
    {
        public Func<IndividualAddress, string?>? FailureFor { get; init; }
        public Action? OnRequest { get; init; }
        public bool ThrowOnDispose { get; init; }
        public bool ReadOnly { get; init; } = true;
        public int Opened, Disposed, Active, MaxActive;
        public List<ScanPeer> Peers { get; } = [];
        public async Task<IManagementConnection> Open(ReadOnlyLineScanPlan plan, IndividualAddress target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var peer = new ScanPeer(target, plan.RequestedTunnelAddress, FailureFor?.Invoke(target), OnRequest);
            var client = new KnxManagementClient(peer, target, readOnly: ReadOnly, TimeSpan.FromMilliseconds(80));
            try { await client.ConnectAsync(ct); }
            catch { await client.DisposeAsync(); throw; }
            Peers.Add(peer); Opened++; Active++; MaxActive = Math.Max(MaxActive, Active);
            return new ScanConnection(client, this);
        }
    }

    private sealed class ScanConnection(KnxManagementClient client, ScanFixture owner) : IManagementConnection
    {
        public KnxManagementClient Client => client;
        public async ValueTask DisposeAsync()
        {
            try { await client.DisposeAsync(); }
            finally { owner.Disposed++; owner.Active--; }
            if (owner.ThrowOnDispose) throw new IOException("Synthetic cleanup failure.");
        }
    }

    private sealed class ScanPeer(IndividualAddress target, IndividualAddress own, string? failure, Action? onRequest) : IManagementTransport
    {
        public IndividualAddress AssignedAddress => own;
        public bool IsConnected => true;
        public event Action<ManagementTransportFrame>? FrameReceived;
        public List<byte[]> DataRequests { get; } = [];
        public List<byte[]> SentFrames { get; } = [];
        public int Disconnects;
        public Task SendAsync(IndividualAddress destination, byte[] tpdu, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentFrames.Add((byte[])tpdu.Clone());
            if (destination != target) throw new IOException("Unexpected scan target.");
            if (tpdu.Length == 1)
            {
                if (tpdu[0] == 0x81) Disconnects++;
                else if (tpdu[0] != 0x80 && (tpdu[0] & 0xC3) != 0xC2) throw new IOException("Unexpected scan control.");
                return Task.CompletedTask;
            }
            DataRequests.Add((byte[])tpdu.Clone());
            if (tpdu.Length != 2 || ManagementApdu.Service(tpdu) != 0x300 || tpdu[1] != 0)
                throw new IOException("Scan attempted a service other than descriptor read.");
            onRequest?.Invoke();
            if (failure == "no_ack") return Task.CompletedTask;
            var sequence = tpdu[0] & 0x3C;
            if (failure == "disconnect") { Emit([0x81]); return Task.CompletedTask; }
            Emit([(byte)((failure == "nak" ? 0xC3 : 0xC2) | sequence)]);
            if (failure is "nak" or "no_response") return Task.CompletedTask;
            if (failure == "foreign")
                FrameReceived?.Invoke(new(new IndividualAddress(1,2,20), own, [0x43,0x40,0x07,0x01]));
            else Emit(failure == "malformed" ? [0x43,0x40,0x07] : [0x43,0x40,0x07,0x01]);
            return Task.CompletedTask;
        }
        private void Emit(byte[] tpdu) => FrameReceived?.Invoke(new(target, own, tpdu));
    }
}
