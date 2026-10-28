namespace Knx;

public enum GeneratedTableFormat { M112, SystemB2ByteAssociations, SystemB4ByteAssociations }
public sealed record GeneratedTableRequest(GeneratedTableFormat Format);
public enum GeneratedTableRole { GroupAddress, Association, CommunicationObject, GroupAcknowledge }
public enum GeneratedTablePayloadOrigin { RetainedCode, FreshGenerated }
public enum GeneratedTableIssueCode
{
    InvalidRequest, InputsChanged, InvalidApplication, InvalidMaster, LimitExceeded,
    IncompleteActivation, ScriptEvaluationRequired, ScriptEvaluationFailed, UnknownObjectOrigin,
    InvalidObjectDeclaration, AmbiguousObjectIdentity, AmbiguousObjectNumber, UnsupportedObjectSize,
    UnsupportedObjectPriority, InvalidNativeOverride, UnknownSavedObject, SavedNumberMismatch,
    InactiveSavedObjectHasLinks, UnknownGroupAddress, AmbiguousGroupAddress, DuplicateAssociation,
    AmbiguousConnectorOverride, MultipleSendingAssociations, UnsupportedConnectorMetadata,
    UnaccountedBinaryData, SecureAssociationUnsupported, UnsupportedMedium, StaleSource,
    UnsupportedTableFormat, MissingTableDeclaration, InvalidTableDeclaration, MissingResource,
    AmbiguousResourceOwnership, MissingInitialData, UnsupportedFactoryImage, TableCapacityExceeded,
    UnsupportedProcedureControl, RequiredProcedureControlMissing, AmbiguousProcedureControl,
    MissingAcknowledgeResource, UnsupportedAcknowledgement, UnsupportedTableManagement, SourceBindingUnavailable,
    ExternalConfigurationUnaccounted,
    UnsupportedActivationDependency, ActivationCycle, ActivationMismatch, UnknownParameterOrigin,
    UnsupportedActivationCondition, UnsupportedStructureActivation,
}
public sealed record GeneratedTableIssue(GeneratedTableIssueCode Code, string? RefId = null, string? ResourceId = null);
public enum GeneratedTableRequirementCode
{
    CompleteParameterImage, PhysicalResourceMapping, ProcedureFinalization, SecurityAndRfTables,
    ProcedureAdjustmentsNotApplied, UnusedTailMaskInvalidation,
}
public sealed record GeneratedTableRequirement(GeneratedTableRequirementCode Code, string? ResourceId = null);
public sealed record GeneratedTableProcedureAdjustment(int ControlOrdinal, LoadControlKind Kind,
    string ArgumentName, ulong BeforeValue, ulong AfterValue, GeneratedTableRole Role);

/// <summary>
/// One used table span. WriteMask records newly assigned whole-byte write operations, independently
/// of retained source-mask validity. It does not describe unused capacity outside this payload.
/// </summary>
public sealed class GeneratedTablePayload
{
    public GeneratedTableRole Role { get; }
    public ResourcePatchArena Arena { get; }
    public uint ByteOffset { get; }
    public GeneratedTablePayloadOrigin Origin { get; }
    public string? ResourceId { get; }
    public ImmutablePatchBytes Data { get; }
    public ImmutablePatchBytes WriteMask { get; }
    public ImmutablePatchBytes? BeforeData { get; }
    public ImmutablePatchBytes? BeforeMask { get; }
    public ImmutablePatchBytes? AfterMask { get; }
    public int Length => Data.Length;

    internal GeneratedTablePayload(GeneratedTableRole role, ResourcePatchArena arena, uint offset,
        GeneratedTablePayloadOrigin origin, string? resourceId, byte[] data, byte[] writes,
        byte[]? beforeData, byte[]? beforeMask, byte[]? afterMask)
    {
        if (data.Length != writes.Length || beforeData is not null && beforeData.Length != data.Length
            || beforeMask is not null && beforeMask.Length != data.Length || afterMask is not null && afterMask.Length != data.Length)
            throw new ArgumentException("Table span lengths must match.");
        Role = role; Arena = arena; ByteOffset = offset; Origin = origin; ResourceId = resourceId;
        Data = new(data); WriteMask = new(writes);
        BeforeData = beforeData is null ? null : new(beforeData);
        BeforeMask = beforeMask is null ? null : new(beforeMask);
        AfterMask = afterMask is null ? null : new(afterMask);
    }
}

/// <summary>Detached common-table bytes and their own freshness binding; no execution or complete-image authority.</summary>
public sealed class GeneratedTablePreview
{
    public int ProfileVersion => 1;
    public string Scope => "GeneratedCommunicationTables";
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public bool ExecutionEligible => false;
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public GeneratedTableFormat? Format { get; }
    public string? NativeWitnessHash { get; }
    public GeneratedTableSourceBinding? Binding { get; }
    public IReadOnlyList<GeneratedTableIssue> Issues { get; }
    public IReadOnlyList<GeneratedTableRequirement> Requirements { get; }
    public IReadOnlyList<GeneratedTablePayload> Payloads { get; }
    public IReadOnlyList<GeneratedTableProcedureAdjustment> ProcedureSizeAdjustments { get; }

    private GeneratedTablePreview(GeneratedTableIssue issue, GeneratedTableFormat? format)
    {
        Format = format is { } selected && Enum.IsDefined(selected) ? selected : null;
        Issues = Array.AsReadOnly(new[] { issue });
        Requirements = Array.AsReadOnly(Array.Empty<GeneratedTableRequirement>());
        Payloads = Array.AsReadOnly(Array.Empty<GeneratedTablePayload>());
        ProcedureSizeAdjustments = Array.AsReadOnly(Array.Empty<GeneratedTableProcedureAdjustment>());
    }
    public static GeneratedTablePreview Unavailable(GeneratedTableIssue issue, GeneratedTableFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new(issue, format);
    }
    internal GeneratedTablePreview(GeneratedTableFormat format, string nativeWitnessHash, GeneratedTableSourceBinding binding,
        GeneratedTablePayload[] payloads, GeneratedTableProcedureAdjustment[] adjustments, GeneratedTableRequirement[] requirements)
    {
        Format = format; NativeWitnessHash = nativeWitnessHash; Binding = binding;
        Issues = Array.AsReadOnly(Array.Empty<GeneratedTableIssue>());
        Payloads = Array.AsReadOnly((GeneratedTablePayload[])payloads.Clone());
        ProcedureSizeAdjustments = Array.AsReadOnly((GeneratedTableProcedureAdjustment[])adjustments.Clone());
        Requirements = Array.AsReadOnly((GeneratedTableRequirement[])requirements.Clone());
    }
}
