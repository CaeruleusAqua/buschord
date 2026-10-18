namespace Knx;

/// <summary>
/// Read/write helpers over the project tree. Everything that needs to walk the
/// topology or the group address tree goes through here so the traversal order
/// stays identical across features.
/// </summary>
public static class ProjectQuery
{
    public static IEnumerable<(Installation Installation, Area Area, Line? Line, Segment? Segment, DeviceInstance Device)> Devices(Project project)
    {
        foreach (var inst in project.Installations)
        {
            foreach (var area in inst.Areas)
            {
                foreach (var d in area.Devices) yield return (inst, area, null, null, d);
                foreach (var line in area.Lines)
                {
                    foreach (var d in line.Devices) yield return (inst, area, line, null, d);
                    foreach (var seg in line.Segments)
                        foreach (var d in seg.Devices) yield return (inst, area, line, seg, d);
                }
            }
        }
    }

    public static IEnumerable<DeviceInstance> DeviceList(Project project)
        => Devices(project).Select(d => d.Device);

    public static IEnumerable<GroupAddressEntry> GroupAddresses(Project project)
        => project.Installations.SelectMany(i => i.MainRanges).SelectMany(AllAddresses);

    public static IEnumerable<GroupAddressEntry> AllAddresses(GroupRange range)
    {
        foreach (var a in range.Addresses) yield return a;
        foreach (var child in range.Ranges)
            foreach (var a in AllAddresses(child))
                yield return a;
    }

    public static IEnumerable<GroupRange> AllRanges(Installation installation)
        => installation.MainRanges.SelectMany(AllRanges);

    public static IEnumerable<GroupRange> AllRanges(GroupRange range)
    {
        yield return range;
        foreach (var child in range.Ranges)
            foreach (var r in AllRanges(child))
                yield return r;
    }

    /// <summary>
    /// Removes the device from the topology and clears every building-tree assignment in every
    /// installation, so no <c>DeviceInstanceRef</c> keeps pointing at the deleted device.
    /// </summary>
    public static bool RemoveDevice(Project project, DeviceInstance device)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        foreach (var inst in project.Installations)
            foreach (var node in inst.Locations.AllNodes)
                node.UnassignDevice(device.Id);
        foreach (var inst in project.Installations)
            foreach (var area in inst.Areas)
            {
                if (area.Devices.Remove(device)) return true;
                foreach (var line in area.Lines)
                {
                    if (line.Devices.Remove(device)) return true;
                    foreach (var seg in line.Segments)
                        if (seg.Devices.Remove(device)) return true;
                }
            }
        return false;
    }

    /// <summary>Moves a device into a line (or into its first segment when the line has segments).</summary>
    public static void AddToLine(Line line, DeviceInstance device)
        => (line.Segments.Count > 0 ? line.Segments[0].Devices : line.Devices).Add(device);

    /// <summary>Area that contains the given line.</summary>
    public static Area? FindAreaOf(Project project, Line line)
    {
        foreach (var installation in project.Installations)
            foreach (var area in installation.Areas)
                if (area.Lines.Contains(line)) return area;
        return null;
    }

    public static DeviceInstance? FindDevice(Project project, IndividualAddress address)        => DeviceList(project).FirstOrDefault(d => d.Address == address);

    public static (DeviceInstance Device, Installation Installation, Area Area, Line? Line)? LocateDevice(Project project, IndividualAddress address)
    {
        foreach (var entry in Devices(project))
            if (entry.Device.Address == address) return (entry.Device, entry.Installation, entry.Area, entry.Line);
        return null;
    }

    public static GroupAddressEntry? FindGroupAddress(Project project, GroupAddress address)
        => GroupAddresses(project).FirstOrDefault(a => a.Address == address);

    public static GroupAddressEntry? FindGroupAddressById(Project project, string id)
        => GroupAddresses(project).FirstOrDefault(a => a.Id == id);

    /// <summary>Group range that directly contains the address, if any.</summary>
    public static GroupRange? FindRangeOf(Project project, GroupAddressEntry entry)
        => project.Installations.SelectMany(i => AllRanges(i)).FirstOrDefault(r => r.Addresses.Contains(entry));

    /// <summary>All communication object references of all devices.</summary>
    public static IEnumerable<(DeviceInstance Device, ComObjectInstanceRef Ref)> ComObjectRefs(Project project)
        => DeviceList(project).SelectMany(d => d.ComObjectRefs.Select(r => (d, r)));

    /// <summary>Next free device address on a line (0.0.0 and taken addresses are skipped).</summary>
    public static IndividualAddress NextFreeDeviceAddress(Project project, byte area, byte line)
    {
        var used = DeviceList(project).Where(d => d.Address.Area == area && d.Address.Line == line)
            .Select(d => d.Address.Device).ToHashSet();
        for (var device = 1; device <= 255; device++)
            if (!used.Contains((byte)device)) return new IndividualAddress(area, line, (byte)device);
        throw new InvalidOperationException($"Linie {area}.{line} ist voll (255 Geräte).");
    }

    /// <summary>Next free group address starting at the given main/middle range.</summary>
    public static GroupAddress NextFreeGroupAddress(Project project, byte main, byte middle, int start = 1)
    {
        var used = GroupAddresses(project).Select(a => a.Address.Raw).ToHashSet();
        for (var sub = start; sub <= 255; sub++)
        {
            var candidate = GroupAddress.ThreeLevel(main, middle, (byte)sub);
            if (!used.Contains(candidate.Raw)) return candidate;
        }
        throw new InvalidOperationException($"Gruppenbereich {main}/{middle} ist voll.");
    }
}
