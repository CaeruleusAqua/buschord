using System.Text.Json;
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
    private bool IsDirty => _instances.Any(i => i.Value != _baseline.GetValueOrDefault(i.Id));
    private Project? _project;
    private string _signature = "";
    private List<ParameterInstance> _instances = [];
    private List<ParameterType> _types = [];
    private readonly Dictionary<DeviceInstance, Draft> _drafts = [];
    private sealed record Draft(List<ParameterInstance> Instances, List<ParameterType> Types,
        Dictionary<string, string?> Baseline, string Signature, string Message);

    public ParameterPane() => InitializeComponent();

    public ParameterPane(MainWindowViewModel vm) : this() => _vm = vm;

    /// <summary>Device currently edited; null while the pane shows an empty state.</summary>
    public DeviceInstance? Device => _row?.Device;

    /// <summary>Includes drafts for devices which are not currently selected; never implies project edits.</summary>
    public bool HasUnappliedChanges
    {
        get
        {
            if (_vm is not null && !ReferenceEquals(_project, _vm.Project)) return false;
            PruneDrafts();
            return _drafts.Count > 0 || (IsDirty && (_vm is null || _row is not null
                && _project is not null && ProjectQuery.DeviceList(_project).Any(d => ReferenceEquals(d, _row.Device))));
        }
    }

    private void PruneDrafts()
    {
        if (_vm is null) return;
        var devices = _vm.Project is { } project ? ProjectQuery.DeviceList(project).ToHashSet() : [];
        foreach (var device in _drafts.Keys.Where(d => !devices.Contains(d)).ToArray()) _drafts.Remove(device);
    }

    /// <summary>Explicitly abandons every draft and restores the current editor from the project.</summary>
    public void DiscardDrafts()
    {
        _drafts.Clear();
        foreach (var instance in _instances) instance.Value = _baseline.GetValueOrDefault(instance.Id);
        if (_vm is not null) Load(_row);
        else
        {
            Show(HeaderText.Text, _instances, _types, WarningDetail.Text, EmptyText.IsVisible ? EmptyText.Text : null);
        }
    }

    /// <summary>Raised by the pane's Close button, which only the dialog wrapper shows.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Shows the device's parameters; null shows the empty state without touching the project.</summary>
    public void Load(DeviceRow? device)
    {
        if (!ReferenceEquals(_project, _vm?.Project))
        {
            _drafts.Clear();
            _project = _vm?.Project;
        }
        else if (IsDirty && _row is not null)
            _drafts[_row.Device] = new(_instances, _types, _baseline, _signature, MessageText.Text ?? "");
        else if (_row is not null) _drafts.Remove(_row.Device);
        PruneDrafts();
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
        if (!ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device.Device)))
        {
            _row = null;
            ShowState(ParameterEmptyState.Text(project, null, 0));
            return;
        }

        var (instances, types) = _vm.LoadParametersFor(device);
        // Compare the entire resolved definition and model values before restoring a draft.
        // Catalogue/visibility changes must not bring back an editor for an obsolete program.
        _signature = JsonSerializer.Serialize(new { instances, types, device.Device.ProductRefId,
            device.Device.Hardware2ProgramRefId, device.Device.ParameterValues });
        var restored = _drafts.Remove(device.Device, out var draft) && draft.Signature == _signature;
        var discarded = draft is not null && !restored;
        if (restored) { instances = draft!.Instances; types = draft.Types; }
        ApplyButton.IsVisible = true;
        Show($"Parameter – {device.Name}", instances, types, _vm.ParameterWarning,
            ParameterEmptyState.Text(project, device.Device, instances.Count));
        if (restored)
        {
            _baseline = draft!.Baseline;
            MessageText.Text = draft.Message;
        }
        else if (discarded) MessageText.Text = ParameterEmptyState.DiscardedText;
        var baseline = _baseline;
        _apply = () => _vm.ApplyParameterValues(device.Device, instances, types, baseline);
    }

    /// <summary>
    /// Re-reads the model, retaining compatible drafts. A new project or undo snapshot invalidates them.
    /// </summary>
    public void Refresh()
    {
        if (_vm is null) return;
        var drafts = IsDirty || _drafts.Count > 0;
        Load(_row);
        if (drafts && !HasUnappliedChanges) MessageText.Text = ParameterEmptyState.DiscardedText;
    }

    /// <summary>Applies the drafts atomically; failures stay inline instead of throwing at the caller.</summary>
    public bool Apply()
    {
        if (_apply is null) return false;
        var apply = _apply;
        var device = _row?.Device;
        var instances = _instances;
        var baseline = _baseline;
        try
        {
            apply();
            foreach (var instance in instances) baseline[instance.Id] = instance.Value;
            if (device is not null && _drafts.TryGetValue(device, out var draft)
                && ReferenceEquals(draft.Instances, instances)) _drafts.Remove(device);
            if (ReferenceEquals(_instances, instances)) MessageText.Text = Strings.Get("ParameterSaved");
            return true;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_instances, instances)) MessageText.Text = ex.Message;
            return false;
        }
    }

    /// <summary>Fills the pane from already resolved parameters (dialog wrapper, demo screens).</summary>
    internal void LoadResolved(string? title, IEnumerable<ParameterInstance> parameters,
        IEnumerable<ParameterType> types, Func<bool>? apply, string? warning = null)
    {
        _drafts.Clear();
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
        _instances = instances;
        _types = types;
        _baseline = instances.ToDictionary(i => i.Id, i => i.Value);
        HeaderText.Text = string.IsNullOrWhiteSpace(title) ? Strings.Get("ParameterTitle") : title;

        var hasRows = instances.Count > 0;
        ParameterList.ItemsSource = hasRows ? ParameterItemViewModel.Build(instances, types) : null;
        ParameterList.IsVisible = hasRows;
        NoticeExpander.IsVisible = hasRows;
        EmptyText.Text = empty ?? ParameterEmptyState.NoParametersText;
        EmptyText.IsVisible = empty is not null;

        var hasWarning = hasRows && !string.IsNullOrWhiteSpace(warning);
        WarningText.IsVisible = hasWarning;
        WarningDetail.IsVisible = hasWarning;
        WarningDetail.Text = warning ?? "";
        MessageText.Text = "";
    }

    private void ShowState(string? empty)
    {
        _instances = [];
        _types = [];
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
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Apply();

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
