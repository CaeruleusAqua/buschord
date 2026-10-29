namespace Knx;

/// <summary>Explicit conditional option words, never observed options from a connected device.</summary>
public sealed record NativeImageBuildRequest(IReadOnlyList<uint> OptionWords, GeneratedTableRequest? Tables = null);
public enum NativeImageBuildSpace { StandardMemory, RelativeMemory, PropertyData, PropertyCount }
public enum NativeImageBuildSourceKind { ZeroRegion, NativeCode, PointerZero, FactoryProperty }
public enum NativeImageBuildProcedureState { BasicSkipped, NativeEmpty, Compiled }
public enum NativeImageBuildIssueCode
{
    InvalidRequest, SourceUnavailable, InvalidDeclaration, UnsupportedApplication,
    UnsupportedSource, UnsupportedType, InvalidValue, InvalidPolicy,
    UnsupportedMapping, AmbiguousMapping, UnsupportedScenario, UnsupportedControl,
    AmbiguousOwnership, MissingAllocation, UncoveredOwner, NoncommutativeValidity,
    ContextMismatch, ProcedureUnavailable, TableUnavailable, InputsChanged,
    ExternalConfigurationUnaccounted, UnaccountedBinaryData, UnsupportedNativeCarrier, LimitExceeded,
}
public sealed record NativeImageBuildIssue(NativeImageBuildIssueCode Code, string? RefId = null,
    string? ResourceId = null, int? VariantOrdinal = null, ParameterPatchIssue? ParameterIssue = null,
    GeneratedTableIssue? ActivationIssue = null, LoadProcedureIssue? ProcedureIssue = null);
public sealed record NativeImageBuildTarget(NativeImageBuildSpace Space, byte? LoadStateMachine, PropertySourceSelector? Property);
public sealed record NativeImageBuildTasks(bool BasicProcedure, bool AddCode, bool Parameters, bool Tables);
public sealed record NativeImageConditionalFact(bool Value, bool SourceProven);
public sealed record NativeImageBuildField(NativeImageBuildTarget Target, uint ByteOffset, byte BitOffset, uint BitLength);
public sealed record NativeImageBuildRecord(string RefId, string ParameterId, string ParameterTypeId,
    RootParameterDisposition Disposition, bool Active, int NormalReferenceCount, bool LegacyPatchAlways,
    bool CustomerAdjustable, bool CustomerExcluded, RootParameterValueOrigin CurrentValueOrigin,
    string CurrentValueHash, string? RecordValueHash, bool NativeRecordEmitted, bool Allocates, bool WritesData,
    NativeImageBuildField? Field);
public sealed record NativeImageBuildModuleOrigin(string InstanceId, string NativeInstanceId, string CallId,
    string DefinitionId, string OriginalParameterId, string ParameterId, string? OriginalReferenceId, string? ReferenceId,
    int SourceKey, string DeclarationHash, NativeImageBuildField? Field);
public sealed record NativeImageBuildModuleAllocation(string InstanceId, string ArgumentId, string AllocatorId,
    long Value, long Allocates, long Alignment, long PoolStart, long PoolMaximumInclusive, string Usage);
public sealed record NativeImageBuildModuleReservation(string OwnerKey, NativeImageBuildField Field);

