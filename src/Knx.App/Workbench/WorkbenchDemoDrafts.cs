namespace Knx.App.Workbench;

/// <summary>Demo drafts outlive their controls but never overwrite changed saved parameters.</summary>
internal sealed class WorkbenchDemoDrafts
{
    private sealed record Draft(Dictionary<string, string> Baseline, Dictionary<string, string> Values);
    private readonly Dictionary<string, Draft> _devices = new(StringComparer.Ordinal);

    public Dictionary<string, string> For(DeviceInstance device)
    {
        if (!_devices.TryGetValue(device.Id, out var draft) || !Same(draft.Baseline, device.ParameterValues))
            _devices[device.Id] = draft = new(new(device.ParameterValues), new(device.ParameterValues));
        return draft.Values;
    }

    public bool HasChanges(Project? project)
    {
        var devices = project is null ? [] : ProjectQuery.DeviceList(project).ToDictionary(d => d.Id);
        foreach (var (id, draft) in _devices.ToArray())
            if (!devices.TryGetValue(id, out var device) || !Same(draft.Baseline, device.ParameterValues)) _devices.Remove(id);
        return _devices.Values.Any(d => !Same(d.Baseline, d.Values));
    }

    public void Discard(string deviceId) => _devices.Remove(deviceId);
    public void Clear() => _devices.Clear();
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value == p.Value);
}
