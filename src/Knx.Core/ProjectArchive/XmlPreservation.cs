using System.Xml.Linq;

namespace Knx;

internal static class XmlPreservation
{
    // Apply modeled changes to the original tree, retaining unmodeled attributes and nodes.
    internal static XDocument Merge(XDocument original, XDocument baseline, XDocument current)
    {
        var result = new XDocument(original);
        var originals = new List<(XElement Raw, XElement Before)>();
        IndexOriginals(original.Root!, baseline.Root!, originals);
        MergeElement(result.Root!, baseline.Root!, current.Root!, originals.ToLookup(p => Key(p.Before)));
        OrderInstallationChildren(result.Root!);
        if (ProjectFormats.IsNative(result.Root!.Name.Namespace))
            foreach (var device in result.Descendants().Where(e => e.Name.LocalName == "DeviceInstance" && e.Name.Namespace == result.Root.Name.Namespace))
                OrderKnownChildren(device, ["ParameterInstanceRefs", "ComObjectInstanceRefs", "ChannelInstances", "ModuleInstances", "GroupObjectTree", "AdditionalAddresses", "BinaryData", "IPConfig", "Security", "BusInterfaces", "RfFastAckSlots"]);
        return result;
    }

    // Schema sequence of the Installation children (knx_project.xsd). The merge appends a newly
    // modeled container that the archive did not have yet, which would place <Locations> after
    // <GroupAddresses>. Known children are re-seated into the slots they occupy, so unmodeled
    // element names keep their positions.
    private static readonly string[] InstallationOrder =
        ["Topology", "Locations", "GroupAddresses", "P2PLinks", "Trades", "SplitInfos"];

    private static void OrderInstallationChildren(XElement root)
    {
        foreach (var installation in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Installation"))
            OrderKnownChildren(installation, InstallationOrder);
    }

    private static void OrderKnownChildren(XElement parent, IReadOnlyList<string> order)
    {
        var children = parent.Elements().ToList();
        var ranks = order.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        int Rank(XElement child) => child.Name.Namespace == parent.Name.Namespace ? ranks.GetValueOrDefault(child.Name.LocalName, -1) : -1;
        var slots = Enumerable.Range(0, children.Count).Where(i => Rank(children[i]) >= 0).ToList();
        var ordered = slots.Select(i => children[i]).OrderBy(Rank).ToList();
        for (var slot = 0; slot < slots.Count; slot++)
        {
            var current = parent.Elements().ElementAt(slots[slot]);
            if (ReferenceEquals(current, ordered[slot])) continue;
            ordered[slot].Remove();
            current.AddBeforeSelf(ordered[slot]);
        }
    }

    private static void IndexOriginals(XElement raw, XElement before, List<(XElement Raw, XElement Before)> originals)
    {
        if (before.Name.LocalName != "BinaryData"
            && (before.Attribute("Id") is not null || before.Attribute("RefId") is not null || before.Attribute("InstallationId") is not null))
            originals.Add((raw, before));
        foreach (var children in before.Elements().GroupBy(e => e.Name))
        {
            var rawChildren = raw.Elements(children.Key).ToList();
            if (rawChildren.Count != children.Count()) continue;
            foreach (var pair in rawChildren.Zip(children)) IndexOriginals(pair.First, pair.Second, originals);
        }
    }

    private static string Key(XElement element) => element.Name + "|" +
        (string?)(element.Name.LocalName == "BinaryData" && element.Parent?.Name.LocalName == "BinaryData"
            ? element.Attribute("Name") : element.Attribute("Id") ?? element.Attribute("InstallationId") ?? element.Attribute("RefId") ?? element.Attribute("Address"));

    private static void MergeElement(XElement target, XElement before, XElement after,
        ILookup<string, (XElement Raw, XElement Before)> originals)
    {
        if (after.Name.LocalName == "Data" && after.Parent?.Name.LocalName == "BinaryData"
            && after.Parent.Parent?.Name.LocalName == "BinaryData"
            && after.Parent.Parent.Parent?.Name.LocalName == "DeviceInstance" && before.Value != after.Value)
        {
            if (target.HasElements) throw new InvalidDataException("Cannot replace opaque binary data content.");
            target.Nodes().OfType<XText>().Remove();
            target.AddFirst(new XText(after.Value));
        }
        // Remove a legacy Address attribute when the segment already has Number.
        if (after.Name.LocalName == "Segment" && after.Attribute("Number") is not null &&
            target.Attribute("Address") is not null)
        {
            target.SetAttributeValue("Address", null);
            target.SetAttributeValue("Number", (string?)after.Attribute("Number"));
        }

        foreach (var name in before.Attributes().Concat(after.Attributes()).Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name).Distinct())
            if ((string?)before.Attribute(name) != (string?)after.Attribute(name))
                target.SetAttributeValue(name, (string?)after.Attribute(name));

        foreach (var name in before.Elements().Concat(after.Elements()).Select(e => e.Name).Distinct())
        {
            var oldChildren = before.Elements(name).ToList();
            var newChildren = after.Elements(name).ToList();
            var rawChildren = target.Elements(name).ToList();
            var oldByKey = oldChildren.ToLookup(Key);
            var newByKey = newChildren.ToLookup(Key);
            if (oldByKey.Any(g => g.Count() > 1) || newByKey.Any(g => g.Count() > 1))
                throw new InvalidDataException($"Cannot safely save ambiguous duplicate XML identities in {name}.");
            if (rawChildren.Count == 0)
            {
                // Reader defaults are modeled in both snapshots but absent from native XML. Do not materialize them on a no-op save.
                if (oldChildren.Count != newChildren.Count || oldChildren.Zip(newChildren).Any(p => !XNode.DeepEquals(p.First, p.Second)))
                    target.Add(newChildren.Select(CopyAdded));
                continue;
            }
            if (rawChildren.Count != oldChildren.Count && oldChildren.Count != 0)
                throw new InvalidDataException($"Cannot safely reconcile XML content in {name}.");
            if (oldChildren.Count == 0)
            {
                // A previously empty modeled container may still contain vendor-only data.
                if (rawChildren.Count == 1 && newChildren.Count == 1)
                    MergeElement(rawChildren[0], new XElement(name), newChildren[0], originals);
                else if (newChildren.Count > 0)
                    throw new InvalidDataException($"Cannot safely add conflicting XML content in {name}.");
                continue;
            }

            var rawByKey = oldChildren.Zip(rawChildren).ToDictionary(p => Key(p.First), p => p.Second);
            var replacements = new List<XElement>();
            foreach (var child in newChildren)
            {
                if (rawByKey.TryGetValue(Key(child), out var raw))
                {
                    MergeElement(raw, oldByKey[Key(child)].Single(), child, originals);
                    replacements.Add(raw);
                }
                else replacements.Add(CopyAdded(child));
            }
            // Deleting a modeled object also deletes its payload. Empty collection containers,
            // however, may still own vendor attributes/children and must be retained.
            foreach (var removed in oldChildren.Where(e => !newByKey.Contains(Key(e)) && (IsContainer(e.Name.LocalName)
                || e.Name.LocalName == "BinaryData" && e.Parent?.Name.LocalName == "DeviceInstance")))
            {
                var raw = rawByKey[Key(removed)];
                MergeElement(raw, removed, new XElement(name), originals);
                replacements.Add(raw);
            }
            ReplaceModeledChildren(rawChildren, replacements);
        }

        XElement CopyAdded(XElement child)
        {
            var matches = originals[Key(child)].ToList();
            if (matches.Count == 0)
                return new XElement(child.Name, child.Attributes(), child.Nodes().Select(n => n is XElement e ? CopyAdded(e) : n));
            if (matches.Count > 1) throw new InvalidDataException($"Cannot safely relocate ambiguous XML identity {Key(child)}.");
            var copy = new XElement(matches[0].Raw);
            MergeElement(copy, matches[0].Before, child, originals);
            return copy;
        }
    }

