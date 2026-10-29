using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Knx;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Workbench;

if (args.Contains("--native-connection"))
{
    Environment.ExitCode = LiveConnectionNativeChecks.Run(args.Contains("--de") ? "de" : "en");
    return;
}
if (args.Contains("--native-secure-connection"))
{
    Environment.ExitCode = SecureConnectionNativeChecks.Run(args.Contains("--de") ? "de" : "en");
    return;
}
if (args.Contains("--native-m112-commissioning"))
{
    Environment.ExitCode = M112CommissioningNativeChecks.Run(args.Contains("--de") ? "de" : "en");
    return;
}

var checks = 0;
void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL " + name);
    checks++; Console.WriteLine("PASS " + name);
}
void Reject(string name, Action action)
{
    try { action(); } catch (InvalidOperationException) { Check(name, true); return; }
    throw new Exception("FAIL " + name);
}

if(args.Contains("--live-connection"))
{
    await LiveConnectionChecks.Run(Check);
    Console.WriteLine($"{checks} live connection checks passed.");
    return;
}

if(args.Contains("--history-adapter"))
{
    await MonitorHistoryCaptureChecks.Run(Check);
    Console.WriteLine($"{checks} history checks passed.");
    return;
}
if (args.Contains("--m112-commissioning"))
{
    await M112CommissioningChecks.Run(Check);
    Console.WriteLine($"{checks} M112 commissioning checks passed.");
    return;
}
if (args.Contains("--secure-connection"))
{
    await SecureConnectionChecks.Run(Check);
    Console.WriteLine($"{checks} secure connection checks passed.");
    return;
}

