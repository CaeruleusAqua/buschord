using System.Net;
using System.Security.Cryptography;
using Knx;
using Knx.App;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Workbench;
using Knx.App.Localization;

internal static class RealM112ProgrammingQueueChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        await using (var f = new Fixture())
        {
            f.Drafts = true; await f.Enqueue(f.Devices[0].Id);
            check("real queue unapplied drafts block planning before backend access", f.Queue.Snapshot.Jobs.Count == 0 && f.Backends[0].Prepares == 0);
            f.Drafts = false; f.ExternalBusy = true; await f.Enqueue(f.Devices[0].Id);
            check("real queue conflicting session blocks planning before backend access", f.Queue.Snapshot.Jobs.Count == 0 && f.Backends[0].Prepares == 0);
        }
        await using (var f = new Fixture())
        {
            check("real queue construction has no I/O", f.Backends.All(b => b.Reads + b.Applies == 0));
            await f.EnqueueBoth();
            check("real queue enqueue compiles offline without opening/writing", f.Queue.Snapshot.Jobs.Count == 2 && f.Backends.All(b => b.Prepares == 1 && b.Reads + b.Applies == 0));
            await f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint);
            check("real queue unconfirmed review never writes", f.Backends.All(b => b.Applies == 0));
            await f.ReadyBoth(); var before = f.Queue.Snapshot.BatchFingerprint;
            var first = f.Queue.Snapshot.Jobs[0]; var refused = false;
            try { f.Queue.Confirm(first.Id, "1.1.254", first.Fingerprint!, true); } catch (InvalidOperationException) { refused = true; }
            check("real queue requires the exact per-device target", refused);
            f.Queue.Confirm(first.Id, first.Address, first.Fingerprint!, false);
            await f.Queue.RunAsync(before);
            check("real queue old batch review cannot bypass changed confirmations", f.Backends.All(b => b.Applies == 0));
        }
        await using (var f = new Fixture())
        {
            foreach (var device in f.Devices) f.Programming.Enqueue(device.Id);
            await f.Programming.RunAsync();
            check("queue fixture starts with explicitly simulated full flags", f.Devices.All(d => f.Programming.DeviceState(d.Id) is { Origin: ProgrammingOrigin.Demo, Flags: DeviceLoadFlags.All }));
            await f.EnqueueBoth(); await f.ReadyBoth();
            await f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint);
            check("real queue verifies two devices strictly serially", f.Order.SequenceEqual(f.Devices.Select(d => d.Id)) && f.MaximumConcurrent == 1 && f.Queue.Snapshot.Jobs.All(j => j.State == RealM112JobState.Verified));
            check("verified M112 only promotes proven Prg/Par/Grp flags", f.Devices.All(d => f.Programming.DeviceState(d.Id) is
                { Origin: ProgrammingOrigin.Real, Flags: DeviceLoadFlags.ApplicationProgram | DeviceLoadFlags.Parameters | DeviceLoadFlags.GroupAddresses }));
            check("real queue writes a durable batch review and result audit", Directory.GetFiles(f.Root, "batch.jsonl", SearchOption.AllDirectories).Single() is { } file
                && File.ReadLines(file).Count() == 3 && File.ReadAllText(file).Contains("Verified"));
        }
        await using (var f = new Fixture())
        {
            f.Backends[1].Unsupported = true; await f.EnqueueBoth();
            check("unsupported device is individually blocked with no writes", f.Queue.Snapshot.Jobs[1] is { State: RealM112JobState.Blocked, Issue: "UnsupportedProfile" } && f.Backends.All(b => b.Applies == 0));
            await f.Queue.ReadIdentityAsync(f.Queue.Snapshot.Jobs[0].Id); f.Confirm(0);
            await f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint);
            check("batch cannot hide a blocked device behind a supported first job", f.Backends.All(b => b.Applies == 0));
        }
        await using (var f = new Fixture())
        {
            await f.EnqueueBoth(); await f.ReadyBoth(); await File.AppendAllTextAsync(f.BaselinePath, "changed");
            await f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint);
            check("changed baseline file refuses before first write", f.Backends.All(b => b.Applies == 0) && f.Queue.Snapshot.Jobs[0].Issue == "SourceChanged");
        }
        await using (var f = new Fixture())
        {
            await f.EnqueueBoth(); await f.ReadyBoth(); f.Backends[0].ApplyGate = Gate();
            var running = f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint); await f.Backends[0].Entered.Task;
            check("real queue records no success before verification completes", f.Programming.DeviceStates.Count == 0 && !running.IsCompleted);
            f.Project.Comment = "changed while device was verifying"; f.Backends[0].ApplyGate!.SetResult(); await running;
            check("verified device with changed model receives no programming flags or following job", f.Queue.Snapshot.Jobs[0].State == RealM112JobState.VerifiedStale
                && f.Programming.DeviceStates.Count == 0 && f.Backends[1].Applies == 0);
        }
        await using (var f = new Fixture())
        {
            await f.EnqueueBoth(); await f.ReadyBoth(); f.Backends[0].WaitForCancellation = true; f.Backends[0].RestoreGate = Gate();
            f.Backends[0].OnCancel = () => throw new InvalidOperationException("Synthetic throwing cancellation observer");
            var run = f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint); await f.Backends[0].Entered.Task;
            f.Queue.Cancel(); await f.Backends[0].RestoreEntered.Task;
            var closing = f.Queue.DisposeAsync().AsTask(); var again = f.Queue.DisposeAsync().AsTask();
            check("real queue cancellation and repeated disposal share joined independent recovery", !run.IsCompleted && !closing.IsCompleted && ReferenceEquals(closing, again)
                && f.Queue.Snapshot.Jobs[0].State == RealM112JobState.Restoring && f.Backends[0].OwnedKey.Any(b => b != 0));
            f.Backends[0].RestoreGate!.SetResult(); await run; await closing;
            check("real queue throwing cancellation observer cannot skip joined restore/keyzero or start later jobs", f.Backends[0].OwnedKey.All(b => b == 0)
                && f.Backends[1].Applies == 0 && f.Programming.DeviceStates.Values.All(s => s.Flags == DeviceLoadFlags.None)
                && f.Queue.Snapshot.Jobs[0].State == RealM112JobState.Restored);
        }
        foreach (var unavailable in new[] { false, true })
        {
            await using var f = new Fixture(); f.Backends[0].Unresolved = true;
            await f.EnqueueBoth(); await f.ReadyBoth(); await f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint);
            check("unverified recovery remains unresolved and blocks success/next job " + unavailable, f.Queue.RecoveryRequired && !f.Queue.MayLeave
                && f.Queue.Snapshot.Jobs[0].State == RealM112JobState.RecoveryRequired && f.Backends[1].Applies == 0);
            f.Backends[0].BadInspection = unavailable;
            await f.Queue.ReviewUnresolvedExitAsync(); var review = f.Queue.ExitReview!;
            check("offline exit review exposes available versus unavailable recovery evidence " + unavailable, review.Evidence.Single().Available == !unavailable && f.Backends[0].Recoveries == 0);
            await f.Queue.ConfirmUnresolvedExitAsync(review.Fingerprint);
            check("conscious unknown-state exit is possible after drain even with unavailable evidence " + unavailable, f.Queue.MayLeave && f.Queue.RecoveryRequired
                && f.Programming.DeviceStates.Values.All(s => s.Flags == DeviceLoadFlags.None) && f.Backends[0].OwnedKey.All(b => b == 0));
            await f.Queue.DisposeAsync();
            if (!unavailable)
            {
                var lifetime = new object(); await using var resume = new M112CommissioningSession(() => new(null, lifetime, false, false), "", f.Backends[0], recoveryOnly: true);
                await resume.PrepareRecoveryAsync(f.RecoveryPath, f.Options); await resume.ReadRecoveryIdentityAsync(); resume.ConfirmIdentity(true);
                await resume.RecoverAsync(f.Devices[0].Address.ToString(), resume.Snapshot.Fingerprint!);
                check("cold resume reuses the existing physical-identity and file-bound recovery gates", resume.Snapshot.Phase == M112CommissioningPhase.Restored && f.Backends[0].Recoveries == 1);
            }
        }
        check("real queue has complete nonempty text in all eight languages", RealProgrammingQueueStrings.Languages.Count == 8
            && RealProgrammingQueueStrings.Languages.All(language => RealProgrammingQueueStrings.Keys.All(key => !string.IsNullOrWhiteSpace(RealProgrammingQueueStrings.GetForLanguage(language, key)))));
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "buschord-real-queue-" + Guid.NewGuid().ToString("N"));
        internal string BaselinePath => Path.Combine(Root, "baseline.knxproj");
        internal string RecoveryPath => Path.Combine(Root, "before-image.json");
        internal Project Project = WorkbenchJson.CreateDemo(); internal object Lifetime = new(); internal bool Drafts, ExternalBusy;
        internal DeviceInstance[] Devices;
        internal Backend[] Backends;
        internal ProgrammingSession Programming;
        internal RealM112ProgrammingQueue Queue;
        internal ReadOnlyConnectionOptions Options = new(IPAddress.Parse("192.0.2.191"), 3671, new(1, 1, 250));
        internal List<string> Order = []; internal int Concurrent, MaximumConcurrent;
        internal Fixture()
        {
            Directory.CreateDirectory(Root); File.WriteAllText(BaselinePath, "synthetic offline baseline"); File.WriteAllText(RecoveryPath, "synthetic before-image for fake inspector");
            Devices = ProjectQuery.DeviceList(Project).Take(2).ToArray(); Backends = Devices.Select(d => new Backend(this, d)).ToArray();
            Programming = new(new(), () => Project, demoExecutor: (_, _) => Task.CompletedTask);
            Queue = new(() => new(Project, Lifetime, Drafts, ExternalBusy || Programming.IsRunning), Programming, id => Backends.Single(b => b.Device.Id == id));
        }
        internal async Task EnqueueBoth() { foreach (var d in Devices) await Enqueue(d.Id); }
        internal async Task Enqueue(string id) => await Queue.EnqueueAsync(id, new(Project, BaselinePath, await M112PrivatePaths.HashAsync(BaselinePath)), Options, Root);
        internal async Task ReadyBoth() { for (var i = 0; i < Queue.Snapshot.Jobs.Count; i++) { await Queue.ReadIdentityAsync(Queue.Snapshot.Jobs[i].Id); Confirm(i); } }
        internal void Confirm(int index) { var j = Queue.Snapshot.Jobs[index]; Queue.Confirm(j.Id, j.Address, j.Fingerprint!, true); }
        public async ValueTask DisposeAsync()
        { foreach (var b in Backends) { b.ApplyGate?.TrySetResult(); b.RestoreGate?.TrySetResult(); } await Queue.DisposeAsync(); await Programming.DisposeAsync(); Directory.Delete(Root, true); }
    }
    internal sealed class Review(Fixture f, DeviceInstance device) : IM112PreparedChange
    {
        private readonly string _hash = ProjectRevisions.ContentHash(f.Project);
        public string Target => device.Address.ToString(); public string Fingerprint => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(device.Id + _hash)));
        public string ExpectedManufacturer => "0083"; public string ExpectedApplication => "0083000110";
        public IReadOnlyList<M112ReviewRange> Ranges => [new(0x4420, 1, "00", "01", "FF", false)];
        public bool IsCurrent() => _hash == ProjectRevisions.ContentHash(f.Project);
    }
    internal sealed class Backend(Fixture f, DeviceInstance device) : IM112CommissioningBackend
    {
        internal DeviceInstance Device = device; internal int Prepares, Reads, Applies, Recoveries;
        internal bool Unsupported, WaitForCancellation, Unresolved, BadInspection;
        internal Action? OnCancel;
        internal TaskCompletionSource? ApplyGate, RestoreGate;
        internal TaskCompletionSource Entered = Gate(), RestoreEntered = Gate(); internal byte[] OwnedKey = [];
        internal ManagementDeviceIdentity Identity => new(Device.Address.ToString(), 0x0701, "0083", "008301234567", "0083000110");
        public Task<IM112PreparedChange> PrepareAsync(Project baseline, DeviceInstance baselineDevice, Project desired, DeviceInstance desiredDevice, CancellationToken token)
        { Prepares++; if (Unsupported) throw new NotSupportedException("Synthetic unsupported application"); return Task.FromResult<IM112PreparedChange>(new Review(f, Device)); }
        public Task<ManagementDeviceIdentity> ReadIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token)
        { Reads++; return Task.FromResult(Identity); }
        public async Task<M112RecoveryInspection> InspectRecoveryAsync(string path, CancellationToken token)
        { if (BadInspection) throw new InvalidDataException("Synthetic missing/checksum-rejected evidence"); return new(path, await M112PrivatePaths.HashAsync(path, token), Identity, new('A', 64), new('C', 64), []); }
        public Task<M112PhysicalIdentity> ReadPhysicalIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token)
            => Task.FromResult(new M112PhysicalIdentity(Identity.Address, Identity.MaskVersion, Identity.Manufacturer, Identity.Serial, new('C', 64)));
        public async Task<M112DownloadResult> ApplyAsync(ReadOnlyConnectionOptions options, IM112PreparedChange review, ManagementDeviceIdentity expected,
            string fingerprint, string journalRoot, Action<string> progress, CancellationToken token)
        {
            Applies++; f.Order.Add(Device.Id); f.MaximumConcurrent = Math.Max(f.MaximumConcurrent, ++f.Concurrent); OwnedKey = Enumerable.Repeat((byte)77, 16).ToArray();
            try
            {
                if (WaitForCancellation) { using var observer = token.Register(() => OnCancel?.Invoke()); Entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { }
                    progress("restoring"); RestoreEntered.TrySetResult(); if (RestoreGate is not null) await RestoreGate.Task;
                    return new(false, true, !Unresolved, f.RecoveryPath, "Synthetic cancellation"); }
                Entered.TrySetResult(); if (ApplyGate is not null) await ApplyGate.Task;
                return Unresolved ? new(false, true, false, f.RecoveryPath, "Synthetic failed recovery") : new(true, false, false, f.RecoveryPath, null);
            }
            finally { CryptographicOperations.ZeroMemory(OwnedKey); f.Concurrent--; }
        }
        public Task<M112DownloadResult> RecoverAsync(ReadOnlyConnectionOptions options, M112RecoveryInspection inspection, string target, Action<string> progress, CancellationToken token)
        { Recoveries++; return Task.FromResult(new M112DownloadResult(false, true, true, inspection.RecoveryPath, null)); }
    }
}
