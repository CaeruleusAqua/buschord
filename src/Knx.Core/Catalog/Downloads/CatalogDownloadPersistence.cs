using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knx.Core.Catalog.Downloads;

internal static class CatalogDownloadPersistence
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16L * 1024 * 1024) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Json), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { CatalogHttpTransport.TryDelete(temp); }
    }

    internal static void CleanJobDirectory(string directory, string jobsRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobsRoot)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(target), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The catalogue job directory is outside its staging root.");
        if (!Directory.Exists(target)) return;
        // Staging paths use generated names, never ZIP paths. Refuse links before recursive cleanup.
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(target, "*", SearchOption.TopDirectoryOnly)
                .Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) return;
        try { Directory.Delete(target, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
