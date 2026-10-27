using System.Collections.ObjectModel;

internal sealed record NativePerformanceEnvironment(string Runtime, int RuntimeMajor, string OS, bool IsWindows,
    string OSArchitecture, string Architecture, int ProcessorCount, bool ServerGc, string AvaloniaVersion,
    double RenderScaling, double WindowWidthDip, double WindowHeightDip,
    string AppAssemblySha256, string CoreAssemblySha256, string HarnessAssemblySha256, string Language, string Theme);

internal sealed record NativePerformanceFixtureEvidence(string Profile, string Sha256, int DeviceCount,
    int ObjectsPerDevice, int ObjectCount, int GroupCount, int WarmupCycles, int SamplesPerOperation,
    int DemoFeedIntervalMs, bool VisibleMonitor, bool BuiltInDemoFeedAlsoActive);

internal sealed record NativePerformanceEvidence(NativePerformanceEnvironment Environment, NativePerformanceFixtureEvidence Fixture);
internal sealed record NativePerformanceRawSample(string Operation, int Index, double DispatcherMilliseconds,
    double LayoutMilliseconds, double AnimationTickMilliseconds, double RenderedAndDispatchedMilliseconds,
    int LogicalRows, int ObjectRows, int RealizedRows, long MonitorEvents, int ExplicitFeedEvents,
    long ProcessAllocatedBytesDelta, long ManagedBytes, long WorkingSetBytes);
internal sealed record NativePerformanceMonitorEvidence(int ExplicitFeedEvents, long RecordedEvents,
    int DisplayRows, int FilteredDisplayRows, int RealizedDisplayRows, bool WasRunningAtEnd);
internal sealed record NativePerformanceMemoryEvidence(long WorkingSetBytes, long PeakWorkingSetBytes);

/// <summary>Detached run data; no controls, model objects, derived summaries or executable callbacks.</summary>
internal sealed class NativePerformanceBudgetSnapshot
{
    internal string MeasurementStatus { get; }
    internal string? Error { get; }
    internal string Stage { get; }
    internal string MeasurementsSha256 { get; }
    internal string? MeasurementAppAssemblySha256 { get; }
    internal NativePerformanceEvidence? Before { get; }
    internal NativePerformanceEvidence? After { get; }
    internal IReadOnlyList<NativePerformanceRawSample> Samples { get; }
    internal NativePerformanceMonitorEvidence? Monitor { get; }
    internal NativePerformanceMemoryEvidence? Memory { get; }

    internal NativePerformanceBudgetSnapshot(string measurementStatus, string? error, string stage, string measurementsSha256,
        string? measurementAppAssemblySha256, NativePerformanceEvidence? before, NativePerformanceEvidence? after,
        IEnumerable<NativePerformanceRawSample>? samples, NativePerformanceMonitorEvidence? monitor,
        NativePerformanceMemoryEvidence? memory)
    {
        MeasurementStatus = measurementStatus; Error = error; Stage = stage; MeasurementsSha256 = measurementsSha256;
        MeasurementAppAssemblySha256 = measurementAppAssemblySha256; Before = before; After = after;
        // Retain one overflow sentinel; even a hostile enumerable cannot allocate an unbounded list here.
        Samples = Array.AsReadOnly((samples ?? []).Take(145).ToArray()); Monitor = monitor; Memory = memory;
    }
}

internal sealed record NativePerformanceBudgetIssue(string Code, string? Operation = null,
    double? Actual = null, double? Limit = null, string? Detail = null, long? ActualBytes = null, long? LimitBytes = null);
internal sealed record NativePerformanceBudgetStatistics(string Operation, int Count, double P50Milliseconds,
    double P95Milliseconds, double MaximumMilliseconds);
internal sealed record NativePerformanceBudgetLimits(double P95Milliseconds, double MaximumMilliseconds, long PeakWorkingSetBytes);

