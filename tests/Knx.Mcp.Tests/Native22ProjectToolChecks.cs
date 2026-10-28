using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using Knx.TestFixtures;

internal static class Native22ProjectToolChecks
{
    public static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-mcp-native22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var layout in new[] { "root", "nested", "prefixed" })
            {
                var source = Path.Combine(root, layout + ".knxproj");
                var sourceBytes = Native22Fixture.Archive(layout);
                File.WriteAllBytes(source, sourceBytes);
                await using var server = new McpServer();
                var sequence = 20000;
                async Task<JsonNode> Tool(string name, object? arguments = null)
                {
                    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = JsonSerializer.SerializeToNode(arguments ?? new { }) } };
                    return JsonNode.Parse((await server.HandleAsync(request.ToJsonString()))!)!;
                }
                static bool Error(JsonNode response) => response["result"]?["isError"]?.GetValue<bool>() == true || response["error"] is not null;
                static JsonNode? Data(JsonNode response) => response["result"]?["structuredContent"];
                void Check(string label, bool value, JsonNode? response = null) => check($"MCP native22 {layout}: {label}", value, response?.ToJsonString());

                var opened = await Tool("project_open", new { path = source });
                Check("open uses the existing project tool", !Error(opened) && server.Project?.Id == Native22Fixture.ProjectId, opened);
                if (server.Project is null) continue;
                Check("native addresses, parameters, channels and links are available", Native22IntegrationFacts.Matches(server.Project, "Switch 22"));
                var beforeHistory = Data(await Tool("project_info"))?["undo"]?.ToJsonString();
                var changed = await Tool("group_address_update", new { address = "1/1/1", name = "Reviewed switch" });
                Check("edit succeeds", !Error(changed) && ProjectQuery.GroupAddresses(server.Project).Single(a => a.Id == Native22Fixture.GroupId).Name == "Reviewed switch", changed);
                var undone = await Tool("undo");
                Check("undo restores native data and metadata", !Error(undone)
                    && ProjectQuery.GroupAddresses(server.Project!).Single(a => a.Id == Native22Fixture.GroupId).Name == "Switch 22"
                    && server.Project!.Name == "Synthetic native 22", undone);
                var redone = await Tool("redo");
                Check("redo restores the reviewed edit", !Error(redone)
                    && ProjectQuery.GroupAddresses(server.Project!).Single(a => a.Id == Native22Fixture.GroupId).Name == "Reviewed switch", redone);
                var savedPath = Path.Combine(root, layout + "-saved.knxproj");
                var saved = await Tool("project_save", new { path = savedPath });
                Check("save succeeds through the existing tool", !Error(saved) && File.Exists(savedPath), saved);
                if (!File.Exists(savedPath)) continue;
                var parts = Native22Fixture.ProjectParts(savedPath, layout);
                var prefix = Native22Fixture.Prefix(layout);
                var xml = Native22Fixture.Xml(parts[prefix + "0.xml"]);
                XNamespace ns = Native22Fixture.Namespace;
                Check("saved data retain native22 namespace and segment topology", xml.Root?.Name == ns + "KNX"
                    && xml.Descendants(ns + "Segment").Count() == 1
                    && (string?)xml.Descendants(ns + "Segment").Single().Attribute("DomainAddress") == "42"
                    && (string?)xml.Descendants(ns + "DeviceInstance").Single().Attribute("Address") == "7");
                Check("opaque parts and input bytes survive", parts[prefix + "opaque.bin"].SequenceEqual(new byte[] { 0, 255, 22, 128 })
                    && SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(sourceBytes)));
                var reopened = await Tool("project_open", new { path = savedPath });
                Check("reopen retains edited values and native channel links", !Error(reopened)
                    && Native22IntegrationFacts.Matches(server.Project!, "Reviewed switch"), reopened);
                Check("native information and both ZIP layers retain their original facts", Native22IntegrationFacts.ArchivePartsMatch(savedPath, layout));

                var projectBeforeFailure = server.Project;
                var infoBeforeFailure = Data(await Tool("project_info"))!.ToJsonString();
                var ambiguous = Native22Fixture.Parts();
                foreach (var (name, bytes) in Native22Fixture.Parts(Native22Fixture.ProjectId + "/")) ambiguous.Add(name, bytes);
                var ambiguousPath = Path.Combine(root, layout + "-ambiguous.knxproj");
                File.WriteAllBytes(ambiguousPath, Native22Fixture.Zip(ambiguous));
                var refused = await Tool("project_open", new { path = ambiguousPath });
                Check("failed open retains the current project, path and history", Error(refused)
                    && ReferenceEquals(server.Project, projectBeforeFailure)
                    && Data(await Tool("project_info"))!.ToJsonString() == infoBeforeFailure, refused);

                var line = server.Project!.Installations.Single().Areas.Single().Lines.Single();
                var beforeFailedSave = Knxproj.ToXml(server.Project).ToString(SaveOptions.DisableFormatting);
                var beforeFailedSaveInfo = Data(await Tool("project_info"))!.ToJsonString();
                var unsupported = new Segment { Id = line.Segments.Single().Id, Address = 1 };
                line.Segments.Add(unsupported);
                var blockedPath = Path.Combine(root, layout + "-blocked.knxproj");
                byte[] sentinel = [7, 19, 23, 255];
                File.WriteAllBytes(blockedPath, sentinel);
                var blocked = await Tool("project_save", new { path = blockedPath });
                Check("duplicate segment identity cannot replace a target or remember its path", Error(blocked)
                    && File.ReadAllBytes(blockedPath).SequenceEqual(sentinel)
                    && ReferenceEquals(server.Project, projectBeforeFailure) && line.Segments.Count == 2
                    && ReferenceEquals(line.Segments[^1], unsupported)
                    && Data(await Tool("project_info"))!.ToJsonString() == beforeFailedSaveInfo, blocked);
                line.Segments.Remove(unsupported);
                Check("failed save changes neither the rest of the model nor history",
                    Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting) == beforeFailedSave
                    && Data(await Tool("project_info"))!.ToJsonString() == beforeFailedSaveInfo);
                Check("reopen resets history through the ordinary lifecycle", Data(await Tool("project_info"))?["undo"]?.ToJsonString() == beforeHistory);
            }
        }
        catch (Exception ex) { check("MCP native22 integration completes", false, ex.ToString()); }
        finally { Directory.Delete(root, true); }
    }
}
