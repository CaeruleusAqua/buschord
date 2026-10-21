using System.Text;

namespace Knx.Reports;

/// <summary>CSV export of generic report rows (project check, linking suggestions, diagnostics).</summary>
public static class ReportCsv
{
    public static string Render(IEnumerable<(string Severity, string Title, string Detail)> rows)
    {
        var sb = new StringBuilder(Strings.Get("ReportCsvHeader") + "\n");
        foreach (var row in rows)
            sb.AppendLine(Csv.Row(';', row.Severity, row.Title, row.Detail));
        return sb.ToString();
    }
}
