using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>KNX IP Secure unicast service identifiers.</summary>
public enum KnxIpSecureService : ushort
{
    Wrapper = 0x0950, SessionRequest = 0x0951, SessionResponse = 0x0952,
    SessionAuthenticate = 0x0953, SessionStatus = 0x0954, TimerNotify = 0x0955
}

/// <summary>Authenticated unicast wrapper facts. The payload is a complete, length-checked KNXnet/IP frame.</summary>
public sealed record KnxIpSecurePacket(ushort SessionId, ulong Sequence, byte[] SerialNumber, byte[] Payload);

/// <summary>Strict KNX IP Secure unicast framing, CBC authentication and counter encryption.</summary>
public static class KnxIpSecureCodec
{
    public const ulong MaximumSequence = 0xFFFFFFFFFFFF;
    public const int MaximumPayloadLength = ushort.MaxValue - 38;

    /// <summary>Derives a 128-bit user key. This profile accepts bounded printable ASCII passwords only.</summary>
    public static byte[] DeriveUserKey(string password) => DerivePassword(password, "user-password.1.secure.ip.knx.org");
    /// <summary>Derives the 128-bit device-authentication key from a printable ASCII authentication password.</summary>
    public static byte[] DeriveDeviceAuthenticationKey(string password) => DerivePassword(password, "device-authentication-code.1.secure.ip.knx.org");
    private static byte[] DerivePassword(string password, string salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > 256 || password.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("This password profile accepts at most 256 printable ASCII characters.", nameof(password));
        var bytes = Encoding.ASCII.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, Encoding.ASCII.GetBytes(salt), 65536, HashAlgorithmName.SHA256, 16); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static byte[] Wrap(ushort sessionId, ulong sequence, ReadOnlySpan<byte> serialNumber,
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        Key(key);
        if (sessionId == 0 || sequence > MaximumSequence || serialNumber.Length != 6)
            throw new ArgumentException("A unicast session requires a nonzero ID, a 48-bit sequence and a six-byte serial number.");
        if (payload.Length > MaximumPayloadLength) throw new ArgumentOutOfRangeException(nameof(payload));
        Decode(payload);
        var body = new byte[16 + payload.Length + 16];
        BinaryPrimitives.WriteUInt16BigEndian(body, sessionId); Write48(body.AsSpan(2), sequence); serialNumber.CopyTo(body.AsSpan(8));
        var frame = Frame((ushort)KnxIpSecureService.Wrapper, body);
        Span<byte> block = stackalloc byte[16]; frame.AsSpan(8, 14).CopyTo(block); BinaryPrimitives.WriteUInt16BigEndian(block[14..], checked((ushort)payload.Length));
        var mac = Mac(key, block, frame.AsSpan(0, 8), payload);
        try
        {
            block[14] = 255; block[15] = 0;
            Crypt(key, block, mac, payload, frame.AsSpan(22, payload.Length), frame.AsSpan(frame.Length - 16));
            return frame;
        }
        finally { CryptographicOperations.ZeroMemory(mac); }
    }

    /// <summary>Rejects malformed, unauthenticated or nested security frames without releasing plaintext.</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key, out KnxIpSecurePacket? packet)
    {
        packet = null; byte[]? plain = null;
        try
        {
            Key(key);
            if (Decode(frame) != (ushort)KnxIpSecureService.Wrapper || frame.Length < 44 || frame[20] != 0 || frame[21] != 0) return false;
            var sid = BinaryPrimitives.ReadUInt16BigEndian(frame[6..]); if (sid == 0) return false;
            var length = frame.Length - 38; plain = new byte[length];
            Span<byte> counter = stackalloc byte[16]; frame.Slice(8, 14).CopyTo(counter); counter[14] = 255; counter[15] = 0;
            Span<byte> rawMac = stackalloc byte[16]; Crypt(key, counter, frame[^16..], frame.Slice(22, length), plain, rawMac);
            BinaryPrimitives.WriteUInt16BigEndian(counter[14..], checked((ushort)length));
            var expected = Mac(key, counter, frame[..8], plain);
            try { if (!CryptographicOperations.FixedTimeEquals(expected, rawMac)) return false; }
            finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(rawMac); }
            var service = Decode(plain);
            if (service is 0x0950 or 0x0951 or 0x0952 or 0x0955 or 0x0201 or 0x0202 or 0x0203 or 0x0204 or 0x020B or 0x020C
                or 0x0740 or 0x0741 or 0x0742 or 0x0743) return false;
            packet = new(sid, Read48(frame[8..]), frame.Slice(14, 6).ToArray(), plain); plain = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or OverflowException) { return false; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    internal static byte[] HandshakeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> additional)
    {
        Span<byte> zero = stackalloc byte[16]; zero.Clear(); var mac = Mac(key, zero, additional, []);
        try
        {
            zero[14] = 255;
            var result = new byte[16]; Crypt(key, zero, mac, [], [], result); return result;
        }
        finally { CryptographicOperations.ZeroMemory(mac); }
    }

