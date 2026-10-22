using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Knx.App.Services;
using Knx.ProjectCompare;

namespace Knx.App;

public partial class CompareWindow : Window
{
    private DifferenceReport? _report;

    public CompareWindow()
    {
        InitializeComponent();
        Opened += (_, _) => DiffGrid.Focus();
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
            .Select(item => new DiffRow(item.Path, ComparisonCsv.ObjectTypeText(item.ObjectType), item.ObjectName,
                ComparisonCsv.ModificationText(item.Modification), item.NewValue))
            .ToList();
    }

    private sealed record DiffRow(string Path, string ObjectType, string ObjectName,
        string Modification, string? NewValue);

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = Strings.Get("CompareCsvFileName") + ".csv",
            DefaultExtension = "csv",
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(ComparisonCsv.Render(_report));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
