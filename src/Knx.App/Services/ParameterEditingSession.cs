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
    public int AddedObjects { get; init; }
    public int RemovedObjects { get; init; }
    public int ActivatedObjects { get; init; }
    public int DeactivatedObjects { get; init; }
    public int AffectedLinks { get; init; }
}

/// <summary>A navigable range of visible parameter rows; <see cref="FirstRow"/> indexes <see cref="ParameterEditingSession.Rows"/>.</summary>
public sealed record ParameterSection(string Title, int FirstRow, int RowCount);

/// <summary>Project-scoped draft state, independent of the number and lifetime of parameter views.</summary>
public sealed class ParameterEditingSession
{
    private readonly MainWindowViewModel? _vm;
    private DeviceRow? _row;
    private Func<bool>? _apply;
    private Dictionary<string, string?> _baseline = [];
    private Project? _project;
    private List<ParameterInstance> _instances = [];
    private List<ParameterType> _types = [];
    private Action<IReadOnlyDictionary<string, string?>, IEnumerable<ParameterInstance>, string?>? _evaluate;
    private Func<IReadOnlyDictionary<string, string?>, string?, ApplicationSnapshot>? _evaluateStructure;
    private ApplicationSnapshot? _structure, _baselineStructure;
    private ParameterPresentation _presentation = ParameterPresentation.Empty;
    private ParameterResourceReader? _resources;
    public ParameterPresentation Presentation => _structure?.Evaluation.Presentation ?? _presentation;
    private List<ParameterInstance> _baselineInstances = [];
    private List<ParameterType> _baselineTypes = [];
    private Dictionary<string, ParameterInstance> _knownInstances = [];
    private string? _definition;
    private Dictionary<string, string> _savedValues = [];
    private string? _productRefId, _hardwareRefId;
    private Dictionary<string, string?> _userValues = [];
    private Dictionary<string, string?> _evaluatedValues = [];
    private bool _evaluating;
    private string? _evaluationError;
    private readonly Dictionary<DeviceInstance, Draft> _drafts = [];
    private string? _manualReadOnlyReason;
    private sealed record Draft(List<ParameterInstance> Instances, List<ParameterType> Types,
        Dictionary<string, string?> Baseline, Dictionary<string, string> SavedValues,
        string? ProductRefId, string? Hardware2ProgramRefId, string Message, Dictionary<string, string?> UserValues,
        ApplicationSnapshot? Structure, ApplicationSnapshot? BaselineStructure, List<ParameterInstance> BaselineInstances,
        List<ParameterType> BaselineTypes, Dictionary<string, ParameterInstance> KnownInstances, string? Definition,
        string? EvaluationError, string? SelectedSection, ParameterPresentation Presentation);

    public ParameterEditingSession() { }
    public ParameterEditingSession(MainWindowViewModel vm) => _vm = vm;
    public event EventHandler? Changed;
    public DeviceInstance? Device => _row?.Device;
    public string? Title { get; private set; }
    public string Warning { get; private set; } = "";
    public string? EmptyText { get; private set; } = ParameterEmptyState.Text(null, null, 0);
    public string Message { get; private set; } = "";
    public ParameterItemViewModel[] Rows { get; private set; } = [];

    /// <summary>Visible metadata sections of the loaded parameter set; one section means the strip stays hidden.</summary>
    public ParameterSection[] Sections { get; private set; } = [];
    public string? SelectedSection { get; private set; }
    public bool HasSectionNavigation => Sections.Length > 1;

    /// <summary>Identity of the project instance behind the loaded editor, so a host can detect a project switch.</summary>
    public string? ProjectId => _vm?.Project?.Id;
    public string? ProjectName => _vm?.Project?.Name;

    /// <summary>Notice for drafts dropped by a project switch or a refresh; never applied silently.</summary>
    public string? DraftNotice { get; private set; }

