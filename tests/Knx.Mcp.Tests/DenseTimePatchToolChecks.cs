using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class DenseTimePatchToolChecks
{
    private const string App = "M-FFE7_A-0001-01-0000", Def = App + "_MD-1", Call = Def + "_M-1";
    private const string ModuleTime = Call + "_MI-1_P-1_R-1", ModuleNumber = Call + "_MI-1_P-2_R-2", ModuleEnum = Call + "_MI-1_P-3_R-3";
    private const string DenseProfile = "SignedRawOrdinaryTime8To32";

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-dense-time-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 90000;
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
                values ??= [new("R-time", "10")]; var path = Fixture(directory, document); var hash = SHA256.HashData(File.ReadAllBytes(path)); await Tool("project_open", new { path });
                var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single(); var xml = Knxproj.ToXml(project).ToString(); var saved = JsonSerializer.Serialize(device.ParameterValues);
                var history = (await Tool("project_info", new { })).ToJsonString(); var ordinary = (await Plan()).ToJsonString(); var response = await Plan(values, order); var patch = response["offlineParameterPatch"]!;
                var binding = OfflineParameterPatch.Preview(project, device, values, order == "big_endian" ? ParameterPatchByteOrder.BigEndian : ParameterPatchByteOrder.LittleEndian).Binding;
                var unchanged = SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash) && Knxproj.ToXml(project).ToString() == xml && JsonSerializer.Serialize(device.ParameterValues) == saved
                    && (await Tool("project_info", new { })).ToJsonString() == history && (await Plan()).ToJsonString() == ordinary
                    && response["plan"]!.ToJsonString() == JsonNode.Parse(ordinary)!["plan"]!.ToJsonString() && response["plan"]?["program"] is null
                    && response["plan"]?["ready"]?.GetValue<bool>() == false && response["plan"]?["chunks"]?.AsArray().Count == 0 && response["plan"]?["imageBytes"]?.GetValue<int>() == 0
                    && response["writesEnabled"]?.GetValue<bool>() == false && server.Tools.Count == 55 && (string?)patch["scope"] == "ExplicitSubsetPreview"
                    && patch["completeApplicationImage"]?.GetValue<bool>() == false && patch["downloadEligible"]?.GetValue<bool>() == false;
                return (patch, unchanged, binding, path);
            }
            static bool Available(JsonNode patch, bool dense = true, int? scope = null, bool single = false, bool packed = false) => patch["available"]?.GetValue<bool>() == true
                && patch["binding"]?["codecProfileVersion"]?.GetValue<int>() == (dense ? 4 : 3)
                && (string?)patch["binding"]?["denseTimeEncodingProfile"] == (dense ? DenseProfile : null)
                && (string?)patch["binding"]?["numericParseProfile"] == (single ? "ExactIntegerAndDotNet10XmlDoubleToSingle" : "ExactInteger")
                && (string?)patch["binding"]?["packedTimeEncodingProfile"] == (packed ? "PackedDaysHoursMinutesAndSeconds24BigEndianOnly" : null)
                && (scope is null ? patch["binding"]?["moduleScopeProfileVersion"] is null && patch["binding"]?["moduleScopeWitnessHash"] is null
                    : patch["binding"]?["moduleScopeProfileVersion"]?.GetValue<int>() == scope && (string?)patch["binding"]?["moduleScopeWitnessHash"] is { Length: 64 } witness && witness.All(Uri.IsHexDigit));
            static bool Rejected(JsonNode patch, string? code = null) => patch["available"]?.GetValue<bool>() == false && patch["binding"] is null
                && patch["fields"]?.AsArray().Count == 0 && patch["ranges"]?.AsArray().Count == 0 && patch["issues"]?.AsArray().Count > 0
                && (code is null || patch["issues"]!.AsArray().Any(i => (string?)i?["code"] == code));

            foreach (var unit in new[] { "Hours", "Minutes", "Seconds", "HundredMilliseconds", "TenMilliseconds", "Milliseconds" })
            foreach (var order in new[] { "big_endian", "little_endian" })
            {
                var document = Application(); Time(document).SetAttributeValue("Unit", unit); var result = await Preview(document, [new("R-time", "1")], order);
                check("ordinary Time preserves raw1 for " + unit + "/" + order, result.Unchanged && Available(result.Patch)
                    && (string?)result.Patch["ranges"]?[0]?["afterData"] == (order == "big_endian" ? "0001" : "0100")
                    && (string?)result.Patch["ranges"]?[0]?["fieldMask"] == "FFFF" && (string?)result.Patch["fields"]?[0]?["rawValue"] == "1", result.Patch.ToJsonString());
            }
            foreach (var vector in new (int Bits, string Min, string Max, string Raw, string After, string Mask)[] {
                (Bits:8, Min:"-128", Max:"127", Raw:"-128", After:"80", Mask:"FF"), (8,"-128","127","127","7F","FF"),
                (11,"-1024","1023","-1024","8000","FFE0"), (11,"-1024","1023","1023","7FE0","FFE0"),
                (16,"-32768","32767","-32768","8000","FFFF"), (16,"-32768","32767","32767","7FFF","FFFF"),
                (24,"-8388608","8388607","-8388608","800000","FFFFFF"), (24,"-8388608","8388607","8388607","7FFFFF","FFFFFF"),
                (32,"-2147483648","2147483647","-2147483648","80000000","FFFFFFFF"), (32,"-2147483648","2147483647","2147483647","7FFFFFFF","FFFFFFFF") })
            {
                var document = Application(); Width(document, vector.Bits, vector.Min, vector.Max); var data = Convert.FromBase64String(One(document, "Data").Value); Array.Clear(data, 0, 4); One(document, "Data").Value = Convert.ToBase64String(data);
                var result = await Preview(document, [new("R-time", vector.Raw)]);
                check("signed Time" + vector.Bits + " endpoint " + vector.Raw, result.Unchanged && Available(result.Patch)
                    && (string?)result.Patch["ranges"]?[0]?["afterData"] == vector.After && (string?)result.Patch["ranges"]?[0]?["fieldMask"] == vector.Mask, result.Patch.ToJsonString());
            }
            foreach (var vector in new (int Bits, string Min, string Max, string After)[] { (8,"-128","127","FF"), (16,"-32768","32767","FFFF"), (24,"-8388608","8388607","FFFFFF"), (32,"-2147483648","2147483647","FFFFFFFF") })
            {
                var document = Application(); Width(document, vector.Bits, vector.Min, vector.Max); var result = await Preview(document, [new("R-time", "-1")], "little_endian");
                check("signed Time minus1 remains twos complement in LE width" + vector.Bits, result.Unchanged && Available(result.Patch)
                    && (string?)result.Patch["ranges"]?[0]?["afterData"] == vector.After, result.Patch.ToJsonString());
            }
            var negativeLe = await Preview(Application(), [new("R-time", "-32768")], "little_endian");
            check("signed Time LE reverses the complete negative word", negativeLe.Unchanged && Available(negativeLe.Patch)
                && (string?)negativeLe.Patch["ranges"]?[0]?["afterData"] == "0080", negativeLe.Patch.ToJsonString());
            var packedBits = Application(); Width(packedBits, 11, "-1024", "1023"); RootMemory(packedBits).SetAttributeValue("BitOffset", "2");
            var bitsResult = await Preview(packedBits, [new("R-time", "-1")]);
            check("signed eleven-bit Time preserves neighboring data and mask positions", bitsResult.Unchanged && Available(bitsResult.Patch)
                && (string?)bitsResult.Patch["ranges"]?[0]?["beforeData"] == "D35A" && (string?)bitsResult.Patch["ranges"]?[0]?["afterData"] == "FFFA"
                && (string?)bitsResult.Patch["ranges"]?[0]?["fieldMask"] == "3FF8" && (string?)bitsResult.Patch["ranges"]?[0]?["afterMask"] == "3FF8", bitsResult.Patch.ToJsonString());
            var bitLe = await Preview(packedBits, [new("R-time", "-1")], "little_endian");
            check("nonbyte signed Time keeps the common LE layout refusal", bitLe.Unchanged && Rejected(bitLe.Patch, "unsupported_layout"), bitLe.Patch.ToJsonString());
            var noMask = Application(); One(noMask, "Mask").Remove(); var nullable = await Preview(noMask);
            check("ordinary Time does not create an absent source mask", nullable.Unchanged && Available(nullable.Patch)
                && nullable.Patch["ranges"]?[0]?["beforeMask"] is null && nullable.Patch["ranges"]?[0]?["afterMask"] is null, nullable.Patch.ToJsonString());
            var module = await Preview(Application(), [new(ModuleTime, "10")]);
            check("selected module ordinary Time uses scope3 and codec4", module.Unchanged && Available(module.Patch, scope: 3)
                && (string?)module.Patch["ranges"]?[0]?["startAddress"] == "0x0000100C" && (string?)module.Patch["ranges"]?[0]?["afterData"] == "000A"
                && module.Binding is { } moduleBinding && moduleBinding.IsCurrent(server.Project!, ProjectQuery.DeviceList(server.Project!).Single()), module.Patch.ToJsonString());
            var numberOnly = await Preview(Application(), [new(ModuleNumber, "4660")]);
            check("unselected ordinary Time does not salt legacy Number codec3 scope1", numberOnly.Unchanged && Available(numberOnly.Patch, dense: false, scope: 1), numberOnly.Patch.ToJsonString());
            var enumOnly = await Preview(Application(), [new(ModuleEnum, "3")]);
            check("unselected ordinary Time does not salt legacy enum codec3 scope2", enumOnly.Unchanged && Available(enumOnly.Patch, dense: false, scope: 2), enumOnly.Patch.ToJsonString());
            var rootNumber = await Preview(Application(), [new("R-time", "10"), new(ModuleNumber, "4660")]);
            check("root DenseTime keeps selected module Number witness at scope1", rootNumber.Unchanged && Available(rootNumber.Patch, scope: 1)
                && (string?)rootNumber.Patch["binding"]?["moduleScopeWitnessHash"] == (string?)numberOnly.Patch["binding"]?["moduleScopeWitnessHash"], rootNumber.Patch.ToJsonString());
            var rootEnum = await Preview(Application(), [new("R-time", "10"), new(ModuleEnum, "3")]);
            check("root DenseTime keeps selected module enum witness at scope2", rootEnum.Unchanged && Available(rootEnum.Patch, scope: 2)
                && (string?)rootEnum.Patch["binding"]?["moduleScopeWitnessHash"] == (string?)enumOnly.Patch["binding"]?["moduleScopeWitnessHash"], rootEnum.Patch.ToJsonString());
            var mixed = await Preview(Application(), [new("R-time", "10"), new("R-number", "5"), new("R-enum", "3"), new("R-single", ".25"), new("R-packed", "90061"), new(ModuleTime, "10"), new(ModuleNumber, "4660"), new(ModuleEnum, "3")]);
            check("DenseTime Number enum Single and packed Time remain one atomic scoped preview", mixed.Unchanged && Available(mixed.Patch, scope: 3, single: true, packed: true)
                && mixed.Patch["fields"]?.AsArray().Count == 8 && mixed.Patch["ranges"]?.AsArray().Count == 4
                && (string?)mixed.Patch["ranges"]?[0]?["afterData"] == "000A" && (string?)mixed.Patch["ranges"]?[1]?["afterData"] == "EF"
                && (string?)mixed.Patch["ranges"]?[1]?["afterMask"] == "BE" && (string?)mixed.Patch["ranges"]?[2]?["afterData"] == "3E800000000A123403"
                && (string?)mixed.Patch["ranges"]?[3]?["afterData"] == "010121", mixed.Patch.ToJsonString());
            var unrelated = Application(); AddCalculation(unrelated, "R-number", "R-enum", false); var unrelatedResult = await Preview(unrelated, [new(ModuleTime, "10")]);
            check("unrelated JS declaration does not become a global Time rejection", unrelatedResult.Unchanged && Available(unrelatedResult.Patch, scope: 3), unrelatedResult.Patch.ToJsonString());

            foreach (var raw in new[] { "32768", "-32769", "1.0", "NaN", "INF", "9223372036854775808", "-9223372036854775809", "1:00:00" })
            {
                var invalid = await Preview(Application(), [new("R-time", raw)]);
                check("ordinary Time rejects invalid raw count " + raw, invalid.Unchanged && Rejected(invalid.Patch, "invalid_value"), invalid.Patch.ToJsonString());
            }
            var negatives = new (string Name, Action<XDocument> Change)[]
            {
                ("entire8bit upper interval exceeds signed storage", d => Width(d, 8, "0", "255")),
                ("entire8bit lower interval exceeds signed storage", d => Width(d, 8, "-129", "10")),
                ("width7", d => Width(d, 7, "0", "10")), ("width33", d => Width(d, 33, "0", "10")), ("width64", d => Width(d, 64, "0", "10")),
                ("reversed bounds", d => Width(d, 16, "2", "1")), ("missing bound", d => Time(d).Attribute("maxInclusive")!.Remove()),
                ("malformed bound", d => Time(d).SetAttributeValue("maxInclusive", "bad")), ("overflow bound", d => Time(d).SetAttributeValue("maxInclusive", "9223372036854775808")),
                ("unmodeled transform", d => Time(d).SetAttributeValue("DisplayFactor", "1")), ("unknown attribute", d => Time(d).SetAttributeValue("Encoding", "unsignedInt")),
                ("shape child", d => Time(d).Add(new XElement(Ns(d) + "Unknown"))), ("unknown unit", d => Time(d).SetAttributeValue("Unit", "Weeks")),
                ("other packed unit", d => Time(d).SetAttributeValue("Unit", "PackedSecondsAndMilliseconds")),
                ("original parameter alias", d => RootStatic(d).Element(Ns(d) + "ParameterRefs")!.Add(new XElement(Ns(d) + "ParameterRef", new XAttribute("Id", "R-alias"), new XAttribute("RefId", "P-time")))),
                ("selected Assign", d => Dynamic(d).Add(new XElement(Ns(d) + "Assign", new XAttribute("TargetParamRefRef", "R-time"), new XAttribute("Value", "0")))),
                ("selected JS dependency", d => AddCalculation(d, "R-time", "R-enum", false)),
                ("missing original Data", d => One(d, "Data").Remove()), ("short original Mask", d => One(d, "Mask").Value = "AA==")
            };
            foreach (var negative in negatives)
            {
                var document = Application(); negative.Change(document); var result = await Preview(document, [new("R-time", "1")]);
                check("ordinary Time rejects " + negative.Name + " atomically", result.Unchanged && Rejected(result.Patch), result.Patch.ToJsonString());
            }
            var dependent = Application(); AddCalculation(dependent, Def + "_P-1_R-1", "R-enum", true); var dependency = await Preview(dependent, [new(ModuleTime, "10")]);
            check("selected module Time calculation binding is refused without running unrelated scripts", dependency.Unchanged && Rejected(dependency.Patch, "unsupported_dependency"), dependency.Patch.ToJsonString());
            var repeat = Application(); var occurrence = Occurrence(repeat); occurrence.ReplaceWith(new XElement(Ns(repeat) + "Repeat", new XAttribute("Id", "RP"), new XAttribute("Count", "1"), new XElement(occurrence)));
            var repeated = await Preview(repeat, [new(ModuleTime, "10")]);
            check("ordinary Time does not widen the admitted module ancestry", repeated.Unchanged && Rejected(repeated.Patch), repeated.Patch.ToJsonString());
            foreach (var timeFirst in new[] { false, true })
            {
                ParameterPatchValue[] values = timeFirst ? [new("R-time", "32768"), new("R-number", "5")] : [new("R-number", "5"), new("R-time", "32768")];
                var invalid = await Preview(Application(), values);
                check("invalid Time cannot publish earlier numeric ranges timeFirst=" + timeFirst, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            var live = await Preview(Application(), [new(ModuleTime, "10")]); var liveProject = server.Project!; var liveDevice = ProjectQuery.DeviceList(liveProject).Single(); var hashLive = SHA256.HashData(File.ReadAllBytes(live.Path));
            liveDevice.ParameterValues["R-control"] = "0"; var changed = Knxproj.ToXml(liveProject).ToString(); var info = (await Tool("project_info", new { })).ToJsonString(); var ordinaryLive = (await Plan()).ToJsonString();
            var absent = (await Plan([new(ModuleTime, "10")]))["offlineParameterPatch"]!;
            check("retired module Time branch stales binding and emits no partial preview", live.Binding is not null && !live.Binding.IsCurrent(liveProject, liveDevice)
                && Rejected(absent, "unknown_parameter") && changed == Knxproj.ToXml(liveProject).ToString() && info == (await Tool("project_info", new { })).ToJsonString()
                && ordinaryLive == (await Plan()).ToJsonString() && SHA256.HashData(File.ReadAllBytes(live.Path)).SequenceEqual(hashLive), absent.ToJsonString());
            liveDevice.ParameterValues["R-control"] = "1"; var restored = (await Plan([new(ModuleTime, "10")]))["offlineParameterPatch"]!;
            check("restored Time scope keeps its witness and requires fresh saved-state binding", Available(restored, scope: 3)
                && (string?)restored["binding"]?["moduleScopeWitnessHash"] == (string?)live.Patch["binding"]?["moduleScopeWitnessHash"]
                && (string?)restored["binding"]?["fingerprint"] != (string?)live.Patch["binding"]?["fingerprint"], restored.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(directory, true); }
    }
    private static XNamespace Ns(XDocument d) => d.Root!.Name.Namespace;
    private static XElement One(XDocument d, string name) => d.Descendants().Single(e => e.Name.LocalName == name);
    private static XElement Program(XDocument d) => One(d, "ApplicationProgram");
    private static XElement RootStatic(XDocument d) => Program(d).Element(Ns(d) + "Static")!;
    private static XElement Dynamic(XDocument d) => Program(d).Element(Ns(d) + "Dynamic")!;
    private static XElement Definition(XDocument d) => d.Descendants(Ns(d) + "ModuleDef").Single();
    private static XElement Occurrence(XDocument d) => d.Descendants(Ns(d) + "Module").Single();
    private static XElement Time(XDocument d) => RootStatic(d).Element(Ns(d) + "ParameterTypes")!.Elements().Single(e => (string?)e.Attribute("Id") == "T").Elements().Single();
    private static XElement RootMemory(XDocument d) => RootStatic(d).Element(Ns(d) + "Parameters")!.Elements().Single(e => (string?)e.Attribute("Id") == "P-time").Element(Ns(d) + "Memory")!;
    private static void Width(XDocument d, int bits, string min, string max) { Time(d).SetAttributeValue("SizeInBit", bits); Time(d).SetAttributeValue("minInclusive", min); Time(d).SetAttributeValue("maxInclusive", max); }
    private static void AddCalculation(XDocument d, string left, string right, bool local)
    {
        (local ? Definition(d).Element(Ns(d) + "Static")! : RootStatic(d)).Add(new XElement(Ns(d) + "ParameterCalculations", new XElement(Ns(d) + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns(d) + "LParameters", new XElement(Ns(d) + "ParameterRefRef", new XAttribute("RefId", left), new XAttribute("AliasName", "x"))),
            new XElement(Ns(d) + "RParameters", new XElement(Ns(d) + "ParameterRefRef", new XAttribute("RefId", right), new XAttribute("AliasName", "y"))), new XElement(Ns(d) + "LRTransformation", "y=x;"))));
    }
    private static XDocument Application() => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="32">
        <Data>{{Convert.ToBase64String([0xD3,0x5A,0x11,0x22,0xD3,0,0,0,0xA5,0x5A,0x11,0x22,0xA5,0x5A,0xC3,0x3C,0,0,0,0,0xA5,0x5A,0x11,0,0,0,0,0,0,0,0,0])}}</Data>
        <Mask>{{Convert.ToBase64String([1,128,32,64,128,0,0,0,1,128,32,64,1,128,32,64,0,0,0,0,1,128,32,0,0,0,0,0,0,0,0,0])}}</Mask></AbsoluteSegment></Code>
        <ParameterTypes><ParameterType Id="T" Name="Ordinary raw count"><TypeTime SizeInBit="16" Unit="Seconds" minInclusive="-32768" maxInclusive="32767" UIHint="Duration_hhmmss"/></ParameterType>
        <ParameterType Id="N3" Name="Three bits"><TypeNumber Type="unsignedInt" SizeInBit="3" minInclusive="0" maxInclusive="7"/></ParameterType><ParameterType Id="N8" Name="Byte"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType>
        <ParameterType Id="N16" Name="Word"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="65535"/></ParameterType>
        <ParameterType Id="E2" Name="Root enum"><TypeRestriction Base="Value" SizeInBit="2"><Enumeration Id="E20" Value="0"/><Enumeration Id="E23" Value="3"/></TypeRestriction></ParameterType>
        <ParameterType Id="E8" Name="Module enum"><TypeRestriction Base="Value" SizeInBit="8"><Enumeration Id="E80" Value="0"/><Enumeration Id="E83" Value="3"/></TypeRestriction></ParameterType>
        <ParameterType Id="F" Name="Single"><TypeFloat Encoding="IEEE-754 Single" minInclusive="-100" maxInclusive="100"/></ParameterType>
        <ParameterType Id="PT" Name="Packed"><TypeTime SizeInBit="24" Unit="PackedDaysHoursMinutesAndSeconds" minInclusive="0" maxInclusive="691199"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P-time" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
        <Parameter Id="P-number" ParameterType="N3" Value="0"><Memory CodeSegment="AS" Offset="4" BitOffset="2"/></Parameter><Parameter Id="P-enum" ParameterType="E2" Value="0"><Memory CodeSegment="AS" Offset="4" BitOffset="5"/></Parameter>
        <Parameter Id="P-single" ParameterType="F" Value="0"><Memory CodeSegment="AS" Offset="8" BitOffset="0"/></Parameter><Parameter Id="P-packed" ParameterType="PT" Value="0"><Memory CodeSegment="AS" Offset="20" BitOffset="0"/></Parameter><Parameter Id="P-control" ParameterType="N8" Value="1"/></Parameters>
        <ParameterRefs><ParameterRef Id="R-time" RefId="P-time"/><ParameterRef Id="R-number" RefId="P-number"/><ParameterRef Id="R-enum" RefId="P-enum"/><ParameterRef Id="R-single" RefId="P-single"/><ParameterRef Id="R-packed" RefId="P-packed"/><ParameterRef Id="R-control" RefId="P-control"/></ParameterRefs></Static>
        <ModuleDefs><ModuleDef Id="{{Def}}"><Arguments><Argument Id="{{Def}}_A-1" Name="Base" Allocates="5"/></Arguments><Static><Parameters>
        <Parameter Id="{{Def}}_P-1" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0" BaseOffset="{{Def}}_A-1"/></Parameter>
        <Parameter Id="{{Def}}_P-2" ParameterType="N16" Value="0"><Memory CodeSegment="AS" Offset="2" BitOffset="0" BaseOffset="{{Def}}_A-1"/></Parameter>
        <Parameter Id="{{Def}}_P-3" ParameterType="E8" Value="0"><Memory CodeSegment="AS" Offset="4" BitOffset="0" BaseOffset="{{Def}}_A-1"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="{{Def}}_P-1_R-1" RefId="{{Def}}_P-1"/><ParameterRef Id="{{Def}}_P-2_R-2" RefId="{{Def}}_P-2"/><ParameterRef Id="{{Def}}_P-3_R-3" RefId="{{Def}}_P-3"/></ParameterRefs></Static>
        <Dynamic><ParameterRefRef RefId="{{Def}}_P-1_R-1"/><ParameterRefRef RefId="{{Def}}_P-2_R-2"/><ParameterRefRef RefId="{{Def}}_P-3_R-3"/></Dynamic></ModuleDef></ModuleDefs>
        <Dynamic><ParameterRefRef RefId="R-time"/><ParameterRefRef RefId="R-number"/><ParameterRefRef RefId="R-enum"/><ParameterRefRef RefId="R-single"/><ParameterRefRef RefId="R-packed"/><ParameterRefRef RefId="R-control"/>
        <choose ParamRefId="R-control"><when test="1"><Module Id="{{Call}}" RefId="{{Def}}"><NumericArg RefId="{{Def}}_A-1" Value="12"/></Module></when><when test="0"/></choose></Dynamic></ApplicationProgram></KNX>
        """);
    private static string Fixture(string directory, XDocument document)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".knxproj"); var project = ProjectWizard.Create(new() { Name = "Synthetic signed ordinary Time subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) }); Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE7/" + App + ".xml").Open(); document.Save(output); return path;
    }
}
