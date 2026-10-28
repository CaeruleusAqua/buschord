using Knx.App.Localization;
using Knx.Workbench;

namespace Knx.App.Services;

public sealed partial class MainWindowViewModel
{
    /// <summary>Commits validated links and their undo snapshot inside the core's rollback boundary.</summary>
    public MutationResult ApplyLinkPairs(string description, IEnumerable<LinkCheckRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var project = Project ?? throw new InvalidOperationException(Strings.Get("UxNeedsProject"));
        if (HistoryBlocked?.Invoke() == true) throw new InvalidOperationException(WorkbenchStrings.Get("SmartBusy"));
        var result = WorkbenchProject.AddLinkPairs(project, requests, _undo);
        if (!result.Ok) { Status = result.Errors.FirstOrDefault()?.Reason ?? WorkbenchStrings.Get("LinkImpossible"); return result; }
        if (result.Changed == 0) return result;
        NotifyEditState(); Refresh(); Status = description; ProjectEdited?.Invoke(this, description);
        return result;
    }
}