    /// <summary>Set for DCA/plug-in or opaque applications: editors and apply stay disabled, values stay untouched.</summary>
    public string? ReadOnlyReason { get; private set; }
    public bool IsReadOnly => ReadOnlyReason is not null;
    /// <summary>False as soon as the project or device identity behind the loaded editor is gone.</summary>
    public bool CanApply => _apply is not null && !IsStale && !IsReadOnly && _evaluationError is null;
    public ParameterChangePreview? Preview { get; private set; }
    private bool IsDirty => _knownInstances.Values.Any(i => i.Value != _baseline.GetValueOrDefault(i.Id));
    public bool IsStale => _vm is not null && (!ReferenceEquals(_project, _vm.Project) || Device is null
        || _vm.Project is null || !ProjectQuery.DeviceList(_vm.Project).Any(d => ReferenceEquals(d, Device))
        || Device.ProductRefId != _productRefId || Device.Hardware2ProgramRefId != _hardwareRefId
        || Device.ParameterValues.Count != _savedValues.Count
        || _savedValues.Any(p => !Device.ParameterValues.TryGetValue(p.Key, out var saved) || saved != p.Value));

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
    public IReadOnlyList<(string Name, string Error)> ValidationErrors => IsReadOnly ? [] : Rows
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
    public bool CanReset => !IsReadOnly && HasUnappliedChanges && Rows.Any(r => r.IsVisible && r.IsEnabled
        && r.Instance.DefaultValue is not null && r.Value != r.Instance.DefaultValue);

    /// <summary>Restores visible editable drafts to the parameter's declared default value.</summary>
    public void ResetToDefaults()
    {
        if (IsReadOnly) return;
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
        DiscardCurrentDraft();
    }

    /// <summary>Discards only the loaded device; navigation drafts belong to their own editors.</summary>
    public void DiscardCurrentDraft()
    {
        if (Device is { } device) _drafts.Remove(device);
        foreach (var instance in _instances) instance.Value = _baseline.GetValueOrDefault(instance.Id);
        foreach (var instance in _knownInstances.Values) instance.Value = _baseline.GetValueOrDefault(instance.Id);
        if (_vm is not null) Load(_row);
        else { SetRows(); Message = ""; Publish(); }
    }

    public void Load(DeviceRow? device)
    {
        var sameDevice = ReferenceEquals(_row?.Device, device?.Device);
        if (!sameDevice) _manualReadOnlyReason = null;
        if (!ReferenceEquals(_project, _vm?.Project))
        {
            // A project switch never carries drafts over, and never applies them silently.
            if (_drafts.Count > 0 || IsDirty)
                DraftNotice = ProjectSwitchNotice(_project?.Name ?? _project?.Id);
            _drafts.Clear();
            _project = _vm?.Project;
        }
        else if (IsDirty && _row is not null)
            _drafts[_row.Device] = new(_instances, _types, _baseline,
                new Dictionary<string, string>(_savedValues),
                _productRefId, _hardwareRefId, Message, new(_userValues),
                _structure, _baselineStructure, _baselineInstances, _baselineTypes, new(_knownInstances), _definition,
                _evaluationError, SelectedSection, _presentation);
        else if (_row is not null) _drafts.Remove(_row.Device);
        PruneDrafts();
        _row = device;
        _productRefId = device?.Device.ProductRefId; _hardwareRefId = device?.Device.Hardware2ProgramRefId;
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
        var restored = _drafts.Remove(device.Device, out var draft)
            && draft.Definition == _vm.LoadedStructureDefinition
            && DraftMatches(draft, device.Device, instances, types, _vm.LoadedParameterEvaluation is not null);
        _baselineInstances = restored ? draft!.BaselineInstances : instances.Select(i => new ParameterInstance(i)).ToList();
        _baselineTypes = restored ? draft!.BaselineTypes : types;
        if (restored) { instances = draft!.Instances; types = draft.Types; }
        _instances = instances;
        _types = types;
        _evaluate = _vm.LoadedParameterEvaluation;
        _evaluateStructure = _vm.LoadedStructureEvaluation;
        _structure = restored ? draft!.Structure : _vm.LoadedStructureSnapshot;
        _baselineStructure = restored ? draft!.BaselineStructure : _vm.LoadedStructureSnapshot;
        _definition = _vm.LoadedStructureDefinition;
        _presentation = restored ? draft!.Presentation : _vm.LoadedParameterPresentation;
        _resources = _vm.LoadedParameterResources;
        _savedValues = new(device.Device.ParameterValues);
        _knownInstances = restored ? new(draft!.KnownInstances) : instances.ToDictionary(i => i.Id);
        _evaluationError = restored ? draft!.EvaluationError : _vm.ParameterEvaluationFailed ? Strings.Get("StatusParameterUnreadable", _vm.ParameterWarning) : null;
        _userValues = restored ? new(draft!.UserValues) : device.Device.ParameterValues.ToDictionary(v => v.Key, v => (string?)v.Value);
        _evaluatedValues = instances.ToDictionary(i => i.Id, i => i.Value);
        _baseline = restored ? draft!.Baseline : instances.ToDictionary(i => i.Id, i => i.Value);
        Title = $"Parameter – {device.Name}";
        Warning = _vm.ParameterWarning;
        EmptyText = ParameterEmptyState.Text(project, device.Device, instances.Count);
        Message = _evaluationError ?? (restored ? draft!.Message : draft is not null ? ParameterEmptyState.DiscardedText : "");
        _apply = () => _vm.ApplyParameterValues(device.Device, _knownInstances.Values.ToList(), _types, _baseline,
            _structure, _baselineStructure, _savedValues, _definition);
        SelectedSection = restored ? draft!.SelectedSection : null;
        SetRows();
        Publish();
    }

