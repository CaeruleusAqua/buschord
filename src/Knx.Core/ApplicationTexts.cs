using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Knx.Workbench;

namespace Knx;

internal sealed record TextArgumentBinding(IReadOnlyDictionary<string, string> Named, bool HasValue, string? Value);
internal sealed record ObjectTextDefinition(string? Text, string? FunctionText, string? ReferenceId, string? DefaultValue, bool KnownReference,
    TextArgumentBinding? Arguments, ObjectChannelDefinition? Channel = null);

/// <summary>Bounded literal text interpolation; inserted parameter values are never parsed as templates.</summary>
public static class ApplicationTexts
{
    private static readonly Regex Placeholder = new(@"\{\{(?<key>[^{}:]+)(?::(?<fallback>[^{}]*))?\}\}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static string Render(string template, string? value, string? declaredValue = "") => Substitute(template, match =>
        match.Groups["key"].Value == "0" ? Replacement(match, value, declaredValue) : null);

    private static string Replacement(Match match, string? value, string? declaredValue) =>
        string.IsNullOrEmpty(value) && (value ?? "") == (declaredValue ?? "") ? match.Groups["fallback"].Value : value ?? "";

    private static string Substitute(string template, Func<Match, string?> replace)
    {
        if (template.Length > 65536) throw new InvalidDataException("Text template exceeds its safety limit.");
        var result = new StringBuilder(); var position = 0;
        void Append(string text)
        {
            if (result.Length + text.Length > 65536) throw new InvalidDataException("Expanded text exceeds its safety limit.");
            result.Append(text);
        }
        foreach (Match match in Placeholder.Matches(template))
        {
            Append(template[position..match.Index]);
            Append(replace(match) ?? match.Value); position = match.Index + match.Length;
        }
        Append(template[position..]);
        return result.ToString();
    }

    internal static void BindArguments(XElement node, IReadOnlyDictionary<string, string> byName, IReadOnlyDictionary<string, string> byId)
    {
        if (node.AncestorsAndSelf().Any(a => a.Name.LocalName == "ModuleDef")) return;
        var referenceId = (string?)node.Attribute("TextParameterRefId");
        var hasValue = referenceId is not null && byId.ContainsKey(referenceId);
        node.AddAnnotation(new TextArgumentBinding(byName, hasValue, hasValue ? byId[referenceId!] : null));
        // Passed text arguments become literal inputs to the inner module, not another template stage.
        if (node.Name.LocalName == "TextArg" && node.Attribute("Value") is { } argument)
            argument.Value = Substitute(argument.Value, match => byName.GetValueOrDefault(match.Groups["key"].Value));
    }

    private static string ResolveArguments(string template, string? referenceId, string? value, string? declaredValue,
        TextArgumentBinding? arguments, ISet<string>? issues)
    {
        var hasValue = referenceId is not null;
        if (arguments?.HasValue == true) { value = declaredValue = arguments.Value; hasValue = true; }
        return Substitute(template, match =>
        {
            var key = match.Groups["key"].Value;
            if (key == "0" && hasValue) return Replacement(match, value, declaredValue);
            if (arguments?.Named.TryGetValue(key, out var named) == true) return named;
            if (hasValue || arguments is not null) issues?.Add("Unresolved text placeholder.");
            return null;
        });
    }

    internal static Dictionary<string, string> Names(XElement root, IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?> defaults, IReadOnlyDictionary<string, string> renames, ISet<string> issues)
    {
        var parameters = root.DescendantsAndSelf().Where(e => e.Name.LocalName is "Parameter" or "ParameterInstance")
            .Where(e => e.Attribute("Id") is not null).GroupBy(e => (string)e.Attribute("Id")!).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf().Where(e => e.Name.LocalName is "Parameter" or "ParameterInstance" or "ParameterRef" or "ParameterBlock" or "Channel")
                     .Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")))
        {
            if ((string?)element.Attribute("Id") is not { } id) continue;
            var parameter = element.Name.LocalName == "ParameterRef"
                ? parameters.GetValueOrDefault((string?)element.Attribute("RefId") ?? "") : null;
            var template = renames.GetValueOrDefault(id)
                ?? (parameter is not null ? renames.GetValueOrDefault((string)parameter.Attribute("Id")!) : null)
                ?? (string?)element.Attribute("Text") ?? (string?)element.Attribute("Name")
                ?? (string?)parameter?.Attribute("Text") ?? (string?)parameter?.Attribute("Name");
            if (template is null) continue;
            var reference = (string?)element.Attribute("TextParameterRefId") ?? (string?)parameter?.Attribute("TextParameterRefId");
            var arguments = element.Attribute("TextParameterRefId") is not null ? element.Annotation<TextArgumentBinding>()
                : parameter?.Annotation<TextArgumentBinding>() ?? element.Annotation<TextArgumentBinding>();
            if (reference is not null && arguments?.HasValue != true && !values.ContainsKey(reference)) issues.Add("Unresolved text parameter reference.");
            result[id] = ResolveArguments(template, reference, reference is null ? null : values.GetValueOrDefault(reference),
                reference is null ? null : defaults.GetValueOrDefault(reference), arguments, issues);
        }
        return result;
    }

    /// <summary>Updates display-only inherited text; native overrides and project values are untouched.</summary>
    public static IReadOnlyList<string> RefreshObjects(Project project, DeviceInstance device, IReadOnlyDictionary<string, string?>? values = null)
    {
        var issues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in device.ComObjectRefs)
        {
            if (!project.ObjectTextDefinitions.TryGetValue(new(device.Id, instance.Id), out var definition)) continue;
            if (definition.Channel is { } channel)
            {
                var key = new ObjectKey(device.Id, instance.Id).ToString();
                if (!project.Workbench.Objects.TryGetValue(key, out var metadata)) project.Workbench.Objects[key] = metadata = new();
                ApplicationChannels.Apply(metadata, channel);
            }
            var current = definition.ReferenceId is { } id && values?.TryGetValue(id, out var calculated) == true ? calculated
                : definition.ReferenceId is { } savedId && device.ParameterValues.TryGetValue(savedId, out var saved) ? saved : definition.DefaultValue;
            if (definition.ReferenceId is not null && definition.Arguments?.HasValue != true && !definition.KnownReference) issues.Add("Unresolved object text parameter reference.");
            instance.InheritedText = definition.Text is null ? null : ResolveArguments(definition.Text, definition.ReferenceId, current, definition.DefaultValue, definition.Arguments, issues);
            instance.InheritedFunctionText = definition.FunctionText is null ? null : ResolveArguments(definition.FunctionText, definition.ReferenceId, current, definition.DefaultValue, definition.Arguments, issues);
        }
        return issues.ToArray();
    }
}
