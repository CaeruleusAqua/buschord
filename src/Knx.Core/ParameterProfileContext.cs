using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Knx;

public enum MaskProfileStatus
{
    Supported, MissingMasterData, UnreadableMasterData, MissingMaskId, MaskNotFound,
    Ambiguous, InvalidDeclaration, MissingHawkData, MissingFirstAppObject, MaskMismatch, UnsupportedApplicationNamespace,
}
public enum ParameterPropertyDomain { System, Application }
public enum ParameterPropertyTypeStatus { Known, NotDeclared, Ambiguous, Unsupported }

/// <summary>One explicitly selected current (LegacyVersion zero) archive-declared mask profile.</summary>
public sealed record MaskProfileSelection(MaskProfileStatus Status, string? MaskVersionId,
    ushort? NumericMask, uint LegacyVersion, byte? FirstAppObjectIndex, string Fingerprint);

/// <summary>
/// Optional interpretation of an unchanged XML property location. An application index is derived
/// only from the matching explicit FirstAppObjectIdx. PDT element bytes are not property capacity,
/// encoded parameter bytes, device detection or a write/readiness permission.
/// </summary>
public sealed record ParameterPropertyProfile(MaskProfileSelection Selection, string? ApplicationMaskVersionId,
    ParameterPropertyDomain? Domain = null, byte? ApplicationObjectIndex = null,
    string? DeclaredPdt = null, uint? ElementBytes = null,
    ParameterPropertyTypeStatus TypeStatus = ParameterPropertyTypeStatus.NotDeclared);

internal sealed record MasterDataProvenance(string Sha256);

/// <summary>
/// Immutable, bounded subset of archived HawkConfigurationData: explicit FirstAppObjectIdx and
/// declared interface-object/PDT facts. No installed master data, additional software profiles,
/// missing-feature defaults, physical device state or executable extension is consulted.
/// </summary>
public sealed class ParameterProfileContext
{
    private sealed record DeclaredObject(ushort? Index, ushort ObjectType, FrozenDictionary<ushort, string?> Properties);
    private readonly DeclaredObject[] _objects;
    private readonly FrozenDictionary<ushort, DeclaredObject> _byIndex;
    private readonly FrozenDictionary<ushort, DeclaredObject[]> _byType;
    public MaskProfileSelection Selection { get; }
    /// <summary>Original archived master XML byte hash, when loaded by Knxproj; excluded from semantic equality/fingerprints.</summary>
    public string? MasterDataHash { get; }
    public int InterfaceObjectCount => _objects.Length;
    public int DeclaredPropertyCount { get; }

