using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Knx.App.Localization;
using Knx.Workbench;

namespace Knx.App.Services;

public sealed record SmartLinkReviewItem(string Id, string DeviceId, string DeviceName, string DeviceAddress,
    string ObjectId, int ObjectNumber, string ObjectText, string GroupId, string GroupAddress, string GroupName,
    string? DatapointType, int Score, string Reason, string? Error)
{ public bool CanApply => Error is null; }

/// <summary>Immutable review data. Applying resolves and validates private original identities, never edited rows.</summary>
public sealed class SmartLinkReviewPlan
{
    public IReadOnlyList<SmartLinkReviewItem> Items { get; }
    public bool IsApplied { get; internal set; }
    internal MainWindowViewModel Owner { get; }
    internal object SessionIdentity { get; }
    internal Project Project { get; }
    internal string Baseline { get; }
    internal IReadOnlyDictionary<string, LinkSuggestion> Proposals { get; }
    internal SmartLinkReviewPlan(MainWindowViewModel owner, Project project, string baseline,
        IEnumerable<SmartLinkReviewItem> items, Dictionary<string, LinkSuggestion> proposals)
    {
        Owner = owner; SessionIdentity = owner.ProjectSessionIdentity; Project = project; Baseline = baseline; Items = Array.AsReadOnly(items.ToArray());
        Proposals = new System.Collections.ObjectModel.ReadOnlyDictionary<string, LinkSuggestion>(proposals);
    }
}

public sealed partial class MainWindowViewModel
{
    private SmartLinkReviewPlan? _smartLinkReview;
    partial void OnProjectChanging(Project? value) => _smartLinkReview = null;

