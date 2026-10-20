using System.Text.Json;
using Knx.App.Services;

namespace Knx.App.Workbench;

/// <summary>Read-only projections of the native project. Targets resolve again after undo replaces it.</summary>
public static class WorkbenchProjection
{
    public static IEnumerable<SpaceNode> Spaces(Project p) => p.Installations.SelectMany(i => i.Locations.AllNodes);
    public static IEnumerable<Line> Lines(Project p) => p.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines);
    public static object? Resolve(Project? p, WorkbenchTarget target) => p is null ? null : target.Kind switch
    {
        "project" => p.Id == target.Id ? p : null,
        "room" => Spaces(p).FirstOrDefault(s => s.Id == target.Id),
        "line" => Lines(p).FirstOrDefault(l => l.Id == target.Id),
        "device" => ProjectQuery.DeviceList(p).FirstOrDefault(d => d.Id == target.Id),
        "ga" => ProjectQuery.GroupAddresses(p).FirstOrDefault(g => g.Id == target.Id),
        _ => null
    };

    public static IReadOnlyList<DeviceInstance> Devices(Project p, WorkbenchTarget target)
    {
        var all = ProjectQuery.DeviceList(p).ToList();
        return Resolve(p, target) switch
        {
            DeviceInstance d => [d],
            SpaceNode s => all.Where(d => BuildingTree.DevicesUnder(s).Contains(d.Id)).ToList(),
            Line l => l.Devices.Concat(l.Segments.SelectMany(s => s.Devices)).ToList(),
            GroupAddressEntry g => all.Where(d => d.ComObjectRefs.Any(o => o.Links.Contains(g.Id))).ToList(),
            Project => all,
            _ => []
        };
    }

    public static string ObjectKey(DeviceInstance d, ComObjectInstanceRef o) => JsonSerializer.Serialize(new[] { d.Id, o.Id });

    public static IReadOnlyList<WorkbenchRow> Rows(Project p, WorkbenchTab tab)
    {
        var addresses = new Dictionary<string, GroupAddressEntry>(StringComparer.Ordinal);
        foreach (var entry in ProjectQuery.GroupAddresses(p)) addresses.TryAdd(entry.Id, entry);
        var result = new List<WorkbenchRow>();
        var ga = Resolve(p, tab.Target) as GroupAddressEntry;
        foreach (var d in Devices(p, tab.Target))
        {
            var objects = d.ComObjectRefs.Where(o => ga is null || o.Links.Contains(ga.Id))
                .Where(o => !tab.UnlinkedOnly || o.Links.Count == 0)
                .Where(o => Match(tab.Filter, d.Name, d.Address.ToString(), o.Text, o.FunctionText, o.Number.ToString(),
                    string.Join(" ", o.Links.Select(id => addresses.TryGetValue(id, out var g) ? $"{g.Address} {g.Name}" : id)))).ToList();
            if (objects.Count == 0 && (tab.UnlinkedOnly || tab.Filter.Length > 0 || ga is not null)) continue;
            result.Add(new("device:" + d.Id, "device", d.Name ?? d.Address.ToString(), d.Address.ToString(), d, null, 0,
                !tab.CollapsedDeviceIds.Contains(d.Id), true));
            if (tab.CollapsedDeviceIds.Contains(d.Id)) continue;
            foreach (var channel in d.Channels) AddChannel(channel, 1);
            if (d.Channels.Count > 0)
                result.Add(new("unassigned:" + d.Id, "group", "Kommunikationsobjekte · kein Kanalbezug hinterlegt", "", d, null, 1, true, false));
            foreach (var o in objects)
            {
                var links = o.Links.Distinct().Select(id => addresses.TryGetValue(id, out var g) ? g.Address.ToString() : id).ToList();
                result.Add(new(ObjectKey(d, o), "object", o.Text ?? o.FunctionText ?? o.Id, "#" + o.Number, d, o, 2,
                    false, false, string.Join("  ", links.Take(2)) + (links.Count > 2 ? $"  +{links.Count - 2}" : ""),
                    string.Join("\n", o.Links.Select(id => addresses.TryGetValue(id, out var g) ? $"{g.Address} · {g.Name}" : id))));
            }
            void AddChannel(ChannelInstance ch, int depth)
            {
                var key = "channel:" + d.Id + ":" + ch.Id;
                var expanded = !tab.CollapsedDeviceIds.Contains(key);
                result.Add(new(key, "channel", ch.Name, "", d, null, depth, expanded, ch.Channels.Count > 0));
                if (expanded) foreach (var child in ch.Channels) AddChannel(child, depth + 1);
            }
        }
        return result;
    }

    public static IReadOnlyList<WorkbenchNavNode> Navigation(Project p, string perspective)
    {
        var devices = ProjectQuery.DeviceList(p).ToList();
        WorkbenchNavNode Device(DeviceInstance d) => new("device:" + d.Id, d.Name ?? d.Address.ToString(), d.Address.ToString(),
            d, new("device", d.Id), []);
        WorkbenchNavNode Space(SpaceNode s) => new("space:" + s.Id, s.Name, s.Kind.ToString(), s, new("room", s.Id),
            s.Children.Select(Space).Concat(s.DeviceRefs.Select(id => devices.FirstOrDefault(d => d.Id == id))
                .OfType<DeviceInstance>().Select(Device)).ToList());
        WorkbenchNavNode Range(GroupRange g) => new("range:" + g.Id, g.Name ?? g.Id, "", g, null,
            g.Ranges.Select(Range).Concat(g.Addresses.Select(a => new WorkbenchNavNode("ga:" + a.Id, a.Name, a.Address.ToString(), a,
                new("ga", a.Id), devices.Where(d => d.ComObjectRefs.Any(o => o.Links.Contains(a.Id))).Select(Device).ToList()))).ToList());
        var children = perspective switch
        {
            "building" => p.Installations.SelectMany(i => i.Locations.Roots).Select(Space).Concat(
                [new WorkbenchNavNode("unassigned", "Ohne Einbauort", "", p, null, p.UnassignedDevices().Select(Device).ToList())]).ToList(),
            "topology" => p.Installations.SelectMany(i => i.Areas).Select(a => new WorkbenchNavNode("area:" + a.Id,
                $"Bereich {a.Address} · {a.Name}", "", a, null, a.Lines.Select(l => new WorkbenchNavNode("line:" + l.Id,
                    l.Name ?? "Linie", $"{a.Address}.{l.Address}", l, new("line", l.Id),
                    l.Devices.Select(Device).Concat(l.Segments.Select(s => new WorkbenchNavNode("segment:" + s.Id,
                        s.Name ?? "Segment", s.Address.ToString(), s, null, s.Devices.Select(Device).ToList()))).ToList()))
                    .Concat(a.Devices.Select(Device)).ToList())).ToList(),
            _ => p.Installations.SelectMany(i => i.MainRanges).Select(Range).ToList()
        };
        return [new("project:" + p.Id, p.Name, "", p, new("project", p.Id), children)];
    }

    public static IEnumerable<(WorkbenchNavNode Node, int Depth)> VisibleNavigation(IEnumerable<WorkbenchNavNode> roots,
        WorkbenchNavigatorState state, int depth = 0)
    {
        foreach (var node in roots)
        {
            if (state.Query.Length > 0)
            {
                if (Match(state.Query, node.Name, node.Meta)) yield return (node, depth);
                foreach (var child in VisibleNavigation(node.Children, state, depth + 1)) yield return child;
            }
            else
            {
                yield return (node, depth);
                if (state.ExpandedIds.Contains(node.Key))
                    foreach (var child in VisibleNavigation(node.Children, state, depth + 1)) yield return child;
            }
        }
    }

    private static bool Match(string query, params string?[] fields) => string.IsNullOrWhiteSpace(query)
        || fields.Any(f => f?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);
}

public sealed record WorkbenchNavNode(string Key, string Name, string Meta, object Model, WorkbenchTarget? Target,
    IReadOnlyList<WorkbenchNavNode> Children);

public sealed record WorkbenchRow(string Key, string Kind, string Name, string Address, DeviceInstance Device,
    ComObjectInstanceRef? Object, int Depth, bool Expanded, bool CanExpand, string Links = "", string LinkDetails = "");
