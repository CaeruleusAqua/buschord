using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Knx.App.Workbench;

/// <summary>Application-owned state. Views borrow these sessions; only the application disposes them.</summary>
public sealed class WorkbenchSessions : IAsyncDisposable
{
    public InterfaceCoordinator Interfaces { get; } = new();
    public MonitorSession Monitor { get; }
    public ProgrammingSession Programming { get; }

    public WorkbenchSessions(Func<Project?> project,
        Func<MonitorFilter, IReadOnlySet<string>>? resolveGroupIds = null,
        Func<Project, DeviceInstance, FrozenConfiguration>? freezeConfiguration = null,
        Func<ProgrammingJob, CancellationToken, Task>? demoExecutor = null)
    {
        var scopedProject = new SessionProject(project);
        Monitor = new(Interfaces, scopedProject.Get, resolveGroupIds);
        Programming = new(Interfaces, scopedProject.Get, freezeConfiguration, demoExecutor);
    }

    public async ValueTask DisposeAsync()
    {
        try { Monitor.Dispose(); }
        finally { await Programming.DisposeAsync(); }
    }
}

public enum SessionOrigin { Demo, Real }
public enum InterfaceActivity { GroupMonitor, BusMonitor, Scan, Programming }

/// <summary>Shared by every interface consumer, including scan/download adapters outside this module.</summary>
public sealed class InterfaceCoordinator
{
    private readonly object _gate = new();
    private readonly List<Reservation> _active = [];

    public bool TryAcquire(string interfaceId, InterfaceActivity activity, out IDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!Enum.IsDefined(activity)) throw new ArgumentOutOfRangeException(nameof(activity));
        lock (_gate)
        {
            if (_active.Any(r => r.InterfaceId == interfaceId &&
                (r.Activity == InterfaceActivity.BusMonitor || activity == InterfaceActivity.BusMonitor ||
                 (r.Activity != InterfaceActivity.GroupMonitor && activity != InterfaceActivity.GroupMonitor))))
            {
                lease = null;
                return false;
            }
            var reservation = new Reservation(this, interfaceId, activity);
            _active.Add(reservation);
            lease = reservation;
            return true;
        }
    }

    private sealed class Reservation(InterfaceCoordinator owner, string interfaceId, InterfaceActivity activity) : IDisposable
    {
        public string InterfaceId { get; } = interfaceId;
        public InterfaceActivity Activity { get; } = activity;
        public void Dispose() { lock (owner._gate) owner._active.Remove(this); }
    }
}

/// <summary>
/// Mutators run on the creating UI synchronization context. Without one, the caller must serialize
/// mutation and await asynchronous operations. Only monitor ingestion is callable from arbitrary threads.
/// </summary>
public abstract class SessionState : ObservableObject
{
    private readonly SynchronizationContext? _owner = SynchronizationContext.Current;
    protected void VerifyAccess()
    {
        if (_owner is not null && SynchronizationContext.Current != _owner)
            throw new InvalidOperationException("Session mutation must run on its owning UI context.");
    }
}

internal sealed class SnapshotCollection<T> : ObservableCollection<T>
{
    public void Replace(IEnumerable<T> values)
    {
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Undo may replace the project instance, but switching project identity requires fresh sessions.</summary>
internal sealed class SessionProject(Func<Project?> provider)
{
    private string? _id;
    public Project? Get()
    {
        var project = provider();
        if (project is null) return null;
        _id ??= project.Id;
        if (_id != project.Id)
            throw new InvalidOperationException("Dispose the previous project's sessions before switching project identity.");
        return project;
    }
}
