using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Velopack;

namespace Knx.Updates;

public static class UpdatePackageScope
{
    public static void Verify(string path, UpdateManifest manifest)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count is < 2 or > 20000) throw new InvalidDataException("Invalid update package entry count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Any(c => c < 32) ||
                name.TrimEnd('/').Split('/').Any(part => part is "" or "." or ".." || part != part.TrimEnd(' ', '.')) ||
                !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe update package path.");
            expanded = checked(expanded + entry.Length);
            if (entry.Length > UpdateManifests.MaximumPackageBytes || expanded > 3 * UpdateManifests.MaximumPackageBytes)
                throw new InvalidDataException("Update package expansion limit exceeded.");
        }
        var specs = zip.Entries.Where(e => !e.FullName.Contains('/') && e.Name.EndsWith(".nuspec", StringComparison.Ordinal)).ToArray();
        if (specs.Length != 1 || specs[0].Length > 256 * 1024) throw new InvalidDataException("Invalid update package specification.");
        using var input = specs[0].Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 });
        var document = XDocument.Load(reader);
        var metadata = document.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata") ?? throw new InvalidDataException("Missing update package metadata.");
        string Field(string name) => metadata.Elements().SingleOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        if (Field("id") != manifest.AppId || SemanticVersion.Parse(Field("version")) != SemanticVersion.Parse(manifest.Asset.Version) ||
            Field("rid") != manifest.Rid || Field("channel") != manifest.Rid + "-" + manifest.Channel ||
            Field("os") != manifest.Rid.Split('-')[0])
            throw new InvalidDataException("The package identity/version/runtime/channel does not match its signed manifest.");
    }
}
