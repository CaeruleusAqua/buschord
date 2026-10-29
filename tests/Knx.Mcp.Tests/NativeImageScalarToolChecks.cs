using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using F = RootPropertyBuildMcpFixture;

internal static class NativeImageScalarToolChecks
{
    private static readonly XNamespace Ns = F.Ns;
    private const string ResultName = "offlineNativeImageBuild";
    private const string Numeric = "ExactIntegerAndDotNet10XmlDoubleToSingle";
    private const string Packed = "PackedDaysHoursMinutesAndSeconds24BigEndianOnly", Dense = "SignedRawOrdinaryTime8To32";
    private sealed record Field(string Id, string Type, int Offset, int Bit, string Value, bool Active = true, bool Always = false, bool Customer = false, string? Override = null);
    private static Field[] Fields(bool little, bool packed) => new[]
    {
        new Field("SF", "FT", 0, 0, ".25"), new Field("SZ", "FT", 4, 0, "-0"), new Field("SR", "FT", 8, 0, "0.1"),
        new Field("PK", "PT", 12, 0, "90061"), new Field("T8", "D8", 16, 0, "-2"), new Field("T9", "D9", 18, little ? 0 : 3, "-1"),
        new Field("T16", "D16", 20, 0, "-32768"), new Field("T24", "D24", 22, 0, "8388607"), new Field("T31", "D31", 26, 0, "-1"),
        new Field("T32", "D32", 32, little ? 0 : 7, little ? "-2" : "-1"), new Field("TA", "D16", 40, 0, "2", false, true, Override: "6"),
        new Field("TD", "D16", 42, 0, "3", false, Override: "8"), new Field("TN", "D16", 44, 0, "4", false),
        new Field("TC", "D16", 46, 0, "5", Customer: true),
    }.Where(f => packed || f.Id != "PK").ToArray();
    private static byte[] Seed()
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 64).ToArray(); bytes[19] = 0x5A; bytes[29] = 0xA4;
        Convert.FromHexString("A45A00A55A").CopyTo(bytes, 32); return bytes;
    }
    private static void AddScalars(XDocument app, bool little, bool packed, string behavior, bool latent)
    {
        var stat = F.Static(app); var types = stat.Element(Ns + "ParameterTypes")!;
        stat.Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", little ? "LittleEndian" : "BigEndian");
        stat.Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", behavior);
        types.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "FT"), new XElement(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"),
            new XAttribute("minInclusive", "-100"), new XAttribute("maxInclusive", "100"), new XAttribute("DisplayFormat", "0.000"))));
        if (packed) types.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "PT"), new XElement(Ns + "TypeTime", new XAttribute("SizeInBit", 24),
            new XAttribute("Unit", "PackedDaysHoursMinutesAndSeconds"), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 691199))));
        foreach (var (id, bits, min, max) in new[] { ("D8", 8, "-128", "127"), ("D9", little ? 16 : 9, "-256", "255"), ("D16", 16, "-32768", "32767"),
            ("D24", 24, "-8388608", "8388607"), ("D31", little ? 32 : 31, "-1073741824", "1073741823"), ("D32", 32, "-2147483648", "2147483647") })
            types.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", id), new XElement(Ns + "TypeTime", new XAttribute("SizeInBit", bits),
                new XAttribute("Unit", "Seconds"), new XAttribute("minInclusive", min), new XAttribute("maxInclusive", max), new XAttribute("UIHint", "Duration_hhmmss"))));
        stat.Add(new XElement(Ns + "Code", new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "SC"), new XAttribute("Address", 100), new XAttribute("Size", 64),
            new XElement(Ns + "Data", Convert.ToBase64String(Seed())), new XElement(Ns + "Mask", Convert.ToBase64String(Enumerable.Repeat((byte)255, 64).ToArray())))));
        foreach (var f in Fields(little, packed))
        {
            stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P" + f.Id), new XAttribute("ParameterType", f.Type), new XAttribute("Value", f.Value),
                f.Always ? new XAttribute("LegacyPatchAlways", true) : null, f.Customer ? new XAttribute("CustomerAdjustable", true) : null,
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "SC"), new XAttribute("Offset", f.Offset), new XAttribute("BitOffset", f.Bit))));
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", f.Id), new XAttribute("RefId", "P" + f.Id), f.Override is null ? null : new XAttribute("Value", f.Override)));
            if (f.Active) F.Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", f.Id)));
        }
        if (latent) stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Latent"), new XAttribute("ParameterType", "FT"), new XAttribute("Value", ".5"),
            new XElement(Ns + "Memory", new XAttribute("CodeSegment", "SC"), new XAttribute("Offset", 52), new XAttribute("BitOffset", 0))));
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "NS"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 1)));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "NS"), new XAttribute("RefId", "NS")));
    }
    private static void AddInitialSources(XDocument master)
    {
        var config = master.Descendants(Ns + "HawkConfigurationData").Single();
        config.Add(new XElement(Ns + "MemorySegments", new XElement(Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"), new XAttribute("Length", 64), new XAttribute("Optional", false),
            new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", 100)),
            new XElement(Ns + "AccessRights", new XAttribute("Read", "Runtime"), new XAttribute("Write", "Configuration")))));
        config.Add(new XElement(Ns + "Resources", new[] { "GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr" }.Select((name, i) =>
            new XElement(Ns + "Resource", new XAttribute("Name", name), new XAttribute("Access", "remote"),
                new XElement(Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"), new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", i + 1), new XAttribute("PropertyID", 7)),
                new XElement(Ns + "ResourceType", new XAttribute("Length", 4))))));
    }
    private static string Fixture(string directory, string name, bool little = false, bool packed = true, string behavior = "DefaultValue", bool latent = true, Action<XDocument>? change = null)
        => F.CreateArchive(directory, name, editApplication: app => { AddScalars(app, little, packed, behavior, latent); change?.Invoke(app); }, editMaster: AddInitialSources);
    private static XElement Param(XDocument app, string id) => F.Static(app).Element(Ns + "Parameters")!.Elements().Single(p => (string?)p.Attribute("Id") == "P" + id);
    private static XElement Shape(XDocument app, string id) => F.Static(app).Element(Ns + "ParameterTypes")!.Elements().Single(t => (string?)t.Attribute("Id") == id).Elements().Single();
    private static void AddRaw(XDocument app)
    {
        var stat = F.Static(app);
        stat.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "RT"),
            new XElement(Ns + "TypeRawData", new XAttribute("MaxSize", 4))));
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "PRAW"),
            new XAttribute("ParameterType", "RT"), new XAttribute("Value", "AID/"),
            new XElement(Ns + "Memory", new XAttribute("CodeSegment", "SC"), new XAttribute("Offset", 56), new XAttribute("BitOffset", 0))));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "RAW"), new XAttribute("RefId", "PRAW")));
        F.Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "RAW")));
    }
    private static (byte[] Data, byte[] Mask) Expected(bool little = false, bool packed = true, string behavior = "DefaultValue", bool excluded = false, string? rounding = null, bool negativeZero = true)
    {
        var data = Seed(); var mask = new byte[64];
        var fields = little ? new (int Offset, string Hex)[] { (0,"0000803E"),(4,negativeZero ? "00000080":"00000000"),(8,rounding ?? "CDCCCC3D"),(16,"FE"),(18,"FFFF"),
            (20,"0080"),(22,"FFFF7F"),(26,"FFFFFFFF"),(32,"FEFFFFFF"),(40,"F9FF") }
            : [(0,"3E800000"),(4,negativeZero ? "80000000":"00000000"),(8,rounding ?? "3DCCCCCD"),(16,"FE"),(18,"BFFA"),(20,"8000"),(22,"7FFFFF"),
                (26,"FFFFFFFE"),(32,"A5FFFFFFFE"),(40,"FFF9")];
        void Put(int offset, string hex) { var bytes = Convert.FromHexString(hex); bytes.CopyTo(data, offset); Array.Fill(mask, (byte)255, offset, bytes.Length); }
        foreach (var field in fields) Put(field.Offset, field.Hex);
        if (packed) Put(12, "010121");
        if (behavior == "DefaultValue") { Put(42, little ? "0300" : "0003"); Put(44, little ? "0400" : "0004"); }
        else if (behavior == "Background") { Array.Fill(mask, (byte)255, 42, 4); }
        if (!excluded) Put(46, little ? "0500" : "0005");
        return (data, mask);
    }
    private static byte[] PropertyWords(bool little)
    {
        var bytes = new byte[F.ByteLength];
        for (var i = 0; i < F.FieldCount; i++)
        { var value = i == 0 ? F.SavedFirstValue : (uint)i + 1; if (little) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), value); else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), value); }
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Error(JsonNode node) => node["error"] is not null || node["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonNode Data(JsonNode node) => node["result"]!["structuredContent"]!;
    private static JsonNode Build(JsonNode node) => Data(node)[ResultName]!;
    private static bool Available(JsonNode node) => !Error(node) && Build(node)["available"]?.GetValue<bool>() == true;
    private static JsonNode Variant(JsonNode node, uint word = 1) => Build(node)["variants"]!.AsArray().Single(v => v!["assumedOptions"]!.GetValue<uint>() == word)!;
    private static JsonNode Memory(JsonNode node, uint word = 1) => Variant(node, word)["chunks"]!.AsArray().Single(c => (string?)c?["target"]?["space"] == "standard_memory")!;
    private static bool Atomic(JsonNode node) => !Error(node) && Build(node)["available"]?.GetValue<bool>() == false && Build(node)["hasGlobalFailure"]?.GetValue<bool>() == true
        && Build(node)["binding"] is null && Build(node)["policy"] is null && Build(node)["variants"]?.AsArray().Count == 0;
    private static bool HashOnly(JsonNode node)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal) { "encodedWord", "rawValue", "recordValue", "controllerValue", "activeValue", "initialData", "initialValidity",
            "workingData", "desiredValidity", "canonicalDesired", "rawData", "rawMask", "inlineData", "inlineMask", "data", "mask", "beforeWorkingData", "base64" };
        var pending = new Stack<JsonNode>(); pending.Push(node);
        while (pending.TryPop(out var current))
        { if (current is JsonObject obj) foreach (var pair in obj) { if (forbidden.Contains(pair.Key)) return false; if (pair.Value is not null) pending.Push(pair.Value); }
            else if (current is JsonArray array) foreach (var child in array) if (child is not null) pending.Push(child); }
        return true;
    }

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-native-scalars-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 221000;
            async Task<JsonNode> Tool(string name, object args) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments = args } })))!)!;
            async Task<JsonNode> Open(string path)
            {
                var opened = await Tool("project_open", new { path });
                if (!Error(opened)) { var d = ProjectQuery.DeviceList(server.Project!).Single(); d.ParameterValues["TA"] = "-7"; d.ParameterValues["TD"] = "9"; }
                return opened;
            }
            Task<JsonNode> Preview(uint[]? words = null) => Tool("download_plan", new { device_address = "1.1.1", native_image_build_preview = new { option_words = words ?? [1u] } });
            NativeImageBuildPlan Core(uint[]? words = null) => OfflineNativeImageBuild.Preview(server.Project!, ProjectQuery.DeviceList(server.Project!).Single(), new(words ?? [1u]));
            bool FullArrays(JsonNode response, NativeImageBuildPlan core, bool little = false, bool packed = true, string behavior = "DefaultValue", bool excluded = false)
            {
                if (!Available(response) || !core.IsAvailable) return false;
                var expected = Expected(little, packed, behavior, excluded); var variant = core.Variants.Single(v => v.AssumedOptions == (excluded ? 1025u : 1u));
                var memory = variant.Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
                var property = variant.Chunks.Single(c => c.Target.Property?.PropertyId == 10);
                return memory.InitialData.ToArray().SequenceEqual(Seed()) && memory.InitialValidity!.ToArray().All(b => b == 0)
                    && memory.WorkingData.ToArray().SequenceEqual(expected.Data) && memory.DesiredValidity!.ToArray().SequenceEqual(expected.Mask)
                    && (string?)Memory(response, excluded ? 1025u : 1u)["workingDataHash"] == Hash(expected.Data)
                    && (string?)Memory(response, excluded ? 1025u : 1u)["desiredValidityHash"] == Hash(expected.Mask)
                    && property.WorkingData.ToArray().SequenceEqual(PropertyWords(little)) && property.DesiredValidity!.ToArray().All(b => b == 255);
            }
            var path = Fixture(directory, "mixed"); check("native scalar mixed own fixture opens", !Error(await Open(path)), null);
            if (server.Project is not { } project) return;
            var device = ProjectQuery.DeviceList(project).Single(); var model = Knxproj.ToXml(project).ToString(); var archive = Hash(File.ReadAllBytes(path)); var revision = project.ApplicationDataRevision;
            var mainResponse = await Preview([1, 128, 1025]); var core = Core([1, 128, 1025]);
            check("native scalar full 64-byte memory and 520-byte property literals", FullArrays(mainResponse, core), mainResponse.ToJsonString());
            if (Available(mainResponse) && core.IsAvailable)
            {
                var policy = Build(mainResponse)["policy"]!; var typed = core.Policy!;
                check("native scalar profile names reflect exact established codecs", typed.CodecProfileVersion == 3 && typed.NumericParseProfile == Numeric
                    && typed.PackedTimeEncodingProfile == Packed && typed.DenseTimeEncodingProfile == Dense
                    && policy["codecProfileVersion"]?.GetValue<int>() == 3 && (string?)policy["numericParseProfile"] == Numeric
                    && (string?)policy["packedTimeEncodingProfile"] == Packed && (string?)policy["denseTimeEncodingProfile"] == Dense, policy.ToJsonString());
                check("physical codec counts include the latent unreferenced Single", typed.PhysicalFieldCount == 145 && typed.SingleFieldCount == 4 && typed.PackedTimeFieldCount == 1 && typed.DenseTimeFieldCount == 10
                    && policy["physicalFieldCount"]?.GetValue<int>() == 145 && policy["singleFieldCount"]?.GetValue<int>() == 4, policy.ToJsonString());
                check("source policy metadata remains distinct from assumed customer/task bits", (string?)policy["byteOrder"] == "big_endian" && (string?)policy["downloadBehavior"] == "default_value"
                    && (string?)policy["maskPolicy"] == "whole_touched_byte" && (string?)policy["minEtsVersion"] == "6.0" && policy["packedMinEtsVersion"]?.GetValue<uint>() == 0x06000000
                    && policy["optionsDeclared"]?.GetValue<bool>() == true && (string?)policy["declaredByteOrder"] == "BigEndian", policy.ToJsonString());
                check("new policy and codec hashes match the public source facts", (string?)policy["sourcePolicyFingerprint"] == typed.SourcePolicyFingerprint
                    && (string?)policy["codecFactsFingerprint"] == typed.CodecFactsFingerprint && (string?)policy["fingerprint"] == typed.Fingerprint
                    && typed.Fingerprint.Length == 64 && typed.Fingerprint != typed.SourcePolicyFingerprint, policy.ToJsonString());
                check("task skip does not erase codec source presence", Variant(mainResponse, 128)["tasks"]?["parameters"]?.GetValue<bool>() == false
                    && Variant(mainResponse, 128)["population"]?["recordsTotal"]?.GetValue<int>() == 0 && typed.SingleFieldCount == 4 && typed.DenseTimeFieldCount == 10, Variant(mainResponse, 128).ToJsonString());
                check("nonnull excluded dense customer keeps seed Data and clears validity", FullArrays(mainResponse, core, excluded: true), Variant(mainResponse, 1025).ToJsonString());
                check("negative-zero Single survives source hydration and whole-word formatting", Convert.ToHexString(core.Variants.First(v => v.AssumedOptions == 1).Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory).WorkingData.ToArray().AsSpan(4, 4)) == "80000000", null);
                check("dense9 and shifted dense32 preserve unrelated seed bits", Convert.ToHexString(core.Variants[0].Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory).WorkingData.ToArray().AsSpan(18, 2)) == "BFFA"
                    && Convert.ToHexString(core.Variants[0].Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory).WorkingData.ToArray().AsSpan(32, 5)) == "A5FFFFFFFE", null);
                check("no scalar raw value word or full buffer leaks through MCP", HashOnly(Build(mainResponse)) && Data(mainResponse)["writesEnabled"]?.GetValue<bool>() == false
                    && Build(mainResponse)["completeApplicationImage"]?.GetValue<bool>() == false && Build(mainResponse)["downloadEligible"]?.GetValue<bool>() == false, Build(mainResponse).ToJsonString());
                var prefix = Variant(mainResponse)["population"]!["records"]!.ToJsonString(); var recordsHash = (string?)Variant(mainResponse)["population"]!["recordsHash"]; var binding = core.Binding!;
                device.ParameterValues["TC"] = "6"; var hidden = await Preview();
                check("hidden final scalar edit changes full hash and binding without prefix changes", Available(hidden) && prefix == Variant(hidden)["population"]!["records"]!.ToJsonString()
                    && recordsHash != (string?)Variant(hidden)["population"]!["recordsHash"] && !binding.IsCurrent(project, device)
                    && (string?)Build(hidden)["binding"]?["fingerprint"] != binding.Fingerprint, hidden.ToJsonString());
                device.ParameterValues.Remove("TC"); check("restoring hidden scalar input restores exact route replay", binding.IsCurrent(project, device), null);
                device.ParameterValues["SZ"] = "0"; var zero = await Preview(); var zeroExpected = Expected(negativeZero: false);
                check("positive and negative Single zero have distinct complete output hashes", Available(zero) && (string?)Memory(zero)["workingDataHash"] == Hash(zeroExpected.Data)
                    && (string?)Memory(zero)["workingDataHash"] != (string?)Memory(mainResponse)["workingDataHash"], zero.ToJsonString()); device.ParameterValues.Remove("SZ");
                device.ParameterValues["SR"] = "1.000000059604644775390625"; var round = await Preview(); var rounded = Expected(rounding: "3F800000");
                check("Single midpoint rounds to the even IEEE32 word under the explicit parser profile", Available(round) && (string?)Memory(round)["workingDataHash"] == Hash(rounded.Data), round.ToJsonString()); device.ParameterValues.Remove("SR");
                foreach (var invalid in new[] { "NaN", "INF", "-INF" })
                { device.ParameterValues["SF"] = invalid; var rejected = await Preview(); check("nonfinite Single refuses atomically " + invalid, Atomic(rejected), rejected.ToJsonString()); }
                device.ParameterValues.Remove("SF");
            }
            check("scalar previews and replays preserve archive model and revision", model == Knxproj.ToXml(project).ToString() && archive == Hash(File.ReadAllBytes(path))
                && revision == project.ApplicationDataRevision, null);
            var changedPath = Fixture(directory, "changed-source", change: app => Shape(app, "FT").SetAttributeValue("DisplayFormat", "0.00"));
            check("valid scalar source metadata change opens independently", !Error(await Open(changedPath)), null);
            var changed = await Preview();
            check("scalar source metadata binds independently of identical complete output bytes", FullArrays(changed, Core())
                && (string?)Build(changed)["sourceHash"] != (string?)Build(mainResponse)["sourceHash"]
                && (string?)Build(changed)["binding"]?["fingerprint"] != (string?)Build(mainResponse)["binding"]?["fingerprint"], changed.ToJsonString());
            var overflowPath = Fixture(directory, "finite-overflow", change: app => { Shape(app, "FT").SetAttributeValue("minInclusive", "-1e40"); Shape(app, "FT").SetAttributeValue("maxInclusive", "1e40"); });
            check("finite-double bounds for Single overflow fixture open", !Error(await Open(overflowPath)), null);
            ProjectQuery.DeviceList(server.Project!).Single().ParameterValues["SF"] = "3.5e38";
            var overflow = await Preview();
            check("finite double inside declared limits but above Single range refuses atomically", Atomic(overflow), overflow.ToJsonString());
            foreach (var behavior in new[] { "Background", "None" })
            {
                check("native scalar " + behavior + " fixture opens", !Error(await Open(Fixture(directory, behavior, behavior: behavior))), null);
                var state = await Preview(); var typed = Core();
                check("native dense null " + behavior + " preserves full literal data and proper masks", FullArrays(state, typed, behavior: behavior), state.ToJsonString());
            }
            check("little-endian scalar fixture opens", !Error(await Open(Fixture(directory, "little", little: true, packed: false))), null);
            var littleResponse = await Preview(); var littleCore = Core();
            check("little endian Single and aligned dense fields produce complete independent bytes", FullArrays(littleResponse, littleCore, little: true, packed: false), littleResponse.ToJsonString());
            check("little endian policy omits packed profile without omitting dense or Single", Available(littleResponse) && littleCore.Policy?.PackedTimeEncodingProfile is null
                && littleCore.Policy?.DenseTimeEncodingProfile == Dense && littleCore.Policy?.NumericParseProfile == Numeric
                && (string?)Build(littleResponse)["policy"]?["byteOrder"] == "little_endian" && Build(littleResponse)["policy"]?["packedTimeEncodingProfile"] is null, littleResponse.ToJsonString());
            foreach (var (name, little, change) in new (string, bool, Action<XDocument>)[]
            {
                ("NoStorage Single", false, app => Param(app,"SF").Element(Ns+"Memory")!.Remove()),
                ("NoStorage Dense", false, app => Param(app,"T8").Element(Ns+"Memory")!.Remove()),
                ("NoStorage Packed", false, app => Param(app,"PK").Element(Ns+"Memory")!.Remove()),
                ("DPT9", false, app => Shape(app,"FT").SetAttributeValue("Encoding","DPT 9")),
                ("Double", false, app => Shape(app,"FT").SetAttributeValue("Encoding","IEEE-754 Double")),
                ("Single size override", false, app => Shape(app,"FT").SetAttributeValue("SizeInBit",32)),
                ("Single shifted", false, app => Param(app,"SF").Element(Ns+"Memory")!.SetAttributeValue("BitOffset",1)),
                ("Packed little endian", true, _ => {}),
                ("Packed shifted", false, app => Param(app,"PK").Element(Ns+"Memory")!.SetAttributeValue("BitOffset",1)),
                ("Dense unsigned interval", false, app => Shape(app,"D8").SetAttributeValue("maxInclusive",255)),
                ("Dense unknown unit", false, app => Shape(app,"D8").SetAttributeValue("Unit","seconds")),
                ("Dense LE partial", true, app => { Shape(app,"D9").SetAttributeValue("SizeInBit",9); Param(app,"PK").Remove(); F.Static(app).Element(Ns+"ParameterRefs")!.Elements().Single(r=>(string?)r.Attribute("Id")=="PK").Remove(); F.Program(app).Descendants(Ns+"ParameterRefRef").Single(r=>(string?)r.Attribute("RefId")=="PK").Remove(); }),
                ("Single controller", false, app => F.Program(app).Element(Ns+"Dynamic")!.Add(new XElement(Ns+"choose",new XAttribute("ParamRefId","SF"),new XElement(Ns+"when",new XAttribute("test","1"))))),
                ("Dense controller", false, app => F.Program(app).Element(Ns+"Dynamic")!.Add(new XElement(Ns+"choose",new XAttribute("ParamRefId","T8"),new XElement(Ns+"when",new XAttribute("test","-2"))))),
            })
            {
                var invalidPath = Fixture(directory, "invalid-" + Guid.NewGuid().ToString("N"), little: little, change: change);
                var opened = await Open(invalidPath); check("invalid scalar source opens for offline typed diagnostics " + name, !Error(opened), opened.ToJsonString());
                if (!Error(opened)) { var rejected = await Preview(); check("invalid scalar source atomically refuses " + name, Atomic(rejected), rejected.ToJsonString()); }
            }
            foreach (var little in new[] { false, true })
            {
                var rawPath = Fixture(directory, "raw-" + little, little: little, packed: !little, change: AddRaw);
                var opened = await Open(rawPath);
                check("native raw MCP fixture opens " + little, !Error(opened), opened.ToJsonString());
                if (Error(opened)) continue;
                var raw = await Preview(); var rawCore = Core();
                var expected = Expected(little: little, packed: !little);
                Convert.FromHexString(little ? "030000000080FF" : "000000030080FF").CopyTo(expected.Data, 56);
                Array.Fill(expected.Mask, (byte)255, 56, 7);
                check("native raw MCP complete memory hashes match independent bytes " + little, Available(raw)
                    && (string?)Memory(raw)["workingDataHash"] == Hash(expected.Data)
                    && (string?)Memory(raw)["desiredValidityHash"] == Hash(expected.Mask), raw.ToJsonString());
                check("native raw MCP policy reports framing without exposing payload " + little, Available(raw)
                    && Build(raw)["policy"]?["rawDataFieldCount"]?.GetValue<int>() == 1
                    && Build(raw)["policy"]?["codecProfileVersion"]?.GetValue<int>() == 4
                    && (string?)Build(raw)["policy"]?["rawDataEncodingProfile"] == "RootMemoryLength32ProgramByteOrderOpaquePayloadRetainedTail"
                    && HashOnly(Build(raw)) && !raw.ToJsonString().Contains("AID/")
                    && Data(raw)["writesEnabled"]?.GetValue<bool>() == false, raw.ToJsonString());
                var rawDevice = ProjectQuery.DeviceList(server.Project!).Single();
                rawDevice.ParameterValues["RAW"] = "AA==";
                var shortened = await Preview(); var shortenedExpected = Expected(little: little, packed: !little);
                Convert.FromHexString(little ? "0100000000" : "0000000100").CopyTo(shortenedExpected.Data, 56);
                check("native raw MCP edit changes binding and retains shortened tail " + little, Available(shortened)
                    && rawCore.Binding is not null && !rawCore.Binding.IsCurrent(server.Project!, rawDevice)
                    && (string?)Memory(shortened)["workingDataHash"] == Hash(shortenedExpected.Data), shortened.ToJsonString());
                rawDevice.ParameterValues["RAW"] = "not:a:base64";
                var invalidRaw = await Preview();
                check("native raw MCP invalid payload refuses atomically without a value echo " + little,
                    Atomic(invalidRaw) && !invalidRaw.ToJsonString().Contains("not:a:base64"), invalidRaw.ToJsonString());
            }
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previous); }
    }
}
