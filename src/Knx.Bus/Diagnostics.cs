using System.Buffers.Binary;
using System.Diagnostics;
using Knx;

namespace Knx.Bus;

/// <summary>
/// Seam for bus diagnostics: a live <see cref="KnxTunnel"/> in production, a fake in tests.
/// Diagnostics only send read-only management frames (IndividualAddressRead,
/// DeviceDescriptorRead); nothing here programs or writes an address.
/// </summary>
public interface IBusDiagnosticSession
{
    /// <summary>Every telegram the interface receives, tagged with its transport.</summary>
    event Action<BusTelegram>? TelegramReceived;

    /// <summary>Sends one telegram; no acknowledgement is awaited here.</summary>
    Task SendAsync(Telegram telegram, CancellationToken ct = default);
}

/// <summary>Adapts a connected <see cref="KnxTunnel"/> to <see cref="IBusDiagnosticSession"/>.</summary>
public sealed class TunnelDiagnosticSession(KnxTunnel tunnel) : IBusDiagnosticSession
{
    public event Action<BusTelegram>? TelegramReceived
    {
        add => tunnel.TelegramReceived += value;
        remove => tunnel.TelegramReceived -= value;
    }

    public Task SendAsync(Telegram telegram, CancellationToken ct = default)
        => tunnel.SendTelegramAsync(telegram, ct);
}

/// <summary>Bounds for a scan: response timeout, parallelism and retry count.</summary>
public sealed record ScanOptions
{
    /// <summary>How long one request waits for its response and how long a bus scan collects.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Requests in flight at the same time.</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>Attempts per request; never more than <see cref="MaxAttempts"/>.</summary>
    public int Attempts { get; init; } = 1;

    public const int MaxAttempts = 3;

    internal ScanOptions Normalized() => new()
    {
        ResponseTimeout = ResponseTimeout > TimeSpan.Zero ? ResponseTimeout : TimeSpan.FromMilliseconds(100),
        Concurrency = Math.Clamp(Concurrency, 1, 16),
        Attempts = Math.Clamp(Attempts, 1, MaxAttempts),
    };
}

/// <summary>
/// Device descriptor bytes as returned by DeviceDescriptorResponse. Mask version is only
/// meaningful for descriptor type 0, where the first two bytes carry it.
/// </summary>
public sealed record DeviceDescriptor(byte Type, byte[] Bytes)
{
    /// <summary>
    /// Parses the response payload. Some stacks echo the requested descriptor type as the first
    /// payload byte; a leading byte equal to <paramref name="type"/> is dropped when extra bytes follow.
    /// </summary>
    public static DeviceDescriptor Parse(byte type, ReadOnlySpan<byte> payload)
    {
        var bytes = payload.ToArray();
        if (bytes.Length > 2 && bytes[0] == type) bytes = bytes[1..];
        return new DeviceDescriptor(type, bytes);
    }

    /// <summary>16-bit mask version (descriptor type 0 only), or null when not available.</summary>
    public ushort? MaskVersion => Type == 0 && Bytes.Length >= 2
        ? BinaryPrimitives.ReadUInt16BigEndian(Bytes)
        : null;

    public string MaskName => MaskVersion is { } mask ? MaskVersions.Describe(mask) : $"descriptor type {Type}";

    public string Describe() => MaskVersion is { } mask
        ? $"{MaskVersions.Describe(mask)} (mask 0x{mask:X4}), descriptor type {Type}, {Bytes.Length} byte(s)"
        : $"descriptor type {Type}, {Bytes.Length} byte(s), no mask version";
}

/// <summary>Names of the KNX mask versions commonly found on TP1 devices.</summary>
public static class MaskVersions
{
    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x0010] = "BCU 1",
        [0x0011] = "BCU 1",
        [0x0012] = "BCU 1",
        [0x0020] = "System 1 (BCU 1)",
        [0x0021] = "System 1 (BCU 1)",
        [0x0022] = "System 2 (BCU 2)",
        [0x0025] = "System B (BCU 2)",
        [0x0300] = "BCU 1",
        [0x0701] = "BIM M112",
        [0x07B0] = "System B (BCU 2)",
        [0x2701] = "System 7",
        [0x27B0] = "System 7",
        [0x57B0] = "System 300",
    };

    public static bool IsKnown(ushort mask) => Names.ContainsKey(mask);

    /// <summary>Human name for a mask version; "unknown mask" when not in the table.</summary>
    public static string Describe(ushort mask) => Names.TryGetValue(mask, out var name) ? name : "unknown mask";
}

