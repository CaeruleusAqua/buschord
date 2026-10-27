using System.Globalization;
using System.Text;
using Knx;
using Knx.Workbench;

namespace Knx.Reports;

/// <summary>CSV artifacts of the handover package: room book, link summary, parameter evidence and revisions.</summary>
public static partial class HandoverPackage
{
    /// <summary>
    /// Room book from the native Locations tree: one row per location and device, then every
    /// device that no node references. Tree order is the native order, devices sort by address.
    /// </summary>
    private static string RoomBookCsv(Project project, string lang)
    {
        var devices = ProjectQuery.DeviceList(project)
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append(Csv.Row(';',
            T("HpRoomBookPath", lang), T("HpRoomBookKind", lang), T("HpRoomBookFloor", lang),
            T("HpRoomBookDevice", lang), T("HpRoomBookAddress", lang), T("HpRoomBookProduct", lang),
            T("HpRoomBookCount", lang))).Append("\r\n");

        foreach (var installation in project.Installations)
        {
            foreach (var root in installation.Locations.Roots)
                AppendNode(root, installation);
        }

        foreach (var device in project.UnassignedDevices().OrderBy(d => d.Address.Raw).ThenBy(d => d.Id, StringComparer.Ordinal))
            sb.Append(Csv.Row(';', "", T("HpRoomBookUnassigned", lang), "", device.Name,
                device.Address.ToString(), device.ProductRefId, "1")).Append("\r\n");
        return sb.ToString();

        void AppendNode(SpaceNode node, Installation installation)
        {
            var path = string.IsNullOrWhiteSpace(installation.Name)
                ? node.Path
                : installation.Name + " / " + node.Path;
            var floor = FloorOf(node);
            var count = BuildingTree.DevicesUnder(node).Count(devices.ContainsKey);
            var direct = node.DeviceRefs
                .Where(devices.ContainsKey)
                .Select(id => devices[id])
                .OrderBy(d => d.Address.Raw)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ToList();

            if (direct.Count == 0)
                sb.Append(Csv.Row(';', path, KindLabel(node.Kind, lang), floor, "", "", "", count.ToString(CultureInfo.InvariantCulture)))
                    .Append("\r\n");
            else
                foreach (var device in direct)
                    sb.Append(Csv.Row(';', path, KindLabel(node.Kind, lang), floor, device.Name,
                        device.Address.ToString(), device.ProductRefId, count.ToString(CultureInfo.InvariantCulture))).Append("\r\n");

            foreach (var child in node.Children) AppendNode(child, installation);
        }

        static string FloorOf(SpaceNode node)
        {
            for (var current = node; current is not null; current = current.Parent)
                if (current.Kind == SpaceKind.Floor)
                    return current.Name;
            return "";
        }
    }

