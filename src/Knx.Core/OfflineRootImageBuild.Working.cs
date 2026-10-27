using System.Numerics;

namespace Knx;

public static partial class OfflineRootImageBuild
{
    private sealed class Buffer(ResourcePatchArena arena, uint offset, int length)
    {
        internal ResourcePatchArena Arena { get; } = arena;
        internal uint Offset { get; } = offset;
        internal byte[] Data { get; } = new byte[length];
        internal byte[] Validity { get; } = new byte[length];
        internal byte[] ParameterBits { get; } = new byte[length];
        internal HashSet<string> SourceIds { get; } = new(StringComparer.Ordinal);
        internal ulong End => (ulong)Offset + (uint)Data.Length;
    }
    private sealed record Interval(ResourcePatchArena Arena, uint Start, ulong End);
    private static Buffer[] Allocate(IReadOnlyList<ImageCompositionSourceSegment> sources, GeneratedTablePreview? tables)
        => AllocatePayloads(sources, tables?.Payloads ?? []);
    private static Buffer[] AllocatePayloads(IReadOnlyList<ImageCompositionSourceSegment> sources, IReadOnlyList<GeneratedTablePayload> payloads)
    {
        var intervals = new List<Interval>();
        foreach (var source in sources)
            if (source.AllocationLength != 0) intervals.Add(new(source.Arena, source.ByteOffsetInArena, (ulong)source.ByteOffsetInArena + (uint)source.AllocationLength));
        if (payloads.Count > 4) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
        foreach (var payload in payloads)
        {
            if (payload.Length > RootBuildGuard.Blob || payload.Length != payload.WriteMask.Length
                || (ulong)payload.ByteOffset + (uint)payload.Length > (ulong)uint.MaxValue + 1)
                RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
            if (payload.Length != 0) intervals.Add(new(payload.Arena, payload.ByteOffset, (ulong)payload.ByteOffset + (uint)payload.Length));
        }
        if (intervals.Select(i => i.Arena.Kind).Distinct().Count() > 1) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedMapping);
        var merged = new List<Interval>(); long total = 0;
        foreach (var group in intervals.GroupBy(i => i.Arena).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.LoadStateMachine))
        {
            Interval? pending = null;
            foreach (var interval in group.OrderBy(i => i.Start).ThenBy(i => i.End))
            {
                if (pending is not null && interval.Start <= pending.End) pending = pending with { End = Math.Max(pending.End, interval.End) };
                else { if (pending is not null) merged.Add(pending); pending = interval; }
            }
            if (pending is not null) merged.Add(pending);
        }
        foreach (var interval in merged)
            if ((total += checked((long)(interval.End - interval.Start))) > RootBuildGuard.Total) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
        // Temporary parameter bit claims are bounded by the same combined working allocation.
        return merged.Select(i => new Buffer(i.Arena, i.Start, checked((int)(i.End - i.Start)))).ToArray();
    }
    private static Buffer Find(Buffer[] buffers, ResourcePatchArena arena, uint start, int length)
    {
        var low = 0; var high = buffers.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2; var candidate = buffers[middle];
            var order = candidate.Arena.Kind.CompareTo(arena.Kind);
            if (order == 0) order = Nullable.Compare(candidate.Arena.LoadStateMachine, arena.LoadStateMachine);
            if (order < 0 || order == 0 && candidate.End <= start) low = middle + 1;
            else if (order > 0 || candidate.Offset > start) high = middle - 1;
            else if ((ulong)start + (uint)length <= candidate.End) return candidate;
            else break;
        }
        RootBuildGuard.Fail(RootImageBuildIssueCode.FieldOutOfRange); return null!;
    }
    private static void Seed(Buffer[] buffers, IReadOnlyList<ImageCompositionSourceSegment> sources, StaticCodeMaskPolicy policy)
    {
        foreach (var source in sources)
        {
            if (source.AllocationLength == 0) continue;
            var buffer = Find(buffers, source.Arena, source.ByteOffsetInArena, source.AllocationLength);
            buffer.SourceIds.Add(source.SourceId); var offset = checked((int)(source.ByteOffsetInArena - buffer.Offset));
            for (var i = 0; i < (source.RawData?.Length ?? 0); i++)
            {
                var mask = source.RawMask?[i] ?? byte.MaxValue;
                buffer.Data[offset + i] = mask == 0 ? (byte)0 : source.RawData![i];
                buffer.Validity[offset + i] = policy == StaticCodeMaskPolicy.SourceDefinedBits ? mask : (byte)0;
            }
        }
    }
    private static void ApplyParameters(Buffer[] buffers, RootImageWriteClaim[] claims, ParameterPatchByteOrder order)
    {
        foreach (var claim in claims)
        {
            var length = checked((int)((claim.BitOffset + claim.BitLength + 7) / 8));
            var buffer = Find(buffers, claim.Arena, claim.ByteOffsetInArena, length);
            var start = checked((int)(claim.ByteOffsetInArena - buffer.Offset));
            if (claim.WritesData)
            {
                if (claim.EncodedWord is null) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration, claim.RefId);
                var touches = new SortedDictionary<int, (byte Bits, byte Mask)>();
                FixedScalarPatchCodec.Accumulate(touches, claim.EncodedWord!.Value, start, claim.BitOffset, claim.BitLength, order);
                foreach (var (index, value) in touches)
                {
                    if ((buffer.ParameterBits[index] & value.Mask) != 0) RootBuildGuard.Fail(RootImageBuildIssueCode.AmbiguousOwnership, claim.RefId);
                    buffer.Data[index] = (byte)((buffer.Data[index] & ~value.Mask) | value.Bits);
                    buffer.ParameterBits[index] |= value.Mask;
                    if (claim.MaskEffect == RootParameterMaskEffect.FieldBits) buffer.Validity[index] |= value.Mask;
                }
            }
            if (claim.MaskEffect == RootParameterMaskEffect.WholeBytes) Array.Fill(buffer.Validity, byte.MaxValue, start, length);
        }
    }
    private static RootImageTableSummary? ApplyTables(Buffer[] buffers, GeneratedTablePreview? tables)
    {
        if (tables is null) return null;
        var contributions = ApplyTablePayloads(buffers, tables.Payloads);
        return new(tables, contributions, RootBuildGuard.Hash(contributions));
    }
    private static RootImageTableContribution[] ApplyTablePayloads(Buffer[] buffers, IReadOnlyList<GeneratedTablePayload> payloads)
    {
        var contributions = new List<RootImageTableContribution>();
        var spans = payloads.Where(p => p.Length != 0).GroupBy(p => p.Arena);
        foreach (var group in spans)
        {
            ulong end = 0; var first = true;
            foreach (var payload in group.OrderBy(p => p.ByteOffset))
            {
                if (!first && payload.ByteOffset < end) RootBuildGuard.Fail(RootImageBuildIssueCode.AmbiguousOwnership, resource: payload.ResourceId);
                end = (ulong)payload.ByteOffset + (uint)payload.Length; first = false;
            }
        }
        // Validate every encoder input before applying any table contribution.
        foreach (var payload in payloads)
        {
            if (payload.Length == 0) continue;
            var buffer = Find(buffers, payload.Arena, payload.ByteOffset, payload.Length); var start = checked((int)(payload.ByteOffset - buffer.Offset));
            for (var i = 0; i < payload.Length; i++)
            {
                if (payload.Origin == GeneratedTablePayloadOrigin.RetainedCode
                    ? payload.BeforeData is null || payload.BeforeData.Length != payload.Length || buffer.Data[start + i] != payload.BeforeData[i]
                    : payload.Origin != GeneratedTablePayloadOrigin.FreshGenerated || buffer.Data[start + i] != 0)
                    RootBuildGuard.Fail(RootImageBuildIssueCode.TableWorkingInputsMismatch, resource: payload.ResourceId);
                if ((buffer.ParameterBits[start + i] & payload.WriteMask[i]) != 0)
                    RootBuildGuard.Fail(RootImageBuildIssueCode.ParameterTableOverlap, resource: payload.ResourceId);
            }
            var inputHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.Data.AsSpan(start, payload.Length)));
            var compatibility = RootBuildGuard.Hash(new { payload.Role, payload.Arena, payload.ByteOffset, payload.Length, payload.Origin,
                current = inputHash, encoder = payload.BeforeData?.Sha256 });
            contributions.Add(new(payload.Role, payload.Arena, payload.ByteOffset, payload.Length, payload.Data.Sha256, payload.WriteMask.Sha256, compatibility));
        }
        foreach (var payload in payloads)
        {
            if (payload.Length == 0) continue;
            var buffer = Find(buffers, payload.Arena, payload.ByteOffset, payload.Length); var start = checked((int)(payload.ByteOffset - buffer.Offset));
            for (var i = 0; i < payload.Length; i++)
            {
                var mask = payload.WriteMask[i];
                buffer.Data[start + i] = (byte)((buffer.Data[start + i] & ~mask) | (payload.Data[i] & mask));
                buffer.Validity[start + i] |= mask;
            }
        }
        return contributions.ToArray();
    }
    private static ImageUnspecifiedSpan[] Missing(RootImageChunk[] chunks, out ulong unspecified, out ulong desired)
    {
        var spans = new List<ImageUnspecifiedSpan>(); unspecified = 0; ulong total = 0;
        foreach (var chunk in chunks)
        {
            total += (ulong)chunk.Length * 8; var i = 0;
            while (i < chunk.Length)
            {
                if (chunk.DesiredValidity[i] == byte.MaxValue) { i++; continue; }
                var start = i; ulong bits = 0;
                while (i < chunk.Length && chunk.DesiredValidity[i] != byte.MaxValue)
                { bits += (uint)(8 - BitOperations.PopCount((uint)chunk.DesiredValidity[i])); i++; }
                if (spans.Count >= 4096) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
                unspecified += bits; spans.Add(new(chunk.Key, chunk.Arena, checked(chunk.ByteOffsetInArena + (uint)start), i - start, bits));
            }
        }
        desired = total - unspecified; return spans.ToArray();
    }
    private static RootImageBuildRequirement[] Requirements(GeneratedTablePreview? tables, ulong unspecified)
        => RequirementsForTables(tables?.Requirements, unspecified);
    private static RootImageBuildRequirement[] RequirementsForTables(IReadOnlyList<GeneratedTableRequirement>? tableRequirements, ulong unspecified)
    {
        var result = new List<RootImageBuildRequirement>
        {
            new(RootImageBuildRequirementCode.MaskMemorySourcesNotComposed), new(RootImageBuildRequirementCode.PreWriterFixupsNotApplied),
            new(RootImageBuildRequirementCode.PropertyAndOtherWritersNotApplied), new(RootImageBuildRequirementCode.ActualDeviceOptionsUnverified),
            new(RootImageBuildRequirementCode.PhysicalMappingUnresolved), new(RootImageBuildRequirementCode.ConfigurationAndSecureStateUnresolved),
            new(RootImageBuildRequirementCode.ProcedureAdjustmentsNotApplied), new(RootImageBuildRequirementCode.FactoryOptimizationNotApplied),
            new(RootImageBuildRequirementCode.ProcedureFinalizationUnresolved), new(RootImageBuildRequirementCode.DeviceStateNotVerified),
        };
        if (unspecified != 0) result.Add(new(RootImageBuildRequirementCode.UnspecifiedBitsNeedExternalFacts));
        if (tableRequirements is null) result.Add(new(RootImageBuildRequirementCode.CommunicationTablesNotApplied));
        else foreach (var requirement in tableRequirements.Where(r => r.Code == GeneratedTableRequirementCode.UnusedTailMaskInvalidation))
            result.Add(new(RootImageBuildRequirementCode.UnusedTableTailMaskUnresolved, requirement.ResourceId));
        return result.ToArray();
    }
}
