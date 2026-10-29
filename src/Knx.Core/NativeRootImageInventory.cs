using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

internal sealed partial class NativeRootImageInventory
{
    private readonly Project? _project;
    private readonly DeviceInstance? _device;
    private readonly ArchivedImageSources.Inputs? _inputs;
    private readonly string? _masterHash;
    private readonly XDocument? _hardwareDocument;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RootParameterSource> _sources = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _saved = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    internal bool IsAvailable => Issues.Count == 0 && SourceHash is not null;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; } = Array.Empty<NativeGeneralIssue>();
    internal string? ApplicationId { get; }
    internal string? MaskVersionId { get; }
    internal string? ApplicationDefinitionHash { get; }
    internal string? SourceFingerprint { get; }
    internal string? SourceHash { get; }
    internal string? SavedValuesHash { get; }
    internal string? CarrierHash { get; }
    internal string? GeometryHash { get; }
    internal string? HardwareDefinitionHash { get; }
    internal bool PrimaryProgramOnly { get; }
    internal bool AdmitRootUnions { get; }
    internal IReadOnlyList<NativeRootUnion> Unions { get; private set; } = Array.Empty<NativeRootUnion>();
    internal Dictionary<string, string> UnionByParameter { get; } = new(StringComparer.Ordinal);
    internal bool FactoryDeclared { get; }
    internal NativeRootSourcePolicy? Policy { get; }
    internal NativeRootCodecFacts? CodecFacts { get; }
    internal ArchivedResourceContext? ResourceContext { get; }
    internal ParameterProfileContext? PropertyProfile { get; }
    internal ArchivedFactoryImageSource? FactorySources { get; }
    internal IReadOnlyList<NativeRootReference> References { get; } = Array.Empty<NativeRootReference>();
    internal IReadOnlyList<NativeRootOwner> Owners { get; } = Array.Empty<NativeRootOwner>();
    internal IReadOnlyList<ImageCompositionSourceSegment> CodeSources { get; } = Array.Empty<ImageCompositionSourceSegment>();
    internal long CodeSourcePayloadBytes => CodeSources.Sum(s => (long)(s.RawData?.Length ?? 0) + (s.RawMask?.Length ?? 0));
    internal long CodeSourcePeakPayloadBytes => checked(CodeSourcePayloadBytes * 2);
    // Includes decoded/transient/framed/immutable copies and the three hydration encodings.
    // Encoded text is an upper bound on decoded length, including accepted whitespace.
    internal long RawDataPeakPayloadBytes => _sources.Where(p => p.Value.ParsedType.DataType == ParameterDataType.RawData)
        .Sum(p => checked(12L * (4 + (p.Value.ParameterValue?.Length ?? 0)
            + Math.Max(p.Value.ReferenceValue?.Length ?? 0, _saved.GetValueOrDefault(p.Key)?.Length ?? 0))));
    internal long PeakPayloadBytes => checked(CodeSourcePeakPayloadBytes + RawDataPeakPayloadBytes);

    private NativeRootImageInventory(NativeGeneralIssue issue) { Issues = Array.AsReadOnly(new[] { issue }); }
    internal static NativeRootImageInventory Read(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
        => ReadProfile(project, device, inputs, false);
    internal static NativeRootImageInventory ReadProfile(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, bool admitRootUnions)
    {
        try { return new(project, device, inputs, admitRootUnions); }
        catch (NativeGeneralRejected failure) { return new(failure.Issue); }
        catch (RootBuildRejected failure) { return new(Adapt(failure.Issue)); }
        catch (Exception failure) when (failure is XmlException or IOException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return new(new(NativeGeneralIssueCode.InvalidDeclaration)); }
    }

    private NativeRootImageInventory(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, bool admitRootUnions)
    {
        AdmitRootUnions = admitRootUnions;
        _project = project; _device = device; _inputs = inputs;
        if (inputs is null || project is null || device is null || !RootBuildGuard.Safe(project.Id) || !RootBuildGuard.Safe(device.Id)
            || project.Guid is { } guid && !RootBuildGuard.Safe(guid)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidRequest);
        CheckSavedBudget(device.ParameterValues);
        if (inputs.Manifest is not { Status: ArchivedImageSourceStatus.Inspected, Binding: not null, Hardware.Status: ArchivedHardwareSourceStatus.Inspected }
            || inputs.Application?.Root is null || inputs.Master?.Root is null || !inputs.Manifest.Binding.MatchesIdentity(project, device))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.SourceUnavailable);
        var root = inputs.Application!.Root!; var ns = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedApplication);
        var nodes = RootBuildGuard.Nodes(inputs.Application, TextLimit); _ = RootBuildGuard.Nodes(inputs.Master!, TextLimit);
        ApplicationDefinitionHash = NativeGeneralHash.Document(inputs.Application); _masterHash = NativeGeneralHash.Document(inputs.Master!);
        if (ApplicationDefinitionHash != inputs.Manifest.ApplicationDefinitionHash) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
        SourceFingerprint = inputs.Manifest.Binding.Fingerprint;
        if (nodes.Any(e => e.Name.Namespace != ns || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml)))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var apps = nodes.Where(e => e.Name == ns + "ApplicationProgram").Take(2).ToArray();
        if (apps.Length != 1 || (string?)apps[0].Attribute("Id") != inputs.Manifest.ApplicationId) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var app = apps[0]; ApplicationId = RootBuildGuard.Id(app); MaskVersionId = RootBuildGuard.Id(app, "MaskVersion");
        if (KnownDcaRequirementsRegistry.RequiresDca(ApplicationId) || nodes.Any(External)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ExternalConfigurationUnaccounted);
        if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnaccountedBinaryData);
        if (admitRootUnions && MaskVersionId != "MV-0701") NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedApplication);
        if (nodes.Any(e => e.Name.LocalName is "Module" or "ModuleDef" or "SubModuleDefs" or "Repeat" or "Allocator" or "Assign" or "ParameterCalculation" or "ParameterInstance"
            || e.Name.LocalName == "Union" && !admitRootUnions
            || e.Attributes().Any(a => a.Name.LocalName is "BaseValue" or "AllocatorRefId" or "BaseOffset" or "BaseIndex" or "BaseOccurrence")))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        var stat = RootBuildGuard.One(app, ns + "Static", true)!;
        var parameters = admitRootUnions ? IndexUnionParameters(RootBuildGuard.One(stat, ns + "Parameters"), ns)
            : Index(RootBuildGuard.One(stat, ns + "Parameters"), ns + "Parameter");
        var references = Index(RootBuildGuard.One(stat, ns + "ParameterRefs"), ns + "ParameterRef");
        var types = Index(RootBuildGuard.One(stat, ns + "ParameterTypes"), ns + "ParameterType", admitRootUnions);
        var known = parameters.Values.Concat(references.Values).Concat(types.Values).ToHashSet();
        var storageLeaves = parameters.Values.SelectMany(p => p.Elements()).Where(e => e.Name == ns + "Memory" || e.Name == ns + "Property").ToHashSet();
        if (admitRootUnions) storageLeaves.UnionWith(parameters.Values.Select(p => p.Parent).Where(p => p?.Name == ns + "Union").SelectMany(p => p!.Elements(ns + "Memory")));
        if (nodes.Any(e => e.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !known.Contains(e))
            || nodes.Any(e => e.Name.LocalName is "Memory" or "Property" && !storageLeaves.Contains(e))
            || nodes.Any(e => e.Name.LocalName == "Options" && !ReferenceEquals(e.Parent, stat))) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        var legacy = RootParameterWriterCommon.ReadPolicyProfile(app, RootBuildGuard.One(stat, ns + "Options"), ApplicationId, admitRootUnions);
        Policy = new(legacy.ByteOrder, legacy.DownloadBehavior, legacy.MaskPolicy, legacy.MinEtsVersion, legacy.PackedMinEtsVersion,
            legacy.OptionsDeclared, legacy.DeclaredDownloadBehavior, legacy.DeclaredByteOrder);
        var parsed = ParseTypes(types, ns, admitRootUnions);
        var code = RootBuildGuard.One(stat, ns + "Code"); var codeNodes = code?.Elements().ToHashSet() ?? [];
        if (nodes.Any(e => e.Name.LocalName == "Code" && !ReferenceEquals(e, code)
            || e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment" && !codeNodes.Contains(e))) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration);
        CodeSources = Array.AsReadOnly(ReadCode(code, ns));
        var segmentsById = CodeSources.ToDictionary(s => s.SourceId, StringComparer.Ordinal);
        ResourceContext = ArchivedResourceContext.Read(inputs.Master, MaskVersionId, ns.NamespaceName);
        if (ResourceContext is not { Status: ArchivedResourceContextStatus.Supported, LegacyVersion: 0 }
            || ResourceContext.MaskVersionId != MaskVersionId) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        if (inputs.Manifest.ResourceContext is not { } resource || resource.Fingerprint != ResourceContext.Fingerprint || resource.DocumentHash != ResourceContext.DocumentHash)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
        PropertyProfile = ParameterProfileContext.Read(inputs.Master, MaskVersionId).ForApplication(MaskVersionId, ns.NamespaceName);
        var storage = new ParameterStorageReader(root, PropertyProfile);
        var aliases = references.Values.GroupBy(e => RootBuildGuard.Id(e, "RefId"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (!admitRootUnions && aliases.Values.Any(c => c > 1)) NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
        var owners = new List<NativeRootOwner>(); var byParameter = new Dictionary<string, NativeRootOwner>(StringComparer.Ordinal);
        var locations = new Dictionary<string, ParameterStorageLocation?>(StringComparer.Ordinal); var ordinal = 0;
        var singleFields = 0; var packedFields = 0; var denseFields = 0; var rawFields = 0;
        foreach (var (id, parameter) in parameters)
        {
            var declarationOrdinal = ordinal++; var typeId = SourceId(parameter, "ParameterType", admitRootUnions);
            parsed.TryGetValue(typeId, out var type);
            if (parameter.Attribute("ParameterTypeId") is not null || type is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
            var unionMember = admitRootUnions && parameter.Parent?.Name == ns + "Union";
            if (parameter.Elements().Any(e => e.Name != ns + "Memory" && e.Name != ns + "Property") || parameter.Elements().Take(2).Count() > 1
                || parameter.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)) || !unionMember && parameter.Attributes().Any(a => a.Name.LocalName is "Offset" or "BitOffset"))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource, id);
            var raw = type!.DataType == ParameterDataType.RawData;
            CheckValue((string?)parameter.Attribute("Value"), id, raw);
            var leaf = unionMember ? parameter.Parent!.Element(ns + "Memory") : parameter.Elements().SingleOrDefault();
            if (leaf is null)
            {
                if (type!.DataType is ParameterDataType.Float or ParameterDataType.Time or ParameterDataType.RawData)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedType, id);
                locations.Add(id, null); continue;
            }
            RootBuildGuard.Leaf(leaf);
            if (leaf.Name.LocalName == "Memory" && leaf.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("CodeSegment" or "Offset" or "BitOffset"))))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
            var location = storage.Read(parameter, null, type!);
            if (raw)
            {
                if (unionMember || leaf.Name.LocalName != "Memory" || location.Status != ParameterStorageStatus.Supported
                    || location.BitOffset != 0 || location.Kind is not (ParameterStorageKind.AbsoluteMemory or ParameterStorageKind.RelativeMemory))
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
                var capacity = checked((uint)type.RawDataMaxSize!.Value + 4);
                location = location with { Footprint = new(ParameterFootprintStatus.Known, capacity * 8, capacity) };
            }
            if (location is not { Status: ParameterStorageStatus.Supported, StartAddress: not null, BitOffset: not null, Footprint: { Status: ParameterFootprintStatus.Known, BitLength: > 0 } }
                || !raw && location.Footprint.BitLength > 32)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
            var mapped = Map(location, id); var byteSpan = checked((int)((location.BitOffset!.Value + location.Footprint!.BitLength!.Value + 7) / 8));
            if (!mapped.Target.IsValid || (ulong)location.StartAddress!.Value + (uint)byteSpan > (ulong)uint.MaxValue + 1)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
            if (leaf.Name.LocalName == "Memory")
            {
                segmentsById.TryGetValue(location.CodeSegmentId!, out var segment);
                if (segment is null || Target(segment.Arena) != mapped.Target || location.StartAddress!.Value < segment.ByteOffsetInArena
                    || (ulong)location.StartAddress.Value + (uint)byteSpan > (ulong)segment.ByteOffsetInArena + (uint)segment.AllocationLength)
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.UncoveredOwner, id);
            }
            var value = (string?)parameter.Attribute("Value");
            if (value is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, id);
            if (raw) { _ = NativeRootRawDataCodec.Encode(type, value!, Policy!.ByteOrder, id); rawFields++; }
            else
            {
                var word = Encode(types[typeId], type!, value!, location.BitOffset.Value, id);
                if (word.Bits != location.Footprint.BitLength) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
                if (word.Single) singleFields++;
                if (word.Packed) packedFields++;
                if (word.Dense) denseFields++;
            }
            var declarationHash = NativeGeneralHash.Document(parameter);
            var owner = new NativeRootOwner(id, declarationOrdinal, declarationHash, mapped.Target, location.StartAddress.Value,
                location.BitOffset.Value, location.Footprint.BitLength.Value, aliases.ContainsKey(id), mapped.Raw, mapped.Pdt,
                NativeGeneralHash.Of(new { id, declarationHash }), NativeGeneralHash.Of(new { mapped.Target, location.StartAddress, location.BitOffset, location.Footprint, mapped.Pdt }));
            owners.Add(owner); byParameter.Add(id, owner); locations.Add(id, location);
        }
        if (admitRootUnions) ReadUnionLayouts(parameters, byParameter, types, parsed);
        CheckOwners(owners); Owners = Array.AsReadOnly(owners.ToArray());
        CodecFacts = new(Owners.Count, singleFields, packedFields, denseFields, rawFields);
        var result = new List<NativeRootReference>(); ordinal = 0;
        foreach (var (id, reference) in references)
        {
            RootBuildGuard.Leaf(reference); var parameterId = RootBuildGuard.Id(reference, "RefId");
            if (!parameters.TryGetValue(parameterId, out var parameter) || reference.Attribute("ParameterType") is not null || reference.Attribute("ParameterTypeId") is not null)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidDeclaration, id);
            var typeId = SourceId(parameter!, "ParameterType", admitRootUnions);
            var raw = parsed[typeId].DataType == ParameterDataType.RawData;
            CheckValue((string?)reference.Attribute("Value"), id, raw);
            var source = new RootParameterSource(parameter!, reference, types[typeId], parsed[typeId], locations[parameterId],
                (string?)parameter!.Attribute("Value"), (string?)reference.Attribute("Value"), RootBuildGuard.Bool(parameter, "LegacyPatchAlways"),
                reference.Attribute("CustomerAdjustable") is null ? RootBuildGuard.Bool(parameter, "CustomerAdjustable") : RootBuildGuard.Bool(reference, "CustomerAdjustable"));
            if (source.Location is not null && source.ReferenceValue is { } value)
            {
                if (raw) _ = NativeRootRawDataCodec.Encode(source.ParsedType, value, Policy!.ByteOrder, id);
                else _ = Encode(source.Type, source.ParsedType, value, source.Location.BitOffset!.Value, id);
            }
            _ids.Add(id, id); _sources.Add(id, source);
            result.Add(new(id, parameterId, typeId, ordinal++, byParameter.GetValueOrDefault(parameterId), source.Always, source.Customer, NativeGeneralHash.Document(reference)));
        }
        References = Array.AsReadOnly(result.ToArray());
        _saved = RootParameterWriterCommon.ReadSavedProfile(_ids, _sources, device, ApplicationId, legacy, allowRawData: true);
        SavedValuesHash = NativeGeneralHash.Of(_saved.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
        CarrierHash = RootParameterWriterCommon.CaptureCarriers(_ids, project, device, ApplicationId);
        _hardwareDocument = Knxproj.LoadHardwareData(project, device.Hardware2ProgramRefId!);
        if (_hardwareDocument is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.SourceUnavailable);
        HardwareDefinitionHash = ValidateCapturedHardware(_hardwareDocument!, device, inputs); PrimaryProgramOnly = true;
        FactorySources = inputs.Manifest.Hardware.FactoryImage; FactoryDeclared = inputs.Manifest.Hardware.LoadedImageDeclared;
        GeometryHash = NativeGeneralHash.Of(Owners);
        var baselineSourceHash = NativeGeneralHash.Of(new { version = 1, ApplicationDefinitionHash, master = _masterHash, SourceFingerprint, HardwareDefinitionHash,
            policy = Policy.Fingerprint, resources = ResourceContext.Fingerprint, property = PropertyProfile.Selection.Fingerprint,
            references = References, owners = Owners, code = CodeSources.Select(s => new { s.SourceId, s.Arena, s.ByteOffsetInArena, s.DeclaredSize,
                data = s.RawData?.Sha256, mask = s.RawMask?.Sha256 }).ToArray() });
        SourceHash = CodecFacts.HasExtendedCodecs
            ? NativeGeneralHash.Of(new { profile = 1, baselineSourceHash, codecFacts = CodecFacts.Fingerprint })
            : baselineSourceHash;
        if (admitRootUnions) SourceHash = NativeGeneralHash.Of(new { profile = "M112RootUnions1", source = SourceHash, unions = Unions });
        if (!MatchesCurrent(project, device, inputs)) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
    }

    internal bool BelongsTo(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
        => IsAvailable && ReferenceEquals(project, _project) && ReferenceEquals(device, _device) && ReferenceEquals(inputs, _inputs);
    internal bool MatchesCurrent() => _project is not null && _device is not null && _inputs is not null && MatchesCurrent(_project, _device, _inputs);
    internal bool MatchesApplication(NativeApplicationEvaluationContext context)
        => _project is not null && _device is not null && _inputs is not null && context.MatchesInputs(_project, _device, _inputs);
    internal bool MatchesPreparation(NativeImageVariantPreparation preparation)
        => _project is not null && _device is not null && _inputs is not null && preparation.BelongsTo(_project, _device, _inputs, this);
    internal bool MatchesCurrent(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
    {
        if (!BelongsTo(project, device, inputs) || inputs.Application is null || inputs.Master is null || inputs.Manifest.Binding is null) return false;
        try
        {
            CheckSavedBudget(device.ParameterValues); _ = RootBuildGuard.Nodes(inputs.Application, TextLimit); _ = RootBuildGuard.Nodes(inputs.Master, TextLimit);
            return inputs.Manifest.Binding.MatchesIdentity(project, device) && NativeGeneralHash.Document(inputs.Application) == ApplicationDefinitionHash
                && NativeGeneralHash.Document(inputs.Master) == _masterHash && MatchesSaved(device.ParameterValues.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal))
                && RootParameterWriterCommon.CaptureCarriers(_ids, project, device, ApplicationId!) == CarrierHash
                && _hardwareDocument is not null && ValidateCapturedHardware(_hardwareDocument, device, inputs) == HardwareDefinitionHash;
        }
        catch (Exception failure) when (failure is NativeGeneralRejected or RootBuildRejected or XmlException or IOException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException) { return false; }
    }
    internal bool MatchesSaved(IReadOnlyDictionary<string, string?> captured)
    {
        if (!IsAvailable) return false;
        try { CheckSavedBudget(captured); return RootParameterWriterCommon.MatchesSaved(_ids, _saved, captured, ApplicationId!); }
        catch (NativeGeneralRejected) { return false; }
    }
    internal IReadOnlyList<NativeRootHydratedReference> Hydrate(NativeApplicationEvaluationContext context)
    {
        var result = new List<NativeRootHydratedReference>();
        foreach (var reference in References)
        {
            var source = _sources[reference.RefId]; var current = RootParameterWriterCommon.Hydrate(reference.RefId, source.ParameterValue, source.ReferenceValue, _saved);
            if (!context.EffectiveValues.TryGetValue(reference.RefId, out var evaluated)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch, reference.RefId);
            ParameterWordEncoding? word = null, fallback = null;
            ImmutablePatchBytes? bytes = null, defaultBytes = null;
            if (reference.Owner is null)
            {
                var placeholder = AdmitRootUnions && source.ParsedType.DataType == ParameterDataType.None
                    && string.IsNullOrEmpty(current.Value) && string.IsNullOrEmpty(evaluated);
                if (!placeholder && (current.Value != evaluated || current.Value is not null && !ParameterValidation.TryValidate(source.ParsedType, current.Value, out _)))
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, reference.RefId);
            }
            else
            {
                if (current.Value is null || evaluated is null || source.ParameterValue is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidValue, reference.RefId);
                if (source.ParsedType.DataType == ParameterDataType.RawData)
                {
                    bytes = NativeRootRawDataCodec.Encode(source.ParsedType, current.Value!, Policy!.ByteOrder, reference.RefId);
                    var comparison = NativeRootRawDataCodec.Encode(source.ParsedType, evaluated!, Policy!.ByteOrder, reference.RefId);
                    if (bytes.Length != comparison.Length || bytes.Sha256 != comparison.Sha256) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch, reference.RefId);
                    defaultBytes = NativeRootRawDataCodec.Encode(source.ParsedType, source.ParameterValue!, Policy!.ByteOrder, reference.RefId);
                }
                else
                {
                    word = Encode(source.Type, source.ParsedType, current.Value!, reference.Owner.BitOffset, reference.RefId);
                    var comparison = Encode(source.Type, source.ParsedType, evaluated!, reference.Owner.BitOffset, reference.RefId);
                    if (word.Value.Word != comparison.Word || word.Value.Bits != comparison.Bits) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch, reference.RefId);
                    fallback = Encode(source.Type, source.ParsedType, source.ParameterValue!, reference.Owner.BitOffset, reference.RefId);
                }
            }
            result.Add(new(reference, current.Origin, NativeGeneralHash.Of(current.Value), NativeGeneralHash.Of(source.ParameterValue), word?.Word, fallback?.Word, bytes, defaultBytes));
        }
        return Array.AsReadOnly(result.ToArray());
    }
    private ParameterWordEncoding Encode(XElement declaration, ParameterType type, string value, byte bit, string id)
    {
        CheckValue(value, id); var result = OfflineParameterPatch.EncodeWord(declaration.Elements().Single(), type, value, Policy!.ByteOrder, bit, id);
        if (result.Issue is not null) throw new NativeGeneralRejected(new(NativeGeneralIssueCode.InvalidValue, id, ParameterIssue: result.Issue));
        return result;
    }
}
internal sealed record NativeRootHydratedReference(NativeRootReference Reference, RootParameterValueOrigin Origin,
    string CurrentHash, string DefaultHash, uint? CurrentWord, uint? DefaultWord,
    ImmutablePatchBytes? CurrentBytes = null, ImmutablePatchBytes? DefaultBytes = null);

