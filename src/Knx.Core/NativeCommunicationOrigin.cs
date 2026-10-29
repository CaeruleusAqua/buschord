using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed record NativeCommunicationSourceNode(int Key);
internal sealed record NativeCommunicationArgument(string Id, long Value);
internal sealed record NativeCommunicationModuleFrame(int CallKey, int DefinitionKey, string DefinitionId, string InstanceId,
    string NativeInstanceId, string NativeReferenceId, string RepeatIndex, string CallHash, string DefinitionHash,
    IReadOnlyList<NativeCommunicationArgument> Arguments);
internal sealed record NativeCommunicationElementOrigin(int Key, string OriginalId, string ExpandedId, string OriginalHash,
    IReadOnlyList<NativeCommunicationModuleFrame> Frames, bool Complete, string? NumberBaseId = null, long? NumberBase = null);

internal sealed record NativeCommunicationOccurrence(NativeCommunicationModuleFrame? Frame,
    IReadOnlyList<ApplicationModuleArgument> Arguments);

/// <summary>Transient expansion output, including instances with no parameter or object declarations.</summary>
internal sealed class NativeCommunicationOccurrenceManifest
{
    private readonly NativeCommunicationOriginCatalog _owner;
    internal IReadOnlyList<NativeCommunicationOccurrence> Occurrences { get; }
    private NativeCommunicationOccurrenceManifest(NativeCommunicationOriginCatalog owner,
        IReadOnlyList<NativeCommunicationOccurrence> occurrences)
    {
        _owner = owner;
        Occurrences = Array.AsReadOnly(occurrences.Select(o => new NativeCommunicationOccurrence(o.Frame,
            Array.AsReadOnly(o.Arguments.Select(a => new ApplicationModuleArgument(a.RefId, a.Value)).ToArray()))).ToArray());
    }
    internal bool BelongsTo(NativeCommunicationOriginCatalog owner) => ReferenceEquals(_owner, owner);
    internal static void Seal(NativeCommunicationOriginCatalog owner, XElement root,
        IReadOnlyList<NativeCommunicationOccurrence> occurrences)
    {
        root.RemoveAnnotations<NativeCommunicationOccurrenceManifest>();
        root.AddAnnotation(new NativeCommunicationOccurrenceManifest(owner, occurrences));
    }
}

/// <summary>Optional transient source facts for a fresh communication-table inspection, separate from parameter subset profiles.</summary>
internal sealed class NativeCommunicationOriginCatalog
{
    private readonly XElement[] _nodes;
    private readonly Dictionary<int, string> _hashes = [];
    private NativeStructuralActivationProof.SourcePlan? _structuralPlan;
    internal bool ImageModuleProfile { get; }
    internal bool IsAvailable => _nodes.Length != 0;
    internal IReadOnlyList<XElement> Nodes => _nodes;
    internal NativeStructuralActivationProof.SourcePlan StructuralPlan
        => _structuralPlan ??= NativeStructuralActivationProof.ReadSource(this);
    internal NativeCommunicationOriginCatalog(XElement source) : this(source, false) { }
    internal NativeCommunicationOriginCatalog(XElement source, bool imageModuleProfile)
    {
        ImageModuleProfile = imageModuleProfile;
        var nodes = source.DescendantsAndSelf().Take(500001).ToArray();
        _nodes = nodes.Length <= 500000 ? nodes : [];
    }
    internal XElement? At(int key) => key >= 0 && key < _nodes.Length ? _nodes[key] : null;
    internal string Hash(int key)
    {
        if (_hashes.TryGetValue(key, out var cached)) return cached;
        return _hashes[key] = CurrentHash(key);
    }
    internal string CurrentHash(int key)
    {
        if (At(key) is not { } node) return "";
        using var digest = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new() { CloseOutput = false, Indent = false })) node.WriteTo(writer);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(digest.Hash!);
    }
    internal void Stamp(XElement clone)
    {
        if (!IsAvailable) return;
        var key = 0;
        foreach (var node in clone.DescendantsAndSelf())
        {
            if (key >= _nodes.Length) return;
            if (node.Name.LocalName is "Module" or "ModuleDef" or "ComObject" or "ComObjectRef" or "ComObjectRefRef"
                or "Parameter" or "ParameterRef" or "ParameterType" or "ParameterRefRef" or "choose" or "when" or "Assign" or "Repeat")
            {
                node.RemoveAnnotations<NativeCommunicationSourceNode>();
                node.RemoveAnnotations<NativeCommunicationElementOrigin>();
                node.AddAnnotation(new NativeCommunicationSourceNode(key));
                node.AddAnnotation(new NativeCommunicationElementOrigin(key, Id(_nodes[key]), Id(node), Hash(key),
                    Array.AsReadOnly(Array.Empty<NativeCommunicationModuleFrame>()), true));
            }
            key++;
        }
        NativeCommunicationOccurrenceManifest.Seal(this, clone, []);
    }
    internal IReadOnlyList<NativeCommunicationModuleFrame>? Frame(XElement call, XElement definition, string instance,
        string nativeInstance, string nativeReference, string repeatIndex, IReadOnlyDictionary<string, string> numeric)
    {
        var sourceCall = call.Annotation<NativeCommunicationElementOrigin>();
        var sourceDefinition = definition.Annotation<NativeCommunicationElementOrigin>();
        if (sourceCall is not { Complete: true } || sourceDefinition is not { Complete: true }
            || sourceCall.Frames.Count >= 8 || numeric.Count > 64 || repeatIndex.Length > 2048) return null;
        var arguments = new List<NativeCommunicationArgument>(numeric.Count);
        foreach (var pair in numeric.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!Safe(pair.Key) || !long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
            arguments.Add(new(pair.Key, value));
        }
        var frame = new NativeCommunicationModuleFrame(sourceCall.Key, sourceDefinition.Key, Id(definition), instance, nativeInstance, nativeReference,
            repeatIndex, sourceCall.OriginalHash, sourceDefinition.OriginalHash, Array.AsReadOnly(arguments.ToArray()));
        return Array.AsReadOnly(sourceCall.Frames.Append(frame).ToArray());
    }
    internal void Remap(XElement node, IReadOnlyList<NativeCommunicationModuleFrame>? frames, string? numberBaseId, long? numberBase)
    {
        if (node.Annotation<NativeCommunicationElementOrigin>() is not { } source) return;
        node.RemoveAnnotations<NativeCommunicationElementOrigin>();
        node.AddAnnotation(source with
        {
            ExpandedId = Id(node), Frames = frames ?? Array.AsReadOnly(Array.Empty<NativeCommunicationModuleFrame>()),
            Complete = source.Complete && frames is not null && (numberBaseId is null || numberBase is not null),
            NumberBaseId = numberBaseId, NumberBase = numberBase,
        });
    }
    private static string Id(XElement node) => (string?)node.Attribute("Id") ?? "";
    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
}
