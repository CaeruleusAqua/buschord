using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class FloatPatchToolChecks
{
    private const string App = "M-FFE3_A-0001-01-0000";
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-float-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 50000;
            async Task<JsonNode> Tool(string name, object arguments)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } });
                var response = JsonNode.Parse((await server.HandleAsync(request))!)!;
                return response["result"]?["structuredContent"] ?? throw new InvalidOperationException(response.ToJsonString());
            }
            async Task<(JsonNode Patch, bool Unchanged)> Preview(XDocument document, IReadOnlyList<ParameterPatchValue> selected, string order = "big_endian")
            {
                var path = Fixture(root, Guid.NewGuid().ToString("N"), document); var sourceHash = SHA256.HashData(File.ReadAllBytes(path)); await Tool("project_open", new { path });
                var original = Knxproj.ToXml(server.Project!).ToString(); var history = (await Tool("project_info", new { })).ToJsonString();
                var ordinary = (await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
                var response = await Tool("download_plan", new { device_address = "1.1.1", byte_order = order,
                    parameter_subset = selected.Select(v => new { refId = v.RefId, rawValue = v.RawValue }).ToArray() }); var patch = response["offlineParameterPatch"]!;
                var unchanged = SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(sourceHash) && Knxproj.ToXml(server.Project!).ToString() == original
                    && (await Tool("project_info", new { })).ToJsonString() == history && (await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString() == ordinary
                    && server.Project!.Installations[0].Areas[0].Lines[0].Devices[0].ParameterValues.Count == 0
                    && response["plan"]!.ToJsonString() == JsonNode.Parse(ordinary)!["plan"]!.ToJsonString() && response["plan"]?["program"] is null
                    && response["plan"]?["ready"]?.GetValue<bool>() == false && response["plan"]?["imageBytes"]?.GetValue<int>() == 0
                    && response["plan"]?["chunks"]?.AsArray().Count == 0 && response["writesEnabled"]?.GetValue<bool>() == false && server.Tools.Count == McpToolContract.ExpectedToolCount
                    && (string?)patch["scope"] == "ExplicitSubsetPreview" && patch["completeApplicationImage"]?.GetValue<bool>() == false && patch["downloadEligible"]?.GetValue<bool>() == false;
                return (patch, unchanged);
            }
            static bool Available(JsonNode patch, string profile = "ExactIntegerAndDotNet10XmlDoubleToSingle") => patch["available"]?.GetValue<bool>() == true
                && patch["binding"]?["codecProfileVersion"]?.GetValue<int>() == 3 && (string?)patch["binding"]?["numericParseProfile"] == profile;
            static bool Rejected(JsonNode patch) => patch["available"]?.GetValue<bool>() == false && patch["binding"] is null
                && patch["ranges"]?.AsArray().Count == 0 && patch["fields"]?.AsArray().Count == 0 && patch["issues"]?.AsArray().Count > 0;
            var vectors = new (string Value, string Order, string Hex)[]
            {
                (".25", "big_endian", "3E800000"), ("0.25", "little_endian", "0000803E"),
                (".1", "big_endian", "3DCCCCCD"), ("0.1", "little_endian", "CDCCCC3D"),
                ("-0", "big_endian", "80000000"), ("-0.0", "little_endian", "00000080"),
                ("1.0000000596046448", "big_endian", "3F800000"),
                ("1e-45", "big_endian", "00000001"), ("-1e-45", "big_endian", "80000001"),
            };
            foreach (var vector in vectors)
            {
                var result = await Preview(Application(), [new("R1", vector.Value)], vector.Order); var range = result.Patch["ranges"]?[0];
                check("IEEE32 XML Double-to-Single " + vector.Value + " " + vector.Order, result.Unchanged && Available(result.Patch)
                    && (string?)range?["beforeData"] == "A55A1122" && (string?)range?["afterData"] == vector.Hex && (string?)range?["fieldMask"] == "FFFFFFFF"
                    && (string?)range?["beforeMask"] == "01802040" && (string?)range?["afterMask"] == "FFFFFFFF"
                    && (string?)result.Patch["fields"]?[0]?["rawValue"] == vector.Value, result.Patch.ToJsonString());
            }
            var noMask = Application(); One(noMask, "Mask").Remove(); var nullable = await Preview(noMask, [new("R1", "0.25")]);
            check("IEEE32 does not invent an absent resource mask", nullable.Unchanged && Available(nullable.Patch)
                && nullable.Patch["ranges"]?[0]?["beforeMask"] is null && nullable.Patch["ranges"]?[0]?["afterMask"] is null
                && (string?)nullable.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFFFF", nullable.Patch.ToJsonString());
            var integerOnly = await Preview(Application(), [new("R2", "5"), new("R3", "3")]);
            check("unselected float metadata does not relabel the exact integer parse profile", integerOnly.Unchanged && Available(integerOnly.Patch, "ExactInteger"), integerOnly.Patch.ToJsonString());
            var mixed = await Preview(Application(), [new("R1", "0.25"), new("R2", "5"), new("R3", "3")]);
            check("mixed Float Number Enum uses one atomic masked subset with explicit parse binding", mixed.Unchanged && Available(mixed.Patch)
                && mixed.Patch["fields"]?.AsArray().Count == 3 && mixed.Patch["ranges"]?.AsArray().Count == 1
                && (string?)mixed.Patch["ranges"]?[0]?["afterData"] == "3E800000D7" && (string?)mixed.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFFFF7E"
                && (string?)mixed.Patch["ranges"]?[0]?["afterMask"] == "FFFFFFFFFE", mixed.Patch.ToJsonString());
            foreach (var value in new[] { "NaN", "INF", "-INF", "Infinity", "1e309", "100.01", "-100.01", "not-a-number" })
            {
                var invalid = await Preview(Application(), [new("R1", value)]);
                check("IEEE32 refuses nonfinite malformed or out-of-bound raw " + value, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            var wideBounds = Application(); Shape(wideBounds).SetAttributeValue("minInclusive", "-1e100"); Shape(wideBounds).SetAttributeValue("maxInclusive", "1e100");
            var overflow = await Preview(wideBounds, [new("R1", "1e39")]);
            check("finite Double within metadata bounds still refuses Single overflow", overflow.Unchanged && Rejected(overflow.Patch), overflow.Patch.ToJsonString());
            var invalidDeclarations = new (string Name, Action<XDocument> Change)[]
            {
                ("IEEE64 encoding", d => Shape(d).SetAttributeValue("Encoding", "IEEE-754 Double")),
                ("DPT9 encoding", d => Shape(d).SetAttributeValue("Encoding", "DPT 9")),
                ("Increment identity", d => Shape(d).SetAttributeValue("Increment", "1")),
                ("DisplayFactor identity", d => Shape(d).SetAttributeValue("DisplayFactor", "1")),
                ("DisplayOffset identity", d => Shape(d).SetAttributeValue("DisplayOffset", "0")),
                ("nonfinite minimum", d => Shape(d).SetAttributeValue("minInclusive", "NaN")),
                ("nonfinite maximum", d => Shape(d).SetAttributeValue("maxInclusive", "INF")),
                ("reversed bounds", d => Shape(d).SetAttributeValue("minInclusive", "101")),
                ("missing bound", d => Shape(d).Attribute("maxInclusive")!.Remove()),
                ("malformed bound", d => Shape(d).SetAttributeValue("maxInclusive", "bad")),
                ("unmodeled SizeInBit", d => Shape(d).SetAttributeValue("SizeInBit", "32")),
                ("shape child", d => Shape(d).Add(new XElement(d.Root!.Name.Namespace + "Unknown"))),
                ("overlong DisplayFormat", d => Shape(d).SetAttributeValue("DisplayFormat", new string('0', 129))),
            };
            foreach (var item in invalidDeclarations)
            {
                var document = Application(); item.Change(document); var invalid = await Preview(document, [new("R1", ".25")]);
                check("IEEE32 source refuses " + item.Name, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            foreach (var order in new[] { "big_endian", "little_endian" })
            {
                var unaligned = Application(); Parameter(unaligned).Elements().Single().SetAttributeValue("BitOffset", "1");
                var invalid = await Preview(unaligned, [new("R1", ".25")], order);
                check("IEEE32 refuses unaligned bit layout in " + order, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            foreach (var floatFirst in new[] { false, true })
            {
                ParameterPatchValue[] values = floatFirst ? [new("R1", "NaN"), new("R2", "5"), new("R3", "3")] : [new("R2", "5"), new("R3", "3"), new("R1", "NaN")];
                var invalid = await Preview(Application(), values);
                check("mixed valid integer enum plus invalid float has no partial bytes, floatFirst=" + floatFirst,
                    invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(root, true); }
    }
    private static XElement One(XDocument document, string name) => document.Descendants().Single(e => e.Name.LocalName == name);
    private static XElement Shape(XDocument document) => One(document, "TypeFloat");
    private static XElement Parameter(XDocument document) => document.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == "P1");
    private static XDocument Application() => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code>
        <AbsoluteSegment Id="AS" Address="4096" Size="8"><Data>{{Convert.ToBase64String([0xA5,0x5A,0x11,0x22,0x81,0,0,0])}}</Data><Mask>{{Convert.ToBase64String([1,128,32,64,128,0,0,0])}}</Mask></AbsoluteSegment>
        </Code><ParameterTypes><ParameterType Id="F" Name="Single"><TypeFloat Encoding="IEEE-754 Single" minInclusive="-100" maxInclusive="100" DisplayFormat="0.000" UIHint="Slider"/></ParameterType>
        <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="3" minInclusive="0" maxInclusive="7"/></ParameterType>
        <ParameterType Id="E" Name="Enum"><TypeRestriction Base="Value" SizeInBit="3"><Enumeration Id="E0" Value="0"/><Enumeration Id="E3" Value="3"/></TypeRestriction></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P1" ParameterType="F" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
        <Parameter Id="P2" ParameterType="N" Value="0"><Memory CodeSegment="AS" Offset="4" BitOffset="1"/></Parameter>
        <Parameter Id="P3" ParameterType="E" Value="0"><Memory CodeSegment="AS" Offset="4" BitOffset="4"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/><ParameterRef Id="R3" RefId="P3"/></ParameterRefs>
        </Static><Dynamic><ParameterRefRef RefId="R1"/><ParameterRefRef RefId="R2"/><ParameterRefRef RefId="R3"/></Dynamic></ApplicationProgram></KNX>
        """);
    private static string Fixture(string root, string name, XDocument definition)
    {
        var path = Path.Combine(root, name + ".knxproj"); var project = ProjectWizard.Create(new() { Name = "Synthetic explicit IEEE32 subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) }); Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE3/" + App + ".xml").Open(); definition.Save(output);
        return path;
    }
}
