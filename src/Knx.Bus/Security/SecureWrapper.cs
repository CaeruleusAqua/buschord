using System.Net;
using System.Security.Cryptography;

namespace Knx.Bus.Security;

/// <summary>KNXnet/IP Secure service types (KNX 03_08_04).</summary>
public enum SecureServiceType : ushort
{
    SecureWrapper = 0x0950,
    SecureSessionStatus = 0x0951,
    SecureSessionAuthenticate = 0x0952,
    SecureSessionResponse = 0x0953,
    SecureTimerNotify = 0x0954,
    SecureKeyResponse = 0x0955,
}

/// <summary>One KNXnet/IP Secure wrapper frame.</summary>
public sealed class SecurePacket
{
    public ushort SessionId { get; init; }

    /// <summary>48-bit sequence number of the sender.</summary>
    public ulong SequenceNumber { get; init; }

    /// <summary>6-byte serial number of the sending interface.</summary>
    public required byte[] SerialNumber { get; init; }

    /// <summary>Message tag; 0x0000 for ordinary secure wrapper frames.</summary>
    public ushort MessageTag { get; init; }

    /// <summary>Wrapped KNXnet/IP frame (payload, zero padded on the wire).</summary>
    public required byte[] Payload { get; init; }
}

/// <summary>
/// KNXnet/IP Secure wrapper framing for routing (multicast) traffic.
/// Body: session id (2) | sequence number (6) | serial number (6) | message tag (2)
///       | AES-128-CBC encrypted payload (n * 16) | MAC (16).
/// </summary>
public static class SecureWrapper
{
    /// <summary>session id + sequence number + serial number + message tag.</summary>
    public const int HeaderLength = 16;

    public const int SerialNumberLength = 6;
    public const int MacLength = 16;

    public static byte[] Wrap(SecurePacket packet, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(packet);
        SecureKeys.RequireKey(key);
        if (packet.SerialNumber.Length != SerialNumberLength)
            throw new ArgumentException($"serial number must be {SerialNumberLength} bytes", nameof(packet));

        var header = new byte[HeaderLength];
        WriteHeader(header, packet.SessionId, packet.SequenceNumber, packet.SerialNumber, packet.MessageTag);

        var padded = Pad(packet.Payload);
        var cipher = Encrypt(key, padded);
        var mac = SecureKeys.CbcMac(key, header, padded);

        var body = new byte[HeaderLength + cipher.Length + MacLength];
        header.CopyTo(body, 0);
        cipher.CopyTo(body, HeaderLength);
        mac.CopyTo(body, HeaderLength + cipher.Length);
        return body;
    }

    public static bool TryUnwrap(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key,
        out SecurePacket? packet, out string? error)
    {
        packet = null;
        error = null;
        try
        {
            SecureKeys.RequireKey(key);
            if (body.Length < HeaderLength + 16 + MacLength || (body.Length - HeaderLength - MacLength) % 16 != 0)
            {
                error = "secure wrapper body has an invalid length";
                return false;
            }

            var header = body[..HeaderLength].ToArray();
            var cipher = body.Slice(HeaderLength, body.Length - HeaderLength - MacLength);
            var plain = Decrypt(key, cipher);
            var mac = SecureKeys.CbcMac(key, header, plain);
            if (!SecureKeys.FixedTimeEquals(mac, body[^MacLength..]))
            {
                error = "MAC mismatch";
                return false;
            }

            packet = new SecurePacket
            {
                SessionId = (ushort)((header[0] << 8) | header[1]),
                SequenceNumber = ReadSequence(header.AsSpan(2)),
                SerialNumber = header[8..14],
                MessageTag = (ushort)((header[14] << 8) | header[15]),
                Payload = TrimPadding(plain),
            };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Checks that the wrapped frame comes from the expected interface.</summary>
    public static bool IsFrom(SecurePacket packet, ReadOnlySpan<byte> serialNumber) =>
        SecureKeys.FixedTimeEquals(packet.SerialNumber, serialNumber);

    private static void WriteHeader(Span<byte> header, ushort sessionId, ulong sequence,
        ReadOnlySpan<byte> serialNumber, ushort messageTag)
    {
        header[0] = (byte)(sessionId >> 8);
        header[1] = (byte)sessionId;
        header[2] = (byte)(sequence >> 40);
        header[3] = (byte)(sequence >> 32);
        header[4] = (byte)(sequence >> 24);
        header[5] = (byte)(sequence >> 16);
        header[6] = (byte)(sequence >> 8);
        header[7] = (byte)sequence;
        serialNumber.CopyTo(header[8..14]);
        header[14] = (byte)(messageTag >> 8);
        header[15] = (byte)messageTag;
    }

    private static ulong ReadSequence(ReadOnlySpan<byte> span) =>
        ((ulong)span[0] << 40) | ((ulong)span[1] << 32) | ((ulong)span[2] << 24)
        | ((ulong)span[3] << 16) | ((ulong)span[4] << 8) | span[5];

    private static byte[] Pad(ReadOnlySpan<byte> payload)
    {
        var length = (payload.Length + 15) / 16 * 16;
        var padded = new byte[length];
        payload.CopyTo(padded);
        return padded;
    }

    /// <summary>
    /// Removes the zero padding. A wrapped KNXnet/IP frame carries its own length, so a
    /// payload whose last byte is 0x00 survives; other payloads lose trailing zeros.
    /// </summary>
    private static byte[] TrimPadding(byte[] payload)
    {
        if (payload.Length >= KnxNetIpFrame.HeaderLength && payload[0] == 0x06 && payload[1] == 0x10)
        {
            var declared = (payload[4] << 8) | payload[5];
            if (declared > 0 && declared <= payload.Length) return payload[..declared];
        }

        var end = payload.Length;
        while (end > 0 && payload[end - 1] == 0) end--;
        return payload[..end];
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> padded)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptCbc(padded, new byte[16], PaddingMode.None);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptCbc(cipher, new byte[16], PaddingMode.None);
    }
}

/// <summary>
/// Session handshake bodies of KNXnet/IP Secure.
/// SESSION_AUTHENTICATE: HPAI (8) | user key (16).
/// SESSION_RESPONSE: session id (2) | serial number (6) | MAC (16) over that header block,
/// keyed with the device authentication key.
/// </summary>
public static class SecureSession
{
    public const int AuthenticateLength = Hpai.Length + SecureKeys.KeyLength;
    public const int ResponseLength = 2 + SecureWrapper.SerialNumberLength + SecureWrapper.MacLength;

