namespace Knx;

public enum ResourcePatchArenaKind { AbsoluteMemory, RelativeMemory }

/// <summary>Logical source arena. An LSM offset is not a physical memory address.</summary>
public sealed record ResourcePatchArena(ResourcePatchArenaKind Kind, byte? LoadStateMachine = null);
public enum ResourcePatchRequirementCode
{
    CompleteParameterActivation, GeneratedTables, LoadProcedure, PhysicalResourceMapping,
    PropertyInitialization, MissingInitialResources, SecureConfiguration, PayloadShorterThanDeclaredSize, UnknownDeclaredSize,
    AdditionalRequirementsOmitted,
}
public sealed record ResourcePatchRequirement(ResourcePatchRequirementCode Code, string? ResourceId = null, string? RefId = null);

/// <summary>ByteOffset includes the resource's BaseOffset, in its logical arena.</summary>
public sealed record ResourcePatchField(string RefId, string ParameterId, string ResourceId, string RawValue,
    ResourcePatchArena Arena, uint ByteOffset, byte BitOffset, uint BitLength);
/// <summary>ByteOffset is arena-relative, never an inferred physical address for an LSM.</summary>
public sealed record ResourcePatchRange(string ResourceId, ResourcePatchArena Arena, uint ByteOffset,
    ImmutablePatchBytes BeforeData, ImmutablePatchBytes AfterData, ImmutablePatchBytes FieldMask,
    ImmutablePatchBytes? BeforeMask, ImmutablePatchBytes? AfterMask)
{
    public int Length => BeforeData.Length;
}

public enum ResourcePayloadCoverage { Unknown, MatchesDeclaredSize, ShorterThanDeclaredSize, LongerThanDeclaredSize }

/// <summary>All supplied before/after bytes of one selected resource; no padding or complete-device claim.</summary>
public sealed class ResourcePatchImage
{
    public string Id { get; }
    public ResourcePatchArena Arena { get; }
    public uint BaseOffset { get; }
    public uint? DeclaredSize { get; }
    public ImmutablePatchBytes BeforeData { get; }
    public ImmutablePatchBytes AfterData { get; }
    public ImmutablePatchBytes? BeforeMask { get; }
    public ImmutablePatchBytes? AfterMask { get; }
    public int PayloadLength => BeforeData.Length;
    public ResourcePayloadCoverage DeclaredSizeCoverage => DeclaredSize is not { } size ? ResourcePayloadCoverage.Unknown
        : PayloadLength == size ? ResourcePayloadCoverage.MatchesDeclaredSize
        : PayloadLength < size ? ResourcePayloadCoverage.ShorterThanDeclaredSize : ResourcePayloadCoverage.LongerThanDeclaredSize;

    internal ResourcePatchImage(InitialMemoryResource resource, ResourcePatchArena arena, IEnumerable<ParameterPatchRange> ranges)
    {
        Id = resource.Id; Arena = arena; BaseOffset = resource.StartAddress; DeclaredSize = resource.DeclaredSize;
        BeforeData = resource.Data; BeforeMask = resource.Mask;
        var data = BeforeData.ToArray(); var mask = BeforeMask?.ToArray();
        foreach (var range in ranges)
        {
            var index = checked((int)(range.StartAddress - BaseOffset));
            range.AfterData.ToArray().CopyTo(data, index);
            if (mask is not null) range.AfterMask!.ToArray().CopyTo(mask, index);
        }
        AfterData = new(data); AfterMask = mask is null ? null : new(mask);
    }
}

