using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Knx.Bus;
using Knx.Bus.Connections;

namespace Knx.Mcp;

/// <summary>One MCP tool: name, agent-facing description, JSON input schema and handler.</summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonElement, Task<object>> Handler);

/// <summary>
/// Newline-delimited JSON-RPC 2.0 dispatcher for the MCP stdio transport.
/// stdout carries protocol messages only; all diagnostics go to stderr.
/// Public so the test project can drive the dispatcher in-process.
/// </summary>
public sealed partial class McpServer : IAsyncDisposable
{
    public const string ServerName = "buschord";
    public const string ServerVersion = "1.0.0";
    public const string ProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26"];

    private const string InstructionsText =
        "BusChord tooling for building automation projects. Open a project with project_open (existing " +
        ".knxproj) or create one with project_new, then inspect it with project_info and project_dashboard. " +
        "Passwords for protected project_open/project_save calls are never retained; execute_scripts is an explicit per-call capability. " +
        "Mutations (group_address_*, group_link_*, device_*, smart_link_apply, mass_link_apply, product_replace, jung_ksp_import_apply) only change the in-memory " +
        "project and can be reverted with undo/redo; call project_save to write the .knxproj. Use " +
        "mass_link_review then mass_link_apply for strictly validated batch links; manual legacy link calls return unknown compatibility explicitly unverified. Use " +
        "project_check and smart_link_suggest for diagnostics, export_csv/export_semantic/export_labels/report_export/" +
        "acceptance_protocol_export/handover_package_export for " +
        "exports, project_compare for diffs and catalog_* for manufacturer product databases. For live bus " +
        "access call bus_connect first (mode tunnel searches for a gateway, mode routing joins multicast, " +
        "ip_secure_udp requires an explicit gateway and exact protected-keyring selectors), then bus_read/recorder_*; " +
        "bus_disconnect drains the interface. Standalone bus_tool_identity_read requires an explicit pinned Tool configuration, " +
        "trusted existing counters and a free gateway reservation. Secure connections never fall back to plain. download_plan reports the " +
        "read-only preflight and dry run for one project device. Group writes and application downloads remain disabled. " +
        "The separate address_assignment_review/apply workflow alone permits one explicitly confirmed plain-device address write after a durable intent/claim; " +
        "poll address_assignment_status or cancel/join with address_assignment_cancel. Cold inspect/reconcile is read-only and never resends an uncertain write.";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // Protocol strings are consumed by agents, not embedded in HTML: keep quotes readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<McpTool> _tools;
    private readonly UndoStack _undo = new(100);
    private readonly KnxBus _bus;
    private readonly TelegramRecorder _recorder = new();
    private readonly BusStatistics _statistics = new();

    private Project? _project;
    private string? _projectPath;

    public McpServer(KnxConnectionFactory? connections = null)
        : this(connections, null) { }
    public McpServer(KnxConnectionFactory? connections, IKnxGroupSecurityProfileResolver? groupProfiles)
    {
        _groupProfiles = groupProfiles;
        _connections = connections ?? new(); _bus = new(_connections);
        // The recorder ignores telegrams while stopped, so subscribing once is enough.
        _bus.TelegramReceived += _recorder.Add;
        _bus.TelegramReceived += _statistics.Add;
        _tools = BuildTools();
        _tools.AddRange(BuildAddressAssignmentTools());
    }

    public IReadOnlyList<McpTool> Tools => _tools;
    public Project? Project => _project;

    /// <summary>Handles one JSON-RPC line; returns the response line or null for notifications.</summary>
    public async Task<string?> HandleAsync(string line, CancellationToken ct = default)
    {
        var previous = _requestCancellation.Value; _requestCancellation.Value = ct;
        try
        {
            return await DispatchAsync(line);
        }
        catch (Exception ex)
        {
            Log("internal error: " + ex);
            return Error(null, -32603, "Internal error: " + ex.Message);
        }
        finally { _requestCancellation.Value = previous; }
    }

