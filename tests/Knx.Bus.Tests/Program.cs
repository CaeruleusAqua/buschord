using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

// ============ Self-Checks für das Bus-Protokoll und den Download ============
// Byte-Vektoren aus der KNXnet/IP-Spezifikation (KNX Association, v1.0) und
// End-to-End-Läufe gegen einen Loopback-Fake.

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

// Read-only live probes (never send a bus write):
//   --live <gateway-ip> [listen-seconds]         tunnel, passive listen, one GroupValueRead, disconnect
//   --routing-live <local-ip> [listen-seconds]   join the routing group, passive listen, leave
if (args is ["--live", var liveHost, ..])
{
    var seconds = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 8;
    Environment.Exit(await LiveProbeAsync(liveHost, seconds));
    return;
}

if (args is ["--routing-live", var localIp, ..])
{
    var seconds = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 15;
    Environment.Exit(await RoutingLiveAsync(localIp, seconds));
    return;
}

// Failed/cancelled facade acquisition must leave a clean, retryable connection.
await using (var local = new LocalTunnelServer(0x22, null, 0))
await using (var bus = new KnxBus())
{
    var rejected = false;
    try { await bus.ConnectTunnelAsync(local.Device, new IndividualAddress(1, 1, 250)); }
    catch (IOException) { rejected = true; }
    Check("failed tunnel acquisition leaves facade empty", rejected && bus.State == BusConnectionState.Disconnected && bus.DeviceName is null);
    using var cancel = new CancellationTokenSource();
    var cancelled = false;
    try
    {
        var connecting = bus.ConnectTunnelAsync(local.Device, new IndividualAddress(1, 1, 250), cancel.Token);
        await local.IgnoredConnect.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancel.CancelAsync();
        await connecting;
    }
    catch (OperationCanceledException) { cancelled = true; }
    catch (InvalidOperationException) { }
    Check("cancelled tunnel acquisition leaves facade empty", cancelled && bus.State == BusConnectionState.Disconnected && bus.DeviceName is null);
    try
    {
        var callbacks = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.TelegramReceived += _ => { Interlocked.Increment(ref callbacks); received.TrySetResult(); };
        await bus.ConnectTunnelAsync(local.Device, new IndividualAddress(1, 1, 250));
        await local.TelegramAsync();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await local.TelegramAsync(); // same sequence is a retransmit, not a second telegram
        await Task.Delay(50);
        Check("failed and cancelled tunnel retries succeed with one callback", bus.State == BusConnectionState.Connected && callbacks == 1);
        await bus.DisposeAsync();
        Check("disposed facade clears connection name", bus.DeviceName is null && bus.State == BusConnectionState.Disconnected);
    }
    catch (InvalidOperationException ex) { Check("failed and cancelled tunnel retries succeed with one callback", false, ex.Message); }
}

