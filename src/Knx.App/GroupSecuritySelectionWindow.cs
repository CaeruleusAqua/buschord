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

/// <summary>One explicit bounded registry/profile choice; performs no network or password read.</summary>
public sealed class GroupSecuritySelectionWindow : Window
{
    private readonly GroupSecuritySelectionSession _session;
    private readonly TextBox _path = new() { Name = "GroupRegistryPath", IsReadOnly = true, Classes = { "wb-field" } };
    private readonly TextBox _pin = new() { Name = "GroupRegistryFingerprint", IsReadOnly = true, Classes = { "wb-field" } };
    private readonly ComboBox _profiles = new() { Name = "GroupProfileChoice", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _details = new() { Name = "GroupProfileDetails", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "GroupProfileStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _confirmationText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _confirmation = new() { Name = "GroupCurrentStateConfirmed" };
    private readonly Button _browse, _read, _choose, _close;
    private readonly List<Action> _translations = [];
    private bool _refreshing, _closed, _closing, _allowClose, _browsing;
    private Task _browseOperation = Task.CompletedTask;
    private Task? _closeOperation;
    public WorkbenchGroupSelection? Selection { get; private set; }
    public Task OperationCompletion => _closeOperation ?? Task.WhenAll(_browseOperation, _session.OperationCompletion);
    public GroupSecuritySelectionWindow(WorkbenchGroupSecurityOwner owner, object projectLifetime, KnxConnectionProfile explicitConnection)
    {
        _session = new(owner, projectLifetime, explicitConnection);
        Width = 780; Height = 720; MinWidth = 420; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap }; body.Children.Add(notice); _translations.Add(() => notice.Text = G("Notice"));
        void Field(string key, Control control)
        { var label = new TextBlock { Classes = { "wb-label" } }; body.Children.Add(label); body.Children.Add(control); _translations.Add(() => { label.Text = G(key); AutomationProperties.SetName(control, G(key)); }); }
        Field("File", _path); Field("Fingerprint", _pin);
        var files = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        _browse = Button("GroupRegistryBrowse", "Browse"); _read = Button("GroupRegistryRead", "Read"); files.Children.Add(_browse); files.Children.Add(_read); body.Children.Add(files);
        Field("Profile", _profiles); _profiles.ItemTemplate = new FuncDataTemplate<KnxGroupHostCandidate>((item, _) => new TextBlock { Text = item?.Id ?? "" });
        body.Children.Add(_details); _confirmation.Content = _confirmationText; body.Children.Add(_confirmation);
        var groupOnly = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "wb-subtle" } }; body.Children.Add(groupOnly); _translations.Add(() => groupOnly.Text = G("GroupOnly")); body.Children.Add(_status);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        _close = new() { Name = "GroupProfileClose", Classes = { "wb-button", "wb-quiet" }, IsCancel = true };
        _choose = Button("GroupProfileChoose", "Choose"); _choose.IsDefault = true; _choose.Classes.Remove("wb-quiet"); _choose.Classes.Add("wb-primary"); footer.Children.Add(_close); footer.Children.Add(_choose);
        var grid = new Grid { RowDefinitions = new("*,Auto") }; grid.Children.Add(new ScrollViewer { Name = "GroupSelectionScroll", Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var border = new Border { Child = footer, Classes = { "wb-dialog-footer" } }; Grid.SetRow(border, 1); grid.Children.Add(border); Content = grid;
        _path.TextChanged += (_, _) => { _session.SetSource(_path.Text ?? ""); Refresh(); };
        _profiles.SelectionChanged += (_, _) => { if (!_refreshing) _session.Select(_profiles.SelectedItem as KnxGroupHostCandidate); };
        _confirmation.IsCheckedChanged += (_, _) => { if (!_refreshing) _session.ConfirmCurrentState(_confirmation.IsChecked == true); };
        _browse.Click += (_, _) => StartBrowse(); _read.Click += (_, _) => Start(false); _close.Click += (_, _) => Close();
        _choose.Click += (_, _) => Start(true);
        _session.Changed += OnChanged; Strings.Instance.PropertyChanged += OnLanguage;
        Closing += OnClosing; Closed += (_, _) => { _closed = true; _session.Changed -= OnChanged; Strings.Instance.PropertyChanged -= OnLanguage; };
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); } };
        WorkbenchDialogChrome.Apply(this); Refresh();
    }
    private Button Button(string name, string key)
    { var button = new Button { Name = name, Classes = { "wb-button", "wb-quiet" } }; _translations.Add(() => { button.Content = G(key); AutomationProperties.SetName(button, G(key)); }); return button; }
    private static string G(string key) => GroupSecurityStrings.Get(key);
    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == "Item[]") Dispatcher.UIThread.Post(() => { if (!_closed) Refresh(); }); }
    private void OnChanged() { if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(() => { if (!_closed) Refresh(); }); }
    private void Refresh()
    {
        if (_closed) return; Title = G("Title"); foreach (var action in _translations) action(); _close.Content = LiveConnectionStrings.Get("Close");
        var state = _session.Snapshot; var busy = state.Busy || _browsing || _closing;
        _refreshing = true;
        try
        {
            _profiles.ItemsSource = state.Candidates?.Candidates; _profiles.SelectedItem = state.Candidate; _pin.Text = state.Candidates?.SourceFingerprint ?? "";
            _confirmation.IsChecked = state.ConfirmedCurrentState; _confirmation.IsVisible = state.Candidate?.RequiresCurrentUnanchoredConfirmation == true;
        }
        finally { _refreshing = false; }
        _confirmationText.Text = G("ConfirmCurrent"); AutomationProperties.SetName(_confirmation, _confirmationText.Text);
        _browse.IsEnabled = _read.IsEnabled = _profiles.IsEnabled = _confirmation.IsEnabled = !busy;
        _choose.IsEnabled = !busy && state.Candidate is { } candidate && (!candidate.RequiresCurrentUnanchoredConfirmation || state.ConfirmedCurrentState);
        _details.Text = state.Candidate is { } selected ? GroupSecurityDialogText.Summary(selected) : G("Unverified"); _status.Text = G(state.Status);
    }
    private void StartBrowse()
    {
        if (_browsing || _session.Snapshot.Busy || _closing) return;
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _browseOperation = owner.Task; _browsing = true; Refresh(); _ = BrowseAsync(owner);
    }
    private async void Start(bool bind)
    {
        if (_session.Snapshot.Busy || _browsing || _closing) return;
        try
        {
            _session.SetSource(_path.Text ?? "");
            await (bind ? _session.BindAsync() : _session.ReadCandidatesAsync());
            if (!_closing && bind && _session.Snapshot.Selection is { } selection) { Selection = selection; Close(); }
        }
        catch { if (!_closing) _status.Text = G("InvalidProfile"); }
    }
    private async Task BrowseAsync(TaskCompletionSource owner)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = G("File"), AllowMultiple = false, FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
            if (!_closing && files.FirstOrDefault()?.TryGetLocalPath() is { } path) _path.Text = path;
        }
        catch { if (!_closing) _status.Text = G("InvalidProfile"); }
        finally { _browsing = false; if (!_closed) Refresh(); owner.TrySetResult(); }
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return; e.Cancel = true; if (_closing) return; _closing = true;
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _closeOperation = owner.Task;
        var closing = _session.DisposeAsync().AsTask(); Refresh(); _ = FinishCloseAsync(closing, owner);
    }
    private async Task FinishCloseAsync(Task closing, TaskCompletionSource owner)
    {
        try { await Task.WhenAll(closing, _browseOperation); _allowClose = true; Close(); owner.TrySetResult(); }
        catch { Selection = null; _allowClose = true; Close(); owner.TrySetException(new IOException("Group selection cleanup failed.")); }
    }
}

internal static class GroupSecurityDialogText
{
    internal static string Summary(KnxGroupHostCandidate candidate)
    {
        string G(string key) => GroupSecurityStrings.Get(key);
        var connection = candidate.ExpectedConnection; var reference = connection.Keyring!;
        return candidate.Id + " · " + connection.Gateway + "\nHost " + reference.HostAddress + " · User " + reference.UserId + " · IA " + reference.TunnelAddress
            + "\n" + G("Scope") + ": " + candidate.InstallationScope + "\n" + G("Epoch") + ": " + candidate.OrdinaryKeyEpoch
            + "\n" + G("State") + ": " + candidate.StatePath + "\n" + G(candidate.RequiresCurrentUnanchoredConfirmation ? "Unanchored" : "Anchor")
            + "\n\n" + G("Groups") + ":\n" + string.Join("\n", candidate.Groups.Select(group => group.Group + " · " + G(group.Protection == KnxDataProtection.Authentication ? "Authenticated" : "Confidential") + " · " + string.Join(", ", group.PermittedSenders)))
            + "\n" + G("PlainGroups") + ": " + string.Join(", ", candidate.PlainGroups) + "\n" + G("PlainPeers") + ": " + string.Join(", ", candidate.PlainIndividualPeers);
    }
}
