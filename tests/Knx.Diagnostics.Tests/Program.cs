using Knx;
using Knx.Bus;

// ============ Self-Checks fuer Bus-Diagnose ohne Hardware ============
// Fake-Session statt UDP: Scan, Descriptor-Parsing, Adressabgleich, Statistik-Fenster.

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

// ---- 1. Scan: Erfolg, Timeout pro Adresse, begrenzte Laufzeit ----
{
    var session = new FakeSession()
        .Device(new IndividualAddress(1, 1, 5), 0x07B0)
        .Device(new IndividualAddress(1, 1, 6), 0x0010);

    var scan = await Diagnostics.ScanAsync(session,
        [new IndividualAddress(1, 1, 5), new IndividualAddress(1, 1, 6), new IndividualAddress(1, 1, 7)],
        new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(50) });

    Check("scan: eine Beobachtung je Adresse", scan.Devices.Count == 3, scan.Devices.Count.ToString());
    Check("scan: zwei Geraete gefunden", scan.Devices.Count(d => d.Found) == 2);
    Check("scan: Masken gelesen",
        scan.Devices[0].Descriptor?.MaskVersion == 0x07B0
        && scan.Devices[1].Descriptor?.MaskVersion == 0x0010);
    Check("scan: Antwortadressen", scan.Devices.Select(d => d.Address).SequenceEqual(
        [new IndividualAddress(1, 1, 5), new IndividualAddress(1, 1, 6), new IndividualAddress(1, 1, 7)]));
    Check("scan: Timeout je Adresse gemeldet",
        scan.Failures.Count == 1 && scan.Devices[2].Error?.Contains("no DeviceDescriptorResponse") == true,
        scan.Devices[2].Error);
    Check("scan: Laufzeit begrenzt", scan.Elapsed < TimeSpan.FromSeconds(2), scan.Elapsed.ToString());
    Check("scan: Descriptor-Read als Einzeladresse", session.Sends(Apci.DeviceDescriptorRead) == 3);
}

// ---- 2. Begrenzte Wiederholungen: keine unbegrenzten Retries ----
{
    var silent = new FakeSession { Silent = true };
    var scan = await Diagnostics.ScanAsync(silent, [new IndividualAddress(1, 1, 5)],
        new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(10), Attempts = 99 });

    Check("retry: auf ScanOptions.MaxAttempts begrenzt",
        silent.Sends(Apci.DeviceDescriptorRead) == ScanOptions.MaxAttempts,
        silent.Sends(Apci.DeviceDescriptorRead).ToString());
    Check("retry: Fehler bleibt am Ergebnis", !scan.Devices[0].Found && scan.Devices[0].Error is not null);
}

// ---- 3. Device-Descriptor: Mask-Version, Echo-Bytes, Kurz-Payload ----
{
    var mask = DeviceDescriptor.Parse(0x00, [0x07, 0xB0]);
    Check("descriptor: Mask-Version", mask.MaskVersion == 0x07B0);
    Check("descriptor: Maskenname", mask.MaskName == "System B (BCU 2)", mask.MaskName);
    Check("descriptor: Beschreibung", mask.Describe().Contains("0x07B0") && mask.Describe().Contains("System B"));

    var echoed = DeviceDescriptor.Parse(0x00, [0x00, 0x40, 0x01, 0x01]);
    Check("descriptor: Echo-Typbyte wird verworfen",
        echoed.MaskVersion == 0x4001 && echoed.Bytes.Length == 3, echoed.Describe());

    var shortPayload = DeviceDescriptor.Parse(0x01, [0x40]);
    Check("descriptor: kein Mask-Wert bei Typ 1",
        shortPayload.MaskVersion is null && shortPayload.Describe().Contains("no mask version"));

    Check("descriptor: unbekannte Maske", MaskVersions.Describe(0x1234) == "unknown mask"
        && !MaskVersions.IsKnown(0x1234) && MaskVersions.IsKnown(0x07B0));
}

