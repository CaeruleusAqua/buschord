using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>Evaluated state of one parameter reference (KSXML ParameterRef).</summary>
public sealed class DynamicParameterState
{
    public required string RefId { get; init; }
    /// <summary>Value declared in the static XML (ParameterRef/Parameter), before assignments.</summary>
    public string? DeclaredValue { get; init; }
    /// <summary>Effective value after the initial dynamic evaluation.</summary>
    public string? Value { get; init; }
    /// <summary>True when the selected dynamic branches instantiate this reference.</summary>
    public bool IsVisible { get; init; }
    /// <summary>KSXML has no separate enable action in the supported patterns; mirrors <see cref="IsVisible"/>.</summary>
    public bool IsEnabled { get; init; }
}

/// <summary>Result of one initial dynamic evaluation of an ApplicationProgram.</summary>
public sealed class DynamicEvaluation
{
    internal DynamicEvaluation() { }
    internal XElement? ExpandedRoot { get; set; }
    public bool StructureFailed { get; internal set; }
    public IReadOnlySet<string> StructuralParameters { get; internal set; } = new HashSet<string>();
    public IReadOnlySet<string> KnownObjectReferences { get; internal set; } = new HashSet<string>();
    public IReadOnlySet<string> ActiveObjectReferences { get; internal set; } = new HashSet<string>();
    /// <summary>Unique channel of each active object placement; null means independent or ambiguous.</summary>
    public IReadOnlyDictionary<string, string?> ObjectChannels { get; internal set; } = new Dictionary<string, string?>();
    public bool ObjectActivityComplete { get; internal set; } = true;

    /// <summary>State per declared ParameterRef Id. Empty when the program declares none.</summary>
    public IReadOnlyDictionary<string, DynamicParameterState> Parameters { get; internal set; } =
        new Dictionary<string, DynamicParameterState>(StringComparer.Ordinal);

