using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knx.Workbench;

public static class WorkbenchProject
{
    public static ObjectMetadata? Metadata(Project project, ObjectKey key) => project.Workbench.Objects.GetValueOrDefault(key.ToString());

    public static IReadOnlyList<ObjectRow> ScopeObjects(Project project, WorkbenchScope? scope = null)
    {
        scope ??= new();
        var locations = project.Installations.SelectMany(i => i.Locations.AllNodes).ToList();
        var rows = ProjectQuery.Devices(project).SelectMany(d => d.Device.ComObjectRefs.Select(o =>
        {
            var key = new ObjectKey(d.Device.Id, o.Id);
            var room = locations.FirstOrDefault(r => r.DeviceRefs.Contains(d.Device.Id))?.Id;
            return new ObjectRow(key, d.Device, o, Metadata(project, key), room, d.Line?.Id, false);
        })).ToList();
        IEnumerable<ObjectRow> selected = rows;
        switch (scope.Kind)
        {
            case WorkbenchScopeKind.Device: selected = rows.Where(r => r.Device.Id == scope.Id); break;
            case WorkbenchScopeKind.Line: selected = rows.Where(r => r.LineId == scope.Id); break;
            case WorkbenchScopeKind.GroupAddress: selected = rows.Where(r => r.Object.Links.Contains(scope.Id!)); break;
            case WorkbenchScopeKind.Room:
                var room = locations.SingleOrDefault(r => r.Id == scope.Id);
                var roomIds = room is null ? new HashSet<string>() : room.Descendants.Select(r => r.Id).Append(room.Id).ToHashSet();
                var local = room is null ? new HashSet<string>() : BuildingTree.DevicesUnder(room).ToHashSet();
                var gas = project.Workbench.Groups.Where(g => g.Value.EffectRoomId is { } id && roomIds.Contains(id)).Select(g => g.Key).ToHashSet();
                var channels = scope.Related ? rows.Where(r => !local.Contains(r.Device.Id) && r.Metadata?.Active != false &&
                    r.Metadata?.ChannelId is not null && r.Object.Links.Any(gas.Contains))
                    .Select(r => (r.Device.Id, r.Metadata!.ChannelId)).ToHashSet() : [];
                selected = rows.Where(r => local.Contains(r.Device.Id) || channels.Contains((r.Device.Id, r.Metadata?.ChannelId)))
                    .Select(r => r with { External = !local.Contains(r.Device.Id) });
                break;
        }
        return selected.Where(r => scope.IncludeInactive || r.Metadata?.Active != false).ToList();
    }

