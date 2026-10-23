using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Services;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

namespace Knx.App;

/// <summary>Object-centred native shell. The workspace and project service own state, not the table controls.</summary>
public partial class WorkbenchWindow : Window
{
    private readonly ParameterPane _parameters;
    private readonly ParameterEditingSession _parameterSession;
    private readonly ComboBox _parameterDevice = new() { MinWidth = 240, Margin = new(0, 0, 0, 12) };
    private readonly StackPanel _modePanel = new() { Margin = new(0, 12, 0, 0), Spacing = 8, MaxWidth = 540, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly StackPanel _modeActions = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
    // ParameterPane keeps its validation summary in the footer; the demo form mirrors it.
    private readonly TextBlock _modeStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "wb-error" } };
    private readonly Border _modeFooter = new() { BorderThickness = new(0, 1, 0, 0), Padding = new(0, 10, 0, 2), Margin = new(0, 12, 0, 0) };
    private readonly ScrollViewer _modeScroll = new() { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly List<Window> _toolWindows = [];
    private readonly Dictionary<WorkbenchTarget, Window> _companions = [];
    private readonly Dictionary<string, Button> _railButtons = new(StringComparer.Ordinal);
    private readonly WorkbenchAudit _audit = new();
    private WorkbenchSessions? _sessions;
    private DispatcherTimer? _sessionTimer;
    private object? _sessionIdentity;
    private bool _closingSessionWindows;
    private readonly WorkbenchDemoDrafts _demoDrafts = new();
    private bool HasParameterDrafts => _parameters.HasUnappliedChanges || _demoDrafts.HasChanges(ViewModel.Project);
    private object? _catalogProject;
    private string? _catalogLineId;
    private string? _catalogRoomId;
    private int _demoTick;
    private bool _refreshing;
    private bool _shuttingDown;
    private bool _confirmingClose;
    private readonly string? _layoutPath;
    private readonly CatalogLibrary _catalogLibrary;
    private Project? _shownProject;
    private WorkbenchTab? _renderedTab;
    private string _renderedPerspective = "building";
    private string _sidebarMode = "properties";
    private bool _simulateProgrammingFailure;
    private int _scrollRestoreVersion;
    private bool _scrollRestorePending;
    private int _navRestoreVersion;
    private bool _navRestorePending;
    private readonly List<Control> _inspectorFooters = [];
    private string? _inspectorFocusId;
    private int _inspectorFocusCaret;
    private static readonly DataFormat<string> DeviceDragFormat = DataFormat.CreateInProcessFormat<string>("buschord/workbench-device");
    private DeviceInstance? _dragDevice;
    private PointerPressedEventArgs? _dragPress;
    private Point _dragOrigin;
    private Control? _dropHighlight;
    private bool _compactToolTabs;
    private bool _compactObjectToolbar;
    private bool _searchForcedMaximize;
    private bool _syncingSelection;
    private bool _updatingSelection;
    private bool _updatingRowChecks;
    private readonly Dictionary<string, Border> _railAccents = new(StringComparer.Ordinal);
    private Border? _issueBadge;
    private TextBlock? _issueBadgeText;
    private Border? _monitorDot;
    private int _pulseTick;
    private Project? _checkedProject;
    private int _issueCount;
    public MainWindowViewModel ViewModel { get; }
    public WorkbenchWorkspace Workspace { get; }
    internal ParameterPane ParameterEditor => _parameters;
    internal WorkbenchSessions? Sessions => _sessions;

    public WorkbenchWindow() : this(new MainWindowViewModel(), new WorkbenchWorkspace(), DefaultLayoutPath) { }

    public WorkbenchWindow(MainWindowViewModel viewModel, WorkbenchWorkspace workspace, string? layoutPath = null, CatalogLibrary? catalogLibrary = null)
    {
        ViewModel = viewModel;
        ViewModel.HistoryBlocked = () => _sessions?.Programming.IsRunning == true;
        Workspace = workspace;
        _layoutPath = layoutPath;
        _catalogLibrary = catalogLibrary ?? CatalogLibrary.Shared;
        InitializeComponent();
        InitializeWindowChrome();
        DataContext = ViewModel;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _parameterSession = new(ViewModel);
        _parameters = new ParameterPane(_parameterSession);
        var parameterLayout = new DockPanel();
        DockPanel.SetDock(_parameterDevice, Avalonia.Controls.Dock.Top);
        parameterLayout.Children.Add(_parameterDevice);
        _modeScroll.Content = _modePanel;
        _modeScroll.IsVisible = false;
        // The demo form mirrors ParameterPane: scrolling fields above, sticky action footer below.
        var modeFooterHost = new Grid { ColumnDefinitions = new("*,Auto") };
        modeFooterHost.Children.Add(_modeStatus);
        Grid.SetColumn(_modeActions, 1);
        modeFooterHost.Children.Add(_modeActions);
        _modeFooter.Child = modeFooterHost;
        _modeFooter.IsVisible = false;
        _modeFooter.Classes.Add("wb-action-footer");
        var parameterContent = new Grid { RowDefinitions = new("*,Auto") };
        parameterContent.Children.Add(_parameters);
        parameterContent.Children.Add(_modeScroll);
        Grid.SetRow(_modeFooter, 1);
        parameterContent.Children.Add(_modeFooter);
        parameterLayout.Children.Add(parameterContent);
        ParametersHost.Content = parameterLayout;
        _parameterDevice.ItemTemplate = new FuncDataTemplate<DeviceRow>((d, _) => Label(d is null ? "" : $"{d.AddressText} · {d.Name}"));
        _parameterDevice.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _parameterDevice.SelectedItem is DeviceRow d)
            {
                _parameters.Load(d);
                ApplyParameterReadOnly();
            }
            UpdateModePreview();
        };
        NavTree.ItemTemplate = new FuncDataTemplate<NavEntry>((entry, _) => entry is null ? new Border() : BuildNavRow(entry));
        ObjectTable.ItemTemplate = new FuncDataTemplate<WorkbenchRow>((row, _) => row is null ? new Border() : BuildObjectRow(row));
        SidebarMode.SelectionChanged += (_, _) =>
        {
            if (_refreshing || SidebarMode.SelectedIndex < 0) return;
            _sidebarMode = SidebarMode.SelectedIndex switch
            {
                1 => "groups", 2 => "search", 3 => "workspaces", 4 => "operations", _ => "properties"
            };
            RefreshInspector();
        };
        RelabelSidebarModes();
        BuildMenus();
        BuildNavigation();
        BuildRepresentationTabs();
        BuildToolTabs();
        Knx.Strings.Instance.PropertyChanged += OnStringsChanged;
        ViewModel.PropertyChanged += OnModelChanged;
        ViewModel.Devices.CollectionChanged += OnDevicesChanged;
        ViewModel.CatalogLoaded += OnCatalogLoaded;
        ViewModel.ProjectEdited += OnProjectEdited;
        Workspace.PropertyChanged += OnWorkspaceChanged;
        SizeChanged += (_, _) => ApplyLayout();
        AddHandler(KeyDownEvent, OnShellKeyDown, RoutingStrategies.Tunnel);
        NavTree.AddHandler(KeyDownEvent, OnNavigatorKeyDown, RoutingStrategies.Tunnel);
        ObjectTable.AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel);
        // Device rows are drag sources; line and segment rows (and a line work item's table) accept drops.
        AddHandler(PointerMovedEvent, OnDragWatchMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => ResetDragWatch(), RoutingStrategies.Tunnel);
        foreach (var surface in new Control[] { NavTree, ObjectTable })
        {
            DragDrop.SetAllowDrop(surface, true);
            surface.AddHandler(DragDrop.DragOverEvent, OnDeviceDragOver);
            surface.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
            surface.AddHandler(DragDrop.DropEvent, OnDeviceDrop);
        }
        Closing += OnClosing;
        foreach (var splitter in new[] { NavSplitter, InspectorSplitter, ToolsSplitter })
        {
            splitter.AddHandler(PointerReleasedEvent, (_, _) => RememberSizes(), RoutingStrategies.Tunnel);
            splitter.KeyUp += (_, _) => RememberSizes();
        }
        Closed += (_, _) =>
        {
            ViewModel.HistoryBlocked = null;
            ViewModel.PropertyChanged -= OnModelChanged;
            ViewModel.Devices.CollectionChanged -= OnDevicesChanged;
            ViewModel.CatalogLoaded -= OnCatalogLoaded;
            ViewModel.ProjectEdited -= OnProjectEdited;
            Workspace.PropertyChanged -= OnWorkspaceChanged;
            Knx.Strings.Instance.PropertyChanged -= OnStringsChanged;
            _sessionTimer?.Stop();
            _parameters.Dispose();
            _ = DisposeSessionsAsync();
            foreach (var tool in _toolWindows.ToArray()) tool.Close();
        };
        if (_layoutPath is not null) Workspace.TryRestoreLayout(_layoutPath);
        RefreshWorkspace();
    }

    private static string DefaultLayoutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusChord", "workbench-layout.json");

    /// <summary>Perspective strip and activity rail; rebuilt only for a language change.</summary>
    private void BuildNavigation()
    {
        Perspectives.Children.Clear();
        Rail.Children.Clear();
        _railButtons.Clear();
        _railAccents.Clear();
        (Button Button, Grid Host) AddRail(string key, string title, string symbol, Action run)
        {
            var button = ActionButton("", run, "quiet", title);
            button.Content = new ConceptIcon { Symbol = symbol, Width = 19, Height = 19 };
            button.Width = 35; button.Height = 35; button.FontSize = 20;
            ToolTip.SetTip(button, title);
            AutomationProperties.SetName(button, title);
            var accent = new Border
            {
                Width = 3, CornerRadius = new(0, 2, 2, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                IsVisible = false
            };
            accent.Bind(Border.BackgroundProperty, accent.GetResourceObservable("WbAccent"));
            var host = new Grid { Width = 35, Height = 35 };
            host.Children.Add(button);
            host.Children.Add(accent);
            Rail.Children.Add(host);
            if (key.Length > 0) { _railButtons[key] = button; _railAccents[key] = accent; }
            return (button, host);
        }
        void AddRailSeparator()
        {
            var line = new Border { Height = 1, Margin = new(8, 4) };
            line.Bind(Border.BackgroundProperty, line.GetResourceObservable("WbLine"));
            Rail.Children.Add(line);
        }
        foreach (var (key, title, glyph) in new[] { ("building", W("PerspectiveBuilding"), "building"), ("topology", W("PerspectiveTopology"), "topology"), ("groups", W("PerspectiveGroups"), "groups") })
        {
            var perspective = key;
            var perspectiveButton = ActionButton(title, () => Navigate(perspective), "quiet");
            perspectiveButton.FontSize = 10; perspectiveButton.Padding = new(2, 0); perspectiveButton.MinWidth = 0;
            // Text-only like the concept's nav-kind strip: icon+label does not fit 210 DIP at 1024/900.
            perspectiveButton.Content = Label(title);
            AutomationProperties.SetName(perspectiveButton, title);
            Perspectives.Children.Add(perspectiveButton);
            AddRail(key, title, glyph, () => Navigate(perspective));
        }
        AddRailSeparator();
        var tools = new (string Symbol, string Title, Action Run)[]
        {
            ("catalog", W("Catalog"), ShowCatalog), ("warning", W("ProjectCheck"), () => ShowTool("problems")),
            ("monitor", W("Monitor"), () => ShowTool("monitor")), ("settings", W("Options"), ShowSettings)
        };
        for (var index = 0; index < tools.Length; index++)
        {
            if (index == 2) AddRailSeparator();
            var (symbol, title, run) = tools[index];
            var (button, host) = AddRail("", title, symbol, run);
            if (symbol == "warning")
            {
                _issueBadgeText = new TextBlock { FontSize = 8, FontWeight = FontWeight.SemiBold };
                _issueBadgeText.Bind(TextBlock.ForegroundProperty, _issueBadgeText.GetResourceObservable("WbOnAccent"));
                _issueBadge = new Border
                {
                    MinWidth = 14, Height = 14, CornerRadius = new(7), Padding = new(3, 0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    IsVisible = false, Child = _issueBadgeText
                };
                _issueBadge.Bind(Border.BackgroundProperty, _issueBadge.GetResourceObservable("WbError"));
                host.Children.Add(_issueBadge);
            }
            else if (symbol == "monitor")
            {
                _monitorDot = new Border
                {
                    Width = 6, Height = 6, CornerRadius = new(3),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                    Margin = new(0, 0, 2, 2), IsVisible = false
                };
                _monitorDot.Bind(Border.BackgroundProperty, _monitorDot.GetResourceObservable("WbError"));
                host.Children.Add(_monitorDot);
            }
        }
    }

    /// <summary>Objects/parameters/relations/overview tabs; labels are regenerated on a language change.</summary>
    private void BuildRepresentationTabs()
    {
        Representations.Children.Clear();
        foreach (var (key, title) in new[] { ("objects", W("CommunicationObjects")), ("params", W("Parameters")), ("relations", W("Relations")), ("overview", W("Overview")) })
        {
            var view = key;
            var tab = new TabItem { Header = title, Tag = view, Margin = new Thickness(0, 0, 18, 0) };
            tab.Classes.Add("wb-tab");
            tab.Classes.Add("wb-view-tab");
            AutomationProperties.SetName(tab, title);
            tab.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !FromButton(e.Source)) SetRepresentation(view); };
            tab.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { SetRepresentation(view); e.Handled = true; } };
            Representations.Children.Add(tab);
        }
    }

    /// <summary>Bottom tool tabs; rebuilt on a language change so headers, tips and names follow.</summary>
    private void BuildToolTabs()
    {
        ToolTabs.Children.Clear();
        _compactToolTabs = false;
        foreach (var (key, title) in new[] { ("monitor", W("Monitor")), ("programming", W("ProgrammingTab")), ("scan", W("Scan")), ("search", W("SidebarSearch")), ("changes", W("Changes")), ("problems", W("Problems")) })
        {
            var tool = key;
            var tab = new TabItem { Header = IconLabel(ToolIcon(tool), title), Tag = tool };
            tab.Classes.Add("wb-tab");
            tab.Classes.Add("wb-tool-tab");
            AutomationProperties.SetName(tab, title);
            tab.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !FromButton(e.Source)) ShowTool(tool); };
            tab.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ShowTool(tool); e.Handled = true; } };
            ToolTabs.Children.Add(tab);
        }
        SyncToolTabs();
    }

    private void SyncToolTabs()
    {
        foreach (var tab in ToolTabs.Children.OfType<TabItem>())
        {
            if (tab.Tag is not string tool) continue;
            var active = tool == Workspace.ActiveTool;
            tab.IsSelected = active;
            AutomationProperties.SetItemStatus(tab, active ? W("Selected") : "");
        }
    }

    private void RelabelSidebarModes()
    {
        var previous = _refreshing;
        _refreshing = true;
        try
        {
            SidebarMode.ItemsSource = new[]
            {
                W("SidebarProperties"), W("SidebarGroups"), W("SidebarSearch"), W("SidebarWorkspaces"), W("SidebarOperations")
            };
            SidebarMode.SelectedIndex = _sidebarMode switch { "groups" => 1, "search" => 2, "workspaces" => 3, "operations" => 4, _ => 0 };
        }
        finally { _refreshing = previous; }
    }

    private void OnStringsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Strings raises "Item[]" and "" for one change; react once.
        if (e.PropertyName != "Item[]" || _shuttingDown) return;
        if (Dispatcher.UIThread.CheckAccess()) ApplyLanguage();
        else Dispatcher.UIThread.Post(ApplyLanguage);
    }

    /// <summary>
    /// Re-renders shell-generated text after <see cref="Knx.Strings.Language"/> changed.
    /// Workspace, project, open tabs, layout and undo history are only read, never reset.
    /// </summary>
    private void ApplyLanguage()
    {
        if (_shuttingDown) return;
        BuildMenus();
        BuildWindowMenu();
        UpdateWindowChrome();
        BuildNavigation();
        BuildRepresentationTabs();
        BuildToolTabs();
        RelabelSidebarModes();
        RefreshWorkspace();
        UpdateMonitorPulse();
        // The bottom panel writes its text in code; re-selecting the active tool rebuilds it.
        if (Workspace.ToolsVisible) ShowTool(Workspace.ActiveTool);
    }

    private string? ProjectLayoutPath(string projectId)
    {
        if (_layoutPath is null) return null;
        var safe = string.Concat(projectId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (safe.Length == 0) return null;
        return Path.Combine(Path.GetDirectoryName(_layoutPath)!, "projects", safe + ".json");
    }
    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Title) or nameof(MainWindowViewModel.Project)) ScheduleRefresh();
    }
    private bool _refreshScheduled;
    private void ScheduleRefresh()
    {
        if (_refreshScheduled || _shuttingDown) return;
        _refreshScheduled = true;
        Dispatcher.UIThread.Post(() => { _refreshScheduled = false; if (!_shuttingDown) RefreshWorkspace(); });
    }
    private void OnCatalogLoaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _parameterSession.Refresh();
        ApplyParameterReadOnly();
    });
    private void OnProjectEdited(object? sender, string description)
    {
        void Add()
        {
            _audit.Add(description);
            if (Workspace.ToolsVisible && Workspace.ActiveTool == "changes") ShowTool("changes");
        }
        if (Dispatcher.UIThread.CheckAccess()) Add(); else Dispatcher.UIThread.Post(Add);
    }
    private void ApplyParameterReadOnly()
    {
        if (_parameterDevice.SelectedItem is not DeviceRow selected
            || ViewModel.Project is not { } project
            || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, selected.Device)))
        {
            _parameterSession.ClearReadOnly();
            return;
        }
        var reason = ViewModel.ParameterReadOnlyReason(selected.Device);
        if (reason is null) _parameterSession.ClearReadOnly(); else _parameterSession.SetReadOnly(reason);
    }

    private void EnsureSessions()
    {
        var project = ViewModel.Project;
        if (project is null)
        {
            _sessionTimer?.Stop();
            if (_sessions is not null) { _ = DisposeSessionsAsync(); }
            _sessionIdentity = null;
            ToolHost.Content = null;
            UpdateMonitorPulse();
            return;
        }
        if (_sessions is not null && ReferenceEquals(_sessionIdentity, ViewModel.ProjectSessionIdentity)) return;
        _ = DisposeSessionsAsync();
        _sessionIdentity = ViewModel.ProjectSessionIdentity;
        _demoDrafts.Clear();
        _catalogProject = null;
        _catalogLineId = _catalogRoomId = null;
        _sessions = new(() => ViewModel.Project,
            filter => ResolveMonitorIds(filter),
            (p, device) => new FrozenConfiguration(
                WorkbenchProject.ConfigurationSignature(p, device.Id),
                DeviceConfigurationSnapshot.Freeze(p, device).PayloadJson),
            async (_, token) =>
            {
                await Task.Delay(120, token);
                if (_simulateProgrammingFailure) throw new InvalidOperationException(W("SimulatedFailure"));
            });
        _sessionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _sessionTimer.Tick -= OnSessionTick;
        _sessionTimer.Tick += OnSessionTick;
        _sessionTimer.Start();
        UpdateMonitorPulse();
        // Tool views close over one session instance; never leave them pointing at a disposed one.
        ToolHost.Content = null;
        if (Workspace.ToolsVisible) ShowTool(Workspace.ActiveTool);
    }

    private void CloseSessionWindows()
    {
        _closingSessionWindows = true;
        try
        {
            foreach (var window in _toolWindows.ToArray()) window.Close();
        }
        finally { _closingSessionWindows = false; }
    }

    private async Task DisposeSessionsAsync()
    {
        // Detach every observer before cancellation publishes the final job state.
        CloseSessionWindows();
        ToolHost.Content = null;
        var sessions = _sessions;
        _sessions = null;
        _sessionIdentity = null;
        if (sessions is null) return;
        try { await sessions.DisposeAsync(); }
        catch (Exception ex) { ViewModel.Status = W("ToolSessionEnded", ex.Message); }
    }

    private async Task ResetSessionsForReplacementAsync()
    {
        _sessionTimer?.Stop();
        ToolHost.Content = null;
        _audit.Clear();
        await DisposeSessionsAsync();
    }

    private IReadOnlySet<string> ResolveMonitorIds(MonitorFilter filter)
    {
        if (ViewModel.Project is not { } project) return new HashSet<string>();
        return filter.Scope switch
        {
            MonitorScope.Project => ProjectQuery.GroupAddresses(project).Select(g => g.Id).ToHashSet(),
            MonitorScope.GroupAddress => new HashSet<string> { filter.Id ?? "" },
            MonitorScope.ComObject => ProjectQuery.ComObjectRefs(project).Where(r => r.Ref.Id == filter.Id)
                .SelectMany(r => r.Ref.Links).ToHashSet(),
            MonitorScope.Room => WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Room, filter.Id))
                .SelectMany(r => r.Object.Links).ToHashSet(),
            MonitorScope.Device => ProjectQuery.ComObjectRefs(project).Where(r => r.Device.Id == filter.Id)
                .SelectMany(r => r.Ref.Links).ToHashSet(),
            _ => new HashSet<string>()
        };
    }

    private void OnSessionTick(object? sender, EventArgs e)
    {
        if (_shuttingDown || _sessions is not { } sessions) return;
        UpdateMonitorPulse();
        sessions.Monitor.FlushDisplay();
        if (!sessions.Monitor.IsRunning || sessions.Monitor.Origin != SessionOrigin.Demo) return;
        if (++_demoTick % 8 != 0 || ViewModel.Project is not { } project) return;
        var groups = ProjectQuery.GroupAddresses(project).OrderBy(g => g.Address.Raw).ToList();
        if (groups.Count == 0) return;
        var group = groups[_demoTick / 8 % groups.Count];
        try
        {
            sessions.Monitor.TickDemo(new(DateTimeOffset.Now, "1.1.250", group.Address.ToString(),
                "GroupValueWrite", group.DatapointType, DemoValue(group.DatapointType), SessionOrigin.Demo,
                group.Id, ProjectQuery.ComObjectRefs(project).Where(r => r.Ref.Links.Contains(group.Id))
                    .Select(r => r.Device.Id).FirstOrDefault()));
        }
        catch (InvalidOperationException) { }
    }

    private static string DemoValue(string? dpt) => dpt switch
    {
        "1.001" or "1.018" => "1", "3.007" => "0 (stop)", "5.001" => "75 %", "9.001" => "21.5 °C", _ => "—"
    };

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_refreshing) RefreshWorkspace();
    }

    public void Navigate(string perspective)
    {
        if (!Workspace.Navigators.ContainsKey(perspective)) throw new ArgumentException("Unknown perspective.", nameof(perspective));
        CaptureNavigatorScroll();
        Workspace.NavigatorVisible = true;
        Workspace.Perspective = perspective;
        RefreshWorkspace();
    }

    private void UpdateRail()
    {
        foreach (var (key, button) in _railButtons)
        {
            var active = key == Workspace.Perspective;
            button.Classes.Set("active", active);
            button.Classes.Set("rail-active", active);
            if (_railAccents.TryGetValue(key, out var accent)) accent.IsVisible = active;
            AutomationProperties.SetItemStatus(button, active ? W("Selected") : "");
        }
    }

    /// <summary>Rail badge with the read-only project check count; recomputed only per project instance.</summary>
    private void UpdateIssueBadge(Project? project)
    {
        if (!ReferenceEquals(_checkedProject, project))
        {
            _checkedProject = project;
            _issueCount = 0;
            if (project is not null)
            {
                try { _issueCount = ProjectCheck.Run(project).Count; }
                catch (Exception) { _issueCount = 0; }
            }
        }
        if (_issueBadge is null || _issueBadgeText is null) return;
        _issueBadgeText.Text = _issueCount > 99 ? "99+" : _issueCount.ToString();
        _issueBadge.IsVisible = _issueCount > 0;
        ToolTip.SetTip(_issueBadge, W("CheckFindings", _issueCount));
        AutomationProperties.SetName(_issueBadge, W("CheckFindings", _issueCount));
    }

    private void UpdateMonitorPulse()
    {
        if (_monitorDot is null) return;
        var running = _sessions?.Monitor.IsRunning == true;
        _monitorDot.IsVisible = running;
        if (!running) return;
        _pulseTick++;
        _monitorDot.Opacity = (_pulseTick / 4) % 2 == 0 ? 1 : 0.3;
        AutomationProperties.SetName(_monitorDot, W("MonitorRunningShort"));
    }

    public WorkbenchTab OpenTarget(WorkbenchTarget target, string? view = null)
    {
        if (ViewModel.Project is { } project && Workspace.ProjectId != project.Id) RefreshWorkspace();
        var model = WorkbenchProjection.Resolve(ViewModel.Project, target);
        if (model is null && target != new WorkbenchTarget("tool", "catalog")) throw new ArgumentException("Work item no longer exists.", nameof(target));
        var title = TargetTitle(model, target);
        var tab = Workspace.Open(target, title, view);
        RefreshWorkspace();
        return tab;
    }

    private static string TargetTitle(object? model, WorkbenchTarget target) => model switch
        {
            Project p => p.Name, SpaceNode s => s.Name, Line l => l.Name ?? W("Line"),
            DeviceInstance d => d.Name ?? d.Address.ToString(), GroupAddressEntry g => $"{g.Address} · {g.Name}",
            _ => target.Id == "catalog" ? W("Catalog") : target.Id
        };

    public void SetRepresentation(string view)
    {
        if (view is not ("objects" or "params" or "relations" or "overview")) throw new ArgumentException("Unknown representation.", nameof(view));
        if (Workspace.ActiveTab is not { } tab) return;
        tab.View = view;
        RefreshEditor();
    }
    public void RefreshWorkspace()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            CaptureNavigatorScroll();
            Title = ViewModel.Title;
            if (!WorkbenchTools.IsKnown(Workspace.ActiveTool)) Workspace.ActiveTool = "monitor";
            var p = ViewModel.Project;
            if (!ReferenceEquals(_shownProject, p))
            {
                _shownProject = p;
                if (ToolPageHost.Content is CatalogPane catalog) catalog.ReloadProject();
                _parameters.Refresh();
                ApplyParameterReadOnly();
            }
            if (p is not null && Workspace.ProjectId != p.Id)
            {
                var previousProject = Workspace.ProjectId;
                if (previousProject.Length > 0 && ProjectLayoutPath(previousProject) is { } previousLayout)
                {
                    try { SaveLayout(previousLayout); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { ViewModel.Status = W("LayoutSaveFailed", ex.Message); }
                }
                Workspace.ChangeProject(p.Id);
                if (ProjectLayoutPath(p.Id) is { } projectLayout && File.Exists(projectLayout))
                    Workspace.TryRestoreLayout(projectLayout, t => t == new WorkbenchTarget("tool", "catalog") || WorkbenchProjection.Resolve(p, t) is not null);
                else if (_layoutPath is not null)
                    Workspace.TryRestoreLayout(_layoutPath, t => t == new WorkbenchTarget("tool", "catalog") || WorkbenchProjection.Resolve(p, t) is not null);
                if (!WorkbenchTools.IsKnown(Workspace.ActiveTool)) Workspace.ActiveTool = "monitor";
                foreach (var nav in Workspace.Navigators.Values) nav.ExpandedIds.Add("project:" + p.Id);
                foreach (var space in p.Installations.SelectMany(i => i.Locations.Roots)) Workspace.Navigators["building"].ExpandedIds.Add("space:" + space.Id);
            }
            if (p is not null && Workspace.Tabs.Count == 0) Workspace.Open(new("project", p.Id), p.Name);
            EnsureSessions();
            foreach (var tab in Workspace.Tabs.Where(t => t.Target.Kind != "tool" && WorkbenchProjection.Resolve(p, t.Target) is null).ToArray()) Workspace.Close(tab);
            foreach (var tab in Workspace.Tabs) tab.Title = TargetTitle(WorkbenchProjection.Resolve(p, tab.Target), tab.Target);
            ProjectName.Text = p?.Name ?? "BusChord";
            ProjectSummary.Text = p is null ? W("OpenOrCreateProject") : W("DeviceLineSummary", ProjectQuery.DeviceList(p).Count(), WorkbenchProjection.Lines(p).Count());
            StatusProject.Text = p?.Name ?? "BusChord";
            UpdateIssueBadge(p);
            // Rewriting unchanged text would clear the field's own undo stack; compare first.
            if (NavSearch.Text != Workspace.Navigators[Workspace.Perspective].Query) NavSearch.Text = Workspace.Navigators[Workspace.Perspective].Query;
            RefreshNavigation();
            UpdateRail();
            RefreshTabs();
            RefreshEditor();
            ApplyLayout();
        }
        finally { _refreshing = false; }
    }

    private void RefreshNavigation()
    {
        var previous = _refreshing;
        _refreshing = true;
        try
        {
        var nav = Workspace.Navigators[Workspace.Perspective];
        _navRestorePending = true;
        var restoreVersion = ++_navRestoreVersion;
        var nodes = ViewModel.Project is { } p ? WorkbenchProjection.Navigation(p, Workspace.Perspective) : [];
        var entries = WorkbenchProjection.VisibleNavigation(nodes, nav).Select(n => new NavEntry(n.Node, n.Depth)).ToList();
        NavTree.ItemsSource = entries;
        NavTree.SelectedItem = entries.FirstOrDefault(e => e.Node.Key == nav.SelectedId);
        for (var i = 0; i < Perspectives.Children.Count; i++)
            Perspectives.Children[i].Classes.Set("active", new[] { "building", "topology", "groups" }[i] == Workspace.Perspective);
        var perspective = Workspace.Perspective;
        _renderedPerspective = perspective;
        Dispatcher.UIThread.Post(() =>
        {
            if (restoreVersion != _navRestoreVersion) return;
            if (Workspace.Perspective == perspective && NavTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                scroll.Offset = new(0, nav.ScrollOffset);
            _navRestorePending = false;
        }, DispatcherPriority.Loaded);
        }
        finally { _refreshing = previous; }
    }

    private void RefreshTabs()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var restoreTabFocus = focused is not null && ObjectTabs.GetVisualDescendants().Contains(focused);
        ObjectTabs.Children.Clear();
        TabItem? activeButton = null;
        foreach (var tab in Workspace.Tabs)
        {
            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var title = Label(tab.Title); title.FontSize = 12; title.MaxWidth = 170; title.Margin = new(0, 0, 4, 0);
            header.Children.Add(title);
            var close = ActionButton("", () => { Workspace.Close(tab); RefreshWorkspace(); }, "quiet", W("CloseTab", tab.Title));
            close.Content = new ConceptIcon { Symbol = "close", Width = 14, Height = 14 };
            close.Width = 24; close.Height = 24; close.MinWidth = 0; close.Padding = new(0);
            header.Children.Add(close);
            var item = new TabItem { Header = header, Height = 39, Tag = tab };
            item.Classes.Add("wb-tab");
            item.Classes.Add("wb-doc-tab");
            item.IsSelected = tab == Workspace.ActiveTab;
            AutomationProperties.SetName(item, W("OpenTab", tab.Title));
            AutomationProperties.SetItemStatus(item, tab == Workspace.ActiveTab ? W("Selected") : "");
            item.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || FromButton(e.Source)) return;
                CaptureScroll(); Workspace.ActiveTab = tab;
            };
            item.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space)) return;
                CaptureScroll(); Workspace.ActiveTab = tab; e.Handled = true;
            };
            if (tab == Workspace.ActiveTab) activeButton = item;
            ObjectTabs.Children.Add(item);
        }
        var addTab = ActionButton("", ShowPalette, "quiet", W("FindWorkItem"));
        addTab.Content = new ConceptIcon { Symbol = "plus", Width = 17, Height = 17 };
        ObjectTabs.Children.Add(addTab);
        if (restoreTabFocus) activeButton?.Focus();
    }

    /// <summary>Insertion line suggested by the current work item; rooms fall back to their first device/line.</summary>
    private Line? CurrentLine()
    {
        if (ViewModel.Project is not { } project || Workspace.ActiveTab is not { } tab) return null;
        return tab.Target.Kind switch
        {
            "line" => WorkbenchProjection.Lines(project).FirstOrDefault(l => l.Id == tab.Target.Id),
            "device" => ProjectQuery.DeviceList(project).FirstOrDefault(d => d.Id == tab.Target.Id) is { } device
                ? ProjectQuery.LocateDevice(project, device.Address)?.Line : null,
            "room" => WorkbenchProjection.Devices(project, tab.Target).Select(d => ProjectQuery.LocateDevice(project, d.Address)?.Line)
                .FirstOrDefault(l => l is not null) ?? WorkbenchProjection.Lines(project).FirstOrDefault(),
            _ => WorkbenchProjection.Lines(project).FirstOrDefault()
        };
    }

    private DeviceRow? CurrentDevice()    {
        if (ViewModel.Project is not { } p || Workspace.ActiveTab is not { } tab) return null;
        var selected = WorkbenchProjection.Rows(p, tab).FirstOrDefault(r => tab.SelectedObjectIds.Contains(r.Key));
        var d = selected?.Device ?? WorkbenchProjection.Devices(p, tab.Target).FirstOrDefault();
        return d is null ? null : ViewModel.Devices.FirstOrDefault(r => ReferenceEquals(r.Device, d)) ?? new DeviceRow(d);
    }

    private void RefreshEditor()
    {
        CaptureScroll();
        var previous = _refreshing;
        _refreshing = true;
        try
        {
            var tab = Workspace.ActiveTab;
            _renderedTab = tab;
            var tool = tab?.Target.Kind == "tool";
            EditorTitle.Text = tab?.Title ?? W("Welcome");
            var subject = tab is null ? null : WorkbenchProjection.Resolve(ViewModel.Project, tab.Target);
            var context = subject switch
            {
                SpaceNode space => W("BreadcrumbBuilding", space.Parent?.Path ?? space.Name),
                DeviceInstance device => W("BreadcrumbDevice", device.Address),
                Line => W("PerspectiveTopology"), GroupAddressEntry => W("GroupAddresses"), Project => W("ProjectOverview"), _ => W("ProjectTools")
            };
            Breadcrumb.Text = $"{ViewModel.Project?.Name ?? W("Project")}  ›  {context}";
            Subtitle.Text = tab is null ? W("WelcomeHint")
                : tool ? W("ToolSubtitle") : ViewModel.Project is { } p
                    ? W("ScopeSummary", WorkbenchProjection.Devices(p, tab.Target).Count()) : "";
            Representations.IsVisible = tab is not null && !tool;
            var workItem = tab is not null && !tool && tab.Target.Kind is not "project";
            WatchButton.IsVisible = AddDeviceButton.IsVisible = InspectorButton.IsVisible = workItem;
            TableViewport.IsVisible = TableHost.IsVisible = tab is not null && !tool && tab.View == "objects";
            ObjectToolbar.IsVisible = TableHost.IsVisible;
            ParametersHost.IsVisible = !tool && tab?.View == "params";
            RelationsHost.IsVisible = !tool && tab?.View == "relations";
            ToolPageHost.IsVisible = tool || tab is null || tab.View == "overview";
            if (tab is not null)
            {
                // Only write when the filter changed, otherwise the field's undo stack is reset.
                if (ObjectSearch.Text != tab.Filter) ObjectSearch.Text = tab.Filter;
                Unlinked.IsChecked = tab.UnlinkedOnly;
                RelatedBox.IsChecked = tab.IncludeRelated;
                var rows = ViewModel.Project is { } project ? WorkbenchProjection.Rows(project, tab) : [];
                _scrollRestorePending = true;
                var restoreVersion = ++_scrollRestoreVersion;
                ObjectTable.ItemsSource = rows;
                if (ObjectTable.SelectedItems is { } selection)
                {
                    selection.Clear();
                    foreach (var row in rows.Where(r => tab.SelectedObjectIds.Contains(r.Key))) selection.Add(row);
                }
                UpdateSelectionState();
                TableFootnote.Text = W("TableFootnote", rows.Count(r => r.Object is not null));
                var current = CurrentDevice();
                if (ParametersHost.IsVisible)
                {
                    var devices = ViewModel.Project is { } pp ? WorkbenchProjection.Devices(pp, tab.Target).Select(d => new DeviceRow(d)).ToList() : [];
                    _parameterDevice.ItemsSource = devices;
                    _parameterDevice.SelectedItem = devices.FirstOrDefault(d => d.Device == current?.Device) ?? devices.FirstOrDefault();
                    var selected = _parameterDevice.SelectedItem as DeviceRow;
                    if (!ReferenceEquals(_parameters.Device, selected?.Device)) _parameters.Load(selected);
                    ApplyParameterReadOnly();
                    UpdateModePreview();
                }
                if (RelationsHost.IsVisible) RelationsHost.Content = CreateRelationsView();
                if (tool && tab.Target.Id == "catalog")
                {
                    var sameProject = ReferenceEquals(_catalogProject, ViewModel.ProjectSessionIdentity);
                    var line = ViewModel.Project is { } catalogProject
                        ? WorkbenchProjection.Lines(catalogProject).FirstOrDefault(l => sameProject && l.Id == _catalogLineId)
                            ?? WorkbenchProjection.Lines(catalogProject).FirstOrDefault() : null;
                    var roomId = sameProject ? _catalogRoomId : null;
                    if (roomId is not null && WorkbenchProjection.Resolve(ViewModel.Project, new("room", roomId)) is null) roomId = null;
                    if (ToolPageHost.Content is CatalogPane open) { open.PreferredLine = line; open.PreferredRoomId = roomId; }
                    else ToolPageHost.Content = new CatalogPane(ViewModel, _catalogLibrary) { DataContext = ViewModel, PreferredLine = line, PreferredRoomId = roomId };
                }
                else if (tab.View == "overview") ToolPageHost.Content = CreateProblemsView(tab.Target);
                for (var i = 0; i < Representations.Children.Count; i++)
                {
                    var activeView = new[] { "objects", "params", "relations", "overview" }[i] == tab.View;
                    if (Representations.Children[i] is TabItem representation) representation.IsSelected = activeView;
                    AutomationProperties.SetItemStatus(Representations.Children[i], activeView ? W("Selected") : "");
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (restoreVersion != _scrollRestoreVersion) return;
                    if (Workspace.ActiveTab == tab && ObjectTable.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
                        scroller.Offset = new(0, tab.ScrollOffset);
                    _scrollRestorePending = false;
                }, DispatcherPriority.Loaded);
            }
            else
            {
                var start = new StackPanel { Spacing = 12 };
                start.Children.Add(EmptyState("building", W("EmptyStartHeading"), W("WelcomeHint")));
                start.Children.Add(ActionButton(W("OpenProjectEllipsis"), () => _ = GuardAsync(OpenProjectAsync), "primary"));
                start.Children.Add(ActionButton(W("NewProjectEllipsis"), () => _ = GuardAsync(NewProjectAsync)));
                start.Children.Add(ActionButton(W("DeviceFromCatalogEllipsis"), ShowCatalog));
                start.Children.Add(ActionButton(W("PortfolioStart"), () => _ = GuardAsync(ShowPortfolioAsync), "quiet"));
                foreach (var recent in ViewModel.RecentProjects)
                    start.Children.Add(ActionButton(recent.Name, () => _ = OpenPathAsync(recent.Path), "quiet"));
                ToolPageHost.Content = start;
            }
            RefreshInspector();
        }
        finally { _refreshing = previous; }
    }

    private Control BuildNavRow(NavEntry entry)
    {
        var node = entry.Node;
        var grid = new Grid { ColumnDefinitions = new("24,*,Auto"), Margin = new(entry.Depth * 12, 0, 4, 0), Height = 29, Tag = node };
        var pinned = node.Target is { } nodeTarget && Workspace.IsFavorite(nodeTarget);
        AutomationProperties.SetName(grid, node.Meta.Length > 0 ? $"{node.Name}, {node.Meta}" : node.Name);
        if (Workspace.ActiveTab?.Target is { } activeTarget && node.Target == activeTarget) grid.Classes.Add("wb-row-active");
        var hasChildren = node.Children.Count > 0;
        var expanded = Workspace.Navigators[Workspace.Perspective].ExpandedIds.Contains(node.Key);
        var expand = ActionButton(hasChildren ? "" : "·", () =>
        {
            var set = Workspace.Navigators[Workspace.Perspective].ExpandedIds;
            CaptureNavigatorScroll();
            if (!set.Add(node.Key)) set.Remove(node.Key);
            RefreshNavigation();
        }, "quiet", W("ExpandCollapse", node.Name));
        if (hasChildren)
            expand.Content = new ConceptIcon { Symbol = expanded ? "down" : "chevron", Width = 16, Height = 16 };
        expand.IsEnabled = node.Children.Count > 0; expand.Padding = new(0);
        expand.Width = expand.Height = expand.MinWidth = expand.MinHeight = 24;
        grid.Children.Add(expand);
        var label = Label(node.Name); label.FontSize = 11; Grid.SetColumn(label, 1); grid.Children.Add(label);
        var meta = Label(pinned ? "★ " + node.Meta : node.Meta); meta.FontSize = 9; meta.Classes.Add("wb-subtle"); Grid.SetColumn(meta, 2); grid.Children.Add(meta);
        ToolTip.SetTip(grid, node.Name + " · " + node.Meta);
        grid.ContextMenu = BuildNavigationMenu(node);
        if (node.Model is DeviceInstance device) WatchDeviceDrag(grid, device);
        return grid;
    }

    private ContextMenu? BuildNavigationMenu(WorkbenchNavNode node)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        switch (node.Model)
        {
            case Area area:
                Add(W("NewLine"), () => _ = CreateLineAsync(area));
                break;
            case Line line:
                Add(W("NewSegment"), () => _ = CreateSegmentAsync(line));
                Add(W("Rename"), () => _ = RenameLineAsync(line));
                Add(W("Move"), () => _ = MoveLineAsync(line));
                Add(W("Delete"), () => _ = DeleteLineAsync(line));
                break;
            case Segment segment:
                Add(W("Rename"), () => _ = RenameSegmentAsync(segment));
                Add(W("Move"), () => _ = MoveSegmentAsync(segment));
                Add(W("Delete"), () => _ = DeleteSegmentAsync(segment));
                break;
            case DeviceInstance device:
                Add(W("OpenBeside"), () => OpenCompanion(new("device", device.Id)));
                Add(W("Rename"), () => _ = RenameDeviceAsync(device));
                Add(W("Copy"), () => _ = CopyDeviceAsync(device));
                Add(W("Move"), () => _ = MoveDeviceAsync(device));
                Add(W("Delete"), () => _ = DeleteDeviceAsync(device));
                break;
            case GroupAddressEntry group:
                Add(W("OpenBeside"), () => OpenCompanion(new("ga", group.Id)));
                Add(W("Rename"), () => _ = RenameGroupAddressAsync(group));
                Add(W("Copy"), () => _ = CopyGroupAddressAsync(group));
                Add(W("Delete"), () => _ = DeleteGroupAddressAsync(group));
                break;
        }
        if (node.Target is { } target)
            Add(Workspace.IsFavorite(target) ? W("Unfavorite") : W("Favorite"), () => ToggleFavorite(target, node.Name));
        return menu.Items.Count > 0 ? menu : null;
    }

    private async Task CreateLineAsync(Area area)
    {
        var name = await PromptTextAsync(W("NewName"), W("Create"));
        if (string.IsNullOrWhiteSpace(name)) return;
        ApplyOperation(W("NewLine"), p => AsResult(ProjectOperations.CreateLine(p, area, name)));
    }

    private async Task CreateSegmentAsync(Line line)
    {
        var name = await PromptTextAsync(W("NewName"), W("Create"));
        if (string.IsNullOrWhiteSpace(name)) return;
        ApplyOperation(W("NewSegment"), p => AsResult(ProjectOperations.CreateSegment(p, line, name)));
    }

    private async Task RenameLineAsync(Line line)
    {
        var name = await PromptTextAsync(W("NewName"), W("Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        ApplyOperation(W("Rename"), p => ProjectOperations.RenameLine(p, line, name));
    }

    private async Task RenameSegmentAsync(Segment segment)
    {
        var name = await PromptTextAsync(W("NewName"), W("Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        ApplyOperation(W("Rename"), p => ProjectOperations.RenameSegment(p, segment, name));
    }

    private async Task DeleteLineAsync(Line line)
    {
        if (!await ConfirmDialog.AskDeleteAsync(this, line.Name ?? W("Line"))) return;
        ApplyOperation(W("Delete"), p => ProjectOperations.DeleteLine(p, line));
    }

    private async Task DeleteSegmentAsync(Segment segment)
    {
        if (!await ConfirmDialog.AskDeleteAsync(this, segment.Name ?? W("Segment"))) return;
        ApplyOperation(W("Delete"), p => ProjectOperations.DeleteSegment(p, segment));
    }

    private async Task RenameDeviceAsync(DeviceInstance device)
    {
        var name = await PromptTextAsync(W("NewName"), W("Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var project = ViewModel.Project;
        var line = project is null ? null : ProjectQuery.LocateDevice(project, device.Address)?.Line;
        var room = project?.Installations.SelectMany(i => i.Locations.AllNodes)
            .FirstOrDefault(n => n.DeviceRefs.Contains(device.Id));
        var error = ViewModel.UpdateDeviceDetails(device, name, device.Address.ToString(), line?.Id, room?.Id);
        if (error is not null) ViewModel.Status = error;
        RefreshWorkspace();
    }

    private async Task CopyDeviceAsync(DeviceInstance device)
    {
        if (ViewModel.Project is not { } project) return;
        var line = ProjectQuery.LocateDevice(project, device.Address)?.Line;
        if (line is null) { ViewModel.Status = W("TargetLineMissing"); return; }
        var segment = line.Segments.FirstOrDefault(s => s.Devices.Contains(device));
        var room = project.Installations.SelectMany(i => i.Locations.AllNodes)
            .FirstOrDefault(n => n.DeviceRefs.Contains(device.Id));
        ApplyOperation(W("Copy"), p => AsResult(ProjectOperations.CopyDevice(p, device, line, segment, room?.Id,
            (device.Name ?? device.Id) + " (2)")));
    }

    private async Task MoveDeviceAsync(DeviceInstance device)
    {
        if (ViewModel.Project is not { } project) return;
        var source = ProjectQuery.LocateDevice(project, device.Address);
        if (source is not { Line: { } sourceLine }) { ViewModel.Status = W("TargetLineMissing"); return; }
        var lines = project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).ToList();
        var dialog = CreateDialog(W("Move"), 460, 220);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        var picker = new ComboBox { ItemsSource = lines, SelectedIndex = Math.Max(0, lines.FindIndex(l => l.Id == sourceLine.Id)), Classes = { "wb-field" } };
        picker.ItemTemplate = new FuncDataTemplate<Line>((line, _) => Label(line is null ? ""
            : $"{line.Address} · {line.Name ?? W("Line")}"));
        panel.Children.Add(Note(W("ChooseLine")));
        panel.Children.Add(picker);
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(W("Move"), () =>
            {
                if (picker.SelectedItem is not Line target) return;
                var segment = target.Id == sourceLine.Id
                    ? sourceLine.Segments.FirstOrDefault(s => s.Devices.Contains(device)) : null;
                var room = project.Installations.SelectMany(i => i.Locations.AllNodes)
                    .FirstOrDefault(n => n.DeviceRefs.Contains(device.Id));
                dialog.Close();
                ApplyOperation(W("Move"), p => ProjectOperations.MoveDevice(p, device, target, segment, room?.Id));
            }, "primary"));
        dialog.Content = DialogLayout(panel, actions);
        dialog.Opened += (_, _) => picker.Focus();
        await dialog.ShowDialog(this);
    }

    private async Task MoveLineAsync(Line line)
    {
        if (ViewModel.Project is not { } project || ProjectQuery.FindInstallationOf(project, line) is not { } installation)
            return;
        var areas = installation.Areas.ToList();
        var sourceArea = areas.FirstOrDefault(a => a.Lines.Contains(line) || a.Lines.Any(l => l.Id == line.Id));
        var dialog = CreateDialog(W("Move"), 460, 220);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        var picker = new ComboBox { ItemsSource = areas, SelectedIndex = Math.Max(0, areas.FindIndex(a => a.Id == sourceArea?.Id)), Classes = { "wb-field" } };
        picker.ItemTemplate = new FuncDataTemplate<Area>((area, _) => Label(area is null ? ""
            : $"{area.Address} · {area.Name ?? W("AreaNode", area.Address, "")}"));
        panel.Children.Add(Note(W("Move"))); panel.Children.Add(picker);
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(W("Move"), () =>
            {
                if (picker.SelectedItem is not Area target) return;
                dialog.Close();
                ApplyOperation(W("Move"), p => ProjectOperations.MoveLine(p, line, target));
            }, "primary"));
        dialog.Content = DialogLayout(panel, actions);
        dialog.Opened += (_, _) => picker.Focus();
        await dialog.ShowDialog(this);
    }

    private async Task MoveSegmentAsync(Segment segment)
    {
        if (ViewModel.Project is not { } project) return;
        var lines = project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).ToList();
        var dialog = CreateDialog(W("Move"), 460, 220);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        var picker = new ComboBox { ItemsSource = lines, SelectedIndex = 0, Classes = { "wb-field" } };
        picker.ItemTemplate = new FuncDataTemplate<Line>((line, _) => Label(line is null ? ""
            : $"{line.Address} · {line.Name ?? W("Line")}"));
        panel.Children.Add(Note(W("Move"))); panel.Children.Add(picker);
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(W("Move"), () =>
            {
                if (picker.SelectedItem is not Line target) return;
                dialog.Close();
                ApplyOperation(W("Move"), p => ProjectOperations.MoveSegment(p, segment, target));
            }, "primary"));
        dialog.Content = DialogLayout(panel, actions);
        dialog.Opened += (_, _) => picker.Focus();
        await dialog.ShowDialog(this);
    }

    private async Task DeleteDeviceAsync(DeviceInstance device)
    {
        if (!await ConfirmDialog.AskDeleteAsync(this, device.Name ?? device.Id)) return;
        ApplyOperation(W("Delete"), p => ProjectOperations.DeleteDevice(p, device));
    }

    private async Task RenameGroupAddressAsync(GroupAddressEntry group)
    {
        var name = await PromptTextAsync(W("NewName"), W("Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        ApplyOperation(W("Rename"), p => ProjectOperations.RenameGroupAddress(p, group, name, group.DatapointType));
    }

    private async Task CopyGroupAddressAsync(GroupAddressEntry group)
    {
        ApplyOperation(W("Copy"), p => AsResult(ProjectOperations.CopyGroupAddress(p, group,
            name: group.Name + " (2)")));
    }

    private async Task DeleteGroupAddressAsync(GroupAddressEntry group)
    {
        if (!await ConfirmDialog.AskDeleteAsync(this, group.Name)) return;
        ApplyOperation(W("Delete"), p => ProjectOperations.DeleteGroupAddress(p, group));
    }

    private void ApplyOperation(string description, Func<Project, ProjectEditResult> operation)
    {
        if (ViewModel.ApplyProjectOperation(description, operation)) RefreshWorkspace();
    }

    private static ProjectEditResult AsResult<T>(ProjectEditResult<T> result) =>
        result.Ok ? new(ProjectEditStatus.Ok) : new(result.Status, result.Message);

    private Control BuildObjectRow(WorkbenchRow row)
    {
        var tab = Workspace.ActiveTab;
        var grid = new Grid { ColumnDefinitions = new("28,*,80,50,50,170"), Height = 32, Margin = new(0, 0, 12, 0) };
        var parts = new List<string> { row.Name };
        if (row.Address.Length > 0) parts.Add(row.Address);
        if (row.External) parts.Add(W("ExternalChannel"));
        AutomationProperties.SetName(grid, row.Object is null ? string.Join(", ", parts)
            : W(row.Links.Length > 0 ? "RowDescription" : "RowDescriptionNoLinks", row.Name, row.Address,
                row.Dpt ?? W("Unknown"), row.Bits is { } bitCount ? W("BitsValue", bitCount) : W("UnknownBits"),
                row.Flags ?? W("Unknown"), row.Links));
        if (row.Kind == "device") grid.Classes.Add("wb-device");
        if (row.Kind is "channel" or "group") grid.Classes.Add("wb-channel");
        // The table checkbox mirrors the ListBox selection; it never owns selection state itself.
        var select = new CheckBox
        {
            IsChecked = tab?.SelectedObjectIds.Contains(row.Key) == true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            MinWidth = 24, Width = 24, Padding = new(0), Tag = row,
        };
        AutomationProperties.SetName(select, W("SelectObject", row.Name));
        select.IsCheckedChanged += (_, _) => SetRowSelection(row, select.IsChecked == true);
        grid.Children.Add(select);
        var name = new Grid { ColumnDefinitions = new("24,Auto,*"), Margin = new(4 + row.Depth * 12, 0, 6, 0) };
        if (row.CanExpand)
        {
            var expand = ActionButton("", () =>
            {
                if (Workspace.ActiveTab is not { } t) return;
                var key = row.Kind == "device" ? row.Device.Id : row.Key;
                if (!t.CollapsedDeviceIds.Add(key)) t.CollapsedDeviceIds.Remove(key);
                CaptureScroll(); RefreshEditor();
            }, "quiet", W("ExpandCollapse", row.Name));
            expand.Content = new ConceptIcon { Symbol = row.Expanded ? "down" : "chevron", Width = 16, Height = 16 };
            expand.Padding = new(0);
            expand.Width = expand.Height = expand.MinWidth = expand.MinHeight = 24;
            name.Children.Add(expand);
        }
        var address = Label(row.Address); address.FontSize = 10; address.Classes.Add("wb-subtle"); address.Margin = new(0, 0, 9, 0); Grid.SetColumn(address, 1); name.Children.Add(address);
        var text = Label(row.Name); text.FontSize = 11; text.FontWeight = row.Kind == "device" ? FontWeight.SemiBold : FontWeight.Normal;
        Grid.SetColumn(text, 2); name.Children.Add(text); Grid.SetColumn(name, 1); grid.Children.Add(name);
        if (row.Object is not null)
        {
            var values = new[] { row.Dpt ?? "—", row.Bits is { } bits ? W("BitsValue", bits) : "—", row.Flags ?? "—" };
            for (var i = 0; i < 3; i++)
            {
                var value = Label(values[i]); value.FontSize = 10; value.Classes.Add("wb-muted");
                Grid.SetColumn(value, i + 2); grid.Children.Add(value);
            }
            var links = BuildLinkChips(row);
            Grid.SetColumn(links, 5); grid.Children.Add(links);
            if (!row.Active) { text.Classes.Add("wb-muted"); ToolTip.SetTip(text, row.Name + W("InactiveSuffix")); }
            if (row.External) text.Classes.Add("wb-success");
        }
        else if (row.External)
        {
            var external = Label(W("ExternalChannel")); external.FontSize = 9; external.Classes.Add("wb-success");
            Grid.SetColumn(external, 2); grid.Children.Add(external);
        }
        ToolTip.SetTip(text, row.Name);
        if (row.Kind == "device") WatchDeviceDrag(grid, row.Device);
        grid.ContextMenu = BuildObjectRowMenu(row);
        return grid;
    }

    /// <summary>Compact group-address chips; the full list stays in the tooltip.</summary>
    private static Control BuildLinkChips(WorkbenchRow row)
    {
        var chips = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        if (row.Links.Length == 0)
        {
            var none = Label(W("NoLinks")); none.FontSize = 10; none.Classes.Add("wb-subtle"); chips.Children.Add(none);
            return chips;
        }
        var details = row.LinkDetails.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var detail in details.Take(2))
        {
            var address = detail.Split(" · ", 2, StringSplitOptions.None)[0];
            var caption = new TextBlock { Text = address, FontSize = 9, MaxWidth = 84, TextTrimming = TextTrimming.CharacterEllipsis };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("WbSelectedText"));
            var chip = new Border { CornerRadius = new(3), Padding = new(4, 1), Child = caption, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("WbTint"));
            chips.Children.Add(chip);
        }
        if (details.Length > 2)
        {
            var more = Label("+" + (details.Length - 2)); more.FontSize = 9; more.Classes.Add("wb-accent");
            chips.Children.Add(more);
        }
        ToolTip.SetTip(chips, row.LinkDetails);
        return chips;
    }

    private ContextMenu BuildObjectRowMenu(WorkbenchRow row)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(W("Open"), () =>
        {
            if (row.Object is { } obj) RevealComObject(row.Device, obj.Id);
            else OpenTarget(new("device", row.Device.Id));
        });
        Add(W("EditLinks"), () => OpenTarget(new("device", row.Device.Id), "relations"));
        var target = new WorkbenchTarget("device", row.Device.Id);
        Add(Workspace.IsFavorite(target) ? W("Unfavorite") : W("Favorite"),
            () => ToggleFavorite(target, row.Device.Name ?? row.Name));
        Add(W("Delete"), () => _ = DeleteDeviceAsync(row.Device));
        return menu;
    }

    /// <summary>Mirrors one row checkbox into the ListBox multi-selection without rebuilding the table.</summary>
    private void SetRowSelection(WorkbenchRow row, bool selected)
    {
        if (_refreshing || _syncingSelection || _updatingRowChecks || Workspace.ActiveTab is not { } tab) return;
        _syncingSelection = true;
        try
        {
            if (selected)
            {
                if (!tab.SelectedObjectIds.Contains(row.Key)) tab.SelectedObjectIds.Add(row.Key);
                if (ObjectTable.SelectedItems is { } items && !items.Contains(row)) items.Add(row);
            }
            else
            {
                while (tab.SelectedObjectIds.Remove(row.Key)) { }
                ObjectTable.SelectedItems?.Remove(row);
            }
        }
        finally { _syncingSelection = false; }
        UpdateSelectionState();
        if (CurrentDevice() is { } device) ViewModel.SelectedDevice = device;
        _sessions?.Monitor.SelectionChanged(SelectedComObjectId(), CurrentDevice()?.Device.Id);
        RefreshInspector();
    }

    /// <summary>Header tri-state from the same selection set the ListBox uses; never a second selection model.</summary>
    private void UpdateSelectionState()
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        try
        {
            if (ObjectTable.ItemsSource is IEnumerable<WorkbenchRow> rows)
            {
                var total = 0;
                var chosen = 0;
                var ids = Workspace.ActiveTab?.SelectedObjectIds;
                foreach (var row in rows)
                {
                    total++;
                    if (ids?.Contains(row.Key) == true) chosen++;
                }
                SelectAllBox.IsChecked = total == 0 || chosen == 0 ? false : chosen == total ? true : null;
            }
            else SelectAllBox.IsChecked = false;
        }
        finally { _updatingSelection = false; }
        UpdateRowCheckStates();
    }

    /// <summary>Keeps realized checkboxes in sync when Shift/Ctrl or keyboard selection changes.</summary>
    private void UpdateRowCheckStates()
    {
        if (Workspace.ActiveTab is not { } tab) return;
        _updatingRowChecks = true;
        try
        {
            foreach (var check in ObjectTable.GetVisualDescendants().OfType<CheckBox>())
                if (check.Tag is WorkbenchRow row) check.IsChecked = tab.SelectedObjectIds.Contains(row.Key);
        }
        finally { _updatingRowChecks = false; }
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || _updatingSelection) return;
        if (SelectAllBox.IsChecked == true) SelectAllRows(); else ClearSelection();
        UpdateSelectionState();
    }

    /// <summary>
    /// Opens the owning device's object table and selects/reveals one communication object.
    /// Search results therefore target the object itself instead of a generic relations view.
    /// </summary>
    internal void RevealComObject(DeviceInstance device, string objectId)
    {
        if (ViewModel.Project is not { } project) return;
        var obj = device.ComObjectRefs.FirstOrDefault(o => o.Id == objectId || o.ComObjectId == objectId);
        if (!ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device))) return;
        var tab = OpenTarget(new("device", device.Id), "objects");
        tab.Filter = "";
        tab.UnlinkedOnly = false;
        tab.IncludeRelated = true;
        tab.CollapsedDeviceIds.Remove(device.Id);
        foreach (var key in tab.CollapsedDeviceIds.Where(k => k.StartsWith("channel:" + device.Id + ":", StringComparison.Ordinal)).ToArray())
            tab.CollapsedDeviceIds.Remove(key);
        tab.SelectedObjectIds.Clear();
        if (obj is null) { RefreshWorkspace(); return; }
        var rowKey = WorkbenchProjection.ObjectKey(device, obj);
        tab.SelectedObjectIds.Add(rowKey);
        RefreshWorkspace();
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Workspace.ActiveTab, tab) || ObjectTable.ItemsSource is not IEnumerable<WorkbenchRow> rows) return;
            if (rows.FirstOrDefault(r => r.Key == rowKey) is { } target) ObjectTable.ScrollIntoView(target);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A row becomes a drag source only after a four-pixel move with the left button held; the
    /// press itself stays unhandled so ListBox selection and multi-selection keep working.
    /// </summary>
    private void WatchDeviceDrag(Control row, DeviceInstance device)
        => row.AddHandler(PointerPressedEvent, (_, e) => StartDragWatch(device, e), RoutingStrategies.Bubble, handledEventsToo: true);

    private void StartDragWatch(DeviceInstance device, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragDevice = device;
        _dragPress = e;
        _dragOrigin = e.GetPosition(this);
    }

    private void OnDragWatchMoved(object? sender, PointerEventArgs e)
    {
        if (_dragDevice is null || _dragPress is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { ResetDragWatch(); return; }
        var delta = e.GetPosition(this) - _dragOrigin;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
        var device = _dragDevice;
        var press = _dragPress;
        ResetDragWatch();
        _ = BeginDeviceDragAsync(press, device);
    }

    private void ResetDragWatch()
    {
        _dragDevice = null;
        _dragPress = null;
    }

    private async Task BeginDeviceDragAsync(PointerPressedEventArgs press, DeviceInstance device)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DeviceDragFormat, device.Id));
        try { await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move); }
        catch (Exception ex) { ViewModel.Status = ex.Message; }
        finally { ResetDragWatch(); ClearDropHighlight(); }
    }

    /// <summary>The navigator row under the pointer, if it belongs to this drag session.</summary>
    private static (Control Row, WorkbenchNavNode Node)? NodeAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { Tag: WorkbenchNavNode node } row) return (row, node);
        return null;
    }

    /// <summary>Line and segment nodes are drop targets; a line work item's table accepts the same drop.</summary>
    private WorkbenchTarget? DropTargetOf(object? sender, object? source)
    {
        if (NodeAt(source)?.Node.Target is { Kind: "line" or "segment" } nodeTarget) return nodeTarget;
        return ReferenceEquals(sender, ObjectTable) && Workspace.ActiveTab?.Target is { Kind: "line" } lineTarget ? lineTarget : null;
    }

    private void OnDeviceDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(DeviceDragFormat) is not { } deviceId) { e.DragEffects = DragDropEffects.None; return; }
        var target = DropTargetOf(sender, e.Source);
        var ok = target is not null && ViewModel.Project is { } project && WorkbenchProjection.ResolveDeviceDrop(project, deviceId, target).Ok;
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
        HighlightDropRow(ok ? NodeAt(e.Source)?.Row : null);
        e.Handled = true;
    }

    private void OnDeviceDrop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        if (e.DataTransfer.TryGetValue(DeviceDragFormat) is not { } deviceId
            || DropTargetOf(sender, e.Source) is not { } target || ViewModel.Project is not { } project) return;
        e.Handled = true;
        MoveDeviceTo(project, deviceId, target);
    }

    /// <summary>Validated drop; an invalid target leaves project, undo history and selection untouched.</summary>
    private void MoveDeviceTo(Project project, string deviceId, WorkbenchTarget target)
    {
        var drop = WorkbenchProjection.ResolveDeviceDrop(project, deviceId, target);
        if (!drop.Ok)
        {
            ViewModel.Status = drop.Status switch
            {
                DeviceDropStatus.AlreadyThere => W("DropAlreadyThere"),
                DeviceDropStatus.DeviceMissing => W("DeviceMissing"),
                DeviceDropStatus.LineMissing => W("TargetLineMissing"),
                _ => W("DropNeedsLine")
            };
            return;
        }
        var device = ProjectQuery.DeviceList(project).First(d => d.Id == deviceId);
        ApplyOperation(W("DeviceMoved", device.Name ?? device.Address.ToString(), drop.Segment?.Name ?? drop.Line!.Name ?? W("Line")),
            p => ProjectOperations.MoveDevice(p, device, drop.Line!, drop.Segment));
    }

    private void HighlightDropRow(Control? row)
    {
        if (ReferenceEquals(_dropHighlight, row)) return;
        _dropHighlight?.Classes.Set("wb-drop-target", false);
        _dropHighlight = row;
        _dropHighlight?.Classes.Set("wb-drop-target", true);
    }

    private void ClearDropHighlight() => HighlightDropRow(null);

    private void OnNavigatorKeyDown(object? sender, KeyEventArgs e)
    {
        if (NavTree.SelectedItem is not NavEntry entry || !entry.Node.Children.Any()) return;
        var set = Workspace.Navigators[Workspace.Perspective].ExpandedIds;
        if (e.Key == Key.Right) { set.Add(entry.Node.Key); CaptureNavigatorScroll(); RefreshNavigation(); e.Handled = true; }
        else if (e.Key == Key.Left) { set.Remove(entry.Node.Key); CaptureNavigatorScroll(); RefreshNavigation(); e.Handled = true; }
    }

    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || ObjectTable.SelectedItem is not WorkbenchRow row
            || Workspace.ActiveTab is not { } tab || !row.CanExpand) return;
        var key = row.Kind == "device" ? row.Device.Id : row.Key;
        if (e.Key == Key.Left) tab.CollapsedDeviceIds.Add(key); else tab.CollapsedDeviceIds.Remove(key);
        RefreshEditor();
        e.Handled = true;
    }

    private void FillGroupSidebar()
    {
        if (ViewModel.Project is not { } project) { InspectorBody.Children.Add(Note(W("ScanNeedsProject"))); return; }
        var groups = ProjectQuery.GroupAddresses(project).OrderBy(g => g.Address.Raw).ToList();
        InspectorBody.Children.Add(Note(W("GroupAddressCount", groups.Count)));
        foreach (var ga in groups.Take(250))
            InspectorBody.Children.Add(ActionButton($"{ga.Address} · {ga.Name}",
                () => OpenTarget(new("ga", ga.Id)), "quiet"));
        if (groups.Count > 250) InspectorBody.Children.Add(Note(W("MoreItems")));
    }

    private void FillSearchSidebar()
    {
        var query = new TextBox { PlaceholderText = W("PaletteWatermark") };
        var results = new StackPanel { Spacing = 4 };
        void Search()
        {
            results.Children.Clear();
            if (ViewModel.Project is not { } project) return;
            var text = query.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            foreach (var d in ProjectQuery.DeviceList(project)
                         .Where(d => (d.Name ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)
                             || d.Address.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)).Take(40))
                results.Children.Add(ActionButton($"{d.Address} · {d.Name}", () => OpenTarget(new("device", d.Id)), "quiet"));
            foreach (var g in ProjectQuery.GroupAddresses(project)
                         .Where(g => g.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                             || g.Address.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)).Take(40))
                results.Children.Add(ActionButton($"{g.Address} · {g.Name}", () => OpenTarget(new("ga", g.Id)), "quiet"));
        }
        query.TextChanged += (_, _) => Search();
        InspectorBody.Children.Add(query);
        InspectorBody.Children.Add(results);
        Search();
    }

    private static string WorkspaceDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BusChord", "workspaces");

    private void FillWorkspacesSidebar()
    {
        InspectorBody.Children.Add(ActionButton(W("SaveWorkspace"), () => _ = SaveNamedWorkspaceAsync(), "primary"));
        InspectorBody.Children.Add(ActionButton(W("RestoreWorkspace"), () => _ = LoadNamedWorkspaceAsync(), "quiet"));
        var files = Directory.Exists(WorkspaceDirectory)
            ? Directory.EnumerateFiles(WorkspaceDirectory, "*.json").OrderBy(f => f).ToList()
            : [];
        if (files.Count == 0) InspectorBody.Children.Add(Note(W("NoWorkspaces")));
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            InspectorBody.Children.Add(ActionButton(name, () =>
            {
                try { Workspace.TryRestoreLayout(file); RefreshWorkspace(); }
                catch (Exception ex) { ViewModel.Status = ex.Message; }
            }, "quiet"));
        }
    }

    private async Task SaveNamedWorkspaceAsync()
    {
        var name = await PromptTextAsync(W("WorkspaceName"), W("Save"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim();
        if (safe.Length == 0) return;
        Directory.CreateDirectory(WorkspaceDirectory);
        SaveLayout(Path.Combine(WorkspaceDirectory, safe + ".json"));
        FillWorkspacesSidebar();
        ViewModel.Status = $"{W("SidebarWorkspaces")}: {safe}";
    }

    private async Task LoadNamedWorkspaceAsync()
    {
        var name = await PromptTextAsync(W("WorkspaceName"), W("Load"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim();
        if (safe.Length == 0) return;
        var path = Path.Combine(WorkspaceDirectory, safe + ".json");
        if (!File.Exists(path)) { ViewModel.Status = W("NoWorkspaces"); return; }
        if (!Workspace.TryRestoreLayout(path)) { ViewModel.Status = W("NoWorkspaces"); return; }
        RefreshWorkspace();
    }

    private async Task<string?> PromptTextAsync(string title, string primary)
    {
        var dialog = CreateDialog(title, 420, 180);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        var box = new TextBox { PlaceholderText = title, Classes = { "wb-field" } };
        string? result = null;
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(primary, () => { result = box.Text; dialog.Close(); }, "primary", isDefault: true));
        panel.Children.Add(box);
        dialog.Content = DialogLayout(panel, actions);
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(this);
        return result;
    }

    private void FillOperationsSidebar()
    {
        if (_sessions is not { } sessions) { InspectorBody.Children.Add(Note(W("NoOperations"))); return; }
        InspectorBody.Children.Add(Note($"{W("Monitor")}: {(sessions.Monitor.IsRunning ? W("MonitorRunning", sessions.Monitor.RecordedCount, sessions.Monitor.FilteredRows.Count) : W("MonitorStopped", sessions.Monitor.RecordedCount))}"));
        InspectorBody.Children.Add(ActionButton(W("Monitor"), () => ShowTool("monitor"), "quiet"));
        InspectorBody.Children.Add(Note($"{W("ProgrammingTab")}: {(sessions.Programming.IsRunning ? W("ProgrammingRunning") : W("NoOperations"))}"));
        var conflicts = sessions.Interfaces.StatusSnapshot().Count(status => status.Blocked.Count > 0);
        InspectorBody.Children.Add(Note($"{W("InterfaceConflicts")}: {conflicts}"));
        InspectorBody.Children.Add(ActionButton(W("ProgrammingTab"), () => ShowTool("programming"), "quiet"));
        InspectorBody.Children.Add(ActionButton(W("Scan"), () => ShowTool("scan"), "quiet"));
    }

    private void OnNavSelected(object? sender, SelectionChangedEventArgs e)    {
        if (_refreshing || NavTree.SelectedItem is not NavEntry entry) return;
        Workspace.Navigators[Workspace.Perspective].SelectedId = entry.Node.Key;
        if (entry.Node.Target is { } target) OpenTarget(target);
    }
    private void OnNavSearch(object? sender, TextChangedEventArgs e)
    {
        if (_refreshing) return;
        CaptureNavigatorScroll();
        Workspace.Navigators[Workspace.Perspective].Query = NavSearch.Text ?? "";
        RefreshNavigation();
    }
    private void OnObjectSearch(object? sender, TextChangedEventArgs e)
    {
        if (_refreshing || Workspace.ActiveTab is not { } tab) return;
        tab.Filter = ObjectSearch.Text ?? ""; RefreshEditor();
    }
    private void OnUnlinked(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || Workspace.ActiveTab is not { } tab) return;
        tab.UnlinkedOnly = Unlinked.IsChecked == true; RefreshEditor();
    }
    private void OnRelated(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || Workspace.ActiveTab is not { } tab) return;
        tab.IncludeRelated = RelatedBox.IsChecked != false; RefreshEditor();
    }
    private void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _syncingSelection || Workspace.ActiveTab is not { } tab) return;
        tab.SelectedObjectIds.Clear();
        if (ObjectTable.SelectedItems is { } selected)
            foreach (var row in selected.OfType<WorkbenchRow>()) tab.SelectedObjectIds.Add(row.Key);
        if (CurrentDevice() is { } device) ViewModel.SelectedDevice = device;
        _sessions?.Monitor.SelectionChanged(SelectedComObjectId(), CurrentDevice()?.Device.Id);
        UpdateSelectionState();
        RefreshInspector();
    }
    private void OnExpandAll(object? sender, RoutedEventArgs e)
    {
        if (Workspace.ActiveTab is not { } tab || ViewModel.Project is not { } p) return;
        if (tab.CollapsedDeviceIds.Count > 0) tab.CollapsedDeviceIds.Clear();
        else foreach (var d in WorkbenchProjection.Devices(p, tab.Target)) tab.CollapsedDeviceIds.Add(d.Id);
        RefreshEditor();
    }
    private void CaptureScroll()
    {
        if (!_scrollRestorePending && TableViewport.IsVisible && _renderedTab is { } tab && ObjectTable.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            tab.ScrollOffset = scroll.Offset.Y;
    }
    private void CaptureNavigatorScroll()
    {
        if (!_navRestorePending && NavTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            Workspace.Navigators[_renderedPerspective].ScrollOffset = scroll.Offset.Y;
    }

    /// <summary>Section header plus its fields: the inspector stays a scannable property grid.</summary>
    private static Control Section(string title, params Control[] fields)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeight.SemiBold, Classes = { "wb-muted" } });
        foreach (var field in fields) stack.Children.Add(field);
        return stack;
    }

    /// <summary>One label and editor on a shared baseline; the label is the editor's accessible name.</summary>
    private static Control Field(string label, Control editor, string automationId)
    {
        var grid = new Grid { ColumnDefinitions = new("104,*"), ColumnSpacing = 8 };
        // Captions wrap inside the fixed column: "Room reference (optional)" must not be covered by the editor.
        var caption = Note(label); caption.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        grid.Children.Add(caption);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        AutomationProperties.SetAutomationId(editor, automationId);
        AutomationProperties.SetName(editor, label);
        return grid;
    }

    /// <summary>Registers an action for the inspector footer, which stays visible while sections scroll.</summary>
    private void FooterAction(Control action) => _inspectorFooters.Add(action);

    private void ToggleFavorite(WorkbenchTarget target, string name)
    {
        var pinned = Workspace.ToggleFavorite(target);
        ViewModel.Status = W(pinned ? "FavoriteAdded" : "FavoriteRemoved", name);
        RefreshWorkspace();
    }

    private Button FavoriteButton(WorkbenchTarget target, string name)
    {
        var pinned = Workspace.IsFavorite(target);
        var text = pinned ? W("Unfavorite") : W("Favorite");
        return ActionButton(text, () => ToggleFavorite(target, name), "quiet", text + ": " + name);
    }

    private void BuildDeviceInspector(DeviceRow current)
    {
        InspectorBody.Children.Add(Label(current.Name));
        var name = new TextBox { Text = current.Name };
        var address = new TextBox { Text = current.AddressText };
        var project = ViewModel.Project;
        var lines = project is null ? [] : project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).ToList();
        var rooms = project is null ? [] : project.Installations.SelectMany(i => i.Locations.AllNodes)
            .Where(n => n.Kind == SpaceKind.Room).ToList();
        var lineBox = new ComboBox { ItemsSource = lines };
        lineBox.ItemTemplate = new FuncDataTemplate<Line>((l, _) => Label(l is null ? "" : $"{l.Address} · {l.Name ?? W("Line")}"));
        var currentLine = project is null ? null : ProjectQuery.LocateDevice(project, current.Device.Address)?.Line;
        lineBox.SelectedItem = lines.FirstOrDefault(l => l.Id == currentLine?.Id) ?? lines.FirstOrDefault();
        var roomBox = new ComboBox { ItemsSource = rooms };
        roomBox.ItemTemplate = new FuncDataTemplate<SpaceNode>((r, _) => Label(r is null ? "" : r.Path));
        var currentRoom = project is null ? null : project.Installations.SelectMany(i => i.Locations.AllNodes)
            .FirstOrDefault(n => n.DeviceRefs.Contains(current.Device.Id));
        roomBox.SelectedItem = rooms.FirstOrDefault(r => r.Id == currentRoom?.Id);
        InspectorBody.Children.Add(Section(W("SectionGeneral"),
            Field(W("NameLabel"), name, "inspector.device.name"),
            Field(W("ColumnAddress"), address, "inspector.device.address")));
        InspectorBody.Children.Add(Section(W("SectionTopology"),
            Field(W("Line"), lineBox, "inspector.device.line"),
            Field(W("RoomOptional"), roomBox, "inspector.device.room"),
            Note(W("DropHint"))));
        var application = current.Device.Hardware2ProgramRefId
            ?? ViewModel.Project?.Workbench.Devices.GetValueOrDefault(current.Device.Id)?.Application
            ?? "-";
        InspectorBody.Children.Add(Section(W("SectionDevice"),
            Note(W("DeviceFacts", current.AddressText, current.Device.ProductRefId, application))));
        var target = new WorkbenchTarget("device", current.Device.Id);
        FooterAction(ActionButton(W("ApplyProperties"), () =>
        {
            var error = ViewModel.UpdateDeviceDetails(current.Device, name.Text ?? "",
                address.Text ?? "", (lineBox.SelectedItem as Line)?.Id, (roomBox.SelectedItem as SpaceNode)?.Id);
            if (error is not null) ViewModel.Status = error;
            RefreshWorkspace();
        }, "primary", W("ApplyProperties")));
        FooterAction(ActionButton(W("ShowInTopology"), () => { Navigate("topology"); NavSearch.Text = current.AddressText; }, "quiet"));
        FooterAction(ActionButton(W("OpenParameters"), () => OpenTarget(new("device", current.Device.Id), "params"), "quiet"));
        FooterAction(ActionButton(W("OpenBeside"), () => OpenCompanion(target), "quiet"));
        FooterAction(FavoriteButton(target, current.Name));
    }

    private void BuildProjectInspector(Project project)
    {
        var name = new TextBox { Text = project.Name };
        var comment = new TextBox { Text = project.Comment ?? "", AcceptsReturn = true, MinHeight = 80 };
        InspectorBody.Children.Add(Label(W("ProjectProperties")));
        InspectorBody.Children.Add(Section(W("SectionGeneral"),
            Field(W("NameLabel"), name, "inspector.project.name"),
            Field(W("Comment"), comment, "inspector.project.comment")));
        FooterAction(ActionButton(W("Apply"), () =>
        {
            ViewModel.Edit(W("ProjectProperties"), p => { p.Name = name.Text?.Trim() ?? p.Name; p.Comment = comment.Text; });
            RefreshWorkspace();
        }, "primary", W("Apply")));
    }

    private void BuildGroupAddressInspector(GroupAddressEntry ga)
    {
        var target = new WorkbenchTarget("ga", ga.Id);
        var name = new TextBox { Text = ga.Name };
        var dpt = new TextBox { Text = ga.DatapointType };
        InspectorBody.Children.Add(Label($"{ga.Address} · {ga.Name}"));
        InspectorBody.Children.Add(Section(W("SectionGeneral"),
            Field(W("NameLabel"), name, "inspector.ga.name"),
            Field(W("DatapointType"), dpt, "inspector.ga.dpt")));
        FooterAction(ActionButton(W("Apply"), () =>
        {
            ViewModel.RenameGroupAddress(new(ga.Address, ga.Name, ga.DatapointType, ga.Id), name.Text ?? "", dpt.Text); RefreshWorkspace();
        }, "primary", W("Apply")));
        if (ViewModel.Project is not { } gaProject) return;
        var linked = ProjectQuery.ComObjectRefs(gaProject).Where(r => r.Ref.Links.Contains(ga.Id)).ToList();
        var links = new StackPanel { Spacing = 4 };
        links.Children.Add(Note(W("GaLinkedCount", linked.Count)));
        if (linked.Count == 0) links.Children.Add(Note(W("GaNoLinks")));
        foreach (var group in linked.GroupBy(r => r.Device).Take(40))
        {
            links.Children.Add(ActionButton(W("GaLinkedDevice", group.Key.Address.ToString(),
                group.Key.Name ?? ""), () => OpenTarget(new("device", group.Key.Id), "relations"), "quiet"));
            foreach (var (device, comObject) in group.Take(10))
            {
                var sending = WorkbenchProject.Metadata(gaProject, new(device.Id, comObject.Id))?.SendingGroupId == ga.Id;
                links.Children.Add(Note($"#{comObject.Number} · {comObject.Text ?? comObject.Id}" + (sending ? " · " + W("SendingBadge") : "")));
            }
        }
        InspectorBody.Children.Add(Section(W("SectionLinks"), links));
        FooterAction(ActionButton(W("OpenBeside"), () => OpenCompanion(target), "quiet", W("OpenBeside") + ": " + ga.Name));
        FooterAction(FavoriteButton(target, ga.Name));
    }

    /// <summary>Object metadata stays compact but editable; links and their actions follow in one section.</summary>
    private void BuildObjectInspector(WorkbenchRow row, DeviceRow current, ComObjectInstanceRef obj)
    {
        if (ViewModel.Project is not { } project) return;
        InspectorBody.Children.Add(Label($"#{obj.Number} · {obj.Text ?? obj.Id}"));
        InspectorBody.Children.Add(Note(W("InspectorObject", current.AddressText, current.Name, obj.Id,
            row.Dpt ?? W("Unknown"), row.Bits is { } bits ? W("BitsValue", bits) : W("UnknownBits"),
            row.Flags ?? W("Unknown"))));
        var objectKey = new ObjectKey(current.Device.Id, obj.Id);
        var storedMetadata = WorkbenchProject.Metadata(project, objectKey) ?? new ObjectMetadata();
        var objectDpt = new TextBox { Text = storedMetadata.Dpt ?? "" };
        var objectBits = new TextBox { Text = storedMetadata.Bits?.ToString() ?? "" };
        var objectFlags = new TextBox { Text = storedMetadata.Flags ?? "" };
        var objectActive = new CheckBox { Content = W("Active"), IsChecked = storedMetadata.Active };
        AutomationProperties.SetName(objectActive, W("Active"));
        InspectorBody.Children.Add(Section(W("SectionObject"),
            Field(W("DatapointType"), objectDpt, "inspector.object.dpt"),
            Field(W("ColumnLength"), objectBits, "inspector.object.bits"),
            Field(W("ObjectFlags"), objectFlags, "inspector.object.flags"),
            objectActive));
        var sendingGroup = Knx.Workbench.WorkbenchProject.Metadata(project, new(current.Device.Id, obj.Id))?.SendingGroupId;
        var links = new StackPanel { Spacing = 4 };
        foreach (var id in obj.Links.ToArray())
        {
            var ga = ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id);
            var linkRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            linkRow.Children.Add(ga is null ? Note(W("Unresolved", id))
                : ActionButton($"{ga.Address} · {ga.Name}" + (sendingGroup == id ? W("SendingSuffix") : ""), () => OpenTarget(new("ga", ga.Id)), "quiet"));
            if (ga is not null)
            {
                linkRow.Children.Add(ActionButton(W("SetSendingAddress"), () =>
                {
                    string? error = null;
                    ViewModel.Edit(W("EditSendingAddress", ga.Address), p =>
                    {
                        var result = Knx.Workbench.WorkbenchProject.SetSending(p, new(current.Device.Id, obj.Id), ga.Id);
                        if (!result.Ok) error = result.Errors.FirstOrDefault()?.Reason;
                    });
                    if (error is not null) ViewModel.Status = error;
                    RefreshWorkspace();
                }, "quiet", W("SetSendingTip")));
            }
            linkRow.Children.Add(ActionButton(W("Remove"), () =>
            {
                string? warning = null;
                ViewModel.Edit(W("EditRemoveLink", id), p =>
                {
                    var result = Knx.Workbench.WorkbenchProject.RemoveLink(p, new(current.Device.Id, obj.Id), id);
                    warning = result.Warnings.FirstOrDefault();
                });
                if (warning is not null) ViewModel.Status = warning;
                RefreshWorkspace();
            }, "quiet"));
            links.Children.Add(linkRow);
        }
        if (obj.Links.Count == 0) links.Children.Add(Note(W("NoLinksDot")));
        links.Children.Add(Note(W("SendingNote")));
        InspectorBody.Children.Add(Section(W("SectionLinks"), links));
        FooterAction(ActionButton(W("Apply"), () =>
        {
            int? parsedBits = null;
            if (!string.IsNullOrWhiteSpace(objectBits.Text))
            {
                if (!int.TryParse(objectBits.Text, out var value) || value is < 1 or > 65535)
                {
                    ViewModel.Status = W("InvalidBits");
                    return;
                }
                parsedBits = value;
            }
            ViewModel.Edit(W("ApplyProperties"), p =>
            {
                var metadata = WorkbenchProject.Metadata(p, objectKey) ?? new ObjectMetadata();
                metadata.Dpt = string.IsNullOrWhiteSpace(objectDpt.Text) ? null : objectDpt.Text.Trim();
                metadata.Bits = parsedBits;
                metadata.Flags = string.IsNullOrWhiteSpace(objectFlags.Text) ? null : objectFlags.Text.Trim().ToUpperInvariant();
                metadata.Active = objectActive.IsChecked == true;
                p.Workbench.Objects[objectKey.ToString()] = metadata;
            });
            RefreshWorkspace();
        }, "primary", W("Apply")));
        FooterAction(ActionButton(W("MoreLinks"), () =>
        {
            if (Workspace.ActiveTab is { } tab)
            {
                tab.SelectedObjectIds.Clear(); tab.SelectedObjectIds.Add(WorkbenchProjection.ObjectKey(current.Device, obj));
            }
            ShowBatchLinkDialog();
        }));
    }

    /// <summary>Remembers the focused inspector field so a rebuild can restore it with its caret.</summary>
    private void RememberInspectorFocus()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        if (focused is null || !Inspector.GetVisualDescendants().Contains(focused)) return;
        var id = AutomationProperties.GetAutomationId(focused);
        if (string.IsNullOrEmpty(id)) return;
        _inspectorFocusId = id;
        _inspectorFocusCaret = focused is TextBox box ? box.CaretIndex : 0;
    }

    private void RestoreInspectorFocus()
    {
        if (_inspectorFocusId is not { Length: > 0 } id) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_inspectorFocusId != id) return;
            _inspectorFocusId = null;
            if (Inspector.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == id) is not { } target) return;
            target.Focus();
            if (target is TextBox box) box.CaretIndex = Math.Min(_inspectorFocusCaret, box.Text?.Length ?? 0);
        }, DispatcherPriority.Loaded);
    }

    private void RefreshInspector()
    {
        RememberInspectorFocus();
        InspectorBody.Children.Clear();
        InspectorFooter.Children.Clear();
        _inspectorFooters.Clear();
        InspectorTitle.Text = W("SidebarTitle");
        try
        {
            RefreshInspectorBody();
        }
        finally
        {
            foreach (var action in _inspectorFooters) InspectorFooter.Children.Add(action);
            InspectorFooterHost.IsVisible = _inspectorFooters.Count > 0;
            RestoreInspectorFocus();
        }
    }

    private void RefreshInspectorBody()
    {
        if (_sidebarMode != "properties")
        {
            switch (_sidebarMode)
            {
                case "groups": FillGroupSidebar(); break;
                case "search": FillSearchSidebar(); break;
                case "workspaces": FillWorkspacesSidebar(); break;
                case "operations": FillOperationsSidebar(); break;
            }
            return;
        }
        var tab = Workspace.ActiveTab;
        var selected = ViewModel.Project is { } p && tab is not null
            ? WorkbenchProjection.Rows(p, tab).Where(r => tab.SelectedObjectIds.Contains(r.Key)).ToList() : [];
        if (selected.Count > 1)
        {
            InspectorBody.Children.Add(Label(W("RowsSelected", selected.Count)));
            InspectorBody.Children.Add(Note(W("MultiSelectionNote")));
            return;
        }
        var current = CurrentDevice();
        if (selected.FirstOrDefault()?.Object is { } obj && current is not null)
            BuildObjectInspector(selected.First(), current, obj);
        else if (current is not null && !(selected.Count == 0 && tab?.Target.Kind == "ga"))
            BuildDeviceInspector(current);
        else if (tab?.Target.Kind == "project" && WorkbenchProjection.Resolve(ViewModel.Project, tab.Target) is Project project)
            BuildProjectInspector(project);
        else if (tab?.Target.Kind == "ga" && WorkbenchProjection.Resolve(ViewModel.Project, tab.Target) is GroupAddressEntry ga)
            BuildGroupAddressInspector(ga);
        else InspectorBody.Children.Add(Note(W("InspectorEmpty")));
    }


    public void ShowTool(string tool)
    {
        if (!WorkbenchTools.IsKnown(tool)) throw new ArgumentException("Unknown tool.", nameof(tool));
        EnsureSessions();
        var previous = Workspace.ActiveTool;
        Workspace.ActiveTool = tool;
        Workspace.ToolsVisible = true;
        // The full-page search pane is unusable in a 242 DIP dock: host it maximized instead.
        if (tool == "search")
        {
            if (!Workspace.ToolsMaximized) { Workspace.ToolsMaximized = true; _searchForcedMaximize = true; }
        }
        else if (previous == "search" && _searchForcedMaximize)
        {
            Workspace.ToolsMaximized = false;
            _searchForcedMaximize = false;
        }
        SyncToolTabs();
        ToolHost.Content = CreateToolView(tool);
        ApplyLayout();
    }

    private Control CreateToolView(string tool) => tool switch
    {
        "problems" => CreateProblemsView(),
        "programming" => CreateProgrammingView(),
        "scan" => CreateScanView(),
        "changes" => CreateChangesView(),
        "search" => CreateSearchView(),
        "monitor" => CreateMonitorView(),
        _ => Note(W("ToolPlaceholder"))
    };

    private string ToolWindowTitle(string tool) => tool switch
    {
        "problems" => W("ProblemsWindowTitle"),
        "programming" => W("ProgrammingWindowTitle"),
        "scan" => W("Scan"),
        "changes" => W("ChangesWindowTitle"),
        "search" => W("SidebarSearch"),
        _ => W("MonitorWindowTitle")
    };
    private Control CreateRelationsView()
    {
        if (ViewModel.Project is not { } project || Workspace.ActiveTab is not { } tab)
            return Note(W("RelationsNeedTarget"));
        var panel = new StackPanel { Spacing = 8, Margin = new(24, 12) };
        var scope = tab.Target.Kind switch
        {
            "room" => new WorkbenchScope(WorkbenchScopeKind.Room, tab.Target.Id, tab.IncludeRelated),
            "line" => new WorkbenchScope(WorkbenchScopeKind.Line, tab.Target.Id),
            "device" => new WorkbenchScope(WorkbenchScopeKind.Device, tab.Target.Id),
            "ga" => new WorkbenchScope(WorkbenchScopeKind.GroupAddress, tab.Target.Id),
            _ => new WorkbenchScope()
        };
        var rows = WorkbenchProject.ScopeObjects(project, scope with { IncludeInactive = true });
        if (rows.Count == 0) { panel.Children.Add(EmptyState("topology", W("Relations"), W("RelationsEmpty"))); return panel; }
        panel.Children.Add(Label(W("RelationsTitle")));
        panel.Children.Add(Note(W("RelationsNote")));
        panel.Children.Add(Note($"KO → GA [C,T / C,R] · KO ← GA [C,W/U] · {W("ObjectFlags")}"));
        var groupIds = tab.Target.Kind == "ga" ? new[] { tab.Target.Id }
            : rows.SelectMany(r => r.Object.Links).Distinct().ToArray();
        var all = WorkbenchProject.ScopeObjects(project, new(IncludeInactive: true));
        foreach (var id in groupIds)
        {
            var card = new Border
            {
                Background = null, BorderThickness = new(0, 0, 0, 1), Padding = new(0, 6),
                BorderBrush = Avalonia.Media.Brushes.Transparent
            };
            var stack = new StackPanel { Spacing = 4 };
            var ga = ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id);
            stack.Children.Add(ga is null ? Note(id) : ActionButton($"{ga.Address} · {ga.Name}", () => OpenTarget(new("ga", id)), "quiet"));
            foreach (var row in all.Where(r => r.Object.Links.Contains(id)))
            {
                var flags = row.Metadata?.Flags;
                var communicating = row.Metadata?.Active != false && flags?.Contains('C') == true;
                var sending = row.Metadata?.Connector(id).Send == true;
                var sends = communicating && (flags!.Contains('R') || (flags.Contains('T') && sending));
                var receives = communicating && (flags!.Contains('W') || flags.Contains('U'));
                var direction = sends && receives ? "↔" : sends ? "→" : receives ? "←" : "·";
                var line = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                line.Children.Add(ActionButton($"{row.Device.Address} · {row.Device.Name} · #{row.Object.Number} · {row.Object.Text}",
                    () => RevealComObject(row.Device, row.Object.Id), "quiet"));
                line.Children.Add(Note($"{direction} [{flags ?? "—"}]" + (sending ? W("SendingSuffix") : "")));
                line.Children.Add(ActionButton(W("Remove"), () =>
                {
                    string? warning = null;
                    ViewModel.Edit(W("EditRemoveLink", id), p =>
                        warning = WorkbenchProject.RemoveLink(p, new(row.Device.Id, row.Object.Id), id).Warnings.FirstOrDefault());
                    if (warning is not null) ViewModel.Status = warning;
                    RefreshWorkspace();
                }, "quiet"));
                stack.Children.Add(line);
                stack.Children.Add(ActionButton(W("EditLinksEllipsis"), () =>
                {
                    // A remote counterpart must become the editing target before opening its associations.
                    var active = OpenTarget(new("device", row.Device.Id));
                    active.SelectedObjectIds.Clear();
                    active.SelectedObjectIds.Add(WorkbenchProjection.ObjectKey(row.Device, row.Object));
                    ShowBatchLinkDialog();
                }, "quiet"));
            }
            card.Child = stack;
            panel.Children.Add(card);
        }
        foreach (var row in rows.Where(r => r.Object.Links.Count == 0))
        {
            panel.Children.Add(Label(W("RelationsRow", row.Object.Number, row.Object.Text ?? row.Object.Id, row.Metadata?.Dpt ?? W("Unknown"))));
            panel.Children.Add(Note(W("NoLinksDot")));
            panel.Children.Add(ActionButton(W("EditLinksEllipsis"), () =>
            {
                var active = OpenTarget(new("device", row.Device.Id));
                active.SelectedObjectIds.Clear(); active.SelectedObjectIds.Add(WorkbenchProjection.ObjectKey(row.Device, row.Object));
                ShowBatchLinkDialog();
            }, "quiet"));
        }
        return new ScrollViewer { Content = panel };
    }

    private Control CreateProblemsView(WorkbenchTarget? target = null)
    {
        ViewModel.RunCheck();
        IReadOnlyCollection<CheckFinding> findings = ViewModel.Project is { } project && target is not null && target.Kind != "project"
            ? ScopedFindings(project, target, ViewModel.Findings).ToList() : ViewModel.Findings;
        var panel = new DockPanel();
        if (findings.Count > 0)
        {
            var summary = Note(W("CheckFindings", findings.Count));
            summary.Margin = new(12, 6);
            DockPanel.SetDock(summary, Avalonia.Controls.Dock.Top);
            panel.Children.Add(summary);
        }
        var list = new ListBox { ItemsSource = findings, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<CheckFinding>((f, _) => Note(f is null ? "" : $"{f.Severity} · {f.Code} · {f.Path}\n{f.Message}"));
        if (findings.Count == 0)
            panel.Children.Add(EmptyState("warning", W("EmptyCheckClean"), W("CheckClean")));
        else
            panel.Children.Add(list);
        return panel;
    }

    private static IEnumerable<CheckFinding> ScopedFindings(Project project, WorkbenchTarget target, IEnumerable<CheckFinding> findings)
    {
        var devices = WorkbenchProjection.Devices(project, target);
        var paths = devices.Select(d => d.Address.ToString()).ToHashSet(StringComparer.Ordinal);
        var ids = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var objects = WorkbenchProject.ScopeObjects(project, new(IncludeInactive: true))
            .Where(r => target.Kind == "ga"
                ? r.Object.Links.Contains(target.Id) || r.Metadata?.SendingGroupId == target.Id
                    || r.Metadata?.Connectors.ContainsKey(target.Id) == true
                : ids.Contains(r.Device.Id)).ToList();
        foreach (var row in objects) paths.Add(row.Key.ToString());
        var groupIds = target.Kind == "ga" ? new HashSet<string> { target.Id }
            : objects.SelectMany(r => r.Object.Links).ToHashSet();
        foreach (var group in ProjectQuery.GroupAddresses(project).Where(g => groupIds.Contains(g.Id)))
        {
            paths.Add(group.Address.ToString()); paths.Add(group.Id); paths.Add($"Gruppenadresse {group.Id}");
        }
        foreach (var id in ids) { paths.Add(id); paths.Add($"Gerät {id}"); }
        if (WorkbenchProjection.Resolve(project, target) is SpaceNode room)
            foreach (var node in room.Descendants.Append(room))
            {
                paths.Add($"Gebäudeknoten {node.Id}");
                foreach (var id in node.DeviceRefs) paths.Add($"{node.Path} [{id}]");
            }
        if (target.Kind == "line")
            foreach (var area in project.Installations.SelectMany(i => i.Areas))
                foreach (var line in area.Lines.Where(l => l.Id == target.Id))
                {
                    paths.Add($"Linie {area.Address}.{line.Address}"); paths.Add($"Linie {line.Id}");
                    foreach (var segment in line.Segments) paths.Add($"Segment {area.Address}.{line.Address}.{segment.Address}");
                }
        // ProjectCheck exposes paths, not object references; compare complete paths, never name substrings.
        return findings.Where(f => paths.Contains(f.Path) || objects.Any(r =>
            f.Path.StartsWith($"{r.Key} → ", StringComparison.Ordinal)
            && (target.Kind != "ga" || f.Path == $"{r.Key} → {target.Id}")));
    }

    private static void ObserveToolState(Control view, INotifyPropertyChanged source, Action refresh)
    {
        var attached = false;
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess()) { if (attached) refresh(); }
            else Dispatcher.UIThread.Post(() => { if (attached) refresh(); });
        }
        view.AttachedToVisualTree += (_, _) =>
        {
            if (attached) return;
            attached = true; source.PropertyChanged += Changed;
            Dispatcher.UIThread.Post(() => { if (attached) refresh(); });
        };
        view.DetachedFromVisualTree += (_, _) => { attached = false; source.PropertyChanged -= Changed; };
    }
    private SearchPane CreateSearchView()
    {
        var pane = new SearchPane(ViewModel) { DataContext = ViewModel };
        pane.NavigateRequested += OpenSearchHit;
        return pane;
    }

    internal void OpenSearchHit(SearchHit hit)
    {
        if (ViewModel.Project is not { } project) return;
        switch (hit.Kind)
        {
            case ProjectSearch.DeviceKind:
                OpenTarget(new("device", hit.Id));
                break;
            case ProjectSearch.GroupAddressKind:
                OpenTarget(new("ga", hit.Id));
                break;
            case ProjectSearch.CommunicationObjectKind:
                if (ProjectQuery.ComObjectRefs(project).FirstOrDefault(r => r.Ref.Id == hit.Id).Device is { } device)
                    RevealComObject(device, hit.Id);
                break;
            case ProjectSearch.BuildingKind:
                if (project.Installations.SelectMany(i => i.Locations.AllNodes).FirstOrDefault(n => n.Id == hit.Id) is { Kind: SpaceKind.Room } room)
                    OpenTarget(new("room", room.Id));
                else
                {
                    Navigate("building");
                    NavSearch.Text = hit.Path;
                }
                break;
            default:
                Navigate("topology");
                NavSearch.Text = hit.Path;
                break;
        }
    }

    private Control CreateMonitorView()
    {
        EnsureSessions();
        var panel = new DockPanel();
        if (_sessions is not { } sessions)
            return Note(W("MonitorNeedsProject"));
        var toolbar = new WrapPanel { Margin = new(12, 6) };
        var live = new TextBlock { FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new(12, 0) };
        var filter = new ComboBox { MinWidth = 190, SelectedIndex = 0 };
        filter.ItemsSource = new[] { W("AllTelegrams"), W("WholeProject"), W("CurrentWorkItem"), W("FollowSelection") };
        var pinned = Note("");
        var syncingFilter = false;
        void RefreshState()
        {
            var running = sessions.Monitor.IsRunning;
            live.Text = running
                ? W("MonitorRunning", sessions.Monitor.RecordedCount, sessions.Monitor.FilteredRows.Count)
                : W("MonitorStopped", sessions.Monitor.RecordedCount);
            live.Classes.Set("wb-success", running);
            live.Classes.Set("wb-subtle", !running);
            syncingFilter = true;
            filter.SelectedIndex = sessions.Monitor.FollowSelection ? 3 : sessions.Monitor.Filter.Scope switch
            {
                MonitorScope.All => 0, MonitorScope.Project => 1, _ => 2
            };
            syncingFilter = false;
            var current = sessions.Monitor.Filter;
            var target = current.Scope switch
            {
                MonitorScope.GroupAddress => new WorkbenchTarget("ga", current.Id ?? ""),
                MonitorScope.Room => new WorkbenchTarget("room", current.Id ?? ""),
                MonitorScope.Device => new WorkbenchTarget("device", current.Id ?? ""),
                _ => null
            };
            var model = target is null ? null : WorkbenchProjection.Resolve(ViewModel.Project, target);
            pinned.Text = model switch
            {
                GroupAddressEntry ga => $"{ga.Address} · {ga.Name}",
                DeviceInstance d => $"{d.Address} · {d.Name}",
                SpaceNode room => room.Path,
                _ => current.Scope is MonitorScope.All or MonitorScope.Project ? "" : $"{current.Scope} · {current.Id ?? "—"}"
            };
        }
        toolbar.Children.Add(ActionButton(W("Stop"), () => { sessions.Monitor.Stop(); RefreshState(); }));
        toolbar.Children.Add(ActionButton(W("Pause"), () => { sessions.Monitor.Pause(); RefreshState(); }, "quiet"));
        toolbar.Children.Add(ActionButton(W("Resume"), () => { sessions.Monitor.Resume(); RefreshState(); }, "quiet"));
        toolbar.Children.Add(ActionButton(W("ClearDisplay"), () => { sessions.Monitor.ClearDisplay(); RefreshState(); }, "quiet"));
        toolbar.Children.Add(ActionButton(W("MonitorValue"), ShowMonitorValueDialog, "quiet"));
        var mode = new ComboBox { ItemsSource = new[] { W("GroupMonitor"), W("BusMonitor") }, SelectedIndex = sessions.Monitor.Mode == WorkbenchMonitorMode.Bus ? 1 : 0, MinWidth = 130 };
        AutomationProperties.SetName(mode, W("MonitorMode"));
        mode.SelectionChanged += (_, _) =>
        {
            if (sessions.Monitor.IsRunning) { mode.SelectedIndex = sessions.Monitor.Mode == WorkbenchMonitorMode.Bus ? 1 : 0; ViewModel.Status = W("MonitorModeLocked"); return; }
            sessions.Monitor.Configure(mode.SelectedIndex == 1 ? WorkbenchMonitorMode.Bus : WorkbenchMonitorMode.Group, SessionOrigin.Demo, "demo-ip");
        };
        toolbar.Children.Add(mode);
        var start = ActionButton(W("Start"), () =>
        {
            try
            {
                if (!sessions.Monitor.IsRunning)
                {
                    var desired = mode.SelectedIndex == 1 ? WorkbenchMonitorMode.Bus : WorkbenchMonitorMode.Group;
                    if (sessions.Monitor.Mode != desired || sessions.Monitor.Origin != SessionOrigin.Demo)
                        sessions.Monitor.Configure(desired, SessionOrigin.Demo, "demo-ip");
                    sessions.Monitor.Start();
                }
                RefreshState();
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "primary");
        toolbar.Children.Insert(0, start);
        AutomationProperties.SetName(filter, W("MonitorFilter"));
        RefreshState();
        filter.SelectionChanged += (_, _) =>
        {
            if (syncingFilter) return;
            try
            {
                switch (filter.SelectedIndex)
                {
                    case 1: sessions.Monitor.PinFilter(new(MonitorScope.Project)); break;
                    case 2 when Workspace.ActiveTab is { } tab:
                        sessions.Monitor.PinFilter(tab.Target.Kind switch
                        {
                            "room" => new(MonitorScope.Room, tab.Target.Id),
                            "ga" => new(MonitorScope.GroupAddress, tab.Target.Id),
                            "device" => new(MonitorScope.Device, tab.Target.Id),
                            _ => new(MonitorScope.Project)
                        });
                        break;
                    case 3: sessions.Monitor.SetFollowSelection(true, SelectedComObjectId(), CurrentDevice()?.Device.Id); break;
                    default: sessions.Monitor.PinFilter(new(MonitorScope.All)); break;
                }
                RefreshState();
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        };
        toolbar.Children.Add(filter);
        toolbar.Children.Add(pinned);
        var autoScroll = new CheckBox { Content = W("AutoScroll"), IsChecked = sessions.Monitor.AutoScroll, FontSize = 11 };
        autoScroll.IsCheckedChanged += (_, _) => sessions.Monitor.AutoScroll = autoScroll.IsChecked == true;
        toolbar.Children.Add(autoScroll);
        toolbar.Children.Add(ActionButton(W("ExportFilteredCsv"), () => _ = GuardAsync(ExportFilteredMonitorAsync)));
        toolbar.Children.Add(ActionButton(W("ExportFullCsv"), () => _ = GuardAsync(ExportFullMonitorAsync), "quiet"));
        toolbar.Children.Add(live);
        var demoBadge = Label(W("MonitorDemoNote")); demoBadge.FontSize = 10;
        demoBadge.Classes.Add("wb-warning");
        toolbar.Children.Add(demoBadge);
        CommitToolbar(panel, toolbar);
        var header = new Grid { ColumnDefinitions = new("70,80,120,90,50,70,*"), Height = 28 };
        header.Classes.Add("wb-table-header");
        var headings = new[] { W("ColumnTime"), W("ColumnSource"), W("ColumnDestination"), W("ColumnService"), "DPT", W("ColumnValue"), W("ColumnOrigin") };
        for (var i = 0; i < headings.Length; i++)
        {
            var text = Label(headings[i]); text.Classes.Add("wb-th"); text.Margin = new(6, 0, 0, 0);
            Grid.SetColumn(text, i); header.Children.Add(text);
        }
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top); panel.Children.Add(header);
        var tablePanel = new Grid();
        var empty = EmptyState("monitor", W("EmptyNoRecording"), W("MonitorEmpty"));
        var list = new ListBox { ItemsSource = sessions.Monitor.FilteredRows, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<WorkbenchTelegram>((row, _) =>
        {
            if (row is null) return new Border();
            var grid = new Grid { ColumnDefinitions = new("70,80,120,90,50,70,*"), Height = 28 };
            var cells = new[] { row.Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), row.Source, row.Destination, row.Service, row.DatapointType ?? "—", row.Value, row.Origin.ToString() };
            for (var i = 0; i < cells.Length; i++)
            {
                Control cell = Label(cells[i]);
                if (ViewModel.Project is { } p)
                {
                    if (i == 1 && ProjectQuery.DeviceList(p).FirstOrDefault(d => d.Address.ToString() == row.Source) is { } source)
                        cell = ActionButton(row.Source, () => OpenTarget(new("device", source.Id)), "quiet");
                    if (i == 2 && row.IsGroup && ProjectQuery.GroupAddresses(p).FirstOrDefault(g =>
                            g.Id == row.GroupAddressId || g.Address.ToString() == row.Destination) is { } destination)
                        cell = ActionButton(row.Destination, () => OpenTarget(new("ga", destination.Id)), "quiet");
                    if (i == 2 && !row.IsGroup && ProjectQuery.DeviceList(p).FirstOrDefault(d => d.Address.ToString() == row.Destination) is { } targetDevice)
                        cell = ActionButton(row.Destination, () => OpenTarget(new("device", targetDevice.Id)), "quiet");
                }
                if (cell is TextBlock text) text.FontSize = 10;
                else if (cell is Button button) button.FontSize = 10;
                cell.Margin = new(6, 0, 0, 0);
                if (i is 0 or 4 or 5 or 6) cell.Classes.Add("wb-muted");
                Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
            return grid;
        });
        tablePanel.Children.Add(list);
        tablePanel.Children.Add(empty);
        empty.IsVisible = sessions.Monitor.FilteredRows.Count == 0;
        // A short dock spends its body on the empty state; the column headings are useless
        // until the first row exists, so reclaim their 28 DIP.
        header.IsVisible = sessions.Monitor.FilteredRows.Count > 0;
        if (sessions.Monitor.FilteredRows is System.Collections.Specialized.INotifyCollectionChanged changed)
        {
            void OnChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
            {
                empty.IsVisible = sessions.Monitor.FilteredRows.Count == 0;
                header.IsVisible = sessions.Monitor.FilteredRows.Count > 0;
                if (sessions.Monitor.AutoScroll && sessions.Monitor.FilteredRows.Count > 0)
                    list.ScrollIntoView(sessions.Monitor.FilteredRows[^1]);
            }
            list.AttachedToVisualTree += (_, _) => changed.CollectionChanged += OnChanged;
            list.DetachedFromVisualTree += (_, _) => changed.CollectionChanged -= OnChanged;
        }
        panel.Children.Add(tablePanel);
        RefreshState();
        ObserveToolState(panel, sessions.Monitor, RefreshState);
        return panel;
    }

    private Control CreateProgrammingView()
    {
        EnsureSessions();
        if (_sessions is not { } sessions) return Note(W("ProgrammingNeedsProject"));
        var panel = new DockPanel();
        ProgrammingJob? selectedJob = null;
        var toolbar = new WrapPanel { Margin = new(12, 6) };
        var devices = ViewModel.Project is { } project ? ViewModel.Devices.ToList() : [];
        var device = new ComboBox { ItemsSource = devices, MinWidth = 250, SelectedIndex = devices.Count > 0 ? 0 : -1 };
        device.ItemTemplate = new FuncDataTemplate<DeviceRow>((d, _) => Label(d is null ? "" : $"{d.AddressText} · {d.Name}"));
        toolbar.Children.Add(device);
        toolbar.Children.Add(ActionButton(W("EnqueueJob"), () =>
        {
            if (device.SelectedItem is not DeviceRow row) { ViewModel.Status = W("SelectDevice"); return; }
            try
            {
                sessions.Programming.Enqueue(row.Device.Id, "full", "demo-ip");
                ViewModel.Status = W("JobQueued", row.Name);
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "primary"));
        toolbar.Children.Add(ActionButton(W("Run"), () => _ = GuardAsync(async () =>
        {
            try { await sessions.Programming.RunAsync(); }
            catch (Exception ex) { ViewModel.Status = W("ProgrammingFailed", ex.Message); }
        })));
        toolbar.Children.Add(ActionButton(W("RetryFailed"), () =>
        {
            try
            {
                foreach (var job in sessions.Programming.Jobs.Where(j => j.Status == ProgrammingJobStatus.Failed).ToArray())
                    sessions.Programming.Retry(job);
                ViewModel.Status = W("JobsRequeued");
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "quiet"));
        toolbar.Children.Add(ActionButton(W("CancelJob"), () =>
        {
            if (selectedJob is null) { ViewModel.Status = W("NoJobSelected"); return; }
            try { sessions.Programming.CancelJob(selectedJob); }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "quiet"));
        toolbar.Children.Add(ActionButton(W("CancelQueued"), () =>
        {
            try { ViewModel.Status = W("JobsCancelled", sessions.Programming.CancelQueued()); }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "quiet"));
        toolbar.Children.Add(ActionButton(W("ClearHistory"), () =>
        {
            try { sessions.Programming.ClearHistory(); }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "quiet"));
        var fail = new CheckBox { Content = W("SimulateFailure"), IsChecked = _simulateProgrammingFailure, FontSize = 11, Margin = new(8, 0) };
        fail.IsCheckedChanged += (_, _) => _simulateProgrammingFailure = fail.IsChecked == true;
        toolbar.Children.Add(fail);
        toolbar.Children.Add(Note(W("ProgrammingDemoNote")));
        CommitToolbar(panel, toolbar);
        var needNote = Note("");
        var projectIdentity = ViewModel.ProjectSessionIdentity;
        void RefreshNeed()
        {
            // Replacement notifications can arrive before the shell has closed old floated views.
            if (!ReferenceEquals(projectIdentity, ViewModel.ProjectSessionIdentity)) return;
            var current = device.SelectedItem as DeviceRow;
            var choices = ViewModel.Devices.ToList();
            if (device.ItemsSource is not IEnumerable<DeviceRow> shown || !shown.SequenceEqual(choices))
            {
                device.ItemsSource = choices;
                device.SelectedItem = choices.FirstOrDefault(d => d.Device.Id == current?.Device.Id) ?? choices.FirstOrDefault();
            }
            needNote.Text = W("ProgrammingNeed", choices.Count(d => sessions.Programming.NeedsProgramming(d.Device.Id)));
        }
        RefreshNeed();
        ObserveToolState(panel, sessions.Programming, RefreshNeed);
        ObserveToolState(panel, ViewModel, RefreshNeed);
        void DownloadedChanged(object? sender, EventArgs e) => RefreshNeed();
        panel.AttachedToVisualTree += (_, _) => sessions.Programming.DownloadedChanged += DownloadedChanged;
        panel.DetachedFromVisualTree += (_, _) => sessions.Programming.DownloadedChanged -= DownloadedChanged;
        DockPanel.SetDock(needNote, Avalonia.Controls.Dock.Top); panel.Children.Add(needNote);
        var header = new Grid { ColumnDefinitions = new("110,220,90,90,60,*"), Height = 28 };
        header.Classes.Add("wb-table-header");
        var headings = new[] { W("ColumnAddress"), W("ColumnDevice"), "Status", W("ColumnJob"), W("Progress"), W("ColumnHint") };
        for (var i = 0; i < headings.Length; i++)
        {
            var text = Label(headings[i]); text.Classes.Add("wb-th"); text.Margin = new(6, 0, 0, 0);
            Grid.SetColumn(text, i); header.Children.Add(text);
        }
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top); panel.Children.Add(header);
        var list = new ListBox { ItemsSource = sessions.Programming.Jobs, Background = Brushes.Transparent };
        list.SelectionChanged += (_, _) => selectedJob = list.SelectedItem as ProgrammingJob;
        list.ItemTemplate = new FuncDataTemplate<ProgrammingJob>((job, _) =>
        {
            if (job is null) return new Border();
            var grid = new Grid { ColumnDefinitions = new("110,220,90,90,60,*"), Height = 28 };
            var cells = new[] { job.Address, job.Name, job.Status.ToString().ToUpperInvariant(), job.Operation, $"{job.Progress} %", job.Error ?? "" };
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = Label(cells[i]); cell.FontSize = 10; cell.Margin = new(6, 0, 0, 0); Grid.SetColumn(cell, i); grid.Children.Add(cell);
                if (i is 2 or 4 or 5)
                    cell.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(i == 2 ? nameof(job.Status) : i == 4 ? nameof(job.Progress) : nameof(job.Error))
                    {
                        Source = job, StringFormat = i == 4 ? "{0} %" : null,
                        Converter = i == 2 ? new Avalonia.Data.Converters.FuncValueConverter<ProgrammingJobStatus, string>(s => s.ToString().ToUpperInvariant()) : null
                    });
            }
            return grid;
        });
        var empty = EmptyState("download", W("EmptyNoJobs"), W("ProgrammingEmpty"));
        empty.IsVisible = sessions.Programming.Jobs.Count == 0;
        // The list is parented once, into the overlay host; a DockPanel cannot re-parent it.
        panel.Children.Remove(list);
        var host = new Grid();
        host.Children.Add(list);
        host.Children.Add(empty);
        panel.Children.Add(host);
        if (sessions.Programming.Jobs is System.Collections.Specialized.INotifyCollectionChanged jobsChanged)
        {
            void OnJobsChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
                => empty.IsVisible = sessions.Programming.Jobs.Count == 0;
            host.AttachedToVisualTree += (_, _) => jobsChanged.CollectionChanged += OnJobsChanged;
            host.DetachedFromVisualTree += (_, _) => jobsChanged.CollectionChanged -= OnJobsChanged;
        }
        return panel;
    }

    private Control CreateChangesView()
    {
        var panel = new DockPanel { Margin = new(12, 6) };
        var clear = ActionButton(W("ClearDisplay"), () => { _audit.Clear(); ShowTool("changes"); }, "quiet");
        DockPanel.SetDock(clear, Avalonia.Controls.Dock.Top);
        panel.Children.Add(clear);
        var list = new ListBox { ItemsSource = _audit.Entries, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<WorkbenchChange>((change, _) => change is null ? new Border()
            : Label($"{change.At:HH:mm:ss} · {change.Description}"));
        if (_audit.Count == 0) panel.Children.Add(EmptyState("clock", W("EmptyNoChanges"), W("ChangesEmpty")));
        else panel.Children.Add(list);
        return panel;
    }

    private Control CreateScanView()
    {
        EnsureSessions();
        if (_sessions is not { } sessions) return Note(W("ScanNeedsProject"));
        var panel = new DockPanel { Margin = new(12, 6) };
        var toolbar = new WrapPanel { Margin = new(0, 0, 0, 6) };
        var lines = ViewModel.Project is { } project
            ? project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).ToList()
            : [];
        var picker = new ComboBox { ItemsSource = lines, MinWidth = 240, SelectedIndex = lines.Count > 0 ? 0 : -1 };
        picker.ItemTemplate = new FuncDataTemplate<Line>((line, _) => Label(line is null ? ""
            : $"{line.Address} · {line.Name ?? W("Line")}"));
        toolbar.Children.Add(picker);
        var run = ActionButton(W("ScanStart"), () => _ = GuardAsync(async () =>
        {
            if (picker.SelectedItem is not Line line) { ViewModel.Status = W("ScanNeedsProject"); return; }
            try
            {
                ViewModel.Status = W("ScanRunning");
                await sessions.LineScan.RunDemoAsync(line.Id);
                ViewModel.Status = sessions.LineScan.LastError ?? W("ScanNote");
                ToolHost.Content = CreateScanView();
            }
            catch (Exception ex) { ViewModel.Status = W("ScanError", ex.Message); }
        }), "primary");
        toolbar.Children.Add(run);
        toolbar.Children.Add(Note(W("ScanNote")));
        CommitToolbar(panel, toolbar);
        var header = new Grid { ColumnDefinitions = new("100,*,100,*"), Height = 28 };
        header.Classes.Add("wb-table-header");
        var headings = new[] { W("ColumnAddress"), W("ColumnDevice"), W("ColumnStatus"), W("ColumnDetail") };
        for (var i = 0; i < headings.Length; i++)
        {
            var text = Label(headings[i]); text.Classes.Add("wb-th"); text.Margin = new(6, 0, 0, 0);
            Grid.SetColumn(text, i); header.Children.Add(text);
        }
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top); panel.Children.Add(header);
        var results = new ListBox { ItemsSource = sessions.LineScan.Results, Background = Brushes.Transparent };
        results.ItemTemplate = new FuncDataTemplate<LineScanResult>((result, _) =>
        {
            if (result is null) return new Border();
            var grid = new Grid { ColumnDefinitions = new("100,*,100,*"), Height = 28 };
            var cells = new[] { result.Address, result.Name, result.Status, result.Detail };
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = Label(cells[i]); cell.FontSize = 10; cell.Margin = new(6, 0, 0, 0);
                Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
            return grid;
        });
        if (sessions.LineScan.Results.Count == 0) panel.Children.Add(EmptyState("refresh", W("EmptyNoScan"), W("ScanEmpty")));
        else panel.Children.Add(results);
        return panel;
    }

    private void ShowMonitorValueDialog()
    {
        if (_sessions is not { } sessions || ViewModel.Project is not { } project)
        { ViewModel.Status = W("ScanNeedsProject"); return; }
        var dialog = CreateDialog(W("ValueTitle"), 480, 320);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        var groups = ProjectQuery.GroupAddresses(project).OrderBy(g => g.Address.Raw).ToList();
        var picker = new ComboBox { ItemsSource = groups, SelectedIndex = groups.Count > 0 ? 0 : -1, Classes = { "wb-field" } };
        picker.ItemTemplate = new FuncDataTemplate<GroupAddressEntry>((g, _) => Label(g is null ? ""
            : $"{g.Address} · {g.Name} · {g.DatapointType ?? "—"}"));
        var value = new TextBox { Text = "1", PlaceholderText = W("ValueText"), Classes = { "wb-field" } };
        panel.Children.Add(Label(W("ValueGroupAddress"))); panel.Children.Add(picker);
        panel.Children.Add(Label(W("ValueText"))); panel.Children.Add(value);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "wb-error" } };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(W("SimulateRead"), () =>
            {
                if (picker.SelectedItem is not GroupAddressEntry ga) { error.Text = W("ChooseGroupAddress"); return; }
                try { sessions.Monitor.SimulateRead(ga.Id, value.Text ?? ""); ViewModel.Status = W("ValueWritten", ga.Address.ToString()); dialog.Close(); }
                catch (Exception ex) { error.Text = ex.Message; }
            }),
            ActionButton(W("SimulateWrite"), () =>
            {
                if (picker.SelectedItem is not GroupAddressEntry ga) { error.Text = W("ChooseGroupAddress"); return; }
                try { sessions.Monitor.SimulateWrite(ga.Id, value.Text ?? ""); ViewModel.Status = W("ValueWritten", ga.Address.ToString()); dialog.Close(); }
                catch (Exception ex) { error.Text = ex.Message; }
            }, "primary"));
        panel.Children.Add(Note(W("MonitorDemoNote")));
        dialog.Content = DialogLayout(panel, actions, error);
        dialog.Opened += (_, _) => picker.Focus();
        dialog.ShowDialog(this);
    }

    private string? SelectedComObjectId()
    {
        if (ViewModel.Project is not { } project || Workspace.ActiveTab is not { } tab) return null;
        var row = WorkbenchProjection.Rows(project, tab)
            .FirstOrDefault(r => r.Object is not null && tab.SelectedObjectIds.Contains(r.Key));
        return row?.Object?.Id;
    }

    private void UpdateModePreview()
    {
        _modePanel.Children.Clear();
        _modeActions.Children.Clear();
        _modeStatus.Text = "";
        _modeFooter.IsVisible = false;
        _modeScroll.IsVisible = false;
        _parameters.IsVisible = true;
        if (_parameterDevice.SelectedItem is not DeviceRow selected || ViewModel.Project is not { } project) return;
        var demo = project.Workbench.Devices.GetValueOrDefault(selected.Device.Id)?.Demo == true
            && selected.Device.ParameterValues.Count > 0;
        if (!demo) return;
        _parameters.IsVisible = false;
        _modeScroll.IsVisible = true;
        _modeFooter.IsVisible = true;
        _modePanel.Children.Add(Label(W("DemoApplicationNote")));
        var editors = new Dictionary<string, Control>(StringComparer.Ordinal);
        var errors = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        var draft = _demoDrafts.For(selected.Device);
        foreach (var (key, raw) in draft
                     .OrderBy(p => WorkbenchDemoForm.SortOrder(p.Key))
                     .ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            Control editor;
            if (key == "mode")
            {
                var box = new ComboBox { ItemsSource = new[] { W("SwitchMode"), W("SwitchDimMode") } };
                box.SelectedIndex = WorkbenchDemoForm.Unquote(raw) == "dim" ? 1 : 0;
                editor = box;
            }
            else if (key == "led")
            {
                var box = new ComboBox { ItemsSource = new[] { W("ViaFeedbackObject"), W("Off"), W("On") } };
                box.SelectedIndex = WorkbenchDemoForm.Unquote(raw) switch { "off" => 1, "on" => 2, _ => 0 };
                editor = box;
            }
            else if (key == "interface")
                editor = new TextBox { Text = WorkbenchDemoForm.Unquote(raw) };
            else
                editor = new TextBox { Text = raw };
            var error = new TextBlock
            {
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 0),
                IsVisible = false, Classes = { "wb-error" }
            };
            editors[key] = editor;
            if (editor is TextBox textEditor)
                textEditor.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        draft[key] = WorkbenchDemoForm.Serialize(key, textEditor.Text ?? "");
                };
            if (editor is ComboBox choice)
                choice.SelectionChanged += (_, _) => draft[key] = WorkbenchDemoForm.Serialize(key,
                    key == "mode" ? choice.SelectedIndex == 1 ? "dim" : "switch"
                        : choice.SelectedIndex switch { 1 => "off", 2 => "on", _ => "feedback" });
            errors[key] = error;
            _modePanel.Children.Add(DemoField(WorkbenchDemoForm.Label(key), editor, error));
        }
        var preview = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-muted" } };
        string Mode() => editors.GetValueOrDefault("mode") is ComboBox { SelectedIndex: 1 } ? "dim" : "switch";
        void Review()
        {
            var result = WorkbenchProject.PreviewMode(project, selected.Device.Id, Mode());
            preview.Text = !result.Supported ? result.Reason
                : result.Effects.Count == 0 ? W("ModeNoChange")
                : W("ModePreviewHeader") + string.Join("\n", result.Effects.Select(e =>
                {
                    var links = e.RetainedGroupIds.Select(id => ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id))
                        .Where(g => g is not null).Select(g => g!.Address.ToString());
                    var ga = string.Join(", ", links);
                    return $"{e.Key.ObjectRefId}: {(e.Active ? W("ActiveState") : W("InactiveState"))}{(ga.Length > 0 ? W("LinksRetained", ga) : "")}";
                }));
        }
        _modePanel.Children.Add(preview);
        var applyButton = ActionButton(W("Apply"), () =>
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, control) in editors)
            {
                var value = control switch
                {
                    ComboBox { SelectedIndex: 1 } when key == "mode" => "dim",
                    ComboBox { SelectedIndex: 1 } when key == "led" => "off",
                    ComboBox { SelectedIndex: 2 } when key == "led" => "on",
                    ComboBox => key == "mode" ? "switch" : "feedback",
                    TextBox { Text: { } text } => text,
                    _ => ""
                };
                values[key] = WorkbenchDemoForm.Serialize(key, value);
            }
            var mode = Mode();
            _demoDrafts.Discard(selected.Device.Id);
            ViewModel.Edit(W("EditDemoParameters", selected.Name), p =>
            {
                var device = ProjectQuery.DeviceList(p).FirstOrDefault(d => d.Id == selected.Device.Id);
                if (device is null) return;
                foreach (var (key, value) in values) device.ParameterValues[key] = value;
                WorkbenchProject.ApplyMode(p, selected.Device.Id, mode);
            });
            ViewModel.Status = W("DemoParametersApplied");
            RefreshWorkspace();
        }, "primary");
        // Same feedback contract as ParameterPane: per-field error text, footer summary, blocked Apply.
        void Validate()
        {
            var invalid = 0;
            foreach (var (key, editor) in editors)
            {
                var error = WorkbenchDemoForm.Validate(key, editor is TextBox box ? box.Text ?? "" : "");
                errors[key].Text = error ?? "";
                errors[key].IsVisible = error is not null;
                if (error is not null) invalid++;
            }
            applyButton.IsEnabled = invalid == 0;
            _modeStatus.Text = invalid == 0 ? "" : W("DemoValidationSummary", invalid);
        }
        foreach (var editor in editors.Values.OfType<TextBox>()) editor.TextChanged += (_, _) => Validate();
        _modeActions.Children.Add(ActionButton(W("ReviewEffects"), Review, "quiet"));
        _modeActions.Children.Add(ActionButton(W("DiscardDrafts"), () =>
        {
            _demoDrafts.Discard(selected.Device.Id);
            UpdateModePreview();
        }, "quiet"));
        _modeActions.Children.Add(applyButton);
        Validate();
    }

    /// <summary>One demo field in ParameterPane's geometry: label, editor and validation text.</summary>
    private static Control DemoField(string label, Control editor, TextBlock error)
    {
        editor.Classes.Add("wb-field");
        editor.Margin = new Thickness(0, 4, 0, 0);
        editor.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        var row = new StackPanel { Margin = new(0, 0, 0, 14) };
        row.Children.Add(new TextBlock { Text = label, Classes = { "wb-label" } });
        row.Children.Add(editor);
        row.Children.Add(error);
        return row;
    }
    private async Task ExportFilteredMonitorAsync()
    {
        if (_sessions is not { } sessions) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "telegramme-gefiltert.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, sessions.Monitor.ExportFilteredCsv());
        ViewModel.Status = W("FilteredExported");
    }

    private async Task ExportFullMonitorAsync()
    {
        if (_sessions is not { } sessions) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "telegramme-vollstaendig.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await using var stream = File.Create(path);
        sessions.Monitor.ExportFullCsv(stream);
        ViewModel.Status = W("FullExported");
    }
    /// <summary>Applies layout for the real window or a width forced by a deterministic test.</summary>
    internal void ApplyLayoutForWidth(double width) => ApplyLayout(width);

    private void ApplyLayout(double? widthOverride = null)
    {
        var width = widthOverride ?? Bounds.Width;
        ApplyTopbarLayout(width);
        var narrow = width is > 0 and < 1100;
        var navigationWidth = narrow ? 210 : Workspace.NavigatorWidth;
        var toolsHeight = Workspace.ToolsVisible
            ? Math.Min(Workspace.ToolsHeight, Bounds.Height is > 0 and < 740 ? 140 : Workspace.ToolsHeight)
            : 0;
        Navigator.IsVisible = NavSplitter.IsVisible = Workspace.NavigatorVisible;
        Zones.ColumnDefinitions[1].Width = new(Workspace.NavigatorVisible ? navigationWidth : 0);
        Zones.ColumnDefinitions[2].Width = new(Workspace.NavigatorVisible ? 5 : 0);
        Inspector.IsVisible = Workspace.InspectorVisible;
        if (Workspace.InspectorVisible && narrow)
        {
            // Narrow windows overlay the inspector like the reference instead of squeezing the editor.
            Grid.SetColumn(Inspector, 3);
            Inspector.Width = Math.Clamp(width - navigationWidth - 47 - 5 - 24, 280, 350);
            Inspector.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            Inspector.BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetX = -2, Color = Color.Parse("#40000000") });
            Zones.ColumnDefinitions[4].Width = new(0);
            Zones.ColumnDefinitions[5].Width = new(0);
            InspectorSplitter.IsVisible = false;
            // Reflow the covered strip instead of painting controls underneath the overlay.
            TabStrip.Margin = new Thickness(0, 0, Inspector.Width, 0);
            Editor.Margin = new Thickness(0, 0, Inspector.Width, 0);
            // The overlay ends above the lower dock; otherwise it would cover its focusables
            // and clip the dock content at 900x600 when both are open.
            Inspector.Margin = new Thickness(0, 0, 0,
                Workspace.ToolsVisible && !Workspace.ToolsMaximized ? toolsHeight + 5 : 0);
        }
        else
        {
            Grid.SetColumn(Inspector, 5);
            Inspector.Width = double.NaN;
            Inspector.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            Inspector.BoxShadow = default;
            Zones.ColumnDefinitions[4].Width = new(Workspace.InspectorVisible ? 5 : 0);
            Zones.ColumnDefinitions[5].Width = new(Workspace.InspectorVisible ? Workspace.InspectorWidth : 0);
            InspectorSplitter.IsVisible = Workspace.InspectorVisible;
            Inspector.Margin = default;
            TabStrip.Margin = default;
            Editor.Margin = default;
        }
        Tools.IsVisible = ToolsSplitter.IsVisible = Workspace.ToolsVisible;
        Editor.IsVisible = !Workspace.ToolsVisible || !Workspace.ToolsMaximized;
        // A covered editor must not keep Tab stops under the narrow inspector overlay (WCAG 2.4.11).
        var covered = narrow && Workspace.InspectorVisible;
        var navigation = covered ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue;
        KeyboardNavigation.SetTabNavigation(Editor, navigation);
        KeyboardNavigation.SetTabNavigation(ObjectTabs, navigation);
        KeyboardNavigation.SetTabNavigation(MainColumn, navigation);
        SetCompactToolTabs(narrow);
        var overlayWidth = Workspace.InspectorVisible && narrow ? Inspector.Width : 0;
        var editorWidth = width - 47 - (Workspace.NavigatorVisible ? navigationWidth : 0) - 5 - overlayWidth
            - (Workspace.InspectorVisible && !narrow ? Workspace.InspectorWidth + 5 : 0);
        LayoutObjectToolbar(editorWidth is > 0 and < 620);
        MainColumn.RowDefinitions[1].Height = Workspace.ToolsVisible && Workspace.ToolsMaximized ? new(0) : new(1, GridUnitType.Star);
        MainColumn.RowDefinitions[2].Height = new(Workspace.ToolsVisible ? 5 : 0);
        MainColumn.RowDefinitions[3].Height = Workspace.ToolsVisible && Workspace.ToolsMaximized
            ? new(1, GridUnitType.Star)
            : new(toolsHeight);
        if (Workspace.ToolsVisible && ToolHost.Content is null)
            ToolHost.Content = CreateToolView(Workspace.ActiveTool);
    }

    /// <summary>Icon-only tool tabs below 1100 DIP so the strip never runs under the float/maximize/close actions.</summary>
    private void SetCompactToolTabs(bool compact)
    {
        if (_compactToolTabs == compact) return;
        _compactToolTabs = compact;
        foreach (var tab in ToolTabs.Children.OfType<TabItem>())
        {
            if (tab.Tag is not string tool) continue;
            var title = AutomationProperties.GetName(tab) ?? tool;
            tab.Header = compact
                ? new ConceptIcon { Symbol = ToolIcon(tool), Width = 17, Height = 17 }
                : IconLabel(ToolIcon(tool), title);
            if (compact) ToolTip.SetTip(tab, title);
        }
    }

    /// <summary>
    /// Two-row object toolbar when the visible editor is narrow, so filters and checkboxes are
    /// never painted under the inspector overlay. Columns are reassigned, never re-parented.
    /// </summary>
    private void LayoutObjectToolbar(bool compact)
    {
        if (_compactObjectToolbar == compact) return;
        _compactObjectToolbar = compact;
        if (compact)
        {
            ObjectToolbar.ColumnDefinitions = new("Auto,Auto,*,Auto");
            ObjectToolbar.RowDefinitions = new("Auto,Auto");
            Grid.SetRow(ExpandAllButton, 0); Grid.SetColumn(ExpandAllButton, 3);
            Grid.SetRow(RelatedBox, 1); Grid.SetColumn(RelatedBox, 0);
            Grid.SetRow(Unlinked, 1); Grid.SetColumn(Unlinked, 1);
            Grid.SetRow(ObjectSearch, 1); Grid.SetColumn(ObjectSearch, 2); Grid.SetColumnSpan(ObjectSearch, 2);
            RelatedBox.Margin = new(0, 4, 8, 0); Unlinked.Margin = new(0, 4, 8, 0); ObjectSearch.Margin = new(0, 4, 0, 0);
        }
        else
        {
            ObjectToolbar.ColumnDefinitions = new("Auto,Auto,*,Auto,Auto,170,Auto");
            ObjectToolbar.RowDefinitions = new("Auto");
            Grid.SetRow(ExpandAllButton, 0); Grid.SetColumn(ExpandAllButton, 6);
            Grid.SetRow(RelatedBox, 0); Grid.SetColumn(RelatedBox, 3);
            Grid.SetRow(Unlinked, 0); Grid.SetColumn(Unlinked, 4);
            Grid.SetRow(ObjectSearch, 0); Grid.SetColumn(ObjectSearch, 5); Grid.SetColumnSpan(ObjectSearch, 1);
            RelatedBox.Margin = new(8, 0, 8, 0); Unlinked.Margin = new(8, 0, 8, 0); ObjectSearch.Margin = default;
        }
    }

    private static void CompactButtons(Panel panel)
    {
        // CheckBox derives from Button: only real buttons take the compact button geometry.
        foreach (var button in panel.Children.OfType<Button>().Where(button => button is not Avalonia.Controls.Primitives.ToggleButton))
            button.Classes.Add("wb-compact");
    }

    /// <summary>Adds a dock toolbar with the V4 compact button height, then docks it on top.</summary>
    private static void CommitToolbar(DockPanel panel, Panel toolbar)
    {
        CompactButtons(toolbar);
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        panel.Children.Add(toolbar);
    }

    private static string ToolIcon(string tool) => tool switch
    {
        "monitor" => "monitor", "programming" => "download", "scan" => "refresh",
        "search" => "search", "changes" => "clock", _ => "warning"
    };
    public void SaveLayout(string path)
    {
        CaptureScroll();
        CaptureNavigatorScroll();
        RememberSizes();
        Workspace.SaveLayout(path);
    }
    private void RememberSizes()
    {
        var previous = _refreshing;
        _refreshing = true;
        try
        {
            if (Workspace.NavigatorVisible && Navigator.Bounds.Width >= 200) Workspace.NavigatorWidth = Math.Clamp(Navigator.Bounds.Width, 200, 400);
            if (Workspace.InspectorVisible && Inspector.Bounds.Width >= 290) Workspace.InspectorWidth = Math.Clamp(Inspector.Bounds.Width, 290, 480);
            if (Workspace.ToolsVisible && !Workspace.ToolsMaximized && Tools.Bounds.Height >= 140) Workspace.ToolsHeight = Tools.Bounds.Height;
        }
        finally { _refreshing = previous; }
    }
    public void RequestShutdown() => _shuttingDown = true;
    private void OnInspector(object? sender, RoutedEventArgs e) => Workspace.InspectorVisible = !Workspace.InspectorVisible;
    private void OnLinks(object? sender, RoutedEventArgs e)
    {
        if (Workspace.ActiveTab is { } tab && tab.SelectedObjectIds.Count > 0) ShowBatchLinkDialog();
        else SetRepresentation("relations");
    }
    private void OnProgramming(object? sender, RoutedEventArgs e) => ShowTool("programming");
    private void OnWatch(object? sender, RoutedEventArgs e)
    {
        ShowTool("monitor");
        if (_sessions is { } sessions && Workspace.ActiveTab is { } tab)
            sessions.Monitor.PinFilter(tab.Target.Kind switch
            {
                "room" => new(MonitorScope.Room, tab.Target.Id),
                "ga" => new(MonitorScope.GroupAddress, tab.Target.Id),
                "device" => new(MonitorScope.Device, tab.Target.Id),
                _ => new(MonitorScope.Project)
            });
    }
    private void OnAddDevice(object? sender, RoutedEventArgs e) => ShowCatalog();
    private void OnPalette(object? sender, RoutedEventArgs e) => ShowPalette();
    private void OnHelp(object? sender, RoutedEventArgs e) => ShowHelp();
    /// <summary>V4 compare: the companion pins the active group address, or the first one for other work items.</summary>
    internal void OpenPinnedCompare()
    {
        var target = WorkbenchCompare.Resolve(ViewModel.Project, Workspace.ActiveTab?.Target);
        if (target is null)
        {
            ViewModel.Status = ViewModel.Project is null ? W("CompareNoProject") : W("CompareNoGroupAddress");
            return;
        }
        OpenCompanion(target);
        var address = ViewModel.Project is { } project
            ? ProjectQuery.FindGroupAddressById(project, target.Id)?.Address.ToString() ?? target.Id
            : target.Id;
        ViewModel.Status = W("CompareOpened", address);
    }
    private void OnComparePinned(object? sender, RoutedEventArgs e) => OpenPinnedCompare();
    private void OnMaximizeTool(object? sender, RoutedEventArgs e) => Workspace.ToolsMaximized = !Workspace.ToolsMaximized;
    private void OnHideTool(object? sender, RoutedEventArgs e) => Workspace.ToolsVisible = false;
    private void OnFloatTool(object? sender, RoutedEventArgs e)
    {
        var content = CreateToolView(Workspace.ActiveTool);
        var window = new Window { Title = ToolWindowTitle(Workspace.ActiveTool), Width = 1000, Height = 380,
            MinWidth = 480, MinHeight = 240, Classes = { "wb-host" }, Content = content };
        // Project undo/redo stays shell state: the floated tool forwards Ctrl+Z/Ctrl+Y to the shell
        // instead of keeping a second edit history. Text editors keep their own undo.
        window.AddHandler(KeyDownEvent, (_, e) => RouteUndoRedo(e), RoutingStrategies.Tunnel);
        _toolWindows.Add(window);
        window.Closed += (_, _) =>
        {
            _toolWindows.Remove(window);
            // Closing a fresh tool window is the explicit re-dock action; no live control is reparented.
            if (!_shuttingDown && !_closingSessionWindows) ShowTool(Workspace.ActiveTool);
        };
        window.Show(this);
    }

    /// <summary>
    /// Single project history: Ctrl+Z/Ctrl+Shift+Z/Ctrl+Y run the shell view-model's undo/redo.
    /// Called by the shell window and by every floated tool window.
    /// </summary>
    private bool RouteUndoRedo(KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is not (Key.Z or Key.Y)) return false;
        var editingText = e.Source is Visual source && (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any());
        if (editingText) return false;
        if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ViewModel.UndoCommand.Execute(null);
        else ViewModel.RedoCommand.Execute(null);
        e.Handled = true;
        return true;
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            ShowWindowMenu();
            e.Handled = true;
            return;
        }
        if (RouteUndoRedo(e)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.B: Workspace.NavigatorVisible = !Workspace.NavigatorVisible; break;
                case Key.J: if (Workspace.ToolsVisible) Workspace.ToolsVisible = false; else ShowTool(Workspace.ActiveTool); break;
                case Key.K: ShowPalette(); break;
                case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift): OpenPinnedCompare(); break;
                case Key.S: _ = GuardAsync(async () => await SaveAsync()); break;
                case Key.O: _ = GuardAsync(OpenProjectAsync); break;
                case Key.N: _ = GuardAsync(NewProjectAsync); break;
                case Key.Tab:
                    if (Workspace.Tabs.Count > 0) Workspace.ActiveTab = Workspace.Tabs[(Workspace.Tabs.IndexOf(Workspace.ActiveTab!) + 1) % Workspace.Tabs.Count]; break;
                default: return;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Workspace.InspectorVisible) { Workspace.InspectorVisible = false; e.Handled = true; }
        else if (e.Key == Key.F1) { ShowHelp(); e.Handled = true; }
    }
    private static string W(string key, params object[] args) => Localization.WorkbenchStrings.Get(key, args);
    private static bool FromButton(object? source) => source is Visual visual
        && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any());
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Concept icon plus label, for buttons that are not icon-only.</summary>
    private static Control IconLabel(string symbol, string text, double size = 14) => new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 5,
        Children = { new ConceptIcon { Symbol = symbol, Width = size, Height = size }, Label(text) }
    };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3), Classes = { "wb-muted" } };
    /// <summary>Shell-generated empty view: icon, heading and explanation; scrolls when the dock is short.</summary>
    private static Control EmptyState(string symbol, string heading, string explanation) => new ScrollViewer
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Content = new StackPanel
        {
            Spacing = 6, MaxWidth = 420,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new ConceptIcon { Symbol = symbol, Width = 30, Height = 30, Opacity = 0.7,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = explanation, FontSize = 11, Classes = { "wb-muted" }, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap }
            }
        }
    };
    private static Button ActionButton(string text, Action action, string? style = null, string? accessibleName = null, bool isDefault = false)
    {
        var button = new Button { Content = text, IsDefault = isDefault };
        button.Classes.Add("wb-button");
        if (style == "primary") button.Classes.Add("wb-primary");
        else if (style == "quiet") button.Classes.Add("wb-quiet");
        if (style is not null) button.Classes.Add(style);
        if (accessibleName is not null) { ToolTip.SetTip(button, accessibleName); AutomationProperties.SetName(button, accessibleName); }
        button.Click += (_, _) => action(); return button;
    }
    internal sealed record NavEntry(WorkbenchNavNode Node, int Depth);
    internal sealed record BatchLinkItem(string DeviceId, string ObjectId, string Name);
}
