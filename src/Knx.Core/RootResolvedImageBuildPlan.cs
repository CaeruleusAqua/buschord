namespace Knx;

public sealed record RootResolvedImageBuildRequest(StaticCodeMaskPolicy CodeMaskPolicy, LoadProcedureRequest Procedure, GeneratedTableRequest? Tables = null);
public sealed record RootProcedureControllerSummary(string RefId, string ParameterId, string ParameterTypeId,
    int EverDeclaredOwnerCount, int ActiveNormalOwnerCount, bool Active, string? ActiveValueHash, string DeclarationHash, string PlacementHash);

/// <summary>Immutable controller provenance from the same application evaluation used by all admitted writers.</summary>
public sealed class RootSharedProcedureProof
{
    public string Fingerprint { get; }
    public string ApplicationContextFingerprint { get; }
    public string ControllerSetHash { get; }
    public string PlacementInventoryHash { get; }
    public string SavedValuesHash { get; }
    public IReadOnlyList<RootProcedureControllerSummary> Controllers { get; }
    internal RootSharedProcedureProof(NativeProcedureEvaluationContext context)
    {
        Fingerprint = context.Fingerprint!; ApplicationContextFingerprint = context.SharedApplicationFingerprint!;
        ControllerSetHash = context.SharedControllerSetHash!; PlacementInventoryHash = context.SharedPlacementInventoryHash!;
        SavedValuesHash = context.SavedValuesHash!;
        Controllers = Array.AsReadOnly(context.Controllers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new RootProcedureControllerSummary(
            p.Value.RefId, p.Value.ParameterId, p.Value.ParameterTypeId, p.Value.EverDeclaredOwnerCount, p.Value.ActiveNormalOwnerCount,
            p.Value.Active, p.Value.ActiveValue is { } value ? RootBuildGuard.Hash(value) : null, p.Value.DeclarationHash, p.Value.PlacementHash)).ToArray());
    }
}

/// <summary>Shared table contributions have an owning root binding, without a standalone table replay route.</summary>
public sealed class RootResolvedImageTableSummary
{
    public int ProfileVersion => 2;
    public GeneratedTableFormat Format { get; }
    public string NativeWitnessHash { get; }
    public string EmissionHash { get; }
    public string PayloadHash { get; }
    public string ActualProcedureWitnessHash { get; }
    public string CompatibilityHash { get; }
    public IReadOnlyList<RootImageTableContribution> Payloads { get; }
    public IReadOnlyList<GeneratedTableProcedureAdjustment> ProcedureSizeAdjustments { get; }
    public IReadOnlyList<GeneratedTableRequirement> UnresolvedRequirements { get; }
    internal RootResolvedImageTableSummary(SharedGeneratedTableEmission emission, RootImageTableContribution[] payloads)
    {
        Format = emission.Format!.Value; NativeWitnessHash = emission.NativeWitnessHash!; EmissionHash = emission.Fingerprint!;
        PayloadHash = emission.PayloadHash!; ActualProcedureWitnessHash = emission.ProcedureWitnessHash!;
        CompatibilityHash = RootBuildGuard.Hash(payloads); Payloads = Array.AsReadOnly((RootImageTableContribution[])payloads.Clone());
        ProcedureSizeAdjustments = Array.AsReadOnly(emission.ProcedureSizeAdjustments.ToArray());
        UnresolvedRequirements = Array.AsReadOnly(emission.Requirements.ToArray());
    }
}

public sealed class RootResolvedImageBuildPlan
{
    public string Scope => "RootFixedMemoryResolvedWorkingSet";
    public int ProfileVersion => 2;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public bool CompleteFixedRootPopulation => IsAvailable;
    public bool CompleteApplicationImage => false;
    public bool ExecutionEligible => false;
    public bool DownloadEligible => false;
    public ArchivedImageSourceManifest Sources { get; }
    public RootResolvedImageBuildBinding? Binding { get; }
    public RootImageBuildPolicy? Policy { get; }
    public RootParameterPopulation? Population { get; }
    public LoadProcedurePlan? Procedure { get; }
    public RootSharedProcedureProof? ProcedureProof { get; }
    public RootResolvedImageTableSummary? Tables { get; }
    public IReadOnlyList<RootImageBuildIssue> Issues { get; }
    public IReadOnlyList<ImageCompositionSourceSegment> SourceSegments { get; }
    public IReadOnlyList<RootImageWriteClaim> Claims { get; }
    public IReadOnlyList<RootImageChunk> Chunks { get; }
    public IReadOnlyList<ImageUnspecifiedSpan> UnspecifiedSpans { get; }
    public IReadOnlyList<RootImageBuildRequirement> UnresolvedRequirements { get; }
    public ulong DesiredBitCount { get; }
    public ulong UnspecifiedBitCount { get; }
    internal RootResolvedImageBuildPlan(ArchivedImageSourceManifest sources, RootImageBuildIssue issue)
    {
        Sources = sources; Issues = Array.AsReadOnly(new[] { issue });
        SourceSegments = Array.Empty<ImageCompositionSourceSegment>(); Claims = Array.Empty<RootImageWriteClaim>();
        Chunks = Array.Empty<RootImageChunk>(); UnspecifiedSpans = Array.Empty<ImageUnspecifiedSpan>(); UnresolvedRequirements = Array.Empty<RootImageBuildRequirement>();
    }
    internal RootResolvedImageBuildPlan(ArchivedImageSourceManifest sources, RootResolvedImageBuildBinding binding, RootImageBuildPolicy policy,
        RootParameterPopulation population, LoadProcedurePlan procedure, RootSharedProcedureProof proof, RootResolvedImageTableSummary? tables,
        ImageCompositionSourceSegment[] segments, RootImageWriteClaim[] claims, RootImageChunk[] chunks, ImageUnspecifiedSpan[] spans,
        RootImageBuildRequirement[] requirements, ulong desired, ulong unspecified)
    {
        Sources = sources; Binding = binding; Policy = policy; Population = population; Procedure = procedure; ProcedureProof = proof; Tables = tables;
        Issues = Array.Empty<RootImageBuildIssue>(); SourceSegments = Array.AsReadOnly((ImageCompositionSourceSegment[])segments.Clone());
        Claims = Array.AsReadOnly((RootImageWriteClaim[])claims.Clone()); Chunks = Array.AsReadOnly((RootImageChunk[])chunks.Clone());
        UnspecifiedSpans = Array.AsReadOnly((ImageUnspecifiedSpan[])spans.Clone()); UnresolvedRequirements = Array.AsReadOnly((RootImageBuildRequirement[])requirements.Clone());
        DesiredBitCount = desired; UnspecifiedBitCount = unspecified;
    }
}

