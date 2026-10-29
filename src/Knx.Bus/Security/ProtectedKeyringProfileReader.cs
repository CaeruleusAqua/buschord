using System.Security.Cryptography;
using Knx.Bus.Connections;

namespace Knx.Bus.Security;

public sealed record ProtectedTunnelChoice(IndividualAddress Host, byte UserId, IndividualAddress TunnelAddress);
public sealed record ProtectedKeyringProfileSummary(string ProjectLabel, string SourceFingerprint, IReadOnlyList<ProtectedTunnelChoice> Interfaces);

/// <summary>Offline, password-verified selection metadata. No keys, passwords, source paths or XML leave this reader.</summary>
public static class ProtectedKeyringProfileReader
{
    public static async Task<ProtectedKeyringProfileSummary> ReadAsync(string path, string expectedProjectLabel, ISecurePasswordSource passwordSource, CancellationToken token = default)
    {
        byte[] xml = []; char[] password = [];
        try
        {
            password = await passwordSource.ReadOwnedAsync(token).ConfigureAwait(false);
            xml = await SecureLocalFiles.ReadPrivateAsync(path, ProtectedKnxKeyringImporter.MaximumXmlBytes, token).ConfigureAwait(false);
            using var ring = ProtectedKnxKeyringImporter.Import(xml, password, expectedProjectLabel);
            return new(ring.ProjectName, ring.SourceFingerprint, Array.AsReadOnly(ring.Interfaces.Values.Where(entry => entry.Type == ProtectedKeyringInterfaceType.Tunneling
                && entry.Host is not null && entry.UserId is not null && entry.UserPassword is not null && entry.DeviceAuthenticationPassword is not null)
                .Select(entry => new ProtectedTunnelChoice(entry.Host!.Value, entry.UserId!.Value, entry.Address)).ToArray()));
        }
        catch (OperationCanceledException) { throw; }
        catch (KnxConnectionException) { throw; }
        catch (ProtectedKeyringImportException) { throw new KnxConnectionException(KnxConnectionFailure.KeyringRejected); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
        finally { Array.Clear(password); CryptographicOperations.ZeroMemory(xml); }
    }
}
