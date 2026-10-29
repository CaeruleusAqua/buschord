using Avalonia.Controls;
using Knx.App.Services;
using Knx.App.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private GroupStateConfirmationWindow? _groupOperationDialog;
    private Task _groupInputOperation = Task.CompletedTask;
    private CancellationTokenSource? _groupInputCancellation;
    public Task GroupSecurityCloseCompletion => JoinGroupInputAsync(_groupInputOperation);
    private Task<ReadOnlyConnectionOptions?> PrepareGroupOptionsForOperationAsync(ReadOnlyConnectionOptions options)
    {
        if (options.GroupSelection is null) return Task.FromResult<ReadOnlyConnectionOptions?>(options);
        if (!_groupInputOperation.IsCompleted) throw new Knx.Bus.Connections.KnxConnectionException(Knx.Bus.Connections.KnxConnectionFailure.Occupied);
        var owner = new TaskCompletionSource<ReadOnlyConnectionOptions?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _groupInputOperation = owner.Task; var cancellation = _groupInputCancellation = new();
        var lifetime = ViewModel.ProjectSessionIdentity; _ = PrepareGroupOptionsCoreAsync(options, lifetime, cancellation, owner); return owner.Task;
    }
    private async Task PrepareGroupOptionsCoreAsync(ReadOnlyConnectionOptions options, object lifetime, CancellationTokenSource cancellation, TaskCompletionSource<ReadOnlyConnectionOptions?> owner)
    {
        try
        {
            var selection = options.GroupSelection!;
            if (!selection.CanStartNewOperation)
            {
                var dialog = _groupOperationDialog = new(selection);
                try { await dialog.ShowDialog(this); await dialog.OperationCompletion; if (dialog.Selection is not { } renewed) { owner.TrySetResult(null); return; } selection = renewed; }
                finally { _groupOperationDialog = null; }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var profile = await selection.PrepareNewOperationAsync(selection.BaseConnection, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_shuttingDown || !ReferenceEquals(lifetime, ViewModel.ProjectSessionIdentity) || !ReferenceEquals(options, _liveOptions)) throw new OperationCanceledException(cancellation.Token);
            owner.TrySetResult(new(profile, selection));
        }
        catch (OperationCanceledException) { owner.TrySetCanceled(); }
        catch (Exception error) { owner.TrySetException(error); }
        finally { if (ReferenceEquals(_groupInputCancellation, cancellation)) _groupInputCancellation = null; cancellation.Dispose(); }
    }
    private void TrackGroupInputDisposal()
    {
        try { _groupInputCancellation?.Cancel(); } catch (AggregateException) { } catch (ObjectDisposedException) { }
        _groupOperationDialog?.Close();
        if (!_groupInputOperation.IsCompleted) _pendingSessionDisposals.Add(JoinGroupInputAsync(_groupInputOperation));
    }
    private static async Task JoinGroupInputAsync(Task operation)
    { try { await operation; } catch { /* Join resource ownership; the initiating view reports the typed operation outcome. */ } }
}
