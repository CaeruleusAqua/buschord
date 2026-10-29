using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.Mcp;

/// <summary>Compatible MCP facade for the shared bounded group registry. The caller drains its server before this owner.</summary>
public sealed class GroupSecurityHostProfiles : IKnxGroupSecurityProfileResolver, IAsyncDisposable
{
    private readonly KnxGroupHostProfiles _inner;
    private GroupSecurityHostProfiles(KnxGroupHostProfiles inner) => _inner = inner;
    public static GroupSecurityHostProfiles Load(string path, IReadOnlyCollection<string> confirmedCurrentUnanchoredIds,
        IHostIntegrityCapabilitySource? integrity = null, IReadOnlyDictionary<string, IDataSecureMonotonicAnchor>? independentAnchors = null)
        => new(KnxGroupHostProfiles.Load(path, confirmedCurrentUnanchoredIds,
            integrity is null ? null : new IntegrityAdapter(integrity), independentAnchors));
    public ValueTask<KnxGroupSecurityProfile> ResolveAsync(string referenceId, KnxConnectionProfile connection, CancellationToken ct)
        => _inner.ResolveAsync(referenceId, connection, ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
    public override string ToString() => "[explicit group security host profiles]";
    private sealed class IntegrityAdapter(IHostIntegrityCapabilitySource source) : IGroupIntegrityCapabilitySource
    { public ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct) => source.ReadOwnedAsync(environmentName, ct); }
}
