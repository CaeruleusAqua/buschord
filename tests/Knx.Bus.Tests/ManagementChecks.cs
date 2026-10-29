using Knx;
using Knx.Bus;

internal static class ManagementChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        void Check(string name, bool passed) => check(name, passed, null);
        Check("management memory read count is inline", Convert.ToHexString(ManagementApdu.ReadMemory(0x1234, 4)) == "02041234");
        Check("management property read preserves full APCI", Convert.ToHexString(ManagementApdu.ReadProperty(3, 13)) == "03D5030D1001"
            && ManagementApdu.Service([0x43, 0xD6]) == ManagementApdu.PropertyResponse);
        var target = new IndividualAddress(1, 1, 20);
        var source = new IndividualAddress(1, 0, 250);
        var control = new ManagementTransportFrame(source, target, [0xC2]).ToRequest();
        Check("transport control has one TPDU octet", Convert.ToHexString(control) == "1100BC6010FA111400C2");
        control[0] = 0x29;
        Check("transport control indication parses", ManagementTransportFrame.TryParseIndication(control, out var parsed)
            && parsed!.Source == source && parsed.Destination == target && parsed.Tpdu.SequenceEqual(new byte[] { 0xC2 }));
        Check("truncated transport control rejected", !ManagementTransportFrame.TryParseIndication(control.AsSpan(0, 9), out _));
        control[3] |= 0x80;
        Check("group telegram excluded from addressed management", !ManagementTransportFrame.TryParseIndication(control, out _));
        var rangeRejected = false;
        try { ManagementApdu.ReadMemory(0xFFFF, 2); } catch (ArgumentOutOfRangeException) { rangeRejected = true; }
        Check("memory read cannot wrap address space", rangeRejected);

        var fake = new ManagementPeer(source, target) { DuplicateResponses = true, ForeignFrames = true, WrongHeaders = true };
        await using (var client = new KnxManagementClient(fake, target, TimeSpan.FromMilliseconds(500)))
        {
            await client.ConnectAsync();
            for (var i = 0; i < 18; i++)
                Check($"property response strictly matched {i}", (await client.ReadPropertyAsync(0, 12)).SequenceEqual(new byte[] { 0, 0x83 }));
            Check("management sequences wrap independently", fake.RequestSequences.SequenceEqual(Enumerable.Range(0, 18).Select(i => (byte)(i & 15))));
            Check("memory response address and inline count matched", (await client.ReadMemoryAsync(0x1234, 3)).SequenceEqual(new byte[] { 1, 2, 3 }));
            var both = await Task.WhenAll(client.ReadPropertyAsync(0, 12), client.ReadPropertyAsync(0, 12));
            Check("concurrent requests are serialized", both.All(v => v.SequenceEqual(new byte[] { 0, 0x83 })));
        }
        Check("management session disconnects on disposal", fake.Disconnects == 1);
        Check("duplicate numbered responses acknowledged again", fake.Acks.Count > fake.RequestSequences.Count);
        Check("read session never emits property or memory writes", !fake.Requests.Any(a => ManagementApdu.Service(a) is 0x3D7 or 0x280));

        foreach (var failure in new[] { "no_ack", "no_response", "nak", "disconnect", "wrong_sequence", "cancel" })
        {
            var peer = new ManagementPeer(source, target) { Failure = failure };
            await using var client = new KnxManagementClient(peer, target, TimeSpan.FromMilliseconds(80));
            await client.ConnectAsync();
            using var cancel = new CancellationTokenSource();
            if (failure == "cancel") cancel.CancelAfter(15);
            var refused = false;
            try { await client.ReadPropertyAsync(0, 12, ct: cancel.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Threading.Channels.ChannelClosedException) { refused = true; }
            Check($"management {failure} fails without retry", refused && peer.Requests.Count == 1);
            refused = false;
            try { await client.ReadPropertyAsync(0, 12); } catch (IOException) { refused = true; }
            Check($"management {failure} invalidates the session", refused && peer.Requests.Count == 1);
        }

        for (var value = 0; value < 256; value++)
        {
            var enabled = ProgrammingModeCycle.StatusWithMode((byte)value, true);
            var disabled = ProgrammingModeCycle.StatusWithMode((byte)value, false);
            Check($"mode status preserves other bits and even parity {value}",
                (enabled & 0x7E) == (value & 0x7E) && (disabled & 0x7E) == (value & 0x7E)
                && (enabled & 1) == 1 && (disabled & 1) == 0
                && System.Numerics.BitOperations.PopCount(enabled) % 2 == 0
                && System.Numerics.BitOperations.PopCount(disabled) % 2 == 0);
        }
        var journal = Path.Combine(Path.GetTempPath(), "buschord-mode-tests-" + Guid.NewGuid().ToString("N"));
        var expected = new ManagementDeviceIdentity(target.ToString(), 0x0701, "0083", "008300000001", "0083000110");
        foreach (var failure in new[] { "none", "server_verify", "write_no_ack", "write_nak", "write_cancel", "restore_identity" })
        {
            var state = new ManagementPeerState { ServerVerify = failure == "server_verify" };
            var peers = new List<ManagementPeer>();
            using var cancel = new CancellationTokenSource();
            async Task<IManagementConnection> Open(CancellationToken token)
            {
                var peer = new ManagementPeer(source, target)
                {
                    State = state, Failure = peers.Count == 0 ? failure : null,
                    WrongIdentity = peers.Count != 0 && failure == "restore_identity",
                    AfterWrite = failure == "write_cancel" && peers.Count == 0 ? () => cancel.Cancel() : null
                };
                peers.Add(peer);
                var client = new KnxManagementClient(peer, target, TimeSpan.FromMilliseconds(100));
                await client.ConnectAsync(token);
                return new TestManagementConnection(client);
            }
            var result = await ProgrammingModeCycle.RunAsync(Open, expected, target.ToString(), journal, cancel.Token);
            var writes = peers.Sum(p => p.Requests.Count(a => ManagementApdu.Service(a) == 0x280));
            if (failure == "restore_identity")
                Check("recovery refuses a changed device identity", !result.RestoredAndVerified && writes == 1
                    && File.ReadAllText(result.JournalPath).Contains("recovery_failed"));
            else
                Check($"mode cycle {failure} restores through a fresh connection", result.RestoredAndVerified
                    && state.Status == 0 && peers.Count == 2 && writes == 2
                    && File.ReadAllText(result.JournalPath).Contains("restored_verified"));
            if (failure is "none" or "server_verify") Check("mode activation is read back before restoration", result.ActivatedAndVerified && result.Error is null);
            else Check($"mode cycle {failure} reports uncertainty", result.Error is not null);
        }
        foreach (var failure in new[] { "status_on", "invalid_parity", "initial_identity" })
        {
            var state = new ManagementPeerState { Status = failure == "status_on" ? (byte)0x81 : failure == "invalid_parity" ? (byte)0x80 : (byte)0 };
            var peer = new ManagementPeer(source, target) { State = state, WrongIdentity = failure == "initial_identity" };
            async Task<IManagementConnection> Open(CancellationToken token)
            {
                var client = new KnxManagementClient(peer, target, TimeSpan.FromMilliseconds(100));
                await client.ConnectAsync(token);
                return new TestManagementConnection(client);
            }
            var result = await ProgrammingModeCycle.RunAsync(Open, expected, target.ToString(), journal);
            Check($"mode cycle preflight {failure} sends no write", !result.ActivatedAndVerified && result.Error is not null
                && !peer.Requests.Any(a => ManagementApdu.Service(a) == 0x280));
        }
        var opens = 0;
        try
        {
            await ProgrammingModeCycle.RunAsync(_ => { opens++; throw new Exception(); }, expected, "1.1.21", journal);
        }
        catch (InvalidOperationException) { }
        Check("target confirmation mismatch refuses before opening a connection", opens == 0);
    }
}

