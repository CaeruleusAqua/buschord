using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using F = RootPropertyBuildMcpFixture;

internal static class NativeImageBuildToolChecks
{
    private const string OptionName = "native_image_build_preview", ResultName = "offlineNativeImageBuild";
    private static readonly XNamespace Ns = F.Ns;
    private static string Option(string words = "1,129", string? format = null) => "\"" + OptionName + "\":{\"option_words\":[" + words + "]"
        + (format is null ? "" : ",\"tables\":{\"format\":\"" + format + "\"}") + "}";
    private static string Arguments(string option, string extra = "") => "{\"device_address\":\"1.1.1\"," + option + extra + "}";
    private static bool Error(JsonNode node) => node["error"] is not null || node["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonNode Data(JsonNode node) => node["result"]!["structuredContent"]!;
    private static JsonNode Build(JsonNode node) => Data(node)[ResultName]!;
    private static bool Available(JsonNode node) => !Error(node) && Build(node)["available"]?.GetValue<bool>() == true;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonNode Variant(JsonNode node, uint options) => Build(node)["variants"]!.AsArray().Single(v => v!["assumedOptions"]!.GetValue<uint>() == options)!;
    private static bool Gates(JsonNode node) => Data(node)["writesEnabled"]?.GetValue<bool>() == false && Data(node)["realDownloadAvailable"]?.GetValue<bool>() == false
        && Data(node)["plan"]?["ready"]?.GetValue<bool>() == false && Data(node)["plan"]?["program"] is null && Data(node)["plan"]?["imageBytes"]?.GetValue<int>() == 0
        && Build(node)["readOnly"]?.GetValue<bool>() == true && Build(node)["actualOptionsKnown"]?.GetValue<bool>() == false
        && Build(node)["completeApplicationImage"]?.GetValue<bool>() == false && Build(node)["executionEligible"]?.GetValue<bool>() == false && Build(node)["downloadEligible"]?.GetValue<bool>() == false;
    private static bool HashOnly(JsonNode node)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal) { "encodedWord", "rawValue", "recordValue", "controllerValue", "activeValue",
            "initialData", "initialValidity", "workingData", "desiredValidity", "canonicalDesired", "parameterWriteBits", "rawData", "rawMask",
            "inlineData", "inlineMask", "beforeData", "beforeWorkingData", "beforeValidity", "afterValidity", "data", "mask", "base64" };
        var pending = new Stack<JsonNode>(); pending.Push(node);
        while (pending.TryPop(out var current))
        {
            if (current is JsonObject obj)
                foreach (var pair in obj) { if (forbidden.Contains(pair.Key)) return false; if (pair.Value is not null) pending.Push(pair.Value); }
            else if (current is JsonArray array) foreach (var child in array) if (child is not null) pending.Push(child);
        }
        return true;
    }
    private static void MixedApp(XDocument app, bool bulk, bool controls)
    {
        var stat = F.Static(app);
        stat.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "Two"),
            new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 2), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 3))));
        stat.Add(new XElement(Ns + "Code", new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "CM"), new XAttribute("Address", 100), new XAttribute("Size", 4),
            new XElement(Ns + "Data", "pQ=="), new XElement(Ns + "Mask", "8A=="))));
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "PM"), new XAttribute("ParameterType", "Two"), new XAttribute("Value", 2),
            new XElement(Ns + "Memory", new XAttribute("CodeSegment", "CM"), new XAttribute("Offset", 0), new XAttribute("BitOffset", 6))),
            new XElement(Ns + "Parameter", new XAttribute("Id", "PNS"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 1)));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "RM"), new XAttribute("RefId", "PM")),
            new XElement(Ns + "ParameterRef", new XAttribute("Id", "RNS"), new XAttribute("RefId", "PNS")));
        F.Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "RM")));
        if (bulk) F.AddPropertyAllocations(app, 128);
        else stat.Descendants(Ns + "LdCtrlDisconnect").Single().AddBeforeSelf(new XElement(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 3),
            new XAttribute("PropId", 11), new XAttribute("StartElement", 1), new XAttribute("Count", 2), new XAttribute("Verify", false)));
        if (controls) stat.Descendants(Ns + "LdCtrlDisconnect").Single().AddBeforeSelf(Enumerable.Range(0, 130).Select(_ => new XElement(Ns + "LdCtrlRestart")));
    }
    private static void MixedMaster(XDocument master, bool bulk)
    {
        var config = master.Descendants(Ns + "HawkConfigurationData").Single();
        if (bulk) F.AddPropertyDeclarations(master, 128);
        else master.Descendants(Ns + "InterfaceObject").Single().Add(new XElement(Ns + "Property", new XAttribute("PropertyID", 11), new XAttribute("PropertyDataType", "PDT_GENERIC_04")));
        config.Add(new XElement(Ns + "MemorySegments", new XElement(Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"), new XAttribute("Length", 4), new XAttribute("Optional", false),
            new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", 100)),
            new XElement(Ns + "AccessRights", new XAttribute("Read", "Runtime"), new XAttribute("Write", "Configuration")))));
        config.Add(new XElement(Ns + "Resources", new[] { "GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr" }
            .Select((name, i) => new XElement(Ns + "Resource", new XAttribute("Name", name), new XAttribute("Access", "remote"),
                new XElement(Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"), new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", i + 1), new XAttribute("PropertyID", 7)),
                new XElement(Ns + "ResourceType", new XAttribute("Length", 4))))));
    }
    private static void Factory(XDocument hardware, int counts)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(0x10001u); writer.Write(counts + 1);
            void Record(ushort index, uint offset, byte[] data)
            { writer.Write(4u); writer.Write(offset); writer.Write(index); writer.Write((ushort)10); writer.Write((uint)data.Length); writer.Write((byte)3); writer.Write(data); writer.Write(Enumerable.Repeat((byte)255, data.Length).ToArray()); }
            Record(3, 0, Enumerable.Repeat((byte)0x7F, F.ByteLength).ToArray());
            for (var i = 0; i < counts; i++) Record(counts == 1 ? (ushort)3 : (ushort)i, 0xFFFFFFFE, [(byte)(i >> 8), counts == 1 ? (byte)2 : (byte)i]);
        }
        var bytes = raw.ToArray(); var stored = new byte[bytes.Length + 5]; stored[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(1), checked((ushort)bytes.Length)); BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(3), unchecked((ushort)~bytes.Length));
        bytes.CopyTo(stored, 5); hardware.Descendants(Ns + "Hardware2Program").Single().SetAttributeValue("LoadedImage", Convert.ToBase64String(stored));
    }
    private static string Fixture(string directory, string name, bool bulk = false, bool controls = false, int counts = 1, Action<XDocument>? change = null)
        => F.CreateArchive(directory, name, editApplication: app => { MixedApp(app, bulk, controls); change?.Invoke(app); }, editMaster: master => MixedMaster(master, bulk), editHardware: hardware => Factory(hardware, counts));
    private static string TableFixture(string directory)
    {
        const string oldId = "M-0001_A-1", appId = "M-0001_A-0001-01-0000", hardwareId = "M-0001_H-native_A-0001-01-0000";
        var path = GeneratedTablesToolChecks.Fixture(directory);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        XDocument Read(string entry) { using var input = zip.GetEntry(entry)!.Open(); return XDocument.Load(input); }
        void Put(string entry, XDocument document)
        { zip.GetEntry(entry)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false)); writer.Write(document.ToString(SaveOptions.DisableFormatting)); }
        var app = Read("M-0001/" + oldId + ".xml");
        foreach (var attribute in app.Descendants().Attributes().Where(a => a.Value.StartsWith(oldId, StringComparison.Ordinal))) attribute.Value = appId + attribute.Value[oldId.Length..];
        var program = app.Descendants(Ns + "ApplicationProgram").Single(); var stat = program.Element(Ns + "Static")!;
        program.SetAttributeValue("MinEtsVersion", "6.0");
        stat.Add(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "BigEndian"), new XAttribute("DownloadInvisibleParameters", "DefaultValue")),
            new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "HeaderT"),
                new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 16), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 65535)))),
            new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "Header"), new XAttribute("ParameterType", "HeaderT"), new XAttribute("Value", 51966),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "GA"), new XAttribute("Offset", 1), new XAttribute("BitOffset", 0)))),
            new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "HeaderRef"), new XAttribute("RefId", "Header"))));
        program.Descendants(Ns + "ChannelIndependentBlock").Single().Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "HeaderRef")));
        var master = Read("knx_master.xml"); master.Descendants(Ns + "HawkConfigurationData").Single().AddFirst(new XElement(Ns + "Features", new XElement(Ns + "Feature", new XAttribute("Name", "FirstAppObjectIdx"), new XAttribute("Value", 5))));
        var project = Read("0.xml"); var device = project.Descendants().Single(e => e.Name.LocalName == "DeviceInstance");
        device.SetAttributeValue("ProductRefId", appId); device.SetAttributeValue("Hardware2ProgramRefId", hardwareId);
        zip.GetEntry("M-0001/" + oldId + ".xml")!.Delete(); Put("M-0001/" + appId + ".xml", app); Put("knx_master.xml", master); Put("0.xml", project);
        Put("M-0001/Hardware.xml", XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-native"><Hardware2Programs>
            <Hardware2Program Id="{{hardwareId}}"><ApplicationProgramRef RefId="{{appId}}"/></Hardware2Program>
            </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """));
        return path;
    }
    private static IEnumerable<string> InvalidOptions()
    {
        foreach (var value in new[] { "false", "true", "null", "0", "[]", "\"yes\"", "{}", "{\"tables\":{\"format\":\"m112\"}}" }) yield return "\"" + OptionName + "\":" + value;
        foreach (var words in new[] { "null", "false", "1", "\"1\"", "[]", "[1,1]", "[-1]", "[4294967296]", "[1.5]", "[1.0]", "[1e0]", "[\"1\"]", "[true]", "[null]", "[{}]", "[[]]",
            "[" + string.Join(',', Enumerable.Range(0, 17)) + "]" }) yield return "\"" + OptionName + "\":{\"option_words\":" + words + "}";
        yield return Option() + "," + Option();
        yield return "\"" + OptionName + "\":{\"option_words\":[1],\"option_words\":[2]}";
        yield return "\"" + OptionName + "\":{\"option_words\":[1],\"rawValue\":\"2\"}";
        foreach (var tables in new[] { "false", "null", "[]", "{}", "{\"format\":1}", "{\"format\":\"unknown\"}", "{\"format\":\"m112\",\"format\":\"m112\"}", "{\"format\":\"m112\",\"data\":[]}" })
            yield return "\"" + OptionName + "\":{\"option_words\":[1],\"tables\":" + tables + "}";
        yield return "\"" + OptionName + "\":{\"option_words\":[1],\"tables\":{\"format\":\"m112\"},\"tables\":{\"format\":\"m112\"}}";
    }

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-native-image-tools-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var oldCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 211000;
            async Task<JsonNode> Tool(string name, object args) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments = args } })))!)!;
            async Task<JsonNode> Raw(string args) => JsonNode.Parse((await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence
                + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + args + "}}"))!)!;
            static bool ParserError(JsonNode response) => Error(response) && response.ToJsonString().Contains(OptionName, StringComparison.Ordinal)
                && !response.ToJsonString().Contains("descriptor read failed", StringComparison.OrdinalIgnoreCase);
            foreach (var invalid in InvalidOptions())
            {
                var invalidResponse = await Raw(Arguments(invalid, ",\"gateway\":\"invalid-native-preview-gateway\""));
                check("native image rejects malformed option before Current and gateway " + invalid, ParserError(invalidResponse), invalidResponse.ToJsonString());
            }
            foreach (var name in new[] { "parameter_subset", "byte_order", "load_procedure_preview", "image_composition_preview", "root_image_build_preview", "root_resolved_image_build_preview", "root_property_image_build_preview" })
            {
                var companionResponse = await Raw(Arguments(Option(), ",\"" + name + "\":null,\"gateway\":\"invalid-native-preview-gateway\""));
                check("native image rejects exclusive companion presence before Current " + name, ParserError(companionResponse), companionResponse.ToJsonString());
            }
            foreach (var backend in new[] { "resource_preview", "property_preview" })
                foreach (var literal in new[] { "true", "null", "0", "\"false\"", "false,\"" + backend + "\":false" })
                {
                    var backendResponse = await Raw(Arguments(Option(), ",\"" + backend + "\":" + literal + ",\"gateway\":\"invalid-native-preview-gateway\""));
                    check("native image validates backend companion before Current " + backend + " " + literal, ParserError(backendResponse), backendResponse.ToJsonString());
                }
            var maxInteger = await Raw(Arguments(Option("4294967295")));
            check("uint32 maximum reaches ordinary missing-project validation", Error(maxInteger) && !ParserError(maxInteger), maxInteger.ToJsonString());

            var path = Fixture(directory, "mixed"); var originalFile = Hash(File.ReadAllBytes(path));
            var opened = await Tool("project_open", new { path }); check("native image own mixed archive opens", !Error(opened), opened.ToJsonString());
            if (Error(opened) || server.Project is not { } project) return;
            var device = ProjectQuery.DeviceList(project).Single(); var model = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting); var revision = project.ApplicationDataRevision;
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            var schema = server.Tools.Single(t => t.Name == "download_plan").InputSchema["properties"]![OptionName]!;
            check("native image remains one closed option within the current tool contract", server.Tools.Count == McpToolContract.ExpectedToolCount && schema["additionalProperties"]?.GetValue<bool>() == false
                && schema["properties"]?["option_words"]?["uniqueItems"]?.GetValue<bool>() == true && schema["properties"]?["option_words"]?["maxItems"]?.GetValue<int>() == 16
                && schema["properties"]?["option_words"]?["items"]?["maximum"]?.GetValue<ulong>() == uint.MaxValue
                && schema["properties"]?["tables"]?["additionalProperties"]?.GetValue<bool>() == false, schema.ToJsonString());
            check("absent native option preserves old JSON shape", !ordinary.Contains(ResultName, StringComparison.Ordinal), ordinary);
            var response = await Raw(Arguments(Option())); var core = OfflineNativeImageBuild.Preview(project, device, new([1u, 129u]));
            check("native mixed whole population alternatives match public Core", Available(response) && core.IsAvailable && Build(response)["variantsTotal"]?.GetValue<int>() == 2
                && (string?)Build(response)["binding"]?["fingerprint"] == core.Binding?.Fingerprint, response.ToJsonString());
            check("native route reports its own captured mask and omits the separate legacy mask read", Available(response)
                && (string?)Build(response)["maskVersionId"] == core.MaskVersionId && Data(response)["archiveMaskProfile"] is null, response.ToJsonString());
            if (Available(response) && core.IsAvailable)
            {
                var first = Variant(response, 1); var second = Variant(response, 129);
                var memory = first["chunks"]!.AsArray().Single(c => (string?)c?["target"]?["space"] == "standard_memory")!;
                var initial = new byte[] { 0xA0, 0, 0, 0 }; var working = new byte[] { 0xA2, 0, 0, 0 };
                check("native seed role hashes preserve region A0 then low-field A2", (string?)memory["initialDataHash"] == Hash(initial)
                    && (string?)memory["initialValidityHash"] == Hash(new byte[4]) && (string?)memory["workingDataHash"] == Hash(working)
                    && (string?)memory["desiredValidityHash"] == Hash([255, 0, 0, 0]), memory.ToJsonString());
                var property = first["chunks"]!.AsArray().Single(c => c?["target"]?["property"]?["propertyId"]?.GetValue<int>() == 10)!;
                check("complete 520-byte property writers equal independent BCL words", (string?)property["workingDataHash"] == Hash(F.ExpectedWords())
                    && (string?)property["desiredValidityHash"] == Hash(F.ExpectedValidity()), property.ToJsonString());
                check("source and working table roles do not claim archived Before", HashOnly(Build(response)) && Gates(response), Build(response).ToJsonString());
                check("all option assumptions retain source and current-context linkage", Build(response)["allRequestedAssumptionsAvailable"]?.GetValue<bool>() == true
                    && Build(response)["declaredReferenceCount"]?.GetValue<int>() == 132 && Build(response)["declaredOwnerCount"]?.GetValue<int>() == 131
                    && (string?)Build(response)["sharedContextHash"] == core.SharedContextHash && (string?)Build(response)["hardwareDefinitionHash"] == core.HardwareDefinitionHash, Build(response).ToJsonString());
                check("pointer contributions differ with correlated QueryFull option", first["sources"]!.AsArray().Count(s => (string?)s?["kind"] == "pointer_zero") == 5
                    && second["sources"]!.AsArray().All(s => (string?)s?["kind"] != "pointer_zero") && (string?)first["fingerprint"] != (string?)second["fingerprint"], Build(response).ToJsonString());
                check("record detail budget is shared while full totals remain", first["population"]?["recordsTotal"]?.GetValue<int>() == 132 && first["population"]?["recordsShown"]?.GetValue<int>() == 128
                    && first["population"]?["recordsTruncated"]?.GetValue<bool>() == true && second["population"]?["recordsShown"]?.GetValue<int>() == 0
                    && second["population"]?["recordsTotal"]?.GetValue<int>() == 132 && second["population"]?["recordsHash"]?.GetValue<string>()?.Length == 64, Build(response).ToJsonString());
                check("count bytes are observed without capacity or count writes", first["observedCountsTotal"]?.GetValue<int>() == 1
                    && first["observedCounts"]![0]?["encodedCount"]?.GetValue<int>() == 2 && first["observedCounts"]![0]?["notAdopted"]?.GetValue<bool>() == true, first.ToJsonString());
                var prefix = first["population"]!["records"]!.ToJsonString(); var oldHash = (string?)first["population"]!["recordsHash"]; var oldBinding = core.Binding!;
                device.ParameterValues["R129"] = "999"; var changed = await Raw(Arguments(Option()));
                check("last hidden record changes full hash and binding while prefix stays exact", Available(changed)
                    && prefix == Variant(changed, 1)["population"]!["records"]!.ToJsonString()
                    && oldHash != (string?)Variant(changed, 1)["population"]!["recordsHash"] && !oldBinding.IsCurrent(project, device)
                    && (string?)Build(changed)["binding"]?["fingerprint"] != oldBinding.Fingerprint, changed.ToJsonString());
                device.ParameterValues.Remove("R129");
                check("restoring captured state restores exact same-route binding", oldBinding.IsCurrent(project, device), null);
            }
            var falseFlags = await Raw(Arguments(Option(), ",\"resource_preview\":false,\"property_preview\":false"));
            check("explicit false backend flags retain the unified request", Available(falseFlags), falseFlags.ToJsonString());
            check("native preview leaves project archive revision and old JSON unchanged", model == Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting)
                && originalFile == Hash(File.ReadAllBytes(path)) && revision == project.ApplicationDataRevision
                && ordinary == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString(), null);
            var unavailable = await Raw(Arguments(Option("3,131", "m112")));
            check("unsupported table request keeps both unavailable variant rows without a partial binding", !Error(unavailable) && Build(unavailable)["available"]?.GetValue<bool>() == false
                && Build(unavailable)["binding"] is null && Build(unavailable)["variants"]?.AsArray().Count == 2
                && Build(unavailable)["variants"]!.AsArray().All(v => v?["available"]?.GetValue<bool>() == false && v?["chunksTotal"]?.GetValue<int>() == 0) && Gates(unavailable), unavailable.ToJsonString());
            var partial = await Raw(Arguments(Option("1,3", "m112")));
            check("available and unavailable assumptions remain together under the entire request binding", Available(partial)
                && Build(partial)["allRequestedAssumptionsAvailable"]?.GetValue<bool>() == false && Variant(partial, 1)["available"]?.GetValue<bool>() == true
                && Variant(partial, 3)["available"]?.GetValue<bool>() == false && Variant(partial, 3)["binding"] is null
                && Variant(partial, 3)["population"] is null && Variant(partial, 3)["chunksTotal"]?.GetValue<int>() == 0 && Gates(partial), partial.ToJsonString());

            var cappedPath = Fixture(directory, "capped", bulk: true, controls: true, counts: 129);
            check("native detail-cap fixture opens", !Error(await Tool("project_open", new { path = cappedPath })), null);
            var capped = await Raw(Arguments(Option()));
            check("native all role collections expose bounded prefixes plus complete totals", Available(capped)
                && new[] { "sources", "chunks", "observedCounts" }.All(role => Variant(capped, 1)[role + "Total"]!.GetValue<int>() > 128
                    && Variant(capped, 1)[role + "Shown"]!.GetValue<int>() == 128 && Variant(capped, 1)[role + "Truncated"]!.GetValue<bool>()
                    && Variant(capped, 1)[role]!.AsArray().Count == 128)
                && Variant(capped, 1)["procedure"]?["controlsTotal"]?.GetValue<int>() > 128 && Variant(capped, 1)["procedure"]?["controlsShown"]?.GetValue<int>() == 128
                && HashOnly(Build(capped)), capped.ToJsonString());
            var changedControlPath = Fixture(directory, "hidden-control", bulk: true, controls: true, counts: 129,
                change: app => app.Descendants(Ns + "LdCtrlRestart").Last().SetAttributeValue("AppliesTo", "all"));
            check("native hidden-control archive opens", !Error(await Tool("project_open", new { path = changedControlPath })), null);
            var changedControl = await Raw(Arguments(Option()));
            check("hidden control changes full control hash and binding without changing displayed prefix", Available(capped) && Available(changedControl)
                && Variant(capped, 1)["procedure"]!["controls"]!.ToJsonString() == Variant(changedControl, 1)["procedure"]!["controls"]!.ToJsonString()
                && (string?)Variant(capped, 1)["procedure"]!["controlsHash"] != (string?)Variant(changedControl, 1)["procedure"]!["controlsHash"]
                && (string?)Build(capped)["binding"]?["fingerprint"] != (string?)Build(changedControl)["binding"]?["fingerprint"], changedControl.ToJsonString());
            var manyWords = Enumerable.Range(0, 16).Select(i => 8u + ((uint)i << 8)).ToArray(); var many = await Raw(Arguments(Option(string.Join(',', manyWords))));
            check("all 32 conditional variants remain visible with aggregate detail budgets", !Error(many) && Build(many)["variantsTotal"]?.GetValue<int>() == 32
                && Build(many)["variantsShown"]?.GetValue<int>() == 32 && Build(many)["variantsTruncated"]?.GetValue<bool>() == false
                && Build(many)["variants"]!.AsArray().Sum(v => v?["sourcesShown"]?.GetValue<int>() ?? 0) <= 128
                && Build(many)["variants"]!.AsArray().Sum(v => v?["procedure"]?["controlsShown"]?.GetValue<int>() ?? 0) <= 128 && HashOnly(Build(many)), many.ToJsonString());
            var changedWords = (uint[])manyWords.Clone(); changedWords[^1] |= 0x01000000;
            var lastChanged = await Raw(Arguments(Option(string.Join(',', changedWords))));
            check("last undisplayed-detail variant remains covered by the full request binding", Available(many) && Available(lastChanged)
                && Build(lastChanged)["variantsTotal"]?.GetValue<int>() == 32
                && Build(lastChanged)["variants"]!.AsArray().Take(30).Select(v => v!["assumedOptions"]!.GetValue<uint>())
                    .SequenceEqual(Build(many)["variants"]!.AsArray().Take(30).Select(v => v!["assumedOptions"]!.GetValue<uint>()))
                && (string?)Build(many)["binding"]?["fingerprint"] != (string?)Build(lastChanged)["binding"]?["fingerprint"]
                && Build(lastChanged)["assumedOptions"]!.AsArray().Last()!.GetValue<uint>() == changedWords[^1], lastChanged.ToJsonString());

            var tablePath = TableFixture(directory);
            check("native current-seed table fixture opens", !Error(await Tool("project_open", new { path = tablePath })), null);
            var tableProject = server.Project!; var tableDevice = ProjectQuery.DeviceList(tableProject).Single();
            foreach (var (format, kind) in new[] { ("m112", GeneratedTableFormat.M112), ("system_b_2", GeneratedTableFormat.SystemB2ByteAssociations), ("system_b_4", GeneratedTableFormat.SystemB4ByteAssociations) })
            {
                var tableResponse = await Raw(Arguments(Option("131", format)));
                var tableCore = OfflineNativeImageBuild.Preview(tableProject, tableDevice, new([131u], new(kind)));
                check("native table format " + format + " preserves Core availability and issues", !Error(tableResponse)
                    && Build(tableResponse)["available"]?.GetValue<bool>() == tableCore.IsAvailable
                    && (string?)Build(tableResponse)["binding"]?["fingerprint"] == tableCore.Binding?.Fingerprint && HashOnly(Build(tableResponse)) && Gates(tableResponse), tableResponse.ToJsonString());
                if (format == "m112")
                {
                    check("native M112 table uses current parameter CAFE header", Available(tableResponse) && tableCore.Variants.Single().Tables is { } generated
                        && generated.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress).BeforeWorkingData.ToArray().AsSpan(1, 2).SequenceEqual(new byte[] { 0xCA, 0xFE }), tableResponse.ToJsonString());
                    if (Available(tableResponse) && tableCore.Variants.Single().Tables is { } tables)
                    {
                        var projection = Variant(tableResponse, 131)["tables"]!;
                        check("native table projection hashes only actual working seed and validity roles", tables.Payloads.All(p =>
                        {
                            var json = projection["payloads"]!.AsArray().Single(x => (string?)x?["role"] == JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Role.ToString()))!;
                            return (string?)json["dataHash"] == p.Data.Sha256 && (string?)json["beforeWorkingDataHash"] == p.BeforeWorkingData.Sha256
                                && (string?)json["beforeValidityHash"] == p.BeforeValidity.Sha256 && (string?)json["afterValidityHash"] == p.AfterValidity.Sha256
                                && json["beforeDataHash"] is null;
                        }), projection.ToJsonString());
                    }
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", oldCatalog); }
    }
}
