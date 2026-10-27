using System.Buffers.Binary;
using System.Net;

namespace Knx.Bus;

/// <summary>KNXnet/IP service types (v1.0).</summary>
public enum ServiceType : ushort
{
    SearchRequest = 0x0201,
    SearchResponse = 0x0202,
    DescriptionRequest = 0x0203,
    DescriptionResponse = 0x0204,
    ConnectRequest = 0x0205,
    ConnectResponse = 0x0206,
    ConnectionStateRequest = 0x0207,
    ConnectionStateResponse = 0x0208,
    DisconnectRequest = 0x0209,
    DisconnectResponse = 0x020A,
    SearchRequestExt = 0x020B,
    SearchResponseExt = 0x020C,
    TunnellingRequest = 0x0420,
    TunnellingAck = 0x0421,
    RoutingIndication = 0x0530,
    RoutingLostMessage = 0x0531,
    RoutingBusy = 0x0532,
}

/// <summary>Decoded KNXnet/IP frame: 6-byte header + body.</summary>
public sealed class KnxNetIpFrame
{
    public required ServiceType Service { get; init; }
    public required byte[] Body { get; init; }

    public const int HeaderLength = 6;

    public static byte[] Encode(ServiceType service, ReadOnlySpan<byte> body)
    {
        var total = (ushort)(HeaderLength + body.Length);
        var frame = new byte[total];
        frame[0] = 0x06;
        frame[1] = 0x10;
        // Service type at octets 2-3, total length at 4-5 (KNXnet/IP Core 3/8/2 header).
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)service);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), total);
        body.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static KnxNetIpFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
            throw new FormatException("KNXnet/IP frame shorter than 6 bytes");
        if (data[0] != 0x06 || data[1] != 0x10)
            throw new FormatException("bad KNXnet/IP header magic");
        var total = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        if (total < HeaderLength || total > data.Length)
            throw new FormatException($"frame length mismatch: header {total}, actual {data.Length}");
        var svc = (ServiceType)BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        return new KnxNetIpFrame { Service = svc, Body = data[HeaderLength..total].ToArray() };
    }
}

/// <summary>HPAI (host protocol address information), 8 octets over UDP.</summary>
public readonly struct Hpai
{
    public Hpai(IPEndPoint endpoint) => Endpoint = endpoint;

    public IPEndPoint Endpoint { get; }
    public byte Protocol => Udp;

    public const byte Udp = 0x01;
    public const int Length = 8;

    public void Write(Span<byte> span)
    {
        if (span.Length < Length || Endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("UDP HPAI requires an IPv4 endpoint and eight output bytes");
        // HPAI: [structure length 1 = 0x08][host protocol 1][ip 4][port 2]
        span[0] = Length;
        span[1] = Udp;
        Endpoint.Address.TryWriteBytes(span[2..], out _);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)Endpoint.Port);
    }

    public static Hpai Read(ReadOnlySpan<byte> span)
    {
        if (span.Length < Length || span[0] != Length || span[1] != Udp)
            throw new FormatException("invalid UDP HPAI");
        var ip = new IPAddress(span[2..6]);
        var port = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        return new Hpai(new IPEndPoint(ip, port));
    }

    public static byte[] Control(IPEndPoint endpoint) => Encode(endpoint);
    public static byte[] Data(IPEndPoint endpoint) => Encode(endpoint);

    private static byte[] Encode(IPEndPoint endpoint)
    {
        var buffer = new byte[Length];
        new Hpai(endpoint).Write(buffer);
        return buffer;
    }

    /// <summary>Any-address HPAI for discovery requests (0.0.0.0:0).</summary>
    public static byte[] Any() => Control(new IPEndPoint(IPAddress.Any, 0));
}

/// <summary>
/// CRI (connection request information). Tunnelling: [len 4][type 4][KNX layer][reserved].
/// Layer 0x02 requests a data link layer (link layer) tunnel, which is what tunnelling
/// clients want; 0x04 would request the raw link layer.
/// </summary>
public static class Cri
{
    public const int TunnelLength = 4;
    public const byte TunnelConnection = 0x04;
    public const byte LinkLayer = 0x02;

    public static byte[] Tunnel() => [TunnelLength, TunnelConnection, LinkLayer, 0x00];
}

/// <summary>
/// CRD (connection response data block). Tunnelling: [len 4][type 4][IA high][IA low].
/// The CRD carries the individual address assigned to the tunnel; the channel id lives in
/// the CONNECT_RESPONSE body and never inside the CRD.
/// </summary>
public readonly struct Crd
{
    public const int TunnelLength = 4;

    public Crd(byte length, byte connectionType, IndividualAddress? assignedAddress)
    {
        Length = length;
        ConnectionType = connectionType;
        AssignedAddress = assignedAddress;
    }

    public byte Length { get; }
    public byte ConnectionType { get; }
    public IndividualAddress? AssignedAddress { get; }

    public bool IsTunnelling => ConnectionType == Cri.TunnelConnection;

    public static byte[] Tunnel(IndividualAddress assigned)
        => [TunnelLength, Cri.TunnelConnection, (byte)(assigned.Raw >> 8), (byte)assigned.Raw];

    public static Crd Parse(ReadOnlySpan<byte> span)
    {
        if (span.Length < 2) throw new FormatException("CRD shorter than 2 bytes");
        var length = span[0];
        if (length < 2) throw new FormatException($"CRD length {length} is invalid");
        if (length > span.Length) throw new FormatException($"CRD length {length} exceeds {span.Length} bytes");
        var type = span[1];
        if (type == Cri.TunnelConnection)
        {
            if (length != TunnelLength) throw new FormatException($"tunnelling CRD length {length} != 4");
            var ia = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
            return new Crd(length, type, new IndividualAddress(ia));
        }
        // Device management CRD is [len 2][type 3]; no per-connection data.
        if (length != 2) throw new FormatException($"CRD length {length} is invalid for type 0x{type:X2}");
        return new Crd(length, type, null);
    }
}

