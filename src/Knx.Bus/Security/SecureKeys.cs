using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>
/// Key material handling for KNX IP Secure and KNX Data Secure: PBKDF2 key derivation,
/// AES-128 block/chaining helpers and constant-time comparison.
/// </summary>
public static class SecureKeys
{
    public const int KeyLength = 16;
    public const int Iterations = 65536;

    /// <summary>
    /// Derives the user key and the device authentication key from a password and the
    /// 16-byte device authentication code (DAC), both 16 bytes.
    /// </summary>
    // ponytail: KNX IP Secure swaps password and salt between the two derivations; the exact
    // order and the encoding of the password as salt are inferred, verify against a real installation.
    public static (byte[] UserKey, byte[] DeviceAuthenticationKey) DeriveKeys(
        string password, ReadOnlySpan<byte> deviceAuthenticationCode)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (deviceAuthenticationCode.Length != KeyLength)
            throw new ArgumentException($"device authentication code must be {KeyLength} bytes",
                nameof(deviceAuthenticationCode));

        var pwd = Encoding.UTF8.GetBytes(password);
        try
        {
            var userKey = Rfc2898DeriveBytes.Pbkdf2(pwd, deviceAuthenticationCode, Iterations,
                HashAlgorithmName.SHA256, KeyLength);
            var deviceKey = Rfc2898DeriveBytes.Pbkdf2(deviceAuthenticationCode, pwd, Iterations,
                HashAlgorithmName.SHA256, KeyLength);
            return (userKey, deviceKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwd);
        }
    }

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
