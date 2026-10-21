using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;

namespace Knx.Catalog;

/// <summary>
/// Per-catalogue metadata cache in %LOCALAPPDATA%\BusChord\cache\catalog-index. One gzip-compressed
/// JSON document per library file, validated by file name, size, last-write time and schema
/// version. A warm start therefore never reads catalogue XML. Writes are atomic (temp + replace);
/// a corrupt or outdated document is ignored and rebuilt.
/// </summary>
public sealed class CatalogIndexStore
{
    /// <summary>Bump when the document shape changes; every entry is rebuilt once.</summary>
    public const int SchemaVersion = 4;

    /// <summary>Maximum compressed cache document; anything larger is not trusted.</summary>
    public const long MaximumIndexBytes = 64L * 1024 * 1024;

    /// <summary>Maximum decompressed cache document; guards the gzip/JSON read.</summary>
    public const long MaximumIndexJsonBytes = 512L * 1024 * 1024;

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
            // The cache is untrusted input: revalidate the content, don't rely on size/mtime alone.
            if (document.ContentHash.Length == 0 ||
                !string.Equals(document.ContentHash, ContentHash(catalogPath), StringComparison.OrdinalIgnoreCase))
                return false;
            if (document.BackingArchiveName is { Length: > 0 } backing)
            {
                if (!IsBackingName(backing)) return false;
                if (!File.Exists(IOPath.Combine(ArchiveDirectory, backing))) return false;
            }

            catalog = KnxProductCatalog.FromIndex(catalogPath, document, ArchiveDirectory);
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
        string? temp = null;
        try
        {
            var file = new FileInfo(catalog.Path);
            if (!file.Exists) return;
            System.IO.Directory.CreateDirectory(Directory);
            var target = IndexPathFor(catalog.Path);
            temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = File.Create(temp))
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, catalog.BuildIndexDocument(file),
                    CatalogIndexJsonContext.Default.CatalogIndexDocument);
            File.Move(temp, target, overwrite: true);
            temp = null;
        }
        catch (Exception)
        {
            // A read-only profile or a concurrent writer must not affect the catalogue itself.
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (Exception) { }
        }
    }

    /// <summary>Drops the cache of a removed or replaced catalogue file.</summary>
    public void Remove(string catalogPath)
    {
        try
        {
            var index = IndexPathFor(catalogPath);
            if (!File.Exists(index)) return;
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
            File.Delete(index);
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
        return JsonSerializer.Deserialize(bounded, CatalogIndexJsonContext.Default.CatalogIndexDocument);
    }

    /// <summary>SHA-256 of the catalogue file; the cache identity is revalidated against this.</summary>
    internal static string ContentHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Only the hex archive names this store writes are accepted from a cache document.</summary>
    private static bool IsBackingName(string name)
    {
        if (name.Length != 64 + ".knxprod".Length ||
            !name.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var character in name.AsSpan(0, 64))
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

internal sealed class CatalogIndexDocument
{
    public int Version { get; set; }
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
    public List<CatalogComObjectIndex> ComObjects { get; set; } = [];
}

internal sealed class CatalogComObjectIndex
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public int SizeInBit { get; set; }
    public string? DatapointType { get; set; }
    public string? Text { get; set; }
    public string? FunctionText { get; set; }
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
            if (product.SearchText.Length == 0) product.SearchText = CatalogSearchText.Product(product, this);
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
            });
        }
        foreach (var program in Programs)
        {
            if (program.SearchText.Length == 0) program.SearchText = CatalogSearchText.Program(program);
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
                ComObjects = program.ComObjects.Select(comObject => new CatalogComObjectIndex
                {
                    Id = comObject.Id,
                    Name = comObject.Name,
                    Number = comObject.Number,
                    SizeInBit = comObject.SizeInBit,
                    DatapointType = comObject.DatapointType,
                    Text = program.ComObjectDisplay.GetValueOrDefault(comObject.Id).Text,
                    FunctionText = program.ComObjectDisplay.GetValueOrDefault(comObject.Id).FunctionText,
                }).ToList(),
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
                Source = new ProgramSource(programIndex.SourceEntry,
                    programIndex.SourceFilePath ?? (isXml ? path : null)),
            };
            foreach (var extension in programIndex.Extensions) program.AddExtension(extension);
            foreach (var comObject in programIndex.ComObjects)
            {
                program.ComObjects.Add(new ComObject
                {
                    Id = comObject.Id,
                    Name = comObject.Name,
                    Number = comObject.Number,
                    SizeInBit = comObject.SizeInBit,
                    DatapointType = comObject.DatapointType,
                });
                program.ComObjectDisplay[comObject.Id] = (comObject.Text, comObject.FunctionText);
            }
            catalog.Programs.Add(program);
            catalog._programsById[program.Id] = program;
        }
        foreach (var hardware2Program in document.Hardware2Programs)
            catalog._hardware2Programs[hardware2Program.Id] = [.. hardware2Program.Programs];
        foreach (var part in document.Parts) catalog._partByName[part.Id] = part.Location;
        catalog.MetadataComplete = document.Complete;
        // Search text is derived, not persisted: this keeps the cache small and the format simple.
        foreach (var product in catalog.Products)
            product.SearchText = CatalogSearchText.Product(product, catalog);
        foreach (var program in catalog.Programs)
        {
            program.SearchText = CatalogSearchText.Program(program);
            program.NormalizedDatapointTypes = CatalogSearchText.DatapointTypes(program.ComObjects);
        }
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

