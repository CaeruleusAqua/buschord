using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Knx;

namespace Knx.App;

/// <summary>Row of a generic result list (project check, link suggestions, diagnostics).</summary>
public sealed record ReportRow(string Severity, string Title, string Detail);

/// <summary>
/// Generic result window: used for project check findings, smart-linking suggestions and
/// diagnostics. One window type instead of one per feature keeps the UI code small.
/// </summary>
public partial class ReportWindow : Window
{
    private readonly List<ReportRow> _rows = [];
    private Func<Task>? _primary;
    private string _csvName = "befunde";

    public ReportWindow()
    {
        InitializeComponent();
    }

    public void Load(string title, string subtitle, IEnumerable<ReportRow> rows,
        string? primaryText = null, Func<Task>? primary = null, string csvName = "befunde")
    {
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        _rows.Clear();
        _rows.AddRange(rows);
        _csvName = csvName;
        Rows.ItemsSource = _rows;
        PrimaryButton.Content = primaryText ?? Strings.Get("BtnApply");
        PrimaryButton.IsVisible = primary is not null;
        _primary = primary;
    }

    private async void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (_primary is null) return;
        PrimaryButton.IsEnabled = false;
        try { await _primary(); }
        finally { PrimaryButton.IsEnabled = true; Close(); }
    }

    private async void OnSaveCsv(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = _csvName + ".csv",
                DefaultExtension = "csv",
            });
            if (file is null) return;

            var sb = new StringBuilder("Stufe;Objekt;Beschreibung\n");
            foreach (var row in _rows)
                sb.AppendLine($"{row.Severity};{Escape(row.Title)};{Escape(row.Detail)}");

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteAsync(sb.ToString());
        }
        catch (Exception ex)
        {
            await ConfirmDialog.ShowMessageAsync(this, Strings.Get("CsvSaveFailed"), ex.Message);
        }
    }

    private static string Escape(string value)
        => value.Contains(';') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
