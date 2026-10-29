using System.Text.Json;
using Knx.Bus;
using Knx.Bus.Security;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private ToolAccessConsoleServices? _toolAccessServices;
    internal void ConfigureToolAccessProviders(Func<string, CancellationToken, Task<byte[]>> integrity,
        ISecurePasswordSource password, IKnxToolAccessCredentialProvider credentials)
        => _toolAccessServices = ToolAccessConsoleServices.FromProviders(integrity, password, credentials);

    private async Task<object> BusToolIdentityRead(JsonElement args)
    {
        var allowed = new[] { "gateway", "target", "tool_access_config", "confirm_current_state" };
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))
            || args.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 4 || args.EnumerateObject().Count() != 4)
            throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
        string Text(string name) => args.GetProperty(name).ValueKind == JsonValueKind.String && args.GetProperty(name).GetString() is { Length: > 0 } value
            ? value : throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
        var target = ConnectionInput.Address(Text("target"));
        var profile = ToolAccessConsoleProfile.Read(Text("tool_access_config"), Text("gateway"), target, Text("confirm_current_state"), _toolAccessServices?.Password);
        profile.RequireWireBudget(M112ApplicationWireBudget.IdentityAndStatus());
        using var job = await EnterConnectionJobAsync();
        await using var operation = await profile.AcquireAsync(_connections, _toolAccessServices, job.Token).ConfigureAwait(false);
        await using var connection = await operation.OpenManagementAsync(target, readOnly: true, job.Token).ConfigureAwait(false);
        var identity = await connection.Client.ReadIdentityAsync(job.Token).ConfigureAwait(false);
        var status = identity.MaskVersion == 0x0701 ? Convert.ToHexString(await connection.Client.ReadMemoryAsync(0x60, 1, job.Token).ConfigureAwait(false)) : null;
        return new { readOnly = true, toolAccessSelected = true, freshSyncCompleted = true, identity, m112Status = status,
            counterStateTrust = "ProvisionerConfirmedCurrentUnanchored", counterRollbackProtection = "NotProvided" };
    }
}
