using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Updates;

namespace Knx.App;

public sealed class UpdateWindow : Window
{
    private readonly AppUpdateService _service;
    private readonly bool _ownsService;
    private readonly ComboBox _mode = new() { Name = "UpdatePolicyBox", Classes = { "wb-field" } };
    private readonly ComboBox _channel = new() { Name = "UpdateChannelBox", Classes = { "wb-field" } };
    private readonly TextBlock _status = new() { Name = "UpdateStatusText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { Name = "UpdateMessageText", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 4, IsIndeterminate = true };
    private readonly Button _check = new() { Name = "CheckUpdatesButton", Classes = { "wb-button" } };
    private readonly Button _retry = new() { Name = "RetryUpdateButton", Classes = { "wb-button" } };
    private readonly Button _save = new() { Name = "SaveUpdatePreferences", Classes = { "wb-button", "wb-primary" } };
    private readonly Button _close = new() { Name = "CloseUpdatesButton", Classes = { "wb-button", "wb-quiet" }, IsCancel = true };
    private readonly List<Action> _translations = [];
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _operation;
    private bool _busy, _closed;

    public UpdateWindow(AppUpdateService? service = null)
    {
        _service = service ?? AppUpdateService.Current ?? new();
        _ownsService = service is null && AppUpdateService.Current is null;
        Width = 650; Height = 540; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("wb-dialog");
        _mode.SelectedIndex = _service.Preferences.Policy switch { UpdatePolicy.Automatic => 0, UpdatePolicy.CheckOnly => 1, _ => 2 };
        _channel.SelectedIndex = _service.Preferences.Channel == "preview" ? 1 : 0;
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        body.Children.Add(Text("Intro"));
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        var versionText = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "wb-muted" } };
        _translations.Add(() => versionText.Text = U("Version", version)); body.Children.Add(versionText);
        body.Children.Add(Text("Mode", "wb-label")); body.Children.Add(_mode);
        body.Children.Add(Text("Channel", "wb-label")); body.Children.Add(_channel);
        body.Children.Add(Text(AppUpdateService.PackageManaged ? "ManagedHint" : "PolicyHint", "wb-muted"));
        body.Children.Add(Text("FeedHint", "wb-muted"));
        body.Children.Add(Text("State", "wb-label")); body.Children.Add(_status); body.Children.Add(_progress);
        body.Children.Add(_check);
        body.Children.Add(_retry);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _close.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(_close); actions.Children.Add(_save);
        var footer = new Border
        {
            Classes = { "wb-dialog-footer" },
            Child = new StackPanel { Spacing = 8, Children = { _message, actions } },
        };
        var root = new Grid { RowDefinitions = new("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _check.Click += (_, _) => _operation = RunAsync(() => _service.CheckNowAsync(_cancellation.Token));
        _retry.Click += (_, _) => _operation = RunAsync(() => _service.RetryAsync(_cancellation.Token));
        _save.Click += (_, _) => _operation = RunAsync(async () =>
        {
            var policy = _mode.SelectedIndex switch { 0 => UpdatePolicy.Automatic, 1 => UpdatePolicy.CheckOnly, _ => UpdatePolicy.Disabled };
            await _service.SaveAsync(new(policy, _channel.SelectedIndex == 1 ? "preview" : "stable"));
            if (!_closed) _message.Text = U("Saved");
        });
        _close.Click += (_, _) => Close();
        _service.Changed += OnStatus;
        UpdateUiStrings.Instance.PropertyChanged += OnLanguage;
        Opened += (_, _) => { FitWorkingArea(); _mode.Focus(); };
        Closed += (_, _) =>
        {
            _closed = true; _cancellation.Cancel();
            _service.Changed -= OnStatus;
            UpdateUiStrings.Instance.PropertyChanged -= OnLanguage;
            if (_ownsService) _ = DisposeWhenIdleAsync();
            else _cancellation.Dispose();
        };
        RefreshLanguage();
        WorkbenchDialogChrome.Apply(this);
    }

    private static string U(string key, params object[] values) => UpdateUiStrings.Get(key, values);
    private TextBlock Text(string key, string? style = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (style is not null) text.Classes.Add(style);
        _translations.Add(() => text.Text = U(key)); return text;
    }
    private void OnLanguage(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == "Item[]") Dispatcher.UIThread.Post(() => { if (!_closed) RefreshLanguage(); }); }
    private void OnStatus(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshState(); });
    private void RefreshLanguage()
    {
        Title = U("Title"); foreach (var setter in _translations) setter();
        var mode = _mode.SelectedIndex >= 0 ? _mode.SelectedIndex
            : _service.Preferences.Policy switch { UpdatePolicy.Automatic => 0, UpdatePolicy.CheckOnly => 1, _ => 2 };
        var channel = _channel.SelectedIndex >= 0 ? _channel.SelectedIndex : _service.Preferences.Channel == "preview" ? 1 : 0;
        _mode.ItemsSource = new[] { U("Automatic"), U("CheckOnly"), U("Disabled") }; _mode.SelectedIndex = mode;
        _channel.ItemsSource = new[] { U("Stable"), U("Preview") }; _channel.SelectedIndex = channel;
        AutomationProperties.SetName(_mode, U("Mode")); AutomationProperties.SetName(_channel, U("Channel"));
        AutomationProperties.SetName(_status, U("State")); AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _check.Content = U("CheckNow"); _retry.Content = U("Retry"); _close.Content = Strings.Get("BtnClose"); _save.Content = Strings.Get("BtnSaveSettings");
        RefreshState();
    }
    private void RefreshState()
    {
        _status.Text = _service.Status.Split(':')[0] switch
        {
            "idle" => U("Idle"), "checking" => U("Checking"), "downloading" => U("Downloading"),
            "ready" => U("Ready"), "up-to-date" => U("UpToDate"), "available" => U("Available"),
            "disabled" => U("DisabledState"), "unavailable" => U("Unavailable"), "cancelled" => U("Cancelled"),
            "preferences-invalid" => U("PreferencesInvalid"), "apply-failed" => U("ApplyFailed"),
            "retry-required" => U("ApplyFailed"),
            "deferred-user-data" => U("UserData"), "package-managed" => U("ManagedHint"), _ => U("Failed"),
        };
        _progress.IsVisible = _service.Status is "checking" or "downloading";
        _mode.IsEnabled = _channel.IsEnabled = _save.IsEnabled = !_busy && !AppUpdateService.PackageManaged;
        _check.IsEnabled = !_busy && _service.Supported && !_progress.IsVisible;
        _retry.IsVisible = _service.Status.Split(':')[0] is "apply-failed" or "retry-required" or "deferred-user-data";
        _retry.IsEnabled = !_busy && _service.Supported;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _message.Text = ""; RefreshState();
        try { await action(); }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) _message.Text = U("OperationFailed", error.Message); }
        finally { _busy = false; if (!_closed) RefreshState(); }
    }
    private async Task DisposeWhenIdleAsync()
    {
        try { if (_operation is { } task) await task.ConfigureAwait(false); }
        catch { }
        finally { _service.Dispose(); _cancellation.Dispose(); }
    }
    private void FitWorkingArea()
    {
        if (Screens?.ScreenFromWindow(this) is not { } screen || RenderScaling <= 0) return;
        var width = Math.Max(1, Math.Floor(screen.WorkingArea.Width / RenderScaling - 20));
        var height = Math.Max(1, Math.Floor(screen.WorkingArea.Height / RenderScaling - 32));
        MinWidth = Math.Min(MinWidth, width); MinHeight = Math.Min(MinHeight, height);
        MaxWidth = width; MaxHeight = height;
        Width = Math.Clamp(Width, MinWidth, width); Height = Math.Clamp(Height, MinHeight, height);
    }
}
