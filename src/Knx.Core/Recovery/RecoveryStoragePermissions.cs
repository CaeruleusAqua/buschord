namespace Knx.Recovery;

internal static class RecoveryStoragePermissions
{
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void Directory(string path)
    {
        RecoveryWire.NoLink(path);
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(path);
        else
        {
            System.IO.Directory.CreateDirectory(path, DirectoryMode);
            if (File.GetUnixFileMode(path) != DirectoryMode) File.SetUnixFileMode(path, DirectoryMode);
        }
        RecoveryWire.NoLink(path);
    }

    internal static void ExistingFile(string path)
    {
        RecoveryWire.NoLink(path);
        if (!OperatingSystem.IsWindows() && File.Exists(path) && File.GetUnixFileMode(path) != FileModeBits)
            File.SetUnixFileMode(path, FileModeBits);
    }

    internal static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options = FileOptions.Asynchronous)
    {
        ExistingFile(path);
        var settings = new FileStreamOptions { Mode = mode, Access = access, Share = share, BufferSize = 65536, Options = options };
        if (!OperatingSystem.IsWindows() && mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append)
            settings.UnixCreateMode = FileModeBits;
        var stream = new FileStream(path, settings);
        try { ExistingFile(path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    // These directories contain only managed files: no user-visible name becomes a path component.
    // Repair old permissive files before any content is read; a bounded scan cannot walk arbitrary subtrees.
    internal static void ManagedDirectory(string path)
    {
        Directory(path); var count = 0;
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(path))
        {
            if (++count > 60000) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.RootEntryBudget");
            RecoveryWire.NoLink(entry);
            if (System.IO.Directory.Exists(entry)) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.UnexpectedStorageDirectory");
            ExistingFile(entry);
        }
    }
}
