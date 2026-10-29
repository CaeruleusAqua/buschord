using System.Security.Cryptography;
using Knx;
using Knx.App.Services;
using Knx.Bus.Connections;
using Knx.Bus.Security;

internal static class CredentialSelectionChecks
{
    private const string Label = "BusChord synthetic keyring";
    public static async Task Run(Action<string, bool> check)
    {
        using var files = new PrivateFiles(); var path = files.File;
        var project = new Project { Id = "P-credentials", Name = "Synthetic", Guid = "00000000-0000-0000-0000-000000000001" };
        var scope = WorkbenchCredentialVaultOwner.ProjectScope(project, Path.Combine(files.Root, "project.knxproj"))!;
        check("credential scope is stable across reopened models and excludes display name", scope == WorkbenchCredentialVaultOwner.ProjectScope(new Project { Id = project.Id, Name = "Renamed", Guid = project.Guid }, Path.Combine(files.Root, "project.knxproj")));
        check("credential scope rejects unsaved or relative projects and separates full paths", WorkbenchCredentialVaultOwner.ProjectScope(project, "") is null && WorkbenchCredentialVaultOwner.ProjectScope(project, "relative.knxproj") is null && scope != WorkbenchCredentialVaultOwner.ProjectScope(project, Path.Combine(files.Root, "another.knxproj")));
        var backend = new MemoryVault(); var creates = 0; await using var owner = new WorkbenchCredentialVaultOwner(() => { creates++; return new(backend); });
        await using (var session = Session(owner, scope, path))
        {
            await session.ReadCandidatesAsync(); check("public candidates carry no verification or OS access", session.Snapshot.Candidates?.Interfaces.Count == 1 && !session.Snapshot.Verified && session.VerifiedReference is null && creates == 0 && backend.Reads == 0);
            var prompt = new Password("pwd"); await session.VerifyEphemeralAsync(prompt);
            check("ephemeral verification preserves owned cleanup and never saves", session.Snapshot.Verified && !session.Snapshot.UsesStored && prompt.Last!.All(c => c == 0) && backend.Writes == 0 && creates == 0);
            await session.ProbeAsync(); check("storage capability is an explicit action with no entry read", creates == 1 && backend.Probes == 1 && backend.Reads == 0 && session.Snapshot.Capability == SecureCredentialCapability.Ready);
            await session.SaveAsync(new Password("wrong"), SecureCredentialWriteMode.CreateNew); check("incorrect password cannot authorize any OS save", !session.Snapshot.Verified && session.Snapshot.Status == "KeyringRejected" && backend.Writes == 0);
            var saving = new Password("pwd"); await session.SaveAsync(saving, SecureCredentialWriteMode.CreateNew);
            check("explicit new save verifies exact source and clears password snapshots", session.Snapshot.Verified && !session.Snapshot.UsesStored && saving.Last!.All(c => c == 0) && backend.Writes == 1 && backend.Borrowed!.All(b => b == 0));
            await session.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.CreateNew); check("duplicate create requires an explicit separate replacement", session.Snapshot.Status == "VaultAlreadyExists" && backend.Writes == 1);
            await session.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.ReplaceMatching); check("explicit matching replacement is available", session.Snapshot.Status == "Replaced" && backend.Writes == 2);
        }
        await using (var reopened = Session(owner, scope, path))
        {
            await reopened.ReadCandidatesAsync(); await reopened.ProbeAsync(); await reopened.UseStoredAsync();
            var reference = reopened.VerifiedReference!; var source = reopened.PasswordSource;
            check("reopened selection verifies a saved password without any prompt", reopened.Snapshot.Verified && reopened.Snapshot.UsesStored && backend.LastRead!.All(b => b == 0));
            check("cached stored sources cannot cross project or selector binding", CredentialSelectionSession.MatchesStoredBinding(scope, reference, source) && !CredentialSelectionSession.MatchesStoredBinding(scope + "different", reference, source) && !CredentialSelectionSession.MatchesStoredBinding(null, reference, source));
            backend.Capability = SecureCredentialCapability.Locked; await reopened.ProbeAsync(); check("a later locked probe revokes the stored selection without choosing a fallback", !reopened.Snapshot.Verified && reopened.Snapshot.Capability == SecureCredentialCapability.Locked);
            backend.Capability = SecureCredentialCapability.Ready; await reopened.ProbeAsync(); await reopened.UseStoredAsync();
            var reads = backend.Reads; await File.WriteAllTextAsync(path, ProtectedKnxKeyringFixture.Xml.Replace("Synthetic vector", "Changed source")); await reopened.UseStoredAsync();
            check("source mutation before saved lookup reads no credential and cannot authorize", !reopened.Snapshot.Verified && reopened.Snapshot.Status == "CredentialBindingMismatch" && backend.Reads == reads);
        }
        await File.WriteAllTextAsync(path, ProtectedKnxKeyringFixture.Xml);
        await using (var missing = Session(owner, scope + "other", path))
        { await missing.ReadCandidatesAsync(); await missing.ProbeAsync(); await missing.UseStoredAsync(); check("missing exact binding never falls back to prompt or another entry", !missing.Snapshot.Verified && missing.Snapshot.Status == "VaultNotFound"); }
        await using (var unavailableOwner = new WorkbenchCredentialVaultOwner(() => new(new MemoryVault { Capability = SecureCredentialCapability.Locked })))
        await using (var locked = Session(unavailableOwner, scope, path))
        { await locked.ReadCandidatesAsync(); await locked.ProbeAsync(); await locked.UseStoredAsync(); check("locked storage is honest and ephemeral verification remains explicit", locked.Snapshot.Status == "VaultLocked" && !locked.Snapshot.Verified); await locked.VerifyEphemeralAsync(new Password("pwd")); check("locked OS storage does not prevent explicit ephemeral verification", locked.Snapshot.Verified && !locked.Snapshot.UsesStored); }
        await using (var unsaved = Session(owner, null, path))
        { await unsaved.ProbeAsync(); check("unsaved project cannot create a persistence scope implicitly", unsaved.Snapshot.Status == "ProjectScopeUnavailable"); }
        await using (var becameLocked = Session(owner, scope, path))
        {
            await becameLocked.ReadCandidatesAsync(); await becameLocked.ProbeAsync(); backend.ReadFailure = SecureCredentialFailure.Locked; await becameLocked.UseStoredAsync();
            check("storage locking after a successful probe invalidates its capability", becameLocked.Snapshot.Status == "VaultLocked" && becameLocked.Snapshot.Capability == SecureCredentialCapability.Locked && !becameLocked.Snapshot.Verified); backend.ReadFailure = null;
        }
        backend.AfterWrite = () => File.WriteAllText(path, ProtectedKnxKeyringFixture.Xml.Replace("Synthetic vector", "After mutation"));
        await using (var uncertain = Session(owner, scope, path))
        { await uncertain.ReadCandidatesAsync(); await uncertain.ProbeAsync(); var before = backend.Writes; await uncertain.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.ReplaceMatching); check("file changes during a committed mutation remain visibly uncertain", uncertain.Snapshot.Status == "VaultMutationOutcomeUnknown" && !uncertain.Snapshot.Verified && backend.Writes == before + 1); }
        backend.AfterWrite = null; await File.WriteAllTextAsync(path, ProtectedKnxKeyringFixture.Xml);
        await using (var deleting = Session(owner, scope, path))
        { await deleting.ReadCandidatesAsync(); await deleting.ProbeAsync(); await deleting.DeleteAsync(); check("delete is explicit and removes only the matching reference", deleting.Snapshot.Status == "Deleted" && !deleting.Snapshot.Verified); await deleting.UseStoredAsync(); check("deleted password is not recreated by a read", deleting.Snapshot.Status == "VaultNotFound"); }
        await ParserChecks(path, check);
        await CloseChecks(owner, backend, scope, path, check);
        await AdversarialChecks(scope, path, check);
    }
    private static CredentialSelectionSession Session(WorkbenchCredentialVaultOwner owner, string? scope, string path)
    { var result = new CredentialSelectionSession(owner, scope); result.SetSource(path, Label); return result; }
    private static async Task ParserChecks(string path, Action<string, bool> check)
    {
        await File.WriteAllTextAsync(path, ProtectedKnxKeyringFixture.Xml.Replace("</Keyring>", "<Interface Type='Tunneling' IndividualAddress='1.2.99'/></Keyring>"));
        check("unsecured interface metadata is skipped without hiding complete Secure candidates", (await ProtectedKeyringSelectionCandidates.ReadAsync(path, Label)).Interfaces.Count == 1);
        foreach (var value in new[] { "<!DOCTYPE Keyring [<!ENTITY bad SYSTEM 'file:///forbidden'>]><Keyring xmlns='http://knx.org/xml/keyring/1' Project='BusChord synthetic keyring'>&bad;</Keyring>", new string('x', ProtectedKnxKeyringImporter.MaximumXmlBytes + 1), "<Keyring xmlns='urn:wrong' Project='BusChord synthetic keyring'/>" })
        {
            await File.WriteAllTextAsync(path, value); var rejected = false; try { await ProtectedKeyringSelectionCandidates.ReadAsync(path, Label); } catch (KnxConnectionException) { rejected = true; } check("unverified metadata rejects DTD/entities, oversized or foreign namespace input", rejected);
        }
        await File.WriteAllTextAsync(path, ProtectedKnxKeyringFixture.Xml);
    }
    private static async Task CloseChecks(WorkbenchCredentialVaultOwner owner, MemoryVault backend, string scope, string path, Action<string, bool> check)
    {
        await using (var create = Session(owner, scope, path)) { await create.ReadCandidatesAsync(); await create.ProbeAsync(); await create.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.CreateNew); }
        var session = Session(owner, scope, path); await session.ReadCandidatesAsync(); await session.ProbeAsync(); backend.HoldRead = true;
        var usingSaved = session.UseStoredAsync(); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); var first = session.DisposeAsync().AsTask(); var second = session.DisposeAsync().AsTask();
        check("closing an active saved lookup shares its pending drain", ReferenceEquals(first, second) && !first.IsCompleted && !usingSaved.IsCompleted);
        backend.Release.TrySetResult(); await usingSaved; await first;
        check("cancelled saved lookup clears owned bytes and releases no verified source", backend.LastRead!.All(b => b == 0) && !session.Snapshot.Verified && session.Snapshot.Status == "Cancelled"); backend.HoldRead = false;
        var reentrantBackend = new MemoryVault(); await using var reentrantOwner = new WorkbenchCredentialVaultOwner(() => new(reentrantBackend)); var reentrant = Session(reentrantOwner, scope, path); Task? stopping = null;
        reentrant.Changed += () => { if (reentrant.Snapshot.Busy && stopping is null) stopping = reentrant.DisposeAsync().AsTask(); };
        await reentrant.ProbeAsync(); await stopping!; check("reentrant close sees published operation before any capability access", reentrantBackend.Probes == 0 && reentrant.Snapshot.Status == "Cancelled");
    }
    private static async Task AdversarialChecks(string scope, string path, Action<string, bool> check)
    {
        var backend = new MemoryVault(); await using var owner = new WorkbenchCredentialVaultOwner(() => new(backend));
        var unsigned = ProtectedKnxKeyringFixture.Xml.Replace("O6g27O2hyVUL9PI0O3E8zA==", "AAAAAAAAAAAAAAAAAAAAAA=="); await File.WriteAllTextAsync(path, unsigned);
        await using (var session = Session(owner, scope, path))
        {
            await session.ReadCandidatesAsync(); await session.ProbeAsync(); await session.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.CreateNew);
            check("unsigned candidate discovery never authorizes save or interface acceptance", session.Snapshot.Candidates?.Interfaces.Count == 1 && session.Snapshot.Status == "KeyringRejected" && !session.Snapshot.Verified && backend.Writes == 0);
        }
        await File.WriteAllTextAsync(path, ProtectedKnxKeyringFixture.Xml);
        await using (var seeded = Session(owner, scope, path)) { await seeded.ReadCandidatesAsync(); await seeded.ProbeAsync(); await seeded.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.CreateNew); }
        await using (var changing = Session(owner, scope, path))
        {
            await changing.ReadCandidatesAsync(); await changing.ProbeAsync(); backend.HoldRead = true; var pending = changing.UseStoredAsync(); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); changing.Select(null); backend.Release.TrySetResult(); await pending;
            check("binding changes while a lookup is pending cannot publish its password source", changing.Snapshot.Status == "CredentialBindingMismatch" && !changing.Snapshot.Verified && backend.LastRead!.All(value => value == 0)); backend.HoldRead = false;
        }
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.AfterWrite = () => { committed.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        var saving = Session(owner, scope, path); await saving.ReadCandidatesAsync(); await saving.ProbeAsync(); var input = new Password("pwd"); var save = saving.SaveAsync(input, SecureCredentialWriteMode.ReplaceMatching); await committed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closed = saving.DisposeAsync().AsTask(); check("close never abandons an admitted storage mutation", !save.IsCompleted && !closed.IsCompleted); release.TrySetResult(); await save; await closed;
        check("cancelled committed mutation remains unknown and clears every password copy", saving.Snapshot.Status == "VaultMutationOutcomeUnknown" && input.Last!.All(c => c == 0) && backend.Borrowed!.All(value => value == 0)); backend.AfterWrite = null;
        var drainBackend = new MemoryVault(); var drainOwner = new WorkbenchCredentialVaultOwner(() => new(drainBackend)); ISecurePasswordSource selected;
        await using (var selection = Session(drainOwner, scope, path)) { await selection.ReadCandidatesAsync(); await selection.ProbeAsync(); await selection.SaveAsync(new Password("pwd"), SecureCredentialWriteMode.CreateNew); await selection.UseStoredAsync(); selected = selection.PasswordSource; }
        drainBackend.HoldRead = true; var read = selected.ReadOwnedAsync(default).AsTask(); await drainBackend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); var first = drainOwner.DisposeAsync().AsTask(); var second = drainOwner.DisposeAsync().AsTask(); var rejected = false;
        try { _ = drainOwner.Vault; } catch (ObjectDisposedException) { rejected = true; }
        check("shared vault owner publishes one disposal before callbacks and holds active readers", ReferenceEquals(first, second) && !first.IsCompleted && rejected);
        drainBackend.Release.TrySetResult(); try { var unexpected = await read; Array.Clear(unexpected); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { } await first;
        check("vault owner joins delayed source read and clears its native snapshot", drainBackend.LastRead!.All(value => value == 0));
    }
    internal sealed class Password(string value) : ISecurePasswordSource
    { public char[]? Last; public ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Last = value.ToCharArray()); } }
    internal sealed class MemoryVault : ISecureCredentialVaultBackend
    {
        public Dictionary<string, byte[]> Values = []; public int Probes, Reads, Writes, Deletes; public byte[]? LastRead, Borrowed;
        public SecureCredentialCapability Capability = SecureCredentialCapability.Ready; public Action? AfterWrite; public bool HoldRead; public SecureCredentialFailure? ReadFailure;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SecureCredentialCapability Probe(CancellationToken ct) { Probes++; return Capability; }
        public byte[] ReadOwned(SecureCredentialBinding binding, CancellationToken ct)
        { Reads++; if (ReadFailure is { } failure) throw new SecureCredentialVaultException(failure); if (!Values.TryGetValue(binding.Id, out var value)) throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound); if (HoldRead) { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); } return LastRead = value.ToArray(); }
        public void Write(SecureCredentialBinding binding, byte[] bytes, SecureCredentialWriteMode mode, CancellationToken ct)
        { if (Values.ContainsKey(binding.Id) && mode == SecureCredentialWriteMode.CreateNew) throw new SecureCredentialVaultException(SecureCredentialFailure.AlreadyExists); if (!Values.ContainsKey(binding.Id) && mode == SecureCredentialWriteMode.ReplaceMatching) throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound); Borrowed = bytes; if (Values.Remove(binding.Id, out var previous)) CryptographicOperations.ZeroMemory(previous); Values[binding.Id] = bytes.ToArray(); Writes++; AfterWrite?.Invoke(); }
        public bool DeleteMatching(SecureCredentialBinding binding, CancellationToken ct) { Deletes++; if (!Values.Remove(binding.Id, out var previous)) return false; CryptographicOperations.ZeroMemory(previous); return true; }
    }
    private sealed class PrivateFiles : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("BusChord.Credential.Selection.").FullName;
        public string File => Path.Combine(Root, "synthetic.knxkeys");
        public PrivateFiles() => System.IO.File.WriteAllText(File, ProtectedKnxKeyringFixture.Xml);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !Path.GetFileName(full).StartsWith("BusChord.Credential.Selection.", StringComparison.Ordinal) || (System.IO.File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unexpected synthetic cleanup target.");
            Directory.Delete(full, true);
        }
    }
}
