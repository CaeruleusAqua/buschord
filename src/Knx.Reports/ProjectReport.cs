using System.Globalization;
using System.Text;
using Knx;

namespace Knx.Reports;

/// <summary>Which parts of the printable project documentation to render.</summary>
public sealed class ProjectReportOptions
{
    public bool IncludeTitlePage { get; set; } = true;
    public bool IncludeTopology { get; set; } = true;
    public bool IncludeGroupAddresses { get; set; } = true;
    public bool IncludeComObjects { get; set; } = true;
    public bool IncludeLinkMatrix { get; set; } = true;
    public bool IncludeFindings { get; set; } = true;
    /// <summary>Include the durable revision/signature section when the project has revisions.</summary>
    public bool IncludeRevisions { get; set; } = true;
    /// <summary>Report title; defaults to the project name.</summary>
    public string? Title { get; set; }
    /// <summary>Generation timestamp printed in header and title page; defaults to now.</summary>
    public DateTimeOffset? GeneratedAt { get; set; }
    /// <summary>Paper size for the print stylesheet: A4, A3 or Letter.</summary>
    public string PaperSize { get; set; } = "A4";
}

/// <summary>Written report file.</summary>
public sealed record ReportFile(string Path, long Bytes);

/// <summary>One row of the link matrix summary: a main group range and its link coverage.</summary>
public sealed record LinkMatrixRow(
    string Range,
    int GroupAddresses,
    int WithDatapointType,
    int Linked,
    int Unlinked,
    int Links);

