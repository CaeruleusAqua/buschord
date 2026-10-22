using Knx.Portfolio;

namespace Knx.App.Workbench;

/// <summary>Outcome of one portfolio action, with the entry it belongs to.</summary>
public enum PortfolioActionStatus
{
    Done,
    AlreadyRegistered,
    Updated,
    Unchanged,
    ProjectNotSaved,
    FileMissing,
    EntryMissing,
    InvalidName,
    DuplicatePath,
    Failed,
}

/// <summary>Result of a portfolio action; <see cref="Path"/> is the project file when known.</summary>
public readonly record struct PortfolioResult(PortfolioActionStatus Status, string? EntryId = null, string? Path = null)
{
    public bool Ok => Status is PortfolioActionStatus.Done or PortfolioActionStatus.AlreadyRegistered
        or PortfolioActionStatus.Updated or PortfolioActionStatus.Unchanged;
}

/// <summary>
/// Pure decisions and labels behind the project portfolio surface, so the shell and the console
/// checks share one contract. The registry owns persistence; this type only validates before it
/// calls the registry and never touches project files (no move, no delete, no save).
/// </summary>
public static class WorkbenchPortfolio
{
    /// <summary>The portfolio document next to the effective settings file (never a second setting).</summary>
    public static string StoragePath(string settingsPath)
    {
        var full = System.IO.Path.GetFullPath(settingsPath);
        var directory = System.IO.Path.GetDirectoryName(full);
        return System.IO.Path.Combine(string.IsNullOrEmpty(directory) ? AppSettings.Directory : directory,
            PortfolioRegistry.FileName);
    }

    /// <summary>Localization key of a file state; the caller resolves the language.</summary>
    public static string StateKey(PortfolioState state) => state switch
    {
        PortfolioState.Changed => "PortfolioStateChanged",
        PortfolioState.Missing => "PortfolioStateMissing",
        _ => "PortfolioStateOk",
    };

    /// <summary>First eight hex characters of the recorded content hash.</summary>
    public static string ShortHash(PortfolioEntry entry)
        => entry.Sha256.Length <= 8 ? entry.Sha256 : entry.Sha256[..8];

    /// <summary>Local wall-clock label of a timestamp, for the last-opened column.</summary>
    public static string TimestampLabel(DateTimeOffset value)
        => value == DateTimeOffset.UnixEpoch ? "" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Registers the open project; an unsaved or missing file is never added.</summary>
    public static PortfolioResult AddCurrent(PortfolioRegistry registry, string? projectPath, string? name)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return new(PortfolioActionStatus.ProjectNotSaved);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(projectPath.Trim());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
        {
            return new(PortfolioActionStatus.FileMissing);
        }
        if (!File.Exists(full)) return new(PortfolioActionStatus.FileMissing, Path: full);
        if (registry.FindByPath(full) is { } existing)
            return new(PortfolioActionStatus.AlreadyRegistered, existing.Id, existing.Path);
        try
        {
            var entry = registry.Add(full, name);
            return new(PortfolioActionStatus.Done, entry.Id, entry.Path);
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return new(PortfolioActionStatus.Failed, Path: full);
        }
    }

    /// <summary>Removes only the registry entry; the project file is never deleted.</summary>
    public static PortfolioResult Remove(PortfolioRegistry registry, string id)
        => registry.Remove(id) ? new(PortfolioActionStatus.Done) : new(PortfolioActionStatus.EntryMissing);

    /// <summary>Changes the display name only; the file and the revision stay untouched.</summary>
    public static PortfolioResult Rename(PortfolioRegistry registry, string id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(PortfolioActionStatus.InvalidName);
        if (registry.Find(id) is not { } entry) return new(PortfolioActionStatus.EntryMissing);
        try
        {
            registry.Rename(id, name.Trim());
            // false means the trimmed name already matched; both are a successful rename request.
            return new(PortfolioActionStatus.Done, entry.Id, entry.Path);
        }
        catch (ArgumentException)
        {
            return new(PortfolioActionStatus.InvalidName, entry.Id, entry.Path);
        }
    }

    /// <summary>Re-reads size and hash of an existing file; a missing file stays a missing entry.</summary>
    public static PortfolioResult Refresh(PortfolioRegistry registry, string id)
    {
        if (registry.Find(id) is not { } entry) return new(PortfolioActionStatus.EntryMissing);
        if (!File.Exists(entry.Path)) return new(PortfolioActionStatus.FileMissing, entry.Id, entry.Path);
        try
        {
            return new(registry.Refresh(id) ? PortfolioActionStatus.Updated : PortfolioActionStatus.Unchanged,
                entry.Id, entry.Path);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            return new(PortfolioActionStatus.Failed, entry.Id, entry.Path);
        }
    }

    /// <summary>Checks whether an entry may be opened; missing files never reach the loader.</summary>
    public static PortfolioResult OpenTarget(PortfolioRegistry registry, string id)
    {
        if (registry.Find(id) is not { } entry) return new(PortfolioActionStatus.EntryMissing);
        return File.Exists(entry.Path)
            ? new(PortfolioActionStatus.Done, entry.Id, entry.Path)
            : new(PortfolioActionStatus.FileMissing, entry.Id, entry.Path);
    }

    /// <summary>Records a successful open; call only after the project was actually loaded.</summary>
    public static bool MarkOpened(PortfolioRegistry registry, string id) => registry.Touch(id);

    /// <summary>Repoints a missing entry to its moved file; existing registrations stay unique.</summary>
    public static PortfolioResult Locate(PortfolioRegistry registry, string id, string? newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return new(PortfolioActionStatus.FileMissing);
        if (registry.Find(id) is not { } entry) return new(PortfolioActionStatus.EntryMissing);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(newPath.Trim());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
        {
            return new(PortfolioActionStatus.FileMissing, entry.Id, entry.Path);
        }
        if (!File.Exists(full)) return new(PortfolioActionStatus.FileMissing, entry.Id, full);
        if (registry.FindByPath(full) is { } other && other.Id != id)
            return new(PortfolioActionStatus.DuplicatePath, other.Id, full);
        try
        {
            return new(registry.Move(id, full) ? PortfolioActionStatus.Done : PortfolioActionStatus.Unchanged,
                entry.Id, full);
        }
        catch (FileNotFoundException)
        {
            return new(PortfolioActionStatus.FileMissing, entry.Id, full);
        }
        catch (InvalidOperationException)
        {
            return new(PortfolioActionStatus.DuplicatePath, entry.Id, full);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            return new(PortfolioActionStatus.Failed, entry.Id, full);
        }
    }
}
