using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;

namespace Knx;

public enum ParameterStorageKind { AbsoluteMemory, RelativeMemory, PropertyByIndex, PropertyByType }
public enum ParameterStorageStatus { NotDeclared, Supported, Unsupported, InvalidDeclaration, MissingArgument, UnknownSegment, OutOfRange }
public enum ParameterFootprintStatus { Known, Unknown, OutOfRange }
public enum ParameterResourceCoverage { Unknown, Covered, Uncovered }

/// <summary>Declared CodeSegment geometry only. Size null means unknown/malformed, while zero remains zero.</summary>
public sealed record ParameterStorageResource(string Id, ParameterStorageKind Kind, uint StartAddress,
    uint? DeclaredSize, ushort? LoadStateMachine = null);

/// <summary>Fixed type width and spanned byte positions, not encoded payload bytes, patch activity or write eligibility.</summary>
public sealed record ParameterStorageFootprint(ParameterFootprintStatus Status, uint? BitLength = null,
    uint? ByteLength = null, ParameterResourceCoverage Coverage = ParameterResourceCoverage.Unknown);

/// <summary>
/// Read-only effective storage diagnosis, not an image or device readiness claim. StartAddress is
/// an absolute memory address, a relative load-state-machine offset, or a property byte offset.
/// PropertyByIndex retains the XML-native object index before mask-dependent FirstAppObject rebasing.
/// Missing fields remain null; a supported address/offset of zero is distinct from NotDeclared.
/// </summary>
public sealed record ParameterStorageLocation(ParameterStorageStatus Status, ParameterStorageKind? Kind = null,
    string? CodeSegmentId = null, uint? StartAddress = null, byte? BitOffset = null, ushort? LoadStateMachine = null,
    byte? ObjectIndex = null, ushort? ObjectType = null, byte? Occurrence = null, byte? PropertyId = null,
    string? ArgumentId = null, string? ModuleInstanceId = null, ParameterStorageResource? Resource = null,
    ParameterStorageFootprint? Footprint = null, ParameterPropertyProfile? PropertyProfile = null);

