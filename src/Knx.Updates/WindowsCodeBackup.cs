using System.Security.Cryptography;
using System.Text.Json;

namespace Knx.Updates;

/// <summary>Independent verified code recovery; project files and global user settings are outside its scope.</summary>
public static class WindowsCodeBackup
{
    private sealed record FileRecord(string Path, long Bytes, string Sha256);
    public static async Task<string> CreateAsync(string current, string stateDirectory, CancellationToken token = default)
    {
        current = Path.GetFullPath(current);
        var backup = Path.Combine(stateDirectory, "code-backups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup); var files = new List<FileRecord>();
        foreach (var source in Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("The installed code includes a link.");
            var relative = Path.GetRelativePath(current, source);
            var target = Path.Combine(backup, "files", relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            input.Position = 0; var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            files.Add(new(relative, input.Length, hash));
        }
        if (files.Count == 0) throw new IOException("The installed code directory is empty.");
        await TrustedUpdateSource.AtomicWrite(Path.Combine(backup, "files.json"), JsonSerializer.Serialize(files), token).ConfigureAwait(false);
        await VerifyAsync(backup, token).ConfigureAwait(false); return backup;
    }
    private static async Task VerifyAsync(string backup, CancellationToken token)
    {
        var entries = JsonSerializer.Deserialize<FileRecord[]>(await File.ReadAllTextAsync(Path.Combine(backup, "files.json"), token).ConfigureAwait(false))
            ?? throw new IOException("Missing code recovery manifest.");
        foreach (var entry in entries)
        {
            var root = Path.GetFullPath(Path.Combine(backup, "files")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, entry.Path));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Invalid code backup path.");
            await using var stream = File.OpenRead(path);
            if (stream.Length != entry.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != entry.Sha256)
                throw new IOException("The code recovery copy is damaged.");
        }
    }
    public static async Task RestoreAsync(string backup, string current, CancellationToken token = default)
    {
        await VerifyAsync(backup, token).ConfigureAwait(false);
        var stage = current + ".restore-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(backup, "files"), "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(stage, Path.GetRelativePath(Path.Combine(backup, "files"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var failed = current + ".failed-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(current)) Directory.Move(current, failed);
        try { Directory.Move(stage, current); }
        catch { if (!Directory.Exists(current) && Directory.Exists(failed)) Directory.Move(failed, current); throw; }
        // Keep the independently verified backup and failed code for explicit recovery/diagnosis.
    }
}
