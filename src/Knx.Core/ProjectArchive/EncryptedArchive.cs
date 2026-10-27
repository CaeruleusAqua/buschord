using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Knx.Encryption.Tests")]

namespace Knx;

/// <summary>
/// Read-only reader for WinZip AES entries (AE-1/AE-2, AES-128/192/256) inside a .knxproj.
/// Decrypts to memory only; never extracts to disk and never writes an archive.
/// A missing or wrong password surfaces as <see cref="InvalidDataException"/> with
/// <see cref="PasswordRequiredMessage"/> or <see cref="WrongPasswordMessage"/>.
/// </summary>
internal static class EncryptedArchive
{
    private const int MaxEntries = 4096;
    private const int MaxEntryBytes = 64 * 1024 * 1024;
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private const int AesExtraId = 0x9901;
    private const int AuthenticationLength = 10;
    private const int DerivationIterations = 1000;
    private const string Supported =
        "Supported format: ZIP entries with WinZip AES (AE-1/AE-2); ZipCrypto (ETS4/5), ZIP64 and multi-volume archives are not supported.";

    internal const string PasswordRequiredMessage =
        "This ETS6 project is password-protected (WinZip AES-256). Reading it requires the project password; BusChord does not guess passwords.";

    internal const string WrongPasswordMessage =
        "Wrong project password for this ETS6 archive (AES authentication failed). If the password is correct, the file is damaged.";

    internal static string DeriveZipPassword(string password) =>
        ArchiveProtectionSettings.Load().DeriveZipPassword(password);

