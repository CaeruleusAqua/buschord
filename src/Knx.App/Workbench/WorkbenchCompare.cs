namespace Knx.App.Workbench;

/// <summary>
/// V4 compare (24/E09): the pinned counterview always follows one concrete group address.
/// The active group address wins; every other work item falls back to the first address in
/// numeric order, so the shell action never depends on what the table happens to show.
/// </summary>
public static class WorkbenchCompare
{
    public static WorkbenchTarget? Resolve(Project? project, WorkbenchTarget? active)
    {
        if (project is null) return null;
        var addresses = ProjectQuery.GroupAddresses(project).ToList();
        if (active is { Kind: "ga" } && addresses.Any(address => address.Id == active.Id)) return active;
        return addresses.OrderBy(address => address.Address.Raw)
            .Select(address => new WorkbenchTarget("ga", address.Id))
            .FirstOrDefault();
    }
}
