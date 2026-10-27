using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class MaskProfileToolChecks
{
    private const string App = "M-0001_A-mask-tool";
    private const string Mask = "MV-0705";
    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-mask-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "library"));
        try
        {
            await using var server = new McpServer(); var sequence = 20000;
            async Task<JsonNode> Call(string name, object args)
            {
                var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments = args } });
                return JsonNode.Parse((await server.HandleAsync(request))!)!;
            }
            async Task<JsonNode> OpenAndPlan(string path)
            {
                var open = await Call("project_open", new { path });
                if (open["result"]?["isError"]?.GetValue<bool>() == true) throw new InvalidOperationException(open.ToJsonString());
                var response = await Call("download_plan", new { device_address = "1.1.1" });
                if (response["result"]?["isError"]?.GetValue<bool>() == true) throw new InvalidOperationException(response.ToJsonString());
                return response["result"]!["structuredContent"]!;
            }
            var master = Master(); var raw = Encoding.UTF8.GetBytes(master.ToString());
            var path = Fixture(directory, "supported", raw);
            var original = File.ReadAllBytes(path); var content = await OpenAndPlan(path); var profile = content["archiveMaskProfile"]!;
            check("mask tool remains additive to the existing 55 tools", server.Tools.Count == 55 && server.Tools.Count(t => t.Name == "download_plan") == 1
                && !server.Tools.Any(t => t.Name == "archive_mask_profile"), null);
            check("download plan selects exact archived current-zero profile without scripts", (string?)profile["source"] == "archive:knx_master.xml"
                && profile["readOnly"]?.GetValue<bool>() == true && (string?)profile["status"] == "supported"
                && (string?)profile["applicationMaskVersionId"] == Mask && (string?)profile["selectedMaskVersionId"] == Mask
                && profile["numericMask"]?.GetValue<int>() == 1797 && profile["requestedLegacyVersion"]?.GetValue<int>() == 0
                && profile["selectedLegacyVersion"]?.GetValue<int>() == 0 && profile["firstAppObjectIndex"]?.GetValue<int>() == 3, content.ToJsonString());
            check("download plan reports source identity separately from selected facts", (string?)profile["archiveMasterSha256"] == Convert.ToHexStringLower(SHA256.HashData(raw))
                && profile["declaredInterfaceObjectCount"]?.GetValue<int>() == 1 && profile["declaredPropertyCount"]?.GetValue<int>() == 1
                && (string?)profile["selectedProfileFingerprint"] is { Length: 64 } fingerprint && fingerprint != (string?)profile["archiveMasterSha256"], profile.ToJsonString());
            var baselinePlan = content["plan"]!.ToJsonString();
            check("archive facts grant no program image readiness chunks or writes", content["writesEnabled"]?.GetValue<bool>() == false
                && content["realDownloadAvailable"]?.GetValue<bool>() == false && content["plan"]?["ready"]?.GetValue<bool>() == false
                && content["plan"]?["program"] is null && content["plan"]?["imageBytes"]?.GetValue<int>() == 0
                && content["plan"]?["chunks"]?.AsArray().Count == 0, content.ToJsonString());
            var before = Knxproj.ToXml(server.Project!).ToString();
            check("archive metadata inspection leaves the owned source fixture byte-identical", File.ReadAllBytes(path).SequenceEqual(original), null);
            File.Delete(path);
            var repeat = (await Call("download_plan", new { device_address = "1.1.1" }))["result"]!["structuredContent"]!;
            check("metadata reads immutable archive bytes after original file removal without undo", repeat["archiveMaskProfile"]!.ToJsonString() == profile.ToJsonString()
                && repeat["plan"]!.ToJsonString() == baselinePlan && Knxproj.ToXml(server.Project!).ToString() == before
                && (await Call("project_info", new { }))["result"]?["structuredContent"]?["undo"]?["canUndo"]?.GetValue<bool>() == false, repeat.ToJsonString());

            var cases = new List<(string Name, byte[]? Bytes, string Status, bool Nested)>
            {
                ("missing", null, "missing_master_data", false),
                ("nested-only", raw, "missing_master_data", true),
                ("malformed", Encoding.UTF8.GetBytes("<KNX>"), "unreadable_master_data", false),
            };
            XDocument Change(Action<XDocument> action) { var copy = Master(); action(copy); return copy; }
            XElement Current(XDocument doc) => doc.Descendants().Single(e => e.Name.LocalName == "HawkConfigurationData" && e.Attribute("LegacyVersion") is null);
            cases.Add(("wrong-exact-mask", Encoding.UTF8.GetBytes(Change(d => d.Descendants().Single(e => e.Name.LocalName == "MaskVersion").SetAttributeValue("Id", Mask + "-extra")).ToString()), "mask_not_found", false));
            cases.Add(("missing-first", Encoding.UTF8.GetBytes(Change(d => Current(d).Descendants().Single(e => e.Name.LocalName == "Feature").Remove()).ToString()), "missing_first_app_object", false));
            cases.Add(("ambiguous-current", Encoding.UTF8.GetBytes(Change(d => Current(d).AddAfterSelf(new XElement(Current(d)))).ToString()), "ambiguous", false));
            cases.Add(("legacy-only", Encoding.UTF8.GetBytes(Change(d => Current(d).Remove()).ToString()), "missing_hawk_data", false));
            foreach (var item in cases)
            {
                var fixture = Fixture(directory, item.Name, item.Bytes, item.Nested); var fixtureHash = SHA256.HashData(File.ReadAllBytes(fixture));
                var result = await OpenAndPlan(fixture); var diagnostic = result["archiveMaskProfile"]!;
                check("mask tool " + item.Name + " is diagnostic without fallback or plan changes", (string?)diagnostic["status"] == item.Status
                    && diagnostic["firstAppObjectIndex"] is null && diagnostic["selectedLegacyVersion"] is null && diagnostic["selectedMaskVersionId"] is null
                    && result["plan"]!.ToJsonString() == baselinePlan && SHA256.HashData(File.ReadAllBytes(fixture)).SequenceEqual(fixtureHash), result.ToJsonString());
            }
            var oversizedMask = new string('X', 4096);
            var oversized = await OpenAndPlan(Fixture(directory, "oversized-reference", raw, mask: oversizedMask));
            var invalid = oversized["archiveMaskProfile"]!;
            check("mask tool rejects oversized references without reflecting their raw value", (string?)invalid["status"] == "invalid_declaration"
                && invalid["applicationMaskVersionId"] is null && invalid["selectedMaskVersionId"] is null && invalid["firstAppObjectIndex"] is null
                && !invalid.ToJsonString().Contains(oversizedMask, StringComparison.Ordinal) && oversized["plan"]!.ToJsonString() == baselinePlan, invalid.ToJsonString());
            var master21 = Encoding.UTF8.GetBytes(Master("http://knx.org/xml/project/21").ToString(SaveOptions.DisableFormatting));
            var app22 = await OpenAndPlan(Fixture(directory, "application22-master21", master21, applicationNamespace: "http://knx.org/xml/project/22"));
            var unsupportedNamespace = app22["archiveMaskProfile"]!;
            check("application22 cannot acquire derived mask support from a master21 profile",
                (string?)unsupportedNamespace["status"] == "unsupported_application_namespace"
                && unsupportedNamespace["firstAppObjectIndex"] is null && unsupportedNamespace["selectedLegacyVersion"] is null
                && unsupportedNamespace["selectedMaskVersionId"] is null
                && (string?)unsupportedNamespace["archiveMasterSha256"] == Convert.ToHexStringLower(SHA256.HashData(master21)), unsupportedNamespace.ToJsonString());
            var app20 = await OpenAndPlan(Fixture(directory, "application20-master21", master21));
            check("a supported application namespace still uses that same archived profile",
                (string?)app20["archiveMaskProfile"]?["status"] == "supported"
                && app20["archiveMaskProfile"]?["firstAppObjectIndex"]?.GetValue<uint>() == 3, app20.ToJsonString());
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog); Directory.Delete(directory, true); }
    }

    private static XDocument Master(string ns = "http://knx.org/xml/project/20") => XDocument.Parse($$"""
        <KNX xmlns="{{ns}}"><MasterData><MaskVersions><MaskVersion Id="{{Mask}}" MaskVersion="1797">
        <HawkConfigurationData><Features><Feature Name="FirstAppObjectIdx" Value="3"/></Features><InterfaceObjects>
        <InterfaceObject Index="3" ObjectType="123"><Property PropertyID="10" PropertyDataType="PDT_UNSIGNED_INT"/></InterfaceObject>
        </InterfaceObjects></HawkConfigurationData><HawkConfigurationData LegacyVersion="1"><Features><Feature Name="FirstAppObjectIdx" Value="99"/></Features></HawkConfigurationData>
        </MaskVersion></MaskVersions></MasterData></KNX>
        """);
    private static string Fixture(string directory, string name, byte[]? master, bool nested = false, string mask = Mask, string applicationNamespace = "http://knx.org/xml/project/20")
    {
        var path = Path.Combine(directory, name + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Synthetic archive mask", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = App, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        using (var stream = zip.CreateEntry("M-0001/" + App + ".xml").Open())
            stream.Write(Encoding.UTF8.GetBytes($"<KNX xmlns='{applicationNamespace}'><ApplicationProgram Id='{App}' MaskVersion='{mask}'><Static/><Dynamic/></ApplicationProgram></KNX>"));
        if (master is not null) { using var stream = zip.CreateEntry((nested ? "M-0001/" : "") + "knx_master.xml").Open(); stream.Write(master); }
        return path;
    }
}
