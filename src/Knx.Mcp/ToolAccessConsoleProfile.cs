using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.Mcp;

internal enum ToolConsoleFailure { InvalidToolConfiguration, CurrentStateConfirmationRequired, IntegrityCapabilityUnavailable, ToolWireCapacityRejected }
internal sealed class ToolConsoleException(ToolConsoleFailure failure) : IOException("Tool-Access console refused: " + failure)
{ internal ToolConsoleFailure Failure { get; } = failure; }

/// <summary>Owned bytes from a validated reference, never a command-line/JSON key value.</summary>
internal interface IToolIntegrityCapabilitySource
{ ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct); }

internal sealed class EnvironmentToolIntegrityCapabilitySource : IToolIntegrityCapabilitySource
{
    public ValueTask<byte[]> ReadOwnedAsync(string environmentName, CancellationToken ct)
    {
        byte[] bytes = new byte[32];
        try
        {
            EnvironmentSecurePasswordSource.ValidateName(environmentName); ct.ThrowIfCancellationRequested();
            var value = Environment.GetEnvironmentVariable(environmentName);
            if (value is not { Length: 44 } || !Convert.TryFromBase64String(value, bytes, out var count) || count != 32
                || Convert.ToBase64String(bytes) != value) throw new ToolConsoleException(ToolConsoleFailure.IntegrityCapabilityUnavailable);
            return ValueTask.FromResult(bytes);
        }
        catch (OperationCanceledException) { CryptographicOperations.ZeroMemory(bytes); throw; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw new ToolConsoleException(ToolConsoleFailure.IntegrityCapabilityUnavailable); }
    }
}

/// <summary>Test/provider seam only; production uses named environment sources and the verified keyring provider.</summary>
internal sealed record ToolAccessConsoleServices(IToolIntegrityCapabilitySource Integrity, ISecurePasswordSource? Password = null,
    IKnxToolAccessCredentialProvider? Credentials = null)
{
    internal static ToolAccessConsoleServices FromProviders(Func<string, CancellationToken, Task<byte[]>> integrity,
        ISecurePasswordSource password, IKnxToolAccessCredentialProvider credentials) => new(new DelegateSource(integrity), password, credentials);
    private sealed class DelegateSource(Func<string, CancellationToken, Task<byte[]>> source) : IToolIntegrityCapabilitySource
    { public async ValueTask<byte[]> ReadOwnedAsync(string name, CancellationToken ct) => await source(name, ct).ConfigureAwait(false); }
}

internal sealed class ToolAccessConsoleProfile
{
    private const string Trust = "provisioner_confirmed_current_unanchored";
    private static readonly string[] Fields = ["format", "gateway", "port", "host_address", "user_id", "own_address", "target", "keyring_path",
        "keyring_project", "keyring_password_env", "expected_keyring_sha256", "installation_scope", "tool_key_epoch", "wire_tpdu_capacity",
        "ordinary_p2p_key_sha256", "counter_state_path", "counter_integrity_env", "state_trust"];
    internal KnxConnectionProfile Connection { get; }
    internal IndividualAddress Target { get; }
    internal int Capacity { get; }
    private readonly string _state, _integrityEnvironment, _installation, _epoch;
    private readonly IReadOnlyList<string> _ordinaryKeys;
    private ToolAccessConsoleProfile(KnxConnectionProfile connection, IndividualAddress target, int capacity, string state,
        string integrityEnvironment, string installation, string epoch, IReadOnlyList<string> ordinaryKeys)
    { Connection = connection; Target = target; Capacity = capacity; _state = state; _integrityEnvironment = integrityEnvironment; _installation = installation; _epoch = epoch; _ordinaryKeys = ordinaryKeys; }

