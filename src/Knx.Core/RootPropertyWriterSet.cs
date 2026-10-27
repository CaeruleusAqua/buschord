namespace Knx;

internal sealed class RootPropertyWriterSet
{
    internal RootPropertyPopulation Population { get; }
    internal RootPropertyWriteClaim[] Claims { get; }
    internal RootPropertyWriterSet(ArchivedImageSources.Inputs inputs, RootPropertyWriterSources sources, NativeApplicationEvaluationContext context)
    {
        if (!sources.BelongsTo(inputs) || !sources.MatchesSources(inputs)
            || context.SourceFingerprint != inputs.Manifest.Binding?.Fingerprint)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
        if (!context.IsAvailable || context.Catalog is null || context.Snapshot is null || context.Activation is null
            || !context.Activation.BelongsTo(context.Catalog, context.Snapshot)
            || context.Snapshot.ModuleInstances.Count != 0 || context.ScriptCapabilityUsed)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.ActivationUnavailable, activation: context.Issues.FirstOrDefault());
        if (context.CapturedInputs is null || !sources.MatchesSaved(context.CapturedInputs.Values, sources.ApplicationId))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var identity in context.Activation!.ActiveParameterOwners)
        {
            var declaration = context.Catalog!.At(identity.SourceKey);
            var id = (string?)declaration?.Attribute("RefId");
            if (identity.ModuleInstanceId.Length != 0 || declaration?.Name != sources.Application.Name.Namespace + "ParameterRefRef"
                || id is null || !sources.ValidatedSourceMap.ContainsKey(id))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.ActivationUnavailable);
            owners[id!] = checked(owners.GetValueOrDefault(id!) + 1);
        }
        var physical = sources.PhysicalReferences.ToDictionary(r => r.RefId, StringComparer.Ordinal);
        var records = new List<RootPropertyParameterRecord>(); var claims = new List<RootPropertyWriteClaim>();
        foreach (var source in sources.References)
        {
            var id = RootBuildGuard.Id(source.Reference); var parameter = RootBuildGuard.Id(source.Parameter); var type = RootBuildGuard.Id(source.Type);
            var hydration = RootParameterWriterCommon.Hydrate(id, source.ParameterValue, source.ReferenceValue, sources.Saved);
            var current = hydration.Value; var origin = hydration.Origin;
            if (!context.EffectiveValues.TryGetValue(id, out var evaluated)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.ActivationUnavailable, id);
            var count = owners.GetValueOrDefault(id); var active = count != 0; var currentHash = RootPropertyBuildGuard.Hash(current);
            if (source.Location is null)
            {
                if (!string.Equals(current, evaluated, StringComparison.Ordinal)
                    || current is not null && !ParameterValidation.TryValidate(source.ParsedType, current, out _))
                    RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id);
                records.Add(new(id, parameter, type, RootParameterDisposition.NoStorage, active, count, source.Always, source.Customer,
                    origin, currentHash, null, false, false, false));
                continue;
            }
            if (!physical.TryGetValue(id, out var field)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnknownStorage, id);
            if (current is null || evaluated is null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id);
            var hydrated = Encode(current!, id, source, sources.Policy);
            var fromContext = Encode(evaluated!, id, source, sources.Policy);
            if (hydrated.Bits != fromContext.Bits || hydrated.Word != fromContext.Word)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id);
            var selection = RootParameterWriterCommon.ChooseValue(true, current, source.ParameterValue, active, source.Always, sources.Policy.DownloadBehavior);
            ParameterWordEncoding? encoding = null;
            if (active || source.Always) encoding = hydrated;
            else if (sources.Policy.DownloadBehavior == RootParameterDownloadBehavior.DefaultValue)
            {
                if (selection.Value is null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id);
                encoding = Encode(selection.Value!, id, source, sources.Policy);
            }
            var writes = encoding is not null;
            var maskEffect = writes ? sources.Policy.MaskPolicy == RootParameterMaskPolicy.FieldBitsOnly ? RootParameterMaskEffect.FieldBits : RootParameterMaskEffect.WholeBytes
                : sources.Policy.DownloadBehavior == RootParameterDownloadBehavior.None ? RootParameterMaskEffect.None : RootParameterMaskEffect.WholeBytes;
            var owner = field!.Owner;
            var claim = new RootPropertyWriteClaim(id, owner.PropertyKey, owner.RawSelector, owner.NativeSelector,
                owner.ByteOffset, owner.BitOffset, owner.BitLength, writes, maskEffect, encoding?.Word);
            claims.Add(claim);
            records.Add(new(id, parameter, type, selection.Disposition, active, count, source.Always, source.Customer,
                origin, currentHash, selection.Value is null ? null : RootPropertyBuildGuard.Hash(selection.Value), true, true, writes,
                owner.PropertyKey, owner.RawSelector, owner.NativeSelector, owner.ByteOffset, owner.BitOffset, owner.BitLength, encoding?.Word));
        }
        Claims = claims.ToArray();
        var valuesHash = RootPropertyBuildGuard.Hash(records.Select(r => new { r.RefId, r.CurrentValueOrigin, r.CurrentValueHash, r.RecordValueHash, r.EncodedWord }).ToArray());
        var geometryHash = RootPropertyBuildGuard.Hash(sources.Owners.OrderBy(o => o.PropertyKey, StringComparer.Ordinal).ThenBy(o => o.Start).ThenBy(o => o.ParameterId, StringComparer.Ordinal).ToArray());
        var populationHash = RootPropertyBuildGuard.Hash(new { sources.SourceHash, records, geometryHash, policy = sources.Policy.Fingerprint });
        Population = new(records.ToArray(), sources.ParameterCount, sources.Owners.Count, populationHash, valuesHash, geometryHash);
    }
    private static ParameterWordEncoding Encode(string value, string reference, RootParameterSource source, RootImageBuildPolicy policy)
    {
        if (value.Length > 64 || value.Any(char.IsControl)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, reference);
        var result = OfflineParameterPatch.EncodeWord(source.Type.Elements().Single(), source.ParsedType, value,
            policy.ByteOrder, source.Location!.BitOffset!.Value, reference);
        if (result.Issue is not null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, reference, parameter: result.Issue);
        return result;
    }
}
