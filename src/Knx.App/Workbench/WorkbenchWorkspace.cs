using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Knx.App.Workbench;

/// <summary>A stable work item, not a view instance or the navigator's current selection.</summary>
public sealed record WorkbenchTarget(string Kind, string Id)
{
    public string Key => JsonSerializer.Serialize(new[] { Kind, Id });
    public bool IsValid => Kind is "project" or "room" or "line" or "device" or "ga" or "tool"
        && !string.IsNullOrWhiteSpace(Id) && Id.Length <= 1024;
}

public sealed partial class WorkbenchTab : ObservableObject
{
    public WorkbenchTab(WorkbenchTarget target, string title)
    {
        if (!target.IsValid) throw new ArgumentException("Invalid work item.", nameof(target));
        Target = target;
        _title = title;
    }

    public WorkbenchTarget Target { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _view = "objects";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _includeRelated = true;
    [ObservableProperty] private bool _unlinkedOnly;
    [ObservableProperty] private double _scrollOffset;
    public ObservableCollection<string> SelectedObjectIds { get; } = [];
    public HashSet<string> CollapsedDeviceIds { get; } = new(StringComparer.Ordinal);
}

public sealed partial class WorkbenchNavigatorState : ObservableObject
{
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _selectedId;
    [ObservableProperty] private double _scrollOffset;
    public HashSet<string> ExpandedIds { get; } = new(StringComparer.Ordinal);
}

/// <summary>Workspace lifetime state. Controls can disappear without closing work items or sessions.</summary>
public sealed partial class WorkbenchWorkspace : ObservableObject
{
    public WorkbenchWorkspace(string projectId = "") => ProjectId = projectId;

    public string ProjectId { get; private set; }
    public ObservableCollection<WorkbenchTab> Tabs { get; } = [];
    public IReadOnlyDictionary<string, WorkbenchNavigatorState> Navigators { get; } =
        new Dictionary<string, WorkbenchNavigatorState>(StringComparer.Ordinal)
        {
            ["building"] = new(), ["topology"] = new(), ["groups"] = new()
        };
    [ObservableProperty] private WorkbenchTab? _activeTab;
    [ObservableProperty] private string _perspective = "building";
    [ObservableProperty] private bool _navigatorVisible = true;
    [ObservableProperty] private bool _inspectorVisible;
    [ObservableProperty] private bool _toolsVisible;
    [ObservableProperty] private bool _toolsMaximized;
    [ObservableProperty] private string _activeTool = "monitor";
    [ObservableProperty] private double _navigatorWidth = 272;
    [ObservableProperty] private double _inspectorWidth = 350;
    [ObservableProperty] private double _toolsHeight = 242;

    public WorkbenchTab Open(WorkbenchTarget target, string title, string? view = null)
    {
        if (!target.IsValid) throw new ArgumentException("Invalid work item.", nameof(target));
        var tab = Tabs.FirstOrDefault(t => t.Target == target);
        if (tab is null) { tab = new(target, title); Tabs.Add(tab); }
        else tab.Title = title;
        if (view is not null) tab.View = view;
        ActiveTab = tab;
        return tab;
    }

