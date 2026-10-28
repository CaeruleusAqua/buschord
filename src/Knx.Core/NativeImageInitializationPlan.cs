using System.Collections.Frozen;

namespace Knx;

internal enum NativeImageContributionKind { ZeroRegion, NativeCode, PointerZero, FactoryProperty }
internal sealed record NativeImageSourceContribution(NativeImageContributionKind Kind, int Order, string SourceId,
    string DeclarationHash, NativeImageRange Range, PropertySourceSelector? RawSelector,
    ImmutablePatchBytes? RawData, ImmutablePatchBytes? RawMask, uint? DeclaredLength, int RequestedLength,
    string MappingHash, PropertyPdtLayout? PdtLayout = null, int? FactorySourceOrdinal = null);
internal enum NativeImagePostTransitionKind { ResetMasksToZero, MaterializeNullMasks, InvalidateStandardMemory }
internal sealed record NativeImagePostTransition(NativeImagePostTransitionKind Kind, NativeImageRange? Range = null);
internal sealed record NativeImageTaskPredicates(bool BasicProcedure, bool AddCode, bool Parameters, bool Tables);
internal enum NativeConditionalBooleanOrigin { SourceProven, ConditionalAssumption }
internal sealed record NativeConditionalBooleanFact(bool Value, NativeConditionalBooleanOrigin Origin);
internal enum NativeImageProcedureState { BasicSkipped, NativeEmpty, ProductRoot, MaskTemplate, Unavailable }
internal sealed record NativeImageProcedureSelection(NativeImageProcedureState State, uint SelectionOptions,
    string? NativeSubtype, int? SourceOrdinal, string? DeclarationHash, int? Rank, string Normalization);

internal sealed class NativeImageVariant
{
    internal int Ordinal { get; }
    internal uint RawOptions { get; }
    internal uint SelectionOptions => Selection.SelectionOptions;
    internal NativeConditionalBooleanFact? ToolManagedIpFact { get; }
    internal NativeImageTaskPredicates Tasks { get; }
    internal bool ExcludeCustomerParameters => (RawOptions & 1024) != 0;
    internal NativeImageProcedureSelection Selection { get; }
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal string Fingerprint { get; }
    internal bool IsAvailable => Issues.Count == 0;
    internal NativeImageVariant(int ordinal, uint options, NativeConditionalBooleanFact? toolIp,
        NativeImageTaskPredicates tasks, NativeImageProcedureSelection selection, NativeGeneralIssue? issue = null)
    {
        Ordinal = ordinal; RawOptions = options; ToolManagedIpFact = toolIp; Tasks = tasks; Selection = selection;
        Issues = Array.AsReadOnly(issue is null ? [] : new[] { issue! });
        Fingerprint = NativeGeneralHash.Of(new { ordinal, options, toolIp, tasks, selection, issues = Issues });
    }
}

/// <summary>Private source preparation for explicitly conditional option words.</summary>
internal sealed class NativeImageVariantPreparation
{
    private readonly Project? _project;
    private readonly DeviceInstance? _device;
    private readonly ArchivedImageSources.Inputs? _inputs;
    private readonly NativeRootImageInventory? _inventory;
    private readonly IReadOnlyDictionary<int, PreparedConditionalLoadProcedure> _procedures;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal IReadOnlyList<uint> AssumedOptions { get; }
    internal IReadOnlyList<NativeImageVariant> Variants { get; }
    internal IReadOnlySet<string> ControllerReferences { get; }
    internal string? SourceFingerprint { get; }
    internal string? InventoryHash { get; }
    internal string? SelectionHash { get; }
    internal string? Fingerprint { get; }
    internal long DecodedOperandBytes { get; }
    internal long ReservedProcedurePayloadBytes => checked(DecodedOperandBytes * 4);
    internal bool HasActualRoute => false;

