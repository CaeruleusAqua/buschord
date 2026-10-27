using System.Reflection;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;

internal static class RawDataParameterChecks
{
    private static ParameterType Raw(string? maximum)
    {
        var element = XElement.Parse("<ParameterType Id='RAW' Name='Raw bytes'><TypeRawData /></ParameterType>");
        element.Element("TypeRawData")!.SetAttributeValue("MaxSize", maximum);
        return Ksxml.ParseParameterType(element)!;
    }

    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "RAW " + name); if (!ok) failures++; }
        var type = Raw("3");
        var copy = new ParameterType(type);
        Check("decoded byte size is retained separately from bit-size metadata", type.DataType == ParameterDataType.RawData
            && type.RawDataMaxSize == 3 && type.SizeInBit == 0 && type.UnsupportedReason is null
            && copy.RawDataMaxSize == 3 && copy.SizeInBit == 0 && copy.UnsupportedReason is null);
        copy.RawDataMaxSize = 2;
        Check("detached raw declarations do not mutate the shared source", type.RawDataMaxSize == 3 && copy.RawDataMaxSize == 2);
        var bytes = new byte[] { 0, 0x80, 0xff };
        Check("Base64 encodes byte values without hex or text conversion", ParameterValidation.EncodeRawData(type, bytes) == "AID/"
            && ParameterValidation.TryDecodeRawData(type, "AID/", out var decoded, out _) && decoded.SequenceEqual(bytes));
        var allBytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var alphabet = Raw("256");
        Check("the complete byte alphabet roundtrips without a text encoding", ParameterValidation.TryDecodeRawData(alphabet,
            ParameterValidation.EncodeRawData(alphabet, allBytes), out var allDecoded, out _) && allDecoded.SequenceEqual(allBytes));
        Check("empty raw storage is an empty byte array", ParameterValidation.TryDecodeRawData(type, "", out var empty, out _)
            && empty.Length == 0 && ParameterValidation.EncodeRawData(type, ReadOnlySpan<byte>.Empty) == "");
        Check("exact decoded size accepts the boundary and rejects boundary plus one", ParameterValidation.TryValidate(type, "AID/", out _)
            && !ParameterValidation.TryValidate(type, "AID/AA==", out _) && !ParameterValidation.TryDecodeRawData(type, "AID/AA==", out var rejected, out _)
            && rejected.Length == 0);
        var encodingRejected = false;
        try { ParameterValidation.EncodeRawData(type, new byte[4]); } catch (InvalidDataException) { encodingRejected = true; }
        Check("encoding also enforces the declared limit without truncation", encodingRejected && bytes.SequenceEqual(new byte[] { 0, 0x80, 0xff }));
        var one = Raw("1");
        Check("padding is checked by exact decoded bytes within the same encoded quartet", ParameterValidation.TryValidate(one, "AA==", out _)
            && !ParameterValidation.TryValidate(one, "AAA=", out _) && !ParameterValidation.TryValidate(one, "AAAA", out _));
        Check("original Base64 whitespace is accepted without changing the caller string", ParameterValidation.TryDecodeRawData(type, " A I\tD/\r\n", out var spaced, out _)
            && spaced.SequenceEqual(bytes) && ParameterValidation.EncodeRawData(type, spaced) == "AID/");
        Check("malformed base64 and guessed hexadecimal encodings are rejected", new[] { "A", "AA=", "A===", "AA==x", "00-FF", "not-base64", "AA\u00a0==" }
            .All(value => !ParameterValidation.TryValidate(type, value, out _)) && !ParameterValidation.TryValidate(type, null, out _));
        Check("total whitespace input has its own bounded budget before decoding", !ParameterValidation.TryValidate(one, new string(' ', 16 * 1024 * 1024 + 1), out _));
        Check("non-whitespace encoded size is bounded before decoding", !ParameterValidation.TryValidate(one, new string('A', 1024), out _));
        Check("missing zero oversized and malformed MaxSize declarations stay unsupported", new string?[] { null, "0", "1048573", "-1", "1.5", "not-a-size", "4294967296" }
            .Select(Raw).All(invalid => invalid.UnsupportedReason is not null && !ParameterValidation.TryValidate(invalid, "", out _)));
        var maximum = Raw("1048572");
        var maximumData = new byte[1_048_572];
        var maximumStorage = Convert.ToBase64String(maximumData);
        Check("the schema maximum is an exact byte bound", maximum.UnsupportedReason is null && ParameterValidation.TryDecodeRawData(maximum, maximumStorage, out var maximumDecoded, out _)
            && maximumDecoded.Length == maximumData.Length && !ParameterValidation.TryValidate(maximum, Convert.ToBase64String(new byte[1_048_573]), out _));
        var unknown = Ksxml.ParseParameterType(XElement.Parse("<ParameterType Id='RAW' Name='Unknown'><TypeRawData MaxSize='3' Encoding='Hex'/></ParameterType>"))!;
        var bitSize = Ksxml.ParseParameterType(XElement.Parse("<ParameterType Id='RAW' Name='Unknown'><TypeRawData MaxSize='3' SizeInBit='24'/></ParameterType>"))!;
        var legacy = Ksxml.ParseParameterType(XElement.Parse("<ParameterType Id='RAW' Name='Legacy' DataType='RawData' MaxSize='3'/>"))!;
        Check("unknown raw schemas and legacy unproven declarations are read-only", new[] { unknown, bitSize, legacy }.All(invalid => invalid.UnsupportedReason is not null
            && !ParameterValidation.TryValidate(invalid, "AID/", out _)));
        foreach (var limit in new[] { 30_716, 16_380 })
        {
            var jung = Raw(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Check("Jung declared raw block limit " + limit + " is exact", ParameterValidation.TryValidate(jung, Convert.ToBase64String(new byte[limit]), out _)
                && !ParameterValidation.TryValidate(jung, Convert.ToBase64String(new byte[limit + 1]), out _));
        }
        var instance = new ParameterInstance { Id = "R-RAW", Name = "Raw", ParameterType = type, ParameterTypeId = type.Id, Value = "AID/", IsEnabled = true };
        var row = new ParameterItemViewModel(instance, type);
        Check("a valid raw codec does not enable a generic editor or instance write", !row.IsEnabled && row.Kind == ParameterKind.ReadOnly
            && ParameterValidation.TryValidate(type, "AID/", out _) && !ParameterValidation.TryValidate(instance, "AID/", out _));
        Check("raw codecs reject non-raw declarations", !ParameterValidation.TryDecodeRawData(new ParameterType { Id = "N", Name = "Number", DataType = ParameterDataType.Number }, "AID/", out _, out _));

        var document = XDocument.Parse("""
            <KNX><ApplicationProgram Id="M-0001_A-0101-10-0000"><Static><ParameterTypes>
            <ParameterType Id="RAW" Name="Raw"><TypeRawData MaxSize="3" /></ParameterType>
            <ParameterType Id="NUM" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-RAW" Text="Opaque raw value" ParameterType="RAW" Value="00-opaque" />
            <Parameter Id="P-NUM" Text="Number" ParameterType="NUM" Value="1" /></Parameters>
            <ParameterRefs><ParameterRef Id="R-RAW" RefId="P-RAW" /><ParameterRef Id="R-NUM" RefId="P-NUM" /></ParameterRefs></Static>
            <Dynamic><ParameterRefRef RefId="R-RAW" /><ParameterRefRef RefId="R-NUM" /></Dynamic></ApplicationProgram></KNX>
            """);
        var original = document.ToString();
        var parsed = Ksxml.Parse(document);
        Check("reading opaque raw strings preserves them without implicit decoding or rewriting", parsed.Instances.Single(i => i.Id == "R-RAW").Value == "00-opaque"
            && document.ToString() == original);
        var session = new ParameterEditingSession();
        var applied = 0;
        typeof(ParameterEditingSession).GetMethod("LoadResolved", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, ["Raw policy", parsed.Instances, parsed.Types, (Func<bool>)(() => { applied++; return true; }), null, null]);
        var rawRow = session.Rows.Single(r => r.Instance.Id == "R-RAW");
        rawRow.Value = "AID/";
        Check("programmatic raw drafts cannot bypass the generic reviewed Apply policy", !rawRow.IsEnabled && !session.ReviewChanges().IsValid
            && !session.Apply() && applied == 0 && document.ToString() == original);
        return failures;
    }
}
