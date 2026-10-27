using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Knx.App.Workbench;

/// <summary>Bounded, human-readable project change history; lives outside disposable views.</summary>
public sealed class WorkbenchAudit
{
    public const int Capacity = 500;
    private readonly ObservableCollection<WorkbenchChange> _entries = [];
    public WorkbenchAudit()
    {
        Entries = new(_entries);
        ((INotifyCollectionChanged)_entries).CollectionChanged += (_, _) =>
        {
            CountChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public ReadOnlyObservableCollection<WorkbenchChange> Entries { get; }
    public event EventHandler? CountChanged;
    public int Count => _entries.Count;

    public void Add(string description, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        _entries.Insert(0, new(at ?? DateTimeOffset.Now, description.Trim()));
        while (_entries.Count > Capacity) _entries.RemoveAt(_entries.Count - 1);
    }

    public void Clear() => _entries.Clear();
}

public sealed record WorkbenchChange(DateTimeOffset At, string Description);
