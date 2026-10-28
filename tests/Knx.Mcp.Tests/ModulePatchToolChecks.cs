using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class ModulePatchToolChecks
{
    private const string App = "M-FFE5_A-0001-01-0000", Def = App + "_MD-1", Call = Def + "_M-1";
    private const string Qualified = Call + "_MI-1_P-1_R-1";
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-module-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 70000;
            async Task<JsonNode> Tool(string name, object arguments)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } });
                var response = JsonNode.Parse((await server.HandleAsync(request))!)!;
                return response["result"]?["structuredContent"] ?? throw new InvalidOperationException(response.ToJsonString());
            }
            async Task<JsonNode> Plan(IReadOnlyList<ParameterPatchValue>? values = null, string order = "big_endian") => values is null
                ? await Tool("download_plan", new { device_address = "1.1.1" })
                : await Tool("download_plan", new { device_address = "1.1.1", byte_order = order, parameter_subset = values.Select(v => new { refId = v.RefId, rawValue = v.RawValue }).ToArray() });
            async Task<(JsonNode Patch, bool Unchanged, ParameterPatchSourceBinding? Binding, string Path)> Preview(XDocument document, IReadOnlyList<ParameterPatchValue>? values = null, string order = "big_endian")
            {
                values ??= [new(Qualified, "4660")];
                var path = Fixture(root, Guid.NewGuid().ToString("N"), document); var hash = SHA256.HashData(File.ReadAllBytes(path)); await Tool("project_open", new { path });
                var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single();
                var original = Knxproj.ToXml(project).ToString(); var history = (await Tool("project_info", new { })).ToJsonString(); var ordinary = (await Plan()).ToJsonString();
                var response = await Plan(values, order); var patch = response["offlineParameterPatch"]!;
                var binding = OfflineParameterPatch.Preview(project, device, values, order == "big_endian" ? ParameterPatchByteOrder.BigEndian : ParameterPatchByteOrder.LittleEndian).Binding;
                var unchanged = SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash) && Knxproj.ToXml(project).ToString() == original
                    && (await Tool("project_info", new { })).ToJsonString() == history && (await Plan()).ToJsonString() == ordinary
                    && response["plan"]!.ToJsonString() == JsonNode.Parse(ordinary)!["plan"]!.ToJsonString() && response["plan"]?["program"] is null
                    && response["plan"]?["ready"]?.GetValue<bool>() == false && response["plan"]?["imageBytes"]?.GetValue<int>() == 0
                    && response["plan"]?["chunks"]?.AsArray().Count == 0 && response["writesEnabled"]?.GetValue<bool>() == false && server.Tools.Count == McpToolContract.ExpectedToolCount
                    && (string?)patch["scope"] == "ExplicitSubsetPreview" && patch["completeApplicationImage"]?.GetValue<bool>() == false && patch["downloadEligible"]?.GetValue<bool>() == false;
                return (patch, unchanged, binding, path);
            }
            static bool Available(JsonNode patch, bool module = true) => patch["available"]?.GetValue<bool>() == true
                && patch["binding"]?["codecProfileVersion"]?.GetValue<int>() == 3 && (string?)patch["binding"]?["numericParseProfile"] == "ExactInteger"
                && patch["binding"]?["packedTimeEncodingProfile"] is null
                && (module ? patch["binding"]?["moduleScopeProfileVersion"]?.GetValue<int>() == 1 && (string?)patch["binding"]?["moduleScopeWitnessHash"] is { Length: 64 } witness && witness.All(Uri.IsHexDigit)
                    : patch["binding"]?["moduleScopeProfileVersion"] is null && patch["binding"]?["moduleScopeWitnessHash"] is null);
            static bool Rejected(JsonNode patch) => patch["available"]?.GetValue<bool>() == false && patch["binding"] is null
                && patch["ranges"]?.AsArray().Count == 0 && patch["fields"]?.AsArray().Count == 0 && patch["issues"]?.AsArray().Count > 0;
            var first = await Preview(Application()); var range = first.Patch["ranges"]?[0];
            check("literal module Number BE has a genuine source-bound scope witness", first.Unchanged && Available(first.Patch)
                && (string?)range?["afterData"] == "1234" && (string?)range?["fieldMask"] == "FFFF" && (string?)range?["afterMask"] == "FFFF"
                && (string?)range?["startAddress"] == "0x00001004" && first.Binding is { } firstBinding && firstBinding.IsCurrent(server.Project!, ProjectQuery.DeviceList(server.Project!).Single())
                && firstBinding.ModuleScopeWitnessHash == (string?)first.Patch["binding"]?["moduleScopeWitnessHash"], first.Patch.ToJsonString());
            var projectedKeys = new HashSet<string>
                { "applicationDataRevision", "applicationId", "codecProfileVersion", "definitionHash", "deviceId", "fingerprint", "moduleScopeProfileVersion", "moduleScopeWitnessHash",
                    "numericParseProfile", "packedTimeEncodingProfile", "projectGuid", "projectId", "savedValuesHash", "subsetHash" };
            check("MCP scope projection exposes hashes without raw source XML or dictionaries", first.Patch["binding"]?.AsObject().All(p => projectedKeys.Contains(p.Key)) == true
                && first.Patch.ToJsonString().Length < 10000, first.Patch.ToJsonString());
            var little = await Preview(Application(), order: "little_endian");
            check("literal aligned module Number uses the already supported LE codec", little.Unchanged && Available(little.Patch)
                && (string?)little.Patch["ranges"]?[0]?["afterData"] == "3412", little.Patch.ToJsonString());
            var moved = Application(); Argument(moved).SetAttributeValue("Value", "8"); var movedResult = await Preview(moved);
            check("changed literal base preserves identity but changes geometry and witness", movedResult.Unchanged && Available(movedResult.Patch)
                && (string?)movedResult.Patch["ranges"]?[0]?["startAddress"] == "0x00001008" && (string?)movedResult.Patch["ranges"]?[0]?["beforeData"] == "C33C"
                && (string?)movedResult.Patch["binding"]?["moduleScopeWitnessHash"] != (string?)first.Patch["binding"]?["moduleScopeWitnessHash"], movedResult.Patch.ToJsonString());
            var bits = Application(); Number(bits).SetAttributeValue("SizeInBit", "3"); Number(bits).SetAttributeValue("maxInclusive", "7"); Memory(bits).SetAttributeValue("BitOffset", "2");
            var data = Convert.FromBase64String(One(bits, "Data").Value); data[4] = 0xD3; One(bits, "Data").Value = Convert.ToBase64String(data);
            var mask = Convert.FromBase64String(One(bits, "Mask").Value); mask[4] = 0x80; One(bits, "Mask").Value = Convert.ToBase64String(mask);
            var subbyte = await Preview(bits, [new(Qualified, "5")]);
            check("literal module subbyte Number preserves unselected data and mask bits", subbyte.Unchanged && Available(subbyte.Patch)
                && (string?)subbyte.Patch["ranges"]?[0]?["afterData"] == "EB" && (string?)subbyte.Patch["ranges"]?[0]?["fieldMask"] == "38"
                && (string?)subbyte.Patch["ranges"]?[0]?["afterMask"] == "B8", subbyte.Patch.ToJsonString());
            var two = Application(); AddSecond(two, 8); var q2 = Def + "_M-2_MI-1_P-1_R-1";
            var twoResult = await Preview(two, [new(Qualified, "4660"), new(q2, "258")]);
            check("two direct instances keep exact distinct identities and literal bases", twoResult.Unchanged && Available(twoResult.Patch)
                && twoResult.Patch["ranges"]?.AsArray().Count == 2 && (string?)twoResult.Patch["ranges"]?[0]?["afterData"] == "1234"
                && (string?)twoResult.Patch["ranges"]?[1]?["afterData"] == "0102" && (string?)twoResult.Patch["ranges"]?[1]?["startAddress"] == "0x00001008", twoResult.Patch.ToJsonString());
            var twoOneSelected = await Preview(two);
            check("witness includes the whole actual instance set beyond the selected origin", twoOneSelected.Unchanged && Available(twoOneSelected.Patch)
                && (string?)twoOneSelected.Patch["binding"]?["moduleScopeWitnessHash"] != (string?)first.Patch["binding"]?["moduleScopeWitnessHash"], twoOneSelected.Patch.ToJsonString());
            var mixed = await Preview(Application(), [new("R-root", "15"), new(Qualified, "4660")]);
            check("root plus module share atomic resource masks without image authority", mixed.Unchanged && Available(mixed.Patch)
                && mixed.Patch["ranges"]?.AsArray().Count == 2 && (string?)mixed.Patch["ranges"]?[0]?["afterData"] == "0F"
                && (string?)mixed.Patch["ranges"]?[1]?["afterData"] == "1234", mixed.Patch.ToJsonString());
            var rootOnly = await Preview(Application(), [new("R-root", "15")]);
            check("unselected module declarations do not grant a module scope profile", rootOnly.Unchanged && Available(rootOnly.Patch, module: false), rootOnly.Patch.ToJsonString());
            var unrelatedJs = Application(); AddCalculation(unrelatedJs, "R-root", "R-aux"); var jsAllowed = await Preview(unrelatedJs);
            check("unrelated known JS is not confused with module controller authority", jsAllowed.Unchanged && Available(jsAllowed.Patch), jsAllowed.Patch.ToJsonString());

            var negativeCases = new (string Name, Action<XDocument> Change, string Raw)[]
            {
                ("forged qualified root without origin", ForgeRoot, "4660"),
                ("original parameter aliases", d => Definition(d).Element(Ns(d) + "Static")!.Element(Ns(d) + "ParameterRefs")!.Add(new XElement(Ns(d) + "ParameterRef", new XAttribute("Id", Def + "_P-1_R-2"), new XAttribute("RefId", Def + "_P-1"))), "4660"),
                ("other instance same physical bits", d => AddSecond(d, 4), "4660"),
                ("hidden other physical owner", d => AddOtherDefinition(d, false), "4660"),
                ("unknown other physical owner", d => AddOtherDefinition(d, true), "4660"),
                ("selected Assign", d => Definition(d).Element(Ns(d) + "Dynamic")!.Add(new XElement(Ns(d) + "Assign", new XAttribute("TargetParamRefRef", Def + "_P-1_R-1"), new XAttribute("Value", "1"))), "4660"),
                ("selected calculation", d => AddCalculation(d, Def + "_P-1_R-1", "R-root", local: true), "4660"),
                ("assigned ancestor selector despite unchanged value", d => Dynamic(d).AddFirst(new XElement(Ns(d) + "Assign", new XAttribute("TargetParamRefRef", "R-control"), new XAttribute("Value", "1"))), "4660"),
                ("JS-derived ancestor selector", d => AddCalculation(d, "R-root", "R-control"), "4660"),
                ("JS through selector alias", d => { RootStatic(d).Element(Ns(d) + "ParameterRefs")!.Add(new XElement(Ns(d) + "ParameterRef", new XAttribute("Id", "R-control-alias"), new XAttribute("RefId", "P-control"))); AddCalculation(d, "R-root", "R-control-alias"); }, "4660"),
                ("BaseValue-derived ancestor selector", d => d.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == "P-control").SetAttributeValue("BaseValue", "Missing"), "4660"),
                ("Repeat ancestry even with one instance", d => { var call = Occurrence(d); call.ReplaceWith(new XElement(Ns(d) + "Repeat", new XAttribute("Id", "RP"), new XAttribute("Count", "1"), new XElement(call))); }, "4660"),
                ("allocator-backed argument", d => { RootStatic(d).Add(new XElement(Ns(d) + "Allocators", new XElement(Ns(d) + "Allocator", new XAttribute("Id", "L1"), new XAttribute("Start", "4"), new XAttribute("maxInclusive", "16")))); Argument(d).SetAttributeValue("AllocatorRefId", "L1"); Argument(d).SetAttributeValue("Value", "0"); }, "4660"),
                ("argument BaseValue", d => Argument(d).SetAttributeValue("BaseValue", Def + "_A-1"), "4660"),
                ("parameter BaseValue hidden by flattening", d => ModuleParameter(d).SetAttributeValue("BaseValue", Def + "_A-1"), "4660"),
                ("missing explicit numeric value", d => { RootMemory(d).SetAttributeValue("Offset", "14"); Argument(d).Attribute("Value")!.Remove(); }, "4660"),
                ("missing memory base argument", d => Memory(d).SetAttributeValue("BaseOffset", "Missing"), "4660"),
                ("module-local resource", LocalResource, "4660"),
                ("parameter reference type override", d => ModuleReference(d).SetAttributeValue("ParameterType", "N8"), "1"),
                ("wrong original ModuleDefs container", d => { var definitions = d.Descendants().Single(e => e.Name.LocalName == "ModuleDefs"); definitions.Remove(); RootStatic(d).Add(definitions); }, "4660"),
                ("missing original Data", d => One(d, "Data").Remove(), "4660"),
                ("short original Mask", d => One(d, "Mask").Value = "AA==", "4660"),
                ("module BinaryValue enum remains unsupported", d => Number(d).ReplaceWith(new XElement(Ns(d) + "TypeRestriction", new XAttribute("Base", "BinaryValue"), new XAttribute("SizeInBit", "16"), new XElement(Ns(d) + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", "0"), new XAttribute("BinaryValue", "AAA=")), new XElement(Ns(d) + "Enumeration", new XAttribute("Id", "E1"), new XAttribute("Value", "1"), new XAttribute("BinaryValue", "AAE=")))), "1"),
                ("module Float not admitted by Number profile", d => Number(d).ReplaceWith(new XElement(Ns(d) + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "10"))), ".25"),
                ("module Time not admitted by Number profile", d => Number(d).ReplaceWith(new XElement(Ns(d) + "TypeTime", new XAttribute("SizeInBit", "24"), new XAttribute("Unit", "PackedDaysHoursMinutesAndSeconds"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "691199"))), "90061"),
            };
            foreach (var item in negativeCases)
            {
                var document = Application(); item.Change(document); var result = await Preview(document, [new(Qualified, item.Raw)]);
                check("module scope rejects " + item.Name + " atomically", result.Unchanged && Rejected(result.Patch), result.Patch.ToJsonString());
            }
            var plainRepeated = Application();
            ModuleParameter(plainRepeated).SetAttributeValue("Id", "PlainP"); ModuleReference(plainRepeated).SetAttributeValue("Id", "PlainR");
            ModuleReference(plainRepeated).SetAttributeValue("RefId", "PlainP");
            Definition(plainRepeated).Element(Ns(plainRepeated) + "Dynamic")!.Element(Ns(plainRepeated) + "ParameterRefRef")!.SetAttributeValue("RefId", "PlainR");
            Memory(plainRepeated).Attribute("BaseOffset")!.Remove(); Memory(plainRepeated).SetAttributeValue("Offset", "4");
            var plainCall = Occurrence(plainRepeated);
            plainCall.ReplaceWith(new XElement(Ns(plainRepeated) + "Repeat", new XAttribute("Id", "PlainRepeat"), new XAttribute("Count", "1"), new XElement(plainCall)));
            var plainResult = await Preview(plainRepeated, [new("PlainR", "4660")]);
            check("repeated PlainP PlainR without origin cannot fall through as original root declarations", plainResult.Unchanged && Rejected(plainResult.Patch), plainResult.Patch.ToJsonString());
            var nested = Application(); MakeNested(nested);
            // Test discovery reads an own synthetic snapshot; product admission never parses ids.
            var nestedRef = ApplicationSnapshot.Evaluate(nested, new Dictionary<string, string?>()).Instances.Single(i => i.Name == "Module number").Id;
            var nestedResult = await Preview(nested, [new(nestedRef, "4660")]);
            check("actual nested qualified identity remains unsupported", nestedResult.Unchanged && Rejected(nestedResult.Patch), nestedResult.Patch.ToJsonString());
            var mixedInvalid = Application(); AddSecond(mixedInvalid, 4); var partial = await Preview(mixedInvalid, [new("R-root", "15"), new(Qualified, "4660")]);
            check("valid root selection cannot publish partial bytes before module ownership failure", partial.Unchanged && Rejected(partial.Patch), partial.Patch.ToJsonString());

            var live = await Preview(Application()); var liveProject = server.Project!; var liveDevice = ProjectQuery.DeviceList(liveProject).Single();
            var originalWitness = (string?)live.Patch["binding"]?["moduleScopeWitnessHash"]; var sourceHash = SHA256.HashData(File.ReadAllBytes(live.Path));
            liveDevice.ParameterValues["R-control"] = "0"; // Explicit owned-fixture edit; no tool/undo authorization is inferred.
            var changed = Knxproj.ToXml(liveProject).ToString(); var info = (await Tool("project_info", new { })).ToJsonString(); var ordinaryPlan = (await Plan()).ToJsonString();
            var absent = (await Plan([new(Qualified, "4660")]))["offlineParameterPatch"]!;
            check("changed saved branch stales the prior binding and refuses its old qualified row", live.Binding is not null && !live.Binding.IsCurrent(liveProject, liveDevice)
                && Rejected(absent) && changed == Knxproj.ToXml(liveProject).ToString() && (await Tool("project_info", new { })).ToJsonString() == info
                && (await Plan()).ToJsonString() == ordinaryPlan && SHA256.HashData(File.ReadAllBytes(live.Path)).SequenceEqual(sourceHash), absent.ToJsonString());
            liveDevice.ParameterValues["R-control"] = "1";
            var restored = (await Plan([new(Qualified, "4660")]))["offlineParameterPatch"]!;
            check("ordinary explicit saved selector restores the same scope but requires a fresh full binding", Available(restored)
                && (string?)restored["binding"]?["moduleScopeWitnessHash"] == originalWitness && (string?)restored["binding"]?["fingerprint"] != (string?)live.Patch["binding"]?["fingerprint"], restored.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(root, true); }
    }
    private static XNamespace Ns(XDocument d) => d.Root!.Name.Namespace;
    private static XElement One(XDocument d, string name) => d.Descendants().Single(e => e.Name.LocalName == name);
    private static XElement Program(XDocument d) => One(d, "ApplicationProgram");
    private static XElement RootStatic(XDocument d) => Program(d).Element(Ns(d) + "Static")!;
    private static XElement Dynamic(XDocument d) => Program(d).Element(Ns(d) + "Dynamic")!;
    private static XElement Definition(XDocument d) => d.Descendants(Ns(d) + "ModuleDef").Single(e => (string?)e.Attribute("Id") == Def);
    private static XElement Occurrence(XDocument d) => d.Descendants(Ns(d) + "Module").Single(e => (string?)e.Attribute("Id") == Call);
    private static XElement Argument(XDocument d) => Occurrence(d).Element(Ns(d) + "NumericArg")!;
    private static XElement ModuleParameter(XDocument d) => Definition(d).Descendants(Ns(d) + "Parameter").Single();
    private static XElement ModuleReference(XDocument d) => Definition(d).Descendants(Ns(d) + "ParameterRef").Single();
    private static XElement Memory(XDocument d) => ModuleParameter(d).Element(Ns(d) + "Memory")!;
    private static XElement RootMemory(XDocument d) => RootStatic(d).Element(Ns(d) + "Parameters")!.Elements(Ns(d) + "Parameter").Single(e => (string?)e.Attribute("Id") == "P-root").Element(Ns(d) + "Memory")!;
    private static XElement Number(XDocument d) => RootStatic(d).Element(Ns(d) + "ParameterTypes")!.Elements(Ns(d) + "ParameterType").Single(e => (string?)e.Attribute("Id") == "N16").Element(Ns(d) + "TypeNumber")!;
    private static void AddSecond(XDocument d, int basis)
    { var call = new XElement(Occurrence(d)); call.SetAttributeValue("Id", Def + "_M-2"); call.Element(Ns(d) + "NumericArg")!.SetAttributeValue("Value", basis); Occurrence(d).AddAfterSelf(call); }
    private static void AddOtherDefinition(XDocument d, bool unknown)
    {
        var id = App + "_MD-2"; var def = new XElement(Definition(d));
        foreach (var node in def.DescendantsAndSelf()) foreach (var attr in node.Attributes().Where(a => a.Value.StartsWith(Def, StringComparison.Ordinal))) attr.Value = id + attr.Value[Def.Length..];
        def.Descendants(Ns(d) + "ParameterRef").Single().SetAttributeValue("Access", "None");
        if (unknown) { RootStatic(d).Element(Ns(d) + "ParameterTypes")!.Add(new XElement(Ns(d) + "ParameterType", new XAttribute("Id", "TXT"), new XAttribute("Name", "Text"), new XElement(Ns(d) + "TypeText", new XAttribute("SizeInBit", "8")))); def.Descendants(Ns(d) + "Parameter").Single().SetAttributeValue("ParameterType", "TXT"); }
        Definition(d).AddAfterSelf(def); Occurrence(d).AddAfterSelf(new XElement(Ns(d) + "Module", new XAttribute("Id", id + "_M-1"), new XAttribute("RefId", id), new XElement(Ns(d) + "NumericArg", new XAttribute("RefId", id + "_A-1"), new XAttribute("Value", "4"))));
    }
    private static void AddCalculation(XDocument d, string left, string right, bool local = false)
    {
        var stat = local ? Definition(d).Element(Ns(d) + "Static")! : RootStatic(d);
        stat.Add(new XElement(Ns(d) + "ParameterCalculations", new XElement(Ns(d) + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns(d) + "LParameters", new XElement(Ns(d) + "ParameterRefRef", new XAttribute("RefId", left), new XAttribute("AliasName", "x"))),
            new XElement(Ns(d) + "RParameters", new XElement(Ns(d) + "ParameterRefRef", new XAttribute("RefId", right), new XAttribute("AliasName", "y"))), new XElement(Ns(d) + "LRTransformation", "y=x;"))));
    }
    private static void ForgeRoot(XDocument d)
    {
        Dynamic(d).Elements(Ns(d) + "choose").Remove(); var p = new XElement(ModuleParameter(d)); p.SetAttributeValue("Id", Call + "_MI-1_P-1");
        p.Element(Ns(d) + "Memory")!.Attribute("BaseOffset")!.Remove(); p.Element(Ns(d) + "Memory")!.SetAttributeValue("Offset", "4");
        RootStatic(d).Element(Ns(d) + "Parameters")!.Add(p); RootStatic(d).Element(Ns(d) + "ParameterRefs")!.Add(new XElement(Ns(d) + "ParameterRef", new XAttribute("Id", Qualified), new XAttribute("RefId", Call + "_MI-1_P-1")));
    }
    private static void LocalResource(XDocument d)
    {
        var id = Def + "_AS-local"; Definition(d).Element(Ns(d) + "Static")!.AddFirst(new XElement(Ns(d) + "Code", new XElement(Ns(d) + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Address", "4096"), new XAttribute("Size", "16"), new XElement(Ns(d) + "Data", Convert.ToBase64String(new byte[16])))));
        Memory(d).SetAttributeValue("CodeSegment", id);
    }
    private static void MakeNested(XDocument d)
    {
        var outer = App + "_MD-0";
        Definition(d).AddBeforeSelf(new XElement(Ns(d) + "ModuleDef", new XAttribute("Id", outer), new XElement(Ns(d) + "Static"), new XElement(Ns(d) + "Dynamic",
            new XElement(Ns(d) + "Module", new XAttribute("Id", outer + "_SM-1_M-1"), new XAttribute("RefId", Def), new XElement(Ns(d) + "NumericArg", new XAttribute("RefId", Def + "_A-1"), new XAttribute("Value", "4"))))));
        Occurrence(d).ReplaceWith(new XElement(Ns(d) + "Module", new XAttribute("Id", outer + "_M-1"), new XAttribute("RefId", outer)));
    }
    private static XDocument Application() => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="16">
        <Data>{{Convert.ToBase64String([0xA5,0x5A,0x11,0x22,0xA5,0x5A,0,0,0xC3,0x3C,0,0,0,0,0,0])}}</Data><Mask>{{Convert.ToBase64String([0x40,0,0,0,1,128,0,0,32,64,0,0,0,0,0,0])}}</Mask>
        </AbsoluteSegment></Code><ParameterTypes><ParameterType Id="N16" Name="Word"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="65535"/></ParameterType>
        <ParameterType Id="N8" Name="Byte"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P-root" ParameterType="N8" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
        <Parameter Id="P-control" ParameterType="N8" Value="1"/><Parameter Id="P-aux" ParameterType="N8" Value="0"/></Parameters>
        <ParameterRefs><ParameterRef Id="R-root" RefId="P-root"/><ParameterRef Id="R-control" RefId="P-control"/><ParameterRef Id="R-aux" RefId="P-aux"/></ParameterRefs></Static>
        <ModuleDefs><ModuleDef Id="{{Def}}"><Arguments><Argument Id="{{Def}}_A-1" Name="MemoryBase" Allocates="2"/></Arguments><Static>
        <Parameters><Parameter Id="{{Def}}_P-1" Text="Module number" ParameterType="N16" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0" BaseOffset="{{Def}}_A-1"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="{{Def}}_P-1_R-1" RefId="{{Def}}_P-1"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="{{Def}}_P-1_R-1"/></Dynamic></ModuleDef></ModuleDefs>
        <Dynamic><ParameterRefRef RefId="R-root"/><ParameterRefRef RefId="R-control"/><ParameterRefRef RefId="R-aux"/><choose ParamRefId="R-control"><when test="1">
        <Module Id="{{Call}}" RefId="{{Def}}"><NumericArg RefId="{{Def}}_A-1" Value="4"/></Module></when><when test="0"/></choose></Dynamic></ApplicationProgram></KNX>
        """);
    private static string Fixture(string root, string name, XDocument document)
    {
        var path = Path.Combine(root, name + ".knxproj"); var project = ProjectWizard.Create(new() { Name = "Synthetic literal module subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) }); Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE5/" + App + ".xml").Open(); document.Save(output); return path;
    }
}