/// <summary>Archived source policy and admitted physical codecs, independent of the assumed task selection.</summary>
public sealed class NativeImageBuildPolicy
{
    public ParameterPatchByteOrder ByteOrder { get; }
    public RootParameterDownloadBehavior DownloadBehavior { get; }
    public RootParameterMaskPolicy MaskPolicy { get; }
    public string? MinEtsVersion { get; }
    public uint PackedMinEtsVersion { get; }
    public bool OptionsDeclared { get; }
    public string? DeclaredDownloadBehavior { get; }
    public string? DeclaredByteOrder { get; }
    public int CodecProfileVersion { get; }
    public string NumericParseProfile { get; }
    public string? PackedTimeEncodingProfile { get; }
    public string? DenseTimeEncodingProfile { get; }
    public int PhysicalFieldCount { get; }
    public int SingleFieldCount { get; }
    public int PackedTimeFieldCount { get; }
    public int DenseTimeFieldCount { get; }
    public int RawDataFieldCount { get; }
    public string? RawDataEncodingProfile { get; }
    public string SourcePolicyFingerprint { get; }
    public string CodecFactsFingerprint { get; }
    public string Fingerprint { get; }
    internal NativeImageBuildPolicy(NativeRootSourcePolicy policy, NativeRootCodecFacts codecs)
    {
        ByteOrder = policy.ByteOrder; DownloadBehavior = policy.DownloadBehavior; MaskPolicy = policy.MaskPolicy;
        MinEtsVersion = policy.MinEtsVersion; PackedMinEtsVersion = policy.PackedMinEtsVersion; OptionsDeclared = policy.OptionsDeclared;
        DeclaredDownloadBehavior = policy.DeclaredDownloadBehavior; DeclaredByteOrder = policy.DeclaredByteOrder;
        CodecProfileVersion = codecs.CodecProfileVersion; NumericParseProfile = codecs.NumericParseProfile;
        PackedTimeEncodingProfile = codecs.PackedTimeEncodingProfile; DenseTimeEncodingProfile = codecs.DenseTimeEncodingProfile;
        PhysicalFieldCount = codecs.PhysicalFieldCount; SingleFieldCount = codecs.SingleFieldCount;
        PackedTimeFieldCount = codecs.PackedTimeFieldCount; DenseTimeFieldCount = codecs.DenseTimeFieldCount;
        RawDataFieldCount = codecs.RawDataFieldCount; RawDataEncodingProfile = codecs.RawDataEncodingProfile;
        SourcePolicyFingerprint = policy.Fingerprint; CodecFactsFingerprint = codecs.Fingerprint;
        Fingerprint = NativeGeneralHash.Of(new { version = 1, SourcePolicyFingerprint, CodecFactsFingerprint });
    }
}

