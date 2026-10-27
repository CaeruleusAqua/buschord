using Knx.Workbench;

namespace Knx.App.Workbench;

/// <summary>Host lifecycle binding for DEMO state. Store errors never turn a simulation into a failed/real bus operation.</summary>
public sealed class ProgrammingStatePersistence : IDisposable
{
    private readonly ProgrammingSession _session;
    private readonly ProgrammingStateStore _store;
    private readonly Action<string>? _error;
    private readonly string _projectId;
    private readonly string? _projectGuid;
    private ProgrammingStateBinding? _binding;
    private bool _dirty, _disposed;

    public ProgrammingStatePersistence(ProgrammingSession session, ProgrammingStateStore store, Project project, string? path, Action<string>? error = null)
    {
        _session = session; _store = store; _error = error; _projectId = project.Id; _projectGuid = project.Guid;
        _session.DownloadedChanged += OnChanged;
        Rebind(project, path);
    }

    public void Rebind(Project project, string? path)
    {
        if (_disposed) return;
        try
        {
            if (project.Id != _projectId || project.Guid != _projectGuid)
                throw new InvalidOperationException("A different project requires a fresh programming session.");
            var next = ProgrammingStateBinding.Create(project, path);
            if (_binding == next) return;
            Flush(); _binding = next;
            if (_binding is null) return;
            if (_session.DeviceStates.Count > 0) { _dirty = true; Flush(); }
            else if (_store.TryLoad(_binding, out var json, out var error))
            {
                // Restore before observing changes: opening a file must not rewrite it.
                _session.DownloadedChanged -= OnChanged;
                try { _session.ImportDownloadedJson(json!); _dirty = false; }
                finally { _session.DownloadedChanged += OnChanged; }
            }
            else if (error is not null) _error?.Invoke("DEMO comparison: " + error);
        }
        catch (Exception ex) when (ProgrammingStateStore.IsFileError(ex)) { _error?.Invoke("DEMO comparison: " + ex.Message); }
    }

    private void OnChanged(object? sender, EventArgs e) { _dirty = true; Flush(); }
    public void Flush()
    {
        if (!_dirty || _binding is null) return;
        try { _store.Save(_binding, _session.DeviceStates.Values); _dirty = false; }
        catch (Exception ex) when (ProgrammingStateStore.IsFileError(ex)) { _error?.Invoke("DEMO comparison: " + ex.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        Flush(); _disposed = true; _session.DownloadedChanged -= OnChanged;
    }
}
