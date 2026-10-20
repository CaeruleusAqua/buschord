using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knx.Workbench;

/// <summary>Strict conversion of the fictional V4 bundle format into the existing native model.</summary>
public static class WorkbenchJson
{
    private static readonly Dictionary<string, int> Dpts = new() { ["1.001"] = 1, ["1.018"] = 1, ["3.007"] = 4, ["5.001"] = 8, ["9.001"] = 16, ["9.004"] = 16 };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Project CreateDemo()
    {
        using var stream = typeof(WorkbenchJson).Assembly.GetManifestResourceStream("Knx.Workbench.demo-project.json")!;
        using var reader = new StreamReader(stream);
        return Import(reader.ReadToEnd());
    }

    public static Project Import(string json)
    {
        if (json.Length > 4_000_000) throw new InvalidDataException("Demo JSON exceeds the size limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 12 });
            var root = document.RootElement;
            Walk(root);
            Shape(root, "format version data");
            Need(S(root, "format") == "knx-studio-demo" && N(root, "version", 4, 4) == 4, "Unsupported demo format/version.");
            var data = root.GetProperty("data");
            Validate(data);
            return Convert(data);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException or ArgumentException)
        { throw new InvalidDataException("Invalid V4 demo JSON: " + e.Message, e); }
    }