internal sealed class NativeRootCodecFacts
{
    internal int CodecProfileVersion => RawDataFieldCount == 0 ? 3 : 4;
    internal string NumericParseProfile => SingleFieldCount == 0 ? "ExactInteger" : "ExactIntegerAndDotNet10XmlDoubleToSingle";
    internal string? PackedTimeEncodingProfile => PackedTimeFieldCount == 0 ? null : "PackedDaysHoursMinutesAndSeconds24BigEndianOnly";
    internal string? DenseTimeEncodingProfile => DenseTimeFieldCount == 0 ? null : "SignedRawOrdinaryTime8To32";
    internal int PhysicalFieldCount { get; }
    internal int SingleFieldCount { get; }
    internal int PackedTimeFieldCount { get; }
    internal int DenseTimeFieldCount { get; }
    internal int RawDataFieldCount { get; }
    internal string? RawDataEncodingProfile => RawDataFieldCount == 0 ? null : "RootMemoryLength32ProgramByteOrderOpaquePayloadRetainedTail";
    internal bool HasExtendedCodecs => SingleFieldCount != 0 || PackedTimeFieldCount != 0 || DenseTimeFieldCount != 0 || RawDataFieldCount != 0;
    internal string Fingerprint { get; }
    internal NativeRootCodecFacts(int physicalFields, int singleFields, int packedFields, int denseFields, int rawFields = 0)
    {
        PhysicalFieldCount = physicalFields; SingleFieldCount = singleFields;
        PackedTimeFieldCount = packedFields; DenseTimeFieldCount = denseFields; RawDataFieldCount = rawFields;
        Fingerprint = NativeGeneralHash.Of(new { CodecProfileVersion, NumericParseProfile, PackedTimeEncodingProfile,
            DenseTimeEncodingProfile, PhysicalFieldCount, SingleFieldCount, PackedTimeFieldCount, DenseTimeFieldCount, HasExtendedCodecs });
        if (RawDataFieldCount != 0) Fingerprint = NativeGeneralHash.Of(new { profile = 4, previous = Fingerprint, RawDataFieldCount, RawDataEncodingProfile });
    }
}
