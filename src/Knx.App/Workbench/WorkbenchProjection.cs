using System.Text.Json;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Workbench;

namespace Knx.App.Workbench;

/// <summary>Read-only projections of the native project. Targets resolve again after undo replaces it.</summary>
public static class WorkbenchProjection
{
    public static IEnumerable<SpaceNode> Spaces(Project p) => p.Installations.SelectMany(i => i.Locations.AllNodes);
    public static IEnumerable<Line> Lines(Project p) => p.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines);
    public static string DeviceName(Project project, DeviceInstance device) =>
        !string.IsNullOrWhiteSpace(device.Name) ? device.Name
        : project.Hardware.TryGetValue(device.ProductRefId, out var product) && !string.IsNullOrWhiteSpace(product.Name)
            ? product.Name : device.Address.ToString();
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
        var scope = tab.Target.Kind switch
        {
            "room" => new WorkbenchScope(WorkbenchScopeKind.Room, tab.Target.Id, tab.IncludeRelated),
            "line" => new WorkbenchScope(WorkbenchScopeKind.Line, tab.Target.Id),
            "device" => new WorkbenchScope(WorkbenchScopeKind.Device, tab.Target.Id),
            "ga" => new WorkbenchScope(WorkbenchScopeKind.GroupAddress, tab.Target.Id),
            _ => new WorkbenchScope(WorkbenchScopeKind.Project)
        };
        var all = WorkbenchProject.ScopeObjects(p, scope with { IncludeInactive = true });
        var result = new List<WorkbenchRow>();
        foreach (var deviceGroup in all.GroupBy(r => r.Device)
                     .OrderBy(g => g.Key.Address.Area).ThenBy(g => g.Key.Address.Line).ThenBy(g => g.Key.Address.Device))
        {
            var d = deviceGroup.Key;
            var objects = deviceGroup
                .Where(r => !tab.UnlinkedOnly || r.Object.Links.Count == 0)
                .Where(r => Match(tab.Filter, DeviceName(p, d), d.Address.ToString(), r.Object.Text, r.Object.FunctionText, r.Object.Number.ToString(),
                    string.Join(" ", r.Object.Links.Select(id => addresses.TryGetValue(id, out var g) ? $"{g.Address} {g.Name}" : id))))
                .OrderBy(r => r.Metadata?.ChannelId, StringComparer.Ordinal)
                .ThenBy(r => r.Object.Number).ToList();
            if (objects.Count == 0 && (tab.UnlinkedOnly || tab.Filter.Length > 0 || tab.Target.Kind == "ga")) continue;
            var external = objects.Any(r => r.External);
            result.Add(new("device:" + d.Id, "device", DeviceName(p, d), d.Address.ToString(), d, null, 0,
                !tab.CollapsedDeviceIds.Contains(d.Id), true, External: external));
            if (tab.CollapsedDeviceIds.Contains(d.Id)) continue;
            var hasChannelMetadata = objects.Any(r => r.Metadata?.ChannelId is not null);
            if (!hasChannelMetadata && d.Channels.Count > 0)
            {
                // Native projects have no Workbench channel metadata: keep the manufacturer hierarchy visible.
                foreach (var channel in d.Channels) AddNativeChannel(d, channel, 1);
                if (objects.Count > 0)
                    result.Add(new("unassigned:" + d.Id, "group", WorkbenchStrings.Get("WithoutChannel"), "", d, null, 1, true, false));
                foreach (var r in objects) result.Add(ObjectRowOf(r, 2, addresses));
                continue;
            }
            IEnumerable<ChannelInstance> AllChannels(IEnumerable<ChannelInstance> channels)
            {
                foreach (var channel in channels)
                {
                    yield return channel;
                    foreach (var child in AllChannels(channel.Channels)) yield return child;
                }
            }
            var channelNames = AllChannels(d.Channels).GroupBy(c => c.Id, StringComparer.Ordinal)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Name, StringComparer.Ordinal);
            foreach (var channel in objects.Where(r => r.Metadata?.ChannelId is not null)
                         .GroupBy(r => r.Metadata!.ChannelId!).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var name = channelNames.GetValueOrDefault(channel.Key);
                if (string.IsNullOrEmpty(name))
                {
                    var declaredNames = channel.Where(r => r.Metadata?.StructuralDeclaredChannelId == channel.Key)
                        .Select(r => r.Metadata?.StructuralDeclaredChannelName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                    name = declaredNames.Length == 1 ? declaredNames[0] : channel.Key;
                }
                var key = "channel:" + d.Id + ":" + channel.Key;
                var expanded = !tab.CollapsedDeviceIds.Contains(key);
                result.Add(new(key, "channel", name, "", d, null, 1, expanded, true, External: channel.Any(r => r.External)));
                if (expanded)
                    foreach (var r in channel)
                        result.Add(ObjectRowOf(r, 2, addresses));
            }
            var unassigned = objects.Where(r => r.Metadata?.ChannelId is null).ToList();
            if (unassigned.Count > 0 && d.Channels.Count > 0)
                result.Add(new("unassigned:" + d.Id, "group", WorkbenchStrings.Get("WithoutChannel"), "", d, null, 1, true, false,
                    External: unassigned.Any(r => r.External)));
            foreach (var r in unassigned) result.Add(ObjectRowOf(r, d.Channels.Count > 0 ? 2 : 1, addresses));
        }
        return result;

        WorkbenchRow ObjectRowOf(ObjectRow r, int depth, Dictionary<string, GroupAddressEntry> linked)
        {
            var links = r.Object.Links.Distinct()
                .Select(id => linked.TryGetValue(id, out var g) ? g.Address.ToString() : id).ToList();
            var metadata = r.Metadata;
            return new(ObjectKey(r.Device, r.Object), "object", r.Object.Text ?? r.Object.FunctionText ?? r.Object.Id,
                "#" + r.Object.Number, r.Device, r.Object, depth, false, false,
                string.Join("  ", links.Take(2)) + (links.Count > 2 ? $"  +{links.Count - 2}" : ""),
                string.Join("\n", r.Object.Links.Select(id => linked.TryGetValue(id, out var g) ? $"{g.Address} · {g.Name}" : id)),
                metadata?.Dpt, metadata?.Bits, metadata?.Flags, metadata?.Active != false, r.External, metadata?.ChannelId);
        }

        void AddNativeChannel(DeviceInstance device, ChannelInstance channel, int depth)
        {
            var key = "channel:" + device.Id + ":" + channel.Id;
            var expanded = !tab.CollapsedDeviceIds.Contains(key);
            result.Add(new(key, "channel", channel.Name, "", device, null, depth, expanded, channel.Channels.Count > 0));
            if (expanded) foreach (var child in channel.Channels) AddNativeChannel(device, child, depth + 1);
        }
    }

    /// <summary>
    /// Validates a device dragged onto a navigator line or segment without touching the project.
    /// The UI only mutates when <see cref="DeviceDropResult.Ok"/> is true, so an invalid drop
    /// can never move a device or allocate an address.
    /// </summary>
    public static DeviceDropResult ResolveDeviceDrop(Project p, string deviceId, WorkbenchTarget? target)
    {
        if (target is not { Kind: "line" or "segment" }) return new(DeviceDropStatus.NotATopologyTarget);
        var device = ProjectQuery.DeviceList(p).FirstOrDefault(d => d.Id == deviceId);
        if (device is null) return new(DeviceDropStatus.DeviceMissing);
        Line? line;
        Segment? segment = null;
        if (target.Kind == "line") line = Lines(p).FirstOrDefault(l => l.Id == target.Id);
        else
        {
            var placement = Lines(p).SelectMany(l => l.Segments.Select(s => (Line: l, Segment: s)))
                .FirstOrDefault(x => x.Segment.Id == target.Id);
            line = placement.Line;
            segment = placement.Segment;
        }
        if (line is null) return new(DeviceDropStatus.LineMissing);
        var currentLine = Lines(p).FirstOrDefault(l => l.Devices.Contains(device) || l.Segments.Any(s => s.Devices.Contains(device)));
        var currentSegment = currentLine?.Segments.FirstOrDefault(s => s.Devices.Contains(device));
        if (ReferenceEquals(currentLine, line) && ReferenceEquals(currentSegment, segment))
            return new(DeviceDropStatus.AlreadyThere);
        return new(DeviceDropStatus.Ok, line, segment);
    }

    public static IReadOnlyList<WorkbenchNavNode> Navigation(Project p, string perspective)
    {
        var devices = ProjectQuery.DeviceList(p).ToList();
        WorkbenchNavNode Device(DeviceInstance d) => new("device:" + d.Id, DeviceName(p, d), d.Address.ToString(),
            d, new("device", d.Id), []);
        WorkbenchNavNode Space(SpaceNode s) => new("space:" + s.Id, s.Name,
            BuildingTree.DevicesUnder(s).Count() is var count && count > 0 ? count.ToString() : "",
            s, new("room", s.Id),
            s.Children.Select(Space).Concat(s.DeviceRefs.Select(id => devices.FirstOrDefault(d => d.Id == id))
                .OfType<DeviceInstance>().Select(Device)).ToList());
        WorkbenchNavNode Range(GroupRange g) => new("range:" + g.Id, g.Name ?? g.Id,
            g.Addresses.Count > 0 ? g.Addresses.Count.ToString() : "", g, null,
            g.Ranges.Select(Range).Concat(g.Addresses.Select(a => new WorkbenchNavNode("ga:" + a.Id, a.Name, a.Address.ToString(), a,
                new("ga", a.Id), devices.Where(d => d.ComObjectRefs.Any(o => o.Links.Contains(a.Id))).Select(Device).ToList()))).ToList());
        var children = perspective switch
        {
            "building" => p.Installations.SelectMany(i => i.Locations.Roots).Select(Space).Concat(
                [new WorkbenchNavNode("unassigned", WorkbenchStrings.Get("WithoutInstallationLocation"), "", p, null, p.UnassignedDevices().Select(Device).ToList())]).ToList(),
            "topology" => p.Installations.SelectMany(i => i.Areas).Select(a => new WorkbenchNavNode("area:" + a.Id,
                WorkbenchStrings.Get("AreaNode", a.Address, a.Name), "", a, null, a.Lines.Select(l => new WorkbenchNavNode("line:" + l.Id,
                    l.Name ?? WorkbenchStrings.Get("Line"), $"{a.Address}.{l.Address}", l, new("line", l.Id),
                    l.Devices.Select(Device).Concat(l.Segments.Select(s => new WorkbenchNavNode("segment:" + s.Id,
                        s.Name ?? WorkbenchStrings.Get("Segment"), s.Address.ToString(), s, null, s.Devices.Select(Device).ToList()))).ToList()))
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

/// <summary>Why a device drag was rejected; the shell localizes every value.</summary>
public enum DeviceDropStatus
{
    Ok,
    NotATopologyTarget,
    DeviceMissing,
    LineMissing,
    AlreadyThere,
}

public sealed record DeviceDropResult(DeviceDropStatus Status, Line? Line = null, Segment? Segment = null)
{
    public bool Ok => Status == DeviceDropStatus.Ok;
}

public sealed record WorkbenchRow(string Key, string Kind, string Name, string Address, DeviceInstance Device,
    ComObjectInstanceRef? Object, int Depth, bool Expanded, bool CanExpand, string Links = "", string LinkDetails = "",
    string? Dpt = null, int? Bits = null, string? Flags = null, bool Active = true, bool External = false,
    string? ChannelId = null);
