using System.Globalization;
using System.Text;
using Knx;
using Knx.App.Services;
using Knx.Bus;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok) failures++;
}

// ---- fixture: project with group addresses and one device ----
var project = new Project { Id = "P-1", Name = "Monitor test" };
var installation = new Installation { Id = "I-1" };
var main = new GroupRange { Id = "GR-1", RangeStart = 1, RangeEnd = 1 };
var light = new GroupAddressEntry
{
    Id = "GA-1", Name = "Licht, \"Wohnzimmer\"", Address = GroupAddress.ThreeLevel(1, 2, 3),
    DatapointType = "DPST-1-1",
};
var dim = new GroupAddressEntry
{
    Id = "GA-2", Name = "Dimmen", Address = GroupAddress.ThreeLevel(1, 2, 4), DatapointType = "DPST-5-1",
};
var temperature = new GroupAddressEntry
{
    Id = "GA-3", Name = "Temperatur", Address = GroupAddress.ThreeLevel(1, 2, 5), DatapointType = "DPST-9-1",
};
var unknown = new GroupAddressEntry
{
    Id = "GA-4", Name = "Frei", Address = GroupAddress.ThreeLevel(1, 2, 6), DatapointType = "DPST-20-1",
};
var shortPayload = new GroupAddressEntry
{
    Id = "GA-5", Name = "Kurz", Address = GroupAddress.ThreeLevel(1, 2, 7), DatapointType = "DPST-9-1",
};
var other = new GroupAddressEntry { Id = "GA-6", Name = "Ohne Typ", Address = GroupAddress.ThreeLevel(1, 3, 1) };
var boundaryEntries = new[]
{
    new GroupAddressEntry { Id = "GA-7", Name = "B1", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1" },
    new GroupAddressEntry { Id = "GA-8", Name = "B2", Address = GroupAddress.ThreeLevel(1, 1, 15), DatapointType = "DPST-1-1" },
    new GroupAddressEntry { Id = "GA-9", Name = "B3", Address = GroupAddress.ThreeLevel(1, 2, 1), DatapointType = "DPST-1-1" },
    new GroupAddressEntry { Id = "GA-10", Name = "B4", Address = GroupAddress.ThreeLevel(1, 2, 15), DatapointType = "DPST-1-1" },
    new GroupAddressEntry { Id = "GA-11", Name = "B5", Address = GroupAddress.ThreeLevel(10, 1, 1), DatapointType = "DPST-1-1" },
};
foreach (var address in new[] { light, dim, temperature, unknown, shortPayload, other }.Concat(boundaryEntries))
    main.Addresses.Add(address);
var area = new Area { Id = "A-1", Address = 1 };
var line = new Line { Id = "L-1", Address = 1 };
line.Devices.Add(new DeviceInstance
{
    Id = "D-1", Name = "Taster", Address = new(1, 1, 100), ProductRefId = "M-0001",
});
area.Lines.Add(line);
installation.Areas.Add(area);
installation.MainRanges.Add(main);
project.Installations.Add(installation);

var timestamp = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

BusTelegram Group(ushort address, Apci apci, params byte[] data) => new()
{
    Timestamp = timestamp,
    Source = "1.1.1",
    Telegram = new Telegram
    {
        Source = new IndividualAddress(1, 1, 1),
        Destination = new IndividualAddress(address),
        IsGroupAddress = true,
        Apci = apci,
        Data = data,
    },
};

BusTelegram Device(string source, string destination, Apci apci, params byte[] data) => new()
{
    Timestamp = timestamp,
    Source = source,
    Telegram = new Telegram
    {
        Source = IndividualAddress.Parse(source),
        Destination = IndividualAddress.Parse(destination),
        IsGroupAddress = false,
        Apci = apci,
        Data = data,
    },
};

static List<List<string>> ParseCsv(string text)
{
    var rows = new List<List<string>>();
    var row = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < text.Length; i++)
    {
        var c = text[i];
        if (quoted)
        {
            if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
            else if (c == '"') quoted = false;
            else field.Append(c);
        }
        else if (c == '"') quoted = true;
        else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
        else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
        {
            row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; i++;
        }
        else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
        else field.Append(c);
    }
    if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
    return rows;
}

