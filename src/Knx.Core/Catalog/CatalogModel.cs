using System.Globalization;
using System.Xml.Linq;

namespace Knx.Catalog;

/// <summary>Location of an application program's full XML, for the lazy parameter model.</summary>
internal sealed record ProgramSource(string? Entry, string? FilePath);

/// <summary>
/// Electrical and topology facts of one hardware item from a manufacturer's Hardware.xml
/// (prod: Hardware). <see cref="BusCurrent"/> is the bus current in mA; on a power supply it is
/// the current the unit delivers. The attribute names are those shipped in real product databases
/// (MDT: BusCurrent="10", BusCurrent="640" IsPowerSupply="true", IsCoupler="true").
/// </summary>
public sealed record CatalogHardware
{
    public required string HardwareId { get; init; }
    public string Name { get; init; } = "";
    public double? BusCurrent { get; init; }
    public bool IsPowerSupply { get; init; }
    public bool IsChoke { get; init; }
    public bool IsCoupler { get; init; }
    public bool IsPowerLineRepeater { get; init; }
    public bool IsIpEnabled { get; init; }
    /// <summary>Medium types of the Hardware2Program (Hardware2Program@MediumTypes, e.g. "MT-0").</summary>
    public IReadOnlyList<string> MediumTypeIds { get; init; } = [];
}

/// <summary>
/// Reads the hardware facts ETS uses for bus power and coupler planning from Hardware.xml parts.
/// Only attributes observed in shipped product databases are interpreted; every other attribute
/// stays in the XML untouched. A device resolves through either its Hardware2Program id or its
/// product id, so both are keys of the result.
/// </summary>
public static class CatalogHardwareFacts
{
    public static void Read(XDocument document, IDictionary<string, CatalogHardware> facts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(facts);
        foreach (var hardware in document.Descendants().Where(e =>
                     e.Name.LocalName == "Hardware" && e.Attribute("Id") is not null))
        {
            var programs = Child(hardware, "Hardware2Programs", "Hardware2Program")
                .Where(e => e.Attribute("Id") is not null).ToList();
            var products = Child(hardware, "Products", "Product")
                .Where(e => e.Attribute("Id") is not null).ToList();
            // A nested Hardware helper or some unrelated element without products/programs is not a device.
            if (programs.Count == 0 && products.Count == 0) continue;

            var hardwareId = (string)hardware.Attribute("Id")!;
            var current = Current(hardware.Attribute("BusCurrent")?.Value);
            var allMediums = programs.SelectMany(p => Mediums(p.Attribute("MediumTypes")?.Value))
                .Distinct(StringComparer.Ordinal).ToList();
            CatalogHardware Fact(IReadOnlyList<string> mediums) => new()
            {
                HardwareId = hardwareId,
                Name = (string?)hardware.Attribute("Name") ?? "",
                BusCurrent = current,
                IsPowerSupply = Flag(hardware, "IsPowerSupply"),
                IsChoke = Flag(hardware, "IsChoke"),
                IsCoupler = Flag(hardware, "IsCoupler"),
                IsPowerLineRepeater = Flag(hardware, "IsPowerLineRepeater"),
                IsIpEnabled = Flag(hardware, "IsIpEnabled"),
                MediumTypeIds = mediums,
            };

            foreach (var program in programs)
                facts.TryAdd((string)program.Attribute("Id")!, Fact(Mediums(program.Attribute("MediumTypes")?.Value)));
            foreach (var product in products)
                facts.TryAdd((string)product.Attribute("Id")!, Fact(allMediums));
        }
    }

    private static IEnumerable<XElement> Child(XElement hardware, string container, string name) =>
        hardware.Elements().Where(e => e.Name.LocalName == container)
            .SelectMany(e => e.Elements()).Where(e => e.Name.LocalName == name);

    private static bool Flag(XElement element, string name) =>
        element.Attribute(name)?.Value.Trim().ToLowerInvariant() is "true" or "1";

    // Product databases write both "10" and "2.0000000e+001"; the value is always invariant.
    private static double? Current(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var current) &&
        double.IsFinite(current) && current >= 0 ? current : null;

