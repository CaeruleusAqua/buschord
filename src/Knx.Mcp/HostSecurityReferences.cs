using Knx.Bus.Security;

namespace Knx.Mcp;

/// <summary>Owned integrity bytes from an explicit host reference. Kept ABI-compatible for existing MCP hosts.</summary>
public interface IHostIntegrityCapabilitySource
{ ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct); }

public sealed class EnvironmentHostIntegrityCapabilitySource : IHostIntegrityCapabilitySource
{
    public ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct)
        => new EnvironmentGroupIntegrityCapabilitySource().ReadOwnedAsync(environmentName, ct);
}

internal static class HostSecurityReferences
{
    internal static string PrivateFile(string path, bool existing) => SecureHostReferences.PrivateFile(path, existing);
    internal static byte[] ReadBounded(string path, int maximum) => SecureHostReferences.ReadBounded(path, maximum);
}
