using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Bus;

namespace Knx.App.Views;

/// <summary>
/// Raw bus list as a dockable pane: monospace telegram lines, pause, clear and a count line.
/// The host supplies telegrams through <see cref="Append"/> and wires the save action itself.
/// A shared <see cref="GroupMonitor"/> also shares its filters and pause state with every host.
/// </summary>
public partial class BusMonitorPane : UserControl
{
    private readonly MainWindowViewModel? _vm;
    private readonly MonitorRefreshQueue _queue;
    private IReadOnlyList<MonitorEntry>? _shown;
    private Window? _host;
    private bool _detached;

    public BusMonitorPane() : this(null, null) { }

    /// <param name="vm">Project and status host; may be null while docked without a project.</param>
    /// <param name="model">Optional shared monitor, so both monitor panes show the same capture.</param>
    public BusMonitorPane(MainWindowViewModel? vm, GroupMonitor? model = null)
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
        Loaded += OnLoaded;
        Model.Project = vm?.Project;
        Model.Changed += OnModelChanged;
        Refresh();
    }

    /// <summary>Raised by the save button; the host owns the file dialog and recording format.</summary>
    public event EventHandler? SaveRecordingRequested;

    public GroupMonitor Model { get; }

    /// <summary>Feeds one telegram; safe to call from a bus thread (refresh is coalesced).</summary>
    public void Append(BusTelegram telegram) => Model.Append(telegram);

    /// <summary>Re-reads address and datapoint names after the project changed.</summary>
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
        PauseText.Text = Strings.Get(Model.IsPaused ? "BtnStart" : "BtnStop");
        PauseIcon.Symbol = Model.IsPaused ? "FiberManualRecord" : "Stop";
    }

    private void OnPause(object? sender, RoutedEventArgs e) => Model.TogglePause();

    private void OnClear(object? sender, RoutedEventArgs e) => Model.Clear();

    private void OnSaveRecording(object? sender, RoutedEventArgs e)
        => SaveRecordingRequested?.Invoke(this, EventArgs.Empty);
}
