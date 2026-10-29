using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private RealM112ProgrammingQueue? _realProgrammingQueue;
    private RealProgrammingQueueWindow? _realProgrammingWindow;
    private bool _realProgrammingClosingOwner;
    private Task _realProgrammingOpening = Task.CompletedTask;
    private bool RealProgrammingBusy => _realProgrammingQueue?.IsBusy == true;
    public Task RealProgrammingCloseCompletion => _realProgrammingQueue?.Completion ?? Task.CompletedTask;
    private Control CreateProgrammingView()
    {
        var real = new StackPanel { Margin = new(18), Spacing = 12 };
        real.Children.Add(Note(RealProgrammingQueueStrings.Get("Scope")));
        var open = ActionButton(RealProgrammingQueueStrings.Get("Open"), () => _ = GuardAsync(ShowRealProgrammingAsync), "primary");
        open.Name = "OpenRealProgrammingQueue"; open.HorizontalAlignment = HorizontalAlignment.Left; real.Children.Add(open);
        return new TabControl
        {
            Name = "ProgrammingModes", SelectedIndex = 0,
            ItemsSource = new[] { new TabItem { Header = RealProgrammingQueueStrings.Get("Simulation"), Content = CreateSimulatedProgrammingView() },
                new TabItem { Header = RealProgrammingQueueStrings.Get("RealMode"), Content = real } }
        };
    }
    private Task ShowRealProgrammingAsync()
    {
        if (_realProgrammingWindow is { } existing) { existing.Activate(); return Task.CompletedTask; }
        if (!_realProgrammingOpening.IsCompleted) return _realProgrammingOpening;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _realProgrammingOpening = completion.Task; _pendingSessionDisposals.Add(ObserveRealProgrammingOpeningAsync(completion.Task));
        _ = RunRealProgrammingOwnedAsync(completion); return completion.Task;
    }
    private async Task RunRealProgrammingOwnedAsync(TaskCompletionSource completion)
    { try { await ShowRealProgrammingCoreAsync(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } }
    private async Task ObserveRealProgrammingOpeningAsync(Task opening)
    {
        try { await opening; }
        catch when (_realProgrammingQueue is null) { /* Read-only validation failed before an owner existed; I/O is joined, caller still receives its refusal. */ }
    }
    private async Task ShowRealProgrammingCoreAsync()
    {
        if (_realProgrammingWindow is { } existing) { existing.Activate(); return; }
        EnsureSessions();
        if (ViewModel.Project is not { } project || _sessions is not { } sessions) { ViewModel.Status = W("ProgrammingNeedsProject"); return; }
        if (HasParameterDrafts || sessions.Programming.IsRunning || sessions.LineScan.IsRunning || M112Busy || _m112Window is not null)
        { ViewModel.Status = M112CommissioningStrings.Get("Stale"); return; }
        if (ViewModel.IsDirty || string.IsNullOrEmpty(ViewModel.ProjectPath))
        { ViewModel.Status = RealProgrammingQueueStrings.Get("SaveTarget"); return; }
        sessions.Programming.BeginVerifiedM112HistoryRevalidation();
        var lifetime = ViewModel.ProjectSessionIdentity;
        var binding = ProgrammingStateBinding.Create(project, M112PrivatePaths.ExistingFile(ViewModel.ProjectPath))!;
        var sourceHash = await M112PrivatePaths.HashAsync(binding.ProjectPath);
        var storedTarget = await ReadRealProgrammingTargetAsync(binding.ProjectPath);
        if (await M112PrivatePaths.HashAsync(binding.ProjectPath) != sourceHash
            || VerifiedM112ReceiptStore.ModelFingerprint(storedTarget) != VerifiedM112ReceiptStore.ModelFingerprint(project))
            throw new IOException(M112CommissioningStrings.Get("SourceChanged"));
        if (_shuttingDown || _closingSessionsForExit || !ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity)) return;
        var receipts = new VerifiedM112ReceiptStore(Path.Combine(_programmingStateStore.DirectoryPath, "verified-m112"), binding, sourceHash,
            VerifiedM112ReceiptStore.ModelFingerprint(project), _m112BackendFactory is null ? null : (id, path, ct) => _m112BackendFactory(id).InspectRecoveryAsync(path, ct));
        var queue = _realProgrammingQueue = new(() => new(ViewModel.Project, ViewModel.ProjectSessionIdentity, HasParameterDrafts,
            _sessions?.Programming.IsRunning == true || _sessions?.LineScan.IsRunning == true || M112Busy), sessions.Programming, _m112BackendFactory, receipts);
        await queue.ReadHistoryAsync();
        if (_shuttingDown || _closingSessionsForExit || !ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity))
        { await queue.DisposeAsync(); if (ReferenceEquals(_realProgrammingQueue, queue)) _realProgrammingQueue = null; return; }
        var window = _realProgrammingWindow = new(queue, ProjectQuery.DeviceList(project).ToArray(), LoadM112BaselineAsync, _liveOptions, ViewModel.ProjectPath);
        var others = _toolWindows.Concat(_companions.Values).Distinct().Select(other => (other, other.IsEnabled)).ToArray();
        foreach (var (other, _) in others) other.IsEnabled = false;
        try { await window.ShowDialog(this); await window.OperationCompletion; }
        finally
        {
            await queue.DisposeAsync();
            foreach (var (other, enabled) in others) other.IsEnabled = enabled;
            if (ReferenceEquals(_realProgrammingQueue, queue)) _realProgrammingQueue = null;
            if (ReferenceEquals(_realProgrammingWindow, window)) _realProgrammingWindow = null;
            if (ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity)) ViewModel.Status = queue.RecoveryRequired
                ? RealProgrammingQueueStrings.Get("UnknownHardware") : RealProgrammingQueueStrings.Get("Title");
        }
    }
    private async Task<Project> ReadRealProgrammingTargetAsync(string path)
    {
        string? password = null;
        try
        {
            while (true)
            {
                try { return await Task.Run(() => Knxproj.Load(path, password)); }
                catch (InvalidDataException error) when (Knxproj.IsPasswordError(error))
                { password = await PromptPasswordAsync(W("PasswordPrompt"), owner: this); if (password is null) throw new OperationCanceledException(); }
                catch { throw new IOException(M112CommissioningStrings.Get("SourceChanged")); }
            }
        }
        finally { password = null; }
    }
    private async Task DrainRealProgrammingBeforeReplacementAsync()
    {
        var queue = _realProgrammingQueue; var window = _realProgrammingWindow;
        if (queue is null) return;
        queue.Cancel(); window?.Close(); await queue.Completion;
        if (window is not null) await window.OperationCompletion;
        if (window is { MayClose: false }) { window.Activate(); throw new InvalidOperationException(RealProgrammingQueueStrings.Get("UnknownHardware")); }
    }
    private void TrackRealProgrammingDisposal()
    {
        var queue = _realProgrammingQueue; var window = _realProgrammingWindow;
        if (queue is null) return;
        queue.Cancel(); window?.Close();
        var completion = FinishRealProgrammingDisposalAsync(queue, window);
        if (!completion.IsCompletedSuccessfully) _pendingSessionDisposals.Add(completion);
    }
    private static async Task FinishRealProgrammingDisposalAsync(RealM112ProgrammingQueue queue, RealProgrammingQueueWindow? window)
    {
        await queue.Completion;
        if (window is not null) await window.ClosedCompletion;
        await queue.DisposeAsync();
    }
    private bool JoinRealProgrammingBeforeOwnerClose(WindowClosingEventArgs args)
    {
        var window = _realProgrammingWindow;
        if (!RealProgrammingBusy && (window?.OperationCompletion.IsCompleted ?? true) && window is { MayClose: false })
        {
            args.Cancel = true; _shuttingDown = false; ViewModel.Status = RealProgrammingQueueStrings.Get("UnknownHardware"); window.Activate(); return true;
        }
        if (!RealProgrammingBusy && (window?.OperationCompletion.IsCompleted ?? true)) return false;
        args.Cancel = true;
        if (_realProgrammingClosingOwner) return true;
        _realProgrammingClosingOwner = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var close = false;
            try { await DrainRealProgrammingBeforeReplacementAsync(); close = true; }
            catch { _shuttingDown = false; ViewModel.Status = RealProgrammingQueueStrings.Get("UnknownHardware"); }
            finally { _realProgrammingClosingOwner = false; if (close) Close(); }
        }); return true;
    }
}
