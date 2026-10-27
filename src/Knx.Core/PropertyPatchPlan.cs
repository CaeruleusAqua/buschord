namespace Knx;

public enum PropertyPatchIssueCode
{
    InvalidRequest, ApplicationUnavailable, UnsupportedApplication, InvalidDeclaration, UnknownParameter,
    UnsupportedParameter, UnsupportedDependency, UnsupportedLayout, InvalidValue, UnknownInitialData,
    InvalidInitialMask, UnknownElementCount, UnknownPdt, AsymmetricPdt, IncompleteInitialData,
    AmbiguousOwnership, UnsupportedPhysicalAlias, ResourceLimitExceeded, InputsChanged,
}
public sealed record PropertyPatchIssue(PropertyPatchIssueCode Code, string? RefId = null, string? PropertyKey = null);
public enum PropertyPatchRequirementCode
{
    CompleteParameterActivation, PropertyCapacityUnknown, PhysicalResourceMapping, GeneratedTables,
    LoadProcedure, Finalization, SecureConfiguration, DeviceStateNotVerified,
}
public sealed record PropertyPatchRequirement(PropertyPatchRequirementCode Code, string? PropertyKey = null);
public sealed record PropertyPatchField(string RefId, string ParameterId, string PropertyKey, string RawValue,
    PropertySourceSelector Selector, uint ByteOffsetInPropertyData, byte BitOffset, uint BitLength);
public sealed record PropertyPatchRange(string PropertyKey, PropertySourceSelector Selector, uint ByteOffsetInPropertyData,
    ImmutablePatchBytes BeforeData, ImmutablePatchBytes AfterData, ImmutablePatchBytes FieldMask,
    ImmutablePatchBytes BeforeMask, ImmutablePatchBytes AfterMask)
{
    public int Length => BeforeData.Length;
}

/// <summary>Complete supplied buffers of one admitted property. Count bytes are preserved, never patched.</summary>
public sealed class PropertyPatchImage
{
    public string PropertyKey { get; }
    public PropertySourceSelector Selector { get; }
    public PropertyPdtLayout PdtLayout { get; }
    public ImmutablePatchBytes BeforeData { get; }
    public ImmutablePatchBytes AfterData { get; }
    public ImmutablePatchBytes BeforeMask { get; }
    public ImmutablePatchBytes AfterMask { get; }
    public ImmutablePatchBytes BeforeCountData { get; }
    public ImmutablePatchBytes AfterCountData => BeforeCountData;
    public ImmutablePatchBytes CountMask { get; }
    public ushort EncodedElementCount { get; }
    public bool CountUnchanged => true;
    public int PayloadLength => BeforeData.Length;
    internal PropertyPatchImage(PropertySourceEntry entry, byte[] data, byte[] mask)
    {
        PropertyKey = entry.PropertyKey; Selector = entry.Selector; PdtLayout = entry.PdtLayout;
        BeforeData = entry.DataFragments[0].Data; BeforeMask = entry.DataFragments[0].Mask!;
        AfterData = new(data); AfterMask = new(mask); BeforeCountData = entry.CountFragment!.Data;
        CountMask = entry.CountFragment.Mask!; EncodedElementCount = entry.EncodedCount!.Value;
    }
}

/// <summary>A new property-only subset contract. No transport, complete image or execution authority.</summary>
public sealed class PropertyPatchPlan
{
    public string Scope => "ExplicitPropertySubsetPreview";
    public int ProfileVersion => 1;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public bool ExecutionEligible => false;
    public ArchivedPropertySourceIndex SourceIndex { get; }
    public PropertyPatchSourceBinding? Binding { get; }
    public IReadOnlyList<PropertyPatchIssue> Issues { get; }
    public IReadOnlyList<PropertyPatchField> Fields { get; }
    public IReadOnlyList<PropertyPatchRange> Ranges { get; }
    public IReadOnlyList<PropertyPatchImage> Images { get; }
    public IReadOnlyList<PropertyPatchRequirement> UnresolvedRequirements { get; }
    internal PropertyPatchPlan(ArchivedPropertySourceIndex source, PropertyPatchIssue issue)
    {
        SourceIndex = source; Issues = Array.AsReadOnly(new[] { issue }); Fields = Array.AsReadOnly(Array.Empty<PropertyPatchField>());
        Ranges = Array.AsReadOnly(Array.Empty<PropertyPatchRange>()); Images = Array.AsReadOnly(Array.Empty<PropertyPatchImage>());
        UnresolvedRequirements = Array.AsReadOnly(Array.Empty<PropertyPatchRequirement>());
    }
    internal PropertyPatchPlan(ArchivedPropertySourceIndex source, PropertyPatchSourceBinding binding,
        PropertyPatchField[] fields, PropertyPatchRange[] ranges, PropertyPatchImage[] images)
    {
        SourceIndex = source; Binding = binding; Issues = Array.AsReadOnly(Array.Empty<PropertyPatchIssue>());
        Fields = Array.AsReadOnly((PropertyPatchField[])fields.Clone()); Ranges = Array.AsReadOnly((PropertyPatchRange[])ranges.Clone());
        Images = Array.AsReadOnly((PropertyPatchImage[])images.Clone());
        UnresolvedRequirements = Array.AsReadOnly(Enum.GetValues<PropertyPatchRequirementCode>().Select(c => new PropertyPatchRequirement(c)).ToArray());
    }
}

