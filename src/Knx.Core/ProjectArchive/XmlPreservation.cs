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
        {
            var children = installation.Elements().ToList();
            var slots = Enumerable.Range(0, children.Count).Where(i => Rank(children[i].Name.LocalName) >= 0).ToList();
            var ordered = slots.Select(i => children[i]).OrderBy(e => Rank(e.Name.LocalName)).ToList();
            for (var slot = 0; slot < slots.Count; slot++)
            {
                var current = installation.Elements().ElementAt(slots[slot]);
                if (ReferenceEquals(current, ordered[slot])) continue;
                ordered[slot].Remove();
                current.AddBeforeSelf(ordered[slot]);
            }
        }

        static int Rank(string name) => Array.IndexOf(InstallationOrder, name);
    }

    private static void IndexOriginals(XElement raw, XElement before, List<(XElement Raw, XElement Before)> originals)
    {
        if (before.Attribute("Id") is not null || before.Attribute("RefId") is not null || before.Attribute("InstallationId") is not null)
            originals.Add((raw, before));
        foreach (var children in before.Elements().GroupBy(e => e.Name))
        {
            var rawChildren = raw.Elements(children.Key).ToList();
            if (rawChildren.Count != children.Count()) continue;
            foreach (var pair in rawChildren.Zip(children)) IndexOriginals(pair.First, pair.Second, originals);
        }
    }

    private static string Key(XElement element) => element.Name + "|" +
        (string?)(element.Attribute("Id") ?? element.Attribute("InstallationId") ?? element.Attribute("RefId") ?? element.Attribute("Address"));

    private static void MergeElement(XElement target, XElement before, XElement after,
        ILookup<string, (XElement Raw, XElement Before)> originals)
    {
        // Segment@Address was BusChord's pre-B6 spelling; ETS uses Segment@Number. Keep an archive
        // that still carries the old attribute from ending up with two segment numbers.
        if (after.Name.LocalName == "Segment" && after.Attribute("Number") is not null &&
            target.Attribute("Address") is not null)
            target.SetAttributeValue("Address", null);

        foreach (var name in before.Attributes().Concat(after.Attributes()).Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name).Distinct())
            if ((string?)before.Attribute(name) != (string?)after.Attribute(name) || target.Attribute(name) is null)
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
            foreach (var removed in oldChildren.Where(e => !newByKey.Contains(Key(e)) && IsContainer(e.Name.LocalName)))
            {
                var raw = rawByKey[Key(removed)];
                MergeElement(raw, removed, new XElement(name), originals);
                replacements.Add(raw);
            }
            var marker = new XComment("merge-position");
            rawChildren[0].AddBeforeSelf(marker);
            foreach (var raw in rawChildren) raw.Remove();
            marker.AddBeforeSelf(replacements);
            marker.Remove();
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