    private NativeImageVariantPreparation(NativeGeneralIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); AssumedOptions = Array.AsReadOnly(Array.Empty<uint>());
        Variants = Array.AsReadOnly(Array.Empty<NativeImageVariant>());
        ControllerReferences = new HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);
        _procedures = new Dictionary<int, PreparedConditionalLoadProcedure>().ToFrozenDictionary();
    }
    private NativeImageVariantPreparation(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, uint[] options, NativeImageVariant[] variants,
        Dictionary<int, PreparedConditionalLoadProcedure> procedures, string[] controllers, long operands)
    {
        _project = project; _device = device; _inputs = inputs; _inventory = inventory;
        Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>());
        AssumedOptions = Array.AsReadOnly((uint[])options.Clone()); Variants = Array.AsReadOnly((NativeImageVariant[])variants.Clone());
        _procedures = procedures.ToFrozenDictionary(); ControllerReferences = controllers.ToFrozenSet(StringComparer.Ordinal);
        SourceFingerprint = inputs.Manifest.Binding!.Fingerprint; InventoryHash = inventory.SourceHash; DecodedOperandBytes = operands;
        SelectionHash = NativeGeneralHash.Of(variants.Select(v => new { v.Ordinal, v.RawOptions, v.Fingerprint,
            prepared = procedures.GetValueOrDefault(v.Ordinal)?.GraphFingerprint }).ToArray());
        Fingerprint = NativeGeneralHash.Of(new { profile = 1, SourceFingerprint, InventoryHash, options, SelectionHash,
            controllers, operands, reserve = ReservedProcedurePayloadBytes });
    }
    internal static NativeImageVariantPreparation Unavailable(NativeGeneralIssue issue) => new(issue);
    internal static NativeImageVariantPreparation Seal(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, uint[] options, NativeImageVariant[] variants,
        Dictionary<int, PreparedConditionalLoadProcedure> procedures, string[] controllers, long operands)
        => new(project, device, inputs, inventory, options, variants, procedures, controllers, operands);
    internal PreparedConditionalLoadProcedure? GetProcedurePreparation(NativeImageVariant variant)
        => Contains(variant) ? _procedures.GetValueOrDefault(variant.Ordinal) : null;
    internal bool Contains(NativeImageVariant variant) => variant is not null && variant.Ordinal >= 0 && variant.Ordinal < Variants.Count
        && ReferenceEquals(Variants[variant.Ordinal], variant);
    internal bool BelongsTo(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, NativeRootImageInventory inventory)
        => IsAvailable && ReferenceEquals(project, _project) && ReferenceEquals(device, _device)
            && ReferenceEquals(inputs, _inputs) && ReferenceEquals(inventory, _inventory);
    internal bool MatchesCurrent() => _project is not null && _device is not null && _inputs is not null && _inventory is not null
        && BelongsTo(_project, _device, _inputs, _inventory) && _inventory.MatchesCurrent(_project, _device, _inputs)
        && _inputs.Manifest.Binding?.Fingerprint == SourceFingerprint && _inventory.SourceHash == InventoryHash
        && _procedures.Values.All(p => p.MatchesSources(_inputs));
}

internal enum NativeImageCompiledProcedureState { BasicSkipped, NativeEmpty, Compiled }
internal sealed class NativeImageCompiledProcedure
{
    internal NativeImageCompiledProcedureState State { get; }
    internal LoadProcedurePlan? DeclarationPlan { get; }
    internal IReadOnlyList<LoadProcedureControl> NativeEffectiveControls { get; }
    internal string Fingerprint { get; }
    private NativeImageCompiledProcedure(NativeImageVariant variant, LoadProcedurePlan? plan, LoadProcedureControl[] controls)
    {
        State = variant.Selection.State == NativeImageProcedureState.BasicSkipped ? NativeImageCompiledProcedureState.BasicSkipped
            : variant.Selection.State == NativeImageProcedureState.NativeEmpty ? NativeImageCompiledProcedureState.NativeEmpty : NativeImageCompiledProcedureState.Compiled;
        DeclarationPlan = plan; NativeEffectiveControls = Array.AsReadOnly(controls);
        Fingerprint = NativeGeneralHash.Of(new { variant = variant.Fingerprint, state = State, declaration = plan?.WitnessHash,
            controls = controls.Select(c => new { c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId,
                c.DeclaredAppliesTo, c.EffectiveAppliesTo, c.Arguments, c.OnError,
                data = c.InlineData?.Sha256, mask = c.InlineMask?.Sha256, c.Provenance }).ToArray() });
    }
    internal static NativeImageCompiledProcedure Mint(NativeImageVariant variant, LoadProcedurePlan? plan)
    {
        LoadProcedureControl Copy(LoadProcedureControl c)
        {
            var effective = c.Origin == LoadControlOrigin.Mask ? LoadControlCondition.All : c.EffectiveAppliesTo;
            return c.Provenance is { } provenance
                ? new(c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId, c.DeclaredAppliesTo, effective,
                    c.Arguments.ToArray(), c.OnError.ToArray(), c.InlineData, c.InlineMask, provenance)
                : new(c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId, c.DeclaredAppliesTo, effective,
                    c.Arguments.ToArray(), c.OnError.ToArray(), c.InlineData, c.InlineMask);
        }
        var controls = plan?.Controls.Select(Copy).ToArray() ?? [];
        return new(variant, plan, controls);
    }
}