internal sealed class TestManagementConnection(KnxManagementClient client) : IManagementConnection
{
    public KnxManagementClient Client => client;
    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal sealed class ManagementPeerState { public byte Status; public bool ServerVerify; }

internal sealed class ManagementPeer(IndividualAddress source, IndividualAddress target) : IManagementTransport
{
    private byte _responseSequence;
    public IndividualAddress AssignedAddress => source;
    public bool IsConnected => true;
    public event Action<ManagementTransportFrame>? FrameReceived;
    public bool DuplicateResponses { get; init; }
    public bool ForeignFrames { get; init; }
    public bool WrongHeaders { get; init; }
    public string? Failure { get; init; }
    public bool WrongIdentity { get; init; }
    public ManagementPeerState State { get; init; } = new();
    public Action? AfterWrite { get; init; }
    public List<byte[]> Requests { get; } = [];
    public List<byte> RequestSequences { get; } = [];
    public List<byte> Acks { get; } = [];
    public int Disconnects { get; private set; }

    public Task SendAsync(IndividualAddress destination, byte[] tpdu, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (destination != target) throw new IOException("Unexpected target.");
        if (tpdu.Length == 1)
        {
            if (tpdu[0] == 0x81) Disconnects++;
            if ((tpdu[0] & 0xC3) == 0xC2) Acks.Add(tpdu[0]);
            return Task.CompletedTask;
        }
        Requests.Add((byte[])tpdu.Clone());
        var sequence = (byte)((tpdu[0] >> 2) & 15);
        RequestSequences.Add(sequence);
        if (ManagementApdu.Service(tpdu) == 0x280)
        {
            if (tpdu.Length != 5 || tpdu[1] != 0x81 || tpdu[2] != 0 || tpdu[3] != 0x60)
                throw new IOException("Write outside the admitted status byte.");
            State.Status = tpdu[4];
            AfterWrite?.Invoke();
            if (Failure is "write_no_ack" or "write_cancel") return Task.CompletedTask;
            Emit([(byte)((Failure == "write_nak" ? 0xC3 : 0xC2) | (sequence << 2))]);
            if (State.ServerVerify) Respond([0x02, 0x41, 0, 0x60, State.Status]);
            return Task.CompletedTask;
        }
        if (Failure is "no_ack" or "cancel") return Task.CompletedTask;
        if (Failure == "nak") { Emit([(byte)(0xC3 | (sequence << 2))]); return Task.CompletedTask; }
        if (Failure == "disconnect") { Emit([0x81]); return Task.CompletedTask; }
        Emit([(byte)(0xC2 | (sequence << 2))]);
        if (Failure == "no_response") return Task.CompletedTask;
        byte[] response = ManagementApdu.Service(tpdu) switch
        {
            0x300 => [0x03, 0x40, 0x07, 0x01],
            ManagementApdu.PropertyRead => [0x03, 0xD6, tpdu[2], tpdu[3], tpdu[4], tpdu[5], .. Property(tpdu[3])],
            0x200 => [0x02, (byte)(0x40 | (tpdu[1] & 0x3F)), tpdu[2], tpdu[3], ..
                (tpdu[2] == 0 && tpdu[3] == 0x60 ? new byte[] { State.Status }
                    : Enumerable.Range(1, tpdu[1] & 0x3F).Select(i => (byte)i).ToArray())],
            _ => throw new IOException("Unexpected management request.")
        };
        if (ForeignFrames)
        {
            FrameReceived?.Invoke(new(new IndividualAddress(1, 1, 21), source, response));
            FrameReceived?.Invoke(new(target, new IndividualAddress(1, 0, 251), response));
        }
        if (WrongHeaders)
        {
            var wrong = (byte[])response.Clone();
            wrong[3] ^= 1;
            Respond(wrong);
        }
        if (Failure == "wrong_sequence") _responseSequence = 7;
        Respond(response);
        return Task.CompletedTask;
    }

    private void Respond(byte[] response)
    {
        response[0] = (byte)((response[0] & 3) | 0x40 | (_responseSequence << 2));
        Emit(response);
        if (DuplicateResponses) Emit((byte[])response.Clone());
        _responseSequence = (byte)((_responseSequence + 1) & 15);
    }

    private void Emit(byte[] tpdu) => FrameReceived?.Invoke(new(target, source, tpdu));
    private byte[] Property(byte pid) => pid switch
    {
        11 => [0, 0x83, 0, 0, 0, (byte)(WrongIdentity ? 2 : 1)],
        13 => [0, 0x83, 0, 1, 0x10],
        14 => [(byte)(State.ServerVerify ? 4 : 0)],
        _ => [0, 0x83]
    };
}
