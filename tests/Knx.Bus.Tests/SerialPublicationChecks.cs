using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using System.Net;
using Knx.Bus.Connections;
using Knx.Bus.Security;

internal static class SerialPublicationChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--serial-publication-child") return Child(args);
        ThreadPool.GetMinThreads(out var workers, out var io); ThreadPool.SetMinThreads(Math.Max(workers, 80), io);
        using var state = new PrivateState(); var failures = 0;
        foreach (var (name, test) in args.Length == 1 && args[0] == "--stress" ? new (string, Func<string, Task>)[] { ("fresh 48-thread publication stress", ThreadStress) } : new (string, Func<string, Task>)[]
        {
            ("fresh 48-thread publication stress", ThreadStress),
            ("independent processes and threads observe one persisted serial", ProcessStress),
            ("held lease fails within bounds and killed owner releases its kernel lock", HeldAndKilled),
            ("factory serial wait stays off dispatcher and cancellation joins capability owners", FactoryCancellation),
            ("cancelled successful serial worker clears its owned result after join", SuccessfulWorkerCancellation),
            ("malformed existing identity is retained and failure releases the lease", Malformed),
            ("existing serial and initialization-lock links cannot redirect state", Links),
        })
        {
            if (OperatingSystem.IsWindows() && test == Links) { Console.WriteLine("SKIP SerialPublication Linux symbolic-link checks (no Windows privilege request)."); continue; }
            var root = Path.Combine(state.Root, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { await test(root); Console.WriteLine("PASS SerialPublication " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL SerialPublication " + name + ": " + error.Message); }
        }
        return failures == 0 ? 0 : 1;
    }
    private static async Task ThreadStress(string parent)
    {
        for (var round = 0; round < (OperatingSystem.IsWindows() ? 10 : 100); round++)
        {
            var root = Path.Combine(parent, "round-" + round); Directory.CreateDirectory(root); using var ready = new CountdownEvent(48); using var go = new ManualResetEventSlim();
            var pending = Enumerable.Range(0, 48).Select(_ => Task.Run(() => { ready.Signal(); go.Wait(); return new FileKnxClientSerialProvider(root).ReadOrCreate(); })).ToArray();
            Require(ready.Wait(TimeSpan.FromSeconds(10)), "Thread start barrier timed out."); go.Set(); var results = await Task.WhenAll(pending);
            Require(results.All(value => value.Length == 6 && value.SequenceEqual(results[0])) && File.ReadAllBytes(Path.Combine(root, "client.serial")).SequenceEqual(results[0]), "Concurrent callers observed different or incomplete identities (round " + round + ").");
            Require(Directory.GetFiles(root, "*.tmp").Length == 0, "A temporary publication file was retained.");
        }
    }
    private static async Task ProcessStress(string parent)
    {
        for (var round = 0; round < 4; round++)
        {
            var root = Path.Combine(parent, "round-" + round); Directory.CreateDirectory(root); var children = new List<Process>();
            try
            {
                for (var index = 0; index < 8; index++) children.Add(Start("serial", root, index.ToString()));
                await Until(() => Enumerable.Range(0, 8).All(index => File.Exists(Path.Combine(root, "ready-" + index))), TimeSpan.FromSeconds(15));
                File.WriteAllBytes(Path.Combine(root, "go"), [1]); await Task.WhenAll(children.Select(child => child.WaitForExitAsync()));
                Require(children.All(child => child.ExitCode == 0), "A serial child failed."); var persisted = File.ReadAllBytes(Path.Combine(root, "client.serial"));
                foreach (var index in Enumerable.Range(0, 8))
                {
                    var values = File.ReadAllBytes(Path.Combine(root, "result-" + index)); Require(values.Length == 48 && Enumerable.Range(0, 8).All(item => values.AsSpan(item * 6, 6).SequenceEqual(persisted)), "Concurrent process/thread identities diverged.");
                }
                Require(Directory.GetFiles(root, "*.tmp").Length == 0, "A process retained its temporary publication file.");
            }
            finally { foreach (var child in children) { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); child.Dispose(); } }
        }
    }
    private static async Task HeldAndKilled(string root)
    {
        using var child = Start("hold", root, "0");
        try
        {
            await Until(() => File.Exists(Path.Combine(root, "ready-0")), TimeSpan.FromSeconds(10)); var timer = Stopwatch.StartNew();
            await Reject(() => Task.Run(() => new FileKnxClientSerialProvider(root).ReadOrCreate()), KnxConnectionFailure.CredentialSourceUnavailable);
            Require(timer.Elapsed >= TimeSpan.FromSeconds(4.8) && timer.Elapsed < TimeSpan.FromSeconds(8), "Initialization acquisition was not bounded.");
            Require(!File.Exists(Path.Combine(root, "client.serial")) && Directory.GetFiles(root, "*.tmp").Length == 0, "A blocked initializer published or staged data.");
            child.Kill(true); await child.WaitForExitAsync(); var value = new FileKnxClientSerialProvider(root).ReadOrCreate();
            Require(value.Length == 6 && File.ReadAllBytes(Path.Combine(root, "client.serial")).SequenceEqual(value), "A killed owner's lease was not released.");
            Require(File.Exists(Path.Combine(root, "client.serial.lock")), "The shared lease path was removed and could split lock ownership.");
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
    }
    private static async Task Malformed(string root)
    {
        var path = Path.Combine(root, "client.serial"); File.WriteAllBytes(path, [1, 2]);
        await Reject(() => Task.Run(() => new FileKnxClientSerialProvider(root).ReadOrCreate()), KnxConnectionFailure.InvalidProfile);
        Require(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2 }), "Malformed state was silently replaced.");
        File.Delete(path); var first = new FileKnxClientSerialProvider(root).ReadOrCreate(); var second = new FileKnxClientSerialProvider(root).ReadOrCreate();
        Require(first.SequenceEqual(second) && !ReferenceEquals(first, second), "Failure retained its lease or buffers were aliased.");
    }
    private static async Task FactoryCancellation(string root)
    {
        using var child = Start("hold", root, "0"); using var cancellation = new CancellationTokenSource(); await using var ui = new AcceptanceDispatcher();
        var credentials = new Credentials(); var serial = new ObservedSerial(root); var leases = new GatewayLeaseCoordinator(Path.Combine(root, "gateway"));
        var profile = KnxConnectionProfile.IpSecure(new(IPAddress.Loopback, 3671), new(Path.Combine(root, "synthetic.knxkeys"), "Synthetic", new(1, 1, 0), 2, new(1, 1, 240), expectedSourceFingerprint: new string('A', 64)));
        var factory = new KnxConnectionFactory(leases, credentials, serial, _ => throw new Exception("No transport may be constructed during acquisition.")); Task<KnxGatewayOperation>? acquiring = null; Task? closing = null;
        try
        {
            await Until(() => File.Exists(Path.Combine(root, "ready-0")), TimeSpan.FromSeconds(10));
            acquiring = await ui.Invoke(() => factory.AcquireOperationAsync(profile, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(1));
            await serial.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(serial.ThreadId != ui.ThreadId && await ui.Invoke(() => true).WaitAsync(TimeSpan.FromSeconds(1)), "Serial initialization blocked the caller dispatcher.");
            await ui.Invoke(() => { cancellation.Cancel(); closing = CloseAsync(acquiring); return true; }).WaitAsync(TimeSpan.FromSeconds(1));
            Require(!acquiring.IsCompleted && !closing!.IsCompleted && credentials.Scope is not null && !Disposed(credentials.Scope), "Cancellation released the capability before its serial worker completed.");
            try { using var unexpected = leases.Acquire(profile.Gateway); throw new Exception("Gateway released before serial worker drain."); }
            catch (KnxConnectionException error) when (error.Failure == KnxConnectionFailure.Occupied) { }
            Require(await ui.Invoke(() => true).WaitAsync(TimeSpan.FromSeconds(1)), "Close waiting blocked the dispatcher.");
            await Cancelled(acquiring); await closing!.WaitAsync(TimeSpan.FromSeconds(2));
            Require(Disposed(credentials.Scope!), "Cancelled acquisition retained its credential capability after join.");
            using var released = leases.Acquire(profile.Gateway);
        }
        finally
        {
            if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync();
            if (acquiring is not null) try { var operation = await acquiring; await operation.DisposeAsync(); } catch (OperationCanceledException) { } catch (KnxConnectionException) { }
        }
        static async Task CloseAsync(Task<KnxGatewayOperation> acquisition) { try { await using var operation = await acquisition; } catch (OperationCanceledException) { } }
        static bool Disposed(KnxSecureCredentialScope scope) { try { using var session = scope.CreateSession(new byte[6]); return false; } catch (ObjectDisposedException) { return true; } }
    }
    private static async Task Links(string root)
    {
        if (OperatingSystem.IsWindows()) return; // Linux link validation is executed natively; no Windows privilege request.
        var foreign = Path.Combine(root, "outside"); File.WriteAllBytes(foreign, [9, 9]);
        var serialRoot = Path.Combine(root, "serial-link"); Directory.CreateDirectory(serialRoot); File.CreateSymbolicLink(Path.Combine(serialRoot, "client.serial"), foreign);
        await Reject(() => Task.Run(() => new FileKnxClientSerialProvider(serialRoot).ReadOrCreate()), KnxConnectionFailure.CredentialSourceUnavailable);
        var lockRoot = Path.Combine(root, "lock-link"); Directory.CreateDirectory(lockRoot); File.CreateSymbolicLink(Path.Combine(lockRoot, "client.serial.lock"), foreign);
        await Reject(() => Task.Run(() => new FileKnxClientSerialProvider(lockRoot).ReadOrCreate()), KnxConnectionFailure.CredentialSourceUnavailable);
        var dangling = Path.Combine(root, "dangling"); Directory.CreateDirectory(dangling); var absent = Path.Combine(root, "must-not-exist"); File.CreateSymbolicLink(Path.Combine(dangling, "client.serial"), absent);
        await Reject(() => Task.Run(() => new FileKnxClientSerialProvider(dangling).ReadOrCreate()), KnxConnectionFailure.CredentialSourceUnavailable);
        Require(File.ReadAllBytes(foreign).SequenceEqual(new byte[] { 9, 9 }) && !File.Exists(absent), "A link target was modified.");
    }
    private static async Task SuccessfulWorkerCancellation(string root)
    {
        using var cancellation = new CancellationTokenSource(); var credentials = new Credentials(); var serial = new ControlledSerial(); var leases = new GatewayLeaseCoordinator(root);
        var profile = KnxConnectionProfile.IpSecure(new(IPAddress.Loopback, 3671), new(Path.Combine(root, "synthetic.knxkeys"), "Synthetic", new(1, 1, 0), 2, new(1, 1, 240), expectedSourceFingerprint: new string('A', 64)));
        var acquiring = new KnxConnectionFactory(leases, credentials, serial).AcquireOperationAsync(profile, cancellation.Token);
        try
        {
            await serial.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); Require(!acquiring.IsCompleted, "Cancellation abandoned a running serial worker.");
            serial.Release.TrySetResult(); await Cancelled(acquiring); Require(serial.Owned.All(value => value == 0), "Cancelled successful serial result was not cleared.");
            using var released = leases.Acquire(profile.Gateway);
        }
        finally { serial.Release.TrySetResult(); try { await acquiring; } catch (OperationCanceledException) { } }
    }
    private static int Child(string[] args)
    {
        var mode = args[1]; var root = args[2]; var id = args[3];
        if (mode == "hold")
        {
            using var held = new FileStream(Path.Combine(root, "client.serial.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.WriteAllBytes(Path.Combine(root, "ready-" + id), [1]); Thread.Sleep(TimeSpan.FromSeconds(30)); return 0;
        }
        File.WriteAllBytes(Path.Combine(root, "ready-" + id), [1]); var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "go"))) { if (clock.Elapsed > TimeSpan.FromSeconds(20)) return 3; Thread.Sleep(5); }
        var values = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new FileKnxClientSerialProvider(root).ReadOrCreate()))).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(root, "result-" + id), values.SelectMany(value => value).ToArray()); return 0;
    }
    private static Process Start(string mode, string root, string id)
    {
        var executable = Environment.ProcessPath!; var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { "--serial-publication-child", mode, root, id }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception("Child process did not start.");
    }
    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > timeout) throw new Exception("Child readiness timed out."); await Task.Delay(10); } }
    private static async Task Reject(Func<Task> action, KnxConnectionFailure failure)
    { try { await action(); } catch (KnxConnectionException error) when (error.Failure == failure) { return; } throw new Exception("Expected " + failure); }
    private static async Task Cancelled(Task action) { try { await action; } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation after serial drain."); }
    private sealed class Credentials : IKnxSecureCredentialProvider
    {
        public KnxSecureCredentialScope? Scope;
        public Task<KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? source, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Scope = new(reference.HostAddress, reference.TunnelAddress, reference.UserId, reference.ExpectedSourceFingerprint!, new byte[16], Enumerable.Repeat((byte)0x11, 16).ToArray())); }
    }
    private sealed class ObservedSerial(string root) : IKnxClientSerialProvider
    {
        public int ThreadId; public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] ReadOrCreate() { ThreadId = Environment.CurrentManagedThreadId; Started.TrySetResult(); return new FileKnxClientSerialProvider(root).ReadOrCreate(); }
    }
    private sealed class ControlledSerial : IKnxClientSerialProvider
    {
        public readonly byte[] Owned = [1, 2, 3, 4, 5, 6];
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] ReadOrCreate() { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return Owned; }
    }
    private sealed class AcceptanceDispatcher : SynchronizationContext, IAsyncDisposable
    {
        private readonly BlockingCollection<Action> _queue = new(); private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;
        public AcceptanceDispatcher() { _thread = new(() => { SetSynchronizationContext(this); foreach (var action in _queue.GetConsumingEnumerable()) action(); }) { IsBackground = true }; _thread.Start(); }
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public Task<T> Invoke<T>(Func<T> action) { var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); _queue.Add(() => { try { result.TrySetResult(action()); } catch (Exception error) { result.TrySetException(error); } }); return result.Task; }
        public async ValueTask DisposeAsync() { _queue.CompleteAdding(); await Task.Run(_thread.Join); _queue.Dispose(); }
    }
    private sealed class PrivateState : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("BusChord.Serial.Acceptance.").FullName;
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Path.GetFileName(absolute).StartsWith("BusChord.Serial.Acceptance.", StringComparison.Ordinal)
                || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new Exception("Refusing an unrecognized cleanup target.");
            Directory.Delete(absolute, true);
        }
    }
}
