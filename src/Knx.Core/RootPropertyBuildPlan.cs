namespace Knx;

public sealed record RootPropertyBuildRequest(LoadProcedureRequest Procedure);
public enum RootPropertyBuildIssueCode
{
    InvalidRequest, SourceUnavailable, UnsupportedApplication, InvalidDeclaration, UnsupportedProfile,
    UnsupportedType, InvalidValue, InvalidPolicy, AmbiguousOwnership, UnknownStorage, UnsupportedPhysicalAlias,
    FieldOutOfRange, LimitExceeded, InputsChanged, ActivationUnavailable, ProcedureUnavailable, InitializationUnavailable,
    ExternalConfigurationUnaccounted, UnaccountedBinaryData, UnsupportedNativeCarrier,
}
public sealed record RootPropertyBuildIssue(RootPropertyBuildIssueCode Code, string? RefId = null, string? PropertyKey = null,
    string? ResourceId = null, ParameterPatchIssue? ParameterIssue = null, GeneratedTableIssue? ActivationIssue = null,
    LoadProcedureIssue? ProcedureIssue = null, PropertyInitializationIssue? InitializationIssue = null);
public enum RootPropertyBuildRequirementCode
{
    UnspecifiedBitsNeedExternalFacts, ActualDeviceOptionsUnverified, PhysicalMappingUnresolved,
    CurrentDeviceStateUnverified, CountAndCapacityNotEstablished, PropertyTransfersNotExecuted,
    InlineActionsNotApplied, OtherWritersNotApplied, CommunicationTablesNotApplied,
    ProcedureFinalizationUnresolved, StampsAndChecksumsUnverified, DeviceInteroperabilityUnverified,
}
public sealed record RootPropertyBuildRequirement(RootPropertyBuildRequirementCode Code, string? PropertyKey = null);
public sealed record RootPropertyParameterRecord(string RefId, string ParameterId, string ParameterTypeId, RootParameterDisposition Disposition,
    bool Active, int NormalReferenceCount, bool LegacyPatchAlways, bool CustomerAdjustable, RootParameterValueOrigin CurrentValueOrigin,
    string CurrentValueHash, string? RecordValueHash, bool NativeRecordEmitted, bool Allocates, bool WritesData,
    string? PropertyKey = null, PropertySourceSelector? RawSelector = null, PropertySourceSelector? NativeSelector = null,
    uint? ByteOffset = null, byte? BitOffset = null, uint? BitLength = null, uint? EncodedWord = null);

public sealed class RootPropertyPopulation
{
    public IReadOnlyList<RootPropertyParameterRecord> Records { get; }
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
    internal RootPropertyPopulation(RootPropertyParameterRecord[] records, int parameters, int owners,
        string populationHash, string valuesHash, string geometryHash)
    {
        Records = Array.AsReadOnly((RootPropertyParameterRecord[])records.Clone()); DeclaredParameterCount = parameters; OwnerCount = owners;
        PopulationHash = populationHash; ValuesHash = valuesHash; GeometryHash = geometryHash;
    }
}
public sealed record RootPropertyWriteClaim(string RefId, string PropertyKey, PropertySourceSelector RawSelector,
    PropertySourceSelector NativeSelector, uint ByteOffset, byte BitOffset, uint BitLength,
    bool WritesData, RootParameterMaskEffect MaskEffect, uint? EncodedWord);

public sealed class RootPropertyAllocation
{
    public string PropertyKey { get; }
    public PropertySourceSelector RawSelector { get; }
    public PropertySourceSelector NativeSelector { get; }
    public PropertyPdtLayout PdtLayout { get; }
    public uint RequestedLength { get; }
    public int Length { get; }
    public string InitialDataHash { get; }
    public string InitialValidityHash { get; }
    public string? FactoryDataHash { get; }
    public IReadOnlyList<int> ControlOrdinals { get; }
    internal RootPropertyAllocation(string key, PropertySourceSelector raw, PropertySourceSelector selector, PropertyPdtLayout pdt, uint requested,
        int length, string initial, string validity, string? factory, IEnumerable<int> controls)
    {
        PropertyKey = key; RawSelector = raw; NativeSelector = selector; PdtLayout = pdt; RequestedLength = requested; Length = length;
        InitialDataHash = initial; InitialValidityHash = validity; FactoryDataHash = factory;
        ControlOrdinals = Array.AsReadOnly(controls.ToArray());
    }
}

