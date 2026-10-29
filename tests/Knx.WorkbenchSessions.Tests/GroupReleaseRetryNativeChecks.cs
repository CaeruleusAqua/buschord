using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Catalog;
using Knx.Workbench;
using F = GroupConnectionFixture;

/// <summary>Actual Workbench replacement/disposal hooks over synthetic group capabilities and fake consumers only.</summary>
internal static class GroupReleaseRetryNativeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    internal static int Run()
    {
        var root = Directory.CreateTempSubdirectory("BusChord.Group.ReleaseRetry.").FullName;
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", Private)!.SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(root, "shared-catalog")));
        App.ForcedLanguage = "en"; App.ForcedTheme = "light"; App.TestWindowFactory = () => new Window { Title = "Synthetic group cleanup checks", Width = 400, Height = 200 };
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "GroupRelease " + name); if (!ok) failures++; }
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await Replacement(desktop.MainWindow!, root, 1, false, Check);
                await Replacement(desktop.MainWindow!, root, 2, false, Check);
                await Replacement(desktop.MainWindow!, root, 0, true, Check);
                await CombinedQueue(desktop.MainWindow!, root, Check);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL GroupRelease exception " + error); }
            finally { desktop.MainWindow!.Close(); Console.WriteLine($"GROUP RELEASE RETRY checks={checks}; failures={failures}"); desktop.Shutdown(failures); }
        }, TimeSpan.FromMilliseconds(200))).StartWithClassicDesktopLifetime([]);
        return failures == 0 ? 0 : 1;
    }
    private static async Task Replacement(Window shell, string root, int faults, bool cancelConsumer, Action<string, bool> check)
    {
        using var fixture = new F(); await fixture.Provision(); var integrity = new GroupSelectionChecks.Integrity();
        await using var owner = new WorkbenchGroupSecurityOwner(integrity);
        var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, Guid.NewGuid().ToString("N"))));
        var vm = new MainWindowViewModel(catalog); vm.CreateProject(WorkbenchJson.CreateDemo(), null);
        var window = new WorkbenchWindow(vm, new(), Path.Combine(root, Guid.NewGuid().ToString("N") + ".layout"), catalog,
            new ProgrammingStateStore(Path.Combine(root, Guid.NewGuid().ToString("N"))),
            credentialVaultOwner: new WorkbenchCredentialVaultOwner(() => throw new Exception("No provider in retry test")));
        Set(window, "_groupSecurityOwner", owner); window.Show(shell); window.RefreshWorkspace();
        var lifetime = Lifetime(vm); var candidates = await owner.ReadCandidatesAsync(lifetime, GroupHostConfigurationFixture.Write(fixture), default);
        await owner.CreateSelectionAsync(lifetime, candidates, candidates.Candidates.Single().Id, fixture.Connection(), true, default);
        var heldIntegrity = HeldIntegrity(owner, lifetime);
        check($"registry owns nonzero integrity before replacement faults={faults} cancel={cancelConsumer}", heldIntegrity.SequenceEqual(F.Integrity));
        var backend = new Backend { Failures = faults };
        var connection = new ReadOnlyWorkbenchConnection(lifetime, () => Lifetime(vm), _ => backend);
        Set(window, "_liveConnection", connection);
        await connection.ConnectAsync(new(System.Net.IPAddress.Loopback, 3671, new(1, 1, 250)));
        TaskCompletionSource? pendingConsumer = null;
        if (cancelConsumer)
        {
            pendingConsumer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Field<List<Task>>(window, "_pendingSessionDisposals").Add(pendingConsumer.Task);
        }
        try
        {
            var first = Reset(window);
            if (pendingConsumer is not null)
            {
                check("pending consumer retains project capability before cancellation", !first.IsCompleted && heldIntegrity.Any(value => value != 0));
                pendingConsumer.TrySetCanceled();
            }
            var failed = await Fails(first);
            check($"failed or cancelled replacement retains capability faults={faults} cancel={cancelConsumer}", failed && heldIntegrity.Any(value => value != 0));
            if (faults > 1)
            {
                check("a second failed fresh cleanup still retains capability", await Fails(Reset(window)) && heldIntegrity.Any(value => value != 0));
            }
            await Reset(window);
            check($"successful fresh replacement retry clears old project integrity faults={faults} cancel={cancelConsumer}", heldIntegrity.All(value => value == 0)
                && !backend.IsConnected && backend.SuccessfulDisposals == 1);
            vm.CreateProject(WorkbenchJson.CreateDemo(), null); window.RefreshWorkspace();
            check("replacement proceeds with a new project lifetime", !ReferenceEquals(lifetime, Lifetime(vm)));
        }
        finally
        {
            pendingConsumer?.TrySetResult(); backend.Failures = 0; window.RequestShutdown(); window.Close();
            await window.ConnectionCloseCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    private static Task Reset(WorkbenchWindow window) => (Task)typeof(WorkbenchWindow).GetMethod("ResetSessionsForReplacementAsync", Private)!.Invoke(window, null)!;
    private static async Task CombinedQueue(Window shell, string root, Action<string, bool> check)
    {
        var fixtureType = typeof(GroupReleaseRetryNativeChecks).Assembly.GetType("RealM112ProgrammingQueueChecks+Fixture");
        if (fixtureType is null) { Console.WriteLine("NOTE actual queue/group close is checked separately on the pinned combined dependency."); return; }
        dynamic queueFixture = Activator.CreateInstance(fixtureType, nonPublic: true)!;
        using var groupFixture = new F(); await groupFixture.Provision(); await using var owner = new WorkbenchGroupSecurityOwner(new GroupSelectionChecks.Integrity());
        var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "queue-combined-catalog")));
        var vm = new MainWindowViewModel(catalog); vm.CreateProject((Project)queueFixture.Project, null);
        var window = new WorkbenchWindow(vm, new(), Path.Combine(root, "queue.layout"), catalog,
            credentialVaultOwner: new WorkbenchCredentialVaultOwner(() => throw new Exception("No provider in combined queue test")));
        Set(window, "_groupSecurityOwner", owner); window.Show(shell); window.RefreshWorkspace(); var lifetime = Lifetime(vm);
        var candidates = await owner.ReadCandidatesAsync(lifetime, GroupHostConfigurationFixture.Write(groupFixture), default);
        await owner.CreateSelectionAsync(lifetime, candidates, candidates.Candidates.Single().Id, groupFixture.Connection(), true, default);
        var held = HeldIntegrity(owner, lifetime);
        try
        {
            await (Task)queueFixture.EnqueueBoth(); await (Task)queueFixture.ReadyBoth();
            queueFixture.Backends[0].WaitForCancellation = true;
            queueFixture.Backends[0].RestoreGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_realProgrammingQueue", (object)queueFixture.Queue);
            Task run = queueFixture.Queue.RunAsync((string)queueFixture.Queue.Snapshot.BatchFingerprint);
            await ((Task)queueFixture.Backends[0].Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            window.RequestShutdown(); window.Close();
            await ((Task)queueFixture.Backends[0].RestoreEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            check("combined owner close retains group integrity while real queue recovery is held", window.IsVisible && !run.IsCompleted && held.Any(value => value != 0));
            queueFixture.Backends[0].RestoreGate.TrySetResult(); await run;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.IsVisible) await Task.Delay(10, timeout.Token);
            await window.ConnectionCloseCompletion;
            check("combined queue recovery and group release join without a close deadlock", held.All(value => value == 0)
                && (int)queueFixture.Backends[1].Applies == 0 && (bool)queueFixture.Queue.Completion.IsCompleted);
        }
        finally
        {
            queueFixture.Backends[0].RestoreGate?.TrySetResult(); queueFixture.Queue.Cancel(); await (Task)queueFixture.Queue.Completion;
            window.RequestShutdown(); window.Close(); await window.ConnectionCloseCompletion;
            await ((IAsyncDisposable)queueFixture).DisposeAsync();
        }
    }
    private static async Task<bool> Fails(Task task) { try { await task; return false; } catch (IOException) { return true; } catch (OperationCanceledException) { return true; } }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object Lifetime(MainWindowViewModel vm) => vm.GetType().GetProperty("ProjectSessionIdentity", Private)!.GetValue(vm)!;
    private static void Set(object value, string name, object content) => value.GetType().GetField(name, Private)!.SetValue(value, content);
    private static byte[] HeldIntegrity(WorkbenchGroupSecurityOwner owner, object lifetime)
    {
        var state = Field<IDictionary>(owner, "_projects")[lifetime]!;
        var registry = Field<IDictionary>(state, "Registries").Values.Cast<object>().Single();
        var cached = Field<IDictionary>(registry, "_cache").Values.Cast<object>().Single();
        var source = cached.GetType().GetField("Item2")!.GetValue(cached)!;
        return Field<byte[]>(source, "_integrity");
    }
    private sealed class Backend : IReadOnlyWorkbenchBackend
    {
        internal int Failures, SuccessfulDisposals;
        public bool IsConnected { get; private set; }
        public bool OwnsGatewayReservation => true;
        public IndividualAddress AssignedAddress => new(1, 1, 250);
        public event Action<BusTelegram>? TelegramReceived { add { } remove { } }
        public event Action<BusConnectionState>? StateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); IsConnected = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            if (Failures-- > 0) return ValueTask.FromException(new IOException("Synthetic retryable consumer cleanup failure"));
            IsConnected = false; SuccessfulDisposals++; return ValueTask.CompletedTask;
        }
    }
}
