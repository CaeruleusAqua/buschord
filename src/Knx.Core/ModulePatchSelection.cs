using System.Xml.Linq;

namespace Knx;

/// <summary>
/// Private original-source indexes and selected Enum/DenseTime preflight. A match
/// is never module-presence or patch authority: the fresh internal origin and scope
/// verifier must still prove the original call, branch, arguments and geometry.
/// </summary>
internal sealed class ModulePatchSelection
{
    private sealed record Candidate(XElement Call, XElement Definition, XElement Parameter,
        XElement Reference, XElement Type, string Instance, bool Native);
    private sealed class Match
    {
        internal Candidate? Candidate;
        internal bool Ambiguous, HasEnum, HasDenseTime;
    }
    private sealed record QualifiedRef(XElement Reference, bool Ambiguous, bool HasEnum, bool HasDenseTime);
    private sealed record DefinitionRefs(Dictionary<string, QualifiedRef> Qualified,
        ILookup<string, XElement> Parameters, HashSet<string> EnumParameters, HashSet<string> DenseTimeParameters, bool Native);
    private readonly XElement _root, _application;
    private readonly XNamespace _ns;
    private ILookup<string, XElement> _allParameters = null!, _allReferences = null!, _allTypes = null!;
    private ILookup<string, XElement> _definitions = null!, _calls = null!, _targets = null!, _directCallsByDefinition = null!;
    private XElement[] _directCalls = [];
    private HashSet<string> _enumTypes = new(StringComparer.Ordinal);
    private HashSet<string> _denseTimeTypes = new(StringComparer.Ordinal);
    private bool _indexed;
    private readonly Dictionary<string, DefinitionRefs> _definitionRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Match> _matches;
    private readonly Dictionary<string, Candidate> _enumProofs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Candidate> _denseTimeProofs = new(StringComparer.Ordinal);
    private ModuleOriginCatalog? _source;
    internal ModuleOriginCatalog Source => _source ??= new(_root);
    internal ILookup<string, XElement> Parameters { get; }
    internal ILookup<string, XElement> References { get; }
    internal ILookup<string, XElement> Types { get; }
    internal ILookup<string, XElement> Segments { get; }
    internal ILookup<string, XElement> Definitions { get { IndexModules(); return _definitions; } }
    internal ILookup<string, XElement> Calls { get { IndexModules(); return _calls; } }
    internal ILookup<string, XElement> Targets { get { IndexModules(); return _targets; } }

