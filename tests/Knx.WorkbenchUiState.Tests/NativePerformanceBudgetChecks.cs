internal static class NativePerformanceBudgetChecks
{
    internal static void Run(Action<string, bool> check)
    {
        void Check(string name, bool value) => check("Performance budget: " + name, value);
        var baseline = ValidSnapshot();
        var result = NativePerformanceBudgetPolicy.Evaluate(baseline);
        Check("complete literal reference passes", result.Status == "passed" && result.ExitCode == 0 && result.Statistics.Count == 6 && result.SampleCount == 144);
        var rank = WithOperationTimes(baseline, Enumerable.Range(1, 24).Select(i => (double)i).ToArray());
        var rankResult = NativePerformanceBudgetPolicy.Evaluate(rank);
        Check("literal 1..24 nearest ranks are 12 / 23 / 24", rankResult.Status == "passed" && rankResult.Statistics[0] is { P50Milliseconds: 12, P95Milliseconds: 23, MaximumMilliseconds: 24 });
        var boundary = WithOperationTimes(baseline, Enumerable.Repeat(100d, 22).Concat([500d, 1500d]).ToArray());
        var boundaryResult = NativePerformanceBudgetPolicy.Evaluate(Copy(boundary, memory: new(128L << 20, 512L << 20)));
        Check("all three exact inclusive limits pass", boundaryResult.Status == "passed" && boundaryResult.Statistics[0] is { P95Milliseconds: 500, MaximumMilliseconds: 1500 } && boundaryResult.PeakWorkingSetBytes == 536870912);
        var p95 = NativePerformanceBudgetPolicy.Evaluate(WithOperationTimes(baseline, Enumerable.Repeat(100d, 22).Concat([500.0001, 1500d]).ToArray()));
        Check("23rd value above 500 fails p95 without max failure", p95.Status == "budget_failed" && p95.Issues.Count == 1 && p95.Issues[0] is { Code: "p95_exceeded", Operation: "filter_exact", Actual: 500.0001, Limit: 500 });
        var maximum = NativePerformanceBudgetPolicy.Evaluate(WithOperationTimes(baseline, Enumerable.Repeat(100d, 23).Append(1500.0001).ToArray()));
        Check("one value above 1500 fails only maximum", maximum.Status == "budget_failed" && maximum.Issues.Count == 1 && maximum.Issues[0] is { Code: "maximum_exceeded", Actual: 1500.0001, Limit: 1500 });
        var memory = NativePerformanceBudgetPolicy.Evaluate(Copy(baseline, memory: new(128L << 20, 536870913)));
        Check("one byte over peak limit fails", memory.Status == "budget_failed" && memory.Issues.Single() is { Code: "peak_working_set_exceeded", ActualBytes: 536870913, LimitBytes: 536870912 } && memory.ExitCode == 1);
        var hugeMemory = NativePerformanceBudgetPolicy.Evaluate(Copy(baseline, memory: new(128L << 20, long.MaxValue)));
        Check("Int64 memory issue retains exact bytes without Double rounding", hugeMemory.Status == "budget_failed" && hugeMemory.Issues.Single().ActualBytes == long.MaxValue);
        var allSlow = baseline.Samples.Select(s => s with { RenderedAndDispatchedMilliseconds = 1600 }).ToArray();
        var allFailures = NativePerformanceBudgetPolicy.Evaluate(Copy(baseline, samples: allSlow, memory: new(128L << 20, 536870913)));
        Check("six separate distributions plus process peak retain all 13 violations", allFailures.Status == "budget_failed" && allFailures.Issues.Count == 13);
        Check("favorable external summaries have no input authority", typeof(NativePerformanceBudgetSnapshot).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).All(p => p.Name != "Summaries")
            && p95.Statistics[0].P95Milliseconds == 500.0001);

        void Invalid(string name, NativePerformanceBudgetSnapshot snapshot, string code)
        {
            var evaluated = NativePerformanceBudgetPolicy.Evaluate(snapshot);
            Check(name, evaluated.Status == "invalid_measurement" && evaluated.ExitCode == 1 && evaluated.Issues.Any(i => i.Code == code) && evaluated.Statistics.Count == 0);
        }
        Invalid("143 samples cannot pass", Copy(baseline, samples: baseline.Samples.Take(143)), "sample_count");
        Invalid("null sample sequence controlled", new(baseline.MeasurementStatus, null, baseline.Stage, baseline.MeasurementsSha256, baseline.MeasurementAppAssemblySha256,
            baseline.Before, baseline.After, null, baseline.Monitor, baseline.Memory), "sample_count");
        Invalid("145 samples cannot pass", Copy(baseline, samples: baseline.Samples.Append(baseline.Samples[0])), "sample_count");
        var bounded = Copy(baseline, samples: Enumerable.Repeat(baseline.Samples[0], 10000));
        Check("snapshot retains bounded overflow sentinel", bounded.Samples.Count == 145);
        Invalid("bounded overflow remains invalid", bounded, "sample_count");
        Invalid("duplicate index cannot replace next cycle", Change(baseline, 6, s => s with { Index = 0 }), "sample_order_or_identity");
        Invalid("negative index rejected", Change(baseline, 0, s => s with { Index = -1 }), "sample_order_or_identity");
        Invalid("index 24 rejected", Change(baseline, 138, s => s with { Index = 24 }), "sample_order_or_identity");
        var reordered = baseline.Samples.ToArray(); (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        Invalid("operation order matters", Copy(baseline, samples: reordered), "sample_order_or_identity");
        Invalid("renamed operation rejected", Change(baseline, 0, s => s with { Operation = "other" }), "sample_order_or_identity");
        Invalid("oversized operation rejected", Change(baseline, 0, s => s with { Operation = new('x', 8193) }), "sample_order_or_identity");
        var nullRow = baseline.Samples.ToArray(); nullRow[12] = null!;
        Invalid("null sample controlled", Copy(baseline, samples: nullRow), "null_sample");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.1 })
        {
            Invalid("nonfinite/negative total " + invalid, Change(baseline, 0, s => s with { RenderedAndDispatchedMilliseconds = invalid }), "invalid_sample_times");
            Invalid("nonfinite/negative dispatch " + invalid, Change(baseline, 0, s => s with { DispatcherMilliseconds = invalid }), "invalid_sample_times");
            Invalid("nonfinite/negative layout " + invalid, Change(baseline, 0, s => s with { LayoutMilliseconds = invalid }), "invalid_sample_times");
            Invalid("nonfinite/negative animation " + invalid, Change(baseline, 0, s => s with { AnimationTickMilliseconds = invalid }), "invalid_sample_times");
        }
        Invalid("zero total cannot pass", Change(baseline, 0, s => s with { RenderedAndDispatchedMilliseconds = 0 }), "invalid_sample_times");
        Invalid("total behind dispatcher rejected", Change(baseline, 0, s => s with { DispatcherMilliseconds = 101 }), "invalid_sample_times");
        Invalid("total behind layout rejected", Change(baseline, 0, s => s with { LayoutMilliseconds = 101 }), "invalid_sample_times");
        Invalid("total behind animation rejected", Change(baseline, 0, s => s with { AnimationTickMilliseconds = 101 }), "invalid_sample_times");
        Check("layout before or after dispatcher is admissible", NativePerformanceBudgetPolicy.Evaluate(Change(baseline, 0, s => s with { DispatcherMilliseconds = 10, LayoutMilliseconds = 5 })).Status == "passed"
            && NativePerformanceBudgetPolicy.Evaluate(Change(baseline, 0, s => s with { DispatcherMilliseconds = 5, LayoutMilliseconds = 10 })).Status == "passed");
        Invalid("logical row count exact", Change(baseline, 0, s => s with { LogicalRows = 3 }), "invalid_rows");
        Invalid("object row count exact", Change(baseline, 0, s => s with { ObjectRows = 2 }), "invalid_rows");
        Invalid("zero realized rows rejected", Change(baseline, 0, s => s with { RealizedRows = 0 }), "invalid_rows");
        Invalid("realized above logical rejected", Change(baseline, 0, s => s with { RealizedRows = 3 }), "invalid_rows");
        Invalid("513 realized rows rejected", Change(baseline, 1, s => s with { RealizedRows = 513 }), "invalid_rows");
        Check("realization cap equality and non-18 counts allowed", NativePerformanceBudgetPolicy.Evaluate(Change(baseline, 1, s => s with { RealizedRows = 512 })).Status == "passed");
        Invalid("negative recorded counter rejected", Change(baseline, 0, s => s with { MonitorEvents = -1 }), "nonmonotonic_monitor_or_feed");
        Invalid("recorded regression rejected", Change(baseline, 10, s => s with { MonitorEvents = 1 }), "nonmonotonic_monitor_or_feed");
        Invalid("feed regression rejected", Change(baseline, 10, s => s with { ExplicitFeedEvents = 1 }), "nonmonotonic_monitor_or_feed");
        Check("adjacent counter plateaus permitted", NativePerformanceBudgetPolicy.Evaluate(Change(baseline, 1, s => s with { MonitorEvents = 0, ExplicitFeedEvents = 0 })).Status == "passed");
        Invalid("whole-run counter plateau rejected", Copy(baseline, samples: baseline.Samples.Select(s => s with { MonitorEvents = 144, ExplicitFeedEvents = 144 })), "missing_or_inconsistent_monitor_progress");
        Invalid("negative allocation delta rejected", Change(baseline, 0, s => s with { ProcessAllocatedBytesDelta = -1 }), "invalid_sample_memory");
        Invalid("zero managed bytes rejected", Change(baseline, 0, s => s with { ManagedBytes = 0 }), "invalid_sample_memory");
        Invalid("zero working set rejected", Change(baseline, 0, s => s with { WorkingSetBytes = 0 }), "invalid_sample_memory");
        Invalid("sample working set above producer peak rejected", Change(baseline, 0, s => s with { WorkingSetBytes = 300L << 20 }), "missing_or_inconsistent_process_peak");
        Invalid("missing memory rejected", Copy(baseline, noMemory: true), "missing_or_inconsistent_process_peak");
        Invalid("peak below final working set rejected", Copy(baseline, memory: new(300L << 20, 256L << 20)), "missing_or_inconsistent_process_peak");
        Invalid("missing monitor rejected", Copy(baseline, noMonitor: true), "missing_or_inconsistent_monitor_progress");
        foreach (var monitor in new[] { baseline.Monitor! with { WasRunningAtEnd = false }, baseline.Monitor! with { ExplicitFeedEvents = 1 },
            baseline.Monitor! with { RecordedEvents = 1 }, baseline.Monitor! with { RecordedEvents = 142 }, baseline.Monitor! with { ExplicitFeedEvents = 142 },
            baseline.Monitor! with { DisplayRows = 0 }, baseline.Monitor! with { FilteredDisplayRows = 0 }, baseline.Monitor! with { RealizedDisplayRows = 0 },
            baseline.Monitor! with { DisplayRows = 145 },
            baseline.Monitor! with { FilteredDisplayRows = 11 }, baseline.Monitor! with { RealizedDisplayRows = 11 } })
            Invalid("inconsistent final monitor " + monitor, Copy(baseline, monitor: monitor), "missing_or_inconsistent_monitor_progress");
        Invalid("missing before facts rejected", Copy(baseline, noBefore: true), "missing_before_or_after_evidence");
        Invalid("missing after facts rejected", Copy(baseline, noAfter: true), "missing_before_or_after_evidence");
        Invalid("null environment controlled", Both(baseline, baseline.Before! with { Environment = null! }), "invalid_environment_facts");
        Invalid("null fixture controlled", Both(baseline, baseline.Before! with { Fixture = null! }), "fixture_mismatch");
        Invalid("raw byte SHA required", Copy(baseline, sha: ""), "invalid_run_provenance");
        Invalid("raw measurement App hash must match", Copy(baseline, appSha: new('b', 64)), "measurement_assembly_mismatch");
        Invalid("passed cannot carry an error", Copy(baseline, error: "failed"), "invalid_run_provenance");
        Invalid("stage required", Copy(baseline, stage: ""), "invalid_run_provenance");
        Invalid("passed stage must be final measured action", Copy(baseline, stage: "warmup"), "invalid_run_provenance");
        foreach (var invalidEnvironment in new[] { baseline.Before!.Environment with { RenderScaling = double.NaN },
            baseline.Before.Environment with { WindowWidthDip = double.PositiveInfinity }, baseline.Before.Environment with { ProcessorCount = 0 } })
            Invalid("invalid finite environment facts " + invalidEnvironment, Both(baseline, baseline.Before with { Environment = invalidEnvironment }), "invalid_environment_facts");
        Invalid("8193-char provenance rejected", Both(baseline, baseline.Before! with { Environment = baseline.Before.Environment with { Runtime = new('x', 8193) } }), "invalid_environment_facts");
        Invalid("malformed assembly hash rejected", Both(baseline, baseline.Before! with { Environment = baseline.Before.Environment with { CoreAssemblySha256 = "not-sha" } }), "invalid_environment_facts");
        foreach (var environment in new[] { baseline.After!.Environment with { AppAssemblySha256 = new('b', 64) },
            baseline.After.Environment with { CoreAssemblySha256 = new('b', 64) }, baseline.After.Environment with { HarnessAssemblySha256 = new('b', 64) } })
            Invalid("assembly changed " + environment, Copy(baseline, after: baseline.After with { Environment = environment }), "environment_fixture_or_assembly_changed");
        foreach (var fixture in new[] { baseline.Before!.Fixture with { Sha256 = new('b', 64) }, baseline.Before.Fixture with { DeviceCount = 47 },
            baseline.Before.Fixture with { ObjectsPerDevice = 249 }, baseline.Before.Fixture with { ObjectCount = 11999 }, baseline.Before.Fixture with { GroupCount = 7 },
            baseline.Before.Fixture with { WarmupCycles = 2 }, baseline.Before.Fixture with { SamplesPerOperation = 23 }, baseline.Before.Fixture with { DemoFeedIntervalMs = 51 },
            baseline.Before.Fixture with { VisibleMonitor = false }, baseline.Before.Fixture with { BuiltInDemoFeedAlsoActive = false }, baseline.Before.Fixture with { Profile = "other" } })
            Invalid("fixture fact exact " + fixture, Both(baseline, baseline.Before with { Fixture = fixture }), "fixture_mismatch");
        foreach (var environment in new[] { baseline.Before!.Environment with { IsWindows = false }, baseline.Before.Environment with { Architecture = "Arm64" },
            baseline.Before.Environment with { OSArchitecture = "Arm64" }, baseline.Before.Environment with { RuntimeMajor = 11 }, baseline.Before.Environment with { AvaloniaVersion = "12.2.0.0" },
            baseline.Before.Environment with { RenderScaling = 1.5 }, baseline.Before.Environment with { WindowWidthDip = 1439 }, baseline.Before.Environment with { WindowHeightDip = 899 },
            baseline.Before.Environment with { Language = "de" }, baseline.Before.Environment with { Theme = "Dark" } })
        {
            var unavailable = NativePerformanceBudgetPolicy.Evaluate(Both(baseline, baseline.Before with { Environment = environment }));
            Check("unsupported reference is unavailable " + environment, unavailable.Status == "unavailable" && unavailable.ExitCode == 3 && unavailable.Statistics.Count == 0);
        }
        foreach (var (status, expected, exit) in new[] { ("unavailable", "unavailable", 3), ("deadline", "deadline", 2), ("failed", "measurement_failed", 1), ("running", "invalid_measurement", 1), ("other", "invalid_measurement", 1) })
        {
            var incomplete = NativePerformanceBudgetPolicy.Evaluate(Copy(baseline, samples: [], status: status, noAfter: true));
            Check("incomplete " + status + " never passes", incomplete.Status == expected && incomplete.ExitCode == exit && incomplete.Statistics.Count == 0);
        }
        var samples = baseline.Samples.ToArray(); var owned = Copy(baseline, samples: samples); samples[0] = samples[0] with { Index = 99 };
        Check("sample collection defensively copied", owned.Samples[0].Index == 0 && owned.Samples is not NativePerformanceRawSample[]);
        var deadline = new NativePerformanceBudgetCompletion();
        Check("pending deadline claims once", deadline.IsPending && deadline.TryLatchDeadline() && deadline.IsDeadline && !deadline.IsPending && !deadline.TryLatchDeadline());
        Check("late success cannot replace terminal deadline", !deadline.TryComplete() && deadline.IsDeadline);
        var completed = new NativePerformanceBudgetCompletion();
        Check("completed save claims once", completed.TryComplete() && !completed.TryComplete() && !completed.IsPending);
        Check("late timer cannot replace completed artifacts", !completed.TryLatchDeadline() && !completed.IsDeadline);

        var actual = NativePerformanceChecks.CreateFixture();
        var actualEvidence = NativePerformanceChecks.CaptureFixture(actual);
        Check("actual stable model projection equals independent literal hash", actualEvidence == baseline.Before!.Fixture);
        var firstDevice = Knx.ProjectQuery.DeviceList(actual).First(); var first = firstDevice.ComObjectRefs[0];
        first.Text += " changed";
        Check("actual text edit changes fixture proof", NativePerformanceChecks.CaptureFixture(actual).Sha256 != actualEvidence.Sha256);
        first.Text = "Perf object 00 000"; first.Links[0] = "GA-PERF-1";
        Check("actual link edit changes fixture proof", NativePerformanceChecks.CaptureFixture(actual).Sha256 != actualEvidence.Sha256);
        first.Links[0] = "GA-PERF-0"; actual.Workbench.Objects[new Knx.Workbench.ObjectKey(firstDevice.Id, first.Id).ToString()].Flags = "CR";
        Check("actual metadata edit changes fixture proof", NativePerformanceChecks.CaptureFixture(actual).Sha256 != actualEvidence.Sha256);
    }

    private static NativePerformanceBudgetSnapshot ValidSnapshot()
    {
        var hash = new string('a', 64);
        var evidence = new NativePerformanceEvidence(new(".NET 10.0.14", 10, "Windows reference", true, "X64", "X64", 8, false,
            "12.1.4.0", 1, 1440, 900, hash, hash, hash, "en", "Light"), new(NativePerformanceBudgetPolicy.FixtureProfile,
            NativePerformanceBudgetPolicy.FixtureSha256, 48, 250, 12000, 8, 3, 24, 50, true, true));
        int[] logical = [2, 12048, 48, 12048, 251, 12048], objects = [1, 12000, 0, 12000, 250, 12000];
        var samples = Enumerable.Range(0, 144).Select(index => new NativePerformanceRawSample(
            NativePerformanceBudgetPolicy.Operations[index % 6], index / 6, 0, 0, 0, 100, logical[index % 6], objects[index % 6], 1,
            index, index, 0, 1L << 20, 128L << 20));
        return new("passed", null, "tab_to_project:23:render", hash, hash, evidence, evidence, samples, new(144, 144, 10, 10, 5, true), new(128L << 20, 256L << 20));
    }
    private static NativePerformanceBudgetSnapshot Change(NativePerformanceBudgetSnapshot snapshot, int index, Func<NativePerformanceRawSample, NativePerformanceRawSample> change)
    {
        var samples = snapshot.Samples.ToArray(); samples[index] = change(samples[index]); return Copy(snapshot, samples: samples);
    }
    private static NativePerformanceBudgetSnapshot WithOperationTimes(NativePerformanceBudgetSnapshot snapshot, double[] values) =>
        Copy(snapshot, samples: snapshot.Samples.Select(s => s.Operation == "filter_exact" ? s with { RenderedAndDispatchedMilliseconds = values[s.Index] } : s));
    private static NativePerformanceBudgetSnapshot Both(NativePerformanceBudgetSnapshot snapshot, NativePerformanceEvidence evidence) => Copy(snapshot, before: evidence, after: evidence);
    private static NativePerformanceBudgetSnapshot Copy(NativePerformanceBudgetSnapshot s, IEnumerable<NativePerformanceRawSample>? samples = null,
        string? status = null, string? error = null, string? stage = null, NativePerformanceEvidence? before = null, NativePerformanceEvidence? after = null,
        NativePerformanceMonitorEvidence? monitor = null, NativePerformanceMemoryEvidence? memory = null, string? sha = null, string? appSha = null,
        bool noBefore = false, bool noAfter = false, bool noMonitor = false, bool noMemory = false) => new(status ?? s.MeasurementStatus, error ?? s.Error,
            stage ?? s.Stage, sha ?? s.MeasurementsSha256, appSha ?? s.MeasurementAppAssemblySha256, noBefore ? null : before ?? s.Before,
            noAfter ? null : after ?? s.After, samples ?? s.Samples, noMonitor ? null : monitor ?? s.Monitor, noMemory ? null : memory ?? s.Memory);
}
