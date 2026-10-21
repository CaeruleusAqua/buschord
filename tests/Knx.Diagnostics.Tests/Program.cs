using Knx;
using Knx.Bus;
using Knx.Workbench;

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
    var descriptorRequests = session.Sent.Where(t => t.Apci == Apci.DeviceDescriptorRead).ToList();
    var firstFrame = descriptorRequests.Count > 0 ? Cemi.LDataRequest(descriptorRequests[0]) : [];
    Check("scan: Descriptor-Typ steht in der APCI, nicht als Datum",
        descriptorRequests.All(t => t.Data is [0x00])
        && firstFrame is [0x11, 0x00, 0xBC, 0x60, 0x00, 0x00, 0x11, 0x05, 0x01, 0x03, 0x00],
        Convert.ToHexString(firstFrame));
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

// ---- 8. Projektdiagnose: Programmierbedarf, Schnittstellenkonflikte, Secure-Grenzen ----
{
    var diagnosticsProject = new Project { Id = "DIAG", Name = "Diagnose" };
    var diagnosticsInstallation = new Installation { Id = "DIAG-I" };
    var diagnosticsArea = new Area { Id = "DIAG-A", Address = 1 };
    var diagnosticsLine = new Line { Id = "DIAG-L", Address = 1 };
    var secureDevice = new DeviceInstance
    {
        Id = "SD", ProductRefId = "Product", Address = new(1, 1, 1), Name = "Secure device",
    };
    var plainDevice = new DeviceInstance
    {
        Id = "PD", ProductRefId = "Product", Address = new(1, 1, 2), Name = "Plain device",
    };
    secureDevice.ComObjectRefs.Add(new() { Id = "O1", ComObjectId = "C1", Number = 1, Links = { "SG" } });
    plainDevice.ComObjectRefs.Add(new() { Id = "O2", ComObjectId = "C2", Number = 1, Links = { "SG" } });
    diagnosticsLine.Devices.Add(secureDevice);
    diagnosticsLine.Devices.Add(plainDevice);
    diagnosticsArea.Lines.Add(diagnosticsLine);
    diagnosticsInstallation.Areas.Add(diagnosticsArea);
    diagnosticsProject.Installations.Add(diagnosticsInstallation);
    var diagnosticsRange = new GroupRange { Id = "DIAG-R" };
    diagnosticsRange.Addresses.Add(new()
    {
        Id = "SG", Name = "Secure group", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
    });
    diagnosticsInstallation.MainRanges.Add(diagnosticsRange);
    diagnosticsProject.Workbench.Devices["SD"] = new() { SecureCapable = true };
    diagnosticsProject.Workbench.Devices["PD"] = new() { SecureCapable = false };
    diagnosticsProject.Workbench.Groups["SG"] = new() { Secure = true, Bits = 1 };

    var states = new Dictionary<string, DeviceProgrammingState>
    {
        ["SD"] = DeviceProgrammingState.Empty("SD").ApplySuccess(DeviceLoadFlags.All,
            "hash", DateTimeOffset.UnixEpoch, "diag-ip", ProgrammingOrigin.Demo),
    };
    var report = DeviceProgrammingReport.Check(diagnosticsProject, states, static (_, _) => "hash");
    var interfaces = new[]
    {
        new WorkbenchInterfaceStatus("diag-ip", ["GroupMonitor"], ["BusMonitor", "Scan", "Programming"]),
    };
    var summary = WorkbenchDiagnostics.Summarize(diagnosticsProject, report, interfaces, DateTimeOffset.UnixEpoch);

    Check("diagnostics: loaded device needs no programming",
        !summary.Programming.DevicesNeedingProgramming.Any(d => d.DeviceId == "SD"));
    Check("diagnostics: unloaded device is reported",
        summary.Programming.DeviceCount == 2 && summary.Programming.NeedsProgrammingCount == 1
        && summary.Programming.DevicesNeedingProgramming.Single().DeviceId == "PD");
    Check("diagnostics: interface conflict is carried through",
        summary.HasInterfaceConflicts && summary.Interfaces.Single().BlockedActivities.Contains("Scan"));
    Check("diagnostics: secure counts and insecure-device gap",
        summary.Secure.SecureDeviceCount == 1 && summary.Secure.SecureGroupCount == 1
        && summary.Secure.SecureLinkCount == 2 && summary.Secure.UnsupportedSecureLinkCount == 1
        && summary.Secure.Gaps.Single().DeviceId == "PD" && !summary.Secure.Complete);
    Check("diagnostics: key management and secure download are explicit limits",
        !summary.Secure.KeyManagementAvailable && !summary.Secure.SecureDownloadAvailable
        && summary.Secure.Limits.Contains("No keyring"));
    Check("diagnostics: generated timestamp is deterministic", summary.GeneratedAt == DateTimeOffset.UnixEpoch);
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

    /// <summary>Every telegram the diagnostic code sent, for frame-shape checks.</summary>
    public IReadOnlyList<Telegram> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
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
