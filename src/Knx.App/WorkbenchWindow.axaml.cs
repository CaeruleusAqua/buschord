using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Services;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Catalog;

namespace Knx.App;

/// <summary>Object-centred native shell. The workspace and project service own state, not the table controls.</summary>
public partial class WorkbenchWindow : Window
{
    private readonly ParameterPane _parameters;
    private readonly ComboBox _parameterDevice = new() { MinWidth = 240, Margin = new(0, 0, 0, 12) };
    private readonly List<Window> _toolWindows = [];
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
        _parameters = new ParameterPane(ViewModel);
        var parameterLayout = new DockPanel();
        DockPanel.SetDock(_parameterDevice, Avalonia.Controls.Dock.Top);
        parameterLayout.Children.Add(_parameterDevice);
        parameterLayout.Children.Add(_parameters);
        ParametersHost.Content = parameterLayout;
        _parameterDevice.ItemTemplate = new FuncDataTemplate<DeviceRow>((d, _) => Label(d is null ? "" : $"{d.AddressText} · {d.Name}"));
        _parameterDevice.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _parameterDevice.SelectedItem is DeviceRow d) _parameters.Load(d);
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
            Representations.Children.Add(ActionButton(title, () => SetRepresentation(view), "quiet"));
        }
        foreach (var (key, title) in new[] { ("monitor", "Monitor"), ("problems", "Prüfmeldungen") })
        {
            var tool = key;
            ToolTabs.Children.Add(ActionButton(title, () => ShowTool(tool), "quiet"));
        }
        ViewModel.PropertyChanged += OnModelChanged;
        ViewModel.Devices.CollectionChanged += OnDevicesChanged;
        ViewModel.CatalogLoaded += OnCatalogLoaded;
        Workspace.PropertyChanged += OnWorkspaceChanged;
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
    private void OnCatalogLoaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _parameters.Refresh());
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
            if (Workspace.ActiveTool is not ("monitor" or "problems")) Workspace.ActiveTool = "monitor";
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
                if (Workspace.ActiveTool is not ("monitor" or "problems")) Workspace.ActiveTool = "monitor";
                foreach (var nav in Workspace.Navigators.Values) nav.ExpandedIds.Add("project:" + p.Id);
                foreach (var space in p.Installations.SelectMany(i => i.Locations.Roots)) Workspace.Navigators["building"].ExpandedIds.Add("space:" + space.Id);
            }
            if (p is not null && Workspace.Tabs.Count == 0) Workspace.Open(new("project", p.Id), p.Name);
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

    private DeviceRow? CurrentDevice()
    {
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
                      + (tab.Target.Kind == "room" ? " · Einbauort; externe Wirkungsbezüge noch nicht integriert" : "") : "";
            Representations.IsVisible = tab is not null && !tool;
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
                }
                if (RelationsHost.IsVisible)
                {
                    var links = new GroupLinksPane(ViewModel);
                    links.Load(current);
                    RelationsHost.Content = links;
                }
                if (tool && tab.Target.Id == "catalog")
                {
                    if (ToolPageHost.Content is not CatalogPane) ToolPageHost.Content = new CatalogPane(ViewModel, _catalogLibrary) { DataContext = ViewModel };
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
        var meta = Label(node.Meta); meta.FontSize = 9; meta.Foreground = Brush.Parse("#8a94a5"); Grid.SetColumn(meta, 2); grid.Children.Add(meta);
        ToolTip.SetTip(grid, node.Name + " · " + node.Meta);
        return grid;
    }

    private Control BuildObjectRow(WorkbenchRow row)
    {
        var grid = new Grid { ColumnDefinitions = new("*,95,65,65,220"), Height = 34, Margin = new(0, 0, 12, 0) };
        if (row.Kind == "device") grid.Background = Brush.Parse("#f5f6fa");
        if (row.Kind is "channel" or "group") grid.Background = Brush.Parse("#fafbfc");
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
        var address = Label(row.Address); address.FontSize = 10; address.Foreground = Brush.Parse("#8490a1"); address.Margin = new(0, 0, 9, 0); Grid.SetColumn(address, 1); name.Children.Add(address);
        var text = Label(row.Name); text.FontSize = 11; text.FontWeight = row.Kind == "device" ? FontWeight.SemiBold : FontWeight.Normal;
        Grid.SetColumn(text, 2); name.Children.Add(text); grid.Children.Add(name);
        if (row.Object is not null)
        {
            for (var i = 1; i < 4; i++) { var unknown = Label("—"); unknown.Foreground = Brush.Parse("#8a94a5"); Grid.SetColumn(unknown, i); grid.Children.Add(unknown); }
            var links = Label(row.Links.Length > 0 ? row.Links : "Keine Zuordnung"); links.FontSize = 10;
            links.Foreground = Brush.Parse(row.Links.Length > 0 ? "#087e81" : "#8a94a5");
            Grid.SetColumn(links, 4); grid.Children.Add(links); ToolTip.SetTip(links, row.LinkDetails);
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
            InspectorBody.Children.Add(Note($"Gerät: {current.AddressText} · {current.Name}\nObjekt-ID: {obj.Id}\nHerstellerdefinition: DPT/Länge/Flags nicht im nativen Referenzmodell hinterlegt."));
            foreach (var id in obj.Links.ToArray())
            {
                var ga = ViewModel.Project is { } project ? ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id) : null;
                if (ga is not null) InspectorBody.Children.Add(ActionButton($"{ga.Address} · {ga.Name}", () => OpenTarget(new("ga", ga.Id)), "quiet"));
                else InspectorBody.Children.Add(Note(id + " · nicht auflösbar"));
                InspectorBody.Children.Add(ActionButton("Zuordnung entfernen", () =>
                {
                    try { ViewModel.ChangeGroupLink(current.Device, obj.Id, id, true); RefreshWorkspace(); }
                    catch (Exception ex) { ViewModel.Status = ex.Message; }
                }, "quiet"));
            }
            InspectorBody.Children.Add(ActionButton("Zuordnungen bearbeiten", () => SetRepresentation("relations")));
            InspectorBody.Children.Add(Note("Sendezuordnung ist in dieser Basis noch nicht verfügbar; keine stille Ableitung aus T-Flags."));
        }
        else if (current is not null && !(selected.Count == 0 && tab?.Target.Kind == "ga"))
        {
            InspectorBody.Children.Add(Label(current.Name));
            var name = new TextBox { Text = current.Name };
            InspectorBody.Children.Add(Note("Bezeichnung")); InspectorBody.Children.Add(name);
            InspectorBody.Children.Add(Note($"Adresse: {current.AddressText}\nProdukt: {current.Device.ProductRefId}\nApplikation: {current.Device.Hardware2ProgramRefId ?? "—"}"));
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
        if (tool is not ("monitor" or "problems")) throw new ArgumentException("Tool not integrated in this milestone.", nameof(tool));
        Workspace.ActiveTool = tool;
        Workspace.ToolsVisible = true;
        ToolHost.Content = tool == "problems" ? CreateProblemsView() : CreateMonitorView();
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
        var panel = new DockPanel();
        var toolbar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new(12, 6) };
        toolbar.Children.Add(ActionButton("Aufzeichnung starten", ViewModel.StartRecording));
        toolbar.Children.Add(ActionButton("Stoppen", ViewModel.StopRecording));
        toolbar.Children.Add(ActionButton("Aufzeichnung exportieren …", () => _ = GuardAsync(ExportRecordingAsync)));
        toolbar.Children.Add(Note("Offline · keine laufende Busverbindung. Aufzeichnung bleibt beim Ausblenden erhalten."));
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top); panel.Children.Add(toolbar);
        var list = new ListBox { ItemsSource = ViewModel.Monitor, Background = Brushes.Transparent };
        list.ItemTemplate = new FuncDataTemplate<MonitorLine>((line, _) => Label(line?.Text ?? ""));
        panel.Children.Add(list); return panel;
    }
    private void ApplyLayout()
    {
        Navigator.IsVisible = NavSplitter.IsVisible = Workspace.NavigatorVisible;
        Zones.ColumnDefinitions[1].Width = new(Workspace.NavigatorVisible ? Workspace.NavigatorWidth : 0);
        Zones.ColumnDefinitions[2].Width = new(Workspace.NavigatorVisible ? 5 : 0);
        Inspector.IsVisible = InspectorSplitter.IsVisible = Workspace.InspectorVisible;
        Zones.ColumnDefinitions[4].Width = new(Workspace.InspectorVisible ? 5 : 0);
        Zones.ColumnDefinitions[5].Width = new(Workspace.InspectorVisible ? Workspace.InspectorWidth : 0);
        Tools.IsVisible = ToolsSplitter.IsVisible = Workspace.ToolsVisible;
        Editor.IsVisible = !Workspace.ToolsVisible || !Workspace.ToolsMaximized;
        MainColumn.RowDefinitions[1].Height = Workspace.ToolsVisible && Workspace.ToolsMaximized ? new(0) : new(1, GridUnitType.Star);
        MainColumn.RowDefinitions[2].Height = new(Workspace.ToolsVisible ? 5 : 0);
        MainColumn.RowDefinitions[3].Height = Workspace.ToolsVisible && Workspace.ToolsMaximized ? new(1, GridUnitType.Star) : new(Workspace.ToolsVisible ? Workspace.ToolsHeight : 0);
        if (Workspace.ToolsVisible && ToolHost.Content is null) ToolHost.Content = Workspace.ActiveTool switch
        {
            "problems" => CreateProblemsView(), "monitor" => CreateMonitorView(),
            _ => Note("Dieses Werkzeug ist im Shell-Meilenstein noch nicht integriert. Monitor oder Prüfmeldungen wählen.")
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
    private void OnLinks(object? sender, RoutedEventArgs e) => SetRepresentation("relations");
    private void OnCatalog(object? sender, RoutedEventArgs e) => ShowCatalog();
    private void OnPalette(object? sender, RoutedEventArgs e) => ShowPalette();
    private void OnHelp(object? sender, RoutedEventArgs e) => ShowHelp();
    private void OnMaximizeTool(object? sender, RoutedEventArgs e) => Workspace.ToolsMaximized = !Workspace.ToolsMaximized;
    private void OnHideTool(object? sender, RoutedEventArgs e) => Workspace.ToolsVisible = false;
    private void OnFloatTool(object? sender, RoutedEventArgs e)
    {
        var window = new Window { Title = Workspace.ActiveTool == "problems" ? "BusChord · Prüfmeldungen" : "BusChord · Monitor", Width = 1000, Height = 380,
            Content = Workspace.ActiveTool == "problems" ? CreateProblemsView() : CreateMonitorView() };
        _toolWindows.Add(window); window.Closed += (_, _) => _toolWindows.Remove(window); window.Show(this);
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
                case Key.Tab:
                    if (Workspace.Tabs.Count > 0) Workspace.ActiveTab = Workspace.Tabs[(Workspace.Tabs.IndexOf(Workspace.ActiveTab!) + 1) % Workspace.Tabs.Count]; break;
                default: return;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Workspace.InspectorVisible) { Workspace.InspectorVisible = false; e.Handled = true; }
    }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = Brush.Parse("#687487"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 3) };
    private static Button ActionButton(string text, Action action, string? style = null, string? accessibleName = null)
    {
        var button = new Button { Content = text };
        if (style is not null) button.Classes.Add(style);
        if (accessibleName is not null) { ToolTip.SetTip(button, accessibleName); AutomationProperties.SetName(button, accessibleName); }
        button.Click += (_, _) => action(); return button;
    }
    internal sealed record NavEntry(WorkbenchNavNode Node, int Depth);
}