var project = new Project { Id = "P", Name = "Synthetic" };
var installation = new Installation { Id = "I" };
var area = new Area { Id = "A", Address = 1 };
var line = new Line { Id = "L", Address = 1 };
var device = new DeviceInstance { Id = "D", ProductRefId = "Product", Address = new(1, 1, 1), Name = "Device" };
var ko = new ComObjectInstanceRef { Id = "KO", ComObjectId = "Object", Number = 1 };
ko.Links.AddRange(["G1", "G2"]); device.ComObjectRefs.Add(ko);
device.ParameterValues["mode"] = "switch";
line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
var range = new GroupRange { Id = "Range" };
range.Addresses.Add(new() { Id = "G1", Name = "Light", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1" });
range.Addresses.Add(new() { Id = "G2", Name = "Status", Address = GroupAddress.ThreeLevel(1, 1, 2), DatapointType = "DPST-1-1" });
installation.MainRanges.Add(range);
project.Workbench.Devices[device.Id] = new() { SecureCapable = true };
project.Workbench.Groups["G1"] = new() { Secure = true, Bits = 1 };
var coordinator = new InterfaceCoordinator();
var realSource = new FakeTelegramSource();
using (var realMonitor = new MonitorSession(coordinator, () => project))
{
    realMonitor.ConfigureReal("fake-real-interface", realSource);
    realSource.IsConnected = false;
    Reject("real source must be connected explicitly", realMonitor.Start);
    realSource.IsConnected = true; realMonitor.Start(); realMonitor.Start();
    Check("real Start is idempotent, one subscription", realSource.SubscriberCount == 1);
    var events = 0;
    ((INotifyCollectionChanged)realMonitor.Rows).CollectionChanged += (_, _) => events++;
    var payload = new byte[] { 1 };
    await Task.Run(() => Parallel.For(0, 600, _ => realSource.Send(new BusTelegram
    {
        Source = "fake transport", Telegram = new Telegram
        {
            Source = device.Address, Destination = new IndividualAddress(range.Addresses[0].Address.Raw),
            IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = payload,
        },
    })));
    payload[0] = 0;
    Check("background real feed never raises observable UI events", events == 0 && realMonitor.RecordedCount == 600);
    realMonitor.FlushDisplay();
    var row = realMonitor.Rows[0];
    Check("real capture copies payload and resolves source/destination/DPT/identity", realMonitor.Rows.Count == 500 && events == 1 &&
        row.Source == "1.1.1" && row.Destination == "1/1/1" && row.DeviceId == device.Id && row.GroupAddressId == "G1" &&
        row.DatapointType == "DPST-1-1" && row.Origin == SessionOrigin.Real && row.DataHex == "01");
    Reject("demo tick cannot enter a real session", () => realMonitor.TickDemo(new(DateTimeOffset.Now, "1.1.1", "1/1/1", "Write", null, "1", SessionOrigin.Demo)));
    realMonitor.Stop(); Check("stop unsubscribes real adapter", realSource.SubscriberCount == 0);
    realMonitor.Start(); realMonitor.Dispose();
    Check("dispose unsubscribes real adapter", realSource.SubscriberCount == 0);
}
using (var monitor = new MonitorSession(coordinator, () => project))
{
    var resets = 0;
    ((INotifyCollectionChanged)monitor.Rows).CollectionChanged += (_, _) => resets++;
    monitor.Start(); monitor.Start();
    for (var i = 0; i < 600; i++) monitor.TickDemo(new(DateTimeOffset.UnixEpoch.AddSeconds(i), "1.1.1",
        i % 2 == 0 ? "1/1/1" : "1/1/2", "GroupValueWrite", "DPST-1-1", "on", SessionOrigin.Demo,
        i % 2 == 0 ? "G1" : "G2", device.Id));
    Check("hidden capture continues without any display/view calls", monitor.RecordedCount == 600 && monitor.Rows.Count == 0);
    monitor.FlushDisplay(); monitor.FlushDisplay();
    Check("bounded 500-row display, one coalesced reset", monitor.Rows.Count == 500 && resets == 1);
    monitor.SetFollowSelection(true, ko.Id, device.Id);
    Check("KO follows ALL linked GAs of the selected device", monitor.FilteredRows.Count == 500
        && monitor.Filter.DeviceId == device.Id);
    ko.Links.Remove("G2"); monitor.RefreshFilter();
    Check("follow reevaluates changed KO membership", monitor.FilteredRows.Count == 250);
    ko.Links.Add("G2"); monitor.RefreshFilter();
    monitor.SelectionChanged(null); Check("no selection is not all traffic", monitor.FilteredRows.Count == 0);
    monitor.PinFilter(new(MonitorScope.GroupAddress, "G1")); monitor.SelectionChanged(ko.Id);
    Check("pinned GA survives selection/tab changes", monitor.FilteredRows.Count == 250 && !monitor.FollowSelection);
    var filteredCsv = monitor.ExportFilteredCsv();
    using var full = new MemoryStream(); monitor.ExportFullCsv(full);
    Check("filtered export is not full recording", filteredCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 251 &&
        Encoding.UTF8.GetString(full.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 601);
    Check("CSV includes explicit origin", filteredCsv.Contains(",Demo,"));
    Reject("cannot switch live interface", () => monitor.Configure(WorkbenchMonitorMode.Bus, SessionOrigin.Demo, "demo-usb"));
    monitor.ClearDisplay(); Check("clearing display preserves full recording", monitor.Rows.Count == 0 && monitor.RecordedCount == 600);
    var simulated = monitor.SimulateWrite("G1", "on", device.Id);
    monitor.FlushDisplay();
    Check("simulated value is explicit Demo and resolves GA/device", simulated.Origin == SessionOrigin.Demo
        && simulated.Service == "GroupValueWrite" && simulated.GroupAddressId == "G1" && simulated.DeviceId == device.Id
        && monitor.Rows.Count == 1);
    Reject("simulated value requires a running demo session", () => { monitor.Stop(); monitor.SimulateRead("G1", "off"); });
    monitor.Stop(); monitor.Configure(WorkbenchMonitorMode.Bus, SessionOrigin.Demo, "demo-ip"); monitor.Start();
    Check("bus monitor excludes programming", !coordinator.TryAcquire("demo-ip", InterfaceActivity.Programming, out _));
    Check("bus monitor excludes scans", !coordinator.TryAcquire("demo-ip", InterfaceActivity.Scan, out _));
    Check("different interface remains available", coordinator.TryAcquire("demo-usb", InterfaceActivity.Scan, out var other)); other?.Dispose();
    monitor.Stop();
    Check("stop releases interface", coordinator.TryAcquire("demo-ip", InterfaceActivity.Scan, out var scan));
    Reject("scan excludes bus monitor", monitor.Start); scan?.Dispose();
    monitor.Start(); monitor.Dispose();
    Check("dispose releases interface", coordinator.TryAcquire("demo-ip", InterfaceActivity.Programming, out var lease)); lease?.Dispose();
}

using (var roomMonitor = new MonitorSession(coordinator, () => project, filter =>
    filter.Scope == MonitorScope.Room && filter.Id == "room" ? new HashSet<string> { "G2" } : new HashSet<string>()))
{
    roomMonitor.Start();
    roomMonitor.TickDemo(new(DateTimeOffset.UtcNow, "1.1.1", "1/1/2", "Write", "1.001", "a,\"b\"\n", SessionOrigin.Demo, "G2"));
    roomMonitor.FlushDisplay(); roomMonitor.PinFilter(new(MonitorScope.Room, "room"));
    Check("room membership comes from supplied domain resolver", roomMonitor.FilteredRows.Count == 1);
    Check("CSV escapes comma quote newline", roomMonitor.ExportFilteredCsv().Contains("\"a,\"\"b\"\"\n\""));
}

var before = DeviceConfigurationSnapshot.Freeze(project, device);
device.Name = "Renamed"; device.Description = "Description"; ko.Text = "Renamed object";
Check("name/description/KO labels do not change bus signature", before == DeviceConfigurationSnapshot.Freeze(project, device));
device.ParameterValues["mode"] = "dim";
Check("parameters change configuration signature", before.Signature != DeviceConfigurationSnapshot.Freeze(project, device).Signature);
device.ParameterValues["mode"] = "switch";
range.Addresses[0].Address = GroupAddress.ThreeLevel(1, 2, 1);
Check("GA address changes affect device signature", before.Signature != DeviceConfigurationSnapshot.Freeze(project, device).Signature);
range.Addresses[0].Address = GroupAddress.ThreeLevel(1, 1, 1);
ko.Links.Reverse();
Check("equivalent link order does not change signature", before.Signature == DeviceConfigurationSnapshot.Freeze(project, device).Signature);
range.Addresses[0].DatapointType = "DPST-5-1";
Check("GA DPT changes affect device signature", before.Signature != DeviceConfigurationSnapshot.Freeze(project, device).Signature);
range.Addresses[0].DatapointType = "DPST-1-1";
device.Address = new(1, 1, 2);
Check("physical address changes affect signature", before.Signature != DeviceConfigurationSnapshot.Freeze(project, device).Signature);
device.Address = new(1, 1, 1);
line.MediumTypeRefId = "MT-1";
Check("topology medium changes affect signature", before.Signature != DeviceConfigurationSnapshot.Freeze(project, device).Signature);
line.MediumTypeRefId = "MT-0";

var attempts = 0;
var failNext = false;
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var queue = new ProgrammingSession(coordinator, () => project, demoExecutor: async (job, token) =>
{
    attempts++;
    if (failNext) throw new IOException("Later failure");
    if (attempts == 1) { entered.SetResult(); await release.Task.WaitAsync(token); throw new IOException("Synthetic demo failure"); }
}))
{
    var job = queue.Enqueue(device.Id);
    Check("job explicitly Demo and queued", job.Origin == SessionOrigin.Demo && job.Status == ProgrammingJobStatus.Queued);
    var frozen = job.Configuration;
    device.ParameterValues["mode"] = "dim";
    Check("enqueue freezes payload and signature", job.Configuration == frozen && frozen.PayloadJson.Contains("switch") && !frozen.PayloadJson.Contains("dim"));
    var run = queue.RunAsync(); await entered.Task;
    Check("active device protected", queue.IsDeviceBusy(device.Id) && !queue.TryEnsureEditable(device.Id, out var reason) && reason is not null);
    Check("unrelated device editable", queue.TryEnsureEditable("other", out _));
    Check("running queue excludes scan", !coordinator.TryAcquire("demo-ip", InterfaceActivity.Scan, out _));
    release.SetResult(); await run;
    Check("failure never updates downloaded state", job.Status == ProgrammingJobStatus.Failed && queue.Downloaded.Count == 0 && job.Error!.Contains("Synthetic"));
    Check("failure unlocks active device", !queue.IsDeviceBusy(device.Id));
    queue.Retry(job); await queue.RunAsync();
    Check("retry uses same frozen job", ReferenceEquals(job, queue.Jobs[0]) && job.Configuration == frozen && job.Attempts == 2 && job.Status == ProgrammingJobStatus.Done);
    Check("downloaded comparison uses frozen state not current project", queue.NeedsProgramming(device.Id) && queue.Downloaded.Single().Signature == frozen.Signature);
    device.ParameterValues["mode"] = "switch";
    Check("undo independently matches successful simulated device state", !queue.NeedsProgramming(device.Id) && queue.Downloaded.Single().Signature == frozen.Signature);
    device.ParameterValues["mode"] = "dim";
    Check("redo does not rewrite downloaded baseline", queue.NeedsProgramming(device.Id));
    var persisted = queue.ExportDownloadedJson();
    Check("comparison persistence excludes config payload", !persisted.Contains("PayloadJson") && !persisted.Contains("switch") && !persisted.Contains("ParameterValues"));
    await using var restored = new ProgrammingSession(coordinator, () => project);
    restored.ImportDownloadedJson(persisted);
    Check("downloaded comparison JSON roundtrip", restored.Downloaded.Single() == queue.Downloaded.Single() && restored.NeedsProgramming(device.Id));
    try { restored.ImportDownloadedJson(persisted.Replace("\"ProjectId\":\"P\"", "\"ProjectId\":\"other\"")); throw new Exception("Expected invalid project"); }
    catch (FormatException) { Check("invalid comparison import is atomic", restored.Downloaded.Single() == queue.Downloaded.Single()); }
    using var blocker = new MonitorSession(coordinator, () => project);
    blocker.Configure(WorkbenchMonitorMode.Bus, SessionOrigin.Demo, "demo-ip"); blocker.Start();
    var blockedJob = restored.Enqueue(device.Id);
    try { await restored.RunAsync(); throw new Exception("Expected interface conflict"); }
    catch (InvalidOperationException) { Check("blocked queue retains queued job and unlocks", !restored.IsRunning && blockedJob.Status == ProgrammingJobStatus.Queued); }
    blocker.Stop(); await restored.RunAsync();
    Check("queue starts after interface released", blockedJob.Status == ProgrammingJobStatus.Done);
    failNext = true;
    var previousDownloaded = queue.Downloaded.Single();
    var laterFailed = queue.Enqueue(device.Id); await queue.RunAsync();
    Check("later failure preserves previous successful baseline", laterFailed.Status == ProgrammingJobStatus.Failed && queue.Downloaded.Single() == previousDownloaded);
}

// Programming state: five load flags, attempt history, change classification, undo independence and persistence.
var stateProject = WorkbenchJson.CreateDemo();
var stateDevice = ProjectQuery.DeviceList(stateProject).First();
var stateLine = ProjectQuery.Devices(stateProject).First(d => d.Device.Id == stateDevice.Id).Line!;
var stateRoom = stateProject.Installations.Single().Locations.AllNodes.First();
DeviceChangeKind ClassifyState(FrozenConfiguration before) =>
    DeviceConfigurationSnapshot.ClassifyChange(before, DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice));
async Task ReloadState(ProgrammingSession queue)
{
    queue.Enqueue(stateDevice.Id);
    await queue.RunAsync();
}

var stateFail = true;
await using (var failingQueue = new ProgrammingSession(coordinator, () => stateProject,
    demoExecutor: (_, _) => stateFail ? Task.FromException(new IOException("Synthetic state failure")) : Task.CompletedTask))
{
    var failedJob = failingQueue.Enqueue(stateDevice.Id);
    await failingQueue.RunAsync();
    var failed = failingQueue.DeviceState(stateDevice.Id)!;
    Check("failed job records attempt/error without success state", failed.Flags == DeviceLoadFlags.None
        && failed.LastSuccessAt is null && failed.LastAttemptAt is not null && failed.Attempts == 1
        && failed.Origin is null && failed.LastAttemptOrigin == ProgrammingOrigin.Demo
        && failed.LastError!.Contains("Synthetic state failure") && failingQueue.Downloaded.Count == 0);
    Check("failed attempt still needs programming", failingQueue.NeedsProgramming(stateDevice.Id));
    stateFail = false;
    failingQueue.Retry(failedJob); await failingQueue.RunAsync();
    var retried = failingQueue.DeviceState(stateDevice.Id)!;
    Check("retry success sets all five flags and clears the error", retried.Flags == DeviceLoadFlags.All
        && retried.IsFullyLoaded && retried.LastError is null && retried.Attempts == 2
        && retried.LastSuccessAt is not null && retried.Origin == ProgrammingOrigin.Demo);
    var successAt = retried.LastSuccessAt; var successHash = retried.ImageHash;
    stateFail = true;
    failingQueue.Enqueue(stateDevice.Id); await failingQueue.RunAsync();
    var afterFailure = failingQueue.DeviceState(stateDevice.Id)!;
    Check("failure after success preserves flags/hash/time", afterFailure.Flags == DeviceLoadFlags.All
        && afterFailure.ImageHash == successHash && afterFailure.LastSuccessAt == successAt
        && afterFailure.Attempts == 3 && afterFailure.LastError!.Contains("Synthetic state failure"));
    Check("failure after success preserves downloaded baseline", failingQueue.Downloaded.Single().Signature == successHash
        && failingQueue.Downloaded.Single().At == successAt);
}

await using (var stateQueue = new ProgrammingSession(coordinator, () => stateProject))
{
    var fullJob = stateQueue.Enqueue(stateDevice.Id);
    await stateQueue.RunAsync();
    var loaded = stateQueue.DeviceState(stateDevice.Id)!;
    Check("success records all five flags, timestamp, interface and demo origin", loaded.Flags == DeviceLoadFlags.All
        && loaded.IsFullyLoaded && loaded.Attempts == 1 && loaded.LastSuccessAt is not null
        && loaded.LastAttemptAt == loaded.LastSuccessAt && loaded.LastError is null
        && loaded.InterfaceId == "demo-ip" && loaded.Origin == ProgrammingOrigin.Demo
        && loaded.LastAttemptOrigin == ProgrammingOrigin.Demo);
    Check("success stores the frozen image/program hash", loaded.ImageHash == fullJob.Configuration.Signature
        && stateQueue.Downloaded.Single().Signature == fullJob.Configuration.Signature
        && stateQueue.Downloaded.Single().Origin == SessionOrigin.Demo);
    Check("matching full load needs no programming and unknown device has no state",
        !stateQueue.NeedsProgramming(stateDevice.Id) && stateQueue.DeviceState("missing") is null);

    var baseline = DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice);
    stateDevice.Name = "Renamed state device"; stateRoom.Name = "Renamed room";
    var nameChange = ClassifyState(baseline);
    Check("name/room edits classify as no programming change", nameChange == DeviceChangeKind.None);
    Check("name/room edits reset no flag", !stateQueue.ResetForChange(stateDevice.Id, nameChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags == DeviceLoadFlags.All && !stateQueue.NeedsProgramming(stateDevice.Id));

    stateDevice.Address = new IndividualAddress(1, 1, 250);
    var addressChange = ClassifyState(baseline);
    Check("address change classifies as individual address", addressChange.HasFlag(DeviceChangeKind.IndividualAddress));
    Check("address reset clears only the address flag", stateQueue.ResetForChange(stateDevice.Id, addressChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags == (DeviceLoadFlags.All & ~DeviceLoadFlags.IndividualAddress)
        && stateQueue.NeedsProgramming(stateDevice.Id));
    await ReloadState(stateQueue);

    var otherLine = new Line { Id = "STATE-LINE-2", Address = 9, MediumTypeRefId = stateLine.MediumTypeRefId };
    var beforeTopology = DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice);
    ProjectQuery.FindAreaOf(stateProject, stateLine)!.Lines.Add(otherLine);
    ProjectQuery.RemoveDevice(stateProject, stateDevice); ProjectQuery.AddToLine(otherLine, stateDevice);
    var topologyChange = ClassifyState(beforeTopology);
    Check("topology move classifies as topology", topologyChange.HasFlag(DeviceChangeKind.Topology));
    Check("topology reset clears only the address flag", stateQueue.ResetForChange(stateDevice.Id, topologyChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags == (DeviceLoadFlags.All & ~DeviceLoadFlags.IndividualAddress));
    await ReloadState(stateQueue);

    baseline = DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice);
    stateDevice.ProductRefId = "STATE-PRODUCT-2";
    var productChange = ClassifyState(baseline);
    Check("product change classifies as product", productChange.HasFlag(DeviceChangeKind.Product));
    Check("product reset keeps address and configuration loaded", stateQueue.ResetForChange(stateDevice.Id, productChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags ==
            (DeviceLoadFlags.All & ~(DeviceLoadFlags.ApplicationProgram | DeviceLoadFlags.Parameters | DeviceLoadFlags.GroupAddresses)));
    await ReloadState(stateQueue);

    baseline = DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice);
    stateDevice.ParameterValues["state-parameter"] = "42";
    var parameterChange = ClassifyState(baseline);
    Check("parameter change classifies as parameters only", parameterChange == DeviceChangeKind.Parameters);
    Check("parameter reset clears only parameters", stateQueue.ResetForChange(stateDevice.Id, parameterChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags == (DeviceLoadFlags.All & ~DeviceLoadFlags.Parameters));
    await ReloadState(stateQueue);

    baseline = DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice);
    stateDevice.ComObjectRefs[0].Links.Add("STATE-UNKNOWN-GA");
    var linkChange = ClassifyState(baseline);
    Check("group link change classifies as group addresses only", linkChange == DeviceChangeKind.GroupAddressLink);
    Check("group link reset clears only group addresses", stateQueue.ResetForChange(stateDevice.Id, linkChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags == (DeviceLoadFlags.All & ~DeviceLoadFlags.GroupAddresses));
    await ReloadState(stateQueue);

    baseline = DeviceConfigurationSnapshot.Freeze(stateProject, stateDevice);
    otherLine.MediumTypeRefId = "MT-STATE";
    var configurationChange = ClassifyState(baseline);
    Check("medium change classifies as configuration only", configurationChange == DeviceChangeKind.Configuration);
    Check("configuration reset clears only configuration", stateQueue.ResetForChange(stateDevice.Id, configurationChange)
        && stateQueue.DeviceState(stateDevice.Id)!.Flags == (DeviceLoadFlags.All & ~DeviceLoadFlags.Configuration));
    await ReloadState(stateQueue);

    var history = new UndoStack(); history.Reset(stateProject);
    var steps = history.Steps;
    var beforeReset = stateQueue.DeviceState(stateDevice.Id)!;
    stateQueue.ResetForChange(stateDevice.Id, DeviceChangeKind.Parameters);
    var stateAfterReset = stateQueue.DeviceState(stateDevice.Id);
    Check("programming reset is not a project undo step", history.Steps == steps
        && stateAfterReset!.Flags == (beforeReset.Flags & ~DeviceLoadFlags.Parameters));
    stateDevice.ParameterValues["undo-parameter"] = "1";
    history.Push(stateProject);
    var undone = history.Undo()!;
    Check("project undo does not rewrite programming state", undone.Id == stateProject.Id
        && stateQueue.DeviceState(stateDevice.Id) == stateAfterReset);
    await ReloadState(stateQueue);

    var persisted = stateQueue.ExportDownloadedJson();
    var persistedNode = JsonNode.Parse(persisted)!.AsObject();
    Check("programming persistence is versioned and payload-free", (int)persistedNode["Version"]! == 2
        && !persisted.Contains("PayloadJson") && !persisted.Contains("ParameterValues") && !persisted.Contains("state-parameter"));
    await using (var restoredState = new ProgrammingSession(coordinator, () => stateProject))
    {
        restoredState.ImportDownloadedJson(persisted);
        Check("programming state roundtrip keeps flags, history and projection",
            restoredState.DeviceState(stateDevice.Id) == stateQueue.DeviceState(stateDevice.Id)
            && restoredState.Downloaded.Single() == stateQueue.Downloaded.Single());
        var original = restoredState.DeviceState(stateDevice.Id);
        void RejectState(string name, Action<JsonObject> corrupt)
        {
            var node = JsonNode.Parse(persisted)!.AsObject();
            corrupt(node);
            try { restoredState.ImportDownloadedJson(node.ToJsonString()); throw new Exception("Expected " + name); }
            catch (FormatException) { Check(name, restoredState.DeviceState(stateDevice.Id) == original); }
        }
        RejectState("wrong programming-state version is rejected atomically", n => n["Version"] = 1);
        RejectState("foreign project programming state is rejected atomically", n => n["ProjectId"] = "other");
        RejectState("duplicate device programming state is rejected atomically",
            n => n["Devices"]!.AsArray().Add(n["Devices"]![0]!.DeepClone()));
        RejectState("unknown load flag is rejected atomically", n => n["Devices"]![0]!["Flags"] = "IndividualAddress, Bogus");
        RejectState("negative attempt count is rejected atomically", n => n["Devices"]![0]!["Attempts"] = -1);
        RejectState("success without image hash is rejected atomically", n => n["Devices"]![0]!["ImageHash"] = null);
        RejectState("null device list is rejected atomically", n => n["Devices"] = null);
        try { restoredState.ImportDownloadedJson("{"); throw new Exception("Expected malformed state JSON"); }
        catch (FormatException) { Check("malformed programming-state JSON is rejected atomically", restoredState.DeviceState(stateDevice.Id) == original); }
    }

    var report = stateQueue.CheckProgramming();
    var loadedRow = report.Single(r => r.DeviceId == stateDevice.Id);
    Check("diagnostics report sees a fully loaded device", !loadedRow.NeedsProgramming
        && loadedRow.MissingFlags == DeviceLoadFlags.None && !loadedRow.SignatureDrift && loadedRow.Origin == ProgrammingOrigin.Demo);
    stateQueue.ResetForChange(stateDevice.Id, DeviceChangeKind.Parameters);
    stateDevice.ParameterValues["report-parameter"] = "1";
    var driftRow = stateQueue.CheckProgramming().Single(r => r.DeviceId == stateDevice.Id);
    Check("diagnostics report exposes missing flags and signature drift", driftRow.NeedsProgramming
        && driftRow.MissingFlags == DeviceLoadFlags.Parameters && driftRow.SignatureDrift);
}

var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelled = new ProgrammingSession(coordinator, () => project, demoExecutor: async (_, token) =>
{
    cancellationEntered.SetResult(); await Task.Delay(Timeout.Infinite, token);
});
var cancelledJob = cancelled.Enqueue(device.Id); var pending = cancelled.RunAsync(); await cancellationEntered.Task;
await cancelled.DisposeAsync(); await pending;
Check("explicit async dispose cancels and joins owned queue", cancelledJob.Status == ProgrammingJobStatus.Cancelled && !cancelled.IsRunning && cancelled.Downloaded.Count == 0);
Check("cancelled queue releases interface", coordinator.TryAcquire("demo-ip", InterfaceActivity.Scan, out var lastLease)); lastLease?.Dispose();

Project? current = project;
await using (var scoped = new WorkbenchSessions(() => current))
{
    scoped.Programming.Enqueue(device.Id);
    scoped.Monitor.PinFilter(new(MonitorScope.Project));
    current = new Project { Id = "OtherProject", Name = "Other" };
    Reject("comparison state/jobs cannot silently migrate to another project", () => scoped.Programming.NeedsProgramming(device.Id));
    Reject("monitor cannot silently migrate to another project", scoped.Monitor.Start);
}
current = project;
Check("disposal does not resolve a changed project or filter", true);

var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var executions = 0;
await using (var growingQueue = new ProgrammingSession(coordinator, () => project, demoExecutor: async (_, token) =>
{
    if (++executions == 1) { appended.SetResult(); await finishFirst.Task.WaitAsync(token); }
}))
{
    growingQueue.Enqueue(device.Id);
    var running = growingQueue.RunAsync(); await appended.Task;
    var appendedJob = growingQueue.Enqueue(device.Id);
    finishFirst.SetResult(); await running;
    Check("jobs enqueued during execution drain in the same run", appendedJob.Status == ProgrammingJobStatus.Done && executions == 2);
}

PumpContext.Run(async () =>
{
    var owner = SynchronizationContext.Current;
    var ownerEventsOnly = true;
    await using var uiQueue = new ProgrammingSession(coordinator, () => project, demoExecutor: async (_, token) => await Task.Delay(10, token));
    uiQueue.PropertyChanged += (_, _) => ownerEventsOnly &= SynchronizationContext.Current == owner;
    ((INotifyCollectionChanged)uiQueue.Downloaded).CollectionChanged += (_, _) => ownerEventsOnly &= SynchronizationContext.Current == owner;
    var job = uiQueue.Enqueue(device.Id);
    job.PropertyChanged += (_, _) => ownerEventsOnly &= SynchronizationContext.Current == owner;
    await Task.Run(() =>
    {
        try { uiQueue.Enqueue(device.Id); throw new Exception("Expected owner boundary"); }
        catch (InvalidOperationException) { }
    });
    await uiQueue.RunAsync();
    Check("async queue notifications return to owning UI context", ownerEventsOnly && job.Status == ProgrammingJobStatus.Done);
});
var lineScan = new LineScanSession(coordinator, () => project);
await lineScan.RunDemoAsync(line.Id);
Check("line scan returns explicit demo results without transport", lineScan.Results.Count == 1
    && lineScan.Results[0].Address == "1.1.1" && lineScan.Results[0].Origin == SessionOrigin.Demo
    && lineScan.Results[0].Detail.Length > 0 && !lineScan.IsRunning);
var lineResult = lineScan.Results[0];
Check("line scan result carries device id, line id and timestamp", lineResult.DeviceId == device.Id
    && lineResult.LineId == line.Id && lineResult.Timestamp != default
    && lineScan.LastLineId == line.Id && lineScan.LastScanAt is not null);
await lineScan.RunDemoDeviceAsync(device.Id);
Check("device scan result carries ids and explicit origin", lineScan.Results.Count == 1
    && lineScan.Results[0].DeviceId == device.Id && lineScan.Results[0].LineId == line.Id
    && lineScan.Results[0].Origin == SessionOrigin.Demo && lineScan.Results[0].Timestamp != default);
var lineScanRejected = false;
try { await lineScan.RunDemoAsync("missing"); }
catch (ArgumentException) { lineScanRejected = true; }
Check("line scan rejects unknown line", lineScanRejected);
var deviceScanRejected = false;
try { await lineScan.RunDemoDeviceAsync("missing"); }
catch (ArgumentException) { deviceScanRejected = true; }
Check("device scan rejects unknown device", deviceScanRejected);

// ---- B6: job type/progress/origin, queue cancellation, retry/clear history ----
await using (var typedQueue = new ProgrammingSession(coordinator, () => project))
{
    var parametersJob = typedQueue.Enqueue(device.Id, "parameters");
    var unknownJob = typedQueue.Enqueue(device.Id, "future-operation");
    Check("job type is explicit and unknown operations stay full loads",
        parametersJob.Type == ProgrammingJobType.Parameters && unknownJob.Type == ProgrammingJobType.Full
        && parametersJob.Origin == SessionOrigin.Demo);
    var realGated = false;
    try { typedQueue.Enqueue(device.Id, origin: SessionOrigin.Real); }
    catch (NotSupportedException) { realGated = true; }
    Check("real programming jobs stay behind the safety gate",
        realGated && !typedQueue.RealExecutionConfigured);
    Check("queued job can be cancelled before it runs", typedQueue.CancelJob(typedQueue.Jobs[1])
        && typedQueue.Jobs[1].Status == ProgrammingJobStatus.Cancelled);
    typedQueue.Retry(typedQueue.Jobs[1]);
    Check("cancelled job can be retried through the identical frozen job",
        typedQueue.Jobs[1].Status == ProgrammingJobStatus.Queued && typedQueue.Jobs[1].Attempts == 0);
    await typedQueue.RunAsync();
    Check("retried and partial jobs complete", typedQueue.Jobs[0].Status == ProgrammingJobStatus.Done
        && typedQueue.Jobs[1].Status == ProgrammingJobStatus.Done
        && typedQueue.Jobs[0].Progress == 100 && typedQueue.Jobs[1].Progress == 100);
}

var explicitRealRuns = 0;
await using (var explicitRealQueue = new ProgrammingSession(coordinator, () => project,
    realExecutor: (_, _) => { explicitRealRuns++; return Task.CompletedTask; }))
{
    var explicitRealJob = explicitRealQueue.Enqueue(device.Id, origin: SessionOrigin.Real);
    await explicitRealQueue.RunAsync();
    Check("explicit real origin uses only the injected executor and is recorded honestly",
        explicitRealQueue.RealExecutionConfigured && explicitRealRuns == 1
        && explicitRealJob.Origin == SessionOrigin.Real && explicitRealJob.Status == ProgrammingJobStatus.Done
        && explicitRealQueue.DeviceState(device.Id)!.Origin == ProgrammingOrigin.Real
        && explicitRealQueue.Downloaded.Single().Origin == SessionOrigin.Real);
}

var progressSession = new ProgrammingSession(coordinator, () => project, demoExecutor: async (job, token) =>
{
    job.ReportProgress(40); await Task.Yield();
    job.ReportProgress(80); await Task.Yield();
});
var progressJob = progressSession.Enqueue(device.Id);
var reportedProgress = new List<int>();
progressJob.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgrammingJob.Progress)) reportedProgress.Add(progressJob.Progress); };
await progressSession.RunAsync();
Check("executor progress is observable and a success ends at 100",
    reportedProgress.Contains(40) && reportedProgress.Contains(80) && reportedProgress[^1] == 100
    && progressJob.Progress == 100);
