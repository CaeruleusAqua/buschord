using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Knx;

public enum ArchivedResourceContextStatus
{
    Supported, MissingMasterData, MissingMaskId, UnsupportedApplicationNamespace, UnsupportedMasterNamespace,
    MaskNotFound, MissingConfiguration, Ambiguous, InvalidDeclaration, LimitExceeded,
}
public enum ArchivedResourceAddressSpace
{
    StandardMemory, RelativeMemory, SystemProperty, AppProperty, PropertyByType, Pointer, None, Constant, Adc,
}
public enum ArchivedResourceResolutionStatus { Found, ContextUnavailable, Missing, Ambiguous, InvalidRequest, ManagementStyleRequired }

/// <summary>Explicit archive coordinates. Native indices, application indices and type selectors are distinct.</summary>
public sealed record ArchivedResourceLocation(ArchivedResourceAddressSpace AddressSpace, uint? Offset = null,
    ushort? NativeObjectIndex = null, ushort? ApplicationObjectIndex = null, ushort? ObjectType = null,
    ushort? Occurrence = null, ushort? PropertyId = null, string? PointerResource = null, ushort? InterfaceObjectRef = null);

/// <summary>One declared named resource, without an inferred physical location or supplied bytes.</summary>
public sealed class ArchivedResourceDescriptor
{
    public string Name { get; }
    public IReadOnlyList<string> Access { get; }
    public IReadOnlyList<string> ManagementStyles { get; }
    public ArchivedResourceLocation? Location { get; }
    public ArchivedResourceLocation? ImageLocation { get; }
    public uint? DeclaredLength { get; }
    public string? Flavour { get; }
    public bool? Optional { get; }
    public string? ReadAccess { get; }
    public string? WriteAccess { get; }

    internal ArchivedResourceDescriptor(string name, string[] access, string[] styles, ArchivedResourceLocation? location,
        ArchivedResourceLocation? imageLocation, uint? length, string? flavour, bool? optional, string? read, string? write)
    {
        Name = name; Access = Array.AsReadOnly((string[])access.Clone()); ManagementStyles = Array.AsReadOnly((string[])styles.Clone());
        Location = location; ImageLocation = imageLocation; DeclaredLength = length; Flavour = flavour; Optional = optional;
        ReadAccess = read; WriteAccess = write;
    }
}

/// <summary>Declared region geometry and rights; length is neither an initial buffer nor property capacity.</summary>
public sealed record ArchivedMemoryRegion(string MemoryType, ArchivedResourceLocation Location, uint Length,
    bool? Optional = null, string? ReadAccess = null, string? WriteAccess = null);
public sealed record ArchivedResourceResolution(ArchivedResourceResolutionStatus Status, ArchivedResourceDescriptor? Descriptor = null);

/// <summary>
/// Bounded, immutable current-zero resource declarations from one exact archived mask.
/// Resolving a declaration does not resolve pointers, establish initial data, activate patches or permit downloads.
/// </summary>
public sealed class ArchivedResourceContext
{
    private const int MaxResources = 4096, MaxRegions = 1024, MaxTokens = 16;
    private readonly FrozenDictionary<string, ArchivedResourceDescriptor[]> _byName;
    public ArchivedResourceContextStatus Status { get; }
    public string? MaskVersionId { get; }
    public ushort? NumericMask { get; }
    public uint LegacyVersion => 0;
    public byte? FirstAppObjectIndex { get; }
    public bool? PropertyMappedLsms { get; }
    /// <summary>Original master entry byte hash when supplied by the archive reader; otherwise absent.</summary>
    public string? MasterDataHash { get; }
    /// <summary>Hash of the supplied XML document serialized without added formatting, not a ZIP entry byte hash.</summary>
    public string? DocumentHash { get; }
    /// <summary>Independent fingerprint of selected, consumed declarations and status; unrelated profiles are excluded.</summary>
    public string Fingerprint { get; }
    public IReadOnlyList<ArchivedResourceDescriptor> Resources { get; }
    public IReadOnlyList<ArchivedMemoryRegion> MemoryRegions { get; }

