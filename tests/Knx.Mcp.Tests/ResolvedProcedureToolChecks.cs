using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using Knx.Workbench;

internal static class ResolvedProcedureToolChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string Options = "\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false";
    private static XDocument Application(int extraControls = 0)
    {
        var doc = XDocument.Parse(ResourcePatchToolChecks.ApplicationXml);
        var app = doc.Descendants(Ns + "ApplicationProgram").Single(); app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        var stat = app.Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Elements().Single(p => (string?)p.Attribute("Id") == "P-relative4").SetAttributeValue("Value", "1");
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-alias"), new XAttribute("RefId", "P-relative4"), new XAttribute("Access", "None")));
        app.Element(Ns + "Dynamic")!.ReplaceNodes(XElement.Parse($$"""
            <ChannelIndependentBlock xmlns="{{Ns}}"><ParameterRefRef RefId="R-absolute"/>
              <choose ParamRefId="R-absolute"><when test="0"><ParameterRefRef RefId="R-relative4"/></when>
                <when test="1"><ParameterRefRef RefId="R-alias"/></when></choose>
            </ChannelIndependentBlock>
            """));
        var procedure = XElement.Parse($$"""
            <LoadProcedure xmlns="{{Ns}}"><LdCtrlConnect/><choose ParamRefId="R-relative4">
              <when test="2"><LdCtrlCompareRelMem ObjIdx="4" Offset="932" Size="1" InlineData="FF" Mask="FF" Invert="true">
                <OnError Cause="CompareMismatch" MessageRef="M-test"/></LdCtrlCompareRelMem></when>
              <when default="true"><LdCtrlWriteProp ObjIdx="4" PropId="27" Verify="false" InlineData="010203"/></when>
            </choose><LdCtrlDisconnect/></LoadProcedure>
            """);
        procedure.Add(Enumerable.Range(0, extraControls).Select(_ => new XElement(Ns + "LdCtrlRestart")));
        stat.Add(new XElement(Ns + "LoadProcedures", procedure)); return doc;
    }

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-mcp-resolved-procedure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 139000;
            async Task<JsonNode> Tool(string name, object arguments) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } })))!)!;
            async Task<JsonNode> Raw(string fields, string extra = "") => JsonNode.Parse((await server.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":{\"device_address\":\"1.1.1\",\"load_procedure_preview\":{" + fields + "}" + extra + "}}}"))!)!;
            static bool Error(JsonNode r) => r["error"] is not null || r["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode r) => r["result"]?["structuredContent"] ?? throw new InvalidOperationException(r.ToJsonString());
            async Task<JsonNode> Preview() => Data(await Raw(Options + ",\"resolve_conditions\":true"));
            static bool Gates(JsonNode r) => r["writesEnabled"]?.GetValue<bool>() == false && r["realDownloadAvailable"]?.GetValue<bool>() == false
                && r["plan"]?["ready"]?.GetValue<bool>() == false && r["plan"]?["program"] is null && r["plan"]?["imageBytes"]?.GetValue<int>() == 0
                && r["plan"]?["chunks"]?.AsArray().Count == 0 && r["offlineApplicationBuild"]?["executionEligible"]?.GetValue<bool>() == false
                && r["offlineApplicationBuild"]?["completeApplicationImage"]?.GetValue<bool>() == false && r["offlineApplicationBuild"]?["downloadEligible"]?.GetValue<bool>() == false;
            var path = ResourcePatchToolChecks.Fixture(directory, "resolved", Application()); var hash = SHA256.HashData(File.ReadAllBytes(path));
            var opened = await Tool("project_open", new { path }); check("resolved procedure fixture opens", !Error(opened), opened.ToJsonString());
            var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single();
            var xml = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting); var revision = project.ApplicationDataRevision;
            var history = new UndoStack(); history.Reset(project);
            var schema = server.Tools.Single(t => t.Name == "download_plan").InputSchema["properties"]!["load_procedure_preview"]!;
            check("condition resolution is an optional boolean within the existing tool", server.Tools.Count == 55
                && (string?)schema["properties"]?["resolve_conditions"]?["type"] == "boolean"
                && !schema["required"]!.AsArray().Any(n => (string?)n == "resolve_conditions") && schema["allOf"]?.AsArray().Count == 2, schema.ToJsonString());
            var ordinary = Data(await Raw(Options)); var explicitFalse = Data(await Raw(Options + ",\"resolve_conditions\":false"));
            check("absent and explicit false retain identical full legacy JSON", ordinary.ToJsonString() == explicitFalse.ToJsonString()
                && ordinary["offlineApplicationBuild"]?["available"]?.GetValue<bool>() == false, ordinary.ToJsonString());
            var first = await Preview(); var build = first["offlineApplicationBuild"]!; var procedure = build["procedure"]!;
            var decision = procedure["conditionalSelection"]!["decisions"]!.AsArray().Single()!;
            check("default controller mode yields a bound offline procedure", build["available"]?.GetValue<bool>() == true && build["profileVersion"]?.GetValue<int>() == 2
                && procedure["profileVersion"]?.GetValue<int>() == 2 && procedure["controls"]!.AsArray().Select(c => (string?)c?["kind"]).SequenceEqual(new[] { "connect", "write_property", "disconnect" })
                && decision["controllerActive"]?.GetValue<bool>() == true && decision["controllerValue"]?.GetValue<int>() == 1 && decision["usedDefault"]?.GetValue<bool>() == true, build.ToJsonString());
            check("resolved projection binds complete Core graph and context", build["binding"]?["procedureGraphHash"]?.GetValue<string>() is { Length: 64 }
                && build["binding"]?["evaluationContextHash"]?.GetValue<string>() is { Length: 64 }
                && (string?)build["binding"]?["fingerprint"] == OfflineApplicationBuildPlan.PreviewResolved(project, device, new("all", LoadProcedureAccess.Remote, LoadParts.Full)).Binding?.Fingerprint, build.ToJsonString());
            check("original element provenance accompanies selected controls and decisions", (string?)decision["source"]?["sourceOrigin"] == "application"
                && decision["source"]?["sourcePath"]?.AsArray().Count > 0 && procedure["controls"]![1]?["provenance"]?["ancestorDecisionOrdinals"]?.AsArray().Count == 1, procedure.ToJsonString());
            check("offline declaration keeps all write and image gates closed", Gates(first) && first["plan"]!.ToJsonString() == ordinary["plan"]!.ToJsonString()
                && procedure["unresolvedRequirements"]!.AsArray().Any(r => (string?)r?["code"] == "generated_tables_not_built"), first.ToJsonString());
            device.ParameterValues["R-relative4"] = "2";
            var comparison = await Preview(); var compareBuild = comparison["offlineApplicationBuild"]!;
            var compare = compareBuild["procedure"]!["controls"]!.AsArray().Single(c => (string?)c?["kind"] == "compare_relative_memory")!;
            check("comparison operands project only lengths and hashes", compareBuild["available"]?.GetValue<bool>() == true
                && compare["inlineDataLength"]?.GetValue<int>() == 1 && compare["inlineMaskLength"]?.GetValue<int>() == 1
                && compare["inlineDataHash"]?.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(new byte[] { 255 }))
                && compare["inlineData"] is null && compare["inlineMask"] is null && compare["onError"]![0]?["cause"]?.GetValue<string>() == "compare_mismatch" && Gates(comparison), compareBuild.ToJsonString());
            check("saved values change context and witness without changing archived graph", (string?)compareBuild["binding"]?["sourceFingerprint"] == (string?)build["binding"]?["sourceFingerprint"]
                && (string?)compareBuild["binding"]?["procedureGraphHash"] == (string?)build["binding"]?["procedureGraphHash"]
                && (string?)compareBuild["binding"]?["evaluationContextHash"] != (string?)build["binding"]?["evaluationContextHash"]
                && (string?)compareBuild["procedure"]?["witnessHash"] != (string?)procedure["witnessHash"], compareBuild.ToJsonString());
            device.ParameterValues["R-absolute"] = "1";
            var inactive = await Preview(); var inactiveBuild = inactive["offlineApplicationBuild"]!;
            var inactiveDecision = inactiveBuild["procedure"]!["conditionalSelection"]!["decisions"]![0]!;
            check("active alias does not activate the procedure reference or its default", inactiveBuild["available"]?.GetValue<bool>() == true
                && inactiveBuild["procedure"]?["controlCount"]?.GetValue<int>() == 2 && inactiveDecision["controllerActive"]?.GetValue<bool>() == false
                && inactiveDecision["controllerValue"] is null && inactiveDecision["selectedWhenOrdinals"]?.AsArray().Count == 0
                && inactiveDecision["usedDefault"]?.GetValue<bool>() == false && Gates(inactive), inactiveBuild.ToJsonString());
            device.ParameterValues.Remove("R-absolute"); device.ParameterValues.Remove("R-relative4");
            foreach (var fields in new[] { Options + ",\"resolve_conditions\":null", Options + ",\"resolve_conditions\":\"true\"", Options + ",\"resolve_conditions\":1",
                Options + ",\"resolve_conditions\":true,\"resolve_conditions\":false", Options + ",\"resolve_conditions\":true,\"generated_tables\":{\"format\":\"m112\"}",
                Options + ",\"resolve_conditions\":true,\"generated_tables\":null" })
            {
                var invalid = await Raw(fields, ",\"gateway\":\"invalid-endpoint\"");
                var errorJson = invalid.ToJsonString();
                check("invalid condition options refuse before descriptor access", Error(invalid)
                    && (errorJson.Contains("load_procedure_preview", StringComparison.Ordinal) || errorJson.Contains("procedure-only", StringComparison.Ordinal)), errorJson);
            }
            check("all previews leave source values revision and undo history untouched", hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                && Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting) == xml && project.ApplicationDataRevision == revision && history.Index == 0 && history.Steps == 0 && !history.IsDirty, null);
            var largePath = ResourcePatchToolChecks.Fixture(directory, "resolved-large", Application(520));
            await Tool("project_open", new { path = largePath }); var large = await Preview(); var largeProcedure = large["offlineApplicationBuild"]!["procedure"]!;
            check("bounded projection retains complete count and binding", large["offlineApplicationBuild"]?["available"]?.GetValue<bool>() == true
                && largeProcedure["controlCount"]?.GetValue<int>() == 523 && largeProcedure["controls"]?.AsArray().Count == 512
                && largeProcedure["controlsTruncated"]?.GetValue<bool>() == true && large["offlineApplicationBuild"]?["binding"]?["procedureWitnessHash"]?.GetValue<string>() == (string?)largeProcedure["witnessHash"] && Gates(large), largeProcedure.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previous); Directory.Delete(directory, true); }
    }
}
