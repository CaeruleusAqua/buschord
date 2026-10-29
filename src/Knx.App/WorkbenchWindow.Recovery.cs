using Knx.App.Localization;
using Knx.App.Services;
using Knx.Recovery;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private ProjectRecoveryController? _recovery;
    private Task? _recoveryCloseCompletion;
    private bool _recoveryCloseApproved;
    public Task RecoveryCloseCompletion => _recoveryCloseCompletion ?? Task.CompletedTask;
    public ProjectRecoveryController AttachRecovery(ProjectRecoveryRecorder recorder, WorkbenchRecoveryStore store,
        IRecoveryDependencyResolver? dependencies = null, Func<Project, RecoveryDependencyInventory>? dependencyRecorder = null)
    {
        if (_recovery is not null) throw new InvalidOperationException("Recovery.ControllerAlreadyAttached");
        return _recovery = new(ViewModel, recorder, store, new RecoveryDispatcher(), () => HasParameterDrafts,
            () => _sessions?.Programming.IsRunning == true, dependencies, dependencyRecorder);
    }
    /// <summary>Root awaits this after the existing close gates and before closing the dispatcher/app lease.</summary>
    public Task DrainRecoveryForCloseAsync() => _recovery?.CloseAndDrainAsync() ?? Task.CompletedTask;
    public async Task BindRecoveryCurrentProjectAsync(RecoverySourceStamp? loadedSource = null, string? unsupportedReason = null)
    {
        if (_recovery is null || ViewModel.Project is not { } project) return;
        _recovery.InvalidateBinding();
        var enabled = AppProjectRecoveryService.Current?.Enabled ?? _recovery.RecordingEnabled;
        if (ViewModel.ProjectPath.Length > 0 && (!enabled || unsupportedReason is not null))
        { _recovery.UnbindForUnsupportedProject(unsupportedReason ?? "Recovery.Disabled"); return; }
        var session = ViewModel.ProjectSessionIdentity;
        var revision = _recovery.CommitRevision;
        try
        {
            var source = ViewModel.ProjectPath.Length == 0 ? ProjectRecoveryController.Unsaved(project)
                : (loadedSource ?? await (AppProjectRecoveryService.Current?.SourceStamps ?? new RecoverySourceStampReader()).ReadAsync(ViewModel.ProjectPath)).Bind(project);
            if (!ReferenceEquals(session, ViewModel.ProjectSessionIdentity) || revision != _recovery.CommitRevision) throw new IOException("Recovery.StaleSession");
            _recovery.SetRecordingEnabled(enabled); await _recovery.BindAsync(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _recovery.UnbindForUnsupportedProject(error is RecoveryRejectedException rejected ? rejected.Code : "Recovery.SourceChanged"); ViewModel.Status = RecoveryUiStrings.Get(_recovery.StatusCode); }
    }
    private async Task<(RecoverySourceStamp? Stamp, string? Reason)> TryRecoverySourceStampAsync(string path)
    {
        if (_recovery is null || !(AppProjectRecoveryService.Current?.Enabled ?? _recovery.RecordingEnabled)) return (null, null);
        try { return (await (AppProjectRecoveryService.Current?.SourceStamps ?? new RecoverySourceStampReader()).ReadAsync(path), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return (null, error is RecoveryRejectedException rejected ? rejected.Code : "Recovery.LimitExceeded"); }
    }
    private IDisposable FreezeReplacementEditors()
    {
        var windows = new[] { this }.Concat(_toolWindows).Concat(_companions.Values).Distinct().Select(window => (window, window.IsEnabled)).ToArray();
        foreach (var (window, _) in windows) window.IsEnabled = false;
        return new RestoreEditors(() => { foreach (var (window, enabled) in windows) window.IsEnabled = enabled; });
    }
    private sealed class RestoreEditors(Action restore) : IDisposable { public void Dispose() => restore(); }
    private async Task DrainRecoveryBeforeReplacementAsync()
    {
        if (_recovery is null) return;
        try { await _recovery.DrainAsync(); }
        catch (IOException) { ViewModel.Status = RecoveryUiStrings.Get("Failed"); }
    }
    private string RecoveryAwareStatus(string normal) => _recovery is { RecordingEnabled: false } && AppProjectRecoveryService.Current?.Enabled == true
        ? normal + " " + RecoveryUiStrings.Get(_recovery.StatusCode) : normal;
    internal Task<bool> OpenStartupProjectAsync(string path) => OpenPathAsync(path);
    private async Task<bool> AskCloseDespiteRecoveryFailureAsync()
    {
        var dialog = CreateDialog(RecoveryUiStrings.Get("Title"), 520, 300);
        var close = false;
        var panel = new Avalonia.Controls.StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        panel.Children.Add(new Avalonia.Controls.TextBlock { Text = RecoveryUiStrings.Get("CloseFailed"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var cancel = ActionButton(RecoveryUiStrings.Get("KeepOpen"), () => dialog.Close(), "quiet", isDefault: true);
        cancel.IsCancel = true; cancel.Name = "RecoveryCloseCancel";
        var proceed = ActionButton(RecoveryUiStrings.Get("CloseAnyway"), () => { close = true; dialog.Close(); }, "danger");
        proceed.Name = "RecoveryCloseAnyway";
        dialog.Content = DialogLayout(panel, DialogActions(cancel, proceed));
        await dialog.ShowDialog(this); return close;
    }
    private async Task FinishRecoveryCloseAsync()
    {
        var session = ViewModel.ProjectSessionIdentity; var revision = _recovery!.CommitRevision;
        var proceed = true;
        try { await DrainRecoveryForCloseAsync(); }
        catch (Exception)
        {
            ViewModel.Status = RecoveryUiStrings.Get("Failed");
            proceed = await AskCloseDespiteRecoveryFailureAsync();
        }
        if (!proceed || !ReferenceEquals(session, ViewModel.ProjectSessionIdentity) || revision != _recovery.CommitRevision || HasParameterDrafts)
        {
            _recovery.CancelCloseAttempt(); _recoveryCloseCompletion = null; _closingSessionsForExit = false; _shuttingDown = false; IsEnabled = true; return;
        }
        _recoveryCloseApproved = true; _closingSessionsForExit = false; _shuttingDown = true; IsEnabled = true; Close();
    }
    private async Task ShowRecoveryAsync()
    {
        if (_recovery is null) { ViewModel.Status = RecoveryUiStrings.Get("Recovery.NotBound"); return; }
        var window = new RecoveryWindow(_recovery, ConfirmDiscardAsync, async () =>
        {
            await ResetSessionsForReplacementAsync(); RefreshWorkspace();
            ViewModel.Status = RecoveryUiStrings.Get(_recovery.Source?.Kind == RecoveryBindingKind.UnsavedProject ? "UnsavedSaveAs" : "Recovery.Restored");
        }, AppProjectRecoveryService.Current);
        await window.ShowDialog(this);
        await window.OperationCompletion;
    }
}