// ---- 4. Zeilen-Scan: Filter, doppelte Adresse, Abgleich mit Erwartung ----
{
    var session = new FakeSession()
        .Device(new IndividualAddress(1, 1, 5), 0x07B0)
        .Responder(new IndividualAddress(1, 1, 5)) // zweites Geraet mit derselben Adresse
        .Device(new IndividualAddress(1, 2, 5), 0x0025);

    var scan = await Diagnostics.ScanLineAsync(session, 1, 1,
        new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(30) });

    Check("line scan: nur Geraete der Zeile",
        scan.Devices.Select(d => d.Address).SequenceEqual([new IndividualAddress(1, 1, 5)]),
        string.Join(", ", scan.Devices.Select(d => d.Address)));
    Check("line scan: alle Antworten gesammelt", scan.Responders.Count == 3, scan.Responders.Count.ToString());

    var duplicate = Diagnostics.CheckAddresses(scan, [new IndividualAddress(1, 1, 5)]);
    Check("address check: Dublette erkannt",
        duplicate.Duplicates.SequenceEqual([new IndividualAddress(1, 1, 5)]),
        string.Join(", ", duplicate.Duplicates));
    Check("address check: nichts fehlend/unerwartet",
        duplicate.Missing.Count == 0 && duplicate.Unexpected.Count == 0);
    Check("address check: nicht sauber", !duplicate.Clean);

    var incomplete = Diagnostics.CheckAddresses(scan,
        [new IndividualAddress(1, 1, 5), new IndividualAddress(1, 1, 9)]);
    Check("address check: fehlende Adresse",
        incomplete.Missing.SequenceEqual([new IndividualAddress(1, 1, 9)]));
    Check("address check: unerwartete Adresse",
        Diagnostics.CheckAddresses(scan, [new IndividualAddress(1, 1, 5), new IndividualAddress(1, 1, 9)])
            .Unexpected.Count == 0);

    var busScan = await Diagnostics.ScanBusAsync(new FakeSession().Device(new IndividualAddress(1, 1, 5), 0x0020),
        new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(30) });
    var unexpected = Diagnostics.CheckAddresses(busScan, [new IndividualAddress(1, 1, 9)]);
    Check("address check: unerwartete Adresse im Bus-Scan",
        unexpected.Unexpected.SequenceEqual([new IndividualAddress(1, 1, 5)])
        && unexpected.Missing.SequenceEqual([new IndividualAddress(1, 1, 9)]));
}

// ---- 5. Statistik: Fenster, Quellen, Fehler-/Ack-Zaehler ----
{
    var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var statistics = new BusStatistics(clock: () => now);

    BusTelegram TelegramFrom(IndividualAddress source, DateTimeOffset at) => new()
    {
        Telegram = new Telegram
        {
            Source = source,
            Destination = new IndividualAddress(0xFFFF),
            Apci = Apci.GroupValueWrite,
            Data = [0x01],
        },
        Timestamp = at,
        Source = "test",
    };

    statistics.Add(TelegramFrom(new IndividualAddress(1, 1, 5), now.AddSeconds(-3)));
    statistics.Add(TelegramFrom(new IndividualAddress(1, 1, 6), now.AddSeconds(-0.5)));
    statistics.Add(TelegramFrom(new IndividualAddress(1, 1, 5), now.AddSeconds(-0.2)));
    statistics.AddAck();
    statistics.AddError(BusErrorKind.Timeout);

    var minute = statistics.Snapshot(TimeSpan.FromMinutes(1));
    Check("statistics: Telegramme im Minutenfenster", minute.Telegrams == 3, minute.Telegrams.ToString());
    Check("statistics: pro Quelle",
        minute.Sources.Count == 2 && minute.Sources[0].Source == "1.1.5" && minute.Sources[0].Telegrams == 2,
        string.Join(", ", minute.Sources.Select(s => $"{s.Source}={s.Telegrams}")));
    Check("statistics: Ack- und Fehlerzaehler",
        minute.Acks == 1 && minute.Errors == 1 && minute.ErrorsOf(BusErrorKind.Timeout) == 1);
    Check("statistics: Raten pro Sekunde/Minute",
        Math.Abs(minute.PerSecond - 3.0 / 60) < 1e-9 && Math.Abs(minute.PerMinute - 3.0) < 1e-9,
        $"{minute.PerSecond}/{minute.PerMinute}");

    var second = statistics.Snapshot(TimeSpan.FromSeconds(1));
    Check("statistics: Sekundenfenster", second.Telegrams == 2 && second.Acks == 1 && second.Errors == 1,
        $"{second.Telegrams}/{second.Acks}/{second.Errors}");

    var empty = statistics.Snapshot(TimeSpan.FromMilliseconds(100));
    Check("statistics: leeres Fenster", empty.Telegrams == 0 && empty.Sources.Count == 0);

    statistics.Reset();
    Check("statistics: Reset", statistics.Count == 0 && statistics.Snapshot().Telegrams == 0);
}

