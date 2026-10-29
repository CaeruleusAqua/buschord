namespace Knx.Bus.Security;

/// <summary>Unsupported legacy cEMI metadata retained for source compatibility only.</summary>
[Obsolete("Unsupported legacy Data Secure profile. No Data Secure traffic is generated or authenticated by this API.")]
public readonly struct SecureFrameHeader
{
    public SecureFrameHeader(byte ctrl1, byte ctrl2, ushort source, ushort destination, byte apciOctet)
    { Ctrl1 = ctrl1; Ctrl2 = ctrl2; Source = source; Destination = destination; ApciOctet = apciOctet; }
    public byte Ctrl1 { get; }
    public byte Ctrl2 { get; }
    public ushort Source { get; }
    public ushort Destination { get; }
    public byte ApciOctet { get; }
    public static SecureFrameHeader Group(ushort source, ushort destination) => new(Cemi.Ctrl1Standard, Cemi.Ctrl2Group, source, destination, 3);
}

/// <summary>Compatibility gates for unsupported legacy Data Secure framing. These methods never return protected or authenticated data.</summary>
[Obsolete("Unsupported legacy Data Secure profile. KNX Data Secure is not enabled by this API.")]
public sealed class SecureTelegram
{
    public const byte SecureApci = 0xF1, SecureApciOctet = 3;
    public const int HeaderLength = 8, TagLength2 = 2, TagLength4 = 4;
    public byte SecurityControlField { get; init; }
    public byte SecureServiceId { get; init; }
    public ulong SequenceNumber { get; init; }
    public required byte[] PlainData { get; init; }
    public int TagLength { get; init; } = TagLength4;
    public static int EncodedLength(int plainDataLength, int tagLength = TagLength4) => throw new NotSupportedException("Unsupported legacy Data Secure profile.");
    public static (SecureFrameHeader Header, byte[] SaData) Encrypt(ushort source, ushort destination, SecureTelegram telegram, ReadOnlySpan<byte> key)
        => throw new NotSupportedException("Unsupported legacy Data Secure profile.");
    public static byte[] Encode(SecureFrameHeader header, SecureTelegram telegram, ReadOnlySpan<byte> key)
        => throw new NotSupportedException("Unsupported legacy Data Secure profile.");
    public static bool TryDecrypt(SecureFrameHeader header, ReadOnlySpan<byte> saData, ReadOnlySpan<byte> key, out SecureTelegram? telegram, out string? error)
    { telegram = null; error = "Unsupported legacy Data Secure profile."; return false; }
    public static bool TryDecrypt(SecureFrameHeader header, ReadOnlySpan<byte> saData, ReadOnlySpan<byte> key, int tagLength, out SecureTelegram? telegram, out string? error)
    { telegram = null; error = "Unsupported legacy Data Secure profile."; return false; }
}

/// <summary>Compatibility gates for unsupported legacy Data Secure cEMI frames.</summary>
[Obsolete("Unsupported legacy Data Secure profile. KNX Data Secure is not enabled by this API.")]
public static class SecureApdu
{
    public static byte[] BuildLDataIndication(SecureFrameHeader header, ReadOnlySpan<byte> saData) => throw new NotSupportedException("Unsupported legacy Data Secure profile.");
    public static byte[] BuildLDataIndication(ushort source, ushort destination, SecureTelegram telegram, ReadOnlySpan<byte> key)
        => throw new NotSupportedException("Unsupported legacy Data Secure profile.");
    public static bool TryParseLDataIndication(ReadOnlySpan<byte> cemi, out SecureFrameHeader header, out byte[]? saData)
    { header = default; saData = null; return false; }
    public static bool TryDecryptLDataIndication(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key, out SecureTelegram? telegram, out string? error)
    { telegram = null; error = "Unsupported legacy Data Secure profile."; return false; }
}