    /// <summary>Link coverage per main range, from the same model as the report's link matrix.</summary>
    private static string LinksSummaryCsv(Project project, string lang)
    {
        var sb = new StringBuilder();
        sb.Append(Csv.Row(';',
            L("ReportColRange", lang), L("ReportFigureGroupAddresses", lang), L("ReportColWithDpt", lang),
            L("ReportColLinked", lang), L("ReportColUnlinked", lang), L("ReportColLinks", lang))).Append("\r\n");
        var rows = ProjectReport.LinkMatrix(project);
        foreach (var row in rows)
            sb.Append(Csv.Row(';', row.Range,
                row.GroupAddresses.ToString(CultureInfo.InvariantCulture),
                row.WithDatapointType.ToString(CultureInfo.InvariantCulture),
                row.Linked.ToString(CultureInfo.InvariantCulture),
                row.Unlinked.ToString(CultureInfo.InvariantCulture),
                row.Links.ToString(CultureInfo.InvariantCulture))).Append("\r\n");
        if (rows.Count > 0)
            sb.Append(Csv.Row(';', L("ReportTotal", lang),
                rows.Sum(r => r.GroupAddresses).ToString(CultureInfo.InvariantCulture),
                rows.Sum(r => r.WithDatapointType).ToString(CultureInfo.InvariantCulture),
                rows.Sum(r => r.Linked).ToString(CultureInfo.InvariantCulture),
                rows.Sum(r => r.Unlinked).ToString(CultureInfo.InvariantCulture),
                rows.Sum(r => r.Links).ToString(CultureInfo.InvariantCulture))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Per-device configuration signature and recorded programming state. Without a state file
    /// every device reports "not recorded"; a Demo origin stays labelled as simulation.
    /// </summary>
    private static string ParameterEvidenceCsv(Project project, HandoverPackageOptions options, string lang)
    {
        var states = options.ProgrammingStates ?? new Dictionary<string, DeviceProgrammingState>();
        var sb = new StringBuilder();
        sb.Append(Csv.Row(';',
            T("HpRoomBookAddress", lang), T("HpRoomBookDevice", lang), T("HpRevisionSignature", lang),
            T("HpEvidenceNeeds", lang), T("HpEvidenceMissing", lang), T("HpEvidenceDrift", lang),
            T("HpEvidenceOrigin", lang), T("HpEvidenceLastSuccess", lang), T("HpEvidenceLastError", lang)))
            .Append("\r\n");
        foreach (var status in DeviceProgrammingReport.Check(project, states))
            sb.Append(Csv.Row(';',
                status.Address,
                status.Name,
                SignatureOrEmpty(project, status.DeviceId),
                status.NeedsProgramming ? L("CsvYes", lang) : L("CsvNo", lang),
                status.MissingFlags.ToString(),
                status.SignatureDrift ? L("CsvYes", lang) : L("CsvNo", lang),
                OriginText(status.Origin, lang),
                status.LastSuccessAt is { } success ? Stamp(success, "dd.MM.yyyy HH:mm") : "",
                status.LastError ?? "")).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Durable revision log. The source path of a revision is deliberately not part of the
    /// package: it can contain customer or site names and is not needed for traceability.
    /// </summary>
    private static string RevisionLogCsv(Project project, string lang)
    {
        var sb = new StringBuilder();
        sb.Append(Csv.Row(';', "Revision", T("HpRevisionTime", lang), "SHA-256",
            T("HpRevisionSignature", lang), L("ReportColDescription", lang))).Append("\r\n");
        foreach (var revision in ProjectRevisions.All(project)
                     .OrderBy(r => r.Number)
                     .ThenBy(r => r.Id, StringComparer.Ordinal))
            sb.Append(Csv.Row(';',
                revision.Number.ToString(CultureInfo.InvariantCulture),
                Stamp(revision.Timestamp, "yyyy-MM-dd HH:mm:ss zzz"),
                revision.ContentHash,
                SignatureStatusText(revision.SignatureStatus, lang),
                revision.Description ?? "")).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Configuration signature; blank for duplicate-invalid projects the check already reports.</summary>
    private static string SignatureOrEmpty(Project project, string deviceId)
    {
        try
        {
            return WorkbenchProject.ConfigurationSignature(project, deviceId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return "";
        }
    }

    private static string OriginText(ProgrammingOrigin? origin, string lang) => origin switch
    {
        ProgrammingOrigin.Demo => T("HpOriginDemo", lang),
        ProgrammingOrigin.Real => T("HpOriginReal", lang),
        _ => T("HpOriginNone", lang),
    };

    private static readonly Dictionary<SpaceKind, (string De, string En)> KindText = new()
    {
        [SpaceKind.Building] = ("Gebäude", "Building"),
        [SpaceKind.BuildingPart] = ("Gebäudeteil", "Building part"),
        [SpaceKind.Floor] = ("Geschoss", "Floor"),
        [SpaceKind.Room] = ("Raum", "Room"),
        [SpaceKind.DistributionBoard] = ("Verteiler", "Distribution board"),
        [SpaceKind.Stairway] = ("Treppenhaus", "Stairway"),
        [SpaceKind.Corridor] = ("Flur", "Corridor"),
        [SpaceKind.Area] = ("Bereich", "Area"),
        [SpaceKind.Ground] = ("Gelände", "Ground"),
        [SpaceKind.Segment] = ("Segment", "Segment"),
        [SpaceKind.Function] = ("Funktion", "Function"),
    };

    private static string KindLabel(SpaceKind kind, string lang)
        => KindText.TryGetValue(kind, out var text) ? lang == "en" ? text.En : text.De : kind.ToString();
}
