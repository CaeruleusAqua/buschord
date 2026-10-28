using System.Diagnostics;
using System.IO.Compression;
using IOPath = System.IO.Path;
using System.Security.Cryptography;

namespace Knx.Catalog;

/// <summary>One file that could not be parsed; LoadAll keeps going and reports it here.</summary>
public sealed record CatalogLoadError(string File, string Message);

/// <summary>
/// Progress of <see cref="CatalogStore.LoadAll"/> in library files, plus the application-program
/// parts of the file currently being read (both zero while a file has no separate parts).
/// </summary>
public sealed record CatalogLoadProgress(int Loaded, int Total, string File, int Part = 0, int Parts = 0);

/// <summary>Summary of one catalogue import, including gating facts.</summary>
public sealed record CatalogImportReport(string File, string CatalogName, int Products, int Sections, int Programs,
    bool HasBrokenProgram, bool HasDcaProgram, bool HasExternalSoftware)
{
    /// <summary>
    /// The container carries a *.signature entry. Presence is surfaced only; the signature is
    /// not cryptographically verified by this reader.
    /// </summary>
    public bool HasSignatureFile { get; init; }

    /// <summary>The import replaced an existing library entry with the same identity.</summary>
    public bool ReplacedExisting { get; init; }
}

/// <summary>What the last <see cref="CatalogStore.LoadAll"/> read from cache and from XML.</summary>
public sealed record CatalogIndexStatistics(int Files, int IndexHits, int IndexMisses, int XmlCatalogFiles,
    int XmlProgramParts, int Errors, TimeSpan Elapsed);

public enum CatalogPreservingImportDisposition { Imported, IdenticalSkipped, ConflictKeptExisting }
public sealed record CatalogPreservingImportResult(CatalogPreservingImportDisposition Disposition,
    string LibraryPath, KnxProductCatalog? Catalog, string? CacheWarning = null);

/// <summary>
/// Local catalogue library in %APPDATA%\BusChord\catalogs (or an explicit directory):
/// import copies a .knxprod into the library, LoadAll reads it through the metadata index
/// in %LOCALAPPDATA%\BusChord\cache, Remove deletes one.
/// </summary>
public sealed class CatalogStore
{
    private readonly CatalogIndexStore _index;

    public CatalogStore(string? directory = null, string? indexDirectory = null)
    {
        Directory = directory ?? DefaultDirectory;
        // An explicit library (tests, portable setups) keeps its derived cache beside it; the
        // roaming default library uses the machine-local cache as designed.
        _index = new CatalogIndexStore(indexDirectory ?? (directory is null
            ? CatalogIndexStore.DefaultDirectory
            : IOPath.Combine(Directory, ".catalog-index")));
    }

    public string Directory { get; }

    /// <summary>Metadata cache used for this library.</summary>
    public CatalogIndexStore Index => _index;

    /// <summary>Summary of the last <see cref="Import"/>; null before the first import.</summary>
    public CatalogImportReport? LastImportReport { get; private set; }

    /// <summary>Cache/XML work of the last <see cref="LoadAll"/>; null before the first load.</summary>
    public CatalogIndexStatistics? LastIndexStatistics { get; private set; }

