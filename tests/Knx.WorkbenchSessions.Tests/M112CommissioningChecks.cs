using System.Net;
using Knx;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Bus;

internal static class M112CommissioningChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        static async Task Until(Func<bool> condition)
        { for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(5); if (!condition()) throw new TimeoutException("M112 test deadline."); }
        await using (var fixture = new Fixture())
        {
            check("M112 construction sends no frame and opens no backend", fixture.Backend.Reads == 0 && fixture.Backend.Applies == 0);
            await fixture.PreviewAsync();
            check("M112 offline preview invokes no identity, apply or recovery transport", fixture.Session.Snapshot.Phase == M112CommissioningPhase.Reviewed
                && fixture.Backend.Prepares == 1 && fixture.Backend.Reads == 0 && fixture.Backend.Applies == 0 && fixture.Backend.Recoveries == 0);
            await fixture.Session.ReadIdentityAsync();
            check("M112 identity is displayed without implicitly confirming it", fixture.Session.Snapshot.Identity == fixture.Backend.Identity && !fixture.Session.Snapshot.IdentityConfirmed);
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 unconfirmed identity cannot start writes", fixture.Backend.Applies == 0 && fixture.Session.Snapshot.Issue == "Confirmation");
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); await fixture.Session.ApplyAsync("1.1.254", fixture.Backend.Review.Fingerprint);
            check("M112 typed target mismatch starts no writes", fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); await fixture.Session.ApplyAsync(fixture.Target, "unreviewed");
            check("M112 fingerprint mismatch starts no writes", fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); fixture.Project.Comment = "changed after review";
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 model drift is refused before writes", fixture.Backend.Applies == 0 && fixture.Session.Snapshot.Issue == "Stale");
            fixture.Session.Invalidate(); await fixture.PreviewAsync();
            check("M112 explicit new preview can bind a changed model in the same lifetime", fixture.Session.Snapshot.Phase == M112CommissioningPhase.Reviewed
                && !fixture.Session.Snapshot.IdentityConfirmed && fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); fixture.Lifetime = new object();
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 same-id replacement lifetime is refused before writes", fixture.Backend.Applies == 0 && fixture.Session.Snapshot.Issue == "Stale");
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); fixture.Drafts = true;
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 private draft appearing after review is refused", fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); await File.AppendAllTextAsync(fixture.BaselinePath, "changed");
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 changed baseline file is refused before writes", fixture.Backend.Applies == 0 && fixture.Session.Snapshot.Issue == "SourceChanged");
        }
        await using (var fixture = new Fixture())
        {
            await fixture.PreviewAsync(); fixture.Backend.Identity = fixture.Backend.Identity with { Serial = "bad" };
            await fixture.Session.ReadIdentityAsync();
            check("M112 malformed physical identity cannot become expected target", fixture.Session.Snapshot.Issue == "Identity" && fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.PreviewAsync(); fixture.Backend.Identity = fixture.Backend.Identity with { Application = "FFFF000000" };
            await fixture.Session.ReadIdentityAsync();
            check("M112 application mismatch is refused after readonly identity", fixture.Session.Snapshot.Issue == "Identity" && fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.PrepareGate = Gate();
            var preview = fixture.PreviewAsync(); await Until(() => fixture.Backend.Prepares == 1);
            fixture.Lifetime = new object(); fixture.Backend.PrepareGate.SetResult(); await preview;
            check("M112 delayed preview cannot bind to a replacement session", fixture.Session.Snapshot.Issue == "Stale" && fixture.Backend.Reads == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.PreviewAsync(); fixture.Backend.ReadGate = Gate(); Task? disposal = null;
            fixture.Session.Changed += snapshot => { if (snapshot.Phase == M112CommissioningPhase.ReadingIdentity) disposal ??= fixture.Session.DisposeAsync().AsTask(); };
            var read = fixture.Session.ReadIdentityAsync();
            check("M112 reentrant observer disposal joins the already-published operation", disposal is { IsCompleted: false } && !read.IsCompleted);
            fixture.Backend.ReadGate.SetResult(); await read; await disposal!;
            check("M112 cancelled late identity cannot become ready after disposal", fixture.Session.Snapshot.Phase == M112CommissioningPhase.Cancelled);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync();
            using var competing = RealEndpointReservations.TryAcquire(fixture.Options.EndpointId);
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 competing window reservation prevents all apply calls", competing is not null && fixture.Backend.Applies == 0 && fixture.Session.Snapshot.Issue == "Occupied");
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); fixture.Backend.WaitForCancellation = true; fixture.Backend.RestoreGate = Gate();
            var applying = fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            await fixture.Backend.ApplyEntered.Task;
            check("M112 applies only after explicit identity, target and fingerprint confirmation", fixture.Backend.Applies == 1 && fixture.Session.IsBusy);
            fixture.Session.Cancel(); await fixture.Backend.RestoreEntered.Task;
            var disposing = fixture.Session.DisposeAsync().AsTask();
            check("M112 cancellation after simulated unload waits for independent restoration", !applying.IsCompleted && !disposing.IsCompleted
                && fixture.Session.Snapshot.Phase == M112CommissioningPhase.Restoring);
            using var competing = RealEndpointReservations.TryAcquire(fixture.Options.EndpointId);
            check("M112 endpoint remains reserved throughout independent restoration", competing is null);
            fixture.Backend.RestoreGate.SetResult(); await applying; await disposing;
            check("M112 cancelled write with verified restoration is never reported as programmed", fixture.Session.Snapshot.Phase == M112CommissioningPhase.FailedRestored
                && fixture.Session.Snapshot.Result is { DownloadedAndVerified: false, OriginalConfigurationRestoredAndVerified: true });
            using var released = RealEndpointReservations.TryAcquire(fixture.Options.EndpointId);
            check("M112 endpoint is reusable only after joined restoration", released is not null);
        }
        foreach (var result in new[] { new M112DownloadResult(true, false, false, "before-image.json", null),
            new M112DownloadResult(false, true, false, "before-image.json", "failure") })
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); fixture.Backend.Result = result;
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            check("M112 typed result " + (result.DownloadedAndVerified ? "Verified" : "RecoveryRequired"),
                fixture.Session.Snapshot.Phase == (result.DownloadedAndVerified ? M112CommissioningPhase.Verified : M112CommissioningPhase.RecoveryRequired)
                && fixture.Session.Snapshot.Result!.RecoveryPath == result.RecoveryPath);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.RecoveryReadyAsync(); fixture.Backend.RestoreGate = Gate(); fixture.Backend.HoldManualRecovery = true;
            var recovery = fixture.RestoreAsync(); await fixture.Backend.RestoreEntered.Task;
            fixture.Session.Cancel(); var disposal = fixture.Session.DisposeAsync().AsTask();
            check("M112 explicitly started resume ignores caller cancellation and joins close", !recovery.IsCompleted && !disposal.IsCompleted);
            fixture.Backend.RestoreGate.SetResult(); await recovery; await disposal;
            check("M112 successful explicit resume has its own verified-restoration result", fixture.Session.Snapshot.Phase == M112CommissioningPhase.Restored);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.ReadyAsync(); fixture.Backend.Result = new(false, true, false, fixture.BaselinePath, "restore failed");
            await fixture.Session.ApplyAsync(fixture.Target, fixture.Backend.Review.Fingerprint);
            var refused = false; try { fixture.Session.ClearReview(); } catch (InvalidOperationException) { refused = true; }
            check("M112 unresolved restoration cannot be erased by clearing preview", refused && fixture.Session.RecoveryRequired
                && fixture.Session.Snapshot.Result?.RecoveryPath == fixture.BaselinePath);
            refused = false; try { await fixture.PreviewAsync(); } catch (InvalidOperationException) { refused = true; }
            check("M112 unresolved restoration cannot be erased by a new preview", refused && fixture.Session.Snapshot.Phase == M112CommissioningPhase.RecoveryRequired);
            await fixture.Session.PrepareRecoveryAsync(Path.Combine(fixture.Root, "missing.json"), fixture.Options);
            check("M112 invalid resume source retains recovery-required state and original path", fixture.Session.RecoveryRequired
                && fixture.Session.Snapshot.Result?.RecoveryPath == fixture.BaselinePath);
            fixture.Session.Invalidate(); fixture.Project.Comment = "changed after failed restoration";
            await fixture.RecoveryReadyAsync(); await fixture.RestoreAsync();
            check("M112 explicit original recovery remains possible after model drift", !fixture.Session.RecoveryRequired
                && fixture.Session.Snapshot.Phase == M112CommissioningPhase.Restored);
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.ApplicationUnavailable = true;
            await fixture.RecoveryReadyAsync();
            check("M112 cold-start recovery needs neither preview nor loaded application identity", fixture.Session.Review is null
                && fixture.Backend.Prepares == 0 && fixture.Backend.Reads == 0 && fixture.Backend.PhysicalReads == 1
                && fixture.Session.Snapshot.Phase == M112CommissioningPhase.ResumeReady);
            await fixture.RestoreAsync();
            check("M112 cold-start recovery executes only after physical and fingerprint confirmation", fixture.Backend.Recoveries == 1
                && fixture.Session.Snapshot.Phase == M112CommissioningPhase.Restored && fixture.Backend.Applies == 0);
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.BadInspection = true;
            await fixture.Session.PrepareRecoveryAsync(fixture.BaselinePath, fixture.Options);
            check("M112 invalid recovery inspection opens no physical or write session", fixture.Backend.PhysicalReads == 0 && fixture.Backend.Recoveries == 0
                && fixture.Session.Snapshot.Resume is null);
        }
        await using (var fixture = new Fixture())
        {
            fixture.Backend.WrongPhysical = true;
            await fixture.Session.PrepareRecoveryAsync(fixture.BaselinePath, fixture.Options); await fixture.Session.ReadRecoveryIdentityAsync();
            check("M112 mismatched recovery physical identity cannot become confirmed", fixture.Session.Snapshot.Issue == "Identity"
                && !fixture.Session.Snapshot.IdentityConfirmed && fixture.Backend.Recoveries == 0);
        }
        await using (var fixture = new Fixture())
        {
            await fixture.RecoveryReadyAsync(); await fixture.Session.RecoverAsync("1.1.254", fixture.Session.Snapshot.Fingerprint!);
            check("M112 recovery wrong target starts no writes", fixture.Backend.Recoveries == 0);
        }
        await using (var fixture = new Fixture())
        {
            var git = Path.Combine(fixture.Root, "checkout"); Directory.CreateDirectory(Path.Combine(git, ".git"));
            var source = Path.Combine(git, "before.knxproj"); await File.WriteAllTextAsync(source, "fixture");
            var refused = false; try { M112PrivatePaths.ExistingFile(source); } catch (IOException) { refused = true; }
            check("M112 private project path rejects a Git checkout", refused);
            refused = false; try { M112PrivatePaths.ExistingDirectory("relative"); } catch (IOException) { refused = true; }
            check("M112 journal requires an existing absolute private directory", refused);
        }
        check("M112 all eight languages have complete nonempty values", M112CommissioningStrings.Languages.Count == 8
            && M112CommissioningStrings.Languages.All(language => M112CommissioningStrings.Keys.All(key => !string.IsNullOrWhiteSpace(M112CommissioningStrings.GetForLanguage(language, key)))));
    }

    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "buschord-m112-ui-" + Guid.NewGuid().ToString("N"));
        internal Project Project = Knx.Workbench.WorkbenchJson.CreateDemo();
        internal object Lifetime = new();
        internal bool Drafts;
        internal string BaselinePath { get; }
        internal string Hash { get; }
        internal string Target { get; }
        internal ReadOnlyConnectionOptions Options { get; } = new(IPAddress.Parse("192.0.2.190"), 3671, new(1, 1, 250));
        internal FakeBackend Backend { get; }
        internal M112CommissioningSession Session { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root); BaselinePath = Path.Combine(Root, "before.knxproj"); File.WriteAllText(BaselinePath, "synthetic baseline stamp");
            Hash = M112PrivatePaths.HashAsync(BaselinePath).GetAwaiter().GetResult();
            var device = ProjectQuery.DeviceList(Project).First(); Target = device.Address.ToString(); Backend = new(Target);
            Session = new(() => new(Project, Lifetime, Drafts, false), device.Id, Backend);
        }
        internal Task PreviewAsync() => Session.PrepareAsync(Project, BaselinePath, Hash, Options, Root);
        internal async Task ReadyAsync() { await PreviewAsync(); await Session.ReadIdentityAsync(); Session.ConfirmIdentity(true); }
        internal async Task RecoveryReadyAsync() { await Session.PrepareRecoveryAsync(BaselinePath, Options); await Session.ReadRecoveryIdentityAsync(); Session.ConfirmIdentity(true); }
        internal Task RestoreAsync() => Session.RecoverAsync(Target, Session.Snapshot.Fingerprint!);
        public async ValueTask DisposeAsync() { Backend.PrepareGate?.TrySetResult(); Backend.ReadGate?.TrySetResult(); Backend.RestoreGate?.TrySetResult(); await Session.DisposeAsync(); Directory.Delete(Root, true); }
    }
    internal sealed class FakeReview(string target) : IM112PreparedChange
    {
        public string Target => target;
        public string Fingerprint => new('A', 64);
        public string ExpectedManufacturer => "0083";
        public string ExpectedApplication => "0083000110";
        public IReadOnlyList<M112ReviewRange> Ranges { get; } = [new(0x4001, 1, "00", "01", "FF", false)];
        public bool IsCurrent() => true;
    }
    internal sealed class FakeBackend(string target) : IM112CommissioningBackend
    {
        internal readonly FakeReview Review = new(target);
        internal ManagementDeviceIdentity Identity = new(target, 0x0701, "0083", "008301234567", "0083000110");
        internal int Prepares, Reads, Applies, Recoveries, PhysicalReads;
        internal TaskCompletionSource? PrepareGate, ReadGate, RestoreGate;
        internal TaskCompletionSource ApplyEntered = Gate(), RestoreEntered = Gate();
        internal bool WaitForCancellation, HoldManualRecovery;
        internal bool ApplicationUnavailable, BadInspection, WrongPhysical;
        internal M112DownloadResult Result = new(true, false, false, "before-image.json", null);
        public async Task<IM112PreparedChange> PrepareAsync(Project baseline, DeviceInstance baselineDevice, Project desired, DeviceInstance desiredDevice, CancellationToken token)
        { Prepares++; if (PrepareGate is not null) await PrepareGate.Task; return Review; }
        public async Task<ManagementDeviceIdentity> ReadIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress selected, CancellationToken token)
        { Reads++; if (ApplicationUnavailable) throw new IOException("Application is unloaded."); if (ReadGate is not null) await ReadGate.Task; return Identity; }
        public async Task<M112RecoveryInspection> InspectRecoveryAsync(string path, CancellationToken token)
        {
            if (BadInspection) throw new InvalidDataException("Checksum mismatch.");
            return new(path, await M112PrivatePaths.HashAsync(path, token), Identity, new string('A', 64), new string('C', 64), []);
        }
        public Task<M112PhysicalIdentity> ReadPhysicalIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress selected, CancellationToken token)
        { PhysicalReads++; return Task.FromResult(new M112PhysicalIdentity(Identity.Address, Identity.MaskVersion, Identity.Manufacturer,
            WrongPhysical ? "008300000000" : Identity.Serial, new string('C', 64))); }
        public async Task<M112DownloadResult> ApplyAsync(ReadOnlyConnectionOptions options, IM112PreparedChange review, ManagementDeviceIdentity expected,
            string fingerprint, string journalRoot, Action<string> progress, CancellationToken token)
        {
            Applies++; ApplyEntered.TrySetResult();
            if (!WaitForCancellation) return Result;
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            progress("restoring"); RestoreEntered.TrySetResult(); if (RestoreGate is not null) await RestoreGate.Task;
            return new(false, true, true, "before-image.json", "cancelled");
        }
        public async Task<M112DownloadResult> RecoverAsync(ReadOnlyConnectionOptions options, M112RecoveryInspection inspection,
            string selected, Action<string> progress, CancellationToken token)
        {
            Recoveries++; RestoreEntered.TrySetResult(); if (HoldManualRecovery && RestoreGate is not null) await RestoreGate.Task.WaitAsync(token);
            return new(false, true, true, inspection.RecoveryPath, null);
        }
    }
}
