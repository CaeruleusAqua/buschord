using Knx.App.Localization;
using Knx.Workbench;

namespace Knx.App.Services;

public sealed partial class MainWindowViewModel
{
    private MassLinkReviewPlan? _massLinkReview;

    public MassLinkReviewPlan PreviewMassLinks(WorkbenchScope? scope = null)
    {
        _massLinkReview = null;
        var project = Project ?? throw new InvalidOperationException(Strings.Get("UxNeedsProject"));
        return _massLinkReview = MassLinkReview.Preview(project, ProjectSessionIdentity, scope);
    }

    public void SelectMassLinkTarget(MassLinkReviewPlan plan, string rowId, string? groupId)
        => SelectMassLinkTargets(plan, [new(rowId, groupId)]);

    public void SelectMassLinkTargets(MassLinkReviewPlan plan, IEnumerable<KeyValuePair<string, string?>> selections)
    {
        if (!ReferenceEquals(plan, _massLinkReview)) throw new InvalidOperationException(WorkbenchStrings.Get("SmartStale"));
        MassLinkReview.SelectTargets(plan, Project ?? throw new InvalidOperationException(Strings.Get("UxNeedsProject")), ProjectSessionIdentity, selections);
    }

    public int ApplyMassLinks(MassLinkReviewPlan plan, IEnumerable<string>? selectedRowIds = null)
    {
        if (HistoryBlocked?.Invoke() == true) throw new InvalidOperationException(WorkbenchStrings.Get("SmartBusy"));
        if (!ReferenceEquals(plan, _massLinkReview)) throw new InvalidOperationException(WorkbenchStrings.Get("SmartStale"));
        var result = MassLinkReview.Apply(plan, Project ?? throw new InvalidOperationException(Strings.Get("UxNeedsProject")), ProjectSessionIdentity, _undo, selectedRowIds);
        if (!result.Ok) throw new InvalidOperationException(result.Errors.FirstOrDefault()?.Reason ?? WorkbenchStrings.Get("LinkImpossible"));
        _massLinkReview = null;
        if (result.Changed > 0)
        {
            NotifyEditState(); Refresh();
            Status = WorkbenchStrings.Get("SmartApplied", result.Changed);
            NotifyProjectEdit(Status);
        }
        return result.Changed;
    }

    public void CancelMassLinkReview(MassLinkReviewPlan plan)
    {
        if (ReferenceEquals(plan, _massLinkReview)) _massLinkReview = null;
    }
}
