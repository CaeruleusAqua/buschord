namespace Knx;

public sealed record RootImageBuildRequest(StaticCodeMaskPolicy CodeMaskPolicy, LoadProcedureRequest Procedure, GeneratedTableRequest? Tables = null);
public enum RootImageBuildIssueCode
{
    InvalidRequest, SourceUnavailable, UnsupportedApplication, InvalidDeclaration, UnsupportedProfile,
    UnsupportedType, InvalidValue, InvalidPolicy, AmbiguousOwnership, UnknownStorage, MissingResource,
    FieldOutOfRange, UnsupportedMapping, SourceOverlap, TableWorkingInputsMismatch, ParameterTableOverlap,
    LimitExceeded, InputsChanged, ActivationUnavailable, ProcedureUnavailable, TableUnavailable,
    ExternalConfigurationUnaccounted, UnaccountedBinaryData, UnsupportedNativeCarrier,
}
public sealed record RootImageBuildIssue(RootImageBuildIssueCode Code, string? RefId = null, string? ResourceId = null,
    ParameterPatchIssue? ParameterIssue = null, GeneratedTableIssue? TableIssue = null, LoadProcedureIssue? ProcedureIssue = null);
public enum RootImageBuildRequirementCode
{
    UnspecifiedBitsNeedExternalFacts, MaskMemorySourcesNotComposed, PreWriterFixupsNotApplied,
    PropertyAndOtherWritersNotApplied, ActualDeviceOptionsUnverified, PhysicalMappingUnresolved,
    ConfigurationAndSecureStateUnresolved, ProcedureAdjustmentsNotApplied, FactoryOptimizationNotApplied,
    ProcedureFinalizationUnresolved, DeviceStateNotVerified, CommunicationTablesNotApplied,
    UnusedTableTailMaskUnresolved,
}
public sealed record RootImageBuildRequirement(RootImageBuildRequirementCode Code, string? ResourceId = null);
public enum RootParameterDownloadBehavior { None, Background, DefaultValue }
public enum RootParameterMaskPolicy { WholeTouchedByte, FieldBitsOnly }
public enum RootParameterValueOrigin { Parameter, Reference, Saved }
public enum RootParameterDisposition { NoStorage, ActiveValue, AlwaysValue, InactiveDefault, InactiveNullNone, InactiveNullBackground }
public enum RootParameterMaskEffect { None, FieldBits, WholeBytes }

public sealed class RootImageBuildPolicy
{
    public ParameterPatchByteOrder ByteOrder { get; }
    public RootParameterDownloadBehavior DownloadBehavior { get; }
    public RootParameterMaskPolicy MaskPolicy { get; }
    public string CustomerPolicy => "IncludeAll";
    public string? MinEtsVersion { get; }
    public uint PackedMinEtsVersion { get; }
    public bool OptionsDeclared { get; }
    public string? DeclaredDownloadBehavior { get; }
    public string? DeclaredByteOrder { get; }
    public string Fingerprint { get; }
    internal RootImageBuildPolicy(ParameterPatchByteOrder order, RootParameterDownloadBehavior behavior, RootParameterMaskPolicy mask,
        string? version, uint packedVersion, bool options, string? declaredBehavior, string? declaredOrder)
    {
        ByteOrder = order; DownloadBehavior = behavior; MaskPolicy = mask; MinEtsVersion = version;
        PackedMinEtsVersion = packedVersion; OptionsDeclared = options; DeclaredDownloadBehavior = declaredBehavior; DeclaredByteOrder = declaredOrder;
        Fingerprint = ParameterPatchSourceBinding.Hash(new { version = 1, ByteOrder, DownloadBehavior, MaskPolicy, CustomerPolicy,
            MinEtsVersion, PackedMinEtsVersion, OptionsDeclared, DeclaredDownloadBehavior, DeclaredByteOrder });
    }
}

