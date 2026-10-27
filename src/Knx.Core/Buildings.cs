namespace Knx;

/// <summary>
/// Kind of a node in the building tree, serialized in a
/// <c>&lt;Space Type="..."&gt;</c> attribute. <see cref="Function"/> is not a space
/// type; it is the native <c>&lt;Function&gt;</c> child of a space (utility/function group).
/// </summary>
public enum SpaceKind
{
    Building,
    BuildingPart,
    Floor,
    Room,
    DistributionBoard,
    /// <summary>The serialized name is "Stairway"; <see cref="Staircase"/> is accepted on read.</summary>
    Stairway,
    Staircase = Stairway,
    Corridor,
    Area,
    Ground,
    Segment,
    /// <summary>Native &lt;Function&gt; element (function/utility group), not a space.</summary>
    Function,
    /// <summary>Unrecognized native type; <see cref="SpaceNode.RawType"/> keeps the original value.</summary>
    Unknown,
}

/// <summary>XML mapping for <see cref="SpaceKind"/> (knx_project.xsd: SpaceType_t).</summary>
internal static class SpaceXml
{
    internal static SpaceKind ParseKind(string? value) => value switch
    {
        "Building" => SpaceKind.Building,
        "BuildingPart" => SpaceKind.BuildingPart,
        "Floor" => SpaceKind.Floor,
        "Room" => SpaceKind.Room,
        "DistributionBoard" => SpaceKind.DistributionBoard,
        "Stairway" or "Staircase" => SpaceKind.Stairway,
        "Corridor" => SpaceKind.Corridor,
        "Area" => SpaceKind.Area,
        "Ground" => SpaceKind.Ground,
        "Segment" => SpaceKind.Segment,
        _ => SpaceKind.Unknown,
    };

    internal static string KindName(SpaceKind kind) => kind switch
    {
        SpaceKind.Building => "Building",
        SpaceKind.BuildingPart => "BuildingPart",
        SpaceKind.Floor => "Floor",
        SpaceKind.Room => "Room",
        SpaceKind.DistributionBoard => "DistributionBoard",
        SpaceKind.Stairway => "Stairway",
        SpaceKind.Corridor => "Corridor",
        SpaceKind.Area => "Area",
        SpaceKind.Ground => "Ground",
        SpaceKind.Segment => "Segment",
        _ => "Unknown",
    };
}

/// <summary>
/// One node of the building tree (knxproj: <c>Space</c> or <c>Function</c> inside
/// <c>Installations/Installation/Locations</c>). IDs are stable XML IDs and survive
/// save/load and undo snapshots. Devices are referenced by <see cref="DeviceInstance.Id"/>.
/// </summary>
public sealed class SpaceNode
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public SpaceKind Kind { get; set; }

    /// <summary>Original native Type attribute when <see cref="Kind"/> is Unknown.</summary>
    public string? RawType { get; set; }

    public string? Number { get; set; }
    public string? Description { get; set; }
    public string? Comment { get; set; }
    /// <summary>Space usage IDREF (native attribute <c>Usage</c>); content is not interpreted.</summary>
    public string? Usage { get; set; }
    /// <summary>Native Puid attribute; preserved so a save does not renumber existing spaces.</summary>
    public int Puid { get; set; }

    public SpaceNode? Parent { get; internal set; }
    public List<SpaceNode> Children { get; } = [];
    /// <summary>DeviceInstance Ids installed in this node (native DeviceInstanceRef RefId).</summary>
    public List<string> DeviceRefs { get; } = [];

    /// <summary>Slash-separated path from the root, e.g. "Haus / EG / Wohnen".</summary>
    public string Path => Parent is null ? Name : Parent.Path + " / " + Name;

    public IEnumerable<SpaceNode> Descendants
    {
        get
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var descendant in child.Descendants) yield return descendant;
            }
        }
    }

    public void Add(SpaceNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        for (var ancestor = this; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, child))
                throw new ArgumentException("A space cannot contain itself or one of its ancestors.", nameof(child));
        if (child.Parent is not null) throw new InvalidOperationException($"Space {child.Id} already has a parent.");
        child.Parent = this;
        Children.Add(child);
    }

    public bool Remove(SpaceNode child)
    {
        if (!Children.Remove(child)) return false;
        child.Parent = null;
        return true;
    }

    /// <summary>Installs a device in this node; returns false when it is already referenced.</summary>
    public bool AssignDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (DeviceRefs.Contains(deviceId)) return false;
        DeviceRefs.Add(deviceId);
        return true;
    }

    public bool AssignDevice(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return AssignDevice(device.Id);
    }

    public bool UnassignDevice(string deviceId) => DeviceRefs.Remove(deviceId);

    public bool UnassignDevice(DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return UnassignDevice(device.Id);
    }

    public override string ToString() => $"{Kind} {Path} [{Id}]";
}