/// <summary>One immutable immediate module scope; native serialized arguments remain untouched.</summary>
internal sealed record ParameterStorageArguments(IReadOnlyDictionary<string, long> Values, string ModuleInstanceId)
{
    internal static ParameterStorageArguments Create(IReadOnlyDictionary<string, string> values, Func<string, string> scope, string instance)
    {
        var arguments = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (id, value) in values)
            if (!arguments.TryAdd(scope(id), long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Ambiguous storage argument scope.");
        return new(new ReadOnlyDictionary<string, long>(arguments), instance);
    }
}

/// <summary>One segment index per expanded parse; location reading neither edits XML nor evaluates manufacturer code.</summary>
internal sealed class ParameterStorageReader
{
    private readonly Dictionary<string, XElement?> _segments;
    private readonly Dictionary<string, ParameterStorageResource?> _resources;
    private readonly ParameterProfileContext _profile;
    private readonly string? _applicationMask;
    internal ParameterStorageReader(XElement root, ParameterProfileContext? profile = null)
    {
        var applications = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        _applicationMask = applications.Length == 1 ? (string?)applications[0].Attribute("MaskVersion") : null;
        _profile = (profile ?? ParameterProfileContext.Read(null, _applicationMask)).ForApplication(_applicationMask,
            applications.Length == 1 ? applications[0].Name.NamespaceName : null, applications.Length > 1);
        _segments = root.DescendantsAndSelf().Where(e => e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment"
            && e.Parent?.Name.LocalName == "Code" && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef"))
            .Where(e => e.Attribute("Id") is not null).GroupBy(e => (string)e.Attribute("Id")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single() : null, StringComparer.Ordinal);
        _resources = _segments.ToDictionary(p => p.Key, p => Resource(p.Key, p.Value), StringComparer.Ordinal);
    }

    internal ParameterStorageLocation Read(XElement parameter, XElement? reference, ParameterType? type)
    {
        var location = ReadLocation(parameter, reference);
        if (location.Status == ParameterStorageStatus.NotDeclared) return location;
        var resource = location.Status == ParameterStorageStatus.Supported && location.CodeSegmentId is { } id
            ? _resources.GetValueOrDefault(id) : null;
        var property = location.Status == ParameterStorageStatus.Supported
            && location.Kind is ParameterStorageKind.PropertyByIndex or ParameterStorageKind.PropertyByType
            ? _profile.Interpret(location, _applicationMask) : null;
        return location with { Resource = resource, Footprint = Footprint(location, resource, type), PropertyProfile = property };
    }

    private ParameterStorageLocation ReadLocation(XElement parameter, XElement? reference)
    {
        try
        {
            if (reference?.Elements().Any(e => e.Name.LocalName is "Memory" or "Property") == true)
                return new(ParameterStorageStatus.Unsupported);
            var union = parameter.Parent?.Name.LocalName == "Union" ? parameter.Parent : null;
            var owner = union ?? parameter;
            var locations = owner.Elements().Where(e => e.Name.LocalName is "Memory" or "Property").ToArray();
            if (locations.Length == 0)
                return new(owner.HasElements && union is null ? ParameterStorageStatus.Unsupported : ParameterStorageStatus.NotDeclared);
            if (locations.Length != 1 || union is not null && parameter.HasElements)
                return new(ParameterStorageStatus.InvalidDeclaration);
            if (owner.Elements().Any(e => e != locations[0] && !(union is not null && e.Name.LocalName == "Parameter"))
                || union is not null && new[] { "BaseOffset", "BaseIndex", "BaseOccurrence" }.Any(name => parameter.Attribute(name) is not null))
                return new(ParameterStorageStatus.Unsupported);
            var location = locations[0];
            if (location.HasElements || location.Name.Namespace != parameter.Name.Namespace)
                return new(ParameterStorageStatus.Unsupported);
            var memory = location.Name.LocalName == "Memory";
            var allowed = memory ? new[] { "CodeSegment", "Offset", "BitOffset", "BaseOffset" }
                : ["ObjectIndex", "ObjectType", "Occurrence", "PropertyId", "Offset", "BitOffset", "BaseOffset", "BaseIndex", "BaseOccurrence"];
            if (location.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.NamespaceName.Length != 0 || !allowed.Contains(a.Name.LocalName))))
                return new(ParameterStorageStatus.Unsupported);
            var offset = Literal(location, "Offset", memory ? 1_048_575 : uint.MaxValue);
            var bit = Literal(location, "BitOffset", 7);
            if (union is not null)
            {
                offset = checked(offset + Literal(parameter, "Offset", 1_048_575));
                bit += Literal(parameter, "BitOffset", 7);
                offset = checked(offset + bit / 8); bit %= 8;
            }
            offset = AddBase(location, "BaseOffset", offset);
            if (memory)
            {
                var segmentId = (string?)location.Attribute("CodeSegment");
                if (string.IsNullOrEmpty(segmentId)) return new(ParameterStorageStatus.InvalidDeclaration);
                if (!_segments.TryGetValue(segmentId, out var segment) || segment is null || segment.Name.Namespace != parameter.Name.Namespace)
                    return new(ParameterStorageStatus.UnknownSegment, CodeSegmentId: segmentId);
                if (segment.Name.LocalName == "AbsoluteSegment")
                {
                    if (segment.Attribute("UserMemory") is { } userMemory)
                    {
                        if (userMemory.Value is not ("false" or "0")) return new(ParameterStorageStatus.Unsupported, CodeSegmentId: segmentId);
                    }
                    var address = Range(checked(Literal(segment, "Address", 1_048_575) + offset), uint.MaxValue);
                    return new(ParameterStorageStatus.Supported, ParameterStorageKind.AbsoluteMemory, segmentId, (uint)address, (byte)bit);
                }
                var lsm = Literal(segment, "LoadStateMachine", byte.MaxValue);
                var relative = Range(checked(Literal(segment, "Offset", 1_048_575) + offset), uint.MaxValue);
                return new(ParameterStorageStatus.Supported, ParameterStorageKind.RelativeMemory, segmentId, (uint)relative, (byte)bit, (ushort)lsm);
            }
            var index = location.Attribute("ObjectIndex"); var type = location.Attribute("ObjectType");
            if ((index is null) == (type is null)) return new(ParameterStorageStatus.InvalidDeclaration);
            var property = Literal(location, "PropertyId", byte.MaxValue);
            if (index is not null)
            {
                if (location.Attribute("BaseOccurrence") is not null
                    || location.Attribute("Occurrence") is not null && Literal(location, "Occurrence", byte.MaxValue) != 0)
                    return new(ParameterStorageStatus.Unsupported);
                var effective = Range(AddBase(location, "BaseIndex", Literal(location, "ObjectIndex", byte.MaxValue)), byte.MaxValue);
                return new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByIndex, StartAddress: (uint)offset,
                    BitOffset: (byte)bit, ObjectIndex: (byte)effective, PropertyId: (byte)property);
            }
            if (location.Attribute("BaseIndex") is not null) return new(ParameterStorageStatus.Unsupported);
            var objectType = Literal(location, "ObjectType", ushort.MaxValue);
            var occurrence = Range(AddBase(location, "BaseOccurrence", location.Attribute("Occurrence") is null
                ? 0 : Literal(location, "Occurrence", byte.MaxValue)), byte.MaxValue);
            return new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByType, StartAddress: (uint)offset,
                BitOffset: (byte)bit, ObjectType: (ushort)objectType, Occurrence: (byte)occurrence, PropertyId: (byte)property);
        }
        catch (StorageError error) { return error.Location; }
        catch (OverflowException) { return new(ParameterStorageStatus.OutOfRange); }
    }

    private static ParameterStorageResource? Resource(string id, XElement? segment)
    {
        if (segment is null) return null;
        try
        {
            var size = (string?)segment.Attribute("Size");
            uint? declaredSize = decimal.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value is >= 0 and <= 1_048_575 ? (uint)value : null;
            if (segment.Name.LocalName == "AbsoluteSegment")
            {
                if (segment.Attribute("UserMemory") is { Value: not ("false" or "0") }) return null;
                return new(id, ParameterStorageKind.AbsoluteMemory, (uint)Literal(segment, "Address", 1_048_575), declaredSize);
            }
            return new(id, ParameterStorageKind.RelativeMemory, (uint)Literal(segment, "Offset", 1_048_575), declaredSize,
                (ushort)Literal(segment, "LoadStateMachine", byte.MaxValue));
        }
        catch (StorageError) { return null; }
    }

    private static ParameterStorageFootprint Footprint(ParameterStorageLocation location, ParameterStorageResource? resource, ParameterType? type)
    {
        if (location.Status != ParameterStorageStatus.Supported || location.StartAddress is not { } start || location.BitOffset is not { } bit
            || type is null || type.UnsupportedReason is not null) return new(ParameterFootprintStatus.Unknown);
        // Only supported fixed-width types establish a byte footprint. Text,
        // RawData capacities and other encodings do not establish a formatted byte footprint.
        var bits = type.DataType switch
        {
            ParameterDataType.Number when type.NumberType is "unsignedInt" or "signedInt" => type.SizeInBit,
            ParameterDataType.Enumeration => type.SizeInBit,
            ParameterDataType.Boolean when type.SizeInBit == 1 => 1,
            ParameterDataType.Float when type.Encoding == "DPT 9" && type.SizeInBit == 16 => 16,
            ParameterDataType.Float when type.Encoding == "IEEE-754 Single" && type.SizeInBit == 32 => 32,
            ParameterDataType.Float when type.Encoding == "IEEE-754 Double" && type.SizeInBit == 64 => 64,
            ParameterDataType.Time when ParameterValidation.TimeDeclarationError(type) is null => type.SizeInBit,
            ParameterDataType.IPAddress when type.AddressType == "HostAddress" && type.AddressVersion == "IPv4" && type.SizeInBit is 0 or 32 => 32,
            ParameterDataType.IPAddress when type.AddressType == "HostAddress" && type.AddressVersion == "IPv6" && type.SizeInBit is 0 or 128 => 128,
            _ => 0,
        };
        if (bits <= 0) return new(ParameterFootprintStatus.Unknown);
        // Native TypeNumber is 1..32 bits in /20,/21,/23. Enumeration's separate
        // diagnostic profile remains bounded to 64; its BinaryValue forms are not numeric codecs.
        if (type.DataType == ParameterDataType.Number && bits > 32 || type.DataType == ParameterDataType.Enumeration && bits > 64)
            return new(ParameterFootprintStatus.OutOfRange);
        var bytes = ((ulong)bit + (uint)bits + 7) / 8;
        if (bytes > uint.MaxValue) return new(ParameterFootprintStatus.OutOfRange);
        var coverage = ParameterResourceCoverage.Unknown;
        if (resource?.DeclaredSize is { } size)
        {
            // One-past-end arithmetic intentionally uses ulong. Declared coverage is geometry,
            // not an image allocation rule, segment extension policy or a physical write gate.
            var end = (ulong)start + bytes;
            var resourceEnd = (ulong)resource.StartAddress + size;
            coverage = start >= resource.StartAddress && end <= resourceEnd
                ? ParameterResourceCoverage.Covered : ParameterResourceCoverage.Uncovered;
        }
        return new(ParameterFootprintStatus.Known, (uint)bits, (uint)bytes, coverage);
    }

    private static long Literal(XElement element, string name, long maximum)
    {
        var raw = (string?)element.Attribute(name);
        if (raw is null || !decimal.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new StorageError(new(ParameterStorageStatus.InvalidDeclaration));
        if (value < 0 || value > maximum) throw new StorageError(new(ParameterStorageStatus.OutOfRange));
        return (long)value;
    }
    private static long Range(long value, long maximum)
    {
        if (value < 0 || value > maximum) throw new StorageError(new(ParameterStorageStatus.OutOfRange));
        return value;
    }
    private static long AddBase(XElement location, string name, long value)
    {
        if (location.Attribute(name) is not { } basis) return Range(value, uint.MaxValue);
        var arguments = location.Annotation<ParameterStorageArguments>();
        if (arguments is null || !arguments.Values.TryGetValue(basis.Value, out var increment))
            throw new StorageError(new(ParameterStorageStatus.MissingArgument, ArgumentId: basis.Value, ModuleInstanceId: arguments?.ModuleInstanceId));
        return Range(checked(value + increment), uint.MaxValue);
    }
    private sealed class StorageError(ParameterStorageLocation location) : Exception
    { internal ParameterStorageLocation Location { get; } = location; }
}
