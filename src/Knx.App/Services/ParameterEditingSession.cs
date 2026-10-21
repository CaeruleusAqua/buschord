using System.Text.Json;
using Knx;

namespace Knx.App.Services;

public sealed record ParameterChange(string Id, string Name, string? Before, string? After, string? Error)
{
    public string Description => $"{Name} ({Id}): {Before ?? "∅"} → {After ?? "∅"}{(Error is null ? "" : $" — {Error}")}";
}

public sealed record ParameterChangePreview(DeviceInstance? Device, IReadOnlyList<ParameterChange> Changes, string Limitations,
    string? Error = null)
{
    public bool IsValid => Error is null && Changes.All(c => c.Error is null);
}

/// <summary>Project-scoped draft state, independent of the number and lifetime of parameter views.</summary>
public sealed class ParameterEditingSession
{
    private readonly MainWindowViewModel? _vm;
    private DeviceRow? _row;
    private Func<bool>? _apply;
    private Dictionary<string, string?> _baseline = [];
    private Project? _project;
    private string _signature = "";
    private List<ParameterInstance> _instances = [];
    private List<ParameterType> _types = [];
    private readonly Dictionary<DeviceInstance, Draft> _drafts = [];
    private sealed record Draft(List<ParameterInstance> Instances, List<ParameterType> Types,
        Dictionary<string, string?> Baseline, string Signature, string Message);

    public ParameterEditingSession() { }
    public ParameterEditingSession(MainWindowViewModel vm) => _vm = vm;
    public event EventHandler? Changed;
    public DeviceInstance? Device => _row?.Device;
    public string? Title { get; private set; }
    public string Warning { get; private set; } = "";
    public string? EmptyText { get; private set; } = ParameterEmptyState.Text(null, null, 0);
    public string Message { get; private set; } = "";
    public ParameterItemViewModel[] Rows { get; private set; } = [];
    /// <summary>False as soon as the project or device identity behind the loaded editor is gone.</summary>
    public bool CanApply => _apply is not null && !IsStale;
    public ParameterChangePreview? Preview { get; private set; }
    private bool IsDirty => _instances.Any(i => i.Value != _baseline.GetValueOrDefault(i.Id));
    private bool IsStale => _vm is not null && (!ReferenceEquals(_project, _vm.Project) || Device is null
        || _vm.Project is null || !ProjectQuery.DeviceList(_vm.Project).Any(d => ReferenceEquals(d, Device)));

    /// <summary>Visible rows whose draft value differs from the saved baseline.</summary>
    public int ChangedCount => Rows.Count(r => r.IsVisible && r.Value != _baseline.GetValueOrDefault(r.Instance.Id));

    /// <summary>Draft indicator text for the footer; counts only this device, never hidden drafts.</summary>
    public string DraftText => ChangedCount switch
    {
        0 => Strings.Language == "en" ? "Unapplied parameter drafts" : "Nicht übernommene Parameterentwürfe",
        1 => Strings.Language == "en" ? "1 unapplied change" : "1 nicht übernommene Änderung",
        var count => Strings.Language == "en" ? $"{count} unapplied changes" : $"{count} nicht übernommene Änderungen",
    };

    /// <summary>Core validation of every visible, editable draft row; field editors show the same errors.</summary>
    public IReadOnlyList<(string Name, string Error)> ValidationErrors => Rows
        .Where(r => r.IsVisible && r.IsEnabled)
        .Select(r => (r.Name, Valid: ParameterValidation.TryValidate(r.Instance, r.Value, out var error), Error: error))
        .Where(x => !x.Valid)
        .Select(x => (x.Name, x.Error ?? ""))
        .ToArray();

    public int ValidationErrorCount => ValidationErrors.Count;
    public bool HasValidationErrors => ValidationErrorCount > 0;
    public string ValidationSummary
    {
        get
        {
            var errors = ValidationErrors;
            if (errors.Count == 0) return "";
            var names = string.Join(", ", errors.Take(3).Select(e => e.Name));
            var more = errors.Count > 3 ? " …" : "";
            return (Strings.Language, errors.Count) switch
            {
                ("en", 1) => $"1 invalid value: {names}",
                ("en", var count) => $"{count} invalid values: {names}{more}",
                (_, 1) => $"1 ungültiger Wert: {names}",
                (_, var count) => $"{count} ungültige Werte: {names}{more}",
            };
        }
    }

    /// <summary>True when a pending draft can be restored to its declared default.</summary>
    public bool CanReset => HasUnappliedChanges && Rows.Any(r => r.IsVisible && r.IsEnabled
        && r.Instance.DefaultValue is not null && r.Value != r.Instance.DefaultValue);

    /// <summary>Restores visible editable drafts to the parameter's declared default value.</summary>
    public void ResetToDefaults()
    {
        foreach (var row in Rows)
            if (row.IsVisible && row.IsEnabled && row.Instance.DefaultValue is { } value && row.Value != value)
                row.Value = value;
    }

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

    public void DiscardDrafts()
    {
        _drafts.Clear();
        foreach (var instance in _instances) instance.Value = _baseline.GetValueOrDefault(instance.Id);
        if (_vm is not null) Load(_row);
        else { SetRows(); Message = ""; Publish(); }
    }

