using System.IO.Compression;
using System.Xml.Linq;
using Knx;

internal static class DenseTimeParameterPatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000", Profile = "SignedRawOrdinaryTime8To32", ModuleTime = "Call1_MI-1_TR";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/23";
    private static XDocument App(int bits = 16, string minimum = "-32768", string maximum = "32767", int bit = 0, bool modules = false)
    {
        var d = XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="64"><Data>{{Convert.ToBase64String(new byte[64])}}</Data></AbsoluteSegment></Code>
            <ParameterTypes><ParameterType Id="T"><TypeTime SizeInBit="{{bits}}" Unit="Seconds" minInclusive="{{minimum}}" maxInclusive="{{maximum}}" UIHint="Duration_hhmmss"/></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="{{bit}}"/></Parameter></Parameters>
            <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
            """);
        if (!modules) return d;
        var stat = RootStatic(d);
        stat.Element(Ns + "ParameterTypes")!.Add(
            new XElement(Ns + "ParameterType", new XAttribute("Id", "N"), new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", "16"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "30000"))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "E"), new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "1"),
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", "0")), new XElement(Ns + "Enumeration", new XAttribute("Id", "E1"), new XAttribute("Value", "1")))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "ModeT"), new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", "8"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "1"))));
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "ModeP"), new XAttribute("ParameterType", "ModeT"), new XAttribute("Value", "1")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Mode"), new XAttribute("RefId", "ModeP")));
        var application = d.Descendants(Ns + "ApplicationProgram").Single();
        application.Add(new XElement(Ns + "ModuleDefs", new XElement(Ns + "ModuleDef", new XAttribute("Id", "MD"), new XAttribute("Name", "Direct"),
            new XElement(Ns + "Arguments", new XElement(Ns + "Argument", new XAttribute("Id", "MD_A"), new XAttribute("Name", "Base"), new XAttribute("Allocates", "8"))),
            new XElement(Ns + "Static", new XElement(Ns + "Parameters", Field("MD_TP", "T", 0, true), Field("MD_NP", "N", 2, true), Field("MD_EP", "E", 4, true)),
                new XElement(Ns + "ParameterRefs", Ref("MD_TR", "MD_TP"), Ref("MD_NR", "MD_NP"), Ref("MD_ER", "MD_EP"))),
            new XElement(Ns + "Dynamic", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_TR"))))));
        application.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "Mode"),
            new XElement(Ns + "when", new XAttribute("test", "1"), new XElement(Ns + "Module", new XAttribute("Id", "Call1"), new XAttribute("RefId", "MD"),
                new XElement(Ns + "NumericArg", new XAttribute("RefId", "MD_A"), new XAttribute("Value", "16")))), new XElement(Ns + "when", new XAttribute("test", "0"))));
        return d;
    }
    private static XElement Field(string id, string type, int offset, bool module = false) => new(Ns + "Parameter", new XAttribute("Id", id), new XAttribute("ParameterType", type),
        new XAttribute("Value", "0"), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", offset), new XAttribute("BitOffset", "0"),
            module ? new XAttribute("BaseOffset", "MD_A") : null));
    private static XElement Ref(string id, string parameter) => new(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", parameter));
    private static XElement RootStatic(XDocument d) => d.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
    private static XElement Shape(XDocument d) => d.Descendants(Ns + "TypeTime").Single();
    private static XElement Definition(XDocument d) => d.Descendants(Ns + "ModuleDef").First();
    private static void SetData(XDocument d, byte[] data, byte[]? mask = null)
    {
        var bytes = new byte[64]; data.CopyTo(bytes, 0); var segment = d.Descendants(Ns + "AbsoluteSegment").Single();
        segment.Element(Ns + "Data")!.Value = Convert.ToBase64String(bytes);
        if (mask is not null) { var masks = new byte[64]; mask.CopyTo(masks, 0); segment.Add(new XElement(Ns + "Mask", Convert.ToBase64String(masks))); }
    }
    private static void Calculation(XDocument d, bool selected, bool module = false)
    {
        var stat = module ? Definition(d).Element(Ns + "Static")! : RootStatic(d); var prefix = module ? "MD_" : "";
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", prefix + "OtherP"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
        stat.Element(Ns + "ParameterRefs")!.Add(Ref(prefix + "OtherR", prefix + "OtherP"));
        if (!selected)
        {
            stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", prefix + "InputP"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
            stat.Element(Ns + "ParameterRefs")!.Add(Ref(prefix + "InputR", prefix + "InputP"));
        }
        stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "y=x;"), new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", selected ? module ? "MD_TR" : "R" : prefix + "InputR"), new XAttribute("AliasName", "x"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", prefix + "OtherR"), new XAttribute("AliasName", "y"))))));
    }
    private static (Project Project, DeviceInstance Device) Fixture(string directory, XDocument application)
    {
        var path = Path.Combine(directory, "dense-time-patch-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Explicit raw signed Time subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = AppId, Address = new(1, 1, 1) }); Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) application.Save(stream);
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single());
    }
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "DenseTimePatch " + name); if (!ok) failures++; }
        ParameterPatchPreview Preview(XDocument d, string raw = "1", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian, string reference = "R")
        { var f = Fixture(directory, d); return OfflineParameterPatch.Preview(f.Project, f.Device, [new(reference, raw)], order); }
        static bool Atomic(ParameterPatchPreview p) => !p.IsAvailable && p.Binding is null && p.Issues.Count > 0 && p.Fields.Count == 0 && p.Resources.Count == 0 && p.Ranges.Count == 0;
        static bool Has(ParameterPatchPreview p, ParameterPatchIssueCode code) => Atomic(p) && p.Issues.Any(i => i.Code == code);
        // Literal signed MSB payloads, including high-bit31/32 and nonbyte padding;
        // expected bytes do not reuse the production encoder or its shifts.
        foreach (var (bits, min, max, minusOne, maximum) in new[] {
            (8, "-128", "127", "FF", "7F"), (9, "-256", "255", "FF80", "7F80"),
            (11, "-1024", "1023", "FFE0", "7FE0"), (16, "-32768", "32767", "FFFF", "7FFF"),
            (24, "-8388608", "8388607", "FFFFFF", "7FFFFF"), (31, "-1073741824", "1073741823", "FFFFFFFE", "7FFFFFFE"),
            (32, "-2147483648", "2147483647", "FFFFFFFF", "7FFFFFFF") })
        {
            foreach (var (raw, expected) in new[] { (min, "80" + new string('0', maximum.Length - 2)), ("-1", minusOne), ("0", new string('0', maximum.Length)), (max, maximum) })
            {
                var p = Preview(App(bits, min, max), raw);
                Check("signed" + bits + " raw" + raw + " ->" + expected, p.IsAvailable && p.Ranges.Single().AfterData.ToHexString() == expected
                    && p.Binding is { CodecProfileVersion: 4, DenseTimeEncodingProfile: Profile, PackedTimeEncodingProfile: null, ModuleScopeProfileVersion: null, NumericParseProfile: "ExactInteger" });
            }
        }
        foreach (var unit in new[] { "Hours", "Minutes", "Seconds", "HundredMilliseconds", "TenMilliseconds", "Milliseconds" })
        { var d = App(); Shape(d).SetAttributeValue("Unit", unit); var p = Preview(d); Check("raw1 same bytes without scaling " + unit, p.IsAvailable && p.Ranges.Single().AfterData.ToHexString() == "0001"); }
        foreach (var (raw, expected) in new[] { ("10", "000A"), ("60", "003C"), ("3600", "0E10"), ("28800", "7080") })
        { var p = Preview(App(16, "0", "28800"), raw); Check("ordinary Seconds literal " + raw, p.IsAvailable && p.Ranges.Single().AfterData.ToHexString() == expected); }
        foreach (var (bits, min, max, expected) in new[] { (8, "-128", "127", "FE"), (16, "-32768", "32767", "FEFF"), (24, "-8388608", "8388607", "FEFFFF"), (32, "-2147483648", "2147483647", "FEFFFFFF") })
        { var p = Preview(App(bits, min, max), "-2", ParameterPatchByteOrder.LittleEndian); Check("aligned signed little endian " + bits, p.IsAvailable && p.Ranges.Single().AfterData.ToHexString() == expected); }
        var packed = App(9, "-256", "255", 3); SetData(packed, [0xA5, 0x5A], [0x80, 1]); var bitPreview = Preview(packed, "-1");
        Check("signed9 bit3 preserves unrelated data and source mask", bitPreview.IsAvailable && bitPreview.Ranges.Single().AfterData.ToHexString() == "BFFA"
            && bitPreview.Ranges.Single().FieldMask.ToHexString() == "1FF0" && bitPreview.Ranges.Single().AfterMask?.ToHexString() == "9FF1");
        var carry = App(32, "-2147483648", "2147483647", 7); SetData(carry, [0xA4, 0x5A, 0, 0xA5, 0x5A], [0x80, 0, 0, 0, 1]); var carryPreview = Preview(carry, "-1");
        Check("signed32 bit7 spans five bytes without shift32 overflow", carryPreview.IsAvailable && carryPreview.Ranges.Single().AfterData.ToHexString() == "A5FFFFFFFE"
            && carryPreview.Ranges.Single().FieldMask.ToHexString() == "01FFFFFFFE" && carryPreview.Ranges.Single().AfterMask?.ToHexString() == "81FFFFFFFF");
        foreach (var bits in new[] { 9, 11, 31 }) Check("little endian partial width stays unsupported " + bits,
            Has(Preview(App(bits, "0", "1"), order: ParameterPatchByteOrder.LittleEndian), ParameterPatchIssueCode.UnsupportedLayout));
        Check("little endian shifted whole width stays unsupported", Has(Preview(App(bit: 1), order: ParameterPatchByteOrder.LittleEndian), ParameterPatchIssueCode.UnsupportedLayout));
        foreach (var (bits, min, max) in new[] { (8, "0", "255"), (9, "-257", "255"), (31, "0", "1073741824"), (32, "-2147483649", "2147483647"), (32, "0", "2147483648") })
            Check("whole declaration must fit signed width " + bits + ":" + min + "/" + max, Has(Preview(App(bits, min, max)), ParameterPatchIssueCode.InvalidDeclaration));
        foreach (var bits in new[] { 7, 33, 64 }) Check("outside dense width profile " + bits, Has(Preview(App(bits, "0", "1")), ParameterPatchIssueCode.UnsupportedParameter));
        foreach (var raw in new[] { "28801", "-1", "1.5", "1e3", "NaN", "INF", "PT1H", "00:00:01", "1ms", "9223372036854775808", "-9223372036854775809" })
            Check("invalid raw signed count " + raw, Has(Preview(App(16, "0", "28800"), raw), ParameterPatchIssueCode.InvalidValue));
        foreach (var unit in new[] { "PackedSecondsAndMilliseconds", "PackedMinutesSecondsAndMilliseconds", "Unknown", "seconds" })
        { var d = App(); Shape(d).SetAttributeValue("Unit", unit); Check("unit cannot acquire dense codec " + unit, Has(Preview(d), ParameterPatchIssueCode.UnsupportedParameter)); }
        var mutations = new (string Name, Action<XDocument> Mutate, ParameterPatchIssueCode Code)[]
        {
            ("missing bound", d => Shape(d).Attribute("minInclusive")!.Remove(), ParameterPatchIssueCode.InvalidDeclaration),
            ("reversed range", d => Shape(d).SetAttributeValue("minInclusive", "32768"), ParameterPatchIssueCode.InvalidDeclaration),
            ("fractional declaration", d => Shape(d).SetAttributeValue("maxInclusive", "1.5"), ParameterPatchIssueCode.InvalidDeclaration),
            ("unknown hint", d => Shape(d).SetAttributeValue("UIHint", "DateTime"), ParameterPatchIssueCode.InvalidDeclaration),
            ("increment", d => Shape(d).SetAttributeValue("Increment", "1"), ParameterPatchIssueCode.InvalidDeclaration),
            ("display scale", d => Shape(d).SetAttributeValue("DisplayFactor", "1000"), ParameterPatchIssueCode.InvalidDeclaration),
            ("foreign shape", d => Shape(d).Name = XNamespace.Get("urn:foreign") + "TypeTime", ParameterPatchIssueCode.InvalidDeclaration),
            ("child payload", d => Shape(d).Add(new XElement(Ns + "Vendor")), ParameterPatchIssueCode.InvalidDeclaration),
            ("alias", d => RootStatic(d).Element(Ns + "ParameterRefs")!.Add(Ref("Alias", "P")), ParameterPatchIssueCode.AmbiguousOverlap),
            ("hidden unreferenced owner", d => { var p = Field("Other", "T", 0); p.SetAttributeValue("Access", "None"); RootStatic(d).Element(Ns + "Parameters")!.Add(p); }, ParameterPatchIssueCode.AmbiguousOverlap),
            ("selected Assign", d => d.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R"), new XAttribute("Value", "1"))), ParameterPatchIssueCode.UnsupportedDependency),
            ("selected Calculation", d => Calculation(d, true), ParameterPatchIssueCode.UnsupportedDependency),
            ("missing Data", d => d.Descendants(Ns + "Data").Single().Remove(), ParameterPatchIssueCode.UnknownInitialData),
            ("short Mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "AA==")), ParameterPatchIssueCode.InvalidInitialMask),
        };
        foreach (var mutation in mutations) { var d = App(); mutation.Mutate(d); Check("atomic " + mutation.Name, Has(Preview(d), mutation.Code)); }
        var unrelated = App(); Calculation(unrelated, false);
        Check("unrelated declared JS is not executed and does not gate dense subset", Preview(unrelated).IsAvailable);
        var module = Fixture(directory, App(modules: true)); var original = Knxproj.ToXml(module.Project).ToString();
        ParameterPatchPreview FromModule(params ParameterPatchValue[] values) => OfflineParameterPatch.Preview(module.Project, module.Device, values, ParameterPatchByteOrder.BigEndian);
        var moduleDense = FromModule(new ParameterPatchValue(ModuleTime, "-1"));
        Check("native root typed literal module Time has scope3", moduleDense.IsAvailable && moduleDense.Ranges.Single().StartAddress == 4112
            && moduleDense.Ranges.Single().AfterData.ToHexString() == "FFFF" && moduleDense.Binding is { CodecProfileVersion: 4, DenseTimeEncodingProfile: Profile, ModuleScopeProfileVersion: 3 });
        var number = FromModule(new ParameterPatchValue("Call1_MI-1_NR", "10")); var enumeration = FromModule(new ParameterPatchValue("Call1_MI-1_ER", "1"));
        var denseNumber = FromModule(new ParameterPatchValue("R", "10"), new("Call1_MI-1_NR", "10")); var denseEnum = FromModule(new ParameterPatchValue("R", "10"), new("Call1_MI-1_ER", "1"));
        Check("rootDense plus moduleNumber keeps exact scope1 witness", number.IsAvailable && denseNumber.IsAvailable
            && number.Binding is { CodecProfileVersion: 3, DenseTimeEncodingProfile: null, ModuleScopeProfileVersion: 1 }
            && denseNumber.Binding is { CodecProfileVersion: 4, ModuleScopeProfileVersion: 1 } && number.Binding.ModuleScopeWitnessHash == denseNumber.Binding.ModuleScopeWitnessHash);
        Check("rootDense plus moduleEnum keeps exact scope2 witness", enumeration.IsAvailable && denseEnum.IsAvailable
            && enumeration.Binding is { CodecProfileVersion: 3, DenseTimeEncodingProfile: null, ModuleScopeProfileVersion: 2 }
            && denseEnum.Binding is { CodecProfileVersion: 4, ModuleScopeProfileVersion: 2 } && enumeration.Binding.ModuleScopeWitnessHash == denseEnum.Binding.ModuleScopeWitnessHash);
        var forward = FromModule(new ParameterPatchValue(ModuleTime, "10"), new("Call1_MI-1_NR", "10"), new("Call1_MI-1_ER", "1"));
        var reverse = FromModule(new ParameterPatchValue("Call1_MI-1_ER", "1"), new("Call1_MI-1_NR", "10"), new(ModuleTime, "10"));
        Check("moduleDense plus legacy module scalars is atomic and order independent", forward.IsAvailable && reverse.IsAvailable
            && forward.Binding!.ModuleScopeProfileVersion == 3 && forward.Binding.Fingerprint == reverse.Binding!.Fingerprint);
        Check("preview leaves source project data and null mask immutable", forward.IsAvailable && Knxproj.ToXml(module.Project).ToString() == original
            && module.Device.ParameterValues.Count == 0 && forward.Resources.Single().Mask is null && !forward.CompleteApplicationImage && !forward.DownloadEligible);
        Check("invalid moduleDense cannot publish preceding Number", Atomic(FromModule(new ParameterPatchValue("Call1_MI-1_NR", "10"), new(ModuleTime, "32768"))));
        module.Device.ParameterValues["Mode"] = "0";
        Check("branch retirement stales binding without module presence guessing", moduleDense.Binding is not null && !moduleDense.Binding.IsCurrent(module.Project, module.Device)
            && Has(FromModule(new ParameterPatchValue(ModuleTime, "1")), ParameterPatchIssueCode.UnknownParameter));
        var moduleMutations = new (string Name, Action<XDocument> Mutate)[]
        {
            ("P112 style calculation participation", d => Calculation(d, true, true)),
            ("alias", d => Definition(d).Element(Ns + "Static")!.Element(Ns + "ParameterRefs")!.Add(Ref("MD_Alias", "MD_TP"))),
            ("duplicate source ref", d => { var r = Definition(d).Descendants(Ns + "ParameterRef").First(); r.Parent!.Add(new XElement(r)); }),
            ("duplicate source parameter", d => { var p = Definition(d).Descendants(Ns + "Parameter").First(); p.Parent!.Add(new XElement(p)); }),
            ("module local type", d => { var t = RootStatic(d).Element(Ns + "ParameterTypes")!.Elements().First(); t.Remove(); Definition(d).Element(Ns + "Static")!.Add(new XElement(Ns + "ParameterTypes", t)); }),
            ("allocator", d => d.Descendants(Ns + "NumericArg").Single().SetAttributeValue("AllocatorRefId", "Missing")),
            ("Repeat ancestry", d => { var c = d.Descendants(Ns + "Module").Single(); var p = c.Parent!; c.Remove(); p.Add(new XElement(Ns + "Repeat", new XAttribute("Id", "RP"), new XAttribute("Count", "1"), c)); }),
            ("assigned controller", d => d.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Dynamic")!.AddFirst(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Mode"), new XAttribute("Value", "1")))),
        };
        foreach (var mutation in moduleMutations) { var d = App(modules: true); mutation.Mutate(d); Check("module atomic " + mutation.Name, Atomic(Preview(d, reference: ModuleTime))); }
        var modulePacked = App(24, "0", "691199", modules: true); Shape(modulePacked).SetAttributeValue("Unit", "PackedDaysHoursMinutesAndSeconds");
        Check("module packed24 stays outside module DenseTime admission", Has(Preview(modulePacked, reference: ModuleTime), ParameterPatchIssueCode.UnsupportedParameter));
        var shadow = App(modules: true); Definition(shadow).Parent!.Add(new XElement(Ns + "ModuleDef", new XAttribute("Id", "Unused"), new XAttribute("Name", "Unused"),
            new XElement(Ns + "Static", new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "N"),
                new XElement(Ns + "TypeTime", new XAttribute("SizeInBit", "8"), new XAttribute("Unit", "Hours"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "255")))))));
        Check("unused local DenseTime shadow cannot narrow original Number scope1", Preview(shadow, "10", reference: "Call1_MI-1_NR").Binding is { CodecProfileVersion: 3, ModuleScopeProfileVersion: 1 });
        var mixed = App(16, "0", "28800"); var mixedStat = RootStatic(mixed);
        mixedStat.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "F"), new XElement(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "1"))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "Packed"), new XElement(Ns + "TypeTime", new XAttribute("SizeInBit", "24"), new XAttribute("Unit", "PackedDaysHoursMinutesAndSeconds"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "691199"))));
        mixedStat.Element(Ns + "Parameters")!.Add(Field("FP", "F", 2), Field("PP", "Packed", 6)); mixedStat.Element(Ns + "ParameterRefs")!.Add(Ref("FR", "FP"), Ref("PR", "PP"));
        var mixedFixture = Fixture(directory, mixed);
        var floatDense = OfflineParameterPatch.Preview(mixedFixture.Project, mixedFixture.Device, [new("R", "10"), new("FR", "0.25")], ParameterPatchByteOrder.BigEndian);
        Check("ordinary raw time and Single retain separate conversion profiles", floatDense.IsAvailable && floatDense.Ranges.Single().AfterData.ToHexString() == "000A3E800000"
            && floatDense.Binding is { CodecProfileVersion: 4, DenseTimeEncodingProfile: Profile, NumericParseProfile: "ExactIntegerAndDotNet10XmlDoubleToSingle", PackedTimeEncodingProfile: null });
        var packedDense = OfflineParameterPatch.Preview(mixedFixture.Project, mixedFixture.Device, [new("R", "10"), new("PR", "90061")], ParameterPatchByteOrder.BigEndian);
        Check("Dense and fixedPacked retain both exact profiles", packedDense.IsAvailable && packedDense.Binding is { CodecProfileVersion: 4, DenseTimeEncodingProfile: Profile,
            PackedTimeEncodingProfile: "PackedDaysHoursMinutesAndSeconds24BigEndianOnly" });
        Check("mixed Packed retains existing BE only gate", Has(OfflineParameterPatch.Preview(mixedFixture.Project, mixedFixture.Device, [new("R", "10"), new("PR", "90061")], ParameterPatchByteOrder.LittleEndian), ParameterPatchIssueCode.UnsupportedLayout));
        return failures;
    }
}
