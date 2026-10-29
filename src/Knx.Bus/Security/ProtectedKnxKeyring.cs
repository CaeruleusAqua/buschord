using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Knx.Bus.Security;

/// <summary>The supported ETS project-export profile in http://knx.org/xml/keyring/1.</summary>
public enum ProtectedKeyringInterfaceType { Tunneling, Backbone, Usb }
public enum ProtectedKeyringSecretKind { Aes128Key, Utf8Password }
public enum ProtectedKeyringFailure { InvalidFormat, UnsupportedProfile, LimitExceeded, SignatureMismatch, ProjectMismatch }

/// <summary>Contains no source XML, credentials or supplied password in its message.</summary>
public sealed class ProtectedKeyringImportException(ProtectedKeyringFailure failure, string message) : FormatException(message)
{
    public ProtectedKeyringFailure Failure { get; } = failure;
}

/// <summary>
/// Secret owned by the imported keyring. CopyBytes returns a detached, caller-owned buffer;
/// the caller must clear that copy. Disposing the keyring clears its copies and prevents later exports.
/// Password fields contain UTF-8 password bytes, not derived KNX IP Secure keys.
/// </summary>
public sealed class ProtectedKeyringSecret
{
    private readonly ProtectedKeyringSecrets _owner;
    private readonly byte[] _bytes;
    public ProtectedKeyringSecretKind Kind { get; }
    public int Length => _bytes.Length;
    internal ProtectedKeyringSecret(ProtectedKeyringSecrets owner, byte[] bytes, ProtectedKeyringSecretKind kind)
    { _owner = owner; _bytes = bytes; Kind = kind; }
    public byte[] CopyBytes() => _owner.Copy(_bytes);
    public override string ToString() => "[protected keyring secret]";
}

public sealed class ProtectedKeyringInterface
{
    public ProtectedKeyringInterfaceType Type { get; }
    public IndividualAddress Address { get; }
    public IndividualAddress? Host { get; }
    public byte? UserId { get; }
    public ProtectedKeyringSecret? UserPassword { get; }
    public ProtectedKeyringSecret? DeviceAuthenticationPassword { get; }
    public IReadOnlyDictionary<GroupAddress, IReadOnlyList<IndividualAddress>> Groups { get; }
    internal ProtectedKeyringInterface(ProtectedKeyringInterfaceType type, IndividualAddress address,
        IndividualAddress? host, byte? userId, ProtectedKeyringSecret? password, ProtectedKeyringSecret? authentication,
        Dictionary<GroupAddress, IReadOnlyList<IndividualAddress>> groups)
    {
        Type = type; Address = address; Host = host; UserId = userId;
        UserPassword = password; DeviceAuthenticationPassword = authentication;
        Groups = new ReadOnlyDictionary<GroupAddress, IReadOnlyList<IndividualAddress>>(groups);
    }
}

public sealed class ProtectedKeyringDevice
{
    public IndividualAddress Address { get; }
    public ProtectedKeyringSecret? ToolKey { get; }
    public ProtectedKeyringSecret? ManagementPassword { get; }
    public ProtectedKeyringSecret? AuthenticationPassword { get; }
    /// <summary>Historical export metadata; not a fresh, authenticated runtime counter baseline.</summary>
    public ulong? ExportedSequenceNumber { get; }
    internal ProtectedKeyringDevice(IndividualAddress address, ProtectedKeyringSecret? toolKey,
        ProtectedKeyringSecret? management, ProtectedKeyringSecret? authentication, ulong? sequence)
    { Address = address; ToolKey = toolKey; ManagementPassword = management; AuthenticationPassword = authentication; ExportedSequenceNumber = sequence; }
}

public sealed class ProtectedKeyringBackbone
{
    public string MulticastAddress { get; }
    public ushort? LatencyMilliseconds { get; }
    public ProtectedKeyringSecret? Key { get; }
    internal ProtectedKeyringBackbone(string multicast, ushort? latency, ProtectedKeyringSecret? key)
    { MulticastAddress = multicast; LatencyMilliseconds = latency; Key = key; }
}

