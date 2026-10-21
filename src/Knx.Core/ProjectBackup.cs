using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Knx;

/// <summary>
/// Local, non-destructive backups of saved *.knxproj archives and explicit snapshots of an
/// in-memory project. Independent of undo/redo and of the workbench layout.
/// </summary>
/// <remarks>
/// Backups are ZIP copies of the whole archive (including unknown parts) plus one JSON manifest.
/// Creation, restore and delete never extract entries and never change the source project.
/// Restore validates the backup hash and loads the archive through <see cref="Knxproj"/> before
/// the destination is replaced atomically.
/// </remarks>
public static class ProjectBackup
{
    /// <summary>Number of backups kept per project identity when no other value is passed.</summary>
    public const int DefaultRetention = 10;

    private const int MaxArchiveBytes = 128 * 1024 * 1024;
    private const int ManifestVersion = 1;
    private const int MaxManifestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Creates a timestamped backup of a saved project archive. The source is copied byte for byte
    /// after it was accepted by the project loader; zero-sized, oversized and corrupt archives are
    /// refused. Older backups of the same project identity are pruned to <paramref name="retention"/>.
    /// </summary>
    public static ProjectBackupInfo Create(string projectPath, string? backupRoot = null, int retention = DefaultRetention)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("A project path is required.", nameof(projectPath));
        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Project file not found.", fullPath);
        var bytes = ReadBounded(fullPath);
        return Add(bytes, IdentityForPath(fullPath), fullPath, backupRoot, retention);
    }

    /// <summary>
    /// Serializes the current in-memory project to a temporary archive and stores that snapshot.
    /// Only an explicit caller invocation creates a snapshot; there is no timer or background work.
    /// Pass the project path so later <see cref="List"/> calls can find the snapshot by identity.
    /// </summary>
    public static ProjectBackupInfo Capture(Project project, string? projectPath = null,
        string? backupRoot = null, int retention = DefaultRetention)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateRetention(retention);
        var temporary = Path.Combine(Path.GetTempPath(), "buschord-capture-" + Guid.NewGuid().ToString("N") + ".knxproj");
        try
        {
            Knxproj.Save(project, temporary);
            var bytes = ReadBounded(temporary);
            var sourcePath = "";
            string identity;
            if (projectPath is null)
            {
                var projectKey = (project.Guid ?? project.Id).Trim();
                if (projectKey.Length == 0) throw new InvalidDataException("Project has neither a GUID nor an ID.");
                identity = "project:" + projectKey;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(projectPath))
                    throw new ArgumentException("Project path must not be empty.", nameof(projectPath));
                sourcePath = Path.GetFullPath(projectPath);
                identity = IdentityForPath(sourcePath);
            }
            return Add(bytes, identity, sourcePath, backupRoot, retention);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Lists usable backups, newest first. Without <paramref name="projectPath"/> every backup in the
    /// root is returned; with it, only backups of that project path. Malformed or stale entries are skipped.
    /// </summary>
    public static IReadOnlyList<ProjectBackupInfo> List(string? projectPath = null, string? backupRoot = null)
    {
        var root = RootPath(backupRoot);
        if (!Directory.Exists(root)) return [];
        string? identity = null;
        if (projectPath is not null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Project path must not be empty.", nameof(projectPath));
            identity = IdentityForPath(Path.GetFullPath(projectPath));
        }
        return Manifests(root)
            .Where(m => identity is null || m.Identity == identity)
            .OrderByDescending(m => m.CreatedUtc)
            .ThenByDescending(m => m.Id, StringComparer.Ordinal)
            .Select(m => ToInfo(m, Path.Combine(root, m.Id + ".knxproj")))
            .ToList();
    }

    /// <summary>
    /// Restores a backup to a caller-selected path. The backup is checked against its manifest and
    /// loaded through <see cref="Knxproj"/>; the destination is replaced atomically only afterwards.
    /// Restoring to the original path replaces it, any other path leaves the original untouched.
    /// </summary>
    public static ProjectBackupInfo Restore(string backupId, string destinationPath, string? backupRoot = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        var root = RootPath(backupRoot);
        var (manifest, archivePath) = ReadEntry(root, backupId);
        var destination = Path.GetFullPath(destinationPath);
        if (IsInside(root, destination))
            throw new InvalidDataException("Restore destination must be outside the backup root.");
        var bytes = ReadBounded(archivePath);
        if (bytes.LongLength != manifest.SizeBytes)
            throw new InvalidDataException(
                $"Backup '{backupId}' is stale: manifest says {manifest.SizeBytes} bytes, archive has {bytes.LongLength}.");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Backup '{backupId}' is corrupt: SHA-256 does not match its manifest.");
        WriteAtomically(destination, bytes, backupId);
        return ToInfo(manifest, archivePath);
    }

    /// <summary>Deletes one backup archive and its manifest. Missing and stale entries fail explicitly.</summary>
    public static void Delete(string backupId, string? backupRoot = null)
    {
        var root = RootPath(backupRoot);
        if (!IsValidId(backupId)) throw new ArgumentException("Backup ID must be a plain name.", nameof(backupId));
        if (!Directory.Exists(root)) throw new FileNotFoundException("Backup root not found.", root);
        var archivePath = Contained(root, backupId + ".knxproj");
        var manifestPath = Contained(root, backupId + ".json");
        if (!File.Exists(archivePath) && !File.Exists(manifestPath))
            throw new FileNotFoundException($"Backup '{backupId}' does not exist.", archivePath);
        File.Delete(archivePath);
        File.Delete(manifestPath);
    }

    private static ProjectBackupInfo Add(byte[] bytes, string identity, string sourcePath,
        string? backupRoot, int retention)
    {
        ValidateRetention(retention);
        if (bytes.Length == 0) throw new InvalidDataException("Project archive is empty.");
        if (bytes.Length > MaxArchiveBytes)
            throw new InvalidDataException("Project archive exceeds the 128 MiB backup limit.");
        var root = RootPath(backupRoot);
        Directory.CreateDirectory(root);

        var created = DateTimeOffset.UtcNow;
        var id = created.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
                 "-" + Guid.NewGuid().ToString("N")[..12];
        var archivePath = Contained(root, id + ".knxproj");
        var manifestPath = Contained(root, id + ".json");
        var temporary = Contained(root, "." + id + ".tmp");
        var archiveWritten = false;
        var manifestWritten = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            Project project;
            try
            {
                project = Knxproj.Load(temporary);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
            {
                throw new InvalidDataException($"Project cannot be backed up: {e.Message}", e);
            }
            File.Move(temporary, archivePath);
            archiveWritten = true;
            WriteManifest(manifestPath, new BackupManifest
            {
                Version = ManifestVersion,
                Id = id,
                Identity = identity,
                ProjectId = project.Id,
                ProjectGuid = project.Guid,
                Name = project.Name,
                SourcePath = sourcePath,
                CreatedUtc = created,
                SizeBytes = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            });
            manifestWritten = true;
            Prune(root, identity, retention, id);
            return ToInfo(ReadManifest(manifestPath), archivePath);
        }
        catch
        {
            TryDelete(temporary);
            if (archiveWritten) TryDelete(archivePath);
            if (manifestWritten) TryDelete(manifestPath);
            throw;
        }
    }

    private static void Prune(string root, string identity, int retention, string keepId)
    {
        // ponytail: no cross-process lock. Unique IDs and temp+move make concurrent creation safe;
        // retention may race and leave one extra file until the next create.
        List<BackupManifest> ordered;
        try
        {
            ordered = Manifests(root)
                .Where(m => m.Identity == identity)
                .OrderByDescending(m => m.CreatedUtc)
                .ThenByDescending(m => m.Id, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var keep = ordered.Take(retention).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        if (keep.Add(keepId) && keep.Count > retention)
        {
            var oldest = ordered.LastOrDefault(m => keep.Contains(m.Id) && m.Id != keepId);
            if (oldest is not null) keep.Remove(oldest.Id);
        }
        foreach (var stale in ordered.Where(m => !keep.Contains(m.Id)))
        {
            try
            {
                TryDelete(Path.Combine(root, stale.Id + ".knxproj"));
                TryDelete(Path.Combine(root, stale.Id + ".json"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A locked old backup stays until the next create; the new backup must not fail.
            }
        }
    }

    private static (BackupManifest Manifest, string ArchivePath) ReadEntry(string root, string backupId)
    {
        if (!IsValidId(backupId)) throw new ArgumentException("Backup ID must be a plain name.", nameof(backupId));
        if (!Directory.Exists(root)) throw new FileNotFoundException("Backup root not found.", root);
        var manifestPath = Contained(root, backupId + ".json");
        var archivePath = Contained(root, backupId + ".knxproj");
        if (!File.Exists(manifestPath) && !File.Exists(archivePath))
            throw new FileNotFoundException($"Backup '{backupId}' does not exist.", archivePath);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"Backup '{backupId}' is stale: its manifest is missing.");
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Backup '{backupId}' is stale: its archive is missing.", archivePath);
        RejectReparsePoint(archivePath);
        var manifest = ReadManifest(manifestPath);
        if (manifest.Id != backupId)
            throw new InvalidDataException($"Backup '{backupId}' is stale: manifest ID does not match its file name.");
        return (manifest, archivePath);
    }

    private static List<BackupManifest> Manifests(string root)
    {
        var manifests = new List<BackupManifest>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var manifest = ReadManifest(path);
                if (Path.GetFileNameWithoutExtension(path) != manifest.Id) continue;
                if (!File.Exists(Path.Combine(root, manifest.Id + ".knxproj"))) continue;
                manifests.Add(manifest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or ArgumentException)
            {
                // One unreadable entry must not make every other backup inaccessible.
            }
        }
        return manifests;
    }

    private static BackupManifest ReadManifest(string path)
    {
        if (new FileInfo(path).Length > MaxManifestBytes)
            throw new InvalidDataException("Backup manifest exceeds the 64 KiB limit.");
        BackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json)
                       ?? throw new InvalidDataException("Backup manifest is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Backup manifest is not valid JSON: " + e.Message, e);
        }
        if (manifest.Version != ManifestVersion)
            throw new InvalidDataException($"Unsupported backup manifest version {manifest.Version}.");
        manifest.Id ??= "";
        manifest.Identity ??= "";
        manifest.ProjectId ??= "";
        manifest.Name ??= "";
        manifest.SourcePath ??= "";
        manifest.Sha256 ??= "";
        if (!IsValidId(manifest.Id)) throw new InvalidDataException("Backup manifest has an invalid ID.");
        if (string.IsNullOrEmpty(manifest.Identity)) throw new InvalidDataException("Backup manifest has no identity.");
        if (string.IsNullOrEmpty(manifest.ProjectId)) throw new InvalidDataException("Backup manifest has no project ID.");
        if (manifest.SizeBytes <= 0 || manifest.SizeBytes > MaxArchiveBytes)
            throw new InvalidDataException("Backup manifest has an invalid size.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Backup manifest has an invalid SHA-256.");
        return manifest;
    }

    private static void WriteManifest(string path, BackupManifest manifest)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json));
            File.Move(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void WriteAtomically(string destination, byte[] bytes, string backupId)
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(Path.GetFileName(destination)))
            throw new ArgumentException("Destination must be a file path.", nameof(destination));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Destination directory not found: {directory}");
        var temporary = Path.Combine(directory,
            "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            try
            {
                _ = Knxproj.Load(temporary);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
            {
                throw new InvalidDataException($"Backup '{backupId}' cannot be restored: {e.Message}", e);
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length == 0) throw new InvalidDataException("Project archive is empty.");
        if (input.Length > MaxArchiveBytes)
            throw new InvalidDataException("Project archive exceeds the 128 MiB backup limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static ProjectBackupInfo ToInfo(BackupManifest manifest, string archivePath) => new(
        manifest.Id, manifest.ProjectId, manifest.ProjectGuid, manifest.Name, manifest.SourcePath,
        manifest.CreatedUtc, manifest.SizeBytes, manifest.Sha256, archivePath);

    private static string RootPath(string? backupRoot)
    {
        var root = string.IsNullOrWhiteSpace(backupRoot) ? DefaultRoot() : backupRoot;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string DefaultRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData)) appData = Path.GetTempPath();
        return Path.Combine(appData, "BusChord", "Backups");
    }

    private static string IdentityForPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return "path:" + (OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized);
    }

    private static string Contained(string root, string name)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, name));
        if (!fullPath.StartsWith(WithSeparator(fullRoot), PathComparison))
            throw new InvalidDataException("Backup path escapes the backup root.");
        return fullPath;
    }

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.StartsWith(WithSeparator(fullRoot), PathComparison);
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool IsValidId(string id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 160 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Backup files must not be symbolic links or reparse points.");
    }

    private static void ValidateRetention(int retention)
    {
        if (retention is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be between 1 and 1000 backups.");
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class BackupManifest
    {
        public int Version { get; set; }
        public string Id { get; set; } = "";
        public string Identity { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string? ProjectGuid { get; set; }
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
    }
}

/// <summary>One listed backup. <paramref name="BackupPath"/> points at the stored *.knxproj copy.</summary>
public sealed record ProjectBackupInfo(
    string Id,
    string ProjectId,
    string? ProjectGuid,
    string Name,
    string SourcePath,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string Sha256,
    string BackupPath);
