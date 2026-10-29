using System.Text;
using Knx.Mcp;
using Knx.Bus.Connections;

if (args.Contains("--list-tools"))
{
    await using var server = new McpServer();
    foreach (var tool in server.Tools) Console.WriteLine(tool.Name);
    return 0;
}

if (args.Contains("--selftest")) return await Selftest.RunAsync();

foreach (var argument in args)
    if (argument.StartsWith("--scan-catalogs=", StringComparison.OrdinalIgnoreCase))
        return CatalogScan.Run(argument["--scan-catalogs=".Length..]);

foreach (var argument in args)
    if (argument.StartsWith("--import-catalogs=", StringComparison.OrdinalIgnoreCase))
        return CatalogImport.Run(argument["--import-catalogs=".Length..]);

foreach (var argument in args)
    if (argument.StartsWith("--management-probe=", StringComparison.OrdinalIgnoreCase))
        return await ManagementProbe.RunAsync(argument["--management-probe=".Length..], args);

foreach (var argument in args)
    if (argument.StartsWith("--bus-probe=", StringComparison.OrdinalIgnoreCase))
        return await BusProbe.RunAsync(argument["--bus-probe=".Length..]);

return await RunStdioAsync(args);

static async Task<int> RunStdioAsync(string[] arguments)
{
    string? profilesPath = null; string[] confirmed = [];
    try
    {
        var paths = arguments.Where(value => value.StartsWith("--group-security-profiles=", StringComparison.Ordinal)).ToArray();
        var confirmations = arguments.Where(value => value.StartsWith("--confirm-current-group-state=", StringComparison.Ordinal)).ToArray();
        if (paths.Length > 1 || confirmations.Length > 1 || confirmations.Length > 0 && paths.Length == 0)
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        if (paths.Length == 1) profilesPath = paths[0]["--group-security-profiles=".Length..];
        if (confirmations.Length == 1) confirmed = confirmations[0]["--confirm-current-group-state=".Length..].Split(',');
    }
    catch (KnxConnectionException error) { Console.Error.WriteLine("knx-mcp: " + error.Failure); return 2; }
    GroupSecurityHostProfiles? profiles;
    try { profiles = profilesPath is null ? null : GroupSecurityHostProfiles.Load(profilesPath, confirmed); }
    catch (KnxConnectionException error) { Console.Error.WriteLine("knx-mcp: " + error.Failure); return 2; }
    await using var profilesOwner = profiles;
    // Declaration order is deliberate: all server connection jobs drain before the host integrity owner is released.
    await using var server = new McpServer(null, profiles);
    using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
    {
        AutoFlush = true,
        NewLine = "\n",
    };

    while (await input.ReadLineAsync() is { } line)
    {
        if (line.Length == 0) continue;
        string? response;
        try
        {
            response = await server.HandleAsync(line);
        }
        catch (Exception ex)
        {
            // stdout is reserved for protocol messages; a broken request must not kill the server.
            Console.Error.WriteLine("knx-mcp: unhandled request error: " + ex);
            continue;
        }
        if (response is not null) await output.WriteLineAsync(response);
    }
    return 0;
}