    private static IReadOnlyList<string> Mediums(string? value) =>
        value is null ? [] : value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Manufacturer of a product database (prod: Manufacturer@RefId, name from the translation unit).</summary>
public sealed record CatalogManufacturer(string Id, string Name);

/// <summary>
/// One product of a hardware item (prod: Hardware/Products/Product). The name is the
/// translated Text attribute; ProgramRefIds lists the application programs the hardware
/// can run (resolved through Hardware2Programs/ApplicationProgramRef).
/// </summary>
public sealed class CatalogProduct
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string? OrderNumber { get; set; }
    public string? HardwareRefId { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// Lowercase search text of the product and its catalogue, maintained by the reader/index.
    /// Empty for a hand-built product; the browser then composes it on demand.
    /// </summary>
    public string SearchText { get; set; } = "";
    public List<string> ProgramRefIds { get; } = [];
    /// <summary>Section path(s) where the product is listed, e.g. "Beleuchtung/Schaltaktoren".</summary>
    public List<string> CatalogPaths { get; } = [];
    /// <summary>Hardware2Program ids of the containing hardware (default program variants).</summary>
    internal List<string> Hardware2ProgramIds { get; } = [];
    /// <summary>Hardware2Program ids named by the catalogue entries for this product (preferred).</summary>
    internal List<string> EntryRefIds { get; } = [];
}

/// <summary>Catalogue node (prod: CatalogSection, nested through further CatalogSection elements).</summary>
public sealed class CatalogSection
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public List<CatalogSection> Sections { get; } = [];
    public List<CatalogItem> Items { get; } = [];
}

/// <summary>Catalogue entry (prod: CatalogItem); ProductRefId points at the Hardware/Products/Product.</summary>
public sealed class CatalogItem
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string? ProductRefId { get; set; }
    public string? Description { get; set; }
    /// <summary>Program variant of the entry; preferred over the product when resolving the program.</summary>
    public string? Hardware2ProgramRefId { get; set; }
}

