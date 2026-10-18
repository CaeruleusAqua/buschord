using System.Net;
using Knx.Bus;

namespace Knx.Mcp;

/// <summary>
/// `--bus-probe=&lt;ip&gt;`: sends a KNXnet/IP SEARCH_REQUEST to one address from every local
/// interface and prints every datagram that comes back. Diagnostic for "ETS sees the gateway,
/// BusChord does not" situations (firewall, VLAN, discovery disabled).
/// </summary>
internal static class BusProbe
{
    public static async Task<int> RunAsync(string host, int timeoutMs = 4000)
    {
        if (!IPAddress.TryParse(host, out var target))
        {
            Console.Error.WriteLine($"probe: '{host}' is not an IP address");
            return 2;
        }

        var addresses = Discovery.LocalAddresses();
        Console.WriteLine($"Ziel {target}:3671, {addresses.Count} lokale Adresse(n):");
        foreach (var address in addresses) Console.WriteLine("  " + address);

        var received = 0;
        var tasks = addresses.Select(async local =>
        {
            using var transport = new UdpTransport(local, 0);
            var request = KnxNetIpFrame.Encode(ServiceType.SearchRequest,
                Hpai.Control(new IPEndPoint(local, transport.LocalEndPoint.Port)));
            await transport.SendAsync(new IPEndPoint(target, 3671), request);
            Console.WriteLine($"  -> gesendet von {local}:{transport.LocalEndPoint.Port} ({request.Length} B)");
            await transport.SendAsync(new IPEndPoint(target, 3671),
                KnxNetIpFrame.Encode(ServiceType.DescriptionRequest,
                    Hpai.Control(new IPEndPoint(local, transport.LocalEndPoint.Port))));

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var result = await transport.ReceiveAsync(cts.Token);
                    Interlocked.Increment(ref received);
                    var hex = Convert.ToHexString(result.Buffer.AsSpan(0, Math.Min(result.Buffer.Length, 24)));
                    var text = Discovery.TryParseDevice(result.Buffer, result.RemoteEndPoint, out var device)
                        ? $"DIB: {device.Name} / {device.KnxAddress} / {device.MacAddress}"
                        : "kein SEARCH_RESPONSE";
                    Console.WriteLine($"  <- {result.RemoteEndPoint} {result.Buffer.Length} B {hex} … {text}");
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.WhenAll(tasks);
        Console.WriteLine($"\n{received} Datagramm(e) empfangen in {timeoutMs} ms");
        if (received == 0)
            Console.WriteLine("Diagnose: keine Antwort. Prüfe Windows-Firewall (eingehend UDP für diese Exe), " +
                              "VLAN/Subnetz und ob die Discovery im IP-Interface aktiviert ist.");
        return received > 0 ? 0 : 1;
    }
}
