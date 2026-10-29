using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Knx;

namespace Knx.Bus;

/// <summary>A bounded snapshot of explicitly selected targets. Constructing a plan performs no network access.</summary>
public sealed class ReadOnlyLineScanPlan
{
    private readonly byte[] _gatewayAddress;
    private readonly int _gatewayPort;
    public IPEndPoint Gateway => new(new IPAddress(_gatewayAddress), _gatewayPort);
    public IndividualAddress RequestedTunnelAddress { get; }
    public IReadOnlyList<IndividualAddress> Targets { get; }
    public TimeSpan ProbeTimeout { get; }
    public TimeSpan InterTargetDelay { get; }
    public string InterfaceId => $"knx-ip:{new IPAddress(_gatewayAddress)}:{_gatewayPort}";

    public ReadOnlyLineScanPlan(IPEndPoint gateway, IndividualAddress requestedTunnelAddress,
        IEnumerable<IndividualAddress> targets, TimeSpan? probeTimeout = null, TimeSpan? interTargetDelay = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(targets);
        var bytes = gateway.Address.GetAddressBytes();
        if (gateway.AddressFamily != AddressFamily.InterNetwork || bytes[0] is 0 or >= 224
            || gateway.Port is < 1 or > 65535 || gateway.Address.Equals(IPAddress.Broadcast))
            throw new ArgumentException("An explicit unicast IPv4 gateway endpoint is required.", nameof(gateway));
        if (requestedTunnelAddress.Device is 0 or 255)
            throw new ArgumentOutOfRangeException(nameof(requestedTunnelAddress));
        var selected = targets.Take(257).ToArray();
        if (selected.Length is < 1 or > 256 || selected.Any(a => a.Raw is 0 or ushort.MaxValue)
            || selected.Any(a => (a.Raw >> 8) != (selected[0].Raw >> 8))
            || selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("Select 1..256 distinct unicast targets on one line; 0.0.0 and the 15.15.255 factory address are not targets.", nameof(targets));
        ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(3);
        InterTargetDelay = interTargetDelay ?? TimeSpan.FromMilliseconds(100);
        if (ProbeTimeout < TimeSpan.FromMilliseconds(50) || ProbeTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        if (InterTargetDelay < TimeSpan.Zero || InterTargetDelay > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(interTargetDelay));
        _gatewayAddress = bytes.ToArray(); _gatewayPort = gateway.Port;
        RequestedTunnelAddress = requestedTunnelAddress;
        Targets = Array.AsReadOnly(selected);
    }
}

public enum ReadOnlyLineScanState { Responded, Unknown, NotProbed }
public enum ReadOnlyLineScanReason { None, Timeout, ManagementFailure, TransportFailure, Cancelled, NotReached, RequestedTunnelAddress, AssignedTunnelAddress }
public enum ReadOnlyLineScanCompletion { Completed, Cancelled, TransportFailed, CleanupFailed }

/// <summary>A descriptor response proves only that this address responded. Silence never establishes absence.</summary>
public sealed record ReadOnlyLineScanObservation(IndividualAddress Address, ReadOnlyLineScanState State,
    ReadOnlyLineScanReason Reason, ushort? MaskVersion, DateTimeOffset? ObservedAtUtc, TimeSpan Elapsed,
    bool CleanupFailed = false);
public sealed record ReadOnlyLineScanProgress(Guid ScanId, int Completed, int Total, ReadOnlyLineScanObservation Observation);
public sealed record ReadOnlyLineScanResult(Guid ScanId, string InterfaceId, DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc,
    ReadOnlyLineScanCompletion Completion, IReadOnlyList<ReadOnlyLineScanObservation> Observations)
{
    public bool IsReadOnly => true;
    public int RespondedCount => Observations.Count(o => o.State == ReadOnlyLineScanState.Responded);
    public int UnknownCount => Observations.Count(o => o.State == ReadOnlyLineScanState.Unknown);
}

/// <summary>
/// Sequential addressed descriptor reads through fresh management connections. No discovery, group telegram,
/// memory/property write, restart, project mutation, or implicit retry is performed.
/// </summary>
public sealed class ReadOnlyLineScanner
{
    private readonly Func<ReadOnlyLineScanPlan, IndividualAddress, CancellationToken, Task<IManagementConnection>> _open;
    private int _running;

    public ReadOnlyLineScanner() : this(async (plan, target, ct) =>
        await KnxManagementConnection.OpenReadOnlyAsync(plan.Gateway, target, plan.RequestedTunnelAddress, ct: ct)) { }

    /// <summary>The factory owns failed-open cleanup, honors cancellation, and returns a fresh connected read-only session for the exact target.</summary>
    public ReadOnlyLineScanner(Func<ReadOnlyLineScanPlan, IndividualAddress, CancellationToken, Task<IManagementConnection>> open)
        => _open = open ?? throw new ArgumentNullException(nameof(open));

    /// <summary>
    /// Returns partial results on caller cancellation. Unattempted addresses are NotProbed; timeouts are Unknown.
    /// Progress callbacks must be nonblocking. Per-target deadlines cover open/read; connection cleanup is always awaited.
    /// </summary>
    public async Task<ReadOnlyLineScanResult> ScanAsync(ReadOnlyLineScanPlan plan,
        IProgress<ReadOnlyLineScanProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("This scanner already has an active run.");
        try
        {
            var id = Guid.NewGuid(); var started = DateTimeOffset.UtcNow;
            var results = new List<ReadOnlyLineScanObservation>(plan.Targets.Count);
            var completion = ReadOnlyLineScanCompletion.Completed;
            for (var index = 0; index < plan.Targets.Count; index++)
            {
                if (ct.IsCancellationRequested) { completion = ReadOnlyLineScanCompletion.Cancelled; break; }
                var target = plan.Targets[index];
                if (target == plan.RequestedTunnelAddress)
                {
                    Add(new(target, ReadOnlyLineScanState.NotProbed, ReadOnlyLineScanReason.RequestedTunnelAddress,
                        null, null, TimeSpan.Zero));
                    continue;
                }
                IManagementConnection? connection = null;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var state = ReadOnlyLineScanState.Unknown;
                var reason = ReadOnlyLineScanReason.None;
                ushort? mask = null; DateTimeOffset? observed = null; var cleanupFailed = false;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(plan.ProbeTimeout);
                try
                {
                    connection = await _open(plan, target, deadline.Token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The scan connection factory returned no connection.");
                    if (connection.Client.Target != target)
                        throw new InvalidOperationException("The scan connection belongs to a different target.");
                    if (!connection.Client.IsReadOnly)
                        throw new InvalidOperationException("The scan connection must prohibit management writes and restart.");
                    var response = await connection.Client.ReadDescriptorAsync(deadline.Token).ConfigureAwait(false);
                    if (response.Length != 2) throw new IOException("Unsupported descriptor response length.");
                    mask = BinaryPrimitives.ReadUInt16BigEndian(response);
                    observed = DateTimeOffset.UtcNow;
                    state = ReadOnlyLineScanState.Responded;
                }
                catch (ManagementAddressCollisionException ex)
                {
                    if (ex.AssignedAddress == target)
                    {
                        state = ReadOnlyLineScanState.NotProbed;
                        reason = ReadOnlyLineScanReason.AssignedTunnelAddress;
                    }
                    else
                    {
                        reason = ReadOnlyLineScanReason.TransportFailure;
                        completion = ReadOnlyLineScanCompletion.TransportFailed;
                    }
                }
                catch (OperationCanceledException)
                {
                    reason = ct.IsCancellationRequested ? ReadOnlyLineScanReason.Cancelled : ReadOnlyLineScanReason.Timeout;
                    if (ct.IsCancellationRequested) completion = ReadOnlyLineScanCompletion.Cancelled;
                    else if (connection is null) completion = ReadOnlyLineScanCompletion.TransportFailed;
                }
                catch (SocketException)
                {
                    reason = ReadOnlyLineScanReason.TransportFailure;
                    completion = ReadOnlyLineScanCompletion.TransportFailed;
                }
                catch (Exception ex) when (ex is IOException or System.Threading.Channels.ChannelClosedException)
                {
                    reason = connection is null ? ReadOnlyLineScanReason.TransportFailure : ReadOnlyLineScanReason.ManagementFailure;
                    if (connection is null) completion = ReadOnlyLineScanCompletion.TransportFailed;
                }
                catch (Exception)
                {
                    reason = ReadOnlyLineScanReason.TransportFailure;
                    completion = ReadOnlyLineScanCompletion.TransportFailed;
                }
                finally
                {
                    if (connection is not null)
                    {
                        try { await connection.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception) { cleanupFailed = true; completion = ReadOnlyLineScanCompletion.CleanupFailed; }
                    }
                }
                Add(new(target, state, reason, mask, observed, clock.Elapsed, cleanupFailed));
                if (completion != ReadOnlyLineScanCompletion.Completed) break;
                if (index + 1 < plan.Targets.Count && plan.InterTargetDelay > TimeSpan.Zero)
                {
                    try { await Task.Delay(plan.InterTargetDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { completion = ReadOnlyLineScanCompletion.Cancelled; break; }
                }
            }
            if (ct.IsCancellationRequested && completion == ReadOnlyLineScanCompletion.Completed)
                completion = ReadOnlyLineScanCompletion.Cancelled;
            foreach (var target in plan.Targets.Skip(results.Count))
                results.Add(new(target, ReadOnlyLineScanState.NotProbed,
                    completion == ReadOnlyLineScanCompletion.Cancelled ? ReadOnlyLineScanReason.Cancelled : ReadOnlyLineScanReason.NotReached,
                    null, null, TimeSpan.Zero));
            return new(id, plan.InterfaceId, started, DateTimeOffset.UtcNow, completion, Array.AsReadOnly(results.ToArray()));

            void Add(ReadOnlyLineScanObservation observation)
            {
                results.Add(observation);
                try { progress?.Report(new(id, results.Count, plan.Targets.Count, observation)); }
                catch (Exception) { /* An observer cannot stop connection cleanup or change scan evidence. */ }
            }
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