public sealed record RootParameterRecord(string RefId, string ParameterId, string ParameterTypeId, RootParameterDisposition Disposition,
    bool Active, int NormalReferenceCount, bool LegacyPatchAlways, bool CustomerAdjustable, RootParameterValueOrigin CurrentValueOrigin,
    string CurrentValueHash, string? RecordValueHash, bool NativeRecordEmitted, bool Allocates, bool WritesData,
    string? ResourceId = null, ResourcePatchArena? Arena = null, uint? ByteOffsetInArena = null,
    byte? BitOffset = null, uint? BitLength = null, uint? EncodedWord = null);

public sealed class RootParameterPopulation
{
    public IReadOnlyList<RootParameterRecord> Records { get; }
    public int DeclaredParameterCount { get; }
    public int ReferenceCount => Records.Count;
    public int OwnerCount { get; }
    public int PhysicalRecordCount => Records.Count(r => r.NativeRecordEmitted);
    public int NoStorageCount => Records.Count(r => r.Disposition == RootParameterDisposition.NoStorage);
    public int ActiveCount => Records.Count(r => r.Disposition == RootParameterDisposition.ActiveValue);
    public int AlwaysCount => Records.Count(r => r.Disposition == RootParameterDisposition.AlwaysValue);
    public int InactiveDefaultCount => Records.Count(r => r.Disposition == RootParameterDisposition.InactiveDefault);
    public int InactiveNullCount => Records.Count(r => r.Disposition is RootParameterDisposition.InactiveNullNone or RootParameterDisposition.InactiveNullBackground);
    public string PopulationHash { get; }
    public string ValuesHash { get; }
    public string GeometryHash { get; }
    internal RootParameterPopulation(RootParameterRecord[] records, int parameters, int owners, string populationHash, string valuesHash, string geometryHash)
    {
        Records = Array.AsReadOnly((RootParameterRecord[])records.Clone()); DeclaredParameterCount = parameters; OwnerCount = owners;
        PopulationHash = populationHash; ValuesHash = valuesHash; GeometryHash = geometryHash;
    }
}

public sealed record RootImageWriteClaim(string RefId, string ResourceId, ResourcePatchArena Arena, uint ByteOffsetInArena,
    byte BitOffset, uint BitLength, bool WritesData, RootParameterMaskEffect MaskEffect, uint? EncodedWord);

/// <summary>Working data and desired validity remain separate from archived data and current device state.</summary>
public sealed class RootImageChunk
{
    public string Key { get; }
    public ResourcePatchArena Arena { get; }
    public uint ByteOffsetInArena { get; }
    public int Length => WorkingData.Length;
    public ImmutablePatchBytes WorkingData { get; }
    public ImmutablePatchBytes DesiredValidity { get; }
    public ImmutablePatchBytes CanonicalDesired { get; }
    public IReadOnlyList<string> SourceIds { get; }
    internal RootImageChunk(ResourcePatchArena arena, uint offset, byte[] working, byte[] validity, string[] sources)
    {
        Arena = arena; ByteOffsetInArena = offset; WorkingData = new(working); DesiredValidity = new(validity);
        var canonical = new byte[working.Length];
        for (var i = 0; i < canonical.Length; i++) canonical[i] = (byte)(working[i] & validity[i]);
        CanonicalDesired = new(canonical); SourceIds = Array.AsReadOnly((string[])sources.Clone());
        Key = ParameterPatchSourceBinding.Hash(new { arena, offset, length = working.Length, sources });
    }
}

public sealed record RootImageTableContribution(GeneratedTableRole Role, ResourcePatchArena Arena, uint ByteOffsetInArena,
    int Length, string DataHash, string WriteMaskHash, string InputCompatibilityHash);
