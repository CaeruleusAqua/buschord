using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Services;

namespace Knx.App.Views;

/// <summary>
/// Building/room workspace as an embedded pane: spaces of the first installation on the left,
/// devices of the selected space and unassigned devices on the right. Every change runs through
/// <see cref="MainWindowViewModel.Edit(string, Action{Project})"/>, so undo/redo covers it.
/// Project editing only; no bus traffic.
/// </summary>
/// <remarks>
/// Deleting a space keeps its children: they move up to the deleted space's parent (devices on
/// those children stay assigned, devices installed directly in the deleted space become
/// unassigned). Names are not checked for uniqueness, only for being non-blank. Spaces are taken
/// from the first installation; unassigned devices are project-wide.
/// </remarks>
public sealed partial class BuildingPane : UserControl
{
    private readonly MainWindowViewModel? _vm;
    private Project? _project;
    private string? _selectedSpaceId;
    private string? _selectedDeviceId;
    private bool _suspend;

    public BuildingPane()
    {
        InitializeComponent();
        FillKinds();
        Refresh();
    }

    public BuildingPane(MainWindowViewModel vm) : this()
    {
        _vm = vm;
        Refresh();
    }

    /// <summary>Space selected in the tree, resolved in the current project; null without selection.</summary>
    public SpaceNode? SelectedSpace => _project is { } project && _selectedSpaceId is { } id
        ? TreeOf(project)?.Find(id)
        : null;

    /// <summary>Re-resolves project, tree and lists, e.g. after undo/redo replaced the project instance.</summary>
    public void Refresh()
    {
        _project = _vm?.Project;
        var project = _project;
        EmptyProject.IsVisible = project is null;
        Body.IsVisible = project is not null;
        if (project is null)
        {
            Tree.ItemsSource = null;
            SpaceDevices.ItemsSource = null;
            FreeDevices.ItemsSource = null;
            StatusText.Text = "";
            return;
        }

        var tree = TreeOf(project);
        if (_selectedSpaceId is { } id && tree?.Find(id) is null) _selectedSpaceId = null;

        var query = Query;
        var rows = tree is null ? new List<SpaceTree>() : BuildRows(tree.Roots, query, includeSubtree: false);
        _suspend = true;
        Tree.ItemsSource = rows;
        Tree.SelectedItem = rows.SelectMany(Flatten).FirstOrDefault(row => row.Node.Id == _selectedSpaceId);
        SpacesEmpty.IsVisible = tree is null || tree.IsEmpty;
        _suspend = false;

        FillDevices(project, query);
        NameBox.Text = SelectedSpace?.Name ?? "";
        UpdateButtons();
    }

    private static BuildingTree? TreeOf(Project project) => project.Installations.FirstOrDefault()?.Locations;

    private string Query => SearchBox.Text?.Trim() ?? "";

    private static bool Matches(string? value, string query)
        => query.Length == 0 || value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    // ---------- space tree ----------

    private List<SpaceTree> BuildRows(IEnumerable<SpaceNode> nodes, string query, bool includeSubtree)
    {
        var rows = new List<SpaceTree>();
        foreach (var node in nodes)
        {
            var self = includeSubtree || Matches(node.Name, query) || Matches(KindLabel(node.Kind), query);
            var children = BuildRows(node.Children, query, self);
            if (!self && children.Count == 0) continue;
            var count = BuildingTree.DevicesUnder(node).Count();
            rows.Add(new SpaceTree(node, KindLabel(node.Kind), count > 0 ? $"({count})" : "", children));
        }
        return rows;
    }

    private static IEnumerable<SpaceTree> Flatten(SpaceTree row)
        => new[] { row }.Concat(row.Children.SelectMany(Flatten));

    private static string KindLabel(SpaceKind kind) => Strings.Get(kind switch
    {
        SpaceKind.Building => "BuildingKindBuilding",
        SpaceKind.BuildingPart => "BuildingKindBuildingPart",
        SpaceKind.Floor => "BuildingKindFloor",
        SpaceKind.Room => "BuildingKindRoom",
        SpaceKind.DistributionBoard => "BuildingKindDistributionBoard",
        SpaceKind.Stairway => "BuildingKindStairway",
        SpaceKind.Corridor => "BuildingKindCorridor",
        SpaceKind.Area => "BuildingKindArea",
        SpaceKind.Ground => "BuildingKindGround",
        SpaceKind.Segment => "BuildingKindSegment",
        SpaceKind.Function => "BuildingKindFunction",
        _ => "BuildingKindUnknown",
    });

