using System.Security.Cryptography;
using Knx.Bus;

namespace Knx.App.Workbench;

public enum M112CommissioningPhase { Idle, Preparing, Reviewed, ReadingIdentity, Ready, ResumeReviewed, ReadingRecoveryIdentity, ResumeReady, Applying, Restoring, Verified, Restored, FailedRestored, RecoveryRequired, Failed, Cancelled }
public sealed record M112PhysicalIdentity(string Address, ushort MaskVersion, string Manufacturer, string Serial, string HardwareSha256);
public sealed record M112LiveProject(Project? Project, object SessionIdentity, bool HasDrafts, bool ProgrammingBusy);
public sealed record M112ReviewRange(ushort Address, int Length, string Baseline, string Desired, string Validity, bool RequiresDeviceRead);
public sealed record M112CommissioningSnapshot(M112CommissioningPhase Phase, string? Fingerprint = null,
    ManagementDeviceIdentity? Identity = null, bool IdentityConfirmed = false, M112DownloadResult? Result = null, string? Issue = null,
    M112RecoveryInspection? Resume = null, M112PhysicalIdentity? PhysicalIdentity = null);
public interface IM112PreparedChange
{
    string Target { get; }
    string Fingerprint { get; }
    string ExpectedManufacturer { get; }
    string ExpectedApplication { get; }
    IReadOnlyList<M112ReviewRange> Ranges { get; }
    bool IsCurrent();
}
public interface IM112CommissioningBackend
{
    Task<IM112PreparedChange> PrepareAsync(Project baseline, DeviceInstance baselineDevice, Project desired, DeviceInstance desiredDevice, CancellationToken token);
    Task<ManagementDeviceIdentity> ReadIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token);
    Task<M112RecoveryInspection> InspectRecoveryAsync(string path, CancellationToken token);
    Task<M112PhysicalIdentity> ReadPhysicalIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token);
    Task<M112DownloadResult> ApplyAsync(ReadOnlyConnectionOptions options, IM112PreparedChange review,
        ManagementDeviceIdentity expected, string fingerprint, string journalRoot, Action<string> progress, CancellationToken token);
    Task<M112DownloadResult> RecoverAsync(ReadOnlyConnectionOptions options, M112RecoveryInspection inspection,
        string target, Action<string> progress, CancellationToken token);
}

