using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Knx;

var directory = Path.Combine(Path.GetTempPath(), "opencode", "buschord-encryption-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int checks = 0;
try
{
    // 1. ETS6 project password to ZIP password. Vectors from analysis/project-encryption.md
    //    and xknxproject test/zip/test_extractor.py::test_ets6_password_generation.
    Check(EncryptedArchive.DeriveZipPassword("a") == "+FAwP4iI7/Pu4WB3HdIHbbFmteLahPAVkjJShKeozAA=", "KDF vector 'a'");
    Check(EncryptedArchive.DeriveZipPassword("test") == "2+IIP7ErCPPKxFjJXc59GFx2+w/1VTLHjJ2duc04CYQ=", "KDF vector 'test'");
    Check(EncryptedArchive.DeriveZipPassword("Penn\u00A5w1se \uD83E\uDD21") == "ZjlYlh+eTtoHvFadU7+EKvF4jOdEm7WkP49uanOMMk0=", "KDF vector non-ASCII/emoji");

    // 2. Real public ETS6 fixture (schema /21, WinZip AES-256 AE-1), password "test".
    var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "testprojekt-ets6.knxproj");
    var fixtureBytes = File.ReadAllBytes(fixture);
    Check(SHA256Hex(fixtureBytes) == "DCB193844518980FB31429D36CDEC929E17B604BCF09D577094E066D4615FB4E", "public fixture SHA-256");
    var project = Knxproj.Load(fixture, "test");
    var devices = project.Installations.SelectMany(i => i.Areas.SelectMany(a => a.Lines.SelectMany(l =>
        l.Devices.Concat(l.Segments.SelectMany(s => s.Devices)).Concat(a.Devices)))).ToList();
    var addresses = project.Installations.SelectMany(i => i.MainRanges.SelectMany(FlattenRanges)).SelectMany(r => r.Addresses).ToList();
    Check(project.Id == "P-04BF" && project.Name == "testprojekt", "/21 project id and name from project.xml");
    Check(project.Guid == "216de323-8b1b-4d0c-8ed5-b71a3ee0b3fa" && project.ProjectStart is not null, "/21 project metadata");
    Check(project.Installations.Count == 1 && project.Installations[0].GroupAddressStyle == GroupAddressStyle.ThreeLevel, "/21 installation and style");
    Check(devices.Count == 3 && string.Join(",", devices.Select(d => d.Address.ToString())) == "1.1.0,1.1.5,1.1.6",
        "/21 device numbers assembled to individual addresses");
    Check(addresses.Count == 3 && string.Join(",", addresses.Select(a => a.Address.Raw)) == "256,257,258", "/21 numeric group addresses");
    Check(project.Installations[0].Locations.Roots.Single().Name == "testprojekt", "/21 building tree");
    Check(devices[1].ComObjectRefs.Count == 8 && devices[1].ComObjectRefs[0].Links.Single() == "GA-1", "/21 device links");

    // /21 also loads as a plain archive (same XML, no encryption).
    var decrypted21 = EncryptedArchive.ReadAll(Unzip(fixtureBytes)["P-04BF.zip"], "test");
    var plain21 = Path.Combine(directory, "plain-21.knxproj");
    File.WriteAllBytes(plain21, PlainZip(decrypted21));
    var plain21Project = Knxproj.Load(plain21);
    Check(plain21Project.Name == "testprojekt" && plain21Project.Installations[0].Areas[1].Lines[1].Segments[0].Devices.Count == 3,
        "plain /21 archive loads");
    var plain21Before = File.ReadAllBytes(plain21);
    ExpectMessage(() => Knxproj.Save(plain21Project, plain21), "schema", "/21 save refused");
    Check(File.ReadAllBytes(plain21).SequenceEqual(plain21Before), "/21 refusal leaves destination identical");

    // 3. Password errors are distinguishable and user-readable.
    ExpectMessage(() => Knxproj.Load(fixture), "requires the project password", "missing password");
    ExpectMessage(() => Knxproj.Load(fixture, "not-the-password"), "Wrong project password", "wrong password");
    // 4. Encrypted projects cannot be saved back (no silent plaintext downgrade).
    var saveTarget = Path.Combine(directory, "copy.knxproj");
    ExpectMessage(() => Knxproj.Save(project, saveTarget), "not save", "save of encrypted /21 project refused");
    Check(!File.Exists(saveTarget), "refused save creates no destination");
    Check(File.ReadAllBytes(fixture).SequenceEqual(fixtureBytes), "fixture unchanged");

    // 5. Tampered authentication code of the real fixture fails with the password message.
    var outer = Unzip(fixtureBytes);
    var inner = outer["P-04BF.zip"];
    var innerOffset = LocalOffset(inner, "0.xml");
    var innerNameLength = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(innerOffset + 26, 2));
    var innerExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(innerOffset + 28, 2));
    var innerCompressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(innerOffset + 18, 4));
    inner[innerOffset + 30 + innerNameLength + innerExtraLength + innerCompressed - 1] ^= 0xFF;
    outer["P-04BF.zip"] = inner;
    var tampered = Path.Combine(directory, "tampered.knxproj");
    File.WriteAllBytes(tampered, PlainZip(outer));
    ExpectMessage(() => Knxproj.Load(tampered, "test"), "Wrong project password", "tampered HMAC");

    // 6. Synthetic WinZip AES fixtures (supplement; the public fixture above is the interop proof).
    var synthetic = Path.Combine(directory, "synthetic-aes1.knxproj");
    File.WriteAllBytes(synthetic, AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("AES synthetic")), ["project.xml"] = "<x/>"u8.ToArray() }, "secret"));
    var syntheticProject = Knxproj.Load(synthetic, "secret");
    Check(syntheticProject.Name == "AES synthetic" && syntheticProject.Id == "P-1", "synthetic AE-1 loads");
    ExpectMessage(() => Knxproj.Load(synthetic), "requires the project password", "synthetic missing password");
    ExpectMessage(() => Knxproj.Load(synthetic, "wrong"), "Wrong project password", "synthetic wrong password");
    ExpectMessage(() => Knxproj.Save(syntheticProject, Path.Combine(directory, "synthetic-copy.knxproj")), "password-protected", "encrypted project save refused");

    var aes2 = Path.Combine(directory, "synthetic-aes2.knxproj");
    File.WriteAllBytes(aes2, AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("AE-2")) }, "secret", version: 2));
    Check(Knxproj.Load(aes2, "secret").Name == "AE-2", "synthetic AE-2 loads");

    // 7. Plain archives behave as before, with or without a password argument.
    var plain = Path.Combine(directory, "plain.knxproj");
    File.WriteAllBytes(plain, PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("Plain")) }));
    Check(Knxproj.Load(plain).Name == "Plain", "plain archive loads");
    Check(Knxproj.Load(plain, "ignored").Name == "Plain", "plain archive ignores password");

    // 8. Unsupported encryption is named, not misreported as a wrong password.
    var zipCrypto = Path.Combine(directory, "zipcrypto.knxproj");
    var zipCryptoBytes = PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("ZipCrypto")) });
    FlagAsEncrypted(zipCryptoBytes, method: 8);
    File.WriteAllBytes(zipCrypto, zipCryptoBytes);
    ExpectMessage(() => Knxproj.Load(zipCrypto, "anything"), "ZipCrypto", "ZipCrypto rejected");

    var noAesExtra = Path.Combine(directory, "no-aes-extra.knxproj");
    var noAesExtraBytes = PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("NoAes")) });
    FlagAsEncrypted(noAesExtraBytes, method: 99);
    File.WriteAllBytes(noAesExtra, noAesExtraBytes);
    ExpectMessage(() => Knxproj.Load(noAesExtra, "anything"), "AES extra field", "missing AES extra field rejected");

    Console.WriteLine($"ALL {checks} ENCRYPTION CHECKS PASSED");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}

