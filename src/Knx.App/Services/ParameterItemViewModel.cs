using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Knx;
using Knx.App.Localization;

namespace Knx.App.Services;

/// <summary>Anzeigbare Parameter-Zeile; bildet KSXML-Parameter auf editierbare Eigenschaften ab.</summary>
public sealed partial class ParameterItemViewModel : ObservableObject
{
    private readonly Action? _onChanged;

    public ParameterItemViewModel(ParameterInstance instance, ParameterType? type, Action? onChanged = null)
    {
        Instance = instance;
        Type = type;
        _onChanged = onChanged;
        _value = instance.Value ?? instance.DefaultValue;
    }

    public ParameterInstance Instance { get; private set; }
    public ParameterType? Type { get; private set; }
    private ParameterResourceReader? _resources;
    internal ParameterResourceReader? Resources { get => _resources; set => SetProperty(ref _resources, value); }
    public bool HasContextHelp => Instance.Placement?.HelpContext is { Length: > 0 } || Description is { Length: > 0 };
    public string HelpAutomationName => WorkbenchStrings.Get("ParameterHelpFor", Name);
    public ParameterHelpContent ReadHelp() => Resources?.ReadHelp(Instance.Placement?.HelpContext) ?? new(ParameterResourceStatus.Missing);
    public ParameterImageContent ReadImage() => Resources?.ReadImage(Type?.PictureResourceId) ?? new(ParameterResourceStatus.Missing);