public sealed class M112CommissioningBackend : IM112CommissioningBackend
{
    private sealed class Prepared(M112ApplicationChangePlan plan) : IM112PreparedChange
    {
        public M112ApplicationChangePlan Plan { get; } = plan;
        public string Target => Plan.Desired.Address;
        public string Fingerprint => Plan.Fingerprint;
        public string ExpectedManufacturer => Plan.Desired.Manufacturer.ToString("X4");
        public string ExpectedApplication => ExpectedManufacturer + Plan.Desired.ApplicationNumber.ToString("X4") + Plan.Desired.ApplicationVersion.ToString("X2");
        public IReadOnlyList<M112ReviewRange> Ranges { get; } = Array.AsReadOnly(plan.Changes.Select(change => new M112ReviewRange(change.Address,
            change.Length, Convert.ToHexString(change.BaselineData.ToArray()), Convert.ToHexString(change.DesiredData.ToArray()),
            Convert.ToHexString(change.DesiredValidity.ToArray()), change.RequiresDeviceRead)).ToArray());
        public bool IsCurrent() => Plan.IsCurrent();
    }
    public async Task<IM112PreparedChange> PrepareAsync(Project baseline, DeviceInstance baselineDevice, Project desired, DeviceInstance desiredDevice, CancellationToken token)
        => await Task.Run<IM112PreparedChange>(() => { token.ThrowIfCancellationRequested(); return new Prepared(M112ApplicationChangePlan.Prepare(baseline, baselineDevice, desired, desiredDevice)); }, token);
    public async Task<ManagementDeviceIdentity> ReadIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token)
    {
        await using var connection = await KnxManagementConnection.OpenReadOnlyAsync(options.Gateway, target, options.RequestedAddress, ct: token);
        return await connection.Client.ReadIdentityAsync(token);
    }
    public Task<M112RecoveryInspection> InspectRecoveryAsync(string path, CancellationToken token)
        => M112ApplicationDownload.InspectRecoveryAsync(path, token);
    public async Task<M112PhysicalIdentity> ReadPhysicalIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token)
    {
        await using var connection = await KnxManagementConnection.OpenReadOnlyAsync(options.Gateway, target, options.RequestedAddress, ct: token);
        var descriptor = await connection.Client.ReadDescriptorAsync(token);
        var manufacturer = await connection.Client.ReadPropertyAsync(0, 12, ct: token);
        var serial = await connection.Client.ReadPropertyAsync(0, 11, ct: token);
        var hardware = await connection.Client.ReadPropertyAsync(0, 78, ct: token);
        if (descriptor.Length != 2 || manufacturer.Length != 2 || serial.Length != 6 || hardware.Length != 6) throw new IOException("Identity");
        return new(target.ToString(), (ushort)((descriptor[0] << 8) | descriptor[1]), Convert.ToHexString(manufacturer),
            Convert.ToHexString(serial), Convert.ToHexString(SHA256.HashData(hardware)));
    }
    public Task<M112DownloadResult> ApplyAsync(ReadOnlyConnectionOptions options, IM112PreparedChange review, ManagementDeviceIdentity expected,
        string fingerprint, string journalRoot, Action<string> progress, CancellationToken token)
        => M112ApplicationDownload.ApplyChangesAsync(ct => OpenAsync(options, expected.Address, ct),
            (review as Prepared ?? throw new ArgumentException("Unsupported prepared profile.")).Plan, fingerprint, expected, review.Target, journalRoot, progress, token);
    public Task<M112DownloadResult> RecoverAsync(ReadOnlyConnectionOptions options, M112RecoveryInspection inspection,
        string target, Action<string> progress, CancellationToken token)
        => M112ApplicationDownload.RecoverInspectedAsync(ct => OpenAsync(options, target, ct), inspection, target, progress, token);
    private static async Task<IManagementConnection> OpenAsync(ReadOnlyConnectionOptions options, string target, CancellationToken token)
        => await KnxManagementConnection.OpenAsync(options.Gateway, IndividualAddress.Parse(target), options.RequestedAddress, ct: token);
}

