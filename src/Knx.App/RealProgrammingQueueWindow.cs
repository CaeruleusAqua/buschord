using System.Net;
using System.Text;
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
using Knx.App.Workbench;

namespace Knx.App;

/// <summary>Explicit real queue review. Views borrow the queue; the Workbench owns and drains it.</summary>
public sealed class RealProgrammingQueueWindow : Window
{
    private readonly RealM112ProgrammingQueue _queue;
    private readonly Func<Window, Task<M112BaselineSelection?>> _loadBaseline;
    private readonly IReadOnlyList<DeviceInstance> _devices;
    private readonly string _targetSource;
    private readonly List<Action> _translations = [];
    private readonly Dictionary<string, Button> _buttons = [];
    private readonly TextBox _gateway = Field("RealQueueGateway"), _port = Field("RealQueuePort", "3671"), _own = Field("RealQueueOwn", "1.1.250");
    private readonly ComboBox _transport = new() { Name = "RealQueueTransport", SelectedIndex = -1 };
    private readonly ComboBox _device = new() { Name = "RealQueueDevice", MinWidth = 220 };
    private readonly ListBox _jobs = new() { Name = "RealQueueJobs", MinHeight = 90, MaxHeight = 180 };
    private readonly Expander _settings = new() { Name = "RealQueueSettings", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _sources = new() { Name = "RealQueueSources", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _review = new() { Name = "RealQueueReview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 240, Classes = { "wb-field" } };
    private readonly TextBox _target = Field("RealQueueConfirmTarget");
    private readonly CheckBox _plan = new() { Name = "RealQueueConfirmPlan" };
    private readonly CheckBox _identityConfirmed = new() { Name = "RealQueueConfirmIdentity" };
    private readonly CheckBox _batch = new() { Name = "RealQueueConfirmBatch" };
    private readonly TextBlock _batchHash = new() { Name = "RealQueueBatchFingerprint", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "RealQueueStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _exitEvidence = new() { Name = "RealQueueExitEvidence", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _exit = new() { Name = "RealQueueExitConfirmed" };
    private readonly StackPanel _exitPanel = new() { Name = "RealQueueUnresolved", Spacing = 8, IsVisible = false };
    private M112BaselineSelection? _baseline;
    private string? _journal, _batchReview;
    private Guid? _selection;
    private (Guid Id, RealM112JobState State)? _shownActive;
    private Task _operation = Task.CompletedTask;
    private readonly TaskCompletionSource _closedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _loading, _closed, _closing, _allowClose, _refreshing;
    public Task OperationCompletion => Task.WhenAll(_operation, _queue.Completion);
    public Task ClosedCompletion => _closedCompletion.Task;
    public bool MayClose => !_queue.IsBusy && (!_queue.RecoveryRequired || _queue.MayLeave && _exit.IsChecked == true);
    public RealProgrammingQueueWindow(RealM112ProgrammingQueue queue, IReadOnlyList<DeviceInstance> devices,
        Func<Window, Task<M112BaselineSelection?>> loadBaseline, ReadOnlyConnectionOptions? initial = null, string targetSource = "")
    {
        _queue = queue; _devices = devices; _loadBaseline = loadBaseline; _targetSource = targetSource;
        Width = 1000; Height = 850; MinWidth = 500; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner; Classes.Add("wb-dialog");
        if (initial is not null) { _gateway.Text = initial.Gateway.Address.ToString(); _port.Text = initial.Port.ToString(); _own.Text = initial.RequestedAddress.ToString(); }
        // Opening never selects a connection mode, imports credentials or changes an existing bus owner.
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 9 };
        Text(body, () => Q("Scope"));
        Text(body, () => Q("HistoryNotice"));
        Text(body, () => Q("TargetSource") + ": " + _targetSource);
        var configuration = new StackPanel { Spacing = 8 };
        void Input(Panel panel, string key, TextBox field, bool live = false)
        { Text(panel, () => live ? LiveConnectionStrings.Get(key) : M(key)); panel.Children.Add(field); _translations.Add(() => AutomationProperties.SetName(field, live ? LiveConnectionStrings.Get(key) : M(key))); }
        Input(configuration, "Gateway", _gateway, true); Input(configuration, "Port", _port, true); Input(configuration, "TunnelAddress", _own, true);
        configuration.Children.Add(_transport); configuration.Children.Add(_device);
        _device.ItemsSource = devices; _device.SelectedIndex = devices.Count > 0 ? 0 : -1;
        _device.ItemTemplate = new FuncDataTemplate<DeviceInstance>((d, _) => new TextBlock { Text = d is null ? "" : d.Address + " · " + d.Name });
        var sources = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        sources.Children.Add(Button("Baseline", () => M("Baseline"), async () => { _baseline = await _loadBaseline(this); }));
        sources.Children.Add(Button("Journal", () => M("Journal"), async () =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = M("Journal"), AllowMultiple = false });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) _journal = M112PrivatePaths.ExistingDirectory(path);
        }));
        sources.Children.Add(Button("Add", () => Q("Add"), async () =>
        {
            if (_baseline is null || _journal is null || _device.SelectedItem is not DeviceInstance device) throw new InvalidOperationException("PrivatePath");
            var options = Options(); await _queue.EnqueueAsync(device.Id, _baseline, options, _journal);
            _selection = _queue.Snapshot.Jobs.LastOrDefault()?.Id;
            if (_queue.Snapshot.Jobs.LastOrDefault()?.State == RealM112JobState.Reviewed) _settings.IsExpanded = false;
        }));
        configuration.Children.Add(sources); configuration.Children.Add(_sources); _settings.Content = configuration; body.Children.Add(_settings); body.Children.Add(_jobs);
        _translations.Add(() => _settings.Header = Q("NewJob"));
        _jobs.ItemTemplate = new FuncDataTemplate<RealM112JobView>((j, _) => new TextBlock { Text = j is null ? "" : j.Address + " · " + j.Name + " · " + JobState(j), TextWrapping = TextWrapping.Wrap });
        _jobs.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selection = (_jobs.SelectedItem as RealM112JobView)?.Id; ResetConfirmation(); RefreshState();
        };
        var jobActions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        jobActions.Children.Add(Button("ReadIdentity", () => M("ReadIdentity"), async () => { ResetConfirmation(); await _queue.ReadIdentityAsync(Selected().Id); }));
        jobActions.Children.Add(Button("Remove", () => Q("Remove"), () => { _queue.Remove(Selected().Id); _selection = null; return Task.CompletedTask; }));
        jobActions.Children.Add(Button("PrepareRecovery", () => Q("PrepareRecovery"), async () => { ResetConfirmation(); await _queue.PrepareRecoveryAsync(Selected().Id); }));
        body.Children.Add(jobActions); body.Children.Add(_review); body.Children.Add(_identityConfirmed); body.Children.Add(_plan); Input(body, "TargetPrompt", _target);
        body.Children.Add(Button("Confirm", () => Q("ConfirmJob"), () =>
        {
            var job = Selected(); _queue.Confirm(job.Id, _target.Text ?? "", job.Fingerprint ?? "", _plan.IsChecked == true && _identityConfirmed.IsChecked == true); return Task.CompletedTask;
        }));
        body.Children.Add(_batchHash); body.Children.Add(_batch);
        var exitWarning = Text(_exitPanel, () => Q("UnknownHardware")); exitWarning.Classes.Add("wb-error");
        _exitPanel.Children.Add(Button("ReviewExit", () => Q("ReviewExit"), async () => await _queue.ReviewUnresolvedExitAsync()));
        _exitPanel.Children.Add(_exitEvidence); _exitPanel.Children.Add(_exit); body.Children.Add(_exitPanel); body.Children.Add(_status);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Button("Close", () => M("Close"), () => { Close(); return Task.CompletedTask; }));
        actions.Children.Add(Button("Cancel", () => M("Cancel"), () => { _queue.Cancel(); return Task.CompletedTask; }));
        actions.Children.Add(Button("Restore", () => M("Restore"), async () => { var job = Selected(); await _queue.RecoverAsync(job.Id, _target.Text ?? "", job.Fingerprint ?? ""); }));
        actions.Children.Add(Button("Start", () => Q("Start"), async () => await _queue.RunAsync(_batchReview ?? ""), danger: true));
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions }; Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _batch.IsCheckedChanged += (_, _) => { if (!_refreshing) _batchReview = _batch.IsChecked == true ? _queue.Snapshot.BatchFingerprint : null; RefreshState(); };
        _exit.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (_exit.IsChecked == true && _queue.ExitReview is { } report) Begin(async () => await _queue.ConfirmUnresolvedExitAsync(report.Fingerprint));
            else _queue.RevokeUnresolvedExit();
        };
        _target.TextChanged += (_, _) => { if (!_refreshing) RefreshState(); };
        _plan.IsCheckedChanged += (_, _) => { if (!_refreshing) RefreshState(); };
        _identityConfirmed.IsCheckedChanged += (_, _) => { if (!_refreshing) RefreshState(); };
        _queue.Changed += OnChanged; Strings.Instance.PropertyChanged += OnLanguage;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _queue.Changed -= OnChanged; Strings.Instance.PropertyChanged -= OnLanguage; _closedCompletion.TrySetResult(); };
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); } };
        RefreshLanguage(); WorkbenchDialogChrome.Apply(this);
    }
    private static string Q(string key) => RealProgrammingQueueStrings.Get(key);
    private static string M(string key) => M112CommissioningStrings.Get(key);
    private static TextBox Field(string name, string text = "") => new() { Name = name, Text = text, Classes = { "wb-field" } };
    private TextBlock Text(Panel panel, Func<string> text)
    { var label = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(label); _translations.Add(() => label.Text = text()); return label; }
    private Button Button(string id, Func<string> text, Func<Task> action, bool danger = false)
    {
        var button = new Button { Name = "RealQueue" + id, Classes = { "wb-button", danger ? "wb-danger" : "wb-quiet" } }; _buttons[id] = button;
        _translations.Add(() => { button.Content = text(); AutomationProperties.SetName(button, text()); });
        button.Click += (_, _) =>
        {
            if (id == "Close") { Close(); return; }
            if (id == "Cancel") { _queue.Cancel(); return; }
            Begin(action);
        }; return button;
    }
    private void Begin(Func<Task> action)
    {
        if (!_operation.IsCompleted || _closed) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task;
        _loading = true; _localIssue = null; RefreshState(); _ = RunActionAsync(action, completion);
    }
    private string? _localIssue;
    private void ResetConfirmation() { _target.Text = ""; _plan.IsChecked = _identityConfirmed.IsChecked = false; }
    private async Task RunActionAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action(); }
        catch (Exception error) { _localIssue = error.Message is "PrivatePath" or "Confirmation" or "RecoveryRequired" ? error.Message : "OperationFailed"; }
        finally { _loading = false; completion.TrySetResult(); RefreshState(); }
    }
    private ReadOnlyConnectionOptions Options()
    {
        if (_transport.SelectedIndex != 0 || !IPAddress.TryParse(_gateway.Text, out var address) || !int.TryParse(_port.Text, out var port)
            || !IndividualAddress.TryParse(_own.Text ?? "", out var own)) throw new InvalidOperationException("Confirmation");
        return new(address, port, own);
    }
    private RealM112JobView Selected() => _queue.Snapshot.Jobs.Single(j => j.Id == _selection);
    private void OnChanged(RealM112QueueView snapshot)
    {
        void Refresh()
        {
            if (_closed) return;
            var active = snapshot.Jobs.FirstOrDefault(j => j.State is RealM112JobState.Running or RealM112JobState.Restoring or RealM112JobState.RecoveryRequired);
            if (active is not null && _shownActive != (active.Id, active.State)) _selection = active.Id;
            _shownActive = active is null ? null : (active.Id, active.State); RefreshState();
        }
        if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(Refresh);
    }
    private void OnLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "") RefreshLanguage(); }
    private void RefreshLanguage()
    {
        Title = Q("Title"); foreach (var update in _translations) update();
        var selected = _transport.SelectedIndex; _transport.ItemsSource = new[] { Q("Plain") }; _transport.SelectedIndex = selected;
        AutomationProperties.SetName(_transport, Q("ChooseConnection")); _transport.PlaceholderText = Q("ChooseConnection");
        AutomationProperties.SetName(_device, Q("Device")); AutomationProperties.SetName(_jobs, Q("Title"));
        AutomationProperties.SetName(_review, M("Columns")); AutomationProperties.SetName(_batchHash, Q("Batch"));
        _identityConfirmed.Content = M("ConfirmIdentity"); _plan.Content = M("ConfirmPlan"); _batch.Content = Q("ConfirmBatch"); _exit.Content = Q("ExitAck");
        AutomationProperties.SetName(_identityConfirmed, M("ConfirmIdentity")); RefreshState();
    }
    private void RefreshState()
    {
        if (_closed || _refreshing) return; _refreshing = true;
        try
        {
            var snapshot = _queue.Snapshot; var busy = _loading || snapshot.Busy;
            if (_batchReview != snapshot.BatchFingerprint) { _batchReview = null; _batch.IsChecked = false; }
            _jobs.ItemsSource = snapshot.Jobs; _jobs.SelectedItem = snapshot.Jobs.FirstOrDefault(j => j.Id == _selection);
            var job = snapshot.Jobs.FirstOrDefault(j => j.Id == _selection);
            foreach (var control in new Control[] { _gateway, _port, _own, _transport, _device }) control.IsEnabled = !busy && !snapshot.RecoveryRequired;
            foreach (var button in _buttons.Values) button.IsEnabled = !busy;
            _buttons["Cancel"].IsEnabled = snapshot.Busy;
            _buttons["Close"].IsEnabled = true;
            _buttons["Add"].IsEnabled = !busy && !snapshot.RecoveryRequired && _baseline is not null && _journal is not null;
            _buttons["ReadIdentity"].IsEnabled = !busy && job is not null && job.State is RealM112JobState.Reviewed or RealM112JobState.Ready or RealM112JobState.RecoveryRequired;
            _buttons["Remove"].IsEnabled = !busy && !snapshot.RecoveryRequired && job is not null;
            _buttons["PrepareRecovery"].IsEnabled = !busy && job?.Result?.RecoveryPath is { Length: > 0 };
            _buttons["Confirm"].IsEnabled = !busy && job?.Phase is M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady
                && _plan.IsChecked == true && _identityConfirmed.IsChecked == true && _target.Text == job.Address;
            _buttons["Restore"].IsEnabled = !busy && job is { Confirmed: true, Phase: M112CommissioningPhase.ResumeReady } && _target.Text == job.Address;
            _buttons["Start"].IsEnabled = !busy && !snapshot.RecoveryRequired && _batch.IsChecked == true && _batchReview == snapshot.BatchFingerprint
                && snapshot.Jobs.Any(j => j.State == RealM112JobState.Ready) && snapshot.Jobs.Where(j => j.State is RealM112JobState.Reviewed or RealM112JobState.Ready or RealM112JobState.Blocked).All(j => j.State == RealM112JobState.Ready && j.Confirmed);
            _batch.IsEnabled = !busy && !snapshot.RecoveryRequired; _plan.IsEnabled = !busy && job?.Phase is M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady;
            _identityConfirmed.IsEnabled = _target.IsEnabled = _plan.IsEnabled;
            _sources.Text = (_baseline?.Path ?? M("Baseline")) + Environment.NewLine + (_journal ?? M("Journal"));
            _batchHash.Text = Q("PendingJobs") + ": " + string.Join(", ", snapshot.Jobs.Where(j => j.State is RealM112JobState.Reviewed or RealM112JobState.Ready or RealM112JobState.Blocked).Select(j => j.Address))
                + Environment.NewLine + Q("Batch") + ": " + snapshot.BatchFingerprint;
            _review.Text = job is null ? Q("Empty") : Review(job);
            _status.Text = Message(_localIssue ?? snapshot.Issue ?? job?.Issue) ?? (job is null ? Q("Empty") : JobState(job));
            _exitPanel.IsVisible = snapshot.RecoveryRequired;
            _buttons["ReviewExit"].IsEnabled = !busy && snapshot.RecoveryRequired;
            _exit.IsEnabled = !busy && _queue.ExitReview is not null;
            _exitEvidence.Text = _queue.ExitReview is { } report ? string.Join(Environment.NewLine, report.Evidence.Select(e => e.Address + ": "
                + Q(e.Available ? "Available" : "RecoveryUnavailable") + Environment.NewLine + (e.Path ?? "—") + Environment.NewLine + e.FileFingerprint)) : Q("UnknownHardware");
        }
        finally { _refreshing = false; }
    }
    private static string Review(RealM112JobView job)
    {
        var text = new StringBuilder().AppendLine(job.Address + " · " + job.Name).AppendLine(job.EndpointId + " · " + job.TunnelAddress)
            .AppendLine(M("Baseline") + " " + job.BaselineFingerprint).AppendLine(job.JournalRoot).AppendLine(job.Fingerprint);
        if (job.Identity is { } identity) text.AppendLine(identity.Manufacturer + " · " + identity.Serial + " · " + identity.Application);
        text.AppendLine(M("Columns"));
        foreach (var range in job.Ranges) text.AppendLine($"{range.Address:X4} +{range.Length}: {range.Baseline} → {range.Desired} · {range.Validity}");
        if (job.Result is { } result) text.AppendLine(result.RecoveryPath);
        return text.ToString();
    }
    private static string? Message(string? key) => key is null ? null : RealProgrammingQueueStrings.Keys.Contains(key) ? Q(key)
        : M112CommissioningStrings.Keys.Contains(key) ? M(key) : M("OperationFailed");
    private static string JobState(RealM112JobView job) => job.State == RealM112JobState.Ready && job.Confirmed ? Q("Confirmed") : State(job.State);
    private static string State(RealM112JobState state) => state switch
    {
        RealM112JobState.Running => M("Applying"), RealM112JobState.Restored => M("FailedRestored"),
        RealM112JobState.Blocked => Q("Blocked"), RealM112JobState.VerifiedStale => Q("VerifiedStale"), RealM112JobState.VerifiedUnrecorded => Q("JournalUnavailable"), _ => M(state.ToString())
    };
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return; _closing = true; _queue.Cancel(); var pending = OperationCompletion;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await pending;
                if (!MayClose) { _localIssue = "UnknownHardware"; RefreshState(); return; }
                _allowClose = true; Close();
            }
            finally { _closing = false; }
        });
    }
}