    public void Load(DeviceRow? device)
    {
        if (!ReferenceEquals(_project, _vm?.Project))
        {
            _drafts.Clear();
            _project = _vm?.Project;
        }
        else if (IsDirty && _row is not null)
            _drafts[_row.Device] = new(_instances, _types, _baseline, _signature, Message);
        else if (_row is not null) _drafts.Remove(_row.Device);
        PruneDrafts();
        _row = device;
        _apply = null;
        if (_vm is not { Project: { } project })
        {
            ShowState(ParameterEmptyState.Text(null, device?.Device, 0));
            return;
        }
        if (device is null || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device.Device)))
        {
            _row = null;
            ShowState(ParameterEmptyState.Text(project, null, 0));
            return;
        }
        var (instances, types) = _vm.LoadParametersFor(device);
        // The complete definition and saved values identify a draft generation.
        _signature = JsonSerializer.Serialize(new { instances, types, device.Device.ProductRefId,
            device.Device.Hardware2ProgramRefId, device.Device.ParameterValues });
        var restored = _drafts.Remove(device.Device, out var draft) && draft.Signature == _signature;
        if (restored) { instances = draft!.Instances; types = draft.Types; }
        _instances = instances;
        _types = types;
        _baseline = restored ? draft!.Baseline : instances.ToDictionary(i => i.Id, i => i.Value);
        Title = $"Parameter – {device.Name}";
        Warning = _vm.ParameterWarning;
        EmptyText = ParameterEmptyState.Text(project, device.Device, instances.Count);
        Message = restored ? draft!.Message : draft is not null ? ParameterEmptyState.DiscardedText : "";
        var baseline = _baseline;
        _apply = () => _vm.ApplyParameterValues(device.Device, instances, types, baseline);
        SetRows();
        Publish();
    }

    public void Refresh()
    {
        if (_vm is null) return;
        var drafts = IsDirty || _drafts.Count > 0;
        Load(_row);
        if (drafts && !HasUnappliedChanges) { Message = ParameterEmptyState.DiscardedText; Publish(); }
    }

    /// <summary>Pure parameter diff and core validation; never evaluates unknown manufacturer effects.</summary>
    public ParameterChangePreview ReviewChanges()
    {
        var map = _types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var changes = _instances.Where(i => i.Value != _baseline.GetValueOrDefault(i.Id)).Select(i =>
        {
            var validation = new ParameterInstance
            {
                Id = i.Id, Name = i.Name, IsEnabled = i.IsEnabled, UnsupportedReason = i.UnsupportedReason,
                ParameterType = i.ParameterType ?? (i.ParameterTypeId is { } id ? map.GetValueOrDefault(id) : null),
            };
            ParameterValidation.TryValidate(validation, i.Value, out var error);
            return new ParameterChange(i.Id, i.Name, _baseline.GetValueOrDefault(i.Id), i.Value, error);
        }).ToArray();
        var stale = IsStale;
        var preview = new ParameterChangePreview(Device, changes, Strings.Language == "en"
            ? "Communication-object activation and assignment effects are not evaluated. Existing links are preserved. Unsupported manufacturer dynamics and DCA are not executed."
            : "KO-Aktivierung und Zuordnungswirkungen werden nicht ausgewertet. Bestehende Verknüpfungen bleiben erhalten. Nicht unterstützte Herstellerdynamik und DCA werden nicht ausgeführt.",
            stale ? Strings.Get("LinkProjectChanged") : null);
        Preview = preview;
        Message = preview.Error ?? string.Join(Environment.NewLine, changes.Where(c => c.Error is not null).Select(c => $"{c.Name}: {c.Error}"));
        Changed?.Invoke(this, EventArgs.Empty);
        return preview;
    }

    public void CancelReview() { Preview = null; Changed?.Invoke(this, EventArgs.Empty); }

    public bool Apply()
    {
        if (_apply is null) return false;
        var apply = _apply;
        var device = Device;
        var instances = _instances;
        var baseline = _baseline;
        try
        {
            if (!ReviewChanges().IsValid || !ReferenceEquals(_instances, instances)) return false;
            if (!apply()) return false;
            foreach (var instance in instances) baseline[instance.Id] = instance.Value;
            if (device is not null && _drafts.TryGetValue(device, out var draft)
                && ReferenceEquals(draft.Instances, instances)) _drafts.Remove(device);
            if (ReferenceEquals(_instances, instances)) { Message = Strings.Get("ParameterSaved"); Publish(); }
            return true;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_instances, instances)) { Message = ex.Message; Changed?.Invoke(this, EventArgs.Empty); }
            return false;
        }
    }

    internal void LoadResolved(string? title, IEnumerable<ParameterInstance> parameters,
        IEnumerable<ParameterType> types, Func<bool>? apply, string? warning)
    {
        _drafts.Clear(); _row = null; _apply = apply;
        _instances = parameters.ToList(); _types = types.ToList();
        _baseline = _instances.ToDictionary(i => i.Id, i => i.Value);
        Title = title; Warning = warning ?? ""; Message = "";
        EmptyText = _instances.Count == 0 ? ParameterEmptyState.NoParametersText : null;
        SetRows(); Publish();
    }

    private void ShowState(string? empty)
    {
        _instances = []; _types = []; _baseline = []; _apply = null;
        Rows = []; Title = null; Warning = ""; EmptyText = empty; Message = "";
        Publish();
    }

    private void SetRows() => Rows = ParameterItemViewModel.Build(_instances, _types, () =>
    {
        // Editing invalidates the reviewed snapshot, never the draft or its validation feedback.
        Preview = null;
        Changed?.Invoke(this, EventArgs.Empty);
    });
    private void Publish() { Preview = null; Changed?.Invoke(this, EventArgs.Empty); }
}
