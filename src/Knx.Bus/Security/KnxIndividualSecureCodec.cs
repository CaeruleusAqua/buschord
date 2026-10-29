using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Knx.Bus.Security;

public enum KnxIndividualSecureKeyKind { PointToPoint, ToolAccess }
public enum KnxIndividualSecureDecodeIssue { None, InvalidFrame, UnsupportedContext, InvalidSequence, InvalidPayload, AuthenticationFailed }

/// <summary>Authenticated individual data only; the caller still must commit its replay floor before releasing Tpdu.</summary>
public sealed record KnxIndividualSecureData(CemiLDataFrame WireFrame, ulong Sequence, KnxIndividualSecureKeyKind KeyKind, byte[] Tpdu)
{
    public byte[] ToPlainCemi() => Cemi.EncodeLDataFrame(new(WireFrame.MessageCode, WireFrame.Control1, WireFrame.Control2,
        WireFrame.Source, WireFrame.DestinationRaw, Tpdu, WireFrame.AdditionalInformation));
}
public sealed record KnxIndividualSyncResponse(ulong RemoteNextSequence, ulong LocalNextSequence);
public sealed record KnxIndividualSyncRequest(ulong Sequence, byte[] Challenge);

/// <summary>
/// Unicast individual S-A_Data and Sync wire mechanics, auth-conf only. Does not allocate safe nonces, select keys,
/// authorize senders, admit replays or implement a device/server role. Tool and P2P contexts must be explicitly provisioned.
/// Public Annex C examples: XKNX/xknx-editor c41bc84c5ac6d8ab92f4fea0f79880fc901b2285,
/// packages/download/tests/test_data_secure.py; Calimero core eccdabc72b37203cfec66313432e750a24384178,
/// test/io/calimero/secure/SecureApplicationLayerTest.java. Independent implementation.
/// </summary>
public static class KnxIndividualSecureCodec
{
    public const ulong MaximumSequence = 0xFFFFFFFFFFFF;
    public const int MaximumPlainTpduLength = 242;

    public static byte[] ProtectData(CemiLDataFrame plain, ulong sequence, ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind)
    {
        ValidateEnvelope(plain); ValidateTpdu(plain.Apdu); ValidSequence(sequence); ValidKey(key); var tpdu = plain.Apdu;
        try { tpdu[0] &= 3; return Protect(plain, sequence, [], tpdu, key, Scf(kind, 0), sequence); }
        finally { CryptographicOperations.ZeroMemory(tpdu); }
    }

    public static byte[] ProtectSyncRequest(CemiLDataFrame transportTemplate, ulong sequence, ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind)
    {
        ValidateEnvelope(transportTemplate); ValidateTpdu(transportTemplate.Apdu); ValidSequence(sequence); ValidKey(key);
        if (challenge.Length != 6) throw new ArgumentException("A Sync challenge is six bytes.", nameof(challenge));
        return Protect(transportTemplate, sequence, new byte[6], challenge, key, Scf(kind, 2), sequence);
    }

    /// <summary>Low-level fixture/device implementation seam. A responder must separately ensure nonce uniqueness and authenticated request/state handling.</summary>
    public static byte[] ProtectSyncResponse(CemiLDataFrame transportTemplate, ReadOnlySpan<byte> challenge, ulong random,
        ulong remoteNextSequence, ulong localNextSequence, ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind)
    {
        ValidateEnvelope(transportTemplate); ValidateTpdu(transportTemplate.Apdu); ValidKey(key);
        ValidSequence(remoteNextSequence); ValidSequence(localNextSequence);
        if (challenge.Length != 6 || random > MaximumSequence) throw new ArgumentException("Sync challenge/random must fit six bytes.");
        Span<byte> payload = stackalloc byte[12]; Span<byte> header = stackalloc byte[6];
        try
        {
            Write48(payload, remoteNextSequence); Write48(payload[6..], localNextSequence); Write48(header, random);
            for (var i = 0; i < 6; i++) header[i] ^= challenge[i];
            return Protect(transportTemplate, random, [], payload, key, Scf(kind, 3), Read48(header));
        }
        finally { CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(header); }
    }

