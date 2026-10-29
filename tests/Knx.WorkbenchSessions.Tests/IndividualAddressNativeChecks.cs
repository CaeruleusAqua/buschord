using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
using Knx.Bus.Commissioning;
using Knx.Bus.Connections;
using Knx.Catalog;

/// <summary>Native windows with private synthetic intents and the actual production protocol stack over in-process datagrams only.</summary>
internal static class IndividualAddressNativeChecks
{
    [STAThread]
    internal static int Run(string language)
    {
        var root = Directory.CreateTempSubdirectory("BusChord.Address.Native.").FullName;
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        App.ForcedLanguage = language; App.ForcedTheme = "light"; App.TestWindowFactory = () => new Window { Title = "Synthetic address commissioning checks", Width = 400, Height = 250 };
        var checks = 0; var failures = 0;
        void Check(string name, bool value) { checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures++; }
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; var shell = desktop.MainWindow!;
            try
            {
                await using (var f = new IndividualAddressSessionChecks.Fixture())
                {
                    f.Gateway.HoldVerification = true; var window = new IndividualAddressAssignmentWindow(f.Session, new(f.Gateway.Profile)); var showing = window.ShowDialog(shell);
                    await Until(() => window.IsVisible);
                    Check("address native opens offline without transport/physical defaults", Find<ComboBox>(window, "AddressTransport").SelectedIndex == -1 && Find<ComboBox>(window, "AddressSelection").SelectedIndex == -1 && f.Gateway.Transports == 0);
                    Check("address native uses common chrome and localized accessible fields", window.Classes.Contains("wb-dialog") && (!OperatingSystem.IsWindows() || window.IsExtendedIntoWindowDecorations)
                        && ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(window, "AddressGateway"))?.GetName() == LiveConnectionStrings.Get("Gateway"));
                    var namedControls = window.GetVisualDescendants().OfType<Control>().Where(control => control.Name?.StartsWith("Address", StringComparison.Ordinal) == true).ToArray();
                    Check("address native separates planned target, review and named private paths with unique control IDs",
                        namedControls.Select(control => control.Name).Distinct(StringComparer.Ordinal).Count() == namedControls.Length
                        && new[] { ("AddressTarget", "Target"), ("AddressReviewDetails", "ReviewDetails"), ("AddressJournal", "JournalPath"), ("AddressIntent", "IntentPath") }
                            .All(pair => ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(window, pair.Item1))?.GetName() == AddressAssignmentStrings.Get(pair.Item2))
                        && ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(window, "AddressTarget"))?.GetName() != ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(window, "AddressRepeatTarget"))?.GetName());
                    Prepare(window, f.Gateway.Root); Click(Find<Button>(window, "AddressReview")); await window.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                    Check("address native readonly review binds serial old/new and complete fingerprint", f.Gateway.Writes == 0 && f.Session.Review is { Fingerprint.Length: 64 }
                        && Find<TextBox>(window, "AddressReviewDetails").Text!.Contains(IndividualAddressGatewayFixture.DeviceSerial) && Find<TextBox>(window, "AddressReviewDetails").Text!.Contains(f.Session.Review.Fingerprint));
                    Check("address serial review never labels its binding as an isolated programming-mode claim", Find<TextBox>(window, "AddressReviewDetails").Text!.Contains(AddressAssignmentStrings.Get("SerialMode"))
                        && !Find<TextBox>(window, "AddressReviewDetails").Text!.Contains(AddressAssignmentStrings.Get("IsolatedMode")));
                    Check("address native silence warning remains explicit and apply unchecked", !Find<Button>(window, "AddressApply").IsEnabled && Find<TextBox>(window, "AddressReviewDetails").Text!.Contains(AddressAssignmentStrings.Get("ConfirmReservation")));
                    Find<CheckBox>(window, "AddressPhysicalConfirmed").IsChecked = true; Find<CheckBox>(window, "AddressReservationConfirmed").IsChecked = true;
                    Find<TextBox>(window, "AddressRepeatTarget").Text = "1.1.99"; Check("address native mistyped target cannot authorize write", !Find<Button>(window, "AddressApply").IsEnabled);
                    Find<TextBox>(window, "AddressRepeatTarget").Text = IndividualAddressGatewayFixture.Desired.ToString();
                    Check("address native separate physical reservation and exact target gates enable once", Find<Button>(window, "AddressApply").IsEnabled);
                    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    {
                        Application.Current.RequestedThemeVariant = theme; window.Width = 680; window.Height = 620; await Task.Delay(80);
                        var scroll = Find<ScrollViewer>(window, "AddressScroll"); scroll.Offset = new(0, scroll.Extent.Height); await Task.Delay(40);
                        Check("address native sticky actions survive compact scrolling " + theme, scroll.Extent.Height > scroll.Viewport.Height && Find<Button>(window, "AddressApply").IsEffectivelyVisible);
                        Save(window, Path.Combine(root, "address-" + language + "-" + theme + ".png"));
                    }
                    Click(Find<Button>(window, "AddressApply")); await f.Gateway.VerificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }; window.RaiseEvent(escape); await Task.Delay(30);
                    Check("address native Escape cancels but joins fresh verification", escape.Handled && window.IsVisible && !window.OperationCompletion.IsCompleted && f.Gateway.Writes == 1);
                    f.Gateway.ReleaseVerification.TrySetResult(); await showing.WaitAsync(TimeSpan.FromSeconds(10)); await window.OperationCompletion;
                    Check("address native joined success records only actual Adr", f.Session.AddressFlagRecorded && f.Programming.DeviceState(f.Device.Id) is { Origin: Knx.Workbench.ProgrammingOrigin.Real, Flags: Knx.Workbench.DeviceLoadFlags.IndividualAddress });
                }
                await using (var f = new IndividualAddressSessionChecks.Fixture())
                {
                    f.Gateway.WrongIdentity = true; var window = new IndividualAddressAssignmentWindow(f.Session, new(f.Gateway.Profile)); var showing = window.ShowDialog(shell); await Until(() => window.IsVisible);
                    Prepare(window, f.Gateway.Root); Click(Find<Button>(window, "AddressReview")); await window.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10)); Confirm(window);
                    Click(Find<Button>(window, "AddressApply")); await window.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                    Check("address native wrong identity produces durable unresolved evidence without flags", f.Session.RecoveryRequired && !f.Session.AddressFlagRecorded && File.Exists(Find<TextBox>(window, "AddressIntent").Text));
                    window.Close(); await Task.Delay(30); Check("address native unknown hardware blocks ordinary close", window.IsVisible && !window.MayClose);
                    var intent = f.Session.IntentPath!; Find<CheckBox>(window, "AddressUnknownExitConfirmed").IsChecked = true;
                    Check("address native conscious unknown exit is no success or rewrite", window.MayClose && f.Gateway.Writes == 1 && !f.Session.AddressFlagRecorded);
                    window.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(5)); await f.Session.DisposeAsync();
                    f.Gateway.WrongIdentity = false;
                    f.Session = new(() => new(f.Project, f.Lifetime, false, f.Programming.IsRunning), f.Programming, f.Device.Id, new AddressAssignmentTransport(f.Gateway.Factory()).OpenAsync);
                    var cold = new IndividualAddressAssignmentWindow(f.Session); var coldShowing = cold.ShowDialog(shell); await Until(() => cold.IsVisible);
                    var before = f.Gateway.Transports; await f.Session.InspectAsync(intent);
                    Check("address native cold inspection grants no write capability and opens no transport", !Find<Button>(cold, "AddressApply").IsEnabled && f.Gateway.Transports == before);
                    Find<CheckBox>(cold, "AddressPhysicalConfirmed").IsChecked = true; Click(Find<Button>(cold, "AddressReconcile")); await cold.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                    Check("address native cold resume verifies through reads only", f.Session.Result?.AddressVerified == true && f.Session.AddressFlagRecorded && f.Gateway.Writes == 1);
                    cold.Close(); await coldShowing.WaitAsync(TimeSpan.FromSeconds(5));
                }
                await using (var f = new IndividualAddressSessionChecks.Fixture())
                {
                    f.Device.Address = f.Gateway.ExpectedTarget = new(1, 1, 0); f.Project.Hardware[f.Device.ProductRefId] = new() { HardwareId = "native-synthetic-coupler", IsCoupler = true };
                    var window = new IndividualAddressAssignmentWindow(f.Session, new(f.Gateway.Profile)); var showing = window.ShowDialog(shell); await Until(() => window.IsVisible);
                    Prepare(window, f.Gateway.Root); Click(Find<Button>(window, "AddressReview")); await window.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                    Check("address native coupler role is bound to displayed actual project hardware", f.Session.Review?.TargetRole == AddressDeviceRole.Coupler
                        && Find<TextBox>(window, "AddressReviewDetails").Text!.Contains("native-synthetic-coupler") && Find<TextBox>(window, "AddressReviewDetails").Text!.Contains(AddressAssignmentStrings.Get("CouplerRole")));
                    Confirm(window); Find<TextBox>(window, "AddressRepeatTarget").Text = "1.1.0"; Click(Find<Button>(window, "AddressApply")); await window.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                    Check("address native bound line coupler can receive verified real Adr", f.Session.AddressFlagRecorded && f.Gateway.Writes == 1);
                    window.Close(); await showing;
                }
                await WorkbenchOwnerClose(root, Check);
                await AddressConnectionPreparationNativeChecks.Run(shell, root, Check);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL address native exception: " + error); }
            finally
            {
                foreach (var window in desktop.Windows.Where(window => window != shell).ToArray()) window.Close(); shell.Close();
                Console.WriteLine($"Native address {language}: {checks} checks, {failures} failures; screenshots {root}"); desktop.Shutdown(failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(180))).StartWithClassicDesktopLifetime([]);
        return failures == 0 ? 0 : 1;
    }
    private static async Task WorkbenchOwnerClose(string root, Action<string, bool> check)
    {
        await using var f = new IndividualAddressSessionChecks.Fixture(); f.Gateway.HoldVerification = true;
        var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "empty-catalog"))); var vm = new MainWindowViewModel(catalog); vm.CreateProject(f.Project, null); vm.SelectedDevice = vm.Devices.Single(device => device.Device.Id == f.Device.Id);
        var providers = 0; var vault = new WorkbenchCredentialVaultOwner(() => { providers++; throw new Exception("Native address must never acquire a credential provider"); });
        var window = new WorkbenchWindow(vm, new(), Path.Combine(root, "layout.json"), catalog, credentialVaultOwner: vault);
        typeof(WorkbenchWindow).GetField("_addressAssignmentOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>>)new AddressAssignmentTransport(f.Gateway.Factory()).OpenAsync);
        typeof(WorkbenchWindow).GetField("_liveOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new ReadOnlyConnectionOptions(f.Gateway.Profile));
        window.Show(); await Task.Delay(70);
        var opening = (Task)typeof(WorkbenchWindow).GetMethod("ShowAddressAssignmentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await Until(() => desktop.Windows.OfType<IndividualAddressAssignmentWindow>().Any()); var dialog = desktop.Windows.OfType<IndividualAddressAssignmentWindow>().Single();
        try
        {
            Prepare(dialog, f.Gateway.Root); Click(Find<Button>(dialog, "AddressReview")); await dialog.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10)); Confirm(dialog);
            Click(Find<Button>(dialog, "AddressApply")); await f.Gateway.VerificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.RequestShutdown(); window.Close(); await Task.Delay(30);
            check("address native owner close keeps dispatcher alive through reconciliation", window.IsVisible && dialog.IsVisible && !opening.IsCompleted);
            f.Gateway.ReleaseVerification.TrySetResult(); await opening.WaitAsync(TimeSpan.FromSeconds(10)); await Until(() => !window.IsVisible); await window.ConnectionCloseCompletion;
            check("address native Workbench releases every owner before shutdown without provider access", f.Gateway.Writes == 1 && f.Gateway.Transports == f.Gateway.Disposals && providers == 0 && !Knx.Bus.KnxBus.WritesEnabled);
        }
        finally { f.Gateway.ReleaseVerification.TrySetResult(); window.RequestShutdown(); window.Close(); await window.ConnectionCloseCompletion; }
    }
    private static void Prepare(IndividualAddressAssignmentWindow window, string journal)
    {
        Find<ComboBox>(window, "AddressTransport").SelectedIndex = 0; Find<ComboBox>(window, "AddressSelection").SelectedIndex = 0;
        Find<TextBox>(window, "AddressSerial").Text = IndividualAddressGatewayFixture.DeviceSerial; Find<TextBox>(window, "AddressReservation").Text = "synthetic independent project reservation";
        Find<CheckBox>(window, "AddressPlainConfirmed").IsChecked = true;
        typeof(IndividualAddressAssignmentWindow).GetField("_journal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, journal);
        Find<TextBox>(window, "AddressJournal").Text = journal;
        typeof(IndividualAddressAssignmentWindow).GetMethod("InputsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    }
    private static void Confirm(IndividualAddressAssignmentWindow window)
    { Find<CheckBox>(window, "AddressPhysicalConfirmed").IsChecked = Find<CheckBox>(window, "AddressReservationConfirmed").IsChecked = true; Find<TextBox>(window, "AddressRepeatTarget").Text = IndividualAddressGatewayFixture.Desired.ToString(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) { if (!button.IsEnabled) throw new InvalidOperationException("Disabled synthetic action " + button.Name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static async Task Until(Func<bool> condition) { for (var i = 0; i < 800 && !condition(); i++) await Task.Delay(10); if (!condition()) throw new TimeoutException("Address native deadline."); }
    private static void Save(Window window, string path) { using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)); bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default); }
}
