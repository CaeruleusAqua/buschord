namespace Knx;

internal sealed record NativeImageFoldStep(int LeftIndex, NativeImageRange LeftRange, bool LeftHasMask,
    NativeImageRange RightRange, bool RightHasMask, NativeImageRange ResultRange, bool ResultHasMask,
    long GrowthPayloadBytes);
internal sealed record NativeImageFoldAddition(int SourceIndex, int InsertIndex, IReadOnlyList<NativeImageFoldStep> Folds);

/// <summary>A source-paired, payload-free insertion and overlap plan.</summary>
internal sealed class NativeImageFoldSchedule
{
    private sealed class Node(NativeImageRange range, bool mask, NativeImageContributionKind kind)
    {
        internal NativeImageRange Range = range;
        internal bool HasMask = mask;
        internal NativeImageContributionKind FirstKind = kind;
        internal List<NativeImageContributionKind> Kinds = [kind];
        internal List<NativeImageRange> CodeRanges = kind == NativeImageContributionKind.NativeCode ? [range] : [];
    }
    private readonly NativeImageSourceContribution[] _sources;
    private readonly NativeImagePostTransition[] _transitions;
    internal IReadOnlyList<NativeImageFoldAddition> Additions { get; }
    internal IReadOnlyList<NativeImageRange> Components { get; }
    internal bool UsesExtendedFolds { get; }
    internal long SourcePayloadBytes { get; }
    internal long GrowthPayloadBytes { get; }
    internal long TransitionPayloadBytes { get; }
    internal long FinalComponentBytes { get; }
    internal long ReservedMaterializationPayloadBytes { get; }
    internal string Fingerprint { get; }

    private NativeImageFoldSchedule(NativeImageSourceContribution[] sources, NativeImagePostTransition[] transitions,
        NativeImageFoldAddition[] additions, NativeImageRange[] components, bool extended, long sourceBytes,
        long growthBytes, long transitionBytes, long finalBytes, long reserve)
    {
        _sources = sources; _transitions = transitions;
        Additions = Array.AsReadOnly(additions); Components = Array.AsReadOnly(components);
        UsesExtendedFolds = extended; SourcePayloadBytes = sourceBytes; GrowthPayloadBytes = growthBytes;
        TransitionPayloadBytes = transitionBytes; FinalComponentBytes = finalBytes;
        ReservedMaterializationPayloadBytes = reserve;
        Fingerprint = NativeGeneralHash.Of(new { profile = 1, sources = sources.Select(s => new { s.Order, s.Kind, s.SourceId, s.Range }).ToArray(),
            transitions, additions, components, extended, sourceBytes, growthBytes, transitionBytes, finalBytes, reserve });
    }

    internal bool Matches(IReadOnlyList<NativeImageSourceContribution> sources, IReadOnlyList<NativeImagePostTransition> transitions)
        => sources is not null && transitions is not null && sources.Count == _sources.Length && transitions.Count == _transitions.Length
            && _sources.Where((s, i) => !ReferenceEquals(s, sources[i])).Any() == false
            && _transitions.Where((t, i) => !ReferenceEquals(t, transitions[i])).Any() == false;

