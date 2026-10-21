namespace Knx;

/// <summary>
/// Pure address allocation over the existing model: it only reads the installation and never
/// mutates it. ETS6 spreads this over a strategy family; one lowest-free-address query with an
/// explicit skip set covers the observed flows (see analysis/ets/addresses.md §4).
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
                if (occupied.Contains((ushort)raw) || parked?.Contains((ushort)raw) == true) continue;
                address = new GroupAddress((ushort)raw);
                return true;
            }
        address = default;
        return false;
    }

    private static bool Allowed(GroupAddressStyle style, ushort raw) => raw != 0 && style switch
    {
        GroupAddressStyle.ThreeLevel => (raw & 0xFF) != 0,
        GroupAddressStyle.TwoLevel => (raw & 0x7FF) != 0,
        _ => true,
    };
}
