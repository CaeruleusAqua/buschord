using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Catalog;

internal static class LiveConnectionNativeChecks
{
    [STAThread]
    public static int Run(string language)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-live-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(root, "shared-catalog")));
        var backends = new List<NativeBackend>(); NativeBackend? next = null;
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        App.ForcedLanguage = language; App.ForcedTheme = "light";
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
            var vm = new MainWindowViewModel(catalog); vm.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null);
            return new WorkbenchWindow(vm, new(), Path.Combine(root, "layout.json"), catalog,
                new ProgrammingStateStore(Path.Combine(root, "programming")), _ =>
                { var backend = next ?? new NativeBackend(); next = null; backends.Add(backend); return backend; });
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var shell = (WorkbenchWindow)lifetime.MainWindow!;
            try
            {
                var opening = Open(shell); await Until(() => lifetime.Windows.OfType<ConnectionWindow>().Any());
                var dialog = lifetime.Windows.OfType<ConnectionWindow>().Single();
                var gateway = Find<TextBox>(dialog, "LiveGatewayAddress"); gateway.Text = "192.0.2.190";
                Check("native gateway and tunnel controls have accessible names", ControlAutomationPeer.CreatePeerForElement(gateway)?.GetName() == LiveConnectionStrings.Get("Gateway")
                    && ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(dialog, "LiveTunnelAddress"))?.GetName() == LiveConnectionStrings.Get("TunnelAddress"));
                Check("native connection uses localized shared dialog chrome", dialog.Title == LiveConnectionStrings.Get("Title") && dialog.Classes.Contains("wb-dialog"));
                Click(Find<Button>(dialog, "LiveConnect")); await Until(() => Owner(shell).IsConnected);
                Check("native explicit connect changes controls and readonly state", !Find<Button>(dialog, "LiveConnect").IsEnabled
                    && Find<Button>(dialog, "LiveDisconnect").IsEnabled && dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(LiveConnectionStrings.Get("ReadOnly")) == true));
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current.RequestedThemeVariant = theme; await Task.Delay(70);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
                    bitmap.Render(dialog); bitmap.Save(Path.Combine(root, $"connection-{language}-{theme}.png"), PngBitmapEncoderOptions.Default);
                    Check("native connection remains usable in " + theme, Find<Button>(dialog, "LiveDisconnect").IsEnabled && dialog.Bounds.Width >= 400);
                }
                dialog.Close(); await opening; shell.ShowTool("monitor"); await Task.Delay(70);
                Check("native chrome never labels connected real tunnel offline", Find<TextBlock>(shell, "ConnectionBadge").Text == LiveConnectionStrings.Get("ReadOnly")
                    && Find<TextBlock>(shell, "ConnectionFooter").Text == LiveConnectionStrings.Get("ReadOnly"));
                Check("explicit connection selects real source instead of hidden demo fallback", Find<ComboBox>(shell, "MonitorSourceChoice").SelectedIndex == 1);
                Check("native real monitor disables demo value operations", !shell.GetVisualDescendants().OfType<Button>()
                    .First(b => Equals(b.Content, WorkbenchStrings.Get("MonitorValue"))).IsEnabled);
                var start = shell.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, WorkbenchStrings.Get("Start")));
                Click(start); backends[0].Send(); await Task.Delay(140);
                var sessions = Sessions(shell);
                Check("native monitor records real traffic through existing session", sessions.Monitor.RecordedCount == 1 && sessions.Monitor.Rows.Single().Origin == SessionOrigin.Real
                    && shell.ViewModel.Recorder.Count == 0 && shell.ViewModel.Monitor.Count == 0);
                shell.ShowTool("changes"); shell.ShowTool("monitor"); backends[0].Send(); await Task.Delay(140);
                Check("pane recreation preserves real source and recording", Find<ComboBox>(shell, "MonitorSourceChoice").SelectedIndex == 1
                    && sessions.Monitor.Origin == SessionOrigin.Real && sessions.Monitor.RecordedCount == 2);
                using (var monitorImage = new RenderTargetBitmap(new PixelSize((int)shell.Bounds.Width, (int)shell.Bounds.Height)))
                { monitorImage.Render(shell); monitorImage.Save(Path.Combine(root, $"monitor-real-{language}.png"), PngBitmapEncoderOptions.Default); }
                Check("legacy write and download buttons remain policy disabled", !shell.ViewModel.CanSend && !shell.ViewModel.CanDownload);
                var oldReceive = backends[0].ReceiveSnapshot; backends[0].Lose();
                await Until(() => Owner(shell).Snapshot.Phase == ReadOnlyConnectionPhase.Disconnected);
                await Task.Delay(70); oldReceive?.Invoke(Frame());
                Check("native remote loss stops real monitor and updates status", !sessions.Monitor.IsRunning && shell.ViewModel.BusState == BusConnectionState.Disconnected
                    && shell.ViewModel.Status.Contains(LiveConnectionStrings.Get("LinkLost")) && sessions.Monitor.RecordedCount == 2);
                Check("native chrome returns to disconnected state after remote loss", Find<TextBlock>(shell, "ConnectionBadge").Text == LiveConnectionStrings.Get("Disconnected")
                    && Find<TextBlock>(shell, "ConnectionFooter").Text == WorkbenchStrings.Get("StatusLocalOnly"));

                next = new NativeBackend { ConnectGate = Gate(), IgnoreCancellation = true };
                var delayed = next;
                var secondOpening = Open(shell); await Until(() => lifetime.Windows.OfType<ConnectionWindow>().Any());
                var second = lifetime.Windows.OfType<ConnectionWindow>().Single();
                Click(Find<Button>(second, "LiveConnect")); await delayed.Entered.Task;
                Click(Find<Button>(second, "LiveCancel")); await Task.Delay(30);
                Check("native cancel waits for pending transport connect cleanup", Owner(shell).Snapshot.Phase == ReadOnlyConnectionPhase.Disconnecting && delayed.Disposals == 0);
                delayed.ConnectGate!.SetResult(); await Until(() => Owner(shell).Snapshot.Phase == ReadOnlyConnectionPhase.Disconnected);
                Check("native delayed connect cannot undo explicit cancellation", !Owner(shell).IsConnected && delayed.Disposals == 1);
                second.Close(); await secondOpening;

                next = new NativeBackend { ConnectGate = Gate(), IgnoreCancellation = true };
                var replaced = next; var previousOwner = Owner(shell);
                var replacing = Open(shell); await Until(() => lifetime.Windows.OfType<ConnectionWindow>().Any());
                Click(Find<Button>(lifetime.Windows.OfType<ConnectionWindow>().Single(), "LiveConnect")); await replaced.Entered.Task;
                var previousId = shell.ViewModel.Project!.Id;
                shell.ViewModel.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null);
                await Task.Delay(60);
                Check("same-id project replacement invalidates old connection owner", shell.ViewModel.Project!.Id == previousId
                    && !previousOwner.IsConnected && !shell.ConnectionCloseCompletion.IsCompleted);
                replaced.ConnectGate!.SetResult(); await replacing; await shell.ConnectionCloseCompletion; await Task.Delay(50);
                Check("project replacement joins late connection before new owner", replaced.Disposals == 1 && !Owner(shell).IsConnected && !ReferenceEquals(previousOwner, Owner(shell)));

                next = new NativeBackend { DisposeGate = Gate() };
                var closing = next;
                var finalOpening = Open(shell); await Until(() => lifetime.Windows.OfType<ConnectionWindow>().Any());
                var finalDialog = lifetime.Windows.OfType<ConnectionWindow>().Single(); Find<TextBox>(finalDialog, "LiveGatewayAddress").Text = "192.0.2.190";
                Click(Find<Button>(finalDialog, "LiveConnect")); await Until(() => Owner(shell).IsConnected);
                finalDialog.Close(); await finalOpening;
                shell.RequestShutdown(); shell.Close(); await closing.DisposeEntered.Task;
                Check("native close retains window until connection disposal is joined", shell.IsVisible && !shell.ConnectionCloseCompletion.IsCompleted);
                closing.DisposeGate!.SetResult(); await shell.ConnectionCloseCompletion; await Until(() => !shell.IsVisible);
                Check("native close finishes after local transport cleanup", closing.Disposals == 1);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL native live exception: " + error); }
            finally
            {
                foreach (var backend in backends) { backend.ConnectGate?.TrySetResult(); backend.DisposeGate?.TrySetResult(); }
                shell.RequestShutdown(); shell.Close();
                Console.WriteLine($"Native live {language}: {checks} checks, {failures} failures; screenshots {root}");
                lifetime.Shutdown(failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(180))).StartWithClassicDesktopLifetime([]);
        return failures == 0 ? 0 : 1;
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10); if (!condition()) throw new TimeoutException("Native live condition."); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Open(WorkbenchWindow window) => (Task)typeof(WorkbenchWindow).GetMethod("ShowConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static ReadOnlyWorkbenchConnection Owner(WorkbenchWindow window) => (ReadOnlyWorkbenchConnection)typeof(WorkbenchWindow).GetProperty("LiveConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static WorkbenchSessions Sessions(WorkbenchWindow window) => (WorkbenchSessions)typeof(WorkbenchWindow).GetProperty("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static BusTelegram Frame() => new() { Source = "synthetic native readonly", Telegram = new()
    { Source = new(1,1,1), Destination = new(0x0101), IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = [1] } };
    private sealed class NativeBackend : IReadOnlyWorkbenchBackend
    {
        private Action<BusTelegram>? _receive;
        private Action<BusConnectionState>? _state;
        public TaskCompletionSource? ConnectGate { get; init; }
        public TaskCompletionSource? DisposeGate { get; init; }
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource DisposeEntered { get; } = Gate();
        public bool IgnoreCancellation { get; init; }
        public bool IsConnected { get; private set; }
        public int Disposals { get; private set; }
        public IndividualAddress AssignedAddress => new(1,0,251);
        public Action<BusTelegram>? ReceiveSnapshot => _receive;
        public event Action<BusTelegram> TelegramReceived { add => _receive += value; remove => _receive -= value; }
        public event Action<BusConnectionState> StateChanged { add => _state += value; remove => _state -= value; }
        public async Task ConnectAsync(CancellationToken ct)
        { Entered.TrySetResult(); if (ConnectGate is not null) await (IgnoreCancellation ? ConnectGate.Task : ConnectGate.Task.WaitAsync(ct)); IsConnected = true; }
        public void Send() => _receive?.Invoke(Frame());
        public void Lose() { IsConnected = false; _state?.Invoke(BusConnectionState.Disconnected); }
        public async ValueTask DisposeAsync()
        { DisposeEntered.TrySetResult(); if (DisposeGate is not null) await DisposeGate.Task; Disposals++; IsConnected = false; }
    }
}
