using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Knx;

/// <summary>Which part of the project a search or replace looks at.</summary>
public enum SearchScope { All, Devices, GroupAddresses, Topology, CommunicationObjects, Buildings }

/// <summary>One search request: plain text or a regular expression, optional case folding, scope and limit.</summary>
public sealed class SearchQuery
{
    public string Text { get; init; } = "";
    public SearchScope Scope { get; init; } = SearchScope.All;
    public bool Regex { get; init; }
    public bool CaseSensitive { get; init; }
    /// <summary>Maximum hits returned; 0 uses <see cref="ProjectSearch.DefaultLimit"/>.</summary>
    public int Limit { get; init; } = ProjectSearch.DefaultLimit;
}

/// <summary>A rename-like replacement request. Addresses, datapoint types and references are never rewritten.</summary>
public sealed class ReplaceQuery
{
    public string Text { get; init; } = "";
    public string Replacement { get; init; } = "";
    public SearchScope Scope { get; init; } = SearchScope.All;
    public bool Regex { get; init; }
    public bool CaseSensitive { get; init; }
}

/// <summary>One matched field: where it lives, which field matched and what to show.</summary>
public sealed record SearchHit(string Kind, string Id, string Path, string Field, string Value, string Snippet);

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int Total, bool Truncated);

/// <summary>One field that a replacement would change.</summary>
public sealed record ReplaceChange(string Kind, string Id, string Path, string Field, string Before, string After);

/// <summary>
/// Result of <see cref="ProjectSearch.Preview"/>: every field the replacement would touch, in the order it
/// would be applied. The project is untouched until <see cref="ProjectSearch.Apply"/> is called.
/// </summary>
public sealed record ReplacePreview(IReadOnlyList<ReplaceChange> Changes)
{
    public int FieldCount => Changes.Count;
    public int ItemCount => Changes.Select(change => (change.Kind, change.Id)).Distinct().Count();
    public IReadOnlyList<string> ItemIds =>
        Changes.Select(change => change.Id).Distinct().ToList();
}

public sealed record ReplaceResult(int ItemCount, int FieldCount, IReadOnlyList<string> ChangedIds);

