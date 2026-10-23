using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Knx.Workbench;

namespace Knx.App.Workbench;

/// <summary>Application-owned state. Views borrow these sessions; only the application disposes them.</summary>
public sealed class WorkbenchSessions : IAsyncDisposable
{
    private readonly Func<Project?> _project;

    public InterfaceCoordinator Interfaces { get; } = new();
    public MonitorSession Monitor { get; }
    public ProgrammingSession Programming { get; }
    public LineScanSession LineScan { get; }

    public WorkbenchSessions(Func<Project?> project,
        Func<MonitorFilter, IReadOnlySet<string>>? resolveGroupIds = null,
        Func<Project, DeviceInstance, FrozenConfiguration>? freezeConfiguration = null,
        Func<ProgrammingJob, CancellationToken, Task>? demoExecutor = null,
        Func<ProgrammingJob, CancellationToken, Task>? realExecutor = null)
    {
        var scopedProject = new SessionProject(project);
        _project = scopedProject.Get;
        Monitor = new(Interfaces, _project, resolveGroupIds);
        Programming = new(Interfaces, _project, freezeConfiguration, demoExecutor, realExecutor);
        LineScan = new(Interfaces, _project);
    }

    /// <summary>
    /// One project-level snapshot for the diagnostics/commissioning shell. Includes per-device programming
    /// needs, currently blocked interface activities and the project's Secure capability limits.
    /// </summary>
    public WorkbenchDiagnosticSummary BuildDiagnosticSummary()
    {
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var interfaces = Interfaces.StatusSnapshot()
            .Select(s => new WorkbenchInterfaceStatus(s.InterfaceId,
                s.Active.Select(a => a.ToString()).ToArray(),
                s.Blocked.Select(a => a.ToString()).ToArray()))
            .ToArray();
        return WorkbenchDiagnostics.Summarize(project, Programming.CheckProgramming(), interfaces);
    }

    public async ValueTask DisposeAsync()
    {
        try { Monitor.Dispose(); }
        finally { await Programming.DisposeAsync(); }
    }
}

public enum SessionOrigin { Demo, Real }
public enum InterfaceActivity { GroupMonitor, BusMonitor, Scan, Programming }

/// <summary>Active reservations of one interface and the activities they currently block.</summary>
public sealed record InterfaceStatus(string InterfaceId,
    IReadOnlyList<InterfaceActivity> Active, IReadOnlyList<InterfaceActivity> Blocked);

/// <summary>Shared by every interface consumer, including scan/download adapters outside this module.</summary>
public sealed class InterfaceCoordinator
{
    private readonly object _gate = new();
    private readonly List<Reservation> _active = [];

    /// <summary>
    /// A bus monitor excludes every other activity; scans/programming exclude each other;
    /// group monitoring may coexist with group monitoring only.
    /// </summary>
    public static bool Conflicts(InterfaceActivity active, InterfaceActivity requested) =>
        active == InterfaceActivity.BusMonitor || requested == InterfaceActivity.BusMonitor ||
        (active != InterfaceActivity.GroupMonitor && requested != InterfaceActivity.GroupMonitor);

    public bool TryAcquire(string interfaceId, InterfaceActivity activity, out IDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!Enum.IsDefined(activity)) throw new ArgumentOutOfRangeException(nameof(activity));
        lock (_gate)
        {
            if (_active.Any(r => r.InterfaceId == interfaceId && Conflicts(r.Activity, activity)))
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

    /// <summary>Read-only lease snapshot for conflict/diagnostic displays; acquiring still uses <see cref="TryAcquire"/>.</summary>
    public IReadOnlyList<InterfaceStatus> StatusSnapshot()
    {
        lock (_gate)
        {
            return _active.GroupBy(r => r.InterfaceId, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var active = group.Select(r => r.Activity).Distinct().Order().ToArray();
                    var blocked = Enum.GetValues<InterfaceActivity>()
                        .Where(candidate => active.Any(a => Conflicts(a, candidate))).ToArray();
                    return new InterfaceStatus(group.Key, active, blocked);
                })
                .ToList();
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
        // Avalonia installs contexts at different dispatcher priorities on the same UI thread.
        // Their object identities differ; the dispatcher, not that identity, owns UI access.
        if (_owner is AvaloniaSynchronizationContext)
        {
            Dispatcher.UIThread.VerifyAccess();
            return;
        }
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
