using System.Globalization;
using System.Text;

namespace Knx;

/// <summary>
/// Semantic export. Emits the project as linked
/// data in the KNX Information Model vocabulary shipped with ETS6 (ontology.ttl):
/// knx: &lt;http://schema.knx.org/2020/ontology/knx#&gt;,
/// core: &lt;http://schema.knx.org/2023/en50090-6-2/core#&gt;.
/// Only a subset of the ontology is instantiated here (installation, devices,
/// function points, datapoint types); property usage is documented in FEATURE.md.
/// </summary>
public static class SemanticExport
{
    public const string KnxNamespace = "http://schema.knx.org/2020/ontology/knx#";
    public const string CoreNamespace = "http://schema.knx.org/2023/en50090-6-2/core#";
    /// <summary>Spatial model vocabulary for buildings and rooms (ontology.ttl: loc:).</summary>
    public const string LocNamespace = "http://schema.knx.org/2023/en50090-6-2/loc#";

    /// <summary>Optional ontology file merged into the semantic export.</summary>
    public const string DefaultOntologyPath = @"C:\Program Files (x86)\ETS6\ontology.ttl";

    public enum SemanticFormat { Turtle, JsonLd }

    private const string Prefixes =
        "@prefix knx: <" + KnxNamespace + "> .\n" +
        "@prefix core: <" + CoreNamespace + "> .\n" +
        "@prefix mac: <http://schema.knx.org/2020/ontology/mac#> .\n" +
        "@prefix dct: <http://purl.org/dc/terms/> .\n" +
        "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n" +
        "@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n";

    public static string ToTurtle(Project project, bool mergeOntology = false, string? ontologyPath = null)
    {
        var body = BuildTurtle(project);
        if (!mergeOntology) return body;

        var path = ontologyPath ?? DefaultOntologyPath;
        // The ontology is concatenated instead of parsed and merged into one graph;
        // valid Turtle, but prefix duplicates and blank-node ids stay as-is.
        return File.Exists(path) ? File.ReadAllText(path) + "\n\n" + body : body;
    }

    /// <summary>Writes a semantic export; Turtle merges the ontology when it is available.</summary>
    public static void Export(Project project, string path, SemanticFormat format, string? ontologyPath = null)
    {
        var content = format == SemanticFormat.Turtle
            ? ToTurtle(project, mergeOntology: true, ontologyPath)
            : ToJsonLd(project);
        File.WriteAllText(path, content);
    }

    private static string BuildTurtle(Project project)
    {
        var sb = new StringBuilder(Prefixes);
        if (HasBuildingTree(project)) sb.AppendLine("@prefix loc: <" + LocNamespace + "> .");
        sb.AppendLine();

        foreach (var inst in project.Installations)
        {
            sb.AppendLine($"<{InstallationIri(project, inst)}> a core:Installation ;");
            sb.AppendLine($"    rdfs:label {Literal(inst.Name ?? project.Name, "de")} ;");
            sb.AppendLine($"    knx:projectInstallationNumber {inst.Address?.Raw ?? 0} .");
            sb.AppendLine();
        }

        foreach (var device in ProjectQuery.DeviceList(project))
        {
            sb.AppendLine($"<{DeviceIri(project, device)}> a core:Device ;");
            sb.AppendLine($"    rdfs:label {Literal(device.Name ?? device.Address.ToString(), "de")} ;");
            sb.AppendLine($"    knx:individualAddress \"{device.Address.Raw:X4}\" ;");
            if (!string.IsNullOrWhiteSpace(device.Comment))
                sb.AppendLine($"    core:comment {Literal(device.Comment, "de")} ;");
            if (!string.IsNullOrWhiteSpace(device.ProductRefId))
                sb.AppendLine($"    core:hardware {Literal(device.ProductRefId)} ;");
            sb.AppendLine("    core:hasProduct [ a core:Product ;" +
                          $" core:number {Literal(device.ProductRefId)} ] .");
            sb.AppendLine();
        }

        foreach (var entry in ProjectQuery.GroupAddresses(project))
        {
            sb.AppendLine($"<{FunctionPointIri(project, entry)}> a knx:FunctionPoint ;");
            sb.AppendLine($"    rdfs:label {Literal(entry.Name, "de")} ;");
            sb.AppendLine($"    knx:groupAddress {entry.Address.Raw} ;");
            if (FormatDatapointType(entry.DatapointType) is { } dpt)
                sb.AppendLine($"    knx:datapointType [ a knx:DatapointType ; dct:subject \"{dpt}\" ; " +
                              $"knx:dptMajorNumber {dpt.Split('.')[0]} ] ;");
            sb.AppendLine($"    knx:installationHints {Literal(entry.Comment ?? entry.Description ?? entry.Id)} .");
            sb.AppendLine();
        }

        // Building tree: loc: space relations and device installation locations.
        var devicesById = ProjectQuery.DeviceList(project).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var node in BuildingNodes(project))
        {
            var lines = new List<string>
            {
                $"<{SpaceIri(project, node)}> a {SpaceClass(node.Kind)}",
                $"    rdfs:label {Literal(node.Name, "de")}",
            };
            if (node.Parent is { } parent)
                lines.Add($"    loc:isSpaceOf <{SpaceIri(project, parent)}>");
            var devices = node.DeviceRefs.Where(devicesById.ContainsKey)
                .Select(id => $"<{DeviceIri(project, devicesById[id])}>").ToList();
            if (devices.Count > 0)
                lines.Add("    loc:containsEquipment " + string.Join(", ", devices));
            sb.AppendLine(string.Join(" ;\n", lines) + " .");
            sb.AppendLine();
        }

