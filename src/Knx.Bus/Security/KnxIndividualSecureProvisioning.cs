namespace Knx.Bus.Security;

/// <summary>
/// Explicit counter-domain binding. Ordinary P2P must share the installation's ordinary group counter identity/store.
/// Tool access uses a separate stable domain; project copies and reconnects must retain the same binding and store.
/// </summary>
public sealed class KnxIndividualSecureCounterBinding
{
    public KnxIndividualSecureKeyKind KeyKind { get; }
    public DataSecureStateIdentity Identity { get; }
    private KnxIndividualSecureCounterBinding(KnxIndividualSecureKeyKind kind, DataSecureStateIdentity identity) { KeyKind = kind; Identity = identity; }
    public static KnxIndividualSecureCounterBinding PointToPoint(DataSecureStateIdentity sharedOrdinaryIdentity)
        => new(KnxIndividualSecureKeyKind.PointToPoint, sharedOrdinaryIdentity ?? throw new ArgumentNullException(nameof(sharedOrdinaryIdentity)));
    public static KnxIndividualSecureCounterBinding ToolAccess(string physicalInstallationScope, string toolKeyEpoch, IndividualAddress localAddress)
    {
        if (string.IsNullOrWhiteSpace(toolKeyEpoch)) throw new ArgumentException("A stable explicit tool-key epoch is required.", nameof(toolKeyEpoch));
        return new(KnxIndividualSecureKeyKind.ToolAccess, new(physicalInstallationScope, "ToolAccess:" + toolKeyEpoch, localAddress));
    }
}
