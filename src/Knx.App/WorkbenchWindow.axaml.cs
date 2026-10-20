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
    private readonly StackPanel _modePanel = new() { Margin = new(0, 12, 0, 0), Spacing = 8, MaxWidth = 620, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    private readonly ScrollViewer _modeScroll = new() { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly List<Window> _toolWindows = [];
    private WorkbenchSessions? _sessions;
    private DispatcherTimer? _sessionTimer;
    private string? _sessionProjectId;
    private int _demoTick;
    private MonitorScope _monitorScope = MonitorScope.All;
    private string? _monitorScopeId;
    private bool _refreshing;
    private bool _shuttingDown;
    private bool _confirmingClose;
    private readonly string? _layoutPath;
    private readonly CatalogLibrary _catalogLibrary;
    private Project? _shownProject;
    private WorkbenchTab? _renderedTab;
    private string _renderedPerspective = "building";
    private int _scrollRestoreVersion;
    private bool _scrollRestorePending;
    private int _navRestoreVersion;
    private bool _navRestorePending;
    public MainWindowViewModel ViewModel { get; }
    public WorkbenchWorkspace Workspace { get; }
    internal ParameterPane ParameterEditor => _parameters;
    internal WorkbenchSessions? Sessions => _sessions;

    public WorkbenchWindow() : this(new MainWindowViewModel(), new WorkbenchWorkspace(), DefaultLayoutPath) { }

    public WorkbenchWindow(MainWindowViewModel viewModel, WorkbenchWorkspace workspace, string? layoutPath = null, CatalogLibrary? catalogLibrary = null)
    {
        ViewModel = viewModel;
        Workspace = workspace;
        _layoutPath = layoutPath;
        _catalogLibrary = catalogLibrary ?? CatalogLibrary.Shared;
        InitializeComponent();
        DataContext = ViewModel;
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        _parameterSession = new(ViewModel);
        _parameters = new ParameterPane(_parameterSession);
        var parameterLayout = new DockPanel();
        DockPanel.SetDock(_parameterDevice, Avalonia.Controls.Dock.Top);
        parameterLayout.Children.Add(_parameterDevice);
        _modeScroll.Content = _modePanel;
        _modeScroll.IsVisible = false;
        var parameterContent = new Grid();
        parameterContent.Children.Add(_parameters);
        parameterContent.Children.Add(new Border { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = _modeScroll });
        parameterLayout.Children.Add(parameterContent);
        ParametersHost.Content = parameterLayout;
        _parameterDevice.ItemTemplate = new FuncDataTemplate<DeviceRow>((d, _) => Label(d is null ? "" : $"{d.AddressText} · {d.Name}"));
        _parameterDevice.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _parameterDevice.SelectedItem is DeviceRow d) _parameters.Load(d);
            UpdateModePreview();
        };
        NavTree.ItemTemplate = new FuncDataTemplate<NavEntry>((entry, _) => entry is null ? new Border() : BuildNavRow(entry));
        ObjectTable.ItemTemplate = new FuncDataTemplate<WorkbenchRow>((row, _) => row is null ? new Border() : BuildObjectRow(row));
        BuildMenus();
        foreach (var (key, title, glyph) in new[] { ("building", "Gebäude", "Category"), ("topology", "Topologie", "AccountTree"), ("groups", "Gruppen", "FormatListBulleted") })
        {
            var perspective = key;
            Perspectives.Children.Add(ActionButton(title, () => Navigate(perspective), "quiet"));
            var rail = ActionButton("", () => Navigate(perspective), "quiet");
            rail.Content = new Icon { Symbol = glyph, Width = 19, Height = 19 };
            rail.Width = 35; rail.Height = 35; rail.FontSize = 20;
            ToolTip.SetTip(rail, title); AutomationProperties.SetName(rail, title);
            Rail.Children.Add(rail);
        }
        foreach (var (symbol, title, run) in new (string, string, Action)[]
        {
            ("LibraryBooks", "Produktkatalog", ShowCatalog), ("FactCheck", "Projektprüfung", () => ShowTool("problems")),
            ("Radar", "Monitor", () => ShowTool("monitor")), ("Settings", "Optionen", ShowSettings)
        })
        {
            var button = ActionButton("", run, "quiet", title);
            button.Content = new Icon { Symbol = symbol, Width = 19, Height = 19 };
            button.Width = 35; button.Height = 35; Rail.Children.Add(button);
        }
        foreach (var (key, title) in new[] { ("objects", "Kommunikationsobjekte"), ("params", "Parameter"), ("relations", "Beziehungen"), ("overview", "Prüfung") })
        {
            var view = key;
            var button = ActionButton(title, () => SetRepresentation(view), "quiet");
            button.Classes.Add("wb-view-tab");
            Representations.Children.Add(button);
        }
        foreach (var (key, title) in new[] { ("monitor", "Monitor"), ("programming", "Programmierung"), ("problems", "Prüfmeldungen") })
        {
            var tool = key;
            var button = ActionButton(title, () => ShowTool(tool), "quiet");
            button.Classes.Add("wb-tool-tab");
            ToolTabs.Children.Add(button);
        }
        ViewModel.PropertyChanged += OnModelChanged;
        ViewModel.Devices.CollectionChanged += OnDevicesChanged;
        ViewModel.CatalogLoaded += OnCatalogLoaded;
        Workspace.PropertyChanged += OnWorkspaceChanged;
        SizeChanged += (_, _) => ApplyLayout();
        KeyDown += OnShellKeyDown;
        Closing += OnClosing;
        foreach (var splitter in new[] { NavSplitter, InspectorSplitter, ToolsSplitter })
        {
            splitter.AddHandler(PointerReleasedEvent, (_, _) => RememberSizes(), RoutingStrategies.Tunnel);
            splitter.KeyUp += (_, _) => RememberSizes();
        }
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnModelChanged;
            ViewModel.Devices.CollectionChanged -= OnDevicesChanged;
            ViewModel.CatalogLoaded -= OnCatalogLoaded;
            Workspace.PropertyChanged -= OnWorkspaceChanged;
            _sessionTimer?.Stop();
            _parameters.Dispose();
            _ = DisposeSessionsAsync();
            foreach (var tool in _toolWindows.ToArray()) tool.Close();
        };
        if (_layoutPath is not null) Workspace.TryRestoreLayout(_layoutPath);
        RefreshWorkspace();
    }

    private static string DefaultLayoutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusChord", "workbench-layout.json");
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
    private void OnCatalogLoaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _parameterSession.Refresh());
    private void EnsureSessions()
    {
        var project = ViewModel.Project;
        if (project is null)
        {
            _sessionTimer?.Stop();
            if (_sessions is not null) { _ = DisposeSessionsAsync(); }
            _sessionProjectId = null;
            ToolHost.Content = null;
            return;
        }
        if (_sessions is not null && _sessionProjectId == project.Id) return;
        _ = DisposeSessionsAsync();
        _sessionProjectId = project.Id;
        _sessions = new(() => ViewModel.Project,
            filter => ResolveMonitorIds(filter),
            (p, device) => new FrozenConfiguration(
                WorkbenchProject.ConfigurationSignature(p, device.Id),
                DeviceConfigurationSnapshot.Freeze(p, device).PayloadJson));
        _monitorScope = MonitorScope.All; _monitorScopeId = null;
        _sessionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _sessionTimer.Tick -= OnSessionTick;
        _sessionTimer.Tick += OnSessionTick;
        _sessionTimer.Start();
        // Tool views close over one session instance; never leave them pointing at a disposed one.
        ToolHost.Content = null;
        if (Workspace.ToolsVisible) ShowTool(Workspace.ActiveTool);
    }

    private async Task DisposeSessionsAsync()
    {
        var sessions = _sessions;
        _sessions = null;
        if (sessions is null) return;
        try { await sessions.DisposeAsync(); }
        catch (Exception ex) { ViewModel.Status = "Werkzeugsitzung beendet: " + ex.Message; }
    }

    private async Task ResetSessionsForReplacementAsync()
    {
        _sessionTimer?.Stop();
        ToolHost.Content = null;
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
            _ => new HashSet<string>()
        };
    }

    private void OnSessionTick(object? sender, EventArgs e)
    {
        if (_shuttingDown || _sessions is not { } sessions) return;
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

    private static string Unquote(string raw)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch (System.Text.Json.JsonException) { return raw; }
    }
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
            Project p => p.Name, SpaceNode s => s.Name, Line l => l.Name ?? "Linie",
            DeviceInstance d => d.Name ?? d.Address.ToString(), GroupAddressEntry g => $"{g.Address} · {g.Name}",
            _ => target.Id == "catalog" ? "Produktkatalog" : target.Id
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
            if (Workspace.ActiveTool is not ("monitor" or "problems" or "programming")) Workspace.ActiveTool = "monitor";
            var p = ViewModel.Project;
            if (!ReferenceEquals(_shownProject, p))
            {
                _shownProject = p;
                if (ToolPageHost.Content is CatalogPane catalog) catalog.ReloadProject();
                _parameters.Refresh();
            }
            if (p is not null && Workspace.ProjectId != p.Id)
            {
                Workspace.ChangeProject(p.Id);
                if (_layoutPath is not null) Workspace.TryRestoreLayout(_layoutPath, t => t == new WorkbenchTarget("tool", "catalog") || WorkbenchProjection.Resolve(p, t) is not null);
                if (Workspace.ActiveTool is not ("monitor" or "problems" or "programming")) Workspace.ActiveTool = "monitor";
                foreach (var nav in Workspace.Navigators.Values) nav.ExpandedIds.Add("project:" + p.Id);
                foreach (var space in p.Installations.SelectMany(i => i.Locations.Roots)) Workspace.Navigators["building"].ExpandedIds.Add("space:" + space.Id);
            }
            if (p is not null && Workspace.Tabs.Count == 0) Workspace.Open(new("project", p.Id), p.Name);
            EnsureSessions();
            foreach (var tab in Workspace.Tabs.Where(t => t.Target.Kind != "tool" && WorkbenchProjection.Resolve(p, t.Target) is null).ToArray()) Workspace.Close(tab);
            foreach (var tab in Workspace.Tabs) tab.Title = TargetTitle(WorkbenchProjection.Resolve(p, tab.Target), tab.Target);
            ProjectName.Text = p?.Name ?? "BusChord";
            ProjectSummary.Text = p is null ? "Projekt öffnen oder neu anlegen" : $"{ProjectQuery.DeviceList(p).Count()} Geräte · {WorkbenchProjection.Lines(p).Count()} Linien";
            NavSearch.Text = Workspace.Navigators[Workspace.Perspective].Query;
            RefreshNavigation();
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
        ObjectTabs.Children.Clear();
        foreach (var tab in Workspace.Tabs)
        {
            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var activate = ActionButton(tab.Title, () => { CaptureScroll(); Workspace.ActiveTab = tab; }, "quiet");
            activate.MaxWidth = 240; activate.Height = 38;
            activate.Classes.Set("active", tab == Workspace.ActiveTab);
            header.Children.Add(activate);
            header.Children.Add(ActionButton("×", () => { Workspace.Close(tab); RefreshWorkspace(); }, "quiet", tab.Title + " schließen"));
            ObjectTabs.Children.Add(header);
        }
        ObjectTabs.Children.Add(ActionButton("+", ShowPalette, "quiet", "Arbeitsgegenstand suchen"));
    }

    /// <summary>Insertion line suggested by the current work item; rooms fall back to their first device/line.</summary>
    private Line? CurrentLine()
    {
        if (ViewModel.Project is not { } project || Workspace.ActiveTab is not { } tab) return null;
        return tab.Target.Kind switch
        {
            "line" => WorkbenchProjection.Lines(project).FirstOrDefault(l => l.Id == tab.Target.Id),
            "device" => ProjectQuery.LocateDevice(project, ProjectQuery.DeviceList(project).First(d => d.Id == tab.Target.Id).Address)?.Line,
            "room" => WorkbenchProjection.Devices(project, tab.Target).Select(d => ProjectQuery.LocateDevice(project, d.Address)?.Line)
                .FirstOrDefault(l => l is not null),
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
            EditorTitle.Text = tab?.Title ?? "Willkommen bei BusChord";
            var subject = tab is null ? null : WorkbenchProjection.Resolve(ViewModel.Project, tab.Target);
            var context = subject switch
            {
                SpaceNode space => "Gebäude / " + (space.Parent?.Path ?? space.Name),
                DeviceInstance device => "Gerät / " + device.Address,
                Line => "Topologie", GroupAddressEntry => "Gruppenadressen", Project => "Projektübersicht", _ => "Projektwerkzeuge"
            };
            Breadcrumb.Text = $"{ViewModel.Project?.Name ?? "Projekt"}  ›  {context}";
            Subtitle.Text = tab is null ? "Ein Projekt öffnen, neu anlegen oder den Produktkatalog durchsuchen."
                : tool ? "Projektwerkzeug · native Ansicht" : ViewModel.Project is { } p
                    ? $"{WorkbenchProjection.Devices(p, tab.Target).Count} Geräte im Ausschnitt · gemeinsamer Projektstand"
                      + (tab.Target.Kind == "room" ? " · Einbauort und relevante externe Kanäle getrennt" : "") : "";
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
                ObjectSearch.Text = tab.Filter;
                Unlinked.IsChecked = tab.UnlinkedOnly;
                var rows = ViewModel.Project is { } project ? WorkbenchProjection.Rows(project, tab) : [];
                _scrollRestorePending = true;
                var restoreVersion = ++_scrollRestoreVersion;
                ObjectTable.ItemsSource = rows;
                if (ObjectTable.SelectedItems is { } selection)
                    foreach (var row in rows.Where(r => tab.SelectedObjectIds.Contains(r.Key))) selection.Add(row);
                TableFootnote.Text = $"{rows.Count(r => r.Object is not null)} sichtbare Objekte · DPT, Länge und Flags nur bei belegter Herstellerdefinition";
                var current = CurrentDevice();
                if (ParametersHost.IsVisible)
                {
                    var devices = ViewModel.Project is { } pp ? WorkbenchProjection.Devices(pp, tab.Target).Select(d => new DeviceRow(d)).ToList() : [];
                    _parameterDevice.ItemsSource = devices;
                    _parameterDevice.SelectedItem = devices.FirstOrDefault(d => d.Device == current?.Device) ?? devices.FirstOrDefault();
                    var selected = _parameterDevice.SelectedItem as DeviceRow;
                    if (!ReferenceEquals(_parameters.Device, selected?.Device)) _parameters.Load(selected);
                    UpdateModePreview();
                }
                if (RelationsHost.IsVisible)
                {
                    var links = new GroupLinksPane(ViewModel);
                    links.Load(current);
                    RelationsHost.Content = links;
                }
                if (tool && tab.Target.Id == "catalog")
                {
                    var line = CurrentLine();
                    if (ToolPageHost.Content is CatalogPane open) { if (line is not null) open.PreferredLine = line; }
                    else ToolPageHost.Content = new CatalogPane(ViewModel, _catalogLibrary) { DataContext = ViewModel, PreferredLine = line };
                }
                else if (tab.View == "overview") ToolPageHost.Content = CreateProblemsView();
                for (var i = 0; i < Representations.Children.Count; i++)
                    Representations.Children[i].Classes.Set("active", new[] { "objects", "params", "relations", "overview" }[i] == tab.View);
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
                start.Children.Add(ActionButton("Projekt öffnen …", () => _ = GuardAsync(OpenProjectAsync), "primary"));
                start.Children.Add(ActionButton("Neues Projekt …", () => _ = GuardAsync(NewProjectAsync)));
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
        var grid = new Grid { ColumnDefinitions = new("22,*,Auto"), Margin = new(entry.Depth * 12, 0, 4, 0), Height = 29 };
        var expand = ActionButton(node.Children.Count == 0 ? "·" : Workspace.Navigators[Workspace.Perspective].ExpandedIds.Contains(node.Key) ? "⌄" : "›", () =>
        {
            var expanded = Workspace.Navigators[Workspace.Perspective].ExpandedIds;
            CaptureNavigatorScroll();
            if (!expanded.Add(node.Key)) expanded.Remove(node.Key);
            RefreshNavigation();
        }, "quiet", "Auf-/zuklappen: " + node.Name);
        expand.IsEnabled = node.Children.Count > 0; expand.Padding = new(0);
        grid.Children.Add(expand);
        var label = Label(node.Name); label.FontSize = 11; Grid.SetColumn(label, 1); grid.Children.Add(label);
        var meta = Label(node.Meta); meta.FontSize = 9; meta.Classes.Add("wb-subtle"); Grid.SetColumn(meta, 2); grid.Children.Add(meta);
        ToolTip.SetTip(grid, node.Name + " · " + node.Meta);
        return grid;
    }

    private Control BuildObjectRow(WorkbenchRow row)
    {
        var grid = new Grid { ColumnDefinitions = new("*,85,55,55,190"), Height = 32, Margin = new(0, 0, 12, 0) };
        if (row.Kind == "device") grid.Classes.Add("wb-device");
        if (row.Kind is "channel" or "group") grid.Classes.Add("wb-channel");
        var name = new Grid { ColumnDefinitions = new("22,Auto,*"), Margin = new(4 + row.Depth * 12, 0, 6, 0) };
        if (row.CanExpand)
        {
            var expand = ActionButton(row.Expanded ? "⌄" : "›", () =>
            {
                if (Workspace.ActiveTab is not { } t) return;
                var key = row.Kind == "device" ? row.Device.Id : row.Key;
                if (!t.CollapsedDeviceIds.Add(key)) t.CollapsedDeviceIds.Remove(key);
                CaptureScroll(); RefreshEditor();
            }, "quiet", "Auf-/zuklappen: " + row.Name);
            expand.Padding = new(0); name.Children.Add(expand);
        }
        var address = Label(row.Address); address.FontSize = 10; address.Classes.Add("wb-subtle"); address.Margin = new(0, 0, 9, 0); Grid.SetColumn(address, 1); name.Children.Add(address);
        var text = Label(row.Name); text.FontSize = 11; text.FontWeight = row.Kind == "device" ? FontWeight.SemiBold : FontWeight.Normal;
        Grid.SetColumn(text, 2); name.Children.Add(text); grid.Children.Add(name);
        if (row.Object is not null)
        {
            var values = new[] { row.Dpt ?? "—", row.Bits is { } bits ? bits + " Bit" : "—", row.Flags ?? "—" };
            for (var i = 0; i < 3; i++)
            {
                var value = Label(values[i]); value.FontSize = 10; value.Classes.Add("wb-muted");
                Grid.SetColumn(value, i + 1); grid.Children.Add(value);
            }
            var links = Label(row.Links.Length > 0 ? row.Links : "Keine Zuordnung"); links.FontSize = 10;
            links.Classes.Add(row.Links.Length > 0 ? "wb-accent" : "wb-subtle");
            Grid.SetColumn(links, 4); grid.Children.Add(links); ToolTip.SetTip(links, row.LinkDetails);
            if (!row.Active) { name.Opacity = 0.55; ToolTip.SetTip(text, row.Name + " · inaktiv"); }
            if (row.External) text.Classes.Add("wb-success");
        }
        else if (row.External)
        {
            var external = Label("externer Kanal"); external.FontSize = 9; external.Classes.Add("wb-success");
            Grid.SetColumn(external, 1); grid.Children.Add(external);
        }
        ToolTip.SetTip(text, row.Name);
        return grid;
    }

    private void OnNavSelected(object? sender, SelectionChangedEventArgs e)
    {
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
    private void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || Workspace.ActiveTab is not { } tab) return;
        tab.SelectedObjectIds.Clear();
        if (ObjectTable.SelectedItems is { } selected)
            foreach (var row in selected.OfType<WorkbenchRow>()) tab.SelectedObjectIds.Add(row.Key);
        if (CurrentDevice() is { } device) ViewModel.SelectedDevice = device;
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

    private void RefreshInspector()
    {
        InspectorBody.Children.Clear();
        var tab = Workspace.ActiveTab;
        var selected = ViewModel.Project is { } p && tab is not null
            ? WorkbenchProjection.Rows(p, tab).Where(r => tab.SelectedObjectIds.Contains(r.Key)).ToList() : [];
        if (selected.Count > 1)
        {
            InspectorBody.Children.Add(Label($"{selected.Count} Zeilen ausgewählt"));
            InspectorBody.Children.Add(Note("Die Auswahl gilt nur für diesen Arbeitsgegenstand. Geräteübergreifende atomare Zuordnung wird separat integriert."));
            return;
        }
        var current = CurrentDevice();
        if (selected.FirstOrDefault()?.Object is { } obj && current is not null)
        {
            InspectorBody.Children.Add(Label($"#{obj.Number} · {obj.Text ?? obj.Id}"));
            InspectorBody.Children.Add(Note($"Gerät: {current.AddressText} · {current.Name}\nObjekt-ID: {obj.Id}\nDPT: {selected.First().Dpt ?? "unbekannt"} · Länge: {selected.First().Bits?.ToString() ?? "unbekannt"} Bit · Flags: {selected.First().Flags ?? "unbekannt"}"));
            var sendingGroup = Knx.Workbench.WorkbenchProject.Metadata(ViewModel.Project!, new(current.Device.Id, obj.Id))?.SendingGroupId;
            foreach (var id in obj.Links.ToArray())
            {
                var ga = ViewModel.Project is { } project ? ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id) : null;
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(ga is null ? Note(id + " · nicht auflösbar")
                    : ActionButton($"{ga.Address} · {ga.Name}" + (sendingGroup == id ? "  · sendend" : ""), () => OpenTarget(new("ga", ga.Id)), "quiet"));
                if (ga is not null)
                {
                    row.Children.Add(ActionButton("Als Sendeadresse", () =>
                    {
                        string? error = null;
                        ViewModel.Edit($"Sendeadresse {ga.Address}", p =>
                        {
                            var result = Knx.Workbench.WorkbenchProject.SetSending(p, new(current.Device.Id, obj.Id), ga.Id);
                            if (!result.Ok) error = result.Errors.FirstOrDefault()?.Reason;
                        });
                        if (error is not null) ViewModel.Status = error;
                        RefreshWorkspace();
                    }, "quiet", "Sendeadresse ausdrücklich wählen"));
                }
                row.Children.Add(ActionButton("Entfernen", () =>
                {
                    string? warning = null;
                    ViewModel.Edit($"Zuordnung {id} entfernen", p =>
                    {
                        var result = Knx.Workbench.WorkbenchProject.RemoveLink(p, new(current.Device.Id, obj.Id), id);
                        warning = result.Warnings.FirstOrDefault();
                    });
                    if (warning is not null) ViewModel.Status = warning;
                    RefreshWorkspace();
                }, "quiet"));
                InspectorBody.Children.Add(row);
            }
            InspectorBody.Children.Add(ActionButton("Weitere Zuordnungen …", () => {
                if (Workspace.ActiveTab is { } tab)
                {
                    tab.SelectedObjectIds.Clear(); tab.SelectedObjectIds.Add(WorkbenchProjection.ObjectKey(current.Device, obj));
                }
                ShowBatchLinkDialog();
            }));
            InspectorBody.Children.Add(Note("Sendezuordnung ist unabhängig vom T-Flag; beim Entfernen wird keine Ersatzadresse still gewählt."));
        }
        else if (current is not null && !(selected.Count == 0 && tab?.Target.Kind == "ga"))
        {
            InspectorBody.Children.Add(Label(current.Name));
            var name = new TextBox { Text = current.Name };
            InspectorBody.Children.Add(Note("Bezeichnung")); InspectorBody.Children.Add(name);
            var application = current.Device.Hardware2ProgramRefId
                ?? ViewModel.Project?.Workbench.Devices.GetValueOrDefault(current.Device.Id)?.Application
                ?? "-";
            InspectorBody.Children.Add(Note($"Adresse: {current.AddressText}\nProdukt: {current.Device.ProductRefId}\nApplikation: {application}"));
            InspectorBody.Children.Add(ActionButton("Eigenschaften übernehmen", () =>
            {
                ViewModel.SelectedDevice = current; ViewModel.EditName = name.Text ?? ""; ViewModel.ApplyDetailsEdit(); RefreshWorkspace();
            }, "primary"));
            InspectorBody.Children.Add(ActionButton("In Topologie zeigen", () => { Navigate("topology"); NavSearch.Text = current.AddressText; }));
            InspectorBody.Children.Add(ActionButton("Parameter öffnen", () => OpenTarget(new("device", current.Device.Id), "params"), "quiet"));
        }
        else if (tab?.Target.Kind == "ga" && WorkbenchProjection.Resolve(ViewModel.Project, tab.Target) is GroupAddressEntry ga)
        {
            var name = new TextBox { Text = ga.Name }; var dpt = new TextBox { Text = ga.DatapointType };
            InspectorBody.Children.Add(Note(ga.Address.ToString())); InspectorBody.Children.Add(name); InspectorBody.Children.Add(dpt);
            InspectorBody.Children.Add(ActionButton("Übernehmen", () =>
            {
                ViewModel.RenameGroupAddress(new(ga.Address, ga.Name, ga.DatapointType, ga.Id), name.Text ?? "", dpt.Text); RefreshWorkspace();
            }, "primary"));
        }
        else InspectorBody.Children.Add(Note("Gerät oder Kommunikationsobjekt auswählen, um Eigenschaften zu sehen."));
    }

    public void ShowTool(string tool)
    {
        if (tool is not ("monitor" or "problems" or "programming")) throw new ArgumentException("Unknown tool.", nameof(tool));
        EnsureSessions();
        Workspace.ActiveTool = tool;
        Workspace.ToolsVisible = true;
        foreach (var button in ToolTabs.Children.OfType<Button>())
            button.Classes.Set("active", button.Content switch
            {
                "Monitor" => tool == "monitor", "Programmierung" => tool == "programming", "Prüfmeldungen" => tool == "problems", _ => false
            });
        ToolHost.Content = tool switch
        {
            "problems" => CreateProblemsView(), "programming" => CreateProgrammingView(), _ => CreateMonitorView()
        };
        ApplyLayout();
    }
    private Control CreateProblemsView()
    {
        ViewModel.RunCheck();
        var list = new ListBox { ItemsSource = ViewModel.Findings, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<CheckFinding>((f, _) => Note(f is null ? "" : $"{f.Severity} · {f.Code} · {f.Path}\n{f.Message}"));
        return list;
    }
    private Control CreateMonitorView()
    {
        EnsureSessions();
        var panel = new DockPanel();
        if (_sessions is not { } sessions)
            return Note("Zuerst ein Projekt öffnen; Monitorsitzungen gehören zum Projekt.");
        var toolbar = new WrapPanel { Margin = new(12, 6) };
        var live = new TextBlock { FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new(12, 0) };
        void RefreshState()
        {
            var running = sessions.Monitor.IsRunning;
            live.Text = running
                ? $"● Aufzeichnung läuft · {sessions.Monitor.RecordedCount} vollständig · {sessions.Monitor.FilteredRows.Count} sichtbar"
                : $"■ Gestoppt · {sessions.Monitor.RecordedCount} aufgezeichnet";
            live.Classes.Set("wb-success", running);
            live.Classes.Set("wb-subtle", !running);
        }
        toolbar.Children.Add(ActionButton("Starten", () =>
        {
            try
            {
                if (!sessions.Monitor.IsRunning)
                {
                    sessions.Monitor.Configure(WorkbenchMonitorMode.Group, SessionOrigin.Demo, "demo-ip");
                    sessions.Monitor.Start();
                }
                RefreshState();
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "primary"));
        toolbar.Children.Add(ActionButton("Stoppen", () => { sessions.Monitor.Stop(); RefreshState(); }));
        toolbar.Children.Add(ActionButton("Anzeige leeren", () => { sessions.Monitor.ClearDisplay(); RefreshState(); }, "quiet"));
        var mode = new ComboBox { ItemsSource = new[] { "Gruppenmonitor", "Busmonitor" }, SelectedIndex = 0, MinWidth = 130 };
        mode.SelectionChanged += (_, _) =>
        {
            if (sessions.Monitor.IsRunning) { mode.SelectedIndex = sessions.Monitor.Mode == WorkbenchMonitorMode.Bus ? 1 : 0; ViewModel.Status = "Monitorart nur im gestoppten Zustand wechseln."; return; }
            sessions.Monitor.Configure(mode.SelectedIndex == 1 ? WorkbenchMonitorMode.Bus : WorkbenchMonitorMode.Group, SessionOrigin.Demo, "demo-ip");
        };
        toolbar.Children.Add(mode);
        var filter = new ComboBox { MinWidth = 190, SelectedIndex = 0 };
        filter.ItemsSource = new[] { "Alle Telegramme", "Ganzes Projekt", "Aktueller Arbeitsgegenstand", "Auswahl folgen" };
        filter.SelectedIndex = _monitorScope switch
        {
            MonitorScope.Project => 1, MonitorScope.Room or MonitorScope.GroupAddress => 2, MonitorScope.ComObject => 3, _ => 0
        };
        filter.SelectionChanged += (_, _) =>
        {
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
                            _ => new(MonitorScope.Project)
                        });
                        break;
                    case 3: sessions.Monitor.SetFollowSelection(true, SelectedComObjectId()); break;
                    default: sessions.Monitor.PinFilter(new(MonitorScope.All)); break;
                }
                RefreshState();
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        };
        toolbar.Children.Add(filter);
        toolbar.Children.Add(ActionButton("CSV gefiltert …", () => _ = GuardAsync(ExportFilteredMonitorAsync)));
        toolbar.Children.Add(ActionButton("Vollaufzeichnung …", () => _ = GuardAsync(ExportFullMonitorAsync), "quiet"));
        toolbar.Children.Add(live);
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top); panel.Children.Add(toolbar);
        var header = new Grid { ColumnDefinitions = new("70,80,120,90,50,70,*"), Height = 28 };
        header.Classes.Add("wb-table-header");
        var headings = new[] { "Zeit", "Quelle", "Ziel / Gruppenadresse", "Dienst", "DPT", "Wert", "Herkunft" };
        for (var i = 0; i < headings.Length; i++)
        {
            var text = Label(headings[i]); text.FontSize = 9; text.Classes.Add("wb-muted"); text.Margin = new(6, 0, 0, 0);
            Grid.SetColumn(text, i); header.Children.Add(text);
        }
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top); panel.Children.Add(header);
        var tablePanel = new Grid();
        var empty = new TextBlock { Text = "Noch keine Telegramme für diesen Filter.", Classes = { "wb-muted" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var list = new ListBox { ItemsSource = sessions.Monitor.FilteredRows, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<WorkbenchTelegram>((row, _) =>
        {
            if (row is null) return new Border();
            var grid = new Grid { ColumnDefinitions = new("70,80,120,90,50,70,*"), Height = 28 };
            var cells = new[] { row.Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), row.Source, row.Destination, row.Service, row.DatapointType ?? "—", row.Value, row.Origin.ToString() };
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = Label(cells[i]); cell.FontSize = 10; cell.Margin = new(6, 0, 0, 0);
                if (i is 0 or 4 or 5 or 6) cell.Classes.Add("wb-muted");
                Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
            return grid;
        });
        tablePanel.Children.Add(list);
        tablePanel.Children.Add(empty);
        empty.IsVisible = sessions.Monitor.FilteredRows.Count == 0;
        if (sessions.Monitor.FilteredRows is System.Collections.Specialized.INotifyCollectionChanged changed)
        {
            void OnChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __) => empty.IsVisible = sessions.Monitor.FilteredRows.Count == 0;
            changed.CollectionChanged += OnChanged;
            list.DetachedFromVisualTree += (_, _) => changed.CollectionChanged -= OnChanged;
        }
        panel.Children.Add(tablePanel);
        RefreshState();
        return panel;
    }

    private Control CreateProgrammingView()
    {
        EnsureSessions();
        if (_sessions is not { } sessions) return Note("Zuerst ein Projekt öffnen.");
        var panel = new DockPanel();
        var toolbar = new WrapPanel { Margin = new(12, 6) };
        var devices = ViewModel.Project is { } project ? ViewModel.Devices.ToList() : [];
        var device = new ComboBox { ItemsSource = devices, MinWidth = 250, SelectedIndex = devices.Count > 0 ? 0 : -1 };
        device.ItemTemplate = new FuncDataTemplate<DeviceRow>((d, _) => Label(d is null ? "" : $"{d.AddressText} · {d.Name}"));
        toolbar.Children.Add(device);
        toolbar.Children.Add(ActionButton("Auftrag einreihen", () =>
        {
            if (device.SelectedItem is not DeviceRow row) { ViewModel.Status = "Gerät wählen."; return; }
            try
            {
                var job = sessions.Programming.Enqueue(row.Device.Id, "full", "demo-ip");
                ViewModel.Status = $"Demo-Auftrag für {row.Name} eingereiht · Gerätestand eingefroren";
                job.PropertyChanged += (_, _) => { };
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "primary"));
        toolbar.Children.Add(ActionButton("Ausführen", () => _ = GuardAsync(async () =>
        {
            try { await sessions.Programming.RunAsync(); }
            catch (Exception ex) { ViewModel.Status = "Programmierung: " + ex.Message; }
        })));
        toolbar.Children.Add(ActionButton("Fehlgeschlagene wiederholen", () =>
        {
            try
            {
                foreach (var job in sessions.Programming.Jobs.Where(j => j.Status == ProgrammingJobStatus.Failed).ToArray())
                    sessions.Programming.Retry(job);
                ViewModel.Status = "Fehlgeschlagene Demo-Aufträge erneut eingereiht.";
            }
            catch (Exception ex) { ViewModel.Status = ex.Message; }
        }, "quiet"));
        toolbar.Children.Add(Note("SIMULATION · kein realer Download und keine Geräteprogrammierung"));
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top); panel.Children.Add(toolbar);
        var header = new Grid { ColumnDefinitions = new("110,220,100,90,*"), Height = 28 };
        header.Classes.Add("wb-table-header");
        var headings = new[] { "Adresse", "Gerät", "Status", "Auftrag", "Hinweis" };
        for (var i = 0; i < headings.Length; i++)
        {
            var text = Label(headings[i]); text.FontSize = 9; text.Classes.Add("wb-muted"); text.Margin = new(6, 0, 0, 0);
            Grid.SetColumn(text, i); header.Children.Add(text);
        }
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top); panel.Children.Add(header);
        var list = new ListBox { ItemsSource = sessions.Programming.Jobs, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<ProgrammingJob>((job, _) =>
        {
            if (job is null) return new Border();
            var grid = new Grid { ColumnDefinitions = new("110,220,100,90,*"), Height = 28 };
            var cells = new[] { job.Address, job.Name, job.Status.ToString().ToUpperInvariant(), job.Operation, job.Error ?? "" };
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = Label(cells[i]); cell.FontSize = 10; cell.Margin = new(6, 0, 0, 0); Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
            return grid;
        });
        panel.Children.Add(list);
        return panel;
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
        _modeScroll.IsVisible = false;
        _parameters.IsVisible = true;
        if (_parameterDevice.SelectedItem is not DeviceRow selected || ViewModel.Project is not { } project) return;
        var demo = project.Workbench.Devices.GetValueOrDefault(selected.Device.Id)?.Demo == true
            && selected.Device.ParameterValues.Count > 0;
        if (!demo) return;
        _parameters.IsVisible = false;
        _modeScroll.IsVisible = true;
        _modePanel.Children.Add(Label("Demo-Applikation · Änderungen erst nach Übernehmen"));
        var editors = new Dictionary<string, Control>(StringComparer.Ordinal);
        foreach (var (key, raw) in selected.Device.ParameterValues
                     .OrderBy(p => p.Key switch { "mode" => 0, "led" => 1, "interface" => 2, _ => 3 })
                     .ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Label(key switch
            {
                "mode" => "Funktion", "led" => "Status-LED", "interface" => "Schnittstelle",
                "debounce" => "Entprellzeit (ms)", "holdTime" => "Haltezeit", "brightnessThreshold" => "Helligkeitsschwelle",
                "setpoint" => "Sollwert", "deadband" => "Totband", _ => key
            }));
            Control editor;
            if (key == "mode")
            {
                var box = new ComboBox { ItemsSource = new[] { "Schalten", "Schalten + Dimmen" } };
                box.SelectedIndex = Unquote(raw) == "dim" ? 1 : 0;
                editor = box;
            }
            else if (key == "led")
            {
                var box = new ComboBox { ItemsSource = new[] { "Über Rückmeldeobjekt", "Aus", "An" } };
                box.SelectedIndex = Unquote(raw) switch { "off" => 1, "on" => 2, _ => 0 };
                editor = box;
            }
            else if (key == "interface")
                editor = new TextBox { Text = Unquote(raw) };
            else
                editor = new TextBox { Text = raw };
            row.Children.Add(editor);
            editors[key] = editor;
            _modePanel.Children.Add(row);
        }
        var preview = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-muted" } };
        string Mode() => editors.GetValueOrDefault("mode") is ComboBox { SelectedIndex: 1 } ? "dim" : "switch";
        void Review()
        {
            var result = WorkbenchProject.PreviewMode(project, selected.Device.Id, Mode());
            preview.Text = !result.Supported ? result.Reason
                : result.Effects.Count == 0 ? "Keine Objektänderung; nur der Demo-Parameter wechselt."
                : "Vorschau · noch nicht übernommen\n" + string.Join("\n", result.Effects.Select(e =>
                {
                    var links = e.RetainedGroupIds.Select(id => ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id))
                        .Where(g => g is not null).Select(g => g!.Address.ToString());
                    var ga = string.Join(", ", links);
                    return $"{e.Key.ObjectRefId}: {(e.Active ? "aktiv" : "inaktiv")}{(ga.Length > 0 ? " · Links bleiben: " + ga : "")}";
                }));
        }
        _modePanel.Children.Add(preview);
        _modePanel.Children.Add(ActionButton("Auswirkungen prüfen", Review, "primary"));
        _modePanel.Children.Add(ActionButton("Übernehmen", () =>
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
                values[key] = key is "mode" or "led" or "interface"
                    ? System.Text.Json.JsonSerializer.Serialize(value) : value;
            }
            var mode = Mode();
            ViewModel.Edit($"Demo-Parameter {selected.Name}", p =>
            {
                var device = ProjectQuery.DeviceList(p).FirstOrDefault(d => d.Id == selected.Device.Id);
                if (device is null) return;
                foreach (var (key, value) in values) device.ParameterValues[key] = value;
                WorkbenchProject.ApplyMode(p, selected.Device.Id, mode);
            });
            ViewModel.Status = "Demo-Parameter übernommen; bestehende Zuordnungen bleiben erhalten.";
            RefreshWorkspace();
        }));
    }

    private async Task ExportFilteredMonitorAsync()
    {
        if (_sessions is not { } sessions) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "telegramme-gefiltert.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, sessions.Monitor.ExportFilteredCsv());
        ViewModel.Status = "Gefilterte Anzeige exportiert";
    }

    private async Task ExportFullMonitorAsync()
    {
        if (_sessions is not { } sessions) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "telegramme-vollstaendig.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await using var stream = File.Create(path);
        sessions.Monitor.ExportFullCsv(stream);
        ViewModel.Status = "Vollständige Aufzeichnung exportiert";
    }
    private void ApplyLayout()
    {
        var narrow = Bounds.Width is > 0 and < 1100;
        var navigationWidth = narrow ? 210 : Workspace.NavigatorWidth;
        Navigator.IsVisible = NavSplitter.IsVisible = Workspace.NavigatorVisible;
        Zones.ColumnDefinitions[1].Width = new(Workspace.NavigatorVisible ? navigationWidth : 0);
        Zones.ColumnDefinitions[2].Width = new(Workspace.NavigatorVisible ? 5 : 0);
        Inspector.IsVisible = Workspace.InspectorVisible;
        if (Workspace.InspectorVisible && narrow)
        {
            // Narrow windows overlay the inspector like the reference instead of squeezing the editor.
            Grid.SetColumn(Inspector, 3);
            Inspector.Width = Math.Clamp(Bounds.Width - navigationWidth - 47 - 5 - 24, 280, 350);
            Inspector.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            Zones.ColumnDefinitions[4].Width = new(0);
            Zones.ColumnDefinitions[5].Width = new(0);
            InspectorSplitter.IsVisible = false;
        }
        else
        {
            Grid.SetColumn(Inspector, 5);
            Inspector.Width = double.NaN;
            Inspector.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            Zones.ColumnDefinitions[4].Width = new(Workspace.InspectorVisible ? 5 : 0);
            Zones.ColumnDefinitions[5].Width = new(Workspace.InspectorVisible ? Workspace.InspectorWidth : 0);
            InspectorSplitter.IsVisible = Workspace.InspectorVisible;
        }
        Tools.IsVisible = ToolsSplitter.IsVisible = Workspace.ToolsVisible;
        Editor.IsVisible = !Workspace.ToolsVisible || !Workspace.ToolsMaximized;
        MainColumn.RowDefinitions[1].Height = Workspace.ToolsVisible && Workspace.ToolsMaximized ? new(0) : new(1, GridUnitType.Star);
        MainColumn.RowDefinitions[2].Height = new(Workspace.ToolsVisible ? 5 : 0);
        MainColumn.RowDefinitions[3].Height = Workspace.ToolsVisible && Workspace.ToolsMaximized ? new(1, GridUnitType.Star) : new(Workspace.ToolsVisible ? Workspace.ToolsHeight : 0);
        if (Workspace.ToolsVisible && ToolHost.Content is null) ToolHost.Content = Workspace.ActiveTool switch
        {
            "problems" => CreateProblemsView(), "programming" => CreateProgrammingView(), "monitor" => CreateMonitorView(),
            _ => Note("Monitor, Programmierung oder Prüfmeldungen wählen.")
        };
    }
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
    private void OnCatalog(object? sender, RoutedEventArgs e) => ShowCatalog();
    private void OnProgramming(object? sender, RoutedEventArgs e) => ShowTool("programming");
    private void OnWatch(object? sender, RoutedEventArgs e)
    {
        ShowTool("monitor");
        if (_sessions is { } sessions && Workspace.ActiveTab is { } tab)
            sessions.Monitor.PinFilter(tab.Target.Kind switch
            {
                "room" => new(MonitorScope.Room, tab.Target.Id),
                "ga" => new(MonitorScope.GroupAddress, tab.Target.Id),
                _ => new(MonitorScope.Project)
            });
    }
    private void OnAddDevice(object? sender, RoutedEventArgs e) => ShowCatalog();
    private void OnPalette(object? sender, RoutedEventArgs e) => ShowPalette();
    private void OnHelp(object? sender, RoutedEventArgs e) => ShowHelp();
    private void OnMaximizeTool(object? sender, RoutedEventArgs e) => Workspace.ToolsMaximized = !Workspace.ToolsMaximized;
    private void OnHideTool(object? sender, RoutedEventArgs e) => Workspace.ToolsVisible = false;
    private void OnFloatTool(object? sender, RoutedEventArgs e)
    {
        var content = Workspace.ActiveTool switch
        {
            "problems" => CreateProblemsView(), "programming" => CreateProgrammingView(), _ => CreateMonitorView()
        };
        var window = new Window { Title = Workspace.ActiveTool == "problems" ? "BusChord · Prüfmeldungen" : Workspace.ActiveTool == "programming" ? "BusChord · Programmierung" : "BusChord · Monitor", Width = 1000, Height = 380,
            Content = content };
        _toolWindows.Add(window);
        window.Closed += (_, _) =>
        {
            _toolWindows.Remove(window);
            // Closing a fresh tool window is the explicit re-dock action; no live control is reparented.
            if (!_shuttingDown) ShowTool(Workspace.ActiveTool);
        };
        window.Show(this);
    }
    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var editingText = e.Source is Visual source && (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any());
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.B: Workspace.NavigatorVisible = !Workspace.NavigatorVisible; break;
                case Key.J: if (Workspace.ToolsVisible) Workspace.ToolsVisible = false; else ShowTool(Workspace.ActiveTool); break;
                case Key.K: ShowPalette(); break;
                case Key.S: _ = GuardAsync(async () => await SaveAsync()); break;
                case Key.O: _ = GuardAsync(OpenProjectAsync); break;
                case Key.N: _ = GuardAsync(NewProjectAsync); break;
                case Key.Z when !editingText:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ViewModel.RedoCommand.Execute(null); else ViewModel.UndoCommand.Execute(null); break;
                case Key.Y when !editingText: ViewModel.RedoCommand.Execute(null); break;
                case Key.Tab:
                    if (Workspace.Tabs.Count > 0) Workspace.ActiveTab = Workspace.Tabs[(Workspace.Tabs.IndexOf(Workspace.ActiveTab!) + 1) % Workspace.Tabs.Count]; break;
                default: return;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Workspace.InspectorVisible) { Workspace.InspectorVisible = false; e.Handled = true; }
    }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3), Classes = { "wb-muted" } };
    private static Button ActionButton(string text, Action action, string? style = null, string? accessibleName = null)
    {
        var button = new Button { Content = text };
        button.Classes.Add("wb-button");
        if (style == "primary") button.Classes.Add("wb-primary");
        else if (style == "quiet") button.Classes.Add("wb-quiet");
        if (style is not null) button.Classes.Add(style);
        if (accessibleName is not null) { ToolTip.SetTip(button, accessibleName); AutomationProperties.SetName(button, accessibleName); }
        button.Click += (_, _) => action(); return button;
    }
    internal sealed record NavEntry(WorkbenchNavNode Node, int Depth);
}
