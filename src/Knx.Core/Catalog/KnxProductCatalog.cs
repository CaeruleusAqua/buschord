using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using IOPath = System.IO.Path;

namespace Knx.Catalog;

/// <summary>
/// Reader for KNX product databases (*.knxprod): ZIP archives whose main part is
/// knxprod.xml in namespace http://knx.org/xml/prod/20 (older 14/17/19 occur too), or a
/// loose knxprod.xml, or a directory containing one.
///
/// Element names follow the KNX product XML schema: KNX/ManufacturerData/
/// Manufacturer(@RefId)/{Catalog/CatalogSection, Hardware/Hardware(@Id)/Products/Product,
/// Hardware/Hardware/Hardware2Programs/Hardware2Program/ApplicationProgramRef,
/// ApplicationPrograms/ApplicationProgram, Languages/Language(@Identifier)/TranslationUnit/
/// TranslationElement(@RefId)/Translation(@AttributeName,@Text)}. Namespaces and unknown
/// elements are ignored, so unverified parts may still parse.
///
/// Catalogue listing and search need only program metadata (name, numbers, flags, COM objects,
/// datapoint types). That metadata is read without building the KSXML parameter model; the full
/// model is read lazily on <see cref="FindProgram"/> / <see cref="FindProgramFor"/> for the
/// parameter editor, from the same archive part.
/// </summary>
public sealed partial class KnxProductCatalog
{
    /// <summary>Maximum nesting of catalogue sections; deeper input is rejected, not recursed.</summary>
    public const int MaximumSectionDepth = 64;

    /// <summary>Maximum uncompressed XML of one archive entry before it is rejected.</summary>
    public const long MaximumXmlEntryBytes = 256L * 1024 * 1024;

    /// <summary>Maximum uncompressed size of an inner archive extracted from a vendor ZIP.</summary>
    public const long MaximumNestedArchiveBytes = 1024L * 1024 * 1024;

    private const int MaximumArchiveDepth = 4;

    private readonly Dictionary<string, List<string>> _hardware2Programs = new(StringComparer.Ordinal);
    // Catalogue items resolve their product once per section; keep both lookups O(1) instead of
    // scanning the full product/program lists for every declaration.
    private readonly Dictionary<string, CatalogProduct> _productsById = new(StringComparer.Ordinal);
    // Metadata is appended by the background index build while search reads it: concurrent map.
    private readonly ConcurrentDictionary<string, CatalogApplicationProgram> _programsById = new(StringComparer.Ordinal);

    // Split catalogue archives keep the
    // application programs in separate parts; part names are known without parsing them.
    private readonly Dictionary<string, string> _partByName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedParts = new(StringComparer.Ordinal);
    // Manufacturer display names live in knx_master.xml, not in the catalogue parts.
    private readonly Dictionary<string, string> _manufacturerNames = new(StringComparer.Ordinal);
    private readonly object _modelGate = new();
    private readonly object _enrichGate = new();
    private string? _archivePath;

    private KnxProductCatalog(string path) => Path = path;

    /// <summary>Full path of the parsed catalogue file.</summary>
    public string Path { get; internal set; }

    /// <summary>File name without extension; unique per library entry.</summary>
    public string Name => IOPath.GetFileNameWithoutExtension(Path);

    public CatalogManufacturer? Manufacturer { get; private set; }
    public List<CatalogProduct> Products { get; } = [];
    public List<CatalogSection> CatalogSections { get; } = [];
    public List<CatalogApplicationProgram> Programs { get; } = [];
    /// <summary>Image entries of the archive (or sibling image files), names only.</summary>
    public List<string> Images { get; } = [];

    /// <summary>Program metadata (names, COM objects, DPT, gating flags) covers every application.</summary>
    public bool MetadataComplete { get; internal set; }

    /// <summary>
    /// Application-program parts this catalogue still has to read; zero once its program metadata
    /// is complete (and for catalogues that keep everything in one XML part).
    /// </summary>
    public int ProgramPartCount => _partByName.Count;

    /// <summary>Number of applications whose full KSXML parameter model has been materialised.</summary>
    public int LoadedProgramModels { get; private set; }

    /// <summary>Archives with a stable layout can persist their metadata; nested wrappers cannot.</summary>
    internal bool Indexable { get; set; } = true;

    /// <summary>Inner archive extracted from a vendor ZIP wrapper, when the catalogue is nested.</summary>
    internal string? BackingArchivePath =>
        _archivePath is not null && !string.Equals(_archivePath, Path, StringComparison.OrdinalIgnoreCase)
            ? _archivePath
            : null;

    /// <summary>Fallback location for extracted vendor ZIP wrappers (machine-local, derived data).</summary>
    private static string DefaultArchiveCache => IOPath.Combine(CatalogIndexStore.DefaultDirectory, "archives");

    public static KnxProductCatalog Open(string path) => Open(path, 0, null);

    internal static KnxProductCatalog Open(string path, string? cacheDirectory) => Open(path, 0, cacheDirectory);

