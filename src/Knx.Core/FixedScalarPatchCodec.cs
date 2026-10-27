namespace Knx;

internal sealed record FixedScalarEncoding(int Bits, uint Word, ParameterPatchIssueCode? Issue = null);

internal static class FixedScalarPatchCodec
{
    internal static FixedScalarEncoding Encode(System.Xml.Linq.XElement shape, ParameterType type, string raw)
        => OfflineParameterPatch.EncodeFixedScalar(shape, type, raw);

    internal static void Accumulate(SortedDictionary<int, (byte Bits, byte Mask)> touches, uint value,
        int start, byte bitOffset, uint bitLength, ParameterPatchByteOrder order)
    {
        for (var bit = 0; bit < bitLength; bit++)
        {
            var sourceBit = order == ParameterPatchByteOrder.BigEndian ? (int)bitLength - 1 - bit : bit / 8 * 8 + 7 - bit % 8;
            var index = checked(start + (bitOffset + bit) / 8);
            var mask = (byte)(1 << (7 - (bitOffset + bit) % 8));
            var old = touches.GetValueOrDefault(index);
            touches[index] = ((byte)(old.Bits | (((value >> sourceBit) & 1u) != 0 ? mask : 0)), (byte)(old.Mask | mask));
        }
    }
}
