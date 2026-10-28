using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class RootImageBuildChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private sealed record Fixture(Project Project, DeviceInstance Device, string Path);
    private static XDocument Original(Project project) => (XDocument)typeof(Project).GetProperty("OriginalXml", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(project)!;
    private static XElement Program(XDocument document) => document.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram");
    private static XElement Static(XDocument document) => Program(document).Element(Ns + "Static")!;
    private static XElement Parameter(XDocument document, int index = 0) => Static(document).Element(Ns + "Parameters")!.Elements().ElementAt(index);
    private static XElement Reference(XDocument document, int index = 0) => Static(document).Element(Ns + "ParameterRefs")!.Elements().ElementAt(index);
    private static XElement Segment(XDocument document) => Static(document).Element(Ns + "Code")!.Elements().First();
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static RootImageBuildRequest Request(StaticCodeMaskPolicy policy = StaticCodeMaskPolicy.SourceDefinedBits, GeneratedTableFormat? tables = null)
        => new(policy, Full, tables is { } format ? new(format) : null);
    private static RootImageBuildPlan Preview(Fixture fixture, RootImageBuildRequest? request = null)
        => OfflineRootImageBuild.Preview(fixture.Project, fixture.Device, request ?? Request());
    private static RootImageBuildIssue? PairSourcesWithLaterCapture(Fixture fixture, Action mutate)
    {
        var assembly = typeof(Project).Assembly; const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        var inputs = typeof(ArchivedImageSources).GetMethod("ReadInputs", hidden)!.Invoke(null, [fixture.Project, fixture.Device])!;
        var sources = assembly.GetType("Knx.RootParameterWriterSources", true)!.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([fixture.Project, fixture.Device, inputs]);
        mutate();
        var context = assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!.GetMethod("Create", hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, null, null])!;
        try
        {
            _ = assembly.GetType("Knx.RootParameterWriterSet", true)!.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([inputs, sources, context]);
            return null;
        }
        catch (TargetInvocationException failure) when (failure.InnerException?.GetType().FullName == "Knx.RootBuildRejected")
        { return (RootImageBuildIssue)failure.InnerException.GetType().GetProperty("Issue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failure.InnerException)!; }
    }
    private static bool Empty(RootImageBuildPlan plan, RootImageBuildIssueCode? code = null)
        => !plan.IsAvailable && plan.Binding is null && plan.Policy is null && plan.Population is null && plan.Procedure is null && plan.Tables is null
            && !plan.CompleteFixedRootPopulation && !plan.CompleteApplicationImage && !plan.ExecutionEligible && !plan.DownloadEligible
            && plan.Claims.Count == 0 && plan.Chunks.Count == 0 && plan.SourceSegments.Count == 0 && plan.UnspecifiedSpans.Count == 0
            && plan.DesiredBitCount == 0 && plan.UnspecifiedBitCount == 0 && plan.Issues.Count == 1 && (code is null || plan.Issues[0].Code == code);
    private static XDocument App(int count = 1, int bits = 8, byte[]? raw = null, byte[]? mask = null)
    {
        var document = ArchivedImageSourceChecks.App(); var app = Program(document); var stat = Static(document);
        app.SetAttributeValue("MinEtsVersion", "6.0"); app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        var width = (bits + 7) / 8;
        stat.AddFirst(new XElement(Ns + "Code", new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "S"), new XAttribute("Address", 100),
            new XAttribute("Size", count * width), raw is null ? null : new XElement(Ns + "Data", Convert.ToBase64String(raw)),
            mask is null ? null : new XElement(Ns + "Mask", Convert.ToBase64String(mask)))));
        stat.Element(Ns + "ParameterTypes")!.ReplaceNodes(new XElement(Ns + "ParameterType", new XAttribute("Id", "T"),
            new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", bits),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", bits == 32 ? uint.MaxValue : (1u << bits) - 1))));
        stat.Element(Ns + "Parameters")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "Parameter", new XAttribute("Id", "P" + i),
            new XAttribute("ParameterType", "T"), new XAttribute("Value", bits >= 8 ? i + 1 : 1), new XElement(Ns + "Memory",
                new XAttribute("CodeSegment", "S"), new XAttribute("Offset", i * width), new XAttribute("BitOffset", bits < 8 ? 8 - bits : 0)))));
        stat.Element(Ns + "ParameterRefs")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "ParameterRef",
            new XAttribute("Id", "R" + i), new XAttribute("RefId", "P" + i))));
        app.Element(Ns + "Dynamic")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R" + i))));
        stat.AddFirst(new XElement(Ns + "Options"));
        stat.Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"), new XElement(Ns + "LdCtrlDisconnect"))));
        return document;
    }
    private static Fixture Load(string directory, XDocument? application = null, XDocument? master = null)
    {
        application ??= App(); var appId = (string)Program(application).Attribute("Id")!; var manufacturer = appId[..6];
        var hardwareId = manufacturer + "_H-root_I-1_A-root";
        var project = new Project { Id = "P-root-working", Name = "Synthetic root working set", Guid = "00000001-0000-0000-0000-000000000001" };
        var installation = new Installation { Id = "I-root" }; var area = new Area { Id = "A-root", Address = 1 }; var line = new Line { Id = "L-root", Address = 1 };
        var device = new DeviceInstance { Id = "D-root", ProductRefId = appId, Hardware2ProgramRefId = hardwareId, Address = new(1, 1, 1) };
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        var hardware = XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="{{manufacturer}}"><Hardware><Hardware Id="{{manufacturer}}_H-root"><Hardware2Programs>
            <Hardware2Program Id="{{hardwareId}}"><ApplicationProgramRef RefId="{{appId}}"/></Hardware2Program>
            </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """);
        var path = Path.Combine(directory, "root-working-" + Guid.NewGuid().ToString("N") + ".knxproj");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, document) in new[] { ("0.xml", Knxproj.ToXml(project)), (manufacturer + "/" + appId + ".xml", application),
                (manufacturer + "/Hardware.xml", hardware), ("knx_master.xml", master ?? GeneratedTableSerializerChecks.Master(GeneratedTableFormat.M112)) })
            { using var stream = zip.CreateEntry(name).Open(); stream.Write(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))); }
        var loaded = Knxproj.Load(path); return new(loaded, ProjectQuery.DeviceList(loaded).Single(), path);
    }
    private static void Special(XDocument app)
    {
        Program(app).SetAttributeValue("Id", "M-009E_A-1000-01-own"); app.Descendants(Ns + "Manufacturer").Single().SetAttributeValue("RefId", "M-009E");
    }
    private static XDocument TwoBits(string? behavior = null, bool active = true, bool special = false, byte raw = 0xA5, byte mask = 0xF0)
    {
        var app = App(1, 2, [raw], [mask]); Parameter(app).SetAttributeValue("Value", 2);
        if (behavior is not null) Static(app).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", behavior);
        if (!active) Program(app).Element(Ns + "Dynamic")!.RemoveNodes();
        if (special) Special(app); return app;
    }
    private static Fixture TableFixture(string directory, GeneratedTableFormat format, bool compatibleSeed = true, bool parameterInTable = false, bool occupiedFresh = false)
    {
        var app = GeneratedTableSerializerChecks.Application(format);
        Program(app).SetAttributeValue("MinEtsVersion", "6.0"); Static(app).AddFirst(new XElement(Ns + "Options"));
        if (compatibleSeed) Static(app).Descendants(Ns + "Mask").Remove();
        {
            var extra = App(); Static(app).Add(extra.Descendants(Ns + "ParameterTypes").Single(), extra.Descendants(Ns + "Parameters").Single(), extra.Descendants(Ns + "ParameterRefs").Single());
            var segment = new XElement(Segment(extra)); segment.SetAttributeValue("Id", "Parameters");
            if (format == GeneratedTableFormat.M112) segment.SetAttributeValue("Address", 0x1000);
            else { segment.Name = Ns + "RelativeSegment"; segment.Attribute("Address")!.Remove(); segment.Add(new XAttribute("Offset", 4), new XAttribute("LoadStateMachine", 4)); }
            if (!parameterInTable) Static(app).Element(Ns + "Code")!.Add(segment);
            Parameter(app).Element(Ns + "Memory")!.SetAttributeValue("CodeSegment", parameterInTable ? "GA" : "Parameters");
            Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R0")));
        }
        if (occupiedFresh) Static(app).Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "Occupied"),
            new XAttribute("Offset", 0), new XAttribute("Size", 1), new XAttribute("LoadStateMachine", 1), new XElement(Ns + "Data", "AQ==")));
        var fixture = Load(directory, app, GeneratedTableSerializerChecks.Master(format));
        var range = new GroupRange { Id = "GR-root", RangeStart = 0, RangeEnd = 65535 }; fixture.Project.Installations[0].MainRanges.Add(range);
        foreach (var (id, value) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        { range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)value) }); fixture.Project.Workbench.Groups[id] = new() { Secure = false }; }
        var numbers = format == GeneratedTableFormat.M112 ? new[] { 0, 1, 3 } : new[] { 1, 2, 4 };
        for (var i = 0; i < numbers.Length; i++)
        {
            var obj = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            obj.Links.AddRange(i == 0 ? ["GA1"] : i == 1 ? ["GA2", "GA1"] : ["GA3", "GA2"]); fixture.Device.ComObjectRefs.Add(obj);
            fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, obj.Id).ToString()] = new() { SendingGroupId = obj.Links[0] };
        }
        return fixture;
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok, RootImageBuildPlan? plan = null)
        { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "RootImageBuild " + name + (!ok && plan is not null ? " [" + string.Join(",", plan.Issues.Select(i => i.Code)) + "]" : "")); if (!ok) failures++; }
        void Refused(string name, Action<XDocument> edit, RootImageBuildIssueCode? code = null)
        { var app = App(); edit(app); var result = Preview(Load(directory, app)); Check(name, Empty(result, code), result); }
        var full = Load(directory, App(130, 32)); full.Device.ParameterValues["R0"] = "16909060";
        var xml = Knxproj.ToXml(full.Project).ToString(SaveOptions.DisableFormatting); var fileHash = Hash(File.ReadAllBytes(full.Path)); var revision = full.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(full.Project); var plan = Preview(full);
        Check("all original 130 references are admitted without a caller subset", plan.IsAvailable && plan.CompleteFixedRootPopulation && plan.Population is { ReferenceCount: 130, ActiveCount: 130, OwnerCount: 130, PhysicalRecordCount: 130 }, plan);
        var expected = new byte[520]; for (var i = 0; i < 130; i++) BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(i * 4, 4), i == 0 ? 0x01020304u : (uint)i + 1);
        Check("all 520 working bytes match an independent BCL serializer", plan.Chunks.Count == 1 && plan.Chunks[0].WorkingData.ToArray().SequenceEqual(expected)
            && plan.Chunks[0].CanonicalDesired.ToArray().SequenceEqual(expected) && plan.Chunks[0].DesiredValidity.ToArray().All(b => b == 255)
            && plan.DesiredBitCount == 4160 && plan.UnspecifiedBitCount == 0, plan);
        Check("source and current values are bound independently from output bytes", plan.Binding is { CarrierHash.Length: 64, PopulationHash.Length: 64, ValuesHash.Length: 64, GeometryHash.Length: 64, RequirementsHash.Length: 64 }
            && plan.Binding.IsCurrent(full.Project, full.Device) && plan.Claims.Count == 130, plan);
        Check("working stage never grants image or device authority", plan.Scope == "RootFixedMemoryWorkingSet" && plan.ProfileVersion == 1 && !plan.CompleteApplicationImage && !plan.ExecutionEligible && !plan.DownloadEligible
            && plan.UnresolvedRequirements.Any(r => r.Code == RootImageBuildRequirementCode.ProcedureFinalizationUnresolved)
            && plan.UnresolvedRequirements.Any(r => r.Code == RootImageBuildRequirementCode.ActualDeviceOptionsUnverified), plan);
        Check("preview preserves model archive revision and undo", xml == Knxproj.ToXml(full.Project).ToString(SaveOptions.DisableFormatting) && fileHash == Hash(File.ReadAllBytes(full.Path))
            && revision == full.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        if (plan.IsAvailable)
        {
            var copy = plan.Chunks[0].WorkingData.ToArray(); copy[0] = 255;
            Check("copy-out does not mutate retained bytes or binding", plan.Chunks[0].WorkingData.ToArray().SequenceEqual(expected) && plan.Binding!.IsCurrent(full.Project, full.Device));
            full.Device.ParameterValues["R0"] = "3";
            Check("saved changes stale the binding and change the result", !plan.Binding!.IsCurrent(full.Project, full.Device) && Preview(full).Binding?.Fingerprint != plan.Binding.Fingerprint);
            full.Device.ParameterValues["R0"] = "16909060";
            Check("exact restored current inputs make the binding current", plan.Binding.IsCurrent(full.Project, full.Device));
            var reloaded = Knxproj.Load(full.Path);
            Check("same project and device ids do not authorize replacement models", !plan.Binding.IsCurrent(reloaded, ProjectQuery.DeviceList(reloaded).Single()));
            typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(full.Project, revision + 1);
            Check("revision changes stale the binding", !plan.Binding.IsCurrent(full.Project, full.Device));
            typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(full.Project, revision);
            full.Device.ComObjectRefs.Add(new() { Id = "unresolved", ComObjectId = "unresolved", Number = 1 });
            Check("new unaccounted object metadata cannot retain an old binding", !plan.Binding.IsCurrent(full.Project, full.Device)); full.Device.ComObjectRefs.Clear();
            full.Project.Name = "Changed"; undo.Push(full.Project); var restored = undo.Undo()!;
            Check("undo model replacement needs its own fresh build", !plan.Binding.IsCurrent(restored, ProjectQuery.DeviceList(restored).Single()) && Preview(new(restored, ProjectQuery.DeviceList(restored).Single(), full.Path)).IsAvailable);
        }
        var littleApp = App(2, 32); Static(littleApp).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "LittleEndian");
        var little = Load(directory, littleApp); little.Device.ParameterValues["R0"] = "16909060"; var littlePlan = Preview(little);
        Check("source LittleEndian selects exact words without a caller option", littlePlan.IsAvailable && littlePlan.Policy?.ByteOrder == ParameterPatchByteOrder.LittleEndian
            && littlePlan.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == "0403020102000000", littlePlan);
        foreach (var (name, bits, shape, value, bytes) in new[]
        {
            ("Value enumeration", 8, new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", 8),
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E1"), new XAttribute("Value", 1)), new XElement(Ns + "Enumeration", new XAttribute("Id", "E3"), new XAttribute("Value", 3))), "3", "03"),
            ("IEEE Single", 32, new XElement(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 10)), "1.5", "3FC00000"),
            ("packed Time", 24, new XElement(Ns + "TypeTime", new XAttribute("SizeInBit", 24), new XAttribute("Unit", "PackedDaysHoursMinutesAndSeconds"),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 691199), new XAttribute("UIHint", "Time_hhmmss")), "90061", "010121"),
            ("dense Time", 16, new XElement(Ns + "TypeTime", new XAttribute("SizeInBit", 16), new XAttribute("Unit", "Seconds"),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 32767), new XAttribute("UIHint", "Duration_hhmmss")), "258", "0102"),
        })
        {
            var app = App(1, bits); Static(app).Descendants(Ns + "TypeNumber").Single().ReplaceWith(shape); Parameter(app).SetAttributeValue("Value", value);
            var result = Preview(Load(directory, app)); Check("admitted codec reaches the complete writer stage: " + name,
                result.IsAvailable && result.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == bytes && result.Chunks.SingleOrDefault()?.DesiredValidity.ToArray().All(b => b == 255) == true, result);
        }

        var populationApp = App(5);
        foreach (var p in Static(populationApp).Element(Ns + "Parameters")!.Elements()) { p.SetAttributeValue("Value", 1); p.SetAttributeValue("CustomerAdjustable", "true"); }
        foreach (var r in Static(populationApp).Element(Ns + "ParameterRefs")!.Elements()) r.SetAttributeValue("Value", 2);
        Reference(populationApp, 2).SetAttributeValue("CustomerAdjustable", "false"); Parameter(populationApp, 2).SetAttributeValue("LegacyPatchAlways", "true");
        Parameter(populationApp, 3).Element(Ns + "Memory")!.Remove(); Program(populationApp).Element(Ns + "Dynamic")!.ReplaceNodes(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R0")));
        var population = Load(directory, populationApp); for (var i = 0; i < 5; i++) population.Device.ParameterValues["R" + i] = "3";
        var populated = Preview(population);
        Check("active default always and no-storage records keep their distinct population", populated.IsAvailable && populated.Population is { ReferenceCount: 5, ActiveCount: 1, AlwaysCount: 1, NoStorageCount: 1, InactiveDefaultCount: 2, PhysicalRecordCount: 4 }, populated);
        Check("inactive defaults use Parameter1 despite Ref2 and Saved3", populated.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == "0301030001"
            && populated.Chunks.SingleOrDefault()?.DesiredValidity.ToHexString() == "FFFFFF00FF", populated);
        Check("current origin and inherited customer metadata remain explicit", populated.Population?.Records.All(r => r.CurrentValueOrigin == RootParameterValueOrigin.Saved) == true
            && populated.Population.Records[0].CustomerAdjustable && !populated.Population.Records[2].CustomerAdjustable
            && populated.Population.Records[1].CurrentValueHash != populated.Population.Records[1].RecordValueHash
            && populated.Population.Records[3] is { NativeRecordEmitted: false, Allocates: false, WritesData: false }, populated);
        var accessApp = App(); Parameter(accessApp).SetAttributeValue("Access", "None"); var access = Preview(Load(directory, accessApp));
        Check("Access None does not erase an otherwise active native writer", access.IsAvailable && access.Population?.ActiveCount == 1
            && access.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == "01", access);
        var duplicateActivityApp = App(); Program(duplicateActivityApp).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R0")));
        var duplicateActivity = Preview(Load(directory, duplicateActivityApp));
        Check("two normal reference owners count activity without duplicating the instance", duplicateActivity.IsAvailable && duplicateActivity.Population?.ReferenceCount == 1
            && duplicateActivity.Population.Records[0].NormalReferenceCount == 2 && duplicateActivity.Claims.Count == 1, duplicateActivity);

        foreach (var (name, app, working, validity, canonical) in new[]
        {
            ("partial seed and active whole-byte writer", TwoBits(), "A6", "FF", "A6"),
            ("explicit zero mask clears raw data before parameter writing", TwoBits(raw: 0xA5, mask: 0), "02", "FF", "02"),
            ("inactive None leaves working data and partial validity", TwoBits("None", false), "A5", "F0", "A0"),
            ("inactive Background marks its entire touched byte valid", TwoBits("Background", false), "A5", "FF", "A5"),
            ("inactive Default emits the underlying value", TwoBits("DefaultValue", false), "A6", "FF", "A6"),
            ("special active profile marks only its field bits", TwoBits(special: true), "A6", "F3", "A2"),
            ("special inactive Background still marks a whole byte", TwoBits("Background", false, true), "A5", "FF", "A5"),
        })
        {
            var result = Preview(Load(directory, app)); var chunk = result.Chunks.SingleOrDefault();
            Check(name, result.IsAvailable && chunk?.WorkingData.ToHexString() == working && chunk.DesiredValidity.ToHexString() == validity && chunk.CanonicalDesired.ToHexString() == canonical, result);
        }
        var resetFixture = Load(directory, TwoBits(special: true)); var included = Preview(resetFixture); var reset = Preview(resetFixture, Request(StaticCodeMaskPolicy.NoStaticSourceBits));
        Check("explicit static-validity reset does not erase retained working bits", reset.IsAvailable && reset.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == "A6"
            && reset.Chunks.SingleOrDefault()?.DesiredValidity.ToHexString() == "03" && reset.Chunks.SingleOrDefault()?.CanonicalDesired.ToHexString() == "02"
            && reset.Binding?.Fingerprint != included.Binding?.Fingerprint, reset);
        var disjoint = App(2, 2); Segment(disjoint).SetAttributeValue("Size", 1); Parameter(disjoint, 1).SetAttributeValue("Value", 2);
        Parameter(disjoint, 1).Element(Ns + "Memory")!.SetAttributeValue("Offset", 0); Parameter(disjoint, 1).Element(Ns + "Memory")!.SetAttributeValue("BitOffset", 4);
        var disjointPlan = Preview(Load(directory, disjoint));
        Check("disjoint physical fields may share one byte", disjointPlan.IsAvailable && disjointPlan.Claims.Count == 2 && disjointPlan.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == "09", disjointPlan);
        foreach (var (version, options, behavior) in new (string?, bool, RootParameterDownloadBehavior)[] { (null, true, RootParameterDownloadBehavior.Background),
            ("4.9.9", true, RootParameterDownloadBehavior.Background), ("6.0", false, RootParameterDownloadBehavior.Background), ("6.0", true, RootParameterDownloadBehavior.DefaultValue) })
        {
            var app = TwoBits(active: false); Program(app).SetAttributeValue("MinEtsVersion", version); if (!options) Static(app).Element(Ns + "Options")!.Remove();
            var result = Preview(Load(directory, app)); Check("native policy preserves version and Options presence " + version + "/" + options, result.IsAvailable && result.Policy?.DownloadBehavior == behavior, result);
        }
        foreach (var value in new[] { "", "6", "6.0suffix", "256.0", "6.256", "6.0.65536", "6.0.0.65536", " 6.0" })
            Refused("malformed or unbounded minimum version " + value, a => Program(a).SetAttributeValue("MinEtsVersion", value), RootImageBuildIssueCode.InvalidPolicy);
        foreach (var value in new[] { "", "bigendian", "unknown" })
            Refused("unknown native byte order " + value, a => Static(a).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", value), RootImageBuildIssueCode.InvalidPolicy);
        foreach (var value in new[] { "", "defaultvalue", "unknown" })
            Refused("unknown inactive policy " + value, a => Static(a).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", value), RootImageBuildIssueCode.InvalidPolicy);

        Refused("a later invalid ref cannot publish a partial population", a => Static(a).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "late"), new XAttribute("RefId", "missing"))));
        Refused("multiple physical aliases are not silently deduplicated", a => Static(a).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "alias"), new XAttribute("RefId", "P0"))), RootImageBuildIssueCode.AmbiguousOwnership);
        Refused("latent physical owners remain in the overlap closure", a => { var latent = new XElement(Parameter(a)); latent.SetAttributeValue("Id", "latent"); Static(a).Element(Ns + "Parameters")!.Add(latent); });
        Refused("null inactive records must still fit their allocation", a => { Program(a).Element(Ns + "Dynamic")!.RemoveNodes(); Parameter(a).Element(Ns + "Memory")!.SetAttributeValue("Offset", 1); }, RootImageBuildIssueCode.FieldOutOfRange);
        Refused("explicit physical underlying value is required", a => Parameter(a).Attribute("Value")!.Remove(), RootImageBuildIssueCode.InvalidValue);
        Refused("source reference override is bounded before evaluation", a => Reference(a).SetAttributeValue("Value", new string('1', 65)), RootImageBuildIssueCode.InvalidValue);
        Refused("property writers do not become NoStorage", a => Parameter(a).Element(Ns + "Memory")!.ReplaceWith(new XElement(Ns + "Property", new XAttribute("ObjectIndex", 1), new XAttribute("PropertyId", 2)))) ;
        Refused("an unrecognized physical type cannot disappear", a => Static(a).Descendants(Ns + "TypeNumber").Single().Name = Ns + "TypeText");
        Refused("raw NoStorage is outside the fixed-root profile", a => { Parameter(a).Element(Ns + "Memory")!.Remove(); var shape = Static(a).Descendants(Ns + "TypeNumber").Single(); shape.ReplaceWith(new XElement(Ns + "TypeRawData", new XAttribute("MaxSize", 8))); Parameter(a).SetAttributeValue("Value", "AA"); }, RootImageBuildIssueCode.UnsupportedType);
        foreach (var shape in new[] { "TypeUnknown", "TypeNone" })
            Refused("missing NoStorage values do not admit an unsupported shape " + shape, a => { Parameter(a).Element(Ns + "Memory")!.Remove(); Parameter(a).Attribute("Value")!.Remove();
                Static(a).Descendants(Ns + "TypeNumber").Single().ReplaceWith(new XElement(Ns + shape)); }, RootImageBuildIssueCode.UnsupportedType);
        Refused("unknown code memory kinds are not accepted", a => Segment(a).SetAttributeValue("MemoryType", "unknown"));
        Refused("unknown code attributes do not supply mapping", a => Segment(a).SetAttributeValue("BaseValue", "X"));
        Refused("overlapping code allocations refuse even equal bytes", a => { var second = new XElement(Segment(a)); second.SetAttributeValue("Id", "second"); Static(a).Element(Ns + "Code")!.Add(second); }, RootImageBuildIssueCode.SourceOverlap);
        Refused("global choice cap precedes typed options", a => Static(a).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "unused"),
            new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", 16), Enumerable.Range(0, 4097).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i)))))), RootImageBuildIssueCode.LimitExceeded);
        Refused("non-enum metadata cannot allocate hidden typed options", a => Static(a).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "unused"),
            new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 8), Enumerable.Range(0, 4097).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i)))))));
        Refused("NoStorage shapes cannot hide nested enum metadata", a => { Parameter(a).Element(Ns + "Memory")!.Remove(); Static(a).Descendants(Ns + "TypeNumber").Single().Add(new XElement(Ns + "Wrapper", new XElement(Ns + "Enumeration", new XAttribute("Id", "E"), new XAttribute("Value", 1)))); });
        Refused("source-segment count is bounded before allocation", a => Static(a).Element(Ns + "Code")!.Add(Enumerable.Range(0, 1024).Select(i => new XElement(Ns + "AbsoluteSegment",
            new XAttribute("Id", "extra" + i), new XAttribute("Address", 1000 + i), new XAttribute("Size", 0)))), RootImageBuildIssueCode.LimitExceeded);
        Refused("combined working allocation is bounded before buffers", a => { Segment(a).SetAttributeValue("Size", 1048575); Static(a).Element(Ns + "Code")!.Add(Enumerable.Range(0, 4).Select(i =>
            new XElement(Ns + "RelativeSegment", new XAttribute("Id", "extra" + i), new XAttribute("Offset", 0), new XAttribute("LoadStateMachine", i), new XAttribute("Size", 1048575)))); }, RootImageBuildIssueCode.LimitExceeded);
        Refused("a single decoded source has its own independent cap", a => Segment(a).Add(new XElement(Ns + "Data", Convert.ToBase64String(new byte[1048577]))), RootImageBuildIssueCode.LimitExceeded);
        Refused("processing-instruction text participates in the early application budget", a => a.AddFirst(new XProcessingInstruction("own", new string('x', 16 * 1024 * 1024))), RootImageBuildIssueCode.LimitExceeded);
        foreach (var name in new[] { "Module", "Repeat", "Union", "Allocator", "Assign", "ParameterCalculation" })
            Refused("unsupported derivation " + name, a => Program(a).Element(Ns + "Dynamic")!.Add(new XElement(Ns + name)));
        foreach (var policy in new[] { StaticCodeMaskPolicy.Unspecified, (StaticCodeMaskPolicy)999 })
            Check("invalid explicit mask policy " + policy, Empty(Preview(Load(directory), Request(policy)), RootImageBuildIssueCode.InvalidRequest));
        Check("groups-only selection cannot mint a parameter build", Empty(Preview(Load(directory), Request() with { Procedure = Full with { Parts = LoadParts.Groups } }), RootImageBuildIssueCode.InvalidRequest));

        var lexical = Load(directory); lexical.Device.ParameterValues["R0"] = "03";
        Check("equal encoded words cannot pair different captured raw strings", PairSourcesWithLaterCapture(lexical, () => lexical.Device.ParameterValues["R0"] = "3")?.Code == RootImageBuildIssueCode.InputsChanged);
        var presence = Load(directory);
        Check("equal numeric defaults cannot conceal a newly saved key", PairSourcesWithLaterCapture(presence, () => presence.Device.ParameterValues["R0"] = "1")?.Code == RootImageBuildIssueCode.InputsChanged);
        var identical = Load(directory); identical.Device.ParameterValues["R0"] = "03";
        Check("identical raw capture remains compatible without another evaluation", PairSourcesWithLaterCapture(identical, () => { }) is null);

        var carrier = Load(directory); var native = Original(carrier.Project).Descendants().Single(e => e.Name.LocalName == "DeviceInstance"); var nativeNs = native.Name.Namespace;
        native.Add(new XElement(nativeNs + "ParameterInstanceRefs", new XElement(nativeNs + "ParameterInstanceRef", new XAttribute("RefId", "R0"), new XAttribute("Value", 3))));
        carrier.Device.ParameterValues["R0"] = "3"; var saved = Preview(carrier); carrier.Device.ParameterValues.Remove("R0"); var removed = Preview(carrier);
        Check("deleting the current saved key never revives its stale OriginalXml value", saved.IsAvailable && removed.IsAvailable && saved.Binding?.IsCurrent(carrier.Project, carrier.Device) == false
            && removed.Chunks.SingleOrDefault()?.WorkingData.ToHexString() == "01" && removed.Population?.Records[0].CurrentValueOrigin == RootParameterValueOrigin.Parameter, removed);
        foreach (var name in new[] { "ParameterValues", "ParameterValue", "ParameterInstances", "ParameterInstance", "ModuleInstance", "BinaryData" })
        {
            var f = Load(directory); var node = Original(f.Project).Descendants().Single(e => e.Name.LocalName == "DeviceInstance"); node.Add(new XElement(node.Name.Namespace + name));
            var result = Preview(f); Check("retained native carrier is classified " + name, Empty(result, RootImageBuildIssueCode.UnsupportedNativeCarrier), result);
        }
        foreach (var kind in new[] { "foreign-namespace", "self-authorized-project", "wrapped-owner", "duplicate-device", "foreign-parameter-container" })
        {
            var f = Load(directory); var doc = Original(f.Project); var node = doc.Descendants().Single(e => e.Name.LocalName == "DeviceInstance");
            switch (kind)
            {
                case "foreign-namespace": node.Name = XNamespace.Get("urn:foreign") + "DeviceInstance"; break;
                case "self-authorized-project": foreach (var e in doc.Descendants()) e.Name = XNamespace.Get("urn:foreign") + e.Name.LocalName; break;
                case "wrapped-owner": var wrapper = new XElement(node.Name.Namespace + "Wrapper"); node.ReplaceWith(wrapper); wrapper.Add(node); break;
                case "duplicate-device": node.Parent!.Add(new XElement(node)); break;
                case "foreign-parameter-container": node.Add(new XElement(XNamespace.Get("urn:foreign") + "ParameterInstanceRefs")); break;
            }
            var result = Preview(f); Check("original carrier cannot authorize itself via " + kind, Empty(result, RootImageBuildIssueCode.UnsupportedNativeCarrier), result);
        }
        var oversized = Load(directory); oversized.Device.ParameterValues["R0"] = new string('1', 65);
        Check("current saved values are bounded before evaluation", Empty(Preview(oversized), RootImageBuildIssueCode.InvalidValue));
        var unknown = Load(directory); unknown.Device.ParameterValues["foreign"] = "1";
        Check("unrecognized saved keys cannot disappear", Empty(Preview(unknown), RootImageBuildIssueCode.UnsupportedNativeCarrier));
        var conflicting = Load(directory); conflicting.Device.ParameterValues["R0"] = "1"; conflicting.Device.ParameterValues["M-0001_A-0100-10-0000_R0"] = "2";
        Check("conflicting short and qualified saved keys refuse", Empty(Preview(conflicting), RootImageBuildIssueCode.InvalidValue));

        foreach (var format in new[] { GeneratedTableFormat.M112, GeneratedTableFormat.SystemB2ByteAssociations, GeneratedTableFormat.SystemB4ByteAssociations })
        {
            var f = TableFixture(directory, format); var request = Request(tables: format); var result = Preview(f, request);
            var standalone = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Full, new(format)).GeneratedTables;
            Check("compatible table contribution is compiled with the full root population " + format, result.IsAvailable && result.Population?.ReferenceCount == 1 && result.Tables is { Payloads.Count: 3 }
                && standalone?.IsAvailable == true && result.Tables.Binding.Fingerprint == standalone.Binding?.Fingerprint, result);
            if (result.IsAvailable && standalone?.IsAvailable == true)
            {
                foreach (var payload in standalone.Payloads)
                {
                    var chunk = result.Chunks.Single(c => c.Arena == payload.Arena && c.ByteOffsetInArena <= payload.ByteOffset && (ulong)c.ByteOffsetInArena + (uint)c.Length >= (ulong)payload.ByteOffset + (uint)payload.Length);
                    var offset = checked((int)(payload.ByteOffset - chunk.ByteOffsetInArena));
                    var contribution = result.Tables!.Payloads.Single(p => p.Role == payload.Role);
                    Check("full table bytes and separate compatibility witness " + format + "/" + payload.Role,
                        chunk.WorkingData.ToArray().AsSpan(offset, payload.Length).SequenceEqual(payload.Data.ToArray()) && contribution.DataHash == payload.Data.Sha256
                        && contribution.InputCompatibilityHash.Length == 64 && result.Tables.CompatibilityHash.Length == 64 && result.Binding!.TableCompatibilityHash == result.Tables.CompatibilityHash, result);
                }
                var resetTables = Preview(f, Request(StaticCodeMaskPolicy.NoStaticSourceBits, format));
                Check("table writes do not restore discarded static validity " + format, resetTables.IsAvailable && standalone.Payloads.All(payload =>
                {
                    var chunk = resetTables.Chunks.Single(c => c.Arena == payload.Arena && c.ByteOffsetInArena <= payload.ByteOffset && (ulong)c.ByteOffsetInArena + (uint)c.Length >= (ulong)payload.ByteOffset + (uint)payload.Length);
                    return chunk.DesiredValidity.ToArray().AsSpan(checked((int)(payload.ByteOffset - chunk.ByteOffsetInArena)), payload.Length).SequenceEqual(payload.WriteMask.ToArray());
                }), resetTables);
                Check("table binding replays the same new scope " + format, result.Binding!.IsCurrent(f.Project, f.Device));
                f.Device.ComObjectRefs[0].Links.Clear(); Check("changed table inputs stale the combined result " + format, !result.Binding!.IsCurrent(f.Project, f.Device));
            }
        }
        var zeroMaskTable = TableFixture(directory, GeneratedTableFormat.M112, compatibleSeed: false); var zeroMaskResult = Preview(zeroMaskTable, Request(tables: GeneratedTableFormat.M112));
        Check("zero-mask raw retained table seed cannot replace cleaned working data", Empty(zeroMaskResult, RootImageBuildIssueCode.TableWorkingInputsMismatch), zeroMaskResult);
        var overlap = TableFixture(directory, GeneratedTableFormat.M112, parameterInTable: true); var overlapResult = Preview(overlap, Request(tables: GeneratedTableFormat.M112));
        Check("parameter modifications cannot be overwritten by retained table encoding", Empty(overlapResult)
            && overlapResult.Issues[0].Code is RootImageBuildIssueCode.TableWorkingInputsMismatch or RootImageBuildIssueCode.ParameterTableOverlap, overlapResult);
        var sameBits = TableFixture(directory, GeneratedTableFormat.M112, parameterInTable: true); sameBits.Device.ParameterValues["R0"] = "153";
        var sameBitsResult = Preview(sameBits, Request(tables: GeneratedTableFormat.M112));
        Check("equal current bytes still do not authorize overlapping parameter and table writers", Empty(sameBitsResult, RootImageBuildIssueCode.ParameterTableOverlap), sameBitsResult);
        var occupied = TableFixture(directory, GeneratedTableFormat.SystemB2ByteAssociations, occupiedFresh: true);
        Check("fresh tables never replace a nonzero occupied source interval", Empty(Preview(occupied, Request(tables: GeneratedTableFormat.SystemB2ByteAssociations))));
        Console.WriteLine($"RootImageBuild: {checks} checks, {failures} failures"); return failures;
    }
}
