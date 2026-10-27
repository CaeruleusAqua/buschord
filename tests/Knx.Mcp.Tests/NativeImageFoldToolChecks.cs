using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using F = RootPropertyBuildMcpFixture;

internal static class NativeImageFoldToolChecks
{
    private static readonly XNamespace Ns = F.Ns;
    private static readonly string[] PointerNames = ["GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr"];
    private sealed record ExpectedChunk(uint Offset, byte[] Initial, byte[] InitialMask, byte[] Working, byte[] Validity, byte[] Canonical, byte[] ParameterBits);
    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static XElement Segment(string id, uint address, byte[] data, byte[] mask) => new(Ns + "AbsoluteSegment", new XAttribute("Id", id),
        new XAttribute("Address", address), new XAttribute("Size", data.Length), new XElement(Ns + "Data", Convert.ToBase64String(data)), new XElement(Ns + "Mask", Convert.ToBase64String(mask)));
    private static XElement Region(uint address, int length) => new(Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"), new XAttribute("Length", length), new XAttribute("Optional", false),
        new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", address)),
        new XElement(Ns + "AccessRights", new XAttribute("Read", "Runtime"), new XAttribute("Write", "Configuration")));
    private static void Master(XDocument master, int regions)
    {
        var config = master.Descendants(Ns + "HawkConfigurationData").Single();
        config.Add(new XElement(Ns + "MemorySegments", Enumerable.Range(0, regions).Select(i => Region(checked((uint)(100 + 8 * i)), 4))));
        config.Add(new XElement(Ns + "Resources", PointerNames.Select((name, i) => new XElement(Ns + "Resource", new XAttribute("Name", name), new XAttribute("Access", "remote"),
            new XElement(Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"), new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", i + 1), new XAttribute("PropertyID", 7)),
            new XElement(Ns + "ResourceType", new XAttribute("Length", 4))))));
    }
    private static void App(XDocument app, bool gap, bool changedSource, int chainRegions)
    {
        var stat = F.Static(app); var code = new XElement(Ns + "Code"); stat.Add(code);
        if (chainRegions > 0)
        {
            for (var i = 0; i < chainRegions - 1; i++) code.Add(Segment("C" + i, checked((uint)(102 + i * 8)), Hex("A5 02 03 04 A5 06 07 08"), Hex("F0 FF FF FF F0 FF FF 00")));
            return;
        }
        code.Add(Segment("C1", 102, Hex("A5 02 03 04 A5 06 07 08"), Hex("F0 FF FF FF F0 FF FF FF")),
            Segment("C2", gap ? 114u : 110u, gap ? Hex("11 12 13 14") : Hex(changedSource ? "10 11 12 13 14 15 16 99" : "10 11 12 13 14 15 16 17"),
                gap ? Hex("FF FF FF 00") : Hex("FF FF FF FF FF FF FF 00")));
        stat.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "Two"), new XElement(Ns + "TypeNumber",
                new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 2), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 3))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "Byte"), new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"),
                new XAttribute("SizeInBit", 8), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 255))));
        foreach (var (id, segment, offset, bit, type, value) in new[] { ("MA", "C1", 0, 6, "Two", 2), ("MB", "C1", 7, 0, "Byte", 224), ("MC", "C2", gap ? 3 : 7, 0, "Byte", 90) })
        {
            stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P" + id), new XAttribute("ParameterType", type), new XAttribute("Value", value),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", segment), new XAttribute("Offset", offset), new XAttribute("BitOffset", bit))));
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", "P" + id)));
            F.Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", id)));
        }
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "PNS"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 1)));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "NS"), new XAttribute("RefId", "PNS")));
    }
    private static string Fixture(string directory, string name, bool gap = false, bool changedSource = false, int chainRegions = 0,
        Action<XDocument>? change = null, Action<XDocument>? masterChange = null)
        => F.CreateArchive(directory, name, editApplication: app => { App(app, gap, changedSource, chainRegions); change?.Invoke(app); },
            editMaster: master => { Master(master, chainRegions > 0 ? chainRegions : 3); masterChange?.Invoke(master); }, editHardware: F.SetFactoryWithObservedCount);
    private static ExpectedChunk[] Expected(uint word, bool gap = false)
    {
        var query = (word & 128) != 0; var skipped = (word & 1) == 0;
        if (gap)
            return [new(100, Hex("00 00 A0 02 03 04 A5 06 07 08 00 00"), query ? Hex("00 00 F0 FF FF FF F0 FF FF FF 00 00") : new byte[12],
                Hex("00 00 A2 02 03 04 A5 06 07 E0 00 00"), query ? Hex("00 00 FF FF FF FF F0 FF FF FF 00 00") : Hex("00 00 FF 00 00 00 00 00 00 FF 00 00"),
                query ? Hex("00 00 A2 02 03 04 A0 06 07 E0 00 00") : Hex("00 00 A2 00 00 00 00 00 00 E0 00 00"), Hex("00 00 03 00 00 00 00 00 00 FF 00 00")),
                new(114, Hex("11 12 13 00 00 00"), query ? Hex("FF FF FF 00 00 00") : new byte[6], Hex("11 12 13 5A 00 00"),
                    query ? Hex("FF FF FF FF 00 00") : Hex("00 00 00 FF 00 00"), query ? Hex("11 12 13 5A 00 00") : Hex("00 00 00 5A 00 00"), Hex("00 00 00 FF 00 00"))];
        var initial = Hex("00 00 A0 02 03 04 A5 06 07 08 10 11 12 13 14 15 16 00 00 00");
        var initialMask = query ? Hex("00 00 F0 FF FF FF F0 FF FF FF FF FF FF FF FF FF FF 00 00 00") : new byte[20];
        if (skipped) return [new(100, initial, initialMask, initial.ToArray(), initialMask.ToArray(), Hex("00 00 A0 02 03 04 A0 06 07 08 10 11 12 13 14 15 16 00 00 00"), new byte[20])];
        return [new(100, initial, initialMask, Hex("00 00 A2 02 03 04 A5 06 07 E0 10 11 12 13 14 15 16 5A 00 00"),
            query ? Hex("00 00 FF FF FF FF F0 FF FF FF FF FF FF FF FF FF FF FF 00 00") : Hex("00 00 FF 00 00 00 00 00 00 FF 00 00 00 00 00 00 00 FF 00 00"),
            query ? Hex("00 00 A2 02 03 04 A0 06 07 E0 10 11 12 13 14 15 16 5A 00 00") : Hex("00 00 A2 00 00 00 00 00 00 E0 00 00 00 00 00 00 00 5A 00 00"),
            Hex("00 00 03 00 00 00 00 00 00 FF 00 00 00 00 00 00 00 FF 00 00"))];
    }
    private static byte[] Words(uint? last = null)
    {
        var bytes = new byte[F.ByteLength];
        for (var i = 0; i < F.FieldCount; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), i == 0 ? F.SavedFirstValue : i == F.FieldCount - 1 && last is { } replacement ? replacement : (uint)i + 1);
        return bytes;
    }
    private static bool Error(JsonNode node) => node["error"] is not null || node["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonNode Data(JsonNode node) => node["result"]!["structuredContent"]!;
    private static JsonNode Build(JsonNode node) => Data(node)["offlineNativeImageBuild"]!;
    private static bool Available(JsonNode node) => !Error(node) && Build(node)["available"]?.GetValue<bool>() == true;
    private static JsonNode Variant(JsonNode node, uint word) => Build(node)["variants"]!.AsArray().Single(v => v!["assumedOptions"]!.GetValue<uint>() == word)!;
    private static JsonNode Chunk(JsonNode node, uint word, NativeImageBuildChunk chunk) => Variant(node, word)["chunks"]!.AsArray()
        .Single(c => (string?)c?["key"] == chunk.Key)!;
    private static bool Bytes(ImmutablePatchBytes? actual, byte[] expected) => actual is not null && actual.ToArray().SequenceEqual(expected);
    private static bool MemoryMatches(NativeImageBuildPlan plan, JsonNode response, uint word, ExpectedChunk[] expected)
    {
        if (!plan.IsAvailable || !Available(response) || plan.Variants.SingleOrDefault(v => v.AssumedOptions == word) is not { IsAvailable: true } variant) return false;
        var memory = variant.Chunks.Where(c => c.Target.Space == NativeImageBuildSpace.StandardMemory).OrderBy(c => c.ByteOffset).ToArray();
        if (memory.Length != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = memory[i]; var e = expected[i]; var shown = Chunk(response, word, actual);
            if (actual.ByteOffset != e.Offset || !Bytes(actual.InitialData, e.Initial) || !Bytes(actual.InitialValidity, e.InitialMask)
                || !Bytes(actual.WorkingData, e.Working) || !Bytes(actual.DesiredValidity, e.Validity) || !Bytes(actual.CanonicalDesired, e.Canonical)
                || !Bytes(actual.ParameterWriteBits, e.ParameterBits) || (string?)shown["initialDataHash"] != Hash(e.Initial)
                || (string?)shown["initialValidityHash"] != Hash(e.InitialMask) || (string?)shown["workingDataHash"] != Hash(e.Working)
                || (string?)shown["desiredValidityHash"] != Hash(e.Validity) || (string?)shown["canonicalDesiredHash"] != Hash(e.Canonical)
                || (string?)shown["parameterWriteBitsHash"] != Hash(e.ParameterBits)) return false;
        }
        return true;
    }
    private static bool PropertyMatches(NativeImageBuildPlan plan, JsonNode response, uint word, uint? last = null)
    {
        if (!plan.IsAvailable || !Available(response) || plan.Variants.SingleOrDefault(v => v.AssumedOptions == word) is not { IsAvailable: true } variant) return false;
        var property = variant.Chunks.Single(c => c.Target.Property?.PropertyId == 10); var shown = Chunk(response, word, property); var skipped = (word & 1) == 0;
        var initial = Enumerable.Repeat((byte)0x7F, F.ByteLength).ToArray(); var zero = new byte[F.ByteLength]; var ff = Enumerable.Repeat((byte)255, F.ByteLength).ToArray();
        var expected = skipped ? initial : Words(last); var validity = skipped ? zero : ff; var canonical = skipped ? zero : expected;
        return Bytes(property.InitialData, initial) && Bytes(property.InitialValidity, zero) && Bytes(property.WorkingData, expected)
            && Bytes(property.DesiredValidity, validity) && Bytes(property.CanonicalDesired, canonical) && Bytes(property.ParameterWriteBits, validity)
            && (string?)shown["initialDataHash"] == Hash(initial) && (string?)shown["initialValidityHash"] == Hash(zero)
            && (string?)shown["workingDataHash"] == Hash(expected) && (string?)shown["desiredValidityHash"] == Hash(validity)
            && (string?)shown["canonicalDesiredHash"] == Hash(canonical) && (string?)shown["parameterWriteBitsHash"] == Hash(validity);
    }
    private static bool Atomic(JsonNode node) => !Error(node) && Build(node)["available"]?.GetValue<bool>() == false
        && Build(node)["hasGlobalFailure"]?.GetValue<bool>() == true && Build(node)["binding"] is null && Build(node)["variants"]?.AsArray().Count == 0;
    private static bool Gates(JsonNode node) => Data(node)["writesEnabled"]?.GetValue<bool>() == false && Data(node)["realDownloadAvailable"]?.GetValue<bool>() == false
        && Data(node)["plan"]?["ready"]?.GetValue<bool>() == false && Data(node)["plan"]?["program"] is null && Data(node)["plan"]?["imageBytes"]?.GetValue<int>() == 0
        && Build(node)["readOnly"]?.GetValue<bool>() == true && Build(node)["actualOptionsKnown"]?.GetValue<bool>() == false
        && Build(node)["completeApplicationImage"]?.GetValue<bool>() == false && Build(node)["executionEligible"]?.GetValue<bool>() == false && Build(node)["downloadEligible"]?.GetValue<bool>() == false;
    private static bool HashOnly(JsonNode node)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal) { "encodedWord", "rawValue", "recordValue", "controllerValue", "activeValue", "initialData", "initialValidity", "workingData", "desiredValidity",
            "canonicalDesired", "parameterWriteBits", "rawData", "rawMask", "inlineData", "inlineMask", "beforeData", "beforeWorkingData", "beforeValidity", "afterValidity", "data", "mask", "base64" };
        var pending = new Stack<JsonNode>(); pending.Push(node);
        while (pending.TryPop(out var current))
        { if (current is JsonObject obj) foreach (var pair in obj) { if (forbidden.Contains(pair.Key)) return false; if (pair.Value is not null) pending.Push(pair.Value); }
            else if (current is JsonArray array) foreach (var child in array) if (child is not null) pending.Push(child); }
        return true;
    }
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-native-fold-tools-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 232000;
            async Task<JsonNode> Tool(string name, object args) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments = args } })))!)!;
            Task<JsonNode> Open(string path) => Tool("project_open", new { path });
            Task<JsonNode> Preview(uint[] words) => Tool("download_plan", new { device_address = "1.1.1", native_image_build_preview = new { option_words = words } });
            NativeImageBuildPlan Core(uint[] words) => OfflineNativeImageBuild.Preview(server.Project!, ProjectQuery.DeviceList(server.Project!).Single(), new(words));
            var path = Fixture(directory, "bridges"); check("fold MCP own three-region two-bridge fixture opens", !Error(await Open(path)), null);
            if (server.Project is not { } project) return;
            var device = ProjectQuery.DeviceList(project).Single(); var model = Knxproj.ToXml(project).ToString(); var archive = Hash(File.ReadAllBytes(path)); var revision = project.ApplicationDataRevision;
            var mainResponse = await Preview([1, 129, 128]); var core = Core([1, 129, 128]);
            check("multi-neighbour and repeated chronological folds are available through the existing route", Available(mainResponse) && core.AllRequestedAssumptionsAvailable, mainResponse.ToJsonString());
            if (Available(mainResponse) && core.AllRequestedAssumptionsAvailable)
            {
                check("fold route enumerates all 130 property and three memory writers without a subset", core.DeclaredReferenceCount == 134 && core.DeclaredOwnerCount == 133
                    && core.Variants.Single(v => v.AssumedOptions == 1).Population is { ReferenceCount: 134, PhysicalRecordCount: 133, NoStorageCount: 1 }, null);
                check("ordinary folds match all initial working validity canonical and claim bytes", MemoryMatches(core, mainResponse, 1, Expected(1)), mainResponse.ToJsonString());
                check("query-full retains its independent source masks and full current byte roles", MemoryMatches(core, mainResponse, 129, Expected(129)), null);
                check("skipped parameter task retains partial-mask raw seed and canonical distinctions", MemoryMatches(core, mainResponse, 128, Expected(128))
                    && Variant(mainResponse, 128)["population"]?["recordsTotal"]?.GetValue<int>() == 0, null);
                check("full 520-byte property images use factory Data with fresh validity in every route", new uint[] { 1, 129, 128 }.All(word => PropertyMatches(core, mainResponse, word)), null);
                var first = core.Variants.Single(v => v.AssumedOptions == 1); var memory = first.Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
                check("bridged component preserves all five unique original contributors", memory.SourceIds.Count == 5
                    && memory.SourceIds.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "mask-region-0", "mask-region-1", "mask-region-2", "C1", "C2" })
                    && Chunk(mainResponse, 1, memory)["sourceIdsTotal"]?.GetValue<int>() == 5
                    && Chunk(mainResponse, 1, memory)["sourceIds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(memory.SourceIds), null);
                var c1 = first.Sources.Single(s => s.SourceId == "C1"); var c2 = first.Sources.Single(s => s.SourceId == "C2");
                check("source buffers remain raw and distinct from normalized folded initial data", Bytes(c1.RawData, Hex("A5 02 03 04 A5 06 07 08")) && Bytes(c1.RawMask, Hex("F0 FF FF FF F0 FF FF FF"))
                    && Bytes(c2.RawData, Hex("10 11 12 13 14 15 16 17")) && Bytes(c2.RawMask, Hex("FF FF FF FF FF FF FF 00"))
                    && memory.InitialData[2] == 0xA0 && memory.InitialData[6] == 0xA5 && memory.InitialData[17] == 0, null);
                check("MCP source hashes expose both partial-mask and whole-zero-mask roles", Variant(mainResponse, 1)["sources"]!.AsArray().Single(s => (string?)s?["sourceId"] == "C1")?["rawDataHash"]?.GetValue<string>() == Hash(Hex("A5 02 03 04 A5 06 07 08"))
                    && Variant(mainResponse, 1)["sources"]!.AsArray().Single(s => (string?)s?["sourceId"] == "C2")?["rawMaskHash"]?.GetValue<string>() == Hash(Hex("FF FF FF FF FF FF FF 00")), null);
                check("all five pointer sources remain conditional and separately named", first.Sources.Where(s => s.Kind == NativeImageBuildSourceKind.PointerZero).Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal).SetEquals(PointerNames)
                    && core.Variants.Where(v => v.AssumedOptions != 1).All(v => v.Sources.All(s => s.Kind != NativeImageBuildSourceKind.PointerZero)), null);
                check("pointer working buffers remain zero data and zero validity", first.Chunks.Where(c => c.Target.Property?.PropertyId == 7).Count() == 5
                    && first.Chunks.Where(c => c.Target.Property?.PropertyId == 7).All(c => Bytes(c.InitialData, new byte[4]) && Bytes(c.InitialValidity, new byte[4])
                        && Bytes(c.WorkingData, new byte[4]) && Bytes(c.DesiredValidity, new byte[4]) && Bytes(c.CanonicalDesired, new byte[4])), null);
                check("separate observed count is preserved without a count working chunk", core.Variants.All(v => v.ObservedCounts.Count == 1 && v.ObservedCounts[0] is { EncodedCount: 2, NotAdopted: true } count
                    && Bytes(count.Data, [0, 2]) && Bytes(count.Mask, [255, 255])) && Variant(mainResponse, 1)["observedCounts"]?[0]?["dataHash"]?.GetValue<string>() == Hash([0, 2])
                    && Variant(mainResponse, 1)["observedCounts"]?[0]?["maskHash"]?.GetValue<string>() == Hash([255, 255]), null);
                check("shared record prefix stays bounded while full population and allocation totals remain visible", Variant(mainResponse, 1)["population"]?["recordsTotal"]?.GetValue<int>() == 134
                    && Variant(mainResponse, 1)["population"]?["recordsShown"]?.GetValue<int>() == 128 && Variant(mainResponse, 1)["population"]?["recordsTruncated"]?.GetValue<bool>() == true
                    && core.AllocatedImagePayloadBytes <= core.ReservedWorstCaseImagePayloadBytes
                    && Build(mainResponse)["allocatedImagePayloadBytes"]?.GetValue<long>() == core.AllocatedImagePayloadBytes
                    && Build(mainResponse)["reservedWorstCaseImagePayloadBytes"]?.GetValue<long>() == core.ReservedWorstCaseImagePayloadBytes, null);
                check("fold diagnostic remains hash-only with all execution and image authorities false", HashOnly(Build(mainResponse)) && Gates(mainResponse), null);
                var again = await Preview([1, 129, 128]); var binding = core.Binding!;
                check("repeated full fold plan has stable source current and binding fingerprints", Available(again)
                    && (string?)Build(again)["binding"]?["fingerprint"] == binding.Fingerprint && binding.IsCurrent(project, device), again.ToJsonString());
                var prefix = Variant(mainResponse, 1)["population"]!["records"]!.ToJsonString(); var recordsHash = (string?)Variant(mainResponse, 1)["population"]!["recordsHash"];
                device.ParameterValues["R129"] = "2712847316"; var hidden = await Preview([1]); var hiddenCore = Core([1]);
                check("hidden last property edit stales binding and matches complete independent bytes", PropertyMatches(hiddenCore, hidden, 1, 0xA1B2C3D4) && !binding.IsCurrent(project, device)
                    && (string?)Build(hidden)["binding"]?["fingerprint"] != binding.Fingerprint, hidden.ToJsonString());
                check("hidden edit changes full record hash while source initial data and displayed prefix stay stable", Available(hidden)
                    && prefix == Variant(hidden, 1)["population"]!["records"]!.ToJsonString() && recordsHash != (string?)Variant(hidden, 1)["population"]!["recordsHash"]
                    && (string?)Build(hidden)["sourceHash"] == (string?)Build(mainResponse)["sourceHash"] && MemoryMatches(hiddenCore, hidden, 1, Expected(1)), hidden.ToJsonString());
                device.ParameterValues.Remove("R129"); check("restored hidden value restores original exact fold replay", binding.IsCurrent(project, device), null);
            }
            check("fold previews preserve project XML revision and original archive bytes", model == Knxproj.ToXml(project).ToString() && revision == project.ApplicationDataRevision
                && archive == Hash(File.ReadAllBytes(path)), null);
            check("changed masked-off final source byte opens independently", !Error(await Open(Fixture(directory, "changed-source", changedSource: true))), null);
            var changed = await Preview([1]); var changedCore = Core([1]);
            check("masked-off source change binds fully despite identical complete output", MemoryMatches(changedCore, changed, 1, Expected(1)) && PropertyMatches(changedCore, changed, 1)
                && (string?)Build(changed)["sourceHash"] != (string?)Build(mainResponse)["sourceHash"]
                && (string?)Build(changed)["binding"]?["fingerprint"] != (string?)Build(mainResponse)["binding"]?["fingerprint"], changed.ToJsonString());
            check("real gap fixture opens", !Error(await Open(Fixture(directory, "gap", gap: true))), null);
            var gapResponse = await Preview([1, 129]); var gapCore = Core([1, 129]);
            check("real gap is retained as two distinct full memory components", gapCore.AllRequestedAssumptionsAvailable && Available(gapResponse)
                && gapCore.Variants.All(v => v.Chunks.Count(c => c.Target.Space == NativeImageBuildSpace.StandardMemory) == 2), gapResponse.ToJsonString());
            check("ordinary gap arrays never invent bytes at addresses112 and113", MemoryMatches(gapCore, gapResponse, 1, Expected(1, true)), null);
            check("query-full gap arrays preserve each independently covered mask", MemoryMatches(gapCore, gapResponse, 129, Expected(129, true)), null);
            check("gaps do not affect complete property formatting or observed count", PropertyMatches(gapCore, gapResponse, 1) && PropertyMatches(gapCore, gapResponse, 129)
                && gapCore.Variants.All(v => v.ObservedCounts.Single().EncodedCount == 2), null);
            check("long chronological growth chain opens", !Error(await Open(Fixture(directory, "chain", chainRegions: 64))), null);
            var chainResponse = await Preview([1, 129]); var chainCore = Core([1, 129]);
            if (Available(chainResponse) && chainCore.AllRequestedAssumptionsAvailable)
            {
                var initial = new byte[508]; var queryMask = new byte[508]; var queryCanonical = new byte[508];
                for (var i = 0; i < 63; i++)
                { Hex("A0 02 03 04 A5 06 07 00").CopyTo(initial, 2 + i * 8); Hex("F0 FF FF FF F0 FF FF 00").CopyTo(queryMask, 2 + i * 8); Hex("A0 02 03 04 A0 06 07 00").CopyTo(queryCanonical, 2 + i * 8); }
                check("63 chronological growths match the complete508-byte ordinary seed and hashes", MemoryMatches(chainCore, chainResponse, 1,
                    [new(100, initial, new byte[508], initial, new byte[508], new byte[508], new byte[508])]) && PropertyMatches(chainCore, chainResponse, 1), null);
                check("query-full growth chain preserves raw partial bits separately from canonical bytes", MemoryMatches(chainCore, chainResponse, 129,
                    [new(100, initial, queryMask, initial, queryMask, queryCanonical, new byte[508])]) && PropertyMatches(chainCore, chainResponse, 129), null);
                var chainMemory = chainCore.Variants[0].Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
                check("chain provenance includes all127 source IDs despite bounded source presentation", chainMemory.SourceIds.Count == 127 && chainMemory.SourceIds.Distinct().Count() == 127
                    && chainCore.Variants[0].Sources.Count == 133 && Variant(chainResponse, 1)["sourcesTotal"]?.GetValue<int>() == 133
                    && Variant(chainResponse, 1)["sourcesShown"]?.GetValue<int>() == 128 && Variant(chainResponse, 1)["sourcesTruncated"]?.GetValue<bool>() == true, null);
                const long allGrowthArrays = 2L * (32L * (62L * 63L / 2L) + 44L * 63L);
                check("reserve charges discarded growth arrays rather than only the small final extent", chainCore.AllocatedImagePayloadBytes >= allGrowthArrays
                    && chainCore.ReservedWorstCaseImagePayloadBytes >= chainCore.AllocatedImagePayloadBytes && chainCore.ReservedWorstCaseImagePayloadBytes > 12L * (1300 + 1280)
                    && Build(chainResponse)["reservedWorstCaseImagePayloadBytes"]?.GetValue<long>() == chainCore.ReservedWorstCaseImagePayloadBytes, null);
                check("large source-prefix truncation preserves complete hashes and false authorities", HashOnly(Build(chainResponse)) && Gates(chainResponse)
                    && Variant(chainResponse, 1)["sourcesHash"]?.GetValue<string>()?.Length == 64 && chainCore.Binding!.IsCurrent(server.Project!, ProjectQuery.DeviceList(server.Project!).Single()), null);
            }
            else check("chronological chain is available", false, chainResponse.ToJsonString());
            foreach (var (name, change) in new (string, Action<XDocument>)[]
            {
                ("original code owners overlap", a => F.Static(a).Descendants(Ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "C2").SetAttributeValue("Address", 109)),
                ("native address exceeds bound", a => F.Static(a).Descendants(Ns + "AbsoluteSegment").First().SetAttributeValue("Address", 1048576)),
                ("native size exceeds bound", a => F.Static(a).Descendants(Ns + "AbsoluteSegment").First().SetAttributeValue("Size", 1048576)),
                ("mask exceeds supplied data", a => F.Static(a).Descendants(Ns + "AbsoluteSegment").First().Element(Ns + "Mask")!.Value = Convert.ToBase64String(new byte[9])),
                ("original owner uncovered despite a larger folded region", a => F.Static(a).Descendants(Ns + "Parameter").Single(e => (string?)e.Attribute("Id") == "PMC").Element(Ns + "Memory")!.SetAttributeValue("Offset", 8)),
            })
            {
                var opened = await Open(Fixture(directory, "invalid-" + Guid.NewGuid().ToString("N"), change: change));
                check("invalid fold source opens for typed offline diagnosis " + name, !Error(opened), opened.ToJsonString());
                if (!Error(opened)) { var rejected = await Preview([1, 129]); check("invalid fold source refuses atomically " + name, Atomic(rejected) && Gates(rejected), rejected.ToJsonString()); }
            }
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previous); }
    }
}
