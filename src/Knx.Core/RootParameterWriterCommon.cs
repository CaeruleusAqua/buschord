using System.Xml.Linq;

namespace Knx;

internal static class RootParameterWriterCommon
{
    internal static RootImageBuildPolicy ReadPolicy(XElement app, XElement? options, string id)
        => ReadPolicyProfile(app, options, id, false);
    internal static RootImageBuildPolicy ReadPolicyProfile(XElement app, XElement? options, string id, bool legacyM112)
    {
        var text = (string?)app.Attribute("MinEtsVersion"); uint packed = 0;
        if (text is not null)
        {
            Version? version = null;
            var numeric = legacyM112 && text == "3.0d" ? "3.0" : text;
            if (numeric.Length is < 3 or > 64 || numeric.Any(c => c != '.' && c is not (>= '0' and <= '9')) || !Version.TryParse(numeric, out version)
                || version.Major > 255 || version.Minor > 255 || version.Build > 65535 || version.Revision > 65535)
                RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidPolicy);
            packed = (uint)version!.Major << 24 | (uint)version.Minor << 16 | (uint)Math.Max(version.Build, 0);
        }
        if (options?.HasElements == true || options is not null && !string.IsNullOrWhiteSpace(options.Value)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidPolicy);
        var order = (string?)options?.Attribute("ParameterByteOrder");
        var byteOrder = order switch { null or "BigEndian" => ParameterPatchByteOrder.BigEndian, "LittleEndian" => ParameterPatchByteOrder.LittleEndian,
            _ => throw new RootBuildRejected(new(RootImageBuildIssueCode.InvalidPolicy)) };
        var declared = (string?)options?.Attribute("DownloadInvisibleParameters");
        var requested = declared switch { null or "DefaultValue" => RootParameterDownloadBehavior.DefaultValue,
            "None" => RootParameterDownloadBehavior.None, "Background" => RootParameterDownloadBehavior.Background,
            _ => throw new RootBuildRejected(new(RootImageBuildIssueCode.InvalidPolicy)) };
        var behavior = packed >= 0x05000000 && options is not null ? requested : RootParameterDownloadBehavior.Background;
        return new(byteOrder, behavior, id.StartsWith("M-009E_A-1000-01", StringComparison.Ordinal) ? RootParameterMaskPolicy.FieldBitsOnly : RootParameterMaskPolicy.WholeTouchedByte,
            text, packed, options is not null, declared, order);
    }

    internal static string Normalize(IReadOnlyDictionary<string, string> referenceIds, string key, string appId)
    {
        if (referenceIds.ContainsKey(key)) return key;
        var full = appId + "_" + key;
        if (referenceIds.ContainsKey(full)) return full;
        var prefix = appId + "_";
        if (key.StartsWith(prefix, StringComparison.Ordinal) && referenceIds.ContainsKey(key[prefix.Length..])) return key[prefix.Length..];
        RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier); return "";
    }
    internal static bool MatchesSaved(IReadOnlyDictionary<string, string> referenceIds, IReadOnlyDictionary<string, string> saved,
        IReadOnlyDictionary<string, string?> values, string appId)
    {
        if (values.Count > RootBuildGuard.Limit || !RootBuildGuard.Safe(appId)) return false;
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal); long characters = 0;
        foreach (var (key, value) in values)
        {
            if (!RootBuildGuard.Safe(key) || value is null || value.Length > 65536
                || (characters += key.Length + value.Length) > RootBuildGuard.TextLimit) return false;
            string reference;
            try { reference = Normalize(referenceIds, key, appId); }
            catch (RootBuildRejected) { return false; }
            if (normalized.TryGetValue(reference, out var previous) && !string.Equals(previous, value, StringComparison.Ordinal)) return false;
            normalized[reference] = value;
        }
        return normalized.Count == saved.Count && normalized.All(pair => saved.TryGetValue(pair.Key, out var savedValue)
            && string.Equals(savedValue, pair.Value, StringComparison.Ordinal));
    }
    internal static IReadOnlyDictionary<string, string> ReadSaved(IReadOnlyDictionary<string, string> referenceIds,
        IReadOnlyDictionary<string, RootParameterSource> sourceReferences, DeviceInstance device, string appId, RootImageBuildPolicy policy)
        => ReadSavedProfile(referenceIds, sourceReferences, device, appId, policy, allowRawData: false);

    internal static IReadOnlyDictionary<string, string> ReadSavedProfile(IReadOnlyDictionary<string, string> referenceIds,
        IReadOnlyDictionary<string, RootParameterSource> sourceReferences, DeviceInstance device, string appId, RootImageBuildPolicy policy,
        bool allowRawData)
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in device.ParameterValues)
        {
            var id = Normalize(referenceIds, key, appId);
            var source = sourceReferences[id];
            if (source.Location is not null)
            {
                if (allowRawData && source.ParsedType.DataType == ParameterDataType.RawData)
                {
                    _ = NativeRootRawDataCodec.Encode(source.ParsedType, value, policy.ByteOrder, id);
                    if (saved.TryGetValue(id, out var previous) && previous != value) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
                    saved[id] = value; continue;
                }
                if (value.Length > 64 || value.Any(char.IsControl)) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
                var encoded = OfflineParameterPatch.EncodeWord(source.Type.Elements().Single(), source.ParsedType, value, policy.ByteOrder, source.Location.BitOffset!.Value, id);
                if (encoded.Issue is not null) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id, parameter: encoded.Issue);
            }
            if (saved.TryGetValue(id, out var existing) && existing != value) RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidValue, id);
            saved[id] = value;
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(saved);
    }
    internal static string CaptureCarriers(IReadOnlyDictionary<string, string> referenceIds, Project project, DeviceInstance device, string appId)
        => CaptureCarriersProfile(referenceIds, project, device, appId, null);
    internal static string CaptureCarriersProfile(IReadOnlyDictionary<string, string> referenceIds, Project project, DeviceInstance device, string appId,
        IReadOnlyList<ApplicationModuleInstance>? moduleInstances)
    {
        if (project.OriginalXml?.Root is null) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var nodes = RootBuildGuard.Nodes(project.OriginalXml!, 128 * RootBuildGuard.Blob);
        var root = project.OriginalXml!.Root!; var projectNs = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || projectNs.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/22" or "http://knx.org/xml/project/23"))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var projectElement = RootBuildGuard.One(root, projectNs + "Project", true)!;
        if ((string?)projectElement.Attribute("Id") != project.Id) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var matches = nodes.Where(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == device.Id).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Name != projectNs + "DeviceInstance" || !DevicePath(matches[0], projectElement, projectNs))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var native = matches[0];
        var allowed = projectNs.NamespaceName == "http://knx.org/xml/project/23" ? new[] { projectNs, Knxproj.ExtensionNamespace } : new[] { projectNs };
        var carriers = native.Descendants().Where(e => e.Name.LocalName is "ParameterInstanceRefs" or "ParameterInstanceRef" or "ModuleInstances" or "ModuleInstance" or "BinaryData"
            or "ParameterValues" or "ParameterValue" or "ParameterInstances" or "ParameterInstance").ToArray();
        if (carriers.Length > RootBuildGuard.Limit) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
        long characters = 0; var ids = new HashSet<string>(StringComparer.Ordinal); var containers = new HashSet<XName>();
        var moduleIds = new HashSet<string>(StringComparer.Ordinal); var moduleHashes = new List<string>();
        foreach (var carrier in carriers)
        {
            foreach (var attribute in carrier.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                if (attribute.Name.ToString().Length > 512 || attribute.Value.Length > 65536
                    || (characters += attribute.Name.ToString().Length + attribute.Value.Length) > RootBuildGuard.TextLimit)
                    RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
            }
            if (!allowed.Contains(carrier.Name.Namespace)) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
            if (carrier.Name.LocalName == "ModuleInstance" && moduleInstances is not null)
            {
                if (carrier.Parent?.Name != carrier.Name.Namespace + "ModuleInstances" || !ReferenceEquals(carrier.Parent.Parent, native))
                    RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
                ValidateModuleCarrier(carrier, moduleInstances, moduleIds, ref characters);
                moduleHashes.Add(NativeGeneralHash.Document(carrier)); continue;
            }
            if (carrier.Name.LocalName is "ModuleInstance" or "BinaryData" or "ParameterValues" or "ParameterValue" or "ParameterInstances" or "ParameterInstance")
                RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
            if (carrier.Name.LocalName is "ParameterInstanceRefs" or "ModuleInstances")
            {
                if (!ReferenceEquals(carrier.Parent, native) || !containers.Add(carrier.Name) || carrier.Attributes().Any(a => !a.IsNamespaceDeclaration)
                    || carrier.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                    || carrier.Elements().Any(e => e.Name != carrier.Name.Namespace + (carrier.Name.LocalName == "ModuleInstances" && moduleInstances is not null ? "ModuleInstance" : "ParameterInstanceRef"))
                    || carrier.Name.LocalName == "ModuleInstances" && moduleInstances is null && carrier.HasElements)
                    RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
            }
            else
            {
                RootBuildGuard.Leaf(carrier);
                if (carrier.Parent?.Name != carrier.Name.Namespace + "ParameterInstanceRefs" || !ReferenceEquals(carrier.Parent.Parent, native)
                    || carrier.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("RefId" or "Value" or "Id")))
                    || carrier.Attribute("Value") is null || !ids.Add(Normalize(referenceIds, RootBuildGuard.Id(carrier, "RefId"), appId)))
                    RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
                if (carrier.Attribute("Id") is { } identity && !RootBuildGuard.Safe(identity.Value)) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
            }
        }
        if (moduleIds.Count != 0 && moduleIds.Count != moduleInstances!.Count) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var baseline = RootBuildGuard.Hash(new { carriers = carriers.Select(e => new { name = e.Name.ToString(), attributes = e.Attributes().Where(a => !a.IsNamespaceDeclaration)
            .Select(a => new { name = a.Name.ToString(), a.Value }).ToArray() }).ToArray(),
            device.Id, address = device.Address.Raw, device.ProductRefId, device.Hardware2ProgramRefId });
        return moduleInstances is null ? baseline : RootBuildGuard.Hash(new { profile = "FlatLiteralModuleCarriers1", baseline, modules = moduleHashes });
    }
    private static void ValidateModuleCarrier(XElement node, IReadOnlyList<ApplicationModuleInstance> expected, HashSet<string> ids, ref long characters)
    {
        var ns = node.Name.Namespace; var id = (string?)node.Attribute("Id");
        if (id is null || !ids.Add(id) || node.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
            || a.Name.LocalName is not ("Id" or "RefId" or "RepeatIndex"))) || node.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var instance = expected.SingleOrDefault(i => i.Id == id);
        if (instance is null || (string?)node.Attribute("RefId") != instance.RefId
            || node.Attribute("RepeatIndex") is { } repeat && repeat.Value != instance.RepeatIndex
            || node.Elements().Count() > 1 || node.Elements().Any(e => e.Name != ns + "Arguments"))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var container = node.Element(ns + "Arguments");
        if (container is not null && (container.Attributes().Any(a => !a.IsNamespaceDeclaration)
            || container.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        var arguments = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var argument in container?.Elements() ?? [])
        {
            RootBuildGuard.Leaf(argument);
            var reference = (string?)argument.Attribute("RefId"); var value = (string?)argument.Attribute("Value");
            if ((characters += (reference?.Length ?? 0) + (value?.Length ?? 0)) > RootBuildGuard.TextLimit)
                RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
            if (argument.Name != ns + "Argument" || reference is null || value is null || !RootBuildGuard.Safe(reference) || value.Length > 65536
                || argument.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("RefId" or "Value")))
                || arguments.Count >= 64 || !arguments.TryAdd(reference, value)) RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
        }
        if (arguments.Count != instance!.Arguments.Count || instance.Arguments.Any(a => !arguments.TryGetValue(a.RefId, out var value) || value != a.Value))
            RootBuildGuard.Fail(RootImageBuildIssueCode.UnsupportedNativeCarrier);
    }
    private static bool DevicePath(XElement device, XElement project, XNamespace ns)
    {
        var owner = device.Parent;
        if (owner?.Name == ns + "Segment")
        { if (ns.NamespaceName == "http://knx.org/xml/project/20") return false; owner = owner.Parent; }
        if (owner?.Name == ns + "Line") owner = owner.Parent;
        if (owner?.Name != ns + "Area" || owner.Parent?.Name != ns + "Topology") return false;
        var installation = owner.Parent.Parent;
        return installation?.Name == ns + "Installation" && installation.Parent?.Name == ns + "Installations"
            && ReferenceEquals(installation.Parent.Parent, project);
    }

    internal readonly record struct CurrentValue(string? Value, RootParameterValueOrigin Origin);
    internal readonly record struct ValueSelection(RootParameterDisposition Disposition, string? Value);

    internal static CurrentValue Hydrate(string reference, string? parameterValue, string? referenceValue,
        IReadOnlyDictionary<string, string> saved)
    {
        var current = parameterValue; var origin = RootParameterValueOrigin.Parameter;
        if (referenceValue is not null) { current = referenceValue; origin = RootParameterValueOrigin.Reference; }
        if (saved.TryGetValue(reference, out var value)) { current = value; origin = RootParameterValueOrigin.Saved; }
        return new(current, origin);
    }

    internal static ValueSelection ChooseValue(bool hasStorage, string? current, string? parameterDefault,
        bool active, bool always, RootParameterDownloadBehavior behavior)
    {
        if (!hasStorage) return new(RootParameterDisposition.NoStorage, null);
        if (active || always) return new(active ? RootParameterDisposition.ActiveValue : RootParameterDisposition.AlwaysValue, current);
        if (behavior == RootParameterDownloadBehavior.DefaultValue) return new(RootParameterDisposition.InactiveDefault, parameterDefault);
        return new(behavior == RootParameterDownloadBehavior.None
            ? RootParameterDisposition.InactiveNullNone : RootParameterDisposition.InactiveNullBackground, null);
    }
}