internal sealed record NativeImageAllocationFact(NativeImageRange Range, string SourceId, string MappingHash,
    PropertySourceSelector? RawSelector = null, PropertyPdtLayout? PdtLayout = null, uint? ElementExtent = null);
internal sealed record NativeImageCoverageFact(string OwnerKey, NativeImageRange Range, bool IsReturnedRecord);
internal sealed record NativeImageCountObservation(int SourceOrdinal, FactoryImageLocation RawLocation,
    ImmutablePatchBytes Data, ImmutablePatchBytes? Mask, ushort EncodedCount, PropertyCountStatus Status)
{ internal bool NotAdopted => true; }

internal sealed class NativeImageInitializationPlan
{
    private readonly NativeRootImageInventory? _inventory;
    private readonly NativeImageVariantPreparation? _preparation;
    private readonly NativeImageVariantViewSet? _views;
    private readonly NativeApplicationEvaluationContext? _application;
    private readonly NativeRootWriterPopulation? _population;
    internal NativeImageVariant Variant { get; }
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal NativeImageCompiledProcedure? CompiledProcedure { get; }
    internal IReadOnlyList<NativeImageSourceContribution> Contributions { get; }
    internal IReadOnlyList<NativeImagePostTransition> PostSourceTransitions { get; }
    internal IReadOnlyList<NativeImageAllocationFact> Allocations { get; }
    internal IReadOnlyList<NativeImageCountObservation> ObservedCounts { get; }
    internal IReadOnlyList<NativeImageCoverageFact> CoverageFacts { get; }
    internal IReadOnlyList<string> Requirements { get; }
    internal string? SourceClosureHash { get; }
    internal string? SelectionHash { get; }
    internal string? RecipeHash { get; }
    internal string? CoverageHash { get; }
    internal string? Fingerprint { get; }
    internal long EstimatedMaterializedPayloadBytes { get; }
    internal NativeImageFoldSchedule? FoldSchedule { get; }
    internal long ReservedMaterializationPayloadBytes => FoldSchedule?.ReservedMaterializationPayloadBytes ?? 0;
    private NativeImageInitializationPlan(NativeImageVariant variant, NativeGeneralIssue issue)
    {
        Variant = variant; Issues = Array.AsReadOnly(new[] { issue });
        Contributions = Array.AsReadOnly(Array.Empty<NativeImageSourceContribution>());
        PostSourceTransitions = Array.AsReadOnly(Array.Empty<NativeImagePostTransition>());
        Allocations = Array.AsReadOnly(Array.Empty<NativeImageAllocationFact>());
        ObservedCounts = Array.AsReadOnly(Array.Empty<NativeImageCountObservation>());
        CoverageFacts = Array.AsReadOnly(Array.Empty<NativeImageCoverageFact>()); Requirements = Array.AsReadOnly(Array.Empty<string>());
    }
    private NativeImageInitializationPlan(NativeRootImageInventory inventory, NativeImageVariantPreparation preparation,
        NativeImageVariant variant, NativeImageVariantViewSet views, NativeApplicationEvaluationContext application,
        NativeRootWriterPopulation population, NativeImageCompiledProcedure procedure,
        NativeImageSourceContribution[] contributions, NativeImagePostTransition[] transitions, NativeImageAllocationFact[] allocations,
        NativeImageCountObservation[] counts, NativeImageCoverageFact[] coverage, string closure, NativeImageFoldSchedule schedule)
    {
        _inventory = inventory; _preparation = preparation; _views = views; _application = application; _population = population;
        Variant = variant; Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>()); CompiledProcedure = procedure;
        Contributions = Array.AsReadOnly(contributions); PostSourceTransitions = Array.AsReadOnly(transitions);
        Allocations = Array.AsReadOnly(allocations); ObservedCounts = Array.AsReadOnly(counts); CoverageFacts = Array.AsReadOnly(coverage);
        if (!schedule.Matches(Contributions, PostSourceTransitions)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        FoldSchedule = schedule;
        Requirements = Array.AsReadOnly(new[] { "PhysicalMappingUnverified", "DeviceStateUnverified", "ConfigurationWritersNotApplied", "FinalizationNotApplied" });
        EstimatedMaterializedPayloadBytes = contributions.Sum(c => (long)c.Range.Length);
        SourceClosureHash = closure; SelectionHash = variant.Fingerprint;
        RecipeHash = NativeGeneralHash.Of(new { contributions = contributions.Select(c => new { c.Kind, c.Order, c.SourceId,
            c.DeclarationHash, c.Range, c.RawSelector, data = c.RawData?.Sha256, mask = c.RawMask?.Sha256,
            c.DeclaredLength, c.RequestedLength, c.MappingHash, c.PdtLayout, c.FactorySourceOrdinal }).ToArray(), transitions, allocations,
            counts = counts.Select(c => new { c.SourceOrdinal, c.RawLocation, data = c.Data.Sha256, mask = c.Mask?.Sha256,
                c.EncodedCount, c.Status }).ToArray() });
        CoverageHash = NativeGeneralHash.Of(coverage);
        var baselineFingerprint = NativeGeneralHash.Of(new { profile = 1, inventory = inventory.SourceHash, preparation = preparation.Fingerprint,
            variant = variant.Fingerprint, views = views.Fingerprint, application = application.Fingerprint, population = population.Fingerprint,
            procedure = procedure.Fingerprint, closure, RecipeHash, CoverageHash, Requirements, EstimatedMaterializedPayloadBytes });
        Fingerprint = schedule.UsesExtendedFolds
            ? NativeGeneralHash.Of(new { profile = 1, baselineFingerprint, foldSchedule = schedule.Fingerprint }) : baselineFingerprint;
    }
    internal static NativeImageInitializationPlan Unavailable(NativeImageVariant variant, NativeGeneralIssue issue) => new(variant, issue);
    internal static NativeImageInitializationPlan Seal(NativeRootImageInventory inventory, NativeImageVariantPreparation preparation,
        NativeImageVariant variant, NativeImageVariantViewSet views, NativeApplicationEvaluationContext application,
        NativeRootWriterPopulation population, NativeImageCompiledProcedure procedure, NativeImageSourceContribution[] contributions,
        NativeImagePostTransition[] transitions, NativeImageAllocationFact[] allocations, NativeImageCountObservation[] counts,
        NativeImageCoverageFact[] coverage, string closure, NativeImageFoldSchedule schedule)
        => new(inventory, preparation, variant, views, application, population, procedure, contributions, transitions, allocations, counts, coverage, closure, schedule);
    internal bool BelongsTo(NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeImageVariant variant,
        NativeImageVariantViewSet views, NativeApplicationEvaluationContext application, NativeRootWriterPopulation population)
        => IsAvailable && ReferenceEquals(_inventory, inventory) && ReferenceEquals(_preparation, preparation) && ReferenceEquals(Variant, variant)
            && ReferenceEquals(_views, views) && ReferenceEquals(_application, application) && ReferenceEquals(_population, population);
    internal bool MatchesCurrent() => IsAvailable && _preparation!.MatchesCurrent() && _views!.MatchesCurrent() && _population!.MatchesCurrent();
    internal bool AcceptsPopulation(NativeRootVariantPopulation population)
        => IsAvailable && population is not null && population.BelongsTo(_inventory!, _preparation!, _application!, Variant) && population.MatchesCurrent();
}

