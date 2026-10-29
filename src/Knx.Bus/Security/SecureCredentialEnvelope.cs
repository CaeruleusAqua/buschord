using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

internal static class SecureCredentialEnvelope
{
    internal const int MaximumPasswordUtf8Bytes = 1024, HeaderLength = 44, MaximumBlobBytes = HeaderLength + MaximumPasswordUtf8Bytes;
    private static readonly byte[] Magic = "BCOSPW01"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(SecureCredentialBinding binding, ReadOnlySpan<char> password)
    {
        byte[]? result = null;
        try
        {
            var length = Utf8.GetByteCount(password);
            if (length is < 1 or > MaximumPasswordUtf8Bytes) throw new SecureCredentialVaultException(SecureCredentialFailure.InvalidSecret);
            result = new byte[HeaderLength + length]; Magic.CopyTo(result, 0); binding.BindingDigest.CopyTo(result, 8);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(40), length); Utf8.GetBytes(password, result.AsSpan(HeaderLength));
            return result;
        }
        catch (EncoderFallbackException) { if (result is not null) CryptographicOperations.ZeroMemory(result); throw new SecureCredentialVaultException(SecureCredentialFailure.InvalidSecret); }
        catch { if (result is not null) CryptographicOperations.ZeroMemory(result); throw; }
    }
    internal static void Validate(SecureCredentialBinding binding, ReadOnlySpan<byte> blob)
    {
        if (blob.Length is <= HeaderLength or > MaximumBlobBytes || !blob[..8].SequenceEqual(Magic)
            || !CryptographicOperations.FixedTimeEquals(blob.Slice(8, 32), binding.BindingDigest)
            || BinaryPrimitives.ReadInt32BigEndian(blob[40..]) != blob.Length - HeaderLength)
            throw new SecureCredentialVaultException(SecureCredentialFailure.BindingMismatch);
    }
    internal static char[] Decode(SecureCredentialBinding binding, ReadOnlySpan<byte> blob)
    {
        Validate(binding, blob); char[]? result = null;
        try { result = new char[Utf8.GetCharCount(blob[HeaderLength..])]; Utf8.GetChars(blob[HeaderLength..], result); return result; }
        catch (DecoderFallbackException) { if (result is not null) Array.Clear(result); throw new SecureCredentialVaultException(SecureCredentialFailure.InvalidSecret); }
    }
}