    public void Refresh()
    {
        if (_vm is null) return;
        var drafts = IsDirty || _drafts.Count > 0;
        var projectChanged = !ReferenceEquals(_project, _vm.Project);
        Load(_row);
        if (drafts && !HasUnappliedChanges && !projectChanged)
        {
            Message = ParameterEmptyState.DiscardedText;
            DraftNotice = RefreshNotice;
            Publish();
        }
    }

    /// <summary>
    /// Explicit read-only mode for DCA/plug-in and opaque applications. The reason is shown in the
    /// pane; editors, Apply and Reset stay disabled. Values and opaque data are only read, never rewritten.
    /// </summary>
    public void SetReadOnly(string? reason)
    {
        _manualReadOnlyReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        ApplyReadOnly();
        Publish();
    }

    public void ClearReadOnly() => SetReadOnly(null);

    public void AcknowledgeNotice() { DraftNotice = null; Publish(); }

    /// <summary>Selects a section for navigation; the pane scrolls it into view and never filters drafts away.</summary>
    public void SelectSection(string? title)
    {
        SelectedSection = Sections.Any(s => s.Title == title) ? title : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void NextSection() => SelectSection(SectionAt(1));
    public void PreviousSection() => SelectSection(SectionAt(-1));
    private string? SectionAt(int delta)
    {
        if (Sections.Length == 0) return null;
        var index = Array.FindIndex(Sections, s => s.Title == SelectedSection);
        if (index < 0) index = delta > 0 ? -1 : 0;
        return Sections[(index + delta + Sections.Length) % Sections.Length].Title;
    }

    private string ProjectSwitchNotice(string? previous) => Strings.Language == "en"
        ? $"Project changed: drafts from {(previous ?? "the previous project")} were discarded and never applied."
        : $"Projekt gewechselt: Entwürfe aus {(previous ?? "dem vorherigen Projekt")} wurden verworfen und nicht übernommen.";

    private string RefreshNotice => Strings.Language == "en"
        ? "Project state changed: drafts no longer matched the saved parameters and were discarded. Nothing was applied."
        : "Projektstand geändert: Entwürfe passten nicht mehr zu den gespeicherten Parametern und wurden verworfen. Es wurde nichts übernommen.";

    /// <summary>Pure parameter diff and core validation; never evaluates unknown manufacturer effects.</summary>
    public ParameterChangePreview ReviewChanges()
    {
        var map = _types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var changes = _knownInstances.Values.Where(i => i.Value != _baseline.GetValueOrDefault(i.Id)).Select(i =>
        {
            var validation = new ParameterInstance
            {
                Id = i.Id, Name = i.Name, IsEnabled = i.IsEnabled, UnsupportedReason = i.UnsupportedReason,
                ParameterType = i.ParameterType ?? (i.ParameterTypeId is { } id ? map.GetValueOrDefault(id) : null),
            };
            string? error;
            if (i.IsDerivedValue) ParameterValidation.TryValidate(validation.ParameterType, i.Value, out error);
            else ParameterValidation.TryValidate(validation, i.Value, out error);
            return new ParameterChange(i.Id, i.Name, _baseline.GetValueOrDefault(i.Id), i.Value, error);
        }).ToArray();
        var stale = IsStale;
        var preview = new ParameterChangePreview(Device, changes, _structure is not null
            ? Knx.App.Localization.WorkbenchStrings.Get("ParameterSupportedStructure") : Strings.Language == "en"
            ? "Communication-object activation and assignment effects are not evaluated. Existing links are preserved. Unsupported manufacturer dynamics and DCA are not executed."
            : "KO-Aktivierung und Zuordnungswirkungen werden nicht ausgewertet. Bestehende Verknüpfungen bleiben erhalten. Nicht unterstützte Herstellerdynamik und DCA werden nicht ausgeführt.",
            stale ? Strings.Get("LinkProjectChanged") : _evaluationError)
        {
            AddedObjects = _structure?.ObjectReferenceNumbers.Keys.Except(_baselineStructure?.ObjectReferenceNumbers.Keys ?? []).Count() ?? 0,
            RemovedObjects = _baselineStructure?.ObjectReferenceNumbers.Keys.Except(_structure?.ObjectReferenceNumbers.Keys ?? []).Count() ?? 0,
            ActivatedObjects = _structure?.ActiveObjects.Except(_baselineStructure?.ActiveObjects ?? new HashSet<string>()).Count() ?? 0,
            DeactivatedObjects = _baselineStructure?.ActiveObjects.Except(_structure?.ActiveObjects ?? new HashSet<string>()).Count() ?? 0,
            AffectedLinks = AffectedLinkCount(),
        };
        Preview = preview;
        Message = preview.Error ?? string.Join(Environment.NewLine, changes.Where(c => c.Error is not null).Select(c => $"{c.Name}: {c.Error}"));
        Changed?.Invoke(this, EventArgs.Empty);
        return preview;
    }

    public void CancelReview() { Preview = null; Changed?.Invoke(this, EventArgs.Empty); }

    private int AffectedLinkCount()
    {
        if (_structure is null || _baselineStructure is null || Device is null) return 0;
        var affected = _baselineStructure.ActiveObjects.Except(_structure.ActiveObjects).ToHashSet(StringComparer.Ordinal);
        return Device.ComObjectRefs.Where(o => affected.Contains(_baselineStructure.ObjectReferenceKey(o.Id)))
            .Sum(o => o.Links.Distinct(StringComparer.Ordinal).Count());
    }

    public bool Apply()
    {
        if (_apply is null || IsReadOnly || IsStale) return false;
        var apply = _apply;
        var device = Device;
        var instances = _instances;
        var baseline = _baseline;
        try
        {
            if (!ReviewChanges().IsValid || !ReferenceEquals(_instances, instances)) return false;
            if (!apply()) return false;
            foreach (var instance in instances) baseline[instance.Id] = instance.Value;
            if (ReferenceEquals(_instances, instances))
            {
                _baselineStructure = _structure;
                _baselineInstances = instances.Select(i => new ParameterInstance(i)).ToList();
                _baselineTypes = _types;
                _savedValues = device is null ? [] : new(device.ParameterValues);
            }
            if (device is not null && _drafts.TryGetValue(device, out var draft)
                && ReferenceEquals(draft.Instances, instances)) _drafts.Remove(device);
            if (ReferenceEquals(_instances, instances)) { Message = Strings.Get("ParameterSaved"); DraftNotice = null; Publish(); }
            return true;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_instances, instances)) { Message = ex.Message; Changed?.Invoke(this, EventArgs.Empty); }
            return false;
        }
    }

    internal void LoadResolved(string? title, IEnumerable<ParameterInstance> parameters,
        IEnumerable<ParameterType> types, Func<bool>? apply, string? warning, string? readOnlyReason = null)
    {
        _drafts.Clear(); _row = null; _apply = apply; _manualReadOnlyReason = readOnlyReason;
        _evaluate = null; _evaluationError = null; _userValues = []; _evaluatedValues = [];
        _evaluateStructure = null; _structure = _baselineStructure = null; _definition = null;
        _presentation = ParameterPresentation.Empty; _resources = null;
        _instances = parameters.ToList(); _types = types.ToList();
        _knownInstances = _instances.ToDictionary(i => i.Id);
        _baseline = _instances.ToDictionary(i => i.Id, i => i.Value);
        Title = title; Warning = warning ?? ""; Message = "";
        EmptyText = _instances.Count == 0 ? ParameterEmptyState.NoParametersText : null;
        SetRows(); Publish();
    }

    private void ShowState(string? empty)
    {
        _evaluate = null; _evaluationError = null; _userValues = []; _evaluatedValues = [];
        _evaluateStructure = null; _structure = _baselineStructure = null; _knownInstances = []; _definition = null;
        _presentation = ParameterPresentation.Empty; _resources = null;
        _instances = []; _types = []; _baseline = []; _apply = null;
        Rows = []; Sections = []; SelectedSection = null; Title = null; Warning = ""; EmptyText = empty; Message = "";
        ApplyReadOnly();
        Publish();
    }

    private void SetRows()
    {
        var wasEvaluating = _evaluating;
        _evaluating = true;
        try
        {
            var previousRows = Rows.ToDictionary(r => r.Instance.Id);
            var built = ParameterItemViewModel.Build(_instances, _types, OnRowsChanged);
            var nextRows = built.Select(row => previousRows.TryGetValue(row.Instance.Id, out var old) && old.Kind == row.Kind
                ? old.Rebind(row.Instance, row.Type) : row).ToArray();
            foreach (var row in nextRows) row.Resources = _resources;
            if (!Rows.SequenceEqual(nextRows)) Rows = nextRows;
        }
        finally { _evaluating = wasEvaluating; }
        Sections = BuildSections(Rows);
        if (!Sections.Any(s => s.Title == SelectedSection)) SelectedSection = Sections.FirstOrDefault()?.Title;
        ApplyReadOnly();
    }

    private void OnRowsChanged()
    {
        if (_evaluating) return;
        if (_evaluateStructure is not null)
        {
            string? edited = null;
            foreach (var instance in _instances)
                if (instance.Value != _evaluatedValues.GetValueOrDefault(instance.Id))
                { _userValues[instance.Id] = instance.Value; edited = instance.Id; }
            _evaluating = true;
            try
            {
                if (edited is not null && _structure?.Evaluation.StructuralParameters.Contains(edited) == true)
                    foreach (var instance in _instances.Where(i => !i.IsDerivedValue)) _userValues.TryAdd(instance.Id, instance.Value);
                var candidate = _evaluateStructure(_userValues, edited);
                Warning = string.Join(", ", candidate.Evaluation.Unsupported);
                if (!candidate.IsValid) _evaluationError = Strings.Get("StatusParameterUnreadable", Warning);
                else
                {
                    var anchor = Sections.FirstOrDefault(s => s.Title == SelectedSection)?.FirstRow;
                    var anchorId = anchor is { } index && index < Rows.Length ? Rows[index].Instance.Id : null;
                    var previousError = _evaluationError;
                    _evaluationError = null;
                    if (Message == previousError) Message = "";
                    foreach (var instance in candidate.Instances)
                    {
                        _baseline.TryAdd(instance.Id, instance.DefaultValue ?? instance.Value);
                        _knownInstances[instance.Id] = instance;
                    }
                    _instances = candidate.Instances; _types = candidate.Types; _structure = candidate;
                    SetRows();
                    if (anchorId is not null && Array.FindIndex(Rows, r => r.Instance.Id == anchorId) is var newIndex && newIndex >= 0)
                        SelectedSection = Sections.LastOrDefault(s => s.FirstRow <= newIndex)?.Title;
                }
                _evaluatedValues = _instances.ToDictionary(i => i.Id, i => i.Value);
                if (_evaluationError is not null) Message = _evaluationError;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            { _evaluationError = Strings.Get("StatusParameterUnreadable", ex.Message); Message = _evaluationError; }
            finally { _evaluating = false; }
        }
        else
        {
            if (_evaluating) return;
            if (_evaluate is not null)
            {
                string? edited = null;
                foreach (var instance in _instances)
                    if (instance.Value != _evaluatedValues.GetValueOrDefault(instance.Id)) { _userValues[instance.Id] = instance.Value; edited = instance.Id; }
                _evaluating = true;
                try
                {
                    _evaluate(_userValues, _instances, edited);
                    _presentation = _vm?.LoadedParameterPresentation ?? _presentation;
                    Warning = _vm?.ParameterWarning ?? Warning;
                    var previousError = _evaluationError;
                    _evaluationError = _vm?.ParameterEvaluationFailed == true ? Strings.Get("StatusParameterUnreadable", Warning) : null;
                    if (_evaluationError is not null || Message == previousError) Message = _evaluationError ?? "";
                    _evaluatedValues = _instances.ToDictionary(i => i.Id, i => i.Value);
                    foreach (var row in Rows) row.RefreshState();
                    var selectedRow = Sections.FirstOrDefault(s => s.Title == SelectedSection)?.FirstRow;
                    Sections = BuildSections(Rows);
                    if (selectedRow is not null) SelectedSection = Sections.FirstOrDefault(s => s.FirstRow == selectedRow)?.Title;
                    ApplyReadOnly();
                }
                finally { _evaluating = false; }
            }
        }
        // Editing invalidates the reviewed snapshot, never the draft or its validation feedback.
        Preview = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Section index from existing instance metadata only: a name such as "Kanal A: Schalten"
    /// groups by its channel prefix, every other visible row belongs to the general section.
    /// </summary>
    private static ParameterSection[] BuildSections(ParameterItemViewModel[] rows)
    {
        var general = Strings.Language == "en" ? "General" : "Allgemein";
        var groups = new List<(string Title, int First, int Count)>();
        for (var index = 0; index < rows.Length; index++)
        {
            if (!rows[index].IsVisible) continue;
            var title = rows[index].Instance.SectionTitle ?? SectionTitle(rows[index].Instance.Name, general);
            if (groups.Count > 0 && groups[^1].Title == title) groups[^1] = (title, groups[^1].First, groups[^1].Count + 1);
            else groups.Add((title, index, 1));
        }
        return groups.Select(g => new ParameterSection(g.Title, g.First, g.Count)).ToArray();
    }

    private static string SectionTitle(string? name, string general)
    {
        if (string.IsNullOrWhiteSpace(name)) return general;
        var separator = name.IndexOf(':');
        return separator > 0 && name[..separator].Trim() is { Length: > 0 } prefix ? prefix : general;
    }

    /// <summary>Opaque sets (DCA/plug-in values or unsupported shapes) expose a reason instead of broken editors.</summary>
    private void ApplyReadOnly() => ReadOnlyReason = _manualReadOnlyReason
        ?? (_vm is not null && Device is { } device && !IsStale ? _vm.ParameterReadOnlyReason(device) : null)
        ?? OpaqueReason();

    private string? OpaqueReason()
    {
        var visible = Rows.Where(r => r.IsVisible).ToArray();
        if (visible.Length == 0 || visible.Any(r => r.IsEnabled)) return null;
        var reason = visible.Select(r => r.Instance.UnsupportedReason ?? r.Type?.UnsupportedReason).FirstOrDefault(r => r is not null);
        return reason is null ? null : (Strings.Language == "en"
            ? $"Opaque application data: {reason} The values are shown read-only and are not executed or rewritten."
            : $"Opaque Applikationsdaten: {reason} Die Werte werden nur lesend angezeigt und nicht ausgeführt oder umgeschrieben.");
    }

    /// <summary>
    /// Draft compatibility: the stored definition and saved values must still be identical to the
    /// freshly loaded model. Replaces the former full-model JSON signature (a second 4-46 MB
    /// serialisation per open) with an allocation-free structural comparison: the copy
    /// constructors share their strings, so the walk compares references in the common case.
    /// </summary>
    private static bool DraftMatches(Draft draft, DeviceInstance device, List<ParameterInstance> instances,
        List<ParameterType> types, bool dynamic)
    {
        if (draft.ProductRefId != device.ProductRefId
            || draft.Hardware2ProgramRefId != device.Hardware2ProgramRefId) return false;
        var savedValues = device.ParameterValues;
        if (draft.SavedValues.Count != savedValues.Count) return false;
        foreach (var (id, value) in savedValues)
            if (!draft.SavedValues.TryGetValue(id, out var before) || before != value) return false;
        var declarations = draft.Structure is null ? draft.Instances : draft.BaselineInstances;
        var declaredTypes = draft.Structure is null ? draft.Types : draft.BaselineTypes;
        if (declaredTypes.Count != types.Count || declarations.Count != instances.Count) return false;
        for (var index = 0; index < types.Count; index++)
            if (!SameDeclaration(declaredTypes[index], types[index])) return false;
        for (var index = 0; index < instances.Count; index++)
        {
            var instance = instances[index];
            if (!SameDeclaration(declarations[index], instance, dynamic)) return false;
            // The loaded value is the saved overlay; the draft baseline is that overlay at draft time.
            if (instance.Value != draft.Baseline.GetValueOrDefault(instance.Id)) return false;
        }
        return true;
    }

    /// <summary>
    /// Declaration equality of two types, ignoring object identity. The resolved
    /// <see cref="ParameterInstance.ParameterType"/> needs no comparison of its own: it is derived
    /// from <see cref="ParameterInstance.ParameterTypeId"/> and the type list compared here.
    /// </summary>
    private static bool SameDeclaration(ParameterType a, ParameterType b) =>
        a.Id == b.Id && a.Name == b.Name && a.DataType == b.DataType && a.SizeInBit == b.SizeInBit
        && a.Encoding == b.Encoding && a.NumberType == b.NumberType && a.UnsupportedReason == b.UnsupportedReason
        && a.AddressType == b.AddressType && a.AddressVersion == b.AddressVersion
        && a.TimeUnit == b.TimeUnit && a.TimeUIHint == b.TimeUIHint
        && a.RawDataMaxSize == b.RawDataMaxSize
        && a.PictureResourceId == b.PictureResourceId && a.PictureAlignment == b.PictureAlignment
        && a.UIHint == b.UIHint && a.Unit == b.Unit && a.Description == b.Description && a.IsVisible == b.IsVisible
        && a.Restrictions.Count == b.Restrictions.Count
        && a.Restrictions.Zip(b.Restrictions).All(pair => SameDeclaration(pair.First, pair.Second));

    private static bool SameDeclaration(ParameterRestriction a, ParameterRestriction b) =>
        a.EnumerationValue == b.EnumerationValue && a.EnumerationText == b.EnumerationText && a.Id == b.Id
        && a.Min == b.Min && a.Max == b.Max && a.MinimumValue == b.MinimumValue && a.MaximumValue == b.MaximumValue
        && a.Pattern == b.Pattern;

    /// <summary>Declaration equality of two instances; their draft values are compared separately.</summary>
    private static bool SameDeclaration(ParameterInstance a, ParameterInstance b, bool dynamic) =>
        a.Id == b.Id && (dynamic ? a.DeclaredName == b.DeclaredName : a.Name == b.Name) && a.ParameterId == b.ParameterId && a.Unit == b.Unit
        && a.UnsupportedReason == b.UnsupportedReason && a.Description == b.Description
        && a.Storage == b.Storage
        && a.ParameterTypeId == b.ParameterTypeId && a.DefaultValue == b.DefaultValue
        && (dynamic || a.IsVisible == b.IsVisible && a.IsEnabled == b.IsEnabled)
        && a.IsStaticallyVisible == b.IsStaticallyVisible && a.IsStaticallyEnabled == b.IsStaticallyEnabled
        && a.Row == b.Row && a.Column == b.Column
        && a.Children.Count == b.Children.Count
        && a.Children.Zip(b.Children).All(pair => SameDeclaration(pair.First, pair.Second, dynamic));

    private void Publish() { Preview = null; Changed?.Invoke(this, EventArgs.Empty); }
}
