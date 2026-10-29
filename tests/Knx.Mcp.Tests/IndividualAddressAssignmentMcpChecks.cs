using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Knx.Mcp;

internal static class IndividualAddressAssignmentMcpChecks
{
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Case(string name, Func<Task> action)
        { try { await action().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine("PASS Address MCP " + name); } catch (Exception error) { failures++; Console.WriteLine("FAIL Address MCP " + name + ": " + error); } }
        void Require(bool condition) { if (!condition) throw new Exception("Address MCP invariant failed."); }
        await Case("stdio-friendly accepted jobs expose status/cancel and keep global write gates closed", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory());
            var accepted = Payload(await Call(server, "address_assignment_review", Args(f))); var id = accepted["operationId"]!.GetValue<string>();
            Require(accepted["accepted"]!.GetValue<bool>() && server.Tools.Count(tool => tool.Name.StartsWith("address_assignment_")) == 6 && !Knx.Bus.KnxBus.WritesEnabled);
            var reviewed = await Done(server, id); Require(reviewed["failure"] is null && reviewed["ownerHeld"]!.GetValue<bool>() && f.Writes == 0);
            await Call(server, "address_assignment_cancel", new() { ["operation_id"] = id }); Require(f.Transports == f.Disposals);
        });
        await Case("invalid and Secure selector/secret arguments open zero transports and stay redacted", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory());
            foreach (var field in new[] { "mode", "confirm_plain_device", "reservation", "serial" })
            { var args = Args(f); args.Remove(field); Require(Error(await Call(server, "address_assignment_review", args))); }
            foreach (var field in new[] { "password", "tool_key", "keyring_password", "counter_path" })
            { var args = Args(f); args[field] = "SyntheticNeverEcho"; var response = await Call(server, "address_assignment_review", args); Require(Error(response) && !response.ToJsonString().Contains("SyntheticNeverEcho")); }
            var secure = Args(f); secure["requires_secure_provisioning"] = true; Require(Error(await Call(server, "address_assignment_review", secure)) && f.Transports == 0);
        });
        await Case("wrong JSON types cannot weaken external Secure/role/confirmation declarations", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory());
            foreach (var (field, value) in new (string, JsonNode?)[] { ("requires_secure_provisioning", JsonValue.Create("true")), ("confirm_coupler_hardware", JsonValue.Create("true")),
                ("observation_ms", JsonValue.Create("20")), ("requires_secure_provisioning", null), ("reservation", JsonValue.Create(12345)) })
            {
                var args = Args(f); args[field] = value;
                var response = await Call(server, "address_assignment_review", args);
                if (!Error(response)) { var id = Payload(response)["operationId"]!.GetValue<string>(); await Done(server, id); await Call(server, "address_assignment_cancel", new() { ["operation_id"] = id }); }
                Require(Error(response) && f.Transports == 0);
            }
        });
        await Case("exact apply emits once and returns independently verified durable outcome", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory());
            var review = await Review(server, f); var accepted = Payload(await Call(server, "address_assignment_apply", Apply(review)));
            var completed = await Done(server, accepted["operationId"]!.GetValue<string>());
            Require(completed["failure"] is null && completed["state"]!.GetValue<string>() == "Verified" && f.Writes == 1);
            var result = completed["payload"]!.AsObject(); var path = result["intentPath"]!.GetValue<string>();
            var inspected = Payload(await Call(server, "address_assignment_inspect", new() { ["intent_path"] = path }));
            Require(inspected["fingerprint"]!.GetValue<string>() == review["fingerprint"]!.GetValue<string>());
            var resumed = Payload(await Call(server, "address_assignment_reconcile", new() { ["intent_path"] = path, ["review_fingerprint"] = review["fingerprint"]!.GetValue<string>() }));
            Require((await Done(server, resumed["operationId"]!.GetValue<string>()))["state"]!.GetValue<string>() == "Verified" && f.Writes == 1);
        });
        await Case("missing explicit reservation confirmation never writes", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory()); var review = await Review(server, f);
            var args = Apply(review); args["confirm_reserved_target"] = false;
            var id = Payload(await Call(server, "address_assignment_apply", args))["operationId"]!.GetValue<string>();
            Require((await Done(server, id))["failure"]!.GetValue<string>() == "ReservationRequired" && f.Writes == 0);
        });
        await Case("cancel remains reachable while actual fresh verification is held", async () =>
        {
            using var f = new IndividualAddressGatewayFixture { HoldVerification = true }; await using var server = new McpServer(f.Factory()); var review = await Review(server, f);
            var id = Payload(await Call(server, "address_assignment_apply", Apply(review)))["operationId"]!.GetValue<string>(); await f.VerificationEntered.Task;
            var status = Payload(await Call(server, "address_assignment_status", new() { ["operation_id"] = id })); Require(status["busy"]!.GetValue<bool>());
            var cancel = Call(server, "address_assignment_cancel", new() { ["operation_id"] = id }); await Task.Delay(30); Require(!cancel.IsCompleted && f.Writes == 1);
            f.ReleaseVerification.TrySetResult(); var cancelled = Payload(await cancel); Require(!cancelled["busy"]!.GetValue<bool>() && f.Transports == f.Disposals && f.Writes == 1);
        });
        await Case("server close joins the same independent reconciliation before releasing owner", async () =>
        {
            using var f = new IndividualAddressGatewayFixture { HoldVerification = true }; var server = new McpServer(f.Factory()); var review = await Review(server, f);
            _ = await Call(server, "address_assignment_apply", Apply(review)); await f.VerificationEntered.Task;
            var close = server.DisposeAsync().AsTask(); Require(!close.IsCompleted && ReferenceEquals(close, server.DisposeAsync().AsTask()));
            f.ReleaseVerification.TrySetResult(); await close; Require(f.Writes == 1 && f.Transports == f.Disposals);
        });
        await Case("host-known Secure target overrides external plain attestation before I/O", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory());
            var project = Knx.Workbench.WorkbenchJson.CreateDemo(); var target = Knx.ProjectQuery.DeviceList(project).First(); target.Address = IndividualAddressGatewayFixture.Desired;
            project.Workbench.Devices[target.Id] = new() { SecureCapable = true };
            typeof(McpServer).GetField("_project", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(server, project);
            var args = Args(f); args["requires_secure_provisioning"] = false;
            var response = await Call(server, "address_assignment_review", args);
            Require(Error(response) && response.ToJsonString().Contains("UnsupportedSecureProvisioning") && f.Transports == 0);
        });
        await Case("newly known Secure target invalidates an already reviewed plain operation", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); await using var server = new McpServer(f.Factory());
            var project = Knx.Workbench.WorkbenchJson.CreateDemo(); var target = Knx.ProjectQuery.DeviceList(project).First(); target.Address = IndividualAddressGatewayFixture.Desired;
            project.Workbench.Devices[target.Id] = new() { SecureCapable = false };
            typeof(McpServer).GetField("_project", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(server, project);
            var review = await Review(server, f); var before = f.Transports; project.Workbench.Devices[target.Id] = new() { SecureCapable = true };
            Require(Error(await Call(server, "address_assignment_apply", Apply(review))) && f.Transports == before && f.Writes == 0);
        });
        foreach (var target in new[] { new Knx.IndividualAddress(1, 1, 0), new Knx.IndividualAddress(1, 1, 255) })
        await Case("explicit coupler/255 review and one-write job " + target, async () =>
        {
            using var f = new IndividualAddressGatewayFixture { ExpectedTarget = target }; await using var server = new McpServer(f.Factory());
            var args = Args(f); args["new_address"] = target.ToString(); if (target.Device == 0) { args["target_role"] = "coupler"; args["confirm_coupler_hardware"] = true; args["coupler_hardware_reference"] = "named synthetic coupler hardware"; }
            var reviewed = await Done(server, Payload(await Call(server, "address_assignment_review", args))["operationId"]!.GetValue<string>());
            Require(reviewed["failure"] is null); var review = reviewed["payload"]!.AsObject();
            var result = await Done(server, Payload(await Call(server, "address_assignment_apply", Apply(review)))["operationId"]!.GetValue<string>());
            Require(result["state"]!.GetValue<string>() == "Verified" && f.Writes == 1);
        });
        await Case("standard sequential stdio accepts a background command then status/cancel/EOF without network", async () =>
        {
            using var f = new IndividualAddressGatewayFixture(); var start = new ProcessStartInfo(Environment.ProcessPath!)
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(IndividualAddressAssignmentMcpChecks).Assembly.Location);
            start.ArgumentList.Add("--group-host-child"); using var child = Process.Start(start)!; var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                async Task<JsonObject> Rpc(string name, JsonObject args)
                {
                    await child.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } }.ToJsonString());
                    await child.StandardInput.FlushAsync(); return JsonNode.Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4)))!)!.AsObject();
                }
                // The invalid directory is rejected inside the accepted job before even creating a gateway operation.
                var args = Args(f); args["journal_directory"] = Path.Combine(f.Root, "never-created");
                var id = Payload(await Rpc("address_assignment_review", args))["operationId"]!.GetValue<string>(); JsonObject? status = null;
                for (var count = 0; count < 100; count++) { status = Payload(await Rpc("address_assignment_status", new() { ["operation_id"] = id })); if (!status["busy"]!.GetValue<bool>()) break; await Task.Delay(10); }
                Require(status?["failure"]?.GetValue<string>() == "InvalidJournal");
                Require(!Payload(await Rpc("address_assignment_cancel", new() { ["operation_id"] = id }))["busy"]!.GetValue<bool>());
                child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); Require(child.ExitCode == 0 && (await stderr).Length == 0);
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
        });
        return failures;
    }
    private static JsonObject Args(IndividualAddressGatewayFixture fixture) => new()
    {
        ["mode"] = "plain_udp", ["gateway"] = "127.0.0.1", ["own_address"] = IndividualAddressGatewayFixture.Client.ToString(), ["new_address"] = IndividualAddressGatewayFixture.Desired.ToString(),
        ["selection"] = "serial", ["serial"] = IndividualAddressGatewayFixture.DeviceSerial, ["reservation"] = "explicit synthetic reserved address", ["confirm_plain_device"] = true, ["target_role"] = "end_device",
        ["journal_directory"] = fixture.Root, ["observation_ms"] = 20
    };
    private static JsonObject Apply(JsonObject review) => new() { ["review_fingerprint"] = review["fingerprint"]!.GetValue<string>(), ["confirm_target_address"] = review["newAddress"]!.GetValue<string>(), ["confirm_reserved_target"] = true, ["confirm_physical_binding"] = true };
    private static async Task<JsonObject> Review(McpServer server, IndividualAddressGatewayFixture fixture)
    { var accepted = Payload(await Call(server, "address_assignment_review", Args(fixture))); return (await Done(server, accepted["operationId"]!.GetValue<string>()))["payload"]!.AsObject(); }
    private static async Task<JsonObject> Done(McpServer server, string id)
    {
        for (var count = 0; count < 500; count++)
        { var status = Payload(await Call(server, "address_assignment_status", new() { ["operation_id"] = id })); if (!status["busy"]!.GetValue<bool>()) return status; await Task.Delay(10); }
        throw new TimeoutException("Synthetic address command did not drain.");
    }
    private static async Task<JsonObject> Call(McpServer server, string name, JsonObject args)
        => JsonNode.Parse((await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } }.ToJsonString()))!)!.AsObject();
    private static bool Error(JsonObject value) => value["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonObject Payload(JsonObject value) => value["result"]!["structuredContent"]!.AsObject();
}
