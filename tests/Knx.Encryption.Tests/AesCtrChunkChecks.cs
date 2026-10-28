using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knx;

internal static class AesCtrChunkChecks
{
    // Stored entries make the ciphertext length exact, independent of Deflate ratios.
    public static void Run(Action<bool, string> check)
    {
        const string password = "synthetic-counter-boundary";
        var zipPassword = SyntheticZipPassword(password);
        foreach (var version in new ushort[] { 1, 2 })
        foreach (var strength in new byte[] { 1, 2, 3 })
        foreach (var length in new[] { 0, 1, 15, 16, 17, 65535, 65536, 65537, 131073 })
        {
            var input = new byte[length];
            for (var index = 0; index < input.Length; index++) input[index] = unchecked((byte)(index * 29 + index / 251));
            var fixture = StoredAesFixture(input, zipPassword, version, strength);
            var before = fixture.ToArray();
            var decoded = EncryptedArchive.ReadAll(fixture, password)["counter.bin"];
            var replacement = input.Select(value => (byte)(value ^ 0xA5)).ToArray();
            var written = EncryptedArchive.Rewrite(fixture, new Dictionary<string, byte[]> { ["counter.bin"] = replacement }, password);
            var independentlyDecoded = ReadFixture(written, zipPassword);
            var tampered = written.ToArray();
            var start = DataStart(tampered);
            var keyLength = strength == 1 ? 16 : strength == 2 ? 24 : 32;
            var authenticationStart = start + keyLength / 2 + 2 + replacement.Length;
            tampered[authenticationStart] ^= 1;
            var rejected = false;
            try { EncryptedArchive.ReadAll(tampered, password); }
            catch (InvalidDataException error) { rejected = error.Message == EncryptedArchive.WrongPasswordMessage; }
            check(decoded.SequenceEqual(input) && independentlyDecoded.SequenceEqual(replacement)
                && fixture.SequenceEqual(before) && rejected,
                $"CTR AE-{version} strength {strength} stored length {length}: independent read/write, authentication and source retention");
        }
        // Carry from the low 16 bits into the next byte, beyond multiple batches.
        var carryData = new byte[16 * 65536 + 1];
        var carryFixture = StoredAesFixture(carryData, zipPassword, 2, 3);
        check(EncryptedArchive.ReadAll(carryFixture, password)["counter.bin"].SequenceEqual(carryData),
            "CTR little-endian counter carries across 65536 blocks without resetting at a batch boundary");
    }

