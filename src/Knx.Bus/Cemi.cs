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
    IndividualAddressSerialRead = 0x3DC,
    IndividualAddressSerialResponse = 0x3DD,
    IndividualAddressSerialWrite = 0x3DE,
    Escape = 0xF00,
}

public enum TelegramPriority : byte { System, Normal, Urgent, Low }

/// <summary>A bus telegram (general purpose, applies to tunnel and routing).</summary>
public sealed class Telegram
{
    public required IndividualAddress Source { get; init; }
    public required IndividualAddress Destination { get; init; }
    public bool IsGroupAddress { get; init; }
    public TelegramPriority Priority { get; init; } = TelegramPriority.Low;

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
        Priority = Priority,
        HopCount = HopCount,
        Apci = Apci,
        DataBits = DataBits,
        Data = Data,
    };
}

/// <summary>
/// Owned raw cEMI L_Data fields. <see cref="Apdu"/> contains the complete TPDU, including TPCI;
/// it is not interpreted as a Telegram or an application service. Array inputs and outputs are copied.
/// </summary>
public sealed class CemiLDataFrame
{
    private readonly byte[] _apdu, _additionalInformation;
    public CemiMessageCode MessageCode { get; }
    public byte Control1 { get; }
    public byte Control2 { get; }
    public IndividualAddress Source { get; }
    public ushort DestinationRaw { get; }
    public byte[] Apdu => _apdu.ToArray();
    public byte[] AdditionalInformation => _additionalInformation.ToArray();
    internal ReadOnlySpan<byte> ApduSpan => _apdu;
    internal ReadOnlySpan<byte> AdditionalInformationSpan => _additionalInformation;

    public CemiLDataFrame(CemiMessageCode messageCode, byte control1, byte control2,
        IndividualAddress source, ushort destinationRaw, ReadOnlySpan<byte> apdu,
        ReadOnlySpan<byte> additionalInformation = default)
    {
        if (apdu.Length is < 1 or > Cemi.MaxExtendedApduOctets) throw new ArgumentOutOfRangeException(nameof(apdu));
        if (additionalInformation.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(additionalInformation));
        MessageCode = messageCode; Control1 = control1; Control2 = control2; Source = source; DestinationRaw = destinationRaw;
        _apdu = apdu.ToArray(); _additionalInformation = additionalInformation.ToArray();
    }
}

/// <summary>
/// cEMI L_Data encode/decode for KNXnet/IP tunnelling and routing. Wire layout
/// Supported frame layout:
/// <list type="bullet">
/// <item>ctrl2 bit 7 is the group address flag; bits 6..4 are the hop count.</item>
/// <item>APCI = ((octet 7 &amp; 0x03) &lt;&lt; 8) | (octet 8 &amp; 0xC0); octet 7 bits 7..6 are TPCI.</item>
/// <item>1..6 bit values live in octet 8 bits 5..0, longer payloads start at octet 9.</item>
/// <item>One NPDU length octet contains the TPDU octet count minus one; 255 is the unsupported escape value.</item>
/// <item>Control field 1 bit 7 selects standard (1) or extended (0) framing; the header has the same length.</item>
/// </list>
/// </summary>
public static class Cemi
{
    public const byte Ctrl1Standard = 0xBC;
    public const byte Ctrl2Group = 0xE0;      // group flag, hop 6
    public const byte Ctrl2Individual = 0x60; // hop 6, no group flag
    public const byte GroupAddressFlag = 0x80;
    public const int MaxStandardApduOctets = 16;
    public const int MaxExtendedApduOctets = 255;
    public const int MaxNpduLength = MaxExtendedApduOctets - 1;

    /// <summary>Builds an L_Data.req frame for tunnelling.</summary>
    public static byte[] LDataRequest(Telegram telegram)
    {
        ArgumentNullException.ThrowIfNull(telegram); ArgumentNullException.ThrowIfNull(telegram.Data);
        if ((byte)telegram.Priority > 3) throw new ArgumentOutOfRangeException(nameof(telegram), "Priority is two bits.");
        return EncodeLDataFrame(new(CemiMessageCode.LDataReq, (byte)((Ctrl1Standard & ~0x0C) | ((byte)telegram.Priority << 2)),
            Control2(telegram.IsGroupAddress, telegram.HopCount), telegram.Source, telegram.Destination.Raw, BuildApdu(telegram)));
    }

