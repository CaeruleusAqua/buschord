using Knx;
using Knx.Bus;

internal static class IndividualAddressWireChecks
{
    internal static int Run()
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool value)
        {
            checks++; if (!value) failures++;
            Console.WriteLine((value ? "PASS " : "FAIL ") + "Address wire " + name);
        }
        Check("programming-mode read uses broadcast/system priority",
            Convert.ToHexString(Management.IndividualAddressRead()) == "1100B0E000000000010100");
        Check("new address belongs in the broadcast write payload",
            Convert.ToHexString(Management.IndividualAddressWrite(new(1, 2, 3))) == "1100B0E0000000000300C01203");
        foreach (var code in new[] { 0x3DC, 0x3DD, 0x3DE, 0x3D5, 0x3F1 })
        {
            var request = Cemi.LDataRequest(new Telegram { Source = new(1, 1, 240), Destination = new(1, 1, 7), Apci = (Apci)code, Data = [1, 2] });
            var raw = Cemi.ParseLDataFrame(request);
            Check("ten-bit service retains every APCI bit " + code.ToString("X"), raw.Apdu.SequenceEqual(new byte[] { 3, (byte)code, 1, 2 }));
            var supplied = Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xBC, 0x60, new(1, 1, 7), 0x11F0, [3, (byte)code, 1, 2]));
            var decoded = Cemi.ParseLData(supplied);
            Check("independent raw service decodes without truncation " + code.ToString("X"), (int)decoded.Apci == code && decoded.Data.SequenceEqual(new byte[] { 1, 2 }) && decoded.DataBits is null);
            var empty = Cemi.ParseLData(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xBC, 0x60, new(1, 1, 7), 0x11F0, [3, (byte)code])));
            Check("service low bits are never inline payload " + code.ToString("X"), (int)empty.Apci == code && empty.Data.Length == 0 && empty.DataBits is null);
        }
        var refused = false;
        try { Cemi.LDataRequest(new Telegram { Source = new(1, 1, 240), Destination = new(1, 1, 7), Apci = (Apci)0x3DC, Data = [1], DataBits = 6 }); }
        catch (ArgumentException) { refused = true; }
        Check("ten-bit service cannot be overwritten by inline data", refused);
        byte[] serial = [0, 0x83, 1, 2, 3, 4];
        Check("serial read golden includes six exact bytes", Convert.ToHexString(Cemi.LDataRequest(Management.IndividualAddressSerialReadTelegram(serial)))
            == "1100B0E0000000000703DC008301020304");
        Check("serial write golden includes address and four reserved bytes", Convert.ToHexString(Cemi.LDataRequest(Management.IndividualAddressSerialWriteTelegram(serial, new(1, 2, 3))))
            == "1100B0E0000000000D03DE008301020304120300000000");
        foreach (var priority in Enum.GetValues<TelegramPriority>())
        {
            var telegram = new Telegram { Source = new(1, 1, 240), Destination = IndividualAddress.Zero, IsGroupAddress = true,
                Priority = priority, Apci = Apci.IndividualAddressRead, Data = [] };
            var raw = Cemi.LDataRequest(telegram.WithSource(new(1, 1, 241)));
            var decoded = Cemi.ParseLData(raw);
            Check("priority survives source substitution and frame roundtrip " + priority,
                raw[2] == new byte[] { 0xB0, 0xB4, 0xB8, 0xBC }[(int)priority] && decoded.Priority == priority && decoded.Source.Raw == 0x11F1);
        }
        foreach (var bad in new byte[][] { [], [1, 2, 3, 4, 5], new byte[6], [255, 255, 255, 255, 255, 255], [1, 2, 3, 4, 5, 6, 7] })
        {
            var readRejected = false; var writeRejected = false;
            try { Management.IndividualAddressSerialReadTelegram(bad); } catch (ArgumentException) { readRejected = true; }
            try { Management.IndividualAddressSerialWriteTelegram(bad, new(1, 1, 7)); } catch (ArgumentException) { writeRejected = true; }
            Check("placeholder or wrong-length serial cannot target a read/write " + Convert.ToHexString(bad), readRejected && writeRejected);
        }
        Check("ordinary telegrams retain the existing low priority default", new Telegram { Source = IndividualAddress.Zero, Destination = new(1), Data = [] }.Priority == TelegramPriority.Low);
        foreach (var unsupported in new[] { 0x105, 0x1C8 })
        {
            var rejected = false;
            try { Cemi.LDataRequest(new Telegram { Source = IndividualAddress.Zero, Destination = new(1), Apci = (Apci)unsupported, Data = [] }); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check("unsupported APCI family cannot silently lose service bits " + unsupported.ToString("X"), rejected);
        }
        foreach (var outside in new[] { -1, 0x400, 0x10000, 0x10080, int.MaxValue })
        {
            var rejected = false;
            try { Cemi.LDataRequest(new Telegram { Source = IndividualAddress.Zero, Destination = new(1), Apci = (Apci)outside, Data = [] }); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check("out-of-range service cannot wrap into a group read/write " + outside.ToString("X"), rejected);
        }
        Console.WriteLine($"Individual address wire: {checks} checks, {failures} failures.");
        return failures;
    }

    internal static async Task<int> RunAsync()
    {
        var failures = Run();
        var diagnostic = new DiagnosticPeer();
        var scan = await Knx.Bus.Diagnostics.ScanBusAsync(diagnostic, new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(20), Concurrency = 1 });
        var valid = diagnostic.ReadBroadcastCorrect && diagnostic.SendCount == 2 && diagnostic.Subscribers == 0
            && scan.Responders.SequenceEqual(new[] { new IndividualAddress(1, 1, 7) })
            && scan.Devices.Single().Found;
        Console.WriteLine((valid ? "PASS " : "FAIL ") + "Address wire diagnostics use the same broadcast and release subscriptions");
        return failures + (valid ? 0 : 1);
    }

    private sealed class DiagnosticPeer : IBusDiagnosticSession
    {
        private event Action<BusTelegram>? Received;
        public event Action<BusTelegram>? TelegramReceived { add => Received += value; remove => Received -= value; }
        internal int Subscribers => Received?.GetInvocationList().Length ?? 0;
        internal int SendCount;
        internal bool ReadBroadcastCorrect;
        public Task SendAsync(Telegram telegram, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); SendCount++;
            if (telegram.Apci == Apci.IndividualAddressRead)
            {
                ReadBroadcastCorrect = telegram.IsGroupAddress && telegram.Destination.Raw == 0 && telegram.Priority == TelegramPriority.System;
                if (!ReadBroadcastCorrect) return Task.CompletedTask;
            }
            Received?.Invoke(new() { Source = "synthetic", Telegram = new() { Source = new(1, 1, 7), Destination = new(1, 1, 240),
                Apci = telegram.Apci == Apci.IndividualAddressRead ? Apci.IndividualAddressResponse : Apci.DeviceDescriptorResponse,
                Data = telegram.Apci == Apci.IndividualAddressRead ? [] : [7, 1] } });
            return Task.CompletedTask;
        }
    }
}
