using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class DesiredImageChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static XElement Absolute(string id, uint size, byte[]? data = null, byte[]? mask = null, uint offset = 100)
        => new(Ns + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Size", size), new XAttribute("Address", offset),
            data is null ? null : new XElement(Ns + "Data", Convert.ToBase64String(data)), mask is null ? null : new XElement(Ns + "Mask", Convert.ToBase64String(mask)));
    private static XElement Relative(string id, uint size, byte[]? data = null, byte[]? mask = null, uint offset = 0, byte lsm = 4)
    {
        var result = Absolute(id, size, data, mask, offset); result.Name = Ns + "RelativeSegment";
        result.Attribute("Address")!.Remove(); result.Add(new XAttribute("Offset", offset), new XAttribute("LoadStateMachine", lsm)); return result;
    }
    private static XDocument App(params XElement[] segments)
    {
        var doc = ArchivedImageSourceChecks.App(); doc.Descendants(Ns + "Static").Single().AddFirst(new XElement(Ns + "Code", segments)); return doc;
    }
    private static XDocument Hardware() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs>
        <Hardware2Program Id="M-0001_H-image_A-0100-10-0000"><ApplicationProgramRef RefId="M-0001_A-0100-10-0000"/></Hardware2Program>
        </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null)
        => ArchivedImageSourceChecks.Load(directory, Hardware(), app ?? App(Absolute("S", 4, [0xA5, 0xC3])));
    private static ImageCompositionPlan Preview(ArchivedImageSourceChecks.Fixture fixture, StaticCodeMaskPolicy policy = StaticCodeMaskPolicy.SourceDefinedBits)
        => OfflineImageComposition.Preview(fixture.Project, fixture.Device, new(policy));
    private static bool Empty(ImageCompositionPlan plan, ImageCompositionIssueCode code) => !plan.IsAvailable && plan.Binding is null
        && plan.Issues.Count == 1 && plan.Issues[0].Code == code && plan.SourceSegments.Count == 0 && plan.Chunks.Count == 0
        && plan.UnspecifiedSpans.Count == 0 && plan.DesiredBitCount == 0 && plan.UnspecifiedBitCount == 0;
    private static object Inputs(ArchivedImageSourceChecks.Fixture f) => typeof(ArchivedImageSources)
        .GetMethod("ReadInputs", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [f.Project, f.Device])!;
    private static ImageCompositionPlan Supplied(ArchivedImageSourceChecks.Fixture f, object inputs) => (ImageCompositionPlan)typeof(OfflineImageComposition)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "Preview" && m.GetParameters().Length == 4)
        .Invoke(null, [f.Project, f.Device, inputs, new ImageCompositionRequest(StaticCodeMaskPolicy.SourceDefinedBits)])!;

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool valid) { checks++; Console.WriteLine((valid ? "PASS " : "FAIL ") + "DesiredImage " + name); if (!valid) failures++; }
        void Refused(string name, XDocument app, ImageCompositionIssueCode code) => Check(name, Empty(Preview(Load(directory, app)), code));
        var f = Load(directory); var xml = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
        var fileHash = SHA256.HashData(File.ReadAllBytes(f.Path)); var revision = f.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(f.Project); var plan = Preview(f); var chunk = plan.Chunks.Single(); var source = plan.SourceSegments.Single();
        Check("absent factory still permits exact root code composition", plan.IsAvailable && plan.Sources.Hardware is { LoadedImageDeclared: false }
            && plan.Sources.Hardware.FactoryImage.Status == ArchivedFactoryImageStatus.Missing && source.RawData?.ToHexString() == "A5C3" && source.RawMask is null);
        Check("declared tail is allocated only as unspecified working bits", source.DeclaredSize == 4 && source.AllocationLength == 4
            && chunk.DesiredData.ToHexString() == "A5C30000" && chunk.DesiredMask.ToHexString() == "FFFF0000" && chunk.ByteOffsetInArena == 100);
        Check("unknown span reports exact arena offset length and bit count", plan.UnspecifiedSpans.Single() is { ByteOffsetInArena: 102, Length: 2, UnspecifiedBitCount: 16 }
            && plan.UnspecifiedSpans[0].ChunkKey == chunk.Key && plan.DesiredBitCount == 16 && plan.UnspecifiedBitCount == 16);
        Check("source and chunk identities stay linked", chunk.SourceIds.SequenceEqual(new[] { "S" }) && chunk.Key.Length == 64
            && chunk.Arena == source.Arena && plan.Binding is { ProfileVersion: 1, MaskPolicy: StaticCodeMaskPolicy.SourceDefinedBits, Fingerprint.Length: 64 });
        Check("all authority flags remain false with explicit unresolved stages", !plan.CompleteApplicationImage && !plan.DownloadEligible && !plan.ExecutionEligible
            && plan.UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.MaskMemorySourcesNotComposed)
            && plan.UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.ParameterWritersNotApplied)
            && plan.UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.DeviceStateNotVerified));
        var excluded = Preview(f, StaticCodeMaskPolicy.NoStaticSourceBits);
        Check("excluding static contributions is an explicit different policy", excluded.IsAvailable && excluded.Chunks.Single().DesiredData.ToHexString() == "00000000"
            && excluded.Chunks.Single().DesiredMask.ToHexString() == "00000000" && excluded.UnspecifiedBitCount == 32 && excluded.DesiredBitCount == 0
            && excluded.SourceSegments.Single().RawData?.ToHexString() == "A5C3" && excluded.Binding!.Fingerprint != plan.Binding!.Fingerprint);
        Check("source composition has no project archive or undo mutation", xml == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting)
            && fileHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Path))) && revision == f.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        var dataCopy = chunk.DesiredData.ToArray(); dataCopy[0] = 0; var rawCopy = source.RawData!.ToArray(); rawCopy[0] = 0;
        var maskCopy = chunk.DesiredMask.ToArray(); maskCopy[0] = 0;
        Check("copy-out arrays cannot modify source or desired buffers", chunk.DesiredData.ToHexString() == "A5C30000" && source.RawData.ToHexString() == "A5C3"
            && chunk.DesiredMask.ToHexString() == "FFFF0000" && plan.Binding!.IsCurrent(f.Project, f.Device));
        f.Device.ParameterValues["unrelated"] = new string('x', 16 * 1024 * 1024 + 1);
        f.Device.ComObjectRefs.Add(new() { Id = "unused", ComObjectId = "unused", Number = 12 });
        Check("saved values and KO-only edits are not source inputs", plan.Binding!.IsCurrent(f.Project, f.Device) && Preview(f).Binding!.Fingerprint == plan.Binding.Fingerprint);
        f.Device.ParameterValues.Clear(); f.Device.ComObjectRefs.Clear();
        var empty = Preview(Load(directory, App(Absolute("Empty", 4, [])))); var missing = Preview(Load(directory, App(Absolute("Missing", 4))));
        Check("explicit empty data remains distinct from absent data", empty.SourceSegments.Single().RawData is { Length: 0 }
            && missing.SourceSegments.Single().RawData is null && empty.Chunks.Single().DesiredMask.ToHexString() == "00000000"
            && missing.Chunks.Single().DesiredMask.ToHexString() == "00000000");
        var zero = Preview(Load(directory, App(Absolute("Zero", 0))));
        Check("declared zero creates no invented bytes or spans", zero.IsAvailable && zero.SourceSegments.Single().AllocationLength == 0
            && zero.Chunks.Count == 0 && zero.UnspecifiedSpans.Count == 0);
        var longer = Preview(Load(directory, App(Absolute("Longer", 2, [0x12, 0x34, 0x56]))));
        Check("source bytes longer than declaration are never truncated", longer.SourceSegments.Single().DeclaredSize == 2
            && longer.SourceSegments.Single().AllocationLength == 3 && longer.Chunks.Single().DesiredData.ToHexString() == "123456");
        var partial = Preview(Load(directory, App(Absolute("Partial", 1, [0xA5], [0xF0]))));
        Check("partial mask canonicalizes only contribution bits", partial.SourceSegments.Single().RawData!.ToHexString() == "A5"
            && partial.SourceSegments.Single().RawMask!.ToHexString() == "F0" && partial.Chunks.Single().DesiredData.ToHexString() == "A0"
            && partial.Chunks.Single().DesiredMask.ToHexString() == "F0" && partial.UnspecifiedBitCount == 4);
        var none = Preview(Load(directory, App(Absolute("None", 1, [255], [0]))));
        Check("zero mask does not convert source FF into known desired bits", none.Chunks.Single().DesiredData.ToHexString() == "00"
            && none.Chunks.Single().DesiredMask.ToHexString() == "00" && none.UnspecifiedBitCount == 8);
        var left = Absolute("Left", 1, [0xA5], [0xF0]); var right = Absolute("Right", 1, [0x3C], [0x0F]);
        var merged = Preview(Load(directory, App(left, right))); var reversed = Preview(Load(directory, App(new XElement(right), new XElement(left))));
        Check("compatible source merge has the exact AC FF literal", merged.IsAvailable && merged.Chunks.Single().DesiredData.ToHexString() == "AC"
            && merged.Chunks.Single().DesiredMask.ToHexString() == "FF" && merged.UnspecifiedSpans.Count == 0);
        Check("source order does not choose a byte winner", reversed.IsAvailable && reversed.Binding!.ChunksHash == merged.Binding!.ChunksHash
            && reversed.DesiredBitCount == merged.DesiredBitCount && reversed.Chunks.Single().SourceIds.SequenceEqual(new[] { "Left", "Right" }));
        Check("identical known overlapping bits are compatible", Preview(Load(directory, App(Absolute("A", 1, [128], [128]), Absolute("B", 1, [128], [128])))).IsAvailable);
        Refused("conflicting known bits do not become native OR or last-writer authority", App(Absolute("A", 1, [128], [128]), Absolute("B", 1, [0], [128])), ImageCompositionIssueCode.ConflictingSourceBits);
        Refused("duplicate IDs refuse even compatible disjoint bytes", App(Absolute("A", 1, [1]), Absolute("A", 1, [1], offset: 105)), ImageCompositionIssueCode.InvalidDeclaration);
        var gap = Preview(Load(directory, App(Absolute("A", 1, [1]), Absolute("B", 1, [2], offset: 103))));
        Check("disjoint intervals do not fill an unrepresented gap", gap.Chunks.Count == 2 && gap.Chunks.Sum(c => c.Length) == 2 && gap.UnspecifiedBitCount == 0);
        var adjacent = Preview(Load(directory, App(Absolute("A", 1, [1]), Absolute("B", 1, [2], offset: 101))));
        Check("adjacent declared intervals coalesce deterministically", adjacent.Chunks.Single().DesiredData.ToHexString() == "0102");
        var arenas = Preview(Load(directory, App(Relative("R0", 1, [1], lsm: 0), Relative("R1", 1, [2], lsm: 1), Absolute("A", 1, [3], offset: 0))));
        Check("mixed arenas retain logical identities without physical claims", arenas.IsAvailable && arenas.Chunks.Count == 3
            && arenas.Chunks.Count(c => c.Arena.Kind == ResourcePatchArenaKind.RelativeMemory) == 2
            && arenas.Chunks.Any(c => c.Arena.LoadStateMachine == 0)
            && arenas.UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.PhysicalMappingUnresolved));
        Refused("same-LSM conflicting contributions refuse atomically", App(Relative("R0", 1, [1]), Relative("R1", 1, [2])), ImageCompositionIssueCode.ConflictingSourceBits);
        var maxima = Preview(Load(directory, App(Absolute("MaxA", 2, [1, 2], offset: 1048575), Relative("MaxR", 1, [3], offset: 1048575, lsm: 255))));
        Check("native maximum starts and LSM preserve their exact values", maxima.IsAvailable && maxima.SourceSegments.All(s => s.ByteOffsetInArena == 1048575)
            && maxima.SourceSegments.Single(s => s.SourceId == "MaxR").Arena.LoadStateMachine == 255);
        foreach (var (kind, attribute) in new[] { ("absolute", "Size"), ("absolute", "Address"), ("relative", "Offset") })
            foreach (var value in new[] { "1048576", "4294967295", "-1" })
            {
                var segment = kind == "absolute" ? Absolute("Bad", 1, [1]) : Relative("Bad", 1, [1]); segment.SetAttributeValue(attribute, value);
                Refused("native literal boundary " + attribute + "=" + value, App(segment), ImageCompositionIssueCode.InvalidDeclaration);
            }
        var badLsm = Relative("Bad", 1, [1]); badLsm.SetAttributeValue("LoadStateMachine", "256");
        Refused("LSM never wraps to a byte", App(badLsm), ImageCompositionIssueCode.InvalidDeclaration);
        var user = Absolute("User", 1, [1]); user.SetAttributeValue("UserMemory", "true");
        Refused("user memory is a separate unsupported arena", App(user), ImageCompositionIssueCode.UnsupportedArena);
        foreach (var mode in new[] { "missing-size", "missing-offset", "foreign-payload", "payload-wrapper", "bad-base64", "mask-length", "mask-only", "extra-child", "unknown-attribute", "long-id" })
        {
            var segment = Absolute("Bad", 2, [1, 2]);
            switch (mode)
            {
                case "missing-size": segment.Attribute("Size")!.Remove(); break;
                case "missing-offset": segment.Attribute("Address")!.Remove(); break;
                case "foreign-payload": segment.Element(Ns + "Data")!.Name = XNamespace.Get("urn:other") + "Data"; break;
                case "payload-wrapper": segment.Element(Ns + "Data")!.Add(new XElement(Ns + "Wrapper", "AQ==")); break;
                case "bad-base64": segment.Element(Ns + "Data")!.Value = "%notbase64"; break;
                case "mask-length": segment.Add(new XElement(Ns + "Mask", "AA==")); break;
                case "mask-only": segment.Element(Ns + "Data")!.Remove(); segment.Add(new XElement(Ns + "Mask", "AAA=")); break;
                case "extra-child": segment.Add(new XElement(Ns + "Other")); break;
                case "unknown-attribute": segment.SetAttributeValue("BaseValue", "A1"); break;
                case "long-id": segment.SetAttributeValue("Id", new string('x', 257)); break;
            }
            Refused("consumed native source shape " + mode, App(segment), ImageCompositionIssueCode.InvalidDeclaration);
        }
        var hidden = App(Absolute("A", 1, [1])); hidden.Descendants(Ns + "Dynamic").Single().Add(Absolute("Hidden", 1, [2]));
        Refused("misplaced consumed segment cannot disappear", hidden, ImageCompositionIssueCode.InvalidDeclaration);
        foreach (var foreignNamespace in new[] { false, true })
        {
            var fakeTemplate = App(Absolute("A", 1, [1]));
            var definition = new XElement((foreignNamespace ? XNamespace.Get("urn:other") : Ns) + "ModuleDef", new XAttribute("Id", "MD"),
                new XElement(Ns + "Static", new XElement(Ns + "Code", Absolute("Hidden", 1, [2]))));
            if (foreignNamespace) fakeTemplate.Descendants(Ns + "ApplicationProgram").Single().Add(new XElement(Ns + "ModuleDefs", definition));
            else fakeTemplate.Descendants(Ns + "Dynamic").Single().Add(definition);
            Refused("fake module wrapper cannot hide a misplaced source " + foreignNamespace, fakeTemplate, ImageCompositionIssueCode.InvalidDeclaration);
        }
        var duplicateCode = App(Absolute("A", 1, [1])); duplicateCode.Descendants(Ns + "Static").Single().Add(new XElement(Ns + "Code", Absolute("B", 1, [2])));
        Refused("duplicate root Code is ambiguous", duplicateCode, ImageCompositionIssueCode.InvalidDeclaration);
        Refused("no root code is not an empty complete image", ArchivedImageSourceChecks.App(), ImageCompositionIssueCode.NoCodeSources);
        foreach (var version in new[] { 21, 22, 23 })
        {
            var app = App(Absolute("S", 1, [0x35])); app.Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
            foreach (var element in app.Descendants()) element.Name = XNamespace.Get("http://knx.org/xml/project/" + version) + element.Name.LocalName;
            var result = Preview(Load(directory, app));
            Check("explicit application namespace " + version, version == 22 ? Empty(result, ImageCompositionIssueCode.UnsupportedApplication)
                : result.IsAvailable && result.Chunks.Single().DesiredData.ToHexString() == "35");
        }
        var withoutMaster = ArchivedImageSourceChecks.Load(directory, Hardware(), App(Relative("R", 2, [1, 2])), omitMaster: true);
        Check("literal LSM source does not invent a missing mask mapping", Preview(withoutMaster).IsAvailable
            && Preview(withoutMaster).UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.MaskMemorySourcesNotComposed));
        var withFactory = ArchivedImageSourceChecks.Load(directory, app: App(Absolute("S", 2, [0xA5, 0xC3])));
        var factoryIgnored = Preview(withFactory);
        Check("factory buffers remain separate unapplied source facts", factoryIgnored.IsAvailable && factoryIgnored.Sources.Hardware!.LoadedImageDeclared
            && factoryIgnored.Chunks.Single().DesiredData.ToHexString() == "A5C3"
            && factoryIgnored.UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.FactoryOptimizationNotApplied));
        var sourceOnly = App(Absolute("S", 1, [0x35])); sourceOnly.Descendants(Ns + "Static").Single().Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation")));
        sourceOnly.Descendants(Ns + "ApplicationProgram").Single().Add(new XElement(Ns + "ModuleDefs", new XElement(Ns + "ModuleDef", new XAttribute("Id", "MD"),
            new XElement(Ns + "Static", new XElement(Ns + "Code", Relative("NotRoot", 1, [255]))))));
        var unapplied = Preview(Load(directory, sourceOnly));
        Check("unapplied module and parameter writers are never adopted", unapplied.IsAvailable && unapplied.SourceSegments.Count == 1
            && unapplied.Chunks.Single().DesiredData.ToHexString() == "35" && unapplied.UnresolvedRequirements.Any(r => r.Code == ImageCompositionRequirementCode.ParameterWritersNotApplied));
        foreach (var policy in new[] { StaticCodeMaskPolicy.Unspecified, (StaticCodeMaskPolicy)999 })
            Check("missing or unknown policy refuses before source read", Empty(Preview(f, policy), ImageCompositionIssueCode.InvalidRequest));
        foreach (var guid in new[] { new string('x', 257), "bad\nidentity", "   " })
        {
            var badIdentity = Load(directory); badIdentity.Project.Guid = guid;
            Check("metadata is bounded before source hashing", Empty(Preview(badIdentity), ImageCompositionIssueCode.InvalidRequest));
        }
        var foreign = Load(directory);
        Check("foreign live identity cannot adopt a source capsule", Empty(Supplied(f, Inputs(foreign)), ImageCompositionIssueCode.InputsChanged));
        var supplied = Inputs(f); var application = (XDocument)supplied.GetType().GetProperty("Application")!.GetValue(supplied)!;
        application.Descendants(Ns + "Data").Single().Value = "AQ==";
        Check("changed owned XML does not match the source manifest", Empty(Supplied(f, supplied), ImageCompositionIssueCode.InputsChanged));
        f.Device.Hardware2ProgramRefId = null;
        Check("changed selection invalidates the old source binding", !plan.Binding!.IsCurrent(f.Project, f.Device));
        f.Device.Hardware2ProgramRefId = "M-0001_H-image_A-0100-10-0000";
        var loaded = Knxproj.Load(f.Path);
        Check("reloaded project cannot reuse private live binding owners", !plan.Binding!.IsCurrent(loaded, ProjectQuery.DeviceList(loaded).Single()));
        f.Project.Name = "Changed"; undo.Push(f.Project); var restored = undo.Undo()!;
        Check("undo restores sources but not a previous binding identity", !plan.Binding!.IsCurrent(restored, ProjectQuery.DeviceList(restored).Single())
            && OfflineImageComposition.Preview(restored, ProjectQuery.DeviceList(restored).Single(), new(StaticCodeMaskPolicy.SourceDefinedBits)).IsAvailable);
        Refused("segment count budget is atomic", App(Enumerable.Range(0, 1025).Select(i => Absolute("S" + i, 0)).ToArray()), ImageCompositionIssueCode.LimitExceeded);
        var maximumSize = Preview(Load(directory, App(Absolute("Max", 1048575))));
        Check("maximum native Size is a zero-mask working allocation", maximumSize.IsAvailable && maximumSize.Chunks.Single().Length == 1048575
            && maximumSize.DesiredBitCount == 0 && maximumSize.UnspecifiedBitCount == 1048575UL * 8);
        Refused("single decoded payload cannot exceed its independent byte budget", App(Absolute("Large", 1, new byte[1048577])), ImageCompositionIssueCode.LimitExceeded);
        Refused("sum of requested working lengths is checked before allocation", App(Enumerable.Range(0, 5).Select(i => Absolute("S" + i, 1048575)).ToArray()), ImageCompositionIssueCode.LimitExceeded);
        var largeData = new byte[1048576]; var largeMask = Enumerable.Repeat((byte)255, largeData.Length).ToArray();
        Refused("raw Data plus Mask aggregate budget is independent", App(Enumerable.Range(0, 3).Select(i => Absolute("S" + i, 1048575, largeData, largeMask)).ToArray()), ImageCompositionIssueCode.LimitExceeded);
        var full = Preview(Load(directory, App(Enumerable.Range(0, 4).Select(i => Absolute("S" + i, 1048575, largeData)).ToArray())));
        Check("exact four-MiB source and working sums remain admitted", full.IsAvailable && full.SourceSegments.Sum(s => s.RawData!.Length) == 4194304
            && full.Chunks.Single().Length == 1048576 && full.UnspecifiedBitCount == 0);
        var alternating = Enumerable.Range(0, 8194).Select(i => (byte)(i % 2 == 0 ? 0 : 255)).ToArray();
        Refused("unspecified-span limit never publishes a truncated plan", App(Absolute("Spans", 8194, new byte[8194], alternating)), ImageCompositionIssueCode.LimitExceeded);
        Console.WriteLine($"DesiredImage: {checks} checks, {failures} failures"); return failures;
    }
}
