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
        // ListBox-basierte Diff-Anzeige (kein DataGrid-Paket nötig)
        DiffGrid.ItemsSource = report.Items;
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "projektvergleich.csv",
            DefaultExtension = "csv",
        });
        if (file is null) return;

        var sb = new StringBuilder();
        sb.AppendLine("Pfad;Typ;Objekt;Änderung;Alt;Neu");
        foreach (var item in _report.Items)
            sb.AppendLine($"{item.Path};{item.ObjectType};{Esc(item.ObjectName)};{item.Modification};{Esc(item.OldValue)};{Esc(item.NewValue)}");

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(sb.ToString());
    }

    private static string Esc(string? v)
        => v is null ? "" : v.Contains(';') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
