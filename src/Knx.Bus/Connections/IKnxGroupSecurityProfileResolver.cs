namespace Knx.Bus.Connections;

/// <summary>
/// Host-provisioned reference lookup. Implementations own their counter-integrity capabilities until all consumers drain;
/// they must return the exact explicit installation/interface/pin policy, never infer floors or keys from a tool request.
/// No filesystem/environment/default registry is installed by this interface.
/// </summary>
public interface IKnxGroupSecurityProfileResolver
{
    ValueTask<KnxGroupSecurityProfile> ResolveAsync(string referenceId, KnxConnectionProfile connection, CancellationToken ct);
}
