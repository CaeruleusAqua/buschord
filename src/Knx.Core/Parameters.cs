using System.Diagnostics.CodeAnalysis;

namespace Knx;

/// <summary>KSXML parameter system: types, blocks, instances and references.
/// Mirrors the parameter model of Knx.Ets.ObjectModel (ParameterType, ParameterBlock,
/// ParameterInstance, ParameterRef) — recovered from the ETS6 assembly string heap.
/// </summary>

/// <summary>Datentyp eines Parameters (KSXML).</summary>
public enum ParameterDataType
{
    None, Number, Float, Text, Color, Date, Time, IPAddress, Picture, RawData,
    Enumeration, Boolean, TimeWithUnit, DateEncoding, FloatEncoding,
}

/// <summary>UI-Hint für einen Parameter.</summary>
public enum ParameterUIHint
{
    None, CheckBox, ComboBox, TextBox, Slider, ColorPicker, DatePicker, TimePicker,
    IpAddress, Picture, Button, Label, Separator,
}

/// <summary>Restriktion eines Parameterwerts.</summary>
public sealed class ParameterRestriction
{
    public ParameterRestriction() { }

    /// <summary>Copies every declared bound, label and pattern of <paramref name="other"/>.</summary>
    public ParameterRestriction(ParameterRestriction other)
    {
        EnumerationValue = other.EnumerationValue;
        EnumerationText = other.EnumerationText;
        Id = other.Id;
        Min = other.Min;
        Max = other.Max;
        MinimumValue = other.MinimumValue;
        MaximumValue = other.MaximumValue;
        Pattern = other.Pattern;
    }

    public string? EnumerationValue { get; set; }
    public string? EnumerationText { get; set; }
    public string? Id { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>Original bounds retained for exact integer comparisons beyond double precision.</summary>
    public string? MinimumValue { get; set; }
    public string? MaximumValue { get; set; }
    public string? Pattern { get; set; }
}

/// <summary>Parameter-Typ (KSXML: ParameterType).</summary>
public sealed class ParameterType
{
    public ParameterType() { }

    /// <summary>
    /// Detached copy of the declaration: editors own their definitions and never mutate the
    /// shared catalogue model. Replaces the former JSON round trip for parameter models.
    /// </summary>
    [SetsRequiredMembers]
    public ParameterType(ParameterType other)
    {
        Id = other.Id;
        Name = other.Name;
        DataType = other.DataType;
        SizeInBit = other.SizeInBit;
        Encoding = other.Encoding;
        NumberType = other.NumberType;
        UnsupportedReason = other.UnsupportedReason;
        UIHint = other.UIHint;
        Unit = other.Unit;
        Description = other.Description;
        IsVisible = other.IsVisible;
        foreach (var restriction in other.Restrictions) Restrictions.Add(new ParameterRestriction(restriction));
    }

    public required string Id { get; init; }
    public required string Name { get; init; }
    public ParameterDataType DataType { get; set; } = ParameterDataType.None;
    public int SizeInBit { get; set; }
    public string? Encoding { get; set; }
    public string? NumberType { get; set; }
    public string? UnsupportedReason { get; set; }
    public ParameterUIHint UIHint { get; set; } = ParameterUIHint.None;
    public List<ParameterRestriction> Restrictions { get; } = [];
    public string? Unit { get; set; }
    public string? Description { get; set; }
    public bool IsVisible { get; set; } = true;
}

/// <summary>Parameter-Block (KSXML: ParameterBlock) — Container mit Zeilen/Spalten.</summary>
public sealed class ParameterBlock
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public int Rows { get; set; } = 1;
    public int Columns { get; set; } = 1;
    /// <summary>False when the initial dynamic evaluation does not instantiate this block.</summary>
    public bool IsVisible { get; set; } = true;
    /// <summary>Mirrors <see cref="IsVisible"/>; KSXML has no separate block enable action.</summary>
    public bool IsEnabled { get; set; } = true;
    public List<ParameterBlockRow> RowDefinitions { get; } = [];
    public List<ParameterBlockColumn> ColumnDefinitions { get; } = [];
    public List<ParameterInstance> Parameters { get; } = [];
}

public sealed class ParameterBlockRow
{
    public int Index { get; set; }
    public int Height { get; set; }
}

