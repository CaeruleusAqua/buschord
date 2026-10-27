using System.Text;
using Knx.ProjectCompare;

namespace Knx.App.Services;

/// <summary>CSV export of a project comparison; labels follow the active UI language.</summary>
public static class ComparisonCsv
{
    public static string Render(DifferenceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.Get("CompareCsvHeader"));
        foreach (var item in report.Items)
            sb.AppendLine(Csv.Row(';', item.Path, ObjectTypeText(item.ObjectType), item.ObjectName,
                ModificationText(item.Modification), item.OldValue, item.NewValue));
        return sb.ToString();
    }

    public static string ObjectTypeText(string objectType) => objectType switch
    {
        "Project" => Strings.Get("CompareTypeProject"),
        "Installation" => Strings.Get("CompareTypeInstallation"),
        "GroupAddress" => Strings.Get("CompareTypeGroupAddress"),
        "Device" => Strings.Get("CompareTypeDevice"),
        _ => objectType,
    };

    public static string ModificationText(ModificationType modification) => modification switch
    {
        ModificationType.Added => Strings.Get("ModificationAdded"),
        ModificationType.Removed => Strings.Get("ModificationRemoved"),
        ModificationType.Changed => Strings.Get("ModificationChanged"),
        _ => Strings.Get("ModificationUnchanged"),
    };
}
