namespace Knx;

internal sealed partial class NativeRootWriterPopulation
{
    private readonly NativeRootImageInventory? _inventory;
    private readonly NativeImageVariantPreparation? _preparation;
    private readonly NativeApplicationEvaluationContext? _application;
    private readonly IReadOnlyList<NativeRootHydratedReference> _values = Array.Empty<NativeRootHydratedReference>();
    private readonly IReadOnlyDictionary<string, int> _active = new Dictionary<string, int>();
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; } = Array.Empty<NativeGeneralIssue>();
    internal string? Fingerprint { get; }
    internal string? CurrentValuesHash { get; }
    internal string? ActivityHash { get; }
    internal string? GeometryHash => _inventory?.GeometryHash;
    private NativeRootWriterPopulation(NativeGeneralIssue issue) { Issues = Array.AsReadOnly(new[] { issue }); }
    internal static NativeRootWriterPopulation Create(NativeRootImageInventory inventory,
        NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application)
    {
        try { return new(inventory, preparation, application); }
        catch (NativeGeneralRejected failure) { return new(failure.Issue); }
        catch (RootBuildRejected failure) { return new(new(NativeGeneralIssueCode.InvalidValue, failure.Issue.RefId, ParameterIssue: failure.Issue.ParameterIssue)); }
        catch (Exception failure) when (failure is FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return new(new(NativeGeneralIssueCode.InvalidDeclaration)); }
    }
    private NativeRootWriterPopulation(NativeRootImageInventory inventory, NativeImageVariantPreparation preparation,
        NativeApplicationEvaluationContext application)
    {
        if (inventory is null || preparation is null || application is null || !inventory.IsAvailable || !preparation.IsAvailable
            || !inventory.MatchesPreparation(preparation) || !inventory.MatchesCurrent() || !preparation.MatchesCurrent())
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
        if (!application.IsAvailable || application.Catalog is null || application.Snapshot is null || application.Activation is null
            || application.CapturedInputs is null || application.ScriptCapabilityUsed || !inventory.MatchesModules(application)
            || !application.Activation.BelongsTo(application.Catalog, application.Snapshot)
            || !inventory.MatchesApplication(application)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        var expected = preparation.ControllerReferences.Concat(application.Catalog!.StructuralPlan.CallControllerReferences)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(4097).ToArray();
        if (expected.Length > 4096 || !application.AdditionalControllerReferences.SetEquals(expected)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        if (!inventory.MatchesSaved(application.CapturedInputs!.Values)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
        _inventory = inventory; _preparation = preparation; _application = application;
        var ids = inventory.References.Select(r => r.RefId).ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal); var seen = new HashSet<NativeStateIdentity>();
        foreach (var owner in application.Activation!.ActiveParameterOwners)
        {
            var original = application.Catalog.At(owner.SourceKey); var id = (string?)original?.Attribute("RefId");
            if (owner.ModuleInstanceId.Length != 0 && id is not null)
                id = inventory.Modules?.ResolveReference(owner.ModuleInstanceId, id) ?? throw new NativeGeneralRejected(new(NativeGeneralIssueCode.ContextMismatch));
            if (original?.Name.LocalName != "ParameterRefRef" || id is null || !ids.Contains(id)
                || !seen.Add(owner)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            counts[id!] = checked(counts.GetValueOrDefault(id!) + 1);
        }
        _active = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(counts);
        _values = inventory.Hydrate(application);
        CurrentValuesHash = NativeGeneralHash.Of(_values.Select(v => new { v.Reference.RefId, v.Origin, v.CurrentHash, v.DefaultHash, v.CurrentWord, v.DefaultWord }).ToArray());
        if (_values.Any(v => v.CurrentBytes is not null)) CurrentValuesHash = NativeGeneralHash.Of(new { profile = 4, previous = CurrentValuesHash,
            bytes = _values.Where(v => v.CurrentBytes is not null).Select(v => new { v.Reference.RefId, current = v.CurrentBytes!.Sha256, fallback = v.DefaultBytes!.Sha256 }).ToArray() });
        ActivityHash = NativeGeneralHash.Of(inventory.References.Select(r => new { r.RefId, normal = counts.GetValueOrDefault(r.RefId) }).ToArray());
        Fingerprint = NativeGeneralHash.Of(new { profile = 1, inventory.SourceHash, inventory.SavedValuesHash, inventory.CarrierHash,
            preparation = preparation.Fingerprint, application = application.Fingerprint, CurrentValuesHash, ActivityHash, inventory.GeometryHash });
        if (!MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
    }
    internal bool BelongsTo(NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application)
        => IsAvailable && ReferenceEquals(_inventory, inventory) && ReferenceEquals(_preparation, preparation) && ReferenceEquals(_application, application);
    internal bool MatchesCurrent() => IsAvailable && _inventory is not null && _preparation is not null && _application is not null
        && _inventory.MatchesCurrent() && _preparation.MatchesCurrent() && _inventory.MatchesApplication(_application)
        && _application.CapturedInputs is not null && _inventory.MatchesSaved(_application.CapturedInputs.Values);
    internal NativeRootVariantPopulation ForVariant(NativeImageVariant variant)
    {
        try
        {
            if (!IsAvailable || _preparation is null || !_preparation.Contains(variant) || !MatchesCurrent())
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            if (!variant.IsAvailable) return NativeRootVariantPopulation.Unavailable(variant.Issues.First());
            var records = new List<NativeRootWriterRecord>(); var claims = new List<NativeRootWriteClaim>();
            if (variant.Tasks.Parameters)
            {
                foreach (var value in _values)
                {
                    var reference = value.Reference; var owner = reference.Owner; var count = _active.GetValueOrDefault(reference.RefId); var active = count != 0;
                    var excluded = variant.ExcludeCustomerParameters && reference.CustomerAdjustable;
                    if (owner is null)
                    {
                        records.Add(new(reference.RefId, reference.ParameterId, reference.ParameterTypeId, RootParameterDisposition.NoStorage,
                            active, count, reference.Always, reference.CustomerAdjustable, value.Origin, value.CurrentHash, null,
                            false, false, false, excluded, null));
                        continue;
                    }
                    var behavior = _inventory!.Policy!.DownloadBehavior;
                    var chosen = active || reference.Always ? value.CurrentWord : behavior == RootParameterDownloadBehavior.DefaultValue ? value.DefaultWord : null;
                    var chosenBytes = active || reference.Always ? value.CurrentBytes : behavior == RootParameterDownloadBehavior.DefaultValue ? value.DefaultBytes : null;
                    var hasValue = chosen is not null || chosenBytes is not null;
                    var chosenHash = !hasValue ? null : active || reference.Always ? value.CurrentHash : value.DefaultHash;
                    var disposition = active ? RootParameterDisposition.ActiveValue : reference.Always ? RootParameterDisposition.AlwaysValue
                        : behavior == RootParameterDownloadBehavior.DefaultValue ? RootParameterDisposition.InactiveDefault
                        : behavior == RootParameterDownloadBehavior.None ? RootParameterDisposition.InactiveNullNone : RootParameterDisposition.InactiveNullBackground;
                    var writes = hasValue && !excluded;
                    var set = NativeRootValidityEffect.None; var clear = NativeRootValidityEffect.None;
                    if (hasValue)
                    {
                        if (excluded) clear = NativeRootValidityEffect.WholeBytes;
                        else set = _inventory.Policy.MaskPolicy == RootParameterMaskPolicy.FieldBitsOnly ? NativeRootValidityEffect.FieldBits : NativeRootValidityEffect.WholeBytes;
                    }
                    else if (behavior != RootParameterDownloadBehavior.None && !excluded) set = NativeRootValidityEffect.WholeBytes;
                    // RawData reserves MaxSize+4 bytes but drives only prefix+actual payload.
                    // A background record keeps its current bytes with that same bounded extent.
                    var bytes = chosenBytes ?? value.CurrentBytes;
                    var claim = new NativeRootWriteClaim(reference.RefId, owner.OwnerKey, owner.Target, owner.ByteOffset, owner.BitOffset,
                        bytes is null ? owner.BitLength : checked((uint)bytes.Length * 8), writes, writes ? chosen : null, set, clear, bytes);
                    claims.Add(claim);
                    records.Add(new(reference.RefId, reference.ParameterId, reference.ParameterTypeId, disposition, active, count,
                        reference.Always, reference.CustomerAdjustable, value.Origin, value.CurrentHash, chosenHash, true, true, writes, excluded, claim));
                }
                if (_inventory!.AdmitRootUnions) CoalesceUnions(records, claims);
                CheckValidityInteractions(claims);
            }
            var result = NativeRootVariantPopulation.Seal(this, _inventory!, _preparation!, _application!, variant,
                records.ToArray(), claims.ToArray(), variant.Tasks.Parameters ? _inventory!.Owners.Concat(_inventory.Modules?.ReservationOwners ?? Array.Empty<NativeRootOwner>()).ToArray() : []);
            if (!MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
            return result;
        }
        catch (NativeGeneralRejected failure) { return NativeRootVariantPopulation.Unavailable(failure.Issue with { VariantOrdinal = variant?.Ordinal }); }
        catch (Exception failure) when (failure is FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return NativeRootVariantPopulation.Unavailable(new(NativeGeneralIssueCode.InvalidDeclaration, VariantOrdinal: variant?.Ordinal)); }
    }
    private static void CheckValidityInteractions(List<NativeRootWriteClaim> claims)
    {
        foreach (var group in claims.GroupBy(c => c.Target))
        {
            var sets = group.Where(c => c.ValiditySet != NativeRootValidityEffect.None).Select(c => Span(c, c.ValiditySet)).OrderBy(s => s.Start).ToArray();
            var clears = group.Where(c => c.ValidityClear != NativeRootValidityEffect.None).Select(c => Span(c, c.ValidityClear)).OrderBy(s => s.Start).ToArray();
            var i = 0; var j = 0;
            while (i < sets.Length && j < clears.Length)
            {
                if (sets[i].Start < clears[j].End && clears[j].Start < sets[i].End) NativeGeneralHash.Fail(NativeGeneralIssueCode.NoncommutativeValidity);
                if (sets[i].End <= clears[j].Start) i++; else j++;
            }
        }
    }
    private static (ulong Start, ulong End) Span(NativeRootWriteClaim claim, NativeRootValidityEffect effect)
    {
        var start = (ulong)claim.ByteOffset * 8; var end = start + claim.BitOffset + claim.BitLength;
        return effect == NativeRootValidityEffect.FieldBits ? (start + claim.BitOffset, end) : (start, (end + 7) / 8 * 8);
    }
}

internal sealed class NativeRootVariantPopulation
{
    private readonly NativeRootWriterPopulation? _population;
    private readonly NativeRootImageInventory? _inventory;
    private readonly NativeImageVariantPreparation? _preparation;
    private readonly NativeApplicationEvaluationContext? _application;
    private readonly NativeImageVariant? _variant;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal IReadOnlyList<NativeRootWriterRecord> Records { get; }
    internal IReadOnlyList<NativeRootWriteClaim> Claims { get; }
    internal IReadOnlyList<NativeRootOwner> AllocationRequirements { get; }
    internal IReadOnlyList<NativeModuleImageMember> ModuleOrigins => _inventory?.Modules?.Members ?? Array.Empty<NativeModuleImageMember>();
    internal string? ModuleOriginsFingerprint => _inventory?.Modules?.Fingerprint;
    internal IReadOnlyList<NativeRootOwner> DeclaredOwners => _inventory?.Owners ?? Array.Empty<NativeRootOwner>();
    internal IReadOnlyList<NativeModuleImageAllocation> ModuleAllocations => _inventory?.Modules?.Allocations ?? Array.Empty<NativeModuleImageAllocation>();
    internal IReadOnlyList<NativeRootOwner> ModuleReservations => _inventory?.Modules?.ReservationOwners ?? Array.Empty<NativeRootOwner>();
    internal IReadOnlyList<ApplicationModuleInstance> ModuleInstances => _inventory?.Modules?.Instances ?? Array.Empty<ApplicationModuleInstance>();
    internal NativeRootTaskDisposition ParameterTaskDisposition { get; }
    internal NativeRootCustomerPolicy CustomerPolicy { get; }
    internal string? Fingerprint { get; }
    internal string? PopulationHash => Fingerprint;
    internal string? GeometryHash { get; }
    internal string? MaskInteractionHash { get; }
    private NativeRootVariantPopulation(NativeGeneralIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Records = Array.Empty<NativeRootWriterRecord>();
        Claims = Array.Empty<NativeRootWriteClaim>(); AllocationRequirements = Array.Empty<NativeRootOwner>();
    }
    private NativeRootVariantPopulation(NativeRootWriterPopulation population, NativeRootImageInventory inventory,
        NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application, NativeImageVariant variant,
        NativeRootWriterRecord[] records, NativeRootWriteClaim[] claims, NativeRootOwner[] allocation)
    {
        _population = population; _inventory = inventory; _preparation = preparation; _application = application; _variant = variant;
        Issues = Array.Empty<NativeGeneralIssue>(); Records = Array.AsReadOnly(records); Claims = Array.AsReadOnly(claims); AllocationRequirements = Array.AsReadOnly(allocation);
        ParameterTaskDisposition = variant.Tasks.Parameters ? NativeRootTaskDisposition.Scheduled : NativeRootTaskDisposition.NotScheduled;
        CustomerPolicy = variant.ExcludeCustomerParameters ? NativeRootCustomerPolicy.ExcludeAdjustable : NativeRootCustomerPolicy.IncludeAll;
        GeometryHash = inventory.GeometryHash;
        MaskInteractionHash = NativeGeneralHash.Of(claims.Select(c => new { c.RefId, c.Target, c.ByteOffset, c.BitOffset, c.BitLength, c.ValiditySet, c.ValidityClear }).ToArray());
        Fingerprint = NativeGeneralHash.Of(new { population = population.Fingerprint, variant = variant.Fingerprint, ParameterTaskDisposition,
            CustomerPolicy, records, claims, allocation, GeometryHash, MaskInteractionHash });
    }
    internal static NativeRootVariantPopulation Unavailable(NativeGeneralIssue issue) => new(issue);
    internal static NativeRootVariantPopulation Seal(NativeRootWriterPopulation population, NativeRootImageInventory inventory,
        NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application, NativeImageVariant variant,
        NativeRootWriterRecord[] records, NativeRootWriteClaim[] claims, NativeRootOwner[] allocation)
        => new(population, inventory, preparation, application, variant, records, claims, allocation);
    internal bool BelongsTo(NativeRootImageInventory inventory, NativeImageVariantPreparation preparation,
        NativeApplicationEvaluationContext application, NativeImageVariant variant)
        => IsAvailable && ReferenceEquals(inventory, _inventory) && ReferenceEquals(preparation, _preparation)
            && ReferenceEquals(application, _application) && ReferenceEquals(variant, _variant);
    internal bool MatchesCurrent() => IsAvailable && _population?.MatchesCurrent() == true && _preparation!.Contains(_variant!);
}
