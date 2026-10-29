using System.Security.Cryptography;
using Knx.Bus.Connections;

namespace Knx.Bus.Security;

public interface ISecurePasswordSource { ValueTask<char[]> ReadOwnedAsync(CancellationToken token); }
public sealed class EnvironmentSecurePasswordSource : ISecurePasswordSource
{
    private readonly string _name;
    public EnvironmentSecurePasswordSource(string name) { ValidateName(name); _name = name; }
    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 128 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(value => !(char.IsAsciiLetterOrDigit(value) || value == '_'))) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
    }
    public ValueTask<char[]> ReadOwnedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var value = Environment.GetEnvironmentVariable(_name);
        if (value is null || value.Length is < 1 or > 1024) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
        return ValueTask.FromResult(value.ToCharArray());
    }
    public override string ToString() => "[secure password source]";
}
public interface IKnxSecureCredentialProvider
{ Task<KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource, CancellationToken token); }

/// <summary>Only selected derived keys survive import. This capability may open fresh sessions until operation recovery drains.</summary>
public sealed class KnxSecureCredentialScope : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _user, _device;
    private bool _disposed;
    public IndividualAddress Host { get; }
    public IndividualAddress TunnelAddress { get; }
    public byte UserId { get; }
    public string SourceFingerprint { get; }
    public IReadOnlyList<GroupAddress> ProtectedGroups { get; }
    public KnxSecureCredentialScope(IndividualAddress host, IndividualAddress tunnelAddress, byte userId, string sourceFingerprint,
        ReadOnlySpan<byte> userKey, ReadOnlySpan<byte> deviceKey, IEnumerable<GroupAddress>? protectedGroups = null)
    {
        if (userKey.Length != 16 || deviceKey.Length != 16 || userId is < 1 or > 127) throw new KnxConnectionException(KnxConnectionFailure.UnsupportedCredentials);
        Host = host; TunnelAddress = tunnelAddress; UserId = userId; SourceFingerprint = sourceFingerprint; _user = userKey.ToArray(); _device = deviceKey.ToArray();
        ProtectedGroups = Array.AsReadOnly((protectedGroups ?? []).Distinct().ToArray());
    }
    public KnxIpSecureSession CreateSession(ReadOnlySpan<byte> localSerial)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return new(UserId, _user, _device, localSerial); } }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_user); CryptographicOperations.ZeroMemory(_device); } }
    public override string ToString() => "[secure credential capability]";
}

public sealed class ProtectedKeyringCredentialProvider : IKnxSecureCredentialProvider
{
    public async Task<KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource, CancellationToken token)
    {
        char[] password = []; byte[] xml = [], user = [], device = [], userKey = [], deviceKey = [];
        try
        {
            passwordSource ??= reference.PasswordEnvironmentName is { } name ? new EnvironmentSecurePasswordSource(name)
                : throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            password = await passwordSource.ReadOwnedAsync(token).ConfigureAwait(false);
            xml = await SecureLocalFiles.ReadPrivateAsync(reference.Path, ProtectedKnxKeyringImporter.MaximumXmlBytes, token).ConfigureAwait(false);
            using var ring = ProtectedKnxKeyringImporter.Import(xml, password, reference.ExpectedProjectLabel);
            if (reference.ExpectedSourceFingerprint is { } expected && expected != ring.SourceFingerprint) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
            var selected = ring.SelectTunneling(reference.HostAddress, reference.UserId, reference.TunnelAddress);
            user = selected.UserPassword!.CopyBytes(); device = selected.DeviceAuthenticationPassword!.CopyBytes();
            userKey = KnxIpSecureCodec.DeriveUserKey(user); deviceKey = KnxIpSecureCodec.DeriveDeviceAuthenticationKey(device);
            token.ThrowIfCancellationRequested();
            return new(reference.HostAddress, reference.TunnelAddress, reference.UserId, ring.SourceFingerprint, userKey, deviceKey, ring.GroupKeys.Keys);
        }
        catch (KnxConnectionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedKeyringImportException) { throw new KnxConnectionException(KnxConnectionFailure.KeyringRejected); }
        catch (ArgumentException) { throw new KnxConnectionException(KnxConnectionFailure.UnsupportedCredentials); }
        catch (InvalidOperationException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
        finally
        {
            Array.Clear(password); CryptographicOperations.ZeroMemory(xml); CryptographicOperations.ZeroMemory(user); CryptographicOperations.ZeroMemory(device);
            CryptographicOperations.ZeroMemory(userKey); CryptographicOperations.ZeroMemory(deviceKey);
        }
    }
}
