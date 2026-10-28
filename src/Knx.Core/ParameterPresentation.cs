using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;

namespace Knx;

public sealed record ParameterPlacement(string ReferenceId, string? BlockId, int? Row, int? Column, string? HelpContext);
public sealed record ParameterPageLayout(string Id, string Title, int Rows, IReadOnlyList<double> ColumnWeights,
    IReadOnlyList<ParameterPlacement> Placements, bool IsTable, string? FallbackReason);

/// <summary>Presentation of the final selected dynamic pass; never evaluates scripts or edits values.</summary>
public sealed class ParameterPresentation
{
    public static ParameterPresentation Empty { get; } = new([], []);
    public IReadOnlyDictionary<string, ParameterPlacement> Placements { get; }
    public IReadOnlyDictionary<string, ParameterPageLayout> Pages { get; }
    private ParameterPresentation(Dictionary<string, ParameterPlacement> placements, Dictionary<string, ParameterPageLayout> pages)
    {
        Placements = new ReadOnlyDictionary<string, ParameterPlacement>(placements);
        Pages = new ReadOnlyDictionary<string, ParameterPageLayout>(pages);
    }

    internal static ParameterPresentation Read(XElement root, IReadOnlyList<XElement> active, IReadOnlyDictionary<string, string> names)
    {
        if (active.Count > 20000) return Empty;
        var placements = new Dictionary<string, ParameterPlacement>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in active)
        {
            if ((string?)element.Attribute("RefId") is not { Length: > 0 } id) continue;
            var block = element.Ancestors().FirstOrDefault(e => e.Name.LocalName == "ParameterBlock");
            var cell = ((string?)element.Attribute("Cell"))?.Split(',');
            int? row = cell is { Length: 2 } && int.TryParse(cell[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r - 1 : null;
            int? column = cell is { Length: 2 } && int.TryParse(cell[1], NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c > 0 ? c - 1 : null;
            var help = (string?)element.Attribute("HelpContext") ?? (string?)block?.Attribute("HelpContext");
            if (!placements.TryAdd(id, new(id, (string?)block?.Attribute("Id"), row, column, help))) duplicates.Add(id);
        }
        var pages = new Dictionary<string, ParameterPageLayout>(StringComparer.Ordinal);
        foreach (var block in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterBlock" && (string?)e.Attribute("Id") is not null
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")))
        {
            if (pages.Count >= 4096) return Empty;
            var id = (string)block.Attribute("Id")!;
            var fields = placements.Values.Where(p => p.BlockId == id).ToArray();
            if (fields.Length == 0) continue;
            var rows = block.Elements().FirstOrDefault(e => e.Name.LocalName == "Rows")?.Elements().Count(e => e.Name.LocalName == "Row") ?? 0;
            var columns = block.Elements().FirstOrDefault(e => e.Name.LocalName == "Columns")?.Elements().Where(e => e.Name.LocalName == "Column").ToArray() ?? [];
            var weights = columns.Select(e => double.TryParse(((string?)e.Attribute("Width"))?.TrimEnd('%'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var width) && double.IsFinite(width) && width > 0 && width <= 100 ? width : 1d).ToArray();
            string? reason = (string?)block.Attribute("Layout") != "Table" ? "NotTable"
                : rows is < 1 or > 64 || columns.Length is < 1 or > 8 || fields.Length > 128 ? "Bounds"
                : fields.Any(p => duplicates.Contains(p.ReferenceId) || p.Row is null || p.Column is null || p.Row >= rows || p.Column >= columns.Length) ? "Placement"
                : fields.GroupBy(p => (p.Row, p.Column)).Any(g => g.Count() != 1) ? "Overlap" : null;
            if (!pages.TryAdd(id, new(id, names.GetValueOrDefault(id) ?? (string?)block.Attribute("Text") ?? id,
                rows, Array.AsReadOnly(weights), Array.AsReadOnly(fields), reason is null, reason)))
                pages[id] = pages[id] with { IsTable = false, FallbackReason = "DuplicateBlock" };
        }
        return new(placements, pages);
    }
}
