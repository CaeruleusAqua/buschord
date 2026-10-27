using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class TimePatchToolChecks
{
    private const string App = "M-FFE4_A-0001-01-0000";
    private const string PackedProfile = "PackedDaysHoursMinutesAndSeconds24BigEndianOnly";
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-time-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 60000;
            async Task<JsonNode> Tool(string name, object arguments)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } });
                var response = JsonNode.Parse((await server.HandleAsync(request))!)!;
                return response["result"]?["structuredContent"] ?? throw new InvalidOperationException(response.ToJsonString());
            }
            async Task<(JsonNode Patch, bool Unchanged)> Preview(XDocument document, IReadOnlyList<ParameterPatchValue> values, string order = "big_endian")
            {
                var path = Fixture(root, Guid.NewGuid().ToString("N"), document); var hash = SHA256.HashData(File.ReadAllBytes(path)); await Tool("project_open", new { path });
                var original = Knxproj.ToXml(server.Project!).ToString(); var history = (await Tool("project_info", new { })).ToJsonString();
                var ordinary = (await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
                var response = await Tool("download_plan", new { device_address = "1.1.1", byte_order = order,
                    parameter_subset = values.Select(v => new { refId = v.RefId, rawValue = v.RawValue }).ToArray() }); var patch = response["offlineParameterPatch"]!;
                var unchanged = SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash) && Knxproj.ToXml(server.Project!).ToString() == original
                    && (await Tool("project_info", new { })).ToJsonString() == history && (await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString() == ordinary
                    && server.Project!.Installations[0].Areas[0].Lines[0].Devices[0].ParameterValues.Count == 0
                    && response["plan"]!.ToJsonString() == JsonNode.Parse(ordinary)!["plan"]!.ToJsonString() && response["plan"]?["program"] is null
                    && response["plan"]?["ready"]?.GetValue<bool>() == false && response["plan"]?["imageBytes"]?.GetValue<int>() == 0
                    && response["plan"]?["chunks"]?.AsArray().Count == 0 && response["writesEnabled"]?.GetValue<bool>() == false && server.Tools.Count == 55
                    && (string?)patch["scope"] == "ExplicitSubsetPreview" && patch["completeApplicationImage"]?.GetValue<bool>() == false && patch["downloadEligible"]?.GetValue<bool>() == false;
                return (patch, unchanged);
            }
            static bool Available(JsonNode patch, bool time = true, bool single = false) => patch["available"]?.GetValue<bool>() == true
                && patch["binding"]?["codecProfileVersion"]?.GetValue<int>() == 3
                && (string?)patch["binding"]?["numericParseProfile"] == (single ? "ExactIntegerAndDotNet10XmlDoubleToSingle" : "ExactInteger")
                && (string?)patch["binding"]?["packedTimeEncodingProfile"] == (time ? PackedProfile : null);
            static bool Rejected(JsonNode patch) => patch["available"]?.GetValue<bool>() == false && patch["binding"] is null
                && patch["ranges"]?.AsArray().Count == 0 && patch["fields"]?.AsArray().Count == 0 && patch["issues"]?.AsArray().Count > 0;
            foreach (var vector in new[] { ("0", "000000"), ("1", "010000"), ("59", "3B0000"), ("60", "000100"), ("3600", "000001"),
                ("86399", "3B3B17"), ("86400", "000020"), ("90061", "010121"), ("691199", "3B3BF7") })
            {
                var result = await Preview(Application(), [new("R1", vector.Item1)]); var range = result.Patch["ranges"]?[0];
                check("PackedTime24 seconds " + vector.Item1 + " uses fixed second-minute-day/hour bytes", result.Unchanged && Available(result.Patch)
                    && (string?)range?["afterData"] == vector.Item2 && (string?)range?["beforeData"] == "A55A11" && (string?)range?["fieldMask"] == "FFFFFF"
                    && (string?)range?["beforeMask"] == "018020" && (string?)range?["afterMask"] == "FFFFFF"
                    && (string?)result.Patch["fields"]?[0]?["rawValue"] == vector.Item1 && (string?)range?["afterData"] != "015FCD", result.Patch.ToJsonString());
            }
            var noMask = Application(); One(noMask, "Mask").Remove(); var nullable = await Preview(noMask, [new("R1", "90061")]);
            check("PackedTime24 retains absent source masks", nullable.Unchanged && Available(nullable.Patch) && nullable.Patch["ranges"]?[0]?["beforeMask"] is null
                && nullable.Patch["ranges"]?[0]?["afterMask"] is null && (string?)nullable.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFF", nullable.Patch.ToJsonString());
            var integerOnly = await Preview(Application(), [new("R2", "5"), new("R3", "3")]);
            check("unselected Time does not imply a packed encoding profile", integerOnly.Unchanged && Available(integerOnly.Patch, time: false), integerOnly.Patch.ToJsonString());
            var floatOnly = await Preview(Application(), [new("R4", ".25")]);
            check("Float binding retains its numeric profile without claiming packed Time", floatOnly.Unchanged && Available(floatOnly.Patch, time: false, single: true), floatOnly.Patch.ToJsonString());
            var mixed = await Preview(Application(), [new("R1", "90061"), new("R2", "5"), new("R3", "3"), new("R4", ".25")]);
            check("Time Number Enum Float combine atomically while preserving unused mask bits", mixed.Unchanged && Available(mixed.Patch, single: true)
                && mixed.Patch["fields"]?.AsArray().Count == 4 && mixed.Patch["ranges"]?.AsArray().Count == 1
                && (string?)mixed.Patch["ranges"]?[0]?["afterData"] == "010121D73E800000" && (string?)mixed.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFF7EFFFFFFFF"
                && (string?)mixed.Patch["ranges"]?[0]?["afterMask"] == "FFFFFFFEFFFFFFFF", mixed.Patch.ToJsonString());
            foreach (var value in new[] { "-1", "691200", "90061.0", "1:01:01", "NaN", "INF", "9223372036854775808", "1e3" })
            {
                var invalid = await Preview(Application(), [new("R1", value)]);
                check("PackedTime24 rejects invalid raw seconds " + value, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            var narrow = Application(); Shape(narrow).SetAttributeValue("minInclusive", "100"); Shape(narrow).SetAttributeValue("maxInclusive", "200");
            Parameter(narrow).SetAttributeValue("Value", "100");
            foreach (var vector in new[] { ("100", "280100"), ("200", "140300"), ("99", ""), ("201", "") })
            {
                var result = await Preview(narrow, [new("R1", vector.Item1)]);
                check("PackedTime24 declared inclusive boundary " + vector.Item1, result.Unchanged && (vector.Item2.Length == 0 ? Rejected(result.Patch)
                    : Available(result.Patch) && (string?)result.Patch["ranges"]?[0]?["afterData"] == vector.Item2), result.Patch.ToJsonString());
            }
            foreach (var unit in new[] { "Hours", "Minutes", "Seconds", "HundredMilliseconds", "TenMilliseconds", "Milliseconds", "PackedSecondsAndMilliseconds", "PackedMinutesSecondsAndMilliseconds" })
            {
                var document = Application(); Shape(document).SetAttributeValue("Unit", unit); var result = await Preview(document, [new("R1", "90061")]);
                var ordinary = unit is "Hours" or "Minutes" or "Seconds" or "HundredMilliseconds" or "TenMilliseconds" or "Milliseconds";
                check("Time unit stays in its own explicit encoding profile: " + unit, result.Unchanged && (ordinary
                    ? result.Patch["available"]?.GetValue<bool>() == true && result.Patch["binding"]?["codecProfileVersion"]?.GetValue<int>() == 4
                        && (string?)result.Patch["binding"]?["denseTimeEncodingProfile"] == "SignedRawOrdinaryTime8To32" && result.Patch["binding"]?["packedTimeEncodingProfile"] is null
                        && (string?)result.Patch["ranges"]?[0]?["afterData"] == "015FCD" && (string?)result.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFF"
                    : Rejected(result.Patch)), result.Patch.ToJsonString());
            }
            var invalidDeclarations = new (string Name, Action<XDocument> Change)[]
            {
                ("width16", d => Shape(d).SetAttributeValue("SizeInBit", "16")), ("width32", d => Shape(d).SetAttributeValue("SizeInBit", "32")),
                ("width64", d => Shape(d).SetAttributeValue("SizeInBit", "64")), ("negative bound", d => Shape(d).SetAttributeValue("minInclusive", "-1")),
                ("wrapping maximum", d => Shape(d).SetAttributeValue("maxInclusive", "691200")), ("reversed bounds", d => { Shape(d).SetAttributeValue("minInclusive", "1000"); Shape(d).SetAttributeValue("maxInclusive", "999"); }),
                ("missing bound", d => Shape(d).Attribute("maxInclusive")!.Remove()), ("malformed bound", d => Shape(d).SetAttributeValue("maxInclusive", "NaN")),
                ("Int64-overflow bound", d => Shape(d).SetAttributeValue("maxInclusive", "9223372036854775808")),
                ("unmodeled transform", d => Shape(d).SetAttributeValue("DisplayFactor", "1")), ("unknown encoding", d => Shape(d).SetAttributeValue("Encoding", "unsignedInt")),
                ("shape child", d => Shape(d).Add(new XElement(d.Root!.Name.Namespace + "Unknown"))), ("unsupported UIHint", d => Shape(d).SetAttributeValue("UIHint", "Date")),
            };
            foreach (var item in invalidDeclarations)
            {
                var document = Application(); item.Change(document); var invalid = await Preview(document, [new("R1", "90061")]);
                check("PackedTime24 refuses source " + item.Name, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            var little = await Preview(Application(), [new("R1", "90061")], "little_endian");
            check("PackedTime24 refuses LE rather than reversing fixed packed bytes", little.Unchanged && Rejected(little.Patch)
                && (string?)little.Patch["issues"]?[0]?["code"] == "unsupported_layout", little.Patch.ToJsonString());
            var bit = Application(); Parameter(bit).Elements().Single().SetAttributeValue("BitOffset", "1"); var unaligned = await Preview(bit, [new("R1", "90061")]);
            check("PackedTime24 requires BitOffset zero", unaligned.Unchanged && Rejected(unaligned.Patch), unaligned.Patch.ToJsonString());
            foreach (var timeFirst in new[] { false, true })
            {
                ParameterPatchValue[] values = timeFirst ? [new("R1", "691200"), new("R2", "5"), new("R3", "3"), new("R4", ".25")]
                    : [new("R2", "5"), new("R3", "3"), new("R4", ".25"), new("R1", "691200")];
                var invalid = await Preview(Application(), values);
                check("mixed Time rejection has no partial prior codec output, timeFirst=" + timeFirst, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            // Align the other three codecs so this request reaches the Time-specific LE gate.
            var aligned = Application(); One(aligned, "TypeNumber").SetAttributeValue("SizeInBit", "8"); One(aligned, "TypeNumber").SetAttributeValue("maxInclusive", "255");
            One(aligned, "TypeRestriction").SetAttributeValue("SizeInBit", "8");
            foreach (var (id, offset) in new[] { ("P2", "3"), ("P3", "4"), ("P4", "5") })
            { var memory = aligned.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == id).Elements().Single(); memory.SetAttributeValue("Offset", offset); memory.SetAttributeValue("BitOffset", "0"); }
            One(aligned, "AbsoluteSegment").SetAttributeValue("Size", "12"); One(aligned, "Data").Value = Convert.ToBase64String(new byte[12]); One(aligned, "Mask").Value = Convert.ToBase64String(new byte[12]);
            var mixedLittle = await Preview(aligned, [new("R2", "5"), new("R3", "3"), new("R4", ".25"), new("R1", "90061")], "little_endian");
            check("mixed LE cannot grant packed-Time capability through numeric codecs", mixedLittle.Unchanged && Rejected(mixedLittle.Patch)
                && (string?)mixedLittle.Patch["issues"]?[0]?["code"] == "unsupported_layout" && (string?)mixedLittle.Patch["issues"]?[0]?["refId"] == "R1", mixedLittle.Patch.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(root, true); }
    }
    private static XElement One(XDocument document, string name) => document.Descendants().Single(e => e.Name.LocalName == name);
    private static XElement Shape(XDocument document) => One(document, "TypeTime");
    private static XElement Parameter(XDocument document) => document.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == "P1");
    private static XDocument Application() => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code>
        <AbsoluteSegment Id="AS" Address="4096" Size="8"><Data>{{Convert.ToBase64String([0xA5,0x5A,0x11,0x81,0xA5,0x5A,0x11,0x22])}}</Data><Mask>{{Convert.ToBase64String([1,128,32,128,1,128,32,64])}}</Mask></AbsoluteSegment>
        </Code><ParameterTypes><ParameterType Id="T" Name="PackedTime"><TypeTime SizeInBit="24" Unit="PackedDaysHoursMinutesAndSeconds" minInclusive="0" maxInclusive="691199" UIHint="Time_hhmmss"/></ParameterType>
        <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="3" minInclusive="0" maxInclusive="7"/></ParameterType>
        <ParameterType Id="E" Name="Enum"><TypeRestriction Base="Value" SizeInBit="3"><Enumeration Id="E0" Value="0"/><Enumeration Id="E3" Value="3"/></TypeRestriction></ParameterType>
        <ParameterType Id="F" Name="Single"><TypeFloat Encoding="IEEE-754 Single" minInclusive="-100" maxInclusive="100"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P1" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
        <Parameter Id="P2" ParameterType="N" Value="0"><Memory CodeSegment="AS" Offset="3" BitOffset="1"/></Parameter>
        <Parameter Id="P3" ParameterType="E" Value="0"><Memory CodeSegment="AS" Offset="3" BitOffset="4"/></Parameter>
        <Parameter Id="P4" ParameterType="F" Value="0"><Memory CodeSegment="AS" Offset="4" BitOffset="0"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/><ParameterRef Id="R3" RefId="P3"/><ParameterRef Id="R4" RefId="P4"/></ParameterRefs>
        </Static><Dynamic><ParameterRefRef RefId="R1"/><ParameterRefRef RefId="R2"/><ParameterRefRef RefId="R3"/><ParameterRefRef RefId="R4"/></Dynamic></ApplicationProgram></KNX>
        """);
    private static string Fixture(string root, string name, XDocument definition)
    {
        var path = Path.Combine(root, name + ".knxproj"); var project = ProjectWizard.Create(new() { Name = "Synthetic PackedTime24 subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) }); Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE4/" + App + ".xml").Open(); definition.Save(output);
        return path;
    }
}