    private static void ReplaceModeledChildren(IReadOnlyList<XElement> existing, IReadOnlyList<XElement> replacements)
    {
        var markers = existing.ToDictionary(e => e, e =>
        {
            var marker = new XComment("merge-position");
            e.AddBeforeSelf(marker); e.Remove(); return marker;
        });
        var surviving = existing.Where(replacements.Contains).ToArray();
        var reordered = !surviving.SequenceEqual(replacements.Where(markers.ContainsKey));
        XElement? previous = null;
        for (var i = 0; i < replacements.Count; i++)
        {
            var replacement = replacements[i];
            if (reordered && i < existing.Count) markers[existing[i]].ReplaceWith(replacement);
            else if (!reordered && markers.TryGetValue(replacement, out var ownSlot)) ownSlot.ReplaceWith(replacement);
            else
            {
                var next = reordered ? null : replacements.Skip(i + 1).FirstOrDefault(markers.ContainsKey);
                if (next is not null) markers[next].AddBeforeSelf(replacement);
                else if (previous is not null) previous.AddAfterSelf(replacement);
                else markers[existing[0]].AddBeforeSelf(replacement);
            }
            previous = replacement;
        }
        foreach (var marker in markers.Values) if (marker.Parent is not null) marker.Remove();
    }

    private static bool IsContainer(string name) => name is "Installations" or "Topology" or "GroupAddresses" or
        "GroupRanges" or "ComObjectInstanceRefs" or "Channels" or "ParameterInstanceRefs";

    /// <summary>
    /// Rejects duplicate modeled identities anywhere in the tree, including newly added
    /// subtrees that the merge copied before reconciling them.
    /// </summary>
    internal static void ValidateIdentities(XElement root)
    {
        foreach (var parent in root.DescendantsAndSelf())
            foreach (var group in parent.Elements()
                         .Where(e => e.Attribute("Id") is not null)
                         .GroupBy(e => e.Name + "|" + (string?)e.Attribute("Id")))
                if (group.Count() > 1)
                    throw new InvalidDataException(
                        $"Cannot safely save ambiguous duplicate XML identities in {group.First().Name}: {group.Key}.");
    }
}