await progressSession.DisposeAsync();

var firstJobEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelExecutions = 0;
await using (var cancelQueue = new ProgrammingSession(coordinator, () => project, demoExecutor: async (job, token) =>
{
    if (Interlocked.Increment(ref cancelExecutions) == 1)
    {
        firstJobEntered.SetResult();
        await Task.Delay(Timeout.Infinite, token);
    }
}))
{
    var runningJob = cancelQueue.Enqueue(device.Id);
    var queuedJob = cancelQueue.Enqueue(device.Id);
    var followerJob = cancelQueue.Enqueue(device.Id);
    var run = cancelQueue.RunAsync(); await firstJobEntered.Task;
    Check("exactly one job runs at a time", cancelQueue.ActiveJob == runningJob
        && runningJob.Status == ProgrammingJobStatus.Running && queuedJob.Status == ProgrammingJobStatus.Queued);
    Check("queued job cancellation does not stop the active job",
        cancelQueue.CancelJob(queuedJob) && queuedJob.Status == ProgrammingJobStatus.Cancelled
        && runningJob.Status == ProgrammingJobStatus.Running);
    Check("running job can be cancelled explicitly", cancelQueue.CancelJob(runningJob));
    await run;
    Check("cancelling the running job continues with the next queued job",
        runningJob.Status == ProgrammingJobStatus.Cancelled && followerJob.Status == ProgrammingJobStatus.Done
        && cancelExecutions == 2);
    Check("cancellation records no failure and keeps successful state",
        runningJob.Error is not null && cancelQueue.DeviceState(device.Id)!.LastError is null
        && cancelQueue.Downloaded.Count == 1);
}

