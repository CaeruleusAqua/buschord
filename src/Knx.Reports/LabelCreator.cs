using System.Globalization;
using System.Text;
using Knx;

namespace Knx.Reports;

/// <summary>One printed label.</summary>
public sealed record Label(string Address, string Title, string? Subtitle, string? Extra);

public sealed class LabelOptions
{
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 8;
    public double LabelWidthMm { get; set; } = 46;
    public double LabelHeightMm { get; set; } = 25;
    public string SheetTitle { get; set; } = "";
    public bool ShowDescription { get; set; } = true;
    public bool ShowDatapointType { get; set; } = true;
}

/// <summary>
/// Etiketten-Druck (ETS: Label Creator). Produces a self-contained printable HTML sheet
/// and a CSV variant; both use the same label model so they never drift apart.
/// </summary>
public static class LabelCreator
{
    public static IReadOnlyList<Label> GroupAddressLabels(Project project, LabelOptions options)
        => ProjectQuery.GroupAddresses(project)
            .OrderBy(a => a.Address.Raw)
            .Select(a => new Label(
                a.Address.ToString(),
                a.Name,
                options.ShowDescription ? a.Description : null,
                options.ShowDatapointType ? a.DatapointType : null))
            .ToList();

    public static IReadOnlyList<Label> DeviceLabels(Project project, LabelOptions options)
        => ProjectQuery.Devices(project)
            .OrderBy(d => d.Device.Address.Raw)
            .Select(d => new Label(
                d.Device.Address.ToString(),
                d.Device.Name ?? "?",
                options.ShowDescription ? d.Device.Description : null,
                options.ShowDatapointType ? d.Line?.Name ?? d.Area.Name : null))
            .ToList();

    public static string ToCsv(IEnumerable<Label> labels)
    {
        var sb = new StringBuilder(Strings.Get("CsvLabelHeader") + "\n");
        foreach (var l in labels)
            sb.AppendLine($"{l.Address};{Escape(l.Title)};{Escape(l.Subtitle)};{Escape(l.Extra)}");
        return sb.ToString();

        static string Escape(string? value)
            => value is null ? "" : value.Contains(';') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    /// <summary>Printable A4 sheet; print with "background graphics" enabled to keep the frames.</summary>
    public static string ToHtml(IEnumerable<Label> labels, LabelOptions options)
    {
        var cells = new StringBuilder();
        foreach (var label in labels)
        {
            cells.Append("<div class=\"label\">");
            cells.Append($"<div class=\"addr\">{Html(label.Address)}</div>");
            cells.Append($"<div class=\"title\">{Html(label.Title)}</div>");
            if (!string.IsNullOrWhiteSpace(label.Subtitle))
                cells.Append($"<div class=\"sub\">{Html(label.Subtitle)}</div>");
            if (!string.IsNullOrWhiteSpace(label.Extra))
                cells.Append($"<div class=\"extra\">{Html(label.Extra)}</div>");
            cells.Append("</div>");
        }

        var columns = Math.Clamp(options.Columns, 1, 10);
        var title = Html(options.SheetTitle.Length > 0 ? options.SheetTitle : "Etiketten");
        var width = options.LabelWidthMm.ToString("0.##", CultureInfo.InvariantCulture);
        var height = options.LabelHeightMm.ToString("0.##", CultureInfo.InvariantCulture);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"de\"><head><meta charset=\"utf-8\">");
        html.AppendLine($"<title>{title}</title>");
        html.AppendLine("<style>");
        html.AppendLine("  @page { size: A4; margin: 8mm; }");
        html.AppendLine("  body { font-family: \"Segoe UI\", Arial, sans-serif; margin: 0; }");
        html.AppendLine("  h1 { font-size: 12pt; margin: 0 0 4mm 0; }");
        html.AppendLine($"  .sheet {{ display: grid; grid-template-columns: repeat({columns}, {width}mm); gap: 1.5mm; }}");
        html.AppendLine($"  .label {{ width: {width}mm; height: {height}mm; border: 0.2mm solid #999; border-radius: 1mm; " +
                        "padding: 1.2mm; box-sizing: border-box; overflow: hidden; page-break-inside: avoid; break-inside: avoid; }");
        html.AppendLine("  .addr { font-family: Consolas, monospace; font-size: 9pt; font-weight: 700; color: #003a70; }");
        html.AppendLine("  .title { font-size: 8pt; font-weight: 600; line-height: 1.15; margin-top: 0.5mm; }");
        html.AppendLine("  .sub { font-size: 6.5pt; color: #444; line-height: 1.1; }");
        html.AppendLine("  .extra { font-size: 6.5pt; color: #666; margin-top: 0.5mm; }");
        html.AppendLine("</style></head>");
        html.AppendLine("<body>");
        html.AppendLine($"<h1>{title}</h1>");
        html.AppendLine($"<div class=\"sheet\">{cells}</div>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static string Html(string? value)
        => value is null
            ? ""
            : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

/// <summary>
/// Label merge record with the fields of the ETS label creator (Seriendruck-Datenquelle):
/// Id, Description, IndividualAddress, Manufacturer, Product, Program, Room, Trade, ImageName.
/// ETS drives a Word mail merge from such a data source; we emit the same data source and
/// additionally render a printable HTML sheet.
/// </summary>
public sealed record MergeLabel(
    string Id,
    string Description,
    string IndividualAddress,
    string Manufacturer,
    string Product,
    string Program,
    string Room,
    string Trade,
    string ImageName);

public static class LabelMergeSource
{
    public static IReadOnlyList<MergeLabel> ForDevices(Project project)
        => ProjectQuery.Devices(project)
            .OrderBy(d => d.Device.Address.Raw)
            .Select(d => new MergeLabel(
                d.Device.Address.ToString(),
                d.Device.Name ?? "",
                d.Device.Address.ToString(),
                ShortManufacturer(d.Device.ProductRefId),
                d.Device.ProductRefId,
                d.Device.Hardware2ProgramRefId ?? "",
                d.Line?.Name ?? d.Area.Name ?? "",
                "",
                ""))
            .ToList();

    public static IReadOnlyList<MergeLabel> ForGroupAddresses(Project project)
        => ProjectQuery.GroupAddresses(project)
            .OrderBy(a => a.Address.Raw)
            .Select(a => new MergeLabel(
                a.Id,
                a.Name,
                "",
                "",
                "",
                "",
                ProjectQuery.FindRangeOf(project, a)?.Name ?? "",
                "",
                ""))
            .ToList();

    /// <summary>Tab separated data source with the merge field header, UTF-8 with BOM for Word.</summary>
    public static string ToDataSource(IEnumerable<MergeLabel> labels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id\tDescription\tIndividualAddress\tManufacturer\tProduct\tProgram\tRoom\tTrade\tImageName");
        foreach (var label in labels)
            sb.AppendLine(string.Join('\t',
                Clean(label.Id), Clean(label.Description), Clean(label.IndividualAddress),
                Clean(label.Manufacturer), Clean(label.Product), Clean(label.Program),
                Clean(label.Room), Clean(label.Trade), Clean(label.ImageName)));
        return sb.ToString();

        static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>Manufacturer part of a product reference ("M-0083_A-..." → "M-0083").</summary>
    private static string ShortManufacturer(string productRefId)
        => productRefId.Length >= 6 ? productRefId[..6] : productRefId;
}
