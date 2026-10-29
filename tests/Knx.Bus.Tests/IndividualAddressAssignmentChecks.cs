using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Commissioning;
using Knx.Bus.Connections;

internal static class IndividualAddressAssignmentChecks
{
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Case(string name, Func<Task> body)
        { try { await body().WaitAsync(TimeSpan.FromSeconds(8)); Console.WriteLine("PASS Address assignment " + name); } catch (Exception error) { failures++; Console.WriteLine("FAIL Address assignment " + name + ": " + error); } }
        void Require(bool value) { if (!value) throw new Exception("Address assignment invariant failed."); }
        await Case("secure profile and missing physical/reservation declarations refuse before acquisition", async () =>
        {
            using var fixture = new Fixture(); var rejected = 0;
            foreach (var create in new Action[] { () => new AddressAssignmentRequest(fixture.Profile, Fixture.New, AddressSelectionKind.ExactSerial, "", Fixture.Serial, confirmPlainDevice: true),
                () => new AddressAssignmentRequest(fixture.Profile, Fixture.New, AddressSelectionKind.ExactSerial, "project reservation", Fixture.Serial),
                () => new AddressAssignmentRequest(fixture.Profile, Fixture.New, AddressSelectionKind.IsolatedProgrammingMode, "project reservation", confirmPlainDevice: true),
                () => new AddressAssignmentRequest(fixture.Profile, Fixture.New, AddressSelectionKind.ExactSerial, "project reservation", Fixture.Serial, confirmPlainDevice: true, requiresSecureProvisioning: true) })
            { try { create(); } catch (AddressAssignmentException) { rejected++; } }
            Require(rejected == 4 && fixture.Opens == 0); await Task.CompletedTask;
        });
        await Case("serial review is read-only and silence remains an explicit reservation requirement", async () =>
        {
            using var f = new Fixture(); await using var assignment = f.Create(); var review = await assignment.ReviewAsync();
            Require(review.Serial == Fixture.Serial && review.OldAddress == Fixture.Old.ToString() && review.Occupancy == AddressOccupancyState.NoResponseReservationRequired && f.Writes == 0 && f.Opens == 1);
            try { await assignment.ApplyAsync(review.Fingerprint, false, true); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.ReservationRequired); }
            Require(f.Writes == 0 && Directory.GetFiles(f.Root, "intent.json", SearchOption.AllDirectories).Length == 0);
        });
        foreach (var mode in new[] { "duplicate", "multiple", "malformed", "occupied" })
        await Case("ambiguous/conflicting evidence refuses without write " + mode, async () =>
        {
            using var f = new Fixture { ObservationMode = mode }; await using var assignment = f.Create();
            try { await assignment.ReviewAsync(); throw new Exception(); } catch (AddressAssignmentException) { }
            Require(f.Writes == 0);
        });
        await Case("intent and exclusive attempt are flushed before the sole write and fresh verification", async () =>
        {
            using var f = new Fixture(); await using var assignment = f.Create(); var review = await assignment.ReviewAsync();
            f.BeforeWrite = () => Require(Directory.GetFiles(f.Root, "intent.json", SearchOption.AllDirectories).Length == 1 && Directory.GetFiles(f.Root, "attempt.json", SearchOption.AllDirectories).Length == 1);
            var result = await assignment.ApplyAsync(review.Fingerprint, true, true);
            Require(result.AddressVerified && f.Writes == 1 && f.IdentityReads == 1 && File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.IntentPath)!, "outcome.jsonl")).Contains("ReviewFingerprint"));
            try { await assignment.ApplyAsync(review.Fingerprint, true, true); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.AlreadyAttempted); }
            Require(f.Writes == 1);
        });
        await Case("ACK loss is reconciled without another write", async () =>
        { using var f = new Fixture { ThrowAfterWrite = true }; await using var assignment = f.Create(); var r = await assignment.ReviewAsync(); var result = await assignment.ApplyAsync(r.Fingerprint, true, true); Require(result.AddressVerified && f.Writes == 1); });
        await Case("device drift and late target occupancy refuse before intent/write", async () =>
        {
            foreach (var drift in new[] { false, true })
            { using var f = new Fixture(); await using var assignment = f.Create(); var r = await assignment.ReviewAsync(); if (drift) f.Current = new(1, 1, 9); else f.ObservationMode = "occupied";
                try { await assignment.ApplyAsync(r.Fingerprint, true, true); throw new Exception(); } catch (AddressAssignmentException) { } Require(f.Writes == 0); }
        });
        await Case("caller cancel and close join held independent reconciliation", async () =>
        {
            using var f = new Fixture { HoldVerification = true }; var assignment = f.Create(); var r = await assignment.ReviewAsync(); using var cancel = new CancellationTokenSource();
            var applying = assignment.ApplyAsync(r.Fingerprint, true, true, cancel.Token); await f.VerifyEntered.Task; cancel.Cancel(); var closing = assignment.DisposeAsync().AsTask();
            Require(!closing.IsCompleted && !applying.IsCompleted && f.Disposals == 0 && ReferenceEquals(closing, assignment.DisposeAsync().AsTask()));
            f.ReleaseVerification.TrySetResult(); var result = await applying; await closing; Require(result.AddressVerified && f.Writes == 1 && f.Disposals == 1);
        });
        await Case("wrong physical identity and missing response remain unresolved", async () =>
        {
            foreach (var missing in new[] { false, true })
            { using var f = new Fixture { WrongIdentity = !missing, MissingAfterWrite = missing }; await using var assignment = f.Create(); var r = await assignment.ReviewAsync(); var result = await assignment.ApplyAsync(r.Fingerprint, true, true); Require(!result.AddressVerified && result.RequiresReconciliation && f.Writes == 1); }
        });
        await Case("cold reconciliation cannot resend even when the claim file is absent", async () =>
        {
            using var f = new Fixture(); string path;
            await using (var assignment = f.Create()) { var r = await assignment.ReviewAsync(); path = (await assignment.ApplyAsync(r.Fingerprint, true, true)).IntentPath; }
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "attempt.json"));
            var result = await IndividualAddressAssignment.ReconcileAsync(path, open: f.Open); Require(result.AddressVerified && !result.WriteAttemptClaimed && f.Writes == 1 && f.Opens == 2 && f.Disposals == 2);
        });
        await Case("isolated programming mode requires continuity on cold resume", async () =>
        {
            using var f = new Fixture(); string path;
            await using (var assignment = f.Create(true)) { var r = await assignment.ReviewAsync(); path = (await assignment.ApplyAsync(r.Fingerprint, true, true)).IntentPath; }
            try { await IndividualAddressAssignment.ReconcileAsync(path, open: f.Open); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.PhysicalBindingRequired); }
            Require(f.Opens == 1); var result = await IndividualAddressAssignment.ReconcileAsync(path, true, "isolated bench device A", f.Open); Require(result.AddressVerified && f.Writes == 1);
        });
        await Case("no-retry tunnel request emits once while ordinary sends retain retry", async () =>
        {
            var transport = new NoAckTransport(); await using var tunnel = new KnxTunnel(transport, transport.Peer, Fixture.Client, transport.Local);
            await tunnel.ConnectAsync(); try { await tunnel.SendIndividualAddressOnceAsync(Fixture.New, Convert.FromHexString(Fixture.Serial)); throw new Exception(); } catch (TimeoutException) { }
            Require(transport.Writes == 1); try { await tunnel.GroupValueReadAsync(new(1)); throw new Exception(); } catch (TimeoutException) { } Require(transport.Writes == 3);
        });
        foreach (var isolated in new[] { false, true })
        await Case("production factory/discovery/management path verifies one write " + isolated, async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using (var assignment = f.Create(isolated))
            { var review = await assignment.ReviewAsync(); var result = await assignment.ApplyAsync(review.Fingerprint, true, true); Require(result.AddressVerified && result.Identity?.Serial == IndividualAddressGatewayFixture.DeviceSerial && f.Writes == 1 && f.Transports >= 6); }
            Require(f.Transports == f.Disposals);
        });
        await Case("actual positive group traffic blocks a proposed address without descriptor support", async () =>
        {
            using var f = new IndividualAddressGatewayFixture { OtherTrafficAtDesired = true }; await using var assignment = f.Create();
            try { await assignment.ReviewAsync(); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.TargetResponded); } Require(f.Writes == 0);
        });
        foreach (var target in new[] { new IndividualAddress(1, 1, 0), new IndividualAddress(1, 0, 0), new IndividualAddress(1, 1, 255) })
        await Case("legitimate coupler/255 target survives the real adapter and cold read " + target, async () =>
        {
            using var f = new IndividualAddressGatewayFixture { ExpectedTarget = target, MaskVersion = target.Device == 0 ? (ushort)0x091A : (ushort)0x0701 }; string path;
            await using (var assignment = f.Create(coupler: target.Device == 0))
            { var review = await assignment.ReviewAsync(); var result = await assignment.ApplyAsync(review.Fingerprint, true, true); Require(result.AddressVerified && f.Writes == 1); path = result.IntentPath; }
            var cold = await IndividualAddressAssignment.ReconcileAsync(path, open: new AddressAssignmentTransport(f.Factory()).OpenAsync);
            Require(cold.AddressVerified && f.Writes == 1 && f.Transports == f.Disposals);
        });
        await Case("reserved zero/FFFF and unproven zero-device role fail before I/O", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); var refused = 0;
            foreach (var target in new[] { new IndividualAddress(0), new IndividualAddress(ushort.MaxValue), new IndividualAddress(1, 1, 0) })
            try { _ = new AddressAssignmentRequest(f.Profile, target, AddressSelectionKind.ExactSerial, "reserved", IndividualAddressGatewayFixture.DeviceSerial, confirmPlainDevice: true); }
            catch (AddressAssignmentException) { refused++; }
            try { _ = new AddressAssignmentRequest(f.Profile, new(1, 1, 0), AddressSelectionKind.ExactSerial, "reserved", IndividualAddressGatewayFixture.DeviceSerial, confirmPlainDevice: true, targetRole: AddressDeviceRole.Coupler, confirmCouplerHardware: true); }
            catch (AddressAssignmentException) { refused++; }
            Require(refused == 4 && f.Transports == 0); await Task.CompletedTask;
        });
        await Case("late host context change blocks the claimed write", async () =>
        {
            using var f = new Fixture(); var current = true;
            await using var assignment = new IndividualAddressAssignment(new(f.Profile, Fixture.New, AddressSelectionKind.ExactSerial, "reserved", Fixture.Serial, confirmPlainDevice: true), f.Root, f.Open, () => current);
            var review = await assignment.ReviewAsync(); current = false;
            try { await assignment.ApplyAsync(review.Fingerprint, true, true); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.StaleReview); }
            Require(f.Writes == 0);
        });
        await Case("cold intent tamper/pin/claim failures never acquire a transport", async () =>
        {
            using var f = new Fixture(); string path;
            await using (var assignment = f.Create()) { var review = await assignment.ReviewAsync(); path = (await assignment.ApplyAsync(review.Fingerprint, true, true)).IntentPath; }
            var before = f.Opens;
            try { await IndividualAddressAssignment.ReconcileAsync(path, open: f.Open, expectedReviewFingerprint: new string('0', 64)); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.StaleReview); }
            var original = File.ReadAllText(path); File.WriteAllText(path, original.Replace("explicit synthetic project reservation", "changed synthetic reservation"));
            try { await IndividualAddressAssignment.ReconcileAsync(path, open: f.Open); throw new Exception(); } catch (AddressAssignmentException) { }
            File.WriteAllText(path, original); var claim = Path.Combine(Path.GetDirectoryName(path)!, "attempt.json"); File.WriteAllText(claim, new string('x', 2049));
            try { await IndividualAddressAssignment.ReconcileAsync(path, open: f.Open); throw new Exception(); } catch (AddressAssignmentException) { }
            Require(f.Opens == before && f.Writes == 1);
        });
        await Case("exclusive claimed attempt cannot be won twice", async () =>
        {
            using var f = new Fixture(); await using var assignment = f.Create(); var review = await assignment.ReviewAsync();
            var intent = new AddressAssignmentIntent(1, Guid.NewGuid(), f.Profile.Gateway.Address.ToString(), f.Profile.Port, review.ClientAddress, null, review, 20);
            var path = AddressAssignmentJournal.Create(f.Root, intent); var winners = 0; var losers = 0;
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => { try { AddressAssignmentJournal.ClaimAttempt(path, intent); Interlocked.Increment(ref winners); } catch (AddressAssignmentException error) when (error.Failure == AddressAssignmentFailure.AlreadyAttempted) { Interlocked.Increment(ref losers); } })));
            Require(winners == 1 && losers == 11 && f.Writes == 0);
        });
        await Case("concurrent apply and stale operations cannot create another attempt", async () =>
        {
            using var f = new Fixture { HoldVerification = true }; await using var assignment = f.Create(); var review = await assignment.ReviewAsync();
            var applying = assignment.ApplyAsync(review.Fingerprint, true, true); await f.VerifyEntered.Task;
            try { await assignment.ApplyAsync(review.Fingerprint, true, true); throw new Exception(); } catch (AddressAssignmentException error) { Require(error.Failure == AddressAssignmentFailure.Occupied); }
            f.ReleaseVerification.TrySetResult(); Require((await applying).AddressVerified && f.Writes == 1);
        });
        await Case("pre-cancelled review acquires nothing", async () =>
        {
            using var f = new Fixture(); await using var assignment = f.Create(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await assignment.ReviewAsync(cancelled.Token); throw new Exception(); } catch (OperationCanceledException) { }
            Require(f.Opens == 0 && f.Writes == 0);
        });
        return failures;
    }
    internal sealed class Fixture : IDisposable
    {
        internal static readonly IndividualAddress Old = new(1, 1, 7), New = new(1, 1, 8), Client = new(1, 1, 240);
        internal const string Serial = "008301020304";
        internal string Root = Directory.CreateTempSubdirectory("BusChord.Address.").FullName;
        internal KnxConnectionProfile Profile = KnxConnectionProfile.Plain(new(IPAddress.Loopback, 3671), Client);
        internal IndividualAddress Current = Old; internal string? ObservationMode; internal int Opens, Writes, IdentityReads, Disposals;
        internal bool ThrowAfterWrite, HoldVerification, WrongIdentity, MissingAfterWrite;
        internal Action? BeforeWrite;
        internal TaskCompletionSource VerifyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseVerification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IndividualAddressAssignment Create(bool isolated = false) => new(new(Profile, New, isolated ? AddressSelectionKind.IsolatedProgrammingMode : AddressSelectionKind.ExactSerial,
            "explicit synthetic project reservation", isolated ? null : Serial, isolated ? "isolated bench device A" : null, confirmPlainDevice: true, observationWindow: TimeSpan.FromMilliseconds(20)), Root, Open);
        internal Task<IAddressAssignmentOperation> Open(KnxConnectionProfile profile, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Opens++; return Task.FromResult<IAddressAssignmentOperation>(new Operation(this, profile)); }
        public void Dispose() { ReleaseVerification.TrySetResult(); Directory.Delete(Root, true); }
        private sealed class Operation(Fixture owner, KnxConnectionProfile profile) : IAddressAssignmentOperation
        {
            public KnxConnectionProfile Profile => profile;
            public Task<IndividualAddressObservation> DiscoverAsync(AddressSelectionKind kind, string? serial, TimeSpan window, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); var mode = owner.MissingAfterWrite && owner.Writes > 0 ? "missing" : owner.ObservationMode;
                return Task.FromResult(new IndividualAddressObservation(mode == "missing" ? AddressObservationState.NoResponse : mode == "malformed" ? AddressObservationState.MalformedResponse : mode == "multiple" ? AddressObservationState.MultipleAddresses : AddressObservationState.ResponsesObserved,
                    mode == "multiple" ? [owner.Current, new(1, 1, 9)] : mode == "missing" ? [] : [owner.Current], mode == "duplicate" ? 2 : mode == "missing" ? 0 : 1, mode == "malformed" ? 1 : 0, serial, window));
            }
            public Task<AddressOccupancyObservation> ProbeAsync(IndividualAddress address, TimeSpan window, CancellationToken ct)
            { ct.ThrowIfCancellationRequested(); return Task.FromResult(new AddressOccupancyObservation(owner.ObservationMode == "occupied" ? AddressOccupancyState.ResponseObserved : AddressOccupancyState.NoResponseReservationRequired, owner.ObservationMode == "occupied" ? 1 : 0)); }
            public async Task<AddressPhysicalIdentity> ReadIdentityAsync(IndividualAddress address, CancellationToken ct)
            {
                owner.IdentityReads++; if (owner.Writes > 0) { owner.VerifyEntered.TrySetResult(); if (owner.HoldVerification) await owner.ReleaseVerification.Task; }
                ct.ThrowIfCancellationRequested(); if (address != owner.Current) throw new TimeoutException();
                return new(0x0701, "0083", owner.WrongIdentity ? "008309090909" : Serial);
            }
            public Task WriteOnceAsync(AddressSelectionKind kind, string? serial, IndividualAddress address, CancellationToken ct)
            { ct.ThrowIfCancellationRequested(); owner.BeforeWrite?.Invoke(); owner.Writes++; owner.Current = address; if (owner.ThrowAfterWrite) throw new IOException("Synthetic ACK loss"); return Task.CompletedTask; }
            public ValueTask DisposeAsync() { owner.Disposals++; return default; }
        }
    }
    private sealed class NoAckTransport : IUdpTransport
    {
        internal readonly IPEndPoint Peer = new(IPAddress.Loopback, 3671), Local = new(IPAddress.Loopback, 43001);
        private readonly Channel<UdpReceiveResult> _frames = Channel.CreateUnbounded<UdpReceiveResult>();
        internal int Writes;
        public Task SendAsync(IPEndPoint endpoint, byte[] bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var frame = KnxNetIpFrame.Decode(bytes);
            switch (frame.Service)
            {
                case ServiceType.ConnectRequest: Push(ServiceType.ConnectResponse, [7, 0, .. Hpai.Control(Peer), .. Crd.Tunnel(Fixture.Client)]); break;
                case ServiceType.DisconnectRequest: Push(ServiceType.DisconnectResponse, Connection.Response(7)); break;
                case ServiceType.ConnectionStateRequest: Push(ServiceType.ConnectionStateResponse, Connection.Response(7)); break;
                case ServiceType.TunnellingRequest: Writes++; break;
            }
            return Task.CompletedTask;
        }
        private void Push(ServiceType service, byte[] body) => _frames.Writer.TryWrite(new(KnxNetIpFrame.Encode(service, body), Peer));
        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct) => await _frames.Reader.ReadAsync(ct);
        public void Dispose() => _frames.Writer.TryComplete();
    }
}
