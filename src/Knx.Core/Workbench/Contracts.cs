using System.Text.Json.Serialization;

namespace Knx.Workbench;

public readonly record struct ObjectKey(string DeviceId, string ObjectRefId)
{
    public override string ToString() => $"{DeviceId.Length}:{DeviceId}{ObjectRefId}";
}

public enum WorkbenchScopeKind { Project, Room, Line, Device, GroupAddress }
public sealed record WorkbenchScope(WorkbenchScopeKind Kind = WorkbenchScopeKind.Project, string? Id = null,
    bool Related = true, bool IncludeInactive = false);

/// <summary>BusChord-only supplemental facts, not native ETS application semantics.</summary>
public sealed class WorkbenchMetadata
{
    public Dictionary<string, ObjectMetadata> Objects { get; set; } = [];
    public Dictionary<string, DeviceMetadata> Devices { get; set; } = [];
    public Dictionary<string, GroupMetadata> Groups { get; set; } = [];
    public Dictionary<string, string> FloorShortNames { get; set; } = [];
    [JsonIgnore] public bool IsEmpty => Objects.Count + Devices.Count + Groups.Count + FloorShortNames.Count == 0;
}

public sealed class ObjectMetadata
{
    public string? Dpt { get; set; }
    public int? Bits { get; set; }
    public string? Flags { get; set; }
    public bool Active { get; set; } = true;
    public string? ChannelId { get; set; }
    public string? Role { get; set; }
    public string? SendingGroupId { get; set; }
}

public sealed class DeviceMetadata
{
    public bool? SecureCapable { get; set; }
    public bool Demo { get; set; }
    public string? Application { get; set; }
    public long Revision { get; set; } = 1;
    public long DownloadedRevision { get; set; } = 1;
}

public sealed class GroupMetadata
{
    public int? Bits { get; set; }
    public bool? Secure { get; set; }
    public string? EffectRoomId { get; set; }
}

/// <summary>Rows reference the actual native entities; metadata may be absent for native imports.</summary>
public sealed record ObjectRow(ObjectKey Key, DeviceInstance Device, ComObjectInstanceRef Object,
    ObjectMetadata? Metadata, string? InstalledRoomId, string? LineId, bool External);
public sealed record LinkError(ObjectKey Key, string Reason);
public sealed record MutationResult(bool Ok, int Changed, IReadOnlyList<LinkError> Errors, IReadOnlyList<string> Warnings);
public sealed record ModeEffect(ObjectKey Key, bool Active, IReadOnlyList<string> RetainedGroupIds);
public sealed record ModePreview(bool Supported, bool Changed, string Reason, string DeviceId, string Mode,
    IReadOnlyList<ModeEffect> Effects);
public sealed record AssociationReport(ObjectKey Key, string DeviceName, string DeviceAddress, int Number,
    string ObjectName, string? Dpt, int? Bits, string? Flags, bool Active, string GroupId,
    string GroupAddress, string GroupName, bool Sending, string? InstalledRoomId);
