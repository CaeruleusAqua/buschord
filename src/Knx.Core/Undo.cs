using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

/// <summary>
/// Snapshot-based undo/redo serializes the whole project XML per step,
/// at the cost of memory (see <see cref="Capacity"/>).
/// </summary>
public sealed class UndoStack
{
    private readonly List<(string Xml, ArchiveState? Archive, Dictionary<string, CatalogHardware> Hardware,
        Dictionary<Workbench.ObjectKey, ObjectTextDefinition> Texts)> _snapshots = [];
    private int _savedIndex = -1;

    public UndoStack(int capacity = 60) => Capacity = capacity;

    /// <summary>Maximum retained snapshots; the oldest is dropped first.</summary>
    public int Capacity { get; }

    public int Index { get; private set; } = -1;
    public int Steps => Math.Max(0, _snapshots.Count - 1);

    public bool CanUndo => Index > 0;
    public bool CanRedo => Index >= 0 && Index < _snapshots.Count - 1;

    /// <summary>
    /// True when the current state differs from the last snapshot that was written to disk.
    /// Undoing back onto that snapshot clears the flag; the history itself is kept.
    /// </summary>
    public bool IsDirty => Index >= 0 && Index != _savedIndex;

    public void Reset(Project project)
    {
        _snapshots.Clear();
        Index = -1;
        _savedIndex = -1;
        Push(project);
        _savedIndex = Index;
    }

    /// <summary>Marks the current snapshot as the state on disk (after save/load).</summary>
    public void MarkSaved() => _savedIndex = Index;

    /// <summary>Records the state after a mutation. No-op when nothing changed.</summary>
    public void Push(Project project)
    {
        var xml = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
        if (Index >= 0 && _snapshots[Index].Xml == xml) return;

        if (Index < _snapshots.Count - 1)
        {
            // A saved state inside the discarded redo branch can no longer be reached.
            if (_savedIndex > Index) _savedIndex = -1;
            _snapshots.RemoveRange(Index + 1, _snapshots.Count - Index - 1);
        }
        // Hardware facts live in the archive's Hardware.xml, not in the project XML; the snapshot keeps
        // them so a restored project can still run the bus-power and coupler checks.
        _snapshots.Add((xml, project.ArchiveState, new Dictionary<string, CatalogHardware>(project.Hardware), new(project.ObjectTextDefinitions)));
        if (_snapshots.Count > Capacity)
        {
            _snapshots.RemoveAt(0);
            if (_savedIndex >= 0) _savedIndex--;
        }
        Index = _snapshots.Count - 1;
    }

    public Project? Undo()
    {
        if (!CanUndo) return null;
        Index--;
        return Restore();
    }

    public Project? Redo()
    {
        if (!CanRedo) return null;
        Index++;
        return Restore();
    }

    private Project Restore()
    {
        var snapshot = _snapshots[Index];
        var project = Knxproj.FromXml(XDocument.Parse(snapshot.Xml, LoadOptions.PreserveWhitespace));
        project.ArchiveState = snapshot.Archive;
        foreach (var (id, fact) in snapshot.Hardware) project.Hardware[id] = fact;
        foreach (var (id, definition) in snapshot.Texts) project.ObjectTextDefinitions[id] = definition;
        foreach (var device in ProjectQuery.DeviceList(project)) ApplicationTexts.RefreshObjects(project, device);
        return project;
    }
}
