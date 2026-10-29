using System.Text.Json.Nodes;
using Knx;
using Knx.App.Workbench;
using Knx.Workbench;

internal static class VerifiedM112ReceiptChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        await using var f = new RealM112ProgrammingQueueChecks.Fixture();
        var target = Path.Combine(f.Root, "target.knxproj"); await File.WriteAllTextAsync(target, "synthetic source bytes for fake-only receipt test");
        var binding = ProgrammingStateBinding.Create(f.Project, target)!; var source = await M112PrivatePaths.HashAsync(target); var model = VerifiedM112ReceiptStore.ModelFingerprint(f.Project);
        var directory = Path.Combine(f.Root, "receipts");
        var normalized = Knxproj.FromXml(Knxproj.ToXml(f.Project));
        check("M112 receipt model fingerprint follows native serializer/parser roundtrip", VerifiedM112ReceiptStore.ModelFingerprint(normalized) == model);
        f.Devices[0].ParameterValues["LiteralSpace"] = "   ";
        check("native receipt normalization preserves literal parameter whitespace", VerifiedM112ReceiptStore.ModelFingerprint(f.Project) != model);
        f.Devices[0].ParameterValues.Remove("LiteralSpace");
        VerifiedM112ReceiptStore Store(ProgrammingStateBinding? owner = null, string? sourceHash = null) => new(directory, owner ?? binding, sourceHash ?? source, model,
            (id, path, ct) => f.Backends.Single(b => b.Device.Id == id).InspectRecoveryAsync(path, ct));
        var store = Store();
        check("M112 receipt construction and empty history create no files or network", !Directory.Exists(directory)
            && (await store.ReadHistoryAsync(f.Project)).Count == 0 && f.Backends.All(b => b.Reads + b.Applies == 0));
        await f.Queue.DisposeAsync(); f.Queue = new(() => new(f.Project, f.Lifetime, false, false), f.Programming,
            id => f.Backends.Single(b => b.Device.Id == id), store);
        await f.EnqueueBoth(); await f.ReadyBoth(); await f.Queue.RunAsync(f.Queue.Snapshot.BatchFingerprint);
        check("only fully verified real jobs persist separate M112 receipts", f.Queue.Snapshot.Jobs.All(j => j.State == RealM112JobState.Verified)
            && Directory.GetFiles(directory, "*.json").Length == 2 && Directory.GetFiles(directory, "*.tmp").Length == 0);
        if (!OperatingSystem.IsWindows()) check("M112 receipt directory/file modes exclude other users", File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            && Directory.GetFiles(directory, "*.json").All(file => !OperatingSystem.IsWindows() && File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)));
        var history = await store.ReadHistoryAsync(f.Project);
        check("reopened receipt store restores historical Prg/Par/Grp without network", history.Count == 2 && history.All(s => s.Origin == ProgrammingOrigin.Real
            && s.Flags == (DeviceLoadFlags.ApplicationProgram | DeviceLoadFlags.Parameters | DeviceLoadFlags.GroupAddresses)) && f.Backends.All(b => b.Reads == 1 && b.Applies == 1));
        var failLive = true;
        await using (var session = new ProgrammingSession(new(), () => f.Project,
            realExecutor: (_, _) => failLive ? Task.FromException(new IOException("Synthetic newer live attempt")) : Task.CompletedTask))
        await using (var reopened = new RealM112ProgrammingQueue(() => new(f.Project, f.Lifetime, false, false), session, receipts: store))
        {
            var demoDirectory = Path.Combine(f.Root, "untouched-demo-history");
            using var persistence = new ProgrammingStatePersistence(session, new(demoDirectory), f.Project, target);
            await reopened.ReadHistoryAsync();
            check("new queue session projects verified history without execution authority", session.DeviceStates.Count == 2
                && session.DeviceStates.Values.All(s => !s.IsFullyLoaded) && reopened.Snapshot.Jobs.Count == 0 && !Knx.Bus.KnxBus.WritesEnabled);
            check("opening verified history never creates or rewrites DEMO metadata", !Directory.Exists(demoDirectory));
            var validReceipt = await File.ReadAllBytesAsync(store.PathFor(f.Devices[0].Id));
            await store.InvalidateBeforeAttemptAsync(f.Project, f.Devices[0].Id);
            await reopened.ReadHistoryAsync();
            check("same-session reopen withdraws invalidated receipt flags", !session.DeviceStates.ContainsKey(f.Devices[0].Id)
                && session.DeviceStates.Count == 1 && !Directory.Exists(demoDirectory));
            await File.WriteAllBytesAsync(store.PathFor(f.Devices[0].Id), validReceipt);
            await reopened.ReadHistoryAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedStore = new VerifiedM112ReceiptStore(directory, binding, source, model, async (_, _, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
            await using var revalidation = new RealM112ProgrammingQueue(() => new(f.Project, f.Lifetime, false, false), session, receipts: blockedStore);
            var pending = revalidation.ReadHistoryAsync(); await entered.Task;
            check("pending history revalidation has no stale receipt projection", session.DeviceStates.Count == 0);
            revalidation.Cancel(); await pending;
            check("cancelled history revalidation cannot retain old receipt flags", session.DeviceStates.Count == 0 && !Directory.Exists(demoDirectory));
            await reopened.ReadHistoryAsync();
            session.Enqueue(f.Devices[0].Id, "parameters", "synthetic-real-executor", SessionOrigin.Real); await session.RunAsync();
            var newerFailure = session.DeviceState(f.Devices[0].Id); await reopened.ReadHistoryAsync();
            check("history revalidation preserves a newer live failure object", ReferenceEquals(newerFailure, session.DeviceState(f.Devices[0].Id))
                && newerFailure?.LastAttemptOrigin == ProgrammingOrigin.Real && newerFailure.LastError is not null);
            failLive = false; session.Enqueue(f.Devices[0].Id, "parameters", "synthetic-real-executor", SessionOrigin.Real); await session.RunAsync();
            var newerSuccess = session.DeviceState(f.Devices[0].Id); await reopened.ReadHistoryAsync();
            check("history revalidation preserves a newer live success object", ReferenceEquals(newerSuccess, session.DeviceState(f.Devices[0].Id))
                && newerSuccess?.LastAttemptOrigin == ProgrammingOrigin.Real && newerSuccess.LastError is null);
            session.ResetForChange(f.Devices[0].Id, DeviceChangeKind.Parameters);
            var newerReset = session.DeviceState(f.Devices[0].Id); await reopened.ReadHistoryAsync();
            check("history revalidation never replaces a newer explicit flag reset", ReferenceEquals(newerReset, session.DeviceState(f.Devices[0].Id))
                && newerReset is not null && !newerReset.Flags.HasFlag(DeviceLoadFlags.Parameters));
            var brokenStore = new VerifiedM112ReceiptStore(directory, binding, source, model, (_, _, _) => throw new IOException("Synthetic inspection failure"));
            await using var failedValidation = new RealM112ProgrammingQueue(() => new(f.Project, f.Lifetime, false, false), session, receipts: brokenStore);
            await failedValidation.ReadHistoryAsync();
            check("failed history inspection removes only old projections and preserves newer state", session.DeviceStates.Count == 1
                && ReferenceEquals(newerReset, session.DeviceState(f.Devices[0].Id)));
        }
        var original = await File.ReadAllBytesAsync(store.PathFor(f.Devices[0].Id));
        var recoveryAttempt = Path.Combine(f.Root, "later-recovery.jsonl"); await File.WriteAllTextAsync(recoveryAttempt, "synthetic later recovery attempt");
        check("a later recovery journal invalidates past desired-image flags without timestamps", (await store.ReadHistoryAsync(f.Project)).Count == 0);
        File.Delete(recoveryAttempt);
        await File.WriteAllTextAsync(store.PathFor(f.Devices[0].Id), "{");
        check("corrupt receipt contributes no flags for that device", (await store.ReadHistoryAsync(f.Project)).All(s => s.DeviceId != f.Devices[0].Id));
        await File.WriteAllBytesAsync(store.PathFor(f.Devices[0].Id), new byte[65537]);
        check("oversize receipt is refused before parsing", (await store.ReadHistoryAsync(f.Project)).All(s => s.DeviceId != f.Devices[0].Id));
        var tampered = JsonNode.Parse(original)!.AsObject(); tampered["Payload"]!["Receipt"]!["PlanFingerprint"] = new string('0', 64);
        await File.WriteAllTextAsync(store.PathFor(f.Devices[0].Id), tampered.ToJsonString());
        check("tampered plan metadata cannot revive historical flags", (await store.ReadHistoryAsync(f.Project)).All(s => s.DeviceId != f.Devices[0].Id));
        await File.WriteAllBytesAsync(store.PathFor(f.Devices[0].Id), original);
        f.Backends[0].BadInspection = true;
        check("checksum-rejected cold source contributes no historical flags", (await store.ReadHistoryAsync(f.Project)).All(s => s.DeviceId != f.Devices[0].Id));
        f.Backends[0].BadInspection = false;
        var journal = await File.ReadAllBytesAsync(f.RecoveryPath); File.Delete(f.RecoveryPath);
        check("missing cold source contributes no historical flags", (await store.ReadHistoryAsync(f.Project)).Count == 0);
        await File.WriteAllBytesAsync(f.RecoveryPath, journal);
        var oldComment = f.Project.Comment; f.Project.Comment = "changed model";
        check("changed model never imports a matching old file's history", (await store.ReadHistoryAsync(f.Project)).Count == 0); f.Project.Comment = oldComment;
        await File.AppendAllTextAsync(target, "corrupted replacement");
        check("changed target bytes never import a matching old model's history", (await store.ReadHistoryAsync(f.Project)).Count == 0);
        check("a new reader cannot reinterpret changed source bytes as old proof", (await Store(sourceHash: await M112PrivatePaths.HashAsync(target)).ReadHistoryAsync(f.Project)).Count == 0);
        await File.WriteAllTextAsync(target, "synthetic source bytes for fake-only receipt test");
        var copy = Path.Combine(f.Root, "other-target.knxproj"); File.Copy(target, copy);
        check("same project id and bytes at another path do not inherit receipts", (await Store(ProgrammingStateBinding.Create(f.Project, copy)).ReadHistoryAsync(f.Project)).Count == 0);
        await store.InvalidateBeforeAttemptAsync(f.Project, f.Devices[0].Id);
        check("pre-attempt tombstone durably removes earlier success before any mutation", (await Store().ReadHistoryAsync(f.Project)).All(s => s.DeviceId != f.Devices[0].Id));
        var demoStore = new ProgrammingStateStore(Path.Combine(f.Root, "demo")); var rejected = false;
        try { demoStore.Save(binding, history); } catch (FormatException) { rejected = true; }
        check("real receipts never widen the old DEMO-only store format", rejected);
        var refused = false; Directory.CreateDirectory(Path.Combine(f.Root, "checkout", ".git"));
        try { _ = new VerifiedM112ReceiptStore(Path.Combine(f.Root, "checkout", "receipts"), binding, source, model); } catch (IOException) { refused = true; }
        check("real receipt paths remain private and outside Git", refused);
    }
}