/// <summary>
/// Application program of a product database (prod: ApplicationPrograms/ApplicationProgram).
/// Parameters use the KSXML model of the project reader.
/// </summary>
public sealed class CatalogApplicationProgram
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string? ApplicationNumber { get; set; }
    public string? ApplicationVersion { get; set; }
    /// <summary>Lowercase search text (program and communication-object metadata) for the catalogue browser.</summary>
    public string SearchText { get; set; } = "";
    /// <summary>Normalised datapoint ids surrounded by ';', e.g. ";dpst-1-1;dpst-1-2;".</summary>
    public string NormalizedDatapointTypes { get; set; } = "";
    /// <summary>Where the full KSXML parameter model can be read; null once it is materialised.</summary>
    internal ProgramSource? Source { get; set; }
    /// <summary>The KSXML parameter model was built; listing and search never build it.</summary>
    internal bool ModelLoaded { get; set; }
    /// <summary>ETS product-database schema version of the application declaration, when present.</summary>
    public string? SchemaVersion { get; set; }
    /// <summary>ETS marked the application as broken; editing/download must be blocked.</summary>
    public bool Broken { get; set; }
    /// <summary>ETS registration status, e.g. registered/certified; displayed read-only.</summary>
    public string? RegistrationStatus { get; set; }
    /// <summary>Minimum ETS version declared by the manufacturer.</summary>
    public string? MinEtsVersion { get; set; }
    /// <summary>Mask version / management model declared by the application.</summary>
    public string? MaskVersion { get; set; }
    public bool DownloadInfoIncomplete { get; set; }
    private List<ComObject>? _comObjects;
    private Dictionary<string, (string? Text, string? FunctionText)>? _comObjectDisplay;
    private string? _packedComObjects;
    private readonly object _comObjectGate = new();

    /// <summary>
    /// Communication objects of the application. A warm index keeps them packed and this
    /// materialises them on first use, so loading a library does not build objects that no
    /// pane shows yet; the objects are identical to the ones the XML reader creates.
    /// </summary>
    public List<ComObject> ComObjects
    {
        get
        {
            lock (_comObjectGate) return Materialise().Objects;
        }
    }

    /// <summary>Remembers the packed index payload; the objects are built on first access.</summary>
    internal void SetPackedComObjects(string packed)
    {
        lock (_comObjectGate) _packedComObjects = packed;
    }

    private (List<ComObject> Objects, Dictionary<string, (string? Text, string? FunctionText)> Display) Materialise()
    {
        if (_comObjects is { } materialised && _comObjectDisplay is { } display) return (materialised, display);
        // Build into fresh collections and publish them, so a reader that got the previous
        // (empty) list never observes it growing underneath an enumeration.
        var objects = new List<ComObject>();
        var texts = new Dictionary<string, (string? Text, string? FunctionText)>();
        if (_packedComObjects is { } packed)
        {
            _packedComObjects = null;
            CatalogComObjectPacking.TryUnpack(packed, objects, texts);
        }
        _comObjects = objects;
        _comObjectDisplay = texts;
        return (objects, texts);
    }

    public List<ParameterType> ParameterTypes { get; } = [];
    public List<ParameterBlock> ParameterBlocks { get; } = [];
    public List<ParameterInstance> ParameterInstances { get; } = [];
    public List<ParameterReference> ParameterReferences { get; } = [];
    /// <summary>Static declarations are editable, but these dynamic semantics are not evaluated.</summary>
    public List<string> UnsupportedFeatures { get; } = [];
    /// <summary>
    /// Plug-in declarations of Static/Extension, as raw ETS attribute/element names with a
    /// non-empty value (EtsUiPlugin, EtsDownloadPlugin, EtsDataHandler, ...). Empty attributes
    /// such as EtsUiPlugin="" mean "not configured" and are not listed.
    /// </summary>
    public IReadOnlyList<string> Extensions => _extensions;
    private readonly List<string> _extensions = [];
    internal void AddExtension(string name)
    {
        if (!_extensions.Contains(name)) _extensions.Add(name);
    }
    /// <summary>Static/Extension RequiresExternalSoftware: the device also needs vendor software.</summary>
    public bool RequiresExternalSoftware { get; set; }
    /// <summary>
    /// An ETS DCA/plug-in provides this program's parameter UI, download or data handling.
    /// Its values are read-only here: the plug-in cannot be executed outside ETS.
    /// </summary>
    public bool RequiresDca => _extensions.Any(IsDcaExtension);
    /// <summary>Parameters can be displayed and edited without executing an ETS plug-in.</summary>
    public bool IsStaticallyEditable => !RequiresDca;
    /// <summary>Broken or incomplete applications must not be edited/downloaded.</summary>
    public bool IsEditable => !Broken && !DownloadInfoIncomplete && IsStaticallyEditable;
    /// <summary>Parameter id (ParameterRef Id or RefId) -> ParameterRef@Plugin value.</summary>
    public IReadOnlyDictionary<string, string> ParameterPlugins => _parameterPlugins;
    private readonly Dictionary<string, string> _parameterPlugins = new(StringComparer.Ordinal);

    internal void SetParameterPlugin(string parameterId, string pluginId) => _parameterPlugins[parameterId] = pluginId;
    /// <summary>ComObject display texts (Id -> Text, FunctionText); ComObject itself has no Text member.</summary>
    internal Dictionary<string, (string? Text, string? FunctionText)> ComObjectDisplay
    {
        get
        {
            lock (_comObjectGate) return Materialise().Display;
        }
    }

    /// <summary>Plug-in that provides the editor of a parameter, or null for a static editor.</summary>
    public string? PluginIdFor(string? parameterId) =>
        parameterId is not null && _parameterPlugins.TryGetValue(parameterId, out var plugin) ? plugin : null;

    public string? PluginIdFor(ParameterInstance instance) =>
        PluginIdFor(instance.ParameterId) ?? PluginIdFor(instance.Id);

    public string? PluginIdFor(ParameterReference reference) =>
        PluginIdFor(reference.Id) ?? PluginIdFor(reference.ParameterInstanceId);

    private static bool IsDcaExtension(string name) =>
        name is "EtsUiPlugin" or "EtsDownloadPlugin" or "EtsDataHandler" or "EtsDataHandlerCapabilities";
}

/// <summary>
/// Language selection for catalogue texts: de-DE, then en-US, then the literal attribute
/// value of the element (which is written in its DefaultLanguage).
/// </summary>
public static class CatalogText
{
    public static readonly string[] Preferred = ["de-DE", "de", "en-US", "en"];

    /// <summary>Picks from a language table (identifier -> text) in <see cref="Preferred"/> order.</summary>
    public static string Pick(IReadOnlyDictionary<string, string>? byLanguage, string? fallback)
    {
        if (byLanguage is not null)
            foreach (var language in Preferred)
                if (byLanguage.TryGetValue(language, out var text))
                {
                    var picked = First(text);
                    if (picked.Length > 0) return picked;
                }
        return First(fallback);
    }

    private static string First(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        return "";
    }
}