/// <summary>Archived count is observed separately; fixed fields neither adopt nor write it.</summary>
public sealed record RootPropertyCountObservation(string PropertyKey, PropertySourceSelector RawSelector, PropertySourceSelector NativeSelector, int SourceOrdinal,
    ImmutablePatchBytes Data, ImmutablePatchBytes? Mask, ushort? EncodedCount, PropertyCountStatus Status)
{
    public string Disposition => "ObservedNotAdopted";
}
public sealed record RootPropertyUnspecifiedSpan(string PropertyKey, uint ByteOffset, int Length, byte BitMask);

/// <summary>Source-initialized working bytes and desired validity, without a current-device before-image claim.</summary>
public sealed class RootPropertyChunk
{
    public string PropertyKey { get; }
    public PropertySourceSelector RawSelector { get; }
    public PropertySourceSelector NativeSelector { get; }
    public PropertyPdtLayout PdtLayout { get; }
    public int Length => WorkingData.Length;
    public ImmutablePatchBytes InitialData { get; }
    public ImmutablePatchBytes InitialValidity { get; }
    public ImmutablePatchBytes WorkingData { get; }
    public ImmutablePatchBytes DesiredValidity { get; }
    public ImmutablePatchBytes CanonicalDesired { get; }
    internal RootPropertyChunk(string key, PropertySourceSelector raw, PropertySourceSelector selector, PropertyPdtLayout pdt,
        ImmutablePatchBytes initial, ImmutablePatchBytes initialValidity, byte[] working, byte[] validity)
    {
        PropertyKey = key; RawSelector = raw; NativeSelector = selector; PdtLayout = pdt; InitialData = initial; InitialValidity = initialValidity;
        WorkingData = new(working); DesiredValidity = new(validity);
        var canonical = new byte[working.Length];
        for (var i = 0; i < canonical.Length; i++) canonical[i] = (byte)(working[i] & validity[i]);
        CanonicalDesired = new(canonical);
    }
}

