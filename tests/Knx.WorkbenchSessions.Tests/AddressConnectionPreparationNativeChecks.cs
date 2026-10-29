using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Bus.Commissioning;
using Knx.Bus.Connections;
using Knx.Catalog;

internal static class AddressConnectionPreparationNativeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task Run(Window shell, string root, Action<string, bool> check)
    {
        foreach (var mode in new[] { "review", "cancel", "close", "replace" })
        {
            await using var fixture = new IndividualAddressSessionChecks.Fixture();
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "network-" + mode)));
            var vm = new MainWindowViewModel(catalog); vm.CreateProject(fixture.Project, null);
            vm.SelectedDevice = vm.Devices.Single(row => row.Device.Id == fixture.Device.Id);
            var window = new WorkbenchWindow(vm, new(), Path.Combine(root, mode + ".layout"), catalog,
                credentialVaultOwner: new WorkbenchCredentialVaultOwner(() => throw new Exception("No provider in connection preparation")));
            window.Show(shell); window.RefreshWorkspace();
            typeof(WorkbenchWindow).GetMethod("EnsureSessions", Private)!.Invoke(window, null);
            object Lifetime() => typeof(MainWindowViewModel).GetProperty("ProjectSessionIdentity", Private)!.GetValue(vm)!;
            var receiver = new HeldReceiver(fixture.Gateway.Factory(), fixture.Gateway.Profile);
            await using var connection = new ReadOnlyWorkbenchConnection(Lifetime(), Lifetime, _ => receiver);
            var localAddress = System.Net.IPAddress.Parse("127.0.0.2");
            var selected = new ReadOnlyConnectionOptions(KnxConnectionProfile.Plain(fixture.Gateway.Profile.Gateway, new(1, 1, 241), localAddress));
            await connection.ConnectAsync(selected);
            Set(window, "_liveConnection", connection); Set(window, "_liveOptions", selected);
            KnxConnectionProfile? openedProfile = null;
            var transport = new AddressAssignmentTransport(fixture.Gateway.Factory());
            Set(window, "_addressAssignmentOpen", (Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>>)((profile, token) =>
            { openedProfile = profile; return transport.OpenAsync(profile, token); }));
            var initialTransports = fixture.Gateway.Transports;
            var showing = (Task)typeof(WorkbenchWindow).GetMethod("ShowAddressAssignmentAsync", Private)!.Invoke(window, null)!;
            var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
            IndividualAddressAssignmentWindow? dialog = null;
            try
            {
                for (var count = 0; count < 500 && dialog is null; count++)
                { dialog = desktop.Windows.OfType<IndividualAddressAssignmentWindow>().SingleOrDefault(); if (dialog is null) await Task.Delay(10); }
                if (dialog is null) throw new TimeoutException("Address preparation window missing.");
                check("address opening preserves the live monitor without new I/O " + mode, connection.IsConnected && fixture.Gateway.Transports == initialTransports);
                check("address proposes the observed assigned client instead of the requested hint " + mode,
                    Find<TextBox>(dialog, "AddressClient").Text == IndividualAddressGatewayFixture.Client.ToString());
                Find<TextBox>(dialog, "AddressClient").Text = IndividualAddressGatewayFixture.Client.ToString();
                Find<ComboBox>(dialog, "AddressTransport").SelectedIndex = 0; Find<ComboBox>(dialog, "AddressSelection").SelectedIndex = 0;
                Find<TextBox>(dialog, "AddressSerial").Text = IndividualAddressGatewayFixture.DeviceSerial;
                Find<TextBox>(dialog, "AddressReservation").Text = "independently reserved synthetic project address";
                Find<CheckBox>(dialog, "AddressPlainConfirmed").IsChecked = true;
                Set(dialog, "_journal", fixture.Gateway.Root); Find<TextBox>(dialog, "AddressJournal").Text = fixture.Gateway.Root;
                typeof(IndividualAddressAssignmentWindow).GetMethod("InputsChanged", Private)!.Invoke(dialog, null);
                Click(Find<Button>(dialog, "AddressReview"));
                await Task.WhenAny(receiver.Entered.Task, dialog.OperationCompletion).WaitAsync(TimeSpan.FromSeconds(8));
                var waiting = receiver.Entered.Task.IsCompleted && !dialog.OperationCompletion.IsCompleted;
                check("address review joins the existing receiver before opening a new owner " + mode,
                    waiting && fixture.Gateway.Transports == initialTransports && fixture.Gateway.Writes == 0);
                if (mode == "close") dialog.Close();
                else if (mode == "cancel" && waiting) Click(Find<Button>(dialog, "AddressCancel"));
                else if (mode == "replace") { vm.CreateProject(fixture.Project, null); window.RefreshWorkspace(); }
                receiver.Release.TrySetResult();
                await dialog.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                var session = (IndividualAddressAssignmentSession)typeof(IndividualAddressAssignmentWindow).GetField("_session", Private)!.GetValue(dialog)!;
                if (mode == "review")
                {
                    check("address review starts only after receiver and reservation are released", receiver.Released && session.Review is not null && session.Issue is null && fixture.Gateway.Writes == 0);
                    check("address review retains the explicitly selected local interface", Equals(openedProfile?.LocalAddress, localAddress));
                }
                else
                    check("address preparation " + mode + " cannot start late discovery", receiver.Released && session.Review is null && fixture.Gateway.Transports == initialTransports && fixture.Gateway.Writes == 0);
                dialog.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                receiver.Release.TrySetResult(); dialog?.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(10));
                window.RequestShutdown(); window.Close(); await window.ConnectionCloseCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
    private sealed class HeldReceiver(KnxConnectionFactory factory, KnxConnectionProfile actual) : IReadOnlyWorkbenchBackend
    {
        private KnxGatewayOperation? _operation;
        private KnxConnectedTunnel? _owned;
        private Task? _disposal;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Released { get; private set; }
        public bool OwnsGatewayReservation => true;
        public IndividualAddress AssignedAddress => _owned!.Tunnel.AssignedAddress;
        public bool IsConnected => _owned?.Tunnel.State == BusConnectionState.Connected;
        public event Action<BusTelegram>? TelegramReceived { add { } remove { } }
        public event Action<BusConnectionState>? StateChanged { add { } remove { } }
        public async Task ConnectAsync(CancellationToken ct)
        { _operation = await factory.AcquireOperationAsync(actual, ct); _owned = await _operation.OpenTunnelAsync(ct); }
        public ValueTask DisposeAsync() => new(_disposal ??= FinishDisposeAsync());
        private async Task FinishDisposeAsync()
        { Entered.TrySetResult(); await Release.Task; if (_owned is not null) await _owned.DisposeAsync(); if (_operation is not null) await _operation.DisposeAsync(); Released = true; }
    }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
