using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>Original native module argument data; an allocator reference is stored as an id, not a computed offset.</summary>
public sealed record ApplicationModuleArgument(string RefId, string Value);

/// <summary>Native relative module identity and original arguments for project serialization.</summary>
public sealed record ApplicationModuleInstance(string Id, string RefId, string RepeatIndex,
    IReadOnlyList<ApplicationModuleArgument> Arguments);

/// <summary>Bounded, detached KSXML module expansion in structural XML/repeat order.</summary>
internal static class ApplicationModules
{
    // Forward identities are shared with the raw selected-type preflight. Caller
    // strings are never decoded to infer an instance, definition or source ref.
    internal static string InstanceId(string callId, IReadOnlyList<int> counters)
        => callId + "_MI-" + (counters.Count == 0 ? "1" : string.Join('-', counters));
    internal static string ScopeId(string definitionId, string instanceId, string originalId)
        => originalId.StartsWith(definitionId + "_", StringComparison.Ordinal)
            ? instanceId + originalId[definitionId.Length..] : originalId;

    // Calculated counts require the caller's bounded, type-validated structure/value fixpoint.
    internal static XElement Expand(XElement source, IReadOnlyDictionary<string, string?>? saved = null,
        Func<XElement, IReadOnlyDictionary<string, string?>, XElement?>? selectBranch = null, bool allowCalculatedCounts = false,
        ModuleOriginCatalog? origins = null)
    {
        var communicationOrigins = source.Annotation<NativeCommunicationOriginCatalog>();
        if (!source.DescendantsAndSelf().Any(e => e.Name.LocalName == "Module" && !InsideDefinition(e)))
        {
            if (communicationOrigins is not null) NativeCommunicationOccurrenceManifest.Seal(communicationOrigins, source, []);
            return source;
        }
        try
        {
            var root = new XElement(source);
            origins ??= new ModuleOriginCatalog(source);
            origins.Stamp(root);
            communicationOrigins?.Stamp(root);
            var programId = (string?)root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "ApplicationProgram")?.Attribute("Id");
            string Local(string id) => programId is { Length: > 0 } && id.StartsWith(programId + "_", StringComparison.Ordinal)
                ? id[(programId.Length + 1)..] : id;
            foreach (var dynamicRoot in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Dynamic"))
            {
                var order = 0;
                foreach (var element in dynamicRoot.DescendantsAndSelf()) element.AddAnnotation(new XmlOrder(order++));
            }
            foreach (var module in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Module"))
                module.AddAnnotation(new OriginalModuleId((string?)module.Attribute("Id") ?? ""));
            foreach (var argument in root.DescendantsAndSelf().Where(e => e.Name.LocalName is "NumericArg" or "TextArg"))
                argument.AddAnnotation(new NativeArgumentReference((string?)argument.Attribute("RefId") ?? "", (string?)argument.Attribute("AllocatorRefId")));
            var topStatic = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "Static" && !InsideDefinition(e));
            if (topStatic is null || !ModuleStructure.Prepare(root, saved, allowCalculatedCounts)) return source;
            var cursors = new Dictionary<string, Allocation>(StringComparer.Ordinal);
            var instances = 0;
            var nodes = 0;
            var instanceIds = new HashSet<string>(StringComparer.Ordinal);
            var nativeInstances = new List<ApplicationModuleInstance>();
            var communicationOccurrences = communicationOrigins is null ? null : new List<NativeCommunicationOccurrence>();
            Dictionary<string, string?> Values()
            {
                var values = DynamicParameters.ReadDeclaredValues(root, out _);
                if (saved is not null)
                    foreach (var (id, value) in saved)
                        if (values.ContainsKey(id)) values[id] = value;
                return values;
            }
            void Budget(XElement element)
            {
                nodes = checked(nodes + element.DescendantsAndSelf().Count());
                if (nodes > 500_000) throw new InvalidDataException("Module expansion exceeds the safety limit.");
            }
            void ValidateParameterBase(XElement parameter)
            {
                var typeId = (string?)parameter.Attribute("ParameterType") ?? (string?)parameter.Attribute("ParameterTypeId");
                var types = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterType" && (string?)e.Attribute("Id") == typeId).ToList();
                if (typeId is null || types.Count != 1 || !ParameterValidation.TryValidate(Ksxml.ParseParameterType(types[0]),
                    (string?)parameter.Attribute("Value"), out _))
                    throw new InvalidDataException("Effective module parameter base value is outside its declared type.");
            }
            void Walk(IEnumerable<XElement> elements, IReadOnlyList<int> counters, int depth,
                IReadOnlyDictionary<string, string>? outerArguments, IReadOnlyList<Dimension>? order = null, string? nativeRootReference = null,
                IReadOnlyList<ModuleChoiceOrigin>? choices = null)
            {
                order ??= [];
                choices ??= [];
                if (depth > 8) throw new InvalidDataException("Module/repeat nesting exceeds the safety limit.");
                foreach (var element in elements.ToArray())
                {
                    switch (element.Name.LocalName)
                    {
                        case "Module":
                            ExpandModule(element, counters, depth, outerArguments, order, nativeRootReference, choices);
                            break;
                        case "Repeat":
                            if (!ModuleStructure.TryCount(root, element, Values(), out var count))
                                throw new InvalidDataException("Unsupported module repeat count or structure.");
                            var copies = new List<XElement>();
                            for (var index = 1; index <= count; index++)
                            {
                                var dimension = counters.Append(index).ToArray();
                                foreach (var child in element.Elements())
                                {
                                    var copy = Copy(child);
                                    Budget(copy);
                                    var holder = new XElement("holder", copy);
                                    Walk([copy], dimension, depth + 1, outerArguments,
                                        order.Append(new Dimension(element.Annotation<XmlOrder>()!.Value, index)).ToArray(), nativeRootReference, choices);
                                    copies.AddRange(holder.Elements().ToArray());
                                }
                            }
                            foreach (var copy in copies.Where(c => c.Parent is not null)) copy.Remove();
                            element.ReplaceWith(copies);
                            break;
                        case "choose":
                            if (element.Descendants().Any(e => e.Name.LocalName is "Module" or "Repeat"))
                            {
                                ModuleStructure.ProtectChoose(root, element);
                                if (selectBranch is null) throw new InvalidDataException("Conditional module structure requires an activity evaluator.");
                                var branchValues = Values();
                                var selected = selectBranch(element, branchValues);
                                if (selected is not null && (selected.Parent != element || selected.Name.LocalName != "when"))
                                    throw new InvalidDataException("Activity evaluator returned an invalid module branch.");
                                foreach (var branch in element.Elements())
                                {
                                    if (ReferenceEquals(branch, selected)) Walk(branch.Elements(), counters, depth, outerArguments, order, nativeRootReference,
                                        choices.Append(origins.Choice(element, branch, branchValues)).ToArray());
                                    else branch.Descendants().Where(e => e.Name.LocalName is "Module" or "Repeat")
                                        .Where(e => !e.Ancestors().TakeWhile(a => a != branch).Any(a => a.Name.LocalName is "Module" or "Repeat")).Remove();
                                }
                            }
                            break;
                        case "Dynamic" or "Channel" or "ChannelIndependentBlock" or "ParameterBlock":
                            Walk(element.Elements(), counters, depth, outerArguments, order, nativeRootReference, choices);
                            break;
                        default:
                            if (element.Descendants().Any(e => e.Name.LocalName is "Module" or "Repeat"))
                                throw new InvalidDataException("Unknown module structure wrapper.");
                            break;
                    }
                }
            }
            void ExpandModule(XElement module, IReadOnlyList<int> counters, int depth,
                IReadOnlyDictionary<string, string>? outerArguments, IReadOnlyList<Dimension> order, string? nativeRootReference, IReadOnlyList<ModuleChoiceOrigin> choices)
            {
                if (++instances > 256 || depth >= 8) throw new InvalidDataException("Module instance safety limit exceeded.");
                var definitionId = (string?)module.Attribute("RefId") ?? throw new InvalidDataException("Module definition reference required.");
                var definitions = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ModuleDef" && (string?)e.Attribute("Id") == definitionId).ToList();
                if (definitions.Count != 1) throw new InvalidDataException("Missing or ambiguous module definition.");
                var definition = definitions[0];
                var instanceId = InstanceId((string?)module.Attribute("Id") ?? throw new InvalidDataException("Module id required."), counters);
                if (!instanceIds.Add(instanceId)) throw new InvalidDataException("Ambiguous module instance identity.");
                nativeRootReference ??= Local(module.Annotation<OriginalModuleId>()!.Value);
                var instanceOrder = order.Append(new Dimension(module.Annotation<XmlOrder>()!.Value, 1)).ToArray();
                var nativeArguments = new List<ApplicationModuleArgument>();
                if (module.Elements().Any(e => e.Name.LocalName is not ("NumericArg" or "TextArg") || e.Attribute("RefId") is null)
                    || module.Elements().Select(e => (string)e.Attribute("RefId")!).Distinct().Count() != module.Elements().Count()
                    || module.Elements().Where(e => e.Attribute("AllocatorRefId") is not null)
                        .GroupBy(e => (string)e.Attribute("AllocatorRefId")!).Any(g => g.Count() != 1))
                    throw new InvalidDataException("Unsupported or ambiguous module arguments.");
                var argumentDeclarations = definition.Elements().Where(e => e.Name.LocalName == "Arguments").SelectMany(e => e.Elements())
                    .Where(e => e.Attribute("Id") is not null).ToList();
                if (argumentDeclarations.GroupBy(e => (string)e.Attribute("Id")!).Any(g => g.Count() != 1))
                    throw new InvalidDataException("Ambiguous module argument declarations.");
                var declaredArguments = argumentDeclarations.ToDictionary(e => (string)e.Attribute("Id")!);
                var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
                var numericArguments = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var argument in module.Elements())
                {
                    var id = (string)argument.Attribute("RefId")!;
                    if (!declaredArguments.TryGetValue(id, out var declaration)) throw new InvalidDataException("Unresolved module argument.");
                    var declaredType = (string?)declaration.Attribute("Type") ?? "Numeric";
                    if (declaredType is not ("Numeric" or "Text") || (argument.Name.LocalName == "TextArg") != (declaredType == "Text"))
                        throw new InvalidDataException("Module argument type does not match its declaration.");
                    var original = argument.Annotation<NativeArgumentReference>()!;
                    var relativeArgument = Local(original.RefId);
                    if (relativeArgument.LastIndexOf("_SM-", StringComparison.Ordinal) is var submodule && submodule >= 0)
                        relativeArgument = relativeArgument[(submodule + 1)..];
                    nativeArguments.Add(new(relativeArgument, original.AllocatorRefId is { } allocatorId
                        ? Local(allocatorId) : (string?)argument.Attribute("Value") ?? "0"));
                    if (argument.Name.LocalName == "TextArg")
                    {
                        arguments[id] = (string?)argument.Attribute("Value") ?? throw new InvalidDataException("Text argument value required.");
                        continue;
                    }
                    if (!Number((string?)argument.Attribute("Value") ?? "0", out var value)) throw new InvalidDataException("Invalid numeric module argument.");
                    var basis = 0L;
                    if (argument.Attribute("BaseValue") is { } baseValue
                        && (outerArguments is null || !outerArguments.TryGetValue(baseValue.Value, out var rawBase) || !Number(rawBase, out basis)))
                        throw new InvalidDataException("Unresolved outer module argument scope.");
                    if (argument.Attribute("AllocatorRefId") is { } allocatorReference)
                    {
                        var allocators = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Allocator" && !InsideDefinition(e)
                            && (string?)e.Attribute("Id") == allocatorReference.Value).ToList();
                        if (allocators.Count != 1 || !Number((string?)allocators[0].Attribute("Start"), out var start) || start < 0
                            || !Number((string?)allocators[0].Attribute("maxInclusive"), out var maximum)
                            || !Number((string?)declaration.Attribute("Allocates"), out var size) || size < 0
                            || !Number((string?)declaration.Attribute("Alignment") ?? "1", out var alignment) || alignment is not (1 or 2 or 4 or 8))
                            throw new InvalidDataException("Invalid module allocator definition.");
                        start = cursors.TryGetValue(allocatorReference.Value, out var previous)
                            ? checked(checked(checked(previous.Start + previous.Size) + alignment - 1) / alignment * alignment)
                            : checked(checked(start + basis) + value);
                        if (start < 0) throw new InvalidDataException("Module allocation exceeds the supported range.");
                        cursors[allocatorReference.Value] = new(start, size, basis, maximum);
                        value = start;
                    }
                    else value = checked(value + basis);
                    arguments[id] = value.ToString(CultureInfo.InvariantCulture);
                    numericArguments[id] = arguments[id];
                }
                var namedArguments = declaredArguments.Values.Where(e => e.Attribute("Name") is not null && arguments.ContainsKey((string)e.Attribute("Id")!)).ToList();
                if (namedArguments.GroupBy(e => (string)e.Attribute("Name")!).Any(g => g.Count() != 1))
                    throw new InvalidDataException("Ambiguous module argument names.");
                var argumentNames = namedArguments.ToDictionary(e => (string)e.Attribute("Name")!, e => arguments[(string)e.Attribute("Id")!]);
                string Scope(string value) => ScopeId(definitionId, instanceId, value);
                var storageArguments = ParameterStorageArguments.Create(numericArguments, Scope, instanceId);
                var occurrence = origins.Occurrence(module, definition, instanceId, Local(instanceId), nativeRootReference,
                    depth, counters.Count, numericArguments, Scope, choices);
                var communicationFrame = communicationOrigins?.Frame(module, definition, instanceId, Local(instanceId), nativeRootReference,
                    string.Join(' ', instanceOrder.Select(d => $"{d.Order}x{d.Index}")), numericArguments);
                XElement Clone(XElement element)
                {
                    var copy = Copy(element); Budget(copy);
                    foreach (var node in copy.DescendantsAndSelf())
                    {
                        var communicationBase = communicationOrigins is null || node.AncestorsAndSelf().Any(a => a.Name.LocalName == "ModuleDef")
                            ? null : (string?)node.Attribute("BaseNumber");
                        long? communicationBaseValue = communicationBase is not null && numericArguments.TryGetValue(communicationBase, out var communicationRaw)
                            && Number(communicationRaw, out var communicationNumber) ? communicationNumber : null;
                        ApplicationTexts.BindArguments(node, argumentNames, arguments);
                        if (!node.AncestorsAndSelf().Any(a => a.Name.LocalName == "ModuleDef"))
                        {
                            if (node.Name.LocalName == "Parameter" && node.Attribute("BaseValue") is not null)
                            { AddBase(node, "BaseValue", "Value", numericArguments, false); ValidateParameterBase(node); }
                            if (node.Attribute("BaseNumber") is not null) AddBase(node, "BaseNumber", "Number", numericArguments, true);
                        }
                        foreach (var attribute in node.Attributes())
                            if (attribute.Name.LocalName is "Id" or "ParameterType" or "ParameterTypeId" or "BaseNumber" or "BaseOffset" or "BaseValue" or "BaseIndex" or "BaseOccurrence" or "CodeSegment"
                                || attribute.Name.LocalName.EndsWith("RefId", StringComparison.Ordinal) || attribute.Name.LocalName.EndsWith("RefRef", StringComparison.Ordinal))
                                attribute.Value = Scope(attribute.Value);
                        origins.Mark(node, occurrence);
                        communicationOrigins?.Remap(node, communicationFrame, communicationBase, communicationBaseValue);
                        if (node.Name.LocalName is "Memory" or "Property" && !node.AncestorsAndSelf().Any(a => a.Name.LocalName == "ModuleDef"))
                        {
                            node.RemoveAnnotations<ParameterStorageArguments>();
                            node.AddAnnotation(storageArguments);
                        }
                    }
                    return copy;
                }
                foreach (var container in definition.Elements().Where(e => e.Name.LocalName == "Static").SelectMany(e => e.Elements())) topStatic.Add(Clone(container));
                foreach (var nestedDefinitions in definition.Elements().Where(e => e.Name.LocalName == "SubModuleDefs"))
                    topStatic.Add(new XElement(nestedDefinitions.Name.Namespace + "ModuleDefs", nestedDefinitions.Elements().Select(Clone)));
                var dynamic = definition.Elements().Where(e => e.Name.LocalName == "Dynamic").SelectMany(e => e.Elements()).Select(Clone).ToArray();
                nativeInstances.Add(new(Local(instanceId), nativeRootReference,
                    string.Join(' ', instanceOrder.Select(d => $"{d.Order}x{d.Index}")), nativeArguments.ToArray()));
                communicationOccurrences?.Add(new(communicationFrame?.LastOrDefault(), nativeArguments.ToArray()));
                module.ReplaceWith(dynamic);
                if (!ModuleStructure.RefreshControls(root)) throw new InvalidDataException("Calculated repeat control is unsupported.");
                if (arguments.Keys.Select(Scope).Distinct(StringComparer.Ordinal).Count() != arguments.Count)
                    throw new InvalidDataException("Ambiguous scoped module arguments.");
                var scopedArguments = numericArguments.ToDictionary(p => Scope(p.Key), p => p.Value, StringComparer.Ordinal);
                Walk(dynamic, counters.Append(1).ToArray(), depth + 1, scopedArguments, instanceOrder, nativeRootReference, choices);
            }
            var dynamicRoots = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Dynamic" && !InsideDefinition(e)).ToArray();
            foreach (var dynamic in dynamicRoots) Walk(dynamic.Elements(), [], 0, null);
            if (cursors.Values.Any(a => checked(checked(a.Start - a.Basis) + a.Size) > a.Maximum)) return source;
            foreach (var definition in root.Descendants().Where(e => e.Name.LocalName == "ModuleDef").ToList()) definition.Remove();
            if (!ModuleStructure.RefreshControls(root)) return source;
            root.AddAnnotation(new ModuleStructure.ModuleInstances(nativeInstances.ToArray()));
            if (communicationOrigins is not null) NativeCommunicationOccurrenceManifest.Seal(communicationOrigins, root, communicationOccurrences!);
            return root;
        }
        catch (Exception ex) when (ex is OverflowException or InvalidDataException) { return source; }
    }
    private sealed record Allocation(long Start, long Size, long Basis, long Maximum);
    private sealed record XmlOrder(int Value);
    private sealed record Dimension(int Order, int Index);
    private sealed record OriginalModuleId(string Value);
    private sealed record NativeArgumentReference(string RefId, string? AllocatorRefId);
    internal static IReadOnlyList<ApplicationModuleInstance> ReadInstances(XElement root) => root.Annotation<ModuleStructure.ModuleInstances>()?.Items ?? [];
    private static XElement Copy(XElement source)
    {
        var copy = new XElement(source);
        foreach (var (before, after) in source.DescendantsAndSelf().Zip(copy.DescendantsAndSelf()))
        {
            if (before.Annotation<XmlOrder>() is { } order) after.AddAnnotation(order);
            if (before.Annotation<OriginalModuleId>() is { } module) after.AddAnnotation(module);
            if (before.Annotation<NativeArgumentReference>() is { } argument) after.AddAnnotation(argument);
            if (before.Annotation<ParameterStorageArguments>() is { } storage) after.AddAnnotation(storage);
            if (before.Annotation<ModuleSourceNode>() is { } sourceNode) after.AddAnnotation(sourceNode);
            if (before.Annotation<ModuleElementOrigin>() is { } origin) after.AddAnnotation(origin);
            if (before.Annotation<NativeCommunicationSourceNode>() is { } communicationSource) after.AddAnnotation(communicationSource);
            if (before.Annotation<NativeCommunicationElementOrigin>() is { } communicationOrigin) after.AddAnnotation(communicationOrigin);
        }
        return copy;
    }
    private static bool InsideDefinition(XElement element) => element.Ancestors().Any(e => e.Name.LocalName == "ModuleDef");
    private static bool Number(string? value, out long number) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    private static void AddBase(XElement node, string name, string literalName, IReadOnlyDictionary<string, string> arguments, bool objectNumber)
    {
        if (node.Attribute(name) is not { } baseAttribute) return;
        if (!arguments.TryGetValue(baseAttribute.Value, out var rawBase) || !Number(rawBase, out var basis)
            || !Number((string?)node.Attribute(literalName) ?? "0", out var literal)) throw new InvalidDataException("Unresolved module base argument.");
        var effective = checked(basis + literal);
        if (objectNumber && effective is < 0 or > 65535) throw new InvalidDataException("Module object number exceeds the supported range.");
        node.SetAttributeValue(literalName, effective.ToString(CultureInfo.InvariantCulture));
        baseAttribute.Remove();
    }
}
