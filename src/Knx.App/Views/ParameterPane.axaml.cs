using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Services;

namespace Knx.App.Views;

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
    public event EventHandler? CloseRequested;
    public void Load(DeviceRow? device) => Session.Load(device);
    public void Refresh() => Session.Refresh();
    public bool Apply() => Session.Apply();
    public void DiscardDrafts() => Session.DiscardDrafts();
    internal void LoadResolved(string? title, IEnumerable<ParameterInstance> parameters,
        IEnumerable<ParameterType> types, Func<bool>? apply, string? warning = null)
        => Session.LoadResolved(title, parameters, types, apply, warning);
    internal void ShowClose() => CloseButton.IsVisible = true;

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
        NoticeExpander.IsVisible = hasRows;
        WarningText.IsVisible = WarningDetail.IsVisible = hasRows && !string.IsNullOrWhiteSpace(Session.Warning);
        WarningDetail.Text = Session.Warning;
        EmptyText.Text = Session.EmptyText ?? ParameterEmptyState.NoParametersText;
        EmptyText.IsVisible = Session.EmptyText is not null;
        MessageText.Text = Session.Message;
        ApplyButton.IsVisible = ReviewButton.IsVisible = Session.CanApply;
        ReviewButton.Content = Strings.Language == "en" ? "Review changes" : "Änderungen prüfen";
        CancelReviewButton.Content = Strings.Language == "en" ? "Cancel review" : "Vorschau schließen";
        PreviewPanel.IsVisible = Session.Preview is not null;
        PreviewList.ItemsSource = Session.Preview?.Changes ?? Array.Empty<ParameterChange>();
        PreviewSummary.Text = Session.Preview is { } preview
            ? Strings.Language == "en" ? $"Parameter changes: {preview.Changes.Count}" : $"Parameteränderungen: {preview.Changes.Count}"
            : "";
        PreviewLimitations.Text = Session.Preview?.Limitations;
        ApplyButton.IsEnabled = Session.Preview?.IsValid != false;
    }

    private void OnReview(object? sender, RoutedEventArgs e) => Session.ReviewChanges();
    private void OnCancelReview(object? sender, RoutedEventArgs e) => Session.CancelReview();
    private void OnApply(object? sender, RoutedEventArgs e) => Apply();
    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
