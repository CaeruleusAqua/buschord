using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Knx.Bus.Security;

public enum KnxDataProtection : byte { Authentication = 0, AuthenticationAndConfidentiality = 0x10 }
public enum KnxDataSecureDecodeIssue { None, InvalidFrame, UnsupportedContext, UnsupportedService, InvalidSequence, InvalidPayload, AuthenticationFailed }

/// <summary>Cryptographically verified S-A_Data facts. Replay admission and persistence are separate, required operations.</summary>
public sealed record KnxDataSecureDecoded(CemiLDataFrame WireFrame, ulong Sequence, KnxDataProtection Protection, byte[] Apdu)
{
    public IndividualAddress Source => WireFrame.Source;
    public GroupAddress Group => new(WireFrame.DestinationRaw);
    public byte[] ToPlainCemi() => Cemi.EncodeLDataFrame(new(WireFrame.MessageCode, WireFrame.Control1, WireFrame.Control2,
        WireFrame.Source, WireFrame.DestinationRaw, Apdu, WireFrame.AdditionalInformation));
}

/// <summary>
/// Ordinary group S-A_Data using the mandatory four-byte authentication tag. This codec does not allocate counters,
/// admit senders or prevent replay; use KnxDataSecureGroupContext for those requirements.
/// </summary>
public static class KnxDataSecureCodec
{
    public const ulong MaximumSequence = 0xFFFFFFFFFFFF;
    public const int MaximumPlainApduLength = 242;
    public const int AuthenticationTagLength = 4;

