using System.Globalization;
using System.Text;
using Knx;
using Knx.Workbench;

namespace Knx.Reports;

/// <summary>
/// Owner-facing template pages of the handover package: operating instructions, maintenance
/// contacts and data-protection notes. Every page is clearly marked as a template, names only
/// project-derived facts and leaves installer details as blank fill-in cells.
/// </summary>
public static partial class HandoverPackage
{
    private static string OperatingPage(Project project, string lang)
    {
        var devices = DeviceIndex(project);
        var body = new StringBuilder();
        body.AppendLine($"<p>{Escape(T("HpOperatingIntro", lang))}</p>");

        var nodes = LocationsWithDevices(project, devices).ToList();
        if (nodes.Count == 0 && !project.UnassignedDevices().Any())
        {
            body.AppendLine($"<p class=\"empty\">{Escape(T("HpOperatingNoDevices", lang))}</p>");
        }
        else
        {
            body.AppendLine("<table><thead><tr>" +
                            $"<th scope=\"col\">{Escape(T("HpOperatingRoom", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("HpOperatingDevices", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("HpOperatingFunction", lang))}</th>" +
                            "</tr></thead><tbody>");
            foreach (var (installation, node) in nodes)
            {
                var names = string.Join("<br>", node.DeviceRefs
                    .Where(devices.ContainsKey)
                    .Select(id => devices[id])
                    .OrderBy(d => d.Address.Raw)
                    .Select(d => $"{Escape(d.Name)} <span class=\"mono\">{Escape(d.Address.ToString())}</span>"));
                body.AppendLine("<tr>" +
                                $"<td>{Escape(LocationPath(installation, node))}</td>" +
                                $"<td>{names}</td>" +
                                "<td class=\"fill\"></td>" +
                                "</tr>");
            }

            foreach (var device in project.UnassignedDevices()
                         .OrderBy(d => d.Address.Raw).ThenBy(d => d.Id, StringComparer.Ordinal))
                body.AppendLine("<tr>" +
                                $"<td>{Escape(T("HpOperatingUnassigned", lang))}</td>" +
                                $"<td>{Escape(device.Name)} <span class=\"mono\">{Escape(device.Address.ToString())}</span></td>" +
                                "<td class=\"fill\"></td>" +
                                "</tr>");
            body.AppendLine("</tbody></table>");
        }

        if (nodes.Count == 0 && project.Installations.All(i => i.Locations.IsEmpty))
            body.AppendLine($"<p class=\"empty\">{Escape(T("HpOperatingNoRooms", lang))}</p>");
        return PageHtml(lang, T("HpOperatingTitle", lang), body.ToString());
    }

    private static string MaintenancePage(Project project, HandoverPackageOptions options, string lang)
    {
        var body = new StringBuilder();
        body.AppendLine($"<p>{Escape(T("HpMaintenanceIntro", lang))}</p>");
        body.AppendLine("<table class=\"facts\">");
        AppendFillFact(body, T("HpMaintenanceTechnician", lang), options.Technician);
        AppendFillFact(body, T("HpMaintenanceCompany", lang), null);
        AppendFillFact(body, T("HpMaintenancePhone", lang), null);
        AppendFillFact(body, T("HpMaintenanceEmail", lang), null);
        AppendFillFact(body, T("HpMaintenanceEmergency", lang), null);
        AppendFillFact(body, T("HpMaintenanceInterval", lang), null);
        AppendFillFact(body, T("HpMaintenanceOwner", lang), options.BuildingOwner);
        body.AppendLine("</table>");

        body.AppendLine($"<h2>{Escape(T("HpMaintenanceDevices", lang))}</h2>");
        var devices = ProjectQuery.DeviceList(project).OrderBy(d => d.Address.Raw).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        if (devices.Count == 0)
        {
            body.AppendLine($"<p class=\"empty\">{Escape(T("HpMaintenanceNoDevices", lang))}</p>");
        }
        else
        {
            body.AppendLine("<table><thead><tr>" +
                            $"<th scope=\"col\">{Escape(T("HpMaintenanceAddress", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("HpMaintenanceDevice", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("HpMaintenanceLast", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("HpMaintenanceNext", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("HpMaintenanceRemark", lang))}</th>" +
                            "</tr></thead><tbody>");
            foreach (var device in devices)
                body.AppendLine("<tr>" +
                                $"<td class=\"mono\">{Escape(device.Address.ToString())}</td>" +
                                $"<td>{Escape(device.Name)}</td>" +
                                "<td class=\"fill\"></td><td class=\"fill\"></td><td class=\"fill\"></td>" +
                                "</tr>");
            body.AppendLine("</tbody></table>");
        }

        return PageHtml(lang, T("HpMaintenanceTitle", lang), body.ToString());
    }

