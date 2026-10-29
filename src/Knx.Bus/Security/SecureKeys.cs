using System.Security.Cryptography;

namespace Knx.Bus.Security;

/// <summary>
/// AES-128 block/chaining helpers and constant-time comparison. Protocol-specific key derivation uses KnxIpSecureCodec.
/// </summary>
public static class SecureKeys
{
    public const int KeyLength = 16;
    public const int Iterations = 65536;

    /// <summary>
    /// Compatibility gate for an unsupported legacy derivation profile.
    /// </summary>
    [Obsolete("Unsupported legacy derivation. Use KnxIpSecureCodec.DeriveUserKey or DeriveDeviceAuthenticationKey.")]
    public static (byte[] UserKey, byte[] DeviceAuthenticationKey) DeriveKeys(
        string password, ReadOnlySpan<byte> deviceAuthenticationCode)
        => throw new NotSupportedException("Unsupported legacy secure key derivation.");

    /// <summary>Encrypts one 16-byte block with AES-128 (ECB, no padding).</summary>
    public static byte[] AesEncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        RequireKey(key);
        if (block.Length != 16) throw new ArgumentException("block must be 16 bytes", nameof(block));
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    /// <summary>Constant-time comparison of two keys or MACs.</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>
    /// AES-128-CBC-MAC over <paramref name="firstBlock"/> followed by <paramref name="data"/>
    /// zero padded to whole blocks. Returns the full last cipher block (16 bytes).
    /// </summary>
    internal static byte[] CbcMac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> firstBlock, ReadOnlySpan<byte> data)
    {
        RequireKey(key);
        if (firstBlock.Length != 16) throw new ArgumentException("first block must be 16 bytes", nameof(firstBlock));

        var mac = AesEncryptBlock(key, firstBlock);
        var buffer = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            buffer.AsSpan().Clear();
            data.Slice(offset, Math.Min(16, data.Length - offset)).CopyTo(buffer);
            for (var i = 0; i < 16; i++) buffer[i] ^= mac[i];
            mac = AesEncryptBlock(key, buffer);
        }
        return mac;
    }

    internal static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength) throw new ArgumentException($"key must be {KeyLength} bytes", nameof(key));
    }
}
