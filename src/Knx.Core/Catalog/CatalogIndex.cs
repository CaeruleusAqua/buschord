using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;

namespace Knx.Catalog;

/// <summary>
/// Per-catalogue metadata cache in %LOCALAPPDATA%\BusChord\cache\catalog-index. One gzip-compressed
/// JSON document per library file, validated by file name, size, last-write time, schema version
/// and content identity. A warm start therefore never reads catalogue XML. Writes are atomic
/// (temp + replace); a corrupt or outdated document is ignored and rebuilt.
///
/// Identity is verified against the file itself, never against its timestamps alone: a small
/// sidecar next to the document remembers the SHA-256 of the content that was hashed once, plus a
/// fingerprint (size, first and last 64 KiB) that is re-read on every warm start. Only when both
/// the sidecar's size/mtime and the fingerprint still match is the remembered full hash reused;
/// any difference re-reads and re-hashes the whole file, which is what decides the cache. The
/// remaining ceiling is a file whose content changed while its size, timestamp and sampled head
/// and tail stayed identical; that is deliberate local tampering, and whoever can write the cache
/// directory can replace the catalogue instead.
///
/// The document carries the derived search text and datapoint index so a warm start assigns
/// strings instead of recomputing them. Communication objects travel gzip-compressed and base64
/// encoded, and the model materialises only the programs a pane actually opens.
/// </summary>
public sealed class CatalogIndexStore
{
    /// <summary>Bump when the document shape changes; every entry is rebuilt once.</summary>
    public const int SchemaVersion = 8;

    /// <summary>Maximum compressed cache document; anything larger is not trusted.</summary>
    public const long MaximumIndexBytes = 64L * 1024 * 1024;

    /// <summary>Maximum decompressed cache document; guards the gzip/JSON read.</summary>
    public const long MaximumIndexJsonBytes = 512L * 1024 * 1024;

    /// <summary>Maximum identity sidecar; it holds one short line.</summary>
    private const long MaximumIdentityBytes = 512;

    /// <summary>Head and tail sample of a catalogue file used as its cheap fingerprint.</summary>
    private const int FingerprintSampleBytes = 64 * 1024;