// ---- 1. KNXnet/IP Frame-Codec (Spezifikations-Bytes) ----
{
    // SEARCH_REQUEST an Multicast, HPAI any
    var search = KnxNetIpFrame.Encode(ServiceType.SearchRequest, Hpai.Any());
    Check("SEARCH_REQUEST frame", search is [0x06, 0x10, 0x02, 0x01, 0x00, 0x0E, 0x08, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
    var decoded = KnxNetIpFrame.Decode(search);
    Check("frame round-trip", decoded.Service == ServiceType.SearchRequest);

    // XKNX tunnelling_ack_test.py: Dienst steht an Octet 2-3, Gesamtlänge an 4-5.
    var externalAck = KnxNetIpFrame.Decode(Convert.FromHexString("06100421000A042A1700"));
    Check("header: service 2-3, length 4-5", externalAck.Service == ServiceType.TunnellingAck
        && externalAck.Body.Length == 4 && externalAck.Body[1] == 42 && externalAck.Body[2] == 23);

    // Echter MDT-Fehlerframe: Header nennt 8 Bytes, das Datagramm ist länger (Padding).
    var padded = KnxNetIpFrame.Decode(Convert.FromHexString("0610020600080024000000000000000000"));
    Check("declared length wins over trailing bytes", padded.Body is [0x00, 0x24]);

    // Kopfbytes ablehnen
    try { KnxNetIpFrame.Decode(new byte[] { 0x06, 0x11, 0x00, 0x0E, 0x02, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 }); Check("magic-Check", false); }
    catch (FormatException) { Check("magic-Check", true); }
    try { KnxNetIpFrame.Decode(Convert.FromHexString("061002050012")); Check("short frame rejected", false); }
    catch (FormatException) { Check("short frame rejected", true); }
}

// ---- 2. HPAI ----
{
    var hpai = Hpai.Control(new IPEndPoint(IPAddress.Parse("192.168.1.17"), 3671));
    Check("HPAI control bytes", hpai is [0x08, 0x01, 192, 168, 1, 17, 0x0E, 0x57], Convert.ToHexString(hpai));
    var read = Hpai.Read(hpai);
    Check("HPAI round-trip", read.Endpoint.Port == 3671 && read.Endpoint.Address.ToString() == "192.168.1.17");
    Check("HPAI data endpoint is UDP", Hpai.Data(read.Endpoint).SequenceEqual(hpai));
    try { Hpai.Read(new byte[] { 0, 1 }); Check("HPAI rejects truncated input", false); }
    catch (FormatException) { Check("HPAI rejects truncated input", true); }
    try { Hpai.Read([0x08, 0x02, 192, 168, 1, 17, 0x0E, 0x57]); Check("HPAI rejects TCP", false); }
    catch (FormatException) { Check("HPAI rejects TCP", true); }
}

// ---- 2b. CONNECT/DISCONNECT/TUNNELLING-Layouts gegen externe Byte-Vektoren ----
{
    // XKNX connect_request_test.py: HPAI control + HPAI data + CRI, kein führendes Statusbyte.
    var externalConnect = Convert.FromHexString("06100205001A0801C0A82A0184950801C0A82A01CCA904040200");
    var connectBody = Connection.ConnectRequest(
        new IPEndPoint(IPAddress.Parse("192.168.42.1"), 33941),
        new IPEndPoint(IPAddress.Parse("192.168.42.1"), 52393));
    Check("CONNECT_REQUEST body = HPAI + HPAI + CRI",
        KnxNetIpFrame.Encode(ServiceType.ConnectRequest, connectBody).SequenceEqual(externalConnect),
        Convert.ToHexString(connectBody));
    Check("CONNECT_REQUEST has no status byte", connectBody.Length == 20 && connectBody[16] == 0x04
        && Cri.Tunnel() is [0x04, 0x04, 0x02, 0x00], Convert.ToHexString(connectBody));

    // XKNX connect_response_test.py: channel, status, HPAI, CRD.
    var externalResponse = KnxNetIpFrame.Decode(
        Convert.FromHexString("06100206001401000801C0A82A0A0E57040411FF"));
    var connectResponse = ConnectResponse.Parse(externalResponse.Body);
    Check("CONNECT_RESPONSE channel/status/HPAI/CRD", connectResponse.Succeeded
        && connectResponse.Channel == 1
        && connectResponse.DataEndpoint!.ToString() == "192.168.42.10:3671"
        && connectResponse.ConnectionData!.Value.AssignedAddress!.Value.Raw == 4607,
        Convert.ToHexString(externalResponse.Body));
    Check("tunnelling CRD = 04 04 IA, ohne Channel",
        Crd.Tunnel(new IndividualAddress(4607)) is [0x04, 0x04, 0x11, 0xFF]);
    try { Crd.Parse(Convert.FromHexString("08040411FF0000")); Check("CRD rejects length 8", false); }
    catch (FormatException) { Check("CRD rejects length 8", true); }

    // Echter MDT-Fehlerframe (XKNX connect_response_test.py): nur Channel + Status, kein CRD.
    var mdtError = ConnectResponse.Parse(KnxNetIpFrame.Decode(
        Convert.FromHexString("0610020600080024000000000000000000")).Body);
    Check("CONNECT_RESPONSE error carries no CRD",
        mdtError is { Status: 0x24, DataEndpoint: null, ConnectionData: null });

    // XKNX disconnect_request/connectionstate_request: channel, 0x00, HPAI.
    Check("DISCONNECT_REQUEST body = channel + 0 + HPAI", KnxNetIpFrame.Encode(ServiceType.DisconnectRequest,
            Connection.DisconnectRequest(21, new IPEndPoint(IPAddress.Parse("192.168.200.12"), 50100)))
        .SequenceEqual(Convert.FromHexString("06100209001015000801C0A8C80CC3B4")));
    Check("CONNECTIONSTATE_REQUEST body = channel + 0 + HPAI", KnxNetIpFrame.Encode(ServiceType.ConnectionStateRequest,
            Connection.ConnectionStateRequest(21, new IPEndPoint(IPAddress.Parse("192.168.200.12"), 50100)))
        .SequenceEqual(Convert.FromHexString("06100207001015000801C0A8C80CC3B4")));

    // XKNX tunnelling_request_test.py: Verbindungsheader 04 <channel> <seq> 00 + cEMI.
    var externalTunnel = KnxNetIpFrame.Decode(
        Convert.FromHexString("061004200015040117001100BCE000004808010081"));
    var (tunnelChannel, tunnelSequence, tunnelCemi) = Tunnelling.ReadRequest(externalTunnel.Body);
    Check("TUNNELLING_REQUEST header", tunnelChannel == 1 && tunnelSequence == 23
        && tunnelCemi.SequenceEqual(Convert.FromHexString("1100BCE000004808010081")), Convert.ToHexString(tunnelCemi));
    Check("TUNNELLING_REQUEST encode", Tunnelling.Request(1, 23, tunnelCemi) is [0x04, 0x01, 0x17, 0x00, ..]);

    // ACK-Body: 04 <channel> <seq> <status> - das vierte Octet ist der Status, kein Typbyte.
    var (ackChannel, ackSequence, ackStatus) = Tunnelling.ReadAck(Convert.FromHexString("042A1700"));
    Check("TUNNELLING_ACK body", ackChannel == 42 && ackSequence == 23 && ackStatus == 0x00);
    Check("TUNNELLING_ACK encode", Tunnelling.Ack(42, 23) is [0x04, 0x2A, 0x17, 0x00]);
    try { Tunnelling.ReadAck([0x03, 0x2A, 0x17, 0x00]); Check("ACK rejects header length 3", false); }
    catch (FormatException) { Check("ACK rejects header length 3", true); }
}

// ---- 3. cEMI L_Data: frame fixtures ----
{
    // Frame fixtures for E6/E7 forwarding:
    // ctrl2 bit 7 is the group flag, bits 6..4 the hop count, APCI lives in octets 7-8.
    var hop4Byte = Convert.FromHexString("2900BCC013033303020080A8");
    var hop5Bit = Convert.FromHexString("2900BCD010063015010081");
    var hop4Word = Convert.FromHexString("2900BCC01303330B0300801CC2");
    var hop6Bit = Convert.FromHexString("2900BCE010063015010081");

    var b4 = Cemi.ParseLData(hop4Byte);
    Check("capture hop 4: group flag + hop count", b4.IsGroupAddress && b4.HopCount == 4,
        $"group={b4.IsGroupAddress} hop={b4.HopCount}");
    Check("capture hop 4: source and group destination",
        b4.Source == new IndividualAddress(0x1303) && Cemi.DestinationGroup(b4) == new GroupAddress(0x3303));
    Check("capture hop 4: GroupValueWrite with one data octet",
        b4.Apci == Apci.GroupValueWrite && b4.Data is [0xA8] && b4.DataBits is null,
        $"{b4.Apci} {Convert.ToHexString(b4.Data)}");

    var b5 = Cemi.ParseLData(hop5Bit);
    Check("capture hop 5: group flag + hop count", b5.IsGroupAddress && b5.HopCount == 5,
        $"group={b5.IsGroupAddress} hop={b5.HopCount}");
    Check("capture hop 5: GroupValueWrite with inline 1-bit value",
        b5.Apci == Apci.GroupValueWrite && b5.Data is [0x01] && b5.DataBits is 6,
        $"{b5.Apci} {Convert.ToHexString(b5.Data)} bits={b5.DataBits}");
    Check("capture hop 5: destination 6/0/21", Cemi.DestinationGroup(b5) == GroupAddress.ThreeLevel(6, 0, 21));

    var w4 = Cemi.ParseLData(hop4Word);
    Check("capture hop 4: GroupValueWrite with two data octets",
        w4.Apci == Apci.GroupValueWrite && w4.Data is [0x1C, 0xC2], Convert.ToHexString(w4.Data));
    var b6 = Cemi.ParseLData(hop6Bit);
    Check("capture hop 6: group flag + hop count", b6.IsGroupAddress && b6.HopCount == 6 && b6.Data is [0x01]);

    // Individual-addressed frames carry the same hop count with the group flag cleared.
    var ind4 = Cemi.ParseLData(Convert.FromHexString("2900BC4010033015010081"));
    var ind5 = Cemi.ParseLData(Convert.FromHexString("2900BC5010033015010081"));
    Check("individual hop 4: no group flag, hop 4",
        !ind4.IsGroupAddress && ind4.HopCount == 4 && ind4.Apci == Apci.GroupValueWrite);
    Check("individual hop 5: no group flag, hop 5", !ind5.IsGroupAddress && ind5.HopCount == 5);

    static Telegram Copy(Telegram t) => new()
    {
        Source = t.Source, Destination = t.Destination, IsGroupAddress = t.IsGroupAddress,
        HopCount = t.HopCount, Apci = t.Apci, DataBits = t.DataBits, Data = t.Data,
    };
    Check("capture hop 4 re-encodes byte-identically",
        Cemi.LDataRequest(Copy(b4)).AsSpan(2).SequenceEqual(hop4Byte.AsSpan(2)),
        Convert.ToHexString(Cemi.LDataRequest(Copy(b4))));
    Check("capture hop 5 re-encodes byte-identically",
        Cemi.LDataRequest(Copy(b5)).AsSpan(2).SequenceEqual(hop5Bit.AsSpan(2)),
        Convert.ToHexString(Cemi.LDataRequest(Copy(b5))));
    Check("capture two-octet write re-encodes byte-identically",
        Cemi.LDataRequest(Copy(w4)).AsSpan(2).SequenceEqual(hop4Word.AsSpan(2)));

    // The outbound read fixture has all APCI bits set to zero.
    var read = Cemi.LDataRequest(new Telegram
    {
        Source = new IndividualAddress(0x11FA),
        Destination = new IndividualAddress(0x3303),
        IsGroupAddress = true,
        Apci = Apci.GroupValueRead,
        Data = [],
    });
    // The APDU is two octets (octet 7 + octet 8); a read carries zeroes in both.
    Check("group read frame matches the wire",
        read is [0x11, 0x00, 0xBC, 0xE0, 0x11, 0xFA, 0x33, 0x03, 0x01, 0x00, 0x00], Convert.ToHexString(read));

    // 1-bit values stay inline in octet 8; whole octets follow it.
    var bit = Cemi.LDataRequest(new Telegram
    {
        Source = new IndividualAddress(0x11FA), Destination = new IndividualAddress(0x3303),
        IsGroupAddress = true, Apci = Apci.GroupValueWrite, DataBits = 1, Data = [0x01],
    });
    Check("group write 1-bit frame", bit is [0x11, 0x00, 0xBC, 0xE0, 0x11, 0xFA, 0x33, 0x03, 0x01, 0x00, 0x81],
        Convert.ToHexString(bit));
    var octet = Cemi.LDataRequest(new Telegram
    {
        Source = new IndividualAddress(0x11FA), Destination = new IndividualAddress(0x3303),
        IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = [0xA8],
    });
    Check("group write 1-octet frame", octet is [0x11, 0x00, 0xBC, 0xE0, 0x11, 0xFA, 0x33, 0x03, 0x02, 0x00, 0x80, 0xA8],
        Convert.ToHexString(octet));
    var octetBack = Cemi.ParseLData(octet);
    Check("group write round-trip", octetBack.Apci == Apci.GroupValueWrite && octetBack.Data is [0xA8]);
    try
    {
        Cemi.LDataRequest(new Telegram
        {
            Source = new IndividualAddress(0x11FA), Destination = new IndividualAddress(0x3303),
            IsGroupAddress = true, Apci = Apci.GroupValueWrite, DataBits = 1, Data = [0x02],
        });
        Check("value wider than its declared bits is rejected", false);
    }
    catch (ArgumentOutOfRangeException) { Check("value wider than its declared bits is rejected", true); }

    // Extended length field: lengths above a standard APDU round-trip, nothing is silently cut.
    var longData = Enumerable.Range(0, 15).Select(i => (byte)i).ToArray();
    var extended = Cemi.LDataRequest(new Telegram
    {
        Source = new IndividualAddress(0x11FA), Destination = new IndividualAddress(0x3303),
        IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = longData,
    });
    Check("extended frame length flag", (extended[8] & 0xC0) == 0x40, Convert.ToHexString(extended[..12]));
    Check("extended frame round-trip", Cemi.ParseLData(extended).Data.SequenceEqual(longData));
}

// ---- 4. Management-Frames ----
{
    // Descriptor type is the 4-bit management parameter in APDU octet 8, not an appended data octet.
    var desc = Management.DeviceDescriptorRead(new IndividualAddress(1, 1, 16));
    var descT = Cemi.ParseLData(desc);
    Check("descriptor read: wire frame",
        desc is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x10, 0x01, 0x03, 0x00], Convert.ToHexString(desc));
    Check("descriptor read", descT.Apci == Apci.DeviceDescriptorRead
        && descT.Data is [0x00] && descT.Destination == new IndividualAddress(1, 1, 16)
        && !descT.IsGroupAddress && descT.HopCount == 6);
    var desc2 = Management.DeviceDescriptorRead(new IndividualAddress(1, 1, 16), 2);
    Check("descriptor type 2 is the APCI parameter",
        desc2 is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x10, 0x01, 0x03, 0x02],
        Convert.ToHexString(desc2));
    Check("descriptor type 2 parses back", Cemi.ParseLData(desc2).Data is [0x02]);
    try { Management.DeviceDescriptorRead(new IndividualAddress(1, 1, 16), 4); Check("descriptor type 4 rejected", false); }
    catch (ArgumentOutOfRangeException) { Check("descriptor type 4 rejected", true); }

    var iaWrite = Management.IndividualAddressWrite(new IndividualAddress(1, 2, 3));
    var iaParsed = Cemi.ParseLData(iaWrite);
    Check("IA write", iaParsed.Apci == Apci.IndividualAddressWrite
        && iaParsed.Data is [0x12, 0x03] && iaParsed.Destination == new IndividualAddress(1, 2, 3)
        && iaWrite[3] == 0x60, Convert.ToHexString(iaWrite));

    var restart = Management.Restart(new IndividualAddress(1, 1, 16), erase: false);
    Check("restart frame", Cemi.ParseLData(restart).Apci == Apci.Restart
        && Cemi.ParseLData(restart).Data is [0x00, 0x00]);

    var memWrite = Management.MemoryWrite(new IndividualAddress(1, 1, 16), 0x0123, [0xDE, 0xAD, 0xBE, 0xEF]);
    Check("memory write frame", memWrite is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x10, 0x07, 0x02, 0x80, 0x01, 0x23, 0xDE, 0xAD, 0xBE, 0xEF],
        Convert.ToHexString(memWrite));
    Check("memory write parse", Cemi.ParseLData(memWrite).Apci == Apci.MemoryWrite
        && Cemi.ParseLData(memWrite).Destination == new IndividualAddress(1, 1, 16)
        && Cemi.ParseLData(memWrite).Data is [0x01, 0x23, 0xDE, 0xAD, 0xBE, 0xEF]);

    var memRead = Management.MemoryRead(new IndividualAddress(1, 1, 16), 0x0123, 4);
    Check("memory read frame", memRead is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x10, 0x04, 0x02, 0x00, 0x01, 0x23, 0x04],
        Convert.ToHexString(memRead));
    Check("memory read parses back", Cemi.ParseLData(memRead) is { Apci: Apci.MemoryRead, Data: [0x01, 0x23, 0x04] });
}

// ---- 5. DPT-Codec ----
{
    Check("DPST-1-1 decode", Dpt.Decode("DPST-1-1", [0x01]) == "On");
    Check("DPST-1-1 encode", Dpt.Encode("DPST-1-1", "On") is [0x01]);
    Check("DPST-9-1 decode", Dpt.Decode("DPST-9-1", [0x08, 0x66]) == "21.5", Dpt.Decode("DPST-9-1", [0x08, 0x66]));
    Check("DPST-9-1 encode", Dpt.Encode("DPST-9-1", "21,5") is [0x08, 0x66]);
    Check("DPST-5-1 decode", Dpt.Decode("DPST-5-1", [0x80]) == "50%", Dpt.Decode("DPST-5-1", [0x80]));
    Check("DPST-5-1 encode", Dpt.Encode("DPST-5-1", "50") is [0x80], Convert.ToHexString(Dpt.Encode("DPST-5-1", "50")));
    Check("raw hex", Dpt.Decode(null, [0xAB, 0xCD]) == "AB-CD");
    Check("DPT bit sizes cover project and human ids", Dpt.TryBitSize("DPST-1-1", out var b1) && b1 == 1
        && Dpt.TryBitSize("9.001", out var b9) && b9 == 16
        && Dpt.TryBitSize("DPT-1-1", out var bd) && bd == 1
        && Dpt.TryBitSize("DPST-5-1", out var b5) && b5 == 8
        && !Dpt.TryBitSize("not-a-dpt", out _) && !Dpt.TryBitSize(null, out _));
}

