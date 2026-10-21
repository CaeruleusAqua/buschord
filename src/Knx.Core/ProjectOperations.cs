using Knx.Workbench;

namespace Knx;

/// <summary>Why a project edit was rejected. Successful edits use <see cref="Ok"/>.</summary>
public enum ProjectEditStatus
{
    Ok,
    EmptyName,
    NotFound,
    AddressOutOfRange,
    AddressInUse,
    NoFreeAddress,
    InstallationMismatch,
    InvalidTarget,
    NotEmpty,
}

public sealed record ProjectEditResult(ProjectEditStatus Status, string? Message = null)
{
    public bool Ok => Status == ProjectEditStatus.Ok;
}

public sealed record ProjectEditResult<T>(ProjectEditStatus Status, T? Value, string? Message = null)
{
    public bool Ok => Status == ProjectEditStatus.Ok;
}

/// <summary>
/// Validated, atomic topology and group-address edits. Every operation validates before it
/// mutates, changes the model in one step and records at most one undo snapshot, so a rejected
/// edit leaves the project and the undo history untouched. The UI stays thin: it calls one
/// operation per user action. Rules follow analysis/ets/addresses.md (line 0-15, segment 0-15,
/// device addresses belong to their line, building references never outlive their device).
/// <para>
/// Limits: deleting a non-empty line or segment is refused instead of deleting or adopting its
/// devices; the caller moves the devices first. Moving a device or segment into a line only
/// reallocates addresses when the target line changes, never silently discards an address.
/// </para>
/// </summary>
public static class ProjectOperations
{
    // ---------- devices ----------

    /// <summary>
    /// Moves a device to a line or segment and optionally installs it in a room. A target in
    /// another line gets the next free individual address; the device keeps its id, channels,
    /// communication objects, links and parameter values. Building references are cleared first,
    /// then the room is set (the previous room is kept when it belongs to the target installation).
    /// </summary>
    public static ProjectEditResult MoveDevice(Project project, DeviceInstance device, Line targetLine,
        Segment? targetSegment = null, string? roomId = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(targetLine);
        if (!TryLocateDevice(project, device, out var source)) return Fail(ProjectEditStatus.NotFound, "Device does not exist in this project.");
        if (!TryLocateLine(project, targetLine, out var targetInstallation, out var targetArea))
            return Fail(ProjectEditStatus.NotFound, "Target line does not exist in this project.");
        if (targetSegment is not null && !targetLine.Segments.Contains(targetSegment))
            return Fail(ProjectEditStatus.InvalidTarget, "Target segment does not belong to the target line.");
        if (targetArea.Address > 15 || targetLine.Address > 15)
            return Fail(ProjectEditStatus.AddressOutOfRange, "Area and line addresses must be between 0 and 15.");
        if (ValidateRoom(project, targetInstallation, roomId, out var room) is { } roomError) return roomError;

        var address = device.Address;
        if (address.Area != targetArea.Address || address.Line != targetLine.Address)
        {
            if (!AddressStrategy.TryNextIndividual(targetInstallation, targetArea.Address, targetLine.Address, out address))
                return Fail(ProjectEditStatus.NoFreeAddress, $"Line {targetArea.Address}.{targetLine.Address} has no free device address.");
        }

        var previousRoom = roomId is null
            ? targetInstallation.Locations.AllNodes.FirstOrDefault(n => n.DeviceRefs.Contains(device.Id))
            : room;
        var samePlacement = ReferenceEquals(source.Line, targetLine)
            && ReferenceEquals(source.Segment, targetSegment) && previousRoom is not null && ReferenceEquals(previousRoom, room);

        ProjectQuery.RemoveDevice(project, device);
        (targetSegment?.Devices ?? targetLine.Devices).Add(device);
        device.Address = address;
        if (previousRoom is not null) targetInstallation.Locations.Assign(previousRoom, device.Id);
        if (!samePlacement) undo?.Push(project);
        return Ok();
    }

