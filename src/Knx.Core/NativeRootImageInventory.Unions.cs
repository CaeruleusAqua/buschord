using System.Xml.Linq;

namespace Knx;

internal sealed record NativeRootUnion(string Key, NativeRootOwner DefaultOwner, uint InitialWord,
    IReadOnlyList<string> ParameterIds, string DeclarationHash);

internal sealed partial class NativeRootImageInventory
{
    private static Dictionary<string, XElement> IndexUnionParameters(XElement? container, XNamespace ns)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (container is null) return result;
        if (container.HasAttributes || container.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        foreach (var item in container.Elements())
        {
            var parameters = item.Name == ns + "Parameter" ? new[] { item }
                : item.Name == ns + "Union" ? item.Elements(ns + "Parameter").ToArray()
                : throw new NativeGeneralRejected(new(NativeGeneralIssueCode.UnsupportedSource));
            if (parameters.Length == 0) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
            foreach (var parameter in parameters)
                if (result.Count >= 100000 || !result.TryAdd(RootBuildGuard.Id(parameter), parameter))
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        }
        return result;
    }

    private void ReadUnionLayouts(Dictionary<string, XElement> parameters, Dictionary<string, NativeRootOwner> owners,
        Dictionary<string, XElement> types, Dictionary<string, ParameterType> parsed)
    {
        var unions = new List<NativeRootUnion>();
        foreach (var element in parameters.Values.Select(p => p.Parent).Where(p => p?.Name.LocalName == "Union").Distinct())
        {
            var ns = element!.Name.Namespace;
            if (element.Attributes().Any(a => a.Name != "SizeInBit")
                || element.Elements().Any(e => e.Name != ns + "Memory" && e.Name != ns + "Parameter")
                || element.Elements(ns + "Memory").Count() != 1 || element.Parent?.Name != ns + "Parameters"
                || element.Parent.Parent?.Name != ns + "Static") NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
            var size = RootBuildGuard.UInt(element, "SizeInBit", 32);
            if (size is not (8 or 16 or 24 or 32)) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping);
            var declarations = element.Elements(ns + "Parameter").ToArray();
            var defaults = declarations.Where(p => RootBuildGuard.Bool(p, "DefaultUnionParameter")).ToArray();
            if (defaults.Length != 1) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
            var id = RootBuildGuard.Id(defaults[0]);
            if (!owners.TryGetValue(id, out var owner) || owner.Target.Space != NativeImageSpace.StandardMemory || owner.BitOffset != 0 || owner.BitLength != size
                || RootBuildGuard.UInt(defaults[0], "Offset", 1048575) != 0 || RootBuildGuard.UInt(defaults[0], "BitOffset", 7) != 0)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
            var key = "union-" + unions.Count;
            foreach (var p in declarations)
            {
                var parameterId = RootBuildGuard.Id(p);
                if (!owners.TryGetValue(parameterId, out var alias) || alias.Target != owner!.Target
                    || alias.ByteOffset < owner.ByteOffset || (ulong)(alias.ByteOffset - owner.ByteOffset) * 8 + alias.BitOffset + alias.BitLength > size)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.UncoveredOwner, parameterId);
                UnionByParameter.Add(parameterId, key);
            }
            var typeId = SourceId(defaults[0], "ParameterType", AdmitRootUnions);
            _ = Encode(types[typeId], parsed[typeId], RootBuildGuard.Id(defaults[0], "Value"), 0, id);
            if (Policy!.ByteOrder != ParameterPatchByteOrder.BigEndian) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidPolicy);
            var segmentId = RootBuildGuard.Id(element.Element(ns + "Memory")!, "CodeSegment");
            var code = CodeSources.Single(s => s.SourceId == segmentId);
            var offset = checked((int)(owner!.ByteOffset - code.ByteOffsetInArena));
            var bytes = checked((int)(size / 8));
            if (code.RawData is null || offset + bytes > code.RawData.Length
                || code.RawMask is not null && (offset + bytes > code.RawMask.Length
                    || Enumerable.Range(offset, bytes).Any(i => code.RawMask[i] != 255)))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource, id, segmentId);
            uint initial = 0;
            for (var i = 0; i < bytes; i++) initial = initial << 8 | code.RawData![offset + i];
            unions.Add(new(key, owner!, initial, declarations.Select(p => RootBuildGuard.Id(p)).ToArray(), NativeGeneralHash.Document(element)));
        }
        Unions = Array.AsReadOnly(unions.ToArray());
    }
}

/// <summary>Explicit M112 full-load working image with bounded root unions. It does not authorize bus access.</summary>
public static class OfflineM112ImageBuild
{
    public static NativeImageBuildPlan Preview(Project project, DeviceInstance device)
        => new(NativeGeneralImageBuild.BuildProfile(project, device, [255], new(GeneratedTableFormat.M112), admitRootUnions: true), [255]);
}
