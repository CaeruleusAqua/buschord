namespace Knx.Workbench;

/// <summary>One interface, the activities holding it and the activities currently blocked on it.</summary>
public sealed record WorkbenchInterfaceStatus(string InterfaceId,
    IReadOnlyList<string> ActiveActivities, IReadOnlyList<string> BlockedActivities)
{
    public bool HasConflict => BlockedActivities.Count > 0;
}

/// <summary>Project-wide programming needs derived from <see cref="DeviceProgrammingReport"/>.</summary>
public sealed record WorkbenchProgrammingSummary(
    int DeviceCount,
    int NeedsProgrammingCount,
    int FailedCount,
    IReadOnlyList<DeviceProgrammingStatus> DevicesNeedingProgramming)
{
    public bool AllLoaded => NeedsProgrammingCount == 0;
}

/// <summary>A link to a Secure group address whose device is not explicitly Secure-capable.</summary>
public sealed record SecureCapabilityGap(string DeviceId, string DeviceName,
    string GroupId, string GroupAddress);

/// <summary>
/// Facts and hard limits of the project's Secure data. BusChord validates Secure links from project
/// metadata only; key management and secure download/programming are not implemented.
/// </summary>
public sealed record SecureCapabilitySummary(
    int SecureDeviceCount,
    int SecureGroupCount,
    int SecureLinkCount,
    int UnsupportedSecureLinkCount,
    IReadOnlyList<SecureCapabilityGap> Gaps,
    bool KeyManagementAvailable,
    bool SecureDownloadAvailable,
    string Limits)
{
    public bool Complete => UnsupportedSecureLinkCount == 0;
}

/// <summary>One project-level snapshot for the commissioning/diagnostics surface.</summary>
public sealed record WorkbenchDiagnosticSummary(
    string? ProjectId,
    string ProjectName,
    DateTimeOffset GeneratedAt,
    WorkbenchProgrammingSummary Programming,
    SecureCapabilitySummary Secure,
    IReadOnlyList<WorkbenchInterfaceStatus> Interfaces)
{
    public bool HasInterfaceConflicts => Interfaces.Any(i => i.HasConflict);
}

/// <summary>
/// Pure project diagnostics. Programming status is supplied by the session that owns the frozen
/// signatures; interface activity is supplied by the application's lease coordinator.
/// </summary>
public static class WorkbenchDiagnostics
{
    public const string SecureLimits =
        "Secure links are validated from project metadata only. No keyring, FDSK or certificate " +
        "management and no secure download/programming are available in this build.";

    public static WorkbenchDiagnosticSummary Summarize(Project project,
        IReadOnlyList<DeviceProgrammingStatus> programming,
        IEnumerable<WorkbenchInterfaceStatus>? interfaces = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(programming);
        var statuses = programming.ToArray();
        var needs = statuses.Where(p => p.NeedsProgramming).ToArray();
        return new(
            project.Id,
            project.Name,
            generatedAt ?? DateTimeOffset.UtcNow,
            new WorkbenchProgrammingSummary(statuses.Length, needs.Length,
                statuses.Count(p => p.LastError is not null), needs),
            Secure(project),
            interfaces?.OrderBy(i => i.InterfaceId, StringComparer.Ordinal).ToArray() ?? []);
    }

    private static SecureCapabilitySummary Secure(Project project)
    {
        var groups = ProjectQuery.GroupAddresses(project).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var secureGroups = project.Workbench.Groups
            .Where(p => p.Value.Secure == true && groups.ContainsKey(p.Key))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var secureDevices = 0;
        var secureLinks = 0;
        var gaps = new Dictionary<(string DeviceId, string GroupId), SecureCapabilityGap>();
        foreach (var located in ProjectQuery.Devices(project))
        {
            var device = located.Device;
            var capable = project.Workbench.Devices.GetValueOrDefault(device.Id)?.SecureCapable == true;
            if (capable) secureDevices++;
            foreach (var groupId in device.ComObjectRefs.SelectMany(o => o.Links).Where(secureGroups.Contains))
            {
                secureLinks++;
                if (capable || gaps.ContainsKey((device.Id, groupId))) continue;
                var address = groups[groupId].Address.ToString();
                gaps[(device.Id, groupId)] = new(device.Id, device.Name ?? device.Id, groupId, address);
            }
        }
        return new(secureDevices, secureGroups.Count, secureLinks, gaps.Count,
            gaps.Values.OrderBy(g => g.GroupAddress, StringComparer.Ordinal)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal).ToArray(),
            false, false, SecureLimits);
    }
}
