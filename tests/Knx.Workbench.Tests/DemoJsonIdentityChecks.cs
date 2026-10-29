using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class DemoJsonIdentityChecks
{
    public static void Run(Action<string, bool> check)
    {
        var count = 0;
        void Check(string name, bool ok) { check("Demo JSON identity: " + name, ok); count++; }
        bool Refuses(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var legacy = WorkbenchJson.CreateDemo(); var legacyJson = WorkbenchJson.Export(legacy);
        var legacyNode = JsonNode.Parse(legacyJson)!.AsObject();
        Check("null GUID keeps schema1 and original project shape", legacy.Guid is null && legacyNode["version"]!.GetValue<int>() == 4
            && legacyNode["data"]!["schemaVersion"]!.GetValue<int>() == 1 && !legacyNode["data"]!["project"]!.AsObject().ContainsKey("guid"));
        Check("legacy profile1 output is byte unchanged", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyJson)))
            == "DDB879A9C8F24B64575D2F3FC6BCA5012F24707D84EC267410561183DDFDDF5E");
        Check("legacy profile1 import/export stays exact", WorkbenchJson.Export(WorkbenchJson.Import(legacyJson)) == legacyJson);
        Check("legacy nullable room fields remain present", legacyNode["data"]!["gas"]!.AsArray().Any(group => group!.AsObject().ContainsKey("room") && group["room"] is null));
        const string spelling = "{AbCdEf01-2345-6789-aBCD-eF0123456789}";
        var source = WorkbenchJson.CreateDemo(); source.Guid = spelling;
        var json = WorkbenchJson.Export(source); var node = JsonNode.Parse(json)!.AsObject();
        var imported = WorkbenchJson.Import(json);
        Check("GUID selects data2 under unchanged envelope4", node["version"]!.GetValue<int>() == 4 && node["data"]!["schemaVersion"]!.GetValue<int>() == 2);
        Check("original GUID spelling is emitted and imported", node["data"]!["project"]!["guid"]!.GetValue<string>() == spelling && imported.Guid == spelling);
        var sourceXml = Knxproj.ToXml(source); var importedXml = Knxproj.ToXml(imported);
        sourceXml.Descendants(XName.Get("Workbench", "urn:buschord:project:1")).Remove();
        importedXml.Descendants(XName.Get("Workbench", "urn:buschord:project:1")).Remove();
        // Demo parameter overrides contain JSON; escaped and literal Unicode spellings
        // represent the same parameter value. Metadata maps likewise have no key order.
        foreach (var document in new[] { sourceXml, importedXml })
            foreach (var value in document.Descendants().Where(element => element.Name.LocalName == "ParameterInstanceRef").Attributes("Value"))
            { using var parameter = JsonDocument.Parse(value.Value); value.Value = JsonSerializer.Serialize(parameter.RootElement); }
        Check("profile2 complete roundtrip preserves XML and metadata maps", XNode.DeepEquals(sourceXml, importedXml)
            && JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(source.Workbench)), JsonNode.Parse(JsonSerializer.Serialize(imported.Workbench))));
        Check("profile2 import/export stays exact", WorkbenchJson.Export(imported) == json);
        foreach (var variant in new[] { "aBcDeF01-2345-6789-abCD-Ef0123456789", "aBcDeF0123456789abCDEf0123456789" })
        {
            var sample = WorkbenchJson.CreateDemo(); sample.Guid = variant;
            Check("valid GUID spelling remains exact " + variant, WorkbenchJson.Import(WorkbenchJson.Export(sample)).Guid == variant);
        }
        var history = new UndoStack(); history.ResetUnsaved(source); var before = WorkbenchJson.Export(source);
        source.Comment += " edited"; history.Push(source); var after = WorkbenchJson.Export(source);
        var undone = history.Undo()!; var redone = history.Redo()!;
        Check("profile2 survives complete Undo and Redo", undone.Guid == spelling && redone.Guid == spelling
            && WorkbenchJson.Export(undone) == before && WorkbenchJson.Export(redone) == after && history.IsDirty);

        string Variant(Action<JsonObject> edit)
        { var candidate = JsonNode.Parse(json)!.AsObject(); edit(candidate); return candidate.ToJsonString(); }
        foreach (var invalid in new (string Name, JsonNode? Value)[]
        {
            ("null", null), ("empty", JsonValue.Create("")), ("whitespace", JsonValue.Create(" \t\n")),
            ("empty GUID", JsonValue.Create("00000000-0000-0000-0000-000000000000")),
            ("empty N GUID", JsonValue.Create("00000000000000000000000000000000")),
            ("malformed", JsonValue.Create("not-a-guid")), ("number", JsonValue.Create(42)),
            ("boolean", JsonValue.Create(true)), ("object", new JsonObject()), ("array", new JsonArray())
        })
        {
            var invalidJson = Variant(candidate => candidate["data"]!["project"]!["guid"] = invalid.Value?.DeepClone());
            Check("profile2 rejects " + invalid.Name + " GUID", Refuses(() => WorkbenchJson.Import(invalidJson)));
        }
        Check("profile2 requires GUID field", Refuses(() => WorkbenchJson.Import(Variant(candidate => candidate["data"]!["project"]!.AsObject().Remove("guid")))));
        Check("profile2 rejects wrong-case GUID field", Refuses(() => WorkbenchJson.Import(Variant(candidate =>
        { var header = candidate["data"]!["project"]!.AsObject(); header.Remove("guid"); header["Guid"] = spelling; }))));
        Check("profile2 rejects extra case-variant GUID", Refuses(() => WorkbenchJson.Import(Variant(candidate => candidate["data"]!["project"]!["Guid"] = spelling))));
        var duplicate = Variant(candidate => { }); var marker = "\"guid\":";
        var at = duplicate.IndexOf(marker, StringComparison.Ordinal);
        duplicate = duplicate.Insert(at, "\"guid\":" + JsonSerializer.Serialize(spelling) + ",");
        Check("profile2 rejects duplicate GUID field", Refuses(() => WorkbenchJson.Import(duplicate)));
        foreach (var value in new JsonNode?[] { JsonValue.Create(0), JsonValue.Create(3), JsonValue.Create("2"), null })
            Check("unknown or mistyped data schema rejects " + (value?.ToJsonString() ?? "null"), Refuses(() => WorkbenchJson.Import(Variant(candidate => candidate["data"]!["schemaVersion"] = value?.DeepClone()))));
        Check("envelope version stays strictly4", Refuses(() => WorkbenchJson.Import(Variant(candidate => candidate["version"] = 5))));
        foreach (var value in new JsonNode?[] { JsonValue.Create(spelling), null })
        {
            var candidate = JsonNode.Parse(legacyJson)!.AsObject(); candidate["data"]!["project"]!["guid"] = value?.DeepClone();
            Check("schema1 rejects extra GUID " + (value is null ? "null" : "string"), Refuses(() => WorkbenchJson.Import(candidate.ToJsonString())));
        }
        foreach (var value in new[] { "", " ", "not-a-guid", "00000000-0000-0000-0000-000000000000" })
        {
            var sample = WorkbenchJson.CreateDemo(); sample.Guid = value;
            Check("invalid nonnull GUID export never downgrades " + value, Refuses(() => WorkbenchJson.Export(sample)));
        }
        Project Sample() { var sample = WorkbenchJson.CreateDemo(); sample.Guid = spelling; return sample; }
        var native = Sample(); ProjectQuery.DeviceList(native).First().Hardware2ProgramRefId = "native-application";
        Check("profile2 refuses extra native application fields", Refuses(() => WorkbenchJson.Export(native)));
        var connectors = Sample(); connectors.Workbench.Objects.First().Value.Connectors["synthetic-extra"] = new() { Acknowledge = true, Priority = 2, Slot = 3 };
        Check("profile2 refuses unrepresented connector metadata", Refuses(() => WorkbenchJson.Export(connectors)));
        var revisions = Sample(); ProjectRevisions.Capture(revisions, description: "Synthetic retained revision");
        Check("profile2 refuses unrepresented revision metadata", Refuses(() => WorkbenchJson.Export(revisions)));
        var opaque = Knxproj.ToXml(Sample()); opaque.Root!.Add(new XElement(XName.Get("Opaque", "urn:synthetic:retained"), "keep"));
        Check("profile2 refuses opaque XML", Refuses(() => WorkbenchJson.Export(Knxproj.FromXml(opaque))));
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "buschord-demo-identity-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "opaque.knxproj");
            using (var file = File.Create(path)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("0.xml").Open())) writer.Write(Knxproj.ToXml(Sample()).ToString());
                using var bytes = zip.CreateEntry("opaque.bin").Open(); bytes.Write([1, 2, 3]);
            }
            var loaded = Knxproj.Load(path);
            Check("profile2 refuses opaque archive entries", loaded.Guid == spelling && Refuses(() => WorkbenchJson.Export(loaded)));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(directory); const string prefix = "buschord-demo-identity-";
            if (!string.Equals(Path.GetDirectoryName(directory), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !leaf.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(leaf[prefix.Length..], "N", out _))
                throw new InvalidOperationException("Diagnostic cleanup escaped its owned directory.");
            Directory.Delete(directory, true);
        }
        Console.WriteLine($"ALL {count} DEMO IDENTITY CHECKS PASSED");
    }
}
