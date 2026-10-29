using Knx.Bus.Commissioning;
using Knx.Bus.Connections;
using Knx.Workbench;

namespace Knx.App.Workbench;

/// <summary>One explicitly selected project target and its joined address operation. A changed project cannot receive a late real Adr flag.</summary>
public sealed class IndividualAddressAssignmentSession : SessionState, IAsyncDisposable
{
    private readonly Func<M112LiveProject> _current;
    private readonly ProgrammingSession? _programming;
    private readonly string _deviceId;
    private readonly Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>> _open;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IndividualAddressAssignment? _assignment;
    private AddressAssignmentReview? _coldReview;
    private string? _intentPath;
    private object? _projectLifetime;
    private Project? _project;
    private string? _modelHash, _configurationSignature, _roleBinding;
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private CancellationTokenSource? _cancellation;
    private bool _busy, _exitAcknowledged;
    public event Action? Changed;
    public IndividualAddressAssignmentSession(Func<M112LiveProject> current, ProgrammingSession? programming, string deviceId,
        Func<KnxConnectionProfile, CancellationToken, Task<IAddressAssignmentOperation>>? open = null)
    { _current = current; _programming = programming; _deviceId = deviceId; _open = open ?? new AddressAssignmentTransport().OpenAsync; }
    public string DeviceId => _deviceId;
    public bool IsBusy { get { lock (_gate) return _busy; } }
    public Task Completion { get { lock (_gate) return _operation; } }
    public AddressAssignmentReview? Review => _assignment?.Review ?? _coldReview;
    public AddressAssignmentResult? Result { get; private set; }
    public bool AddressFlagRecorded { get; private set; }
    public string? Issue { get; private set; }
    public string? IntentPath => Result?.IntentPath ?? _intentPath;
    public AddressAssignmentState State => _assignment?.State ?? (IsBusy ? AddressAssignmentState.Reconciling : Result?.State ?? AddressAssignmentState.Idle);
    public bool RecoveryRequired => Result?.RequiresReconciliation == true;
    public bool MayLeave => !IsBusy && (!RecoveryRequired || _exitAcknowledged);
    public bool HasProjectTarget => _current().Project is { } project && ProjectQuery.DeviceList(project).Any(device => device.Id == _deviceId);
    public IndividualAddress? ProjectTarget => _current().Project is { } project ? ProjectQuery.DeviceList(project).SingleOrDefault(device => device.Id == _deviceId)?.Address : null;
    public string? ProjectCouplerHardware => _current().Project is { } project && ProjectQuery.DeviceList(project).SingleOrDefault(device => device.Id == _deviceId) is { } device ? CouplerHardware(project, device) : null;
    private static string? CouplerHardware(Project project, DeviceInstance device)
    {
        var reference = device.Hardware2ProgramRefId is { Length: > 0 } program && project.Hardware.ContainsKey(program) ? program : device.ProductRefId;
        return project.Hardware.TryGetValue(reference, out var hardware) && hardware.IsCoupler ? reference + " / " + hardware.HardwareId : null;
    }
    private DeviceInstance BindCurrent(IndividualAddress target, AddressDeviceRole role, string? hardwareReference)
    {
        var live = _current();
        var device = live.Project is { } project ? ProjectQuery.DeviceList(project).SingleOrDefault(device => device.Id == _deviceId) : null;
        if (device is null || live.HasDrafts || live.ProgrammingBusy || device.Address != target) throw Refuse(AddressAssignmentFailure.StaleReview);
        if (ProjectQuery.DeviceList(live.Project!).Any(other => other.Id != device.Id && other.Address == target)) throw Refuse(AddressAssignmentFailure.ReservationRequired);
        if (live.Project!.Workbench.Devices.GetValueOrDefault(device.Id)?.SecureCapable == true) throw Refuse(AddressAssignmentFailure.UnsupportedSecureProvisioning);
        var actualRole = CouplerHardware(live.Project, device);
        if ((actualRole is not null) != (role == AddressDeviceRole.Coupler) || hardwareReference != actualRole) throw Refuse(AddressAssignmentFailure.InvalidSelection);
        _project = live.Project; _projectLifetime = live.SessionIdentity; _modelHash = ProjectRevisions.ContentHash(live.Project);
        _roleBinding = actualRole;
        _configurationSignature = DeviceConfigurationSnapshot.Freeze(live.Project, device).Signature; return device;
    }
    private bool IsCurrent()
    {
        var live = _current();
        return !live.HasDrafts && !live.ProgrammingBusy && ReferenceEquals(_project, live.Project) && ReferenceEquals(_projectLifetime, live.SessionIdentity)
            && live.Project is not null && _modelHash == ProjectRevisions.ContentHash(live.Project)
            && ProjectQuery.DeviceList(live.Project).SingleOrDefault(device => device.Id == _deviceId) is { } current
            && live.Project.Workbench.Devices.GetValueOrDefault(current.Id)?.SecureCapable != true && CouplerHardware(live.Project, current) == _roleBinding;
    }
    public Task ReviewAsync(AddressAssignmentRequest request, string journalDirectory) => Start(async token =>
    {
        if (RecoveryRequired) throw Refuse(AddressAssignmentFailure.RecoveryRequired);
        BindCurrent(request.NewAddress, request.TargetRole, request.CouplerHardwareReference); await ReleaseAssignmentAsync();
        _coldReview = null; _intentPath = null; Result = null; AddressFlagRecorded = false;
        var assignment = _assignment = new(request, journalDirectory, _open, IsCurrent); assignment.Changed += Notify;
        await assignment.ReviewAsync(token);
        if (!IsCurrent()) throw Refuse(AddressAssignmentFailure.StaleReview);
    });
    public Task ApplyAsync(string fingerprint, bool reserved, bool physical) => Start(async token =>
    {
        if (!IsCurrent()) throw Refuse(AddressAssignmentFailure.StaleReview);
        var assignment = _assignment ?? throw Refuse(AddressAssignmentFailure.StaleReview);
        if (assignment.Review?.Fingerprint == fingerprint && reserved && physical)
        { _programming?.InvalidateIndividualAddressEvidence(_deviceId); if (!IsCurrent()) throw Refuse(AddressAssignmentFailure.StaleReview); }
        Result = await assignment.ApplyAsync(fingerprint, reserved, physical, token);
        AddressFlagRecorded = Result.AddressVerified && IsCurrent() && _programming?.RecordVerifiedIndividualAddress(_deviceId, _configurationSignature!, Result) == true;
        if (!Result.AddressVerified && IsCurrent()) _programming?.RecordIndividualAddressFailure(_deviceId, Result);
        if (Result.AddressVerified && !AddressFlagRecorded) Issue = "VerifiedWithoutProjectFlag";
    });
    public Task InspectAsync(string intentPath) => Start(async token =>
    {
        if (RecoveryRequired) throw Refuse(AddressAssignmentFailure.RecoveryRequired);
        await ReleaseAssignmentAsync(); _coldReview = null; Result = null; AddressFlagRecorded = false;
        _coldReview = await IndividualAddressAssignment.InspectIntentAsync(intentPath, token); _intentPath = intentPath;
    });
    public Task ReconcileAsync(bool confirmPhysicalBinding, string? isolatedSetup = null) => Start(async token =>
    {
        var review = Review ?? throw Refuse(AddressAssignmentFailure.StaleReview);
        var path = IntentPath ?? throw Refuse(AddressAssignmentFailure.InvalidJournal);
        // Read-only recovery is independent of project freshness. Only flag projection needs the exact current selected target.
        var mayProject = false;
        if (confirmPhysicalBinding && IndividualAddress.TryParse(review.NewAddress, out var address))
            try { BindCurrent(address, review.TargetRole, review.CouplerHardwareReference); mayProject = true; } catch (AddressAssignmentException) { }
        if (mayProject) _programming?.InvalidateIndividualAddressEvidence(_deviceId);
        await ReleaseAssignmentAsync(); _coldReview = review; _intentPath = path;
        Result = await IndividualAddressAssignment.ReconcileAsync(path, confirmPhysicalBinding, isolatedSetup, _open, token, review.Fingerprint);
        AddressFlagRecorded = mayProject && Result.AddressVerified && IsCurrent() && _programming?.RecordVerifiedIndividualAddress(_deviceId, _configurationSignature!, Result) == true;
        if (Result.AddressVerified && !AddressFlagRecorded) Issue = "VerifiedWithoutProjectFlag";
    });
    public void AcknowledgeUnknownStateExit(bool acknowledged)
    { VerifyAccess(); if (IsBusy) throw Refuse(AddressAssignmentFailure.Occupied); _exitAcknowledged = acknowledged; Notify(); }
    public void Cancel() { lock (_gate) try { _cancellation?.Cancel(); } catch { } _assignment?.Cancel(); }
    private Task Start(Func<CancellationToken, Task> action)
    {
        VerifyAccess(); TaskCompletionSource completion; CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposal is not null) throw Refuse(AddressAssignmentFailure.Disposed);
            if (_busy) throw Refuse(AddressAssignmentFailure.Occupied);
            _busy = true; Issue = null; _exitAcknowledged = false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = completion.Task;
            cancellation = _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        Notify(); _ = RunAsync(action, cancellation, completion); return completion.Task;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Issue = "Cancelled"; }
        catch (AddressAssignmentException error) { Issue = error.Failure.ToString(); }
        catch (KnxConnectionException error) { Issue = error.Failure.ToString(); }
        catch { Issue = "TransportFailed"; }
        finally
        {
            lock (_gate) { _busy = false; if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; }
            cancellation.Dispose(); Notify(); completion.TrySetResult();
        }
    }
    private async Task ReleaseAssignmentAsync()
    {
        if (_assignment is { } assignment)
        { await assignment.DisposeAsync(); assignment.Changed -= Notify; if (ReferenceEquals(_assignment, assignment)) _assignment = null; }
    }
    private void Notify() { foreach (Action observer in Changed?.GetInvocationList() ?? []) try { observer(); } catch { } }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task result;
        lock (_gate) { if (_disposal is null || _disposal.IsFaulted) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; } result = _disposal; }
        if (completion is not null) { try { _lifetime.Cancel(); } catch { } finally { _ = FinishDisposeAsync(completion); } }
        return new(result);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    { try { await Completion; await ReleaseAssignmentAsync(); _lifetime.Dispose(); completion.TrySetResult(); } catch { completion.TrySetException(Refuse(AddressAssignmentFailure.TransportFailed)); } }
    private static AddressAssignmentException Refuse(AddressAssignmentFailure failure) => new(failure);
}
