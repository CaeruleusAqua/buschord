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
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.App;

public sealed record M112BaselineSelection(Project Project, string Path, string Sha256);

public sealed class M112CommissioningWindow : Window
{
    private readonly M112CommissioningSession _session;
    private readonly Func<Window, Task<M112BaselineSelection?>> _loadBaseline;
    private readonly Func<Task> _prepareNetwork;
    private M112BaselineSelection? _baseline;
    private string? _journalPath;
    private readonly TextBox _gateway = new() { Name = "M112Gateway", Classes = { "wb-field" } };
    private readonly TextBox _port = new() { Name = "M112Port", Text = "3671", Classes = { "wb-field" } };
    private readonly TextBox _source = new() { Name = "M112TunnelAddress", Text = "1.1.250", Classes = { "wb-field" } };
    private readonly ComboBox _mode = new() { Name = "M112TransportMode", SelectedIndex = -1 };
    private readonly Button _secure = new() { Name = "M112ConfigureSecure", Classes = { "wb-button", "wb-quiet" } };
    private readonly TextBlock _secureBinding = new() { Name = "M112SecureBinding", TextWrapping = TextWrapping.Wrap };
    private SecureKeyringReference? _secureReference;
    private ISecurePasswordSource? _passwordSource;
    private SecureConnectionWindow? _secureDialog;
    private readonly string _expectedKeyringProject;
    private readonly string? _credentialScope;
    private readonly WorkbenchCredentialVaultOwner? _credentialOwner;
    private readonly TextBox _target = new() { Name = "M112ConfirmTarget", Classes = { "wb-field" } };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "M112Status", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _paths = new() { Name = "M112Sources", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _review = new() { Name = "M112Review", IsReadOnly = true, AcceptsReturn = true, MinHeight = 140, TextWrapping = TextWrapping.Wrap, Classes = { "wb-field" } };
    private readonly TextBlock _identity = new() { Name = "M112Identity", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _recovery = new() { Name = "M112RecoveryPath", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Classes = { "wb-field" } };
    private readonly CheckBox _identityConfirmed = new() { Name = "M112IdentityConfirmed" };
    private readonly CheckBox _planConfirmed = new() { Name = "M112PlanConfirmed" };
    private readonly CheckBox _closeUnverified = new() { Name = "M112CloseUnverified", IsVisible = false };
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly List<Action> _translations = [];
    private Task _operation = Task.CompletedTask;
    private bool _closed, _closing, _allowClose, _loading, _refreshing;
    public Task OperationCompletion => _operation;
    public bool MayClose => !_session.RecoveryRequired || _closeUnverified.IsChecked == true;

    public M112CommissioningWindow(M112CommissioningSession session, Func<Window, Task<M112BaselineSelection?>> loadBaseline,
        ReadOnlyConnectionOptions? initial = null, Func<Task>? prepareNetwork = null, string expectedKeyringProject = "",
        string? credentialScope = null, WorkbenchCredentialVaultOwner? credentialOwner = null)
    {
        _session = session; _loadBaseline = loadBaseline; _prepareNetwork = prepareNetwork ?? (() => Task.CompletedTask);
        _expectedKeyringProject = expectedKeyringProject;
        _credentialScope = credentialScope; _credentialOwner = credentialOwner;
        Width = 900; Height = 820; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        if (initial is not null) { _gateway.Text = initial.Gateway.Address.ToString(); _port.Text = initial.Port.ToString(); _source.Text = initial.RequestedAddress.ToString(); }
        if (initial is not null) _mode.SelectedIndex = initial.Profile.Kind == KnxTransportKind.IpSecureUdp ? 1 : 0;
        if (initial?.Profile.Kind == KnxTransportKind.IpSecureUdp) { _secureReference = initial.Profile.Keyring; _passwordSource = initial.Profile.PasswordSource; }
        if (!CredentialSelectionSession.MatchesStoredBinding(_credentialScope, _secureReference, _passwordSource)) { _secureReference = null; _passwordSource = null; }
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        body.Children.Add(_scope);
        void Field(string key, TextBox input, bool live = false)
        {
            var label = new TextBlock { Classes = { "wb-label" } }; body.Children.Add(label); body.Children.Add(input);
            _translations.Add(() => { label.Text = live ? LiveConnectionStrings.Get(key) : L(key); AutomationProperties.SetName(input, label.Text); });
        }
        Field("Gateway", _gateway, true); Field("Port", _port, true); Field("TunnelAddress", _source, true);
        body.Children.Add(_mode); body.Children.Add(_secure); body.Children.Add(_secureBinding);
        _mode.SelectionChanged += (_, _) => { if (!_refreshing && !_session.IsBusy && !_session.RecoveryRequired) ResetReview(); };
        _secure.Click += (_, _) => { if (_operation.IsCompleted) _operation = RunAsync(ConfigureSecureAsync); };
        var sources = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        sources.Children.Add(Button("Baseline", async () => { ResetReview(); _baseline = await _loadBaseline(this); ResetReview(); }));
        sources.Children.Add(Button("Journal", async () =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = L("Journal"), AllowMultiple = false });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) { _journalPath = M112PrivatePaths.ExistingDirectory(path); ResetReview(); }
        }));
        body.Children.Add(sources); body.Children.Add(_paths); body.Children.Add(_review);
        body.Children.Add(_identity); body.Children.Add(_identityConfirmed); body.Children.Add(_planConfirmed);
        Field("TargetPrompt", _target); body.Children.Add(_status); body.Children.Add(_recovery); body.Children.Add(_closeUnverified);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var key in new[] { "Close", "Cancel", "Preview", "ReadIdentity", "Resume", "Apply" })
            actions.Children.Add(Button(key, key switch
            {
                "Close" => () => { Close(); return Task.CompletedTask; },
                "Cancel" => () => { _session.Cancel(); return Task.CompletedTask; },
                "Preview" => PreviewAsync, "ReadIdentity" => async () => { await _prepareNetwork(); if (session.Snapshot.Resume is not null) await session.ReadRecoveryIdentityAsync(); else await session.ReadIdentityAsync(); },
                "Apply" => async () => { if (_planConfirmed.IsChecked != true) throw new InvalidOperationException("Confirmation");
                    if (session.Snapshot.Resume is not null) await session.RecoverAsync(_target.Text ?? "", session.Snapshot.Fingerprint ?? "");
                    else await session.ApplyAsync(_target.Text ?? "", session.Snapshot.Fingerprint ?? ""); },
                _ => ResumeAsync
            }));
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions }; Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _identityConfirmed.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing || session.IsBusy || session.Snapshot.Phase is not (M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady)) return;
            try { session.ConfirmIdentity(_identityConfirmed.IsChecked == true); } catch { _status.Text = L("Stale"); }
            RefreshState();
        };
        _planConfirmed.IsCheckedChanged += (_, _) => RefreshState();
        _target.TextChanged += (_, _) => RefreshState();
        foreach (var input in new[] { _gateway, _port, _source }) input.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || _refreshing || _session.RecoveryRequired) return;
            if (_session.IsBusy) { _prepared = false; _session.Invalidate(); }
            else ResetReview();
        };
        session.Changed += OnChanged; Strings.Instance.PropertyChanged += OnLanguage;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; session.Changed -= OnChanged; Strings.Instance.PropertyChanged -= OnLanguage; };
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); } };
        RefreshLanguage(); WorkbenchDialogChrome.Apply(this);
    }
    private static string L(string key) => M112CommissioningStrings.Get(key);
    private Button Button(string key, Func<Task> action)
    {
        var button = new Button { Name = "M112" + key, Classes = { "wb-button", key == "Apply" ? "wb-danger" : "wb-quiet" } };
        _buttons[key] = button; button.Click += (_, _) =>
        {
            if (key == "Cancel") { _session.Cancel(); return; }
            if (key == "Close") { Close(); return; }
            if (_operation.IsCompleted) _operation = RunAsync(action);
        };
        return button;
    }
    private async Task RunAsync(Func<Task> action)
    {
        _localIssue = null; _loading = true; RefreshState();
        try { await action(); }
        catch (Exception error) { _localIssue = error.Message is "PrivatePath" or "Confirmation" or "RecoveryRequired" ? error.Message : "OperationFailed"; }
        finally { _loading = false; RefreshState(); }
    }
    private void ResetReview()
    {
        _session.ClearReview();
        _closeUnverified.IsChecked = false;
        _identityConfirmed.IsChecked = _planConfirmed.IsChecked = false;
        _target.Text = ""; _review.Text = ""; _identity.Text = "";
        _paths.Text = (_baseline?.Path ?? "") + Environment.NewLine + (_journalPath ?? "");
        _buttons.GetValueOrDefault("Apply")?.SetCurrentValue(IsEnabledProperty, false);
        _prepared = false; RefreshState();
    }
    private bool _prepared;
    private string? _localIssue;
    private async Task PreviewAsync()
    {
        _prepared = false;
        if (_baseline is null || _journalPath is null) throw new InvalidOperationException("PrivatePath");
        await _session.PrepareAsync(_baseline.Project, _baseline.Path, _baseline.Sha256, Options(), _journalPath);
        _prepared = _session.Snapshot.Phase == M112CommissioningPhase.Reviewed;
        _identityConfirmed.IsChecked = _planConfirmed.IsChecked = false;
    }
    private async Task ResumeAsync()
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new() { Title = L("Resume"), AllowMultiple = false,
            FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) await InspectRecoveryAsync(path);
    }
    private ReadOnlyConnectionOptions Options()
    {
        if (!IPAddress.TryParse(_gateway.Text, out var address) || !int.TryParse(_port.Text, out var port)
            || !IndividualAddress.TryParse(_source.Text ?? "", out var source)) throw new InvalidOperationException("Target");
        return _mode.SelectedIndex switch
        {
            0 => new(address, port, source),
            1 when _secureReference is not null && CredentialSelectionSession.MatchesStoredBinding(_credentialScope, _secureReference, _passwordSource) => new(KnxConnectionProfile.IpSecure(new(address, port), _secureReference, _passwordSource)),
            _ => throw new InvalidOperationException("Target")
        };
    }
    private async Task ConfigureSecureAsync()
    {
        ResetReview(); var dialog = _secureDialog = new SecureConnectionWindow(this, _expectedKeyringProject, _credentialScope, _credentialOwner);
        try
        {
            await dialog.ShowDialog(this); await dialog.OperationCompletion;
            if (dialog.Selection is { } selected) { _secureReference = selected; _passwordSource = dialog.PasswordSource; _source.Text = selected.TunnelAddress.ToString(); }
        }
        finally { _secureDialog = null; }
    }
    private async Task InspectRecoveryAsync(string explicitlySelectedPath)
    {
        await _session.PrepareRecoveryAsync(explicitlySelectedPath, Options());
        _prepared = _session.Snapshot.Phase == M112CommissioningPhase.ResumeReviewed;
        _identityConfirmed.IsChecked = _planConfirmed.IsChecked = false; _target.Text = ""; RefreshState();
    }
    private void OnChanged(M112CommissioningSnapshot snapshot) => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshState(); });
    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { if (args.PropertyName == "Item[]") Dispatcher.UIThread.Post(() => { if (!_closed) RefreshLanguage(); }); }
    private void RefreshLanguage()
    {
        Title = L("Title"); _scope.Text = L("Scope") + " " + L("NetworkNotice");
        _refreshing = true; var mode = _mode.SelectedIndex; _mode.ItemsSource = new[] { SecureConnectionStrings.Get("Plain"), SecureConnectionStrings.Get("Secure") }; _mode.SelectedIndex = mode; _refreshing = false;
        AutomationProperties.SetName(_mode, SecureConnectionStrings.Get("Mode")); _secure.Content = SecureConnectionStrings.Get("Title");
        foreach (var (key, button) in _buttons) { button.Content = L(key); AutomationProperties.SetName(button, L(key)); }
        _identityConfirmed.Content = L("ConfirmIdentity"); _planConfirmed.Content = L("ConfirmPlan");
        _closeUnverified.Content = L("CloseUnverified");
        foreach (var translate in _translations) translate(); RefreshState();
    }
    private void RefreshState()
    {
        if (_buttons.Count < 8) return;
        var snapshot = _session.Snapshot; var busy = _loading || _session.IsBusy;
        _refreshing = true;
        try
        {
            _status.Text = L(snapshot.Phase.ToString()) + (snapshot.ConnectionFailure is { } failure ? " " + SecureConnectionStrings.Get(failure.ToString()) : ((_localIssue ?? snapshot.Issue) is { } issue ? " " + L(issue) : ""));
            var identity = snapshot.Identity;
            var resume = snapshot.Resume;
            _identity.Text = identity is null ? "" : (resume is null ? "" : L("RecordedIdentity") + " ") + $"{identity.Address} · MV-{identity.MaskVersion:X4} · {identity.Manufacturer} · {identity.Serial} · {identity.Application}";
            if (snapshot.PhysicalIdentity is { } physical) _identity.Text += Environment.NewLine + L("PhysicalIdentity") + $" {physical.Address} · MV-{physical.MaskVersion:X4} · {physical.Manufacturer} · {physical.Serial}";
            _recovery.Text = snapshot.Result?.RecoveryPath ?? resume?.RecoveryPath ?? (_session.RecoveryRequired ? _journalPath ?? "" : "");
            _closeUnverified.IsVisible = _session.RecoveryRequired;
            _identityConfirmed.IsChecked = snapshot.IdentityConfirmed;
            _identityConfirmed.IsEnabled = !busy && _prepared && snapshot.Phase is M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady;
            _planConfirmed.IsEnabled = !busy && _prepared;
            _gateway.IsEnabled = _port.IsEnabled = _source.IsEnabled = !busy && !_session.RecoveryRequired;
            _source.IsReadOnly = _mode.SelectedIndex == 1;
            _mode.IsEnabled = !busy && !_session.RecoveryRequired; _secure.IsEnabled = !busy && !_session.RecoveryRequired && _mode.SelectedIndex == 1;
            _secureBinding.Text = _mode.SelectedIndex == 1 && _secureReference is { } reference ? $"Host {reference.HostAddress} · User {reference.UserId} · IA {reference.TunnelAddress}" : "";
            _target.IsEnabled = !busy;
            foreach (var key in new[] { "Baseline", "Journal", "Preview" }) _buttons[key].IsEnabled = !busy && !_session.RecoveryRequired && !_session.RecoveryOnly;
            _buttons["ReadIdentity"].IsEnabled = !busy && _prepared && snapshot.Phase is M112CommissioningPhase.Reviewed or M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReviewed or M112CommissioningPhase.ResumeReady;
            _buttons["Apply"].Content = L(resume is null ? "Apply" : "Restore");
            _buttons["Apply"].IsEnabled = !busy && _prepared && snapshot.Phase is (M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady)
                && snapshot.IdentityConfirmed && _planConfirmed.IsChecked == true && _target.Text == identity?.Address;
            _buttons["Resume"].IsEnabled = !busy;
            _buttons["Cancel"].IsEnabled = _session.IsBusy && snapshot.Phase != M112CommissioningPhase.Restoring;
            if (_prepared && _session.Review is { } review)
            {
                var text = new StringBuilder().AppendLine(review.Target).AppendLine(review.Fingerprint).AppendLine(L("Columns"));
                foreach (var range in review.Ranges.Take(128))
                    text.AppendLine($"0x{range.Address:X4} +{range.Length}: {ClipHex(range.Baseline)} → {ClipHex(range.Desired)} · {ClipHex(range.Validity)}{(range.RequiresDeviceRead ? " · " + L("DeviceRead") : "")}");
                if (review.Ranges.Count > 128 || review.Ranges.Any(range => range.Desired.Length > 128)) text.AppendLine(L("Truncated"));
                _review.Text = text.ToString();
            }
            else if (_prepared && resume is not null)
            {
                var text = new StringBuilder().AppendLine(resume.RecoveryPath).AppendLine(resume.FileSha256)
                    .AppendLine(resume.ImageFingerprint).AppendLine(resume.RecoveryScope);
                foreach (var segment in resume.Segments) text.AppendLine($"0x{segment.Address:X4} +{segment.Size} · SHA256 {segment.OriginalSha256}");
                _review.Text = text.ToString();
            }
        }
        finally { _refreshing = false; }
    }
    private static string ClipHex(string text) => text.Length <= 128 ? text : text[..128] + "…";
    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!_session.IsBusy && !MayClose) { args.Cancel = true; RefreshState(); return; }
        if (_allowClose || !_session.IsBusy && _operation.IsCompleted) return;
        args.Cancel = true; if (_closing) return;
        _closing = true; _secureDialog?.Close(); _session.Cancel(); var pending = _operation;
        _operation = CloseJoinedAsync(pending);
    }
    private async Task CloseJoinedAsync(Task pending)
    {
        await pending; await _session.Completion;
        if (!MayClose) { _closing = false; RefreshState(); return; }
        _allowClose = true; Close();
    }
}