// ---- 6. Discovery-Parser (SEARCH_RESPONSE) ----
{
    // Independent XKNX search_response_test.py vector; not generated by our encoder.
    var external = Convert.FromHexString(
        "0610020200500801C0A82A0A0E573601020011000000112233445566E000170C" +
        "01020304050647697261204B4E582F49502D526F75746572000000000000000000000000" +
        "0C0202010302040105010701");
    Check("external SEARCH_RESPONSE fixture", Discovery.TryParseDevice(external,
        new IPEndPoint(IPAddress.Loopback, 9999), out var externalDevice)
        && externalDevice.Name == "Gira KNX/IP-Router"
        && externalDevice.ControlEndpoint.ToString() == "192.168.42.10:3671"
        && externalDevice.KnxAddress == "1.1.0"
        && externalDevice.SupportedFamilies.SequenceEqual(new byte[] { 2, 3, 4, 5, 7 }));
    var alternate = (byte[])external.Clone();
    alternate[12] = 0x12; alternate[13] = 0x34;
    alternate[38] = 0xFC; // Latin-1 device name; independent of the request sender.
    Check("discovery advertised port and Latin-1", Discovery.TryParseDevice(alternate,
        new IPEndPoint(IPAddress.Loopback, 9999), out var alternateDevice)
        && alternateDevice.ControlEndpoint.Port == 0x1234 && alternateDevice.Name.StartsWith("üira"));
    var malformed = (byte[])external.Clone();
    malformed[14] = 0;
    Check("discovery rejects zero-length DIB", !Discovery.TryParseDevice(malformed,
        new IPEndPoint(IPAddress.Loopback, 9999), out _));

    // Search-response fixture with device identifiers omitted.
    var realDescription = Convert.FromHexString(
        "0610020400483601020010000000000000000001E000170C020000000001" +
        "53434E2D49503130302E303320495020526F75746572206D697420536563" +
        "0C0202020302040205020702");
    Check("real MDT DESCRIPTION_RESPONSE", Discovery.TryParseDevice(realDescription,
        new IPEndPoint(IPAddress.Parse("192.0.2.10"), 3671), out var mdt)
        && mdt.Name == "SCN-IP100.03 IP Router mit Sec" && mdt.KnxAddress == "1.0.0"
        && mdt.MacAddress == "02:00:00:00:00:01" && mdt.Serial.SequenceEqual(
            new byte[] { 0, 0, 0, 0, 0, 1 }));

    // DIB: length(1), type(1), medium/status(2), IA(2), project ID(2),
    // serial(6), multicast(4), MAC(6), name(30). Total: 54 bytes.
    var name = Encoding.ASCII.GetBytes("MDT GlsX0201 IP-Router".PadRight(30));
    var dib = new byte[54];
    dib[0] = 54;
    dib[1] = 0x01;
    dib[2] = 0x02; // medium TP1
    BinaryPrimitives.WriteUInt16BigEndian(dib.AsSpan(4), 0x1F10); // ia 1.15.16
    new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC }.CopyTo(dib, 8);
    new byte[] { 224, 0, 23, 12 }.CopyTo(dib, 14);
    new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(dib, 18);
    name.CopyTo(dib, 24);
    var body = new byte[Hpai.Length + dib.Length + 4];
    Hpai.Control(new IPEndPoint(IPAddress.Parse("192.168.1.99"), 3671)).CopyTo(body, 0);
    dib.CopyTo(body, Hpai.Length);
    new byte[] { 4, 2, 4, 1 }.CopyTo(body, Hpai.Length + dib.Length);
    var frame = KnxNetIpFrame.Encode(ServiceType.SearchResponse, body);
    Check("discovery parse", Discovery.TryParseDevice(frame, new IPEndPoint(IPAddress.Parse("192.168.1.99"), 3671), out var d)
        && d is { Name: "MDT GlsX0201 IP-Router", KnxAddress: "1.15.16" }, $"name={d?.Name}, ia={d?.KnxAddress}");
    Check("discovery multicast/MAC/families", d.MulticastAddress == "224.0.23.12"
        && d.MacAddress == "01:02:03:04:05:06" && d.SupportedFamilies.SequenceEqual(new byte[] { 4 }));
    Check("discovery rejects truncated DIB", !Discovery.TryParseDevice(
        KnxNetIpFrame.Encode(ServiceType.SearchResponse, body[..^5]),
        new IPEndPoint(IPAddress.Loopback, 3671), out _));
}

// ---- 7. Handshake, ACK- und Sequenz-Zustandsmaschine gegen strengen Loopback-Fake ----
var (ok, detail) = await RunLoopbackAsync();
Check("tunnel connect + ack + download + disconnect", ok, detail);
var gatedBus = new KnxBus();
try
{
    await gatedBus.DownloadAsync(new KnxDevice
    {
        ControlEndpoint = new IPEndPoint(IPAddress.Loopback, 3671), Name = "gated", Serial = [1, 2, 3, 4, 5, 6],
        MulticastAddress = "224.0.23.12", MacAddress = "00:00:00:00:00:00", FriendlyName = "gated",
        KnxAddress = "1.1.16", SupportedFamilies = [1]
    }, new IndividualAddress(1, 1, 16), new TestProgram("gated", new byte[] { 1 }, 0x60, 1), new Progress<DownloadProgress>());
    Check("real download is safety-gated", false);
}
catch (NotSupportedException ex)
{
    Check("real download is safety-gated", true);
    Check("real download names the preflight gate", ex.Message.Contains("DownloadPreflight"), ex.Message);
}
try
{
    await gatedBus.GroupValueWriteAsync(new GroupAddress(0x0001), [0x01], 1);
    Check("group writes are read-only policy-gated", false);
}
catch (NotSupportedException) { Check("group writes are read-only policy-gated", true); }
Check("write gate is disabled by default", !KnxBus.WritesEnabled);
await gatedBus.DisposeAsync();
var (rejectOk, rejectDetail) = await MalformedConnectRejectedAsync();
Check("fake rejects malformed CONNECT_REQUEST", rejectOk, rejectDetail);
var (retryOk, retryDetail) = await AckRetryAsync();
Check("missing ACK repeats the same sequence once", retryOk, retryDetail);
var (statusOk, statusDetail) = await AckStatusRejectedAsync();
Check("failing ACK status aborts the send", statusOk, statusDetail);
var (seqOk, seqDetail) = await IncomingSequenceAsync();
Check("incoming sequence: ack, repeat, drop", seqOk, seqDetail);
var (heartbeatOk, heartbeatDetail) = await HeartbeatAsync();
Check("heartbeat CONNECTIONSTATE_REQUEST", heartbeatOk, heartbeatDetail);
var (serverCloseOk, serverCloseDetail) = await ServerDisconnectAsync();
Check("server DISCONNECT_REQUEST answered without our request", serverCloseOk, serverCloseDetail);
var (confirmationOk, confirmationDetail) = await BusWireChecks.ConfirmationFilterAsync();
Check("L_Data.con stays out of the telegram feed", confirmationOk, confirmationDetail);
var (readAnswerOk, readAnswerDetail) = await BusWireChecks.ReadGroupValueAsync(echoOnly: false);
Check("read waits for GroupValueResponse from the device", readAnswerOk, readAnswerDetail);
var (readTimeoutOk, readTimeoutDetail) = await BusWireChecks.ReadGroupValueAsync(echoOnly: true);
Check("confirmation echo alone does not satisfy a read", readTimeoutOk, readTimeoutDetail);
var (routingOk, routingDetail) = await BusWireChecks.RoutingJoinAsync();
Check("routing joins, decodes and leaves the multicast group", routingOk, routingDetail);

