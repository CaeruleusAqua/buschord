using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>Repeat controls and structural validation shared by bounded module snapshots.</summary>
internal static class ModuleStructure
{
    internal sealed record Controls(HashSet<string> References, HashSet<string> DirectReferences, HashSet<string> CountReferences,
        bool AllowCalculatedCounts = false)
    {
        internal Controls(HashSet<string> references, HashSet<string> directReferences)
            : this(references, directReferences, new(directReferences, StringComparer.Ordinal)) { }
    }
    internal sealed record ModuleInstances(IReadOnlyList<ApplicationModuleInstance> Items);
    internal static bool Prepare(XElement root, IReadOnlyDictionary<string, string?>? saved, bool allowCalculatedCounts = false)
    {
        var counts = CountReferences(root).ToHashSet(StringComparer.Ordinal);
        var direct = counts.Concat(ChooseReferences(root)).ToHashSet(StringComparer.Ordinal);
        root.AddAnnotation(new Controls(new(direct, StringComparer.Ordinal), direct, counts, allowCalculatedCounts));
        return RefreshControls(root);
    }
    internal static bool TryCount(XElement root, XElement repeat, IReadOnlyDictionary<string, string?> values, out long count)
    {
        count = 0;
        if (repeat.Attribute("Id") is null || !Number((string?)repeat.Attribute("Count") ?? "0", out count) || count is < 0 or > 256
            || repeat.Elements().Any(e => e.Name.LocalName is not ("Module" or "Repeat" or "choose"))) return false;
        if ((string?)repeat.Attribute("ParameterRefId") is { } reference)
        {
            if (!values.TryGetValue(reference, out var value) || !Number(value, out var increment) || increment is < 0 or > 256) return false;
            count = checked(count + increment);
            if (root.Annotation<Controls>() is { } controls)
            { controls.DirectReferences.Add(reference); controls.References.Add(reference); controls.CountReferences.Add(reference); }
        }
        return count is >= 0 and <= 256 && RefreshControls(root);
    }
    internal static bool RefreshControls(XElement root)
    {
        if (root.Annotation<Controls>() is not { } controls) return true;
        foreach (var reference in CountReferences(root))
        { controls.DirectReferences.Add(reference); controls.References.Add(reference); controls.CountReferences.Add(reference); }
        foreach (var reference in ChooseReferences(root))
        { controls.DirectReferences.Add(reference); controls.References.Add(reference); }
        IncludeAliases(root, controls.References);
        IncludeAliases(root, controls.CountReferences);
        var writes = root.Descendants().Where(e => e.Name.LocalName == "Assign").Attributes("TargetParamRefRef").Select(a => a.Value)
            .Concat(root.Descendants().Where(e => e.Name.LocalName == "ParameterCalculation").Elements().Where(e => e.Name.LocalName is "LParameters" or "RParameters")
                .Elements().Where(e => e.Name.LocalName == "ParameterRefRef").Attributes("RefId").Select(a => a.Value));
        return controls.AllowCalculatedCounts || !writes.Any(controls.CountReferences.Contains);
    }
    internal static HashSet<string> ProtectedReferences(XElement root)
    {
        var controls = CountReferences(root).Concat(ChooseReferences(root)).ToHashSet(StringComparer.Ordinal);
        IncludeAliases(root, controls); return controls;
    }
    internal static void ProtectChoose(XElement root, XElement choose)
    {
        if (root.Annotation<Controls>() is { } controls && (string?)choose.Attribute("ParamRefId") is { } reference)
        { controls.DirectReferences.Add(reference); controls.References.Add(reference); IncludeAliases(root, controls.References); }
    }
    private static IEnumerable<string> CountReferences(XElement root) => root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Repeat")
        .Attributes("ParameterRefId").Select(a => a.Value);
    private static IEnumerable<string> ChooseReferences(XElement root) => root.DescendantsAndSelf().Where(e => e.Name.LocalName == "choose"
        && e.Descendants().Any(a => a.Name.LocalName is "Module" or "Repeat")).Attributes("ParamRefId").Select(a => a.Value);
    private static bool Number(string? value, out long number) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    private static void IncludeAliases(XElement root, HashSet<string> controls)
    {
        var references = root.Descendants().Where(e => e.Name.LocalName == "ParameterRef").ToList();
        var parameters = references.Where(e => controls.Contains((string?)e.Attribute("Id") ?? "")).Attributes("RefId").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
        controls.UnionWith(references.Where(e => parameters.Contains((string?)e.Attribute("RefId") ?? "")).Attributes("Id").Select(a => a.Value));
    }
}