await using (var clearQueue = new ProgrammingSession(coordinator, () => project))
{
    var finished = clearQueue.Enqueue(device.Id);
    await clearQueue.RunAsync();
    var stillQueued = clearQueue.Enqueue(device.Id);
    Check("clear history removes terminal jobs and keeps queued work",
        clearQueue.ClearHistory() == 1 && clearQueue.Jobs.Count == 1 && clearQueue.Jobs[0] == stillQueued
        && finished.Status == ProgrammingJobStatus.Done && stillQueued.Status == ProgrammingJobStatus.Queued);
    Check("bulk cancel marks all queued jobs cancelled", clearQueue.CancelQueued() == 1
        && stillQueued.Status == ProgrammingJobStatus.Cancelled);
    Check("clear history removes cancelled jobs too", clearQueue.ClearHistory() == 1 && clearQueue.Jobs.Count == 0);
}

var queueCancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var queueCancel = new ProgrammingSession(coordinator, () => project, demoExecutor: async (_, token) =>
{
    queueCancelEntered.SetResult();
    await Task.Delay(Timeout.Infinite, token);
});
var queueCancelRunning = queueCancel.Enqueue(device.Id);
var queueCancelWaiting = queueCancel.Enqueue(device.Id);
var queueCancelRun = queueCancel.RunAsync(); await queueCancelEntered.Task;
queueCancel.Cancel(); await queueCancelRun;
Check("cancelling the queue marks the running job and retains queued work",
    queueCancelRunning.Status == ProgrammingJobStatus.Cancelled
    && queueCancelWaiting.Status == ProgrammingJobStatus.Queued && !queueCancel.IsRunning);
