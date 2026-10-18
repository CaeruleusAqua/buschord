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

    /// <summary>State per declared ParameterRef Id. Empty when the program declares none.</summary>
    public IReadOnlyDictionary<string, DynamicParameterState> Parameters { get; internal set; } =
        new Dictionary<string, DynamicParameterState>(StringComparer.Ordinal);

    /// <summary>ParameterBlock ids declared inside Dynamic.</summary>
    public IReadOnlySet<string> KnownBlocks { get; internal set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Subset of <see cref="KnownBlocks"/> instantiated by the selected branches.</summary>
    public IReadOnlySet<string> VisibleBlocks { get; internal set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Constructs the evaluator saw and did not implement (deterministic, sorted, deduplicated).</summary>
    public IReadOnlyList<string> Unsupported { get; internal set; } = [];

    /// <summary>False when the program has no ParameterRefRef at all, so static visibility stays authoritative.</summary>
    public bool TracksVisibility { get; internal set; }
    public bool Converged { get; internal set; } = true;
    public bool DepthLimitReached { get; internal set; }
    public int Passes { get; internal set; }
    public int EvaluatedChooses { get; internal set; }
    public int SelectedBranches { get; internal set; }
    public int UnmatchedChooses { get; internal set; }
    public int Assignments { get; internal set; }
}

/// <summary>
/// Bounded evaluator for the static KSXML parameter program: <c>choose</c>, <c>when</c>,
/// <c>assign</c>, and ParameterRefRef/ParameterBlock placement.
///
/// Supported <c>when test</c> grammar (observed in the MDT corpus): exact values, space-separated
/// alternatives ("1 2") and one comparison per token ("!=0", "&lt;16", "&gt;=4", "&lt;=90", "&gt;0").
/// A bare <c>default="true"</c> branch is the fallback. Comparisons use invariant decimals; exact
/// matches fall back to ordinal text comparison (enum labels are never compared, only storage values).
///
/// Evaluation is deterministic: passes run in document order, each pass starts from the declared
/// values, and assignments are repeated until the value map is stable. A pass limit and a nesting
/// limit make cycles and pathological input finite and visible in <see cref="DynamicEvaluation.Unsupported"/>.
/// No script is executed: ParameterCalculation JavaScript, Module/ModuleDef instantiation, Repeat,
/// Button and load procedures are rejected and reported.
///
/// Re-evaluation after a UI edit: pass the edited values and apply the result again.
/// <code>
/// var state = DynamicParameters.Evaluate(programRoot, instances.ToDictionary(i => i.Id, i => i.Value));
/// DynamicParameters.Apply(state, instances, blocks);
/// </code>
/// </summary>
public static class DynamicParameters
{
    /// <summary>Maximum assignment fixpoint passes before the state is reported as non-convergent.</summary>
    public const int MaxPasses = 16;
    /// <summary>Maximum element nesting inside Dynamic before evaluation stops descending.</summary>
    public const int MaxDepth = 64;

    public static DynamicEvaluation Evaluate(XElement root, IReadOnlyDictionary<string, string?>? currentValues = null)
    {
        var declared = ReadDeclaredValues(root, out var unresolved);
        var initial = new Dictionary<string, string?>(declared, StringComparer.Ordinal);
        if (currentValues is not null)
            foreach (var (id, value) in currentValues)
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

        var state = new Dictionary<string, string?>(initial, StringComparer.Ordinal);
        var pass = new Pass(new Dictionary<string, string?>(initial, StringComparer.Ordinal), declared, state);
        var converged = false;
        var passes = 0;
        while (passes < MaxPasses)
        {
            passes++;
            var working = new Dictionary<string, string?>(initial, StringComparer.Ordinal);
            pass = new Pass(working, declared, state);
            Walk(dynamicRoots.SelectMany(e => e.Elements()), pass, 0);
            if (SameValues(working, state))
            {
                converged = true;
                state = working;
                break;
            }
            state = working;
        }

        var unsupported = new HashSet<string>(pass.Unsupported, StringComparer.Ordinal);
        foreach (var reference in unresolved) unsupported.Add("Unresolved parameter reference: " + reference);
        if (root.DescendantsAndSelf().Any(e => e.Name.LocalName == "ParameterCalculation"))
            unsupported.Add("Parameter calculations (JavaScript) are not executed.");
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
                IsEnabled = visible,
            };
        }

        return new DynamicEvaluation
        {
            Parameters = parameters,
            KnownBlocks = knownBlocks,
            VisibleBlocks = tracksVisibility ? pass.Blocks : knownBlocks,
            Unsupported = unsupported.Order(StringComparer.Ordinal).ToList(),
            TracksVisibility = tracksVisibility,
            Converged = converged,
            DepthLimitReached = pass.DepthLimit,
            Passes = passes,
            EvaluatedChooses = pass.Chooses,
            SelectedBranches = pass.Selected,
            UnmatchedChooses = pass.Unmatched,
            Assignments = pass.Assignments,
        };
    }

    /// <summary>Convenience overload that uses the current values of already parsed rows.</summary>
    public static DynamicEvaluation Evaluate(XElement root, IEnumerable<ParameterInstance> instances) =>
        Evaluate(root, instances.GroupBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal));

    /// <summary>Writes an evaluation onto parsed rows; static declarations stay in DefaultValue.</summary>
    public static void Apply(DynamicEvaluation evaluation, IEnumerable<ParameterInstance> instances,
        IEnumerable<ParameterBlock>? blocks = null)
    {
        foreach (var instance in instances)
            if (evaluation.Parameters.TryGetValue(instance.Id, out var state))
            {
                instance.Value = state.Value;
                instance.IsVisible = instance.IsStaticallyVisible && state.IsVisible;
                instance.IsEnabled = instance.IsStaticallyEnabled && state.IsEnabled;
            }

        if (blocks is null) return;
        foreach (var block in blocks)
            if (evaluation.KnownBlocks.Contains(block.Id))
            {
                block.IsVisible = evaluation.VisibleBlocks.Contains(block.Id);
                block.IsEnabled = block.IsVisible;
            }
    }

    /// <summary>Declared value per ParameterRef Id, mirroring <see cref="Ksxml.Parse"/> precedence.</summary>
    private static Dictionary<string, string?> ReadDeclaredValues(XElement root, out List<string> unresolved)
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
        Dictionary<string, string?> previous)
    {
        public Dictionary<string, string?> Values { get; } = values;
        public Dictionary<string, string?> Declared { get; } = declared;
        public Dictionary<string, string?> Previous { get; } = previous;
        public HashSet<string> Assigned { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Blocks { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unsupported { get; } = new(StringComparer.Ordinal);
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
            return;
        }

        switch (element.Name.LocalName)
        {
            case "ParameterRefRef":
                var refId = (string?)element.Attribute("RefId");
                if (refId is null) pass.Unsupported.Add("ParameterRefRef without RefId.");
                else if (!pass.Declared.ContainsKey(refId)) pass.Unsupported.Add("Unknown parameter reference: " + refId);
                else pass.Visited.Add(refId);
                return;
            case "choose":
                Choose(element, pass, depth);
                return;
            case "Assign":
                Assign(element, pass);
                return;
            case "ParameterBlock":
                if ((string?)element.Attribute("Id") is { } blockId) pass.Blocks.Add(blockId);
                Walk(element.Elements(), pass, depth + 1);
                return;
            case "Dynamic" or "Channel" or "ChannelIndependentBlock":
                Walk(element.Elements(), pass, depth + 1);
                return;
            case "when":
                pass.Unsupported.Add("when outside choose.");
                return;
            case "ParameterSeparator" or "ComObjectRefRef" or "Rows" or "Columns" or "Row" or "Column":
                return;
            case "ParameterCalculation" or "Module" or "ModuleDef" or "Repeat" or "Button"
                or "Rename" or "ParameterBlockRename":
                pass.Unsupported.Add("Unsupported dynamic construct: " + element.Name.LocalName);
                return;
            default:
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
            return;
        }
        if (!pass.Declared.ContainsKey(paramRefId))
        {
            pass.Unsupported.Add("choose references unknown parameter: " + paramRefId);
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
                continue;
            }
            var matched = Matches(test, current, out var unsupported);
            if (unsupported is not null) pass.Unsupported.Add(unsupported);
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