    /// <summary>
    /// Copies a device into a line or segment with a new unique id and the next free individual
    /// address. Channels, communication objects with their links, parameter values, the workbench
    /// device metadata and per-object metadata (including external channel facts) are deep-copied.
    /// The copy gets no building references of its own beyond the requested/kept room.
    /// </summary>
    public static ProjectEditResult<DeviceInstance> CopyDevice(Project project, DeviceInstance source, Line targetLine,
        Segment? targetSegment = null, string? roomId = null, string? name = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetLine);
        if (!TryLocateDevice(project, source, out _)) return Fail<DeviceInstance>(ProjectEditStatus.NotFound, "Device does not exist in this project.");
        if (!TryLocateLine(project, targetLine, out var targetInstallation, out var targetArea))
            return Fail<DeviceInstance>(ProjectEditStatus.NotFound, "Target line does not exist in this project.");
        if (targetSegment is not null && !targetLine.Segments.Contains(targetSegment))
            return Fail<DeviceInstance>(ProjectEditStatus.InvalidTarget, "Target segment does not belong to the target line.");
        if (targetArea.Address > 15 || targetLine.Address > 15)
            return Fail<DeviceInstance>(ProjectEditStatus.AddressOutOfRange, "Area and line addresses must be between 0 and 15.");
        if (ValidateRoom(project, targetInstallation, roomId, out var room) is { } roomError)
            return new(roomError.Status, null, roomError.Message);
        if (!AddressStrategy.TryNextIndividual(targetInstallation, targetArea.Address, targetLine.Address, out var address))
            return Fail<DeviceInstance>(ProjectEditStatus.NoFreeAddress, $"Line {targetArea.Address}.{targetLine.Address} has no free device address.");

