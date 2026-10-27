using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Knx;

internal static partial class EncryptedArchive
{
    /// <summary>
    /// Replaces selected entries while retaining every unmodified local record and its metadata byte-for-byte.
    /// Existing AES version, strength, compression and archive protection are preserved; no password is retained.
    /// </summary>
    internal static byte[] Rewrite(byte[] source, IReadOnlyDictionary<string, byte[]> replacements, string? password)
    {
        var entries = Parse(source, out var centralOffset);
        if (entries.Select(e => e.LocalOffset).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Overlapping archive entry records are not supported.");
        // Use one settings capture for source authentication and protected replacement entries.
        var encrypted = entries.Any(e => e.Method == 99);
        var zipPassword = encrypted && password is not null ? DeriveZipPassword(password) : null;
        var plain = ReadPrepared(source, entries, centralOffset, zipPassword);
        var added = replacements.Keys.Where(name => !plain.ContainsKey(name)).ToArray();
        if (entries.Count + added.Length > MaxEntries) throw new InvalidDataException("Archive entry count exceeds the safety limit.");
        long expanded = 0;
        foreach (var name in plain.Keys.Concat(added))
        {
            var length = replacements.TryGetValue(name, out var replacement) ? replacement.Length : plain[name].Length;
            if (length > ArchiveEntryLimits.For(name) || (expanded += length) > MaxTotalBytes)
                throw new InvalidDataException("Archive expanded size exceeds the safety limit.");
        }
        using var output = new MemoryStream();
        using var directory = new MemoryStream();
        var localOffsets = entries.Select(e => e.LocalOffset).Order().Append(centralOffset).ToArray();
        if (localOffsets.Length > 1) output.Write(source.AsSpan(0, localOffsets[0]));
        foreach (var entry in entries)
        {
            var localOffset = checked((uint)output.Position);
            var centralLength = 46 + U16(source, entry.CentralOffset + 28) + U16(source, entry.CentralOffset + 30) + U16(source, entry.CentralOffset + 32);
            var central = source.AsSpan(entry.CentralOffset, centralLength).ToArray();
            if (!replacements.TryGetValue(entry.Name, out var replacement))
            {
                var next = localOffsets[Array.BinarySearch(localOffsets, entry.LocalOffset) + 1];
                output.Write(source.AsSpan(entry.LocalOffset, next - entry.LocalOffset));
            }
            else
            {
                _ = DataStart(source, entry, centralOffset, out var aes);
                var nameLength = U16(source, entry.LocalOffset + 26);
                var extraLength = U16(source, entry.LocalOffset + 28);
                var localExtra = source.AsSpan(entry.LocalOffset + 30 + nameLength, extraLength).ToArray();
                var encoded = Encode(replacement, entry.Method == 99 ? aes : null, entry.Method, zipPassword);
                var localHeader = source.AsSpan(entry.LocalOffset, 30).ToArray();
                if (entry.Method == 99)
                {
                    Set16(localHeader, 4, Math.Max((ushort)51, U16(localHeader, 4)));
                    Set16(central, 6, Math.Max((ushort)51, U16(central, 6)));
                }
                var flags = (ushort)(entry.Flags & ~8); // Sizes are known; replacement has no trailing data descriptor.
                Set16(localHeader, 6, flags); Set32(localHeader, 14, encoded.Crc);
                Set32(localHeader, 18, checked((uint)encoded.Bytes.Length)); Set32(localHeader, 22, checked((uint)replacement.Length));
                output.Write(localHeader); output.Write(source.AsSpan(entry.LocalOffset + 30, nameLength)); output.Write(localExtra); output.Write(encoded.Bytes);
                Set16(central, 8, flags); Set32(central, 16, encoded.Crc);
                Set32(central, 20, checked((uint)encoded.Bytes.Length)); Set32(central, 24, checked((uint)replacement.Length));
            }
            Set32(central, 42, localOffset); directory.Write(central);
        }
        foreach (var name in added)
        {
            var bytes = replacements[name];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > ushort.MaxValue) throw new InvalidDataException("Archive entry name exceeds the safety limit.");
            var aes = encrypted ? new AesExtra(2, "AE", 3, 8) : null;
            var encoded = Encode(bytes, aes, 8, zipPassword);
            var extra = aes is null ? [] : new byte[] { 1, 0x99, 7, 0, 2, 0, (byte)'A', (byte)'E', 3, 8, 0 };
            var method = aes is null ? (ushort)8 : (ushort)99;
            var flags = aes is null ? (ushort)0x800 : (ushort)0x801;
            var localOffset = checked((uint)output.Position);
            var header = new byte[30]; Set32(header, 0, 0x04034b50); Set16(header, 4, aes is null ? (ushort)20 : (ushort)51);
            Set16(header, 6, flags); Set16(header, 8, method); Set16(header, 12, 0x21); Set32(header, 14, encoded.Crc);
            Set32(header, 18, checked((uint)encoded.Bytes.Length)); Set32(header, 22, checked((uint)bytes.Length));
            Set16(header, 26, checked((ushort)nameBytes.Length)); Set16(header, 28, checked((ushort)extra.Length));
            output.Write(header); output.Write(nameBytes); output.Write(extra); output.Write(encoded.Bytes);
            var central = new byte[46]; Set32(central, 0, 0x02014b50); Set16(central, 4, 51); Set16(central, 6, aes is null ? (ushort)20 : (ushort)51);
            Set16(central, 8, flags); Set16(central, 10, method); Set16(central, 14, 0x21); Set32(central, 16, encoded.Crc);
            Set32(central, 20, checked((uint)encoded.Bytes.Length)); Set32(central, 24, checked((uint)bytes.Length));
            Set16(central, 28, checked((ushort)nameBytes.Length)); Set16(central, 30, checked((ushort)extra.Length)); Set32(central, 42, localOffset);
            directory.Write(central); directory.Write(nameBytes); directory.Write(extra);
        }
        var newCentral = checked((uint)output.Position);
        directory.Position = 0; directory.CopyTo(output);
        var oldEnd = centralOffset + entries.Sum(e => 46 + U16(source, e.CentralOffset + 28) + U16(source, e.CentralOffset + 30) + U16(source, e.CentralOffset + 32));
        var end = source.AsSpan(oldEnd, 22 + U16(source, oldEnd + 20)).ToArray();
        Set16(end, 8, checked((ushort)(entries.Count + added.Length))); Set16(end, 10, checked((ushort)(entries.Count + added.Length)));
        Set32(end, 12, checked((uint)directory.Length)); Set32(end, 16, newCentral); output.Write(end);
        if (output.Length > 128 * 1024 * 1024) throw new InvalidDataException("Archive exceeds the 128 MiB safety limit.");
        return output.ToArray();
    }

