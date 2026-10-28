namespace Knx;

public enum PropertySourceKind { SystemProperty, AppProperty, ByType }
public enum PropertySourceStatus { Inspected, ApplicationUnavailable, UnsupportedApplication, FactoryUnavailable, InvalidDeclaration, AmbiguousLayout, LimitExceeded }
public enum PropertyPdtStatus { KnownSymmetric, KnownAsymmetric, Unknown, Ambiguous }
public enum PropertyCountStatus { Missing, MaskMissing, MaskPartial, FullyValid }

/// <summary>Raw source selectors retain their widths. A type/occurrence does not imply an object index.</summary>
public sealed record PropertySourceSelector(PropertySourceKind Kind, ushort PropertyId, ushort? NativeObjectIndex = null,
    ushort? ApplicationObjectIndex = null, ushort? ObjectType = null, ushort? Occurrence = null);

/// <summary>Role-specific element widths, never capacity, scalar bit width or supplied initial bytes.</summary>
public sealed record PropertyPdtLayout(string SelectionFingerprint, string? DeclaredPdt, uint? ReadElementBytes,
    uint? WriteElementBytes, PropertyPdtStatus Status)
{
    public int ProfileVersion => 1;
}

public sealed record PropertySourceFragment(int SourceOrdinal, PropertySourceSelector RawSelector,
    FactoryImageComponent Component, uint? ByteOffset, ImmutablePatchBytes Data, ImmutablePatchBytes? Mask);

/// <summary>Supplied property fragments and count word, without a current-device or capacity claim.</summary>
public sealed class PropertySourceEntry
{
    public string PropertyKey { get; }
    public PropertySourceSelector Selector { get; }
    public bool SelectorIsCanonical { get; }
    public IReadOnlyList<PropertySourceFragment> DataFragments { get; }
    public PropertySourceFragment? CountFragment { get; }
    public ushort? EncodedCount { get; }
    public PropertyCountStatus CountStatus { get; }
    public PropertyPdtLayout PdtLayout { get; }
    public uint? CapacityElements => null;
    internal PropertySourceEntry(string key, PropertySourceSelector selector, bool canonical, PropertySourceFragment[] data,
        PropertySourceFragment? count, ushort? encoded, PropertyCountStatus countStatus, PropertyPdtLayout layout)
    {
        PropertyKey = key; Selector = selector; SelectorIsCanonical = canonical;
        DataFragments = Array.AsReadOnly((PropertySourceFragment[])data.Clone()); CountFragment = count;
        EncodedCount = encoded; CountStatus = countStatus; PdtLayout = layout;
    }
}

/// <summary>Independent source inspection. Missing interpretation never manufactures an initial buffer.</summary>
public sealed class ArchivedPropertySourceIndex
{
    public int ProfileVersion => 1;
    public string Scope => "ArchivedPropertySources";
    public PropertySourceStatus Status { get; }
    public ArchivedFactoryImageStatus? FactoryStatus { get; }
    public string? FactoryBlobHash { get; }
    public string? SourceFingerprint { get; }
    public MaskProfileSelection PropertyProfileSelection { get; }
    public int PdtRoleProfileVersion => 1;
    public string? Fingerprint { get; }
    public PropertySourceBinding? Binding { get; }
    public IReadOnlyList<PropertySourceEntry> Entries { get; }
    internal ArchivedPropertySourceIndex(PropertySourceStatus status, ArchivedImageSourceManifest sources,
        MaskProfileSelection selection, PropertySourceEntry[]? entries = null, string? fingerprint = null,
        PropertySourceBinding? binding = null)
    {
        Status = status; FactoryStatus = sources.Hardware?.FactoryImage.Status; FactoryBlobHash = sources.Hardware?.FactoryImage.BlobHash;
        SourceFingerprint = sources.Binding?.Fingerprint; PropertyProfileSelection = selection; Fingerprint = fingerprint; Binding = binding;
        Entries = Array.AsReadOnly(entries is null ? [] : (PropertySourceEntry[])entries.Clone());
    }
}

/// <summary>Source-only replay; saved parameter values and communication-object edits are not source inputs.</summary>
public sealed class PropertySourceBinding
{
    private readonly ArchivedImageSourceBinding _sources;
    public int ProfileVersion => 1;
    public string Fingerprint { get; }
    public string SourceFingerprint => _sources.Fingerprint;
    public string PropertyProfileFingerprint { get; }
    internal PropertySourceBinding(ArchivedImageSourceBinding sources, string profile, string fingerprint)
    { _sources = sources; PropertyProfileFingerprint = profile; Fingerprint = fingerprint; }
    public bool IsCurrent(Project project, DeviceInstance device) => _sources.MatchesIdentity(project, device)
        && ArchivedPropertySources.Read(project, device).Fingerprint == Fingerprint;
}

internal static class PropertyPdtRoles
{
    internal static PropertyPdtLayout Read(ParameterPropertyProfile profile)
    {
        var pdt = profile.DeclaredPdt; uint? read = null, write = null;
        if (profile.TypeStatus == ParameterPropertyTypeStatus.Ambiguous)
            return new(profile.Selection.Fingerprint, pdt, null, null, PropertyPdtStatus.Ambiguous);
        if (pdt == "PDT_CONTROL") { read = 1; write = 10; }
        else
        {
            read = pdt switch
            {
                "PDT_CHAR" or "PDT_UNSIGNED_CHAR" or "PDT_BINARY_INFORMATION" or "PDT_BITSET8" or "PDT_ENUM8" or "PDT_SCALING" => 1,
                "PDT_INT" or "PDT_UNSIGNED_INT" or "PDT_KNX_FLOAT" or "PDT_VERSION" or "PDT_BITSET16" => 2,
                "PDT_DATE" or "PDT_TIME" or "PDT_POLL_GROUP_SETTINGS" => 3,
                "PDT_LONG" or "PDT_UNSIGNED_LONG" or "PDT_FLOAT" => 4,
                "PDT_SHORT_CHAR_BLOCK" => 5, "PDT_ALARM_INFO" => 6,
                "PDT_DOUBLE" or "PDT_DATE_TIME" => 8, "PDT_CHAR_BLOCK" => 10, _ => null,
            };
            if (pdt is { Length: 14 } && pdt.StartsWith("PDT_GENERIC_", StringComparison.Ordinal)
                && int.TryParse(pdt.AsSpan(12), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var width)
                && width is >= 1 and <= 20 && pdt == "PDT_GENERIC_" + width.ToString("00", System.Globalization.CultureInfo.InvariantCulture)) read = (uint)width;
            write = read;
        }
        return new(profile.Selection.Fingerprint, pdt, read, write, read is null ? PropertyPdtStatus.Unknown
            : read == write ? PropertyPdtStatus.KnownSymmetric : PropertyPdtStatus.KnownAsymmetric);
    }
}
