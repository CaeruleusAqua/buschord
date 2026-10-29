using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

internal static class DataSecureChecks
{
    private static readonly byte[] Integrity = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly IndividualAddress Local = new(1, 1, 240), Sender = new(1, 1, 1), OtherSender = new(1, 1, 2);
    private static readonly GroupAddress Group = new(0x101), OtherGroup = new(0x102);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static DataSecureStateIdentity Identity(string epoch = "synthetic-v1") => new("Regression project scope", epoch, Local);
    private static string Folder() { var path = Path.Combine(Path.GetTempPath(), "buschord-data-secure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static byte[] Hex(string value) => Convert.FromHexString(value);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string text) { if (!condition) throw new Exception(text); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task CounterFailure(Func<Task> action, params DataSecureCounterFailure[] expected)
    { try { await action(); } catch (DataSecureCounterStateException ex) when (expected.Contains(ex.Failure)) { return; } throw new Exception("Expected counter-state failure: " + string.Join(", ", expected)); }
    private static CemiLDataFrame Plain(IndividualAddress? sender = null, GroupAddress? group = null, byte[]? apdu = null, bool incoming = false, byte[]? additional = null)
        => new(incoming ? CemiMessageCode.LDataInd : CemiMessageCode.LDataReq, 0xBC, 0xE0, sender ?? Sender, (group ?? Group).Raw, apdu ?? [0, 0x80, 1], additional ?? []);
    private static byte[] Secured(ulong sequence, IndividualAddress? sender = null, GroupAddress? group = null, KnxDataProtection protection = KnxDataProtection.AuthenticationAndConfidentiality, byte[]? apdu = null)
        => KnxDataSecureCodec.ProtectCemi(Plain(sender, group, apdu, true), sequence, Key, protection);
    private static DataSecureCounterBootstrap Bootstrap(ulong next = 10) => new(next, new Dictionary<IndividualAddress, ulong> { [Sender] = 1, [OtherSender] = 1 });
    private static KnxDataSecureGroupContext Context(DataSecureCounterStore store, KnxDataProtection protection = KnxDataProtection.AuthenticationAndConfidentiality)
        => new(store, Identity(), [new(Group, Key, protection, new[] { Sender })]);

    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("published group read, extended write and received-response vectors", () => Sync(PublishedVectors)),
            ("independent auth-only and auth-conf CBC/CTR vectors", () => Sync(IndependentVectors)),
            ("header, sequence, ciphertext and tag tampering fail authentication", () => Sync(Tamper)),
            ("mutable routing flags are excluded while address/TPCI/type are protected", () => Sync(AuthenticatedFields)),
            ("additional information and standard/extended boundaries survive", () => Sync(CemiBoundaries)),
            ("unsupported services, broadcast/tool access, lengths and zero sequence are refused", () => Sync(UnsupportedProfiles)),
            ("explicit provisioning persists before counters can be released", PersistenceAsync),
            ("counter allocation remains unique across concurrent store instances", ConcurrentInstancesAsync),
            ("independent processes allocate disjoint counters through the file lock", ConcurrentProcessesAsync),
            ("an actual process exit between local flush and anchor commit cannot reuse its counter", CrashedProcessAsync),
            ("missing, truncated and wrong-context state never resets automatically", InvalidStateAsync),
            ("48-bit counters exhaust without wrapping or partially allocating", ExhaustionAsync),
            ("unknown and replayed receive sequences cannot move trusted floors", ReceiverFloorsAsync),
            ("authenticated own-address observations advance the local send highwater", OwnSenderAsync),
            ("provisioning and trusted restore merge own receive floors into send highwater", OwnBootstrapAsync),
            ("unanchored damaged A/B pages require trusted synchronization", TornUnanchoredAsync),
            ("anchored inactive-page damage preserves the committed counter", TornAnchoredAsync),
            ("a monotonic anchor detects restored valid old counter files", AnchoredRollbackAsync),
            ("anchor failure after local flush releases nothing and can reconcile", AnchorGapAsync),
            ("an uncertain successful anchor commit reconciles without counter reuse", AnchorUncertainAsync),
            ("missing or conflicting anchors never permit local fallback", AnchorMismatchAsync),
            ("restore mode blocks sends and data release until all trusted floors commit", RestoreModeAsync),
            ("private paths cannot target a Git checkout or a relative path", PrivatePathsAsync),
            ("group contexts bind project, local sender, group key and sender policies", ContextBindingsAsync),
            ("group contexts refuse plaintext, auth downgrade and forged payloads", ContextAdmissionAsync),
            ("receive replay floors persist across context/process lifetimes", ContextReplayAsync),
            ("received application data waits for durable anchor commit", ContextCommitBoundaryAsync),
            ("queued caller buffers cannot change group or payload authorization", CallerMutationAsync),
            ("outgoing payload snapshots survive caller edits during counter reservation", OutgoingMutationAsync),
            ("disposing a context cancels and joins pending admission before key release", ContextDisposalAsync),
            ("state errors release no verified application payload", ContextStateFailureAsync),
        })
        {
            try { await run().WaitAsync(Deadline); Console.WriteLine("PASS DataSecure " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL DataSecure " + name + ": " + ex); }
        }
        return failures;
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void PublishedVectors()
    {
        // Public synthetic KNX/XKNX SecureTest fixtures; this is not a customer key or installation.
        var key = Hex("dfdf23a59fbb40404091d1c162087e8b");
        var read = KnxDataSecureCodec.ProtectCemi(Plain(new(5, 0, 1), new(0x400), [0, 0]), 160170101607, key, KnxDataProtection.AuthenticationAndConfidentiality);
        Require(Convert.ToHexString(read) == "1100BCE0500104000E03F11000254AE1CB67CD184AFE5744", "Published group-read vector differs.");
        var extended = KnxDataSecureCodec.ProtectCemi(Plain(new(5, 0, 1), new(0x400), [0, 0x80, 255, 0, 5]), 160170101607, key, KnxDataProtection.AuthenticationAndConfidentiality);
        Require(Convert.ToHexString(extended) == "11003CE0500104001103F11000254AE1CB67CD98E577B519BE47BB", "Published extended group-write vector differs.");
        var response = Hex("29003ce0400904001103f110002446cfef4ac085e7092ab062b44d");
        Require(KnxDataSecureCodec.TryUnprotectCemi(response, key, out var decoded, out _) && decoded!.Sequence == 155806854986 && decoded.Apdu.SequenceEqual(new byte[] { 0, 0x40, 116, 41, 41 }), "Published received group response differs.");
    }
    private static void IndependentVectors()
    {
        foreach (var (protection, size, expected) in new (KnxDataProtection, int, string)[]
        {
            (KnxDataProtection.AuthenticationAndConfidentiality,2,"1100bce0110101010e03f1100102030405061c29489b9557"),
            (KnxDataProtection.AuthenticationAndConfidentiality,4,"11003ce0110101011003f1100102030405061c29f62d2289dad3"),
            (KnxDataProtection.AuthenticationAndConfidentiality,32,"11003ce0110101012c03f1100102030405061c29f62df7430f4cf0e999ae20aadc5d5e70b8e2683d670cc680ba9d9327e4575fb11aed"),
            (KnxDataProtection.Authentication,2,"1100bce0110101010e03f100010203040506008099316edc"),
            (KnxDataProtection.Authentication,4,"11003ce0110101011003f1000102030405060080000141a25d67"),
        })
        {
            var plain = new byte[size]; plain[1] = 128; for (var i = 2; i < size; i++) plain[i] = (byte)(i - 2);
            var wire = KnxDataSecureCodec.ProtectCemi(Plain(new(1, 1, 1), Group, plain), 0x010203040506, Key, protection);
            Require(Convert.ToHexString(wire).Equals(expected, StringComparison.OrdinalIgnoreCase), "Independent CBC/CTR vector differs.");
            Require(KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out var back, out _) && back!.Protection == protection && back.Apdu.SequenceEqual(plain), "Independent vector did not authenticate.");
        }
    }
    private static void Tamper()
    {
        foreach (var protection in Enum.GetValues<KnxDataProtection>())
        {
            var wire = Secured(2, protection: protection);
            foreach (var index in Enumerable.Range(4, 4).Concat(Enumerable.Range(9, wire.Length - 9)))
            { var bad = wire.ToArray(); bad[index] ^= 1; Require(!KnxDataSecureCodec.TryUnprotectCemi(bad, Key, out var decoded, out _) && decoded is null, "Accepted modified protected byte " + index); }
            var wrong = Key.ToArray(); wrong[0] ^= 1; Require(!KnxDataSecureCodec.TryUnprotectCemi(wire, wrong, out _, out _), "Accepted wrong group key.");
        }
    }
    private static void AuthenticatedFields()
    {
        var wire = Secured(2); wire[2] ^= 0xA4; wire[3] ^= 0x70;
        Require(KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out _, out _), "Mutable repeat/priority/hop/frame-type bits affected authentication.");
        wire[3] ^= 0x80; Require(!KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out _, out _), "Address type was not bound.");
    }
    private static void CemiBoundaries()
    {
        foreach (var size in new[] { 2, 3, 4, 15, 16, 17, 32, 242 })
        {
            var plain = new byte[size]; plain[1] = 128; var frame = Plain(apdu: plain, additional: [5, 1, 9]);
            var wire = KnxDataSecureCodec.ProtectCemi(frame, 2, Key, KnxDataProtection.AuthenticationAndConfidentiality);
            var raw = Cemi.ParseLDataFrame(wire); Require(raw.Apdu.Length == size + 13 && raw.AdditionalInformation.SequenceEqual(new byte[] { 5, 1, 9 }), "cEMI geometry or additional info changed.");
            Require((raw.Control1 & 128) == (size <= 3 ? 128 : 0), "Wrong standard/extended frame selection.");
            Require(KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out var decoded, out _) && Cemi.ParseLDataFrame(decoded!.ToPlainCemi()).Apdu.SequenceEqual(plain), "cEMI round-trip failed.");
        }
    }
    private static void UnsupportedProfiles()
    {
        Throws<ArgumentOutOfRangeException>(() => KnxDataSecureCodec.ProtectCemi(Plain(), 0, Key, KnxDataProtection.Authentication));
        Throws<ArgumentOutOfRangeException>(() => KnxDataSecureCodec.ProtectCemi(Plain(), 1UL << 48, Key, KnxDataProtection.Authentication));
        Throws<NotSupportedException>(() => KnxDataSecureCodec.ProtectCemi(new(CemiMessageCode.LDataReq, 0xBC, 0xE0, Sender, 0, new byte[] { 0, 128 }), 2, Key, KnxDataProtection.Authentication));
        Throws<NotSupportedException>(() => KnxDataSecureCodec.ProtectCemi(Plain(apdu: [3, 0xD7, 1]), 2, Key, KnxDataProtection.Authentication));
        Throws<NotSupportedException>(() => KnxDataSecureCodec.ProtectCemi(Plain(apdu: new byte[243]), 2, Key, KnxDataProtection.Authentication));
        var wire = Secured(2);
        foreach (var scf in new byte[] { 0x12, 0x13, 0x80, 0x90, 0x18, 0x20 }) { var bad = wire.ToArray(); bad[11] = scf; Require(!KnxDataSecureCodec.TryUnprotectCemi(bad, Key, out _, out _), "Accepted unsupported security service."); }
        for (var length = 0; length < wire.Length; length++) Require(!KnxDataSecureCodec.TryUnprotectCemi(wire.AsSpan(0, length), Key, out _, out _), "Accepted truncated cEMI.");
        Require(!KnxDataSecureCodec.TryUnprotectCemi([.. wire, 0], Key, out _, out _), "Accepted trailing cEMI bytes.");
    }

    private static async Task PersistenceAsync()
    {
        var path = Path.Combine(Folder(), "state.bin");
        await using (var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap()))
        { var first = await store.ReserveSendCountersAsync(3); Require(first == new DataSecureCounterRange(10, 12), "Provisioned range differs."); }
        await using var reopened = await DataSecureCounterStore.OpenAsync(path, Identity(), Integrity); Require((await reopened.ReserveSendCountersAsync()).First == 13, "Reopening reused a reserved counter.");
        Require(!reopened.HasExternalRollbackAnchor && File.ReadAllBytes(path).AsSpan().IndexOf(Integrity) < 0, "The state leaked its integrity key or claimed an absent anchor.");
    }
    private static async Task ConcurrentInstancesAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var a = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(1));
        await using var b = await DataSecureCounterStore.OpenAsync(path, Identity(), Integrity);
        var values = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => (i % 2 == 0 ? a : b).ReserveSendCountersAsync()));
        Require(values.Select(v => v.First).Distinct().Count() == 64 && values.Min(v => v.First) == 1 && values.Max(v => v.Last) == 64, "Concurrent instances reused a counter.");
    }
    private static async Task ConcurrentProcessesAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, new(1, new Dictionary<IndividualAddress, ulong>()));
        var runs = await Task.WhenAll(ChildAsync(path, 24), ChildAsync(path, 24)); var values = runs.SelectMany(v => v).ToArray();
        Require(values.Length == 48 && values.Distinct().Count() == 48 && values.Min() == 1 && values.Max() == 48, "Separate processes reused counters.");
        Require((await store.ReserveSendCountersAsync()).First == 49, "Parent ignored counters committed by a child.");
    }
    internal static async Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--data-secure-counter-crash")
        {
            if (args.Length != 3) return 2;
            await using var crashed = await DataSecureCounterStore.OpenAsync(args[1], Identity(), Integrity, new TestFileAnchor(args[2], true));
            await crashed.ReserveSendCountersAsync(); return 3;
        }
        if (args.Length == 0 || args[0] != "--data-secure-counter-child") return null;
        if (args.Length != 3 || !int.TryParse(args[2], out var count) || count is < 1 or > 128) return 2;
        await using var store = await DataSecureCounterStore.OpenAsync(args[1], Identity(), Integrity); var values = new List<ulong>();
        for (var i = 0; i < count; i++) values.Add((await store.ReserveSendCountersAsync()).First);
        Console.WriteLine(JsonSerializer.Serialize(values)); return 0;
    }
    private static async Task<ulong[]> ChildAsync(string path, int count)
    {
        var result = await RunProcessAsync(["--data-secure-counter-child", path, count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        Require(result.ExitCode == 0, "Counter child failed: " + result.Error); return JsonSerializer.Deserialize<ulong[]>(result.Output)!;
    }
    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new Exception("No test process executable.");
        var options = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) options.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (var arg in args) options.ArgumentList.Add(arg);
        using var child = Process.Start(options) ?? throw new Exception("Could not start isolated counter child.");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await child.WaitForExitAsync(timeout.Token); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        return (child.ExitCode, await output, await error);
    }
    private static async Task CrashedProcessAsync()
    {
        var folder = Folder(); var path = Path.Combine(folder, "state.bin"); var anchor = new TestFileAnchor(Path.Combine(folder, "synthetic-test-anchor.json"));
        await using (var initial = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor)) { }
        var child = await RunProcessAsync(["--data-secure-counter-crash", path, anchor.Path]); Require(child.ExitCode == 73, "The child did not exit at the commit gap.");
        await using var reopened = await DataSecureCounterStore.OpenAsync(path, Identity(), Integrity, anchor);
        Require((await reopened.ReserveSendCountersAsync()).First == 11, "A process exit reused the flushed counter.");
    }
    private static async Task InvalidStateAsync()
    {
        var folder = Folder(); var path = Path.Combine(folder, "state.bin");
        await CounterFailure(() => DataSecureCounterStore.OpenAsync(path, Identity(), Integrity), DataSecureCounterFailure.MissingState); Require(!File.Exists(path), "Missing state was created automatically.");
        await using (var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap())) { }
        await CounterFailure(() => DataSecureCounterStore.OpenAsync(path, Identity("another-epoch"), Integrity), DataSecureCounterFailure.ContextMismatch);
        var wrong = Integrity.ToArray(); wrong[0] ^= 1; await CounterFailure(() => DataSecureCounterStore.OpenAsync(path, Identity(), wrong), DataSecureCounterFailure.CorruptState);
        var bytes = File.ReadAllBytes(path); File.WriteAllBytes(path, bytes[..^1]); await CounterFailure(() => DataSecureCounterStore.OpenAsync(path, Identity(), Integrity), DataSecureCounterFailure.CorruptState);
    }
    private static async Task ExhaustionAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(KnxDataSecureCodec.MaximumSequence));
        await CounterFailure(() => store.ReserveSendCountersAsync(2), DataSecureCounterFailure.Exhausted);
        Require((await store.ReserveSendCountersAsync()).First == KnxDataSecureCodec.MaximumSequence, "A rejected range partially allocated counters.");
        await CounterFailure(() => store.ReserveSendCountersAsync(), DataSecureCounterFailure.Exhausted);
    }
    private static async Task ReceiverFloorsAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap());
        Require(!await store.TryAcceptReceiveAsync(Sender, 1) && await store.TryAcceptReceiveAsync(Sender, 8) && !await store.TryAcceptReceiveAsync(Sender, 8), "Receive replay admission differs.");
        await CounterFailure(() => store.TryAcceptReceiveAsync(new(1, 1, 3), 9), DataSecureCounterFailure.ContextMismatch);
        Require(await store.ReadReceiveFloorAsync(Sender) == 8 && await store.ReadReceiveFloorAsync(OtherSender) == 1, "Receiver floors crossed sender identities.");
    }
    private static async Task OwnSenderAsync()
    {
        var path = Path.Combine(Folder(), "state.bin");
        await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, new(10, new Dictionary<IndividualAddress, ulong> { [Local] = 1 }));
        Require(await store.TryAcceptReceiveAsync(Local, 50) && (await store.ReserveSendCountersAsync()).First == 51, "An authenticated local-address observation left a reusable send nonce.");
    }
    private static async Task OwnBootstrapAsync()
    {
        var path = Path.Combine(Folder(), "state.bin");
        await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, new(10, new Dictionary<IndividualAddress, ulong> { [Local] = 50 }));
        Require((await store.ReserveSendCountersAsync()).First == 51, "Provisioning ignored the known own-address receive floor.");
        await store.MarkSynchronizationRequiredAsync();
        await store.ConfirmTrustedCountersAsync(new(20, new Dictionary<IndividualAddress, ulong> { [Local] = 80 }));
        Require((await store.ReserveSendCountersAsync()).First == 81, "Trusted restore ignored a raised own-address receive floor.");
        await store.MarkSynchronizationRequiredAsync();
        await store.ConfirmTrustedCountersAsync(new(1, new Dictionary<IndividualAddress, ulong> { [Local] = 1 }));
        Require((await store.ReserveSendCountersAsync()).First == 82 && await store.ReadReceiveFloorAsync(Local) == 80, "Repeated restore lowered the merged own-address highwater.");
        var exhaustedPath = Path.Combine(Folder(), "state.bin");
        await using var exhausted = await DataSecureCounterStore.InitializeAsync(exhaustedPath, Identity(), Integrity, new(1, new Dictionary<IndividualAddress, ulong> { [Local] = KnxDataSecureCodec.MaximumSequence }));
        await CounterFailure(() => exhausted.ReserveSendCountersAsync(), DataSecureCounterFailure.Exhausted);
    }
    private static void Damage(string path, int index, int bank) { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite); file.Position = 4096 + ((long)index * 2 + bank) * 4096; file.Write(new byte[4096]); file.Flush(true); }
    private static async Task TornUnanchoredAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap());
        await store.ReserveSendCountersAsync(); Damage(path, 1, 1);
        await CounterFailure(() => store.ReserveSendCountersAsync(), DataSecureCounterFailure.SynchronizationRequired);
    }
    private static async Task TornAnchoredAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor);
        await store.ReserveSendCountersAsync(); Damage(path, 1, 1); Require((await store.ReserveSendCountersAsync()).First == 11, "The anchored committed value was lost.");
    }
    private static async Task AnchoredRollbackAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor();
        byte[] snapshot;
        await using (var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor))
        { snapshot = File.ReadAllBytes(path); await store.ReserveSendCountersAsync(); }
        File.WriteAllBytes(path, snapshot); await using var reopened = await DataSecureCounterStore.OpenAsync(path, Identity(), Integrity, anchor);
        await CounterFailure(() => reopened.ReserveSendCountersAsync(), DataSecureCounterFailure.UncertainAnchor);
    }
    private static async Task AnchorGapAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor);
        anchor.FailNextAdvance = true; await ThrowsAsync<IOException>(() => store.ReserveSendCountersAsync());
        Require((await store.ReserveSendCountersAsync()).First == 11, "A flushed but unconfirmed counter was reused.");
    }
    private static async Task AnchorUncertainAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor);
        anchor.ThrowAfterNextAdvance = true; await ThrowsAsync<IOException>(() => store.ReserveSendCountersAsync());
        Require((await store.ReserveSendCountersAsync()).First == 11, "An uncertain successful commit reused a counter.");
    }
    private static async Task AnchorMismatchAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor);
        await CounterFailure(() => DataSecureCounterStore.OpenAsync(path, Identity(), Integrity), DataSecureCounterFailure.ContextMismatch);
        anchor.Missing = true; await CounterFailure(() => store.ReserveSendCountersAsync(), DataSecureCounterFailure.UncertainAnchor);
        anchor.Missing = false; anchor.Conflicting = true; await CounterFailure(() => store.ReserveSendCountersAsync(), DataSecureCounterFailure.UncertainAnchor);
    }
    private static async Task RestoreModeAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap());
        await store.MarkSynchronizationRequiredAsync(); Require(await store.IsSynchronizationRequiredAsync(), "Restore was not marked.");
        await CounterFailure(() => store.ReserveSendCountersAsync(), DataSecureCounterFailure.SynchronizationRequired);
        await CounterFailure(() => store.TryAcceptReceiveAsync(Sender, 3), DataSecureCounterFailure.SynchronizationRequired);
        await store.ConfirmTrustedCountersAsync(new(20, new Dictionary<IndividualAddress, ulong> { [Sender] = 30, [OtherSender] = 40 }));
        Require(!await store.IsSynchronizationRequiredAsync() && (await store.ReserveSendCountersAsync()).First == 20 && await store.ReadReceiveFloorAsync(Sender) == 30, "Trusted resume lost a floor.");
        await store.MarkSynchronizationRequiredAsync(); await store.ConfirmTrustedCountersAsync(Bootstrap(1));
        Require((await store.ReserveSendCountersAsync()).First == 21 && await store.ReadReceiveFloorAsync(Sender) == 30, "Trusted resume decreased a previous floor.");
    }
    private static async Task PrivatePathsAsync()
    {
        await CounterFailure(() => DataSecureCounterStore.InitializeAsync("state.bin", Identity(), Integrity, Bootstrap()), DataSecureCounterFailure.UnsupportedPath);
        var folder = Folder(); Directory.CreateDirectory(Path.Combine(folder, ".git"));
        await CounterFailure(() => DataSecureCounterStore.InitializeAsync(Path.Combine(folder, "state.bin"), Identity(), Integrity, Bootstrap()), DataSecureCounterFailure.UnsupportedPath);
        Require(!File.Exists(Path.Combine(folder, "state.bin")), "A Git checkout received counter state.");
    }

    private static async Task ContextBindingsAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap());
        Throws<ArgumentException>(() => new KnxDataSecureGroupContext(store, Identity("other"), [new(Group, Key, KnxDataProtection.Authentication, new[] { Sender })]));
        Throws<ArgumentException>(() => new KnxDataSecureGroupContext(store, Identity(), [new(Group, Key, KnxDataProtection.Authentication, new[] { new IndividualAddress(1, 1, 3) })]));
        await using var context = Context(store);
        await ThrowsAsync<InvalidOperationException>(() => context.ProtectCemiAsync(Cemi.EncodeLDataFrame(Plain())));
        await ThrowsAsync<InvalidOperationException>(() => context.ProtectCemiAsync(Cemi.EncodeLDataFrame(Plain(Local, OtherGroup))));
        var wire = await context.ProtectCemiAsync(Cemi.EncodeLDataFrame(Plain(Local)));
        Require(KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out var decoded, out _) && decoded!.Source == Local && decoded.Sequence == 10, "Bound outgoing sender/counter differs.");
        Require((await context.ReceiveCemiAsync(Secured(2, OtherSender))).Failure == DataSecureReceiveFailure.UnauthorizedSender
            && (await context.ReceiveCemiAsync(Secured(2, group: OtherGroup))).Failure == DataSecureReceiveFailure.UnknownGroup, "Group/sender policy crossed contexts.");
    }
    private static async Task ContextAdmissionAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap()); await using var context = Context(store);
        Require((await context.ReceiveCemiAsync(Cemi.EncodeLDataFrame(Plain(incoming: true)))).Failure == DataSecureReceiveFailure.PlaintextForbidden, "Accepted plaintext downgrade.");
        Require((await context.ReceiveCemiAsync(Secured(2, protection: KnxDataProtection.Authentication))).Failure == DataSecureReceiveFailure.ProtectionDowngrade, "Accepted authentication-only for a confidential group.");
        var bad = Secured(90); bad[^1] ^= 1; Require((await context.ReceiveCemiAsync(bad)).Failure == DataSecureReceiveFailure.AuthenticationFailed, "Accepted forged authentication.");
        Require(await store.ReadReceiveFloorAsync(Sender) == 1 && (await context.ReceiveCemiAsync(Secured(2))).Accepted, "Rejected traffic moved replay state.");
    }
    private static async Task ContextReplayAsync()
    {
        var path = Path.Combine(Folder(), "state.bin");
        await using (var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap()))
        { await using var context = Context(store, KnxDataProtection.Authentication); Require((await context.ReceiveCemiAsync(Secured(2, protection: KnxDataProtection.Authentication))).Accepted, "Authentication-only group was refused."); }
        await using var reopened = await DataSecureCounterStore.OpenAsync(path, Identity(), Integrity); await using var next = Context(reopened);
        Require((await next.ReceiveCemiAsync(Secured(2))).Failure == DataSecureReceiveFailure.Replay && (await next.ReceiveCemiAsync(Secured(3))).Accepted, "A context restart reset its replay floor.");
    }
    private static async Task ContextCommitBoundaryAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor); await using var context = Context(store);
        anchor.HoldNextAdvance = true; var receiving = context.ReceiveCemiAsync(Secured(2)); await anchor.AdvanceStarted.Task;
        Require(!receiving.IsCompleted, "Application data was released before its anchor committed."); anchor.ReleaseAdvance.TrySetResult(); Require((await receiving).Accepted, "A committed frame was lost.");
    }
    private static async Task CallerMutationAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor); await using var context = Context(store);
        anchor.HoldNextAdvance = true; var first = context.ReceiveCemiAsync(Secured(2)); await anchor.AdvanceStarted.Task;
        var original = Secured(3); var queued = context.ReceiveCemiAsync(original); var replacement = Secured(3, group: OtherGroup); replacement.CopyTo(original, 0);
        anchor.ReleaseAdvance.TrySetResult(); Require((await first).Accepted, "First admission failed."); var result = await queued;
        Require(result.Accepted && result.Data!.Group == Group && result.Data.Sequence == 3, "A caller edit changed queued group authorization.");
    }
    private static async Task OutgoingMutationAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor); await using var context = Context(store);
        var plain = Cemi.EncodeLDataFrame(Plain(Local)); anchor.HoldNextAdvance = true; var sending = context.ProtectCemiAsync(plain); await anchor.AdvanceStarted.Task;
        plain[^1] ^= 1; anchor.ReleaseAdvance.TrySetResult(); var wire = await sending;
        Require(KnxDataSecureCodec.TryUnprotectCemi(wire, Key, out var decoded, out _) && decoded!.Apdu.SequenceEqual(new byte[] { 0, 128, 1 }), "A caller edit changed a reserved outgoing payload.");
    }
    private static async Task ContextDisposalAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor); var context = Context(store);
        anchor.HoldNextAdvance = true; var receiving = context.ReceiveCemiAsync(Secured(2)); await anchor.AdvanceStarted.Task;
        await context.DisposeAsync(); await ThrowsAsync<OperationCanceledException>(() => receiving); await ThrowsAsync<ObjectDisposedException>(() => context.ReceiveCemiAsync(Secured(3)));
        anchor.ReleaseAdvance.TrySetResult(); Require(await store.ReadReceiveFloorAsync(Sender) == 2, "Cancelled admission reused a locally flushed receive counter.");
    }
    private static async Task ContextStateFailureAsync()
    {
        var path = Path.Combine(Folder(), "state.bin"); var anchor = new Anchor(); await using var store = await DataSecureCounterStore.InitializeAsync(path, Identity(), Integrity, Bootstrap(), anchor); await using var context = Context(store);
        anchor.FailNextAdvance = true; var failed = await context.ReceiveCemiAsync(Secured(2)); Require(!failed.Accepted && failed.Data is null && failed.Failure == DataSecureReceiveFailure.StateUnavailable, "State failure released verified application data.");
        Require((await context.ReceiveCemiAsync(Secured(2))).Failure == DataSecureReceiveFailure.Replay, "A locally consumed but failed admission replayed later.");
    }

    private sealed class Anchor : IDataSecureMonotonicAnchor
    {
        private readonly Dictionary<string, DataSecureCounterCheckpoint> _values = [];
        private readonly object _gate = new();
        public bool FailNextAdvance, ThrowAfterNextAdvance, HoldNextAdvance, Missing, Conflicting;
        public TaskCompletionSource AdvanceStarted { get; } = Gate();
        public TaskCompletionSource ReleaseAdvance { get; } = Gate();
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); lock (_gate)
            {
                var value = Missing ? null : _values.GetValueOrDefault(context);
                if (value is not null && Conflicting) value = value with { Revision = value.Revision + 2 };
                return Task.FromResult(value);
            }
        }
        public async Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        {
            if (HoldNextAdvance && expected is not null) { HoldNextAdvance = false; AdvanceStarted.TrySetResult(); await ReleaseAdvance.Task.WaitAsync(ct); }
            ct.ThrowIfCancellationRequested(); lock (_gate)
            {
                if (expected is not null && FailNextAdvance) { FailNextAdvance = false; throw new IOException("Synthetic unavailable anchor."); }
                if (_values.GetValueOrDefault(context) != expected) return false;
                _values[context] = proposed;
                if (expected is not null && ThrowAfterNextAdvance) { ThrowAfterNextAdvance = false; throw new IOException("Synthetic uncertain anchor result."); }
                return true;
            }
        }
    }
    // An intentionally synthetic cross-process anchor fixture. An ordinary JSON file is not a production monotonic anchor.
    private sealed class TestFileAnchor(string path, bool exitBeforeAdvance = false) : IDataSecureMonotonicAnchor
    {
        public string Path => path;
        private Dictionary<string, DataSecureCounterCheckpoint> Load() => File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, DataSecureCounterCheckpoint>>(File.ReadAllText(path))! : [];
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Load().GetValueOrDefault(context)); }
        public Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var values = Load(); if (values.GetValueOrDefault(context) != expected) return Task.FromResult(false);
            if (exitBeforeAdvance && expected is not null) Environment.Exit(73);
            values[context] = proposed;
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(stream, values); stream.Flush(true); return Task.FromResult(true);
        }
    }
}
