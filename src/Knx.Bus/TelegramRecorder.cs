using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Knx.Bus;

/// <summary>One recorded bus telegram in a file-friendly shape.</summary>
public sealed class RecordedTelegram
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool IsGroupAddress { get; set; }
    public string Apci { get; set; } = "";
    public string Data { get; set; } = "";
    /// <summary>Live in-process provenance only; untrusted recording imports cannot assert authentication.</summary>
    [JsonIgnore] public BusDataSecurity DataSecurity { get; set; } = BusDataSecurity.Unverified;
    private byte? _hopCount;
    /// <summary>Captured hop count; null when the source recording did not retain it.</summary>
    public byte? HopCount
    {
        get => _hopCount;
        set
        {
            if (value is > 7) throw new ArgumentOutOfRangeException(nameof(value), "Hop count must be between zero and seven.");
            _hopCount = value;
        }
    }

    public static RecordedTelegram From(BusTelegram telegram) => new()
    {
        Timestamp = telegram.Timestamp,
        Source = telegram.Telegram.Source.ToString(),
        Destination = telegram.Telegram.IsGroupAddress
            ? new Knx.GroupAddress(telegram.Telegram.Destination.Raw).ToString()
            : telegram.Telegram.Destination.ToString(),
        IsGroupAddress = telegram.Telegram.IsGroupAddress,
        Apci = telegram.Telegram.Apci.ToString(),
        Data = Convert.ToHexString(telegram.Telegram.Data),
        HopCount = telegram.Telegram.HopCount,
        DataSecurity = telegram.DataSecurity,
    };

    public string ToDisplayLine()
        => $"{Timestamp:HH:mm:ss.fff}  {Source,-9} {(IsGroupAddress ? "→ Gruppe " : "→ Gerät  ")}{Destination,-11} {Apci,-16} {Data}";
}

