using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Knx.Bus.Security;

namespace Knx.Bus.Connections;

public sealed record KnxConnectionSecurity(KnxTransportKind Transport, bool GatewayAuthenticated, IndividualAddress AssignedAddress, string? KeyringFingerprint);
public interface IKnxClientSerialProvider { byte[] ReadOrCreate(); }

/// <summary>One private non-secret client identity. Cooperating processes hold the same bounded initialization lease;
/// existing malformed data is never replaced implicitly. The persistent lease file must not be removed while in use.</summary>
public sealed class FileKnxClientSerialProvider(string? directory = null) : IKnxClientSerialProvider
{
    public byte[] ReadOrCreate()
    {
        try
        {
            var root = Path.GetFullPath(directory ?? GatewayLeaseCoordinator.DefaultDirectory);
            Directory.CreateDirectory(root); SecureLocalFiles.NoLinks(root);
            using var initialization = AcquireInitializationLease(root);
            var path = Path.Combine(root, "client.serial"); RejectExistingLinks(path);
            if (!File.Exists(path))
            {
                var serial = RandomNumberGenerator.GetBytes(6);
                var temporary = Path.Combine(root, "client-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var created = new FileStream(temporary, options)) { created.Write(serial); created.Flush(true); }
                    // Move(false) is not a concurrent no-replace primitive on every platform. The common lease
                    // protects publication; read the final file below rather than returning an unpublished proposal.
                    try { File.Move(temporary, path, overwrite: false); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally { CryptographicOperations.ZeroMemory(serial); if (File.Exists(temporary)) File.Delete(temporary); }
            }
            SecureLocalFiles.NoLinks(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != 6) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            var result = new byte[6]; file.ReadExactly(result); return result;
        }
        catch (KnxConnectionException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
    }
    private static FileStream AcquireInitializationLease(string root)
    {
        var path = Path.Combine(root, "client.serial.lock"); var clock = System.Diagnostics.Stopwatch.StartNew();
        for (;;)
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(5)) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            RejectExistingLinks(path);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                var lease = new FileStream(path, options);
                try { SecureLocalFiles.NoLinks(path); return lease; }
                catch { lease.Dispose(); throw; }
            }
            catch (IOException) { Thread.Sleep(15); }
        }
    }
    private static void RejectExistingLinks(string path)
    {
        try { SecureLocalFiles.NoLinks(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}

public sealed partial class KnxConnectionFactory
{
    private readonly GatewayLeaseCoordinator _leases;
    private readonly IKnxSecureCredentialProvider _credentials;
    private readonly IKnxClientSerialProvider _serial;
    private readonly Func<KnxConnectionProfile, IUdpTransport> _createTransport;
    private readonly Func<IUdpTransport, IPEndPoint, IPEndPoint> _localEndpoint;
    public KnxConnectionFactory(GatewayLeaseCoordinator? leases = null, IKnxSecureCredentialProvider? credentials = null,
        IKnxClientSerialProvider? serial = null, Func<KnxConnectionProfile, IUdpTransport>? createTransport = null,
        Func<IUdpTransport, IPEndPoint, IPEndPoint>? localEndpoint = null)
    {
        _leases = leases ?? new(); _credentials = credentials ?? new ProtectedKeyringCredentialProvider(); _serial = serial ?? new FileKnxClientSerialProvider();
        _createTransport = createTransport ?? (profile => new UdpTransport(profile.LocalAddress ?? IPAddress.Any, 0));
        _localEndpoint = localEndpoint ?? ((transport, gateway) =>
        {
            if (transport is not UdpTransport udp) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            var endpoint = udp.LocalEndPoint;
            return new(endpoint.Address.Equals(IPAddress.Any) ? UdpTransport.ResolveLocalAddress(gateway) : endpoint.Address, endpoint.Port);
        });
    }
    public async Task<KnxGatewayOperation> AcquireOperationAsync(KnxConnectionProfile profile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lease = _leases.Acquire(profile.Gateway); KnxSecureCredentialScope? credentials = null; byte[] serial = [];
        try
        {
            if (profile.RequiresAuthentication)
            {
                credentials = await _credentials.AcquireAsync(profile.Keyring!, profile.PasswordSource, ct).ConfigureAwait(false);
                if (credentials.Host != profile.Keyring!.HostAddress || credentials.UserId != profile.Keyring.UserId || credentials.TunnelAddress != profile.RequestedAddress
                    || profile.Keyring.ExpectedSourceFingerprint is { } expected && expected != credentials.SourceFingerprint)
                    throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
                ct.ThrowIfCancellationRequested();
                try
                {
                    // The synchronous file lease may wait. Keep it off the caller's dispatcher and always join it
                    // before cancellation can release the gateway/credential owners; do not abandon it with WaitAsync.
                    serial = await Task.Run(() => { ct.ThrowIfCancellationRequested(); return _serial.ReadOrCreate(); }).ConfigureAwait(false);
                }
                catch { ct.ThrowIfCancellationRequested(); throw; }
                ct.ThrowIfCancellationRequested();
                if (serial.Length != 6) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            }
            ct.ThrowIfCancellationRequested();
            return new(profile, lease, credentials, serial, _createTransport, _localEndpoint);
        }
        catch (Exception error)
        {
            credentials?.Dispose(); CryptographicOperations.ZeroMemory(serial); lease.Dispose();
            if (error is OperationCanceledException or KnxConnectionException) throw;
            throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
        }
    }
}

/// <summary>One gateway reservation and frozen credential capability spanning sequential fresh connections and recovery.</summary>
public sealed partial class KnxGatewayOperation : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _lease;
    private readonly KnxSecureCredentialScope? _credentials;
    private readonly byte[] _serial;
    private readonly Func<KnxConnectionProfile, IUdpTransport> _createTransport;
    private readonly Func<IUdpTransport, IPEndPoint, IPEndPoint> _localEndpoint;
    private IAsyncDisposable? _active;
    private Task? _disposal;
    private bool _stopping;
    public KnxConnectionProfile Profile { get; }
    public string? KeyringFingerprint => _credentials?.SourceFingerprint;
    internal KnxGatewayOperation(KnxConnectionProfile profile, IDisposable lease, KnxSecureCredentialScope? credentials, byte[] serial,
        Func<KnxConnectionProfile, IUdpTransport> createTransport, Func<IUdpTransport, IPEndPoint, IPEndPoint> localEndpoint)
    { Profile = profile; _lease = lease; _credentials = credentials; _serial = serial; _createTransport = createTransport; _localEndpoint = localEndpoint; }

    public async Task<KnxConnectedTunnel> OpenTunnelAsync(CancellationToken ct = default)
    {
        if (_toolAccess is not null) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _transition.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try { return await OpenCoreAsync(cancellation.Token).ConfigureAwait(false); }
        finally { _transition.Release(); }
    }
    public async Task<IManagementConnection> OpenManagementAsync(IndividualAddress target, bool readOnly, CancellationToken ct = default)
    {
        _toolAccess?.RequireTarget(target);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _transition.WaitAsync(cancellation.Token).ConfigureAwait(false);
        KnxConnectedTunnel? owned = null; KnxManagementClient? client = null;
        try
        {
            owned = await OpenCoreAsync(cancellation.Token).ConfigureAwait(false);
            if (owned.Tunnel.AssignedAddress == target) throw new ManagementAddressCollisionException(target);
            client = new(owned.Tunnel, target, readOnly);
            await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            var result = new ManagementOwner(this, client, owned);
            lock (_sync)
            {
                if (_toolAccess is not null)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_stopping) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
                }
                _active = result;
            }
            return result;
        }
        catch (Exception error)
        {
            try
            {
                try { if (client is not null) await client.DisposeAsync().ConfigureAwait(false); }
                finally { if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false); }
            }
            catch when (_toolAccess is not null) { /* Preserve the safe Tool-Access acquisition reason. */ }
            if (_toolAccess is not null && error is not (OperationCanceledException or KnxConnectionException))
                throw new KnxConnectionException(KnxConnectionFailure.ConnectionFailed);
            throw;
        }
        finally { _transition.Release(); }
    }
    private async Task<KnxConnectedTunnel> OpenCoreAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_stopping) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
            if (_active is not null) throw new KnxConnectionException(KnxConnectionFailure.Occupied);
        }
        ct.ThrowIfCancellationRequested();
        IUdpTransport? transport = null; SecureSessionTransport? secure = null; KnxIpSecureSession? session = null; KnxTunnel? tunnel = null;
        try
        {
            if (_toolAccess is not null) await _toolAccess.PreflightAsync(ct).ConfigureAwait(false);
            transport = _createTransport(Profile);
            if (Profile.Kind == KnxTransportKind.IpSecureUdp)
            {
                session = (_credentials ?? throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile)).CreateSession(_serial);
                secure = new(transport, Profile.Gateway, _localEndpoint(transport, Profile.Gateway), session);
                await secure.ConnectAsync(ct).ConfigureAwait(false);
                tunnel = new(secure, Profile.Gateway, Profile.RequestedAddress, secure.LocalEndpoint, null,
                    _toolAccess is not null ? _toolAccess.CreateProcessor() : new KeyringGroupBoundary(_credentials!.ProtectedGroups));
            }
            else if (Profile.Kind == KnxTransportKind.PlainUdp)
                tunnel = new(transport, Profile.Gateway, Profile.RequestedAddress);
            else throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            await tunnel.ConnectAsync(ct).ConfigureAwait(false);
            if (Profile.RequiresAuthentication && (!secure!.IsConnected || tunnel.AssignedAddress != Profile.RequestedAddress))
                throw new KnxConnectionException(KnxConnectionFailure.AssignedAddressMismatch);
            if (_toolAccess is not null) await ToolAccessCapability.InitializeAsync(tunnel, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var owned = new KnxConnectedTunnel(this, tunnel, new(Profile.Kind, secure?.IsConnected == true, tunnel.AssignedAddress, KeyringFingerprint));
            lock (_sync)
            {
                if (_toolAccess is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_stopping) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
                }
                _active = owned;
            }
            return owned;
        }
        catch (Exception error)
        {
            try
            {
                if (tunnel is not null) await tunnel.DisposeAsync().ConfigureAwait(false);
                else if (secure is not null) await secure.DisposeAsync().ConfigureAwait(false);
                else { session?.Dispose(); transport?.Dispose(); }
            }
            catch { /* Preserve the sanitized original acquisition error after joined cleanup. */ }
            if (error is OperationCanceledException or KnxConnectionException) throw;
            if (error is AuthenticationException) throw new KnxConnectionException(KnxConnectionFailure.AuthenticationFailed);
            throw new KnxConnectionException(KnxConnectionFailure.ConnectionFailed);
        }
    }
    internal void Released(IAsyncDisposable owner) { lock (_sync) if (ReferenceEquals(_active, owner)) _active = null; }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task pending;
        lock (_sync)
        {
            if (_disposal is null) { _stopping = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; }
            pending = _disposal;
        }
        if (completion is not null) { try { _lifetime.Cancel(); } catch (AggregateException) { } finally { _ = FinishDisposeAsync(completion); } }
        return new(pending);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            IAsyncDisposable? active; lock (_sync) active = _active;
            if (active is not null) try { await active.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        }
        finally
        {
            try { if (_toolAccess is not null) await _toolAccess.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            finally { _credentials?.Dispose(); CryptographicOperations.ZeroMemory(_serial); _lease.Dispose(); _transition.Release(); _lifetime.Dispose(); }
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(new KnxConnectionException(KnxConnectionFailure.ConnectionFailed));
    }
    private sealed class ManagementOwner(KnxGatewayOperation parent, KnxManagementClient client, KnxConnectedTunnel tunnel) : IManagementConnection
    {
        private readonly object _gate = new(); private Task? _disposal;
        public KnxManagementClient Client => client;
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? completion = null; Task pending;
            lock (_gate) { if (_disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; } pending = _disposal; }
            if (completion is not null) _ = CloseAndCompleteAsync(completion);
            return new(pending);
        }
        private async Task CloseAndCompleteAsync(TaskCompletionSource completion)
        { try { await CloseAsync().ConfigureAwait(false); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } }
        private async Task CloseAsync()
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            finally { try { await tunnel.DisposeAsync().ConfigureAwait(false); } finally { parent.Released(this); } }
        }
    }
}

public sealed class KnxConnectedTunnel : IAsyncDisposable
{
    private readonly KnxGatewayOperation _parent;
    private readonly object _gate = new();
    private Task? _disposal;
    public KnxTunnel Tunnel { get; }
    public KnxConnectionSecurity Security { get; }
    internal KnxConnectedTunnel(KnxGatewayOperation parent, KnxTunnel tunnel, KnxConnectionSecurity security) { _parent = parent; Tunnel = tunnel; Security = security; }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task pending;
        lock (_gate) { if (_disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; } pending = _disposal; }
        if (completion is not null) _ = CloseAndCompleteAsync(completion);
        return new(pending);
    }
    private async Task CloseAndCompleteAsync(TaskCompletionSource completion)
    { try { await CloseAsync().ConfigureAwait(false); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } }
    private async Task CloseAsync() { try { await Tunnel.DisposeAsync().ConfigureAwait(false); } finally { _parent.Released(this); } }
}
