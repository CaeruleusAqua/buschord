using System.Net;
using System.Security.Cryptography;
using Knx.Bus.Security;

namespace Knx.Bus.Connections;

public sealed partial class KnxConnectionFactory
{
    public Task<KnxGatewayOperation> AcquireToolAccessOperationAsync(KnxConnectionProfile connection, KnxToolAccessProfile toolAccess, CancellationToken ct = default)
        => AcquireToolAccessOperationAsync(connection, toolAccess, new ProtectedToolAccessCredentialProvider(), ct);

    public async Task<KnxGatewayOperation> AcquireToolAccessOperationAsync(KnxConnectionProfile connection, KnxToolAccessProfile toolAccess,
        IKnxToolAccessCredentialProvider credentialProvider, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (connection is null || toolAccess is null || credentialProvider is null || connection.Kind != KnxTransportKind.IpSecureUdp
            || connection.Keyring?.ExpectedSourceFingerprint is null || toolAccess.Target == connection.RequestedAddress)
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var binding = toolAccess.Bind(connection.RequestedAddress);
        var lease = _leases.Acquire(connection.Gateway);
        DataSecureCounterStore? store = null; KnxToolAccessCredentialScope? credentials = null; byte[] serial = [];
        try
        {
            try { store = await toolAccess.StateSource.OpenExistingAsync(binding, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (KnxConnectionException) { throw; }
            catch (Exception error) { throw KnxToolAccessProfile.StateFailure(error); }
            await ToolAccessCapability.ValidateStoreAsync(store, binding, toolAccess.Target, ct).ConfigureAwait(false);
            try
            {
                credentials = await credentialProvider.AcquireAsync(connection.Keyring, connection.PasswordSource, toolAccess.Target,
                    toolAccess.OrdinaryPointToPointKeyFingerprints, ct).ConfigureAwait(false);
                if (credentials is null) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            }
            catch (OperationCanceledException) { throw; }
            catch (KnxConnectionException) { throw; }
            catch { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
            var gateway = credentials.Gateway;
            if (credentials.Target != toolAccess.Target || gateway.Host != connection.Keyring.HostAddress || gateway.UserId != connection.Keyring.UserId
                || gateway.TunnelAddress != connection.RequestedAddress || gateway.SourceFingerprint != connection.Keyring.ExpectedSourceFingerprint)
                throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
            try { serial = await Task.Run(() => { ct.ThrowIfCancellationRequested(); return _serial.ReadOrCreate(); }).ConfigureAwait(false); }
            catch { ct.ThrowIfCancellationRequested(); throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
            ct.ThrowIfCancellationRequested();
            if (serial.Length != 6) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            var capability = new ToolAccessCapability(toolAccess, binding, store, credentials);
            return new(connection, lease, gateway, serial, _createTransport, _localEndpoint, capability);
        }
        catch (Exception error)
        {
            try { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }
            catch { /* Joined cleanup must not replace a typed, redacted acquisition failure. */ }
            finally { credentials?.Dispose(); CryptographicOperations.ZeroMemory(serial); lease.Dispose(); }
            if (error is OperationCanceledException or KnxConnectionException) throw;
            throw new KnxConnectionException(KnxConnectionFailure.ConnectionFailed);
        }
    }
}

internal sealed class ToolAccessCapability(KnxToolAccessProfile profile, KnxIndividualSecureCounterBinding binding,
    DataSecureCounterStore store, KnxToolAccessCredentialScope credentials) : IAsyncDisposable
{
    internal void RequireTarget(IndividualAddress target)
    { if (target != profile.Target) throw new KnxConnectionException(KnxConnectionFailure.ToolAccessBindingMismatch); }
    internal Task PreflightAsync(CancellationToken ct) => ValidateStoreAsync(store, binding, profile.Target, ct);
    internal KnxIndividualSecureCemiProcessor CreateProcessor() => credentials.CreateProcessor(store, binding, profile.MaximumWireTpduLength);
    internal static async Task ValidateStoreAsync(DataSecureCounterStore store, KnxIndividualSecureCounterBinding binding, IndividualAddress target, CancellationToken ct)
    {
        if (store is null || binding.KeyKind != KnxIndividualSecureKeyKind.ToolAccess || store.IdentityFingerprint != binding.Identity.Fingerprint
            || store.LocalAddress != binding.Identity.LocalAddress || !store.KnownSenders.Contains(target))
            throw new KnxConnectionException(KnxConnectionFailure.ToolAccessBindingMismatch);
        try { await store.ValidateReadyForPeerAsync(target, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw KnxToolAccessProfile.StateFailure(error); }
    }
    internal static async Task InitializeAsync(KnxTunnel tunnel, CancellationToken ct)
    {
        try { await tunnel.InitializeCemiSecurityAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DataSecureCounterStateException error) { throw KnxToolAccessProfile.StateFailure(error); }
        catch { throw new KnxConnectionException(KnxConnectionFailure.ToolAccessSynchronizationFailed); }
    }
    public async ValueTask DisposeAsync()
    { try { await store.DisposeAsync().ConfigureAwait(false); } finally { credentials.Dispose(); } }
}

public sealed partial class KnxGatewayOperation
{
    private readonly ToolAccessCapability? _toolAccess;
    internal KnxGatewayOperation(KnxConnectionProfile profile, IDisposable lease, KnxSecureCredentialScope credentials, byte[] serial,
        Func<KnxConnectionProfile, IUdpTransport> createTransport, Func<IUdpTransport, IPEndPoint, IPEndPoint> localEndpoint, ToolAccessCapability toolAccess)
        : this(profile, lease, credentials, serial, createTransport, localEndpoint) => _toolAccess = toolAccess;
}
