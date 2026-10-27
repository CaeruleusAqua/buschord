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
    public static LinkValidation CheckLink(Project project, ObjectKey key, string groupId) =>
        CheckLinks(project, [new(key, groupId)])[0].Validation;

    /// <summary>
    /// Checks every pair using one fresh device/object/group index. Results preserve input order,
    /// contain no mutable model references and never authorize a later mutation. Empty input returns
    /// an empty preview; AddLinks/AddLinkPairs report NoSelection when asked to mutate an empty batch.
    /// </summary>
    public static IReadOnlyList<LinkCheckResult> CheckLinks(Project project, IEnumerable<LinkCheckRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(requests);
        var selected = requests.ToArray();
        return new LinkIndex(project).Check(selected);
    }

    private sealed class LinkIndex
    {
        private readonly Project _project;
        private readonly Dictionary<ObjectKey, ObjectRow?> _rows = [];
        private readonly Dictionary<string, GroupAddressEntry?> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<ObjectKey, HashSet<string>> _links = [];
        internal LinkIndex(Project project)
        {
            _project = project;
            var devices = ProjectQuery.DeviceList(project).ToArray();
            var ambiguousDevices = devices.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() != 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var device in devices)
            {
                if (string.IsNullOrEmpty(device.Id)) continue;
                foreach (var obj in device.ComObjectRefs)
                {
                    if (string.IsNullOrEmpty(obj.Id)) continue;
                    var key = new ObjectKey(device.Id, obj.Id);
                    var row = ambiguousDevices.Contains(device.Id) ? null : new ObjectRow(key, device, obj, Metadata(project, key), null, null, false);
                    if (!_rows.TryAdd(key, row)) _rows[key] = null;
                }
            }
            foreach (var group in ProjectQuery.GroupAddresses(project))
                if (!string.IsNullOrEmpty(group.Id) && !_groups.TryAdd(group.Id, group)) _groups[group.Id] = null;
        }
        internal IReadOnlyList<LinkCheckResult> Check(IEnumerable<LinkCheckRequest> requests)
        {
            var results = new List<LinkCheckResult>();
            foreach (var request in requests)
            {
                var row = _rows.GetValueOrDefault(request.Key);
                var group = string.IsNullOrEmpty(request.GroupId) ? null : _groups.GetValueOrDefault(request.GroupId);
                var linked = false;
                if (row is not null && group is not null)
                {
                    if (!_links.TryGetValue(request.Key, out var links)) _links.Add(request.Key, links = row.Object.Links.ToHashSet(StringComparer.Ordinal));
                    linked = links.Contains(request.GroupId);
                }
                results.Add(new(request.Key, request.GroupId, CheckLink(_project, request.Key, row, group), linked));
            }
            return Array.AsReadOnly(results.ToArray());
        }
        internal ComObjectInstanceRef Object(ObjectKey key) => _rows[key]!.Object;
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
        if (DptId.Normalize(m.Dpt) is not { } objectDpt || DptId.Normalize(ga.DatapointType) is not { } groupDpt)
            return new(LinkValidationReason.UnknownCapability, "DPT is unknown; native compatibility cannot be inferred.");
        if (objectDpt != groupDpt)
            return objectDpt.Split('.')[0] != groupDpt.Split('.')[0]
                ? new(LinkValidationReason.LongMainType, "Long main types differ.")
                : new(LinkValidationReason.DatapointType, "Full DPTs differ.");
        if (g.Secure == true && project.Workbench.Devices.GetValueOrDefault(key.DeviceId)?.SecureCapable != true)
            return new(LinkValidationReason.SecureCapability,
                "Secure group address requires an explicitly Secure-capable device.");
        return LinkValidation.Valid;
    }

    /// <summary>Priority is the KNX four-level value; an upper RF fast-ack slot limit is not modeled.</summary>
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
        ArgumentNullException.ThrowIfNull(keys);
        return AddLinkPairs(project, keys.Select(key => new LinkCheckRequest(key, groupId)), undo);
    }

    /// <summary>
    /// Adds multiple exact object/group pairs atomically. Builds a new private index on every call;
    /// no preview can bypass current identity, activity, DPT, bit-size or security validation.
    /// Like GroupLinks.Link, an existing link is a no-op and connector/sending metadata is untouched.
    /// </summary>
    public static MutationResult AddLinkPairs(Project project, IEnumerable<LinkCheckRequest> requests, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(requests);
        var selected = requests.Distinct().ToArray();
        if (selected.Length == 0) return new(false, 0, [new(default, "No communication objects selected.", LinkValidationReason.NoSelection)], []);
        var index = new LinkIndex(project);
        var checks = index.Check(selected);
        var errors = checks.Where(c => !c.Validation.Ok).Select(c => new LinkError(c.Key, c.Validation.Message, c.Validation.Reason)).ToArray();
        if (errors.Length > 0) return new(false, 0, errors, []);
        var added = new List<(List<string> Links, int Position)>(checks.Count);
        try
        {
            foreach (var check in checks)
                if (!check.AlreadyLinked)
                {
                    var links = index.Object(check.Key).Links;
                    var position = links.Count;
                    links.Add(check.GroupId); added.Add((links, position));
                }
            if (added.Count > 0) undo?.Push(project);
            return new(true, added.Count, [], []);
        }
        catch
        {
            // Undo.Push serializes before changing history. Unsupported native layouts can
            // fail that serialization; undo only the appends made here, retaining list identity,
            // existing duplicates and ordering even when several pairs share one object.
            for (var i = added.Count - 1; i >= 0; i--) added[i].Links.RemoveAt(added[i].Position);
            throw;
        }
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
        if (!row.Object.Links.Contains(groupId))
        {
            if (row.Metadata is not { } stale || !stale.Connectors.Remove(groupId)) return new(true, 0, [], []);
            if (stale.SendingGroupId == groupId) stale.SendingGroupId = null;
            undo?.Push(project);
            return new(true, 1, [], []);
        }
        var wasSending = row.Metadata?.SendingGroupId == groupId;
        GroupLinks.Unlink(project, row.Device, key.ObjectRefId, groupId);
        var warnings = new List<string>();
        if (wasSending && row.Object.Links.Count > 0)
            warnings.Add("Sending group address removed; explicitly select a remaining link. No replacement was chosen.");
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
            // Version 2 adds native segment-medium and domain facts. Hashes from the previous
            // projection must remain stale even when these newly tracked values are all absent.
            Version = 2,
            Address = device.Address.ToString(), Line = located.Line?.Address, Area = located.Area.Address,
            Segment = located.Segment?.Address,
            Medium = located.Line?.MediumTypeRefId, SegmentMedium = located.Segment?.MediumTypeRefId,
            LineDomainAddress = located.Line?.DomainAddress, SegmentDomainAddress = located.Segment?.DomainAddress,
            device.ProductRefId, device.Hardware2ProgramRefId,
            Application = project.Workbench.Devices.GetValueOrDefault(deviceId)?.Application,
            Secure = project.Workbench.Devices.GetValueOrDefault(deviceId)?.SecureCapable,
            Params = device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            Objects = device.ComObjectRefs.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o =>
            {
                var m = Metadata(project, new(deviceId, o.Id));
                return new { o.Id, o.ComObjectId, o.Number, m?.Dpt, m?.Bits, m?.Flags, m?.Active,
                    Links = o.Links.Distinct().Order(StringComparer.Ordinal).Select(id =>
                    {
                        var connector = m?.Connector(id);
                        return new { Id = id,
                            Address = groups.GetValueOrDefault(id)?.Address.ToString(), Dpt = groups.GetValueOrDefault(id)?.DatapointType,
                            project.Workbench.Groups.GetValueOrDefault(id)?.Secure, project.Workbench.Groups.GetValueOrDefault(id)?.Bits,
                            Sending = connector?.Send == true, Acknowledge = connector?.Acknowledge,
                            Priority = connector?.Priority, Slot = connector?.Slot };
                    }).ToArray() };
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
