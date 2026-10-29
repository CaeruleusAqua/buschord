using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.App.Services;

public sealed record CredentialSelectionSnapshot(bool Busy, bool Verified, bool UsesStored,
    SecureCredentialCapability? Capability, string Status, UnverifiedKeyringSelection? Candidates, ProtectedTunnelChoice? Choice);

/// <summary>Explicit offline choices. Unverified selectors never authorize a connection or OS mutation.</summary>
public sealed class CredentialSelectionSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly WorkbenchCredentialVaultOwner _owner;
    private readonly string? _scope;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private string _path = "", _project = "", _status = "Unverified";
    private long _generation;
    private bool _busy, _verified;
    private SecureCredentialCapability? _capability;
    private UnverifiedKeyringSelection? _candidates;
    private ProtectedTunnelChoice? _choice;
    private ISecurePasswordSource? _passwordSource;
    public event Action? Changed;
    public CredentialSelectionSession(WorkbenchCredentialVaultOwner owner, string? stableProjectScope)
    { _owner = owner; _scope = stableProjectScope; }
    public bool HasStableScope => _scope is not null;
    public static bool MatchesStoredBinding(string? scope, SecureKeyringReference? reference, ISecurePasswordSource? source)
    {
        if (source is not VaultSecurePasswordSource stored) return true;
        if (scope is null || reference is null) return false;
        try { return SecureCredentialBinding.Create(SecureCredentialNamespace.Production, scope, reference).Id == stored.Binding.Id; }
        catch (SecureCredentialVaultException) { return false; }
    }
    public Task OperationCompletion { get { lock (_gate) return _operation; } }
    public CredentialSelectionSnapshot Snapshot { get { lock (_gate) return new(_busy, _verified, _passwordSource is VaultSecurePasswordSource, _capability, _status, _candidates, _choice); } }
    [JsonIgnore] public ISecurePasswordSource PasswordSource { get { lock (_gate) return _verified && _passwordSource is not null ? _passwordSource : throw new InvalidOperationException("No verified password selection."); } }
    [JsonIgnore] public SecureKeyringReference? VerifiedReference { get { lock (_gate) return _verified ? Reference() : null; } }
    public void SetSource(string path, string expectedProject)
    {
        lock (_gate)
        {
            if (_disposal is not null) return; if (_path == path && _project == expectedProject) return;
            _path = path; _project = expectedProject; _generation++; _candidates = null; _choice = null; _verified = false; _passwordSource = null; _status = "Unverified";
        }
        Changed?.Invoke();
    }
    public void Select(ProtectedTunnelChoice? choice)
    {
        lock (_gate)
        {
            if (_disposal is not null || Equals(_choice, choice)) return;
            if (choice is not null && _candidates?.Interfaces.Contains(choice) != true) throw new InvalidOperationException("Unknown candidate.");
            _choice = choice; _generation++; _verified = false; _passwordSource = null; _status = "Unverified";
        }
        Changed?.Invoke();
    }
    public Task ReadCandidatesAsync() => Start(async (generation, ct) =>
    {
        string path, project; lock (_gate) { path = _path; project = _project; }
        var candidate = await ProtectedKeyringSelectionCandidates.ReadAsync(path, project, ct).ConfigureAwait(false);
        lock (_gate) { Current(generation, ct); _candidates = candidate; _choice = candidate.Interfaces.FirstOrDefault(); _verified = false; _passwordSource = null; _status = "Unverified"; }
    });
    public Task ProbeAsync() => Start(async (generation, ct) =>
    {
        RequireScope(); var capability = await _owner.Vault.ProbeAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            Current(generation, ct); _capability = capability; _status = "Capability" + capability;
            if (capability != SecureCredentialCapability.Ready && _passwordSource is VaultSecurePasswordSource) { _verified = false; _passwordSource = null; }
        }
    });
    public Task VerifyEphemeralAsync(ISecurePasswordSource prompt) => Start(async (generation, ct) =>
    {
        var reference = await EnsureReferenceAsync(generation, ct).ConfigureAwait(false);
        await VerifyAsync(reference, prompt, generation, ct).ConfigureAwait(false);
        lock (_gate) { Current(generation, ct); _verified = true; _passwordSource = prompt; _status = "EphemeralSelected"; }
    });
    public Task UseStoredAsync() => Start(async (generation, ct) =>
    {
        RequireReady(); var reference = await EnsureReferenceAsync(generation, ct).ConfigureAwait(false); var source = new VaultSecurePasswordSource(_owner.Vault, Binding(reference));
        // Refuse a stale file before the OS password is even looked up.
        await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct);
        char[] owned = [];
        try
        {
            // Read directly here so fixed Locked/NotFound/Unavailable vault codes remain visible in this UI.
            owned = await source.ReadOwnedAsync(ct).ConfigureAwait(false); CurrentLocked(generation, ct);
            await VerifyAsync(reference, new BorrowedPassword(owned), generation, ct).ConfigureAwait(false);
        }
        finally { Array.Clear(owned); }
        lock (_gate) { Current(generation, ct); _verified = true; _passwordSource = source; _status = "StoredSelected"; }
    });
    public Task SaveAsync(ISecurePasswordSource prompt, SecureCredentialWriteMode mode, ISecurePasswordSource? futureEphemeralSource = null) => Start(async (generation, ct) =>
    {
        RequireReady(); var reference = await EnsureReferenceAsync(generation, ct).ConfigureAwait(false); char[] password = []; var mutationStarted = false;
        try
        {
            password = await prompt.ReadOwnedAsync(ct).ConfigureAwait(false); CurrentLocked(generation, ct);
            await VerifyAsync(reference, new BorrowedPassword(password), generation, ct).ConfigureAwait(false);
            await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct);
            mutationStarted = true; await _owner.Vault.SaveAsync(Binding(reference), password, mode, ct).ConfigureAwait(false);
            try { await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct); }
            catch { throw new SecureCredentialVaultException(SecureCredentialFailure.MutationOutcomeUnknown); }
            lock (_gate) { Current(generation, ct); _verified = true; _passwordSource ??= futureEphemeralSource ?? prompt; _status = mode == SecureCredentialWriteMode.CreateNew ? "Saved" : "Replaced"; }
        }
        catch (OperationCanceledException) when (mutationStarted) { throw new SecureCredentialVaultException(SecureCredentialFailure.MutationOutcomeUnknown); }
        finally { Array.Clear(password); }
    });
    public Task DeleteAsync() => Start(async (generation, ct) =>
    {
        RequireReady(); var reference = await EnsureReferenceAsync(generation, ct).ConfigureAwait(false);
        await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct);
        var deleted = await _owner.Vault.DeleteAsync(Binding(reference), ct).ConfigureAwait(false);
        try { await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct); }
        catch { throw new SecureCredentialVaultException(SecureCredentialFailure.MutationOutcomeUnknown); }
        lock (_gate) { Current(generation, ct); _verified = false; _passwordSource = null; _status = deleted ? "Deleted" : "VaultNotFound"; }
    });
    private async Task<SecureKeyringReference> EnsureReferenceAsync(long generation, CancellationToken ct)
    {
        string path, project; bool read; lock (_gate) { Current(generation, ct); path = _path; project = _project; read = _candidates is null; }
        if (read)
        {
            var candidates = await ProtectedKeyringSelectionCandidates.ReadAsync(path, project, ct).ConfigureAwait(false);
            lock (_gate) { Current(generation, ct); _candidates = candidates; _choice = candidates.Interfaces.FirstOrDefault(); }
        }
        lock (_gate) { Current(generation, ct); return Reference() ?? throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch); }
    }
    private async Task VerifyAsync(SecureKeyringReference reference, ISecurePasswordSource password, long generation, CancellationToken ct)
    {
        await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct);
        var summary = await ProtectedKeyringProfileReader.ReadAsync(reference.Path, reference.ExpectedProjectLabel, password, ct).ConfigureAwait(false);
        if (summary.SourceFingerprint != reference.ExpectedSourceFingerprint || !summary.Interfaces.Contains(new(reference.HostAddress, reference.UserId, reference.TunnelAddress)))
            throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
        await ProtectedKeyringSelectionCandidates.VerifyCurrentSourceAsync(reference, ct).ConfigureAwait(false); CurrentLocked(generation, ct);
    }
    private SecureKeyringReference? Reference() => _candidates is { } candidates && _choice is { } choice
        ? new(_path, candidates.ProjectLabel, choice.Host, choice.UserId, choice.TunnelAddress, expectedSourceFingerprint: candidates.SourceFingerprint) : null;
    private SecureCredentialBinding Binding(SecureKeyringReference reference) { RequireScope(); return SecureCredentialBinding.Create(SecureCredentialNamespace.Production, _scope!, reference); }
    private void RequireScope() { if (_scope is null) throw new InvalidOperationException("ProjectScopeUnavailable"); }
    private void RequireReady() { RequireScope(); lock (_gate) if (_capability != SecureCredentialCapability.Ready) throw new SecureCredentialVaultException(_capability == SecureCredentialCapability.Locked ? SecureCredentialFailure.Locked : SecureCredentialFailure.Unavailable); }
    private void CurrentLocked(long generation, CancellationToken ct) { lock (_gate) Current(generation, ct); }
    private void Current(long generation, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (_generation != generation) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch); }
    private Task Start(Func<long, CancellationToken, Task> body)
    {
        TaskCompletionSource completion; long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this); if (_busy) throw new InvalidOperationException("Credential operation already running.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task; _busy = true; generation = _generation;
        }
        // Publish ownership before notifications can reenter Close/Dispose.
        Notify(); _ = RunAsync(body, generation, completion); return completion.Task;
    }
    private async Task RunAsync(Func<long, CancellationToken, Task> body, long generation, TaskCompletionSource completion)
    {
        try { _lifetime.Token.ThrowIfCancellationRequested(); await body(generation, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Fail("Cancelled"); }
        catch (SecureCredentialVaultException error)
        {
            lock (_gate)
            {
                if (error.Failure == SecureCredentialFailure.Locked) _capability = SecureCredentialCapability.Locked;
                else if (error.Failure == SecureCredentialFailure.Unavailable) _capability = SecureCredentialCapability.Unavailable;
                else if (error.Failure == SecureCredentialFailure.UnsupportedPlatform) _capability = SecureCredentialCapability.UnsupportedPlatform;
            }
            Fail("Vault" + error.Failure);
        }
        catch (KnxConnectionException error) { Fail(error.Failure.ToString()); }
        catch (InvalidOperationException error) when (error.Message == "ProjectScopeUnavailable") { Fail("ProjectScopeUnavailable"); }
        catch { Fail("CredentialSourceUnavailable"); }
        finally { lock (_gate) _busy = false; Notify(); completion.TrySetResult(); }
    }
    private void Notify() { try { Changed?.Invoke(); } catch { /* An observer cannot strand operation ownership or expose raw exceptions. */ } }
    private void Fail(string status) { lock (_gate) { _verified = false; _passwordSource = null; _status = status; } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task result;
        lock (_gate) { if (_disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; } result = _disposal; }
        if (completion is not null) { var failed = false; try { _lifetime.Cancel(); } catch { failed = true; } _ = FinishDisposeAsync(completion, failed); } return new(result);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion, bool failed)
    {
        try { await OperationCompletion.ConfigureAwait(false); _lifetime.Dispose(); if (failed) completion.TrySetException(new IOException("Credential input cleanup failed.")); else completion.TrySetResult(); }
        catch { completion.TrySetException(new IOException("Credential input cleanup failed.")); }
    }
    private sealed class BorrowedPassword(char[] value) : ISecurePasswordSource
    { public ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(value.ToArray()); } }
    public override string ToString() => "[explicit credential selection session]";
}
