using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class NativeImageFoldChecks
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static Type Core(string name) => typeof(Project).Assembly.GetType("Knx." + name, true)!;
    private static object New(string name, params object?[] args) => Activator.CreateInstance(Core(name), Any, null, args, null)!;
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static object[] Items(object value, string name) => Get<IEnumerable>(value, name).Cast<object>().ToArray();
    private static object Typed(string name, object[] values)
    { var result = Array.CreateInstance(Core(name), values.Length); for (var i = 0; i < values.Length; i++) result.SetValue(values[i], i); return result; }
    private static ImmutablePatchBytes Immutable(byte[] bytes)
        => Get<ImmutablePatchBytes>(New("InitialMemoryResource", "fixture", 0u, (uint?)bytes.Length, bytes, null), "Data");
    private static object Target(string space = "StandardMemory", byte? lsm = null)
        => New("NativeImageTarget", Enum.Parse(Core("NativeImageSpace"), space), lsm,
            space is "PropertyData" or "PropertyCount" ? new PropertySourceSelector(PropertySourceKind.SystemProperty, 10, NativeObjectIndex: 3) : null);
    private static object Source(int order, uint offset, int length, string kind = "ZeroRegion", object? target = null,
        byte[]? data = null, byte[]? mask = null)
        => New("NativeImageSourceContribution", Enum.Parse(Core("NativeImageContributionKind"), kind), order, "source-" + order, "declaration",
            New("NativeImageRange", target ?? Target(), offset, length), null,
            data is null ? null : Immutable(data), mask is null ? null : Immutable(mask),
            (uint?)length, length, "mapping", null, null);
    private static object Schedule(object[] sources, string[]? transitions = null)
        => Core("NativeImageFoldSchedule").GetMethod("Create", Static)!.Invoke(null,
            [Typed("NativeImageSourceContribution", sources), Typed("NativeImagePostTransition", (transitions ?? [])
                .Select(t => New("NativeImagePostTransition", Enum.Parse(Core("NativeImagePostTransitionKind"), t), null)).ToArray())])!;
    private static string? Rejected(Action action)
    {
        try { action(); return null; }
        catch (TargetInvocationException failure)
        {
            var error = failure.InnerException!;
            var issue = error.GetType().GetProperty("Issue", Any)?.GetValue(error);
            return issue is null ? error.GetType().Name : Get<object>(issue, "Code").ToString();
        }
    }
    private static bool Bytes(ImmutablePatchBytes? value, byte[] expected) => value is not null && value.ToArray().SequenceEqual(expected);
    private static NativeImageBuildChunk Memory(NativeImageBuildPlan plan)
        => plan.Variants.Single().Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
    private static NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture fixture, params uint[] options)
        => OfflineNativeImageBuild.Preview(fixture.Project, fixture.Device, new(options.Length == 0 ? [129u] : options));
    private static string Explain(NativeImageBuildPlan plan) => string.Join(",", plan.Issues.Select(i => i.Code))
        + string.Join(";", plan.Variants.Select(v => string.Join(",", v.Issues.Select(i => i.Code))));
    private static ArchivedImageSourceChecks.Fixture Regions(string directory, (uint Offset, int Length)[] ranges,
        byte[]? data = null, byte[]? mask = null)
    {
        var app = NativeGeneralImageBuildChecks.MixedApplication();
        var segment = NativeGeneralPopulationChecks.Stat(app).Element(Ns + "Code")!.Elements().Single();
        segment.RemoveNodes();
        if (data is not null) segment.Add(new XElement(Ns + "Data", Convert.ToBase64String(data)));
        if (mask is not null) segment.Add(new XElement(Ns + "Mask", Convert.ToBase64String(mask)));
        var master = NativeGeneralImageBuildChecks.MixedMaster(); var container = master.Descendants(Ns + "MemorySegments").Single();
        var template = new XElement(container.Elements().Single()); container.RemoveNodes();
        foreach (var range in ranges)
        {
            var region = new XElement(template); region.SetAttributeValue("Length", range.Length);
            region.Element(Ns + "Location")!.SetAttributeValue("StartAddress", range.Offset); container.Add(region);
        }
        return NativeGeneralImageBuildChecks.Mixed(directory, app, master);
    }
    private static byte[] Formatted(byte[] initial)
    {
        var result = (byte[])initial.Clone(); result[0] = (byte)((result[0] & 0xFC) | 2);
        for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4, 4), (uint)i + 1);
        return result;
    }
    private static byte[] WrittenMask(byte[] initial)
    { var result = (byte[])initial.Clone(); result[0] = 255; Array.Fill(result, (byte)255, 4, 508); return result; }
    private static ArchivedImageSourceChecks.Fixture WideChain(string directory)
    {
        const int delta = 5200;
        var app = NativeGeneralImageBuildChecks.MixedApplication(); var code = NativeGeneralPopulationChecks.Stat(app).Element(Ns + "Code")!;
        code.RemoveNodes();
        for (var i = 0; i < 31; i++)
            code.Add(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", i == 0 ? "S" : "Bridge" + i),
                new XAttribute("Address", 101 + i * delta), new XAttribute("Size", delta),
                new XElement(Ns + "Data", "pQ=="), new XElement(Ns + "Mask", "8A==")));
        var master = NativeGeneralImageBuildChecks.MixedMaster(); var container = master.Descendants(Ns + "MemorySegments").Single();
        var template = new XElement(container.Elements().Single()); container.RemoveNodes();
        for (var i = 0; i < 32; i++)
        {
            var region = new XElement(template); region.SetAttributeValue("Length", 2);
            region.Element(Ns + "Location")!.SetAttributeValue("StartAddress", 100 + i * delta); container.Add(region);
        }
        return NativeGeneralImageBuildChecks.Mixed(directory, app, master);
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeImageFold " + name); if (!ok) failures++; }
        var bridge = Schedule([Source(0, 100, 4), Source(1, 108, 4), Source(2, 116, 4), Source(3, 102, 16, "NativeCode", data: new byte[16])]);
        var component = Items(bridge, "Components").Single(); var additions = Items(bridge, "Additions");
        Check("one real contribution folds all three prior neighbours", Get<bool>(bridge, "UsesExtendedFolds")
            && Get<uint>(component, "ByteOffset") == 100 && Get<int>(component, "Length") == 20 && Items(additions[3], "Folds").Length == 3);
        Check("three-neighbour growth charges both actual union allocations", Get<long>(bridge, "GrowthPayloadBytes") == 76
            && Get<long>(bridge, "SourcePayloadBytes") == 40 && Get<long>(bridge, "ReservedMaterializationPayloadBytes") == 336);
        var tie = Schedule([Source(0, 100, 8), Source(1, 100, 16, "NativeCode", data: new byte[16])], ["MaterializeNullMasks"]);
        var tiedFold = Items(Items(tie, "Additions")[1], "Folds").Single();
        Check("equal start inserts the new complete maskless source on the left", Get<int>(Items(tie, "Additions")[1], "InsertIndex") == 0
            && Get<int>(Get<object>(tiedFold, "LeftRange"), "Length") == 16 && !Get<bool>(tiedFold, "LeftHasMask")
            && !Get<bool>(tiedFold, "ResultHasMask") && Get<long>(tie, "TransitionPayloadBytes") == 16);
        var adjacent = Schedule([Source(0, 0, 4), Source(1, 4, 4)]);
        Check("adjacent allocations remain two components without early concatenation", Items(adjacent, "Components").Length == 2
            && Items(adjacent, "Additions").All(a => Items(a, "Folds").Length == 0));
        var gap = Schedule([Source(0, 0, 4), Source(1, 5, 4)]);
        Check("a real gap is never included in any final allocation", Items(gap, "Components").Length == 2
            && Items(gap, "Components").Sum(c => Get<int>(c, "Length")) == 8);
        var targets = Schedule([Source(0, 0, 8), Source(1, 0, 8, target: Target("RelativeMemory", 1))]);
        Check("equal offsets in distinct logical targets never fold", Items(targets, "Components").Length == 2);
        Check("overlapping original code owners remain refused", Rejected(() => Schedule([Source(0, 0, 8, "NativeCode"), Source(1, 7, 4, "NativeCode")])) == "AmbiguousOwnership");
        Check("touching code owners remain separate rather than colliding", Items(Schedule([Source(0, 0, 8, "NativeCode"), Source(1, 8, 4, "NativeCode")]), "Components").Length == 2);
        Check("Count components are not PropertyData initialization targets", Rejected(() => Schedule([Source(0, 0, 2, "PointerZero", Target("PropertyCount"))])) == "UnsupportedMapping");
        Check("repeated factory owners do not become an arbitrary OR policy", Rejected(() => Schedule([Source(0, 0, 8, "FactoryProperty", Target("PropertyData")), Source(1, 4, 8, "FactoryProperty", Target("PropertyData"))])) == "AmbiguousOwnership");
        Check("duplicate source order is rejected", Rejected(() => Schedule([Source(0, 0, 4), Source(0, 0, 8)])) == "InvalidDeclaration");
        Check("unsigned endpoint overflow is rejected before materialization", Rejected(() => Schedule([Source(0, uint.MaxValue, 2)])) == "InvalidDeclaration");
        Check("each union remains bounded to1MiB", Rejected(() => Schedule([Source(0, 0, 1024 * 1024), Source(1, 1, 1024 * 1024)])) == "LimitExceeded");
        Check("raw operands cannot exceed their original interval", Rejected(() => Schedule([Source(0, 0, 1, "NativeCode", data: [1, 2])])) == "InvalidDeclaration");
        Check("a raw mask cannot appear without its raw data", Rejected(() => Schedule([Source(0, 0, 1, "NativeCode", mask: [255])])) == "InvalidDeclaration");
        var repeated = Schedule(Enumerable.Range(0, 1000).Select(i => Source(i, (uint)i, 16)).ToArray());
        Check("growth reserve counts discarded quadratic copies rather than only the final union", Get<long>(repeated, "GrowthPayloadBytes") == 1030968
            && Get<long>(repeated, "FinalComponentBytes") == 1015 && Get<long>(repeated, "ReservedMaterializationPayloadBytes") == 1071088);
        Check("quadratic copies can exceed the limit despite a tiny final component", Rejected(() => Schedule(Enumerable.Range(0, 4096)
            .Select(i => Source(i, (uint)i, 16)).ToArray())) == "LimitExceeded");
        var exact = Schedule([Source(0, 0, 1048576), Source(1, 524288, 349524), Source(2, 0, 1)]);
        Check("legacy12S remains the conservative minimum", Get<long>(exact, "ReservedMaterializationPayloadBytes") == 16777212);
        Check("one additional source byte exceeds the conservative envelope before payloads", Rejected(() => Schedule(
            [Source(0, 0, 1048576), Source(1, 524288, 349525), Source(2, 0, 1)])) == "LimitExceeded");
        var same = Source(0, 0, 4); var paired = Schedule([same]);
        var matches = paired.GetType().GetMethod("Matches", Any)!;
        Check("schedule pairing uses the exact original source objects", (bool)matches.Invoke(paired,
            [Typed("NativeImageSourceContribution", [same]), Typed("NativeImagePostTransition", [])])!
            && !(bool)matches.Invoke(paired, [Typed("NativeImageSourceContribution", [Source(0, 0, 4)]), Typed("NativeImagePostTransition", [])])!);
        var immutable = false;
        try { ((IList)Get<object>(paired, "Additions")).Clear(); } catch (NotSupportedException) { immutable = true; }
        Check("published schedule metadata cannot be rewritten through list casts", immutable);

        foreach (var row in new[]
        {
            (Name: "three-neighbour real code bridge", Ranges: new[] { (100u, 4), (108u, 4), (116u, 4) }, Data: new byte[] { 0xA5 }, Mask: new byte[] { 0xF0 }, Initial: (byte)0xA0, Valid: (byte)0xF0, Full: false),
            (Name: "complete maskless bridge", Ranges: new[] { (100u, 4), (108u, 4), (116u, 4) }, Data: Enumerable.Repeat((byte)0xA5, 512).ToArray(), Mask: (byte[]?)null, Initial: (byte)0xA5, Valid: (byte)255, Full: true),
            (Name: "maskless supplied prefix and unknown padding", Ranges: new[] { (100u, 4), (108u, 4), (116u, 4) }, Data: new byte[] { 0xA5 }, Mask: (byte[]?)null, Initial: (byte)0xA5, Valid: (byte)255, Full: false),
        })
        {
            var fixture = Regions(directory, row.Ranges, row.Data, row.Mask);
            var before = Knxproj.ToXml(fixture.Project).ToString(); var archive = SHA256.HashData(File.ReadAllBytes(fixture.Path));
            var undo = new UndoStack(); undo.Reset(fixture.Project);
            var revision = fixture.Project.ApplicationDataRevision; var plan = Preview(fixture);
            Check(row.Name + " full public pipeline: " + Explain(plan), plan.IsAvailable);
            if (!plan.IsAvailable) continue;
            var initial = new byte[512]; var validity = new byte[512]; initial[0] = row.Initial; validity[0] = row.Valid;
            if (row.Full) { Array.Fill(initial, row.Initial); Array.Fill(validity, row.Valid); }
            var working = Formatted(initial); var desiredMask = WrittenMask(validity); var chunk = Memory(plan);
            Check(row.Name + " full independent initial data and validity", Bytes(chunk.InitialData, initial) && Bytes(chunk.InitialValidity, validity));
            Check(row.Name + " full independent working validity canonical and claim arrays", Bytes(chunk.WorkingData, working)
                && Bytes(chunk.DesiredValidity, desiredMask) && Bytes(chunk.CanonicalDesired, working.Select((b, i) => (byte)(b & desiredMask[i])).ToArray())
                && Bytes(chunk.ParameterWriteBits, WrittenMask(new byte[512]).Select((b, i) => i == 0 ? (byte)3 : b).ToArray()));
            Check(row.Name + " original source intervals and source order survive", plan.Variants[0].Sources.Where(s => s.Kind == NativeImageBuildSourceKind.ZeroRegion)
                .Select(s => (s.ByteOffset, s.Length)).SequenceEqual(row.Ranges) && chunk.SourceIds.Count == row.Ranges.Length + 1);
            Check(row.Name + " all admitted references and bounded cumulative payloads remain complete", plan.Variants[0].Population?.ReferenceCount == 131
                && plan.AllocatedImagePayloadBytes <= plan.ReservedWorstCaseImagePayloadBytes && plan.ReservedWorstCaseImagePayloadBytes <= 16L * 1024 * 1024);
            Check(row.Name + " fresh replay and unchanged model archive revision", plan.Binding!.IsCurrent(fixture.Project, fixture.Device)
                && Knxproj.ToXml(fixture.Project).ToString() == before && fixture.Project.ApplicationDataRevision == revision
                && archive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Path))) && !undo.CanUndo && !undo.IsDirty);
            fixture.Device.ParameterValues["R1"] = "99";
            Check(row.Name + " changed captured writer values stale the binding", !plan.Binding.IsCurrent(fixture.Project, fixture.Device));
        }

        var overlapping = Regions(directory, [(100u, 4), (103u, 5), (107u, 5), (111u, 501)], [0xA5], [0xF0]);
        var overlappingBefore = Knxproj.ToXml(overlapping.Project).ToString(); var overlappingResult = Preview(overlapping);
        Check("overlapping master regions remain an atomic source refusal", !overlappingResult.IsAvailable && overlappingResult.Binding is null
            && overlappingResult.Issues.Concat(overlappingResult.Variants.SelectMany(v => v.Issues)).Any(i => i.Code == NativeImageBuildIssueCode.UnsupportedSource)
            && overlappingResult.Variants.All(v => !v.IsAvailable && v.Chunks.Count == 0 && v.Sources.Count == 0)
            && Knxproj.ToXml(overlapping.Project).ToString() == overlappingBefore);

        var pointerMaster = NativeGeneralImageBuildChecks.MixedMaster();
        var pointers = pointerMaster.Descendants(Ns + "Resource").ToArray();
        for (var i = 0; i < pointers.Length; i++)
        {
            var location = pointers[i].Element(Ns + "Location")!; location.SetAttributeValue("InterfaceObjectRef", 3);
            location.SetAttributeValue("PropertyID", 10); location.SetAttributeValue("StartAddress", i * 2);
        }
        var pointerFixture = NativeGeneralImageBuildChecks.Mixed(directory, master: pointerMaster); var pointerPlan = Preview(pointerFixture, 1);
        Check("five overlapping pointer sources and the one paired factory allocation fold together: " + Explain(pointerPlan), pointerPlan.IsAvailable);
        if (pointerPlan.IsAvailable)
        {
            var chunk = pointerPlan.Variants[0].Chunks.Single(c => c.Target.Property?.PropertyId == 10);
            Check("factory raw data and untouched pointer tail remain initially unknown", chunk.Length == 12
                && Bytes(chunk.InitialData, Convert.FromHexString("DEADBEEF1020304000000000")) && Bytes(chunk.InitialValidity, new byte[12]));
            Check("property writer modifies only the original two elements", Bytes(chunk.WorkingData, Convert.FromHexString("000000810000008200000000"))
                && Bytes(chunk.DesiredValidity, Convert.FromHexString("FFFFFFFFFFFFFFFF00000000")) && chunk.SourceIds.Count == 6);
            Check("observed count is unchanged and not adopted as a source mask", pointerPlan.Variants[0].ObservedCounts.Single().EncodedCount == 0x1234
                && pointerPlan.Variants[0].ObservedCounts.Single().NotAdopted && pointerPlan.Binding!.IsCurrent(pointerFixture.Project, pointerFixture.Device));
        }

        // Declared parameter geometry cannot replace a missing source Code allocation.
        var noCodeApp = NativeGeneralImageBuildChecks.MixedApplication(); NativeGeneralPopulationChecks.Stat(noCodeApp).Element(Ns + "Code")!.Remove();
        var noCode = NativeGeneralImageBuildChecks.Mixed(directory, noCodeApp);
        var noCodeBefore = Knxproj.ToXml(noCode.Project).ToString(); var refused = Preview(noCode);
        Check("missing source allocation is atomic and cannot borrow a nominal owner span", !refused.IsAvailable && refused.Binding is null
            && refused.Variants.All(v => !v.IsAvailable && v.Chunks.Count == 0 && v.Sources.Count == 0)
            && Knxproj.ToXml(noCode.Project).ToString() == noCodeBefore);
        var large = WideChain(directory);
        var largeBefore = Knxproj.ToXml(large.Project).ToString(); var largeArchive = SHA256.HashData(File.ReadAllBytes(large.Path));
        var oneLarge = Preview(large, 129);
        Check("one disjoint wide bridge chain fits its complete growth reserve: " + Explain(oneLarge), oneLarge.IsAvailable);
        if (oneLarge.IsAvailable)
        {
            const int length = 161202;
            var initial = new byte[length]; var initialMask = new byte[length];
            for (var i = 0; i < 31; i++) { initial[1 + i * 5200] = 0xA0; initialMask[1 + i * 5200] = 0xF0; }
            var working = (byte[])initial.Clone(); working[1] = 0xA2;
            for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(working.AsSpan(1 + i * 4, 4), (uint)i + 1);
            var validity = (byte[])initialMask.Clone(); validity[1] = 255; Array.Fill(validity, (byte)255, 5, 508);
            var claims = new byte[length]; claims[1] = 3; Array.Fill(claims, (byte)255, 5, 508);
            var chunk = Memory(oneLarge);
            Check("wide chain complete independent arrays preserve real gaps covered by bridge code", chunk.ByteOffset == 100 && chunk.Length == length
                && Bytes(chunk.InitialData, initial) && Bytes(chunk.InitialValidity, initialMask) && Bytes(chunk.WorkingData, working)
                && Bytes(chunk.DesiredValidity, validity) && Bytes(chunk.ParameterWriteBits, claims)
                && Bytes(chunk.CanonicalDesired, working.Select((b, i) => (byte)(b & validity[i])).ToArray()));
            Check("wide chain accounts for both growths of every bridge before final payload copies", oneLarge.AllocatedImagePayloadBytes <= oneLarge.ReservedWorstCaseImagePayloadBytes
                && oneLarge.ReservedWorstCaseImagePayloadBytes > 10316986 && oneLarge.ReservedWorstCaseImagePayloadBytes <= 16L * 1024 * 1024
                && oneLarge.Variants[0].Sources.Count(s => s.Kind == NativeImageBuildSourceKind.ZeroRegion) == 32
                && oneLarge.Variants[0].Sources.Count(s => s.Kind == NativeImageBuildSourceKind.NativeCode) == 31);
        }
        var cumulative = Preview(large, 1, 129);
        Check("all alternatives share one cumulative reserve rather than a limit per variant", cumulative.HasGlobalFailure
            && cumulative.Issues.Any(i => i.Code == NativeImageBuildIssueCode.LimitExceeded) && cumulative.Binding is null && cumulative.Variants.Count == 0);
        Check("cumulative growth refusal leaves model and original archive unchanged", Knxproj.ToXml(large.Project).ToString() == largeBefore
            && largeArchive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(large.Path))));
        Console.WriteLine($"NativeImageFold: {checks} checks, {failures} failures");
        return failures;
    }
}
