using System.Xml.Linq;

namespace Knx;

/// <summary>Datapoint subtype from the KNX master data (knx_master.xml).</summary>
public sealed class DatapointSubtype
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Text { get; init; }
    public int SizeInBit { get; init; }
}

/// <summary>Datapoint main type (knx_master.xml: DatapointType).</summary>
public sealed class DatapointType
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int SizeInBit { get; init; }
    public List<DatapointSubtype> Subtypes { get; } = [];
}

/// <summary>
/// Parses KNX master data (knx_master.xml): datapoint types, manufacturer and product counts.
/// The file's catalog version is available through <see cref="Version"/>.
/// </summary>
public static class MasterData
{
    public static (uint Version, List<DatapointType> Types) LoadDatapointTypes(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException("no <KNX> root");
        var ns = (XNamespace)Knxproj.ProjectNamespace;
        var master = root.Element(ns + "MasterData")
            ?? throw new InvalidDataException("no <MasterData>");
        var version = uint.Parse((string?)master.Attribute("Version") ?? "0");
        var types = new List<DatapointType>();
        foreach (var dt in master.Element(ns + "DatapointTypes")?.Elements(ns + "DatapointType") ?? [])
        {
            var type = new DatapointType
            {
                Id = (string?)dt.Attribute("Id") ?? "",
                Name = (string?)dt.Attribute("Name") ?? "",
                SizeInBit = int.TryParse((string?)dt.Attribute("SizeInBit"), out var s) ? s : 0,
            };
            foreach (var st in dt.Element(ns + "DatapointSubtypes")?.Elements(ns + "DatapointSubtype") ?? [])
            {
                type.Subtypes.Add(new DatapointSubtype
                {
                    Id = (string?)st.Attribute("Id") ?? "",
                    Name = (string?)st.Attribute("Name") ?? "",
                    Text = (string?)st.Attribute("Text"),
                    SizeInBit = type.SizeInBit,
                });
            }
            types.Add(type);
        }
        return (version, types);
    }

    public static (int Manufacturers, int Products) CountCatalogEntries(string path)
    {
        var doc = XDocument.Load(path);
        var ns = (XNamespace)Knxproj.ProjectNamespace;
        var master = doc.Root?.Element(ns + "MasterData")!;
        return (
            master.Element(ns + "Manufacturers")?.Elements(ns + "Manufacturer").Count() ?? 0,
            master.Element(ns + "Products")?.Elements(ns + "Product").Count() ?? 0);
    }
}
