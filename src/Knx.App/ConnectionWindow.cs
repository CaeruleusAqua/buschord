using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Workbench;

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
    private Task? _operation;
    private bool _closed, _closing;
    public ReadOnlyConnectionOptions? Options { get; private set; }
    public Task OperationCompletion => _operation ?? Task.CompletedTask;

    public ConnectionWindow(ReadOnlyWorkbenchConnection connection, ReadOnlyConnectionOptions? initial = null)
    {
        _connection = connection;
        Width = 620; Height = 470; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        Options = initial;
        if (initial is not null) { _gateway.Text = initial.Gateway.Address.ToString(); _port.Text = initial.Port.ToString(); _address.Text = initial.RequestedAddress.ToString(); }
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        void Field(string key, TextBox input)
        {
            var label = new TextBlock { Classes = { "wb-label" } }; body.Children.Add(label); body.Children.Add(input);
            _translations.Add(() => { label.Text = L(key); AutomationProperties.SetName(input, L(key)); });
        }
        Field("Gateway", _gateway); Field("Port", _port); Field("TunnelAddress", _address);
        var explanation = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "wb-subtle" } };
        _translations.Add(() => explanation.Text = L("GroupOnly")); body.Children.Add(explanation); body.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _close, _cancel, _disconnect, _connect }) { button.Margin = new Thickness(4); actions.Children.Add(button); }
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions };
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _connect.Click += (_, _) => _operation = ConnectAsync();
        _cancel.Click += (_, _) => _operation = DisconnectAsync();
        _disconnect.Click += (_, _) => _operation = DisconnectAsync();
        _close.Click += (_, _) => Close();
        _connection.Changed += OnChanged;
        Strings.Instance.PropertyChanged += OnLanguage;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _connection.Changed -= OnChanged; Strings.Instance.PropertyChanged -= OnLanguage; };
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
        _disconnect.Content = L("Disconnect"); _close.Content = L("Close");
        foreach (var translate in _translations) translate(); RefreshState();
    }
    public static string Status(ReadOnlyConnectionSnapshot snapshot)
    {
        var issue = snapshot.Issue != ReadOnlyConnectionIssue.None;
        var text = L(issue ? snapshot.Issue.ToString() : snapshot.Phase.ToString());
        return text + (snapshot.EndpointId is { } endpoint ? " · " + endpoint : "")
            + (snapshot.AssignedAddress is { } assigned ? " · " + assigned : "") + " · " + L("ReadOnly");
    }
    private void OnChanged(ReadOnlyConnectionSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    { if (!_closed && snapshot.Generation == _connection.Snapshot.Generation) RefreshState(); });
    private void RefreshState()
    {
        var snapshot = _connection.Snapshot; _status.Text = Status(snapshot);
        var busy = snapshot.Phase is ReadOnlyConnectionPhase.Connecting or ReadOnlyConnectionPhase.Disconnecting;
        var connected = snapshot.Phase == ReadOnlyConnectionPhase.Connected;
        _gateway.IsEnabled = _port.IsEnabled = _address.IsEnabled = !busy && !connected;
        _connect.IsEnabled = !busy && !connected && snapshot.Issue != ReadOnlyConnectionIssue.CleanupFailed;
        _cancel.IsEnabled = snapshot.Phase == ReadOnlyConnectionPhase.Connecting;
        _disconnect.IsEnabled = connected || snapshot.Issue == ReadOnlyConnectionIssue.CleanupFailed;
    }
    private async Task ConnectAsync()
    {
        try
        {
            if (!IPAddress.TryParse(_gateway.Text, out var gateway) || !int.TryParse(_port.Text, out var port)
                || !IndividualAddress.TryParse(_address.Text ?? "", out var address)) throw new ArgumentException();
            Options = new(gateway, port, address);
            await _connection.ConnectAsync(Options);
        }
        catch (ArgumentException) { if (!_closed) _status.Text = L("Invalid"); return; }
        catch (Exception) { /* The connection snapshot carries the failure; no private endpoint exception text is logged. */ }
        if (!_closed) RefreshState();
    }
    private async Task DisconnectAsync()
    {
        try { await _connection.DisconnectAsync(); }
        catch { }
        if (!_closed) RefreshState();
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_connection.Snapshot.Phase is not (ReadOnlyConnectionPhase.Connecting or ReadOnlyConnectionPhase.Disconnecting))
        {
            if (IPAddress.TryParse(_gateway.Text, out var gateway) && int.TryParse(_port.Text, out var port)
                && IndividualAddress.TryParse(_address.Text ?? "", out var address))
                try { Options = new(gateway, port, address); } catch (ArgumentException) { }
            return;
        }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _operation = CloseAfterCancelAsync();
    }
    private async Task CloseAfterCancelAsync()
    {
        try { await _connection.DisconnectAsync(); }
        catch { _closing = false; RefreshState(); return; }
        _closing = false; Close();
    }
}
