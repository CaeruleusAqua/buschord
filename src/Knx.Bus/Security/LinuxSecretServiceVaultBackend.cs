using System.Security.Cryptography;

namespace Knx.Bus.Security;

internal sealed record LinuxSecretMatch(string Path, bool Locked);
/// <summary>Exact-target trusted native seam. There is deliberately no Unlock, Prompt, enumerate-all or plaintext fallback method.</summary>
internal interface ILinuxSecretServiceSession : IDisposable
{
    bool DefaultCollectionLocked { get; }
    IReadOnlyList<LinuxSecretMatch> FindExact(SecureCredentialBinding binding);
    void VerifyExactAttributes(string itemPath, SecureCredentialBinding binding);
    byte[] ReadOwned(string itemPath);
    void Create(SecureCredentialBinding binding, byte[] borrowedBlob);
    void Replace(string itemPath, byte[] borrowedBlob);
    void Delete(string itemPath);
}

/// <summary>Existing Secret Service via libsecret/GIO. Locked collections and required prompts fail; never unlocks implicitly.</summary>
internal sealed class LinuxSecretServiceVaultBackend : ISecureCredentialVaultBackend
{
    private readonly Func<CancellationToken, ILinuxSecretServiceSession> _open;
    internal LinuxSecretServiceVaultBackend(Func<CancellationToken, ILinuxSecretServiceSession>? open = null)
        => _open = open ?? (ct => new LibsecretCredentialSession(ct));
    public SecureCredentialCapability Probe(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var session = _open(ct);
        return session.DefaultCollectionLocked ? SecureCredentialCapability.Locked : SecureCredentialCapability.Ready;
    }
    public byte[] ReadOwned(SecureCredentialBinding binding, CancellationToken ct)
    {
        byte[]? result = null;
        try
        {
            using var lease = CredentialEntryLock.Enter(binding, ct); using var session = _open(ct); var item = Single(session, binding);
            if (item is null) throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound);
            result = ReadVerified(session, binding, item);
        }
        catch { if (result is not null) CryptographicOperations.ZeroMemory(result); throw; }
        return result;
    }
    public void Write(SecureCredentialBinding binding, byte[] borrowedBlob, SecureCredentialWriteMode mode, CancellationToken ct)
    {
        SecureCredentialEnvelope.Validate(binding, borrowedBlob); using var lease = CredentialEntryLock.Enter(binding, ct); using var session = _open(ct); var item = Single(session, binding);
        if (item is null)
        {
            if (mode == SecureCredentialWriteMode.ReplaceMatching) throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound);
            if (session.DefaultCollectionLocked) throw new SecureCredentialVaultException(SecureCredentialFailure.Locked);
            ct.ThrowIfCancellationRequested(); session.Create(binding, borrowedBlob);
        }
        else
        {
            if (mode == SecureCredentialWriteMode.CreateNew) throw new SecureCredentialVaultException(SecureCredentialFailure.AlreadyExists);
            var existing = ReadVerified(session, binding, item);
            try { ct.ThrowIfCancellationRequested(); session.Replace(item.Path, borrowedBlob); }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }
    }
    public bool DeleteMatching(SecureCredentialBinding binding, CancellationToken ct)
    {
        using var lease = CredentialEntryLock.Enter(binding, ct); using var session = _open(ct); var item = Single(session, binding); if (item is null) return false;
        var existing = ReadVerified(session, binding, item);
        try { ct.ThrowIfCancellationRequested(); session.Delete(item.Path); return true; }
        catch (SecureCredentialVaultException ex) when (ex.Failure == SecureCredentialFailure.NotFound) { return false; }
        finally { CryptographicOperations.ZeroMemory(existing); }
    }
    private static LinuxSecretMatch? Single(ILinuxSecretServiceSession session, SecureCredentialBinding binding)
    {
        var matches = session.FindExact(binding);
        if (matches.Count > 1) throw new SecureCredentialVaultException(SecureCredentialFailure.Ambiguous);
        if (matches.Count == 0) return null;
        if (matches[0].Locked) throw new SecureCredentialVaultException(SecureCredentialFailure.Locked);
        session.VerifyExactAttributes(matches[0].Path, binding); return matches[0];
    }
    private static byte[] ReadVerified(ILinuxSecretServiceSession session, SecureCredentialBinding binding, LinuxSecretMatch item)
    {
        var bytes = session.ReadOwned(item.Path);
        try { SecureCredentialEnvelope.Validate(binding, bytes); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
}
