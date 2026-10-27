using System.Numerics;
using System.Xml;

namespace Knx;

public static class OfflineRootPropertyBuild
{
    private const long MaximumOwnedPayload = 16L * 1_048_576;
    private sealed class Buffer(NativePropertyAllocation allocation)
    {
        internal NativePropertyAllocation Allocation { get; } = allocation;
        internal byte[] Data { get; } = allocation.InitialData.ToArray();
        internal byte[] Validity { get; } = allocation.InitialValidity.ToArray();
        internal byte[] ClaimedBits { get; } = new byte[allocation.Length];
    }

    /// <summary>Initializes and formats the complete admitted fixed-root property population from one shared evaluation.</summary>
    public static RootPropertyBuildPlan Preview(Project project, DeviceInstance device, RootPropertyBuildRequest request)
    {
        var manifest = new ArchivedImageSourceManifest(ArchivedImageSourceStatus.InvalidRequest);
        try
        {
            if (!WithinBudget(project, device) || request?.Procedure is not { } requested
                || !RootBuildGuard.Safe(requested.NativeSubtype) || !Enum.IsDefined(requested.Access)
                || (requested.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0
                || (requested.Parts & (LoadParts.Full | LoadParts.Parameters)) == 0)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidRequest);
            var owned = new RootPropertyBuildRequest(request!.Procedure with { });
            if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnaccountedBinaryData);
            var inputs = ArchivedImageSources.ReadInputs(project, device); manifest = inputs.Manifest;
            if (manifest.Binding is null || !manifest.Binding.MatchesIdentity(project, device)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.SourceUnavailable);
            var sources = RootPropertyWriterSources.Read(project, device, inputs);
            var prepared = LoadProcedureCompiler.PrepareResolved(inputs, owned.Procedure);
            if (!prepared.IsAvailable) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.ProcedureUnavailable, procedure: prepared.Issues.FirstOrDefault());
            var references = prepared.ControllerReferences.Take(4097).ToArray();
            if (references.Length > 4096) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded);
            var context = NativeApplicationEvaluationContext.Create(project, device, inputs, references);
            if (!context.IsAvailable)
            {
                var issue = context.Issues.FirstOrDefault();
                RootPropertyBuildGuard.Fail(issue?.Code switch
                {
                    GeneratedTableIssueCode.ExternalConfigurationUnaccounted => RootPropertyBuildIssueCode.ExternalConfigurationUnaccounted,
                    GeneratedTableIssueCode.UnaccountedBinaryData => RootPropertyBuildIssueCode.UnaccountedBinaryData,
                    GeneratedTableIssueCode.LimitExceeded => RootPropertyBuildIssueCode.LimitExceeded,
                    GeneratedTableIssueCode.InputsChanged or GeneratedTableIssueCode.StaleSource => RootPropertyBuildIssueCode.InputsChanged,
                    _ => RootPropertyBuildIssueCode.ActivationUnavailable,
                }, activation: issue);
            }
            var writers = new RootPropertyWriterSet(inputs, sources, context);
            var view = NativeProcedureEvaluationContext.FromApplicationContext(project, device, inputs, prepared, context);
            if (!view.IsAvailable) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.ProcedureUnavailable, procedure: view.Issues.FirstOrDefault());
            var initialization = NativePropertyInitializer.Compile(project, device, inputs, prepared, view, context, sources);
            if (!initialization.IsAvailable)
            {
                var issue = initialization.Issues.FirstOrDefault();
                RootPropertyBuildGuard.Fail(issue?.Code switch
                {
                    PropertyInitializationIssueCode.LimitExceeded => RootPropertyBuildIssueCode.LimitExceeded,
                    PropertyInitializationIssueCode.InputsChanged or PropertyInitializationIssueCode.ContextMismatch => RootPropertyBuildIssueCode.InputsChanged,
                    _ => RootPropertyBuildIssueCode.InitializationUnavailable,
                }, initialization: issue);
            }
            if (!initialization.BelongsTo(inputs, prepared, view, context, sources) || initialization.ActualProcedure is not { IsAvailable: true })
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
            long bytes = 0;
            foreach (var allocation in initialization.Allocations)
            {
                if (allocation.Length is < 1 or > 1_048_576 || allocation.InitialValidity.Length != allocation.Length
                    || (bytes += checked(11L * allocation.Length)) > MaximumOwnedPayload)
                    RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded);
            }
            if (initialization.Allocations.Count > 128 || bytes != initialization.ReservedPayloadBytes)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded);
            var buffers = initialization.Allocations.ToDictionary(a => a.PropertyKey, a => new Buffer(a), StringComparer.Ordinal);
            Apply(buffers, writers.Claims, sources.Policy.ByteOrder);
            var ordered = buffers.Values.OrderBy(b => b.Allocation.PropertyKey, StringComparer.Ordinal).ToArray();
            var chunks = ordered.Select(b => new RootPropertyChunk(b.Allocation.PropertyKey, b.Allocation.RawSelector, b.Allocation.NativeSelector, b.Allocation.PdtLayout,
                b.Allocation.InitialData, b.Allocation.InitialValidity, b.Data, b.Validity)).ToArray();
            var spans = Missing(chunks, out var desired, out var unspecified);
            var allocations = ordered.Select(b => new RootPropertyAllocation(b.Allocation.PropertyKey, b.Allocation.RawSelector, b.Allocation.NativeSelector,
                b.Allocation.PdtLayout, (uint)b.Allocation.ControlBytes, b.Allocation.Length, b.Allocation.InitialData.Sha256,
                b.Allocation.InitialValidity.Sha256, b.Allocation.FactoryDataHash, b.Allocation.Controls.Select(c => c.Ordinal))).ToArray();
            var counts = initialization.Counts.Select(c => new RootPropertyCountObservation(c.PropertyKey, c.RawSelector, c.NativeSelector, c.SourceOrdinal,
                c.Data, c.Mask, c.EncodedCount, c.Status)).ToArray();
            var requirements = Enum.GetValues<RootPropertyBuildRequirementCode>()
                .Where(c => c != RootPropertyBuildRequirementCode.UnspecifiedBitsNeedExternalFacts || unspecified != 0)
                .Select(c => new RootPropertyBuildRequirement(c)).ToArray();
            if (!WithinBudget(project, device) || !manifest.Binding.MatchesIdentity(project, device) || !sources.MatchesSources(inputs)
                || !context.MatchesInputs(project, device, inputs) || !prepared.MatchesSources(inputs) || !view.MatchesCurrent(prepared)
                || !view.UsesSharedApplicationContext(context) || context.CapturedInputs is null
                || !sources.MatchesSaved(context.CapturedInputs.Values, sources.ApplicationId)
                || sources.CaptureCarriers(project, device, sources.ApplicationId) != sources.CarrierHash)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
            var proof = new RootSharedProcedureProof(view); var procedure = initialization.ActualProcedure!;
            var binding = new RootPropertyBuildBinding(project, device, inputs, owned, sources, context, prepared, proof, writers.Population,
                initialization.Fingerprint!, allocations, writers.Claims, chunks, spans, counts, requirements, procedure);
            return new(manifest, binding, sources.Policy, writers.Population, procedure, proof, allocations, writers.Claims, chunks,
                spans, counts, requirements, desired, unspecified);
        }
        catch (RootPropertyBuildRejected failure) { return new(manifest, failure.Issue); }
        catch (RootBuildRejected failure) { return new(manifest, RootPropertyBuildGuard.Adapt(failure.Issue)); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or XmlException or FormatException or OverflowException or ArgumentException)
        { return new(manifest, new(RootPropertyBuildIssueCode.InvalidDeclaration)); }
        catch (InvalidOperationException) { return new(manifest, new(RootPropertyBuildIssueCode.InputsChanged)); }
    }
    internal static bool WithinBudget(Project project, DeviceInstance device) => OfflineRootImageBuild.WithinBudget(project, device);

    private static void Apply(IReadOnlyDictionary<string, Buffer> buffers, IEnumerable<RootPropertyWriteClaim> claims, ParameterPatchByteOrder order)
    {
        foreach (var claim in claims)
        {
            if (!buffers.TryGetValue(claim.PropertyKey, out var buffer) || buffer.Allocation.NativeSelector != claim.NativeSelector)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnknownStorage, claim.RefId, claim.PropertyKey);
            var length = checked((int)((claim.BitOffset + claim.BitLength + 7) / 8));
            if ((ulong)claim.ByteOffset + (uint)length > (ulong)buffer!.Data.Length)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.FieldOutOfRange, claim.RefId, claim.PropertyKey);
            var start = checked((int)claim.ByteOffset);
            if (claim.WritesData)
            {
                if (claim.EncodedWord is null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration, claim.RefId, claim.PropertyKey);
                var touches = new SortedDictionary<int, (byte Bits, byte Mask)>();
                FixedScalarPatchCodec.Accumulate(touches, claim.EncodedWord!.Value, start, claim.BitOffset, claim.BitLength, order);
                foreach (var (index, value) in touches)
                {
                    if ((buffer.ClaimedBits[index] & value.Mask) != 0) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.AmbiguousOwnership, claim.RefId, claim.PropertyKey);
                    buffer.Data[index] = (byte)((buffer.Data[index] & ~value.Mask) | value.Bits); buffer.ClaimedBits[index] |= value.Mask;
                    if (claim.MaskEffect == RootParameterMaskEffect.FieldBits) buffer.Validity[index] |= value.Mask;
                }
            }
            if (claim.MaskEffect == RootParameterMaskEffect.WholeBytes) Array.Fill(buffer.Validity, byte.MaxValue, start, length);
        }
    }
    private static RootPropertyUnspecifiedSpan[] Missing(IReadOnlyList<RootPropertyChunk> chunks, out ulong desired, out ulong unspecified)
    {
        desired = 0; unspecified = 0; var spans = new List<RootPropertyUnspecifiedSpan>();
        foreach (var chunk in chunks)
        {
            var start = 0; byte pending = 0;
            for (var i = 0; i <= chunk.Length; i++)
            {
                var mask = i == chunk.Length ? (byte)0 : (byte)~chunk.DesiredValidity[i];
                if (i < chunk.Length) { var bits = (uint)BitOperations.PopCount(chunk.DesiredValidity[i]); desired += bits; unspecified += 8 - bits; }
                if (mask == pending) continue;
                if (pending != 0)
                {
                    if (spans.Count >= 100000) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded);
                    spans.Add(new(chunk.PropertyKey, (uint)start, i - start, pending));
                }
                pending = mask; start = i;
            }
        }
        return spans.ToArray();
    }
}