public sealed class RootPropertyBuildPlan
{
    public string Scope => "RootFixedPropertyWorkingSet";
    public int ProfileVersion => 1;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public bool CompleteFixedRootPopulation => IsAvailable;
    public bool CompleteApplicationImage => false;
    public bool ExecutionEligible => false;
    public bool DownloadEligible => false;
    public ArchivedImageSourceManifest Sources { get; }
    public RootPropertyBuildBinding? Binding { get; }
    public RootImageBuildPolicy? Policy { get; }
    public RootPropertyPopulation? Population { get; }
    public LoadProcedurePlan? Procedure { get; }
    public RootSharedProcedureProof? ProcedureProof { get; }
    public IReadOnlyList<RootPropertyBuildIssue> Issues { get; }
    public IReadOnlyList<RootPropertyAllocation> Allocations { get; }
    public IReadOnlyList<RootPropertyWriteClaim> Claims { get; }
    public IReadOnlyList<RootPropertyChunk> Chunks { get; }
    public IReadOnlyList<RootPropertyUnspecifiedSpan> UnspecifiedSpans { get; }
    public IReadOnlyList<RootPropertyCountObservation> ObservedCounts { get; }
    public IReadOnlyList<RootPropertyBuildRequirement> UnresolvedRequirements { get; }
    public ulong DesiredBitCount { get; }
    public ulong UnspecifiedBitCount { get; }
    internal RootPropertyBuildPlan(ArchivedImageSourceManifest sources, RootPropertyBuildIssue issue)
    {
        Sources = sources; Issues = Array.AsReadOnly(new[] { issue }); Allocations = Array.Empty<RootPropertyAllocation>();
        Claims = Array.Empty<RootPropertyWriteClaim>(); Chunks = Array.Empty<RootPropertyChunk>(); UnspecifiedSpans = Array.Empty<RootPropertyUnspecifiedSpan>();
        ObservedCounts = Array.Empty<RootPropertyCountObservation>(); UnresolvedRequirements = Array.Empty<RootPropertyBuildRequirement>();
    }
    internal RootPropertyBuildPlan(ArchivedImageSourceManifest sources, RootPropertyBuildBinding binding, RootImageBuildPolicy policy,
        RootPropertyPopulation population, LoadProcedurePlan procedure, RootSharedProcedureProof proof, RootPropertyAllocation[] allocations,
        RootPropertyWriteClaim[] claims, RootPropertyChunk[] chunks, RootPropertyUnspecifiedSpan[] spans, RootPropertyCountObservation[] counts,
        RootPropertyBuildRequirement[] requirements, ulong desired, ulong unspecified)
    {
        Sources = sources; Binding = binding; Policy = policy; Population = population; Procedure = procedure; ProcedureProof = proof;
        Issues = Array.Empty<RootPropertyBuildIssue>(); Allocations = Array.AsReadOnly((RootPropertyAllocation[])allocations.Clone());
        Claims = Array.AsReadOnly((RootPropertyWriteClaim[])claims.Clone()); Chunks = Array.AsReadOnly((RootPropertyChunk[])chunks.Clone());
        UnspecifiedSpans = Array.AsReadOnly((RootPropertyUnspecifiedSpan[])spans.Clone()); ObservedCounts = Array.AsReadOnly((RootPropertyCountObservation[])counts.Clone());
        UnresolvedRequirements = Array.AsReadOnly((RootPropertyBuildRequirement[])requirements.Clone()); DesiredBitCount = desired; UnspecifiedBitCount = unspecified;
    }
}

