using System.Text.Json;
using Knx;
using Knx.Mcp;

internal static class TopologyTemplateToolChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        await using var server = new McpServer();
        async Task New(object args) { await server.Tools.Single(t => t.Name == "project_new").Handler(JsonSerializer.SerializeToElement(args)); }
        await New(new { name = "Legacy" });
        check("project_new defaults to existing zero-based template", server.Project!.Installations[0].Areas[0].Address == 0);
        await New(new { name = "TP", topology_template = "tp", areas = 2, lines = 3 });
        check("project_new supports explicit TP template", server.Project!.Installations[0].Areas[0].Address == 1 && server.Project.Installations[0].Areas[1].Lines[2].Address == 3);
        var original = server.Project;
        foreach (var args in new object[] { new { name = "Invalid", topology_template = "other" }, new { name = "Overflow", topology_template = "tp", areas = 16 } })
        {
            var rejected = false;
            try { await New(args); } catch (Exception) { rejected = true; }
            check("project_new rejects invalid template without replacing current project", rejected && ReferenceEquals(original, server.Project));
        }
    }
}