    private static (byte[] Bytes, uint Crc) Encode(byte[] data, AesExtra? aes, ushort method, string? zipPassword)
    {
        method = aes?.Method ?? method;
        byte[] compressed;
        if (method == 0) compressed = data;
        else if (method == 8)
        {
            using var buffer = new MemoryStream();
            using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, true)) deflate.Write(data);
            compressed = buffer.ToArray();
        }
        else throw new InvalidDataException("Unsupported replacement compression method.");
        var crc = Crc32(data);
        if (aes is null) return (compressed, crc);
        if (zipPassword is null) throw new InvalidDataException(PasswordRequiredMessage);
        if (aes.Version is not (1 or 2) || aes.Vendor != "AE") throw new InvalidDataException("Unsupported AES metadata.");
        var keyLength = aes.Strength switch { 1 => 16, 2 => 24, 3 => 32, _ => throw new InvalidDataException("Unsupported AES strength.") };
        var salt = RandomNumberGenerator.GetBytes(keyLength / 2);
        var passwordBytes = Encoding.UTF8.GetBytes(zipPassword);
        var derived = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, DerivationIterations, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        try
        {
            var encryptionKey = derived.AsSpan(0, keyLength).ToArray();
            byte[] cipher;
            try { cipher = AesCtr(encryptionKey, compressed); }
            finally { CryptographicOperations.ZeroMemory(encryptionKey); }
            var authenticationKey = derived.AsSpan(keyLength, keyLength).ToArray();
            byte[] authentication;
            try { authentication = HMACSHA1.HashData(authenticationKey, cipher); }
            finally { CryptographicOperations.ZeroMemory(authenticationKey); }
            var body = new byte[salt.Length + 2 + cipher.Length + AuthenticationLength];
            salt.CopyTo(body, 0); derived.AsSpan(2 * keyLength, 2).CopyTo(body.AsSpan(salt.Length));
            cipher.CopyTo(body, salt.Length + 2); authentication.AsSpan(0, AuthenticationLength).CopyTo(body.AsSpan(salt.Length + 2 + cipher.Length));
            return (body, aes.Version == 2 ? 0 : crc);
        }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (var current in bytes)
        {
            value ^= current;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~value;
    }

    private static void Set16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    private static void Set32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