        // device ↔ function point links from the com object references
        foreach (var (device, comObject) in ProjectQuery.ComObjectRefs(project))
            foreach (var link in comObject.Links)
                if (ProjectQuery.FindGroupAddressById(project, link) is { } ga)
                    sb.AppendLine($"<{DeviceIri(project, device)}> core:hasPoint <{FunctionPointIri(project, ga)}> .");

        return sb.ToString();
    }

    public static string ToJsonLd(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"@context\": {");
        var context = new List<string>
        {
            $"    \"knx\": \"{KnxNamespace}\"",
            $"    \"core\": \"{CoreNamespace}\"",
            "    \"dct\": \"http://purl.org/dc/terms/\"",
            "    \"rdfs\": \"http://www.w3.org/2000/01/rdf-schema#\"",
            "    \"label\": \"rdfs:label\"",
            "    \"individualAddress\": { \"@id\": \"knx:individualAddress\" }",
            "    \"groupAddress\": { \"@id\": \"knx:groupAddress\" }",
            "    \"datapointType\": { \"@id\": \"knx:datapointType\", \"@type\": \"@id\" }",
            "    \"hasPoint\": { \"@id\": \"core:hasPoint\", \"@type\": \"@id\" }",
        };
        if (HasBuildingTree(project))
        {
            context.Add($"    \"loc\": \"{LocNamespace}\"");
            context.Add("    \"isSpaceOf\": { \"@id\": \"loc:isSpaceOf\", \"@type\": \"@id\" }");
            context.Add("    \"containsEquipment\": { \"@id\": \"loc:containsEquipment\", \"@type\": \"@id\" }");
        }
        sb.AppendLine(string.Join(",\n", context));
        sb.AppendLine("  },");
        sb.AppendLine("  \"@graph\": [");

        var nodes = new List<string>();
        foreach (var inst in project.Installations)
            nodes.Add($$"""
                { "@id": "{{InstallationIri(project, inst)}}", "@type": "core:Installation",
                  "label": {{JsonString(inst.Name ?? project.Name)}},
                  "knx:projectInstallationNumber": {{inst.Address?.Raw ?? 0}} }
        """);

        foreach (var device in ProjectQuery.DeviceList(project))
        {
            var points = device.ComObjectRefs.SelectMany(r => r.Links).Distinct()
                .Select(id => ProjectQuery.FindGroupAddressById(project, id))
                .Where(ga => ga is not null)
                .Select(ga => $"\"{FunctionPointIri(project, ga!)}\"");
            var pointArray = string.Join(", ", points);
            nodes.Add($$"""
                { "@id": "{{DeviceIri(project, device)}}", "@type": "core:Device",
                  "label": {{JsonString(device.Name ?? device.Address.ToString())}},
                  "individualAddress": "{{device.Address.Raw:X4}}",
                  "core:hardware": {{JsonString(device.ProductRefId)}},
                  "hasPoint": [{{pointArray}}] }
        """);
        }

        foreach (var entry in ProjectQuery.GroupAddresses(project))
        {
            var dpt = FormatDatapointType(entry.DatapointType);
            var dptNode = dpt is null
                ? "null"
                : $"{{\"@type\": \"knx:DatapointType\", \"dct:subject\": \"{dpt}\"}}";
            nodes.Add($$"""
                { "@id": "{{FunctionPointIri(project, entry)}}", "@type": "knx:FunctionPoint",
                  "label": {{JsonString(entry.Name)}},
                  "groupAddress": {{entry.Address.Raw}},
                  "datapointType": {{dptNode}} }
        """);
        }

        var spaceDevices = ProjectQuery.DeviceList(project).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var node in BuildingNodes(project))
        {
            var properties = new List<string>
            {
                $"\"@id\": \"{SpaceIri(project, node)}\"",
                $"\"@type\": \"{SpaceClass(node.Kind)}\"",
                $"\"label\": {JsonString(node.Name)}",
            };
            if (node.Parent is { } parent)
                properties.Add($"\"isSpaceOf\": {{ \"@id\": \"{SpaceIri(project, parent)}\" }}");
            var devices = node.DeviceRefs.Where(spaceDevices.ContainsKey)
                .Select(id => $"\"{DeviceIri(project, spaceDevices[id])}\"").ToList();
            if (devices.Count > 0)
                properties.Add($"\"containsEquipment\": [{string.Join(", ", devices)}]");
            nodes.Add("  { " + string.Join(",\n    ", properties) + " }");
        }

        sb.AppendLine(string.Join(",\n", nodes));
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string InstallationIri(Project project, Installation installation)
        => $"urn:knx:installation:{Slug(project.Id)}:{Slug(installation.Id)}";

    private static string DeviceIri(Project project, DeviceInstance device)
        => $"urn:knx:device:{Slug(project.Id)}:{device.Address.Raw:X4}";

    private static string FunctionPointIri(Project project, GroupAddressEntry entry)
        => $"urn:knx:functionpoint:{Slug(project.Id)}:GA-{entry.Address.Raw}";

    private static bool HasBuildingTree(Project project) => project.Installations.Any(i => !i.Locations.IsEmpty);

    private static IEnumerable<SpaceNode> BuildingNodes(Project project)
        => project.Installations.SelectMany(i => i.Locations.AllNodes);

    private static string SpaceIri(Project project, SpaceNode node)
        => $"urn:knx:space:{Slug(project.Id)}:{Slug(node.Id)}";

    /// <summary>
    /// Maps space kinds to the classes of the loc vocabulary. The ontology has no
    /// BuildingPart/Corridor/Stairway/DistributionBoard classes, so those (and Function)
    /// are exported as the generic loc:Space; Segment maps to loc:RoomSegment.
    /// </summary>
    private static string SpaceClass(SpaceKind kind) => kind switch
    {
        SpaceKind.Building => "loc:Building",
        SpaceKind.Floor => "loc:Floor",
        SpaceKind.Room => "loc:Room",
        SpaceKind.Segment => "loc:RoomSegment",
        _ => "loc:Space",
    };

    private static string Slug(string value)
        => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static string Literal(string value, string? language = null)
        => language is null ? $"\"{Escape(value)}\"" : $"\"{Escape(value)}\"@{language}";

    private static string JsonString(string? value)
        => value is null ? "null" : $"\"{Escape(value)}\"";

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    /// <summary>Converts a datapoint type id (DPST-1-1) to the ontology subject form (1.001).</summary>
    public static string? FormatDatapointType(string? datapointType)
    {
        if (string.IsNullOrWhiteSpace(datapointType)) return null;
        var digits = datapointType.Replace("DPST-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("DPT-", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        // Already in "1.001" notation
        if (digits.Contains('.')) return digits;

        var parts = digits.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            >= 2 when int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
                => $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString("D3", CultureInfo.InvariantCulture)}",
            1 when int.TryParse(parts[0], out var only) => only.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
