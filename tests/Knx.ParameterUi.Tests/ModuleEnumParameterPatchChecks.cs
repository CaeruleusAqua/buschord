using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Knx;

internal static class ModuleEnumParameterPatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000", EnumRef = "Call1_MI-1_ER", NumberRef = "Call1_MI-1_NR";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/23";
    private static XElement Shape(int bits, IEnumerable<string> values) => new(Ns + "TypeRestriction",
        new XAttribute("Base", "Value"), new XAttribute("SizeInBit", bits), new XAttribute("UIHint", "DropDown"),
        values.Select((v, i) => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", v),
            new XAttribute("Text", "Choice " + i), new XAttribute("DisplayOrder", 10000 - i))));
    private static XDocument App(int bits = 1, string[]? choices = null, int bit = 2)
    {
        var document = XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="64"><Data>{{Convert.ToBase64String(new byte[64])}}</Data></AbsoluteSegment></Code>
            <ParameterTypes><ParameterType Id="E"/><ParameterType Id="N"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="30000"/></ParameterType>
            <ParameterType Id="ModeT"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="1"/></ParameterType><ParameterType Id="RootE"/></ParameterTypes>
            <Parameters><Parameter Id="ModeP" ParameterType="ModeT" Value="1"/>
            <Parameter Id="RootEP" ParameterType="RootE" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
            <Parameter Id="RootNP" ParameterType="N" Value="0"><Memory CodeSegment="AS" Offset="2" BitOffset="0"/></Parameter></Parameters>
            <ParameterRefs><ParameterRef Id="Mode" RefId="ModeP"/><ParameterRef Id="RootER" RefId="RootEP"/><ParameterRef Id="RootNR" RefId="RootNP"/></ParameterRefs></Static>
            <ModuleDefs><ModuleDef Id="MD" Name="Direct"><Arguments><Argument Id="MD_A" Name="Base" Allocates="4"/></Arguments>
            <Static><Parameters><Parameter Id="MD_EP" ParameterType="E" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="{{bit}}" BaseOffset="MD_A"/></Parameter>
            <Parameter Id="MD_NP" ParameterType="N" Value="0"><Memory CodeSegment="AS" Offset="2" BitOffset="0" BaseOffset="MD_A"/></Parameter></Parameters>
            <ParameterRefs><ParameterRef Id="MD_ER" RefId="MD_EP"/><ParameterRef Id="MD_NR" RefId="MD_NP"/></ParameterRefs></Static>
            <Dynamic><ParameterRefRef RefId="MD_ER"/><ParameterRefRef RefId="MD_NR"/></Dynamic></ModuleDef></ModuleDefs>
            <Dynamic><choose ParamRefId="Mode"><when test="1"><Module Id="Call1" RefId="MD"><NumericArg RefId="MD_A" Value="4"/></Module></when><when test="0"/></choose></Dynamic>
            </ApplicationProgram></KNX>
            """);
        Type(document, "E").Add(Shape(bits, choices ?? ["0", "1"])); Type(document, "RootE").Add(Shape(16, ["0", "300"]));
        return document;
    }
    private static XElement Type(XDocument d, string id) => d.Descendants(Ns + "ParameterType").Single(e => (string?)e.Attribute("Id") == id);
    private static XElement Parameter(XDocument d, string id = "MD_EP") => d.Descendants(Ns + "Parameter").Single(e => (string?)e.Attribute("Id") == id);
    private static XElement Reference(XDocument d, string id = "MD_ER") => d.Descendants(Ns + "ParameterRef").Single(e => (string?)e.Attribute("Id") == id);
    private static XElement Definition(XDocument d) => d.Descendants(Ns + "ModuleDef").Single();
    private static XElement Call(XDocument d) => d.Descendants(Ns + "Module").Single(e => (string?)e.Attribute("Id") == "Call1");
    private static XElement RootStatic(XDocument d) => d.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
    private static void Data(XDocument d, byte value, bool mask = false)
    {
        var bytes = new byte[64]; bytes[4] = value;
        var segment = d.Descendants(Ns + "AbsoluteSegment").Single();
        if (mask) segment.Add(new XElement(Ns + "Mask", Convert.ToBase64String(bytes)));
        else segment.Element(Ns + "Data")!.Value = Convert.ToBase64String(bytes);
    }
    private static void AddCall(XDocument d, string id, string basis, string branch = "1")
    {
        var copy = new XElement(Call(d)); copy.SetAttributeValue("Id", id); copy.Element(Ns + "NumericArg")!.SetAttributeValue("Value", basis);
        d.Descendants(Ns + "choose").Single().Elements(Ns + "when").Single(e => (string?)e.Attribute("test") == branch).Add(copy);
    }
    private static void Plain(XDocument d, bool number)
    {
        var p = number ? "MD_NP" : "MD_EP"; var r = number ? "MD_NR" : "MD_ER";
        Parameter(d, p).SetAttributeValue("Id", "PlainP"); Reference(d, r).SetAttributeValue("Id", "PlainR"); Reference(d, "PlainR").SetAttributeValue("RefId", "PlainP");
        foreach (var item in d.Descendants(Ns + "ParameterRefRef").Where(e => (string?)e.Attribute("RefId") == r)) item.SetAttributeValue("RefId", "PlainR");
    }
    private static void Calculation(XDocument d, string source)
    {
        var stat = Definition(d).Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "MD_Other"), new XAttribute("ParameterType", "E"), new XAttribute("Value", "0")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "MD_OtherR"), new XAttribute("RefId", "MD_Other")));
        stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "y=x;"), new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", source), new XAttribute("AliasName", "x"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_OtherR"), new XAttribute("AliasName", "y"))))));
    }
    private static (Project Project, DeviceInstance Device) Fixture(string directory, XDocument application)
    {
        var path = Path.Combine(directory, "module-enum-patch-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Checked direct module enum subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = AppId, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) application.Save(stream);
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single());
    }
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "ModuleEnumPatch " + name); if (!ok) failures++; }
        ParameterPatchPreview Preview(XDocument app, string raw = "1", string reference = EnumRef, ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        { var f = Fixture(directory, app); return OfflineParameterPatch.Preview(f.Project, f.Device, [new(reference, raw)], order); }
        static bool Atomic(ParameterPatchPreview p) => !p.IsAvailable && p.Binding is null && p.Issues.Count > 0 && p.Fields.Count == 0 && p.Ranges.Count == 0 && p.Resources.Count == 0;
        static bool Has(ParameterPatchPreview p, ParameterPatchIssueCode code) => Atomic(p) && p.Issues.Any(i => i.Code == code);
        var basic = Preview(App());
        Check("one bit module ValueEnum uses literal base and profile2", basic.IsAvailable && basic.Ranges.Single().StartAddress == 4100
            && basic.Ranges.Single().BeforeData.ToHexString() == "00" && basic.Ranges.Single().AfterData.ToHexString() == "20"
            && basic.Ranges.Single().FieldMask.ToHexString() == "20" && basic.Ranges.Single().AfterMask is null
            && basic.Binding is { CodecProfileVersion: 3, ModuleScopeProfileVersion: 2, ModuleScopeWitnessHash.Length: 64, NumericParseProfile: "ExactInteger" });
        var sparse = App(16, ["0", "30", "300"], 0);
        var sparseBe = Preview(sparse, "300"); var sparseLe = Preview(sparse, "300", order: ParameterPatchByteOrder.LittleEndian);
        Check("sparse16 is declared value300 rather than ordinal2", sparseBe.IsAvailable && sparseBe.Ranges.Single().AfterData.ToHexString() == "012C");
        Check("aligned sparse16 little endian reuses existing enum codec", sparseLe.IsAvailable && sparseLe.Ranges.Single().AfterData.ToHexString() == "2C01");
        var packed = App(3, ["0", "5"]); Data(packed, 0xD3); Data(packed, 0x80, true);
        var packedPreview = Preview(packed, "5");
        Check("module subbyte preserves unrelated data and mask", packedPreview.IsAvailable && packedPreview.Ranges.Single().AfterData.ToHexString() == "EB"
            && packedPreview.Ranges.Single().FieldMask.ToHexString() == "38" && packedPreview.Ranges.Single().AfterMask?.ToHexString() == "B8");
        var unsigned = App(32, ["0", "4294967295"], 0); Parameter(unsigned, "MD_NP").Element(Ns + "Memory")!.SetAttributeValue("Offset", "4");
        var unsignedPreview = Preview(unsigned, "4294967295");
        Check("module selector retains full UInt32 word", unsignedPreview.IsAvailable && unsignedPreview.Ranges.Single().AfterData.ToHexString() == "FFFFFFFF");
        var mixed = App(); AddCall(mixed, "Call2", "16"); var f = Fixture(directory, mixed); var before = Knxproj.ToXml(f.Project).ToString();
        var one = OfflineParameterPatch.Preview(f.Project, f.Device, [new(EnumRef, "1"), new("Call2_MI-1_NR", "10")], ParameterPatchByteOrder.BigEndian);
        var reversed = OfflineParameterPatch.Preview(f.Project, f.Device, [new("Call2_MI-1_NR", "10"), new(EnumRef, "1")], ParameterPatchByteOrder.BigEndian);
        Check("mixed independent occurrences are atomic profile2 in both orders", one.IsAvailable && reversed.IsAvailable && one.Ranges.Count == 2
            && one.Ranges[0].AfterData.ToHexString() == "20" && one.Ranges[1].AfterData.ToHexString() == "000A"
            && one.Binding!.ModuleScopeProfileVersion == 2 && one.Binding.Fingerprint == reversed.Binding!.Fingerprint);
        Check("preview leaves source resources and project values unchanged", one.IsAvailable && Knxproj.ToXml(f.Project).ToString() == before
            && f.Device.ParameterValues.Count == 0 && !one.CompleteApplicationImage && !one.DownloadEligible);
        var number = OfflineParameterPatch.Preview(f.Project, f.Device, [new(NumberRef, "10")], ParameterPatchByteOrder.BigEndian);
        var rootEnumAndNumber = OfflineParameterPatch.Preview(f.Project, f.Device, [new("RootER", "300"), new(NumberRef, "10")], ParameterPatchByteOrder.BigEndian);
        Check("root enum plus module Number retains exact scope1 witness", number.IsAvailable && rootEnumAndNumber.IsAvailable
            && number.Binding!.ModuleScopeProfileVersion == 1 && rootEnumAndNumber.Binding!.ModuleScopeProfileVersion == 1
            && number.Binding.ModuleScopeWitnessHash == rootEnumAndNumber.Binding.ModuleScopeWitnessHash);
        var rootOnly = OfflineParameterPatch.Preview(f.Project, f.Device, [new("RootER", "300")], ParameterPatchByteOrder.BigEndian);
        Check("root-only enum never acquires module scope", rootOnly.IsAvailable && rootOnly.Binding is { ModuleScopeProfileVersion: null, ModuleScopeWitnessHash: null });
        var repeatPreview = OfflineParameterPatch.Preview(f.Project, f.Device, [new(EnumRef, "1"), new("Call2_MI-1_NR", "10")], ParameterPatchByteOrder.BigEndian);
        Check("equivalent regenerated enum scope has stable binding", one.IsAvailable && repeatPreview.IsAvailable && repeatPreview.Binding!.Fingerprint == one.Binding!.Fingerprint);
        f.Device.ParameterValues["Mode"] = "0";
        Check("ordinary branch retirement stales old scope and refuses missing row", !one.Binding!.IsCurrent(f.Project, f.Device)
            && Has(OfflineParameterPatch.Preview(f.Project, f.Device, [new(EnumRef, "1")], ParameterPatchByteOrder.BigEndian), ParameterPatchIssueCode.UnknownParameter));
        foreach (var raw in new[] { "2", "01", "+1", "Choice 1", "E1" })
            Check("exact native choice membership " + raw, Has(Preview(App(), raw), ParameterPatchIssueCode.InvalidValue));
        Check("subbyte little endian stays unsupported", Has(Preview(App(), order: ParameterPatchByteOrder.LittleEndian), ParameterPatchIssueCode.UnsupportedLayout));
        var cap = Enumerable.Range(0, 4096).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        Check("selected4096 is checked before typed parse and remains supported", Preview(App(16, cap, 0), "4095").IsAvailable);
        var excessive = App(16, cap.Concat(["4096"]).ToArray(), 0);
        Parameter(excessive, "ModeP").SetAttributeValue("Value", "2"); // Outside ModeT's declared range; no valid occurrence can be selected.
        Check("selected4097 refuses before even an invalid Snapshot can run", Has(Preview(excessive), ParameterPatchIssueCode.ResourceLimitExceeded));
        foreach (var duplicate in new[] { "ref", "parameter" })
        {
            var d = App(16, cap.Concat(["4096"]).ToArray(), 0);
            var node = duplicate == "ref" ? Reference(d) : Parameter(d); node.Parent!.Add(new XElement(node));
            Check("4097 ambiguous original " + duplicate + " refuses before typed choices", Has(Preview(d), ParameterPatchIssueCode.InvalidDeclaration));
        }
        var unrelated = App(); RootStatic(unrelated).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "Unrelated"), Shape(16, cap.Concat(["4096"]))));
        Check("module Number ignores unrelated4097 restriction profile", Preview(unrelated, "10", NumberRef).Binding is { ModuleScopeProfileVersion: 1 });
        Check("root Number keeps lightweight unrelated4097 path", Preview(unrelated, "10", "RootNR").Binding is { ModuleScopeProfileVersion: null });
        var unusedLocal = App();
        Definition(unusedLocal).Parent!.Add(new XElement(Ns + "ModuleDef", new XAttribute("Id", "Unused"), new XAttribute("Name", "Unused"),
            new XElement(Ns + "Static", new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "N"), Shape(1, ["0", "1"]))))));
        Check("unused local enum with root Number type id cannot narrow scope1", Preview(unusedLocal, "10", NumberRef).Binding is { ModuleScopeProfileVersion: 1 });
        var many = App(); for (var i = 0; i < 257; i++) AddCall(many, "Inactive" + i, "4", "0");
        Check("257 inactive raw alternatives do not become actual instance limit", Preview(many, "10", NumberRef).Binding is { ModuleScopeProfileVersion: 1 });
        var plainNumber = App(); Plain(plainNumber, true); AddCall(plainNumber, "Alternative", "4", "0");
        Parameter(plainNumber, "PlainP").Element(Ns + "Memory")!.Attribute("BaseOffset")!.Remove();
        Parameter(plainNumber, "PlainP").Element(Ns + "Memory")!.SetAttributeValue("Offset", "6");
        Check("unqualified Number with one active of two calls keeps published scope1", Preview(plainNumber, "10", "PlainR").Binding is { ModuleScopeProfileVersion: 1 });
        var plainEnum = App(); Plain(plainEnum, false);
        Check("unique unqualified original enum has exact source proof", Preview(plainEnum, reference: "PlainR").Binding is { ModuleScopeProfileVersion: 2 });
        AddCall(plainEnum, "Alternative", "4", "0");
        Check("unqualified enum cannot choose between two raw original calls", Has(Preview(plainEnum, reference: "PlainR"), ParameterPatchIssueCode.InvalidDeclaration));
        var mutations = new (string Name, Action<XDocument> Mutate)[]
        {
            ("BinaryValue even empty", d => Type(d, "E").Element(Ns + "TypeRestriction")!.Elements().First().SetAttributeValue("BinaryValue", "")),
            ("binary base", d => Type(d, "E").Element(Ns + "TypeRestriction")!.SetAttributeValue("Base", "BinaryValue")),
            ("wide diagnostic enum", d => Type(d, "E").Element(Ns + "TypeRestriction")!.SetAttributeValue("SizeInBit", "64")),
            ("noncanonical unselected member", d => Type(d, "E").Descendants(Ns + "Enumeration").Last().SetAttributeValue("Value", "01")),
            ("duplicate member Id", d => Type(d, "E").Descendants(Ns + "Enumeration").Last().SetAttributeValue("Id", "E0")),
            ("duplicate original call", d => Call(d).Parent!.Add(new XElement(Call(d)))),
            ("duplicate original definition", d => Definition(d).Parent!.Add(new XElement(Definition(d)))),
            ("duplicate original type", d => Type(d, "E").Parent!.Add(new XElement(Type(d, "E")))),
            ("module-local type", d => { var t = Type(d, "E"); t.Remove(); Definition(d).Element(Ns + "Static")!.Add(new XElement(Ns + "ParameterTypes", t)); }),
            ("reference type override", d => Reference(d).SetAttributeValue("ParameterType", "E")),
            ("alias", d => { var r = new XElement(Reference(d)); r.SetAttributeValue("Id", "MD_Alias"); Reference(d).Parent!.Add(r); }),
            ("hidden overlapping original owner", d => { var p = new XElement(Parameter(d)); p.SetAttributeValue("Id", "MD_Other"); p.SetAttributeValue("Access", "None"); Parameter(d).Parent!.Add(p); }),
            ("untyped overlapping original owner", d => { var p = new XElement(Parameter(d)); p.SetAttributeValue("Id", "MD_Other"); p.Attribute("ParameterType")!.Remove(); Parameter(d).Parent!.Add(p); }),
            ("Assign participation", d => Definition(d).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "MD_ER"), new XAttribute("Value", "1")))),
            ("Calculation participation", d => Calculation(d, "MD_ER")),
            ("assigned branch selector", d => d.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Dynamic")!.AddFirst(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Mode"), new XAttribute("Value", "1")))),
            ("unexecuted branch selector JS", d => { var stat = RootStatic(d); stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "OtherP"), new XAttribute("ParameterType", "ModeT"), new XAttribute("Value", "0")));
                stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "OtherR"), new XAttribute("RefId", "OtherP")));
                stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"), new XElement(Ns + "LRTransformation", "y=x;"),
                    new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Mode"), new XAttribute("AliasName", "x"))),
                    new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "OtherR"), new XAttribute("AliasName", "y")))))); }),
            ("missing explicit argument", d => Call(d).Element(Ns + "NumericArg")!.Attribute("Value")!.Remove()),
            ("allocator", d => Call(d).Element(Ns + "NumericArg")!.SetAttributeValue("AllocatorRefId", "Missing")),
            ("parameter BaseValue", d => Parameter(d).SetAttributeValue("BaseValue", "MD_A")),
            ("local Code", d => Definition(d).Element(Ns + "Static")!.Add(new XElement(Ns + "Code"))),
            ("short Mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "AA=="))),
            ("missing Data", d => d.Descendants(Ns + "Data").Single().Remove()),
            ("Repeat ancestry", d => { var c = Call(d); var p = c.Parent!; c.Remove(); p.Add(new XElement(Ns + "Repeat", new XAttribute("Id", "Repeat1"), new XAttribute("Count", "1"), c)); }),
        };
        foreach (var mutation in mutations) { var d = App(); mutation.Mutate(d); Check("atomic refusal " + mutation.Name, Atomic(Preview(d))); }
        var collision = App(); var root = RootStatic(collision);
        root.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "CollisionP"), new XAttribute("ParameterType", "E"), new XAttribute("Value", "0"),
            new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", "32"), new XAttribute("BitOffset", "0"))));
        root.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", EnumRef), new XAttribute("RefId", "CollisionP")));
        Check("root and module identities cannot overwrite original ownership", Atomic(Preview(collision)));
        var mixedFailure = Fixture(directory, App());
        Check("valid Number cannot leak bytes beside invalid module enum", Atomic(OfflineParameterPatch.Preview(mixedFailure.Project, mixedFailure.Device,
            [new(NumberRef, "10"), new(EnumRef, "2")], ParameterPatchByteOrder.BigEndian)));
        return failures;
    }
}
