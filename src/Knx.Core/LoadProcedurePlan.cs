namespace Knx;

[Flags]
public enum LoadParts { None = 0, Full = 1, Parameters = 2, Groups = 4 }
public enum LoadProcedureAccess { Remote }
public sealed record LoadProcedureRequest(string NativeSubtype, LoadProcedureAccess Access, LoadParts Parts);
public enum LoadProcedureStyle { DefaultProcedure, ProductProcedure, MergedProcedure }
public enum LoadControlKind
{
    Connect, Disconnect, Load, Unload, LoadCompleted, AbsoluteSegment, RelativeSegment,
    TaskSegment, CompareProperty, WriteProperty, WriteRelativeMemory, LoadImageProperty, Restart, MapError, Merge,
    CompareRelativeMemory,
}
public enum LoadControlOrigin { Product, Mask, ProductMerge }
public enum LoadControlCondition { Auto, All, Full, Parameters, Groups, FullOrParameters, FullOrGroups, ParametersAndGroups }
public enum LoadControlArgumentKind { Unsigned, Boolean, Text }
public sealed record LoadControlArgument(string Name, LoadControlArgumentKind Kind,
    ulong? UnsignedValue = null, bool? BooleanValue = null, string? TextValue = null);
public enum LoadControlErrorCause { ResourceNotFound, CompareMismatch }
public sealed record LoadControlErrorPolicy(LoadControlErrorCause Cause, bool Ignore, string? MessageRef);

/// <summary>A detached declaration. Inline bytes are action arguments, never inferred property before-data.</summary>
public sealed class LoadProcedureControl
{
    public int Ordinal { get; }
    public LoadControlKind Kind { get; }
    public LoadControlOrigin Origin { get; }
    public int SourceOrdinal { get; }
    public int? TemplateOrdinal { get; }
    public byte? MergeId { get; }
    public LoadControlCondition DeclaredAppliesTo { get; }
    public LoadControlCondition EffectiveAppliesTo { get; }
    public IReadOnlyList<LoadControlArgument> Arguments { get; }
    public IReadOnlyList<LoadControlErrorPolicy> OnError { get; }
    public ImmutablePatchBytes? InlineData { get; }
    public ImmutablePatchBytes? InlineMask { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LoadProcedureSourceLocation? Provenance { get; }
    internal LoadProcedureControl(int ordinal, LoadControlKind kind, LoadControlOrigin origin, int sourceOrdinal,
        int? templateOrdinal, byte? mergeId, LoadControlCondition declared, LoadControlCondition effective,
        LoadControlArgument[] arguments, LoadControlErrorPolicy[] onError, ImmutablePatchBytes? data, ImmutablePatchBytes? mask)
    {
        Ordinal = ordinal; Kind = kind; Origin = origin; SourceOrdinal = sourceOrdinal; TemplateOrdinal = templateOrdinal;
        MergeId = mergeId; DeclaredAppliesTo = declared; EffectiveAppliesTo = effective;
        Arguments = Array.AsReadOnly((LoadControlArgument[])arguments.Clone());
        OnError = Array.AsReadOnly((LoadControlErrorPolicy[])onError.Clone()); InlineData = data; InlineMask = mask;
    }
    internal LoadProcedureControl(int ordinal, LoadControlKind kind, LoadControlOrigin origin, int sourceOrdinal,
        int? templateOrdinal, byte? mergeId, LoadControlCondition declared, LoadControlCondition effective,
        LoadControlArgument[] arguments, LoadControlErrorPolicy[] onError, ImmutablePatchBytes? data, ImmutablePatchBytes? mask,
        LoadProcedureSourceLocation provenance)
        : this(ordinal, kind, origin, sourceOrdinal, templateOrdinal, mergeId, declared, effective, arguments, onError, data, mask)
        => Provenance = provenance;
}

public enum LoadMergeDecisionKind { MissingGroupSkipped, Expanded, AllControlsFiltered }
public sealed record LoadMergeDecision(byte MergeId, int TemplateOrdinal, LoadMergeDecisionKind Decision, int EmittedControls)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LoadProcedureSourceLocation? Provenance { get; internal init; }
}
public enum LoadProcedureIssueCode
{
    InvalidRequest, UnsupportedApplicationNamespace, InvalidApplication, InvalidMaster,
    MissingProcedure, AmbiguousProcedure, UnsupportedControl, InvalidControl, InvalidResource,
    AmbiguousResourceOwnership, LimitExceeded,
    InvalidControllerDeclaration, UnknownController, NeverPlacedController, UnsupportedActivationDependency,
    ActivationCycle, InvalidControllerValue, UnsupportedActivationCondition, UnsupportedStructureActivation,
    ExternalConfigurationUnaccounted, InputsChanged, ContextMismatch, UnaccountedBinaryData,
}
public sealed record LoadProcedureIssue(LoadProcedureIssueCode Code, string? ReferenceId = null, int? SourceOrdinal = null);
public enum LoadProcedureRequirementCode
{
    SecondaryProgramOutsideScope, ModuleProceduresNotCompiled, FinalizationNotCompiled,
    GlobalLoadPolicyNotCompiled, FixupsNotCompiled, GeneratedTablesNotBuilt, ParameterActivationUnresolved,
    MissingArchivedMaster, MissingInitialResource, UnknownResourceSize, IncompleteResourcePayload,
    TablePlacementUnresolved, RuntimeResourceMappingUnresolved, ScriptEvaluationNotPerformed,
}
public sealed record LoadProcedureRequirement(LoadProcedureRequirementCode Code, string? ReferenceId = null);
public enum LoadProcedureTableRole { GroupAddress, Association, CommunicationObject }