/// <summary>
/// Building tree of one installation (native <c>&lt;Locations&gt;</c> container).
/// An empty tree is not serialized, so projects without building data keep their XML unchanged.
/// </summary>
public sealed class BuildingTree
{
    public List<SpaceNode> Roots { get; } = [];

    public bool IsEmpty => Roots.Count == 0;

    public IEnumerable<SpaceNode> AllNodes => Roots.SelectMany(r => new[] { r }.Concat(r.Descendants));

    public void AddRoot(SpaceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Parent is not null) throw new InvalidOperationException($"Space {node.Id} already has a parent.");
        Roots.Add(node);
    }

    public bool Remove(SpaceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Parent is { } parent ? parent.Remove(node) : Roots.Remove(node);
    }

    /// <summary>
    /// Creates and attaches a node. The id is unique within the tree: a requested id that is
    /// already used elsewhere gets a numeric suffix, and generated ids are checked as well.
    /// Native space ids are <c>xs:ID</c>, so uniqueness must not be limited to the siblings.
    /// Scope is the tree (one installation); cross-installation duplicates are not compared.
    /// </summary>
    public SpaceNode Add(SpaceNode? parent, string name, SpaceKind kind, string? id = null, int puid = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var node = new SpaceNode { Id = UniqueId(id), Name = name, Kind = kind, Puid = puid };
        if (parent is null) AddRoot(node);
        else parent.Add(node);
        return node;
    }

    private string UniqueId(string? requested)
    {
        if (requested is not null && Find(requested) is null) return requested;
        var stem = requested ?? NewId();
        for (var suffix = 2; ; suffix++)
        {
            var candidate = stem + "-" + suffix;
            if (Find(candidate) is null) return candidate;
        }
    }

    public static string NewId() => "SP-" + Guid.NewGuid().ToString("N")[..8];

    public SpaceNode? Find(string id) => AllNodes.FirstOrDefault(n => n.Id == id);

    public SpaceNode? Find(string parentId, string name)
        => AllNodes.FirstOrDefault(n => n.Parent?.Id == parentId && n.Name == name);

    /// <summary>Node that references the device, or null when the device has no location.</summary>
    public SpaceNode? FindDevice(string deviceId) => AllNodes.FirstOrDefault(n => n.DeviceRefs.Contains(deviceId));

    public IEnumerable<string> AllDeviceRefs() => AllNodes.SelectMany(n => n.DeviceRefs);

    /// <summary>Device ids in the node and its descendants.</summary>
    public static IEnumerable<string> DevicesUnder(SpaceNode node)
        => node.DeviceRefs.Concat(node.Descendants.SelectMany(n => n.DeviceRefs));

    /// <summary>Installs the device in the node, removing it from every other node first.</summary>
    public void Assign(SpaceNode node, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        foreach (var existing in AllNodes) existing.UnassignDevice(deviceId);
        node.AssignDevice(deviceId);
    }
}

/// <summary>Outcome of a validated building-tree edit.</summary>
public enum SpaceEditResult
{
    Ok,
    EmptyName,
}

/// <summary>
/// Validated edits shared by the building pane. Keeping them here lets the UI stay thin and lets
/// the building tests cover the rules without an Avalonia host.
/// </summary>
public static class BuildingEdits
{
    /// <summary>Renames a node in place; whitespace is trimmed and blank names are refused.</summary>
    public static SpaceEditResult Rename(SpaceNode node, string? name)
    {
        ArgumentNullException.ThrowIfNull(node);
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return SpaceEditResult.EmptyName;
        node.Name = trimmed;
        return SpaceEditResult.Ok;
    }

    /// <summary>
    /// Removes a node but promotes its children into its place, preserving order. Devices that
    /// were installed directly in the removed node become unassigned; references on the promoted
    /// children stay. Returns false when the node is not part of the tree.
    /// </summary>
    public static bool RemoveKeepingChildren(BuildingTree tree, SpaceNode node)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(node);
        var siblings = node.Parent is { } parent ? parent.Children : tree.Roots;
        var index = siblings.IndexOf(node);
        if (index < 0) return false;
        var promoted = node.Children.ToList();
        foreach (var child in promoted) node.Remove(child);
        for (var position = 0; position < promoted.Count; position++)
        {
            promoted[position].Parent = node.Parent;
            siblings.Insert(index + position, promoted[position]);
        }
        siblings.Remove(node);
        node.Parent = null;
        return true;
    }
}