    public void Close(WorkbenchTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        var active = ReferenceEquals(ActiveTab, tab);
        Tabs.RemoveAt(index);
        if (active) ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public void ChangeProject(string projectId)
    {
        if (projectId == ProjectId) return;
        ProjectId = projectId;
        Tabs.Clear(); ActiveTab = null;
        foreach (var nav in Navigators.Values)
        {
            nav.Query = ""; nav.SelectedId = null; nav.ScrollOffset = 0; nav.ExpandedIds.Clear();
        }
        OnPropertyChanged(nameof(ProjectId));
    }

    public void ResetLayout()
    {
        NavigatorVisible = true; InspectorVisible = false; ToolsVisible = false; ToolsMaximized = false;
        NavigatorWidth = 272; InspectorWidth = 350; ToolsHeight = 242;
    }

    public void FocusLayout()
    {
        NavigatorVisible = false; InspectorVisible = false; ToolsVisible = false; ToolsMaximized = false;
    }

    public void SaveLayout(string path)
    {
        var data = new WorkspaceLayout
        {
            ProjectId = ProjectId, Perspective = Perspective,
            NavigatorVisible = NavigatorVisible, InspectorVisible = InspectorVisible,
            ToolsVisible = ToolsVisible, ActiveTool = ActiveTool,
            NavigatorWidth = Size(NavigatorWidth, 200, 400, 272),
            InspectorWidth = Size(InspectorWidth, 290, 480, 350),
            ToolsHeight = Size(ToolsHeight, 140, 1000, 242), ActiveTarget = ActiveTab?.Target,
            Tabs = Tabs.Select(t => (TabLayout?)new TabLayout
            {
                Target = t.Target, Title = t.Title, View = t.View, Filter = t.Filter,
                IncludeRelated = t.IncludeRelated, UnlinkedOnly = t.UnlinkedOnly,
                ScrollOffset = Size(t.ScrollOffset, 0, 1e8, 0), CollapsedDeviceIds = t.CollapsedDeviceIds.ToArray()
            }).ToList(),
            Navigators = Navigators.ToDictionary(p => p.Key, p => (NavigatorLayout?)new NavigatorLayout
            {
                Query = p.Value.Query, SelectedId = p.Value.SelectedId,
                ScrollOffset = Size(p.Value.ScrollOffset, 0, 1e8, 0), ExpandedIds = p.Value.ExpandedIds.ToArray()
            })
        };
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Malformed or obsolete layout data cannot replace project data or break startup.</summary>
    public bool TryRestoreLayout(string path, Func<WorkbenchTarget, bool>? targetExists = null)
    {
        WorkspaceLayout? data;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return false;
            data = JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        if (data is null || data.Version != 1 || data.Tabs is null || data.Navigators is null) return false;
        NavigatorWidth = Size(data.NavigatorWidth, 200, 400, 272);
        InspectorWidth = Size(data.InspectorWidth, 290, 480, 350);
        ToolsHeight = Size(data.ToolsHeight, 140, 1000, 242);
        NavigatorVisible = data.NavigatorVisible; InspectorVisible = data.InspectorVisible;
        ToolsVisible = data.ToolsVisible; ToolsMaximized = false;
        ActiveTool = data.ActiveTool is "monitor" or "queue" or "programming" or "problems" or "changes" or "scan" ? data.ActiveTool : "monitor";
        Perspective = Navigators.ContainsKey(data.Perspective ?? "") ? data.Perspective! : "building";
        if (data.ProjectId != ProjectId) return true;
        Tabs.Clear(); ActiveTab = null;
        foreach (var stored in data.Tabs.Take(100))
        {
            if (stored?.Target is not { IsValid: true } target || targetExists?.Invoke(target) == false) continue;
            var tab = Open(target, stored.Title ?? target.Id,
                stored.View is "objects" or "params" or "relations" or "overview" ? stored.View : "objects");
            tab.Filter = stored.Filter ?? ""; tab.IncludeRelated = stored.IncludeRelated;
            tab.UnlinkedOnly = stored.UnlinkedOnly; tab.ScrollOffset = Size(stored.ScrollOffset, 0, 1e8, 0);
            tab.CollapsedDeviceIds.UnionWith((stored.CollapsedDeviceIds ?? []).Where(x => !string.IsNullOrEmpty(x)));
        }
        ActiveTab = Tabs.FirstOrDefault(t => t.Target == data.ActiveTarget) ?? Tabs.FirstOrDefault();
        foreach (var (key, stored) in data.Navigators)
        {
            if (stored is null || !Navigators.TryGetValue(key, out var nav)) continue;
            nav.Query = stored.Query ?? ""; nav.SelectedId = stored.SelectedId;
            nav.ScrollOffset = Size(stored.ScrollOffset, 0, 1e8, 0);
            nav.ExpandedIds.Clear(); nav.ExpandedIds.UnionWith((stored.ExpandedIds ?? []).Where(x => !string.IsNullOrEmpty(x)));
        }
        return true;
    }

    private static double Size(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private sealed class WorkspaceLayout
    {
        public int Version { get; set; } = 1;
        public string ProjectId { get; set; } = "";
        public string? Perspective { get; set; }
        public bool NavigatorVisible { get; set; } = true;
        public bool InspectorVisible { get; set; }
        public bool ToolsVisible { get; set; }
        public string? ActiveTool { get; set; }
        public double NavigatorWidth { get; set; } = 272;
        public double InspectorWidth { get; set; } = 350;
        public double ToolsHeight { get; set; } = 242;
        public WorkbenchTarget? ActiveTarget { get; set; }
        public List<TabLayout?>? Tabs { get; set; } = [];
        public Dictionary<string, NavigatorLayout?>? Navigators { get; set; } = [];
    }
    private sealed class TabLayout
    {
        public WorkbenchTarget? Target { get; set; }
        public string? Title { get; set; }
        public string? View { get; set; }
        public string? Filter { get; set; }
        public bool IncludeRelated { get; set; } = true;
        public bool UnlinkedOnly { get; set; }
        public double ScrollOffset { get; set; }
        public string[]? CollapsedDeviceIds { get; set; }
    }
    private sealed class NavigatorLayout
    {
        public string? Query { get; set; }
        public string? SelectedId { get; set; }
        public double ScrollOffset { get; set; }
        public string[]? ExpandedIds { get; set; }
    }
}
