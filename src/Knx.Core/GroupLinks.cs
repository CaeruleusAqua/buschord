namespace Knx;

/// <summary>Manual communication-object links. Mutates existing lists; callers record undo snapshots.</summary>
public static class GroupLinks
{
    /// <summary>Adds a group address id once; returns false if already linked.</summary>
    public static bool Link(Project project, DeviceInstance device, string comObjectRefId, string groupAddressId)
    {
        var obj = Validate(project, device, comObjectRefId, groupAddressId);
        if (obj.Links.Contains(groupAddressId)) return false;
        obj.Links.Add(groupAddressId);
        return true;
    }

    /// <summary>Removes the link (including legacy duplicates); returns false if absent.</summary>
    public static bool Unlink(Project project, DeviceInstance device, string comObjectRefId, string groupAddressId)
        => Validate(project, device, comObjectRefId, groupAddressId).Links.RemoveAll(id => id == groupAddressId) > 0;

    private static ComObjectInstanceRef Validate(Project project, DeviceInstance device,
        string comObjectRefId, string groupAddressId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(comObjectRefId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupAddressId);
        if (!ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device)))
            throw new ArgumentException("device is not a member of the current project", nameof(device));
        var objects = device.ComObjectRefs.Where(r => r.Id == comObjectRefId).Take(2).ToList();
        if (objects.Count != 1)
            throw new ArgumentException($"communication object '{comObjectRefId}' not found or ambiguous on device {device.Address}", nameof(comObjectRefId));
        if (ProjectQuery.GroupAddresses(project).Count(a => a.Id == groupAddressId) != 1)
            throw new ArgumentException($"group address id '{groupAddressId}' not found or ambiguous in the current project", nameof(groupAddressId));
        return objects[0];
    }
}
