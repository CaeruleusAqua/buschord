using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Knx;

internal static class ResourcePatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/23";
    private static XElement Static(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
    private static XDocument App(bool relative = true, byte lsm = 4, uint basis = 10, byte[]? data = null, byte[]? mask = null)
    {
        data ??= Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var doc = XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code/>
              <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="65535"/></ParameterType></ParameterTypes>
              <Parameters><Parameter Id="P" ParameterType="T" Value="0"><Memory CodeSegment="RS" Offset="2" BitOffset="0"/></Parameter></Parameters>
              <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
            """);
        Static(doc).Element(Ns + "Code")!.Add(new XElement(Ns + (relative ? "RelativeSegment" : "AbsoluteSegment"), new XAttribute("Id", "RS"), new XAttribute("Size", data.Length),
            relative ? new XAttribute("LoadStateMachine", lsm) : null, new XAttribute(relative ? "Offset" : "Address", basis),
            new XElement(Ns + "Data", Convert.ToBase64String(data)), mask is null ? null : new XElement(Ns + "Mask", Convert.ToBase64String(mask))));
        return doc;
    }
    private static void Type(XDocument doc, XElement shape, string defaultValue = "0")
    {
        Static(doc).Element(Ns + "ParameterTypes")!.Elements().Single().ReplaceNodes(shape);
        Static(doc).Element(Ns + "Parameters")!.Elements().Single().SetAttributeValue("Value", defaultValue);
    }
    private static XElement Number(int bits = 16, bool signed = false) => new(Ns + "TypeNumber", new XAttribute("Type", signed ? "signedInt" : "unsignedInt"), new XAttribute("SizeInBit", bits),
        new XAttribute("minInclusive", signed ? -(1L << (bits - 1)) : 0), new XAttribute("maxInclusive", (1L << (bits - (signed ? 1 : 0))) - 1));
    private static XElement Enum(int bits = 16) => new(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", bits),
        (bits == 1 ? new[] { 0, 1 } : new[] { 0, 30, 300 }).Select(v => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + v), new XAttribute("Value", v))));
    private static XElement Time(string unit = "Seconds", int bits = 16) => new(Ns + "TypeTime", new XAttribute("Unit", unit), new XAttribute("SizeInBit", bits),
        new XAttribute("minInclusive", unit == "Seconds" ? "-32768" : "0"), new XAttribute("maxInclusive", unit == "Seconds" ? "32767" : "691199"));
    private static (Project Project, DeviceInstance Device, string Path) Load(string directory, XDocument app)
    {
        var project = new Project { Id = "P-resource", Name = "Resource fixture", Guid = "11111111-2222-3333-4444-555555555555" };
        var installation = new Installation { Id = "I-0" }; var area = new Area { Id = "A-1", Address = 1 }; var line = new Line { Id = "L-1", Address = 1 };
        var device = new DeviceInstance { Id = "D-1", ProductRefId = AppId, Address = new(1, 1, 1) };
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        var path = Path.Combine(directory, "resource-" + Guid.NewGuid().ToString("N") + ".knxproj");
        using (var file = File.Create(path)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("0.xml").Open()) Knxproj.ToXml(project).Save(output);
            using (var output = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) app.Save(output);
        }
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single(), path);
    }
    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string label, bool ok) { checks++; Console.WriteLine((ok ? "PASS" : "FAIL") + " Resource patch: " + label); if (!ok) failures++; }
        OfflineResourcePlan Preview(XDocument doc, string value = "4660", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        { var f = Load(directory, doc); return OfflineResourcePatch.Preview(f.Project, f.Device, [new("R", value)], order); }
        void Reject(string label, XDocument doc, ParameterPatchIssueCode? code = null)
        {
            var plan = Preview(doc);
            Check(label, !plan.IsAvailable && plan.Binding is null && plan.Fields.Count == 0 && plan.Ranges.Count == 0 && plan.Resources.Count == 0
                && plan.Issues.Count > 0 && (code is null || plan.Issues[0].Code == code));
        }
        var fixture = Load(directory, App(mask: Enumerable.Repeat((byte)0x80, 64).ToArray()));
        var source = File.ReadAllBytes(fixture.Path); var xml = Knxproj.ToXml(fixture.Project).ToString(); var undo = new UndoStack(); undo.Reset(fixture.Project);
        var plan = OfflineResourcePatch.Preview(fixture.Project, fixture.Device, [new("R", "4660")], ParameterPatchByteOrder.BigEndian);
        Check("root LSM resource is available", plan.IsAvailable);
        if (plan.IsAvailable)
        {
            var image = plan.Resources.Single(); var range = plan.Ranges.Single();
            Check("arena coordinates include the resource base", image.Arena == new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, 4) && image.BaseOffset == 10 && range.ByteOffset == 12 && plan.Fields.Single().ByteOffset == 12);
            Check("known BE word and nullable mask policy", range.BeforeData.ToHexString() == "0203" && range.AfterData.ToHexString() == "1234" && range.FieldMask.ToHexString() == "FFFF" && range.BeforeMask!.ToHexString() == "8080" && range.AfterMask!.ToHexString() == "FFFF");
            Check("all supplied bytes survive outside the subset", image.PayloadLength == 64 && image.AfterData[0] == 0 && image.AfterData[1] == 1 && image.AfterData[2] == 0x12 && image.AfterData[3] == 0x34 && image.AfterData.ToArray().Skip(4).SequenceEqual(image.BeforeData.ToArray().Skip(4)));
            Check("full masks preserve unrelated bytes", image.BeforeMask!.Length == 64 && image.AfterMask![2] == 255 && image.AfterMask[3] == 255 && image.AfterMask[4] == 0x80);
            var dataCopy = image.AfterData.ToArray(); dataCopy[2] = 0; var maskCopy = image.AfterMask!.ToArray(); maskCopy[2] = 0;
            Check("buffer getters cannot mutate owned payloads", image.AfterData[2] == 0x12 && image.AfterMask[2] == 255 && image.BeforeData[2] == 2);
            Check("independent profile and geometry hashes are current", plan.Binding!.ProfileVersion == 1 && plan.Binding.ResourceHash.Length == 64 && plan.Binding.FieldsHash.Length == 64 && plan.Binding.IsCurrent(fixture.Project, fixture.Device));
            Check("resource completeness stays separate from device readiness", !plan.CompleteApplicationImage && !plan.DownloadEligible && image.DeclaredSizeCoverage == ResourcePayloadCoverage.MatchesDeclaredSize
                && plan.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.CompleteParameterActivation) && plan.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.LoadProcedure));
        }
        Check("preview leaves archive, project and undo untouched", File.ReadAllBytes(fixture.Path).SequenceEqual(source) && Knxproj.ToXml(fixture.Project).ToString() == xml && undo.Steps == 0);
        Check("legacy API still refuses relative memory", !OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("R", "4660")], ParameterPatchByteOrder.BigEndian).IsAvailable);
        var le = Preview(App(), order: ParameterPatchByteOrder.LittleEndian);
        Check("LE shares the existing encoder", le.IsAvailable && le.Ranges.Single().AfterData.ToHexString() == "3412" && le.Resources.Single().AfterMask is null);

        foreach (var (label, shape, value, expected, bits, bit, defaults) in new (string, XElement, string, string, int, int, string)[]
        {
            ("signed cross-byte", Number(9, true), "-1", "BFFA", 9, 3, "0"),
            ("sparse enum", Enum(), "300", "012C", 16, 0, "0"),
            ("single", new XElement(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"), new XAttribute("minInclusive", "-1"), new XAttribute("maxInclusive", "1")), "0.25", "3E800000", 32, 0, "0"),
            ("ordinary time", Time(), "10", "000A", 16, 0, "0"),
            ("packed time", Time("PackedDaysHoursMinutesAndSeconds", 24), "90061", "010121", 24, 0, "0"),
        })
        {
            var bytes = new byte[64]; if (bit != 0) { bytes[2] = 0xA5; bytes[3] = 0x5A; }
            var doc = App(data: bytes); Type(doc, shape, defaults); Static(doc).Element(Ns + "Parameters")!.Elements().Single().Element(Ns + "Memory")!.SetAttributeValue("BitOffset", bit);
            var result = Preview(doc, value);
            Check(label + " uses the shared scalar profile", result.IsAvailable && result.Ranges.Single().AfterData.ToHexString() == expected && result.Fields.Single().BitLength == bits);
        }
        var packed = App(); Type(packed, Time("PackedDaysHoursMinutesAndSeconds", 24));
        var packedLe = Preview(packed, "90061", ParameterPatchByteOrder.LittleEndian);
        Check("packed LE remains atomically unsupported", !packedLe.IsAvailable && packedLe.Issues[0].Code == ParameterPatchIssueCode.UnsupportedLayout && packedLe.Resources.Count == 0);
        var combined = App(data: new byte[64], mask: Enumerable.Repeat((byte)0x80, 64).ToArray());
        var cs = Static(combined); cs.Element(Ns + "ParameterTypes")!.RemoveNodes(); cs.Element(Ns + "Parameters")!.RemoveNodes(); cs.Element(Ns + "ParameterRefs")!.RemoveNodes();
        var selections = new List<ParameterPatchValue>();
        foreach (var (id, shape, offset, raw) in new (string, XElement, int, string)[]
        {
            ("N", Number(), 0, "4660"), ("E", Enum(), 2, "300"),
            ("F", new XElement(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"), new XAttribute("minInclusive", "-1"), new XAttribute("maxInclusive", "1")), 4, "0.25"),
            ("D", Time(), 8, "10"), ("K", Time("PackedDaysHoursMinutesAndSeconds", 24), 10, "90061"),
        })
        {
            cs.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", id + "T"), shape));
            cs.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", id + "P"), new XAttribute("ParameterType", id + "T"), new XAttribute("Value", "0"), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "RS"), new XAttribute("Offset", offset), new XAttribute("BitOffset", 0))));
            cs.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id + "R"), new XAttribute("RefId", id + "P"))); selections.Add(new(id + "R", raw));
        }
        combined.Descendants(Ns + "Dynamic").Single().RemoveNodes(); var cf = Load(directory, combined);
        var all = OfflineResourcePatch.Preview(cf.Project, cf.Device, selections, ParameterPatchByteOrder.BigEndian);
        Check("all existing codecs compose in one LSM resource", all.IsAvailable && all.Ranges.Single().AfterData.ToHexString() == "1234012C3E800000000A010121" && all.Resources.Single().AfterData.ToArray().Skip(13).All(b => b == 0));
        Check("mixed codec binding records the actual profiles", all.IsAvailable && all.Binding!.CodecProfileVersion == 4 && all.Binding.NumericParseProfile == "ExactIntegerAndDotNet10XmlDoubleToSingle" && all.Binding.DenseTimeEncodingProfile == "SignedRawOrdinaryTime8To32" && all.Binding.PackedTimeEncodingProfile == "PackedDaysHoursMinutesAndSeconds24BigEndianOnly");
        Check("mixed codec full mask preserves untouched payload", all.IsAvailable && all.Resources.Single().AfterMask!.ToArray().Take(13).All(b => b == 255) && all.Resources.Single().AfterMask!.ToArray().Skip(13).All(b => b == 0x80));
        foreach (var lsm in new byte[] { 0, 255 })
        {
            var result = Preview(App(lsm: lsm));
            Check("LSM endpoint " + lsm, result.IsAvailable && result.Resources.Single().Arena.LoadStateMachine == lsm);
        }
        var shortDeclared = App(); Static(shortDeclared).Element(Ns + "Code")!.Elements().Single().SetAttributeValue("Size", "128");
        var shortPlan = Preview(shortDeclared);
        Check("declared size never pads source Data", shortPlan.IsAvailable && shortPlan.Resources.Single().PayloadLength == 64 && shortPlan.Resources.Single().AfterData.Length == 64
            && shortPlan.Resources.Single().DeclaredSizeCoverage == ResourcePayloadCoverage.ShorterThanDeclaredSize && shortPlan.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.PayloadShorterThanDeclaredSize && r.ResourceId == "RS"));
        var noSize = App(); Static(noSize).Element(Ns + "Code")!.Elements().Single().Attribute("Size")!.Remove(); var noSizePlan = Preview(noSize);
        Check("unknown size is a diagnostic, not guessed capacity", noSizePlan.IsAvailable && noSizePlan.Resources.Single().DeclaredSizeCoverage == ResourcePayloadCoverage.Unknown && noSizePlan.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.UnknownDeclaredSize));
        var secure = App(); secure.Descendants(Ns + "ApplicationProgram").Single().SetAttributeValue("IsSecureEnabled", "true");
        Static(secure).Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlLoadImageProp", new XAttribute("ObjIdx", 0), new XAttribute("PropId", 78)))));
        Static(secure).Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "Absent"), new XAttribute("LoadStateMachine", 6), new XAttribute("Offset", 0), new XAttribute("Size", 64)));
        var requirements = Preview(secure);
        Check("property, Secure and missing initial data stay explicit unresolved requirements", requirements.IsAvailable
            && requirements.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.SecureConfiguration)
            && requirements.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.PropertyInitialization)
            && requirements.UnresolvedRequirements.Any(r => r.Code == ResourcePatchRequirementCode.MissingInitialResources && r.ResourceId == "Absent") && requirements.Resources.Count == 1 && !requirements.CompleteApplicationImage);
        var numerous = App();
        for (var i = 0; i < 100; i++) Static(numerous).Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "Missing-" + i), new XAttribute("LoadStateMachine", 6), new XAttribute("Offset", 0)));
        var bounded = Preview(numerous);
        Check("unselected requirements remain a bounded diagnostic", bounded.IsAvailable && bounded.UnresolvedRequirements.Count == 64 && bounded.UnresolvedRequirements.Last().Code == ResourcePatchRequirementCode.AdditionalRequirementsOmitted);

        XDocument WithOther(byte lsm = 4, int offset = 4, bool unknownType = true)
        {
            var d = App(); var s = Static(d);
            if (lsm != 4) s.Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "Other"), new XAttribute("LoadStateMachine", lsm), new XAttribute("Offset", 10), new XAttribute("Size", 64), new XElement(Ns + "Data", Convert.ToBase64String(new byte[64]))));
            if (unknownType) s.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "Raw"), new XElement(Ns + "TypeRawData", new XAttribute("MaxSize", 8))));
            s.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "OtherP"), new XAttribute("ParameterType", unknownType ? "Raw" : "T"), new XAttribute("Value", unknownType ? "" : "0"), new XElement(Ns + "Memory", new XAttribute("CodeSegment", lsm == 4 ? "RS" : "Other"), new XAttribute("Offset", offset), new XAttribute("BitOffset", 0))));
            s.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "OtherR"), new XAttribute("RefId", "OtherP")));
            return d;
        }
        Check("known later start proves unknown width disjoint", Preview(WithOther()).IsAvailable);
        Reject("unknown width before the selected end stays ambiguous", WithOther(offset: 1), ParameterPatchIssueCode.AmbiguousOverlap);
        Check("different LSM owns an independent logical arena", Preview(WithOther(5, 2)).IsAvailable);
        var two = WithOther(5, 2, false); var twoFixture = Load(directory, two);
        var twoPlan = OfflineResourcePatch.Preview(twoFixture.Project, twoFixture.Device, [new("R", "4660"), new("OtherR", "43981")], ParameterPatchByteOrder.BigEndian);
        Check("two LSMs with identical offsets stage atomically", twoPlan.IsAvailable && twoPlan.Resources.Count == 2 && twoPlan.Ranges.Select(r => r.AfterData.ToHexString()).SequenceEqual(new[] { "1234", "ABCD" }) && twoPlan.Ranges.Select(r => r.Arena.LoadStateMachine).SequenceEqual(new byte?[] { 4, 5 }));
        var sameLsm = App(); Static(sameLsm).Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "Unused"), new XAttribute("LoadStateMachine", 4), new XAttribute("Offset", 11), new XAttribute("Size", 64)));
        Reject("unreferenced same-LSM segment blocks overlap", sameLsm, ParameterPatchIssueCode.AmbiguousOverlap);
        var afterEnd = App(); Static(afterEnd).Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "After"), new XAttribute("LoadStateMachine", 4), new XAttribute("Offset", 14)));
        Check("unknown resource extent is disjoint only after the field end", Preview(afterEnd).IsAvailable);
        var beforeEnd = new XDocument(afterEnd); Static(beforeEnd).Element(Ns + "Code")!.Elements().Last().SetAttributeValue("Offset", 13);
        Reject("unknown resource extent before field end stays ambiguous", beforeEnd, ParameterPatchIssueCode.AmbiguousOverlap);
        var mixed = WithOther(5, 2, false); var other = Static(mixed).Element(Ns + "Code")!.Elements().Last(); other.Name = Ns + "AbsoluteSegment"; other.Attribute("LoadStateMachine")!.Remove(); other.Attribute("Offset")!.Remove(); other.SetAttributeValue("Address", 4096);
        var mixedFixture = Load(directory, mixed); var mixedPlan = OfflineResourcePatch.Preview(mixedFixture.Project, mixedFixture.Device, [new("R", "4660"), new("OtherR", "43981")], ParameterPatchByteOrder.BigEndian);
        Check("mixed absolute/relative selection refuses without mapping", !mixedPlan.IsAvailable && mixedPlan.Issues[0].Code == ParameterPatchIssueCode.UnsupportedLayout && mixedPlan.Resources.Count == 0);
        foreach (var (label, mutate, code) in new (string, Action<XDocument>, ParameterPatchIssueCode?)[]
        {
            ("missing Data", d => Static(d).Element(Ns + "Code")!.Elements().Single().Element(Ns + "Data")!.Remove(), ParameterPatchIssueCode.UnknownInitialData),
            ("short Mask", d => Static(d).Element(Ns + "Code")!.Elements().Single().Add(new XElement(Ns + "Mask", "AA==")), ParameterPatchIssueCode.InvalidInitialMask),
            ("malformed Mask", d => Static(d).Element(Ns + "Code")!.Elements().Single().Add(new XElement(Ns + "Mask", "bad!")), ParameterPatchIssueCode.InvalidInitialMask),
            ("alias", d => Static(d).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R2"), new XAttribute("RefId", "P"))), ParameterPatchIssueCode.AmbiguousOverlap),
            ("read access", d => Static(d).Element(Ns + "Parameters")!.Elements().Single().SetAttributeValue("Access", "Read"), ParameterPatchIssueCode.UnsupportedParameter),
            ("BaseValue", d => Static(d).Element(Ns + "Parameters")!.Elements().Single().SetAttributeValue("BaseValue", "Unknown"), ParameterPatchIssueCode.UnsupportedParameter),
            ("duplicate segment", d => Static(d).Element(Ns + "Code")!.Add(new XElement(Static(d).Element(Ns + "Code")!.Elements().Single())), null),
            ("unknown relative resource semantics", d => Static(d).Element(Ns + "Code")!.Elements().Single().SetAttributeValue("UnprovenMapping", 1), ParameterPatchIssueCode.InvalidDeclaration),
            ("unresolved base", d => Static(d).Element(Ns + "Parameters")!.Elements().Single().Element(Ns + "Memory")!.SetAttributeValue("BaseOffset", "Unknown"), null),
            ("DCA", d => Static(d).Add(new XElement(Ns + "Extension", new XElement(Ns + "DCA"))), ParameterPatchIssueCode.UnsupportedApplication),
        }) { var d = App(); mutate(d); Reject(label + " keeps all outputs empty", d, code); }
        var absent = Load(directory, App()); var unknown = OfflineResourcePatch.Preview(absent.Project, absent.Device, [new("Unknown", "1")], ParameterPatchByteOrder.BigEndian);
        Check("unknown selection has no partial buffers", !unknown.IsAvailable && unknown.Issues[0].Code == ParameterPatchIssueCode.UnknownParameter && unknown.Resources.Count == 0);
        var atom = OfflineResourcePatch.Preview(absent.Project, absent.Device, [new("R", "1"), new("Unknown", "1")], ParameterPatchByteOrder.BigEndian);
        Check("late selected failure does not publish earlier fields", !atom.IsAvailable && atom.Fields.Count == 0 && atom.Ranges.Count == 0 && atom.Binding is null);
        var absolute = Load(directory, App(false, basis: 4096));
        var old = JsonSerializer.Serialize(OfflineParameterPatch.Preview(absolute.Project, absolute.Device, [new("R", "4660")], ParameterPatchByteOrder.BigEndian));
        var modern = OfflineResourcePatch.Preview(absolute.Project, absolute.Device, [new("R", "4660")], ParameterPatchByteOrder.BigEndian);
        Check("new absolute resource retains typed arena and word", modern.IsAvailable && modern.Resources.Single().Arena.Kind == ResourcePatchArenaKind.AbsoluteMemory && modern.Ranges.Single().ByteOffset == 4098 && modern.Ranges.Single().AfterData.ToHexString() == "1234");
        Check("calling new API does not change legacy complete output", old == JsonSerializer.Serialize(OfflineParameterPatch.Preview(absolute.Project, absolute.Device, [new("R", "4660")], ParameterPatchByteOrder.BigEndian)));
        var moduleDoc = App(false, basis: 4096); var ms = Static(moduleDoc); var application = moduleDoc.Descendants(Ns + "ApplicationProgram").Single();
        ms.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "ModeT"), Number(8)));
        ms.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "ModeP"), new XAttribute("ParameterType", "ModeT"), new XAttribute("Value", 1)));
        ms.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Mode"), new XAttribute("RefId", "ModeP")));
        application.Add(new XElement(Ns + "ModuleDefs", new XElement(Ns + "ModuleDef", new XAttribute("Id", "MD"),
            new XElement(Ns + "Arguments", new XElement(Ns + "Argument", new XAttribute("Id", "MD_A"), new XAttribute("Allocates", 4))),
            new XElement(Ns + "Static", new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "MD_P"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 0), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "RS"), new XAttribute("Offset", 0), new XAttribute("BitOffset", 0), new XAttribute("BaseOffset", "MD_A")))),
                new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "MD_R"), new XAttribute("RefId", "MD_P")))),
            new XElement(Ns + "Dynamic", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_R"))))));
        application.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "Mode"), new XElement(Ns + "when", new XAttribute("test", 1),
            new XElement(Ns + "Module", new XAttribute("Id", "Call1"), new XAttribute("RefId", "MD"), new XElement(Ns + "NumericArg", new XAttribute("RefId", "MD_A"), new XAttribute("Value", 8)))), new XElement(Ns + "when", new XAttribute("test", 0))));
        var mf = Load(directory, moduleDoc); const string moduleRef = "Call1_MI-1_R";
        var oldModule = OfflineParameterPatch.Preview(mf.Project, mf.Device, [new(moduleRef, "10")], ParameterPatchByteOrder.BigEndian);
        var newModule = OfflineResourcePatch.Preview(mf.Project, mf.Device, [new(moduleRef, "10")], ParameterPatchByteOrder.BigEndian);
        Check("new absolute module request retains its exact original witness", newModule.IsAvailable && oldModule.IsAvailable && newModule.Binding!.ModuleScopeProfileVersion == 1 && newModule.Binding.ModuleScopeWitnessHash == oldModule.Binding!.ModuleScopeWitnessHash && newModule.Fields.Single().ByteOffset == 4104);
        var changedArg = new XDocument(moduleDoc); changedArg.Descendants(Ns + "NumericArg").Single().SetAttributeValue("Value", 12); var changedMf = Load(directory, changedArg);
        var changedModule = OfflineResourcePatch.Preview(changedMf.Project, changedMf.Device, [new(moduleRef, "10")], ParameterPatchByteOrder.BigEndian);
        Check("literal argument changes bind geometry and witness independently", changedModule.IsAvailable && newModule.IsAvailable && changedModule.Fields.Single().ByteOffset == 4108 && changedModule.Binding!.FieldsHash != newModule.Binding!.FieldsHash && changedModule.Binding.ModuleScopeWitnessHash != newModule.Binding.ModuleScopeWitnessHash);
        mf.Device.ParameterValues["Mode"] = "0";
        Check("retired branch makes the source binding stale", newModule.IsAvailable && !newModule.Binding!.IsCurrent(mf.Project, mf.Device));
        var moduleRelative = new XDocument(moduleDoc); var moduleSegment = Static(moduleRelative).Element(Ns + "Code")!.Elements().Single(); moduleSegment.Name = Ns + "RelativeSegment"; moduleSegment.Attribute("Address")!.Remove(); moduleSegment.SetAttributeValue("Offset", 0); moduleSegment.SetAttributeValue("LoadStateMachine", 4);
        var mr = Load(directory, moduleRelative); var noRelativeModule = OfflineResourcePatch.Preview(mr.Project, mr.Device, [new(moduleRef, "10")], ParameterPatchByteOrder.BigEndian);
        Check("module relative admission remains outside this profile", !noRelativeModule.IsAvailable && noRelativeModule.Issues[0].Code == ParameterPatchIssueCode.UnsupportedParameter && noRelativeModule.Resources.Count == 0);
        var dependency = WithOther(5, 2, false); dependency.Descendants(Ns + "Dynamic").Single().Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R"), new XAttribute("Value", 1)));
        Reject("selected Assign refuses without executing anything", dependency, ParameterPatchIssueCode.UnsupportedDependency);
        var calculation = WithOther(5, 2, false); Static(calculation).Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "y=x;"), new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R"), new XAttribute("AliasName", "x"))), new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "OtherR"), new XAttribute("AliasName", "y"))))));
        Reject("selected calculation refuses without a runtime", calculation, ParameterPatchIssueCode.UnsupportedDependency);
        var tooLarge = App(data: new byte[1_048_577]); Reject("single-resource byte budget remains enforced", tooLarge, ParameterPatchIssueCode.ResourceLimitExceeded);
        foreach (var (count, length, withMask) in new[] { (17, 64, false), (3, 1_048_576, true) })
        {
            var d = App(); var stat = Static(d); stat.Element(Ns + "Code")!.RemoveNodes(); stat.Element(Ns + "Parameters")!.RemoveNodes(); stat.Element(Ns + "ParameterRefs")!.RemoveNodes(); d.Descendants(Ns + "Dynamic").Single().RemoveNodes();
            var values = new List<ParameterPatchValue>(); var data = Convert.ToBase64String(new byte[length]);
            for (var i = 0; i < count; i++)
            {
                stat.Element(Ns + "Code")!.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "S" + i), new XAttribute("LoadStateMachine", i), new XAttribute("Offset", 0), new XAttribute("Size", length), new XElement(Ns + "Data", data), withMask ? new XElement(Ns + "Mask", data) : null));
                stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P" + i), new XAttribute("ParameterType", "T"), new XAttribute("Value", 0), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "S" + i), new XAttribute("Offset", 0), new XAttribute("BitOffset", 0))));
                stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R" + i), new XAttribute("RefId", "P" + i))); values.Add(new("R" + i, "1"));
            }
            var f = Load(directory, d); var result = OfflineResourcePatch.Preview(f.Project, f.Device, values, ParameterPatchByteOrder.BigEndian);
            Check(withMask ? "aggregate Data+Mask budget precedes decode" : "seventeen selected resources exceed the plan budget", !result.IsAvailable && result.Issues[0].Code == ParameterPatchIssueCode.ResourceLimitExceeded && result.Fields.Count == 0 && result.Ranges.Count == 0 && result.Resources.Count == 0 && result.Binding is null);
        }
        var noRefs = OfflineResourcePatch.Preview(absent.Project, absent.Device, [], ParameterPatchByteOrder.BigEndian);
        Check("empty selection never decodes or publishes resource bytes", !noRefs.IsAvailable && noRefs.Issues[0].Code == ParameterPatchIssueCode.InvalidRequest && noRefs.Resources.Count == 0);
        if (plan.IsAvailable)
        {
            var binding = plan.Binding!; fixture.Device.ParameterValues["R"] = "2";
            Check("saved-value edits stale the new binding", !binding.IsCurrent(fixture.Project, fixture.Device)); fixture.Device.ParameterValues.Clear();
            Check("restored original state revalidates the binding", binding.IsCurrent(fixture.Project, fixture.Device));
            var savedProduct = fixture.Device.ProductRefId; fixture.Device.ProductRefId = "M-0001_A-other";
            Check("program changes stale before source lookup", !binding.IsCurrent(fixture.Project, fixture.Device)); fixture.Device.ProductRefId = savedProduct;
            Check("another project with matching IDs is not the same source", !binding.IsCurrent(Knxproj.Load(fixture.Path), fixture.Device));
            fixture.Device.ParameterValues["R"] = "1"; undo.Push(fixture.Project); var restored = undo.Undo()!;
            Check("Undo produces a distinct bound device identity", !binding.IsCurrent(restored, ProjectQuery.DeviceList(restored).Single()));
            var redone = undo.Redo()!;
            Check("Redo does not revive authority for a replaced live instance", !binding.IsCurrent(redone, ProjectQuery.DeviceList(redone).Single()));
        }
        Console.WriteLine($"Resource patch: {checks - failures}/{checks} passed");
        return failures;
    }
}
