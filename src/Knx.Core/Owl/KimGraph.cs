using System.Globalization;

namespace Knx.Owl;

/// <summary>Device individual of the KIM instance graph (core:Device).</summary>
public sealed record KimDevice(
    string Subject,
    string? Label,
    IndividualAddress? Address,
    string? IndividualAddressHex,
    string? ProductRefId,
    IReadOnlyList<string> PointSubjects);

/// <summary>Function point individual of the KIM instance graph (knx:FunctionPoint).</summary>
public sealed record KimFunctionPoint(
    string Subject,
    string? Label,
    uint? GroupAddressRaw,
    string? DatapointTypeCode,
    DptInfo? DatapointType,
    int? DeclaredMajor);

/// <summary>core:hasPoint link from a device to a function point.</summary>
public sealed record KimPointLink(string Subject, string Target);

/// <summary>
/// Projects a <see cref="Project"/> onto KIM individuals. The instance data is the
/// existing <see cref="SemanticExport"/> output parsed as a graph, so export and
/// reasoning share one vocabulary instead of a second copy of it. The ontology is
/// used to resolve datapoint types and their KNX semantics.
/// </summary>
public sealed class KimGraph
{
    public const string InstallationClass = SemanticExport.CoreNamespace + "Installation";
    public const string DeviceClass = SemanticExport.CoreNamespace + "Device";
    public const string ProductClass = SemanticExport.CoreNamespace + "Product";
    public const string FunctionPointClass = SemanticExport.KnxNamespace + "FunctionPoint";
    public const string HasPointProperty = SemanticExport.CoreNamespace + "hasPoint";
    public const string HasProductProperty = SemanticExport.CoreNamespace + "hasProduct";
    public const string ProductNumberProperty = SemanticExport.CoreNamespace + "number";
    public const string HardwareProperty = SemanticExport.CoreNamespace + "hardware";
    public const string IndividualAddressProperty = SemanticExport.KnxNamespace + "individualAddress";
    public const string GroupAddressProperty = SemanticExport.KnxNamespace + "groupAddress";
    public const string DatapointTypeProperty = SemanticExport.KnxNamespace + "datapointType";
    public const string DptMajorProperty = SemanticExport.KnxNamespace + "dptMajorNumber";

    private KimGraph(Ontology instance, Ontology schema)
    {
        Instance = instance;
        Schema = schema;
        Dpts = new DatapointTypes(schema);

        Installations = instance.IndividualsOfClass(InstallationClass).ToList();
        Devices = instance.IndividualsOfClass(DeviceClass).Select(ReadDevice).ToList();
        FunctionPoints = instance.IndividualsOfClass(FunctionPointClass).Select(ReadFunctionPoint).ToList();
        PointLinks = Devices
            .SelectMany(d => d.PointSubjects.Select(p => new KimPointLink(d.Subject, p)))
            .ToList();
    }

    /// <summary>Instance graph: only the triples produced by <see cref="SemanticExport"/>.</summary>
    public Ontology Instance { get; }

    /// <summary>Reference ontology (the shipped KIM).</summary>
    public Ontology Schema { get; }

    public DatapointTypes Dpts { get; }

    public IReadOnlyList<string> Installations { get; }

    public IReadOnlyList<KimDevice> Devices { get; }

    public IReadOnlyList<KimFunctionPoint> FunctionPoints { get; }

    public IReadOnlyList<KimPointLink> PointLinks { get; }

    /// <summary>Builds the instance graph for a project and resolves it against the ontology.</summary>
    public static KimGraph Build(Project project, Ontology ontology)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ontology);
        var instance = Ontology.FromTurtle(SemanticExport.ToTurtle(project));
        return new KimGraph(instance, ontology);
    }

    public KimFunctionPoint? FindFunctionPoint(string subject)
        => FunctionPoints.FirstOrDefault(p => p.Subject == subject);

    private KimDevice ReadDevice(string subject)
    {
        var hex = Instance.Value(subject, IndividualAddressProperty);
        var address = ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)
            ? new IndividualAddress(raw)
            : (IndividualAddress?)null;

        var product = Instance.Value(subject, HardwareProperty);
        if (string.IsNullOrWhiteSpace(product))
            product = Instance.IriObjects(subject, HasProductProperty)
                .Select(p => Instance.Value(p, ProductNumberProperty))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        return new KimDevice(
            subject,
            Instance.Label(subject),
            address,
            hex,
            product,
            Instance.IriObjects(subject, HasPointProperty).ToList());
    }

    private KimFunctionPoint ReadFunctionPoint(string subject)
    {
        var addressText = Instance.Value(subject, GroupAddressProperty);
        var address = uint.TryParse(addressText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)
            ? raw
            : (uint?)null;

        string? code = null;
        int? declaredMajor = null;
        var dptNode = Instance.IriObjects(subject, DatapointTypeProperty).FirstOrDefault();
        if (dptNode is not null)
        {
            code = Instance.Value(dptNode, Ontology.DctSubject);
            declaredMajor = int.TryParse(Instance.Value(dptNode, DptMajorProperty), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var major) ? major : null;
        }

        var dpt = code is not null
            ? Dpts.Find(code)
            : declaredMajor is int m ? Dpts.Find(m) : null;

        return new KimFunctionPoint(subject, Instance.Label(subject), address, code, dpt, declaredMajor);
    }
}
