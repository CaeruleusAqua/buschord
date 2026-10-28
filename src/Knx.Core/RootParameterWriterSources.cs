using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed class RootBuildRejected(RootImageBuildIssue issue) : Exception
{ internal RootImageBuildIssue Issue => issue; }
internal static class RootBuildGuard
{
    internal const int Limit = 100000, Blob = 1_048_576, Total = 4 * Blob, TextLimit = 16 * Blob;
    [DoesNotReturn]
    internal static void Fail(RootImageBuildIssueCode code, string? reference = null, string? resource = null,
        ParameterPatchIssue? parameter = null, GeneratedTableIssue? table = null, LoadProcedureIssue? procedure = null)
        => throw new RootBuildRejected(new(code, Safe(reference) ? reference : null, Safe(resource) ? resource : null, parameter, table, procedure));
    internal static bool Safe(string? value) => value is { Length: > 0 and <= 256 }
        && !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || c > 127);
    internal static string Id(XElement element, string name = "Id")
    { var value = (string?)element.Attribute(name); if (!Safe(value)) Fail(RootImageBuildIssueCode.InvalidDeclaration); return value!; }
    internal static bool Bool(XElement element, string name)
        => (string?)element.Attribute(name) switch
        { null or "false" or "0" => false, "true" or "1" => true, _ => throw new RootBuildRejected(new(RootImageBuildIssueCode.InvalidDeclaration)) };
    internal static string Hash<T>(T value) => ParameterPatchSourceBinding.Hash(value);
    internal static uint UInt(XElement element, string name, uint maximum)
    {
        if (!uint.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value > maximum)
            Fail(RootImageBuildIssueCode.InvalidDeclaration);
        return value;
    }
    internal static void Leaf(XElement element)
    { if (element.HasElements || !string.IsNullOrWhiteSpace(element.Value)) Fail(RootImageBuildIssueCode.InvalidDeclaration); }
    internal static XElement? One(XElement element, XName name, bool required = false)
    {
        var matches = element.Elements(name).Take(2).ToArray();
        if (matches.Length > 1 || required && matches.Length != 1) Fail(RootImageBuildIssueCode.InvalidDeclaration);
        return matches.SingleOrDefault();
    }
    internal static List<XElement> Nodes(XDocument document, int textLimit)
    {
        if (document.Root is null || document.DocumentType is not null) Fail(RootImageBuildIssueCode.InvalidDeclaration);
        var result = new List<XElement>(); var stack = new Stack<(XNode Node, int Depth)>(); stack.Push((document, 0)); long text = 0; var count = 0;
        while (stack.TryPop(out var item))
        {
            if (++count > 500000 || item.Depth > 128) Fail(RootImageBuildIssueCode.LimitExceeded);
            if (item.Node is XContainer container)
                foreach (var child in container.Nodes())
                { if (stack.Count >= 500000) Fail(RootImageBuildIssueCode.LimitExceeded); stack.Push((child, item.Depth + 1)); }
            if (item.Node is XElement element)
            {
                result.Add(element); text += element.Name.ToString().Length;
                foreach (var attribute in element.Attributes()) text += attribute.Name.ToString().Length + attribute.Value.Length;
            }
            else if (item.Node is XText value) text += value.Value.Length;
            else if (item.Node is XComment comment) text += comment.Value.Length;
            else if (item.Node is XProcessingInstruction instruction) text += instruction.Target.Length + instruction.Data.Length;
            if (text > textLimit) Fail(RootImageBuildIssueCode.LimitExceeded);
        }
        return result;
    }
}

internal sealed record RootParameterSource(XElement Parameter, XElement Reference, XElement Type, ParameterType ParsedType,
    ParameterStorageLocation? Location, string? ParameterValue, string? ReferenceValue, bool Always, bool Customer);
internal sealed record RootMemoryOwner(string ParameterId, string ResourceId, ResourcePatchArena Arena, uint Offset, byte BitOffset, uint Bits, bool Referenced)
{
    internal ulong Start => (ulong)Offset * 8 + BitOffset;
    internal ulong End => Start + Bits;
}