    /// <summary>ParameterBlock ids declared inside Dynamic.</summary>
    public IReadOnlySet<string> KnownBlocks { get; internal set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Subset of <see cref="KnownBlocks"/> instantiated by the selected branches.</summary>
    public IReadOnlySet<string> VisibleBlocks { get; internal set; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Renames { get; internal set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ResolvedNames { get; internal set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ParameterSections { get; internal set; } = new Dictionary<string, string>();

    /// <summary>Constructs the evaluator saw and did not implement (deterministic, sorted, deduplicated).</summary>
    public IReadOnlyList<string> Unsupported { get; internal set; } = [];

    /// <summary>RefIds whose effective value came from an <c>Assign</c> in the final pass.</summary>
    internal IReadOnlySet<string> AssignedParameters { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// RefIds whose assigned value failed type/range validation during the last
    /// <see cref="DynamicParameters.Apply"/>; their rows keep the declared value.
    /// </summary>
    public IReadOnlyList<string> InvalidAssignments { get; internal set; } = [];

    /// <summary>False when the program has no ParameterRefRef at all, so static visibility stays authoritative.</summary>
    public bool TracksVisibility { get; internal set; }
    public bool Converged { get; internal set; } = true;
    public bool DepthLimitReached { get; internal set; }
    public int Passes { get; internal set; }
    public int EvaluatedChooses { get; internal set; }
    public int SelectedBranches { get; internal set; }
    public int UnmatchedChooses { get; internal set; }
    public int Assignments { get; internal set; }
    public int ScriptExecutions { get; internal set; }
    public int ScriptFailures { get; internal set; }
}

/// <summary>
/// Bounded evaluator for the static KSXML parameter program: <c>choose</c>, <c>when</c>,
/// <c>assign</c>, and ParameterRefRef/ParameterBlock placement.
///
/// Supported <c>when test</c> grammar: exact values, space-separated
/// alternatives ("1 2") and one comparison per token ("!=0", "&lt;16", "&gt;=4", "&lt;=90", "&gt;0").
/// A bare <c>default="true"</c> branch is the fallback. Comparisons use invariant decimals; exact
/// matches fall back to ordinal text comparison (enum labels are never compared, only storage values).
///
/// Evaluation is deterministic: passes run in document order, each pass starts from the declared
/// values, and assignments are repeated until the value map is stable. A pass limit and a nesting
/// limit make cycles and pathological input finite and visible in <see cref="DynamicEvaluation.Unsupported"/>.
/// Scripts require an explicit runtime capability; the core never loads an interpreter itself.
/// Literal module hierarchies and bounded unconditional Repeat/global allocator snapshots are
/// expanded for inspection. Other module structures, Button and load procedures are reported.
///
/// Re-evaluation after a UI edit: pass only the caller-known user edits and apply the result again.
/// Never seed from effective row values, because they already contain earlier assignment results.
/// <code>
/// var state = DynamicParameters.Evaluate(programRoot,
///     new Dictionary&lt;string, string?&gt; { ["R1"] = "5" });
/// DynamicParameters.Apply(state, instances, blocks);
/// </code>
/// </summary>
public static class DynamicParameters
{
    /// <summary>Maximum assignment fixpoint passes before the state is reported as non-convergent.</summary>
    public const int MaxPasses = 16;
    /// <summary>Maximum element nesting inside Dynamic before evaluation stops descending.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Evaluates the program. <paramref name="userValues"/> must contain explicit user edits only,
    /// keyed by ParameterRef id; derived values from a previous evaluation or Apply must not be fed back.
    /// Unknown keys are ignored. Each pass starts from the declarations plus these edits.
    /// </summary>
    public static DynamicEvaluation Evaluate(XElement root,
        IReadOnlyDictionary<string, string?>? userValues = null, IParameterScriptRuntime? scripts = null, string? editedReference = null,
        bool editableStructure = false)
        => EvaluateExpanded(StructureExpansion.Expand(root, userValues, scripts, editedReference), userValues, scripts, editedReference, editableStructure);

    internal static DynamicEvaluation EvaluateExpanded(XElement root,
        IReadOnlyDictionary<string, string?>? userValues = null, IParameterScriptRuntime? scripts = null, string? editedReference = null,
        bool editableStructure = false)
    {
        var structureFailed = root.Annotation<StructureExpansion.Failure>() is not null
            || root.DescendantsAndSelf().Any(e => e.Name.LocalName is "Module" or "Repeat" && !InsideModuleDef(e));
        var failedControls = structureFailed ? ModuleStructure.ProtectedReferences(root) : new HashSet<string>();
        var declared = ReadDeclaredValues(root, out var unresolved);
        var initial = new Dictionary<string, string?>(declared, StringComparer.Ordinal);
        if (userValues is not null)
            foreach (var (id, value) in userValues)
                if (initial.ContainsKey(id)) initial[id] = value;

        var dynamicRoots = root.DescendantsAndSelf()
            .Where(e => e.Name.LocalName == "Dynamic" && !InsideModuleDef(e))
            .ToList();
        var knownBlocks = dynamicRoots.SelectMany(e => e.Descendants())
            .Where(e => e.Name.LocalName == "ParameterBlock")
            .Select(e => (string?)e.Attribute("Id"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var tracksVisibility = dynamicRoots.SelectMany(e => e.DescendantsAndSelf())
            .Any(e => e.Name.LocalName == "ParameterRefRef");
        var renameTargets = root.DescendantsAndSelf().Where(e => e.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterBlock")
            .Attributes("Id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);

        var state = new Dictionary<string, string?>(initial, StringComparer.Ordinal);
        var calculations = new ParameterCalculations(root, scripts, editedReference);
        var pass = new Pass(new Dictionary<string, string?>(initial, StringComparer.Ordinal), declared, state, renameTargets);
        var converged = false;
        var passes = 0;
        while (passes < MaxPasses)
        {
            passes++;
            var working = new Dictionary<string, string?>(initial, StringComparer.Ordinal);
            pass = new Pass(working, declared, state, renameTargets);
            Walk(dynamicRoots.SelectMany(e => e.Elements()), pass, 0);
            calculations.Apply(working, pass.Read, pass.Assigned, pass.Unsupported, pass.Blocked);
            if (SameValues(working, state))
            {
                converged = true;
                state = working;
                break;
            }
            state = working;
        }

        var unsupported = new HashSet<string>(pass.Unsupported, StringComparer.Ordinal);
        if (root.Annotation<StructureExpansion.Failure>() is { } structureError) unsupported.Add(structureError.Message);
        var declaredObjects = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObject" && !InsideModuleDef(e))
            .Attributes("Id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
        var objects = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObjectRef" && !InsideModuleDef(e)
                && declaredObjects.Contains((string?)e.Attribute("RefId") ?? ""))
            .Attributes("Id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var id in pass.Objects.Where(id => !objects.Contains(id))) unsupported.Add("Unknown object reference: " + id);
        foreach (var reference in unresolved) unsupported.Add("Unresolved parameter reference: " + reference);
        if (root.DescendantsAndSelf().Any(e => e.Name.LocalName == "ModuleDef"))
            unsupported.Add("Module definitions are not instantiated.");
        if (!converged)
            unsupported.Add($"Dynamic assignment did not converge after {MaxPasses} passes.");

        var parameters = new Dictionary<string, DynamicParameterState>(StringComparer.Ordinal);
        foreach (var (id, declaredValue) in declared)
        {
            var visible = !tracksVisibility || pass.Visited.Contains(id);
            parameters[id] = new DynamicParameterState
            {
                RefId = id,
                DeclaredValue = declaredValue,
                Value = state.GetValueOrDefault(id),
                IsVisible = visible,
                IsEnabled = visible && !pass.Blocked.Contains(id)
                    && !failedControls.Contains(id)
                    && (root.Annotation<ModuleStructure.Controls>()?.References.Contains(id) != true
                        || editableStructure && root.Annotation<ModuleStructure.Controls>()!.DirectReferences.Contains(id)),
            };
        }
        var names = ApplicationTexts.Names(root, state, declared, pass.Renames, unsupported);
        var blockNames = knownBlocks.ToDictionary(id => id, id => names.GetValueOrDefault(id) ?? "");

        return new DynamicEvaluation
        {
            ExpandedRoot = root,
            StructureFailed = structureFailed,
            StructuralParameters = root.Annotation<ModuleStructure.Controls>()?.DirectReferences ?? new HashSet<string>(),
            KnownObjectReferences = objects,
            ActiveObjectReferences = pass.Objects,
            ObjectChannels = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(pass.ObjectChannels),
            ObjectActivityComplete = !pass.ObjectActivityUnsupported && pass.Objects.All(objects.Contains),
            Parameters = parameters,
            KnownBlocks = knownBlocks,
            VisibleBlocks = tracksVisibility ? pass.Blocks : knownBlocks,
            Renames = pass.Renames,
            ResolvedNames = names,
            ParameterSections = pass.ParameterBlocks.Where(p => blockNames.TryGetValue(p.Value, out var title) && title.Length > 0)
                .ToDictionary(p => p.Key, p => blockNames[p.Value]),
            Unsupported = unsupported.Order(StringComparer.Ordinal).ToList(),
            AssignedParameters = new HashSet<string>(pass.Assigned, StringComparer.Ordinal),
            TracksVisibility = tracksVisibility,
            Converged = converged,
            DepthLimitReached = pass.DepthLimit,
            Passes = passes,
            EvaluatedChooses = pass.Chooses,
            SelectedBranches = pass.Selected,
            UnmatchedChooses = pass.Unmatched,
            Assignments = pass.Assignments,
            ScriptExecutions = calculations.Executions,
            ScriptFailures = calculations.Failures,
        };
    }

    /// <summary>
    /// Writes an evaluation onto parsed rows; static declarations stay in DefaultValue.
    /// Assigned values are validated against the declared parameter type/range. An invalid
    /// assignment keeps the declared value and its refId is reported in
    /// <see cref="DynamicEvaluation.InvalidAssignments"/>.
    /// </summary>
    public static void Apply(DynamicEvaluation evaluation, IEnumerable<ParameterInstance> instances,
        IEnumerable<ParameterBlock>? blocks = null)
    {
        var invalid = new List<string>();
        foreach (var instance in instances)
        {
            instance.IsDerivedValue = evaluation.AssignedParameters.Contains(instance.Id);
            instance.DeclaredName ??= instance.Name;
            instance.Name = evaluation.ResolvedNames.GetValueOrDefault(instance.Id)
                ?? (instance.ParameterId is { } parameterId ? evaluation.ResolvedNames.GetValueOrDefault(parameterId) : null) ?? instance.DeclaredName;
            instance.SectionTitle = evaluation.ParameterSections.GetValueOrDefault(instance.Id);
            if (evaluation.Parameters.TryGetValue(instance.Id, out var state))
            {
                var value = state.Value;
                if (evaluation.AssignedParameters.Contains(instance.Id) && instance.ParameterType is not null &&
                    !ParameterValidation.TryValidate(instance.ParameterType, value, out _))
                {
                    value = state.DeclaredValue;
                    instance.IsDerivedValue = false;
                    invalid.Add(instance.Id);
                }
                instance.Value = value;
                instance.IsVisible = instance.IsStaticallyVisible && state.IsVisible;
                instance.IsEnabled = instance.IsStaticallyEnabled && state.IsEnabled;
            }
        }
        evaluation.InvalidAssignments = invalid.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();

        if (blocks is null) return;
        foreach (var block in blocks)
        {
            block.DeclaredName ??= block.Name ?? "";
            block.Name = evaluation.ResolvedNames.GetValueOrDefault(block.Id) ?? block.DeclaredName;
            if (evaluation.KnownBlocks.Contains(block.Id))
            {
                block.IsVisible = evaluation.VisibleBlocks.Contains(block.Id);
                block.IsEnabled = block.IsVisible;
            }
        }
    }

    /// <summary>Declared value per ParameterRef Id, mirroring <see cref="Ksxml.Parse"/> precedence.</summary>
    internal static Dictionary<string, string?> ReadDeclaredValues(XElement root, out List<string> unresolved)
    {
        var parameters = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var parameter in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName is "Parameter" or "ParameterInstance" && !InsideModuleDef(e)))
            if ((string?)parameter.Attribute("Id") is { } id) parameters.TryAdd(id, parameter);

        var declared = new Dictionary<string, string?>(StringComparer.Ordinal);
        unresolved = [];
        foreach (var reference in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName == "ParameterRef" && !InsideModuleDef(e)))
        {
            var id = (string?)reference.Attribute("Id");
            if (id is null) continue;
            var refId = (string?)reference.Attribute("RefId");
            var parameter = refId is null ? null : parameters.GetValueOrDefault(refId);
            if (parameter is null && refId is not null) unresolved.Add(refId);
            declared[id] = (string?)reference.Attribute("Value")
                ?? (string?)parameter?.Attribute("Value")
                ?? (string?)reference.Attribute("DefaultValue")
                ?? (string?)reference.Attribute("InitialValue")
                ?? (string?)parameter?.Attribute("DefaultValue")
                ?? (string?)parameter?.Attribute("InitialValue");
        }
        return declared;
    }

    private static bool InsideModuleDef(XElement element) =>
        element.AncestorsAndSelf().Any(a => a.Name.LocalName == "ModuleDef");

    private static bool SameValues(Dictionary<string, string?> left, Dictionary<string, string?> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (id, value) in left)
            if (!right.TryGetValue(id, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
                return false;
        return true;
    }

    private sealed class Pass(Dictionary<string, string?> values, Dictionary<string, string?> declared,
        Dictionary<string, string?> previous, IReadOnlySet<string> renameTargets)
    {
        public Dictionary<string, string?> Values { get; } = values;
        public Dictionary<string, string?> Declared { get; } = declared;
        public Dictionary<string, string?> Previous { get; } = previous;
        public HashSet<string> Assigned { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Blocks { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> ObjectChannels { get; } = new(StringComparer.Ordinal);
        public Stack<string?> ChannelStack { get; } = new();
        public bool ObjectActivityUnsupported;
        public HashSet<string> Unsupported { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Blocked { get; } = new(StringComparer.Ordinal);
        public IReadOnlySet<string> RenameTargets { get; } = renameTargets;
        public Dictionary<string, string> Renames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ParameterBlocks { get; } = new(StringComparer.Ordinal);
        public Stack<string> BlockStack { get; } = new();
        public int Chooses;

        /// <summary>Assignment applied earlier in this pass wins; otherwise use the previous pass.</summary>
        public string? Read(string id) =>
            Assigned.Contains(id) ? Values.GetValueOrDefault(id) : Previous.GetValueOrDefault(id);
        public int Selected;
        public int Unmatched;
        public int Assignments;
        public bool DepthLimit;
    }

    private static void Walk(IEnumerable<XElement> elements, Pass pass, int depth)
    {
        foreach (var element in elements) Visit(element, pass, depth);
    }

    private static void Visit(XElement element, Pass pass, int depth)
    {
        if (depth > MaxDepth)
        {
            if (!pass.DepthLimit)
            {
                pass.DepthLimit = true;
                pass.Unsupported.Add($"Dynamic nesting exceeds {MaxDepth} levels.");
            }
            pass.ObjectActivityUnsupported = true;
            return;
        }

        switch (element.Name.LocalName)
        {
            case "ParameterRefRef":
                var refId = (string?)element.Attribute("RefId");
                if (refId is null) pass.Unsupported.Add("ParameterRefRef without RefId.");
                else if (!pass.Declared.ContainsKey(refId)) pass.Unsupported.Add("Unknown parameter reference: " + refId);
                else pass.Visited.Add(refId);
                if (refId is not null && pass.BlockStack.TryPeek(out var block)) pass.ParameterBlocks[refId] = block;
                return;
            case "choose":
                Choose(element, pass, depth);
                return;
            case "Assign":
                Assign(element, pass);
                return;
            case "ParameterBlock":
                var blockId = (string?)element.Attribute("Id");
                if (blockId is not null) { pass.Blocks.Add(blockId); pass.BlockStack.Push(blockId); }
                try { Walk(element.Elements(), pass, depth + 1); }
                finally { if (blockId is not null) pass.BlockStack.Pop(); }
                return;
            case "Rename" or "ParameterBlockRename":
                if ((string?)element.Attribute("RefId") is { } target && pass.RenameTargets.Contains(target)
                    && (string?)element.Attribute("Text") is { } text) pass.Renames[target] = text;
                else pass.Unsupported.Add("Unresolved or unsupported Rename target.");
                return;
            case "Channel" or "ChannelIndependentBlock":
                pass.ChannelStack.Push(element.Name.LocalName == "Channel" ? (string?)element.Attribute("Id") : null);
                try { Walk(element.Elements(), pass, depth + 1); }
                finally { pass.ChannelStack.Pop(); }
                return;
            case "Dynamic":
                Walk(element.Elements(), pass, depth + 1);
                return;
            case "when":
                pass.Unsupported.Add("when outside choose.");
                if (element.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
                return;
            case "ComObjectRefRef":
                if ((string?)element.Attribute("RefId") is { } objectId)
                {
                    pass.Objects.Add(objectId);
                    var channel = pass.ChannelStack.TryPeek(out var currentChannel) ? currentChannel : null;
                    if (!pass.ObjectChannels.TryAdd(objectId, channel) && pass.ObjectChannels[objectId] != channel)
                        pass.ObjectChannels[objectId] = null;
                }
                else { pass.Unsupported.Add("ComObjectRefRef without RefId."); pass.ObjectActivityUnsupported = true; }
                return;
            case "ParameterSeparator" or "Rows" or "Columns" or "Row" or "Column":
                return;
            case "ParameterCalculation" or "Module" or "ModuleDef" or "Repeat" or "Button":
                if (element.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
                pass.Unsupported.Add("Unsupported dynamic construct: " + element.Name.LocalName);
                return;
            default:
                if (element.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
                pass.Unsupported.Add(element.Name.LocalName.StartsWith("LdCtrl", StringComparison.Ordinal)
                    ? "Unsupported load procedure: " + element.Name.LocalName
                    : "Unsupported dynamic construct: " + element.Name.LocalName);
                return;
        }
    }

    private static void Choose(XElement choose, Pass pass, int depth)
    {
        pass.Chooses++;
        var paramRefId = (string?)choose.Attribute("ParamRefId");
        if (paramRefId is null)
        {
            pass.Unsupported.Add("choose without ParamRefId.");
            if (choose.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
            return;
        }
        if (!pass.Declared.ContainsKey(paramRefId))
        {
            pass.Unsupported.Add("choose references unknown parameter: " + paramRefId);
            if (choose.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
            return;
        }
        var current = pass.Read(paramRefId);

        XElement? fallback = null;
        XElement? selected = null;
        foreach (var branch in choose.Elements().Where(e => e.Name.LocalName == "when"))
        {
            if (bool.TryParse((string?)branch.Attribute("default"), out var isDefault) && isDefault)
            {
                fallback ??= branch;
                continue;
            }

            var test = (string?)branch.Attribute("test");
            if (test is null)
            {
                pass.Unsupported.Add("when without test or default.");
                if (choose.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
                continue;
            }
            var matched = Matches(test, current, out var unsupported);
            if (unsupported is not null)
            {
                pass.Unsupported.Add(unsupported);
                if (choose.Descendants().Any(e => e.Name.LocalName == "ComObjectRefRef")) pass.ObjectActivityUnsupported = true;
            }
            if (matched)
            {
                selected = branch;
                break;
            }
        }

        selected ??= fallback;
        if (selected is null) pass.Unmatched++;
        else
        {
            pass.Selected++;
            Walk(selected.Elements(), pass, depth + 1);
        }
    }

    /// <summary>The same condition grammar used by parameter and structural evaluation.</summary>
    internal static XElement? SelectStructureBranch(XElement choose, IReadOnlyDictionary<string, string?> values)
    {
        var id = (string?)choose.Attribute("ParamRefId") ?? throw new InvalidDataException("Structural choose has no parameter reference.");
        if (!values.TryGetValue(id, out var value)) throw new InvalidDataException("Structural choose references an unknown parameter.");
        XElement? fallback = null;
        foreach (var branch in choose.Elements().Where(e => e.Name.LocalName == "when"))
        {
            if (bool.TryParse((string?)branch.Attribute("default"), out var isDefault) && isDefault) { fallback ??= branch; continue; }
            var test = (string?)branch.Attribute("test") ?? throw new InvalidDataException("Structural when has no test.");
            var matched = Matches(test, value, out var unsupported);
            if (unsupported is not null) throw new InvalidDataException(unsupported);
            if (matched) return branch;
        }
        return fallback;
    }

    private static void Assign(XElement assign, Pass pass)
    {
        var target = (string?)assign.Attribute("TargetParamRefRef");
        if (target is null)
        {
            pass.Unsupported.Add("Assign without TargetParamRefRef.");
            return;
        }
        if (!pass.Declared.ContainsKey(target))
        {
            pass.Unsupported.Add("Assign targets unknown parameter: " + target);
            return;
        }
        if ((string?)assign.Attribute("Value") is { } value)
        {
            pass.Values[target] = value;
            pass.Assigned.Add(target);
            pass.Assignments++;
            return;
        }
        var source = (string?)assign.Attribute("SourceParamRefRef");
        if (source is null)
        {
            pass.Unsupported.Add("Assign without Value or SourceParamRefRef.");
            return;
        }
        if (!pass.Declared.ContainsKey(source))
        {
            pass.Unsupported.Add("Assign source unknown parameter: " + source);
            return;
        }
        pass.Values[target] = pass.Read(source);
        pass.Assigned.Add(target);
        pass.Assignments++;
    }

    /// <summary>Any whitespace-separated alternative may match; a token is an equality or one comparison.</summary>
    private static bool Matches(string test, string? value, out string? unsupported)
    {
        unsupported = null;
        if (value is null) return false;

        var tokens = test.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var comparisons = tokens.Count(t => IsComparison(t));
        if (comparisons > 0 && tokens.Length > 1)
        {
            // Combining alternatives with comparisons has no defined order; refuse instead of guessing.
            unsupported = $"Unsupported dynamic condition: test=\"{test}\"";
            return false;
        }

        var matched = false;
        foreach (var token in tokens)
        {
            if (token.Contains("..", StringComparison.Ordinal) || !TryToken(token, value, out var tokenMatch))
            {
                unsupported ??= $"Unsupported dynamic condition: test=\"{test}\"";
                continue;
            }
            matched |= tokenMatch;
        }
        return matched;
    }

    private static bool IsComparison(string token) =>
        token.StartsWith("!=", StringComparison.Ordinal) || token.StartsWith(">", StringComparison.Ordinal) ||
        token.StartsWith("<", StringComparison.Ordinal);

    private static bool TryToken(string token, string value, out bool match)
    {
        match = false;
        var (op, operand) = token switch
        {
            _ when token.StartsWith("!=", StringComparison.Ordinal) => ("!=", token[2..]),
            _ when token.StartsWith(">=", StringComparison.Ordinal) => (">=", token[2..]),
            _ when token.StartsWith("<=", StringComparison.Ordinal) => ("<=", token[2..]),
            _ when token.StartsWith(">", StringComparison.Ordinal) => (">", token[1..]),
            _ when token.StartsWith("<", StringComparison.Ordinal) => ("<", token[1..]),
            _ => ("=", token),
        };

        if (op == "=")
        {
            match = ValuesEqual(value, operand);
            return true;
        }
        if (op == "!=" && !(TryNumber(value, out var textLeft) && TryNumber(operand, out var textRight)))
        {
            match = !string.Equals(value, operand, StringComparison.Ordinal);
            return true;
        }
        if (!TryNumber(value, out var left) || !TryNumber(operand, out var right)) return false;
        match = op switch
        {
            "!=" => left != right,
            ">=" => left >= right,
            "<=" => left <= right,
            ">" => left > right,
            "<" => left < right,
            _ => false,
        };
        return true;
    }

    private static bool ValuesEqual(string value, string operand) =>
        TryNumber(value, out var left) && TryNumber(operand, out var right)
            ? left == right
            : string.Equals(value, operand, StringComparison.Ordinal);

    private static bool TryNumber(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // Cycles: a value oscillation is visible because every pass starts from the declared values
    // and re-applies the now active branches. MaxPasses bounds the fixpoint.
}
