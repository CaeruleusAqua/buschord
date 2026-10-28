using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class EnumPatchToolChecks
{
    private const string App = "M-FFE2_A-0001-01-0000";
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-enum-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 40000;
            async Task<JsonNode> Tool(string name, object arguments)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } });
                var response = JsonNode.Parse((await server.HandleAsync(request))!)!;
                return response["result"]?["structuredContent"] ?? throw new InvalidOperationException(response.ToJsonString());
            }
            async Task<(JsonNode Patch, bool Unchanged)> Preview(XDocument document, IReadOnlyList<ParameterPatchValue> values, string order = "big_endian")
            {
                var path = Fixture(root, Guid.NewGuid().ToString("N"), document); var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
                await Tool("project_open", new { path });
                var original = Knxproj.ToXml(server.Project!).ToString(); var history = (await Tool("project_info", new { })).ToJsonString();
                var ordinary = (await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
                var response = await Tool("download_plan", new { device_address = "1.1.1", byte_order = order,
                    parameter_subset = values.Select(v => new { refId = v.RefId, rawValue = v.RawValue }).ToArray() });
                var patch = response["offlineParameterPatch"]!;
                var unchanged = SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(sourceHash) && Knxproj.ToXml(server.Project!).ToString() == original
                    && (await Tool("project_info", new { })).ToJsonString() == history && (await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString() == ordinary
                    && server.Project!.Installations[0].Areas[0].Lines[0].Devices[0].ParameterValues.Count == 0
                    && response["plan"]!.ToJsonString() == JsonNode.Parse(ordinary)!["plan"]!.ToJsonString()
                    && response["plan"]?["program"] is null && response["plan"]?["ready"]?.GetValue<bool>() == false
                    && response["plan"]?["imageBytes"]?.GetValue<int>() == 0 && response["plan"]?["chunks"]?.AsArray().Count == 0
                    && response["writesEnabled"]?.GetValue<bool>() == false && server.Tools.Count == McpToolContract.ExpectedToolCount
                    && (string?)patch["scope"] == "ExplicitSubsetPreview" && patch["completeApplicationImage"]?.GetValue<bool>() == false
                    && patch["downloadEligible"]?.GetValue<bool>() == false;
                return (patch, unchanged);
            }
            static bool Available(JsonNode patch) => patch["available"]?.GetValue<bool>() == true;
            static bool Rejected(JsonNode patch) => patch["available"]?.GetValue<bool>() == false && patch["binding"] is null
                && patch["ranges"]?.AsArray().Count == 0 && patch["fields"]?.AsArray().Count == 0 && patch["issues"]?.AsArray().Count > 0;
            var sparse = await Preview(Application(), [new("R1", "300")]); var range = sparse.Patch["ranges"]?[0];
            check("enum sparse value is encoded rather than its ordinal label or Id", sparse.Unchanged && Available(sparse.Patch)
                && (string?)range?["afterData"] == "012C" && (string?)range?["fieldMask"] == "FFFF"
                && (string?)range?["beforeMask"] == "0180" && (string?)range?["afterMask"] == "FFFF"
                && (string?)sparse.Patch["fields"]?[0]?["rawValue"] == "300", sparse.Patch.ToJsonString());
            var reordered = Application(); var restriction = Shape(reordered); var choices = restriction.Elements().ToArray();
            restriction.ReplaceNodes(choices.Reverse());
            foreach (var choice in restriction.Elements()) { choice.SetAttributeValue("Text", "Another localized label"); choice.SetAttributeValue("DisplayOrder", "-77"); }
            var reorderedResult = await Preview(reordered, [new("R1", "300")]);
            check("enum source order translated labels and display order do not alter selector bytes", reorderedResult.Unchanged && Available(reorderedResult.Patch)
                && reorderedResult.Patch["ranges"]!.ToJsonString() == sparse.Patch["ranges"]!.ToJsonString()
                && (string?)reorderedResult.Patch["binding"]?["definitionHash"] != (string?)sparse.Patch["binding"]?["definitionHash"], reorderedResult.Patch.ToJsonString());
            var little = await Preview(Application(), [new("R1", "300")], "little_endian");
            check("enum LittleEndian uses the same declared selector with explicit byte order", little.Unchanged && Available(little.Patch)
                && (string?)little.Patch["ranges"]?[0]?["afterData"] == "2C01", little.Patch.ToJsonString());
            var highDocument = Application(); Shape(highDocument).SetAttributeValue("SizeInBit", "32");
            Shape(highDocument).Add(new XElement(highDocument.Root!.Name.Namespace + "Enumeration", new XAttribute("Id", "Ehigh"), new XAttribute("Value", "2147483648"), new XAttribute("Text", "High bit")));
            foreach (var order in new[] { "big_endian", "little_endian" })
            {
                var high = await Preview(highDocument, [new("R1", "2147483648")], order);
                check("enum UInt32 high bit stays unsigned in " + order, high.Unchanged && Available(high.Patch)
                    && (string?)high.Patch["ranges"]?[0]?["afterData"] == (order == "big_endian" ? "80000000" : "00000080")
                    && (string?)high.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFFFF", high.Patch.ToJsonString());
            }
            Shape(highDocument).Add(new XElement(highDocument.Root!.Name.Namespace + "Enumeration", new XAttribute("Id", "Emax"), new XAttribute("Value", "4294967295")));
            var maximum = await Preview(highDocument, [new("R1", "4294967295")]);
            check("enum UInt32 maximum is accepted without signed conversion or truncation", maximum.Unchanged && Available(maximum.Patch)
                && (string?)maximum.Patch["ranges"]?[0]?["afterData"] == "FFFFFFFF" && (string?)maximum.Patch["ranges"]?[0]?["fieldMask"] == "FFFFFFFF", maximum.Patch.ToJsonString());
            var bitDocument = Application(); Shape(bitDocument).SetAttributeValue("SizeInBit", "3");
            Shape(bitDocument).ReplaceNodes(new[] { "0", "3", "5" }.Select(v => new XElement(bitDocument.Root!.Name.Namespace + "Enumeration", new XAttribute("Id", "E" + v), new XAttribute("Value", v))));
            Parameter(bitDocument).Elements().Single().SetAttributeValue("BitOffset", "2");
            var bitData = Convert.FromBase64String(One(bitDocument, "Data").Value); bitData[0] = 0xD3; One(bitDocument, "Data").Value = Convert.ToBase64String(bitData);
            var bitMask = Convert.FromBase64String(One(bitDocument, "Mask").Value); bitMask[0] = 0x80; One(bitDocument, "Mask").Value = Convert.ToBase64String(bitMask);
            var subbyte = await Preview(bitDocument, [new("R1", "5")]);
            check("enum subbyte fields preserve unrelated data and existing mask bits", subbyte.Unchanged && Available(subbyte.Patch)
                && (string?)subbyte.Patch["ranges"]?[0]?["beforeData"] == "D3" && (string?)subbyte.Patch["ranges"]?[0]?["afterData"] == "EB"
                && (string?)subbyte.Patch["ranges"]?[0]?["fieldMask"] == "38" && (string?)subbyte.Patch["ranges"]?[0]?["afterMask"] == "B8", subbyte.Patch.ToJsonString());
            var noMask = Application(); One(noMask, "Mask").Remove(); var nullable = await Preview(noMask, [new("R1", "30")]);
            check("enum absent mask remains nullable with an independent known field mask", nullable.Unchanged && Available(nullable.Patch)
                && (string?)nullable.Patch["ranges"]?[0]?["afterData"] == "001E" && (string?)nullable.Patch["ranges"]?[0]?["fieldMask"] == "FFFF"
                && nullable.Patch["ranges"]?[0]?["beforeMask"] is null && nullable.Patch["ranges"]?[0]?["afterMask"] is null, nullable.Patch.ToJsonString());
            foreach (var value in new[] { "2", "Sparse three hundred", "E300", "-1", "4294967296", "030", "+30", " 30 " })
            {
                var invalid = await Preview(Application(), [new("R1", value)]);
                check("enum exact lexical membership rejects " + value, invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            var invalidDeclarations = new (string Name, Action<XDocument> Change)[]
            {
                ("BinaryValue base", d => Shape(d).SetAttributeValue("Base", "BinaryValue")),
                ("mixed BinaryValue metadata", d => Shape(d).Elements().First().SetAttributeValue("BinaryValue", "AA==")),
                ("empty BinaryValue metadata", d => Shape(d).Elements().First().SetAttributeValue("BinaryValue", "")),
                ("width33", d => Shape(d).SetAttributeValue("SizeInBit", "33")),
                ("unselected out-of-width choice", d => Shape(d).Elements().Last().SetAttributeValue("Value", "65536")),
                ("duplicate choices", d => Shape(d).Elements().Last().SetAttributeValue("Value", "30")),
                ("duplicate member Ids", d => Shape(d).Elements().Last().SetAttributeValue("Id", "E30")),
                ("noncanonical declared choice", d => Shape(d).Elements().Last().SetAttributeValue("Value", "0300")),
                ("negative declared choice", d => Shape(d).Elements().Last().SetAttributeValue("Value", "-1")),
                ("choice beyond UInt32", d => Shape(d).Elements().Last().SetAttributeValue("Value", "4294967296")),
                ("missing member Id", d => Shape(d).Elements().First().Attribute("Id")!.Remove()),
                ("foreign member attribute", d => Shape(d).Elements().First().SetAttributeValue(XNamespace.Get("urn:unproven") + "Value", "0")),
                ("member child content", d => Shape(d).Elements().First().Add(new XElement(d.Root!.Name.Namespace + "Unknown"))),
                ("unmodeled formatter attribute", d => Shape(d).SetAttributeValue("Increment", "1")),
                ("empty choices", d => Shape(d).RemoveNodes()),
                ("over4096 choices", d => Shape(d).ReplaceNodes(Enumerable.Range(0, 4097).Select(i => new XElement(d.Root!.Name.Namespace + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i))))),
            };
            foreach (var item in invalidDeclarations)
            {
                var document = Application(); item.Change(document); var invalid = await Preview(document, [new("R1", "30")]);
                check("enum source rejects " + item.Name + " atomically", invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
            foreach (var enumFirst in new[] { false, true })
            {
                ParameterPatchValue[] mixed = enumFirst ? [new("R1", "2"), new("R2", "7")] : [new("R2", "7"), new("R1", "2")];
                var invalid = await Preview(Application(), mixed);
                check("mixed valid numeric and invalid enum selection has no partial bytes, enumFirst=" + enumFirst,
                    invalid.Unchanged && Rejected(invalid.Patch), invalid.Patch.ToJsonString());
            }
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(root, true); }
    }
    private static XElement One(XDocument document, string name) => document.Descendants().Single(e => e.Name.LocalName == name);
    private static XElement Shape(XDocument document) => One(document, "TypeRestriction");
    private static XElement Parameter(XDocument document) => document.Descendants().Single(e => e.Name.LocalName == "Parameter" && (string?)e.Attribute("Id") == "P1");
    private static XDocument Application() => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{App}}"><Static><Code>
        <AbsoluteSegment Id="AS" Address="4096" Size="8"><Data>{{Convert.ToBase64String([0xA5,0x5A,0,0,0,0,0,0])}}</Data><Mask>{{Convert.ToBase64String([1,128,32,64,0,0,0,0])}}</Mask></AbsoluteSegment>
        </Code><ParameterTypes><ParameterType Id="E" Name="Sparse"><TypeRestriction Base="Value" SizeInBit="16" UIHint="DropDown">
        <Enumeration Id="E0" Value="0" Text="Disabled" DisplayOrder="50"/><Enumeration Id="E30" Value="30" Text="Thirty label" DisplayOrder="3"/>
        <Enumeration Id="E300" Value="300" Text="Sparse three hundred" DisplayOrder="-2"/></TypeRestriction></ParameterType>
        <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P1" ParameterType="E" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter>
        <Parameter Id="P2" ParameterType="N" Value="0"><Memory CodeSegment="AS" Offset="6" BitOffset="0"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/></ParameterRefs>
        </Static><Dynamic><ParameterRefRef RefId="R1"/><ParameterRefRef RefId="R2"/></Dynamic></ApplicationProgram></KNX>
        """);
    private static string Fixture(string root, string name, XDocument definition)
    {
        var path = Path.Combine(root, name + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Synthetic unsigned enum subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); using var output = archive.CreateEntry("M-FFE2/" + App + ".xml").Open(); definition.Save(output);
        return path;
    }
}
