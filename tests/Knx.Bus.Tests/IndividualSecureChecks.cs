using System.Reflection;
using System.Security.Cryptography;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

internal static class IndividualSecureChecks
{
    private static readonly IndividualAddress Local = new(0x11F0), Peer = new(0x1101);
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), Integrity = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
    private static void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));
    private static CemiLDataFrame Plain(bool incoming = false, byte[]? tpdu = null) => new(incoming ? CemiMessageCode.LDataInd : CemiMessageCode.LDataReq,
        0xB0, 0x60, incoming ? Peer : Local, incoming ? Local.Raw : Peer.Raw, tpdu ?? [0x03, 0xD5, 0, 7, 0x10, 1]);
    private static byte[] Secured(ulong sequence, KnxIndividualSecureKeyKind kind = KnxIndividualSecureKeyKind.ToolAccess)
        => KnxIndividualSecureCodec.ProtectData(Plain(true, [0x43, 0xD6, 0, 7, 0x10, 1, 9]), sequence, Key, kind);
    private static async Task<(DataSecureCounterStore Store, KnxIndividualSecureContext Context, KnxIndividualSecureCounterBinding Binding, string Path)> Policy(
        Anchor? anchor = null, TimeProvider? clock = null, ulong next = 10, KnxIndividualSecureKeyKind kind = KnxIndividualSecureKeyKind.ToolAccess)
    {
        var folder = Path.Combine(Path.GetTempPath(), "buschord-individual-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var binding = kind == KnxIndividualSecureKeyKind.ToolAccess ? KnxIndividualSecureCounterBinding.ToolAccess("Synthetic installation", "key-v1", Local)
            : KnxIndividualSecureCounterBinding.PointToPoint(new("Synthetic installation", "key-v1", Local));
        var path = Path.Combine(folder, "state.bin"); var store = await DataSecureCounterStore.InitializeAsync(path, binding.Identity, Integrity, new(next, new Dictionary<IndividualAddress, ulong> { [Peer] = 1 }), anchor);
        return (store, new(store, binding, Peer, Key, 64, clock), binding, path);
    }
    private static byte[] Response(KnxIndividualSynchronization operation, ulong remote = 30, ulong local = 50, KnxIndividualSecureKeyKind kind = KnxIndividualSecureKeyKind.ToolAccess)
    {
        Require(KnxIndividualSecureCodec.TryUnprotectSyncRequest(operation.CopyFrame(), Key, kind, out var request, out _), "Synthetic peer could not verify request.");
        try { var sent = Cemi.ParseLDataFrame(operation.CopyFrame()); return KnxIndividualSecureCodec.ProtectSyncResponse(Plain(true, [(byte)(sent.Apdu[0] & 0xFC), 0]), request!.Challenge, 0xAABBCCDDEEFF, remote, local, Key, kind); }
        finally { CryptographicOperations.ZeroMemory(request!.Challenge); }
    }
    private static async Task Synchronize(KnxIndividualSecureContext context, ulong remote = 30, ulong local = 50)
    { var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); Require(await context.HandleSynchronizationResponseAsync(Response(pending, remote, local, context.KeyKind)), "Fresh Sync rejected."); await pending.Completion; }
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("four published Annex data/Sync vectors", () => Sync(PublishedVectors)),
            ("twelve independent Tool/P2P connected and connectionless vectors", () => Sync(IndependentVectors)),
            ("full TPCI ranges, APDU limits, tampering and unsupported profiles", () => Sync(CodecBoundaries)),
            ("stable explicit domains and exact store/peer binding", ProvisioningAsync),
            ("fresh Sync gates data and identical retries consume one nonce", BeforeSyncAsync),
            ("peer, mode, message type and MAC bind the pending Sync", SyncBindingAsync),
            ("Sync readiness waits for durable max merging", SyncCommitAsync),
            ("authenticated Sync cannot clear an external restore gate", RestoreGateAsync),
            ("expired and unsolicited responses do not move counters", ExpirationAsync),
            ("raw initializer, cancellation and pending-state cleanup", InitializerAsync),
            ("individual replay floor commits before plaintext release", ReceiveCommitAsync),
            ("caller buffer snapshot survives an awaited reservation", MutationAsync),
            ("reset and reopen require Sync without nonce reuse", RestartAsync),
            ("Sync/data sequence exhaustion never wraps or resets", ExhaustionAsync),
            ("disposal joins pending work before clearing owned keys", DisposalAsync),
            ("ordinary P2P shares its supplied ordinary counter store", PointToPointAsync),
            ("authenticated max merge preserves all higher prior facts", MergeAsync),
            ("explicit peer wire capacity includes the Secure overhead", CapacityAsync),
            ("throwing cancellation callbacks cannot skip join or key cleanup", CallbackFailureAsync),
            ("partial Sync anchor failure releases no readiness and restart burns ahead", PartialMergeAsync),
            ("throwing anchor cancellation cannot strand store drain or integrity keys", StoreCallbackFailureAsync),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS IndividualSecure " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL IndividualSecure " + name + ": " + ex); }
        }
        return failures;
    }
    private static Task Sync(Action run) { run(); return Task.CompletedTask; }
    private static void PublishedVectors()
    {
        CemiLDataFrame F(bool response, byte[] apdu) => new(CemiMessageCode.LDataInd, 0xB0, 0x60, new((ushort)(response ? 0xFF00 : 0xFF67)), (ushort)(response ? 0xFF67 : 0xFF00), apdu);
        void Equal(byte[] wire, string expected) => Require(Cemi.ParseLDataFrame(wire).Apdu.SequenceEqual(Hex(expected)), "Published Annex vector differs.");
        var data = KnxIndividualSecureCodec.ProtectData(F(false, Hex("03D705351001202122232425262728292A2B2C2D2E2F")), 4, Key, KnxIndividualSecureKeyKind.ToolAccess);
        Equal(data, "03f1900000000000046767242a2308ca76a11774214ee4cf5d94909f743d050d8fc168");
        Require(KnxIndividualSecureCodec.TryUnprotectData(data, Key, KnxIndividualSecureKeyKind.ToolAccess, out var plain, out _) && plain!.Tpdu.SequenceEqual(Hex("03D705351001202122232425262728292A2B2C2D2E2F")), "Published decode differs.");
        Equal(KnxIndividualSecureCodec.ProtectData(F(true, Hex("03D605351001202122232425262728292A2B2C2D2E2F")), 3, Key, KnxIndividualSecureKeyKind.ToolAccess), "03f190000000000003706f533105503557cb2b24f1dd341b60b7e017ecd6b06849a72b");
        data = KnxIndividualSecureCodec.ProtectSyncRequest(F(false, [0x40, 0]), 1, Hex("000000000003"), Key, KnxIndividualSecureKeyKind.ToolAccess);
        Equal(data, "43f192000000000001000000000000c1cf4506f09bd79fab55");
        Require(KnxIndividualSecureCodec.TryUnprotectSyncRequest(data, Key, KnxIndividualSecureKeyKind.ToolAccess, out var request, out _) && request!.Challenge.SequenceEqual(Hex("000000000003")), "Published challenge differs.");
        data = KnxIndividualSecureCodec.ProtectSyncResponse(F(true, [0x40, 0]), Hex("000000000003"), 0xAAAAAAAAAAAA, 3, 4, Key, KnxIndividualSecureKeyKind.ToolAccess);
        Equal(data, "43f193aaaaaaaaaaa99c023ad25e146470693e638d5b70cac4");
        Require(KnxIndividualSecureCodec.TryUnprotectSyncResponse(data, Key, KnxIndividualSecureKeyKind.ToolAccess, Hex("000000000003"), out var response, out _) && response == new KnxIndividualSyncResponse(3, 4), "Published Sync counters differ.");
    }
    private static void IndependentVectors()
    {
        string[][] expected = [
            ["03f19001020304050638c73cccef97ce4f4432", "03f1920102030405060000000000002b320c8baff66769d159", "03f193ba9bfc9dbe9faacbd9b3002bf9b25d70aadb964d7e12"],
            ["6ff19001020304050638c73cccef97bfa2356f", "6ff1920102030405060000000000002b320c8baff648110e26", "6ff193ba9bfc9dbe9faacbd9b3002bf9b25d70aadb73a8a0d0"],
            ["03f11001020304050638c73cccef97b09d1723", "03f1120102030405060000000000002b320c8baff618eee81e", "03f113ba9bfc9dbe9faacbd9b3002bf9b25d70aadb29122a0a"],
            ["6ff11001020304050638c73cccef976a289f3d", "6ff1120102030405060000000000002b320c8baff664ec43ec", "6ff113ba9bfc9dbe9faacbd9b3002bf9b25d70aadba2812bc6"] ];
        var index = 0;
        foreach (var kind in new[] { KnxIndividualSecureKeyKind.ToolAccess, KnxIndividualSecureKeyKind.PointToPoint }) foreach (byte tpci in new byte[] { 0, 0x6C })
        {
            var inner = Hex("03D500071001"); inner[0] |= tpci;
            byte[][] frames = [ KnxIndividualSecureCodec.ProtectData(Plain(tpdu: inner), 0x010203040506, Key, kind),
                KnxIndividualSecureCodec.ProtectSyncRequest(Plain(tpdu: [tpci, 0]), 0x010203040506, Hex("102030405060"), Key, kind),
                KnxIndividualSecureCodec.ProtectSyncResponse(Plain(true, [tpci, 0]), Hex("102030405060"), 0xAABBCCDDEEFF, 0x010203040507, 0x010203040609, Key, kind) ];
            for (var service = 0; service < frames.Length; service++) Require(Cemi.ParseLDataFrame(frames[service]).Apdu.SequenceEqual(Hex(expected[index][service])), "Independent vector differs."); index++;
        }
    }
    private static void CodecBoundaries()
    {
        foreach (var size in new[] { 2, 16, 17, 242 }) foreach (var number in Enumerable.Range(0, 16))
        {
            var tpdu = new byte[size]; tpdu[0] = (byte)(0x43 | number << 2); tpdu[1] = 0xD5;
            var wire = KnxIndividualSecureCodec.ProtectData(Plain(tpdu: tpdu), 19, Key, KnxIndividualSecureKeyKind.ToolAccess);
            Require(KnxIndividualSecureCodec.TryUnprotectData(wire, Key, KnxIndividualSecureKeyKind.ToolAccess, out var data, out _) && data!.Tpdu.SequenceEqual(tpdu), "Connected TPCI/APDU differs.");
        }
        var valid = Secured(20);
        foreach (var offset in new[] { 4, 5, 6, 7, 9, 10, 11, 12, 17, 19, valid.Length - 1 })
        { var changed = valid.ToArray(); changed[offset] ^= 4; Require(!KnxIndividualSecureCodec.TryUnprotectData(changed, Key, KnxIndividualSecureKeyKind.ToolAccess, out var result, out _) && result is null, "Tampered field released plaintext."); }
        var mutable = valid.ToArray(); mutable[2] ^= 0x28; mutable[3] ^= 0x70; Require(KnxIndividualSecureCodec.TryUnprotectData(mutable, Key, KnxIndividualSecureKeyKind.ToolAccess, out _, out _), "Mutable routing flag was authenticated.");
        Require(!KnxIndividualSecureCodec.TryUnprotectData(valid, Key, KnxIndividualSecureKeyKind.PointToPoint, out _, out _), "Tool data entered P2P context.");
        Throws<ArgumentOutOfRangeException>(() => KnxIndividualSecureCodec.ProtectData(Plain(), 0, Key, KnxIndividualSecureKeyKind.ToolAccess));
        Throws<ArgumentOutOfRangeException>(() => KnxIndividualSecureCodec.ProtectData(Plain(), 0x1000000000000, Key, KnxIndividualSecureKeyKind.ToolAccess));
        Throws<NotSupportedException>(() => KnxIndividualSecureCodec.ProtectData(new(CemiMessageCode.LDataReq, 0xBC, 0xE0, Local, 0x101, [0, 0x80]), 1, Key, KnxIndividualSecureKeyKind.ToolAccess));
        Throws<ArgumentException>(() => KnxIndividualSecureCodec.ProtectData(Plain(tpdu: [0x80]), 1, Key, KnxIndividualSecureKeyKind.ToolAccess));
        Throws<ArgumentException>(() => KnxIndividualSecureCodec.ProtectData(Plain(tpdu: [3, 0xF1]), 1, Key, KnxIndividualSecureKeyKind.ToolAccess));
        valid[11] = 0x80; Require(!KnxIndividualSecureCodec.TryUnprotectData(valid, Key, KnxIndividualSecureKeyKind.ToolAccess, out _, out _), "Tool auth-only allowed.");
        for (var length = 0; length < 25; length++) Require(!KnxIndividualSecureCodec.TryUnprotectSyncResponse(new byte[length], Key, KnxIndividualSecureKeyKind.ToolAccess, new byte[6], out _, out _), "Truncated Sync accepted.");
    }
    private static async Task ProvisioningAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var context = p.Context; var ordinary = KnxIndividualSecureCounterBinding.PointToPoint(new("Synthetic installation", "key-v1", Local));
        Require(ordinary.Identity.Fingerprint != p.Binding.Identity.Fingerprint, "Tool/ordinary domains collided."); Throws<ArgumentException>(() => KnxIndividualSecureCounterBinding.ToolAccess("installation", " ", Local));
        Throws<ArgumentException>(() => new KnxIndividualSecureContext(store, ordinary, Peer, Key, 64)); Throws<ArgumentException>(() => new KnxIndividualSecureContext(store, p.Binding, new(0x1102), Key, 64));
        Require(KnxIndividualSecureCounterBinding.ToolAccess("Synthetic installation", "key-v1", Local).Identity.Fingerprint == p.Binding.Identity.Fingerprint, "Repeated binding invented a domain.");
    }
    private static async Task BeforeSyncAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var context = p.Context;
        await ThrowsAsync<InvalidOperationException>(() => context.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain()))); Require(!(await context.ReceiveDataAsync(Secured(30))).Accepted, "Data entered before Sync.");
        var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); var retry = await context.CopyPendingRetryAsync(pending); retry[^1] ^= 1;
        Require((await context.CopyPendingRetryAsync(pending)).SequenceEqual(pending.CopyFrame()), "Caller changed cached Sync."); await ThrowsAsync<InvalidOperationException>(() => context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])));
        Require(context.State == KnxIndividualSecureState.Synchronizing, "Competing Sync invalidated original pending state."); Require(await context.HandleSynchronizationResponseAsync(Response(pending, 30, 10)), "Original challenge stopped working.");
        Require(KnxIndividualSecureCodec.TryUnprotectData(await context.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain())), Key, context.KeyKind, out var data, out _) && data!.Sequence == 11, "Sync nonce reused for S-A_Data.");
        Require(!await context.HandleSynchronizationResponseAsync(Response(pending)), "Unsolicited duplicate Sync admitted.");
    }
    private static async Task SyncBindingAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var context = p.Context; var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); var good = Response(pending);
        foreach (var offset in new[] { 0, 4, 5, 6, 7, 9, 11, 17, good.Length - 1 })
        { var bad = good.ToArray(); bad[offset] ^= offset == 0 ? (byte)7 : (byte)0x40; Require(!await context.HandleSynchronizationResponseAsync(bad), "Unbound response satisfied challenge."); }
        Require(context.State == KnxIndividualSecureState.Synchronizing && await store.ReadReceiveFloorAsync(Peer) == 1, "Forged response moved state."); Require(await context.HandleSynchronizationResponseAsync(good), "Valid response was lost.");
    }
    private static async Task SyncCommitAsync()
    {
        var anchor = new Anchor(); var p = await Policy(anchor); await using var store = p.Store; await using var context = p.Context;
        var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); anchor.HoldNext = true; var receiving = context.HandleSynchronizationResponseAsync(Response(pending)); await anchor.Started.Task;
        Require(!receiving.IsCompleted && !pending.Completion.IsCompleted && context.State == KnxIndividualSecureState.Synchronizing, "Sync ready before durable commit."); anchor.Release.TrySetResult();
        Require(await receiving && await store.ReadReceiveFloorAsync(Peer) == 29 && (await store.ReserveSendCountersAsync()).First == 50, "Sync max merge differs.");
    }
    private static async Task RestoreGateAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var context = p.Context;
        var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); await store.MarkSynchronizationRequiredAsync(); await ThrowsAsync<DataSecureCounterStateException>(() => context.HandleSynchronizationResponseAsync(Response(pending)));
        Require(await store.IsSynchronizationRequiredAsync() && context.State == KnxIndividualSecureState.Faulted && await store.ReadReceiveFloorAsync(Peer) == 1, "Sync bypassed restore gate.");
        context.ResetConnection(); await ThrowsAsync<InvalidOperationException>(() => context.BeginSynchronizationAsync(Plain(tpdu: [0, 0]))); Require(context.State == KnxIndividualSecureState.Faulted, "Reset cleared a fault.");
    }
    private static async Task ExpirationAsync()
    {
        var clock = new Clock(); var p = await Policy(clock: clock); await using var store = p.Store; await using var context = p.Context;
        var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); clock.Advance(TimeSpan.FromSeconds(7)); Require(!await context.HandleSynchronizationResponseAsync(Response(pending)) && await store.ReadReceiveFloorAsync(Peer) == 1, "Expired Sync moved a floor.");
        await ThrowsAsync<TimeoutException>(() => pending.Completion); var next = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); Require(next.Sequence == 11, "New challenge reused expired nonce."); context.ResetConnection();
    }
    private static async Task InitializerAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var context = p.Context; var calls = 0;
        await context.InitializeAsync(async (wire, ct) =>
        {
            calls++; Require(KnxIndividualSecureCodec.TryUnprotectSyncRequest(wire.Span, Key, context.KeyKind, out var request, out _), "Initializer sent ordinary data.");
            try { Require(await context.HandleSynchronizationResponseAsync(KnxIndividualSecureCodec.ProtectSyncResponse(Plain(true, [0, 0]), request!.Challenge, 99, 30, 50, Key, context.KeyKind), ct), "Initializer response rejected."); }
            finally { CryptographicOperations.ZeroMemory(request!.Challenge); }
        });
        Require(calls == 1 && context.State == KnxIndividualSecureState.Ready, "Initializer published state too early."); context.ResetConnection(); using var cancel = new CancellationTokenSource(); var started = Gate();
        var opening = context.InitializeAsync((_, _) => { started.TrySetResult(); return Task.CompletedTask; }, cancel.Token); await started.Task; cancel.Cancel(); await ThrowsAsync<OperationCanceledException>(() => opening);
        Require(context.State == KnxIndividualSecureState.SynchronizationRequired, "Cancellation retained a usable session.");
    }
    private static async Task ReceiveCommitAsync()
    {
        var anchor = new Anchor(); var p = await Policy(anchor); await using var store = p.Store; await using var context = p.Context; await Synchronize(context);
        anchor.HoldNext = true; var receiving = context.ReceiveDataAsync(Secured(30)); await anchor.Started.Task; Require(!receiving.IsCompleted, "Plaintext escaped before durable replay admission."); anchor.Release.TrySetResult(); var result = await receiving;
        Require(result.Accepted && result.Data!.Tpdu.SequenceEqual(Hex("43D60007100109")) && await store.ReadReceiveFloorAsync(Peer) == 30, "Full transport TPCI was lost."); Require((await context.ReceiveDataAsync(Secured(30))).Failure == KnxIndividualSecureReceiveFailure.Replay, "Replay admitted.");
        var bad = Secured(31); bad[^1] ^= 1; Require(!(await context.ReceiveDataAsync(bad)).Accepted && await store.ReadReceiveFloorAsync(Peer) == 30, "Bad MAC moved replay floor.");
    }
    private static async Task MutationAsync()
    {
        var anchor = new Anchor(); var p = await Policy(anchor); await using var store = p.Store; await using var context = p.Context; await Synchronize(context);
        var caller = Cemi.EncodeLDataFrame(Plain()); anchor.HoldNext = true; var sending = context.ProtectDataAsync(caller); await anchor.Started.Task; caller[^1] = 99; anchor.Release.TrySetResult();
        Require(KnxIndividualSecureCodec.TryUnprotectData(await sending, Key, context.KeyKind, out var data, out _) && data!.Tpdu[^1] == 1, "Caller changed queued command.");
    }
    private static async Task RestartAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using (var context = p.Context) { await Synchronize(context); await context.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain())); context.ResetConnection(); await ThrowsAsync<InvalidOperationException>(() => context.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain()))); }
        await using var reopened = await DataSecureCounterStore.OpenAsync(p.Path, p.Binding.Identity, Integrity); await using var fresh = new KnxIndividualSecureContext(reopened, p.Binding, Peer, Key, 64);
        Require(fresh.State == KnxIndividualSecureState.SynchronizationRequired, "File restored readiness."); var next = await fresh.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); Require(next.Sequence == 51, "Reopen reused command nonce."); fresh.ResetConnection();
    }
    private static async Task ExhaustionAsync()
    {
        var p = await Policy(next: KnxIndividualSecureCodec.MaximumSequence + 1); await using var store = p.Store; await using var context = p.Context; await ThrowsAsync<DataSecureCounterStateException>(() => context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])));
        var p2 = await Policy(); await using var store2 = p2.Store; await using var context2 = p2.Context; await Synchronize(context2, 30, KnxIndividualSecureCodec.MaximumSequence);
        Require(KnxIndividualSecureCodec.TryUnprotectData(await context2.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain())), Key, context2.KeyKind, out var data, out _) && data!.Sequence == KnxIndividualSecureCodec.MaximumSequence, "Last sequence skipped.");
        await ThrowsAsync<DataSecureCounterStateException>(() => context2.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain()))); Require(context2.State == KnxIndividualSecureState.Faulted, "Exhaustion requested unsafe reset.");
    }
    private static async Task DisposalAsync()
    {
        var anchor = new Anchor(); var p = await Policy(anchor); await using var store = p.Store; var context = p.Context; var key = (byte[])typeof(KnxIndividualSecureContext).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        await Synchronize(context); anchor.HoldNext = true; anchor.IgnoreCancellation = true; var sending = context.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain())); await anchor.Started.Task;
        var closing = context.DisposeAsync().AsTask(); Require(!closing.IsCompleted && key.Any(b => b != 0), "Key cleared before work joined."); anchor.Release.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => sending); await closing; await context.DisposeAsync();
        Require(key.All(b => b == 0) && (await store.ReserveSendCountersAsync()).First > 50, "Key not cleared or borrowed store was disposed.");
    }
    private static async Task PointToPointAsync()
    {
        var p = await Policy(kind: KnxIndividualSecureKeyKind.PointToPoint); await using var store = p.Store; await using var context = p.Context; await Synchronize(context); var wire = await context.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain()));
        Require(Cemi.ParseLDataFrame(wire).Apdu[2] == 0x10 && KnxIndividualSecureCodec.TryUnprotectData(wire, Key, context.KeyKind, out var data, out _) && data!.Sequence == 50 && (await store.ReserveSendCountersAsync()).First == 51, "Ordinary P2P did not share the ordinary highwater.");
    }
    private static async Task MergeAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var context = p.Context; await store.MergeAuthenticatedSyncAsync(Peer, 100, 200); await store.MergeAuthenticatedSyncAsync(Peer, 2, 3);
        Require(await store.ReadReceiveFloorAsync(Peer) == 99 && (await store.ReserveSendCountersAsync()).First == 200, "Sync merge decreased a counter."); await store.MarkSynchronizationRequiredAsync(); await ThrowsAsync<DataSecureCounterStateException>(() => store.MergeAuthenticatedSyncAsync(Peer, 500, 500)); Require(await store.IsSynchronizationRequiredAsync(), "Merge cleared restore readiness.");
    }
    private static async Task CapacityAsync()
    {
        var p = await Policy(); await using var store = p.Store; await using var unused = p.Context;
        Throws<ArgumentOutOfRangeException>(() => new KnxIndividualSecureContext(store, p.Binding, Peer, Key, 24));
        await using var narrow = new KnxIndividualSecureContext(store, p.Binding, Peer, Key, 25); await Synchronize(narrow);
        await ThrowsAsync<ArgumentOutOfRangeException>(() => narrow.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain(tpdu: new byte[13]))));
        Require((await store.ReserveSendCountersAsync()).First == 50, "An over-capacity command allocated a counter.");
        var large = KnxIndividualSecureCodec.ProtectData(Plain(true, new byte[13]), 30, Key, narrow.KeyKind);
        Require(!(await narrow.ReceiveDataAsync(large)).Accepted && await store.ReadReceiveFloorAsync(Peer) == 29, "An over-capacity incoming message moved its floor.");
    }
    private static async Task CallbackFailureAsync()
    {
        var p = await Policy(); await using var store = p.Store; var context = p.Context; var started = Gate();
        var key = (byte[])typeof(KnxIndividualSecureContext).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        var opening = context.InitializeAsync(async (_, ct) =>
        {
            using var registration = ct.Register(() => throw new IOException("Synthetic cancellation callback failure."));
            started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await started.Task; var closing = context.DisposeAsync().AsTask(); await ThrowsAsync<OperationCanceledException>(() => opening);
        await ThrowsAsync<AggregateException>(() => closing); Require(key.All(value => value == 0) && context.State == KnxIndividualSecureState.Disposed, "Cancellation callback prevented owned key cleanup.");
    }
    private static async Task PartialMergeAsync()
    {
        var anchor = new Anchor(); var p = await Policy(anchor); await using var store = p.Store;
        await using (var context = p.Context)
        {
            var pending = await context.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); anchor.FailRemoteOnce = true;
            await ThrowsAsync<DataSecureCounterStateException>(() => context.HandleSynchronizationResponseAsync(Response(pending)));
            Require(context.State == KnxIndividualSecureState.Faulted, "A partial Sync merge published readiness.");
        }
        await using var reopened = await DataSecureCounterStore.OpenAsync(p.Path, p.Binding.Identity, Integrity, anchor);
        await using var fresh = new KnxIndividualSecureContext(reopened, p.Binding, Peer, Key, 64);
        var next = await fresh.BeginSynchronizationAsync(Plain(tpdu: [0, 0])); Require(next.Sequence == 50, "Restart lost the committed Sync send highwater.");
        Require(await fresh.HandleSynchronizationResponseAsync(Response(next)) && await reopened.ReadReceiveFloorAsync(Peer) == 29, "Pending receive-anchor commit did not reconcile.");
        Require(KnxIndividualSecureCodec.TryUnprotectData(await fresh.ProtectDataAsync(Cemi.EncodeLDataFrame(Plain())), Key, fresh.KeyKind, out var data, out _) && data!.Sequence == 51, "Recovery reused its new Sync nonce.");
    }
    private static async Task StoreCallbackFailureAsync()
    {
        var anchor = new Anchor(); var p = await Policy(anchor); var store = p.Store; await p.Context.DisposeAsync();
        var key = (byte[])typeof(DataSecureCounterStore).GetField("_integrityKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        anchor.HoldNext = true; anchor.IgnoreCancellation = true; anchor.ThrowOnCancellation = true;
        var reserving = store.ReserveSendCountersAsync(); await anchor.Started.Task;
        var first = store.DisposeAsync().AsTask(); var second = store.DisposeAsync().AsTask();
        Require(ReferenceEquals(first, second) && !first.IsCompleted && key.Any(value => value != 0), "Store disposal did not retain shared drain/key ownership.");
        anchor.Release.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => reserving);
        await ThrowsAsync<AggregateException>(() => first); await ThrowsAsync<AggregateException>(() => second);
        Require(key.All(value => value == 0), "Throwing anchor callback prevented integrity-key zeroing.");
        await ThrowsAsync<ObjectDisposedException>(() => store.ReserveSendCountersAsync());
    }
    private sealed class Clock : TimeProvider { private long _ticks; public override long TimestampFrequency => TimeSpan.TicksPerSecond; public override long GetTimestamp() => Interlocked.Read(ref _ticks); public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks); }
    private sealed class Anchor : IDataSecureMonotonicAnchor
    {
        private readonly object _gate = new(); private readonly Dictionary<string, DataSecureCounterCheckpoint> _values = [];
        public bool HoldNext, IgnoreCancellation, FailRemoteOnce, ThrowOnCancellation; public TaskCompletionSource Started { get; } = Gate(); public TaskCompletionSource Release { get; } = Gate();
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_values.GetValueOrDefault(context)); }
        public async Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        {
            if (HoldNext && expected is not null)
            {
                HoldNext = false; using var registration = ThrowOnCancellation ? ct.Register(() => throw new IOException("Synthetic anchor cancellation callback.")) : default;
                Started.TrySetResult(); if (IgnoreCancellation) await Release.Task; else await Release.Task.WaitAsync(ct);
            }
            if (FailRemoteOnce && context.EndsWith(":2", StringComparison.Ordinal)) { FailRemoteOnce = false; return false; }
            if (!IgnoreCancellation) ct.ThrowIfCancellationRequested(); lock (_gate) { if (_values.GetValueOrDefault(context) != expected) return false; _values[context] = proposed; return true; }
        }
    }
}
