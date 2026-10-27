using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;

namespace Knx.App.Views;

/// <summary>
/// Device parameters as an embeddable pane: static-editor notice, unsupported-dynamic warning,
/// parameter rows and the Apply row pinned to the pane's bottom edge. The pane owns loading,
/// validation and inline feedback so a hosting window only has to place it.
/// </summary>
public sealed partial class ParameterPane : UserControl
{
    private MainWindowViewModel? _vm;
    private DeviceRow? _row;
    private Func<bool>? _apply;
    private Dictionary<string, string?> _baseline = [];
    private bool _dirty;

    public ParameterPane() => InitializeComponent();

    public ParameterPane(MainWindowViewModel vm) : this() => _vm = vm;

    /// <summary>Device currently edited; null while the pane shows an empty state.</summary>
    public DeviceInstance? Device => _row?.Device;

    /// <summary>Raised by the pane's Close button, which only the dialog wrapper shows.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Shows the device's parameters; null shows the empty state without touching the project.</summary>
    public void Load(DeviceRow? device)
    {
        _row = device;
        _apply = null;
        if (_vm is not { Project: { } project })
        {
            ShowState(ParameterEmptyState.Text(null, device?.Device, 0));
            return;
        }
        if (device is null)
        {
            ShowState(ParameterEmptyState.Text(project, null, 0));
            return;
        }

        var (instances, types) = _vm.LoadParametersFor(device);
        _apply = () => _vm.ApplyParameterValues(device.Device, instances, types, _baseline);
        ApplyButton.IsVisible = true;
        Show($"Parameter – {device.Name}", instances, types, _vm.ParameterWarning,
            ParameterEmptyState.Text(project, device.Device, instances.Count));
    }

    /// <summary>
    /// Re-reads the model after undo/redo or a project change. Unsaved drafts are dropped, so the
    /// pane says so instead of silently replacing what the user just typed.
    /// </summary>
    public void Refresh()
    {
        if (_vm is null) return;
        var drafts = _dirty;
        Load(_row);
        if (drafts && ParameterList.IsVisible) MessageText.Text = ParameterEmptyState.DiscardedText;
    }

    /// <summary>Applies the drafts atomically; failures stay inline instead of throwing at the caller.</summary>
    public bool Apply()
    {
        if (_apply is null) return false;
        try
        {
            _apply();
            _dirty = false;
            MessageText.Text = Strings.Get("ParameterSaved");
            return true;
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
            return false;
        }
    }

    /// <summary>Fills the pane from already resolved parameters (dialog wrapper, demo screens).</summary>
    internal void LoadResolved(string? title, IEnumerable<ParameterInstance> parameters,
        IEnumerable<ParameterType> types, Func<bool>? apply, string? warning = null)
    {
        _row = null;
        _apply = apply;
        ApplyButton.IsVisible = apply is not null;
        var instances = parameters.ToList();
        Show(title, instances, types.ToList(), warning,
            instances.Count == 0 ? ParameterEmptyState.NoParametersText : null);
    }

    /// <summary>Shows the Close button; the dialog wrapper uses it, embedded panes do not.</summary>
    internal void ShowClose() => CloseButton.IsVisible = true;

    private void Show(string? title, List<ParameterInstance> instances, List<ParameterType> types,
        string? warning, string? empty)
    {
        _baseline = instances.ToDictionary(i => i.Id, i => i.Value);
        HeaderText.Text = string.IsNullOrWhiteSpace(title) ? Strings.Get("ParameterTitle") : title;

        var hasRows = instances.Count > 0;
        ParameterList.ItemsSource = hasRows ? ParameterItemViewModel.Build(instances, types, () => _dirty = true) : null;
        ParameterList.IsVisible = hasRows;
        NoticeExpander.IsVisible = hasRows;
        EmptyText.Text = empty ?? ParameterEmptyState.NoParametersText;
        EmptyText.IsVisible = empty is not null;

        var hasWarning = hasRows && !string.IsNullOrWhiteSpace(warning);
        WarningText.IsVisible = hasWarning;
        WarningDetail.IsVisible = hasWarning;
        WarningDetail.Text = warning ?? "";
        MessageText.Text = "";
        _dirty = false;
    }

    private void ShowState(string? empty)
    {
        _baseline = [];
        _apply = null;
        ApplyButton.IsVisible = false;
        ParameterList.ItemsSource = null;
        ParameterList.IsVisible = false;
        NoticeExpander.IsVisible = false;
        WarningText.IsVisible = false;
        WarningDetail.IsVisible = false;
        HeaderText.Text = Strings.Get("ParameterTitle");
        EmptyText.Text = empty ?? ParameterEmptyState.NoParametersText;
        EmptyText.IsVisible = true;
        MessageText.Text = "";
        _dirty = false;
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Apply();

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
