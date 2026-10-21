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
            sb.AppendLine($"{ga.Address};{Escape(ga.Name)};{Escape(ga.Description)};{ga.DatapointType};{ga.Puid}");
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
        => sb.AppendLine($"{d.Address};{Escape(d.Name)};{Escape(d.ProductRefId)};{Escape(d.Description)};{Escape(d.Comment)}");

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
                            sb.AppendLine($"{area.Address};{line.Address};;{Escape(d.Name)};{d.Address}");
                    }
                    foreach (var seg in line.Segments)
                        foreach (var d in seg.Devices)
                            sb.AppendLine($"{area.Address};{line.Address};{seg.Address};{Escape(d.Name)};{d.Address}");
                }
        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        if (value is null) return "";
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
