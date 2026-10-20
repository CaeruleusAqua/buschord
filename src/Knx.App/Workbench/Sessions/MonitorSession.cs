using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Knx.App.Services;
using Knx.Bus;

namespace Knx.App.Workbench;

public enum WorkbenchMonitorMode { Group, Bus }
public enum MonitorScope { All, Project, Room, GroupAddress, ComObject, None }
public sealed record MonitorFilter(MonitorScope Scope, string? Id = null);
public sealed record WorkbenchTelegram(DateTimeOffset Timestamp, string Source, string Destination,
    string Service, string? DatapointType, string Value, SessionOrigin Origin,
    string? GroupAddressId = null, string? DeviceId = null, string DataHex = "", bool IsGroup = true);

/// <summary>
/// Full CSV recording is disk-backed and independent of the 500-row display tail. Ingest is thread-safe;
/// call FlushDisplay on a UI timer (e.g. 100 ms) to coalesce observable updates. No view owns capture.
/// </summary>
public sealed class MonitorSession : SessionState, IDisposable
{
    public const int DisplayCapacity = 500;
    private const string Header = "Timestamp,Source,Destination,Service,DPT,Value,Origin,GroupAddressId,DeviceId,Data\r\n";
    private readonly InterfaceCoordinator _interfaces;
    private readonly Func<Project?> _project;
    private readonly Func<MonitorFilter, IReadOnlySet<string>>? _resolve;
    private readonly object _gate = new();
    private readonly Queue<WorkbenchTelegram> _tail = new();
    private readonly SnapshotCollection<WorkbenchTelegram> _rows = new();
    private readonly SnapshotCollection<WorkbenchTelegram> _filtered = new();
    private readonly FileStream _recording;
    private readonly StreamWriter _writer;
    private Dictionary<ushort, (string Id, string? Dpt)> _groups = [];
    private Dictionary<string, string> _devices = [];
    private IDisposable? _lease;
    private KnxBus? _bus;
    private bool _running, _disposed, _follow;
    private long _count, _publishedCount = -1;
    private MonitorFilter _filter = new(MonitorScope.All);

