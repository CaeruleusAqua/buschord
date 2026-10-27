using System.Xml;
using System.Xml.Linq;
using System.Diagnostics.CodeAnalysis;

namespace Knx;

internal static class NativeImageInitializer
{
    private static readonly string[] PointerNames = ["GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr"];
    private sealed record PropertyTuple(PropertySourceSelector Raw, ushort Start, ushort Count);

    internal static NativeImageInitializationSet CompileAll(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeImageVariantViewSet views,
        NativeApplicationEvaluationContext application, NativeRootWriterPopulation population)
    {
        try
        {
            ValidatePair(project, device, inputs, inventory, preparation, views, application, population);
            var plans = new List<NativeImageInitializationPlan>();
            var compiled = new Dictionary<PreparedConditionalLoadProcedure, LoadProcedurePlan>(ReferenceEqualityComparer.Instance);
            long total = 0;
            foreach (var variant in preparation.Variants)
            {
                if (!variant.IsAvailable) { plans.Add(NativeImageInitializationPlan.Unavailable(variant, variant.Issues[0])); continue; }
                try
                {
                    var prepared = preparation.GetProcedurePreparation(variant); LoadProcedurePlan? procedure = null;
                    if (prepared is not null)
                    {
                        var view = views.GetView(variant);
                        if (view is null || !view.MatchesPrepared(prepared) || !view.MatchesCurrent(prepared)) Fail(NativeGeneralIssueCode.ContextMismatch);
                        if (!compiled.TryGetValue(prepared, out procedure))
                        {
                            procedure = LoadProcedureCompiler.CompileResolved(prepared, view);
                            if (!procedure.IsAvailable) throw new NativeGeneralRejected(new(NativeGeneralIssueCode.ProcedureUnavailable,
                                VariantOrdinal: variant.Ordinal, ProcedureIssue: procedure.Issues.FirstOrDefault()));
                            // Prepare already made the one fresh primitive compile; do not decode payloads again.
                            if (!prepared.RequiresExtendedProfile && !ReferenceEquals(procedure, prepared.Graph!.Legacy)) Fail(NativeGeneralIssueCode.ContextMismatch);
                            if (procedure.Request != prepared.Request || procedure.ApplicationId != prepared.ApplicationId
                                || procedure.MaskVersionId != prepared.MaskVersionId || procedure.SelectedProfileHash != prepared.SelectedProfileHash
                                || prepared.RequiresExtendedProfile && procedure.ApplicationDefinitionHash != prepared.ApplicationDefinitionHash)
                                Fail(NativeGeneralIssueCode.InputsChanged);
                            compiled.Add(prepared, procedure);
                        }
                        if (!view.MatchesCurrent(prepared)) Fail(NativeGeneralIssueCode.InputsChanged);
                    }
                    else if (variant.Selection.State is not (NativeImageProcedureState.BasicSkipped or NativeImageProcedureState.NativeEmpty))
                        Fail(NativeGeneralIssueCode.ProcedureUnavailable);
                    var actual = NativeImageCompiledProcedure.Mint(variant, procedure);
                    var selectedPopulation = population.ForVariant(variant);
                    if (!selectedPopulation.IsAvailable)
                        throw new NativeGeneralRejected(selectedPopulation.Issues.FirstOrDefault() ?? new(NativeGeneralIssueCode.ContextMismatch));
                    if (!selectedPopulation.BelongsTo(inventory, preparation, application, variant) || !selectedPopulation.MatchesCurrent())
                        Fail(NativeGeneralIssueCode.ContextMismatch);
                    var plan = Build(inputs, inventory, preparation, variant, views, application, population, selectedPopulation, actual);
                    total = checked(total + plan.EstimatedMaterializedPayloadBytes);
                    if (total > 16 * 1024 * 1024) Fail(NativeGeneralIssueCode.LimitExceeded);
                    plans.Add(plan);
                }
                catch (NativeGeneralRejected failure) when (failure.Issue.Code is not (NativeGeneralIssueCode.InputsChanged or NativeGeneralIssueCode.ContextMismatch or NativeGeneralIssueCode.LimitExceeded))
                { plans.Add(NativeImageInitializationPlan.Unavailable(variant, failure.Issue with { VariantOrdinal = variant.Ordinal })); }
            }
            ValidatePair(project, device, inputs, inventory, preparation, views, application, population);
            return NativeImageInitializationSet.Seal(preparation, plans.ToArray());
        }
        catch (NativeGeneralRejected failure) { return NativeImageInitializationSet.Unavailable(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return NativeImageInitializationSet.Unavailable(new(NativeGeneralIssueCode.InvalidDeclaration)); }
    }

    private static void ValidatePair(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeImageVariantViewSet views,
        NativeApplicationEvaluationContext application, NativeRootWriterPopulation population)
    {
        if (project is null || device is null || inputs is null || inventory is null || preparation is null || views is null || application is null || population is null
            || !inventory.IsAvailable || !preparation.IsAvailable || !views.IsAvailable || !application.IsAvailable || !population.IsAvailable
            || !inventory.BelongsTo(project, device, inputs) || !preparation.BelongsTo(project, device, inputs, inventory)
            || !views.BelongsTo(project, device, inputs, inventory, preparation, application) || !population.BelongsTo(inventory, preparation, application))
            Fail(NativeGeneralIssueCode.ContextMismatch);
        if (application.ScriptCapabilityUsed || application.Snapshot is null || application.Catalog is null || application.Activation is null
            || application.Snapshot.ModuleInstances.Count != 0 || !application.Activation.BelongsTo(application.Catalog, application.Snapshot))
            Fail(NativeGeneralIssueCode.ContextMismatch);
        if (!inventory.MatchesCurrent(project, device, inputs) || !preparation.MatchesCurrent() || !views.MatchesCurrent()
            || !application.MatchesInputs(project, device, inputs) || !population.MatchesCurrent()
            || application.CapturedInputs is null || !inventory.MatchesSaved(application.CapturedInputs.Values))
            Fail(NativeGeneralIssueCode.InputsChanged);
        var expected = preparation.ControllerReferences.Concat(application.Catalog!.StructuralPlan.CallControllerReferences)
            .Distinct(StringComparer.Ordinal).Take(4097).ToArray();
        if (expected.Length > 4096 || !application.AdditionalControllerReferences.SetEquals(expected)) Fail(NativeGeneralIssueCode.ContextMismatch);
    }

    private static NativeImageInitializationPlan Build(ArchivedImageSources.Inputs inputs, NativeRootImageInventory inventory,
        NativeImageVariantPreparation preparation, NativeImageVariant variant, NativeImageVariantViewSet views,
        NativeApplicationEvaluationContext application, NativeRootWriterPopulation population, NativeRootVariantPopulation selectedPopulation,
        NativeImageCompiledProcedure procedure)
    {
        var contributions = new List<NativeImageSourceContribution>(); var transitions = new List<NativeImagePostTransition>();
        var allocations = new List<NativeImageAllocationFact>(); var counts = new List<NativeImageCountObservation>();
        var app = NativeImagePreparation.Application(inputs.Application!, inventory.ApplicationId!);
        var context = inventory.ResourceContext;
        var sourceClosure = NativeGeneralHash.Of(new { inventory.SourceHash, inventory.HardwareDefinitionHash,
            resource = context?.Fingerprint, property = inventory.PropertyProfile?.Selection.Fingerprint, variant = variant.Fingerprint,
            procedure = procedure.Fingerprint, factory = inventory.FactorySources?.DecodedHash });
        if (variant.Tasks.AddCode)
        {
            if (context is not { Status: ArchivedResourceContextStatus.Supported, NumericMask: not null }) Fail(NativeGeneralIssueCode.SourceUnavailable);
            if (app.Descendants().Any(e => e.Name.LocalName.Contains("Fixup", StringComparison.OrdinalIgnoreCase) && (e.HasElements || e.HasAttributes || !string.IsNullOrWhiteSpace(e.Value))))
                Fail(NativeGeneralIssueCode.UnsupportedSource);
            foreach (var region in context!.MemoryRegions)
            {
                if (region.Optional == true || region.MemoryType != "EEPROM") continue;
                if (region.WriteAccess is null) Fail(NativeGeneralIssueCode.UnsupportedSource);
                if (region.WriteAccess is not ("Configuration" or "Manufacturer")) continue;
                var target = MemoryTarget(region.Location);
                Add(NativeImageContributionKind.ZeroRegion, "mask-region-" + contributions.Count, NativeGeneralHash.Of(region),
                    new(target, region.Location.Offset!.Value, Length(region.Length)), null, null, null, region.Length, Length(region.Length), context.Fingerprint);
            }
            foreach (var code in inventory.CodeSources)
            {
                var target = code.Arena.Kind == ResourcePatchArenaKind.AbsoluteMemory ? new NativeImageTarget(NativeImageSpace.StandardMemory)
                    : code.Arena.Kind == ResourcePatchArenaKind.RelativeMemory && code.Arena.LoadStateMachine is <= byte.MaxValue
                        ? new NativeImageTarget(NativeImageSpace.RelativeMemory, (byte)code.Arena.LoadStateMachine.Value)
                        : throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedMapping, ResourceId: code.SourceId));
                Add(NativeImageContributionKind.NativeCode, code.SourceId, NativeGeneralHash.Of(new { code.SourceId, code.Arena,
                    code.ByteOffsetInArena, code.DeclaredSize, data = code.RawData?.Sha256, mask = code.RawMask?.Sha256 }),
                    new(target, code.ByteOffsetInArena, code.AllocationLength), null, code.RawData, code.RawMask,
                    code.DeclaredSize, code.AllocationLength, NativeGeneralHash.Of(target));
            }
            if ((variant.RawOptions & 128) == 0)
                foreach (var name in PointerNames)
                {
                    var matching = context.Resources.Where(r => r.Name == name && r.Access.Contains("remote", StringComparer.Ordinal)).Take(2).ToArray();
                    // A missing declaration cannot prove the absence of additional-profile fallback sources.
                    if (matching.Length != 1 || matching[0].ManagementStyles.Count != 0) Fail(NativeGeneralIssueCode.UnsupportedMapping, resource: name);
                    var resource = matching[0];
                    if (resource.Location is not { AddressSpace: ArchivedResourceAddressSpace.SystemProperty, NativeObjectIndex: { } index,
                        PropertyId: { } pid, Offset: { } offset } || resource.DeclaredLength != 4) continue;
                    if (index > byte.MaxValue || pid > byte.MaxValue) Fail(NativeGeneralIssueCode.UnsupportedMapping, resource: name);
                    var raw = new PropertySourceSelector(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: index);
                    var selector = Normalize(raw, inventory);
                    Add(NativeImageContributionKind.PointerZero, name, NativeGeneralHash.Of(resource),
                        new(new(NativeImageSpace.PropertyData, Selector: selector), offset, 4), raw, null, null, 4, 4, context.Fingerprint);
                }
            if (procedure.DeclarationPlan?.Style != LoadProcedureStyle.DefaultProcedure)
                PropertyRecipes(procedure.NativeEffectiveControls, inventory, contributions, allocations);
            foreach (var family in contributions.Where(c => c.Range.Target.Space == NativeImageSpace.PropertyData)
                .Select(c => c.Range.Target.Selector!).GroupBy(s => s.PropertyId))
                if (family.Any(s => s.Kind == PropertySourceKind.ByType) && family.Any(s => s.Kind != PropertySourceKind.ByType))
                    Fail(NativeGeneralIssueCode.AmbiguousMapping);
            ValidateCodeOverlaps(contributions);
            if ((variant.RawOptions & 128) == 0) transitions.Add(new(NativeImagePostTransitionKind.ResetMasksToZero));
            transitions.Add(new(NativeImagePostTransitionKind.MaterializeNullMasks));
            var maskFamily = context.NumericMask.GetValueOrDefault() & 0xFF0;
            if (maskFamily is 0x10 or 0x20) transitions.Add(new(NativeImagePostTransitionKind.InvalidateStandardMemory,
                new(new(NativeImageSpace.StandardMemory), 279, 2)));
            else if (maskFamily == 0x700) transitions.Add(new(NativeImagePostTransitionKind.InvalidateStandardMemory,
                new(new(NativeImageSpace.StandardMemory), 16385, 2)));
            CountObservations(inventory, counts);
        }
        var sourceArray = contributions.ToArray(); var transitionArray = transitions.ToArray();
        var schedule = NativeImageFoldSchedule.Create(sourceArray, transitionArray);
        var coverage = Coverage(inventory, variant, selectedPopulation, contributions, schedule);
        return NativeImageInitializationPlan.Seal(inventory, preparation, variant, views, application, population, procedure,
            sourceArray, transitionArray, allocations.ToArray(), counts.ToArray(), coverage, sourceClosure, schedule);

