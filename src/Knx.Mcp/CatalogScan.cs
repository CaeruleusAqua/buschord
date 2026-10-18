using Knx.Catalog;

namespace Knx.Mcp;

/// <summary>
/// `--scan-catalogs=&lt;dir&gt;`: opens every *.knxprod below a directory and reports what the
/// reader found. Used to validate the parser against real manufacturer files.
/// </summary>
internal static class CatalogScan
{
    public static int Run(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"scan: '{directory}' is not a directory");
            return 2;
        }

        var files = Directory.EnumerateFiles(directory, "*.knxprod", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var failures = 0;

        foreach (var file in files)
        {
            try
            {
                var catalog = KnxProductCatalog.Open(file);
                var resolved = catalog.Products.Count(product => catalog.FindProgramFor(product) is not null);
                Console.WriteLine(
                    $"  OK   {Path.GetFileName(file),-62} " +
                    $"{catalog.Manufacturer?.Name ?? catalog.Manufacturer?.Id,-24} products={catalog.Products.Count,3} " +
                    $"programs={resolved,3} sections={catalog.CatalogSections.Count,2} " +
                    $"images={catalog.Images.Count,2}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  FAIL {Path.GetFileName(file),-62} {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\n{files.Count} Kataloge geprüft, {failures} Fehler");
        return failures == 0 ? 0 : 1;
    }
}
