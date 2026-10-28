using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Velopack;

namespace Knx.Updates;

public sealed record InstalledCodeFile(string Path, long Bytes, string Sha256);
public sealed record InstalledCodeManifest(int Schema, string AppId, string Version, string Rid, string Channel,
    IReadOnlyList<InstalledCodeFile> Files, IReadOnlyList<string> Directories, string SelfHash);
public sealed record InstalledCodeCheck(bool Safe, string Detail);

/// <summary>Separates the exact published code tree from user files before Windows replaces current.</summary>
public static class InstalledCodeInventory
{
    public const string FileName = "buschord-code-inventory.json";
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    public static void Create(string directory, string appId, string version, string rid, string channel)
    {
        directory = Path.GetFullPath(directory);
        var files = new List<InstalledCodeFile>(); var directories = new HashSet<string>(Names);
        foreach (var path in Enumerate(directory))
        {
            var relative = Relative(directory, path);
            if (relative == FileName) continue;
            if (relative == "sq.version") throw new IOException("SDK metadata must not be part of the publish input.");
            if (Directory.Exists(path)) continue;
            using var input = File.OpenRead(path);
            files.Add(new(relative, input.Length, Convert.ToHexString(SHA256.HashData(input))));
            for (var parent = Path.GetDirectoryName(relative)?.Replace('\\', '/'); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent)?.Replace('\\', '/'))
                directories.Add(parent);
        }
        var manifest = new InstalledCodeManifest(1, appId, version, rid, channel, files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            directories.Order(StringComparer.Ordinal).ToArray(), "");
        manifest = manifest with { SelfHash = Digest(manifest) };
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(manifest, UpdateManifests.Json));
    }
    public static InstalledCodeCheck Check(string directory, string appId, string installedVersion, string rid)
    {
        try
        {
            var inventoryPath = Path.Combine(directory, FileName);
            if (!File.Exists(inventoryPath) || new FileInfo(inventoryPath).Length > 4 * 1024 * 1024) return new(false, "Missing or excessive code inventory.");
            var manifest = JsonSerializer.Deserialize<InstalledCodeManifest>(File.ReadAllText(inventoryPath), UpdateManifests.Json)
                ?? throw new InvalidDataException("Missing code inventory.");
            if (manifest.Schema != 1 || manifest.AppId != appId || manifest.Rid != rid ||
                SemanticVersion.Parse(manifest.Version) != SemanticVersion.Parse(installedVersion) ||
                manifest.Channel is null || !manifest.Channel.StartsWith(rid + "-", StringComparison.Ordinal) ||
                manifest.Channel[(rid.Length + 1)..] is not ("stable" or "preview") ||
                manifest.Files is null || manifest.Files.Count is < 1 or > 50000 || manifest.Directories is null || manifest.Directories.Count > 10000 ||
                manifest.SelfHash != Digest(manifest with { SelfHash = "" })) return new(false, "The code inventory scope or checksum changed.");
            var expected = new Dictionary<string, InstalledCodeFile>(Names);
            foreach (var file in manifest.Files)
            {
                if (file is null || !SafeRelative(file.Path) || file.Path is FileName or "sq.version" || file.Bytes < 0 ||
                    file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit) || !expected.TryAdd(file.Path, file))
                    return new(false, "Invalid code inventory entry.");
            }
            var expectedDirectories = new HashSet<string>(Names);
            foreach (var path in manifest.Directories)
                if (!SafeRelative(path) || !expectedDirectories.Add(path)) return new(false, "Invalid code directory inventory.");
            ValidateSdkVersion(Path.Combine(directory, "sq.version"), manifest);
            var seen = new HashSet<string>(Names);
            foreach (var path in Enumerate(directory))
            {
                var relative = Relative(directory, path);
                if (Directory.Exists(path))
                { if (!expectedDirectories.Contains(relative)) return new(false, "Unexpected directory: " + relative); continue; }
                // These are the only non-payload files: the inventory's self checksum/scope was checked,
                // and the SDK's sq.version was checked against that scope, with XML external access prohibited.
                if (relative is FileName or "sq.version") continue;
                if (!expected.TryGetValue(relative, out var file)) return new(false, "Unexpected file: " + relative);
                using var input = File.OpenRead(path);
                if (input.Length != file.Bytes || !string.Equals(Convert.ToHexString(SHA256.HashData(input)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, "Modified code: " + relative);
                seen.Add(relative);
            }
            return seen.Count == expected.Count ? new(true, "") : new(false, "An inventoried code file is missing.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or XmlException or ArgumentException or InvalidOperationException or FormatException)
        { return new(false, "The installed code tree cannot be verified: " + error.GetType().Name); }
    }
    private static string Digest(InstalledCodeManifest manifest) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateManifests.Json)));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool SafeRelative(string? value) => !string.IsNullOrEmpty(value) && value.Length < 4096 && !value.Contains('\\') && !value.Contains(':') &&
        !value.StartsWith('/') && !value.Any(c => c < 32) && value.Split('/').All(part => part is not ("" or "." or "..") && part == part.TrimEnd(' ', '.'));
    private static IEnumerable<string> Enumerate(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installation root.");
        var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 60000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked or excessive installation content.");
                yield return path;
                if (Directory.Exists(path)) pending.Push(path);
            }
    }
    private static void ValidateSdkVersion(string path, InstalledCodeManifest manifest)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) throw new IOException("Missing SDK metadata.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 };
        using (var depth = XmlReader.Create(path, settings)) while (depth.Read()) if (depth.Depth > 32) throw new XmlException("Excessive SDK metadata depth.");
        using var reader = XmlReader.Create(path, settings); var xml = XDocument.Load(reader);
        var metadata = xml.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata") ?? throw new XmlException("Missing SDK package metadata.");
        string Field(string name) => metadata.Elements().SingleOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        if (Field("id") != manifest.AppId || SemanticVersion.Parse(Field("version")) != SemanticVersion.Parse(manifest.Version) ||
            Field("rid") != manifest.Rid || Field("channel") != manifest.Channel || Field("mainExe") != manifest.AppId + ".exe")
            throw new IOException("SDK metadata does not match the code inventory.");
    }
}
