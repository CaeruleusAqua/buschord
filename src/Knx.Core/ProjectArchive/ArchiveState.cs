using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

// Immutable archive bytes are shared by undo snapshots, not copied per edit.
internal sealed record ArchiveState(byte[] Bytes, string? NestedEntry, bool Encrypted = false)
{
    private const int MaxArchiveBytes = 128 * 1024 * 1024;
    private const int MaxEntryBytes = 64 * 1024 * 1024;
    private const int MaxXmlBytes = 16 * 1024 * 1024;
    private const string Supported = "Supported format: ZIP with root 0.xml or one P-*.zip containing 0.xml; ZIP64 and multi-volume archives are not supported.";

    internal static Project Load(string path, string? password = null)
    {
        var bytes = ReadFile(path);
        var encrypted = ValidateZip(bytes);
        var (xml, nested, projectEncrypted, information, hardware) = ReadProject(bytes, password, encrypted, 0);
        var project = Knxproj.FromXml(xml);
        if (information is not null) Knxproj.ApplyProjectInformation(project, information);
        project.ArchiveState = new ArchiveState(bytes, nested, projectEncrypted);
        foreach (var part in hardware) CatalogHardwareFacts.Read(part, project.Hardware);
        return project;
    }

    private static (XDocument Xml, string? Nested, bool Encrypted, XDocument? Information, List<XDocument> Hardware) ReadProject(
        byte[] zipBytes, string? password, bool encrypted, int depth)
    {
        if (depth > 1) throw new InvalidDataException("Nested project archives deeper than one level are not supported. " + Supported);
        IReadOnlyDictionary<string, byte[]>? decrypted = encrypted ? EncryptedArchive.ReadAll(zipBytes, password) : null;
        var names = decrypted?.Keys.ToList() ?? ZipNames(zipBytes);
        var hardware = ReadHardwareParts(decrypted, zipBytes, names);
        var candidates = names.Where(n => n.StartsWith("P-", StringComparison.Ordinal) &&
            n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !n.Contains('/')).ToList();
        // /21 keeps project name/GUID/style in project.xml; bounded like 0.xml.
        var information = names.Contains("project.xml")
            ? ReadXml(ReadProjectEntry(decrypted, zipBytes, "project.xml", MaxXmlBytes)) : null;

        if (names.Contains("0.xml"))
        {
            if (candidates.Count > 0) throw new InvalidDataException("Ambiguous root and nested project archives. " + Supported);
            return (ReadXml(ReadProjectEntry(decrypted, zipBytes, "0.xml", MaxXmlBytes)), null, encrypted, information, hardware);
        }
        if (candidates.Count != 1) throw new InvalidDataException(Supported);
        var nested = candidates[0];
        var inner = ReadProjectEntry(decrypted, zipBytes, nested, MaxEntryBytes);
        var innerEncrypted = ValidateZip(inner);
        var (xml, deeper, _, innerInformation, innerHardware) = ReadProject(inner, password, innerEncrypted, depth + 1);
        if (deeper is not null) throw new InvalidDataException("Nested project archives deeper than one level are not supported. " + Supported);
        hardware.AddRange(innerHardware);
        return (xml, nested, encrypted || innerEncrypted, innerInformation ?? information, hardware);
    }

