using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Bus.Commissioning;
using Knx.Bus.Connections;

namespace Knx.App;

/// <summary>Explicit single-device address workflow. The Workbench owns the session; this modal joins input/read/verification operations before closing.</summary>
public sealed class IndividualAddressAssignmentWindow : Window
{
    private readonly IndividualAddressAssignmentSession _session;
    private readonly Func<Task> _prepareNetwork;
    private readonly IPAddress? _localAddress;
    private CancellationTokenSource? _inputCancellation;
    private readonly bool _secureInitial;
    private readonly TextBox _gateway = Field("AddressGateway"), _port = Field("AddressPort", "3671"), _client = Field("AddressClient", "1.1.240");
    private readonly TextBox _target = Field("AddressTarget"), _serial = Field("AddressSerial"), _isolation = Field("AddressIsolation"), _reservation = Field("AddressReservation");
    private readonly TextBox _journalPath = Field("AddressJournal"), _intentPath = Field("AddressIntent"), _repeatTarget = Field("AddressRepeatTarget");
    private readonly ComboBox _mode = new() { Name = "AddressTransport", SelectedIndex = -1 }, _selection = new() { Name = "AddressSelection", SelectedIndex = -1 };
    private readonly CheckBox _plain = new() { Name = "AddressPlainConfirmed" }, _physical = new() { Name = "AddressPhysicalConfirmed" }, _reserved = new() { Name = "AddressReservationConfirmed" }, _exit = new() { Name = "AddressUnknownExitConfirmed" };
    private readonly TextBox _details = new() { Name = "AddressReview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 230, Classes = { "wb-field" } };
    private readonly TextBlock _status = new() { Name = "AddressStatus", TextWrapping = TextWrapping.Wrap };
    private readonly List<Action> _translations = [];
    private readonly Dictionary<string, Button> _buttons = [];
    private readonly TaskCompletionSource _closedTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _journal, _localIssue;
    private long _generation, _reviewGeneration = -1;
    private AddressAssignmentResult? _shownResult;
    private Task _inputOperation = Task.CompletedTask;
    private bool _loading, _refreshing, _closing, _closed, _allowClose;
    public Task OperationCompletion => Task.WhenAll(_inputOperation, _session.Completion);
    public Task ClosedCompletion => _closedTask.Task;
    public bool MayClose => !_loading && _inputOperation.IsCompleted && _session.MayLeave;
    public IndividualAddressAssignmentWindow(IndividualAddressAssignmentSession session, ReadOnlyConnectionOptions? initial = null)
        : this(session, initial, null) { }
    public IndividualAddressAssignmentWindow(IndividualAddressAssignmentSession session, ReadOnlyConnectionOptions? initial, Func<Task>? prepareNetwork)
    {
        _session = session; _secureInitial = initial?.Profile.RequiresAuthentication == true;
        _prepareNetwork = prepareNetwork ?? (() => Task.CompletedTask); _localAddress = initial?.Profile.LocalAddress;
        Width = 840; Height = 850; MinWidth = 460; MinHeight = 400; Classes.Add("wb-dialog"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (initial is not null) { _gateway.Text = initial.Gateway.Address.ToString(); _port.Text = initial.Port.ToString(); _client.Text = initial.RequestedAddress.ToString(); }
        _target.IsReadOnly = _journalPath.IsReadOnly = _intentPath.IsReadOnly = true;
        var body = new StackPanel { Spacing = 9, Margin = new Thickness(20) };
        Text(body, () => A(_secureInitial ? "SecureBlocked" : "Scope"));
        void Input(string key, Control control, Func<string>? title = null)
        { Text(body, title ?? (() => A(key))); body.Children.Add(control); _translations.Add(() => AutomationProperties.SetName(control, title?.Invoke() ?? A(key))); }
        Input("Gateway", _gateway, () => LiveConnectionStrings.Get("Gateway")); Input("Port", _port, () => LiveConnectionStrings.Get("Port"));
        Input("TunnelAddress", _client, () => LiveConnectionStrings.Get("TunnelAddress")); body.Children.Add(_mode);
        Input("Target", _target, () => A("TypeTarget")); Input("Selection", _selection); Input("Serial", _serial); Input("Isolation", _isolation); Input("Reservation", _reservation);
        Text(body, () => A(_session.ProjectCouplerHardware is null ? "EndDeviceRole" : "CouplerRole") + (_session.ProjectCouplerHardware is { } hardware ? " · " + hardware : ""));
        body.Children.Add(_plain); body.Children.Add(_journalPath);
        body.Children.Add(Button("ChooseJournal", "Journal", async () =>
        {
            var paths = await StorageProvider.OpenFolderPickerAsync(new() { Title = A("Journal"), AllowMultiple = false });
            if (!_closing && paths.FirstOrDefault()?.TryGetLocalPath() is { } path) { _journal = path; _journalPath.Text = path; InputsChanged(); }
        }));
        var readings = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        readings.Children.Add(Button("Review", "ReviewAction", ReviewAsync));
        readings.Children.Add(Button("Inspect", "InspectAction", async () =>
        {
            var paths = await StorageProvider.OpenFilePickerAsync(new() { Title = A("InspectAction"), AllowMultiple = false, FileTypeFilter = [new("JSON") { Patterns = ["intent.json"] }] });
            if (!_closing && paths.FirstOrDefault()?.TryGetLocalPath() is { } path) { await _session.InspectAsync(path); if (_session.Review is { } review) { _isolation.Text = review.IsolatedSetup; _reviewGeneration = _generation; } }
        }));
        body.Children.Add(readings); body.Children.Add(_details); body.Children.Add(_physical); body.Children.Add(_reserved); Input("TypeTarget", _repeatTarget); body.Children.Add(_intentPath); body.Children.Add(_exit); body.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        var close = Button("Close", "Close", () => { Close(); return Task.CompletedTask; }); close.IsCancel = true;
        actions.Children.Add(close); actions.Children.Add(Button("Cancel", "Cancel", () => { CancelInput(); return Task.CompletedTask; }));
        actions.Children.Add(Button("Reconcile", "ReconcileAction", ReconcileAsync));
        actions.Children.Add(Button("Apply", "ApplyAction", async () =>
        {
            if (_reviewGeneration != _generation || _session.Review is not { } review || _repeatTarget.Text != review.NewAddress) throw new AddressAssignmentException(AddressAssignmentFailure.StaleReview);
            await _session.ApplyAsync(review.Fingerprint, _reserved.IsChecked == true, _physical.IsChecked == true);
        }, danger: true));
        var root = new Grid { RowDefinitions = new("*,Auto") }; root.Children.Add(new ScrollViewer { Name = "AddressScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions }; Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        foreach (var field in new[] { _gateway, _port, _client, _serial, _isolation, _reservation })
            field.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) InputsChanged(); };
        _mode.SelectionChanged += (_, _) => InputsChanged(); _selection.SelectionChanged += (_, _) => InputsChanged(); _plain.IsCheckedChanged += (_, _) => InputsChanged();
        foreach (var confirm in new[] { _physical, _reserved }) confirm.IsCheckedChanged += (_, _) => { if (!_refreshing) RefreshState(); };
        _repeatTarget.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_refreshing) RefreshState(); };
        _exit.IsCheckedChanged += (_, _) => { if (!_refreshing && !_session.IsBusy) _session.AcknowledgeUnknownStateExit(_exit.IsChecked == true); };
        _session.Changed += OnSessionChanged; Strings.Instance.PropertyChanged += OnLanguage;
        Closing += OnClosing; Closed += (_, _) => { _closed = true; _session.Changed -= OnSessionChanged; Strings.Instance.PropertyChanged -= OnLanguage; _closedTask.TrySetResult(); };
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); } };
        RefreshLanguage(); WorkbenchDialogChrome.Apply(this);
    }
    private static TextBox Field(string name, string? text = "") => new() { Name = name, Text = text, Classes = { "wb-field" } };
    private static string A(string key) => AddressAssignmentStrings.Get(key);
    private void Text(Panel panel, Func<string> value) { var text = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(text); _translations.Add(() => text.Text = value()); }
    private Button Button(string id, string key, Func<Task> action, bool danger = false)
    {
        var button = new Button { Name = "Address" + id, Classes = { "wb-button", danger ? "wb-danger" : "wb-quiet" } }; _buttons[id] = button;
        _translations.Add(() => { var text = key is "Close" ? LiveConnectionStrings.Get("Close") : key is "Cancel" ? M112CommissioningStrings.Get("Cancel") : A(key); button.Content = text; AutomationProperties.SetName(button, text); });
        button.Click += (_, _) => { if (id == "Close") Close(); else if (id == "Cancel") CancelInput(); else Begin(action); }; return button;
    }
    private void InputsChanged()
    {
        if (_refreshing || _closed) return;
        _generation++; _reviewGeneration = -1; _localIssue = null;
        _refreshing = true; try { _physical.IsChecked = _reserved.IsChecked = false; _repeatTarget.Text = ""; } finally { _refreshing = false; }
        if (_loading || _session.IsBusy) CancelInput(); RefreshState();
    }
    private async Task ReviewAsync()
    {
        if (_secureInitial) throw new AddressAssignmentException(AddressAssignmentFailure.UnsupportedSecureProvisioning);
        if (_mode.SelectedIndex != 0 || _session.ProjectTarget is not { } target || _journal is null
            || !IPAddress.TryParse(_gateway.Text, out var gateway) || !int.TryParse(_port.Text, out var port) || !IndividualAddress.TryParse(_client.Text ?? "", out var client))
            throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        var selection = _selection.SelectedIndex switch { 0 => AddressSelectionKind.ExactSerial, 1 => AddressSelectionKind.IsolatedProgrammingMode, _ => throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection) };
        var request = new AddressAssignmentRequest(KnxConnectionProfile.Plain(new(gateway, port), client, _localAddress), target, selection, _reservation.Text ?? "",
            selection == AddressSelectionKind.ExactSerial ? _serial.Text : null, selection == AddressSelectionKind.IsolatedProgrammingMode ? _isolation.Text : null, confirmPlainDevice: _plain.IsChecked == true,
            targetRole: _session.ProjectCouplerHardware is null ? AddressDeviceRole.EndDevice : AddressDeviceRole.Coupler, confirmCouplerHardware: _session.ProjectCouplerHardware is not null, couplerHardwareReference: _session.ProjectCouplerHardware);
        var generation = _generation; await PrepareNetworkForInputAsync(generation);
        await _session.ReviewAsync(request, _journal); if (generation == _generation && _session.Issue is null) _reviewGeneration = generation;
    }
    private async Task ReconcileAsync()
    {
        var physical = _physical.IsChecked == true; var isolation = _isolation.Text;
        await PrepareNetworkForInputAsync(_generation); await _session.ReconcileAsync(physical, isolation);
    }
    private async Task PrepareNetworkForInputAsync(long generation)
    {
        var token = _inputCancellation?.Token ?? throw new OperationCanceledException();
        token.ThrowIfCancellationRequested(); await _prepareNetwork(); token.ThrowIfCancellationRequested();
        if (_closing || _closed || generation != _generation) throw new OperationCanceledException(token);
    }
    private void CancelInput()
    { try { _inputCancellation?.Cancel(); } catch (ObjectDisposedException) { } _session.Cancel(); }
    private void Begin(Func<Task> action)
    {
        if (!_inputOperation.IsCompleted || _session.IsBusy || _closing || _closed) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = _inputCancellation = new CancellationTokenSource();
        _inputOperation = completion.Task; _loading = true; _localIssue = null;
        _refreshing = true; _exit.IsChecked = false; _refreshing = false; RefreshState(); _ = RunAsync(action, cancellation, completion);
    }
    private async Task RunAsync(Func<Task> action, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { cancellation.Token.ThrowIfCancellationRequested(); await action(); }
        catch (AddressAssignmentException error) { _localIssue = error.Failure.ToString(); }
        catch (OperationCanceledException) { _localIssue = "Cancelled"; }
        catch { _localIssue = "Failed"; }
        finally { if (ReferenceEquals(_inputCancellation, cancellation)) _inputCancellation = null; cancellation.Dispose(); _loading = false; completion.TrySetResult(); if (!_closed) RefreshState(); }
    }
    private void OnSessionChanged() { if (Dispatcher.UIThread.CheckAccess()) RefreshState(); else Dispatcher.UIThread.Post(() => { if (!_closed) RefreshState(); }); }
    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (args.PropertyName == "Item[]") Dispatcher.UIThread.Post(() => { if (!_closed) RefreshLanguage(); }); }
    private void RefreshLanguage()
    {
        _refreshing = true;
        try
        {
            Title = A("Title"); foreach (var update in _translations) update();
            var mode = _mode.SelectedIndex; _mode.ItemsSource = new[] { A("Plain") }; _mode.SelectedIndex = mode;
            var selection = _selection.SelectedIndex; _selection.ItemsSource = new[] { A("SerialMode"), A("IsolatedMode") }; _selection.SelectedIndex = selection;
            foreach (var (control, key) in new[] { (_plain, "PlainConsent"), (_physical, "ConfirmPhysical"), (_reserved, "ConfirmReservation"), (_exit, "ExitAck") })
            { control.Content = new TextBlock { Text = A(key), TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetName(control, A(key)); }
            AutomationProperties.SetName(_mode, SecureConnectionStrings.Get("Mode")); AutomationProperties.SetName(_selection, A("Selection"));
            AutomationProperties.SetName(_details, SecureConnectionStrings.Get("Fingerprint")); AutomationProperties.SetName(_intentPath, A("InspectAction"));
        }
        finally { _refreshing = false; }
        RefreshState();
    }
    private void RefreshState()
    {
        if (_refreshing || _closed) return; _refreshing = true;
        try
        {
            var busy = _loading || _session.IsBusy; var unresolved = _session.RecoveryRequired; var review = _session.Review;
            if (!ReferenceEquals(_shownResult, _session.Result)) { _shownResult = _session.Result; _physical.IsChecked = _reserved.IsChecked = false; _repeatTarget.Text = ""; }
            _target.Text = review?.NewAddress ?? _session.ProjectTarget?.ToString() ?? "";
            foreach (var control in new Control[] { _gateway, _port, _client, _mode, _selection, _serial, _isolation, _reservation, _plain }) control.IsEnabled = !busy && !unresolved && !_secureInitial;
            _serial.IsEnabled = !busy && !unresolved && !_secureInitial && _selection.SelectedIndex == 0;
            _isolation.IsEnabled = !busy && !unresolved && (_selection.SelectedIndex == 1 || review?.Selection == AddressSelectionKind.IsolatedProgrammingMode);
            foreach (var button in _buttons.Values) button.IsEnabled = !busy;
            _buttons["Close"].IsEnabled = true; _buttons["Cancel"].IsEnabled = busy;
            _buttons["Review"].IsEnabled = !busy && !unresolved && !_secureInitial && _session.HasProjectTarget && _journal is not null && _mode.SelectedIndex == 0 && _selection.SelectedIndex >= 0 && _plain.IsChecked == true;
            _buttons["ChooseJournal"].IsEnabled = !busy && !unresolved && !_secureInitial;
            _buttons["Inspect"].IsEnabled = !busy && !unresolved;
            _buttons["Apply"].IsEnabled = !busy && !unresolved && _session.Result is null && _session.Issue is null && review is not null && _reviewGeneration == _generation
                && _physical.IsChecked == true && _reserved.IsChecked == true && _repeatTarget.Text == review.NewAddress;
            _buttons["Reconcile"].IsEnabled = !busy && review is not null && _session.IntentPath is not null && _physical.IsChecked == true;
            _physical.IsEnabled = _reserved.IsEnabled = _repeatTarget.IsEnabled = !busy && review is not null;
            _exit.IsVisible = unresolved; _exit.IsEnabled = !busy && unresolved;
            _intentPath.Text = _session.IntentPath ?? "";
            _details.Text = review is null ? A("Scope") : new StringBuilder().AppendLine(review.InterfaceId + " · " + review.ClientAddress).AppendLine(review.OldAddress + " → " + review.NewAddress)
                .AppendLine(review.Serial ?? review.IsolatedSetup).AppendLine(review.Identity is { } identity ? identity.Manufacturer + " · " + identity.Serial + " · " + identity.MaskVersion.ToString("X4") : A(review.Selection == AddressSelectionKind.ExactSerial ? "SerialMode" : "IsolatedMode"))
                .AppendLine(A(review.TargetRole == AddressDeviceRole.Coupler ? "CouplerRole" : "EndDeviceRole")).AppendLine(review.CouplerHardwareReference)
                .AppendLine(review.Reservation).AppendLine(A("ConfirmReservation")).AppendLine(review.Fingerprint).ToString();
            _status.Text = A(unresolved ? "Unresolved" : _localIssue ?? _session.Issue ?? (_session.AddressFlagRecorded ? "FlagRecorded" : _session.State.ToString()));
        }
        finally { _refreshing = false; }
    }
    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return; args.Cancel = true; if (_closing) return; _closing = true; CancelInput();
        var pending = OperationCompletion; _ = FinishCloseAsync(pending);
    }
    private async Task FinishCloseAsync(Task pending)
    {
        try { await pending; if (!MayClose) { _localIssue = "Unresolved"; return; } _allowClose = true; Close(); }
        finally { _closing = false; if (!_closed) RefreshState(); }
    }
}
