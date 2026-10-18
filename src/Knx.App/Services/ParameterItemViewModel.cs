using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Knx;

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

    public ParameterInstance Instance { get; }
    public ParameterType? Type { get; }

    public string Name => Instance.Name;
    public string? Description => Instance.UnsupportedReason ?? Type?.UnsupportedReason ?? Instance.Description ?? Type?.Description;
    public string? Unit => Instance.Unit ?? Type?.Unit;
    public bool IsVisible => Instance.IsVisible;
    public bool IsEnabled => Instance.IsEnabled && Instance.UnsupportedReason is null && Type?.UnsupportedReason is null
        && Type?.DataType is ParameterDataType.Number or ParameterDataType.Float or ParameterDataType.Text
            or ParameterDataType.Enumeration or ParameterDataType.Boolean;

    public ParameterKind Kind => !IsEnabled ? ParameterKind.ReadOnly : Type?.DataType switch
    {
        ParameterDataType.Number => ParameterKind.Number,
        ParameterDataType.Float => ParameterKind.Float,
        ParameterDataType.Text => ParameterKind.Text,
        ParameterDataType.Enumeration => ParameterKind.Enumeration,
        ParameterDataType.Boolean => ParameterKind.Boolean,
        ParameterDataType.Color => ParameterKind.Color,
        ParameterDataType.Date => ParameterKind.Date,
        ParameterDataType.DateEncoding => ParameterKind.Date,
        ParameterDataType.Time => ParameterKind.Time,
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

public enum ParameterKind { ReadOnly, Number, Float, Text, Enumeration, Boolean, Color, Date, Time, IpAddress, Picture, RawData }
