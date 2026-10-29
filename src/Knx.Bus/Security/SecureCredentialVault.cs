using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Knx.Bus.Security;

public enum SecureCredentialFailure { UnsupportedPlatform, Unavailable, Locked, NotFound, AlreadyExists, Ambiguous, InvalidBinding, BindingMismatch, InvalidSecret, AccessDenied, Cancelled, MutationOutcomeUnknown, NativeFailure }
public enum SecureCredentialCapability { Ready, Locked, Unavailable, UnsupportedPlatform }
public enum SecureCredentialWriteMode { CreateNew, ReplaceMatching }
/// <summary>Fixed codes only. No native error message, inner exception, password or private reference is retained.</summary>
public sealed class SecureCredentialVaultException(SecureCredentialFailure failure) : IOException("OS credential operation failed: " + failure)
{ public SecureCredentialFailure Failure { get; } = failure; }

/// <summary>
/// Trusted OS adapter seam. Read returns a new caller-owned buffer. Write borrows its buffer only for the duration
/// of the call. Implementations must address only the exact binding, must never unlock/prompt implicitly, and must not
/// log or retain secret values. The production factory never substitutes a file or command-line persistence backend.
/// </summary>
public interface ISecureCredentialVaultBackend
{
    SecureCredentialCapability Probe(CancellationToken ct);
    byte[] ReadOwned(SecureCredentialBinding binding, CancellationToken ct);
    void Write(SecureCredentialBinding binding, byte[] borrowedBlob, SecureCredentialWriteMode mode, CancellationToken ct);
    bool DeleteMatching(SecureCredentialBinding binding, CancellationToken ct);
}

