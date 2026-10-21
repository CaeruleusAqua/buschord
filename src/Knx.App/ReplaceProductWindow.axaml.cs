using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;

namespace Knx.App;

/// <summary>Produkt austauschen: preview first, then apply.</summary>
public partial class ReplaceProductWindow : Window
{
    private readonly Func<string, string, string?, bool, IReadOnlyList<ProductReplaceResult>> _replace;
    private IReadOnlyList<ProductReplaceResult> _preview = [];

    public ReplaceProductWindow() : this((_, _, _, _) => []) { }

    public ReplaceProductWindow(Func<string, string, string?, bool, IReadOnlyList<ProductReplaceResult>> replace)
    {
        _replace = replace;
        InitializeComponent();
        Opened += (_, _) => OldBox.Focus();
    }

    public string? AppliedMessage { get; private set; }

    /// <summary>Pre-fills the source reference from the selected device.</summary>
    public void SetOldReference(string productRefId) => OldBox.Text = productRefId;

    private void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var oldRef, out var newRef, out var program)) return;
        _preview = _replace(oldRef, newRef, program, true);
        ResultList.ItemsSource = _preview;
        SummaryText.Text = _preview.Count == 0
            ? Strings.Get("ReplaceNoDevices")
            : Strings.Get("ReplacePreviewSummary", _preview.Count, _preview.Sum(r => r.KeptLinks));
        ApplyButton.IsEnabled = _preview.Count > 0;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var oldRef, out var newRef, out var program)) return;
        var results = _replace(oldRef, newRef, program, false);
        AppliedMessage = Strings.Get("ReplaceApplied", results.Count);
        Close(true);
    }

    private bool TryReadInput(out string oldRef, out string newRef, out string? program)
    {
        oldRef = OldBox.Text?.Trim() ?? "";
        newRef = NewBox.Text?.Trim() ?? "";
        program = string.IsNullOrWhiteSpace(ProgramBox.Text) ? null : ProgramBox.Text!.Trim();
        if (oldRef.Length == 0 || newRef.Length == 0)
        {
            SummaryText.Text = Strings.Get("ReplaceRequiredFields");
            return false;
        }
        return true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