/// <summary>
/// Project-wide ETS-like search and rename replacement. Search covers devices, group addresses, topology
/// nodes, communication objects and (optionally) the building tree; replacement only touches names, comments
/// and object texts. Results are ordered by kind, display path, field and id so repeated calls never shuffle.
/// </summary>
public static class ProjectSearch
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 5000;

    public const string DeviceKind = "device";
    public const string GroupAddressKind = "group_address";
    public const string TopologyKind = "topology";
    public const string CommunicationObjectKind = "communication_object";
    public const string BuildingKind = "building";

    public static SearchResult Search(Project project, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        var (matcher, limit) = Prepare(query);

        var matches = new List<SearchHit>();
        foreach (var target in Targets(project, query.Scope))
        {
            if (target.Value is not { Length: > 0 } value) continue;
            if (!matcher.TryMatch(value, out var index, out var length)) continue;
            matches.Add(new SearchHit(target.Kind, target.Id, target.Path, target.Field, value,
                Snippet(value, index, length)));
        }

        matches.Sort(CompareHits);
        var hits = matches.Count > limit ? matches.GetRange(0, limit) : matches;
        return new SearchResult(hits, matches.Count, matches.Count > hits.Count);
    }

    /// <summary>Computes every rename-like change without touching the project.</summary>
    public static ReplacePreview Preview(Project project, ReplaceQuery query)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        var (matcher, _) = Prepare(new SearchQuery
        {
            Text = query.Text,
            Scope = query.Scope,
            Regex = query.Regex,
            CaseSensitive = query.CaseSensitive,
            Limit = MaxLimit,
        });

        var changes = new List<ReplaceChange>();
        foreach (var target in Targets(project, query.Scope))
        {
            if (target.Set is null || target.Value is not { Length: > 0 } value) continue;
            if (!matcher.TryMatch(value, out _, out _)) continue;
            var after = matcher.Replace(value, query.Replacement);
            if (after == value) continue;
            changes.Add(new ReplaceChange(target.Kind, target.Id, target.Path, target.Field, value, after));
        }

        changes.Sort(CompareChanges);
        return new ReplacePreview(changes);
    }

    /// <summary>
    /// Applies a preview. Every target is validated against the project before the first mutation, so a stale
    /// preview fails without leaving a partially renamed project behind. The caller owns snapshots/undo.
    /// </summary>
    public static ReplaceResult Apply(Project project, ReplacePreview preview)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preview);

        var writable = Targets(project, SearchScope.All).Where(target => target.Set is not null)
            .GroupBy(target => (target.Kind, target.Id, target.Field))
            .ToDictionary(group => group.Key, group => group.First());
        var resolved = new List<(Target Target, ReplaceChange Change)>(preview.Changes.Count);
        var seen = new HashSet<(string Kind, string Id, string Field)>();
        foreach (var change in preview.Changes)
        {
            if (!seen.Add((change.Kind, change.Id, change.Field)))
                throw new InvalidOperationException(
                    $"replace preview lists {change.Kind} {change.Id} field '{change.Field}' twice");
            if (!writable.TryGetValue((change.Kind, change.Id, change.Field), out var target))
                throw new InvalidOperationException(
                    $"replace target {change.Kind} {change.Id} field '{change.Field}' no longer exists");
            if (target.Value != change.Before)
                throw new InvalidOperationException(
                    $"replace target {change.Kind} {change.Id} field '{change.Field}' changed after the preview");
            resolved.Add((target, change));
        }

        foreach (var (target, change) in resolved) target.Set!(change.After);
        var ids = resolved.Select(entry => entry.Change.Id).Distinct().ToList();
        return new ReplaceResult(ids.Count, resolved.Count, ids);
    }

    /// <summary>Parses the scope names used by tools and the UI ("all", "devices", "group_addresses", ...).</summary>
    public static bool TryParseScope(string? value, out SearchScope scope)
    {
        switch ((value ?? "all").Trim().ToLowerInvariant().Replace('-', '_'))
        {
            case "all" or "": scope = SearchScope.All; return true;
            case "devices" or "device": scope = SearchScope.Devices; return true;
            case "group_addresses" or "group_address" or "groupaddresses" or "gas":
                scope = SearchScope.GroupAddresses; return true;
            case "topology" or "topology_nodes": scope = SearchScope.Topology; return true;
            case "communication_objects" or "communication_object" or "com_objects" or "comobjects":
                scope = SearchScope.CommunicationObjects; return true;
            case "buildings" or "building": scope = SearchScope.Buildings; return true;
            default: scope = SearchScope.All; return false;
        }
    }

    public static string ScopeName(SearchScope scope) => scope switch
    {
        SearchScope.Devices => "devices",
        SearchScope.GroupAddresses => "group_addresses",
        SearchScope.Topology => "topology",
        SearchScope.CommunicationObjects => "communication_objects",
        SearchScope.Buildings => "buildings",
        _ => "all",
    };

    // ---------------------------------------------------------------- targets

    private sealed record Target(string Kind, string Id, string Path, string Field, string? Value, Action<string>? Set);

    private static IEnumerable<Target> Targets(Project project, SearchScope scope)
    {
        var all = scope == SearchScope.All;
        if (all || scope == SearchScope.Devices)
        {
            foreach (var (_, _, _, _, device) in ProjectQuery.Devices(project))
            {
                var path = Join(device.Address.ToString(), device.Name);
                yield return new(DeviceKind, device.Id, path, "name", device.Name, value => device.Name = value);
                yield return new(DeviceKind, device.Id, path, "address", device.Address.ToString(), null);
                yield return new(DeviceKind, device.Id, path, "comment", device.Comment, value => device.Comment = value);
                yield return new(DeviceKind, device.Id, path, "product_ref", device.ProductRefId, null);
            }
        }

        if (all || scope == SearchScope.GroupAddresses)
        {
            foreach (var entry in ProjectQuery.GroupAddresses(project))
            {
                var path = Join(entry.Address.ToString(), entry.Name);
                yield return new(GroupAddressKind, entry.Id, path, "name", entry.Name, value => entry.Name = value);
                yield return new(GroupAddressKind, entry.Id, path, "address", entry.Address.ToString(), null);
                yield return new(GroupAddressKind, entry.Id, path, "datapoint_type", entry.DatapointType, null);
                yield return new(GroupAddressKind, entry.Id, path, "comment", entry.Comment, value => entry.Comment = value);
            }
        }

        if (all || scope == SearchScope.Topology)
        {
            foreach (var installation in project.Installations)
            {
                var installationPath = Join(installation.Name);
                yield return new(TopologyKind, installation.Id, PathOrId(installationPath, installation.Id), "name",
                    installation.Name, value => installation.Name = value);
                foreach (var area in installation.Areas)
                {
                    var areaPath = Join(installation.Name, area.Name);
                    yield return new(TopologyKind, area.Id, PathOrId(areaPath, area.Id), "name", area.Name,
                        value => area.Name = value);
                    foreach (var line in area.Lines)
                    {
                        var linePath = Join(installation.Name, area.Name, line.Name);
                        yield return new(TopologyKind, line.Id, PathOrId(linePath, line.Id), "name", line.Name,
                            value => line.Name = value);
                        foreach (var segment in line.Segments)
                            yield return new(TopologyKind, segment.Id,
                                PathOrId(Join(installation.Name, area.Name, line.Name, segment.Name), segment.Id),
                                "name", segment.Name, value => segment.Name = value);
                    }
                }
            }
        }

        if (all || scope == SearchScope.CommunicationObjects)
        {
            foreach (var (device, reference) in ProjectQuery.ComObjectRefs(project))
            {
                var path = Join(device.Address.ToString(), device.Name, "KO " + reference.Number.ToString(CultureInfo.InvariantCulture));
                yield return new(CommunicationObjectKind, reference.Id, path, "text", reference.Text,
                    value => reference.Text = value);
                yield return new(CommunicationObjectKind, reference.Id, path, "function_text", reference.FunctionText,
                    value => reference.FunctionText = value);
                yield return new(CommunicationObjectKind, reference.Id, path, "number",
                    reference.Number.ToString(CultureInfo.InvariantCulture), null);
            }
        }

        if (all || scope == SearchScope.Buildings)
        {
            foreach (var target in BuildingTargets(project)) yield return target;
        }
    }

    private static string Join(params string?[] parts)
        => string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string PathOrId(string path, string id) => path.Length > 0 ? path : id;

    // ---------------------------------------------------------------- building tree (optional model)

    /// <summary>
    /// Duck-typed bridge to the building model, which is not part of this assembly yet. When a later model adds
    /// <c>Project.Buildings</c>, its nodes are picked up without a code change here: nodes expose Id, Name and
    /// Comment, children hang off BuildingParts, Spaces, Children, Parts or Nodes. Without such a property the
    /// building scope is simply empty.
    /// </summary>
    private static IEnumerable<Target> BuildingTargets(Project project)
    {
        var queue = new Queue<(object Node, string Path)>();
        foreach (var root in BuildingRoots(project))
            queue.Enqueue((root, NodeString(root, "Name") ?? NodeId(root) ?? root.GetType().Name));

        while (queue.Count > 0)
        {
            var (node, path) = queue.Dequeue();
            var id = NodeId(node);
            if (id is not null)
            {
                var name = NodeString(node, "Name");
                var comment = NodeString(node, "Comment");
                if (name is { Length: > 0 })
                    yield return new(BuildingKind, id, path, "name", name, NodeSetter(node, "Name"));
                if (comment is { Length: > 0 })
                    yield return new(BuildingKind, id, path, "comment", comment, NodeSetter(node, "Comment"));
            }

            foreach (var child in NodeChildren(node))
            {
                var name = NodeString(child, "Name");
                queue.Enqueue((child, name is null ? path : path + " / " + name));
            }
        }
    }

    private static IEnumerable<object> BuildingRoots(Project project)
    {
        var property = project.GetType().GetProperty("Buildings", BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(project) is not IEnumerable nodes) yield break;
        foreach (var node in nodes)
            if (node is not null)
                yield return node;
    }

    private static IEnumerable<object> NodeChildren(object node)
    {
        string[] names = ["BuildingParts", "Spaces", "Children", "Parts", "Nodes"];
        foreach (var name in names)
        {
            var property = node.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(node) is not IEnumerable children) continue;
            foreach (var child in children)
                if (child is not null)
                    yield return child;
        }
    }

    private static string? NodeId(object node)
    {
        var property = node.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var value = property?.GetValue(node);
        return value switch
        {
            null => null,
            string text => text,
            _ => value.ToString(),
        };
    }

    private static string? NodeString(object node, string name)
        => node.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(node) as string;

    private static Action<string>? NodeSetter(object node, string name)
    {
        var property = node.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(string) || property.SetMethod is null) return null;
        return value => property.SetValue(node, value);
    }

    // ---------------------------------------------------------------- matching

    private static (Matcher Matcher, int Limit) Prepare(SearchQuery query)
    {
        var text = query.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("query must not be empty", nameof(query));
        var limit = query.Limit <= 0 ? DefaultLimit : Math.Min(query.Limit, MaxLimit);
        return (new Matcher(text, query.Regex, query.CaseSensitive), limit);
    }

    private sealed class Matcher
    {
        private readonly string _text;
        private readonly StringComparison _comparison;
        private readonly Regex? _regex;

        public Matcher(string text, bool regex, bool caseSensitive)
        {
            _text = text;
            _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!regex) return;

            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            try
            {
                _regex = new Regex(text, options, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"invalid regular expression '{text}': {ex.Message}", nameof(text), ex);
            }
        }

        public bool TryMatch(string value, out int index, out int length)
        {
            if (_regex is null)
            {
                index = value.IndexOf(_text, _comparison);
                length = _text.Length;
                return index >= 0;
            }

            var match = _regex.Match(value);
            index = match.Success ? match.Index : 0;
            length = match.Success ? match.Length : 0;
            return match.Success;
        }

        /// <summary>Replacement is literal in both modes; regex captures are not expanded.</summary>
        public string Replace(string value, string replacement)
        {
            if (_regex is not null)
                return _regex.Replace(value, _ => replacement);

            var builder = new StringBuilder();
            var start = 0;
            while (start <= value.Length)
            {
                var index = value.IndexOf(_text, start, _comparison);
                if (index < 0) break;
                builder.Append(value, start, index - start).Append(replacement);
                start = index + _text.Length;
            }
            builder.Append(value, start, value.Length - start);
            return builder.ToString();
        }
    }

    private static string Snippet(string value, int index, int length)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length <= 120) return text;
        var start = Math.Max(0, index - 24);
        var end = Math.Min(text.Length, index + length + 24);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    private static int CompareHits(SearchHit left, SearchHit right)
    {
        var order = KindRank(left.Kind).CompareTo(KindRank(right.Kind));
        if (order != 0) return order;
        order = string.CompareOrdinal(left.Path, right.Path);
        if (order != 0) return order;
        order = string.CompareOrdinal(left.Field, right.Field);
        if (order != 0) return order;
        order = string.CompareOrdinal(left.Id, right.Id);
        return order != 0 ? order : string.CompareOrdinal(left.Value, right.Value);
    }

    private static int CompareChanges(ReplaceChange left, ReplaceChange right)
    {
        var order = KindRank(left.Kind).CompareTo(KindRank(right.Kind));
        if (order != 0) return order;
        order = string.CompareOrdinal(left.Path, right.Path);
        if (order != 0) return order;
        order = string.CompareOrdinal(left.Field, right.Field);
        return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
    }

    private static int KindRank(string kind) => kind switch
    {
        DeviceKind => 0,
        GroupAddressKind => 1,
        TopologyKind => 2,
        CommunicationObjectKind => 3,
        BuildingKind => 4,
        _ => 5,
    };
}