/// <summary>Owns operation lifetimes; all secret snapshots and failed/cancelled read results are cleared before completion.</summary>
public sealed class SecureCredentialVault : IAsyncDisposable
{
    private readonly ISecureCredentialVaultBackend _backend;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _drained = Completed();
    private int _operations;
    private bool _disposed;
    private Task? _disposal;
    public SecureCredentialVault(ISecureCredentialVaultBackend trustedBackend) => _backend = trustedBackend ?? throw new ArgumentNullException(nameof(trustedBackend));
    public static SecureCredentialVault CreateCurrentUser() => OperatingSystem.IsWindows() ? new(new WindowsCredentialVaultBackend())
        : OperatingSystem.IsLinux() ? new(new LinuxSecretServiceVaultBackend()) : new(new UnsupportedBackend());
    public async Task<SecureCredentialCapability> ProbeAsync(CancellationToken ct = default)
    {
        using var operation = Admit(ct);
        var capability = await Task.Run(() => { operation.Token.ThrowIfCancellationRequested(); try { return _backend.Probe(operation.Token); } catch (SecureCredentialVaultException ex) { return ex.Failure == SecureCredentialFailure.Locked ? SecureCredentialCapability.Locked : ex.Failure == SecureCredentialFailure.UnsupportedPlatform ? SecureCredentialCapability.UnsupportedPlatform : SecureCredentialCapability.Unavailable; } catch (OperationCanceledException) { operation.Token.ThrowIfCancellationRequested(); return SecureCredentialCapability.Unavailable; } catch { return SecureCredentialCapability.Unavailable; } }).ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested(); return capability;
    }
    public async ValueTask<char[]> ReadOwnedAsync(SecureCredentialBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding); using var operation = Admit(ct); byte[]? blob = null; char[]? password = null;
        try
        {
            blob = await Task.Run(() => { operation.Token.ThrowIfCancellationRequested(); return _backend.ReadOwned(binding, operation.Token); }).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested(); password = SecureCredentialEnvelope.Decode(binding, blob);
            operation.Token.ThrowIfCancellationRequested(); var owned = password; password = null; return owned;
        }
        catch (SecureCredentialVaultException) { throw; }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { throw new OperationCanceledException(operation.Token); }
        catch (OperationCanceledException) { throw new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure); }
        catch { throw new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure); }
        finally { if (blob is not null) CryptographicOperations.ZeroMemory(blob); if (password is not null) Array.Clear(password); }
    }
    /// <summary>Explicit user-requested mutation only; callers must never invoke this automatically from a password read/import.</summary>
    public async Task SaveAsync(SecureCredentialBinding binding, ReadOnlyMemory<char> password, SecureCredentialWriteMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding); if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var operation = Admit(ct); var blob = SecureCredentialEnvelope.Encode(binding, password.Span); var started = false;
        try
        {
            await Task.Run(() => { operation.Token.ThrowIfCancellationRequested(); started = true; _backend.Write(binding, blob, mode, operation.Token); }).ConfigureAwait(false);
        }
        catch (SecureCredentialVaultException) { throw; }
        catch (OperationCanceledException) when (started) { throw new SecureCredentialVaultException(SecureCredentialFailure.MutationOutcomeUnknown); }
        catch (OperationCanceledException) { operation.Token.ThrowIfCancellationRequested(); throw new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure); }
        catch { throw new SecureCredentialVaultException(started ? SecureCredentialFailure.MutationOutcomeUnknown : SecureCredentialFailure.NativeFailure); }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }
    /// <summary>Explicit user-requested deletion only. Missing entries return false; foreign/mismatched entries are never deleted.</summary>
    public async Task<bool> DeleteAsync(SecureCredentialBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding); using var operation = Admit(ct); var started = false;
        try { return await Task.Run(() => { operation.Token.ThrowIfCancellationRequested(); started = true; return _backend.DeleteMatching(binding, operation.Token); }).ConfigureAwait(false); }
        catch (SecureCredentialVaultException) { throw; }
        catch (OperationCanceledException) when (started) { throw new SecureCredentialVaultException(SecureCredentialFailure.MutationOutcomeUnknown); }
        catch (OperationCanceledException) { operation.Token.ThrowIfCancellationRequested(); throw new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure); }
        catch { throw new SecureCredentialVaultException(started ? SecureCredentialFailure.MutationOutcomeUnknown : SecureCredentialFailure.NativeFailure); }
    }
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    private Operation Admit(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_operations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return new(this, CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token)); }
            catch { if (--_operations == 0) _drained.TrySetResult(); throw; }
        }
    }
    private sealed class Operation(SecureCredentialVault owner, CancellationTokenSource cancellation) : IDisposable
    { public CancellationToken Token => cancellation.Token; public void Dispose() { cancellation.Dispose(); lock (owner._gate) if (--owner._operations == 0) owner._drained.TrySetResult(); } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task task;
        lock (_gate) { if (_disposal is null) { _disposed = true; owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; } task = _disposal; }
        if (owner is not null) { var cancellationFailed = false; try { _lifetime.Cancel(); } catch { cancellationFailed = true; } _ = FinishDisposeAsync(owner, cancellationFailed); } return new(task);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion, bool cancellationFailed)
    { try { Task drained; lock (_gate) drained = _drained.Task; await drained.ConfigureAwait(false); _lifetime.Dispose(); if (!cancellationFailed) completion.TrySetResult(); else completion.TrySetException(new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure)); } catch { completion.TrySetException(new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure)); } }
    private sealed class UnsupportedBackend : ISecureCredentialVaultBackend
    {
        public SecureCredentialCapability Probe(CancellationToken ct) => SecureCredentialCapability.UnsupportedPlatform;
        public byte[] ReadOwned(SecureCredentialBinding binding, CancellationToken ct) => throw new SecureCredentialVaultException(SecureCredentialFailure.UnsupportedPlatform);
        public void Write(SecureCredentialBinding binding, byte[] blob, SecureCredentialWriteMode mode, CancellationToken ct) => throw new SecureCredentialVaultException(SecureCredentialFailure.UnsupportedPlatform);
        public bool DeleteMatching(SecureCredentialBinding binding, CancellationToken ct) => throw new SecureCredentialVaultException(SecureCredentialFailure.UnsupportedPlatform);
    }
}

/// <summary>Read-only adapter. It never stores, migrates, unlocks, prompts or silently falls back to another password source.</summary>
public sealed class VaultSecurePasswordSource : ISecurePasswordSource
{
    private readonly SecureCredentialVault _vault;
    public VaultSecurePasswordSource(SecureCredentialVault vault, SecureCredentialBinding binding)
    { _vault = vault ?? throw new ArgumentNullException(nameof(vault)); Binding = binding ?? throw new ArgumentNullException(nameof(binding)); }
    [JsonIgnore] public SecureCredentialBinding Binding { get; }
    public ValueTask<char[]> ReadOwnedAsync(CancellationToken token) => _vault.ReadOwnedAsync(Binding, token);
    public override string ToString() => "[protected OS password source]";
}
