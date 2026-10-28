using System.Runtime.InteropServices;
using Knx.Updates;

namespace Knx.App.Services;

public interface IAppUpdateClient : IDisposable
{
    bool Supported { get; }
    event Action<string>? StatusChanged;
    Task CheckAsync(UpdatePolicy policy, CancellationToken token);
    Task<bool> ApplyAsync(UpdatePolicy policy, bool restart, string[] arguments);
    Task RetryAsync(CancellationToken token);
}

/// <summary>One application update lifecycle; UI close never disposes an active transfer.</summary>
public sealed class AppUpdateService : IDisposable
{
    private readonly Func<string, IAppUpdateClient?> _factory;
    private readonly string _preferencesPath;
    private readonly SemaphoreSlim _configuration = new(1);
    private readonly SemaphoreSlim _checks = new(1);
    private CancellationTokenSource? _lifetime = new();
    private Task? _background;
    private IAppUpdateClient? _client;
    private bool _disposed;
    private volatile bool _stopping;
    public static AppUpdateService? Current { get; set; }
    public static string StateDirectory
    {
        get
        {
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new InvalidDataException("The user update-state location is unavailable.");
            return Path.Combine(directory, "BusChord.UpdateState");
        }
    }
    public static bool PackageManaged => OperatingSystem.IsLinux() && File.Exists(Path.Combine(AppContext.BaseDirectory, ".package-managed"));
    public static string RuntimeId => (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsLinux() ? "linux-" : "unsupported-")
        + (RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "x64", _ => "unsupported" });
    public UpdatePreferences Preferences { get; private set; }
    public string Status { get; private set; } = "idle";
    public bool Supported => _client?.Supported == true;
    public bool SuppressApply { get; set; }
    public event EventHandler? Changed;

    public AppUpdateService(string? preferencesPath = null, Func<string, IAppUpdateClient?>? factory = null)
    {
        _preferencesPath = preferencesPath ?? AppUpdatePreferences.FilePath;
        _factory = factory ?? CreateClient;
        try { Preferences = AppUpdatePreferences.Load(_preferencesPath); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Preferences = new(UpdatePolicy.Disabled); Status = "preferences-invalid"; }
        ReplaceClient();
    }

    private static IAppUpdateClient? CreateClient(string channel)
    {
        if (PackageManaged || !ProductionUpdateTrust.Value.Configured || RuntimeId.Contains("unsupported", StringComparison.Ordinal)) return null;
        return new NativeClient(new(ProductionUpdateTrust.Value, RuntimeId, channel, StateDirectory));
    }

    private void ReplaceClient()
    {
        if (_client is not null) { _client.StatusChanged -= OnStatus; _client.Dispose(); }
        try { _client = _factory(Preferences.Channel); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { _client = null; OnStatus("unavailable"); }
        if (_client is not null) _client.StatusChanged += OnStatus;
        if (Status == "preferences-invalid") return;
        OnStatus(PackageManaged ? "package-managed" : !Supported ? "unavailable" : Preferences.Policy == UpdatePolicy.Disabled ? "disabled" : "idle");
    }

    private void OnStatus(string status) { Status = status; Changed?.Invoke(this, EventArgs.Empty); }

    public void Start()
    {
        if (_disposed || _background is not null || Preferences.Policy == UpdatePolicy.Disabled || !Supported) return;
        _stopping = false;
        _lifetime ??= new(); var token = _lifetime.Token;
        _background = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await CheckNowAsync(token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromHours(6), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { OnStatus("failed"); }
        });
    }

    public async Task CheckNowAsync(CancellationToken token = default)
    {
        if (_disposed || _stopping) return;
        if (!Supported) { OnStatus(PackageManaged ? "package-managed" : "unavailable"); return; }
        _lifetime ??= new();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime?.Token ?? CancellationToken.None);
        if (!await _checks.WaitAsync(0, linked.Token).ConfigureAwait(false)) return;
        try
        {
            if (_disposed || _stopping) return;
            await _client!.CheckAsync(Preferences.Policy == UpdatePolicy.Disabled ? UpdatePolicy.CheckOnly : Preferences.Policy, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch { OnStatus("failed"); }
        finally { _checks.Release(); }
    }

    public async Task SaveAsync(UpdatePreferences preferences)
    {
        await _configuration.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            AppUpdatePreferences.Save(preferences, _preferencesPath);
            Preferences = preferences;
            Status = "idle";
            ReplaceClient();
            _stopping = false;
        }
        finally { if (!_disposed) { _stopping = false; Start(); } _configuration.Release(); }
    }

    public async Task StopAsync()
    {
        await _configuration.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _configuration.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _lifetime?.Cancel();
        if (_background is { } task) await task.ConfigureAwait(false);
        await _checks.WaitAsync().ConfigureAwait(false);
        _checks.Release();
        _background = null;
        _lifetime?.Dispose(); _lifetime = null;
    }

    public async Task<bool> TryApplyAsync(bool restart, string[] arguments)
    {
        await _configuration.WaitAsync().ConfigureAwait(false);
        try
        {
            if (SuppressApply || _disposed || _client is null || Preferences.Policy != UpdatePolicy.Automatic) return false;
            await StopCoreAsync().ConfigureAwait(false);
            return await _client.ApplyAsync(Preferences.Policy, restart, arguments).ConfigureAwait(false);
        }
        finally { _configuration.Release(); }
    }

    public async Task RetryAsync(CancellationToken token = default)
    {
        await _configuration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed || _client is null || !Supported) return;
            await StopCoreAsync().ConfigureAwait(false);
            await _client.RetryAsync(token).ConfigureAwait(false);
            OnStatus("idle");
        }
        finally
        {
            if (!_disposed) { _stopping = false; Start(); }
            _configuration.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configuration.Wait();
        try
        {
            StopCoreAsync().GetAwaiter().GetResult();
            if (_client is not null) { _client.StatusChanged -= OnStatus; _client.Dispose(); }
        }
        finally { _configuration.Release(); }
        _configuration.Dispose(); _checks.Dispose();
    }

    private sealed class NativeClient(AutomaticUpdates updater) : IAppUpdateClient
    {
        public bool Supported => updater.Supported;
        public event Action<string>? StatusChanged { add => updater.StatusChanged += value; remove => updater.StatusChanged -= value; }
        public Task CheckAsync(UpdatePolicy policy, CancellationToken token) => updater.CheckAndDownloadAsync(policy, token);
        public Task<bool> ApplyAsync(UpdatePolicy policy, bool restart, string[] arguments) => updater.TryApplyAtSafeBoundaryAsync(policy, restart, arguments);
        public Task RetryAsync(CancellationToken token) => updater.RetryPendingUpdateAsync(token);
        public void Dispose() => updater.Dispose();
    }
}
