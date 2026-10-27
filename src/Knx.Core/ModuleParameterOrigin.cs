using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed record ModuleSourceNode(int Key);
internal sealed record ModuleChoiceOrigin(int ChooseKey, int WhenKey, string SelectorId, string? RawValue, string ChooseHash);
internal sealed record ModuleLiteralArgument(string Id, string ExpandedId, long Literal, long Effective);
internal sealed record ModuleOccurrenceOrigin(int CallKey, int DefinitionKey, string CallId, string DefinitionId,
    string InstanceId, string NativeInstanceId, string NativeReferenceId, string CallHash, string DefinitionHash,
    IReadOnlyList<ModuleLiteralArgument> Arguments, IReadOnlyList<ModuleChoiceOrigin> Choices);
internal sealed record ModuleElementOrigin(ModuleOccurrenceOrigin Occurrence, int Key, string OriginalId, string ExpandedId, string Hash);
internal sealed record ModuleParameterOrigin(ModuleOccurrenceOrigin Occurrence, ModuleElementOrigin Parameter, ModuleElementOrigin Reference)
{
    internal static ModuleParameterOrigin? Read(XElement parameter, XElement? reference)
    {
        var p = parameter.Annotation<ModuleElementOrigin>(); var r = reference?.Annotation<ModuleElementOrigin>();
        return p is not null && r is not null && ReferenceEquals(p.Occurrence, r.Occurrence)
            && (string?)reference!.Attribute("RefId") == (string?)parameter.Attribute("Id")
            ? new(p.Occurrence, p, r) : null;
    }
}