/// <summary>
/// Fully verified project export. The password-based KNX signature verifies file integrity and
/// password agreement; it does not certify a publisher, a unique project ID, device ownership or counter freshness.
/// No conversion to the legacy unverified Keyring model occurs.
/// </summary>
public sealed class ProtectedKnxKeyring : IDisposable
{
    private readonly ProtectedKeyringSecrets _secrets;
    public string ProjectName { get; }
    public string Created { get; }
    public string? CreatedBy { get; }
    /// <summary>SHA-256 of the exact input bytes, for explicit caller-side source binding.</summary>
    public string SourceFingerprint { get; }
    public ProtectedKeyringBackbone? Backbone { get; }
    public IReadOnlyDictionary<IndividualAddress, ProtectedKeyringInterface> Interfaces { get; }
    public IReadOnlyDictionary<IndividualAddress, ProtectedKeyringDevice> Devices { get; }
    public IReadOnlyDictionary<GroupAddress, ProtectedKeyringSecret> GroupKeys { get; }
    internal ProtectedKnxKeyring(ProtectedKeyringSecrets secrets, string project, string created, string? createdBy,
        string fingerprint, ProtectedKeyringBackbone? backbone,
        Dictionary<IndividualAddress, ProtectedKeyringInterface> interfaces,
        Dictionary<IndividualAddress, ProtectedKeyringDevice> devices, Dictionary<GroupAddress, ProtectedKeyringSecret> groups)
    {
        _secrets = secrets; ProjectName = project; Created = created; CreatedBy = createdBy;
        SourceFingerprint = fingerprint; Backbone = backbone;
        Interfaces = new ReadOnlyDictionary<IndividualAddress, ProtectedKeyringInterface>(interfaces);
        Devices = new ReadOnlyDictionary<IndividualAddress, ProtectedKeyringDevice>(devices);
        GroupKeys = new ReadOnlyDictionary<GroupAddress, ProtectedKeyringSecret>(groups);
    }

    /// <summary>Selects exactly the declared host, user and tunnel address; never falls back to another interface or device credentials.</summary>
    public ProtectedKeyringInterface SelectTunneling(IndividualAddress host, byte userId, IndividualAddress tunnelAddress)
    {
        _secrets.ThrowIfDisposed();
        if (!Interfaces.TryGetValue(tunnelAddress, out var entry) || entry.Type != ProtectedKeyringInterfaceType.Tunneling
            || entry.Host != host || entry.UserId != userId || entry.UserPassword is null || entry.DeviceAuthenticationPassword is null)
            throw new InvalidOperationException("No credentialed tunnel matches the explicit host, user and interface address.");
        return entry;
    }

    /// <summary>Returns only the group keys assigned to this exact interface. Sender lists remain available on the interface entry.</summary>
    public IReadOnlyDictionary<GroupAddress, ProtectedKeyringSecret> SelectInterfaceGroupKeys(IndividualAddress interfaceAddress)
    {
        _secrets.ThrowIfDisposed();
        if (!Interfaces.TryGetValue(interfaceAddress, out var entry)) throw new InvalidOperationException("The requested interface is absent.");
        return new ReadOnlyDictionary<GroupAddress, ProtectedKeyringSecret>(entry.Groups.Keys.ToDictionary(group => group, group => GroupKeys[group]));
    }
    public void Dispose() => _secrets.Dispose();
}

internal sealed class ProtectedKeyringSecrets : IDisposable
{
    private readonly object _gate = new();
    private readonly List<byte[]> _buffers = [];
    private bool _disposed;
    internal ProtectedKeyringSecret Adopt(byte[] bytes, ProtectedKeyringSecretKind kind)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _buffers.Add(bytes); return new(this, bytes, kind); }
    }
    internal byte[] Copy(byte[] bytes) { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return bytes.ToArray(); } }
    internal void ThrowIfDisposed() { lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var bytes in _buffers) CryptographicOperations.ZeroMemory(bytes);
            _buffers.Clear();
        }
    }
}