    private static KnxProductCatalog Open(string path, int depth, string? cacheDirectory)
    {
        if (depth > MaximumArchiveDepth) throw new InvalidDataException("Catalogue archive nesting exceeds four levels.");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("catalogue path is empty", nameof(path));

        var full = IOPath.GetFullPath(path);
        if (Directory.Exists(full))
        {
            var mono = IOPath.Combine(full, "knxprod.xml");
            if (File.Exists(mono)) return Open(mono, depth, cacheDirectory);
            if (Directory.EnumerateFiles(full, "Catalog.xml", SearchOption.AllDirectories).Any())
                return OpenSplitDirectory(full);
            full = FindCatalogFile(full);
        }
        if (!File.Exists(full)) throw new FileNotFoundException($"catalogue '{path}' not found.", full);

        var catalog = new KnxProductCatalog(full);
        var images = new List<string>();

        if (full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || LooksLikeXml(full))
        {
            var document = LoadXmlFile(full);
            var folder = IOPath.GetDirectoryName(full);
            if (folder is not null && Directory.Exists(folder))
                images.AddRange(Directory.EnumerateFiles(folder).Where(IsImage).Select(f => IOPath.GetFileName(f)));
            catalog.Parse(document, new CatalogSource(full, null));
            catalog.Images.AddRange(images);
            catalog.MetadataComplete = true;
            return catalog;
        }

        using var zip = ZipFile.OpenRead(full);
        // One master read; the split decision comes from the entry names, not from scanning it.
        var masterNames = ReadMasterNames(zip);
        if (HasSplitBase(zip)) return OpenSplitArchive(full, zip, depth, masterNames, cacheDirectory);

        var main = PickMainEntry(zip);
        if (main is null) return OpenSplitArchive(full, zip, depth, masterNames, cacheDirectory);
        catalog._archivePath = full;
        foreach (var (id, name) in masterNames) catalog._manufacturerNames[id] = name;

        XDocument mainDocument;
        using (var stream = OpenXmlEntry(main)) mainDocument = LoadXml(stream);
        images.AddRange(zip.Entries.Where(e => IsImage(e.FullName)).Select(e => e.FullName));

        // Older archives keep device XMLs as separate parts; keep them lazy like split archives.
        foreach (var entry in zip.Entries)
        {
            if (entry == main || !entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = IOPath.GetFileNameWithoutExtension(entry.Name);
            if (stem.Contains("_A-", StringComparison.OrdinalIgnoreCase)) catalog._partByName[stem] = entry.FullName;
        }

        catalog.Parse(mainDocument, new CatalogSource(null, main.FullName));
        catalog.Images.AddRange(images);
        catalog.MetadataComplete = catalog._partByName.Count == 0;
        return catalog;
    }

    /// <summary>
    /// Split catalogue layout: knx_master.xml plus per-manufacturer
    /// Catalog.xml, Hardware.xml and one XML per application program. Hardware and catalogue
    /// metadata are read eagerly; program metadata and parameter models load on demand.
    /// </summary>
    private static KnxProductCatalog OpenSplitArchive(string path, ZipArchive zip, int depth,
        Dictionary<string, string> masterNames, string? cacheDirectory)
    {
        var catalog = new KnxProductCatalog(path) { _archivePath = path };
        foreach (var (id, name) in masterNames) catalog._manufacturerNames[id] = name;
        var entries = zip.Entries
            .Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hardware = entries.FirstOrDefault(e => e.Name.Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase));
        var catalogFile = entries.FirstOrDefault(e => e.Name.Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase));
        if (hardware is null && catalogFile is null)
        {
            // Manufacturer downloads are often a ZIP that contains the .knxprod (also a ZIP).
            var nested = zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (nested is not null) return OpenNested(path, nested, depth, cacheDirectory);

            throw new InvalidDataException(
                $"'{path}' is not a KNX product catalogue (no knxprod.xml, Catalog.xml or Hardware.xml).");
        }

        foreach (var entry in new[] { hardware, catalogFile })
        {
            if (entry is null) continue;
            try
            {
                using var stream = OpenXmlEntry(entry);
                catalog.Parse(LoadXml(stream), new CatalogSource(null, entry.FullName));
            }
            catch (System.Xml.XmlException)
            {
                // A broken part must not make the whole catalogue unreadable.
            }
        }

        foreach (var entry in entries)
        {
            var stem = IOPath.GetFileNameWithoutExtension(entry.Name);
            if (stem.Contains("_A-", StringComparison.OrdinalIgnoreCase)) catalog._partByName[stem] = entry.FullName;
        }

