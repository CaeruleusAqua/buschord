using Knx.Catalog;

namespace Knx;

/// <summary>Device in the topology (knxproj: DeviceInstance, see Element-Names der ETS6-XML-Assembly).</summary>
public sealed class DeviceInstance
{
    public required string Id { get; init; }
    public required string ProductRefId { get; set; }
    public string? Hardware2ProgramRefId { get; set; }
    public IndividualAddress Address { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Comment { get; set; }
    public List<ChannelInstance> Channels { get; } = [];
    /// <summary>Communication objects with their group address links (knxproj: ComObjectInstanceRefs).</summary>
    public List<ComObjectInstanceRef> ComObjectRefs { get; } = [];
    /// <summary>Per-device overrides persisted in the BusChord XML extension; native ETS parameter XML remains opaque.</summary>
    public Dictionary<string, string> ParameterValues { get; } = [];
}

/// <summary>Channel of a device (knxproj: ChannelInstance).</summary>
public sealed class ChannelInstance
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<ChannelInstance> Channels { get; } = [];
}

/// <summary>Topological segment within a line (knxproj: Segment).</summary>
public sealed class Segment
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    /// <summary>Segment number (knxproj: Segment@Number). BusChord keeps the ETS 0-15 range as a byte.</summary>
    public byte Address { get; set; }
    /// <summary>
    /// Medium override of this segment (knxproj: Segment@MediumTypeRefId). NULL inherits the line's
    /// medium; different media within one line need a media coupler.
    /// </summary>
    public string? MediumTypeRefId { get; set; }
    /// <summary>
    /// Domain address of a PL/RF segment, kept as opaque text so its ETS representation survives
    /// unchanged. The attribute name follows the ETS object-model property (DomainAddress); the
    /// analysed sample projects carry no domain address, and unread native XML is preserved anyway.
    /// </summary>
    public string? DomainAddress { get; set; }
    public List<DeviceInstance> Devices { get; } = [];
}

/// <summary>Bus line within an area (knxproj: Line).</summary>
public sealed class Line
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public byte Address { get; set; }
    public string MediumTypeRefId { get; set; } = "MT-0";
    /// <summary>
    /// Domain address of the line, opaque text; PL/RF only. Attribute name follows the ETS
    /// object-model property (DomainAddress), see <see cref="Segment.DomainAddress"/>.
    /// </summary>
    public string? DomainAddress { get; set; }
    public List<DeviceInstance> Devices { get; } = [];
    public List<Segment> Segments { get; } = [];
}

/// <summary>Topological area (knxproj: Area).</summary>
public sealed class Area
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public byte Address { get; set; }
    public List<Line> Lines { get; } = [];
    public List<DeviceInstance> Devices { get; } = [];
}

/// <summary>Group range tree node; holds group addresses on the deepest level (knxproj: GroupRange).</summary>
public sealed class GroupRange
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public int RangeStart { get; set; }
    public int RangeEnd { get; set; }
    public List<GroupRange> Ranges { get; } = [];
    public List<GroupAddressEntry> Addresses { get; } = [];
}

/// <summary>Group address entry (knxproj: GroupAddress).</summary>
public sealed class GroupAddressEntry
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Comment { get; set; }
    public GroupAddress Address { get; set; }
    public string? DatapointType { get; set; }
    public uint Puid { get; set; }
}

/// <summary>One topology installation (knxproj: Installation).</summary>
public sealed class Installation
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public IndividualAddress? Address { get; set; }
    public InstallationType Type { get; set; } = InstallationType.Unknown;
    public List<Area> Areas { get; } = [];
    /// <summary>Building tree (knxproj: Locations). Empty when the project has no building data.</summary>
    public BuildingTree Locations { get; } = new();
    public GroupAddressStyle GroupAddressStyle { get; set; } = GroupAddressStyle.ThreeLevel;
    public List<GroupRange> MainRanges { get; } = [];
}

public enum InstallationType { Unknown, Regular, Partial }

/// <summary>ETS project root (knxproj: Project).</summary>
public sealed class Project
{
    public Workbench.WorkbenchMetadata Workbench { get; set; } = new();
    internal ArchiveState? ArchiveState { get; set; }
    internal System.Xml.Linq.XDocument? OriginalXml { get; set; }
    internal System.Xml.Linq.XDocument? BaselineXml { get; set; }
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset? ProjectStart { get; set; }
    public string? Guid { get; set; }
    public MasterDataInfo MasterData { get; set; } = new();
    public List<Installation> Installations { get; } = [];

    /// <summary>
    /// Hardware planning facts of the project's own manufacturer data (M-*/Hardware.xml), keyed by
    /// Hardware2Program id and product id. Populated on load; empty when the archive carries no
    /// hardware parts (then bus-power and coupler checks stay silent instead of guessing).
    /// </summary>
    public Dictionary<string, CatalogHardware> Hardware { get; } = new(StringComparer.Ordinal);

    /// <summary>Topology devices that no building-tree node references.</summary>
    public IEnumerable<DeviceInstance> UnassignedDevices()
    {
        var assigned = Installations.SelectMany(i => i.Locations.AllDeviceRefs()).ToHashSet(StringComparer.Ordinal);
        return ProjectQuery.DeviceList(this).Where(d => !assigned.Contains(d.Id));
    }
}

/// <summary>Master data reference stored in 0.xml (knxproj: MasterData element attributes).</summary>
public sealed class MasterDataInfo
{
    public string Id { get; set; } = "MD-1";
    public uint Version { get; set; } = 198;
    public string? Signature { get; set; }
}