public sealed class NativeImageBuildPopulation
{
    public IReadOnlyList<NativeImageBuildModuleOrigin> ModuleOrigins { get; }
    public string? ModuleOriginsFingerprint { get; }
    public IReadOnlyList<NativeImageBuildModuleAllocation> ModuleAllocations { get; }
    public IReadOnlyList<NativeImageBuildModuleReservation> ModuleReservations { get; }
    public IReadOnlyList<ApplicationModuleInstance> ModuleInstances { get; }
    public bool ParameterTaskScheduled { get; }
    public bool CompleteAdmittedRootPopulation => ParameterTaskScheduled;
    public string CustomerPolicy { get; }
    public int ReferenceCount => Records.Count;
    public int OwnerCount { get; }
    public int PhysicalRecordCount => Records.Count(r => r.NativeRecordEmitted);
    public int NoStorageCount => Records.Count(r => r.Disposition == RootParameterDisposition.NoStorage);
    public int ActiveCount => Records.Count(r => r.Active);
    public int AlwaysCount => Records.Count(r => r.Disposition == RootParameterDisposition.AlwaysValue);
    public int InactiveCount => Records.Count(r => r.NativeRecordEmitted && !r.Active && !r.LegacyPatchAlways);
    public int CustomerExcludedCount => Records.Count(r => r.CustomerExcluded);
    public string PopulationHash { get; }
    public string GeometryHash { get; }
    public string MaskInteractionHash { get; }
    public IReadOnlyList<NativeImageBuildRecord> Records { get; }
    internal NativeImageBuildPopulation(NativeRootVariantPopulation population)
    {
        ModuleOriginsFingerprint = population.ModuleOriginsFingerprint;
        ModuleInstances = Array.AsReadOnly(population.ModuleInstances.Select(i=>new ApplicationModuleInstance(i.Id,i.RefId,i.RepeatIndex,
            Array.AsReadOnly(i.Arguments.Select(a=>new ApplicationModuleArgument(a.RefId,a.Value)).ToArray()))).ToArray());
        ModuleAllocations = Array.AsReadOnly(population.ModuleAllocations.Select(a=>new NativeImageBuildModuleAllocation(a.InstanceId,
            a.ArgumentId,a.AllocatorId,a.Value,a.Allocates,a.Alignment,a.PoolStart,a.PoolMaximumInclusive,a.Usage)).ToArray());
        ModuleReservations = Array.AsReadOnly(population.ModuleReservations.Select(owner=>new NativeImageBuildModuleReservation(owner.OwnerKey,
            new NativeImageBuildField(NativeImageBuildProjection.Target(owner.Target),owner.ByteOffset,owner.BitOffset,owner.BitLength))).ToArray());
        var declaredOwners = population.DeclaredOwners.ToDictionary(owner => owner.ParameterId, StringComparer.Ordinal);
        ModuleOrigins = Array.AsReadOnly(population.ModuleOrigins.Select(origin => new NativeImageBuildModuleOrigin(origin.InstanceId,
            origin.NativeInstanceId, origin.CallId, origin.DefinitionId, origin.OriginalParameterId, origin.ParameterId,
            origin.OriginalReferenceId, origin.ReferenceId, origin.SourceKey, origin.DeclarationHash,
            !declaredOwners.TryGetValue(origin.ParameterId, out var owner) ? null : new NativeImageBuildField(NativeImageBuildProjection.Target(owner.Target),
                owner.ByteOffset, owner.BitOffset, owner.BitLength))).ToArray());
        ParameterTaskScheduled = population.ParameterTaskDisposition == NativeRootTaskDisposition.Scheduled;
        CustomerPolicy = population.CustomerPolicy.ToString(); OwnerCount = ParameterTaskScheduled ? population.DeclaredOwners.Count : 0;
        PopulationHash = population.PopulationHash!; GeometryHash = population.GeometryHash!; MaskInteractionHash = population.MaskInteractionHash!;
        Records = Array.AsReadOnly(population.Records.Select(r => new NativeImageBuildRecord(r.RefId, r.ParameterId, r.ParameterTypeId,
            r.Disposition, r.Active, r.NormalReferenceCount, r.LegacyPatchAlways, r.CustomerAdjustable, r.CustomerExcluded,
            r.CurrentValueOrigin, r.CurrentValueHash, r.RecordValueHash, r.NativeRecordEmitted, r.Allocates, r.WritesData,
            r.Claim is not { } claim ? null : new NativeImageBuildField(NativeImageBuildProjection.Target(claim.Target),
                claim.ByteOffset, claim.BitOffset, claim.BitLength))).ToArray());
    }
}

/// <summary>Source roles remain separate from native allocation and later desired bytes.</summary>
public sealed record NativeImageBuildSource(NativeImageBuildSourceKind Kind, int Order, string SourceId, string DeclarationHash,
    NativeImageBuildTarget Target, uint ByteOffset, int Length, uint? DeclaredLength, int RequestedLength,
    PropertySourceSelector? RawSelector, ImmutablePatchBytes? RawData, ImmutablePatchBytes? RawMask,
    string MappingHash, PropertyPdtLayout? PdtLayout, int? FactorySourceOrdinal);
