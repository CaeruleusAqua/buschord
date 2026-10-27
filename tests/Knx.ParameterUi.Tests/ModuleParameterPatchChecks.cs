using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Knx;

internal static class ModuleParameterPatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000", Ref = "Call1_MI-1_R";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/23";
    private static XDocument App() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="64"><Data>{{Convert.ToBase64String(new byte[64])}}</Data></AbsoluteSegment></Code>
        <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="30000"/></ParameterType>
        <ParameterType Id="ModeT"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="1"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="ModeP" ParameterType="ModeT" Value="1"/><Parameter Id="RootP" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="Mode" RefId="ModeP"/><ParameterRef Id="RootR" RefId="RootP"/></ParameterRefs></Static>
        <ModuleDefs><ModuleDef Id="MD" Name="Direct"><Arguments><Argument Id="MD_A" Name="Base" Allocates="4"/></Arguments>
        <Static><Parameters><Parameter Id="MD_P" Name="Selected" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0" BaseOffset="MD_A"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="MD_R" RefId="MD_P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="MD_R"/></Dynamic></ModuleDef></ModuleDefs>
        <Dynamic><choose ParamRefId="Mode"><when test="1"><Module Id="Call1" RefId="MD"><NumericArg RefId="MD_A" Value="8"/></Module></when><when test="0"/></choose></Dynamic>
        </ApplicationProgram></KNX>
        """);
    private static XElement Module(XDocument d) => d.Descendants(Ns + "Module").Single();
    private static XElement Parameter(XDocument d) => d.Descendants(Ns + "Parameter").Single(e => (string?)e.Attribute("Id") == "MD_P");
    private static void Calculation(XDocument d, string left, string right)
    {
        var stat = d.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", right + "P"), new XAttribute("ParameterType", "ModeT"), new XAttribute("Value", "0")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", right), new XAttribute("RefId", right + "P")));
        stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"), new XElement(Ns + "LRTransformation", "y=x;"),
            new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", left), new XAttribute("AliasName", "x"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", right), new XAttribute("AliasName", "y"))))));
    }
    private static (Project Project, DeviceInstance Device, string Path) Fixture(string directory, XDocument app)
    {
        var path = Path.Combine(directory, "module-patch-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Verified direct module subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = AppId, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) app.Save(stream);
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single(), path);
    }
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "ModulePatch " + name); if (!ok) failures++; }
        ParameterPatchPreview Preview(XDocument app, string raw = "10", string reference = Ref, ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        { var f = Fixture(directory, app); return OfflineParameterPatch.Preview(f.Project, f.Device, [new(reference, raw)], order); }
        static bool Atomic(ParameterPatchPreview p) => !p.IsAvailable && p.Binding is null && p.Issues.Count > 0 && p.Fields.Count == 0 && p.Resources.Count == 0 && p.Ranges.Count == 0;
        var positive = Preview(App());
        Check("literal Number module origin produces exact bytes at base8", positive.IsAvailable && positive.Ranges.Single().StartAddress == 4104
            && positive.Ranges.Single().AfterData.ToHexString() == "000A" && positive.Ranges.Single().FieldMask.ToHexString() == "FFFF" && positive.Ranges.Single().AfterMask is null
            && positive.Binding is { CodecProfileVersion: 3, ModuleScopeProfileVersion: 1, ModuleScopeWitnessHash.Length: 64 });
        Check("Type absent is native Numeric default and Allocates alone is inert", positive.IsAvailable && positive.Binding!.NumericParseProfile == "ExactInteger");
        Check("ordinary aligned little endian reuses numeric encoding", Preview(App(), "4660", order: ParameterPatchByteOrder.LittleEndian).Ranges.Single().AfterData.ToHexString() == "3412");
        var signed = App(); var shape = signed.Descendants(Ns + "TypeNumber").First(); shape.SetAttributeValue("Type", "signedInt"); shape.SetAttributeValue("SizeInBit", "5"); shape.SetAttributeValue("minInclusive", "-16"); shape.SetAttributeValue("maxInclusive", "15");
        Parameter(signed).Element(Ns + "Memory")!.SetAttributeValue("BitOffset", "1");
        var packed = Preview(signed, "-3");
        Check("signed subbyte Number module retains existing MSB codec", packed.IsAvailable && packed.Ranges.Single().AfterData.ToHexString() == "74" && packed.Ranges.Single().FieldMask.ToHexString() == "7C");
        var two = App(); var second = new XElement(Module(two)); second.SetAttributeValue("Id", "Call2"); second.Element(Ns + "NumericArg")!.SetAttributeValue("Value", "16"); Module(two).Parent!.Add(second);
        var pair = Fixture(directory, two);
        var both = OfflineParameterPatch.Preview(pair.Project, pair.Device, [new(Ref, "10"), new("Call2_MI-1_R", "20")], ParameterPatchByteOrder.BigEndian);
        Check("independent flat calls retain distinct typed literal bases", both.IsAvailable && both.Ranges.Count == 2 && both.Ranges[0].StartAddress == 4104 && both.Ranges[1].StartAddress == 4112
            && both.Ranges[0].AfterData.ToHexString() == "000A" && both.Ranges[1].AfterData.ToHexString() == "0014");
        var row = Ksxml.Parse(App()).Instances.Single(p => p.Id == Ref); var copy = new ParameterInstance(row);
        var originProperty = typeof(ParameterInstance).GetProperty("ModuleOrigin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check("parameter copy preserves immutable internally produced origin", originProperty.GetValue(row) is { } origin && ReferenceEquals(origin, originProperty.GetValue(copy)));
        var f = Fixture(directory, App()); var before = Knxproj.ToXml(f.Project).ToString();
        var a = OfflineParameterPatch.Preview(f.Project, f.Device, [new(Ref, "10")], ParameterPatchByteOrder.BigEndian);
        var b = OfflineParameterPatch.Preview(f.Project, f.Device, [new(Ref, "10")], ParameterPatchByteOrder.BigEndian);
        Check("regenerated equivalent origins have stable witness and binding", a.IsAvailable && b.IsAvailable && a.Binding!.ModuleScopeWitnessHash == b.Binding!.ModuleScopeWitnessHash && a.Binding.Fingerprint == b.Binding.Fingerprint);
        Check("preview preserves project source values and original resource", a.IsAvailable && Knxproj.ToXml(f.Project).ToString() == before && f.Device.ParameterValues.Count == 0
            && a.Resources.Single().Data.ToHexString() == new string('0', 128) && !a.CompleteApplicationImage && !a.DownloadEligible);
        var rootOnly = OfflineParameterPatch.Preview(f.Project, f.Device, [new("RootR", "10")], ParameterPatchByteOrder.BigEndian);
        Check("root-only preview acquires no module scope claim", rootOnly.IsAvailable && rootOnly.Binding is { ModuleScopeProfileVersion: null, ModuleScopeWitnessHash: null });
        var rootAndModule = OfflineParameterPatch.Preview(f.Project, f.Device, [new("RootR", "20"), new(Ref, "10")], ParameterPatchByteOrder.BigEndian);
        Check("root and module share one atomic preview", rootAndModule.IsAvailable && rootAndModule.Ranges.Count == 2);
        f.Device.ParameterValues["Mode"] = "0";
        Check("branch retirement stales binding and cannot guess a missing occurrence", !a.Binding!.IsCurrent(f.Project, f.Device) && Atomic(OfflineParameterPatch.Preview(f.Project, f.Device, [new(Ref, "10")], ParameterPatchByteOrder.BigEndian)));
        f.Device.ParameterValues["Mode"] = "1";
        Check("ordinary saved selector admits the same literal scope", OfflineParameterPatch.Preview(f.Project, f.Device, [new(Ref, "10")], ParameterPatchByteOrder.BigEndian).IsAvailable);
        var assign = App(); assign.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Dynamic")!.AddFirst(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Mode"), new XAttribute("Value", "1")));
        Check("assigned choose selector is rejected even when value remains1", Atomic(Preview(assign)));
        var calculated = App(); Calculation(calculated, "Mode", "Other");
        Check("unexecuted JS cannot authorize a default-looking choose selector", Atomic(Preview(calculated)));
        var aliasControl = App(); aliasControl.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "ModeAlias"), new XAttribute("RefId", "ModeP")));
        Calculation(aliasControl, "ModeAlias", "Other");
        Check("selector alias calculations cannot authorize module existence", Atomic(Preview(aliasControl)));
        var unrelated = App(); var stat = unrelated.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "UnrelatedP"), new XAttribute("ParameterType", "ModeT"), new XAttribute("Value", "0")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Unrelated"), new XAttribute("RefId", "UnrelatedP")));
        Calculation(unrelated, "Unrelated", "Other");
        Check("unrelated known JS is not a global ban on literal module fields", Preview(unrelated).IsAvailable);
        var selectedCalculation = App(); var selectedStatic = selectedCalculation.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!;
        selectedStatic.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "MD_Other"), new XAttribute("ParameterType", "ModeT"), new XAttribute("Value", "0")));
        selectedStatic.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "MD_OtherRef"), new XAttribute("RefId", "MD_Other")));
        selectedStatic.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"), new XElement(Ns + "LRTransformation", "y=x;"),
            new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_R"), new XAttribute("AliasName", "x"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_OtherRef"), new XAttribute("AliasName", "y"))))));
        Check("valid module-local calculation participation remains rejected", Atomic(Preview(selectedCalculation)));
        var hidden = App(); var moduleDynamic = hidden.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Dynamic")!;
        moduleDynamic.RemoveNodes(); moduleDynamic.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "Mode"),
            new XElement(Ns + "when", new XAttribute("test", "0"), new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_R")))));
        Check("explicit subset does not mistake hidden UI placement for inactive storage", Preview(hidden).IsAvailable);
        var differentBase = App(); Module(differentBase).Element(Ns + "NumericArg")!.SetAttributeValue("Value", "10");
        var shiftedBase = Preview(differentBase);
        Check("changed literal base binds different source facts and geometry", shiftedBase.IsAvailable && shiftedBase.Ranges.Single().StartAddress == 4106
            && shiftedBase.Binding!.ModuleScopeWitnessHash != positive.Binding!.ModuleScopeWitnessHash);
        var nested = App(); var outer = nested.Descendants(Ns + "ModuleDef").Single(); var child = new XElement(outer);
        child.SetAttributeValue("Id", "MD_Sub"); child.Element(Ns + "Arguments")!.Element(Ns + "Argument")!.SetAttributeValue("Id", "MD_Sub_A");
        var childParameter = child.Descendants(Ns + "Parameter").Single(); childParameter.SetAttributeValue("Id", "MD_Sub_P"); childParameter.SetAttributeValue("Name", "Nested");
        childParameter.Element(Ns + "Memory")!.SetAttributeValue("BaseOffset", "MD_Sub_A");
        child.Descendants(Ns + "ParameterRef").Single().SetAttributeValue("Id", "MD_Sub_R"); child.Descendants(Ns + "ParameterRef").Single().SetAttributeValue("RefId", "MD_Sub_P");
        child.Descendants(Ns + "ParameterRefRef").Single().SetAttributeValue("RefId", "MD_Sub_R");
        outer.Add(new XElement(Ns + "SubModuleDefs", child)); outer.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Module", new XAttribute("Id", "MD_ChildCall"), new XAttribute("RefId", "MD_Sub"),
            new XElement(Ns + "NumericArg", new XAttribute("RefId", "MD_Sub_A"), new XAttribute("Value", "24"))));
        Check("safe direct field is not globally banned by a disjoint child call", Preview(nested).IsAvailable);
        var nestedReference = Ksxml.Parse(nested).Instances.Single(p => p.Name == "Nested").Id;
        Check("selected nested ancestry remains outside direct module profile", Atomic(Preview(nested, reference: nestedReference)));
        foreach (var shapeXml in new[] { "<TypeRestriction Base='BinaryValue' SizeInBit='8'><Enumeration Id='E0' Value='0' BinaryValue='AA=='/></TypeRestriction>",
            "<TypeFloat Encoding='IEEE-754 Single' minInclusive='0' maxInclusive='10'/>",
            "<TypeTime SizeInBit='24' Unit='PackedDaysHoursMinutesAndSeconds' minInclusive='0' maxInclusive='691199'/>" })
        {
            var d = App(); var type = d.Descendants(Ns + "ParameterType").First(); type.RemoveNodes(); var s = XElement.Parse(shapeXml);
            foreach (var node in s.DescendantsAndSelf()) node.Name = Ns + node.Name.LocalName; type.Add(s);
            Check("module BinaryValue Float and Time remain outside admission " + s.Name.LocalName, Atomic(Preview(d, "0")));
        }
        var mutations = new (string Name, Action<XDocument> Mutate)[]
        {
            ("numeric missing explicit Value", d => Module(d).Element(Ns + "NumericArg")!.Attribute("Value")!.Remove()),
            ("allocator", d => Module(d).Element(Ns + "NumericArg")!.SetAttributeValue("AllocatorRefId", "Missing")),
            ("argument BaseValue", d => Module(d).Element(Ns + "NumericArg")!.SetAttributeValue("BaseValue", "Missing")),
            ("parameter BaseValue", d => Parameter(d).SetAttributeValue("BaseValue", "MD_A")),
            ("missing memory base", d => Parameter(d).Element(Ns + "Memory")!.SetAttributeValue("BaseOffset", "Missing")),
            ("overflowing base", d => Module(d).Element(Ns + "NumericArg")!.SetAttributeValue("Value", long.MaxValue.ToString())),
            ("negative base", d => Module(d).Element(Ns + "NumericArg")!.SetAttributeValue("Value", "-1")),
            ("duplicate refs", d => d.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "MD_Alias"), new XAttribute("RefId", "MD_P")))),
            ("ref type override", d => d.Descendants(Ns + "ParameterRef").Single(e => (string?)e.Attribute("Id") == "MD_R").SetAttributeValue("ParameterType", "T")),
            ("module-local type", d => { var t = d.Descendants(Ns + "ParameterType").First(); t.Remove(); d.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!.Add(new XElement(Ns + "ParameterTypes", t)); }),
            ("module-local Code", d => d.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!.Add(new XElement(Ns + "Code"))),
            ("duplicate Static cannot conceal local Code", d => d.Descendants(Ns + "ModuleDef").Single().Add(new XElement(Ns + "Static", new XElement(Ns + "Code")))),
            ("legacy controller type binding", d => { var p = d.Descendants(Ns + "Parameter").Single(e => (string?)e.Attribute("Id") == "ModeP"); p.Attribute("ParameterType")!.Remove(); p.SetAttributeValue("ParameterTypeId", "ModeT"); }),
            ("wrong original parameter container", d => { var p = Parameter(d); p.Remove(); d.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!.Add(new XElement(Ns + "Wrapper", p)); }),
            ("repeat ancestry", d => { var m = Module(d); var holder = m.Parent!; m.Remove(); holder.Add(new XElement(Ns + "Repeat", new XAttribute("Id", "RP"), new XAttribute("Count", "1"), m)); }),
            ("zero repeat", d => { var m = Module(d); var holder = m.Parent!; m.Remove(); holder.Add(new XElement(Ns + "Repeat", new XAttribute("Id", "RP"), new XAttribute("Count", "0"), m)); }),
            ("unreferenced hidden overlapping owner", d => { var p = new XElement(Parameter(d)); p.SetAttributeValue("Id", "MD_Other"); p.SetAttributeValue("Access", "None"); Parameter(d).Parent!.Add(p); }),
            ("untyped overlapping owner", d => { var p = new XElement(Parameter(d)); p.SetAttributeValue("Id", "MD_Other"); p.Attribute("ParameterType")!.Remove(); Parameter(d).Parent!.Add(p); }),
            ("missing Data", d => d.Descendants(Ns + "Data").Single().Remove()),
            ("short Mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "AA=="))),
            ("selected Assign", d => d.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "MD_R"), new XAttribute("Value", "10")))),
        };
        foreach (var mutation in mutations) { var d = App(); mutation.Mutate(d); Check("atomic refusal " + mutation.Name, Atomic(Preview(d))); }
        var overlapping = new XDocument(two); overlapping.Descendants(Ns + "Module").Last().Element(Ns + "NumericArg")!.SetAttributeValue("Value", "8");
        Check("another instance cannot own the selected physical bits", Atomic(Preview(overlapping)));
        var forged = App(); forged.Descendants(Ns + "ModuleDefs").Remove(); forged.Descendants(Ns + "Module").Remove();
        var rootStatic = forged.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
        rootStatic.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Call1_MI-1_P"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0"),
            new XAttribute("ModuleOrigin", "claimed"), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", "8"), new XAttribute("BitOffset", "0"))));
        rootStatic.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", Ref), new XAttribute("RefId", "Call1_MI-1_P")));
        Check("qualified id and XML origin claim do not prove source identity", Atomic(Preview(forged)));
        var plainRepeated = App(); var plain = Parameter(plainRepeated); plain.SetAttributeValue("Id", "PlainP"); plain.Element(Ns + "Memory")!.Attribute("BaseOffset")!.Remove();
        plain.Element(Ns + "Memory")!.SetAttributeValue("Offset", "8");
        var plainRef = plainRepeated.Descendants(Ns + "ParameterRef").Single(e => (string?)e.Attribute("Id") == "MD_R"); plainRef.SetAttributeValue("Id", "PlainR"); plainRef.SetAttributeValue("RefId", "PlainP");
        plainRepeated.Descendants(Ns + "ParameterRefRef").Single().SetAttributeValue("RefId", "PlainR");
        var plainCall = Module(plainRepeated); var plainParent = plainCall.Parent!; plainCall.Remove(); plainParent.Add(new XElement(Ns + "Repeat", new XAttribute("Id", "RP"), new XAttribute("Count", "1"), plainCall));
        Check("unprefixed repeated module cannot fall through as a root declaration", Atomic(Preview(plainRepeated, reference: "PlainR")));
        var invalid = OfflineParameterPatch.Preview(pair.Project, pair.Device, [new("RootR", "10"), new("missing", "1")], ParameterPatchByteOrder.BigEndian);
        Check("one bad selection publishes no root or module bytes", Atomic(invalid));
        return failures;
    }
}
