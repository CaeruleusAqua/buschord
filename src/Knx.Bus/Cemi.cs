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
    public Apci Apci { get; init; }
    public required byte[] Data { get; init; }
}

/// <summary>
/// cEMI L_Data encode/decode for KNXnet/IP tunnelling and routing
/// (standard frames, APCI in the high nibble of octet 7).
/// </summary>
public static class Cemi
{
    public const byte Ctrl1Standard = 0xBC;
    public const byte Ctrl2Group = 0xE0;
    public const byte Ctrl2Individual = 0xD0;

    /// <summary>Builds an L_Data.req frame for tunnelling.</summary>
    public static byte[] LDataRequest(Telegram telegram)
    {
        var apdu = BuildApdu(telegram);
        var frame = new byte[2 + apdu.Length];
        frame[0] = (byte)CemiMessageCode.LDataReq;
        frame[1] = 0x00; // additional info length
        apdu.CopyTo(frame, 2);
        return frame;
    }

    /// <summary>Builds an L_Data frame body (ctrl1/ctrl2/src/dst/npci/apci/data).</summary>
    private static byte[] BuildApdu(Telegram telegram)
    {
        var apci = (ushort)telegram.Apci;
        var apciNibble = (ushort)(apci >> 6); // 4-bit APCI service code, octet 7 high nibble
        if (apciNibble > 0x0F || (apci & 0x3F) != 0 || apciNibble == 0x0F)
            throw new ArgumentOutOfRangeException(nameof(telegram), $"APCI 0x{apci:X} needs an extended/escaped frame");
        var data = telegram.Data;
        var adp = new byte[8 + data.Length]; // ctrl1..npci (7) + apci octet (1) + data
        adp[0] = Ctrl1Standard;
        adp[1] = telegram.IsGroupAddress ? Ctrl2Group : Ctrl2Individual;
        BinaryPrimitives.WriteUInt16BigEndian(adp.AsSpan(2), telegram.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(adp.AsSpan(4), telegram.Destination.Raw);
        // NPCI/TPCI: unnumbered data connection, APDU length in low nibble
        adp[6] = (byte)(1 + data.Length);
        adp[7] = (byte)(apciNibble << 4);
        data.CopyTo(adp.AsSpan(8));
        return adp;
    }

    /// <summary>Parses an L_Data frame (req/ind/con are structurally identical here).</summary>
    public static Telegram ParseLData(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 10) throw new FormatException("L_Data frame too short");
        // frame: [mc][addil][ctrl1][ctrl2][src(2)][dst(2)][npci][apci+data...]
        var ctrl2 = frame[3];
        var src = new IndividualAddress(BinaryPrimitives.ReadUInt16BigEndian(frame[4..]));
        var dstRaw = BinaryPrimitives.ReadUInt16BigEndian(frame[6..]);
        var isGroup = (ctrl2 & 0xF0) == 0xE0; // standard group frames carry ctrl2 base 0xE0, individual 0xD0
        var npci = frame[8];
        var apciNibble = (byte)(frame[9] >> 4);
        var data = frame.Length > 10 ? frame[10..].ToArray() : [];
        return new Telegram
        {
            Source = src,
            Destination = new IndividualAddress(dstRaw),
            IsGroupAddress = isGroup,
            Apci = (Apci)(apciNibble << 6),
            Data = data,
        };
    }

    /// <summary>Group address of a telegram (only valid for group-addressed telegrams).</summary>
    public static Knx.GroupAddress DestinationGroup(Telegram telegram) =>
        new(telegram.Destination.Raw);
}