/// <summary>Fresh replay of the same complete fixed-property route; no apply or execution authority.</summary>
public sealed class RootPropertyBuildBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly ResourcePatchIdentity _identity;
    private readonly ArchivedImageSourceBinding _sources;
    private readonly RootPropertyBuildRequest _request;
    public int ProfileVersion => 1;
    public string NumericParseProfile => "ExactInteger";
    public string ProjectId => _identity.ProjectId;
    public string? ProjectGuid => _identity.ProjectGuid;
    public string DeviceId => _identity.DeviceId;
    public long ApplicationDataRevision => _identity.Revision;
    public string SavedValuesHash => _identity.ValuesHash;
    public string ApplicationId { get; }
    public string DefinitionHash { get; }
    public string SourceFingerprint { get; }
    public string PropertyProfileHash { get; }
    public string CarrierHash { get; }
    public string ProjectInputHash { get; }
    public string ContextFingerprint { get; }
    public string SourceHash { get; }
    public string PopulationHash { get; }
    public string PopulationValuesHash { get; }
    public string PolicyHash { get; }
    public string GeometryHash { get; }
    public string InitializationHash { get; }
    public string AllocationHash { get; }
    public string InitialDataHash { get; }
    public string ObservedCountsHash { get; }
    public string RequirementsHash { get; }
    public string ClaimsHash { get; }
    public string WorkingHash { get; }
    public string ValidityHash { get; }
    public string CanonicalHash { get; }
    public string ProcedureGraphHash { get; }
    public string? SelectedProcedureProfileHash { get; }
    public string ControllerSetHash { get; }
    public string SharedProcedureProofHash { get; }
    public string SharedPlacementInventoryHash { get; }
    public string ProcedureWitnessHash { get; }
    public string Fingerprint { get; }
    internal RootPropertyBuildBinding(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        RootPropertyBuildRequest request, RootPropertyWriterSources sources, NativeApplicationEvaluationContext context,
        PreparedConditionalLoadProcedure prepared, RootSharedProcedureProof proof, RootPropertyPopulation population,
        string initializationHash, RootPropertyAllocation[] allocations, RootPropertyWriteClaim[] claims, RootPropertyChunk[] chunks,
        RootPropertyUnspecifiedSpan[] spans, RootPropertyCountObservation[] counts, RootPropertyBuildRequirement[] requirements, LoadProcedurePlan procedure)
    {
        _project = project; _device = device; _identity = ResourcePatchIdentity.Capture(project, device); _sources = inputs.Manifest.Binding!;
        _request = new(request.Procedure with { }); ApplicationId = inputs.Manifest.ApplicationId!; DefinitionHash = inputs.Manifest.ApplicationDefinitionHash!;
        SourceFingerprint = _sources.Fingerprint; PropertyProfileHash = sources.PropertyProfile.Selection.Fingerprint; CarrierHash = sources.CarrierHash;
        SourceHash = sources.SourceHash; ProjectInputHash = context.ProjectInputHash!; ContextFingerprint = context.Fingerprint!;
        PopulationHash = population.PopulationHash; PopulationValuesHash = population.ValuesHash; GeometryHash = population.GeometryHash; PolicyHash = sources.Policy.Fingerprint;
        InitializationHash = initializationHash; AllocationHash = RootPropertyBuildGuard.Hash(allocations);
        InitialDataHash = RootPropertyBuildGuard.Hash(chunks.Select(c => new { c.PropertyKey, data = c.InitialData.Sha256, mask = c.InitialValidity.Sha256 }).ToArray());
        ObservedCountsHash = RootPropertyBuildGuard.Hash(counts.Select(c => new { c.PropertyKey, c.RawSelector, c.NativeSelector, c.SourceOrdinal, c.EncodedCount, c.Status, c.Disposition,
            data = c.Data.Sha256, mask = c.Mask?.Sha256 }).ToArray());
        RequirementsHash = RootPropertyBuildGuard.Hash(requirements); ClaimsHash = RootPropertyBuildGuard.Hash(claims);
        WorkingHash = RootPropertyBuildGuard.Hash(chunks.Select(c => new { c.PropertyKey, data = c.WorkingData.Sha256 }).ToArray());
        ValidityHash = RootPropertyBuildGuard.Hash(chunks.Select(c => new { c.PropertyKey, data = c.DesiredValidity.Sha256 }).ToArray());
        CanonicalHash = RootPropertyBuildGuard.Hash(chunks.Select(c => new { c.PropertyKey, data = c.CanonicalDesired.Sha256 }).ToArray());
        ProcedureGraphHash = prepared.GraphFingerprint!; SelectedProcedureProfileHash = prepared.SelectedProfileHash;
        ControllerSetHash = proof.ControllerSetHash; SharedProcedureProofHash = proof.Fingerprint; SharedPlacementInventoryHash = proof.PlacementInventoryHash;
        ProcedureWitnessHash = procedure.WitnessHash!;
        Fingerprint = RootPropertyBuildGuard.Hash(new { scope = "RootFixedPropertyWorkingSet", ProfileVersion, NumericParseProfile,
            ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision, ApplicationId, DefinitionHash, SavedValuesHash,
            SourceFingerprint, PropertyProfileHash, CarrierHash, ProjectInputHash, ContextFingerprint, SourceHash, PopulationHash,
            PopulationValuesHash, PolicyHash, GeometryHash, InitializationHash, AllocationHash, InitialDataHash, ObservedCountsHash,
            RequirementsHash, ClaimsHash, WorkingHash, ValidityHash, CanonicalHash, ProcedureGraphHash, SelectedProcedureProfileHash,
            ControllerSetHash, SharedProcedureProofHash, SharedPlacementInventoryHash, ProcedureWitnessHash, request = _request, spans });
    }
    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!ReferenceEquals(project, _project) || !ReferenceEquals(device, _device)
            || !OfflineRootPropertyBuild.WithinBudget(project, device) || !_sources.MatchesIdentity(project, device)
            || ResourcePatchIdentity.Capture(project, device) != _identity) return false;
        return OfflineRootPropertyBuild.Preview(project, device, _request).Binding?.Fingerprint == Fingerprint;
    }
}
