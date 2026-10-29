using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>Original structural declarations checked against detached, complete expansion occurrences.</summary>
internal sealed class NativeStructuralActivationProof
{
    internal bool IsAvailable => Issues.Count == 0 && Fingerprint is not null;
    internal IReadOnlyList<GeneratedTableIssue> Issues { get; }
    internal string? Fingerprint { get; }
    internal IReadOnlyList<NativeCommunicationOccurrence> Occurrences { get; }

    private NativeStructuralActivationProof(GeneratedTableIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue });
        Occurrences = Array.AsReadOnly(Array.Empty<NativeCommunicationOccurrence>());
    }
    private NativeStructuralActivationProof(SourcePlan source, NativeCommunicationOccurrenceManifest manifest, NativeActivationProof? activation)
    {
        Issues = Array.AsReadOnly(Array.Empty<GeneratedTableIssue>());
        Occurrences = manifest.Occurrences;
        Fingerprint = source.ImageModuleProfile ? NativeCommunicationGuard.Hash(new
        {
            profile = 4, scope = "LiteralRepeatRootAllocatorModuleImage1", source.SourceHash,
            expected = source.Expected, occurrences = Occurrences,
        }) : source.ConditionalWhenKeys.Count == 0 ? NativeCommunicationGuard.Hash(new
        {
            profile = 1, scope = "UnconditionalFlatLiteralModules", source.SourceHash,
            occurrences = Occurrences.Select(o => new { o.Frame, o.Arguments }).ToArray(),
        }) : source.ConditionalWhenKeys.Values.All(keys => keys.Count == 1) ? NativeCommunicationGuard.Hash(new
        {
            profile = 2, scope = "SingleRootChoiceFlatLiteralModules", source.SourceHash,
            candidates = source.Expected.Select(o => new { o.Frame, o.Arguments, whenKey = source.ConditionalWhenKeys.TryGetValue(o.Frame!.CallKey, out var keys) ? keys[0] : -1 }).ToArray(),
            controllers = source.CallControllerReferences.Select(id => activation!.Controllers[id]).ToArray(),
            selectedWhenStates = activation!.ActiveWhenStates.Where(s => s.ModuleInstanceId.Length == 0).OrderBy(s => s.SourceKey).ToArray(),
            occurrences = Occurrences.Select(o => new { o.Frame, o.Arguments }).ToArray(),
        }) : NativeCommunicationGuard.Hash(new
        {
            profile = 3, scope = "TwoRootChoicesFlatLiteralModules", source.SourceHash,
            candidates = source.Expected.Select(o => new { o.Frame, o.Arguments,
                whenKeys = source.ConditionalWhenKeys.GetValueOrDefault(o.Frame!.CallKey) ?? Array.AsReadOnly(Array.Empty<int>()) }).ToArray(),
            controllers = source.CallControllerReferences.Select(id => activation!.Controllers[id]).ToArray(),
            selectedWhenStates = activation!.ActiveWhenStates.Where(s => s.ModuleInstanceId.Length == 0).OrderBy(s => s.SourceKey).ToArray(),
            occurrences = Occurrences.Select(o => new { o.Frame, o.Arguments }).ToArray(),
        });
    }

    internal sealed class SourcePlan(XElement application, string sourceHash,
        IReadOnlyList<XElement> scopeElements, IReadOnlyList<NativeCommunicationOccurrence> expected,
        IReadOnlyDictionary<int, int> definitionOwners, IReadOnlyDictionary<int, IReadOnlyList<int>> conditionalWhenKeys,
        IReadOnlyList<string> callControllerReferences, bool imageModuleProfile = false)
    {
        internal XElement Application { get; } = application;
        internal string SourceHash { get; } = sourceHash;
        internal IReadOnlyList<XElement> ScopeElements { get; } = scopeElements;
        internal IReadOnlyList<NativeCommunicationOccurrence> Expected { get; } = expected;
        internal IReadOnlyDictionary<int, int> DefinitionOwners { get; } = definitionOwners;
        internal IReadOnlyDictionary<int, IReadOnlyList<int>> ConditionalWhenKeys { get; } = conditionalWhenKeys;
        internal IReadOnlyList<string> CallControllerReferences { get; } = callControllerReferences;
        internal bool ImageModuleProfile { get; } = imageModuleProfile;
    }

    /// <summary>Validates source structure before any optional evaluation capability is called.</summary>
    internal static IReadOnlyList<XElement> SourceScopeElements(NativeCommunicationOriginCatalog catalog)
        => catalog.StructuralPlan.ScopeElements;

    internal static NativeStructuralActivationProof Create(NativeCommunicationOriginCatalog catalog, ApplicationSnapshot snapshot)
        => CreateCore(catalog, snapshot, null);

    internal static NativeStructuralActivationProof CreateResolved(NativeCommunicationOriginCatalog catalog, ApplicationSnapshot snapshot,
        NativeActivationProof activation) => CreateCore(catalog, snapshot, activation);

    private static NativeStructuralActivationProof CreateCore(NativeCommunicationOriginCatalog catalog, ApplicationSnapshot snapshot,
        NativeActivationProof? activation)
    {
        try
        {
            var plan = catalog.StructuralPlan;
            if (plan.ConditionalWhenKeys.Count != 0 && (activation is null || !activation.BelongsTo(catalog, snapshot)))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
            var activeWhens = activation?.ActiveWhenStates.Where(s => s.ModuleInstanceId.Length == 0).Select(s => s.SourceKey).ToHashSet() ?? [];
            foreach (var id in plan.CallControllerReferences)
                if (activation is null || !activation.Controllers.TryGetValue(id, out var controller) || !controller.Active
                    || controller.NormalReferenceCount != 1 || controller.ValueAssignmentReferenceCount != 0)
                    Fail(GeneratedTableIssueCode.ActivationMismatch, id);
            var expectedOccurrences = plan.Expected.Where(o => !plan.ConditionalWhenKeys.TryGetValue(o.Frame!.CallKey, out var keys)
                || keys.All(activeWhens.Contains)).ToArray();
            var root = snapshot.Evaluation.ExpandedRoot;
            if (root is null || !snapshot.IsValid || catalog.CurrentHash(0) != plan.SourceHash)
                Fail(GeneratedTableIssueCode.ActivationMismatch);
            var manifest = root!.Annotation<NativeCommunicationOccurrenceManifest>();
            if (manifest is null || !manifest.BelongsTo(catalog) || manifest.Occurrences.Count != expectedOccurrences.Length
                || snapshot.ModuleInstances.Count != expectedOccurrences.Length)
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
            var actual = manifest!.Occurrences;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < expectedOccurrences.Length; i++)
            {
                var expected = expectedOccurrences[i]; var occurrence = actual[i]; var instance = snapshot.ModuleInstances[i];
                if (occurrence.Frame is null || !SameFrame(expected.Frame!, occurrence.Frame)
                    || !occurrence.Arguments.SequenceEqual(expected.Arguments)
                    || !seen.Add(occurrence.Frame.InstanceId)
                    || instance.Id != expected.Frame!.NativeInstanceId || instance.RefId != expected.Frame.NativeReferenceId
                    || instance.RepeatIndex != expected.Frame.RepeatIndex || !instance.Arguments.SequenceEqual(expected.Arguments))
                    Fail(GeneratedTableIssueCode.ActivationMismatch);
            }
            var frames = expectedOccurrences.ToDictionary(e => e.Frame!.InstanceId, e => e.Frame!, StringComparer.Ordinal);
            var checkedFrames = new HashSet<NativeCommunicationModuleFrame>();
            var elementCount = 0;
            foreach (var element in root.DescendantsAndSelf())
            {
                if (++elementCount > 500000) Fail(GeneratedTableIssueCode.LimitExceeded);
                if (element.Name.LocalName is "Module" or "Repeat") Fail(GeneratedTableIssueCode.ActivationMismatch);
                if (element.Annotation<NativeCommunicationElementOrigin>() is not { } origin) continue;
                if (!plan.DefinitionOwners.TryGetValue(origin.Key, out var definitionOwner))
                    Fail(GeneratedTableIssueCode.ActivationMismatch);
                if (origin.Frames.Count == 0)
                {
                    if (definitionOwner != -1 && (expectedOccurrences.Length != 0 || !ReferenceEquals(element, catalog.At(origin.Key))))
                        Fail(GeneratedTableIssueCode.ActivationMismatch);
                    continue;
                }
                if (!origin.Complete || origin.Frames.Count != 1) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                var frame = origin.Frames[0];
                if (definitionOwner != frame.DefinitionKey) Fail(GeneratedTableIssueCode.ActivationMismatch);
                if (checkedFrames.Add(frame) && (!frames.TryGetValue(frame.InstanceId, out var expected)
                    || !SameFrame(expected, frame))) Fail(GeneratedTableIssueCode.ActivationMismatch);
            }
            return new(plan, manifest, activation);
        }
        catch (NativeCommunicationRejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or OverflowException)
        { return new(new GeneratedTableIssue(GeneratedTableIssueCode.UnsupportedStructureActivation)); }
    }

    internal static SourcePlan ReadSource(NativeCommunicationOriginCatalog catalog)
    {
        if (catalog.ImageModuleProfile && catalog.Nodes.Any(e => e.Name.LocalName is "Repeat" or "Allocator"))
            return NativeModuleImageDeclarations.Read(catalog);
        var nodes = catalog.Nodes;
        if (!catalog.IsAvailable || nodes.Count > 500000) Fail(GeneratedTableIssueCode.LimitExceeded);
        var root = nodes[0]; var ns = root.Name.Namespace;
        if (ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
        var keys = new Dictionary<XElement, int>(nodes.Count);
        var owners = new Dictionary<XElement, int>(nodes.Count);
        var depths = new Dictionary<XElement, int>(nodes.Count);
        var definitionOwners = new Dictionary<int, int>();
        var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var calls = new List<XElement>(); XElement? app = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]; keys.Add(node, i);
            if (node.Name.Namespace != ns) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
            var depth = node.Parent is { } parent ? depths.GetValueOrDefault(parent, 0) + 1 : 0;
            if (depth > 128) Fail(GeneratedTableIssueCode.LimitExceeded);
            depths[node] = depth;
            var owner = node.Parent is { } ancestor ? owners.GetValueOrDefault(ancestor, -1) : -1;
            if (node.Name == ns + "ModuleDef")
            {
                if (owner != -1 || !NativeCommunicationGuard.Safe(Id(node)) || !definitions.TryAdd(Id(node), node))
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(node));
                owner = i;
            }
            owners[node] = owner; definitionOwners[i] = owner;
            switch (node.Name.LocalName)
            {
                case "ApplicationProgram":
                    if (app is not null) Fail(GeneratedTableIssueCode.InvalidApplication);
                    app = node;
                    break;
                case "Repeat" or "SubModuleDefs":
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(node));
                    break;
                case "Module":
                    if (owner != -1 || calls.Count >= 256) Fail(owner != -1
                        ? GeneratedTableIssueCode.UnsupportedStructureActivation : GeneratedTableIssueCode.LimitExceeded, Id(node));
                    calls.Add(node);
                    break;
            }
        }
        if (app is null || !NativeCommunicationGuard.Safe(Id(app))) Fail(GeneratedTableIssueCode.InvalidApplication);
        var application = app!;
        var rootStatic = One(application, "Static", true)!;
        var dynamic = One(application, "Dynamic", true)!;
        Shape(dynamic, []);
        if (application.Elements(ns + "ModuleDefs").Count() > 1) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
        foreach (var definition in definitions.Values)
            if (definition.Parent?.Name != ns + "ModuleDefs" || !ReferenceEquals(definition.Parent.Parent, application))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(definition));
        var order = new Dictionary<XElement, int>(); var ordinal = 0;
        foreach (var node in dynamic.DescendantsAndSelf()) order[node] = ordinal++;
        var usedDefinitions = new HashSet<int>(); var callIds = new HashSet<string>(StringComparer.Ordinal);
        var conditionalWhenKeys = new Dictionary<int, IReadOnlyList<int>>();
        var controllers = new HashSet<string>(StringComparer.Ordinal);
        var rootNodes = nodes.Where(n => owners[n] == -1).ToArray();
        var rootReferences = rootNodes.Where(n => n.Name == ns + "ParameterRef").ToLookup(Id, StringComparer.Ordinal);
        var rootParameters = rootNodes.Where(n => n.Name == ns + "Parameter").ToLookup(Id, StringComparer.Ordinal);
        var rootTypes = rootNodes.Where(n => n.Name == ns + "ParameterType").ToLookup(Id, StringComparer.Ordinal);
        var rootPlacements = rootNodes.Where(n => n.Name == ns + "ParameterRefRef" && order.ContainsKey(n))
            .ToLookup(n => (string?)n.Attribute("RefId") ?? "", StringComparer.Ordinal);
        var rootDeclarations = rootStatic.Descendants().Where(IsDeclaration).ToLookup(Id, StringComparer.Ordinal);
        var taintedReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.Name == ns + "Assign")
                foreach (var name in new[] { "TargetParamRefRef", "SourceParamRefRef" })
                    if (node.Attribute(name) is { } binding) taintedReferences.Add(binding.Value);
            if (node.Name == ns + "ParameterRefRef" && node.Ancestors(ns + "ParameterCalculation").Any()
                && node.Attribute("RefId") is { } calculation) taintedReferences.Add(calculation.Value);
        }
        var taintedParameters = rootNodes.Where(n => n.Name == ns + "ParameterRef" && taintedReferences.Contains(Id(n)))
            .Select(n => (string?)n.Attribute("RefId") ?? "").ToHashSet(StringComparer.Ordinal);
        bool Unconditional(XElement node)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, dynamic)) return true;
                if (parent.Name.LocalName is not ("Channel" or "ChannelIndependentBlock" or "ParameterBlock")) return false;
            }
            return false;
        }
        XElement Unique(ILookup<string, XElement> index, string id)
        {
            var matches = index[id].Take(2).ToArray();
            if (matches.Length != 1) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
            return matches[0];
        }
        void Controller(XElement choose, string callId)
        {
            Shape(choose, ["ParamRefId", "InternalDescription"]);
            var id = (string?)choose.Attribute("ParamRefId");
            if (!NativeCommunicationGuard.Safe(id))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, callId);
            if (!controllers.Add(id!)) return;
            var reference = Unique(rootReferences, id!);
            var parameterId = (string?)reference.Attribute("RefId") ?? "";
            var parameter = Unique(rootParameters, parameterId);
            var type = Unique(rootTypes, (string?)parameter.Attribute("ParameterType") ?? "");
            if (reference.Parent?.Name != ns + "ParameterRefs" || !ReferenceEquals(reference.Parent.Parent, rootStatic)
                || parameter.Parent?.Name != ns + "Parameters" || !ReferenceEquals(parameter.Parent.Parent, rootStatic)
                || type.Parent?.Name != ns + "ParameterTypes" || !ReferenceEquals(type.Parent.Parent, rootStatic)
                || reference.HasElements
                || type.Elements().Take(2).Count() != 1 || !ModuleControllerType(type.Elements().Single(), ns))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
            var placements = rootPlacements[id!].Take(2).ToArray();
            if (placements.Length != 1 || !Unconditional(placements[0])) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
            if (taintedParameters.Contains(parameterId))
                Fail(GeneratedTableIssueCode.UnsupportedActivationDependency, id);
        }
        var declarationCache = new Dictionary<XElement, Dictionary<string, XElement>>();
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        var expected = new List<NativeCommunicationOccurrence>(); long textBudget = 0;
        string Local(string value) => value.StartsWith(Id(application) + "_", StringComparison.Ordinal)
            ? value[(Id(application).Length + 1)..] : value;
        foreach (var call in calls)
        {
            if (!NativeCommunicationGuard.Safe(Id(call)) || !callIds.Add(Id(call)) || !order.ContainsKey(call))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            Shape(call, ["Id", "RefId", "Name", "InternalDescription"]);
            XElement? callWhen = null; var whenChain = new List<int>(2);
            for (var parent = call.Parent; parent is not null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, dynamic)) break;
                if (parent.Name == ns + "when")
                {
                    if (callWhen is not null || whenChain.Count == 2 || parent.Parent?.Name != ns + "choose")
                        Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
                    callWhen = parent;
                    whenChain.Add(keys[parent]);
                    continue;
                }
                if (parent.Name == ns + "choose" && ReferenceEquals(callWhen?.Parent, parent))
                {
                    Controller(parent, Id(call));
                    foreach (var branch in parent.Elements())
                    {
                        Shape(branch, ["test", "default", "InternalDescription"]);
                        if (branch.Name != ns + "when" || branch.Attribute("test") is not null
                            && (string?)branch.Attribute("default") is "true" or "1")
                            Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
                    }
                    callWhen = null;
                    continue;
                }
                if (parent.Name.Namespace != ns || parent.Name.LocalName is not ("Channel" or "ChannelIndependentBlock" or "ParameterBlock"))
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            }
            if (callWhen is not null) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            if (whenChain.Count != 0)
            {
                whenChain.Reverse();
                conditionalWhenKeys.Add(keys[call], Array.AsReadOnly(whenChain.ToArray()));
            }
            var refId = (string?)call.Attribute("RefId");
            if (refId is null) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            if (!definitions.TryGetValue(refId!, out var definition)) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            var def = definition!;
            if (!declarationCache.TryGetValue(def, out var arguments))
            {
                Shape(def, ["Id", "Name"]);
                var staticPart = One(def, "Static", true)!; var moduleDynamic = One(def, "Dynamic", true)!;
                Shape(staticPart, []); Shape(moduleDynamic, []);
                if (def.Elements().Any(n => n.Name.LocalName is not ("Arguments" or "Static" or "Dynamic"))
                    || staticPart.Descendants().Any(n => n.Name.LocalName is "Code" or "Allocator"))
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(def));
                ValidateDynamicSources(staticPart, moduleDynamic, rootStatic, rootDeclarations, allowCalls: false);
                var argumentContainer = One(def, "Arguments", false);
                if (argumentContainer is not null) Shape(argumentContainer, []);
                var declarations = argumentContainer?.Elements().Take(65).ToArray() ?? [];
                if (declarations.Length > 64) Fail(GeneratedTableIssueCode.LimitExceeded);
                arguments = new(StringComparer.Ordinal);
                var argumentNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var declaration in declarations)
                {
                    Shape(declaration, ["Id", "Name", "Type", "Allocates", "Alignment"]);
                    if (declaration.Name != ns + "Argument" || declaration.HasElements || !NativeCommunicationGuard.Safe(Id(declaration))
                        || !arguments.TryAdd(Id(declaration), declaration)) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(def));
                    if (declaration.Attribute("Name") is { } name && (name.Value.Length > 256 || name.Value.Any(char.IsControl)
                        || !argumentNames.Add(name.Value))) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(def));
                }
                declarationCache.Add(def, arguments);
            }
            if (call.Elements().Take(65).Count() > 64) Fail(GeneratedTableIssueCode.LimitExceeded);
            var seenArguments = new HashSet<string>(StringComparer.Ordinal);
            var numeric = new List<NativeCommunicationArgument>(); var native = new List<ApplicationModuleArgument>();
            foreach (var argument in call.Elements())
            {
                Shape(argument, argument.Name == ns + "TextArg" ? ["RefId", "Value", "Id"] : ["RefId", "Value"]);
                var argumentId = (string?)argument.Attribute("RefId");
                XElement? declaration = null;
                if (argumentId is null || !arguments.TryGetValue(argumentId, out declaration) || !seenArguments.Add(argumentId)
                    || argument.HasElements || argument.Name.LocalName is not ("NumericArg" or "TextArg"))
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
                var kind = (string?)declaration!.Attribute("Type") ?? "Numeric";
                if (kind is not ("Numeric" or "Text") || (kind == "Text") != (argument.Name.LocalName == "TextArg"))
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
                var value = (string?)argument.Attribute("Value") ?? (kind == "Numeric" ? "0" : null);
                if (value is null || value.Length > 4096 || (textBudget += value.Length) > 65536)
                    Fail(GeneratedTableIssueCode.LimitExceeded);
                native.Add(new(Local(argumentId), value!));
                if (kind == "Numeric")
                {
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                        Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
                    numeric.Add(new(argumentId, number));
                }
            }
            if (seenArguments.Count != arguments.Count) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            var instance = ApplicationModules.InstanceId(Id(call), []);
            if (!NativeCommunicationGuard.Safe(instance) || !instanceIds.Add(instance)) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(call));
            usedDefinitions.Add(keys[def]);
            var frame = new NativeCommunicationModuleFrame(keys[call], keys[def], Id(def), instance, Local(instance), Local(Id(call)),
                order[call].ToString(CultureInfo.InvariantCulture) + "x1", catalog.Hash(keys[call]), catalog.Hash(keys[def]),
                Array.AsReadOnly(numeric.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray()));
            expected.Add(new(frame, Array.AsReadOnly(native.ToArray())));
        }
        var scope = new List<XElement>();
        foreach (var node in nodes)
            if (owners[node] == -1 || usedDefinitions.Contains(owners[node])) scope.Add(node);
        var possiblePlacements = scope.Where(n => n.Name == ns + "ParameterRefRef" && n.Ancestors(ns + "Dynamic").Any())
            .ToLookup(n => (string?)n.Attribute("RefId") ?? "", StringComparer.Ordinal);
        foreach (var controller in controllers)
            if (possiblePlacements[controller].Take(2).Count() != 1)
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, controller);
        ValidateDynamicSources(rootStatic, dynamic, rootStatic, rootDeclarations, allowCalls: true);
        return new(application, catalog.Hash(0), Array.AsReadOnly(scope.ToArray()), Array.AsReadOnly(expected.ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(definitionOwners),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, IReadOnlyList<int>>(conditionalWhenKeys),
            Array.AsReadOnly(controllers.Order(StringComparer.Ordinal).ToArray()));
    }

    // A binary integer carries its declared 0/1 domain, not an inferred enumeration.
    // Dependency and independently evaluated activity checks remain mandatory.
    private static bool ModuleControllerType(XElement shape, XNamespace ns)
    {
        if(shape.Name==ns+"TypeRestriction")return (string?)shape.Attribute("Base")=="Value";
        return shape.Name==ns+"TypeNumber" && !shape.HasElements && string.IsNullOrWhiteSpace(shape.Value)
            && shape.Attributes().All(a=>a.IsNamespaceDeclaration || a.Name.Namespace==XNamespace.None
                && a.Name.LocalName is "Type" or "SizeInBit" or "minInclusive" or "maxInclusive" or "UIHint")
            && (string?)shape.Attribute("Type")=="unsignedInt" && (string?)shape.Attribute("SizeInBit")=="1"
            && (string?)shape.Attribute("minInclusive")=="0" && (string?)shape.Attribute("maxInclusive")=="1"
            && (string?)shape.Attribute("UIHint") is null or "CheckBox";
    }

    private static bool IsDeclaration(XElement element)
        => element.Name.LocalName is "ParameterRef" or "Parameter" or "ParameterType" or "ComObjectRef" or "ComObject";

    internal static void ValidateImageDynamicSources(XElement rootStatic, XElement dynamic, IEnumerable<XElement> definitions)
    {
        var roots = rootStatic.Descendants().Where(IsDeclaration).ToLookup(Id, StringComparer.Ordinal);
        ValidateDynamicSources(rootStatic, dynamic, rootStatic, roots, true, true);
        foreach (var definition in definitions)
            ValidateDynamicSources(One(definition, "Static", true)!, One(definition, "Dynamic", true)!, rootStatic, roots, false);
    }
    private static void ValidateDynamicSources(XElement stat, XElement dynamic, XElement rootStatic,
        ILookup<string, XElement> roots, bool allowCalls, bool allowLiteralRepeat = false)
    {
        var ns = dynamic.Name.Namespace;
        var locals = ReferenceEquals(stat, rootStatic) ? roots : stat.Descendants().Where(IsDeclaration).ToLookup(Id, StringComparer.Ordinal);
        XElement Declaration(string? id, string name)
        {
            if (!NativeCommunicationGuard.Safe(id)) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
            var matches = locals[id!].Take(2).ToArray();
            if (matches.Length == 0) matches = roots[id!].Take(2).ToArray();
            if (matches.Length != 1 || matches[0].Name != ns + name) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
            return matches[0];
        }
        void Reference(string? id, bool comObject)
        {
            var reference = Declaration(id, comObject ? "ComObjectRef" : "ParameterRef");
            var owner = reference.Parent?.Parent;
            if (reference.Parent?.Name != ns + (comObject ? "ComObjectRefs" : "ParameterRefs")
                || !ReferenceEquals(owner, stat) && !ReferenceEquals(owner, rootStatic))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
            var target = Declaration((string?)reference.Attribute("RefId"), comObject ? "ComObject" : "Parameter");
            if (comObject)
            {
                var tableName = ns + (ReferenceEquals(owner, rootStatic) ? "ComObjectTable" : "ComObjects");
                if (target.Parent?.Name != tableName || !ReferenceEquals(target.Parent.Parent, owner))
                    Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, id);
            }
            else if (!target.Ancestors().Any(a => a.Name == ns + "Parameters" && ReferenceEquals(a.Parent, owner)))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, id);
        }
        void Walk(XElement element, int depth, bool metadata = false)
        {
            if (depth > 64) Fail(GeneratedTableIssueCode.LimitExceeded);
            if (element.Name.Namespace != ns || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(element));
            var kind = element.Name.LocalName;
            if (metadata && kind is not ("Rows" or "Columns" or "Row" or "Column"))
                Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(element));
            switch (kind)
            {
                case "Channel": case "ChannelIndependentBlock": case "ParameterBlock":
                    foreach (var child in element.Elements()) Walk(child, depth + 1);
                    break;
                case "choose":
                    Shape(element, ["ParamRefId", "InternalDescription"]);
                    Reference((string?)element.Attribute("ParamRefId"), false);
                    if (element.Elements().Any(e => e.Name != ns + "when")) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    foreach (var child in element.Elements()) Walk(child, depth + 1);
                    break;
                case "when":
                    Shape(element, ["test", "default", "InternalDescription"]);
                    if (element.Parent?.Name != ns + "choose") Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    foreach (var child in element.Elements()) Walk(child, depth + 1);
                    break;
                case "ParameterRefRef":
                    Shape(element, ["RefId", "HelpContext", "IndentLevel", "InternalDescription", "Cell", "Icon"]);
                    if (element.HasElements) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    Reference((string?)element.Attribute("RefId"), false);
                    break;
                case "ComObjectRefRef":
                    Shape(element, ["RefId"]);
                    if (element.HasElements) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    Reference((string?)element.Attribute("RefId"), true);
                    break;
                case "Assign":
                    Shape(element, ["TargetParamRefRef", "SourceParamRefRef", "Value", "InternalDescription"]);
                    if (element.HasElements) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    Reference((string?)element.Attribute("TargetParamRefRef"), false);
                    if (element.Attribute("SourceParamRefRef") is { } source) Reference(source.Value, false);
                    else if (element.Attribute("Value") is null) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    break;
                case "Rows": case "Columns": case "Row": case "Column":
                    foreach (var child in element.Elements()) Walk(child, depth + 1, true);
                    break;
                case "ParameterSeparator": case "Rename": case "ParameterBlockRename":
                    if (element.HasElements) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation);
                    break;
                case "Module" when allowCalls:
                    break; // Every original call and all of its arguments are checked by the source plan.
                case "Repeat" when allowCalls && allowLiteralRepeat:
                    Shape(element, ["Id", "Count"]);
                    foreach (var child in element.Elements()) Walk(child, depth + 1);
                    break;
                default:
                    Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(element));
                    break;
            }
        }
        foreach (var child in dynamic.Elements()) Walk(child, 0);
    }

    private static bool SameFrame(NativeCommunicationModuleFrame left, NativeCommunicationModuleFrame right)
        => left.CallKey == right.CallKey && left.DefinitionKey == right.DefinitionKey && left.DefinitionId == right.DefinitionId
            && left.InstanceId == right.InstanceId && left.NativeInstanceId == right.NativeInstanceId
            && left.NativeReferenceId == right.NativeReferenceId && left.RepeatIndex == right.RepeatIndex
            && left.CallHash == right.CallHash && left.DefinitionHash == right.DefinitionHash
            && left.Arguments.SequenceEqual(right.Arguments);
    private static void Shape(XElement node, string[] attributes)
    {
        if (node.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName)))
            || node.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(node));
    }
    private static XElement? One(XElement owner, string name, bool required)
    {
        var nodes = owner.Elements(owner.Name.Namespace + name).Take(2).ToArray();
        if (nodes.Length > 1 || required && nodes.Length != 1) Fail(GeneratedTableIssueCode.UnsupportedStructureActivation, Id(owner));
        return nodes.FirstOrDefault();
    }
    private static string Id(XElement element) => (string?)element.Attribute("Id") ?? "";
    [DoesNotReturn]
    private static void Fail(GeneratedTableIssueCode code, string? id = null) => NativeCommunicationGuard.Fail(code, id);
}