    /// <summary>
    /// Manufacturer hardware data is optional planning input (BusCurrent, IsPowerSupply, IsCoupler).
    /// An unreadable part leaves the facts empty, so the check stays silent instead of failing the project.
    /// </summary>
    private static List<XDocument> ReadHardwareParts(IReadOnlyDictionary<string, byte[]>? decrypted,
        byte[] zipBytes, List<string> names)
    {
        var parts = new List<XDocument>();
        foreach (var name in names)
        {
            if (!name.EndsWith("/Hardware.xml", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                parts.Add(ReadXml(ReadProjectEntry(decrypted, zipBytes, name, MaxXmlBytes)));
            }
            catch (Exception e) when (e is InvalidDataException or XmlException) { }
        }
        return parts;
    }

    private static List<string> ZipNames(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static byte[] ReadProjectEntry(IReadOnlyDictionary<string, byte[]>? decrypted, byte[] zipBytes, string name, int limit)
    {
        byte[] bytes;
        if (decrypted is not null)
        {
            if (!decrypted.TryGetValue(name, out var data)) throw new InvalidDataException(Supported);
            if (data.Length > limit) throw new InvalidDataException("Archive entry exceeds the safety limit.");
            bytes = data;
        }
        else
        {
            using var source = new MemoryStream(zipBytes, false);
            using var zip = new ZipArchive(source, ZipArchiveMode.Read);
            bytes = ReadEntry(zip.GetEntry(name) ?? throw new InvalidDataException(Supported), limit);
        }
        return bytes;
    }

    internal static XDocument? LoadPart(string path, string entryPath)
    {
        var bytes = ReadFile(path);
        if (ValidateZip(bytes))
            throw new InvalidDataException(
                "This project is password-protected; its additional parts cannot be read without the project password. " +
                EncryptedArchive.PasswordRequiredMessage);
        using var source = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        return zip.GetEntry(entryPath) is { } entry ? ReadXml(ReadEntry(entry, MaxXmlBytes)) : null;
    }

    internal static void Save(Project project, XDocument xml, string path)
    {
        var state = project.ArchiveState;
        // No re-encryption exists: writing would drop either the encryption or the password.
        if (state?.Encrypted == true)
            throw new InvalidDataException(
                "This project was opened from a password-protected archive. BusChord cannot write it back without dropping the encryption, " +
                "so saving is refused. Export the project from ETS6 without a password to edit it.");
        using var xmlBytes = new MemoryStream();
        xml.Save(xmlBytes);
        if (xmlBytes.Length > MaxXmlBytes) throw new InvalidDataException("Project XML exceeds the 16 MiB safety limit.");
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (state is not null) output.Write(state.Bytes);
                using (var zip = new ZipArchive(output, state is null ? ZipArchiveMode.Create : ZipArchiveMode.Update, true))
                {
                    if (state?.NestedEntry is { } nested)
                    {
                        var entry = zip.GetEntry(nested) ?? throw new InvalidDataException(Supported);
                        var inner = ReadEntry(entry, MaxEntryBytes);
                        using var buffer = new MemoryStream();
                        buffer.Write(inner);
                        using (var innerZip = new ZipArchive(buffer, ZipArchiveMode.Update, true))
                            Replace(innerZip, "0.xml", xmlBytes.ToArray());
                        Replace(zip, nested, buffer.ToArray());
                    }
                    else Replace(zip, "0.xml", xmlBytes.ToArray());
                }
                output.Flush(true);
            }
            // Validate the finished candidate before the only operation touching the destination.
            _ = Load(temporary);
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Replace(ZipArchive zip, string name, byte[] bytes)
    {
        if (zip.Mode == ZipArchiveMode.Update) zip.GetEntry(name)?.Delete();
        using var output = zip.CreateEntry(name).Open();
        output.Write(bytes);
    }

    private static byte[] ReadFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxArchiveBytes) throw new InvalidDataException("Archive exceeds the 128 MiB safety limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        var length = entry.Length;
        if (length > limit) throw new InvalidDataException("Archive entry exceeds the safety limit.");
        using var input = entry.Open();
        var bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Archive entry length mismatch.");
        return bytes;
    }

    private static XDocument ReadXml(byte[] bytes)
    {
        if (bytes.Length > MaxXmlBytes) throw new InvalidDataException("Project XML exceeds the 16 MiB safety limit.");
        using var input = new MemoryStream(bytes, false);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes };
        // Limit depth before LINQ-to-XML/model recursion can consume the stack.
        using (var reader = XmlReader.Create(input, settings))
            while (reader.Read())
                if (reader.Depth > 128) throw new InvalidDataException("Project XML exceeds the depth safety limit.");
        input.Position = 0;
        using var bounded = XmlReader.Create(input, settings);
        return XDocument.Load(bounded, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Validates the central directory and returns whether any entry is encrypted.</summary>
    private static bool ValidateZip(byte[] bytes)
    {
        // Check central-directory flags before opening encrypted/AES entries. No passwords or extraction.
        int end = -1;
        for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            if (U32(i) == 0x06054b50 && i + 22 + U16(i + 20) == bytes.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException(Supported);
        var count = U16(end + 10);
        var offset = U32(end + 16);
        var size = U32(end + 12);
        if (U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 8) != count || count > 4096 ||
            offset == uint.MaxValue || size == uint.MaxValue || (long)offset + size != end)
            throw new InvalidDataException(Supported + " Limit: 4096 entries.");
        var cursor = checked((int)offset);
        long total = 0;
        var encrypted = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if ((long)cursor + 46 > end || U32(cursor) != 0x02014b50) throw new InvalidDataException(Supported);
            var local = U32(cursor + 42);
            if ((long)local + 30 > offset || U32((int)local) != 0x04034b50) throw new InvalidDataException(Supported);
            var flags = U16(cursor + 8);
            var method = U16(cursor + 10);
            // Strong encryption (bit 6) and masked headers (bit 13) are not supported.
            if ((flags & 0x2040) != 0 || (U16((int)local + 6) & 0x2040) != 0)
                throw new InvalidDataException("Unsupported ZIP encryption. " + Supported);
            encrypted |= (flags & 0x1) != 0 || method == 99 || (U16((int)local + 6) & 0x1) != 0 || U16((int)local + 8) == 99;
            var length = U32(cursor + 24);
            total += length;
            if (length > MaxEntryBytes || total > 256L * 1024 * 1024) throw new InvalidDataException("Archive expanded size exceeds safety limits (64 MiB/entry, 256 MiB total).");
            var nameLength = U16(cursor + 28);
            var next = (long)cursor + 46 + nameLength + U16(cursor + 30) + U16(cursor + 32);
            if (next > end) throw new InvalidDataException(Supported);
            // Compare raw name bytes as well as ZipArchive's decoded entry names below.
            if (!names.Add(Convert.ToHexString(bytes.AsSpan(cursor + 46, nameLength)))) throw new InvalidDataException("Duplicate archive entry names are not supported.");
            cursor = (int)next;
        }
        if (cursor != end) throw new InvalidDataException(Supported);
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != count) throw new InvalidDataException("Ambiguous archive entry names.");

        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
        return encrypted;
    }
}