/// <summary>Strict value/subset/source replay, separate from the source-only index binding.</summary>
public sealed class PropertyPatchSourceBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly ResourcePatchIdentity _identity;
    private readonly ParameterPatchValue[] _subset;
    public int ProfileVersion => 1;
    public string NumericParseProfile => "ExactInteger";
    public string ProjectId => _identity.ProjectId;
    public string? ProjectGuid => _identity.ProjectGuid;
    public string DeviceId => _identity.DeviceId;
    public long ApplicationDataRevision => _identity.Revision;
    public string ApplicationId { get; }
    public string DefinitionHash { get; }
    public string SavedValuesHash => _identity.ValuesHash;
    public string SourceFingerprint { get; }
    public string SourceIndexFingerprint { get; }
    public string PropertyProfileFingerprint { get; }
    public int PdtRoleProfileVersion => 1;
    public string SubsetHash { get; }
    public ParameterPatchByteOrder ByteOrder { get; }
    public string FieldsHash { get; }
    public string PropertyImagesHash { get; }
    public string Fingerprint { get; }
    internal PropertyPatchSourceBinding(Project project, DeviceInstance device, ResourcePatchIdentity identity,
        ArchivedImageSources.Inputs inputs, ArchivedPropertySourceIndex source, ParameterPatchValue[] subset,
        ParameterPatchByteOrder order, PropertyPatchField[] fields, PropertyPatchImage[] images)
    {
        _project = project; _device = device; _identity = identity; _subset = subset.Select(p => p with { }).ToArray();
        ApplicationId = inputs.Manifest.ApplicationId!; DefinitionHash = inputs.Manifest.ApplicationDefinitionHash!;
        SourceFingerprint = source.SourceFingerprint!; SourceIndexFingerprint = source.Fingerprint!;
        PropertyProfileFingerprint = source.PropertyProfileSelection.Fingerprint; ByteOrder = order;
        SubsetHash = ParameterPatchSourceBinding.Hash(_subset.OrderBy(p => p.RefId, StringComparer.Ordinal).ToArray());
        FieldsHash = ParameterPatchSourceBinding.Hash(fields.OrderBy(f => f.RefId, StringComparer.Ordinal).ToArray());
        PropertyImagesHash = ParameterPatchSourceBinding.Hash(images.OrderBy(i => i.PropertyKey, StringComparer.Ordinal).Select(i => new
        {
            i.PropertyKey, i.Selector, i.PdtLayout, before = i.BeforeData.Sha256, after = i.AfterData.Sha256,
            beforeMask = i.BeforeMask.Sha256, afterMask = i.AfterMask.Sha256, count = i.BeforeCountData.Sha256, countMask = i.CountMask.Sha256,
        }).ToArray());
        Fingerprint = ParameterPatchSourceBinding.Hash(new { ProfileVersion, NumericParseProfile, ProjectId, ProjectGuid, DeviceId,
            ApplicationDataRevision, ApplicationId, DefinitionHash, SavedValuesHash, SourceFingerprint, SourceIndexFingerprint,
            PropertyProfileFingerprint, PdtRoleProfileVersion, SubsetHash, ByteOrder, FieldsHash, PropertyImagesHash });
    }
    internal bool MatchesIdentity(Project project, DeviceInstance device) => ReferenceEquals(project, _project) && ReferenceEquals(device, _device)
        && ArchivedPropertySources.IdentityWithinBudget(project, device) && ParameterPatchSourceBinding.ValuesWithinBudget(device)
        && ResourcePatchIdentity.Capture(project, device) == _identity;
    public bool IsCurrent(Project project, DeviceInstance device) => MatchesIdentity(project, device)
        && OfflinePropertyPatch.Preview(project, device, _subset, ByteOrder).Binding?.Fingerprint == Fingerprint;
}
