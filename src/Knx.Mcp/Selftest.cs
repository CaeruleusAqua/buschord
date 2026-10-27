using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knx.Mcp;

/// <summary>
/// End-to-end self check: initialize, tools/list, create project, add group address, check,
/// export Turtle, undo/redo. Run with <c>dotnet run --project src/Knx.Mcp -- --selftest</c>.
/// </summary>
internal static class Selftest
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
            if (!ok) failures++;
        }

        var temp = Path.Combine(Path.GetTempPath(), "knx-mcp-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        await using var server = new McpServer();

        try
        {
            var initialize = await Request(server, 1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "selftest", ["version"] = "0" },
            });
            var initResult = initialize["result"]!;
            Check("initialize protocolVersion", (string?)initResult["protocolVersion"] == "2025-06-18");
            Check("initialize server name", (string?)initResult["serverInfo"]?["name"] == McpServer.ServerName);
            Check("initialize capabilities.tools", initResult["capabilities"]?["tools"] is not null);
            Check("initialize instructions", !string.IsNullOrWhiteSpace((string?)initResult["instructions"]));

            var listed = await Request(server, 2, "tools/list", null);
            var tools = listed["result"]?["tools"]?.AsArray() ?? new JsonArray();
            Check("tools/list count", tools.Count == 55, tools.Count.ToString());
            Check("tools/list schemas",
                tools.All(tool => (string?)tool?["name"] is { Length: > 0 } &&
                                  (string?)tool?["inputSchema"]?["type"] == "object"));

            var created = await Tool(server, 3, "project_new", new
            {
                name = "MCP Selftest",
                style = "three_level",
                areas = 1,
                lines = 1,
            });
            Check("project_new", created["result"]?["isError"] is null, created.ToJsonString());

            var added = await Tool(server, 4, "group_address_add", new
            {
                address = "1/1/1",
                name = "Wohnzimmer Decke",
                datapoint_type = "1.001",
            });
            Check("group_address_add", added["result"]?["isError"] is null, added.ToJsonString());
            Check("group_address_add datapoint type",
                (string?)added["result"]?["structuredContent"]?["groupAddress"]?["datapointType"] == "DPST-1-1");

            var listedAddresses = await Tool(server, 5, "group_addresses_list");
            Check("group_addresses_list count",
                listedAddresses["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1);
            Check("group_addresses_list round trip",
                (string?)listedAddresses["result"]?["structuredContent"]?["groupAddresses"]?[0]?["name"] ==
                "Wohnzimmer Decke");

            var check = await Tool(server, 6, "project_check");
            Check("project_check has no errors",
                check["result"]?["structuredContent"]?["errors"]?.GetValue<int>() == 0, check.ToJsonString());

            var turtlePath = Path.Combine(temp, "project.ttl");
            var exported = await Tool(server, 7, "export_semantic", new { format = "turtle", path = turtlePath });
            Check("export_semantic turtle", exported["result"]?["isError"] is null && File.Exists(turtlePath),
                exported.ToJsonString());
            var turtle = File.Exists(turtlePath) ? File.ReadAllText(turtlePath) : "";
            Check("turtle contains ontology prefix", turtle.Contains("@prefix knx:"));
            Check("turtle contains group address", turtle.Contains("Wohnzimmer Decke"));

            var savePath = Path.Combine(temp, "selftest.knxproj");
            var saved = await Tool(server, 8, "project_save", new { path = savePath });
            Check("project_save", saved["result"]?["isError"] is null && File.Exists(savePath), saved.ToJsonString());

            var undo = await Tool(server, 9, "undo");
            Check("undo", undo["result"]?["isError"] is null, undo.ToJsonString());
            var afterUndo = await Tool(server, 10, "group_addresses_list");
            Check("undo removed the group address",
                afterUndo["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 0);

            var redo = await Tool(server, 11, "redo");
            Check("redo", redo["result"]?["isError"] is null, redo.ToJsonString());
            var afterRedo = await Tool(server, 12, "group_addresses_list");
            Check("redo restored the group address",
                afterRedo["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1);

            var unknown = await Tool(server, 13, "does_not_exist");
            Check("unknown tool reports isError", unknown["result"]?["isError"]?.GetValue<bool>() == true);

            var malformed = await server.HandleAsync("{not json");
            Check("malformed JSON is -32700",
                malformed is not null && JsonNode.Parse(malformed)?["error"]?["code"]?.GetValue<int>() == -32700);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }

        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures} failure(s))");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<JsonNode> Request(McpServer server, int id, string method, JsonNode? parameters)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) request["params"] = parameters;
        var response = await server.HandleAsync(request.ToJsonString());
        return JsonNode.Parse(response ?? "null") ?? new JsonObject();
    }

    private static async Task<JsonNode> Tool(McpServer server, int id, string name, object? arguments = null)
    {
        var parameters = new JsonObject { ["name"] = name };
        if (arguments is not null) parameters["arguments"] = JsonSerializer.SerializeToNode(arguments);
        return await Request(server, id, "tools/call", parameters);
    }
}