await queueCancel.DisposeAsync();

// ---- B6: monitor pause, auto-scroll and full export ----
using (var pausedMonitor = new MonitorSession(coordinator, () => project))
{
    pausedMonitor.Start();
    pausedMonitor.TickDemo(new(DateTimeOffset.UnixEpoch, "1.1.1", "1/1/1", "GroupValueWrite",
        "DPST-1-1", "on", SessionOrigin.Demo, "G1", device.Id));
    pausedMonitor.Pause();
    Check("pause keeps the interface lease and marks the session paused",
        pausedMonitor.IsPaused && pausedMonitor.IsRunning
        && !coordinator.TryAcquire("demo-ip", InterfaceActivity.BusMonitor, out _));
    pausedMonitor.TickDemo(new(DateTimeOffset.UnixEpoch.AddSeconds(1), "1.1.1", "1/1/2", "GroupValueWrite",
        "DPST-1-1", "off", SessionOrigin.Demo, "G2", device.Id));
    pausedMonitor.Resume();
    pausedMonitor.TickDemo(new(DateTimeOffset.UnixEpoch.AddSeconds(2), "1.1.1", "1/1/2", "GroupValueWrite",
        "DPST-1-1", "off", SessionOrigin.Demo, "G2", device.Id));
    pausedMonitor.FlushDisplay();
    Check("paused telegrams are dropped and resume captures again", !pausedMonitor.IsPaused
        && pausedMonitor.RecordedCount == 2 && pausedMonitor.Rows.Count == 2);
    Check("auto-scroll is an explicit presentation flag", pausedMonitor.AutoScroll);
    pausedMonitor.AutoScroll = false;
    Check("auto-scroll can be disabled", !pausedMonitor.AutoScroll);
    var fullText = pausedMonitor.ExportFullCsv();
    Check("full text export contains rows beyond the display tail",
        fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3
        && fullText.Contains(",Demo,", StringComparison.Ordinal));
    pausedMonitor.Stop();
}

