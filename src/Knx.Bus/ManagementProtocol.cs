using System.Buffers.Binary;
using Knx;

namespace Knx.Bus;

/// <summary>Standard cEMI transport frame, including one-octet transport controls.</summary>
public sealed record ManagementTransportFrame(IndividualAddress Source, IndividualAddress Destination, byte[] Tpdu)
{
    public byte[] ToRequest()
    {
        if (Tpdu.Length is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(Tpdu));
        var frame = new byte[9 + Tpdu.Length];
        frame[0] = (byte)CemiMessageCode.LDataReq;
        frame[2] = Cemi.Ctrl1Standard;
        frame[3] = Cemi.Ctrl2Individual;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), Destination.Raw);
        frame[8] = (byte)(Tpdu.Length - 1);
        Tpdu.CopyTo(frame, 9);
        return frame;
    }

    public static bool TryParseIndication(ReadOnlySpan<byte> frame, out ManagementTransportFrame? result)
    {
        result = null;
        if (frame.Length < 2 || frame[0] != (byte)CemiMessageCode.LDataInd) return false;
        var start = 2 + frame[1];
        if (frame.Length < start + 8 || (frame[start] & 0x80) == 0
            || (frame[start + 1] & Cemi.GroupAddressFlag) != 0 || frame[start + 6] > 15) return false;
        var length = frame[start + 6] + 1;
        if (frame.Length != start + 7 + length) return false;
        result = new(new(BinaryPrimitives.ReadUInt16BigEndian(frame[(start + 2)..])),
            new(BinaryPrimitives.ReadUInt16BigEndian(frame[(start + 4)..])),
            frame.Slice(start + 7, length).ToArray());
        return true;
    }
}

/// <summary>Bounded standard-frame management APDUs. Memory counts are inline; property services use all ten APCI bits.</summary>
public static class ManagementApdu
{
    public const int PropertyRead = 0x3D5, PropertyResponse = 0x3D6, PropertyWrite = 0x3D7;
    public const int MemoryResponse = 0x240;

    public static int Service(ReadOnlySpan<byte> apdu)
    {
        if (apdu.Length < 2 || (apdu[0] & 0x80) != 0) throw new FormatException("Not a management data APDU.");
        var service = ((apdu[0] & 3) << 8) | apdu[1];
        return (service & 0x3C0) == 0x3C0 ? service : service & 0x3C0;
    }

    public static byte[] ReadMemory(ushort address, byte count)
    {
        if (count is < 1 or > 12 || address + count > 0x10000) throw new ArgumentOutOfRangeException(nameof(count));
        return [0x02, count, (byte)(address >> 8), (byte)address];
    }

    public static byte[] WriteMemory(ushort address, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 12 || address + bytes.Length > 0x10000)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        return [0x02, (byte)(0x80 | bytes.Length), (byte)(address >> 8), (byte)address, .. bytes];
    }

    public static byte[] ReadProperty(byte objectIndex, byte propertyId, ushort start = 1, byte count = 1)
    {
        if (start > 0xFFF || count is < 1 or > 15 || (start == 0 && count != 1))
            throw new ArgumentOutOfRangeException(nameof(start));
        return [0x03, 0xD5, objectIndex, propertyId, (byte)((count << 4) | (start >> 8)), (byte)start];
    }

    public static byte[] WriteProperty(byte objectIndex, byte propertyId, ushort start, byte count,
        ReadOnlySpan<byte> bytes)
    {
        if (start > 0xFFF || count is < 1 or > 15 || (start == 0 && count != 1)
            || start + count > 0x1000)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (bytes.Length is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(bytes));
        return [0x03, 0xD7, objectIndex, propertyId, (byte)((count << 4) | (start >> 8)), (byte)start, .. bytes];
    }

}