    public SmartLinkReviewPlan PreviewSmartLinks()
    {
        _smartLinkReview = null;
        var project = Project ?? throw new InvalidOperationException(Strings.Get("UxNeedsProject"));
        if (ProjectQuery.DeviceList(project).GroupBy(d => d.Id, StringComparer.Ordinal).Any(g => g.Count() != 1)
            || ProjectQuery.DeviceList(project).Any(d => d.ComObjectRefs.GroupBy(o => o.Id, StringComparer.Ordinal).Any(g => g.Count() != 1))
            || ProjectQuery.GroupAddresses(project).GroupBy(g => g.Id, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidOperationException(WorkbenchStrings.Get("SmartAmbiguous"));
        var proposals = new Dictionary<string, LinkSuggestion>(StringComparer.Ordinal);
        var rows = new List<SmartLinkReviewItem>();
        var suggestions = SmartLinking.Suggest(project, DatapointTypeOf, perObject: 1);
        var checks = WorkbenchProject.CheckLinks(project, suggestions.Select(s => new LinkCheckRequest(new(s.Device.Id, s.ComObject.Id), s.GroupAddress.Id)));
        for (var i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            var id = JsonSerializer.Serialize(new[] { suggestion.Device.Id, suggestion.ComObject.Id, suggestion.GroupAddress.Id });
            var validation = checks[i].Validation;
            proposals.Add(id, suggestion);
            rows.Add(new(id, suggestion.Device.Id, suggestion.Device.Name ?? "", suggestion.Device.Address.ToString(),
                suggestion.ComObject.Id, suggestion.ComObject.Number, suggestion.ComObject.Text ?? suggestion.ComObject.Id,
                suggestion.GroupAddress.Id, suggestion.GroupAddress.Address.ToString(), suggestion.GroupAddress.Name,
                suggestion.GroupAddress.DatapointType, suggestion.Score, suggestion.Reason, validation.Ok ? null : SmartReason(validation)));
        }
        var review = new SmartLinkReviewPlan(this, project, SmartFingerprint(project), rows, proposals);
        _smartLinkReview = review;
        Status = WorkbenchStrings.Get("SmartFound", rows.Count, rows.Count(r => r.CanApply));
        return review;
    }

    public int ApplySmartLinks(SmartLinkReviewPlan review, IEnumerable<string>? selectedIds = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (HistoryBlocked?.Invoke() == true) throw new InvalidOperationException(WorkbenchStrings.Get("SmartBusy"));
        if (review.IsApplied || !ReferenceEquals(review.Owner, this) || !ReferenceEquals(_smartLinkReview, review)
            || !ReferenceEquals(ProjectSessionIdentity, review.SessionIdentity)
            || !ReferenceEquals(Project, review.Project) || SmartFingerprint(review.Project) != review.Baseline)
            throw new InvalidOperationException(WorkbenchStrings.Get("SmartStale"));
        var ids = (selectedIds ?? review.Items.Select(r => r.Id)).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !review.Proposals.ContainsKey(id)))
            throw new InvalidOperationException(WorkbenchStrings.Get("SmartUnknownSelection"));
        if (ids.Length == 0) return 0;
        var proposals = ids.Select(id => review.Proposals[id]).ToArray();
        var requests = proposals.Select(p => new LinkCheckRequest(new(p.Device.Id, p.ComObject.Id), p.GroupAddress.Id)).ToArray();
        var checks = WorkbenchProject.CheckLinks(review.Project, requests);
        var devices = ProjectQuery.DeviceList(review.Project).GroupBy(d => d.Id, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var objects = ProjectQuery.ComObjectRefs(review.Project).GroupBy(r => new ObjectKey(r.Device.Id, r.Ref.Id))
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Ref);
        var groups = ProjectQuery.GroupAddresses(review.Project).GroupBy(g => g.Id, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        for (var i = 0; i < proposals.Length; i++)
        {
            var proposal = proposals[i];
            var device = devices.GetValueOrDefault(proposal.Device.Id);
            var obj = objects.GetValueOrDefault(requests[i].Key);
            var group = groups.GetValueOrDefault(proposal.GroupAddress.Id);
            if (!ReferenceEquals(device, proposal.Device) || !ReferenceEquals(obj, proposal.ComObject)
                || !ReferenceEquals(group, proposal.GroupAddress) || obj!.Links.Count != 0)
                throw new InvalidOperationException(WorkbenchStrings.Get("SmartStale"));
            var validation = checks[i].Validation;
            if (!validation.Ok) throw new InvalidOperationException(WorkbenchStrings.Get("SmartIncompatible", obj.Number, SmartReason(validation)));
        }
        var result = ApplyLinkPairs(WorkbenchStrings.Get("SmartApplied", proposals.Length), requests);
        if (!result.Ok) throw new InvalidOperationException(WorkbenchStrings.Get("SmartStale"));
        review.IsApplied = true; _smartLinkReview = null;
        return result.Changed;
    }

    public void CancelSmartLinkReview(SmartLinkReviewPlan review)
    { if (ReferenceEquals(_smartLinkReview, review)) _smartLinkReview = null; }

    /// <summary>Legacy callers also receive a bound review; externally supplied/stale live references are refused.</summary>
    public IReadOnlyList<LinkSuggestion> SuggestLinks()
    {
        if (Project is null) { Status = Strings.Get("UxNeedsProject"); return []; }
        return PreviewSmartLinks().Proposals.Values.ToArray();
    }
    public int ApplyLinks(IEnumerable<LinkSuggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        var review = _smartLinkReview ?? throw new InvalidOperationException(WorkbenchStrings.Get("SmartStale"));
        var known = new Dictionary<LinkSuggestion, string>(ReferenceEqualityComparer.Instance);
        foreach (var proposal in review.Proposals) known.Add(proposal.Value, proposal.Key);
        var ids = suggestions.Select(p => known.TryGetValue(p, out var id) ? id
            : throw new InvalidOperationException(WorkbenchStrings.Get("SmartUnknownSelection"))).ToArray();
        return ApplySmartLinks(review, ids);
    }
    private static string SmartFingerprint(Project project) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting) + JsonSerializer.Serialize(project.Hardware))));
    private static string SmartReason(LinkValidation validation) => WorkbenchStrings.Get(validation.Reason switch
    {
        LinkValidationReason.Missing => "SmartMissing", LinkValidationReason.Inactive => "SmartInactive",
        LinkValidationReason.BitSize => "SmartBits", LinkValidationReason.DatapointType or LinkValidationReason.LongMainType => "SmartDpt",
        LinkValidationReason.SecureCapability => "SmartSecure", _ => "SmartUnknown",
    });
}
