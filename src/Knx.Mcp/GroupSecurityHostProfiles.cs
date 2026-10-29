using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Knx;
using Knx.Bus.Connections;
using Knx.Bus.Security;

namespace Knx.Mcp;

/// <summary>
/// Explicit bounded, outside-Git host configuration. Owns copied integrity capabilities and joins resolutions on close.
/// The caller must close/drain its MCP server before disposing this owner. No configuration is discovered or migrated.
/// </summary>
public sealed class GroupSecurityHostProfiles : IKnxGroupSecurityProfileResolver, IAsyncDisposable
{
    private sealed record Entry(KnxConnectionProfile Connection, string State, string IntegrityEnvironment, string Installation, string Epoch,
        KnxSecureGroupSelection[] Groups, GroupAddress[] PlainGroups, IndividualAddress[] PlainPeers, string[] ExternalKeys, IDataSecureMonotonicAnchor? Anchor);
    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, (KnxGroupSecurityProfile Profile, ExistingGroupCounterStateSource Source)> _cache = [];
    private readonly IHostIntegrityCapabilitySource _integrity;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _operations;
    private TaskCompletionSource _drained = Completed();
    private Task? _disposal;
    private GroupSecurityHostProfiles(Dictionary<string, Entry> entries, IHostIntegrityCapabilitySource integrity) { _entries = entries; _integrity = integrity; }
    private static TaskCompletionSource Completed() { var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); tcs.SetResult(); return tcs; }
    public static GroupSecurityHostProfiles Load(string path, IReadOnlyCollection<string> confirmedCurrentUnanchoredIds,
        IHostIntegrityCapabilitySource? integrity = null, IReadOnlyDictionary<string, IDataSecureMonotonicAnchor>? independentAnchors = null)
    {
        byte[] json = [];
        try
        {
            ArgumentNullException.ThrowIfNull(confirmedCurrentUnanchoredIds);
            json = HostSecurityReferences.ReadBounded(path, 65536);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement; Shape(root, ["format", "profiles"]);
            if (Text(root, "format", 64) != "BusChord.GroupSecurityHost.v1") throw Invalid();
            var array = root.GetProperty("profiles"); if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) throw Invalid();
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                Shape(item, ["id", "gateway", "port", "own_address", "host_address", "user_id", "keyring_path", "keyring_project", "keyring_password_env", "expected_keyring_sha256",
                    "installation_scope", "ordinary_key_epoch", "counter_state_path", "counter_integrity_env", "state_trust", "individual_key_sha256", "groups", "plain_groups", "plain_peers"]);
                var id = Text(item, "id", 128); if (!ValidId(id)) throw Invalid();
                var trust = Text(item, "state_trust", 64); IDataSecureMonotonicAnchor? anchor = null;
                if (trust == "independent_anchor")
                { if (independentAnchors is null || !independentAnchors.TryGetValue(id, out anchor) || anchor is null) throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityStateNotReady); }
                else if (trust != "provisioner_confirmed_current_unanchored" || !confirmedCurrentUnanchoredIds.Contains(id, StringComparer.Ordinal))
                    throw new KnxConnectionException(KnxConnectionFailure.GroupSecurityStateNotReady);
                var gateway = Text(item, "gateway", 64);
                if (!IPAddress.TryParse(gateway, out var address) || address.ToString() != gateway) throw Invalid();
                var hostText = Text(item, "host_address", 16);
                if (!IndividualAddress.TryParse(hostText, out var host) || host.ToString() != hostText || host.Raw == 0 || host.Device == 255) throw Invalid();
                var user = Number(item, "user_id"); if (user is < 1 or > 127) throw Invalid();
                var passwordEnv = Text(item, "keyring_password_env", 128); var integrityEnv = Text(item, "counter_integrity_env", 128);
                EnvironmentSecurePasswordSource.ValidateName(passwordEnv); EnvironmentSecurePasswordSource.ValidateName(integrityEnv);
                var pin = Text(item, "expected_keyring_sha256", 64); if (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit)) throw Invalid();
                var connection = KnxConnectionProfile.IpSecure(new(address, Number(item, "port")), new(HostSecurityReferences.PrivateFile(Text(item, "keyring_path"), false),
                    Text(item, "keyring_project", 255), host, (byte)user, ConnectionInput.Address(Text(item, "own_address", 16)), passwordEnv, pin));
                var groups = item.GetProperty("groups"); if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() is < 1 or > 128) throw Invalid();
                var selected = new List<KnxSecureGroupSelection>();
                foreach (var group in groups.EnumerateArray())
                {
                    Shape(group, ["address", "protection", "senders"]);
                    var protection = Text(group, "protection", 32) switch
                    { "authentication" => KnxDataProtection.Authentication, "auth_conf" => KnxDataProtection.AuthenticationAndConfidentiality, _ => throw Invalid() };
                    selected.Add(new(Group(Text(group, "address", 16)), protection, Array.AsReadOnly(StringArray(group, "senders", 128).Select(PeerAddress).ToArray())));
                }
                var plainGroups = StringArray(item, "plain_groups", 128).Select(Group).ToArray(); var peers = StringArray(item, "plain_peers", 128).Select(PeerAddress).ToArray();
                var keys = StringArray(item, "individual_key_sha256", 128);
                if (keys.Any(key => key.Length != 64 || !key.All(char.IsAsciiHexDigit)) || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Length) throw Invalid();
                var state = HostSecurityReferences.PrivateFile(Text(item, "counter_state_path"), false);
                var installation = Text(item, "installation_scope", 128); var epoch = Text(item, "ordinary_key_epoch", 128);
                // Reuse the complete immutable profile validation now without opening state or reading ENV capabilities.
                _ = new KnxGroupSecurityProfile(installation, epoch, selected, ValidationStateSource.Instance, keys, plainGroups, peers);
                if (!entries.TryAdd(id, new(connection, state, integrityEnv, installation, epoch, selected.ToArray(), plainGroups, peers, keys, anchor))) throw Invalid();
            }
            if (confirmedCurrentUnanchoredIds.Distinct(StringComparer.Ordinal).Count() != confirmedCurrentUnanchoredIds.Count
                || confirmedCurrentUnanchoredIds.Any(id => !ValidId(id) || !entries.ContainsKey(id))) throw Invalid();
            return new(entries, integrity ?? new EnvironmentHostIntegrityCapabilitySource());
        }
        catch (KnxConnectionException) { throw; }
        catch { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }
    public async ValueTask<KnxGroupSecurityProfile> ResolveAsync(string referenceId, KnxConnectionProfile connection, CancellationToken ct)
    {
        lock (_gate) { if (_disposal is not null) throw new KnxConnectionException(KnxConnectionFailure.Disposed); if (_operations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            await _serial.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!_entries.TryGetValue(referenceId, out var entry) || !entry.Connection.EquivalentTo(connection))
                    throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_cache.TryGetValue(referenceId, out var cached)) return cached.Profile;
                HostSecurityReferences.PrivateFile(entry.State, true); byte[] owned = [];
                try
                {
                    owned = await _integrity.ReadOwnedAsync(entry.IntegrityEnvironment, cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested(); if (owned.Length != 32) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
                    var source = entry.Anchor is { } anchor ? new ExistingGroupCounterStateSource(entry.State, owned, anchor)
                        : ExistingGroupCounterStateSource.FromTrustedUnanchoredCurrentState(entry.State, owned);
                    try
                    {
                        var profile = new KnxGroupSecurityProfile(entry.Installation, entry.Epoch, entry.Groups, source, entry.ExternalKeys, entry.PlainGroups, entry.PlainPeers);
                        cancellation.Token.ThrowIfCancellationRequested(); _cache.Add(referenceId, (profile, source)); return profile;
                    }
                    catch { source.Dispose(); throw; }
                }
                finally { CryptographicOperations.ZeroMemory(owned); }
            }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (KnxConnectionException) { throw; }
        catch { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
        finally { lock (_gate) if (--_operations == 0) _drained.TrySetResult(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task pending;
        lock (_gate) { if (_disposal is null) { owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; } pending = _disposal; }
        if (owner is not null) { try { _lifetime.Cancel(); } catch { } finally { _ = FinishDisposeAsync(owner); } }
        return new(pending);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource owner)
    {
        try
        {
            Task drained; lock (_gate) drained = _drained.Task; await drained.ConfigureAwait(false);
            foreach (var cached in _cache.Values) cached.Source.Dispose(); _cache.Clear(); _serial.Dispose(); _lifetime.Dispose(); owner.TrySetResult();
        }
        catch { owner.TrySetException(new KnxConnectionException(KnxConnectionFailure.ConnectionFailed)); }
    }
    private static KnxConnectionException Invalid() => new(KnxConnectionFailure.InvalidProfile);
    private static bool ValidId(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static void Shape(JsonElement element, string[] fields)
    { if (element.ValueKind != JsonValueKind.Object) throw Invalid(); var seen = new HashSet<string>(); foreach (var field in element.EnumerateObject()) if (!fields.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw Invalid(); if (seen.Count != fields.Length) throw Invalid(); }
    private static string Text(JsonElement element, string field, int maximum = 4096)
    { var value = element.GetProperty(field); return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Contains('\0') ? text : throw Invalid(); }
    private static int Number(JsonElement element, string field)
    { var value = element.GetProperty(field); return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Invalid(); }
    private static string[] StringArray(JsonElement element, string field, int maximum)
    { var value = element.GetProperty(field); if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Invalid(); return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 128 } text && !text.Contains('\0') ? text : throw Invalid()).ToArray(); }
    private static GroupAddress Group(string value)
    { var group = GroupAddress.Parse(value); if (group.ToString() != value || group.Raw == 0) throw Invalid(); return group; }
    private static IndividualAddress PeerAddress(string value)
    { if (!IndividualAddress.TryParse(value, out var peer) || peer.Raw == 0 || peer.Device == 255 || peer.ToString() != value) throw Invalid(); return peer; }
    private sealed class ValidationStateSource : IKnxGroupCounterStateSource
    { internal static ValidationStateSource Instance { get; } = new(); public Task<DataSecureCounterStore> OpenExistingAsync(DataSecureStateIdentity identity, CancellationToken ct) => throw Invalid(); }
    public override string ToString() => "[explicit group security host profiles]";
}