    public static string? ValidateLink(Project project, ObjectKey key, string groupId)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        var ga = ProjectQuery.GroupAddresses(project).SingleOrDefault(g => g.Id == groupId);
        if (row is null || ga is null) return "Object or group address does not exist.";
        if (row.Object.Links.Contains(groupId)) return null;
        var m = row.Metadata;
        var g = project.Workbench.Groups.GetValueOrDefault(groupId);
        if (m?.Active == false) return "Communication object is inactive.";
        if (m?.Dpt is null || m.Bits is null || ga.DatapointType is null || g?.Bits is null || g.Secure is null)
            return "DPT, size or security capability is unknown; native compatibility cannot be inferred.";
        if (m.Bits != g.Bits) return "Bit lengths differ.";
        if (m.Dpt != ga.DatapointType) return "Full DPTs differ.";
        if (g.Secure == true && project.Workbench.Devices.GetValueOrDefault(key.DeviceId)?.SecureCapable != true)
            return "Secure group address requires an explicitly Secure-capable device.";
        return null;
    }

    public static MutationResult AddLinks(Project project, IEnumerable<ObjectKey> keys, string groupId, UndoStack? undo = null)
    {
        var selected = keys.Distinct().ToList();
        var errors = selected.Select(k => new LinkError(k, ValidateLink(project, k, groupId) ?? "")).Where(e => e.Reason.Length > 0).ToList();
        if (selected.Count == 0) errors.Add(new(default, "No communication objects selected."));
        if (errors.Count > 0) return new(false, 0, errors, []);
        var rows = ScopeObjects(project, new(IncludeInactive: true)).ToDictionary(r => r.Key);
        var changed = 0;
        foreach (var key in selected)
            if (GroupLinks.Link(project, rows[key].Device, key.ObjectRefId, groupId)) changed++;
        if (changed > 0) undo?.Push(project);
        return new(true, changed, [], []);
    }

    public static MutationResult SetSending(Project project, ObjectKey key, string groupId, UndoStack? undo = null)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        if (row is null || row.Metadata?.Active == false || !row.Object.Links.Contains(groupId))
            return new(false, 0, [new(key, "Select an existing link of an active object.")], []);
        if (row.Metadata?.SendingGroupId == groupId) return new(true, 0, [], []);
        var metadata = row.Metadata ?? new ObjectMetadata();
        project.Workbench.Objects[key.ToString()] = metadata;
        metadata.SendingGroupId = groupId;
        undo?.Push(project);
        return new(true, 1, [], []);
    }

    public static MutationResult RemoveLink(Project project, ObjectKey key, string groupId, UndoStack? undo = null)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        if (row is null) return new(false, 0, [new(key, "Object does not exist.")], []);
        if (!row.Object.Links.Contains(groupId)) return new(true, 0, [], []);
        GroupLinks.Unlink(project, row.Device, key.ObjectRefId, groupId);
        var warnings = new List<string>();
        if (row.Metadata?.SendingGroupId == groupId)
        {
            row.Metadata.SendingGroupId = null;
            if (row.Object.Links.Count > 0) warnings.Add("Sending group address removed; explicitly select a remaining link. No replacement was chosen.");
        }
        undo?.Push(project);
        return new(true, 1, [], warnings);
    }

    public static ModePreview PreviewMode(Project project, string deviceId, string mode)
    {
        var device = ProjectQuery.DeviceList(project).SingleOrDefault(d => d.Id == deviceId);
        if (device is null || project.Workbench.Devices.GetValueOrDefault(deviceId)?.Demo != true || !device.ParameterValues.ContainsKey("mode"))
            return new(false, false, "Native manufacturer dynamics are unsupported; only explicit DEMO switch/dim is modeled.", deviceId, mode, []);
        if (mode is not ("switch" or "dim")) return new(false, false, "Unknown mode.", deviceId, mode, []);
        var effects = ScopeObjects(project, new(WorkbenchScopeKind.Device, deviceId, IncludeInactive: true))
            .Where(r => r.Metadata?.Dpt == "3.007" && r.Metadata.Active != (mode == "dim"))
            .Select(r => new ModeEffect(r.Key, mode == "dim", r.Object.Links.ToArray())).ToList();
        return new(true, device.ParameterValues["mode"] != JsonSerializer.Serialize(mode) || effects.Count > 0,
            "DEMO effect preview; all links are retained.", deviceId, mode, effects);
    }

    public static ModePreview ApplyMode(Project project, string deviceId, string mode, UndoStack? undo = null)
    {
        var preview = PreviewMode(project, deviceId, mode);
        if (!preview.Supported || !preview.Changed) return preview;
        var device = ProjectQuery.DeviceList(project).Single(d => d.Id == deviceId);
        device.ParameterValues["mode"] = JsonSerializer.Serialize(mode);
        foreach (var effect in preview.Effects) Metadata(project, effect.Key)!.Active = effect.Active;
        undo?.Push(project);
        return preview;
    }

    public static string ConfigurationSignature(Project project, string deviceId)
    {
        var located = ProjectQuery.Devices(project).Single(d => d.Device.Id == deviceId);
        var device = located.Device;
        var groups = ProjectQuery.GroupAddresses(project).ToDictionary(g => g.Id);
        var config = new
        {
            Address = device.Address.ToString(), Line = located.Line?.Address, Area = located.Area.Address,
            Medium = located.Line?.MediumTypeRefId, device.ProductRefId, device.Hardware2ProgramRefId,
            Secure = project.Workbench.Devices.GetValueOrDefault(deviceId)?.SecureCapable,
            Params = device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            Objects = device.ComObjectRefs.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o =>
            {
                var m = Metadata(project, new(deviceId, o.Id));
                return new { o.Id, o.ComObjectId, o.Number, m?.Dpt, m?.Bits, m?.Flags, m?.Active,
                    Links = o.Links.Distinct().Order(StringComparer.Ordinal).Select(id => new { Id = id,
                        Address = groups.GetValueOrDefault(id)?.Address.ToString(), Dpt = groups.GetValueOrDefault(id)?.DatapointType,
                        project.Workbench.Groups.GetValueOrDefault(id)?.Secure, project.Workbench.Groups.GetValueOrDefault(id)?.Bits,
                        Sending = m?.SendingGroupId == id }).ToArray() };
            }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config))));
    }

    public static IReadOnlyList<AssociationReport> Associations(Project project)
    {
        var groups = ProjectQuery.GroupAddresses(project).ToDictionary(g => g.Id);
        return ScopeObjects(project, new(IncludeInactive: true)).SelectMany(r => r.Object.Links.Select(id =>
        {
            var ga = groups[id];
            return new AssociationReport(r.Key, r.Device.Name ?? "", r.Device.Address.ToString(), r.Object.Number,
                r.Object.Text ?? "", r.Metadata?.Dpt, r.Metadata?.Bits, r.Metadata?.Flags, r.Metadata?.Active != false,
                id, ga.Address.ToString(), ga.Name, r.Metadata?.SendingGroupId == id, r.InstalledRoomId);
        })).ToList();
    }
}