        void Add(NativeImageContributionKind kind, string id, string declaration, NativeImageRange range, PropertySourceSelector? raw,
            ImmutablePatchBytes? data, ImmutablePatchBytes? mask, uint? declared, int requested, string mapping)
        {
            if (!range.IsValid || contributions.Count >= 4096) Fail(NativeGeneralIssueCode.LimitExceeded, resource: id);
            contributions.Add(new(kind, contributions.Count, id, declaration, range, raw, data, mask, declared, requested, mapping));
            allocations.Add(new(range, id, mapping, raw));
        }
    }

    private static void PropertyRecipes(IReadOnlyList<LoadProcedureControl> controls, NativeRootImageInventory inventory,
        List<NativeImageSourceContribution> contributions, List<NativeImageAllocationFact> allocations)
    {
        var tuples = new HashSet<PropertyTuple>();
        var groups = new Dictionary<PropertySourceSelector, List<(PropertySourceSelector Raw, LoadProcedureControl Control, ushort Start, ushort Count)>>();
        foreach (var control in controls.Where(c => c.Kind == LoadControlKind.WriteProperty))
        {
            var raw = ControlSelector(control); var start = checked((ushort)Unsigned(control, "StartElement")); var count = checked((ushort)Unsigned(control, "Count"));
            if (raw.PropertyId == 27 || start == 0 || count == 0) Fail(NativeGeneralIssueCode.UnsupportedControl);
            if (!tuples.Add(new(raw, start, count))) Fail(NativeGeneralIssueCode.AmbiguousOwnership);
            var target = Normalize(raw, inventory);
            if (!groups.TryGetValue(target, out var group)) groups.Add(target, group = []);
            group.Add((raw, control, start, count));
        }
        if (groups.Count > 128) Fail(NativeGeneralIssueCode.LimitExceeded);
        var byPid = groups.Keys.GroupBy(k => k.PropertyId);
        if (byPid.Any(g => g.Any(k => k.Kind == PropertySourceKind.ByType) && g.Any(k => k.Kind != PropertySourceKind.ByType))) Fail(NativeGeneralIssueCode.AmbiguousMapping);
        foreach (var group in groups)
        {
            var pdt = Layout(inventory, group.Key);
            if (pdt.Status != PropertyPdtStatus.KnownSymmetric || pdt.WriteElementBytes is not (> 0) || pdt.ReadElementBytes != pdt.WriteElementBytes)
                Fail(NativeGeneralIssueCode.UnsupportedMapping);
            var extent = group.Value.Max(g => checked((uint)g.Start + g.Count - 1));
            var requested = Length(checked((ulong)extent * pdt.WriteElementBytes!.Value));
            var factory = FactorySource(inventory, group.Key, out var ordinal);
            var length = Math.Max(requested, factory?.Data.Length ?? 0);
            if (length > 1024 * 1024) Fail(NativeGeneralIssueCode.LimitExceeded);
            var range = new NativeImageRange(new(NativeImageSpace.PropertyData, Selector: group.Key), 0, length);
            var id = ArchivedPropertySources.Key(group.Key);
            var mapping = NativeGeneralHash.Of(new { group.Key, pdt, extent, requested,
                controls = group.Value.Select(c => new { c.Raw, c.Control.Ordinal, c.Start, c.Count, c.Control.Provenance }).ToArray() });
            contributions.Add(new(NativeImageContributionKind.FactoryProperty, contributions.Count, id, mapping, range,
                group.Value[0].Raw, factory?.Data, factory?.Mask, (uint)requested, requested, mapping, pdt, ordinal));
            allocations.Add(new(range, id, mapping, group.Value[0].Raw, pdt, extent));
        }
    }

    private static NativeImageCoverageFact[] Coverage(NativeRootImageInventory inventory, NativeImageVariant variant,
        NativeRootVariantPopulation population, IReadOnlyList<NativeImageSourceContribution> contributions, NativeImageFoldSchedule schedule)
    {
        if (!variant.Tasks.Parameters) return [];
        var sourceRanges = schedule.UsesExtendedFolds ? schedule.Components : contributions.Select(c => c.Range);
        var ranges = sourceRanges.GroupBy(r => r.Target).ToDictionary(g => g.Key, g =>
        {
            var ordered = g.OrderBy(r => r.ByteOffset).ThenByDescending(r => r.Length).ToArray();
            var maxima = new ulong[ordered.Length]; ulong maximum = 0;
            for (var i = 0; i < ordered.Length; i++) maxima[i] = maximum = Math.Max(maximum, (ulong)ordered[i].ByteOffset + (uint)ordered[i].Length);
            return (Ranges: ordered, Maxima: maxima);
        });
        var requested = population.AllocationRequirements.Select(o => o.OwnerKey).ToHashSet(StringComparer.Ordinal);
        var result = new List<NativeImageCoverageFact>();
        foreach (var owner in inventory.Owners)
        {
            var length = checked((int)(((ulong)owner.BitOffset + owner.BitLength + 7) / 8));
            if (!ranges.TryGetValue(owner.Target, out var candidates)) Fail(NativeGeneralIssueCode.MissingAllocation, owner.ParameterId);
            var index = UpperBound(candidates.Ranges, owner.ByteOffset) - 1;
            var covered = index >= 0 && candidates.Maxima[index] >= (ulong)owner.ByteOffset + (uint)length;
            if (!covered) Fail(NativeGeneralIssueCode.UncoveredOwner, owner.ParameterId);
            result.Add(new(owner.OwnerKey, new(owner.Target, owner.ByteOffset, length), requested.Contains(owner.OwnerKey)));
        }
        return result.ToArray();
    }
    private static int UpperBound(NativeImageRange[] ranges, uint value)
    { var lo = 0; var hi = ranges.Length; while (lo < hi) { var mid = lo + (hi - lo) / 2; if (ranges[mid].ByteOffset <= value) lo = mid + 1; else hi = mid; } return lo; }

    private static void ValidateCodeOverlaps(List<NativeImageSourceContribution> contributions)
    {
        foreach (var group in contributions.Where(c => c.Kind == NativeImageContributionKind.NativeCode).GroupBy(c => c.Range.Target))
        {
            ulong end = 0; var first = true;
            foreach (var code in group.OrderBy(c => c.Range.ByteOffset))
            {
                if (!first && code.Range.ByteOffset < end) Fail(NativeGeneralIssueCode.AmbiguousOwnership, resource: code.SourceId);
                first = false; end = (ulong)code.Range.ByteOffset + (uint)code.Range.Length;
            }
        }
    }
    private static NativeImageTarget MemoryTarget(ArchivedResourceLocation location) => location.AddressSpace switch
    {
        ArchivedResourceAddressSpace.StandardMemory when location.Offset is not null => new(NativeImageSpace.StandardMemory),
        ArchivedResourceAddressSpace.RelativeMemory when location.Offset is not null && location.NativeObjectIndex is <= byte.MaxValue
            => new(NativeImageSpace.RelativeMemory, (byte)location.NativeObjectIndex.Value),
        _ => throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedMapping)),
    };
    private static PropertySourceSelector Normalize(PropertySourceSelector raw, NativeRootImageInventory inventory)
    {
        var profile = inventory.PropertyProfile;
        if (profile is null) Fail(NativeGeneralIssueCode.UnsupportedMapping);
        var normalized = ArchivedPropertySources.Normalize(raw, profile!.Selection);
        if (!normalized.Canonical) Fail(NativeGeneralIssueCode.UnsupportedMapping);
        return normalized.Selector;
    }
    private static PropertyPdtLayout Layout(NativeRootImageInventory inventory, PropertySourceSelector selector)
    {
        var profile = inventory.PropertyProfile;
        if (profile is null || selector.PropertyId > byte.MaxValue) Fail(NativeGeneralIssueCode.UnsupportedMapping);
        ParameterStorageLocation location;
        if (selector.Kind == PropertySourceKind.ByType && selector.ObjectType is { } type && selector.Occurrence is <= byte.MaxValue)
            location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByType, ObjectType: type,
                Occurrence: (byte)selector.Occurrence.Value, PropertyId: (byte)selector.PropertyId);
        else
        {
            uint? index = selector.Kind == PropertySourceKind.SystemProperty ? selector.NativeObjectIndex
                : selector.ApplicationObjectIndex is { } app && profile!.Selection.FirstAppObjectIndex is { } first ? (uint)app + first : null;
            if (index is null or > byte.MaxValue) Fail(NativeGeneralIssueCode.UnsupportedMapping);
            location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByIndex, ObjectIndex: (byte)index.Value, PropertyId: (byte)selector.PropertyId);
        }
        return PropertyPdtRoles.Read(profile!.Interpret(location, inventory.MaskVersionId));
    }
    private static PropertySourceSelector ControlSelector(LoadProcedureControl control)
    {
        var pid = Unsigned(control, "PropId"); var index = Optional(control, "ObjIdx"); var type = Optional(control, "ObjType"); var occurrence = Optional(control, "Occurrence") ?? 0;
        if (pid > byte.MaxValue || occurrence > byte.MaxValue || index is > byte.MaxValue || type is > ushort.MaxValue || (index is null) == (type is null))
            Fail(NativeGeneralIssueCode.UnsupportedMapping);
        return index is { } value ? new(PropertySourceKind.SystemProperty, (ushort)pid, NativeObjectIndex: (ushort)value)
            : new(PropertySourceKind.ByType, (ushort)pid, ObjectType: (ushort)type!.Value, Occurrence: (ushort)occurrence);
    }
    private static FactoryImageSegment? FactorySource(NativeRootImageInventory inventory, PropertySourceSelector target, out int? ordinal)
    {
        ordinal = null; var factory = inventory.FactorySources;
        if (factory is null || inventory.FactoryDeclared && (factory.Status != ArchivedFactoryImageStatus.Decoded || factory.Segments.Count == 0)
            || !inventory.FactoryDeclared && factory.Status != ArchivedFactoryImageStatus.Missing) Fail(NativeGeneralIssueCode.UnsupportedSource);
        FactoryImageSegment? selected = null;
        for (var i = 0; i < factory!.Segments.Count; i++)
        {
            var segment = factory.Segments[i]; var raw = FactorySelector(segment.Location);
            if (raw is null || raw.PropertyId != target.PropertyId) continue;
            if ((raw.Kind == PropertySourceKind.ByType) != (target.Kind == PropertySourceKind.ByType)) Fail(NativeGeneralIssueCode.AmbiguousMapping);
            if (Normalize(raw, inventory) != target) continue;
            if (raw != target) Fail(NativeGeneralIssueCode.AmbiguousMapping);
            if (segment.Location.Component == FactoryImageComponent.PropertyElementCount) continue;
            if (segment.Location.ByteOffset != 0 || selected is not null) Fail(NativeGeneralIssueCode.AmbiguousOwnership);
            selected = segment; ordinal = i;
        }
        return selected;
    }
    private static void CountObservations(NativeRootImageInventory inventory, List<NativeImageCountObservation> counts)
    {
        if (inventory.FactorySources is not { } factory) return;
        for (var i = 0; i < factory.Segments.Count; i++)
        {
            var segment = factory.Segments[i]; if (segment.Location.Component != FactoryImageComponent.PropertyElementCount) continue;
            if (segment.Data.Length != 2 || segment.Mask is { Length: not 2 }) Fail(NativeGeneralIssueCode.InvalidDeclaration);
            counts.Add(new(i, segment.Location, segment.Data, segment.Mask, (ushort)((segment.Data[0] << 8) | segment.Data[1]),
                segment.Mask is null ? PropertyCountStatus.MaskMissing : segment.Mask[0] == 255 && segment.Mask[1] == 255 ? PropertyCountStatus.FullyValid : PropertyCountStatus.MaskPartial));
        }
    }
    private static PropertySourceSelector? FactorySelector(FactoryImageLocation location) => location.AddressSpace switch
    {
        FactoryImageAddressSpace.SystemProperty when location.NativeObjectIndex is { } index && location.PropertyId is { } pid
            => new(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: index),
        FactoryImageAddressSpace.AppProperty when location.ApplicationObjectIndex is { } index && location.PropertyId is { } pid
            => new(PropertySourceKind.AppProperty, pid, ApplicationObjectIndex: index),
        FactoryImageAddressSpace.PropertyByType when location.ObjectType is { } type && location.Occurrence is { } occurrence && location.PropertyId is { } pid
            => new(PropertySourceKind.ByType, pid, ObjectType: type, Occurrence: occurrence),
        _ => null,
    };
    private static ulong Unsigned(LoadProcedureControl control, string name) => Optional(control, name)
        ?? throw new NativeGeneralRejected(new(NativeGeneralIssueCode.InvalidDeclaration));
    private static ulong? Optional(LoadProcedureControl control, string name)
    {
        var args = control.Arguments.Where(a => a.Name == name).Take(2).ToArray();
        if (args.Length == 0) return null;
        if (args.Length != 1 || args[0].Kind != LoadControlArgumentKind.Unsigned || args[0].UnsignedValue is null) Fail(NativeGeneralIssueCode.InvalidDeclaration);
        return args[0].UnsignedValue;
    }
    private static int Length(ulong length)
    { if (length == 0 || length > 1024 * 1024) Fail(NativeGeneralIssueCode.LimitExceeded); return checked((int)length); }
    [DoesNotReturn] private static void Fail(NativeGeneralIssueCode code, string? reference = null, string? resource = null) => NativeGeneralHash.Fail(code, reference, resource);
}