// ---- append / pause / resume / clear ----
var monitor = new GroupMonitor { Project = project };
monitor.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueWrite, 1));
Check("Append fills buffer and view", monitor.TotalCount == 1 && monitor.VisibleCount == 1);
monitor.Pause();
monitor.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueWrite, 0));
Check("Pause drops new appends", monitor.TotalCount == 1 && monitor.VisibleCount == 1);
monitor.Resume();
monitor.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueRead));
Check("Resume captures again", monitor.TotalCount == 2 && monitor.VisibleCount == 2);
monitor.Clear();
Check("Clear empties buffer and view", monitor.TotalCount == 0 && monitor.VisibleCount == 0);

// ---- filters ----
var filtered = new GroupMonitor { Project = project };
filtered.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueWrite, 1));
filtered.Append(Group(GroupAddress.ThreeLevel(1, 3, 1).Raw, Apci.GroupValueRead));
filtered.Append(Group(GroupAddress.ThreeLevel(1, 2, 4).Raw, Apci.GroupValueResponse, 128));
filtered.Append(Device("1.1.5", "1.1.100", Apci.GroupValueWrite, 1));
Check("Buffer holds all four", filtered.TotalCount == 4 && filtered.VisibleCount == 4);

filtered.TextFilter = "taster";
Check("Text filter matches project device name", filtered.VisibleCount == 1
    && filtered.Visible[0].Source == "1.1.5");
filtered.TextFilter = "wohnzimmer";
Check("Text filter matches group address name", filtered.VisibleCount == 1
    && filtered.Visible[0].Destination == "1/2/3");
filtered.TextFilter = "1/3/1";
Check("Text filter matches address", filtered.VisibleCount == 1);
filtered.TextFilter = "";
Check("Clearing text filter keeps buffer", filtered.VisibleCount == 4 && filtered.TotalCount == 4);

filtered.KindFilter = MonitorKind.Write;
Check("Kind filter write", filtered.VisibleCount == 2
    && filtered.Visible.All(entry => entry.KindText == "Write"));
filtered.KindFilter = MonitorKind.Read;
Check("Kind filter read", filtered.VisibleCount == 1 && filtered.Visible[0].Apci == Apci.GroupValueRead);
filtered.KindFilter = MonitorKind.Response;
Check("Kind filter response", filtered.VisibleCount == 1
    && filtered.Visible[0].Apci == Apci.GroupValueResponse);
filtered.KindFilter = MonitorKind.All;

filtered.GroupOnly = true;
Check("Group-only hides device telegrams", filtered.VisibleCount == 3
    && filtered.Visible.All(entry => entry.IsGroup));
filtered.GroupOnly = false;

filtered.AddressPrefix = "1/2";
Check("Address prefix filters destinations", filtered.VisibleCount == 2);
filtered.KindFilter = MonitorKind.Write;
Check("Prefix combines with kind", filtered.VisibleCount == 1
    && filtered.Visible[0].Destination == "1/2/3");
filtered.AddressPrefix = "1.2";
Check("Address prefix accepts dot separators", filtered.VisibleCount == 1);
filtered.AddressPrefix = "";
filtered.KindFilter = MonitorKind.All;
Check("All filters reset leaves capture intact", filtered.VisibleCount == 4 && filtered.TotalCount == 4);

// ---- address prefix boundaries ----
var boundary = new GroupMonitor { Project = project };
boundary.Append(Group(GroupAddress.ThreeLevel(1, 1, 1).Raw, Apci.GroupValueWrite, 1));
boundary.Append(Group(GroupAddress.ThreeLevel(1, 1, 15).Raw, Apci.GroupValueWrite, 1));
boundary.Append(Group(GroupAddress.ThreeLevel(1, 2, 1).Raw, Apci.GroupValueWrite, 1));
boundary.Append(Group(GroupAddress.ThreeLevel(1, 2, 15).Raw, Apci.GroupValueWrite, 1));
boundary.Append(Group(GroupAddress.ThreeLevel(10, 1, 1).Raw, Apci.GroupValueWrite, 1));
boundary.Append(Device("1.1.5", "1.1.5", Apci.GroupValueWrite, 1));
boundary.Append(Device("1.10.1", "1.10.1", Apci.GroupValueWrite, 1));
static string Destinations(GroupMonitor monitor)
    => string.Join(",", monitor.Visible.Select(entry => entry.Destination));

