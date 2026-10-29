using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;
using Knx.Mcp;

internal static class SecureConnectionMcpChecks
{
    private static readonly IPEndPoint Gateway = new(IPAddress.Loopback, 3671), Local = new(IPAddress.Loopback, 43123);
    private static readonly IndividualAddress Assigned = new(1, 1, 240);
    private static readonly string Fingerprint = new('A', 64);
    internal static async Task RunAsync(Action<string, bool, string?> check)
    {
        async Task Case(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(15)); check("Secure MCP " + name, true, null); }
            catch (Exception error) { check("Secure MCP " + name, false, error.GetType().Name + ": " + error.Message); }
        }
        await Case("schema exposes references and exact selectors without password/key values", async () =>
        {
            await using var server = new McpServer();
            foreach (var name in new[] { "bus_connect", "bus_scan", "bus_device_info", "download_plan" })
            {
                var schema = server.Tools.Single(tool => tool.Name == name).InputSchema["properties"]!.AsObject();
                Require(schema.ContainsKey("keyring_password_env") && schema.ContainsKey("host_address") && schema.ContainsKey("user_id")
                    && schema.ContainsKey("own_address") && schema.ContainsKey("expected_keyring_sha256") && !schema.ContainsKey("keyring_password") && !schema.ContainsKey("user_key"), "Unsafe or incomplete schema.");
            }
        });
        await Case("incomplete secure selection and raw secrets open zero transports", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            foreach (var required in new[] { "gateway", "keyring_path", "keyring_password_env", "keyring_project", "host_address", "user_id", "own_address" })
            { var args = fixture.Arguments(); args.Remove(required); Require(Error(await Call(server, "bus_connect", args)), "Missing selector accepted."); }
            foreach (var name in new[] { "password", "keyring_password", "authentication_code", "user_key", "tool_key" })
            { var args = fixture.Arguments(); args[name] = "SyntheticSecretValue"; var response = await Call(server, "bus_connect", args); Require(Error(response) && !response.ToJsonString().Contains("SyntheticSecretValue"), "Secret accepted or echoed."); }
            Require(fixture.Transports.Count == 0 && fixture.Provider.Acquisitions == 0, "Invalid profile reached credentials/network.");
            Require(Error(await Call(server, "bus_connect", new JsonObject())), "An incomplete secure selection implicitly became plain.");
            foreach (var name in new[] { "bus_discover", "bus_status" })
            {
                var failed = await Captured(server, name, new JsonObject { ["password"] = "SyntheticSecretValue" });
                Require(Error(failed.Response) && !failed.Output.Contains("SyntheticSecretValue") && !failed.Output.Contains(" at "), "A bus metadata tool exposed rejected secret arguments or stack paths.");
            }
            Require(Error(await Call(server, "bus_discover", new JsonObject { ["mode"] = "ip_secure_udp" })), "Secure mode was accepted by unauthenticated discovery.");
        });
        await Case("profile construction and source failures sanitize paths/env names/passwords", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            var args = fixture.Arguments(); args["keyring_path"] = fixture.Path + "\0SyntheticSecretValue";
            var failed = await Captured(server, "bus_connect", args);
            Require(Error(failed.Response) && !failed.Output.Contains(fixture.Path) && !failed.Output.Contains("SyntheticSecretValue"), "Construction leaked its source.");
            fixture.Provider.Error = new IOException(fixture.Path + " SYNTHETIC_PASSWORD_ENV SyntheticSecretValue");
            failed = await Captured(server, "bus_connect", fixture.Arguments());
            Require(Error(failed.Response) && !failed.Output.Contains(fixture.Path) && !failed.Output.Contains("SYNTHETIC_PASSWORD_ENV")
                && !failed.Output.Contains("SyntheticSecretValue") && fixture.Transports.Count == 0, "Credential error leaked or opened a transport.");
        });
        await Case("authenticated exact interface is reported and active mode changes are rejected", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            var connected = await Call(server, "bus_connect", fixture.Arguments());
            Require(!Error(connected) && connected["result"]!["structuredContent"]!["security"]!["gatewayAuthenticated"]!.GetValue<bool>(), "Secure state was not authenticated.");
            Require(fixture.Provider.Last is { UserId: 2 } reference && reference.HostAddress == new IndividualAddress(1, 1, 0)
                && reference.TunnelAddress == Assigned, "Selector was changed.");
            var sends = fixture.Transports[0].TotalSends;
            Require(!Error(await Call(server, "bus_connect", new JsonObject())), "Same active selection was not reused.");
            Require(Error(await Call(server, "bus_connect", new JsonObject { ["mode"] = "plain_udp", ["gateway"] = "127.0.0.1" })), "Secure connection ignored a different requested profile.");
            var changed = fixture.Arguments(); changed["user_id"] = 3;
            Require(Error(await Call(server, "bus_connect", changed)) && fixture.Transports.Count == 1 && fixture.Transports[0].TotalSends == sends, "Changed selector emitted network traffic.");
            Require(!Error(await Call(server, "bus_disconnect", new JsonObject())), "Disconnect failed.");
            Require(!Error(await Call(server, "bus_connect", new JsonObject())) && fixture.Provider.Last is { UserId: 2 }, "Rejected selector changed the later reconnect profile.");
        });
        await Case("diagnostics reuse the authenticated tunnel and its reservation", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            Require(!Error(await Call(server, "bus_connect", fixture.Arguments())), "Connect failed.");
            var peer = fixture.Transports.Single(); var before = peer.ApplicationSends;
            var reading = Call(server, "bus_device_info", new JsonObject { ["address"] = "1.1.1", ["response_timeout_ms"] = 1000 });
            await Until(() => peer.ApplicationSends > before);
            peer.PushPayload(KnxNetIpFrame.Encode(ServiceType.TunnellingRequest, Tunnelling.Request(7, 0,
                Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, new(1, 1, 1), Assigned.Raw, new byte[] { 3, 0x40, 7, 1 })))));
            var result = await reading;
            Require(!Error(result) && fixture.Provider.Acquisitions == 1 && fixture.Transports.Count == 1, "Diagnostics created another/plain tunnel or ignored its response.");
        });
        await Case("standalone diagnostics retain one secure reservation until the response and drain", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            var args = fixture.Arguments(); args["address"] = "1.1.1"; args["response_timeout_ms"] = 1000;
            var reading = Call(server, "bus_device_info", args);
            await Until(() => fixture.Transports.Count == 1 && fixture.Transports[0].ApplicationSends >= 2);
            var occupied = false;
            try { await using var owner = await fixture.Factory.AcquireOperationAsync(KnxConnectionProfile.Plain(Gateway, Assigned)); }
            catch (KnxConnectionException error) { occupied = error.Failure == KnxConnectionFailure.Occupied; }
            Require(occupied && !reading.IsCompleted, "Diagnostics released or duplicated its gateway reservation.");
            fixture.Transports[0].PushPayload(KnxNetIpFrame.Encode(ServiceType.TunnellingRequest, Tunnelling.Request(7, 0,
                Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, new(1, 1, 1), Assigned.Raw, new byte[] { 3, 0x40, 7, 1 })))));
            Require(!Error(await reading) && fixture.Transports.Single().Disposals == 1 && fixture.Provider.Acquisitions == 1, "Standalone diagnostics did not join its single secure owner.");
            await using var released = await fixture.Factory.AcquireOperationAsync(KnxConnectionProfile.Plain(Gateway, Assigned));
        });
        await Case("an authenticated source is pinned for later implicit reconnect", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            var args = fixture.Arguments(); args.Remove("expected_keyring_sha256");
            Require(!Error(await Call(server, "bus_connect", args)), "Initial unpinned selection failed.");
            await Call(server, "bus_disconnect", new JsonObject()); fixture.Provider.ReturnedFingerprint = new('B', 64);
            var response = await Call(server, "bus_connect", new JsonObject());
            Require(Error(response) && response.ToJsonString().Contains("CredentialBindingMismatch") && fixture.Transports.Count == 1, "Changed credential source was accepted or reached network.");
        });
        await Case("remote disconnect drains the stale owner before a fresh Secure diagnosis", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            Require(!Error(await Call(server, "bus_connect", fixture.Arguments())), "Initial connect failed.");
            var original = fixture.Transports.Single(); original.PushPayload(KnxNetIpFrame.Encode(ServiceType.DisconnectRequest, [7, 0, .. Hpai.Control(Gateway)]));
            var disconnected = false;
            for (var index = 0; index < 1000; index++)
            {
                var status = await Call(server, "bus_status", new JsonObject());
                if (status["result"]?["structuredContent"]?["state"]?.GetValue<string>() == "disconnected") { disconnected = true; break; }
                await Task.Delay(2);
            }
            Require(disconnected && original.Disposals == 0, "Remote-close fixture did not retain the old facade owner.");
            var reading = Call(server, "bus_device_info", new JsonObject { ["address"] = "1.1.1", ["response_timeout_ms"] = 1000 });
            await Until(() => fixture.Transports.Count == 2 && fixture.Transports[1].ApplicationSends >= 2);
            var fresh = fixture.Transports[1];
            Require(original.Disposals == 1 && fixture.Provider.Last is { UserId: 2 }, "Stale owner was not drained or exact Secure selection was lost.");
            fresh.PushPayload(KnxNetIpFrame.Encode(ServiceType.TunnellingRequest, Tunnelling.Request(7, 0,
                Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, new(1, 1, 1), Assigned.Raw, new byte[] { 3, 0x40, 7, 1 })))));
            Require(!Error(await reading) && fresh.PlainSends == 1 && fresh.Disposals == 1, "Fresh diagnosis downgraded or leaked its owner.");
        });
        await Case("diagnostics preserve safe typed rejection and cancellation codes", async () =>
        {
            using (var fixture = new Fixture())
            {
                await using var server = new McpServer(fixture.Factory);
                fixture.Provider.Error = new KnxConnectionException(KnxConnectionFailure.KeyringRejected);
                foreach (var name in new[] { "bus_scan", "bus_device_info" })
                {
                    var args = fixture.Arguments(); args["address"] = "1.1.1"; args["target"] = "1.1";
                    var rejected = await Captured(server, name, args);
                    Require(Error(rejected.Response) && rejected.Output.Contains("KeyringRejected") && !rejected.Output.Contains(fixture.Path)
                        && !rejected.Output.Contains("SYNTHETIC_PASSWORD_ENV") && fixture.Transports.Count == 0, "Typed diagnostic failure was wrapped or leaked.");
                }
            }
            using var blocked = new Fixture { HoldSend = true }; await using var cancellingServer = new McpServer(blocked.Factory); using var cancellation = new CancellationTokenSource();
            var input = blocked.Arguments(); input["address"] = "1.1.1";
            var reading = Call(cancellingServer, "bus_device_info", input, cancellation.Token);
            await Until(() => blocked.Transports.Count == 1); var peer = blocked.Transports.Single(); await peer.SendStarted.Task;
            cancellation.Cancel(); await peer.SendCancelled.Task;
            Require(!reading.IsCompleted && peer.Disposals == 0, "Diagnostic cancellation escaped before joined cleanup.");
            peer.ReleaseSend.TrySetResult(); var result = await reading;
            Require(Error(result) && result.ToJsonString().Contains("Cancelled") && peer.Disposals == 1, "Diagnostic cancellation lost its typed code or owner cleanup.");
        });
        await Case("gateway auth failure and implicit retry never open plain", async () =>
        {
            using var fixture = new Fixture { BadGateway = true }; await using var server = new McpServer(fixture.Factory);
            Require(Error(await Call(server, "bus_connect", fixture.Arguments())), "Bad device authentication accepted.");
            Require(Error(await Call(server, "bus_connect", new JsonObject())), "Failed Secure mode silently became plain.");
            Require(fixture.Transports.Count == 2 && fixture.Transports.All(peer => peer.PlainSends == 1 && peer.ApplicationSends == 0 && peer.Disposals == 1), "Failure leaked a peer or sent plain CONNECT.");
        });
        await Case("wrong actual assigned IA cannot publish Connected", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            var args = fixture.Arguments(); args["own_address"] = "1.1.241";
            var result = await Call(server, "bus_connect", args);
            Require(Error(result) && result.ToJsonString().Contains("AssignedAddressMismatch") && fixture.Transports.Single().Disposals == 1, "Wrong interface accepted.");
        });
        await Case("cancellation joins secure send unwind and releases lease only afterwards", async () =>
        {
            using var fixture = new Fixture { HoldSend = true }; var server = new McpServer(fixture.Factory); using var cancellation = new CancellationTokenSource();
            var connecting = Call(server, "bus_connect", fixture.Arguments(), cancellation.Token);
            await Until(() => fixture.Transports.Count == 1); var peer = fixture.Transports.Single(); await peer.SendStarted.Task;
            cancellation.Cancel(); await peer.SendCancelled.Task; var disposing = server.DisposeAsync().AsTask(); var secondDispose = server.DisposeAsync().AsTask();
            Require(!connecting.IsCompleted && !disposing.IsCompleted && !secondDispose.IsCompleted && peer.Disposals == 0, "Cancellation released owners before unwind.");
            peer.ReleaseSend.TrySetResult(); Require(Error(await connecting), "Cancelled connection succeeded."); await Task.WhenAll(disposing, secondDispose);
            Require(peer.Disposals == 1, "Cancelled transport was not disposed once.");
        });
        await Case("explicit Plain UDP calls retain the compatible factory route", async () =>
        {
            using var fixture = new Fixture(); var plain = new PlainPeer(); var transports = 0;
            var factory = new KnxConnectionFactory(new(fixture.Directory), fixture.Provider, new Serial(), _ => { transports++; return plain; }, (_, _) => Local);
            await using var server = new McpServer(factory);
            var result = await Call(server, "bus_connect", new JsonObject { ["mode"] = "plain_udp", ["gateway"] = "127.0.0.1", ["own_address"] = "1.1.240" });
            Require(!Error(result) && fixture.Provider.Acquisitions == 0 && transports == 1, "Explicit plain changed credentials or bypassed factory.");
            await Call(server, "bus_disconnect", new JsonObject()); Require(plain.Disposals == 1, "Plain owner did not drain.");
        });
        await Case("project replacement drains secure ownership before clearing selected profile", async () =>
        {
            using var fixture = new Fixture(); await using var server = new McpServer(fixture.Factory);
            Require(!Error(await Call(server, "bus_connect", fixture.Arguments())), "Connect failed.");
            Require(!Error(await Call(server, "project_new", new JsonObject { ["name"] = "Synthetic replacement" })), "Project replacement failed.");
            Require(fixture.Transports.Single().Disposals == 1, "Project switch retained old credential/network ownership.");
        });
        await Case("management CLI profile selectors and errors cannot expose reference values", async () =>
        {
            using var fixture = new Fixture();
            var args = fixture.CliArguments(); var malformed = args.Select(value => value.StartsWith("--keyring-path=") ? value + "\0SyntheticSecretValue" : value).ToArray();
            var result = await Cli(fixture.Factory, malformed);
            Require(result.Code != 0 && !result.Output.Contains(fixture.Path) && !result.Output.Contains("SyntheticSecretValue") && fixture.Provider.Acquisitions == 0, "CLI construction leaked or performed IO.");
            result = await Cli(fixture.Factory, [.. args, "--keyring-password=SyntheticSecretValue"]);
            Require(result.Code == 2 && !result.Output.Contains("SyntheticSecretValue") && fixture.Transports.Count == 0, "CLI accepted or printed password value.");
            args = fixture.CliArguments("1.1.241"); result = await Cli(fixture.Factory, args);
            Require(result.Code == 1 && result.Output.Contains("AssignedAddressMismatch") && fixture.Transports.Single().Disposals == 1, "CLI failed to enforce actual assigned IA.");
        });
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> ready) { for (var index = 0; index < 1000 && !ready(); index++) await Task.Delay(2); Require(ready(), "Fixture condition timed out."); }
    private static bool Error(JsonNode response) => response["result"]?["isError"]?.GetValue<bool>() == true;
    private static async Task<JsonNode> Call(McpServer server, string name, JsonObject args, CancellationToken ct = default)
        => JsonNode.Parse((await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } }.ToJsonString(), ct))!)!;
    private static async Task<(JsonNode Response, string Output)> Captured(McpServer server, string name, JsonObject args)
    {
        var previous = Console.Error; using var output = new StringWriter();
        try { Console.SetError(output); var response = await Call(server, name, args); return (response, response.ToJsonString() + output); }
        finally { Console.SetError(previous); }
    }
    private static async Task<(int Code, string Output)> Cli(KnxConnectionFactory factory, string[] args)
    {
        var method = typeof(McpServer).Assembly.GetType("Knx.Mcp.ManagementProbe")!.GetMethod("RunWithFactoryAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousOut = Console.Out; var previousError = Console.Error; using var output = new StringWriter();
        try { Console.SetOut(output); Console.SetError(output); var code = await (Task<int>)method.Invoke(null, ["127.0.0.1", args, factory])!; return (code, output.ToString()); }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "buschord-mcp-secure-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "synthetic-private-reference.knxkeys");
        internal Credentials Provider { get; } = new(); internal List<IndependentSecureGateway> Transports { get; } = [];
        internal bool BadGateway, HoldSend;
        internal KnxConnectionFactory Factory { get; }
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Factory = new(new(Directory), Provider, new Serial(), _ => { var peer = new IndependentSecureGateway { BadGateway = BadGateway, HoldSendUnwind = HoldSend,
                RejectUser = false, HoldReceiveUnwind = false, SuppressResponse = false, FailKeepalive = false }; Transports.Add(peer); return peer; }, (_, _) => Local);
        }
        internal JsonObject Arguments() => new() { ["mode"] = "ip_secure_udp", ["gateway"] = "127.0.0.1", ["own_address"] = "1.1.240",
            ["keyring_path"] = Path, ["keyring_project"] = "Synthetic MCP project", ["keyring_password_env"] = "SYNTHETIC_PASSWORD_ENV", ["host_address"] = "1.1.0", ["user_id"] = 2, ["expected_keyring_sha256"] = Fingerprint };
        internal string[] CliArguments(string own = "1.1.240") => ["--management-probe=127.0.0.1", "--target=1.1.1", "--transport=ip_secure_udp", "--own-address=" + own,
            "--keyring-path=" + Path, "--keyring-project=Synthetic MCP project", "--keyring-password-env=SYNTHETIC_PASSWORD_ENV", "--host-address=1.1.0", "--user-id=2", "--expected-keyring-sha256=" + Fingerprint];
        public void Dispose() { try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } }
    }
    private sealed class Credentials : IKnxSecureCredentialProvider
    {
        internal int Acquisitions; internal SecureKeyringReference? Last; internal Exception? Error; internal string ReturnedFingerprint = Fingerprint;
        public Task<KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? passwordSource, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Acquisitions++; Last = reference;
            if (Error is not null) throw Error;
            return Task.FromResult(new KnxSecureCredentialScope(reference.HostAddress, reference.TunnelAddress, reference.UserId, ReturnedFingerprint,
                Enumerable.Range(16, 16).Select(value => (byte)value).ToArray(), Enumerable.Range(32, 16).Select(value => (byte)value).ToArray()));
        }
    }
    private sealed class Serial : IKnxClientSerialProvider { public byte[] ReadOrCreate() => [1, 2, 3, 4, 5, 6]; }
    private sealed class PlainPeer : IUdpTransport
    {
        private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>(); internal int Disposals;
        public Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); var frame = KnxNetIpFrame.Decode(data);
            var response = frame.Service switch
            {
                ServiceType.ConnectRequest => KnxNetIpFrame.Encode(ServiceType.ConnectResponse, [7, 0, .. Hpai.Control(Gateway), .. Crd.Tunnel(Assigned)]),
                ServiceType.DisconnectRequest => KnxNetIpFrame.Encode(ServiceType.DisconnectResponse, [7, 0]),
                ServiceType.ConnectionStateRequest => KnxNetIpFrame.Encode(ServiceType.ConnectionStateResponse, [7, 0]),
                _ => throw new InvalidOperationException("Unexpected plain fixture service."),
            };
            _incoming.Writer.TryWrite(new(response, Gateway)); return Task.CompletedTask;
        }
        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default) => _incoming.Reader.ReadAsync(ct).AsTask();
        public void Dispose() => Disposals++;
    }
}
