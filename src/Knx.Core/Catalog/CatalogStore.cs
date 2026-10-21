using IOPath = System.IO.Path;
using System.Security.Cryptography;

namespace Knx.Catalog;

/// <summary>One file that could not be parsed; LoadAll keeps going and reports it here.</summary>
public sealed record CatalogLoadError(string File, string Message);

/// <summary>Progress of <see cref="CatalogStore.LoadAll"/> in library files.</summary>
public sealed record CatalogLoadProgress(int Loaded, int Total, string File);

/// <summary>Summary of one catalogue import, including gating facts.</summary>
public sealed record CatalogImportReport(string File, string CatalogName, int Products, int Sections, int Programs,
    bool HasBrokenProgram, bool HasDcaProgram, bool HasExternalSoftware);

/// <summary>
/// Local catalogue library in %APPDATA%\KnxClone\catalogs (or an explicit directory):
/// import copies a .knxprod into the library, LoadAll parses every entry, Remove deletes one.
/// </summary>
public sealed class CatalogStore
{
    public CatalogStore(string? directory = null) => Directory = directory ?? DefaultDirectory;

    public string Directory { get; }

    /// <summary>Summary of the last <see cref="Import"/>; null before the first import.</summary>
    public CatalogImportReport? LastImportReport { get; private set; }

    public static string DefaultDirectory =>
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppSettings.AppFolderName, "catalogs");

    /// <summary>Files that failed to load during the last <see cref="LoadAll"/>.</summary>
    public List<CatalogLoadError> LoadErrors { get; } = [];

    /// <summary>
    /// Copies a catalogue into the library, keyed by its content digest, and returns the parsed copy.
    /// </summary>
    public KnxProductCatalog Import(string path)
    {
        var catalog = KnxProductCatalog.Open(path);
        System.IO.Directory.CreateDirectory(Directory);
        var target = IOPath.Combine(Directory, LibraryFileName(catalog));
        if (!string.Equals(IOPath.GetFullPath(catalog.Path), IOPath.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(catalog.Path, target, overwrite: true);
        catalog.Relocate(target);
        LastImportReport = new(target, catalog.Name, catalog.Products.Count, catalog.CatalogSections.Count,
            catalog.Programs.Count, catalog.Programs.Any(p => p.Broken || p.DownloadInfoIncomplete),
            catalog.Programs.Any(p => p.RequiresDca), catalog.Programs.Any(p => p.RequiresExternalSoftware));
        return catalog;
    }

    /// <summary>Parses every catalogue in the library; broken files land in <see cref="LoadErrors"/>.</summary>
    /// <param name="progress">Optional per-file progress, reported on the calling thread.</param>
    public List<KnxProductCatalog> LoadAll(IProgress<CatalogLoadProgress>? progress = null)
    {
        LoadErrors.Clear();
        var catalogs = new List<KnxProductCatalog>();
        if (!System.IO.Directory.Exists(Directory)) return catalogs;

        var files = System.IO.Directory.EnumerateFiles(Directory)
            .Where(IsCatalogFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            try
            {
                catalogs.Add(KnxProductCatalog.Open(file));
            }
            catch (Exception ex)
            {
                LoadErrors.Add(new CatalogLoadError(file, ex.Message));
            }
            progress?.Report(new CatalogLoadProgress(index + 1, files.Count, file));
        }
        return catalogs;
    }

    /// <summary>Deletes one catalogue file from the library; refuses anything outside it.</summary>
    public bool Remove(string catalogPath)
    {
        var full = IOPath.GetFullPath(catalogPath);
        if (!string.Equals(IOPath.GetDirectoryName(full),
                IOPath.TrimEndingDirectorySeparator(IOPath.GetFullPath(Directory)), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(full)) return false;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
        File.Delete(full);
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
