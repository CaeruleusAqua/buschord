using System.Net;
using System.Security.Cryptography;
using Knx.Bus.Security;

namespace Knx.Bus.Connections;

public sealed partial class KnxConnectionFactory
{
    public async Task<KnxGatewayOperation> AcquireGroupSecurityOperationAsync(KnxConnectionProfile connection,
        IKnxGroupCredentialProvider credentialProvider, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var policy = connection?.GroupSecurity;
        if (policy is null || credentialProvider is null || connection!.Kind != KnxTransportKind.IpSecureUdp
            || connection.Keyring?.ExpectedSourceFingerprint is null)
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var identity = policy.Bind(connection.RequestedAddress); var lease = _leases.Acquire(connection.Gateway);
        DataSecureCounterStore? store = null; KnxGroupCredentialScope? credentials = null; byte[] serial = [];
        try
        {
            if (policy.SourceGuard is { } sourceGuard) await sourceGuard.BeginOperationAsync(ct).ConfigureAwait(false);
            try { store = await policy.StateSource.OpenExistingAsync(identity, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (KnxConnectionException) { throw; }
            catch (Exception error) { throw KnxGroupSecurityProfile.StateFailure(error); }
            await GroupSecurityCapability.ValidateStoreAsync(store, identity, policy, ct).ConfigureAwait(false);
            try { credentials = await credentialProvider.AcquireAsync(connection.Keyring, connection.PasswordSource, policy, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (KnxConnectionException) { throw; }
            catch { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
            if (credentials is null) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            var gateway = credentials.Gateway;
            if (gateway.Host != connection.Keyring.HostAddress || gateway.UserId != connection.Keyring.UserId || gateway.TunnelAddress != connection.RequestedAddress
                || gateway.SourceFingerprint != connection.Keyring.ExpectedSourceFingerprint)
                throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
            if (policy.SourceGuard is { } afterCredentials) await afterCredentials.ValidateAsync(ct).ConfigureAwait(false);
            // Validate selected policy and all-protected boundary offline, before any socket is created.
            await using (credentials.CreateProcessor(store!, identity, policy)) { }
            try { serial = await Task.Run(() => { ct.ThrowIfCancellationRequested(); return _serial.ReadOrCreate(); }).ConfigureAwait(false); }
            catch { ct.ThrowIfCancellationRequested(); throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
            ct.ThrowIfCancellationRequested();
            if (serial.Length != 6) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            return new(connection, lease, gateway, serial, _createTransport, _localEndpoint, new GroupSecurityCapability(policy, identity, store!, credentials));
        }
        catch (Exception error)
        {
            try { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }
            catch { /* Retain typed failure after joined cleanup. */ }
            finally { credentials?.Dispose(); CryptographicOperations.ZeroMemory(serial); lease.Dispose(); }
            if (error is OperationCanceledException or KnxConnectionException) throw;
            throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
        }
    }
}

internal sealed class GroupSecurityCapability(KnxGroupSecurityProfile profile, DataSecureStateIdentity identity,
    DataSecureCounterStore store, KnxGroupCredentialScope credentials) : IAsyncDisposable
{
    internal void RequirePlainTarget(IndividualAddress target)
    { if (!profile.AllowedPlainIndividualPeers.Contains(target)) throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityPolicyDenied); }
    internal async Task PreflightAsync(CancellationToken ct)
    {
        if (profile.SourceGuard is { } guard) await guard.ValidateAsync(ct).ConfigureAwait(false);
        await ValidateStoreAsync(store, identity, profile, ct).ConfigureAwait(false);
    }
    internal KnxGroupConnectionProcessor CreateProcessor() => credentials.CreateProcessor(store, identity, profile);
    internal static async Task ValidateStoreAsync(DataSecureCounterStore? store, DataSecureStateIdentity identity, KnxGroupSecurityProfile policy, CancellationToken ct)
    {
        var senders = policy.Groups.SelectMany(group => group.PermittedSenders).Distinct().ToArray();
        if (store is null || store.IdentityFingerprint != identity.Fingerprint || store.LocalAddress != identity.LocalAddress
            || senders.Any(sender => !store.KnownSenders.Contains(sender)))
            throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
        try { await store.ValidateReadyForGroupsAsync(senders, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw KnxGroupSecurityProfile.StateFailure(error); }
    }
    public async ValueTask DisposeAsync()
    { try { await store.DisposeAsync().ConfigureAwait(false); } finally { credentials.Dispose(); } }
}

public sealed partial class KnxGatewayOperation
{
    private readonly GroupSecurityCapability? _groupSecurity;
    internal KnxGatewayOperation(KnxConnectionProfile profile, IDisposable lease, KnxSecureCredentialScope credentials, byte[] serial,
        Func<KnxConnectionProfile, IUdpTransport> createTransport, Func<IUdpTransport, IPEndPoint, IPEndPoint> localEndpoint, GroupSecurityCapability groupSecurity)
        : this(profile, lease, credentials, serial, createTransport, localEndpoint) => _groupSecurity = groupSecurity;
}