internal sealed class NativeImageInitializationSet
{
    internal bool HasGlobalFailure { get; }
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal IReadOnlyList<NativeImageInitializationPlan> Variants { get; }
    internal bool AllRequestedVariantsAvailable => !HasGlobalFailure && Variants.Count != 0 && Variants.All(v => v.IsAvailable);
    internal string? Fingerprint { get; }
    private NativeImageInitializationSet(NativeGeneralIssue issue)
    { HasGlobalFailure = true; Issues = Array.AsReadOnly(new[] { issue }); Variants = Array.AsReadOnly(Array.Empty<NativeImageInitializationPlan>()); }
    private NativeImageInitializationSet(NativeImageVariantPreparation preparation, NativeImageInitializationPlan[] plans)
    {
        Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>()); Variants = Array.AsReadOnly(plans);
        Fingerprint = NativeGeneralHash.Of(new { preparation = preparation.Fingerprint,
            variants = plans.Select(p => new { p.Variant.Ordinal, p.Variant.Fingerprint, p.IsAvailable, p.Issues, plan = p.Fingerprint }).ToArray() });
    }
    internal static NativeImageInitializationSet Unavailable(NativeGeneralIssue issue) => new(issue);
    internal static NativeImageInitializationSet Seal(NativeImageVariantPreparation preparation, NativeImageInitializationPlan[] plans) => new(preparation, plans);
}