void ExpectMessage(Action action, string fragment, string name)
{
    try { action(); }
    catch (InvalidDataException e) when (e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { checks++; return; }
    throw new Exception("FAIL: expected '" + fragment + "': " + name);
}

static string MinimalXml(string name) =>
    "<KNX xmlns=\"http://knx.org/xml/project/23\"><Project Id=\"P-1\" Name=\"" + name + "\"><Installations/></Project></KNX>";

static string SHA256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

static IEnumerable<GroupRange> FlattenRanges(GroupRange range) =>
    new[] { range }.Concat(range.Ranges.SelectMany(FlattenRanges));

static Dictionary<string, byte[]> Unzip(byte[] bytes)
{
    using var buffer = new MemoryStream(bytes);
    using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
    return zip.Entries.ToDictionary(e => e.FullName, e =>
    {
        using var stream = e.Open();
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    });
}

static byte[] PlainZip(Dictionary<string, byte[]> entries)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    return buffer.ToArray();
}

// Local header offset of one entry, read from the central directory.
static int LocalOffset(byte[] zip, string name)
{
    var target = Encoding.UTF8.GetBytes(name);
    for (int i = 0; i + 46 < zip.Length; i++)
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i, 4)) == 0x02014b50 &&
            BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 28, 2)) == target.Length &&
            zip.AsSpan(i + 46, target.Length).SequenceEqual(target))
            return (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i + 42, 4));
    throw new Exception("entry not found: " + name);
}