/// <summary>One explicit profile operation; no generic programming success flags or implicit connections.</summary>
public sealed class M112CommissioningSession : IAsyncDisposable
{
    private readonly Func<M112LiveProject> _current;
    private readonly IM112CommissioningBackend _backend;
    private readonly object _sync = new();
    private readonly string _deviceId;
    private readonly object _lifetime;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;
    private bool _busy, _disposed, _invalidated;
    private bool _recoveryRequired;
    private Project? _desired;
    private string? _modelHash, _baselinePath, _baselineHash, _journal;
    private ReadOnlyConnectionOptions? _options;
    private IM112PreparedChange? _review;
    private M112CommissioningSnapshot _snapshot = new(M112CommissioningPhase.Idle);
    public M112CommissioningSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public bool IsBusy { get { lock (_sync) return _busy; } }
    public bool RecoveryRequired { get { lock (_sync) return _recoveryRequired; } }
    public bool RecoveryOnly { get; }
    public IM112PreparedChange? Review => _review;
    public Task Completion { get { lock (_sync) return _operation; } }
    public event Action<M112CommissioningSnapshot>? Changed;
    public M112CommissioningSession(Func<M112LiveProject> current, string deviceId, IM112CommissioningBackend? backend = null, bool recoveryOnly = false)
    { _current = current; _deviceId = deviceId; _lifetime = current().SessionIdentity; _backend = backend ?? new M112CommissioningBackend(); RecoveryOnly = recoveryOnly; }
    public void Cancel() { lock (_sync) _cancellation?.Cancel(); }
    public void Invalidate() { lock (_sync) { _invalidated = true; _cancellation?.Cancel(); } }
    public void ClearReview()
    {
        if (IsBusy || RecoveryRequired) throw new InvalidOperationException(RecoveryRequired ? "RecoveryRequired" : "Busy");
        _review = null; _options = null; Publish(new(M112CommissioningPhase.Idle));
    }
    public Task PrepareAsync(Project baseline, string baselinePath, string baselineHash, ReadOnlyConnectionOptions options, string journalRoot)
        => Start(async token =>
        {
            if (RecoveryOnly) throw new InvalidOperationException("Target");
            _review = null; Publish(new(M112CommissioningPhase.Preparing));
            var live = _current();
            if (ReferenceEquals(_lifetime, live.SessionIdentity)) _invalidated = false;
            RequireLive(live);
            _desired = live.Project!; _modelHash = ProjectRevisions.ContentHash(live.Project!);
            _baselinePath = M112PrivatePaths.ExistingFile(baselinePath); _baselineHash = baselineHash;
            _journal = M112PrivatePaths.ExistingDirectory(journalRoot); _options = options;
            var desiredDevice = Device(live.Project!);
            if (desiredDevice.Address.Device is 0 or 255 || desiredDevice.Address == options.RequestedAddress) throw new InvalidOperationException("Target");
            var baselineDevice = ProjectQuery.DeviceList(baseline).Single(device => device.Address == desiredDevice.Address);
            await VerifyBaselineAsync(token);
            var review = await _backend.PrepareAsync(baseline, baselineDevice, live.Project!, desiredDevice, token);
            VerifyCurrent(); await VerifyBaselineAsync(token); VerifyCurrent();
            if (!review.IsCurrent() || review.Target != desiredDevice.Address.ToString()) throw new InvalidOperationException("Stale");
            _review = review; Publish(new(M112CommissioningPhase.Reviewed, review.Fingerprint));
        });
    public Task ReadIdentityAsync() => Start(async token =>
    {
        var review = RequireReview(); VerifyCurrent();
        Publish(new(M112CommissioningPhase.ReadingIdentity, review.Fingerprint));
        using var lease = AcquireEndpoint();
        var identity = await _backend.ReadIdentityAsync(_options!, IndividualAddress.Parse(review.Target), token);
        token.ThrowIfCancellationRequested(); VerifyCurrent(); ValidateIdentity(identity, review);
        Publish(new(M112CommissioningPhase.Ready, review.Fingerprint, identity));
    });
    public void ConfirmIdentity(bool confirmed)
    {
        if (IsBusy || Snapshot.Phase is not (M112CommissioningPhase.Ready or M112CommissioningPhase.ResumeReady)) throw new InvalidOperationException("Review");
        if (Snapshot.Phase == M112CommissioningPhase.Ready) VerifyCurrent();
        else VerifyRecoveryScope(Snapshot.Resume ?? throw new InvalidOperationException("Review"));
        Publish(Snapshot with { IdentityConfirmed = confirmed });
    }
    public Task ApplyAsync(string confirmedTarget, string confirmedFingerprint) => Start(async token =>
    {
        var review = RequireReview(); var snapshot = Snapshot;
        if (snapshot.Phase != M112CommissioningPhase.Ready || !snapshot.IdentityConfirmed || snapshot.Identity is null
            || confirmedTarget != review.Target || confirmedFingerprint != review.Fingerprint) throw new InvalidOperationException("Confirmation");
        VerifyCurrent(); await VerifyBaselineAsync(token); VerifyCurrent();
        if (!review.IsCurrent()) throw new InvalidOperationException("Stale");
        using var lease = AcquireEndpoint();
        Publish(snapshot with { Phase = M112CommissioningPhase.Applying });
        try { Complete(await _backend.ApplyAsync(_options!, review, snapshot.Identity, confirmedFingerprint, _journal!, Progress, token)); }
        catch { lock (_sync) _recoveryRequired = true; throw; }
    }, TimeSpan.FromMinutes(10));
    public Task PrepareRecoveryAsync(string explicitlySelectedPath, ReadOnlyConnectionOptions options) => Start(async token =>
    {
        var path = M112PrivatePaths.ExistingFile(explicitlySelectedPath);
        var inspection = await _backend.InspectRecoveryAsync(path, token);
        VerifyRecoveryScope(inspection);
        _options = options; _review = null;
        Publish(new(M112CommissioningPhase.ResumeReviewed, inspection.FileSha256, inspection.Identity, Result: Snapshot.Result, Resume: inspection));
    }, allowRecovery: true);
    public Task ReadRecoveryIdentityAsync() => Start(async token =>
    {
        var inspection = Snapshot.Resume ?? throw new InvalidOperationException("Review");
        VerifyRecoveryScope(inspection);
        Publish(Snapshot with { Phase = M112CommissioningPhase.ReadingRecoveryIdentity, IdentityConfirmed = false });
        using var lease = AcquireEndpoint();
        var physical = await _backend.ReadPhysicalIdentityAsync(_options!, IndividualAddress.Parse(inspection.Identity.Address), token);
        token.ThrowIfCancellationRequested(); VerifyRecoveryScope(inspection);
        if (physical.Address != inspection.Identity.Address || physical.MaskVersion != inspection.Identity.MaskVersion
            || physical.Manufacturer != inspection.Identity.Manufacturer || physical.Serial != inspection.Identity.Serial || physical.HardwareSha256 != inspection.HardwareSha256)
            throw new InvalidOperationException("Identity");
        Publish(Snapshot with { Phase = M112CommissioningPhase.ResumeReady, PhysicalIdentity = physical });
    }, allowRecovery: true);
    public Task RecoverAsync(string confirmedTarget, string confirmedFileFingerprint) => Start(async _ =>
    {
        var inspection = Snapshot.Resume ?? throw new InvalidOperationException("Review");
        if (Snapshot.Phase != M112CommissioningPhase.ResumeReady || !Snapshot.IdentityConfirmed || Snapshot.PhysicalIdentity is null
            || inspection.Identity.Address != confirmedTarget || confirmedFileFingerprint != inspection.FileSha256) throw new InvalidOperationException("Confirmation");
        VerifyRecoveryScope(inspection);
        using var lease = AcquireEndpoint();
        Publish(Snapshot with { Phase = M112CommissioningPhase.Restoring });
        // Once explicit recovery starts, caller cancellation cannot cut its restoration short.
        using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Complete(await _backend.RecoverAsync(_options!, inspection, confirmedTarget, Progress, recovery.Token));
    }, allowRecovery: true);
    private IDisposable AcquireEndpoint() => RealEndpointReservations.TryAcquire(_options!.EndpointId) ?? throw new InvalidOperationException("Occupied");
    private void VerifyRecoveryScope(M112RecoveryInspection inspection)
    {
        var live = _current();
        if (!ReferenceEquals(_lifetime, live.SessionIdentity) || !RecoveryOnly && Device(live.Project ?? throw new InvalidOperationException("Target")).Address.ToString() != inspection.Identity.Address || live.ProgrammingBusy)
            throw new InvalidOperationException("Target");
    }
    private IM112PreparedChange RequireReview() => _review ?? throw new InvalidOperationException("Review");
    private DeviceInstance Device(Project project) => ProjectQuery.DeviceList(project).Single(device => device.Id == _deviceId);
    private void RequireLive(M112LiveProject live)
    {
        if (_invalidated || live.Project is null || !ReferenceEquals(_lifetime, live.SessionIdentity) || live.HasDrafts || live.ProgrammingBusy) throw new InvalidOperationException("Stale");
    }
    private void VerifyCurrent()
    {
        var live = _current(); RequireLive(live);
        if (!ReferenceEquals(_desired, live.Project) || _modelHash != ProjectRevisions.ContentHash(live.Project!)) throw new InvalidOperationException("Stale");
    }
    private async Task VerifyBaselineAsync(CancellationToken token)
    { if (await M112PrivatePaths.HashAsync(_baselinePath!, token) != _baselineHash) throw new IOException("SourceChanged"); }
    private static void ValidateIdentity(ManagementDeviceIdentity identity, IM112PreparedChange review)
    {
        if (identity.Address != review.Target || identity.MaskVersion != 0x0701 || identity.Manufacturer != review.ExpectedManufacturer
            || identity.Application != review.ExpectedApplication || identity.Serial.Length != 12 || !identity.Serial.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Identity");
    }
    private void Progress(string phase)
    {
        if (phase.Contains("restor", StringComparison.OrdinalIgnoreCase) || phase.Contains("recover", StringComparison.OrdinalIgnoreCase))
            Publish(Snapshot with { Phase = M112CommissioningPhase.Restoring });
    }
    private void Complete(M112DownloadResult result)
    {
        lock (_sync) _recoveryRequired = !result.OriginalConfigurationRestoredAndVerified && !result.DownloadedAndVerified
            && (_recoveryRequired || result.RollbackAttempted);
        var phase = result.DownloadedAndVerified && result.Error is null ? M112CommissioningPhase.Verified
            : result.OriginalConfigurationRestoredAndVerified && result.Error is null ? M112CommissioningPhase.Restored
            : result.OriginalConfigurationRestoredAndVerified ? M112CommissioningPhase.FailedRestored
            : RecoveryRequired ? M112CommissioningPhase.RecoveryRequired : M112CommissioningPhase.Failed;
        Publish(Snapshot with { Phase = phase, Result = result, Issue = result.Error is null ? null : "OperationFailed" });
    }
    private Task Start(Func<CancellationToken, Task> operation, TimeSpan? timeout = null, bool allowRecovery = false)
    {
        lock (_sync)
        {
            if (_disposed || _busy) throw new InvalidOperationException("Busy");
            if (_recoveryRequired && !allowRecovery) throw new InvalidOperationException("RecoveryRequired");
            _busy = true; _cancellation = new(timeout ?? TimeSpan.FromMinutes(2));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = completion.Task;
            _ = RunOwnedAsync(operation, _cancellation, completion);
            return completion.Task;
        }
    }
    private async Task RunOwnedAsync(Func<CancellationToken, Task> operation, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try { await RunAsync(operation, cancellation); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationTokenSource cancellation)
    {
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { Publish(Snapshot with { Phase = RecoveryRequired ? M112CommissioningPhase.RecoveryRequired : M112CommissioningPhase.Cancelled, Issue = "Cancelled" }); }
        catch (Exception error)
        {
            var issue = error.Message is "Stale" or "SourceChanged" or "Identity" or "Confirmation" or "Occupied" or "Target" ? error.Message : "OperationFailed";
            Publish(Snapshot with { Phase = RecoveryRequired ? M112CommissioningPhase.RecoveryRequired : M112CommissioningPhase.Failed, Issue = issue });
        }
        finally { lock (_sync) { _busy = false; _cancellation = null; } cancellation.Dispose(); Publish(Snapshot); }
    }
    private void Publish(M112CommissioningSnapshot snapshot)
    {
        lock (_sync) _snapshot = snapshot;
        if (Changed is { } handlers) foreach (Action<M112CommissioningSnapshot> callback in handlers.GetInvocationList()) try { callback(snapshot); } catch { }
    }
    public async ValueTask DisposeAsync()
    { Task pending; lock (_sync) { _disposed = true; _cancellation?.Cancel(); pending = _operation; } await pending; }
}

public static class M112PrivatePaths
{
    public static string ExistingFile(string path) => Validate(path, false);
    public static string ExistingDirectory(string path) => Validate(path, true);
    private static string Validate(string path, bool directory)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("PrivatePath");
        path = Path.GetFullPath(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new IOException("PrivatePath");
        for (var entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(Path.Combine(entry, ".git")) || File.Exists(Path.Combine(entry, ".git")))
                throw new IOException("PrivatePath");
        return path;
    }
    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        path = ExistingFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > 134217728) throw new IOException("PrivatePath");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
