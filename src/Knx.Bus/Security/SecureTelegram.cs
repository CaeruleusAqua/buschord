using System.Buffers.Binary;

namespace Knx.Bus.Security;

/// <summary>cEMI header octets a secure APDU authenticates (see <see cref="SecureTelegram"/>).</summary>
public readonly struct SecureFrameHeader
{
    public SecureFrameHeader(byte ctrl1, byte ctrl2, ushort source, ushort destination, byte apciOctet)
    {
        Ctrl1 = ctrl1;
        Ctrl2 = ctrl2;
        Source = source;
        Destination = destination;
        ApciOctet = apciOctet;
    }

    public byte Ctrl1 { get; }
    public byte Ctrl2 { get; }
    public ushort Source { get; }
    public ushort Destination { get; }

    /// <summary>Octet 7 of the APDU: TPCI plus the two high APCI bits.</summary>
    public byte ApciOctet { get; }

    /// <summary>Standard group frame header (ctrl1 standard, ctrl2 group).</summary>
    public static SecureFrameHeader Group(ushort source, ushort destination) =>
        new(Cemi.Ctrl1Standard, Cemi.Ctrl2Group, source, destination, SecureTelegram.SecureApciOctet);
}

/// <summary>
/// KNX Data Secure secure application message (S-A_Data) for group telegrams.
/// Layout: SCF (1) | SSI (1) | SQN (6) | plain data (n) | MAC tag (2 or 4).
/// </summary>
public sealed class SecureTelegram
{
    /// <summary>APCI octet of the secure service (APCI 0x03F1) in octet 8 of the APDU.</summary>
    public const byte SecureApci = 0xF1;

    /// <summary>Octet 7 for a secure APDU: TPCI 0 plus APCI bits 9..8 = 0b11.</summary>
    public const byte SecureApciOctet = 0x03;

    /// <summary>SCF, SSI and 48-bit sequence number ahead of the plain data.</summary>
    public const int HeaderLength = 8;

    public const int TagLength2 = 2;
    public const int TagLength4 = 4;

    /// <summary>Security control field; reserved bits are 0 for standard group telegrams.</summary>
    public byte SecurityControlField { get; init; }

    /// <summary>Secure service identifier; 0 for standard group services.</summary>
    public byte SecureServiceId { get; init; }

    /// <summary>48-bit sequence number of the sender for this key.</summary>
    public ulong SequenceNumber { get; init; }

    public required byte[] PlainData { get; init; }

    /// <summary>MAC length in bytes: 4 (standard) or 2 (short tag).</summary>
    public int TagLength { get; init; } = TagLength4;

    /// <summary>Length of the encoded S-A_Data block for a given plain data length.</summary>
    public static int EncodedLength(int plainDataLength, int tagLength = TagLength4) =>
        HeaderLength + plainDataLength + tagLength;

    /// <summary>Encodes and authenticates the telegram; returns the authenticated cEMI header plus S-A_Data.</summary>
    public static (SecureFrameHeader Header, byte[] SaData) Encrypt(
        ushort source, ushort destination, SecureTelegram telegram, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        var header = SecureFrameHeader.Group(source, destination);
        return (header, Encode(header, telegram, key));
    }

    /// <summary>Encodes S-A_Data for a header known upfront (the header is authenticated).</summary>
    public static byte[] Encode(SecureFrameHeader header, SecureTelegram telegram, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        ValidateTagLength(telegram.TagLength);

        var saData = new byte[EncodedLength(telegram.PlainData.Length, telegram.TagLength)];
        saData[0] = telegram.SecurityControlField;
        saData[1] = telegram.SecureServiceId;
        WriteSequence(saData.AsSpan(2), telegram.SequenceNumber);
        telegram.PlainData.CopyTo(saData.AsSpan(HeaderLength));

        var tag = ComputeTag(header, telegram, key);
        tag.CopyTo(saData.AsSpan(saData.Length - telegram.TagLength));
        return saData;
    }

    /// <summary>Verifies the MAC and decodes S-A_Data. A flipped byte or wrong key fails.</summary>
    public static bool TryDecrypt(SecureFrameHeader header, ReadOnlySpan<byte> saData,
        ReadOnlySpan<byte> key, out SecureTelegram? telegram, out string? error) =>
        TryDecrypt(header, saData, key, TagLength4, out telegram, out error);

