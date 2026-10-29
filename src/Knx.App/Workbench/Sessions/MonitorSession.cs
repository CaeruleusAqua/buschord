using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Knx.App.Services;
using Knx.App.Localization;
using Knx.Bus;

namespace Knx.App.Workbench;

public enum WorkbenchMonitorMode { Group, Bus }
public enum MonitorScope { All, Project, Room, GroupAddress, ComObject, Device, None }
public sealed record MonitorFilter(MonitorScope Scope, string? Id = null, string? DeviceId = null);
public sealed record WorkbenchTelegram(DateTimeOffset Timestamp, string Source, string Destination,
    string Service, string? DatapointType, string Value, SessionOrigin Origin,
    string? GroupAddressId = null, string? DeviceId = null, string DataHex = "", bool IsGroup = true)
{
    public byte? HopCount { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public BusDataSecurity DataSecurity { get; init; } = BusDataSecurity.Unverified;
}

/// <summary>Receive-only seam; implementations must not start a network connection implicitly.</summary>
public interface IMonitorTelegramSource
{
    bool IsConnected { get; }
    event Action<BusTelegram> TelegramReceived;
}

public interface IMonitorConnectionNotifications
{
    event Action<BusConnectionState> StateChanged;
}

public sealed class KnxBusMonitorSource(KnxBus bus) : IMonitorTelegramSource, IMonitorConnectionNotifications
{
    public bool IsConnected => bus.State == BusConnectionState.Connected;
    public event Action<BusTelegram> TelegramReceived
    {
        add => bus.TelegramReceived += value;
        remove => bus.TelegramReceived -= value;
    }
    public event Action<BusConnectionState> StateChanged
    {
        add => bus.StateChanged += value;
        remove => bus.StateChanged -= value;
    }
}

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
    private IMonitorTelegramSource? _bus;
    private Action<BusTelegram>? _receive;
    private Action<BusConnectionState>? _connectionChanged;
    private string? _historyProjectId;
    private DateTimeOffset _lastReceiveUtc;
    public MonitorHistoryCapture History { get; }
    public Task HistoryCompletion { get; private set; } = Task.CompletedTask;
    private (bool,MonitorHistoryFailure,long,long,long) _publishedHistory = (false,MonitorHistoryFailure.None,-1,-1,-1);
    public string HistoryActionLabel => WorkbenchStrings.Get(History.IsEnabled?"HistoryToolbarActive":"HistoryTitle");
    private long _generation;
    private string? _error;
    private bool _running, _disposed, _follow, _paused, _autoScroll = true;
    private long _count, _publishedCount = -1;
    private MonitorFilter _filter = new(MonitorScope.All);

    public MonitorSession(InterfaceCoordinator interfaces, Func<Project?> project,
        Func<MonitorFilter, IReadOnlySet<string>>? resolveGroupIds = null)
        : this(interfaces, project, resolveGroupIds, new MonitorHistoryCapture()) { }

    public MonitorSession(InterfaceCoordinator interfaces, Func<Project?> project,
        Func<MonitorFilter, IReadOnlySet<string>>? resolveGroupIds, MonitorHistoryCapture history)
    {
        History = history ?? throw new ArgumentNullException(nameof(history));
        _interfaces = interfaces; _project = new SessionProject(project).Get; _resolve = resolveGroupIds;
        Rows = new(_rows); FilteredRows = new(_filtered);
        var path = Path.Combine(Path.GetTempPath(), $"buschord-monitor-{Guid.NewGuid():N}.csv");
        var recordingOptions=new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,Share=FileShare.Read,
            BufferSize=4096,Options=FileOptions.DeleteOnClose };
        if(!OperatingSystem.IsWindows())recordingOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        _recording = new(path,recordingOptions);
        _writer = new(_recording, new UTF8Encoding(false), 4096, leaveOpen: true);
        _writer.Write(Header);
    }

    public ReadOnlyObservableCollection<WorkbenchTelegram> Rows { get; }
    public ReadOnlyObservableCollection<WorkbenchTelegram> FilteredRows { get; }
    public bool IsRunning { get { lock (_gate) return _running; } }
    /// <summary>Paused sessions keep their interface lease but drop incoming telegrams.</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }
    /// <summary>Whether the shell should keep the newest row visible. Presentation only; capture is unaffected.</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            VerifyAccess(); ThrowIfDisposed();
            if (_autoScroll == value) return;
            _autoScroll = value;
            OnPropertyChanged(nameof(AutoScroll));
        }
    }
    public bool FollowSelection => _follow;
    public MonitorFilter Filter => _filter;
    public WorkbenchMonitorMode Mode { get; private set; }
    public SessionOrigin Origin { get; private set; } = SessionOrigin.Demo;
    public string InterfaceId { get; private set; } = "demo-ip";
    public long RecordedCount { get { lock (_gate) return _count; } }
    public string? LastError { get { lock (_gate) return _error; } }

    public async Task SetHistoryEnabledAsync(bool enabled,CancellationToken cancellationToken=default)
    {
        VerifyAccess();ThrowIfDisposed();
        if(enabled) await History.OpenAsync(cancellationToken);
        VerifyAccess();ThrowIfDisposed();
        lock(_gate) History.SetEnabled(enabled,HistoryState(DateTimeOffset.UtcNow));
        await History.FlushAsync(cancellationToken);
        RefreshHistoryStatus();
    }
    private void RefreshHistoryStatus()
    {
        var current=(History.IsEnabled,History.Failure,History.WrittenCount,History.DroppedCount,History.SkippedBeforeAnchorCount);
        if(current==_publishedHistory)return;
        _publishedHistory=current;OnPropertyChanged(nameof(History));OnPropertyChanged(nameof(HistoryActionLabel));
    }
    private MonitorHistoryState HistoryState(DateTimeOffset atUtc,bool? connected=null)=>new(_running,_paused,
        connected??(Origin==SessionOrigin.Demo||_bus?.IsConnected==true),InterfaceId,Origin.ToString(),_historyProjectId,atUtc);

    /// <summary>Real mode borrows an already connected bus; never connects, writes or disposes that bus.</summary>
    public void Configure(WorkbenchMonitorMode mode, SessionOrigin origin, string interfaceId, KnxBus? bus = null)
        => ConfigureCore(mode, origin, interfaceId, bus is null ? null : new KnxBusMonitorSource(bus));

    private void ConfigureCore(WorkbenchMonitorMode mode, SessionOrigin origin, string interfaceId, IMonitorTelegramSource? source)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) throw new InvalidOperationException("Stop the monitor before changing interface or mode.");
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        if (origin == SessionOrigin.Real && mode == WorkbenchMonitorMode.Bus)
            throw new NotSupportedException("KnxBus supplies group traffic, not a hardware bus-monitor adapter.");
        if (origin == SessionOrigin.Real && source is null) throw new ArgumentNullException(nameof(source));
        ReleaseInterface();
        Mode = mode; Origin = origin; InterfaceId = interfaceId; _bus = source;
        lock (_gate) _paused = false;
        OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(Origin)); OnPropertyChanged(nameof(InterfaceId));
        OnPropertyChanged(nameof(IsPaused));
    }

    public void ConfigureReal(string interfaceId, IMonitorTelegramSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ConfigureCore(WorkbenchMonitorMode.Group, SessionOrigin.Real, interfaceId, source);
    }

    public void Start()
    {
        VerifyAccess(); ThrowIfDisposed();
        if (IsRunning) return;
        ReleaseInterface();
        if (Origin == SessionOrigin.Real && _bus?.IsConnected != true)
            throw new InvalidOperationException("The real bus must already be connected.");
        RefreshMetadata();
        if (!_interfaces.TryAcquire(InterfaceId, Mode == WorkbenchMonitorMode.Bus
                ? InterfaceActivity.BusMonitor : InterfaceActivity.GroupMonitor, out _lease))
            throw new InvalidOperationException("The interface is occupied by a conflicting operation.");
        lock (_gate) { _running = true; _error = null; _paused = false; _generation++; _lastReceiveUtc=DateTimeOffset.UtcNow;History.ObserveState(HistoryState(_lastReceiveUtc)); }
        try
        {
            if (Origin == SessionOrigin.Real)
            {
                var generation = _generation;
                _receive = telegram => IngestReal(telegram, generation);
                _bus!.TelegramReceived += _receive;
                if(_bus is IMonitorConnectionNotifications notifications)
                {
                    _connectionChanged = state =>
                    {
                        if(state==BusConnectionState.Connected)return;
                        lock(_gate)
                        {
                            if(!_running||generation!=_generation)return;
                            _running=false;_paused=false;_generation++;
                            History.ObserveState(HistoryState(DateTimeOffset.UtcNow,false));
                        }
                    };
                    notifications.StateChanged += _connectionChanged;
                }
            }
        }
        catch
        {
            lock (_gate) { _running = false;History.ObserveState(HistoryState(DateTimeOffset.UtcNow)); }
            ReleaseInterface();
            throw;
        }
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
    }

    public void Stop()
    {
        VerifyAccess();
        lock (_gate) { _running = false; _paused = false; _generation++;History.ObserveState(HistoryState(DateTimeOffset.UtcNow)); }
        ReleaseInterface();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        if (!_disposed) FlushDisplay();
    }

    /// <summary>Freezes capture without releasing the interface. Telegrams arriving while paused are dropped.</summary>
    public void Pause()
    {
        VerifyAccess(); ThrowIfDisposed();
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            History.ObserveState(HistoryState(DateTimeOffset.UtcNow));
        }
        OnPropertyChanged(nameof(IsPaused));
    }

    public void Resume()
    {
        VerifyAccess(); ThrowIfDisposed();
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            History.ObserveState(HistoryState(DateTimeOffset.UtcNow));
        }
        OnPropertyChanged(nameof(IsPaused));
    }

    private void ReleaseInterface()
    {
        if (_bus is not null && _receive is not null) _bus.TelegramReceived -= _receive;
        if (_bus is IMonitorConnectionNotifications notifications && _connectionChanged is not null) notifications.StateChanged -= _connectionChanged;
        _connectionChanged=null;
        _receive = null;
        _lease?.Dispose(); _lease = null;
    }

    /// <summary>Real adapter feed. Payload and metadata are copied immediately, never retained as mutable frames.</summary>
    public void IngestReal(BusTelegram telegram) => IngestReal(telegram, null);

    private void IngestReal(BusTelegram telegram, long? generation)
    {
        lock (_gate)
        {
            if (!_running || _disposed || Origin != SessionOrigin.Real ||
                (generation is not null && generation != _generation) || telegram?.Telegram is null) return;
            var frame = telegram.Telegram;
            var data = frame.Data?.ToArray() ?? [];
            var group = frame.IsGroupAddress && _groups.TryGetValue(frame.Destination.Raw, out var g)
                ? g : default;
            var (value, unit) = GroupMonitor.DecodeValue(group.Dpt, data);
            Append(new(telegram.Timestamp, frame.Source.ToString(), frame.IsGroupAddress
                ? GroupMonitor.GroupText(frame.Destination.Raw) : frame.Destination.ToString(),
                frame.Apci.ToString(), group.Dpt, value + (unit.Length == 0 ? "" : " " + unit), SessionOrigin.Real,
                group.Id, _devices.GetValueOrDefault(frame.Source.ToString()), Convert.ToHexString(data), frame.IsGroupAddress) {HopCount=frame.HopCount, DataSecurity=telegram.DataSecurity});
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
            Append(telegram with { DataSecurity = BusDataSecurity.Unverified });
        }
    }

    /// <summary>Appends a clearly marked simulated read response for a project group address.</summary>
    public WorkbenchTelegram SimulateRead(string groupId, string value, string? deviceId = null)
        => Simulate(groupId, value, deviceId, "GroupValueResponse");

    /// <summary>Appends a clearly marked simulated write; no transport is opened.</summary>
    public WorkbenchTelegram SimulateWrite(string groupId, string value, string? deviceId = null)
        => Simulate(groupId, value, deviceId, "GroupValueWrite");

    private WorkbenchTelegram Simulate(string groupId, string value, string? deviceId, string service)
    {
        VerifyAccess(); ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var project = _project() ?? throw new InvalidOperationException("No project is open.");
        var group = ProjectQuery.GroupAddresses(project).SingleOrDefault(g => g.Id == groupId)
            ?? throw new ArgumentException("Group address is not in the current project.", nameof(groupId));
        var source = deviceId is null ? "1.1.250" : ProjectQuery.DeviceList(project)
            .FirstOrDefault(d => d.Id == deviceId)?.Address.ToString() ?? "1.1.250";
        var telegram = new WorkbenchTelegram(DateTimeOffset.Now, source, group.Address.ToString(), service,
            group.DatapointType, value.Trim(), SessionOrigin.Demo, group.Id, deviceId);
        TickDemo(telegram);
        return telegram;
    }

    private void Append(WorkbenchTelegram telegram)
    {
        if (_paused) return;
        try { _writer.Write(CsvRow(telegram)); }
        catch (IOException ex)
        {
            _error = "Recording stopped: " + ex.Message;
            _running = false;
            History.ObserveState(HistoryState(DateTimeOffset.UtcNow));
            return;
        }
        _count++;
        _lastReceiveUtc=DateTimeOffset.UtcNow;
        History.Observe(telegram,HistoryState(_lastReceiveUtc));
        _tail.Enqueue(telegram);
        if (_tail.Count > DisplayCapacity) _tail.Dequeue();
    }

    public void PinFilter(MonitorFilter filter)
    {
        VerifyAccess(); ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(filter);
        if (!Enum.IsDefined(filter.Scope)) throw new ArgumentOutOfRangeException(nameof(filter));
        if (filter.Scope is MonitorScope.ComObject or MonitorScope.GroupAddress or MonitorScope.Room or MonitorScope.Device)
            ArgumentException.ThrowIfNullOrWhiteSpace(filter.Id);
        _follow = false; _filter = filter;
        OnPropertyChanged(nameof(FollowSelection)); OnPropertyChanged(nameof(Filter)); Refilter();
    }

    public void SetFollowSelection(bool follow, string? selectedComObjectId = null, string? selectedDeviceId = null)
    {
        VerifyAccess(); ThrowIfDisposed(); _follow = follow;
        OnPropertyChanged(nameof(FollowSelection));
        if (follow) SelectionChanged(selectedComObjectId, selectedDeviceId);
    }

    public void SelectionChanged(string? selectedComObjectId, string? selectedDeviceId = null)
    {
        VerifyAccess(); ThrowIfDisposed();
        if (!_follow) return;
        _filter = selectedComObjectId is null ? new(MonitorScope.None) : new(MonitorScope.ComObject, selectedComObjectId, selectedDeviceId);
        OnPropertyChanged(nameof(Filter)); Refilter();
    }

    public void FlushDisplay()
    {
        VerifyAccess(); ThrowIfDisposed();
        RefreshHistoryStatus();
        lock(_gate)
        {
            if(_running&&Origin==SessionOrigin.Real&&_bus?.IsConnected!=true)
            {
                _running=false;_paused=false;_generation++;
                // A source without notifications has no exact disconnect time. Confirm no later than its last observed receive.
                History.ObserveState(HistoryState(_lastReceiveUtc,false));
            }
        }
        if (!_running && _lease is not null)
        {
            ReleaseInterface();
            OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(LastError));
        }
        WorkbenchTelegram[] rows;
        lock (_gate)
        {
            if (_publishedCount == _count) return;
            rows = _tail.ToArray(); _publishedCount = _count;
        }
        _rows.Replace(rows); Refilter(); OnPropertyChanged(nameof(RecordedCount));
    }

    /// <summary>Call after project/link changes to reevaluate dynamic room/project/KO membership.</summary>
    public void RefreshFilter() { VerifyAccess(); ThrowIfDisposed(); RefreshMetadata(); Refilter(); }

    private void RefreshMetadata()
    {
        var project = _project();
        var groups = project is null ? [] : ProjectQuery.GroupAddresses(project)
            .GroupBy(g => g.Address.Raw).ToDictionary(g => g.Key, g => (g.First().Id, g.First().DatapointType));
        var devices = project is null ? [] : ProjectQuery.DeviceList(project)
            .GroupBy(d => d.Address.ToString()).ToDictionary(g => g.Key, g => g.First().Id);
        lock (_gate) { _groups = groups; _devices = devices; _historyProjectId=string.IsNullOrWhiteSpace(project?.Guid)?project?.Id:project.Guid; }
    }

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
            ids = _project() is { } p ? ProjectQuery.ComObjectRefs(p)
                .Where(o => o.Ref.Id == _filter.Id && (_filter.DeviceId is null || o.Device.Id == _filter.DeviceId))
                .SelectMany(o => o.Ref.Links).ToHashSet() : new HashSet<string>();
        else if (_filter.Scope == MonitorScope.Device)
            ids = _project() is { } p ? ProjectQuery.ComObjectRefs(p).Where(o => o.Device.Id == _filter.Id)
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
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            _writer.Flush();
            var position = _recording.Position;
            try { _recording.Position = 0; _recording.CopyTo(destination); }
            finally { _recording.Position = position; }
        }
    }

    /// <summary>Full disk-backed recording as text, including rows no longer in the display tail.</summary>
    public string ExportFullCsv()
    {
        VerifyAccess(); ThrowIfDisposed();
        using var buffer = new MemoryStream();
        ExportFullCsv(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string CsvRow(WorkbenchTelegram r) => Csv.Row(',',
        r.Timestamp.ToString("O", CultureInfo.InvariantCulture), r.Source, r.Destination, r.Service,
        r.DatapointType ?? "", r.Value, r.Origin.ToString(), r.GroupAddressId ?? "", r.DeviceId ?? "",
        r.DataHex) + "\r\n";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        try
        {
            lock (_gate)
            {
                _disposed = true; _running = false; _generation++;
                History.ObserveState(HistoryState(DateTimeOffset.UtcNow));
                try { ReleaseInterface(); }
                finally
                {
                    try { _writer.Dispose(); }
                    finally { _recording.Dispose(); }
                }
            }
        }
        finally { HistoryCompletion=History.DisposeAsync().AsTask(); }
        OnPropertyChanged(nameof(IsRunning));
    }
}