/// <summary>Fresh same-route replay of a shared resolved working set; never apply or execution authority.</summary>
public sealed class RootResolvedImageBuildBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly ArchivedImageSourceBinding _sources;
    private readonly RootResolvedImageBuildRequest _request;
    public int ProfileVersion => 2;
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
    public string ProcedureGraphHash { get; }
    public string? SelectedProcedureProfileHash { get; }
    public string ControllerSetHash { get; }
    public string SharedProcedureProofHash { get; }
    public string SharedPlacementInventoryHash { get; }
    public string ProcedureWitnessHash { get; }
    public string? TableEmissionHash { get; }
    public string? TableCompatibilityHash { get; }
    public string Fingerprint { get; }
    internal RootResolvedImageBuildBinding(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        RootResolvedImageBuildRequest request, string carrier, NativeApplicationEvaluationContext context,
        PreparedConditionalLoadProcedure prepared, RootParameterPopulation population, RootImageBuildPolicy policy,
        RootSharedProcedureProof proof, RootImageWriteClaim[] claims, RootImageChunk[] chunks, ImageUnspecifiedSpan[] spans,
        LoadProcedurePlan procedure, RootResolvedImageTableSummary? tables, RootImageBuildRequirement[] requirements)
    {
        _project = project; _device = device; _sources = inputs.Manifest.Binding!; _request = Copy(request);
        ProjectId = project.Id; ProjectGuid = project.Guid; DeviceId = device.Id; ApplicationDataRevision = project.ApplicationDataRevision;
        ApplicationId = inputs.Manifest.ApplicationId!; SourceFingerprint = _sources.Fingerprint; CarrierHash = carrier;
        ProjectInputHash = context.ProjectInputHash!; ContextFingerprint = context.Fingerprint!;
        PopulationHash = population.PopulationHash; ValuesHash = population.ValuesHash; PolicyHash = policy.Fingerprint; GeometryHash = population.GeometryHash;
        RequirementsHash = RootBuildGuard.Hash(requirements); ClaimsHash = RootBuildGuard.Hash(claims);
        WorkingHash = RootBuildGuard.Hash(chunks.Select(c => new { c.Key, data = c.WorkingData.Sha256 }).ToArray());
        ValidityHash = RootBuildGuard.Hash(chunks.Select(c => new { c.Key, data = c.DesiredValidity.Sha256 }).ToArray());
        CanonicalHash = RootBuildGuard.Hash(chunks.Select(c => new { c.Key, data = c.CanonicalDesired.Sha256 }).ToArray());
        ProcedureGraphHash = prepared.GraphFingerprint!; SelectedProcedureProfileHash = prepared.SelectedProfileHash;
        ControllerSetHash = proof.ControllerSetHash; SharedProcedureProofHash = proof.Fingerprint; SharedPlacementInventoryHash = proof.PlacementInventoryHash;
        ProcedureWitnessHash = procedure.WitnessHash!; TableEmissionHash = tables?.EmissionHash; TableCompatibilityHash = tables?.CompatibilityHash;
        Fingerprint = RootBuildGuard.Hash(new
        {
            scope = "RootFixedMemoryResolvedWorkingSet", ProfileVersion, ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision, ApplicationId,
            SourceFingerprint, CarrierHash, ProjectInputHash, ContextFingerprint, PopulationHash, ValuesHash, PolicyHash, GeometryHash, RequirementsHash,
            ClaimsHash, WorkingHash, ValidityHash, CanonicalHash, ProcedureGraphHash, SelectedProcedureProfileHash, ControllerSetHash,
            SharedProcedureProofHash, SharedPlacementInventoryHash, ProcedureWitnessHash, TableEmissionHash, TableCompatibilityHash, request = _request, spans,
        });
    }
    internal static RootResolvedImageBuildRequest Copy(RootResolvedImageBuildRequest request)
        => new(request.CodeMaskPolicy, request.Procedure with { }, request.Tables is null ? null : request.Tables with { });
    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!ReferenceEquals(project, _project) || !ReferenceEquals(device, _device)
            || !OfflineRootImageBuild.WithinBudget(project, device) || !_sources.MatchesIdentity(project, device)) return false;
        return OfflineRootImageBuild.PreviewResolved(project, device, _request).Binding?.Fingerprint == Fingerprint;
    }
}
