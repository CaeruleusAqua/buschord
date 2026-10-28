using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Knx.Workbench;

public enum MassLinkReviewState { Ready, Ambiguous, Incompatible, Unmatched, Unverified }
public sealed record MassLinkTarget(string Id, string Address, string Name, string? DatapointType, int? Bits, bool? Secure);
public sealed record MassLinkCandidate(string GroupId, int Score, string Reason, LinkValidation Validation);
public sealed record MassLinkReviewRow(string Id, ObjectKey Key, string DeviceName, string DeviceAddress,
    int ObjectNumber, string ObjectText, string? DatapointType, MassLinkReviewState State,
    string? SelectedGroupId, LinkValidation? Validation, IReadOnlyList<MassLinkCandidate> Candidates)
{
    public bool CanApply => State == MassLinkReviewState.Ready && SelectedGroupId is not null;
}

/// <summary>Bound immutable display rows and private original identities for one review.</summary>
public sealed class MassLinkReviewPlan
{
    public IReadOnlyList<MassLinkReviewRow> Rows { get; internal set; }
    public IReadOnlyList<MassLinkTarget> Targets { get; }
    public bool IsApplied { get; internal set; }
    internal Project Project { get; }
    internal object SessionIdentity { get; }
    internal string Baseline { get; }
    internal IReadOnlyDictionary<string, ObjectRow> Objects { get; }
    internal IReadOnlyDictionary<string, GroupAddressEntry> Groups { get; }
    internal Dictionary<string, string?> SelectedTargets { get; }

    internal MassLinkReviewPlan(Project project, object sessionIdentity, string baseline,
        IEnumerable<MassLinkReviewRow> rows, IEnumerable<MassLinkTarget> targets,
        Dictionary<string, ObjectRow> objects, Dictionary<string, GroupAddressEntry> groups)
    {
        Project = project; SessionIdentity = sessionIdentity; Baseline = baseline;
        Rows = Array.AsReadOnly(rows.ToArray()); Targets = Array.AsReadOnly(targets.ToArray());
        Objects = new ReadOnlyDictionary<string, ObjectRow>(objects);
        Groups = new ReadOnlyDictionary<string, GroupAddressEntry>(groups);
        SelectedTargets = Rows.ToDictionary(row => row.Id, row => row.SelectedGroupId, StringComparer.Ordinal);
    }
}

/// <summary>Explicit target review followed by one strictly validated atomic link transaction.</summary>
public static class MassLinkReview
{
    public const int MaximumRows = 5000;
    public const int MaximumTargets = 10000;
    public const int MaximumSuggestionsPerRow = 16;
    public const long MaximumComparisons = 2_000_000;

