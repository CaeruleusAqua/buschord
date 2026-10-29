using System.Buffers.Binary;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Knx.Bus.Tests")]

namespace Knx.Bus.Security;

public enum KnxIpSecureSessionState { New, AwaitingResponse, AwaitingAuthentication, Connected, Closed, Failed, Disposed }

/// <summary>
/// One authenticated unicast session. Device authentication is mandatory; a new session always creates a new ephemeral key.
/// Credentials are copied, never serialized, and cleared immediately after the handshake or its failure.
/// </summary>
public sealed class KnxIpSecureSession : IDisposable
{
    private readonly object _gate = new();
    private readonly ushort _userId;
    private readonly byte[] _serialNumber;
    private readonly Func<OsX25519KeyAgreement> _keyFactory;
    private byte[] _userKey, _deviceKey, _sessionKey = [];
    private byte[]? _peerSerial;
    private OsX25519KeyAgreement? _agreement;
    private ulong _sendSequence;
    private long _receivedSequence = -1;
    private KnxIpSecureSessionState _state;
    public KnxIpSecureSessionState State { get { lock (_gate) return _state; } }
    public ushort SessionId { get; private set; }

    public KnxIpSecureSession(ushort userId, ReadOnlySpan<byte> userKey, ReadOnlySpan<byte> deviceAuthenticationKey, ReadOnlySpan<byte> localSerialNumber)
        : this(userId, userKey, deviceAuthenticationKey, localSerialNumber, OsX25519KeyAgreement.Create) { }

    internal KnxIpSecureSession(ushort userId, ReadOnlySpan<byte> userKey, ReadOnlySpan<byte> deviceAuthenticationKey,
        ReadOnlySpan<byte> localSerialNumber, Func<OsX25519KeyAgreement> keyFactory)
    {
        KnxIpSecureCodec.Key(userKey); KnxIpSecureCodec.Key(deviceAuthenticationKey);
        if (userId is < 1 or > 127 || localSerialNumber.Length != 6) throw new ArgumentException("A secure tunnel requires user 1..127 and a six-byte local serial number.");
        _keyFactory = keyFactory ?? throw new ArgumentNullException(nameof(keyFactory));
        _userId = userId; _userKey = userKey.ToArray(); _deviceKey = deviceAuthenticationKey.ToArray(); _serialNumber = localSerialNumber.ToArray();
    }

    public byte[] CreateRequest(IPEndPoint localEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        lock (_gate)
        {
            Require(KnxIpSecureSessionState.New);
            try
            {
                var hpai = Hpai.Control(localEndpoint); _agreement = _keyFactory();
                var body = new byte[40]; hpai.CopyTo(body, 0); _agreement.PublicKey.CopyTo(body, 8);
                _state = KnxIpSecureSessionState.AwaitingResponse;
                return KnxIpSecureCodec.Frame((ushort)KnxIpSecureService.SessionRequest, body);
            }
            catch { Fail(); throw; }
        }
    }

    /// <summary>Authenticates the response before deriving the session key; returns the wrapped user authentication frame.</summary>
    public byte[] AcceptResponse(ReadOnlySpan<byte> response)
    {
        lock (_gate)
        {
            Require(KnxIpSecureSessionState.AwaitingResponse);
            try
            {
                if (KnxIpSecureCodec.Decode(response) != (ushort)KnxIpSecureService.SessionResponse || response.Length != 56)
                    throw new AuthenticationException("Invalid secure session response.");
                var sid = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
                if (sid == 0) throw new AuthenticationException("The gateway refused a secure session.");
                var peer = response.Slice(8, 32); var local = _agreement!.PublicKey;
                var xor = new byte[32]; for (var i = 0; i < xor.Length; i++) xor[i] = (byte)(local[i] ^ peer[i]);
                var additional = new byte[40]; response[..8].CopyTo(additional); xor.CopyTo(additional, 8);
                var expected = KnxIpSecureCodec.HandshakeTag(_deviceKey, additional);
                try { if (!CryptographicOperations.FixedTimeEquals(expected, response[^16..])) throw new AuthenticationException("Secure gateway authentication failed."); }
                finally { CryptographicOperations.ZeroMemory(expected); }
                var secret = _agreement.DeriveRawSecret(peer);
                try { var hash = SHA256.HashData(secret); try { _sessionKey = hash[..16]; } finally { CryptographicOperations.ZeroMemory(hash); } }
                finally { CryptographicOperations.ZeroMemory(secret); _agreement.Dispose(); _agreement = null; }
                SessionId = sid;
                var authentication = KnxIpSecureCodec.Frame((ushort)KnxIpSecureService.SessionAuthenticate, new byte[18]);
                BinaryPrimitives.WriteUInt16BigEndian(authentication.AsSpan(6), _userId);
                authentication.AsSpan(0, 8).CopyTo(additional); xor.CopyTo(additional, 8);
                var tag = KnxIpSecureCodec.HandshakeTag(_userKey, additional); tag.CopyTo(authentication, 8); CryptographicOperations.ZeroMemory(tag);
                _state = KnxIpSecureSessionState.AwaitingAuthentication;
                try { return WrapCore(authentication); }
                finally { CryptographicOperations.ZeroMemory(authentication); ClearCredentials(); }
            }
            catch { Fail(); throw; }
        }
    }