    /// <summary>Builds a SESSION_AUTHENTICATE body carrying our control endpoint and user key.</summary>
    public static byte[] Authenticate(IPEndPoint endpoint, ReadOnlySpan<byte> userKey)
    {
        SecureKeys.RequireKey(userKey);
        var body = new byte[AuthenticateLength];
        Hpai.Control(endpoint).CopyTo(body, 0);
        userKey.CopyTo(body.AsSpan(Hpai.Length));
        return body;
    }

    public static bool TryParseAuthenticate(ReadOnlySpan<byte> body,
        out IPEndPoint? endpoint, out byte[]? userKey, out string? error)
    {
        endpoint = null;
        userKey = null;
        error = null;
        if (body.Length != AuthenticateLength)
        {
            error = "SESSION_AUTHENTICATE body has an invalid length";
            return false;
        }
        endpoint = Hpai.Read(body).Endpoint;
        userKey = body[Hpai.Length..].ToArray();
        return true;
    }

    /// <summary>Builds a SESSION_RESPONSE body: the session id the interface assigned to us.</summary>
    public static byte[] Response(ushort sessionId, ReadOnlySpan<byte> serialNumber,
        ReadOnlySpan<byte> deviceAuthenticationKey)
    {
        SecureKeys.RequireKey(deviceAuthenticationKey);
        if (serialNumber.Length != SecureWrapper.SerialNumberLength)
            throw new ArgumentException($"serial number must be {SecureWrapper.SerialNumberLength} bytes",
                nameof(serialNumber));

        var header = new byte[2 + SecureWrapper.SerialNumberLength];
        header[0] = (byte)(sessionId >> 8);
        header[1] = (byte)sessionId;
        serialNumber.CopyTo(header.AsSpan(2));
        var block = new byte[16];
        header.CopyTo(block, 0);

        var mac = SecureKeys.CbcMac(deviceAuthenticationKey, block, []);
        var body = new byte[ResponseLength];
        header.CopyTo(body, 0);
        mac.CopyTo(body, header.Length);
        return body;
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> body, ReadOnlySpan<byte> deviceAuthenticationKey,
        out ushort sessionId, out byte[]? serialNumber, out string? error)
    {
        sessionId = 0;
        serialNumber = null;
        error = null;
        try
        {
            SecureKeys.RequireKey(deviceAuthenticationKey);
            if (body.Length != ResponseLength)
            {
                error = "SESSION_RESPONSE body has an invalid length";
                return false;
            }

            var header = body[..(2 + SecureWrapper.SerialNumberLength)];
            var block = new byte[16];
            header.CopyTo(block);
            var expected = SecureKeys.CbcMac(deviceAuthenticationKey, block, []);
            if (!SecureKeys.FixedTimeEquals(expected, body[header.Length..]))
            {
                error = "MAC mismatch";
                return false;
            }

            sessionId = (ushort)((header[0] << 8) | header[1]);
            serialNumber = header[2..].ToArray();
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
