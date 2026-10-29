using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Knx.Bus.Security;

namespace Knx.Bus.Connections;

public enum KnxTransportKind { PlainUdp, IpSecureUdp }
public enum KnxConnectionFailure { InvalidProfile, Occupied, CredentialSourceUnavailable, KeyringRejected, CredentialBindingMismatch, UnsupportedCredentials, AuthenticationFailed, AssignedAddressMismatch, ConnectionFailed, Cancelled, Disposed,
    ToolAccessKeyUnavailable, ToolAccessBindingMismatch, ToolAccessStateUnavailable, ToolAccessStateNotReady, ToolAccessCounterExhausted, ToolAccessSynchronizationFailed,
    GroupSecurityBindingMismatch, GroupSecurityStateUnavailable, GroupSecurityStateNotReady, GroupSecurityCounterExhausted, GroupSecurityPolicyDenied }
public sealed class KnxConnectionException(KnxConnectionFailure failure) : IOException("KNX connection failed: " + failure)
{ public KnxConnectionFailure Failure { get; } = failure; }

/// <summary>References only. Password values and keyring bytes are never part of a connection profile.</summary>
public sealed class SecureKeyringReference
{
    [JsonIgnore] public string Path { get; }
    [JsonIgnore] public string? PasswordEnvironmentName { get; }
    public string ExpectedProjectLabel { get; }
    public IndividualAddress HostAddress { get; }
    public byte UserId { get; }
    public IndividualAddress TunnelAddress { get; }
    public string? ExpectedSourceFingerprint { get; }
    internal string SelectionId { get; }
    public SecureKeyringReference(string path, string expectedProjectLabel, IndividualAddress hostAddress, byte userId,
        IndividualAddress tunnelAddress, string? passwordEnvironmentName = null, string? expectedSourceFingerprint = null)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(expectedProjectLabel) || userId is < 1 or > 127
            || tunnelAddress.Device is 0 or 255 || hostAddress.Device == 255
            || expectedSourceFingerprint is not null && (expectedSourceFingerprint.Length != 64 || !expectedSourceFingerprint.All(char.IsAsciiHexDigit)))
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        if (passwordEnvironmentName is not null) EnvironmentSecurePasswordSource.ValidateName(passwordEnvironmentName);
        Path = System.IO.Path.GetFullPath(path); ExpectedProjectLabel = expectedProjectLabel; HostAddress = hostAddress;
        UserId = userId; TunnelAddress = tunnelAddress; PasswordEnvironmentName = passwordEnvironmentName;
        ExpectedSourceFingerprint = expectedSourceFingerprint?.ToUpperInvariant();
        SelectionId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Path, expectedProjectLabel,
            hostAddress.ToString(), userId.ToString(), tunnelAddress.ToString(), passwordEnvironmentName ?? "", ExpectedSourceFingerprint ?? ""))));
    }
    public override string ToString() => "[protected keyring reference]";
}

public sealed class KnxConnectionProfile
{
    private readonly byte[] _gateway;
    private readonly IPAddress? _local;
    public KnxTransportKind Kind { get; }
    [JsonIgnore] public IPEndPoint Gateway => new(new IPAddress(_gateway), Port);
    public int Port { get; }
    [JsonIgnore] public IPAddress? LocalAddress => _local is null ? null : new(_local.GetAddressBytes());
    public IndividualAddress RequestedAddress { get; }
    [JsonIgnore] public SecureKeyringReference? Keyring { get; }
    [JsonIgnore] public ISecurePasswordSource? PasswordSource { get; }
    [JsonIgnore] public KnxGroupSecurityProfile? GroupSecurity { get; private init; }
    public string EndpointId => $"knx-ip:{new IPAddress(_gateway)}:{Port}";
    public bool RequiresAuthentication => Kind == KnxTransportKind.IpSecureUdp;
    private KnxConnectionProfile(KnxTransportKind kind, IPEndPoint gateway, IndividualAddress requested,
        SecureKeyringReference? keyring, ISecurePasswordSource? passwordSource, IPAddress? local)
    {
        var bytes = gateway.Address.GetAddressBytes();
        if (gateway.AddressFamily != AddressFamily.InterNetwork || bytes[0] is 0 or >= 224 || gateway.Address.Equals(IPAddress.Broadcast)
            || gateway.Port is < 1 or > 65535 || requested.Device is 0 or 255 || local is not null && local.AddressFamily != AddressFamily.InterNetwork
            || kind == KnxTransportKind.IpSecureUdp && (keyring is null || keyring.TunnelAddress != requested)
            || kind == KnxTransportKind.PlainUdp && (keyring is not null || passwordSource is not null))
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        Kind = kind; _gateway = bytes; Port = gateway.Port; RequestedAddress = requested; Keyring = keyring; PasswordSource = passwordSource;
        _local = local is null ? null : new(local.GetAddressBytes());
    }
    public static KnxConnectionProfile Plain(IPEndPoint gateway, IndividualAddress requested, IPAddress? local = null)
        => new(KnxTransportKind.PlainUdp, gateway, requested, null, null, local);
    public static KnxConnectionProfile IpSecure(IPEndPoint gateway, SecureKeyringReference reference, ISecurePasswordSource? passwordSource = null, IPAddress? local = null)
        => new(KnxTransportKind.IpSecureUdp, gateway, reference.TunnelAddress, reference, passwordSource, local);
    public KnxConnectionProfile WithGroupSecurity(KnxGroupSecurityProfile groupSecurity)
    {
        if (!RequiresAuthentication || Keyring?.ExpectedSourceFingerprint is null || groupSecurity is null)
            throw new KnxConnectionException(KnxConnectionFailure.InvalidProfile);
        return new(Kind, Gateway, RequestedAddress, Keyring, PasswordSource, LocalAddress) { GroupSecurity = groupSecurity };
    }
    public bool EquivalentTo(KnxConnectionProfile other) => Kind == other.Kind && Gateway.Equals(other.Gateway)
        && RequestedAddress == other.RequestedAddress && Equals(_local, other._local) && Keyring?.SelectionId == other.Keyring?.SelectionId
        && GroupSecurity?.SelectionId == other.GroupSecurity?.SelectionId && ReferenceEquals(GroupSecurity?.StateSource, other.GroupSecurity?.StateSource);
    public override string ToString() => "[KNX connection profile: " + Kind + "]";
}
