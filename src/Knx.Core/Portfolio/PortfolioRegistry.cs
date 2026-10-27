using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knx.Portfolio;

/// <summary>File state of a registered project, derived from its size without reading it.</summary>
public enum PortfolioState
{
    /// <summary>The file still has the size recorded at the last registration or refresh.</summary>
    Ok,

    /// <summary>The file exists but no longer matches the recorded size.</summary>
    Changed,

    /// <summary>No file exists at the registered path (moved, renamed outside BusChord or deleted).</summary>
    Missing,
}

/// <summary>
/// One project in the portfolio registry. Immutable: the registry stores a replaced copy on
/// every change. <paramref name="Revision"/> counts content changes (add, refresh, move with a
/// different hash), not display renames or opens.
/// </summary>
public sealed record PortfolioEntry(
    string Id,
    string Path,
    string Name,
    DateTimeOffset AddedUtc,
    DateTimeOffset LastOpenedUtc,
    DateTimeOffset UpdatedUtc,
    long Revision,
    long SizeBytes,
    string Sha256,
    string? ProjectId,
    string? ProjectGuid,
    IReadOnlyList<string> BackupRefs,
    string? VersionRef);

/// <summary>Read-only view of one registered project for list, find and status callers.</summary>
public sealed record PortfolioSummary(
    string Id,
    string Path,
    string Name,
    PortfolioState State,
    long Revision,
    long SizeBytes,
    string Sha256,
    DateTimeOffset LastOpenedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string> BackupRefs,
    string? VersionRef)
{
    /// <summary>True when the entry references at least one backup id.</summary>
    public bool HasBackups => BackupRefs.Count > 0;
}

/// <summary>
/// Offline registry for several project files: stable ids, last-opened/updated times, size and
/// SHA-256, and caller-supplied backup/version references. It never opens a project, never writes
/// to one, and never changes the single-project behaviour of <see cref="AppSettings"/>: its own
/// <c>portfolio.json</c> sits next to the settings file.
/// </summary>
/// <remarks>
/// The document carries an explicit <see cref="SchemaVersion"/> and is written atomically
/// (temporary file, then replace) while keeping the previous good document as <c>.bak</c>.
/// A corrupt or unsupported file therefore falls back to the backup, then to an empty registry;
/// the corrupt file is preserved as <c>.corrupt</c> for inspection.
/// All mutations persist immediately and are transactional: the change is applied to a copy, the
/// copy is written, and only a successful write replaces the in-memory state, so a failed write
/// (disk full, locked file) leaves the registry at its previous state.
/// Writes take a bounded advisory lock (<see cref="LockPath"/>) that serializes app instances;
/// a missing or foreign lock falls back to the atomic swap after
/// <see cref="WriteLockTimeoutMilliseconds"/>. Two instances still resolve a lost update
/// last-writer-wins; the registry never merges foreign entries.
/// Paths are compared case-insensitively on Windows only.
/// </remarks>
public sealed class PortfolioRegistry
{
    /// <summary>Schema version written into and required from the portfolio document.</summary>
    public const int SchemaVersion = 1;

    /// <summary>File name used inside <see cref="AppSettings.Directory"/>.</summary>
    public const string FileName = "portfolio.json";

    /// <summary>How long a write waits for another process' lock before it writes unlocked.</summary>
    public const int WriteLockTimeoutMilliseconds = 2000;

    private const int MaxEntries = 1000;
    private const int MaxNameLength = 200;
    private const int MaxPathLength = 4096;
    private const int MaxRefsPerEntry = 64;
    private const int MaxRefLength = 128;
    private const long MaxDocumentBytes = 4 * 1024 * 1024;
    private const int WriteLockRetryMilliseconds = 25;
    private const int SwapAttempts = 8;
    private const int SwapRetryMilliseconds = 25;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly object _gate = new();
    private readonly List<PortfolioEntry> _entries = [];

