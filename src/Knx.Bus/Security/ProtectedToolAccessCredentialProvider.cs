using System.Security.Cryptography;
using Knx.Bus.Connections;

namespace Knx.Bus.Security;

/// <summary>Trusted provider boundary: exact verified file/interface/device selection, no alternate or derived Tool keys.</summary>
public interface IKnxToolAccessCredentialProvider
{
    Task<KnxToolAccessCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource,
        IndividualAddress target, IReadOnlyList<string> ordinaryPointToPointKeyFingerprints, CancellationToken ct);
}

public sealed class KnxToolAccessCredentialScope : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _tool;
    private bool _disposed;
    public IndividualAddress Target { get; }
    public KnxSecureCredentialScope Gateway { get; }
    public KnxToolAccessCredentialScope(IndividualAddress target, KnxSecureCredentialScope gateway, ReadOnlySpan<byte> toolKey)
    {
        if (target.Raw == 0 || target.Device == 255 || gateway is null || toolKey.Length != 16)
            throw new KnxConnectionException(KnxConnectionFailure.UnsupportedCredentials);
        Target = target; Gateway = gateway; _tool = toolKey.ToArray();
    }
    internal KnxIndividualSecureCemiProcessor CreateProcessor(DataSecureCounterStore store, KnxIndividualSecureCounterBinding binding, int capacity)
    {
        lock (_gate)
        {
            if (_disposed) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
            // No other-traffic policy: all groups and other individual destinations remain blocked.
            return new(new(store, binding, Target, _tool, capacity));
        }
    }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_tool); Gateway.Dispose(); } }
    public override string ToString() => "[Tool-Access credential capability]";
}

public sealed class ProtectedToolAccessCredentialProvider : IKnxToolAccessCredentialProvider
{
    public async Task<KnxToolAccessCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource,
        IndividualAddress target, IReadOnlyList<string> ordinaryPointToPointKeyFingerprints, CancellationToken ct)
    {
        char[] password = []; byte[] xml = [], user = [], device = [], tool = [], userKey = [], deviceKey = [];
        KnxSecureCredentialScope? gateway = null;
        try
        {
            passwordSource ??= reference.PasswordEnvironmentName is { } name ? new EnvironmentSecurePasswordSource(name)
                : throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            password = await passwordSource.ReadOwnedAsync(ct).ConfigureAwait(false);
            xml = await SecureLocalFiles.ReadPrivateAsync(reference.Path, ProtectedKnxKeyringImporter.MaximumXmlBytes, ct).ConfigureAwait(false);
            using var ring = ProtectedKnxKeyringImporter.Import(xml, password, reference.ExpectedProjectLabel);
            if (reference.ExpectedSourceFingerprint is not { } expected || expected != ring.SourceFingerprint)
                throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
            var selected = ring.SelectTunneling(reference.HostAddress, reference.UserId, reference.TunnelAddress);
            if (!ring.Devices.TryGetValue(target, out var entry) || entry.ToolKey is not { Kind: ProtectedKeyringSecretKind.Aes128Key, Length: 16 })
                throw new KnxConnectionException(KnxConnectionFailure.ToolAccessKeyUnavailable);
            tool = entry.ToolKey.CopyBytes(); var fingerprint = Convert.ToHexString(SHA256.HashData(tool));
            if (ordinaryPointToPointKeyFingerprints.Contains(fingerprint, StringComparer.OrdinalIgnoreCase))
                throw new KnxConnectionException(KnxConnectionFailure.ToolAccessBindingMismatch);
            foreach (var ordinary in ring.GroupKeys.Values)
            {
                var key = ordinary.CopyBytes();
                try { if (CryptographicOperations.FixedTimeEquals(key, tool)) throw new KnxConnectionException(KnxConnectionFailure.ToolAccessBindingMismatch); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            user = selected.UserPassword!.CopyBytes(); device = selected.DeviceAuthenticationPassword!.CopyBytes();
            userKey = KnxIpSecureCodec.DeriveUserKey(user); deviceKey = KnxIpSecureCodec.DeriveDeviceAuthenticationKey(device);
            gateway = new(reference.HostAddress, reference.TunnelAddress, reference.UserId, ring.SourceFingerprint, userKey, deviceKey, ring.GroupKeys.Keys);
            ct.ThrowIfCancellationRequested();
            var result = new KnxToolAccessCredentialScope(target, gateway, tool); gateway = null; return result;
        }
        catch (KnxConnectionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedKeyringImportException) { throw new KnxConnectionException(KnxConnectionFailure.KeyringRejected); }
        catch (InvalidOperationException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
        finally
        {
            gateway?.Dispose(); Array.Clear(password);
            foreach (var bytes in new[] { xml, user, device, tool, userKey, deviceKey }) CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