    /// <summary>Verifies the MAC with the given tag length (2 or 4 bytes) and decodes S-A_Data.</summary>
    public static bool TryDecrypt(SecureFrameHeader header, ReadOnlySpan<byte> saData,
        ReadOnlySpan<byte> key, int tagLength, out SecureTelegram? telegram, out string? error)
    {
        telegram = null;
        error = null;
        try
        {
            SecureKeys.RequireKey(key);
            ValidateTagLength(tagLength);
            var dataLength = saData.Length - HeaderLength - tagLength;
            if (dataLength < 0)
            {
                error = "S-A_Data too short";
                return false;
            }

            var candidate = new SecureTelegram
            {
                SecurityControlField = saData[0],
                SecureServiceId = saData[1],
                SequenceNumber = ReadSequence(saData[2..]),
                PlainData = saData.Slice(HeaderLength, dataLength).ToArray(),
                TagLength = tagLength,
            };

            var expected = ComputeTag(header, candidate, key);
            if (!SecureKeys.FixedTimeEquals(expected, saData[^tagLength..]))
            {
                error = "MAC mismatch";
                return false;
            }

            telegram = candidate;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Truncated AES-128-CBC-MAC over the canonical header block and the plain data.</summary>
    private static byte[] ComputeTag(SecureFrameHeader header, SecureTelegram telegram, ReadOnlySpan<byte> key)
    {
        var headerBlock = new byte[16];
        headerBlock[0] = header.Ctrl1;
        headerBlock[1] = header.Ctrl2;
        BinaryPrimitives.WriteUInt16BigEndian(headerBlock.AsSpan(2), header.Source);
        BinaryPrimitives.WriteUInt16BigEndian(headerBlock.AsSpan(4), header.Destination);
        headerBlock[6] = header.ApciOctet;
        headerBlock[7] = SecureApci;
        headerBlock[8] = telegram.SecurityControlField;
        headerBlock[9] = telegram.SecureServiceId;
        WriteSequence(headerBlock.AsSpan(10), telegram.SequenceNumber);

        var mac = SecureKeys.CbcMac(key, headerBlock, telegram.PlainData);
        return mac[^telegram.TagLength..];
    }

    private static void ValidateTagLength(int tagLength)
    {
        if (tagLength is not (TagLength2 or TagLength4))
            throw new ArgumentOutOfRangeException(nameof(tagLength), "secure MAC tag must be 2 or 4 bytes");
    }

    private static void WriteSequence(Span<byte> span, ulong sequence)
    {
        span[0] = (byte)(sequence >> 40);
        span[1] = (byte)(sequence >> 32);
        span[2] = (byte)(sequence >> 24);
        span[3] = (byte)(sequence >> 16);
        span[4] = (byte)(sequence >> 8);
        span[5] = (byte)sequence;
    }

    private static ulong ReadSequence(ReadOnlySpan<byte> span) =>
        ((ulong)span[0] << 40) | ((ulong)span[1] << 32) | ((ulong)span[2] << 24)
        | ((ulong)span[3] << 16) | ((ulong)span[4] << 8) | span[5];
}

/// <summary>cEMI L_Data wrapper that carries a secure APDU (routing and tunnelling frames).</summary>
public static class SecureApdu
{
    /// <summary>Builds an L_Data.ind routing body with the secure APDU appended.</summary>
    public static byte[] BuildLDataIndication(SecureFrameHeader header, ReadOnlySpan<byte> saData)
    {
        var frame = new byte[10 + saData.Length];
        frame[0] = (byte)CemiMessageCode.LDataInd;
        frame[1] = 0x00; // additional info length
        frame[2] = header.Ctrl1;
        frame[3] = header.Ctrl2;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), header.Source);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), header.Destination);
        frame[8] = header.ApciOctet;
        frame[9] = SecureTelegram.SecureApci;
        saData.CopyTo(frame.AsSpan(10));
        return frame;
    }

    /// <summary>Builds a secure routing body for a telegram, authenticated with the group key.</summary>
    public static byte[] BuildLDataIndication(ushort source, ushort destination,
        SecureTelegram telegram, ReadOnlySpan<byte> key)
    {
        var (header, saData) = SecureTelegram.Encrypt(source, destination, telegram, key);
        return BuildLDataIndication(header, saData);
    }

    /// <summary>Reads header and S-A_Data from a cEMI L_Data frame with the secure APCI.</summary>
    public static bool TryParseLDataIndication(ReadOnlySpan<byte> cemi,
        out SecureFrameHeader header, out byte[]? saData)
    {
        header = default;
        saData = null;
        if (cemi.Length < 10) return false;
        if (cemi[0] != (byte)CemiMessageCode.LDataInd && cemi[0] != (byte)CemiMessageCode.LDataReq
            && cemi[0] != (byte)CemiMessageCode.LDataCon)
            return false;
        if (cemi[9] != SecureTelegram.SecureApci) return false;

        saData = cemi[10..].ToArray();
        header = new SecureFrameHeader(cemi[2], cemi[3],
            BinaryPrimitives.ReadUInt16BigEndian(cemi[4..]), BinaryPrimitives.ReadUInt16BigEndian(cemi[6..]),
            cemi[8]);
        return true;
    }

    /// <summary>Reads and authenticates a secure cEMI frame with the given key.</summary>
    public static bool TryDecryptLDataIndication(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key,
        out SecureTelegram? telegram, out string? error)
    {
        telegram = null;
        if (!TryParseLDataIndication(cemi, out var header, out var saData))
        {
            error = "not a secure L_Data frame";
            return false;
        }
        return SecureTelegram.TryDecrypt(header, saData, key, out telegram, out error);
    }
}
