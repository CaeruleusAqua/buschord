using System.Security.Cryptography;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.Bus.Security;

/// <summary>Owned integrity bytes from an explicit host reference. Tool requests cannot select a value or source name.</summary>
public interface IGroupIntegrityCapabilitySource
{
    ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct);
}

/// <summary>Same strict canonical Base64/32-byte profile as the Tool-Access console. No hex/whitespace/value fallback.</summary>
public sealed class EnvironmentGroupIntegrityCapabilitySource : IGroupIntegrityCapabilitySource
{
    public ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct)
    {
        var owned = new byte[32];
        try
        {
            EnvironmentSecurePasswordSource.ValidateName(environmentName); ct.ThrowIfCancellationRequested();
            var value = Environment.GetEnvironmentVariable(environmentName);
            if (value is not { Length: 44 } || !Convert.TryFromBase64String(value, owned, out var count) || count != 32
                || Convert.ToBase64String(owned) != value) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            return ValueTask.FromResult(owned);
        }
        catch (OperationCanceledException) { CryptographicOperations.ZeroMemory(owned); throw; }
        catch { CryptographicOperations.ZeroMemory(owned); throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
    }
}

public static class SecureHostReferences
{
    // Shared contract with ToolAccessConsoleProfile.PrivateReference; neither changes the other's frozen source.
    public static string PrivateFile(string path, bool existing)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException();
            path = Path.GetFullPath(path);
            if (Directory.Exists(path) || existing && !File.Exists(path)) throw new ArgumentException();
            for (var item = path; item is not null; item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0
                    || File.Exists(Path.Combine(item, ".git")) || Directory.Exists(Path.Combine(item, ".git"))) throw new ArgumentException();
            return path;
        }
        catch { throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); }
    }
    public static byte[] ReadBounded(string path, int maximum)
    {
        try
        {
            path = PrivateFile(path, true); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 || file.Length > maximum) throw new ArgumentException();
            var bytes = new byte[(int)file.Length];
            try { file.ReadExactly(bytes); return bytes; } catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        catch { throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); }
    }
}
