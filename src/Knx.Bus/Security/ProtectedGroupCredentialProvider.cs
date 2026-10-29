using System.Security.Cryptography;
using Knx.Bus.Connections;

namespace Knx.Bus.Security;

/// <summary>Trusted provider boundary. Custom implementations must enforce signature, full pin, exact interface and policy/key-domain binding.</summary>
public interface IKnxGroupCredentialProvider
{
    Task<KnxGroupCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource,
        KnxGroupSecurityProfile policy, CancellationToken ct);
}

public sealed class KnxGroupCredentialScope : IDisposable
{
    private readonly object _gate = new();
    private readonly DataSecureGroupConfiguration[] _groups;
    private bool _disposed;
    public KnxSecureCredentialScope Gateway { get; }
    public KnxGroupCredentialScope(KnxSecureCredentialScope gateway, IEnumerable<DataSecureGroupConfiguration> groups)
    {
        Gateway = gateway ?? throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var owned = new List<DataSecureGroupConfiguration>();
        try
        {
            foreach (var group in groups)
            {
                if (group.Key.Length != 16 || owned.Count >= 4096) throw new KnxConnectionException(KnxConnectionFailure.UnsupportedCredentials);
                owned.Add(new(group.Group, group.Key.ToArray(), group.Protection, Array.AsReadOnly(group.PermittedSenders.ToArray())));
            }
            _groups = owned.ToArray();
        }
        catch { foreach (var group in owned) Wipe(group); throw; }
    }
    internal KnxGroupConnectionProcessor CreateProcessor(DataSecureCounterStore store, DataSecureStateIdentity identity, KnxGroupSecurityProfile policy)
    {
        lock (_gate)
        {
            if (_disposed) throw new KnxConnectionException(KnxConnectionFailure.Disposed);
            var expected = policy.Groups;
            if (_groups.Length != expected.Count || expected.Any(wanted => !_groups.Any(actual => actual.Group == wanted.Group && actual.Protection == wanted.Protection
                && actual.PermittedSenders.ToHashSet().SetEquals(wanted.PermittedSenders)))
                || policy.AllowedPlainGroups.Any(group => Gateway.ProtectedGroups.Contains(group)))
                throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
            var context = new KnxDataSecureGroupContext(store, identity, _groups);
            return new(new(context, policy.AllowedPlainGroups, policy.AllowedPlainIndividualPeers), policy);
        }
    }
    private static void Wipe(DataSecureGroupConfiguration group)
    { if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(group.Key, out var segment)) CryptographicOperations.ZeroMemory(segment.AsSpan()); }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; foreach (var group in _groups) Wipe(group); Gateway.Dispose(); } }
    public override string ToString() => "[ordinary group credential capability]";
}

public sealed class ProtectedGroupCredentialProvider : IKnxGroupCredentialProvider
{
    public async Task<KnxGroupCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource,
        KnxGroupSecurityProfile policy, CancellationToken ct)
    {
        char[] password = []; byte[] xml = [], user = [], device = [], userKey = [], deviceKey = [];
        var temporaryKeys = new List<byte[]>(); KnxSecureCredentialScope? gateway = null;
        try
        {
            passwordSource ??= reference.PasswordEnvironmentName is { } name ? new EnvironmentSecurePasswordSource(name)
                : throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            password = await passwordSource.ReadOwnedAsync(ct).ConfigureAwait(false);
            xml = await SecureLocalFiles.ReadPrivateAsync(reference.Path, ProtectedKnxKeyringImporter.MaximumXmlBytes, ct).ConfigureAwait(false);
            using var ring = ProtectedKnxKeyringImporter.Import(xml, password, reference.ExpectedProjectLabel);
            if (reference.ExpectedSourceFingerprint is not { } pin || pin != ring.SourceFingerprint)
                throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
            var selected = ring.SelectTunneling(reference.HostAddress, reference.UserId, reference.TunnelAddress);
            if (policy.AllowedPlainGroups.Any(group => ring.GroupKeys.ContainsKey(group)))
                throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
            var excluded = new HashSet<string>(policy.ExternalIndividualKeyFingerprints, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in ring.Devices.Values)
            {
                if (entry.ToolKey is null) continue;
                var key = entry.ToolKey.CopyBytes();
                try { excluded.Add(Convert.ToHexString(SHA256.HashData(key))); } finally { CryptographicOperations.ZeroMemory(key); }
            }
            if (ring.Backbone?.Key is { } backbone)
            { var key = backbone.CopyBytes(); try { excluded.Add(Convert.ToHexString(SHA256.HashData(key))); } finally { CryptographicOperations.ZeroMemory(key); } }
            var configurations = new List<DataSecureGroupConfiguration>();
            foreach (var group in policy.Groups)
            {
                if (!selected.Groups.TryGetValue(group.Group, out var senders) || !ring.GroupKeys.TryGetValue(group.Group, out var secret)
                    || group.PermittedSenders.Any(sender => !senders.Contains(sender)))
                    throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
                var key = secret.CopyBytes(); temporaryKeys.Add(key);
                if (excluded.Contains(Convert.ToHexString(SHA256.HashData(key))))
                    throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
                configurations.Add(new(group.Group, key, group.Protection, group.PermittedSenders));
            }
            user = selected.UserPassword!.CopyBytes(); device = selected.DeviceAuthenticationPassword!.CopyBytes();
            userKey = KnxIpSecureCodec.DeriveUserKey(user); deviceKey = KnxIpSecureCodec.DeriveDeviceAuthenticationKey(device);
            if (temporaryKeys.Any(key => CryptographicOperations.FixedTimeEquals(key, userKey) || CryptographicOperations.FixedTimeEquals(key, deviceKey)))
                throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityBindingMismatch);
            gateway = new(reference.HostAddress, reference.TunnelAddress, reference.UserId, ring.SourceFingerprint, userKey, deviceKey, ring.GroupKeys.Keys);
            ct.ThrowIfCancellationRequested(); var result = new KnxGroupCredentialScope(gateway, configurations); gateway = null; return result;
        }
        catch (KnxConnectionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedKeyringImportException) { throw new KnxConnectionException(KnxConnectionFailure.KeyringRejected); }
        catch (InvalidOperationException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
        finally
        {
            gateway?.Dispose(); Array.Clear(password);
            foreach (var bytes in temporaryKeys.Concat(new[] { xml, user, device, userKey, deviceKey })) CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
