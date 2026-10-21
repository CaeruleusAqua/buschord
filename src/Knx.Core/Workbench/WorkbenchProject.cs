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
        var validation = CheckLink(project, key, groupId);
        return validation.Ok ? null : validation.Message;
    }

    /// <summary>Typed link validation; the string adapter above stays for existing UI callers.</summary>
    public static LinkValidation CheckLink(Project project, ObjectKey key, string groupId)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        var ga = ProjectQuery.GroupAddresses(project).SingleOrDefault(g => g.Id == groupId);
        return CheckLink(project, key, row, ga);
    }

    private static LinkValidation CheckLink(Project project, ObjectKey key, ObjectRow? row, GroupAddressEntry? ga)
    {
        if (row is null || ga is null) return new(LinkValidationReason.Missing, "Object or group address does not exist.");
        var groupId = ga.Id;
        var m = row.Metadata;
        var g = project.Workbench.Groups.GetValueOrDefault(groupId);
        if (m?.Active == false) return new(LinkValidationReason.Inactive, "Communication object is inactive.");
        if (m?.Dpt is null || m.Bits is null || ga.DatapointType is null || g?.Bits is null || g.Secure is null)
            return new(LinkValidationReason.UnknownCapability,
                "DPT, size or security capability is unknown; native compatibility cannot be inferred.");
        if (m.Bits != g.Bits) return new(LinkValidationReason.BitSize, "Bit lengths differ.");
        if (m.Dpt != ga.DatapointType)
            return MainType(m.Dpt) is { } objectType && MainType(ga.DatapointType) is { } groupType && objectType != groupType
                ? new(LinkValidationReason.LongMainType, "Long main types differ.")
                : new(LinkValidationReason.DatapointType, "Full DPTs differ.");
        if (g.Secure == true && project.Workbench.Devices.GetValueOrDefault(key.DeviceId)?.SecureCapable != true)
            return new(LinkValidationReason.SecureCapability,
                "Secure group address requires an explicitly Secure-capable device.");
        return LinkValidation.Valid;
    }

    // DPT main type of "1.001", "DPT-14" or "DPST-9-1"; null when the text carries no number.
    private static int? MainType(string dpt)
    {
        var text = dpt.StartsWith("DPST-", StringComparison.OrdinalIgnoreCase) ? dpt[5..]
            : dpt.StartsWith("DPT-", StringComparison.OrdinalIgnoreCase) ? dpt[4..] : dpt;
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }

    /// <summary>Priority is the KNX four-level value; the RF fast-ack slot limit is not reconstructed.</summary>
    public static LinkValidation ValidateConnector(ConnectorMetadata connector)
    {
        if (connector.Priority is < 0 or > 3)
            return new(LinkValidationReason.Priority, "Connector priority must be between 0 and 3.");
        if (connector.Slot is < 0)
            return new(LinkValidationReason.Slot, "Connector slot must not be negative.");
        return LinkValidation.Valid;
    }

    public static MutationResult AddLinks(Project project, IEnumerable<ObjectKey> keys, string groupId, UndoStack? undo = null)
    {
        var selected = keys.Distinct().ToList();
        var rows = ScopeObjects(project, new(IncludeInactive: true)).ToDictionary(r => r.Key);
        var ga = ProjectQuery.GroupAddresses(project).SingleOrDefault(g => g.Id == groupId);
        var errors = selected.Select(k =>
        {
            var validation = CheckLink(project, k, rows.GetValueOrDefault(k), ga);
            return validation.Ok ? null : new LinkError(k, validation.Message, validation.Reason);
        }).OfType<LinkError>().ToList();
        if (selected.Count == 0) errors.Add(new(default, "No communication objects selected.", LinkValidationReason.NoSelection));
        if (errors.Count > 0) return new(false, 0, errors, []);
        var changed = 0;
        foreach (var key in selected)
            if (GroupLinks.Link(project, rows[key].Device, key.ObjectRefId, groupId)) changed++;
        if (changed > 0) undo?.Push(project);
        return new(true, changed, [], []);
    }

    /// <summary>Reads the effective per-link connector facts; null when the link does not exist.</summary>
    public static ConnectorMetadata? Connector(Project project, ObjectKey key, string groupId)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        return row is null || !row.Object.Links.Contains(groupId) ? null : row.Metadata?.Connector(groupId) ?? new();
    }

    /// <summary>
    /// Replaces the connector facts of one existing link. Setting Send clears every other
    /// sending connector of the object; clearing Send removes the sending choice explicitly.
    /// </summary>
    public static MutationResult SetConnector(Project project, ObjectKey key, string groupId,
        ConnectorMetadata connector, UndoStack? undo = null)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        if (row is null || row.Metadata?.Active == false || !row.Object.Links.Contains(groupId))
            return new(false, 0, [new(key, "Select an existing link of an active object.")], []);
        var validation = ValidateConnector(connector);
        if (!validation.Ok) return new(false, 0, [new(key, validation.Message, validation.Reason)], []);
        if ((row.Metadata?.Connector(groupId) ?? new ConnectorMetadata()) == connector) return new(true, 0, [], []);
        var metadata = row.Metadata ?? new ObjectMetadata();
        project.Workbench.Objects[key.ToString()] = metadata;
        ApplyConnector(metadata, groupId, connector);
        undo?.Push(project);
        return new(true, 1, [], []);
    }

    private static void ApplyConnector(ObjectMetadata metadata, string groupId, ConnectorMetadata connector)
    {
        if (connector.Send)
        {
            // At most one sending connector per communication object.
            foreach (var id in metadata.Connectors.Where(p => p.Key != groupId && p.Value.Send).Select(p => p.Key).ToList())
                Store(metadata.Connectors, id, metadata.Connectors[id] with { Send = false });
            metadata.SendingGroupId = groupId;
        }
        else if (metadata.SendingGroupId == groupId)
            metadata.SendingGroupId = null;
        Store(metadata.Connectors, groupId, connector);

        static void Store(Dictionary<string, ConnectorMetadata> connectors, string id, ConnectorMetadata value)
        {
            if (value.IsDefault) connectors.Remove(id);
            else connectors[id] = value;
        }
    }

    public static MutationResult SetSending(Project project, ObjectKey key, string groupId, UndoStack? undo = null)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        if (row is null || row.Metadata?.Active == false || !row.Object.Links.Contains(groupId))
            return new(false, 0, [new(key, "Select an existing link of an active object.")], []);
        if (row.Metadata?.SendingGroupId == groupId) return new(true, 0, [], []);
        return SetConnector(project, key, groupId, (row.Metadata ?? new ObjectMetadata()).Connector(groupId) with { Send = true }, undo);
    }

    public static MutationResult RemoveLink(Project project, ObjectKey key, string groupId, UndoStack? undo = null)
    {
        var row = ScopeObjects(project, new(IncludeInactive: true)).SingleOrDefault(r => r.Key == key);
        if (row is null) return new(false, 0, [new(key, "Object does not exist.")], []);
        if (!row.Object.Links.Contains(groupId)) return new(true, 0, [], []);
        GroupLinks.Unlink(project, row.Device, key.ObjectRefId, groupId);
        var warnings = new List<string>();
        if (row.Metadata is { } metadata)
        {
            metadata.Connectors.Remove(groupId);
            if (metadata.SendingGroupId == groupId)
            {
                metadata.SendingGroupId = null;
                if (row.Object.Links.Count > 0) warnings.Add("Sending group address removed; explicitly select a remaining link. No replacement was chosen.");
            }
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
            Segment = located.Segment?.Address,
            Medium = located.Line?.MediumTypeRefId, device.ProductRefId, device.Hardware2ProgramRefId,
            Application = project.Workbench.Devices.GetValueOrDefault(deviceId)?.Application,
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
