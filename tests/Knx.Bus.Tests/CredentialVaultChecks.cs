using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Knx;
using Knx.Bus.Connections;
using Knx.Bus.Security;

internal static class CredentialVaultChecks
{
    private static readonly SecureCredentialNamespace Space = SecureCredentialNamespace.Acceptance(Guid.NewGuid());
    private static readonly char[] Password = "BusChord synthetic vault ä🔐\0value".ToCharArray();
    internal static SecureCredentialBinding Binding(string scope = "Synthetic project scope", string hash = "A", byte user = 2, string? path = null)
        => SecureCredentialBinding.Create(Space, scope, new(path ?? Path.Combine(Path.GetTempPath(), "synthetic-secret-reference.knxkeys"),
            "Synthetic project label", new(1, 1, 0), user, new(1, 1, 240), expectedSourceFingerprint: new(hash[0], 64)));
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reject(Func<Task> action, SecureCredentialFailure expected)
    { try { await action(); } catch (SecureCredentialVaultException ex) when (ex.Failure == expected) { return; } throw new Exception("Expected " + expected); }
    private static void Reject(Action action, SecureCredentialFailure expected)
    { try { action(); } catch (SecureCredentialVaultException ex) when (ex.Failure == expected) { return; } throw new Exception("Expected " + expected); }
    private static async Task Cancelled(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation."); }
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("exact project/path/fingerprint/selector binding and safe JSON", () => Sync(Bindings)),
            ("owned binary Unicode envelope and malformed-input bounds", () => Sync(Envelopes)),
            ("read-only password source never saves, migrates or falls back", SourceAsync),
            ("save snapshots callers and clears borrowed native buffers", SnapshotsAsync),
            ("failed/cancelled reads clear every owned password/blob copy", ReadFailureAsync),
            ("capability and errors expose fixed codes only", RedactionAsync),
            ("foreign cancellation exceptions cannot leak native details", ForeignCancellationAsync),
            ("dispose joins a held read before releasing a cancelled result", DrainAsync),
            ("throwing cancellation callbacks cannot bypass drain and secret cleanup", ThrowingCancellationAsync),
            ("capability cancellation is observed after a held backend returns", ProbeCancellationAsync),
            ("uncertain mutation outcome never claims no write occurred", UncertainAsync),
            ("password limits and explicit missing-entry replacement are enforced", ExplicitLimitsAsync),
            ("Windows fake API: user-local Generic credentials and targeted ownership", WindowsAsync),
            ("Windows fake API: native buffers clear even on malformed records", WindowsMalformedAsync),
            ("Windows fake API: concurrent CreateNew cannot overwrite", WindowsConcurrentAsync),
            ("Linux fake service: locked/unavailable/duplicate results never unlock", LinuxGatesAsync),
            ("GNOME MIME normalization never replaces binary binding validation", () => Sync(LinuxMime)),
            ("Linux fake service: exact attributes/envelope bind replacement and delete", LinuxBindingAsync),
            ("Linux fake service: cooperative concurrent creates never overwrite", LinuxConcurrentAsync),
            ("Linux fake service: cleanup failures clear an already obtained owned blob", LinuxCleanupAsync),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS CredentialVault " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL CredentialVault " + name + ": " + error); }
        }
        return failures;
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Bindings()
    {
        var binding = Binding(); Require(binding.Id == Binding().Id && binding.TargetName.StartsWith("BusChord.Acceptance.", StringComparison.Ordinal), "Binding changed or escaped its test namespace.");
        Require(new[] { Binding("Another scope"), Binding(hash: "B"), Binding(user: 3), Binding(path: Path.Combine(Path.GetTempPath(), "another.knxkeys")) }.All(other => other.Id != binding.Id), "Changed selectors reused a credential.");
        var reference = new SecureKeyringReference(Path.Combine(Path.GetTempPath(), "unpinned.knxkeys"), "Project", new(1, 1, 0), 2, new(1, 1, 240));
        Reject(() => SecureCredentialBinding.Create(Space, "Project scope", reference), SecureCredentialFailure.InvalidBinding);
        var json = JsonSerializer.Serialize(binding) + binding; Require(!json.Contains("synthetic-secret-reference") && !json.Contains("Synthetic project") && !json.Contains("BusChord.Acceptance"), "A profile serialized its private selection.");
    }
    private static void Envelopes()
    {
        var binding = Binding(); var blob = SecureCredentialEnvelope.Encode(binding, Password); var chars = SecureCredentialEnvelope.Decode(binding, blob);
        Require(chars.SequenceEqual(Password) && !ReferenceEquals(chars, Password), "Unicode or caller ownership changed."); Array.Clear(chars);
        Reject(() => SecureCredentialEnvelope.Decode(Binding(hash: "B"), blob), SecureCredentialFailure.BindingMismatch);
        for (var size = 0; size <= SecureCredentialEnvelope.HeaderLength; size++) Reject(() => SecureCredentialEnvelope.Decode(binding, new byte[size]), SecureCredentialFailure.BindingMismatch);
        var malformed = blob.ToArray(); malformed[0] ^= 1; Reject(() => SecureCredentialEnvelope.Decode(binding, malformed), SecureCredentialFailure.BindingMismatch);
        malformed = blob.ToArray(); malformed[^1] = 0xFF; Reject(() => SecureCredentialEnvelope.Decode(binding, malformed), SecureCredentialFailure.InvalidSecret);
        Reject(() => SecureCredentialEnvelope.Encode(binding, []), SecureCredentialFailure.InvalidSecret);
        Reject(() => SecureCredentialEnvelope.Encode(binding, new string('a', 1025)), SecureCredentialFailure.InvalidSecret);
        Reject(() => SecureCredentialEnvelope.Encode(binding, new char[] { '\uD800' }), SecureCredentialFailure.InvalidSecret);
        CryptographicOperations.ZeroMemory(blob); CryptographicOperations.ZeroMemory(malformed);
    }
    private static async Task SourceAsync()
    {
        var backend = new MemoryBackend(); await using var vault = new SecureCredentialVault(backend); var binding = Binding(); var source = new VaultSecurePasswordSource(vault, binding);
        await Reject(() => source.ReadOwnedAsync(default).AsTask(), SecureCredentialFailure.NotFound); Require(backend.Writes == 0 && backend.Deletes == 0, "Read attempted persistence.");
        await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew); var owned = await source.ReadOwnedAsync(default);
        Require(owned.SequenceEqual(Password) && backend.LastRead!.All(value => value == 0), "Source did not clear its temporary blob."); Array.Clear(owned);
        await Reject(() => vault.ReadOwnedAsync(Binding(hash: "B")).AsTask(), SecureCredentialFailure.NotFound);
        Require(!JsonSerializer.Serialize(source).Contains(binding.Id) && !source.ToString().Contains("value"), "Password source leaked a reference/value.");
        Require(await vault.DeleteAsync(binding) && !await vault.DeleteAsync(binding), "Explicit delete contract differs.");
    }
    private static async Task SnapshotsAsync()
    {
        var backend = new MemoryBackend { HoldWrite = true }; await using var vault = new SecureCredentialVault(backend); var binding = Binding(); var caller = Password.ToArray();
        var saving = vault.SaveAsync(binding, caller, SecureCredentialWriteMode.CreateNew); await backend.Started.Task; Array.Clear(caller); backend.Release.TrySetResult(); await saving;
        Require(backend.Borrowed!.All(value => value == 0), "Borrowed write blob survived completion."); var actual = await vault.ReadOwnedAsync(binding);
        Require(actual.SequenceEqual(Password), "Caller mutation changed an awaited save."); Array.Clear(actual);
    }
    private static async Task ReadFailureAsync()
    {
        var backend = new MemoryBackend(); await using var vault = new SecureCredentialVault(backend); var binding = Binding(); await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew);
        backend.WrongEnvelope = true; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.BindingMismatch); Require(backend.LastRead!.All(value => value == 0), "Rejected blob was retained.");
        backend.WrongEnvelope = false; backend.CancelBeforeReadReturn = new(); await Cancelled(() => vault.ReadOwnedAsync(binding, backend.CancelBeforeReadReturn.Token).AsTask());
        Require(backend.LastRead!.All(value => value == 0), "Cancelled returned blob was retained.");
    }
    private static async Task RedactionAsync()
    {
        var backend = new MemoryBackend { Error = new IOException("SyntheticSecretValue PRIVATE_PATH PRIVATE_ENV") }; await using var vault = new SecureCredentialVault(backend);
        Require(await vault.ProbeAsync() == SecureCredentialCapability.Unavailable, "Probe leaked an exception or claimed ready.");
        await Reject(() => vault.ReadOwnedAsync(Binding()).AsTask(), SecureCredentialFailure.NativeFailure);
        try { await vault.ReadOwnedAsync(Binding()); } catch (SecureCredentialVaultException error) { Require(error.InnerException is null && !error.ToString().Contains("SyntheticSecretValue") && !error.Message.Contains("PRIVATE"), "Native error was not redacted."); }
    }
    private static async Task ForeignCancellationAsync()
    {
        var backend = new MemoryBackend { Error = new OperationCanceledException("SyntheticSecretValue PRIVATE_PATH PRIVATE_ENV") }; await using var vault = new SecureCredentialVault(backend);
        Require(await vault.ProbeAsync() == SecureCredentialCapability.Unavailable, "A foreign cancellation bypassed capability redaction.");
        await Reject(() => vault.ReadOwnedAsync(Binding()).AsTask(), SecureCredentialFailure.NativeFailure);
    }
    private static async Task DrainAsync()
    {
        var backend = new MemoryBackend { HoldRead = true }; var vault = new SecureCredentialVault(backend); var binding = Binding(); await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew);
        var reading = vault.ReadOwnedAsync(binding).AsTask(); await backend.Started.Task; var first = vault.DisposeAsync().AsTask(); var second = vault.DisposeAsync().AsTask();
        Require(ReferenceEquals(first, second) && !first.IsCompleted && !reading.IsCompleted, "Dispose did not retain its active read."); backend.Release.TrySetResult(); await Cancelled(() => reading); await Task.WhenAll(first, second);
        Require(backend.LastRead!.All(value => value == 0), "Late cancelled plaintext was not cleared.");
    }
    private static async Task ThrowingCancellationAsync()
    {
        var backend = new MemoryBackend { HoldRead = true, ThrowOnCancellation = true }; var vault = new SecureCredentialVault(backend); var binding = Binding(); await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew);
        var reading = vault.ReadOwnedAsync(binding).AsTask(); await backend.Started.Task; var first = vault.DisposeAsync().AsTask(); var second = vault.DisposeAsync().AsTask();
        Require(ReferenceEquals(first, second) && !first.IsCompleted && !reading.IsCompleted, "A throwing callback bypassed the shared drain.");
        backend.Release.TrySetResult(); await Cancelled(() => reading); await Reject(() => first, SecureCredentialFailure.NativeFailure); await Reject(() => second, SecureCredentialFailure.NativeFailure);
        Require(backend.LastRead!.All(value => value == 0), "Callback failure retained the returned native blob.");
    }
    private static async Task ProbeCancellationAsync()
    {
        var backend = new MemoryBackend { HoldProbe = true }; await using var vault = new SecureCredentialVault(backend); using var cancellation = new CancellationTokenSource();
        var probing = vault.ProbeAsync(cancellation.Token); await backend.Started.Task; cancellation.Cancel(); backend.Release.TrySetResult(); await Cancelled(() => probing);
        Require(backend.Writes == 0 && backend.Deletes == 0, "A capability probe performed a mutation.");
    }
    private static async Task UncertainAsync()
    {
        var backend = new MemoryBackend { ThrowAfterWrite = true }; await using var vault = new SecureCredentialVault(backend); var binding = Binding();
        await Reject(() => vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew), SecureCredentialFailure.MutationOutcomeUnknown);
        Require(backend.Values.Count == 1 && backend.Borrowed!.All(value => value == 0), "Uncertain write cleanup or outcome differed.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var writes = backend.Writes; await Cancelled(() => vault.SaveAsync(Binding(hash: "B"), Password, SecureCredentialWriteMode.CreateNew, cancelled.Token)); Require(writes == backend.Writes, "A cancelled queued request performed a mutation.");
    }
    private static async Task ExplicitLimitsAsync()
    {
        using var api = new WindowsFake(); await using var windows = new SecureCredentialVault(new WindowsCredentialVaultBackend(api)); var binding = Binding();
        await Reject(() => windows.SaveAsync(binding, Password, SecureCredentialWriteMode.ReplaceMatching), SecureCredentialFailure.NotFound); Require(api.Writes == 0, "Replace implicitly created a credential.");
        var fake = new LinuxFake(); await using var linux = new SecureCredentialVault(new LinuxSecretServiceVaultBackend(_ => fake));
        await Reject(() => linux.SaveAsync(binding, Password, SecureCredentialWriteMode.ReplaceMatching), SecureCredentialFailure.NotFound); Require(fake.Creates == 0, "Linux replacement implicitly created an item.");
        var maximum = Enumerable.Repeat('a', SecureCredentialEnvelope.MaximumPasswordUtf8Bytes).ToArray();
        await windows.SaveAsync(binding, maximum, SecureCredentialWriteMode.CreateNew); var read = await windows.ReadOwnedAsync(binding); Require(read.SequenceEqual(maximum), "Maximum password changed."); Array.Clear(read); Array.Clear(maximum);
        await Reject(() => windows.SaveAsync(binding, Enumerable.Repeat('\u00E4', 513).ToArray(), SecureCredentialWriteMode.ReplaceMatching), SecureCredentialFailure.InvalidSecret);
        var invalidModeRejected = false; try { await windows.SaveAsync(binding, Password, (SecureCredentialWriteMode)99); } catch (ArgumentOutOfRangeException) { invalidModeRejected = true; } Require(invalidModeRejected && api.Writes == 1, "An invalid mode reached native persistence.");
    }
    private static async Task WindowsAsync()
    {
        using var api = new WindowsFake(); await using var vault = new SecureCredentialVault(new WindowsCredentialVaultBackend(api)); var binding = Binding();
        Require(await vault.ProbeAsync() == SecureCredentialCapability.Ready && api.Reads == 0, "Capability enumerated/read credentials.");
        await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew); Require(api.LastWrite.Type == 1 && api.LastWrite.Persist == 2 && api.LastWrite.Flags == 0 && api.LastWrite.AttributeCount == 0, "Not user-local Generic credentials.");
        var actual = await vault.ReadOwnedAsync(binding); Require(actual.SequenceEqual(Password) && api.LastFreeWasZero, "Native read ownership/zeroing changed."); Array.Clear(actual);
        await Reject(() => vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew), SecureCredentialFailure.AlreadyExists);
        await vault.SaveAsync(binding, new char[] { 'n', 'e', 'w' }, SecureCredentialWriteMode.ReplaceMatching); Require(await vault.DeleteAsync(binding) && api.Values.Count == 0, "Explicit replacement/deletion failed.");
    }
    private static async Task WindowsMalformedAsync()
    {
        using var api = new WindowsFake(); await using var vault = new SecureCredentialVault(new WindowsCredentialVaultBackend(api)); var binding = Binding(); await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew);
        api.ShortComment = true; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.BindingMismatch); Require(api.LastFreeWasZero, "Malformed native record was not cleared/freed.");
        api.ShortComment = false; api.Values[binding.TargetName][8] ^= 1; await Reject(() => vault.DeleteAsync(binding), SecureCredentialFailure.BindingMismatch); Require(api.Values.Count == 1, "A mismatched native record was deleted.");
        api.ReadError = 5; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.AccessDenied);
    }
    private static async Task WindowsConcurrentAsync()
    {
        using var api = new WindowsFake(); await using var one = new SecureCredentialVault(new WindowsCredentialVaultBackend(api)); await using var two = new SecureCredentialVault(new WindowsCredentialVaultBackend(api)); var binding = Binding(); int completed = 0, refused = 0;
        async Task Save(SecureCredentialVault vault) { try { await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew); Interlocked.Increment(ref completed); } catch (SecureCredentialVaultException error) when (error.Failure == SecureCredentialFailure.AlreadyExists) { Interlocked.Increment(ref refused); } }
        await Task.WhenAll(Save(one), Save(two)); Require(completed == 1 && refused == 1 && api.Writes == 1, "Concurrent creates overwrote a credential.");
    }
    private static async Task LinuxGatesAsync()
    {
        var fake = new LinuxFake(); await using var vault = new SecureCredentialVault(new LinuxSecretServiceVaultBackend(_ => fake)); var binding = Binding();
        fake.Locked = true; Require(await vault.ProbeAsync() == SecureCredentialCapability.Locked, "Locked capability was lost."); await Reject(() => vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew), SecureCredentialFailure.Locked); Require(fake.Creates == 0, "Locked store was written.");
        fake.Locked = false; fake.Duplicate = true; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.Ambiguous); Require(fake.Reads == 0, "Duplicate matches returned a secret.");
        fake.Duplicate = false; fake.EntryLocked = true; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.Locked); Require(fake.Reads == 0, "Locked item returned a secret.");
    }
    private static void LinuxMime()
    {
        Require(LibsecretCredentialSession.SupportedContentType("application/octet-stream") && LibsecretCredentialSession.SupportedContentType("text/plain"), "A documented GNOME binary-value return profile was rejected.");
        Require(!LibsecretCredentialSession.SupportedContentType("text/html") && !LibsecretCredentialSession.SupportedContentType(""), "An unrelated MIME profile was accepted.");
        Reject(() => SecureCredentialEnvelope.Decode(Binding(), "ordinary foreign plain text"u8), SecureCredentialFailure.BindingMismatch);
    }
    private static async Task LinuxBindingAsync()
    {
        var fake = new LinuxFake(); await using var vault = new SecureCredentialVault(new LinuxSecretServiceVaultBackend(_ => fake)); var binding = Binding(); await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew);
        fake.BadAttributes = true; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.BindingMismatch); Require(fake.Reads == 0, "Wrong attributes were read.");
        fake.BadAttributes = false; var actual = await vault.ReadOwnedAsync(binding); Require(actual.SequenceEqual(Password), "Linux owned read differed."); Array.Clear(actual);
        fake.Value![8] ^= 1; await Reject(() => vault.DeleteAsync(binding), SecureCredentialFailure.BindingMismatch); Require(fake.Deletes == 0, "A mismatched Linux item was deleted.");
    }
    private static async Task LinuxConcurrentAsync()
    {
        var fake = new LinuxFake(); await using var one = new SecureCredentialVault(new LinuxSecretServiceVaultBackend(_ => fake)); await using var two = new SecureCredentialVault(new LinuxSecretServiceVaultBackend(_ => fake)); var binding = Binding(); int completed = 0, refused = 0;
        async Task Save(SecureCredentialVault vault) { try { await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew); Interlocked.Increment(ref completed); } catch (SecureCredentialVaultException error) when (error.Failure == SecureCredentialFailure.AlreadyExists) { Interlocked.Increment(ref refused); } }
        await Task.WhenAll(Save(one), Save(two)); Require(completed == 1 && refused == 1 && fake.Creates == 1, "Concurrent Secret-Service creates overwrote an item.");
    }
    private static async Task LinuxCleanupAsync()
    {
        var fake = new LinuxFake(); await using var vault = new SecureCredentialVault(new LinuxSecretServiceVaultBackend(_ => fake)); var binding = Binding(); await vault.SaveAsync(binding, Password, SecureCredentialWriteMode.CreateNew);
        fake.ThrowDispose = true; await Reject(() => vault.ReadOwnedAsync(binding).AsTask(), SecureCredentialFailure.NativeFailure);
        Require(fake.LastRead!.All(value => value == 0), "Native-session cleanup failure leaked an obtained owned blob.");
    }
    private sealed class MemoryBackend : ISecureCredentialVaultBackend
    {
        public Dictionary<string, byte[]> Values = []; public int Writes, Deletes; public Exception? Error; public bool HoldWrite, HoldRead, HoldProbe, WrongEnvelope, ThrowAfterWrite, ThrowOnCancellation;
        public byte[]? Borrowed, LastRead; public CancellationTokenSource? CancelBeforeReadReturn; public TaskCompletionSource Started = Gate(), Release = Gate();
        public SecureCredentialCapability Probe(CancellationToken ct) { if (HoldProbe) { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); } if (Error is not null) throw Error; return SecureCredentialCapability.Ready; }
        public byte[] ReadOwned(SecureCredentialBinding binding, CancellationToken ct)
        {
            if (Error is not null) throw Error; if (!Values.TryGetValue(binding.Id, out var bytes)) throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound);
            using var registration = ThrowOnCancellation ? ct.Register(() => throw new IOException("SyntheticSecretValue callback failure")) : default;
            if (HoldRead) { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); } LastRead = bytes.ToArray(); if (WrongEnvelope) LastRead[8] ^= 1; CancelBeforeReadReturn?.Cancel(); return LastRead;
        }
        public void Write(SecureCredentialBinding binding, byte[] blob, SecureCredentialWriteMode mode, CancellationToken ct)
        {
            Borrowed = blob; if (HoldWrite) { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); } Writes++;
            if (Values.ContainsKey(binding.Id) && mode == SecureCredentialWriteMode.CreateNew) throw new SecureCredentialVaultException(SecureCredentialFailure.AlreadyExists);
            Values[binding.Id] = blob.ToArray(); if (ThrowAfterWrite) throw new IOException("SyntheticSecretValue");
        }
        public bool DeleteMatching(SecureCredentialBinding binding, CancellationToken ct) { Deletes++; if (!Values.Remove(binding.Id, out var value)) return false; CryptographicOperations.ZeroMemory(value); return true; }
    }
    private sealed class WindowsFake : IWindowsCredentialApi, IDisposable
    {
        public Dictionary<string, byte[]> Values = []; private readonly Dictionary<IntPtr, (WindowsCredentialRecord Record, List<IntPtr> Metadata)> _reads = [];
        public int Reads, Writes, ReadError; public bool LastFreeWasZero, ShortComment; public WindowsCredentialRecord LastWrite;
        public bool Available => true; public string CurrentUserId() => "BusChord.Acceptance.MockUser";
        public bool GetSessionTypes(uint[] persistence, out int error) { persistence[1] = 2; error = 0; return true; }
        public bool Read(string target, out IntPtr credential, out int error)
        {
            Reads++; credential = IntPtr.Zero; if (ReadError != 0) { error = ReadError; return false; } if (!Values.TryGetValue(target, out var blob)) { error = 1168; return false; }
            var metadata = new List<IntPtr>(); IntPtr Text(string value) { var p = Marshal.StringToHGlobalUni(value); metadata.Add(p); return p; }
            var record = new WindowsCredentialRecord { Type = 1, Persist = 2, TargetName = Text(target), UserName = Text("BusChord"), Comment = Text(ShortComment ? "x" : "BusChord protected keyring password v1"), CredentialBlobSize = (uint)blob.Length, CredentialBlob = Marshal.AllocHGlobal(blob.Length) };
            Marshal.Copy(blob, 0, record.CredentialBlob, blob.Length); credential = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsCredentialRecord>()); Marshal.StructureToPtr(record, credential, false); _reads.Add(credential, (record, metadata)); error = 0; return true;
        }
        public bool Write(ref WindowsCredentialRecord credential, out int error)
        { LastWrite = credential; var value = new byte[credential.CredentialBlobSize]; Marshal.Copy(credential.CredentialBlob, value, 0, value.Length); Values[Marshal.PtrToStringUni(credential.TargetName)!] = value; Writes++; error = 0; return true; }
        public bool Delete(string target, out int error) { error = Values.Remove(target) ? 0 : 1168; return error == 0; }
        public void Free(IntPtr credential)
        {
            var (record, metadata) = _reads[credential]; var bytes = new byte[record.CredentialBlobSize]; Marshal.Copy(record.CredentialBlob, bytes, 0, bytes.Length); LastFreeWasZero = bytes.All(value => value == 0);
            Marshal.FreeHGlobal(record.CredentialBlob); foreach (var pointer in metadata) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(credential); _reads.Remove(credential);
        }
        public void Dispose() { foreach (var pointer in _reads.Keys.ToArray()) Free(pointer); foreach (var bytes in Values.Values) CryptographicOperations.ZeroMemory(bytes); Values.Clear(); }
    }
    private sealed class LinuxFake : ILinuxSecretServiceSession
    {
        public bool Locked, EntryLocked, Duplicate, BadAttributes, ThrowDispose; public int Creates, Reads, Deletes; public byte[]? Value, LastRead;
        public bool DefaultCollectionLocked => Locked;
        public IReadOnlyList<LinuxSecretMatch> FindExact(SecureCredentialBinding binding) => Duplicate ? [new("/test/one", false), new("/test/two", false)] : EntryLocked ? [new("/test/one", true)] : Value is null ? [] : [new("/test/one", false)];
        public void VerifyExactAttributes(string path, SecureCredentialBinding binding) { if (BadAttributes) throw new SecureCredentialVaultException(SecureCredentialFailure.BindingMismatch); }
        public byte[] ReadOwned(string path) { Reads++; return LastRead = Value!.ToArray(); }
        public void Create(SecureCredentialBinding binding, byte[] bytes) { Creates++; Value = bytes.ToArray(); }
        public void Replace(string path, byte[] bytes) { if (Value is not null) CryptographicOperations.ZeroMemory(Value); Value = bytes.ToArray(); }
        public void Delete(string path) { Deletes++; if (Value is not null) CryptographicOperations.ZeroMemory(Value); Value = null; }
        public void Dispose() { if (ThrowDispose) throw new IOException("SyntheticSecretValue cleanup"); }
    }
}