    public static byte[] ProtectCemi(CemiLDataFrame plain, ulong sequence, ReadOnlySpan<byte> key, KnxDataProtection protection)
    {
        ValidatePlainCemi(plain); Key(key);
        if (sequence is 0 or > MaximumSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (protection is not (KnxDataProtection.Authentication or KnxDataProtection.AuthenticationAndConfidentiality)) throw new ArgumentOutOfRangeException(nameof(protection));
        var apdu = plain.Apdu; ValidateGroupApdu(apdu);
        var secured = new byte[apdu.Length + 13]; secured[0] = 3; secured[1] = 0xF1; secured[2] = (byte)protection;
        Write48(secured.AsSpan(3), sequence);
        Span<byte> block = stackalloc byte[16]; Block0(block, plain, sequence, protection == KnxDataProtection.Authentication ? 0 : apdu.Length);
        var tag = protection == KnxDataProtection.Authentication
            ? Mac(key, block, [secured[2], .. apdu], []) : Mac(key, block, [secured[2]], apdu);
        try
        {
            if (protection == KnxDataProtection.Authentication)
            { apdu.CopyTo(secured, 9); tag.AsSpan(0, 4).CopyTo(secured.AsSpan(secured.Length - 4)); }
            else
            {
                Counter0(block, plain, sequence);
                Transform(key, block, tag.AsSpan(0, 4), apdu, secured.AsSpan(secured.Length - 4), secured.AsSpan(9, apdu.Length));
            }
            return Cemi.EncodeLDataFrame(new(plain.MessageCode, plain.Control1, plain.Control2, plain.Source,
                plain.DestinationRaw, secured, plain.AdditionalInformation));
        }
        finally { CryptographicOperations.ZeroMemory(tag); CryptographicOperations.ZeroMemory(block); }
    }

    public static bool TryUnprotectCemi(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key,
        out KnxDataSecureDecoded? decoded, out KnxDataSecureDecodeIssue issue)
    {
        decoded = null; issue = KnxDataSecureDecodeIssue.InvalidFrame; byte[]? plain = null;
        try
        {
            Key(key); var frame = Cemi.ParseLDataFrame(cemi); ValidateEnvelope(frame);
            var apdu = frame.Apdu;
            if (apdu.Length < 15 || apdu[0] != 3 || apdu[1] != 0xF1) return false;
            issue = KnxDataSecureDecodeIssue.UnsupportedService;
            if (apdu[2] is not (0 or 0x10)) return false;
            var protection = (KnxDataProtection)apdu[2]; var sequence = Read48(apdu.AsSpan(3));
            issue = KnxDataSecureDecodeIssue.InvalidSequence;
            if (sequence is 0 or > MaximumSequence) return false;
            plain = new byte[apdu.Length - 13]; Span<byte> tag = stackalloc byte[4]; Span<byte> block = stackalloc byte[16];
            if (protection == KnxDataProtection.Authentication)
            { apdu.AsSpan(9, plain.Length).CopyTo(plain); apdu.AsSpan(apdu.Length - 4).CopyTo(tag); }
            else
            { Counter0(block, frame, sequence); Transform(key, block, apdu.AsSpan(apdu.Length - 4), apdu.AsSpan(9, plain.Length), tag, plain); }
            Block0(block, frame, sequence, protection == KnxDataProtection.Authentication ? 0 : plain.Length);
            var expected = protection == KnxDataProtection.Authentication
                ? Mac(key, block, [apdu[2], .. plain], []) : Mac(key, block, [apdu[2]], plain);
            try
            {
                issue = KnxDataSecureDecodeIssue.AuthenticationFailed;
                if (!CryptographicOperations.FixedTimeEquals(tag, expected.AsSpan(0, 4))) return false;
            }
            finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(tag); CryptographicOperations.ZeroMemory(block); }
            issue = KnxDataSecureDecodeIssue.InvalidPayload; ValidateGroupApdu(plain);
            decoded = new(frame, sequence, protection, plain); plain = null; issue = KnxDataSecureDecodeIssue.None; return true;
        }
        catch (NotSupportedException) { issue = KnxDataSecureDecodeIssue.UnsupportedContext; return false; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or OverflowException) { return false; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ValidateEnvelope(CemiLDataFrame frame)
    {
        if ((frame.Control2 & 0x80) == 0 || (frame.Control2 & 15) != 0 || frame.Source.Raw == 0 || frame.Source.Device == 255
            || frame.DestinationRaw == 0) throw new NotSupportedException("Only ordinary group-addressed Data Secure frames are supported.");
    }
    internal static void ValidatePlainCemi(CemiLDataFrame plain)
    { ArgumentNullException.ThrowIfNull(plain); ValidateEnvelope(plain); ValidateGroupApdu(plain.Apdu); }
    private static void ValidateGroupApdu(ReadOnlySpan<byte> apdu)
    {
        if (apdu.Length is < 2 or > MaximumPlainApduLength || apdu[0] != 0 || (apdu[1] & 0xC0) == 0xC0
            || (apdu[1] & 0xC0) == 0 && (apdu.Length != 2 || apdu[1] != 0)
            || apdu.Length > 2 && (apdu[1] & 0x3F) != 0) throw new NotSupportedException("Only canonical group-value read/response/write APDUs are supported.");
    }
    private static void Block0(Span<byte> block, CemiLDataFrame frame, ulong sequence, int length)
    {
        block.Clear(); Write48(block, sequence); BinaryPrimitives.WriteUInt16BigEndian(block[6..], frame.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(block[8..], frame.DestinationRaw); block[11] = (byte)(frame.Control2 & 0x8F);
        block[12] = 3; block[13] = 0xF1; block[15] = checked((byte)length);
    }
    private static void Counter0(Span<byte> block, CemiLDataFrame frame, ulong sequence)
    {
        block.Clear(); Write48(block, sequence); BinaryPrimitives.WriteUInt16BigEndian(block[6..], frame.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(block[8..], frame.DestinationRaw); block[14] = 1;
    }
    private static void Key(ReadOnlySpan<byte> key) { if (key.Length != 16) throw new ArgumentException("A Data Secure group key is 16 bytes."); }
    private static Aes AesFor(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create(); var copy = key.ToArray();
        try { aes.Key = copy; return aes; } catch { aes.Dispose(); throw; } finally { CryptographicOperations.ZeroMemory(copy); }
    }
    private static byte[] Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block, ReadOnlySpan<byte> additional, ReadOnlySpan<byte> payload)
    {
        var length = 18 + additional.Length + payload.Length; var input = new byte[(length + 15) / 16 * 16]; block.CopyTo(input);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(16), checked((ushort)additional.Length)); additional.CopyTo(input.AsSpan(18)); payload.CopyTo(input.AsSpan(18 + additional.Length));
        byte[]? cipher = null;
        try { using var aes = AesFor(key); cipher = aes.EncryptCbc(input, new byte[16], PaddingMode.None); return cipher[^16..]; }
        finally { CryptographicOperations.ZeroMemory(input); if (cipher is not null) CryptographicOperations.ZeroMemory(cipher); }
    }
    private static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> counter0, ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> payload, Span<byte> tagResult, Span<byte> payloadResult)
    {
        using var aes = AesFor(key); Span<byte> counter = stackalloc byte[16]; counter0.CopyTo(counter); Span<byte> stream = stackalloc byte[16];
        try
        {
            for (var offset = 0; offset < 4 + payload.Length; offset++)
            {
                if (offset % 16 == 0)
                {
                    if (offset > 0) for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { }
                    aes.EncryptEcb(counter, stream, PaddingMode.None);
                }
                if (offset < 4) tagResult[offset] = (byte)(tag[offset] ^ stream[offset]);
                else payloadResult[offset - 4] = (byte)(payload[offset - 4] ^ stream[offset % 16]);
            }
        }
        finally { CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(stream); }
    }
    private static void Write48(Span<byte> bytes, ulong value) { for (var i = 5; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; } }
    private static ulong Read48(ReadOnlySpan<byte> bytes) { ulong value = 0; for (var i = 0; i < 6; i++) value = (value << 8) | bytes[i]; return value; }
}
