using System.Security.Cryptography;
using System.Text.Json;
using Knx.Recovery;

namespace Knx.App.Services;

/// <summary>One local recovery writer per process. Startup creates it off the dispatcher; shutdown awaits it before releasing the app lease.</summary>
public sealed class AppProjectRecoveryService : IAsyncDisposable
{
    public static AppProjectRecoveryService? Current { get; set; }
    public WorkbenchRecoveryStore Store { get; }
    public ProjectRecoveryRecorder Recorder { get; }
    public IRecoverySourceStampReader SourceStamps { get; }
    private readonly Dictionary<WorkbenchWindow, ProjectRecoveryController> _windows = [];
    private readonly HashSet<Task> _closedDisposals = [];
    private readonly string? _preferences;
    private readonly IRecoveryDispatcher _ui;
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    public bool Enabled { get; private set; }
    public event EventHandler? Changed;
    public bool ShutdownFailed { get; private set; }
    public AppProjectRecoveryService(WorkbenchRecoveryStore store, string? preferences = null, IRecoveryDispatcher? ui = null, IRecoverySourceStampReader? sourceStamps = null)
    {
        Store = store; Recorder = new(new ProjectRecoveryPersistence(store)); _preferences = preferences; _ui = ui ?? new RecoveryDispatcher(); SourceStamps = sourceStamps ?? new RecoverySourceStampReader();
        if (preferences is not null)
        {
            try { if (new FileInfo(preferences).Length > 4096) throw new InvalidDataException(); using var json = JsonDocument.Parse(File.ReadAllText(preferences)); Enabled = json.RootElement.GetProperty("Schema").GetInt32() == 1 && json.RootElement.GetProperty("Enabled").GetBoolean(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { Enabled = false; }
        }
    }
    public static Task<AppProjectRecoveryService> CreateAsync(string? directory = null, string? preferences = null) => Task.Run(() =>
        new AppProjectRecoveryService(new WorkbenchRecoveryStore(directory), preferences ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusChord.UserData", "recovery-preferences.json")));
    public ProjectRecoveryController Attach(WorkbenchWindow window)
    {
        if (_windows.TryGetValue(window, out var existing)) return existing;
        var controller = window.AttachRecovery(Recorder, Store); controller.SetRecordingEnabled(Enabled); _windows.Add(window, controller);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            var task = controller.DisposeAsync().AsTask(); lock (_closedDisposals) _closedDisposals.Add(task);
            _ = ObserveClosedAsync(task);
        };
        return controller;
    }
    private async Task ObserveClosedAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch { ShutdownFailed = true; } finally { lock (_closedDisposals) _closedDisposals.Remove(task); } }
    public async Task SetEnabledAsync(bool enabled)
    {
        if (_preferences is null) throw new IOException("Recovery.PreferencesUnavailable");
        await _policyGate.WaitAsync();
        try
        {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_preferences))!);
            var temporary = _preferences + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, new { Schema = 1, Enabled = enabled }); file.Flush(true); }
                File.Move(temporary, _preferences, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        await _ui.InvokeAsync(() => { Enabled = enabled; foreach (var controller in _windows.Values) controller.SetRecordingEnabled(enabled); Changed?.Invoke(this, EventArgs.Empty); return true; });
        }
        finally { _policyGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var controller in _windows.Values)
        {
            try { await controller.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { ShutdownFailed = true; }
        }
        Task[] closed; lock (_closedDisposals) closed = _closedDisposals.ToArray();
        try { await Task.WhenAll(closed).ConfigureAwait(false); } catch { ShutdownFailed = true; }
        try { await Recorder.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { ShutdownFailed = true; }
        if (ShutdownFailed) Console.Error.WriteLine("Recovery.WriterFailed");
        if (ReferenceEquals(Current, this)) Current = null;
        _policyGate.Dispose();
    }
}

public interface IRecoverySourceStampReader { Task<RecoverySourceStamp> ReadAsync(string path); }
public sealed class RecoverySourceStampReader : IRecoverySourceStampReader
{ public Task<RecoverySourceStamp> ReadAsync(string path) => RecoverySourceStamp.ReadAsync(path); }

/// <summary>Hash before a successful load; verify before adoption. Never opens a path discovered in recovery metadata.</summary>
public sealed record RecoverySourceStamp(string Path, RecoveryBlob File)
{
    public static Task<RecoverySourceStamp> ReadAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        path = System.IO.Path.GetFullPath(path);
        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery.SourceChanged");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length <= 0 || file.Length > 128L * 1024 * 1024) throw new IOException("Recovery.LimitExceeded");
        return new RecoverySourceStamp(path, new(Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), file.Length));
    }, token);
    public RecoverySource Bind(Project project) => new(RecoveryBindingKind.SavedArchive, project.Id, project.Guid, Path,
        RecoveryContractValidation.PathIdentity(Path), File, null);
    public async Task VerifyAsync(CancellationToken token = default)
    { if (await ReadAsync(Path, token).ConfigureAwait(false) != this) throw new IOException("Recovery.SourceChanged"); }
}
