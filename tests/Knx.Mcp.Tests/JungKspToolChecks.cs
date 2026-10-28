using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Manufacturer;
using Knx.Mcp;
using Knx.Workbench;

internal static class JungKspToolChecks
{
    private const string App = JungKspImport.ApplicationProgramId;
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-mcp-jung-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "synthetic.knxproj"); var kspPath = Path.Combine(root, "synthetic.ksp");
            var masterPath = Path.Combine(root, "knx_master.xml");
            var (project, definition) = Fixture();
            using (var archive = ZipFile.Open(projectPath, ZipArchiveMode.Create))
            {
                using (var output = archive.CreateEntry("0.xml").Open()) Knxproj.ToXml(project).Save(output);
                using var program = archive.CreateEntry("M-0004/" + App + ".xml").Open(); definition.Save(program);
            }
            File.WriteAllBytes(kspPath, Payload(1)); File.WriteAllText(masterPath, MasterXml());
            var source = File.ReadAllBytes(projectPath); var kspSource = File.ReadAllBytes(kspPath); var masterSource = File.ReadAllBytes(masterPath);
            await using var server = new McpServer(); var sequence = 22000;
            async Task<JsonNode> Tool(string name, object? arguments = null)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments = arguments ?? new { } } });
                return JsonNode.Parse((await server.HandleAsync(request))!)!;
            }
            static bool Error(JsonNode result) => result["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode? Data(JsonNode result) => result["result"]?["structuredContent"];
            async Task<int> Steps() => Data(await Tool("project_info"))!["undo"]!["steps"]!.GetValue<int>();
            var info = await Tool("jung_ksp_info", new { path = kspPath });
            check("MCP Jung info reads bounded metadata without requiring or mutating a project", !Error(info)
                && Data(info)?["fileValidated"]?.GetValue<bool>() == true && Data(info)?["importValidated"]?.GetValue<bool>() == false
                && Data(info)?["codeExecuted"]?.GetValue<bool>() == false && Data(info)?["blocks"]?.AsArray().Count == 6 && server.Project is null, info.ToJsonString());
            var applySchema = server.Tools.Single(t => t.Name == "jung_ksp_import_apply").InputSchema;
            check("MCP Jung unlink decisions are typed booleans rather than coercible strings", (string?)applySchema["properties"]?["unlink_decisions"]?["additionalProperties"]?["type"] == "boolean", null);
            _ = await Tool("project_new", new { name = "New memory project" });
            var notLoaded = await Tool("jung_ksp_import_preview", new { path = kspPath, master_data_path = masterPath, device_id = "D1" });
            check("MCP Jung import requires an actually opened project", Error(notLoaded), notLoaded.ToJsonString());
            var opened = await Tool("project_open", new { path = projectPath });
            check("MCP Jung opens a fully synthetic bound project", !Error(opened), opened.ToJsonString());
            var before = Knxproj.ToXml(server.Project!).ToString(); var steps = await Steps();
            object PreviewArguments() => new { path = kspPath, master_data_path = masterPath, device_id = "D1", language = "de" };
            var preview = await Tool("jung_ksp_import_preview", PreviewArguments()); var token = (string?)Data(preview)?["reviewToken"];
            check("MCP Jung preview exposes exact changes and owns no undo mutation", !Error(preview) && token?.Length == 64
                && Data(preview)?["validated"]?.GetValue<bool>() == true && Data(preview)?["masterData"]?["sha256"]?.GetValue<string>()?.Length == 64
                && Data(preview)?["objects"]?[0]?["targetReferenceId"]?.GetValue<string>() == "O-1_R-11"
                && Knxproj.ToXml(server.Project!).ToString() == before && await Steps() == steps, preview.ToJsonString());
            var nextPreview = await Tool("jung_ksp_import_preview", PreviewArguments());
            var oldToken = await Tool("jung_ksp_import_apply", new { review_token = token });
            check("MCP Jung a new preview retires the older review token", Error(oldToken) && await Steps() == steps, oldToken.ToJsonString());
            token = (string?)Data(nextPreview)?["reviewToken"];
            var mistyped = await Tool("jung_ksp_import_apply", new { review_token = token, unlink_decisions = new { not_a_number = "true" } });
            check("MCP Jung malformed unlink inputs fail without project or undo changes", Error(mistyped) && Knxproj.ToXml(server.Project!).ToString() == before && await Steps() == steps, mistyped.ToJsonString());
            var applied = await Tool("jung_ksp_import_apply", new { review_token = token });
            check("MCP Jung reviewed import commits exactly one undo snapshot", !Error(applied) && Data(applied)?["applied"]?.GetValue<bool>() == true
                && await Steps() == steps + 1 && ProjectQuery.DeviceList(server.Project!).Single().BinaryData.ContainsKey("FunctionTexts"), applied.ToJsonString());
            var replay = await Tool("jung_ksp_import_apply", new { review_token = token });
            check("MCP Jung review cannot be replayed", Error(replay) && await Steps() == steps + 1, replay.ToJsonString());
            _ = await Tool("undo");
            check("MCP Jung one undo restores raw values, object text and binary data", !ProjectQuery.DeviceList(server.Project!).Single().BinaryData.ContainsKey("FunctionTexts")
                && ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs.Single().Text == "Old label", null);
            _ = await Tool("redo");
            check("MCP Jung redo restores the reviewed raw and function-text state", ProjectQuery.DeviceList(server.Project!).Single().BinaryData.ContainsKey("FunctionTexts")
                && ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs.Single().Text == "Imported", null);

            _ = await Tool("project_open", new { path = projectPath });
            File.WriteAllBytes(kspPath, Payload(5));
            preview = await Tool("jung_ksp_import_preview", PreviewArguments()); token = (string?)Data(preview)?["reviewToken"];
            check("MCP Jung incompatible bit-size variant shows required unlink review", !Error(preview)
                && (string?)Data(preview)?["objects"]?[0]?["linkAction"] == "RequiredUnlink" && (string?)Data(preview)?["objects"]?[0]?["targetReferenceId"] == "O-1_R-13", preview.ToJsonString());
            var missing = await Tool("jung_ksp_import_apply", new { review_token = token });
            var negative = await Tool("jung_ksp_import_apply", new { review_token = token, unlink_decisions = new Dictionary<string, bool> { ["1"] = false } });
            check("MCP Jung missing and negative unlink approvals cannot partially mutate", Error(missing) && Error(negative)
                && ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs.Single().Links.SequenceEqual(["G1"]) && await Steps() == 0, negative.ToJsonString());
            var approved = await Tool("jung_ksp_import_apply", new { review_token = token, unlink_decisions = new Dictionary<string, bool> { ["1"] = true } });
            check("MCP Jung explicit positive approval clears old variant links atomically", !Error(approved) && Data(approved)?["removedLinks"]?.GetValue<int>() == 1
                && ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs.All(o => o.Links.Count == 0) && await Steps() == 1, approved.ToJsonString());

            _ = await Tool("project_open", new { path = projectPath }); File.WriteAllBytes(kspPath, kspSource);
            preview = await Tool("jung_ksp_import_preview", PreviewArguments()); token = (string?)Data(preview)?["reviewToken"];
            _ = await Tool("device_update", new { address = "1.1.1", name = "Changed after preview" });
            var stale = await Tool("jung_ksp_import_apply", new { review_token = token });
            check("MCP Jung project changes invalidate reviewed input without adding undo", Error(stale) && !ProjectQuery.DeviceList(server.Project!).Single().BinaryData.ContainsKey("FunctionTexts")
                && await Steps() == 1, stale.ToJsonString());
            _ = await Tool("project_open", new { path = projectPath });
            preview = await Tool("jung_ksp_import_preview", PreviewArguments()); token = (string?)Data(preview)?["reviewToken"];
            _ = await Tool("project_open", new { path = projectPath });
            var reopened = await Tool("jung_ksp_import_apply", new { review_token = token });
            check("MCP Jung project reopen clears reviews even for equal project ids", Error(reopened), reopened.ToJsonString());
            File.WriteAllText(masterPath, "<!DOCTYPE KNX [<!ENTITY x 'bad'>]><KNX>&x;</KNX>");
            var dtd = await Tool("jung_ksp_import_preview", PreviewArguments());
            check("MCP Jung master input prohibits DTD/entity expansion", Error(dtd), dtd.ToJsonString());
            File.WriteAllBytes(masterPath, masterSource);
            check("MCP Jung tools keep manufacturer and project source files byte-identical", File.ReadAllBytes(projectPath).SequenceEqual(source)
                && File.ReadAllBytes(kspPath).SequenceEqual(kspSource) && File.ReadAllBytes(masterPath).SequenceEqual(masterSource)
                && Directory.GetFiles(root).Length == 3, null);
        }
        finally { Directory.Delete(root, true); }
    }

    private static (Project Project, XDocument Definition) Fixture()
    {
        var ns = (XNamespace)"http://knx.org/xml/project/20";
        var number = App + "_PT-N"; var raw30 = App + "_PT-R30"; var raw16 = App + "_PT-R16";
        var parameters = new XElement(ns + "Parameters"); var references = new XElement(ns + "ParameterRefs");
        foreach (var n in new[] { 1, 2, 3, 4, 16, 17 })
        {
            var raw = n is 2 or 3 or 4;
            parameters.Add(new XElement(ns + "Parameter", new XAttribute("Id", App + "_P-" + n), new XAttribute("ParameterType", raw ? n == 2 ? raw30 : raw16 : number), new XAttribute("Value", raw ? "" : n == 1 ? "2048" : "1"), new XAttribute("Access", "None")));
            references.Add(new XElement(ns + "ParameterRef", new XAttribute("Id", P(n)), new XAttribute("RefId", App + "_P-" + n)));
        }
        var refs = new XElement(ns + "ComObjectRefs"); var dynamic = new XElement(ns + "Dynamic");
        foreach (var (bits, suffix, dpt) in new[] { (1, 11, "DPST-1-1"), (8, 13, "DPST-5-1") })
        {
            var id = App + "_O-1_R-" + suffix;
            refs.Add(new XElement(ns + "ComObjectRef", new XAttribute("Id", id), new XAttribute("RefId", App + "_O-1"), new XAttribute("ObjectSize", bits == 1 ? "1 Bit" : "1 Byte"), new XAttribute("DatapointType", dpt)));
            dynamic.Add(new XElement(ns + "choose", new XAttribute("ParamRefId", P(16)), new XElement(ns + "when", new XAttribute("test", bits), new XElement(ns + "ComObjectRefRef", new XAttribute("RefId", id)))));
        }
        var definition = new XDocument(new XElement(ns + "KNX", new XElement(ns + "ApplicationProgram", new XAttribute("Id", App), new XElement(ns + "Static",
            new XElement(ns + "ParameterTypes", new XElement(ns + "ParameterType", new XAttribute("Id", number), new XElement(ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 16))),
                new XElement(ns + "ParameterType", new XAttribute("Id", raw30), new XElement(ns + "TypeRawData", new XAttribute("MaxSize", 30716))), new XElement(ns + "ParameterType", new XAttribute("Id", raw16), new XElement(ns + "TypeRawData", new XAttribute("MaxSize", 16380)))),
            parameters, references, new XElement(ns + "ComObjectTable", new XElement(ns + "ComObject", new XAttribute("Id", App + "_O-1"), new XAttribute("Number", 1), new XAttribute("ObjectSize", "14 Bytes"), new XAttribute("Text", "Default"), new XAttribute("CommunicationFlag", "Enabled"))), refs), dynamic)));
        var project = ProjectWizard.Create(new() { Name = "Synthetic MCP Jung", Areas = 1, LinesPerArea = 1 });
        var device = new DeviceInstance { Id = "D1", ProductRefId = App, Hardware2ProgramRefId = App, Address = new(1, 1, 1) };
        var obj = new ComObjectInstanceRef { Id = "O-1_R-11", ComObjectId = "O-1", Number = 1, Text = "Old label" }; obj.Links.Add("G1"); device.ComObjectRefs.Add(obj);
        project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
        var range = new GroupRange { Id = "GR1", Name = "Synthetic", RangeStart = 1, RangeEnd = 65535 };
        range.Addresses.Add(new() { Id = "G1", Name = "Fixture", Address = GroupAddress.ThreeLevel(1, 0, 1), DatapointType = "DPST-1-1" }); project.Installations[0].MainRanges.Add(range);
        project.Workbench.Groups["G1"] = new() { Bits = 1, Secure = false };
        project.Workbench.Objects[new ObjectKey("D1", obj.Id).ToString()] = new() { Active = true, Bits = 1, Dpt = "1.001", Flags = "CRW" };
        return (project, definition);
    }
    private static byte[] Payload(int main)
    {
        var config = JsonSerializer.Serialize(new { versions = new { dca_api = 1, database = "3.0.0" }, items = new { objects_list = new Dictionary<string, object> { ["1"] = new { command = "1", name = "Imported", type = "dtp_" + main, typeformat = "1", id_driver = "synthetic", flags = 5460 } } },
            elements = new { objects_list = new Dictionary<string, object> { ["E"] = new Dictionary<string, object> { ["@class"] = "psoe_switch", ["function_map"] = new { toggle = "1" } } } } });
        var parts = new byte[][] { Pack("{\"device\":{\"major\":1}}"), Pack(config), [], [], Encoding.ASCII.GetBytes("##01").Concat(Pack("{\"psoe_switch\":{\"toggle\":{\"command\":{\"de\":\"Funktion\"}}}}" )).ToArray(), Encoding.ASCII.GetBytes("##FF0004|ksp\0") };
        using var output = new MemoryStream(); var prefix = new byte[4]; foreach (var part in parts) { BinaryPrimitives.WriteInt32LittleEndian(prefix, part.Length); output.Write(prefix); output.Write(part); } return output.ToArray();
    }
    private static byte[] Pack(string json)
    { var raw = Encoding.UTF8.GetBytes(json); using var output = new MemoryStream(); var prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)raw.Length); output.Write(prefix); using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true)) zlib.Write(raw); return output.ToArray(); }
    private static string P(int n) => App + $"_P-{n}_R-{n}";
    private static string MasterXml() => "<KNX><MasterData Version='198'><DatapointTypes><DatapointType Id='DPT-1' SizeInBit='1'><DatapointSubtypes><DatapointSubtype Id='DPST-1-1'/></DatapointSubtypes></DatapointType><DatapointType Id='DPT-5' SizeInBit='8'><DatapointSubtypes><DatapointSubtype Id='DPST-5-1'/></DatapointSubtypes></DatapointType></DatapointTypes></MasterData></KNX>";
}
