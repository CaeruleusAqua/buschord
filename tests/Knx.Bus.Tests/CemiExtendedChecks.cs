using Knx;
using Knx.Bus;

internal static class CemiExtendedChecks
{
    // Public protocol vectors, not encrypted by the codec under test:
    // XKNX/xknx 82e1a851, test/secure_tests/data_secure_test.py, group-response fixture
    // and test_data_secure_group_send_extended; point-to-point fixture cites AN158 v07 Annex A.
    private const string GroupResponse = "29003CE0400904001103F110002446CFEF4AC085E7092AB062B44D";
    private const string GroupWrite = "11003CE0500104001103F11000254AE1CB67CD98E577B519BE47BB";
    private const string PointToPoint = "2900B060FF67FF002203F1900000000000046767242A2308CA76A11774214EE4CF5D94909F743D050D8FC168";
    private static byte[] Hex(string text) => Convert.FromHexString(text);
    private static Telegram Write(byte[] data) => new() { Source = new(0x11FA), Destination = new(0x3303),
        IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = data };

    internal static int Run()
    {
        var count = 0; var failures = 0;
        void Check(string label, bool condition) { count++; Console.WriteLine((condition ? "PASS " : "FAIL ") + "CemiExtended " + label); if (!condition) failures++; }
        void Refuse(string label, Action action)
        {
            var refused = false; try { action(); } catch (Exception ex) when (ex is FormatException or ArgumentException) { refused = true; }
            Check(label, refused);
        }
        var response = Cemi.ParseLDataFrame(Hex(GroupResponse));
        Check("published extended group vector has one length octet", response.Control1 == 0x3C && response.Control2 == 0xE0
            && response.Source.Raw == 0x4009 && response.DestinationRaw == 0x0400 && response.Apdu.Length == 18
            && response.Apdu.SequenceEqual(Hex("03F110002446CFEF4AC085E7092AB062B44D")));
        Check("published group vector encodes byte-for-byte", Convert.ToHexString(Cemi.EncodeLDataFrame(response)) == GroupResponse);
        var write = new CemiLDataFrame(CemiMessageCode.LDataReq, 0xBC, 0xE0, new(0x5001), 0x0400,
            Hex("03F11000254AE1CB67CD98E577B519BE47BB"));
        Check("published secured write derives extended type from raw TPDU", Convert.ToHexString(Cemi.EncodeLDataFrame(write)) == GroupWrite);
        var point = Cemi.ParseLDataFrame(Hex(PointToPoint));
        Check("AN158 point-to-point vector tolerates standard flag with long payload", point.Control1 == 0xB0 && point.Control2 == 0x60
            && point.Source.Raw == 0xFF67 && point.DestinationRaw == 0xFF00 && point.Apdu.Length == 35 && point.Apdu[0] == 3 && point.Apdu[1] == 0xF1);
        var canonicalPoint = Cemi.EncodeLDataFrame(point); var expectedPoint = Hex(PointToPoint); expectedPoint[2] = 0x30;
        Check("encoding canonicalizes only received frame-type choice", canonicalPoint.SequenceEqual(expectedPoint));
        foreach (var (size, npdu, control1) in new[] { (0,1,0xBC), (1,2,0xBC), (14,15,0xBC), (15,16,0x3C), (63,64,0x3C), (127,128,0x3C), (253,254,0x3C) })
        {
            var data = Enumerable.Range(0,size).Select(i => (byte)i).ToArray(); var encoded = Cemi.LDataRequest(Write(data));
            // Header constants, payload extent and first TPDU octets are checked independently of the parser.
            Check("wire boundary data=" + size, encoded.Length == 11 + size && encoded[0] == 0x11 && encoded[1] == 0
                && encoded[2] == control1 && encoded[3] == 0xE0 && encoded.AsSpan(4,4).SequenceEqual(Hex("11FA3303"))
                && encoded[8] == npdu && encoded[9] == 0 && encoded[10] == 0x80 && encoded.AsSpan(11).SequenceEqual(data));
            var parsed = Cemi.ParseLData(encoded);
            Check("decode boundary data=" + size, parsed.Source.Raw == 0x11FA && parsed.Destination.Raw == 0x3303 && parsed.IsGroupAddress
                && parsed.HopCount == 6 && parsed.Apci == Apci.GroupValueWrite
                && (size == 0 ? parsed.Data is [0] && parsed.DataBits == 6 : parsed.Data.SequenceEqual(data) && parsed.DataBits is null));
        }
        Refuse("NPDU255 escape is not emitted", () => Cemi.LDataRequest(Write(new byte[254])));
        Refuse("old1024-octet extension is not emitted", () => Cemi.LDataRequest(Write(new byte[1022])));
        var escape = new byte[265]; Hex("29003CE011FA3303FF").CopyTo(escape,0);
        Refuse("NPDU255 escape is not accepted", () => Cemi.ParseLDataFrame(escape));
        var shortExtended = Hex("29003CE011FA3303010081");
        Check("receiver also tolerates extended flag on short APDU", Cemi.ParseLData(shortExtended).Data is [1]
            && Cemi.ParseLDataFrame(shortExtended).Control1 == 0x3C);
        // Published XKNX raw T_Connect vector. NPDU0 is a one-octet control TPDU, not an empty APDU.
        var control = Cemi.ParseLDataFrame(Hex("29008000000000000080"));
        Check("raw seam retains one-octet transport control", control.Apdu is [0x80] && control.Source.Raw == 0 && control.DestinationRaw == 0);
        Check("control TPDU wire vector remains byte-for-byte", Convert.ToHexString(Cemi.EncodeLDataFrame(control)) == "29008000000000000080");
        Refuse("Telegram parser does not invent APCI for transport control", () => Cemi.ParseLData(Hex("29008000000000000080")));
        var numbered = Cemi.ParseLDataFrame(Hex("2900BC6011101120014300"));
        Check("raw seam retains TPCI and all APCI bits", numbered.Apdu is [0x43,0] && Convert.ToHexString(Cemi.EncodeLDataFrame(numbered)) == "2900BC6011101120014300");
        // Calimero CEMILDataExTest uses PL information 10 20 and an extended timestamp 80 02 03 04.
        var additionalVector = Hex("290A010210200604800203043CE0400904001103F110002446CFEF4AC085E7092AB062B44D");
        var additional = Cemi.ParseLDataFrame(additionalVector);
        Check("additional information does not shift or truncate extended TPDU", additional.AdditionalInformation.SequenceEqual(Hex("01021020060480020304"))
            && additional.Apdu.SequenceEqual(response.Apdu) && additional.Control1 == 0x3C && additional.Source.Raw == 0x4009);
        Check("additional records retain their exact order and values", Cemi.EncodeLDataFrame(additional).SequenceEqual(additionalVector));
        var unknownInfo = Hex("F003123456");
        var unknown = new CemiLDataFrame(CemiMessageCode.LDataCon,0x1D,0xD0,new(0x1234),0xABCD,[0,0x81],unknownInfo);
        Check("unknown additional type stays opaque and supported flags survive", Convert.ToHexString(Cemi.EncodeLDataFrame(unknown)) == "2E05F0031234569DD01234ABCD010081");
        var originalApdu = new byte[] { 0x03,0xF1,0x10 }; var originalInfo = Hex("04021234");
        var owned = new CemiLDataFrame(CemiMessageCode.LDataInd,0xBC,0xE0,new(1),2,originalApdu,originalInfo);
        originalApdu[0]=0xFF; originalInfo[0]=0xFF; var outgoingApdu=owned.Apdu; var outgoingInfo=owned.AdditionalInformation;
        outgoingApdu[0]=0xFF; outgoingInfo[0]=0xFF;
        Check("raw frame owns inputs and returns detached arrays", owned.Apdu is [3,0xF1,0x10] && owned.AdditionalInformation.SequenceEqual(Hex("04021234")));
        var inputCopy=Hex(GroupResponse); var parsedOwned=Cemi.ParseLDataFrame(inputCopy); inputCopy[9]=0;
        Check("parsed raw frame owns its wire data", parsedOwned.Apdu[0]==3);
        var maxInfo = new byte[255]; maxInfo[0]=0xF0; maxInfo[1]=253;
        var maxAdditional = new CemiLDataFrame(CemiMessageCode.LDataInd,0xBC,0xE0,new(1),2,[0,0x81],maxInfo);
        Check("one-byte additional-info length supports255octets", Cemi.EncodeLDataFrame(maxAdditional).Length == 266
            && Cemi.ParseLDataFrame(Cemi.EncodeLDataFrame(maxAdditional)).AdditionalInformation.Length == 255);
        Refuse("additional info beyond255octets is refused", () => _=new CemiLDataFrame(CemiMessageCode.LDataInd,0xBC,0xE0,new(1),2,[0,0x81],new byte[256]));
        Refuse("empty raw TPDU is refused", () => _=new CemiLDataFrame(CemiMessageCode.LDataInd,0xBC,0xE0,new(1),2,[]));
        Refuse("raw TPDU beyond255octets is refused", () => _=new CemiLDataFrame(CemiMessageCode.LDataInd,0xBC,0xE0,new(1),2,new byte[256]));
        foreach (var value in new byte[] { 1,4,7,15 })
        {
            var bad=Hex(GroupResponse); bad[3]|=value;
            Refuse("reserved or LTE frame format is refused="+value,()=>Cemi.ParseLDataFrame(bad));
            Refuse("unsupported outgoing frame format is refused="+value,()=>Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataReq,0xBC,value,new(1),2,[0,0])));
        }
        var standard = Hex("2900BCE011FA3303010081");
        for (var i=0;i<standard.Length;i++)
        { var truncated=standard[..i]; Refuse("every standard header/payload truncation="+i,()=>Cemi.ParseLDataFrame(truncated)); }
        foreach (var i in new[] { 0,1,8,9,10,20,26 })
        { var truncated=Hex(GroupResponse)[..i]; Refuse("extended truncation="+i,()=>Cemi.ParseLDataFrame(truncated)); }
        Refuse("trailing extended data is refused",()=>Cemi.ParseLDataFrame([..Hex(GroupResponse),0]));
        Refuse("trailing standard data is refused",()=>Cemi.ParseLDataFrame([..standard,0]));
        foreach (var length in new byte[] { 0,2,15,16,64,128,254,255 })
        { var bad=standard.ToArray(); bad[8]=length; Refuse("length byte is neither masked nor ignored="+length,()=>Cemi.ParseLDataFrame(bad)); }
        Refuse("unknown cEMI message code is refused",()=>Cemi.ParseLDataFrame([0x2B,..standard.AsSpan(1).ToArray()]));
        Refuse("unsupported output message code is refused",()=>Cemi.EncodeLDataFrame(new((CemiMessageCode)0x2B,0xBC,0xE0,new(1),2,[0,0])));
        Refuse("additional length cannot consume control fields",()=>Cemi.ParseLDataFrame([0x29,255,..standard.AsSpan(2).ToArray()]));
        Refuse("partial additional TLV header is refused",()=>Cemi.ParseLDataFrame([0x29,1,4,..standard.AsSpan(2).ToArray()]));
        Refuse("additional TLV cannot overrun its declared block",()=>Cemi.ParseLDataFrame([0x29,3,4,2,0x12,..standard.AsSpan(2).ToArray()]));
        Refuse("outgoing malformed additional TLV is refused",()=>Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataReq,0xBC,0xE0,new(1),2,[0,0],[4,2,0x12])));
        // The former private 0x40/two-octet length scheme is not a cEMI encoding.
        Refuse("old length-marker wire format is refused",()=>Cemi.ParseLDataFrame(Hex("2900BCE011FA330340100080000102030405060708090A0B0C0D0E")));
        var descriptor=Management.DeviceDescriptorRead(new(0x1110));
        Check("short M112 descriptor request remains unchanged", Convert.ToHexString(descriptor)=="1100BC6000001110010300"
            && Cemi.ParseLData(descriptor).Apci==Apci.DeviceDescriptorRead);
        var restart=Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataReq,0xBC,0x60,new(0x11F9),0x1110,[3,0x80]));
        Check("short M112 Basic Restart remains unchanged", Convert.ToHexString(restart)=="1100BC6011F91110010380");
        Console.WriteLine($"CemiExtended: {count} checks, {failures} failures"); return failures;
    }
}
