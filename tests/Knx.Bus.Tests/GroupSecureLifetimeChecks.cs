using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

internal static class GroupSecureLifetimeChecks
{
    private static readonly IndividualAddress Local = new(0x11F0), Peer = new(0x1101);
    private static readonly GroupAddress Group = new(0x0A01);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var mode in new[] { "group context", "group processor", "individual processor with group policy" })
        {
            try { await CallbackFailureAsync(mode).WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS GroupSecureLifetime " + mode); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL GroupSecureLifetime " + mode + ": " + ex); }
        }
        return failures;
    }

    private static async Task CallbackFailureAsync(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-group-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var anchor = new ThrowingAnchor();
        var identity = new DataSecureStateIdentity("Synthetic lifetime installation", "ordinary-v1", Local);
        var integrity = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        DataSecureCounterStore? store = null; IAsyncDisposable? owner = null; Task<byte[]>? sending = null;
        var ownedBuffers = new List<byte[]>();
        try
        {
            store = await DataSecureCounterStore.InitializeAsync(Path.Combine(directory, "state.bin"), identity, integrity,
                new(10, new Dictionary<IndividualAddress, ulong> { [Peer] = 0 }), anchor);
            var context = new KnxDataSecureGroupContext(store, identity,
                [new(Group, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), KnxDataProtection.AuthenticationAndConfidentiality, [Peer])]);
            var groups = (IDictionary)Field(context, "_groups");
            foreach (var binding in groups.Values)
                ownedBuffers.Add((byte[])binding!.GetType().GetProperty("Key")!.GetValue(binding)!);
            owner = context;
            ICemiSecurityProcessor? processor = null;
            KnxDataSecureCemiProcessor? groupProcessor = null;
            if (mode != "group context")
            {
                groupProcessor = new(context); processor = groupProcessor; owner = groupProcessor;
                if (mode == "individual processor with group policy")
                {
                    var individual = new KnxIndividualSecureContext(store, KnxIndividualSecureCounterBinding.PointToPoint(identity),
                        Peer, Enumerable.Range(96, 16).Select(i => (byte)i).ToArray(), 64);
                    ownedBuffers.Add((byte[])Field(individual, "_key"));
                    var outer = new KnxIndividualSecureCemiProcessor(individual, groupProcessor);
                    processor = outer; owner = outer;
                }
            }
            var frame = Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataReq, 0xB0, 0xE0, Local, Group.Raw, [0, 0x80, 0x2A]));
            if (processor is not null)
            {
                // Keep an actual prepared confirmation's plaintext in the owned policy too.
                _ = await processor.PrepareOutgoingAsync(frame, Local, CancellationToken.None);
                foreach (var prepared in (IEnumerable)Field(groupProcessor!, "_prepared"))
                    ownedBuffers.Add((byte[])prepared!.GetType().GetProperty("PlainApdu")!.GetValue(prepared)!);
            }
            Require(ownedBuffers.All(buffer => buffer.Any(value => value != 0)), "Fixture has no live owned key/plaintext buffers.");
            anchor.HoldNext = true;
            sending = processor is null ? context.ProtectCemiAsync(frame) : processor.PrepareOutgoingAsync(frame, Local, CancellationToken.None);
            await anchor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Before the fix this call throws synchronously, stranding the already
            // published completion forever; the public anchor callback is the trigger.
            var first = owner.DisposeAsync().AsTask();
            var second = owner.DisposeAsync().AsTask();
            Require(ReferenceEquals(first, second) && !first.IsCompleted,
                "Concurrent disposal must share unfinished cleanup while anchor work is held.");
            Require(ownedBuffers.All(buffer => buffer.Any(value => value != 0)), "Owned buffers were cleared before work joined.");
            anchor.Release.TrySetResult();
            try { await sending; throw new InvalidOperationException("Cancelled group send released a ciphertext."); }
            catch (OperationCanceledException) { }
            await CancellationFailureAsync(first); await CancellationFailureAsync(second);
            Require(ownedBuffers.All(buffer => buffer.All(value => value == 0)), "Disposal failure skipped owned key/plaintext cleanup.");
            var repeated = owner.DisposeAsync().AsTask();
            Require(ReferenceEquals(first, repeated) && repeated.IsCompleted, "Repeated disposal did not retain completed shared cleanup.");
            await CancellationFailureAsync(repeated);
            try { _ = context.Groups; throw new InvalidOperationException("Disposed group state remained accessible."); }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            anchor.Release.TrySetResult();
            if (sending is not null) try { await sending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (store is not null) await store.DisposeAsync();
            foreach (var buffer in ownedBuffers) CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(integrity);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static object Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static async Task CancellationFailureAsync(Task closing)
    {
        try { await closing.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.Any(inner => inner is IOException)) { return; }
        throw new InvalidOperationException("Joined disposal did not report the anchor cancellation failure.");
    }
    private sealed class ThrowingAnchor : IDataSecureMonotonicAnchor
    {
        private readonly Dictionary<string, DataSecureCounterCheckpoint> _values = [];
        private readonly object _gate = new();
        public bool HoldNext;
        public TaskCompletionSource Started { get; } = Gate();
        public TaskCompletionSource Release { get; } = Gate();
        public Task<DataSecureCounterCheckpoint?> ReadAsync(string context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_values.GetValueOrDefault(context)); }
        public async Task<bool> TryAdvanceAsync(string context, DataSecureCounterCheckpoint? expected, DataSecureCounterCheckpoint proposed, CancellationToken ct)
        {
            if (HoldNext && expected is not null)
            {
                HoldNext = false;
                using var registration = ct.Register(() => throw new IOException("Synthetic public anchor cancellation callback."));
                Started.TrySetResult(); await Release.Task.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_values.GetValueOrDefault(context) != expected) return false;
                _values[context] = proposed; return true;
            }
        }
    }
}
