namespace Knx;

internal sealed partial class NativeRootWriterPopulation
{
    private void CoalesceUnions(List<NativeRootWriterRecord> records, List<NativeRootWriteClaim> claims)
    {
        var unionParameters = _inventory!.UnionByParameter;
        var values = _values.GroupBy(v => v.Reference.ParameterId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var recordIndex = records.Select((r, i) => (r.RefId, i)).ToDictionary(p => p.RefId, p => p.i, StringComparer.Ordinal);
        claims.RemoveAll(c => unionParameters.ContainsKey(records[recordIndex[c.RefId]].ParameterId));
        foreach (var group in claims.GroupBy(c => c.OwnerKey).Where(g => g.Count() > 1).ToArray())
        {
            var writers = group.Where(c => c.WritesData).ToArray();
            if (writers.Select(c => c.EncodedWord).Distinct().Count() > 1)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, writers[0].RefId);
            var retained = writers.FirstOrDefault() ?? group.First();
            foreach (var other in group.Where(c => !ReferenceEquals(c, retained)))
            {
                claims.Remove(other);
                var index = recordIndex[other.RefId];
                records[index] = records[index] with { Claim = null, WritesData = false, NativeRecordEmitted = false };
            }
        }
        var order = _inventory.Policy!.ByteOrder;
        foreach (var union in _inventory.Unions)
        {
            var owner = union.DefaultOwner;
            var length = checked((int)(owner.BitLength / 8));
            values.TryGetValue(owner.ParameterId, out var defaults);
            var applicable = defaults?.Where(v => _active.GetValueOrDefault(v.Reference.RefId) != 0 || v.Reference.Always).ToArray() ?? [];
            if (applicable.Select(v => v.CurrentWord).Distinct().Count() > 1)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, owner.ParameterId);
            var initial = applicable.FirstOrDefault() ?? defaults?.FirstOrDefault();
            var data = new byte[length];
            var initialTouches = new SortedDictionary<int, (byte Bits, byte Mask)>();
            FixedScalarPatchCodec.Accumulate(initialTouches, applicable.FirstOrDefault()?.CurrentWord ?? union.InitialWord, 0, 0, owner.BitLength, order);
            foreach (var (index, touch) in initialTouches) data[index] = touch.Bits;
            var written = new byte[length]; var activeBits = new byte[length];
            foreach (var id in union.ParameterIds)
            {
                if (!values.TryGetValue(id, out var aliases)) continue;
                foreach (var value in aliases)
                {
                var reference = value.Reference;
                var rowIndex = recordIndex[reference.RefId]; var row = records[rowIndex];
                records[rowIndex] = row with { Claim = null, WritesData = false, NativeRecordEmitted = false };
                if (id == owner.ParameterId || (!row.Active && !reference.Always) || row.CustomerExcluded) continue;
                if (value.CurrentWord is null || reference.Owner is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, reference.RefId);
                var alias = reference.Owner!;
                var touches = new SortedDictionary<int, (byte Bits, byte Mask)>();
                FixedScalarPatchCodec.Accumulate(touches, value.CurrentWord!.Value, checked((int)(alias.ByteOffset - owner.ByteOffset)),
                    alias.BitOffset, alias.BitLength, order);
                foreach (var (index, touch) in touches)
                {
                    var overlap = (byte)(written[index] & touch.Mask);
                    if (((activeBits[index] ^ touch.Bits) & overlap) != 0)
                        NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, reference.RefId);
                    written[index] |= touch.Mask; activeBits[index] = (byte)((activeBits[index] & ~touch.Mask) | touch.Bits);
                    data[index] = (byte)((data[index] & ~touch.Mask) | touch.Bits);
                }
                }
            }
            uint word = 0;
            for (var i = 0; i < length; i++)
                word |= (uint)data[i] << (8 * (order == ParameterPatchByteOrder.BigEndian ? length - 1 - i : i));
            var refId = initial?.Reference.RefId ?? owner.ParameterId;
            var claim = new NativeRootWriteClaim(refId, owner.OwnerKey, owner.Target, owner.ByteOffset, 0,
                owner.BitLength, true, word, NativeRootValidityEffect.WholeBytes, NativeRootValidityEffect.None);
            claims.Add(claim);
            if (initial is not null)
            {
                var rowIndex = recordIndex[refId];
                records[rowIndex] = records[rowIndex] with { Claim = claim, WritesData = true, NativeRecordEmitted = true };
            }
        }
    }
}