    /// <summary>Only authenticated tunnelling/control frames are released. Rejected frames do not advance the receive counter.</summary>
    public bool TryReceive(ReadOnlySpan<byte> frame, out byte[]? payload)
    {
        payload = null;
        lock (_gate)
        {
            if (_state is not (KnxIpSecureSessionState.AwaitingAuthentication or KnxIpSecureSessionState.Connected)
                || !KnxIpSecureCodec.TryUnwrap(frame, _sessionKey, out var packet)) return false;
            var data = packet!.Payload;
            try
            {
                if (packet!.SessionId != SessionId || packet.Sequence <= (ulong)Math.Max(_receivedSequence, 0) && _receivedSequence >= 0
                    || _peerSerial is not null && !CryptographicOperations.FixedTimeEquals(_peerSerial, packet.SerialNumber)) return false;
                var service = KnxIpSecureCodec.Decode(data);
                if (service == (ushort)KnxIpSecureService.SessionStatus)
                {
                    if (data.Length != 8 || data[7] != 0 || data[6] > 5) return false;
                    var status = data[6];
                    if (_state == KnxIpSecureSessionState.AwaitingAuthentication)
                    {
                        if (status != 0) { Fail(); return false; }
                        _state = KnxIpSecureSessionState.Connected; _peerSerial = packet.SerialNumber.ToArray();
                    }
                    else if (status is 1 or 2 or 3 or 5) { _state = KnxIpSecureSessionState.Closed; ClearSecrets(); }
                    else if (status != 4) return false;
                }
                else
                {
                    if (_state != KnxIpSecureSessionState.Connected || !TunnelService(service)) return false;
                    payload = data;
                }
                _receivedSequence = checked((long)packet.Sequence); return true;
            }
            finally { if (payload is null) CryptographicOperations.ZeroMemory(data); }
        }
    }

    public byte[] Wrap(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            Require(KnxIpSecureSessionState.Connected);
            if (!TunnelService(KnxIpSecureCodec.Decode(frame))) throw new InvalidOperationException("Only tunnelling and connection control services are admitted.");
            return WrapCore(frame);
        }
    }
    public byte[] CreateKeepalive() { lock (_gate) { Require(KnxIpSecureSessionState.Connected); return WrapCore(KnxIpSecureCodec.Frame((ushort)KnxIpSecureService.SessionStatus, [4, 0])); } }
    public byte[] CreateClose()
    {
        lock (_gate)
        {
            Require(KnxIpSecureSessionState.Connected);
            try { return WrapCore(KnxIpSecureCodec.Frame((ushort)KnxIpSecureService.SessionStatus, [5, 0])); }
            finally { _state = KnxIpSecureSessionState.Closed; ClearSecrets(); }
        }
    }
    private byte[] WrapCore(ReadOnlySpan<byte> frame)
    {
        if (_sendSequence > KnxIpSecureCodec.MaximumSequence) { Fail(); throw new CryptographicException("The secure session sequence is exhausted; establish a new session."); }
        return KnxIpSecureCodec.Wrap(SessionId, _sendSequence++, _serialNumber, frame, _sessionKey);
    }
    private static bool TunnelService(ushort service) => service is >= 0x0205 and <= 0x020A or 0x0420 or 0x0421;
    private void Require(KnxIpSecureSessionState state)
    {
        ObjectDisposedException.ThrowIf(_state == KnxIpSecureSessionState.Disposed, this);
        if (_state != state) throw new InvalidOperationException("The secure session is not in the required state.");
    }
    private void ClearCredentials() { CryptographicOperations.ZeroMemory(_userKey); CryptographicOperations.ZeroMemory(_deviceKey); _userKey = []; _deviceKey = []; }
    private void ClearSecrets() { ClearCredentials(); CryptographicOperations.ZeroMemory(_sessionKey); _sessionKey = []; _agreement?.Dispose(); _agreement = null; }
    private void Fail() { _state = KnxIpSecureSessionState.Failed; ClearSecrets(); }
    public void Dispose() { lock (_gate) { if (_state == KnxIpSecureSessionState.Disposed) return; _state = KnxIpSecureSessionState.Disposed; ClearSecrets(); } }
}
