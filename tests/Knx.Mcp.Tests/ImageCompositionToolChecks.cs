using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class ImageCompositionToolChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    public static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-mcp-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer();
            var sequence = 38000;
            async Task<JsonNode> Tool(string name, object arguments) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } })))!)!;
            async Task<JsonNode> Raw(string arguments) => JsonNode.Parse((await server.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            static bool Error(JsonNode response) => response["error"] is not null || response["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode response) => response["result"]!["structuredContent"]!;
            static JsonNode Composition(JsonNode response) => Data(response)["offlineImageComposition"]!;
            object Selection(string policy = "source_defined_bits") => new
            { device_address = "1.1.1", image_composition_preview = new { mask_policy = policy } };
            var path = Fixture(directory, "tail", Code(Segment("S", 2, 4, [0xA5], [0xF0])));
            var archive = SHA256.HashData(File.ReadAllBytes(path));
            check("composition fixture opens without a factory blob", !Error(await Tool("project_open", new { path })), null);
            var model = Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting);
            var info = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            check("absent composition leaves the ordinary response without a new null field", !ordinary.Contains("offlineImageComposition", StringComparison.Ordinal), ordinary);
            var schema = server.Tools.Single(tool => tool.Name == "download_plan").InputSchema;
            check("composition is an explicit closed policy object on the current tool contract", server.Tools.Count == McpToolContract.ExpectedToolCount
                && (string?)schema["properties"]?["image_composition_preview"]?["type"] == "object"
                && schema["properties"]?["image_composition_preview"]?["additionalProperties"]?.GetValue<bool>() == false
                && schema["properties"]?["image_composition_preview"]?["properties"]?["mask_policy"]?["enum"]?.AsArray().Count == 2, null);
            foreach (var (name, arguments, expected) in new[]
            {
                ("boolean", """{"image_composition_preview":true}""", "must be an object"),
                ("null", """{"image_composition_preview":null}""", "must be an object"),
                ("missing policy", """{"image_composition_preview":{}}""", "requires an explicit"),
                ("unknown policy", """{"image_composition_preview":{"mask_policy":"full"}}""", "mask_policy must"),
                ("non-string policy", """{"image_composition_preview":{"mask_policy":1}}""", "requires an explicit"),
                ("duplicate option", """{"image_composition_preview":{},"image_composition_preview":{}}""", "must occur exactly once"),
                ("duplicate policy", """{"image_composition_preview":{"mask_policy":"source_defined_bits","mask_policy":"no_static_source_bits"}}""", "allows exactly one"),
                ("extra writer", """{"image_composition_preview":{"mask_policy":"source_defined_bits","parameter_subset":[]}}""", "allows exactly one"),
            })
            {
                var rejected = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-composition-gateway\"," + arguments[1..]);
                check("composition rejects " + name + " before gateway access", Error(rejected)
                    && rejected.ToJsonString().Contains(expected, StringComparison.Ordinal)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }
            var response = await Tool("download_plan", Selection()); var composition = Composition(response);
            check("root source composition is available without factory data", !Error(response) && composition["available"]?.GetValue<bool>() == true
                && (string?)composition["scope"] == "RootStaticCodeWorkingSet", response.ToJsonString());
            if (composition["available"]?.GetValue<bool>() == true)
            {
                var source = composition["sourceSegments"]!.AsArray().Single()!;
                var chunk = composition["chunks"]!.AsArray().Single()!;
                var span = composition["unspecifiedSpans"]!.AsArray().Single()!;
                check("raw supplied facts stay separate from allocated tail", source["rawDataLength"]?.GetValue<int>() == 1
                    && source["rawMaskLength"]?.GetValue<int>() == 1 && source["declaredSize"]?.GetValue<uint>() == 4
                    && source["allocationLength"]?.GetValue<int>() == 4 && (string?)source["rawDataHash"] == Hash([0xA5])
                    && (string?)source["rawMaskHash"] == Hash([0xF0]), source.ToJsonString());
                check("working mask preserves exact partial bits and unknown tail", (string?)chunk["desiredDataHash"] == Hash([0xA0, 0, 0, 0])
                    && (string?)chunk["desiredMaskHash"] == Hash([0xF0, 0, 0, 0]) && chunk["byteLength"]?.GetValue<int>() == 4
                    && composition["desiredBitCount"]?.GetValue<ulong>() == 4 && composition["unspecifiedBitCount"]?.GetValue<ulong>() == 28
                    && span["byteOffsetInArena"]?.GetValue<uint>() == 2 && span["byteLength"]?.GetValue<int>() == 4
                    && span["unspecifiedBitCount"]?.GetValue<ulong>() == 28, composition.ToJsonString());
                check("composition exposes no full buffers or device-read actions", chunk["desiredData"] is null && chunk["desiredMask"] is null
                    && source["rawData"] is null && source["rawMask"] is null && composition["readActions"] is null
                    && composition["binding"]?["fingerprint"]?.GetValue<string>() is { Length: 64 }, composition.ToJsonString());
                check("composition cannot authorize images, writes or execution", composition["completeApplicationImage"]?.GetValue<bool>() == false
                    && composition["downloadEligible"]?.GetValue<bool>() == false && composition["executionEligible"]?.GetValue<bool>() == false
                    && Data(response)["writesEnabled"]?.GetValue<bool>() == false && Data(response)["realDownloadAvailable"]?.GetValue<bool>() == false
                    && Data(response)["plan"]?["ready"]?.GetValue<bool>() == false && Data(response)["plan"]?["program"] is null, response.ToJsonString());
                check("composition retains every omitted writer stage as a requirement", composition["unresolvedRequirements"]!.AsArray()
                    .Select(requirement => (string?)requirement!["code"]).Contains("mask_memory_sources_not_composed")
                    && composition["unresolvedRequirements"]!.AsArray().Any(requirement => (string?)requirement!["code"] == "parameter_writers_not_applied"), composition.ToJsonString());
            }
            var cleared = Composition(await Tool("download_plan", Selection("no_static_source_bits")));
            check("explicit empty source contributions do not fabricate before bytes", cleared["available"]?.GetValue<bool>() == true
                && cleared["desiredBitCount"]?.GetValue<ulong>() == 0 && cleared["unspecifiedBitCount"]?.GetValue<ulong>() == 32
                && (string?)cleared["chunks"]?[0]?["desiredMaskHash"] == Hash([0, 0, 0, 0])
                && (string?)cleared["sourceSegments"]?[0]?["rawDataHash"] == Hash([0xA5])
                && (string?)cleared["binding"]?["fingerprint"] != (string?)composition["binding"]?["fingerprint"], cleared.ToJsonString());
            var withFull = Composition(await Raw("""{"device_address":"1.1.1","image_composition_preview":{"mask_policy":"no_static_source_bits"},"load_procedure_preview":{"subtype":"all","access":"remote","full":true,"parameters":false,"groups":false}}"""));
            check("procedure Full never replaces the explicit composition policy", cleared.ToJsonString() == withFull.ToJsonString(), withFull.ToJsonString());
            check("composition preserves original model, undo, source archive and ordinary JSON", model == Knxproj.ToXml(server.Project!).ToString(SaveOptions.DisableFormatting)
                && info == Data(await Tool("project_info", new { })).ToJsonString() && archive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                && ordinary == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString(), null);

            var overlapPath = Fixture(directory, "overlap", Code(Segment("A", 0, 1, [0xA5], [0xF0]), Segment("B", 0, 1, [0x3C], [0x0F])));
            await Tool("project_open", new { path = overlapPath });
            var compatible = Composition(await Tool("download_plan", Selection()));
            check("complementary sources compose their exact defined bits", compatible["available"]?.GetValue<bool>() == true
                && (string?)compatible["chunks"]?[0]?["desiredDataHash"] == Hash([0xAC])
                && (string?)compatible["chunks"]?[0]?["desiredMaskHash"] == Hash([0xFF]) && compatible["unspecifiedBitCount"]?.GetValue<ulong>() == 0, compatible.ToJsonString());
            var conflictPath = Fixture(directory, "conflict", Code(Segment("A", 0, 1, [0x80], [0x80]), Segment("B", 0, 1, [0], [0x80])));
            await Tool("project_open", new { path = conflictPath });
            var conflict = Composition(await Tool("download_plan", Selection()));
            check("conflicting source bits discard all composition artifacts", conflict["available"]?.GetValue<bool>() == false
                && conflict["binding"] is null && conflict["sourceSegments"]?.AsArray().Count == 0 && conflict["chunks"]?.AsArray().Count == 0
                && conflict["unspecifiedSpans"]?.AsArray().Count == 0
                && conflict["issues"]!.AsArray().Any(issue => (string?)issue!["code"] == "conflicting_source_bits"), conflict.ToJsonString());

            var manyPath = Fixture(directory, "many", Code(Enumerable.Range(0, 130).Select(i => Segment("S" + i, (uint)i * 2, 1, [0xA5])).ToArray()));
            await Tool("project_open", new { path = manyPath });
            var many = Composition(await Tool("download_plan", Selection()));
            check("source and chunk presentation caps retain complete binding counts", many["available"]?.GetValue<bool>() == true
                && many["sourceSegmentsTotal"]?.GetValue<int>() == 130 && many["sourceSegmentsShown"]?.GetValue<int>() == 128
                && many["sourceSegmentsTruncated"]?.GetValue<bool>() == true && many["chunksTotal"]?.GetValue<int>() == 130
                && many["chunksShown"]?.GetValue<int>() == 128 && many["chunksTruncated"]?.GetValue<bool>() == true, many.ToJsonString());
            var sourceIdsPath = Fixture(directory, "source-ids", Code(Enumerable.Range(0, 130).Select(i => Segment("S" + i, 0, 1, [0xA5])).ToArray()));
            await Tool("project_open", new { path = sourceIdsPath });
            var sourceIds = Composition(await Tool("download_plan", Selection()));
            check("coalesced source identities report their presentation cap", sourceIds["available"]?.GetValue<bool>() == true
                && sourceIds["chunksTotal"]?.GetValue<int>() == 1 && sourceIds["chunks"]?[0]?["sourceIdsTotal"]?.GetValue<int>() == 130
                && sourceIds["chunks"]?[0]?["sourceIdsShown"]?.GetValue<int>() == 128
                && sourceIds["chunks"]?[0]?["sourceIdsTruncated"]?.GetValue<bool>() == true, sourceIds.ToJsonString());
            var alternating = Enumerable.Range(0, 513).Select(i => i % 2 == 0 ? (byte)0 : (byte)255).ToArray();
            var spansPath = Fixture(directory, "spans", Code(Segment("S", 0, 513, new byte[513], alternating)));
            await Tool("project_open", new { path = spansPath });
            var spans = Composition(await Tool("download_plan", Selection()));
            check("unspecified-span presentation cap does not change bit totals", spans["available"]?.GetValue<bool>() == true
                && spans["unspecifiedSpansTotal"]?.GetValue<int>() == 257 && spans["unspecifiedSpansShown"]?.GetValue<int>() == 256
                && spans["unspecifiedSpansTruncated"]?.GetValue<bool>() == true && spans["unspecifiedBitCount"]?.GetValue<ulong>() == 2056, spans.ToJsonString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog);
            Directory.Delete(directory, true);
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static XElement Segment(string id, uint offset, uint size, byte[]? data = null, byte[]? mask = null)
        => new(Ns + "RelativeSegment", new XAttribute("Id", id), new XAttribute("Offset", offset), new XAttribute("Size", size), new XAttribute("LoadStateMachine", "7"),
            data is null ? null : new XElement(Ns + "Data", Convert.ToBase64String(data)), mask is null ? null : new XElement(Ns + "Mask", Convert.ToBase64String(mask)));
    private static XDocument Code(params XElement[] segments)
    {
        var document = XDocument.Parse(ResourcePatchToolChecks.ApplicationXml);
        document.Descendants(Ns + "Code").Single().ReplaceNodes(segments);
        foreach (var container in document.Descendants().Where(element => element.Name.LocalName is "ParameterTypes" or "Parameters" or "ParameterRefs" or "Dynamic").ToArray()) container.RemoveNodes();
        return document;
    }
    private static string Fixture(string directory, string name, XDocument application)
    {
        var path = ResourcePatchToolChecks.Fixture(directory, name, application);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("M-FFE8/Hardware.xml")!; XDocument hardware;
        using (var stream = entry.Open()) hardware = XDocument.Load(stream);
        var selected = hardware.Descendants(Ns + "Hardware2Program").Single();
        selected.Attribute("LoadedImage")!.Remove(); selected.Attribute("CheckSums")!.Remove(); entry.Delete();
        using var output = new StreamWriter(zip.CreateEntry("M-FFE8/Hardware.xml").Open(), new UTF8Encoding(false));
        output.Write(hardware.ToString());
        return path;
    }
}