// ---- 7b. Download-Preflight: Dry-Run-Plan und Verweigerung ohne ein einziges Frame ----
{
    var target = new IndividualAddress(1, 1, 16);
    var program = new TestProgram("Test-App", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22 }, 0x0060, 2);
    var knownMask = DeviceDescriptor.Parse(0x00, [0x07, 0xB0]);

    DownloadPreflightRequest Ready() => new()
    {
        DeviceAddress = target,
        Program = program,
        Descriptor = knownMask,
        ProgrammingMode = true,
        AddressConfirmed = true,
        DryRunConfirmed = true,
        ExpectedArea = 1,
        ExpectedLine = 1,
    };

    var plan = DownloadPreflight.Plan(Ready());
    Check("preflight: ready dry-run plan", plan.Ready, string.Join("; ", plan.Blockers.Select(b => b.Message)));
    Check("preflight: chunks cover the image deterministically",
        plan.Chunks.Count == 4
        && plan.Chunks[0] is { Address: 0x0060, Length: 2 }
        && plan.Chunks[3] is { Address: 0x0066, Length: 2 },
        string.Join(", ", plan.Chunks.Select(c => $"0x{c.Address:X4}/{c.Length}")));
    Check("preflight: every chunk fits one memory-write frame",
        plan.Chunks.All(c => c.Length is >= 1 and <= 12));
    Check("preflight: image hash is the SHA-256 of the image",
        plan.ImageHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(program.Image)));
    Check("preflight: mask check names the management model",
        plan.MaskVersion == 0x07B0 && plan.Checks.Any(c => c.Code == DownloadCheckCode.MaskKnown));
    var dryRun = plan.Describe();
    Check("preflight: dry run names target, mask and addresses",
        dryRun.Contains("1.1.16") && dryRun.Contains("0x07B0") && dryRun.Contains("0x0060"),
        dryRun.Split('\n')[0]);

    // The planned chunks encode to exactly the management frames a download would send.
    var firstChunkFrame = Management.MemoryWrite(target, plan.Chunks[0].Address, program.Image[..2]);
    Check("preflight: first planned chunk encodes to the memory-write frame",
        firstChunkFrame is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x10, 0x05, 0x02, 0x80, 0x00, 0x60, 0xAA, 0xBB],
        Convert.ToHexString(firstChunkFrame));
    Check("preflight: planned chunk round-trips through the codec",
        Cemi.ParseLData(firstChunkFrame) is { Apci: Apci.MemoryWrite, Destination: var chunkDestination, Data: [0x00, 0x60, 0xAA, 0xBB] }
        && chunkDestination == target);
    var lastChunk = plan.Chunks[^1];
    var lastChunkFrame = Management.MemoryWrite(target, lastChunk.Address, program.Image[^lastChunk.Length..]);
    Check("preflight: last planned chunk address is start + offset",
        lastChunkFrame is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x10, 0x05, 0x02, 0x80, 0x00, 0x66, 0x11, 0x22],
        Convert.ToHexString(lastChunkFrame));

    var unknownMask = DownloadPreflight.Plan(Ready() with { Descriptor = DeviceDescriptor.Parse(0x00, [0x12, 0x34]) });
    Check("preflight: unsupported mask blocks",
        !unknownMask.Ready && unknownMask.Blockers.Any(b => b.Code == DownloadCheckCode.MaskUnknown),
        string.Join("; ", unknownMask.Blockers.Select(b => b.Message)));
    var noDescriptor = DownloadPreflight.Plan(Ready() with { Descriptor = null });
    Check("preflight: missing descriptor blocks",
        !noDescriptor.Ready && noDescriptor.Blockers.Any(b => b.Code == DownloadCheckCode.DescriptorMissing));
    var wrongLine = DownloadPreflight.Plan(Ready() with { ExpectedLine = 2 });
    Check("preflight: wrong line blocks",
        !wrongLine.Ready && wrongLine.Blockers.Any(b => b.Code == DownloadCheckCode.AddressLineMismatch));
    var unconfirmedAddress = DownloadPreflight.Plan(Ready() with { AddressConfirmed = false });
    Check("preflight: unconfirmed address blocks",
        !unconfirmedAddress.Ready && unconfirmedAddress.Blockers.Any(b => b.Code == DownloadCheckCode.AddressNotConfirmed));
    var noProgrammingMode = DownloadPreflight.Plan(Ready() with { ProgrammingMode = false });
    Check("preflight: missing programming mode blocks",
        !noProgrammingMode.Ready && noProgrammingMode.Blockers.Any(b => b.Code == DownloadCheckCode.ProgrammingModeMissing));
    var incomplete = DownloadPreflight.Plan(Ready() with
    {
        MissingParameters = ["Kanal A: Helligkeit"],
        MissingLinks = ["Objekt 1 -> 1/1/1"],
    });
    Check("preflight: incomplete parameters and links block",
        !incomplete.Ready
        && incomplete.Blockers.Any(b => b.Code == DownloadCheckCode.ParametersIncomplete)
        && incomplete.Blockers.Any(b => b.Code == DownloadCheckCode.LinksIncomplete));
    var noProgram = DownloadPreflight.Plan(Ready() with { Program = null });
    Check("preflight: missing program image blocks",
        !noProgram.Ready && noProgram.Blockers.Any(b => b.Code == DownloadCheckCode.ProgramMissing));
    var oversizedChunk = DownloadPreflight.Plan(Ready() with { Program = new TestProgram("bad", [1, 2], 0x0060, 13) });
    Check("preflight: chunk size above the memory-write limit blocks",
        !oversizedChunk.Ready && oversizedChunk.Blockers.Any(b => b.Code == DownloadCheckCode.ChunkSizeInvalid));
    var rangeOverflow = DownloadPreflight.Plan(Ready() with { Program = new TestProgram("bad", [1, 2], 0xFFFF, 2) });
    Check("preflight: image beyond the address space blocks",
        !rangeOverflow.Ready && rangeOverflow.Blockers.Any(b => b.Code == DownloadCheckCode.ImageRangeInvalid));
    var programMismatch = DownloadPreflight.Plan(Ready() with
    {
        ExpectedApplicationId = "M-1_A-1", ApplicationId = "M-1_A-2",
    });
    Check("preflight: program mismatch blocks",
        !programMismatch.Ready && programMismatch.Blockers.Any(b => b.Code == DownloadCheckCode.ProgramMismatch));
    var dryRunUnconfirmed = DownloadPreflight.Plan(Ready() with { DryRunConfirmed = false });
    Check("preflight: unconfirmed dry run blocks",
        !dryRunUnconfirmed.Ready && dryRunUnconfirmed.Blockers.Any(b => b.Code == DownloadCheckCode.DryRunNotConfirmed));
    var deviceZero = DownloadPreflight.Plan(Ready() with { DeviceAddress = new IndividualAddress(1, 1, 0) });
    Check("preflight: address x.y.0 blocks",
        !deviceZero.Ready && deviceZero.Blockers.Any(b => b.Code == DownloadCheckCode.AddressInvalid));

    // Refusals happen before the first frame: the tunnel is never connected, so any send would throw.
    var log = new DownloadAttemptLog();
    var refusalTunnel = new KnxTunnel(new LoopbackPair().Client, new IPEndPoint(IPAddress.Loopback, 3671),
        new IndividualAddress(15, 15, 250));
    var refusalSession = new DownloadSession(refusalTunnel, target, program, "test-fake", log);
    var refused = await refusalSession.RunAsync(noDescriptor);
    Check("preflight: session refuses a non-ready plan before sending",
        !refused.Success && refused.Message.Contains("refused by preflight"), refused.Message);
    Check("preflight: refusal writes one attempt-log entry",
        log.Count == 1 && !log.Entries[0].Success && log.Entries[0].FailureCause is not null
        && log.Entries[0].Device == "1.1.16" && log.Entries[0].Interface == "test-fake",
        log.Count.ToString());
    var otherPlan = DownloadPreflight.Plan(Ready() with { DeviceAddress = new IndividualAddress(1, 1, 17) });
    var wrongTarget = await refusalSession.RunAsync(otherPlan);
    Check("preflight: session refuses a plan for another device before sending",
        !wrongTarget.Success && wrongTarget.Message.Contains("refused") && log.Count == 2, wrongTarget.Message);
    var otherImage = DownloadPreflight.Plan(Ready() with { Program = new TestProgram("other", [0x01], 0x0060, 1) });
    var wrongImage = await refusalSession.RunAsync(otherImage);
    Check("preflight: session refuses a plan for another image before sending",
        !wrongImage.Success && wrongImage.Message.Contains("different image") && log.Count == 3, wrongImage.Message);
    await refusalTunnel.DisposeAsync();
}

// ---- 8. KNX Data Secure: Schlüsselableitung, Keyring, S-A_Data, Secure Wrapper ----
var dac = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
var dacOther = Convert.FromHexString("0F0E0D0C0B0A09080706050403020100");
var keys = SecureKeys.DeriveKeys("geheim", dac);
var keysAgain = SecureKeys.DeriveKeys("geheim", dac);
var keysOtherPw = SecureKeys.DeriveKeys("anders", dac);
var keysOtherDac = SecureKeys.DeriveKeys("geheim", dacOther);
{
    Check("PBKDF2 deterministisch",
        keys.UserKey.SequenceEqual(keysAgain.UserKey)
        && keys.DeviceAuthenticationKey.SequenceEqual(keysAgain.DeviceAuthenticationKey));
    Check("User- und Device-Key unterschiedlich", !keys.UserKey.SequenceEqual(keys.DeviceAuthenticationKey));
    Check("anderes Passwort -> anderer Key", !keys.UserKey.SequenceEqual(keysOtherPw.UserKey));
    Check("anderer DAC -> anderer Key", !keys.UserKey.SequenceEqual(keysOtherDac.UserKey));
    Check("Keylänge 16 Byte", keys.UserKey.Length == 16 && keys.DeviceAuthenticationKey.Length == 16);
    Check("FixedTimeEquals", SecureKeys.FixedTimeEquals(keys.UserKey, keysAgain.UserKey)
        && !SecureKeys.FixedTimeEquals(keys.UserKey, keysOtherPw.UserKey));
    Check("AES-128 Blockvektor", SecureKeys.AesEncryptBlock(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        Convert.FromHexString("00112233445566778899AABBCCDDEEFF")) is
        [0x69, 0xC4, 0xE0, 0xD8, 0x6A, 0x7B, 0x04, 0x30, 0xD8, 0xCD, 0xB7, 0x80, 0x70, 0xB4, 0xC5, 0x5A],
        "AES-128 ECB Vektor (FIPS-197)");
}

// ---- 9. Keyring: JSON-Round-Trip + Bereichssuche + XML ----
{
    var groupKey = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
    var deviceKey = Convert.FromHexString("FFEEDDCCBBAA99887766554433221100");
    var ring = new Keyring
    {
        ProjectName = "Testprojekt",
        BackboneKey = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"),
        MulticastAddress = "224.0.23.12",
    };
    ring.Interfaces.Add(new InterfaceKey
    {
        Type = InterfaceType.Tunneling,
        Host = "192.168.1.10",
        User = "1",
        Password = "geheim",
        Key = keys.UserKey,
        DeviceAuthenticationCode = dac,
    });
    ring.Keys.Add(new SecureKey
    {
        Id = "g1",
        Name = "Licht EG",
        Type = KeyType.Group,
        Key = groupKey,
        DeviceAuthenticationCode = dac,
        GroupAddressRange = new KeyAddressRange(GroupAddress.ThreeLevel(1, 2, 1).Raw, GroupAddress.ThreeLevel(1, 2, 15).Raw),
        SequenceNumber = 41,
        Latency = 1000,
    });
    ring.Keys.Add(new SecureKey
    {
        Id = "i1",
        Name = "Schaltaktor",
        Type = KeyType.Individual,
        Key = deviceKey,
        DeviceAuthenticationCode = dac,
        ToolKey = deviceKey,
        IndividualAddressRange = new KeyAddressRange(new IndividualAddress(1, 1, 16).Raw, new IndividualAddress(1, 1, 16).Raw),
    });

    var json = ring.ToJson();
    var back = Keyring.FromJson(json);
    Check("Keyring JSON: Anzahl", back.Keys.Count == 2, back.Keys.Count.ToString());
    Check("Keyring JSON: Group-Key hex", back.FindKey("g1")?.Key.SequenceEqual(groupKey) == true);
    Check("Keyring JSON: DAC + Backbone", back.FindKey("g1")?.DeviceAuthenticationCode?.SequenceEqual(dac) == true
        && back.BackboneKey?.SequenceEqual(ring.BackboneKey!) == true);
    Check("Keyring JSON: Sequenz + Latenz", back.FindKey("g1") is { SequenceNumber: 41, Latency: 1000 });
    Check("Keyring JSON: Interface", back.Interfaces is [{ Type: InterfaceType.Tunneling, Host: "192.168.1.10", Password: "geheim" }]);
    Check("Keyring JSON: Hex-Schreibweise", json.Contains(Convert.ToHexString(groupKey)), "Key nicht als Hex serialisiert");

    Check("Bereichssuche Group: Treffer", ring.FindGroupKey(GroupAddress.ThreeLevel(1, 2, 3))?.Id == "g1");
    Check("Bereichssuche Group: Rand", ring.FindGroupKey(GroupAddress.ThreeLevel(1, 2, 15))?.Id == "g1");
    Check("Bereichssuche Group: daneben", ring.FindGroupKey(GroupAddress.ThreeLevel(1, 2, 16)) is null);
    Check("Bereichssuche Individual", ring.FindIndividualKey(new IndividualAddress(1, 1, 16))?.Id == "i1"
        && ring.FindIndividualKey(new IndividualAddress(1, 1, 17)) is null);

    // Datei-Round-Trip
    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knx-keyring-{Guid.NewGuid():N}.json");
    ring.Save(path);
    var loaded = Keyring.Load(path);
    System.IO.File.Delete(path);
    Check("Keyring Save/Load", loaded.FindGroupKey(GroupAddress.ThreeLevel(1, 2, 2))?.Key.SequenceEqual(groupKey) == true);

    // Keyring-XML
    var xml = $"""
        <Keyring xmlns="http://knx.org/xml/keyring/1" ProjectId="P-0001">
          <Backbone MulticastAddress="224.0.23.12" Latency="1000" Key="{Convert.ToHexString(ring.BackboneKey!)}">
            <Interface Type="Tunneling" Host="192.168.1.10" User="1" Password="geheim" Key="{Convert.ToHexString(keys.UserKey)}" />
            <Devices>
              <Device IndividualAddress="1.1.16" ToolKey="{Convert.ToHexString(deviceKey)}" SequenceNumber="00000000001F" />
            </Devices>
          </Backbone>
          <Keys>
            <Group Address="1/2/1-1/2/15" Key="{Convert.ToHexString(groupKey)}" Name="Licht EG" SequenceNumber="000000000029" />
          </Keys>
        </Keyring>
        """;
    var fromXml = Keyring.FromXml(xml);
    Check("Keyring XML: Group-Key", fromXml.FindGroupKey(GroupAddress.ThreeLevel(1, 2, 9))?.Name == "Licht EG");
    Check("Keyring XML: Sequenz hex", fromXml.FindGroupKey(GroupAddress.ThreeLevel(1, 2, 9))?.SequenceNumber == 0x29);
    Check("Keyring XML: Device-Key", fromXml.FindIndividualKey(new IndividualAddress(1, 1, 16))?.ToolKey!.SequenceEqual(deviceKey) == true);
    Check("Keyring XML: Backbone + Interface", fromXml.BackboneKey!.SequenceEqual(ring.BackboneKey!)
        && fromXml.Interfaces is [{ Type: InterfaceType.Tunneling }]);
}