internal sealed class NativePerformanceBudgetResult
{
    public string Format => "BusChord.NativeUiPerformanceBudget.v1";
    public string ReferenceProfile => "WindowsX64.DotNet10.Avalonia12.1.4.12000Objects.v1";
    public string Scope => "Engineering regression targets for this reference fixture; no universal hardware, physical presentation, input or accessibility guarantee.";
    public string Status { get; }
    public string MeasurementStatus { get; }
    public string Stage { get; }
    public string MeasurementsSha256 { get; }
    public NativePerformanceEvidence? Before { get; }
    public NativePerformanceEvidence? After { get; }
    public int SampleCount { get; }
    public long? PeakWorkingSetBytes { get; }
    public NativePerformanceBudgetLimits Limits { get; } = new(500, 1500, 512L * 1024 * 1024);
    public IReadOnlyList<NativePerformanceBudgetStatistics> Statistics { get; }
    public IReadOnlyList<NativePerformanceBudgetIssue> Issues { get; }
    internal int ExitCode => Status switch { "passed" => 0, "unavailable" => 3, "deadline" => 2, _ => 1 };

    internal NativePerformanceBudgetResult(string status, NativePerformanceBudgetSnapshot snapshot,
        IEnumerable<NativePerformanceBudgetIssue> issues, IEnumerable<NativePerformanceBudgetStatistics>? statistics = null)
    {
        Status = status; MeasurementStatus = snapshot.MeasurementStatus; Stage = snapshot.Stage;
        MeasurementsSha256 = snapshot.MeasurementsSha256; Before = snapshot.Before; After = snapshot.After;
        SampleCount = snapshot.Samples.Count; PeakWorkingSetBytes = snapshot.Memory?.PeakWorkingSetBytes;
        Issues = new ReadOnlyCollection<NativePerformanceBudgetIssue>(issues.ToArray());
        Statistics = new ReadOnlyCollection<NativePerformanceBudgetStatistics>((statistics ?? []).ToArray());
    }
}

/// <summary>Called under the producer's single Gate; a deadline cannot later become success.</summary>
internal sealed class NativePerformanceBudgetCompletion
{
    private int state;
    internal bool IsPending => Volatile.Read(ref state) == 0;
    internal bool IsDeadline => Volatile.Read(ref state) == 2;
    internal bool TryLatchDeadline() => Interlocked.CompareExchange(ref state, 2, 0) == 0;
    internal bool TryComplete() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
}

internal static class NativePerformanceBudgetPolicy
{
    internal const string FixtureProfile = "BusChord.SyntheticNative12000.v1";
    // Independently generated from the literal fixture formula; see CaptureFixture's ordered JSON rows.
    internal const string FixtureSha256 = "a91a5c7031c9b4f6d18f9061b8db714d568f07ea7816f3162a5dd0bc1907b443";
    internal const long MaximumPeakBytes = 512L * 1024 * 1024;
    internal static readonly IReadOnlyList<string> Operations = Array.AsReadOnly(new[] { "filter_exact", "filter_clear", "collapse_all", "expand_all", "tab_to_device", "tab_to_project" });
    private static readonly int[] LogicalRows = [2, 12048, 48, 12048, 251, 12048];
    private static readonly int[] ObjectRows = [1, 12000, 0, 12000, 250, 12000];

