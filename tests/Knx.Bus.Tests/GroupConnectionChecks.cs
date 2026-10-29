using System.Security.Cryptography;
using System.Text.Json;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;
using F = GroupConnectionFixture;

internal static class GroupConnectionChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Code(KnxConnectionFailure expected, Func<Task> action)
    { try { await action(); } catch (KnxConnectionException error) when (error.Failure == expected) { return; } throw new Exception("Expected " + expected); }
    private static async Task Until(Func<bool> test) { for (var i = 0; i < 200 && !test(); i++) await Task.Delay(10); Require(test(), "Deadline."); }
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("missing state blocks credentials and network", Missing),
            ("restore, exhausted and wrong-domain state fail before I/O", InvalidState),
            ("independent anchor loss never becomes unanchored trust", MissingAnchor),
            ("source pin and exact signed group/sender subsets are required", Selection),
            ("all signed protected groups cannot be made plain", PlainBoundary),
            ("known cross-domain key reuse is refused", KeyReuse),
            ("anchored group read commits before verified payload", AnchoredRead),
            ("group monitor survives fresh authenticated opens without reimport", Reconnect),
            ("replay, bad MAC, sender and plaintext never reach monitor", Denials),
            ("auth-only provenance stays separate from confidentiality and IP auth", AuthOnly),
            ("own secure confirmation is never remote-authenticated traffic", Confirmation),
            ("gateway and assigned-address failures never publish group tunnel", GatewayFailure),
            ("close joins held receive-floor commit and retains reservation", JoinedClose),
            ("caller cancellation joins password acquisition and reservation", CancelAcquire),
            ("read-only facade still refuses group writes", ReadOnly),
            ("configuration snapshots and serialization expose no capabilities", Snapshot),
            ("recording import cannot assert live authentication", Recording),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine("PASS GroupConnection " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL GroupConnection " + name + ": " + error); }
        }
        return failures;
    }
    private static async Task Missing()
    {
        using var fixture = new F(); await Code(KnxConnectionFailure.GroupSecurityStateUnavailable,
            async () => await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy())));
        Require(fixture.PasswordSource.Reads == 0 && fixture.Peers.Count == 0 && !File.Exists(fixture.State), "Missing state was bootstrapped.");
    }
    private static async Task InvalidState()
    {
        foreach (var mode in new[] { "restore", "exhausted", "identity" })
        {
            using var fixture = new F(); await fixture.Provision(mode == "exhausted" ? KnxDataSecureCodec.MaximumSequence + 1 : 10, mode != "restore");
            var profile = mode == "identity" ? new KnxGroupSecurityProfile("another installation", "ordinary-1", fixture.Policy().Groups,
                fixture.Source, []) : fixture.Policy();
            await Code(mode == "restore" ? KnxConnectionFailure.GroupSecurityStateNotReady : mode == "identity"
                ? KnxConnectionFailure.GroupSecurityBindingMismatch : KnxConnectionFailure.GroupSecurityCounterExhausted,
                async () => await fixture.Factory().AcquireOperationAsync(fixture.Connection(profile)));
            Require(fixture.PasswordSource.Reads == 0 && fixture.Peers.Count == 0, "Invalid state reached credentials or network.");
        }
    }
    private static async Task Selection()
    {
        using var fixture = new F(); await fixture.Provision();
        foreach (var policy in new[] { fixture.Policy(group: F.OtherGroup), fixture.Policy(sender: new(1, 2, 3)) })
            await Code(KnxConnectionFailure.GroupSecurityBindingMismatch, async () => await fixture.Factory().AcquireOperationAsync(fixture.Connection(policy)));
        await Code(KnxConnectionFailure.InvalidProfile, () => { fixture.Connection(fixture.Policy(), pinned: false); return Task.CompletedTask; });
        var prior = fixture.Connection(fixture.Policy()); await File.AppendAllTextAsync(fixture.Keyring, " ");
        await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await fixture.Factory().AcquireOperationAsync(prior));
        Require(fixture.Peers.Count == 0 && fixture.PasswordSource.Last!.All(value => value == '\0'), "Source selection escaped offline validation/cleanup.");
    }
    private static async Task MissingAnchor()
    {
        using var fixture = new F(); var anchor = new F.Anchor(); await fixture.Provision(anchor: anchor); anchor.Missing = true;
        using var source = new ExistingGroupCounterStateSource(fixture.State, F.Integrity, anchor);
        await Code(KnxConnectionFailure.GroupSecurityStateNotReady, async () => await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy(source: source))));
        Require(fixture.PasswordSource.Reads == 0 && fixture.Peers.Count == 0, "Uncertain anchor reached credentials/network.");
    }
    private static async Task PlainBoundary()
    {
        using var fixture = new F(); await fixture.Provision();
        await Code(KnxConnectionFailure.GroupSecurityBindingMismatch, async () => await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy(plain: [F.OtherGroup]))));
        await using var operation = await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy()));
        await using var connection = await operation.OpenTunnelAsync(); var peer = fixture.Peers.Single();
        var sent = peer.Gateway.TotalSends;
        foreach (var group in new[] { F.OtherGroup, new GroupAddress(0x333) })
            await Code(KnxConnectionFailure.GroupSecurityPolicyDenied, () => connection.Tunnel.GroupValueReadAsync(group));
        Require(sent == peer.Gateway.TotalSends, "Unconfigured destination leaked to transport.");
        await connection.Tunnel.GroupValueReadAsync(F.PlainGroup);
        Require(peer.Gateway.TotalSends > sent, "Explicit plain group was not available.");
        await Code(KnxConnectionFailure.GroupSecurityPolicyDenied, async () => await operation.OpenManagementAsync(new(1, 2, 3), true));
    }
    private static async Task KeyReuse()
    {
        using var fixture = new F(); await fixture.Provision(); var hash = Convert.ToHexString(SHA256.HashData(fixture.GroupKey));
        await Code(KnxConnectionFailure.GroupSecurityBindingMismatch, async () => await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy(external: [hash]))));
        Require(fixture.Peers.Count == 0, "Individual/group key reuse reached the network.");
    }
    private static async Task AnchoredRead()
    {
        using var fixture = new F(); var anchor = fixture.AnchorSource = new F.Anchor { HoldOnValue = true }; await fixture.Provision(anchor: anchor);
        using var source = new ExistingGroupCounterStateSource(fixture.State, F.Integrity, anchor);
        await using var operation = await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy(source: source)));
        await using var connection = await operation.OpenTunnelAsync(); var count = 0; connection.Tunnel.TelegramReceived += _ => count++;
        var reading = connection.Tunnel.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(3)); await anchor.Entered.Task;
        Require(!reading.IsCompleted && count == 0, "Payload preceded durable floor/anchor commit."); anchor.Release.TrySetResult();
        var value = await reading; Require(value?.DataSecurity == BusDataSecurity.IncomingAuthenticatedAndConfidential && value.Telegram.Data.SequenceEqual(new byte[] { 42 }), "Missing verified data.");
        Require(connection.Security.GatewayAuthenticated && connection.Security.GroupSecurityConfigured, "Configured policy and transport status lost.");
        await using var state = await fixture.OpenState(); Require(await state.ReadReceiveFloorAsync(F.Sender) == 21, "Event preceded retained floor.");
    }
    private static async Task Reconnect()
    {
        using var fixture = new F(); await fixture.Provision(); var factory = fixture.Factory();
        await using var operation = await factory.AcquireOperationAsync(fixture.Connection(fixture.Policy()));
        ulong previous = 0;
        for (var index = 0; index < 2; index++)
        {
            await using var connection = await operation.OpenTunnelAsync(); var peer = fixture.Peers.Last();
            // A new transport does not reset the ordinary device sequence. Deliberately send a fresh remote frame.
            fixture.AutoReply = false; var reading = connection.Tunnel.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(2)); await Until(() => peer.Reads == 1);
            peer.Push(fixture.Value((ulong)(30 + index))); var value = await reading;
            Require(value?.DataSecurity == BusDataSecurity.IncomingAuthenticatedAndConfidential && peer.SentSequences.Single() > previous, "Reconnect reused a nonce or dropped provenance.");
            previous = peer.SentSequences.Single();
        }
        Require(fixture.PasswordSource.Reads == 1 && fixture.Peers.All(peer => peer.Gateway.AuthenticatedUser && peer.Gateway.Disposals == 1), "Fresh opens reimported or did not drain.");
    }
    private static async Task Denials()
    {
        using var fixture = new F { AutoReply = false }; await fixture.Provision();
        await using var operation = await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy())); await using var connection = await operation.OpenTunnelAsync();
        var peer = fixture.Peers.Single(); var observed = new List<BusTelegram>(); connection.Tunnel.TelegramReceived += observed.Add;
        var bad = fixture.Value(20); bad[^1] ^= 1;
        var denied = new[] { bad, fixture.Value(20, F.OtherSender), fixture.Value(20, group: F.OtherGroup), fixture.Value(20, group: F.PlainGroup),
            fixture.Value(20, group: new(0x444)), fixture.Value(20, protection: KnxDataProtection.Authentication),
            Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xBC, 0xE0, F.Sender, F.Group.Raw, [0, 0x40, 99])),
            Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xBC, 0xE0, F.Sender, F.OtherGroup.Raw, [0, 0x40, 99])) };
        foreach (var raw in denied) peer.Push(raw);
        await Until(() => peer.Acks == denied.Length); Require(observed.Count == 0, "Denied secure/plain frame leaked.");
        peer.Push(fixture.Value(21)); await Until(() => observed.Count == 1);
        peer.Push(fixture.Value(21)); await Until(() => peer.Acks == denied.Length + 2); Require(observed.Count == 1, "New NetIP sequence bypassed secure replay floor.");
        peer.Push(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xBC, 0xE0, F.Sender, F.PlainGroup.Raw, [0, 0x40, 7])));
        await Until(() => observed.Count == 2); Require(observed.Last().DataSecurity == BusDataSecurity.Unverified, "IP auth was mistaken for Data Secure.");
    }
    private static async Task AuthOnly()
    {
        using var fixture = new F { AutoReply = false }; await fixture.Provision();
        await using var operation = await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy(KnxDataProtection.Authentication)));
        await using var connection = await operation.OpenTunnelAsync(); var peer = fixture.Peers.Single();
        var reading = connection.Tunnel.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(2)); await Until(() => peer.Reads == 1);
        peer.Push(fixture.Value(20, protection: KnxDataProtection.Authentication));
        Require((await reading)?.DataSecurity == BusDataSecurity.IncomingAuthenticated, "Auth-only was reported as confidential.");
    }
    private static async Task Confirmation()
    {
        using var fixture = new F(); await fixture.Provision();
        await using var operation = await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy())); await using var connection = await operation.OpenTunnelAsync();
        var peer = fixture.Peers.Single(); await connection.Tunnel.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(2));
        BusTelegram? confirmation = null; var received = 0; connection.Tunnel.ConfirmationReceived += value => confirmation = value; connection.Tunnel.TelegramReceived += _ => received++;
        var raw = peer.LastRead!.ToArray(); raw[0] = (byte)CemiMessageCode.LDataCon; peer.Push(raw); await Until(() => confirmation is not null);
        Require(confirmation!.DataSecurity == BusDataSecurity.Unverified && received == 0, "Own confirmation asserted remote authentication.");
    }
    private static async Task GatewayFailure()
    {
        foreach (var mismatch in new[] { false, true })
        {
            using var fixture = new F { BadGateway = !mismatch, WrongAddress = mismatch }; await fixture.Provision();
            await using var operation = await fixture.Factory().AcquireOperationAsync(fixture.Connection(fixture.Policy()));
            await Code(mismatch ? KnxConnectionFailure.AssignedAddressMismatch : KnxConnectionFailure.AuthenticationFailed, async () => await operation.OpenTunnelAsync());
            Require(fixture.Peers.All(peer => peer.Reads == 0 && peer.Gateway.Disposals == 1), "Invalid gateway/IA published application data.");
        }
    }
    private static async Task JoinedClose()
    {
        using var fixture = new F(); var anchor = fixture.AnchorSource = new F.Anchor { HoldOnValue = true }; await fixture.Provision(anchor: anchor);
        using var source = new ExistingGroupCounterStateSource(fixture.State, F.Integrity, anchor); var factory = fixture.Factory();
        var operation = await factory.AcquireOperationAsync(fixture.Connection(fixture.Policy(source: source))); var connection = await operation.OpenTunnelAsync();
        var reading = connection.Tunnel.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(3)); await anchor.Entered.Task;
        var closing = operation.DisposeAsync().AsTask(); await anchor.Cancelled.Task; Require(!closing.IsCompleted, "Close abandoned admitted floor operation.");
        await Code(KnxConnectionFailure.Occupied, async () => await factory.AcquireOperationAsync(fixture.Connection()));
        anchor.Release.TrySetResult(); await closing; try { await reading; } catch (OperationCanceledException) { }
        Require(fixture.Peers.Single().Gateway.Disposals == 1, "Close did not join transport."); await connection.DisposeAsync();
    }
    private static async Task ReadOnly()
    {
        using var fixture = new F(); await fixture.Provision(); await using var bus = new KnxBus(fixture.Factory()); await bus.ConnectAsync(fixture.Connection(fixture.Policy()));
        try { await bus.GroupValueWriteAsync(F.Group, [1]); throw new Exception("Write enabled."); } catch (NotSupportedException) { }
        Require((await bus.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(2)))?.DataSecurity == BusDataSecurity.IncomingAuthenticatedAndConfidential, "Facade lost secure result.");
    }
    private static async Task CancelAcquire()
    {
        using var fixture = new F(); await fixture.Provision(); var password = new HoldingPassword(); var factory = fixture.Factory();
        using var cancellation = new CancellationTokenSource();
        var profile = KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, fixture.Reference(), password).WithGroupSecurity(fixture.Policy());
        var acquiring = factory.AcquireOperationAsync(profile, cancellation.Token); await password.Entered.Task; cancellation.Cancel();
        Require(!acquiring.IsCompleted, "Cancelled acquisition abandoned password owner.");
        await Code(KnxConnectionFailure.Occupied, async () => await factory.AcquireOperationAsync(fixture.Connection()));
        password.Release.TrySetResult(); try { await acquiring; throw new Exception("Cancelled acquisition published."); } catch (OperationCanceledException) { }
        Require(password.Last!.All(value => value == '\0') && fixture.Peers.Count == 0, "Cancelled secret retained or network opened.");
        await using var next = await factory.AcquireOperationAsync(fixture.Connection());
    }
    private sealed class HoldingPassword : ISecurePasswordSource
    {
        internal char[]? Last;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { Entered.TrySetResult(); await Release.Task; return Last = "pwd".ToCharArray(); }
    }
    private static Task Snapshot()
    {
        using var fixture = new F(); var senders = new[] { F.Sender }; var input = new[] { new KnxSecureGroupSelection(F.Group, KnxDataProtection.Authentication, senders) };
        var policy = new KnxGroupSecurityProfile("physical", "ordinary", input, fixture.Source, []); senders[0] = F.OtherSender;
        Require(policy.Groups.Single().PermittedSenders.Single() == F.Sender, "Caller mutation changed sender policy.");
        var json = JsonSerializer.Serialize(fixture.Connection(policy)); Require(!json.Contains(fixture.Root) && !json.Contains("ordinary.state") && !json.Contains("StateSource"), "Capabilities serialized.");
        return Task.CompletedTask;
    }
    private static Task Recording()
    {
        var imported = JsonSerializer.Deserialize<RecordedTelegram>("{\"DataSecurity\":2}"); Require(imported!.DataSecurity == BusDataSecurity.Unverified, "Untrusted file asserted live authentication."); return Task.CompletedTask;
    }
}
