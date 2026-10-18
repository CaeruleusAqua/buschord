using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Bus;

namespace Knx.App.Views;

/// <summary>
/// ETS-like group monitor pane: pause/resume, clear, CSV export, filters and a decoded
/// telegram table. The pane owns no bus connection; a host feeds it through <see cref="Append"/>.
/// Filters, pause state and the captured buffer belong to the <see cref="GroupMonitor"/> model, so a
/// shared model means a filter set on it affects every pane showing that model.
/// </summary>
public partial class GroupMonitorPane : UserControl
{
    private static readonly MonitorKind[] Kinds =
        [MonitorKind.All, MonitorKind.Write, MonitorKind.Read, MonitorKind.Response];

    private readonly MainWindowViewModel? _vm;
    private readonly MonitorRefreshQueue _queue;
    private IReadOnlyList<MonitorEntry>? _shown;
    private Window? _host;
    private bool _detached;

    public GroupMonitorPane() : this(null, null) { }

    /// <param name="vm">Project and status host; may be null while docked without a project.</param>
    /// <param name="model">Optional shared monitor; filters and pause state are shared with other hosts.</param>
    public GroupMonitorPane(MainWindowViewModel? vm, GroupMonitor? model = null)
    {
        _vm = vm;
        Model = model ?? new GroupMonitor();
        InitializeComponent();
        _queue = new MonitorRefreshQueue(
            callback =>
            {
                if (Dispatcher.UIThread.CheckAccess()) callback();
                else Dispatcher.UIThread.Post(callback);
            },
            Refresh);
        KindBox.ItemsSource = new[]
        {
            Strings.Get("MonitorKindAll"), Strings.Get("MonitorKindWrite"),
            Strings.Get("MonitorKindRead"), Strings.Get("MonitorKindResponse"),
        };
        KindBox.SelectedIndex = 0;
        Loaded += OnLoaded;
        Model.Project = vm?.Project;
        Model.Changed += OnModelChanged;
        Refresh();
    }

    /// <summary>The captured telegram model; a host may append directly instead of using <see cref="Append"/>.</summary>
    public GroupMonitor Model { get; }

    /// <summary>Feeds one telegram; safe to call from a bus thread (refresh is coalesced).</summary>
    public void Append(BusTelegram telegram) => Model.Append(telegram);

    /// <summary>Re-reads the project after it was loaded, closed, undone or redone.</summary>
    public void RefreshProject() => Model.Project = _vm?.Project;

    /// <summary>
    /// Stops queued UI updates and drops the row references. The host calls this when the pane closes;
    /// a pane attached to a window also detaches itself when that window closes.
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        Model.Changed -= OnModelChanged;
        _queue.Detach();
        _shown = null;
        EntryList.ItemsSource = null;
        if (_host is not null)
        {
            _host.Closed -= OnHostClosed;
            _host = null;
        }
    }

    private void OnModelChanged(object? sender, EventArgs e) => _queue.Request();

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window || ReferenceEquals(_host, window)) return;
        if (_host is not null) _host.Closed -= OnHostClosed;
        _host = window;
        _host.Closed += OnHostClosed;
    }

    private void OnHostClosed(object? sender, EventArgs e) => Detach();

    private void Refresh()
    {
        if (_detached || EntryList is null) return;
        var entries = Model.Visible;
        if (_shown is null || _shown.Count != entries.Count || !_shown.SequenceEqual(entries))
        {
            EntryList.ItemsSource = entries;
            _shown = entries;
        }
        var total = Model.TotalCount;
        CountText.Text = entries.Count == total
            ? Strings.Get("LblTelegrams", entries.Count)
            : $"{Strings.Get("LblTelegrams", entries.Count)} / {total}";
        EmptyState.IsVisible = entries.Count == 0;
        EmptyText.Text = total == 0 ? Strings.Get("UxEmptyFindings") : Strings.Get("CatalogNoResults");
        PauseText.Text = Strings.Get(Model.IsPaused ? "BtnStart" : "BtnStop");
        PauseIcon.Symbol = Model.IsPaused ? "FiberManualRecord" : "Stop";
    }

    private void SetMessage(string text)
    {
        MessageText.Text = text;
        MessageText.IsVisible = text.Length > 0;
    }

    private void OnPause(object? sender, RoutedEventArgs e) => Model.TogglePause();

    private void OnClear(object? sender, RoutedEventArgs e) => Model.Clear();

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
        => Model.TextFilter = FilterBox.Text ?? "";

    private void OnKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = Math.Clamp(KindBox.SelectedIndex, 0, Kinds.Length - 1);
        Model.KindFilter = Kinds[index];
    }

    private void OnGroupOnlyChanged(object? sender, RoutedEventArgs e)
        => Model.GroupOnly = GroupOnlyBox.IsChecked == true;

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Get("BtnSaveCsvDots"),
                SuggestedFileName = "busmonitor.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            Model.SaveCsv(path);
            SetMessage("");
            if (_vm is { } vm) vm.Status = Strings.Get("LblTelegrams", Model.VisibleCount);
        }
        catch (Exception ex)
        {
            // Also catches picker failures: an async void handler must never reach the dispatcher.
            SetMessage(ex.Message);
            if (_vm is { } vm) vm.Status = ex.Message;
        }
    }
}
