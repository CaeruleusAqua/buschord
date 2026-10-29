using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;

namespace Knx.App;

/// <summary>One new explicit operation grant for the same immutable selection; no file/profile wizard and no automatic consent.</summary>
public sealed class GroupStateConfirmationWindow : Window
{
    private readonly WorkbenchGroupSelection _selection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CheckBox _confirmed = new() { Name = "GroupRenewConfirmed" };
    private readonly Button _accept = new() { Name = "GroupRenewAccept", IsDefault = true, Classes = { "wb-button", "wb-primary" } };
    private readonly TextBlock _status = new() { Name = "GroupRenewStatus", TextWrapping = TextWrapping.Wrap };
    private Task _operation = Task.CompletedTask;
    private Task? _closing;
    private bool _allowClose;
    private bool _closed;
    private readonly List<Action> _translations = [];
    public WorkbenchGroupSelection? Selection { get; private set; }
    public Task OperationCompletion => _closing ?? _operation;
    public GroupStateConfirmationWindow(WorkbenchGroupSelection selection)
    {
        _selection = selection; Title = G("Reconfirm"); Width = 700; Height = 600; MinWidth = 420; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap }; body.Children.Add(summary); _translations.Add(() => summary.Text = GroupSecurityDialogText.Summary(selection.Candidate));
        var registryPin = new TextBox { Name = "GroupRenewRegistryPin", Text = selection.RegistryFingerprint, IsReadOnly = true, Classes = { "wb-field" } }; body.Children.Add(registryPin);
        var keyringPin = new TextBox { Name = "GroupRenewKeyringPin", Text = selection.BaseConnection.Keyring!.ExpectedSourceFingerprint, IsReadOnly = true, Classes = { "wb-field" } }; body.Children.Add(keyringPin);
        var confirmationText = new TextBlock { TextWrapping = TextWrapping.Wrap }; _confirmed.Content = confirmationText;
        _translations.Add(() => { confirmationText.Text = G("ConfirmCurrent"); AutomationProperties.SetName(_confirmed, G("ConfirmCurrent")); AutomationProperties.SetName(registryPin, G("Fingerprint")); AutomationProperties.SetName(keyringPin, SecureConnectionStrings.Get("Fingerprint")); }); body.Children.Add(_confirmed); body.Children.Add(_status);
        var close = new Button { Name = "GroupRenewClose", Content = LiveConnectionStrings.Get("Close"), IsCancel = true, Classes = { "wb-button", "wb-quiet" } }; close.Click += (_, _) => Close();
        _accept.Content = G("Reconfirm"); AutomationProperties.SetName(_accept, G("Reconfirm")); _accept.IsEnabled = false;
        _translations.Add(() => { Title = G("Reconfirm"); close.Content = LiveConnectionStrings.Get("Close"); _accept.Content = G("Reconfirm"); AutomationProperties.SetName(_accept, G("Reconfirm")); });
        _confirmed.IsCheckedChanged += (_, _) => _accept.IsEnabled = _confirmed.IsChecked == true && _operation.IsCompleted && _closing is null;
        _accept.Click += (_, _) => Start();
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 }; footer.Children.Add(close); footer.Children.Add(_accept);
        var root = new Grid { RowDefinitions = new("*,Auto") }; root.Children.Add(new ScrollViewer { Name = "GroupRenewScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var border = new Border { Child = footer, Classes = { "wb-dialog-footer" } }; Grid.SetRow(border, 1); root.Children.Add(border); Content = root;
        Closing += (_, e) =>
        {
            if (_allowClose) return; e.Cancel = true; if (_closing is not null) return;
            var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _closing = owner.Task;
            try { _lifetime.Cancel(); } catch { } _ = FinishCloseAsync(owner);
        };
        Strings.Instance.PropertyChanged += OnLanguage; Closed += (_, _) => { _closed = true; Strings.Instance.PropertyChanged -= OnLanguage; };
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); } }; WorkbenchDialogChrome.Apply(this); foreach (var translate in _translations) translate();
    }
    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == "Item[]") Dispatcher.UIThread.Post(() => { if (!_closed) foreach (var translate in _translations) translate(); }); }
    private static string G(string key) => GroupSecurityStrings.Get(key);
    private void Start()
    {
        if (_operation.IsCompleted == false || _closing is not null || _confirmed.IsChecked != true) return;
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operation = owner.Task; _accept.IsEnabled = _confirmed.IsEnabled = false; _ = RenewAsync(owner);
    }
    private async Task RenewAsync(TaskCompletionSource owner)
    {
        try
        {
            var selection = await _selection.ReconfirmNewOperationAsync(_selection.BaseConnection, true, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested(); if (_closing is not null) throw new OperationCanceledException(_lifetime.Token); Selection = selection;
        }
        catch (OperationCanceledException) { _status.Text = G("Cancelled"); }
        catch (Knx.Bus.Connections.KnxConnectionException error) { _status.Text = G(error.Failure.ToString()); }
        catch { _status.Text = G("InvalidProfile"); }
        finally { owner.TrySetResult(); if (Selection is not null) Close(); else if (_closing is null) { _confirmed.IsEnabled = true; _accept.IsEnabled = _confirmed.IsChecked == true; } }
    }
    private async Task FinishCloseAsync(TaskCompletionSource owner)
    { try { await _operation; _lifetime.Dispose(); _allowClose = true; Close(); owner.TrySetResult(); } catch { Selection = null; _allowClose = true; Close(); owner.TrySetException(new IOException("Group confirmation cleanup failed.")); } }
}
