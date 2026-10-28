using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Knx;

internal sealed partial class NativeRootImageInventory
{
    private const int TextLimit = 4 * 1024 * 1024;
    private static bool External(XElement element) => element.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware"
        && !string.IsNullOrWhiteSpace(a.Value) && a.Value is not ("false" or "False" or "0"))
        || element.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler" or "BinaryDataRef"
        || element.Name.LocalName == "Extension" && (element.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
            || element.Elements().Any(e => e.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
        || element.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Plugin"));
    private static void CheckValue(string? value, string id)
    { if (value is not null && (value.Length > 64 || value.Any(char.IsControl))) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, id); }
    private static void CheckSavedBudget<T>(IReadOnlyDictionary<string, T> values) where T : class?
    {
        if (values.Count > 100000) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        long text = 0;
        foreach (var (key, value) in values)
        {
            var raw = value as string;
            if (!RootBuildGuard.Safe(key) || raw is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue);
            CheckValue(raw, key); if ((text += key.Length + raw.Length) > TextLimit) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        }
    }
    private static Dictionary<string, XElement> Index(XElement? container, XName child)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (container is null) return result;
        if (container.Attributes().Any(a => !a.IsNamespaceDeclaration) || container.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        foreach (var item in container.Elements())
            if (item.Name != child || result.Count >= 100000 || !result.TryAdd(RootBuildGuard.Id(item), item)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        return result;
    }
    private static Dictionary<string, ParameterType> ParseTypes(Dictionary<string, XElement> types, XNamespace ns)
    {
        var result = new Dictionary<string, ParameterType>(StringComparer.Ordinal); var total = 0;
        foreach (var (id, declaration) in types)
        {
            var shapes = declaration.Elements().Take(2).ToArray();
            if (shapes.Length != 1 || shapes[0].Name.Namespace != ns || declaration.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
            var shape = shapes[0];
            if (shape.Name.LocalName is "TypeFloat" or "TypeTime")
            {
                if (OfflineParameterPatch.PreflightWordDeclaration(shape) is { } extendedIssue)
                    throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedType, id, ParameterIssue: extendedIssue));
                var extended = Ksxml.ParseParameterType(declaration);
                if (extended is null || extended.UnsupportedReason is not null
                    || extended.DataType is not (ParameterDataType.Float or ParameterDataType.Time))
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedType, id);
                result.Add(id, extended!);
                continue;
            }
            if (shape.Name.LocalName is not ("TypeNumber" or "TypeRestriction")) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedType, id);
            var bits = RootBuildGuard.UInt(shape, "SizeInBit", 32); if (bits == 0) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedType, id);
            if (shape.Name.LocalName == "TypeRestriction")
            {
                var count = shape.Elements().Take(4097).Count(); total = checked(total + count);
                if (count > 4096 || total > 65536) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded, id);
                if (OfflineParameterPatch.PreflightWordDeclaration(shape) is { } issue)
                    throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedType, id, ParameterIssue: issue));
            }
            else
            {
                RootBuildGuard.Leaf(shape);
                var signed = (string?)shape.Attribute("Type") switch { "signedInt" => true, "unsignedInt" => false,
                    _ => throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedType, id)) };
                var hasMin = long.TryParse((string?)shape.Attribute("minInclusive"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min);
                var hasMax = long.TryParse((string?)shape.Attribute("maxInclusive"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max);
                if (!hasMin || !hasMax || min > max)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
                var lower = signed ? -(1L << ((int)bits - 1)) : 0;
                var upper = signed ? (1L << ((int)bits - 1)) - 1 : (1L << (int)bits) - 1;
                if (Math.Max(lower, min) > Math.Min(upper, max)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
            }
            var parsed = Ksxml.ParseParameterType(declaration);
            if (parsed is null || parsed.UnsupportedReason is not null || parsed.DataType is not (ParameterDataType.Number or ParameterDataType.Enumeration))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedType, id);
            result.Add(id, parsed!);
        }
        return result;
    }
    internal static NativeImageTarget Target(ResourcePatchArena arena) => arena.Kind switch
    {
        ResourcePatchArenaKind.AbsoluteMemory when arena.LoadStateMachine is null => new(NativeImageSpace.StandardMemory),
        ResourcePatchArenaKind.RelativeMemory when arena.LoadStateMachine is { } lsm => new(NativeImageSpace.RelativeMemory, lsm),
        _ => throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedMapping)),
    };
    private (NativeImageTarget Target, PropertySourceSelector? Raw, PropertyPdtLayout? Pdt) Map(ParameterStorageLocation location, string id)
    {
        if (location.Kind is ParameterStorageKind.AbsoluteMemory or ParameterStorageKind.RelativeMemory)
        {
            var target = location.Kind == ParameterStorageKind.AbsoluteMemory ? new NativeImageTarget(NativeImageSpace.StandardMemory)
                : new NativeImageTarget(NativeImageSpace.RelativeMemory, checked((byte)location.LoadStateMachine!.Value));
            return (target, null, null);
        }
        if (PropertyProfile?.Selection is not { Status: MaskProfileStatus.Supported, LegacyVersion: 0, FirstAppObjectIndex: not null }
            || PropertyProfile.Selection.FirstAppObjectIndex != ResourceContext!.FirstAppObjectIndex)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
        PropertySourceSelector raw = location.Kind switch
        {
            ParameterStorageKind.PropertyByIndex when location.ObjectIndex is { } index && location.PropertyId is { } pid
                => new(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: index),
            ParameterStorageKind.PropertyByType when location.ObjectType is { } type && location.Occurrence is { } occurrence && location.PropertyId is { } pid
                => new(PropertySourceKind.ByType, pid, ObjectType: type, Occurrence: occurrence),
            _ => throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedMapping, id)),
        };
        var (normalized, canonical) = ArchivedPropertySources.Normalize(raw, PropertyProfile!.Selection);
        if (!canonical) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
        var pdt = PropertyPdtRoles.Read(PropertyProfile.Interpret(location, MaskVersionId));
        if (pdt.Status != PropertyPdtStatus.KnownSymmetric || pdt.WriteElementBytes is not > 0) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
        return (new(NativeImageSpace.PropertyData, Selector: normalized), raw, pdt);
    }
    private static void CheckOwners(List<NativeRootOwner> owners)
    {
        foreach (var family in owners.Where(o => o.Target.Space == NativeImageSpace.PropertyData).GroupBy(o => o.Target.Selector!.PropertyId))
            if (family.Any(o => o.Target.Selector!.Kind == PropertySourceKind.ByType) && family.Any(o => o.Target.Selector!.Kind != PropertySourceKind.ByType))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousMapping);
        foreach (var group in owners.GroupBy(o => o.Target))
        {
            ulong end = 0; var first = true;
            foreach (var owner in group.OrderBy(o => (ulong)o.ByteOffset * 8 + o.BitOffset))
            {
                var start = (ulong)owner.ByteOffset * 8 + owner.BitOffset;
                if (!first && start < end) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, owner.ParameterId);
                end = start + owner.BitLength; first = false;
            }
        }
    }
    private static ImageCompositionSourceSegment[] ReadCode(XElement? code, XNamespace ns)
    {
        var plans = new List<(string Id, ResourcePatchArena Arena, uint Offset, uint Size, string? Data, string? Mask)>();
        var ids = new HashSet<string>(StringComparer.Ordinal); long payload = 0, allocation = 0;
        if (code is not null && (code.Attributes().Any(a => !a.IsNamespaceDeclaration) || code.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        foreach (var item in code?.Elements() ?? [])
        {
            if (plans.Count >= 1024) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
            var relative = item.Name == ns + "RelativeSegment";
            if (!relative && item.Name != ns + "AbsoluteSegment") NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
            var allowed = relative ? new[] { "Id", "Name", "InternalDescription", "Offset", "Size", "LoadStateMachine" }
                : ["Id", "Name", "InternalDescription", "Address", "Size", "MemoryType", "UserMemory"];
            if (item.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !allowed.Contains(a.Name.LocalName)))
                || item.Elements().Any(e => e.Name != ns + "Data" && e.Name != ns + "Mask")
                || item.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
            if (RootBuildGuard.Bool(item, "UserMemory") || item.Attribute("MemoryType") is { Value: not ("RAM" or "EEPROM" or "FLASH") })
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping);
            var id = RootBuildGuard.Id(item); if (!ids.Add(id)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
            var offset = RootBuildGuard.UInt(item, relative ? "Offset" : "Address", 1048575); var size = RootBuildGuard.UInt(item, "Size", 1048575);
            var arena = relative ? new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, (byte)RootBuildGuard.UInt(item, "LoadStateMachine", 255))
                : new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory);
            var data = Payload(RootBuildGuard.One(item, ns + "Data")); var mask = Payload(RootBuildGuard.One(item, ns + "Mask"));
            var dataLength = data is null ? 0 : PayloadLength(data); var maskLength = mask is null ? 0 : PayloadLength(mask);
            if (mask is not null && (data is null || dataLength != maskLength)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
            var length = Math.Max(size, (uint)dataLength);
            if ((payload += dataLength + maskLength) > TextLimit || (allocation += length) > TextLimit || (ulong)offset + length > (ulong)uint.MaxValue + 1)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded, id);
            plans.Add((id, arena, offset, size, data, mask));
        }
        foreach (var group in plans.GroupBy(p => p.Arena))
        {
            ulong end = 0; var first = true;
            foreach (var plan in group.OrderBy(p => p.Offset))
            {
                var length = Math.Max(plan.Size, (uint)(plan.Data is null ? 0 : PayloadLength(plan.Data))); if (length == 0) continue;
                if (!first && plan.Offset < end) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership, resource: plan.Id);
                end = (ulong)plan.Offset + length; first = false;
            }
        }
        return plans.Select(p => new ImageCompositionSourceSegment(p.Id, p.Arena, p.Offset, p.Size,
            p.Data is null ? null : Convert.FromBase64String(p.Data), p.Mask is null ? null : Convert.FromBase64String(p.Mask))).ToArray();
    }
    private static string? Payload(XElement? element)
    {
        if (element is null) return null;
        if (element.HasElements || element.Attributes().Any(a => !a.IsNamespaceDeclaration)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        return element.Value;
    }
    private static int PayloadLength(string value)
    {
        if (value.Length > 2 * 1024 * 1024) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        var compact = value.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (compact.Length % 4 != 0) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var padding = compact.Length == 0 ? 0 : compact[^1] == '=' ? compact.Length > 1 && compact[^2] == '=' ? 2 : 1 : 0;
        var length = compact.Length / 4 * 3 - padding;
        if (length > 1024 * 1024) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        return length;
    }
    private static string ValidateCapturedHardware(XDocument document, DeviceInstance device, ArchivedImageSources.Inputs inputs)
    {
        var hardware = inputs.Manifest.Hardware;
        if (document.Root is null || hardware is not { Status: ArchivedHardwareSourceStatus.Inspected })
            NativeGeneralHash.Fail(NativeGeneralIssueCode.SourceUnavailable);
        var nodes = RootBuildGuard.Nodes(document!, TextLimit); var ns = document!.Root!.Name.Namespace;
        if (document.Root.Name != ns + "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var rows = nodes.Where(e => e.Name.LocalName == "Hardware2Program" && (string?)e.Attribute("Id") == device.Hardware2ProgramRefId).Take(2).ToArray();
        if (rows.Length != 1) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var selected = rows[0]; XElement? cursor = selected;
        foreach (var name in new[] { "Hardware2Program", "Hardware2Programs", "Hardware", "Hardware", "Manufacturer", "ManufacturerData", "KNX" })
        { if (cursor?.Name != ns + name) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration); cursor = cursor!.Parent; }
        if (cursor is not null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var manufacturer = selected.Parent!.Parent!.Parent!.Parent!;
        var manufacturerId = device.Hardware2ProgramRefId![..6];
        if ((string?)manufacturer.Attribute("RefId") != manufacturerId
            || manufacturer.Parent!.Elements().Count(e => e.Name.LocalName == "Manufacturer" && (string?)e.Attribute("RefId") == manufacturerId) != 1)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var refs = selected.Descendants().Where(e => e.Name.LocalName == "ApplicationProgramRef").Take(2).ToArray();
        if (refs.Length != 1 || refs[0].Name != ns + "ApplicationProgramRef" || !ReferenceEquals(refs[0].Parent, selected)
            || refs[0].HasElements || !string.IsNullOrWhiteSpace(refs[0].Value)
            || refs[0].Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "RefId")
            || (string?)refs[0].Attribute("RefId") != inputs.Manifest.ApplicationId) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selected.ToString(SaveOptions.DisableFormatting))));
        if (hash != hardware!.DefinitionHash) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
        return hash;
    }
    private static NativeGeneralIssue Adapt(RootImageBuildIssue issue) => new(issue.Code switch
    {
        RootImageBuildIssueCode.LimitExceeded => NativeGeneralIssueCode.LimitExceeded,
        RootImageBuildIssueCode.InvalidValue => NativeGeneralIssueCode.InvalidValue,
        RootImageBuildIssueCode.InvalidPolicy => NativeGeneralIssueCode.InvalidPolicy,
        RootImageBuildIssueCode.UnsupportedType => NativeGeneralIssueCode.UnsupportedType,
        RootImageBuildIssueCode.UnsupportedNativeCarrier => NativeGeneralIssueCode.UnsupportedNativeCarrier,
        RootImageBuildIssueCode.InvalidRequest => NativeGeneralIssueCode.InvalidRequest,
        _ => NativeGeneralIssueCode.InvalidDeclaration,
    }, issue.RefId, issue.ResourceId, ParameterIssue: issue.ParameterIssue);
}
