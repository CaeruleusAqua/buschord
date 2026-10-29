using System.Net;

namespace Knx.Bus.Security;

/// <summary>Unsupported legacy identifiers retained for source compatibility only.</summary>
[Obsolete("Unsupported legacy framing. Use KnxIpSecureService and KnxIpSecureSession.")]
public enum SecureServiceType : ushort
{
    SecureWrapper = 0x0950, SecureSessionStatus = 0x0951, SecureSessionAuthenticate = 0x0952,
    SecureSessionResponse = 0x0953, SecureTimerNotify = 0x0954, SecureKeyResponse = 0x0955
}

/// <summary>Unsupported legacy data container; it does not establish authenticated protocol facts.</summary>
[Obsolete("Unsupported legacy framing. Use KnxIpSecurePacket.")]
public sealed class SecurePacket
{
    public ushort SessionId { get; init; }
    public ulong SequenceNumber { get; init; }
    public required byte[] SerialNumber { get; init; }
    public ushort MessageTag { get; init; }
    public required byte[] Payload { get; init; }
}

/// <summary>Compatibility gates for unsupported legacy framing. These methods cannot generate or authenticate traffic.</summary>
[Obsolete("Unsupported legacy framing. Use KnxIpSecureCodec for authenticated unicast sessions.")]
public static class SecureWrapper
{
    public const int HeaderLength = 16, SerialNumberLength = 6, MacLength = 16;
    public static byte[] Wrap(SecurePacket packet, ReadOnlySpan<byte> key) => throw new NotSupportedException("Unsupported legacy secure wrapper framing.");
    public static bool TryUnwrap(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key, out SecurePacket? packet, out string? error)
    { packet = null; error = "Unsupported legacy secure wrapper framing."; return false; }
    public static bool IsFrom(SecurePacket packet, ReadOnlySpan<byte> serialNumber) => false;
}

/// <summary>Compatibility gates for unsupported legacy session messages. No method exposes a credential.</summary>
[Obsolete("Unsupported legacy framing. Use KnxIpSecureSession for an authenticated X25519 handshake.")]
public static class SecureSession
{
    public const int AuthenticateLength = 24, ResponseLength = 24;
    public static byte[] Authenticate(IPEndPoint endpoint, ReadOnlySpan<byte> userKey) => throw new NotSupportedException("Unsupported legacy secure session authentication.");
    public static bool TryParseAuthenticate(ReadOnlySpan<byte> body, out IPEndPoint? endpoint, out byte[]? userKey, out string? error)
    { endpoint = null; userKey = null; error = "Unsupported legacy secure session authentication."; return false; }
    public static byte[] Response(ushort sessionId, ReadOnlySpan<byte> serialNumber, ReadOnlySpan<byte> deviceAuthenticationKey)
        => throw new NotSupportedException("Unsupported legacy secure session response.");
    public static bool TryParseResponse(ReadOnlySpan<byte> body, ReadOnlySpan<byte> deviceAuthenticationKey, out ushort sessionId, out byte[]? serialNumber, out string? error)
    { sessionId = 0; serialNumber = null; error = "Unsupported legacy secure session response."; return false; }
}
