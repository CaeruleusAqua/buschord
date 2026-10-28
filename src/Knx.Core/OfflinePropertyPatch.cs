using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

/// <summary>Explicit root fixed-field preview over complete supplied property buffers. Never executes or writes.</summary>
public static class OfflinePropertyPatch
{
    private sealed class Rejected(PropertyPatchIssue issue) : Exception { internal PropertyPatchIssue Issue => issue; }
    [DoesNotReturn]
    private static void Fail(PropertyPatchIssueCode code, string? reference = null, string? key = null)
        => throw new Rejected(new(code, Safe(reference) ? reference : null, Safe(key) ? key : null));
    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private sealed record Owner(string Id, PropertySourceSelector Selector, ulong Start, uint? Width);

    public static PropertyPatchPlan Preview(Project project, DeviceInstance device, IReadOnlyList<ParameterPatchValue> subset,
        ParameterPatchByteOrder byteOrder)
    {
        var empty = new ArchivedPropertySourceIndex(PropertySourceStatus.ApplicationUnavailable,
            new(ArchivedImageSourceStatus.InvalidRequest), ParameterProfileContext.Read(null, null).Selection);
        if (!ArchivedPropertySources.IdentityWithinBudget(project, device) || !ParameterPatchSourceBinding.ValuesWithinBudget(device))
            return new(empty, new(PropertyPatchIssueCode.InvalidRequest));
        var selection = CopyRequest(subset, byteOrder);
        if (selection is null) return new(empty, new(PropertyPatchIssueCode.InvalidRequest));
        var inputs = ArchivedImageSources.ReadInputs(project, device); var index = ArchivedPropertySources.Read(inputs);
        try { return Create(project, device, selection, byteOrder, inputs, index); }
        catch (Rejected failure) { return new(index, failure.Issue); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or IOException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return new(index, new(PropertyPatchIssueCode.InvalidDeclaration)); }
    }
    private static ParameterPatchValue[]? CopyRequest(IReadOnlyList<ParameterPatchValue> subset, ParameterPatchByteOrder order)
    {
        if (subset is null || subset.Count is < 1 or > 64 || !Enum.IsDefined(order)) return null;
        var references = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        var selection = new List<ParameterPatchValue>(subset.Count);
        foreach (var value in subset.Take(65))
        {
            if (++count > 64 || value is null || !Safe(value.RefId) || value.RawValue is null || value.RawValue.Length > 64
                || value.RawValue.Any(char.IsControl) || !references.Add(value.RefId)) return null;
            selection.Add(new(value.RefId, value.RawValue));
        }
        return count == subset.Count ? selection.ToArray() : null;
    }
    private static PropertyPatchPlan Create(Project project, DeviceInstance device, IReadOnlyList<ParameterPatchValue> subset,
        ParameterPatchByteOrder order, ArchivedImageSources.Inputs inputs, ArchivedPropertySourceIndex index)
    {
        if (subset is null || subset.Count is < 1 or > 64 || !Enum.IsDefined(order) || !ArchivedPropertySources.IdentityWithinBudget(project, device)
            || !ParameterPatchSourceBinding.ValuesWithinBudget(device)) Fail(PropertyPatchIssueCode.InvalidRequest);
        var identity = ResourcePatchIdentity.Capture(project, device);
        var selected = subset.Take(65).Select(p => p is null ? null : p with { }).ToArray();
        if (selected.Length is < 1 or > 64 || selected.Any(p => p is null || !Safe(p.RefId) || p.RawValue is null || p.RawValue.Length > 64 || p.RawValue.Any(char.IsControl))
            || selected.Select(p => p!.RefId).Distinct(StringComparer.Ordinal).Count() != selected.Length) Fail(PropertyPatchIssueCode.InvalidRequest);
        var selection = selected.Select(p => p!).ToArray();
        var root = inputs.Application?.Root;
        if (inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || root is null
            || inputs.Manifest.Binding is null || !inputs.Manifest.Binding.MatchesIdentity(project, device)) Fail(PropertyPatchIssueCode.ApplicationUnavailable);
        if (index.Status == PropertySourceStatus.AmbiguousLayout) Fail(PropertyPatchIssueCode.AmbiguousOwnership);
        if (index.Status != PropertySourceStatus.Inspected || index.Binding is null) Fail(PropertyPatchIssueCode.UnknownInitialData);
        if (index.PropertyProfileSelection.Status != MaskProfileStatus.Supported) Fail(PropertyPatchIssueCode.UnsupportedLayout);
        if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) Fail(PropertyPatchIssueCode.UnsupportedApplication);
        var nodes = root!.DescendantsAndSelf().Take(500001).ToArray();
        if (nodes.Length > 500000) Fail(PropertyPatchIssueCode.ResourceLimitExceeded);
        var ns = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23")
            || nodes.Any(e => e.Name.Namespace != ns || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml)))
            Fail(PropertyPatchIssueCode.InvalidDeclaration);
        var applications = nodes.Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        if (applications.Length != 1 || (string?)applications[0].Attribute("Id") != inputs.Manifest.ApplicationId) Fail(PropertyPatchIssueCode.InvalidDeclaration);
        var app = applications[0]; var appId = inputs.Manifest.ApplicationId!;
        ProfileGate(nodes, appId);
        if (nodes.Any(e => e.Name.LocalName == "Memory" && e.Parent?.Name.LocalName is "Parameter" or "Union")) Fail(PropertyPatchIssueCode.UnsupportedPhysicalAlias);
        if (nodes.Any(e => e.Name.LocalName == "ParameterCalculation")) Fail(PropertyPatchIssueCode.UnsupportedDependency);
        if (nodes.Any(e => e.Name.LocalName is "Module" or "Repeat" or "ParameterInstance")) Fail(PropertyPatchIssueCode.UnsupportedParameter);
        var stat = One(app, ns + "Static", true)!;
        var parametersContainer = One(stat, ns + "Parameters", true)!;
        var parameters = Index(parametersContainer.Descendants(ns + "Parameter"), ns + "Parameter");
        var knownParameters = parameters.Values.ToHashSet();
        if (nodes.Any(e => e.Name == ns + "Parameter" && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef") && !knownParameters.Contains(e)))
            Fail(PropertyPatchIssueCode.InvalidDeclaration);
        var references = Index(One(stat, ns + "ParameterRefs", true)!.Elements(), ns + "ParameterRef");
        var types = Index(One(stat, ns + "ParameterTypes", true)!.Elements(), ns + "ParameterType");
        var knownReferences = references.Values.ToHashSet(); var knownTypes = types.Values.ToHashSet();
        if (nodes.Any(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")
            && (e.Name == ns + "ParameterRef" && !knownReferences.Contains(e) || e.Name == ns + "ParameterType" && !knownTypes.Contains(e))))
            Fail(PropertyPatchIssueCode.InvalidDeclaration);
        foreach (var pair in references)
            if (pair.Value.HasElements || !string.IsNullOrWhiteSpace(pair.Value.Value) || !parameters.ContainsKey((string?)pair.Value.Attribute("RefId") ?? "")
                || pair.Value.Attribute("ParameterType") is not null || pair.Value.Attribute("ParameterTypeId") is not null)
                Fail(PropertyPatchIssueCode.InvalidDeclaration, pair.Key);
        var aliases = references.GroupBy(p => (string)p.Value.Attribute("RefId")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var profile = ParameterProfileContext.Read(inputs.Master, (string?)app.Attribute("MaskVersion")).ForApplication((string?)app.Attribute("MaskVersion"), ns.NamespaceName);
        if (profile.Selection.Fingerprint != index.PropertyProfileSelection.Fingerprint) Fail(PropertyPatchIssueCode.InputsChanged);
        var storage = new ParameterStorageReader(root, profile);
        if (app.Elements().Any(e => e.Name.LocalName == "Options")) Fail(PropertyPatchIssueCode.InvalidDeclaration);
        var options = One(stat, ns + "Options", false);
        if (options is not null && (options.HasElements || !string.IsNullOrWhiteSpace(options.Value))) Fail(PropertyPatchIssueCode.InvalidDeclaration);
        if (options?.Attribute("ParameterByteOrder") is { } declaredOrder)
        {
            if (declaredOrder.Value is not ("BigEndian" or "LittleEndian")) Fail(PropertyPatchIssueCode.InvalidDeclaration);
            if (declaredOrder.Value != order.ToString()) Fail(PropertyPatchIssueCode.UnsupportedLayout);
        }
        var cachedTypes = new Dictionary<string, ParameterType>(StringComparer.Ordinal);
        var fields = new List<PropertyPatchField>(); var words = new Dictionary<string, uint>(StringComparer.Ordinal);
        var selectedParameters = new HashSet<string>(StringComparer.Ordinal); var chosen = new Dictionary<string, PropertySourceEntry>(StringComparer.Ordinal);
        foreach (var value in selection)
        {
            if (!references.TryGetValue(value.RefId, out var reference)) Fail(PropertyPatchIssueCode.UnknownParameter, value.RefId);
            var parameterId = (string)reference!.Attribute("RefId")!; var parameter = parameters[parameterId];
            Header(parameter, "Id", "Name", "Text", "ParameterType", "Value", "Access", "InternalDescription", "Description", "SuffixText", "BaseValue");
            Header(reference, "Id", "RefId", "Name", "Text", "Value", "Access", "InternalDescription", "Description", "SuffixText", "DisplayOrder");
            if (parameter.Parent != parametersContainer || parameter.Attribute("ParameterTypeId") is not null) Fail(PropertyPatchIssueCode.InvalidDeclaration, value.RefId);
            if (parameter.Attribute("BaseValue") is not null) Fail(PropertyPatchIssueCode.UnsupportedParameter, value.RefId);
            if (aliases[parameterId] != 1 || !selectedParameters.Add(parameterId)) Fail(PropertyPatchIssueCode.AmbiguousOwnership, value.RefId);
            var access = (string?)reference.Attribute("Access") ?? (string?)parameter.Attribute("Access");
            if (access is "None" or "Read")
                Fail(PropertyPatchIssueCode.UnsupportedParameter, value.RefId);
            if (parameter.Elements().Take(2).Count() != 1 || parameter.Elements().Single().Name != ns + "Property") Fail(PropertyPatchIssueCode.UnsupportedParameter, value.RefId);
            var property = parameter.Elements().Single();
            if (property.HasElements || !string.IsNullOrWhiteSpace(property.Value)) Fail(PropertyPatchIssueCode.InvalidDeclaration, value.RefId);
            if (property.Attributes().Any(a => a.Name.LocalName is "BaseOffset" or "BaseIndex" or "BaseOccurrence")) Fail(PropertyPatchIssueCode.UnsupportedParameter, value.RefId);
            var typeId = (string?)parameter.Attribute("ParameterType");
            XElement? typeDeclaration = null;
            if (!Safe(typeId) || !types.TryGetValue(typeId!, out typeDeclaration) || typeDeclaration.Elements().Take(2).Count() != 1)
                Fail(PropertyPatchIssueCode.InvalidDeclaration, value.RefId);
            var shape = typeDeclaration!.Elements().Single();
            Header(typeDeclaration, "Id", "Name", "InternalDescription", "Plugin", "ValidationErrorRef");
            if (shape.Name != ns + "TypeNumber" && shape.Name != ns + "TypeRestriction") Fail(PropertyPatchIssueCode.UnsupportedParameter, value.RefId);
            if (!cachedTypes.TryGetValue(typeId!, out var type))
            {
                if (shape.Name == ns + "TypeRestriction" && OfflineParameterPatch.ValidateFixedEnumDeclaration(shape) is { } enumIssue) Fail(CodecIssue(enumIssue), value.RefId);
                if (shape.Name == ns + "TypeNumber" && shape.HasElements) Fail(PropertyPatchIssueCode.InvalidDeclaration, value.RefId);
                type = Ksxml.ParseParameterType(typeDeclaration)!;
                if (type is null) Fail(PropertyPatchIssueCode.InvalidDeclaration, value.RefId);
                cachedTypes.Add(typeId!, type);
            }
            var encoded = FixedScalarPatchCodec.Encode(shape, type, value.RawValue);
            if (encoded.Issue is { } issue) Fail(CodecIssue(issue), value.RefId);
            var location = storage.Read(parameter, reference, type);
            if (location.Status != ParameterStorageStatus.Supported || location.Kind is not (ParameterStorageKind.PropertyByIndex or ParameterStorageKind.PropertyByType)
                || location.StartAddress is null || location.BitOffset is null) Fail(PropertyPatchIssueCode.UnsupportedParameter, value.RefId);
            if (order == ParameterPatchByteOrder.LittleEndian && (encoded.Bits % 8 != 0 || location.BitOffset != 0)) Fail(PropertyPatchIssueCode.UnsupportedLayout, value.RefId);
            var selector = Selector(location, profile.Selection);
            var key = ArchivedPropertySources.Key(selector);
            var entry = index.Entries.SingleOrDefault(e => e.PropertyKey == key && e.SelectorIsCanonical);
            if (entry is null) Fail(PropertyPatchIssueCode.UnknownInitialData, value.RefId, key);
            if (!chosen.ContainsKey(key)) ValidateBefore(entry!, value.RefId);
            foreach (var other in index.Entries)
                if (other.Selector.PropertyId == selector.PropertyId && (other.Selector.Kind == PropertySourceKind.ByType) != (selector.Kind == PropertySourceKind.ByType))
                    Fail(PropertyPatchIssueCode.AmbiguousOwnership, value.RefId, key);
            var span = (uint)((location.BitOffset.Value + encoded.Bits + 7) / 8);
            if ((ulong)location.StartAddress.Value + span > (ulong)entry!.DataFragments[0].Data.Length) Fail(PropertyPatchIssueCode.UnknownInitialData, value.RefId, key);
            fields.Add(new(value.RefId, parameterId, key, value.RawValue, selector, location.StartAddress.Value, location.BitOffset.Value, (uint)encoded.Bits));
            words.Add(value.RefId, encoded.Word); chosen[key] = entry;
        }
        if (chosen.Count > 16) Fail(PropertyPatchIssueCode.ResourceLimitExceeded);
        DependencyGate(nodes, references, selectedParameters);
        var ownerList = new List<Owner>();
        foreach (var pair in parameters)
        {
            var location = storage.Read(pair.Value, null, OwnerType(types.GetValueOrDefault((string?)pair.Value.Attribute("ParameterType") ?? "")));
            if (location.Status == ParameterStorageStatus.NotDeclared) continue;
            if (location.Status != ParameterStorageStatus.Supported || location.StartAddress is null || location.BitOffset is null
                || location.Kind is not (ParameterStorageKind.PropertyByIndex or ParameterStorageKind.PropertyByType)) Fail(PropertyPatchIssueCode.AmbiguousOwnership);
            ownerList.Add(new(pair.Key, Selector(location, profile.Selection), (ulong)location.StartAddress.Value * 8 + location.BitOffset.Value,
                location.Footprint is { Status: ParameterFootprintStatus.Known } footprint ? footprint.BitLength : null));
        }
        var owners = ownerList.GroupBy(o => o.Selector.PropertyId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var field in fields)
        {
            var start = (ulong)field.ByteOffsetInPropertyData * 8 + field.BitOffset; var end = start + field.BitLength;
            foreach (var owner in owners.GetValueOrDefault(field.Selector.PropertyId) ?? [])
            {
                if (owner.Id == field.ParameterId) continue;
                var other = owner.Selector;
                if ((other.Kind == PropertySourceKind.ByType) != (field.Selector.Kind == PropertySourceKind.ByType)) Fail(PropertyPatchIssueCode.AmbiguousOwnership, field.RefId);
                if (other != field.Selector) continue;
                var otherStart = owner.Start;
                if (otherStart >= end) continue;
                if (owner.Width is not { } width || otherStart + width > start)
                    Fail(PropertyPatchIssueCode.AmbiguousOwnership, field.RefId);
            }
        }
        var ranges = new List<PropertyPatchRange>(); var images = new List<PropertyPatchImage>(); var totalBytes = 0L;
        foreach (var entry in chosen.Values.OrderBy(e => e.PropertyKey, StringComparer.Ordinal))
        {
            var before = entry.DataFragments[0]; totalBytes += before.Data.Length * 2L + 4;
            if (totalBytes > 4 * 1024 * 1024) Fail(PropertyPatchIssueCode.ResourceLimitExceeded);
            var touches = new SortedDictionary<int, (byte Bits, byte Mask)>();
            foreach (var field in fields.Where(f => f.PropertyKey == entry.PropertyKey))
                FixedScalarPatchCodec.Accumulate(touches, words[field.RefId], checked((int)field.ByteOffsetInPropertyData), field.BitOffset, field.BitLength, order);
            var data = before.Data.ToArray(); var mask = before.Mask!.ToArray(); var keys = touches.Keys.ToArray();
            for (var from = 0; from < keys.Length;)
            {
                var to = from + 1; while (to < keys.Length && keys[to] == keys[to - 1] + 1) to++;
                var oldBytes = new byte[to - from]; var newBytes = new byte[oldBytes.Length]; var fieldMask = new byte[oldBytes.Length];
                for (var i = from; i < to; i++)
                {
                    var at = keys[i]; var target = i - from; var touch = touches[at]; oldBytes[target] = data[at]; fieldMask[target] = touch.Mask;
                    newBytes[target] = data[at] = (byte)((data[at] & ~touch.Mask) | (touch.Bits & touch.Mask));
                }
                var validity = Enumerable.Repeat((byte)255, oldBytes.Length).ToArray();
                ranges.Add(new(entry.PropertyKey, entry.Selector, (uint)keys[from], new(oldBytes), new(newBytes), new(fieldMask), new(validity), new(validity)));
                from = to;
            }
            images.Add(new(entry, data, mask));
        }
        if (ranges.Sum(r => r.Length) > 320 || !ArchivedPropertySources.IdentityWithinBudget(project, device) || !ParameterPatchSourceBinding.ValuesWithinBudget(device)
            || ResourcePatchIdentity.Capture(project, device) != identity || !index.Binding.IsCurrent(project, device))
            Fail(PropertyPatchIssueCode.InputsChanged);
        var binding = new PropertyPatchSourceBinding(project, device, identity, inputs, index, selection, order, fields.ToArray(), images.ToArray());
        if (!binding.MatchesIdentity(project, device)) Fail(PropertyPatchIssueCode.InputsChanged);
        return new(index, binding, fields.ToArray(), ranges.ToArray(), images.ToArray());
    }
    private static void ValidateBefore(PropertySourceEntry entry, string reference)
    {
        if (entry.CountFragment is null || entry.EncodedCount is null) Fail(PropertyPatchIssueCode.UnknownElementCount, reference, entry.PropertyKey);
        if (entry.CountStatus != PropertyCountStatus.FullyValid || entry.DataFragments.Count != 1 || entry.DataFragments[0].ByteOffset != 0)
        {
            if (entry.CountStatus != PropertyCountStatus.FullyValid) Fail(PropertyPatchIssueCode.InvalidInitialMask, reference, entry.PropertyKey);
            Fail(PropertyPatchIssueCode.IncompleteInitialData, reference, entry.PropertyKey);
        }
        var data = entry.DataFragments[0];
        if (!ArchivedPropertySources.FullMask(data.Mask, data.Data.Length)) Fail(PropertyPatchIssueCode.InvalidInitialMask, reference, entry.PropertyKey);
        if (entry.PdtLayout.Status == PropertyPdtStatus.KnownAsymmetric) Fail(PropertyPatchIssueCode.AsymmetricPdt, reference, entry.PropertyKey);
        if (entry.PdtLayout.Status != PropertyPdtStatus.KnownSymmetric || entry.PdtLayout.WriteElementBytes is not > 0) Fail(PropertyPatchIssueCode.UnknownPdt, reference, entry.PropertyKey);
        if ((ulong)entry.EncodedCount.Value * entry.PdtLayout.WriteElementBytes.Value != (ulong)data.Data.Length)
            Fail(PropertyPatchIssueCode.IncompleteInitialData, reference, entry.PropertyKey);
    }
    private static PropertySourceSelector Selector(ParameterStorageLocation location, MaskProfileSelection profile)
    {
        var pid = location.PropertyId.GetValueOrDefault();
        if (location.PropertyId is null) Fail(PropertyPatchIssueCode.UnsupportedLayout);
        PropertySourceSelector raw;
        if (location.Kind == ParameterStorageKind.PropertyByIndex && location.ObjectIndex is { } index)
            raw = new(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: index);
        else if (location.Kind == ParameterStorageKind.PropertyByType && location.ObjectType is { } type && location.Occurrence is { } occurrence)
            raw = new(PropertySourceKind.ByType, pid, ObjectType: type, Occurrence: occurrence);
        else { Fail(PropertyPatchIssueCode.UnsupportedLayout); return null!; }
        var normalized = ArchivedPropertySources.Normalize(raw, profile);
        if (!normalized.Canonical) Fail(PropertyPatchIssueCode.UnsupportedLayout);
        return normalized.Selector;
    }
    private static XElement? One(XElement parent, XName name, bool required)
    {
        var rows = parent.Elements().Where(e => e.Name.LocalName == name.LocalName).Take(2).ToArray();
        if (rows.Length > 1 || rows.Any(e => e.Name != name) || required && rows.Length != 1) Fail(PropertyPatchIssueCode.InvalidDeclaration);
        return rows.FirstOrDefault();
    }
    private static void Header(XElement element, params string[] names)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !names.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) Fail(PropertyPatchIssueCode.InvalidDeclaration);
    }
    private static Dictionary<string, XElement> Index(IEnumerable<XElement> elements, XName name)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in elements)
        {
            var id = (string?)e.Attribute("Id");
            if (result.Count >= 100000 || e.Name != name || !Safe(id) || !result.TryAdd(id!, e)) Fail(PropertyPatchIssueCode.InvalidDeclaration);
        }
        return result;
    }
    private static ParameterType? OwnerType(XElement? declaration)
    {
        if (declaration?.Elements().Take(2).Count() != 1) return null;
        var shape = declaration.Elements().Single();
        if (!int.TryParse((string?)shape.Attribute("SizeInBit"), NumberStyles.None, CultureInfo.InvariantCulture, out var bits)) return null;
        return shape.Name.LocalName switch
        {
            "TypeNumber" => new() { Id = (string?)declaration.Attribute("Id") ?? "", Name = (string?)declaration.Attribute("Name") ?? "",
                DataType = ParameterDataType.Number, NumberType = (string?)shape.Attribute("Type"), SizeInBit = bits },
            "TypeRestriction" => new() { Id = (string?)declaration.Attribute("Id") ?? "", Name = (string?)declaration.Attribute("Name") ?? "",
                DataType = ParameterDataType.Enumeration, SizeInBit = bits }, _ => null,
        };
    }
    private static PropertyPatchIssueCode CodecIssue(ParameterPatchIssueCode code) => code switch
    {
        ParameterPatchIssueCode.InvalidValue => PropertyPatchIssueCode.InvalidValue,
        ParameterPatchIssueCode.UnsupportedParameter => PropertyPatchIssueCode.UnsupportedParameter,
        ParameterPatchIssueCode.ResourceLimitExceeded => PropertyPatchIssueCode.ResourceLimitExceeded,
        _ => PropertyPatchIssueCode.InvalidDeclaration,
    };
    private static void ProfileGate(IEnumerable<XElement> nodes, string app)
    {
        if (KnownDcaRequirementsRegistry.RequiresDca(app)) Fail(PropertyPatchIssueCode.UnsupportedApplication);
        foreach (var e in nodes)
            if (e.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware" && a.Value is not ("false" or "False" or "0" or ""))
                || e.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler" or "BinaryDataRef"
                || e.Name.LocalName == "Extension" && (e.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
                    || e.Elements().Any(c => c.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
                || e.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !string.IsNullOrWhiteSpace((string?)e.Attribute("Plugin")))
                Fail(PropertyPatchIssueCode.UnsupportedApplication);
    }
    private static void DependencyGate(IEnumerable<XElement> nodes, Dictionary<string, XElement> refs, HashSet<string> selectedParameters)
    {
        var watched = refs.Where(p => selectedParameters.Contains((string)p.Value.Attribute("RefId")!)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var e in nodes.Where(e => e.Name.LocalName == "Assign"))
        {
            var target = (string?)e.Attribute("TargetParamRefRef"); var source = (string?)e.Attribute("SourceParamRefRef");
            if (target is null || !refs.ContainsKey(target) || source is not null && !refs.ContainsKey(source) || source is null && e.Attribute("Value") is null
                || watched.Contains(target) || source is not null && watched.Contains(source)) Fail(PropertyPatchIssueCode.UnsupportedDependency);
        }
    }
}
