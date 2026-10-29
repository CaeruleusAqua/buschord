using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;
using Knx.Mcp;

internal static class ToolAccessCliChecks
{
    private static readonly IndividualAddress Local = new(0x11F0), Target = new(0x1101), Host = new(0x1100);
    private static readonly byte[] Integrity = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ToolKey = Convert.FromHexString("AEAC47C4653ED0B25249B4AB3F474479");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task<int> RunAsync()
    {
        var failed = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("explicit offline Tool preview preserves review and reads no secrets", Preview),
            ("missing trust confirmation and conflicting selectors open nothing", SelectorRefusals),
            ("unknown/raw secret configuration and malformed refs are redacted", ConfigurationRefusals),
            ("missing state or integrity capability never creates a transport", CapabilityRefusals),
            ("corrupt or restore-blocked state refuses before password/network", StateRefusals),
            ("named integrity source requires canonical capability and observes cancellation", EnvironmentCapability),
            ("read-only identity fits capacity25 without promoting write permissions", ReadOnly),
            ("complete original and target budget rejects capacity28 before open/unload", WireTooSmall),
            ("capacity29 changes EEPROM and joins fresh pre/postverify owners", Change),
            ("server-verified writes fit the same complete capacity29 boundary", ServerVerifiedChange),
            ("uncertain write restores the entire original through fresh Tool connections", AutomaticRecovery),
            ("caller cancellation cannot cancel independent Tool restoration", CancellationRecovery),
            ("explicit cold recovery uses the bound full original and same wire budget", ColdRecovery),
            ("standalone MCP Tool read is explicit, read-only and secret-safe", McpRead),
            ("legacy MCP tools refuse Tool references before any plaintext path", McpRefusal),
            ("occupied standalone MCP read leaves the existing gateway owner intact", McpOccupied),
            ("MCP request cancellation and server disposal join capability acquisition", McpCancellation),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(60)); Console.WriteLine("PASS ToolCli " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL ToolCli " + name + ": " + error); }
        }
        return failed;
    }
    private static async Task Preview()
    {
        using var fixture = new Fixture();
        var result = await fixture.Run([.. fixture.ChangeArguments, "--m112-preview"]);
        Require(result.Code == 0 && JsonNode.Parse(result.Output)!["devicePreflightPerformed"]!.GetValue<bool>() == false
            && fixture.Opens == 0 && fixture.IntegrityCopies.Count == 0 && fixture.Password.Reads == 0, result.Error);
        var fingerprint = JsonNode.Parse(result.Output)!["Fingerprint"]!.GetValue<string>();
        Require(fingerprint.Length == 64 && result.Error.Contains("does not detect restored valid backups"), "Preview lost the plan binding or explicit trust boundary.");
        var denied = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + new string('0', 64)]);
        Require(denied.Code != 0 && fixture.Opens == 0 && fixture.Password.Reads == 0, "Stale review reached Tool acquisition.");
    }
    private static async Task SelectorRefusals()
    {
        using var fixture = new Fixture();
        foreach (var args in new[]
        {
            fixture.ReadArguments.Where(a => !a.StartsWith("--tool-current-state-confirm=", StringComparison.Ordinal)).ToArray(),
            fixture.ReadArguments.Append("--transport=plain_udp").ToArray(),
            fixture.ReadArguments.Select(a => a.StartsWith("--target=", StringComparison.Ordinal) ? "--target=1.1.2" : a).ToArray(),
        })
        {
            var result = await fixture.Run(args); Require(result.Code == 2 && fixture.Opens == 0 && fixture.IntegrityCopies.Count == 0, result.Error);
        }
    }
    private static async Task ConfigurationRefusals()
    {
        using var fixture = new Fixture(); var original = fixture.Config.ToJsonString();
        foreach (var mutate in new Action<JsonObject>[]
        {
            node => node["tool_key"] = "secret-must-not-echo",
            node => node["counter_integrity_env"] = "secret-invalid=value",
            node => node["state_trust"] = "automatic",
            node => node["ordinary_p2p_key_sha256"] = new JsonArray("not-a-key-fingerprint"),
            node => node["counter_state_path"] = "relative-private-state",
        })
        {
            var changed = JsonNode.Parse(original)!.AsObject(); mutate(changed); await File.WriteAllTextAsync(fixture.ConfigPath, changed.ToJsonString());
            var result = await fixture.Run(fixture.ReadArguments);
            Require(result.Code == 2 && fixture.Opens == 0 && !result.Error.Contains("secret-") && !result.Error.Contains(fixture.Root), "Configuration error echoed a value/path or opened a transport.");
        }
        await File.WriteAllTextAsync(fixture.ConfigPath, original);
        var raw = await fixture.Run([.. fixture.ReadArguments, "--tool-integrity-key=secret-command-value"]);
        Require(raw.Code == 2 && !raw.Error.Contains("secret-command-value"), "Raw secret argument was accepted or echoed.");
    }
    private static async Task CapabilityRefusals()
    {
        using var fixture = new Fixture();
        var missing = await fixture.Run(fixture.ReadArguments);
        Require(missing.Code != 0 && fixture.Opens == 0 && fixture.IntegrityCopies.Count == 0 && !File.Exists(fixture.StatePath), "Missing state bootstrapped or requested credentials.");
        await fixture.Provision(); fixture.BadIntegrity = true;
        var bad = await fixture.Run(fixture.ReadArguments);
        Require(bad.Code == 2 && fixture.Opens == 0 && fixture.IntegrityCopies.All(bytes => bytes.All(value => value == 0))
            && !bad.Error.Contains(fixture.Root), "Integrity failure leaked or reached transport.");
    }
    private static async Task ReadOnly()
    {
        using var fixture = new Fixture(); await fixture.Provision(); fixture.Config["wire_tpdu_capacity"] = 25; fixture.SaveConfig();
        var result = await fixture.Run(fixture.ReadArguments);
        Require(result.Code == 0 && JsonNode.Parse(result.Output)!["readOnly"]!.GetValue<bool>() && fixture.Device.Writes == 0
            && fixture.Device.Unloads == 0 && fixture.Device.MaxReply <= 25, result.Error);
        fixture.RequireCleared();
    }
    private static async Task StateRefusals()
    {
        foreach (var corrupt in new[] { false, true })
        {
            using var fixture = new Fixture(); await fixture.Provision();
            if (corrupt) await File.WriteAllBytesAsync(fixture.StatePath, new byte[8192]);
            else
            {
                await using var store = await DataSecureCounterStore.OpenAsync(fixture.StatePath,
                    KnxIndividualSecureCounterBinding.ToolAccess("Synthetic CLI physical scope", "cli-tool-v1", Local).Identity, Integrity);
                await store.MarkSynchronizationRequiredAsync();
            }
            var result = await fixture.Run(fixture.ReadArguments);
            Require(result.Code != 0 && fixture.Opens == 0 && fixture.Password.Reads == 0 && !result.Error.Contains(fixture.Root),
                "An invalid existing state reached password import/network or leaked a path.");
            fixture.RequireCleared();
        }
    }
    private static async Task EnvironmentCapability()
    {
        const string name = "BUSCHORD_SYNTHETIC_TOOL_INTEGRITY_TEST";
        var previous = Environment.GetEnvironmentVariable(name);
        var type = typeof(McpServer).Assembly.GetType("Knx.Mcp.EnvironmentToolIntegrityCapabilitySource")!;
        var source = Activator.CreateInstance(type, nonPublic: true)!;
        var read = type.GetMethod("ReadOwnedAsync")!;
        async Task<byte[]> Read(CancellationToken ct = default) => await (ValueTask<byte[]>)read.Invoke(source, [name, ct])!;
        try
        {
            Environment.SetEnvironmentVariable(name, Convert.ToBase64String(Integrity));
            var bytes = await Read(); try { Require(bytes.SequenceEqual(Integrity), "Canonical named capability changed."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            foreach (var invalid in new[] { " " + Convert.ToBase64String(Integrity), Convert.ToBase64String(new byte[31]), new string('A', 44) })
            {
                Environment.SetEnvironmentVariable(name, invalid); var refused = false;
                try { _ = await Read(); } catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "ToolConsoleException") { refused = true; }
                Require(refused, "Noncanonical named capability was accepted.");
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = false;
            try { _ = await Read(cancel.Token); } catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Named capability ignored cancellation.");
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }
    private static async Task WireTooSmall()
    {
        using var fixture = new Fixture(); await fixture.Provision(); var fingerprint = await fixture.PreviewFingerprint();
        var budget = M112ApplicationWireBudget.ForImages(fixture.Baseline, fixture.Desired);
        Require(budget.RequiredToolWireTpduLength == 29 && budget.MaximumPlainRequestLength == 16 && budget.MaximumPlainReplyLength == 16
            && fixture.Baseline.Segments.Any(s => s.Size > M112ApplicationDownload.MemoryTransferBlockLength), "Fixture does not prove the complete-block boundary.");
        fixture.Config["wire_tpdu_capacity"] = 28; fixture.SaveConfig();
        var result = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + fingerprint]);
        Require(result.Code == 2 && result.Error.Contains("ToolWireCapacityRejected") && fixture.Opens == 0 && fixture.Device.Writes == 0
            && fixture.Device.Unloads == 0 && fixture.IntegrityCopies.Count == 0, "Insufficient full rollback capacity reached acquisition or unload.");
    }
    private static async Task Change()
    {
        using var fixture = new Fixture(); await fixture.Provision(); var fingerprint = await fixture.PreviewFingerprint();
        var result = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + fingerprint]);
        Require(result.Code == 0 && JsonNode.Parse(result.Output)!["DownloadedAndVerified"]!.GetValue<bool>() && fixture.Device.Memory[0x4420] == 2
            && fixture.Device.Memory[0x4000] == 2 && fixture.Device.Memory[0x4003] == 0x12 && fixture.Device.Memory[0x4004] == 0x34
            && fixture.Device.Memory[0x4201] == 1
            && fixture.Opens >= 3 && fixture.Password.Reads == 1 && fixture.IntegrityCopies.Count == 1 && fixture.Device.MaxRequest == 29
            && fixture.Device.MaxReply == 29 && fixture.Device.SyncSequences.Distinct().Count() == fixture.Device.SyncSequences.Count, result.Error);
        fixture.RequireCleared();
    }
    private static async Task AutomaticRecovery()
    {
        using var fixture = new Fixture(); await fixture.Provision(); var original = fixture.Device.Memory.ToArray(); fixture.Device.FailWrite = true;
        var result = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + await fixture.PreviewFingerprint()]);
        var json = JsonNode.Parse(result.Output)!;
        Require(result.Code == 1 && json["RollbackAttempted"]!.GetValue<bool>() && json["OriginalConfigurationRestoredAndVerified"]!.GetValue<bool>()
            && fixture.Baseline.Segments.Where(s => s.MemoryType == 3).All(s => fixture.Device.Memory.AsSpan(s.Address, s.Size).SequenceEqual(original.AsSpan(s.Address, s.Size)))
            && fixture.Password.Reads == 1 && fixture.IntegrityCopies.Count == 1 && fixture.Opens >= 3, result.Error);
        fixture.RequireCleared();
    }
    private static async Task ServerVerifiedChange()
    {
        using var fixture = new Fixture(); await fixture.Provision(); fixture.Device.ServerVerify = true;
        var result = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + await fixture.PreviewFingerprint()]);
        Require(result.Code == 0 && fixture.Device.VerifiedWrites > 0 && fixture.Device.Memory[0x4420] == 2
            && fixture.Device.MaxRequest == 29 && fixture.Device.MaxReply == 29, result.Error);
        fixture.RequireCleared();
    }
    private static async Task CancellationRecovery()
    {
        using var fixture = new Fixture(); await fixture.Provision(); using var cancel = new CancellationTokenSource(); fixture.Device.OnWrite = cancel.Cancel;
        var result = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + await fixture.PreviewFingerprint()], cancel.Token);
        Require(result.Code == 1 && JsonNode.Parse(result.Output)!["OriginalConfigurationRestoredAndVerified"]!.GetValue<bool>()
            && fixture.Device.Memory[0x4420] == 1 && fixture.Opens >= 3 && fixture.Password.Reads == 1, "Caller cancellation escaped restoration: " + result.Error);
        fixture.RequireCleared();
    }
    private static async Task ColdRecovery()
    {
        using var fixture = new Fixture(); await fixture.Provision();
        var first = await fixture.Run([.. fixture.ChangeArguments, "--review-fingerprint=" + await fixture.PreviewFingerprint()]); Require(first.Code == 0, first.Error);
        var recovery = Directory.GetFiles(fixture.Journal, "before-image.json", SearchOption.AllDirectories).Single();
        fixture.Device.SetUnloaded(); fixture.Config["wire_tpdu_capacity"] = 28; fixture.SaveConfig(); var oldOpens = fixture.Opens;
        var args = fixture.ReadArguments.Concat(["--m112-recover=" + recovery, "--m112-recovery-confirm=1.1.1", "--expected-identity=" + fixture.IdentityPath]).ToArray();
        var denied = await fixture.Run(args); Require(denied.Code == 2 && fixture.Opens == oldOpens, "Undersized cold recovery reached the device.");
        fixture.Config["wire_tpdu_capacity"] = 29; fixture.SaveConfig();
        var result = await fixture.Run(args);
        Require(result.Code == 0 && JsonNode.Parse(result.Output)!["OriginalConfigurationRestoredAndVerified"]!.GetValue<bool>()
            && fixture.Device.Memory[0x4420] == 1 && fixture.Device.UnloadedAppReads == 0, result.Error);
        fixture.RequireCleared();
    }
    private static async Task McpRead()
    {
        using var fixture = new Fixture(); await fixture.Provision(); await using var server = fixture.CreateServer();
        var args = new JsonObject { ["gateway"] = "127.0.0.1", ["target"] = "1.1.1", ["tool_access_config"] = fixture.ConfigPath, ["confirm_current_state"] = "1.1.1" };
        var reply = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "bus_tool_identity_read", ["arguments"] = args } }.ToJsonString());
        var result = JsonNode.Parse(reply!)!["result"]!;
        Require(result["isError"] is null && result["structuredContent"]!["readOnly"]!.GetValue<bool>()
            && result["structuredContent"]!["counterRollbackProtection"]!.GetValue<string>() == "NotProvided" && fixture.Device.Writes == 0
            && !reply!.Contains(fixture.Root) && !reply.Contains("SYNTHETIC_INTEGRITY"), reply!);
        fixture.RequireCleared();
    }
    private static async Task McpRefusal()
    {
        using var fixture = new Fixture(); await using var server = fixture.CreateServer();
        foreach (var name in server.Tools.Where(tool => tool.Name.StartsWith("bus_", StringComparison.Ordinal)).Select(tool => tool.Name).Append("download_plan"))
        {
            var args = new JsonObject { ["gateway"] = "127.0.0.1", ["address"] = "1.1.1", ["tool_access_config"] = fixture.ConfigPath };
            var reply = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } }.ToJsonString());
            Require(JsonNode.Parse(reply!)!["result"]!["isError"]!.GetValue<bool>() && fixture.Opens == 0 && !reply!.Contains(fixture.Root)
                && !reply.Contains("secret-no-echo"), "MCP selector refusal leaked or reached networking.");
        }
    }
    private static async Task McpOccupied()
    {
        using var fixture = new Fixture(); await fixture.Provision(); await using var server = fixture.CreateServer();
        await using (var existing = await fixture.HoldGateway())
        {
            var reply = await server.HandleAsync(fixture.McpRequest());
            Require(reply!.Contains("Occupied") && fixture.Opens == 0 && fixture.Password.Reads == 0, "Standalone Tool read displaced an existing owner.");
            var stillOccupied = await server.HandleAsync(fixture.McpRequest());
            Require(stillOccupied!.Contains("Occupied"), "A refused Tool job disposed the unrelated owner.");
        }
        var fresh = await server.HandleAsync(fixture.McpRequest());
        Require(JsonNode.Parse(fresh!)!["result"]!["isError"] is null && fixture.Opens == 1, "Disposed external reservation remained occupied.");
        fixture.RequireCleared();
    }
    private static async Task McpCancellation()
    {
        foreach (var disposeServer in new[] { false, true })
        {
            using var fixture = new Fixture(); await fixture.Provision(); await using var server = fixture.CreateServer();
            var entered = Gate(); var unwound = Gate(); var release = Gate(); byte[] owned = Integrity.ToArray();
            fixture.ConfigureServer(server, async (_, ct) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return owned; }
                finally { await release.Task; CryptographicOperations.ZeroMemory(owned); unwound.TrySetResult(); }
            });
            using var cancel = new CancellationTokenSource(); var request = server.HandleAsync(fixture.McpRequest(), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task? disposing = null; if (disposeServer) disposing = server.DisposeAsync().AsTask(); else cancel.Cancel();
            Require(!request.IsCompleted && (disposing is null || !disposing.IsCompleted), "Cancelled owner returned before capability unwind.");
            release.TrySetResult(); var reply = await request; if (disposing is not null) await disposing;
            Require(reply!.Contains("Cancelled") && unwound.Task.IsCompleted && owned.All(value => value == 0) && fixture.Opens == 0,
                "Cancellation/disposal leaked, opened a tunnel, or did not join the provider.");
        }
    }
    private sealed class Password : ISecurePasswordSource
    {
        public int Reads; public List<char[]> Copies { get; } = [];
        public ValueTask<char[]> ReadOwnedAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Reads++; var copy = "pwd".ToCharArray(); Copies.Add(copy); return ValueTask.FromResult(copy); }
    }
    private sealed class Credentials : IKnxToolAccessCredentialProvider
    {
        public List<byte[]> Copies { get; } = [];
        public async Task<KnxToolAccessCredentialScope> AcquireAsync(SecureKeyringReference reference, ISecurePasswordSource? password,
            IndividualAddress target, IReadOnlyList<string> inventory, CancellationToken ct)
        {
            var scope = await new ProtectedToolAccessCredentialProvider().AcquireAsync(reference, password, target, inventory, ct);
            foreach (var owner in new object[] { scope, scope.Gateway }) Copies.AddRange(owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(byte[])).Select(field => (byte[])field.GetValue(owner)!));
            return scope;
        }
    }
    private sealed class Serial : IKnxClientSerialProvider { public byte[] ReadOrCreate() => [1, 2, 3, 4, 5, 6]; }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "buschord-tool-cli-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Root, "tool.json"); public string StatePath => Path.Combine(Root, "counter.bin");
        public string IdentityPath => Path.Combine(Root, "identity.json"); public string Journal => Path.Combine(Root, "journal");
        public JsonObject Config { get; }
        public Password Password { get; } = new(); public Credentials Credentials { get; } = new(); public List<byte[]> IntegrityCopies { get; } = [];
        public Device Device { get; } public M112ApplicationImage Baseline { get; } public M112ApplicationImage Desired { get; }
        public int Opens; public bool BadIntegrity; private readonly KnxConnectionFactory _factory;
        public string[] ReadArguments => ["--management-probe=127.0.0.1", "--target=1.1.1", "--tool-access-config=" + ConfigPath, "--tool-current-state-confirm=1.1.1"];
        public string[] ChangeArguments { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var baseline = (string)typeof(ManagementChangeCliChecks).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Root])!;
            var project = Knxproj.Load(baseline); Baseline = OfflineM112ApplicationBuild.Build(project, ProjectQuery.DeviceList(project).Single());
            var targetDevice = ProjectQuery.DeviceList(project).Single(); targetDevice.ParameterValues["R"] = "2";
            var range = new GroupRange { Id = "ToolRange", Name = "Synthetic Tool links", RangeStart = 0, RangeEnd = 65535 };
            range.Addresses.Add(new GroupAddressEntry { Id = "ToolGA", Name = "Synthetic group", Address = new(0x1234) });
            project.Installations.Single().MainRanges.Add(range); project.Workbench.Groups["ToolGA"] = new() { Secure = false };
            var linked = new ComObjectInstanceRef { Id = "OR", ComObjectId = "O", Number = 0 }; linked.Links.Add("ToolGA"); targetDevice.ComObjectRefs.Add(linked);
            var desired = Path.Combine(Root, "desired.knxproj"); Knxproj.Save(project, desired);
            var changed = Knxproj.Load(desired); Desired = OfflineM112ApplicationBuild.Build(changed, ProjectQuery.DeviceList(changed).Single()); Device = new(Baseline);
            File.WriteAllText(IdentityPath, JsonSerializer.Serialize(new ManagementDeviceIdentity("1.1.1", 0x0701, "0001", "000100000001", "0001010010")));
            var ring = XElement.Parse((string)typeof(ProtectedKnxKeyringChecks).GetField("Rich", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!); var ns = ring.Name.Namespace;
            var selected = ring.Elements(ns + "Interface").First(); selected.SetAttributeValue("Host", Host.ToString()); selected.SetAttributeValue("IndividualAddress", Local.ToString());
            ring.Element(ns + "Devices")!.Element(ns + "Device")!.SetAttributeValue("IndividualAddress", Target.ToString());
            var xml = (string)typeof(ProtectedKnxKeyringChecks).GetMethod("SignForNegativeCase", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [ring])!;
            var keyring = Path.Combine(Root, "synthetic.knxkeys"); File.WriteAllText(keyring, xml);
            Config = new() { ["format"] = "BusChord.ToolAccessManagement.v1", ["gateway"] = "127.0.0.1", ["port"] = 3671, ["host_address"] = Host.ToString(),
                ["user_id"] = 4, ["own_address"] = Local.ToString(), ["target"] = Target.ToString(), ["keyring_path"] = keyring,
                ["keyring_project"] = "BusChord synthetic keyring", ["keyring_password_env"] = "SYNTHETIC_PASSWORD", ["expected_keyring_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keyring))),
                ["installation_scope"] = "Synthetic CLI physical scope", ["tool_key_epoch"] = "cli-tool-v1", ["wire_tpdu_capacity"] = 29,
                ["ordinary_p2p_key_sha256"] = new JsonArray(), ["counter_state_path"] = StatePath, ["counter_integrity_env"] = "SYNTHETIC_INTEGRITY",
                ["state_trust"] = "provisioner_confirmed_current_unanchored" };
            SaveConfig(); ChangeArguments = [.. ReadArguments, "--m112-change-confirm=1.1.1", "--baseline-project=" + baseline, "--project=" + desired,
                "--expected-identity=" + IdentityPath, "--journal-directory=" + Journal];
            _factory = new(new(Path.Combine(Root, "leases")), serial: new Serial(), createTransport: _ =>
            { Opens++; var peer = new Peer(Device); return peer.Gateway; }, localEndpoint: (_, _) => new(IPAddress.Loopback, 43123));
        }
        public void SaveConfig() => File.WriteAllText(ConfigPath, Config.ToJsonString());
        public async Task Provision()
        { await using var store = await DataSecureCounterStore.InitializeAsync(StatePath, KnxIndividualSecureCounterBinding.ToolAccess("Synthetic CLI physical scope", "cli-tool-v1", Local).Identity,
            Integrity, new(10, new Dictionary<IndividualAddress, ulong> { [Target] = 1 })); }
        public async Task<string> PreviewFingerprint()
        { var result = await Run([.. ChangeArguments, "--m112-preview"]); Require(result.Code == 0, result.Error); return JsonNode.Parse(result.Output)!["Fingerprint"]!.GetValue<string>(); }
        public async Task<(int Code, string Output, string Error)> Run(string[] args, CancellationToken ct = default)
        {
            var method = typeof(McpServer).Assembly.GetType("Knx.Mcp.ManagementProbe")!.GetMethod("RunWithToolFactoryAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            Func<string, CancellationToken, Task<byte[]>> integrity = (name, token) =>
            { token.ThrowIfCancellationRequested(); Require(name == "SYNTHETIC_INTEGRITY", "Unvalidated reference reached the integrity provider."); var copy = Integrity.Take(BadIntegrity ? 31 : 32).ToArray(); IntegrityCopies.Add(copy); return Task.FromResult(copy); };
            var oldOut = Console.Out; var oldError = Console.Error; using var output = new StringWriter(); using var error = new StringWriter();
            try { Console.SetOut(output); Console.SetError(error); var code = await (Task<int>)method.Invoke(null, ["127.0.0.1", args, _factory, integrity, Password, Credentials, ct])!; return (code, output.ToString(), error.ToString()); }
            finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        }
        public McpServer CreateServer()
        {
            var server = new McpServer(_factory);
            Func<string, CancellationToken, Task<byte[]>> integrity = (name, token) => { token.ThrowIfCancellationRequested(); Require(name == "SYNTHETIC_INTEGRITY", "Unexpected provider reference."); var copy = Integrity.ToArray(); IntegrityCopies.Add(copy); return Task.FromResult(copy); };
            ConfigureServer(server, integrity);
            return server;
        }
        public void ConfigureServer(McpServer server, Func<string, CancellationToken, Task<byte[]>> integrity) =>
            typeof(McpServer).GetMethod("ConfigureToolAccessProviders", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [integrity, Password, Credentials]);
        public Task<KnxGatewayOperation> HoldGateway() => _factory.AcquireOperationAsync(KnxConnectionProfile.Plain(new(IPAddress.Loopback, 3671), Local));
        public string McpRequest() => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "bus_tool_identity_read", ["arguments"] = new JsonObject
                { ["gateway"] = "127.0.0.1", ["target"] = "1.1.1", ["tool_access_config"] = ConfigPath, ["confirm_current_state"] = "1.1.1" } } }.ToJsonString();
        public void RequireCleared() => Require(IntegrityCopies.All(bytes => bytes.All(value => value == 0)) && Password.Copies.All(chars => chars.All(value => value == '\0'))
            && Credentials.Copies.All(bytes => bytes.All(value => value == 0)), "CLI retained an owned integrity/password/credential copy.");
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Device
    {
        public byte[] Memory { get; } = new byte[65536]; public int Writes, Unloads, UnloadedAppReads, MaxRequest, MaxReply, VerifiedWrites; public bool FailWrite, ServerVerify; public Action? OnWrite;
        public ulong RemoteNext = 30; public List<ulong> SyncSequences { get; } = []; private readonly byte[] _hardware;
        public Device(M112ApplicationImage image) { _hardware = image.HardwareType.ToArray(); foreach (var s in image.Segments) s.Data.ToArray().CopyTo(Memory, s.Address); Memory[0x4001] = 0x11; Memory[0x4002] = 1; Loaded(); }
        private void Loaded() { Memory[0xB6EA] = Memory[0xB6EB] = Memory[0xB6EC] = 1; }
        public void SetUnloaded() { Memory[0xB6EA] = Memory[0xB6EB] = Memory[0xB6EC] = 0; }
        public byte[]? Apply(byte[] tpdu)
        {
            var service = ManagementApdu.Service(tpdu); var address = tpdu.Length >= 4 ? BinaryPrimitives.ReadUInt16BigEndian(tpdu.AsSpan(2)) : (ushort)0;
            if (service == 0x300) return [0x03, 0x40, 0x07, 0x01];
            if (service == 0x200) { var count = tpdu[1] & 63; return [0x02, (byte)(0x40 | count), tpdu[2], tpdu[3], .. Memory.AsSpan(address, count).ToArray()]; }
            if (service == 0x280)
            {
                Writes++; var bytes = tpdu[4..];
                if (address == 0x104) { var oi = bytes[0] >> 4; var command = bytes[0] & 15; if (command == 4) Unloads++; Memory[0xB6E9 + oi] = command switch { 4 => 0, 1 or 3 => 2, 2 => 1, _ => throw new IOException("Unexpected synthetic control") }; }
                else { bytes.CopyTo(Memory, address); var cancel = OnWrite; OnWrite = null; cancel?.Invoke(); if (FailWrite) { FailWrite = false; throw new IOException("Synthetic uncertain memory write"); } }
                if (address != 0x104 && ServerVerify) { VerifiedWrites++; return [0x02, (byte)(0x40 | bytes.Length), tpdu[2], tpdu[3], .. bytes]; }
                return null;
            }
            if (service == 0x380) return [0x81];
            if (service == ManagementApdu.PropertyRead)
            {
                var oi = tpdu[2]; var pid = tpdu[3];
                if (oi == 3 && pid == 13 && Memory[0xB6EC] != 1) { UnloadedAppReads++; return [0x03, 0xD6, oi, pid, (byte)(tpdu[4] & 15), tpdu[5]]; }
                byte[] data = (oi, pid) switch { (0, 12) => [0, 1], (0, 11) => [0, 1, 0, 0, 0, 1], (3, 13) => [0, 1, 1, 0, 0x10],
                    (0, 14) => [ServerVerify ? (byte)4 : (byte)0], (0, 78) => _hardware, (1 or 2 or 3, 5) => [Memory[0xB6E9 + oi]], (1, 7) => [0x40, 0], (2, 7) => [0x42, 1],
                    (3, 6) => [1], (3, 16) => [1], _ => throw new IOException("Unexpected synthetic property") };
                return [0x03, 0xD6, oi, pid, tpdu[4], tpdu[5], .. data];
            }
            throw new IOException("Unexpected synthetic management service");
        }
    }
    private sealed class Peer
    {
        public IndependentSecureGateway Gateway { get; }
        private readonly Device _device; private byte _incoming, _transport; private bool _synced;
        public Peer(Device device)
        {
            _device = device; Gateway = new() { ExpectedUserId = 4, AssignedAddressOverride = Local,
                UserKeyOverride = Rfc2898DeriveBytes.Pbkdf2("user4", Encoding.ASCII.GetBytes("user-password.1.secure.ip.knx.org"), 65536, HashAlgorithmName.SHA256, 16),
                DeviceKeyOverride = Rfc2898DeriveBytes.Pbkdf2("dev", Encoding.ASCII.GetBytes("device-authentication-code.1.secure.ip.knx.org"), 65536, HashAlgorithmName.SHA256, 16), ApplicationPacket = Process };
        }
        private static byte[] Frame(ServiceType type, byte[] body) => KnxNetIpFrame.Encode(type, body);
        private byte[] Incoming(byte[] cemi) => Frame(ServiceType.TunnellingRequest, Tunnelling.Request(7, _incoming++, cemi));
        private IReadOnlyList<byte[]>? Process(byte[] packet)
        {
            var frame = KnxNetIpFrame.Decode(packet); if (frame.Service != ServiceType.TunnellingRequest) return null;
            var (_, sequence, raw) = Tunnelling.ReadRequest(frame.Body); var cemi = Cemi.ParseLDataFrame(raw); var tpdu = cemi.Apdu;
            var result = new List<byte[]> { Frame(ServiceType.TunnellingAck, Tunnelling.Ack(7, sequence)) };
            if (tpdu.Length == 1) { if (tpdu[0] == 0x80) Require(_synced, "Management published before authenticated Sync."); return result; }
            if (tpdu[2] == 0x92)
            {
                Require(KnxIndividualSecureCodec.TryUnprotectSyncRequest(raw, ToolKey, KnxIndividualSecureKeyKind.ToolAccess, out var sync, out _), "Invalid Tool Sync.");
                _device.SyncSequences.Add(sync!.Sequence);
                try { result.Add(Incoming(KnxIndividualSecureCodec.ProtectSyncResponse(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw, [0, 0]), sync.Challenge,
                    0xAABBCCDDEEFF, _device.RemoteNext, 50, ToolKey, KnxIndividualSecureKeyKind.ToolAccess))); _synced = true; }
                finally { CryptographicOperations.ZeroMemory(sync.Challenge); } return result;
            }
            var valid = KnxIndividualSecureCodec.TryUnprotectData(raw, ToolKey, KnxIndividualSecureKeyKind.ToolAccess, out var decoded, out _); Require(valid && _synced, "Plain management fallback.");
            _device.MaxRequest = Math.Max(_device.MaxRequest, tpdu.Length);
            try
            {
                var response = _device.Apply(decoded!.Tpdu); result.Add(Incoming(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw, [(byte)(0xC2 | (decoded.Tpdu[0] & 0x3C))]))));
                if (response is [0x81]) result.Add(Incoming(Cemi.EncodeLDataFrame(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw, response))));
                else if (response is not null)
                {
                    response[0] = (byte)((response[0] & 3) | 0x40 | (_transport++ << 2)); _transport &= 15;
                    var secure = KnxIndividualSecureCodec.ProtectData(new(CemiMessageCode.LDataInd, 0xB0, 0x60, Target, Local.Raw, response), _device.RemoteNext++, ToolKey, KnxIndividualSecureKeyKind.ToolAccess);
                    _device.MaxReply = Math.Max(_device.MaxReply, Cemi.ParseLDataFrame(secure).Apdu.Length); result.Add(Incoming(secure));
                }
            }
            finally { CryptographicOperations.ZeroMemory(decoded!.Tpdu); }
            return result;
        }
    }
}
