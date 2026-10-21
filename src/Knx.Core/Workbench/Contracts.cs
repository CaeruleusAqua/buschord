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

    /// <summary>Rejects malformed supplemental data before it reaches project-public state.</summary>
    public void Validate()
    {
        if (Objects is null) throw new InvalidDataException("Workbench object metadata is null.");
        if (Devices is null) throw new InvalidDataException("Workbench device metadata is null.");
        if (Groups is null) throw new InvalidDataException("Workbench group metadata is null.");
        if (FloorShortNames is null) throw new InvalidDataException("Workbench floor metadata is null.");
        if (Objects.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null))
            throw new InvalidDataException("Workbench object metadata contains an empty key or null entry.");
        if (Devices.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null))
            throw new InvalidDataException("Workbench device metadata contains an empty key or null entry.");
        if (Groups.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null))
            throw new InvalidDataException("Workbench group metadata contains an empty key or null entry.");
        if (FloorShortNames.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null))
            throw new InvalidDataException("Workbench floor metadata contains an empty key or null entry.");
        foreach (var (key, value) in Objects)
        {
            if (value.Bits is < 1 or > 65535) throw new InvalidDataException($"Object {key} has an invalid bit size.");
            if (value.Dpt is { Length: > 30 }) throw new InvalidDataException($"Object {key} has an invalid DPT.");
            if (value.Flags is { } flags && !System.Text.RegularExpressions.Regex.IsMatch(flags, @"^C[RWTUI]*\z"))
                throw new InvalidDataException($"Object {key} has invalid flags.");
        }
        foreach (var (key, value) in Groups)
            if (value.Bits is < 1 or > 65535) throw new InvalidDataException($"Group {key} has an invalid bit size.");
        foreach (var (key, value) in Objects) value.Validate(key);
    }
}

/// <summary>Per-link facts of one communication object / group address pair (ETS connector).</summary>
public sealed record ConnectorMetadata
{
    public bool Send { get; set; }
    public bool Acknowledge { get; set; }
    public int? Priority { get; set; }
    public int? Slot { get; set; }

    [JsonIgnore] public bool IsDefault => !Send && !Acknowledge && Priority is null && Slot is null;
}

public sealed class ObjectMetadata
{
    public string? Dpt { get; set; }
    public int? Bits { get; set; }
    public string? Flags { get; set; }
    public bool Active { get; set; } = true;
    public string? ChannelId { get; set; }
    public string? Role { get; set; }
    /// <summary>Legacy single sending address; kept in sync with <see cref="Connectors"/>.</summary>
    public string? SendingGroupId { get; set; }
    /// <summary>Explicit per-link connector facts, keyed by group address id. Absent means defaults.</summary>
    public Dictionary<string, ConnectorMetadata> Connectors { get; set; } = [];

    /// <summary>Effective connector facts; legacy <see cref="SendingGroupId"/> counts as Send.</summary>
    public ConnectorMetadata Connector(string groupId) =>
        Connectors.TryGetValue(groupId, out var connector)
            ? connector with { Send = connector.Send || SendingGroupId == groupId }
            : new() { Send = SendingGroupId == groupId };

    /// <summary>Rejects malformed entries and normalizes the single-sending invariant.</summary>
    internal void Validate(string key)
    {
        if (Connectors is null) throw new InvalidDataException($"Object {key} has null connector metadata.");
        foreach (var (groupId, connector) in Connectors)
        {
            if (string.IsNullOrWhiteSpace(groupId) || connector is null)
                throw new InvalidDataException($"Object {key} has an invalid connector entry.");
            if (connector.Priority is < 0 or > 3)
                throw new InvalidDataException($"Object {key} has an invalid connector priority.");
            if (connector.Slot is < 0)
                throw new InvalidDataException($"Object {key} has an invalid connector slot.");
        }
        var sending = Connectors.Where(p => p.Value.Send).Select(p => p.Key).ToList();
        if (sending.Count > 1)
            throw new InvalidDataException($"Object {key} has more than one sending connector.");
        if (sending.Count == 1)
        {
            if (SendingGroupId is { } legacy && legacy != sending[0])
                throw new InvalidDataException($"Object {key} has conflicting sending addresses.");
            SendingGroupId = sending[0];
        }
        // Without an explicit connector flag the legacy SendingGroupId stays authoritative;
        // Connector() reports it as Send and the first connector edit replaces it.
    }
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
/// <summary>Why a link or connector was rejected. Mirrors the ETS cases this model can observe.</summary>
public enum LinkValidationReason
{
    None,
    Missing,
    NoSelection,
    UnknownCapability,
    Inactive,
    BitSize,
    DatapointType,
    LongMainType,
    SecureCapability,
    Priority,
    Slot,
}

/// <summary>Typed link validation result; <see cref="Message"/> is the UI-compatible text.</summary>
public sealed record LinkValidation(LinkValidationReason Reason, string Message)
{
    public static readonly LinkValidation Valid = new(LinkValidationReason.None, "");
    [JsonIgnore] public bool Ok => Reason == LinkValidationReason.None;
}

public sealed record LinkError(ObjectKey Key, string Reason, LinkValidationReason Code = LinkValidationReason.None);
public sealed record MutationResult(bool Ok, int Changed, IReadOnlyList<LinkError> Errors, IReadOnlyList<string> Warnings);
public sealed record ModeEffect(ObjectKey Key, bool Active, IReadOnlyList<string> RetainedGroupIds);
public sealed record ModePreview(bool Supported, bool Changed, string Reason, string DeviceId, string Mode,
    IReadOnlyList<ModeEffect> Effects);
public sealed record AssociationReport(ObjectKey Key, string DeviceName, string DeviceAddress, int Number,
    string ObjectName, string? Dpt, int? Bits, string? Flags, bool Active, string GroupId,
    string GroupAddress, string GroupName, bool Sending, string? InstalledRoomId);