    /// <summary>
    /// Returns every entry of a ZIP as uncompressed bytes. Plain entries are read directly,
    /// AES entries are decrypted and authenticated. Requires a password when any entry is
    /// encrypted. Bounded by entry count, per-entry size and total size.
    /// </summary>
    internal static Dictionary<string, byte[]> ReadAll(byte[] zip, string? password)
    {
        var entries = Parse(zip, out var centralOffset);
        var zipPassword = password is null || !entries.Any(entry => entry.Method == 99) ? null : DeriveZipPassword(password);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in entries)
        {
            if (entry.UncompressedSize > MaxEntryBytes)
                throw new InvalidDataException("Archive entry exceeds the 64 MiB safety limit.");
            total += entry.UncompressedSize;
            if (total > MaxTotalBytes)
                throw new InvalidDataException("Archive expanded size exceeds the 256 MiB safety limit.");
            var data = Read(zip, entry, centralOffset, zipPassword);
            if (!result.TryAdd(entry.Name, data))
                throw new InvalidDataException("Duplicate archive entry names are not supported.");
        }
        return result;
    }

    private static byte[] Read(byte[] zip, Raw entry, int centralOffset, string? zipPassword)
    {
        var encrypted = (entry.Flags & 0x1) != 0;
        if (entry.Method != 99)
        {
            if (encrypted)
                throw new InvalidDataException(
                    "This project archive uses ZipCrypto encryption (ETS4/5), which is not supported. " + Supported);
            var offset = DataStart(zip, entry, centralOffset, out _);
            return Decompress(zip, offset, checked((int)entry.CompressedSize), entry.Method, entry.UncompressedSize);
        }

        var dataStart = DataStart(zip, entry, centralOffset, out var aes);
        if (aes is null)
            throw new InvalidDataException("Encrypted entry without a WinZip AES extra field. " + Supported);
        if (aes.Version is not (1 or 2))
            throw new InvalidDataException($"Unsupported WinZip AES version {aes.Version}. " + Supported);
        if (aes.Vendor != "AE")
            throw new InvalidDataException($"Unsupported AES vendor id '{aes.Vendor}'. " + Supported);
        var (saltLength, keyLength) = aes.Strength switch
        {
            1 => (8, 16),
            2 => (12, 24),
            3 => (16, 32),
            _ => throw new InvalidDataException($"Unsupported AES strength {aes.Strength}. " + Supported),
        };
        if (zipPassword is null) throw new InvalidDataException(PasswordRequiredMessage);

        var overhead = saltLength + 2 + AuthenticationLength;
        if (entry.CompressedSize < overhead)
            throw new InvalidDataException("Encrypted entry is shorter than its AES header. " + Supported);
        var cipherLength = checked((int)(entry.CompressedSize - overhead));
        if ((long)dataStart + entry.CompressedSize > centralOffset)
            throw new InvalidDataException("Archive entry exceeds the archive bounds.");

        var salt = zip.AsSpan(dataStart, saltLength);
        var verifier = zip.AsSpan(dataStart + saltLength, 2);
        var cipher = zip.AsSpan(dataStart + saltLength + 2, cipherLength);
        var authentication = zip.AsSpan(dataStart + saltLength + 2 + cipherLength, AuthenticationLength);

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(zipPassword), salt, DerivationIterations, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        if (!CryptographicOperations.FixedTimeEquals(derived.AsSpan(2 * keyLength, 2), verifier))
            throw new InvalidDataException(WrongPasswordMessage);

        using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, derived.AsSpan(keyLength, keyLength).ToArray()))
        {
            hmac.AppendData(cipher);
            var digest = hmac.GetHashAndReset();
            // The HMAC over the ciphertext authenticates the entry. AE-1's CRC-32 is not
            // re-checked: the BCL has no CRC32 helper and the HMAC is strictly stronger.
            if (!CryptographicOperations.FixedTimeEquals(digest.AsSpan(0, AuthenticationLength), authentication))
                throw new InvalidDataException(WrongPasswordMessage);
        }

        var plain = AesCtr(derived.AsSpan(0, keyLength).ToArray(), cipher);
        return Decompress(plain, 0, plain.Length, aes.Method, entry.UncompressedSize);
    }

    /// <summary>WinZip AES uses AES-CTR with a 128-bit little-endian counter block starting at 1.</summary>
    private static byte[] AesCtr(byte[] key, ReadOnlySpan<byte> cipher)
    {
        var output = new byte[cipher.Length];
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var transform = aes.CreateEncryptor();
        var counter = new byte[16];
        var keyStream = new byte[16];
        counter[0] = 1;
        for (int position = 0; position < cipher.Length; position += 16)
        {
            transform.TransformBlock(counter, 0, 16, keyStream, 0);
            var count = Math.Min(16, cipher.Length - position);
            for (int i = 0; i < count; i++) output[position + i] = (byte)(cipher[position + i] ^ keyStream[i]);
            for (int i = 0; i < counter.Length; i++) if (++counter[i] != 0) break;
        }
        return output;
    }

    private static byte[] Decompress(byte[] source, int offset, int length, ushort method, long expected)
    {
        if (expected > MaxEntryBytes) throw new InvalidDataException("Archive entry exceeds the 64 MiB safety limit.");
        if (method == 0)
        {
            if (length != expected) throw new InvalidDataException("Archive entry length mismatch.");
            return source.AsSpan(offset, length).ToArray();
        }
        if (method != 8) throw new InvalidDataException($"Unsupported compression method {method}. " + Supported);
        var output = new byte[checked((int)expected)];
        using var input = new MemoryStream(source, offset, length, false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        deflate.ReadExactly(output);
        if (deflate.ReadByte() != -1) throw new InvalidDataException("Archive entry length mismatch.");
        return output;
    }

    private static int DataStart(byte[] zip, Raw entry, int centralOffset, out AesExtra? aes)
    {
        if ((long)entry.LocalOffset + 30 > centralOffset || U32(zip, entry.LocalOffset) != 0x04034b50)
            throw new InvalidDataException(". " + Supported);
        var nameLength = U16(zip, entry.LocalOffset + 26);
        var extraLength = U16(zip, entry.LocalOffset + 28);
        var start = (long)entry.LocalOffset + 30 + nameLength + extraLength;
        if (start + entry.CompressedSize > centralOffset)
            throw new InvalidDataException("Archive entry exceeds the archive bounds.");
        aes = FindAesExtra(zip, (int)start - extraLength, extraLength);
        return (int)start;
    }

    private static AesExtra? FindAesExtra(byte[] zip, int start, int length)
    {
        for (int cursor = start; cursor + 4 <= start + length;)
        {
            var id = U16(zip, cursor);
            var size = U16(zip, cursor + 2);
            cursor += 4;
            if (cursor + size > start + length) throw new InvalidDataException("Malformed ZIP extra field. " + Supported);
            if (id == AesExtraId)
            {
                if (size < 7) throw new InvalidDataException("Malformed WinZip AES extra field. " + Supported);
                return new AesExtra(U16(zip, cursor), Encoding.ASCII.GetString(zip, cursor + 2, 2), zip[cursor + 4], U16(zip, cursor + 5));
            }
            cursor += size;
        }
        return null;
    }

    private static List<Raw> Parse(byte[] bytes, out int centralOffset)
    {
        var end = -1;
        for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            if (U32(bytes, i) == 0x06054b50 && i + 22 + U16(bytes, i + 20) == bytes.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException("Not a ZIP archive. " + Supported);
        var count = U16(bytes, end + 10);
        long offset = U32(bytes, end + 16);
        long size = U32(bytes, end + 12);
        if (U16(bytes, end + 4) != 0 || U16(bytes, end + 6) != 0 || U16(bytes, end + 8) != count || count > MaxEntries ||
            offset == uint.MaxValue || size == uint.MaxValue || offset + size != end)
            throw new InvalidDataException("Unsupported ZIP layout. " + Supported + " Limit: 4096 entries.");
        centralOffset = checked((int)offset);

        var entries = new List<Raw>(count);
        var cursor = centralOffset;
        for (int i = 0; i < count; i++)
        {
            if ((long)cursor + 46 > end || U32(bytes, cursor) != 0x02014b50)
                throw new InvalidDataException("Malformed central directory. " + Supported);
            var nameLength = U16(bytes, cursor + 28);
            var next = (long)cursor + 46 + nameLength + U16(bytes, cursor + 30) + U16(bytes, cursor + 32);
            if (next > end) throw new InvalidDataException("Malformed central directory. " + Supported);
            var name = Encoding.UTF8.GetString(bytes, cursor + 46, nameLength);
            entries.Add(new Raw(name, U16(bytes, cursor + 8), U16(bytes, cursor + 10),
                U32(bytes, cursor + 20), U32(bytes, cursor + 24), checked((int)U32(bytes, cursor + 42))));
            cursor = (int)next;
        }
        if (cursor != end) throw new InvalidDataException("Malformed central directory. " + Supported);
        return entries;
    }

    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));

    private sealed record Raw(string Name, ushort Flags, ushort Method, uint CompressedSize, uint UncompressedSize, int LocalOffset);
    private sealed record AesExtra(ushort Version, string Vendor, byte Strength, ushort Method);
}
