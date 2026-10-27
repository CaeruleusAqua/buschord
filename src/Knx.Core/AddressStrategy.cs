namespace Knx;

/// <summary>
/// Pure address allocation over the existing model: it only reads the installation and never
/// mutates it. A lowest-free-address query supports an explicit skip set.
/// </summary>
public static class AddressStrategy
{
    /// <summary>
    /// Next free individual address on one line, starting at device 1. Devices on the area,
    /// on the line and inside segments all occupy their address; parked addresses are skipped.
    /// Returns false when all 255 device addresses are taken.
    /// </summary>
    public static bool TryNextIndividual(Installation installation, byte area, byte line,
        out IndividualAddress address, IReadOnlySet<IndividualAddress>? parked = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var used = IndividualAddressesInUse(installation);
        if (parked is not null) used.UnionWith(parked);
        for (var device = 1; device <= 255; device++)
        {
            var candidate = new IndividualAddress(area, line, (byte)device);
            if (used.Contains(candidate)) continue;
            address = candidate;
            return true;
        }
        address = default;
        return false;
    }

    /// <summary>Every individual address currently used anywhere in the installation.</summary>
    public static HashSet<IndividualAddress> IndividualAddressesInUse(Installation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var used = new HashSet<IndividualAddress>();
        foreach (var area in installation.Areas)
        {
            foreach (var device in area.Devices) used.Add(device.Address);
            foreach (var line in area.Lines)
            {
                foreach (var device in line.Devices) used.Add(device.Address);
                foreach (var segment in line.Segments)
                    foreach (var device in segment.Devices) used.Add(device.Address);
            }
        }
        return used;
    }

    /// <summary>
    /// Next free group address inside the installed ranges, lowest raw address first. Addresses
    /// already used anywhere in the installation and parked raw values are skipped, as is the
    /// reserved all-zero address and the zero sub-address of the installed style. Without
    /// installed ranges the complete style space is searched. Returns false when it is full.
    /// </summary>
    public static bool TryNextGroup(Installation installation, out GroupAddress address,
        IReadOnlySet<ushort>? parked = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var occupied = new HashSet<ushort>();
        foreach (var range in installation.MainRanges)
            foreach (var entry in ProjectQuery.AllAddresses(range))
                occupied.Add(entry.Address.Raw);

        var spans = new List<(int Start, int End)>();
        foreach (var main in installation.MainRanges)
        {
            if (main.RangeStart is < 0 or > 31 || main.RangeEnd is < 0 or > 31 || main.Ranges.Count == 0) continue;
            var mainStart = Math.Min(main.RangeStart, main.RangeEnd);
            var mainEnd = Math.Max(main.RangeStart, main.RangeEnd);
            for (var mainNumber = mainStart; mainNumber <= mainEnd; mainNumber++)
                foreach (var middle in main.Ranges)
                {
                    if (middle.RangeStart is < 0 or > 255 || middle.RangeEnd is < 0 or > 255) continue;
                    var middleStart = Math.Min(middle.RangeStart, middle.RangeEnd);
                    var middleEnd = Math.Max(middle.RangeStart, middle.RangeEnd);
                    spans.Add(((mainNumber << 11) | (middleStart << 8) | 1,
                        (mainNumber << 11) | (middleEnd << 8) | 255));
                }
        }
        foreach (var range in installation.MainRanges.SelectMany(ProjectQuery.AllRanges))
        {
            var start = Math.Max(1, Math.Min(range.RangeStart, range.RangeEnd));
            var end = Math.Min(ushort.MaxValue, Math.Max(range.RangeStart, range.RangeEnd));
            if (start <= end) spans.Add((start, end));
        }
        if (spans.Count == 0) spans.Add((1, ushort.MaxValue));

        foreach (var (start, end) in spans.OrderBy(s => s.Start).ThenBy(s => s.End))
            for (var raw = start; raw <= end; raw++)
            {
                if (!Allowed(installation.GroupAddressStyle, (ushort)raw)) continue;
                if (!WithinGroupHierarchy(installation, (ushort)raw)) continue;
                if (occupied.Contains((ushort)raw) || parked?.Contains((ushort)raw) == true) continue;
                address = new GroupAddress((ushort)raw);
                return true;
            }
        address = default;
        return false;
    }

    /// <summary>
    /// Bounds are stored either as absolute raw values (native import) or as parent-relative
    /// main/middle numbers (wizard-created ranges). Keep both readings for allocation so a
    /// wizard range cannot emit an address under the wrong main group.
    /// </summary>
    private static bool WithinGroupHierarchy(Installation installation, ushort raw)
    {
        if (installation.MainRanges.Count == 0) return true;
        var address = new GroupAddress(raw);
        foreach (var main in installation.MainRanges)
        {
            if (!CoversMain(main, address)) continue;
            if (main.Ranges.Count == 0) return true;
            foreach (var middle in main.Ranges)
                if (CoversMiddle(middle, address)) return true;
        }
        return false;
    }

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

    private static bool Allowed(GroupAddressStyle style, ushort raw) => raw != 0 && style switch
    {
        GroupAddressStyle.ThreeLevel => (raw & 0xFF) != 0,
        GroupAddressStyle.TwoLevel => (raw & 0x7FF) != 0,
        _ => true,
    };
}
