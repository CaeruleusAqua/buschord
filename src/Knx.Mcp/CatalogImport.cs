using Knx.Catalog;

namespace Knx.Mcp;

/// <summary>
/// `--import-catalogs=&lt;dir&gt;`: imports every .knxprod and vendor ZIP below a directory
/// into the local library. Re-imports are idempotent because the store keys files by
/// catalogue identity, so a download folder can be imported repeatedly.
/// </summary>
internal static class CatalogImport
{
    public static int Run(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"import: '{directory}' is not a directory");
            return 2;
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var store = new CatalogStore();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(store.Directory))
            foreach (var path in Directory.EnumerateFiles(store.Directory, "*.knxprod"))
                existing.Add(Path.GetFileName(path)!);
        int imported = 0, unchanged = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                var catalog = store.Import(file);
                imported++;
                if (!existing.Add(Path.GetFileName(catalog.Path)!)) unchanged++;
                Console.WriteLine(
                    $"  OK   {Path.GetFileName(file),-60} {catalog.Manufacturer?.Name ?? catalog.Manufacturer?.Id,-24} " +
                    $"products={catalog.Products.Count,4}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL {Path.GetFileName(file),-60} {ex.GetType().Name}: {ex.Message}");
            }
        }

        var catalogs = store.LoadAll();
        Console.WriteLine(
            $"\n{files.Count} Dateien, {imported} importiert ({unchanged} bereits vorhanden), {failed} Fehler");
        Console.WriteLine($"Bibliothek: {store.Directory} ({catalogs.Count} Kataloge, " +
                          $"{catalogs.Sum(c => c.Products.Count)} Produkte)");
        return failed == 0 ? 0 : 1;
    }
}