// ---- 10. S-A_Data: Round-Trip, Tamper, falscher Key, 2-Byte-Tag ----
{
    var first = new byte[] { 0x00, 0x80, 0x01 }; // GroupValueWrite, 1 Byte
    var telegram = new SecureTelegram { PlainData = first, SequenceNumber = 7 };
    var (header, saData) = SecureTelegram.Encrypt(0x0110, GroupAddress.ThreeLevel(1, 2, 3).Raw, telegram, keys.UserKey);
    Check("S-A_Data Länge", saData.Length == 8 + first.Length + 4, saData.Length.ToString());
    Check("S-A_Data Round-Trip", SecureTelegram.TryDecrypt(header, saData, keys.UserKey, out var decoded, out var err)
        && decoded!.PlainData.SequenceEqual(first) && decoded.SequenceNumber == 7, err);

    var tampered = (byte[])saData.Clone();
    tampered[8] ^= 0x01; // erstes Datumbyte
    Check("S-A_Data Tamper erkannt", !SecureTelegram.TryDecrypt(header, tampered, keys.UserKey, out _, out _));

    var tamperedTag = (byte[])saData.Clone();
    tamperedTag[^1] ^= 0x01;
    Check("S-A_Data Tag-Tamper erkannt", !SecureTelegram.TryDecrypt(header, tamperedTag, keys.UserKey, out _, out _));

    var tamperedHeader = SecureTelegram.Encrypt(0x0110, GroupAddress.ThreeLevel(1, 2, 4).Raw, telegram, keys.UserKey).Header;
    Check("S-A_Data Header-Tamper erkannt", !SecureTelegram.TryDecrypt(tamperedHeader, saData, keys.UserKey, out _, out _));

    Check("S-A_Data falscher Key abgelehnt", !SecureTelegram.TryDecrypt(header, saData, keysOtherPw.UserKey, out _, out var wrongErr)
        && wrongErr == "MAC mismatch", wrongErr);

    var telegram2 = new SecureTelegram { PlainData = [0x09, 0x1F], SequenceNumber = 8, TagLength = SecureTelegram.TagLength2 };
    var (header2, saData2) = SecureTelegram.Encrypt(0x0110, GroupAddress.ThreeLevel(1, 2, 3).Raw, telegram2, keys.UserKey);
    Check("S-A_Data 2-Byte-Tag Länge", saData2.Length == 8 + 2 + 2, saData2.Length.ToString());
    Check("S-A_Data 2-Byte-Tag Round-Trip", SecureTelegram.TryDecrypt(header2, saData2, keys.UserKey,
        SecureTelegram.TagLength2, out var decoded2, out var err2) && decoded2!.PlainData.SequenceEqual(new byte[] { 0x09, 0x1F }), err2);
}

// ---- 11. S-A_Data im Secure-Routing-Frame ----
{
    var telegram = new SecureTelegram { PlainData = [0x00, 0x80, 0x2A], SequenceNumber = 12 };
    var frame = SecureApdu.BuildLDataIndication(0x0110, GroupAddress.ThreeLevel(1, 2, 3).Raw, telegram, keys.UserKey);
    Check("Secure-Frame: cEMI + APCI", frame[0] == 0x29 && frame[8] == 0x03 && frame[9] == 0xF1,
        Convert.ToHexString(frame[..10]));
    Check("Secure-Frame Round-Trip", SecureApdu.TryDecryptLDataIndication(frame, keys.UserKey, out var decoded, out var err)
        && decoded!.PlainData.SequenceEqual(new byte[] { 0x00, 0x80, 0x2A })
        && decoded.SequenceNumber == 12, err);
    frame[12] ^= 0x10;
    Check("Secure-Frame Tamper erkannt", !SecureApdu.TryDecryptLDataIndication(frame, keys.UserKey, out _, out _));
    Check("Secure-Frame ohne Secure-APCI abgelehnt", !SecureApdu.TryDecryptLDataIndication(frame[..10], keys.UserKey, out _, out _));
}

// ---- 12. KNXnet/IP Secure Wrapper ----
{
    var payload = new byte[] { 0x29, 0x00, 0xBC, 0xE0, 0x01, 0x10, 0x0A, 0x03, 0x03, 0xF1, 1, 2, 3, 4, 5, 0xAB };
    var serial = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
    var packet = new SecurePacket
    {
        SessionId = 0x1234,
        SequenceNumber = 5,
        SerialNumber = serial,
        MessageTag = 0,
        Payload = payload,
    };
    var body = SecureWrapper.Wrap(packet, keys.DeviceAuthenticationKey);
    Check("Wrapper Länge", body.Length == 16 + 16 + 16, body.Length.ToString());
    Check("Wrapper Round-Trip", SecureWrapper.TryUnwrap(body, keys.DeviceAuthenticationKey, out var back, out var err)
        && back!.Payload.SequenceEqual(payload) && back.SessionId == 0x1234 && back.SequenceNumber == 5, err);
    Check("Wrapper Seriennummer", back?.SerialNumber.SequenceEqual(serial) == true);
    Check("Wrapper Seriennummer passt", back is not null && SecureWrapper.IsFrom(back, serial)
        && !SecureWrapper.IsFrom(back, new byte[] { 0, 1, 2, 3, 4, 6 }));
    Check("Wrapper falscher Key abgelehnt",
        !SecureWrapper.TryUnwrap(body, keysOtherPw.DeviceAuthenticationKey, out _, out _));

    var tampered = (byte[])body.Clone();
    tampered[18] ^= 0x40;
    Check("Wrapper Tamper erkannt", !SecureWrapper.TryUnwrap(tampered, keys.DeviceAuthenticationKey, out _, out _));
    Check("Wrapper Längenfehler erkannt",
        !SecureWrapper.TryUnwrap(body[..^1], keys.DeviceAuthenticationKey, out _, out _));

    // KNXnet/IP-Frame mit 0x00 am Ende: Länge kommt aus dem Frame-Header, nicht aus dem Padding
    var frameBytes = new byte[16];
    frameBytes[0] = 0x06; frameBytes[1] = 0x10; frameBytes[3] = 0x50; frameBytes[5] = 0x10; frameBytes[9] = 0x09;
    var zeroPacket = new SecurePacket
    {
        SessionId = 0x1234,
        SequenceNumber = 6,
        SerialNumber = serial,
        Payload = frameBytes,
    };
    var zeroBody = SecureWrapper.Wrap(zeroPacket, keys.DeviceAuthenticationKey);
    Check("Wrapper Frame mit 0x00-Ende", SecureWrapper.TryUnwrap(zeroBody, keys.DeviceAuthenticationKey, out var zeroBack, out _)
        && zeroBack!.Payload.SequenceEqual(frameBytes));
}

// ---- 13. Secure-Session-Bodies ----
{
    var endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 3671);
    var auth = SecureSession.Authenticate(endpoint, keys.UserKey);
    Check("SESSION_AUTHENTICATE Round-Trip", SecureSession.TryParseAuthenticate(auth, out var ep, out var userKey, out var err)
        && ep!.Port == 3671 && ep.Address.ToString() == "192.168.1.10" && userKey!.SequenceEqual(keys.UserKey), err);

    var response = SecureSession.Response(0x0A0B, [0, 1, 2, 3, 4, 5], keys.DeviceAuthenticationKey);
    Check("SESSION_RESPONSE Round-Trip", SecureSession.TryParseResponse(response, keys.DeviceAuthenticationKey,
        out var sessionId, out var serial, out var err2) && sessionId == 0x0A0B
        && serial!.SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5 }), err2);
    response[7] ^= 0x01;
    Check("SESSION_RESPONSE Tamper erkannt", !SecureSession.TryParseResponse(response,
        keys.DeviceAuthenticationKey, out _, out _, out _));
}

// ---- 14. Sequenznummern ----
{
    var key = new SecureKey { SequenceNumber = 3 };
    var first = key.NextSequenceNumber();
    var second = key.NextSequenceNumber();
    Check("Sequenz monoton", second == first + 1 && key.SequenceNumber == second);
    var max = new SecureKey { SequenceNumber = SecureKey.MaxSequenceNumber };
    Check("Sequenz Wrap bei 48 Bit", max.NextSequenceNumber() == 0 && max.SequenceNumber == 0);
    Check("Sequenz bleibt 48 Bit", new SecureKey { SequenceNumber = SecureKey.MaxSequenceNumber - 1 }
        .NextSequenceNumber() == SecureKey.MaxSequenceNumber);
}

