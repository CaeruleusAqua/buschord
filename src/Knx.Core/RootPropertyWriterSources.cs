using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

internal sealed class RootPropertyBuildRejected(RootPropertyBuildIssue issue) : Exception
{ internal RootPropertyBuildIssue Issue => issue; }
internal static class RootPropertyBuildGuard
{
    internal static string Hash<T>(T value)
    {
        using var hash = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(sink, value); sink.FlushFinalBlock();
        return Convert.ToHexStringLower(hash.Hash!);
    }
    internal static string DocumentHash(XDocument document)
    {
        using var hash = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(sink, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false })) document.WriteTo(writer);
        sink.FlushFinalBlock(); return Convert.ToHexStringLower(hash.Hash!);
    }
    [DoesNotReturn]
    internal static void Fail(RootPropertyBuildIssueCode code, string? reference = null, string? property = null,
        ParameterPatchIssue? parameter = null, GeneratedTableIssue? activation = null, LoadProcedureIssue? procedure = null,
        PropertyInitializationIssue? initialization = null)
        => throw new RootPropertyBuildRejected(new(code, RootBuildGuard.Safe(reference) ? reference : null, property,
            ParameterIssue: parameter, ActivationIssue: activation, ProcedureIssue: procedure, InitializationIssue: initialization));
    internal static RootPropertyBuildIssue Adapt(RootImageBuildIssue issue)
    {
        var code = issue.Code switch
        {
            RootImageBuildIssueCode.InvalidRequest => RootPropertyBuildIssueCode.InvalidRequest,
            RootImageBuildIssueCode.SourceUnavailable => RootPropertyBuildIssueCode.SourceUnavailable,
            RootImageBuildIssueCode.UnsupportedApplication => RootPropertyBuildIssueCode.UnsupportedApplication,
            RootImageBuildIssueCode.UnsupportedProfile => RootPropertyBuildIssueCode.UnsupportedProfile,
            RootImageBuildIssueCode.UnsupportedType => RootPropertyBuildIssueCode.UnsupportedType,
            RootImageBuildIssueCode.InvalidValue => RootPropertyBuildIssueCode.InvalidValue,
            RootImageBuildIssueCode.InvalidPolicy => RootPropertyBuildIssueCode.InvalidPolicy,
            RootImageBuildIssueCode.AmbiguousOwnership => RootPropertyBuildIssueCode.AmbiguousOwnership,
            RootImageBuildIssueCode.LimitExceeded => RootPropertyBuildIssueCode.LimitExceeded,
            RootImageBuildIssueCode.InputsChanged => RootPropertyBuildIssueCode.InputsChanged,
            RootImageBuildIssueCode.UnsupportedNativeCarrier => RootPropertyBuildIssueCode.UnsupportedNativeCarrier,
            _ => RootPropertyBuildIssueCode.InvalidDeclaration,
        };
        return new(code, issue.RefId, ResourceId: issue.ResourceId, ParameterIssue: issue.ParameterIssue,
            ActivationIssue: issue.TableIssue, ProcedureIssue: issue.ProcedureIssue);
    }
}
internal sealed record RootPropertyOwner(string ParameterId, PropertySourceSelector RawSelector, PropertySourceSelector NativeSelector,
    string PropertyKey, uint ByteOffset, byte BitOffset, uint BitLength, PropertyPdtLayout PdtLayout, bool Referenced)
{
    internal ulong Start => (ulong)ByteOffset * 8 + BitOffset;
    internal ulong End => Start + BitLength;
}
internal sealed record RootPropertyPhysicalReference(string RefId, string ParameterId, string ParameterTypeId, RootPropertyOwner Owner);

internal sealed class RootPropertyWriterSources
{
    private readonly ArchivedImageSources.Inputs _inputs;
    private readonly string _applicationDocumentHash;
    private readonly string _masterDocumentHash;
    internal string ApplicationId { get; }
    internal string ApplicationDefinitionHash { get; }
    internal XElement Application { get; }
    internal string SourceHash { get; }
    internal RootImageBuildPolicy Policy { get; }
    internal IReadOnlyList<RootParameterSource> References { get; }
    internal IReadOnlyDictionary<string, RootParameterSource> ValidatedSourceMap { get; }
    internal IReadOnlyDictionary<string, string> ValidatedReferenceIds { get; }
    internal IReadOnlyDictionary<string, string> Saved { get; }
    internal string CarrierHash { get; }
    internal IReadOnlyList<RootPropertyPhysicalReference> PhysicalReferences { get; }
    internal IReadOnlyList<RootPropertyOwner> Owners { get; }
    internal ArchivedResourceContext ResourceContext { get; }
    internal ParameterProfileContext PropertyProfile { get; }
    internal string MaskVersionId { get; }
    internal int ParameterCount { get; }

