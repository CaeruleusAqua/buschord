using Knx.Reports;

namespace Knx.App.Services;

/// <summary>Workbench's Word-compatible UTF-8/BOM tab data source, with shared spreadsheet-cell protection.</summary>
public static class WorkbenchLabelMerge
{
    public static string Render(Project project, bool devices)
    {
        ArgumentNullException.ThrowIfNull(project);
        var labels = devices ? LabelMergeSource.ForDevices(project) : LabelMergeSource.ForGroupAddresses(project);
        if (devices)
        {
            var rooms = ProjectQuery.DeviceList(project).GroupBy(d => d.Address.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.Count() != 1 ? "" : string.Join(" / ", project.Installations.SelectMany(i => i.Locations.AllNodes)
                    .Where(room => room.DeviceRefs.Contains(g.Single().Id)).Select(room => room.Path).Distinct(StringComparer.Ordinal)), StringComparer.Ordinal);
            labels = labels.Select(label => label with { Room = rooms.GetValueOrDefault(label.Id, "") }).ToArray();
        }
        string Safe(string value) => Csv.Field(value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '), '\t');
        return "\uFEFF" + LabelMergeSource.ToDataSource(labels.Select(label => new MergeLabel(Safe(label.Id), Safe(label.Description),
            Safe(label.IndividualAddress), Safe(label.Manufacturer), Safe(label.Product), Safe(label.Program), Safe(label.Room), Safe(label.Trade), Safe(label.ImageName))));
    }
}
