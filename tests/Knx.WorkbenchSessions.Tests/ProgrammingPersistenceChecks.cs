using System.Text.Json.Nodes;
using Knx;
using Knx.App.Workbench;
using Knx.Workbench;

internal static class ProgrammingPersistenceChecks
{
    public static async Task Run(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-demo-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, bool ok) => check("DEMO persistence: " + name, ok);
        var project = WorkbenchJson.CreateDemo(); project.Guid = "763cc171-d622-48fe-b2f5-f9f40914f6db";
        var path = Path.Combine(root, "project.knxproj");
        var binding = ProgrammingStateBinding.Create(project, path)!;
        var store = new ProgrammingStateStore(Path.Combine(root, "facts"));
        var device = ProjectQuery.DeviceList(project).First();
        var state = DeviceProgrammingState.Empty(device.Id).ApplySuccess(DeviceLoadFlags.All, "frozen-signature",
            DateTimeOffset.UtcNow, "demo-ip", ProgrammingOrigin.Demo);
        try
        {
            store.Save(binding, [state]);
            var file = store.PathFor(binding); var original = File.ReadAllBytes(file);
            Check("versioned state restores exact DEMO execution source", store.TryLoad(binding, out var json, out _) &&
                DeviceProgrammingStates.Parse(json!, project.Id)[device.Id] == state);
            Check("only metadata is written", !File.ReadAllText(file).Contains("PayloadJson", StringComparison.Ordinal)
                && !File.ReadAllText(file).Contains("ParameterValues", StringComparison.Ordinal));
            var envelope = JsonNode.Parse(original)!;
            foreach (var field in new[] { "ProjectId", "ProjectGuid", "ProjectPath", "Version", "Kind" })
            {
                var foreign = envelope.DeepClone();
                if (field == "Version") foreign[field] = 99; else foreign[field] = "different";
                File.WriteAllText(file, foreign.ToJsonString());
                Check(field + " mismatch cannot restore state", !store.TryLoad(binding, out var refused, out var error) && refused is null && error is not null);
            }
            File.WriteAllText(file, "{" + "\"Version\":1," + System.Text.Encoding.UTF8.GetString(original)[1..]);
            Check("duplicate JSON properties are rejected", !store.TryLoad(binding, out _, out _));
            var excessive = envelope.DeepClone();
            excessive["States"]!["Devices"] = new JsonArray(Enumerable.Range(0, ProgrammingStateStore.MaximumDevices + 1)
                .Select(_ => (JsonNode?)new JsonObject()).ToArray());
            File.WriteAllText(file, excessive.ToJsonString());
            Check("entry count is bounded before domain decoding", !store.TryLoad(binding, out _, out _));
            File.WriteAllBytes(file, new byte[ProgrammingStateStore.MaximumBytes + 1]);
            Check("file size is bounded before JSON parsing", !store.TryLoad(binding, out _, out _));
            File.WriteAllBytes(file, original);
            var rejected = false;
            try { store.Save(binding, [state with { Origin = ProgrammingOrigin.Real }]); }
            catch (FormatException) { rejected = true; }
            Check("a real origin cannot be silently persisted as DEMO", rejected && File.ReadAllBytes(file).SequenceEqual(original));
            rejected = false;
            try { store.Save(binding, [state with { Attempts = -1 }]); }
            catch (FormatException) { rejected = true; }
            Check("invalid writes preserve the prior file atomically", rejected && File.ReadAllBytes(file).SequenceEqual(original)
                && Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length == 0);
            var sameIdOtherGuid = new Project { Id = project.Id, Name = "Other", Guid = "7d694c48-683a-4f0a-949e-a29498454d7f" };
            Check("same project id with another GUID or path has a distinct store key",
                store.PathFor(ProgrammingStateBinding.Create(sameIdOtherGuid, path)!) != file
                && store.PathFor(ProgrammingStateBinding.Create(project, path + ".other")!) != file);

            var liveStore = new ProgrammingStateStore(Path.Combine(root, "live"));
            var livePath = Path.Combine(root, "live.knxproj"); var liveBinding = ProgrammingStateBinding.Create(project, livePath)!;
            var failure = false; var errors = new List<string>();
            await using var session = new ProgrammingSession(new InterfaceCoordinator(), () => project,
                demoExecutor: (_, _) => failure ? Task.FromException(new InvalidOperationException("synthetic attempt failure")) : Task.CompletedTask);
            using var persistence = new ProgrammingStatePersistence(session, liveStore, project, null, errors.Add);
            session.Enqueue(device.Id); await session.RunAsync();
            Check("unsaved projects remain memory-only", !Directory.Exists(Path.Combine(root, "live")) && session.Downloaded.Single().Origin == SessionOrigin.Demo);
            persistence.Rebind(project, livePath);
            Check("first Save As persists the existing simulation", liveStore.TryLoad(liveBinding, out _, out _) && session.DeviceState(device.Id)!.Attempts == 1);
            failure = true; session.Enqueue(device.Id); await session.RunAsync();
            Check("failed attempts persist without replacing the successful comparison", liveStore.TryLoad(liveBinding, out json, out _)
                && DeviceProgrammingStates.Parse(json!, project.Id)[device.Id] is { Attempts: 2, LastError: "synthetic attempt failure", Origin: ProgrammingOrigin.Demo }
                && session.Downloaded.Count == 1);
            session.ResetForChange(device.Id, DeviceChangeKind.Parameters);
            Check("partial reset flags are persisted", liveStore.TryLoad(liveBinding, out json, out _)
                && !DeviceProgrammingStates.Parse(json!, project.Id)[device.Id].Flags.HasFlag(DeviceLoadFlags.Parameters));
            var nextPath = Path.Combine(root, "saved-as.knxproj"); persistence.Rebind(project, nextPath);
            var nextBinding = ProgrammingStateBinding.Create(project, nextPath)!;
            Check("Save As carries facts while retaining the previous project's side file", liveStore.TryLoad(nextBinding, out _, out _) && File.Exists(liveStore.PathFor(liveBinding)));
            var saved = File.ReadAllBytes(liveStore.PathFor(nextBinding));
            persistence.Dispose(); session.ResetForChange(device.Id, DeviceChangeKind.GroupAddressLink);
            Check("disposed hosts detach state observers", File.ReadAllBytes(liveStore.PathFor(nextBinding)).SequenceEqual(saved));
            await using var restored = new ProgrammingSession(new InterfaceCoordinator(), () => project);
            using var restoredHost = new ProgrammingStatePersistence(restored, liveStore, project, nextPath, errors.Add);
            Check("a new host restores failed-attempt metadata and partial flags", restored.DeviceState(device.Id) is { Attempts: 2, Origin: ProgrammingOrigin.Demo }
                && !restored.DeviceState(device.Id)!.Flags.HasFlag(DeviceLoadFlags.Parameters) && restored.NeedsProgramming(device.Id));
            restoredHost.Rebind(sameIdOtherGuid, nextPath);
            Check("a foreign project cannot steal an attached session's facts", errors.Count == 1 && restored.DeviceStates.Count == 1
                && !File.Exists(liveStore.PathFor(ProgrammingStateBinding.Create(sameIdOtherGuid, nextPath)!)));

            var corruptPath = Path.Combine(root, "corrupt.knxproj"); var corruptBinding = ProgrammingStateBinding.Create(project, corruptPath)!;
            File.WriteAllText(liveStore.PathFor(corruptBinding), "{");
            await using (var empty = new ProgrammingSession(new InterfaceCoordinator(), () => project))
            {
                using var corruptHost = new ProgrammingStatePersistence(empty, liveStore, project, corruptPath, errors.Add);
                Check("invalid restore leaves current facts empty instead of inventing a baseline", empty.DeviceStates.Count == 0 && empty.Downloaded.Count == 0);
            }
            Check("closing after a failed restore does not overwrite the invalid file", File.ReadAllText(liveStore.PathFor(corruptBinding)) == "{");

            var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "keep");
            await using var failedIo = new ProgrammingSession(new InterfaceCoordinator(), () => project, demoExecutor: (_, _) => Task.CompletedTask);
            using var failedHost = new ProgrammingStatePersistence(failedIo, new ProgrammingStateStore(blocked), project, livePath, errors.Add);
            var completed = failedIo.Enqueue(device.Id); await failedIo.RunAsync();
            Check("I/O failure leaves the successful job honest and reports a DEMO metadata error", completed.Status == ProgrammingJobStatus.Done
                && failedIo.Downloaded.Single().Origin == SessionOrigin.Demo && errors.Any(e => e.StartsWith("DEMO comparison:", StringComparison.Ordinal))
                && File.ReadAllText(blocked) == "keep");

            var cancelPath = Path.Combine(root, "cancel.knxproj");
            var blockAttempt = false;
            await using var cancelled = new ProgrammingSession(new InterfaceCoordinator(), () => project,
                demoExecutor: (_, token) => blockAttempt ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask);
            using var cancelHost = new ProgrammingStatePersistence(cancelled, liveStore, project, cancelPath, errors.Add);
            cancelled.Enqueue(device.Id); await cancelled.RunAsync();
            var successfulState = cancelled.DeviceState(device.Id)!;
            blockAttempt = true;
            var job = cancelled.Enqueue(device.Id); var running = cancelled.RunAsync(); cancelled.CancelJob(job); await running;
            Check("cancellation retains the prior persisted comparison without inventing an attempt", liveStore.TryLoad(ProgrammingStateBinding.Create(project, cancelPath)!, out json, out _)
                && DeviceProgrammingStates.Parse(json!, project.Id)[device.Id] == successfulState
                && cancelled.DeviceState(device.Id) == successfulState && successfulState is { Attempts: 1, LastError: null }
                && job.Status == ProgrammingJobStatus.Cancelled && cancelled.Downloaded.Single().At == successfulState.LastSuccessAt);
        }
        finally { Directory.Delete(root, true); }
    }
}
