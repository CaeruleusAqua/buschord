using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Knx.ProjectCompare;

namespace Knx.App;

public partial class CompareWindow : Window
{
    private DifferenceReport? _report;

    public CompareWindow()
    {
        InitializeComponent();
    }

    public void Load(DifferenceReport report)
    {
        _report = report;
        SummaryText.Text = Strings.Get("CompareSummary",
            report.AddedCount, report.RemovedCount, report.ChangedCount);
        ProjectsText.Text = $"{report.LeftProjectName}  ↔  {report.RightProjectName}   ({report.GeneratedAt:dd.MM.yyyy HH:mm})";
        // ListBox-basierte Diff-Anzeige (kein DataGrid-Paket nötig); die Enum-Werte
        // werden für Anzeige und CSV in die aktive Sprache übersetzt.
        DiffGrid.ItemsSource = report.Items
            .Select(item => new DiffRow(item.Path, ObjectTypeText(item.ObjectType), item.ObjectName,
                ModificationText(item.Modification), item.NewValue))
            .ToList();
    }

    private sealed record DiffRow(string Path, string ObjectType, string ObjectName,
        string Modification, string? NewValue);

    private static string ObjectTypeText(string objectType) => objectType switch
    {
        "Project" => Strings.Get("CompareTypeProject"),
        "Installation" => Strings.Get("CompareTypeInstallation"),
        "GroupAddress" => Strings.Get("CompareTypeGroupAddress"),
        "Device" => Strings.Get("CompareTypeDevice"),
        _ => objectType,
    };

    private static string ModificationText(ModificationType modification) => modification switch
    {
        ModificationType.Added => Strings.Get("ModificationAdded"),
        ModificationType.Removed => Strings.Get("ModificationRemoved"),
        ModificationType.Changed => Strings.Get("ModificationChanged"),
        _ => Strings.Get("ModificationUnchanged"),
    };

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = Strings.Get("CompareCsvFileName") + ".csv",
            DefaultExtension = "csv",
        });
        if (file is null) return;

        var sb = new StringBuilder();
        sb.AppendLine(Strings.Get("CompareCsvHeader"));
        foreach (var item in _report.Items)
            sb.AppendLine($"{item.Path};{ObjectTypeText(item.ObjectType)};{Esc(item.ObjectName)};{ModificationText(item.Modification)};{Esc(item.OldValue)};{Esc(item.NewValue)}");

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(sb.ToString());
    }

    private static string Esc(string? v)
        => v is null ? "" : v.Contains(';') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