    /// <summary>Kinds offered for new spaces; <see cref="SpaceKind.Unknown"/> is read-only data.</summary>
    private void FillKinds()
    {
        var kinds = new List<SpaceKind>
        {
            SpaceKind.Building, SpaceKind.BuildingPart, SpaceKind.Floor, SpaceKind.Room,
            SpaceKind.DistributionBoard, SpaceKind.Stairway, SpaceKind.Corridor, SpaceKind.Area,
            SpaceKind.Ground, SpaceKind.Segment, SpaceKind.Function,
        };
        KindBox.ItemsSource = kinds.Select(kind => new KindChoice(kind, KindLabel(kind))).ToList();
        KindBox.SelectedIndex = 0;
    }

    /// <summary>Kind proposed for a child of the given node (ETS hierarchy: building → floor → room).</summary>
    private static SpaceKind ChildKind(SpaceKind parent) => parent switch
    {
        SpaceKind.Building or SpaceKind.BuildingPart or SpaceKind.Area or SpaceKind.Ground => SpaceKind.Floor,
        _ => SpaceKind.Room,
    };

    private void SelectKind(SpaceKind kind)
    {
        if (KindBox.ItemsSource is not IEnumerable<KindChoice> choices) return;
        KindBox.SelectedItem = choices.FirstOrDefault(choice => choice.Kind == kind);
    }

    // ---------- device lists ----------

    private void FillDevices(Project project, string query)
    {
        var devices = ProjectQuery.DeviceList(project).ToDictionary(device => device.Id, StringComparer.Ordinal);
        var space = SelectedSpace;
        var inSpace = new List<DeviceRow>();
        if (space is not null)
            foreach (var id in BuildingTree.DevicesUnder(space).Distinct())
                if (devices.TryGetValue(id, out var device) && MatchesDevice(device, query))
                    inSpace.Add(new DeviceRow(device));
        var free = project.UnassignedDevices()
            .Where(device => MatchesDevice(device, query))
            .OrderBy(device => device.Address.Raw)
            .Select(device => new DeviceRow(device))
            .ToList();

        _suspend = true;
        SpaceDevices.ItemsSource = inSpace.OrderBy(row => row.Address.Raw).ToList();
        FreeDevices.ItemsSource = free;
        SpaceDevices.SelectedItem = inSpace.FirstOrDefault(row => row.Device.Id == _selectedDeviceId);
        FreeDevices.SelectedItem = free.FirstOrDefault(row => row.Device.Id == _selectedDeviceId);
        if (SpaceDevices.SelectedItem is null && FreeDevices.SelectedItem is null) _selectedDeviceId = null;
        SpaceDeviceEmpty.Text = space is null ? Strings.Get("BuildingSelectSpace") : Strings.Get("BuildingNoDevicesInSpace");
        SpaceDeviceEmpty.IsVisible = inSpace.Count == 0;
        FreeDeviceEmpty.Text = Strings.Get("BuildingNoUnassigned");
        FreeDeviceEmpty.IsVisible = free.Count == 0;
        _suspend = false;
    }

    private static bool MatchesDevice(DeviceInstance device, string query)
        => Matches(device.Name, query) || Matches(device.Address.ToString(), query) || Matches(device.Id, query);

    private DeviceRow? SelectedDeviceRow => (SpaceDevices.SelectedItem ?? FreeDevices.SelectedItem) as DeviceRow;

    private void UpdateButtons()
    {
        var space = SelectedSpace;
        var device = SelectedDeviceRow;
        AddButton.IsEnabled = _project is not null;
        RenameButton.IsEnabled = space is not null;
        DeleteButton.IsEnabled = space is not null;
        AssignButton.IsEnabled = space is not null && device is not null;
        UnassignButton.IsEnabled = device is not null && _project is not null
            && TreeOf(_project)?.FindDevice(device.Device.Id) is not null;
    }

