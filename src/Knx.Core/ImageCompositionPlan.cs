namespace Knx;

public enum StaticCodeMaskPolicy { Unspecified, SourceDefinedBits, NoStaticSourceBits }
public sealed record ImageCompositionRequest(StaticCodeMaskPolicy MaskPolicy);
public enum ImageCompositionIssueCode
{
    InvalidRequest, ApplicationUnavailable, UnsupportedApplication, InvalidDeclaration,
    UnsupportedArena, NoCodeSources, ConflictingSourceBits, LimitExceeded, InputsChanged,
}
public sealed record ImageCompositionIssue(ImageCompositionIssueCode Code, string? SourceId = null);
public enum ImageCompositionRequirementCode
{
    UnspecifiedBitsNeedExternalFacts, ParameterWritersNotApplied, CommunicationTablesNotApplied,
    PropertySourcesNotComposed, MaskMemorySourcesNotComposed, ConfigurationAndFixupsNotApplied,
    PhysicalMappingUnresolved, ProcedureFinalizationUnresolved, FactoryOptimizationNotApplied,
    DeviceStateNotVerified, SecureAndExternalStateUnresolved,
}
public sealed record ImageCompositionRequirement(ImageCompositionRequirementCode Code, string? SourceId = null, string? ChunkKey = null);

/// <summary>Literal root code sources. Missing source bytes remain distinct from allocated working bytes.</summary>
public sealed class ImageCompositionSourceSegment
{
    public string SourceId { get; }
    public ResourcePatchArena Arena { get; }
    public uint ByteOffsetInArena { get; }
    public uint DeclaredSize { get; }
    public ImmutablePatchBytes? RawData { get; }
    public ImmutablePatchBytes? RawMask { get; }
    public int AllocationLength { get; }
    internal ImageCompositionSourceSegment(string id, ResourcePatchArena arena, uint offset, uint size, byte[]? data, byte[]? mask)
    {
        SourceId = id; Arena = arena; ByteOffsetInArena = offset; DeclaredSize = size;
        RawData = data is null ? null : new(data); RawMask = mask is null ? null : new(mask);
        AllocationLength = checked((int)Math.Max(size, (uint)(data?.Length ?? 0)));
    }
}

/// <summary>Logical source contributions, not a final write mask or verified current device data.</summary>
public sealed class ImageCompositionChunk
{
    public string Key { get; }
    public ResourcePatchArena Arena { get; }
    public uint ByteOffsetInArena { get; }
    public int Length => DesiredData.Length;
    public ImmutablePatchBytes DesiredData { get; }
    public ImmutablePatchBytes DesiredMask { get; }
    public IReadOnlyList<string> SourceIds { get; }
    internal ImageCompositionChunk(ResourcePatchArena arena, uint offset, byte[] data, byte[] mask, string[] sources)
    {
        Arena = arena; ByteOffsetInArena = offset; DesiredData = new(data); DesiredMask = new(mask);
        SourceIds = Array.AsReadOnly((string[])sources.Clone());
        Key = ParameterPatchSourceBinding.Hash(new { arena, offset, length = data.Length, sources });
    }
}

/// <summary>Exact missing bits are the inverse of the referenced chunk mask slice; no read action is inferred.</summary>
public sealed record ImageUnspecifiedSpan(string ChunkKey, ResourcePatchArena Arena, uint ByteOffsetInArena, int Length, ulong UnspecifiedBitCount);

