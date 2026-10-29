using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.App;

/// <summary>Offline selection, signature verification and explicit password-source decisions.</summary>
public sealed class SecureConnectionWindow : Window
{
    private readonly TextBox _project = new() { Name = "SecureProjectLabel", Classes = { "wb-field" } };
    private readonly TextBox _file = new() { Name = "SecureKeyringPath", IsReadOnly = true, Classes = { "wb-field" } };
    private readonly TextBox _fingerprint = new() { Name = "SecureKeyringFingerprint", IsReadOnly = true, Classes = { "wb-field" } };
    private readonly ComboBox _interfaces = new() { Name = "SecureInterfaceChoice", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Name = "SecureProfileStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _passwordMode = new() { Name = "SecurePasswordMode", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _vaultState = new() { Name = "SecureVaultState", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _interfaceLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, Button> _vaultButtons = [];
    private readonly Button _browse, _read, _verify, _accept;
    private readonly CredentialSelectionSession _selection;
    private readonly ISecurePasswordSource _prompt;
    private readonly ISecurePasswordSource _storagePrompt;
    private readonly WorkbenchCredentialVaultOwner _vaultOwner;
    private readonly bool _ownsOwner;
    private Task _browseOperation = Task.CompletedTask;
    private Task? _closeOperation;
    private bool _closing, _allowClose, _closed, _refreshing, _browsing;
    public ISecurePasswordSource PasswordSource => _selection.PasswordSource;
    public SecureKeyringReference? Selection { get; private set; }
    public Task OperationCompletion => _closeOperation ?? Task.WhenAll(_browseOperation, _selection.OperationCompletion);
    public SecureConnectionWindow(Window promptOwner, string expectedProjectLabel, string? stableProjectScope = null, WorkbenchCredentialVaultOwner? vaultOwner = null)
    {
        _vaultOwner = vaultOwner ?? new(); _ownsOwner = vaultOwner is null;
        _selection = new(_vaultOwner, vaultOwner is null ? null : stableProjectScope); _prompt = new PromptSecurePasswordSource(promptOwner); _project.Text = expectedProjectLabel;
        _storagePrompt = new PromptSecurePasswordSource(promptOwner, V("StoragePasswordPrompt"), V("ConfirmStorage"));
        Title = L("Title"); Width = 760; Height = 700; MinWidth = 420; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = L("Boundary"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = L("ProjectLabel"), Classes = { "wb-label" } }); body.Children.Add(_project);
        AutomationProperties.SetName(_project, L("ProjectLabel")); AutomationProperties.SetName(_file, L("File"));
        AutomationProperties.SetName(_fingerprint, L("Fingerprint")); AutomationProperties.SetName(_interfaces, V("Candidates"));
        _browse = Button("SecureKeyringBrowse", L("File")); _read = Button("SecureReadSelection", V("ReadCandidates"));
        _browse.Click += (_, _) => StartBrowse(); _read.Click += (_, _) => Start(_selection.ReadCandidatesAsync);
        var files = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 }; files.Children.Add(_browse); files.Children.Add(_read);
        body.Children.Add(files); body.Children.Add(_file); body.Children.Add(_fingerprint);
        body.Children.Add(_interfaceLabel); body.Children.Add(_interfaces);
        _interfaces.ItemTemplate = new FuncDataTemplate<ProtectedTunnelChoice>((choice, _) => new TextBlock { Text = choice is null ? "" : $"Host {choice.Host} · User {choice.UserId} · IA {choice.TunnelAddress}" });
        _interfaces.SelectionChanged += (_, _) => { if (!_refreshing) _selection.Select(_interfaces.SelectedItem as ProtectedTunnelChoice); };
        body.Children.Add(_passwordMode);
        body.Children.Add(new TextBlock { Text = V("StorageNotice"), TextWrapping = TextWrapping.Wrap, Classes = { "wb-subtle" } });
        var storage = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        foreach (var key in new[] { "Probe", "UseStored", "SaveNew", "Replace", "Delete" }) { var button = _vaultButtons[key] = Button("SecureVault" + key, V(key)); storage.Children.Add(button); }
        _vaultButtons["Probe"].Click += (_, _) => Start(_selection.ProbeAsync);
        _vaultButtons["UseStored"].Click += (_, _) => Start(_selection.UseStoredAsync);
        _vaultButtons["SaveNew"].Click += (_, _) => Start(() => _selection.SaveAsync(_storagePrompt, SecureCredentialWriteMode.CreateNew, _prompt));
        _vaultButtons["Replace"].Click += (_, _) => Start(() => _selection.SaveAsync(_storagePrompt, SecureCredentialWriteMode.ReplaceMatching, _prompt));
        _vaultButtons["Delete"].Click += (_, _) => Start(_selection.DeleteAsync);
        body.Children.Add(storage); body.Children.Add(_vaultState); body.Children.Add(_status);
        _verify = Button("SecureVerify", L("Verify")); _accept = Button("SecureAccept", L("Choose")); _accept.Classes.Remove("wb-quiet"); _accept.Classes.Add("wb-primary");
        _verify.Click += (_, _) => Start(() => _selection.VerifyEphemeralAsync(_prompt));
        _accept.Click += (_, _) => { if (_selection.VerifiedReference is { } selected) { Selection = selected; Close(); } };
        _project.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) ChangedSource(); };
        _file.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) ChangedSource(); };
        var close = Button("SecureClose", LiveConnectionStrings.Get("Close")); close.IsCancel = true; close.Click += (_, _) => Close();
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 }; actions.Children.Add(close); actions.Children.Add(_verify); actions.Children.Add(_accept);
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Name = "SecureSelectionScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions }; Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _selection.Changed += OnChanged; ChangedSource();
        KeyDown += (_, args) => { if (!args.Handled && args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closing += (_, args) =>
        {
            if (_allowClose) return; args.Cancel = true; if (_closing) return; _closing = true;
            if (_selection.VerifiedReference is null) Selection = null;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _closeOperation = completion.Task;
            Dispatcher.UIThread.Post(async () =>
            {
                var failed = false;
                try { await Task.WhenAll(_selection.DisposeAsync().AsTask(), _browseOperation); if (_ownsOwner) await _vaultOwner.DisposeAsync(); }
                catch { failed = true; Selection = null; }
                _allowClose = true; Close(); if (failed) completion.TrySetException(new IOException("Credential selection cleanup failed.")); else completion.TrySetResult();
            });
        };
        Closed += (_, _) => { _closed = true; _selection.Changed -= OnChanged; }; WorkbenchDialogChrome.Apply(this); Refresh();
    }
    private static Button Button(string name, string text) { var button = new Button { Name = name, Content = text, Classes = { "wb-button", "wb-quiet" } }; AutomationProperties.SetName(button, text); return button; }
    private static string L(string key) => SecureConnectionStrings.Get(key);
    private static string V(string key) => CredentialVaultStrings.Get(key);
    private void ChangedSource() { Selection = null; _selection.SetSource(_file.Text ?? "", _project.Text ?? ""); Refresh(); }
    private void OnChanged() { if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(() => { if (!_closed) Refresh(); }); }
    private void Refresh()
    {
        if (_closed) return; var state = _selection.Snapshot; var busy = state.Busy || _browsing || _closing;
        _refreshing = true;
        try { if (!ReferenceEquals(_interfaces.ItemsSource, state.Candidates?.Interfaces)) _interfaces.ItemsSource = state.Candidates?.Interfaces; _interfaces.SelectedItem = state.Choice; _fingerprint.Text = state.Candidates?.SourceFingerprint ?? ""; }
        finally { _refreshing = false; }
        _project.IsEnabled = _browse.IsEnabled = _read.IsEnabled = _verify.IsEnabled = !busy; _interfaces.IsEnabled = !busy; _accept.IsEnabled = !busy && state.Verified;
        _passwordMode.Text = V(state.Verified ? state.UsesStored ? "StoredSelected" : "EphemeralSelected" : "Unverified");
        _interfaceLabel.Text = state.Verified ? L("Interfaces") : V("Candidates"); AutomationProperties.SetName(_interfaces, _interfaceLabel.Text);
        _vaultState.Text = !_selection.HasStableScope ? V("ProjectScopeUnavailable") : V(state.Capability is { } capability ? "Capability" + capability : "CapabilityUnchecked");
        _vaultButtons["Probe"].IsEnabled = !busy && _selection.HasStableScope;
        foreach (var key in new[] { "UseStored", "SaveNew", "Replace", "Delete" }) _vaultButtons[key].IsEnabled = !busy && _selection.HasStableScope && state.Capability == SecureCredentialCapability.Ready && state.Choice is not null;
        _status.Text = CredentialVaultStrings.Contains(state.Status) ? V(state.Status) : L(state.Status);
        _status.IsVisible = state.Status is not ("StoredSelected" or "EphemeralSelected");
    }
    private void Start(Func<Task> action) { if (_closing || _browsing || _selection.Snapshot.Busy) return; ChangedSource(); _ = action(); }
    private void StartBrowse()
    {
        if (_closing || _browsing || _selection.Snapshot.Busy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _browseOperation = completion.Task; _browsing = true; Refresh(); _ = BrowseAsync(completion);
    }
    private async Task BrowseAsync(TaskCompletionSource completion)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = L("File"), AllowMultiple = false, FileTypeFilter = [new("KNX Keyring") { Patterns = ["*.knxkeys", "*.xml"] }] });
            if (!_closing && files.FirstOrDefault()?.TryGetLocalPath() is { } path) { _file.Text = path; await _selection.ReadCandidatesAsync(); }
        }
        catch { if (!_closed) _status.Text = L("CredentialSourceUnavailable"); }
        finally { _browsing = false; completion.TrySetResult(); if (!_closed) Refresh(); }
    }
}
