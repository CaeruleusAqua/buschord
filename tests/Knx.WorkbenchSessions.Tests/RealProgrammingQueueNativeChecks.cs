using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;

/// <summary>Native UI driven solely by injected M112 fakes; no network or OS credential provider.</summary>
internal static class RealProgrammingQueueNativeChecks
{
    [STAThread]
    internal static int Run(string language)
    {
        var f = new RealM112ProgrammingQueueChecks.Fixture(); var checks = 0; var failures = 0; var vaultCreations = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(f.Root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(f.Root, "shared-catalog")));
        App.ForcedLanguage = language; App.ForcedTheme = "light";
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(f.Root, "catalog")));
            var vm = new MainWindowViewModel(catalog); vm.CreateProject(f.Project, Path.Combine(f.Root, "target.knxproj"));
            var vault = new WorkbenchCredentialVaultOwner(() => { vaultCreations++; throw new Exception("Native queue must never access a provider"); });
            return new WorkbenchWindow(vm, new(), Path.Combine(f.Root, "layout.json"), catalog,
                new ProgrammingStateStore(Path.Combine(f.Root, "programming")), m112BackendFactory: id => f.Backends.Single(b => b.Device.Id == id), credentialVaultOwner: vault);
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var shell = (WorkbenchWindow)desktop.MainWindow!; RealProgrammingQueueWindow? dialog = null;
            try
            {
                var panel = (Control)typeof(WorkbenchWindow).GetMethod("CreateProgrammingView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
                Check("native programming panel keeps simulation selected beside explicit real mode", panel is TabControl { SelectedIndex: 0, ItemCount: 2 });
                var savedTarget = File.ReadAllBytes(shell.ViewModel.ProjectPath); var refusedSource = false;
                File.WriteAllBytes(shell.ViewModel.ProjectPath, [0, 1, 2]);
                try { await Open(shell); } catch (IOException) { refusedSource = true; }
                Check("native corrupt target source is refused before network and creates no queue owner", refusedSource
                    && !desktop.Windows.OfType<RealProgrammingQueueWindow>().Any() && f.Backends.All(b => b.Reads + b.Applies == 0));
                File.WriteAllBytes(shell.ViewModel.ProjectPath, savedTarget);
                var opening = Open(shell); await Until(() => opening.IsCompleted || desktop.Windows.OfType<RealProgrammingQueueWindow>().Any());
                if (opening.IsCompleted) { await opening; throw new InvalidOperationException("Queue open refused: " + shell.ViewModel.Status); }
                dialog = desktop.Windows.OfType<RealProgrammingQueueWindow>().Single();
                Check("native queue uses localized shared dialog chrome", dialog.Title == RealProgrammingQueueStrings.Get("Title") && dialog.Classes.Contains("wb-dialog"));
                Check("native queue opens offline with no implicit connection selection", Find<ComboBox>(dialog, "RealQueueTransport").SelectedIndex == -1
                    && f.Backends.All(b => b.Prepares + b.Reads + b.Applies == 0) && !Find<Button>(dialog, "RealQueueStart").IsEnabled);
                Check("native queue connection fields have localized accessible names", ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(dialog, "RealQueueGateway"))?.GetName() == LiveConnectionStrings.Get("Gateway"));
                await AddBoth(dialog, f); var queue = GetQueue(shell);
                Check("native offline enqueue creates both jobs without transport", queue.Snapshot.Jobs.Count == 2 && f.Backends.All(b => b.Reads + b.Applies == 0));
                await ConfirmBoth(dialog, queue, Check);
                Find<TextBox>(dialog, "RealQueueConfirmTarget").Focus(NavigationMethod.Tab);
                Check("native real queue target confirmation remains keyboard focusable", Find<TextBox>(dialog, "RealQueueConfirmTarget").IsFocused);
                Check("native batch requires a separate final review confirmation", !Find<Button>(dialog, "RealQueueStart").IsEnabled);
                Find<CheckBox>(dialog, "RealQueueConfirmBatch").IsChecked = true;
                Check("native batch start exposes the complete reviewed fingerprint", Find<Button>(dialog, "RealQueueStart").IsEnabled
                    && Find<TextBlock>(dialog, "RealQueueBatchFingerprint").Text!.Contains(queue.Snapshot.BatchFingerprint));
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current.RequestedThemeVariant = theme; await Task.Delay(80);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
                    bitmap.Render(dialog); bitmap.Save(Path.Combine(f.Root, $"real-queue-{language}-{theme}.png"), PngBitmapEncoderOptions.Default);
                    Check("native queue sticky start and close actions remain reachable in " + theme, Find<Button>(dialog, "RealQueueStart").Bounds.Width > 0
                        && Find<Button>(dialog, "RealQueueClose").IsVisible);
                }
                Click(Find<Button>(dialog, "RealQueueStart")); await dialog.OperationCompletion;
                Check("native real start verifies both jobs before success", queue.Snapshot.Jobs.All(j => j.State == RealM112JobState.Verified) && f.MaximumConcurrent == 1);
                Click(Find<Button>(dialog, "RealQueueClose")); await opening;
                f.Backends[0].WaitForCancellation = true; f.Backends[0].Unresolved = true; f.Backends[0].RestoreGate = Gate();
                f.Backends[0].Entered = Gate(); f.Backends[0].RestoreEntered = Gate();
                opening = Open(shell); await Until(() => desktop.Windows.OfType<RealProgrammingQueueWindow>().Any()); dialog = desktop.Windows.OfType<RealProgrammingQueueWindow>().Single();
                var sessions = (WorkbenchSessions)typeof(WorkbenchWindow).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                Check("native real queue reopens historical receipts without network or write authority", sessions.Programming.DeviceStates.Values.Count(s => s.Origin == Knx.Workbench.ProgrammingOrigin.Real) == 2
                    && f.Backends.All(b => b.Reads == 1 && b.Applies == 1));
                await AddBoth(dialog, f); queue = GetQueue(shell); await ConfirmBoth(dialog, queue); Find<CheckBox>(dialog, "RealQueueConfirmBatch").IsChecked = true;
                Click(Find<Button>(dialog, "RealQueueStart")); await f.Backends[0].Entered.Task;
                var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }; dialog.RaiseEvent(escape); await f.Backends[0].RestoreEntered.Task;
                Check("native close cancels later jobs and waits for active independent recovery", dialog.IsVisible && !dialog.OperationCompletion.IsCompleted && f.Backends[0].OwnedKey.Any(b => b != 0));
                Check("native Escape follows the same joined close path", escape.Handled);
                f.Backends[0].RestoreGate!.SetResult(); await dialog.OperationCompletion; await Task.Delay(30);
                Check("native failed recovery remains visible with durable source and no next job", dialog.IsVisible && !dialog.MayClose && queue.RecoveryRequired
                    && Find<TextBox>(dialog, "RealQueueReview").Text!.Contains(f.RecoveryPath) && f.Backends[1].Applies == 1);
                shell.RequestShutdown(); shell.Close(); await Task.Delay(40);
                Check("native idle owner close requires explicit unresolved-state acknowledgement", shell.IsVisible && dialog.IsVisible && !dialog.MayClose);
                f.Backends[0].BadInspection = true; Click(Find<Button>(dialog, "RealQueueReviewExit")); await dialog.OperationCompletion;
                Check("native missing recovery evidence is explicitly visible and remains exit-capable", Find<TextBlock>(dialog, "RealQueueExitEvidence").Text!.Contains(RealProgrammingQueueStrings.Get("RecoveryUnavailable"))
                    && Find<CheckBox>(dialog, "RealQueueExitConfirmed").IsEnabled);
                Find<CheckBox>(dialog, "RealQueueExitConfirmed").IsChecked = true; await dialog.OperationCompletion;
                Check("native unknown-state acknowledgement permits leaving without success", dialog.MayClose && queue.RecoveryRequired
                    && f.Backends[0].OwnedKey.All(b => b == 0));
                Click(Find<Button>(dialog, "RealQueueClose")); await opening; dialog = null;
                f.Backends[0].BadInspection = false;
                shell.ViewModel.SelectedDevice = shell.ViewModel.Devices.Single(d => d.Device.Id == f.Devices[0].Id);
                var coldOpening = (Task)typeof(WorkbenchWindow).GetMethod("ShowM112Async", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
                await Until(() => desktop.Windows.OfType<M112CommissioningWindow>().Any()); var cold = desktop.Windows.OfType<M112CommissioningWindow>().Single();
                Find<TextBox>(cold, "M112Gateway").Text = "192.0.2.191"; Find<ComboBox>(cold, "M112TransportMode").SelectedIndex = 0;
                await (Task)typeof(M112CommissioningWindow).GetMethod("InspectRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cold, [f.RecoveryPath])!;
                Click(Find<Button>(cold, "M112ReadIdentity")); await cold.OperationCompletion;
                Find<CheckBox>(cold, "M112IdentityConfirmed").IsChecked = true; Find<CheckBox>(cold, "M112PlanConfirmed").IsChecked = true;
                Find<TextBox>(cold, "M112ConfirmTarget").Text = f.Devices[0].Address.ToString();
                await Until(() => Find<Button>(cold, "M112Apply").IsEnabled);
                Click(Find<Button>(cold, "M112Apply")); await cold.OperationCompletion;
                Check("native existing cold recovery dialog can resume the captured unresolved queue job", f.Backends[0].Recoveries == 1 && cold.MayClose);
                cold.Close(); await coldOpening;
                Check("native plain queue accesses no vault/provider and enables no global legacy writer", vaultCreations == 0 && !Knx.Bus.KnxBus.WritesEnabled && !shell.ViewModel.CanDownload);
                shell.RequestShutdown(); shell.Close(); await Until(() => !shell.IsVisible); await shell.ConnectionCloseCompletion;
                Check("native app close joins queue and credential-owner cleanup", shell.RealProgrammingCloseCompletion.IsCompleted && shell.ConnectionCloseCompletion.IsCompleted);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL native real queue exception: " + error); }
            finally
            {
                foreach (var b in f.Backends) { b.ApplyGate?.TrySetResult(); b.RestoreGate?.TrySetResult(); }
                try
                {
                    if (dialog is not null && dialog.IsVisible)
                    {
                        var queue = GetQueue(shell); queue.Cancel(); await queue.Completion;
                        if (queue.RecoveryRequired) { await queue.ReviewUnresolvedExitAsync(); await queue.ConfirmUnresolvedExitAsync(queue.ExitReview!.Fingerprint);
                            Find<CheckBox>(dialog, "RealQueueExitConfirmed").IsChecked = true; await dialog.OperationCompletion; }
                        dialog.Close(); await dialog.ClosedCompletion;
                    }
                    foreach (var cold in desktop.Windows.OfType<M112CommissioningWindow>().ToArray()) { cold.Close(); await cold.OperationCompletion; }
                    shell.RequestShutdown(); shell.Close(); await shell.ConnectionCloseCompletion;
                }
                catch (Exception error) { failures++; Console.WriteLine("FAIL native real queue cleanup: " + error.GetType().Name); }
                Console.WriteLine($"Native real queue {language}: {checks} checks, {failures} failures; evidence {f.Root}");
                desktop.Shutdown(failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(160))).StartWithClassicDesktopLifetime([]);
        return failures == 0 ? 0 : 1;
    }
    private static RealM112ProgrammingQueue GetQueue(WorkbenchWindow shell) => (RealM112ProgrammingQueue)typeof(WorkbenchWindow)
        .GetField("_realProgrammingQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
    private static Task Open(WorkbenchWindow shell) => (Task)typeof(WorkbenchWindow).GetMethod("ShowRealProgrammingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
    private static async Task AddBoth(RealProgrammingQueueWindow window, RealM112ProgrammingQueueChecks.Fixture f)
    {
        Find<TextBox>(window, "RealQueueGateway").Text = "192.0.2.191"; Find<ComboBox>(window, "RealQueueTransport").SelectedIndex = 0;
        typeof(RealProgrammingQueueWindow).GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new M112BaselineSelection(f.Project, f.BaselinePath, await M112PrivatePaths.HashAsync(f.BaselinePath)));
        typeof(RealProgrammingQueueWindow).GetField("_journal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, f.Root);
        typeof(RealProgrammingQueueWindow).GetMethod("RefreshState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        for (var i = 0; i < 2; i++) { Find<Expander>(window, "RealQueueSettings").IsExpanded = true; Find<ComboBox>(window, "RealQueueDevice").SelectedIndex = i; Click(Find<Button>(window, "RealQueueAdd")); await window.OperationCompletion; }
    }
    private static async Task ConfirmBoth(RealProgrammingQueueWindow window, RealM112ProgrammingQueue queue, Action<string, bool>? check = null)
    {
        for (var i = 0; i < 2; i++)
        {
            Find<ListBox>(window, "RealQueueJobs").SelectedIndex = i; Click(Find<Button>(window, "RealQueueReadIdentity")); await window.OperationCompletion;
            Find<CheckBox>(window, "RealQueueConfirmPlan").IsChecked = true;
            var field = Find<TextBox>(window, "RealQueueConfirmTarget"); var delivered = Gate();
            EventHandler<TextChangedEventArgs> changed = (_, _) => { if (field.Text == queue.Snapshot.Jobs[i].Address) delivered.TrySetResult(); };
            field.TextChanged += changed; field.Text = queue.Snapshot.Jobs[i].Address; await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); field.TextChanged -= changed;
            check?.Invoke("native plan/target alone cannot substitute physical identity confirmation " + i, !Find<Button>(window, "RealQueueConfirm").IsEnabled);
            Find<CheckBox>(window, "RealQueueConfirmIdentity").IsChecked = true;
            await Until(() => Find<Button>(window, "RealQueueConfirm").IsEnabled);
            Click(Find<Button>(window, "RealQueueConfirm")); await window.OperationCompletion;
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> predicate) { for (var i = 0; i < 500 && !predicate(); i++) await Task.Delay(10); if (!predicate()) throw new TimeoutException("Native queue deadline"); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Button button) { if (!button.IsEnabled) throw new InvalidOperationException("Disabled UI action " + button.Name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
}
