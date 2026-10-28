using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knx.Mcp;

internal static class DcaManifestToolChecks
{
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-dca-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "synthetic.etsapp");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var output = zip.CreateEntry("AddInManifest.xml").Open()) output.Write(Encoding.UTF8.GetBytes(
                    "<Knxapp AppId='M0083-A0158' AddInAssembly='metadata.dll'><DeviceConfiguration><ApplicationProgram Id='M-0083_A-0154-11-0000'/></DeviceConfiguration></Knxapp>"));
                using var binary = zip.CreateEntry("metadata.dll").Open(); binary.Write([1, 2, 3]);
            }
            var before = File.ReadAllBytes(path);
            await using var server = new McpServer(); var id = 0;
            async Task<JsonNode> Call(object args)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++id, method = "tools/call", @params = new { name = "dca_manifest_info", arguments = args } });
                return JsonNode.Parse(await server.HandleAsync(request) ?? "null")!;
            }
            var matched = await Call(new { path, application_program_id = "M-0083_A-0154-11-B606-O00EF" });
            var info = matched["result"]?["structuredContent"];
            check("DCA tool inspects declared compatibility without a project or code execution", info?["declaresSupport"]?.GetValue<bool>() == true
                && info?["codeExecuted"]?.GetValue<bool>() == false && (string?)info?["appId"] == "M0083-A0158"
                && info?["knownRequirement"] is not null, matched.ToJsonString());
            var wrong = await Call(new { path, application_program_id = "M-0083_A-0154-10-B606" });
            check("DCA tool preserves exact version binding", wrong["result"]?["structuredContent"]?["declaresSupport"]?.GetValue<bool>() == false, wrong.ToJsonString());
            var malformed = await Call(new { path, application_program_id = "MDT" });
            check("DCA tool rejects malformed identities", malformed["result"]?["isError"]?.GetValue<bool>() == true, malformed.ToJsonString());
            var mistyped = await Call(new { path, application_program_id = 5 });
            check("DCA tool rejects non-string identities", mistyped["result"]?["isError"]?.GetValue<bool>() == true, mistyped.ToJsonString());
            check("DCA tool retains original package bytes and extracts no members", File.ReadAllBytes(path).SequenceEqual(before)
                && Directory.GetFiles(directory).Length == 1, null);
        }
        finally { Directory.Delete(directory, true); }
    }
}
