using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>
/// Reads KSXML declarations and applies the initial <see cref="DynamicParameters"/> evaluation:
/// instance Value/IsVisible/IsEnabled and block visibility reflect the selected choose branches.
/// Static declarations stay available in DefaultValue, ParameterReference and the source XML.
/// </summary>
public static class Ksxml
{
    public static (List<ParameterType> Types, List<ParameterBlock> Blocks,
        List<ParameterInstance> Instances, List<ComObject> ComObjects,
        List<ComObjectInstanceRef> ComObjectRefs) Parse(XDocument doc) => Parse(doc, out _);

    /// <summary>Parses and returns the dynamic evaluation for callers that need to re-evaluate later.</summary>
    public static (List<ParameterType> Types, List<ParameterBlock> Blocks,
        List<ParameterInstance> Instances, List<ComObject> ComObjects,
        List<ComObjectInstanceRef> ComObjectRefs) Parse(XDocument doc, out DynamicEvaluation evaluation,
            IReadOnlyDictionary<string, string?>? userValues = null, IParameterScriptRuntime? scripts = null,
            string? editedReference = null, bool editableStructure = false, ParameterProfileContext? profile = null)
    {
        var root = StructureExpansion.Expand(doc.Root ?? throw new InvalidDataException("empty XML"), userValues, scripts, editedReference);
        var types = Elements(root, "ParameterType").Select(ParseParameterType).OfType<ParameterType>().ToList();
        var typeMap = types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        // Unexpanded definitions have a separate identity space; never treat templates as instances.
        var parameters = root.DescendantsAndSelf().Where(e => e.Name.LocalName is "Parameter" or "ParameterInstance")
            .Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).ToList();
        var refs = Elements(root, "ParameterRef").Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef"))
            .ToLookup(e => Attr(e, "RefId") ?? "", StringComparer.Ordinal);
        var instances = new List<ParameterInstance>();
        var storage = new ParameterStorageReader(root, profile);
        foreach (var parameter in parameters)
        {
            var references = refs[Attr(parameter, "Id") ?? ""].ToList();
            if (references.Count == 0) instances.Add(ReadInstance(parameter, null, typeMap, storage));
            else foreach (var reference in references) instances.Add(ReadInstance(parameter, reference, typeMap, storage));
        }
        var blocks = Elements(root, "ParameterBlock").Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).Select(el => new ParameterBlock
        {
            Id = Attr(el, "Id") ?? "", Name = Attr(el, "Text") ?? Attr(el, "Name"),
            Rows = Int(el, "Rows", 1), Columns = Int(el, "Columns", 1),
        }).ToList();
        var objects = Elements(root, "ComObject").Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).Select(el => new ComObject
        {
            Id = Attr(el, "Id") ?? "", Name = Attr(el, "Text") ?? Attr(el, "Name") ?? "",
            Description = Attr(el, "Description"), Number = Int(el, "Number"),
            SizeInBit = Int(el, "SizeInBit", ParseObjectSize(Attr(el, "ObjectSize"))),
            DatapointType = Attr(el, "DatapointType"), IsVisible = Visible(el),
        }).ToList();
        var objectRefs = Elements(root, "ComObjectInstanceRef").Select(el => new ComObjectInstanceRef
        {
            Id = Attr(el, "Id") ?? "", ComObjectId = Attr(el, "ComObjectId") ?? Attr(el, "RefId") ?? "",
            Text = Attr(el, "Text"), Number = Int(el, "Number"), FunctionText = Attr(el, "FunctionText"),
        }).ToList();
        evaluation = DynamicParameters.Evaluate(root, userValues, scripts, editedReference, editableStructure);
        DynamicParameters.Apply(evaluation, instances, blocks);
        return (types, blocks, instances, objects, objectRefs);
    }

    private static ParameterInstance ReadInstance(XElement parameter, XElement? reference,
        Dictionary<string, ParameterType> types, ParameterStorageReader storage)
    {
        string? Pick(string name) => Attr(reference, name) ?? Attr(parameter, name);
        var typeId = Pick("ParameterType") ?? Pick("ParameterTypeId");
        var type = typeId is null ? null : types.GetValueOrDefault(typeId);
        var value = Pick("Value");
        var access = Pick("Access");
        var enabled = !bool.TryParse(Pick("IsEnabled"), out var flag) || flag;
        var visible = Visible(parameter) && (reference is null || Visible(reference)) && access != "None";
        var editable = enabled && access is not ("Read" or "None") && type?.UnsupportedReason is null;
        return new ParameterInstance
        {
            Id = Attr(reference, "Id") ?? Attr(parameter, "Id") ?? "",
            ParameterId = reference is null ? null : Attr(parameter, "Id"),
            Name = Pick("Text") ?? Pick("Name") ?? "", Description = Pick("Description"),
            Unit = Pick("SuffixText"), ParameterTypeId = typeId, ParameterType = type,
            Value = value, DefaultValue = value ?? Pick("DefaultValue") ?? Pick("InitialValue"),
            IsVisible = visible, IsEnabled = editable,
            IsStaticallyVisible = visible, IsStaticallyEnabled = editable,
            UnsupportedReason = type?.UnsupportedReason,
            Storage = storage.Read(parameter, reference, type),
            ModuleOrigin = ModuleParameterOrigin.Read(parameter, reference),
            Row = Int(reference ?? parameter, "Row"), Column = Int(reference ?? parameter, "Column"),
        };
    }

    public static List<ParameterInstance> ParseDeviceParameters(string path) => Parse(XDocument.Load(path)).Instances;

    public static ParameterType? ParseParameterType(XElement el)
    {
        if (el.Name.LocalName != "ParameterType") return null;
        var shape = el.Elements().FirstOrDefault(e => e.Name.LocalName != "TypeRestrictions" && e.Name.LocalName.StartsWith("Type", StringComparison.Ordinal));
        var source = shape ?? el;
        var type = new ParameterType
        {
            Id = Attr(el, "Id") ?? "", Name = Attr(el, "Name") ?? "",
            SizeInBit = Int(source, "SizeInBit"), Encoding = Attr(source, "Encoding"),
            NumberType = Attr(source, "Type") ?? Attr(source, "Base"),
            AddressType = Attr(source, "AddressType"), AddressVersion = Attr(source, "Version"),
            TimeUnit = shape?.Name.LocalName == "TypeTime" ? Attr(source, "Unit") : null,
            TimeUIHint = shape?.Name.LocalName == "TypeTime" ? Attr(source, "UIHint") : null,
            PictureResourceId = shape?.Name.LocalName == "TypePicture" ? Attr(source, "RefId") : null,
            PictureAlignment = shape?.Name.LocalName == "TypePicture" ? Attr(source, "HorizontalAlignment") : null,
            Unit = Attr(el, "Unit"), Description = Attr(el, "Description"), IsVisible = Visible(el),
        };
        if (shape is null)
        {
            if (Enum.TryParse<ParameterDataType>(Attr(el, "DataType"), out var dt)) type.DataType = dt;
        }
        else
        {
            type.DataType = shape.Name.LocalName switch
            {
                "TypeNumber" => ParameterDataType.Number,
                "TypeRestriction" => ParameterDataType.Enumeration,
                "TypeFloat" => ParameterDataType.Float,
                "TypeText" => ParameterDataType.Text,
                "TypeColor" => ParameterDataType.Color,
                "TypeTime" => ParameterDataType.Time,
                "TypeIPAddress" => ParameterDataType.IPAddress,
                "TypeRawData" => ParameterDataType.RawData,
                "TypePicture" => ParameterDataType.Picture,
                "TypeNone" => ParameterDataType.None,
                _ => ParameterDataType.None,
            };
            if (type.DataType == ParameterDataType.None)
                type.UnsupportedReason = $"Unsupported parameter shape: {shape.Name.LocalName}";
            if (type.DataType == ParameterDataType.Float)
                type.SizeInBit = type.Encoding switch { "DPT 9" => 16, "IEEE-754 Single" => 32, "IEEE-754 Double" => 64, _ => type.SizeInBit };
        }
        if (Enum.TryParse<ParameterUIHint>(Attr(source, "UIHint"), out var ui)) type.UIHint = ui;
        var min = Number(source, "minInclusive");
        var max = Number(source, "maxInclusive");
        var pattern = Attr(source, "Pattern");
        if (min is not null || max is not null || pattern is not null)
            type.Restrictions.Add(new ParameterRestriction
            {
                Min = min, Max = max, MinimumValue = Attr(source, "minInclusive"),
                MaximumValue = Attr(source, "maxInclusive"), Pattern = pattern,
            });
        if (Attr(source, "minInclusive") is not null && min is null || Attr(source, "maxInclusive") is not null && max is null)
            type.UnsupportedReason = "Invalid numeric restriction.";
        foreach (var enumeration in Elements(source, "Enumeration"))
            type.Restrictions.Add(new ParameterRestriction
            {
                Id = Attr(enumeration, "Id"), EnumerationValue = Attr(enumeration, "Value"),
                EnumerationText = Attr(enumeration, "Text") ?? Attr(enumeration, "Value"),
            });
        if (type.DataType == ParameterDataType.Time)
            type.UnsupportedReason ??= ParameterValidation.TimeDeclarationError(type);
        if (type.DataType == ParameterDataType.RawData)
        {
            if (shape?.Name.LocalName == "TypeRawData" && int.TryParse(Attr(source, "MaxSize"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum)) type.RawDataMaxSize = maximum;
            if (shape?.Name.LocalName != "TypeRawData" || source.HasElements
                || source.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "MaxSize"))
                type.UnsupportedReason ??= "Unsupported TypeRawData declaration.";
            type.UnsupportedReason ??= ParameterValidation.RawDataDeclarationError(type);
        }
        return type;
    }

    public static List<ParameterReference> ParseParameterReferences(XElement root) => Elements(root, "ParameterRef")
        .Select(el => new ParameterReference
        {
            Id = Attr(el, "Id") ?? "", ParameterInstanceId = Attr(el, "RefId") ?? "",
            Text = Attr(el, "Text"), Value = Attr(el, "Value"), InitialValue = Attr(el, "InitialValue"),
        }).ToList();

    // KSXML writes these element names in lower case (choose/when); older hand-written
    // fixtures used title case, so match names case-insensitively.
    private static readonly HashSet<string> UnsupportedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "choose", "when", "assign", "module", "moduleDef", "parameterCalculation",
    };

    /// <summary>Names of semantics the static editor does not execute.</summary>
    public static List<string> UnsupportedFeatures(XElement root) => root.DescendantsAndSelf()
        .Select(e => e.Name.LocalName).Where(UnsupportedNames.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static int ParseObjectSize(string? size)
    {
        var parts = size?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 } || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0) return 0;
        return parts[1] switch { "Bit" or "Bits" => count, "Byte" or "Bytes" when count <= int.MaxValue / 8 => count * 8, _ => 0 };
    }

    private static IEnumerable<XElement> Elements(XElement root, string name) => root.DescendantsAndSelf().Where(e => e.Name.LocalName == name);
    private static string? Attr(XElement? el, string name) => (string?)el?.Attribute(name);
    private static int Int(XElement el, string name, int fallback = 0) => int.TryParse(Attr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private static double? Number(XElement el, string name) => double.TryParse(Attr(el, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    private static bool Visible(XElement el) => !bool.TryParse(Attr(el, "IsVisible"), out var visible) || visible;
}
