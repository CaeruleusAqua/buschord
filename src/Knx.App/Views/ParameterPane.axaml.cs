using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Services;

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

/// <summary>One field column: the reference's 540 px cap, narrowed on small windows instead of clipping.</summary>
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

    private void Subscribe() { if (_subscribed) return; Session.Changed += OnChanged; _subscribed = true; }
    private void Unsubscribe() { if (!_subscribed) return; Session.Changed -= OnChanged; _subscribed = false; }
    private void OnChanged(object? sender, EventArgs e) => Render();
    public void Dispose() { _disposed = true; Unsubscribe(); ParameterList.ItemsSource = Array.Empty<ParameterItemViewModel>(); }

    private void Render()
    {
        var hasRows = Session.Rows.Length > 0;
        // Do not recreate native editors during a keystroke (local TextBox undo must survive).
        if (!ReferenceEquals(ParameterList.ItemsSource, Session.Rows)) ParameterList.ItemsSource = Session.Rows;
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
        ResetButton.IsVisible = Session.CanApply;
        ResetButton.IsEnabled = Session.CanReset;
        DiscardButton.IsVisible = ReviewButton.IsVisible = ApplyButton.IsVisible = Session.CanApply;
        var hasDraft = Session.HasUnappliedChanges;
        DiscardButton.IsEnabled = ReviewButton.IsEnabled = hasDraft;
        ReviewButton.Content = T("Änderungen prüfen", "Review changes");
        CancelReviewButton.Content = T("Vorschau schließen", "Cancel review");
        PreviewPanel.IsVisible = Session.Preview is not null;
        PreviewList.ItemsSource = Session.Preview?.Changes ?? Array.Empty<ParameterChange>();
        PreviewSummary.Text = Session.Preview is { } preview
            ? T($"Parameteränderungen: {preview.Changes.Count}", $"Parameter changes: {preview.Changes.Count}")
            : "";
        PreviewLimitations.Text = Session.Preview?.Limitations;
        ApplyButton.IsEnabled = Session.CanApply && hasDraft && !Session.HasValidationErrors
            && Session.Preview?.IsValid != false;
    }

    private void OnReview(object? sender, RoutedEventArgs e) => Session.ReviewChanges();
    private void OnCancelReview(object? sender, RoutedEventArgs e) => Session.CancelReview();
    private void OnApply(object? sender, RoutedEventArgs e) => Apply();
    private void OnDiscard(object? sender, RoutedEventArgs e) => DiscardDrafts();
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