    internal ParameterItemViewModel Rebind(ParameterInstance instance, ParameterType? type)
    {
        Instance = instance; Type = type;
        OnPropertyChanged(nameof(Type));
        var choices = type?.Restrictions.Where(r => r.EnumerationValue is not null)
            .Select(r => new ParameterChoice(r.EnumerationValue!, r.EnumerationText ?? r.EnumerationValue!)).ToArray() ?? [];
        if (!Choices.SequenceEqual(choices))
        {
            Choices.Clear();
            foreach (var choice in choices) Choices.Add(choice);
        }
        RefreshState();
        OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(Unit)); OnPropertyChanged(nameof(RangeText));
        return this;
    }

    public string Name => Instance.Name;
    public string? Description => Instance.UnsupportedReason ?? Type?.UnsupportedReason ?? Instance.Description ?? Type?.Description;
    public bool HasStorage => Instance.Storage.Status != ParameterStorageStatus.NotDeclared;
    public string StorageAutomationName => WorkbenchStrings.Get("ParameterStorageAccessible", Name);
    [ObservableProperty] private bool _isStorageExpanded;
    /// <summary>Read-only effective storage diagnostics; never an image, write address or editability gate.</summary>
    public string? StorageText
    {
        get
        {
            var storage = Instance.Storage;
            if (!HasStorage) return null;
            var lines = new List<string>();
            string W(string key, params object[] args) => WorkbenchStrings.Get(key, args);
            string Decimal<T>(T? value) where T : struct, IFormattable => value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? W("Unknown");
            string Address(uint? value) => value is { } address ? "0x" + address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : W("Unknown");
            if (storage.Status != ParameterStorageStatus.Supported || storage.Kind is null || !Enum.IsDefined(storage.Kind.Value))
            {
                lines.Add(W(storage.Status switch
                {
                    ParameterStorageStatus.InvalidDeclaration => "StorageInvalid",
                    ParameterStorageStatus.MissingArgument => "StorageMissingArgument",
                    ParameterStorageStatus.UnknownSegment => "StorageUnknownSegment",
                    ParameterStorageStatus.OutOfRange => "StorageOutOfRange",
                    _ => "StorageUnsupported",
                }));
                if (storage.CodeSegmentId is { } segment) lines.Add(W("StorageSegment", segment));
                if (storage.ArgumentId is { } argument) lines.Add(W("StorageArgument", argument));
                if (storage.ModuleInstanceId is { } module) lines.Add(W("StorageModule", module));
            }
            else
            {
                switch (storage.Kind.Value)
                {
                    case ParameterStorageKind.AbsoluteMemory:
                        lines.Add(W("StorageAbsolute"));
                        lines.Add(W("StorageAbsoluteAddress", Address(storage.StartAddress)));
                        break;
                    case ParameterStorageKind.RelativeMemory:
                        lines.Add(W("StorageRelative"));
                        lines.Add(W("StorageRelativeOffset", Address(storage.StartAddress)));
                        lines.Add(W("StorageLoadStateMachine", Decimal(storage.LoadStateMachine)));
                        break;
                    case ParameterStorageKind.PropertyByIndex:
                        lines.Add(W("StoragePropertyIndex"));
                        lines.Add(W("StorageXmlObjectIndex", Decimal(storage.ObjectIndex)));
                        lines.Add(W("StoragePropertyOffset", Decimal(storage.StartAddress)));
                        lines.Add(W("StoragePropertyId", Decimal(storage.PropertyId)));
                        break;
                    case ParameterStorageKind.PropertyByType:
                        lines.Add(W("StoragePropertyType"));
                        lines.Add(W("StorageObjectType", Decimal(storage.ObjectType)));
                        lines.Add(W("StorageOccurrence", Decimal(storage.Occurrence)));
                        lines.Add(W("StoragePropertyOffset", Decimal(storage.StartAddress)));
                        lines.Add(W("StoragePropertyId", Decimal(storage.PropertyId)));
                        break;
                }
                if (storage.Kind is ParameterStorageKind.AbsoluteMemory or ParameterStorageKind.RelativeMemory)
                    lines.Add(W("StorageSegment", storage.CodeSegmentId ?? W("Unknown")));
                lines.Add(W("StorageBitOffset", Decimal(storage.BitOffset)));
                lines.Add(storage.Resource?.DeclaredSize is { } resourceSize
                    ? W("StorageResourceSize", resourceSize) : W("StorageResourceSizeUnknown"));
                if (storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: { } bits, ByteLength: { } bytes } footprint)
                {
                    lines.Add(W("StorageFootprintBits", bits));
                    lines.Add(W("StorageFootprintBytes", bytes));
                    lines.Add(W(footprint.Coverage switch
                    {
                        ParameterResourceCoverage.Covered => "StorageCoverageCovered",
                        ParameterResourceCoverage.Uncovered => "StorageCoverageUncovered",
                        _ => "StorageCoverageUnknown",
                    }));
                }
                else
                {
                    lines.Add(W(storage.Footprint?.Status == ParameterFootprintStatus.OutOfRange
                        ? "StorageFootprintOutOfRange" : "StorageFootprintUnknown"));
                    lines.Add(W("StorageCoverageUnknown"));
                }
                if (storage.PropertyProfile is { } property)
                {
                    lines.Add(W("StorageArchiveMaskProfile", W(property.Selection.Status switch
                    {
                        MaskProfileStatus.Supported => "StorageMaskSupported",
                        MaskProfileStatus.MissingMasterData => "StorageMaskMissingMaster",
                        MaskProfileStatus.UnreadableMasterData => "StorageMaskUnreadable",
                        MaskProfileStatus.MissingMaskId => "StorageMaskMissingId",
                        MaskProfileStatus.MaskNotFound => "StorageMaskNotFound",
                        MaskProfileStatus.Ambiguous => "StorageMaskAmbiguous",
                        MaskProfileStatus.MissingHawkData => "StorageMaskMissingHawk",
                        MaskProfileStatus.MissingFirstAppObject => "StorageMaskMissingFirst",
                        MaskProfileStatus.MaskMismatch => "StorageMaskMismatch",
                        MaskProfileStatus.UnsupportedApplicationNamespace => "StorageMaskApplicationUnsupported",
                        _ => "StorageMaskInvalid",
                    })));
                    if (property.ApplicationMaskVersionId is { } applicationMask) lines.Add(W("StorageApplicationMask", applicationMask));
                    if (property.Selection.MaskVersionId is { } selectedMask && selectedMask != property.ApplicationMaskVersionId)
                        lines.Add(W("StorageSelectedMask", selectedMask));
                    var knownProfile = property.Selection.Status == MaskProfileStatus.Supported
                        && property.ApplicationMaskVersionId is not null && property.ApplicationMaskVersionId == property.Selection.MaskVersionId;
                    if (knownProfile)
                    {
                        lines.Add(W("StorageMaskVersion", property.Selection.LegacyVersion));
                        lines.Add(W("StorageObjectDomain", W(property.Domain switch
                        {
                            ParameterPropertyDomain.System => "StorageDomainSystem",
                            ParameterPropertyDomain.Application => "StorageDomainApplication",
                            _ => "StorageDomainUnknown",
                        })));
                        if (property.Domain == ParameterPropertyDomain.Application && property.ApplicationObjectIndex is { } applicationIndex)
                            lines.Add(W("StorageApplicationObjectIndex", applicationIndex));
                        if (property.DeclaredPdt is { } pdt) lines.Add(W("StorageDeclaredPdt", pdt));
                        if (property.TypeStatus == ParameterPropertyTypeStatus.Known && property.DeclaredPdt is not null && property.ElementBytes is { } elementBytes)
                            lines.Add(W("StoragePdtElementBytes", elementBytes));
                        else lines.Add(W(property.TypeStatus switch
                        {
                            ParameterPropertyTypeStatus.NotDeclared => "StoragePdtNotDeclared",
                            ParameterPropertyTypeStatus.Ambiguous => "StoragePdtAmbiguous",
                            ParameterPropertyTypeStatus.Unsupported => "StoragePdtUnsupported",
                            _ => "StoragePdtUnknown",
                        }));
                    }
                    else lines.Add(W("StoragePdtUnknown"));
                }
            }
            if (Type?.DataType == ParameterDataType.RawData)
                lines.Add(Type.RawDataMaxSize is { } rawMaximum ? W("StorageRawDataMaximum", rawMaximum) : W("StorageRawDataUnknown"));
            return string.Join(Environment.NewLine, lines);
        }
    }
    public string? Unit => Type?.DataType == ParameterDataType.Time ? Type.TimeUnit switch
    {
        "Hours" => "h", "Minutes" => "min", "Seconds" or "PackedDaysHoursMinutesAndSeconds" => "s",
        "HundredMilliseconds" => "100 ms", "TenMilliseconds" => "10 ms",
        "Milliseconds" or "PackedSecondsAndMilliseconds" or "PackedMinutesSecondsAndMilliseconds" => "ms",
        _ => Type.TimeUnit,
    } : Instance.Unit ?? Type?.Unit;
    public bool IsVisible => Instance.IsVisible;
    public bool IsEnabled => Instance.IsEnabled && Instance.UnsupportedReason is null && Type?.UnsupportedReason is null
        && Type?.DataType is ParameterDataType.Number or ParameterDataType.Float or ParameterDataType.Text
            or ParameterDataType.Enumeration or ParameterDataType.Boolean or ParameterDataType.Time or ParameterDataType.IPAddress;

    public ParameterKind Kind => Type?.DataType == ParameterDataType.Picture ? ParameterKind.Picture : !IsEnabled ? ParameterKind.ReadOnly : Type?.DataType switch
    {
        ParameterDataType.Number => ParameterKind.Number,
        ParameterDataType.Float => ParameterKind.Float,
        ParameterDataType.Text => ParameterKind.Text,
        ParameterDataType.Enumeration => ParameterKind.Enumeration,
        ParameterDataType.Boolean => ParameterKind.Boolean,
        ParameterDataType.Color => ParameterKind.Color,
        ParameterDataType.Date => ParameterKind.Date,
        ParameterDataType.DateEncoding => ParameterKind.Date,
        ParameterDataType.Time => ParameterKind.Duration,
        ParameterDataType.TimeWithUnit => ParameterKind.Time,
        ParameterDataType.IPAddress => ParameterKind.IpAddress,
        ParameterDataType.Picture => ParameterKind.Picture,
        ParameterDataType.RawData => ParameterKind.RawData,
        _ => ParameterKind.ReadOnly,
    };

    [ObservableProperty] private string? _value;

    partial void OnValueChanged(string? value)
    {
        Instance.Value = value;
        if (Kind is ParameterKind.Boolean) OnPropertyChanged(nameof(IsChecked));
        _onChanged?.Invoke();
    }

    internal void RefreshState()
    {
        Value = Instance.Value;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasContextHelp)); OnPropertyChanged(nameof(HelpAutomationName));
        RefreshStorage();
    }

    internal void RefreshStorage()
    {
        if (!HasStorage) IsStorageExpanded = false;
        OnPropertyChanged(nameof(HasStorage));
        OnPropertyChanged(nameof(StorageText));
        OnPropertyChanged(nameof(StorageAutomationName));
    }

    public bool IsChecked
    {
        get => Value is "1" or "true" or "True" or "on" or "On" or "Enabled";
        set
        {
            Value = value ? "1" : "0";
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ParameterChoice> Choices { get; } = [];

    public string? RangeText => Type is { } t
        ? string.Join(", ", t.Restrictions.Select(r =>
            t.DataType == ParameterDataType.Time && r.MinimumValue is { } exactMin && r.MaximumValue is { } exactMax
                ? $"{exactMin}..{exactMax}" :
            r.Min is { } min && r.Max is { } max ? $"{min}..{max}"
            : r.EnumerationValue ?? "?"))
        : null;

    public static ParameterItemViewModel[] Build(IEnumerable<ParameterInstance> instances, IEnumerable<ParameterType> types,
        Action? onChanged = null)
    {
        var map = types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        return instances.Select(i =>
        {
            var type = i.ParameterType ?? (i.ParameterTypeId is { } id ? map.GetValueOrDefault(id) : null);
            var vm = new ParameterItemViewModel(i, type, onChanged);
            if (type is not null)
                foreach (var r in type.Restrictions)
                    if (r.EnumerationValue is { } ev) vm.Choices.Add(new(ev, r.EnumerationText ?? ev));
            return vm;
        }).ToArray();
    }
}

public sealed record ParameterChoice(string Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Why the parameter pane has no rows to show.</summary>
public enum ParameterEmptyReason { None, NeedsProject, NeedsDevice, WithoutProduct, WithoutParameters }

/// <summary>
/// Short empty-state straplines for the parameter pane. A device without a product says so in one
/// sentence; the XML diagnostics of the loader stay out of the UI.
/// </summary>
public static class ParameterEmptyState
{
    // Localization.cs is frozen this round; these two sentences live here until it gains keys.
    public static string NoParametersText => Strings.Language == "en"
        ? "No parameters are stored for this device."
        : "Für dieses Gerät liegen keine Parameter vor.";

    public static string DiscardedText => Strings.Language == "en"
        ? "Unsaved changes discarded."
        : "Ungespeicherte Änderungen verworfen.";

    public static ParameterEmptyReason Reason(Project? project, DeviceInstance? device, int rowCount)
    {
        if (project is null) return ParameterEmptyReason.NeedsProject;
        if (device is null) return ParameterEmptyReason.NeedsDevice;
        // Same identity the parameter loader resolves against.
        var reference = device.Hardware2ProgramRefId ?? device.ProductRefId;
        if (string.IsNullOrWhiteSpace(reference)) return ParameterEmptyReason.WithoutProduct;
        return rowCount == 0 ? ParameterEmptyReason.WithoutParameters : ParameterEmptyReason.None;
    }

    public static string? Text(Project? project, DeviceInstance? device, int rowCount) =>
        Reason(project, device, rowCount) switch
        {
            ParameterEmptyReason.NeedsProject => Strings.Get("UxNeedsProject"),
            ParameterEmptyReason.NeedsDevice => Strings.Get("UxNeedsDevice"),
            ParameterEmptyReason.WithoutProduct => Strings.Get("UxDeviceWithoutProduct"),
            ParameterEmptyReason.WithoutParameters => NoParametersText,
            _ => null,
        };
}

public enum ParameterKind { ReadOnly, Number, Float, Text, Enumeration, Boolean, Color, Date, Time, IpAddress, Picture, RawData, Duration }
