using System.Buffers.Binary;
using System.Xml.Linq;

namespace Knx;

/// <summary>Memory-only RawData framing. Capacity and the prefix/payload write extent are deliberately separate.</summary>
internal static class NativeRootRawDataCodec
{
    // Framing reference (independent implementation): XKNX/xknx-editor c1a23c5,
    // packages/prod/src/xknxeditor/prod/parser_v2/encode.py (_encode_raw_data/_written_width_in_bit),
    // packages/prod/tests/parser_v2/test_encode_sizeless_types.py (nonzero seed and mask tests).
    // https://github.com/XKNX/xknx-editor/blob/c1a23c5da56ddf90d93b0462b4bd565c47f1580f/packages/prod/tests/parser_v2/test_encode_sizeless_types.py
    internal static void ValidateDeclaration(XElement shape, ParameterType type, string id)
    {
        if (shape.Name.LocalName != "TypeRawData" || shape.HasElements || !string.IsNullOrWhiteSpace(shape.Value)
            || shape.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName != "MaxSize"))
            || type.UnsupportedReason is not null || ParameterValidation.RawDataDeclarationError(type) is not null)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedType, id);
    }

    internal static ImmutablePatchBytes Encode(ParameterType type, string value, ParameterPatchByteOrder order, string id)
    {
        // The common evaluation/input capture has the same per-value budget.
        if (value.Length > 65536) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded, id);
        if (!ParameterValidation.TryDecodeRawData(type, value, out var payload, out _))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, id);
        var framed = new byte[checked(4 + payload.Length)];
        if (order == ParameterPatchByteOrder.BigEndian) BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        else if (order == ParameterPatchByteOrder.LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)payload.Length);
        else NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidPolicy, id);
        payload.CopyTo(framed, 4); // Opaque octets never follow numeric byte order.
        return new(framed);
    }
}