public sealed class RootImageTableSummary
{
    public GeneratedTableFormat Format { get; }
    public GeneratedTableSourceBinding Binding { get; }
    public string NativeWitnessHash { get; }
    public string CompatibilityHash { get; }
    public IReadOnlyList<RootImageTableContribution> Payloads { get; }
    public IReadOnlyList<GeneratedTableRequirement> UnresolvedRequirements { get; }
    internal RootImageTableSummary(GeneratedTablePreview tables, RootImageTableContribution[] contributions, string compatibility)
    {
        Format = tables.Format!.Value; Binding = tables.Binding!; NativeWitnessHash = tables.NativeWitnessHash!;
        CompatibilityHash = compatibility; Payloads = Array.AsReadOnly((RootImageTableContribution[])contributions.Clone());
        UnresolvedRequirements = Array.AsReadOnly(tables.Requirements.ToArray());
    }
}

public sealed class RootImageBuildPlan
{
    public string Scope => "RootFixedMemoryWorkingSet";
    public int ProfileVersion => 1;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public bool CompleteFixedRootPopulation => IsAvailable;
    public bool CompleteApplicationImage => false;
    public bool ExecutionEligible => false;
    public bool DownloadEligible => false;
    public ArchivedImageSourceManifest Sources { get; }
    public RootImageBuildBinding? Binding { get; }
    public RootImageBuildPolicy? Policy { get; }
    public RootParameterPopulation? Population { get; }
    public LoadProcedurePlan? Procedure { get; }
    public RootImageTableSummary? Tables { get; }
    public IReadOnlyList<RootImageBuildIssue> Issues { get; }
    public IReadOnlyList<ImageCompositionSourceSegment> SourceSegments { get; }
    public IReadOnlyList<RootImageWriteClaim> Claims { get; }
    public IReadOnlyList<RootImageChunk> Chunks { get; }
    public IReadOnlyList<ImageUnspecifiedSpan> UnspecifiedSpans { get; }
    public IReadOnlyList<RootImageBuildRequirement> UnresolvedRequirements { get; }
    public ulong DesiredBitCount { get; }
    public ulong UnspecifiedBitCount { get; }
    internal RootImageBuildPlan(ArchivedImageSourceManifest sources, RootImageBuildIssue issue)
    {
        Sources = sources; Issues = Array.AsReadOnly(new[] { issue });
        SourceSegments = Array.Empty<ImageCompositionSourceSegment>(); Claims = Array.Empty<RootImageWriteClaim>();
        Chunks = Array.Empty<RootImageChunk>(); UnspecifiedSpans = Array.Empty<ImageUnspecifiedSpan>(); UnresolvedRequirements = Array.Empty<RootImageBuildRequirement>();
    }
    internal RootImageBuildPlan(ArchivedImageSourceManifest sources, RootImageBuildBinding binding, RootImageBuildPolicy policy,
        RootParameterPopulation population, LoadProcedurePlan procedure, RootImageTableSummary? tables,
        ImageCompositionSourceSegment[] segments, RootImageWriteClaim[] claims, RootImageChunk[] chunks,
        ImageUnspecifiedSpan[] spans, RootImageBuildRequirement[] requirements, ulong desired, ulong unspecified)
    {
        Sources = sources; Binding = binding; Policy = policy; Population = population; Procedure = procedure; Tables = tables;
        Issues = Array.Empty<RootImageBuildIssue>(); SourceSegments = Array.AsReadOnly((ImageCompositionSourceSegment[])segments.Clone());
        Claims = Array.AsReadOnly((RootImageWriteClaim[])claims.Clone()); Chunks = Array.AsReadOnly((RootImageChunk[])chunks.Clone());
        UnspecifiedSpans = Array.AsReadOnly((ImageUnspecifiedSpan[])spans.Clone()); UnresolvedRequirements = Array.AsReadOnly((RootImageBuildRequirement[])requirements.Clone());
        DesiredBitCount = desired; UnspecifiedBitCount = unspecified;
    }
}

