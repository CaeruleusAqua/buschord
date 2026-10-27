using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Knx.ProjectCompare;

/// <summary>
/// Deterministic text/HTML rendering of a <see cref="DifferenceReport"/>. Items are sorted by
/// an ordinal, culture-independent key; line endings are always "\n"; revision source paths are
/// never rendered. The same report therefore yields the same text and hash on every run.
/// </summary>
public static class DifferenceReportRenderer
{
    public const int FormatVersion = 1;

    public static string RenderText(DifferenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.Append("BusChord project comparison\n");
        text.Append("format=").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("left=").Append(OneLine(report.LeftProjectName)).Append('\n');
        text.Append("right=").Append(OneLine(report.RightProjectName)).Append('\n');
        text.Append("generated=").Append(report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        AppendRevisionText(text, "left-revision", report.LeftRevision);
        AppendRevisionText(text, "right-revision", report.RightRevision);
        text.Append("revision-content=").Append(RevisionContent(report)).Append('\n');
        text.Append("revision-signature=").Append(RevisionSignature(report)).Append('\n');
        text.Append("added=").Append(report.AddedCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("removed=").Append(report.RemovedCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("changed=").Append(report.ChangedCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("unchanged=").Append(report.UnchangedCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var item in report.OrderedItems())
        {
            text.Append(ModificationText(item.Modification)).Append('\t')
                .Append(OneLine(item.Path)).Append('\t')
                .Append(OneLine(item.ObjectType)).Append('\t')
                .Append(OneLine(item.ObjectName));
            if (item.OldValue is not null) text.Append("\told=").Append(OneLine(item.OldValue));
            if (item.NewValue is not null) text.Append("\tnew=").Append(OneLine(item.NewValue));
            if (item.Details is not null) text.Append("\tdetails=").Append(OneLine(item.Details));
            text.Append('\n');
        }
        return text.ToString();
    }

    public static string RenderHtml(DifferenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n");
        html.Append("<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"generator\" content=\"BusChord\">\n");
        html.Append("<title>Project comparison</title>\n<style>\n").Append(StyleSheet).Append("\n</style>\n</head>\n<body>\n");
        html.Append("<h1>Project comparison</h1>\n");
        html.Append("<table class=\"facts\">\n");
        AppendFact(html, "Left", report.LeftProjectName);
        AppendFact(html, "Right", report.RightProjectName);
        AppendFact(html, "Generated", report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture));
        AppendFact(html, "Revision content", RevisionContent(report));
        AppendFact(html, "Revision signature", RevisionSignature(report));
        html.Append("</table>\n");

        if (report.LeftRevision is not null || report.RightRevision is not null)
        {
            html.Append("<h2>Revisions</h2>\n<table>\n<thead><tr>")
                .Append("<th scope=\"col\">Revision</th><th scope=\"col\">Timestamp</th>")
                .Append("<th scope=\"col\">SHA-256</th><th scope=\"col\">Signature</th>")
                .Append("<th scope=\"col\">Source SHA-256</th><th scope=\"col\">Description</th>")
                .Append("</tr></thead>\n<tbody>\n");
            AppendRevisionRow(html, report.LeftRevision);
            AppendRevisionRow(html, report.RightRevision);
            html.Append("</tbody>\n</table>\n");
        }

        html.Append("<h2>Differences</h2>\n");
        var items = report.OrderedItems();
        if (items.Count == 0)
        {
            html.Append("<p class=\"empty\">No differences.</p>\n");
        }
        else
        {
            html.Append("<table>\n<thead><tr>")
                .Append("<th scope=\"col\">Change</th><th scope=\"col\">Path</th>")
                .Append("<th scope=\"col\">Type</th><th scope=\"col\">Object</th>")
                .Append("<th scope=\"col\">Old value</th><th scope=\"col\">New value</th>")
                .Append("<th scope=\"col\">Details</th></tr></thead>\n<tbody>\n");
            foreach (var item in items)
            {
                html.Append("<tr><td>").Append(Escape(ModificationText(item.Modification)))
                    .Append("</td><td class=\"mono\">").Append(Escape(item.Path))
                    .Append("</td><td>").Append(Escape(item.ObjectType))
                    .Append("</td><td>").Append(Escape(item.ObjectName))
                    .Append("</td><td>").Append(Escape(item.OldValue ?? ""))
                    .Append("</td><td>").Append(Escape(item.NewValue ?? ""))
                    .Append("</td><td>").Append(Escape(item.Details ?? ""))
                    .Append("</td></tr>\n");
            }
            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>SHA-256 of the stable text rendering, uppercase hex.</summary>
    public static string RenderTextHash(DifferenceReport report) => Sha256(RenderText(report));

    /// <summary>SHA-256 of the stable HTML rendering, uppercase hex.</summary>
    public static string RenderHtmlHash(DifferenceReport report) => Sha256(RenderHtml(report));

    private static void AppendRevisionText(StringBuilder text, string label, ProjectRevision? revision)
    {
        text.Append(label).Append('=');
        if (revision is null)
        {
            text.Append("-\n");
            return;
        }
        text.Append("number:").Append(revision.Number.ToString(CultureInfo.InvariantCulture))
            .Append(" id:").Append(OneLine(revision.Id))
            .Append(" hash:").Append(revision.ContentHash)
            .Append(" algorithm:").Append(OneLine(revision.HashAlgorithm))
            .Append(" signature:").Append(SignatureText(revision))
            .Append(" source-hash:").Append(revision.SourceHash ?? "-")
            .Append(" timestamp:").Append(revision.Timestamp.ToString("O", CultureInfo.InvariantCulture))
            .Append(" description:").Append(revision.Description is null ? "-" : OneLine(revision.Description))
            .Append('\n');
    }

    private static void AppendRevisionRow(StringBuilder html, ProjectRevision? revision)
    {
        html.Append("<tr>");
        if (revision is null)
        {
            html.Append("<td>—</td><td>—</td><td>—</td><td>—</td><td>—</td><td>—</td>");
        }
        else
        {
            html.Append("<td class=\"mono\">").Append(revision.Number.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td class=\"mono\">")
                .Append(Escape(revision.Timestamp.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td><td class=\"mono\">").Append(Escape(revision.ContentHash))
                .Append("</td><td>").Append(Escape(SignatureText(revision)))
                .Append("</td><td class=\"mono\">").Append(Escape(revision.SourceHash ?? "—"))
                .Append("</td><td>").Append(Escape(revision.Description ?? "—"))
                .Append("</td>");
        }
        html.Append("</tr>\n");
    }

    private static void AppendFact(StringBuilder html, string label, string value)
    {
        html.Append("<tr><th scope=\"row\">").Append(Escape(label)).Append("</th><td>")
            .Append(Escape(value)).Append("</td></tr>\n");
    }

    private static string RevisionContent(DifferenceReport report) =>
        report.HasRevisions ? (report.ContentUnchanged ? "unchanged" : "changed") : "n/a";

    private static string RevisionSignature(DifferenceReport report) =>
        report.HasRevisions ? (report.SignatureUnchanged ? "unchanged" : "changed") : "n/a";

    private static string SignatureText(ProjectRevision revision) =>
        revision.SignatureStatus == RevisionSignatureStatus.Absent ? "absent" : "unverified";

    private static string ModificationText(ModificationType modification) => modification switch
    {
        ModificationType.Added => "added",
        ModificationType.Removed => "removed",
        ModificationType.Changed => "changed",
        _ => "unchanged",
    };

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static string Escape(string value) => WebUtility.HtmlEncode(OneLine(value));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string StyleSheet = """
        body { font-family: "Segoe UI", Arial, sans-serif; font-size: 10pt; color: #111;
               background: #fff; margin: 0 auto; padding: 14mm; max-width: 240mm; }
        h1 { font-size: 20pt; margin: 0 0 4mm 0; }
        h2 { font-size: 13pt; margin: 8mm 0 3mm 0; border-bottom: 0.4mm solid #333; }
        table { width: 100%; border-collapse: collapse; font-size: 9pt; margin: 0 0 4mm 0; }
        th, td { text-align: left; padding: 1.2mm 2mm; border-bottom: 0.2mm solid #ccc;
                 vertical-align: top; }
        th { background: #f2f2f2; font-weight: 600; }
        .facts { width: auto; min-width: 60%; }
        .facts th { width: 45mm; background: none; }
        .mono { font-family: Consolas, "Courier New", monospace; word-break: break-all; }
        .empty { color: #555; font-style: italic; }
        """;
}
