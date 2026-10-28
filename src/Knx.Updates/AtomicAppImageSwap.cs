using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Knx.Updates;

/// <summary>Stages beside the installed image, then uses one same-filesystem rename. Never requests elevation.</summary>
public static class AtomicAppImageSwap
{
    public static async Task ApplyPackageAsync(string authenticatedPackage, string image, CancellationToken token = default)
    {
        using var package = ZipFile.OpenRead(authenticatedPackage);
        var candidates = package.Entries.Where(e => e.FullName == "lib/app/BusChord.AppImage").ToArray();
        if (candidates.Length != 1 || candidates[0].Length is <= 0 or > UpdateManifests.MaximumPackageBytes)
            throw new InvalidDataException("The authenticated package has no single supported AppImage.");
        using var input = candidates[0].Open();
        await StageAndReplaceAsync(input, candidates[0].Length, image, token).ConfigureAwait(false);
    }
    public static async Task StageAndReplaceAsync(Stream authenticatedImage, long expectedBytes, string image, CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        image = Path.GetFullPath(image);
        if ((File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0) throw new IOException("The installed AppImage must not be a symbolic link.");
        var folder = Path.GetDirectoryName(image)!;
        var stage = Path.Combine(folder, ".buschord-stage-" + Guid.NewGuid().ToString("N"));
        var backupStage = Path.Combine(folder, ".buschord-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; long received = 0; int read;
                while ((read = await authenticatedImage.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received = checked(received + read);
                    if (received > expectedBytes || received > UpdateManifests.MaximumPackageBytes) throw new InvalidDataException("AppImage staging exceeded its signed package bounds.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (received != expectedBytes) throw new InvalidDataException("The new AppImage is incomplete.");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            token.ThrowIfCancellationRequested();
            // A hard link preserves the old inode. Failure leaves the active image unchanged.
            if (link(image, backupStage) != 0) throw new IOException("The previous AppImage could not be preserved.");
            File.Move(backupStage, image + ".previous", overwrite: true);
            token.ThrowIfCancellationRequested();
            File.Move(stage, image, overwrite: true); // One rename, never cross-device mv/copy or pkexec.
            SyncDirectory(folder);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); if (File.Exists(backupStage)) File.Delete(backupStage); }
    }
    private static void SyncDirectory(string directory)
    {
        var fd = open(directory, 0x10000 | 0x80000); // Linux O_DIRECTORY | O_CLOEXEC.
        if (fd < 0) throw new IOException("Cannot flush the AppImage directory.");
        try { if (fsync(fd) != 0) throw new IOException("Cannot persist the AppImage rename."); }
        finally { close(fd); }
    }
    [DllImport("libc", SetLastError = true)] private static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
