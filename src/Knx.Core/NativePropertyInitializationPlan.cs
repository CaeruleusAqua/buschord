namespace Knx;

public enum PropertyInitializationIssueCode
{
    InvalidRequest, InputsChanged, ContextMismatch, ProcedureUnavailable, SourceUnavailable,
    UnsupportedInitialSource, UnsupportedControl, InvalidControl, DuplicateControlGroup,
    UnsupportedSelector, UnsupportedPdt, AmbiguousFactorySource, FactoryUnavailable,
    MissingAllocation, UncoveredOwner, LimitExceeded, InvalidDeclaration,
}

/// <summary>A source-initialization diagnostic, without a device operation or inferred before-state.</summary>
public sealed record PropertyInitializationIssue(PropertyInitializationIssueCode Code, string? RefId = null,
    string? PropertyKey = null, string? ResourceId = null, LoadProcedureIssue? ProcedureIssue = null,
    ArchivedFactoryImageStatus? FactoryStatus = null, string? FactoryBlobHash = null);

internal sealed record NativePropertyAllocationControl(int Ordinal, ushort StartElement, ushort Count,
    LoadProcedureSourceLocation? Provenance);

internal sealed class NativePropertyAllocation
{
    internal PropertySourceSelector RawSelector { get; }
    internal PropertySourceSelector NativeSelector { get; }
    internal string PropertyKey { get; }
    internal PropertyPdtLayout PdtLayout { get; }
    internal IReadOnlyList<NativePropertyAllocationControl> Controls { get; }
    internal uint ElementExtent { get; }
    internal int ControlBytes { get; }
    internal int? FactorySourceOrdinal { get; }
    internal FactoryImageLocation? FactoryLocation { get; }
    internal int? FactoryDataLength { get; }
    internal string? FactoryDataHash { get; }
    internal string? FactoryMaskHash { get; }
    internal ImmutablePatchBytes InitialData { get; }
    internal ImmutablePatchBytes InitialValidity { get; }
    internal int Length => InitialData.Length;

    internal NativePropertyAllocation(PropertySourceSelector raw, PropertySourceSelector native, string key,
        PropertyPdtLayout pdt, IReadOnlyList<NativePropertyAllocationControl> controls, uint extent, int controlBytes,
        int? factoryOrdinal, FactoryImageSegment? factory, ImmutablePatchBytes data, ImmutablePatchBytes validity)
    {
        RawSelector = raw; NativeSelector = native; PropertyKey = key; PdtLayout = pdt;
        Controls = Array.AsReadOnly(controls.ToArray()); ElementExtent = extent; ControlBytes = controlBytes;
        FactorySourceOrdinal = factoryOrdinal; FactoryLocation = factory?.Location; FactoryDataLength = factory?.Data.Length;
        FactoryDataHash = factory?.Data.Sha256; FactoryMaskHash = factory?.Mask?.Sha256;
        InitialData = data; InitialValidity = validity;
    }
}

internal sealed record NativePropertyCountObservation(int SourceOrdinal, PropertySourceSelector RawSelector,
    PropertySourceSelector NativeSelector, string PropertyKey, ImmutablePatchBytes Data, ImmutablePatchBytes? Mask,
    ushort? EncodedCount, PropertyCountStatus Status)
{ internal bool NotAdopted => true; }

/// <summary>Privately paired working allocations. Raw data and zero validity have separate roles.</summary>
internal sealed class NativePropertyInitializationPlan
{
    private readonly ArchivedImageSources.Inputs? _inputs;
    private readonly PreparedConditionalLoadProcedure? _prepared;
    private readonly NativeProcedureEvaluationContext? _view;
    private readonly NativeApplicationEvaluationContext? _application;
    private readonly RootPropertyWriterSources? _sources;
    internal bool IsAvailable => Issues.Count == 0 && Fingerprint is not null;
    internal IReadOnlyList<PropertyInitializationIssue> Issues { get; }
    internal LoadProcedurePlan? ActualProcedure { get; }
    internal IReadOnlyList<NativePropertyAllocation> Allocations { get; }
    internal IReadOnlyList<NativePropertyCountObservation> Counts { get; }
    internal string? SourceClosureHash { get; }
    internal string? AllocationHash { get; }
    internal string? CoverageHash { get; }
    internal string? Fingerprint { get; }
    internal long ReservedPayloadBytes { get; }

    private NativePropertyInitializationPlan(PropertyInitializationIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Allocations = Array.AsReadOnly(Array.Empty<NativePropertyAllocation>());
        Counts = Array.AsReadOnly(Array.Empty<NativePropertyCountObservation>());
    }
    private NativePropertyInitializationPlan(ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared,
        NativeProcedureEvaluationContext view, NativeApplicationEvaluationContext application, RootPropertyWriterSources sources,
        LoadProcedurePlan procedure, NativePropertyAllocation[] allocations, NativePropertyCountObservation[] counts,
        string closure, string allocation, string coverage, string fingerprint, long reserved)
    {
        _inputs = inputs; _prepared = prepared; _view = view; _application = application; _sources = sources;
        ActualProcedure = procedure; Allocations = Array.AsReadOnly(allocations); Counts = Array.AsReadOnly(counts);
        Issues = Array.AsReadOnly(Array.Empty<PropertyInitializationIssue>()); SourceClosureHash = closure;
        AllocationHash = allocation; CoverageHash = coverage; Fingerprint = fingerprint; ReservedPayloadBytes = reserved;
    }
    internal static NativePropertyInitializationPlan Unavailable(PropertyInitializationIssue issue) => new(issue);
    internal static NativePropertyInitializationPlan Success(ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared,
        NativeProcedureEvaluationContext view, NativeApplicationEvaluationContext application, RootPropertyWriterSources sources,
        LoadProcedurePlan procedure, NativePropertyAllocation[] allocations, NativePropertyCountObservation[] counts,
        string closure, string allocation, string coverage, string fingerprint, long reserved)
        => new(inputs, prepared, view, application, sources, procedure, allocations, counts, closure, allocation, coverage, fingerprint, reserved);
    internal bool BelongsTo(ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared,
        NativeProcedureEvaluationContext view, NativeApplicationEvaluationContext application, RootPropertyWriterSources sources)
        => IsAvailable && ReferenceEquals(_inputs, inputs) && ReferenceEquals(_prepared, prepared)
            && ReferenceEquals(_view, view) && ReferenceEquals(_application, application) && ReferenceEquals(_sources, sources);
}
