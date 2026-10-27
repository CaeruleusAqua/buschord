using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

/// <summary>Opt-in native measurements, without injected input, OS settings or product timing assertions.</summary>
internal static class NativePerformanceChecks
{
    private const int DeviceCount = 48, ObjectsPerDevice = 250, SampleCount = 24, WarmupCycles = 3;
    private static readonly TimeSpan SampleDeadline = TimeSpan.FromSeconds(8);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    internal static int Run() => RunCore(false);

    [STAThread]
    internal static int RunBudget() => RunCore(true);

    private static int RunCore(bool budgetMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-native-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "measurements.json");
        var budgetOutput = Path.Combine(root, "budget.json");
        var report = new Report();
        var completion = new NativePerformanceBudgetCompletion();
        NativePerformanceEvidence? before = null, after = null;
        NativePerformanceBudgetResult? budgetResult = null;
        Exception? artifactError = null;
        var previousSettings = Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        App.ForcedLanguage = "en"; App.ForcedTheme = "light";
        var fixture = CreateFixture();
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
            var vm = new MainWindowViewModel(catalog);
            vm.CreateProject(fixture, null);
            return new WorkbenchWindow(vm, new WorkbenchWorkspace(), Path.Combine(root, "layout.json"), catalog)
            { Width = 1440, Height = 900 };
        };
        void Save()
        {
            lock (Gate)
            {
                if (budgetMode && !completion.IsPending) return;
                try
                {
                    WriteArtifacts();
                    if (budgetMode) completion.TryComplete();
                }
                catch (Exception error) when (budgetMode)
                {
                    artifactError = error;
                    completion.TryComplete(); // A late timer must not turn a failed normal save into a second finalization.
                }
            }
        }
        // Caller owns Gate. The timer reads only detached cached facts and scalar report records.
        void WriteArtifacts()
        {
            File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
            if (!budgetMode) return;
            var snapshot = new NativePerformanceBudgetSnapshot(report.Status, report.Error, report.Stage, FileHash(output),
                report.Environment?.AppAssemblySha256, before, after, report.Samples.Select(sample => new NativePerformanceRawSample(
                    sample.Operation, sample.Index, sample.DispatcherMilliseconds, sample.LayoutMilliseconds, sample.AnimationTickMilliseconds,
                    sample.RenderedAndDispatchedMilliseconds, sample.LogicalRows, sample.ObjectRows, sample.RealizedRows, sample.MonitorEvents,
                    sample.ExplicitFeedEvents, sample.ProcessAllocatedBytesDelta, sample.ManagedBytes, sample.WorkingSetBytes)),
                report.Monitor is { } monitor ? new(monitor.ExplicitFeedEvents, monitor.RecordedEvents, monitor.DisplayRows,
                    monitor.FilteredDisplayRows, monitor.RealizedDisplayRows, monitor.WasRunningAtEnd) : null,
                report.Memory is { } memory ? new(memory.WorkingSetBytes, memory.PeakWorkingSetBytes) : null);
            budgetResult = NativePerformanceBudgetPolicy.Evaluate(snapshot);
            File.WriteAllText(budgetOutput, JsonSerializer.Serialize(budgetResult, JsonOptions));
        }
        // This watchdog also handles a synchronous UI-thread hang, which an awaited timeout cannot interrupt.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            lock (Gate)
            {
                if (budgetMode && !completion.TryLatchDeadline()) return;
                report.Status = "deadline"; report.Error = "The isolated performance process exceeded its 150-second safety deadline.";
                report.FinishedUtc = DateTimeOffset.UtcNow;
                try { WriteArtifacts(); }
                catch (Exception error) when (budgetMode) { Console.Error.WriteLine("Deadline artifact write failed: " + error.Message); }
            }
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
                .AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
                {
                    var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                    var shell = (WorkbenchWindow)lifetime.MainWindow!;
                    try
                    {
                        await Measure(shell, fixture, report, budgetMode ? () =>
                        {
                            var evidence = CaptureEvidence(shell, fixture);
                            lock (Gate) { if (completion.IsPending) before = evidence; }
                        } : null);
                        if (budgetMode)
                        {
                            var evidence = CaptureEvidence(shell, fixture);
                            using var process = Process.GetCurrentProcess(); process.Refresh();
                            lock (Gate)
                            {
                                if (completion.IsPending)
                                {
                                    after = evidence;
                                    if (report.Memory is { } memory) report.Memory = memory with { WorkingSetBytes = process.WorkingSet64, PeakWorkingSetBytes = process.PeakWorkingSet64 };
                                }
                            }
                        }
                        lock (Gate) { if (!budgetMode || completion.IsPending) report.Status = "passed"; }
                    }
                    catch (Exception error)
                    {
                        lock (Gate) { if (!budgetMode || completion.IsPending) { report.Status = error is NativeProgressUnavailableException ? "unavailable" : "failed"; report.Error = error.ToString(); } }
                    }
                    finally
                    {
                        lock (Gate) { if (!budgetMode || completion.IsPending) report.FinishedUtc = DateTimeOffset.UtcNow; }
                        Save();
                        lifetime.Shutdown(budgetMode ? artifactError is null ? budgetResult?.ExitCode ?? 1 : 1 : report.Status == "passed" ? 0 : 1);
                    }
                }, TimeSpan.FromMilliseconds(100)))
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        {
            lock (Gate) { if (!budgetMode || completion.IsPending) { report.Status = "failed"; report.Error = error.ToString(); report.FinishedUtc = DateTimeOffset.UtcNow; } }
        }
        finally
        {
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Save();
            App.TestWindowFactory = null;
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", previousSettings);
        }
        Console.WriteLine("Native performance JSON: " + output);
        Console.WriteLine("Native performance status: " + report.Status);
        if (budgetMode)
        {
            Console.WriteLine("Native performance budget JSON: " + budgetOutput);
            Console.WriteLine("Native performance budget status: " + (artifactError is null ? budgetResult?.Status ?? "invalid_measurement" : "artifact_write_failed"));
            if (artifactError is not null) Console.Error.WriteLine(artifactError);
            return artifactError is null ? budgetResult?.ExitCode ?? 1 : 1;
        }
        return report.Status switch { "passed" => 0, "unavailable" => 3, _ => 1 };
    }

    private static async Task Measure(WorkbenchWindow shell, Project project, Report report, Action? captureBefore = null)
    {
        var process = Process.GetCurrentProcess();
        using (process)
        {
            shell.RefreshWorkspace();
            var device = ProjectQuery.DeviceList(project).First();
            var narrow = shell.OpenTarget(new("device", device.Id), "objects");
            var wide = shell.OpenTarget(new("project", project.Id), "objects");
            shell.ShowTool("monitor");
            var sessions = (WorkbenchSessions?)typeof(WorkbenchWindow).GetProperty("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)
                ?? throw new InvalidOperationException("The application-owned sessions are missing.");
            var monitor = sessions.Monitor;
            var group = ProjectQuery.GroupAddresses(project).First();
            var feedCount = 0;
            var feed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            feed.Tick += (_, _) =>
            {
                monitor.TickDemo(new(DateTimeOffset.UnixEpoch.AddMilliseconds(++feedCount * 50L), "1.1.250", group.Address.ToString(),
                    "GroupValueWrite", "1.001", (feedCount % 2).ToString(), SessionOrigin.Demo, group.Id, device.Id));
            };
            var table = shell.FindControl<ListBox>("ObjectTable")!;
            var search = shell.FindControl<TextBox>("ObjectSearch")!;
            var expand = shell.FindControl<Button>("ExpandAllButton")!;
            var compositor = ElementComposition.GetElementVisual(shell)?.Compositor
                ?? throw new NativeProgressUnavailableException("No native compositor is attached; no render measurements can be claimed.");
            lock (Gate)
            {
                report.Environment = new(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                    typeof(Window).Assembly.GetName().Version?.ToString(), shell.RenderScaling, shell.Bounds.Width, shell.Bounds.Height,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WorkbenchWindow).Assembly.Location))).ToLowerInvariant());
                report.Stage = "warmup";
            }
            captureBefore?.Invoke();
            var cpuStart = process.TotalProcessorTime;
            monitor.Configure(WorkbenchMonitorMode.Group, SessionOrigin.Demo, "performance-demo-only");
            monitor.Start();
            try
            {
                feed.Start();
                // Each direction has its own distribution: unlike one long aggregate, slow expansion is not diluted by collapse.
                for (var cycle = -WarmupCycles; cycle < SampleCount; cycle++)
                {
                    await One("filter_exact", () => search.Text = "Perf object 00 249", () => wide.Filter == "Perf object 00 249", 1, cycle);
                    await One("filter_clear", () => search.Text = "", () => wide.Filter == "", DeviceCount * ObjectsPerDevice, cycle);
                    await One("collapse_all", () => expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), () => wide.CollapsedDeviceIds.Count == DeviceCount, 0, cycle);
                    await One("expand_all", () => expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), () => wide.CollapsedDeviceIds.Count == 0, DeviceCount * ObjectsPerDevice, cycle);
                    await One("tab_to_device", () => shell.Workspace.ActiveTab = narrow, () => ReferenceEquals(shell.Workspace.ActiveTab, narrow), ObjectsPerDevice, cycle);
                    await One("tab_to_project", () => shell.Workspace.ActiveTab = wide, () => ReferenceEquals(shell.Workspace.ActiveTab, wide), DeviceCount * ObjectsPerDevice, cycle);
                }
                monitor.FlushDisplay();
                var monitorRealized = shell.FindControl<ContentControl>("ToolHost")!.GetVisualDescendants().OfType<ListBoxItem>().Count();
                if (!monitor.IsRunning || feedCount < 2 || monitor.RecordedCount < 2 || monitor.Rows.Count == 0)
                    throw new InvalidOperationException("The concurrent demo-monitor/display feed did not advance.");
                if (monitorRealized <= 0) throw new InvalidOperationException("The running monitor has no realized native display rows.");
                lock (Gate)
                {
                    report.Summaries = report.Samples.GroupBy(sample => sample.Operation).Select(grouping =>
                    {
                        var values = grouping.Select(sample => sample.RenderedAndDispatchedMilliseconds).Order().ToArray();
                        return new Summary(grouping.Key, values.Length, Percentile(values, .50), Percentile(values, .95), values[^1]);
                    }).ToArray();
                    report.Monitor = new(feedCount, monitor.RecordedCount, monitor.Rows.Count, monitor.FilteredRows.Count,
                        monitorRealized, monitor.IsRunning);
                    process.Refresh();
                    report.Memory = new(GC.GetTotalMemory(false), GC.GetGCMemoryInfo().HeapSizeBytes, GC.GetTotalAllocatedBytes(false),
                        process.WorkingSet64, process.PeakWorkingSet64, process.PrivateMemorySize64, (process.TotalProcessorTime - cpuStart).TotalMilliseconds);
                }
            }
            finally { feed.Stop(); monitor.Stop(); }

            async Task One(string operation, Action action, Func<bool> stateReady, int expectedObjects, int cycle)
            {
                lock (Gate) report.Stage = operation + ":" + cycle;
                var watch = Stopwatch.StartNew();
                var allocated = GC.GetTotalAllocatedBytes(false);
                var layout = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Updated(object? sender, EventArgs args)
                {
                    if (stateReady()) layout.TrySetResult(watch.Elapsed.TotalMilliseconds);
                }
                shell.LayoutUpdated += Updated;
                try
                {
                    action();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    var dispatchedMs = watch.Elapsed.TotalMilliseconds;
                    if (!stateReady()) throw new InvalidOperationException(operation + " did not reach its expected control state.");
                    lock (Gate) report.Stage = operation + ":" + cycle + ":layout";
                    var layoutMs = await layout.Task.WaitAsync(SampleDeadline);
                    var frame = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    shell.RequestAnimationFrame(_ => frame.TrySetResult(watch.Elapsed.TotalMilliseconds));
                    var committed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                    compositor.RequestCompositionUpdate(() => committed.TrySetResult(compositor.RequestCompositionBatchCommitAsync().Rendered));
                    lock (Gate) report.Stage = operation + ":" + cycle + ":render";
                    var batchRendered = await committed.Task.WaitAsync(SampleDeadline);
                    await Task.WhenAll(batchRendered, frame.Task).WaitAsync(SampleDeadline);
                    // Always leave render-thread callbacks through the UI dispatcher before touching controls.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    watch.Stop();
                    var rows = ((IEnumerable<WorkbenchRow>)table.ItemsSource!).ToArray();
                    var objects = rows.Count(row => row.Object is not null);
                    var realized = table.GetVisualDescendants().OfType<ListBoxItem>().Count();
                    if (objects != expectedObjects || realized <= 0 || realized > Math.Min(rows.Length, 512))
                        throw new InvalidOperationException($"{operation}: wrong/empty/unbounded native rows ({objects} objects, {realized}/{rows.Length} realized).");
                    if (!monitor.IsRunning) throw new InvalidOperationException("The demo monitor stopped during measurement.");
                    if (cycle >= 0)
                    {
                        process.Refresh();
                        var sample = new Sample(operation, cycle, dispatchedMs, layoutMs, await frame.Task, watch.Elapsed.TotalMilliseconds,
                            rows.Length, objects, realized, monitor.RecordedCount, feedCount,
                            GC.GetTotalAllocatedBytes(false) - allocated, GC.GetTotalMemory(false), process.WorkingSet64);
                        lock (Gate) report.Samples.Add(sample);
                    }
                }
                catch (TimeoutException error)
                {
                    throw new NativeProgressUnavailableException("Native layout/frame/render progress was not observed before its safety deadline ("
                        + operation + "). A locked/occluded desktop or render backend may prevent this measurement; this is not a contractual timing failure.", error);
                }
                finally { shell.LayoutUpdated -= Updated; }
            }
        }
    }

    private static double Percentile(double[] sorted, double fraction) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static NativePerformanceEvidence CaptureEvidence(WorkbenchWindow shell, Project project) => new(
        new(RuntimeInformation.FrameworkDescription, Environment.Version.Major, RuntimeInformation.OSDescription,
            OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, System.Runtime.GCSettings.IsServerGC, typeof(Window).Assembly.GetName().Version?.ToString() ?? "",
            shell.RenderScaling, shell.Bounds.Width, shell.Bounds.Height, FileHash(typeof(WorkbenchWindow).Assembly.Location),
            FileHash(typeof(Project).Assembly.Location), FileHash(typeof(NativePerformanceChecks).Assembly.Location), Strings.Language, shell.ActualThemeVariant.ToString() ?? ""),
        CaptureFixture(project));

    internal static NativePerformanceFixtureEvidence CaptureFixture(Project project)
    {
        // Canonical UTF8 JSON array of positional rows, in actual model-list order. No timestamps or runtime identity.
        // The expected hash is generated independently from the literal 48x250 fixture formula.
        using var hash = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using var writer = new Utf8JsonWriter(sink);
        writer.WriteStartArray();
        Row("project", NativePerformanceBudgetPolicy.FixtureProfile, project.Id, project.Name, project.Comment, project.Guid,
            project.Installations.Count, project.Hardware.Count, project.ApplicationDataRevision, project.Workbench.Objects.Count,
            project.Workbench.Devices.Count, project.Workbench.Groups.Count, project.Workbench.FloorShortNames.Count, project.Workbench.Revisions.Count);
        foreach (var installation in project.Installations)
        {
            Row("installation", installation.Id, installation.Name, installation.Address?.ToString(), installation.Type.ToString(),
                installation.GroupAddressStyle.ToString(), installation.Areas.Count, installation.Locations.Roots.Count, installation.MainRanges.Count);
            foreach (var area in installation.Areas)
            {
                Row("area", area.Id, area.Name, area.Address, area.Devices.Count, area.Lines.Count);
                foreach (var line in area.Lines)
                {
                    Row("line", line.Id, line.Name, line.Address, line.MediumTypeRefId, line.DomainAddress, line.Segments.Count, line.Devices.Count);
                    foreach (var device in line.Devices)
                    {
                        var meta = project.Workbench.Devices.GetValueOrDefault(device.Id);
                        Row("device", device.Id, device.Name, device.Address.ToString(), device.ProductRefId, device.Hardware2ProgramRefId,
                            device.Description, device.Comment, device.Channels.Count, device.ParameterValues.Count, device.BinaryData.Count, device.HasOpaqueBinaryData,
                            meta?.Demo, meta?.SecureCapable, meta?.Application, meta?.Revision, meta?.DownloadedRevision, device.ComObjectRefs.Count);
                        foreach (var obj in device.ComObjectRefs)
                        {
                            var key = new ObjectKey(device.Id, obj.Id).ToString();
                            var metadata = project.Workbench.Objects.GetValueOrDefault(key);
                            Row("object", key, obj.Id, obj.ComObjectId, obj.Number, obj.Text, obj.FunctionText, obj.Device, obj.ChannelId, obj.Links.Count,
                                metadata?.Dpt, metadata?.Bits, metadata?.Flags, metadata?.Active, metadata?.ChannelId, metadata?.ChannelSource.ToString(),
                                metadata?.StructuralPreviousActive, metadata?.StructuralDeclaredNumber, metadata?.StructuralDeclaredChannelId,
                                metadata?.StructuralDeclaredChannelName, metadata?.Role, metadata?.SendingGroupId, metadata?.Connectors.Count);
                            foreach (var link in obj.Links) Row("link", obj.Id, link);
                        }
                    }
                }
            }
            foreach (var space in installation.Locations.AllNodes)
            {
                Row("space", space.Id, space.Name, space.Kind.ToString(), space.Parent?.Id, space.RawType, space.Number,
                    space.Description, space.Comment, space.Usage, space.Puid, space.Children.Count, space.DeviceRefs.Count);
                foreach (var deviceId in space.DeviceRefs) Row("installed", space.Id, deviceId);
            }
            foreach (var range in installation.MainRanges) Range(range);
        }
        writer.WriteEndArray(); writer.Flush(); sink.FlushFinalBlock();
        var devices = ProjectQuery.DeviceList(project).ToArray();
        return new(NativePerformanceBudgetPolicy.FixtureProfile, Convert.ToHexString(hash.Hash!).ToLowerInvariant(), devices.Length,
            devices.Select(d => d.ComObjectRefs.Count).Distinct().Count() == 1 ? devices[0].ComObjectRefs.Count : -1,
            devices.Sum(d => d.ComObjectRefs.Count), ProjectQuery.GroupAddresses(project).Count(), WarmupCycles, SampleCount, 50, true, true);

        void Row(params object?[] values)
        {
            writer.WriteStartArray();
            foreach (var value in values) JsonSerializer.Serialize(writer, value);
            writer.WriteEndArray();
        }
        void Range(GroupRange range)
        {
            Row("range", range.Id, range.Name, range.RangeStart, range.RangeEnd, range.Ranges.Count, range.Addresses.Count);
            foreach (var group in range.Addresses)
            {
                var metadata = project.Workbench.Groups.GetValueOrDefault(group.Id);
                Row("group", group.Id, group.Name, group.Address.ToString(), group.DatapointType, group.Description, group.Comment, group.Puid,
                    metadata?.Bits, metadata?.Secure, metadata?.EffectRoomId);
            }
            foreach (var child in range.Ranges) Range(child);
        }
    }

    internal static Project CreateFixture()
    {
        var project = new Project { Id = "P-UI-PERF-12000", Name = "Synthetic native performance" };
        var installation = new Installation { Id = "I-PERF", Name = "Synthetic" };
        var area = new Area { Id = "A-PERF", Address = 1 };
        var line = new Line { Id = "L-PERF", Address = 1, Name = "Synthetic line" };
        installation.Areas.Add(area); area.Lines.Add(line); project.Installations.Add(installation);
        var room = installation.Locations.Add(null, "Synthetic room", SpaceKind.Room, "R-PERF");
        var range = new GroupRange { Id = "GR-PERF", Name = "Synthetic groups", RangeStart = 0, RangeEnd = 65535 };
        installation.MainRanges.Add(range);
        for (var index = 0; index < 8; index++)
        {
            var group = new GroupAddressEntry { Id = "GA-PERF-" + index, Name = "Synthetic group " + index,
                Address = GroupAddress.ThreeLevel(1, 0, (byte)(index + 1)), DatapointType = "1.001" };
            range.Addresses.Add(group); project.Workbench.Groups[group.Id] = new() { Bits = 1, Secure = false, EffectRoomId = room.Id };
        }
        for (var index = 0; index < DeviceCount; index++)
        {
            var device = new DeviceInstance { Id = $"D-PERF-{index:D2}", Name = $"Synthetic device {index:D2}",
                Address = new(1, 1, (byte)(index + 1)), ProductRefId = "SYNTHETIC-NO-CATALOG" };
            line.Devices.Add(device); room.AssignDevice(device);
            project.Workbench.Devices[device.Id] = new() { Demo = true, SecureCapable = false, Application = "Synthetic performance fixture" };
            for (var number = 0; number < ObjectsPerDevice; number++)
            {
                var obj = new ComObjectInstanceRef { Id = $"{device.Id}_O-{number:D3}", ComObjectId = $"CO-{number:D3}",
                    Number = number, Text = $"Perf object {index:D2} {number:D3}" };
                obj.Links.Add("GA-PERF-" + number % 8); device.ComObjectRefs.Add(obj);
                project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { Dpt = "1.001", Bits = 1, Flags = "CT" };
            }
        }
        return project;
    }

    private sealed class Report
    {
        public string Format { get; } = "BusChord.NativeUiPerformance.v1";
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string Status { get; set; } = "running";
        public string Stage { get; set; } = "startup";
        public string? Error { get; set; }
        public object Fixture { get; } = new { DeviceCount, ObjectsPerDevice, ObjectCount = DeviceCount * ObjectsPerDevice, GroupCount = 8,
            WindowWidthDip = 1440, WindowHeightDip = 900, WarmupCycles, SamplesPerOperation = SampleCount, DemoFeedIntervalMs = 50,
            VisibleMonitor = true, BuiltInDemoFeedAlsoActive = true };
        public string Measurement { get; } = "Control change -> observed layout -> animation tick + rendered composition batch -> ContextIdle dispatcher; not physical display presentation or manual input latency.";
        public object Guards { get; } = new { AsyncWaitSeconds = 8, WholeProcessSeconds = 150, MaximumRealizedRows = 512, ContractualPerformanceBudget = (string?)null };
        public string Percentiles { get; } = "Nearest rank: sorted[ceil(fraction * n)-1]; warmup excluded, no timing pass/fail threshold.";
        public string ManualVisibility { get; } = "Not inspected; native automated rendering does not prove an unlocked desktop, physical presentation or manual accessibility.";
        public EnvironmentInfo? Environment { get; set; }
        public List<Sample> Samples { get; } = [];
        public Summary[] Summaries { get; set; } = [];
        public MonitorInfo? Monitor { get; set; }
        public MemoryInfo? Memory { get; set; }
    }
    private sealed record EnvironmentInfo(string Runtime, string OS, string Architecture, string? AvaloniaVersion,
        double RenderScaling, double WindowWidthDip, double WindowHeightDip, string AppAssemblySha256);
    private sealed record Sample(string Operation, int Index, double DispatcherMilliseconds, double LayoutMilliseconds,
        double AnimationTickMilliseconds, double RenderedAndDispatchedMilliseconds, int LogicalRows, int ObjectRows, int RealizedRows,
        long MonitorEvents, int ExplicitFeedEvents, long ProcessAllocatedBytesDelta, long ManagedBytes, long WorkingSetBytes);
    private sealed record Summary(string Operation, int Count, double P50Milliseconds, double P95Milliseconds, double MaximumMilliseconds);
    private sealed record MonitorInfo(int ExplicitFeedEvents, long RecordedEvents, int DisplayRows, int FilteredDisplayRows, int RealizedDisplayRows, bool WasRunningAtEnd);
    private sealed record MemoryInfo(long ManagedBytes, long LastGcHeapBytes, long ProcessAllocatedBytes, long WorkingSetBytes,
        long PeakWorkingSetBytes, long PrivateBytes, double CpuMilliseconds);
    private sealed class NativeProgressUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
}