/// <summary>Decoded CONNECT_RESPONSE body: channel, status, server HPAI, CRD.</summary>
public sealed class ConnectResponse
{
    public const int MinLength = 2;

    public required byte Channel { get; init; }
    public required byte Status { get; init; }
    public IPEndPoint? DataEndpoint { get; init; }
    public Crd? ConnectionData { get; init; }

    public bool Succeeded => Status == 0x00;

    public static ConnectResponse Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < MinLength)
            throw new FormatException($"CONNECT_RESPONSE shorter than {MinLength} bytes");
        var channel = body[0];
        var status = body[1];
        // A rejected connect only carries channel and status; HPAI and CRD are absent.
        if (status != 0x00)
            return new ConnectResponse { Channel = channel, Status = status };

        if (body.Length < MinLength + Hpai.Length)
            throw new FormatException("CONNECT_RESPONSE lacks the server HPAI");
        var endpoint = Hpai.Read(body[MinLength..]).Endpoint;
        var crdOffset = MinLength + Hpai.Length;
        var crd = Crd.Parse(body[crdOffset..]);
        return new ConnectResponse { Channel = channel, Status = status, DataEndpoint = endpoint, ConnectionData = crd };
    }
}

/// <summary>
/// Bodies of the connection management services (CONNECT_REQUEST, DISCONNECT,
/// CONNECTIONSTATE). Every pattern below was cross-checked against XKNX
/// (knxip/connect_request.py, disconnect_request.py, connectionstate_request.py) and
/// Calimero (ConnectRequest.java, DisconnectRequest.java, ConnectionstateRequest.java).
/// </summary>
public static class Connection
{
    /// <summary>CONNECT_REQUEST: control HPAI + data HPAI + CRI. No leading status octet.</summary>
    public static byte[] ConnectRequest(IPEndPoint control, IPEndPoint data)
    {
        var cri = Cri.Tunnel();
        var body = new byte[Hpai.Length * 2 + cri.Length];
        Hpai.Control(control).CopyTo(body, 0);
        Hpai.Data(data).CopyTo(body, Hpai.Length);
        cri.CopyTo(body, Hpai.Length * 2);
        return body;
    }

    /// <summary>DISCONNECT_REQUEST: channel id + reserved 0x00 + control HPAI (10 octets).</summary>
    public static byte[] DisconnectRequest(byte channel, IPEndPoint control)
    {
        var body = new byte[2 + Hpai.Length];
        body[0] = channel;
        body[1] = 0x00; // reserved
        Hpai.Control(control).CopyTo(body, 2);
        return body;
    }

    /// <summary>CONNECTIONSTATE_REQUEST: channel id + reserved 0x00 + control HPAI (10 octets).</summary>
    public static byte[] ConnectionStateRequest(byte channel, IPEndPoint control)
        => DisconnectRequest(channel, control);

    /// <summary>DISCONNECT_RESPONSE / CONNECTIONSTATE_RESPONSE: channel id + status.</summary>
    public static byte[] Response(byte channel, byte status = 0x00) => [channel, status];
}

/// <summary>
/// Tunnelling connection header: [structure length 4][channel][sequence][reserved/status].
/// A request carries cEMI after it, an ack carries the status code in the fourth octet.
/// </summary>
public static class Tunnelling
{
    public const int HeaderLength = 4;

    public static byte[] Request(byte channel, byte sequence, ReadOnlySpan<byte> cemi)
    {
        var body = new byte[HeaderLength + cemi.Length];
        body[0] = HeaderLength;
        body[1] = channel;
        body[2] = sequence;
        body[3] = 0x00; // reserved
        cemi.CopyTo(body.AsSpan(HeaderLength));
        return body;
    }

    public static byte[] Ack(byte channel, byte sequence, byte status = 0x00)
        => [HeaderLength, channel, sequence, status];

    public static (byte Channel, byte Sequence, byte[] Cemi) ReadRequest(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderLength) throw new FormatException("TUNNELLING_REQUEST shorter than 4 bytes");
        if (body[0] != HeaderLength) throw new FormatException($"bad tunnelling connection header length {body[0]}");
        return (body[1], body[2], body[HeaderLength..].ToArray());
    }

    public static (byte Channel, byte Sequence, byte Status) ReadAck(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderLength) throw new FormatException("TUNNELLING_ACK shorter than 4 bytes");
        if (body[0] != HeaderLength) throw new FormatException($"bad tunnelling connection header length {body[0]}");
        return (body[1], body[2], body[3]);
    }
}