    /// <summary>Loads (or starts) the registry. A missing file is an empty registry, not an error.</summary>
    public PortfolioRegistry(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        Load();
    }

    /// <summary>Default registry next to the application settings.</summary>
    public static string DefaultPath => System.IO.Path.Combine(AppSettings.Directory, FileName);

    /// <summary>Document this instance reads and writes.</summary>
    public string FilePath { get; }

    /// <summary>Advisory lock file that serializes writers of <see cref="FilePath"/> across processes.</summary>
    public string LockPath => FilePath + ".lock";

    /// <summary>Why the document could not be read as written, or null after a clean load.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Entries sorted by last open (newest first), then by id. Treat as read-only.</summary>
    public IReadOnlyList<PortfolioEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries
                    .OrderByDescending(e => e.LastOpenedUtc)
                    .ThenBy(e => e.Id, StringComparer.Ordinal)
                    .ToList();
        }
    }

    /// <summary>Writes the current state again, atomically. Loading and saving is byte-stable.</summary>
    public void Save()
    {
        lock (_gate) Persist(_entries);
    }

    /// <summary>Finds one entry by its stable id, or null.</summary>
    public PortfolioEntry? Find(string id)
    {
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            return index < 0 ? null : _entries[index];
        }
    }

    /// <summary>Finds one entry by path (full path, case-insensitive on Windows), or null.</summary>
    public PortfolioEntry? FindByPath(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            var index = FindIndexByPath(_entries, full);
            return index < 0 ? null : _entries[index];
        }
    }

    /// <summary>
    /// Registers a project file and persists the registry. A path that is already registered is
    /// returned unchanged, so a duplicate add never creates a second entry.
    /// </summary>
    public PortfolioEntry Add(string projectPath, string? name = null, string? projectId = null,
        string? projectGuid = null)
    {
        var full = Normalize(projectPath);
        lock (_gate)
        {
            var existing = FindIndexByPath(_entries, full);
            if (existing >= 0) return _entries[existing];
            var next = Copy();
            var entry = AddCore(next, full, name, projectId, projectGuid, DateTimeOffset.UtcNow);
            Commit(next);
            return entry;
        }
    }

    /// <summary>Removes an entry. Returns false when the id is unknown.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var next = Copy();
            next.RemoveAt(index);
            Commit(next);
            return true;
        }
    }

    /// <summary>Renames the display name (not the file). Returns false when unknown or unchanged.</summary>
    public bool Rename(string id, string name)
    {
        var clean = CleanName(name, "");
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var entry = _entries[index];
            if (string.Equals(entry.Name, clean, StringComparison.Ordinal)) return false;
            var next = Copy();
            next[index] = entry with { Name = clean };
            Commit(next);
            return true;
        }
    }

    /// <summary>
    /// Repoints an entry to a moved project file and re-reads size and hash. The id and
    /// last-opened time stay; the revision rises only when the content changed.
    /// Throws when the new path is missing or already registered under another id.
    /// </summary>
    public bool Move(string id, string newPath)
    {
        var full = Normalize(newPath);
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var entry = _entries[index];
            if (string.Equals(entry.Path, full, PathComparison)) return false;
            if (!File.Exists(full)) throw new FileNotFoundException("Project file not found.", full);
            if (FindIndexByPath(_entries, full) >= 0)
                throw new InvalidOperationException($"Path '{full}' is already registered under another id.");
            var next = Copy();
            next[index] = RefreshEntry(entry, full);
            Commit(next);
            return true;
        }
    }

    /// <summary>Marks an entry as opened now. Returns false when the id is unknown.</summary>
    public bool Touch(string id)
    {
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var next = Copy();
            next[index] = _entries[index] with { LastOpenedUtc = DateTimeOffset.UtcNow };
            Commit(next);
            return true;
        }
    }

    /// <summary>
    /// Re-reads size and hash of the registered file. Returns false when nothing changed;
    /// throws when the file is missing (use <see cref="State"/> or <see cref="DetectMove"/>).
    /// </summary>
    public bool Refresh(string id)
    {
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var entry = _entries[index];
            if (!File.Exists(entry.Path)) throw new FileNotFoundException("Project file not found.", entry.Path);
            var refreshed = RefreshEntry(entry, entry.Path);
            if (refreshed.Revision == entry.Revision &&
                refreshed.SizeBytes == entry.SizeBytes &&
                string.Equals(refreshed.Sha256, entry.Sha256, StringComparison.Ordinal)) return false;
            var next = Copy();
            next[index] = refreshed;
            Commit(next);
            return true;
        }
    }

    /// <summary>
    /// Appends a backup id (or other caller-supplied version reference) to an entry. Repeats of
    /// the same reference are ignored; older references are dropped beyond the per-entry limit.
    /// Returns false when the id is unknown or the reference was already present.
    /// </summary>
    public bool AttachBackup(string id, string backupRef)
    {
        var reference = NormalizeRef(backupRef, nameof(backupRef));
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var entry = _entries[index];
            if (entry.BackupRefs.Contains(reference, StringComparer.Ordinal)) return false;
            var refs = entry.BackupRefs.ToList();
            refs.Add(reference);
            if (refs.Count > MaxRefsPerEntry) refs.RemoveRange(0, refs.Count - MaxRefsPerEntry);
            var next = Copy();
            next[index] = entry with { BackupRefs = refs };
            Commit(next);
            return true;
        }
    }

    /// <summary>Sets or clears the free-form version reference of an entry.</summary>
    public bool SetVersionRef(string id, string? versionRef)
    {
        var reference = versionRef is null ? null : NormalizeRef(versionRef, nameof(versionRef));
        lock (_gate)
        {
            var index = FindIndexById(_entries, id);
            if (index < 0) return false;
            var entry = _entries[index];
            if (string.Equals(entry.VersionRef, reference, StringComparison.Ordinal)) return false;
            var next = Copy();
            next[index] = entry with { VersionRef = reference };
            Commit(next);
            return true;
        }
    }

    /// <summary>Size-based state of one entry; a same-size edit stays <see cref="PortfolioState.Ok"/> until <see cref="Refresh"/>.</summary>
    public PortfolioState State(PortfolioEntry entry)
    {
        var info = new FileInfo(entry.Path);
        if (!info.Exists) return PortfolioState.Missing;
        return info.Length == entry.SizeBytes ? PortfolioState.Ok : PortfolioState.Changed;
    }

    /// <summary>Read-only revision and backup summary of one entry, or null when the id is unknown.</summary>
    public PortfolioSummary? Summarize(string id)
    {
        var entry = Find(id);
        return entry is null
            ? null
            : new PortfolioSummary(entry.Id, entry.Path, entry.Name, State(entry), entry.Revision,
                entry.SizeBytes, entry.Sha256, entry.LastOpenedUtc, entry.UpdatedUtc,
                entry.BackupRefs, entry.VersionRef);
    }

    /// <summary>
    /// Searches <paramref name="searchRoot"/> for a project file whose size and SHA-256 match the
    /// entry, for the "project was moved outside BusChord" case. Read-only: the caller decides
    /// whether to call <see cref="Move"/>. Returns the matching full path, or null.
    /// </summary>
    public string? DetectMove(string id, string searchRoot)
    {
        if (string.IsNullOrWhiteSpace(searchRoot))
            throw new ArgumentException("A search root is required.", nameof(searchRoot));
        var entry = Find(id) ?? throw new ArgumentException($"Unknown portfolio entry '{id}'.", nameof(id));
        var root = System.IO.Path.GetFullPath(searchRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 8,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        foreach (var file in Directory.EnumerateFiles(root, "*.knxproj", options)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var full = System.IO.Path.GetFullPath(file);
            if (string.Equals(full, entry.Path, PathComparison)) continue;
            if (new FileInfo(full).Length != entry.SizeBytes) continue;
            if (string.Equals(Hash(full), entry.Sha256, StringComparison.Ordinal)) return full;
        }
        return null;
    }

    /// <summary>
    /// Imports existing recent-project paths from the settings once: still existing files are
    /// registered in their recent order, and the last project is the most recently opened one.
    /// The settings object and file are not modified. Returns the number of newly added entries.
    /// </summary>
    public int Seed(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.LastProject)) candidates.Add(settings.LastProject);
        candidates.AddRange(settings.RecentProjects.Where(p => !string.IsNullOrWhiteSpace(p)));
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var next = Copy();
            var added = 0;
            var rank = 0;
            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Normalize(candidate);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
                {
                    continue;
                }
                if (!File.Exists(full) || FindIndexByPath(next, full) >= 0 || next.Count >= MaxEntries) continue;
                AddCore(next, full, null, null, null, now.AddTicks(-rank++));
                added++;
            }
            if (added > 0) Commit(next);
            return added;
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A project path is required.", nameof(path));
        var full = System.IO.Path.GetFullPath(path.Trim());
        if (full.Length > MaxPathLength) throw new ArgumentException("Project path is too long.", nameof(path));
        return full;
    }

    private static string NormalizeRef(string value, string parameter)
    {
        var reference = value.Trim();
        if (reference.Length == 0) throw new ArgumentException("A reference is required.", parameter);
        if (reference.Length > MaxRefLength) throw new ArgumentException("Reference is too long.", parameter);
        return reference;
    }

    private static string CleanName(string name, string path)
    {
        var text = name.Trim();
        if (text.Length == 0)
            text = System.IO.Path.GetFileNameWithoutExtension(path);
        if (text.Length == 0) throw new ArgumentException("A name is required.", nameof(name));
        return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
    }

    private PortfolioEntry AddCore(List<PortfolioEntry> entries, string fullPath, string? name, string? projectId,
        string? projectGuid, DateTimeOffset opened)
    {
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Project file not found.", fullPath);
        if (entries.Count >= MaxEntries)
            throw new InvalidOperationException($"The portfolio is limited to {MaxEntries} projects.");
        var entry = new PortfolioEntry(
            NewId(entries), fullPath, CleanName(name ?? "", fullPath), opened, opened, opened, 1,
            new FileInfo(fullPath).Length, Hash(fullPath), projectId?.Trim(), projectGuid?.Trim(), [], null);
        entries.Add(entry);
        return entry;
    }

    private PortfolioEntry RefreshEntry(PortfolioEntry entry, string path)
    {
        var size = new FileInfo(path).Length;
        var sha = Hash(path);
        var changed = size != entry.SizeBytes || !string.Equals(sha, entry.Sha256, StringComparison.Ordinal);
        return entry with
        {
            Path = path,
            SizeBytes = size,
            Sha256 = sha,
            Revision = changed ? entry.Revision + 1 : entry.Revision,
            UpdatedUtc = changed ? DateTimeOffset.UtcNow : entry.UpdatedUtc,
        };
    }

    private static int FindIndexById(List<PortfolioEntry> entries, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        return entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
    }

    private static int FindIndexByPath(List<PortfolioEntry> entries, string fullPath)
        => entries.FindIndex(e => string.Equals(e.Path, fullPath, PathComparison));

    private static string NewId(List<PortfolioEntry> entries)
    {
        while (true)
        {
            var id = "pf-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant();
            if (entries.All(e => !string.Equals(e.Id, id, StringComparison.Ordinal))) return id;
        }
    }

    /// <summary>Candidate state for one mutation; never the live list.</summary>
    private List<PortfolioEntry> Copy() => new(_entries);

    /// <summary>Persists the candidate first, then makes it the in-memory state.</summary>
    private void Commit(List<PortfolioEntry> next)
    {
        Persist(next);
        _entries.Clear();
        _entries.AddRange(next);
    }

    /// <summary>SHA-256 of the file, lowercase hex. Reads the file once; never opens the archive.</summary>
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private string BackupPath => FilePath + ".bak";

    private void Load()
    {
        _entries.Clear();
        if (!File.Exists(FilePath)) return;
        try
        {
            _entries.AddRange(ReadDocument(FilePath));
            return;
        }
        catch (Exception e) when (IsContentError(e))
        {
            LoadError = "Portfolio file is corrupt: " + e.Message;
            PreserveCorrupt();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadError = "Portfolio file cannot be read: " + e.Message;
            return;
        }

        if (File.Exists(BackupPath))
        {
            try
            {
                _entries.AddRange(ReadDocument(BackupPath));
                LoadError += " Recovered the last good state from " + System.IO.Path.GetFileName(BackupPath) + ".";
                return;
            }
            catch (Exception e) when (IsContentError(e) || e is IOException or UnauthorizedAccessException)
            {
                LoadError += " The backup was unreadable too: " + e.Message;
            }
        }
        _entries.Clear();
    }

    private static bool IsContentError(Exception e)
        => e is JsonException or InvalidDataException or ArgumentException or NotSupportedException;

    /// <summary>Keeps a corrupt document for inspection instead of overwriting it on the next save.</summary>
    private void PreserveCorrupt()
    {
        try
        {
            File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked file stays in place; the next successful save replaces it.
        }
    }

    private static List<PortfolioEntry> ReadDocument(string path)
    {
        var info = new FileInfo(path);
        if (info.Length == 0) throw new InvalidDataException("The document is empty.");
        if (info.Length > MaxDocumentBytes)
            throw new InvalidDataException("The document exceeds the 4 MiB limit.");
        var document = JsonSerializer.Deserialize<PortfolioDocument>(File.ReadAllText(path), Json)
                       ?? throw new InvalidDataException("The document is empty.");
        if (document.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Unsupported schema version {document.SchemaVersion}.");
        if (document.Entries is null) return [];

        var entries = new List<PortfolioEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer);
        foreach (var dto in document.Entries)
        {
            if (entries.Count >= MaxEntries) break;
            var entry = FromDto(dto);
            if (entry is null) continue;
            if (!ids.Add(entry.Id) || !paths.Add(entry.Path)) continue;
            entries.Add(entry);
        }
        return entries;
    }

    private static PortfolioEntry? FromDto(PortfolioEntryDto? dto)
    {
        if (dto?.Id is null || dto.Path is null || dto.Name is null) return null;
        if (!IsValidId(dto.Id)) return null;
        string full;
        try
        {
            full = Normalize(dto.Path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
        {
            return null;
        }
        var name = dto.Name.Trim();
        if (name.Length == 0) return null;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var size = dto.SizeBytes;
        if (size < 0) return null;
        var sha = dto.Sha256 ?? "";
        if (sha.Length is not (0 or 64) || !sha.All(Uri.IsHexDigit)) return null;
        var revision = dto.Revision;
        if (revision < 1) revision = 1;
        var added = dto.AddedUtc ?? DateTimeOffset.UnixEpoch;
        var opened = dto.LastOpenedUtc ?? added;
        var updated = dto.UpdatedUtc ?? added;
        var refs = new List<string>();
        if (dto.BackupRefs is not null)
        {
            foreach (var reference in dto.BackupRefs)
            {
                if (refs.Count >= MaxRefsPerEntry) break;
                if (string.IsNullOrWhiteSpace(reference) || reference.Length > MaxRefLength) continue;
                var clean = reference.Trim();
                if (!refs.Contains(clean, StringComparer.Ordinal)) refs.Add(clean);
            }
        }
        var version = string.IsNullOrWhiteSpace(dto.VersionRef) ? null : dto.VersionRef.Trim();
        if (version is { Length: > MaxRefLength }) version = version[..MaxRefLength];
        return new PortfolioEntry(dto.Id, full, name, added, opened, updated, revision, size, sha,
            CleanOptional(dto.ProjectId), CleanOptional(dto.ProjectGuid), refs, version);
    }

    private static string? CleanOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidId(string id)
    {
        if (id.Length != 3 + 12 || !id.StartsWith("pf-", StringComparison.Ordinal)) return false;
        foreach (var character in id.AsSpan(3))
            if (!char.IsAsciiHexDigitLower(character)) return false;
        return true;
    }

    /// <summary>
    /// Writes <paramref name="entries"/> atomically and keeps the previous good state as <c>.bak</c>.
    /// Throws on failure while the caller's live list stays untouched.
    /// </summary>
    private void Persist(IReadOnlyList<PortfolioEntry> entries)
    {
        var directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var bytes = Serialize(entries);
        using var writeLock = TryAcquireWriteLock();
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            Swap(temp);
            temp = null;
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    /// <summary>
    /// Bounded advisory lock across processes. Returns null when another holder keeps the lock
    /// past <see cref="WriteLockTimeoutMilliseconds"/> or locking is not permitted; the caller then
    /// writes unlocked, which the atomic swap still keeps consistent.
    /// </summary>
    private FileStream? TryAcquireWriteLock()
    {
        var deadline = Environment.TickCount64 + WriteLockTimeoutMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(WriteLockRetryMilliseconds);
            }
        }
    }

    /// <summary>Replaces the document; a briefly locked file is retried, never waited on forever.</summary>
    private void Swap(string temp)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    try
                    {
                        File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // Some file systems cannot replace: keep the last good state aside first.
                        TryBackup();
                        File.Move(temp, FilePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temp, FilePath);
                }
                return;
            }
            catch (Exception e) when (attempt < SwapAttempts && e is IOException)
            {
                Thread.Sleep(SwapRetryMilliseconds);
            }
        }
    }

    private void TryBackup()
    {
        try
        {
            File.Copy(FilePath, BackupPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No backup is better than a failed save; the corrupt-recovery path still applies.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; the next save uses a fresh name.
        }
    }

    /// <summary>Deterministic bytes: fixed property order, entries sorted by id, one trailing newline.</summary>
    private byte[] Serialize(IReadOnlyList<PortfolioEntry> entries)
    {
        var document = new PortfolioDocument
        {
            SchemaVersion = SchemaVersion,
            Entries = entries
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new PortfolioEntryDto
                {
                    Id = e.Id,
                    Path = e.Path,
                    Name = e.Name,
                    AddedUtc = e.AddedUtc,
                    LastOpenedUtc = e.LastOpenedUtc,
                    UpdatedUtc = e.UpdatedUtc,
                    Revision = e.Revision,
                    SizeBytes = e.SizeBytes,
                    Sha256 = e.Sha256,
                    ProjectId = e.ProjectId,
                    ProjectGuid = e.ProjectGuid,
                    BackupRefs = [.. e.BackupRefs],
                    VersionRef = e.VersionRef,
                })
                .ToList(),
        };
        var json = JsonSerializer.Serialize(document, Json) + "\n";
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
    }

    private sealed class PortfolioDocument
    {
        public int SchemaVersion { get; set; }
        public List<PortfolioEntryDto>? Entries { get; set; } = [];
    }

    private sealed class PortfolioEntryDto
    {
        public string? Id { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public DateTimeOffset? AddedUtc { get; set; }
        public DateTimeOffset? LastOpenedUtc { get; set; }
        public DateTimeOffset? UpdatedUtc { get; set; }
        public long Revision { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
        public string? ProjectId { get; set; }
        public string? ProjectGuid { get; set; }
        public List<string>? BackupRefs { get; set; } = [];
        public string? VersionRef { get; set; }
    }
}
