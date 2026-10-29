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
using Knx.Bus.Connections;
using Knx.Catalog;
using System.Reflection;
using F = GroupConnectionFixture;

/// <summary>Native UI only with synthetic registries/counters and in-process authenticated fake gateways. No OS vault or real bus.</summary>
internal static class GroupSelectionNativeChecks
{
    [STAThread]
    internal static int Run(string language)
    {
        var root = Directory.CreateTempSubdirectory("BusChord.Group.Native.").FullName;
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        var checks = 0; var failures = 0;
        void Check(string name, bool value) { checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures++; }
        App.ForcedLanguage = language; App.ForcedTheme = "light"; App.TestWindowFactory = () => new Window { Title = "Synthetic group UI checks", Width = 400, Height = 240 };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; var shell = desktop.MainWindow!;
            try
            {
                using var fixture = new F(); await fixture.Provision(); var integrity = new GroupSelectionChecks.Integrity(); await using var owner = new WorkbenchGroupSecurityOwner(integrity);
                var scope = new object(); await using var connection = new ReadOnlyWorkbenchConnection(scope, () => scope, options => new NativeBackend(options, fixture));
                var parent = new ConnectionWindow(connection, new(fixture.Connection()), "BusChord synthetic keyring", null, null, owner, scope);
                var parentShowing = parent.ShowDialog(shell); await Until(() => parent.IsVisible);
                Check("group button requires the explicit secure source and exposes translated name", Find<Button>(parent, "LiveConfigureGroups").IsEnabled
                    && ControlAutomationPeer.CreatePeerForElement(Find<Button>(parent, "LiveConfigureGroups"))?.GetName() == GroupSecurityStrings.Get("Title"));
                Click(Find<Button>(parent, "LiveConfigureGroups")); var chooser = await Window<GroupSecuritySelectionWindow>(desktop);
                Find<TextBox>(chooser, "GroupRegistryPath").Text = GroupHostConfigurationFixture.Write(fixture);
                Click(Find<Button>(chooser, "GroupRegistryRead")); await chooser.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(6)); await Task.Delay(50);
                Check("native public candidates read no password or integrity capability", fixture.PasswordSource.Reads == 0 && integrity.Reads == 0 && Find<ComboBox>(chooser, "GroupProfileChoice").SelectedItem is KnxGroupHostCandidate);
                Check("native current-state confirmation is unchecked and required", Find<CheckBox>(chooser, "GroupCurrentStateConfirmed").IsChecked != true && !Find<Button>(chooser, "GroupProfileChoose").IsEnabled);
                Check("registry pin is complete readonly metadata", Find<TextBox>(chooser, "GroupRegistryFingerprint") is { IsReadOnly: true, Text.Length: 64 });
                Check("profile assignment has translated automation metadata", ControlAutomationPeer.CreatePeerForElement(Find<ComboBox>(chooser, "GroupProfileChoice"))?.GetName() == GroupSecurityStrings.Get("Profile"));
                Check("group selector shares application titlebar", !OperatingSystem.IsWindows() || chooser.IsExtendedIntoWindowDecorations);
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current.RequestedThemeVariant = theme; chooser.Width = 650; chooser.Height = 490; await Task.Delay(90);
                    var scroll = Find<ScrollViewer>(chooser, "GroupSelectionScroll"); scroll.Offset = new(0, scroll.Extent.Height); await Task.Delay(60);
                    Check("compact group selector retains sticky actions " + theme, scroll.Extent.Height > scroll.Viewport.Height && Find<Button>(chooser, "GroupProfileChoose").IsEffectivelyVisible);
                    Save(chooser, Path.Combine(root, "groups-" + language + "-" + theme + ".png"));
                }
                Find<CheckBox>(chooser, "GroupCurrentStateConfirmed").IsChecked = true; Click(Find<Button>(chooser, "GroupProfileChoose")); await parent.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(6));
                Check("native selection retains explicit source and binds owner beyond modal close", parent.Options?.GroupSelection is not null && ReferenceEquals(parent.Options.Profile.PasswordSource, fixture.PasswordSource) && fixture.PasswordSource.Reads == 0 && integrity.Reads == 1 && integrity.Last!.All(value => value == 0));
                Click(Find<Button>(parent, "LiveConnect")); await parent.OperationCompletion;
                Check("native connect authenticates fake gateway and consumes one grant", connection.IsConnected && connection.Snapshot.GatewayAuthenticated && parent.Options!.GroupSelection!.CanStartNewOperation == false && fixture.PasswordSource.Reads == 1);
                Click(Find<Button>(parent, "LiveDisconnect")); await parent.OperationCompletion; var before = fixture.Peers.Count;
                Click(Find<Button>(parent, "LiveConnect")); var renew = await Window<GroupStateConfirmationWindow>(desktop);
                Check("reconnect requests concrete confirmation without registry reselection", Find<CheckBox>(renew, "GroupRenewConfirmed").IsChecked != true && !Find<Button>(renew, "GroupRenewAccept").IsEnabled && fixture.Peers.Count == before);
                renew.Close(); await parent.OperationCompletion;
                Check("cancelled reconfirmation sends no frame", !connection.IsConnected && fixture.Peers.Count == before);
                Click(Find<Button>(parent, "LiveConnect")); renew = await Window<GroupStateConfirmationWindow>(desktop);
                Find<CheckBox>(renew, "GroupRenewConfirmed").IsChecked = true; Click(Find<Button>(renew, "GroupRenewAccept")); await parent.OperationCompletion;
                Check("explicit reconfirmation reconnects with same registry and source", connection.IsConnected && fixture.Peers.Count == before + 1 && integrity.Reads == 1 && fixture.PasswordSource.Reads == 2);
                Click(Find<Button>(parent, "LiveDisconnect")); await parent.OperationCompletion;
                Find<TextBox>(parent, "LiveGatewayPort").Text = "3672";
                Check("changed endpoint cannot silently drop the group policy", !Find<Button>(parent, "LiveConnect").IsEnabled && parent.Options?.GroupSelection is not null);
                Find<TextBox>(parent, "LiveGatewayPort").Text = "3671";
                parent.Close(); await parentShowing; await parent.OperationCompletion;
                await M112Policy(shell, parent.Options!, Check);
                await WorkbenchClose(desktop, shell, root, fixture, parent.Options!, Check);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL group native exception: " + error); }
            finally
            {
                foreach (var window in desktop.Windows.Where(window => window != shell).ToArray()) window.Close();
                shell.Close(); Console.WriteLine($"Native group {language}: {checks} checks, {failures} failures; screenshots {root}"); desktop.Shutdown(failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(180))).StartWithClassicDesktopLifetime([]);
        return failures == 0 ? 0 : 1;
    }
    private static async Task M112Policy(Window shell, ReadOnlyConnectionOptions options, Action<string, bool> check)
    {
        // Existing fixture hashes its file synchronously; construct away from the native dispatcher.
        await using var fixture = await Task.Run(() => new M112CommissioningChecks.Fixture());
        var dialog = new M112CommissioningWindow(fixture.Session, _ => Task.FromResult<M112BaselineSelection?>(null), options);
        var showing = dialog.ShowDialog(shell); await Until(() => dialog.IsVisible);
        try
        {
            var attached = (ReadOnlyConnectionOptions)typeof(M112CommissioningWindow).GetMethod("Options", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;
            check("M112 retains exact group policy and password source instead of dropping it", ReferenceEquals(attached.Profile.GroupSecurity, options.Profile.GroupSecurity) && ReferenceEquals(attached.Profile.PasswordSource, options.Profile.PasswordSource));
            typeof(M112CommissioningWindow).GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, new M112BaselineSelection(fixture.Project, fixture.BaselinePath, fixture.Hash));
            typeof(M112CommissioningWindow).GetField("_journalPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, fixture.Root);
            Click(Find<Button>(dialog, "M112Preview")); await dialog.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(6));
            Click(Find<Button>(dialog, "M112ReadIdentity")); await dialog.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(6));
            check("M112 refuses an individual target outside explicit plain peer policy before I/O", fixture.Backend.Reads == 0 && fixture.Backend.Applies == 0 && fixture.Session.Snapshot.Phase == M112CommissioningPhase.Reviewed);
            Click(Find<Button>(dialog, "M112RemoveGroupPolicy"));
            attached = (ReadOnlyConnectionOptions)typeof(M112CommissioningWindow).GetMethod("Options", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;
            check("M112 requires explicit removal before ordinary management options", attached.Profile.GroupSecurity is null && fixture.Session.Review is null && fixture.Backend.Reads == 0);
        }
        finally { dialog.Close(); await showing; await dialog.OperationCompletion; }
    }
    private static async Task WorkbenchClose(IClassicDesktopStyleApplicationLifetime desktop, Window shell, string root, F fixture, ReadOnlyConnectionOptions consumedOptions, Action<string, bool> check)
    {
        var library = new CatalogLibrary(new CatalogStore(Path.Combine(root, "empty-catalog"))); var vm = new MainWindowViewModel(library); vm.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null);
        var integrity = new GroupSelectionChecks.Integrity { Hold = true }; await using var owner = new WorkbenchGroupSecurityOwner(integrity);
        var window = new WorkbenchWindow(vm, new("synthetic-group-ui"), Path.Combine(root, "layout.json"), library);
        typeof(WorkbenchWindow).GetField("_groupSecurityOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, owner);
        typeof(WorkbenchWindow).GetField("_liveOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new ReadOnlyConnectionOptions(fixture.Connection()));
        window.Show(); await Task.Delay(100);
        try
        {
            typeof(WorkbenchWindow).GetField("_liveOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, consumedOptions);
            var preparing = (Task<ReadOnlyConnectionOptions?>)typeof(WorkbenchWindow).GetMethod("PrepareGroupOptionsForOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { consumedOptions })!;
            var reconfirm = await Window<GroupStateConfirmationWindow>(desktop);
            check("next scan operation asks for unchecked current-state confirmation", !preparing.IsCompleted && Find<CheckBox>(reconfirm, "GroupRenewConfirmed").IsChecked != true);
            reconfirm.Close(); check("cancelled scan preparation retains policy and opens no connection", await preparing is null && ReferenceEquals(consumedOptions.GroupSelection, ((ReadOnlyConnectionOptions)typeof(WorkbenchWindow).GetField("_liveOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).GroupSelection));
            typeof(WorkbenchWindow).GetField("_liveOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new ReadOnlyConnectionOptions(fixture.Connection()));
            var sessions = (WorkbenchSessions)typeof(WorkbenchWindow).GetProperty("Sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var source = new MonitorSource(); sessions.Monitor.ConfigureReal("synthetic-verified-feed", source); sessions.Monitor.Start();
            source.Emit(new() { Source = "synthetic", DataSecurity = BusDataSecurity.IncomingAuthenticatedAndConfidential,
                Telegram = new() { Source = F.Sender, Destination = new(F.Group.Raw), IsGroupAddress = true, Apci = Apci.GroupValueResponse, Data = [42] } });
            sessions.Monitor.FlushDisplay();
            typeof(WorkbenchWindow).GetField("_realMonitorSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.ShowTool("monitor"); await Task.Delay(100);
            check("real monitor keeps actual per-message verification", sessions.Monitor.Rows.Single().DataSecurity == BusDataSecurity.IncomingAuthenticatedAndConfidential);
            check("monitor renders translated verification separately from origin", window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains(GroupSecurityStrings.Get("Confidential")) == true));
            Save(window, Path.Combine(root, "monitor-" + Strings.Language + ".png")); sessions.Monitor.Stop();
            var connectionTask = (Task)typeof(WorkbenchWindow).GetMethod("ShowConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            var connection = await Window<ConnectionWindow>(desktop); Click(Find<Button>(connection, "LiveConfigureGroups")); var chooser = await Window<GroupSecuritySelectionWindow>(desktop);
            Find<TextBox>(chooser, "GroupRegistryPath").Text = GroupHostConfigurationFixture.Write(fixture); Click(Find<Button>(chooser, "GroupRegistryRead")); await chooser.OperationCompletion;
            Find<CheckBox>(chooser, "GroupCurrentStateConfirmed").IsChecked = true; Click(Find<Button>(chooser, "GroupProfileChoose")); await integrity.Entered.Task;
            window.RequestShutdown(); window.Close(); await Task.Delay(80);
            check("Workbench close joins nested group capability read with dispatcher alive", window.IsVisible && !window.ConnectionCloseCompletion.IsCompleted);
            integrity.Release.TrySetResult(); await connectionTask.WaitAsync(TimeSpan.FromSeconds(6)); await Until(() => !window.IsVisible);
            check("Workbench close clears held integrity bytes after joined modal cleanup", integrity.Last!.All(value => value == 0) && window.ConnectionCloseCompletion.IsCompleted);
        }
        finally { integrity.Release.TrySetResult(); window.RequestShutdown(); window.Close(); await window.ConnectionCloseCompletion; }
    }
    private sealed class MonitorSource : IMonitorTelegramSource
    { public bool IsConnected => true; public event Action<BusTelegram>? TelegramReceived; internal void Emit(BusTelegram value) => TelegramReceived?.Invoke(value); }
    private sealed class NativeBackend(ReadOnlyConnectionOptions options, F fixture) : IReadOnlyWorkbenchBackend
    {
        private KnxGatewayOperation? _operation; private KnxConnectedTunnel? _owned;
        public bool OwnsGatewayReservation => true; public KnxConnectionSecurity? Security => _owned?.Security;
        public IndividualAddress AssignedAddress => _owned?.Tunnel.AssignedAddress ?? default;
        public bool IsConnected => _owned?.Tunnel.State == BusConnectionState.Connected;
        public event Action<BusTelegram>? TelegramReceived; public event Action<BusConnectionState>? StateChanged;
        public async Task ConnectAsync(CancellationToken ct) { _operation = await fixture.Factory().AcquireOperationAsync(options.Profile, ct); _owned = await _operation.OpenTunnelAsync(ct); _owned.Tunnel.TelegramReceived += Forward; _owned.Tunnel.Disconnected += Disconnected; }
        private void Forward(BusTelegram value) => TelegramReceived?.Invoke(value);
        private void Disconnected() => StateChanged?.Invoke(BusConnectionState.Disconnected);
        public async ValueTask DisposeAsync()
        { if (_owned is not null) { _owned.Tunnel.TelegramReceived -= Forward; _owned.Tunnel.Disconnected -= Disconnected; await _owned.DisposeAsync(); } if (_operation is not null) await _operation.DisposeAsync(); }
    }
    private static async Task<T> Window<T>(IClassicDesktopStyleApplicationLifetime desktop) where T : Window
    { await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible)); return desktop.Windows.OfType<T>().Last(window => window.IsVisible); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition) { for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(10); if (!condition()) throw new TimeoutException("Group native deadline."); }
    private static void Save(Window window, string path) { using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)); image.Render(window); image.Save(path, PngBitmapEncoderOptions.Default); }
}