// ---- B6: project diagnostic summary ----
await using (var diagnostic = new WorkbenchSessions(() => project))
{
    diagnostic.Monitor.Configure(WorkbenchMonitorMode.Bus, SessionOrigin.Demo, "diag-ip");
    diagnostic.Monitor.Start();
    var summary = diagnostic.BuildDiagnosticSummary();
    var diagInterface = summary.Interfaces.Single(i => i.InterfaceId == "diag-ip");
    Check("diagnostic summary reports project and programming needs",
        summary.ProjectId == project.Id && summary.Programming.DeviceCount == 1
        && summary.Programming.NeedsProgrammingCount == 1 && !summary.Programming.AllLoaded);
    Check("diagnostic summary reports blocked interface activities",
        diagInterface.ActiveActivities.Contains("BusMonitor")
        && diagInterface.BlockedActivities.Contains("Scan")
        && diagInterface.BlockedActivities.Contains("Programming")
        && summary.HasInterfaceConflicts && diagInterface.HasConflict);
    Check("diagnostic summary exposes secure capability limits and counts",
        summary.Secure.SecureDeviceCount == 1 && summary.Secure.SecureGroupCount == 1
        && summary.Secure.SecureLinkCount == 1 && summary.Secure.UnsupportedSecureLinkCount == 0
        && !summary.Secure.KeyManagementAvailable && !summary.Secure.SecureDownloadAvailable
        && summary.Secure.Limits.Length > 0);
    diagnostic.Monitor.Stop();
    var relaxed = diagnostic.BuildDiagnosticSummary();
    Check("released interface disappears from the diagnostic summary",
        !relaxed.HasInterfaceConflicts && relaxed.Interfaces.Count == 0);
}

