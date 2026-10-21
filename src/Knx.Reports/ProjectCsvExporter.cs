namespace Knx.Reports;

/// <summary>Exportiert Projektdaten als CSV (Gruppenadressen, Geräte, Topologie).</summary>
public static class ProjectCsvExporter
{
    public static string GroupAddresses(Project project)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Strings.Get("CsvGroupAddressHeader"));
        foreach (var inst in project.Installations)
            foreach (var range in inst.MainRanges)
                CollectGroupAddresses(range, sb);
        return sb.ToString();
    }

    private static void CollectGroupAddresses(GroupRange range, System.Text.StringBuilder sb)
    {
        foreach (var ga in range.Addresses)
            sb.AppendLine(Csv.Row(';', ga.Address.ToString(), ga.Name, ga.Description, ga.DatapointType,
                ga.Puid.ToString()));
        foreach (var child in range.Ranges) CollectGroupAddresses(child, sb);
    }

    public static string Devices(Project project)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Strings.Get("CsvDeviceHeader"));
        foreach (var inst in project.Installations)
            foreach (var area in inst.Areas)
            {
                foreach (var d in area.Devices) AppendDevice(sb, d);
                foreach (var line in area.Lines)
                {
                    foreach (var d in line.Devices) AppendDevice(sb, d);
                    foreach (var seg in line.Segments)
                        foreach (var d in seg.Devices) AppendDevice(sb, d);
                }
            }
        return sb.ToString();
    }

    private static void AppendDevice(System.Text.StringBuilder sb, DeviceInstance d)
        => sb.AppendLine(Csv.Row(';', d.Address.ToString(), d.Name, d.ProductRefId, d.Description, d.Comment));

    public static string Topology(Project project)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Strings.Get("CsvTopologyHeader"));
        foreach (var inst in project.Installations)
            foreach (var area in inst.Areas)
                foreach (var line in area.Lines)
                {
                    if (line.Segments.Count == 0)
                    {
                        foreach (var d in line.Devices)
                            sb.AppendLine(Csv.Row(';', area.Address.ToString(), line.Address.ToString(), "",
                                d.Name, d.Address.ToString()));
                    }
                    foreach (var seg in line.Segments)
                        foreach (var d in seg.Devices)
                            sb.AppendLine(Csv.Row(';', area.Address.ToString(), line.Address.ToString(),
                                seg.Address.ToString(), d.Name, d.Address.ToString()));
                }
        return sb.ToString();
    }

    /// <summary>Communication-object links (workbench export).</summary>
    public static string Associations(Project project)
    {
        var sb = new System.Text.StringBuilder(Strings.Get("CsvAssociationsHeader") + "\r\n");
        foreach (var a in Workbench.WorkbenchProject.Associations(project))
            sb.Append(Csv.Row(';', a.DeviceAddress, a.DeviceName, a.Number.ToString(),
                a.ObjectName, a.Dpt ?? "", a.Bits?.ToString() ?? "", a.Flags ?? "",
                a.Active ? Strings.Get("CsvYes") : Strings.Get("CsvNo"),
                a.GroupAddress, a.GroupName, a.Sending ? Strings.Get("CsvYes") : Strings.Get("CsvNo"),
                a.InstalledRoomId ?? "")).Append("\r\n");
        return sb.ToString();
    }
}