    internal static NativeImageFoldSchedule Create(IReadOnlyList<NativeImageSourceContribution> sources,
        IReadOnlyList<NativeImagePostTransition> transitions)
    {
        if (sources is null || transitions is null || sources.Count > 4096 || transitions.Count > 4096)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        var owned = sources!.ToArray(); var post = transitions!.ToArray();
        var orders = new HashSet<int>(); long sum = 0, sourceBytes = 0, growthBytes = 0;
        foreach (var source in owned)
        {
            if (source is null || source.Range is null || source.Range.Target is null || !source.Range.IsValid
                || !Enum.IsDefined(source.Kind) || !orders.Add(source.Order)
                || source.RawData?.Length > source.Range.Length || source.RawMask?.Length > source.Range.Length
                || source.RawMask is not null && source.RawData is null)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
            if (source.Kind is NativeImageContributionKind.PointerZero or NativeImageContributionKind.FactoryProperty
                ? source.Range.Target.Space != NativeImageSpace.PropertyData
                : source.Range.Target.Space is not (NativeImageSpace.StandardMemory or NativeImageSpace.RelativeMemory))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, resource: source.SourceId);
            sum = checked(sum + source.Range.Length);
            sourceBytes = checked(sourceBytes + (long)source.Range.Length * (SourceHasMask(source) ? 2 : 1));
        }
        var ordered = owned.Select((source, index) => (Source: source, Index: index)).OrderBy(s => s.Source.Order).ToArray();
        // Original owners remain distinct even after their containing buffers coalesce.
        foreach (var group in ordered.Where(s => s.Source.Kind == NativeImageContributionKind.NativeCode).GroupBy(s => s.Source.Range.Target))
        {
            ulong end = 0; var first = true;
            foreach (var item in group.OrderBy(s => s.Source.Range.ByteOffset))
            {
                if (!first && item.Source.Range.ByteOffset < end)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, resource: item.Source.SourceId);
                end = End(item.Source.Range); first = false;
            }
        }
        foreach (var group in ordered.Where(s => s.Source.Kind == NativeImageContributionKind.FactoryProperty).GroupBy(s => s.Source.Range.Target))
            if (group.Take(2).Count() != 1) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);

        var nodes = new List<Node>(); var additions = new List<NativeImageFoldAddition>();
        foreach (var item in ordered)
        {
            var source = item.Source;
            var insert = InsertionIndex(nodes, source.Range);
            nodes.Insert(insert, new(source.Range, SourceHasMask(source), source.Kind));
            var folds = new List<NativeImageFoldStep>();
            var at = Math.Max(0, insert - 1);
            while (at + 1 < nodes.Count)
            {
                var left = nodes[at]; var right = nodes[at + 1];
                if (left.Range.Target != source.Range.Target || right.Range.Target != source.Range.Target) { at++; continue; }
                if (!Overlaps(left.Range, right.Range)) { at++; continue; }
                var end = Math.Max(End(left.Range), End(right.Range));
                var length = end - left.Range.ByteOffset;
                if (length > 1024 * 1024 || end > (ulong)uint.MaxValue + 1)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
                var grows = length > (uint)left.Range.Length;
                var hasMask = grows ? left.HasMask || right.HasMask : left.HasMask;
                var bytes = grows ? checked((long)length * (hasMask ? 2 : 1)) : 0;
                growthBytes = checked(growthBytes + bytes);
                if (growthBytes > NativeImagePayloadLedger.Limit) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
                var range = new NativeImageRange(left.Range.Target, left.Range.ByteOffset, (int)length);
                folds.Add(new(at, left.Range, left.HasMask, right.Range, right.HasMask, range, hasMask, bytes));
                left.Range = range; left.HasMask = hasMask;
                nodes.RemoveAt(at + 1); // Retry the same left neighbour after a bridge.
            }
            if (nodes.Count > 256) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
            additions.Add(new(item.Index, insert, Array.AsReadOnly(folds.ToArray())));
        }
        var components = nodes.Select(n => n.Range).ToArray();
        long transitionBytes = 0;
        foreach (var transition in post)
        {
            if (transition is null || !Enum.IsDefined(transition.Kind)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
            if (transition.Kind is NativeImagePostTransitionKind.ResetMasksToZero or NativeImagePostTransitionKind.MaterializeNullMasks)
            {
                if (transition.Range is not null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
                foreach (var node in nodes)
                    if (!node.HasMask) { transitionBytes = checked(transitionBytes + node.Range.Length); node.HasMask = true; }
            }
            else if (transition.Range is not { IsValid: true, Target.Space: NativeImageSpace.StandardMemory })
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        }
        var finalBytes = components.Sum(r => (long)r.Length);
        // Includes discarded growth arrays, initial seals, claim bytes, canonical temp/seal and final seals.
        var reserve = Math.Max(checked(sum * 12), checked(sourceBytes + growthBytes + transitionBytes + finalBytes * 8));
        if (reserve > NativeImagePayloadLedger.Limit) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        return new(owned, post, additions.ToArray(), components, !SupportsLegacy(ordered.Select(s => s.Source)),
            sourceBytes, growthBytes, transitionBytes, finalBytes, reserve);
    }

    private static bool SourceHasMask(NativeImageSourceContribution source)
        => source.Kind != NativeImageContributionKind.NativeCode || source.RawMask is not null
            || (source.RawData?.Length ?? 0) < source.Range.Length;
    private static int InsertionIndex(List<Node> nodes, NativeImageRange range)
    {
        var first = nodes.FindIndex(n => n.Range.Target == range.Target);
        if (first < 0) return nodes.Count;
        var at = first;
        while (at < nodes.Count && nodes[at].Range.Target == range.Target && nodes[at].Range.ByteOffset < range.ByteOffset) at++;
        return at;
    }
    private static bool SupportsLegacy(IEnumerable<NativeImageSourceContribution> sources)
    {
        var buffers = new List<Node>();
        foreach (var source in sources)
        {
            var matches = buffers.Where(b => b.Range.Target == source.Range.Target && Overlaps(b.Range, source.Range)).Take(2).ToArray();
            if (matches.Length == 0) { buffers.Add(new(source.Range, SourceHasMask(source), source.Kind)); continue; }
            if (matches.Length != 1) return false;
            var existing = matches[0];
            if (!existing.Range.Covers(source.Range.ByteOffset, source.Range.Length))
            {
                if (!(source.Kind == NativeImageContributionKind.NativeCode && existing.FirstKind == NativeImageContributionKind.ZeroRegion && existing.Kinds.Count == 1)
                    && !(source.Kind == NativeImageContributionKind.FactoryProperty && existing.FirstKind == NativeImageContributionKind.PointerZero && existing.Kinds.Count == 1)) return false;
                var leftRange = existing.Range.ByteOffset < source.Range.ByteOffset ? existing.Range : source.Range;
                var length = (int)(Math.Max(End(existing.Range), End(source.Range)) - leftRange.ByteOffset);
                existing.Range = new(leftRange.Target, leftRange.ByteOffset, length); existing.HasMask = true;
            }
            else if (source.Kind == NativeImageContributionKind.NativeCode && existing.FirstKind == NativeImageContributionKind.ZeroRegion
                && existing.Kinds.All(k => k is NativeImageContributionKind.ZeroRegion or NativeImageContributionKind.NativeCode)
                && existing.HasMask && !existing.CodeRanges.Any(r => Overlaps(r, source.Range))) { }
            else if (source.Kind == NativeImageContributionKind.FactoryProperty && existing.FirstKind == NativeImageContributionKind.PointerZero
                && existing.Kinds.All(k => k == NativeImageContributionKind.PointerZero) && existing.HasMask) { }
            else return false;
            if (source.Kind == NativeImageContributionKind.NativeCode) existing.CodeRanges.Add(source.Range);
            existing.Kinds.Add(source.Kind);
        }
        return true;
    }
    private static ulong End(NativeImageRange range) => (ulong)range.ByteOffset + (uint)range.Length;
    private static bool Overlaps(NativeImageRange left, NativeImageRange right)
        => (ulong)left.ByteOffset < End(right) && (ulong)right.ByteOffset < End(left);
}
