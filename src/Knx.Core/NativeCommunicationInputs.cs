using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Knx.Workbench;

namespace Knx;

internal sealed class NativeCommunicationRejected(GeneratedTableIssue issue) : Exception { internal GeneratedTableIssue Issue { get; } = issue; }
internal static class NativeCommunicationGuard
{
    [DoesNotReturn]
    internal static void Fail(GeneratedTableIssueCode code, string? id = null) => throw new NativeCommunicationRejected(new(code, Safe(id) ? id : null));
    internal static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);
    internal static string Hash(object value) => ParameterPatchSourceBinding.Hash(value);
}
internal sealed record NativeConnectorInput(string Group, bool Send, bool Acknowledge, int? Priority, int? Slot);
internal sealed record NativeObjectInput(string Id, string DefinitionId, int Number, string? Device, string? Channel,
    IReadOnlyList<string> Links, string? Flags, int? Bits, string? Dpt, bool? DisplayActive, string? Sender,
    IReadOnlyList<NativeConnectorInput> Connectors);
internal sealed record NativeGroupInput(string Id, ushort Address, string? Dpt, uint Puid, bool Secure);
internal sealed record NativeObjectOverride(string RefId, string? Priority, string? Communication, string? Read, string? Write,
    string? Transmit, string? Update, string? ReadOnInit, IReadOnlyList<string> Acknowledges, string Hash);

