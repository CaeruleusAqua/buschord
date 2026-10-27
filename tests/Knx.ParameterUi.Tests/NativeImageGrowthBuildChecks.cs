using System.Buffers.Binary;
using System.Xml.Linq;
using Knx;

internal static class NativeImageGrowthBuildChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static NativeImageBuildChunk Memory(NativeImageBuildPlan plan) => plan.Variants.Single().Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
    private static bool Bytes(ImmutablePatchBytes? actual, byte[] expected) => actual is not null && actual.ToArray().SequenceEqual(expected);
    private static string Explain(NativeImageBuildPlan plan) => string.Join(",", plan.Issues.Select(i => i.Code))
        + string.Join(";", plan.Variants.Select(v => string.Join(",", v.Issues.Select(i => i.Code))));
    private static bool LocalEmpty(NativeImageBuildPlan plan, NativeImageBuildIssueCode code) => !plan.IsAvailable && !plan.HasGlobalFailure
        && plan.Binding is null && plan.Variants.Count == 1 && plan.Variants[0] is { IsAvailable: false, Population: null, Procedure: null, Tables: null }
        && plan.Variants[0].Chunks.Count == 0 && plan.Variants[0].Sources.Count == 0 && plan.Variants[0].Issues.Any(i => i.Code == code);
    private static ArchivedImageSourceChecks.Fixture CodeFixture(string directory, uint regionStart, int regionLength,
        int codeLength, int supplied, byte? explicitMask = 0xF0, bool secondGrowth = false)
    {
        var app = NativeGeneralImageBuildChecks.MixedApplication(); var stat = NativeGeneralPopulationChecks.Stat(app);
        var code = stat.Element(Ns + "Code")!; var segment = code.Elements().Single(); segment.SetAttributeValue("Size", codeLength);
        segment.RemoveNodes();
        if (supplied != 0) segment.Add(new XElement(Ns + "Data", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, supplied).ToArray())));
        if (explicitMask is { } mask) segment.Add(new XElement(Ns + "Mask", Convert.ToBase64String(Enumerable.Repeat(mask, supplied).ToArray())));
        if (secondGrowth) code.Add(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "Second"), new XAttribute("Address", 614), new XAttribute("Size", 8),
            new XElement(Ns + "Data", "zA=="), new XElement(Ns + "Mask", "8A==")));
        var master = NativeGeneralImageBuildChecks.MixedMaster(); var region = master.Descendants(Ns + "MemorySegment").Single();
        region.SetAttributeValue("Length", regionLength); region.Element(Ns + "Location")!.SetAttributeValue("StartAddress", regionStart);
        return NativeGeneralImageBuildChecks.Mixed(directory, app, master);
    }
    private static NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture fixture, uint options = 129)
        => OfflineNativeImageBuild.Preview(fixture.Project, fixture.Device, new([options]));

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeImageGrowthBuild " + name); if (!ok) failures++; }
        foreach (var row in new[]
        {
            (Name: "right", Start: 100u, Region: 512, Code: 520, Union: 520),
            (Name: "left", Start: 104u, Region: 512, Code: 512, Union: 516),
            (Name: "both", Start: 104u, Region: 504, Code: 512, Union: 512),
        })
        {
            var fixture = CodeFixture(directory, row.Start, row.Region, row.Code, row.Code); var model = Knxproj.ToXml(fixture.Project).ToString();
            var plan = Preview(fixture);
            Check(row.Name + " code grows one initialized region: " + Explain(plan), plan.IsAvailable);
            if (!plan.IsAvailable) continue;
            var chunk = Memory(plan); var initial = new byte[row.Union]; var mask = new byte[row.Union];
            for (var i = 0; i < row.Code; i++)
            {
                var address = 100u + (uint)i; var overlap = address >= row.Start && address < row.Start + row.Region;
                initial[i] = overlap ? (byte)0xA0 : (byte)0xA5; mask[i] = 0xF0;
            }
            var working = (byte[])initial.Clone(); working[0] = (byte)((working[0] & 0xFC) | 2);
            for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(working.AsSpan(i * 4, 4), (uint)i + 1);
            var validity = (byte[])mask.Clone(); validity[0] = 255; Array.Fill(validity, (byte)255, 4, 508);
            var canonical = working.Select((b, i) => (byte)(b & validity[i])).ToArray();
            Check(row.Name + " union geometry is checked and source ranges remain original", chunk.ByteOffset == 100 && chunk.Length == row.Union
                && plan.Variants[0].Sources.Single(s => s.Kind == NativeImageBuildSourceKind.ZeroRegion).ByteOffset == row.Start
                && plan.Variants[0].Sources.Single(s => s.Kind == NativeImageBuildSourceKind.NativeCode).Length == row.Code);
            Check(row.Name + " complete initial data distinguishes masked overlap from raw exclusive code", Bytes(chunk.InitialData, initial) && Bytes(chunk.InitialValidity, mask));
            Check(row.Name + " complete formatted data validity and canonical literals", Bytes(chunk.WorkingData, working)
                && Bytes(chunk.DesiredValidity, validity) && Bytes(chunk.CanonicalDesired, canonical));
            Check(row.Name + " all131 references and their own covering geometry survive growth", plan.Variants[0].Population?.ReferenceCount == 131
                && plan.Variants[0].Population?.PhysicalRecordCount == 130 && plan.Variants[0].CoverageHash is { Length: 64 });
            Check(row.Name + " growing copies fit the existing conservative reservation", plan.AllocatedImagePayloadBytes <= plan.ReservedWorstCaseImagePayloadBytes
                && plan.ReservedWorstCaseImagePayloadBytes <= 16L * 1024 * 1024);
            Check(row.Name + " binding replays growth without source mutation", plan.Binding!.IsCurrent(fixture.Project, fixture.Device)
                && Knxproj.ToXml(fixture.Project).ToString() == model);
        }
        foreach (var supplied in new[] { 520, 512, 0 })
        {
            var plan = Preview(CodeFixture(directory, 100, 512, 520, supplied, explicitMask: null));
            Check("maskless supplied" + supplied + " grows with native mask preparation: " + Explain(plan), plan.IsAvailable);
            if (!plan.IsAvailable) continue;
            var expected = new byte[520]; Array.Fill(expected, (byte)0xA5, 0, supplied);
            var mask = new byte[520]; Array.Fill(mask, (byte)255, 0, supplied);
            Check("maskless supplied" + supplied + " complete raw data and prefix validity", Bytes(Memory(plan).InitialData, expected)
                && Bytes(Memory(plan).InitialValidity, mask));
        }
        foreach (var factoryLength in new[] { 8, 12 })
        {
            var master = NativeGeneralImageBuildChecks.MixedMaster();
            var pointer = master.Descendants(Ns + "Resource").First();
            pointer.Element(Ns + "Location")!.SetAttributeValue("InterfaceObjectRef", 3);
            pointer.Element(Ns + "Location")!.SetAttributeValue("PropertyID", 10);
            var raw = factoryLength == 8 ? Convert.FromHexString("DEADBEEF10203040") : Convert.FromHexString("DEADBEEF1020304050607080");
            var factory = PropertySourceChecks.Frame(data: raw, dataMask: Enumerable.Repeat((byte)255, factoryLength).ToArray(), count: [0x12, 0x34]);
            var fixture = NativeGeneralImageBuildChecks.Mixed(directory, master: master, raw: factory); var plan = Preview(fixture, 1);
            Check("pointer4 to factory" + factoryLength + " is admitted by source-owned property allocation: " + Explain(plan), plan.IsAvailable);
            if (!plan.IsAvailable) continue;
            var variant = plan.Variants.Single(); var chunk = variant.Chunks.Single(c => c.Target.Property?.PropertyId == 10);
            var working = (byte[])raw.Clone(); Convert.FromHexString("0000008100000082").CopyTo(working, 0);
            var mask = new byte[factoryLength]; Array.Fill(mask, (byte)255, 0, 8);
            Check("factory" + factoryLength + " complete raw bytes survive zero-mask union and reset", chunk.Length == factoryLength
                && Bytes(chunk.InitialData, raw) && Bytes(chunk.InitialValidity, new byte[factoryLength]));
            Check("factory" + factoryLength + " complete desired data and validity keep untouched tail unknown", Bytes(chunk.WorkingData, working)
                && Bytes(chunk.DesiredValidity, mask));
            Check("factory" + factoryLength + " original pointer and property provenance stay separate", variant.Sources.Any(s => s.Kind == NativeImageBuildSourceKind.PointerZero
                    && s.Target.Property?.PropertyId == 10 && s.Length == 4)
                && variant.Sources.Any(s => s.Kind == NativeImageBuildSourceKind.FactoryProperty && s.Target.Property?.PropertyId == 10 && s.Length == factoryLength)
                && variant.ObservedCounts.Single().EncodedCount == 0x1234 && variant.ObservedCounts.Single().NotAdopted);
            Check("factory" + factoryLength + " allocation accounting and source binding stay complete", plan.AllocatedImagePayloadBytes <= plan.ReservedWorstCaseImagePayloadBytes
                && plan.Binding!.IsCurrent(fixture.Project, fixture.Device));
        }
        var repeated = Preview(CodeFixture(directory, 104, 512, 512, 512, secondGrowth: true));
        var repeatedInitial = new byte[522]; Array.Fill(repeatedInitial, (byte)0xA0, 0, 512); Array.Fill(repeatedInitial, (byte)0xA5, 0, 4);
        repeatedInitial[514] = 0xC0;
        var repeatedMask = new byte[522]; Array.Fill(repeatedMask, (byte)0xF0, 0, 512); repeatedMask[514] = 0xF0;
        var repeatedWorking = (byte[])repeatedInitial.Clone(); repeatedWorking[0] = 0xA6;
        for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(repeatedWorking.AsSpan(i * 4, 4), (uint)i + 1);
        var repeatedValidity = (byte[])repeatedMask.Clone(); repeatedValidity[0] = 255; Array.Fill(repeatedValidity, (byte)255, 4, 508);
        Check("second disjoint code fold grows the same component with complete independent bytes: " + Explain(repeated), repeated.IsAvailable
            && Memory(repeated).Length == 522 && Bytes(Memory(repeated).InitialData, repeatedInitial) && Bytes(Memory(repeated).InitialValidity, repeatedMask)
            && Bytes(Memory(repeated).WorkingData, repeatedWorking) && Bytes(Memory(repeated).DesiredValidity, repeatedValidity)
            && Bytes(Memory(repeated).CanonicalDesired, repeatedWorking.Select((b, i) => (byte)(b & repeatedValidity[i])).ToArray()));
        var bridgedMaster = NativeGeneralImageBuildChecks.MixedMaster(); var regions = bridgedMaster.Descendants(Ns + "MemorySegments").Single();
        var first = regions.Element(Ns + "MemorySegment")!; first.SetAttributeValue("Length", 4);
        var second = new XElement(first); second.Element(Ns + "Location")!.SetAttributeValue("StartAddress", 108); regions.Add(second);
        var bridged = Preview(NativeGeneralImageBuildChecks.Mixed(directory, master: bridgedMaster));
        var bridgeInitial = new byte[512]; bridgeInitial[0] = 0xA0; var bridgeMask = new byte[512]; bridgeMask[0] = 0xF0;
        var bridgeWorking = new byte[512]; bridgeWorking[0] = 0xA2;
        for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(bridgeWorking.AsSpan(i * 4, 4), (uint)i + 1);
        var bridgeValidity = new byte[512]; bridgeValidity[0] = 255; Array.Fill(bridgeValidity, (byte)255, 4, 508);
        Check("real code bridges two zero regions with complete independent bytes: " + Explain(bridged), bridged.IsAvailable
            && Memory(bridged).Length == 512 && Bytes(Memory(bridged).InitialData, bridgeInitial) && Bytes(Memory(bridged).InitialValidity, bridgeMask)
            && Bytes(Memory(bridged).WorkingData, bridgeWorking) && Bytes(Memory(bridged).DesiredValidity, bridgeValidity)
            && Bytes(Memory(bridged).CanonicalDesired, bridgeWorking));
        Console.WriteLine($"NativeImageGrowthBuild: {checks} checks, {failures} failures");
        return failures;
    }
}
