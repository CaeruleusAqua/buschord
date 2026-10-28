using System.Diagnostics;
using System.Text.Json;

namespace Knx.Updates;

public sealed record ApplyTicket(string Rid, string Channel, string InstalledVersion, string StateDirectory,
    string RootDirectory, string PackagesDirectory, string UpdaterPath, string RestartExecutable, string[] RestartArguments, bool Restart);

public static class ApplyBroker
{
    public static async Task<int> ApplyAsync(ApplyTicket ticket, UpdateTrust trust)
    {
        if (!trust.Configured) return 2;
        // The broker executable/runtime is copied outside current and the AppImage mount before launch.
        var state = Path.GetFullPath(ticket.StateDirectory);
        if (AppContext.BaseDirectory.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ticket.RootDirectory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update broker must run outside the installation being replaced.");
        UpdateProcessLease? lease = null;
        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        while (lease is null && DateTimeOffset.UtcNow < until)
        { lease = UpdateProcessLease.TryEnterInstaller(state); if (lease is null) await Task.Delay(100).ConfigureAwait(false); }
        if (lease is null) return 3; // Existing work wins; defer until a later safe boundary.
        int result;
        using (lease)
        {
            using var source = new TrustedUpdateSource(trust, ticket.Rid, ticket.Channel, ticket.InstalledVersion, state);
            var pending = await source.PendingAsync(ticket.PackagesDirectory).ConfigureAwait(false);
            if (pending is null) return 0;
            var package = Path.Combine(ticket.PackagesDirectory, pending.FileName);
            if (OperatingSystem.IsWindows() && !InstalledCodeInventory.Check(Path.Combine(ticket.RootDirectory, "current"), "BusChord", ticket.InstalledVersion, ticket.Rid).Safe)
            {
                if (ticket.Restart)
                {
                    var start = new ProcessStartInfo(ticket.RestartExecutable) { UseShellExecute = false, WorkingDirectory = state };
                    foreach (var arg in ticket.RestartArguments) start.ArgumentList.Add(arg);
                    start.ArgumentList.Add("--skip-auto-update-once");
                    // Release the kernel lease before this process can open an editor.
                    lease.Dispose(); Process.Start(start)?.Dispose();
                }
                return 4;
            }
            string? backup = null;
            var nativeStarted = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    backup = await WindowsCodeBackup.CreateAsync(Path.Combine(ticket.RootDirectory, "current"), state).ConfigureAwait(false);
                    await TrustedUpdateSource.AtomicWrite(Path.Combine(state, "recovery.json"),
                        JsonSerializer.Serialize(new CodeRecovery(ticket.RootDirectory, backup, pending.SHA256, pending.Version.ToString())), CancellationToken.None).ConfigureAwait(false);
                }
                if (OperatingSystem.IsLinux())
                {
                    await AtomicAppImageSwap.ApplyPackageAsync(package, ticket.RestartExecutable).ConfigureAwait(false); result = 0;
                }
                else
                {
                    using var updater = InheritedLeaseProcess.Start(ticket.UpdaterPath,
                    ["--silent", "apply", "--package", package, "--rootDir", ticket.RootDirectory, "--packageDir", ticket.PackagesDirectory, "--norestart"],
                    state, lease.Handles);
                    nativeStarted = true;
                    // No timeout kills an app or updater. Inherited kernel leases survive a broker crash.
                    result = await updater.WaitAsync().ConfigureAwait(false);
                    if (!File.Exists(Path.Combine(ticket.RootDirectory, "current", "BusChord.exe"))) result = -2;
                    else
                    {
                        var version = FileVersionInfo.GetVersionInfo(Path.Combine(ticket.RootDirectory, "current", "BusChord.dll")).ProductVersion;
                        if (Velopack.SemanticVersion.Parse(version!) != pending.Version) result = -3;
                        else if (!InstalledCodeInventory.Check(Path.Combine(ticket.RootDirectory, "current"), "BusChord", pending.Version.ToString(), ticket.Rid).Safe) result = -4;
                    }
                }
            }
            catch { result = -1; }
            // A failed hash is suppressed across startup, avoiding an automatic restart/retry loop.
            if (result != 0)
            {
                try { await source.RecordApplyFailureAsync(pending, result).ConfigureAwait(false); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (result != 0 && nativeStarted && backup is not null)
                await WindowsCodeBackup.RestoreAsync(backup, Path.Combine(ticket.RootDirectory, "current")).ConfigureAwait(false);
        }
        if (ticket.Restart)
        {
            var start = new ProcessStartInfo(ticket.RestartExecutable) { UseShellExecute = false, WorkingDirectory = state };
            foreach (var arg in ticket.RestartArguments) start.ArgumentList.Add(arg);
            if (result != 0) start.ArgumentList.Add("--skip-auto-update-once");
            Process.Start(start)?.Dispose();
        }
        return result;
    }

    public sealed record CodeRecovery(string RootDirectory, string Backup, string Sha256, string Version);
    public static async Task RecoverAsync(string journalPath)
    {
        var state = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        var recovery = JsonSerializer.Deserialize<CodeRecovery>(await File.ReadAllTextAsync(journalPath).ConfigureAwait(false))
            ?? throw new IOException("Invalid code recovery journal.");
        var allowed = Path.GetFullPath(Path.Combine(state, "code-backups")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(recovery.Backup).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("The recovery backup is outside its state directory.");
        using var lease = UpdateProcessLease.TryEnterInstaller(state) ?? throw new IOException("Close all app instances before recovery.");
        await WindowsCodeBackup.RestoreAsync(recovery.Backup, Path.Combine(recovery.RootDirectory, "current")).ConfigureAwait(false);
    }

    public static string Start(ApplyTicket ticket, string brokerDirectory)
    {
        Directory.CreateDirectory(ticket.StateDirectory);
        var ticketPath = Path.Combine(ticket.StateDirectory, "apply-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(ticketPath, JsonSerializer.Serialize(ticket));
        var executable = Path.Combine(brokerDirectory, OperatingSystem.IsWindows() ? "BusChord.UpdateBroker.exe" : "BusChord.UpdateBroker");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = ticket.StateDirectory, CreateNoWindow = true };
        start.ArgumentList.Add("--apply"); start.ArgumentList.Add(ticketPath);
        Process.Start(start)?.Dispose(); return ticketPath;
    }
}