boundary.AddressPrefix = "1/1";
Check("Prefix matches only whole segments", Destinations(boundary) == "1/1/1,1/1/15,1.1.5");
boundary.AddressPrefix = "1/1/1";
Check("Prefix 1/1/1 does not match 1/1/15", Destinations(boundary) == "1/1/1");
boundary.AddressPrefix = "1/1/15";
Check("Exact prefix matches one address", Destinations(boundary) == "1/1/15");
boundary.AddressPrefix = "1/2";
Check("Prefix 1/2 selects its middle range", Destinations(boundary) == "1/2/1,1/2/15");
boundary.AddressPrefix = "1/2/1";
Check("Prefix 1/2/1 does not match 1/2/15", Destinations(boundary) == "1/2/1");
boundary.AddressPrefix = "1.10";
Check("Dot prefix matches only the line-10 device", Destinations(boundary) == "1.10.1");
boundary.AddressPrefix = "1";
Check("Prefix 1 excludes main range 10", boundary.VisibleCount == 6
    && !Destinations(boundary).Contains("10/1/1", StringComparison.Ordinal));
boundary.AddressPrefix = "1.1";
Check("Dot prefix keeps the same boundary", Destinations(boundary) == "1/1/1,1/1/15,1.1.5");
boundary.AddressPrefix = "1/1/";
Check("Trailing separator is ignored", Destinations(boundary) == "1/1/1,1/1/15,1.1.5");
boundary.AddressPrefix = "10/1";
Check("Prefix 10/1 matches the second main range", Destinations(boundary) == "10/1/1");
boundary.AddressPrefix = "";
Check("Empty prefix keeps all rows", boundary.VisibleCount == 7);

// ---- decoding ----
var decoded = new GroupMonitor { Project = project };
decoded.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueWrite, 1));
decoded.Append(Group(GroupAddress.ThreeLevel(1, 2, 4).Raw, Apci.GroupValueWrite, 128));
decoded.Append(Group(GroupAddress.ThreeLevel(1, 2, 5).Raw, Apci.GroupValueWrite, 0x08, 0x66));
decoded.Append(Group(GroupAddress.ThreeLevel(1, 2, 6).Raw, Apci.GroupValueWrite, 0x01, 0x02));
decoded.Append(Group(GroupAddress.ThreeLevel(1, 2, 7).Raw, Apci.GroupValueWrite, 0x01));
var rows = decoded.Visible;
Check("DPT1 decoded as On", rows[0].DatapointType == "DPST-1-1" && rows[0].Value == "On"
    && rows[0].Unit == "");
Check("DPT5 decoded as percent", rows[1].Value == "50" && rows[1].Unit == "%"
    && rows[1].ValueText == "50 %");
Check("DPT9 decoded as temperature", rows[2].Value == "21.5" && rows[2].Unit == "°C");
Check("Unknown DPT falls back to hex", rows[3].Value == "01-02" && rows[3].Unit == ""
    && rows[3].DataHex == "0102");
Check("Short DPT9 payload does not throw", rows[4].Value == "?");

// ---- project loaded after capture ----
var late = new GroupMonitor();
late.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueWrite, 1));
Check("Without project the name stays empty", late.Visible[0].Name.Length == 0
    && late.Visible[0].DatapointType is null);
late.Project = project;
Check("Project refresh resolves name and DPT", late.Visible[0].Name.StartsWith("Licht")
    && late.Visible[0].DatapointType == "DPST-1-1" && late.Visible[0].Value == "On");

// ---- CSV ----
var csv = decoded.ToCsv();
var parsed = ParseCsv(csv);
Check("CSV round-trip keeps one header plus one line per visible row",
    parsed.Count == decoded.VisibleCount + 1);