    internal static RootPropertyWriterSources Read(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
    {
        try { return new(project, device, inputs); }
        catch (RootBuildRejected failure) { throw new RootPropertyBuildRejected(RootPropertyBuildGuard.Adapt(failure.Issue)); }
    }
    private RootPropertyWriterSources(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
    {
        _inputs = inputs;
        if (inputs.Manifest is not { Status: ArchivedImageSourceStatus.Inspected, Binding: not null, Hardware.Status: ArchivedHardwareSourceStatus.Inspected }
            || inputs.Application?.Root is null || inputs.Master?.Root is null)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.SourceUnavailable);
        var root = inputs.Application!.Root!; var ns = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedApplication);
        var nodes = RootBuildGuard.Nodes(inputs.Application, RootBuildGuard.TextLimit);
        _ = RootBuildGuard.Nodes(inputs.Master!, RootBuildGuard.TextLimit);
        _applicationDocumentHash = RootPropertyBuildGuard.DocumentHash(inputs.Application);
        _masterDocumentHash = RootPropertyBuildGuard.DocumentHash(inputs.Master!);
        if (_applicationDocumentHash != inputs.Manifest.ApplicationDefinitionHash)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
        if (nodes.Any(e => e.Name.Namespace != ns || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml)))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration);
        var apps = nodes.Where(e => e.Name == ns + "ApplicationProgram").Take(2).ToArray();
        if (apps.Length != 1 || (string?)apps[0].Attribute("Id") != inputs.Manifest.ApplicationId)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration);
        Application = apps[0]; ApplicationId = RootBuildGuard.Id(Application); ApplicationDefinitionHash = inputs.Manifest.ApplicationDefinitionHash!;
        if (KnownDcaRequirementsRegistry.RequiresDca(ApplicationId) || nodes.Any(e =>
            e.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware"
                && !string.IsNullOrWhiteSpace(a.Value) && a.Value is not ("false" or "False" or "0"))
            || e.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler" or "BinaryDataRef"
            || e.Name.LocalName == "Extension" && (e.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
                || e.Elements().Any(c => c.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
            || e.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !string.IsNullOrWhiteSpace((string?)e.Attribute("Plugin"))))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.ExternalConfigurationUnaccounted);
        if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnaccountedBinaryData);
        if (nodes.Any(e => e.Name.LocalName == "Memory")) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedPhysicalAlias);
        if (nodes.Any(e => e.Name.LocalName is "Module" or "ModuleDef" or "SubModuleDefs" or "Repeat" or "Union" or "Allocator" or "Assign" or "ParameterCalculation" or "ParameterInstance" or "Fixup" or "ApplicationFixUp"
            || e.Attributes().Any(a => a.Name.LocalName is "BaseValue" or "AllocatorRefId" or "BaseOffset" or "BaseIndex" or "BaseOccurrence")))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedProfile);
        var stat = RootBuildGuard.One(Application, ns + "Static", true)!;
        var parameters = Index(RootBuildGuard.One(stat, ns + "Parameters"), ns + "Parameter");
        var references = Index(RootBuildGuard.One(stat, ns + "ParameterRefs"), ns + "ParameterRef");
        var types = Index(RootBuildGuard.One(stat, ns + "ParameterTypes"), ns + "ParameterType"); ParameterCount = parameters.Count;
        var known = parameters.Values.Concat(references.Values).Concat(types.Values).ToHashSet();
        if (nodes.Any(e => e.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !known.Contains(e))
            || nodes.Any(e => e.Name.LocalName == "Options" && !ReferenceEquals(e.Parent, stat)))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration);
        Policy = RootParameterWriterCommon.ReadPolicy(Application, RootBuildGuard.One(stat, ns + "Options"), ApplicationId);
        var parsedTypes = new Dictionary<string, ParameterType>(StringComparer.Ordinal); var choices = 0;
        foreach (var (id, declaration) in types)
        {
            var shapes = declaration.Elements().ToArray();
            if (shapes.Length != 1 || shapes[0].Name.Namespace != ns || declaration.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration);
            var shape = shapes[0];
            if (shape.Name.LocalName == "TypeRawData") RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedType);
            if (shape.Name.LocalName == "TypeRestriction")
            {
                var count = shape.Elements().Take(4097).Count(); choices = checked(choices + count);
                if (count > 4096 || choices > 65536) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded);
                foreach (var choice in shape.Elements()) _ = RootBuildGuard.Id(choice);
            }
            else RootBuildGuard.Leaf(shape);
            if (shape.Name.LocalName is "TypeRestriction" or "TypeFloat" or "TypeTime"
                && OfflineParameterPatch.PreflightWordDeclaration(shape) is { } issue)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedType, parameter: issue);
            var parsed = Ksxml.ParseParameterType(declaration);
            if (parsed is null || parsed.UnsupportedReason is not null || parsed.DataType == ParameterDataType.None)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedType);
            parsedTypes.Add(id, parsed!);
        }
        var code = RootBuildGuard.One(stat, ns + "Code");
        if (nodes.Any(e => e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment" || e.Name.LocalName == "Code" && !ReferenceEquals(e, code))
            || code is not null && (code.HasElements || !string.IsNullOrWhiteSpace(code.Value) || code.Attributes().Any(a => !a.IsNamespaceDeclaration)))
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedProfile);
        MaskVersionId = RootBuildGuard.Id(Application, "MaskVersion");
        ResourceContext = ArchivedResourceContext.Read(inputs.Master, MaskVersionId, ns.NamespaceName);
        if (inputs.Manifest.ResourceContext is not { } originalResources || ResourceContext.Fingerprint != originalResources.Fingerprint
            || ResourceContext.DocumentHash != originalResources.DocumentHash)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
        if (ResourceContext is not { Status: ArchivedResourceContextStatus.Supported, LegacyVersion: 0 }
            || ResourceContext.MaskVersionId != MaskVersionId || ResourceContext.MemoryRegions.Count != 0)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedProfile);
        PropertyProfile = ParameterProfileContext.Read(inputs.Master, MaskVersionId).ForApplication(MaskVersionId, ns.NamespaceName);
        if (PropertyProfile.Selection is not { Status: MaskProfileStatus.Supported, FirstAppObjectIndex: not null, LegacyVersion: 0 }
            || PropertyProfile.Selection.MaskVersionId != MaskVersionId
            || PropertyProfile.Selection.FirstAppObjectIndex != ResourceContext.FirstAppObjectIndex)
            RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedProfile);
        var storage = new ParameterStorageReader(root, PropertyProfile);
        var aliases = references.Values.GroupBy(r => RootBuildGuard.Id(r, "RefId"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (aliases.Values.Any(c => c != 1)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.AmbiguousOwnership);
        var locations = new Dictionary<string, ParameterStorageLocation?>(StringComparer.Ordinal);
        var owners = new List<RootPropertyOwner>(); var ownerByParameter = new Dictionary<string, RootPropertyOwner>(StringComparer.Ordinal);
        foreach (var (id, parameter) in parameters)
        {
            if (((string?)parameter.Attribute("Value"))?.Length > 65536) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded, id);
            var typeId = RootBuildGuard.Id(parameter, "ParameterType");
            ParameterType? type = null;
            if (parameter.Attribute("ParameterTypeId") is not null || !parsedTypes.TryGetValue(typeId, out type))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration, id);
            if (parameter.Elements().Any(e => e.Name != ns + "Property") || parameter.Elements().Count() > 1
                || parameter.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                || parameter.Attributes().Any(a => a.Name.LocalName is "Offset" or "BitOffset"))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedProfile, id);
            var property = parameter.Element(ns + "Property");
            if (property is null) { locations.Add(id, null); continue; }
            RootBuildGuard.Leaf(property);
            if (property.Attribute("PropertyId") is null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration, id);
            if (type!.DataType is not (ParameterDataType.Number or ParameterDataType.Enumeration) || type.SizeInBit is < 1 or > 32)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedType, id);
            var location = storage.Read(parameter, null, type);
            if (location is not { Status: ParameterStorageStatus.Supported, StartAddress: not null, BitOffset: not null,
                    Footprint: { Status: ParameterFootprintStatus.Known, BitLength: > 0 } }
                || location.Kind is not (ParameterStorageKind.PropertyByIndex or ParameterStorageKind.PropertyByType))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnknownStorage, id);
            var raw = RawSelector(location); var native = NativeSelector(raw);
            var pdt = PropertyPdtRoles.Read(PropertyProfile.Interpret(location, MaskVersionId));
            if (pdt.Status != PropertyPdtStatus.KnownSymmetric || pdt.WriteElementBytes is not > 0)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedProfile, id);
            var underlying = (string?)parameter.Attribute("Value");
            if (underlying is null || underlying.Length > 64 || underlying.Any(char.IsControl)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id);
            var encoding = OfflineParameterPatch.EncodeWord(types[typeId].Elements().Single(), type, underlying, Policy.ByteOrder, location.BitOffset!.Value, id);
            if (encoding.Issue is not null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.UnsupportedType, id, parameter: encoding.Issue);
            if (encoding.Bits != location.Footprint!.BitLength) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration, id);
            var owner = new RootPropertyOwner(id, raw, native, ArchivedPropertySources.Key(native), location.StartAddress!.Value,
                location.BitOffset.Value, location.Footprint.BitLength!.Value, pdt, aliases.ContainsKey(id));
            if (owner.End > ((ulong)uint.MaxValue + 1) * 8) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.FieldOutOfRange, id);
            owners.Add(owner); ownerByParameter.Add(id, owner); locations.Add(id, location);
        }
        foreach (var family in owners.GroupBy(o => o.NativeSelector.PropertyId))
            if (family.Any(o => o.NativeSelector.Kind == PropertySourceKind.ByType) && family.Any(o => o.NativeSelector.Kind != PropertySourceKind.ByType))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.AmbiguousOwnership);
        foreach (var group in owners.GroupBy(o => o.PropertyKey, StringComparer.Ordinal))
        {
            ulong end = 0; var first = true;
            foreach (var owner in group.OrderBy(o => o.Start).ThenBy(o => o.End))
            { if (!first && owner.Start < end) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.AmbiguousOwnership, owner.ParameterId, owner.PropertyKey); end = owner.End; first = false; }
        }
        Owners = Array.AsReadOnly(owners.ToArray());
        var sourceList = new List<RootParameterSource>(); var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceMap = new Dictionary<string, RootParameterSource>(StringComparer.Ordinal); var physical = new List<RootPropertyPhysicalReference>();
        foreach (var (id, reference) in references)
        {
            RootBuildGuard.Leaf(reference);
            if (((string?)reference.Attribute("Value"))?.Length > 65536) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded, id);
            var target = RootBuildGuard.Id(reference, "RefId");
            if (!parameters.TryGetValue(target, out var parameter) || reference.Attribute("ParameterType") is not null || reference.Attribute("ParameterTypeId") is not null)
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration, id);
            var typeId = RootBuildGuard.Id(parameter!, "ParameterType");
            var source = new RootParameterSource(parameter!, reference, types[typeId], parsedTypes[typeId], locations[target],
                (string?)parameter!.Attribute("Value"), (string?)reference.Attribute("Value"), RootBuildGuard.Bool(parameter, "LegacyPatchAlways"),
                reference.Attribute("CustomerAdjustable") is null ? RootBuildGuard.Bool(parameter, "CustomerAdjustable") : RootBuildGuard.Bool(reference, "CustomerAdjustable"));
            if (source.Location is not null && source.ReferenceValue is { } declared)
            {
                if (declared.Length > 64 || declared.Any(char.IsControl)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id);
                var encoded = OfflineParameterPatch.EncodeWord(source.Type.Elements().Single(), source.ParsedType, declared, Policy.ByteOrder, source.Location.BitOffset!.Value, id);
                if (encoded.Issue is not null) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidValue, id, parameter: encoded.Issue);
            }
            ids.Add(id, id); sourceMap.Add(id, source); sourceList.Add(source);
            if (ownerByParameter.TryGetValue(target, out var owner)) physical.Add(new(id, target, typeId, owner));
        }
        References = Array.AsReadOnly(sourceList.ToArray()); ValidatedSourceMap = new ReadOnlyDictionary<string, RootParameterSource>(sourceMap);
        ValidatedReferenceIds = new ReadOnlyDictionary<string, string>(ids); PhysicalReferences = Array.AsReadOnly(physical.ToArray());
        Saved = RootParameterWriterCommon.ReadSaved(ValidatedReferenceIds, ValidatedSourceMap, device, ApplicationId, Policy);
        long characters = 0;
        foreach (var source in References)
        {
            var id = RootBuildGuard.Id(source.Reference);
            var current = RootParameterWriterCommon.Hydrate(id, source.ParameterValue, source.ReferenceValue, Saved).Value;
            if ((characters += id.Length + (current?.Length ?? 0)) > RootBuildGuard.TextLimit) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.LimitExceeded);
        }
        CarrierHash = CaptureCarriers(project, device, ApplicationId);
        SourceHash = RootPropertyBuildGuard.Hash(new { version = 1, ApplicationDefinitionHash, resource = ResourceContext.Fingerprint,
            profile = PropertyProfile.Selection.Fingerprint, policy = Policy.Fingerprint,
            parameters = parameters.Keys.ToArray(), references = references.Keys.ToArray(), types = types.Keys.ToArray(), owners });
        if (!MatchesSources(inputs)) RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InputsChanged);
    }
    internal bool BelongsTo(ArchivedImageSources.Inputs inputs) => ReferenceEquals(_inputs, inputs);
    internal bool MatchesSources(ArchivedImageSources.Inputs inputs)
    {
        if (!BelongsTo(inputs) || inputs.Application is null || inputs.Master is null) return false;
        try
        {
            _ = RootBuildGuard.Nodes(inputs.Application, RootBuildGuard.TextLimit); _ = RootBuildGuard.Nodes(inputs.Master, RootBuildGuard.TextLimit);
            return RootPropertyBuildGuard.DocumentHash(inputs.Application) == _applicationDocumentHash
                && RootPropertyBuildGuard.DocumentHash(inputs.Master) == _masterDocumentHash;
        }
        catch (RootBuildRejected) { return false; }
    }
    internal bool MatchesSaved(IReadOnlyDictionary<string, string?> captured, string appId)
        => appId == ApplicationId && RootParameterWriterCommon.MatchesSaved(ValidatedReferenceIds, Saved, captured, appId);
    internal string CaptureCarriers(Project project, DeviceInstance device, string appId)
        => RootParameterWriterCommon.CaptureCarriers(ValidatedReferenceIds, project, device, appId);
    internal PropertySourceSelector NativeSelector(PropertySourceSelector raw)
    {
        if (raw.Kind != PropertySourceKind.SystemProperty) return raw;
        var first = PropertyProfile.Selection.FirstAppObjectIndex!.Value;
        return raw.NativeObjectIndex!.Value >= first
            ? new(PropertySourceKind.AppProperty, raw.PropertyId, ApplicationObjectIndex: checked((ushort)(raw.NativeObjectIndex.Value - first))) : raw;
    }
    private static PropertySourceSelector RawSelector(ParameterStorageLocation location) => location.Kind switch
    {
        ParameterStorageKind.PropertyByIndex when location.ObjectIndex is { } index && location.PropertyId is { } pid
            => new(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: index),
        ParameterStorageKind.PropertyByType when location.ObjectType is { } type && location.Occurrence is { } occurrence && location.PropertyId is { } pid
            => new(PropertySourceKind.ByType, pid, ObjectType: type, Occurrence: occurrence),
        _ => throw new RootPropertyBuildRejected(new(RootPropertyBuildIssueCode.UnknownStorage)),
    };
    private static Dictionary<string, XElement> Index(XElement? container, XName name)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in container?.Elements() ?? [])
            if (element.Name != name || result.Count >= RootBuildGuard.Limit || !result.TryAdd(RootBuildGuard.Id(element), element))
                RootPropertyBuildGuard.Fail(RootPropertyBuildIssueCode.InvalidDeclaration);
        return result;
    }
}