/// <summary>Detached current model facts and retained native overrides, captured without projecting or changing XML.</summary>
internal sealed class NativeCommunicationInputs
{
    internal IReadOnlyDictionary<string, string?> Values { get; }
    internal IReadOnlyList<NativeObjectInput> Objects { get; }
    internal IReadOnlyDictionary<string, NativeGroupInput> Groups { get; }
    internal IReadOnlyDictionary<string, NativeObjectOverride> Overrides { get; }
    internal string Medium { get; }
    internal string Fingerprint { get; }
    private NativeCommunicationInputs(Dictionary<string, string?> values, NativeObjectInput[] objects,
        Dictionary<string, NativeGroupInput> groups, Dictionary<string, NativeObjectOverride> overrides, string medium)
    {
        Values = values.ToFrozenDictionary(StringComparer.Ordinal); Objects = Array.AsReadOnly(objects);
        Groups = groups.ToFrozenDictionary(StringComparer.Ordinal); Overrides = overrides.ToFrozenDictionary(StringComparer.Ordinal); Medium = medium;
        Fingerprint = NativeCommunicationGuard.Hash(new
        {
            version = 1, medium, values = values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), objects,
            groups = groups.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
            overrides = overrides.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
        });
    }

    internal static NativeCommunicationInputs Capture(Project project, DeviceInstance device)
    {
        const int limit = 100000;
        long characters = 0;
        string? Text(string? value, int maximum = 256)
        {
            if (value is not null && (value.Length > maximum || (characters += value.Length) > 4 * 1024 * 1024)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            return value;
        }
        string Id(string? value)
        {
            if (!NativeCommunicationGuard.Safe(value)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidRequest);
            return Text(value)!;
        }
        var devices = ProjectQuery.Devices(project).Take(limit + 1).ToArray();
        if (devices.Length > limit) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        var matches = devices.Where(d => d.Device.Id == device.Id).Take(2).ToArray();
        if (matches.Length != 1 || !ReferenceEquals(matches[0].Device, device)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidRequest);
        if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnaccountedBinaryData);
        if (device.ParameterValues.Count > limit || device.ComObjectRefs.Count > limit || project.Workbench.Objects.Count > 200000 || project.Workbench.Groups.Count > 65536)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in device.ParameterValues) values.Add(Id(pair.Key), Text(pair.Value, 65536));

        var nativeNodes = project.OriginalXml?.Descendants().Take(1000001).ToArray() ?? [];
        if (nativeNodes.Length > 1000000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        var nativeDevices = nativeNodes.Where(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == device.Id).Take(2).ToArray();
        if (nativeDevices.Length > 1) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidNativeOverride, device.Id);
        var overrides = new Dictionary<string, NativeObjectOverride>(StringComparer.Ordinal);
        if (nativeDevices.Length == 1)
        {
            var native = nativeDevices[0]; var ns = native.Name.Namespace;
            var containers = native.Elements().Where(e => e.Name.LocalName == "ComObjectInstanceRefs").Take(2).ToArray();
            if (containers.Length > 1 || containers.Any(e => e.Name != ns + "ComObjectInstanceRefs")) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidNativeOverride);
            foreach (var row in containers.SelectMany(e => e.Elements()))
            {
                if (overrides.Count >= limit) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
                if (row.Name != ns + "ComObjectInstanceRef" || row.HasElements) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidNativeOverride);
                foreach (var attribute in row.Attributes().Where(a => !a.IsNamespaceDeclaration))
                { Text(attribute.Name.ToString(), 512); Text(attribute.Value, attribute.Name.LocalName is "Links" or "Acknowledges" ? 4 * 1024 * 1024 : 65536); }
                var id = Id((string?)row.Attribute("RefId"));
                string? Attr(string name) => Text((string?)row.Attribute(name));
                var ackText = Text((string?)row.Attribute("Acknowledges"), 4 * 1024 * 1024) ?? "";
                var acks = ackText.Split([' ', '\t', '\r', '\n'], 65536, StringSplitOptions.RemoveEmptyEntries);
                if (acks.Length > 65535 || acks.Distinct(StringComparer.Ordinal).Count() != acks.Length) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidNativeOverride, id);
                foreach (var ack in acks) Id(ack);
                var raw = new NativeObjectOverride(id, Attr("Priority"), Attr("CommunicationFlag"), Attr("ReadFlag"), Attr("WriteFlag"),
                    Attr("TransmitFlag"), Attr("UpdateFlag"), Attr("ReadOnInitFlag"), Array.AsReadOnly(acks),
                    NativeCommunicationGuard.Hash(new { id, attributes = row.Attributes().Where(a => !a.IsNamespaceDeclaration)
                        .OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new { name = a.Name.ToString(), a.Value }).ToArray() }));
                if (!overrides.TryAdd(id, raw)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, id);
            }
        }
        var groupSource = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var group in nativeNodes.Where(e => e.Name.LocalName == "GroupAddress"))
        {
            var id = (string?)group.Attribute("Id"); if (!NativeCommunicationGuard.Safe(id)) continue;
            var secure = group.Attribute("Key") is not null;
            foreach (var node in group.AncestorsAndSelf().Where(e => e.Name.LocalName is "GroupAddress" or "GroupRange"))
            {
                var mode = (string?)node.Attribute("Security");
                if (mode is null or "Auto") continue;
                if (mode is not ("On" or "Off")) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidNativeOverride, id);
                secure |= mode == "On"; break;
            }
            if (!groupSource.TryAdd(id!, secure)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.AmbiguousGroupAddress, id);
        }
        var groups = new Dictionary<string, NativeGroupInput>(StringComparer.Ordinal);
        var ranges = new Stack<(GroupRange Range, int Depth)>(); var seen = new HashSet<GroupRange>(ReferenceEqualityComparer.Instance);
        foreach (var installation in project.Installations)
            foreach (var range in installation.MainRanges)
            { if (ranges.Count >= 65536) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded); ranges.Push((range, 1)); }
        while (ranges.TryPop(out var entry))
        {
            if (entry.Depth > 128 || !seen.Add(entry.Range) || seen.Count > 65536) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            foreach (var group in entry.Range.Addresses)
            {
                if (groups.Count >= 65535) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
                var id = Id(group.Id);
                if (group.Address.Raw == 0) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownGroupAddress, id);
                var secure = groupSource.GetValueOrDefault(id) || project.Workbench.Groups.GetValueOrDefault(id)?.Secure == true;
                if (!groups.TryAdd(id, new(id, group.Address.Raw, Text(group.DatapointType, 64), group.Puid, secure)))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.AmbiguousGroupAddress, id);
            }
            foreach (var child in entry.Range.Ranges)
            { if (ranges.Count >= 65536) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded); ranges.Push((child, entry.Depth + 1)); }
        }
        var objects = new List<NativeObjectInput>(device.ComObjectRefs.Count); long connectors = 0;
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in device.ComObjectRefs)
        {
            var id = Id(obj.Id); if (!objectIds.Add(id)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, id);
            var definition = Id(obj.ComObjectId);
            if (obj.Links.Count > 65535 || (connectors += obj.Links.Count) > 100000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            var links = obj.Links.Select(Id).ToArray();
            if (links.Distinct(StringComparer.Ordinal).Count() != links.Length) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.DuplicateAssociation, id);
            var metadata = project.Workbench.Objects.GetValueOrDefault(new ObjectKey(device.Id, id).ToString());
            var facts = new List<NativeConnectorInput>();
            if (metadata is not null)
            {
                if (metadata.Connectors is null || metadata.Connectors.Count > 65535 || (connectors += metadata.Connectors.Count) > 200000)
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
                foreach (var pair in metadata.Connectors.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (pair.Value is null) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.AmbiguousConnectorOverride, id);
                    facts.Add(new(Id(pair.Key), pair.Value.Send, pair.Value.Acknowledge, pair.Value.Priority, pair.Value.Slot));
                }
            }
            objects.Add(new(id, definition, obj.Number, Text(obj.Device), Text(obj.ChannelId), Array.AsReadOnly(links),
                Text(metadata?.Flags, 64), metadata?.Bits, Text(metadata?.Dpt, 64), metadata?.Active,
                Text(metadata?.SendingGroupId), Array.AsReadOnly(facts.ToArray())));
        }
        var location = matches[0];
        return new(values, objects.ToArray(), groups, overrides, location.Segment?.MediumTypeRefId ?? location.Line?.MediumTypeRefId ?? "MT-0");
    }
}