// ---- 6. Fehlerfortpflanzung aus der Session ----
{
    var session = new FakeSession { SendError = new IOException("socket closed") };
    var scan = await Diagnostics.ScanAsync(session, [new IndividualAddress(1, 1, 5)],
        new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(10) });

    Check("error: Sendefehler landet am Ergebnis", scan.Devices[0].Error == "socket closed",
        scan.Devices[0].Error);
    Check("error: kein Retry ohne Attempts", session.Sends(Apci.DeviceDescriptorRead) == 1);
}

// ---- 7. Randfaelle ----
{
    Check("empty: keine Adressen", (await Diagnostics.ScanAsync(new FakeSession(), [])).Devices.Count == 0);

    var silent = new FakeSession { Silent = true };
    var scan = await Diagnostics.ScanLineAsync(silent, 1, 1,
        new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(10) });
    Check("empty: stille Zeile ohne Geraete", scan.Devices.Count == 0 && scan.Responders.Count == 0);
    Check("empty: IA-Read wurde gesendet", silent.Sends(Apci.IndividualAddressRead) == 1);
}

Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER");
Environment.Exit(failures.Count == 0 ? 0 : 1);

/// <summary>Fake-Session: antwortet wie Geraete der Adressliste, ohne Socket.</summary>
internal sealed class FakeSession : IBusDiagnosticSession
{
    private readonly Dictionary<ushort, byte[]> _descriptors = new();
    private readonly List<ushort> _responders = new();
    private readonly List<Telegram> _sent = new();

    public event Action<BusTelegram>? TelegramReceived;

    /// <summary>When true no response is generated (timeout tests).</summary>
    public bool Silent { get; init; }

    /// <summary>When set, every send throws this exception (error propagation test).</summary>
    public Exception? SendError { get; init; }

    public FakeSession Device(IndividualAddress address, ushort mask)
        => Device(address, [(byte)(mask >> 8), (byte)mask]);

    public FakeSession Device(IndividualAddress address, byte[] descriptorPayload)
    {
        _descriptors[address.Raw] = descriptorPayload;
        _responders.Add(address.Raw);
        return this;
    }

    /// <summary>Adds one IndividualAddressResponse without a descriptor answer (duplicate/ghost).</summary>
    public FakeSession Responder(IndividualAddress address)
    {
        _responders.Add(address.Raw);
        return this;
    }

    public int Sends(Apci apci, IndividualAddress? address = null)
    {
        lock (_sent)
            return _sent.Count(t => t.Apci == apci && (address is null || t.Destination == address));
    }

    public Task SendAsync(Telegram telegram, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(telegram);
        if (SendError is not null) throw SendError;
        if (Silent) return Task.CompletedTask;

        var responses = new List<BusTelegram>();
        switch (telegram.Apci)
        {
            case Apci.IndividualAddressRead:
                responses.AddRange(_responders.Select(raw => Response(new IndividualAddress(raw),
                    telegram.Destination, Apci.IndividualAddressResponse, [])));
                break;
            case Apci.DeviceDescriptorRead:
                if (_descriptors.TryGetValue(telegram.Destination.Raw, out var payload))
                    responses.Add(Response(telegram.Destination, telegram.Source,
                        Apci.DeviceDescriptorResponse, payload));
                break;
        }

        foreach (var response in responses) TelegramReceived?.Invoke(response);
        return Task.CompletedTask;
    }

    private static BusTelegram Response(IndividualAddress source, IndividualAddress destination, Apci apci,
        byte[] data) => new()
    {
        Telegram = new Telegram
        {
            Source = source,
            Destination = destination,
            Apci = apci,
            Data = data,
        },
        Source = "fake",
    };
}
