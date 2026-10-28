using Velopack;
using Velopack.Locators;

namespace Knx.Updates;

/// <summary>Application-independent update lifecycle. Apply is called only before UI startup or after its normal close gate.</summary>
public sealed class AutomaticUpdates : IDisposable
{
    private readonly TrustedUpdateSource _source;
    private readonly UpdateManager _manager;
    private readonly IVelopackLocator _locator;
    private readonly string _directory;
    private readonly string _rid, _channel;
    private readonly SemaphoreSlim _gate = new(1);
    public event Action<string>? StatusChanged;
    public AutomaticUpdates(UpdateTrust trust, string rid, string channel, string directory, IVelopackLocator? locator = null, HttpMessageHandler? handler = null)
    {
        _locator = locator ?? VelopackLocator.Current; _directory = directory; _rid = rid; _channel = channel;
        _source = new(trust, rid, channel, _locator.CurrentlyInstalledVersion?.ToString() ?? "0.0.0", directory, handler);
        _manager = new(_source, new UpdateOptions { ExplicitChannel = rid + "-" + channel, AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 }, _locator);
    }
    public bool Supported => _source.Configured && _manager.IsInstalled && _locator.PackagesDir is not null &&
        !File.Exists(Path.Combine(AppContext.BaseDirectory, ".package-managed")) && UserWritableLocation();

    private bool UserWritableLocation()
    {
        // Linux's SDK can request pkexec for privileged AppImages. Never enter that path.
        var image = Environment.GetEnvironmentVariable("APPIMAGE");
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(image)) return false;
        var directory = image is not null ? Path.GetDirectoryName(image)! : _locator.RootAppDir;
        if (directory is null) return false;
        var probe = Path.Combine(directory, ".buschord-update-probe-" + Guid.NewGuid().ToString("N"));
        try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } File.Delete(probe); return true; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
    public async Task CheckAndDownloadAsync(UpdatePolicy policy, CancellationToken token = default)
    {
        if (policy == UpdatePolicy.Disabled || !Supported) { StatusChanged?.Invoke("disabled"); return; }
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            StatusChanged?.Invoke("checking");
            _source.OperationCancellation = token;
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null) { StatusChanged?.Invoke("up-to-date"); return; }
            if (_source.CurrentRequiresRetry) { StatusChanged?.Invoke("retry-required"); return; }
            if (policy == UpdatePolicy.CheckOnly) { StatusChanged?.Invoke("available"); return; }
            await _manager.DownloadUpdatesAsync(update, _ => StatusChanged?.Invoke("downloading"), token).ConfigureAwait(false);
            await _source.MarkReadyAsync(_locator.PackagesDir!, token).ConfigureAwait(false);
            StatusChanged?.Invoke(InstalledTreeSafe() ? "ready" : "deferred-user-data");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusChanged?.Invoke("cancelled"); }
        catch (Exception error) { StatusChanged?.Invoke("failed:" + error.GetType().Name); }
        finally { _gate.Release(); }
    }

    /// <summary>Never call while any UI or unsaved project is open. No process is asked to close by this method.</summary>
    public async Task<bool> TryApplyAtSafeBoundaryAsync(UpdatePolicy policy, bool restart, string[]? restartArgs = null)
    {
        if (policy != UpdatePolicy.Automatic || !Supported || !await _gate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            using var lease = UpdateProcessLease.TryEnterInstaller(_directory);
            if (lease is null) return false;
            if (!InstalledTreeSafe()) { StatusChanged?.Invoke("deferred-user-data"); return false; }
            var pending = await _source.PendingAsync(_locator.PackagesDir!).ConfigureAwait(false);
            if (pending is null || pending.Version <= _manager.CurrentVersion) return false;
            // Copy the broker and native updater outside the directory that will be swapped.
            // The broker, then its native updater child, inherit the exclusive kernel leases.
            var broker = Path.Combine(_directory, "brokers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(broker);
            var source = Path.Combine(AppContext.BaseDirectory, "UpdateBroker");
            foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(broker, Path.GetFileName(file)));
            var native = Path.Combine(broker, OperatingSystem.IsWindows() ? "Update.Native.exe" : "Update.Native");
            if (OperatingSystem.IsWindows()) File.Copy(_locator.UpdateExePath!, native);
            var brokerExe = Path.Combine(broker, OperatingSystem.IsWindows() ? "BusChord.UpdateBroker.exe" : "BusChord.UpdateBroker");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(brokerExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var executable = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath!;
            ApplyBroker.Start(new(_rid, _channel, _locator.CurrentlyInstalledVersion!.ToString(), _directory,
                _locator.RootAppDir!, _locator.PackagesDir!, native, executable, restartArgs ?? [], restart), broker);
            return true;
        }
        catch (Exception error) { StatusChanged?.Invoke("apply-failed:" + error.GetType().Name); return false; }
        finally { _gate.Release(); }
    }
    private bool InstalledTreeSafe() => !OperatingSystem.IsWindows() || _locator.CurrentlyInstalledVersion is not null &&
        InstalledCodeInventory.Check(Path.Combine(_locator.RootAppDir!, "current"), "BusChord", _locator.CurrentlyInstalledVersion.ToString(), _rid).Safe;
    public async Task RetryPendingUpdateAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { _source.ClearApplyFailureForUserRetry(); }
        finally { _gate.Release(); }
    }
    public void Dispose() { _source.Dispose(); _gate.Dispose(); }
}