/// <summary>
/// Printable project documentation (ETS: "Projekt dokumentieren"). Produces one self-contained
/// HTML document with inline CSS, print page breaks and page numbers; no external assets, no
/// scripts and no dependencies. A PDF export would need a rendering engine, so callers print the
/// HTML instead (see limitations in mcp.md).
/// </summary>
public static class ProjectReport
{
    public static string Render(Project project, ProjectReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new ProjectReportOptions();

        var generated = options.GeneratedAt ?? DateTimeOffset.Now;
        var title = string.IsNullOrWhiteSpace(options.Title) ? project.Name : options.Title!;
        var date = generated.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var dashboard = Dashboard.Build(project);
        var findings = ProjectCheck.Run(project);

        var html = new StringBuilder(32 * 1024);
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine($"<html lang=\"{(Strings.Language == "en" ? "en" : "de")}\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta name=\"generator\" content=\"BusChord\">");
        html.AppendLine($"<title>{Escape(title)} – {Escape(Strings.Get("ReportDocumentation"))}</title>");
        html.AppendLine("<style>");
        AppendStyle(html, options.PaperSize, title, date);
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        if (options.IncludeTitlePage) AppendTitlePage(html, project, title, date, dashboard, findings);
        if (options.IncludeTopology)
            AppendSection(html, "topology", Strings.Get("ReportTopology"), TopologyBody(project));
        if (options.IncludeGroupAddresses)
            AppendSection(html, "group-addresses", Strings.Get("ReportGroupAddresses"), GroupAddressBody(project));
        if (options.IncludeComObjects)
            AppendSection(html, "com-objects", Strings.Get("ReportComObjects"), ComObjectBody(project));
        if (options.IncludeLinkMatrix)
            AppendSection(html, "link-matrix", Strings.Get("ReportLinkMatrix"), LinkMatrixBody(project));
        if (options.IncludeRevisions && project.Workbench.Revisions.Count > 0)
            AppendSection(html, "revisions", "Revision", RevisionBody(project));
        if (options.IncludeFindings)
            AppendSection(html, "findings", Strings.Get("ReportFindings"), FindingsBody(findings));

        html.AppendLine($"<div class=\"page-footer\"><span>{Escape(title)}</span><span>{date}</span></div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>Writes the report as UTF-8 HTML, creating the target directory when needed.</summary>
    public static ReportFile Write(Project project, string path, ProjectReportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var html = Render(project, options);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, html, new UTF8Encoding(false));
        return new ReportFile(full, new FileInfo(full).Length);
    }

    /// <summary>Link coverage per main range: group addresses, datapoint types and link occurrences.</summary>
    public static IReadOnlyList<LinkMatrixRow> LinkMatrix(Project project)
    {
        var linksPerAddress = ProjectQuery.ComObjectRefs(project)
            .SelectMany(r => r.Ref.Links)
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = new List<LinkMatrixRow>();
        foreach (var installation in project.Installations)
            foreach (var range in installation.MainRanges)
            {
                var addresses = ProjectQuery.AllAddresses(range).ToList();
                var linked = addresses.Count(a => linksPerAddress.ContainsKey(a.Id));
                rows.Add(new LinkMatrixRow(
                    RangeLabel(range),
                    addresses.Count,
                    addresses.Count(a => !string.IsNullOrWhiteSpace(a.DatapointType)),
                    linked,
                    addresses.Count - linked,
                    addresses.Sum(a => linksPerAddress.GetValueOrDefault(a.Id))));
            }
        return rows;
    }

    // ---------------------------------------------------------------- sections

    private static void AppendTitlePage(StringBuilder html, Project project, string title, string date,
        DashboardStats dashboard, IReadOnlyList<CheckFinding> findings)
    {
        html.AppendLine("<section class=\"title-page\">");
        html.AppendLine($"<p class=\"kicker\">{Escape(Strings.Get("ReportDocumentation"))}</p>");
        html.AppendLine($"<h1>{Escape(title)}</h1>");
        if (!string.IsNullOrWhiteSpace(project.Comment))
            html.AppendLine($"<p class=\"lead\">{Escape(project.Comment)}</p>");

        html.AppendLine("<table class=\"facts\">");
        AppendFact(html, Strings.Get("ReportFactId"), project.Id);
        AppendFact(html, Strings.Get("ReportFactCreated"), date);
        if (project.ProjectStart is { } start)
            AppendFact(html, Strings.Get("ReportFactStart"), start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(project.Guid)) AppendFact(html, Strings.Get("ReportFactGuid"), project.Guid!);
        if (ProjectRevisions.Latest(project) is { } latest)
            AppendFact(html, "Revision", $"{latest.Number} · {latest.Timestamp:dd.MM.yyyy HH:mm}");
        AppendFact(html, "Signature", ProjectSignatureText(project));
        html.AppendLine("</table>");

        html.AppendLine("<div class=\"figures\">");
        AppendFigure(html, dashboard.Installations, Strings.Get("ReportFigureInstallations"));
        AppendFigure(html, dashboard.Areas, Strings.Get("ReportFigureAreas"));
        AppendFigure(html, dashboard.Lines, Strings.Get("ReportFigureLines"));
        AppendFigure(html, dashboard.Devices, Strings.Get("ReportFigureDevices"));
        AppendFigure(html, dashboard.GroupAddresses, Strings.Get("ReportFigureGroupAddresses"));
        AppendFigure(html, dashboard.GroupAddressesWithDpt, Strings.Get("ReportFigureWithDpt"));
        AppendFigure(html, dashboard.ComObjects, Strings.Get("ReportFigureComObjects"));
        AppendFigure(html, dashboard.LinkedGroupAddresses, Strings.Get("ReportFigureLinked"));
        AppendFigure(html, dashboard.DptTypes, Strings.Get("ReportFigureDptTypes"));
        AppendFigure(html, ProjectCheck.Count(findings, CheckSeverity.Error), Strings.Get("ReportFigureErrors"));
        AppendFigure(html, ProjectCheck.Count(findings, CheckSeverity.Warning), Strings.Get("ReportFigureWarnings"));
        AppendFigure(html, ProjectCheck.Count(findings, CheckSeverity.Info), Strings.Get("ReportFigureInfos"));
        html.AppendLine("</div>");
        html.AppendLine("</section>");
    }

    private static string TopologyBody(Project project)
    {
        var html = new StringBuilder();
        if (!ProjectQuery.DeviceList(project).Any())
        {
            html.AppendLine($"<p class=\"empty\">{Escape(Strings.Get("ReportNoDevices"))}</p>");
            return html.ToString();
        }

        foreach (var installation in project.Installations)
        {
            html.AppendLine($"<h3>{Titled(Strings.Get("ReportInstallationTitle"), installation.Name)}</h3>");
            foreach (var area in installation.Areas)
            {
                html.AppendLine($"<h3>{Titled(Strings.Get("ReportAreaTitle", area.Address), area.Name)}</h3>");
                if (area.Devices.Count > 0) html.Append(DeviceTable(area.Devices));
                foreach (var line in area.Lines)
                {
                    html.AppendLine($"<h4>{Titled(Strings.Get("ReportLineTitle", $"{area.Address}.{line.Address}"), line.Name)}</h4>");
                    if (line.Devices.Count > 0) html.Append(DeviceTable(line.Devices));
                    foreach (var segment in line.Segments)
                    {
                        html.AppendLine($"<h5>{Titled(Strings.Get("ReportSegmentTitle", $"{area.Address}.{line.Address}.{segment.Address}"), segment.Name)}</h5>");
                        if (segment.Devices.Count > 0) html.Append(DeviceTable(segment.Devices));
                    }
                }
            }
        }
        return html.ToString();
    }

    private static string DeviceTable(IEnumerable<DeviceInstance> devices)
    {
        var html = new StringBuilder();
        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(Strings.Get("ReportColAddress"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColDevice"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColProductRef"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColDescription"))}</th></tr></thead><tbody>");
        foreach (var device in devices.OrderBy(d => d.Address.Raw))
            html.AppendLine("<tr>" +
                            $"<td class=\"mono\">{device.Address}</td>" +
                            $"<td>{Show(device.Name)}</td>" +
                            $"<td class=\"mono\">{Show(device.ProductRefId)}</td>" +
                            $"<td>{Show(device.Description)}</td>" +
                            "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string GroupAddressBody(Project project)
    {
        var html = new StringBuilder();
        var addresses = ProjectQuery.GroupAddresses(project).OrderBy(a => a.Address.Raw).ToList();
        if (addresses.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(Strings.Get("ReportNoGroupAddresses"))}</p>");
            return html.ToString();
        }

        var rangeNames = new Dictionary<GroupAddressEntry, string>();
        foreach (var installation in project.Installations)
            foreach (var range in ProjectQuery.AllRanges(installation))
                foreach (var entry in range.Addresses)
                    rangeNames[entry] = RangeLabel(range);

        var links = new Dictionary<string, List<string>>();
        foreach (var (device, comObject) in ProjectQuery.ComObjectRefs(project))
            foreach (var link in comObject.Links)
            {
                if (!links.TryGetValue(link, out var list)) links[link] = list = [];
                list.Add($"{device.Address} · {comObject.Text ?? comObject.ComObjectId}");
            }

        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(Strings.Get("ReportColAddress"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColName"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColDpt"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColRange"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColLinks"))}</th></tr></thead><tbody>");
        foreach (var entry in addresses)
            html.AppendLine("<tr>" +
                            $"<td class=\"mono\">{entry.Address}</td>" +
                            $"<td>{Show(entry.Name)}</td>" +
                            $"<td class=\"mono\">{Show(entry.DatapointType)}</td>" +
                            $"<td>{Show(rangeNames.GetValueOrDefault(entry))}</td>" +
                            $"<td class=\"links\">{LinkCell(links.GetValueOrDefault(entry.Id))}</td>" +
                            "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string ComObjectBody(Project project)
    {
        var html = new StringBuilder();
        var refs = ProjectQuery.ComObjectRefs(project)
            .OrderBy(r => r.Device.Address.Raw)
            .ThenBy(r => r.Ref.Number)
            .ThenBy(r => r.Ref.Id, StringComparer.Ordinal)
            .ToList();
        if (refs.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(Strings.Get("ReportNoComObjects"))}</p>");
            return html.ToString();
        }

        var addresses = ProjectQuery.GroupAddresses(project).ToDictionary(a => a.Id, a => a.Address.ToString());
        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(Strings.Get("ReportColDevice"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColAddress"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColNumber"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColObject"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColLinkedGroupAddresses"))}</th></tr></thead><tbody>");
        foreach (var (device, comObject) in refs)
        {
            var links = comObject.Links.Count == 0
                ? Dash
                : string.Join(", ", comObject.Links.Select(id =>
                    addresses.TryGetValue(id, out var address) ? address : Strings.Get("ReportUnknownReference", id)));
            html.AppendLine("<tr>" +
                            $"<td>{Show(device.Name)}</td>" +
                            $"<td class=\"mono\">{device.Address}</td>" +
                            $"<td class=\"mono\">{comObject.Number}</td>" +
                            $"<td>{Show(comObject.Text ?? comObject.ComObjectId)}</td>" +
                            $"<td>{Show(links)}</td>" +
                            "</tr>");
        }
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string LinkMatrixBody(Project project)
    {
        var html = new StringBuilder();
        var rows = LinkMatrix(project);
        if (rows.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(Strings.Get("ReportNoRanges"))}</p>");
            return html.ToString();
        }

        html.AppendLine($"<p>{Escape(Strings.Get("ReportLinkMatrixIntro"))}</p>");
        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(Strings.Get("ReportColRange"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportFigureGroupAddresses"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColWithDpt"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColLinked"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColUnlinked"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColLinks"))}</th></tr></thead><tbody>");
        foreach (var row in rows)
            html.AppendLine("<tr>" +
                            $"<td>{Escape(row.Range)}</td>" +
                            $"<td class=\"num\">{row.GroupAddresses}</td>" +
                            $"<td class=\"num\">{row.WithDatapointType}</td>" +
                            $"<td class=\"num\">{row.Linked}</td>" +
                            $"<td class=\"num\">{row.Unlinked}</td>" +
                            $"<td class=\"num\">{row.Links}</td>" +
                            "</tr>");
        html.AppendLine("<tr class=\"total\">" +
                        $"<td>{Escape(Strings.Get("ReportTotal"))}</td>" +
                        $"<td class=\"num\">{rows.Sum(r => r.GroupAddresses)}</td>" +
                        $"<td class=\"num\">{rows.Sum(r => r.WithDatapointType)}</td>" +
                        $"<td class=\"num\">{rows.Sum(r => r.Linked)}</td>" +
                        $"<td class=\"num\">{rows.Sum(r => r.Unlinked)}</td>" +
                        $"<td class=\"num\">{rows.Sum(r => r.Links)}</td>" +
                        "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string RevisionBody(Project project)
    {
        var revisions = project.Workbench.Revisions
            .OrderBy(r => r.Number)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var html = new StringBuilder();
        html.AppendLine($"<p class=\"mono\">SHA-256: {Escape(ProjectRevisions.ContentHash(project))}</p>");
        html.AppendLine($"<p>Signature: {Escape(ProjectSignatureText(project))}</p>");
        html.AppendLine($"<table><thead><tr><th scope=\"col\">Revision</th>" +
                        $"<th scope=\"col\">Timestamp</th>" +
                        $"<th scope=\"col\">SHA-256</th>" +
                        $"<th scope=\"col\">Signature</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColDescription"))}</th></tr></thead><tbody>");
        foreach (var revision in revisions)
            html.AppendLine("<tr>" +
                            $"<td class=\"mono\">{revision.Number}</td>" +
                            $"<td class=\"mono\">{Escape(revision.Timestamp.ToString("O", CultureInfo.InvariantCulture))}</td>" +
                            $"<td class=\"mono\">{Escape(revision.ContentHash)}</td>" +
                            $"<td>{Escape(RevisionSignatureText(revision))}</td>" +
                            $"<td>{Show(revision.Description)}</td>" +
                            "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string ProjectSignatureText(Project project) =>
        ProjectRevisions.SignatureStatus(project) == RevisionSignatureStatus.Absent ? "absent" : "unverified";

    private static string RevisionSignatureText(ProjectRevision revision) =>
        revision.SignatureStatus == RevisionSignatureStatus.Absent ? "absent" : "unverified";

    private static string FindingsBody(IReadOnlyList<CheckFinding> findings)
    {
        var html = new StringBuilder();
        html.AppendLine($"<p>{Escape(Strings.Get("ReportFindingsSummary",
            ProjectCheck.Count(findings, CheckSeverity.Error),
            ProjectCheck.Count(findings, CheckSeverity.Warning),
            ProjectCheck.Count(findings, CheckSeverity.Info)))}</p>");
        if (findings.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(Strings.Get("ReportNoFindings"))}</p>");
            return html.ToString();
        }

        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(Strings.Get("ReportColSeverity"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColCode"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColPath"))}</th>" +
                        $"<th scope=\"col\">{Escape(Strings.Get("ReportColMessage"))}</th></tr></thead><tbody>");
        foreach (var finding in findings.OrderByDescending(f => f.Severity))
            html.AppendLine("<tr>" +
                            $"<td class=\"sev\">{Escape(Severity(finding.Severity))}</td>" +
                            $"<td class=\"mono\">{Escape(finding.Code)}</td>" +
                            $"<td>{Escape(finding.Path)}</td>" +
                            $"<td>{Escape(finding.Message)}</td>" +
                            "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    // ---------------------------------------------------------------- layout

    private static void AppendStyle(StringBuilder html, string paperSize, string title, string date)
    {
        var paper = paperSize.ToUpperInvariant() switch
        {
            "A3" => "A3",
            "LETTER" => "Letter",
            _ => "A4",
        };

        html.AppendLine($"  @page {{ size: {paper}; margin: 16mm 12mm 18mm;");
        html.AppendLine($"    @top-left {{ content: \"{Css(title)}\"; font-size: 8pt; color: #555; }}");
        html.AppendLine($"    @bottom-left {{ content: \"{Css(date)}\"; font-size: 8pt; color: #555; }}");
        html.AppendLine($"    @bottom-right {{ content: \"{Css(Strings.Get("ReportPageOf"))}\" counter(page) \" / \" counter(pages); font-size: 8pt; color: #555; }}");
        html.AppendLine("  }");
        html.AppendLine(StyleSheet);
    }

    /// <summary>Static screen and print rules; raw literal so CSS braces stay literal.</summary>
    private const string StyleSheet = """
          html { background: #f4f4f4; }
          body { margin: 0 auto; padding: 14mm; max-width: 210mm; background: #fff; color: #111;
                 font-family: "Segoe UI", Arial, sans-serif; font-size: 10pt; line-height: 1.45; }
          h1 { font-size: 24pt; margin: 0 0 4mm 0; line-height: 1.15; }
          h2 { font-size: 15pt; margin: 0 0 4mm 0; padding-bottom: 1.5mm; border-bottom: 0.4mm solid #333; }
          h3 { font-size: 12pt; margin: 6mm 0 2mm 0; }
          h4 { font-size: 10.5pt; margin: 4mm 0 1.5mm 0; }
          h5 { font-size: 9.5pt; margin: 3mm 0 1mm 0; color: #333; }
          p { margin: 0 0 3mm 0; }
          .kicker { font-size: 9pt; text-transform: uppercase; letter-spacing: 0.15em; color: #666; margin: 0 0 2mm 0; }
          .lead { font-size: 11pt; color: #333; }
          .title-page { page-break-after: always; break-after: page; }
          .report-section { page-break-before: always; break-before: page; }
          table { width: 100%; border-collapse: collapse; font-size: 9pt; margin: 0 0 4mm 0; }
          th, td { text-align: left; padding: 1.2mm 2mm; border-bottom: 0.2mm solid #ccc; vertical-align: top; }
          thead { display: table-header-group; }
          tr { page-break-inside: avoid; break-inside: avoid; }
          th { background: #f2f2f2; color: #222; font-weight: 600; }
          tr.total td { border-top: 0.4mm solid #333; font-weight: 600; }
          .mono { font-family: Consolas, "Courier New", monospace; white-space: nowrap; }
          .num { text-align: right; font-variant-numeric: tabular-nums; }
          .links { color: #333; }
          .sev { font-weight: 600; }
          .empty { color: #555; font-style: italic; }
          .facts { width: auto; min-width: 60%; }
          .facts th { width: 40mm; background: none; font-weight: 600; }
          .figures { display: grid; grid-template-columns: repeat(3, 1fr); gap: 2mm 4mm; margin-top: 6mm; }
          .figure { padding: 3mm; background: #f5f5f5; border-radius: 1.5mm; }
          .figure .value { font-size: 16pt; font-weight: 700; line-height: 1.1; }
          .figure .label { font-size: 8pt; color: #555; }
          .page-footer { display: none; }
          @media print {
            html, body { background: #fff; }
            body { margin: 0; padding: 0; max-width: none; font-size: 10pt; }
            a { color: inherit; text-decoration: none; }
            .figure { background: #f5f5f5; }
            .page-footer { display: block; position: fixed; left: 0; right: 0; bottom: 0;
                           font-size: 8pt; color: #555; border-top: 0.2mm solid #bbb; padding-top: 1mm; }
            .page-footer span:last-child { float: right; }
          }
        """;

    // ---------------------------------------------------------------- helpers

    private const string Dash = "—";

    private static string Show(string? value)
        => string.IsNullOrWhiteSpace(value) ? Dash : Escape(value);

    private static string RangeLabel(GroupRange range)
        => string.IsNullOrWhiteSpace(range.Name)
            ? $"{range.RangeStart}..{range.RangeEnd}"
            : $"{range.Name} ({range.RangeStart}..{range.RangeEnd})";

    private static string Titled(string prefix, string? name)
        => string.IsNullOrWhiteSpace(name) ? Escape(prefix) : $"{Escape(prefix)} – {Escape(name)}";

    private static string Severity(CheckSeverity severity) => severity switch
    {
        CheckSeverity.Error => Strings.Get("ReportSeverityError"),
        CheckSeverity.Warning => Strings.Get("ReportSeverityWarning"),
        _ => Strings.Get("ReportSeverityInfo"),
    };

    private static string LinkCell(List<string>? links)
        => links is null || links.Count == 0
            ? Dash
            : $"{links.Count}×<br>{string.Join("<br>", links.Select(Escape))}";

    private static void AppendFact(StringBuilder html, string label, string value)
        => html.AppendLine($"<tr><th scope=\"row\">{Escape(label)}</th><td>{Escape(value)}</td></tr>");

    private static void AppendFigure(StringBuilder html, int value, string label)
        => html.AppendLine($"<div class=\"figure\"><div class=\"value\">{value}</div>" +
                           $"<div class=\"label\">{Escape(label)}</div></div>");

    private static void AppendSection(StringBuilder html, string id, string heading, string body)
    {
        html.AppendLine($"<section class=\"report-section\" id=\"{id}\">");
        html.AppendLine($"<h2>{Escape(heading)}</h2>");
        html.Append(body);
        html.AppendLine("</section>");
    }

    /// <summary>Escapes every user value for HTML text and attribute context.</summary>
    private static string Escape(string? value)
        => value is null
            ? ""
            : value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");

    /// <summary>Escapes a value for use inside a CSS string literal in the inline stylesheet.</summary>
    private static string Css(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r':
                case '\n': sb.Append(' '); break;
                case '<': sb.Append("\\3C "); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
