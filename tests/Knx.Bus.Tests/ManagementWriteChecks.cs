using Knx;
using Knx.Bus;

internal static class ManagementWriteChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool passed) => check(name, passed, null);
        var twelve = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var ten = twelve[..10];
        Check("memory write uses inline byte count and exact bytes",
            Convert.ToHexString(ManagementApdu.WriteMemory(0x1234, twelve)) == "028C12340102030405060708090A0B0C");
        Check("property write uses full APCI and twelve-bit start index",
            Convert.ToHexString(ManagementApdu.WriteProperty(3, 5, 0xABC, 2, new byte[] { 0x11, 0x22, 0x33, 0x44 }))
                == "03D703052ABC11223344");
        Check("maximum management writes fit standard frames",
            ManagementApdu.WriteMemory(0xFFF4, twelve).Length == 16
            && ManagementApdu.WriteProperty(3, 5, 0xFFF, 1, ten).Length == 16);
        foreach (var (name, action) in new (string, Action)[]
        {
            ("empty memory", () => ManagementApdu.WriteMemory(0, [])),
            ("oversized memory", () => ManagementApdu.WriteMemory(0, new byte[13])),
            ("wrapped memory", () => ManagementApdu.WriteMemory(0xFFFF, new byte[2])),
            ("empty property", () => ManagementApdu.WriteProperty(0, 1, 1, 1, [])),
            ("oversized property", () => ManagementApdu.WriteProperty(0, 1, 1, 1, new byte[11])),
            ("zero property count", () => ManagementApdu.WriteProperty(0, 1, 1, 0, ten)),
            ("oversized property count", () => ManagementApdu.WriteProperty(0, 1, 1, 16, ten)),
            ("oversized property index", () => ManagementApdu.WriteProperty(0, 1, 0x1000, 1, ten)),
            ("wrapped property range", () => ManagementApdu.WriteProperty(0, 1, 0xFFF, 2, ten)),
            ("property count field range", () => ManagementApdu.WriteProperty(0, 1, 0, 2, ten))
        })
        {
            var rejected = false;
            try { action(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check($"management write rejects {name}", rejected);
        }

        foreach (var verify in new[] { false, true })
        {
            var peer = new WritePeer { ServerVerify = verify, ForeignHeaders = verify };
            await using var client = NewClient(peer);
            await client.ConnectAsync();
            await client.WriteMemoryAsync(0x1234, twelve, verify);
            Check($"memory write serverVerify={verify} has exact bounded frame",
                Convert.ToHexString(peer.Requests.Single()) == "428C12340102030405060708090A0B0C");
            Check($"memory write serverVerify={verify} awaits only required response", peer.ResponseAcks == (verify ? 2 : 0));
            var property = await client.WritePropertyAsync(3, 5, 0xABC, 2, new byte[] { 0x11, 0x22, 0x33, 0x44 });
            Check($"property write matches data and preserves request sequence after memory verify={verify}",
                Convert.ToHexString(peer.Requests[1]) == "47D703052ABC11223344" && property.SequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            Check($"addressed writes never change destination verify={verify}", peer.Targets.All(t => t == WritePeer.Target));
        }

        foreach (var service in new[] { "memory", "property" })
        {
            foreach (var failure in new[] { "refused", "wrong_count", "wrong_data", "short_echo", "empty_response", "oversized_response", "partial_element", "no_response", "no_ack", "nak", "disconnect", "cancel" })
            {
                if (service == "property" && failure == "wrong_data") continue;
                if (service == "memory" && failure == "partial_element") continue;
                using var cancel = new CancellationTokenSource();
                var peer = new WritePeer { ServerVerify = true, Failure = failure,
                    AfterRequest = failure == "cancel" ? cancel.Cancel : null };
                await using var client = NewClient(peer);
                await client.ConnectAsync();
                async Task Write(CancellationToken ct = default)
                {
                    if (service == "memory") await client.WriteMemoryAsync(0x2345, new byte[] { 1, 2 }, true, ct);
                    else await client.WritePropertyAsync(3, 5, 0x123, 2, new byte[] { 1, 2 }, ct);
                }
                Check($"{service} write {failure} fails without retry", await FailsAsync(() => Write(cancel.Token)) && peer.Requests.Count == 1);
                Check($"{service} write {failure} invalidates the session", await FailsAsync(() => Write()) && peer.Requests.Count == 1);
            }
        }

        var controlProperty = new WritePeer { Failure = "wrong_data" };
        await using (var client = NewClient(controlProperty))
        {
            await client.ConnectAsync();
            var resultingState = await client.WritePropertyAsync(3, 5, 1, 1, new byte[] { 1 });
            Check("property control returns resulting state for explicit caller validation", resultingState.SequenceEqual(new byte[] { 0 })
                && controlProperty.Requests.Count == 1);
        }

        var variableControl = new WritePeer { PropertyResult = [2] };
        await using (var client = NewClient(variableControl))
        {
            await client.ConnectAsync();
            var state = await client.WritePropertyAsync(3, 5, 1, 1, ten);
            Check("ten-byte property control command accepts one-byte resulting state",
                state.SequenceEqual(new byte[] { 2 }) && variableControl.Requests.Count == 1
                && Convert.ToHexString(variableControl.Requests[0]) == "43D7030510010102030405060708090A");
        }

        foreach (var service in new[] { "memory", "property", "restart" })
        {
            var peer = new WritePeer();
            await using var client = NewClient(peer);
            await client.ConnectAsync();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Task Operation() => service switch
            {
                "memory" => client.WriteMemoryAsync(0x1000, new byte[] { 1 }, false, cancel.Token),
                "property" => client.WritePropertyAsync(3, 5, 1, 1, new byte[] { 1 }, cancel.Token),
                _ => client.RestartAsync(cancel.Token)
            };
            Check($"pre-cancelled {service} sends no command", await FailsAsync(Operation) && peer.Requests.Count == 0);
        }

        var malformed = new WritePeer();
        await using (var client = NewClient(malformed))
        {
            await client.ConnectAsync();
            var memoryRejected = false;
            var propertyRejected = false;
            try { await client.WriteMemoryAsync(0xFFFF, new byte[2], false); }
            catch (ArgumentOutOfRangeException) { memoryRejected = true; }
            try { await client.WritePropertyAsync(0, 1, 1, 1, new byte[11]); }
            catch (ArgumentOutOfRangeException) { propertyRejected = true; }
            await client.WriteMemoryAsync(0xFFFF, new byte[] { 1 }, false);
            Check("invalid public write arguments send nothing and preserve the session", memoryRejected && propertyRejected && malformed.Requests.Count == 1);
        }

        foreach (var reply in new[] { "no_ack", "disconnect", "nak", "restart_disconnect", "wrong_ack_disconnect", "memory_instead_of_ack" })
        {
            // Replies are emitted before the transport send returns; none establishes that the device restarted.
            var restartRacesPassed = true;
            for (var i = 0; i < 32; i++)
            {
                var peer = new WritePeer { Failure = reply };
                await using (var client = NewClient(peer))
                {
                    await client.ConnectAsync();
                    try { await client.RestartAsync(); }
                    catch { restartRacesPassed = false; }
                    restartRacesPassed &= peer.Requests.Count == 1 && Convert.ToHexString(peer.Requests[0]) == "4380";
                    restartRacesPassed &= await FailsAsync(() => client.ReadDescriptorAsync());
                }
                restartRacesPassed &= peer.Disconnects == 1 && peer.ResponseAcks == 0
                    && peer.SentFrames.Select(Convert.ToHexString).SequenceEqual(new[] { "80", "4380", "81" });
            }
            Check($"basic restart {reply} reports only one request sent and ends session", restartRacesPassed);
        }

        var alreadySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alreadySent.SetResult();
        var sequencedRestart = new WritePeer { RestartSendCompletion = alreadySent };
        await using (var client = NewClient(sequencedRestart))
        {
            await client.ConnectAsync();
            await client.WriteMemoryAsync(0x1000, new byte[] { 1 }, false);
            await client.RestartAsync();
            Check("basic restart preserves connected-data sequence without device ACK", Convert.ToHexString(sequencedRestart.Requests[1]) == "4780");
            Check("sent restart makes session terminal without a device reply", await FailsAsync(() => client.ReadDescriptorAsync()));
        }
        Check("disposing sent restart sends one cleanup disconnect", sequencedRestart.Disconnects == 1);

        foreach (var reply in new[] { "no_ack", "disconnect", "nak" })
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var peer = new WritePeer { Failure = reply, RestartSendCompletion = completion, AfterRequest = () => sent.SetResult() };
            await using var client = new KnxManagementClient(peer, WritePeer.Target, TimeSpan.FromSeconds(2));
            await client.ConnectAsync();
            var sending = client.RestartAsync();
            await sent.Task;
            await Task.Delay(20);
            Check($"restart {reply} cannot finish before transport send completion", !sending.IsCompleted);
            completion.SetResult();
            await sending;
            Check($"restart {reply} finishes at transport send without retry", peer.Requests.Count == 1 && peer.Disconnects == 0);
        }

        foreach (var failure in new[] { "send_error", "cancel" })
        {
            var peer = new WritePeer { Failure = failure };
            await using var client = NewClient(peer);
            await client.ConnectAsync();
            Exception? error = null;
            try { await client.RestartAsync(); } catch (Exception ex) { error = ex; }
            Check($"restart actual transport {failure} propagates", (failure == "send_error" ? error is IOException : error is OperationCanceledException)
                && peer.Requests.Count == 1 && peer.Disconnects == 0);
            Check($"restart {failure} is not retried", await FailsAsync(() => client.RestartAsync()) && peer.Requests.Count == 1);
            await client.DisposeAsync();
            Check($"restart {failure} cleanup disconnect is sent once", peer.Disconnects == 1 && peer.Requests.Count == 1);
        }

        foreach (var fail in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var peer = new WritePeer { Failure = "disconnect", RestartSendCompletion = completion, AfterRequest = () => sent.SetResult() };
            await using var client = new KnxManagementClient(peer, WritePeer.Target, TimeSpan.FromSeconds(2));
            await client.ConnectAsync();
            var sending = client.RestartAsync(cancel.Token);
            await sent.Task;
            await Task.Delay(20);
            if (fail) completion.SetException(new IOException("Synthetic restart send failure after disconnect."));
            else cancel.Cancel();
            Exception? error = null;
            try { await sending; } catch (Exception ex) { error = ex; }
            Check($"disconnect cannot hide outstanding transport {(fail ? "failure" : "cancellation")}",
                (fail ? error is IOException && error.Message == "Synthetic restart send failure after disconnect." : error is OperationCanceledException)
                && peer.Requests.Count == 1 && peer.Disconnects == 0);
            Check($"restart after disconnect and {(fail ? "failure" : "cancellation")} cannot be retried",
                await FailsAsync(() => client.RestartAsync()) && peer.Requests.Count == 1);
            await client.DisposeAsync();
            Check($"restart after disconnect and {(fail ? "failure" : "cancellation")} cleans up once", peer.Disconnects == 1);
        }

        var cleanupFailure = new WritePeer { Failure = "no_ack", RejectDisconnect = true };
        await using (var client = NewClient(cleanupFailure))
        {
            await client.ConnectAsync();
            await client.RestartAsync();
            await client.DisposeAsync();
            Check("restart cleanup failure does not repeat restart or hide successful transport send", cleanupFailure.Requests.Count == 1
                && cleanupFailure.Disconnects == 1 && cleanupFailure.SentFrames.Count == 3);
        }
    }

    private static KnxManagementClient NewClient(WritePeer peer) => new(peer, WritePeer.Target, TimeSpan.FromMilliseconds(100));

    private static async Task<bool> FailsAsync(Func<Task> operation)
    {
        try { await operation(); return false; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Threading.Channels.ChannelClosedException) { return true; }
    }

    private sealed class WritePeer : IManagementTransport
    {
        public static readonly IndividualAddress Target = new(1, 1, 20);
        public IndividualAddress AssignedAddress => new(1, 0, 250);
        public bool IsConnected => true;
        public event Action<ManagementTransportFrame>? FrameReceived;
        public bool ServerVerify { get; init; }
        public bool ForeignHeaders { get; init; }
        public string? Failure { get; init; }
        public byte[]? PropertyResult { get; init; }
        public TaskCompletionSource? RestartSendCompletion { get; init; }
        public bool RejectDisconnect { get; init; }
        public Action? AfterRequest { get; init; }
        public List<byte[]> Requests { get; } = [];
        public List<byte[]> SentFrames { get; } = [];
        public List<IndividualAddress> Targets { get; } = [];
        public int Disconnects { get; private set; }
        public int ResponseAcks { get; private set; }
        private byte _responseSequence;

        public Task SendAsync(IndividualAddress target, byte[] tpdu, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Targets.Add(target);
            SentFrames.Add((byte[])tpdu.Clone());
            if (target != Target) throw new IOException("Unexpected addressed target.");
            if (tpdu.Length == 1)
            {
                if (tpdu[0] == 0x81)
                {
                    Disconnects++;
                    if (RejectDisconnect) return Task.FromException(new IOException("Synthetic restart disconnect cleanup failure."));
                }
                if ((tpdu[0] & 0xC3) == 0xC2) ResponseAcks++;
                return Task.CompletedTask;
            }
            Requests.Add((byte[])tpdu.Clone());
            AfterRequest?.Invoke();
            if (Failure == "send_error") return Task.FromException(new IOException("Synthetic restart transport send failure."));
            if (RestartSendCompletion is not null && ManagementApdu.Service(tpdu) == 0x380)
            {
                if (Failure == "disconnect") Emit([0x81]);
                if (Failure == "nak") Emit([(byte)(0xC3 | (tpdu[0] & 0x3C))]);
                return RestartSendCompletion.Task.WaitAsync(ct);
            }
            if (Failure == "cancel" && ManagementApdu.Service(tpdu) == 0x380)
                return Task.FromCanceled(new CancellationToken(canceled: true));
            if (Failure is "no_ack" or "cancel") return Task.CompletedTask;
            var sequence = (tpdu[0] >> 2) & 15;
            if (Failure == "nak") { Emit([(byte)(0xC3 | (sequence << 2))]); return Task.CompletedTask; }
            if (Failure == "disconnect") { Emit([0x81]); return Task.CompletedTask; }
            if (Failure == "wrong_ack_disconnect")
            {
                Emit([(byte)(0xC2 | (((sequence + 1) & 15) << 2))]); Emit([0x81]); return Task.CompletedTask;
            }
            if (Failure == "memory_instead_of_ack") { Respond([0x02, 0x41, 0, 0x60, 0]); return Task.CompletedTask; }
            Emit([(byte)(0xC2 | (sequence << 2))]);
            if (ManagementApdu.Service(tpdu) == 0x380)
            {
                if (Failure == "restart_disconnect") Emit([0x81]);
                return Task.CompletedTask;
            }
            if (Failure == "no_response") return Task.CompletedTask;
            var memory = ManagementApdu.Service(tpdu) == 0x280;
            if (memory && !ServerVerify) return Task.CompletedTask;
            byte[] response = memory
                ? [0x02, (byte)(0x40 | (tpdu[1] & 0x3F)), .. tpdu[2..]]
                : [0x03, 0xD6, .. tpdu[2..]];
            if (!memory && PropertyResult is not null) response = [.. response[..6], .. PropertyResult];
            if (ForeignHeaders)
            {
                var other = (byte[])response.Clone(); other[3] ^= 1; Respond(other);
                FrameReceived?.Invoke(new(new IndividualAddress(1, 1, 21), AssignedAddress, response));
                FrameReceived?.Invoke(new(Target, new IndividualAddress(1, 0, 251), response));
            }
            if (Failure == "refused")
            {
                if (memory) { response[1] = 0x40; response = response[..4]; }
                else { response[4] &= 0x0F; response = response[..6]; }
            }
            if (Failure == "wrong_count")
            {
                if (memory) response[1] ^= 1;
                else response[4] ^= 0x10;
            }
            if (Failure == "wrong_data") response[^1] ^= 1;
            if (Failure == "short_echo") response = response[..^1];
            if (Failure == "empty_response") response = response[..(memory ? 4 : 6)];
            if (Failure == "oversized_response") response = [.. response[..(memory ? 4 : 6)], .. new byte[13]];
            if (Failure == "partial_element") response = [.. response[..6], 1, 2, 3];
            Respond(response);
            return Task.CompletedTask;
        }

        private void Respond(byte[] response)
        {
            response[0] = (byte)((response[0] & 3) | 0x40 | (_responseSequence << 2));
            _responseSequence = (byte)((_responseSequence + 1) & 15);
            Emit(response);
        }

        private void Emit(byte[] tpdu) => FrameReceived?.Invoke(new(Target, AssignedAddress, tpdu));
    }
}
