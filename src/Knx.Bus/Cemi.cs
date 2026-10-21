using System.Buffers.Binary;

namespace Knx.Bus;

/// <summary>cEMI message codes.</summary>
public enum CemiMessageCode : byte
{
    LDataReq = 0x11,
    LDataCon = 0x2E,
    LDataInd = 0x29,
}

/// <summary>APCI (application layer service codes) used on the bus.</summary>
public enum Apci
{
    GroupValueRead = 0x000,
    GroupValueResponse = 0x040,
    GroupValueWrite = 0x080,
    IndividualAddressWrite = 0x0C0,
    IndividualAddressRead = 0x100,
    IndividualAddressResponse = 0x140,
    MemoryRead = 0x200,
    MemoryResponse = 0x240,
    MemoryWrite = 0x280,
    DeviceDescriptorRead = 0x300,
    DeviceDescriptorResponse = 0x340,
    Restart = 0x380,
    Escape = 0xF00,
}

/// <summary>A bus telegram (general purpose, applies to tunnel and routing).</summary>
public sealed class Telegram
{
    public required IndividualAddress Source { get; init; }
    public required IndividualAddress Destination { get; init; }
    public bool IsGroupAddress { get; init; }

    /// <summary>Hop count from ctrl2 bits 6..4; routers decrement it (6 on the sender, 4 after two hops).</summary>
    public byte HopCount { get; init; } = 6;

    public Apci Apci { get; init; }

    /// <summary>
    /// Bit width of a 1..6 bit value carried inline in the second APDU octet, or null when
    /// <see cref="Data"/> occupies whole octets after it. A decoded 1-bit group write is 6-bit
    /// inline data with the value in the low bits.
    /// </summary>
    public int? DataBits { get; init; }

    public required byte[] Data { get; init; }

    /// <summary>Copy with another source address (a tunnel must send with its assigned address).</summary>
    public Telegram WithSource(IndividualAddress source) => new()
    {
        Source = source,
        Destination = Destination,
        IsGroupAddress = IsGroupAddress,
        HopCount = HopCount,
        Apci = Apci,
        DataBits = DataBits,
        Data = Data,
    };
}

/// <summary>
/// cEMI L_Data encode/decode for KNXnet/IP tunnelling and routing. Wire layout
/// (verified against frames forwarded by an MDT SCN-IP100.03):
/// <list type="bullet">
/// <item>ctrl2 bit 7 is the group address flag; bits 6..4 are the hop count.</item>
/// <item>APCI = ((octet 7 &amp; 0x03) &lt;&lt; 8) | (octet 8 &amp; 0xC0); octet 7 bits 7..6 are TPCI.</item>
/// <item>1..6 bit values live in octet 8 bits 5..0, longer payloads start at octet 9.</item>
/// <item>The length octet is the APDU octet count minus one; 0x40 in it marks an extended frame.</item>
/// </list>
/// </summary>
public static class Cemi
{
    public const byte Ctrl1Standard = 0xBC;
    public const byte Ctrl2Group = 0xE0;      // group flag, hop 6
    public const byte Ctrl2Individual = 0x60; // hop 6, no group flag
    public const byte GroupAddressFlag = 0x80;
    public const int MaxStandardApduOctets = 16;

