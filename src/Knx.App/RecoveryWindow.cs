using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Recovery;

namespace Knx.App;

/// <summary>Local explicit recovery; list metadata never opens its stored source path.</summary>
public sealed class RecoveryWindow : Window
{
    private readonly ProjectRecoveryController _controller;
    private readonly Func<Task<bool>> _discardGate;
    private readonly Func<Task> _adopted;
    private readonly AppProjectRecoveryService? _service;
    private readonly CheckBox _enabled = new() { Name = "RecoveryCaptureEnabled" };
    private readonly TextBlock _boundary = new() { Name = "RecoveryBoundary", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _privacy = new() { Name = "RecoveryPrivacy", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool _relabeling;
    private readonly ListBox _list = new() { Name = "RecoveryEntries", MinHeight = 100 };
    private readonly TextBlock _status = new() { Name = "RecoveryStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _source = new() { Name = "RecoverySource", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _refresh = new() { Name = "RecoveryRefresh" }, _more = new() { Name = "RecoveryMore" },
        _choose = new() { Name = "RecoveryChooseSource" }, _unsaved = new() { Name = "RecoveryChooseUnsaved" },
        _restore = new() { Name = "RecoveryRestore", IsDefault = true }, _close = new() { Name = "RecoveryClose", IsCancel = true };
    private readonly CancellationTokenSource _cancel = new();
    private RecoverySource? _selectedSource;
    private string? _sourceCheckpoint, _continuation;
    private IReadOnlyList<string>? _validatedNotices;
    private readonly List<RecoveryListItem> _items = [];
    private bool _closed, _busy;
    private Task _operation = Task.CompletedTask;
    public Task OperationCompletion => _operation;
    public RecoveryWindow(ProjectRecoveryController controller, Func<Task<bool>> discardGate, Func<Task> adopted, AppProjectRecoveryService? service = null)
    {
        _controller = controller; _discardGate = discardGate; _adopted = adopted;
        _service = service;
        Width = 800; Height = 650; MinWidth = 440; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("wb-dialog");
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        body.Children.Add(_boundary); body.Children.Add(_privacy);
        body.Children.Add(_enabled);
        body.Children.Add(_status); body.Children.Add(_list);
        var discovery = new WrapPanel { Children = { _refresh, _more } }; body.Children.Add(discovery);
        body.Children.Add(_source);
        body.Children.Add(new WrapPanel { Children = { _choose, _unsaved } });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { _close, _restore } };
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body }); Grid.SetRow(actions, 1); root.Children.Add(actions); Content = root;
        foreach (var button in new[] { _refresh, _more, _choose, _unsaved, _restore, _close }) { button.Classes.Add("wb-button"); button.Margin = new Thickness(0, 4, 8, 4); }
        _list.ItemTemplate = new FuncDataTemplate<RecoveryListItem>((item, _) => new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = item is null ? "" : $"{item.ProjectName ?? R("Protected")} · {item.CreatedUtc?.ToString("u") ?? R("UnknownTime")}\n{R(item.Status.ToString())} · {R("Steps", item.UndoCount?.ToString() ?? "?", item.RedoCount?.ToString() ?? "?")}"
        });
        _list.SelectionChanged += (_, _) => { _selectedSource = null; _sourceCheckpoint = null; _validatedNotices = null; RefreshControls(); };
        _refresh.Click += (_, _) => Start(() => LoadAsync(false)); _more.Click += (_, _) => Start(() => LoadAsync(true));
        _choose.Click += (_, _) => Start(ChooseSourceAsync);
        _unsaved.Click += (_, _) =>
        {
            if (_list.SelectedItem is not RecoveryListItem { BindingKind: RecoveryBindingKind.UnsavedProject, ProjectId: not null, ProjectGuid: not null } item) return;
            _selectedSource = new(RecoveryBindingKind.UnsavedProject, item.ProjectId, item.ProjectGuid, null, null, null, item.SessionId);
            _sourceCheckpoint = item.Id; _validatedNotices = null; RefreshControls();
        };
        _restore.Click += (_, _) => Start(RestoreAsync); _close.Click += (_, _) => Close();
        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (_relabeling || _service is null) return;
            var enabled = _enabled.IsChecked == true;
            Start(() => _service.SetEnabledAsync(enabled));
        };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _controller.Changed += OnControllerChanged; Strings.Instance.PropertyChanged += OnLanguageChanged;
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / RenderScaling - 40));
                Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / RenderScaling - 40));
            }
            _list.Focus(); Start(() => LoadAsync(false));
        };
        Closed += (_, _) => { _closed = true; _cancel.Cancel(); _controller.Changed -= OnControllerChanged; Strings.Instance.PropertyChanged -= OnLanguageChanged; _ = DisposeCancellationWhenIdle(); };
        Relabel(); WorkbenchDialogChrome.Apply(this);
    }
    private static string R(string key, params object[] args) => RecoveryUiStrings.Get(key, args);
    private void Relabel()
    {
        Title = R("Title"); _refresh.Content = R("Refresh"); _more.Content = R("More"); _choose.Content = R("ChooseSource");
        _relabeling = true; _enabled.Content = R("Enable"); _enabled.IsChecked = _service?.Enabled ?? _controller.RecordingEnabled; _enabled.IsEnabled = _service is not null && !_busy; _relabeling = false;
        _unsaved.Content = R("ChooseUnsaved"); _restore.Content = R("Restore"); _close.Content = R("Close");
        AutomationProperties.SetName(_list, R("Title")); AutomationProperties.SetName(_source, R("Source"));
        _boundary.Text = R("Boundary"); _privacy.Text = R("Privacy");
        var selected = _list.SelectedItem; var source = _selectedSource; var checkpoint = _sourceCheckpoint; var notices = _validatedNotices;
        _list.ItemsSource = _items.ToArray(); _list.SelectedItem = selected;
        _selectedSource = source; _sourceCheckpoint = checkpoint; _validatedNotices = notices;
        RefreshControls();
    }
    private void RefreshControls()
    {
        _relabeling = true;
        _enabled.IsEnabled = _service is not null && !_busy;
        if (!_busy) _enabled.IsChecked = _service?.Enabled ?? _controller.RecordingEnabled;
        _relabeling = false;
        var item = _list.SelectedItem as RecoveryListItem;
        _refresh.IsEnabled = !_busy; _more.IsEnabled = !_busy && _continuation is not null;
        _choose.IsEnabled = !_busy && item?.BindingKind == RecoveryBindingKind.SavedArchive;
        _unsaved.IsEnabled = !_busy && item?.BindingKind == RecoveryBindingKind.UnsavedProject;
        _restore.IsEnabled = !_busy && item?.Id == _sourceCheckpoint && _selectedSource is not null;
        _restore.Content = R(_validatedNotices is null ? "Validate" : "Restore");
        _source.Text = _selectedSource is null ? R("SourceRequired") : _selectedSource.Kind == RecoveryBindingKind.UnsavedProject
            ? R("UnsavedSaveAs") : R("ChosenSource", Path.GetFileName(_selectedSource.FullPath) ?? "");
        _status.Text = R(_controller.StatusCode);
    }
    private void Start(Func<Task> action)
    {
        if (_busy || _closed) return;
        _operation = RunAsync();
        async Task RunAsync()
        {
            _busy = true; RefreshControls();
            try { await action(); }
            catch (OperationCanceledException) { if (!_closed) _status.Text = R("Cancelled"); }
            catch (Exception ex) { if (!_closed) _status.Text = R(ex is RecoveryRejectedException rejected ? rejected.Failure.ToString() : "Failed"); }
            finally { _busy = false; if (!_closed) { var message = _status.Text; RefreshControls(); _status.Text = message; } }
        }
    }
    private async Task LoadAsync(bool next)
    {
        var page = await _controller.ListAsync(next ? _continuation : null, _cancel.Token);
        if (_closed) return;
        if (!next) _items.Clear(); _items.AddRange(page.Items); _continuation = page.Continuation;
        _list.ItemsSource = _items.ToArray(); _status.Text = page.ScanLimitReached ? R("LimitExceeded") : R("MetadataOnly");
    }
    private async Task ChooseSourceAsync()
    {
        if (_list.SelectedItem is not RecoveryListItem item) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = R("ChooseSource"), AllowMultiple = false,
            FileTypeFilter = [new("KNX") { Patterns = ["*.knxproj"] }] });
        if (_closed || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var source = await ProjectRecoveryController.ReadSelectedSourceAsync(path, _cancel.Token);
        if (_closed) return;
        if (source.ProjectId != item.ProjectId || source.ProjectGuid != item.ProjectGuid) throw new RecoveryRejectedException(RecoveryFailure.ProjectIdentityMismatch, "Recovery.ProjectIdentityMismatch");
        _selectedSource = source; _sourceCheckpoint = item.Id; _validatedNotices = null;
    }
    private async Task RestoreAsync()
    {
        if (_selectedSource is null || _list.SelectedItem is not RecoveryListItem item || item.Id != _sourceCheckpoint) return;
        if (_validatedNotices is null)
        {
            var candidate = await _controller.ValidateAsync(item.Id, _selectedSource, _cancel.Token);
            if (_closed) return;
            _validatedNotices = candidate.Notices.ToArray();
            _status.Text = R("Validated") + "\n" + string.Join("\n", candidate.Notices.Select(notice => R(notice)));
            return;
        }
        var ticket = await _controller.PrepareAdoptionAsync(item.Id, _selectedSource, _discardGate, _validatedNotices, _cancel.Token);
        await _controller.AdoptAsync(ticket, _cancel.Token);
        await _adopted(); if (!_closed) Close();
    }
    private void OnControllerChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_closed && !_busy) RefreshControls(); });
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() => { if (!_closed) Relabel(); });
    private async Task DisposeCancellationWhenIdle() { try { await _operation; } catch { } finally { _cancel.Dispose(); } }
}
