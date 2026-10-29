using Knx;
using Knx.Bus;

internal static class IndividualAddressDiscoveryChecks
{
    private static readonly IndividualAddress Client = new(1, 1, 240), Device = new(1, 1, 7);
    private static readonly byte[] Serial = [0, 0x83, 1, 2, 3, 4];
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Case(string name, Func<Task> body)
        {
            try { await body().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine("PASS Address discovery " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL Address discovery " + name + ": " + error); }
        }
        void Require(bool value) { if (!value) throw new Exception("Observation invariant failed."); }
        Task<IndividualAddressObservation> Read(Peer peer) => IndividualAddressDiscovery.ReadProgrammingModeAsync(peer, Client, TimeSpan.FromMilliseconds(20));
        Task<IndividualAddressObservation> ReadSerial(Peer peer, ReadOnlyMemory<byte>? serial = null) => IndividualAddressDiscovery.ReadSerialAsync(peer, Client, serial ?? Serial, TimeSpan.FromMilliseconds(20));
        await Case("silence is no response, not proof of availability", async () =>
        { var peer = new Peer(); var result = await Read(peer); Require(result.State == AddressObservationState.NoResponse && result.Addresses.Count == 0 && peer.Subscribers == 0 && peer.Last!.Apci == Apci.IndividualAddressRead); });
        await Case("programming reply accepts its actual empty wire representation", async () =>
        {
            var telegram = Cemi.ParseLData(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0xE0, Device, 0, [1, 0x40])));
            var peer = new Peer { Send = p => p.Emit(telegram) }; var result = await Read(peer);
            Require(result.State == AddressObservationState.ResponsesObserved && result.Addresses.SequenceEqual(new[] { Device }) && result.ValidResponses == 1 && peer.Subscribers == 0);
        });
        await Case("duplicate addresses preserve raw counts without inventing unique devices", async () =>
        { var peer = new Peer { Send = p => { p.Emit(Reply()); p.Emit(Reply()); } }; var result = await Read(peer); Require(result.ValidResponses == 2 && result.Addresses.Count == 1); });
        await Case("multiple responding addresses remain explicit", async () =>
        { var peer = new Peer { Send = p => { p.Emit(Reply()); p.Emit(Reply(source: new(1, 1, 8))); } }; var result = await Read(peer); Require(result.State == AddressObservationState.MultipleAddresses && result.Addresses.Count == 2); });
        await Case("factory address is observable without making it a valid assignment", async () =>
        { var peer = new Peer { Send = p => p.Emit(Reply(source: new(ushort.MaxValue))) }; Require((await Read(peer)).Addresses.Single().Raw == ushort.MaxValue); });
        await Case("wrong destination and unrelated services are ignored", async () =>
        {
            var peer = new Peer { Send = p => { p.Emit(Reply(destination: new(1, 1, 239))); p.Emit(Reply(service: Apci.GroupValueResponse)); } };
            Require((await Read(peer)).State == AddressObservationState.NoResponse);
        });
        await Case("invalid source and reserved inline bits refuse the observation", async () =>
        {
            foreach (var reply in new[] { Reply(source: IndividualAddress.Zero), Reply(source: Client), Reply(source: new(1, 1, 255)), Reply(data: [1], bits: 6) })
            { var peer = new Peer { Send = p => p.Emit(reply) }; var result = await Read(peer); Require(result.State == AddressObservationState.MalformedResponse && result.MalformedResponses == 1); }
        });
        await Case("response work is bounded", async () =>
        { var peer = new Peer { Send = p => { for (var i = 0; i <= IndividualAddressDiscovery.MaximumResponses; i++) p.Emit(Reply()); } }; var result = await Read(peer); Require(result.State == AddressObservationState.ResponseLimitReached && result.ValidResponses == IndividualAddressDiscovery.MaximumResponses); });
        await Case("serial selector and complete reserved response fields are matched", async () =>
        {
            var peer = new Peer { Send = p => p.Emit(Reply(service: Apci.IndividualAddressSerialResponse, data: [.. Serial, 0, 0])) };
            var result = await ReadSerial(peer); Require(result.State == AddressObservationState.ResponsesObserved && result.QueriedSerial == Convert.ToHexString(Serial) && peer.Last!.Data.SequenceEqual(Serial));
        });
        await Case("a different serial cannot satisfy the query", async () =>
        { var peer = new Peer { Send = p => p.Emit(Reply(service: Apci.IndividualAddressSerialResponse, data: [1, 2, 3, 4, 5, 6, 0, 0])) }; Require((await ReadSerial(peer)).State == AddressObservationState.NoResponse); });
        await Case("truncated and nonzero reserved serial replies are malformed", async () =>
        {
            foreach (var data in new byte[][] { [.. Serial], [.. Serial, 0, 1], [.. Serial, 0, 0, 0] })
            { var peer = new Peer { Send = p => p.Emit(Reply(service: Apci.IndividualAddressSerialResponse, data: data)) }; Require((await ReadSerial(peer)).State == AddressObservationState.MalformedResponse); }
        });
        await Case("selector mutation cannot change an in-flight query", async () =>
        {
            var owned = Serial.ToArray(); var peer = new Peer { Send = p => p.Emit(Reply(service: Apci.IndividualAddressSerialResponse, data: [.. Serial, 0, 0])) };
            var reading = ReadSerial(peer, owned); Array.Fill(owned, (byte)255); var result = await reading;
            Require(result.QueriedSerial == Convert.ToHexString(Serial) && peer.Last!.Data.SequenceEqual(Serial));
        });
        await Case("invalid arguments and pre-cancellation never subscribe or send", async () =>
        {
            var peer = new Peer();
            try { await IndividualAddressDiscovery.ReadProgrammingModeAsync(peer, Client, TimeSpan.Zero); throw new Exception(); } catch (ArgumentOutOfRangeException) { }
            try { await IndividualAddressDiscovery.ReadSerialAsync(peer, Client, new byte[6], TimeSpan.FromMilliseconds(20)); throw new Exception(); } catch (ArgumentException) { }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await IndividualAddressDiscovery.ReadProgrammingModeAsync(peer, Client, TimeSpan.FromMilliseconds(20), cancellation.Token); throw new Exception(); } catch (OperationCanceledException) { }
            Require(peer.Sends == 0 && peer.Subscribers == 0);
        });
        await Case("cancellation joins a held send before releasing the subscription", async () =>
        {
            var peer = new Peer { Hold = true }; using var cancellation = new CancellationTokenSource();
            var reading = IndividualAddressDiscovery.ReadProgrammingModeAsync(peer, Client, TimeSpan.FromMilliseconds(20), cancellation.Token);
            var cancelled = false;
            try { await peer.Entered.Task; cancellation.Cancel(); Require(!reading.IsCompleted && peer.Subscribers == 1); }
            finally { peer.Release.TrySetResult(); try { await reading; } catch (OperationCanceledException) { cancelled = true; } }
            Require(cancelled && peer.Subscribers == 0);
        });
        await Case("send failure propagates and releases the subscription", async () =>
        {
            var peer = new Peer { Fail = true };
            try { await Read(peer); throw new Exception(); } catch (IOException) { }
            Require(peer.Subscribers == 0);
        });
        await Case("window closure rejects callbacks injected during external unsubscription", async () =>
        {
            var peer = new Peer { Send = p => p.Emit(Reply()), ReplyDuringRemove = Reply(source: new(1, 1, 8)) };
            var result = await Read(peer);
            Require(result.State == AddressObservationState.ResponsesObserved && result.ValidResponses == 1 && result.Addresses.Single() == Device && peer.Subscribers == 0);
        });
        return failures;
    }
    private static Telegram Reply(IndividualAddress? source = null, IndividualAddress? destination = null,
        Apci service = Apci.IndividualAddressResponse, byte[]? data = null, int? bits = null)
        => new() { Source = source ?? Device, Destination = destination ?? Client, Apci = service, Data = data ?? [], DataBits = bits };
    private sealed class Peer : IBusDiagnosticSession
    {
        private event Action<BusTelegram>? Received;
        public event Action<BusTelegram>? TelegramReceived
        {
            add => Received += value;
            remove
            {
                var captured = Received; Received -= value;
                if (ReplyDuringRemove is { } reply) captured?.Invoke(new() { Source = "synthetic-late", Telegram = reply });
            }
        }
        internal Telegram? ReplyDuringRemove;
        internal int Subscribers => Received?.GetInvocationList().Length ?? 0;
        internal int Sends; internal Telegram? Last; internal Action<Peer>? Send; internal bool Hold, Fail;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Emit(Telegram telegram) => Received?.Invoke(new() { Source = "synthetic", Telegram = telegram });
        public async Task SendAsync(Telegram telegram, CancellationToken ct = default)
        {
            Sends++; Last = telegram; Entered.TrySetResult();
            if (Hold) await Release.Task;
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Synthetic send failure.");
            Send?.Invoke(this);
        }
    }
}
