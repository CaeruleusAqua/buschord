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
using Knx.Catalog;

internal static class M112CommissioningNativeChecks
{
    [STAThread]
    internal static int Run(string language)
    {
        var fixture = new M112CommissioningChecks.Fixture();
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(fixture.Root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(fixture.Root, "shared-catalog")));
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        App.ForcedLanguage = language; App.ForcedTheme = "light";
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(fixture.Root, "catalog")));
            var vm = new MainWindowViewModel(catalog);
            return new WorkbenchWindow(vm, new(), Path.Combine(fixture.Root, "layout.json"), catalog,
                new ProgrammingStateStore(Path.Combine(fixture.Root, "programming")), m112BackendFactory: _ => fixture.Backend);
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var shell = (WorkbenchWindow)lifetime.MainWindow!;
            try
            {
                var opening = (Task)typeof(WorkbenchWindow).GetMethod("ShowM112Async", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
                await Until(() => lifetime.Windows.OfType<M112CommissioningWindow>().Any());
                var dialog = lifetime.Windows.OfType<M112CommissioningWindow>().Single();
                Check("native M112 opens as localized profile dialog with shared chrome", dialog.Title == M112CommissioningStrings.Get("Title") && dialog.Classes.Contains("wb-dialog"));
                Check("native M112 opens without any network or write operation", fixture.Backend.Reads == 0 && fixture.Backend.Applies == 0);
                var gateway = Find<TextBox>(dialog, "M112Gateway"); gateway.Text = "192.0.2.190";
                Check("native M112 gateway and target fields have localized accessible names", ControlAutomationPeer.CreatePeerForElement(gateway)?.GetName() == LiveConnectionStrings.Get("Gateway")
                    && ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(dialog, "M112ConfirmTarget"))?.GetName() == M112CommissioningStrings.Get("TargetPrompt"));
                var session = (M112CommissioningSession)typeof(WorkbenchWindow).GetField("_m112Session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                fixture.Backend.ApplicationUnavailable = true;
                Check("native M112 cold-start resume is reachable without baseline or identity", Find<Button>(dialog, "M112Resume").IsEnabled && session.Review is null);
                Check("native M112 emergency recovery requires no open project", shell.ViewModel.Project is null && session.RecoveryOnly
                    && !Find<Button>(dialog, "M112Preview").IsEnabled);
                await Inspect(dialog, fixture.BaselinePath);
                Check("native M112 cold inspection is offline and needs no preview", fixture.Backend.Prepares == 0 && fixture.Backend.Reads == 0 && fixture.Backend.PhysicalReads == 0
                    && session.Snapshot.Phase == M112CommissioningPhase.ResumeReviewed);
                Click(Find<Button>(dialog, "M112ReadIdentity")); await dialog.OperationCompletion;
                Check("native M112 unloaded recovery reads physical fields without application identity", fixture.Backend.PhysicalReads == 1 && fixture.Backend.Reads == 0
                    && session.Snapshot.Phase == M112CommissioningPhase.ResumeReady && !session.Snapshot.IdentityConfirmed);
                Find<CheckBox>(dialog, "M112IdentityConfirmed").IsChecked = true; Find<CheckBox>(dialog, "M112PlanConfirmed").IsChecked = true;
                Find<TextBox>(dialog, "M112ConfirmTarget").Text = fixture.Target; await Until(() => Find<Button>(dialog, "M112Apply").IsEnabled);
                Click(Find<Button>(dialog, "M112Apply")); await dialog.OperationCompletion;
                Check("native M112 cold restore is explicitly confirmed and independently verified", fixture.Backend.Recoveries == 1 && session.Snapshot.Phase == M112CommissioningPhase.Restored);
                fixture.Backend.ApplicationUnavailable = false;
                dialog.Close(); await opening;
                shell.ViewModel.CreateProject(fixture.Project, null); shell.ViewModel.SelectedDevice = shell.ViewModel.Devices.First();
                opening = (Task)typeof(WorkbenchWindow).GetMethod("ShowM112Async", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
                await Until(() => lifetime.Windows.OfType<M112CommissioningWindow>().Any());
                dialog = lifetime.Windows.OfType<M112CommissioningWindow>().Single();
                session = (M112CommissioningSession)typeof(WorkbenchWindow).GetField("_m112Session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                Find<TextBox>(dialog, "M112Gateway").Text = "192.0.2.190";
                typeof(M112CommissioningWindow).GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, new M112BaselineSelection(fixture.Project, fixture.BaselinePath, fixture.Hash));
                typeof(M112CommissioningWindow).GetField("_journalPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, fixture.Root);
                Click(Find<Button>(dialog, "M112Preview")); await dialog.OperationCompletion;
                Console.WriteLine("Native M112 preview state: " + session.Snapshot.Phase + " / " + session.Snapshot.Issue);
                Check("native M112 preview sends no frame and shows exact reviewed fingerprint", fixture.Backend.Reads == 0 && fixture.Backend.Applies == 0
                    && Find<TextBox>(dialog, "M112Review").Text!.Contains(fixture.Backend.Review.Fingerprint));
                Check("native M112 preview alone cannot enable apply", !Find<Button>(dialog, "M112Apply").IsEnabled);
                Click(Find<Button>(dialog, "M112ReadIdentity")); await dialog.OperationCompletion;
                Check("native M112 physical identity remains unconfirmed after readonly capture", fixture.Backend.Reads == 1
                    && Find<TextBlock>(dialog, "M112Identity").Text!.Contains(fixture.Backend.Identity.Serial) && !session.Snapshot.IdentityConfirmed);
                Find<CheckBox>(dialog, "M112IdentityConfirmed").IsChecked = true;
                Find<CheckBox>(dialog, "M112PlanConfirmed").IsChecked = true;
                Find<TextBox>(dialog, "M112ConfirmTarget").Text = "1.1.254";
                Check("native M112 mistyped target keeps write action disabled", !Find<Button>(dialog, "M112Apply").IsEnabled);
                Find<TextBox>(dialog, "M112ConfirmTarget").Text = fixture.Target;
                await Until(() => Find<Button>(dialog, "M112Apply").IsEnabled);
                Check("native M112 enables apply only after all explicit confirmations", Find<Button>(dialog, "M112Apply").IsEnabled);
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current.RequestedThemeVariant = theme;
                    dialog.Width = 720; dialog.Height = 650; await Task.Delay(80);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
                    bitmap.Render(dialog); bitmap.Save(Path.Combine(fixture.Root, $"m112-{language}-{theme}.png"), PngBitmapEncoderOptions.Default);
                    Check("native M112 keeps profile actions reachable in " + theme, Find<Button>(dialog, "M112Apply").IsVisible
                        && Find<Button>(dialog, "M112Apply").Bounds.Width > 0 && dialog.Bounds.Height >= 420);
                }
                fixture.Backend.Result = new(false, true, false, fixture.BaselinePath, "synthetic restoration failure");
                Click(Find<Button>(dialog, "M112Apply")); await dialog.OperationCompletion;
                Check("native M112 failed restoration displays durable source and blocks ordinary close", !dialog.MayClose
                    && Find<TextBox>(dialog, "M112RecoveryPath").Text == fixture.BaselinePath && Find<CheckBox>(dialog, "M112CloseUnverified").IsVisible);
                dialog.Close();
                Check("native M112 failed restoration keeps dialog and resume available", dialog.IsVisible && Find<Button>(dialog, "M112Resume").IsEnabled
                    && !Find<Button>(dialog, "M112Preview").IsEnabled);
                shell.RequestShutdown(); shell.Close(); await Task.Delay(40);
                Check("native M112 idle owner close cannot bypass unresolved-recovery acknowledgement", shell.IsVisible && dialog.IsVisible && !dialog.MayClose);
                await Inspect(dialog, fixture.BaselinePath);
                Click(Find<Button>(dialog, "M112ReadIdentity")); await dialog.OperationCompletion;
                Find<CheckBox>(dialog, "M112IdentityConfirmed").IsChecked = true; Find<CheckBox>(dialog, "M112PlanConfirmed").IsChecked = true;
                Find<TextBox>(dialog, "M112ConfirmTarget").Text = fixture.Target; await Until(() => Find<Button>(dialog, "M112Apply").IsEnabled);
                Click(Find<Button>(dialog, "M112Apply")); await dialog.OperationCompletion;
                Check("native M112 explicit verified restoration releases close guard", dialog.MayClose && session.Snapshot.Phase == M112CommissioningPhase.Restored);
                Click(Find<Button>(dialog, "M112Preview")); await dialog.OperationCompletion;
                Click(Find<Button>(dialog, "M112ReadIdentity")); await dialog.OperationCompletion;
                Find<CheckBox>(dialog, "M112IdentityConfirmed").IsChecked = true;
                Find<CheckBox>(dialog, "M112PlanConfirmed").IsChecked = true;
                Find<TextBox>(dialog, "M112ConfirmTarget").Text = fixture.Target;
                await Until(() => Find<Button>(dialog, "M112Apply").IsEnabled);
                fixture.Backend.ApplyEntered = M112CommissioningChecks.Gate(); fixture.Backend.RestoreEntered = M112CommissioningChecks.Gate();
                fixture.Backend.WaitForCancellation = true; fixture.Backend.RestoreGate = M112CommissioningChecks.Gate();
                Click(Find<Button>(dialog, "M112Apply")); await fixture.Backend.ApplyEntered.Task;
                Click(Find<Button>(dialog, "M112Cancel")); await fixture.Backend.RestoreEntered.Task;
                Check("native M112 cancel enters restoration without a success flag", session.Snapshot.Phase == M112CommissioningPhase.Restoring
                    && session.Snapshot.Result is null && !Find<Button>(dialog, "M112Apply").IsEnabled);
                shell.RequestShutdown(); shell.Close(); await Task.Delay(70);
                Check("native M112 owner close waits for independent restoration", shell.IsVisible && dialog.IsVisible && !session.Completion.IsCompleted);
                using (var competing = RealEndpointReservations.TryAcquire(fixture.Options.EndpointId))
                    Check("native M112 retains global endpoint reservation while closing", competing is null);
                fixture.Backend.RestoreGate.SetResult(); await opening; await Until(() => !shell.IsVisible);
                Check("native M112 close joins restoration and preserves its typed result", session.Snapshot.Phase == M112CommissioningPhase.FailedRestored
                    && session.Snapshot.Result is { DownloadedAndVerified: false, OriginalConfigurationRestoredAndVerified: true });
                using (var free = RealEndpointReservations.TryAcquire(fixture.Options.EndpointId))
                    Check("native M112 releases endpoint after restoration completion", free is not null);
                Check("native M112 never enables global legacy writes", !Knx.Bus.KnxBus.WritesEnabled && !shell.ViewModel.CanSend && !shell.ViewModel.CanDownload);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL native M112 exception: " + error); }
            finally
            {
                fixture.Backend.RestoreGate?.TrySetResult(); fixture.Backend.PrepareGate?.TrySetResult();
                shell.RequestShutdown(); shell.Close();
                Console.WriteLine($"Native M112 {language}: {checks} checks, {failures} failures; screenshots {fixture.Root}");
                lifetime.Shutdown(failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(180))).StartWithClassicDesktopLifetime([]);
        // Preserve screenshots and synthetic evidence in the private temporary directory.
        return failures == 0 ? 0 : 1;
    }
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10); if (!condition()) throw new TimeoutException("Native M112 deadline."); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Inspect(M112CommissioningWindow window, string path) => (Task)typeof(M112CommissioningWindow)
        .GetMethod("InspectRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [path])!;
}
