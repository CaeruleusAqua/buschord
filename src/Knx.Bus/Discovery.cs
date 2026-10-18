using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Knx.Bus;

/// <summary>A KNXnet/IP device found by SEARCH (DIB device info).</summary>
public sealed class KnxDevice
{
    public required IPEndPoint ControlEndpoint { get; init; }
    public required string Name { get; init; }
    public required byte[] Serial { get; init; }
    public required string MulticastAddress { get; init; }
    public required string MacAddress { get; init; }
    public required string FriendlyName { get; init; }
    public ushort KnxMedium { get; init; }
    public byte KnxInstallation { get; init; }
    public required string KnxAddress { get; init; }
    public required byte[] SupportedFamilies { get; init; }
}

/// <summary>Discovery via SEARCH_REQUEST over the KNXnet/IP multicast address.</summary>
public static class Discovery
{
    public const string MulticastIp = "224.0.23.12";
    public const int MulticastPort = 3671;

    public static async Task<List<KnxDevice>> SearchAsync(IUdpTransport transport, TimeSpan timeout)
    {
        var request = KnxNetIpFrame.Encode(ServiceType.SearchRequest, Hpai.Any());
        var endpoints = new IPEndPoint[2]
        {
            new(IPAddress.Parse(MulticastIp), MulticastPort),
            new(IPAddress.Parse("233.255.255.250"), MulticastPort), // some stacks listen here
        };
        foreach (var ep in endpoints) await transport.SendAsync(ep, request);
        return await CollectAsync(transport, timeout, new Dictionary<string, KnxDevice>());
    }

    /// <summary>
    /// Discovery that works on multi-homed machines: one socket per local IPv4 address, each
    /// sending a SEARCH_REQUEST with a real HPAI (many IP interfaces ignore 0.0.0.0:0, which is
    /// why a plain multicast search can miss a device that ETS sees). With <paramref name="host"/>
    /// the same request goes to that address only, for networks that drop broadcasts.
    /// </summary>
    public static async Task<List<KnxDevice>> DiscoverAsync(TimeSpan timeout, string? host = null,
        CancellationToken ct = default)
    {
        var devices = new Dictionary<string, KnxDevice>();

        if (!string.IsNullOrWhiteSpace(host))
        {
            if (!IPAddress.TryParse(host, out var target))
                throw new ArgumentException($"'{host}' is not an IP address", nameof(host));

            using var transport = new UdpTransport(0);
            var destination = new IPEndPoint(target, MulticastPort);
            var hpai = Hpai.Control(new IPEndPoint(UdpTransport.ResolveLocalAddress(destination),
                transport.LocalEndPoint.Port));
            await transport.SendAsync(destination, KnxNetIpFrame.Encode(ServiceType.SearchRequest, hpai), ct);
            // Some routers answer SEARCH only on multicast, but DESCRIPTION on unicast.
            await transport.SendAsync(destination, KnxNetIpFrame.Encode(ServiceType.DescriptionRequest, hpai), ct);
            return await CollectAsync(transport, timeout, devices, ct);
        }

        var searches = LocalAddresses().Select(async local =>
        {
            using var transport = new UdpTransport(local, 0);
            var request = KnxNetIpFrame.Encode(ServiceType.SearchRequest,
                Hpai.Control(new IPEndPoint(local, transport.LocalEndPoint.Port)));

            var targets = new[]
            {
                new IPEndPoint(IPAddress.Parse(MulticastIp), MulticastPort),
                new IPEndPoint(IPAddress.Broadcast, MulticastPort),
            };
            foreach (var target in targets)
            {
                try
                {
                    await transport.SendAsync(target, request, ct);
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // An interface without broadcast support must not abort the whole search.
                }
            }
            return await CollectAsync(transport, timeout, devices, ct);
        });

        await Task.WhenAll(searches);
        lock (devices) return devices.Values.ToList();
    }

    private static async Task<List<KnxDevice>> CollectAsync(IUdpTransport transport, TimeSpan timeout,
        Dictionary<string, KnxDevice> devices, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await transport.ReceiveAsync(cts.Token);
                if (!TryParseDevice(result.Buffer, result.RemoteEndPoint, out var device)) continue;
                lock (devices) devices[device.ControlEndpoint.ToString()] = device;
            }
        }
        catch (OperationCanceledException)
        {
        }
        lock (devices) return devices.Values.ToList();
    }

    /// <summary>All usable IPv4 addresses of this host (loopback and link-local excluded).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = info.Address;
                    if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address) || address.ToString().StartsWith("169.254.")) continue;
                    addresses.Add(address);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }
        if (addresses.Count == 0) addresses.Add(IPAddress.Any);
        return addresses;
    }

    public static bool TryParseDevice(byte[] buffer, IPEndPoint remote, out KnxDevice device)
    {
        device = new KnxDevice
        {
            ControlEndpoint = new IPEndPoint(remote.Address, 3671),
            Name = "", Serial = [], MulticastAddress = "", MacAddress = "", FriendlyName = "",
            KnxAddress = "", SupportedFamilies = [],
        };
        try
        {
            var frame = KnxNetIpFrame.Decode(buffer);
            if (frame.Service != ServiceType.SearchResponse && frame.Service != ServiceType.SearchResponseExt
                && frame.Service != ServiceType.DescriptionResponse) return false;
            ReadOnlySpan<byte> span = frame.Body;
            var isDescription = frame.Service == ServiceType.DescriptionResponse;
            var advertised = isDescription ? remote : Hpai.Read(span).Endpoint;
            var endpoint = advertised.Address.Equals(IPAddress.Any) || advertised.Port == 0
                ? remote : advertised;
            var families = new List<byte>();
            string name = "", mac = "", multicast = "";
            byte medium = 0, installation = 0;
            ushort ia = 0;
            byte[] serial = [];
            int pos = isDescription ? 0 : Hpai.Length;
            while (pos < span.Length)
            {
                if (span.Length - pos < 2) return false;
                var len = span[pos];
                if (len < 2 || len > span.Length - pos) return false;
                var type = span[pos + 1];
                var dib = span.Slice(pos, len);
                switch ((DibType)type)
                {
                    case DibType.DeviceInfo:
                        if (len != 54) return false;
                        medium = dib[2];
                        ia = BinaryPrimitives.ReadUInt16BigEndian(dib[4..]);
                        installation = (byte)(BinaryPrimitives.ReadUInt16BigEndian(dib[6..]) & 0x0f);
                        serial = dib.Slice(8, 6).ToArray();
                        multicast = new IPAddress(dib.Slice(14, 4)).ToString();
                        mac = string.Join(":", dib.Slice(18, 6).ToArray().Select(b => b.ToString("X2")));
                        name = Encoding.Latin1.GetString(dib.Slice(24, 30)).TrimEnd('\0', ' ');
                        break;
                    case DibType.SuppSvcFamilies:
                        if ((len - 2) % 2 != 0) return false;
                        for (int i = 2; i < dib.Length; i += 2) families.Add(dib[i]);
                        break;
                }
                pos += len;
            }
            if (serial.Length == 0) return false;
            device = new KnxDevice
            {
                ControlEndpoint = endpoint, Name = name, FriendlyName = name,
                Serial = serial, MacAddress = mac, MulticastAddress = multicast,
                KnxMedium = medium, KnxInstallation = installation,
                KnxAddress = new IndividualAddress(ia).ToString(),
                SupportedFamilies = families.ToArray(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum DibType : byte { DeviceInfo = 0x01, SuppSvcFamilies = 0x02, IpConfig = 0x03, IpCurConfig = 0x04, KnxAddress = 0x05 }
}
