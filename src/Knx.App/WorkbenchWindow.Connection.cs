using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Bus;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private readonly Func<ReadOnlyConnectionOptions, IReadOnlyWorkbenchBackend>? _realConnectionFactory;
    private ReadOnlyWorkbenchConnection? _liveConnection;
    private ReadOnlyConnectionOptions? _liveOptions;
    private ConnectionWindow? _connectionWindow;
    private bool _realMonitorSelected;
    private bool _realScanSelected;
    private readonly List<ReadOnlyWorkbenchConnection> _closingConnections = [];
    internal ReadOnlyWorkbenchConnection? LiveConnection => _liveConnection;
    public Task ConnectionCloseCompletion => Task.WhenAll(_pendingSessionDisposals);

    private void EnsureLiveConnection()
    {
        if (_liveConnection is not null || ViewModel.Project is null) return;
        var lifetime = ViewModel.ProjectSessionIdentity;
        var connection = _liveConnection = new(lifetime, () => ViewModel.ProjectSessionIdentity, _realConnectionFactory);
        connection.Changed += snapshot => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_liveConnection, connection) || !ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity)
                || snapshot.Generation != connection.Snapshot.Generation || _shuttingDown) return;
            ViewModel.BusName = snapshot.EndpointId ?? "";
            ViewModel.BusState = snapshot.Phase switch
            {
                ReadOnlyConnectionPhase.Connected => BusConnectionState.Connected,
                ReadOnlyConnectionPhase.Connecting => BusConnectionState.Connecting,
                ReadOnlyConnectionPhase.Disconnecting => BusConnectionState.Closing,
                _ => BusConnectionState.Disconnected
            };
            if (connection.IsConnected && _sessions is { } sessions)
            {
                _realMonitorSelected = true;
                if (sessions.Monitor.Origin != SessionOrigin.Real)
                { sessions.Monitor.Stop(); sessions.Monitor.ConfigureReal(snapshot.EndpointId!, connection); }
            }
            if (!connection.IsConnected && _sessions?.Monitor.Origin == SessionOrigin.Real)
                _sessions.Monitor.Stop();
            ViewModel.Status = ConnectionWindow.Status(snapshot);
            UpdateConnectionChrome();
            if (Workspace.ToolsVisible && Workspace.ActiveTool == "monitor") ShowTool("monitor");
        });
    }
    private void UpdateConnectionChrome()
    {
        var connected = _liveConnection?.IsConnected == true;
        var connecting = _liveConnection?.Snapshot.Phase == ReadOnlyConnectionPhase.Connecting;
        var authenticated = connected && _liveConnection?.Snapshot.GatewayAuthenticated == true;
        ConnectionBadge.Text = authenticated ? SecureConnectionStrings.Get("Authenticated") : LiveConnectionStrings.Get(connected ? "ReadOnly" : connecting ? "Connecting" : "Disconnected");
        ConnectionFooter.Text = authenticated ? SecureConnectionStrings.Get("Authenticated") : connected ? LiveConnectionStrings.Get("ReadOnly")
            : connecting ? LiveConnectionStrings.Get("Connecting") : W("StatusLocalOnly");
    }
    private async Task ShowConnectionAsync()
    {
        if (ViewModel.Project is null) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var lifetime = ViewModel.ProjectSessionIdentity;
        try { await Task.WhenAll(_pendingSessionDisposals.ToArray()); }
        catch { if (_closingConnections.Count == 0) throw; }
        _pendingSessionDisposals.RemoveAll(task => task.IsCompleted);
        foreach (var closing in _closingConnections.ToArray()) { await closing.DisposeAsync(); _closingConnections.Remove(closing); }
        if (!ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity) || _shuttingDown) return;
        EnsureSessions(); EnsureLiveConnection();
        if (_liveConnection is null) return;
        if (_connectionWindow is not null) { _connectionWindow.Activate(); return; }
        var connection = _liveConnection;
        var window = _connectionWindow = new(connection, _liveOptions, ViewModel.Project?.Name ?? "");
        try
        {
            await window.ShowDialog(this);
            await window.OperationCompletion;
            if (ReferenceEquals(connection, _liveConnection) && ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity))
                _liveOptions = window.Options;
        }
        finally { if (ReferenceEquals(_connectionWindow, window)) _connectionWindow = null; }
    }
    private void ConfigureMonitorSource(WorkbenchSessions sessions, bool real, WorkbenchMonitorMode mode)
    {
        if (sessions.Monitor.IsRunning) sessions.Monitor.Stop();
        if (real)
        {
            if (mode != WorkbenchMonitorMode.Group) throw new NotSupportedException(LiveConnectionStrings.Get("GroupOnly"));
            EnsureLiveConnection();
            if (_liveConnection?.IsConnected != true) throw new InvalidOperationException(LiveConnectionStrings.Get("Disconnected"));
            sessions.Monitor.ConfigureReal(_liveOptions?.EndpointId ?? _liveConnection.Snapshot.EndpointId!, _liveConnection);
        }
        else sessions.Monitor.Configure(mode, SessionOrigin.Demo, "demo-ip");
    }
}
