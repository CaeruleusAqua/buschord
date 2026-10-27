using System.Xml;
using System.Xml.Linq;

namespace Knx;

/// <summary>Exact project-local entry names; independent of schema and optional containing ZIP.</summary>
internal sealed record ProjectEntryLayout
{
    internal static readonly ProjectEntryLayout Root = new("");
    internal string Prefix { get; }
    internal string DataEntry => Prefix + "0.xml";
    internal string InformationEntry => Prefix + "project.xml";
    internal string SidecarEntry => Prefix + NativeProject.SidecarEntry;
    private ProjectEntryLayout(string prefix) => Prefix = prefix;

    internal static ProjectEntryLayout? Find(IReadOnlyList<string> names)
    {
        var data = names.Where(n => n == "0.xml" || n.StartsWith("P-", StringComparison.Ordinal)
            && n.EndsWith("/0.xml", StringComparison.Ordinal)).ToArray();
        if (data.Length > 1) throw new InvalidDataException("Ambiguous project XML entry layout.");
        if (data.Length == 0) return null;
        var prefix = data[0][..^5];
        if (prefix.Length != 0)
        {
            var folder = prefix[..^1];
            if (folder.Length is < 3 or > 256 || folder.Contains('/') || folder.Contains('\\'))
                throw new InvalidDataException("Invalid project directory layout.");
            try { XmlConvert.VerifyNCName(folder); }
            catch (XmlException ex) { throw new InvalidDataException("Invalid project directory identity.", ex); }
        }
        var result = prefix.Length == 0 ? Root : new ProjectEntryLayout(prefix);
        if (names.Any(n => n.StartsWith("P-", StringComparison.Ordinal) && n.EndsWith("/project.xml", StringComparison.Ordinal)
                && n.IndexOf('/') == n.Length - "/project.xml".Length
                && n != result.InformationEntry)
            || prefix.Length != 0 && (names.Contains("project.xml") || names.Contains(NativeProject.SidecarEntry)))
            throw new InvalidDataException("Conflicting project information entry layout.");
        return result;
    }

    internal void Validate(XDocument data, XDocument? information)
    {
        var root = data.Root ?? throw new InvalidDataException("Missing project root.");
        var format = ProjectFormats.Of(root);
        var projects = root.Elements(root.Name.Namespace + "Project").Take(2).ToArray();
        if (root.Name.LocalName != "KNX" || projects.Length != 1)
            throw new InvalidDataException("Expected exactly one identified project.");
        if (Prefix.Length != 0 && (format is not (ProjectFormat.Native20 or ProjectFormat.Native22)
            || (string?)projects[0].Attribute("Id") != Prefix[..^1]))
            throw new InvalidDataException("Project directory identity or schema does not match its XML.");
        if (information is not null && format.IsNative())
        {
            var infoRoot = information.Root;
            var infos = infoRoot?.Elements(root.Name.Namespace + "Project").Take(2).ToArray() ?? [];
            if (infoRoot?.Name != root.Name || infos.Length != 1
                || (string?)infos[0].Attribute("Id") != (string?)projects[0].Attribute("Id"))
                throw new InvalidDataException("Project information identity or namespace does not match its data.");
        }
    }
}
