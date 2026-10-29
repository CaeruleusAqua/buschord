using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus.Connections;

namespace Knx.App;

public sealed partial class M112CommissioningWindow
{
    private WorkbenchGroupSelection? _groupSelection;
    private System.Net.IPAddress? _selectedLocalAddress;
    private bool _unboundGroupPolicy;
    private readonly TextBlock _groupNotice = new() { Name = "M112GroupPolicy", TextWrapping = TextWrapping.Wrap };
    private readonly Button _removeGroup = new() { Name = "M112RemoveGroupPolicy", Classes = { "wb-button", "wb-quiet" } };
    private GroupStateConfirmationWindow? _groupConfirmationWindow;
    private CancellationTokenSource? _groupInputCancellation;
    private Task? _groupCloseOperation;
    private void InitializeGroupSelection(ReadOnlyConnectionOptions? options, StackPanel body)
    {
        _groupSelection = options?.GroupSelection; _unboundGroupPolicy = options?.Profile.GroupSecurity is not null && _groupSelection is null;
        _selectedLocalAddress = options?.Profile.LocalAddress;
        body.Children.Add(_groupNotice); body.Children.Add(_removeGroup);
        _removeGroup.Click += (_, _) =>
        {
            if (_loading || _session.IsBusy || _session.RecoveryRequired) return;
            ResetReview(); _groupSelection = null; _unboundGroupPolicy = false; RefreshState();
        };
    }
    private ReadOnlyConnectionOptions AttachGroupSelection(ReadOnlyConnectionOptions options)
    {
        if (_unboundGroupPolicy) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
        if (_groupSelection is not { } selection) return options;
        if (!selection.Matches(options.Profile)) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
        return new(selection.BoundProfile, selection);
    }
    private void RefreshGroupSelection(bool busy)
    {
        _groupNotice.IsVisible = _removeGroup.IsVisible = _groupSelection is not null || _unboundGroupPolicy;
        _groupNotice.Text = (_groupSelection?.Candidate.Id ?? "") + "\n" + GroupSecurityStrings.Get("GroupOnly");
        _removeGroup.Content = GroupSecurityStrings.Get("Remove"); AutomationProperties.SetName(_removeGroup, GroupSecurityStrings.Get("Remove"));
        _removeGroup.IsEnabled = !busy && !_session.RecoveryRequired;
    }
    private async Task<bool> PrepareGroupOperationAsync()
    {
        if (_groupSelection is not { } selection) { if (_unboundGroupPolicy) throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch); return true; }
        _ = Options(); // Refuses stale endpoint/keyring/PasswordSource changes before confirmation or I/O.
        var target = _session.Review?.Target ?? _session.Snapshot.Resume?.Identity.Address;
        if (target is not null && IndividualAddress.TryParse(target, out var address) && !selection.Candidate.PlainIndividualPeers.Contains(address))
            throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityPolicyDenied);
        if (!selection.CanStartNewOperation)
        {
            var dialog = _groupConfirmationWindow = new(selection);
            try { await dialog.ShowDialog(this); await dialog.OperationCompletion; if (_closing || dialog.Selection is not { } renewed) return false; _groupSelection = selection = renewed; }
            finally { _groupConfirmationWindow = null; }
        }
        var profile = await selection.PrepareNewOperationAsync(selection.BaseConnection, _groupInputCancellation?.Token ?? default);
        _session.RenewEquivalentGroupGrant(new(profile, selection)); return true;
    }
    private static string GroupOrConnectionFailure(string key) => GroupSecurityStrings.Contains(key) ? GroupSecurityStrings.Get(key) : SecureConnectionStrings.Get(key);
    private void StartInputOperation(Func<Task> action)
    {
        if (!_operation.IsCompleted || _closing || _closed) return;
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operation = owner.Task;
        var cancellation = _groupInputCancellation = new(); _groupCloseOperation = null; _ = CompleteInputAsync(action, cancellation, owner);
    }
    private async Task CompleteInputAsync(Func<Task> action, CancellationTokenSource cancellation, TaskCompletionSource owner)
    {
        try { await RunAsync(action); }
        finally { if (ReferenceEquals(_groupInputCancellation, cancellation)) _groupInputCancellation = null; cancellation.Dispose(); owner.TrySetResult(); if (!_closed) RefreshState(); }
    }
    private void CancelInputOperation()
    {
        try { _groupInputCancellation?.Cancel(); } catch (AggregateException) { } catch (ObjectDisposedException) { }
        _groupConfirmationWindow?.Close();
        try { _session.Cancel(); } catch (AggregateException) { }
    }
}
