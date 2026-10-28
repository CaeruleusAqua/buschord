using System.Collections.Frozen;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed record NativeStateIdentity(int SourceKey, string ModuleInstanceId);
internal sealed record NativeControllerActivity(string RefId, int NormalReferenceCount, int ValueAssignmentReferenceCount, bool Active, long Value);

/// <summary>Fresh ordinary-reference activation over an acyclic declared state graph; no GUI visibility or retained activity is authoritative.</summary>
internal sealed class NativeActivationProof
{
    private const int StateLimit = 100000;
    private readonly NativeCommunicationOriginCatalog? _catalog;
    private readonly ApplicationSnapshot? _snapshot;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<GeneratedTableIssue> Issues { get; }
    internal string? Fingerprint { get; }
    internal IReadOnlyDictionary<string, NativeControllerActivity> Controllers { get; }
    internal IReadOnlySet<string> ActiveObjectReferences { get; }
    internal IReadOnlyList<NativeStateIdentity> ActiveParameterOwners { get; }
    internal IReadOnlyList<NativeStateIdentity> ActiveWhenStates { get; }
    private NativeActivationProof(GeneratedTableIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Controllers = new Dictionary<string, NativeControllerActivity>().ToFrozenDictionary(StringComparer.Ordinal);
        ActiveObjectReferences = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        ActiveParameterOwners = Array.AsReadOnly(Array.Empty<NativeStateIdentity>()); ActiveWhenStates = Array.AsReadOnly(Array.Empty<NativeStateIdentity>());
    }
    private NativeActivationProof(NativeCommunicationOriginCatalog catalog, ApplicationSnapshot snapshot, Dictionary<string, NativeControllerActivity> controllers, HashSet<string> objects,
        NativeStateIdentity[] owners, NativeStateIdentity[] whens, object facts)
    {
        _catalog = catalog; _snapshot = snapshot;
        Controllers = controllers.ToFrozenDictionary(StringComparer.Ordinal); ActiveObjectReferences = objects.ToFrozenSet(StringComparer.Ordinal);
        ActiveParameterOwners = Array.AsReadOnly(owners); ActiveWhenStates = Array.AsReadOnly(whens); Issues = Array.AsReadOnly(Array.Empty<GeneratedTableIssue>());
        Fingerprint = NativeCommunicationGuard.Hash(new { version = 1, controllers = controllers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(), objects = objects.Order(StringComparer.Ordinal).ToArray(), owners, whens, facts });
    }
    internal bool BelongsTo(NativeCommunicationOriginCatalog catalog, ApplicationSnapshot snapshot)
        => IsAvailable && ReferenceEquals(_catalog, catalog) && ReferenceEquals(_snapshot, snapshot);
    internal static NativeActivationProof Create(NativeCommunicationOriginCatalog catalog, XElement originalApplication, ApplicationSnapshot snapshot,
        IReadOnlyDictionary<string, string?> values, IReadOnlyList<string> additionalControllers)
    {
        try { return Mint(catalog, originalApplication, snapshot, values, additionalControllers); }
        catch (NativeCommunicationRejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return new(new(GeneratedTableIssueCode.IncompleteActivation)); }
    }
    private sealed class State(string kind, int parent = -1, XElement? element = null)
    {
        internal string Kind = kind;
        internal int Parent = parent;
        internal XElement? Element = element;
        internal NativeStateIdentity? Identity;
        internal string? RefId;
        internal int Controller = -1;
        internal Condition? Test;
        internal bool Default;
        internal bool Matches;
        internal bool Active;
        internal int Count;
        internal readonly List<int> Children = [];
        internal readonly List<int> Dependencies = [];
        internal readonly List<int> Dependents = [];
    }
    private static NativeActivationProof Mint(NativeCommunicationOriginCatalog catalog, XElement application, ApplicationSnapshot snapshot,
        IReadOnlyDictionary<string, string?> values, IReadOnlyList<string> additional)
    {
        var expanded = snapshot.Evaluation.ExpandedRoot!; var ns = application.Name.Namespace;
        var elements = expanded.DescendantsAndSelf().Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).Take(500001).ToArray();
        if (elements.Length > 500000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        var references = Index(elements, "ParameterRef"); var parameters = Index(elements, "Parameter"); var types = Index(elements, "ParameterType");
        var rows = snapshot.Instances.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var objects = Index(elements, "ComObjectRef"); var origins = new NativeCommunicationWitness.OriginProof(catalog, application, snapshot);
        var dynamics = elements.Where(e => e.Name == ns + "Dynamic").Take(2).ToArray();
        if (dynamics.Length > 1 || dynamics.Length == 1 && (dynamics[0].Parent?.Name != application.Name || (string?)dynamics[0].Parent?.Attribute("Id") != (string?)application.Attribute("Id")))
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.IncompleteActivation);
        var states = new List<State> { new("Root") }; var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var controllerValues = new Dictionary<string, long>(StringComparer.Ordinal); var identities = new HashSet<NativeStateIdentity>();
        int Add(State state)
        {
            if (states.Count >= StateLimit) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            var index = states.Count; states.Add(state);
            if (state.Parent >= 0) { states[state.Parent].Children.Add(index); state.Dependencies.Add(state.Parent); }
            return index;
        }
        NativeStateIdentity Identity(XElement element)
        {
            var origin = element.Annotation<NativeCommunicationElementOrigin>();
            if (origin is not { Complete: true }) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
            var identity = new NativeStateIdentity(origin.Key, origin.Frames.Count == 0 ? "" : origin.Frames[^1].InstanceId);
            if (!identities.Add(identity)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
            return identity;
        }
        void Attributes(XElement element, params string[] permitted)
        {
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !permitted.Contains(a.Name.LocalName, StringComparer.Ordinal))))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
        }
        void Walk(IEnumerable<XElement> children, int parent, int depth)
        {
            if (depth > 64) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            foreach (var element in children)
            {
                if (element.Name.Namespace != ns) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
                var kind = element.Name.LocalName;
                switch (kind)
                {
                    case "Channel": case "ChannelIndependentBlock": case "ParameterBlock":
                        var wrapper = Add(new(kind, parent, element)); Walk(element.Elements(), wrapper, depth + 1); break;
                    case "choose":
                        origins.State(element); Attributes(element, "ParamRefId", "InternalDescription");
                        var selector = (string?)element.Attribute("ParamRefId");
                        if (!NativeCommunicationGuard.Safe(selector)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
                        var choose = Add(new(kind, parent, element) { RefId = selector, Identity = Identity(element) });
                        if (element.Elements().Any(e => e.Name != ns + "when")) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                        Walk(element.Elements(), choose, depth + 1);
                        if (states[choose].Children.Count == 0 || states[choose].Children.Count(c => states[c].Default) > 1)
                            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition, selector);
                        break;
                    case "when":
                        if (states[parent].Kind != "choose") NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                        origins.State(element); Attributes(element, "test", "default", "InternalDescription");
                        var fallback = (string?)element.Attribute("default") switch
                        { null or "false" or "0" => false, "true" or "1" => true, _ => throw new NativeCommunicationRejected(new(GeneratedTableIssueCode.UnsupportedActivationCondition)) };
                        var text = (string?)element.Attribute("test");
                        if (fallback && text is not null) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                        if (!fallback && text is null) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                        var when = Add(new(kind, parent, element) { Default = fallback, Test = text is null ? null : Condition.Parse(text), Identity = Identity(element) });
                        Walk(element.Elements(), when, depth + 1); break;
                    case "ParameterRefRef":
                        origins.State(element);
                        Attributes(element, "RefId", "HelpContext", "IndentLevel", "InternalDescription", "Cell", "Icon");
                        var reference = (string?)element.Attribute("RefId");
                        if (element.HasElements || reference is null || !references.ContainsKey(reference)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, reference);
                        var parameter = Add(new(kind, parent, element) { RefId = reference, Identity = Identity(element) });
                        if (!owners.TryGetValue(reference!, out var list)) owners[reference!] = list = [];
                        list.Add(parameter); break;
                    case "ComObjectRefRef":
                        origins.Placement(element); var objectId = (string?)element.Attribute("RefId");
                        if (objectId is null || !objects.ContainsKey(objectId)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, objectId);
                        Add(new(kind, parent, element) { RefId = objectId, Identity = Identity(element) }); break;
                    case "Assign":
                        origins.State(element);
                        Attributes(element, "TargetParamRefRef", "SourceParamRefRef", "Value", "InternalDescription");
                        if (element.HasElements) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationDependency);
                        Add(new(kind, parent, element) { Identity = Identity(element) }); break;
                    case "Rows": case "Columns": case "Row": case "Column":
                        Metadata(element, depth); break;
                    case "ParameterSeparator": case "Rename": case "ParameterBlockRename":
                        if (element.HasElements) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                        break;
                    default:
                        NativeCommunicationGuard.Fail(kind == "BinaryDataRef" ? GeneratedTableIssueCode.UnaccountedBinaryData : GeneratedTableIssueCode.UnsupportedActivationCondition);
                        break;
                }
            }
        }
        void Metadata(XElement element, int depth)
        {
            if (depth > 64 || element.Name.Namespace != ns || element.Name.LocalName is not ("Rows" or "Columns" or "Row" or "Column"))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
            foreach (var child in element.Elements()) Metadata(child, depth + 1);
        }
        if (dynamics.Length == 1) Walk(dynamics[0].Elements(), 0, 0);
        var controllerIds = states.Where(s => s.Kind == "choose").Select(s => s.RefId!).Concat(additional).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in controllerIds)
        {
            var reference = references.GetValueOrDefault(id); var row = rows.GetValueOrDefault(id);
            var parameter = row?.ParameterId is { } parameterId ? parameters.GetValueOrDefault(parameterId) : null;
            var type = row?.ParameterTypeId is { } typeId ? types.GetValueOrDefault(typeId) : null;
            if (reference is null || row is null || row.ParameterId is null || row.ParameterTypeId is null || row.ParameterType is null || parameter is null || type is null)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id);
            if ((string?)reference.Attribute("RefId") != row.ParameterId || parameter.Attribute("ParameterTypeId") is not null
                || reference.Attribute("ParameterType") is not null || reference.Attribute("ParameterTypeId") is not null
                || (string?)parameter.Attribute("ParameterType") != row.ParameterTypeId)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id);
            try { origins.Validate(parameter, "Parameters"); origins.Validate(reference, "ParameterRefs"); origins.Validate(type, "ParameterTypes"); }
            catch (NativeCommunicationRejected) { NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id); }
            var originalParameter = catalog.At(parameter.Annotation<NativeCommunicationElementOrigin>()!.Key)!;
            var originalType = catalog.At(type.Annotation<NativeCommunicationElementOrigin>()!.Key)!;
            if ((string?)originalParameter.Attribute("ParameterType") != (string?)originalType.Attribute("Id")
                || type.Elements().Count() != 1 || type.Elements().Single().Name.Namespace != ns)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id);
            ValidateType(type, id);
            var raw = values.GetValueOrDefault(id);
            var value = 0L;
            if (!ParameterValidation.TryValidate(row.ParameterType, raw, out _) || raw is null
                || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition, id);
            controllerValues.Add(id, value);
        }
        var referenceNodes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in references.Keys)
        {
            var index = Add(new("Reference") { RefId = id }); referenceNodes.Add(id, index);
            states[index].Dependencies.AddRange(owners.GetValueOrDefault(id) ?? []);
        }
        foreach (var state in states.Where(s => s.Kind == "choose"))
        {
            state.Controller = referenceNodes[state.RefId!]; state.Dependencies.Add(state.Controller);
            var value = controllerValues[state.RefId!]; var tested = state.Children.Where(c => !states[c].Default).ToArray();
            foreach (var child in tested) states[child].Matches = states[child].Test!.Matches(value);
            var any = tested.Any(c => states[c].Matches);
            foreach (var child in state.Children.Where(c => states[c].Default)) states[child].Matches = !any;
        }
        var degrees = new int[states.Count];
        for (var i = 0; i < states.Count; i++)
        {
            degrees[i] = states[i].Dependencies.Count;
            foreach (var dependency in states[i].Dependencies) states[dependency].Dependents.Add(i);
        }
        var pending = new Queue<int>(Enumerable.Range(0, states.Count).Where(i => degrees[i] == 0)); var processed = 0;
        while (pending.TryDequeue(out var index))
        {
            processed++; var state = states[index];
            state.Active = state.Kind switch
            {
                "Root" => true,
                "Reference" => state.Dependencies.Any(d => states[d].Active),
                "choose" => states[state.Parent].Active && states[state.Controller].Active,
                "when" => states[state.Parent].Active && state.Matches,
                _ => states[state.Parent].Active,
            };
            if (state.Kind == "Reference") state.Count = state.Dependencies.Count(d => states[d].Active);
            foreach (var child in state.Dependents) if (--degrees[child] == 0) pending.Enqueue(child);
        }
        if (processed != states.Count) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.ActivationCycle);
        var nativeOwners = states.Where(s => s.Active && s.Kind == "ParameterRefRef").Select(s => s.Identity!).ToHashSet();
        var nativeWhens = states.Where(s => s.Active && s.Kind == "when").Select(s => s.Identity!).ToHashSet();
        var nativeObjectOwners = states.Where(s => s.Active && s.Kind == "ComObjectRefRef").Select(s => s.Identity!).ToHashSet();
        var nativeObjects = states.Where(s => s.Active && s.Kind == "ComObjectRefRef").Select(s => s.RefId!).ToHashSet(StringComparer.Ordinal);
        var firstOwners = new HashSet<NativeStateIdentity>(); var firstWhens = new HashSet<NativeStateIdentity>();
        var firstObjects = new HashSet<NativeStateIdentity>(); var firstRefs = new HashSet<string>(StringComparer.Ordinal);
        // This separate first-match walk is a compatibility comparison only. It never mints native activity.
        void First(int index)
        {
            var state = states[index];
            if (state.Kind == "choose")
            {
                var selected = state.Children.FirstOrDefault(c => !states[c].Default && states[c].Matches, -1);
                if (selected < 0) selected = state.Children.FirstOrDefault(c => states[c].Default, -1);
                if (selected >= 0) First(selected);
                return;
            }
            if (state.Kind == "when") firstWhens.Add(state.Identity!);
            if (state.Kind == "ParameterRefRef") firstOwners.Add(state.Identity!);
            if (state.Kind == "ComObjectRefRef") { firstObjects.Add(state.Identity!); firstRefs.Add(state.RefId!); }
            foreach (var child in state.Children) First(child);
        }
        First(0);
        if (!firstRefs.SetEquals(snapshot.Evaluation.ActiveObjectReferences) || !nativeOwners.SetEquals(firstOwners)
            || !nativeWhens.SetEquals(firstWhens) || !nativeObjectOwners.SetEquals(firstObjects) || !nativeObjects.SetEquals(firstRefs))
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.ActivationMismatch);
        var controllerFacts = controllerValues.ToDictionary(p => p.Key,
            p => new NativeControllerActivity(p.Key, states[referenceNodes[p.Key]].Count, 0, states[referenceNodes[p.Key]].Active, p.Value), StringComparer.Ordinal);
        NativeStateIdentity[] Sorted(IEnumerable<NativeStateIdentity> source) => source.OrderBy(s => s.ModuleInstanceId, StringComparer.Ordinal).ThenBy(s => s.SourceKey).ToArray();
        return new(catalog, snapshot, controllerFacts, nativeObjects, Sorted(nativeOwners), Sorted(nativeWhens), new
        {
            objectOwners = Sorted(nativeObjectOwners),
            sources = states.Where(s => s.Identity is not null).Select(s => new { s.Identity, s.Kind, s.RefId,
                hash = s.Element?.Annotation<NativeCommunicationElementOrigin>()?.OriginalHash }).ToArray(),
        });
    }
    private static Dictionary<string, XElement> Index(IEnumerable<XElement> elements, string kind)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in elements.Where(e => e.Name.LocalName == kind))
        {
            var id = (string?)element.Attribute("Id");
            if (!NativeCommunicationGuard.Safe(id) || result.Count >= StateLimit || !result.TryAdd(id!, element))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id);
        }
        return result;
    }
    internal static void ValidateControllerDeclarations(IReadOnlyList<XElement> elements, IReadOnlyList<string> additional)
    {
        ValidateConditionSources(elements);
        var references = Index(elements, "ParameterRef"); var parameters = Index(elements, "Parameter"); var types = Index(elements, "ParameterType");
        var controllers = elements.Where(e => e.Name.LocalName == "choose" && e.Ancestors().Any(a => a.Name.LocalName == "Dynamic"))
            .Attributes("ParamRefId").Select(a => a.Value).Concat(additional).Distinct(StringComparer.Ordinal);
        var checkedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in controllers)
        {
            var reference = references.GetValueOrDefault(id);
            var parameter = parameters.GetValueOrDefault((string?)reference?.Attribute("RefId") ?? "");
            var type = types.GetValueOrDefault((string?)parameter?.Attribute("ParameterType") ?? "");
            if (reference is null || parameter is null || type is null || reference.Attribute("ParameterType") is not null
                || reference.Attribute("ParameterTypeId") is not null || parameter.Attribute("ParameterTypeId") is not null
                || parameter.Parent?.Name.LocalName != "Parameters" || type.Parent?.Name.LocalName != "ParameterTypes"
                || type.Parent.Parent?.Name.LocalName != "Static" || type.Parent.Parent.Parent?.Name.LocalName != "ApplicationProgram")
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id);
            if (checkedTypes.Add((string)type.Attribute("Id")!)) ValidateType(type, id);
        }
    }
    private static void ValidateConditionSources(IReadOnlyList<XElement> elements)
    {
        static bool Fallback(XElement element) => (string?)element.Attribute("default") switch
        { null or "false" or "0" => false, "true" or "1" => true, _ => throw new NativeCommunicationRejected(new(GeneratedTableIssueCode.UnsupportedActivationCondition)) };
        foreach (var element in elements.Where(e => e.Ancestors().Any(a => a.Name.LocalName == "Dynamic")))
        {
            var kind = element.Name.LocalName;
            if (kind is not ("choose" or "when" or "ParameterRefRef" or "ComObjectRefRef" or "Assign")) continue;
            var depth = 0;
            foreach (var ancestor in element.Ancestors())
            {
                if (++depth > 64) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
                if (ancestor.Name.LocalName == "Dynamic")
                {
                    if (ancestor.Parent?.Name.LocalName is not ("ApplicationProgram" or "ModuleDef"))
                        NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
                    break;
                }
                if (ancestor.Name.LocalName is not ("Channel" or "ChannelIndependentBlock" or "ParameterBlock" or "choose" or "when"))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
            }
            if (kind is not ("choose" or "when")) continue;
            var allowed = kind == "choose" ? new[] { "ParamRefId", "InternalDescription" } : new[] { "test", "default", "InternalDescription" };
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !allowed.Contains(a.Name.LocalName, StringComparer.Ordinal))))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
            if (kind == "choose")
            {
                if (!NativeCommunicationGuard.Safe((string?)element.Attribute("ParamRefId"))) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
                var defaults = 0; var count = 0;
                foreach (var branch in element.Elements())
                {
                    if (++count > StateLimit) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
                    if (branch.Name != element.Name.Namespace + "when") NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                    if (Fallback(branch) && ++defaults > 1) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                }
                if (count == 0) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
            }
            else
            {
                if (element.Parent?.Name != element.Name.Namespace + "choose") NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                var text = (string?)element.Attribute("test");
                var fallback = Fallback(element);
                if (fallback && text is not null || !fallback && text is null) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                if (text is not null) _ = Condition.Parse(text);
            }
        }
    }
    private static void ValidateType(XElement type, string reference)
    {
        void Reject() => NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition, reference);
        if (type.Elements().Take(2).Count() != 1 || type.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) Reject();
        var shape = type.Elements().Single(); var ns = type.Name.Namespace;
        void Attributes(XElement element, params string[] permitted)
        {
            if (element.Name.Namespace != ns || element.Attributes().Any(a => !a.IsNamespaceDeclaration
                && (a.Name.Namespace != XNamespace.None || !permitted.Contains(a.Name.LocalName, StringComparer.Ordinal)))) Reject();
        }
        Attributes(type, "Id", "Name", "InternalDescription", "Plugin", "ValidationErrorRef");
        static bool Int64(string? text, out long value) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        var bits = 0;
        if (!int.TryParse((string?)shape.Attribute("SizeInBit"), NumberStyles.None, CultureInfo.InvariantCulture, out bits)) Reject();
        if (shape.Name == ns + "TypeNumber")
        {
            Attributes(shape, "Type", "SizeInBit", "minInclusive", "maxInclusive", "Increment", "UIHint", "DisplayOffset", "DisplayFactor");
            var minimum = 0L; var maximum = 0L;
            if (shape.HasElements || !string.IsNullOrWhiteSpace(shape.Value) || bits is < 1 or > 32 || (string?)shape.Attribute("Type") is not ("signedInt" or "unsignedInt")
                || !Int64((string?)shape.Attribute("minInclusive"), out minimum) || !Int64((string?)shape.Attribute("maxInclusive"), out maximum) || minimum > maximum
                || shape.Attribute("Increment") is { } increment && !Int64(increment.Value, out _)
                || shape.Attribute("UIHint") is { } hint && hint.Value is not ("Slider" or "CheckBox" or "ProgressBar")) Reject();
            foreach (var name in new[] { "DisplayOffset", "DisplayFactor" })
                if (shape.Attribute(name) is { } value)
                { try { _ = XmlConvert.ToDouble(value.Value); } catch (FormatException) { Reject(); } }
            return;
        }
        Attributes(shape, "Base", "SizeInBit", "UIHint");
        if (shape.Name != ns + "TypeRestriction" || (string?)shape.Attribute("Base") != "Value" || bits is < 1 or > 1048575
            || shape.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
            || shape.Attribute("UIHint") is { } enumHint && enumHint.Value is not ("Text" or "DropDown" or "Buttons" or "Segmented")) Reject();
        var count = shape.Elements().Take(4097).Count();
        if (count > 4096) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded, reference);
        if (count == 0) Reject();
        var ids = new HashSet<string>(StringComparer.Ordinal); var values = new HashSet<uint>();
        foreach (var choice in shape.Elements())
        {
            Attributes(choice, "Id", "Value", "Text", "Icon", "PictureAlignment", "DisplayOrder");
            var id = (string?)choice.Attribute("Id"); var raw = (string?)choice.Attribute("Value"); var value = 0u;
            if (choice.Name != ns + "Enumeration" || choice.HasElements || !string.IsNullOrWhiteSpace(choice.Value) || !NativeCommunicationGuard.Safe(id)
                || !ids.Add(id!) || raw is null || raw.Length > 32 || !uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                || !values.Add(value) || bits < 32 && value >= (1u << bits)
                || choice.Attribute("DisplayOrder") is { } display && !int.TryParse(display.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                || choice.Attribute("PictureAlignment") is { } alignment && alignment.Value is not ("Left" or "Center" or "Right")) Reject();
            try { XmlConvert.VerifyNCName(id!); } catch (XmlException) { Reject(); }
        }
    }
    private sealed record Condition(string Operator, long[] Values)
    {
        internal bool Matches(long value) => Operator switch
        { "=" => value == Values[0], "!=" => value != Values[0], ">" => value > Values[0], "<" => value < Values[0], ">=" => value >= Values[0], "<=" => value <= Values[0], _ => Values.Contains(value) };
        internal static Condition Parse(string text)
        {
            if (text.Length is 0 or > 4096 || text.Any(c => c > 127 || char.IsControl(c)))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
            var tokens = text.Split(' ', 257, StringSplitOptions.None);
            if (tokens.Length is 0 or > 256) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
            var op = new[] { ">=", "<=", "!=", "=", ">", "<" }.FirstOrDefault(prefix => tokens[0].StartsWith(prefix, StringComparison.Ordinal));
            if (op is not null)
            {
                if (tokens.Length != 1) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
                return new(op, [Integer(tokens[0][op.Length..])]);
            }
            return new("set", tokens.Select(Integer).ToArray());
        }
        private static long Integer(string text)
        {
            var digits = text.StartsWith('-') ? text.AsSpan(1) : text.AsSpan();
            var value = 0L;
            if (digits.Length == 0 || digits.ContainsAnyExceptInRange('0', '9')
                || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationCondition);
            return value;
        }
    }
}