// ---- 15. CSV-Export: RFC 4180 und Formel-Neutralisierung ----
{
    var csvProject = new Project { Id = "P-CSV", Name = "CSV" };
    var csvInst = new Installation { Id = "I-CSV" };
    var csvArea = new Area { Id = "A-CSV", Address = 1, Name = "=1+1" };
    var csvLine = new Line { Id = "L-CSV", Address = 1, Name = "+SUM(1,1)" };
    csvLine.Devices.Add(new DeviceInstance
    {
        Id = "D-CSV",
        Address = new IndividualAddress(1, 1, 1),
        Name = "@cmd;x",
        ProductRefId = "M-1",
    });
    csvArea.Lines.Add(csvLine);
    csvInst.Areas.Add(csvArea);
    var csvRange = new GroupRange { Id = "GR-CSV", RangeStart = 1, RangeEnd = 15 };
    csvRange.Addresses.Add(new GroupAddressEntry
    {
        Id = "GA-CSV",
        Name = "=1+1;\"quote\"",
        Address = GroupAddress.ThreeLevel(1, 1, 1),
        DatapointType = "+SUM(1,1)",
    });
    csvInst.MainRanges.Add(csvRange);
    csvProject.Installations.Add(csvInst);

    var recorder = new TelegramRecorder();
    recorder.Start();
    recorder.Add(new BusTelegram
    {
        Timestamp = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Source = "1.1.1",
        Telegram = new Telegram
        {
            Source = new IndividualAddress(1, 1, 1),
            Destination = new IndividualAddress(GroupAddress.ThreeLevel(1, 1, 1).Raw),
            IsGroupAddress = true,
            Apci = Apci.GroupValueWrite,
            Data = [1],
        },
    });

    static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { cells.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        cells.Add(field.ToString());
        return cells;
    }

    var etsCsv = recorder.ToEtsCsv(csvProject);
    Check("CSV: ETS-Kopf unverändert", etsCsv.StartsWith(
        "Time;Service;Flags;Prio;Source Address;Source Name;Destination Address;" +
        "Destination Name;Hop Count;Type;DPT;Iack;Info"));
    var etsRow = etsCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(Cells).Single();
    Check("CSV: ETS-Zeile behält 13 Spalten", etsRow.Count == 13, etsRow.Count.ToString());
    Check("CSV: Quellname neutralisiert und zitiert", etsRow[5] == "'@cmd;x", etsRow[5]);
    Check("CSV: Zielname neutralisiert und zitiert", etsRow[7] == "'=1+1;\"quote\"", etsRow[7]);
    Check("CSV: DPT neutralisiert", etsRow[10] == "'+SUM(1,1)", etsRow[10]);

    var csvPath = Path.Combine(Path.GetTempPath(), "knx-bus-csv-" + Guid.NewGuid().ToString("N") + ".csv");
    try
    {
        recorder.SaveCsv(csvPath);
        var bytes = File.ReadAllBytes(csvPath);
        Check("CSV: Aufzeichnung ist UTF-8 mit BOM", bytes is [0xEF, 0xBB, 0xBF, ..]);
        var lines = File.ReadAllLines(csvPath);
        Check("CSV: Aufzeichnungs-Kopf unverändert", lines[0] == "Zeitstempel;Quelle;Ziel;Gruppe;APCI;Daten");
        Check("CSV: Aufzeichnung behält 6 Spalten", lines[1].Split(';').Length == 6);
        var reread = TelegramRecorder.LoadCsv(csvPath);
        Check("CSV: Aufzeichnung lädt unverändert zurück",
            reread.Count == 1 && reread[0].Data == "01" && reread[0].Destination == "1/1/1");
    }
    finally
    {
        File.Delete(csvPath);
    }
}

TelegramMetadataChecks.Run((name, ok) => Check(name, ok));
TelegramHistoryChecks.Run((name, ok) => Check(name, ok));
await ManagementChecks.RunAsync(Check);
await ManagementWriteChecks.RunAsync(Check);
await ReadOnlyLineScanChecks.RunAsync(Check);
Check("tunnel lifetime checks", await TunnelLifetimeChecks.RunAsync() == 0);
Check("tunnel peer checks", await TunnelPeerChecks.RunAsync() == 0);
Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER");
Environment.Exit(failures.Count == 0 ? 0 : 1);

// ================= Loopback: strenger Fake + Protokoll-Zustandsmaschine =================

async Task<(bool Ok, string Detail)> RunLoopbackAsync()
{
    var (pair, fake, tunnel) = await StartAsync(channel: 7, log: s => Console.WriteLine("[tunnel] " + s));
    try
    {
        // 1. Handshake: der Fake akzeptiert nur HPAI + HPAI + CRI ohne Statusbyte.
        var connect = fake.Bodies(ServiceType.ConnectRequest).SingleOrDefault();
        var handshake = tunnel.State == BusConnectionState.Connected && tunnel.Channel == 7
            && tunnel.AssignedAddress == fake.AssignedAddress
            && fake.Violations.Count == 0
            && connect is { Length: 20 } && connect[16..].SequenceEqual(Cri.Tunnel());
        Check("loopback: CONNECT handshake accepted", handshake,
            $"state={tunnel.State} channel={tunnel.Channel} ia={tunnel.AssignedAddress} " +
            $"violations=[{string.Join("; ", fake.Violations)}] connect={Convert.ToHexString(connect ?? [])}");

        // 2. GroupValueWrite: erstes Telegramm trägt Sequenz 0.
        var received = new TaskCompletionSource<Telegram>();
        fake.TelegramReceived += t => received.TrySetResult(t);
        await tunnel.GroupValueWriteAsync(new Knx.GroupAddress(GroupAddress.ThreeLevel(1, 1, 2).Raw), [0x01]);
        var tg = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var requests = fake.Bodies(ServiceType.TunnellingRequest).Where(b => b.Length > 4).ToList();
        Check("loopback: group write with sequence 0", tg.Apci == Apci.GroupValueWrite && tg.Data is [0x01]
            && requests.Count == 1 && requests[0][2] == 0, Convert.ToHexString(requests.FirstOrDefault() ?? []));

        // 3. Download mit 4 Chunks (2 Bytes/Chunk), nur mit bereitstehendem Preflight-Plan.
        IApplicationProgram program = new TestProgram("Test-App", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22 }, 0x0060, 2);
        var plan = DownloadPreflight.Plan(new DownloadPreflightRequest
        {
            DeviceAddress = new IndividualAddress(1, 1, 16),
            Program = program,
            Descriptor = DeviceDescriptor.Parse(0x00, [0x07, 0xB0]),
            ProgrammingMode = true,
            AddressConfirmed = true,
            DryRunConfirmed = true,
            ExpectedArea = 1,
            ExpectedLine = 1,
        });
        var attemptLog = new DownloadAttemptLog();
        var blockedSession = new DownloadSession(tunnel, new IndividualAddress(1, 1, 16), program, "loopback-fake");
        var blocked = await blockedSession.RunAsync(plan);
        Check("preflight: session refuses real writes without explicit opt-in",
            !blocked.Success && blocked.Message.Contains("opt-in", StringComparison.Ordinal));
        var session = new DownloadSession(tunnel, new IndividualAddress(1, 1, 16), program, "loopback-fake",
            attemptLog, allowWrites: true);
        var stages = new List<DownloadStage>();
        session.Progress += p => Console.WriteLine($"[dl] {p.Stage} {p.Percent}% {p.Message}");
        var result = await session.RunAsync(plan);

        var written = fake.Memory.Count;
        var attemptLogged = attemptLog.Count == 1 && attemptLog.Entries[0].Success
            && attemptLog.Entries[0].Interface == "loopback-fake" && attemptLog.Entries[0].ImageHash is not null;
        var downloaded = result.Success && written == program.Image.Length && attemptLogged;

        // 4. DISCONNECT_REQUEST: channel + 0 + HPAI, Antwort wird abgewartet.
        await tunnel.CloseAsync();
        var disconnect = fake.Bodies(ServiceType.DisconnectRequest).SingleOrDefault();
        var closed = tunnel.State == BusConnectionState.Disconnected && disconnect is { Length: 10 }
            && disconnect[0] == 7 && disconnect[1] == 0x00 && disconnect[2] == 0x08;
        Check("loopback: DISCONNECT_REQUEST layout", closed,
            disconnect is null ? "kein DISCONNECT_REQUEST" : Convert.ToHexString(disconnect));

        var summary = downloaded
            ? $"{stages.Count} Fortschritts-Events, {written} Bytes verifiziert, {attemptLog.Count} Log-Eintrag"
            : result.Message + $" (geschrieben={written}, attemptLog={attemptLogged})";
        return (downloaded && handshake && closed && fake.Violations.Count == 0, summary);
    }
    finally
    {
        fake.Stop();
        await tunnel.DisposeAsync();
    }
}

/// <summary>Verbindet einen Tunnel gegen einen Fake, der jedes Frame-Layout prüft.</summary>
async Task<(LoopbackPair Pair, FakeKnxDevice Fake, KnxTunnel Tunnel)> StartAsync(byte channel = 7,
    int dropFirstAckFor = -1, byte ackStatus = 0x00, TimeSpan? heartbeat = null, Action<string>? log = null)
{
    var pair = new LoopbackPair();
    var endpoint = new IPEndPoint(IPAddress.Loopback, 3671);
    var fake = new FakeKnxDevice(pair.Server, endpoint, channel)
    {
        DropFirstAckForSequence = dropFirstAckFor,
        AckStatus = ackStatus,
    };
    fake.Start();
    var tunnel = new KnxTunnel(pair.Client, endpoint, new IndividualAddress(15, 15, 250),
        localEndpoint: new IPEndPoint(IPAddress.Loopback, 40001), heartbeatInterval: heartbeat);
    if (log is not null) tunnel.LogLine += log;
    await tunnel.ConnectAsync();
    return (pair, fake, tunnel);
}

