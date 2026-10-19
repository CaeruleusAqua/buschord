using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Knx;
using Knx.Bus;
using Knx.Catalog;

namespace Knx.App.Services;

/// <summary>Project, editing and bus session state behind the main window.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private KnxBus? _bus;
    private UndoStack _undo = new();
    private readonly CatalogLibrary _catalog;
    private readonly Dictionary<string, CatalogApplicationProgram> _catalogPrograms = [];
    private bool _awaitingCatalog;

    public MainWindowViewModel() : this(CatalogLibrary.Shared) { }

    /// <summary>Tests pass their own library; the app shares one parse across panes and windows.</summary>
    public MainWindowViewModel(CatalogLibrary catalog)
    {
        _catalog = catalog;
        _catalog.Changed += OnCatalogChanged;
        Settings = AppSettings.Load();
        Recorder = new TelegramRecorder();
        _status = Strings.Get("StatusReady");
        Stats = new DashboardStats { ProjectName = "" };
        RefreshRecents();
    }

    /// <summary>Raised off the UI thread when the catalogue snapshot finally arrived.</summary>
    public event EventHandler? CatalogLoaded;

    /// <summary>True while a parameter lookup waits for the first catalogue snapshot.</summary>
    public bool IsAwaitingCatalog => _awaitingCatalog;

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (_awaitingCatalog) CatalogLoaded?.Invoke(this, EventArgs.Empty);
    }

    // ---------- project ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private Project? _project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _projectPath = "";

    public string Title => Project is null
        ? "BusChord – kein Projekt"
        : $"BusChord – {Project.Name}{(_undo.IsDirty ? " *" : "")}";

    /// <summary>True when the project differs from the last state written to disk.</summary>
    public bool IsDirty => _undo.IsDirty;

    /// <summary>True while a project is loaded; gates commands that need project data.</summary>
    public bool HasProject => Project is not null;

    /// <summary>
    /// Set when the user opened a project-independent workspace (catalogue) from the start screen,
    /// so the workspace replaces the start screen without a loaded project.
    /// </summary>
    [ObservableProperty]
    private bool _startDismissed;

    /// <summary>Start screen: no project loaded and no workspace opened explicitly.</summary>
    public bool ShowStart => Project is null && !StartDismissed;

    /// <summary>Workspace: a project is loaded, or a project-independent workspace was opened.</summary>
    public bool ShowWorkspace => Project is not null || StartDismissed;

    private void NotifyWorkspaceState()
    {
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowWorkspace));
    }

    partial void OnStartDismissedChanged(bool value) => NotifyWorkspaceState();

    /// <summary>Project file name for the status bar; empty while no project is loaded.</summary>
    public string ProjectLabel
    {
        get
        {
            if (Project is not { } project) return "";
            return ProjectPath.Length == 0 ? project.Name : System.IO.Path.GetFileName(ProjectPath);
        }
    }

    /// <summary>Full project path behind the status-bar label; the start screen never shows it as text.</summary>
    public string ProjectTooltip => ProjectPath.Length == 0 ? Project?.Name ?? "" : ProjectPath;

    public bool HasDevices => Devices.Count > 0;
    public bool HasGroupAddresses => GroupAddresses.Count > 0;
    public bool HasFindings => Findings.Count > 0;
    public bool HasSelection => SelectedDevice is not null || SelectedGroupAddress is not null;
    public bool HasDeviceSelected => SelectedDevice is not null;

    /// <summary>Next-step hint for the bus buttons: empty once an interface host is configured.</summary>
    public string BusInterfaceHint => string.IsNullOrWhiteSpace(Settings.BusInterface)
        ? Strings.Get("UxNeedsInterface")
        : "";

    /// <summary>Recently opened projects for the start screen (most recent first).</summary>
    public ObservableCollectionEx<RecentItem> RecentProjects { get; } = [];

    public bool HasRecents => RecentProjects.Count > 0;

    private void RefreshRecents()
    {
        RecentProjects.Clear();
        // Missing files are noise on the start screen; tests never write the real settings.
        foreach (var path in Settings.RecentProjects
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            RecentProjects.Add(new RecentItem(path));
        OnPropertyChanged(nameof(HasRecents));
    }

    partial void OnProjectChanged(Project? value)
    {
        StartDismissed = false;
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(ProjectTooltip));
        NotifyWorkspaceState();
    }

    partial void OnProjectPathChanged(string value)
    {
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(ProjectTooltip));
    }

    partial void OnSettingsChanged(AppSettings value) => OnPropertyChanged(nameof(BusInterfaceHint));

    [ObservableProperty]
    private AppSettings _settings;

    [ObservableProperty]
    private DashboardStats? _stats;

    // ---------- bus ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(BusStateText))]
    private BusConnectionState _busState = BusConnectionState.Disconnected;

    public string BusStateText => BusState switch
    {
        BusConnectionState.Connected => Strings.Get("StatusConnectedTo", BusName),
        BusConnectionState.Connecting => Strings.Get("StatusConnecting"),
        BusConnectionState.Closing => Strings.Get("StatusDisconnecting"),
        _ => Strings.Get("StatusNotConnected"),
    };

    [ObservableProperty]
    private string _busName = "";

    [ObservableProperty]
    private string _status = "Bereit";

    [ObservableProperty]
    private DeviceRow? _selectedDevice;

    [ObservableProperty]
    private GroupAddressRow? _selectedGroupAddress;

    [ObservableProperty]
    private string _sendValue = "";

    [ObservableProperty]
    private KnxDevice? _selectedDeviceOnBus;

    [ObservableProperty]
    private TreeNode? _selectedTreeNode;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editDatapointType = "";

    // ---------- recorder ----------

    public TelegramRecorder Recorder { get; }

    public ObservableCollectionEx<RecordedTelegram> Recorded { get; } = [];

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private int _recordedCount;

    // ---------- collections ----------

    public ObservableCollectionEx<GroupAddressRow> GroupAddresses { get; } = [];
    public ObservableCollectionEx<MonitorLine> Monitor { get; } = [];
    public ObservableCollectionEx<DeviceRow> Devices { get; } = [];
    public ObservableCollectionEx<KnxDevice> DiscoveredDevices { get; } = [];
    public ObservableCollectionEx<TreeNode> Tree { get; } = [];
    public ObservableCollectionEx<CheckFinding> Findings { get; } = [];
    public ObservableCollectionEx<NamedCount> DashboardLines { get; } = [];
    public ObservableCollectionEx<NamedCount> DashboardDpt { get; } = [];
    public ObservableCollectionEx<NameValueRow> Details { get; } = [];

    public bool CanSend => BusState == BusConnectionState.Connected && SelectedGroupAddress is not null;
    public bool CanDownload => BusState == BusConnectionState.Connected && SelectedDevice is not null && SelectedDeviceOnBus is not null;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    // Locale dependent labels are composed here: XAML binds to one property per label
    // instead of nesting a markup extension inside a StringFormat.
    public string SelectionText => SelectedGroupAddress is null
        ? ""
        : Strings.Get("LblSelection", SelectedGroupAddress.AddressText);

    public string DashboardProjectText => Strings.Get("DashProject", Stats?.ProjectName ?? "");

    public string FindingsText => Strings.Get("LblFindings", Findings.Count);
    public string RecordedText => Strings.Get("LblRecorded", RecordedCount);
    public string MonitorText => Strings.Get("LblTelegrams", MonitorCount);

    [ObservableProperty]
    private int _monitorCount;

    partial void OnRecordedCountChanged(int value) => OnPropertyChanged(nameof(RecordedText));

    // ---------- project lifecycle ----------

    public void LoadProject(string path)
    {
        var project = Knxproj.Load(path);
        var undo = new UndoStack();
        undo.Reset(project);
        RememberProject(path);
        Project = project;
        ClearSelection();
        ProjectPath = path;
        _undo = undo;
        RefreshRecents();
        Refresh();
        Status = $"Projekt geladen: {Project.Name}";
    }

    public void CreateProject(Project project, string? path)
    {
        var undo = new UndoStack();
        undo.Reset(project);
        if (path is not null)
        {
            Knxproj.Save(project, path);
            RememberProject(path);
        }
        Project = project;
        ClearSelection();
        ProjectPath = path ?? "";
        _undo = undo;
        if (path is not null) RefreshRecents();
        Refresh();
        Status = $"Neues Projekt '{project.Name}' angelegt";
    }

    private void RememberProject(string path)
    {
        var last = Settings.LastProject;
        var recent = Settings.RecentProjects.ToList();
        try
        {
            Settings.TouchProject(path);
            Settings.Save();
        }
        catch
        {
            Settings.LastProject = last;
            Settings.RecentProjects = recent;
            throw;
        }
    }

    public void SaveProject()
    {
        if (Project is null || ProjectPath.Length == 0) return;
        Knxproj.Save(Project, ProjectPath);
        // Keep the history, but the current state is now what is on disk.
        _undo.Push(Project);
        _undo.MarkSaved();
        NotifyEditState();
        Status = "Projekt gespeichert";
    }

    public void SaveProjectAs(string path)
    {
        if (Project is null) return;
        Knxproj.Save(Project, path);
        RememberProject(path);
        ProjectPath = path;
        RefreshRecents();
        _undo.Push(Project);
        _undo.MarkSaved();
        NotifyEditState();
        Status = "Projekt gespeichert";
    }

    // ---------- editing with undo ----------

    private void ClearSelection()
    {
        _comObjectDpt.Clear();
        SelectedDevice = null;
        SelectedGroupAddress = null;
        SelectedTreeNode = null;
    }

    /// <summary>Runs a mutation, records an undo snapshot and refreshes the views.</summary>
    public void Edit(string description, Action<Project> mutation)
    {
        if (Project is not { } project) return;
        mutation(project);
        _undo.Push(project);
        NotifyEditState();
        Refresh();
        Status = description;
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Undo() is not { } restored) return;
        Project = restored;
        NotifyEditState();
        Refresh();
        Status = "Rückgängig";
    }

    [RelayCommand]
    private void Redo()
    {
        if (_undo.Redo() is not { } restored) return;
        Project = restored;
        NotifyEditState();
        Refresh();
        Status = "Wiederhergestellt";
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    // ---------- group addresses ----------

    public void AddGroupAddress()
    {
        if (Project is not { } project) { Status = Strings.Get("UxNeedsProject"); return; }
        var installation = project.Installations.FirstOrDefault();
        if (installation is null) { Status = Strings.Get("UxNeedsProject"); return; }

        Edit("Gruppenadresse angelegt", p =>
        {
            var address = ProjectQuery.NextFreeGroupAddress(p, 1, 1);
            var range = EnsureRangeFor(p, installation, address);
            range.Addresses.Add(new GroupAddressEntry
            {
                Id = NextGroupAddressId(p),
                Name = "Neue Gruppenadresse",
                Address = address,
            });
        });
    }

    public void DeleteGroupAddress(GroupAddressRow row)
    {
        if (Project is not { } project) return;
        var id = ProjectQuery.FindGroupAddress(project, row.Address)?.Id;
        Edit($"Gruppenadresse {row.AddressText} gelöscht", p =>
        {
            foreach (var range in p.Installations.SelectMany(i => ProjectQuery.AllRanges(i)))
            {
                var entry = range.Addresses.FirstOrDefault(a => a.Address == row.Address);
                if (entry is null) continue;
                range.Addresses.Remove(entry);
                break;
            }
            if (id is null) return;
            foreach (var (_, comObject) in ProjectQuery.ComObjectRefs(p))
                comObject.Links.RemoveAll(link => link == id);
        });
    }

    public void RenameGroupAddress(GroupAddressRow row, string name, string? datapointType)
    {
        if (Project is not { } project) return;
        Edit("Gruppenadresse geändert", p =>
        {
            if (ProjectQuery.FindGroupAddress(p, row.Address) is not { } entry) return;
            entry.Name = name;
            entry.DatapointType = datapointType;
        });
    }

    // ---------- devices ----------

    public void AddDevice(Line line,string name = "Neues Gerät")
    {
        if (Project is not { } project) return;
        var installation = project.Installations.FirstOrDefault();
        if (installation is null) return;
        Edit("Gerät angelegt", p =>
        {
            var address = ProjectQuery.NextFreeDeviceAddress(p, ProjectQuery.FindAreaOf(p, line)?.Address ?? 0, line.Address);
            var device = new DeviceInstance
            {
                Id = address.ToString(),
                Address = address,
                Name = name,
                ProductRefId = "",
            };
            ProjectQuery.AddToLine(line, device);
        });
    }

    public void DeleteDevice(DeviceRow row)
    {
        if (Project is not { } project) return;
        Edit($"Gerät {row.AddressText} gelöscht", p => ProjectQuery.RemoveDevice(p, row.Device));
    }

    /// <summary>Drag &amp; drop: moves a device into another line/segment.</summary>
    public void MoveDevice(DeviceInstance device, Line target)
    {
        if (Project is not { } project) return;
        Edit($"Gerät {device.Address} verschoben", p =>
        {
            ProjectQuery.RemoveDevice(p, device);
            ProjectQuery.AddToLine(target, device);
        });
    }

    public void MoveDevice(DeviceInstance device, Segment target)
    {
        if (Project is not { } project) return;
        Edit($"Gerät {device.Address} verschoben", p =>
        {
            ProjectQuery.RemoveDevice(p, device);
            target.Devices.Add(device);
        });
    }

    // ---------- analysis ----------

    public IReadOnlyList<CheckFinding> RunCheck()
    {
        Findings.Clear();
        OnPropertyChanged(nameof(HasFindings));
        if (Project is null) { Status = Strings.Get("UxNeedsProject"); return []; }
        foreach (var finding in ProjectCheck.Run(Project)) Findings.Add(finding);
        Status = Findings.Count == 0
            ? "Projektprüfung: keine Befunde"
            : $"Projektprüfung: {Findings.Count} Befunde";
        OnPropertyChanged(nameof(HasFindings));
        return Findings;
    }

    public IReadOnlyList<LinkSuggestion> SuggestLinks()
    {
        if (Project is null) { Status = Strings.Get("UxNeedsProject"); return []; }
        var suggestions = SmartLinking.Suggest(Project, DatapointTypeOf, perObject: 1);
        Status = $"Intelligente Verknüpfung: {suggestions.Count} Vorschläge";
        return suggestions;
    }

    public int ApplyLinks(IEnumerable<LinkSuggestion> suggestions)
    {
        var list = suggestions.ToList();
        var count = list.Count;
        Edit($"{count} Verknüpfung(en) angelegt", p => SmartLinking.Apply(list));
        return count;
    }

    public bool ChangeGroupLink(DeviceInstance device, string objectId, string groupAddressId, bool remove)
    {
        if (Project is not { } project) throw new InvalidOperationException("No project loaded");
        var changed = remove ? GroupLinks.Unlink(project, device, objectId, groupAddressId)
            : GroupLinks.Link(project, device, objectId, groupAddressId);
        if (!changed) return false;
        _undo.Push(project);
        NotifyEditState();
        Refresh();
        Status = Strings.Get(remove ? "LinkRemoved" : "LinkAdded");
        return true;
    }

    public IReadOnlyList<ProductReplaceResult> ReplaceProduct(string oldRef, string newRef, string? newProgram, bool dryRun)
    {
        if (Project is null) return [];
        var results = ProductReplacer.Replace(Project, oldRef, newRef, newProgram, dryRun);
        if (!dryRun && results.Count > 0)
        {
            _undo.Push(Project);
            NotifyEditState();
            Refresh();
        }
        return results;
    }

    /// <summary>DPT of a com object, resolved from the device's KSXML when the project archive is available.</summary>
    private string? DatapointTypeOf(DeviceInstance device, ComObjectInstanceRef comObject)
        => _comObjectDpt.GetValueOrDefault((device.ProductRefId, comObject.ComObjectId))
            ?? _comObjectDpt.GetValueOrDefault((device.ProductRefId, comObject.Id));

    private readonly Dictionary<(string Product, string Object), string> _comObjectDpt = [];

    // ---------- parameters ----------

    public string ParameterWarning { get; private set; } = "";

    public (List<ParameterInstance> Instances, List<ParameterType> Types) LoadParametersFor(DeviceRow device)
    {
        ParameterWarning = "";
        var refId = device.Device.Hardware2ProgramRefId ?? device.Device.ProductRefId;
        if (string.IsNullOrWhiteSpace(refId)) return ([], []);
        try
        {
            var doc = ProjectPath.Length == 0 ? null : Knxproj.LoadApplicationProgram(ProjectPath, refId);
            List<ParameterType> types;
            List<ParameterInstance> instances;
            List<ComObject> comObjects;
            if (doc is not null)
            {
                (types, _, instances, comObjects, _) = Ksxml.Parse(doc);
                if (doc.Root is { } root) ParameterWarning = string.Join(", ", Ksxml.UnsupportedFeatures(root));
            }
            else
            {
                var program = CatalogProgram(device.Device);
                if (program is null) return ([], []);
                // Editors own their mutable values and definitions; never overlay the shared catalogue.
                var copyOptions = new System.Text.Json.JsonSerializerOptions
                {
                    PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
                };
                types = System.Text.Json.JsonSerializer.Deserialize<List<ParameterType>>(
                    System.Text.Json.JsonSerializer.Serialize(program.ParameterTypes), copyOptions)!;
                instances = System.Text.Json.JsonSerializer.Deserialize<List<ParameterInstance>>(
                    System.Text.Json.JsonSerializer.Serialize(program.ParameterInstances), copyOptions)!;
                comObjects = program.ComObjects;
                ParameterWarning = string.Join(", ", program.UnsupportedFeatures);
            }
            foreach (var comObject in comObjects)
                if (comObject.DatapointType is { Length: > 0 })
                    _comObjectDpt[(device.Device.ProductRefId, comObject.Id)] = comObject.DatapointType;
            foreach (var instance in instances)
                if (device.Device.ParameterValues.TryGetValue(instance.Id, out var saved)) instance.Value = saved;
            if (instances.Any(i => string.IsNullOrWhiteSpace(i.Id)) || instances.GroupBy(i => i.Id).Any(g => g.Count() > 1))
                throw new InvalidDataException("Missing or ambiguous parameter reference ids");
            return (instances.Where(i => i.ParameterTypeId is not null).ToList(), types);
        }
        catch (Exception ex)
        {
            Status = $"Parameter nicht lesbar: {ex.Message}";
            return ([], []);
        }
    }

    /// <summary>
    /// Resolves the device's application program from the shared catalogue snapshot. With a cold
    /// library the background parse starts once and this returns null immediately: the pane shows
    /// its empty state and <see cref="CatalogLoaded"/> reloads it when the snapshot is published.
    /// </summary>
    private CatalogApplicationProgram? CatalogProgram(DeviceInstance device)
    {
        if (_catalogPrograms.TryGetValue(device.ProductRefId, out var cached)) return cached;
        if (_catalog.Current is not { } snapshot)
        {
            if (!_awaitingCatalog)
            {
                _awaitingCatalog = true;
                _ = _catalog.GetAsync();
            }
            Status = Strings.Get("CatalogLoading");
            return null;
        }
        if (_awaitingCatalog)
        {
            _awaitingCatalog = false;
            if (Status == Strings.Get("CatalogLoading")) Status = Strings.Get("StatusReady");
        }
        foreach (var catalog in snapshot.Catalogs)
        {
            var product = catalog.Products.FirstOrDefault(p =>
                string.Equals(p.Id, device.ProductRefId, StringComparison.Ordinal));
            if (product is null || catalog.FindProgramFor(product) is not { } program) continue;
            // ponytail: resolved once per product ref; a re-imported catalogue keeps the first
            // program until restart. Restart is when the cache is rebuilt anyway.
            _catalogPrograms[device.ProductRefId] = program;
            return program;
        }
        return null;
    }

    public bool ApplyParameterValues(DeviceInstance device, IReadOnlyList<ParameterInstance> instances,
        IReadOnlyList<ParameterType> types, Dictionary<string, string?> baseline)
    {
        if (Project is not { } project || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device)))
            throw new InvalidOperationException(Strings.Get("LinkProjectChanged"));
        if (instances.Select(i => i.Id).Distinct().Count() != instances.Count)
            throw new InvalidDataException("Ambiguous parameter reference ids");
        var changes = instances.Where(i => !baseline.TryGetValue(i.Id, out var before)
            || before != i.Value).ToList();
        var map = types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var instance in changes)
        {
            instance.ParameterType ??= instance.ParameterTypeId is { } id ? map.GetValueOrDefault(id) : null;
            if (!ParameterValidation.TryValidate(instance, instance.Value, out var error))
                throw new ArgumentException($"{instance.Name}: {error}");
        }
        var changed = changes.Any(i => !device.ParameterValues.TryGetValue(i.Id, out var saved) || saved != i.Value);
        if (changed)
        {
            foreach (var instance in changes) device.ParameterValues[instance.Id] = instance.Value!;
            _undo.Push(project);
            NotifyEditState();
            Refresh();
            Status = Strings.Get("ParameterSaved");
        }
        foreach (var instance in instances) baseline[instance.Id] = instance.Value;
        return changed;
    }

    // ---------- bus ----------

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.BusInterface))
        {
            Status = Strings.Get("UxNeedsInterface");
            return;
        }
        _bus ??= new KnxBus();
        DiscoveredDevices.Clear();
        foreach (var device in await _bus.DiscoverAsync(host: Settings.BusInterface)) DiscoveredDevices.Add(device);
        Status = DiscoveredDevices.Count == 0 ? "keine Geräte gefunden" : $"{DiscoveredDevices.Count} Geräte gefunden";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.BusInterface))
        {
            Status = Strings.Get("UxNeedsInterface");
            return;
        }
        if (SelectedDeviceOnBus is null) { Status = "kein Gerät ausgewählt"; return; }
        _bus ??= new KnxBus();
        _bus.TelegramReceived += OnTelegram;
        var address = IndividualAddress.TryParse(Settings.TunnelAddress, out var ia)
            ? ia
            : new IndividualAddress(1, 1, 250);
        await _bus.ConnectTunnelAsync(SelectedDeviceOnBus, address);
        StateChanged();
        Status = $"Verbunden mit {SelectedDeviceOnBus.Name}";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_bus is not null)
        {
            _bus.TelegramReceived -= OnTelegram;
            await _bus.DisposeAsync();
        }
        StateChanged();
        Status = "Getrennt";
    }

    [RelayCommand]
    private async Task SendGroupValueAsync()
    {
        if (_bus is null || !CanSend) return;
        var row = SelectedGroupAddress!;
        await _bus.GroupValueWriteAsync(row.Address, Dpt.Encode(row.DatapointType, SendValue));
        Status = $"gesendet {row.AddressText} = {SendValue}";
    }

    public async Task DownloadAsync(string imagePath)
    {
        if (_bus is null || !CanDownload) return;
        var row = SelectedDevice!;
        var progress = new Progress<DownloadProgress>(p => Status = $"Download {p.Stage} {p.Percent}% – {p.Message}");
        var result = await _bus.DownloadAsync(SelectedDeviceOnBus!, row.Address, new FileProgram(imagePath), progress);
        Status = result.Success ? "Download OK" : $"Download fehlgeschlagen: {result.Message}";
    }

    private void OnTelegram(BusTelegram telegram)
    {
        Recorder.Add(telegram);
        void Update()
        {
            Monitor.Insert(0, new MonitorLine(telegram));
            if (Monitor.Count > 2000) Monitor.RemoveAt(Monitor.Count - 1);
            MonitorCount = Monitor.Count;
            if (Recorder.IsRecording && Recorder.Count > Recorded.Count)
                Recorded.Add(Recorder.Entries[^1]);
            RecordedCount = Recorder.Count;
        }

        if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Update();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(Update);
    }

    public void StartRecording()
    {
        Recorder.Start();
        Recorded.Clear();
        IsRecording = true;
        Status = "Aufzeichnung gestartet";
    }

    public void StopRecording()
    {
        Recorder.Stop();
        IsRecording = false;
        Status = $"Aufzeichnung beendet: {Recorder.Count} Telegramme";
    }

    // ---------- refresh ----------

    public void Refresh()
    {
        var project = Project;
        var deviceId = SelectedDevice?.Device.Id;
        var deviceAddress = SelectedDevice?.Address;
        var groupId = SelectedGroupAddress?.Id;
        SelectedDevice = null;
        SelectedGroupAddress = null;
        SelectedTreeNode = null;
        GroupAddresses.Clear();
        Devices.Clear();
        Tree.Clear();
        DashboardLines.Clear();
        DashboardDpt.Clear();
        if (project is null)
        {
            Stats = null;
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(HasGroupAddresses));
            OnPropertyChanged(nameof(Title));
            return;
        }

        foreach (var entry in ProjectQuery.GroupAddresses(project).OrderBy(a => a.Address.Raw))
            GroupAddresses.Add(new GroupAddressRow(entry.Address, entry.Name, entry.DatapointType, entry.Id));

        foreach (var device in ProjectQuery.DeviceList(project).OrderBy(d => d.Address.Raw))
            Devices.Add(new DeviceRow(device));

        foreach (var installation in project.Installations)
        {
            var installationNode = new TreeNode($"Installation {installation.Name}".Trim(), null, "⌂", installation)
            {
                IsExpanded = true,
            };
            foreach (var area in installation.Areas)
            {
                var areaNode = new TreeNode($"Bereich {area.Address}", area.Name, "▣", area) { IsExpanded = true };
                foreach (var device in area.Devices)
                    areaNode.Children.Add(DeviceNode(device));
                foreach (var line in area.Lines)
                {
                    var lineNode = new TreeNode($"Linie {area.Address}.{line.Address}", line.Name, "≡", line) { IsExpanded = true };
                    foreach (var device in line.Devices)
                        lineNode.Children.Add(DeviceNode(device));
                    foreach (var segment in line.Segments)
                    {
                        var segmentNode = new TreeNode($"Segment {segment.Address}", segment.Name, "▢", segment);
                        foreach (var device in segment.Devices)
                            segmentNode.Children.Add(DeviceNode(device));
                        lineNode.Children.Add(segmentNode);
                    }
                    areaNode.Children.Add(lineNode);
                }
                installationNode.Children.Add(areaNode);
            }
            Tree.Add(installationNode);
        }

        Stats = Dashboard.Build(project);
        foreach (var entry in Stats.DevicesPerLine) DashboardLines.Add(entry);
        foreach (var entry in Stats.TopDatapointTypes) DashboardDpt.Add(entry);
        if (deviceId is not null)
            SelectedDevice = Devices.FirstOrDefault(d => d.Device.Id == deviceId && d.Address == deviceAddress);
        else if (groupId is not null)
            SelectedGroupAddress = GroupAddresses.FirstOrDefault(g => g.Id == groupId);
        UpdateDetails();
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasGroupAddresses));
        OnPropertyChanged(nameof(Title));
    }

    private static TreeNode DeviceNode(DeviceInstance device)
        => new(device.Address.ToString(), device.Name, "◈", device);

    public void UpdateDetails()
    {
        Details.Clear();
        EditName = "";
        EditDatapointType = "";
        OnPropertyChanged(nameof(HasSelection));
        if (SelectedDevice is { } device)
        {
            EditName = device.Name;
            Details.Add(new("Adresse", device.AddressText));
            Details.Add(new("Name", device.Name));
            Details.Add(new("Beschreibung", device.Device.Description ?? ""));
            Details.Add(new("Produkt", device.Device.ProductRefId));
            Details.Add(new("Applikation", device.Device.Hardware2ProgramRefId ?? ""));
            Details.Add(new("Kommunikationsobjekte", device.Device.ComObjectRefs.Count.ToString()));
            Details.Add(new("Verknüpfungen", device.Device.ComObjectRefs.Sum(r => r.Links.Count).ToString()));
            return;
        }
        if (SelectedGroupAddress is { } row)
        {
            EditName = row.Name;
            EditDatapointType = row.DatapointType ?? "";
            Details.Add(new("Adresse", row.AddressText));
            Details.Add(new("Name", row.Name));
            Details.Add(new("Datapoint-Typ", row.DatapointType ?? ""));
            var entry = Project is { } p ? ProjectQuery.FindGroupAddress(p, row.Address) : null;
            Details.Add(new("Beschreibung", entry?.Description ?? ""));
            Details.Add(new("Kommentar", entry?.Comment ?? ""));
            var links = entry is null || Project is null
                ? 0
                : ProjectQuery.ComObjectRefs(Project).Count(r => r.Ref.Links.Contains(entry.Id));
            Details.Add(new("Verknüpfte Objekte", links.ToString()));
        }
    }

    /// <summary>Selects the model object behind a topology tree node.</summary>
    public void SelectNode(TreeNode? node)
    {
        SelectedTreeNode = node;
        switch (node?.Tag)
        {
            case DeviceInstance device:
                SelectedDevice = Devices.FirstOrDefault(d => d.Device == device);
                break;
            case Line line:
            case Segment:
            case Area:
            case Installation:
                SelectedDevice = null;
                SelectedGroupAddress = null;
                break;
        }
    }

    /// <summary>Applies the inline editors of the details pane.</summary>
    public void ApplyDetailsEdit()
    {
        if (SelectedGroupAddress is { } row)
            RenameGroupAddress(row, EditName, string.IsNullOrWhiteSpace(EditDatapointType) ? null : EditDatapointType);
        else if (SelectedDevice is { } device && device.Name != EditName && EditName.Trim().Length > 0)
            Edit($"Gerät {device.AddressText} umbenannt", p =>
            {
                if (ProjectQuery.FindDevice(p, device.Address) is { } target) target.Name = EditName;
            });
    }

    public async Task ReadGroupValueAsync(GroupAddressRow row)
    {
        if (_bus is null) throw new InvalidOperationException("keine Busverbindung");
        await _bus.GroupValueReadAsync(row.Address);
    }

    public void LoadRecording(IReadOnlyList<RecordedTelegram> entries)
    {
        Recorded.Clear();
        foreach (var entry in entries) Recorded.Add(entry);
        RecordedCount = entries.Count;
        Status = $"{entries.Count} Telegramme geladen";
    }

    private void StateChanged()
    {
        BusState = _bus?.State ?? BusConnectionState.Disconnected;
        BusName = _bus?.DeviceName ?? "";
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(BusStateText));
    }

    partial void OnSelectedGroupAddressChanged(GroupAddressRow? value)
    {
        if (value is not null) SelectedDevice = null;
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SelectionText));
        UpdateDetails();
    }

    partial void OnSelectedDeviceChanged(DeviceRow? value)
    {
        if (value is not null) SelectedGroupAddress = null;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(HasDeviceSelected));
        UpdateDetails();
    }

    partial void OnSelectedDeviceOnBusChanged(KnxDevice? value) => OnPropertyChanged(nameof(CanDownload));

    /// <summary>Test hook: feeds a telegram into the monitor and recorder pipeline.</summary>
    public void SimulateTelegram(BusTelegram telegram) => OnTelegram(telegram);

    // ---------- helpers ----------

    private static string NextGroupAddressId(Project project)
    {
        var max = ProjectQuery.GroupAddresses(project)
            .Select(a => int.TryParse(a.Id.Replace("GA-", ""), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"GA-{max + 1}";
    }

    private static GroupRange EnsureRangeFor(Project project, Installation installation, GroupAddress address)
    {
        var main = installation.MainRanges.FirstOrDefault(r => r.RangeStart == address.Main || r.RangeEnd == address.Main);
        if (main is null)
        {
            main = new GroupRange
            {
                Id = $"GR-{installation.MainRanges.Count + 1}",
                Name = $"Hauptbereich {address.Main}",
                RangeStart = address.Main,
                RangeEnd = address.Main,
            };
            installation.MainRanges.Add(main);
        }
        var middle = main.Ranges.FirstOrDefault(r => r.RangeStart == address.Middle || r.RangeEnd == address.Middle);
        if (middle is null)
        {
            middle = new GroupRange
            {
                Id = $"{main.Id}-{address.Middle}",
                Name = $"Mittelbereich {address.Main}/{address.Middle}",
                RangeStart = address.Middle,
                RangeEnd = address.Middle,
            };
            main.Ranges.Add(middle);
        }
        return middle;
    }
}

public sealed record GroupAddressRow(GroupAddress Address, string Name, string? DatapointType, string Id)
{
    public string AddressText => Address.ToString();
}

public sealed record MonitorLine(BusTelegram Telegram)
{
    public string Text =>
        $"{Telegram.Timestamp:HH:mm:ss.fff}  {Telegram.Telegram.Source,-9} " +
        $"{(Telegram.Telegram.IsGroupAddress ? "→" : "·")} {Telegram.Telegram.Destination,-11} " +
        $"{Telegram.Telegram.Apci,-14} {Convert.ToHexString(Telegram.Telegram.Data)}";
}

public sealed record DeviceRow(DeviceInstance Device)
{
    public string Name => Device.Name ?? Device.Address.ToString();
    public string AddressText => Device.Address.ToString();
    public IndividualAddress Address => Device.Address;
}

public sealed record NameValueRow(string Name, string? Value)
{
    public string DisplayValue => Value ?? "";
}

/// <summary>Entry of the recent-project list on the start screen.</summary>
public sealed record RecentItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>Node of the topology tree; Tag holds the underlying model object.</summary>
public sealed partial class TreeNode : ObservableObject
{
    public TreeNode(string name, string? subtitle, string icon, object tag)
    {
        Name = name;
        Subtitle = subtitle;
        Icon = icon;
        Tag = tag;
    }

    public string Name { get; }
    public string? Subtitle { get; }
    public string Icon { get; }
    public object Tag { get; }
    public ObservableCollection<TreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;
}

public sealed class ObservableCollectionEx<T> : ObservableCollection<T> { }

internal sealed class FileProgram : IApplicationProgram
{
    private readonly byte[] _image;

    public FileProgram(string path)
    {
        _image = File.ReadAllBytes(path);
        Name = Path.GetFileName(path);
    }

    public string Name { get; }
    public byte[] Image => _image;
    public ushort StartAddress => 0x0060;
    public int ChunkSize => 12;
}
