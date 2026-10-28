using System.Text.Json;
using System.Reflection;
using Knx;
using Knx.Bus;
using Knx.Mcp;
using Knx.Workbench;

internal static class MassLinkToolChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        void Check(string name, bool value) => check("mass link tools: " + name, value);
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "buschord-mass-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "synthetic.knxproj");
        Knxproj.Save(Fixture(), path);
        await using var server = new McpServer();
        async Task<JsonElement> Tool(string name, object args)
        {
            var result = await server.Tools.Single(tool => tool.Name == name).Handler(JsonSerializer.SerializeToElement(args));
            return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        async Task Open() { await Tool("project_open", new { path }); }
        try
        {
            await Open();
            var reviewSchema = server.Tools.Single(tool => tool.Name == "mass_link_review").InputSchema;
            var applySchema = server.Tools.Single(tool => tool.Name == "mass_link_apply").InputSchema;
            Check("review schema exposes bounded nullable target updates", reviewSchema["properties"]?["targets"]?["type"]?.GetValue<string>() == "object"
                && reviewSchema["properties"]?["targets"]?["maxProperties"]?.GetValue<int>() == MassLinkReview.MaximumRows
                && reviewSchema["properties"]?["targets"]?["additionalProperties"]?["type"]?.AsArray().Select(item => item!.GetValue<string>()).SequenceEqual(["string", "null"]) == true);
            Check("apply schema requires token and exact unique row ids", applySchema["required"]!.AsArray().Any(item => item!.GetValue<string>() == "review_token")
                && applySchema["properties"]?["row_ids"]?["type"]?.GetValue<string>() == "array"
                && applySchema["properties"]?["row_ids"]?["uniqueItems"]?.GetValue<bool>() == true
                && applySchema["properties"]?["row_ids"]?["maxItems"]?.GetValue<int>() == MassLinkReview.MaximumRows);
            Check("single-link strict option is a boolean", server.Tools.Single(tool => tool.Name == "group_link_add").InputSchema
                ["properties"]?["require_compatibility"]?["type"]?.GetValue<string>() == "boolean");
            var review = await Tool("mass_link_review", new { });
            var token = review.GetProperty("reviewToken").GetString()!;
            var rows = review.GetProperty("rows").EnumerateArray().ToArray();
            var ready = rows.Single(row => row.GetProperty("state").GetString() == "ready").GetProperty("id").GetString()!;
            var blocked = rows.Single(row => row.GetProperty("state").GetString() == "incompatible").GetProperty("id").GetString()!;
            Check("review reports ready incompatible and unverified rows", rows.Length == 3
                && review.GetProperty("counts").GetProperty("ready").GetInt32() == 1
                && review.GetProperty("counts").GetProperty("unverified").GetInt32() == 1);
            var before = Knxproj.ToXml(server.Project!).ToString();
            var rejected = false;
            try { await Tool("mass_link_review", new { review_token = token, targets = new[] { "invalid-shape" } }); }
            catch (Exception) { rejected = true; }
            Check("malformed target updates do not mutate project", rejected && Knxproj.ToXml(server.Project!).ToString() == before);
            rejected = false;
            try { await Tool("mass_link_apply", new { review_token = token, row_ids = new[] { ready, ready } }); }
            catch (Exception) { rejected = true; }
            Check("duplicate apply row ids are refused", rejected && Knxproj.ToXml(server.Project!).ToString() == before);
            rejected = false;
            try { await Tool("mass_link_apply", new { review_token = token, row_ids = ready }); }
            catch (Exception) { rejected = true; }
            Check("apply selection must be an array", rejected && Knxproj.ToXml(server.Project!).ToString() == before);
            rejected = false;
            try { await Tool("mass_link_apply", new { review_token = token, row_ids = new[] { ready, blocked } }); }
            catch (Exception) { rejected = true; }
            Check("incompatible mixed apply has no partial mutation", rejected && Knxproj.ToXml(server.Project!).ToString() == before);
            var applied = await Tool("mass_link_apply", new { review_token = token, row_ids = new[] { ready } });
            Check("strict ready apply records one undo", applied.GetProperty("applied").GetInt32() == 1 && applied.GetProperty("compatibilityVerified").GetBoolean()
                && applied.GetProperty("undo").GetProperty("steps").GetInt32() == 1);
            rejected = false;
            try { await Tool("mass_link_apply", new { review_token = token }); } catch (Exception) { rejected = true; }
            Check("review token is single use", rejected);
            await Tool("undo", new { });
            Check("one undo restores routed batch", Knxproj.ToXml(server.Project!).ToString() == before);
            review = await Tool("mass_link_review", new { }); token = review.GetProperty("reviewToken").GetString()!;
            server.Project!.Name = "Changed source";
            rejected = false;
            try { await Tool("mass_link_apply", new { review_token = token }); } catch (Exception) { rejected = true; }
            Check("changed source invalidates MCP review", rejected && ProjectQuery.ComObjectRefs(server.Project!).All(pair => pair.Ref.Links.Count == 0));

            await Open();
            before = Knxproj.ToXml(server.Project!).ToString();
            rejected = false;
            try { await Tool("group_link_add", new { device_address = "1.1.1", object_id = "O1", group_address = "1/0/1", require_compatibility = "true" }); }
            catch (Exception) { rejected = true; }
            Check("strict compatibility flag rejects string coercion", rejected && before == Knxproj.ToXml(server.Project!).ToString());
            rejected = false;
            try { await Tool("group_link_add", new { device_address = "1.1.2", object_id = "O1", group_address = "1/0/1" }); }
            catch (Exception error) { rejected = error.Message.Contains("BitSize", StringComparison.Ordinal); }
            Check("manual link rejects known bit conflict", rejected && before == Knxproj.ToXml(server.Project!).ToString());
            rejected = false;
            try { await Tool("group_link_add", new { device_address = "1.1.3", object_id = "O1", group_address = "1/0/1", require_compatibility = true }); }
            catch (Exception error) { rejected = error.Message.Contains("UnknownCapability", StringComparison.Ordinal); }
            Check("manual strict option refuses missing facts", rejected && before == Knxproj.ToXml(server.Project!).ToString());
            var manual = await Tool("group_link_add", new { device_address = "1.1.3", object_id = "O1", group_address = "1/0/1" });
            Check("legacy manual unknown is explicitly unverified", manual.GetProperty("changed").GetBoolean() && !manual.GetProperty("compatibilityVerified").GetBoolean()
                && manual.GetProperty("warnings").GetArrayLength() == 1 && manual.GetProperty("undo").GetProperty("steps").GetInt32() == 1);
            await Open(); before = Knxproj.ToXml(server.Project!).ToString(); rejected = false;
            try { await Tool("smart_link_apply", new { }); } catch (Exception error) { rejected = error.Message.Contains("BitSize", StringComparison.Ordinal); }
            Check("legacy smart batch rejects known conflict atomically", rejected && before == Knxproj.ToXml(server.Project!).ToString());
            await Tool("recorder_start", new { });
            var recorder = (TelegramRecorder)typeof(McpServer).GetField("_recorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            recorder.Add(new BusTelegram { Source = "synthetic", Telegram = new Telegram
            {
                Source = new(1, 1, 1), Destination = new(GroupAddress.ThreeLevel(1, 0, 1).Raw), IsGroupAddress = true,
                Apci = Apci.GroupValueWrite, Data = [1], HopCount = 3,
            } });
            var capture = await Tool("recorder_stop", new { });
            Check("recorder response preserves captured nondefault hop count", capture.GetProperty("telegrams")[0].GetProperty("hopCount").GetInt32() == 3);
            recorder.Entries[0].HopCount = null;
            capture = await Tool("recorder_stop", new { });
            Check("recorder response never invents unknown hop count", !capture.GetProperty("telegrams")[0].TryGetProperty("hopCount", out var unknownHop)
                || unknownHop.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(parent)
                && Path.GetFileName(resolved).StartsWith("buschord-mass-mcp-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static Project Fixture()
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic mass MCP" });
        project.Installations[0].Areas[0].Address = 1; project.Installations[0].Areas[0].Lines[0].Address = 1;
        var range = new GroupRange { Id = "GR-MCP", Name = "Synthetic", RangeStart = 0, RangeEnd = 65535 };
        range.Addresses.Add(new() { Id = "G1", Name = "Hall light", DatapointType = "1.001", Address = GroupAddress.ThreeLevel(1, 0, 1) });
        project.Installations[0].MainRanges.Add(range); project.Workbench.Groups["G1"] = new() { Bits = 1, Secure = false };
        for (byte index = 1; index <= 3; index++)
        {
            var device = new DeviceInstance { Id = "D" + index, Name = "Hall", ProductRefId = "SYNTHETIC", Address = new(1, 1, index) };
            device.ComObjectRefs.Add(new() { Id = "O1", ComObjectId = "O1", Number = 1, Text = "light" });
            project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
            if (index < 3) project.Workbench.Objects[new ObjectKey(device.Id, "O1").ToString()] = new() { Active = true, Dpt = "1.001", Bits = index == 1 ? 1 : 8 };
        }
        return project;
    }
}