    /// <summary>Encodes one raw L_Data frame. Frame type follows the current TPDU length; all other supported flags are retained.</summary>
    public static byte[] EncodeLDataFrame(CemiLDataFrame data)
    {
        ArgumentNullException.ThrowIfNull(data);
        RequireLData(data.MessageCode, data.Control2);
        var additional = data.AdditionalInformationSpan; ValidateAdditionalInformation(additional);
        var apdu = data.ApduSpan;
        var frame = new byte[9 + additional.Length + apdu.Length];
        frame[0] = (byte)data.MessageCode; frame[1] = (byte)additional.Length;
        additional.CopyTo(frame.AsSpan(2)); var offset = 2 + additional.Length;
        frame[offset] = (byte)((data.Control1 & 0x7F) | (apdu.Length <= MaxStandardApduOctets ? 0x80 : 0));
        frame[offset + 1] = data.Control2;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 2), data.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 4), data.DestinationRaw);
        frame[offset + 6] = (byte)(apdu.Length - 1);
        apdu.CopyTo(frame.AsSpan(offset + 7)); return frame;
    }

    /// <summary>Parses the raw TPDU and supported address format without interpreting APCI or discarding TPCI.</summary>
    public static CemiLDataFrame ParseLDataFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) throw new FormatException("L_Data frame too short");
        var offset = 2 + frame[1];
        if (frame.Length < offset + 8) throw new FormatException("L_Data frame too short");
        RequireLData((CemiMessageCode)frame[0], frame[offset + 1]);
        var additional = frame.Slice(2, frame[1]); ValidateAdditionalInformation(additional);
        var npduLength = frame[offset + 6];
        if (npduLength > MaxNpduLength) throw new FormatException("The NPDU length escape is not supported");
        if (frame.Length != offset + 8 + npduLength) throw new FormatException("L_Data length does not match its NPDU length");
        // 3/6/3 §4.1.5.2.3 requires receivers to tolerate the frame-type choice.
        // XKNX CEMILData.from_knx retains it rather than enforcing a length/type match.
        return new((CemiMessageCode)frame[0], frame[offset], frame[offset + 1],
            new(BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..])),
            BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 4)..]), frame[(offset + 7)..], additional);
    }

    private static void RequireLData(CemiMessageCode code, byte control2)
    {
        if (code is not (CemiMessageCode.LDataReq or CemiMessageCode.LDataInd or CemiMessageCode.LDataCon))
            throw new FormatException("Unsupported cEMI message code");
        if ((control2 & 15) != 0) throw new FormatException("Unsupported cEMI extended frame format");
    }
    private static void ValidateAdditionalInformation(ReadOnlySpan<byte> information)
    {
        // Preserve unknown information types, but require complete type/length/value records.
        for (var offset = 0; offset < information.Length;)
        {
            if (information.Length - offset < 2) throw new FormatException("Truncated cEMI additional information header");
            var size = information[offset + 1]; offset += 2;
            if (size > information.Length - offset) throw new FormatException("Truncated cEMI additional information value");
            offset += size;
        }
    }

    /// <summary>ctrl2: group flag in bit 7, hop count in bits 6..4, standard frame format in bits 3..0.</summary>
    public static byte Control2(bool isGroupAddress, byte hopCount)
    {
        if (hopCount > 7) throw new ArgumentOutOfRangeException(nameof(hopCount), "hop count is three bits (0..7)");
        return (byte)((isGroupAddress ? GroupAddressFlag : 0x00) | (hopCount << 4));
    }

    /// <summary>Builds the APDU (TPCI/APCI octets and payload).</summary>
    private static byte[] BuildApdu(Telegram telegram)
    {
        var apci = (int)telegram.Apci;
        if (apci < 0 || apci > 0x3FF)
            throw new ArgumentOutOfRangeException(nameof(telegram), $"APCI 0x{apci:X} needs an escaped APDU");
        if (apci < 0x3C0 && (apci & 0x3F) != 0)
            throw new ArgumentOutOfRangeException(nameof(telegram), "This APCI family requires its explicit raw management codec.");
        var data = telegram.Data;
        var inlineBits = telegram.Apci switch
        {
            Apci.DeviceDescriptorRead => 4, // descriptor type is the management parameter
            _ => telegram.DataBits,
        };
        if (inlineBits is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(telegram), "inline data width must be 1..6 bits");
        var inline = inlineBits is not null;
        var fullService = apci >= 0x3C0;
        if (fullService && inline)
            throw new ArgumentException("A ten-bit APCI service has no inline data bits.", nameof(telegram));
        if (inline && data.Length > 1)
            throw new ArgumentException("an inline APDU value is one octet", nameof(telegram));
        var value = data.Length == 1 ? data[0] : 0;
        if (inline && (value >> inlineBits!.Value) != 0)
            throw new ArgumentOutOfRangeException(nameof(telegram),
                $"value 0x{value:X2} does not fit in {inlineBits} bit(s)");

        var apduOctets = inline ? 2 : 2 + data.Length;
        if (apduOctets > MaxExtendedApduOctets)
            throw new ArgumentOutOfRangeException(nameof(telegram), "APDU longer than the extended frame limit");
        var apdu = new byte[apduOctets];
        apdu[0] = (byte)((apci >> 8) & 0x03); // TPCI 0, APCI bits 9..8
        apdu[1] = (byte)(apci & (fullService ? 0xFF : 0xC0));
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
        var raw = ParseLDataFrame(frame); var ctrl2 = raw.Control2;
        var apdu = raw.ApduSpan;
        if (apdu.Length < 2) throw new FormatException("APDU shorter than two octets");

        var baseService = ((apdu[0] & 0x03) << 8) | (apdu[1] & 0xC0);
        var fullService = baseService == 0x3C0;
        var apci = (Apci)(baseService | (fullService ? apdu[1] & 0x3F : 0));
        byte[] data;
        int? dataBits = null;
        if (apdu.Length == 2 && !fullService)
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
            Source = raw.Source,
            Destination = new IndividualAddress(raw.DestinationRaw),
            IsGroupAddress = (ctrl2 & GroupAddressFlag) != 0,
            Priority = (TelegramPriority)((raw.Control1 >> 2) & 3),
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