Check("CSV header has ten columns", parsed[0].Count == 10 && parsed[0][0] == "Timestamp");
Check("CSV quotes commas and quotes", parsed[1][9] == light.Name);
Check("CSV timestamp is invariant", parsed[1][0].StartsWith("2024-01-02T03:04:05", StringComparison.Ordinal)
    && !parsed[1][0].Contains("02.01.2024", StringComparison.Ordinal));
var quoted = GroupMonitor.CsvField("a,b\"c\nd");
Check("RFC 4180 quoting", quoted == "\"a,b\"\"c\nd\"");

decoded.TextFilter = "Temperatur";
var filteredCsv = ParseCsv(decoded.ToCsv());
Check("CSV exports only the filtered view",
    filteredCsv.Count == 2 && filteredCsv[1][9] == "Temperatur"
    && filteredCsv[1][6] == "21.5" && filteredCsv[1][7] == "°C");
decoded.TextFilter = "";
Check("CSV returns to the full view after clearing the filter", ParseCsv(decoded.ToCsv()).Count == 6);

var exportPath = Path.Combine(Path.GetTempPath(), "knx-monitor-csv-" + Guid.NewGuid().ToString("N") + ".csv");
try
{
    decoded.SaveCsv(exportPath);
    var reread = ParseCsv(File.ReadAllText(exportPath));
    Check("Saved CSV is UTF-8 with the same record count", reread.Count == decoded.VisibleCount + 1);
    Check("Saved CSV has no BOM", File.ReadAllBytes(exportPath) is not [0xEF, 0xBB, 0xBF, ..]);
}
finally
{
    File.Delete(exportPath);
}

// ---- shared model change notifications ----
var notified = 0;
var shared = new GroupMonitor { Project = project };
shared.Changed += (_, _) => notified++;
shared.Append(Group(GroupAddress.ThreeLevel(1, 2, 3).Raw, Apci.GroupValueWrite, 1));
shared.TextFilter = "licht";
shared.Clear();
Check("Model raises Changed for append, filter and clear", notified == 3);

// ---- UI refresh coalescing (the mechanism both panes use) ----
var posted = new Queue<Action>();
var refreshes = 0;
var coalescer = new MonitorRefreshQueue(posted.Enqueue, () => refreshes++);
coalescer.Request();
coalescer.Request();
coalescer.Request();
Check("Coalescer posts one callback per burst", posted.Count == 1);
posted.Dequeue()();
Check("Coalescer refreshes once per burst", refreshes == 1);
coalescer.Request();
Check("Coalescer posts again after a refresh", posted.Count == 1);
coalescer.Detach();
posted.Dequeue()();
coalescer.Request();
Check("Detached coalescer drops queued and future refreshes", refreshes == 1 && posted.Count == 0);

// ---- bounded buffer ----
var bounded = new GroupMonitor(3);
for (var i = 1; i <= 5; i++) bounded.Append(Device($"1.1.{i}", "1.1.100", Apci.GroupValueWrite, 1));
Check("Bounded buffer keeps newest entries", bounded.TotalCount == 3
    && bounded.VisibleCount == 3 && bounded.Visible[0].Source == "1.1.3");

// ---- malformed input ----
var malformed = new GroupMonitor();
var threw = false;
try
{
    malformed.Append(Group(0, Apci.GroupValueWrite));
    malformed.Append(new BusTelegram
    {
        Source = "1.1.1",
        Telegram = new Telegram
        {
            Source = new IndividualAddress(0), Destination = new IndividualAddress(0),
            IsGroupAddress = true, Apci = Apci.GroupValueWrite, Data = null!,
        },
    });
    malformed.Append(Group(0x7FFF, Apci.GroupValueWrite, 0xFF));
    malformed.Append(null!);
}
catch (Exception)
{
    threw = true;
}
Check("Malformed telegrams never throw", !threw && malformed.TotalCount == 3
    && malformed.Visible[0].Destination == "0/0/0");

// ---- culture independence ----
var previousCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = new CultureInfo("de-DE");
    var cultureCsv = decoded.ToCsv();
    Check("CSV stays invariant under de-DE", cultureCsv.Contains("2024-01-02T03:04:05", StringComparison.Ordinal)
        && ParseCsv(cultureCsv).Count == decoded.VisibleCount + 1);
}
finally
{
    CultureInfo.CurrentCulture = previousCulture;
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
