using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private readonly Func<string, IM112CommissioningBackend>? _m112BackendFactory;
    private M112CommissioningSession? _m112Session;
    private M112CommissioningWindow? _m112Window;
    private bool _m112ClosingOwner;
    public Task M112CloseCompletion => _m112Session?.Completion ?? Task.CompletedTask;
    private bool M112Busy => _m112Session?.IsBusy == true;
    private async Task ShowM112Async()
    {
        if (_realProgrammingWindow is { } queue) { queue.Activate(); return; }
        if (_addressAssignmentWindow is { } addressing) { addressing.Activate(); return; }
        if (_m112Window is not null) { _m112Window.Activate(); return; }
        EnsureSessions();
        var selected = ViewModel.SelectedDevice;
        var recoveryOnly = ViewModel.Project is null || selected is null;
        if (!recoveryOnly && HasParameterDrafts || _sessions?.Programming.IsRunning == true || _sessions?.LineScan.IsRunning == true)
        { await ConfirmDialog.ShowMessageAsync(this, M112CommissioningStrings.Get("Title"), M112CommissioningStrings.Get("Stale")); return; }
        var lifetime = ViewModel.ProjectSessionIdentity;
        var deviceId = selected?.Device.Id ?? "";
        var session = _m112Session = new(() => new(ViewModel.Project,
            ViewModel.ProjectSessionIdentity, HasParameterDrafts, _sessions?.Programming.IsRunning == true), deviceId,
            _m112BackendFactory?.Invoke(deviceId), recoveryOnly);
        session.Changed += snapshot =>
        {
            if (snapshot.Phase is not (M112CommissioningPhase.Applying or M112CommissioningPhase.Restoring)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity) && ReferenceEquals(_m112Session, session)
                    && _sessions is { } current && current.Programming.DeviceStates.ContainsKey(deviceId)) current.Programming.InvalidateM112Flags(deviceId);
            });
        };
        var window = _m112Window = new(session, LoadM112BaselineAsync, _liveOptions, PrepareM112NetworkAsync, ViewModel.Project?.Name ?? "",
            Services.WorkbenchCredentialVaultOwner.ProjectScope(ViewModel.Project, ViewModel.ProjectPath), _credentialVaultOwner);
        var others = _toolWindows.Concat(_companions.Values).Distinct().Select(other => (other, other.IsEnabled)).ToArray();
        foreach (var (other, _) in others) other.IsEnabled = false;
        try { await window.ShowDialog(this); await window.OperationCompletion; }
        finally
        {
            await session.DisposeAsync();
            foreach (var (other, enabled) in others) other.IsEnabled = enabled;
            if (ReferenceEquals(_m112Session, session)) _m112Session = null;
            if (ReferenceEquals(_m112Window, window)) _m112Window = null;
            if (ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity))
                ViewModel.Status = M112CommissioningStrings.Get(session.Snapshot.Phase.ToString())
                    + (session.Snapshot.Result?.RecoveryPath is { } path ? " " + path : "");
        }
    }
    private async Task PrepareM112NetworkAsync()
    {
        if (_sessions?.LineScan.IsRunning == true || _sessions?.Programming.IsRunning == true) throw new InvalidOperationException("Occupied");
        _sessions?.Monitor.Stop();
        if (_liveConnection is { } connection) await connection.DisconnectAsync();
        await Task.WhenAll(_pendingSessionDisposals.ToArray());
    }
    private async Task<M112BaselineSelection?> LoadM112BaselineAsync(Window owner)
    {
        var selected = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = M112CommissioningStrings.Get("Baseline"), AllowMultiple = false,
            FileTypeFilter = [new(W("KnxProjectFile")) { Patterns = ["*.knxproj"] }] });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is not { } path) return null;
        path = M112PrivatePaths.ExistingFile(path);
        var hash = await M112PrivatePaths.HashAsync(path);
        string? password = null;
        try
        {
            Project baseline;
            while (true)
            {
                try { baseline = await Task.Run(() => Knxproj.Load(path, password)); break; }
                catch (InvalidDataException error) when (Knxproj.IsPasswordError(error))
                { password = await PromptPasswordAsync(W("PasswordPrompt"), owner: owner); if (password is null) return null; }
            }
            if (await M112PrivatePaths.HashAsync(path) != hash) throw new IOException("SourceChanged");
            return new(baseline, path, hash);
        }
        finally { password = null; }
    }
    private async Task DrainM112BeforeReplacementAsync()
    {
        var session = _m112Session; var window = _m112Window;
        if (session is null) return;
        session.Cancel(); window?.Close();
        await session.Completion;
        if (window is not null) await window.OperationCompletion;
        if (window is { MayClose: false }) throw new InvalidOperationException(M112CommissioningStrings.Get("RecoveryRequired"));
    }
    private void TrackM112Disposal()
    {
        if (_m112Session is not { } session) return;
        session.Invalidate(); _m112Window?.Close();
        var completion = session.DisposeAsync().AsTask();
        if (!completion.IsCompletedSuccessfully) _pendingSessionDisposals.Add(completion);
    }
    private bool JoinM112BeforeOwnerClose(WindowClosingEventArgs args)
    {
        if (!M112Busy && (_m112Window?.OperationCompletion.IsCompleted ?? true) && _m112Window is { MayClose: false } blocked)
        {
            args.Cancel = true; _shuttingDown = false;
            ViewModel.Status = M112CommissioningStrings.Get("RecoveryRequired"); blocked.Activate(); return true;
        }
        if (!M112Busy && (_m112Window?.OperationCompletion.IsCompleted ?? true)) return false;
        args.Cancel = true;
        if (_m112ClosingOwner) return true;
        _m112ClosingOwner = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var close = false;
            try { await DrainM112BeforeReplacementAsync(); close = true; }
            catch { _shuttingDown = false; ViewModel.Status = M112CommissioningStrings.Get("RecoveryRequired"); }
            finally { _m112ClosingOwner = false; if (close) Close(); }
        });
        return true;
    }
}