    public static MassLinkReviewPlan Preview(Project project, object sessionIdentity, WorkbenchScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(sessionIdentity);
        var devices = ProjectQuery.DeviceList(project).ToArray();
        var groups = ProjectQuery.GroupAddresses(project).ToArray();
        if (devices.Any(device => string.IsNullOrEmpty(device.Id))
            || devices.GroupBy(device => device.Id, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || devices.Any(device => device.ComObjectRefs.Any(obj => string.IsNullOrEmpty(obj.Id))
                || device.ComObjectRefs.GroupBy(obj => obj.Id, StringComparer.Ordinal).Any(group => group.Count() != 1))
            || groups.Any(group => string.IsNullOrEmpty(group.Id))
            || groups.GroupBy(group => group.Id, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Project identities are missing or ambiguous.");
        var objects = WorkbenchProject.ScopeObjects(project, scope ?? new(IncludeInactive: true))
            .Where(row => row.Object.Links.Count == 0).ToArray();
        if (objects.Length > MaximumRows || groups.Length > MaximumTargets || (long)objects.Length * groups.Length > MaximumComparisons)
            throw new InvalidOperationException("Review exceeds its row/target limit; choose a narrower project scope.");
        var keys = objects.Select(row => row.Key).ToHashSet();
        var proposals = SmartLinking.SuggestForReview(project,
            (device, obj) => WorkbenchProject.Metadata(project, new(device.Id, obj.Id))?.Dpt,
            perObject: MaximumSuggestionsPerRow, objectKeys: keys);
        var checks = WorkbenchProject.CheckLinks(project, proposals.Select(proposal =>
            new LinkCheckRequest(new(proposal.Device.Id, proposal.ComObject.Id), proposal.GroupAddress.Id)));
        var candidates = new Dictionary<ObjectKey, List<MassLinkCandidate>>();
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            var key = new ObjectKey(proposal.Device.Id, proposal.ComObject.Id);
            if (!candidates.TryGetValue(key, out var list)) candidates.Add(key, list = []);
            list.Add(new(proposal.GroupAddress.Id, proposal.Score, proposal.Reason, checks[index].Validation));
        }
        var rows = objects.Select(row =>
        {
            var choices = Array.AsReadOnly(candidates.GetValueOrDefault(row.Key)?.ToArray() ?? []);
            MassLinkCandidate[] top = choices.Count == 0 ? [] : choices.Where(candidate => candidate.Score == choices[0].Score).ToArray();
            var validTop = top.Where(candidate => candidate.Validation.Ok).ToArray();
            var selected = validTop.Length == 1 ? validTop[0] : top.Length == 1 ? top[0] : null;
            var validation = selected?.Validation ?? top.FirstOrDefault(candidate => candidate.Validation.Reason == LinkValidationReason.UnknownCapability)?.Validation
                ?? top.FirstOrDefault()?.Validation;
            var state = choices.Count == 0 ? MassLinkReviewState.Unmatched : validTop.Length > 1
                ? MassLinkReviewState.Ambiguous : validation is null ? MassLinkReviewState.Unmatched : State(validation);
            return new MassLinkReviewRow(row.Key.ToString(), row.Key, row.Device.Name ?? "", row.Device.Address.ToString(),
                row.Object.Number, row.Object.Text ?? row.Object.Id, row.Metadata?.Dpt, state,
                selected?.GroupId, state == MassLinkReviewState.Ambiguous ? null : validation, choices);
        }).ToArray();
        var targets = groups.OrderBy(group => group.Address.Raw).ThenBy(group => group.Id, StringComparer.Ordinal)
            .Select(group => new MassLinkTarget(group.Id, group.Address.ToString(), group.Name, group.DatapointType,
                project.Workbench.Groups.GetValueOrDefault(group.Id)?.Bits, project.Workbench.Groups.GetValueOrDefault(group.Id)?.Secure));
        return new(project, sessionIdentity, Fingerprint(project), rows, targets,
            objects.ToDictionary(row => row.Key.ToString(), StringComparer.Ordinal), groups.ToDictionary(group => group.Id, StringComparer.Ordinal));
    }

    public static void SelectTarget(MassLinkReviewPlan plan, Project project, object sessionIdentity, string rowId, string? groupId)
        => SelectTargets(plan, project, sessionIdentity, [new(rowId, groupId)]);

    public static void SelectTargets(MassLinkReviewPlan plan, Project project, object sessionIdentity,
        IEnumerable<KeyValuePair<string, string?>> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        EnsureCurrent(plan, project, sessionIdentity);
        var requested = selections.ToArray();
        if (requested.Length > MaximumRows || requested.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != requested.Length
            || requested.Any(pair => !plan.Objects.ContainsKey(pair.Key))) throw new ArgumentException("Unknown or duplicate review row.", nameof(selections));
        if (requested.Any(pair => pair.Value is not null && !plan.Groups.ContainsKey(pair.Value)))
            throw new ArgumentException("Unknown review target.", nameof(selections));
        var selected = requested.Where(pair => pair.Value is not null).ToArray();
        var checks = WorkbenchProject.CheckLinks(project, selected.Select(pair => new LinkCheckRequest(plan.Objects[pair.Key].Key, pair.Value!)));
        var validations = selected.Select((pair, index) => (pair.Key, checks[index].Validation)).ToDictionary(pair => pair.Key, pair => pair.Validation, StringComparer.Ordinal);
        var changes = requested.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in requested) plan.SelectedTargets[pair.Key] = pair.Value;
        plan.Rows = Array.AsReadOnly(plan.Rows.Select(row => !changes.TryGetValue(row.Id, out var groupId) ? row : row with
        {
            SelectedGroupId = groupId,
            Validation = validations.GetValueOrDefault(row.Id),
            State = validations.GetValueOrDefault(row.Id) is { } validation ? State(validation) : MassLinkReviewState.Unmatched,
        }).ToArray());
    }

    public static MutationResult Apply(MassLinkReviewPlan plan, Project project, object sessionIdentity,
        UndoStack undo, IEnumerable<string>? selectedRowIds = null)
    {
        ArgumentNullException.ThrowIfNull(undo);
        EnsureCurrent(plan, project, sessionIdentity);
        var selected = (selectedRowIds ?? plan.Rows.Where(row => row.CanApply).Select(row => row.Id)).ToArray();
        if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length
            || selected.Any(id => !plan.Objects.ContainsKey(id))) throw new ArgumentException("Unknown or duplicate review selection.", nameof(selectedRowIds));
        if (selected.Length == 0) return new(true, 0, [], []);
        var freshObjects = WorkbenchProject.ScopeObjects(project, new(IncludeInactive: true)).ToDictionary(row => row.Key);
        var freshGroups = ProjectQuery.GroupAddresses(project).ToDictionary(group => group.Id, StringComparer.Ordinal);
        var requests = new List<LinkCheckRequest>(selected.Length);
        foreach (var id in selected)
        {
            var original = plan.Objects[id];
            var groupId = plan.SelectedTargets[id] ?? throw new InvalidOperationException("Selected row has no reviewed target.");
            if (!freshObjects.TryGetValue(original.Key, out var row)
                || !ReferenceEquals(row.Device, original.Device) || !ReferenceEquals(row.Object, original.Object)
                || row.Object.Links.Count != 0 || !freshGroups.TryGetValue(groupId, out var group)
                || !ReferenceEquals(group, plan.Groups[groupId])) throw new InvalidOperationException("The reviewed identities changed; refresh the review.");
            requests.Add(new(original.Key, groupId));
        }
        // The core validates all requests before mutation and includes undo in its rollback boundary.
        var result = WorkbenchProject.AddLinkPairs(project, requests, undo);
        if (result.Ok) plan.IsApplied = true;
        return result;
    }

    private static MassLinkReviewState State(LinkValidation validation) => validation.Reason switch
    {
        LinkValidationReason.None => MassLinkReviewState.Ready,
        LinkValidationReason.UnknownCapability => MassLinkReviewState.Unverified,
        _ => MassLinkReviewState.Incompatible,
    };
    private static void EnsureCurrent(MassLinkReviewPlan plan, Project project, object sessionIdentity)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(sessionIdentity);
        if (plan.IsApplied || !ReferenceEquals(plan.Project, project) || !ReferenceEquals(plan.SessionIdentity, sessionIdentity)
            || plan.Baseline != Fingerprint(project)) throw new InvalidOperationException("The review is stale; refresh it before applying.");
    }
    private static string Fingerprint(Project project) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting) + JsonSerializer.Serialize(project.Hardware))));
}
