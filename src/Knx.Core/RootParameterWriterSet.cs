using System.Xml.Linq;

namespace Knx;

internal sealed class RootParameterWriterSet
{
    internal RootParameterPopulation Population { get; }
    internal RootImageWriteClaim[] Claims { get; }
    internal RootParameterWriterSet(ArchivedImageSources.Inputs inputs, RootParameterWriterSources sources, NativeApplicationEvaluationContext context)
    {
        if (!context.IsAvailable || context.Catalog is null || context.Snapshot is null || context.Activation is null
            || !context.Activation.BelongsTo(context.Catalog, context.Snapshot)
            || context.Snapshot.ModuleInstances.Count != 0 || context.ScriptCapabilityUsed)
            RootBuildGuard.Fail(RootImageBuildIssueCode.ActivationUnavailable, table: context.Issues.FirstOrDefault());
        if (context.CapturedInputs is null || !sources.MatchesSaved(context.CapturedInputs.Values, RootBuildGuard.Id(sources.Application)))
            RootBuildGuard.Fail(RootImageBuildIssueCode.InputsChanged);
        var referenceMap = sources.References.ToDictionary(s => RootBuildGuard.Id(s.Reference), StringComparer.Ordinal);
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var identity in context.Activation!.ActiveParameterOwners)
        {
            var source = context.Catalog!.At(identity.SourceKey);
            var id = (string?)source?.Attribute("RefId");
            if (identity.ModuleInstanceId.Length != 0 || source?.Name != sources.Application.Name.Namespace + "ParameterRefRef"
                || id is null || !referenceMap.ContainsKey(id)) RootBuildGuard.Fail(RootImageBuildIssueCode.ActivationUnavailable);
            owners[id!] = checked(owners.GetValueOrDefault(id!) + 1);
        }
        var records = new List<RootParameterRecord>(); var claims = new List<RootImageWriteClaim>();
        foreach (var source in sources.References)
        {
            var id = RootBuildGuard.Id(source.Reference); var parameter = RootBuildGuard.Id(source.Parameter); var type = RootBuildGuard.Id(source.Type);
            var hydration = RootParameterWriterCommon.Hydrate(id, source.ParameterValue, source.ReferenceValue, sources.Saved);
            var current = hydration.Value; var origin = hydration.Origin;
            if (!context.EffectiveValues.TryGetValue(id, out var evaluated)) RootBuildGuard.Fail(RootImageBuildIssueCode.ActivationUnavailable, id);
            var count = owners.GetValueOrDefault(id); var active = count != 0;
            var currentHash = RootBuildGuard.Hash(current);
            if (source.Location is null)
            {
                // NoStorage is established by the original declaration, not by an unknown parsed location.
                if (!string.Equals(current, evaluated, StringComparison.Ordinal)
                    || current is not null && !ParameterValidation.TryValidate(source.ParsedType, current, out _))
                    RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
                records.Add(new(id, parameter, type, RootParameterDisposition.NoStorage, active, count, source.Always, source.Customer,
                    origin, currentHash, null, false, false, false));
                continue;
            }
            var location = source.Location;
            if (current is null || evaluated is null) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
            var hydrated = Encode(current!, id, source, sources.Policy);
            var fromContext = Encode(evaluated!, id, source, sources.Policy);
            if (hydrated.Bits != fromContext.Bits || hydrated.Word != fromContext.Word)
                RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
            var selection = RootParameterWriterCommon.ChooseValue(true, current, source.ParameterValue, active, source.Always, sources.Policy.DownloadBehavior);
            var disposition = selection.Disposition; var selected = selection.Value; ParameterWordEncoding? encoding = null;
            if (active || source.Always)
            {
                encoding = hydrated;
            }
            else if (sources.Policy.DownloadBehavior == RootParameterDownloadBehavior.DefaultValue)
            {
                if (selected is null) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
                encoding = Encode(selected!, id, source, sources.Policy);
            }
            var writes = encoding is not null; var arena = RootParameterWriterSources.Arena(location);
            var maskEffect = writes ? sources.Policy.MaskPolicy == RootParameterMaskPolicy.FieldBitsOnly ? RootParameterMaskEffect.FieldBits : RootParameterMaskEffect.WholeBytes
                : sources.Policy.DownloadBehavior == RootParameterDownloadBehavior.None ? RootParameterMaskEffect.None : RootParameterMaskEffect.WholeBytes;
            var width = location.Footprint!.BitLength!.Value;
            var claim = new RootImageWriteClaim(id, location.CodeSegmentId!, arena, location.StartAddress!.Value,
                location.BitOffset!.Value, width, writes, maskEffect, encoding?.Word);
            claims.Add(claim);
            records.Add(new(id, parameter, type, disposition, active, count, source.Always, source.Customer, origin, currentHash,
                selected is null ? null : RootBuildGuard.Hash(selected), true, true, writes, claim.ResourceId, arena,
                claim.ByteOffsetInArena, claim.BitOffset, width, encoding?.Word));
        }
        Claims = claims.ToArray();
        var valuesHash = RootBuildGuard.Hash(records.Select(r => new { r.RefId, r.CurrentValueOrigin, r.CurrentValueHash, r.RecordValueHash, r.EncodedWord }).ToArray());
        var geometryHash = RootBuildGuard.Hash(sources.Owners);
        var populationHash = RootBuildGuard.Hash(new { sources.SourceHash, records, geometryHash, policy = sources.Policy.Fingerprint });
        Population = new(records.ToArray(), sources.ParameterCount, sources.Owners.Count, populationHash, valuesHash, geometryHash);
    }
    private static ParameterWordEncoding Encode(string value, string reference, RootParameterSource source, RootImageBuildPolicy policy)
    {
        if (value.Length > 64 || value.Any(char.IsControl)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, reference);
        var result = OfflineParameterPatch.EncodeWord(source.Type.Elements().Single(), source.ParsedType, value, policy.ByteOrder, source.Location!.BitOffset!.Value, reference);
        if (result.Issue is not null) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, reference, parameter: result.Issue);
        return result;
    }
}
