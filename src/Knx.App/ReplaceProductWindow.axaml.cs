using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Services;

namespace Knx.App;

/// <summary>Produkt austauschen: preview first, then apply.</summary>
public partial class ReplaceProductWindow : Window
{
    private readonly Func<string, string, string?, bool, IReadOnlyList<ProductReplaceResult>> _replace;
    private IReadOnlyList<ProductReplaceResult> _preview = [];
    private readonly Func<ProductMigrationInput, IReadOnlyList<ProductMigrationPlan>>? _migrationPreview;
    private readonly Func<IReadOnlyList<ProductMigrationPlan>, IReadOnlyList<ProductReplaceResult>>? _migrationApply;
    private IReadOnlyList<ProductMigrationPlan>? _plans;

    public ReplaceProductWindow() : this((_, _, _, _) => []) { }

    public ReplaceProductWindow(Func<string, string, string?, bool, IReadOnlyList<ProductReplaceResult>> replace)
    {
        _replace = replace;
        InitializeComponent();
        WorkbenchDialogChrome.Apply(this);
        Opened += (_, _) => OldBox.Focus();
        foreach (var box in new[] { OldBox, NewBox, ProgramBox, ParameterMapBox, ObjectMapBox, ChannelMapBox })
            box.TextChanged += (_, _) => { _plans = null; _preview = []; ApplyButton.IsEnabled = false; ResultList.ItemsSource = null; MigrationDetails.ItemsSource = null; };
    }

    public ReplaceProductWindow(Func<ProductMigrationInput, IReadOnlyList<ProductMigrationPlan>> preview,
        Func<IReadOnlyList<ProductMigrationPlan>, IReadOnlyList<ProductReplaceResult>> apply) : this((_, _, _, _) => [])
    { _migrationPreview = preview; _migrationApply = apply; }

    public string? AppliedMessage { get; private set; }

    /// <summary>Pre-fills the source reference from the selected device.</summary>
    public void SetOldReference(string productRefId) => OldBox.Text = productRefId;

    private void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var oldRef, out var newRef, out var program)) return;
        ApplyButton.IsEnabled = false; _plans = null;
        try
        {
            if (_migrationPreview is not null)
            {
                _plans = _migrationPreview(new(oldRef, newRef, program, ReadMappings(ParameterMapBox.Text),
                    ReadMappings(ObjectMapBox.Text), ReadMappings(ChannelMapBox.Text)));
                ResultList.ItemsSource = _plans.Select(p => new MigrationRow(new(p.DeviceAddress), p.NewProductRefId, p.Objects.Sum(o => o.KeptLinks))).ToArray();
                MigrationDetails.ItemsSource = _plans.SelectMany(p => p.Parameters.Select(v => $"{p.DeviceAddress} · {v.SourceId} → {v.TargetId}: {v.Value}")
                    .Concat(p.Objects.Select(o => $"{p.DeviceAddress} · {o.SourceId} → {o.TargetId}: #{o.OldNumber} → #{o.NewNumber} · {o.KeptLinks}"))).ToArray();
                SummaryText.Text = _plans.Count == 0 ? Strings.Get("ReplaceNoDevices")
                    : Strings.Get("ReplacePreviewSummary", _plans.Count, _plans.Sum(p => p.Objects.Sum(o => o.KeptLinks)));
                ApplyButton.IsEnabled = _plans.Count > 0;
            }
            else
            {
                _preview = _replace(oldRef, newRef, program, true);
                ResultList.ItemsSource = _preview;
                SummaryText.Text = _preview.Count == 0 ? Strings.Get("ReplaceNoDevices")
                    : Strings.Get("ReplacePreviewSummary", _preview.Count, _preview.Sum(r => r.KeptLinks));
                ApplyButton.IsEnabled = _preview.Count > 0;
            }
        }
        catch (Exception ex) { SummaryText.Text = ex.Message; }
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var oldRef, out var newRef, out var program)) return;
        try
        {
            var results = _migrationApply is not null
                ? _migrationApply(_plans ?? throw new InvalidOperationException(Localization.WorkbenchStrings.Get("MigrationReviewRequired")))
                : _replace(oldRef, newRef, program, false);
            AppliedMessage = Strings.Get("ReplaceApplied", results.Count);
            Close(true);
        }
        catch (Exception ex) { SummaryText.Text = ex.Message; ApplyButton.IsEnabled = false; }
    }

    public sealed record MigrationDevice(string Address);
    public sealed record MigrationRow(MigrationDevice Device, string NewRefId, int KeptLinks);

    private static IReadOnlyDictionary<string, string> ReadMappings(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var pair = line.Split('=');
            if (pair.Length != 2 || pair.Any(p => string.IsNullOrWhiteSpace(p)) || !result.TryAdd(pair[0].Trim(), pair[1].Trim()))
                throw new InvalidDataException(Localization.WorkbenchStrings.Get("MigrationMapHint"));
        }
        return result;
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
