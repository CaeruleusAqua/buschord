using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.App;

/// <summary>Offline verification and exact protected interface selection. No network is opened here.</summary>
public sealed class SecureConnectionWindow : Window
{
    private readonly TextBox _project = new() { Name = "SecureProjectLabel", Classes = { "wb-field" } };
    private readonly TextBox _file = new() { Name = "SecureKeyringPath", IsReadOnly = true, Classes = { "wb-field" } };
    private readonly TextBox _fingerprint = new() { Name = "SecureKeyringFingerprint", IsReadOnly = true, Classes = { "wb-field" } };
    private readonly ComboBox _interfaces = new() { Name = "SecureInterfaceChoice", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Name = "SecureProfileStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _verify = new() { Name = "SecureVerify", Classes = { "wb-button", "wb-quiet" } };
    private readonly Button _accept = new() { Name = "SecureAccept", Classes = { "wb-button", "wb-primary" } };
    private readonly CancellationTokenSource _lifetime = new();
    public ISecurePasswordSource PasswordSource { get; }
    private ProtectedKeyringProfileSummary? _summary;
    private Task _operation = Task.CompletedTask;
    private bool _closing, _allowClose;
    public SecureKeyringReference? Selection { get; private set; }
    public Task OperationCompletion => _operation;
    public SecureConnectionWindow(Window promptOwner, string expectedProjectLabel)
    {
        PasswordSource = new PromptSecurePasswordSource(promptOwner); _project.Text = expectedProjectLabel;
        Title = L("Title"); Width = 720; Height = 540; MinWidth = 420; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = L("Boundary"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = L("ProjectLabel"), Classes = { "wb-label" } }); body.Children.Add(_project);
        AutomationProperties.SetName(_project, L("ProjectLabel")); AutomationProperties.SetName(_file, L("File"));
        AutomationProperties.SetName(_fingerprint, L("Fingerprint")); AutomationProperties.SetName(_interfaces, L("Interfaces"));
        var browse = new Button { Content = L("File"), Name = "SecureKeyringBrowse", Classes = { "wb-button", "wb-quiet" } };
        browse.Click += (_, _) => { if (_operation.IsCompleted) _operation = BrowseAsync(); };
        body.Children.Add(browse); body.Children.Add(_file); body.Children.Add(_fingerprint); body.Children.Add(_interfaces); body.Children.Add(_status);
        _interfaces.ItemTemplate = new FuncDataTemplate<ProtectedTunnelChoice>((choice, _) => new TextBlock
            { Text = choice is null ? "" : $"Host {choice.Host} · User {choice.UserId} · IA {choice.TunnelAddress}" });
        _verify.Content = L("Verify"); _accept.Content = L("Choose"); _accept.IsEnabled = false;
        _verify.Click += (_, _) => { if (_operation.IsCompleted) _operation = VerifyAsync(); };
        _accept.Click += (_, _) =>
        {
            if (_summary is null || _interfaces.SelectedItem is not ProtectedTunnelChoice selected) return;
            Selection = new(_file.Text!, _summary.ProjectLabel, selected.Host, selected.UserId, selected.TunnelAddress,
                expectedSourceFingerprint: _summary.SourceFingerprint); Close();
        };
        _interfaces.SelectionChanged += (_, _) => _accept.IsEnabled = _summary is not null && _interfaces.SelectedItem is ProtectedTunnelChoice;
        _project.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) InvalidateSelection(); };
        var close = new Button { Content = LiveConnectionStrings.Get("Close"), IsCancel = true, Classes = { "wb-button", "wb-quiet" } };
        close.Click += (_, _) => Close();
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(close); actions.Children.Add(_verify); actions.Children.Add(_accept);
        var root = new Grid { RowDefinitions = new("*,Auto") }; root.Children.Add(new ScrollViewer { Content = body });
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions }; Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closing += (_, args) =>
        {
            if (_allowClose || _operation.IsCompleted) return;
            args.Cancel = true; if (_closing) return; _closing = true; _lifetime.Cancel();
            var pending = _operation; _operation = CloseJoinedAsync(pending);
        };
        Closed += (_, _) => _lifetime.Dispose(); WorkbenchDialogChrome.Apply(this);
    }
    private static string L(string key) => SecureConnectionStrings.Get(key);
    private void InvalidateSelection() { _summary = null; Selection = null; _interfaces.ItemsSource = null; _fingerprint.Text = ""; _accept.IsEnabled = false; }
    private async Task BrowseAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = L("File"), AllowMultiple = false,
                FileTypeFilter = [new("KNX Keyring") { Patterns = ["*.knxkeys", "*.xml"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { InvalidateSelection(); _file.Text = path; }
        }
        catch { _status.Text = L("CredentialSourceUnavailable"); }
    }
    private async Task VerifyAsync()
    {
        InvalidateSelection(); _verify.IsEnabled = false; _project.IsEnabled = false;
        try
        {
            _summary = await ProtectedKeyringProfileReader.ReadAsync(_file.Text ?? "", _project.Text ?? "", new PromptSecurePasswordSource(this), _lifetime.Token);
            _fingerprint.Text = _summary.SourceFingerprint; _interfaces.ItemsSource = _summary.Interfaces;
            if (_summary.Interfaces.Count > 0) _interfaces.SelectedIndex = 0;
            _status.Text = _summary.Interfaces.Count > 0 ? L("Choose") : L("CredentialBindingMismatch");
        }
        catch (OperationCanceledException) { _status.Text = L("Cancelled"); }
        catch (KnxConnectionException error) { _status.Text = L(error.Failure.ToString()); }
        catch { _status.Text = L("CredentialSourceUnavailable"); }
        finally { _verify.IsEnabled = true; _project.IsEnabled = true; }
    }
    private async Task CloseJoinedAsync(Task pending) { try { await pending; } finally { _allowClose = true; Close(); } }
}
