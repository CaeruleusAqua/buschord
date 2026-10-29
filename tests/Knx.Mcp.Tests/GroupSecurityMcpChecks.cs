using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Mcp;
using F = GroupConnectionFixture;

internal static class GroupSecurityMcpChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task<JsonNode> Call(McpServer server, string name, JsonObject args)
        => JsonNode.Parse((await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } }.ToJsonString()))!)!;
    private static bool Error(JsonNode result) => result["error"] is not null || result["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonObject Arguments(F fixture, string env) => new()
    {
        ["mode"] = "ip_secure_udp", ["gateway"] = "127.0.0.1", ["own_address"] = F.Local.ToString(), ["keyring_path"] = fixture.Keyring,
        ["keyring_project"] = "BusChord synthetic keyring", ["keyring_password_env"] = env, ["host_address"] = "1.2.0", ["user_id"] = 4,
        ["expected_keyring_sha256"] = fixture.Reference().ExpectedSourceFingerprint, ["group_security_profile"] = "accepted.groups"
    };
    internal static async Task RunAsync(Action<string, bool, string?> check)
    {
        async Task Case(string name, Func<Task> run) { try { await run().WaitAsync(TimeSpan.FromSeconds(15)); check("group MCP " + name, true, null); } catch (Exception error) { check("group MCP " + name, false, error.ToString()); } }
        await Case("default host rejects reference before credentials/network", async () =>
        {
            using var fixture = new F(); await fixture.Provision(); await using var server = new McpServer(fixture.Factory());
            Require(Error(await Call(server, "bus_connect", Arguments(fixture, "SYNTHETIC_NEVER_READ"))) && fixture.Peers.Count == 0, "Missing host registration reached network.");
        });
        await Case("raw group/integrity/counter arguments are rejected without echo", async () =>
        {
            using var fixture = new F(); await fixture.Provision(); await using var server = new McpServer(fixture.Factory(), new Resolver(fixture));
            foreach (var field in new[] { "group_key", "group_keys", "integrity_key", "counter_state", "counter_floor", "counter_next" })
            { var args = Arguments(fixture, "SYNTHETIC_NEVER_READ"); args[field] = "SyntheticOnlySecret"; var response = await Call(server, "bus_connect", args); Require(Error(response) && !response.ToJsonString().Contains("SyntheticOnlySecret"), "Secret accepted or echoed."); }
            Require(fixture.Peers.Count == 0, "Rejected secret opened network.");
        });
        await Case("host profile authenticates reads and monitor retains provenance", async () =>
        {
            using var fixture = new F(); await fixture.Provision(); var env = "BUSCHORD_ACCEPTANCE_PASSWORD_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(env, "pwd");
            try
            {
                var source = new Integrity(); var config = Config(fixture, env, "SYNTHETIC_INTEGRITY_ENV"); var path = Save(fixture, config);
                await using var profiles = GroupSecurityHostProfiles.Load(path, ["accepted.groups"], source);
                await using var server = new McpServer(fixture.Factory(), profiles);
                var connected = await Call(server, "bus_connect", Arguments(fixture, env)); Require(!Error(connected), connected.ToJsonString());
                Require(!Error(await Call(server, "recorder_start", new())), "Recorder start failed.");
                var read = await Call(server, "bus_read", new() { ["group_address"] = F.Group.ToString(), ["timeout_ms"] = 1000 });
                Require(!Error(read) && read["result"]!["structuredContent"]!["dataSecurity"]!.GetValue<string>() == "IncomingAuthenticatedAndConfidential", read.ToJsonString());
                var recording = await Call(server, "recorder_stop", new());
                Require(recording.ToJsonString().Contains("IncomingAuthenticatedAndConfidential"), "Live monitor lost Data Secure provenance.");
                Require(Error(await Call(server, "bus_write", new() { ["group_address"] = F.Group.ToString(), ["value"] = true })), "Read-only write gate changed.");
                var sent = fixture.Peers.Single().Gateway.TotalSends;
                Require(Error(await Call(server, "bus_read", new() { ["group_address"] = F.OtherGroup.ToString() })) && fixture.Peers.Single().Gateway.TotalSends == sent, "Unprovisioned protected GA leaked.");
                await Call(server, "bus_disconnect", new());
                Require(!Error(await Call(server, "bus_connect", new())), "Reconnect lost the selected group policy.");
                Require(source.Reads == 1 && source.Last!.All(value => value == 0), "Integrity capability was reacquired or not cleared.");
                Require(fixture.Peers.All(peer => peer.Gateway.AuthenticatedUser), "Reconnect downgraded.");
            }
            finally { Environment.SetEnvironmentVariable(env, null); }
        });
        await Case("resolver errors and cancellation remain typed and redacted", async () =>
        {
            using var fixture = new F(); await fixture.Provision(); var resolver = new Resolver(fixture) { Error = new IOException(fixture.Root + " SyntheticOnlySecret") };
            await using var server = new McpServer(fixture.Factory(), resolver); var result = await Call(server, "bus_connect", Arguments(fixture, "SYNTHETIC_NEVER_READ"));
            Require(Error(result) && !result.ToJsonString().Contains(fixture.Root) && !result.ToJsonString().Contains("SyntheticOnlySecret") && fixture.Peers.Count == 0, "Resolver leaked state.");
        });
        await Case("strict host schema and explicit current-state confirmation", async () =>
        {
            using var fixture = new F(); var config = Config(fixture, "SYNTHETIC_PASSWORD_ENV", "SYNTHETIC_INTEGRITY_ENV");
            await Rejected(Save(fixture, config), []);
            foreach (var mutation in new Action<JsonObject>[]
            {
                root => root["raw_key"] = "SyntheticOnlySecret", root => root["profiles"]![0]!["state_trust"] = "automatic",
                root => root["profiles"]![0]!["groups"]![0]!["protection"] = "plain", root => root["profiles"]![0]!["groups"]![0]!["senders"] = new JsonArray("1.2.1", "1.2.1"),
                root => root["profiles"]![0]!["ordinary_key_epoch"] = "ToolAccess:forbidden", root => root["profiles"]![0]!["counter_integrity_env"] = "not a name",
                root => root["profiles"]![0]!["counter_state_path"] = "relative.state", root => root["profiles"]![0]!["expected_keyring_sha256"] = ""
            }) { var modified = config.DeepClone().AsObject(); mutation(modified); await Rejected(Save(fixture, modified), ["accepted.groups"]); }
            var duplicate = config.ToJsonString().Replace("\"format\":", "\"format\":\"invalid\",\"format\":"); File.WriteAllText(Path.Combine(fixture.Root, "duplicate.json"), duplicate); await Rejected(Path.Combine(fixture.Root, "duplicate.json"), ["accepted.groups"]);
            await using var profiles = GroupSecurityHostProfiles.Load(Save(fixture, config), ["accepted.groups"], new Integrity());
        });
        await Case("host exact selector and missing state never read integrity ENV", async () =>
        {
            using var fixture = new F(); var env = "SYNTHETIC_PASSWORD_ENV"; var source = new Integrity(); await using var profiles = GroupSecurityHostProfiles.Load(Save(fixture, Config(fixture, env, "SYNTHETIC_INTEGRITY_ENV")), ["accepted.groups"], source);
            await using var server = new McpServer(fixture.Factory(), profiles);
            var wrong = Arguments(fixture, env); wrong["user_id"] = 3; Require(Error(await Call(server, "bus_connect", wrong)), "Wrong selector accepted.");
            Require(Error(await Call(server, "bus_connect", Arguments(fixture, env))) && source.Reads == 0 && fixture.Peers.Count == 0, "Missing state acquired capabilities.");
        });
        await Case("host owner joins a delayed integrity read and clears cancelled results", async () =>
        {
            using var fixture = new F(); await fixture.Provision(); var source = new Integrity { Hold = true }; var config = Config(fixture, "SYNTHETIC_PASSWORD_ENV", "SYNTHETIC_INTEGRITY_ENV");
            var profiles = GroupSecurityHostProfiles.Load(Save(fixture, config), ["accepted.groups"], source);
            var connection = KnxConnectionProfile.IpSecure(fixture.Connection().Gateway,
                new(fixture.Keyring, "BusChord synthetic keyring", new(1, 2, 0), 4, F.Local, "SYNTHETIC_PASSWORD_ENV", fixture.Reference().ExpectedSourceFingerprint));
            var resolving = profiles.ResolveAsync("accepted.groups", connection, default).AsTask(); await source.Entered.Task;
            var closing = profiles.DisposeAsync().AsTask(); Require(!closing.IsCompleted, "Host owner abandoned pending read."); source.Release.TrySetResult();
            try { await resolving; throw new Exception("Cancelled result published."); } catch (OperationCanceledException) { }
            await closing; Require(source.Last!.All(value => value == 0), "Cancelled integrity buffer retained.");
        });
        await Case("canonical ENV integrity profile has no hex or whitespace fallback", async () =>
        {
            var env = "BUSCHORD_ACCEPTANCE_INTEGRITY_" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var bad in new[] { "", Convert.ToHexString(F.Integrity), Convert.ToBase64String(F.Integrity) + " ", Convert.ToBase64String(new byte[31]) })
                { Environment.SetEnvironmentVariable(env, bad); try { await new EnvironmentHostIntegrityCapabilitySource().ReadOwnedAsync(env, default); throw new Exception("Invalid ENV accepted."); } catch (KnxConnectionException) { } }
                Environment.SetEnvironmentVariable(env, Convert.ToBase64String(F.Integrity)); var bytes = await new EnvironmentHostIntegrityCapabilitySource().ReadOwnedAsync(env, default); Require(bytes.SequenceEqual(F.Integrity), "ENV decode changed."); CryptographicOperations.ZeroMemory(bytes);
            }
            finally { Environment.SetEnvironmentVariable(env, null); }
        });
        await Case("standard stdio host loads explicitly named private registry without capabilities", async () =>
        {
            using var fixture = new F(); var path = Save(fixture, Config(fixture, "SYNTHETIC_PASSWORD_ENV", "SYNTHETIC_INTEGRITY_ENV"));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(GroupSecurityMcpChecks).Assembly.Location);
            start.ArgumentList.Add("--group-host-child");
            start.ArgumentList.Add("--group-security-profiles=" + path); start.ArgumentList.Add("--confirm-current-group-state=accepted.groups");
            using var child = Process.Start(start)!; child.StandardInput.Close(); var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync();
            Require(child.ExitCode == 0 && (await output).Length == 0 && (await error).Length == 0 && fixture.Peers.Count == 0, "Explicit standard-host registry failed or acquired a network capability at startup.");
        });
    }
    private static JsonObject Config(F fixture, string passwordEnv, string integrityEnv) => new()
    {
        ["format"] = "BusChord.GroupSecurityHost.v1", ["profiles"] = new JsonArray(new JsonObject
        {
            ["id"] = "accepted.groups", ["gateway"] = "127.0.0.1", ["port"] = 3671, ["own_address"] = F.Local.ToString(), ["host_address"] = "1.2.0", ["user_id"] = 4,
            ["keyring_path"] = fixture.Keyring, ["keyring_project"] = "BusChord synthetic keyring", ["keyring_password_env"] = passwordEnv, ["expected_keyring_sha256"] = fixture.Reference().ExpectedSourceFingerprint,
            ["installation_scope"] = "Synthetic physical group installation", ["ordinary_key_epoch"] = "ordinary-1", ["counter_state_path"] = fixture.State, ["counter_integrity_env"] = integrityEnv,
            ["state_trust"] = "provisioner_confirmed_current_unanchored", ["individual_key_sha256"] = new JsonArray(),
            ["groups"] = new JsonArray(new JsonObject { ["address"] = F.Group.ToString(), ["protection"] = "auth_conf", ["senders"] = new JsonArray(F.Sender.ToString()) }),
            ["plain_groups"] = new JsonArray(F.PlainGroup.ToString()), ["plain_peers"] = new JsonArray()
        })
    };
    private static string Save(F fixture, JsonObject root) { var path = Path.Combine(fixture.Root, "group-profiles.json"); File.WriteAllText(path, root.ToJsonString()); return path; }
    private static async Task Rejected(string path, string[] confirmed)
    { try { await using var owner = GroupSecurityHostProfiles.Load(path, confirmed); throw new Exception("Invalid config accepted."); } catch (KnxConnectionException) { } }
    private sealed class Resolver(F fixture) : IKnxGroupSecurityProfileResolver
    { internal Exception? Error; public ValueTask<KnxGroupSecurityProfile> ResolveAsync(string id, KnxConnectionProfile connection, CancellationToken ct) => Error is null ? ValueTask.FromResult(fixture.Policy()) : ValueTask.FromException<KnxGroupSecurityProfile>(Error); }
    private sealed class Integrity : IHostIntegrityCapabilitySource
    {
        internal int Reads; internal byte[]? Last; internal bool Hold;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct)
        { Reads++; Entered.TrySetResult(); if (Hold) await Release.Task; return Last = F.Integrity.ToArray(); }
    }
}
