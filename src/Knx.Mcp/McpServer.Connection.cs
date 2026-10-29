using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knx.Bus;
using Knx.Bus.Connections;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private readonly SemaphoreSlim _connectionJobs = new(1, 1);
    private readonly CancellationTokenSource _connectionLifetime = new();
    private readonly AsyncLocal<CancellationToken> _requestCancellation = new();
    private readonly KnxConnectionFactory _connections;
    private readonly IKnxGroupSecurityProfileResolver? _groupProfiles;
    private readonly object _connectionDisposeGate = new();
    private Task? _connectionDisposeTask;
    private KnxConnectionProfile? _selectedConnectionProfile;
    private bool _secureConnectionRequested;
    private static readonly string[] ConnectionFields = ["mode", "gateway", "port", "device", "own_address", "interface",
        "keyring_path", "keyring_password_env", "keyring_project", "host_address", "user_id", "expected_keyring_sha256", "group_security_profile"];

    private static bool IsConnectionTool(string name) => name is "bus_discover" or "bus_connect" or "bus_disconnect" or "bus_read" or "bus_scan" or "bus_device_info" or "bus_tool_identity_read" or "download_plan";
    private static string SafeConnectionFailure(Exception error) => "KNX connection operation failed: " + (error switch
    {
        KnxConnectionException connection => connection.Failure.ToString(),
        ToolConsoleException tool => tool.Failure.ToString(),
        OperationCanceledException => "Cancelled",
        _ => "ConnectionFailed",
    });
    private static void RefuseSecretArguments(JsonElement args, bool allowToolReferences = false)
    {
        if (args.ValueKind != JsonValueKind.Object) return;
        foreach (var property in args.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (!allowToolReferences && (name.StartsWith("tool_", StringComparison.Ordinal) || name.StartsWith("counter_", StringComparison.Ordinal)
                || name is "confirm_current_state" or "state_trust" or "wire_tpdu_capacity" or "ordinary_p2p_key_sha256"))
                throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            if (name is "key" or "tool_key" or "user_key" or "device_key" or "authentication" or "authentication_code" or "device_authentication_code"
                or "group_key" or "group_keys" or "integrity_key" or "counter_state" or "counter_path" or "counter_floor" or "counter_next"
                || name.Contains("integrity", StringComparison.Ordinal) || name.StartsWith("counter_", StringComparison.Ordinal)
                || name.Contains("secret", StringComparison.Ordinal) || name.EndsWith("_key", StringComparison.Ordinal) || name.EndsWith("_keys", StringComparison.Ordinal)
                || name.Contains("password", StringComparison.Ordinal) && name != "keyring_password_env")
                throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        }
    }
    private async Task<ConnectionJob> EnterConnectionJobAsync()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_requestCancellation.Value, _connectionLifetime.Token);
        try { await _connectionJobs.WaitAsync(cancellation.Token).ConfigureAwait(false); return new(_connectionJobs, cancellation); }
        catch { cancellation.Dispose(); throw; }
    }
    private sealed class ConnectionJob(SemaphoreSlim gate, CancellationTokenSource cancellation) : IDisposable
    { public CancellationToken Token => cancellation.Token; public void Dispose() { gate.Release(); cancellation.Dispose(); } }

    private async Task<object> ReplaceProjectAsync(JsonElement args, Func<JsonElement, object> action)
    {
        using var job = await EnterConnectionJobAsync();
        await _bus.DisposeAsync().ConfigureAwait(false);
        _selectedConnectionProfile = null; _secureConnectionRequested = false;
        return action(args);
    }

    private async Task<KnxConnectionProfile> ResolveConnectionProfileAsync(JsonElement args, IndividualAddress defaultOwn, CancellationToken ct)
    {
        RefuseSecretArguments(args);
        var mode = Str(args, "mode")?.ToLowerInvariant();
        if (mode == "ip_secure_udp" && _bus.State != BusConnectionState.Connected) _secureConnectionRequested = true;
        var active = _bus.Profile;
        if (_bus.State == BusConnectionState.Connected && active is null) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var selection = args.ValueKind == JsonValueKind.Object ? args.EnumerateObject().Where(property => ConnectionFields.Contains(property.Name)).ToArray() : [];
        if (_bus.State == BusConnectionState.Connected && active is not null && selection.Length == 0) return active;
        // A gateway-only request may explicitly name the already-selected endpoint without changing its security policy.
        if (_bus.State == BusConnectionState.Connected && active is not null && selection.All(property => property.Name is "gateway" or "port"))
        {
            if (!IPAddress.TryParse(Str(args, "gateway"), out var gateway) || !gateway.Equals(active.Gateway.Address)
                || (Int(args, "port") ?? active.Port) != active.Port) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            return active;
        }
        if (_bus.State == BusConnectionState.Connected && active?.RequiresAuthentication == true && mode != "ip_secure_udp")
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        if (_bus.State != BusConnectionState.Connected && mode is null && _secureConnectionRequested)
        {
            var selectedProfile = _selectedConnectionProfile;
            if (selectedProfile?.RequiresAuthentication != true || selection.Any(property => property.Name is not ("gateway" or "port")))
                throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            if (Str(args, "gateway") is { } selectedGateway && (!IPAddress.TryParse(selectedGateway, out var requested) || !requested.Equals(selectedProfile.Gateway.Address))
                || (Int(args, "port") ?? selectedProfile.Port) != selectedProfile.Port) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            return selectedProfile;
        }
        if (mode == "ip_secure_udp")
        {
            var secure = ConnectionInput.Explicit(args, defaultOwn);
            if (args.TryGetProperty("group_security_profile", out var selectedGroup))
            {
                if (selectedGroup.ValueKind != JsonValueKind.String || selectedGroup.GetString() is not { Length: > 0 and <= 128 } id
                    || id.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_'))
                    || _groupProfiles is null || secure.Keyring?.ExpectedSourceFingerprint is null)
                    throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
                try
                {
                    var groups = await _groupProfiles.ResolveAsync(id, secure, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested(); secure = secure.WithGroupSecurity(groups);
                }
                catch (OperationCanceledException) { throw; }
                catch (KnxConnectionException) { throw; }
                catch { throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityStateUnavailable); }
            }
            if (_bus.State != BusConnectionState.Connected) _selectedConnectionProfile = secure;
            return secure;
        }
        if (ConnectionInput.HasSecureFields(args) || mode is not (null or "tunnel" or "plain_udp"))
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var source = ConnectionInput.Address(Str(args, "own_address") ?? (active?.RequestedAddress ?? defaultOwn).ToString());
        if (Str(args, "gateway") is { } explicitGateway && IPAddress.TryParse(explicitGateway, out _))
            return ConnectionInput.Explicit(args, active?.RequestedAddress ?? defaultOwn);
        var timeout = Math.Clamp(Int(args, "timeout_ms") ?? 1500, 100, 10000);
        var selector = Str(args, "gateway") ?? Str(args, "device");
        var devices = await _bus.DiscoverAsync(TimeSpan.FromMilliseconds(timeout), ct: ct).ConfigureAwait(false);
        var selected = selector is null ? devices.FirstOrDefault() : devices.FirstOrDefault(device =>
            device.Name.Contains(selector, StringComparison.OrdinalIgnoreCase) || device.KnxAddress.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || device.ControlEndpoint.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase));
        if (selected is null) throw new KnxConnectionException(KnxConnectionFailure.ConnectionFailed);
        return KnxConnectionProfile.Plain(selected.ControlEndpoint, source, ConnectionInput.Local(args));
    }

    private async Task<T> WithDiagnosticsAsync<T>(JsonElement args, int timeoutMs, Func<IBusDiagnosticSession, CancellationToken, Task<T>> action)
    {
        using var job = await EnterConnectionJobAsync();
        if (_bus.State != BusConnectionState.Connected && _bus.Profile is not null)
            await _bus.DisposeAsync().ConfigureAwait(false);
        var profile = await ResolveConnectionProfileAsync(args, new(1, 1, 249), job.Token).ConfigureAwait(false);
        if (_bus.State == BusConnectionState.Connected)
        {
            if (_bus.Profile is not { } active || !active.EquivalentTo(profile)) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            return await _bus.WithDiagnosticsAsync(action, job.Token).ConfigureAwait(false);
        }
        return await BusDiagnostics.WithSessionAsync(_connections, profile, action, job.Token,
            security => RememberConnection(profile, security)).ConfigureAwait(false);
    }

    private void RememberConnection(KnxConnectionProfile profile, KnxConnectionSecurity? security)
    {
        _secureConnectionRequested = profile.RequiresAuthentication;
        if (profile.RequiresAuthentication && profile.Keyring is { } reference && security?.KeyringFingerprint is { } fingerprint)
        {
            var selected = KnxConnectionProfile.IpSecure(profile.Gateway,
                new(reference.Path, reference.ExpectedProjectLabel, reference.HostAddress, reference.UserId, reference.TunnelAddress,
                    reference.PasswordEnvironmentName, fingerprint), profile.PasswordSource, profile.LocalAddress);
            _selectedConnectionProfile = profile.GroupSecurity is { } groups ? selected.WithGroupSecurity(groups) : selected;
        }
        else _selectedConnectionProfile = profile;
    }

    private static JsonObject ConnectionSchema(JsonObject schema)
    {
        var properties = schema["properties"]!.AsObject();
        foreach (var (name, type, description) in new[]
        {
            ("mode", "string", "Explicit tunnel/plain_udp or ip_secure_udp. Secure mode never discovers a gateway or falls back to plain."),
            ("gateway", "string", "Explicit gateway IPv4 for ip_secure_udp; plain diagnostics retain legacy gateway selection."),
            ("port", "integer", "Explicit gateway port, default 3671."),
            ("own_address", "string", "Requested tunnel KNX individual address; required in Secure mode and matched after CONNECT."),
            ("keyring_path", "string", "Absolute private protected .knxkeys file; never returned in results."),
            ("keyring_password_env", "string", "Environment VARIABLE NAME containing the keyring password; never the password value."),
            ("keyring_project", "string", "Exact expected signed keyring Project label; not a universal project ID."),
            ("host_address", "string", "Exact gateway KNX individual address from the selected keyring interface, not its IPv4."),
            ("user_id", "integer", "Exact selected Secure tunnel user, 1..127."),
            ("expected_keyring_sha256", "string", "Optional SHA256 pin of the exact protected export bytes."),
            ("group_security_profile", "string", "Optional explicit host-provisioned group policy reference. Requires exact source pin and existing trusted ordinary counters; no default registry, bootstrap or secret tool arguments."),
        })
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
        return schema;
    }

    private static List<McpTool> AddConnectionSchemas(List<McpTool> tools)
    {
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            if (tool.Name is not ("bus_connect" or "bus_scan" or "bus_device_info" or "download_plan")) continue;
            var schema = ConnectionSchema(tool.InputSchema);
            if (tool.Name == "bus_connect") schema["properties"]!["mode"]!["enum"] = new JsonArray("tunnel", "plain_udp", "ip_secure_udp", "routing");
            tools[index] = tool with { InputSchema = schema };
        }
        return tools;
    }
}

