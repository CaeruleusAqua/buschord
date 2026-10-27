namespace Knx.App.Workbench;

/// <summary>The lower workbench tool pages and their persisted identifiers.</summary>
public static class WorkbenchTools
{
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { "monitor", "programming", "scan", "search", "changes", "problems" };

    public static bool IsKnown(string? tool) => tool is not null && Known.Contains(tool);
}
