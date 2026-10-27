using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Services;
using Knx.App.Localization;

namespace Knx.App.Views;

/// <summary>Per-field validation text; the footer uses the same core validation as the session.</summary>
public sealed class ParameterFieldErrorConverter : IMultiValueConverter
{
    public static ParameterFieldErrorConverter Instance { get; } = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not ParameterItemViewModel row || !row.IsVisible || !row.IsEnabled)
            return null;
        return ParameterValidation.TryValidate(row.Instance, row.Value, out var error) ? null : error;
    }
}

/// <summary>One field column, capped at 540 px and narrowed on small windows instead of clipping.</summary>
public sealed class ParameterFieldWidthConverter : IValueConverter
{
    public static ParameterFieldWidthConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width && double.IsFinite(width) ? Math.Clamp(width - 4, 280, 540) : 540;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>A disposable view over a host-owned parameter editing session.</summary>
public sealed partial class ParameterPane : UserControl, IDisposable
{
    public ParameterEditingSession Session { get; }
    private bool _subscribed;
    private bool _disposed;
    private readonly ObservableCollection<ParameterItemViewModel> _displayRows = [];

    public ParameterPane() : this(new ParameterEditingSession()) { }
    public ParameterPane(MainWindowViewModel vm) : this(new ParameterEditingSession(vm)) { }
    public ParameterPane(ParameterEditingSession session)
    {
        Session = session;
        InitializeComponent();
        Session.Refresh();
        Subscribe();
        Render();
        AttachedToVisualTree += (_, _) => { if (!_disposed) { Subscribe(); Render(); } };
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    public DeviceInstance? Device => Session.Device;
    public bool HasUnappliedChanges => Session.HasUnappliedChanges;
    public bool IsReadOnly => Session.IsReadOnly;
    public string? ReadOnlyReason => Session.ReadOnlyReason;
    public event EventHandler? CloseRequested;
    public void Load(DeviceRow? device) => Session.Load(device);
    public void Refresh() => Session.Refresh();
    public bool Apply() => Session.Apply();
    public void DiscardDrafts() => Session.DiscardDrafts();
    internal void LoadResolved(string? title, IEnumerable<ParameterInstance> parameters,
        IEnumerable<ParameterType> types, Func<bool>? apply, string? warning = null, string? readOnlyReason = null)
        => Session.LoadResolved(title, parameters, types, apply, warning, readOnlyReason);
    internal void ShowClose() => CloseButton.IsVisible = true;

    private static string T(string de, string en) => Strings.Language == "en" ? en : de;

    private void Subscribe()
    {
        if (_subscribed) return;
        Session.Changed += OnChanged; Strings.Instance.PropertyChanged += OnLanguageChanged; _subscribed = true;
        foreach (var row in Session.Rows) row.RefreshStorage();
    }
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        Session.Changed -= OnChanged; Strings.Instance.PropertyChanged -= OnLanguageChanged; _subscribed = false;
    }
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == "Item[]") foreach (var row in Session.Rows) row.RefreshStorage(); }
    private void OnChanged(object? sender, EventArgs e) => Render();
    public void Dispose() { _disposed = true; Unsubscribe(); ParameterList.ItemsSource = Array.Empty<ParameterItemViewModel>(); }

    private void Render()
    {
        var hasRows = Session.Rows.Length > 0;
        if (TopLevel.GetTopLevel(ParameterList) is null)
        {
            if (!ReferenceEquals(ParameterList.ItemsSource, Session.Rows)) ParameterList.ItemsSource = Session.Rows;
        }
        else
        {
            // Collection edits retain surviving native editors, keyboard focus, scroll and local text undo.
            var retained = Session.Rows.ToHashSet();
            for (var index = _displayRows.Count - 1; index >= 0; index--)
                if (!retained.Contains(_displayRows[index])) _displayRows.RemoveAt(index);
            for (var index = 0; index < Session.Rows.Length; index++)
            {
                var row = Session.Rows[index];
                if (index < _displayRows.Count && ReferenceEquals(_displayRows[index], row)) continue;
                var previous = _displayRows.IndexOf(row);
                if (previous < 0) _displayRows.Insert(index, row);
                else _displayRows.Move(previous, index);
            }
            if (!ReferenceEquals(ParameterList.ItemsSource, _displayRows)) ParameterList.ItemsSource = _displayRows;
        }
        ParameterList.IsVisible = hasRows;
        HeaderText.Text = string.IsNullOrWhiteSpace(Session.Title) ? Strings.Get("ParameterTitle") : Session.Title;
        NoticePanel.IsVisible = hasRows;
        WarningText.IsVisible = WarningDetail.IsVisible = hasRows && !string.IsNullOrWhiteSpace(Session.Warning);
        WarningDetail.Text = Session.Warning;
        EmptyText.Text = Session.EmptyText ?? ParameterEmptyState.NoParametersText;
        EmptyText.IsVisible = Session.EmptyText is not null;
        MessageText.Text = Session.Message;
        Footer.IsVisible = hasRows;

        // Section navigation and read-only/opaque banner; drafts stay untouched.
        var sections = Session.HasSectionNavigation ? Session.Sections : null;
        if (TopLevel.GetTopLevel(SectionList) is not null && !ReferenceEquals(SectionList.ItemsSource, sections))
            SectionList.ItemsSource = sections;
        SectionList.IsVisible = hasRows && sections is not null;
        ParameterList.IsEnabled = !Session.IsReadOnly;
        ReadOnlyPanel.IsVisible = hasRows && Session.IsReadOnly;
        ReadOnlyHeading.Text = T("Schreibgeschützt", "Read-only");
        ReadOnlyText.Text = Session.ReadOnlyReason ?? "";
        DraftNoticePanel.IsVisible = Session.DraftNotice is { Length: > 0 };
        DraftNoticeText.Text = Session.DraftNotice ?? "";
        DismissDraftNoticeButton.Content = T("Hinweis schließen", "Dismiss notice");

        // Draft indicator and footer-wide validation summary share the left footer slot.
        DraftPill.IsVisible = hasRows && Session.HasUnappliedChanges;
        DraftText.Text = Session.DraftText;
        ValidationSummary.IsVisible = hasRows && Session.HasValidationErrors;
        ValidationSummary.Text = Session.ValidationSummary;
        StatusHint.IsVisible = hasRows && !Session.HasUnappliedChanges && !Session.HasValidationErrors;
        StatusHint.Text = T("Keine nicht übernommenen Parameteränderungen", "No unapplied parameter changes");

        ResetButton.Content = T("Standardwerte", "Reset defaults");
        DiscardButton.Content = T("Verwerfen", "Discard");
        var editable = hasRows && !Session.IsReadOnly && !Session.IsStale;
        ResetButton.IsVisible = editable;
        ResetButton.IsEnabled = Session.CanReset;
        DiscardButton.IsVisible = ReviewButton.IsVisible = ApplyButton.IsVisible = editable;
        var hasDraft = Session.HasUnappliedChanges;
        DiscardButton.IsEnabled = ReviewButton.IsEnabled = hasDraft;
        ReviewButton.Content = T("Änderungen prüfen", "Review changes");
        CancelReviewButton.Content = T("Vorschau schließen", "Cancel review");
        PreviewPanel.IsVisible = Session.Preview is not null;
        PreviewList.ItemsSource = Session.Preview?.Changes ?? Array.Empty<ParameterChange>();
        PreviewSummary.Text = Session.Preview is { } preview
            ? T($"Parameteränderungen: {preview.Changes.Count}", $"Parameter changes: {preview.Changes.Count}")
                + (preview.AddedObjects + preview.RemovedObjects > 0
                    ? " · " + WorkbenchStrings.Get("ParameterStructureObjects", preview.AddedObjects, preview.RemovedObjects) : "")
                + (preview.ActivatedObjects + preview.DeactivatedObjects > 0
                    ? " · " + WorkbenchStrings.Get("ParameterActivityObjects", preview.ActivatedObjects, preview.DeactivatedObjects) : "")
            : "";
        PreviewLimitations.Text = Session.Preview?.Limitations;
        ApplyButton.IsEnabled = Session.CanApply && hasDraft && !Session.HasValidationErrors
            && Session.Preview?.IsValid != false;
    }

    private void OnReview(object? sender, RoutedEventArgs e) => Session.ReviewChanges();
    private void OnCancelReview(object? sender, RoutedEventArgs e) => Session.CancelReview();
    private void OnApply(object? sender, RoutedEventArgs e) => Apply();
    private void OnDiscard(object? sender, RoutedEventArgs e) => Session.DiscardCurrentDraft();
    private void OnReset(object? sender, RoutedEventArgs e) => Session.ResetToDefaults();
    private void OnDismissDraftNotice(object? sender, RoutedEventArgs e) => Session.AcknowledgeNotice();
    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Jumps to a section without filtering rows, so drafts and validation stay intact.</summary>
    private void OnSection(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string title }) return;
        Session.SelectSection(title);
        if (Session.Sections.FirstOrDefault(s => s.Title == title) is not { } section) return;
        // ScrollIntoView works with the virtualized list, where far rows have no container yet.
        ParameterList.ScrollIntoView(section.FirstRow);
    }
}
