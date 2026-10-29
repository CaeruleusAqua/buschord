using System.Text.Json;
using System.Text.Json.Nodes;
using Knx.Bus;
using Knx.Bus.Commissioning;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private IndividualAddressAssignment? _addressAssignment;
    private readonly object _addressCommandGate = new();
    private Task _addressCommand = Task.CompletedTask;
    private CancellationTokenSource? _addressCommandCancellation;
    private Guid _addressCommandId;
    private object? _addressCommandPayload;
    private string? _addressCommandFailure;
    private List<McpTool> BuildAddressAssignmentTools() =>
    [
        new("address_assignment_review", "Read-only review of one explicitly bound plain-device individual address assignment. Silence is not address availability. " +
            "Requires exact serial or an explicitly isolated single-device setup, a project reservation and private journal directory. Returns an accepted operationId; poll address_assignment_status. Holds one gateway owner until cancel/replacement/close. Secure provisioning is refused.",
            Schema(StrProp("mode", "Must be explicit plain_udp.", true), StrProp("gateway", "Explicit gateway IPv4.", true), IntProp("port", "Gateway UDP port, default3671."),
                StrProp("own_address", "Exact tunnel/client IA.", true), StrProp("interface", "Optional explicit local IPv4."), StrProp("new_address", "Exact operator-reserved target IA.", true),
                EnumProp("selection", "Physical binding; a single observed IA never proves one physical device.", ["serial", "isolated_programming_mode"], true),
                StrProp("serial", "Exact complete twelve-hex-digit serial for serial mode."), StrProp("isolated_setup", "Named physically isolated one-device setup; mandatory for programming mode."),
                EnumProp("target_role", "Explicit topology role; zero device numbers require a coupler hardware reference.", ["end_device", "coupler"], true),
                BoolProp("confirm_coupler_hardware", "Explicitly attest the named manufacturer/project hardware is a coupler."), StrProp("coupler_hardware_reference", "Exact named hardware evidence for coupler role; project hardware reference / HardwareId when a project target exists."),
                StrProp("reservation", "Explicit project/operator address-reservation reference, not a silence-derived claim.", true), BoolProp("confirm_plain_device", "Explicitly attest the device does not require Secure broadcast commissioning.", true),
                BoolProp("requires_secure_provisioning", "True refuses before any I/O; no Tool/P2P fallback."), IntProp("observation_ms", "Bounded collection window20..3000ms, default750."), StrProp("journal_directory", "Existing absolute private outside-Git directory.", true)), AddressAssignmentReviewTool),
        new("address_assignment_apply", "Apply the exact retained review once after explicit physical and target-reservation confirmation. Durable intent/attempt precede one no-retry address write. " +
            "Returns an accepted operationId; poll status. Fresh verification/reconciliation joins cancellation. No blind resend, rollback or generic programming permission.",
            Schema(StrProp("review_fingerprint", "Exact complete reviewed fingerprint.", true), StrProp("confirm_target_address", "Repeat the exact reserved new IA.", true),
                BoolProp("confirm_reserved_target", "Confirm independent address reservation despite NoResponse uncertainty.", true), BoolProp("confirm_physical_binding", "Confirm the exact serial/continuously isolated device.", true)), AddressAssignmentApplyTool),
        new("address_assignment_status", "Poll the exact accepted address operation. Incoming verification and durable outcome are facts only after completion; no writes are performed.",
            Schema(StrProp("operation_id", "Exact accepted operationId.", true)), Sync(AddressAssignmentStatusTool)),
        new("address_assignment_cancel", "Cancel and join the exact accepted address operation and release its gateway owner. Reachable while the standard stdio host processes the background job. An attempted write is never repeated.",
            Schema(StrProp("operation_id", "Exact accepted operationId.", true)), AddressAssignmentCancelTool),
        new("address_assignment_inspect", "Offline inspect a private durable address intent; provides no write capability or successful device verification.", Schema(StrProp("intent_path", "Absolute private intent.json.", true)), AddressAssignmentInspectTool),
        new("address_assignment_reconcile", "Cold resume through fresh READS ONLY. Never writes, even when the attempt marker is absent. " +
            "Isolated mode needs a new explicit declaration of the exact same physical setup. Returning to an old address requires a separate new review.",
            Schema(StrProp("intent_path", "Exact inspected private intent.json.", true), StrProp("review_fingerprint", "Exact inspected review fingerprint.", true),
                BoolProp("confirm_same_isolated_device", "Explicitly confirm continuity of the original isolated physical device."), StrProp("isolated_setup", "Repeat the exact original isolated setup.")), AddressAssignmentReconcileTool)
    ];
    private static void AddressArguments(JsonElement args, params string[] allowed)
    {
        RefuseSecretArguments(args);
        if (args.ValueKind != JsonValueKind.Object) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in args.EnumerateObject())
        {
            if (!names.Add(item.Name) || !allowed.Contains(item.Name)) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
            var valid = item.Name switch
            {
                "confirm_plain_device" or "requires_secure_provisioning" or "confirm_coupler_hardware" or "confirm_reserved_target" or "confirm_physical_binding" or "confirm_same_isolated_device"
                    => item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "port" or "observation_ms" => item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out _),
                _ => item.Value.ValueKind == JsonValueKind.String
            };
            if (!valid) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        }
    }
    private Task<object> AddressAssignmentReviewTool(JsonElement args)
    {
        AddressArguments(args, "mode", "gateway", "port", "own_address", "interface", "new_address", "selection", "serial", "isolated_setup", "reservation", "confirm_plain_device", "requires_secure_provisioning", "observation_ms", "journal_directory", "target_role", "confirm_coupler_hardware", "coupler_hardware_reference");
        if (Str(args, "mode") != "plain_udp" || Str(args, "own_address") is null) throw new AddressAssignmentException(AddressAssignmentFailure.UnsupportedSecureProvisioning);
        var profile = ConnectionInput.Explicit(args, default);
        var selection = Str(args, "selection") switch { "serial" => AddressSelectionKind.ExactSerial, "isolated_programming_mode" => AddressSelectionKind.IsolatedProgrammingMode, _ => throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection) };
        if (!IndividualAddress.TryParse(Required(args, "new_address"), out var target) || target.ToString() != Required(args, "new_address")) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        var role = Str(args, "target_role") switch { "end_device" => AddressDeviceRole.EndDevice, "coupler" => AddressDeviceRole.Coupler, _ => throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection) };
        var request = new AddressAssignmentRequest(profile, target, selection, Required(args, "reservation"), Str(args, "serial"), Str(args, "isolated_setup"),
            Bool(args, "confirm_plain_device"), Bool(args, "requires_secure_provisioning"), TimeSpan.FromMilliseconds(Int(args, "observation_ms") ?? 750),
            role, Bool(args, "confirm_coupler_hardware"), Str(args, "coupler_hardware_reference"));
        var hostProject = _project; var hostPin = AddressHostBinding(request);
        var journal = Required(args, "journal_directory");
        return Task.FromResult(StartAddressCommand(async ct =>
        {
            using var job = await EnterAddressConnectionJobAsync(ct).ConfigureAwait(false); await CloseAddressAssignmentAsync().ConfigureAwait(false);
            var assignment = _addressAssignment = new(request, journal, new AddressAssignmentTransport(_connections).OpenAsync, () =>
            { try { return ReferenceEquals(hostProject, _project) && hostPin == AddressHostBinding(request); } catch { return false; } });
            try { return await assignment.ReviewAsync(job.Token).ConfigureAwait(false); }
            catch { await CloseAddressAssignmentAsync().ConfigureAwait(false); throw; }
        }));
    }
    private Task<object> AddressAssignmentApplyTool(JsonElement args)
    {
        AddressArguments(args, "review_fingerprint", "confirm_target_address", "confirm_reserved_target", "confirm_physical_binding");
        var current = _addressAssignment ?? throw new AddressAssignmentException(AddressAssignmentFailure.StaleReview);
        _ = AddressHostBinding(current.Request);
        if (Str(args, "confirm_target_address") != current.Review?.NewAddress) throw new AddressAssignmentException(AddressAssignmentFailure.ReservationRequired);
        var fingerprint = Required(args, "review_fingerprint"); var reserved = Bool(args, "confirm_reserved_target"); var physical = Bool(args, "confirm_physical_binding");
        return Task.FromResult(StartAddressCommand(async ct =>
        {
            using var job = await EnterAddressConnectionJobAsync(ct).ConfigureAwait(false);
            if (!ReferenceEquals(current, _addressAssignment)) throw new AddressAssignmentException(AddressAssignmentFailure.StaleReview);
            return await current.ApplyAsync(fingerprint, reserved, physical, job.Token).ConfigureAwait(false);
        }));
    }
    private async Task<object> AddressAssignmentCancelTool(JsonElement args)
    {
        AddressArguments(args, "operation_id"); var id = AddressOperationId(args); Task pending; IndividualAddressAssignment? owner;
        lock (_addressCommandGate)
        {
            if (id != _addressCommandId) throw new AddressAssignmentException(AddressAssignmentFailure.StaleReview);
            pending = _addressCommand; owner = _addressAssignment; try { _addressCommandCancellation?.Cancel(); } catch { }
        }
        owner?.Cancel(); await pending.ConfigureAwait(false);
        using var job = await EnterConnectionJobAsync();
        bool same; lock (_addressCommandGate) same = id == _addressCommandId;
        if (same) await CloseAddressAssignmentAsync().ConfigureAwait(false);
        return AddressCommandSnapshot(id);
    }
    private async Task<object> AddressAssignmentInspectTool(JsonElement args)
    {
        AddressArguments(args, "intent_path"); using var job = await EnterConnectionJobAsync();
        return await IndividualAddressAssignment.InspectIntentAsync(Required(args, "intent_path"), job.Token).ConfigureAwait(false);
    }
    private Task<object> AddressAssignmentReconcileTool(JsonElement args)
    {
        AddressArguments(args, "intent_path", "review_fingerprint", "confirm_same_isolated_device", "isolated_setup");
        var path = Required(args, "intent_path"); var fingerprint = Required(args, "review_fingerprint"); var confirmed = Bool(args, "confirm_same_isolated_device"); var isolation = Str(args, "isolated_setup");
        return Task.FromResult(StartAddressCommand(async ct =>
        {
            using var job = await EnterAddressConnectionJobAsync(ct).ConfigureAwait(false);
            await CloseAddressAssignmentAsync().ConfigureAwait(false);
            return await IndividualAddressAssignment.ReconcileAsync(path, confirmed, isolation, new AddressAssignmentTransport(_connections).OpenAsync, job.Token, fingerprint).ConfigureAwait(false);
        }));
    }
    private static Guid AddressOperationId(JsonElement args) => Guid.TryParseExact(Required(args, "operation_id"), "D", out var id) && id != Guid.Empty
        ? id : throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
    private object AddressAssignmentStatusTool(JsonElement args)
    { AddressArguments(args, "operation_id"); return AddressCommandSnapshot(AddressOperationId(args)); }
    private object AddressCommandSnapshot(Guid id)
    {
        lock (_addressCommandGate)
        {
            if (id != _addressCommandId) throw new AddressAssignmentException(AddressAssignmentFailure.StaleReview);
            return new { operationId = id, busy = !_addressCommand.IsCompleted, failure = _addressCommandFailure, ownerHeld = _addressAssignment is not null,
                state = _addressAssignment?.State.ToString() ?? (_addressCommandPayload as AddressAssignmentResult)?.State.ToString(), payload = _addressCommandPayload };
        }
    }
    private object StartAddressCommand(Func<CancellationToken, Task<object>> action)
    {
        _requestCancellation.Value.ThrowIfCancellationRequested(); TaskCompletionSource completion; CancellationTokenSource cancellation; Guid id;
        lock (_addressCommandGate)
        {
            if (_connectionLifetime.IsCancellationRequested) throw new AddressAssignmentException(AddressAssignmentFailure.Disposed);
            if (!_addressCommand.IsCompleted) throw new AddressAssignmentException(AddressAssignmentFailure.Occupied);
            id = _addressCommandId = Guid.NewGuid(); _addressCommandFailure = null; _addressCommandPayload = null;
            cancellation = _addressCommandCancellation = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _addressCommand = completion.Task;
        }
        _ = RunAddressCommandAsync(action, cancellation, completion);
        return new { operationId = id, accepted = true, statusTool = "address_assignment_status", cancelTool = "address_assignment_cancel" };
    }
    private async Task RunAddressCommandAsync(Func<CancellationToken, Task<object>> action, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        object? payload = null; string? failure = null;
        try { payload = await action(cancellation.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error switch { AddressAssignmentException address => address.Failure.ToString(), Knx.Bus.Connections.KnxConnectionException connection => connection.Failure.ToString(), OperationCanceledException => "Cancelled", _ => "TransportFailed" }; }
        finally
        {
            lock (_addressCommandGate) { _addressCommandPayload = payload; _addressCommandFailure = failure; if (ReferenceEquals(_addressCommandCancellation, cancellation)) _addressCommandCancellation = null; }
            cancellation.Dispose(); completion.TrySetResult();
        }
    }
    private async Task<ConnectionJob> EnterAddressConnectionJobAsync(CancellationToken ct)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _connectionLifetime.Token);
        try { await _connectionJobs.WaitAsync(cancellation.Token).ConfigureAwait(false); return new(_connectionJobs, cancellation); }
        catch { cancellation.Dispose(); throw; }
    }
    private Task DrainAddressCommandAsync() { lock (_addressCommandGate) return _addressCommand; }
    private string? AddressHostBinding(AddressAssignmentRequest request)
    {
        if (_project is not { } project) return null;
        var targets = ProjectQuery.DeviceList(project).Where(device => device.Address == request.NewAddress).ToArray();
        if (targets.Any(device => project.Workbench.Devices.GetValueOrDefault(device.Id)?.SecureCapable == true))
            throw new AddressAssignmentException(AddressAssignmentFailure.UnsupportedSecureProvisioning);
        if (targets.Length > 1) throw new AddressAssignmentException(AddressAssignmentFailure.PhysicalBindingRequired);
        string? hardwareReference = null;
        if (targets.SingleOrDefault() is { } target)
        {
            var reference = target.Hardware2ProgramRefId is { Length: > 0 } program && project.Hardware.ContainsKey(program) ? program : target.ProductRefId;
            if (project.Hardware.TryGetValue(reference, out var hardware) && hardware.IsCoupler) hardwareReference = reference + " / " + hardware.HardwareId;
            if ((hardwareReference is not null) != (request.TargetRole == AddressDeviceRole.Coupler) || hardwareReference != request.CouplerHardwareReference)
                throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        }
        return ProjectRevisions.ContentHash(project) + "|" + hardwareReference;
    }
    private async Task CloseAddressAssignmentAsync()
    { if (_addressAssignment is { } current) { await current.DisposeAsync().ConfigureAwait(false); if (ReferenceEquals(current, _addressAssignment)) _addressAssignment = null; } }
}
