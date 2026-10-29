namespace Knx.Bus.History;

/// <summary>Private creation modes without changing permissions of existing parent directories.</summary>
internal static class TelegramHistoryStorage
{
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode DataMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        var missing = new Stack<string>();
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException("No existing history directory ancestor.");
        }
        while (missing.TryPop(out var directory)) Directory.CreateDirectory(directory, DirectoryMode);
    }

    internal static FileStream Create(string path, System.IO.FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = DataMode;
        return new(path, options);
    }
}
