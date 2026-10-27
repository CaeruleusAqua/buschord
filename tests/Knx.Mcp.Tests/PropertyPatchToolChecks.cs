using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class PropertyPatchToolChecks
{
    private const string ApplicationXml = """
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-FFE8"><ApplicationPrograms>
          <ApplicationProgram Id="M-FFE8_A-resource" ApplicationNumber="1" ApplicationVersion="1" ProgramType="ApplicationProgram"
            MaskVersion="MV-0701" Name="Synthetic property application" LoadProcedureStyle="DefaultProcedure" PeiType="0"
            DefaultLanguage="en-US" DynamicTableManagement="false" Linkable="false">
            <Static><ParameterTypes><ParameterType Id="T" Name="Three bits"><TypeNumber SizeInBit="3" Type="unsignedInt" minInclusive="0" maxInclusive="7"/></ParameterType></ParameterTypes>
              <Parameters><Parameter Id="P" Name="P" Text="Three bits" ParameterType="T" Value="0"><Property ObjectIndex="3" PropertyId="10" Offset="2" BitOffset="3"/></Parameter></Parameters>
              <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static>
            <Dynamic><ChannelIndependentBlock><ParameterBlock Id="PB"><ParameterRefRef RefId="R"/></ParameterBlock></ChannelIndependentBlock></Dynamic>
          </ApplicationProgram>
        </ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
        """;
    // Independent literal container: one complete property Data buffer and its separate BE count.
    private const string FactoryFrame = "0100010002000000040000000000000003000A000400000003AABBE3CCFFFFFFFF04000000FEFFFFFF03000A0002000000030002FFFF";

    public static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-mcp-property-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer();
            var sequence = 34000;
            async Task<JsonNode> Raw(string arguments) => JsonNode.Parse((await server.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            async Task<JsonNode> Tool(string name, object arguments) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments },
            })))!)!;
            static bool Error(JsonNode response) => response["error"] is not null || response["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode response) => response["result"]!["structuredContent"]!;
            static JsonNode Patch(JsonNode response) => Data(response)["offlineParameterPatch"]!;
            object Selection(bool property = true, bool resource = false, string value = "5") => new
            {
                device_address = "1.1.1", property_preview = property, resource_preview = resource, byte_order = "big_endian",
                parameter_subset = new[] { new { refId = "R", rawValue = value } },
            };

            var path = Fixture(directory, "source");
            var archiveHash = SHA256.HashData(File.ReadAllBytes(path));
            check("property fixture opens", !Error(await Tool("project_open", new { path })), null);
            var model = Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting);
            var history = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            var schema = server.Tools.Single(t => t.Name == "download_plan").InputSchema;
            check("property option is a boolean on the existing 55-tool contract", server.Tools.Count == 55
                && (string?)schema["properties"]?["property_preview"]?["type"] == "boolean"
                && schema["allOf"]?[1]?["then"]?["required"]?.AsArray().Count == 2
                && schema["allOf"]?[1]?["then"]?["not"]?["properties"]?["resource_preview"]?["const"]?.GetValue<bool>() == true, null);
            check("property false without subset preserves the complete ordinary JSON", ordinary == Data(await Tool("download_plan", new
            { device_address = "1.1.1", property_preview = false })).ToJsonString(), null);
            var subsetOnly = Data(await Raw("""{"device_address":"1.1.1","byte_order":"big_endian","parameter_subset":[{"refId":"R","rawValue":"5"}]}""")).ToJsonString();
            check("property false preserves the complete existing subset JSON", subsetOnly == Data(await Tool("download_plan", Selection(false))).ToJsonString(), null);

            foreach (var (name, arguments, expected) in new[]
            {
                ("true without subset", """{"property_preview":true}""", "Subset-Vorschau"),
                ("true without order", """{"property_preview":true,"parameter_subset":[{"refId":"R","rawValue":"5"}]}""", "Subset-Vorschau"),
                ("string", """{"property_preview":"true"}""", "property_preview must"),
                ("null", """{"property_preview":null}""", "property_preview must"),
                ("number", """{"property_preview":1}""", "property_preview must"),
                ("duplicate", """{"property_preview":true,"property_preview":false}""", "property_preview must"),
                ("duplicate false", """{"property_preview":false,"property_preview":false}""", "property_preview must"),
                ("conflicting backends", """{"property_preview":true,"resource_preview":true}""", "cannot both be true"),
            })
            {
                // An invalid gateway proves these errors are reported before descriptor/session access.
                var rejected = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-property-gateway\"," + arguments[1..]);
                check("property request rejects " + name + " before gateway access", Error(rejected)
                    && rejected.ToJsonString().Contains(expected, StringComparison.Ordinal)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }

            var response = await Tool("download_plan", Selection());
            check("explicit property patch is available", !Error(response) && Patch(response)["available"]?.GetValue<bool>() == true, response.ToJsonString());
            if (!Error(response) && Patch(response)["available"]?.GetValue<bool>() == true)
            {
                var patch = Patch(response);
                var range = patch["ranges"]!.AsArray().Single()!;
                var image = patch["properties"]!.AsArray().Single()!;
                check("property range retains literal byte geometry and unrelated bits", (string?)range["beforeData"] == "E3"
                    && (string?)range["afterData"] == "F7" && (string?)range["fieldMask"] == "1C"
                    && range["byteOffsetInPropertyData"]?.GetValue<uint>() == 2
                    && (string?)range["beforeMask"] == "FF" && (string?)range["afterMask"] == "FF", range.ToJsonString());
                check("property count is separate and unchanged", (string?)image["countData"] == "0002"
                    && (string?)image["countMask"] == "FFFF" && image["encodedCount"]?.GetValue<ushort>() == 2
                    && (string?)image["beforeDataHash"] == Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString("AABBE3CC")))
                    && (string?)image["afterDataHash"] == Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString("AABBF7CC"))), image.ToJsonString());
                check("property result cannot authorize execution or download", patch["completeApplicationImage"]?.GetValue<bool>() == false
                    && patch["downloadEligible"]?.GetValue<bool>() == false && patch["executionEligible"]?.GetValue<bool>() == false
                    && patch["readOnly"]?.GetValue<bool>() == true && Data(response)["realDownloadAvailable"]?.GetValue<bool>() == false
                    && Data(response)["writesEnabled"]?.GetValue<bool>() == false && Data(response)["plan"]?["ready"]?.GetValue<bool>() == false
                    && Data(response)["plan"]?["program"] is null, response.ToJsonString());
                check("complete property buffers are compact hashes in MCP", image["beforeData"] is null && image["afterData"] is null
                    && image["payloadLength"]?.GetValue<int>() == 4 && patch["binding"]?["fingerprint"]?.GetValue<string>() is { Length: 64 }, image.ToJsonString());
                check("property response is deterministic", patch.ToJsonString() == Patch(await Tool("download_plan", Selection())).ToJsonString(), null);
            }
            var invalidValue = Patch(await Tool("download_plan", Selection(value: "8")));
            check("property invalid value refuses all patch artifacts", invalidValue["available"]?.GetValue<bool>() == false
                && invalidValue["binding"] is null && invalidValue["fields"]?.AsArray().Count == 0
                && invalidValue["ranges"]?.AsArray().Count == 0 && invalidValue["properties"]?.AsArray().Count == 0, invalidValue.ToJsonString());
            check("property requests preserve model, saved values, undo and archive bytes", model == Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting)
                && history == Data(await Tool("project_info", new { })).ToJsonString() && archiveHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                && ordinary == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString(), null);

            var manyPath = Fixture(directory, "many-properties", extraProperties: 129);
            await Tool("project_open", new { path = manyPath });
            var many = Patch(await Tool("download_plan", Selection()));
            var manySources = many["propertySources"]!;
            check("property source projection reports its entry cap without truncating the actual patch", many["available"]?.GetValue<bool>() == true
                && manySources["entriesTotal"]?.GetValue<int>() == 130 && manySources["entriesShown"]?.GetValue<int>() == 128
                && manySources["entriesTruncated"]?.GetValue<bool>() == true && manySources["entries"]?.AsArray().Count == 128
                && many["properties"]?.AsArray().Count == 1 && many["binding"]?["sourceIndexFingerprint"]?.GetValue<string>() is { Length: 64 }, many.ToJsonString());
            var fragmentsPath = Fixture(directory, "many-fragments", extraFragments: 33);
            await Tool("project_open", new { path = fragmentsPath });
            var fragmented = Patch(await Tool("download_plan", Selection()));
            var fragmentedSource = fragmented["propertySources"]!["entries"]!.AsArray().Single(entry => entry!["fragmentsTotal"]?.GetValue<int>() == 33)!;
            check("property source projection reports its fragment cap with full-source binding", fragmented["available"]?.GetValue<bool>() == true
                && fragmentedSource["fragmentsShown"]?.GetValue<int>() == 32 && fragmentedSource["fragmentsTruncated"]?.GetValue<bool>() == true
                && fragmentedSource["dataFragments"]?.AsArray().Count == 32
                && fragmented["binding"]?["sourceIndexFingerprint"]?.GetValue<string>() is { Length: 64 }, fragmented.ToJsonString());

            var memoryPath = ResourcePatchToolChecks.Fixture(directory, "memory", XDocument.Parse(ResourcePatchToolChecks.ApplicationXml));
            await Tool("project_open", new { path = memoryPath });
            const string memory = """{"device_address":"1.1.1","resource_preview":true,"byte_order":"big_endian","parameter_subset":[{"refId":"R-relative4","rawValue":"4660"}]}""";
            var existingResource = Data(await Raw(memory)).ToJsonString();
            check("property false preserves the complete resource backend JSON", existingResource == Data(await Raw(memory[..^1] + ",\"property_preview\":false}")).ToJsonString(), null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog);
            Directory.Delete(directory, true);
        }
    }

    private static string Fixture(string directory, string name, int extraProperties = 0, int extraFragments = 0)
    {
        var path = ResourcePatchToolChecks.Fixture(directory, name, XDocument.Parse(ApplicationXml));
        var raw = Convert.FromHexString(FactoryFrame);
        if (extraProperties != 0 || extraFragments != 0)
        {
            using var frame = new MemoryStream();
            using var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true);
            writer.Write(0x00010001u); writer.Write(2 + extraProperties + extraFragments); writer.Write(raw[8..]);
            for (var i = 0; i < extraProperties + extraFragments; i++)
            {
                writer.Write(4u); writer.Write(extraProperties != 0 ? 0u : (uint)i);
                writer.Write((ushort)3); writer.Write((ushort)(11 + (extraProperties != 0 ? i : 0)));
                writer.Write(1); writer.Write((byte)3); writer.Write((byte)0); writer.Write((byte)255);
            }
            raw = frame.ToArray();
        }
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.NoCompression, leaveOpen: true))
            deflate.Write(raw);
        var master = """
            <KNX xmlns="http://knx.org/xml/project/20"><MasterData><MaskVersions><MaskVersion Id="MV-0701" MaskVersion="1793">
              <HawkConfigurationData><Features><Feature Name="FirstAppObjectIdx" Value="5"/></Features><InterfaceObjects>
                <InterfaceObject Index="3" ObjectType="123"><Property PropertyID="10" PropertyDataType="PDT_UNSIGNED_INT"/></InterfaceObject>
              </InterfaceObjects></HawkConfigurationData>
            </MaskVersion></MaskVersions></MasterData></KNX>
            """;
        var hardware = $$"""
            <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-FFE8"><Hardware><Hardware Id="M-FFE8_H-test"><Hardware2Programs>
              <Hardware2Program Id="M-FFE8_H-test_I-1_A-resource" LoadedImage="{{Convert.ToBase64String(compressed.ToArray())}}"><ApplicationProgramRef RefId="M-FFE8_A-resource"/></Hardware2Program>
            </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var (entry, text) in new[] { ("knx_master.xml", master), ("M-FFE8/Hardware.xml", hardware) })
        {
            zip.GetEntry(entry)!.Delete();
            using var output = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            output.Write(text);
        }
        return path;
    }
}