/// <summary>
/// Der alte, fehlerhafte Aufbau (Statusbyte vorne) und ein 0.0.0.0-HPAI müssen vom
/// Gegenüber erkannt und mit einem Fehlerstatus abgelehnt werden.
/// </summary>
async Task<(bool Ok, string Detail)> MalformedConnectRejectedAsync()
{
    var pair = new LoopbackPair();
    var fake = new FakeKnxDevice(pair.Server, new IPEndPoint(IPAddress.Loopback, 3671), 3);
    fake.Start();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var client = pair.Client;
    var endpoint = new IPEndPoint(IPAddress.Loopback, 3671);
    var local = new IPEndPoint(IPAddress.Loopback, 40002);
    byte[][] malformed =
    [
        [0x00, .. Hpai.Control(local), .. Hpai.Control(local), .. Cri.Tunnel()],       // Statusbyte: 21 Oktetts
        [.. Hpai.Control(local), .. Hpai.Control(local), 0x04, 0x03, 0x02, 0x00],     // CRI = Device Management
    ];
    var replies = new List<byte[]>();
    foreach (var body in malformed)
    {
        await client.SendAsync(endpoint, KnxNetIpFrame.Encode(ServiceType.ConnectRequest, body), cts.Token);
        replies.Add(KnxNetIpFrame.Decode((await client.ReceiveAsync(cts.Token)).Buffer).Body);
    }
    fake.Stop();
    client.Dispose();

    var ok = fake.Violations.Count >= 2 && replies.All(r => r is [_, 0x22]);
    return (ok, $"violations=[{string.Join("; ", fake.Violations)}] replies={string.Join(",", replies.Select(Convert.ToHexString))}");
}

/// <summary>Fehlt ein ACK, muss dasselbe Frame mit derselben Sequenz einmal wiederholt werden.</summary>
async Task<(bool Ok, string Detail)> AckRetryAsync()
{
    var (_, fake, tunnel) = await StartAsync(dropFirstAckFor: 0);
    try
    {
        await tunnel.GroupValueWriteAsync(new Knx.GroupAddress(GroupAddress.ThreeLevel(1, 1, 2).Raw), [0x01]);
        var sequences = fake.Bodies(ServiceType.TunnellingRequest).Where(b => b.Length > 4)
            .Select(b => b[2]).ToArray();
        return (sequences is [0, 0] && fake.Violations.Count == 0, string.Join(",", sequences));
    }
    finally
    {
        fake.Stop();
        await tunnel.DisposeAsync();
    }
}

/// <summary>Ein ACK mit Fehlerstatus darf den Sendevorgang nicht als Erfolg verbuchen.</summary>
async Task<(bool Ok, string Detail)> AckStatusRejectedAsync()
{
    var (_, fake, tunnel) = await StartAsync(ackStatus: 0x21);
    try
    {
        await tunnel.GroupValueWriteAsync(new Knx.GroupAddress(GroupAddress.ThreeLevel(1, 1, 2).Raw), [0x01]);
        return (false, "ACK mit Status 0x21 wurde akzeptiert");
    }
    catch (IOException ex)
    {
        return (ex.Message.Contains("0x21"), ex.Message);
    }
    finally
    {
        fake.Stop();
        await tunnel.DisposeAsync();
    }
}

/// <summary>Eingehende Sequenz: erwartet -> ACK, wiederholt -> ACK ohne Verarbeitung, sonst verwerfen.</summary>
async Task<(bool Ok, string Detail)> IncomingSequenceAsync()
{
    var (_, fake, tunnel) = await StartAsync();
    try
    {
        var telegrams = 0;
        tunnel.TelegramReceived += _ => Interlocked.Increment(ref telegrams);
        var cemi = Indication(new Telegram
        {
            Source = new IndividualAddress(1, 1, 16),
            Destination = new IndividualAddress(GroupAddress.ThreeLevel(1, 1, 2).Raw),
            IsGroupAddress = true,
            Apci = Apci.GroupValueResponse,
            Data = [0x01],
        });

        await fake.PushAsync(5, cemi);   // out of order: kein ACK, keine Verarbeitung
        await Task.Delay(200);
        await fake.PushAsync(0, cemi);   // erwartet: ACK + Verarbeitung
        await WaitUntilAsync(() => fake.Bodies(ServiceType.TunnellingAck).Count >= 1);
        await fake.PushAsync(0, cemi);   // Wiederholung: ACK, aber keine zweite Verarbeitung
        await WaitUntilAsync(() => fake.Bodies(ServiceType.TunnellingAck).Count >= 2);
        await Task.Delay(100);

        var acks = fake.Bodies(ServiceType.TunnellingAck).Select(b => b[2]).ToArray();
        var ok = acks is [0, 0] && telegrams == 1 && fake.Violations.Count == 0;
        return (ok, $"acks=[{string.Join(",", acks)}] telegrams={telegrams}");
    }
    finally
    {
        fake.Stop();
        await tunnel.DisposeAsync();
    }
}

/// <summary>Heartbeat sendet CONNECTIONSTATE_REQUEST mit korrektem Aufbau und bleibt verbunden.</summary>
async Task<(bool Ok, string Detail)> HeartbeatAsync()
{
    var (_, fake, tunnel) = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(40));
    try
    {
        await WaitUntilAsync(() => fake.Bodies(ServiceType.ConnectionStateRequest).Count > 0);
        var body = fake.Bodies(ServiceType.ConnectionStateRequest).FirstOrDefault();
        var ok = body is { Length: 10 } && body[0] == 7 && body[1] == 0x00 && body[2] == 0x08
            && body[3] == 0x01
            && BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(8)) == 40001
            && tunnel.State == BusConnectionState.Connected;
        return (ok, body is null ? "kein Heartbeat" : Convert.ToHexString(body) + $" state={tunnel.State}");
    }
    finally
    {
        fake.Stop();
        await tunnel.DisposeAsync();
    }
}

/// <summary>Server-initiated DISCONNECT_REQUEST: Client antwortet nur, schickt kein eigenes Request.</summary>
async Task<(bool Ok, string Detail)> ServerDisconnectAsync()
{
    var (_, fake, tunnel) = await StartAsync();
    try
    {
        await fake.PushDisconnectAsync();
        await WaitUntilAsync(() => tunnel.State == BusConnectionState.Disconnected);
        var response = fake.Bodies(ServiceType.DisconnectResponse).SingleOrDefault();
        var requests = fake.Bodies(ServiceType.DisconnectRequest).Count;
        return (tunnel.State == BusConnectionState.Disconnected && response is [7, 0x00] && requests == 0,
            $"state={tunnel.State} response={Convert.ToHexString(response ?? [])} clientDisconnects={requests}");
    }
    finally
    {
        fake.Stop();
        await tunnel.DisposeAsync();
    }
}

static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition()) return true;
        await Task.Delay(20);
    }
    return condition();
}

/// <summary>A frame a device/gateway sends: same body as L_Data.req, message code L_Data.ind.</summary>
static byte[] Indication(Telegram telegram)
{
    var frame = Cemi.LDataRequest(telegram);
    frame[0] = (byte)CemiMessageCode.LDataInd;
    return frame;
}

/// <summary>
/// Read-only live check against a real KNXnet/IP interface: passive listen, one GroupValueRead,
/// DISCONNECT. Never writes group values, never downloads or reprograms anything.
/// </summary>
async Task<int> LiveProbeAsync(string host, int listenSeconds)
{
    if (!IPAddress.TryParse(host, out var address))
    {
        Console.WriteLine($"'{host}' ist keine IP-Adresse");
        return 2;
    }
    var endpoint = new IPEndPoint(address, 3671);
    using var transport = new UdpTransport(0);
    Console.WriteLine($"live: {endpoint}, local {transport.LocalEndPoint}");
    await using var tunnel = new KnxTunnel(transport, endpoint, new IndividualAddress(15, 15, 250));
    tunnel.LogLine += line => Console.WriteLine("[wire] " + line);

    var telegrams = 0;
    var confirmations = 0;
    tunnel.TelegramReceived += t =>
    {
        Interlocked.Increment(ref telegrams);
        Console.WriteLine($"[bus] {t.Telegram.Source} -> {t.Telegram.Destination} " +
                          $"group={t.Telegram.IsGroupAddress} hop={t.Telegram.HopCount} " +
                          $"apci={t.Telegram.Apci} data={Convert.ToHexString(t.Telegram.Data)}");
    };
    tunnel.ConfirmationReceived += t => Interlocked.Increment(ref confirmations);

    try
    {
        await tunnel.ConnectAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"CONNECT fehlgeschlagen: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    Console.WriteLine($"verbunden: Kanal {tunnel.Channel}, Adresse {tunnel.AssignedAddress}; " +
                      $"passives Zuhören {listenSeconds} s");

    await Task.Delay(TimeSpan.FromSeconds(listenSeconds));
    Console.WriteLine($"Zuhören beendet: {telegrams} Telegramm(e), {confirmations} L_Data.con");

    try
    {
        // 0/0/1 as a read-only probe address (a read never changes a group value).
        var addressGa = new Knx.GroupAddress(GroupAddress.ThreeLevel(0, 0, 1).Raw);
        var response = await tunnel.ReadGroupValueAsync(addressGa, TimeSpan.FromSeconds(3));
        Console.WriteLine(response is null
            ? "keine GroupValueResponse innerhalb 3 s"
            : $"GroupValueResponse von {response.Telegram.Source} nach dem Read: " +
              $"{Convert.ToHexString(response.Telegram.Data)} (hop {response.Telegram.HopCount})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"GroupValueRead fehlgeschlagen: {ex.GetType().Name}: {ex.Message}");
    }

    await tunnel.CloseAsync();
    Console.WriteLine($"Endzustand: {tunnel.State}");
    return tunnel.State == BusConnectionState.Disconnected ? 0 : 1;
}

/// <summary>
/// Passive routing listen: join 224.0.23.12 on the given local interface, decode incoming
/// frames, leave again. Sends nothing on the bus.
/// </summary>
async Task<int> RoutingLiveAsync(string localIp, int seconds)
{
    if (!IPAddress.TryParse(localIp, out var localAddress))
    {
        Console.WriteLine($"'{localIp}' ist keine lokale IP-Adresse");
        return 2;
    }
    using var transport = new UdpTransport(localAddress, Discovery.MulticastPort, reuseAddress: true);
    await using var routing = new KnxRouting(transport, localAddress);
    routing.LogLine += line => Console.WriteLine("[routing] " + line);
    var telegrams = 0;
    routing.TelegramReceived += t =>
    {
        Interlocked.Increment(ref telegrams);
        Console.WriteLine($"[bus] {t.Telegram.Source} -> {t.Telegram.Destination} " +
                          $"group={t.Telegram.IsGroupAddress} hop={t.Telegram.HopCount} " +
                          $"apci={t.Telegram.Apci} data={Convert.ToHexString(t.Telegram.Data)}");
    };

    try
    {
        await routing.OpenAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"routing open fehlgeschlagen: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    Console.WriteLine($"routing: {Discovery.MulticastIp}:{Discovery.MulticastPort} auf {localAddress}, " +
                      $"passives Zuhören {seconds} s");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"Zuhören beendet: {telegrams} Telegramm(e)");

    await routing.CloseAsync();
    Console.WriteLine($"Endzustand: {routing.State}");
    return routing.State == BusConnectionState.Disconnected ? 0 : 1;
}