        var copy = Clone(source, UniqueId(project, address.ToString()));
        copy.Address = address;
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name)) return Fail<DeviceInstance>(ProjectEditStatus.EmptyName, "A device needs a name.");
            copy.Name = name.Trim();
        }
        CopyMetadata(project, source, copy);
        (targetSegment?.Devices ?? targetLine.Devices).Add(copy);
        var targetRoom = room ?? (roomId is null
            ? targetInstallation.Locations.AllNodes.FirstOrDefault(n => n.DeviceRefs.Contains(source.Id))
            : null);
        if (targetRoom is not null) targetInstallation.Locations.Assign(targetRoom, copy.Id);
        undo?.Push(project);
        return Ok(copy);
    }

    /// <summary>
    /// Deletes a device from the topology, clears every building reference and drops its workbench
    /// metadata and per-object metadata. Communication-object links disappear with the device.
    /// </summary>
    public static ProjectEditResult DeleteDevice(Project project, DeviceInstance device, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        if (!TryLocateDevice(project, device, out _)) return Fail(ProjectEditStatus.NotFound, "Device does not exist in this project.");
        var prefix = $"{device.Id.Length}:{device.Id}";
        ProjectQuery.RemoveDevice(project, device);
        project.Workbench.Devices.Remove(device.Id);
        foreach (var key in project.Workbench.Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            project.Workbench.Objects.Remove(key);
        undo?.Push(project);
        return Ok();
    }

    // ---------- lines and segments ----------

    public static ProjectEditResult<Line> CreateLine(Project project, Area area, string name,
        byte? address = null, string? id = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(area);
        if (ProjectQuery.FindInstallationOf(project, area) is null)
            return Fail<Line>(ProjectEditStatus.NotFound, "Area does not exist in this project.");
        if (string.IsNullOrWhiteSpace(name)) return Fail<Line>(ProjectEditStatus.EmptyName, "A line needs a name.");
        if (area.Address > 15) return Fail<Line>(ProjectEditStatus.AddressOutOfRange, "Area address must be between 0 and 15.");
        if (address > 15) return Fail<Line>(ProjectEditStatus.AddressOutOfRange, "Line address must be between 0 and 15.");
        if (address is { } requested && area.Lines.Any(l => l.Address == requested))
            return Fail<Line>(ProjectEditStatus.AddressInUse, $"Line address {area.Address}.{requested} is already used.");
        if (address is null)
        {
            if (!TryNextFree(area.Lines, l => l.Address, out var free))
                return Fail<Line>(ProjectEditStatus.NoFreeAddress, $"Area {area.Address} has no free line address.");
            address = free;
        }

        var line = new Line { Id = UniqueId(project, id ?? $"L-{area.Address}-{address}"), Name = name.Trim(), Address = address.Value };
        area.Lines.Add(line);
        undo?.Push(project);
        return Ok(line);
    }

    public static ProjectEditResult RenameLine(Project project, Line line, string name, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(line);
        if (!TryLocateLine(project, line, out _, out _)) return Fail(ProjectEditStatus.NotFound, "Line does not exist in this project.");
        if (string.IsNullOrWhiteSpace(name)) return Fail(ProjectEditStatus.EmptyName, "A line needs a name.");
        var trimmed = name.Trim();
        if (line.Name == trimmed) return Ok();
        line.Name = trimmed;
        undo?.Push(project);
        return Ok();
    }

    /// <summary>
    /// Moves a line to another area of the same installation and keeps all devices. When the
    /// target area differs, every device address is rebased onto the target area and line.
    /// </summary>
    public static ProjectEditResult MoveLine(Project project, Line line, Area targetArea,
        byte? address = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(targetArea);
        if (!TryLocateLine(project, line, out var installation, out var sourceArea))
            return Fail(ProjectEditStatus.NotFound, "Line does not exist in this project.");
        if (ProjectQuery.FindInstallationOf(project, targetArea) is not { } targetInstallation)
            return Fail(ProjectEditStatus.NotFound, "Target area does not exist in this project.");
        if (!ReferenceEquals(installation, targetInstallation))
            return Fail(ProjectEditStatus.InstallationMismatch, "A line cannot move between installations.");
        if (targetArea.Address > 15) return Fail(ProjectEditStatus.AddressOutOfRange, "Area address must be between 0 and 15.");
        if (address > 15) return Fail(ProjectEditStatus.AddressOutOfRange, "Line address must be between 0 and 15.");
        if (address is { } requested && targetArea.Lines.Any(l => !ReferenceEquals(l, line) && l.Address == requested))
            return Fail(ProjectEditStatus.AddressInUse, $"Line address {targetArea.Address}.{requested} is already used.");
        if (address is null && !ReferenceEquals(sourceArea, targetArea))
        {
            if (!TryNextFree(targetArea.Lines, l => l.Address, out var free))
                return Fail(ProjectEditStatus.NoFreeAddress, $"Area {targetArea.Address} has no free line address.");
            address = free;
        }
        address ??= line.Address;
        if (ReferenceEquals(sourceArea, targetArea) && line.Address == address) return Ok();

        var devices = line.Devices.Concat(line.Segments.SelectMany(s => s.Devices)).ToList();
        if (!AddressesFit(project, devices, targetArea.Address, address.Value))
            return Fail(ProjectEditStatus.AddressInUse, "A device address would collide in the target area.");
        sourceArea.Lines.Remove(line);
        line.Address = address.Value;
        targetArea.Lines.Add(line);
        foreach (var device in devices) Rebase(device, targetArea.Address, line.Address);
        undo?.Push(project);
        return Ok();
    }

    /// <summary>Deletes an empty line; devices must be moved or deleted first.</summary>
    public static ProjectEditResult DeleteLine(Project project, Line line, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(line);
        if (!TryLocateLine(project, line, out _, out var area)) return Fail(ProjectEditStatus.NotFound, "Line does not exist in this project.");
        if (line.Devices.Count > 0 || line.Segments.Any(s => s.Devices.Count > 0))
            return Fail(ProjectEditStatus.NotEmpty, "Line still contains devices; move or delete them first.");
        area.Lines.Remove(line);
        undo?.Push(project);
        return Ok();
    }

    public static ProjectEditResult<Segment> CreateSegment(Project project, Line line, string name,
        byte? address = null, string? id = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(line);
        if (!TryLocateLine(project, line, out _, out _)) return Fail<Segment>(ProjectEditStatus.NotFound, "Line does not exist in this project.");
        if (string.IsNullOrWhiteSpace(name)) return Fail<Segment>(ProjectEditStatus.EmptyName, "A segment needs a name.");
        if (address > 15) return Fail<Segment>(ProjectEditStatus.AddressOutOfRange, "Segment address must be between 0 and 15.");
        if (address is { } requested && line.Segments.Any(s => s.Address == requested))
            return Fail<Segment>(ProjectEditStatus.AddressInUse, $"Segment address {requested} is already used in this line.");
        if (address is null)
        {
            if (!TryNextFree(line.Segments, s => s.Address, out var free))
                return Fail<Segment>(ProjectEditStatus.NoFreeAddress, "The line has no free segment address.");
            address = free;
        }

        var segment = new Segment { Id = UniqueId(project, id ?? $"S-{line.Id}-{address}"), Name = name.Trim(), Address = address.Value };
        line.Segments.Add(segment);
        undo?.Push(project);
        return Ok(segment);
    }

    public static ProjectEditResult RenameSegment(Project project, Segment segment, string name, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(segment);
        if (!TryLocateSegment(project, segment, out _, out _, out _)) return Fail(ProjectEditStatus.NotFound, "Segment does not exist in this project.");
        if (string.IsNullOrWhiteSpace(name)) return Fail(ProjectEditStatus.EmptyName, "A segment needs a name.");
        var trimmed = name.Trim();
        if (segment.Name == trimmed) return Ok();
        segment.Name = trimmed;
        undo?.Push(project);
        return Ok();
    }

    /// <summary>
    /// Moves a segment to another line of the same installation. Device addresses are rebased
    /// onto the target line; a device address collision rejects the whole move.
    /// </summary>
    public static ProjectEditResult MoveSegment(Project project, Segment segment, Line targetLine,
        byte? address = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(targetLine);
        if (!TryLocateSegment(project, segment, out var installation, out _, out var sourceLine))
            return Fail(ProjectEditStatus.NotFound, "Segment does not exist in this project.");
        if (!TryLocateLine(project, targetLine, out var targetInstallation, out var targetArea))
            return Fail(ProjectEditStatus.NotFound, "Target line does not exist in this project.");
        if (!ReferenceEquals(installation, targetInstallation))
            return Fail(ProjectEditStatus.InstallationMismatch, "A segment cannot move between installations.");
        if (targetArea.Address > 15 || targetLine.Address > 15)
            return Fail(ProjectEditStatus.AddressOutOfRange, "Area and line addresses must be between 0 and 15.");
        if (address > 15) return Fail(ProjectEditStatus.AddressOutOfRange, "Segment address must be between 0 and 15.");
        if (address is { } requested && targetLine.Segments.Any(s => !ReferenceEquals(s, segment) && s.Address == requested))
            return Fail(ProjectEditStatus.AddressInUse, $"Segment address {requested} is already used in the target line.");
        if (address is null && !ReferenceEquals(sourceLine, targetLine))
        {
            if (!TryNextFree(targetLine.Segments, s => s.Address, out var free))
                return Fail(ProjectEditStatus.NoFreeAddress, "The target line has no free segment address.");
            address = free;
        }
        address ??= segment.Address;
        if (ReferenceEquals(sourceLine, targetLine) && segment.Address == address) return Ok();

        if (!AddressesFit(project, segment.Devices, targetArea.Address, targetLine.Address))
            return Fail(ProjectEditStatus.AddressInUse, "A device address would collide in the target line.");

        sourceLine.Segments.Remove(segment);
        segment.Address = address.Value;
        targetLine.Segments.Add(segment);
        foreach (var device in segment.Devices) Rebase(device, targetArea.Address, targetLine.Address);
        undo?.Push(project);
        return Ok();
    }

    /// <summary>Deletes an empty segment; devices must be moved or deleted first.</summary>
    public static ProjectEditResult DeleteSegment(Project project, Segment segment, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(segment);
        if (!TryLocateSegment(project, segment, out _, out _, out var line)) return Fail(ProjectEditStatus.NotFound, "Segment does not exist in this project.");
        if (segment.Devices.Count > 0)
            return Fail(ProjectEditStatus.NotEmpty, "Segment still contains devices; move or delete them first.");
        line.Segments.Remove(segment);
        undo?.Push(project);
        return Ok();
    }

    // ---------- group addresses ----------

    /// <summary>
    /// Creates a group address in an installed range. Without an explicit address the next free
    /// group address is allocated; a missing main/middle range is created. Duplicate addresses
    /// are rejected before anything changes. <paramref name="metadata"/> carries the workbench
    /// bit size/security facts needed to link the new address (native imports may omit them).
    /// </summary>
    public static ProjectEditResult<GroupAddressEntry> CreateGroupAddress(Project project, GroupAddress? address,
        string name, string? datapointType = null, Installation? installation = null, string? id = null,
        UndoStack? undo = null, GroupMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        installation ??= project.Installations.FirstOrDefault();
        if (installation is null) return Fail<GroupAddressEntry>(ProjectEditStatus.NotFound, "Project has no installation.");
        if (string.IsNullOrWhiteSpace(name)) return Fail<GroupAddressEntry>(ProjectEditStatus.EmptyName, "A group address needs a name.");
        if (address is null)
        {
            if (!AddressStrategy.TryNextGroup(installation, out var allocated))
                return Fail<GroupAddressEntry>(ProjectEditStatus.NoFreeAddress, "The group address space is full.");
            address = allocated;
        }
        if (ProjectQuery.GroupAddresses(project).Any(a => a.Address == address.Value))
            return Fail<GroupAddressEntry>(ProjectEditStatus.AddressInUse, $"Group address {address.Value} is already used.");

        var entry = new GroupAddressEntry
        {
            Id = UniqueId(project, id ?? NextGroupAddressId(project)),
            Name = name.Trim(),
            Address = address.Value,
            DatapointType = Trim(datapointType),
        };
        EnsureRange(installation, address.Value).Addresses.Add(entry);
        if (metadata is not null) project.Workbench.Groups[entry.Id] = metadata;
        undo?.Push(project);
        return Ok(entry);
    }

    public static ProjectEditResult RenameGroupAddress(Project project, GroupAddressEntry entry,
        string name, string? datapointType = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(entry);
        if (!TryLocateGroupAddress(project, entry, out _)) return Fail(ProjectEditStatus.NotFound, "Group address does not exist in this project.");
        if (string.IsNullOrWhiteSpace(name)) return Fail(ProjectEditStatus.EmptyName, "A group address needs a name.");
        var trimmed = name.Trim();
        var dpt = Trim(datapointType);
        if (entry.Name == trimmed && entry.DatapointType == dpt) return Ok();
        entry.Name = trimmed;
        entry.DatapointType = dpt;
        undo?.Push(project);
        return Ok();
    }

    /// <summary>
    /// Copies the address, metadata and text into a new group address with a new id, keeping the
    /// target range consistent. Links are not copied: links belong to communication objects.
    /// </summary>
    public static ProjectEditResult<GroupAddressEntry> CopyGroupAddress(Project project, GroupAddressEntry source,
        GroupAddress? address = null, string? name = null, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (!TryLocateGroupAddress(project, source, out var installation))
            return Fail<GroupAddressEntry>(ProjectEditStatus.NotFound, "Group address does not exist in this project.");
        if (name is not null && string.IsNullOrWhiteSpace(name))
            return Fail<GroupAddressEntry>(ProjectEditStatus.EmptyName, "A group address needs a name.");
        if (address is null)
        {
            if (!AddressStrategy.TryNextGroup(installation, out var allocated))
                return Fail<GroupAddressEntry>(ProjectEditStatus.NoFreeAddress, "The group address space is full.");
            address = allocated;
        }
        if (ProjectQuery.GroupAddresses(project).Any(a => a.Address == address.Value))
            return Fail<GroupAddressEntry>(ProjectEditStatus.AddressInUse, $"Group address {address.Value} is already used.");

        var copy = new GroupAddressEntry
        {
            Id = UniqueId(project, NextGroupAddressId(project)),
            Name = name?.Trim() ?? source.Name,
            Description = source.Description,
            Comment = source.Comment,
            Address = address.Value,
            DatapointType = source.DatapointType,
        };
        EnsureRange(installation, address.Value).Addresses.Add(copy);
        if (project.Workbench.Groups.TryGetValue(source.Id, out var metadata))
            project.Workbench.Groups[copy.Id] = new GroupMetadata
            {
                Bits = metadata.Bits, Secure = metadata.Secure, EffectRoomId = metadata.EffectRoomId,
            };
        undo?.Push(project);
        return Ok(copy);
    }

    /// <summary>Moves a group address to another free address and keeps its links and metadata.</summary>
    public static ProjectEditResult MoveGroupAddress(Project project, GroupAddressEntry entry,
        GroupAddress address, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(entry);
        if (!TryLocateGroupAddress(project, entry, out var installation)) return Fail(ProjectEditStatus.NotFound, "Group address does not exist in this project.");
        if (entry.Address == address) return Ok();
        if (ProjectQuery.GroupAddresses(project).Any(a => !ReferenceEquals(a, entry) && a.Address == address))
            return Fail(ProjectEditStatus.AddressInUse, $"Group address {address} is already used.");
        foreach (var range in ProjectQuery.AllRanges(installation)) range.Addresses.Remove(entry);
        entry.Address = address;
        EnsureRange(installation, address).Addresses.Add(entry);
        undo?.Push(project);
        return Ok();
    }

    /// <summary>
    /// Deletes a group address, its links, its ranges metadata and its connector facts. A sending
    /// address is cleared explicitly and never replaced by another link.
    /// </summary>
    public static ProjectEditResult DeleteGroupAddress(Project project, GroupAddressEntry entry, UndoStack? undo = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(entry);
        if (!TryLocateGroupAddress(project, entry, out var installation)) return Fail(ProjectEditStatus.NotFound, "Group address does not exist in this project.");
        var range = ProjectQuery.AllRanges(installation).FirstOrDefault(r => r.Addresses.Contains(entry));
        if (range is null) return Fail(ProjectEditStatus.NotFound, "Group address is not stored in a range.");
        range.Addresses.Remove(entry);

        foreach (var (_, comObject) in ProjectQuery.ComObjectRefs(project))
            comObject.Links.RemoveAll(link => link == entry.Id);
        project.Workbench.Groups.Remove(entry.Id);
        var sendingRemoved = false;
        foreach (var metadata in project.Workbench.Objects.Values)
        {
            if (metadata.Connectors.TryGetValue(entry.Id, out var connector) && connector.Send) sendingRemoved = true;
            metadata.Connectors.Remove(entry.Id);
            if (metadata.SendingGroupId != entry.Id) continue;
            metadata.SendingGroupId = null;
            sendingRemoved = true;
        }
        undo?.Push(project);
        return new(ProjectEditStatus.Ok, sendingRemoved
            ? "Sending group address removed; no replacement was chosen." : null);
    }

    // ---------- helpers ----------

    private readonly record struct DevicePlacement(Installation Installation, Area Area, Line? Line, Segment? Segment);

    private static ProjectEditResult Ok() => new(ProjectEditStatus.Ok);

    private static ProjectEditResult Fail(ProjectEditStatus status, string message) => new(status, message);

    private static ProjectEditResult<T> Ok<T>(T value) => new(ProjectEditStatus.Ok, value);

    private static ProjectEditResult<T> Fail<T>(ProjectEditStatus status, string message) => new(status, default, message);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryLocateDevice(Project project, DeviceInstance device, out DevicePlacement placement)
    {
        foreach (var (installation, area, line, segment, candidate) in ProjectQuery.Devices(project))
            if (ReferenceEquals(candidate, device) || candidate.Id == device.Id)
            {
                placement = new(installation, area, line, segment);
                return true;
            }
        placement = default;
        return false;
    }

    private static bool TryLocateLine(Project project, Line line, out Installation installation, out Area area)
    {
        foreach (var candidate in project.Installations)
            foreach (var candidateArea in candidate.Areas)
                foreach (var candidateLine in candidateArea.Lines)
                    if (ReferenceEquals(candidateLine, line) || candidateLine.Id == line.Id)
                    {
                        installation = candidate;
                        area = candidateArea;
                        return true;
                    }
        installation = null!;
        area = null!;
        return false;
    }

    private static bool TryLocateSegment(Project project, Segment segment, out Installation installation,
        out Area area, out Line line)
    {
        foreach (var candidate in project.Installations)
            foreach (var candidateArea in candidate.Areas)
                foreach (var candidateLine in candidateArea.Lines)
                    foreach (var candidateSegment in candidateLine.Segments)
                        if (ReferenceEquals(candidateSegment, segment) || candidateSegment.Id == segment.Id)
                        {
                            installation = candidate;
                            area = candidateArea;
                            line = candidateLine;
                            return true;
                        }
        installation = null!;
        area = null!;
        line = null!;
        return false;
    }

    private static bool TryLocateGroupAddress(Project project, GroupAddressEntry entry, out Installation installation)
    {
        foreach (var candidate in project.Installations)
            if (candidate.MainRanges.SelectMany(ProjectQuery.AllAddresses)
                .Any(a => ReferenceEquals(a, entry) || a.Id == entry.Id))
            {
                installation = candidate;
                return true;
            }
        installation = null!;
        return false;
    }

    private static ProjectEditResult? ValidateRoom(Project project, Installation installation, string? roomId, out SpaceNode? room)
    {
        room = null;
        if (roomId is null) return null;
        room = project.Installations.SelectMany(i => i.Locations.AllNodes).FirstOrDefault(n => n.Id == roomId);
        if (room is null) return Fail(ProjectEditStatus.NotFound, $"Room '{roomId}' does not exist.");
        if (!installation.Locations.AllNodes.Contains(room))
            return Fail(ProjectEditStatus.InstallationMismatch, "Room and line belong to different installations.");
        return null;
    }

    private static bool AddressesFit(Project project, IReadOnlyCollection<DeviceInstance> devices, byte area, byte line)
    {
        var proposed = devices.Select(d => new IndividualAddress(area, line, d.Address.Device)).ToList();
        if (proposed.Select(a => a.Raw).Distinct().Count() != proposed.Count) return false;
        var moving = devices.ToHashSet();
        return !ProjectQuery.DeviceList(project)
            .Any(d => !moving.Contains(d) && proposed.Contains(d.Address));
    }

    private static void Rebase(DeviceInstance device, byte area, byte line)
        => device.Address = new IndividualAddress(area, line, device.Address.Device);

    private static bool TryNextFree<T>(IEnumerable<T> items, Func<T, byte> address, out byte free)
    {
        var used = items.Select(address).ToHashSet();
        for (var candidate = 0; candidate <= 15; candidate++)
            if (!used.Contains((byte)candidate))
            {
                free = (byte)candidate;
                return true;
            }
        free = 0;
        return false;
    }

    private static DeviceInstance Clone(DeviceInstance source, string id)
    {
        var copy = new DeviceInstance
        {
            Id = id,
            ProductRefId = source.ProductRefId,
            Hardware2ProgramRefId = source.Hardware2ProgramRefId,
            Address = source.Address,
            Name = source.Name,
            Description = source.Description,
            Comment = source.Comment,
        };
        foreach (var channel in source.Channels) copy.Channels.Add(Clone(channel));
        foreach (var comObject in source.ComObjectRefs)
        {
            var clone = new ComObjectInstanceRef
            {
                Id = comObject.Id,
                ComObjectId = comObject.ComObjectId,
                Text = comObject.Text,
                Number = comObject.Number,
                FunctionText = comObject.FunctionText,
                Device = comObject.Device,
            };
            clone.Links.AddRange(comObject.Links);
            copy.ComObjectRefs.Add(clone);
        }
        foreach (var (key, value) in source.ParameterValues) copy.ParameterValues[key] = value;
        return copy;

        static ChannelInstance Clone(ChannelInstance source)
        {
            var copy = new ChannelInstance { Id = source.Id, Name = source.Name };
            foreach (var child in source.Channels) copy.Channels.Add(Clone(child));
            return copy;
        }
    }

    private static void CopyMetadata(Project project, DeviceInstance source, DeviceInstance copy)
    {
        if (project.Workbench.Devices.TryGetValue(source.Id, out var device))
            project.Workbench.Devices[copy.Id] = new DeviceMetadata
            {
                SecureCapable = device.SecureCapable, Demo = device.Demo, Application = device.Application,
                Revision = device.Revision, DownloadedRevision = device.DownloadedRevision,
            };
        foreach (var comObject in source.ComObjectRefs)
            if (project.Workbench.Objects.TryGetValue(new ObjectKey(source.Id, comObject.Id).ToString(), out var metadata))
                project.Workbench.Objects[new ObjectKey(copy.Id, comObject.Id).ToString()] = new ObjectMetadata
                {
                    Dpt = metadata.Dpt, Bits = metadata.Bits, Flags = metadata.Flags, Active = metadata.Active,
                    ChannelId = metadata.ChannelId, Role = metadata.Role, SendingGroupId = metadata.SendingGroupId,
                    Connectors = metadata.Connectors.ToDictionary(p => p.Key, p => p.Value with { }),
                };
    }

    /// <summary>Ids from every XML element that is an <c>xs:ID</c> in the native schema.</summary>
    private static HashSet<string> UsedIds(Project project)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var installation in project.Installations)
        {
            ids.Add(installation.Id);
            foreach (var node in installation.Locations.AllNodes) ids.Add(node.Id);
            foreach (var range in ProjectQuery.AllRanges(installation)) ids.Add(range.Id);
            foreach (var entry in installation.MainRanges.SelectMany(ProjectQuery.AllAddresses)) ids.Add(entry.Id);
            foreach (var area in installation.Areas)
            {
                ids.Add(area.Id);
                foreach (var device in area.Devices) ids.Add(device.Id);
                foreach (var line in area.Lines)
                {
                    ids.Add(line.Id);
                    foreach (var device in line.Devices) ids.Add(device.Id);
                    foreach (var segment in line.Segments)
                    {
                        ids.Add(segment.Id);
                        foreach (var device in segment.Devices) ids.Add(device.Id);
                    }
                }
            }
        }
        return ids;
    }

    private static string UniqueId(Project project, string requested)
    {
        var used = UsedIds(project);
        if (!used.Contains(requested)) return requested;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = requested + "-" + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string NextGroupAddressId(Project project)
    {
        var max = ProjectQuery.GroupAddresses(project)
            .Select(a => int.TryParse(a.Id.Replace("GA-", ""), out var number) ? number : 0)
            .DefaultIfEmpty(0).Max();
        return $"GA-{max + 1}";
    }

    private static int NextRangeNumber(Installation installation)
    {
        var max = ProjectQuery.AllRanges(installation)
            .Select(r => int.TryParse(r.Id.Replace("GR-", ""), out var number) ? number : 0)
            .DefaultIfEmpty(0).Max();
        return max + 1;
    }

    /// <summary>Deepest covering range, creating the missing main/middle range when needed.</summary>
    private static GroupRange EnsureRange(Installation installation, GroupAddress address)
    {
        var covering = ProjectQuery.FindRangeFor(installation, address);
        // A three-level group address belongs in a middle range; a bare main range is not a
        // valid target and must get its middle child first.
        if (covering is not null && (installation.GroupAddressStyle != GroupAddressStyle.ThreeLevel
            || !installation.MainRanges.Contains(covering)))
            return covering;
        if (installation.GroupAddressStyle == GroupAddressStyle.ThreeLevel)
        {
            var main = installation.MainRanges.FirstOrDefault(r => CoversMain(r, address));
            if (main is null)
            {
                main = new GroupRange
                {
                    Id = $"GR-{NextRangeNumber(installation)}", Name = $"Hauptbereich {address.Main}",
                    RangeStart = address.Main, RangeEnd = address.Main,
                };
                installation.MainRanges.Add(main);
            }
            var middle = main.Ranges.FirstOrDefault(r => CoversMiddle(r, address));
            if (middle is null)
            {
                middle = new GroupRange
                {
                    Id = $"GR-{NextRangeNumber(installation)}", Name = $"Mittelbereich {address.Main}/{address.Middle}",
                    RangeStart = address.Middle, RangeEnd = address.Middle,
                };
                main.Ranges.Add(middle);
            }
            return middle;
        }
        var root = new GroupRange
        {
            Id = $"GR-{NextRangeNumber(installation)}", Name = $"Hauptbereich {address.Main}",
            RangeStart = address.Raw, RangeEnd = address.Raw,
        };
        installation.MainRanges.Add(root);
        return root;
    }

    // Same dual reading as AddressStrategy: main/middle bounds may be relative numbers or raw values.
    private static bool CoversMain(GroupRange range, GroupAddress address) =>
        range.RangeStart is >= 0 and <= 31 && range.RangeEnd is >= 0 and <= 31
            ? address.Main >= range.RangeStart && address.Main <= range.RangeEnd
            : address.Raw >= range.RangeStart && address.Raw <= range.RangeEnd
                || address.Main >= range.RangeStart && address.Main <= range.RangeEnd;

    private static bool CoversMiddle(GroupRange range, GroupAddress address) =>
        range.RangeStart is >= 0 and <= 255 && range.RangeEnd is >= 0 and <= 255
            ? address.Middle >= range.RangeStart && address.Middle <= range.RangeEnd
            : address.Raw >= range.RangeStart && address.Raw <= range.RangeEnd
                || address.Middle >= range.RangeStart && address.Middle <= range.RangeEnd;
}