public sealed class ImageCompositionPlan
{
    public string Scope => "RootStaticCodeWorkingSet";
    public int ProfileVersion => 1;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public bool ExecutionEligible => false;
    public ArchivedImageSourceManifest Sources { get; }
    public ImageCompositionBinding? Binding { get; }
    public IReadOnlyList<ImageCompositionIssue> Issues { get; }
    public IReadOnlyList<ImageCompositionSourceSegment> SourceSegments { get; }
    public IReadOnlyList<ImageCompositionChunk> Chunks { get; }
    public IReadOnlyList<ImageUnspecifiedSpan> UnspecifiedSpans { get; }
    public IReadOnlyList<ImageCompositionRequirement> UnresolvedRequirements { get; }
    public ulong DesiredBitCount { get; }
    public ulong UnspecifiedBitCount { get; }
    internal ImageCompositionPlan(ArchivedImageSourceManifest sources, ImageCompositionIssue issue)
    {
        Sources = sources; Issues = Array.AsReadOnly(new[] { issue });
        SourceSegments = Array.AsReadOnly(Array.Empty<ImageCompositionSourceSegment>());
        Chunks = Array.AsReadOnly(Array.Empty<ImageCompositionChunk>());
        UnspecifiedSpans = Array.AsReadOnly(Array.Empty<ImageUnspecifiedSpan>());
        UnresolvedRequirements = Array.AsReadOnly(Array.Empty<ImageCompositionRequirement>());
    }
    internal ImageCompositionPlan(ArchivedImageSourceManifest sources, ImageCompositionBinding binding,
        ImageCompositionSourceSegment[] segments, ImageCompositionChunk[] chunks, ImageUnspecifiedSpan[] spans,
        ImageCompositionRequirement[] requirements, ulong desiredBits, ulong unspecifiedBits)
    {
        Sources = sources; Binding = binding; Issues = Array.AsReadOnly(Array.Empty<ImageCompositionIssue>());
        SourceSegments = Array.AsReadOnly((ImageCompositionSourceSegment[])segments.Clone());
        Chunks = Array.AsReadOnly((ImageCompositionChunk[])chunks.Clone());
        UnspecifiedSpans = Array.AsReadOnly((ImageUnspecifiedSpan[])spans.Clone());
        UnresolvedRequirements = Array.AsReadOnly((ImageCompositionRequirement[])requirements.Clone());
        DesiredBitCount = desiredBits; UnspecifiedBitCount = unspecifiedBits;
    }
}

/// <summary>Source-only replay for the explicit contribution policy; never an apply token.</summary>
public sealed class ImageCompositionBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly ArchivedImageSourceBinding _sources;
    private readonly ImageCompositionRequest _request;
    public int ProfileVersion => 1;
    public string ProjectId { get; }
    public string? ProjectGuid { get; }
    public string DeviceId { get; }
    public long ApplicationDataRevision { get; }
    public string ApplicationId { get; }
    public string DefinitionHash { get; }
    public string SourceFingerprint { get; }
    public StaticCodeMaskPolicy MaskPolicy => _request.MaskPolicy;
    public string SourceSegmentsHash { get; }
    public string ChunksHash { get; }
    public string UnspecifiedSpansHash { get; }
    public string RequirementsHash { get; }
    public string Fingerprint { get; }
    internal ImageCompositionBinding(Project project, DeviceInstance device, ArchivedImageSourceManifest sources,
        ImageCompositionRequest request, ImageCompositionSourceSegment[] segments, ImageCompositionChunk[] chunks,
        ImageUnspecifiedSpan[] spans, ImageCompositionRequirement[] requirements)
    {
        _project = project; _device = device; _sources = sources.Binding!; _request = request with { };
        ProjectId = project.Id; ProjectGuid = project.Guid; DeviceId = device.Id; ApplicationDataRevision = project.ApplicationDataRevision;
        ApplicationId = sources.ApplicationId!; DefinitionHash = sources.ApplicationDefinitionHash!; SourceFingerprint = _sources.Fingerprint;
        SourceSegmentsHash = ParameterPatchSourceBinding.Hash(segments.Select(s => new
        { s.SourceId, s.Arena, s.ByteOffsetInArena, s.DeclaredSize, s.AllocationLength, data = s.RawData?.Sha256, mask = s.RawMask?.Sha256 }).ToArray());
        ChunksHash = ParameterPatchSourceBinding.Hash(chunks.Select(c => new
        { c.Key, c.Arena, c.ByteOffsetInArena, c.Length, c.SourceIds, data = c.DesiredData.Sha256, mask = c.DesiredMask.Sha256 }).ToArray());
        UnspecifiedSpansHash = ParameterPatchSourceBinding.Hash(spans); RequirementsHash = ParameterPatchSourceBinding.Hash(requirements);
        Fingerprint = ParameterPatchSourceBinding.Hash(new { ProfileVersion, ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision,
            ApplicationId, DefinitionHash, SourceFingerprint, MaskPolicy, SourceSegmentsHash, ChunksHash, UnspecifiedSpansHash, RequirementsHash });
    }
    public bool IsCurrent(Project project, DeviceInstance device) => ReferenceEquals(project, _project) && ReferenceEquals(device, _device)
        && OfflineImageComposition.IdentityWithinBudget(project, device) && _sources.MatchesIdentity(project, device)
        && OfflineImageComposition.Preview(project, device, _request).Binding?.Fingerprint == Fingerprint;
}