    internal static ToolAccessConsoleProfile Read(string path, string gateway, IndividualAddress target, string? confirmation, ISecurePasswordSource? passwordSource = null)
    {
        byte[] json = [];
        try
        {
            path = PrivateReference(path, existing: true);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > 32768) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            json = new byte[checked((int)file.Length)]; file.ReadExactly(json);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            if (seen.Count != Fields.Length) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            string Text(string name, int maximum = 4096)
            {
                var value = root.GetProperty(name);
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > maximum || text.Contains('\0'))
                    throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
                return text;
            }
            int Number(string name) => root.GetProperty(name).TryGetInt32(out var value) ? value : throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            if (Text("format") != "BusChord.ToolAccessManagement.v1" || Text("gateway") != gateway || Text("target") != target.ToString()
                || !IPAddress.TryParse(gateway, out var gatewayAddress) || gatewayAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || gatewayAddress.ToString() != gateway)
                throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            if (Text("state_trust") != Trust || confirmation != target.ToString())
                throw new ToolConsoleException(ToolConsoleFailure.CurrentStateConfirmationRequired);
            var port = Number("port"); var user = Number("user_id"); var capacity = Number("wire_tpdu_capacity");
            if (port is < 1 or > 65535 || user is < 1 or > 127 || capacity is < 25 or > 255) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var own = ConnectionInput.Address(Text("own_address"));
            if (!IndividualAddress.TryParse(Text("host_address"), out var host) || host.Raw == 0 || host.Device == 255 || host.ToString() != Text("host_address") || own == target)
                throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var passwordEnvironment = Text("keyring_password_env", 128); var integrityEnvironment = Text("counter_integrity_env", 128);
            EnvironmentSecurePasswordSource.ValidateName(passwordEnvironment); EnvironmentSecurePasswordSource.ValidateName(integrityEnvironment);
            var fingerprint = Text("expected_keyring_sha256", 64);
            if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit)) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var inventory = root.GetProperty("ordinary_p2p_key_sha256");
            if (inventory.ValueKind != JsonValueKind.Array || inventory.GetArrayLength() > 128) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var keys = new List<string>();
            foreach (var key in inventory.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
                    throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
                keys.Add(hash.ToUpperInvariant());
            }
            if (keys.Distinct().Count() != keys.Count) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var installation = Text("installation_scope", 128); var epoch = Text("tool_key_epoch", 117);
            if (string.IsNullOrWhiteSpace(installation) || string.IsNullOrWhiteSpace(epoch)) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
            var connection = KnxConnectionProfile.IpSecure(new(IPAddress.Parse(gateway), port), new(PrivateReference(Text("keyring_path"), false),
                Text("keyring_project", 255), host, (byte)user, own, passwordEnvironment, fingerprint), passwordSource);
            return new(connection, target, capacity, PrivateReference(Text("counter_state_path"), false), integrityEnvironment,
                installation, epoch, Array.AsReadOnly(keys.ToArray()));
        }
        catch (ToolConsoleException) { throw; }
        catch { throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    internal async Task<KnxGatewayOperation> AcquireAsync(KnxConnectionFactory factory, ToolAccessConsoleServices? services, CancellationToken ct)
    {
        byte[] integrity = [];
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(_state)) throw new KnxConnectionException(KnxConnectionFailure.ToolAccessStateUnavailable);
            try { integrity = await (services?.Integrity ?? new EnvironmentToolIntegrityCapabilitySource()).ReadOwnedAsync(_integrityEnvironment, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw new ToolConsoleException(ToolConsoleFailure.IntegrityCapabilityUnavailable); }
            if (integrity is not { Length: 32 }) throw new ToolConsoleException(ToolConsoleFailure.IntegrityCapabilityUnavailable);
            using var source = ExistingToolCounterStateSource.FromTrustedUnanchoredState(_state, integrity);
            var profile = new KnxToolAccessProfile(Target, _installation, _epoch, Capacity, source, _ordinaryKeys);
            return services?.Credentials is { } provider
                ? await factory.AcquireToolAccessOperationAsync(Connection, profile, provider, ct).ConfigureAwait(false)
                : await factory.AcquireToolAccessOperationAsync(Connection, profile, ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(integrity); }
    }
    internal void RequireWireBudget(M112ApplicationWireBudget budget)
    { if (budget.RequiredToolWireTpduLength > Capacity) throw new ToolConsoleException(ToolConsoleFailure.ToolWireCapacityRejected); }
    internal static string PrivateReference(string path, bool existing)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
        path = Path.GetFullPath(path);
        if (Directory.Exists(path) || existing && !File.Exists(path)) throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0
                || File.Exists(Path.Combine(item, ".git")) || Directory.Exists(Path.Combine(item, ".git")))
                throw new ToolConsoleException(ToolConsoleFailure.InvalidToolConfiguration);
        }
        return path;
    }
}