public sealed record NativeImageBuildChunk(string Key, NativeImageBuildTarget Target, uint ByteOffset,
    IReadOnlyList<string> SourceIds, ImmutablePatchBytes InitialData, ImmutablePatchBytes? InitialValidity,
    ImmutablePatchBytes WorkingData, ImmutablePatchBytes? DesiredValidity, ImmutablePatchBytes? CanonicalDesired,
    ImmutablePatchBytes ParameterWriteBits)
{ public int Length => WorkingData.Length; }
public sealed record NativeImageBuildCount(int SourceOrdinal, FactoryImageLocation RawLocation,
    ImmutablePatchBytes Data, ImmutablePatchBytes? Mask, ushort EncodedCount, PropertyCountStatus Status)
{ public bool NotAdopted => true; }
public sealed class NativeImageBuildProcedure
{
    public NativeImageBuildProcedureState State { get; }
    public string? NativeSubtype { get; }
    public int? SelectedSourceOrdinal { get; }
    public string? SelectedSourceHash { get; }
    public int? Rank { get; }
    public string? DeclarationWitnessHash { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<LoadProcedureControl> EffectiveControls { get; }
    internal NativeImageBuildProcedure(NativeImageVariant variant, NativeImageCompiledProcedure procedure)
    {
        State = Enum.Parse<NativeImageBuildProcedureState>(procedure.State.ToString());
        NativeSubtype = variant.Selection.NativeSubtype; SelectedSourceOrdinal = variant.Selection.SourceOrdinal;
        SelectedSourceHash = variant.Selection.DeclarationHash; Rank = variant.Selection.Rank;
        DeclarationWitnessHash = procedure.DeclarationPlan?.WitnessHash; Fingerprint = procedure.Fingerprint;
        EffectiveControls = procedure.NativeEffectiveControls;
    }
}
/// <summary>Encoder inputs are privately captured current working bytes; no archived Before state is asserted.</summary>
public sealed record NativeImageBuildTablePayload(GeneratedTableRole Role, NativeImageBuildTarget Target,
    uint ByteOffset, string? ResourceId, ImmutablePatchBytes Data, ImmutablePatchBytes WriteMask,
    ImmutablePatchBytes BeforeWorkingData, ImmutablePatchBytes BeforeValidity, ImmutablePatchBytes AfterValidity)
{ public int Length => Data.Length; }
public sealed class NativeImageBuildTables
{
    public GeneratedTableFormat Format { get; }
    public string Fingerprint { get; }
    public string CurrentSeedHash { get; }
    public string NativeWitnessHash { get; }
    public IReadOnlyList<NativeImageBuildTablePayload> Payloads { get; }
    public IReadOnlyList<GeneratedTableProcedureAdjustment> ProcedureAdjustments { get; }
    public IReadOnlyList<string> Requirements { get; }
    internal NativeImageBuildTables(NativeGeneralTableEmission tables)
    {
        Format = tables.Format; Fingerprint = tables.Fingerprint!; CurrentSeedHash = tables.CurrentSeedHash!;
        NativeWitnessHash = tables.NativeWitnessHash!; ProcedureAdjustments = tables.ProcedureAdjustments; Requirements = tables.Requirements;
        Payloads = Array.AsReadOnly(tables.Payloads.Select(p => new NativeImageBuildTablePayload(p.Role,
            NativeImageBuildProjection.Target(NativeImageWorkingKernel.Target(p.Arena)), p.ByteOffset, p.ResourceId,
            p.Data, p.WriteMask, p.BeforeData!, p.BeforeMask!, p.AfterMask!)).ToArray());
    }
}

public sealed class NativeImageBuildVariant
{
    public int Ordinal { get; }
    public uint AssumedOptions { get; }
    public NativeImageConditionalFact? ToolManagedIp { get; }
    public NativeImageBuildTasks Tasks { get; }
    public bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    public string? Fingerprint { get; }
    public string? InitializationHash { get; }
    public string? CoverageHash { get; }
    public NativeImageBuildPopulation? Population { get; }
    public NativeImageBuildProcedure? Procedure { get; }
    public NativeImageBuildTables? Tables { get; }
    public IReadOnlyList<NativeImageBuildIssue> Issues { get; }
    public IReadOnlyList<NativeImageBuildSource> Sources { get; }
    public IReadOnlyList<NativeImageBuildChunk> Chunks { get; }
    public IReadOnlyList<NativeImageBuildCount> ObservedCounts { get; }
    public IReadOnlyList<string> Requirements { get; }
    internal NativeImageBuildVariant(NativeGeneralImageAlternative alternative)
    {
        var variant = alternative.Variant; Ordinal = variant.Ordinal; AssumedOptions = variant.RawOptions;
        ToolManagedIp = variant.ToolManagedIpFact is not { } toolIp ? null : new(toolIp.Value, toolIp.Origin == NativeConditionalBooleanOrigin.SourceProven);
        Tasks = new(variant.Tasks.BasicProcedure, variant.Tasks.AddCode, variant.Tasks.Parameters, variant.Tasks.Tables);
        Issues = Array.AsReadOnly(alternative.Issues.Select(NativeImageBuildProjection.Issue).ToArray());
        Sources = Array.AsReadOnly(Array.Empty<NativeImageBuildSource>()); Chunks = Array.AsReadOnly(Array.Empty<NativeImageBuildChunk>());
        ObservedCounts = Array.AsReadOnly(Array.Empty<NativeImageBuildCount>()); Requirements = Array.AsReadOnly(Array.Empty<string>());
        if (!alternative.IsAvailable) return;
        var initialization = alternative.Initialization!;
        Fingerprint = alternative.Fingerprint; InitializationHash = initialization.Fingerprint; CoverageHash = initialization.CoverageHash;
        Population = new(alternative.Population!); Procedure = new(variant, initialization.CompiledProcedure!);
        Tables = alternative.Tables is null ? null : new(alternative.Tables);
        Sources = Array.AsReadOnly(initialization.Contributions.Select(s => new NativeImageBuildSource(
            Enum.Parse<NativeImageBuildSourceKind>(s.Kind.ToString()), s.Order, s.SourceId, s.DeclarationHash,
            NativeImageBuildProjection.Target(s.Range.Target), s.Range.ByteOffset, s.Range.Length, s.DeclaredLength, s.RequestedLength,
            s.RawSelector, s.RawData, s.RawMask, s.MappingHash, s.PdtLayout, s.FactorySourceOrdinal)).ToArray());
        Chunks = Array.AsReadOnly(alternative.Chunks.Select(c => new NativeImageBuildChunk(
            NativeGeneralHash.Of(new { c.Range, c.Sources }), NativeImageBuildProjection.Target(c.Range.Target), c.Range.ByteOffset,
            c.Sources, c.InitialData, c.InitialValidity, c.WorkingData, c.DesiredValidity, c.CanonicalDesired, c.ParameterWriteBits)).ToArray());
        ObservedCounts = Array.AsReadOnly(initialization.ObservedCounts.Select(c => new NativeImageBuildCount(c.SourceOrdinal,
            c.RawLocation, c.Data, c.Mask, c.EncodedCount, c.Status)).ToArray());
        Requirements = alternative.Requirements;
    }
}

/// <summary>Combined logical working alternatives. Physical mapping, actual options and finalization remain explicit.</summary>
public sealed class NativeImageBuildPlan
{
    public int ProfileVersion => 1;
    public string Scope => "NativeLogicalWorkingSet";
    public bool IsAvailable => Binding is not null && Variants.Any(v => v.IsAvailable);
    public bool HasGlobalFailure { get; }
    public bool AllRequestedAssumptionsAvailable => IsAvailable && Variants.All(v => v.IsAvailable);
    public bool ActualOptionsKnown => false;
    public bool CompleteApplicationImage => false;
    public bool ExecutionEligible => false;
    public bool DownloadEligible => false;
    public string? SourceFingerprint { get; }
    public string? SourceHash { get; }
    public string? ApplicationId { get; }
    public string? MaskVersionId { get; }
    public string? ApplicationDefinitionHash { get; }
    public string? HardwareDefinitionHash { get; }
    public string? PreparationHash { get; }
    public string? SharedContextHash { get; }
    public int DeclaredReferenceCount { get; }
    public int DeclaredOwnerCount { get; }
    public NativeImageBuildPolicy? Policy { get; }
    public long AllocatedImagePayloadBytes { get; }
    public long ReservedWorstCaseImagePayloadBytes { get; }
    public long ReservedProcedurePayloadBytes { get; }
    public IReadOnlyList<uint> AssumedOptions { get; }
    public NativeImageBuildBinding? Binding { get; }
    public IReadOnlyList<NativeImageBuildIssue> Issues { get; }
    public IReadOnlyList<NativeImageBuildVariant> Variants { get; }
    internal NativeImageBuildPlan(NativeGeneralImageBuildPlan plan, uint[] options)
    {
        HasGlobalFailure = plan.HasGlobalFailure; SourceFingerprint = plan.SourceFingerprint; SourceHash = plan.SourceHash;
        ApplicationId = plan.ApplicationId; MaskVersionId = plan.MaskVersionId;
        ApplicationDefinitionHash = plan.ApplicationDefinitionHash; HardwareDefinitionHash = plan.HardwareDefinitionHash;
        PreparationHash = plan.PreparationHash; SharedContextHash = plan.SharedContextHash;
        DeclaredReferenceCount = plan.DeclaredReferenceCount; DeclaredOwnerCount = plan.DeclaredOwnerCount;
        Policy = plan.SourcePolicy is not { } policy || plan.CodecFacts is not { } codecs ? null : new(policy, codecs);
        AllocatedImagePayloadBytes = plan.AllocatedImagePayloadBytes; ReservedWorstCaseImagePayloadBytes = plan.ReservedWorstCaseImagePayloadBytes;
        ReservedProcedurePayloadBytes = plan.ReservedProcedurePayloadBytes; AssumedOptions = Array.AsReadOnly((uint[])options.Clone());
        Binding = plan.Binding is null ? null : new(plan.Binding);
        Issues = Array.AsReadOnly(plan.Issues.Select(NativeImageBuildProjection.Issue).ToArray());
        Variants = Array.AsReadOnly(plan.Variants.Select(v => new NativeImageBuildVariant(v)).ToArray());
    }
}
public sealed class NativeImageBuildBinding
{
    private readonly NativeGeneralImageBinding _binding;
    public string Fingerprint => _binding.Fingerprint;
    internal NativeImageBuildBinding(NativeGeneralImageBinding binding) => _binding = binding;
    public bool IsCurrent(Project project, DeviceInstance device) => _binding.IsCurrent(project, device);
}
internal static class NativeImageBuildProjection
{
    internal static NativeImageBuildTarget Target(NativeImageTarget target)
        => new(Enum.Parse<NativeImageBuildSpace>(target.Space.ToString()), target.Lsm, target.Selector);
    internal static NativeImageBuildIssue Issue(NativeGeneralIssue issue)
        => new(Enum.Parse<NativeImageBuildIssueCode>(issue.Code.ToString()), issue.RefId, issue.ResourceId, issue.VariantOrdinal,
            issue.ParameterIssue, issue.ActivationIssue, issue.ProcedureIssue);
}

public static class OfflineNativeImageBuild
{
    public static NativeImageBuildPlan Preview(Project project, DeviceInstance device, NativeImageBuildRequest request)
    {
        if (request is null || request.OptionWords is null || request.OptionWords.Count is < 1 or > 16
            || request.Tables is not null && !Enum.IsDefined(request.Tables.Format))
            return new(NativeGeneralImageBuildPlan.Unavailable(new(NativeGeneralIssueCode.InvalidRequest)), []);
        var options = request.OptionWords.Take(17).ToArray();
        if (options.Length is < 1 or > 16 || options.Distinct().Count() != options.Length)
            return new(NativeGeneralImageBuildPlan.Unavailable(new(NativeGeneralIssueCode.InvalidRequest)), []);
        var tables = request.Tables is null ? null : request.Tables with { };
        return new(NativeGeneralImageBuild.Build(project, device, options, tables), options);
    }
}