    internal static NativePerformanceBudgetResult Evaluate(NativePerformanceBudgetSnapshot snapshot)
    {
        var issues = new List<NativePerformanceBudgetIssue>();
        NativePerformanceBudgetResult Result(string status, string code) => new(status, snapshot, [new(code, Detail: Bounded(snapshot.Error))]);
        // Incomplete runs have no complete distribution, regardless of any partial fast samples.
        if (snapshot.MeasurementStatus == "deadline") return Result("deadline", "process_deadline");
        if (snapshot.MeasurementStatus == "unavailable") return Result("unavailable", "native_progress_unavailable");
        if (snapshot.MeasurementStatus == "failed") return Result("measurement_failed", "native_measurement_failed");
        if (snapshot.MeasurementStatus != "passed") return Result("invalid_measurement", "incomplete_or_unknown_status");
        void Invalid(string code, string? operation = null) => issues.Add(new(code, Bounded(operation)));
        if (snapshot.Stage != "tab_to_project:23:render" || snapshot.Error is not null || !Hash(snapshot.MeasurementsSha256)) Invalid("invalid_run_provenance");
        var before = snapshot.Before; var after = snapshot.After;
        if (before is null || after is null) Invalid("missing_before_or_after_evidence");
        else
        {
            if (!ValidEnvironment(before.Environment) || !ValidEnvironment(after.Environment)) Invalid("invalid_environment_facts");
            if (!ValidFixture(before.Fixture) || !ValidFixture(after.Fixture)) Invalid("fixture_mismatch");
            if (before != after) Invalid("environment_fixture_or_assembly_changed");
            if (!Hash(snapshot.MeasurementAppAssemblySha256) || snapshot.MeasurementAppAssemblySha256 != before.Environment?.AppAssemblySha256)
                Invalid("measurement_assembly_mismatch");
        }
        if (snapshot.Samples.Count != 144) Invalid("sample_count");
        long previousMonitor = -1, firstMonitor = -1, maximumWorkingSet = 0;
        int previousFeed = -1, firstFeed = -1;
        for (var index = 0; index < snapshot.Samples.Count; index++)
        {
            var sample = snapshot.Samples[index];
            if (sample is null) { Invalid("null_sample"); continue; }
            var operation = index % 6;
            if (sample.Operation != Operations[operation] || sample.Index != index / 6) Invalid("sample_order_or_identity", sample.Operation);
            if (!Finite(sample.DispatcherMilliseconds) || !Finite(sample.LayoutMilliseconds) || !Finite(sample.AnimationTickMilliseconds)
                || !Finite(sample.RenderedAndDispatchedMilliseconds) || sample.RenderedAndDispatchedMilliseconds <= 0
                || sample.RenderedAndDispatchedMilliseconds < Math.Max(sample.DispatcherMilliseconds, Math.Max(sample.LayoutMilliseconds, sample.AnimationTickMilliseconds)))
                Invalid("invalid_sample_times", sample.Operation);
            if (sample.ObjectRows != ObjectRows[operation] || sample.LogicalRows != LogicalRows[operation]
                || sample.RealizedRows <= 0 || sample.RealizedRows > Math.Min(sample.LogicalRows, 512)) Invalid("invalid_rows", sample.Operation);
            if (sample.MonitorEvents < 0 || sample.ExplicitFeedEvents < 0 || sample.MonitorEvents < previousMonitor || sample.ExplicitFeedEvents < previousFeed)
                Invalid("nonmonotonic_monitor_or_feed", sample.Operation);
            if (index == 0) { firstMonitor = sample.MonitorEvents; firstFeed = sample.ExplicitFeedEvents; }
            previousMonitor = sample.MonitorEvents; previousFeed = sample.ExplicitFeedEvents;
            if (sample.ProcessAllocatedBytesDelta < 0 || sample.ManagedBytes <= 0 || sample.WorkingSetBytes <= 0) Invalid("invalid_sample_memory", sample.Operation);
            maximumWorkingSet = Math.Max(maximumWorkingSet, sample.WorkingSetBytes);
        }
        var monitor = snapshot.Monitor;
        if (monitor is null || !monitor.WasRunningAtEnd || monitor.ExplicitFeedEvents < 2 || monitor.RecordedEvents < 2
            || monitor.DisplayRows <= 0 || monitor.FilteredDisplayRows <= 0 || monitor.RealizedDisplayRows <= 0
            || monitor.DisplayRows > monitor.RecordedEvents || monitor.FilteredDisplayRows > monitor.DisplayRows || monitor.RealizedDisplayRows > monitor.FilteredDisplayRows
            || monitor.RecordedEvents < previousMonitor || monitor.ExplicitFeedEvents < previousFeed
            || monitor.RecordedEvents <= firstMonitor || monitor.ExplicitFeedEvents <= firstFeed)
            Invalid("missing_or_inconsistent_monitor_progress");
        var memory = snapshot.Memory;
        if (memory is null || memory.WorkingSetBytes <= 0 || memory.PeakWorkingSetBytes <= 0
            || memory.PeakWorkingSetBytes < Math.Max(maximumWorkingSet, memory.WorkingSetBytes)) Invalid("missing_or_inconsistent_process_peak");
        if (issues.Count != 0) return new("invalid_measurement", snapshot, issues);
        if (!ReferenceEnvironment(before!.Environment!)) return Result("unavailable", "unsupported_reference_environment");
        var statistics = Operations.Select(operation =>
        {
            var values = snapshot.Samples.Where(s => s.Operation == operation).Select(s => s.RenderedAndDispatchedMilliseconds).Order().ToArray();
            return new NativePerformanceBudgetStatistics(operation, values.Length, Percentile(values, .5), Percentile(values, .95), values[^1]);
        }).ToArray();
        foreach (var statistic in statistics)
        {
            if (statistic.P95Milliseconds > 500) issues.Add(new("p95_exceeded", statistic.Operation, statistic.P95Milliseconds, 500));
            if (statistic.MaximumMilliseconds > 1500) issues.Add(new("maximum_exceeded", statistic.Operation, statistic.MaximumMilliseconds, 1500));
        }
        if (memory!.PeakWorkingSetBytes > MaximumPeakBytes) issues.Add(new("peak_working_set_exceeded", ActualBytes: memory.PeakWorkingSetBytes, LimitBytes: MaximumPeakBytes));
        return new(issues.Count == 0 ? "passed" : "budget_failed", snapshot, issues, statistics);
    }

