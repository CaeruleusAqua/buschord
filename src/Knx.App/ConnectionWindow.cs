using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.App;

/// <summary>Explicit standard UDP tunnel controls; all entered options remain in this window's project lifetime.</summary>
public sealed class ConnectionWindow : Window
{
    private readonly ReadOnlyWorkbenchConnection _connection;
    private readonly TextBox _gateway = new() { Name = "LiveGatewayAddress", Classes = { "wb-field" } };
    private readonly TextBox _port = new() { Name = "LiveGatewayPort", Text = "3671", Classes = { "wb-field" } };
    private readonly TextBox _address = new() { Name = "LiveTunnelAddress", Text = "1.1.250", Classes = { "wb-field" } };
    private readonly TextBlock _status = new() { Name = "LiveConnectionStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect = new() { Name = "LiveConnect", Classes = { "wb-button", "wb-primary" }, IsDefault = true };
    private readonly Button _cancel = new() { Name = "LiveCancel", Classes = { "wb-button", "wb-quiet" } };
    private readonly Button _disconnect = new() { Name = "LiveDisconnect", Classes = { "wb-button", "wb-quiet" } };
    private readonly Button _close = new() { Name = "LiveClose", Classes = { "wb-button", "wb-quiet" }, IsCancel = true };
    private readonly List<Action> _translations = [];
    private readonly ComboBox _mode = new() { Name = "LiveTransportMode", SelectedIndex = 0 };
    private readonly Button _keyring = new() { Name = "LiveConfigureSecure", Classes = { "wb-button", "wb-quiet" } };
    private readonly TextBlock _binding = new() { Name = "LiveSecureBinding", TextWrapping = TextWrapping.Wrap };
    private readonly string _expectedKeyringProject;
    private readonly string? _credentialScope;
    private readonly WorkbenchCredentialVaultOwner? _credentialOwner;
    private readonly IPAddress? _selectedLocalAddress;
    private SecureKeyringReference? _secureReference;
    private ISecurePasswordSource? _passwordSource;
    private SecureConnectionWindow? _secureDialog;
    private readonly WorkbenchGroupSecurityOwner? _groupOwner;
    private readonly object? _groupProjectLifetime;
    private readonly Button _groups = new() { Name = "LiveConfigureGroups", Classes = { "wb-button", "wb-quiet" } };
    private readonly Button _removeGroups = new() { Name = "LiveRemoveGroups", Classes = { "wb-button", "wb-quiet" } };
    private readonly TextBlock _groupBinding = new() { Name = "LiveGroupBinding", TextWrapping = TextWrapping.Wrap };
    private WorkbenchGroupSelection? _groupSelection;
    private bool _unboundGroupPolicy;
    private GroupSecuritySelectionWindow? _groupDialog;
    private GroupStateConfirmationWindow? _groupConfirmation;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _operation;
    private Task _cancelOperation = Task.CompletedTask;
    private Task? _closeOperation;
    private bool _closed, _closing, _allowClose;
    private string? _inputIssue;
    public ReadOnlyConnectionOptions? Options { get; private set; }
    public Task OperationCompletion => _closeOperation ?? Task.WhenAll(_operation ?? Task.CompletedTask, _cancelOperation);

    public ConnectionWindow(ReadOnlyWorkbenchConnection connection, ReadOnlyConnectionOptions? initial = null, string expectedKeyringProject = "",
        string? credentialScope = null, WorkbenchCredentialVaultOwner? credentialOwner = null)
        : this(connection, initial, expectedKeyringProject, credentialScope, credentialOwner, null, null) { }
    public ConnectionWindow(ReadOnlyWorkbenchConnection connection, ReadOnlyConnectionOptions? initial, string expectedKeyringProject,
        string? credentialScope, WorkbenchCredentialVaultOwner? credentialOwner, WorkbenchGroupSecurityOwner? groupOwner, object? groupProjectLifetime)
    {
        _connection = connection;
        _selectedLocalAddress = initial?.Profile.LocalAddress;
        _groupOwner = groupOwner; _groupProjectLifetime = groupProjectLifetime; _groupSelection = initial?.GroupSelection;
        _unboundGroupPolicy = initial?.Profile.GroupSecurity is not null && _groupSelection is null;
        _expectedKeyringProject = expectedKeyringProject;
        _credentialScope = credentialScope; _credentialOwner = credentialOwner;
        Width = 640; Height = 610; MinWidth = 400; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        Options = initial;
        if (initial is not null) { _gateway.Text = initial.Gateway.Address.ToString(); _port.Text = initial.Port.ToString(); _address.Text = initial.RequestedAddress.ToString(); }
        if (initial?.Profile.Kind == KnxTransportKind.IpSecureUdp) { _mode.SelectedIndex = 1; _secureReference = initial.Profile.Keyring; _passwordSource = initial.Profile.PasswordSource; }
        if (!CredentialSelectionSession.MatchesStoredBinding(_credentialScope, _secureReference, _passwordSource)) { _secureReference = null; _passwordSource = null; Options = null; }
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        void Field(string key, TextBox input)
        {
            var label = new TextBlock { Classes = { "wb-label" } }; body.Children.Add(label); body.Children.Add(input);
            _translations.Add(() => { label.Text = L(key); AutomationProperties.SetName(input, L(key)); });
        }
        Field("Gateway", _gateway); Field("Port", _port); Field("TunnelAddress", _address);
        body.Children.Add(_mode); body.Children.Add(_keyring); body.Children.Add(_binding);
        var groupActions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 }; groupActions.Children.Add(_groups); groupActions.Children.Add(_removeGroups); body.Children.Add(groupActions); body.Children.Add(_groupBinding);
        _mode.SelectionChanged += (_, _) => RefreshState();
        foreach (var input in new[] { _gateway, _port, _address }) input.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) { _inputIssue = null; RefreshState(); } };
        _keyring.Click += (_, _) => StartOwned(ConfigureSecureAsync);
        _groups.Click += (_, _) => StartOwned(ConfigureGroupsAsync);
        _removeGroups.Click += (_, _) => { if (_connection.Snapshot.Phase is ReadOnlyConnectionPhase.Connected or ReadOnlyConnectionPhase.Connecting || _operation?.IsCompleted == false) return; _groupSelection = null; _unboundGroupPolicy = false; Options = null; RefreshState(); };
        var explanation = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "wb-subtle" } };
        _translations.Add(() => explanation.Text = L("GroupOnly") + " " + GroupSecurityStrings.Get("GroupOnly")); body.Children.Add(explanation); body.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _close, _cancel, _disconnect, _connect }) { button.Margin = new Thickness(4); actions.Children.Add(button); }
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions };
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _connect.Click += (_, _) => StartOwned(ConnectAsync);
        _cancel.Click += (_, _) => StartOwned(DisconnectAsync, allowBusy: true);
        _disconnect.Click += (_, _) => StartOwned(DisconnectAsync);
        _close.Click += (_, _) => Close();
        _connection.Changed += OnChanged;
        Strings.Instance.PropertyChanged += OnLanguage;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _connection.Changed -= OnChanged; Strings.Instance.PropertyChanged -= OnLanguage; _lifetime.Dispose(); };
        Opened += (_, _) => _gateway.Focus();
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); } };
        RefreshLanguage(); WorkbenchDialogChrome.Apply(this);
    }
    private static string L(string key) => LiveConnectionStrings.Get(key);
    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == "Item[]") Dispatcher.UIThread.Post(() => { if (!_closed) RefreshLanguage(); }); }
    private void RefreshLanguage()
    {
        Title = L("Title"); _connect.Content = L("Connect"); _cancel.Content = L("CancelConnect");
        var selectedMode = _mode.SelectedIndex; _mode.ItemsSource = new[] { SecureConnectionStrings.Get("Plain"), SecureConnectionStrings.Get("Secure") }; _mode.SelectedIndex = selectedMode;
        _keyring.Content = SecureConnectionStrings.Get("Title"); AutomationProperties.SetName(_mode, SecureConnectionStrings.Get("Mode"));
        _groups.Content = GroupSecurityStrings.Get("Title"); _removeGroups.Content = GroupSecurityStrings.Get("Remove");
        AutomationProperties.SetName(_groups, GroupSecurityStrings.Get("Title")); AutomationProperties.SetName(_removeGroups, GroupSecurityStrings.Get("Remove"));
        _disconnect.Content = L("Disconnect"); _close.Content = L("Close");
        foreach (var translate in _translations) translate(); RefreshState();
    }
    public static string Status(ReadOnlyConnectionSnapshot snapshot)
    {
        var issue = snapshot.Issue != ReadOnlyConnectionIssue.None;
        var text = snapshot.Failure is { } failure ? GroupSecurityStrings.Contains(failure.ToString()) ? GroupSecurityStrings.Get(failure.ToString()) : SecureConnectionStrings.Get(failure.ToString()) : L(issue ? snapshot.Issue.ToString() : snapshot.Phase.ToString());
        return text + (snapshot.EndpointId is { } endpoint ? " · " + endpoint : "")
            + (snapshot.AssignedAddress is { } assigned ? " · " + assigned : "") + " · "
            + (snapshot.GatewayAuthenticated ? SecureConnectionStrings.Get("Authenticated") : snapshot.Transport == KnxTransportKind.IpSecureUdp ? SecureConnectionStrings.Get("Secure") : L("ReadOnly"));
    }
    private void OnChanged(ReadOnlyConnectionSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    { if (!_closed && snapshot.Generation == _connection.Snapshot.Generation) RefreshState(); });
    private void RefreshState()
    {
        var snapshot = _connection.Snapshot; _status.Text = _inputIssue ?? Status(snapshot);
        var busy = snapshot.Phase is ReadOnlyConnectionPhase.Connecting or ReadOnlyConnectionPhase.Disconnecting || _operation?.IsCompleted == false;
        var connected = snapshot.Phase == ReadOnlyConnectionPhase.Connected;
        _gateway.IsEnabled = _port.IsEnabled = _address.IsEnabled = !busy && !connected;
        _mode.IsEnabled = !busy && !connected; _keyring.IsEnabled = !busy && !connected && _mode.SelectedIndex == 1;
        _address.IsReadOnly = _mode.SelectedIndex == 1;
        _binding.Text = _secureReference is { } reference && _mode.SelectedIndex == 1 ? $"Host {reference.HostAddress} · User {reference.UserId} · IA {reference.TunnelAddress}" : "";
        var groupMatches = GroupMatchesCurrent();
        _groups.IsEnabled = !busy && !connected && _mode.SelectedIndex == 1 && _secureReference is not null && _passwordSource is not null && _groupOwner is not null && _groupProjectLifetime is not null;
        _removeGroups.IsEnabled = !busy && !connected && (_groupSelection is not null || _unboundGroupPolicy);
        _groupBinding.Text = _groupSelection is { } group ? (groupMatches ? GroupSecurityStrings.Get(group.CanStartNewOperation ? "Selected" : "Reconfirm") : GroupSecurityStrings.Get("BindingMismatch")) + " · " + group.Candidate.Id
            : _unboundGroupPolicy ? GroupSecurityStrings.Get("BindingMismatch") : "";
        _connect.IsEnabled = !busy && !connected && snapshot.Issue != ReadOnlyConnectionIssue.CleanupFailed && (_mode.SelectedIndex == 0 || _secureReference is not null)
            && !_unboundGroupPolicy && (_groupSelection is null || groupMatches);
        _cancel.IsEnabled = snapshot.Phase == ReadOnlyConnectionPhase.Connecting;
        _disconnect.IsEnabled = connected || snapshot.Issue == ReadOnlyConnectionIssue.CleanupFailed;
    }
    private async Task ConnectAsync()
    {
        try
        {
            if (!IPAddress.TryParse(_gateway.Text, out var gateway) || !int.TryParse(_port.Text, out var port)
                || !IndividualAddress.TryParse(_address.Text ?? "", out var address)) throw new ArgumentException();
            var selected = SelectedOptions(gateway, port, address);
            if (_groupSelection is { } group)
            {
                if (!group.CanStartNewOperation)
                {
                    var confirmation = _groupConfirmation = new(group);
                    try { await confirmation.ShowDialog(this); await confirmation.OperationCompletion; if (_closing || confirmation.Selection is not { } renewed) return; _groupSelection = group = renewed; }
                    finally { _groupConfirmation = null; }
                }
                var bare = BareSecure(gateway, port); selected = new(await group.PrepareNewOperationAsync(bare, _lifetime.Token), group);
            }
            _lifetime.Token.ThrowIfCancellationRequested(); Options = selected;
            await _connection.ConnectAsync(Options, _lifetime.Token);
        }
        catch (ArgumentException) { if (!_closed) _inputIssue = L("Invalid"); return; }
        catch (KnxConnectionException error) { if (!_closed) _inputIssue = GroupSecurityStrings.Contains(error.Failure.ToString()) ? GroupSecurityStrings.Get(error.Failure.ToString()) : SecureConnectionStrings.Get(error.Failure.ToString()); return; }
        catch (Exception) { /* The connection snapshot carries the failure; no private endpoint exception text is logged. */ }
        if (!_closed) RefreshState();
    }
    private ReadOnlyConnectionOptions SelectedOptions(IPAddress gateway, int port, IndividualAddress address)
    {
        if (_unboundGroupPolicy || _groupSelection is not null && !GroupMatchesCurrent()) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
        if (_groupSelection is { } selected) return new(selected.BoundProfile, selected);
        return _mode.SelectedIndex switch
        {
            0 => new(gateway, port, address),
            1 when _secureReference is not null && CredentialSelectionSession.MatchesStoredBinding(_credentialScope, _secureReference, _passwordSource) => new(KnxConnectionProfile.IpSecure(new(gateway, port), _secureReference, _passwordSource, _selectedLocalAddress)),
            _ => throw new ArgumentException("Invalid explicit transport selection.")
        };
    }
    private KnxConnectionProfile BareSecure(IPAddress gateway, int port)
        => _mode.SelectedIndex == 1 && _secureReference is not null && _passwordSource is not null && CredentialSelectionSession.MatchesStoredBinding(_credentialScope, _secureReference, _passwordSource)
            ? KnxConnectionProfile.IpSecure(new(gateway, port), _secureReference, _passwordSource, _selectedLocalAddress) : throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
    private bool GroupMatchesCurrent()
    { try { return _groupSelection is null || IPAddress.TryParse(_gateway.Text, out var gateway) && int.TryParse(_port.Text, out var port) && _groupSelection.Matches(BareSecure(gateway, port)); } catch { return false; } }
    private async Task ConfigureGroupsAsync()
    {
        if (_groupOwner is null || _groupProjectLifetime is null || !IPAddress.TryParse(_gateway.Text, out var gateway) || !int.TryParse(_port.Text, out var port)) return;
        var bare = BareSecure(gateway, port); var dialog = _groupDialog = new(_groupOwner, _groupProjectLifetime, bare);
        try { await dialog.ShowDialog(this); await dialog.OperationCompletion; if (!_closing && dialog.Selection is { } selected && GroupMatchesCurrentSource(selected)) { _groupSelection = selected; _unboundGroupPolicy = false; Options = new(selected.BoundProfile, selected); } }
        finally { _groupDialog = null; }
        if (!_closed) RefreshState();
    }
    private bool GroupMatchesCurrentSource(WorkbenchGroupSelection selection)
    { try { return IPAddress.TryParse(_gateway.Text, out var gateway) && int.TryParse(_port.Text, out var port) && selection.Matches(BareSecure(gateway, port)); } catch { return false; } }
    private void StartOwned(Func<Task> action, bool allowBusy = false)
    {
        if (_closing || _closed) return;
        if (_operation?.IsCompleted == false)
        {
            if (allowBusy && _cancelOperation.IsCompleted)
            { var cancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _cancelOperation = cancel.Task; _ = RunOwnedAsync(DisconnectAsync, cancel); }
            return;
        }
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operation = owner.Task; _inputIssue = null; RefreshState(); _ = RunOwnedAsync(action, owner);
    }
    private async Task RunOwnedAsync(Func<Task> action, TaskCompletionSource owner)
    { try { await action(); } catch { if (!_closed) _inputIssue = GroupSecurityStrings.Get("InvalidProfile"); } finally { owner.TrySetResult(); if (!_closed) RefreshState(); } }
    private async Task ConfigureSecureAsync()
    {
        var dialog = _secureDialog = new SecureConnectionWindow(Owner as Window ?? this, _expectedKeyringProject, _credentialScope, _credentialOwner);
        try { await dialog.ShowDialog(this); await dialog.OperationCompletion;
            if (dialog.Selection is { } selected) { _secureReference = selected; _passwordSource = dialog.PasswordSource; _address.Text = selected.TunnelAddress.ToString(); } }
        finally { _secureDialog = null; }
        RefreshState();
    }
    private async Task DisconnectAsync()
    {
        try { await _connection.DisconnectAsync(); }
        catch { }
        if (!_closed) RefreshState();
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        var hasChild = _secureDialog is not null || _groupDialog is not null || _groupConfirmation is not null;
        if (!hasChild && (_operation?.IsCompleted ?? true) && _cancelOperation.IsCompleted
            && _connection.Snapshot.Phase is not (ReadOnlyConnectionPhase.Connecting or ReadOnlyConnectionPhase.Disconnecting))
        {
            if (IPAddress.TryParse(_gateway.Text, out var gateway) && int.TryParse(_port.Text, out var port)
                && IndividualAddress.TryParse(_address.Text ?? "", out var address))
                try { Options = SelectedOptions(gateway, port, address); } catch (Exception error) when (error is ArgumentException or KnxConnectionException) { }
            _allowClose = true; try { _lifetime.Cancel(); } catch { } return;
        }
        e.Cancel = true; if (_closing) return; _closing = true;
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _closeOperation = owner.Task;
        var pending = Task.WhenAll(_operation ?? Task.CompletedTask, _cancelOperation);
        try { _lifetime.Cancel(); } catch { }
        _secureDialog?.Close(); _groupDialog?.Close(); _groupConfirmation?.Close();
        _ = FinishCloseAsync(pending, owner);
    }
    private async Task FinishCloseAsync(Task pending, TaskCompletionSource owner)
    {
        try
        {
            if (_connection.Snapshot.Phase is ReadOnlyConnectionPhase.Connecting or ReadOnlyConnectionPhase.Disconnecting) await _connection.DisconnectAsync();
            await pending;
            if (_connection.Snapshot.Phase is ReadOnlyConnectionPhase.Connecting or ReadOnlyConnectionPhase.Disconnecting) await _connection.DisconnectAsync();
            _allowClose = true; Close(); owner.TrySetResult();
        }
        catch { _closing = false; _closeOperation = null; RefreshState(); owner.TrySetException(new IOException("Connection input cleanup failed.")); }
    }
}
