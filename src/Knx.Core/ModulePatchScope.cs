using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>Revalidates internally produced origins against the private original XML, never an id suffix.</summary>
internal sealed class ModulePatchScope
{
    private readonly ModuleOriginCatalog _source;
    private readonly XElement _application;
    private readonly XNamespace _ns;
    private readonly ApplicationSnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, string?> _saved;
    private readonly Dictionary<string, string?> _defaults;
    private readonly Dictionary<string, ParameterInstance> _rows;
    private readonly ILookup<string, XElement> _parameters, _references, _types, _segments, _definitions, _calls, _targets;
    private readonly ModulePatchSelection _preflight;
    private bool _includesEnum;
    private bool _includesDenseTime;
    private readonly List<(string Reference, string OriginHash, uint? Address, byte? Bit, uint? Width)> _selected = [];
    private sealed record ControlWitness(string Reference, string Parameter, string Type, int Choose, int When, string? Test, string? DefaultBranch, string Value);
    internal ModulePatchScope(XElement root, ApplicationSnapshot snapshot, IReadOnlyDictionary<string, string?> saved, ModulePatchSelection preflight)
    {
        _preflight = preflight; _source = preflight.Source; _snapshot = snapshot; _saved = saved;
        _application = root.DescendantsAndSelf().Single(e => e.Name.LocalName == "ApplicationProgram"); _ns = _application.Name.Namespace;
        _parameters = preflight.Parameters; _references = preflight.References; _types = preflight.Types;
        _segments = preflight.Segments; _definitions = preflight.Definitions; _calls = preflight.Calls; _targets = preflight.Targets;
        _defaults = DynamicParameters.ReadDeclaredValues(root, out _);
        _rows = snapshot.Instances.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }
    private static string Id(XElement e) => (string?)e.Attribute("Id") ?? "";
    private static XElement? One(IEnumerable<XElement> elements) { var a = elements.Take(2).ToArray(); return a.Length == 1 ? a[0] : null; }
    private bool NativePath(XElement? element, string container, XElement owner) => element?.Parent is { } parent && parent.Name == _ns + container
        && parent.Parent is { } stat && stat.Name == _ns + "Static" && ReferenceEquals(stat.Parent, owner);
    internal ParameterPatchIssueCode? Validate(ParameterInstance row, XElement expandedParameter, XElement? expandedReference, HashSet<string> watched)
    {
        var origin = row.ModuleOrigin;
        if (origin is null || row.ParameterType?.DataType is not (ParameterDataType.Number or ParameterDataType.Enumeration or ParameterDataType.Time))
            return ParameterPatchIssueCode.UnsupportedParameter;
        var isEnum = row.ParameterType.DataType == ParameterDataType.Enumeration;
        var isDenseTime = row.ParameterType.DataType == ParameterDataType.Time;
        if (isDenseTime && !_preflight.HasDenseTimeProof(row.Id)) return ParameterPatchIssueCode.UnsupportedParameter;
        var occurrence = origin.Occurrence; var parameter = _source.At(origin.Parameter.Key); var reference = _source.At(origin.Reference.Key);
        var definition = _source.At(occurrence.DefinitionKey); var call = _source.At(occurrence.CallKey);
        if (parameter is null || reference is null || definition is null || call is null || expandedReference is null || occurrence.Choices.Count > 16
            || _application.Elements(_ns + "Static").Count() != 1 || _application.Elements(_ns + "ModuleDefs").Count() != 1
            || _application.Elements(_ns + "Dynamic").Count() != 1 || definition.Elements(_ns + "Static").Count() != 1
            || definition.Name != _ns + "ModuleDef" || definition.Parent is not { } defContainer || defContainer.Name != _ns + "ModuleDefs" || !ReferenceEquals(defContainer.Parent, _application)
            || call.Name != _ns + "Module" || Id(call) != occurrence.CallId || Id(definition) != occurrence.DefinitionId
            || (string?)call.Attribute("RefId") != occurrence.DefinitionId
            || !ReferenceEquals(One(_definitions[occurrence.DefinitionId]), definition) || !ReferenceEquals(One(_calls[occurrence.CallId]), call)
            || _source.Hash(occurrence.CallKey) != occurrence.CallHash || _source.Hash(occurrence.DefinitionKey) != occurrence.DefinitionHash
            || _source.Hash(origin.Parameter.Key) != origin.Parameter.Hash || _source.Hash(origin.Reference.Key) != origin.Reference.Hash
            || !NativePath(parameter, "Parameters", definition) || !NativePath(reference, "ParameterRefs", definition)
            || Id(parameter) != origin.Parameter.OriginalId || Id(reference) != origin.Reference.OriginalId
            || (string?)reference.Attribute("RefId") != Id(parameter) || origin.Parameter.ExpandedId != row.ParameterId || origin.Reference.ExpandedId != row.Id
            || Id(expandedParameter) != origin.Parameter.ExpandedId || Id(expandedReference!) != origin.Reference.ExpandedId
            || (string?)parameter.Attribute("ParameterType") != row.ParameterTypeId || One(_types[row.ParameterTypeId!]) is not { } type
            || type.Elements().Count() != 1 || type.Elements().Single().Name != _ns + (isDenseTime ? "TypeTime" : isEnum ? "TypeRestriction" : "TypeNumber")
            || isEnum && !_preflight.ProvesEnum(row, origin, type)
            || isDenseTime && !_preflight.ProvesDenseTime(row, origin, type)
            || parameter.Attribute("BaseValue") is not null || reference.Attribute("ParameterType") is not null || reference.Attribute("ParameterTypeId") is not null
            || definition.Elements(_ns + "Static").Elements(_ns + "Code").Any())
            return ParameterPatchIssueCode.InvalidDeclaration;
        if (!ModuleOriginCatalog.Safe(occurrence.InstanceId) || !ModuleOriginCatalog.Safe(occurrence.CallId) || !ModuleOriginCatalog.Safe(occurrence.DefinitionId)
            || _snapshot.ModuleInstances.Count(m => m.Id == occurrence.NativeInstanceId && m.RefId == occurrence.NativeReferenceId) != 1)
            return ParameterPatchIssueCode.InvalidDeclaration;
        var ancestors = call.Ancestors().TakeWhile(e => !ReferenceEquals(e, _application)).ToArray();
        if (ancestors.Length == 0 || ancestors.Last().Name != _ns + "Dynamic"
            || ancestors.Any(e => e.Name.Namespace != _ns || e.Name.LocalName is not ("Dynamic" or "Channel" or "ChannelIndependentBlock" or "ParameterBlock" or "choose" or "when"))
            || ancestors.Count(e => e.Name.LocalName == "choose") != occurrence.Choices.Count)
            return ParameterPatchIssueCode.UnsupportedParameter;
        if (!ReferenceEquals(One(_targets[Id(parameter)]), reference))
            return ParameterPatchIssueCode.AmbiguousOverlap;
        if (!ModuleOriginCatalog.TryLiteralArguments(call, definition, out var literals) || literals.Count != occurrence.Arguments.Count
            || occurrence.Arguments.Any(a => !literals.TryGetValue(a.Id, out var value) || value != a.Literal || a.Literal != a.Effective || !ModuleOriginCatalog.Safe(a.ExpandedId)))
            return ParameterPatchIssueCode.UnsupportedParameter;
        var controls = new List<ControlWitness>();
        foreach (var choice in occurrence.Choices)
        {
            var choose = _source.At(choice.ChooseKey); var when = _source.At(choice.WhenKey);
            if (choose?.Name != _ns + "choose" || when?.Name != _ns + "when" || !ReferenceEquals(when.Parent, choose)
                || !ancestors.Contains(choose) || !ancestors.Contains(when) || _source.Hash(choice.ChooseKey) != choice.ChooseHash
                || (string?)choose.Attribute("ParamRefId") != choice.SelectorId || !_rows.TryGetValue(choice.SelectorId, out var control))
                return ParameterPatchIssueCode.InvalidDeclaration;
            var sourceRef = One(_references[choice.SelectorId]);
            var sourceParameter = One(_parameters[sourceRef is null ? choice.SelectorId : (string?)sourceRef.Attribute("RefId") ?? ""]);
            if (sourceParameter is null || sourceRef is null || !NativePath(sourceParameter, "Parameters", _application) || !NativePath(sourceRef, "ParameterRefs", _application)
                || !ModuleOriginCatalog.Safe(choice.SelectorId) || !ModuleOriginCatalog.Safe(Id(sourceParameter)) || !ModuleOriginCatalog.Safe(control.ParameterTypeId)
                || control.ParameterId != Id(sourceParameter) || (string?)sourceParameter.Attribute("ParameterType") != control.ParameterTypeId
                || sourceParameter.Attribute("ParameterTypeId") is not null || One(_types[control.ParameterTypeId!]) is not { } controlType
                || !NativePath(controlType, "ParameterTypes", _application) || sourceParameter.Attribute("BaseValue") is not null
                || sourceRef.Attribute("ParameterType") is not null || sourceRef.Attribute("ParameterTypeId") is not null
                || control.ModuleOrigin is not null || control.IsDerivedValue || _snapshot.Evaluation.AssignedParameters.Contains(choice.SelectorId))
                return ParameterPatchIssueCode.UnsupportedDependency;
            var ordinary = _saved.TryGetValue(choice.SelectorId, out var saved) ? saved : _defaults.GetValueOrDefault(choice.SelectorId);
            if (ordinary is not { Length: <= 64 } || ordinary.Any(char.IsControl) || ordinary != choice.RawValue || control.Value != ordinary
                || !ParameterValidation.TryValidate(control.ParameterType, ordinary, out _)) return ParameterPatchIssueCode.UnsupportedDependency;
            var values = new Dictionary<string, string?>(StringComparer.Ordinal) { [choice.SelectorId] = ordinary };
            if (!ReferenceEquals(DynamicParameters.SelectStructureBranch(choose, values), when)) return ParameterPatchIssueCode.UnsupportedDependency;
            controls.Add(new(choice.SelectorId, Id(sourceParameter), control.ParameterTypeId!, choice.ChooseKey, choice.WhenKey,
                (string?)when.Attribute("test"), (string?)when.Attribute("default"), ordinary));
            watched.Add(choice.SelectorId); watched.Add(Id(sourceParameter));
            foreach (var alias in _references.SelectMany(g => g).Where(e => (string?)e.Attribute("RefId") == Id(sourceParameter))) watched.Add(Id(alias));
        }
        var locations = parameter.Elements().ToArray();
        if (locations.Length != 1 || locations[0].Name != _ns + "Memory" || locations[0].HasElements || !string.IsNullOrWhiteSpace(locations[0].Value))
            return ParameterPatchIssueCode.UnsupportedParameter;
        var memory = locations[0]; var resource = (string?)memory.Attribute("CodeSegment") ?? "";
        var segment = One(_segments[resource]); var basis = 0L;
        if (segment is null || memory.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
            || a.Name.LocalName is not ("CodeSegment" or "Offset" or "BitOffset" or "BaseOffset")))) return ParameterPatchIssueCode.InvalidDeclaration;
        if (memory.Attribute("BaseOffset") is { } baseId)
        {
            var args = occurrence.Arguments.Where(a => a.Id == baseId.Value).ToArray();
            if (args.Length != 1 || (string?)expandedParameter.Element(_ns + "Memory")?.Attribute("BaseOffset") != args[0].ExpandedId)
                return ParameterPatchIssueCode.InvalidDeclaration;
            basis = args[0].Effective;
        }
        if (!uint.TryParse((string?)segment.Attribute("Address"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var address)
            || !uint.TryParse((string?)memory.Attribute("Offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset > 1_048_575
            || !byte.TryParse((string?)memory.Attribute("BitOffset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit) || bit > 7
            || basis < 0 || checked((long)address + offset + basis) is var actual && (actual > uint.MaxValue || row.Storage.StartAddress != actual)
            || row.Storage.CodeSegmentId != resource || row.Storage.BitOffset != bit || row.Storage.Kind != ParameterStorageKind.AbsoluteMemory)
            return ParameterPatchIssueCode.InvalidDeclaration;
        _selected.Add((row.Id, ParameterPatchSourceBinding.Hash(new { origin, controls }), row.Storage.StartAddress, row.Storage.BitOffset, row.Storage.Footprint?.BitLength));
        _includesEnum |= isEnum;
        _includesDenseTime |= isDenseTime;
        return null;
    }
    internal int ProfileVersion => _includesDenseTime ? 3 : _includesEnum ? 2 : 1;
    internal string? Witness()
    {
        if (_selected.Count == 0) return null;
        if (_snapshot.ModuleInstances.Count > 256 || _snapshot.ModuleInstances.Any(m => !ModuleOriginCatalog.Safe(m.Id) || !ModuleOriginCatalog.Safe(m.RefId)
            || m.RepeatIndex.Length > 512 || m.RepeatIndex.Any(char.IsControl))) throw new InvalidDataException("Unbounded module identity metadata.");
        // Number-only witnesses retain the exact profile1 envelope and values.
        // Root enums do not participate here and cannot promote a Number scope.
        return ParameterPatchSourceBinding.Hash(new { profile = ProfileVersion, selected = _selected.OrderBy(p => p.Reference, StringComparer.Ordinal)
            .Select(p => new { p.Reference, p.OriginHash, p.Address, p.Bit, p.Width }).ToArray(),
            instances = _snapshot.ModuleInstances.Select(m => new { m.Id, m.RefId, m.RepeatIndex }).ToArray() });
    }
}