    /// <summary>Builds an L_Data.req frame for tunnelling.</summary>
    public static byte[] LDataRequest(Telegram telegram)
    {
        var apdu = BuildApdu(telegram, out var extended);
        var headerLength = extended ? 10 : 9;
        var frame = new byte[headerLength + apdu.Length];
        frame[0] = (byte)CemiMessageCode.LDataReq;
        frame[1] = 0x00; // additional info length
        frame[2] = Ctrl1Standard;
        frame[3] = Control2(telegram.IsGroupAddress, telegram.HopCount);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), telegram.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), telegram.Destination.Raw);
        var length = (ushort)(apdu.Length - 1);
        if (extended)
        {
            // ponytail: extended length word (0x40 flag + 6/8 bit length) is spec-derived and has no
            // real-device fixture yet; standard frames (all current read paths) are capture-verified.
            frame[8] = (byte)(0x40 | (length >> 8));
            frame[9] = (byte)length;
        }
        else
        {
            frame[8] = (byte)length;
        }
        apdu.CopyTo(frame.AsSpan(headerLength));
        return frame;
    }

    /// <summary>ctrl2: group flag in bit 7, hop count in bits 6..4, standard frame format in bits 3..0.</summary>
    public static byte Control2(bool isGroupAddress, byte hopCount)
    {
        if (hopCount > 7) throw new ArgumentOutOfRangeException(nameof(hopCount), "hop count is three bits (0..7)");
        return (byte)((isGroupAddress ? GroupAddressFlag : 0x00) | (hopCount << 4));
    }

    /// <summary>Builds the APDU (octet 7, octet 8 and payload); sets <paramref name="extended"/> when it exceeds a standard frame.</summary>
    private static byte[] BuildApdu(Telegram telegram, out bool extended)
    {
        var apci = (ushort)telegram.Apci;
        if (apci > 0x3FF)
            throw new ArgumentOutOfRangeException(nameof(telegram), $"APCI 0x{apci:X} needs an escaped APDU");
        var data = telegram.Data;
        var inlineBits = telegram.Apci switch
        {
            Apci.DeviceDescriptorRead => 4, // descriptor type is the management parameter
            _ => telegram.DataBits,
        };
        if (inlineBits is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(telegram), "inline data width must be 1..6 bits");
        var inline = inlineBits is not null;
        if (inline && data.Length > 1)
            throw new ArgumentException("an inline APDU value is one octet", nameof(telegram));
        var value = data.Length == 1 ? data[0] : 0;
        if (inline && (value >> inlineBits!.Value) != 0)
            throw new ArgumentOutOfRangeException(nameof(telegram),
                $"value 0x{value:X2} does not fit in {inlineBits} bit(s)");

        var apduOctets = inline ? 2 : 2 + data.Length;
        extended = apduOctets > MaxStandardApduOctets;
        if (apduOctets > 1024)
            throw new ArgumentOutOfRangeException(nameof(telegram), "APDU longer than the extended frame limit");
        var apdu = new byte[apduOctets];
        apdu[0] = (byte)((apci >> 8) & 0x03); // TPCI 0, APCI bits 9..8
        apdu[1] = (byte)(apci & 0xC0);        // APCI bits 7..6
        if (inline)
        {
            apdu[1] |= (byte)(value & 0x3F);
        }
        else
        {
            data.CopyTo(apdu.AsSpan(2));
        }
        return apdu;
    }

    /// <summary>Parses an L_Data frame (req/ind/con are structurally identical here).</summary>
    public static Telegram ParseLData(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) throw new FormatException("L_Data frame too short");
        var offset = 2 + frame[1]; // skip cEMI message code and additional info
        if (frame.Length < offset + 8) throw new FormatException("L_Data frame too short");

        var ctrl2 = frame[offset + 1];
        var src = new IndividualAddress(BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]));
        var dstRaw = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 4)..]);

        var lengthOctet = frame[offset + 6];
        ReadOnlySpan<byte> apdu;
        if ((lengthOctet & 0xC0) == 0x40)
        {
            if (frame.Length < offset + 9) throw new FormatException("extended L_Data frame too short");
            var length = ((lengthOctet & 0x3F) << 8) | frame[offset + 7];
            if (frame.Length < offset + 9 + length) throw new FormatException("extended L_Data frame truncated");
            apdu = frame.Slice(offset + 8, length + 1);
        }
        else
        {
            var length = lengthOctet & 0x0F;
            if (frame.Length < offset + 8 + length) throw new FormatException("L_Data frame truncated");
            apdu = frame.Slice(offset + 7, length + 1);
        }
        if (apdu.Length < 2) throw new FormatException("APDU shorter than two octets");

        var apci = (Apci)(((apdu[0] & 0x03) << 8) | (apdu[1] & 0xC0));
        byte[] data;
        int? dataBits = null;
        if (apdu.Length == 2)
        {
            switch (apci)
            {
                case Apci.GroupValueRead:
                case Apci.IndividualAddressRead:
                    data = [];
                    break;
                case Apci.DeviceDescriptorRead:
                    data = [(byte)(apdu[1] & 0x0F)];
                    dataBits = 4;
                    break;
                default:
                    data = [(byte)(apdu[1] & 0x3F)];
                    dataBits = 6;
                    break;
            }
        }
        else
        {
            data = apdu[2..].ToArray();
        }

        return new Telegram
        {
            Source = src,
            Destination = new IndividualAddress(dstRaw),
            IsGroupAddress = (ctrl2 & GroupAddressFlag) != 0,
            HopCount = (byte)((ctrl2 >> 4) & 0x07),
            Apci = apci,
            DataBits = dataBits,
            Data = data,
        };
    }

    /// <summary>Group address of a telegram (only valid for group-addressed telegrams).</summary>
    public static Knx.GroupAddress DestinationGroup(Telegram telegram) =>
        new(telegram.Destination.Raw);
}