    private ArchivedResourceContext(ArchivedResourceContextStatus status, string? id, string? source = null, string? document = null,
        ushort? numeric = null, byte? first = null, bool? mapped = null,
        ArchivedResourceDescriptor[]? resources = null, ArchivedMemoryRegion[]? regions = null)
    {
        Status = status; MaskVersionId = id; MasterDataHash = source; DocumentHash = document; NumericMask = numeric;
        FirstAppObjectIndex = first; PropertyMappedLsms = mapped;
        resources ??= []; regions ??= [];
        Resources = Array.AsReadOnly((ArchivedResourceDescriptor[])resources.Clone());
        MemoryRegions = Array.AsReadOnly((ArchivedMemoryRegion[])regions.Clone());
        _byName = resources.GroupBy(r => r.Name, StringComparer.Ordinal).ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var facts = new
        {
            version = 1, status, id, numeric, legacyVersion = 0, first, mapped,
            resources = resources.OrderBy(ResourceKey, StringComparer.Ordinal).Select(r => new
            {
                r.Name, r.Access, r.ManagementStyles, r.Location, r.ImageLocation, r.DeclaredLength, r.Flavour,
                r.Optional, r.ReadAccess, r.WriteAccess,
            }).ToArray(),
            regions = regions.OrderBy(r => JsonSerializer.Serialize(r), StringComparer.Ordinal).ToArray(),
        };
        Fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(facts));
    }

    /// <summary>Reads only the exact declared mask's unique current-zero set, with no installed-data fallback.</summary>
    public static ArchivedResourceContext Read(XDocument? master, string? exactMaskId, string? appNamespace)
    {
        var id = SafeName(exactMaskId) ? exactMaskId : null;
        var provenance = master?.Annotation<MasterDataProvenance>()?.Sha256;
        string? document = null;
        ArchivedResourceContext Error(ArchivedResourceContextStatus status) => new(status, id, provenance, document);
        if (!SupportedNamespace(appNamespace)) return Error(ArchivedResourceContextStatus.UnsupportedApplicationNamespace);
        if (exactMaskId is not null && id is null) return Error(ArchivedResourceContextStatus.InvalidDeclaration);
        if (id is null) return Error(ArchivedResourceContextStatus.MissingMaskId);
        if (master is null) return Error(ArchivedResourceContextStatus.MissingMasterData);
        if (master.Root is null || master.DocumentType is not null) return Error(ArchivedResourceContextStatus.InvalidDeclaration);
        if (!WithinBudget(master)) return Error(ArchivedResourceContextStatus.LimitExceeded);
        try { document = Hash(Encoding.UTF8.GetBytes(master.ToString(SaveOptions.DisableFormatting))); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Xml.XmlException)
        { return Error(ArchivedResourceContextStatus.InvalidDeclaration); }
        if (master.Root is not { Name.LocalName: "KNX" } root) return Error(ArchivedResourceContextStatus.InvalidDeclaration);
        if (!SupportedNamespace(root.Name.NamespaceName)) return Error(ArchivedResourceContextStatus.UnsupportedMasterNamespace);
        var ns = root.Name.Namespace;
        try
        {
            var metadata = One(root, ns + "MasterData", true)!;
            var masks = One(metadata, ns + "MaskVersions", true)!;
            var matches = masks.Elements(ns + "MaskVersion").Where(e => (string?)e.Attribute("Id") == id).Take(2).ToArray();
            if (matches.Length != 1) return Error(matches.Length == 0 ? ArchivedResourceContextStatus.MaskNotFound : ArchivedResourceContextStatus.Ambiguous);
            var mask = matches[0];
            var numeric = (ushort)RequiredUInt(mask, "MaskVersion", ushort.MaxValue);
            var sets = mask.Elements(ns + "HawkConfigurationData").Take(65).ToArray();
            if (sets.Length > 64) Fail(ArchivedResourceContextStatus.LimitExceeded);
            var current = sets.Where(e => OptionalUInt(e, "LegacyVersion", uint.MaxValue) is null or 0).Take(2).ToArray();
            if (current.Length != 1) return Error(current.Length == 0 ? ArchivedResourceContextStatus.MissingConfiguration : ArchivedResourceContextStatus.Ambiguous);
            var selected = current[0];
            Shape(selected, ["LegacyVersion"], ["Features", "Resources", "MemorySegments", "Procedures", "InterfaceObjects"]);
            var features = One(selected, ns + "Features", false);
            byte? first = null; bool? mapped = null;
            if (features is not null)
            {
                Shape(features, [], ["Feature"]);
                foreach (var featureName in new[] { "FirstAppObjectIdx", "PropertyMappedLsms" })
                {
                    var found = features.Elements(ns + "Feature").Where(e => (string?)e.Attribute("Name") == featureName).Take(2).ToArray();
                    if (found.Length > 1) Fail(ArchivedResourceContextStatus.Ambiguous);
                    if (found.Length == 0) continue;
                    Shape(found[0], ["Name", "Value"], []);
                    var value = RequiredUInt(found[0], "Value", featureName == "FirstAppObjectIdx" ? (uint)byte.MaxValue : 1u);
                    if (featureName == "FirstAppObjectIdx") first = (byte)value; else mapped = value == 1;
                }
            }
            var group = One(selected, ns + "Resources", false);
            if (group is not null) Shape(group, [], ["Resource"]);
            var rawResources = group?.Elements().Take(MaxResources + 1).ToArray() ?? [];
            if (rawResources.Length > MaxResources) Fail(ArchivedResourceContextStatus.LimitExceeded);
            var resources = new List<ArchivedResourceDescriptor>(rawResources.Length);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in rawResources)
            {
                Shape(raw, ["Name", "Access", "MgmtStyle", "Optional"], ["Location", "ImgLocation", "ResourceType", "AccessRights"]);
                var name = RequiredName(raw, "Name");
                var access = Tokens(raw, "Access", true, ["remote", "local1", "local2"]);
                var styles = Tokens(raw, "MgmtStyle", false, ["simple", "lsm"]);
                var location = ReadLocation(One(raw, ns + "Location", false));
                var image = ReadLocation(One(raw, ns + "ImgLocation", false));
                var type = One(raw, ns + "ResourceType", false);
                uint? length = null; string? flavour = null;
                if (type is not null)
                {
                    Shape(type, ["Length", "Flavour"], []);
                    length = OptionalUInt(type, "Length", uint.MaxValue);
                    flavour = OptionalName(type, "Flavour");
                }
                CheckEnd(location, length); CheckEnd(image, length);
                var (read, write) = Rights(One(raw, ns + "AccessRights", false));
                var descriptor = new ArchivedResourceDescriptor(name, access, styles, location, image, length, flavour,
                    OptionalBool(raw, "Optional"), read, write);
                if (!keys.Add(ResourceKey(descriptor))) Fail(ArchivedResourceContextStatus.Ambiguous);
                resources.Add(descriptor);
            }
            var regionGroup = One(selected, ns + "MemorySegments", false);
            if (regionGroup is not null) Shape(regionGroup, [], ["MemorySegment"]);
            var rawRegions = regionGroup?.Elements().Take(MaxRegions + 1).ToArray() ?? [];
            if (rawRegions.Length > MaxRegions) Fail(ArchivedResourceContextStatus.LimitExceeded);
            var regions = new List<ArchivedMemoryRegion>(rawRegions.Length);
            foreach (var raw in rawRegions)
            {
                Shape(raw, ["MemoryType", "Length", "Optional"], ["Location", "AccessRights"]);
                var location = ReadLocation(One(raw, ns + "Location", true))!;
                if (location.AddressSpace is not (ArchivedResourceAddressSpace.StandardMemory or ArchivedResourceAddressSpace.RelativeMemory))
                    Fail(ArchivedResourceContextStatus.InvalidDeclaration);
                var length = RequiredUInt(raw, "Length", uint.MaxValue);
                CheckEnd(location, length);
                var (read, write) = Rights(One(raw, ns + "AccessRights", false));
                var region = new ArchivedMemoryRegion(RequiredName(raw, "MemoryType"), location, length, OptionalBool(raw, "Optional"), read, write);
                // Regions describe one arena, so conflicting geometry cannot be resolved by source order or Optional.
                var start = location.Offset!.Value;
                foreach (var previous in regions)
                    if (SameArena(location, previous.Location) && start < (ulong)previous.Location.Offset!.Value + previous.Length
                        && previous.Location.Offset!.Value < (ulong)start + length)
                        Fail(ArchivedResourceContextStatus.Ambiguous);
                if (regions.Contains(region)) Fail(ArchivedResourceContextStatus.Ambiguous);
                regions.Add(region);
            }
            return new(ArchivedResourceContextStatus.Supported, id, provenance, document, numeric, first, mapped, resources.ToArray(), regions.ToArray());
        }
        catch (DeclarationException error) { return Error(error.Status); }
    }

    /// <summary>Finds one exact remote declaration. It does not dereference a pointer or resolve an image/resource address.</summary>
    public ArchivedResourceResolution ResolveRemote(string exactName, string? managementStyle = null)
    {
        if (Status != ArchivedResourceContextStatus.Supported) return new(ArchivedResourceResolutionStatus.ContextUnavailable);
        if (!SafeName(exactName) || managementStyle is not (null or "simple" or "lsm")) return new(ArchivedResourceResolutionStatus.InvalidRequest);
        if (!_byName.TryGetValue(exactName, out var named)) return new(ArchivedResourceResolutionStatus.Missing);
        var remote = named.Where(r => r.Access.Contains("remote", StringComparer.Ordinal)).ToArray();
        if (managementStyle is null && remote.Any(r => r.ManagementStyles.Count > 0)) return new(ArchivedResourceResolutionStatus.ManagementStyleRequired);
        var matches = remote.Where(r => r.ManagementStyles.Count == 0 || r.ManagementStyles.Contains(managementStyle!, StringComparer.Ordinal)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => new(ArchivedResourceResolutionStatus.Missing),
            1 => new(ArchivedResourceResolutionStatus.Found, matches[0]),
            _ => new(ArchivedResourceResolutionStatus.Ambiguous),
        };
    }

    private static string ResourceKey(ArchivedResourceDescriptor resource) => resource.Name + "|" + string.Join(' ', resource.Access) + "|" + string.Join(' ', resource.ManagementStyles);
    private static bool SameArena(ArchivedResourceLocation a, ArchivedResourceLocation b) => (a with { Offset = null }) == (b with { Offset = null });
    private static void CheckEnd(ArchivedResourceLocation? location, uint? length)
    {
        if (location?.Offset is { } start && length is { } size && (ulong)start + size > (ulong)uint.MaxValue + 1)
            Fail(ArchivedResourceContextStatus.InvalidDeclaration);
    }
    private static ArchivedResourceLocation? ReadLocation(XElement? element)
    {
        if (element is null) return null;
        var space = (string?)element.Attribute("AddressSpace");
        var allowed = space switch
        {
            "StandardMemory" or "Constant" or "ADC" => new[] { "AddressSpace", "StartAddress" },
            "RelativeMemory" => ["AddressSpace", "StartAddress", "InterfaceObjectRef", "PropertyID"],
            "SystemProperty" or "AppProperty" => ["AddressSpace", "StartAddress", "InterfaceObjectRef", "PropertyID"],
            "Property" => ["AddressSpace", "StartAddress", "InterfaceObjectRef", "Occurrence", "PropertyID"],
            "Pointer" => ["AddressSpace", "PtrResource"],
            "None" => ["AddressSpace"],
            _ => throw new DeclarationException(ArchivedResourceContextStatus.InvalidDeclaration),
        };
        Shape(element, allowed, []);
        if (space == "None") return new(ArchivedResourceAddressSpace.None);
        if (space == "Pointer") return new(ArchivedResourceAddressSpace.Pointer, PointerResource: RequiredName(element, "PtrResource"));
        var offset = RequiredUInt(element, "StartAddress", uint.MaxValue);
        if (space is "StandardMemory" or "Constant" or "ADC")
            return new(space == "StandardMemory" ? ArchivedResourceAddressSpace.StandardMemory : space == "Constant" ? ArchivedResourceAddressSpace.Constant : ArchivedResourceAddressSpace.Adc, offset);
        var reference = (ushort)RequiredUInt(element, "InterfaceObjectRef", ushort.MaxValue);
        var property = space == "RelativeMemory" ? OptionalUInt(element, "PropertyID", ushort.MaxValue) : RequiredUInt(element, "PropertyID", ushort.MaxValue);
        var occurrence = OptionalUInt(element, "Occurrence", ushort.MaxValue);
        return new(space switch
        {
            "RelativeMemory" => ArchivedResourceAddressSpace.RelativeMemory,
            "SystemProperty" => ArchivedResourceAddressSpace.SystemProperty,
            "AppProperty" => ArchivedResourceAddressSpace.AppProperty,
            _ => ArchivedResourceAddressSpace.PropertyByType,
        }, offset, NativeObjectIndex: space == "SystemProperty" ? reference : null,
            ApplicationObjectIndex: space == "AppProperty" ? reference : null, ObjectType: space == "Property" ? reference : null,
            Occurrence: occurrence is { } value ? (ushort)value : null, PropertyId: property is { } pid ? (ushort)pid : null, InterfaceObjectRef: reference);
    }

    private static XElement? One(XElement parent, XName name, bool required)
    {
        var values = parent.Elements(name).Take(2).ToArray();
        if (values.Length > 1) Fail(ArchivedResourceContextStatus.Ambiguous);
        if (required && values.Length == 0) Fail(ArchivedResourceContextStatus.InvalidDeclaration);
        return values.FirstOrDefault();
    }
    private static void Shape(XElement element, string[] attributes, string[] children)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.NamespaceName.Length != 0 || !attributes.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            || element.Elements().Any(c => c.Name.Namespace != element.Name.Namespace || !children.Contains(c.Name.LocalName, StringComparer.Ordinal))
            || element.Nodes().Any(n => n is XProcessingInstruction || n is XText text && !string.IsNullOrWhiteSpace(text.Value)))
            Fail(ArchivedResourceContextStatus.InvalidDeclaration);
    }
    private static (string? Read, string? Write) Rights(XElement? element)
    {
        if (element is null) return (null, null);
        Shape(element, ["Read", "Write"], []);
        string? Get(string name)
        {
            var value = (string?)element.Attribute(name);
            if (value is not (null or "None" or "Runtime" or "Configuration" or "Manufacturer")) Fail(ArchivedResourceContextStatus.InvalidDeclaration);
            return value;
        }
        return (Get("Read"), Get("Write"));
    }
    private static string[] Tokens(XElement element, string name, bool required, string[] allowed)
    {
        var text = (string?)element.Attribute(name);
        if (text is null && !required) return [];
        if (text is null || text.Length > 256) throw new DeclarationException(ArchivedResourceContextStatus.InvalidDeclaration);
        var tokens = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > MaxTokens || tokens.Any(t => !allowed.Contains(t, StringComparer.Ordinal)) || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
            Fail(ArchivedResourceContextStatus.InvalidDeclaration);
        Array.Sort(tokens, StringComparer.Ordinal); return tokens;
    }
    private static string RequiredName(XElement element, string name) => OptionalName(element, name) ?? throw new DeclarationException(ArchivedResourceContextStatus.InvalidDeclaration);
    private static string? OptionalName(XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        if (value is not null && !SafeName(value)) Fail(ArchivedResourceContextStatus.InvalidDeclaration);
        return value;
    }
    private static bool SafeName(string? value) => value is { Length: > 0 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static uint RequiredUInt(XElement element, string name, uint maximum) => OptionalUInt(element, name, maximum) ?? throw new DeclarationException(ArchivedResourceContextStatus.InvalidDeclaration);
    private static uint? OptionalUInt(XElement element, string name, uint maximum)
    {
        var text = (string?)element.Attribute(name);
        if (text is null) return null;
        if (text.Length is 0 or > 10 || text.Any(c => c is < '0' or > '9')
            || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > maximum)
            throw new DeclarationException(ArchivedResourceContextStatus.InvalidDeclaration);
        return value;
    }
    private static bool? OptionalBool(XElement element, string name) => (string?)element.Attribute(name) switch
    {
        null => null, "true" or "1" => true, "false" or "0" => false,
        _ => throw new DeclarationException(ArchivedResourceContextStatus.InvalidDeclaration),
    };
    private static bool SupportedNamespace(string? value) => value is "http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23";
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private sealed class DeclarationException(ArchivedResourceContextStatus status) : Exception { internal ArchivedResourceContextStatus Status { get; } = status; }
    private static void Fail(ArchivedResourceContextStatus status) => throw new DeclarationException(status);

    private static bool WithinBudget(XDocument document)
    {
        if (document.DocumentType is not null || document.Root is null) return false;
        var pending = new Stack<(XNode Node, int Depth)>();
        foreach (var node in document.Nodes()) pending.Push((node, 1));
        var count = 0; long chars = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > 200000 || item.Depth > 128) return false;
            if (item.Node is XElement element)
            {
                chars += element.Name.ToString().Length;
                foreach (var attribute in element.Attributes())
                {
                    if (++count > 200000) return false;
                    chars += attribute.Name.ToString().Length + attribute.Value.Length;
                }
                foreach (var child in element.Nodes())
                {
                    if (count + pending.Count >= 200000) return false;
                    pending.Push((child, item.Depth + 1));
                }
            }
            else if (item.Node is XText text) chars += text.Value.Length;
            else if (item.Node is XComment comment) chars += comment.Value.Length;
            else if (item.Node is XProcessingInstruction instruction) chars += instruction.Target.Length + instruction.Data.Length;
            if (chars > 16 * 1024 * 1024) return false;
        }
        return true;
    }
}