    public MonitorSession(InterfaceCoordinator interfaces, Func<Project?> project,
        Func<MonitorFilter, IReadOnlySet<string>>? resolveGroupIds = null)
    {
        _interfaces = interfaces; _project = project; _resolve = resolveGroupIds;
        Rows = new(_rows); FilteredRows = new(_filtered);
        var path = Path.Combine(Path.GetTempPath(), $"buschord-monitor-{Guid.NewGuid():N}.csv");
        _recording = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            4096, FileOptions.DeleteOnClose);
        _writer = new(_recording, new UTF8Encoding(false), 4096, leaveOpen: true);
        _writer.Write(Header);
    }

    public ReadOnlyObservableCollection<WorkbenchTelegram> Rows { get; }
    public ReadOnlyObservableCollection<WorkbenchTelegram> FilteredRows { get; }
    public bool IsRunning => _running;
    public bool FollowSelection => _follow;
    public MonitorFilter Filter => _filter;
    public WorkbenchMonitorMode Mode { get; private set; }
    public SessionOrigin Origin { get; private set; } = SessionOrigin.Demo;
    public string InterfaceId { get; private set; } = "demo-ip";
    public long RecordedCount { get { lock (_gate) return _count; } }

    /// <summary>Real mode borrows an already connected bus; never connects, writes or disposes that bus.</summary>
    public void Configure(WorkbenchMonitorMode mode, SessionOrigin origin, string interfaceId, KnxBus? bus = null)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) throw new InvalidOperationException("Stop the monitor before changing interface or mode.");
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (origin == SessionOrigin.Real && mode == WorkbenchMonitorMode.Bus)
            throw new NotSupportedException("KnxBus supplies group traffic, not a hardware bus-monitor adapter.");
        if (origin == SessionOrigin.Real && bus is null) throw new ArgumentNullException(nameof(bus));
        Mode = mode; Origin = origin; InterfaceId = interfaceId; _bus = bus;
        OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(Origin)); OnPropertyChanged(nameof(InterfaceId));
    }

    public void Start()
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) return;
        if (Origin == SessionOrigin.Real && _bus?.State != BusConnectionState.Connected)
            throw new InvalidOperationException("The real bus must already be connected.");
        var project = _project();
        _groups = project is null ? [] : ProjectQuery.GroupAddresses(project)
            .GroupBy(g => g.Address.Raw).ToDictionary(g => g.Key, g => (g.First().Id, g.First().DatapointType));
        _devices = project is null ? [] : ProjectQuery.DeviceList(project)
            .GroupBy(d => d.Address.ToString()).ToDictionary(g => g.Key, g => g.First().Id);
        if (!_interfaces.TryAcquire(InterfaceId, Mode == WorkbenchMonitorMode.Bus
                ? InterfaceActivity.BusMonitor : InterfaceActivity.GroupMonitor, out _lease))
            throw new InvalidOperationException("The interface is occupied by a conflicting operation.");
        lock (_gate) _running = true;
        if (Origin == SessionOrigin.Real) _bus!.TelegramReceived += Receive;
        OnPropertyChanged(nameof(IsRunning));
    }

    public void Stop()
    {
        VerifyAccess();
        lock (_gate) _running = false;
        if (_bus is not null) _bus.TelegramReceived -= Receive;
        _lease?.Dispose(); _lease = null;
        OnPropertyChanged(nameof(IsRunning));
        if (!_disposed) FlushDisplay();
    }

    private void Receive(BusTelegram telegram) => IngestReal(telegram);

    /// <summary>Real adapter feed. Payload and metadata are copied immediately, never retained as mutable frames.</summary>
    public void IngestReal(BusTelegram telegram)
    {
        lock (_gate)
        {
            if (!_running || _disposed || Origin != SessionOrigin.Real) return;
            var frame = telegram.Telegram;
            var group = frame.IsGroupAddress && _groups.TryGetValue((ushort)(frame.Destination.Raw & 0x7fff), out var g)
                ? g : default;
            var (value, unit) = GroupMonitor.DecodeValue(group.Dpt, frame.Data);
            Append(new(telegram.Timestamp, frame.Source.ToString(), frame.IsGroupAddress
                ? GroupMonitor.GroupText(frame.Destination.Raw) : frame.Destination.ToString(),
                frame.Apci.ToString(), group.Dpt, value + (unit.Length == 0 ? "" : " " + unit), SessionOrigin.Real,
                group.Id, _devices.GetValueOrDefault(frame.Source.ToString()), Convert.ToHexString(frame.Data ?? []), frame.IsGroupAddress));
        }
    }

    /// <summary>Explicit simulation tick; cannot transmit anything. A UI/application timer may call this while hidden.</summary>
    public void TickDemo(WorkbenchTelegram telegram)
    {
        VerifyAccess(); ThrowIfDisposed();
        lock (_gate)
        {
            if (!_running || Origin != SessionOrigin.Demo) throw new InvalidOperationException("Demo monitor is not running.");
            if (telegram.Origin != SessionOrigin.Demo) throw new ArgumentException("Demo ticks must be marked Demo.", nameof(telegram));
            Append(telegram);
        }
    }

    private void Append(WorkbenchTelegram telegram)
    {
        _writer.Write(CsvRow(telegram));
        _count++;
        _tail.Enqueue(telegram);
        if (_tail.Count > DisplayCapacity) _tail.Dequeue();
    }

    public void PinFilter(MonitorFilter filter)
    {
        VerifyAccess(); ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(filter);
        _follow = false; _filter = filter;
        OnPropertyChanged(nameof(FollowSelection)); OnPropertyChanged(nameof(Filter)); Refilter();
    }

    public void SetFollowSelection(bool follow, string? selectedComObjectId = null)
    {
        VerifyAccess(); ThrowIfDisposed(); _follow = follow;
        OnPropertyChanged(nameof(FollowSelection));
        if (follow) SelectionChanged(selectedComObjectId);
    }

    public void SelectionChanged(string? selectedComObjectId)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (!_follow) return;
        _filter = selectedComObjectId is null ? new(MonitorScope.None) : new(MonitorScope.ComObject, selectedComObjectId);
        OnPropertyChanged(nameof(Filter)); Refilter();
    }

    public void FlushDisplay()
    {
        VerifyAccess(); ThrowIfDisposed();
        WorkbenchTelegram[] rows;
        lock (_gate)
        {
            if (_publishedCount == _count) return;
            rows = _tail.ToArray(); _publishedCount = _count;
        }
        _rows.Replace(rows); Refilter(); OnPropertyChanged(nameof(RecordedCount));
    }

    /// <summary>Call after project/link changes to reevaluate dynamic room/project/KO membership.</summary>
    public void RefreshFilter() { VerifyAccess(); ThrowIfDisposed(); Refilter(); }

    private void Refilter()
    {
        IReadOnlySet<string> ids;
        if (_filter.Scope == MonitorScope.All) { _filtered.Replace(_rows); return; }
        if (_filter.Scope == MonitorScope.None) { _filtered.Replace([]); return; }
        if (_resolve is not null) ids = _resolve(_filter);
        else if (_filter.Scope == MonitorScope.GroupAddress) ids = new HashSet<string> { _filter.Id ?? "" };
        else if (_filter.Scope == MonitorScope.Project)
            ids = _project() is { } p ? ProjectQuery.GroupAddresses(p).Select(g => g.Id).ToHashSet() : new HashSet<string>();
        else if (_filter.Scope == MonitorScope.ComObject)
            ids = _project() is { } p ? ProjectQuery.ComObjectRefs(p).Where(o => o.Ref.Id == _filter.Id)
                .SelectMany(o => o.Ref.Links).ToHashSet() : new HashSet<string>();
        else ids = new HashSet<string>();
        _filtered.Replace(_rows.Where(r => r.GroupAddressId is not null && ids.Contains(r.GroupAddressId)));
    }

    /// <summary>Clear the display only; the full recording is intentionally retained.</summary>
    public void ClearDisplay()
    {
        VerifyAccess(); ThrowIfDisposed();
        lock (_gate) { _tail.Clear(); _publishedCount = _count; }
        _rows.Replace([]); Refilter();
    }

    public string ExportFilteredCsv()
    {
        VerifyAccess(); ThrowIfDisposed(); FlushDisplay();
        return Header + string.Concat(FilteredRows.Select(CsvRow));
    }

    public void ExportFullCsv(Stream destination)
    {
        VerifyAccess(); ThrowIfDisposed();
        lock (_gate)
        {
            _writer.Flush();
            var position = _recording.Position;
            try { _recording.Position = 0; _recording.CopyTo(destination); }
            finally { _recording.Position = position; }
        }
    }

    private static string CsvRow(WorkbenchTelegram r) => string.Join(',', new[]
    {
        r.Timestamp.ToString("O", CultureInfo.InvariantCulture), r.Source, r.Destination, r.Service,
        r.DatapointType ?? "", r.Value, r.Origin.ToString(), r.GroupAddressId ?? "", r.DeviceId ?? "", r.DataHex,
    }.Select(GroupMonitor.CsvField)) + "\r\n";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        Stop();
        lock (_gate) { _disposed = true; _writer.Dispose(); _recording.Dispose(); }
    }
}
