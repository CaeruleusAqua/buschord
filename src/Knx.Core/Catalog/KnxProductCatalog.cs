using System.IO.Compression;
using System.Xml.Linq;
using IOPath = System.IO.Path;

namespace Knx.Catalog;

/// <summary>
/// Reader for KNX product databases (*.knxprod): ZIP archives whose main part is
/// knxprod.xml in namespace http://knx.org/xml/prod/20 (older 14/17/19 occur too), or a
/// loose knxprod.xml, or a directory containing one.
///
/// Element names follow the KNX product schema shipped with ETS6: KNX/ManufacturerData/
/// Manufacturer(@RefId)/{Catalog/CatalogSection, Hardware/Hardware(@Id)/Products/Product,
/// Hardware/Hardware/Hardware2Programs/Hardware2Program/ApplicationProgramRef,
/// ApplicationPrograms/ApplicationProgram, Languages/Language(@Identifier)/TranslationUnit/
/// TranslationElement(@RefId)/Translation(@AttributeName,@Text)}. Namespaces and unknown
/// elements are ignored, so unverified parts may still parse.
/// </summary>
public sealed class KnxProductCatalog
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _translations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _hardware2Programs = new(StringComparer.Ordinal);

    // Split archives (the layout every shipped .knxprod we have seen uses) keep the
    // application programs in separate parts; they are loaded on demand by program id.
    private readonly Dictionary<string, string> _partByName = new(StringComparer.Ordinal);
    // Manufacturer display names live in knx_master.xml, not in the catalogue parts.
    private readonly Dictionary<string, string> _manufacturerNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedParts = new(StringComparer.Ordinal);
    private string? _archivePath;

    private KnxProductCatalog(string path) => Path = path;

    /// <summary>Full path of the parsed catalogue file.</summary>
    public string Path { get; internal set; }

    /// <summary>File name without extension; unique per library entry.</summary>
    public string Name => IOPath.GetFileNameWithoutExtension(Path);

    public CatalogManufacturer? Manufacturer { get; private set; }
    public List<CatalogProduct> Products { get; } = [];
    public List<CatalogSection> CatalogSections { get; } = [];
    public List<CatalogApplicationProgram> Programs { get; } = [];
    /// <summary>Image entries of the archive (or sibling image files), names only.</summary>
    public List<string> Images { get; } = [];

    public static KnxProductCatalog Open(string path) => Open(path, 0);

    private static KnxProductCatalog Open(string path, int depth)
    {
        if (depth > 4) throw new InvalidDataException("Catalogue archive nesting exceeds four levels.");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("catalogue path is empty", nameof(path));

        var full = IOPath.GetFullPath(path);
        if (Directory.Exists(full))
        {
            var mono = IOPath.Combine(full, "knxprod.xml");
            if (File.Exists(mono)) return Open(mono);
            if (Directory.EnumerateFiles(full, "Catalog.xml", SearchOption.AllDirectories).Any())
                return OpenSplitDirectory(full);
            full = FindCatalogFile(full);
        }
        if (!File.Exists(full)) throw new FileNotFoundException($"catalogue '{path}' not found.", full);

        var catalog = new KnxProductCatalog(full);
        var images = new List<string>();
        var attachedPrograms = new List<XDocument>();
        XDocument document;

        if (full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || LooksLikeXml(full))
        {
            document = XDocument.Load(full);
            var folder = IOPath.GetDirectoryName(full);
            if (folder is not null && Directory.Exists(folder))
                images.AddRange(Directory.EnumerateFiles(folder).Where(IsImage).Select(f => IOPath.GetFileName(f)));
        }
        else
        {
            using var zip = ZipFile.OpenRead(full);
            var main = PickMainEntry(zip);
            if (main is null) return OpenSplitArchive(full, zip, depth, ReadMasterNames(zip));
            foreach (var (id, name) in ReadMasterNames(zip)) catalog._manufacturerNames[id] = name;
            using (var stream = main.Open()) document = XDocument.Load(stream);
            images.AddRange(zip.Entries.Where(e => IsImage(e.FullName)).Select(e => e.FullName));
            foreach (var entry in zip.Entries)
            {
                // Older archives keep device XMLs as separate parts; tolerate them being absent or broken.
                if (entry == main || !entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.FullName.Contains("_A-", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var stream = entry.Open();
                    var part = XDocument.Load(stream);
                    if (part.Root?.Name.LocalName == "ApplicationProgram") attachedPrograms.Add(part);
                }
                catch (System.Xml.XmlException)
                {
                    // Ignore a broken embedded part; the catalogue itself stays usable.
                }
            }
        }

        catalog.Parse(document);
        catalog.Images.AddRange(images);
        foreach (var part in attachedPrograms)
        {
            try
            {
                catalog.Parse(part);
            }
            catch (InvalidDataException)
            {
                // A part without a Manufacturer element is not an application program.
            }
        }
        return catalog;
    }

    /// <summary>
    /// Split layout as shipped by manufacturers: knx_master.xml plus per-manufacturer
    /// Catalog.xml, Hardware.xml and one XML per application program. Only hardware and
    /// catalogue metadata are read eagerly; programs load on first lookup by id.
    /// </summary>
    private static KnxProductCatalog OpenSplitArchive(string path, ZipArchive zip, int depth,
        Dictionary<string, string> masterNames)
    {
        var catalog = new KnxProductCatalog(path) { _archivePath = path };
        foreach (var (id, name) in masterNames) catalog._manufacturerNames[id] = name;
        var entries = zip.Entries
            .Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hardware = entries.FirstOrDefault(e => e.Name.Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase));
        var catalogFile = entries.FirstOrDefault(e => e.Name.Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase));
        if (hardware is null && catalogFile is null)
        {
            // Manufacturer downloads are often a ZIP that contains the .knxprod (also a ZIP).
            var nested = zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (nested is not null) return OpenNested(path, nested, depth);

            throw new InvalidDataException(
                $"'{path}' is not a KNX product catalogue (no knxprod.xml, Catalog.xml or Hardware.xml).");
        }

        foreach (var entry in new[] { hardware, catalogFile })
        {
            if (entry is null) continue;
            try
            {
                using var stream = entry.Open();
                catalog.Parse(XDocument.Load(stream));
            }
            catch (System.Xml.XmlException)
            {
                // A broken part must not make the whole catalogue unreadable.
            }
        }

        foreach (var entry in entries)
        {
            var stem = IOPath.GetFileNameWithoutExtension(entry.Name);
            if (stem.Contains("_A-", StringComparison.OrdinalIgnoreCase)) catalog._partByName[stem] = entry.FullName;
        }

        catalog.Images.AddRange(zip.Entries.Where(e => IsImage(e.FullName)).Select(e => e.FullName));
        return catalog;
    }

    /// <summary>Unwraps a vendor ZIP that contains the actual .knxprod (nested archive).</summary>
    private static KnxProductCatalog OpenNested(string outerPath, ZipArchiveEntry nested, int depth)
    {
        var extension = IOPath.GetExtension(nested.Name);
        if (string.IsNullOrEmpty(extension)) extension = ".knxprod";
        var temp = IOPath.Combine(IOPath.GetTempPath(),
            "knxclone-" + Guid.NewGuid().ToString("N") + extension);

        try
        {
            using (var input = nested.Open())
            using (var output = File.Create(temp))
                input.CopyTo(output);
            var catalog = Open(temp, depth + 1);
            // Materialize lazy parts before removing their temporary backing archive.
            foreach (var id in catalog._partByName.Keys.ToList()) catalog.FindProgramById(id);
            catalog._partByName.Clear();
            catalog._archivePath = null;
            catalog.Path = outerPath;
            return catalog;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal void Relocate(string path)
    {
        if (_archivePath is not null) _archivePath = path;
        Path = path;
    }

    /// <summary>Split layout extracted to disk (same parts as <see cref="OpenSplitArchive"/>).</summary>
    private static KnxProductCatalog OpenSplitDirectory(string directory)
    {
        var catalog = new KnxProductCatalog(directory);
        foreach (var (id, name) in ReadMasterNames(directory)) catalog._manufacturerNames[id] = name;
        var files = Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).ToList();

        var hardware = files.FirstOrDefault(f => IOPath.GetFileName(f).Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase));
        var catalogFile = files.FirstOrDefault(f => IOPath.GetFileName(f).Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase));
        if (hardware is null && catalogFile is null)
            throw new InvalidDataException($"'{directory}' is not a KNX product catalogue.");

        foreach (var file in new[] { hardware, catalogFile })
        {
            if (file is null) continue;
            try
            {
                catalog.Parse(XDocument.Load(file));
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        foreach (var file in files)
        {
            var stem = IOPath.GetFileNameWithoutExtension(file);
            if (stem.Contains("_A-", StringComparison.OrdinalIgnoreCase)) catalog._partByName[stem] = file;
        }

        catalog.Images.AddRange(files.Where(IsImage).Select(file => IOPath.GetFileName(file)!));
        return catalog;
    }

    /// <summary>Loads one application-program part of a split archive (no-op when already loaded).</summary>
    private void LoadPart(string location)
    {
        try
        {
            XDocument document;
            if (_archivePath is null)
            {
                document = XDocument.Load(location);
            }
            else
            {
                using var zip = ZipFile.OpenRead(_archivePath);
                var entry = zip.GetEntry(location);
                if (entry is null) return;
                using var stream = entry.Open();
                document = XDocument.Load(stream);
            }
            Parse(document);
        }
        catch (System.Xml.XmlException)
        {
        }
        catch (IOException)
        {
        }
    }

    public CatalogProduct? FindProduct(string productId) =>        Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.Ordinal));

    /// <summary>Finds a program by its own id or by a Hardware2Program id that references it.</summary>
    public CatalogApplicationProgram? FindProgram(string programRefId)
    {
        if (string.IsNullOrEmpty(programRefId)) return null;
        var direct = FindProgramById(programRefId);
        if (direct is not null) return direct;
        if (!_hardware2Programs.TryGetValue(programRefId, out var refs)) return null;
        foreach (var reference in refs)
            if (FindProgramById(reference) is { } program) return program;
        return null;
    }

    /// <summary>
    /// Resolves the program a product runs: the Hardware2ProgramRefId of the catalogue entry
    /// first, then the Hardware2Programs of the product's hardware, then the product id itself.
    /// </summary>
    public CatalogApplicationProgram? FindProgramFor(CatalogProduct product)
    {
        foreach (var entryRef in product.EntryRefIds)
            if (FindProgram(entryRef) is { } byEntry) return byEntry;
        foreach (var programId in product.ProgramRefIds)
            if (FindProgram(programId) is { } byHardware) return byHardware;
        return FindProgram(product.Id);
    }

    private CatalogApplicationProgram? FindProgramById(string id)
    {
        var found = Programs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (found is not null) return found;

        // Split archives: pull the part that holds this program, once.
        if (!_partByName.TryGetValue(id, out var location) || !_loadedParts.Add(id)) return null;
        LoadPart(location);
        return Programs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
    }

    private void Parse(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("empty catalogue XML");
        var manufacturers = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Manufacturer").ToList();
        if (manufacturers.Count == 0)
            throw new InvalidDataException("not a KNX product catalogue: no <Manufacturer> element.");

        foreach (var manufacturer in manufacturers)
        {
            _translations.Clear();
            ReadTranslations(manufacturer);
            var id = Attr(manufacturer, "RefId") ?? Attr(manufacturer, "Id") ?? "";
            var name = Text(id, "Name", Attr(manufacturer, "Name"));
            if (string.IsNullOrWhiteSpace(name) || name == id)
                name = _manufacturerNames.GetValueOrDefault(id) ?? name;
            Manufacturer ??= new CatalogManufacturer(id, string.IsNullOrWhiteSpace(name) ? id : name);
            ReadHardware(manufacturer);
            ReadCatalog(manufacturer);
            AddPrograms(manufacturer);
        }
        _translations.Clear();
    }

    /// <summary>Manufacturer display names from knx_master.xml (optional part of the archive).</summary>
    private static Dictionary<string, string> ReadMasterNames(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            e.Name.Equals("knx_master.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = entry.Open();
            return ParseMasterNames(stream);
        }
        catch (System.Xml.XmlException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> ReadMasterNames(string directory)
    {
        var file = Directory.EnumerateFiles(directory, "knx_master.xml", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(file);
            return ParseMasterNames(stream);
        }
        catch (System.Xml.XmlException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> ParseMasterNames(Stream stream)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
        });
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.LocalName != "Manufacturer") continue;
            var id = reader.GetAttribute("Id");
            var name = reader.GetAttribute("Name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) names[id] = name;
        }
        return names;
    }

    private void ReadTranslations(XElement manufacturer)
    {
        foreach (var translation in manufacturer.Descendants().Where(e => e.Name.LocalName == "Translation"))
        {
            var language = Attr(translation, "Language")
                ?? translation.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Language")
                    ?.Attribute("Identifier")?.Value
                ?? Attr(manufacturer, "DefaultLanguage")
                ?? "en-US";
            var refId = translation.Ancestors().FirstOrDefault(a => a.Name.LocalName == "TranslationElement")
                    ?.Attribute("RefId")?.Value
                ?? translation.Ancestors().FirstOrDefault(a => a.Name.LocalName == "TranslationUnit")
                    ?.Attribute("RefId")?.Value
                ?? Attr(translation, "RefId")
                ?? "";
            var attribute = Attr(translation, "AttributeName") ?? "Text";
            var text = Attr(translation, "Text") ?? translation.Value;
            if (language.Length == 0 || refId.Length == 0 || string.IsNullOrWhiteSpace(text)) continue;

            if (!_translations.TryGetValue(language, out var byRef))
                _translations[language] = byRef = new(StringComparer.Ordinal);
            if (!byRef.TryGetValue(refId, out var byAttribute))
                byRef[refId] = byAttribute = new(StringComparer.Ordinal);
            byAttribute[attribute] = text;
        }
    }

    private void ReadHardware(XElement manufacturer)
    {
        foreach (var hardware in manufacturer.Descendants()
                     .Where(e => e.Name.LocalName == "Hardware" && Attr(e, "Id") is not null))
        {
            var hardwareId = Attr(hardware, "Id")!;
            var programIds = new List<string>();
            var hardware2ProgramIds = new List<string>();
            foreach (var link in hardware.Descendants().Where(e => e.Name.LocalName == "Hardware2Program"))
            {
                var linkId = Attr(link, "Id");
                var references = link.Descendants()
                    .Where(e => e.Name.LocalName == "ApplicationProgramRef")
                    .Select(e => Attr(e, "RefId"))
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Select(r => r!)
                    .ToList();
                if (linkId is not null) _hardware2Programs[linkId] = references;
                hardware2ProgramIds.Add(linkId ?? "");
                programIds.AddRange(references);
            }

            foreach (var element in hardware.Descendants()
                         .Where(e => e.Name.LocalName == "Product" && Attr(e, "Id") is not null))
            {
                var id = Attr(element, "Id")!;
                var product = new CatalogProduct
                {
                    Id = id,
                    Name = Text(id, "Text", Attr(element, "Text")),
                    OrderNumber = Attr(element, "OrderNumber"),
                    HardwareRefId = hardwareId,
                    Description = Text(id, "VisibleDescription", Attr(element, "VisibleDescription")),
                };
                foreach (var programId in programIds)
                    if (!product.ProgramRefIds.Contains(programId)) product.ProgramRefIds.Add(programId);
                foreach (var linkId in hardware2ProgramIds)
                    if (linkId.Length > 0 && !product.Hardware2ProgramIds.Contains(linkId))
                        product.Hardware2ProgramIds.Add(linkId);
                Products.Add(product);
            }
        }
    }

    private void ReadCatalog(XElement manufacturer)
    {
        var roots = manufacturer.Descendants()
            .Where(e => e.Name.LocalName == "CatalogSection" && e.Parent?.Name.LocalName != "CatalogSection");
        foreach (var section in roots) CatalogSections.Add(ReadSection(section, ""));
    }

    private CatalogSection ReadSection(XElement element, string parentPath)
    {
        var id = Attr(element, "Id") ?? "";
        var section = new CatalogSection
        {
            Id = id,
            Name = Text(id, "Name", Attr(element, "Name")),
            Number = Int(element, "Number"),
        };
        var path = parentPath.Length == 0 ? section.Name : parentPath + "/" + section.Name;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "CatalogSection":
                    section.Sections.Add(ReadSection(child, path));
                    break;
                case "CatalogItem":
                    section.Items.Add(ReadItem(child, path));
                    break;
            }
        }
        return section;
    }

    private CatalogItem ReadItem(XElement element, string path)
    {
        var id = Attr(element, "Id") ?? "";
        var item = new CatalogItem
        {
            Id = id,
            Name = Text(id, "Name", Attr(element, "Name")),
            ProductRefId = Attr(element, "ProductRefId"),
            Description = Text(id, "VisibleDescription", Attr(element, "VisibleDescription")),
            Hardware2ProgramRefId = Attr(element, "Hardware2ProgramRefId"),
        };
        if (item.ProductRefId is { } productId && FindProduct(productId) is { } product)
        {
            if (!product.CatalogPaths.Contains(path)) product.CatalogPaths.Add(path);
            if (item.Hardware2ProgramRefId is { } hardware2Program &&
                !product.EntryRefIds.Contains(hardware2Program))
                product.EntryRefIds.Add(hardware2Program);
        }
        return item;
    }

    private void AddPrograms(XElement scope)
    {
        foreach (var element in scope.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "ApplicationProgram" && Attr(e, "Id") is not null))
        {
            var id = Attr(element, "Id")!;
            if (Programs.Any(p => p.Id == id)) continue;

            var program = new CatalogApplicationProgram
            {
                Id = id,
                Name = Text(id, "Name", Attr(element, "Name")),
                ApplicationNumber = Attr(element, "ApplicationNumber"),
                ApplicationVersion = Attr(element, "ApplicationVersion"),
                SchemaVersion = Attr(element, "SchemaVersion") ?? Attr(element, "CreatedFromLegacySchemaVersion"),
                Broken = IsTrue(Attr(element, "Broken")),
                RegistrationStatus = Attr(element, "RegistrationStatus"),
                MinEtsVersion = Attr(element, "MinEtsVersion"),
                MaskVersion = Attr(element, "MaskVersion") ?? Attr(element, "ManagementModel"),
                DownloadInfoIncomplete = IsTrue(Attr(element, "DownloadInfoIncomplete")),
            };
            var translated = new XElement(element);
            foreach (var declaration in translated.DescendantsAndSelf())
            {
                var declarationId = Attr(declaration, "Id");
                if (declarationId is null) continue;
                foreach (var attribute in new[] { "Text", "SuffixText", "Description" })
                {
                    var text = Text(declarationId, attribute, Attr(declaration, attribute));
                    if (text.Length > 0) declaration.SetAttributeValue(attribute, text);
                }
            }
            var parsed = Ksxml.Parse(new XDocument(translated));
            program.ParameterTypes.AddRange(parsed.Types);
            program.ParameterBlocks.AddRange(parsed.Blocks);
            program.ParameterInstances.AddRange(parsed.Instances);
            program.ParameterReferences.AddRange(Ksxml.ParseParameterReferences(translated));
            program.UnsupportedFeatures.AddRange(Ksxml.UnsupportedFeatures(translated));
            ReadExtensions(program, element);
            ReadParameterPlugins(program, element);

            foreach (var comObject in element.Descendants().Where(e => e.Name.LocalName == "ComObject"))
            {
                var comObjectId = Attr(comObject, "Id") ?? "";
                var text = Text(comObjectId, "Text", Attr(comObject, "Text"));
                var functionText = Text(comObjectId, "FunctionText", Attr(comObject, "FunctionText"));
                program.ComObjects.Add(new ComObject
                {
                    Id = comObjectId,
                    Name = text.Length > 0 ? text : Attr(comObject, "Name") ?? comObjectId,
                    Number = Int(comObject, "Number"),
                    SizeInBit = int.TryParse(Attr(comObject, "SizeInBit"), out var size) ? size : Ksxml.ParseObjectSize(Attr(comObject, "ObjectSize")),
                    DatapointType = Attr(comObject, "DatapointType"),
                });
                program.ComObjectDisplay[comObjectId] = (text, functionText);
            }

            Programs.Add(program);
        }
    }

    /// <summary>
    /// Reads Static/Extension. ETS declares the plug-ins as attributes (EtsUiPlugin="{GUID}" or
    /// an assembly-qualified type name); an empty value means "not configured". Elements with
    /// Ets* names are accepted too, so older/newer schema shapes do not silently disappear.
    /// </summary>
    private static void ReadExtensions(CatalogApplicationProgram program, XElement element)
    {
        foreach (var extension in element.Descendants().Where(e => e.Name.LocalName == "Extension"))
        {
            foreach (var attribute in extension.Attributes())
                if (IsEtsName(attribute.Name.LocalName) && !string.IsNullOrWhiteSpace(attribute.Value))
                    program.AddExtension(attribute.Name.LocalName);
            foreach (var child in extension.Elements())
                if (IsEtsName(child.Name.LocalName))
                    program.AddExtension(child.Name.LocalName);
            if (IsTrue((string?)extension.Attribute("RequiresExternalSoftware")))
                program.RequiresExternalSoftware = true;
        }
    }

    private static void ReadParameterPlugins(CatalogApplicationProgram program, XElement element)
    {
        foreach (var declaration in element.Descendants()
                     .Where(e => e.Name.LocalName is "ParameterRef" or "ParameterInstanceRef"))
        {
            var plugin = (string?)declaration.Attribute("Plugin");
            if (string.IsNullOrWhiteSpace(plugin)) continue;
            // A ParameterRef references a Parameter by RefId; ParameterInstanceRef only has RefId.
            foreach (var key in new[] { (string?)declaration.Attribute("Id"), (string?)declaration.Attribute("RefId") })
                if (!string.IsNullOrWhiteSpace(key)) program.SetParameterPlugin(key, plugin);
        }
    }

    private static bool IsEtsName(string name) => name.StartsWith("Ets", StringComparison.Ordinal);

    private static bool IsTrue(string? value) =>
        value is "1" || bool.TryParse(value, out var flag) && flag;

    private string Text(string refId, string attribute, string? fallback)
    {
        Dictionary<string, string>? byLanguage = null;
        foreach (var (language, byRef) in _translations)
        {
            if (!byRef.TryGetValue(refId, out var byAttribute)) continue;
            if (!byAttribute.TryGetValue(attribute, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            (byLanguage ??= new(StringComparer.OrdinalIgnoreCase))[language] = text;
        }
        return CatalogText.Pick(byLanguage, fallback);
    }

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    private static int Int(XElement element, string name) =>
        int.TryParse(Attr(element, name), out var value) ? value : 0;

    private static bool LooksLikeXml(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[64];
            var read = stream.Read(buffer, 0, buffer.Length);
            var offset = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            while (offset < read && buffer[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
            return offset < read && buffer[offset] == (byte)'<';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsImage(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);

    private static string FindCatalogFile(string directory)
    {
        var named = Directory.EnumerateFiles(directory)
            .FirstOrDefault(f => IOPath.GetFileName(f).Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase));
        if (named is not null) return named;

        var archive = Directory.EnumerateFiles(directory, "*.knxprod").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (archive is not null) return archive;

        return Directory.EnumerateFiles(directory, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new InvalidDataException($"no knxprod.xml or *.knxprod in '{directory}'.");
    }

    private static ZipArchiveEntry? PickMainEntry(ZipArchive zip)
    {
        var xml = zip.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
        var named = xml.FirstOrDefault(e => e.Name.Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase));
        if (named is not null) return named;

        // Archives with a differently named main part: it must carry catalogue/hardware
        // content, which excludes knx_master.xml and single application-program parts.
        foreach (var candidate in xml
                     .Where(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\'))
                     .OrderByDescending(e => e.Length))
            if (ContainsCatalogueContent(candidate)) return candidate;

        return null;
    }

    /// <summary>Cheap streaming check: does this part hold a Manufacturer with Catalog/Hardware?</summary>
    private static bool ContainsCatalogueContent(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = System.Xml.XmlReader.Create(stream,
                new System.Xml.XmlReaderSettings { IgnoreWhitespace = true });
            var sawManufacturer = false;
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "Manufacturer":
                        sawManufacturer = true;
                        break;
                    case "Catalog" or "CatalogSection" or "Hardware":
                        return sawManufacturer;
                }
            }
            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
