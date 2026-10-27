namespace Knx;

/// <summary>Private owned materialization buffers; source bytes never enter this type by reference.</summary>
internal sealed record NativeImageMutableSegment(NativeImageRange Range, byte[] Data, byte[]? Mask);

internal static class NativeImageCoalescer
{
    internal static NativeImageMutableSegment Coalesce(NativeImageMutableSegment earlier,
        NativeImageMutableSegment incoming, NativeImagePayloadLedger ledger)
    {
        Validate(earlier); Validate(incoming);
        if (ledger is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidRequest);
        if (ReferenceEquals(earlier.Data, incoming.Data)
            || earlier.Mask is not null && (ReferenceEquals(earlier.Mask, earlier.Data)
                || ReferenceEquals(earlier.Mask, incoming.Data) || ReferenceEquals(earlier.Mask, incoming.Mask))
            || incoming.Mask is not null && (ReferenceEquals(incoming.Mask, incoming.Data)
                || ReferenceEquals(incoming.Mask, earlier.Data)))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
        if (earlier.Range.Target != incoming.Range.Target)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping);

        // Equal locations insert the incoming contribution before the existing one.
        var left = earlier.Range.ByteOffset < incoming.Range.ByteOffset ? earlier : incoming;
        var right = ReferenceEquals(left, earlier) ? incoming : earlier;
        var leftEnd = (ulong)left.Range.ByteOffset + (uint)left.Range.Length;
        var rightEnd = (ulong)right.Range.ByteOffset + (uint)right.Range.Length;
        if ((ulong)right.Range.ByteOffset >= leftEnd || (ulong)left.Range.ByteOffset >= rightEnd)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedScenario);
        var unionLength = Math.Max(leftEnd, rightEnd) - left.Range.ByteOffset;
        if (unionLength > 1024 * 1024 || Math.Max(leftEnd, rightEnd) > (ulong)uint.MaxValue + 1)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        var length = (int)unionLength;
        var oldLength = left.Range.Length;
        var delta = (int)((ulong)right.Range.ByteOffset - left.Range.ByteOffset);
        var overlap = Math.Min(oldLength - delta, right.Range.Length);
        var grows = length > oldLength;
        var growsMask = grows && (left.Mask is not null || right.Mask is not null);
        var allocation = grows ? (long)length * (growsMask ? 2 : 1) : 0;
        // Check the entire operation before the first allocation or receiver mutation.
        if (ledger.AllocatedBytes < 0 || ledger.AllocatedBytes > NativeImagePayloadLedger.Limit
            || allocation > NativeImagePayloadLedger.Limit - ledger.AllocatedBytes)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);

        var data = left.Data;
        var mask = left.Mask;
        if (grows)
        {
            data = ledger.Allocate(length);
            left.Data.CopyTo(data, 0);
            if (growsMask)
            {
                mask = ledger.Allocate(length);
                if (left.Mask is not null) left.Mask.CopyTo(mask, 0);
                else Array.Fill(mask, byte.MaxValue, 0, oldLength);
            }
        }
        for (var j = 0; j < overlap; j++)
        {
            var i = delta + j;
            if (mask is not null && right.Mask is not null)
            {
                var leftMask = mask[i]; var rightMask = right.Mask[j];
                data[i] = leftMask == 0 && rightMask == 0
                    ? (byte)(data[i] | right.Data[j])
                    : (byte)((data[i] & leftMask) | (right.Data[j] & rightMask));
                mask[i] = (byte)(leftMask | rightMask);
            }
            else if (mask is not null)
            {
                data[i] = right.Data[j]; mask[i] = byte.MaxValue;
            }
            else if (right.Mask is null) data[i] = right.Data[j];
            // Null-left/explicit-right containment preserves the old left bytes.
        }
        if (grows)
        {
            var tail = length - oldLength;
            Array.Copy(right.Data, oldLength - delta, data, oldLength, tail);
            if (mask is not null)
            {
                if (right.Mask is not null) Array.Copy(right.Mask, oldLength - delta, mask, oldLength, tail);
                else Array.Fill(mask, byte.MaxValue, oldLength, tail);
            }
        }
        return new(new(left.Range.Target, left.Range.ByteOffset, length), data, mask);
    }

    private static void Validate(NativeImageMutableSegment segment)
    {
        if (segment is null || segment.Range is null || segment.Range.Target is null || !segment.Range.IsValid
            || segment.Data is null || segment.Data.Length != segment.Range.Length
            || segment.Mask is not null && segment.Mask.Length != segment.Range.Length)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
    }
}