// Marks every entry as encrypted with the given method, without writing any encrypted data.
static void FlagAsEncrypted(byte[] zip, ushort method)
{
    for (int i = 0; i + 46 < zip.Length; i++)
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i, 4)) == 0x02014b50)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + 8, 2), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 8, 2)) | 1));
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + 10, 2), method);
        }
}

// Synthetic WinZip AES archive (write direction, AE-1/AE-2, AES-256, Deflate).
static byte[] AesZip(Dictionary<string, byte[]> entries, string userPassword, ushort version = 1)
{
    const int saltLength = 16;
    const int keyLength = 32;
    // ETS6 stores Base64(PBKDF2(user password)) as the ZIP password.
    var zipPassword = EncryptedArchive.DeriveZipPassword(userPassword);
    using var output = new MemoryStream();
    using var central = new MemoryStream();
    foreach (var (name, data) in entries)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true)) deflate.Write(data);
        var deflateBytes = compressed.ToArray();
        var salt = RandomNumberGenerator.GetBytes(saltLength);
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(zipPassword), salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        var cipher = AesCtr(derived[..keyLength], deflateBytes);
        var mac = HMACSHA1.HashData(derived[keyLength..(2 * keyLength)], cipher).AsSpan(0, 10).ToArray();
        var extra = new byte[] { 0x01, 0x99, 0x07, 0x00, (byte)version, 0x00, (byte)'A', (byte)'E', 0x03, 0x08, 0x00 };
        var body = salt.Concat(derived[(2 * keyLength)..]).Concat(cipher).Concat(mac).ToArray();

        var localOffset = (uint)output.Length;
        WriteU32(output, 0x04034b50); WriteU16(output, 20); WriteU16(output, 0x0801); WriteU16(output, 99);
        WriteU16(output, 0); WriteU16(output, 0x21); WriteU32(output, 0); WriteU32(output, (uint)body.Length);
        WriteU32(output, (uint)data.Length); WriteU16(output, nameBytes.Length); WriteU16(output, extra.Length);
        output.Write(nameBytes); output.Write(extra); output.Write(body);

        WriteU32(central, 0x02014b50); WriteU16(central, 20); WriteU16(central, 20); WriteU16(central, 0x0801);
        WriteU16(central, 99); WriteU16(central, 0); WriteU16(central, 0x21); WriteU32(central, 0);
        WriteU32(central, (uint)body.Length); WriteU32(central, (uint)data.Length); WriteU16(central, nameBytes.Length);
        WriteU16(central, extra.Length); WriteU16(central, 0); WriteU16(central, 0); WriteU16(central, 0);
        WriteU32(central, 0); WriteU32(central, localOffset);
        central.Write(nameBytes); central.Write(extra);
    }
    var centralOffset = (uint)output.Length;
    central.WriteTo(output);
    WriteU32(output, 0x06054b50); WriteU16(output, 0); WriteU16(output, 0);
    WriteU16(output, entries.Count); WriteU16(output, entries.Count);
    WriteU32(output, (uint)central.Length); WriteU32(output, centralOffset); WriteU16(output, 0);
    return output.ToArray();
}

static void WriteU16(Stream stream, int value)
{
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
    stream.Write(bytes);
}

static void WriteU32(Stream stream, uint value)
{
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
}

static byte[] AesCtr(byte[] key, byte[] input)
{
    using var aes = Aes.Create();
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;
    aes.Key = key;
    using var transform = aes.CreateEncryptor();
    var output = new byte[input.Length];
    var counter = new byte[16];
    var keyStream = new byte[16];
    counter[0] = 1;
    for (int position = 0; position < input.Length; position += 16)
    {
        transform.TransformBlock(counter, 0, 16, keyStream, 0);
        for (int i = 0; i < Math.Min(16, input.Length - position); i++)
            output[position + i] = (byte)(input[position + i] ^ keyStream[i]);
        for (int i = 0; i < counter.Length; i++) if (++counter[i] != 0) break;
    }
    return output;
}
