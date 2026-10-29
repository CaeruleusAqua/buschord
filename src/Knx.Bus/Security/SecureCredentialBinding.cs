using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Knx.Bus.Connections;

namespace Knx.Bus.Security;

public sealed class SecureCredentialNamespace
{
    public static SecureCredentialNamespace Production { get; } = new("BusChord.Credentials.v1");
    internal string Name { get; }
    private SecureCredentialNamespace(string name) => Name = name;
    /// <summary>Only for a new synthetic entry owned by this acceptance run; never derive a name from an existing user entry.</summary>
    public static SecureCredentialNamespace Acceptance(Guid freshRunId)
        => freshRunId == Guid.Empty ? throw new ArgumentException("A fresh acceptance run ID is required.") : new("BusChord.Acceptance." + freshRunId.ToString("N"));
    public override string ToString() => "[credential namespace]";
}

/// <summary>
/// Exact non-secret binding of one keyring password to its stable project scope, private path, verified source fingerprint
/// and selected Host/User/Tunnel tuple. Changed selectors or project copies do not migrate a saved password automatically.
/// Profiles may serialize only Id; constructing the reference never reads the keyring or OS vault.
/// </summary>
public sealed class SecureCredentialBinding
{
    public string Id { get; }
    [JsonIgnore] public string TargetName { get; }
    internal string NamespaceName { get; }
    internal byte[] BindingDigest => Convert.FromHexString(Id);
    private SecureCredentialBinding(string id, string space) { Id = id; NamespaceName = space; TargetName = space + "." + id; }
    public static SecureCredentialBinding Create(SecureCredentialNamespace space, string stableProjectScope, SecureKeyringReference reference)
    {
        ArgumentNullException.ThrowIfNull(space); ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(stableProjectScope) || stableProjectScope.Length > 128 || stableProjectScope.Contains('\0')
            || reference.ExpectedSourceFingerprint is null) throw new SecureCredentialVaultException(SecureCredentialFailure.InvalidBinding);
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string value)
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(value); Span<byte> length = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); digest.AppendData(length); digest.AppendData(bytes);
            }
            Add("BusChord.OsCredentialBinding.1"); Add(space.Name); Add(stableProjectScope);
            var path = Path.GetFullPath(reference.Path); Add(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
            Add(reference.ExpectedProjectLabel); Add(reference.ExpectedSourceFingerprint);
            Span<byte> selectors = stackalloc byte[5]; BinaryPrimitives.WriteUInt16BigEndian(selectors, reference.HostAddress.Raw);
            selectors[2] = reference.UserId; BinaryPrimitives.WriteUInt16BigEndian(selectors[3..], reference.TunnelAddress.Raw); digest.AppendData(selectors);
            return new(Convert.ToHexString(digest.GetHashAndReset()), space.Name);
        }
        catch (SecureCredentialVaultException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { throw new SecureCredentialVaultException(SecureCredentialFailure.InvalidBinding); }
    }
    public override string ToString() => "[bound OS credential reference]";
}
