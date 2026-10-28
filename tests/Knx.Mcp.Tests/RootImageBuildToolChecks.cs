using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class RootImageBuildToolChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string Selection = "\"load_procedure_preview\":{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false}";

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-mcp-root-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 51000;
            async Task<JsonNode> Tool(string name, object arguments) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } })))!)!;
            async Task<JsonNode> Raw(string arguments) => JsonNode.Parse((await server.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            static bool Error(JsonNode node) => node["error"] is not null || node["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode node) => node["result"]!["structuredContent"]!;
            static JsonNode Build(JsonNode node) => Data(node)["offlineRootImageBuild"]!;
            async Task<JsonNode> Preview(string policy = "source_defined_bits") => await Raw("{\"device_address\":\"1.1.1\"," + Selection
                + ",\"root_image_build_preview\":{\"mask_policy\":\"" + policy + "\"}}");
            static bool Gates(JsonNode node)
            {
                var data = Data(node); var build = Build(node);
                return data["writesEnabled"]?.GetValue<bool>() == false && data["realDownloadAvailable"]?.GetValue<bool>() == false
                    && data["plan"]?["ready"]?.GetValue<bool>() == false && data["plan"]?["program"] is null
                    && data["plan"]?["imageBytes"]?.GetValue<int>() == 0 && data["plan"]?["chunks"]?.AsArray().Count == 0
                    && build["completeApplicationImage"]?.GetValue<bool>() == false && build["executionEligible"]?.GetValue<bool>() == false
                    && build["downloadEligible"]?.GetValue<bool>() == false;
            }

            var path = Fixture(directory, "whole", Application(130));
            var archive = SHA256.HashData(File.ReadAllBytes(path));
            check("root working fixture opens", !Error(await Tool("project_open", new { path })), null);
            var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single();
            device.ParameterValues["R-0"] = "16909060";
            var model = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
            var info = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            check("absent root build preserves the ordinary response", !ordinary.Contains("offlineRootImageBuild", StringComparison.Ordinal), ordinary);
            var schema = server.Tools.Single(tool => tool.Name == "download_plan").InputSchema;
            check("root build is a closed explicit policy on the current tool contract", server.Tools.Count == McpToolContract.ExpectedToolCount
                && (string?)schema["properties"]?["root_image_build_preview"]?["type"] == "object"
                && schema["properties"]?["root_image_build_preview"]?["additionalProperties"]?.GetValue<bool>() == false
                && schema["dependentRequired"]?["root_image_build_preview"]?.AsArray().Select(x => (string?)x).SequenceEqual(["load_procedure_preview"]) == true, null);
            foreach (var (name, option, expected) in new[]
            {
                ("boolean", "\"root_image_build_preview\":true", "must be an object"),
                ("null", "\"root_image_build_preview\":null", "must be an object"),
                ("missing policy", "\"root_image_build_preview\":{}", "requires an explicit"),
                ("non-string policy", "\"root_image_build_preview\":{\"mask_policy\":false}", "requires an explicit"),
                ("unknown policy", "\"root_image_build_preview\":{\"mask_policy\":\"full\"}", "mask_policy must"),
                ("duplicate option", "\"root_image_build_preview\":{},\"root_image_build_preview\":{}", "must occur exactly once"),
                ("duplicate policy", "\"root_image_build_preview\":{\"mask_policy\":\"source_defined_bits\",\"mask_policy\":\"no_static_source_bits\"}", "allows exactly one"),
                ("extra values", "\"root_image_build_preview\":{\"mask_policy\":\"source_defined_bits\",\"parameter_subset\":[]}", "allows exactly one"),
            })
            {
                var rejected = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-root-image-gateway\"," + Selection + "," + option + "}");
                check("root build rejects " + name + " before gateway access", Error(rejected)
                    && rejected.ToJsonString().Contains(expected, StringComparison.Ordinal)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }
            var missing = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-root-image-gateway\",\"root_image_build_preview\":{\"mask_policy\":\"source_defined_bits\"}}");
            check("root build requires an explicit procedure selection before gateway access", Error(missing)
                && missing.ToJsonString().Contains("requires an explicit load_procedure_preview", StringComparison.Ordinal), missing.ToJsonString());
            foreach (var (name, extra, selection) in new[]
            {
                ("subset and caller byte order", ",\"parameter_subset\":[{\"refId\":\"R-0\",\"rawValue\":\"1\"}],\"byte_order\":\"big_endian\"", Selection),
                ("source-only composition", ",\"image_composition_preview\":{\"mask_policy\":\"source_defined_bits\"}", Selection),
                ("resolved conditions", "", Selection[..^1] + ",\"resolve_conditions\":true}"),
                ("groups-only parts", "", "\"load_procedure_preview\":{\"subtype\":\"all\",\"access\":\"remote\",\"full\":false,\"parameters\":false,\"groups\":true}"),
            })
            {
                var rejected = await Raw("{\"device_address\":\"1.1.1\",\"gateway\":\"invalid-root-image-gateway\"," + selection
                    + ",\"root_image_build_preview\":{\"mask_policy\":\"source_defined_bits\"}" + extra + "}");
                check("root build refuses " + name + " before gateway access", Error(rejected)
                    && !rejected.ToJsonString().Contains("descriptor read failed", StringComparison.Ordinal), rejected.ToJsonString());
            }
            var response = await Preview(); var build = Build(response);
            check("root build handles 130 references without an explicit subset", !Error(response) && build["available"]?.GetValue<bool>() == true
                && (string?)build["scope"] == "RootFixedMemoryWorkingSet" && build["completeFixedRootPopulation"]?.GetValue<bool>() == true
                && build["population"]?["referenceCount"]?.GetValue<int>() == 130 && Data(response)["offlineParameterPatch"] is null, response.ToJsonString());
            if (build["available"]?.GetValue<bool>() == true)
            {
                var chunk = build["chunks"]!.AsArray().Single()!;
                var expected = ExpectedWords(130, false); var mask = Enumerable.Repeat((byte)255, 520).Concat(new byte[4]).ToArray();
                check("all 520 parameter bytes and the unknown tail match an independent serializer", chunk["byteLength"]?.GetValue<int>() == 524
                    && (string?)chunk["workingDataHash"] == Hash(expected) && (string?)chunk["canonicalDesiredHash"] == Hash(expected)
                    && (string?)chunk["desiredValidityHash"] == Hash(mask) && build["desiredBitCount"]?.GetValue<ulong>() == 4160
                    && build["unspecifiedBitCount"]?.GetValue<ulong>() == 32, build.ToJsonString());
                check("root population and claim caps retain totals and binding", build["population"]?["recordsTotal"]?.GetValue<int>() == 130
                    && build["population"]?["recordsShown"]?.GetValue<int>() == 128 && build["population"]?["recordsTruncated"]?.GetValue<bool>() == true
                    && build["claimsTotal"]?.GetValue<int>() == 130 && build["claimsShown"]?.GetValue<int>() == 128
                    && build["claimsTruncated"]?.GetValue<bool>() == true && build["binding"]?["fingerprint"]?.GetValue<string>() is { Length: 64 }, build.ToJsonString());
                check("root policy uses original native byte-order and options defaults", (string?)build["policy"]?["byteOrder"] == "big_endian"
                    && (string?)build["policy"]?["downloadBehavior"] == "default_value" && (string?)build["policy"]?["customerPolicy"] == "IncludeAll", build.ToJsonString());
                check("root binding identifies geometry and unresolved requirements", build["binding"]?["geometryHash"]?.GetValue<string>() is { Length: 64 }
                    && (string?)build["binding"]?["geometryHash"] == (string?)build["population"]?["geometryHash"]
                    && build["binding"]?["requirementsHash"]?.GetValue<string>() is { Length: 64 }
                    && build["requirementsTotal"]?.GetValue<int>() > 0, build.ToJsonString());
                check("root projection exposes hashes and geometry without raw buffers or words", chunk["workingData"] is null
                    && chunk["canonicalDesired"] is null && chunk["desiredValidity"] is null
                    && build["population"]?["records"]?[0]?["rawValue"] is null && build["claims"]?[0]?["encodedWord"] is null
                    && build["sourceSegments"]?[0]?["rawData"] is null, build.ToJsonString());
                check("root request uses its one shared result instead of a second standalone build", Data(response)["offlineApplicationBuild"] is null
                    && build["procedure"]?["available"]?.GetValue<bool>() == true && build["procedure"]?["controlsTotal"]?.GetValue<int>() == 2, response.ToJsonString());
            }
            check("root build keeps every device/programming gate closed", Gates(response), response.ToJsonString());
            check("root request preserves model, undo, archive and ordinary MCP output", model == Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting)
                && info == Data(await Tool("project_info", new { })).ToJsonString() && archive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))
                && ordinary == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString(), null);

            var littlePath = Fixture(directory, "little", Application(130, "LittleEndian"));
            await Tool("project_open", new { path = littlePath }); ProjectQuery.DeviceList(server.Project!).Single().ParameterValues["R-0"] = "16909060";
            var little = Build(await Preview());
            check("native LittleEndian source selects all words without a caller byte_order", little["available"]?.GetValue<bool>() == true
                && (string?)little["policy"]?["byteOrder"] == "little_endian"
                && (string?)little["chunks"]?[0]?["canonicalDesiredHash"] == Hash(ExpectedWords(130, true)), little.ToJsonString());

            var partial = Application(1);
            var partialApp = partial.Descendants(Ns + "ApplicationProgram").Single(); var partialStat = partialApp.Element(Ns + "Static")!;
            partialStat.Element(Ns + "Code")!.ReplaceNodes(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "S"),
                new XAttribute("Address", 16), new XAttribute("Size", 1), new XElement(Ns + "Data", Convert.ToBase64String([0xA5])),
                new XElement(Ns + "Mask", Convert.ToBase64String([0xF0]))));
            var shape = partialStat.Descendants(Ns + "TypeNumber").Single(); shape.SetAttributeValue("SizeInBit", 2); shape.SetAttributeValue("maxInclusive", 3);
            partialStat.Element(Ns + "Parameters")!.Element(Ns + "Parameter")!.SetAttributeValue("Value", 2);
            partialStat.Descendants(Ns + "Memory").Single().SetAttributeValue("BitOffset", 6);
            var partialPath = Fixture(directory, "partial", partial); await Tool("project_open", new { path = partialPath });
            var retained = Build(await Preview()); var cleared = Build(await Preview("no_static_source_bits"));
            check("whole-byte validity preserves partial-mask raw seed bits", retained["available"]?.GetValue<bool>() == true
                && (string?)retained["chunks"]?[0]?["workingDataHash"] == Hash([0xA6])
                && (string?)retained["chunks"]?[0]?["canonicalDesiredHash"] == Hash([0xA6])
                && (string?)retained["chunks"]?[0]?["desiredValidityHash"] == Hash([255]), retained.ToJsonString());
            check("explicit validity reset keeps working seed and remains separately bound", cleared["available"]?.GetValue<bool>() == true
                && (string?)cleared["chunks"]?[0]?["workingDataHash"] == Hash([0xA6])
                && (string?)cleared["binding"]?["fingerprint"] != (string?)retained["binding"]?["fingerprint"], cleared.ToJsonString());

            var unsupported = Application(2);
            unsupported.Descendants(Ns + "Parameters").Single().Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-latent"),
                new XAttribute("ParameterType", "T"), new XAttribute("Value", 1), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "S"),
                    new XAttribute("Offset", 0), new XAttribute("BitOffset", 0))));
            var unsupportedPath = Fixture(directory, "latent-overlap", unsupported); await Tool("project_open", new { path = unsupportedPath });
            var refused = Build(await Preview());
            check("unreferenced physical owners cannot disappear from overlap closure", refused["available"]?.GetValue<bool>() == false
                && refused["binding"] is null && refused["population"] is null && refused["claims"]?.AsArray().Count == 0
                && refused["chunks"]?.AsArray().Count == 0 && refused["completeFixedRootPopulation"]?.GetValue<bool>() == false, refused.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", oldCatalog); Directory.Delete(directory, true); }
    }

    internal static XDocument Application(int count, string? order = null)
    {
        var doc = XDocument.Parse(ResourcePatchToolChecks.ApplicationXml); var app = doc.Descendants(Ns + "ApplicationProgram").Single();
        app.SetAttributeValue("MinEtsVersion", "6.0"); app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        var stat = app.Element(Ns + "Static")!;
        stat.Element(Ns + "Code")!.ReplaceNodes(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "S"),
            new XAttribute("Address", 16), new XAttribute("Size", count * 4 + 4)));
        stat.Element(Ns + "ParameterTypes")!.ReplaceNodes(new XElement(Ns + "ParameterType", new XAttribute("Id", "T"),
            new XElement(Ns + "TypeNumber", new XAttribute("SizeInBit", 32), new XAttribute("Type", "unsignedInt"),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", "4294967295"))));
        stat.Element(Ns + "Parameters")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "Parameter",
            new XAttribute("Id", "P-" + i), new XAttribute("ParameterType", "T"), new XAttribute("Value", i + 1),
            new XElement(Ns + "Memory", new XAttribute("CodeSegment", "S"), new XAttribute("Offset", i * 4), new XAttribute("BitOffset", 0)))));
        stat.Element(Ns + "ParameterRefs")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "ParameterRef",
            new XAttribute("Id", "R-" + i), new XAttribute("RefId", "P-" + i))));
        app.Element(Ns + "Dynamic")!.ReplaceNodes(new XElement(Ns + "ChannelIndependentBlock", new XElement(Ns + "ParameterBlock", new XAttribute("Id", "PB"),
            Enumerable.Range(0, count).Select(i => new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R-" + i))))));
        stat.AddFirst(new XElement(Ns + "Options", order is null ? null : new XAttribute("ParameterByteOrder", order)));
        stat.Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"), new XElement(Ns + "LdCtrlDisconnect"))));
        return doc;
    }
    internal static string Fixture(string directory, string name, XDocument application)
    {
        var path = ResourcePatchToolChecks.Fixture(directory, name, application);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry("M-FFE8/Hardware.xml")!; XDocument hardware;
        using (var stream = entry.Open()) hardware = XDocument.Load(stream);
        var selected = hardware.Descendants(Ns + "Hardware2Program").Single(); selected.Attribute("LoadedImage")?.Remove(); selected.Attribute("CheckSums")?.Remove(); entry.Delete();
        using var output = new StreamWriter(zip.CreateEntry("M-FFE8/Hardware.xml").Open(), new UTF8Encoding(false)); output.Write(hardware.ToString());
        return path;
    }
    private static byte[] ExpectedWords(int count, bool little)
    {
        var bytes = new byte[count * 4 + 4];
        for (var i = 0; i < count; i++)
        {
            var value = i == 0 ? 0x01020304u : (uint)i + 1;
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), value);
            else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), value);
        }
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
