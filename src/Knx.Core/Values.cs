namespace Knx;

/// <summary>KNX individual address (area.line.device).</summary>
public readonly struct IndividualAddress : IEquatable<IndividualAddress>
{
    public IndividualAddress(ushort raw) => Raw = raw;
    public IndividualAddress(byte area, byte line, byte device)
    {
        if (area > 15) throw new ArgumentOutOfRangeException(nameof(area));
        if (line > 15) throw new ArgumentOutOfRangeException(nameof(line));
        Raw = (ushort)((area << 12) | (line << 8) | device);
    }

    public ushort Raw { get; }
    public static IndividualAddress Zero => new(0);
    public byte Area => (byte)((Raw >> 12) & 0x0F);
    public byte Line => (byte)((Raw >> 8) & 0x0F);
    public byte Device => (byte)(Raw & 0xFF);

    public static IndividualAddress Parse(string value)    {
        var p = value.Split('.');
        if (p.Length != 3
            || !byte.TryParse(p[0], out var a)
            || a > 15 || !byte.TryParse(p[1], out var l) || l > 15
            || !byte.TryParse(p[2], out var d))
            throw new FormatException($"'{value}' ist keine gültige Physikalische Adresse (x.y.z).");
        return new IndividualAddress(a, l, d);
    }

    public static bool TryParse(string value, out IndividualAddress result)
    {
        result = default;
        var p = value.Split('.');
        if (p.Length != 3
            || !byte.TryParse(p[0], out var a)
            || a > 15 || !byte.TryParse(p[1], out var l) || l > 15
            || !byte.TryParse(p[2], out var d))
            return false;
        result = new IndividualAddress(a, l, d);
        return true;
    }

    public override string ToString() => $"{Area}.{Line}.{Device}";

    public bool Equals(IndividualAddress other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is IndividualAddress o && Equals(o);
    public override int GetHashCode() => Raw;
    public static bool operator ==(IndividualAddress a, IndividualAddress b) => a.Equals(b);
    public static bool operator !=(IndividualAddress a, IndividualAddress b) => !a.Equals(b);
}

/// <summary>Group address style: three-level (main/middle/sub), two-level or free (x/y).</summary>
public enum GroupAddressStyle { ThreeLevel, TwoLevel, Free }

/// <summary>KNX group address, 16-bit encoded like ETS stores it.</summary>
public readonly struct GroupAddress : IEquatable<GroupAddress>
{
    public GroupAddress(ushort raw)
    {
        // KNX group addresses use the full 16-bit space (main groups 0-31); only the
        // reserved all-zero address is rejected.
        if (raw is 0) throw new ArgumentOutOfRangeException(nameof(raw));
        Raw = raw;
    }

    public ushort Raw { get; }

    public static GroupAddress ThreeLevel(byte main, byte middle, byte sub)
        => new((ushort)((main << 11) | (middle << 8) | sub));

    public static GroupAddress Parse(string value)
    {
        var p = value.Split('/');
        if (p.Length is 3 && byte.TryParse(p[0], out var m) && byte.TryParse(p[1], out var mi) && byte.TryParse(p[2], out var s))
            return ThreeLevel(m, mi, s);
        if (p.Length is 2 && ushort.TryParse(p[0], out var a) && ushort.TryParse(p[1], out var b))
            return new((ushort)((a << 11) | b));
        throw new FormatException($"'{value}' ist keine gültige Gruppenadresse.");
    }

    public override string ToString() => $"{Main}/{Middle}/{Sub}";

    public byte Main => (byte)((Raw >> 11) & 0x1F);
    public byte Middle => (byte)((Raw >> 8) & 0x07);
    public byte Sub => (byte)(Raw & 0xFF);

    public bool Equals(GroupAddress other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is GroupAddress o && Equals(o);
    public override int GetHashCode() => Raw;
    public static bool operator ==(GroupAddress a, GroupAddress b) => a.Equals(b);
    public static bool operator !=(GroupAddress a, GroupAddress b) => !a.Equals(b);
}