    internal static byte[] Frame(ushort service, ReadOnlySpan<byte> body)
    {
        if (body.Length > ushort.MaxValue - 6) throw new ArgumentOutOfRangeException(nameof(body));
        var frame = new byte[body.Length + 6]; frame[0] = 6; frame[1] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), service); BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), checked((ushort)frame.Length));
        body.CopyTo(frame.AsSpan(6)); return frame;
    }
    internal static ushort Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame.Length > ushort.MaxValue || frame[0] != 6 || frame[1] != 16
            || BinaryPrimitives.ReadUInt16BigEndian(frame[4..]) != frame.Length) throw new FormatException("Invalid KNXnet/IP frame length or header.");
        return BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
    }
    internal static void Key(ReadOnlySpan<byte> key) { if (key.Length != 16) throw new ArgumentException("A KNX IP Secure key is 16 bytes."); }
    private static byte[] Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block, ReadOnlySpan<byte> additional, ReadOnlySpan<byte> payload)
    {
        Key(key); if (block.Length != 16 || additional.Length > ushort.MaxValue) throw new ArgumentException("Invalid authentication input.");
        var length = checked(18 + additional.Length + payload.Length); var input = new byte[checked((length + 15) / 16 * 16)];
        block.CopyTo(input); BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(16), checked((ushort)additional.Length));
        additional.CopyTo(input.AsSpan(18)); payload.CopyTo(input.AsSpan(18 + additional.Length));
        byte[]? cipher = null;
        try
        {
            using var aes = CreateAes(key); cipher = aes.EncryptCbc(input, new byte[16], PaddingMode.None); return cipher[^16..];
        }
        finally { CryptographicOperations.ZeroMemory(input); if (cipher is not null) CryptographicOperations.ZeroMemory(cipher); }
    }
    private static void Crypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> start, ReadOnlySpan<byte> mac, ReadOnlySpan<byte> payload, Span<byte> result, Span<byte> macResult)
    {
        using var aes = CreateAes(key); Span<byte> counter = stackalloc byte[16]; start.CopyTo(counter);
        Span<byte> stream = stackalloc byte[16];
        try
        {
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            for (var i = 0; i < 16; i++) macResult[i] = (byte)(mac[i] ^ stream[i]);
            for (var offset = 0; offset < payload.Length; offset += 16)
            {
                for (var n = 15; n >= 0 && ++counter[n] == 0; n--) { }
                aes.EncryptEcb(counter, stream, PaddingMode.None);
                for (var n = 0; n < Math.Min(16, payload.Length - offset); n++) result[offset + n] = (byte)(payload[offset + n] ^ stream[n]);
            }
        }
        finally { CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(stream); }
    }
    private static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create(); var copy = key.ToArray();
        try { aes.Key = copy; return aes; }
        catch { aes.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    private static void Write48(Span<byte> target, ulong value) { for (var i = 5; i >= 0; i--) { target[i] = (byte)value; value >>= 8; } }
    private static ulong Read48(ReadOnlySpan<byte> source) { ulong value = 0; for (var i = 0; i < 6; i++) value = (value << 8) | source[i]; return value; }
}
