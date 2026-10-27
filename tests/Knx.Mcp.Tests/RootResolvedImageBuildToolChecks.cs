using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class RootResolvedImageBuildToolChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string Procedure = "{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false}";
    private static string Option(string policy = "source_defined_bits", string? tables = null)
        => "\"root_resolved_image_build_preview\":{\"mask_policy\":\"" + policy + "\",\"procedure\":" + Procedure
            + (tables is null ? "" : ",\"generated_tables\":{\"format\":\"" + tables + "\"}") + "}";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-resolved-root-tools-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var oldCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 181000;
            async Task<JsonNode> Tool(string name, object arguments) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } })))!)!;
            async Task<JsonNode> Raw(string arguments) => JsonNode.Parse((await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence
                + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            static bool Error(JsonNode node) => node["error"] is not null || node["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode node) => node["result"]!["structuredContent"]!;
            static JsonNode Build(JsonNode node) => Data(node)["offlineRootResolvedImageBuild"]!;
            async Task<JsonNode> Preview(string policy = "source_defined_bits", string? tables = null)
                => await Raw("{\"device_address\":\"1.1.1\"," + Option(policy, tables) + "}");
            var app = Application(130); var path = RootImageBuildToolChecks.Fixture(directory, "joint-root", app);
            check("resolved root own fixture opens", !Error(await Tool("project_open", new { path })), null);
            var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single(); device.ParameterValues["R-0"] = "16909060";
            var source = SHA256.HashData(File.ReadAllBytes(path)); var xml = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
            var info = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            var schema = server.Tools.Single(tool => tool.Name == "download_plan").InputSchema;
            check("resolved root is one closed option within the existing 55 tools", server.Tools.Count == 55
                && schema["properties"]?["root_resolved_image_build_preview"]?["additionalProperties"]?.GetValue<bool>() == false
                && schema["properties"]?["root_resolved_image_build_preview"]?["properties"]?["procedure"]?["required"]?.AsArray().Count == 5
                && schema["properties"]?["load_procedure_preview"]?["allOf"]?.AsArray().Count == 2, null);
            check("absent joint root option leaves ordinary JSON unchanged", !ordinary.Contains("offlineRootResolvedImageBuild", StringComparison.Ordinal), ordinary);

            foreach (var (name, raw) in new[]
            {
                ("boolean", "\"root_resolved_image_build_preview\":true"),
                ("null", "\"root_resolved_image_build_preview\":null"),
                ("no fields", "\"root_resolved_image_build_preview\":{}"),
                ("no policy", "\"root_resolved_image_build_preview\":{\"procedure\":" + Procedure + "}"),
                ("unknown policy", Option("all")),
                ("nonstring policy", Option().Replace("\"source_defined_bits\"", "true", StringComparison.Ordinal)),
                ("no procedure", "\"root_resolved_image_build_preview\":{\"mask_policy\":\"source_defined_bits\"}"),
                ("nonobject procedure", Option().Replace(Procedure, "true", StringComparison.Ordinal)),
                ("missing flag", Option().Replace(",\"groups\":false", "", StringComparison.Ordinal)),
                ("nonboolean flag", Option().Replace("\"groups\":false", "\"groups\":\"false\"", StringComparison.Ordinal)),
                ("unknown subtype", Option().Replace("\"all\"", "\"unknown\"", StringComparison.Ordinal)),
                ("nonstring subtype", Option().Replace("\"subtype\":\"all\"", "\"subtype\":1", StringComparison.Ordinal)),
                ("nonremote access", Option().Replace("\"remote\"", "\"local\"", StringComparison.Ordinal)),
                ("groups only", Option().Replace("\"full\":true", "\"full\":false", StringComparison.Ordinal).Replace("\"groups\":false", "\"groups\":true", StringComparison.Ordinal)),
                ("extra procedure field", Option().Replace("\"groups\":false", "\"groups\":false,\"resolve_conditions\":true", StringComparison.Ordinal)),
                ("duplicate procedure flag", Option().Replace("\"full\":true", "\"full\":true,\"full\":false", StringComparison.Ordinal)),
                ("duplicate root policy", Option().Replace("\"mask_policy\":\"source_defined_bits\"", "\"mask_policy\":\"source_defined_bits\",\"mask_policy\":\"no_static_source_bits\"", StringComparison.Ordinal)),
                ("duplicate root option", Option() + "," + Option()),
                ("unknown root field", Option()[..^1] + ",\"values\":[]}"),
                ("null tables", Option()[..^1] + ",\"generated_tables\":null}"),
                ("empty tables", Option()[..^1] + ",\"generated_tables\":{}}"),
                ("unknown table format", Option(tables: "all")),
                ("duplicate table format", Option(tables: "m112").Replace("\"format\":\"m112\"", "\"format\":\"m112\",\"format\":\"system_b_2\"", StringComparison.Ordinal)),
                ("extra table field", Option(tables: "m112").Replace("\"format\":\"m112\"", "\"format\":\"m112\",\"data\":\"00\"", StringComparison.Ordinal)),
                ("tables without full or groups", Option(tables: "m112").Replace("\"full\":true", "\"full\":false", StringComparison.Ordinal).Replace("\"parameters\":false", "\"parameters\":true", StringComparison.Ordinal)),
            })
            {
                var rejected = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-resolved-root-gateway\"," + raw + "}");
                check("resolved root rejects " + name + " before gateway access", Error(rejected)
                    && rejected.ToJsonString().Contains("root_resolved_image_build_preview", StringComparison.Ordinal)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }
            foreach (var (name, extra) in new[]
            {
                ("old procedure", ",\"load_procedure_preview\":" + Procedure),
                ("old root", ",\"load_procedure_preview\":" + Procedure + ",\"root_image_build_preview\":{\"mask_policy\":\"source_defined_bits\"}"),
                ("source-only composer", ",\"image_composition_preview\":{\"mask_policy\":\"source_defined_bits\"}"),
                ("caller subset and byte order", ",\"parameter_subset\":[{\"refId\":\"R-0\",\"rawValue\":\"1\"}],\"byte_order\":\"big_endian\""),
            })
            {
                var rejected = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-resolved-root-gateway\"," + Option() + extra + "}");
                check("joint root excludes " + name + " before gateway access", Error(rejected)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }
            var response = await Preview(); var build = Build(response);
            check("joint root emits all 130 references and resolves its branch", !Error(response) && build["available"]?.GetValue<bool>() == true
                && (string?)build["scope"] == "RootFixedMemoryResolvedWorkingSet" && build["profileVersion"]?.GetValue<int>() == 2
                && build["population"]?["referenceCount"]?.GetValue<int>() == 130 && build["procedure"]?["profileVersion"]?.GetValue<int>() == 2, response.ToJsonString());
            if (build["available"]?.GetValue<bool>() == true)
            {
                var expected = new byte[524]; for (var i = 0; i < 130; i++) BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(i * 4, 4), i == 0 ? 0x01020304u : (uint)i + 1);
                check("resolved root preserves every independent expected byte and unknown tail", (string?)build["chunks"]?[0]?["workingDataHash"] == Hash(expected)
                    && build["desiredBitCount"]?.GetValue<ulong>() == 4160 && build["unspecifiedBitCount"]?.GetValue<ulong>() == 32, build.ToJsonString());
                check("same evaluation has distinct complete controller and population witnesses", build["procedureProof"]?["controllersTotal"]?.GetValue<int>() == 1
                    && build["procedureProof"]?["controllers"]?[0]?["everDeclaredOwnerCount"]?.GetValue<int>() == 1
                    && build["procedureProof"]?["controllers"]?[0]?["activeNormalOwnerCount"]?.GetValue<int>() == 1
                    && (string?)build["binding"]?["contextFingerprint"] == (string?)build["procedureProof"]?["applicationContextFingerprint"]
                    && (string?)build["binding"]?["sharedProcedureProofHash"] == (string?)build["procedureProof"]?["fingerprint"], build.ToJsonString());
                var write = build["procedure"]!["controls"]!.AsArray().Single(c => (string?)c?["kind"] == "write_property")!;
                check("selected conditional control carries its actual inline hash and provenance", (string?)write["inlineDataHash"] == Hash([0x11])
                    && write["inlineDataLength"]?.GetValue<int>() == 1 && write["provenance"] is not null
                    && build["procedure"]?["conditionalSelection"]?["decisionsTotal"]?.GetValue<int>() == 1, build.ToJsonString());
                check("joint projection caps all reference arrays while retaining totals", build["population"]?["recordsShown"]?.GetValue<int>() == 128
                    && build["population"]?["recordsTruncated"]?.GetValue<bool>() == true && build["claimsShown"]?.GetValue<int>() == 128
                    && build["claimsTruncated"]?.GetValue<bool>() == true, build.ToJsonString());
                check("joint result does not expose raw buffers strings or encoded words", build["chunks"]?[0]?["workingData"] is null
                    && build["population"]?["records"]?[0]?["rawValue"] is null && build["claims"]?[0]?["encodedWord"] is null
                    && build["procedure"]?["conditionalSelection"]?["decisions"]?[0]?["controllerValue"] is null
                    && build["procedureProof"]?["controllers"]?[0]?["activeValue"] is null, build.ToJsonString());
            }
            check("new joint result does not perform a separate old build", Data(response)["offlineApplicationBuild"] is null && Data(response)["offlineRootImageBuild"] is null
                && Data(response)["offlineParameterPatch"] is null, response.ToJsonString());
            check("joint working stage leaves all programming gates closed", Data(response)["writesEnabled"]?.GetValue<bool>() == false
                && Data(response)["realDownloadAvailable"]?.GetValue<bool>() == false && Data(response)["plan"]?["ready"]?.GetValue<bool>() == false
                && Data(response)["plan"]?["program"] is null && Data(response)["plan"]?["imageBytes"]?.GetValue<int>() == 0
                && Data(response)["plan"]?["chunks"]?.AsArray().Count == 0 && build["completeApplicationImage"]?.GetValue<bool>() == false
                && build["executionEligible"]?.GetValue<bool>() == false && build["downloadEligible"]?.GetValue<bool>() == false, response.ToJsonString());
            check("joint preview preserves project undo source and ordinary JSON", xml == Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting)
                && info == Data(await Tool("project_info", new { })).ToJsonString() && source.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                && ordinary == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString(), null);
            device.ParameterValues["R-0"] = "2"; var fallback = Build(await Preview());
            check("changed current controller chooses the actual default control", fallback["available"]?.GetValue<bool>() == true
                && fallback["procedure"]?["conditionalSelection"]?["decisions"]?[0]?["usedDefault"]?.GetValue<bool>() == true
                && (string?)fallback["binding"]?["fingerprint"] != (string?)build["binding"]?["fingerprint"], fallback.ToJsonString());

            var tablePath = TableFixture(Path.Combine(directory, "tables")); await Tool("project_open", new { path = tablePath });
            var tableProject = server.Project!; var tableDevice = ProjectQuery.DeviceList(tableProject).Single(); tableDevice.ParameterValues["R-0"] = "16909060";
            var joint = Build(await Preview(tables: "m112"));
            check("MCP joint route combines a resolved branch complete population and M112 tables", joint["available"]?.GetValue<bool>() == true
                && joint["population"]?["referenceCount"]?.GetValue<int>() == 130 && (string?)joint["tables"]?["format"] == "m112"
                && joint["tables"]?["payloads"]?.AsArray().Count == 3 && joint["tables"]?["profileVersion"]?.GetValue<int>() == 2, joint.ToJsonString());
            check("joint table freshness is witnessed by its owning root route", joint["tables"]?["bindingFingerprint"] is null
                && (string?)joint["tables"]?["emissionHash"] == (string?)joint["binding"]?["tableEmissionHash"]
                && (string?)joint["tables"]?["actualProcedureWitnessHash"] == (string?)joint["procedure"]?["witnessHash"], joint.ToJsonString());
            var reset = Build(await Preview("no_static_source_bits", "m112"));
            check("joint table validity reset retains separately bound compatible working bytes", reset["available"]?.GetValue<bool>() == true
                && (string?)reset["binding"]?["fingerprint"] != (string?)joint["binding"]?["fingerprint"]
                && (string?)reset["tables"]?["compatibilityHash"] == (string?)joint["tables"]?["compatibilityHash"], reset.ToJsonString());
            var oldRejected = await Raw("{\"device_address\":\"1.1.1\",\"load_procedure_preview\":" + Procedure[..^1]
                + ",\"resolve_conditions\":true,\"generated_tables\":{\"format\":\"m112\"}}}");
            check("old procedure-only option still refuses resolved tables", Error(oldRejected), oldRejected.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", oldCatalog); Directory.Delete(directory, true); }
    }
    private static XDocument Application(int count)
    {
        var app = RootImageBuildToolChecks.Application(count); var procedure = app.Descendants(Ns + "LoadProcedure").Single();
        procedure.ReplaceNodes(new XElement(Ns + "LdCtrlConnect"), new XElement(Ns + "choose", new XAttribute("ParamRefId", "R-0"),
            new XElement(Ns + "when", new XAttribute("test", "16909060"), Write("11")), new XElement(Ns + "when", new XAttribute("default", true), Write("22"))), new XElement(Ns + "LdCtrlDisconnect"));
        return app;
    }
    private static XElement Write(string data) => new(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27), new XAttribute("Verify", false), new XAttribute("InlineData", data));
    private static string TableFixture(string directory)
    {
        Directory.CreateDirectory(directory); var path = GeneratedTablesToolChecks.Fixture(directory);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        XDocument Read(string name) { using var stream = zip.GetEntry(name)!.Open(); return XDocument.Load(stream); }
        void Put(string name, XDocument doc) { zip.GetEntry(name)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(doc.ToString()); }
        var app = Read("M-0001/M-0001_A-1.xml"); var root = Application(130); var stat = app.Descendants(Ns + "Static").Single(); var rootStat = root.Descendants(Ns + "Static").Single();
        app.Descendants(Ns + "ApplicationProgram").Single().SetAttributeValue("MinEtsVersion", "6.0");
        stat.Descendants(Ns + "Mask").Remove(); stat.Element(Ns + "Code")!.Add(new XElement(rootStat.Element(Ns + "Code")!.Elements().Single()));
        stat.Add(new XElement(rootStat.Element(Ns + "Options")!), new XElement(rootStat.Element(Ns + "ParameterTypes")!), new XElement(rootStat.Element(Ns + "Parameters")!), new XElement(rootStat.Element(Ns + "ParameterRefs")!));
        stat.Element(Ns + "LoadProcedures")!.ReplaceWith(new XElement(rootStat.Element(Ns + "LoadProcedures")!));
        app.Descendants(Ns + "ChannelIndependentBlock").Single().Add(root.Descendants(Ns + "ParameterRefRef").Select(e => new XElement(e)));
        var project = Read("0.xml"); project.Descendants().Single(e => e.Name.LocalName == "DeviceInstance").SetAttributeValue("Hardware2ProgramRefId", "M-0001_H-root_I-1_A-root");
        Put("0.xml", project); Put("M-0001/M-0001_A-1.xml", app);
        Put("M-0001/Hardware.xml", XDocument.Parse("<KNX xmlns='http://knx.org/xml/project/20'><ManufacturerData><Manufacturer RefId='M-0001'><Hardware><Hardware Id='M-0001_H-root'><Hardware2Programs><Hardware2Program Id='M-0001_H-root_I-1_A-root'><ApplicationProgramRef RefId='M-0001_A-1'/></Hardware2Program></Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>"));
        return path;
    }
}
