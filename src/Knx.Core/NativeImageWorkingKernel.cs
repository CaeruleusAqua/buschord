namespace Knx;

/// <summary>Charges every new image payload, including transient and immutable copies.</summary>
internal sealed class NativeImagePayloadLedger
{
    internal const long Limit = 16L * 1024 * 1024;
    internal long AllocatedBytes { get; private set; }
    internal void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > Limit - AllocatedBytes) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        AllocatedBytes += bytes;
    }
    internal byte[] Allocate(int length, byte fill = 0)
    {
        if (length < 0 || length > 1024 * 1024) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        Reserve(length); var result = new byte[length];
        if (fill != 0) Array.Fill(result, fill);
        return result;
    }
    internal ImmutablePatchBytes Seal(ReadOnlySpan<byte> data)
    { Reserve(data.Length); return new(data); }
}

internal sealed record NativeGeneralWorkingChunk(NativeImageRange Range, IReadOnlyList<string> Sources,
    ImmutablePatchBytes InitialData, ImmutablePatchBytes? InitialValidity,
    ImmutablePatchBytes WorkingData, ImmutablePatchBytes? DesiredValidity, ImmutablePatchBytes? CanonicalDesired,
    ImmutablePatchBytes ParameterWriteBits);

/// <summary>A private mutable stage; no array or materialization authority escapes this object.</summary>
internal sealed class NativeImageWorkingKernel
{
    private sealed class Buffer(NativeImageRange range, NativeImageContributionKind kind, byte[] data, byte[]? mask)
    {
        internal NativeImageRange Range { get; set; } = range;
        internal byte[] Data { get; set; } = data;
        internal byte[]? Mask { get; set; } = mask;
        internal NativeImageContributionKind FirstKind { get; } = kind;
        internal List<NativeImageContributionKind> Kinds { get; } = [kind];
        internal List<NativeImageRange> CodeRanges { get; } = [];
        internal List<string> Sources { get; } = [];
        internal List<NativeImageSourceContribution> Originals { get; } = [];
        internal ImmutablePatchBytes? InitialData { get; set; }
        internal ImmutablePatchBytes? InitialValidity { get; set; }
        internal byte[]? ParameterWriteBits { get; set; }
    }
    private readonly NativeImagePayloadLedger _ledger;
    private NativeImageInitializationPlan? _plan;
    private readonly List<Buffer> _buffers = [];
    private bool _initialized, _parametersApplied, _sealed;
    private int _generation;
    internal string? InitializationFingerprint { get; private set; }