    private static string SyntheticZipPassword(string password)
    {
        // This runner's module initializer selects a synthetic profile. Never inspect user settings.
        var path = Environment.GetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION")
            ?? throw new InvalidOperationException("A synthetic protection profile is required.");
        var root = Path.GetDirectoryName(Path.GetFullPath(path));
        if (root is null || !root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("buschord-check-profile-", StringComparison.Ordinal))
            throw new InvalidOperationException("Counter checks require their isolated synthetic profile.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var settings = json.RootElement;
        if (settings.GetProperty("Salt").GetString() != "buschord-synthetic-test-salt"
            || settings.GetProperty("Iterations").GetInt32() != 4096
            || settings.GetProperty("HashAlgorithm").GetString() != "SHA256"
            || settings.GetProperty("KeyLength").GetInt32() != 32 || settings.GetProperty("PasswordEncoding").GetString() != "utf-8")
            throw new InvalidOperationException("Counter checks require the known synthetic protection settings.");
        return Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
            "buschord-synthetic-test-salt"u8, 4096, HashAlgorithmName.SHA256, 32));
    }

    private static byte[] StoredAesFixture(byte[] data, string zipPassword, ushort version, byte strength)
    {
        var keyLength = strength == 1 ? 16 : strength == 2 ? 24 : 32;
        var salt = Enumerable.Range(1, keyLength / 2).Select(value => (byte)value).ToArray();
        var keys = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(zipPassword), salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        var encrypted = IndependentCounterXor(keys[..keyLength], data);
        var body = salt.Concat(keys[(2 * keyLength)..]).Concat(encrypted).Concat(HMACSHA1.HashData(keys[keyLength..(2 * keyLength)], encrypted)[..10]).ToArray();
        byte[] extra = [1, 0x99, 7, 0, (byte)version, 0, (byte)'A', (byte)'E', strength, 0, 0];
        var name = "counter.bin"u8.ToArray();
        var crc = version == 1 ? IndependentCrc(data) : 0;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        void U16(int value) => writer.Write((ushort)value);
        void U32(uint value) => writer.Write(value);
        U32(0x04034b50); U16(51); U16(0x801); U16(99); U16(0); U16(0x21); U32(crc); U32((uint)body.Length); U32((uint)data.Length);
        U16(name.Length); U16(extra.Length); writer.Write(name); writer.Write(extra); writer.Write(body);
        var directory = (uint)output.Position;
        U32(0x02014b50); U16(51); U16(51); U16(0x801); U16(99); U16(0); U16(0x21); U32(crc); U32((uint)body.Length); U32((uint)data.Length);
        U16(name.Length); U16(extra.Length); U16(0); U16(0); U16(0); U32(0); U32(0); writer.Write(name); writer.Write(extra);
        var directoryLength = (uint)output.Position - directory;
        U32(0x06054b50); U16(0); U16(0); U16(1); U16(1); U32(directoryLength); U32(directory); U16(0);
        return output.ToArray();
    }

    private static byte[] ReadFixture(byte[] zip, string zipPassword)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(26));
        var extra = 30 + nameLength;
        var strength = zip[extra + 8];
        var keyLength = strength == 1 ? 16 : strength == 2 ? 24 : 32;
        var start = DataStart(zip);
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(18))) - keyLength / 2 - 2 - 10;
        var keys = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(zipPassword), zip.AsSpan(start, keyLength / 2), 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        var cipher = zip.AsSpan(start + keyLength / 2 + 2, length);
        var mac = HMACSHA1.HashData(keys.AsSpan(keyLength, keyLength), cipher);
        if (!CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, 10), zip.AsSpan(start + keyLength / 2 + 2 + length, 10)))
            throw new InvalidDataException("Independent fixture authentication failed.");
        var plain = IndependentCounterXor(keys[..keyLength], cipher);
        if (zip[extra + 4] == 1 && IndependentCrc(plain) != BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(14)))
            throw new InvalidDataException("Independent fixture CRC check failed.");
        return plain;
    }

    private static int DataStart(byte[] zip) => 30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(26))
        + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(28));

    private static byte[] IndependentCounterXor(byte[] key, ReadOnlySpan<byte> input)
    {
        using var aes = Aes.Create(); aes.Key = key;
        var result = new byte[input.Length];
        Span<byte> counterBlock = stackalloc byte[16]; counterBlock.Clear();
        Span<byte> mask = stackalloc byte[16];
        for (var block = 0; block < (input.Length + 15) / 16; block++)
        {
            // The fixture derives each counter from its ordinal, independently of the production carry loop.
            BinaryPrimitives.WriteUInt64LittleEndian(counterBlock, (ulong)block + 1);
            aes.EncryptEcb(counterBlock, mask, PaddingMode.None);
            var position = block * 16;
            for (var index = 0; index < Math.Min(16, input.Length - position); index++) result[position + index] = (byte)(input[position + index] ^ mask[index]);
        }
        return result;
    }

    private static uint IndependentCrc(byte[] data)
    {
        var state = uint.MaxValue;
        foreach (var value in data)
        {
            state ^= value;
            for (var bit = 0; bit < 8; bit++) state = (state >> 1) ^ (0xEDB88320u & unchecked((uint)-(int)(state & 1)));
        }
        return ~state;
    }
}
