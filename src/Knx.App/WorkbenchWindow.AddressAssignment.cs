using Avalonia.Controls;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Bus.Commissioning;
using Knx.Bus.Connections;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private IndividualAddressAssignmentSession? _addressAssignmentSession;
    private IndividualAddressAssignmentWindow? _addressAssignmentWindow;
    private Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>>? _addressAssignmentOpen = null;
    private bool _addressClosingOwner;
    private bool AddressAssignmentBusy => _addressAssignmentSession?.IsBusy == true;
    public Task AddressAssignmentCloseCompletion => _addressAssignmentSession?.Completion ?? Task.CompletedTask;
    private async Task ShowAddressAssignmentAsync()
    {
        if (_addressAssignmentWindow is { } existing) { existing.Activate(); return; }
        if (_m112Window is { } m112) { m112.Activate(); return; }
        EnsureSessions();
        if (HasParameterDrafts || _sessions?.Programming.IsRunning == true || _sessions?.LineScan.IsRunning == true || M112Busy)
        { ViewModel.Status = AddressAssignmentStrings.Get("Stale"); return; }
        var lifetime = ViewModel.ProjectSessionIdentity; var selected = ViewModel.SelectedDevice?.Device;
        var session = _addressAssignmentSession = new(() => new(ViewModel.Project, ViewModel.ProjectSessionIdentity, HasParameterDrafts,
            _sessions?.Programming.IsRunning == true || _sessions?.LineScan.IsRunning == true || M112Busy), _sessions?.Programming, selected?.Id ?? "", _addressAssignmentOpen);
        var window = _addressAssignmentWindow = new(session, _liveOptions);
        var others = _toolWindows.Concat(_companions.Values).Distinct().Select(other => (other, other.IsEnabled)).ToArray();
        foreach (var (other, _) in others) other.IsEnabled = false;
        try { await window.ShowDialog(this); await window.OperationCompletion; }
        finally
        {
            await session.DisposeAsync(); foreach (var (other, enabled) in others) other.IsEnabled = enabled;
            if (ReferenceEquals(_addressAssignmentSession, session)) _addressAssignmentSession = null;
            if (ReferenceEquals(_addressAssignmentWindow, window)) _addressAssignmentWindow = null;
            if (ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity)) ViewModel.Status = AddressAssignmentStrings.Get(session.RecoveryRequired ? "Unresolved" : session.AddressFlagRecorded ? "FlagRecorded" : session.Issue ?? session.State.ToString());
        }
    }
    private async Task DrainAddressAssignmentBeforeReplacementAsync()
    {
        var session = _addressAssignmentSession; var window = _addressAssignmentWindow;
        if (session is null) return;
        session.Cancel(); window?.Close(); await session.Completion;
        if (window is not null) await window.OperationCompletion;
        if (window is { MayClose: false }) { window.Activate(); throw new InvalidOperationException(AddressAssignmentStrings.Get("Unresolved")); }
    }
    private void TrackAddressAssignmentDisposal()
    {
        var session = _addressAssignmentSession; var window = _addressAssignmentWindow;
        if (session is null) return;
        session.Cancel(); window?.Close();
        var joined = FinishAddressAssignmentDisposalAsync(session, window);
        if (!joined.IsCompletedSuccessfully) _pendingSessionDisposals.Add(joined);
    }
    private static async Task FinishAddressAssignmentDisposalAsync(IndividualAddressAssignmentSession session, IndividualAddressAssignmentWindow? window)
    {
        await session.Completion;
        if (window is not null) await window.ClosedCompletion;
        await session.DisposeAsync();
    }
    private bool JoinAddressAssignmentBeforeOwnerClose(WindowClosingEventArgs args)
    {
        var window = _addressAssignmentWindow;
        if (!AddressAssignmentBusy && (window?.OperationCompletion.IsCompleted ?? true) && window is { MayClose: false })
        { args.Cancel = true; _shuttingDown = false; ViewModel.Status = AddressAssignmentStrings.Get("Unresolved"); window.Activate(); return true; }
        if (!AddressAssignmentBusy && (window?.OperationCompletion.IsCompleted ?? true)) return false;
        args.Cancel = true; if (_addressClosingOwner) return true; _addressClosingOwner = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var close = false;
            try { await DrainAddressAssignmentBeforeReplacementAsync(); close = true; }
            catch { _shuttingDown = false; ViewModel.Status = AddressAssignmentStrings.Get("Unresolved"); }
            finally { _addressClosingOwner = false; if (close) Close(); }
        }); return true;
    }
}
