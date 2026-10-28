using System.Reflection;
using Avalonia.Threading;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Updates;
using Knx.Workbench;

internal static class UpdateShutdownChecks
{
    internal static async Task Run(WorkbenchWindow owner, string theme, Action<string, bool> check)
    {
        foreach (var alreadyDetached in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "buschord-shutdown-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
            var vm = new MainWindowViewModel(catalog);
            vm.CreateProject(WorkbenchJson.CreateDemo(), null);
            var window = new WorkbenchWindow(vm, new WorkbenchWorkspace(), catalogLibrary: catalog,
                programmingStateStore: new ProgrammingStateStore(Path.Combine(root, "programming")));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = false;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IDisposable? appLease = UpdateProcessLease.EnterApplication(Path.Combine(root, "updates"));
            window.Closed += (_, _) => { appLease?.Dispose(); appLease = null; closed.TrySetResult(); };
            var detach = typeof(WorkbenchWindow).GetMethod("DisposeSessionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            WorkbenchSessions? sessions = null;
            try
            {
                window.Show(owner);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await (Task)detach.Invoke(window, [])!;
                sessions = new WorkbenchSessions(() => vm.Project, demoExecutor: async (_, token) =>
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { cancelled.TrySetResult(); await release.Task; throw; }
                    finally { ended = true; }
                });
                typeof(WorkbenchWindow).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, sessions);
                var job = sessions.Programming.Enqueue(ProjectQuery.DeviceList(vm.Project!).First().Id);
                var run = sessions.Programming.RunAsync();
                if (alreadyDetached) _ = (Task)detach.Invoke(window, [])!;
                window.Close();
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.Close();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var label = theme + " update shutdown " + (alreadyDetached ? "previous session: " : "active session: ");
                check(label + "native close waits for cancellation without blocking the dispatcher",
                    window.IsVisible && !window.IsEnabled && !closed.Task.IsCompleted && !ended);
                using (var installer = UpdateProcessLease.TryEnterInstaller(Path.Combine(root, "updates")))
                    check(label + "application lease prevents a swap until session completion", installer is null);
                release.TrySetResult();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(label + "executor and interface reservation end before native close",
                    ended && job.Status == ProgrammingJobStatus.Cancelled && !sessions.Programming.IsRunning
                    && sessions.Interfaces.StatusSnapshot().Count == 0 && !window.IsVisible);
                using (var installer = UpdateProcessLease.TryEnterInstaller(Path.Combine(root, "updates")))
                    check(label + "installer can acquire the boundary after native close", installer is not null);
            }
            finally
            {
                release.TrySetResult();
                if (sessions is not null) await sessions.DisposeAsync();
                if (window.IsVisible) { window.RequestShutdown(); window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                appLease?.Dispose();
                var target = Path.GetFullPath(root);
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(target).StartsWith("buschord-shutdown-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected shutdown-check directory.");
                Directory.Delete(target, true);
            }
        }
    }
}