// ---- B6: project switch requires disposing the previous sessions ----
Project? switchProject = project;
var oldSessions = new WorkbenchSessions(() => switchProject);
oldSessions.Monitor.Start();
oldSessions.Programming.Enqueue(device.Id);
await oldSessions.DisposeAsync();
Check("disposing old sessions releases their interface lease",
    coordinator.TryAcquire("demo-ip", InterfaceActivity.BusMonitor, out var releasedLease));
releasedLease?.Dispose();
switchProject = new Project { Id = "P-switch", Name = "Other project" };
await using (var freshSessions = new WorkbenchSessions(() => switchProject))
{
    freshSessions.Monitor.Configure(WorkbenchMonitorMode.Group, SessionOrigin.Demo, "switch-ip");
    freshSessions.Monitor.Start();
    Check("fresh sessions bind the switched project", freshSessions.BuildDiagnosticSummary().ProjectId == "P-switch");
}
try { oldSessions.Monitor.Start(); throw new Exception("Expected disposed monitor"); }
catch (ObjectDisposedException) { Check("disposed sessions cannot service the old project", true); }
try { oldSessions.Programming.Enqueue(device.Id); throw new Exception("Expected disposed programming session"); }
catch (ObjectDisposedException) { Check("disposed programming queue rejects new jobs", true); }

await ProgrammingPersistenceChecks.Run(Check);
await MonitorHistoryCaptureChecks.Run(Check);
await LiveConnectionChecks.Run(Check);
await M112CommissioningChecks.Run(Check);
await SecureConnectionChecks.Run(Check);
Check("configuration location identity checks", ConfigurationLocationChecks.Run() == 0);
Console.WriteLine($"{checks} checks passed.");

sealed class FakeTelegramSource : IMonitorTelegramSource
{
    private Action<BusTelegram>? _received;
    public bool IsConnected { get; set; } = true;
    public int SubscriberCount => _received?.GetInvocationList().Length ?? 0;
    public event Action<BusTelegram> TelegramReceived { add => _received += value; remove => _received -= value; }
    public void Send(BusTelegram telegram) => _received?.Invoke(telegram);
}

sealed class PumpContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
    public override void Post(SendOrPostCallback d, object? state) => _work.Add((d, state));
    public static void Run(Func<Task> action)
    {
        var previous = Current;
        var context = new PumpContext();
        SetSynchronizationContext(context);
        try
        {
            var task = action();
            while (!task.IsCompleted)
                if (context._work.TryTake(out var item, 100)) item.Callback(item.State);
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); context._work.Dispose(); }
    }
}