/// <summary>Paired in-memory transport for tests: what one endpoint writes, the other reads.</summary>
internal sealed class LoopbackPair
{
    private readonly Channel<byte[]> _clientToServer = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _serverToClient = Channel.CreateUnbounded<byte[]>();

    public IUdpTransport Client => new Endpoint(_clientToServer, _serverToClient);
    public IUdpTransport Server => new Endpoint(_serverToClient, _clientToServer);

    private sealed class Endpoint(Channel<byte[]> write, Channel<byte[]> read) : IUdpTransport
    {
        private static readonly IPEndPoint Loopback = new(IPAddress.Loopback, 3671);

        public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        {
            write.Writer.TryWrite(data);
            return Task.CompletedTask;
        }

        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
            => read.Reader.ReadAsync(ct).AsTask().ContinueWith(t =>
                new UdpReceiveResult(t.Result, Loopback), ct);

        public void Dispose() { }
    }
}

/// <summary>
/// Minimal KNXnet/IP device emulator for tests. It validates every frame it receives
/// against the wire layouts (HPAI + HPAI + CRI without a status byte, tunnelling CRD
/// 04 04 IA, ACK body 04 channel sequence status) and records violations instead of
/// silently accepting a wrong layout. Malformed CONNECT_REQUESTs are answered with a
/// CONNECT_RESPONSE carrying status 0x22 (E_CONNECTION_TYPE) and no CRD.
/// </summary>
internal sealed class FakeKnxDevice
{
    private readonly IUdpTransport _server;
    private readonly IPEndPoint _endpoint;
    private readonly byte _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<(ServiceType Service, byte[] Body)> _frames = new();
    private readonly List<string> _violations = new();
    private Task? _loop;
    private bool _droppedAck;

    public FakeKnxDevice(IUdpTransport server, IPEndPoint endpoint, byte channel = 7)
    {
        _server = server;
        _endpoint = endpoint;
        _channel = channel;
    }

    public IndividualAddress AssignedAddress { get; init; } = new(1, 1, 16);
    public int DropFirstAckForSequence { get; init; } = -1;
    public byte AckStatus { get; init; }
    public Dictionary<int, byte> Memory { get; } = new();
    public event Action<Telegram>? TelegramReceived;

    public IReadOnlyList<string> Violations { get { lock (_gate) return _violations.ToList(); } }

    /// <summary>Decoded bodies of all received frames of one service, in arrival order.</summary>
    public IReadOnlyList<byte[]> Bodies(ServiceType service)
    {
        lock (_gate) return _frames.Where(f => f.Service == service).Select(f => f.Body).ToList();
    }

    public void Start() => _loop = Task.Run(LoopAsync);
    public void Stop() => _cts.Cancel();

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _server.ReceiveAsync(_cts.Token);
                KnxNetIpFrame frame;
                try
                {
                    frame = KnxNetIpFrame.Decode(result.Buffer);
                }
                catch (FormatException ex)
                {
                    Violation("undecodable datagram: " + ex.Message);
                    continue;
                }
                lock (_gate) _frames.Add((frame.Service, frame.Body));

                switch (frame.Service)
                {
                    case ServiceType.ConnectRequest:
                        if (ValidateConnect(frame.Body))
                        {
                            Violation("CONNECT_REQUEST rejected with status 0x22");
                            await SendAsync(result.RemoteEndPoint, ServiceType.ConnectResponse, [0x00, 0x22]);
                        }
                        else
                        {
                            byte[] body = [_channel, 0x00, .. Hpai.Control(_endpoint), .. Crd.Tunnel(AssignedAddress)];
                            await SendAsync(result.RemoteEndPoint, ServiceType.ConnectResponse, body);
                        }
                        break;
                    case ServiceType.ConnectionStateRequest:
                        ValidateManagement("CONNECTIONSTATE_REQUEST", frame.Body);
                        await SendAsync(result.RemoteEndPoint, ServiceType.ConnectionStateResponse,
                            Connection.Response(_channel));
                        break;
                    case ServiceType.DisconnectRequest:
                        ValidateManagement("DISCONNECT_REQUEST", frame.Body);
                        await SendAsync(result.RemoteEndPoint, ServiceType.DisconnectResponse,
                            Connection.Response(_channel));
                        break;
                    case ServiceType.TunnellingRequest:
                        await HandleTunnellingRequestAsync(result.RemoteEndPoint, frame.Body);
                        break;
                    case ServiceType.TunnellingAck:
                        if (frame.Body.Length < 4 || frame.Body[0] != 4 || frame.Body[1] != _channel)
                            Violation("malformed TUNNELLING_ACK: " + Convert.ToHexString(frame.Body));
                        break;
                    case ServiceType.DisconnectResponse:
                        break;
                    default:
                        Violation($"unexpected service {frame.Service}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Violation("fake loop: " + ex.Message);
        }
    }

    /// <summary>Returns true when the body is malformed (violation recorded).</summary>
    private bool ValidateConnect(ReadOnlySpan<byte> body)
    {
        if (body.Length != 20)
        {
            Violation($"CONNECT_REQUEST body {body.Length} != 20 octets (status byte?)");
            return true;
        }
        for (var offset = 0; offset < 16; offset += 8)
        {
            if (body[offset] != 0x08)
            {
                Violation($"HPAI at {offset}: structure length {body[offset]} != 8");
                return true;
            }
            if (body[offset + 1] != 0x01)
            {
                Violation($"HPAI at {offset}: protocol {body[offset + 1]} != 1 (UDP)");
                return true;
            }
            if (body[offset + 2] == 0 && body[offset + 3] == 0 && body[offset + 4] == 0
                && body[offset + 5] == 0 && body[offset + 6] == 0 && body[offset + 7] == 0)
            {
                Violation($"HPAI at {offset} is 0.0.0.0:0, no tunnel traffic can be returned");
                return true;
            }
        }
        if (!body[16..].SequenceEqual(Cri.Tunnel()))
        {
            Violation("CRI is not tunnelling link layer 04 04 02 00: " + Convert.ToHexString(body[16..]));
            return true;
        }
        return false;
    }

    private void ValidateManagement(string name, ReadOnlySpan<byte> body)
    {
        if (body.Length != 10) Violation($"{name} body {body.Length} != 10 octets");
        else if (body[0] != _channel) Violation($"{name} channel {body[0]} != {_channel}");
        else if (body[1] != 0x00) Violation($"{name} reserved octet {body[1]} != 0");
        else if (body[2] != 0x08 || body[3] != 0x01) Violation($"{name} HPAI header is wrong");
    }

    private async Task HandleTunnellingRequestAsync(IPEndPoint remote, byte[] body)
    {
        if (body.Length < 4 || body[0] != 4)
        {
            Violation("TUNNELLING_REQUEST connection header: " + Convert.ToHexString(body));
            return;
        }
        var channel = body[1];
        var sequence = body[2];
        if (channel != _channel)
        {
            Violation($"TUNNELLING_REQUEST channel {channel} != {_channel}");
            return;
        }
        if (sequence == DropFirstAckForSequence && !_droppedAck)
        {
            _droppedAck = true;
            return; // ACK absichtlich einmal verlieren
        }

        var cemi = body[4..].ToArray();
        await SendAsync(remote, ServiceType.TunnellingAck, Tunnelling.Ack(channel, sequence, AckStatus));
        if (cemi.Length < 2)
        {
            Violation("TUNNELLING_REQUEST without cEMI");
            return;
        }
        if (cemi[0] != (byte)CemiMessageCode.LDataReq) return;
        var telegram = Cemi.ParseLData(cemi);
        TelegramReceived?.Invoke(telegram);
        var response = HandleManagement(telegram);
        if (response is not null) await PushAsync(_endpoint, _serverSequence++, response);
    }

    /// <summary>Pushes a TUNNELLING_REQUEST with an explicit sequence counter to the client.</summary>
    public Task PushAsync(byte sequence, byte[] cemi)
        => SendAsync(_endpoint, ServiceType.TunnellingRequest, Tunnelling.Request(_channel, sequence, cemi));

    /// <summary>Server-initiated disconnect for the open channel.</summary>
    public Task PushDisconnectAsync()
        => SendAsync(_endpoint, ServiceType.DisconnectRequest, Connection.DisconnectRequest(_channel, _endpoint));

    private Task PushAsync(IPEndPoint destination, byte sequence, byte[] cemi)
        => SendAsync(destination, ServiceType.TunnellingRequest, Tunnelling.Request(_channel, sequence, cemi));

    private byte _serverSequence;

    private Task SendAsync(IPEndPoint destination, ServiceType service, byte[] body)
        => _server.SendAsync(destination, KnxNetIpFrame.Encode(service, body), _cts.Token);

    private void Violation(string message)
    {
        lock (_gate) _violations.Add(message);
        Console.WriteLine("[fake] " + message);
    }

    private byte[]? HandleManagement(Telegram t)
    {
        Telegram Response(Telegram req, Apci apci, byte[] data) => new()
        {
            Source = req.Destination,
            Destination = req.Source,
            IsGroupAddress = false,
            Apci = apci,
            Data = data,
        };

        static byte[] Indication(Telegram telegram)
        {
            var frame = Cemi.LDataRequest(telegram);
            frame[0] = (byte)CemiMessageCode.LDataInd; // what a gateway forwards to the client
            return frame;
        }

        switch (t.Apci)
        {
            case Apci.DeviceDescriptorRead:
                return Indication(Response(t, Apci.DeviceDescriptorResponse, [0x00, 0x07, 0xB0]));
            case Apci.MemoryResponse:
            case Apci.MemoryRead:
            {
                var addr = (ushort)((t.Data[0] << 8) | t.Data[1]);
                var count = t.Data[2];
                var data = new byte[count + 2];
                data[0] = (byte)(addr >> 8); data[1] = (byte)addr;
                for (int i = 0; i < count; i++) data[2 + i] = Memory.TryGetValue(addr + i, out var b) ? b : (byte)0;
                return Indication(Response(t, Apci.MemoryResponse, data));
            }
            case Apci.MemoryWrite:
            {
                var addr = (ushort)((t.Data[0] << 8) | t.Data[1]);
                var payload = t.Data.Skip(2).ToArray();
                for (int i = 0; i < payload.Length; i++) Memory[addr + i] = payload[i];
                return Indication(Response(t, Apci.MemoryResponse, [0x00, 0x00]));
            }
            case Apci.Restart:
                return Indication(Response(t, Apci.MemoryResponse, [0x00, 0x00]));
        }
        return null;
    }
}

internal sealed class TestProgram : IApplicationProgram
{
    public TestProgram(string name, byte[] image, ushort start, int chunk)
    {
        Name = name; Image = image; StartAddress = start; ChunkSize = chunk;
    }
    public string Name { get; }
    public byte[] Image { get; }
    public ushort StartAddress { get; }
    public int ChunkSize { get; }
}