/// <summary>Fresh same-route replay, never apply authority. The request and public facts contain no interpreter.</summary>
public sealed class RootImageBuildBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly ArchivedImageSourceBinding _sources;
    private readonly RootImageBuildRequest _request;
    public int ProfileVersion => 1;
    public string ProjectId { get; }
    public string? ProjectGuid { get; }
    public string DeviceId { get; }
    public long ApplicationDataRevision { get; }
    public string ApplicationId { get; }
    public string SourceFingerprint { get; }
    public string CarrierHash { get; }
    public string ProjectInputHash { get; }
    public string ContextFingerprint { get; }
    public string PopulationHash { get; }
    public string ValuesHash { get; }
    public string PolicyHash { get; }
    public string GeometryHash { get; }
    public string RequirementsHash { get; }
    public string ClaimsHash { get; }
    public string WorkingHash { get; }
    public string ValidityHash { get; }
    public string CanonicalHash { get; }
    public string ProcedureWitnessHash { get; }
    public string? TableBindingHash { get; }
    public string? TableCompatibilityHash { get; }
    public string Fingerprint { get; }
    internal RootImageBuildBinding(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, RootImageBuildRequest request,
        string carrier, NativeApplicationEvaluationContext context, RootParameterPopulation population, RootImageBuildPolicy policy,
        RootImageWriteClaim[] claims, RootImageChunk[] chunks, ImageUnspecifiedSpan[] spans, LoadProcedurePlan procedure, RootImageTableSummary? tables,
        RootImageBuildRequirement[] requirements)
    {
        _project = project; _device = device; _sources = inputs.Manifest.Binding!; _request = Copy(request);
        ProjectId = project.Id; ProjectGuid = project.Guid; DeviceId = device.Id; ApplicationDataRevision = project.ApplicationDataRevision;
        ApplicationId = inputs.Manifest.ApplicationId!; SourceFingerprint = _sources.Fingerprint; CarrierHash = carrier;
        ProjectInputHash = context.ProjectInputHash!; ContextFingerprint = context.Fingerprint!; PopulationHash = population.PopulationHash;
        ValuesHash = population.ValuesHash; PolicyHash = policy.Fingerprint; GeometryHash = population.GeometryHash;
        RequirementsHash = ParameterPatchSourceBinding.Hash(requirements); ClaimsHash = ParameterPatchSourceBinding.Hash(claims);
        WorkingHash = ParameterPatchSourceBinding.Hash(chunks.Select(c => new { c.Key, data = c.WorkingData.Sha256 }).ToArray());
        ValidityHash = ParameterPatchSourceBinding.Hash(chunks.Select(c => new { c.Key, data = c.DesiredValidity.Sha256 }).ToArray());
        CanonicalHash = ParameterPatchSourceBinding.Hash(chunks.Select(c => new { c.Key, data = c.CanonicalDesired.Sha256 }).ToArray());
        ProcedureWitnessHash = procedure.WitnessHash!; TableBindingHash = tables?.Binding.Fingerprint; TableCompatibilityHash = tables?.CompatibilityHash;
        Fingerprint = ParameterPatchSourceBinding.Hash(new { ProfileVersion, ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision, ApplicationId,
            SourceFingerprint, CarrierHash, ProjectInputHash, ContextFingerprint, PopulationHash, ValuesHash, PolicyHash, GeometryHash, RequirementsHash, ClaimsHash,
            WorkingHash, ValidityHash, CanonicalHash, ProcedureWitnessHash, TableBindingHash, TableCompatibilityHash, request = _request, spans });
    }
    internal static RootImageBuildRequest Copy(RootImageBuildRequest request)
        => new(request.CodeMaskPolicy, request.Procedure with { }, request.Tables is null ? null : request.Tables with { });
    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!ReferenceEquals(project, _project) || !ReferenceEquals(device, _device)
            || !OfflineRootImageBuild.WithinBudget(project, device) || !_sources.MatchesIdentity(project, device)) return false;
        return OfflineRootImageBuild.Preview(project, device, _request).Binding?.Fingerprint == Fingerprint;
    }
}