    public static bool TryUnprotectData(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind,
        out KnxIndividualSecureData? decoded, out KnxIndividualSecureDecodeIssue issue)
    {
        decoded = null;
        if (!TryUnprotect(cemi, key, kind, 0, [], out var frame, out var sequence, out var plain, out issue)) return false;
        try
        {
            issue = KnxIndividualSecureDecodeIssue.InvalidPayload;
            if (plain!.Length < 2 || (plain[0] & 0xFC) != 0) return false;
            var tpdu = frame!.Apdu; plain[0] |= (byte)(tpdu[0] & 0xFC); ValidateTpdu(plain);
            decoded = new(frame, sequence, kind, plain); plain = null; issue = KnxIndividualSecureDecodeIssue.None; return true;
        }
        catch (ArgumentException) { return false; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public static bool TryUnprotectSyncRequest(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind,
        out KnxIndividualSyncRequest? decoded, out KnxIndividualSecureDecodeIssue issue)
    {
        decoded = null;
        if (!TryUnprotect(cemi, key, kind, 2, [], out _, out var sequence, out var plain, out issue)) return false;
        decoded = new(sequence, plain!); return true;
    }

    public static bool TryUnprotectSyncResponse(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind,
        ReadOnlySpan<byte> challenge, out KnxIndividualSyncResponse? decoded, out KnxIndividualSecureDecodeIssue issue)
    {
        decoded = null;
        if (!TryUnprotect(cemi, key, kind, 3, challenge, out _, out _, out var plain, out issue)) return false;
        try
        {
            var remote = Read48(plain!); var local = Read48(plain.AsSpan(6));
            if (remote == 0 || local == 0) { issue = KnxIndividualSecureDecodeIssue.InvalidSequence; return false; }
            decoded = new(remote, local); return true;
        }
        finally { CryptographicOperations.ZeroMemory(plain!); }
    }

    private static byte[] Protect(CemiLDataFrame template, ulong nonce, ReadOnlySpan<byte> serial, ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> key, byte scf, ulong header)
    {
        var apdu = new byte[13 + serial.Length + payload.Length]; var tpci = (byte)(template.Apdu[0] & 0xFC);
        apdu[0] = (byte)(tpci | 3); apdu[1] = 0xF1; apdu[2] = scf; Write48(apdu.AsSpan(3), header); serial.CopyTo(apdu.AsSpan(9));
        Span<byte> block = stackalloc byte[16]; var aad = new byte[1 + serial.Length]; aad[0] = scf; serial.CopyTo(aad.AsSpan(1));
        byte[]? mac = null;
        try
        {
            Block0(block, template, nonce, payload.Length); mac = Mac(key, block, aad, payload); Counter0(block, template, nonce);
            Transform(key, block, mac.AsSpan(0, 4), payload, apdu.AsSpan(apdu.Length - 4), apdu.AsSpan(9 + serial.Length, payload.Length));
            return Cemi.EncodeLDataFrame(new(template.MessageCode, template.Control1, template.Control2, template.Source,
                template.DestinationRaw, apdu, template.AdditionalInformation));
        }
        finally { CryptographicOperations.ZeroMemory(block); if (mac is not null) CryptographicOperations.ZeroMemory(mac); }
    }

    private static bool TryUnprotect(ReadOnlySpan<byte> cemi, ReadOnlySpan<byte> key, KnxIndividualSecureKeyKind kind, int service,
        ReadOnlySpan<byte> challenge, out CemiLDataFrame? frame, out ulong sequence, out byte[]? plain, out KnxIndividualSecureDecodeIssue issue)
    {
        frame = null; sequence = 0; plain = null; issue = KnxIndividualSecureDecodeIssue.InvalidFrame;
        byte[]? candidate = null; byte[]? expected = null; Span<byte> block = stackalloc byte[16]; Span<byte> tag = stackalloc byte[4];
        try
        {
            ValidKey(key); frame = Cemi.ParseLDataFrame(cemi); ValidateEnvelope(frame); var apdu = frame.Apdu; ValidateTpdu(apdu, secure: true);
            issue = KnxIndividualSecureDecodeIssue.UnsupportedContext;
            if (apdu.Length < 15 || (apdu[0] & 3) != 3 || apdu[1] != 0xF1 || apdu[2] != Scf(kind, service)) return false;
            var serialLength = service == 2 ? 6 : 0; var length = apdu.Length - 13 - serialLength;
            if (service == 2 && (length != 6 || apdu.AsSpan(9, 6).IndexOfAnyExcept((byte)0) >= 0)
                || service == 3 && (length != 12 || challenge.Length != 6) || service == 0 && length < 2) return false;
            sequence = Read48(apdu.AsSpan(3)); var nonce = sequence;
            if (service == 3) { Span<byte> random = stackalloc byte[6]; for (var i = 0; i < 6; i++) random[i] = (byte)(apdu[3 + i] ^ challenge[i]); nonce = Read48(random); CryptographicOperations.ZeroMemory(random); }
            else if (sequence == 0) { issue = KnxIndividualSecureDecodeIssue.InvalidSequence; return false; }
            candidate = new byte[length]; Counter0(block, frame, nonce);
            Transform(key, block, apdu.AsSpan(apdu.Length - 4), apdu.AsSpan(9 + serialLength, length), tag, candidate);
            var aad = new byte[1 + serialLength]; aad[0] = apdu[2]; if (serialLength > 0) apdu.AsSpan(9, serialLength).CopyTo(aad.AsSpan(1));
            Block0(block, frame, nonce, length); expected = Mac(key, block, aad, candidate);
            issue = KnxIndividualSecureDecodeIssue.AuthenticationFailed;
            if (!CryptographicOperations.FixedTimeEquals(tag, expected.AsSpan(0, 4))) return false;
            plain = candidate; candidate = null; issue = KnxIndividualSecureDecodeIssue.None; return true;
        }
        catch (NotSupportedException) { issue = KnxIndividualSecureDecodeIssue.UnsupportedContext; return false; }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException or OverflowException) { return false; }
        finally
        {
            if (candidate is not null) CryptographicOperations.ZeroMemory(candidate);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(block); CryptographicOperations.ZeroMemory(tag);
        }
    }

    internal static void ValidateEnvelope(CemiLDataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if ((frame.Control2 & 0x8F) != 0 || (frame.Control1 & 0x10) == 0 || frame.Source.Raw == 0 || frame.Source.Device == 255
            || frame.DestinationRaw == 0 || (frame.DestinationRaw & 255) == 255)
            throw new NotSupportedException("Only explicitly addressed ordinary unicast frames are supported.");
    }
    internal static void ValidateTpdu(ReadOnlySpan<byte> apdu, bool secure = false)
    {
        if (apdu.Length < 2 || apdu.Length > (secure ? 255 : MaximumPlainTpduLength) || (apdu[0] & 0x80) != 0
            || (apdu[0] & 0x40) == 0 && (apdu[0] & 0xFC) != 0)
            throw new ArgumentException("An individual data TPDU with a supported TPCI and length is required.");
        if (!secure && (apdu[0] & 3) == 3 && apdu[1] == 0xF1) throw new ArgumentException("Nested Secure APDUs are unsupported.");
    }
    private static byte Scf(KnxIndividualSecureKeyKind kind, int service) => kind switch
    { KnxIndividualSecureKeyKind.ToolAccess => (byte)(0x90 | service), KnxIndividualSecureKeyKind.PointToPoint => (byte)(0x10 | service), _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static void ValidSequence(ulong sequence) { if (sequence is 0 or > MaximumSequence) throw new ArgumentOutOfRangeException(nameof(sequence)); }
    private static void ValidKey(ReadOnlySpan<byte> key) { if (key.Length != 16) throw new ArgumentException("An explicitly provisioned individual key is 16 bytes."); }
    private static void Block0(Span<byte> block, CemiLDataFrame frame, ulong nonce, int length)
    {
        block.Clear(); Write48(block, nonce); BinaryPrimitives.WriteUInt16BigEndian(block[6..], frame.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(block[8..], frame.DestinationRaw); block[11] = (byte)(frame.Control2 & 0x8F);
        block[12] = (byte)((frame.Apdu[0] & 0xFC) | 3); block[13] = 0xF1; block[15] = checked((byte)length);
    }
    private static void Counter0(Span<byte> block, CemiLDataFrame frame, ulong nonce)
    { block.Clear(); Write48(block, nonce); BinaryPrimitives.WriteUInt16BigEndian(block[6..], frame.Source.Raw); BinaryPrimitives.WriteUInt16BigEndian(block[8..], frame.DestinationRaw); block[14] = 1; }
    private static Aes AesFor(ReadOnlySpan<byte> key)
    { var aes = Aes.Create(); var copy = key.ToArray(); try { aes.Key = copy; return aes; } catch { aes.Dispose(); throw; } finally { CryptographicOperations.ZeroMemory(copy); } }
    private static byte[] Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> payload)
    {
        var input = new byte[(18 + aad.Length + payload.Length + 15) / 16 * 16]; block.CopyTo(input);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(16), checked((ushort)aad.Length)); aad.CopyTo(input.AsSpan(18)); payload.CopyTo(input.AsSpan(18 + aad.Length)); byte[]? cipher = null;
        try { using var aes = AesFor(key); cipher = aes.EncryptCbc(input, new byte[16], PaddingMode.None); return cipher[^16..]; }
        finally { CryptographicOperations.ZeroMemory(input); if (cipher is not null) CryptographicOperations.ZeroMemory(cipher); }
    }
    private static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> counter0, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> payload, Span<byte> tagResult, Span<byte> payloadResult)
    {
        using var aes = AesFor(key); Span<byte> counter = stackalloc byte[16]; counter0.CopyTo(counter); Span<byte> stream = stackalloc byte[16];
        try
        {
            for (var offset = 0; offset < 4 + payload.Length; offset++)
            {
                if (offset % 16 == 0) { if (offset > 0) for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { } aes.EncryptEcb(counter, stream, PaddingMode.None); }
                if (offset < 4) tagResult[offset] = (byte)(tag[offset] ^ stream[offset]);
                else payloadResult[offset - 4] = (byte)(payload[offset - 4] ^ stream[offset % 16]);
            }
        }
        finally { CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(stream); }
    }
    internal static void Write48(Span<byte> target, ulong value) { for (var i = 5; i >= 0; i--) { target[i] = (byte)value; value >>= 8; } }
    internal static ulong Read48(ReadOnlySpan<byte> source) { ulong value = 0; for (var i = 0; i < 6; i++) value = (value << 8) | source[i]; return value; }
}