    private async Task<string?> DispatchAsync(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Log("parse error");
            return Error(null, -32700, "Parse error: invalid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Invalid Request");

            // A present "id" member makes it a request, even when the value is null.
            var hasId = root.TryGetProperty("id", out var idElement);
            var id = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;

            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                return hasId ? Error(id, -32600, "Invalid Request: missing method") : null;

            var method = methodElement.GetString()!;
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;

            if (method.StartsWith("notifications/", StringComparison.Ordinal)) return null;
            if (!hasId) return null;

            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize(parameters));
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = ToolListJson() });
                case "tools/call":
                    return await CallToolAsync(id, parameters);
                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }
    }

    private static JsonObject Initialize(JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("protocolVersion", out var versionElement)
                        && versionElement.ValueKind == JsonValueKind.String
            ? versionElement.GetString()
            : null;

        var version = requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : ProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["instructions"] = InstructionsText,
        };
    }

    private JsonArray ToolListJson()
    {
        var array = new JsonArray();
        foreach (var tool in _tools)
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        return array;
    }

    private async Task<string> CallToolAsync(JsonNode? id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
            return Error(id, -32602, "Invalid params: tools/call requires a 'name' string");

        var name = nameElement.GetString()!;
        var arguments = parameters.TryGetProperty("arguments", out var argsElement)
                        && argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : default;

        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return Result(id, ToolResult($"Unknown tool '{name}'. Call tools/list for the available tools.", null, true));

        try
        {
            if (name.StartsWith("bus_", StringComparison.Ordinal) || name == "download_plan") RefuseSecretArguments(arguments, allowToolReferences: name == "bus_tool_identity_read");
            if (name == "bus_discover" && (ConnectionInput.HasSecureFields(arguments)
                || string.Equals(Str(arguments, "mode"), "ip_secure_udp", StringComparison.OrdinalIgnoreCase)))
                throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
            var payload = await tool.Handler(arguments);
            return Result(id, ToolResult(JsonSerializer.Serialize(payload, Json), payload, false));
        }
        catch (ToolException ex)
        {
            if (IsConnectionTool(name) && (name != "download_plan" || ConnectionInput.HasSecureFields(arguments)))
            {
                var safe = SafeConnectionFailure(ex); Log(name + ": " + safe);
                return Result(id, ToolResult(safe, null, true));
            }
            Log($"{name}: {ex.Message}");
            return Result(id, ToolResult(ex.Message, null, true));
        }
        catch (Exception ex)
        {
            if (IsConnectionTool(name) || ex is KnxConnectionException)
            {
                var safe = SafeConnectionFailure(ex); Log(name + ": " + safe);
                return Result(id, ToolResult(safe, null, true));
            }
            Log($"{name} failed: {ex}");
            return Result(id, ToolResult(ex.Message, null, true));
        }
    }

    private static JsonObject ToolResult(string text, object? payload, bool isError)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError)
            result["isError"] = true;
        else if (payload is not null)
            result["structuredContent"] = JsonSerializer.SerializeToNode(payload, Json);
        return result;
    }

    private static string Result(JsonNode? id, JsonNode result)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        }.ToJsonString(Json);

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString(Json);

    private static void Log(string message) => Console.Error.WriteLine("knx-mcp: " + message);

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task drain;
        lock (_connectionDisposeGate)
        {
            if (_connectionDisposeTask is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _connectionDisposeTask = completion.Task;
            }
            drain = _connectionDisposeTask;
        }
        if (completion is not null) _ = DisposeConnectionsAsync(completion);
        return new(drain);
    }

    private async Task DisposeConnectionsAsync(TaskCompletionSource completion)
    {
        try
        {
            // Foreign token callbacks cannot prevent owner cleanup or leave a half-drained server.
            try { _connectionLifetime.Cancel(); } catch (AggregateException) { }
            await DrainAddressCommandAsync().ConfigureAwait(false);
            await _connectionJobs.WaitAsync().ConfigureAwait(false);
            try
            {
                _migrationReviews.Clear();
                _jungKspReview = null;
                await CloseAddressAssignmentAsync().ConfigureAwait(false);
                await _bus.DisposeAsync().ConfigureAwait(false);
            }
            finally { _connectionJobs.Release(); }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    /// <summary>Expected tool failure; reported to the client as isError, never as a protocol error.</summary>
    private sealed class ToolException(string message) : Exception(message);
}