    // ---------- events ----------

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suspend || _project is null) return;
        Refresh();
    }

    private void OnSpaceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        _selectedSpaceId = (Tree.SelectedItem as SpaceTree)?.Node.Id;
        _selectedDeviceId = null;
        _suspend = true;
        SpaceDevices.SelectedItem = null;
        FreeDevices.SelectedItem = null;
        _suspend = false;
        if (SelectedSpace is { } space)
        {
            NameBox.Text = space.Name;
            SelectKind(ChildKind(space.Kind));
        }
        else
        {
            NameBox.Text = "";
            SelectKind(SpaceKind.Building);
        }
        if (_project is { } project) FillDevices(project, Query);
        UpdateButtons();
    }

    private void OnSpaceDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (SpaceDevices.SelectedItem is DeviceRow row)
        {
            _selectedDeviceId = row.Device.Id;
            _suspend = true;
            FreeDevices.SelectedItem = null;
            _suspend = false;
        }
        UpdateButtons();
    }

    private void OnFreeDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (FreeDevices.SelectedItem is DeviceRow row)
        {
            _selectedDeviceId = row.Device.Id;
            _suspend = true;
            SpaceDevices.SelectedItem = null;
            _suspend = false;
        }
        UpdateButtons();
    }

    // ---------- edits ----------

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { Project: not null } vm) return;
        var parentId = SelectedSpace?.Id;
        var kind = (KindBox.SelectedItem as KindChoice)?.Kind ?? SpaceKind.Building;
        var name = NameBox.Text?.Trim();
        if (name is not { Length: > 0 }) name = Strings.Get("BuildingNewSpace");

        var newId = "";
        try
        {
            vm.Edit($"Bereich '{name}' angelegt", project =>
            {
                var tree = TreeOf(project) ?? throw new InvalidOperationException("Kein Bereichsbaum");
                var parent = parentId is null ? null : tree.Find(parentId)
                    ?? throw new InvalidOperationException("Bereich nicht gefunden");
                newId = tree.Add(parent, name, kind).Id;
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        _selectedSpaceId = newId;
        NameBox.Text = "";
        Refresh();
        StatusText.Text = vm.Status;
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { Project: not null } vm) return;
        if (SelectedSpace is not { } space) { StatusText.Text = Strings.Get("BuildingSelectSpace"); return; }
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            StatusText.Text = Strings.Get("BuildingNameEmpty");
            return;
        }

        var id = space.Id;
        try
        {
            vm.Edit($"Bereich umbenannt in '{name}'", project =>
            {
                var node = TreeOf(project)?.Find(id) ?? throw new InvalidOperationException("Bereich nicht gefunden");
                BuildingEdits.Rename(node, name);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        Refresh();
        StatusText.Text = vm.Status;
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { Project: not null } vm) return;
        if (SelectedSpace is not { } space) { StatusText.Text = Strings.Get("BuildingSelectSpace"); return; }
        var id = space.Id;
        var name = space.Name;
        var parentId = space.Parent?.Id;
        try
        {
            vm.Edit($"Bereich '{name}' gelöscht", project =>
            {
                var tree = TreeOf(project) ?? throw new InvalidOperationException("Kein Bereichsbaum");
                var node = tree.Find(id) ?? throw new InvalidOperationException("Bereich nicht gefunden");
                BuildingEdits.RemoveKeepingChildren(tree, node);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        _selectedSpaceId = parentId;
        _selectedDeviceId = null;
        Refresh();
        StatusText.Text = vm.Status;
    }

    private void OnAssign(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { Project: not null } vm) return;
        if (SelectedSpace is not { } space) { StatusText.Text = Strings.Get("BuildingSelectSpace"); return; }
        if (SelectedDeviceRow is not { } row) { StatusText.Text = Strings.Get("UxNeedsDevice"); return; }
        var spaceId = space.Id;
        var device = row.Device;
        try
        {
            vm.Edit($"Gerät {device.Address} zugewiesen", project =>
            {
                var tree = TreeOf(project) ?? throw new InvalidOperationException("Kein Bereichsbaum");
                var node = tree.Find(spaceId) ?? throw new InvalidOperationException("Bereich nicht gefunden");
                tree.Assign(node, device.Id);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        _selectedDeviceId = device.Id;
        Refresh();
        StatusText.Text = vm.Status;
    }

    private void OnUnassign(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { Project: not null } vm) return;
        if (SelectedDeviceRow is not { } row) { StatusText.Text = Strings.Get("UxNeedsDevice"); return; }
        var device = row.Device;
        try
        {
            vm.Edit($"Gerät {device.Address} entfernt", project =>
            {
                var tree = TreeOf(project) ?? throw new InvalidOperationException("Kein Bereichsbaum");
                tree.FindDevice(device.Id)?.UnassignDevice(device.Id);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        Refresh();
        StatusText.Text = vm.Status;
    }
}

/// <summary>Row of the space tree; <see cref="Node"/> is the model object of the current project.</summary>
public sealed record SpaceTree(SpaceNode Node, string KindText, string CountText, IReadOnlyList<SpaceTree> Children)
{
    /// <summary>Expansion is view-only; every row starts expanded after a rebuild.</summary>
    public bool IsExpanded { get; set; } = true;
}

/// <summary>Entry of the kind selector for new spaces.</summary>
public sealed record KindChoice(SpaceKind Kind, string Label);
