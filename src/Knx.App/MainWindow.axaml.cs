using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Knx;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Bus;
using Knx.ProjectCompare;
using Knx.Reports;

namespace Knx.App;

/// <summary>Main window: ribbon commands, topology drag &amp; drop, ETS-style workspace panes.</summary>
public partial class MainWindow : Window
{
    private const string DeviceFormat = "knx-device";
    private readonly MainWindowViewModel _vm = new();
    private readonly CatalogPane _catalog;
    private readonly ParameterPane _parameters;
    private readonly GroupLinksPane _links;
    private readonly Factory _dockFactory = new();
    private Document _docDashboard = null!;
    private Document _docGroupAddresses = null!;
    private Document _docCatalog = null!;
    private Document _docParameters = null!;
    private Document _docLinks = null!;
    private Document _docCheck = null!;
    private Document _docMonitor = null!;
    private Document _docRecorder = null!;
    private IRootDock _root = null!;
    private readonly Dictionary<IDockable, IDock> _homeDocks = new();
    private readonly HashSet<IDockWindow> _releasing = new();
    private IDockWindow? _selfClosing;
    private DeviceRow? _paneDevice;
    private Point? _dragOrigin;
    private DeviceInstance? _draggedDevice;

    /// <summary>Exposed for start-up options (settings, startup project).</summary>
    public MainWindowViewModel ViewModel => _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _catalog = new CatalogPane(_vm);
        _parameters = new ParameterPane(_vm);
        _links = new GroupLinksPane(_vm);
        // Padding the panes used to get from their workspace tab.
        _parameters.Margin = new Thickness(10, 6);
        _links.Margin = new Thickness(10, 6);
        BuildLayout();
        DragDrop.SetAllowDrop(TopologyTree, true);
        TopologyTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        TopologyTree.PointerMoved += OnTreePointerMoved;
        KeyDown += OnKeyDown;
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.Title):
                    Title = _vm.Title;
                    break;
                case nameof(MainWindowViewModel.SelectedDevice):
                    SyncDevicePanes();
                    break;
                case nameof(MainWindowViewModel.Project):
                    ReloadPanes();
                    break;
            }
        };
    }

    // ---------- docking ----------

    /// <summary>
    /// Creates the dock layout: topology and properties as tool docks around one document dock
    /// holding every workspace document. The panels are the controls instantiated by the window,
    /// so all named fields and event handlers keep working after docking takes them over.
    /// </summary>
    private void BuildLayout()
    {
        foreach (var panel in PanelStaging.Children.ToList())
            PanelStaging.Children.Remove(panel);
        WorkspaceHost.Children.Remove(PanelStaging);

        _docDashboard = NewDocument("Dashboard", Strings.Get("TabDashboard"), DashboardPanel);
        _docGroupAddresses = NewDocument("GroupAddresses", Strings.Get("TabGroupAddresses"), GroupAddressesPanel);
        _docCatalog = NewDocument("Catalog", Strings.Get("MenuCatalog"), _catalog);
        _docParameters = NewDocument("Parameters", Strings.Get("BtnParameters"), _parameters);
        _docLinks = NewDocument("Links", Strings.Get("CommunicationObjects"), _links);
        _docCheck = NewDocument("Check", Strings.Get("TabCheck"), CheckPanel);
        _docMonitor = NewDocument("Monitor", Strings.Get("TabMonitor"), MonitorPanel);
        _docRecorder = NewDocument("Recorder", Strings.Get("TabRecorder"), RecorderPanel);

        var topology = new Tool
        {
            Id = "Topology", Title = Strings.Get("PanelTopology"), Context = TopologyPanel,
            CanClose = false, CanPin = true, CanFloat = true
        };
        var properties = new Tool
        {
            Id = "Properties", Title = Strings.Get("PanelProperties"), Context = PropertiesPanel,
            CanClose = false, CanPin = true, CanFloat = true
        };

        var documents = new DocumentDock
        {
            Id = "Documents",
            IsCollapsable = false,
            CanCreateDocument = false,
            ActiveDockable = _docDashboard,
            VisibleDockables = _dockFactory.CreateList<IDockable>(
                _docDashboard, _docGroupAddresses, _docCatalog, _docParameters,
                _docLinks, _docCheck, _docMonitor, _docRecorder)
        };
        var left = new ToolDock
        {
            Id = "Topology",
            Alignment = Alignment.Left,
            Proportion = 0.18,
            ActiveDockable = topology,
            VisibleDockables = _dockFactory.CreateList<IDockable>(topology)
        };
        var right = new ToolDock
        {
            Id = "Properties",
            Alignment = Alignment.Right,
            Proportion = 0.21,
            ActiveDockable = properties,
            VisibleDockables = _dockFactory.CreateList<IDockable>(properties)
        };
        var main = new ProportionalDock
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = _dockFactory.CreateList<IDockable>(
                left, new ProportionalDockSplitter { Id = "LeftSplitter" }, documents,
                new ProportionalDockSplitter { Id = "RightSplitter" }, right)
        };

        var root = _dockFactory.CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.Factory = _dockFactory;
        root.VisibleDockables = _dockFactory.CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        _root = root;

        // Floating opens a real HostWindow (Dock's own float). Closing a floating window returns
        // its panel to the dock it came from instead of losing it.
        _dockFactory.HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };
        _dockFactory.WindowClosing += (_, e) =>
        {
            if (e.Window is not { Layout: IRootDock windowRoot } window || ReferenceEquals(windowRoot, _root))
                return;
            if (ReferenceEquals(_selfClosing, window)) return; // our own close of the emptied window

            // Closing a floating panel re-docks it: release it from the floating window first,
            // re-add it to the dock it came from, then close the now empty window.
            var dockables = _homeDocks
                .Where(pair => ReferenceEquals(_dockFactory.FindRoot(pair.Key, _ => true), windowRoot))
                .Select(pair => (pair.Key, pair.Value))
                .ToList();

            // A cross-window drop can already have moved every dockable out; closing that
            // emptied window must go through or it stays behind as a ghost.
            if (dockables.Count == 0) return;

            e.Cancel = true;
            if (!_releasing.Add(window)) return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (dockable, _) in dockables) _dockFactory.RemoveDockable(dockable, false);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var (dockable, home) in dockables)
                    {
                        _dockFactory.AddDockable(home, dockable);
                        if (dockable is Document document) Activate(document);
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        _releasing.Remove(window);
                        _selfClosing = window;
                        window.Host?.Exit();
                        _selfClosing = null;
                    }, DispatcherPriority.Background);
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        };
        _dockFactory.InitLayout(root);

        foreach (var (dockable, dock) in new (IDockable, IDock)[]
        {
            (_docDashboard, documents), (_docGroupAddresses, documents), (_docCatalog, documents),
            (_docParameters, documents), (_docLinks, documents), (_docCheck, documents),
            (_docMonitor, documents), (_docRecorder, documents),
            (topology, left), (properties, right)
        })
        {
            _homeDocks[dockable] = dock;
        }

        DockArea.Factory = _dockFactory;
        DockArea.Layout = root;
    }

    private static Document NewDocument(string id, string title, object context) => new()
    {
        Id = id,
        Title = title,
        Context = context,
        CanClose = false,
        CanFloat = true
    };

    /// <summary>Brings a document to the front of its current dock and leaves the start screen.</summary>
    private void Activate(Document document)
    {
        _vm.StartDismissed = true;
        _dockFactory.SetActiveDockable(document);
        if (document.Owner is IDock owner) _dockFactory.SetFocusedDockable(owner, document);
    }

    // ---------- keyboard ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (control)
        {
            switch (e.Key)
            {
                case Key.K when shift:
                    OnCatalogLibrary(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.G:
                    OnAddGroupAddress(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.Z: _vm.UndoCommand.Execute(null); e.Handled = true; break;
                case Key.Y: _vm.RedoCommand.Execute(null); e.Handled = true; break;
                case Key.S: _vm.SaveProject(); e.Handled = true; break;
                case Key.N: OnNewProject(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.O: OnOpenProject(this, new RoutedEventArgs()); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.F5)
        {
            OnRunCheck(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            StartRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.SelectedGroupAddress is not null)
        {
            OnDeleteGroupAddress(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>F2: puts the inline name editor of the properties pane into edit mode.</summary>
    private void StartRename()
    {
        if (!_vm.HasSelection)
        {
            _vm.Status = Strings.Get("UxNeedsDevice");
            return;
        }
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.ApplyDetailsEdit();
        e.Handled = true;
    }

    // ---------- project ----------

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var window = new NewProjectWindow();
        if (await window.ShowDialog<bool>(this) != true) return;

        var project = ProjectWizard.Create(window.Options);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Neues Projekt speichern",
            SuggestedFileName = $"{project.Name}.knxproj",
            DefaultExtension = "knxproj",
        });
        var path = file?.TryGetLocalPath();
        _vm.CreateProject(project, path);
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "KNX-Projekt öffnen",
            FileTypeFilter = [new FilePickerFileType("KNX-Projekt") { Patterns = ["*.knxproj"] }],
        });
        if (files is not { Count: > 0 } || files[0].TryGetLocalPath() is not { } path) return;
        await OpenProject(path);
    }

    /// <summary>Loads a project path; used by the file picker and the recent list on the start screen.</summary>
    private async Task OpenProject(string path)
    {
        try
        {
            _vm.LoadProject(path);
        }
        catch (Exception ex)
        {
            _vm.Status = $"Projekt nicht ladbar: {ex.Message}";
            await ShowMessage("Projekt nicht ladbar", ex.Message);
        }
    }

    /// <summary>Start screen: one click on a recent project opens it.</summary>
    private async void OnRecentSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: RecentItem item } list) return;
        list.SelectedItem = null;
        await OpenProject(item.Path);
    }

    private void OnSaveProject(object? sender, RoutedEventArgs e)
    {
        if (_vm.ProjectPath.Length == 0) { OnSaveProjectAs(sender, e); return; }
        _vm.SaveProject();
    }

    private async void OnSaveProjectAs(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{_vm.Project.Name}.knxproj",
            DefaultExtension = "knxproj",
        });
        if (file?.TryGetLocalPath() is { } path) _vm.SaveProjectAs(path);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    // ---------- export ----------

    private async void OnExportGroupAddresses(object? sender, RoutedEventArgs e)
        => await ExportTextAsync("gruppenadressen.csv", "csv", p => ProjectCsvExporter.GroupAddresses(p));

    private async void OnExportDevices(object? sender, RoutedEventArgs e)
        => await ExportTextAsync("geraete.csv", "csv", p => ProjectCsvExporter.Devices(p));

    private async void OnExportTopology(object? sender, RoutedEventArgs e)
        => await ExportTextAsync("topologie.csv", "csv", p => ProjectCsvExporter.Topology(p));

    private async void OnExportSemanticTtl(object? sender, RoutedEventArgs e)
        => await ExportSemanticAsync(SemanticExport.SemanticFormat.Turtle, "semantik.ttl", "ttl");

    private async void OnExportSemanticJsonLd(object? sender, RoutedEventArgs e)
        => await ExportSemanticAsync(SemanticExport.SemanticFormat.JsonLd, "semantik.jsonld", "jsonld");

    private async Task ExportSemanticAsync(SemanticExport.SemanticFormat format, string fileName, string extension)
    {
        if (_vm.Project is not { } project)
        {
            _vm.Status = "kein Projekt geladen";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = fileName,
            DefaultExtension = extension,
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        SemanticExport.Export(project, path, format);
        var ontology = format == SemanticExport.SemanticFormat.Turtle && File.Exists(SemanticExport.DefaultOntologyPath);
        _vm.Status = $"{Path.GetFileName(path)} exportiert" + (ontology ? " (Ontologie eingebettet)" : "");
    }

    private async void OnExportLabels(object? sender, RoutedEventArgs e)
    {
        var options = new LabelOptions { SheetTitle = _vm.Project?.Name ?? "Etiketten" };
        await ExportTextAsync("etiketten.html", "html",
            p => LabelCreator.ToHtml(LabelCreator.GroupAddressLabels(p, options), options));
    }

    private async void OnExportLabelsCsv(object? sender, RoutedEventArgs e)
        => await ExportTextAsync("etiketten.csv", "csv",
            p => LabelCreator.ToCsv(LabelCreator.GroupAddressLabels(p, new LabelOptions())));

    private async void OnExportMergeDataSource(object? sender, RoutedEventArgs e)
        => await ExportTextAsync("etiketten-mailmerge.txt", "txt",
            p => LabelMergeSource.ToDataSource(LabelMergeSource.ForDevices(p)));

    private async Task ExportTextAsync(string fileName, string extension, Func<Project, string> render)
    {
        if (_vm.Project is not { } project)
        {
            _vm.Status = "kein Projekt geladen";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = fileName,
            DefaultExtension = extension,
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(render(project));
        _vm.Status = $"{fileName} exportiert";
    }

    private async void OnImportGroupAddresses(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is not { } project || project.Installations.FirstOrDefault() is not { } installation)
        {
            _vm.Status = "kein Projekt geladen";
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Gruppenadressen importieren",
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.txt"] }],
        });
        if (files is not { Count: > 0 } || files[0].TryGetLocalPath() is not { } path) return;

        var result = ProjectWizard.ImportGroupAddresses(project, installation, await File.ReadAllTextAsync(path));
        _vm.Refresh();
        _vm.Status = $"Import: {result.Imported} neu, {result.Skipped} übersprungen, {result.Errors.Count} Fehler";
        if (result.Errors.Count > 0)
            ShowReport("Importfehler", $"{result.Errors.Count} Zeile(n) konnten nicht gelesen werden",
                result.Errors.Select(error => new ReportRow("Fehler", Path.GetFileName(path), error)));
    }

    // ---------- editing ----------

    private void OnUndo(object? sender, RoutedEventArgs e) => _vm.UndoCommand.Execute(null);
    private void OnRedo(object? sender, RoutedEventArgs e) => _vm.RedoCommand.Execute(null);

    private void OnAddGroupAddress(object? sender, RoutedEventArgs e) => _vm.AddGroupAddress();

    private void OnDeleteGroupAddress(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGroupAddress is not { } row) { _vm.Status = "keine Gruppenadresse ausgewählt"; return; }
        _vm.DeleteGroupAddress(row);
    }

    private void OnApplyDetails(object? sender, RoutedEventArgs e) => _vm.ApplyDetailsEdit();

    private void OnGroupAddressDoubleTapped(object? sender, TappedEventArgs e)
        => _vm.Status = "Name und Datapoint-Typ rechts unter Eigenschaften bearbeiten";

    private async void OnReadGroupValue(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGroupAddress is not { } row || _vm.BusState != BusConnectionState.Connected)
        {
            _vm.Status = "Lesen benötigt eine Busverbindung";
            return;
        }
        await _vm.ReadGroupValueAsync(row);
        _vm.Status = $"Leseanforderung {row.AddressText} gesendet";
    }

    // ---------- topology ----------

    /// <summary>Tree context menu: catalogue, with the line of the clicked node preselected.</summary>
    private void OnCatalogForNode(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
        {
            TopologyTree.SelectedItem = node;
            _vm.SelectNode(node);
        }
        OnCatalogLibrary(sender, e);
    }

    private void OnDeleteDevice(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDevice is { } device) _vm.DeleteDevice(device);
        else _vm.Status = "kein Gerät ausgewählt";
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => _vm.SelectNode(_vm.SelectedTreeNode);

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.SelectedDevice is { } device) ShowParameters(device);
    }

    // ---------- drag & drop ----------

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragOrigin = e.GetPosition(TopologyTree);
        _draggedDevice = NodeAt(e.Source)?.Tag as DeviceInstance;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedDevice is null || _dragOrigin is not { } origin) return;
        if (!e.GetCurrentPoint(TopologyTree).Properties.IsLeftButtonPressed) { ResetDrag(); return; }

        var now = e.GetPosition(TopologyTree);
        if (Math.Abs(now.X - origin.X) < 6 && Math.Abs(now.Y - origin.Y) < 6) return;

        var device = _draggedDevice;
        ResetDrag();
        var data = new DataObject();
        data.Set(DeviceFormat, device.Address.ToString());
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = TargetOf(NodeAt(e.Source)) is null ? DragDropEffects.None : DragDropEffects.Move;

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (_draggedDevice is not { } device) return;
        switch (TargetOf(NodeAt(e.Source)))
        {
            case Line line:
                _vm.MoveDevice(device, line);
                break;
            case Segment segment:
                _vm.MoveDevice(device, segment);
                break;
            default:
                _vm.Status = "Ziel ist keine Linie oder kein Segment";
                break;
        }
        ResetDrag();
        e.Handled = true;
    }

    private void ResetDrag()
    {
        _dragOrigin = null;
        _draggedDevice = null;
    }

    /// <summary>Resolves a drop target: the node itself or the line/segment that owns the device.</summary>
    private object? TargetOf(TreeNode? node) => node?.Tag switch
    {
        Line line => line,
        Segment segment => segment,
        Area area => area.Lines.FirstOrDefault(),
        DeviceInstance device => OwnerLineOf(device),
        Installation installation => installation.Areas.SelectMany(a => a.Lines).FirstOrDefault(),
        _ => null,
    };

    private Line? OwnerLineOf(DeviceInstance device)
        => _vm.Project?.Installations
            .SelectMany(i => i.Areas)
            .SelectMany(a => a.Lines)
            .FirstOrDefault(l => l.Devices.Contains(device) || l.Segments.Any(s => s.Devices.Contains(device)));

    private TreeNode? NodeOf(object? sender) => (sender as Control)?.DataContext as TreeNode;

    private static TreeNode? NodeAt(object? source)
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (current is TreeViewItem { DataContext: TreeNode node }) return node;
            current = current.GetVisualParent();
        }
        return null;
    }

    // ---------- analysis ----------

    private void OnRunCheck(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is null)
        {
            _vm.Status = Strings.Get("UxNeedsProject");
            return;
        }
        Activate(_docCheck);
        var findings = _vm.RunCheck();
        ShowReport($"Projektprüfung – {_vm.Project?.Name}",
            $"{CheckCount(findings, CheckSeverity.Error)} Fehler · " +
            $"{CheckCount(findings, CheckSeverity.Warning)} Warnungen · " +
            $"{CheckCount(findings, CheckSeverity.Info)} Hinweise",
            findings.Select(f => new ReportRow(f.Severity.ToString(), $"{f.Code} {f.Path}", f.Message)),
            csvName: "projektpruefung");
    }

    private static int CheckCount(IEnumerable<CheckFinding> findings, CheckSeverity severity)
        => findings.Count(f => f.Severity == severity);

    private void OnSmartLink(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is null) { _vm.Status = Strings.Get("UxNeedsProject"); return; }
        var suggestions = _vm.SuggestLinks();
        if (suggestions.Count == 0)
        {
            ShowReport("Intelligente Verknüpfung", "Keine Vorschläge gefunden – alle Objekte sind verknüpft oder es fehlen Namensähnlichkeiten.", []);
            return;
        }
        ShowReport("Intelligente Verknüpfung",
            $"{suggestions.Count} Vorschläge. „Übernehmen“ legt alle vorgeschlagenen Verknüpfungen an (rückgängig mit Strg+Z).",
            suggestions.Select(s => new ReportRow(
                s.Score.ToString(),
                $"{s.Device.Address} · {s.GroupAddress.Address}",
                $"{s.ComObject.Text ?? s.ComObject.Id}  →  {s.GroupAddress.Name}   ({s.Reason})")),
            "Alle Verknüpfungen anlegen",
            async () => { _vm.ApplyLinks(suggestions); await Task.CompletedTask; },
            "verknuepfungen");
    }

    private async void OnReplaceProduct(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is null) { _vm.Status = "kein Projekt geladen"; return; }
        var window = new ReplaceProductWindow(_vm.ReplaceProduct);
        if (_vm.SelectedDevice is { } device && device.Device.ProductRefId.Length > 0)
            window.SetOldReference(device.Device.ProductRefId);
        if (await window.ShowDialog<bool>(this) == true && window.AppliedMessage is { } message)
            _vm.Status = message;
    }

    // ---------- workspace panes & download ----------

    private void OnOpenParameters(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDevice is not { } device) { _vm.Status = "kein Gerät ausgewählt"; return; }
        ShowParameters(device);
    }

    private void OnGroupLinks(object? sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDevice is not { } device) { _vm.Status = Strings.Get("LinkSelectDevice"); return; }
        ShowLinks(device);
    }

    /// <summary>Shows the parameter document for the device; the pane owns its empty state.</summary>
    private void ShowParameters(DeviceRow device)
    {
        _parameters.Load(device);
        Activate(_docParameters);
    }

    /// <summary>Shows the communication-object document for the current device.</summary>
    public void ShowLinks()
    {
        if (_vm.SelectedDevice is { } device) _links.Load(device);
        Activate(_docLinks);
    }

    /// <summary>Shows the communication-object document for the device.</summary>
    private void ShowLinks(DeviceRow device)
    {
        _links.Load(device);
        Activate(_docLinks);
    }

    /// <summary>
    /// Opens the catalogue document, revealing the workspace even without a project,
    /// with the line of the selected tree node or device preselected.
    /// </summary>
    public void ShowCatalog()
    {
        if (SelectedLine() is { } line) _catalog.PreferredLine = line;
        Activate(_docCatalog);
        Dispatcher.UIThread.Post(_catalog.FocusSearch, DispatcherPriority.Loaded);
    }

    /// <summary>Completes when the background catalogue load has bound its data (screenshot hook).</summary>
    internal Task CatalogReady => _catalog.Ready;

    /// <summary>Line insert target from the topology selection, if it names one.</summary>
    private Line? SelectedLine()
    {
        if (_vm.SelectedTreeNode?.Tag is Line line) return line;
        return _vm is { Project: { } project, SelectedDevice: { } device }
            && ProjectQuery.LocateDevice(project, device.Address) is { } located
                ? located.Line
                : null;
    }

    /// <summary>Screenshot hook: shows the parameter document with a resolved demo set.</summary>
    public void ShowDemoParameters(IEnumerable<ParameterInstance> instances, IEnumerable<ParameterType> types)
        => ShowDemoParameters("Geräteparameter – Demo", instances, types);

    private void ShowDemoParameters(string title, IEnumerable<ParameterInstance> instances,
        IEnumerable<ParameterType> types)
    {
        _parameters.LoadResolved(title, instances, types, null);
        Activate(_docParameters);
    }

    /// <summary>Keeps the device panes on the current selection without reloading the same device.</summary>
    private void SyncDevicePanes()
    {
        var device = _vm.SelectedDevice;
        if (ReferenceEquals(device, _paneDevice)) return;
        _paneDevice = device;
        _parameters.Load(device);
        _links.Load(device);
    }

    /// <summary>Re-reads all panes after the project instance was replaced (load, undo, redo).</summary>
    private void ReloadPanes()
    {
        _parameters.Refresh();
        _links.Refresh();
        _catalog.ReloadProject();
    }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanDownload) { _vm.Status = "Download benötigt Verbindung, Gerät und Zielgerät"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Applikations-Image wählen",
        });
        if (files is { Count: > 0 } && files[0].TryGetLocalPath() is { } path)
            await _vm.DownloadAsync(path);
    }

    // ---------- recorder ----------

    private void OnStartRecording(object? sender, RoutedEventArgs e) => _vm.StartRecording();
    private void OnStopRecording(object? sender, RoutedEventArgs e) => _vm.StopRecording();

    private async void OnSaveRecording(object? sender, RoutedEventArgs e)
    {
        if (_vm.Recorder.Count == 0) { _vm.Status = "keine Aufzeichnung vorhanden"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "telegramme.jsonl",
            DefaultExtension = "jsonl",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON Lines") { Patterns = ["*.jsonl"] },
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) _vm.Recorder.SaveCsv(path);
        else _vm.Recorder.SaveJsonLines(path);
        _vm.Status = $"Aufzeichnung gespeichert: {Path.GetFileName(path)}";
    }

    private async void OnLoadRecording(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Aufzeichnung laden",
            FileTypeFilter = [new FilePickerFileType("Aufzeichnung") { Patterns = ["*.jsonl", "*.csv"] }],
        });
        if (files is not { Count: > 0 } || files[0].TryGetLocalPath() is not { } path) return;

        var entries = TelegramRecorder.Load(path);
        _vm.LoadRecording(entries);
        _vm.Status = $"{entries.Count} Telegramme geladen (Wiedergabe über den Monitor)";
    }

    private void OnClearMonitor(object? sender, RoutedEventArgs e) => _vm.Monitor.Clear();

    // ---------- compare & settings ----------

    private async void OnCompareProjects(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is not { } left) { _vm.Status = "erst ein Projekt laden"; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Vergleichsprojekt wählen",
            FileTypeFilter = [new FilePickerFileType("KNX-Projekt") { Patterns = ["*.knxproj"] }],
        });
        if (files is not { Count: > 0 } || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            var right = Knxproj.Load(path);
            var report = ProjectComparer.Compare(left, right);
            var window = new CompareWindow();
            window.Load(report);
            await window.ShowDialog(this);
            _vm.Status = $"Vergleich: {report.AddedCount} + / {report.RemovedCount} − / {report.ChangedCount} ~";
        }
        catch (Exception ex)
        {
            _vm.Status = $"Vergleich fehlgeschlagen: {ex.Message}";
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_vm.Settings);
        if (await window.ShowDialog<bool>(this) == true)
            _vm.Status = Strings.Get("OptionsSaved");
    }

    private void OnCatalogLibrary(object? sender, RoutedEventArgs e) => ShowCatalog();

    // ---------- helpers ----------

    private void ShowReport(string title, string subtitle, IEnumerable<ReportRow> rows,
        string? primaryText = null, Func<Task>? primary = null, string csvName = "befunde")
    {
        var window = new ReportWindow();
        window.Load(title, subtitle, rows, primaryText, primary, csvName);
        window.ShowDialog(this);
    }

    private async Task ShowMessage(string title, string message)
    {
        await Task.CompletedTask;
        ShowReport(title, message, []);
    }
}
