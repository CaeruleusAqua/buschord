using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class ParameterPatchToolChecks
{
    private const string App = "M-FFE1_A-0001-01-0000";
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-subset-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 30000;
            async Task<JsonNode> Tool(string name, object arguments)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } });
                return JsonNode.Parse((await server.HandleAsync(request))!)!;
            }
            async Task<JsonNode> RawPlan(string argumentsJson)
            {
                var request = "{\"jsonrpc\":\"2.0\",\"id\":" + (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + argumentsJson + "}}";
                return JsonNode.Parse((await server.HandleAsync(request))!)!;
            }
            static bool Error(JsonNode result) => result["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode response) => response["result"]?["structuredContent"] ?? throw new InvalidOperationException(response.ToJsonString());
            async Task<JsonNode> Plan(object? args = null) => await Tool("download_plan", args ?? new { device_address = "1.1.1" });
            object Selection(string value = "4660", string order = "big_endian") => new { device_address = "1.1.1", parameter_subset = new[] { new { refId = "R1", rawValue = value } }, byte_order = order };
            var path = Fixture(root, "valid", Application()); var hash = SHA256.HashData(File.ReadAllBytes(path));
            var opened = await Tool("project_open", new { path });
            if (Error(opened)) throw new InvalidOperationException(opened.ToJsonString());
            var before = Knxproj.ToXml(server.Project!).ToString(); var beforeInfo = Data(await Tool("project_info", new { })).ToJsonString();
            var baseline = Data(await Plan()).ToJsonString();
            check("subset options are bounded and paired on the existing 55-tool schema", server.Tools.Count == 55
                && server.Tools.Single(t => t.Name == "download_plan").InputSchema["properties"]?["parameter_subset"]?["maxItems"]?.GetValue<int>() == 64
                && server.Tools.Single(t => t.Name == "download_plan").InputSchema["properties"]?["byte_order"]?["enum"]?.AsArray().Count == 2
                && server.Tools.Single(t => t.Name == "download_plan").InputSchema["dependentRequired"] is JsonObject, null);
            check("ordinary download plan has no automatic numeric patch computation", Data(await Plan())["offlineParameterPatch"] is null, baseline);
            var result = await Plan(Selection()); var content = Data(result); var patch = content["offlineParameterPatch"]!;
            var range = patch["ranges"]?.AsArray().SingleOrDefault();
            check("explicit BigEndian subset returns bound readonly preview without image authority", !Error(result)
                && patch["available"]?.GetValue<bool>() == true && (string?)patch["scope"] == "ExplicitSubsetPreview"
                && patch["completeApplicationImage"]?.GetValue<bool>() == false && patch["downloadEligible"]?.GetValue<bool>() == false
                && patch["readOnly"]?.GetValue<bool>() == true && (string?)patch["binding"]?["applicationId"] == App
                && (string?)patch["binding"]?["fingerprint"] is { Length: 64 } && patch["issues"]?.AsArray().Count == 0, patch.ToJsonString());
            check("only selected bytes are exposed from a larger source resource", patch["fields"]?.AsArray().Count == 1 && range?["byteLength"]?.GetValue<int>() == 2
                && (string?)range?["beforeData"] == "A55A" && (string?)range?["afterData"] == "1234" && (string?)range?["fieldMask"] == "FFFF"
                && (string?)range?["beforeMask"] == "0180" && (string?)range?["afterMask"] == "FFFF" && patch.ToJsonString().Length < 10000, patch.ToJsonString());
            check("successful subset preview leaves all download gates unchanged", content["plan"]!.ToJsonString() == JsonNode.Parse(baseline)!["plan"]!.ToJsonString()
                && content["writesEnabled"]?.GetValue<bool>() == false && content["plan"]?["ready"]?.GetValue<bool>() == false
                && content["plan"]?["program"] is null && content["plan"]?["imageBytes"]?.GetValue<int>() == 0 && content["plan"]?["chunks"]?.AsArray().Count == 0, content.ToJsonString());
            var little = Data(await Plan(Selection(order: "little_endian")))["offlineParameterPatch"]!;
            check("explicit LittleEndian selection uses its own byte order and source binding", little["available"]?.GetValue<bool>() == true
                && (string?)little["ranges"]?[0]?["afterData"] == "3412" && (string?)little["binding"]?["fingerprint"] != (string?)patch["binding"]?["fingerprint"], little.ToJsonString());
            var invalidOptions = new (string Name, object Args)[]
            {
                ("subset without byte order", new { device_address = "1.1.1", parameter_subset = new[] { new { refId = "R1", rawValue = "2" } } }),
                ("byte order without subset", new { device_address = "1.1.1", byte_order = "big_endian" }),
                ("empty subset", new { device_address = "1.1.1", parameter_subset = Array.Empty<object>(), byte_order = "big_endian" }),
                ("more than64 refs", new { device_address = "1.1.1", parameter_subset = Enumerable.Range(0, 65).Select(i => new { refId = "R" + i, rawValue = "2" }).ToArray(), byte_order = "big_endian" }),
                ("duplicate refs", new { device_address = "1.1.1", parameter_subset = new[] { new { refId = "R1", rawValue = "2" }, new { refId = "R1", rawValue = "2" } }, byte_order = "big_endian" }),
                ("non-string raw value", new { device_address = "1.1.1", parameter_subset = new[] { new { refId = "R1", rawValue = 2 } }, byte_order = "big_endian" }),
                ("unrecognized byte order", Selection(order: "BigEndian")),
                ("overlong raw value", Selection(new string('1', 65))),
                ("overlong ref", new { device_address = "1.1.1", parameter_subset = new[] { new { refId = new string('R', 257), rawValue = "2" } }, byte_order = "big_endian" }),
                ("extra item field", new { device_address = "1.1.1", parameter_subset = new[] { new { refId = "R1", rawValue = "2", execute_scripts = true } }, byte_order = "big_endian" }),
            };
            foreach (var item in invalidOptions)
            {
                var rejected = await Plan(item.Args);
                check("subset tool rejects " + item.Name + " atomically", Error(rejected) && Data(await Plan()).ToJsonString() == baseline
                    && Knxproj.ToXml(server.Project!).ToString() == before && Data(await Tool("project_info", new { })).ToJsonString() == beforeInfo, rejected.ToJsonString());
            }
            // Preserve duplicate JSON members: serializing anonymous objects cannot construct
            // these requests and JsonElement.TryGetProperty alone would silently choose the last.
            var rawInvalidOptions = new (string Name, string Arguments)[]
            {
                ("duplicate byte_order members", """{"device_address":"1.1.1","byte_order":"big_endian","byte_order":"little_endian","parameter_subset":[{"refId":"R1","rawValue":"2"}]}"""),
                ("duplicate parameter_subset members", """{"device_address":"1.1.1","byte_order":"big_endian","parameter_subset":[{"refId":"R1","rawValue":"2"}],"parameter_subset":[{"refId":"R2","rawValue":"3"}]}"""),
                ("duplicate item refId members", """{"device_address":"1.1.1","byte_order":"big_endian","parameter_subset":[{"refId":"R1","refId":"R2","rawValue":"2"}]}"""),
                ("duplicate item rawValue members", """{"device_address":"1.1.1","byte_order":"big_endian","parameter_subset":[{"refId":"R1","rawValue":"2","rawValue":"3"}]}"""),
                ("control character in refId", """{"device_address":"1.1.1","byte_order":"big_endian","parameter_subset":[{"refId":"R1\u0001","rawValue":"2"}]}"""),
                ("control character in rawValue", """{"device_address":"1.1.1","byte_order":"big_endian","parameter_subset":[{"refId":"R1","rawValue":"2\u0001"}]}"""),
                ("control character in byte_order", """{"device_address":"1.1.1","byte_order":"big_endian\u0001","parameter_subset":[{"refId":"R1","rawValue":"2"}]}"""),
            };
            foreach (var item in rawInvalidOptions)
            {
                var rejected = await RawPlan(item.Arguments);
                check("subset raw JSON rejects " + item.Name + " without last-member precedence", Error(rejected)
                    && Data(await Plan()).ToJsonString() == baseline && Data(await Tool("project_info", new { })).ToJsonString() == beforeInfo
                    && Knxproj.ToXml(server.Project!).ToString() == before && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash), rejected.ToJsonString());
            }
            foreach (var rawValue in new[] { "65536", "1.5" })
            {
                var rejected = Data(await Plan(Selection(rawValue)))["offlineParameterPatch"]!;
                check("subset rejects unencodable raw " + rawValue + " without partial bytes", rejected["available"]?.GetValue<bool>() == false
                    && rejected["binding"] is null && rejected["ranges"]?.AsArray().Count == 0 && rejected["fields"]?.AsArray().Count == 0
                    && rejected["issues"]?.AsArray().Count > 0 && Data(await Plan()).ToJsonString() == baseline, rejected.ToJsonString());
            }
            var partlyInvalid = Data(await Plan(new { device_address = "1.1.1", byte_order = "big_endian", parameter_subset = new[]
                { new { refId = "R1", rawValue = "2" }, new { refId = "missing", rawValue = "2" } } }))["offlineParameterPatch"]!;
            check("one unknown selection rejects the entire subset without previous successful fields", partlyInvalid["available"]?.GetValue<bool>() == false
                && partlyInvalid["ranges"]?.AsArray().Count == 0 && partlyInvalid["fields"]?.AsArray().Count == 0 && partlyInvalid["binding"] is null, partlyInvalid.ToJsonString());
            check("all positive and rejected previews preserve source values project undo and ordinary plan", SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash)
                && Knxproj.ToXml(server.Project!).ToString() == before && Data(await Tool("project_info", new { })).ToJsonString() == beforeInfo
                && Data(await Plan()).ToJsonString() == baseline, null);

            var negative = new (string Name, Action<XDocument> Change, string Order)[]
            {
                ("missing initial Data", d => One(d, "Data").Remove(), "big_endian"),
                ("malformed initial Data", d => One(d, "Data").Value = "%%%", "big_endian"),
                ("short source mask", d => One(d, "Mask").Value = Convert.ToBase64String([1]), "big_endian"),
                ("malformed source mask", d => One(d, "Mask").Value = "%%%", "big_endian"),
                ("DCA requirement", d => One(d, "Static").Add(new XElement(d.Root!.Name.Namespace + "Extension", new XElement(d.Root.Name.Namespace + "DCA"))), "big_endian"),
                ("unknown unselected overlap", d => d.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == "P2").Elements().Single().SetAttributeValue("Offset", "0"), "big_endian"),
                ("selected assignment dependency", d => One(d, "Dynamic").Add(new XElement(d.Root!.Name.Namespace + "Assign", new XAttribute("TargetParamRefRef", "R1"), new XAttribute("Value", "2"))), "big_endian"),
                ("unaligned LittleEndian", d => d.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == "P1").Elements().Single().SetAttributeValue("BitOffset", "1"), "little_endian"),
            };
            foreach (var item in negative)
            {
                var document = Application(); item.Change(document); var input = Fixture(root, Guid.NewGuid().ToString("N"), document);
                var inputHash = SHA256.HashData(File.ReadAllBytes(input)); await Tool("project_open", new { path = input });
                var unchanged = Knxproj.ToXml(server.Project!).ToString(); var undo = Data(await Tool("project_info", new { })).ToJsonString();
                var unchangedPlan = Data(await Plan()).ToJsonString(); var rejected = Data(await Plan(Selection(order: item.Order)))["offlineParameterPatch"]!;
                check("subset tool rejects " + item.Name + " while retaining prior plan and undo", rejected["available"]?.GetValue<bool>() == false
                    && rejected["binding"] is null && rejected["ranges"]?.AsArray().Count == 0 && rejected["fields"]?.AsArray().Count == 0
                    && rejected["issues"]?.AsArray().Count > 0 && Data(await Plan()).ToJsonString() == unchangedPlan
                    && Knxproj.ToXml(server.Project!).ToString() == unchanged && Data(await Tool("project_info", new { })).ToJsonString() == undo
                    && SHA256.HashData(File.ReadAllBytes(input)).SequenceEqual(inputHash), rejected.ToJsonString());
            }
            var absentMask = Application(); One(absentMask, "Mask").Remove(); var absentInput = Fixture(root, "absent-mask", absentMask);
            await Tool("project_open", new { path = absentInput }); var nullable = Data(await Plan(Selection()))["offlineParameterPatch"]!;
            check("an absent initial mask remains null rather than invented all-ones bytes", nullable["available"]?.GetValue<bool>() == true
                && nullable["ranges"]?[0]?["beforeMask"] is null && nullable["ranges"]?[0]?["afterMask"] is null
                && (string?)nullable["ranges"]?[0]?["fieldMask"] == "FFFF", nullable.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(root, true); }
    }

    private static XElement One(XDocument document, string name) => document.Descendants().Single(e => e.Name.LocalName == name);
    private static XDocument Application()
    {
        var data = new byte[4096]; data[0] = 0xA5; data[1] = 0x5A;
        var mask = new byte[4096]; mask[0] = 1; mask[1] = 128;
        return XDocument.Parse($$"""
            <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code>
            <AbsoluteSegment Id="AS" Address="4096" Size="4096"><Data>{{Convert.ToBase64String(data)}}</Data><Mask>{{Convert.ToBase64String(mask)}}</Mask></AbsoluteSegment>
            </Code><ParameterTypes><ParameterType Id="N16" Name="Word"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="65535"/></ParameterType>
            <ParameterType Id="N8" Name="Byte"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P1" ParameterType="N16" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
            <Parameter Id="P2" ParameterType="N8" Value="0"><Memory CodeSegment="AS" Offset="2" BitOffset="0"/></Parameter></Parameters>
            <ParameterRefs><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/></ParameterRefs>
            </Static><Dynamic><ParameterRefRef RefId="R1"/><ParameterRefRef RefId="R2"/></Dynamic></ApplicationProgram></KNX>
            """);
    }
    private static string Fixture(string root, string name, XDocument definition)
    {
        var path = Path.Combine(root, name + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Synthetic subset preview", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE1/" + App + ".xml").Open(); definition.Save(output);
        return path;
    }
}
