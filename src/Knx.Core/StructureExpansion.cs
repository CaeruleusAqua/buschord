using System.Xml.Linq;

namespace Knx;

/// <summary>Bounded joint fixpoint of structural branches and effective parameter values.</summary>
internal static class StructureExpansion
{
    internal sealed record Failure(string Message);

    internal static XElement Expand(XElement source, IReadOnlyDictionary<string, string?>? userValues,
        IParameterScriptRuntime? scripts = null, string? editedReference = null)
    {
        if (!source.DescendantsAndSelf().Any(e => e.Name.LocalName == "Module"
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef"))) return source;
        if (scripts is null && RequiresStructuralScripts(source))
            return Failed(source, "Structural calculations require an explicit script runtime.");
        var inputs = userValues is null ? new Dictionary<string, string?>() : new(userValues);
        var origins = new ModuleOriginCatalog(source);
        // Global assignments must settle before reserving an inactive branch's allocation.
        // The unexpanded seed deliberately ignores missing module-local declarations; only
        // a complete expanded evaluation is eligible for publication below.
        var seed = DynamicParameters.EvaluateExpanded(source, userValues, scripts, editedReference);
        if (seed.Converged && seed.ScriptFailures == 0 && !seed.DepthLimitReached)
        {
            if (!ValidControls(source, seed)) return Failed(source, "Invalid structural parameter value.");
            foreach (var (id, state) in seed.Parameters) inputs[id] = state.Value;
        }
        XElement? previous = null;
        IReadOnlyDictionary<string, DynamicParameterState>? previousValues = null;
        for (var pass = 0; pass < DynamicParameters.MaxPasses; pass++)
        {
            var expanded = ApplicationModules.Expand(source, inputs, DynamicParameters.SelectStructureBranch, allowCalculatedCounts: true, origins: origins);
            if (ReferenceEquals(expanded, source)) return source;
            if (scripts is null && RequiresStructuralScripts(expanded))
                return Failed(source, "Structural calculations require an explicit script runtime.");
            var evaluation = DynamicParameters.EvaluateExpanded(expanded, userValues, scripts, editedReference);
            if (!evaluation.Converged || evaluation.ScriptFailures > 0 || evaluation.DepthLimitReached) return expanded;
            if (!ValidControls(expanded, evaluation)) return Failed(source, "Invalid structural parameter value.");
            if (previous is not null && XNode.DeepEquals(previous, expanded) && SameValues(previousValues!, evaluation.Parameters)) return expanded;
            previous = expanded;
            previousValues = evaluation.Parameters;
            inputs = evaluation.Parameters.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
            // Retired instance inputs must remain available if another branch makes them live again.
            if (userValues is not null)
                foreach (var (id, value) in userValues) inputs.TryAdd(id, value);
        }
        return Failed(source, "Structural parameter evaluation did not converge.");
    }

    private static XElement Failed(XElement source, string message)
    {
        var failed = new XElement(source);
        failed.AddAnnotation(new Failure(message));
        return failed;
    }

    private static bool ValidControls(XElement root, DynamicEvaluation evaluation)
    {
        var controls = ModuleStructure.ProtectedReferences(root);
        controls.UnionWith(root.Annotation<ModuleStructure.Controls>()?.DirectReferences ?? new HashSet<string>());
        var elements = root.DescendantsAndSelf().Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).ToList();
        var parameters = elements.Where(e => e.Name.LocalName is "Parameter" or "ParameterInstance")
            .Where(e => e.Attribute("Id") is not null).GroupBy(e => (string)e.Attribute("Id")!)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var types = elements.Where(e => e.Name.LocalName == "ParameterType").Select(Ksxml.ParseParameterType)
            .OfType<ParameterType>().GroupBy(t => t.Id).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var references = elements.Where(e => e.Name.LocalName == "ParameterRef" && e.Attribute("Id") is not null)
            .GroupBy(e => (string)e.Attribute("Id")!).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var id in controls.Where(evaluation.Parameters.ContainsKey))
        {
            XElement? reference = null;
            if (references.TryGetValue(id, out var matches))
            {
                if (matches.Length != 1) return false;
                reference = matches[0];
            }
            if (!parameters.TryGetValue((string?)reference?.Attribute("RefId") ?? id, out var parameter)) return false;
            var typeId = (string?)reference?.Attribute("ParameterType") ?? (string?)reference?.Attribute("ParameterTypeId")
                ?? (string?)parameter.Attribute("ParameterType") ?? (string?)parameter.Attribute("ParameterTypeId") ?? "";
            if (!types.TryGetValue(typeId, out var type)
                || !ParameterValidation.TryValidate(type, evaluation.Parameters[id].Value, out _)) return false;
        }
        return true;
    }

    private static bool SameValues(IReadOnlyDictionary<string, DynamicParameterState> a, IReadOnlyDictionary<string, DynamicParameterState> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value.Value == p.Value.Value);

    private static bool RequiresStructuralScripts(XElement root)
    {
        var dependencies = ModuleStructure.ProtectedReferences(root);
        dependencies.UnionWith(root.Annotation<ModuleStructure.Controls>()?.References ?? new HashSet<string>());
        var assignments = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Assign").ToList();
        var calculations = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterCalculation").ToList();
        // Both directions of a manufacturer calculation may write its bindings. Follow
        // Assign edges backwards too: a script may feed Count through an intermediate ref.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var assignment in assignments)
                if (dependencies.Contains((string?)assignment.Attribute("TargetParamRefRef") ?? "")
                    && (string?)assignment.Attribute("SourceParamRefRef") is { } source)
                    changed |= dependencies.Add(source);
            foreach (var calculation in calculations)
                if (calculation.Descendants().Where(e => e.Name.LocalName == "ParameterRefRef").Attributes("RefId")
                    .Any(a => dependencies.Contains(a.Value))) return true;
        }
        return false;
    }
}
