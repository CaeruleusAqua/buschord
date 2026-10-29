using System.Net;
using System.Text.Json;
using Knx.Bus;

namespace Knx.Mcp;

/// <summary>Explicit single-device commissioning diagnostics; normal MCP sessions remain read-only.</summary>
internal static class ManagementProbe
{
    public static async Task<int> RunAsync(string host, string[] args)
    {
        string? Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || Option("--target") is not { Length: > 0 } targetText)
        {
            Console.Error.WriteLine("management-probe requires a gateway IPv4 and --target=area.line.device.");
            return 2;
        }
        try
        {
            var target = IndividualAddress.Parse(targetText);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            async Task<IManagementConnection> Open(CancellationToken token)
                => await KnxManagementConnection.OpenAsync(new IPEndPoint(address, 3671), target,
                    new IndividualAddress(1, 1, 249), ct: token);
            if (Option("--mode-cycle-confirm") is { } confirmed)
            {
                if (Option("--expected-identity") is not { Length: > 0 } expectedPath
                    || Option("--journal-directory") is not { Length: > 0 } journalDirectory
                    || confirmed != targetText)
                    throw new ArgumentException("Mode cycle requires identical --target and --mode-cycle-confirm, --expected-identity and --journal-directory.");
                var expected = JsonSerializer.Deserialize<ManagementDeviceIdentity>(await File.ReadAllTextAsync(expectedPath, timeout.Token))
                    ?? throw new InvalidDataException("Expected identity is missing.");
                var result = await ProgrammingModeCycle.RunAsync(Open, expected, confirmed, journalDirectory, timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return result.ActivatedAndVerified && result.RestoredAndVerified && result.Error is null ? 0 : 1;
            }
            await using var connection = await Open(timeout.Token);
            var identity = await connection.Client.ReadIdentityAsync(timeout.Token);
            var status = identity.MaskVersion == 0x0701
                ? Convert.ToHexString(await connection.Client.ReadMemoryAsync(0x60, 1, timeout.Token)) : null;
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, identity, m112Status = status }));
            if (Option("--identity-output") is { Length: > 0 } output)
            {
                await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(file, identity, cancellationToken: timeout.Token);
                file.Flush(flushToDisk: true);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("management-probe: " + ex.Message);
            return 1;
        }
    }
}
