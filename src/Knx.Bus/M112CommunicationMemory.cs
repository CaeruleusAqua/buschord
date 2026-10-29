using System.Buffers.Binary;
using Knx;

namespace Knx.Bus;

/// <summary>Validates retained absolute selector-zero RAM assignments; it never invents or repacks a manufacturer's pointers.</summary>
internal static class M112CommunicationMemory
{
    internal static void Validate(M112ApplicationImage baseline, M112ApplicationImage desired,
        IReadOnlyList<(ushort Address, byte[] Before, byte[] Desired)> regions)
    {
        var count = baseline.Objects.Count;
        if (count != desired.Objects.Count || baseline.CommunicationTableAddress != desired.CommunicationTableAddress)
            throw new IOException("The communication storage layout changed.");
        var address = baseline.CommunicationTableAddress;
        var length = checked(3 + 4 * count);
        var region = regions.Single(r => r.Address <= address && r.Address + r.Before.Length >= address + length);
        var offset = address - region.Address;
        var before = region.Before.AsSpan(offset, length); var after = region.Desired.AsSpan(offset, length);
        if (before[0] != count || after[0] != count || !before.Slice(1, 2).SequenceEqual(after.Slice(1, 2)))
            throw new IOException("The communication count or status pointer differs from its baseline.");
        for (var n = 0; n < count; n++)
        {
            var record = 3 + 4 * n;
            if (!before.Slice(record, 2).SequenceEqual(after.Slice(record, 2)) || ((before[record + 2] ^ after[record + 2]) & 0x20) != 0)
                throw new IOException($"Communication object {n} changed its data pointer or segment selector without a physical allocator.");
        }
        Check(baseline, before); Check(desired, after);
    }

    private static void Check(M112ApplicationImage image, ReadOnlySpan<byte> table)
    {
        if (image.Objects.Count == 0) return;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(table[1..]);
        // This absolute profile reserves at least one status byte for every declared slot.
        RequireRam(flags, image.Objects.Count);
        var used = new List<(int Start, int Length)> { (flags, image.Objects.Count) };
        foreach (var slot in image.Objects.Where(o => o.Active))
        {
            var offset = 3 + 4 * slot.Number;
            if ((table[offset + 2] & 0x20) != 0) throw new NotSupportedException($"Communication object {slot.Number} requires an explicit segment mapping.");
            var bits = DecodeSize((byte)(table[offset + 3] & 0x3F));
            if (bits != slot.SizeInBit) throw new IOException($"Communication object {slot.Number} type differs from its evaluated configuration ({bits}/{slot.SizeInBit} bits).");
            var width = checked((int)((bits + 7) / 8));
            var pointer = BinaryPrimitives.ReadUInt16BigEndian(table[offset..]);
            RequireRam(pointer, width);
            if (used.Any(s => pointer < s.Start + s.Length && s.Start < pointer + width))
                throw new IOException($"Communication object {slot.Number} at 0x{pointer:X4}+{width} overlaps active values or status flags.");
            used.Add((pointer, width));
        }
        void RequireRam(int address, int bytes)
        {
            if (bytes <= 0 || !image.Segments.Any(s => s.MemoryType == 2 && s.SegmentType == 0
                && s.Address <= address && s.Address + s.Size >= address + bytes))
                throw new IOException($"Communication storage 0x{address:X4}+{bytes} exceeds the declared data RAM allocation.");
        }
    }

    private static uint DecodeSize(byte code)
    {
        ReadOnlySpan<uint> sizes = [1,2,3,4,5,6,7,8,16,24,32,48,64,80,112,40,56,72,88,96,104];
        if (code < sizes.Length) return sizes[code];
        if (code is >= 21 and <= 56) return (uint)(code - 6) * 8;
        throw new NotSupportedException("Unsupported communication value size.");
    }
}
