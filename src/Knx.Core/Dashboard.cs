namespace Knx;

public sealed record NamedCount(string Name, int Count);

/// <summary>Aggregated project figures for the dashboard.</summary>
public sealed class DashboardStats
{
    public required string ProjectName { get; init; }
    public int Installations { get; init; }
    public int Areas { get; init; }
    public int Lines { get; init; }
    public int Devices { get; init; }
    public int ComObjects { get; init; }
    public int LinkedComObjects { get; init; }
    public int GroupAddresses { get; init; }
    public int GroupAddressesWithDpt { get; init; }
    public int LinkedGroupAddresses { get; init; }
    public int DptTypes { get; init; }
    public List<NamedCount> DevicesPerLine { get; } = [];
    public List<NamedCount> TopDatapointTypes { get; } = [];
    public List<NamedCount> GroupAddressesPerMainRange { get; } = [];

    public double LinkCoverage => GroupAddresses == 0 ? 0 : 100.0 * LinkedGroupAddresses / GroupAddresses;
    public double DptCoverage => GroupAddresses == 0 ? 0 : 100.0 * GroupAddressesWithDpt / GroupAddresses;
}

public static class Dashboard
{
    public static DashboardStats Build(Project project)
    {
        var addresses = ProjectQuery.GroupAddresses(project).ToList();
        var refs = ProjectQuery.ComObjectRefs(project).ToList();
        var linked = refs.SelectMany(r => r.Ref.Links).ToHashSet();

        var stats = new DashboardStats
        {
            ProjectName = project.Name,
            Installations = project.Installations.Count,
            Areas = project.Installations.Sum(i => i.Areas.Count),
            Lines = project.Installations.Sum(i => i.Areas.Sum(a => a.Lines.Count)),
            Devices = ProjectQuery.DeviceList(project).Count(),
            ComObjects = refs.Count,
            LinkedComObjects = refs.Count(r => r.Ref.Links.Count > 0),
            GroupAddresses = addresses.Count,
            GroupAddressesWithDpt = addresses.Count(a => !string.IsNullOrWhiteSpace(a.DatapointType)),
            LinkedGroupAddresses = addresses.Count(a => linked.Contains(a.Id)),
            DptTypes = addresses.Where(a => !string.IsNullOrWhiteSpace(a.DatapointType))
                .Select(a => a.DatapointType!).Distinct().Count(),
        };

        foreach (var inst in project.Installations)
            foreach (var area in inst.Areas)
            {
                foreach (var line in area.Lines)
                    stats.DevicesPerLine.Add(new NamedCount(
                        $"{area.Address}.{line.Address} {line.Name}".Trim(),
                        line.Devices.Count + line.Segments.Sum(s => s.Devices.Count)));
                if (area.Devices.Count > 0)
                    stats.DevicesPerLine.Add(new NamedCount($"Bereich {area.Address} (direkt)", area.Devices.Count));
            }

        foreach (var group in addresses.Where(a => !string.IsNullOrWhiteSpace(a.DatapointType))
                     .GroupBy(a => a.DatapointType!)
                     .OrderByDescending(g => g.Count())
                     .Take(8))
            stats.TopDatapointTypes.Add(new NamedCount(group.Key, group.Count()));

        foreach (var inst in project.Installations)
            foreach (var range in inst.MainRanges)
                stats.GroupAddressesPerMainRange.Add(new NamedCount(
                    $"{range.RangeStart} {range.Name}".Trim(),
                    ProjectQuery.AllAddresses(range).Count()));

        return stats;
    }
}