internal static class ConnectionInput
{
    private static readonly string[] SecureFields = ["keyring_path", "keyring_password_env", "keyring_project", "host_address", "user_id", "expected_keyring_sha256", "group_security_profile"];
    internal static bool HasSecureFields(JsonElement args) => args.ValueKind == JsonValueKind.Object && SecureFields.Any(name => args.TryGetProperty(name, out _));
    private static string? Text(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        return value.GetString();
    }
    private static string Required(JsonElement args, string name) => Text(args, name) ?? throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
    private static int Number(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        return number;
    }
    internal static IndividualAddress Address(string text)
    { if (!IndividualAddress.TryParse(text, out var address) || address.ToString() != text || address.Device is 0 or 255) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); return address; }
    internal static IPAddress? Local(JsonElement args)
    { if (Text(args, "interface") is not { } text) return null; return IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? address : throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile); }
    internal static KnxConnectionProfile Explicit(JsonElement args, IndividualAddress defaultOwn)
    {
        var mode = Text(args, "mode")?.ToLowerInvariant() ?? "plain_udp";
        if (!IPAddress.TryParse(Required(args, "gateway"), out var gateway)) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var port = Number(args, "port", 3671);
        if (port is < 1 or > 65535) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        var endpoint = new IPEndPoint(gateway, port);
        if (mode == "ip_secure_udp")
        {
            if (Text(args, "device") is not null) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            var own = Address(Required(args, "own_address"));
            var hostText = Required(args, "host_address");
            if (!IndividualAddress.TryParse(hostText, out var host) || host.ToString() != hostText || host.Device == 255 || host.Raw == 0)
                throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            var user = Number(args, "user_id", 0);
            if (user is < 1 or > 127) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            return KnxConnectionProfile.IpSecure(endpoint, new(Required(args, "keyring_path"), Required(args, "keyring_project"), host, (byte)user, own,
                Required(args, "keyring_password_env"), Text(args, "expected_keyring_sha256")), local: Local(args));
        }
        if (mode is not ("plain_udp" or "tunnel") || HasSecureFields(args)) throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        return KnxConnectionProfile.Plain(endpoint, Text(args, "own_address") is { } source ? Address(source) : defaultOwn, Local(args));
    }
}
