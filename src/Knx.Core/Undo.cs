using System.Xml.Linq;

namespace Knx;

/// <summary>
/// Snapshot based undo/redo. ETS stores per-object change records; we serialise the
/// whole project XML per step instead – far less code and correct for every mutation,
/// at the cost of memory (see <see cref="Capacity"/>).
/// </summary>
public sealed class UndoStack
{
    private readonly List<(string Xml, ArchiveState? Archive)> _snapshots = [];

    public UndoStack(int capacity = 60) => Capacity = capacity;

    /// <summary>Maximum retained snapshots; the oldest is dropped first.</summary>
    public int Capacity { get; }

    public int Index { get; private set; } = -1;
    public int Steps => Math.Max(0, _snapshots.Count - 1);

    public bool CanUndo => Index > 0;
    public bool CanRedo => Index >= 0 && Index < _snapshots.Count - 1;

    public void Reset(Project project)
    {
        _snapshots.Clear();
        Index = -1;
        Push(project);
    }

    /// <summary>Records the state after a mutation. No-op when nothing changed.</summary>
    public void Push(Project project)
    {
        var xml = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
        if (Index >= 0 && _snapshots[Index].Xml == xml) return;

        if (Index < _snapshots.Count - 1)
            _snapshots.RemoveRange(Index + 1, _snapshots.Count - Index - 1);
        _snapshots.Add((xml, project.ArchiveState));
        if (_snapshots.Count > Capacity) _snapshots.RemoveAt(0);
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
        return project;
    }
}