    public static string DefaultDirectory =>
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppSettings.AppFolderName, "catalogs");

    /// <summary>Files that failed to load during the last <see cref="LoadAll"/>.</summary>
    public List<CatalogLoadError> LoadErrors { get; } = [];

    /// <summary>
    /// Copies a catalogue into the library, keyed by its content digest, and returns the parsed copy.
    /// The metadata cache is written in the same step; split-archive program metadata is filled by
    /// the next <see cref="LoadAll"/> in the background.
    /// </summary>
    public KnxProductCatalog Import(string path)
    {
        var catalog = KnxProductCatalog.Open(path, _index.Directory);
        System.IO.Directory.CreateDirectory(Directory);
        var target = IOPath.Combine(Directory, LibraryFileName(catalog));
        var replaced = false;
        if (!string.Equals(IOPath.GetFullPath(catalog.Path), IOPath.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            // Copy beside the target, then replace it in one move: a failed import never
            // destroys the previously imported catalogue.
            replaced = File.Exists(target);
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(catalog.Path, temp, overwrite: false);
                if (new FileInfo(temp).Length != new FileInfo(catalog.Path).Length)
                    throw new IOException("catalogue copy is incomplete");
                File.Move(temp, target, overwrite: true);
                temp = null;
            }
            finally
            {
                if (temp is not null)
                    try { File.Delete(temp); } catch (Exception) { }
            }
        }
        catalog.Relocate(target);
        LastImportReport = new(target, catalog.Name, catalog.Products.Count, catalog.CatalogSections.Count,
            catalog.Programs.Count, catalog.Programs.Any(p => p.Broken || p.DownloadInfoIncomplete),
            catalog.Programs.Any(p => p.RequiresDca), catalog.Programs.Any(p => p.RequiresExternalSoftware))
        {
            HasSignatureFile = HasSignatureEntry(catalog),
            ReplacedExisting = replaced,
        };
        _index.Save(catalog);
        return catalog;
    }

    /// <summary>
    /// Imports a validated acquisition candidate without replacing an installed identity.
    /// Identical bytes skip; changed bytes under the same logical identity preserve the old file.
    /// A create-only final move protects the library when another importer wins the race.
    /// </summary>
    public CatalogPreservingImportResult ImportPreservingExisting(string path, CancellationToken cancellationToken = default,
        string? expectedManufacturer = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = KnxProductCatalog.Open(path, _index.Directory);
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedManufacturer is not null && catalog.Manufacturer?.Id != expectedManufacturer)
            throw new InvalidDataException("The parsed catalogue manufacturer does not match the acquisition source.");
        System.IO.Directory.CreateDirectory(Directory);
        var target = IOPath.Combine(Directory, LibraryFileName(catalog));
        var incomingHash = Hash(path);
        CatalogPreservingImportResult Existing() => new(
            incomingHash.SequenceEqual(Hash(target)) ? CatalogPreservingImportDisposition.IdenticalSkipped :
                CatalogPreservingImportDisposition.ConflictKeptExisting, target, null);
        if (File.Exists(target)) return Existing();
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!incomingHash.SequenceEqual(Hash(temp))) throw new IOException("Catalogue staging copy is incomplete.");
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temp, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { return Existing(); }
            catalog.Relocate(target);
            LastImportReport = new(target, catalog.Name, catalog.Products.Count, catalog.CatalogSections.Count,
                catalog.Programs.Count, catalog.Programs.Any(p => p.Broken || p.DownloadInfoIncomplete),
                catalog.Programs.Any(p => p.RequiresDca), catalog.Programs.Any(p => p.RequiresExternalSoftware))
            {
                HasSignatureFile = HasSignatureEntry(catalog), ReplacedExisting = false,
            };
            string? cacheWarning = null;
            try { _index.Save(catalog); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
            {
                cacheWarning = "Catalogue imported; its derived metadata cache could not be saved and can be rebuilt.";
            }
            return new(CatalogPreservingImportDisposition.Imported, target, catalog, cacheWarning);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        byte[] Hash(string file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return hash.GetHashAndReset();
        }
    }

    /// <summary>
    /// True when the container (or, for a nested vendor ZIP, the extracted inner archive) carries
    /// a *.signature entry. The entry is only surfaced, never parsed or verified.
    /// </summary>
    private static bool HasSignatureEntry(KnxProductCatalog catalog)
    {
        foreach (var path in new[] { catalog.BackingArchivePath, catalog.Path })
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.Entries.Any(entry => entry.Name.EndsWith(".signature", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (Exception)
            {
                // Loose XML catalogues are no archives; there is nothing to check.
            }
        }
        return false;
    }

    /// <summary>Parses every catalogue in the library; broken files land in <see cref="LoadErrors"/>.</summary>
    /// <param name="progress">Optional per-file progress, reported on the calling thread.</param>
    public List<KnxProductCatalog> LoadAll(IProgress<CatalogLoadProgress>? progress = null) =>
        LoadAll(progress, null, CancellationToken.None);

    /// <summary>
    /// Reads every catalogue through the metadata cache (or parses XML on a miss). A catalogue is
    /// reported through <paramref name="catalogReady"/> as soon as its product metadata is usable,
    /// and again after its program metadata was indexed, so callers can publish progressive
    /// snapshots. XML work runs on a bounded pool, so a few very large archives no longer block
    /// the rest of the library, and each file reports its program parts while they are read.
    /// Cancellation leaves no half-written cache entry behind.
    /// </summary>
    public List<KnxProductCatalog> LoadAll(IProgress<CatalogLoadProgress>? progress,
        Action<KnxProductCatalog>? catalogReady, CancellationToken cancellationToken = default)
    {
        LoadErrors.Clear();
        var catalogs = new List<KnxProductCatalog>();
        if (!System.IO.Directory.Exists(Directory))
        {
            LastIndexStatistics = new CatalogIndexStatistics(0, 0, 0, 0, 0, 0, TimeSpan.Zero);
            return catalogs;
        }

        var files = System.IO.Directory.EnumerateFiles(Directory)
            .Where(IsCatalogFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var results = new KnxProductCatalog?[files.Count];
        var errors = new List<CatalogLoadError>();
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errorGate = new object();
        var publishGate = new object();
        var loaded = 0;
        var hits = 0;
        var misses = 0;
        var xmlFiles = 0;
        var xmlParts = 0;
        var watch = Stopwatch.StartNew();

        void Ready(KnxProductCatalog catalog)
        {
            if (catalogReady is null) return;
            // Parallel index reads may publish out of order; the library sorts its snapshot.
            lock (publishGate) catalogReady(catalog);
        }

        void Report(string file) =>
            progress?.Report(new CatalogLoadProgress(Interlocked.Increment(ref loaded), files.Count, file));

        // Sub-file progress: the file keeps its place in the file counter while its parts advance.
        void ReportPart(string file, int part, int parts) => progress?.Report(
            new CatalogLoadProgress(Volatile.Read(ref loaded), files.Count, file, part, parts));

        void Fail(string file, Exception ex)
        {
            lock (errorGate)
            {
                errors.Add(new CatalogLoadError(file, ex.Message));
                failed.Add(file);
            }
        }

        bool Failed(string file)
        {
            lock (errorGate) return failed.Contains(file);
        }

        // Phase 1: warm metadata, bounded parallel. A hit reads JSON only, never catalogue XML.
        Parallel.For(0, files.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken,
        }, index =>
        {
            var file = files[index];
            try
            {
                if (!_index.TryLoad(file, out var cached) || cached is null) return;
                Interlocked.Increment(ref hits);
                results[index] = cached;
                Ready(cached);
                // Complete indexes are done; incomplete ones are enriched below.
                if (cached.MetadataComplete) Report(file);
            }
            catch (Exception ex)
            {
                Fail(file, ex);
                Report(file);
            }
        });

        // Phase 2: XML work for misses and for caches written before program metadata existed.
        // Files are independent; a handful of huge archives overlap instead of blocking the run.
        Parallel.For(0, files.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 4),
            CancellationToken = cancellationToken,
        }, index =>
        {
            var file = files[index];
            if (results[index] is { } cached)
            {
                if (cached.MetadataComplete) return;
                try
                {
                    var parts = cached.ProgramPartCount;
                    var read = cached.LoadProgramMetadata(cancellationToken,
                        new PartProgress(file, parts, ReportPart));
                    Interlocked.Add(ref xmlParts, read);
                    _index.Save(cached);
                    if (read > 0) Ready(cached);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Fail(file, ex);
                }
                Report(file);
                return;
            }
            if (Failed(file)) return;

            try
            {
                Interlocked.Increment(ref misses);
                var catalog = KnxProductCatalog.Open(file, _index.Directory);
                Interlocked.Increment(ref xmlFiles);
                results[index] = catalog;
                Ready(catalog);
                if (!catalog.MetadataComplete)
                {
                    var parts = catalog.ProgramPartCount;
                    var read = catalog.LoadProgramMetadata(cancellationToken,
                        new PartProgress(file, parts, ReportPart));
                    Interlocked.Add(ref xmlParts, read);
                    if (read > 0) Ready(catalog);
                }
                _index.Save(catalog);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fail(file, ex);
            }
            Report(file);
        });

        watch.Stop();
        LoadErrors.AddRange(errors.OrderBy(error => error.File, StringComparer.OrdinalIgnoreCase));
        catalogs.AddRange(results.OfType<KnxProductCatalog>()
            .OrderBy(catalog => catalog.Path, StringComparer.OrdinalIgnoreCase));
        LastIndexStatistics = new CatalogIndexStatistics(files.Count, hits, misses, xmlFiles, xmlParts,
            LoadErrors.Count, watch.Elapsed);
        return catalogs;
    }

    /// <summary>Forwards the part counter of one file to the library progress sink.</summary>
    private sealed class PartProgress(string file, int parts, Action<string, int, int> report) : IProgress<int>
    {
        public void Report(int value) => report(file, value, parts);
    }

    /// <summary>Deletes one catalogue file from the library and its cache; refuses anything outside it.</summary>
    public bool Remove(string catalogPath)
    {
        var full = IOPath.GetFullPath(catalogPath);
        if (!string.Equals(IOPath.GetDirectoryName(full),
                IOPath.TrimEndingDirectorySeparator(IOPath.GetFullPath(Directory)), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(full)) return false;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
        File.Delete(full);
        _index.Remove(full);
        return true;
    }

    private static string LibraryFileName(KnxProductCatalog catalog)
    {
        var manufacturer = catalog.Manufacturer?.Id;
        if (string.IsNullOrWhiteSpace(manufacturer)) manufacturer = "unknown";
        // Identity, not container bytes: the same catalogue imported as .knxprod and as a
        // vendor ZIP wrapper must land on one library entry instead of duplicating products.
        // Product refs are parsed eagerly, so split catalogues keep distinct applications
        // even though their application programs are loaded lazily.
        var identity = string.Join('|',
            manufacturer,
            string.Join(',', catalog.Products
                .Select(p => string.Join(':',
                    p.Id, string.Join(',', p.EntryRefIds), string.Join(',', p.ProgramRefIds),
                    string.Join(',', p.Hardware2ProgramIds)))
                .Order(StringComparer.Ordinal)),
            string.Join(',', catalog.Programs.Select(p => p.Id + "@" + p.ApplicationVersion).Order(StringComparer.Ordinal)));
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Sanitize(manufacturer) + "_" + digest + ".knxprod";

        static string Sanitize(string value) =>
            string.Join('_', value.Split(IOPath.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
                .TrimEnd('-');
    }

    private static bool IsCatalogFile(string file) =>
        file.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase) ||
        // Vendor downloads are frequently a ZIP around the .knxprod (Open unwraps one level).
        file.EndsWith(".knxprod.zip", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        IOPath.GetFileName(file).Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase);
}