/// <summary>
/// Telegram recorder. Collects telegrams, writes them to
/// CSV or JSON Lines, reads them back and replays a recording.
/// </summary>
public sealed class TelegramRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<RecordedTelegram> _entries = [];

    public bool IsRecording { get; private set; }
    public int Count => _entries.Count;
    public IReadOnlyList<RecordedTelegram> Entries => _entries;
    public DateTimeOffset? StartedAt { get; private set; }

    public void Start()
    {
        _entries.Clear();
        IsRecording = true;
        StartedAt = DateTimeOffset.Now;
    }

    public IReadOnlyList<RecordedTelegram> Stop()
    {
        IsRecording = false;
        return _entries;
    }

    public void Clear()
    {
        _entries.Clear();
        StartedAt = null;
    }

    /// <summary>Feed point; ignores telegrams while not recording.</summary>
    public void Add(BusTelegram telegram)
    {
        if (!IsRecording) return;
        _entries.Add(RecordedTelegram.From(telegram));
    }

    public void SaveCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Zeitstempel;Quelle;Ziel;Gruppe;APCI;Daten");
        foreach (var e in _entries)
            sb.AppendLine(Csv.Row(';',
                e.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                e.Source, e.Destination, e.IsGroupAddress ? "1" : "0", e.Apci, e.Data));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public void SaveJsonLines(string path)
    {
        var sb = new StringBuilder();
        foreach (var e in _entries)
            sb.AppendLine(JsonSerializer.Serialize(e, JsonOptions));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static List<RecordedTelegram> Load(string path)
        => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? LoadCsv(path)
            : Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                ? LoadXml(path)
                : LoadJsonLines(path);

    /// <summary>
    /// XML recording file with one element per event and the extension .xml.
    /// Events are appended incrementally using BusChord element names.
    /// </summary>
    public void SaveXml(string path)
    {
        var doc = new XDocument(
            new XElement("MonitorLog",
                new XAttribute("Version", 1),
                new XAttribute("Created", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)),
                new XAttribute("Count", _entries.Count),
                _entries.Select(e => new XElement("Event",
                    new XAttribute("Time", e.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
                    new XAttribute("Service", e.Apci),
                    new XAttribute("SourceAddress", e.Source),
                    new XAttribute("DestinationAddress", e.Destination),
                    new XAttribute("IsGroupAddress", e.IsGroupAddress),
                    new XAttribute("Data", e.Data),
                    e.HopCount is { } hopCount ? new XAttribute("HopCount", hopCount) : null))));
        doc.Save(path);
    }

    public static List<RecordedTelegram> LoadXml(string path)
    {
        var result = new List<RecordedTelegram>();
        var doc = XDocument.Load(path);
        foreach (var element in doc.Root?.Elements("Event") ?? [])
        {
            if (!DateTimeOffset.TryParse((string?)element.Attribute("Time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp))
                timestamp = DateTimeOffset.Now;
            result.Add(new RecordedTelegram
            {
                Timestamp = timestamp,
                Apci = (string?)element.Attribute("Service") ?? "",
                Source = (string?)element.Attribute("SourceAddress") ?? "",
                Destination = (string?)element.Attribute("DestinationAddress") ?? "",
                IsGroupAddress = bool.TryParse((string?)element.Attribute("IsGroupAddress"), out var group) && group,
                Data = (string?)element.Attribute("Data") ?? "",
                HopCount = ReadHopCount((string?)element.Attribute("HopCount")),
            });
        }
        return result;
    }

    /// <summary>
    /// CSV export with 13 monitor columns
    /// (Time, Service, Flags, Prio, Source/Destination address and name, Hop Count, Type, DPT, Iack, Info).
    /// Flags, priority and Iack are left blank when not retained by the recorder.
    /// Hop count is emitted only when the source supplied it.
    /// </summary>
    public string ToEtsCsv(Project? project = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time;Service;Flags;Prio;Source Address;Source Name;Destination Address;" +
                      "Destination Name;Hop Count;Type;DPT;Iack;Info");
        foreach (var entry in _entries)
        {
            var sourceName = DeviceName(project, entry.Source);
            var destinationName = entry.IsGroupAddress
                ? GroupAddressName(project, entry.Destination)
                : DeviceName(project, entry.Destination);
            var dpt = entry.IsGroupAddress ? GroupAddressDpt(project, entry.Destination) : "";
            sb.AppendLine(Csv.Row(';',
                entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                entry.Apci, "", "",
                entry.Source, sourceName,
                entry.Destination, destinationName,
                entry.HopCount?.ToString(CultureInfo.InvariantCulture) ?? "", entry.Apci, dpt, "", ""));
        }
        return sb.ToString();
    }

    private static byte? ReadHopCount(string? text)
    {
        if (text is null) return null;
        return byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count <= 7
            ? count : throw new InvalidDataException("The recording contains an invalid hop count.");
    }

    private static string DeviceName(Project? project, string address)
    {
        if (project is null || !Knx.IndividualAddress.TryParse(address, out var ia)) return "";
        return Knx.ProjectQuery.FindDevice(project, ia)?.Name ?? "";
    }

    private static GroupAddressEntry? FindGroupAddress(Project? project, string address)
    {
        if (project is null) return null;
        try { return Knx.ProjectQuery.FindGroupAddress(project, Knx.GroupAddress.Parse(address)); }
        catch (FormatException) { return null; }
    }

    private static string GroupAddressName(Project? project, string address)
        => FindGroupAddress(project, address)?.Name ?? "";

    private static string GroupAddressDpt(Project? project, string address)
        => FindGroupAddress(project, address)?.DatapointType ?? "";

    public static List<RecordedTelegram> LoadJsonLines(string path)
    {
        var result = new List<RecordedTelegram>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonSerializer.Deserialize<RecordedTelegram>(line, JsonOptions) is { } entry)
                result.Add(entry);
        }
        return result;
    }

    public static List<RecordedTelegram> LoadCsv(string path)
    {
        var result = new List<RecordedTelegram>();
        var first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = line.Split(';');
            if (first) { first = false; if (columns[0].Contains("Zeitstempel")) continue; }
            if (columns.Length < 6) continue;
            if (!DateTimeOffset.TryParse(columns[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                continue;
            result.Add(new RecordedTelegram
            {
                Timestamp = ts,
                Source = columns[1],
                Destination = columns[2],
                IsGroupAddress = columns[3] == "1",
                Apci = columns[4],
                Data = columns[5],
            });
        }
        return result;
    }

    /// <summary>Replays a recording into a sink, preserving relative timing when requested.</summary>
    public static async Task ReplayAsync(IEnumerable<RecordedTelegram> entries, Func<RecordedTelegram, Task> sink,
        bool preserveTiming = false, CancellationToken ct = default)
    {
        DateTimeOffset? previous = null;
        foreach (var entry in entries)
        {
            if (preserveTiming && previous is { } last)
            {
                var delay = entry.Timestamp - last;
                if (delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(10))
                    await Task.Delay(delay, ct);
            }
            await sink(entry);
            previous = entry.Timestamp;
        }
    }
}