        catalog.Images.AddRange(zip.Entries.Where(e => IsImage(e.FullName)).Select(e => e.FullName));
        catalog.MetadataComplete = catalog._partByName.Count == 0;
        return catalog;
    }

    /// <summary>
    /// Unwraps a vendor ZIP that contains the actual .knxprod (nested archive). The inner archive
    /// is kept in the machine-local cache, so its parts and parameter models stay lazy and the
    /// derived metadata index works across restarts. If the cache is unavailable the programs are
    /// materialised before the temporary archive disappears (the previous behaviour).
    /// </summary>
    private static KnxProductCatalog OpenNested(string outerPath, ZipArchiveEntry nested, int depth,
        string? cacheDirectory)
    {
        var extension = IOPath.GetExtension(nested.Name);
        if (string.IsNullOrEmpty(extension)) extension = ".knxprod";
        var temp = IOPath.Combine(IOPath.GetTempPath(),
            "buschord-" + Guid.NewGuid().ToString("N") + extension);

        try
        {
            if (nested.Length > MaximumNestedArchiveBytes || nested.CompressedLength > MaximumNestedArchiveBytes)
                throw new InvalidDataException(
                    $"nested archive '{nested.FullName}' exceeds the supported size.");
            using (var input = new BoundedReadStream(nested.Open(), MaximumNestedArchiveBytes, nested.FullName))
            using (var output = File.Create(temp))
                input.CopyTo(output);
            var catalog = Open(temp, depth + 1, cacheDirectory);
            var backing = PersistInnerArchive(outerPath, nested, temp, cacheDirectory);
            if (backing is not null)
            {
                catalog._archivePath = backing;
                catalog.Path = outerPath;
                return catalog;
            }

            // Fallback: the temporary archive disappears below, materialise every program.
            catalog.LoadProgramMetadata();
            foreach (var program in catalog.Programs.ToList()) catalog.EnsureFullModel(program);
            catalog._partByName.Clear();
            catalog._archivePath = null;
            catalog.Indexable = false;
            catalog.Path = outerPath;
            return catalog;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Copies the inner archive into the cache, keyed by the outer wrapper identity.</summary>
    private static string? PersistInnerArchive(string outerPath, ZipArchiveEntry nested, string temp,
        string? cacheDirectory)
    {
        string? staging = null;
        try
        {
            var directory = cacheDirectory is null
                ? DefaultArchiveCache
                : IOPath.Combine(cacheDirectory, "archives");
            System.IO.Directory.CreateDirectory(directory);
            var outer = new FileInfo(outerPath);
            var identity = string.Join('|', outer.FullName, outer.Length, outer.LastWriteTimeUtc.Ticks,
                nested.FullName, nested.Length, CatalogIndexStore.SchemaVersion);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
            var target = IOPath.Combine(directory, hash + ".knxprod");
            if (File.Exists(target)) return target;

            staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(temp, staging);
            File.Move(staging, target, overwrite: true);
            staging = null;
            return target;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (staging is not null)
                try { File.Delete(staging); } catch (Exception) { }
        }
    }

    internal void Relocate(string path)
    {
        var oldPath = Path;
        // A nested catalogue reads its parts from the extracted inner archive, not from Path.
        if (_archivePath is not null &&
            string.Equals(_archivePath, oldPath, StringComparison.OrdinalIgnoreCase))
            _archivePath = path;
        // Loose XML catalogues parse their programs from the file itself.
        foreach (var program in Programs)
            if (program.Source is { FilePath: { } file } source &&
                string.Equals(file, oldPath, StringComparison.OrdinalIgnoreCase))
                program.Source = source with { FilePath = path };
        Path = path;
    }

    /// <summary>Split layout extracted to disk (same parts as <see cref="OpenSplitArchive"/>).</summary>
    private static KnxProductCatalog OpenSplitDirectory(string directory)
    {
        var catalog = new KnxProductCatalog(directory);
        foreach (var (id, name) in ReadMasterNames(directory)) catalog._manufacturerNames[id] = name;
        var files = Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).ToList();

        var hardware = files.FirstOrDefault(f => IOPath.GetFileName(f).Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase));
        var catalogFile = files.FirstOrDefault(f => IOPath.GetFileName(f).Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase));
        if (hardware is null && catalogFile is null)
            throw new InvalidDataException($"'{directory}' is not a KNX product catalogue.");

        foreach (var file in new[] { hardware, catalogFile })
        {
            if (file is null) continue;
            try
            {
                catalog.Parse(LoadXmlFile(file), new CatalogSource(file, null));
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        foreach (var file in files)
        {
            var stem = IOPath.GetFileNameWithoutExtension(file);
            if (stem.Contains("_A-", StringComparison.OrdinalIgnoreCase)) catalog._partByName[stem] = file;
        }

        catalog.Images.AddRange(files.Where(IsImage).Select(file => IOPath.GetFileName(file)!));
        catalog.MetadataComplete = catalog._partByName.Count == 0;
        return catalog;
    }

    /// <summary>
    /// Reads program metadata (name, numbers, flags, COM objects, datapoint types) for every
    /// application part that has not been read yet. Does not build any KSXML parameter model.
    /// </summary>
    /// <returns>Number of XML parts that were read.</returns>
    public int LoadProgramMetadata(CancellationToken cancellationToken = default, IProgress<int>? progress = null)
    {
        if (MetadataComplete) return 0;
        lock (_enrichGate)
        {
            if (MetadataComplete) return 0;
            var parts = _partByName.ToArray();
            var read = 0;
            var done = 0;
            var complete = true;
            // One container handle for the whole batch: opening per part would re-read the archive
            // directory of a manufacturer archive for every one of its hundreds of parts.
            using var archive = OpenArchiveForParts();
            foreach (var (id, location) in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_loadedParts.Add(id))
                {
                    if (LoadPartMetadata(location, archive)) read++;
                    else
                    {
                        _loadedParts.Remove(id);
                        complete = false;
                    }
                }
                progress?.Report(++done);
            }
            MetadataComplete = complete;
            return read;
        }
    }

    /// <summary>Shared read handle for a batch of parts; null when they live outside an archive.</summary>
    private ZipArchive? OpenArchiveForParts()
    {
        if (_archivePath is not { } path || !File.Exists(path)) return null;
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Loads metadata of one application-program part (never the parameter model).</summary>
    private bool LoadPartMetadata(string location, ZipArchive? archive = null)
    {
        try
        {
            XDocument document;
            if (_archivePath is null)
            {
                document = LoadXmlFile(location);
            }
            else if (archive is not null)
            {
                var entry = archive.GetEntry(location);
                if (entry is null) return false;
                using var stream = OpenXmlEntry(entry);
                document = LoadXml(stream);
            }
            else
            {
                using var zip = ZipFile.OpenRead(_archivePath);
                var entry = zip.GetEntry(location);
                if (entry is null) return false;
                using var stream = OpenXmlEntry(entry);
                document = LoadXml(stream);
            }
            var manufacturer = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Manufacturer");
            if (manufacturer is null) return false;
            var source = _archivePath is null ? new CatalogSource(location, null) : new CatalogSource(null, location);
            AddProgramMetadata(manufacturer, ReadTranslations(manufacturer), source);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public CatalogProduct? FindProduct(string productId) =>
        _productsById.GetValueOrDefault(productId);

    /// <summary>Exact catalogue entry references, falling back to the product's declared program references.</summary>
    public IReadOnlyList<string> GetProgramReferencesFor(CatalogProduct product) =>
        (product.EntryRefIds.Count > 0 ? product.EntryRefIds : product.ProgramRefIds).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Finds a program by its own id or by a Hardware2Program id that references it.</summary>
    public CatalogApplicationProgram? FindProgram(string programRefId)
    {
        if (string.IsNullOrEmpty(programRefId)) return null;
        var direct = FindProgramById(programRefId);
        if (direct is not null) return direct;
        if (!_hardware2Programs.TryGetValue(programRefId, out var refs)) return null;
        foreach (var reference in refs)
            if (FindProgramById(reference) is { } program) return program;
        return null;
    }

    /// <summary>Loads one detached original application document with its namespace and enclosing XML containers.</summary>
    public XDocument? LoadProgramDocument(string programRefId)
    {
        if (_hardware2Programs.TryGetValue(programRefId, out var references)
            && references.Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("Hardware references multiple application programs; choose an explicit application id.");
        var program = FindProgram(programRefId);
        if (program?.Source is not { } source) return null;
        lock (_modelGate)
        {
            var (element, _) = LoadProgramElement(program, source);
            if (element is null) return null;
            if (element.Document is { } original && original.Descendants().Count(e => e.Name.LocalName == "ApplicationProgram") == 1)
                return new XDocument(original);
            var copy = new XElement(element);
            foreach (var parent in element.Ancestors())
                copy = new XElement(parent.Name, parent.Attributes().Select(a => new XAttribute(a)), copy,
                    parent.Elements().Where(e => e.Name.LocalName == "Languages").Select(e => new XElement(e)));
            return new XDocument(copy);
        }
    }

    /// <summary>Detached manufacturer parts for migration; the original catalogue is never modified. Plain XML has no archive parts.</summary>
    public IReadOnlyDictionary<string, byte[]> ReadManufacturerParts(string? applicationReference = null)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (_archivePath is not null)
        {
            if (applicationReference is not null)
            {
                var id = applicationReference;
                if (_hardware2Programs.TryGetValue(id, out var references))
                {
                    var distinct = references.Distinct(StringComparer.Ordinal).ToArray();
                    if (distinct.Length != 1) throw new InvalidDataException("An exact application is required for migration parts.");
                    id = distinct[0];
                }
                var selected = Knxproj.ApplicationProgramEntryPath(id);
                if (selected is null || !ArchiveEntryLimits.IsApplication(selected)) throw new InvalidDataException("Invalid application identity for migration parts.");
                var manufacturer = selected[..selected.IndexOf('/')];
                using var selectedStream = new FileStream(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (selectedStream.Length > 128L * 1024 * 1024) throw new InvalidDataException("Migration catalogue archive exceeds the 128 MiB safety limit.");
                using var archive = new ZipArchive(selectedStream, ZipArchiveMode.Read);
                if (archive.Entries.Count > 4096 || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
                    throw new InvalidDataException("Ambiguous or excessive migration catalogue entries.");
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName;
                    if (!name.StartsWith(manufacturer + "/", StringComparison.Ordinal) || name.EndsWith('/')) continue;
                    if (ArchiveEntryLimits.IsApplication(name) && name != selected) continue;
                    if (name.Split('/').Any(s => s is "" or "." or ".." || s.Any(c => char.IsControl(c) || c is '\\' or ':'))
                        || entry.Length > ArchiveEntryLimits.For(name) || (total += entry.Length) > 128L * 1024 * 1024)
                        throw new InvalidDataException("Selected manufacturer parts exceed archive safety limits.");
                    var data = new byte[checked((int)entry.Length)];
                    using var input = entry.Open(); input.ReadExactly(data);
                    if (input.ReadByte() != -1) throw new InvalidDataException("Catalogue entry length mismatch.");
                    parts.Add(name, data);
                }
                return new System.Collections.ObjectModel.ReadOnlyDictionary<string, byte[]>(parts);
            }
            using var stream = new FileStream(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 128L * 1024 * 1024) throw new InvalidDataException("Migration catalogue archive exceeds the 128 MiB safety limit.");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Catalogue changed while reading migration parts.");
            foreach (var (name, data) in EncryptedArchive.ReadAll(bytes, null))
                if (name.StartsWith("M-", StringComparison.Ordinal) && !(name.EndsWith('/') && data.Length == 0)) parts.Add(name, data);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, byte[]>(parts);
    }

    /// <summary>
    /// Resolves the program a product runs: the Hardware2ProgramRefId of the catalogue entry
    /// first, then the Hardware2Programs of the product's hardware, then the product id itself.
    /// </summary>
    public CatalogApplicationProgram? FindProgramFor(CatalogProduct product)
    {
        foreach (var entryRef in product.EntryRefIds)
            if (FindProgram(entryRef) is { } byEntry) return byEntry;
        foreach (var programId in product.ProgramRefIds)
            if (FindProgram(programId) is { } byHardware) return byHardware;
        return FindProgram(product.Id);
    }

    /// <summary>
    /// Resolves the product's program from metadata only: no part is parsed, no KSXML model is
    /// built. Returns null while a split archive's program metadata is not indexed yet.
    /// </summary>
    public CatalogApplicationProgram? FindProgramSummaryFor(CatalogProduct product)
    {
        foreach (var entryRef in product.EntryRefIds)
            if (SummaryFromReference(entryRef) is { } byEntry) return byEntry;
        foreach (var programId in product.ProgramRefIds)
            if (SummaryFromReference(programId) is { } byHardware) return byHardware;
        return SummaryFromReference(product.Id);
    }

    private CatalogApplicationProgram? SummaryFromReference(string referenceId)
    {
        if (_programsById.TryGetValue(referenceId, out var direct)) return direct;
        if (!_hardware2Programs.TryGetValue(referenceId, out var refs)) return null;
        foreach (var reference in refs)
            if (_programsById.TryGetValue(reference, out var program)) return program;
        return null;
    }

    /// <summary>Full parameter model; application programs of split archives load on first use.</summary>
    private CatalogApplicationProgram? FindProgramById(string id)
    {
        if (_programsById.TryGetValue(id, out var found))
        {
            EnsureFullModel(found);
            return found;
        }

        // Split archives: pull the part that holds this program, once.
        if (!_partByName.TryGetValue(id, out var location) || !_loadedParts.Add(id)) return null;
        LoadPartMetadata(location);
        if (!_programsById.TryGetValue(id, out found)) return null;
        EnsureFullModel(found);
        return found;
    }

    /// <summary>Builds the KSXML model of one application, once; safe for concurrent callers.</summary>
    private void EnsureFullModel(CatalogApplicationProgram program)
    {
        if (program.ModelLoaded) return;
        lock (_modelGate)
        {
            if (program.ModelLoaded) return;
            try
            {
                if (program.Source is { } source && LoadProgramElement(program, source) is var (element, translations)
                    && element is not null)
                    FillProgramModel(program, element, translations);
            }
            catch (System.Xml.XmlException)
            {
            }
            catch (IOException)
            {
            }
            program.ModelLoaded = true;
            LoadedProgramModels++;
        }
    }

    /// <summary>Reads the program subtree and its translation unit from the archive part.</summary>
    private (XElement? Element, Dictionary<string, Dictionary<string, Dictionary<string, string>>> Translations)
        LoadProgramElement(CatalogApplicationProgram program, ProgramSource source)
    {
        XDocument document;
        if (source.Entry is { } entry)
        {
            if (_archivePath is null || !File.Exists(_archivePath)) return (null, EmptyTranslations);
            using var zip = ZipFile.OpenRead(_archivePath);
            var part = zip.GetEntry(entry);
            if (part is null) return (null, EmptyTranslations);
            using var stream = OpenXmlEntry(part);
            document = LoadXml(stream);
        }
        else if (source.FilePath is { } file && File.Exists(file))
        {
            document = LoadXmlFile(file);
        }
        else return (null, EmptyTranslations);

        var manufacturer = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Manufacturer");
        var translations = manufacturer is null ? EmptyTranslations : ReadTranslations(manufacturer);
        var element = document.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "ApplicationProgram" && Attr(e, "Id") == program.Id);
        return (element, translations);
    }

    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> EmptyTranslations =
        new(StringComparer.Ordinal);

    private void FillProgramModel(CatalogApplicationProgram program, XElement element,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations)
    {
        // We own the loaded document: detach instead of cloning the 30 MB subtree.
        if (element.Parent is not null) element.Remove();
        foreach (var declaration in element.DescendantsAndSelf())
        {
            var declarationId = Attr(declaration, "Id");
            if (declarationId is null) continue;
            foreach (var attribute in new[] { "Text", "SuffixText", "Description" })
            {
                var text = Text(translations, declarationId, attribute, Attr(declaration, attribute));
                if (text.Length > 0) declaration.SetAttributeValue(attribute, text);
            }
        }
        var parsed = Ksxml.Parse(new XDocument(element));
        program.ParameterTypes.AddRange(parsed.Types);
        program.ParameterBlocks.AddRange(parsed.Blocks);
        program.ParameterInstances.AddRange(parsed.Instances);
        program.ParameterReferences.AddRange(Ksxml.ParseParameterReferences(element));
        program.UnsupportedFeatures.AddRange(Ksxml.UnsupportedFeatures(element));
        // Plug-in declarations are not persisted in the metadata cache; read them again for the
        // parameter editor (additive, so metadata read earlier stays valid).
        ReadExtensions(program, element);
        ReadParameterPlugins(program, element);
    }

    /// <summary>Parsed metadata of one document; parameters stay in XML until requested.</summary>
    private void Parse(XDocument document, CatalogSource source)
    {
        var root = document.Root ?? throw new InvalidDataException("empty catalogue XML");
        var manufacturers = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Manufacturer").ToList();
        if (manufacturers.Count == 0)
            throw new InvalidDataException("not a KNX product catalogue: no <Manufacturer> element.");

        foreach (var manufacturer in manufacturers)
        {
            var translations = ReadTranslations(manufacturer);
            var id = Attr(manufacturer, "RefId") ?? Attr(manufacturer, "Id") ?? "";
            var name = Text(translations, id, "Name", Attr(manufacturer, "Name"));
            if (string.IsNullOrWhiteSpace(name) || name == id)
                name = _manufacturerNames.GetValueOrDefault(id) ?? name;
            Manufacturer ??= new CatalogManufacturer(id, string.IsNullOrWhiteSpace(name) ? id : name);
            ReadHardware(manufacturer, translations);
            ReadCatalog(manufacturer, translations);
            AddProgramMetadata(manufacturer, translations, source);
        }

        foreach (var product in Products)
            product.SearchText = CatalogSearchText.Product(product, this);
    }

    /// <summary>Manufacturer display names from knx_master.xml (optional part of the archive).</summary>
    private static Dictionary<string, string> ReadMasterNames(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            e.Name.Equals("knx_master.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = OpenXmlEntry(entry);
            return ParseMasterNames(stream);
        }
        catch (System.Xml.XmlException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> ReadMasterNames(string directory)
    {
        var file = Directory.EnumerateFiles(directory, "knx_master.xml", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = OpenXmlFile(file);
            return ParseMasterNames(stream);
        }
        catch (System.Xml.XmlException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> ParseMasterNames(Stream stream)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
        });
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.LocalName != "Manufacturer") continue;
            var id = reader.GetAttribute("Id");
            var name = reader.GetAttribute("Name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) names[id] = name;
        }
        return names;
    }

    /// <summary>
    /// Reads one language table in a single pass: the enclosing Language, TranslationUnit and
    /// TranslationElement are found by walking the ancestors once per translation.
    /// </summary>
    private static Dictionary<string, Dictionary<string, Dictionary<string, string>>> ReadTranslations(
        XElement manufacturer)
    {
        var result = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        var defaultLanguage = Attr(manufacturer, "DefaultLanguage") ?? "en-US";

        foreach (var translation in manufacturer.Descendants().Where(e => e.Name.LocalName == "Translation"))
        {
            string? elementRef = null, unitRef = null, languageIdentifier = null;
            for (var ancestor = translation.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                switch (ancestor.Name.LocalName)
                {
                    case "TranslationElement": elementRef ??= Attr(ancestor, "RefId"); break;
                    case "TranslationUnit": unitRef ??= Attr(ancestor, "RefId"); break;
                    case "Language": languageIdentifier ??= Attr(ancestor, "Identifier"); break;
                }
                if (languageIdentifier is not null && elementRef is not null) break;
            }
            var language = Attr(translation, "Language") ?? languageIdentifier ?? defaultLanguage;
            var refId = elementRef ?? unitRef ?? Attr(translation, "RefId") ?? "";
            var attribute = Attr(translation, "AttributeName") ?? "Text";
            var text = Attr(translation, "Text") ?? translation.Value;
            if (language.Length == 0 || refId.Length == 0 || string.IsNullOrWhiteSpace(text)) continue;

            if (!result.TryGetValue(language, out var byRef))
                result[language] = byRef = new(StringComparer.Ordinal);
            if (!byRef.TryGetValue(refId, out var byAttribute))
                byRef[refId] = byAttribute = new(StringComparer.Ordinal);
            byAttribute[attribute] = text;
        }
        return result;
    }

    private void ReadHardware(XElement manufacturer,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations)
    {
        foreach (var hardware in manufacturer.Descendants()
                     .Where(e => e.Name.LocalName == "Hardware" && Attr(e, "Id") is not null))
        {
            var hardwareId = Attr(hardware, "Id")!;
            var programIds = new List<string>();
            var hardware2ProgramIds = new List<string>();
            foreach (var link in hardware.Descendants().Where(e => e.Name.LocalName == "Hardware2Program"))
            {
                var linkId = Attr(link, "Id");
                var references = link.Descendants()
                    .Where(e => e.Name.LocalName == "ApplicationProgramRef")
                    .Select(e => Attr(e, "RefId"))
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Select(r => r!)
                    .ToList();
                if (linkId is not null) _hardware2Programs[linkId] = references;
                hardware2ProgramIds.Add(linkId ?? "");
                programIds.AddRange(references);
            }

            foreach (var element in hardware.Descendants()
                         .Where(e => e.Name.LocalName == "Product" && Attr(e, "Id") is not null))
            {
                var id = Attr(element, "Id")!;
                var product = new CatalogProduct
                {
                    Id = id,
                    Name = Text(translations, id, "Text", Attr(element, "Text")),
                    OrderNumber = Attr(element, "OrderNumber"),
                    HardwareRefId = hardwareId,
                    Description = Text(translations, id, "VisibleDescription", Attr(element, "VisibleDescription")),
                };
                foreach (var programId in programIds)
                    if (!product.ProgramRefIds.Contains(programId)) product.ProgramRefIds.Add(programId);
                foreach (var linkId in hardware2ProgramIds)
                    if (linkId.Length > 0 && !product.Hardware2ProgramIds.Contains(linkId))
                        product.Hardware2ProgramIds.Add(linkId);
                Products.Add(product);
                _productsById.TryAdd(product.Id, product);
            }
        }
    }

    private void ReadCatalog(XElement manufacturer,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations)
    {
        var roots = manufacturer.Descendants()
            .Where(e => e.Name.LocalName == "CatalogSection" && e.Parent?.Name.LocalName != "CatalogSection");
        foreach (var section in roots) CatalogSections.Add(ReadSection(section, "", translations, 0));
    }

    private CatalogSection ReadSection(XElement element, string parentPath,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations, int depth)
    {
        if (depth > MaximumSectionDepth)
            throw new InvalidDataException(
                $"catalogue section nesting exceeds {MaximumSectionDepth} levels.");
        var id = Attr(element, "Id") ?? "";
        var section = new CatalogSection
        {
            Id = id,
            Name = Text(translations, id, "Name", Attr(element, "Name")),
            Number = Int(element, "Number"),
        };
        var path = parentPath.Length == 0 ? section.Name : parentPath + "/" + section.Name;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "CatalogSection":
                    section.Sections.Add(ReadSection(child, path, translations, depth + 1));
                    break;
                case "CatalogItem":
                    section.Items.Add(ReadItem(child, path, translations));
                    break;
            }
        }
        return section;
    }

    private CatalogItem ReadItem(XElement element, string path,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations)
    {
        var id = Attr(element, "Id") ?? "";
        var item = new CatalogItem
        {
            Id = id,
            Name = Text(translations, id, "Name", Attr(element, "Name")),
            ProductRefId = Attr(element, "ProductRefId"),
            Description = Text(translations, id, "VisibleDescription", Attr(element, "VisibleDescription")),
            Hardware2ProgramRefId = Attr(element, "Hardware2ProgramRefId"),
        };
        if (item.ProductRefId is { } productId && FindProduct(productId) is { } product)
        {
            if (!product.CatalogPaths.Contains(path)) product.CatalogPaths.Add(path);
            if (item.Hardware2ProgramRefId is { } hardware2Program &&
                !product.EntryRefIds.Contains(hardware2Program))
                product.EntryRefIds.Add(hardware2Program);
        }
        return item;
    }

    /// <summary>Reads program metadata only; the KSXML model stays in the XML until requested.</summary>
    private void AddProgramMetadata(XElement scope,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations, CatalogSource source)
    {
        foreach (var element in scope.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "ApplicationProgram" && Attr(e, "Id") is not null))
        {
            var id = Attr(element, "Id")!;
            if (_programsById.ContainsKey(id)) continue;

            var program = new CatalogApplicationProgram
            {
                Id = id,
                Name = Text(translations, id, "Name", Attr(element, "Name")),
                ApplicationNumber = Attr(element, "ApplicationNumber"),
                ApplicationVersion = Attr(element, "ApplicationVersion"),
                SchemaVersion = Attr(element, "SchemaVersion") ?? Attr(element, "CreatedFromLegacySchemaVersion"),
                Broken = IsTrue(Attr(element, "Broken")),
                RegistrationStatus = Attr(element, "RegistrationStatus"),
                MinEtsVersion = Attr(element, "MinEtsVersion"),
                MaskVersion = Attr(element, "MaskVersion") ?? Attr(element, "ManagementModel"),
                DownloadInfoIncomplete = IsTrue(Attr(element, "DownloadInfoIncomplete")),
                Source = new ProgramSource(source.Entry, source.FilePath),
            };
            ReadExtensions(program, element);
            ReadParameterPlugins(program, element);

            foreach (var comObject in element.Descendants().Where(e => e.Name.LocalName == "ComObject"))
            {
                var comObjectId = Attr(comObject, "Id") ?? "";
                var text = Text(translations, comObjectId, "Text", Attr(comObject, "Text"));
                var functionText = Text(translations, comObjectId, "FunctionText", Attr(comObject, "FunctionText"));
                program.ComObjects.Add(new ComObject
                {
                    Id = comObjectId,
                    Name = text.Length > 0 ? text : Attr(comObject, "Name") ?? comObjectId,
                    Number = Int(comObject, "Number"),
                    SizeInBit = int.TryParse(Attr(comObject, "SizeInBit"), out var size)
                        ? size
                        : Ksxml.ParseObjectSize(Attr(comObject, "ObjectSize")),
                    DatapointType = Attr(comObject, "DatapointType"),
                });
                program.ComObjectDisplay[comObjectId] = (text, functionText);
            }

            program.SearchText = CatalogSearchText.Program(program);
            program.NormalizedDatapointTypes = CatalogSearchText.DatapointTypes(program.ComObjects);
            Programs.Add(program);
            _programsById[program.Id] = program;
        }
    }

    /// <summary>
    /// Reads Static/Extension plug-in attributes (EtsUiPlugin="{GUID}" or
    /// an assembly-qualified type name); an empty value means "not configured". Elements with
    /// Ets* names are accepted too, so older/newer schema shapes do not silently disappear.
    /// </summary>
    private static void ReadExtensions(CatalogApplicationProgram program, XElement element)
    {
        foreach (var extension in element.Descendants().Where(e => e.Name.LocalName == "Extension"))
        {
            foreach (var attribute in extension.Attributes())
                if (IsEtsName(attribute.Name.LocalName) && !string.IsNullOrWhiteSpace(attribute.Value))
                    program.AddExtension(attribute.Name.LocalName);
            foreach (var child in extension.Elements())
                if (IsEtsName(child.Name.LocalName))
                    program.AddExtension(child.Name.LocalName);
            if (IsTrue((string?)extension.Attribute("RequiresExternalSoftware")))
                program.RequiresExternalSoftware = true;
        }
    }

    private static void ReadParameterPlugins(CatalogApplicationProgram program, XElement element)
    {
        foreach (var declaration in element.Descendants()
                     .Where(e => e.Name.LocalName is "ParameterRef" or "ParameterInstanceRef"))
        {
            var plugin = (string?)declaration.Attribute("Plugin");
            if (string.IsNullOrWhiteSpace(plugin)) continue;
            // A ParameterRef references a Parameter by RefId; ParameterInstanceRef only has RefId.
            foreach (var key in new[] { (string?)declaration.Attribute("Id"), (string?)declaration.Attribute("RefId") })
                if (!string.IsNullOrWhiteSpace(key)) program.SetParameterPlugin(key, plugin);
        }
    }

    private static bool IsEtsName(string name) => name.StartsWith("Ets", StringComparison.Ordinal);

    private static bool IsTrue(string? value) =>
        value is "1" || bool.TryParse(value, out var flag) && flag;

    private static string Text(Dictionary<string, Dictionary<string, Dictionary<string, string>>> translations,
        string refId, string attribute, string? fallback)
    {
        Dictionary<string, string>? byLanguage = null;
        foreach (var (language, byRef) in translations)
        {
            if (!byRef.TryGetValue(refId, out var byAttribute)) continue;
            if (!byAttribute.TryGetValue(attribute, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            (byLanguage ??= new(StringComparer.OrdinalIgnoreCase))[language] = text;
        }
        return CatalogText.Pick(byLanguage, fallback);
    }

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    private static int Int(XElement element, string name) =>
        int.TryParse(Attr(element, name), out var value) ? value : 0;

    /// <summary>Opens one archive XML entry, rejecting oversized declared or actual content.</summary>
    private static Stream OpenXmlEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumXmlEntryBytes || entry.CompressedLength > MaximumXmlEntryBytes)
            throw new InvalidDataException(
                $"catalogue XML part '{entry.FullName}' exceeds the supported size.");
        return new BoundedReadStream(entry.Open(), MaximumXmlEntryBytes, entry.FullName);
    }

    private static Stream OpenXmlFile(string file)
    {
        var length = new FileInfo(file).Length;
        if (length > MaximumXmlEntryBytes)
            throw new InvalidDataException($"catalogue XML '{IOPath.GetFileName(file)}' exceeds the supported size.");
        return new BoundedReadStream(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan),
            MaximumXmlEntryBytes, file);
    }

    private static XDocument LoadXmlFile(string file)
    {
        using var stream = OpenXmlFile(file);
        return LoadXml(stream);
    }

    /// <summary>Single XML entry point: no DTD, bounded entity expansion and bounded document size.</summary>
    private static XDocument LoadXml(Stream stream)
    {
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1_000_000,
            MaxCharactersInDocument = MaximumXmlEntryBytes,
        });
        return XDocument.Load(reader);
    }

    private static bool LooksLikeXml(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[64];
            var read = stream.Read(buffer, 0, buffer.Length);
            var offset = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            while (offset < read && buffer[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
            return offset < read && buffer[offset] == (byte)'<';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsImage(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);

    private static string FindCatalogFile(string directory)
    {
        var named = Directory.EnumerateFiles(directory)
            .FirstOrDefault(f => IOPath.GetFileName(f).Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase));
        if (named is not null) return named;

        var archive = Directory.EnumerateFiles(directory, "*.knxprod").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (archive is not null) return archive;

        return Directory.EnumerateFiles(directory, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new InvalidDataException($"no knxprod.xml or *.knxprod in '{directory}'.");
    }

    private static bool HasSplitBase(ZipArchive zip) => zip.Entries.Any(e =>
        e.Name.Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase) ||
        e.Name.Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase));

    private static ZipArchiveEntry? PickMainEntry(ZipArchive zip)
    {
        var xml = zip.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
        var named = xml.FirstOrDefault(e => e.Name.Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase));
        if (named is not null) return named;

        // Archives with a differently named main part: it must carry catalogue/hardware
        // content, which excludes knx_master.xml and single application-program parts.
        foreach (var candidate in xml
                     .Where(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\'))
                     .Where(e => !e.Name.Equals("knx_master.xml", StringComparison.OrdinalIgnoreCase))
                     .Where(e => !IOPath.GetFileNameWithoutExtension(e.Name)
                         .Contains("_A-", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.Length))
            if (ContainsCatalogueContent(candidate)) return candidate;

        return null;
    }

    /// <summary>Cheap streaming check: does this part hold a Manufacturer with Catalog/Hardware?</summary>
    private static bool ContainsCatalogueContent(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = OpenXmlEntry(entry);
            using var reader = System.Xml.XmlReader.Create(stream,
                new System.Xml.XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            var sawManufacturer = false;
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "Manufacturer":
                        sawManufacturer = true;
                        break;
                    case "ApplicationPrograms" or "ApplicationProgram":
                        // A lone device part: no catalogue/hardware content follows.
                        return false;
                    case "Catalog" or "CatalogSection" or "Hardware":
                        return sawManufacturer;
                }
            }
            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}

/// <summary>Document location that owns application programs (archive entry or loose XML file).</summary>
internal readonly record struct CatalogSource(string? FilePath, string? Entry);

/// <summary>Reads a stream but fails as soon as it produces more than a fixed number of bytes.</summary>
internal sealed class BoundedReadStream(Stream inner, long limit, string what) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Track(inner.Read(buffer));

    public override int ReadByte()
    {
        var value = inner.ReadByte();
        if (value >= 0) Track(1);
        return value;
    }

    private int Track(int read)
    {
        _read += read;
        if (_read > limit)
            throw new InvalidDataException($"'{what}' exceeds the supported size of {limit} bytes.");
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