    internal ModulePatchSelection(XElement root, XElement application, IReadOnlyList<ParameterPatchValue> selected)
    {
        _root = root; _application = application; _ns = application.Name.Namespace;
        _matches = selected.ToDictionary(v => v.RefId, _ => new Match(), StringComparer.Ordinal);
        // Genuine root-only requests keep their previous lightweight path. Full
        // module indexes are built only when original root pairing is absent.
        var statics = application.Elements(_ns + "Static");
        Parameters = statics.Elements(_ns + "Parameters").Elements(_ns + "Parameter").ToLookup(Id, StringComparer.Ordinal);
        References = statics.Elements(_ns + "ParameterRefs").Elements(_ns + "ParameterRef").ToLookup(Id, StringComparer.Ordinal);
        Types = statics.Elements(_ns + "ParameterTypes").Elements(_ns + "ParameterType").ToLookup(Id, StringComparer.Ordinal);
        Segments = statics.Elements(_ns + "Code").Elements(_ns + "AbsoluteSegment").ToLookup(Id, StringComparer.Ordinal);
    }
    private void IndexModules()
    {
        if (_indexed) return;
        // The existing origin producer cannot mint module proof above this same
        // source ceiling. This is NOT a new root-only or unrelated-enum limit.
        if (_root.DescendantsAndSelf().Take(500_001).Count() > 500_000)
            throw new InvalidDataException("Selected module source exceeds the origin safety limit.");
        var parameters = new List<XElement>(); var references = new List<XElement>(); var types = new List<XElement>();
        var definitions = new List<XElement>(); var calls = new List<XElement>();
        // One native source walk, independent of selection count; no enum choices
        // or per-character trie nodes are copied. XML still owns its usual budget.
        foreach (var node in _application.Descendants())
        {
            if (node.Name.Namespace != _ns) continue;
            switch (node.Name.LocalName)
            {
                case "Parameter": parameters.Add(node); break;
                case "ParameterRef": references.Add(node); break;
                case "ParameterType": types.Add(node); break;
                case "ModuleDef": definitions.Add(node); break;
                case "Module": calls.Add(node); break;
            }
        }
        _allParameters = parameters.ToLookup(Id, StringComparer.Ordinal);
        _allReferences = references.ToLookup(Id, StringComparer.Ordinal);
        _allTypes = types.ToLookup(Id, StringComparer.Ordinal);
        _enumTypes = _allTypes.Where(g => !RootNumberType(g.Key) && g.Any(t => t.Elements(_ns + "TypeRestriction").Any()))
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        _denseTimeTypes = _allTypes.Where(g => !RootNumberType(g.Key) && g.Any(t => t.Elements(_ns + "TypeTime")
                .Any(s => OfflineParameterPatch.IsDenseTimeUnit((string?)s.Attribute("Unit")))))
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        _definitions = definitions.ToLookup(Id, StringComparer.Ordinal); _calls = calls.ToLookup(Id, StringComparer.Ordinal);
        _targets = references.ToLookup(e => (string?)e.Attribute("RefId") ?? "", StringComparer.Ordinal);
        _directCalls = calls.Where(DirectCall).ToArray();
        _directCallsByDefinition = _directCalls.ToLookup(e => (string?)e.Attribute("RefId") ?? "", StringComparer.Ordinal);
        _indexed = true;
    }
    private static string Id(XElement e) => (string?)e.Attribute("Id") ?? "";
    private static XElement? One(IEnumerable<XElement> values) { var pair = values.Take(2).ToArray(); return pair.Length == 1 ? pair[0] : null; }
    private bool Path(XElement element, string container, XElement owner) => element.Parent is { } parent && parent.Name == _ns + container
        && parent.Parent is { } stat && stat.Name == _ns + "Static" && ReferenceEquals(stat.Parent, owner);
    private bool DirectCall(XElement call)
    {
        var ancestors = call.Ancestors().TakeWhile(e => !ReferenceEquals(e, _application)).ToArray();
        return ancestors.Length > 0 && ancestors[^1].Name == _ns + "Dynamic"
            && ancestors.All(e => e.Name.Namespace == _ns && e.Name.LocalName is
                "Dynamic" or "Channel" or "ChannelIndependentBlock" or "ParameterBlock" or "choose" or "when");
    }
    private bool RootPair(string id)
    {
        var reference = One(References[id]);
        return reference is not null && One(Parameters[(string?)reference.Attribute("RefId") ?? ""]) is not null
            || !References[id].Any() && One(Parameters[id]) is not null;
    }
    private bool RootNumberType(string id)
    {
        var type = One(Types[id]);
        // Uninstantiated local definitions do not alter the published Number
        // profile's original ROOT type proof. Their same-id enum is unrelated.
        return type is not null && type.Elements().Take(2).ToArray() is { Length: 1 } shape && shape[0].Name == _ns + "TypeNumber";
    }
    private DefinitionRefs Refs(XElement definition)
    {
        var id = Id(definition);
        if (_definitionRefs.TryGetValue(id, out var cached)) return cached;
        // Duplicate definitions are retained as ambiguity facts, never overwritten.
        var statics = Definitions[id].SelectMany(d => d.Elements(_ns + "Static"));
        var parameters = statics.Elements(_ns + "Parameters").Elements(_ns + "Parameter").ToLookup(Id, StringComparer.Ordinal);
        var enumParameters = parameters.SelectMany(g => g).Where(p => _enumTypes.Contains((string?)p.Attribute("ParameterType")
            ?? (string?)p.Attribute("ParameterTypeId") ?? "")).Select(Id).ToHashSet(StringComparer.Ordinal);
        var denseTimeParameters = parameters.SelectMany(g => g).Where(p => _denseTimeTypes.Contains((string?)p.Attribute("ParameterType")
            ?? (string?)p.Attribute("ParameterTypeId") ?? "")).Select(Id).ToHashSet(StringComparer.Ordinal);
        var qualified = new Dictionary<string, QualifiedRef>(StringComparer.Ordinal);
        foreach (var reference in statics.Elements(_ns + "ParameterRefs").Elements(_ns + "ParameterRef"))
        {
            var original = Id(reference);
            if (!ModuleOriginCatalog.Safe(original) || !original.StartsWith(id + "_", StringComparison.Ordinal)) continue;
            // Keys come only from original source refs. Alternate span lookup below
            // is a candidate search; final forward ScopeId equality is compulsory.
            var suffix = original[id.Length..];
            var hasEnum = enumParameters.Contains((string?)reference.Attribute("RefId") ?? "");
            var hasDenseTime = denseTimeParameters.Contains((string?)reference.Attribute("RefId") ?? "");
            if (qualified.TryGetValue(suffix, out var previous))
                qualified[suffix] = previous with { Ambiguous = true, HasEnum = previous.HasEnum || hasEnum, HasDenseTime = previous.HasDenseTime || hasDenseTime };
            else qualified.Add(suffix, new(reference, false, hasEnum, hasDenseTime));
        }
        var native = ModuleOriginCatalog.Safe(id) && ReferenceEquals(One(Definitions[id]), definition)
            && definition.Parent is { } parent && parent.Name == _ns + "ModuleDefs" && ReferenceEquals(parent.Parent, _application)
            && definition.Elements(_ns + "Static").Count() == 1;
        return _definitionRefs[id] = new(qualified, parameters, enumParameters, denseTimeParameters, native);
    }
    private void Add(string requested, XElement call, XElement definition, XElement reference, DefinitionRefs refs)
    {
        var instance = ApplicationModules.InstanceId(Id(call), []);
        if (ApplicationModules.ScopeId(Id(definition), instance, Id(reference)) != requested) return;
        var match = _matches[requested];
        match.HasEnum |= refs.EnumParameters.Contains((string?)reference.Attribute("RefId") ?? "");
        match.HasDenseTime |= refs.DenseTimeParameters.Contains((string?)reference.Attribute("RefId") ?? "");
        var parameter = One(refs.Parameters[(string?)reference.Attribute("RefId") ?? ""]);
        if (parameter is null) { match.Ambiguous = true; return; }
        var typeId = (string?)parameter.Attribute("ParameterType") ?? "";
        var type = One(Types[typeId]);
        // Local/ambiguous types may never turn a flattened enum into native proof.
        match.HasEnum |= _enumTypes.Contains(typeId);
        match.HasDenseTime |= _denseTimeTypes.Contains(typeId);
        if (type is null) { match.Ambiguous = true; return; }
        var native = refs.Native && DirectCall(call) && ModuleOriginCatalog.Safe(Id(call)) && ModuleOriginCatalog.Safe(instance)
            && ReferenceEquals(One(Calls[Id(call)]), call) && ReferenceEquals(One(_allParameters[Id(parameter)]), parameter)
            && ReferenceEquals(One(_allReferences[Id(reference)]), reference) && ReferenceEquals(One(_allTypes[typeId]), type)
            && ModuleOriginCatalog.Safe(Id(parameter)) && ModuleOriginCatalog.Safe(Id(reference)) && ModuleOriginCatalog.Safe(typeId)
            && ModuleOriginCatalog.TryLiteralArguments(call, definition, out _)
            && !Parameters[requested].Any() && !References[requested].Any()
            && !Parameters[ApplicationModules.ScopeId(Id(definition), instance, Id(parameter))].Any();
        var candidate = new Candidate(call, definition, parameter, reference, type, instance, native);
        if (match.Candidate is not null) match.Ambiguous = true;
        else match.Candidate = candidate;
    }
    internal void PreflightTypes(Action<XElement> readEnum, Action<XElement> readDenseTime)
    {
        if (_matches.Keys.All(RootPair)) return;
        IndexModules();
        // At most 64 request slots plus ambiguity flags are retained. A source-
        // generated prefix filters calls BEFORE any definition-ref index is built;
        // lookup is O(1) in the native suffix dictionary, not Calls x all Refs.
        // Inactive alternatives are not actual instances: no raw-call-count cap.
        foreach (var call in _directCalls)
        {
            if (!ModuleOriginCatalog.Safe(Id(call))) continue;
            var instance = ApplicationModules.InstanceId(Id(call), []);
            var prefix = instance + "_";
            var requested = _matches.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (requested.Length == 0) continue;
            var definition = Definitions[(string?)call.Attribute("RefId") ?? ""].FirstOrDefault();
            if (definition is null) continue;
            var refs = Refs(definition); var lookup = refs.Qualified.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach (var id in requested)
                if (lookup.TryGetValue(id.AsSpan(instance.Length), out var candidate))
                {
                    _matches[id].HasEnum |= candidate.HasEnum;
                    _matches[id].HasDenseTime |= candidate.HasDenseTime;
                    _matches[id].Ambiguous |= candidate.Ambiguous;
                    Add(id, call, definition, candidate.Reference, refs);
                }
        }
        // Unqualified refs are original literal ids, not inferred suffixes. Each
        // requested raw id is indexed once; >1 direct calls are an ambiguity even
        // when an unrelated branch would hide one. No dictionary overwrite wins.
        foreach (var requested in _matches.Keys)
        foreach (var reference in _allReferences[requested])
        {
            if (reference.Parent?.Parent?.Parent is not { } definition || definition.Name != _ns + "ModuleDef"
                || !Path(reference, "ParameterRefs", definition) || Id(reference).StartsWith(Id(definition) + "_", StringComparison.Ordinal)) continue;
            var refs = Refs(definition);
            foreach (var call in _directCallsByDefinition[Id(definition)].Take(2)) Add(requested, call, definition, reference, refs);
        }
        foreach (var (requested, match) in _matches)
        {
            if (!match.HasEnum && !match.HasDenseTime) continue; // Existing Number requests get no new type-profile gate.
            var candidate = match.Candidate;
            if (candidate is null || match.Ambiguous || !candidate.Native)
                throw new InvalidDataException("Selected module type has ambiguous or non-native original source.");
            var shapes = candidate.Type.Elements().Take(2).ToArray();
            if (shapes.Length != 1) throw new InvalidDataException("Selected module type has no unique root native shape.");
            if (match.HasEnum && shapes[0].Name == _ns + "TypeRestriction")
            {
                readEnum(shapes[0]); // Includes 4096 count BEFORE Snapshot's typed choice allocation.
                _enumProofs.Add(requested, candidate);
            }
            else if (match.HasDenseTime && shapes[0].Name == _ns + "TypeTime")
            {
                readDenseTime(shapes[0]);
                _denseTimeProofs.Add(requested, candidate);
            }
            else throw new InvalidDataException("Selected module type is outside its checked native profile.");
        }
    }
    internal bool ProvesEnum(ParameterInstance row, ModuleParameterOrigin origin, XElement type)
        => Proves(_enumProofs, row, origin, type);
    internal bool HasDenseTimeProof(string reference) => _denseTimeProofs.ContainsKey(reference);
    internal bool ProvesDenseTime(ParameterInstance row, ModuleParameterOrigin origin, XElement type)
        => Proves(_denseTimeProofs, row, origin, type);
    private bool Proves(Dictionary<string, Candidate> proofs, ParameterInstance row, ModuleParameterOrigin origin, XElement type)
        => proofs.TryGetValue(row.Id, out var candidate) && ReferenceEquals(Source.At(origin.Occurrence.CallKey), candidate.Call)
            && ReferenceEquals(Source.At(origin.Occurrence.DefinitionKey), candidate.Definition)
            && ReferenceEquals(Source.At(origin.Parameter.Key), candidate.Parameter) && ReferenceEquals(Source.At(origin.Reference.Key), candidate.Reference)
            && ReferenceEquals(type, candidate.Type) && origin.Occurrence.InstanceId == candidate.Instance;
}