public sealed class ParameterBlockColumn
{
    public int Index { get; set; }
    public int Width { get; set; }
}

/// <summary>Parameter-Instanz (KSXML: ParameterInstance).</summary>
public sealed class ParameterInstance
{
    public ParameterInstance() { }

    /// <summary>
    /// Detached copy of the declaration and its current values. <see cref="ParameterType"/> stays
    /// a shared reference so an editor can remap it onto its own copied type list; children are
    /// copied recursively. Editors mutate this copy, never the shared catalogue model.
    /// </summary>
    [SetsRequiredMembers]
    public ParameterInstance(ParameterInstance other)
    {
        Id = other.Id;
        Name = other.Name;
        ParameterId = other.ParameterId;
        Unit = other.Unit;
        UnsupportedReason = other.UnsupportedReason;
        Description = other.Description;
        ParameterTypeId = other.ParameterTypeId;
        ParameterType = other.ParameterType;
        Value = other.Value;
        DefaultValue = other.DefaultValue;
        IsVisible = other.IsVisible;
        IsEnabled = other.IsEnabled;
        IsStaticallyVisible = other.IsStaticallyVisible;
        IsStaticallyEnabled = other.IsStaticallyEnabled;
        Row = other.Row;
        Column = other.Column;
        foreach (var child in other.Children) Children.Add(new ParameterInstance(child));
    }

    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Static Parameter id when this row represents a ParameterRef.</summary>
    public string? ParameterId { get; set; }
    public string? Unit { get; set; }
    public string? UnsupportedReason { get; set; }
    public string? Description { get; set; }
    public string? ParameterTypeId { get; set; }
    public ParameterType? ParameterType { get; set; }
    /// <summary>Effective value after the initial dynamic evaluation (assignments applied).</summary>
    public string? Value { get; set; }
    /// <summary>Static declaration as written in the XML, unaffected by dynamic assignments.</summary>
    public string? DefaultValue { get; set; }
    /// <summary>Effective visibility: static declaration combined with the dynamic evaluation.</summary>
    public bool IsVisible { get; set; } = true;
    /// <summary>Effective editability: static declaration combined with the dynamic evaluation.</summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>Static visibility, before dynamic evaluation; lets a caller re-apply an evaluation.</summary>
    public bool IsStaticallyVisible { get; set; } = true;
    /// <summary>Static editability, before dynamic evaluation; lets a caller re-apply an evaluation.</summary>
    public bool IsStaticallyEnabled { get; set; } = true;
    public int Row { get; set; }
    public int Column { get; set; }
    public List<ParameterInstance> Children { get; } = [];
}

/// <summary>Parameter-Referenz (KSXML: ParameterRef).</summary>
public sealed class ParameterReference
{
    public required string Id { get; init; }
    public required string ParameterInstanceId { get; init; }
    public string? ParameterTypeId { get; set; }
    public string? Text { get; set; }
    public string? Value { get; set; }
    public string? InitialValue { get; set; }
}

/// <summary>ComObject (KSXML: ComObject).</summary>
public sealed class ComObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; set; }
    public int Number { get; set; }
    public int SizeInBit { get; set; }
    public string? DatapointType { get; set; }
    public bool IsVisible { get; set; } = true;
}

/// <summary>ComObject-Instanz-Referenz (KSXML: ComObjectInstanceRef, knxproj: ComObjectInstanceRef).</summary>
public sealed class ComObjectInstanceRef
{
    public required string Id { get; init; }
    public required string ComObjectId { get; init; }
    public string? Text { get; set; }
    public int Number { get; set; }
    public string? FunctionText { get; set; }
    /// <summary>Send/receive flag as stored in the knxproj (ComObjectInstanceRef Device=\...\...).</summary>
    public string? Device { get; set; }
    /// <summary>Referenced group address ids ("GA-1 GA-2"), knxproj attribute Links.</summary>
    public List<string> Links { get; } = [];
}

/// <summary>Parameter-Separator (KSXML: ParameterSeparator).</summary>
public sealed class ParameterSeparator
{
    public required string Id { get; init; }
    public string? Name { get; set; }
    public string? Style { get; set; }
    public int Level { get; set; }
}