internal sealed class RootParameterWriterSources
{
    internal XElement Application { get; }
    internal IReadOnlyList<RootParameterSource> References { get; }
    internal IReadOnlyList<RootMemoryOwner> Owners { get; }
    internal IReadOnlyList<ImageCompositionSourceSegment> Segments { get; }
    internal IReadOnlyDictionary<string, string> Saved { get; }
    internal RootImageBuildPolicy Policy { get; }
    internal string CarrierHash { get; }
    internal int ParameterCount { get; }
    internal string SourceHash { get; }
    private readonly Dictionary<string, string> _referenceIds;
    private readonly Dictionary<string, RootParameterSource> _sourceReferences;

    internal RootParameterWriterSources(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
    {
        var document = inputs.Application;
        if (inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || inputs.Manifest.Binding is null || document?.Root is null
            || inputs.Manifest.Hardware?.Status != ArchivedHardwareSourceStatus.Inspected)
            RootBuildGuard.Fail(RootImageBuildIssueCode.SourceUnavailable);
        if (inputs.Manifest.Hardware.LoadedImageDeclared) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedProfile);
        var root = document!.Root!; var ns = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedApplication);
        var nodes = RootBuildGuard.Nodes(document, RootBuildGuard.TextLimit);
        if (nodes.Any(e => e.Name.Namespace != ns || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml)))
            RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        var apps = nodes.Where(e => e.Name == ns + "ApplicationProgram").Take(2).ToArray();
        if (apps.Length != 1 || (string?)apps[0].Attribute("Id") != inputs.Manifest.ApplicationId)
            RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        Application = apps[0]; var appId = RootBuildGuard.Id(Application);
        if (appId.Any(c => c > 127)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        if (nodes.Any(e => e.Name.LocalName is "Module" or "Repeat" or "Union" or "Allocator" or "Assign" or "ParameterCalculation" or "ParameterInstance"
            || e.Attributes().Any(a => a.Name.LocalName is "BaseValue" or "AllocatorRefId")))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedProfile);
        var stat = RootBuildGuard.One(Application, ns + "Static", true)!;
        var parameterContainer = RootBuildGuard.One(stat, ns + "Parameters");
        var referenceContainer = RootBuildGuard.One(stat, ns + "ParameterRefs");
        var typeContainer = RootBuildGuard.One(stat, ns + "ParameterTypes");
        var parameters = Index(parameterContainer, ns + "Parameter"); var references = Index(referenceContainer, ns + "ParameterRef");
        var types = Index(typeContainer, ns + "ParameterType"); ParameterCount = parameters.Count;
        var known = parameters.Values.Concat(references.Values).Concat(types.Values).ToHashSet();
        if (nodes.Any(e => e.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !known.Contains(e)))
            RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        if (nodes.Any(e => e.Name.LocalName == "Options" && !ReferenceEquals(e.Parent, stat))) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        Policy = ReadPolicy(Application, RootBuildGuard.One(stat, ns + "Options"), appId);
        var parsedTypes = new Dictionary<string, ParameterType>(StringComparer.Ordinal); var choices = 0;
        foreach (var (id, type) in types)
        {
            var shapes = type.Elements().ToArray();
            if (shapes.Length != 1 || shapes[0].Name.Namespace != ns) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
            if (shapes[0].Name.LocalName == "TypeRawData") RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedType);
            if (shapes[0].Name.LocalName == "TypeRestriction")
            {
                var count = shapes[0].Elements().Take(4097).Count(); choices = checked(choices + count);
                if (count > 4096 || choices > 65536) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
                foreach (var choice in shapes[0].Elements()) _ = RootBuildGuard.Id(choice);
            }
            else RootBuildGuard.Leaf(shapes[0]);
            if (shapes[0].Name.LocalName is "TypeRestriction" or "TypeFloat" or "TypeTime"
                && OfflineParameterPatch.PreflightWordDeclaration(shapes[0]) is { } issue)
                RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedType, parameter: issue);
            var parsed = Ksxml.ParseParameterType(type);
            if (parsed is null || parsed.UnsupportedReason is not null || parsed.DataType == ParameterDataType.None)
                RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedType);
            parsedTypes.Add(id, parsed!);
        }
        var code = RootBuildGuard.One(stat, ns + "Code");
        var knownSegments = code?.Elements().ToHashSet() ?? [];
        if (nodes.Any(e => e.Name.LocalName == "Code" && !ReferenceEquals(e, code)
            || e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment" && !knownSegments.Contains(e)))
            RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        var segments = ReadSegments(stat, ns); Segments = Array.AsReadOnly(segments);
        var segmentMap = segments.ToDictionary(s => s.SourceId, StringComparer.Ordinal);
        var storage = new ParameterStorageReader(root); var owners = new List<RootMemoryOwner>();
        var parameterLocations = new Dictionary<string, ParameterStorageLocation?>(StringComparer.Ordinal);
        var aliasCounts = references.Values.GroupBy(r => RootBuildGuard.Id(r, "RefId"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (id, parameter) in parameters)
        {
            if (((string?)parameter.Attribute("Value"))?.Length > 65536) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded, id);
            var typeId = RootBuildGuard.Id(parameter, "ParameterType");
            ParameterType? type = null;
            if (parameter.Attribute("ParameterTypeId") is not null || !parsedTypes.TryGetValue(typeId, out type)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration, id);
            if (parameter.Elements().Any(e => e.Name != ns + "Memory") || parameter.Elements().Count() > 1)
                RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedProfile, id);
            var memory = parameter.Element(ns + "Memory");
            if (memory is null) { parameterLocations.Add(id, null); continue; }
            RootBuildGuard.Leaf(memory);
            if (memory.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("CodeSegment" or "Offset" or "BitOffset"))))
                RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedProfile, id);
            var location = storage.Read(parameter, null, type);
            if (location is not { Status: ParameterStorageStatus.Supported, CodeSegmentId: not null, StartAddress: not null, BitOffset: not null,
                Footprint: { Status: ParameterFootprintStatus.Known, BitLength: > 0 } }
                || location.Kind is not (ParameterStorageKind.AbsoluteMemory or ParameterStorageKind.RelativeMemory))
                RootBuildGuard.Fail(RootImageBuildIssueCode.UnknownStorage, id);
            var arena = Arena(location); var width = location.Footprint!.BitLength!.Value;
            if (!segmentMap.TryGetValue(location.CodeSegmentId!, out var segment) || segment.Arena != arena) RootBuildGuard.Fail(RootImageBuildIssueCode.MissingResource, id);
            var start = location.StartAddress!.Value; var byteSpan = ((ulong)location.BitOffset!.Value + width + 7) / 8;
            if (start < segment!.ByteOffsetInArena || (ulong)start + byteSpan > (ulong)segment.ByteOffsetInArena + (uint)segment.AllocationLength)
                RootBuildGuard.Fail(RootImageBuildIssueCode.FieldOutOfRange, id, segment.SourceId);
            if (aliasCounts.GetValueOrDefault(id) > 1) RootBuildGuard.Fail(RootImageBuildIssueCode.AmbiguousOwnership, id);
            var underlying = (string?)parameter.Attribute("Value");
            if (underlying is null || underlying.Length > 64 || underlying.Any(char.IsControl)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
            var encoding = OfflineParameterPatch.EncodeWord(types[typeId].Elements().Single(), type, underlying, Policy.ByteOrder, location.BitOffset.Value, id);
            if (encoding.Issue is not null) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedType, id, parameter: encoding.Issue);
            if (encoding.Bits != width) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration, id);
            parameterLocations.Add(id, location); owners.Add(new(id, segment.SourceId, arena, start, location.BitOffset.Value, width, aliasCounts.ContainsKey(id)));
        }
        foreach (var group in owners.GroupBy(o => o.Arena))
        {
            ulong end = 0; var first = true;
            foreach (var owner in group.OrderBy(o => o.Start).ThenBy(o => o.End))
            { if (!first && owner.Start < end) RootBuildGuard.Fail(RootImageBuildIssueCode.AmbiguousOwnership, owner.ParameterId); end = owner.End; first = false; }
        }
        Owners = Array.AsReadOnly(owners.ToArray());
        var result = new List<RootParameterSource>(); _referenceIds = new(StringComparer.Ordinal); _sourceReferences = new(StringComparer.Ordinal);
        foreach (var (id, reference) in references)
        {
            RootBuildGuard.Leaf(reference);
            if (((string?)reference.Attribute("Value"))?.Length > 65536) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded, id);
            var target = RootBuildGuard.Id(reference, "RefId");
            XElement? parameter = null;
            if (!parameters.TryGetValue(target, out parameter) || reference.Attribute("ParameterType") is not null || reference.Attribute("ParameterTypeId") is not null)
                RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration, id);
            _referenceIds.Add(id, id); var typeId = RootBuildGuard.Id(parameter!, "ParameterType");
            var source = new RootParameterSource(parameter!, reference, types[typeId], parsedTypes[typeId], parameterLocations[target],
                (string?)parameter!.Attribute("Value"), (string?)reference.Attribute("Value"),
                RootBuildGuard.Bool(parameter, "LegacyPatchAlways"), reference.Attribute("CustomerAdjustable") is null
                    ? RootBuildGuard.Bool(parameter, "CustomerAdjustable") : RootBuildGuard.Bool(reference, "CustomerAdjustable"));
            if (source.Location is not null && source.ReferenceValue is { } declared)
            {
                if (declared.Length > 64 || declared.Any(char.IsControl)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
                var encoded = OfflineParameterPatch.EncodeWord(source.Type.Elements().Single(), source.ParsedType, declared, Policy.ByteOrder, source.Location.BitOffset!.Value, id);
                if (encoded.Issue is not null) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id, parameter: encoded.Issue);
            }
            result.Add(source); _sourceReferences.Add(id, source);
        }
        References = Array.AsReadOnly(result.ToArray());
        Saved = ReadSaved(device, appId);
        long currentCharacters = 0;
        foreach (var source in result)
        {
            var reference = RootBuildGuard.Id(source.Reference);
            var current = Saved.TryGetValue(reference, out var saved) ? saved : source.ReferenceValue ?? source.ParameterValue;
            if ((currentCharacters += reference.Length + (current?.Length ?? 0)) > RootBuildGuard.TextLimit)
                RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
        }
        CarrierHash = CaptureCarriers(project, device, appId);
        SourceHash = RootBuildGuard.Hash(new { inputs.Manifest.ApplicationDefinitionHash,
            parameters = parameters.Keys.ToArray(), references = references.Keys.ToArray(), types = types.Keys.ToArray(), geometry = owners });
    }

    internal string Normalize(string key, string appId)
        => RootParameterWriterCommon.Normalize(_referenceIds, key, appId);
    internal bool MatchesSaved(IReadOnlyDictionary<string, string?> values, string appId)
        => RootParameterWriterCommon.MatchesSaved(_referenceIds, Saved, values, appId);
    private IReadOnlyDictionary<string, string> ReadSaved(DeviceInstance device, string appId)
        => RootParameterWriterCommon.ReadSaved(_referenceIds, _sourceReferences, device, appId, Policy);
    internal string CaptureCarriers(Project project, DeviceInstance device, string appId)
        => RootParameterWriterCommon.CaptureCarriers(_referenceIds, project, device, appId);
    private static Dictionary<string, XElement> Index(XElement? container, XName child)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in container?.Elements() ?? [])
            if (element.Name != child || result.Count >= RootBuildGuard.Limit || !result.TryAdd(RootBuildGuard.Id(element), element))
                RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        return result;
    }
    internal static ResourcePatchArena Arena(ParameterStorageLocation location)
        => location.Kind == ParameterStorageKind.AbsoluteMemory ? new(ResourcePatchArenaKind.AbsoluteMemory)
            : new(ResourcePatchArenaKind.RelativeMemory, checked((byte)location.LoadStateMachine!.Value));

    private static RootImageBuildPolicy ReadPolicy(XElement app, XElement? options, string id)
        => RootParameterWriterCommon.ReadPolicy(app, options, id);

    private static ImageCompositionSourceSegment[] ReadSegments(XElement stat, XNamespace ns)
    {
        var code = RootBuildGuard.One(stat, ns + "Code");
        var plans = new List<(string Id, ResourcePatchArena Arena, uint Offset, uint Size, string? Data, string? Mask)>();
        long raw = 0, allocation = 0; var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in code?.Elements() ?? [])
        {
            if (plans.Count >= 1024) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
            var relative = segment.Name == ns + "RelativeSegment";
            if (!relative && segment.Name != ns + "AbsoluteSegment") RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedMapping);
            var permitted = relative ? new[] { "Id", "Name", "InternalDescription", "Offset", "Size", "LoadStateMachine" }
                : new[] { "Id", "Name", "InternalDescription", "Address", "Size", "MemoryType", "UserMemory" };
            if (segment.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !permitted.Contains(a.Name.LocalName)))
                || segment.Elements().Any(e => e.Name != ns + "Data" && e.Name != ns + "Mask")
                || segment.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
            if (RootBuildGuard.Bool(segment, "UserMemory")) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedMapping);
            if (segment.Attribute("MemoryType") is { Value: not ("RAM" or "EEPROM" or "FLASH") }) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedMapping);
            var id = RootBuildGuard.Id(segment); if (!ids.Add(id)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
            var offset = RootBuildGuard.UInt(segment, relative ? "Offset" : "Address", 1_048_575); var size = RootBuildGuard.UInt(segment, "Size", 1_048_575);
            var arena = relative ? new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, (byte)RootBuildGuard.UInt(segment, "LoadStateMachine", 255))
                : new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory);
            var data = Payload(RootBuildGuard.One(segment, ns + "Data")); var mask = Payload(RootBuildGuard.One(segment, ns + "Mask"));
            var dataLength = data is null ? 0 : Length(data); var maskLength = mask is null ? 0 : Length(mask);
            if (mask is not null && (data is null || dataLength != maskLength)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
            var length = Math.Max(size, (uint)dataLength);
            if ((raw += dataLength + maskLength) > RootBuildGuard.Total || (allocation += length) > RootBuildGuard.Total)
                RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
            plans.Add((id, arena, offset, size, data, mask));
        }
        if (plans.Select(p => p.Arena.Kind).Distinct().Count() > 1) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedMapping);
        foreach (var group in plans.GroupBy(p => p.Arena))
        {
            ulong end = 0; var first = true;
            foreach (var item in group.OrderBy(p => p.Offset))
            {
                var length = Math.Max(item.Size, (uint)(item.Data is null ? 0 : Length(item.Data)));
                if (length == 0) continue;
                if (!first && item.Offset < end) RootBuildGuard.Fail(RootImageBuildIssueCode.SourceOverlap, resource: item.Id);
                end = (ulong)item.Offset + length; first = false;
            }
        }
        return plans.Select(p => new ImageCompositionSourceSegment(p.Id, p.Arena, p.Offset, p.Size,
            p.Data is null ? null : Convert.FromBase64String(p.Data), p.Mask is null ? null : Convert.FromBase64String(p.Mask))).ToArray();
    }
    private static string? Payload(XElement? element)
    {
        if (element is null) return null;
        if (element.HasElements || element.Attributes().Any(a => !a.IsNamespaceDeclaration)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        return element.Value;
    }
    private static int Length(string text)
    {
        if (text.Length > 2 * RootBuildGuard.Blob) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
        var compact = text.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (compact.Length % 4 != 0) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidDeclaration);
        var padding = compact.Length == 0 ? 0 : compact[^1] == '=' ? compact.Length > 1 && compact[^2] == '=' ? 2 : 1 : 0;
        var count = compact.Length / 4 * 3 - padding;
        if (count > RootBuildGuard.Blob) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
        return count;
    }
}
