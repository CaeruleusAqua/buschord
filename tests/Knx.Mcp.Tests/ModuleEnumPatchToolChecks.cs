using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class ModuleEnumPatchToolChecks
{
    private const string App = "M-FFE6_A-0001-01-0000", Def = App + "_MD-1", Call = Def + "_M-1";
    private const string EnumRef = Call + "_MI-1_P-1_R-1", NumberRef = Call + "_MI-1_P-2_R-2";

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-module-enum-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 80000;
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
                values ??= [new(EnumRef, "1")];
                var path = Fixture(directory, document); var hash = SHA256.HashData(File.ReadAllBytes(path)); await Tool("project_open", new { path });
                var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single();
                var xml = Knxproj.ToXml(project).ToString(); var valuesBefore = JsonSerializer.Serialize(device.ParameterValues);
                var history = (await Tool("project_info", new { })).ToJsonString(); var ordinary = (await Plan()).ToJsonString();
                var response = await Plan(values, order); var patch = response["offlineParameterPatch"]!;
                var binding = OfflineParameterPatch.Preview(project, device, values, order == "big_endian" ? ParameterPatchByteOrder.BigEndian : ParameterPatchByteOrder.LittleEndian).Binding;
                var unchanged = SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash) && Knxproj.ToXml(project).ToString() == xml
                    && JsonSerializer.Serialize(device.ParameterValues) == valuesBefore && (await Tool("project_info", new { })).ToJsonString() == history
                    && (await Plan()).ToJsonString() == ordinary && response["plan"]!.ToJsonString() == JsonNode.Parse(ordinary)!["plan"]!.ToJsonString()
                    && response["plan"]?["program"] is null && response["plan"]?["ready"]?.GetValue<bool>() == false
                    && response["plan"]?["imageBytes"]?.GetValue<int>() == 0 && response["plan"]?["chunks"]?.AsArray().Count == 0
                    && response["writesEnabled"]?.GetValue<bool>() == false && server.Tools.Count == McpToolContract.ExpectedToolCount
                    && (string?)patch["scope"] == "ExplicitSubsetPreview" && patch["completeApplicationImage"]?.GetValue<bool>() == false && patch["downloadEligible"]?.GetValue<bool>() == false;
                return (patch, unchanged, binding, path);
            }
            static bool Available(JsonNode patch, int? scope = 2) => patch["available"]?.GetValue<bool>() == true
                && patch["binding"]?["codecProfileVersion"]?.GetValue<int>() == 3 && (string?)patch["binding"]?["numericParseProfile"] == "ExactInteger"
                && patch["binding"]?["packedTimeEncodingProfile"] is null
                && (scope is null ? patch["binding"]?["moduleScopeProfileVersion"] is null && patch["binding"]?["moduleScopeWitnessHash"] is null
                    : patch["binding"]?["moduleScopeProfileVersion"]?.GetValue<int>() == scope && (string?)patch["binding"]?["moduleScopeWitnessHash"] is { Length: 64 } witness && witness.All(Uri.IsHexDigit));
            static bool Rejected(JsonNode patch, string? code = null) => patch["available"]?.GetValue<bool>() == false && patch["binding"] is null
                && patch["ranges"]?.AsArray().Count == 0 && patch["fields"]?.AsArray().Count == 0 && patch["issues"]?.AsArray().Count > 0
                && (code is null || patch["issues"]!.AsArray().Any(i => (string?)i?["code"] == code));

            var bit = await Preview(Application()); var bitRange = bit.Patch["ranges"]?[0];
            check("module Value-enum uses profile2 and preserves unrelated MSB data and mask bits", bit.Unchanged && Available(bit.Patch)
                && (string?)bitRange?["startAddress"] == "0x00001004" && (string?)bitRange?["beforeData"] == "D3" && (string?)bitRange?["afterData"] == "F3"
                && (string?)bitRange?["fieldMask"] == "20" && (string?)bitRange?["beforeMask"] == "80" && (string?)bitRange?["afterMask"] == "A0"
                && bit.Binding is { ModuleScopeProfileVersion: 2 } firstBinding && firstBinding.IsCurrent(server.Project!, ProjectQuery.DeviceList(server.Project!).Single())
                && firstBinding.ModuleScopeWitnessHash == (string?)bit.Patch["binding"]?["moduleScopeWitnessHash"], bit.Patch.ToJsonString());
            var noMask = Application(); One(noMask, "Mask").Remove(); var nullable = await Preview(noMask);
            check("module enum nullable source masks stay absent", nullable.Unchanged && Available(nullable.Patch)
                && nullable.Patch["ranges"]?[0]?["beforeMask"] is null && nullable.Patch["ranges"]?[0]?["afterMask"] is null
                && (string?)nullable.Patch["ranges"]?[0]?["fieldMask"] == "20", nullable.Patch.ToJsonString());
            var sparseDocument = Sparse(); var sparse = await Preview(sparseDocument, [new(EnumRef, "300")]);
            check("module sparse Value-enum encodes its declared UInt16 value", sparse.Unchanged && Available(sparse.Patch)
                && (string?)sparse.Patch["ranges"]?[0]?["afterData"] == "012C" && (string?)sparse.Patch["ranges"]?[0]?["fieldMask"] == "FFFF"
                && (string?)sparse.Patch["ranges"]?[0]?["afterMask"] == "FFFF", sparse.Patch.ToJsonString());
            var sparseLe = await Preview(Sparse(), [new(EnumRef, "300")], "little_endian");
            check("aligned module enum reuses explicit LE encoding", sparseLe.Unchanged && Available(sparseLe.Patch)
                && (string?)sparseLe.Patch["ranges"]?[0]?["afterData"] == "2C01", sparseLe.Patch.ToJsonString());
            var uiOnly = Application(); ModuleDynamic(uiOnly).Add(new XElement(Ns(uiOnly) + "choose", new XAttribute("ParamRefId", Def + "_P-1_R-1"),
                new XElement(Ns(uiOnly) + "when", new XAttribute("test", "1"), new XElement(Ns(uiOnly) + "ParameterRefRef", new XAttribute("RefId", Def + "_P-2_R-2")))));
            var uiOnlyResult = await Preview(uiOnly);
            check("module-local UI-only choose does not become structural activation authority", uiOnlyResult.Unchanged && Available(uiOnlyResult.Patch), uiOnlyResult.Patch.ToJsonString());
            var reordered = Sparse(); Restriction(reordered).ReplaceNodes(Restriction(reordered).Elements().Reverse().Select(e => new XElement(e)).ToArray());
            foreach (var choice in Restriction(reordered).Elements()) { choice.SetAttributeValue("Text", "Translated label"); choice.SetAttributeValue("DisplayOrder", "-9"); }
            var reorderedResult = await Preview(reordered, [new(EnumRef, "300")]);
            check("module enum source order and labels do not become encoded selectors", reorderedResult.Unchanged && Available(reorderedResult.Patch)
                && reorderedResult.Patch["ranges"]!.ToJsonString() == sparse.Patch["ranges"]!.ToJsonString(), reorderedResult.Patch.ToJsonString());
            var mixed = await Preview(Application(), [new(NumberRef, "4660"), new(EnumRef, "1"), new("R-root-enum", "3")]);
            check("module Number plus enum and root enum combine under one profile2 atomic witness", mixed.Unchanged && Available(mixed.Patch)
                && mixed.Patch["fields"]?.AsArray().Count == 3 && mixed.Patch["ranges"]?.AsArray().Count == 3
                && (string?)mixed.Patch["ranges"]?[0]?["afterData"] == "03" && (string?)mixed.Patch["ranges"]?[1]?["afterData"] == "F3"
                && (string?)mixed.Patch["ranges"]?[2]?["afterData"] == "1234", mixed.Patch.ToJsonString());
            var number = await Preview(Application(), [new(NumberRef, "4660")]);
            check("Number-only module selection retains its existing profile1 contract", number.Unchanged && Available(number.Patch, 1)
                && (string?)number.Patch["ranges"]?[0]?["afterData"] == "1234", number.Patch.ToJsonString());
            var rootEnumNumber = await Preview(Application(), [new("R-root-enum", "3"), new(NumberRef, "4660")]);
            check("root enum plus module Number does not claim module-enum profile2", rootEnumNumber.Unchanged && Available(rootEnumNumber.Patch, 1), rootEnumNumber.Patch.ToJsonString());
            var rootOnly = await Preview(Application(), [new("R-root-enum", "3")]);
            check("root-only enum keeps nullable module profile and witness", rootOnly.Unchanged && Available(rootOnly.Patch, null), rootOnly.Patch.ToJsonString());
            var two = Application(); AddSecond(two, 12); var otherRef = Def + "_M-2_MI-1_P-1_R-1";
            var twoResult = await Preview(two, [new(EnumRef, "1"), new(otherRef, "1")]);
            check("two literal module enum instances use distinct physical bases", twoResult.Unchanged && Available(twoResult.Patch)
                && twoResult.Patch["ranges"]?.AsArray().Count == 2 && (string?)twoResult.Patch["ranges"]?[0]?["afterData"] == "F3"
                && (string?)twoResult.Patch["ranges"]?[1]?["startAddress"] == "0x0000100C" && (string?)twoResult.Patch["ranges"]?[1]?["afterData"] == "20", twoResult.Patch.ToJsonString());
            var max = ManyChoices(4096); var maximum = await Preview(max, [new(EnumRef, "4095")]);
            check("exactly4096 selected module enum members are accepted", maximum.Unchanged && Available(maximum.Patch)
                && (string?)maximum.Patch["ranges"]?[0]?["afterData"] == "0FFF", maximum.Patch.ToJsonString());
            var excessive = await Preview(ManyChoices(4097), [new(EnumRef, "4095")]);
            check("selected module enum4097 preflight refuses without partial bytes", excessive.Unchanged && Rejected(excessive.Patch, "resource_limit_exceeded"), excessive.Patch.ToJsonString());
            var numberUnrelated = await Preview(ManyChoices(4097), [new(NumberRef, "4660")]);
            check("Number-only selection is independent of an unselected4097-member module enum", numberUnrelated.Unchanged && Available(numberUnrelated.Patch, 1), numberUnrelated.Patch.ToJsonString());

            foreach (var raw in new[] { "2", "01", "normally closed", "E1" })
            {
                var invalid = await Preview(Application(), [new(EnumRef, raw)]);
                check("module enum exact membership refuses " + raw, invalid.Unchanged && Rejected(invalid.Patch, "invalid_value"), invalid.Patch.ToJsonString());
            }
            var negatives = new (string Name, Action<XDocument> Change)[]
            {
                ("BinaryValue base", d => Restriction(d).SetAttributeValue("Base", "BinaryValue")),
                ("BinaryValue even empty", d => Restriction(d).Elements().First().SetAttributeValue("BinaryValue", "")),
                ("wide33", d => Restriction(d).SetAttributeValue("SizeInBit", "33")),
                ("duplicate values", d => Restriction(d).Elements().Last().SetAttributeValue("Value", "0")),
                ("duplicate IDs", d => Restriction(d).Elements().Last().SetAttributeValue("Id", "E0")),
                ("noncanonical choice", d => Restriction(d).Elements().Last().SetAttributeValue("Value", "01")),
                ("unknown member attribute", d => Restriction(d).Elements().First().SetAttributeValue("Unproven", "1")),
                ("local type", d => { var type = Restriction(d).Parent!; type.Remove(); ModuleStatic(d).AddFirst(new XElement(Ns(d) + "ParameterTypes", type)); }),
                ("reference override", d => ModuleReference(d).SetAttributeValue("ParameterType", "N8")),
                ("original alias", d => ModuleStatic(d).Element(Ns(d) + "ParameterRefs")!.Add(new XElement(Ns(d) + "ParameterRef", new XAttribute("Id", Def + "_P-1_R-9"), new XAttribute("RefId", Def + "_P-1")))),
                ("other instance overlap", d => AddSecond(d, 4)),
                ("selected Assign", d => ModuleDynamic(d).Add(new XElement(Ns(d) + "Assign", new XAttribute("TargetParamRefRef", Def + "_P-1_R-1"), new XAttribute("Value", "1")))),
                ("controller Assign", d => Dynamic(d).AddFirst(new XElement(Ns(d) + "Assign", new XAttribute("TargetParamRefRef", "R-control"), new XAttribute("Value", "1")))),
                ("selected JS dependency", d => AddCalculation(d, Def + "_P-1_R-1", "R-root-number", true)),
                ("controller JS dependency", d => AddCalculation(d, "R-root-number", "R-control", false)),
                ("Repeat ancestry", d => { var occurrence = Occurrence(d); occurrence.ReplaceWith(new XElement(Ns(d) + "Repeat", new XAttribute("Id", "Repeat"), new XAttribute("Count", "1"), new XElement(occurrence))); }),
            };
            foreach (var negative in negatives)
            {
                var doc = Application(); negative.Change(doc); var result = await Preview(doc);
                check("module enum rejects " + negative.Name + " atomically", result.Unchanged && Rejected(result.Patch), result.Patch.ToJsonString());
            }
            var bitLe = await Preview(Application(), order: "little_endian");
            check("unaligned one-bit module enum cannot use LE layout", bitLe.Unchanged && Rejected(bitLe.Patch, "unsupported_layout"), bitLe.Patch.ToJsonString());
            foreach (var enumFirst in new[] { false, true })
            {
                ParameterPatchValue[] values = enumFirst ? [new(EnumRef, "2"), new(NumberRef, "4660")] : [new(NumberRef, "4660"), new(EnumRef, "2")];
                var partial = await Preview(Application(), values);
                check("mixed valid Number and invalid module enum publishes no partial fields enumFirst=" + enumFirst, partial.Unchanged && Rejected(partial.Patch), partial.Patch.ToJsonString());
            }
            var plain = Application(); ModuleParameter(plain).SetAttributeValue("Id", "PlainP"); ModuleReference(plain).SetAttributeValue("Id", "PlainR");
            ModuleReference(plain).SetAttributeValue("RefId", "PlainP"); ModuleDynamic(plain).Elements().First().SetAttributeValue("RefId", "PlainR");
            Memory(plain).Attribute("BaseOffset")!.Remove(); Memory(plain).SetAttributeValue("Offset", "4");
            var plainCall = Occurrence(plain); plainCall.ReplaceWith(new XElement(Ns(plain) + "Repeat", new XAttribute("Id", "PlainRepeat"), new XAttribute("Count", "1"), new XElement(plainCall)));
            var plainResult = await Preview(plain, [new("PlainR", "1")]);
            check("repeated unqualified PlainP PlainR enum cannot impersonate original root ownership", plainResult.Unchanged && Rejected(plainResult.Patch), plainResult.Patch.ToJsonString());

            var live = await Preview(Application()); var projectLive = server.Project!; var deviceLive = ProjectQuery.DeviceList(projectLive).Single();
            var witness = (string?)live.Patch["binding"]?["moduleScopeWitnessHash"]; var sourceHash = SHA256.HashData(File.ReadAllBytes(live.Path));
            deviceLive.ParameterValues["R-control"] = "0"; // Only an explicit owned-fixture control edit; preview never saves it.
            var changed = Knxproj.ToXml(projectLive).ToString(); var historyLive = (await Tool("project_info", new { })).ToJsonString(); var ordinaryLive = (await Plan()).ToJsonString();
            var absent = (await Plan([new(EnumRef, "1")]))["offlineParameterPatch"]!;
            check("saved branch retirement stales enum binding and refuses its old qualified row", live.Binding is not null && !live.Binding.IsCurrent(projectLive, deviceLive)
                && Rejected(absent, "unknown_parameter") && changed == Knxproj.ToXml(projectLive).ToString() && (await Tool("project_info", new { })).ToJsonString() == historyLive
                && (await Plan()).ToJsonString() == ordinaryLive && SHA256.HashData(File.ReadAllBytes(live.Path)).SequenceEqual(sourceHash), absent.ToJsonString());
            deviceLive.ParameterValues["R-control"] = "1"; var restored = (await Plan([new(EnumRef, "1")]))["offlineParameterPatch"]!;
            check("restored module enum requires a fresh binding with the same source witness", Available(restored)
                && (string?)restored["binding"]?["moduleScopeWitnessHash"] == witness && (string?)restored["binding"]?["fingerprint"] != (string?)live.Patch["binding"]?["fingerprint"], restored.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(directory, true); }
    }

    private static XNamespace Ns(XDocument d) => d.Root!.Name.Namespace;
    private static XElement One(XDocument d, string name) => d.Descendants().Single(e => e.Name.LocalName == name);
    private static XElement Program(XDocument d) => One(d, "ApplicationProgram");
    private static XElement RootStatic(XDocument d) => Program(d).Element(Ns(d) + "Static")!;
    private static XElement Dynamic(XDocument d) => Program(d).Element(Ns(d) + "Dynamic")!;
    private static XElement Definition(XDocument d) => d.Descendants(Ns(d) + "ModuleDef").Single();
    private static XElement ModuleStatic(XDocument d) => Definition(d).Element(Ns(d) + "Static")!;
    private static XElement ModuleDynamic(XDocument d) => Definition(d).Element(Ns(d) + "Dynamic")!;
    private static XElement Occurrence(XDocument d) => d.Descendants(Ns(d) + "Module").Single(e => (string?)e.Attribute("Id") == Call);
    private static XElement ModuleParameter(XDocument d) => ModuleStatic(d).Element(Ns(d) + "Parameters")!.Elements().Single(e => (string?)e.Attribute("Id") == Def + "_P-1" || (string?)e.Attribute("Id") == "PlainP");
    private static XElement ModuleReference(XDocument d) => ModuleStatic(d).Element(Ns(d) + "ParameterRefs")!.Elements().Single(e => (string?)e.Attribute("Id") == Def + "_P-1_R-1" || (string?)e.Attribute("Id") == "PlainR");
    private static XElement Memory(XDocument d) => ModuleParameter(d).Element(Ns(d) + "Memory")!;
    private static XElement Restriction(XDocument d) => RootStatic(d).Element(Ns(d) + "ParameterTypes")!.Elements().Single(e => (string?)e.Attribute("Id") == "E").Elements().Single();
    private static XDocument Sparse()
    {
        var d = Application(); Restriction(d).SetAttributeValue("SizeInBit", "16"); Memory(d).SetAttributeValue("BitOffset", "0");
        Restriction(d).ReplaceNodes(new[] { "0", "30", "300" }.Select(v => new XElement(Ns(d) + "Enumeration", new XAttribute("Id", "E" + v), new XAttribute("Value", v), new XAttribute("Text", "Sparse")))); return d;
    }
    private static XDocument ManyChoices(int count)
    {
        var d = Sparse(); Restriction(d).ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns(d) + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i)))); return d;
    }
    private static void AddSecond(XDocument d, int basis)
    { var occurrence = new XElement(Occurrence(d)); occurrence.SetAttributeValue("Id", Def + "_M-2"); occurrence.Element(Ns(d) + "NumericArg")!.SetAttributeValue("Value", basis); Occurrence(d).AddAfterSelf(occurrence); }
    private static void AddCalculation(XDocument d, string left, string right, bool local)
    {
        (local ? ModuleStatic(d) : RootStatic(d)).Add(new XElement(Ns(d) + "ParameterCalculations", new XElement(Ns(d) + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns(d) + "LParameters", new XElement(Ns(d) + "ParameterRefRef", new XAttribute("RefId", left), new XAttribute("AliasName", "x"))),
            new XElement(Ns(d) + "RParameters", new XElement(Ns(d) + "ParameterRefRef", new XAttribute("RefId", right), new XAttribute("AliasName", "y"))), new XElement(Ns(d) + "LRTransformation", "y=x;"))));
    }
    private static XDocument Application() => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="16">
        <Data>{{Convert.ToBase64String([0xA5,0,0,0,0xD3,0x5A,0xC3,0x3C,0,0,0,0,0,0,0,0])}}</Data><Mask>{{Convert.ToBase64String([1,0,0,0,0x80,2,4,8,0,0,0,0,0,0,0,0])}}</Mask>
        </AbsoluteSegment></Code><ParameterTypes><ParameterType Id="E" Name="Declared choices"><TypeRestriction Base="Value" SizeInBit="1"><Enumeration Id="E0" Value="0"/><Enumeration Id="E1" Value="1"/></TypeRestriction></ParameterType>
        <ParameterType Id="N16" Name="Word"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="65535"/></ParameterType>
        <ParameterType Id="N8" Name="Byte"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType>
        <ParameterType Id="RE" Name="Root choices"><TypeRestriction Base="Value" SizeInBit="8"><Enumeration Id="RE0" Value="0"/><Enumeration Id="RE3" Value="3"/></TypeRestriction></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P-root-enum" ParameterType="RE" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
        <Parameter Id="P-root-number" ParameterType="N8" Value="0"><Memory CodeSegment="AS" Offset="2" BitOffset="0"/></Parameter><Parameter Id="P-control" ParameterType="N8" Value="1"/></Parameters>
        <ParameterRefs><ParameterRef Id="R-root-enum" RefId="P-root-enum"/><ParameterRef Id="R-root-number" RefId="P-root-number"/><ParameterRef Id="R-control" RefId="P-control"/></ParameterRefs></Static>
        <ModuleDefs><ModuleDef Id="{{Def}}"><Arguments><Argument Id="{{Def}}_A-1" Name="Base" Allocates="4"/></Arguments><Static>
        <Parameters><Parameter Id="{{Def}}_P-1" ParameterType="E" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="2" BaseOffset="{{Def}}_A-1"/></Parameter>
        <Parameter Id="{{Def}}_P-2" ParameterType="N16" Value="0"><Memory CodeSegment="AS" Offset="2" BitOffset="0" BaseOffset="{{Def}}_A-1"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="{{Def}}_P-1_R-1" RefId="{{Def}}_P-1"/><ParameterRef Id="{{Def}}_P-2_R-2" RefId="{{Def}}_P-2"/></ParameterRefs></Static>
        <Dynamic><ParameterRefRef RefId="{{Def}}_P-1_R-1"/><ParameterRefRef RefId="{{Def}}_P-2_R-2"/></Dynamic></ModuleDef></ModuleDefs>
        <Dynamic><ParameterRefRef RefId="R-root-enum"/><ParameterRefRef RefId="R-root-number"/><ParameterRefRef RefId="R-control"/><choose ParamRefId="R-control"><when test="1">
        <Module Id="{{Call}}" RefId="{{Def}}"><NumericArg RefId="{{Def}}_A-1" Value="4"/></Module></when><when test="0"/></choose></Dynamic></ApplicationProgram></KNX>
        """);
    private static string Fixture(string directory, XDocument document)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".knxproj"); var project = ProjectWizard.Create(new() { Name = "Synthetic module Value-enum subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) }); Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE6/" + App + ".xml").Open(); document.Save(output); return path;
    }
}