/// <summary>Transient original-source index. Published origins contain only immutable facts, never XML nodes.</summary>
internal sealed class ModuleOriginCatalog
{
    private readonly XElement[] _nodes;
    private readonly Dictionary<int, string> _hashes = [];
    internal ModuleOriginCatalog(XElement source)
    {
        var nodes = source.DescendantsAndSelf().Take(500_001).ToArray();
        _nodes = nodes.Length <= 500_000 ? nodes : []; // Origin metadata must not block richer normal inspection.
    }
    internal XElement? At(int key) => key >= 0 && key < _nodes.Length ? _nodes[key] : null;
    internal void Stamp(XElement clone)
    {
        if (_nodes.Length == 0) return;
        var key = 0;
        foreach (var node in clone.DescendantsAndSelf())
        {
            if (node.Name.LocalName is "Module" or "ModuleDef" or "Parameter" or "ParameterRef" or "choose" or "when")
                node.AddAnnotation(new ModuleSourceNode(key));
            key++;
        }
    }
    internal string Hash(int key)
    {
        if (_hashes.TryGetValue(key, out var hash)) return hash;
        using var algorithm = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new() { CloseOutput = false, Indent = false })) _nodes[key].WriteTo(writer);
        stream.FlushFinalBlock(); return _hashes[key] = Convert.ToHexStringLower(algorithm.Hash!);
    }
    internal ModuleChoiceOrigin Choice(XElement choose, XElement when, IReadOnlyDictionary<string, string?> values)
    {
        var key = choose.Annotation<ModuleSourceNode>()?.Key ?? -1; var branch = when.Annotation<ModuleSourceNode>()?.Key ?? -1;
        var id = (string?)choose.Attribute("ParamRefId") ?? "";
        return new(key, branch, id, values.GetValueOrDefault(id), key >= 0 ? Hash(key) : "");
    }
    internal ModuleOccurrenceOrigin? Occurrence(XElement module, XElement definition, string instance, string nativeInstance, string nativeReference,
        int depth, int repeats, IReadOnlyDictionary<string, string> effective, Func<string, string> scope, IReadOnlyList<ModuleChoiceOrigin> choices)
    {
        // Diagnostic source facts are fail-soft. General inspection keeps supporting richer
        // scopes; only a genuinely literal direct occurrence receives this narrow origin.
        var callKey = module.Annotation<ModuleSourceNode>()?.Key ?? -1; var definitionKey = definition.Annotation<ModuleSourceNode>()?.Key ?? -1;
        if (depth != 0 || repeats != 0 || callKey < 0 || definitionKey < 0 || !Safe(instance) || !Safe(nativeInstance) || !Safe(nativeReference)
            || choices.Count > 16 || choices.Any(c => c.ChooseKey < 0 || c.WhenKey < 0)) return null;
        var originalCall = At(callKey)!; var originalDefinition = At(definitionKey)!;
        if (!TryLiteralArguments(originalCall, originalDefinition, out var literals)) return null;
        var arguments = new List<ModuleLiteralArgument>();
        foreach (var (id, literal) in literals)
        {
            if (!effective.TryGetValue(id, out var raw) || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != literal) return null;
            arguments.Add(new(id, scope(id), literal, value));
        }
        return new(callKey, definitionKey, (string?)originalCall.Attribute("Id") ?? "", (string?)originalDefinition.Attribute("Id") ?? "",
            instance, nativeInstance, nativeReference, Hash(callKey), Hash(definitionKey), Array.AsReadOnly(arguments.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(choices.ToArray()));
    }
    internal void Mark(XElement node, ModuleOccurrenceOrigin? occurrence)
    {
        if (occurrence is null || node.Ancestors().Any(e => e.Name.LocalName == "ModuleDef")
            || node.Name.LocalName is not ("Parameter" or "ParameterRef") || node.Annotation<ModuleSourceNode>() is not { } source) return;
        var original = At(source.Key);
        if (original is null) return;
        node.AddAnnotation(new ModuleElementOrigin(occurrence, source.Key, (string?)original.Attribute("Id") ?? "", (string?)node.Attribute("Id") ?? "", Hash(source.Key)));
    }
    internal static bool Safe(string? id) => id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl);
    internal static bool TryLiteralArguments(XElement call, XElement definition, out Dictionary<string, long> numeric)
    {
        numeric = new(StringComparer.Ordinal);
        var ns = call.Name.Namespace;
        if (call.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
            || a.Name.LocalName is not ("Id" or "RefId" or "Name" or "InternalDescription")))) return false;
        var containers = definition.Elements(ns + "Arguments").Take(2).ToArray();
        if (containers.Length > 1 || call.Elements().Take(65).Count() > 64) return false;
        var declarations = containers.SelectMany(e => e.Elements()).Take(65).ToArray();
        if (declarations.Length > 64 || declarations.Any(e => e.Name != ns + "Argument" || !Safe((string?)e.Attribute("Id")) || e.HasElements
            || !string.IsNullOrWhiteSpace(e.Value) || e.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None
                || a.Name.LocalName is not ("Id" or "Name" or "Type" or "InternalDescription" or "Allocates" or "Alignment")))
            || e.Attribute("Allocates") is { } size && !ulong.TryParse(size.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || e.Attribute("Alignment") is { Value: not ("1" or "2" or "4" or "8") })
            || declarations.GroupBy(e => (string)e.Attribute("Id")!).Any(g => g.Count() != 1)) return false;
        var map = declarations.ToDictionary(e => (string)e.Attribute("Id")!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in call.Elements())
        {
            var id = (string?)argument.Attribute("RefId");
            if (!Safe(id) || !seen.Add(id!) || !map.TryGetValue(id!, out var declared) || argument.HasElements || !string.IsNullOrWhiteSpace(argument.Value)) return false;
            var type = (string?)declared.Attribute("Type") ?? "Numeric";
            if (argument.Name == ns + "NumericArg" && type == "Numeric")
            {
                var raw = (string?)argument.Attribute("Value");
                if (argument.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("RefId" or "Value")))
                    || raw is not { Length: > 0 and <= 32 } || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return false;
                numeric.Add(id!, value);
            }
            else if (argument.Name != ns + "TextArg" || type != "Text" || (string?)argument.Attribute("Value") is not { Length: <= 255 }
                || argument.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || a.Name.LocalName is not ("Id" or "RefId" or "Value")))) return false;
        }
        return true;
    }
}