    public CatalogIndexStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
        CleanupTemporaries();
    }

    public string Directory { get; }

    /// <summary>Extracted inner archives of vendor ZIP wrappers (derived data).</summary>
    public string ArchiveDirectory => IOPath.Combine(Directory, "archives");

    public static string DefaultDirectory => IOPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppSettings.AppFolderName, "cache", "catalog-index");

    /// <summary>Cache file for one library catalogue (path hash keeps long/unsafe names out).</summary>
    public string IndexPathFor(string catalogPath)
    {
        var full = IOPath.GetFullPath(catalogPath).ToLowerInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
        return IOPath.Combine(Directory, digest + ".json.gz");
    }

    /// <summary>Verified-identity sidecar of one cache document.</summary>
    public string IdentityPathFor(string catalogPath) =>
        IndexPathFor(catalogPath) + ".identity";

    /// <summary>Loads a catalogue from the cache when its identity and schema still match.</summary>
    public bool TryLoad(string catalogPath, out KnxProductCatalog? catalog)
    {
        catalog = null;
        try
        {
            var file = new FileInfo(catalogPath);
            if (!file.Exists) return false;
            var index = IndexPathFor(catalogPath);
            var indexInfo = new FileInfo(index);
            if (!indexInfo.Exists || indexInfo.Length > MaximumIndexBytes) return false;

            // Cheap path: a previously verified identity whose size, last-write time and
            // fingerprint still hold, so the full content hash does not have to be read again.
            var identity = ReadIdentity(index);
            var reusable = identity is not null && identity.Length == file.Length &&
                identity.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks &&
                string.Equals(identity.Fingerprint, Fingerprint(catalogPath), StringComparison.OrdinalIgnoreCase);
            var contentHash = reusable ? identity!.ContentHash : ContentHash(catalogPath);

            CatalogIndexDocument? document;
            try
            {
                document = ReadDocument(index);
            }
            catch (Exception)
            {
                return false;
            }

            if (document is null || document.Version != SchemaVersion) return false;
            if (!string.Equals(document.FileName, file.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (document.Length != file.Length) return false;
            if (document.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks) return false;
            // The cache is untrusted input: the document only counts when it describes exactly the
            // content the file has, never merely its size or timestamp.
            if (!string.Equals(document.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase)) return false;
            if (document.BackingArchiveName is { Length: > 0 } backing)
            {
                if (!IsBackingName(backing)) return false;
                if (!File.Exists(IOPath.Combine(ArchiveDirectory, backing))) return false;
            }
            if (!LocationsAreSafe(catalogPath, document)) return false;

            catalog = KnxProductCatalog.FromIndex(catalogPath, document, ArchiveDirectory);
            if (!reusable) WriteIdentity(index, file, contentHash);
            return true;
        }
        catch (Exception)
        {
            // Corrupt, truncated or unreadable cache: the caller rebuilds from XML.
            return false;
        }
    }

    /// <summary>Writes the catalogue metadata atomically; failures never break a catalogue load.</summary>
    public void Save(KnxProductCatalog catalog)
    {
        if (!catalog.Indexable) return;
        try
        {
            var file = new FileInfo(catalog.Path);
            if (!file.Exists) return;
            System.IO.Directory.CreateDirectory(Directory);
            var target = IndexPathFor(catalog.Path);
            var document = catalog.BuildIndexDocument(file);
            WriteAtomic(target, stream =>
            {
                using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
                JsonSerializer.Serialize(gzip, document, CatalogIndexJsonContext.Default.CatalogIndexDocument);
            });
            WriteIdentity(target, file, document.ContentHash);
        }
        catch (Exception)
        {
            // A read-only profile or a concurrent writer must not affect the catalogue itself.
        }
    }

    /// <summary>Drops the cache of a removed or replaced catalogue file.</summary>
    public void Remove(string catalogPath)
    {
        try
        {
            var index = IndexPathFor(catalogPath);
            // A nested wrapper owns an extracted inner archive; remove that derived file too.
            try
            {
                var document = ReadDocument(index);
                if (document?.BackingArchiveName is { Length: > 0 } backing && IsBackingName(backing))
                {
                    var backingPath = IOPath.Combine(ArchiveDirectory, backing);
                    if (File.Exists(backingPath)) File.Delete(backingPath);
                }
            }
            catch (Exception)
            {
            }
            Delete(index);
            Delete(IdentityPathFor(catalogPath));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Remembers the content hash of a catalogue file that was verified against it.</summary>
    private void WriteIdentity(string index, FileInfo file, string contentHash)
    {
        try
        {
            var line = string.Join('|', SchemaVersion, file.Length, file.LastWriteTimeUtc.Ticks,
                Fingerprint(file.FullName), contentHash) + "\n";
            WriteAtomic(IdentityPathOf(index), stream => stream.Write(Encoding.ASCII.GetBytes(line)));
        }
        catch (Exception)
        {
            // Without the sidecar the next load simply hashes the file again.
        }
    }

    /// <summary>Reads the verified-identity sidecar; anything malformed is ignored.</summary>
    private static CatalogIdentity? ReadIdentity(string index)
    {
        try
        {
            var info = new FileInfo(IdentityPathOf(index));
            if (!info.Exists || info.Length > MaximumIdentityBytes) return null;
            var fields = File.ReadAllText(info.FullName).Trim().Split('|');
            if (fields.Length != 5) return null;
            if (!int.TryParse(fields[0], out var version) || version != SchemaVersion) return null;
            if (!long.TryParse(fields[1], out var length)) return null;
            if (!long.TryParse(fields[2], out var ticks)) return null;
            if (!IsHex64(fields[3]) || !IsHex64(fields[4])) return null;
            return new CatalogIdentity(length, ticks, fields[3], fields[4]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string IdentityPathOf(string index) => index + ".identity";

    private static void WriteAtomic(string target, Action<Stream> write)
    {
        string? temp = null;
        try
        {
            temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = File.Create(temp)) write(stream);
            File.Move(temp, target, overwrite: true);
            temp = null;
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (Exception) { }
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static CatalogIndexDocument? ReadDocument(string index)
    {
        using var stream = File.OpenRead(index);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var bounded = new BoundedReadStream(gzip, MaximumIndexJsonBytes, IOPath.GetFileName(index));
        // Inflate into memory first: the JSON reader would otherwise pull the document through the
        // gzip stream in tiny steps, and the span overload is the fastest deserialization path.
        using var buffer = new MemoryStream();
        bounded.CopyTo(buffer, 1 << 20);
        return JsonSerializer.Deserialize(
            buffer.GetBuffer().AsSpan(0, (int)buffer.Length), CatalogIndexJsonContext.Default.CatalogIndexDocument);
    }

    /// <summary>SHA-256 of the catalogue file; the cache identity is revalidated against this.</summary>
    internal static string ContentHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// SHA-256 over the file size, its first 64 KiB and its last 64 KiB: cheap enough to read on
    /// every warm start, and for a catalogue archive the tail carries the ZIP directory with the
    /// name, size and CRC of every entry. A file shorter than the sample is covered completely.
    /// </summary>
    internal static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, info.Length);
        hash.AppendData(length);
        var buffer = new byte[FingerprintSampleBytes];
        var head = Read(stream, buffer, buffer.Length);
        hash.AppendData(buffer, 0, head);
        var remaining = info.Length - head;
        if (remaining > 0)
        {
            var tail = (int)Math.Min(FingerprintSampleBytes, remaining);
            stream.Seek(-tail, SeekOrigin.End);
            hash.AppendData(buffer, 0, Read(stream, buffer, tail));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        static int Read(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }
    }

    /// <summary>
    /// The cache is untrusted: an XML catalogue's source paths must resolve below its directory,
    /// and an archive's entry names must be relative without a ".." segment. The content hash
    /// already binds the document to the exact archive, so entry existence is left to the lazy
    /// reader instead of a central-directory scan on every warm start.
    /// </summary>
    private static bool LocationsAreSafe(string catalogPath, CatalogIndexDocument document)
    {
        var isXml = document.IsXml || catalogPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        if (isXml)
        {
            var root = IOPath.GetFullPath(IOPath.GetDirectoryName(catalogPath)!);
            bool SafeFile(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var full = IOPath.GetFullPath(IOPath.IsPathRooted(value) ? value : IOPath.Combine(root, value));
                return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(root + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            return document.Programs.All(p => p.SourceEntry is null && SafeFile(p.SourceFilePath)) &&
                   document.Parts.All(p => SafeFile(p.Location));
        }

        // The content hash above already binds the document to this exact archive, so a name can
        // only be wrong, not point somewhere else. Structural safety is what matters here: no
        // rooted path and no ".." segment. Listing the archive's entries would cost a central-
        // directory scan per catalogue on every warm start; a missing entry surfaces when the
        // lazy reader opens it.
        bool SafeEntry(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !IOPath.IsPathRooted(value) &&
            !value.Split('/', '\\').Any(part => part == "..");
        return document.Programs.All(p => SafeEntry(p.SourceEntry) &&
                                            (p.SourceFilePath is null || SafeEntry(p.SourceFilePath))) &&
               document.Parts.All(p => SafeEntry(p.Location));
    }

    /// <summary>Only the hex archive names this store writes are accepted from a cache document.</summary>
    private static bool IsBackingName(string name)
    {
        if (name.Length != 64 + ".knxprod".Length ||
            !name.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase)) return false;
        return IsHex64(name.AsSpan(0, 64));
    }

    private static bool IsHex64(ReadOnlySpan<char> value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
            if (!char.IsAsciiHexDigitLower(character)) return false;
        return true;
    }

    /// <summary>Removes temp files a crashed writer may have left behind (older than a day).</summary>
    private void CleanupTemporaries()
    {
        foreach (var directory in new[] { Directory, ArchiveDirectory })
        {
            try
            {
                if (!System.IO.Directory.Exists(directory)) continue;
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.tmp"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1)) continue;
                    try { File.Delete(file); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>
/// Content identity of one catalogue file that was hashed once: the size and last-write time it
/// had then, the cheap fingerprint that is re-read on every start, and the full content hash.
/// </summary>
internal sealed record CatalogIdentity(long Length, long LastWriteUtcTicks, string Fingerprint, string ContentHash);

internal sealed class CatalogIndexDocument
{    public int Version { get; set; }
    public string FileName { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string? ManufacturerId { get; set; }
    public string? ManufacturerName { get; set; }
    public bool Complete { get; set; }
    public bool IsXml { get; set; }
    public string ContentHash { get; set; } = "";
    public string? BackingArchiveName { get; set; }
    public List<string> Images { get; set; } = [];
    public List<CatalogSectionIndex> Sections { get; set; } = [];
    public List<CatalogProductIndex> Products { get; set; } = [];
    public List<CatalogProgramIndex> Programs { get; set; } = [];
    public List<CatalogHardware2ProgramIndex> Hardware2Programs { get; set; } = [];
    public List<CatalogPartIndex> Parts { get; set; } = [];
}

internal sealed class CatalogSectionIndex
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public List<CatalogSectionIndex> Sections { get; set; } = [];
    public List<CatalogItemIndex> Items { get; set; } = [];
}

internal sealed class CatalogItemIndex
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ProductRefId { get; set; }
    public string? Description { get; set; }
    public string? Hardware2ProgramRefId { get; set; }
}

internal sealed class CatalogProductIndex
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? OrderNumber { get; set; }
    public string? HardwareRefId { get; set; }
    public string? Description { get; set; }
    public List<string> ProgramRefIds { get; set; } = [];
    public List<string> CatalogPaths { get; set; } = [];
    public List<string> EntryRefIds { get; set; } = [];
    public List<string> Hardware2ProgramIds { get; set; } = [];
    /// <summary>Lowercased product search text; computed once at index build, never rebuilt on warm start.</summary>
    public string SearchText { get; set; } = "";
}

internal sealed class CatalogProgramIndex
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ApplicationNumber { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public bool Broken { get; set; }
    public string? RegistrationStatus { get; set; }
    public string? MinEtsVersion { get; set; }
    public string? MaskVersion { get; set; }
    public bool DownloadInfoIncomplete { get; set; }
    public bool RequiresExternalSoftware { get; set; }
    public List<string> Extensions { get; set; } = [];
    public string? SourceEntry { get; set; }
    public string? SourceFilePath { get; set; }
    /// <summary>Base64(gzip(packed communication objects)); see <see cref="CatalogComObjectPacking"/>.</summary>
    public string PackedComObjects { get; set; } = "";
    /// <summary>Lowercased program search text; computed once at index build.</summary>
    public string SearchText { get; set; } = "";
    /// <summary>Normalised datapoint ids (";dpst-1-1;..."); computed once at index build.</summary>
    public string DatapointTypes { get; set; } = "";
}

/// <summary>
/// Communication objects of one application program as one delimited string. Reading them back is
/// the warm start's dominant cost: as JSON objects the same metadata deserializes at roughly
/// 40 MB/s, as a packed string it parses about twenty times faster (measured on the local library).
/// Field separator is U+001F, record separator U+001E; XML 1.0 forbids both in character data, so
/// catalogue texts cannot contain them.
/// </summary>
internal static class CatalogComObjectPacking
{
    private const char FieldSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    public static string Pack(IEnumerable<ComObject> comObjects,
        Dictionary<string, (string? Text, string? FunctionText)> display)
    {
        var builder = new StringBuilder();
        foreach (var comObject in comObjects)
        {
            var (text, functionText) = display.GetValueOrDefault(comObject.Id);
            Append(builder, comObject.Id);
            Append(builder, comObject.Name);
            builder.Append(comObject.Number).Append(FieldSeparator);
            builder.Append(comObject.SizeInBit).Append(FieldSeparator);
            Append(builder, comObject.DatapointType);
            Append(builder, text);
            Append(builder, functionText);
            builder.Length--; // no field separator after the last field
            builder.Append(RecordSeparator);
        }
        return builder.ToString();

        static void Append(StringBuilder builder, string? value) =>
            builder.Append(value?.Replace(FieldSeparator, ' ').Replace(RecordSeparator, ' ')).Append(FieldSeparator);
    }

    /// <summary>
    /// Gzip-compresses and base64-encodes one packed payload. The index document then carries
    /// about a quarter of the characters, and a warm start never pays for the encoding cost of
    /// programs it does not open.
    /// </summary>
    public static string Encode(string packed)
    {
        if (packed.Length == 0) return "";
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(packed);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Decodes and unpacks one program's payload; a corrupt payload leaves it empty.</summary>
    public static bool TryUnpack(string encoded, List<ComObject> objects,
        Dictionary<string, (string? Text, string? FunctionText)> display)
    {
        if (encoded.Length == 0) return true;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            UnpackInto(Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length), objects, display);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the packed records straight into the program's object list and display map. Spans
    /// keep the per-record overhead to the strings that are actually retained.
    /// </summary>
    public static void UnpackInto(string packed, List<ComObject> objects,
        Dictionary<string, (string? Text, string? FunctionText)> display)
    {
        var text = packed.AsSpan();
        var start = 0;
        while (start < text.Length)
        {
            var separator = text[start..].IndexOf(RecordSeparator);
            var end = separator < 0 ? text.Length : start + separator;
            var position = start;
            var id = Next(text, ref position, end);
            var name = Next(text, ref position, end);
            var number = Integer(text, ref position, end);
            var size = Integer(text, ref position, end);
            var datapointType = Next(text, ref position, end);
            var displayText = Next(text, ref position, end);
            var functionText = Next(text, ref position, end);
            objects.Add(new ComObject
            {
                Id = id,
                Name = name,
                Number = number,
                SizeInBit = size,
                DatapointType = datapointType.Length == 0 ? null : datapointType,
            });
            // Keep the entry even when both texts are empty: CreateDevice treats a missing entry
            // as "use the COM name", a present empty one as "no display text".
            display[id] = (displayText.Length == 0 ? null : displayText,
                functionText.Length == 0 ? null : functionText);
            start = end + 1;
        }

        static string Next(ReadOnlySpan<char> text, ref int position, int end)
        {
            if (position >= end)
            {
                position = end + 1;
                return "";
            }
            var length = text[position..end].IndexOf(FieldSeparator);
            var stop = length < 0 ? end : position + length;
            var value = text[position..stop].ToString();
            position = stop + 1;
            return value;
        }

        static int Integer(ReadOnlySpan<char> text, ref int position, int end)
        {
            if (position >= end)
            {
                position = end + 1;
                return 0;
            }
            var length = text[position..end].IndexOf(FieldSeparator);
            var stop = length < 0 ? end : position + length;
            int.TryParse(text[position..stop], out var value);
            position = stop + 1;
            return value;
        }
    }
}

internal sealed class CatalogHardware2ProgramIndex
{
    public string Id { get; set; } = "";
    public List<string> Programs { get; set; } = [];
}

internal sealed class CatalogPartIndex
{
    public string Id { get; set; } = "";
    public string Location { get; set; } = "";
}

/// <summary>Source-generated contract keeps warm index reads fast and trim-friendly.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CatalogIndexDocument))]
internal partial class CatalogIndexJsonContext : JsonSerializerContext
{
}

public sealed partial class KnxProductCatalog
{
    internal CatalogIndexDocument BuildIndexDocument(FileInfo file)
    {
        var document = new CatalogIndexDocument
        {
            Version = CatalogIndexStore.SchemaVersion,
            FileName = file.Name,
            Length = file.Length,
            LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
            ManufacturerId = Manufacturer?.Id,
            ManufacturerName = Manufacturer?.Name,
            Complete = MetadataComplete,
            IsXml = _archivePath is null,
            ContentHash = CatalogIndexStore.ContentHash(file.FullName),
            BackingArchiveName = BackingArchivePath is { } backing ? IOPath.GetFileName(backing) : null,
            Images = [.. Images],
        };
        foreach (var section in CatalogSections) document.Sections.Add(ToIndex(section));
        foreach (var product in Products)
        {
            // Recompute with this catalogue's final name and manufacturer: an import relocates
            // the file after parsing, so a text captured during the parse could name the source.
            product.SearchText = CatalogSearchText.Product(product, this);
            document.Products.Add(new CatalogProductIndex
            {
                Id = product.Id,
                Name = product.Name,
                OrderNumber = product.OrderNumber,
                HardwareRefId = product.HardwareRefId,
                Description = product.Description,
                ProgramRefIds = [.. product.ProgramRefIds],
                CatalogPaths = [.. product.CatalogPaths],
                EntryRefIds = [.. product.EntryRefIds],
                Hardware2ProgramIds = [.. product.Hardware2ProgramIds],
                SearchText = product.SearchText,
            });
        }
        foreach (var program in Programs)
        {
            if (program.SearchText.Length == 0) program.SearchText = CatalogSearchText.Program(program);
            if (program.NormalizedDatapointTypes.Length == 0)
                program.NormalizedDatapointTypes = CatalogSearchText.DatapointTypes(program.ComObjects);
            document.Programs.Add(new CatalogProgramIndex
            {
                Id = program.Id,
                Name = program.Name,
                ApplicationNumber = program.ApplicationNumber,
                ApplicationVersion = program.ApplicationVersion,
                SchemaVersion = program.SchemaVersion,
                Broken = program.Broken,
                RegistrationStatus = program.RegistrationStatus,
                MinEtsVersion = program.MinEtsVersion,
                MaskVersion = program.MaskVersion,
                DownloadInfoIncomplete = program.DownloadInfoIncomplete,
                RequiresExternalSoftware = program.RequiresExternalSoftware,
                Extensions = [.. program.Extensions],
                SourceEntry = program.Source?.Entry,
                SourceFilePath = program.Source?.FilePath,
                PackedComObjects = CatalogComObjectPacking.Encode(
                    CatalogComObjectPacking.Pack(program.ComObjects, program.ComObjectDisplay)),
                SearchText = program.SearchText,
                DatapointTypes = program.NormalizedDatapointTypes,
            });
        }
        foreach (var (id, programs) in _hardware2Programs)
            document.Hardware2Programs.Add(new CatalogHardware2ProgramIndex { Id = id, Programs = [.. programs] });
        foreach (var (id, location) in _partByName)
            document.Parts.Add(new CatalogPartIndex { Id = id, Location = location });
        return document;
    }

    private static CatalogSectionIndex ToIndex(CatalogSection section)
    {
        var index = new CatalogSectionIndex { Id = section.Id, Name = section.Name, Number = section.Number };
        foreach (var child in section.Sections) index.Sections.Add(ToIndex(child));
        foreach (var item in section.Items)
            index.Items.Add(new CatalogItemIndex
            {
                Id = item.Id,
                Name = item.Name,
                ProductRefId = item.ProductRefId,
                Description = item.Description,
                Hardware2ProgramRefId = item.Hardware2ProgramRefId,
            });
        return index;
    }

    internal static KnxProductCatalog FromIndex(string path, CatalogIndexDocument document, string archiveDirectory)
    {
        var catalog = new KnxProductCatalog(path);
        var isXml = document.IsXml || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        if (document.BackingArchiveName is { } backingName &&
            File.Exists(IOPath.Combine(archiveDirectory, backingName)))
            catalog._archivePath = IOPath.Combine(archiveDirectory, backingName);
        else if (!isXml) catalog._archivePath = path;
        if (document.ManufacturerId is { } manufacturerId)
            catalog.Manufacturer = new CatalogManufacturer(manufacturerId, document.ManufacturerName ?? manufacturerId);
        catalog.Images.AddRange(document.Images);
        foreach (var section in document.Sections) catalog.CatalogSections.Add(FromIndex(section));
        foreach (var productIndex in document.Products)
        {
            var product = new CatalogProduct
            {
                Id = productIndex.Id,
                Name = productIndex.Name,
                OrderNumber = productIndex.OrderNumber,
                HardwareRefId = productIndex.HardwareRefId,
                Description = productIndex.Description,
                SearchText = productIndex.SearchText,
            };
            product.ProgramRefIds.AddRange(productIndex.ProgramRefIds);
            product.CatalogPaths.AddRange(productIndex.CatalogPaths);
            product.EntryRefIds.AddRange(productIndex.EntryRefIds);
            product.Hardware2ProgramIds.AddRange(productIndex.Hardware2ProgramIds);
            catalog.Products.Add(product);
            catalog._productsById[product.Id] = product;
        }
        foreach (var programIndex in document.Programs)
        {
            // Archived sources are entry names, not file-system paths. Older indexes stored
            // monolithic entry names in SourceFilePath; normalise them to ProgramSource.Entry
            // so the lazy parameter model can still reopen the archive after a warm start.
            var sourceEntry = programIndex.SourceEntry;
            var sourceFilePath = programIndex.SourceFilePath;
            if (!isXml || document.BackingArchiveName is not null)
            {
                sourceEntry ??= sourceFilePath;
                sourceFilePath = null;
            }
            var program = new CatalogApplicationProgram
            {
                Id = programIndex.Id,
                Name = programIndex.Name,
                ApplicationNumber = programIndex.ApplicationNumber,
                ApplicationVersion = programIndex.ApplicationVersion,
                SchemaVersion = programIndex.SchemaVersion,
                Broken = programIndex.Broken,
                RegistrationStatus = programIndex.RegistrationStatus,
                MinEtsVersion = programIndex.MinEtsVersion,
                MaskVersion = programIndex.MaskVersion,
                DownloadInfoIncomplete = programIndex.DownloadInfoIncomplete,
                RequiresExternalSoftware = programIndex.RequiresExternalSoftware,
                Source = new ProgramSource(sourceEntry, sourceFilePath ?? (isXml ? path : null)),
                // Search text and datapoint index travel with the document: a warm start assigns
                // the strings JSON already produced instead of lowercasing and joining every field.
                SearchText = programIndex.SearchText,
                NormalizedDatapointTypes = programIndex.DatapointTypes,
            };
            foreach (var extension in programIndex.Extensions) program.AddExtension(extension);
            // Keep the payload encoded: a library load must not build 200k objects nobody displays.
            program.SetPackedComObjects(programIndex.PackedComObjects);
            catalog.Programs.Add(program);
            catalog._programsById[program.Id] = program;
        }
        foreach (var hardware2Program in document.Hardware2Programs)
            catalog._hardware2Programs[hardware2Program.Id] = [.. hardware2Program.Programs];
        foreach (var part in document.Parts) catalog._partByName[part.Id] = part.Location;
        catalog.MetadataComplete = document.Complete;
        return catalog;
    }

    private static CatalogSection FromIndex(CatalogSectionIndex index, int depth = 0)
    {
        if (depth > KnxProductCatalog.MaximumSectionDepth)
            throw new InvalidDataException(
                $"cached catalogue section nesting exceeds {KnxProductCatalog.MaximumSectionDepth} levels.");
        var section = new CatalogSection { Id = index.Id, Name = index.Name, Number = index.Number };
        foreach (var child in index.Sections) section.Sections.Add(FromIndex(child, depth + 1));
        foreach (var item in index.Items)
            section.Items.Add(new CatalogItem
            {
                Id = item.Id,
                Name = item.Name,
                ProductRefId = item.ProductRefId,
                Description = item.Description,
                Hardware2ProgramRefId = item.Hardware2ProgramRefId,
            });
        return section;
    }
}

