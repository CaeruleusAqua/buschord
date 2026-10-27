using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using Knx.Workbench;

internal static class ApplicationBuildToolChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string ValidOptions = "{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false}";

    private static XDocument Application(int? restartCount = null)
    {
        var document = XDocument.Parse(ResourcePatchToolChecks.ApplicationXml);
        var app = document.Descendants(Ns + "ApplicationProgram").Single(); app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        var stat = app.Element(Ns + "Static")!; var code = stat.Element(Ns + "Code")!; code.RemoveNodes();
        foreach (var (id, address) in new[] { ("ABS", 100), ("REL4", 200), ("REL0", 300) })
            code.Add(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Address", address), new XAttribute("Size", 16),
                new XElement(Ns + "Data", Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()))));
        stat.Add(new XElement(Ns + "AddressTable", new XAttribute("CodeSegment", "ABS"), new XAttribute("Offset", 0), new XAttribute("MaxEntries", 10)),
            new XElement(Ns + "AssociationTable", new XAttribute("CodeSegment", "REL4"), new XAttribute("Offset", 0), new XAttribute("MaxEntries", 10)),
            new XElement(Ns + "ComObjectTable", new XAttribute("CodeSegment", "REL0"), new XAttribute("Offset", 0)));
        var procedure = new XElement(Ns + "LoadProcedure");
        if (restartCount is { } count) procedure.Add(Enumerable.Range(0, count).Select(_ => new XElement(Ns + "LdCtrlRestart")));
        else procedure.Add(new XElement(Ns + "LdCtrlConnect"),
            new XElement(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27), new XAttribute("Verify", false),
                new XAttribute("InlineData", "0102030405060708090A")),
            new XElement(Ns + "LdCtrlRestart", new XAttribute("AppliesTo", "par")), new XElement(Ns + "LdCtrlDisconnect"));
        stat.Add(new XElement(Ns + "LoadProcedures", procedure));
        return document;
    }

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-mcp-application-build-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 135000;
            async Task<JsonNode> Tool(string name, object arguments)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } });
                return JsonNode.Parse((await server.HandleAsync(request))!)!;
            }
            async Task<JsonNode> RawPlan(string arguments) => JsonNode.Parse((await server.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            static bool Error(JsonNode response) => response["error"] is not null || response["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode response) => response["result"]?["structuredContent"] ?? throw new InvalidOperationException(response.ToJsonString());
            async Task<JsonNode> Preview(bool full = true, bool parameters = false, bool groups = false) => Data(await Tool("download_plan", new
            {
                device_address = "1.1.1", load_procedure_preview = new { subtype = "all", access = "remote", full, parameters, groups },
            }));
            static bool Gates(JsonNode response) => response["writesEnabled"]?.GetValue<bool>() == false && response["realDownloadAvailable"]?.GetValue<bool>() == false
                && response["plan"]?["ready"]?.GetValue<bool>() == false && response["plan"]?["program"] is null && response["plan"]?["imageBytes"]?.GetValue<int>() == 0
                && response["plan"]?["chunks"]?.AsArray().Count == 0 && response["offlineApplicationBuild"]?["executionEligible"]?.GetValue<bool>() == false
                && response["offlineApplicationBuild"]?["completeApplicationImage"]?.GetValue<bool>() == false && response["offlineApplicationBuild"]?["downloadEligible"]?.GetValue<bool>() == false;

            var path = ResourcePatchToolChecks.Fixture(directory, "application-build", Application()); var original = File.ReadAllBytes(path);
            var opened = await Tool("project_open", new { path }); check("load build fixture opens", !Error(opened), opened.ToJsonString());
            var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single();
            var originalXml = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting); var originalRevision = project.ApplicationDataRevision;
            var originalInfo = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })); var ordinaryJson = ordinary.ToJsonString();
            var falseResource = Data(await Tool("download_plan", new { device_address = "1.1.1", resource_preview = false })).ToJsonString();
            object AbsoluteSubset(bool? resource = null) => resource is null
                ? new { device_address = "1.1.1", byte_order = "big_endian", parameter_subset = new[] { new { refId = "R-absolute", rawValue = "4660" } } }
                : (object)new { device_address = "1.1.1", byte_order = "big_endian", resource_preview = resource.Value, parameter_subset = new[] { new { refId = "R-absolute", rawValue = "4660" } } };
            var legacySubset = Data(await Tool("download_plan", AbsoluteSubset()));
            var explicitFalseSubset = Data(await Tool("download_plan", AbsoluteSubset(false)));
            check("absent load option preserves ordinary and false resource JSON", falseResource == ordinaryJson && ordinary["offlineApplicationBuild"] is null, ordinaryJson);
            check("false resource mode preserves the full legacy subset JSON", legacySubset.ToJsonString() == explicitFalseSubset.ToJsonString()
                && legacySubset["offlineParameterPatch"]?["available"]?.GetValue<bool>() == true, legacySubset.ToJsonString());

            var schema = server.Tools.Single(t => t.Name == "download_plan").InputSchema; var option = schema["properties"]!["load_procedure_preview"]!;
            check("load preview adds no tool and requires one explicit object", server.Tools.Count == 55 && (string?)option["type"] == "object"
                && option["additionalProperties"]?.GetValue<bool>() == false && option["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order().SequenceEqual(new[] { "access", "full", "groups", "parameters", "subtype" }), option.ToJsonString());
            check("all three schema flags are booleans", new[] { "full", "parameters", "groups" }.All(n => (string?)option["properties"]?[n]?["type"] == "boolean"), option.ToJsonString());
            var tableOption = option["properties"]!["generated_tables"]!;
            check("table format is an explicit optional selection with no inferred width", (string?)tableOption["type"] == "object"
                && tableOption["additionalProperties"]?.GetValue<bool>() == false
                && tableOption["required"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "format" })
                && tableOption["properties"]!["format"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "m112", "system_b_2", "system_b_4" })
                && !option["required"]!.AsArray().Any(n => (string?)n == "generated_tables"), tableOption.ToJsonString());
            var response = await Preview(); var build = response["offlineApplicationBuild"]!; var procedure = build["procedure"]!;
            var controls = procedure["controls"]!.AsArray(); var write = controls.Single(c => (string?)c?["kind"] == "write_property")!;
            JsonNode Argument(string name) => write["arguments"]!.AsArray().Single(a => (string?)a?["name"] == name)!;
            check("load preview works without a numeric subset", (string?)build["scope"] == "OfflineApplicationBuildPlan" && build["available"]?.GetValue<bool>() == true
                && response["offlineParameterPatch"] is null && build["readOnly"]?.GetValue<bool>() == true, response.ToJsonString());
            check("typed product controls retain order after full filtering", controls.Select(c => (string?)c?["kind"]).SequenceEqual(new[] { "connect", "write_property", "disconnect" })
                && controls.Select(c => c!["ordinal"]!.GetValue<int>()).SequenceEqual(new[] { 0, 1, 2 }) && (string?)procedure["style"] == "product_procedure", procedure.ToJsonString());
            check("write selectors and schema defaults are explicit typed arguments", Argument("ObjIdx")["unsignedValue"]?.GetValue<int>() == 4 && Argument("PropId")["unsignedValue"]?.GetValue<int>() == 27
                && Argument("StartElement")["unsignedValue"]?.GetValue<int>() == 1 && Argument("Count")["unsignedValue"]?.GetValue<int>() == 1
                && Argument("Verify")["booleanValue"]?.GetValue<bool>() == false && (string?)Argument("Verify")["kind"] == "boolean", write.ToJsonString());
            check("unmodified product auto retains declared and effective condition", (string?)write["declaredAppliesTo"] == "auto" && (string?)write["effectiveAppliesTo"] == "auto"
                && (string?)write["origin"] == "product" && write["sourceOrdinal"]?.GetValue<int>() == 1, write.ToJsonString());
            check("inline action data projects exact length and hash without full bytes", write["inlineDataLength"]?.GetValue<int>() == 10
                && (string?)write["inlineDataHash"] == Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString("0102030405060708090A")))
                && write["inlineData"] is null && write["inlineMask"] is null && !write.ToJsonString().Contains("0102030405060708090A", StringComparison.Ordinal), write.ToJsonString());
            var resources = procedure["resources"]!.AsArray(); var tables = procedure["tableRequirements"]!.AsArray();
            check("three table roles retain separate nominal resources and addresses", tables.Select(t => (string?)t?["role"]).SequenceEqual(new[] { "group_address", "association", "communication_object" })
                && tables.Select(t => t!["byteOffsetInArena"]!.GetValue<int>()).SequenceEqual(new[] { 100, 200, 300 })
                && tables.Select(t => (string?)t?["codeSegmentId"]).SequenceEqual(new[] { "ABS", "REL4", "REL0" }), tables.ToJsonString());
            check("source buffers are summarized without payload arrays", resources.Count == 3 && resources.All(r => r?["dataLength"]?.GetValue<int>() == 16
                && r["dataHash"]?.GetValue<string>() is { Length: 64 } && r["data"] is null && r["mask"] is null), resources.ToJsonString());
            check("source and declared project input hashes are separate", build["binding"]?["sourceFingerprint"]?.GetValue<string>() is { Length: 64 }
                && build["binding"]?["projectInputHash"]?.GetValue<string>() is { Length: 64 } && build["binding"]?["procedureWitnessHash"]?.GetValue<string>() is { Length: 64 }
                && (string?)build["binding"]?["sourceFingerprint"] == (string?)build["sources"]?["binding"]?["fingerprint"]
                && (string?)build["binding"]?["procedureWitnessHash"] == (string?)procedure["witnessHash"], build["binding"]!.ToJsonString());
            var core = OfflineApplicationBuildPlan.Preview(project, device, new("all", LoadProcedureAccess.Remote, LoadParts.Full));
            check("projection is bound to the complete Core procedure", core.IsAvailable && core.Binding?.Fingerprint == (string?)build["binding"]?["fingerprint"]
                && core.Procedure?.WitnessHash == (string?)procedure["witnessHash"] && core.Binding!.IsCurrent(project, device), null);
            check("successful symbolic preview leaves all device write gates closed", Gates(response) && response["plan"]!.ToJsonString() == ordinary["plan"]!.ToJsonString()
                && procedure["executionEligible"]?.GetValue<bool>() == false && procedure["completeApplicationImage"]?.GetValue<bool>() == false && procedure["downloadEligible"]?.GetValue<bool>() == false
                && procedure["unresolvedRequirements"]!.AsArray().Any(r => (string?)r?["code"] == "generated_tables_not_built"), response.ToJsonString());

            for (var bits = 0; bits < 8; bits++)
            {
                var full = (bits & 1) != 0; var parameters = (bits & 2) != 0; var groups = (bits & 4) != 0; var current = await Preview(full, parameters, groups);
                var graph = current["offlineApplicationBuild"]!; var expected = !full && parameters ? 4 : 3;
                check("explicit boolean FPG selection " + bits, graph["available"]?.GetValue<bool>() == true && graph["procedure"]?["controlCount"]?.GetValue<int>() == expected
                    && graph["request"]?["full"]?.GetValue<bool>() == full && graph["request"]?["parameters"]?.GetValue<bool>() == parameters && graph["request"]?["groups"]?.GetValue<bool>() == groups
                    && (string?)graph["binding"]?["sourceFingerprint"] == (string?)build["binding"]?["sourceFingerprint"] && Gates(current), graph.ToJsonString());
            }
            var parameterPlan = (await Preview(false, true))["offlineApplicationBuild"]!;
            check("part selection changes graph and witness with a stable archive source", parameterPlan["procedure"]?["controlCount"]?.GetValue<int>() == 4
                && (string?)parameterPlan["procedure"]?["witnessHash"] != (string?)procedure["witnessHash"] && (string?)parameterPlan["binding"]?["fingerprint"] != (string?)build["binding"]?["fingerprint"]
                && (string?)parameterPlan["binding"]?["sourceFingerprint"] == (string?)build["binding"]?["sourceFingerprint"]
                && (string?)parameterPlan["binding"]?["projectInputHash"] == (string?)build["binding"]?["projectInputHash"], parameterPlan.ToJsonString());

            device.ParameterValues["R-absolute"] = "2"; var savedValue = (await Preview())["offlineApplicationBuild"]!;
            check("saved values stale build inputs without changing source definitions", (string?)savedValue["binding"]?["projectInputHash"] != (string?)build["binding"]?["projectInputHash"]
                && (string?)savedValue["binding"]?["sourceFingerprint"] == (string?)build["binding"]?["sourceFingerprint"] && !core.Binding!.IsCurrent(project, device), savedValue.ToJsonString());
            device.ParameterValues.Remove("R-absolute");
            var range = new GroupRange { Id = "BuildRange", Name = "Synthetic build inputs", RangeStart = 0, RangeEnd = 65535 };
            var group = new GroupAddressEntry { Id = "BuildGroup", Name = "Synthetic group", Address = GroupAddress.ThreeLevel(1, 2, 3), DatapointType = "1.001" }; range.Addresses.Add(group);
            project.Installations.Single().MainRanges.Add(range);
            var obj = new ComObjectInstanceRef { Id = "BuildObject", ComObjectId = "BuildObject", Number = 7 }; device.ComObjectRefs.Add(obj);
            var noLink = (await Preview())["offlineApplicationBuild"]!; obj.Links.Add(group.Id);
            var withLink = (await Preview())["offlineApplicationBuild"]!;
            check("declared object links participate only in build input binding", (string?)noLink["binding"]?["projectInputHash"] != (string?)withLink["binding"]?["projectInputHash"]
                && (string?)noLink["binding"]?["sourceFingerprint"] == (string?)withLink["binding"]?["sourceFingerprint"], withLink.ToJsonString());
            var key = new ObjectKey(device.Id, obj.Id).ToString(); project.Workbench.Objects[key] = new() { SendingGroupId = group.Id, Dpt = "1.001", Bits = 1, Flags = "CTW" };
            project.Workbench.Objects[key].Connectors[group.Id] = new() { Send = true, Acknowledge = true, Priority = 1, Slot = 2 };
            var sending = (await Preview())["offlineApplicationBuild"]!;
            check("sending connector facts change the declared-input fingerprint", (string?)sending["binding"]?["projectInputHash"] != (string?)withLink["binding"]?["projectInputHash"]
                && (string?)sending["binding"]?["sourceFingerprint"] == (string?)build["binding"]?["sourceFingerprint"], sending.ToJsonString());
            group.Address = GroupAddress.ThreeLevel(1, 2, 4); var movedGroup = (await Preview())["offlineApplicationBuild"]!;
            check("group addresses change build input binding without fabricating tables", (string?)movedGroup["binding"]?["projectInputHash"] != (string?)sending["binding"]?["projectInputHash"]
                && movedGroup["procedure"]?["unresolvedRequirements"]!.AsArray().Any(r => (string?)r?["code"] == "generated_tables_not_built") == true, movedGroup.ToJsonString());
            project.Workbench.Objects.Remove(key); device.ComObjectRefs.Remove(obj); project.Installations.Single().MainRanges.Remove(range);
            check("restoring declared inputs reproduces the original build binding", (string?)(await Preview())["offlineApplicationBuild"]?["binding"]?["fingerprint"] == (string?)build["binding"]?["fingerprint"], null);

            var malformed = new List<string>
            {
                "false", "null", "\"true\"", "true", "[]", "0",
                "{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false}",
                "{\"subtype\":\"all\",\"access\":\"local2\",\"full\":true,\"parameters\":false,\"groups\":false}",
                "{\"subtype\":\"auto\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false}",
                "{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false,\"extra\":true}",
                "{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"full\":false,\"parameters\":false,\"groups\":false}",
            };
            foreach (var field in new[] { "full", "parameters", "groups" })
            foreach (var invalid in new[] { "null", "\"false\"", "0" })
            {
                var flags = new Dictionary<string, string> { ["full"] = "true", ["parameters"] = "false", ["groups"] = "false" }; flags[field] = invalid;
                malformed.Add("{\"subtype\":\"all\",\"access\":\"remote\",\"full\":" + flags["full"] + ",\"parameters\":" + flags["parameters"] + ",\"groups\":" + flags["groups"] + "}");
            }
            foreach (var missing in new[] { "full", "parameters" })
            {
                var incomplete = JsonNode.Parse(ValidOptions)!.AsObject(); incomplete.Remove(missing); malformed.Add(incomplete.ToJsonString());
            }
            foreach (var invalid in malformed)
            {
                var rejected = await RawPlan("{\"device_address\":\"1.1.1\",\"load_procedure_preview\":" + invalid + "}");
                check("malformed explicit load selection is a controlled ToolError " + invalid, rejected["result"]?["isError"]?.GetValue<bool>() == true
                    && rejected["error"] is null && rejected["result"]?["structuredContent"] is null && rejected.ToJsonString().Contains("load_procedure_preview", StringComparison.Ordinal), rejected.ToJsonString());
            }
            var duplicate = await RawPlan("{\"device_address\":\"1.1.1\",\"load_procedure_preview\":" + ValidOptions + ",\"load_procedure_preview\":" + ValidOptions + "}");
            check("duplicate top-level opt-in is rejected before source compilation", duplicate["result"]?["isError"]?.GetValue<bool>() == true && duplicate["result"]?["structuredContent"] is null, duplicate.ToJsonString());
            foreach (var invalid in new[]
            {
                "null", "true", "false", "0", "[]", "\"m112\"", "{}", "{\"format\":null}", "{\"format\":2}",
                "{\"format\":\"auto\"}", "{\"format\":\"system_b\"}", "{\"format\":\"m112\",\"extra\":true}",
                "{\"format\":\"m112\",\"format\":\"m112\"}",
            })
            {
                var selection = ValidOptions[..^1] + ",\"generated_tables\":" + invalid + "}";
                var rejected = await RawPlan("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-sentinel\",\"load_procedure_preview\":" + selection + "}");
                check("malformed table selection refuses before any gateway read " + invalid, rejected["result"]?["isError"]?.GetValue<bool>() == true
                    && rejected["result"]?["structuredContent"] is null && rejected.ToJsonString().Contains("load_procedure_preview", StringComparison.Ordinal)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }
            foreach (var format in new[] { "m112", "system_b_2", "system_b_4" })
            {
                var selection = JsonNode.Parse(ValidOptions)!;
                selection["full"] = false; selection["parameters"] = true;
                selection["generated_tables"] = new JsonObject { ["format"] = format };
                var rejected = await RawPlan("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-sentinel\",\"load_procedure_preview\":" + selection.ToJsonString() + "}");
                check("tables require explicit full or group load parts " + format, rejected["result"]?["isError"]?.GetValue<bool>() == true
                    && rejected["result"]?["structuredContent"] is null && rejected.ToJsonString().Contains("requires full or groups", StringComparison.Ordinal)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
                selection["full"] = true;
                var responseWithTables = await RawPlan("{\"device_address\":\"1.1.1\",\"load_procedure_preview\":" + selection.ToJsonString() + "}");
                check("an unsupported table profile is a bounded diagnostic " + format, !Error(responseWithTables), responseWithTables.ToJsonString());
                if (!Error(responseWithTables))
                {
                    var result = Data(responseWithTables); var attempted = result["offlineApplicationBuild"]!; var tableResult = attempted["generatedTables"]!;
                    check("unsupported profile emits no partial payloads or binding " + format, attempted["available"]?.GetValue<bool>() == false
                        && attempted["binding"] is null && tableResult["available"]?.GetValue<bool>() == false && tableResult["binding"] is null
                        && tableResult["payloadCount"]?.GetValue<int>() == 0 && tableResult["payloads"]?.AsArray().Count == 0
                        && tableResult["procedureSizeAdjustments"]?.AsArray().Count == 0 && tableResult["issues"]!.AsArray().Count > 0
                        && Gates(result) && tableResult["executionEligible"]?.GetValue<bool>() == false
                        && tableResult["completeApplicationImage"]?.GetValue<bool>() == false && tableResult["downloadEligible"]?.GetValue<bool>() == false,
                        attempted.ToJsonString());
                }
            }
            check("all diagnostics leave archive model history revision and ordinary JSON unchanged", original.SequenceEqual(File.ReadAllBytes(path))
                && originalXml == Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting) && project.ApplicationDataRevision == originalRevision
                && originalInfo == Data(await Tool("project_info", new { })).ToJsonString() && ordinaryJson == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString()
                && falseResource == Data(await Tool("download_plan", new { device_address = "1.1.1", resource_preview = false })).ToJsonString()
                && legacySubset.ToJsonString() == Data(await Tool("download_plan", AbsoluteSubset(false))).ToJsonString(), null);

            var largeApp = Application(600); var largePath = ResourcePatchToolChecks.Fixture(directory, "large-application-build", largeApp);
            check("large symbolic procedure fixture opens", !Error(await Tool("project_open", new { path = largePath })), null);
            var largeResponse = await Preview(); var large = largeResponse["offlineApplicationBuild"]!; var largeProcedure = large["procedure"]!;
            check("presentation reports complete counts and explicit truncation", large["available"]?.GetValue<bool>() == true && largeProcedure["controlCount"]?.GetValue<int>() == 600
                && largeProcedure["controlLimit"]?.GetValue<int>() == 512 && largeProcedure["controls"]?.AsArray().Count == 512
                && largeProcedure["controlsTruncated"]?.GetValue<bool>() == true && Gates(largeResponse), largeProcedure.ToJsonString());
            var largeProject = server.Project!; var largeDevice = ProjectQuery.DeviceList(largeProject).Single();
            var fullCore = OfflineApplicationBuildPlan.Preview(largeProject, largeDevice, new("all", LoadProcedureAccess.Remote, LoadParts.Full));
            check("truncated MCP list retains the full 600-control Core witness", fullCore.Procedure?.Controls.Count == 600
                && fullCore.Procedure.WitnessHash == (string?)largeProcedure["witnessHash"] && fullCore.Binding?.Fingerprint == (string?)large["binding"]?["fingerprint"], null);
            var tailChanged = new XDocument(largeApp); tailChanged.Descendants(Ns + "LdCtrlRestart").Last().SetAttributeValue("InternalDescription", "changed hidden control 600");
            var hiddenCore = LoadProcedureCompiler.Compile(tailChanged, Knxproj.LoadMasterData(largeProject), new("all", LoadProcedureAccess.Remote, LoadParts.Full));
            check("unprojected tail controls remain covered by the procedure witness", hiddenCore.IsAvailable && hiddenCore.Controls.Count == 600 && hiddenCore.WitnessHash != fullCore.Procedure?.WitnessHash, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog);
            Directory.Delete(directory, recursive: true);
        }
    }
}