    private static string PrivacyPage(Project project, string lang)
    {
        var nodes = project.Installations.SelectMany(i => i.Locations.AllNodes).ToList();
        var deviceCount = ProjectQuery.DeviceList(project).Count();
        var addressCount = ProjectQuery.GroupAddresses(project).Count();
        var body = new StringBuilder();
        body.AppendLine($"<p>{Escape(T("HpPrivacyIntro", lang))}</p>");
        body.AppendLine($"<h2>{Escape(T("HpPrivacyClasses", lang))}</h2>");
        body.AppendLine("<ul>");
        body.AppendLine($"<li>{Escape(T("HpPrivacyClassProject", lang))}</li>");
        body.AppendLine($"<li>{Escape(string.Format(CultureInfo.InvariantCulture, T("HpPrivacyClassRooms", lang), nodes.Count))}</li>");
        body.AppendLine($"<li>{Escape(string.Format(CultureInfo.InvariantCulture, T("HpPrivacyClassDevices", lang), deviceCount))}</li>");
        body.AppendLine($"<li>{Escape(string.Format(CultureInfo.InvariantCulture, T("HpPrivacyClassGa", lang), addressCount))}</li>");
        body.AppendLine($"<li>{Escape(T("HpPrivacyClassContacts", lang))}</li>");
        body.AppendLine("</ul>");
        foreach (var key in new[] { "HpPrivacyPersonal", "HpPrivacyRetention", "HpPrivacyDeletion", "HpPrivacyNoSecrets" })
            body.AppendLine($"<p>{Escape(T(key, lang))}</p>");
        return PageHtml(lang, T("HpPrivacyTitle", lang), body.ToString());
    }

    /// <summary>Minimal self-contained page: inline CSS, no scripts, no external resources.</summary>
    private static string PageHtml(string lang, string heading, string body)
    {
        var html = new StringBuilder(body.Length + 2048);
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine($"<html lang=\"{lang}\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta name=\"generator\" content=\"BusChord\">");
        html.AppendLine($"<title>{Escape(heading)}</title>");
        html.AppendLine("<style>");
        html.AppendLine("  @page { size: A4; margin: 16mm 12mm 18mm; }");
        html.AppendLine("  body { margin: 0 auto; padding: 14mm; max-width: 210mm; color: #111;" +
                        " font-family: \"Segoe UI\", Arial, sans-serif; font-size: 10pt; line-height: 1.45; }");
        html.AppendLine("  h1 { font-size: 22pt; margin: 0 0 4mm 0; }");
        html.AppendLine("  h2 { font-size: 14pt; margin: 6mm 0 3mm 0; padding-bottom: 1.5mm; border-bottom: 0.4mm solid #333; }");
        html.AppendLine("  .kicker { font-size: 9pt; text-transform: uppercase; letter-spacing: 0.15em; color: #666; margin: 0 0 2mm 0; }");
        html.AppendLine("  table { width: 100%; border-collapse: collapse; font-size: 9pt; margin: 0 0 4mm 0; }");
        html.AppendLine("  th, td { text-align: left; padding: 1.5mm 2mm; border-bottom: 0.2mm solid #ccc; vertical-align: top; }");
        html.AppendLine("  th { background: #f2f2f2; }");
        html.AppendLine("  .facts th { width: 45mm; background: none; }");
        html.AppendLine("  .mono { font-family: Consolas, \"Courier New\", monospace; color: #444; }");
        html.AppendLine("  .fill { height: 7mm; }");
        html.AppendLine("  .notice { margin-top: 8mm; padding: 3mm; background: #f5f5f5; border-left: 1mm solid #888;" +
                        " font-size: 8.5pt; color: #333; }");
        html.AppendLine("  .empty { color: #555; font-style: italic; }");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"<p class=\"kicker\">{Escape(T("HpOwnerKicker", lang))}</p>");
        html.AppendLine($"<h1>{Escape(heading)}</h1>");
        html.Append(body);
        html.AppendLine($"<p class=\"notice\">{Escape(T("HpTemplateNotice", lang))}</p>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static void AppendFillFact(StringBuilder body, string label, string? value)
        => body.AppendLine("<tr>" +
                           $"<th scope=\"row\">{Escape(label)}</th>" +
                           (string.IsNullOrWhiteSpace(value)
                               ? "<td class=\"fill\"></td>"
                               : $"<td>{Escape(value)}</td>") +
                           "</tr>");

    private static Dictionary<string, DeviceInstance> DeviceIndex(Project project)
        => ProjectQuery.DeviceList(project)
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Nodes that directly reference a device, in native tree order, plus their installation.</summary>
    private static IEnumerable<(Installation Installation, SpaceNode Node)> LocationsWithDevices(
        Project project, IReadOnlyDictionary<string, DeviceInstance> devices)
    {
        foreach (var installation in project.Installations)
            foreach (var node in installation.Locations.AllNodes)
                if (node.DeviceRefs.Any(devices.ContainsKey))
                    yield return (installation, node);
    }

    private static string LocationPath(Installation installation, SpaceNode node)
        => string.IsNullOrWhiteSpace(installation.Name) ? node.Path : installation.Name + " / " + node.Path;
}
