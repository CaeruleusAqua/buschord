using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class ResourcePatchToolChecks
{
    internal const string AppId = "M-FFE8_A-resource";
    internal const string ApplicationXml = """
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-FFE8"><ApplicationPrograms>
          <ApplicationProgram Id="M-FFE8_A-resource" ApplicationNumber="1" ApplicationVersion="1" ProgramType="ApplicationProgram"
            MaskVersion="MV-0701" Name="Synthetic resource application" LoadProcedureStyle="DefaultProcedure" PeiType="0"
            DefaultLanguage="en-US" DynamicTableManagement="false" Linkable="false">
            <Static><Code>
              <AbsoluteSegment Id="ABS" Address="4" Size="8"><Data>ECAwQAAAAAA=</Data></AbsoluteSegment>
              <RelativeSegment Id="REL4" Offset="4" Size="8" LoadStateMachine="4"><Data>qrvM3QAAAAA=</Data></RelativeSegment>
              <RelativeSegment Id="REL0" Offset="4" Size="8" LoadStateMachine="0"><Data>ESIzRAAAAAA=</Data></RelativeSegment>
            </Code><ParameterTypes><ParameterType Id="T" Name="Unsigned word"><TypeNumber SizeInBit="16" Type="unsignedInt" minInclusive="0" maxInclusive="65535" /></ParameterType></ParameterTypes>
            <Parameters>
              <Parameter Id="P-relative4" Name="Relative4" Text="Relative four" ParameterType="T" Value="0"><Memory CodeSegment="REL4" Offset="2" BitOffset="0" /></Parameter>
              <Parameter Id="P-relative0" Name="Relative0" Text="Relative zero" ParameterType="T" Value="0"><Memory CodeSegment="REL0" Offset="2" BitOffset="0" /></Parameter>
              <Parameter Id="P-absolute" Name="Absolute" Text="Absolute" ParameterType="T" Value="0"><Memory CodeSegment="ABS" Offset="2" BitOffset="0" /></Parameter>
            </Parameters><ParameterRefs>
              <ParameterRef Id="R-relative4" RefId="P-relative4" /><ParameterRef Id="R-relative0" RefId="P-relative0" /><ParameterRef Id="R-absolute" RefId="P-absolute" />
            </ParameterRefs></Static>
            <Dynamic><ChannelIndependentBlock><ParameterBlock Id="PB"><ParameterRefRef RefId="R-relative4" /><ParameterRefRef RefId="R-relative0" /><ParameterRefRef RefId="R-absolute" /></ParameterBlock></ChannelIndependentBlock></Dynamic>
          </ApplicationProgram>
        </ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
        """;

    public static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-mcp-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "catalog"));
        try
        {
            var path = Fixture(root, "source", XDocument.Parse(ApplicationXml));
            var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
            await using var server = new McpServer();
            var sequence = 24000;
            async Task<JsonNode> Tool(string name, object? arguments = null)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = name, ["arguments"] = JsonSerializer.SerializeToNode(arguments ?? new { }) } };
                return JsonNode.Parse((await server.HandleAsync(request.ToJsonString()))!)!;
            }
            async Task<JsonNode> RawPlan(string arguments) => JsonNode.Parse((await server.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            static bool Error(JsonNode response) => response["error"] is not null || response["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode response) => response["result"]!["structuredContent"]!;
            static JsonNode Preview(JsonNode response) => Data(response)["offlineParameterPatch"]!;
            object Selection(string reference = "R-relative4", string raw = "4660", string order = "big_endian", bool resource = true)
                => new { device_address = "1.1.1", resource_preview = resource, byte_order = order,
                    parameter_subset = new[] { new { refId = reference, rawValue = raw } } };
            var opened = await Tool("project_open", new { path });
            check("resource preview fixture opens", !Error(opened), opened.ToJsonString());
            var before = Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting);
            var beforeInfo = Data(await Tool("project_info")).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            var schema = server.Tools.Single(tool => tool.Name == "download_plan").InputSchema;
            check("resource mode is an explicit optional boolean on the existing tool", server.Tools.Count == McpToolContract.ExpectedToolCount
                && (string?)schema["properties"]?["resource_preview"]?["type"] == "boolean"
                && schema["allOf"]?[0]?["then"]?["required"]?.AsArray().Count == 2, null);
            check("false without a subset preserves the ordinary plan",
                Data(await Tool("download_plan", new { device_address = "1.1.1", resource_preview = false })).ToJsonString() == ordinary, null);

            var oldAbsolute = await Tool("download_plan", new { device_address = "1.1.1", byte_order = "big_endian",
                parameter_subset = new[] { new { refId = "R-absolute", rawValue = "4660" } } });
            var falseAbsolute = await Tool("download_plan", Selection("R-absolute", resource: false));
            check("explicit false preserves the entire legacy preview", Preview(oldAbsolute)["available"]?.GetValue<bool>() == true
                && Preview(oldAbsolute).ToJsonString() == Preview(falseAbsolute).ToJsonString()
                && Preview(falseAbsolute)["backendVersion"] is null, falseAbsolute.ToJsonString());
            var oldRelative = Preview(await Tool("download_plan", Selection(resource: false)));
            check("legacy mode does not silently admit relative memory", oldRelative["available"]?.GetValue<bool>() == false, oldRelative.ToJsonString());

            var big = await Tool("download_plan", Selection());
            var preview = Preview(big); var range = preview["ranges"]!.AsArray().Single()!; var image = preview["resources"]!.AsArray().Single()!;
            check("resource preview encodes in the declared LSM arena", !Error(big) && preview["available"]?.GetValue<bool>() == true
                && (string?)preview["scope"] == "ExplicitResourceSubsetPreview" && preview["binding"]?["profileVersion"]?.GetValue<int>() == 1
                && (string?)range["arena"]?["kind"] == "relative_memory" && range["arena"]?["loadStateMachine"]?.GetValue<int>() == 4
                && range["byteOffsetInArena"]?.GetValue<uint>() == 6 && range["byteOffsetInResource"]?.GetValue<uint>() == 2
                && (string?)range["beforeData"] == "CCDD" && (string?)range["afterData"] == "1234"
                && (string?)range["fieldMask"] == "FFFF" && range["beforeMask"] is null && range["afterMask"] is null, big.ToJsonString());
            check("resource projection exposes hashes rather than complete buffers", image["payloadLength"]?.GetValue<int>() == 8
                && image["baseOffsetInArena"]?.GetValue<uint>() == 4 && (string?)image["declaredSizeCoverage"] == "matches_declared_size"
                && (string?)image["beforeDataHash"] == Convert.ToHexStringLower(SHA256.HashData(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0, 0, 0, 0 }))
                && (string?)image["afterDataHash"] == Convert.ToHexStringLower(SHA256.HashData(new byte[] { 0xAA, 0xBB, 0x12, 0x34, 0, 0, 0, 0 }))
                && image["beforeData"] is null && image["afterData"] is null && image["beforeMaskHash"] is null && image["afterMaskHash"] is null, image.ToJsonString());
            check("resource success retains all device-image and download gates", preview["completeApplicationImage"]?.GetValue<bool>() == false
                && preview["downloadEligible"]?.GetValue<bool>() == false && preview["readOnly"]?.GetValue<bool>() == true
                && preview["unresolvedRequirements"]?.AsArray().Count > 0 && !big.ToJsonString().Contains("\"writesEnabled\":true", StringComparison.Ordinal), null);
            var sources = preview["imageSources"]!;
            check("resource opt-in includes independently bound archive source declarations", (string?)sources["status"] == "inspected"
                && (string?)sources["resourceContext"]?["status"] == "supported"
                && sources["resourceContext"]?["resourceCount"]?.GetValue<int>() == 2
                && sources["binding"]?["fingerprint"]?.GetValue<string>() is { Length: 64 }
                && sources["resourceContext"]?["archiveMasterHash"]?.GetValue<string>() is { Length: 64 }, sources.ToJsonString());
            check("source projection separates native selectors, declarations and image locations", (string?)sources["resourceContext"]?["resources"]?[0]?["location"]?["addressSpace"] == "system_property"
                && sources["resourceContext"]?["resources"]?[0]?["location"]?["nativeObjectIndex"]?.GetValue<int>() == 4
                && sources["resourceContext"]?["resources"]?[0]?["location"]?["applicationObjectIndex"] is null
                && sources["resourceContext"]?["resources"]?[0]?["imageLocation"] is null, sources.ToJsonString());
            var factory = sources["hardware"]?["factoryImage"];
            check("factory source projection retains hashes and typed geometry without full buffers", (string?)sources["hardware"]?["status"] == "inspected"
                && (string?)factory?["status"] == "decoded" && factory?["segmentCount"]?.GetValue<int>() == 1
                && (string?)factory?["segments"]?[0]?["location"]?["addressSpace"] == "system_property"
                && factory?["segments"]?[0]?["byteLength"]?.GetValue<int>() == 2
                && factory?["segments"]?[0]?["dataHash"]?.GetValue<string>() is { Length: 64 }
                && factory?["segments"]?[0]?["data"] is null && factory?["segments"]?[0]?["mask"] is null
                && factory?["stampVerified"]?.GetValue<bool>() == false
                && sources["hardware"]?["checksumLength"]?.GetValue<int>() == 2, sources.ToJsonString());
            check("archive sources do not upgrade image, count or stamp readiness", sources["completeApplicationImage"]?.GetValue<bool>() == false
                && sources["downloadEligible"]?.GetValue<bool>() == false
                && sources["unresolvedRequirements"]!.AsArray().Any(x => (string?)x == "property_initialization_and_count_policy")
                && sources["unresolvedRequirements"]!.AsArray().Any(x => (string?)x == "factory_stamp_unverified"), sources.ToJsonString());
            var little = Preview(await Tool("download_plan", Selection(order: "little_endian")));
            check("resource numeric endian is explicit and bound", (string?)little["ranges"]?[0]?["afterData"] == "3412"
                && (string?)little["binding"]?["fingerprint"] != (string?)preview["binding"]?["fingerprint"], little.ToJsonString());
            var two = Preview(await Tool("download_plan", new { device_address = "1.1.1", byte_order = "big_endian", resource_preview = true,
                parameter_subset = new[] { new { refId = "R-relative4", rawValue = "4660" }, new { refId = "R-relative0", rawValue = "22136" } } }));
            check("identical offsets in different LSMs remain distinct logical resources", two["available"]?.GetValue<bool>() == true
                && two["ranges"]?.AsArray().Count == 2 && two["resources"]?.AsArray().Count == 2
                && two["ranges"]!.AsArray().Select(item => item!["arena"]!["loadStateMachine"]!.GetValue<int>()).Order().SequenceEqual(new[] { 0, 4 }), two.ToJsonString());
            foreach (var refs in new[]
            {
                new[] { new { refId = "R-relative4", rawValue = "4660" }, new { refId = "R-absolute", rawValue = "4660" } },
                new[] { new { refId = "R-relative4", rawValue = "4660" }, new { refId = "missing", rawValue = "0" } },
                new[] { new { refId = "R-relative4", rawValue = "4660" }, new { refId = "R-relative0", rawValue = "65536" } },
            })
            {
                var rejected = Preview(await Tool("download_plan", new { device_address = "1.1.1", byte_order = "big_endian", resource_preview = true, parameter_subset = refs }));
                check("resource selection failures publish no partial artifacts", rejected["available"]?.GetValue<bool>() == false
                    && rejected["binding"] is null && rejected["fields"]?.AsArray().Count == 0 && rejected["ranges"]?.AsArray().Count == 0
                    && rejected["resources"]?.AsArray().Count == 0 && rejected["issues"]?.AsArray().Count > 0, rejected.ToJsonString());
            }
            foreach (var invalid in new[]
            {
                """{"device_address":"1.1.1","resource_preview":true}""",
                """{"device_address":"1.1.1","resource_preview":"true","byte_order":"big_endian","parameter_subset":[{"refId":"R-relative4","rawValue":"1"}]}""",
                """{"device_address":"1.1.1","resource_preview":null,"byte_order":"big_endian","parameter_subset":[{"refId":"R-relative4","rawValue":"1"}]}""",
                """{"device_address":"1.1.1","resource_preview":true,"resource_preview":false,"byte_order":"big_endian","parameter_subset":[{"refId":"R-relative4","rawValue":"1"}]}""",
            }) check("resource option validation rejects malformed requests", Error(await RawPlan(invalid)), invalid);

            var device = ProjectQuery.DeviceList(server.Project!).Single();
            device.ParameterValues["R-relative0"] = "1";
            var changedBinding = Preview(await Tool("download_plan", Selection()));
            check("resource binding observes saved-state changes even outside the selection",
                (string?)changedBinding["binding"]?["fingerprint"] != (string?)preview["binding"]?["fingerprint"], changedBinding.ToJsonString());
            device.ParameterValues.Remove("R-relative0");
            check("all previews preserve source data, model, undo and ordinary preflight", SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(sourceHash)
                && Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting) == before
                && Data(await Tool("project_info")).ToJsonString() == beforeInfo
                && Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString() == ordinary, null);
            var largePath = Fixture(root, "source-large-manifest", XDocument.Parse(ApplicationXml), 65, 33);
            check("large source inventory fixture opens", !Error(await Tool("project_open", new { path = largePath })), null);
            var largeSources = Preview(await Tool("download_plan", Selection()))["imageSources"]!;
            check("source presentation limits retain complete counts and explicit truncation", largeSources["resourceContext"]?["resourceCount"]?.GetValue<int>() == 65
                && largeSources["resourceContext"]?["resources"]?.AsArray().Count == 64
                && largeSources["resourceContext"]?["resourcesTruncated"]?.GetValue<bool>() == true
                && largeSources["hardware"]?["factoryImage"]?["segmentCount"]?.GetValue<int>() == 33
                && largeSources["hardware"]?["factoryImage"]?["segments"]?.AsArray().Count == 32
                && largeSources["hardware"]?["factoryImage"]?["segmentsTruncated"]?.GetValue<bool>() == true
                && largeSources["completeApplicationImage"]?.GetValue<bool>() == false, largeSources.ToJsonString());
        }
        catch (Exception ex) { check("MCP resource preview integration completes", false, ex.ToString()); }
        finally
        {
            Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog);
            Directory.Delete(root, true);
        }
    }

    internal static string Fixture(string root, string name, XDocument application, int sourceResources = 2, int factorySegments = 1)
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic resource project", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new DeviceInstance
        {
            Id = "D-resource", ProductRefId = AppId, Hardware2ProgramRefId = "M-FFE8_H-test_I-1_A-resource",
            Address = new(1, 1, 1), Name = "Synthetic device",
        });
        var path = Path.Combine(root, name + ".knxproj");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var rawFactory = Convert.FromHexString("0100010001000000040000000700000004001B0002000000031234FFFF");
        if (factorySegments != 1)
        {
            var record = rawFactory[8..];
            using var factoryRaw = new MemoryStream(); using var factoryWriter = new BinaryWriter(factoryRaw);
            factoryWriter.Write(0x00010001u); factoryWriter.Write(factorySegments);
            for (var i = 0; i < factorySegments; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)(7 + i * 2));
                factoryWriter.Write(record);
            }
            rawFactory = factoryRaw.ToArray();
        }
        var factory = new byte[rawFactory.Length + 5]; factory[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(factory.AsSpan(1), (ushort)rawFactory.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(factory.AsSpan(3), (ushort)~rawFactory.Length);
        rawFactory.CopyTo(factory, 5);
        var master = """
            <KNX xmlns="http://knx.org/xml/project/20"><MasterData><MaskVersions><MaskVersion Id="MV-0701" MaskVersion="1793">
              <HawkConfigurationData><Features><Feature Name="FirstAppObjectIdx" Value="6"/></Features><Resources>
                <Resource Name="ApplicationDataPtr" Access="remote"><Location AddressSpace="SystemProperty" StartAddress="0" InterfaceObjectRef="4" PropertyID="7"/><ResourceType Length="4"/></Resource>
                <Resource Name="ApplicationData" Access="remote"><Location AddressSpace="RelativeMemory" StartAddress="0" InterfaceObjectRef="4"/><ResourceType Length="8"/></Resource>
              </Resources></HawkConfigurationData></MaskVersion></MaskVersions></MasterData></KNX>
            """;
        if (sourceResources > 2)
        {
            var document = XDocument.Parse(master); var ns = document.Root!.Name.Namespace;
            var resources = document.Descendants(ns + "Resources").Single();
            for (var i = 2; i < sourceResources; i++) resources.Add(new XElement(ns + "Resource", new XAttribute("Name", "Extra" + i),
                new XAttribute("Access", "remote"), new XElement(ns + "Location", new XAttribute("AddressSpace", "None"))));
            master = document.ToString();
        }
        var hardware = $$"""
            <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-FFE8"><Hardware><Hardware Id="M-FFE8_H-test"><Hardware2Programs>
              <Hardware2Program Id="M-FFE8_H-test_I-1_A-resource" LoadedImage="{{Convert.ToBase64String(factory)}}" CheckSums="AQI="><ApplicationProgramRef RefId="{{AppId}}"/></Hardware2Program>
            </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """;
        foreach (var (entryName, text) in new[] { ("0.xml", Knxproj.ToXml(project).ToString()), ("M-FFE8/" + AppId + ".xml", application.ToString()),
            ("knx_master.xml", master), ("M-FFE8/Hardware.xml", hardware) })
        {
            using var output = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
            output.Write(text);
        }
        return path;
    }
}
