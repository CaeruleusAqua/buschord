using System.Globalization;
using System.Text;
using Knx.Bus;

namespace Knx.App.Services;

/// <summary>Telegram kind filter of a monitor view.</summary>
public enum MonitorKind { All, Write, Read, Response, Other }

/// <summary>
/// One decoded monitor row. UI-free so the monitor and its decoding stay testable without Avalonia.
/// </summary>
public sealed record MonitorEntry(
    DateTimeOffset Timestamp,
    string Source,
    string Destination,
    bool IsGroup,
    Apci Apci,
    string DataHex,
    string Value,
    string Unit,
    string? DatapointType,
    string Name)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Decoded value with its unit, e.g. <c>50 %</c>.</summary>
    public string ValueText => Unit.Length == 0 ? Value : $"{Value} {Unit}";

    /// <summary>Service without the redundant GroupValue prefix, e.g. <c>Write</c>.</summary>
    public string KindText => Apci.ToString().Replace("GroupValue", "", StringComparison.Ordinal);

    /// <summary>One monospace line for the raw telegram list.</summary>
    public string RawLine =>
        $"{TimeText}  {Source,-9} {(IsGroup ? "→" : "·")} {Destination,-11} {Apci,-22} {DataHex}";
}

/// <summary>
/// Captured bus telegrams with project decoding and filters, like the ETS group monitor.
/// Keeps a bounded buffer, is safe to feed from a bus thread and owns no UI types.
/// </summary>
public sealed class GroupMonitor
{
    public const int DefaultCapacity = 5000;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly List<BusTelegram> _buffer = [];
    private readonly List<MonitorEntry?> _current = [];
    private Project? _project;
    private string _textFilter = "";
    private string _addressPrefix = "";
    private MonitorKind _kindFilter = MonitorKind.All;
    private bool _groupOnly;
    private bool _paused;

    public GroupMonitor(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _capacity = capacity;
    }

    /// <summary>Appends are dropped while paused; the captured buffer is untouched.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>
    /// Raised after the capture or the filter/pause state changed; handlers run outside the lock.
    /// Panes sharing one model use this to keep every view current.
    /// </summary>
    public event EventHandler? Changed;

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public int TotalCount
    {
        get { lock (_gate) return _buffer.Count; }
    }

    public int VisibleCount
    {
        get { lock (_gate) return _current.Count(entry => entry is not null); }
    }

    /// <summary>Snapshot of the filtered view; safe to hand to the UI thread.</summary>
    public IReadOnlyList<MonitorEntry> Visible
    {
        get
        {
            lock (_gate)
                return _current.Where(entry => entry is not null).Select(entry => entry!).ToArray();
        }
    }

    /// <summary>Project used for group address names and datapoint types; may be null.</summary>
    public Project? Project
    {
        get { lock (_gate) return _project; }
        set
        {
            lock (_gate)
            {
                _project = value;
                Rebuild();
            }
            NotifyChanged();
        }
    }

    /// <summary>Free text over source, destination and project name.</summary>
    public string TextFilter
    {
        get { lock (_gate) return _textFilter; }
        set
        {
            lock (_gate)
            {
                _textFilter = value?.Trim() ?? "";
                Rebuild();
            }
            NotifyChanged();
        }
    }

    /// <summary>
    /// Destination prefix filter. Accepts group (<c>1/2</c>) and individual (<c>1.1</c>) addresses with
    /// '/' or '.' as separator. It matches whole leading segments only: <c>1/1</c> matches <c>1/1/5</c>
    /// but not <c>1/10/5</c>, and <c>1/1/1</c> does not match <c>1/1/15</c>. A trailing separator is ignored.
    /// </summary>
    public string AddressPrefix
    {
        get { lock (_gate) return _addressPrefix; }
        set
        {
            lock (_gate)
            {
                _addressPrefix = NormalizePrefix(value);
                Rebuild();
            }
            NotifyChanged();
        }
    }

    public MonitorKind KindFilter
    {
        get { lock (_gate) return _kindFilter; }
        set
        {
            lock (_gate)
            {
                _kindFilter = value;
                Rebuild();
            }
            NotifyChanged();
        }
    }

    public bool GroupOnly
    {
        get { lock (_gate) return _groupOnly; }
        set
        {
            lock (_gate)
            {
                _groupOnly = value;
                Rebuild();
            }
            NotifyChanged();
        }
    }

    public void Pause()
    {
        lock (_gate) _paused = true;
        NotifyChanged();
    }

    public void Resume()
    {
        lock (_gate) _paused = false;
        NotifyChanged();
    }

