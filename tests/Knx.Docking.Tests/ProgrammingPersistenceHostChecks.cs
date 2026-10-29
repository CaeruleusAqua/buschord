using System.Reflection;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

internal static class ProgrammingPersistenceHostChecks
{
    private static async Task CloseTestHostAsync(WorkbenchWindow? window)
    {
        if (window is null || !window.IsVisible) return;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
        window.Closed += OnClosed;
        try
        {
            window.RequestShutdown(); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { window.Closed -= OnClosed; }
    }
    private static ProgrammingSession Session(WorkbenchWindow window) => ((WorkbenchSessions)typeof(WorkbenchWindow)
        .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Programming;

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-programming-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL programming host: " + name); Console.WriteLine("PASS programming host: " + name); }
        var path = Path.Combine(root, "project.knxproj"); var savedAs = Path.Combine(root, "saved-as.knxproj");
        var store = new ProgrammingStateStore(Path.Combine(root, "demo-state"));
        var catalogue = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalogue")));
        WorkbenchWindow? first = null, second = null;
        try
        {
            Knxproj.Save(WorkbenchJson.CreateDemo(), path);
            var source = File.ReadAllBytes(path);
            var vm = new MainWindowViewModel(catalogue); vm.CreateProject(Knxproj.Load(path), path);
            first = new(vm, new WorkbenchWorkspace(), Path.Combine(root, "layout.json"), catalogue, store);
            first.Show();
            var device = ProjectQuery.DeviceList(vm.Project!).First();
            var session = Session(first); var job = session.Enqueue(device.Id); await session.RunAsync();
            Check("successful DEMO facts are saved by the real host event", job.Status == ProgrammingJobStatus.Done
                && File.Exists(store.PathFor(ProgrammingStateBinding.Create(vm.Project!, path)!))
                && session.Downloaded.Single().Origin == SessionOrigin.Demo && File.ReadAllBytes(path).SequenceEqual(source));
            var success = session.Downloaded.Single();
            await CloseTestHostAsync(first);
            var reopened = new MainWindowViewModel(catalogue); reopened.CreateProject(Knxproj.Load(path), path);
            second = new(reopened, new WorkbenchWorkspace(), Path.Combine(root, "layout.json"), catalogue, store);
            second.Show();
            var restored = Session(second);
            Check("a new window/session restores only the saved DEMO baseline", !ReferenceEquals(session, restored)
                && restored.Downloaded.Single() == success && !restored.NeedsProgramming(device.Id)
                && restored.Downloaded.All(s => s.Origin == SessionOrigin.Demo));
            reopened.Edit("label only", p => p.Name += " renamed"); reopened.UndoCommand.Execute(null); second.RefreshWorkspace();
            Check("undo retains the independent session and downloaded timestamp", ReferenceEquals(restored, Session(second))
                && restored.Downloaded.Single() == success && !restored.NeedsProgramming(device.Id));
            reopened.SaveProjectAs(savedAs); second.RefreshWorkspace();
            Check("Save As persists the same DEMO facts under the new project path", store.TryLoad(ProgrammingStateBinding.Create(reopened.Project!, savedAs)!, out _, out _)
                && File.Exists(store.PathFor(ProgrammingStateBinding.Create(reopened.Project!, path)!)));
            reopened.CreateProject(WorkbenchJson.CreateDemo(), null); second.RefreshWorkspace();
            Check("a fresh unsaved project with equal id receives a fresh comparison session", !ReferenceEquals(restored, Session(second))
                && Session(second).Downloaded.Count == 0);
            await CloseTestHostAsync(second);
            Check("test hosts are closed before the next floating-window scenario", !first.IsVisible && !second.IsVisible);
            Check("closing session hosts leaves no temporary state files", Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0);
            await CheckOverlappingReplacement(root, catalogue, Check);
        }
        finally
        {
            await CloseTestHostAsync(first); await CloseTestHostAsync(second);
            await Task.Delay(100);
            Directory.Delete(root, true);
        }
    }

    private static async Task CheckOverlappingReplacement(string root, CatalogLibrary catalogue, Action<string, bool> check)
    {
        var path = Path.Combine(root, "overlap.knxproj"); Knxproj.Save(WorkbenchJson.CreateDemo(), path);
        var directory = Path.Combine(root, "blocked-state"); File.WriteAllText(directory, "block writes");
        var store = new ProgrammingStateStore(directory);
        var vm = new MainWindowViewModel(catalogue); vm.CreateProject(Knxproj.Load(path), path);
        var window = new WorkbenchWindow(vm, new WorkbenchWorkspace(), Path.Combine(root, "overlap-layout.json"), catalogue, store);
        var disposal = typeof(WorkbenchWindow).GetMethod("DisposeSessionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Field(string name, object? value) => typeof(WorkbenchWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? closing = null;
        try
        {
            window.Show(); await (Task)disposal.Invoke(window, null)!;
            var wait = false;
            var old = new WorkbenchSessions(() => vm.Project, demoExecutor: async (_, token) =>
            { if (wait) await release.Task; token.ThrowIfCancellationRequested(); });
            var binder = new ProgrammingStatePersistence(old.Programming, store, vm.Project!, path);
            Field("_sessions", old); Field("_programmingPersistence", binder);
            Field("_sessionIdentity", typeof(MainWindowViewModel).GetProperty("ProjectSessionIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
            var device = ProjectQuery.DeviceList(vm.Project!).First();
            old.Programming.Enqueue(device.Id); await old.Programming.RunAsync();
            var prior = old.Programming.DeviceState(device.Id)!;
            wait = true; old.Programming.Enqueue(device.Id); var running = old.Programming.RunAsync();
            File.Delete(directory); Directory.CreateDirectory(directory);
            closing = (Task)disposal.Invoke(window, null)!;
            check("old dirty facts flush before asynchronous cancellation can overlap a new host", !closing.IsCompleted
                && store.TryLoad(ProgrammingStateBinding.Create(vm.Project!, path)!, out var flushed, out _)
                && DeviceProgrammingStates.Parse(flushed!, vm.Project!.Id)[device.Id] == prior);
            vm.CreateProject(Knxproj.Load(path), path); window.RefreshWorkspace();
            var current = Session(window);
            check("same-project recreation restores facts before the old executor finishes", current.DeviceState(device.Id) == prior);
            current.Enqueue(device.Id); await current.RunAsync(); var newer = current.DeviceState(device.Id)!;
            release.SetResult(); await running; await closing;
            check("old cancellation cannot overwrite the replacement session's newer comparison", newer.Attempts == prior.Attempts + 1
                && store.TryLoad(ProgrammingStateBinding.Create(vm.Project!, path)!, out var saved, out _)
                && DeviceProgrammingStates.Parse(saved!, vm.Project!.Id)[device.Id] == newer);
        }
        finally
        {
            release.TrySetResult(); if (closing is not null) await closing;
            await CloseTestHostAsync(window);
        }
    }
}
