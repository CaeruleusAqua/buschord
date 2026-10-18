using System.Text;
using Knx.Mcp;

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
    if (argument.StartsWith("--bus-probe=", StringComparison.OrdinalIgnoreCase))
        return await BusProbe.RunAsync(argument["--bus-probe=".Length..]);

return await RunStdioAsync();

static async Task<int> RunStdioAsync()
{
    await using var server = new McpServer();
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
