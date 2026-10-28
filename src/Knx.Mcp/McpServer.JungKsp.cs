using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Knx.Manufacturer;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private sealed record JungKspReview(string Token, JungKspImportPlan Plan, DateTimeOffset Expires);
    private JungKspReview? _jungKspReview;
    private bool _jungKspProjectLoaded;

    private IEnumerable<McpTool> JungKspTools()
    {
        yield return new("jung_ksp_info",
            "Read bounded Jung SP8 KSP file metadata and exact block hashes without a project, extraction, manufacturer code or bus operations. This is file validation, not an applied import.",
            Schema(StrProp("path", "Local .ksp file, optionally containing one root KSP ZIP entry.", true)), Sync(JungKspInfo));
        yield return new("jung_ksp_import_preview",
            "Preview an atomic import for the exact supported Jung SP8 application in a project opened with project_open. Returns full parameter/object/link changes and a single-use review token; changes no project or undo state.",
            Schema(StrProp("device_id", "Exact id of the project device.", true), StrProp("path", "Local KSP file to snapshot and review.", true),
                StrProp("master_data_path", "Explicit KNX master XML supplying DPT sizes/subtypes; read bounded without DTDs.", true),
                StrProp("language", "Two-letter lowercase function-text language, default en.")), Sync(JungKspPreview));
        var applySchema = Schema(StrProp("review_token", "Token from the inspected preview; expires after ten minutes and applies once.", true),
            MapProp("unlink_decisions", "Object-number keys mapped to JSON booleans. Every RequiredUnlink needs explicit true; optional unlink defaults false."));
        applySchema["properties"]!["unlink_decisions"]!["additionalProperties"] = new JsonObject { ["type"] = "boolean" };
        yield return new("jung_ksp_import_apply",
            "Apply the stored reviewed KSP bytes after current project/definition validation. Required unlink approvals must be explicit. Adds exactly one undo snapshot and performs no file write, manufacturer execution or bus operation.",
            applySchema, Sync(JungKspApply));
    }

    private object JungKspInfo(JsonElement args)
    {
        var (document, sha) = ReadJungKsp(RequiredString(args, "path"));
        return new { fileValidated = true, importValidated = false, codeExecuted = false, sourceSha256 = sha, document.FileName,
            document.FromZip, document.Header, blocks = document.Blocks.Select(b => new { b.Index, kind = b.Kind.ToString(), b.Length, b.Sha256 }),
            deviceInfo = document.DeviceInfo, functionTextProperties = document.FunctionTextsJson.EnumerateObject().Select(p => p.Name).ToArray() };
    }

    private object JungKspPreview(JsonElement args)
    {
        _jungKspReview = null;
        var project = RequireJungProject(); var id = RequiredString(args, "device_id");
        var devices = ProjectQuery.DeviceList(project).Where(d => d.Id == id).ToArray();
        if (devices.Length != 1) throw new ToolException("device_id must resolve to exactly one open-project device.");
        var device = devices[0];
        var definition = Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId)
            ?? throw new ToolException("The project snapshot must contain the exact application definition; import does not guess catalogue/hardware mappings.");
        var (document, sha) = ReadJungKsp(RequiredString(args, "path"));
        var (version, master, masterHash) = ReadJungMaster(RequiredString(args, "master_data_path"));
        var plan = JungKspImport.Preview(project, device, definition, document, master, OptionalString(args, "language") ?? "en");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        _jungKspReview = new(token, plan, expires);
        return new { validated = true, applied = false, codeExecuted = false, reviewToken = token, expires, sourceSha256 = sha,
            masterData = new { version, sha256 = masterHash }, plan.DeviceId, plan.ApplicationProgramId, plan.DefinitionSha256, plan.ProjectSha256,
            parameters = plan.Parameters, objects = plan.Objects.Select(o => new { o.Number, o.SourceReferenceId, o.TargetReferenceId,
                o.WasActive, o.WillBeActive, o.OldDpt, o.NewDpt, o.OldBits, o.NewBits, o.OldText, o.NewText,
                o.OldFunctionText, o.NewFunctionText, o.OldFlags, o.NewFlags, linkAction = o.LinkAction.ToString(), o.LinkReason, o.ExistingLinks }),
            functionTexts = plan.FunctionTexts, frames = plan.FrameSha256, undo = History() };
    }

    private object JungKspApply(JsonElement args)
    {
        var project = RequireJungProject(); var token = RequiredString(args, "review_token");
        var review = _jungKspReview;
        if (review is null || review.Token != token || review.Expires <= DateTimeOffset.UtcNow)
        {
            if (review?.Expires <= DateTimeOffset.UtcNow) _jungKspReview = null;
            throw new ToolException("Create and inspect a fresh jung_ksp_import_preview and pass its review_token.");
        }
        var definition = Knxproj.LoadApplicationProgram(project, review.Plan.ApplicationProgramId)
            ?? throw new ToolException("Reviewed application definition is no longer available in the project snapshot.");
        var decisions = JungUnlinkDecisions(args);
        var result = JungKspImport.Apply(project, review.Plan, definition, decisions);
        _undo.Push(project); _jungKspReview = null; _migrationReviews.Clear();
        return new { applied = true, codeExecuted = false, result.DeviceId, result.ParameterWrites, result.ObjectChanges, result.RemovedLinks, undo = History() };
    }

    private Project RequireJungProject()
    {
        var project = Current;
        if (!_jungKspProjectLoaded) throw new ToolException("Open an existing project with project_open before previewing or applying a KSP import.");
        return project;
    }
    private static string RequiredString(JsonElement args, string name) => OptionalString(args, name)
        ?? throw new ToolException(name + " must be a nonempty string.");
    private static Dictionary<int, bool> JungUnlinkDecisions(JsonElement args)
    {
        var result = new Dictionary<int, bool>();
        if (!args.TryGetProperty("unlink_decisions", out var map)) return result;
        if (map.ValueKind != JsonValueKind.Object) throw new ToolException("unlink_decisions must be an object of explicit booleans.");
        foreach (var property in map.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 1000
                || property.Name != number.ToString(CultureInfo.InvariantCulture) || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !result.TryAdd(number, property.Value.GetBoolean()))
                throw new ToolException("unlink_decisions requires unique canonical object-number keys and boolean values.");
        }
        return result;
    }
    private static (JungKspDocument Document, string Sha256) ReadJungKsp(string path)
    {
        var bytes = ReadJungFile(path, JungKsp.MaxInputBytes);
        return (JungKsp.Read(bytes, Path.GetFileName(path)), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    private static byte[] ReadJungFile(string path, int maximum)
    {
        using var input = File.OpenRead(Path.GetFullPath(path));
        if (input.Length > maximum) throw new ToolException("Manufacturer input exceeds its byte budget.");
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = input.Read(buffer, 0, Math.Min(buffer.Length, maximum - (int)output.Length + 1));
            if (count == 0) return output.ToArray();
            if (count > maximum - output.Length) throw new ToolException("Manufacturer input exceeds its byte budget.");
            output.Write(buffer, 0, count);
        }
    }
    private static (uint Version, IReadOnlyList<DatapointType> Types, string Sha256) ReadJungMaster(string path)
    {
        var bytes = ReadJungFile(path, 16 * 1024 * 1024);
        using var input = new MemoryStream(bytes, writable: false);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
        using (var guard = XmlReader.Create(input, settings))
        {
            var elements = 0;
            while (guard.Read())
                if (guard.Depth > 64 || guard.NodeType == XmlNodeType.Element && ++elements > 100_000)
                    throw new ToolException("Master XML depth or element budget exceeded.");
        }
        input.Position = 0;
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "KNX" || document.Descendants().Take(100_001).Count() > 100_000)
            throw new ToolException("Master XML root or element budget is invalid.");
        var masters = document.Root.Elements().Where(e => e.Name.LocalName == "MasterData").ToArray();
        if (masters.Length != 1 || !uint.TryParse((string?)masters[0].Attribute("Version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new ToolException("A unique versioned MasterData element is required.");
        var tables = masters[0].Elements().Where(e => e.Name.LocalName == "DatapointTypes").ToArray();
        if (tables.Length != 1) throw new ToolException("A unique DatapointTypes table is required.");
        var types = new List<DatapointType>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in tables[0].Elements())
        {
            var id = (string?)element.Attribute("Id") ?? "";
            if (element.Name.LocalName != "DatapointType" || !ids.Add(id) || !id.StartsWith("DPT-", StringComparison.Ordinal)
                || !ushort.TryParse(id[4..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !int.TryParse((string?)element.Attribute("SizeInBit"), NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
                throw new ToolException("Malformed or duplicate master DPT definition.");
            var type = new DatapointType { Id = id, Name = (string?)element.Attribute("Name") ?? id, SizeInBit = bits };
            var subIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var subtype in element.Elements().Where(e => e.Name.LocalName == "DatapointSubtypes").Elements())
            {
                var subId = (string?)subtype.Attribute("Id") ?? "";
                if (subtype.Name.LocalName != "DatapointSubtype" || !subIds.Add(subId) || !subId.StartsWith("DPST-" + id[4..] + "-", StringComparison.Ordinal))
                    throw new ToolException("Malformed or duplicate master subtype definition.");
                type.Subtypes.Add(new() { Id = subId, Name = (string?)subtype.Attribute("Name") ?? subId, SizeInBit = bits });
            }
            types.Add(type);
        }
        if (types.Count == 0) throw new ToolException("Master data contains no DPT definitions.");
        return (version, types.AsReadOnly(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
