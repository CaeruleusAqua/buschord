using System.Net;
using System.Text.Json.Nodes;
using Knx.Bus.Connections;
using Knx.Bus.Security;
using F = GroupConnectionFixture;

internal static class GroupHostExplicitSourceChecks
{
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("unverified candidates read no password/integrity/state", Candidates),
            ("normal ENV binding remains exact while explicit source is mandatory", Bindings),
            ("explicit App binding retains every endpoint/source/selector constraint", Metadata),
            ("LoadPinned refuses registry changes and stale actual keyring bytes", Pins),
            ("registry change during password acquisition prevents transport", PromptRace),
            ("new explicit source path actually authenticates without ENV fallback", Connect),
            ("registry owner disposal prevents future guarded opens", DisposedGuard),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine("PASS GroupHostExplicit " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL GroupHostExplicit " + name + ": " + error); }
        }
        return failures;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Code(KnxConnectionFailure failure, Func<Task> run)
    { try { await run(); } catch (KnxConnectionException error) when (error.Failure == failure) { return; } throw new Exception("Expected " + failure); }
    internal static string Write(F fixture) => GroupHostConfigurationFixture.Write(fixture);
    private static async Task Candidates()
    {
        using var fixture = new F(); var path = Write(fixture); var data = KnxGroupHostProfiles.ReadCandidates(path);
        Require(data.Candidates.Single().Id == "test" && data.Candidates.Single().RequiresCurrentUnanchoredConfirmation && data.SourceFingerprint.Length == 64
            && fixture.PasswordSource.Reads == 0 && !File.Exists(fixture.State), "Candidate read acquired credentials/state.");
        await Code(KnxConnectionFailure.GroupSecurityStateNotReady, () => { KnxGroupHostProfiles.LoadPinned(path, data.SourceFingerprint, []); return Task.CompletedTask; });
    }
    private static async Task Bindings()
    {
        using var fixture = new F(); await fixture.Provision(); var path = Write(fixture); var integrity = new Integrity();
        await using var owner = KnxGroupHostProfiles.LoadPinned(path, KnxGroupHostProfiles.ReadCandidates(path).SourceFingerprint, ["test"], integrity);
        await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await owner.ResolveAsync("test", fixture.Connection(), default));
        await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await owner.ResolveForExplicitPasswordSourceAsync("test", KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, fixture.Reference()), default));
        Require(integrity.Reads == 0, "Bad source read integrity.");
        var policy = await owner.ResolveForExplicitPasswordSourceAsync("test", fixture.Connection(), default);
        Require(policy.Groups.Single().Group == F.Group && integrity.Reads == 1 && integrity.Last!.All(value => value == 0) && fixture.PasswordSource.Reads == 0, "App source path changed or read a password.");
    }
    private static async Task Metadata()
    {
        using var fixture = new F(); await fixture.Provision(); var path = Write(fixture); var integrity = new Integrity(); await using var owner = KnxGroupHostProfiles.Load(path, ["test"], integrity);
        var correct = fixture.Reference();
        var wrong = new[]
        {
            KnxConnectionProfile.IpSecure(new(IPAddress.Loopback, 3672), correct, fixture.PasswordSource),
            KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, correct, fixture.PasswordSource, IPAddress.Loopback),
            KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, new(fixture.Keyring, "wrong", correct.HostAddress, 4, F.Local, expectedSourceFingerprint: correct.ExpectedSourceFingerprint), fixture.PasswordSource),
            KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, new(fixture.Keyring, correct.ExpectedProjectLabel, correct.HostAddress, 3, F.Local, expectedSourceFingerprint: correct.ExpectedSourceFingerprint), fixture.PasswordSource),
            KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, new(fixture.Keyring, correct.ExpectedProjectLabel, new(1, 2, 5), 4, F.Local, expectedSourceFingerprint: correct.ExpectedSourceFingerprint), fixture.PasswordSource),
            KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, new(fixture.Keyring, correct.ExpectedProjectLabel, correct.HostAddress, 4, new(1, 2, 11), expectedSourceFingerprint: correct.ExpectedSourceFingerprint), fixture.PasswordSource),
            KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, new(fixture.Keyring, correct.ExpectedProjectLabel, correct.HostAddress, 4, F.Local, expectedSourceFingerprint: new string('A', 64)), fixture.PasswordSource),
        };
        foreach (var connection in wrong) await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await owner.ResolveForExplicitPasswordSourceAsync("test", connection, default));
        Require(integrity.Reads == 0 && fixture.Peers.Count == 0, "Mismatched metadata reached capabilities.");
    }
    private static async Task Pins()
    {
        using var fixture = new F(); await fixture.Provision(); var path = Write(fixture); var candidates = KnxGroupHostProfiles.ReadCandidates(path);
        await File.AppendAllTextAsync(path, " "); await Code(KnxConnectionFailure.CredentialBindingMismatch,
            () => { KnxGroupHostProfiles.LoadPinned(path, candidates.SourceFingerprint, ["test"]); return Task.CompletedTask; });
        path = Write(fixture); await using var owner = KnxGroupHostProfiles.LoadPinned(path, candidates.SourceFingerprint, ["test"], new Integrity());
        var connection = fixture.Connection(); await File.AppendAllTextAsync(fixture.Keyring, " ");
        await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await owner.ResolveForExplicitPasswordSourceAsync("test", connection, default));
    }
    private static async Task PromptRace()
    {
        using var fixture = new F(); await fixture.Provision(); var path = Write(fixture); var password = new HeldPassword();
        await using var owner = KnxGroupHostProfiles.Load(path, ["test"], new Integrity());
        var bare = KnxConnectionProfile.IpSecure(fixture.Connection().Gateway, fixture.Reference(), password);
        var selected = bare.WithGroupSecurity(await owner.ResolveForExplicitPasswordSourceAsync("test", bare, default));
        var acquiring = fixture.Factory().AcquireOperationAsync(selected); await password.Entered.Task; await File.AppendAllTextAsync(path, " "); password.Release.TrySetResult();
        await Code(KnxConnectionFailure.CredentialBindingMismatch, async () => await acquiring);
        Require(fixture.Peers.Count == 0 && password.Last!.All(value => value == '\0'), "Old registry authorized after password prompt.");
    }
    private static async Task Connect()
    {
        using var fixture = new F(); await fixture.Provision(); var path = Write(fixture); await using var owner = KnxGroupHostProfiles.Load(path, ["test"], new Integrity());
        var bare = fixture.Connection(); var selected = bare.WithGroupSecurity(await owner.ResolveForExplicitPasswordSourceAsync("test", bare, default));
        Require(ReferenceEquals(selected.PasswordSource, fixture.PasswordSource) && selected.Keyring!.PasswordEnvironmentName is null, "ENV reference replaced App source.");
        await using var operation = await fixture.Factory().AcquireOperationAsync(selected); await using var connection = await operation.OpenTunnelAsync();
        Require((await connection.Tunnel.ReadGroupValueAsync(F.Group, TimeSpan.FromSeconds(2)))?.DataSecurity == Knx.Bus.BusDataSecurity.IncomingAuthenticatedAndConfidential
            && fixture.PasswordSource.Reads == 1, "Explicit source failed or used fallback.");
    }
    private static async Task DisposedGuard()
    {
        using var fixture = new F(); await fixture.Provision(); var path = Write(fixture); var owner = KnxGroupHostProfiles.Load(path, ["test"], new Integrity());
        var bare = fixture.Connection(); var selected = bare.WithGroupSecurity(await owner.ResolveForExplicitPasswordSourceAsync("test", bare, default)); await owner.DisposeAsync();
        await Code(KnxConnectionFailure.Disposed, async () => await fixture.Factory().AcquireOperationAsync(selected));
        Require(fixture.Peers.Count == 0, "Disposed registry allowed a new transport.");
    }
    private sealed class Integrity : IGroupIntegrityCapabilitySource
    { internal int Reads; internal byte[]? Last; public ValueTask<byte[]> ReadOwnedAsync(string name, CancellationToken ct) { Reads++; return ValueTask.FromResult(Last = F.Integrity.ToArray()); } }
    private sealed class HeldPassword : ISecurePasswordSource
    {
        internal char[]? Last; internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { Entered.TrySetResult(); await Release.Task; return Last = "pwd".ToCharArray(); }
    }
}