/// <summary>One probed individual address: its descriptor, or the error of the last attempt.</summary>
public sealed record ScannedDevice(IndividualAddress Address, DeviceDescriptor? Descriptor, string? Error)
{
    public bool Found => Descriptor is not null;
}

/// <summary>Result of a scan: per-address probes plus every IndividualAddressResponse seen.</summary>
public sealed record BusScan(
    IReadOnlyList<ScannedDevice> Devices,
    IReadOnlyList<IndividualAddress> Responders,
    TimeSpan Elapsed)
{
    public IReadOnlyList<ScannedDevice> Failures => Devices.Where(d => !d.Found).ToList();
}

/// <summary>Comparison of a scan against the addresses a project expects.</summary>
public sealed record AddressCheck(
    IReadOnlyList<IndividualAddress> Duplicates,
    IReadOnlyList<IndividualAddress> Missing,
    IReadOnlyList<IndividualAddress> Unexpected)
{
    public bool Clean => Duplicates.Count == 0 && Missing.Count == 0 && Unexpected.Count == 0;
}

/// <summary>
/// Read-only bus diagnostics: line/device scan, device descriptor parsing and duplicate/missing
/// address comparison. All I/O goes through <see cref="IBusDiagnosticSession"/> so tests inject a
/// fake and no UDP socket appears in this file.
/// unverified live: frames follow the KNX spec but have not been validated against real hardware.
/// </summary>
public static class Diagnostics
{
    /// <summary>
    /// Probes the given addresses with DeviceDescriptorRead and returns one result per address,
    /// with bounded concurrency and at most <see cref="ScanOptions.MaxAttempts"/> attempts each.
    /// </summary>
    public static async Task<BusScan> ScanAsync(IBusDiagnosticSession session, IEnumerable<IndividualAddress> addresses,
        ScanOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(addresses);
        var opts = (options ?? new ScanOptions()).Normalized();
        var started = Stopwatch.GetTimestamp();
        var targets = addresses.Distinct().OrderBy(a => a.Raw).ToList();
        var devices = await ProbeAllAsync(session, targets, opts, ct);
        var responders = devices.Where(d => d.Found).Select(d => d.Address).ToList();
        return new BusScan(devices, responders, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Broadcasts IndividualAddressRead, collects IndividualAddressResponse frames for
    /// <see cref="ScanOptions.ResponseTimeout"/> and reads the descriptor of every responder.
    /// </summary>
    public static Task<BusScan> ScanBusAsync(IBusDiagnosticSession session, ScanOptions? options = null,
        CancellationToken ct = default)
        => DiscoverAsync(session, null, options, ct);

    /// <summary>Like <see cref="ScanBusAsync"/>, restricted to one area.line.</summary>
    public static Task<BusScan> ScanLineAsync(IBusDiagnosticSession session, byte area, byte line,
        ScanOptions? options = null, CancellationToken ct = default)
        => DiscoverAsync(session, address => address.Area == area && address.Line == line, options, ct);

    private static async Task<BusScan> DiscoverAsync(IBusDiagnosticSession session,
        Func<IndividualAddress, bool>? filter, ScanOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var opts = (options ?? new ScanOptions()).Normalized();
        var started = Stopwatch.GetTimestamp();
        var responders = new List<IndividualAddress>();

        void OnTelegram(BusTelegram telegram)
        {
            var inner = telegram.Telegram;
            if (inner.Apci != Apci.IndividualAddressResponse) return;
            lock (responders) responders.Add(inner.Source);
        }

        session.TelegramReceived += OnTelegram;
        try
        {
            await session.SendAsync(new Telegram
            {
                Source = IndividualAddress.Zero,
                Destination = IndividualAddress.Zero, // 0.0.0 is the broadcast address of an IA read
                Apci = Apci.IndividualAddressRead,
                Data = [],
            }, ct);
            await Task.Delay(opts.ResponseTimeout, ct);
        }
        finally
        {
            session.TelegramReceived -= OnTelegram;
        }

        List<IndividualAddress> seen;
        lock (responders) seen = responders.ToList();

        var targets = seen.Where(a => filter?.Invoke(a) ?? true).Distinct().OrderBy(a => a.Raw).ToList();
        var devices = await ProbeAllAsync(session, targets, opts, ct);
        return new BusScan(devices, seen, Stopwatch.GetElapsedTime(started));
    }

    private static async Task<IReadOnlyList<ScannedDevice>> ProbeAllAsync(IBusDiagnosticSession session,
        IReadOnlyList<IndividualAddress> addresses, ScanOptions options, CancellationToken ct)
    {
        if (addresses.Count == 0) return [];

        using var gate = new SemaphoreSlim(options.Concurrency);
        var results = new ScannedDevice[addresses.Count];
        var tasks = new Task[addresses.Count];
        for (var i = 0; i < addresses.Count; i++)
        {
            var index = i;
            tasks[i] = ProbeWithGateAsync(gate, session, addresses[index], options, ct, results, index);
        }
        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task ProbeWithGateAsync(SemaphoreSlim gate, IBusDiagnosticSession session,
        IndividualAddress address, ScanOptions options, CancellationToken ct, ScannedDevice[] results, int index)
    {
        await gate.WaitAsync(ct);
        try
        {
            results[index] = await ProbeAsync(session, address, options, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ScannedDevice> ProbeAsync(IBusDiagnosticSession session, IndividualAddress address,
        ScanOptions options, CancellationToken ct)
    {
        const byte descriptorType = 0x00; // mask version
        string? error = null;
        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await RequestAsync(session, Management.DeviceDescriptorReadTelegram(address, descriptorType),
                    address, Apci.DeviceDescriptorResponse, options.ResponseTimeout, ct);

                if (response is not null)
                    return new ScannedDevice(address, DeviceDescriptor.Parse(descriptorType, response.Data), null);
                error = $"no DeviceDescriptorResponse within {options.ResponseTimeout.TotalMilliseconds:0} ms";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        return new ScannedDevice(address, null, error ?? "no response");
    }

    /// <summary>Sends one request and waits for the matching response APCI from the same device.</summary>
    private static async Task<Telegram?> RequestAsync(IBusDiagnosticSession session, Telegram request,
        IndividualAddress device, Apci expected, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<Telegram>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTelegram(BusTelegram telegram)
        {
            if (telegram.Telegram.Apci != expected || telegram.Telegram.Source != device) return;
            completion.TrySetResult(telegram.Telegram);
        }

        session.TelegramReceived += OnTelegram;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await session.SendAsync(request, cts.Token);
            return await completion.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // response timeout
        }
        finally
        {
            session.TelegramReceived -= OnTelegram;
        }
    }

    /// <summary>
    /// Finds duplicate individual addresses and compares the scan against the expected addresses.
    /// A duplicate is an address that answered with more than one IndividualAddressResponse in the
    /// same scan window: a single device answers an IA read exactly once, so a second answer means
    /// two devices share the address (repeater echoes are not filtered and would look the same).
    /// </summary>
    public static AddressCheck CheckAddresses(BusScan scan, IEnumerable<IndividualAddress>? expected = null)
    {
        ArgumentNullException.ThrowIfNull(scan);

        // Only addresses the scan actually probed count; a line scan sees the whole bus but
        // reports on its own line.
        var covered = scan.Devices.Select(d => d.Address).ToHashSet();
        var duplicates = scan.Responders
            .Where(covered.Contains)
            .GroupBy(a => a)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(a => a.Raw)
            .ToList();

        var found = scan.Devices.Where(d => d.Found).Select(d => d.Address).Distinct().ToList();

        if (expected is null) return new AddressCheck(duplicates, [], []);

        var wanted = expected.Distinct().ToList();
        var missing = wanted.Except(found).OrderBy(a => a.Raw).ToList();
        var unexpected = found.Except(wanted).OrderBy(a => a.Raw).ToList();
        return new AddressCheck(duplicates, missing, unexpected);
    }
}

/// <summary>Kinds of bus errors a caller reports explicitly to <see cref="BusStatistics"/>.</summary>
public enum BusErrorKind { Timeout, SendFailed, Malformed, NegativeAck, Other }

public sealed record SourceCount(string Source, int Telegrams);

public sealed record ErrorCount(BusErrorKind Kind, int Count);

/// <summary>Counters of one trailing time window.</summary>
public sealed record BusStatisticsSnapshot(
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Window,
    int Telegrams,
    int Acks,
    int Errors,
    IReadOnlyList<SourceCount> Sources,
    IReadOnlyList<ErrorCount> ErrorsByKind)
{
    public double PerSecond => Window.TotalSeconds > 0 ? Telegrams / Window.TotalSeconds : 0;
    public double PerMinute => PerSecond * 60;

    public int ErrorsOf(BusErrorKind kind) => ErrorsByKind.FirstOrDefault(e => e.Kind == kind)?.Count ?? 0;
}

/// <summary>
/// Windowed telegram/error/ack counters over a <see cref="BusTelegram"/> stream. A caller feeds
/// telegrams and reports errors and acknowledgements it observed; snapshots are rates per second
/// and per minute over a chosen trailing window.
/// </summary>
public sealed class BusStatistics
{
    private readonly record struct Entry(DateTimeOffset Timestamp, string? Source, BusErrorKind? Error, bool Ack);

    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();
    private readonly TimeSpan _retention;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="retention">How far back events are kept (default 5 minutes).</param>
    /// <param name="capacity">Hard upper bound on stored events, oldest dropped first.</param>
    /// <param name="clock">Clock for explicit events and snapshot bounds; defaults to UTC now.</param>
    public BusStatistics(TimeSpan? retention = null, int capacity = 20_000, Func<DateTimeOffset>? clock = null)
    {
        _retention = retention is { } keep && keep > TimeSpan.Zero ? keep : TimeSpan.FromMinutes(5);
        _capacity = Math.Max(1, capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Stored events, after pruning.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Adds one observed telegram, using the timestamp the transport assigned.</summary>
    public void Add(BusTelegram telegram)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        Append(new Entry(telegram.Timestamp, telegram.Telegram.Source.ToString(), null, false));
    }

    public void AddAck(string? source = null) => Append(new Entry(_clock(), source, null, true));

    public void AddError(BusErrorKind kind, string? source = null) => Append(new Entry(_clock(), source, kind, false));

    public void Reset()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>Counters for the trailing <paramref name="window"/> (default one minute).</summary>
    public BusStatisticsSnapshot Snapshot(TimeSpan? window = null)
    {
        var span = window is { } wanted && wanted > TimeSpan.Zero ? wanted : TimeSpan.FromMinutes(1);
        lock (_gate)
        {
            var to = _clock();
            var from = to - span;
            var telegrams = 0;
            var acks = 0;
            var errors = 0;
            var sources = new Dictionary<string, int>();
            var kinds = new Dictionary<BusErrorKind, int>();

            foreach (var entry in _entries)
            {
                if (entry.Timestamp < from || entry.Timestamp > to) continue;
                if (entry.Ack) acks++;
                if (entry.Error is { } kind)
                {
                    errors++;
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                }
                if (!entry.Ack && entry.Error is null && entry.Source is { } source)
                {
                    telegrams++;
                    sources[source] = sources.GetValueOrDefault(source) + 1;
                }
            }

            return new BusStatisticsSnapshot(
                from, to, span, telegrams, acks, errors,
                sources.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new SourceCount(pair.Key, pair.Value)).ToList(),
                kinds.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
                    .Select(pair => new ErrorCount(pair.Key, pair.Value)).ToList());
        }
    }

    // ponytail: one lock for every event; fine at bus rates (~tens per second), shard if a
    // multi-megabit capture ever needs it.
    private void Append(Entry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            var cutoff = entry.Timestamp - _retention;
            while (_entries.Count > 0 && (_entries.Peek().Timestamp < cutoff || _entries.Count > _capacity))
                _entries.Dequeue();
        }
    }
}