    private ParameterProfileContext(MaskProfileStatus status, string? id, string? sourceHash = null,
        ushort? numericMask = null, byte? firstApp = null, DeclaredObject[]? objects = null)
    {
        _objects = objects ?? [];
        _byIndex = _objects.Where(o => o.Index is not null).ToFrozenDictionary(o => o.Index!.Value);
        _byType = _objects.GroupBy(o => o.ObjectType).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        DeclaredPropertyCount = _objects.Sum(o => o.Properties.Count);
        MasterDataHash = sourceHash;
        // Only selected, consumed facts participate. An unrelated mask/manufacturer or source
        // formatting update must not invalidate a parameter draft. Source-byte identity is separate.
        var facts = new
        {
            version = 1, status, id, numericMask, legacyVersion = 0, firstApp,
            objects = _objects.OrderBy(o => o.Index).ThenBy(o => o.ObjectType).Select(o => new
            {
                o.Index, o.ObjectType,
                properties = o.Properties.OrderBy(p => p.Key).Select(p => new { id = p.Key, pdt = p.Value }).ToArray(),
            }).ToArray(),
        };
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(facts)));
        Selection = new(status, id, numericMask, 0, firstApp, fingerprint);
    }

    /// <summary>Represents unreadable/protected metadata without storing a password or blocking ordinary parameter evaluation.</summary>
    public static ParameterProfileContext Unavailable(string? maskVersionId) => new(MaskProfileStatus.UnreadableMasterData, SafeId(maskVersionId));

    /// <summary>
    /// Reads the exact mask reference and current (LegacyVersion absent or zero) set. /20 is
    /// accepted here only as master-data metadata, not as an additional project import format.
    /// Malformed/ambiguous metadata returns a diagnostic context and never executes code.
    /// </summary>
    public static ParameterProfileContext Read(XDocument? masterData, string? maskVersionId)
    {
        var id = SafeId(maskVersionId);
        var sourceHash = masterData?.Annotation<MasterDataProvenance>()?.Sha256;
        ParameterProfileContext Error(MaskProfileStatus status) => new(status, id, sourceHash);
        if (maskVersionId is not null && id is null) return Error(MaskProfileStatus.InvalidDeclaration);
        if (id is null) return Error(MaskProfileStatus.MissingMaskId);
        if (masterData is null) return Error(MaskProfileStatus.MissingMasterData);
        if (!WithinBudget(masterData) || masterData.Root is not { Name.LocalName: "KNX" } root
            || root.Name.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            return Error(MaskProfileStatus.InvalidDeclaration);
        var ns = root.Name.Namespace;
        var masters = root.Elements(ns + "MasterData").Take(2).ToArray();
        if (masters.Length != 1) return Error(masters.Length > 1 ? MaskProfileStatus.Ambiguous : MaskProfileStatus.InvalidDeclaration);
        var groups = masters[0].Elements(ns + "MaskVersions").Take(2).ToArray();
        if (groups.Length != 1) return Error(groups.Length > 1 ? MaskProfileStatus.Ambiguous : MaskProfileStatus.MaskNotFound);
        var masks = groups[0].Elements(ns + "MaskVersion").Where(e => (string?)e.Attribute("Id") == id).Take(2).ToArray();
        if (masks.Length != 1) return Error(masks.Length > 1 ? MaskProfileStatus.Ambiguous : MaskProfileStatus.MaskNotFound);
        if (!UShort(masks[0], "MaskVersion", out var numericMask)) return Error(MaskProfileStatus.InvalidDeclaration);
        var sets = masks[0].Elements(ns + "HawkConfigurationData").Take(65).ToArray();
        if (sets.Length > 64) return Error(MaskProfileStatus.InvalidDeclaration);
        var current = new List<XElement>();
        foreach (var set in sets)
        {
            var version = (string?)set.Attribute("LegacyVersion");
            if (version is not null && !uint.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return Error(MaskProfileStatus.InvalidDeclaration);
            if (version is null || uint.Parse(version, NumberStyles.Integer, CultureInfo.InvariantCulture) == 0) current.Add(set);
        }
        // An omitted LegacyVersion identifies version zero; selection requires an exact version match.
        // This subset explicitly requests zero and rejects duplicate matches instead of guessing.
        if (current.Count != 1) return Error(current.Count > 1 ? MaskProfileStatus.Ambiguous : MaskProfileStatus.MissingHawkData);
        var features = current[0].Elements(ns + "Features").Take(2).ToArray();
        if (features.Length > 1) return Error(MaskProfileStatus.Ambiguous);
        var first = features.SelectMany(e => e.Elements(ns + "Feature"))
            .Where(e => (string?)e.Attribute("Name") == "FirstAppObjectIdx").Take(2).ToArray();
        if (first.Length != 1) return Error(first.Length > 1 ? MaskProfileStatus.Ambiguous : MaskProfileStatus.MissingFirstAppObject);
        if (!byte.TryParse((string?)first[0].Attribute("Value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var firstApp))
            return Error(MaskProfileStatus.InvalidDeclaration);
        var objectGroups = current[0].Elements(ns + "InterfaceObjects").Take(2).ToArray();
        if (objectGroups.Length > 1) return Error(MaskProfileStatus.Ambiguous);
        var elements = objectGroups.SelectMany(e => e.Elements()).Take(1025).ToArray();
        if (elements.Length > 1024 || elements.Any(e => e.Name != ns + "InterfaceObject")) return Error(MaskProfileStatus.InvalidDeclaration);
        var objects = new List<DeclaredObject>();
        var indices = new HashSet<ushort>(); var propertyCount = 0;
        foreach (var element in elements)
        {
            if (!UShort(element, "ObjectType", out var objectType)) return Error(MaskProfileStatus.InvalidDeclaration);
            ushort? index = null;
            if (element.Attribute("Index") is not null)
            {
                if (!UShort(element, "Index", out var value)) return Error(MaskProfileStatus.InvalidDeclaration);
                if (!indices.Add(value)) return Error(MaskProfileStatus.Ambiguous);
                index = value;
            }
            var properties = new Dictionary<ushort, string?>();
            foreach (var property in element.Elements())
            {
                if (++propertyCount > 16384 || property.Name != ns + "Property" || property.HasElements
                    || !UShort(property, "PropertyID", out var propertyId)) return Error(MaskProfileStatus.InvalidDeclaration);
                var pdt = (string?)property.Attribute("PropertyDataType");
                if (pdt?.Length > 256) return Error(MaskProfileStatus.InvalidDeclaration);
                if (!properties.TryAdd(propertyId, pdt)) return Error(MaskProfileStatus.Ambiguous);
            }
            objects.Add(new(index, objectType, properties.ToFrozenDictionary()));
        }
        return new(MaskProfileStatus.Supported, id, sourceHash, numericMask, firstApp, objects.ToArray());
    }

    /// <summary>Gates derived application facts by an explicitly supplied, supported application namespace.</summary>
    public ParameterProfileContext ForApplication(string? actualMask, string? applicationNamespace, bool ambiguous = false)
    {
        if (ambiguous) return ForApplication(actualMask, true);
        if (applicationNamespace is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            return new(MaskProfileStatus.UnsupportedApplicationNamespace, Selection.MaskVersionId, MasterDataHash);
        return ForApplication(actualMask, false);
    }

    internal ParameterProfileContext ForApplication(string? actualMask, bool ambiguous)
    {
        if (ambiguous) return new(MaskProfileStatus.Ambiguous, Selection.MaskVersionId, MasterDataHash);
        if (SafeId(actualMask) is null) return new(actualMask is null ? MaskProfileStatus.MissingMaskId : MaskProfileStatus.InvalidDeclaration, null, MasterDataHash);
        return actualMask == Selection.MaskVersionId ? this
            : new(MaskProfileStatus.MaskMismatch, Selection.MaskVersionId, MasterDataHash);
    }

    internal ParameterPropertyProfile Interpret(ParameterStorageLocation location, string? actualMask)
    {
        var result = new ParameterPropertyProfile(Selection, SafeId(actualMask));
        if (Selection.Status != MaskProfileStatus.Supported || actualMask != Selection.MaskVersionId) return result;
        DeclaredObject? declaration = null;
        if (location.Kind == ParameterStorageKind.PropertyByIndex && location.ObjectIndex is { } index)
        {
            var first = Selection.FirstAppObjectIndex!.Value;
            result = result with { Domain = index < first ? ParameterPropertyDomain.System : ParameterPropertyDomain.Application,
                ApplicationObjectIndex = index < first ? null : checked((byte)(index - first)) };
            _byIndex.TryGetValue(index, out declaration);
        }
        else if (location.Kind == ParameterStorageKind.PropertyByType && location.ObjectType is { } type
            && _byType.TryGetValue(type, out var matches))
        {
            if (matches.Length != 1) return result with { TypeStatus = ParameterPropertyTypeStatus.Ambiguous };
            declaration = matches[0]; // PDT type lookup does not resolve native occurrence to an index.
        }
        if (declaration is null || location.PropertyId is not { } pid || !declaration.Properties.TryGetValue(pid, out var pdt) || pdt is null)
            return result;
        // Explicit supported property-type widths. These are element
        // widths only; neither maximum element count nor property capacity is established.
        uint? bytes = pdt switch { "PDT_VERSION" or "PDT_UNSIGNED_INT" => 2,
            "PDT_UNSIGNED_LONG" => 4, "PDT_GENERIC_08" => 8, _ => null };
        return result with { DeclaredPdt = pdt, ElementBytes = bytes,
            TypeStatus = bytes is null ? ParameterPropertyTypeStatus.Unsupported : ParameterPropertyTypeStatus.Known };
    }

    private static string? SafeId(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static bool UShort(XElement e, string name, out ushort value) =>
        ushort.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static bool WithinBudget(XDocument document)
    {
        if (document.DocumentType is not null || document.Root is null) return false;
        var pending = new Stack<(XNode Node, int Depth)>();
        foreach (var node in document.Nodes())
        {
            if (pending.Count >= 200000) return false;
            pending.Push((node, 1));
        }
        var count = 0; long characters = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > 200000 || item.Depth > 128) return false;
            if (item.Node is XElement element)
            {
                characters += element.Name.ToString().Length;
                foreach (var attribute in element.Attributes())
                {
                    if (++count > 200000) return false;
                    characters += attribute.Name.ToString().Length + attribute.Value.Length;
                }
                foreach (var node in element.Nodes())
                {
                    if (count + pending.Count >= 200000) return false;
                    pending.Push((node, item.Depth + 1));
                }
            }
            else if (item.Node is XText text) characters += text.Value.Length;
            else if (item.Node is XComment comment) characters += comment.Value.Length;
            else if (item.Node is XProcessingInstruction instruction) characters += instruction.Target.Length + instruction.Data.Length;
            if (characters > 16 * 1024 * 1024) return false;
        }
        return true;
    }
}
