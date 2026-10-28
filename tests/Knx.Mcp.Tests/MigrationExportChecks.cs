using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;

internal static class MigrationExportChecks
{
    public static async Task Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-mcp-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(root, "library"));
        const string sourceId = "M-FFE1_A-source", targetId = "M-FFE2_A-target";
        var fixture = ProjectWizard.Create(new() { Name = "Synthetic MCP migration", Areas = 1, LinesPerArea = 1 });
        var device = new DeviceInstance { Id = "MCP-D1", ProductRefId = sourceId, Hardware2ProgramRefId = sourceId, Address = new(1, 1, 1), Name = "Synthetic device" };
        fixture.Installations[0].Areas[0].Lines[0].Devices.Add(device);
        device.ParameterValues[sourceId + "_R-1"] = "7";
        var input = Path.Combine(root, "source.knxproj");
        File.WriteAllBytes(input, Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(fixture).ToString()),
            ["M-FFE1/" + sourceId + ".xml"] = Encoding.UTF8.GetBytes(Application(sourceId).ToString()) }));
        try
        {
            await using var server = new McpServer();
            var sequence = 10000;
            async Task<JsonNode> Tool(string name, object? arguments = null)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = name, ["arguments"] = JsonSerializer.SerializeToNode(arguments ?? new { }) } };
                return JsonNode.Parse((await server.HandleAsync(request.ToJsonString()))!)!;
            }
            static bool Error(JsonNode result) => result["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode? Data(JsonNode result) => result["result"]?["structuredContent"];
            async Task<int> Steps() => Data(await Tool("project_info"))!["undo"]!["steps"]!.GetValue<int>();
            var schema = server.Tools.Single(t => t.Name == "product_replace").InputSchema;
            check("MCP migration maps are typed string dictionaries", (string?)schema["properties"]?["parameter_map"]?["additionalProperties"]?["type"] == "string", null);
            check("MCP project password/script capabilities are explicit schema inputs",
                server.Tools.Where(t => t.Name is "project_open" or "project_save").All(t => t.InputSchema["properties"]?["password"] is not null && t.InputSchema["properties"]?["execute_scripts"] is not null), null);
            var opened = await Tool("project_open", new { path = input });
            check("MCP opens the detached synthetic migration project", !Error(opened), opened.ToJsonString());
            var discovery = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, dry_run = true });
            check("MCP missing definitions produce discovery without validation or token", !Error(discovery) && Data(discovery)?["validated"]?.GetValue<bool>() == false && Data(discovery)?["reviewToken"] is null, discovery.ToJsonString());
            var unreviewed = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId });
            check("MCP refuses unreviewed product replacement", Error(unreviewed) && server.Project!.Installations[0].Areas[0].Lines[0].Devices[0].ProductRefId == sourceId, unreviewed.ToJsonString());

            var catalog = Path.Combine(root, "target.knxprod");
            var targetDocument = Application(targetId);
            var wrapped = new XDocument(new XElement("KNX", new XElement("ManufacturerData", new XElement("Manufacturer", new XAttribute("RefId", "M-FFE2"),
                new XElement("ApplicationPrograms", new XElement(targetDocument.Descendants("ApplicationProgram").Single()))))));
            File.WriteAllBytes(catalog, Zip(new() { ["knxprod.xml"] = Encoding.UTF8.GetBytes(wrapped.ToString()) }));
            var imported = await Tool("catalog_import", new { path = catalog });
            check("MCP migration imports a synthetic target application", !Error(imported), imported.ToJsonString());
            var before = Knxproj.ToXml(server.Project!).ToString();
            var beforeSteps = await Steps();
            var missingMap = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, dry_run = true });
            check("MCP migration refuses suffix guesses and requires a complete-id map", Error(missingMap) && Knxproj.ToXml(server.Project!).ToString() == before, missingMap.ToJsonString());
            var map = new Dictionary<string, string> { [sourceId + "_R-1"] = targetId + "_R-1" };
            var preview = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, dry_run = true, parameter_map = map });
            var token = (string?)Data(preview)?["reviewToken"];
            check("MCP exact migration returns a detached review token", !Error(preview) && Data(preview)?["validated"]?.GetValue<bool>() == true && token?.Length == 64
                && Knxproj.ToXml(server.Project!).ToString() == before && await Steps() == beforeSteps, preview.ToJsonString());
            var replacementPreview = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, dry_run = true, parameter_map = map });
            var replacedReview = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, review_token = token, parameter_map = map });
            check("MCP a new preview retires the previous review token", Error(replacedReview), replacedReview.ToJsonString());
            token = (string?)Data(replacementPreview)?["reviewToken"];
            var different = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, review_token = token,
                parameter_map = new Dictionary<string, string> { [sourceId + "_R-1"] = targetId + "_different" } });
            check("MCP review token binds the exact reviewed inputs", Error(different) && Knxproj.ToXml(server.Project!).ToString() == before, different.ToJsonString());
            var applied = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, review_token = token, parameter_map = map });
            check("MCP reviewed migration applies once with one undo entry", !Error(applied) && Data(applied)?["applied"]?.GetValue<bool>() == true
                && ProjectQuery.DeviceList(server.Project!).Single().ParameterValues[targetId + "_R-1"] == "7" && await Steps() == beforeSteps + 1, applied.ToJsonString());
            var replay = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, review_token = token, parameter_map = map });
            check("MCP migration review tokens cannot be replayed", Error(replay), replay.ToJsonString());
            var saved = Path.Combine(root, "migrated.knxproj");
            var save = await Tool("project_save", new { path = saved });
            check("MCP migration saves its target application overlay", !Error(save) && Knxproj.LoadApplicationProgram(Knxproj.Load(saved), targetId) is not null, save.ToJsonString());
            _ = await Tool("undo");
            check("MCP migration undo restores source references and values", ProjectQuery.DeviceList(server.Project!).Single().ProductRefId == sourceId
                && ProjectQuery.DeviceList(server.Project!).Single().ParameterValues[sourceId + "_R-1"] == "7", null);
            _ = await Tool("redo");
            check("MCP migration redo restores target application references", ProjectQuery.DeviceList(server.Project!).Single().ProductRefId == targetId, null);

            _ = await Tool("project_open", new { path = input });
            var stalePreview = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, dry_run = true, parameter_map = map });
            var staleToken = (string?)Data(stalePreview)?["reviewToken"];
            _ = await Tool("device_update", new { address = "1.1.1", name = "Changed after preview" });
            var stale = await Tool("product_replace", new { old_ref = sourceId, new_ref = targetId, new_program = targetId, review_token = staleToken, parameter_map = map });
            check("MCP refuses stale reviewed migration without partial edits", Error(stale) && ProjectQuery.DeviceList(server.Project!).Single().ProductRefId == sourceId, stale.ToJsonString());

            const string scriptSource = "M-FFE3_A-scriptSource", scriptTarget = "M-FFE4_A-scriptTarget";
            var scriptProject = Knxproj.FromXml(Knxproj.ToXml(fixture)); var scriptDevice = ProjectQuery.DeviceList(scriptProject).Single();
            scriptDevice.ProductRefId = scriptSource; scriptDevice.Hardware2ProgramRefId = scriptSource;
            scriptDevice.ParameterValues.Clear(); scriptDevice.ParameterValues[scriptSource + "_R-1"] = "7";
            var scriptPath = Path.Combine(root, "script.knxproj");
            File.WriteAllBytes(scriptPath, Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(scriptProject).ToString()),
                ["M-FFE3/" + scriptSource + ".xml"] = Encoding.UTF8.GetBytes(ScriptApplication(scriptSource).ToString()),
                ["M-FFE4/" + scriptTarget + ".xml"] = Encoding.UTF8.GetBytes(ScriptApplication(scriptTarget).ToString()) }));
            _ = await Tool("project_open", new { path = scriptPath });
            var scriptMap = new Dictionary<string, string> { [scriptSource + "_R-1"] = scriptTarget + "_R-1" };
            var refusedScript = await Tool("product_replace", new { old_ref = scriptSource, new_ref = scriptTarget, dry_run = true, parameter_map = scriptMap });
            check("MCP migration does not execute manufacturer scripts by default", Error(refusedScript), refusedScript.ToJsonString());
            var scriptPreview = await Tool("product_replace", new { old_ref = scriptSource, new_ref = scriptTarget, dry_run = true, parameter_map = scriptMap, execute_scripts = true });
            var scriptToken = (string?)Data(scriptPreview)?["reviewToken"];
            check("MCP migration runs manufacturer functions with an explicit capability", !Error(scriptPreview) && scriptToken is not null, scriptPreview.ToJsonString());
            var wrongCapability = await Tool("product_replace", new { old_ref = scriptSource, new_ref = scriptTarget, review_token = scriptToken, parameter_map = scriptMap });
            check("MCP apply requires the reviewed script opt-in", Error(wrongCapability), wrongCapability.ToJsonString());
            var scriptApply = await Tool("product_replace", new { old_ref = scriptSource, new_ref = scriptTarget, review_token = scriptToken, parameter_map = scriptMap, execute_scripts = true });
            check("MCP explicitly reviewed script migration applies", !Error(scriptApply), scriptApply.ToJsonString());
            var noStickyCapability = await Tool("product_replace", new { old_ref = scriptTarget, new_ref = scriptSource, dry_run = true,
                parameter_map = new Dictionary<string, string> { [scriptTarget + "_R-1"] = scriptSource + "_R-1" } });
            check("MCP script capability is not retained for the next migration", Error(noStickyCapability), noStickyCapability.ToJsonString());

            const string secret = "synthetic-call-password";
            var protectedPath = Path.Combine(root, "protected.knxproj");
            File.WriteAllBytes(protectedPath, AesZip(Encoding.UTF8.GetBytes(Knxproj.ToXml(fixture).ToString()), secret));
            var protectedOpen = await Tool("project_open", new { path = protectedPath, password = secret });
            check("MCP protected open uses an ephemeral project password", !Error(protectedOpen) && !protectedOpen.ToJsonString().Contains(secret, StringComparison.Ordinal), protectedOpen.ToJsonString());
            var protectedCopy = Path.Combine(root, "protected-copy.knxproj");
            var noPassword = await Tool("project_save", new { path = protectedCopy });
            check("MCP protected save never remembers the open password or a failed target", Error(noPassword) && !File.Exists(protectedCopy)
                && (string?)Data(await Tool("project_info"))?["project"]?["path"] == protectedPath, noPassword.ToJsonString());
            var protectedSave = await Tool("project_save", new { path = protectedCopy, password = secret });
            check("MCP protected save reauthenticates and retains encryption", !Error(protectedSave) && Knxproj.IsPasswordProtected(Knxproj.Load(protectedCopy, secret)), protectedSave.ToJsonString());
            check("MCP successful save also retains no password", Error(await Tool("project_save")), null);
            check("MCP script capability requires a boolean opt-in", Error(await Tool("project_open", new { path = input, execute_scripts = "true" })), null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previousCatalog);
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(full).StartsWith("buschord-mcp-migration-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup target.");
            Directory.Delete(full, true);
        }
    }

    private static XDocument Application(string id) => XDocument.Parse($"""
        <KNX><ApplicationProgram Id="{id}"><Static><ParameterTypes><ParameterType Id="{id}_T"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="{id}_P-1" ParameterType="{id}_T" Value="1" /></Parameters><ParameterRefs><ParameterRef Id="{id}_R-1" RefId="{id}_P-1" /></ParameterRefs></Static>
        <Dynamic><ParameterRefRef RefId="{id}_R-1" /></Dynamic></ApplicationProgram></KNX>
        """);

    private static byte[] Zip(Dictionary<string, byte[]> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries) { using var stream = zip.CreateEntry(name).Open(); stream.Write(data); }
        return output.ToArray();
    }

    private static XDocument ScriptApplication(string id)
    {
        var document = Application(id); var stat = document.Descendants("Static").Single();
        stat.Element("Parameters")!.Add(new XElement("Parameter", new XAttribute("Id", id + "_P-2"), new XAttribute("ParameterType", id + "_T"), new XAttribute("Value", "0")));
        stat.Element("ParameterRefs")!.Add(new XElement("ParameterRef", new XAttribute("Id", id + "_R-2"), new XAttribute("RefId", id + "_P-2")));
        stat.Add(new XElement("Script", "function forward(input,output,context){output.right=input.left*2;} function reverse(input,output,context){output.left=input.right/2;}"),
            new XElement("ParameterCalculations", new XElement("ParameterCalculation", new XAttribute("Language", "JavaScript"),
                new XAttribute("LRTransformationFunc", "forward"), new XAttribute("RLTransformationFunc", "reverse"),
                new XElement("LParameters", new XElement("ParameterRefRef", new XAttribute("RefId", id + "_R-1"), new XAttribute("AliasName", "left"))),
                new XElement("RParameters", new XElement("ParameterRefRef", new XAttribute("RefId", id + "_R-2"), new XAttribute("AliasName", "right"))))));
        document.Descendants("Dynamic").Single().Add(new XElement("ParameterRefRef", new XAttribute("RefId", id + "_R-2")));
        return document;
    }

    // Independent synthetic AE-2/AES256/Stored fixture; no encryption writer under test is used here.
    private static byte[] AesZip(byte[] data, string password)
    {
        var profilePath = Environment.GetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION")
            ?? throw new InvalidOperationException("The synthetic archive profile was not initialized.");
        using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
        var settings = profile.RootElement;
        if (settings.GetProperty("HashAlgorithm").GetString() != "SHA256"
            || settings.GetProperty("PasswordEncoding").GetString() != "utf-8")
            throw new InvalidOperationException("The independent fixture requires its synthetic profile.");
        var profileSalt = Encoding.UTF8.GetBytes(settings.GetProperty("Salt").GetString()!);
        var zipPassword = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
            profileSalt, settings.GetProperty("Iterations").GetInt32(), HashAlgorithmName.SHA256,
            settings.GetProperty("KeyLength").GetInt32()));
        var salt = RandomNumberGenerator.GetBytes(16);
        var keys = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(zipPassword), salt, 1000, HashAlgorithmName.SHA1, 66);
        // WinZip CTR fixture: only counter blocks enter AES; HMAC authenticates ciphertext.
        // https://www.winzip.com/en/support/aes-encryption/ (III.4-5)
        using var aes = Aes.Create(); aes.Key = keys[..32];
        var cipher = new byte[data.Length]; var counter = new byte[16]; counter[0] = 1; var block = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            for (var i = 0; i < Math.Min(16, data.Length - offset); i++) cipher[offset + i] = (byte)(data[offset + i] ^ block[i]);
            for (var i = 0; i < 16; i++) if (++counter[i] != 0) break;
        }
        var body = salt.Concat(keys[64..]).Concat(cipher).Concat(HMACSHA1.HashData(keys[32..64], cipher)[..10]).ToArray();
        byte[] extra = [1, 0x99, 7, 0, 2, 0, (byte)'A', (byte)'E', 3, 0, 0];
        byte[] name = "0.xml"u8.ToArray();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        void U16(int n) => writer.Write((ushort)n); void U32(int n) => writer.Write((uint)n);
        U32(0x04034b50); U16(51); U16(0x801); U16(99); U16(0); U16(0x21); U32(0); U32(body.Length); U32(data.Length); U16(name.Length); U16(extra.Length);
        writer.Write(name); writer.Write(extra); writer.Write(body); var central = (int)output.Position;
        U32(0x02014b50); U16(51); U16(51); U16(0x801); U16(99); U16(0); U16(0x21); U32(0); U32(body.Length); U32(data.Length);
        U16(name.Length); U16(extra.Length); U16(0); U16(0); U16(0); U32(0); U32(0); writer.Write(name); writer.Write(extra);
        var size = (int)output.Position - central; U32(0x06054b50); U16(0); U16(0); U16(1); U16(1); U32(size); U32(central); U16(0);
        return output.ToArray();
    }
}