    private static void Validate(JsonElement data)
    {
        Shape(data, "schemaVersion project floors rooms lines devices objects gas links");
        N(data, "schemaVersion", 1, 1);
        var project = data.GetProperty("project");
        Shape(project, "id name description addressStyle");
        Id(S(project, "id")); S(project, "name"); S(project, "description"); N(project, "addressStyle", 3, 3);
        var floors = Index(data, "floors", 20, "id name short");
        var rooms = Index(data, "rooms", 100, "id name floor");
        var lines = Index(data, "lines", 100, "id name area medium");
        var devices = Index(data, "devices", 100, "id name address room line product application secure params revision downloadedRevision channels");
        var objects = Index(data, "objects", 1000, "id device channel number name dpt bits flags active role");
        var groups = Index(data, "gas", 1000, "id address name dpt bits room secure");
        foreach (var floor in floors.Values) S(floor, "short");
        foreach (var room in rooms.Values) Ref(S(room, "floor"), floors);
        foreach (var line in lines.Values)
        {
            var address = Address(S(line, "id"), '.', [15, 15]);
            Need(S(line, "area") == address[0].ToString(), "Line area mismatch.");
            Need(new[] { "TP", "RF", "PL", "IP" }.Contains(S(line, "medium")), "Unknown medium.");
        }
        var addresses = new HashSet<string>();
        var channels = new Dictionary<string, Dictionary<string, JsonElement>>();
        foreach (var device in devices.Values)
        {
            Ref(S(device, "room"), rooms); Ref(S(device, "line"), lines);
            S(device, "product"); S(device, "application"); B(device, "secure");
            N(device, "revision", 0, 9_007_199_254_740_991); N(device, "downloadedRevision", 0, 9_007_199_254_740_991);
            var address = S(device, "address");
            var parts = Address(address, '.', [15, 15, 255]);
            Need(parts.Any(p => p != 0) && string.Join('.', parts.Take(2)) == S(device, "line"), "Device address/line mismatch.");
            Need(addresses.Add(address), "Duplicate device address.");
            channels.Add(S(device, "id"), Index(device, "channels", 100, "id name"));
            var parameters = device.GetProperty("params");
            Need(parameters.ValueKind == JsonValueKind.Object, "Expected parameter object.");
            foreach (var p in parameters.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "mode": Need(p.Value.GetString() is "switch" or "dim", "Invalid demo mode."); break;
                    case "led": Need(p.Value.GetString() is "feedback" or "off" or "on", "Invalid LED mode."); break;
                    case "interface": Text(p.Value); break;
                    case "debounce": case "holdTime": case "brightnessThreshold": case "setpoint": case "deadband":
                    case "minimum": case "fade": case "onDelay": case "offDelay":
                        var value = p.Value.GetDouble();
                        Need(double.IsFinite(value) && value >= (p.Name == "setpoint" ? -50 : 0) && value <= 1_000_000, "Parameter outside demo range."); break;
                    default: throw new InvalidDataException("Unknown demo parameter: " + p.Name);
                }
            }
        }
        var numbers = new HashSet<(string, long)>();
        foreach (var obj in objects.Values)
        {
            var device = S(obj, "device"); Ref(device, devices); Ref(S(obj, "channel"), channels[device]);
            Need(numbers.Add((device, N(obj, "number", 0, 65535))), "Duplicate object number within device.");
            CheckDpt(obj); B(obj, "active");
            var flags = S(obj, "flags");
            Need(Regex.IsMatch(flags, "^C[RWTUI]*$") && flags.Distinct().Count() == flags.Length, "Invalid object flags.");
            Need(S(obj, "role") is "command" or "feedback" or "measure", "Unknown object role.");
        }
        addresses.Clear();
        foreach (var group in groups.Values)
        {
            CheckDpt(group); B(group, "secure");
            if (group.GetProperty("room").ValueKind != JsonValueKind.Null) Ref(S(group, "room"), rooms);
            var address = S(group, "address");
            Need(Address(address, '/', [31, 7, 255]).Any(p => p != 0), "Zero group address.");
            Need(addresses.Add(address), "Duplicate group address.");
        }
        var pairs = new HashSet<(string, string)>(); var sending = new HashSet<string>();
        foreach (var link in Array(data, "links", 5000))
        {
            Shape(link, "ko ga sending");
            var ko = S(link, "ko"); var ga = S(link, "ga");
            Ref(ko, objects); Ref(ga, groups);
            Need(pairs.Add((ko, ga)), "Duplicate link.");
            if (B(link, "sending")) Need(sending.Add(ko), "Multiple sending addresses for one object.");
            var obj = objects[ko]; var group = groups[ga];
            Need(S(obj, "dpt") == S(group, "dpt") && N(obj, "bits", 1, 65535) == N(group, "bits", 1, 65535), "Incompatible DPT/size link.");
            Need(!B(group, "secure") || B(devices[S(obj, "device")], "secure"), "Incompatible Secure link.");
        }
    }

    private static Project Convert(JsonElement data)
    {
        var p = data.GetProperty("project");
        var project = new Project { Id = S(p, "id"), Name = S(p, "name"), Comment = S(p, "description") };
        var installation = new Installation { Id = "BC-V4-Installation", Name = project.Name };
        project.Installations.Add(installation);
        foreach (var f in data.GetProperty("floors").EnumerateArray())
        {
            installation.Locations.AddRoot(new() { Id = S(f, "id"), Name = S(f, "name"), Kind = SpaceKind.Floor });
            project.Workbench.FloorShortNames.Add(S(f, "id"), S(f, "short"));
        }
        foreach (var r in data.GetProperty("rooms").EnumerateArray())
            installation.Locations.Find(S(r, "floor"))!.Add(new() { Id = S(r, "id"), Name = S(r, "name"), Kind = SpaceKind.Room });
        foreach (var l in data.GetProperty("lines").EnumerateArray())
        {
            var parts = Address(S(l, "id"), '.', [15, 15]);
            var area = installation.Areas.SingleOrDefault(a => a.Address == parts[0]);
            if (area is null) { area = new() { Id = "BC-V4-Area-" + parts[0], Address = (byte)parts[0] }; installation.Areas.Add(area); }
            area.Lines.Add(new() { Id = S(l, "id"), Name = S(l, "name"), Address = (byte)parts[1], MediumTypeRefId = Medium(S(l, "medium")) });
        }
        foreach (var d in data.GetProperty("devices").EnumerateArray())
        {
            var device = new DeviceInstance { Id = S(d, "id"), Name = S(d, "name"), Address = IndividualAddress.Parse(S(d, "address")), ProductRefId = S(d, "product") };
            installation.Areas.SelectMany(a => a.Lines).Single(l => l.Id == S(d, "line")).Devices.Add(device);
            installation.Locations.Find(S(d, "room"))!.AssignDevice(device);
            project.Workbench.Devices.Add(device.Id, new() { Demo = true, SecureCapable = B(d, "secure"), Application = S(d, "application"),
                Revision = N(d, "revision", 0, long.MaxValue), DownloadedRevision = N(d, "downloadedRevision", 0, long.MaxValue) });
            foreach (var c in d.GetProperty("channels").EnumerateArray()) device.Channels.Add(new() { Id = S(c, "id"), Name = S(c, "name") });
            foreach (var param in d.GetProperty("params").EnumerateObject()) device.ParameterValues.Add(param.Name, param.Value.GetRawText());
        }
        var devices = ProjectQuery.DeviceList(project).ToDictionary(d => d.Id);
        var objectKeys = new Dictionary<string, ObjectKey>();
        foreach (var o in data.GetProperty("objects").EnumerateArray())
        {
            var id = S(o, "id"); var device = devices[S(o, "device")]; var key = new ObjectKey(device.Id, id);
            device.ComObjectRefs.Add(new() { Id = id, ComObjectId = id, Number = (int)N(o, "number", 0, 65535), Text = S(o, "name") });
            objectKeys.Add(id, key);
            project.Workbench.Objects.Add(key.ToString(), new() { Dpt = S(o, "dpt"), Bits = (int)N(o, "bits", 1, 65535),
                Flags = S(o, "flags"), Active = B(o, "active"), ChannelId = S(o, "channel"), Role = S(o, "role") });
        }
        foreach (var g in data.GetProperty("gas").EnumerateArray())
        {
            var address = GroupAddress.Parse(S(g, "address")); var parts = Address(S(g, "address"), '/', [31, 7, 255]);
            var mainId = "BC-V4-Main-" + parts[0]; var middleId = mainId + "-" + parts[1];
            var main = installation.MainRanges.SingleOrDefault(r => r.Id == mainId);
            if (main is null) { main = new() { Id = mainId, Name = parts[0].ToString(), RangeStart = parts[0] << 11, RangeEnd = (parts[0] << 11) + 2047 }; installation.MainRanges.Add(main); }
            var middle = main.Ranges.SingleOrDefault(r => r.Id == middleId);
            if (middle is null) { middle = new() { Id = middleId, Name = parts[1].ToString(), RangeStart = (parts[0] << 11) + (parts[1] << 8), RangeEnd = (parts[0] << 11) + (parts[1] << 8) + 255 }; main.Ranges.Add(middle); }
            middle.Addresses.Add(new() { Id = S(g, "id"), Name = S(g, "name"), Address = address, DatapointType = S(g, "dpt") });
            project.Workbench.Groups.Add(S(g, "id"), new() { Bits = (int)N(g, "bits", 1, 65535), Secure = B(g, "secure"),
                EffectRoomId = g.GetProperty("room").ValueKind == JsonValueKind.Null ? null : S(g, "room") });
        }
        foreach (var l in data.GetProperty("links").EnumerateArray())
        {
            var key = objectKeys[S(l, "ko")];
            GroupLinks.Link(project, devices[key.DeviceId], key.ObjectRefId, S(l, "ga"));
            if (B(l, "sending")) project.Workbench.Objects[key.ToString()].SendingGroupId = S(l, "ga");
        }
        return project;
    }

    public static string Export(Project project)
    {
        var locations = project.Installations.SelectMany(i => i.Locations.AllNodes).ToList();
        var devices = ProjectQuery.Devices(project).ToList();
        if (devices.Any(d => project.Workbench.Devices.GetValueOrDefault(d.Device.Id)?.Demo != true))
            throw new InvalidDataException("V4 JSON exports explicit DEMO projects only; use native knxproj for manufacturer data.");
        var data = new
        {
            schemaVersion = 1, project = new { id = project.Id, name = project.Name, description = project.Comment, addressStyle = 3 },
            floors = locations.Where(n => n.Kind == SpaceKind.Floor).Select(f => new { id = f.Id, name = f.Name, @short = project.Workbench.FloorShortNames.GetValueOrDefault(f.Id) }),
            rooms = locations.Where(n => n.Kind == SpaceKind.Room).Select(r => new { id = r.Id, name = r.Name, floor = r.Parent?.Id }),
            lines = project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines.Select(l => new { id = l.Id, name = l.Name, area = a.Address.ToString(), medium = MediumName(l.MediumTypeRefId) })),
            devices = devices.Select(d => new { id = d.Device.Id, name = d.Device.Name, address = d.Device.Address.ToString(),
                room = locations.FirstOrDefault(r => r.DeviceRefs.Contains(d.Device.Id))?.Id, line = d.Line?.Id, product = d.Device.ProductRefId,
                application = project.Workbench.Devices[d.Device.Id].Application, secure = project.Workbench.Devices[d.Device.Id].SecureCapable,
                @params = d.Device.ParameterValues.ToDictionary(p => p.Key, p => JsonSerializer.Deserialize<JsonElement>(p.Value)),
                revision = project.Workbench.Devices[d.Device.Id].Revision, downloadedRevision = project.Workbench.Devices[d.Device.Id].DownloadedRevision,
                channels = d.Device.Channels.Select(c => new { id = c.Id, name = c.Name }) }),
            objects = WorkbenchProject.ScopeObjects(project, new(IncludeInactive: true)).Select(r => new { id = r.Object.Id, device = r.Device.Id,
                channel = r.Metadata?.ChannelId, number = r.Object.Number, name = r.Object.Text, dpt = r.Metadata?.Dpt, bits = r.Metadata?.Bits,
                flags = r.Metadata?.Flags, active = r.Metadata?.Active, role = r.Metadata?.Role }),
            gas = ProjectQuery.GroupAddresses(project).Select(g => new { id = g.Id, address = g.Address.ToString(), name = g.Name, dpt = g.DatapointType,
                bits = project.Workbench.Groups.GetValueOrDefault(g.Id)?.Bits, room = project.Workbench.Groups.GetValueOrDefault(g.Id)?.EffectRoomId,
                secure = project.Workbench.Groups.GetValueOrDefault(g.Id)?.Secure }),
            links = WorkbenchProject.ScopeObjects(project, new(IncludeInactive: true)).SelectMany(r => r.Object.Links.Select(id => new { ko = r.Object.Id, ga = id, sending = r.Metadata?.SendingGroupId == id }))
        };
        var json = JsonSerializer.Serialize(new { format = "knx-studio-demo", version = 4, data }, JsonOptions);
        Import(json); // Refuse lossy/unrepresentable native states instead of emitting an invalid demo file.
        return json;
    }

    private static string Medium(string name) => name switch { "TP" => "MT-0", "PL" => "MT-1", "RF" => "MT-2", "IP" => "MT-5", _ => throw new InvalidDataException("Unknown medium.") };
    private static string MediumName(string id) => id switch { "MT-0" => "TP", "MT-1" => "PL", "MT-2" => "RF", "MT-5" => "IP", _ => throw new InvalidDataException("Unknown native medium.") };
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static string Text(JsonElement value) { var s = value.GetString(); Need(!string.IsNullOrWhiteSpace(s) && s.Length <= 300, "Expected nonempty text up to 300 characters."); return s!; }
    private static string S(JsonElement element, string name) => Text(element.GetProperty(name));
    private static bool B(JsonElement element, string name) => element.GetProperty(name).GetBoolean();
    private static long N(JsonElement element, string name, long min, long max) { var n = element.GetProperty(name).GetInt64(); Need(n >= min && n <= max, "Integer out of range: " + name); return n; }
    private static void Id(string value) => Need(Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$") && value is not ("__proto__" or "prototype" or "constructor"), "Invalid ID.");
    private static void Ref(string id, Dictionary<string, JsonElement> index) => Need(index.ContainsKey(id), "Missing reference: " + id);
    private static void CheckDpt(JsonElement value) => Need(Dpts.TryGetValue(S(value, "dpt"), out var bits) && N(value, "bits", 1, 65535) == bits, "Unsupported demo DPT or inconsistent size.");
    private static void Shape(JsonElement element, string fields)
    {
        Need(element.ValueKind == JsonValueKind.Object, "Expected object.");
        var expected = fields.Split(' ').ToHashSet(); var actual = element.EnumerateObject().Select(p => p.Name).ToList();
        Need(actual.Count == expected.Count && actual.ToHashSet().SetEquals(expected), "Missing, duplicate or unknown fields; expected: " + fields);
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement element, string name, int max)
    { var array = element.GetProperty(name); Need(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= max, "Invalid/oversized collection: " + name); return array.EnumerateArray(); }
    private static Dictionary<string, JsonElement> Index(JsonElement element, string name, int max, string shape)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var item in Array(element, name, max)) { Shape(item, shape); var id = S(item, "id"); Id(id); Need(result.TryAdd(id, item), "Duplicate ID: " + id); S(item, "name"); }
        return result;
    }
    private static int[] Address(string value, char separator, int[] limits)
    {
        var parts = value.Split(separator); Need(parts.Length == limits.Length, "Invalid address format.");
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++) { Need(int.TryParse(parts[i], out result[i]) && result[i] >= 0 && result[i] <= limits[i] && result[i].ToString() == parts[i], "Invalid address range/canonical spelling."); }
        return result;
    }
    private static void Walk(JsonElement element, int depth = 0)
    {
        Need(depth <= 10, "JSON nesting limit exceeded.");
        if (element.ValueKind == JsonValueKind.String) Need(element.GetString()!.Length <= 300, "Text limit exceeded.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>();
            foreach (var p in element.EnumerateObject()) { Need(p.Name.Length <= 100 && p.Name is not ("__proto__" or "prototype" or "constructor") && names.Add(p.Name), "Unsafe/duplicate field."); Walk(p.Value, depth + 1); }
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Walk(child, depth + 1);
    }
}