/// <summary>Source code geometry and supplied byte lengths; Size never allocates or pads data.</summary>
public sealed record LoadProcedureResourceReference(string Id, ResourcePatchArena Arena, uint BaseOffset,
    uint? DeclaredSize, int? DataLength, int? MaskLength, string? DataHash, string? MaskHash);

/// <summary>A generated-table responsibility, without generated bytes, capacity inference or physical pointer resolution.</summary>
public sealed record LoadProcedureTableRequirement(LoadProcedureTableRole Role, string? CodeSegmentId, uint? Offset,
    uint? MaxEntries, ResourcePatchArena? Arena, uint? ByteOffsetInArena, string? MaskResourceFlavour,
    ArchivedResourceLocation? MaskResourceLocation);

/// <summary>Primary-program declarations only. This type cannot execute controls or become a device image.</summary>
public sealed class LoadProcedurePlan
{
    public int ProfileVersion { get; } = 1;
    public string Scope => "PrimaryProgramProcedure";
    public bool ExecutionEligible => false;
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public bool IsAvailable => Issues.Count == 0 && WitnessHash is not null;
    public string? ApplicationId { get; }
    public string? MaskVersionId { get; }
    public LoadProcedureStyle? Style { get; }
    public LoadProcedureRequest? Request { get; }
    public string? ApplicationDefinitionHash { get; }
    public string? SelectedProfileHash { get; }
    public string? WitnessHash { get; }
    public IReadOnlyList<LoadProcedureIssue> Issues { get; }
    public IReadOnlyList<LoadProcedureRequirement> Requirements { get; }
    public IReadOnlyList<LoadProcedureControl> Controls { get; }
    public IReadOnlyList<LoadMergeDecision> MergeDecisions { get; }
    public IReadOnlyList<LoadProcedureResourceReference> ResourceReferences { get; }
    public IReadOnlyList<LoadProcedureTableRequirement> TableRequirements { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LoadProcedureConditionalSelection? ConditionalSelection { get; }
    internal LoadProcedurePlan(LoadProcedureIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Requirements = Array.AsReadOnly(Array.Empty<LoadProcedureRequirement>());
        Controls = Array.AsReadOnly(Array.Empty<LoadProcedureControl>()); MergeDecisions = Array.AsReadOnly(Array.Empty<LoadMergeDecision>());
        ResourceReferences = Array.AsReadOnly(Array.Empty<LoadProcedureResourceReference>());
        TableRequirements = Array.AsReadOnly(Array.Empty<LoadProcedureTableRequirement>());
    }
    internal LoadProcedurePlan(string application, string? mask, LoadProcedureStyle style, LoadProcedureRequest request,
        string definitionHash, string? profileHash, string witnessHash, LoadProcedureControl[] controls,
        LoadMergeDecision[] decisions, LoadProcedureResourceReference[] resources, LoadProcedureTableRequirement[] tables,
        LoadProcedureRequirement[] requirements)
    {
        ApplicationId = application; MaskVersionId = mask; Style = style; Request = request;
        ApplicationDefinitionHash = definitionHash; SelectedProfileHash = profileHash; WitnessHash = witnessHash;
        Issues = Array.AsReadOnly(Array.Empty<LoadProcedureIssue>()); Requirements = Array.AsReadOnly(requirements);
        Controls = Array.AsReadOnly(controls); MergeDecisions = Array.AsReadOnly(decisions);
        ResourceReferences = Array.AsReadOnly(resources); TableRequirements = Array.AsReadOnly(tables);
    }
    internal LoadProcedurePlan(string application, string? mask, LoadProcedureStyle style, LoadProcedureRequest request,
        string definitionHash, string? profileHash, string witnessHash, LoadProcedureControl[] controls,
        LoadMergeDecision[] decisions, LoadProcedureResourceReference[] resources, LoadProcedureTableRequirement[] tables,
        LoadProcedureRequirement[] requirements, LoadProcedureConditionalSelection? selection)
        : this(application, mask, style, request, definitionHash, profileHash, witnessHash, controls, decisions, resources, tables, requirements)
    { ProfileVersion = 2; ConditionalSelection = selection; }
}

public enum LoadProcedureSourceOrigin { Application, ArchivedMaster }

/// <summary>Original element paths and a distinct insertion occurrence, without source XML.</summary>
public sealed class LoadProcedureSourceLocation
{
    public LoadProcedureSourceOrigin SourceOrigin { get; }
    public IReadOnlyList<int> SourcePath { get; }
    public LoadProcedureSourceOrigin? TemplateOrigin { get; }
    public IReadOnlyList<int>? TemplatePath { get; }
    public int? TemplateOrdinal { get; }
    public byte? MergeId { get; }
    public int ExpansionOrdinal { get; }
    public IReadOnlyList<int> AncestorDecisionOrdinals { get; }
    internal LoadProcedureSourceLocation(LoadProcedureSourceOrigin origin, IReadOnlyList<int> path,
        LoadProcedureSourceOrigin? templateOrigin, IReadOnlyList<int>? templatePath, int? templateOrdinal,
        byte? mergeId, int expansionOrdinal, IReadOnlyList<int> ancestors)
    {
        SourceOrigin = origin; SourcePath = Array.AsReadOnly(path.ToArray()); TemplateOrigin = templateOrigin;
        TemplatePath = templatePath is null ? null : Array.AsReadOnly(templatePath.ToArray()); TemplateOrdinal = templateOrdinal;
        MergeId = mergeId; ExpansionOrdinal = expansionOrdinal; AncestorDecisionOrdinals = Array.AsReadOnly(ancestors.ToArray());
    }
}

public sealed class LoadProcedureBranchDecision
{
    public int Ordinal { get; }
    public LoadProcedureSourceLocation Source { get; }
    public string ControllerRefId { get; }
    public bool ControllerActive { get; }
    public long? ControllerValue { get; }
    public IReadOnlyList<int> SelectedWhenOrdinals { get; }
    public bool UsedDefault { get; }
    internal LoadProcedureBranchDecision(int ordinal, LoadProcedureSourceLocation source, string reference, bool active,
        long? value, IReadOnlyList<int> selected, bool usedDefault)
    {
        Ordinal = ordinal; Source = source; ControllerRefId = reference; ControllerActive = active; ControllerValue = value;
        SelectedWhenOrdinals = Array.AsReadOnly(selected.ToArray()); UsedDefault = usedDefault;
    }
}

public sealed class LoadProcedureConditionalSelection
{
    public int ProfileVersion => 1;
    public string ControllerWitnessHash { get; }
    public IReadOnlyList<LoadProcedureBranchDecision> Decisions { get; }
    internal LoadProcedureConditionalSelection(string witness, IReadOnlyList<LoadProcedureBranchDecision> decisions)
    { ControllerWitnessHash = witness; Decisions = Array.AsReadOnly(decisions.ToArray()); }
}