    internal static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
    private static bool Finite(double value) => double.IsFinite(value) && value >= 0;
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192;
    private static string? Bounded(string? value) => value is { Length: > 8192 } ? value[..8192] : value;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidEnvironment(NativePerformanceEnvironment? value) => value is not null
        && Text(value.Runtime) && value.RuntimeMajor > 0 && Text(value.OS) && Text(value.OSArchitecture) && Text(value.Architecture)
        && value.ProcessorCount > 0 && Text(value.AvaloniaVersion) && Text(value.Language) && Text(value.Theme) && double.IsFinite(value.RenderScaling) && value.RenderScaling > 0
        && double.IsFinite(value.WindowWidthDip) && value.WindowWidthDip > 0 && double.IsFinite(value.WindowHeightDip) && value.WindowHeightDip > 0
        && Hash(value.AppAssemblySha256) && Hash(value.CoreAssemblySha256) && Hash(value.HarnessAssemblySha256);
    private static bool ReferenceEnvironment(NativePerformanceEnvironment value) => value.IsWindows && value.OSArchitecture == "X64"
        && value.Architecture == "X64" && value.RuntimeMajor == 10 && value.AvaloniaVersion == "12.1.4.0"
        && value.RenderScaling == 1 && value.WindowWidthDip == 1440 && value.WindowHeightDip == 900 && value.Language == "en" && value.Theme == "Light";
    private static bool ValidFixture(NativePerformanceFixtureEvidence? value) => value is not null && value.Profile == FixtureProfile
        && value.Sha256 == FixtureSha256 && value.DeviceCount == 48 && value.ObjectsPerDevice == 250 && value.ObjectCount == 12000
        && value.GroupCount == 8 && value.WarmupCycles == 3 && value.SamplesPerOperation == 24 && value.DemoFeedIntervalMs == 50
        && value.VisibleMonitor && value.BuiltInDemoFeedAlsoActive;
}