    /// <summary>Flips pause state and returns the new state (true = paused).</summary>
    public bool TogglePause()
    {
        bool paused;
        lock (_gate)
        {
            _paused = !_paused;
            paused = _paused;
        }
        NotifyChanged();
        return paused;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _current.Clear();
        }
        NotifyChanged();
    }

    /// <summary>Feed point for the bus dispatcher; never throws on malformed telegrams.</summary>
    public void Append(BusTelegram telegram)
    {
        if (telegram?.Telegram is null) return;
        lock (_gate)
        {
            if (_paused) return;
            MonitorEntry? entry = null;
            try
            {
                var decoded = Decode(telegram);
                entry = Matches(decoded) ? decoded : null;
            }
            catch (Exception)
            {
                // A single unreadable telegram must not stop the monitor; it stays counted
                // in TotalCount and reappears once the filters (or project) change.
            }
            _buffer.Add(telegram);
            _current.Add(entry);
            if (_buffer.Count > _capacity)
            {
                var drop = _buffer.Count - _capacity;
                _buffer.RemoveRange(0, drop);
                _current.RemoveRange(0, drop);
            }
        }
        NotifyChanged();
    }

    /// <summary>Visible view as RFC 4180 CSV with invariant timestamps.</summary>
    public string ToCsv()
    {
        var builder = new StringBuilder();
        AppendRow(builder, "Timestamp", "Source", "Destination", "IsGroup", "Apci", "Data",
            "Value", "Unit", "DatapointType", "Name");
        foreach (var entry in Visible)
            AppendRow(builder, entry.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                entry.Source, entry.Destination, entry.IsGroup ? "1" : "0", entry.Apci.ToString(),
                entry.DataHex, entry.Value, entry.Unit, entry.DatapointType ?? "", entry.Name);
        return builder.ToString();
    }

    public void SaveCsv(string path) => File.WriteAllText(path, ToCsv(), new UTF8Encoding(false));

    /// <summary>Quotes one CSV field only when it needs quoting (RFC 4180).</summary>
    public static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Write, read or response, derived from the APCI service name.</summary>
    public static MonitorKind KindOf(Apci apci)
    {
        var name = apci.ToString();
        if (name.EndsWith("Response", StringComparison.Ordinal)) return MonitorKind.Response;
        if (name.EndsWith("Write", StringComparison.Ordinal)) return MonitorKind.Write;
        if (name.EndsWith("Read", StringComparison.Ordinal)) return MonitorKind.Read;
        return MonitorKind.Other;
    }

    /// <summary>Decodes a payload through <see cref="Dpt.Decode"/> and splits off its unit.</summary>
    public static (string Value, string Unit) DecodeValue(string? datapointType, byte[]? data)
    {
        var bytes = data ?? [];
        string text;
        try
        {
            text = Dpt.Decode(datapointType, bytes);
        }
        catch (Exception)
        {
            return (Convert.ToHexString(bytes), "");
        }
        var unit = UnitFor(datapointType);
        if (unit.Length > 0 && text.EndsWith(unit, StringComparison.Ordinal))
            text = text[..^unit.Length];
        return (text, unit);
    }

    /// <summary>Unit the DPT implies; empty for dimensionless types and unknown DPTs.</summary>
    public static string UnitFor(string? datapointType) => datapointType switch
    {
        "DPST-5-1" or "DPST-5-3" or "DPST-9-7" => "%",
        "DPST-9-1" => "°C",
        _ => "",
    };

    private void Rebuild()
    {
        _current.Clear();
        foreach (var telegram in _buffer)
        {
            try
            {
                var entry = Decode(telegram);
                _current.Add(Matches(entry) ? entry : null);
            }
            catch (Exception)
            {
                // Unreadable entries are skipped instead of breaking the whole view.
                _current.Add(null);
            }
        }
    }

    private bool Matches(MonitorEntry entry)
    {
        if (_groupOnly && !entry.IsGroup) return false;
        if (_kindFilter != MonitorKind.All && KindOf(entry.Apci) != _kindFilter) return false;
        if (!MatchesPrefix(entry.Destination, _addressPrefix)) return false;
        return _textFilter.Length == 0
            || entry.Source.Contains(_textFilter, StringComparison.OrdinalIgnoreCase)
            || entry.Destination.Contains(_textFilter, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(_textFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePrefix(string? value)
        => (value ?? "").Trim().Replace('.', '/').TrimEnd('/');

    /// <summary>
    /// Whole-segment prefix match. Both forms share the same comparison text, so a typed prefix
    /// matches a group or individual address, but only at a segment boundary.
    /// </summary>
    private static bool MatchesPrefix(string destination, string prefix)
    {
        if (prefix.Length == 0) return true;
        var normalized = destination.Replace('.', '/');
        if (normalized.Length < prefix.Length
            || !normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return normalized.Length == prefix.Length || normalized[prefix.Length] == '/';
    }

    private MonitorEntry Decode(BusTelegram telegram)
    {
        var frame = telegram.Telegram;
        var data = frame.Data ?? [];
        var isGroup = frame.IsGroupAddress;
        var destination = isGroup ? GroupText(frame.Destination.Raw) : frame.Destination.ToString();
        string? datapointType = null;
        var name = "";
        if (_project is { } project)
        {
            try
            {
                if (isGroup)
                {
                    var raw = (ushort)(frame.Destination.Raw & 0x7FFF);
                    var address = raw == 0 ? null : ProjectQuery.FindGroupAddress(project, new GroupAddress(raw));
                    datapointType = address?.DatapointType;
                    name = address?.Name ?? "";
                }
                else
                {
                    name = ProjectQuery.FindDevice(project, frame.Destination)?.Name ?? "";
                }
            }
            catch (Exception)
            {
                // A malformed project tree must not break the monitor; fall back to the raw address.
            }
        }
        var (value, unit) = DecodeValue(datapointType, data);
        return new MonitorEntry(telegram.Timestamp, frame.Source.ToString(), destination, isGroup, frame.Apci,
            Convert.ToHexString(data), value, unit, datapointType, name);
    }

    /// <summary>Group address text without throwing on a reserved raw value such as 0.</summary>
    public static string GroupText(ushort raw)
    {
        var value = (ushort)(raw & 0x7FFF);
        return value == 0 ? "0/0/0" : new GroupAddress(value).ToString();
    }

    private static void AppendRow(StringBuilder builder, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(CsvField(fields[i] ?? ""));
        }
        builder.Append("\r\n");
    }
}