    private NativeImageWorkingKernel(NativeImagePayloadLedger ledger) => _ledger = ledger;
    internal static NativeImageWorkingKernel Initialize(NativeImageInitializationPlan plan, NativeImagePayloadLedger ledger)
    {
        if (!plan.IsAvailable || !plan.MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        var schedule = plan.FoldSchedule;
        if (schedule is null || !schedule.Matches(plan.Contributions, plan.PostSourceTransitions))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        if (ledger is null || ledger.AllocatedBytes < 0 || ledger.AllocatedBytes > NativeImagePayloadLedger.Limit
            || schedule!.ReservedMaterializationPayloadBytes > NativeImagePayloadLedger.Limit - ledger.AllocatedBytes)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        var result = new NativeImageWorkingKernel(ledger);
        result._plan = plan;
        if (schedule.UsesExtendedFolds) result.AddScheduled(schedule);
        else
        {
            var orders = new HashSet<int>();
            foreach (var contribution in plan.Contributions.OrderBy(c => c.Order))
            {
                if (!orders.Add(contribution.Order)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
                result.Add(contribution);
            }
        }
        foreach (var transition in plan.PostSourceTransitions) result.Transition(transition);
        foreach (var buffer in result._buffers)
        {
            buffer.InitialData = ledger.Seal(buffer.Data);
            buffer.InitialValidity = buffer.Mask is null ? null : ledger.Seal(buffer.Mask);
        }
        result._initialized = true;
        result.InitializationFingerprint = NativeGeneralHash.Of(new { plan = plan.Fingerprint,
            buffers = result._buffers.Select(b => new { b.Range, b.Sources,
                data = b.InitialData!.Sha256, mask = b.InitialValidity?.Sha256 }).ToArray() });
        return result;
    }

    private void AddScheduled(NativeImageFoldSchedule schedule)
    {
        foreach (var addition in schedule.Additions)
        {
            var source = _plan!.Contributions[addition.SourceIndex];
            var data = _ledger.Allocate(source.Range.Length);
            byte[]? mask;
            switch (source.Kind)
            {
                case NativeImageContributionKind.ZeroRegion:
                case NativeImageContributionKind.PointerZero:
                    mask = _ledger.Allocate(source.Range.Length); break;
                case NativeImageContributionKind.FactoryProperty:
                    Copy(source.RawData, data); mask = _ledger.Allocate(source.Range.Length); break;
                case NativeImageContributionKind.NativeCode:
                    Copy(source.RawData, data);
                    var supplied = source.RawData?.Length ?? 0;
                    mask = source.RawMask is not null || supplied < source.Range.Length ? _ledger.Allocate(source.Range.Length) : null;
                    if (mask is not null)
                    {
                        if (source.RawMask is null) Array.Fill(mask, byte.MaxValue, 0, supplied);
                        else Copy(source.RawMask, mask);
                        for (var i = 0; i < mask.Length; i++) if (mask[i] == 0) data[i] = 0;
                    }
                    break;
                default: NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource); return;
            }
            var incoming = new Buffer(source.Range, source.Kind, data, mask);
            incoming.Originals.Add(source); incoming.Sources.Add(source.SourceId);
            if (source.Kind == NativeImageContributionKind.NativeCode) incoming.CodeRanges.Add(source.Range);
            _buffers.Insert(addition.InsertIndex, incoming);
            foreach (var step in addition.Folds)
            {
                var left = _buffers[step.LeftIndex]; var right = _buffers[step.LeftIndex + 1];
                if (left.Range != step.LeftRange || right.Range != step.RightRange
                    || (left.Mask is not null) != step.LeftHasMask || (right.Mask is not null) != step.RightHasMask)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
                var before = _ledger.AllocatedBytes;
                // The schedule already inserted equal starts with the new node on the left.
                var merged = NativeImageCoalescer.Coalesce(new(right.Range, right.Data, right.Mask),
                    new(left.Range, left.Data, left.Mask), _ledger);
                if (merged.Range != step.ResultRange || (merged.Mask is not null) != step.ResultHasMask
                    || _ledger.AllocatedBytes - before != step.GrowthPayloadBytes)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
                left.Range = merged.Range; left.Data = merged.Data; left.Mask = merged.Mask;
                left.Originals.AddRange(right.Originals);
                left.Originals.Sort((a, b) => a.Order.CompareTo(b.Order));
                left.Sources.Clear(); left.Sources.AddRange(left.Originals.Select(s => s.SourceId));
                left.CodeRanges.AddRange(right.CodeRanges); left.Kinds.AddRange(right.Kinds);
                _buffers.RemoveAt(step.LeftIndex + 1);
            }
        }
        if (!_buffers.Select(b => b.Range).SequenceEqual(schedule.Components))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
    }

    private void Add(NativeImageSourceContribution source)
    {
        if (!source.Range.IsValid || source.RawData?.Length > source.Range.Length
            || source.RawMask?.Length > source.Range.Length
            || source.RawMask is not null && source.RawData is null)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, resource: source.SourceId);
        var range = source.Range;
        var matches = _buffers.Where(b => b.Range.Target == range.Target && Overlaps(b.Range, range)).ToArray();
        if (matches.Length > 1) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousMapping, resource: source.SourceId);
        var data = _ledger.Allocate(range.Length);
        byte[]? mask;
        switch (source.Kind)
        {
            case NativeImageContributionKind.ZeroRegion:
                mask = _ledger.Allocate(range.Length);
                break;
            case NativeImageContributionKind.PointerZero:
                mask = _ledger.Allocate(range.Length);
                break;
            case NativeImageContributionKind.FactoryProperty:
                if (range.Target.Space != NativeImageSpace.PropertyData)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, resource: source.SourceId);
                Copy(source.RawData, data);
                mask = _ledger.Allocate(range.Length); // The archived factory mask is an observation only.
                break;
            case NativeImageContributionKind.NativeCode:
                Copy(source.RawData, data);
                var supplied = source.RawData?.Length ?? 0;
                mask = source.RawMask is not null || supplied < range.Length ? _ledger.Allocate(range.Length) : null;
                if (mask is not null)
                {
                    if (source.RawMask is null) Array.Fill(mask, byte.MaxValue, 0, supplied);
                    else Copy(source.RawMask, mask);
                    for (var i = 0; i < mask.Length; i++) if (mask[i] == 0) data[i] = 0;
                }
                break;
            default:
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource, resource: source.SourceId);
                return;
        }
        if (matches.Length == 0)
        {
            if (_buffers.Count >= 256) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
            var buffer = new Buffer(range, source.Kind, data, mask); buffer.Sources.Add(source.SourceId);
            if (source.Kind == NativeImageContributionKind.NativeCode) buffer.CodeRanges.Add(range);
            _buffers.Add(buffer); return;
        }
        var existing = matches[0];
        if (!existing.Range.Covers(range.ByteOffset, range.Length))
        {
            // One proven pair may grow. Further growing folds need a separate ownership and reservation proof.
            var regionCode = source.Kind == NativeImageContributionKind.NativeCode
                && existing.FirstKind == NativeImageContributionKind.ZeroRegion && existing.Kinds.Count == 1
                && existing.Mask is not null && existing.Mask.All(m => m == 0);
            var pointerFactory = source.Kind == NativeImageContributionKind.FactoryProperty
                && existing.FirstKind == NativeImageContributionKind.PointerZero && existing.Kinds.Count == 1
                && existing.Mask is not null && mask is not null
                && existing.Mask.All(m => m == 0) && mask.All(m => m == 0);
            if (!regionCode && !pointerFactory) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedScenario, resource: source.SourceId);
            var union = NativeImageCoalescer.Coalesce(new(existing.Range, existing.Data, existing.Mask),
                new(range, data, mask), _ledger);
            existing.Range = union.Range; existing.Data = union.Data; existing.Mask = union.Mask;
            if (source.Kind == NativeImageContributionKind.NativeCode) existing.CodeRanges.Add(range);
            existing.Sources.Add(source.SourceId); existing.Kinds.Add(source.Kind);
            return;
        }
        var start = checked((int)(range.ByteOffset - existing.Range.ByteOffset));
        // Contained, disjoint code writes retain their original allocation behavior.
        if (source.Kind == NativeImageContributionKind.NativeCode
            && existing.FirstKind == NativeImageContributionKind.ZeroRegion
            && existing.Kinds.All(k => k is NativeImageContributionKind.ZeroRegion or NativeImageContributionKind.NativeCode)
            && existing.Mask is not null && !existing.CodeRanges.Any(r => Overlaps(r, range)))
        {
            for (var i = 0; i < range.Length; i++)
            {
                var incoming = mask is null ? byte.MaxValue : mask[i];
                existing.Data[start + i] = (byte)((existing.Data[start + i] & ~incoming) | (data[i] & incoming));
                existing.Mask[start + i] |= incoming;
            }
            existing.CodeRanges.Add(range);
        }
        else if (source.Kind == NativeImageContributionKind.FactoryProperty
            && existing.FirstKind == NativeImageContributionKind.PointerZero
            && existing.Kinds.All(k => k == NativeImageContributionKind.PointerZero)
            && existing.Mask is not null && mask is not null
            && existing.Mask.All(m => m == 0) && mask.All(m => m == 0))
        {
            for (var i = 0; i < range.Length; i++) existing.Data[start + i] |= data[i];
        }
        else
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedScenario, resource: source.SourceId);
        existing.Sources.Add(source.SourceId); existing.Kinds.Add(source.Kind);
    }
    private void Transition(NativeImagePostTransition transition)
    {
        switch (transition.Kind)
        {
            case NativeImagePostTransitionKind.ResetMasksToZero:
                if (transition.Range is not null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
                foreach (var buffer in _buffers)
                {
                    if (buffer.Mask is null) buffer.Mask = _ledger.Allocate(buffer.Data.Length);
                    else Array.Clear(buffer.Mask);
                }
                break;
            case NativeImagePostTransitionKind.MaterializeNullMasks:
                if (transition.Range is not null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
                foreach (var buffer in _buffers)
                    buffer.Mask ??= _ledger.Allocate(buffer.Data.Length, byte.MaxValue);
                break;
            case NativeImagePostTransitionKind.InvalidateStandardMemory:
                if (transition.Range is not { IsValid: true, Target.Space: NativeImageSpace.StandardMemory } range)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
                else
                {
                    var covering = _buffers.Where(b => b.Range.Target == range.Target
                        && b.Range.Covers(range.ByteOffset, range.Length)).ToArray();
                    if (covering.Length > 1) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousMapping);
                    if (covering.Length == 1)
                    {
                        var buffer = covering[0];
                        if (buffer.Mask is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedScenario);
                        Array.Clear(buffer.Mask!, checked((int)(range.ByteOffset - buffer.Range.ByteOffset)), range.Length);
                    }
                }
                break;
            default: NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedScenario); break;
        }
    }

    internal void Apply(NativeRootVariantPopulation population, ParameterPatchByteOrder order)
    {
        if (!_initialized || _parametersApplied || _sealed || !population.IsAvailable || !Enum.IsDefined(order)
            || !_plan!.AcceptsPopulation(population))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        // Check every physical/null allocation first, including records with no data or mask effect.
        foreach (var owner in population.AllocationRequirements)
            _ = Find(owner.Target, owner.ByteOffset, FieldBytes(owner.BitOffset, owner.BitLength));
        ValidateInteractions(population.Claims);
        foreach (var claim in population.Claims)
        {
            if (claim.BitLength is 0 or > 32 || claim.BitOffset > 7)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, claim.RefId);
            var length = FieldBytes(claim.BitOffset, claim.BitLength);
            var buffer = Find(claim.Target, claim.ByteOffset, length);
            if (buffer.Mask is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.MissingAllocation, claim.RefId);
            var start = checked((int)(claim.ByteOffset - buffer.Range.ByteOffset));
            buffer.ParameterWriteBits ??= _ledger.Allocate(buffer.Data.Length);
            if (claim.WritesData && claim.EncodedWord is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, claim.RefId);
            var touches = new SortedDictionary<int, (byte Bits, byte Mask)>();
            FixedScalarPatchCodec.Accumulate(touches, claim.EncodedWord ?? 0, start, claim.BitOffset, claim.BitLength, order);
            foreach (var (index, touch) in touches)
            {
                if (claim.WritesData)
                {
                    buffer.Data[index] = (byte)((buffer.Data[index] & ~touch.Mask) | touch.Bits);
                    buffer.ParameterWriteBits[index] |= touch.Mask;
                }
                if (claim.ValidityClear == NativeRootValidityEffect.FieldBits) buffer.Mask![index] &= (byte)~touch.Mask;
                if (claim.ValiditySet == NativeRootValidityEffect.FieldBits) buffer.Mask![index] |= touch.Mask;
            }
            if (claim.ValidityClear == NativeRootValidityEffect.WholeBytes) Array.Clear(buffer.Mask!, start, length);
            if (claim.ValiditySet == NativeRootValidityEffect.WholeBytes) Array.Fill(buffer.Mask!, byte.MaxValue, start, length);
        }
        _parametersApplied = true;
        _generation++;
    }

    internal sealed class WorkingSeed
    {
        private readonly NativeImageWorkingKernel _kernel;
        private readonly NativeImageInitializationPlan _plan;
        private readonly int _generation;
        private readonly IReadOnlyList<SeedChunk> _chunks;
        private sealed record SeedChunk(NativeImageRange Range, ImmutablePatchBytes Data, ImmutablePatchBytes? Mask);
        internal string Fingerprint { get; }
        private WorkingSeed(NativeImageWorkingKernel kernel, NativeImageInitializationPlan plan)
        {
            _kernel = kernel; _plan = plan; _generation = kernel._generation;
            _chunks = Array.AsReadOnly(kernel._buffers.Select(b => new SeedChunk(b.Range, kernel._ledger.Seal(b.Data),
                b.Mask is null ? null : kernel._ledger.Seal(b.Mask))).ToArray());
            Fingerprint = NativeGeneralHash.Of(new { version = 1, plan = plan.Fingerprint, generation = _generation,
                chunks = _chunks.Select(c => new { c.Range, data = c.Data.Sha256, mask = c.Mask?.Sha256 }).ToArray() });
        }
        internal static WorkingSeed Capture(NativeImageWorkingKernel kernel, NativeImageInitializationPlan plan)
        {
            if (!ReferenceEquals(kernel._plan, plan) || !kernel._initialized || !kernel._parametersApplied || kernel._sealed
                || !plan.IsAvailable || !plan.MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            return new(kernel, plan);
        }
        internal bool BelongsTo(NativeImageWorkingKernel kernel, NativeImageInitializationPlan plan)
            => ReferenceEquals(kernel, _kernel) && ReferenceEquals(plan, _plan)
                && _generation == kernel._generation && !kernel._sealed && plan.MatchesCurrent();
        internal bool MatchesPlan(NativeImageInitializationPlan plan) => BelongsTo(_kernel, plan);
        internal GeneratedTableSourceSpan Read(NativeImageTarget target, uint offset, int length, string? resourceId)
        {
            if (!MatchesPlan(_plan) || length <= 0) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var matches = _chunks.Where(c => c.Range.Target == target && c.Range.Covers(offset, length)).Take(2).ToArray();
            if (matches.Length != 1) NativeGeneralHash.Fail(matches.Length == 0
                ? NativeGeneralIssueCode.MissingAllocation : NativeGeneralIssueCode.AmbiguousMapping);
            var chunk = matches[0]; var start = checked((int)(offset - chunk.Range.ByteOffset));
            var data = _kernel._ledger.Allocate(length); byte[]? mask = chunk.Mask is null ? null : _kernel._ledger.Allocate(length);
            for (var i = 0; i < length; i++) { data[i] = chunk.Data[start + i]; if (mask is not null) mask[i] = chunk.Mask![start + i]; }
            var arena = target.Space switch
            {
                NativeImageSpace.StandardMemory => new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory),
                NativeImageSpace.RelativeMemory => new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, target.Lsm),
                _ => throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedMapping)),
            };
            // This is an encoder input role, paired to the CURRENT working seed; no old source binding is adopted.
            return new(GeneratedTablePayloadOrigin.RetainedCode, resourceId, arena, offset, data, mask);
        }
    }

    internal void ApplyTables(NativeGeneralTableEmission emission, WorkingSeed seed)
    {
        if (!_parametersApplied || _sealed || !emission.IsAvailable || !seed.BelongsTo(this, _plan!)
            || !emission.BelongsTo(_plan!, seed)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        var payloads = emission.Payloads;
        foreach (var group in payloads.GroupBy(p => p.Arena))
        {
            ulong end = 0; var first = true;
            foreach (var payload in group.OrderBy(p => p.ByteOffset))
            {
                if (!first && payload.ByteOffset < end) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
                end = (ulong)payload.ByteOffset + (uint)payload.Length; first = false;
            }
        }
        foreach (var payload in payloads)
        {
            var target = Target(payload.Arena); var buffer = Find(target, payload.ByteOffset, payload.Length);
            var start = checked((int)(payload.ByteOffset - buffer.Range.ByteOffset));
            if (payload.BeforeData?.Length != payload.Length || payload.BeforeMask?.Length != payload.Length
                || buffer.Mask is null || payload.WriteMask.Length != payload.Length)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            for (var i = 0; i < payload.Length; i++)
            {
                if (buffer.Data[start + i] != payload.BeforeData![i] || buffer.Mask![start + i] != payload.BeforeMask![i])
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
                if (((buffer.ParameterWriteBits?[start + i] ?? 0) & payload.WriteMask[i]) != 0)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
            }
        }
        foreach (var payload in payloads)
        {
            var buffer = Find(Target(payload.Arena), payload.ByteOffset, payload.Length);
            var start = checked((int)(payload.ByteOffset - buffer.Range.ByteOffset));
            for (var i = 0; i < payload.Length; i++)
            {
                var mask = payload.WriteMask[i];
                buffer.Data[start + i] = (byte)((buffer.Data[start + i] & ~mask) | (payload.Data[i] & mask));
                buffer.Mask![start + i] |= mask;
            }
        }
        _generation++;
    }
    internal static NativeImageTarget Target(ResourcePatchArena arena) => arena.Kind switch
    {
        ResourcePatchArenaKind.AbsoluteMemory when arena.LoadStateMachine is null => new(NativeImageSpace.StandardMemory),
        ResourcePatchArenaKind.RelativeMemory when arena.LoadStateMachine is not null => new(NativeImageSpace.RelativeMemory, arena.LoadStateMachine),
        _ => throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedMapping)),
    };

    private static void ValidateInteractions(IReadOnlyList<NativeRootWriteClaim> claims)
    {
        // A clear and a set of the same validity byte are order-sensitive even for different data bits.
        foreach (var group in claims.GroupBy(c => c.Target))
        {
            var sets = group.Where(c => c.ValiditySet != NativeRootValidityEffect.None)
                .Select(c => EffectSpan(c, c.ValiditySet))
                .OrderBy(c => c.Start).ToArray();
            var clears = group.Where(c => c.ValidityClear != NativeRootValidityEffect.None)
                .Select(c => EffectSpan(c, c.ValidityClear))
                .OrderBy(c => c.Start).ToArray();
            var at = 0;
            foreach (var clear in clears)
            {
                while (at < sets.Length && sets[at].End <= clear.Start) at++;
                if (at < sets.Length && sets[at].Start < clear.End)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.NoncommutativeValidity);
            }
            ulong end = 0; var first = true;
            foreach (var data in group.Where(c => c.WritesData).OrderBy(c => (ulong)c.ByteOffset * 8 + c.BitOffset))
            {
                var start = (ulong)data.ByteOffset * 8 + data.BitOffset;
                if (!first && start < end) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, data.RefId);
                end = start + data.BitLength; first = false;
            }
        }
    }

    internal IReadOnlyList<NativeGeneralWorkingChunk> Complete()
    {
        if (!_initialized || !_parametersApplied || _sealed) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        var result = new List<NativeGeneralWorkingChunk>();
        foreach (var buffer in _buffers)
        {
            ImmutablePatchBytes? canonical = null;
            if (buffer.Mask is not null)
            {
                var data = _ledger.Allocate(buffer.Data.Length);
                for (var i = 0; i < data.Length; i++) data[i] = (byte)(buffer.Data[i] & buffer.Mask[i]);
                canonical = _ledger.Seal(data);
            }
            buffer.ParameterWriteBits ??= _ledger.Allocate(buffer.Data.Length);
            result.Add(new(buffer.Range, Array.AsReadOnly(buffer.Sources.ToArray()), buffer.InitialData!, buffer.InitialValidity,
                _ledger.Seal(buffer.Data), buffer.Mask is null ? null : _ledger.Seal(buffer.Mask), canonical,
                _ledger.Seal(buffer.ParameterWriteBits)));
        }
        _sealed = true;
        return Array.AsReadOnly(result.ToArray());
    }

    private Buffer Find(NativeImageTarget target, uint offset, int length)
    {
        var matches = _buffers.Where(b => b.Range.Target == target && b.Range.Covers(offset, length)).Take(2).ToArray();
        if (matches.Length != 1) NativeGeneralHash.Fail(matches.Length == 0
            ? NativeGeneralIssueCode.MissingAllocation : NativeGeneralIssueCode.AmbiguousMapping);
        return matches[0];
    }
    private static int FieldBytes(byte offset, uint bits) => checked((int)(((uint)offset + bits + 7) / 8));
    private static (ulong Start, ulong End) EffectSpan(NativeRootWriteClaim claim, NativeRootValidityEffect effect)
    {
        var start = (ulong)claim.ByteOffset * 8; var end = start + claim.BitOffset + claim.BitLength;
        return effect == NativeRootValidityEffect.FieldBits ? (start + claim.BitOffset, end) : (start, (end + 7) / 8 * 8);
    }
    private static bool Overlaps(NativeImageRange left, NativeImageRange right)
        => (ulong)left.ByteOffset < (ulong)right.ByteOffset + (uint)right.Length
            && (ulong)right.ByteOffset < (ulong)left.ByteOffset + (uint)left.Length;
    private static void Copy(ImmutablePatchBytes? source, byte[] destination)
    { if (source is not null) for (var i = 0; i < source.Length; i++) destination[i] = source[i]; }
}
