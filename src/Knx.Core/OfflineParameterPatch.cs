using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

/// <summary>
/// Pure offline preview for an explicit, checked numeric subset in the absolute resource arena.
/// It never derives patch activation from UI visibility, executes scripts, creates an image or writes.
/// </summary>
public static class OfflineParameterPatch
{
    private const int BlobLimit = 1_048_576, TotalLimit = 4 * BlobLimit;
    private sealed class Rejected(ParameterPatchIssue issue) : Exception { internal ParameterPatchIssue Issue => issue; }
    [DoesNotReturn]
    private static void Reject(ParameterPatchIssueCode code, string? reference = null, string? resource = null)
        => throw new Rejected(new(code, Safe(reference) ? reference : null, Safe(resource) ? resource : null));
    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    public static ParameterPatchPreview Preview(Project project, DeviceInstance device,
        IReadOnlyList<ParameterPatchValue> subset, ParameterPatchByteOrder byteOrder)
    {
        try { return Create(project, device, subset, byteOrder); }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is InvalidDataException or XmlException or IOException or OverflowException or FormatException)
        { return new(new ParameterPatchIssue(ParameterPatchIssueCode.InvalidDeclaration)); }
    }

    private static ParameterPatchPreview Create(Project project, DeviceInstance device,
        IReadOnlyList<ParameterPatchValue> subset, ParameterPatchByteOrder order)
    {
        var built = Prepare(project, device, subset, order, resourceMode: false);
        var binding = new ParameterPatchSourceBinding(project, device, built.ApplicationId, built.DefinitionHash, built.Selection, order, built.Resources,
            built.Single, built.Packed, built.Dense, built.ModuleWitness, built.ModuleProfile);
        if (!binding.IsCurrent(project, device)) Reject(ParameterPatchIssueCode.InvalidRequest);
        return new(binding, built.Fields, built.Ranges, built.Resources);
    }

    private sealed record PreparedPatch(string ApplicationId, string DefinitionHash, ParameterPatchValue[] Selection,
        List<ParameterPatchField> Fields, List<ParameterPatchRange> Ranges, List<InitialMemoryResource> Resources,
        Dictionary<string, ResourcePatchArena> Arenas, bool Single, bool Packed, bool Dense, string? ModuleWitness, int? ModuleProfile,
        ResourcePatchIdentity? Identity, ResourcePatchRequirement[] Requirements);

    internal static OfflineResourcePlan PreviewResources(Project project, DeviceInstance device,
        IReadOnlyList<ParameterPatchValue> subset, ParameterPatchByteOrder order)
    {
        try
        {
            var built = Prepare(project, device, subset, order, resourceMode: true);
            var images = built.Resources.Select(r => new ResourcePatchImage(r, built.Arenas[r.Id], built.Ranges.Where(g => g.ResourceId == r.Id))).ToArray();
            var fields = built.Fields.Select(f => new ResourcePatchField(f.RefId, f.ParameterId, f.ResourceId, f.RawValue,
                built.Arenas[f.ResourceId], f.StartAddress, f.BitOffset, f.BitLength)).ToArray();
            var binding = new ResourcePatchSourceBinding(project, device, built.Identity!, built.ApplicationId, built.DefinitionHash, built.Selection, order,
                images, fields, built.Single, built.Packed, built.Dense, built.ModuleWitness, built.ModuleProfile);
            if (!binding.MatchesIdentity(project, device)) Reject(ParameterPatchIssueCode.InvalidRequest);
            var ranges = built.Ranges.Select(r => new ResourcePatchRange(r.ResourceId, built.Arenas[r.ResourceId], r.StartAddress,
                r.BeforeData, r.AfterData, r.FieldMask, r.BeforeMask, r.AfterMask)).ToArray();
            return new(binding, fields, ranges, images, built.Requirements);
        }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is InvalidDataException or XmlException or IOException or OverflowException or FormatException)
        { return new(new ParameterPatchIssue(ParameterPatchIssueCode.InvalidDeclaration)); }
    }

    private static PreparedPatch Prepare(Project project, DeviceInstance device,
        IReadOnlyList<ParameterPatchValue> subset, ParameterPatchByteOrder order, bool resourceMode)
    {
        if (project is null || device is null || subset is null || subset.Count is < 1 or > 64
            || !Enum.IsDefined(order) || !Safe(project.Id) || project.Guid is { } guid && !Safe(guid) || !Safe(device.Id)
            || !Safe(device.ProductRefId) || device.Hardware2ProgramRefId is { } hardware && !Safe(hardware)
            || subset.Any(v => v is null || !Safe(v.RefId) || v.RawValue is null || v.RawValue.Length > 64 || v.RawValue.Any(char.IsControl))
            || subset.Select(v => v.RefId).Distinct(StringComparer.Ordinal).Count() != subset.Count
            || !ParameterPatchSourceBinding.ValuesWithinBudget(device)
            || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device))) Reject(ParameterPatchIssueCode.InvalidRequest);
        var selection = subset.Select(v => new ParameterPatchValue(v.RefId, v.RawValue)).ToArray();
        var revision = project.ApplicationDataRevision;
        var sourceValues = device.ParameterValues.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
        var identity = resourceMode ? ResourcePatchIdentity.Capture(project, device) : null;
        var document = Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId);
        if (document?.Root is null) Reject(ParameterPatchIssueCode.ApplicationUnavailable);
        var root = document!.Root!;
        if (root.Name.LocalName != "KNX" || root.Name.NamespaceName is not
            ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23")) Reject(ParameterPatchIssueCode.InvalidDeclaration);
        // The codec consumes native declarations only. The general inspection reader's
        // LocalName compatibility must not turn a foreign shape into a proven native encoding.
        if (root.DescendantsAndSelf().Any(e => e.Name.Namespace != root.Name.Namespace
            || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml)))
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        if (root.DescendantsAndSelf().Any(e => e.Name.LocalName == "ParameterInstance")) Reject(ParameterPatchIssueCode.InvalidDeclaration);
        var applications = root.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        if (applications.Length != 1 || !Safe((string?)applications[0].Attribute("Id"))) Reject(ParameterPatchIssueCode.InvalidDeclaration);
        var application = applications[0]; var appId = (string)application.Attribute("Id")!;
        var originalResources = resourceMode ? application.Elements(application.Name.Namespace + "Static").Elements(application.Name.Namespace + "Code").Elements()
            .Where(e => e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment").GroupBy(e => (string?)e.Attribute("Id") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal) : null;
        ProfileGate(root, appId);
        var options = application.Elements().Where(e => e.Name.LocalName == "Static").Elements().Where(e => e.Name.LocalName == "Options").ToArray();
        if (options.Length > 1) Reject(ParameterPatchIssueCode.InvalidDeclaration);
        if (options.SingleOrDefault()?.Attribute("ParameterByteOrder") is { } declaredOrder
            && declaredOrder.Value != order.ToString()) Reject(ParameterPatchIssueCode.UnsupportedLayout);
        var nativeRoots = PreflightSelectedTypes(root, application, selection);
        var definitionHash = ParameterPatchSourceBinding.DefinitionFingerprint(document);
        var snapshot = ApplicationSnapshot.Evaluate(document, sourceValues); // No runtime capability, even for unrelated scripts.
        if (!snapshot.IsValid || snapshot.Evaluation.Unsupported.Any(s => s != "Parameter calculations (JavaScript) are not executed."))
            Reject(ParameterPatchIssueCode.UnsupportedEvaluation);
        var expanded = snapshot.Evaluation.ExpandedRoot!;
        var expandedApplication = expanded.DescendantsAndSelf().Single(e => e.Name.LocalName == "ApplicationProgram");
        var rows = snapshot.Instances.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var definitions = expanded.DescendantsAndSelf().Where(e => e.Name.LocalName == "Parameter" && !Template(e))
            .GroupBy(e => (string?)e.Attribute("Id") ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var types = expanded.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterType").GroupBy(e => (string?)e.Attribute("Id") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var referenceDeclarations = expanded.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterRef" && !Template(e))
            .GroupBy(e => (string?)e.Attribute("Id") ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var fields = new List<ParameterPatchField>();
        var arenas = new Dictionary<string, ResourcePatchArena>(StringComparer.Ordinal);
        // Only validated exact unsigned words reach the shared bit packer. Raw IEEE
        // strings never pass through the integer parser or acquire integer semantics.
        var encodedWords = new Dictionary<string, uint>(StringComparer.Ordinal);
        var includesSingle = false;
        var includesPackedTime = false;
        var includesDenseTime = false;
        ModulePatchScope? moduleScope = null;
        var watched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in selection)
        {
            if (!rows.TryGetValue(value.RefId, out var row)) Reject(ParameterPatchIssueCode.UnknownParameter, value.RefId);
            var parameterId = row!.ParameterId ?? row.Id;
            if (!Safe(parameterId) || !Safe(row.ParameterTypeId) || !Safe(row.Storage.CodeSegmentId)) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if (!definitions.TryGetValue(parameterId, out var declarations) || declarations.Length != 1) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            var declaration = declarations![0];
            if (resourceMode && row.Storage.Kind == ParameterStorageKind.RelativeMemory && declaration.Attribute("BaseValue") is not null)
                Reject(ParameterPatchIssueCode.UnsupportedParameter, row.Id);
            if (row.ModuleOrigin is not null)
            {
                if (resourceMode && row.Storage.Kind == ParameterStorageKind.RelativeMemory) Reject(ParameterPatchIssueCode.UnsupportedParameter, row.Id);
                moduleScope ??= new ModulePatchScope(root, snapshot, sourceValues, nativeRoots);
                var reference = referenceDeclarations.TryGetValue(row.Id, out var matches) && matches.Length == 1 ? matches[0] : null;
                if (moduleScope.Validate(row, declaration, reference, watched) is { } scopeIssue) Reject(scopeIssue, row.Id);
            }
            if (!RootDeclaration(declaration, "Parameters", expandedApplication)) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if ((string?)declaration.Attribute("ParameterType") != row.ParameterTypeId) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if (row.ParameterId is not null && (!referenceDeclarations.TryGetValue(row.Id, out var refDeclarations) || refDeclarations.Length != 1
                || refDeclarations[0].Attribute("ParameterType") is not null || refDeclarations[0].Attribute("ParameterTypeId") is not null))
                Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if (row.ParameterId is not null && !RootDeclaration(referenceDeclarations[row.Id][0], "ParameterRefs", expandedApplication))
                Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if (row.ModuleOrigin is null)
            {
                // Absence of an admitted origin is not evidence of root ownership: a
                // repeated/nested module can legally use ids without the module prefix.
                var original = nativeRoots.Parameters[parameterId].Take(2).ToArray();
                if (original.Length != 1 || !XNode.DeepEquals(original[0], declaration)) Reject(ParameterPatchIssueCode.UnsupportedParameter, row.Id);
                if (row.ParameterId is not null)
                {
                    var references = nativeRoots.References[row.Id].Take(2).ToArray();
                    if (references.Length != 1 || !XNode.DeepEquals(references[0], referenceDeclarations[row.Id][0])) Reject(ParameterPatchIssueCode.UnsupportedParameter, row.Id);
                }
            }
            // Flattened geometry alone does not prove module origin or patch activation.
            if (declaration.Ancestors().Any(e => e.Name.LocalName is "ModuleDef" or "Union") || row.ModuleOrigin is null && parameterId.Contains("_MI-", StringComparison.Ordinal)
                || declaration.Elements().Count() != 1 || declaration.Elements().Single().Name.LocalName != "Memory"
                || row.ModuleOrigin is null && declaration.Elements().Single().Attribute("BaseOffset") is not null
                || row.Storage is not { Status: ParameterStorageStatus.Supported,
                    StartAddress: not null, BitOffset: not null, Footprint.Status: ParameterFootprintStatus.Known }
                || row.Storage.Kind != ParameterStorageKind.AbsoluteMemory && (!resourceMode || row.Storage.Kind != ParameterStorageKind.RelativeMemory || row.Storage.LoadStateMachine is null)
                || row.UnsupportedReason is not null || !row.IsStaticallyEnabled) Reject(ParameterPatchIssueCode.UnsupportedParameter, value.RefId);
            if (row.IsDerivedValue || snapshot.Evaluation.StructuralParameters.Contains(row.Id)) Reject(ParameterPatchIssueCode.UnsupportedDependency, value.RefId);
            if (snapshot.Instances.Count(i => (i.ParameterId ?? i.Id) == parameterId) != 1) Reject(ParameterPatchIssueCode.AmbiguousOverlap, value.RefId);
            if (!types.TryGetValue(row.ParameterTypeId!, out var typeDeclarations) || typeDeclarations.Length != 1) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if (!RootDeclaration(typeDeclarations[0], "ParameterTypes", expandedApplication)) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            var originalTypes = nativeRoots.Types[row.ParameterTypeId!].Take(2).ToArray();
            if (originalTypes.Length != 1 || !XNode.DeepEquals(originalTypes[0], typeDeclarations[0])) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            var shape = typeDeclarations![0].Elements().ToArray();
            if (shape.Length != 1) Reject(ParameterPatchIssueCode.UnsupportedParameter, value.RefId);
            var bitOffset = row.Storage.BitOffset!.Value;
            var encoding = EncodeWord(shape[0], row.ParameterType, value.RawValue, order, bitOffset, value.RefId);
            if (encoding.Issue is { } encodingIssue) throw new Rejected(encodingIssue);
            var bits = encoding.Bits; var encodedWord = encoding.Word;
            includesSingle |= encoding.Single;
            includesPackedTime |= encoding.Packed;
            includesDenseTime |= encoding.Dense;
            watched.Add(row.Id); watched.Add(parameterId);
            encodedWords.Add(row.Id, encodedWord);
            var arena = row.Storage.Kind == ParameterStorageKind.AbsoluteMemory ? new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory)
                : new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, checked((byte)row.Storage.LoadStateMachine!.Value));
            if (arenas.TryGetValue(row.Storage.CodeSegmentId!, out var previousArena) && previousArena != arena) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            arenas[row.Storage.CodeSegmentId!] = arena;
            if (resourceMode && arenas.Values.Select(a => a.Kind).Distinct().Count() > 1) Reject(ParameterPatchIssueCode.UnsupportedLayout);
            fields.Add(new(row.Id, parameterId, row.Storage.CodeSegmentId!, value.RawValue, row.Storage.StartAddress!.Value, bitOffset, (uint)bits));
        }
        DependencyGate(expanded, watched, rows.Keys.ToHashSet(StringComparer.Ordinal));
        var knownParameters = snapshot.Instances.Select(i => i.ParameterId ?? i.Id).ToHashSet(StringComparer.Ordinal);
        if (definitions.Any(p => !knownParameters.Contains(p.Key) && p.Value.Any(e => e.Elements().Any(c => c.Name.LocalName == "Memory")
            || e.Ancestors().Any(u => u.Name.LocalName == "Union" && u.Elements().Any(c => c.Name.LocalName == "Memory")))))
            Reject(ParameterPatchIssueCode.AmbiguousOverlap);
        var relative = resourceMode && arenas.Values.First().Kind == ResourcePatchArenaKind.RelativeMemory;
        if (relative) RelativeOverlapGate(fields, snapshot.Instances, arenas);
        else OverlapGate(fields, snapshot.Instances);
        var resourceIds = fields.Select(f => f.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
        if (resourceIds.Length > 16) Reject(ParameterPatchIssueCode.ResourceLimitExceeded);
        var segments = expanded.DescendantsAndSelf().Where(e => e.Name.LocalName == (relative ? "RelativeSegment" : "AbsoluteSegment") && e.Parent?.Name.LocalName == "Code" && !Template(e))
            .GroupBy(e => (string?)e.Attribute("Id") ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var resources = new List<InitialMemoryResource>(); var total = 0;
        var payloadPlans = new List<(string Id, uint Address, uint? Size, string Data, string? Mask)>();
        foreach (var id in resourceIds)
        {
            if (!segments.TryGetValue(id, out var declarations) || declarations.Length != 1) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            var segment = declarations![0];
            if (!RootDeclaration(segment, "Code", expandedApplication)) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            if (resourceMode && (!originalResources!.TryGetValue(id, out var originals) || originals.Length != 1 || !XNode.DeepEquals(originals[0], segment)))
                Reject(ParameterPatchIssueCode.UnsupportedParameter);
            var address = 0u;
            if (relative)
            {
                if (!RelativeHeaderKnown(segment)) Reject(ParameterPatchIssueCode.InvalidDeclaration);
                if (!uint.TryParse((string?)segment.Attribute("Offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out address) || address > 1_048_575
                    || !byte.TryParse((string?)segment.Attribute("LoadStateMachine"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lsm)
                    || arenas[id].LoadStateMachine != lsm) Reject(ParameterPatchIssueCode.InvalidDeclaration);
            }
            else if (!uint.TryParse((string?)segment.Attribute("Address"), NumberStyles.Integer, CultureInfo.InvariantCulture, out address)
                || segment.Attribute("UserMemory") is { Value: not ("0" or "false") }) Reject(ParameterPatchIssueCode.UnsupportedParameter);
            var dataElements = segment.Elements().Where(e => e.Name.LocalName == "Data").Take(2).ToArray();
            var maskElements = segment.Elements().Where(e => e.Name.LocalName == "Mask").Take(2).ToArray();
            if (dataElements.Length != 1) Reject(ParameterPatchIssueCode.UnknownInitialData, resource: id);
            if (maskElements.Length > 1) Reject(ParameterPatchIssueCode.InvalidInitialMask, resource: id);
            var dataText = Payload(dataElements[0], segment.Name.Namespace, ParameterPatchIssueCode.UnknownInitialData, id);
            var maskText = maskElements.Length == 0 ? null : Payload(maskElements[0], segment.Name.Namespace, ParameterPatchIssueCode.InvalidInitialMask, id);
            var dataLength = DecodedLength(dataText, ParameterPatchIssueCode.UnknownInitialData, id);
            var maskLength = maskText is null ? 0 : DecodedLength(maskText, ParameterPatchIssueCode.InvalidInitialMask, id);
            total = checked(total + dataLength + maskLength); if (total > TotalLimit) Reject(ParameterPatchIssueCode.ResourceLimitExceeded);
            if (maskText is not null && maskLength != dataLength) Reject(ParameterPatchIssueCode.InvalidInitialMask, resource: id);
            uint? size = uint.TryParse((string?)segment.Attribute("Size"), out var declaredSize) && declaredSize <= 1_048_575 ? declaredSize : null;
            payloadPlans.Add((id, address, size, dataText, maskText));
        }
        // All selected Data+Mask sizes pass the aggregate budget before the first decode.
        if (relative) RelativeOwnershipGate(fields, segments, arenas);
        else OwnershipGate(fields, segments);
        foreach (var plan in payloadPlans) resources.Add(new(plan.Id, plan.Address, plan.Size,
            Decode(plan.Data, ParameterPatchIssueCode.UnknownInitialData, plan.Id),
            plan.Mask is null ? null : Decode(plan.Mask, ParameterPatchIssueCode.InvalidInitialMask, plan.Id)));
        var ranges = new List<ParameterPatchRange>();
        foreach (var resource in resources)
        {
            var touches = new SortedDictionary<int, (byte Bits, byte Mask)>();
            foreach (var field in fields.Where(f => f.ResourceId == resource.Id))
            {
                var start = (long)field.StartAddress - resource.StartAddress;
                var span = (field.BitOffset + field.BitLength + 7) / 8;
                if (start < 0 || (ulong)start + span > (ulong)resource.Data.Length) Reject(ParameterPatchIssueCode.UnknownInitialData, field.RefId, resource.Id);
                var value = encodedWords[field.RefId];
                FixedScalarPatchCodec.Accumulate(touches, value, checked((int)start), field.BitOffset, field.BitLength, order);
            }
            var keys = touches.Keys.ToArray();
            for (var from = 0; from < keys.Length;)
            {
                var to = from + 1; while (to < keys.Length && keys[to] == keys[to - 1] + 1) to++;
                var before = new byte[to - from]; var after = new byte[before.Length]; var fieldMask = new byte[before.Length];
                var beforeMask = resource.Mask is null ? null : new byte[before.Length]; var afterMask = resource.Mask is null ? null : new byte[before.Length];
                for (var i = from; i < to; i++)
                {
                    var key = keys[i]; var at = i - from; var change = touches[key];
                    before[at] = resource.Data[key]; fieldMask[at] = change.Mask;
                    after[at] = (byte)((before[at] & ~change.Mask) | (change.Bits & change.Mask));
                    if (beforeMask is not null) { beforeMask[at] = resource.Mask![key]; afterMask![at] = (byte)(beforeMask[at] | change.Mask); }
                }
                ranges.Add(new(resource.Id, checked(resource.StartAddress + (uint)keys[from]), new(before), new(after), new(fieldMask),
                    beforeMask is null ? null : new(beforeMask), afterMask is null ? null : new(afterMask)));
                from = to;
            }
        }
        if (ranges.Sum(r => r.Length) > 320 || project.ApplicationDataRevision != revision
            || !sourceValues.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))) Reject(ParameterPatchIssueCode.InvalidRequest);
        var requirements = resourceMode ? Requirements(application, resources) : [];
        return new(appId, definitionHash, selection, fields, ranges, resources, arenas, includesSingle, includesPackedTime, includesDenseTime,
            moduleScope?.Witness(), moduleScope?.ProfileVersion, identity, requirements);
    }

    private static bool Template(XElement element) => element.Ancestors().Any(e => e.Name.LocalName == "ModuleDef");

    /// <summary>Encodes an admitted scalar and its layout without inferring source ownership or activity.</summary>
    internal static ParameterWordEncoding EncodeWord(XElement shape, ParameterType? type, string rawValue,
        ParameterPatchByteOrder order, byte bitOffset, string? reference)
    {
        try
        {
            var includesSingle = false; var includesPackedTime = false; var includesDenseTime = false;
            int bits; uint encodedWord;
            if (shape.Name.LocalName == "TypeRestriction" && type?.DataType == ParameterDataType.Enumeration)
            {
                var enumeration = ReadEnum(shape); bits = enumeration.Bits;
                if (!CanonicalUInt(rawValue, out var choice) || !enumeration.Values.Contains(choice)
                    || !ParameterValidation.TryValidate(type, rawValue, out _)) Reject(ParameterPatchIssueCode.InvalidValue, reference);
                encodedWord = choice;
            }
            else if (shape.Name.LocalName == "TypeFloat" && type?.DataType == ParameterDataType.Float)
            {
                var range = ReadSingleDeclaration(shape);
                bits = 32;
                if (bitOffset != 0) Reject(ParameterPatchIssueCode.UnsupportedLayout, reference);
                if (!XmlDouble(rawValue, out var number) || number < range.Minimum || number > range.Maximum)
                    Reject(ParameterPatchIssueCode.InvalidValue, reference);
                // This profile parses XML Double before converting to Single. Single.Parse gives
                // different bits at double-rounding midpoints. This is an explicit .NET10
                // parser profile, not a claim of all .NET Framework decimal-parser parity.
                var single = (float)number;
                if (!float.IsFinite(single)) Reject(ParameterPatchIssueCode.InvalidValue, reference);
                encodedWord = BitConverter.SingleToUInt32Bits(single); // Includes negative zero.
                includesSingle = true;
            }
            else if (shape.Name.LocalName == "TypeTime" && type?.DataType == ParameterDataType.Time)
            {
                if (IsDenseTimeUnit((string?)shape.Attribute("Unit")))
                {
                    var range = ReadDenseTimeDeclaration(shape); bits = range.Bits;
                    if (!long.TryParse(rawValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var raw)
                        || raw < range.Minimum || raw > range.Maximum || !ParameterValidation.TryValidate(type, rawValue, out _))
                        Reject(ParameterPatchIssueCode.InvalidValue, reference);
                    // Ordinary Time preserves raw Int64 unit counts. This profile
                    // uses a signed interval at the declared bit width.
                    // Whole declared bounds already fit; this is never truncation,
                    // a duration scale, or a packed-time byte layout.
                    encodedWord = (uint)(raw < 0 ? raw + (1L << bits) : raw);
                    includesDenseTime = true;
                }
                else
                {
                    var range = ReadPackedTimeDeclaration(shape);
                    bits = 24;
                    if (order != ParameterPatchByteOrder.BigEndian || bitOffset != 0)
                        Reject(ParameterPatchIssueCode.UnsupportedLayout, reference);
                    if (!long.TryParse(rawValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
                        || seconds < range.Minimum || seconds > range.Maximum
                        || !ParameterValidation.TryValidate(type, rawValue, out _))
                        Reject(ParameterPatchIssueCode.InvalidValue, reference);
                    // Packed Time uses fixed bytes [second, minute, day<<5|hour], not
                    // integer bytes of the raw seconds. The accepted BE-only profile
                    // gives those bytes to the existing packer without any reversal.
                    var day = (uint)(seconds / 86400); var remainder = (uint)(seconds % 86400);
                    var hour = remainder / 3600; var minute = remainder % 3600 / 60; var second = remainder % 60;
                    encodedWord = second << 16 | minute << 8 | day << 5 | hour;
                    includesPackedTime = true;
                }
            }
            else
            {
                (bits, encodedWord) = EncodeNumber(shape, type, rawValue, reference);
            }
            if (order == ParameterPatchByteOrder.LittleEndian && (bits % 8 != 0 || bitOffset != 0)) Reject(ParameterPatchIssueCode.UnsupportedLayout, reference);
            return new(bits, encodedWord, includesSingle, includesPackedTime, includesDenseTime);
        }
        catch (Rejected failure) { return new(0, 0, false, false, false, failure.Issue); }
        catch (Exception failure) when (failure is InvalidDataException or XmlException or IOException or OverflowException or FormatException)
        { return new(0, 0, false, false, false, new(ParameterPatchIssueCode.InvalidDeclaration)); }
    }

    /// <summary>Preflights bounded enumeration, Single and Time declarations before typed parsing.</summary>
    internal static ParameterPatchIssue? PreflightWordDeclaration(XElement shape)
    {
        try
        {
            if (shape.Name.LocalName == "TypeRestriction") _ = ReadEnum(shape);
            else if (shape.Name.LocalName == "TypeFloat") _ = ReadSingleDeclaration(shape);
            else if (shape.Name.LocalName == "TypeTime")
            {
                if (IsDenseTimeUnit((string?)shape.Attribute("Unit"))) _ = ReadDenseTimeDeclaration(shape);
                else _ = ReadPackedTimeDeclaration(shape);
            }
            else Reject(ParameterPatchIssueCode.UnsupportedParameter);
            return null;
        }
        catch (Rejected failure) { return failure.Issue; }
        catch (Exception failure) when (failure is InvalidDataException or XmlException or IOException or OverflowException or FormatException)
        { return new(ParameterPatchIssueCode.InvalidDeclaration); }
    }

    private static (int Bits, uint Word) EncodeNumber(XElement shape, ParameterType? type, string raw, string? reference)
    {
        if (shape.Name.LocalName != "TypeNumber" || type?.DataType != ParameterDataType.Number
            || type.NumberType is not ("unsignedInt" or "signedInt") || type.SizeInBit is < 1 or > 32)
            Reject(ParameterPatchIssueCode.UnsupportedParameter, reference);
        if (shape.HasElements || !string.IsNullOrWhiteSpace(shape.Value)
            || shape.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not
                ("Type" or "SizeInBit" or "minInclusive" or "maxInclusive" or "Increment" or "UIHint" or "DisplayFactor" or "DisplayOffset"))))
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        var bits = type!.SizeInBit;
        if (shape.Attribute("Increment") is { Value: not "1" }
            || shape.Attribute("DisplayFactor") is { Value: not "1" } || shape.Attribute("DisplayOffset") is { Value: not "0" })
            Reject(ParameterPatchIssueCode.UnsupportedParameter, reference);
        var signed = type.NumberType == "signedInt";
        var minimum = signed ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
        var maximum = (BigInteger.One << (bits - (signed ? 1 : 0))) - 1;
        var declaredMin = BigInteger.Zero; var declaredMax = BigInteger.Zero;
        if (!Integer((string?)shape.Attribute("minInclusive"), out declaredMin) || !Integer((string?)shape.Attribute("maxInclusive"), out declaredMax)
            || declaredMin > declaredMax || declaredMin < minimum || declaredMax > maximum) Reject(ParameterPatchIssueCode.InvalidDeclaration);
        if (!Integer(raw, out var number) || number < minimum || number > maximum || number < declaredMin || number > declaredMax
            || !ParameterValidation.TryValidate(type, raw, out _)) Reject(ParameterPatchIssueCode.InvalidValue, reference);
        return (bits, (uint)(number < 0 ? number + (BigInteger.One << bits) : number));
    }

    internal static FixedScalarEncoding EncodeFixedScalar(XElement shape, ParameterType type, string raw)
    {
        try
        {
            if (shape.Name.LocalName == "TypeRestriction" && type.DataType == ParameterDataType.Enumeration)
            {
                var enumeration = ReadEnum(shape);
                if (!CanonicalUInt(raw, out var value) || !enumeration.Values.Contains(value) || !ParameterValidation.TryValidate(type, raw, out _))
                    Reject(ParameterPatchIssueCode.InvalidValue);
                return new(enumeration.Bits, value);
            }
            var number = EncodeNumber(shape, type, raw, null); return new(number.Bits, number.Word);
        }
        catch (Rejected failure) { return new(0, 0, failure.Issue.Code); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or OverflowException or FormatException)
        { return new(0, 0, ParameterPatchIssueCode.InvalidDeclaration); }
    }

    internal static ParameterPatchIssueCode? ValidateFixedEnumDeclaration(XElement shape)
    {
        try { _ = ReadEnum(shape); return null; }
        catch (Rejected failure) { return failure.Issue.Code; }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or OverflowException or FormatException)
        { return ParameterPatchIssueCode.InvalidDeclaration; }
    }
    private sealed record NumericEnumeration(int Bits, HashSet<uint> Values);

    private static ModulePatchSelection PreflightSelectedTypes(XElement root, XElement application, ParameterPatchValue[] selected)
    {
        var ns = application.Name.Namespace;
        var native = new ModulePatchSelection(root, application, selected);
        var references = native.References; var parameters = native.Parameters; var types = native.Types;
        var visited = new HashSet<XElement>();
        foreach (var value in selected)
        {
            var refs = references[value.RefId].Take(2).ToArray();
            var targets = parameters[refs.Length == 1 ? (string?)refs[0].Attribute("RefId") ?? "" : value.RefId].Take(2).ToArray();
            if (targets.Length != 1) continue; // Existing native scope/identity gates still own invalid or module requests.
            var declarations = types[(string?)targets[0].Attribute("ParameterType") ?? ""].Take(2).ToArray();
            if (declarations.Length != 1) continue;
            var shapes = declarations[0].Elements().Take(2).ToArray();
            if (shapes.Length == 1 && shapes[0].Name == ns + "TypeRestriction" && visited.Add(shapes[0]))
                _ = ReadEnum(shapes[0]);
            if (shapes.Length == 1 && shapes[0].Name == ns + "TypeFloat" && visited.Add(shapes[0]))
                _ = ReadSingleDeclaration(shapes[0]);
            if (shapes.Length == 1 && shapes[0].Name == ns + "TypeTime" && visited.Add(shapes[0]))
            {
                if (IsDenseTimeUnit((string?)shapes[0].Attribute("Unit"))) _ = ReadDenseTimeDeclaration(shapes[0]);
                else _ = ReadPackedTimeDeclaration(shapes[0]);
            }
        }
        // Only selected native root declarations are preflighted. Existing bounded XML/evaluation
        // retains its normal allocations for unrelated types; Number requests do not acquire
        // new global enum-size, Float- or Time-profile gates.
        native.PreflightTypes(shape => { if (visited.Add(shape)) _ = ReadEnum(shape); },
            shape => { if (visited.Add(shape)) _ = ReadDenseTimeDeclaration(shape); });
        return native;
    }

    // IsKnownTimeUnit also includes packed labels and is deliberately NOT a codec permission.
    internal static bool IsDenseTimeUnit(string? unit) => unit is "Hours" or "Minutes" or "Seconds"
        or "HundredMilliseconds" or "TenMilliseconds" or "Milliseconds";
    private readonly record struct DenseTimeRange(int Bits, long Minimum, long Maximum);
    private static DenseTimeRange ReadDenseTimeDeclaration(XElement shape)
    {
        var bits = 0;
        if (!IsDenseTimeUnit((string?)shape.Attribute("Unit"))
            || !int.TryParse((string?)shape.Attribute("SizeInBit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bits) || bits is < 8 or > 32)
            Reject(ParameterPatchIssueCode.UnsupportedParameter);
        if (shape.HasElements || !string.IsNullOrWhiteSpace(shape.Value)
            || shape.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
                || a.Name.LocalName is not ("SizeInBit" or "Unit" or "minInclusive" or "maxInclusive" or "UIHint"))))
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        var minimum = 0L; var maximum = 0L;
        var minText = (string?)shape.Attribute("minInclusive"); var maxText = (string?)shape.Attribute("maxInclusive");
        var signedMinimum = -(1L << (bits - 1)); var signedMaximum = (1L << (bits - 1)) - 1;
        if (minText is not { Length: > 0 and <= 64 } || maxText is not { Length: > 0 and <= 64 }
            || !long.TryParse(minText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out minimum)
            || !long.TryParse(maxText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out maximum)
            || minimum > maximum || minimum < signedMinimum || maximum > signedMaximum
            || Ksxml.ParseParameterType(shape.Parent!) is not { } type || ParameterValidation.TimeDeclarationError(type) is not null)
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        return new(bits, minimum, maximum);
    }

    private readonly record struct PackedTimeRange(long Minimum, long Maximum);
    private static PackedTimeRange ReadPackedTimeDeclaration(XElement shape)
    {
        if ((string?)shape.Attribute("Unit") != "PackedDaysHoursMinutesAndSeconds"
            || !int.TryParse((string?)shape.Attribute("SizeInBit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width != 24)
            Reject(ParameterPatchIssueCode.UnsupportedParameter);
        if (shape.HasElements || !string.IsNullOrWhiteSpace(shape.Value)
            || shape.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
                || a.Name.LocalName is not ("SizeInBit" or "Unit" or "minInclusive" or "maxInclusive" or "UIHint"))))
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        var minimum = 0L; var maximum = 0L;
        var minText = (string?)shape.Attribute("minInclusive"); var maxText = (string?)shape.Attribute("maxInclusive");
        if (minText is not { Length: > 0 and <= 64 } || maxText is not { Length: > 0 and <= 64 }
            || !long.TryParse(minText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out minimum)
            || !long.TryParse(maxText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out maximum)
            || minimum < 0 || maximum > 691199 || minimum > maximum
            || Ksxml.ParseParameterType(shape.Parent!) is not { } type || ParameterValidation.TimeDeclarationError(type) is not null)
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        return new(minimum, maximum);
    }

    private readonly record struct SingleRange(double Minimum, double Maximum);
    private static SingleRange ReadSingleDeclaration(XElement shape)
    {
        if ((string?)shape.Attribute("Encoding") != "IEEE-754 Single") Reject(ParameterPatchIssueCode.UnsupportedParameter);
        if (shape.HasElements || !string.IsNullOrWhiteSpace(shape.Value)
            || shape.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
                || a.Name.LocalName is not ("Encoding" or "minInclusive" or "maxInclusive" or "Increment" or "UIHint"
                    or "DisplayFormat" or "DisplayFactor" or "DisplayOffset")))
            || (string?)shape.Attribute("UIHint") is not (null or "Slider")
            || shape.Attribute("DisplayFormat") is { } format && (format.Value.Length > 128 || format.Value.Any(char.IsControl)))
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        // The first IEEE preview never interprets display transforms or step rules.
        // DisplayFormat is bounded inert text and is never given to a formatter.
        if (shape.Attribute("Increment") is not null || shape.Attribute("DisplayFactor") is not null || shape.Attribute("DisplayOffset") is not null)
            Reject(ParameterPatchIssueCode.UnsupportedParameter);
        var minimum = 0d; var maximum = 0d;
        if (!XmlDouble((string?)shape.Attribute("minInclusive"), out minimum)
            || !XmlDouble((string?)shape.Attribute("maxInclusive"), out maximum) || minimum > maximum)
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        return new(minimum, maximum);
    }

    private static bool XmlDouble(string? text, out double number)
    {
        number = 0;
        if (text is not { Length: > 0 and <= 64 } || text.Any(char.IsControl)) return false;
        try { number = XmlConvert.ToDouble(text); return double.IsFinite(number); }
        catch (Exception failure) when (failure is FormatException or OverflowException) { return false; }
    }

    private static NumericEnumeration ReadEnum(XElement shape)
    {
        // Schema permits much wider/binary restrictions. This is an explicit unsigned
        // declared-choice profile, not a TypeNumber alias or a general enum formatter.
        var count = shape.Elements().Take(4097).Count();
        if (count > 4096) Reject(ParameterPatchIssueCode.ResourceLimitExceeded);
        if (count == 0) Reject(ParameterPatchIssueCode.InvalidDeclaration);
        var bits = 0;
        if ((string?)shape.Attribute("Base") != "Value" || !int.TryParse((string?)shape.Attribute("SizeInBit"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out bits) || bits is < 1 or > 32) Reject(ParameterPatchIssueCode.UnsupportedParameter);
        if (shape.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
            || a.Name.LocalName is not ("Base" or "SizeInBit" or "UIHint")))
            || shape.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
            || (string?)shape.Attribute("UIHint") is not (null or "Text" or "DropDown" or "Buttons" or "Segmented"))
            Reject(ParameterPatchIssueCode.InvalidDeclaration);
        // The cap is checked before these per-type choice allocations and (in preflight)
        // before Snapshot builds the selected enum's typed Restrictions list.
        var ids = new HashSet<string>(StringComparer.Ordinal); var values = new HashSet<uint>();
        var maximum = (1UL << bits) - 1;
        foreach (var member in shape.Elements())
        {
            if (member.Attribute("BinaryValue") is not null) Reject(ParameterPatchIssueCode.UnsupportedParameter);
            var id = (string?)member.Attribute("Id");
            if (member.Name != shape.Name.Namespace + "Enumeration" || member.HasElements
                || member.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                || member.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
                    || a.Name.LocalName is not ("Id" or "Value" or "Text" or "Icon" or "PictureAlignment" or "DisplayOrder")))
                || !Safe(id) || string.IsNullOrWhiteSpace(id) || !ids.Add(id!)
                || !CanonicalUInt((string?)member.Attribute("Value"), out var value) || value > maximum || !values.Add(value))
                Reject(ParameterPatchIssueCode.InvalidDeclaration);
            try { XmlConvert.VerifyNCName(id!); } catch (XmlException) { Reject(ParameterPatchIssueCode.InvalidDeclaration); }
        }
        return new(bits, values);
    }

    private static bool CanonicalUInt(string? text, out uint value)
    {
        value = 0;
        return text is { Length: > 0 and <= 10 } && text.All(char.IsAsciiDigit)
            && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value.ToString(CultureInfo.InvariantCulture) == text;
    }
    private static bool RootDeclaration(XElement element, string container, XElement application) =>
        element.Parent is { } parent && parent.Name.LocalName == container && parent.Parent is { } stat
        && stat.Name.LocalName == "Static" && ReferenceEquals(stat.Parent, application);
    private static bool Integer(string? value, out BigInteger number) => BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    private static string Payload(XElement element, XNamespace expected, ParameterPatchIssueCode failure, string id)
    {
        if (element.HasElements || element.Name.Namespace != expected || element.Attributes().Any(a => !a.IsNamespaceDeclaration)
            || element.Nodes().OfType<XText>().Sum(n => (long)n.Value.Length) > 4 * BlobLimit) Reject(failure, resource: id);
        return element.Value;
    }
    private static int DecodedLength(string text, ParameterPatchIssueCode failure, string id)
    {
        var count = 0; var padding = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n') continue;
            count++;
            if (c == '=') padding++;
            else if (padding != 0 || !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/')) Reject(failure, resource: id);
        }
        if (count % 4 != 0 || padding > 2) Reject(failure, resource: id);
        var size = count / 4 * 3 - padding;
        if (size < 0) Reject(failure, resource: id);
        if (size > BlobLimit) Reject(ParameterPatchIssueCode.ResourceLimitExceeded);
        return size;
    }
    private static byte[] Decode(string text, ParameterPatchIssueCode failure, string id)
    {
        byte[] data; try { data = Convert.FromBase64String(text); } catch (FormatException) { Reject(failure, resource: id); throw; }
        if (data.Length > BlobLimit) Reject(ParameterPatchIssueCode.ResourceLimitExceeded);
        return data;
    }

    private static void ProfileGate(XElement root, string id)
    {
        if (KnownDcaRequirementsRegistry.RequiresDca(id)) Reject(ParameterPatchIssueCode.UnsupportedApplication);
        foreach (var e in root.DescendantsAndSelf())
            if (e.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware"
                    && !string.IsNullOrWhiteSpace(a.Value) && a.Value is not ("false" or "False" or "0"))
                || e.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler"
                || e.Name.LocalName == "Extension" && (e.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
                    || e.Elements().Any(c => c.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
                || e.Name.LocalName is "Parameter" or "ParameterRef" && !string.IsNullOrWhiteSpace((string?)e.Attribute("Plugin")))
                Reject(ParameterPatchIssueCode.UnsupportedApplication);
    }

    private static void DependencyGate(XElement root, HashSet<string> selected, HashSet<string> references)
    {
        foreach (var node in root.DescendantsAndSelf().Where(e => !Template(e)))
        {
            if (node.Name.LocalName == "Assign")
            {
                var target = (string?)node.Attribute("TargetParamRefRef"); var source = (string?)node.Attribute("SourceParamRefRef");
                if (target is null || !references.Contains(target) || source is not null && !references.Contains(source)
                    || node.Attribute("Value") is null && source is null || selected.Contains(target) || source is not null && selected.Contains(source))
                    Reject(ParameterPatchIssueCode.UnsupportedDependency);
            }
            if (node.Name.LocalName != "ParameterCalculation") continue;
            if ((string?)node.Attribute("Language") != "JavaScript") Reject(ParameterPatchIssueCode.UnsupportedDependency);
            var sides = node.Elements().Where(e => e.Name.LocalName is "LParameters" or "RParameters").ToArray();
            var bindings = sides.SelectMany(e => e.Elements()).ToArray();
            if (sides.Length != 2 || sides.Select(e => e.Name.LocalName).Distinct().Count() != 2 || bindings.Length is < 2 or > 256
                || bindings.Any(e => e.Name.LocalName != "ParameterRefRef" || e.HasElements || e.Attribute("RefId") is null
                    || !references.Contains((string)e.Attribute("RefId")!) || selected.Contains((string?)e.Attribute("RefId") ?? ""))
                || node.Descendants().Attributes("RefId").Count() != bindings.Length) Reject(ParameterPatchIssueCode.UnsupportedDependency);
        }
    }

    private static void OverlapGate(List<ParameterPatchField> fields, List<ParameterInstance> rows)
    {
        foreach (var field in fields)
        {
            var start = (ulong)field.StartAddress * 8 + field.BitOffset; var end = start + field.BitLength;
            foreach (var row in rows)
            {
                if (row.Id == field.RefId || row.Storage.Status == ParameterStorageStatus.NotDeclared) continue;
                var storage = row.Storage;
                if (storage.Status != ParameterStorageStatus.Supported) Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
                if (storage.Kind != ParameterStorageKind.AbsoluteMemory) continue;
                var otherStart = (ulong)storage.StartAddress!.Value * 8 + storage.BitOffset!.Value;
                if (otherStart >= end) continue;
                if (storage.Footprint is not { Status: ParameterFootprintStatus.Known, BitLength: { } width }
                    || otherStart + width > start) Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
            }
        }
    }

    private static void RelativeOverlapGate(List<ParameterPatchField> fields, List<ParameterInstance> rows,
        Dictionary<string, ResourcePatchArena> arenas)
    {
        foreach (var field in fields)
        {
            var start = (ulong)field.StartAddress * 8 + field.BitOffset; var end = start + field.BitLength;
            var arena = arenas[field.ResourceId];
            foreach (var row in rows)
            {
                if (row.Id == field.RefId || row.Storage.Status == ParameterStorageStatus.NotDeclared) continue;
                var storage = row.Storage;
                if (storage.Status != ParameterStorageStatus.Supported) Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
                if (storage.Kind != ParameterStorageKind.RelativeMemory) continue;
                if (storage.LoadStateMachine is null || storage.StartAddress is null || storage.BitOffset is null)
                    Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
                if (storage.LoadStateMachine != arena.LoadStateMachine) continue;
                var otherStart = (ulong)storage.StartAddress.Value * 8 + storage.BitOffset.Value;
                // An unknown positive width cannot reach backwards from a known start
                // at or beyond the selected end. No other unknown span is ignored.
                if (otherStart >= end) continue;
                if (storage.Footprint is not { Status: ParameterFootprintStatus.Known, BitLength: { } width }
                    || otherStart + width > start) Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
            }
        }
    }

    private static void RelativeOwnershipGate(List<ParameterPatchField> fields, Dictionary<string, XElement[]> segments,
        Dictionary<string, ResourcePatchArena> arenas)
    {
        var declarations = new List<(string Id, byte Lsm, uint Start, ulong? Length)>();
        foreach (var (id, nodes) in segments)
            foreach (var segment in nodes)
            {
                var lsm = (byte)0; var start = 0u;
                if (!RelativeHeaderKnown(segment)
                    || !byte.TryParse((string?)segment.Attribute("LoadStateMachine"), NumberStyles.Integer, CultureInfo.InvariantCulture, out lsm)
                    || !uint.TryParse((string?)segment.Attribute("Offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
                    || start > 1_048_575) Reject(ParameterPatchIssueCode.AmbiguousOverlap);
                var relevant = fields.Where(f => arenas[f.ResourceId].LoadStateMachine == lsm).ToArray();
                if (relevant.Length == 0 || relevant.All(f => (ulong)start >= (ulong)f.StartAddress + (f.BitOffset + f.BitLength + 7) / 8)) continue;
                ulong? extent = null;
                if (uint.TryParse((string?)segment.Attribute("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size <= 1_048_575)
                {
                    var payloads = segment.Elements().Where(e => e.Name.LocalName == "Data").Take(2).ToArray();
                    if (payloads.Length > 1) Reject(ParameterPatchIssueCode.AmbiguousOverlap);
                    var supplied = payloads.Length == 0 ? 0 : DecodedLength(Payload(payloads[0], segment.Name.Namespace, ParameterPatchIssueCode.AmbiguousOverlap, id), ParameterPatchIssueCode.AmbiguousOverlap, id);
                    extent = Math.Max((ulong)size, (ulong)supplied);
                }
                declarations.Add((id, lsm, start, extent));
            }
        foreach (var field in fields)
        {
            var start = (ulong)field.StartAddress; var end = start + (field.BitOffset + field.BitLength + 7) / 8;
            foreach (var owner in declarations.Where(d => d.Id != field.ResourceId && d.Lsm == arenas[field.ResourceId].LoadStateMachine))
                if (owner.Start < end && (owner.Length is null || owner.Start + owner.Length.Value > start))
                    Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
        }
    }

    private static bool RelativeHeaderKnown(XElement segment) => !segment.Attributes().Any(a => !a.IsNamespaceDeclaration
            && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("Id" or "Size" or "Offset" or "LoadStateMachine")))
        && !segment.Elements().Any(e => e.Name.Namespace != segment.Name.Namespace || e.Name.LocalName is not ("Data" or "Mask"))
        && !segment.Nodes().Any(n => n is not XElement and not XComment && (n is not XText text || !string.IsNullOrWhiteSpace(text.Value)));

    private static ResourcePatchRequirement[] Requirements(XElement application, List<InitialMemoryResource> resources)
    {
        var requirements = new List<ResourcePatchRequirement>
        {
            new(ResourcePatchRequirementCode.CompleteParameterActivation), new(ResourcePatchRequirementCode.GeneratedTables),
            new(ResourcePatchRequirementCode.LoadProcedure), new(ResourcePatchRequirementCode.PhysicalResourceMapping),
        };
        if (application.Descendants().Any(e => e.Name.LocalName is "Property" or "LdCtrlLoadImageProp" or "LdCtrlWriteProp"))
            requirements.Add(new(ResourcePatchRequirementCode.PropertyInitialization));
        if ((string?)application.Attribute("IsSecureEnabled") is "true" or "True" or "1")
            requirements.Add(new(ResourcePatchRequirementCode.SecureConfiguration));
        foreach (var resource in resources)
        {
            if (resource.DeclaredSize is not { } size) requirements.Add(new(ResourcePatchRequirementCode.UnknownDeclaredSize, resource.Id));
            else if (resource.Data.Length < size) requirements.Add(new(ResourcePatchRequirementCode.PayloadShorterThanDeclaredSize, resource.Id));
        }
        var ns = application.Name.Namespace;
        foreach (var segment in application.Elements(ns + "Static").Elements(ns + "Code").Elements()
            .Where(e => e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment" && !e.Elements(ns + "Data").Any()))
        {
            if (requirements.Count >= 63) { requirements.Add(new(ResourcePatchRequirementCode.AdditionalRequirementsOmitted)); break; }
            requirements.Add(new(ResourcePatchRequirementCode.MissingInitialResources, Safe((string?)segment.Attribute("Id")) ? (string?)segment.Attribute("Id") : null));
        }
        return requirements.ToArray();
    }

    private static void OwnershipGate(List<ParameterPatchField> fields, Dictionary<string, XElement[]> segments)
    {
        // Ownership refers to the declared/supplied absolute resource arena only. This does
        // not resolve physical LSM/property resources or authorize extending any segment.
        var arena = new List<(string Id, ulong Start, ulong? Length)>();
        foreach (var (id, declarations) in segments)
        {
            foreach (var declaration in declarations)
            {
                if (declaration.Attribute("UserMemory") is { Value: "true" or "1" }) continue;
                if (!uint.TryParse((string?)declaration.Attribute("Address"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var address))
                    Reject(ParameterPatchIssueCode.AmbiguousOverlap);
                if (fields.All(f => (ulong)address >= (ulong)f.StartAddress + (f.BitOffset + f.BitLength + 7) / 8)) continue;
                ulong? extent = null;
                if (uint.TryParse((string?)declaration.Attribute("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size <= 1_048_575
                    && declaration.Attribute("UserMemory") is not { Value: not ("false" or "0") })
                {
                    var payloads = declaration.Elements().Where(e => e.Name.LocalName == "Data").Take(2).ToArray();
                    if (payloads.Length > 1) Reject(ParameterPatchIssueCode.AmbiguousOverlap);
                    var supplied = payloads.Length == 0 ? 0 : DecodedLength(Payload(payloads[0], declaration.Name.Namespace, ParameterPatchIssueCode.AmbiguousOverlap, id),
                        ParameterPatchIssueCode.AmbiguousOverlap, id);
                    extent = Math.Max((ulong)size, (ulong)supplied);
                }
                arena.Add((id, address, extent));
            }
        }
        foreach (var field in fields)
        {
            var start = (ulong)field.StartAddress; var end = start + (field.BitOffset + field.BitLength + 7) / 8;
            foreach (var owner in arena.Where(a => a.Id != field.ResourceId))
                if (owner.Start < end && (owner.Length is null || owner.Start + owner.Length.Value > start))
                    Reject(ParameterPatchIssueCode.AmbiguousOverlap, field.RefId);
        }
    }
}
