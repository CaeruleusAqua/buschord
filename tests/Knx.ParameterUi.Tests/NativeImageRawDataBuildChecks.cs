using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class NativeImageRawDataBuildChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static XElement Stat(XDocument app) => NativeGeneralPopulationChecks.Stat(app);
    private static XElement Program(XDocument app) => NativeGeneralPopulationChecks.Program(app);
    private static XElement Parameter(XDocument app) => Stat(app).Element(Ns + "Parameters")!.Elements().First();
    private static XElement Segment(XDocument app) => Stat(app).Element(Ns + "Code")!.Elements().Single();
    private static XDocument Application(string value = "AID/", int maximum = 16, bool little = false)
    {
        var app = NativeGeneralPopulationChecks.App(1);
        Stat(app).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "RAW"),
            new XElement(Ns + "TypeRawData", new XAttribute("MaxSize", maximum))));
        Parameter(app).SetAttributeValue("ParameterType", "RAW"); Parameter(app).SetAttributeValue("Value", value);
        var segment = Segment(app); segment.SetAttributeValue("Size", maximum + 4);
        var seed = Enumerable.Range(0, maximum + 4).Select(i => (byte)(0xA0 + i)).ToArray();
        segment.Element(Ns + "Data")!.Value = Convert.ToBase64String(seed);
        segment.Element(Ns + "Mask")!.Value = Convert.ToBase64String(Enumerable.Repeat((byte)255, seed.Length).ToArray());
        if (little) Stat(app).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "LittleEndian");
        return app;
    }
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null)
        => NativeGeneralImageBuildChecks.Mixed(directory, app ?? Application());
    private static NativeImageBuildPlan Build(ArchivedImageSourceChecks.Fixture f, uint[]? options = null)
        => OfflineNativeImageBuild.Preview(f.Project, f.Device, new(options ?? [129u]));
    private static NativeImageBuildChunk Memory(NativeImageBuildPlan p)
        => p.Variants.Single().Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
    private static bool Empty(NativeImageBuildPlan p) => p.HasGlobalFailure && !p.IsAvailable && p.Binding is null && p.Variants.Count == 0;
    private static string Explain(NativeImageBuildPlan p) => string.Join(';', p.Issues.Concat(p.Variants.SelectMany(v => v.Issues)));
    private static string Xml(Project p) => Knxproj.ToXml(p).ToString(SaveOptions.DisableFormatting);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static int Run(string directory)
    {
        var failures = 0; var count = 0;
        void Check(string label, bool ok) { count++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeImageRawData " + label); if (!ok) failures++; }
        void Refused(string label, Action<XDocument> edit)
        {
            var app = Application(); edit(app); var f = Load(directory, app); var xml = Xml(f.Project); var archive = Hash(f.Path);
            var plan = Build(f);
            Check(label, Empty(plan) && xml == Xml(f.Project) && archive == Hash(f.Path));
        }
        var fixture = Load(directory); var originalXml = Xml(fixture.Project); var originalArchive = Hash(fixture.Path);
        var revision = fixture.Project.ApplicationDataRevision; var image = Build(fixture);
        Check("complete native pipeline admits direct root memory: " + Explain(image), image.IsAvailable && image.AllRequestedAssumptionsAvailable);
        if (!image.IsAvailable) return failures;
        var chunk = Memory(image); var expected = Enumerable.Range(0, 512).Select(i => i < 20 ? (byte)(0xA0 + i) : (byte)0).ToArray();
        Convert.FromHexString("000000030080FF").CopyTo(expected, 0);
        Check("all512 bytes match independent prefix/payload/seed constants", chunk.WorkingData.ToArray().SequenceEqual(expected));
        Check("only seven prefix/payload bytes are parameter-driven", chunk.ParameterWriteBits.ToArray().AsSpan(0,7).ToArray().All(b => b == 255)
            && chunk.ParameterWriteBits.ToArray().AsSpan(7).ToArray().All(b => b == 0));
        Check("tail keeps nonzero source mask independently of write mask", chunk.DesiredValidity!.ToArray().AsSpan(0,7).ToArray().All(b => b == 255)
            && chunk.DesiredValidity.ToArray().AsSpan(7,13).ToArray().All(b => b == 255)
            && chunk.DesiredValidity.ToArray().AsSpan(20).ToArray().All(b => b == 0));
        Check("initial bytes remain distinct from desired bytes", chunk.InitialData[0] == 0xA0 && chunk.InitialData[6] == 0xA6 && chunk.InitialValidity![0] == 255);
        var partialApp = Application(); Segment(partialApp).Element(Ns+"Mask")!.Value = Convert.ToBase64String(Enumerable.Repeat((byte)15,20).ToArray());
        var partial = Build(Load(directory,partialApp));
        Check("partial seed mask remains separate from raw write extent", partial.IsAvailable && Memory(partial).WorkingData[7] == 7
            && Memory(partial).DesiredValidity![7] == 15 && Memory(partial).ParameterWriteBits[7] == 0);
        Check("same evaluation retains property/count contributions", image.Variants.Single().Chunks.Any(c => c.Target.Space == NativeImageBuildSpace.PropertyData)
            && image.Variants.Single().ObservedCounts.Single().EncodedCount == 0x1234);
        Check("policy identifies raw framing and physical population", image.Policy is { RawDataFieldCount: 1, CodecProfileVersion: 4 }
            && image.Policy.RawDataEncodingProfile == "RootMemoryLength32ProgramByteOrderOpaquePayloadRetainedTail"
            && image.Variants.Single().Population is { ReferenceCount: 4, PhysicalRecordCount: 3 });
        Check("build changes neither archive/model/revision nor grants execution", Xml(fixture.Project) == originalXml && Hash(fixture.Path) == originalArchive
            && fixture.Project.ApplicationDataRevision == revision && !image.CompleteApplicationImage && !image.ExecutionEligible && !image.DownloadEligible);
        var second = Build(fixture);
        Check("replay is deterministic and current", second.Binding!.Fingerprint == image.Binding!.Fingerprint && image.Binding.IsCurrent(fixture.Project, fixture.Device));
        var detached = chunk.WorkingData.ToArray(); detached[4] = 0x42;
        Check("public byte buffers remain immutable", chunk.WorkingData[4] == 0 && second.Variants.Single().Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory).WorkingData[4] == 0);
        var little = Build(Load(directory, Application(little: true)));
        Check("little-endian reverses only length prefix", little.IsAvailable && Memory(little).WorkingData.ToArray().AsSpan(0,7).SequenceEqual(Convert.FromHexString("030000000080FF")));
        var empty = Build(Load(directory, Application("")));
        Check("empty payload writes exactly a four-byte zero length", empty.IsAvailable && Memory(empty).WorkingData.ToArray().AsSpan(0,5).SequenceEqual(Convert.FromHexString("00000000A4"))
            && Memory(empty).ParameterWriteBits[3] == 255 && Memory(empty).ParameterWriteBits[4] == 0);
        var payload = Enumerable.Range(0,256).Select(i => (byte)i).ToArray();
        var full = Build(Load(directory, Application(Convert.ToBase64String(payload), 256)));
        Check("full byte alphabet reaches exact MaxSize boundary", full.IsAvailable && Memory(full).WorkingData.ToArray().AsSpan(0,4).SequenceEqual(Convert.FromHexString("00000100"))
            && Memory(full).WorkingData.ToArray().AsSpan(4,256).SequenceEqual(payload));
        var largePayload = Enumerable.Range(0,49152).Select(i => (byte)i).ToArray();
        var large = Build(Load(directory, Application(Convert.ToBase64String(largePayload), largePayload.Length)));
        Check("shared64KiB text boundary writes real48KiB binary payload", large.IsAvailable
            && Memory(large).WorkingData.ToArray().AsSpan(0,4).SequenceEqual(Convert.FromHexString("0000C000"))
            && Memory(large).WorkingData.ToArray().AsSpan(4,largePayload.Length).SequenceEqual(largePayload)
            && large.AllocatedImagePayloadBytes <= large.ReservedWorstCaseImagePayloadBytes && large.ReservedWorstCaseImagePayloadBytes <= 16*1024*1024);
        var overBudget = Build(Load(directory, Application(Convert.ToBase64String(new byte[49153]), 49153)));
        Check("oversized raw input refuses without a partial image", Empty(overBudget));
        fixture.Device.ParameterValues["R0"] = " A I\tD/\r\n";
        Check("equal bytes with a changed raw value stale the old binding", !image.Binding.IsCurrent(fixture.Project, fixture.Device));
        var spaced = Build(fixture);
        Check("strict Base64 whitespace reaches writer without text conversion", spaced.IsAvailable && Memory(spaced).WorkingData.Sha256 == chunk.WorkingData.Sha256);
        fixture.Device.ParameterValues["R0"] = "AQI=";
        var changed = Build(fixture);
        Check("saved payload overrides definition and keeps shortened tail at seed", changed.IsAvailable && Memory(changed).WorkingData.ToArray().AsSpan(0,8).SequenceEqual(Convert.FromHexString("000000020102A6A7"))
            && Memory(changed).ParameterWriteBits[6] == 0);
        foreach (var behavior in new[] { "DefaultValue", "Background", "None" })
        {
            var app = Application(); Program(app).Element(Ns + "Dynamic")!.Elements().Single(e => (string?)e.Attribute("RefId") == "R0").Remove();
            Stat(app).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", behavior);
            var f = Load(directory, app); f.Device.ParameterValues["R0"] = "AQI="; var p = Build(f);
            Check("inactive raw policy " + behavior, p.IsAvailable && Memory(p).WorkingData[0] == (behavior == "DefaultValue" ? 0 : 0xA0)
                && Memory(p).ParameterWriteBits[0] == (behavior == "DefaultValue" ? 255 : 0)
                && Memory(p).DesiredValidity![0] == 255
                && Memory(p).ParameterWriteBits[7] == 0);
        }
        var excludedApp = Application(); Parameter(excludedApp).SetAttributeValue("CustomerAdjustable", true);
        var excluded = Build(Load(directory, excludedApp), [1153]);
        Check("customer exclusion clears only actual frame validity", excluded.IsAvailable && Memory(excluded).WorkingData[0] == 0xA0
            && Memory(excluded).DesiredValidity![6] == 0 && Memory(excluded).DesiredValidity![7] == 255 && Memory(excluded).ParameterWriteBits[0] == 0);
        var skipped = Build(Load(directory), [128]);
        Check("skipped parameter task preserves complete seed", skipped.IsAvailable && Memory(skipped).WorkingData[0] == 0xA0
            && Memory(skipped).ParameterWriteBits.ToArray().All(b => b == 0));
        var relativeApp = Application(); var s = Segment(relativeApp); s.Name = Ns + "RelativeSegment"; s.Attribute("Address")!.Remove();
        s.SetAttributeValue("Offset", 100); s.SetAttributeValue("LoadStateMachine", 3);
        var relative = Build(Load(directory, relativeApp));
        Check("explicit relative memory uses the same payload writer", relative.IsAvailable && relative.Variants.Single().Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.RelativeMemory)
            .WorkingData.ToArray().AsSpan(0,7).SequenceEqual(Convert.FromHexString("000000030080FF")));
        foreach (var value in new[] { "A", "00-FF", "A===", "AA\u00a0==", Convert.ToBase64String(new byte[17]) })
            Refused("invalid payload is an atomic refusal " + value.Length, a => Parameter(a).SetAttributeValue("Value", value));
        Refused("unaligned raw layout is refused", a => Parameter(a).Element(Ns + "Memory")!.SetAttributeValue("BitOffset", 1));
        Refused("entire reserved capacity must fit, even for short payload", a => {
            Segment(a).SetAttributeValue("Size", 6); Segment(a).Element(Ns+"Data")!.Value = Convert.ToBase64String(new byte[6]);
            Segment(a).Element(Ns+"Mask")!.Value = Convert.ToBase64String(new byte[6]); });
        Refused("capacity also participates in alias detection", a => { var p = Stat(a).Element(Ns + "Parameters")!.Elements().ElementAt(1); p.Elements().Remove();
            p.Add(new XElement(Ns+"Memory", new XAttribute("CodeSegment","S"),new XAttribute("Offset",16),new XAttribute("BitOffset",0))); });
        Refused("raw Property is not admitted", a => { Parameter(a).Elements().Remove(); Parameter(a).Add(new XElement(Ns+"Property",new XAttribute("ObjectIndex",3),new XAttribute("PropertyId",10),new XAttribute("Offset",0),new XAttribute("BitOffset",0))); });
        Refused("raw without memory is not admitted", a => Parameter(a).Elements().Remove());
        Refused("raw does not loosen module admission", a => Stat(a).Add(new XElement(Ns+"ModuleDef",new XAttribute("Id","UnsupportedModule"))));
        foreach (var maximum in new[] { "0", "1048573", "bad" }) Refused("invalid MaxSize " + maximum,
            a => Stat(a).Descendants(Ns+"TypeRawData").Single().SetAttributeValue("MaxSize",maximum));
        Refused("unknown raw declaration attributes stay refused", a => Stat(a).Descendants(Ns+"TypeRawData").Single().SetAttributeValue("Encoding","Hex"));
        Refused("late invalid ordinary parameter yields no partial raw image", a => Stat(a).Element(Ns+"Parameters")!.Elements().ElementAt(2).SetAttributeValue("Value","bad"));
        var m112 = M112UnionBuildChecks.Fixture(directory, fullLoad: true, fullEdit: p => AddM112Raw(p));
        var m112Image = OfflineM112ApplicationBuild.Build(m112.Project, m112.Device);
        var appSegment = m112Image.Segments.Single(sg => sg.ObjectIndex == 3 && sg.MemoryType == 3);
        Check("full M112 image uses raw writer alongside unions and generated tables", appSegment.Data.ToArray().AsSpan(0x70,7).SequenceEqual(Convert.FromHexString("000000030080FF"))
            && m112Image.IsCurrent(m112.Project,m112.Device));
        var target = M112UnionBuildChecks.Fixture(directory, fullLoad:true, fullEdit:p=>AddM112Raw(p));
        target.Device.ParameterValues["RawDirectR"] = "AA==";
        var change = M112ApplicationChangePlan.Prepare(m112.Project,m112.Device,target.Project,target.Device);
        Check("raw parameter edits enter the existing reviewed M112 change plan", change.HasChanges && change.IsCurrent()
            && change.Desired.Segments.Single(sg=>sg.ObjectIndex==3&&sg.MemoryType==3).Data.ToArray().AsSpan(0x70,5).SequenceEqual(Convert.FromHexString("0000000100"))
            && change.Changes.Any(c=>c.Address>=0x4470&&c.Address+c.Length<=0x4477));
        var badUnion = M112UnionBuildChecks.Fixture(directory, edit: p => {
            var stat=p.Element(Ns+"Static")!; stat.Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","RawUnion"),new XElement(Ns+"TypeRawData",new XAttribute("MaxSize",1))));
            stat.Element(Ns+"Parameters")!.Descendants(Ns+"Parameter").First().SetAttributeValue("ParameterType","RawUnion"); });
        Check("RawData union alias remains explicitly refused", Empty(OfflineM112ImageBuild.Preview(badUnion.Project,badUnion.Device)));
        Console.WriteLine($"NativeImageRawData: {count} checks, {failures} failures"); return failures;
    }
    private static void AddM112Raw(XElement program)
    {
        var stat=program.Element(Ns+"Static")!;
        stat.Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","RawDirect"),new XElement(Ns+"TypeRawData",new XAttribute("MaxSize",12))));
        stat.Element(Ns+"Parameters")!.Add(new XElement(Ns+"Parameter",new XAttribute("Id","RawDirectP"),new XAttribute("ParameterType","RawDirect"),new XAttribute("Value","AID/"),
            new XElement(Ns+"Memory",new XAttribute("CodeSegment","PARAM"),new XAttribute("Offset",80),new XAttribute("BitOffset",0))));
        stat.Element(Ns+"ParameterRefs")!.Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","RawDirectR"),new XAttribute("RefId","RawDirectP")));
        program.Element(Ns+"Dynamic")!.Add(new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","RawDirectR")));
    }
}