/// <summary>A separate versioned source binding; never an apply token or activation proof.</summary>
public sealed class ResourcePatchSourceBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly string? _product, _hardware;
    private readonly ushort _address;
    private readonly ParameterPatchValue[] _subset;
    public int ProfileVersion => 1;
    public string ProjectId { get; }
    public string? ProjectGuid { get; }
    public string DeviceId { get; }
    public long ApplicationDataRevision { get; }
    public string ApplicationId { get; }
    public string DefinitionHash { get; }
    public string SavedValuesHash { get; }
    public string SubsetHash { get; }
    public ParameterPatchByteOrder ByteOrder { get; }
    public int CodecProfileVersion { get; }
    public string NumericParseProfile { get; }
    public string? PackedTimeEncodingProfile { get; }
    public string? DenseTimeEncodingProfile { get; }
    public int? ModuleScopeProfileVersion { get; }
    public string? ModuleScopeWitnessHash { get; }
    public string ResourceHash { get; }
    public string FieldsHash { get; }
    public string Fingerprint { get; }

    internal ResourcePatchSourceBinding(Project project, DeviceInstance device, ResourcePatchIdentity identity, string application, string definitionHash,
        ParameterPatchValue[] subset, ParameterPatchByteOrder order, IReadOnlyList<ResourcePatchImage> resources, IReadOnlyList<ResourcePatchField> fields,
        bool single, bool packed, bool dense, string? moduleWitness, int? moduleProfile)
    {
        _project = project; _device = device; _product = identity.Product; _hardware = identity.Hardware; _address = identity.Address;
        ProjectId = identity.ProjectId; ProjectGuid = identity.ProjectGuid; DeviceId = identity.DeviceId; ApplicationDataRevision = identity.Revision;
        ApplicationId = application; DefinitionHash = definitionHash; SavedValuesHash = identity.ValuesHash;
        _subset = subset.Select(v => new ParameterPatchValue(v.RefId, v.RawValue)).ToArray();
        SubsetHash = ParameterPatchSourceBinding.Hash(subset.OrderBy(v => v.RefId, StringComparer.Ordinal).ToArray()); ByteOrder = order;
        CodecProfileVersion = dense ? 4 : 3; NumericParseProfile = single ? "ExactIntegerAndDotNet10XmlDoubleToSingle" : "ExactInteger";
        PackedTimeEncodingProfile = packed ? "PackedDaysHoursMinutesAndSeconds24BigEndianOnly" : null;
        DenseTimeEncodingProfile = dense ? "SignedRawOrdinaryTime8To32" : null;
        ModuleScopeWitnessHash = moduleWitness; ModuleScopeProfileVersion = moduleWitness is null ? null : moduleProfile;
        ResourceHash = ParameterPatchSourceBinding.Hash(resources.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new
        {
            r.Id, r.Arena, r.BaseOffset, r.DeclaredSize, data = r.BeforeData.Sha256, mask = r.BeforeMask?.Sha256,
        }).ToArray());
        FieldsHash = ParameterPatchSourceBinding.Hash(fields.OrderBy(f => f.RefId, StringComparer.Ordinal).Select(f => new
        { f.RefId, f.ParameterId, f.ResourceId, f.Arena, f.ByteOffset, f.BitOffset, f.BitLength }).ToArray());
        Fingerprint = ParameterPatchSourceBinding.Hash(new
        {
            ProfileVersion, CodecProfileVersion, NumericParseProfile, PackedTimeEncodingProfile, DenseTimeEncodingProfile,
            ModuleScopeProfileVersion, ModuleScopeWitnessHash, ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision,
            ApplicationId, DefinitionHash, SavedValuesHash, SubsetHash, ByteOrder, ResourceHash, FieldsHash,
        });
    }

    /// <summary>Revalidates the same explicit subset without executing scripts or mutating the project.</summary>
    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!MatchesIdentity(project, device)) return false;
        var fresh = OfflineResourcePatch.Preview(project, device, _subset, ByteOrder);
        return fresh.IsAvailable && fresh.Binding!.Fingerprint == Fingerprint;
    }

    internal bool MatchesIdentity(Project project, DeviceInstance device) => ReferenceEquals(project, _project) && ReferenceEquals(device, _device)
        && project.Id == ProjectId && project.Guid == ProjectGuid && device.Id == DeviceId
        && project.ApplicationDataRevision == ApplicationDataRevision && device.Address.Raw == _address
        && device.ProductRefId == _product && device.Hardware2ProgramRefId == _hardware
        && ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device)) && ParameterPatchSourceBinding.ValuesWithinBudget(device)
        && ParameterPatchSourceBinding.ValuesHash(device) == SavedValuesHash;
}

internal sealed record ResourcePatchIdentity(string ProjectId, string? ProjectGuid, string DeviceId, long Revision,
    string? Product, string? Hardware, ushort Address, string ValuesHash)
{
    internal static ResourcePatchIdentity Capture(Project project, DeviceInstance device) => new(project.Id, project.Guid, device.Id,
        project.ApplicationDataRevision, device.ProductRefId, device.Hardware2ProgramRefId, device.Address.Raw, ParameterPatchSourceBinding.ValuesHash(device));
}

/// <summary>An atomic explicit resource subset, with full buffers and compact changed ranges.</summary>
public sealed class OfflineResourcePlan
{
    public string Scope => "ExplicitResourceSubsetPreview";
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public ResourcePatchSourceBinding? Binding { get; }
    public IReadOnlyList<ParameterPatchIssue> Issues { get; }
    public IReadOnlyList<ResourcePatchField> Fields { get; }
    public IReadOnlyList<ResourcePatchRange> Ranges { get; }
    public IReadOnlyList<ResourcePatchImage> Resources { get; }
    public IReadOnlyList<ResourcePatchRequirement> UnresolvedRequirements { get; }
    internal OfflineResourcePlan(ParameterPatchIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Fields = Array.AsReadOnly(Array.Empty<ResourcePatchField>());
        Ranges = Array.AsReadOnly(Array.Empty<ResourcePatchRange>()); Resources = Array.AsReadOnly(Array.Empty<ResourcePatchImage>());
        UnresolvedRequirements = Array.AsReadOnly(Array.Empty<ResourcePatchRequirement>());
    }
    internal OfflineResourcePlan(ResourcePatchSourceBinding binding, ResourcePatchField[] fields, ResourcePatchRange[] ranges,
        ResourcePatchImage[] resources, ResourcePatchRequirement[] requirements)
    {
        Binding = binding; Issues = Array.AsReadOnly(Array.Empty<ParameterPatchIssue>()); Fields = Array.AsReadOnly(fields);
        Ranges = Array.AsReadOnly(ranges); Resources = Array.AsReadOnly(resources); UnresolvedRequirements = Array.AsReadOnly(requirements);
    }
}
