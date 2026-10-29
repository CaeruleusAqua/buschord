using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;
using Knx;
using Knx.Bus;
using Knx.Bus.Security;

internal static class SecureTunnelChecks
{
    private static readonly byte[] Alice = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
    private static readonly byte[] AlicePublic = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
    private static readonly byte[] Bob = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
    private static readonly byte[] BobPublic = Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
    private static readonly byte[] UserKey = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] DeviceKey = Enumerable.Range(32, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Serial = Hex("112233445566"), PeerSerial = Hex("aabbccddeeff");
    private static readonly byte[] Response = Hex("0610095200381234de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f1498cc021796b2660af6f71c5d6fc9d1");
    private static readonly byte[] Status = Hex("06100950002e1234000000000000aabbccddeeff00007c8161ad986bb55e59ddae19c1d6bdb6a3a8abc0a47007a3");
    private static readonly IPEndPoint Gateway = new(IPAddress.Loopback, 3671), Local = new(IPAddress.Loopback, 43123);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static byte[] Hex(string value) => Convert.FromHexString(value);
    private static void Require(bool result, string message) { if (!result) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("OS X25519 agrees with RFC 7748", () => Sync(CurveVector)),
            ("OS X25519 masks and reduces public encodings, rejects low order", () => Sync(CurveInputs)),
            ("OS key generation agrees and disposed keys cannot be reused", () => Sync(CurveLifetime)),
            ("wrapper equals independent CBC/CTR vector without block padding", () => Sync(WrapperVector)),
            ("every wrapper octet is length checked or authenticated", () => Sync(WrapperTamper)),
            ("wrapper rejects wrong keys, nested security and malformed inner lengths", () => Sync(WrapperBounds)),
            ("password profiles match independent PBKDF2 vectors and reject ambiguous text", () => Sync(PasswordVectors)),
            ("published KNX IP Secure KDF and handshake examples agree", () => Sync(PublishedHandshake)),
            ("handshake matches independently generated wire vectors", () => Sync(HandshakeVector)),
            ("all nonzero 16-bit session identifiers are admitted after authentication", () => Sync(SessionIdentifier)),
            ("gateway authentication fails before any user authentication frame", () => Sync(GatewayAuthentication)),
            ("no plaintext or application frame is accepted before user authentication", () => Sync(NoDowngrade)),
            ("user authentication rejection clears session secrets", () => Sync(UserAuthentication)),
            ("replays, wrong sessions and peer serial changes do not advance receive state", () => Sync(ReplayAndIdentity)),
            ("48-bit sequence exhaustion fails closed without wrap", () => Sync(SequenceExhaustion)),
            ("close and dispose clear retained credential and session buffers", () => Sync(KeyLifetime)),
            ("secure adapter supports the existing tunnel without plaintext fallback", AdapterTunnelAsync),
            ("secure adapter pins gateway and authenticated data endpoints", AdapterEndpointsAsync),
            ("secure adapter rejects gateway and user authentication failures", AdapterAuthenticationAsync),
            ("cancelled connect sends no frame and clears the single-use session", CancelledConnectAsync),
            ("disposal joins cancelled authentication handshake receive", DrainHandshakeAsync),
            ("handshake timeout refuses plaintext fallback and clears credentials", HandshakeTimeoutAsync),
            ("concurrent disposal joins blocked receive cancellation unwind", DrainReceiveAsync),
            ("concurrent disposal joins blocked sends and cancels queued sends", DrainSendAsync),
            ("remote close stops a pending receive and keepalive", RemoteCloseAsync),
            ("keepalive is authenticated and stops after joined disposal", KeepaliveAsync),
            ("keepalive failure aborts a pending receive", KeepaliveFailureAsync),
            ("real loopback secure UDP session releases its endpoint", RealUdpAsync),
        })
        {
            try { await run().WaitAsync(Deadline); Console.WriteLine("PASS SecureTunnel " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL SecureTunnel " + name + ": " + ex); }
        }
        return failures;
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static KnxIpSecureSession Session()
    {
        // A test-only private constructor seam selects the public RFC scalar instead of random key generation.
        // The maintained assembly receives access through InternalsVisibleTo.
        return new(2, UserKey, DeviceKey, Serial, () => OsX25519KeyAgreement.ImportPrivateKey(Alice));
    }
    private static byte[] SessionKey() { using var a = OsX25519KeyAgreement.ImportPrivateKey(Alice); var raw = a.DeriveRawSecret(BobPublic); try { return SHA256.HashData(raw)[..16]; } finally { CryptographicOperations.ZeroMemory(raw); } }
    private static KnxIpSecureSession Connected()
    {
        var session = Session(); session.CreateRequest(Local); session.AcceptResponse(Response);
        Require(session.TryReceive(Status, out var payload) && payload is null && session.State == KnxIpSecureSessionState.Connected, "No authenticated status.");
        return session;
    }
    private static void CurveVector()
    {
        using var alice = OsX25519KeyAgreement.ImportPrivateKey(Alice);
        Require(alice.PublicKey.SequenceEqual(AlicePublic), "RFC public key differs.");
        Require(Convert.ToHexString(alice.DeriveRawSecret(BobPublic)) == "4A5D9D5BA4CE2DE1728E3BF480350F25E07E21C947D19E3376F09B3C1E161742", "RFC shared secret differs.");
    }
    private static void CurveInputs()
    {
        using var alice = OsX25519KeyAgreement.ImportPrivateKey(Alice); var high = BobPublic.ToArray(); high[31] |= 128;
        Require(alice.DeriveRawSecret(high).SequenceEqual(alice.DeriveRawSecret(BobPublic)), "The public high bit was not masked.");
        var noncanonical = Enumerable.Repeat((byte)255, 32).ToArray(); noncanonical[0] = 246; noncanonical[31] = 127;
        Require(alice.DeriveRawSecret(noncanonical).SequenceEqual(AlicePublic), "Noncanonical public coordinate was not reduced.");
        Throws<CryptographicException>(() => alice.DeriveRawSecret(new byte[32])); var one = new byte[32]; one[0] = 1;
        Throws<CryptographicException>(() => alice.DeriveRawSecret(one)); Throws<ArgumentException>(() => alice.DeriveRawSecret(new byte[31]));
    }
    private static void CurveLifetime()
    {
        using var a = OsX25519KeyAgreement.Create(); using var b = OsX25519KeyAgreement.Create();
        Require(a.DeriveRawSecret(b.PublicKey).SequenceEqual(b.DeriveRawSecret(a.PublicKey)), "Generated agreement is asymmetric.");
        var copy = a.PublicKey; copy[0] ^= 255; Require(!copy.SequenceEqual(a.PublicKey), "Public key array was borrowed.");
        a.Dispose(); Throws<ObjectDisposedException>(() => _ = a.PublicKey); Throws<ObjectDisposedException>(() => a.DeriveRawSecret(b.PublicKey));
    }
    private static void WrapperVector()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var actual = KnxIpSecureCodec.Wrap(0x1234, 0x010203040506, Serial, Hex("0610020800087b00"), key);
        Require(Convert.ToHexString(actual).ToLowerInvariant() == "06100950002e123401020304050611223344556600007c3dd808c751da846061268bca74144bbc5c4a215fd1d74c", "Independent wrapper vector differs.");
        Require(KnxIpSecureCodec.TryUnwrap(actual, key, out var packet) && packet!.Payload.SequenceEqual(Hex("0610020800087b00"))
            && packet.Sequence == 0x010203040506 && packet.SessionId == 0x1234 && packet.SerialNumber.SequenceEqual(Serial), "Independent wrapper did not decode.");
    }
    private static void WrapperTamper()
    {
        var key = SessionKey(); var bytes = KnxIpSecureCodec.Wrap(0x1234, 0, Serial, Hex("0610020800087b00"), key);
        for (var i = 0; i < bytes.Length; i++) { var bad = bytes.ToArray(); bad[i] ^= 1; Require(!KnxIpSecureCodec.TryUnwrap(bad, key, out var packet) && packet is null, "Accepted modified byte " + i); }
        for (var length = 0; length < bytes.Length; length++) Require(!KnxIpSecureCodec.TryUnwrap(bytes.AsSpan(0, length), key, out _), "Accepted truncated wrapper.");
        Require(!KnxIpSecureCodec.TryUnwrap([.. bytes, 0], key, out _), "Accepted trailing data.");
    }
    private static void WrapperBounds()
    {
        var key = SessionKey(); var bytes = KnxIpSecureCodec.Wrap(0x1234, 0, Serial, Hex("0610020800087b00"), key);
        var wrong = key.ToArray(); wrong[0] ^= 1; Require(!KnxIpSecureCodec.TryUnwrap(bytes, wrong, out _), "Accepted wrong key.");
        Require(!KnxIpSecureCodec.TryUnwrap(KnxIpSecureCodec.Wrap(0x1234, 0, Serial, bytes, key), key, out _), "Accepted nested wrapper.");
        Throws<ArgumentException>(() => KnxIpSecureCodec.Wrap(0, 0, Serial, Hex("0610020800087b00"), key));
        Throws<ArgumentException>(() => KnxIpSecureCodec.Wrap(1, 1UL << 48, Serial, Hex("0610020800087b00"), key));
        Throws<ArgumentException>(() => KnxIpSecureCodec.Wrap(1, 0, Serial, Hex("0610020800087b00"), new byte[15]));
        Throws<FormatException>(() => KnxIpSecureCodec.Wrap(1, 0, Serial, Hex("0610020800097b00"), key));
        Throws<ArgumentOutOfRangeException>(() => KnxIpSecureCodec.Wrap(1, 0, Serial, new byte[KnxIpSecureCodec.MaximumPayloadLength + 1], key));
    }
    private static void PasswordVectors()
    {
        Require(Convert.ToHexString(KnxIpSecureCodec.DeriveUserKey("Synthetic secure password")) == "345345ED8F2E803BD42C9B8F4F261EB1", "User KDF differs.");
        Require(Convert.ToHexString(KnxIpSecureCodec.DeriveDeviceAuthenticationKey("Synthetic authentication")) == "8F4F6468D86AC34CC52928CE84394CD6", "Device KDF differs.");
        Throws<ArgumentException>(() => KnxIpSecureCodec.DeriveUserKey("\u00e4")); Throws<ArgumentException>(() => KnxIpSecureCodec.DeriveUserKey(new string('x', 257)));
    }
    private static void HandshakeVector()
    {
        using var session = Session(); var request = session.CreateRequest(Local); Require(request.Length == 46 && request[3] == 0x51 && request.AsSpan(14).SequenceEqual(AlicePublic), "Request has wrong geometry.");
        var actual = session.AcceptResponse(Response);
        Require(Convert.ToHexString(actual).ToLowerInvariant() == "06100950003e12340000000000001122334455660000cdd5ebdfdee9748bc94b52ddeccdb9539e5a67f0814bda8f7e454c5f4ce7423fade90601ba1b0adc", "Independent authentication vector differs.");
        Require(session.State == KnxIpSecureSessionState.AwaitingAuthentication && actual.AsSpan().IndexOf(UserKey) < 0, "Authentication state differs or exposed a key.");
        Require(session.TryReceive(Status, out var data) && data is null && session.State == KnxIpSecureSessionState.Connected, "Independent authenticated status was refused.");
    }
    private static void PublishedHandshake()
    {
        // Public AN159v06 example inputs retained by XKNX's secure protocol tests, separate from our generated vectors.
        var device = KnxIpSecureCodec.DeriveDeviceAuthenticationKey("trustme"); var user = KnxIpSecureCodec.DeriveUserKey("secret");
        Require(Convert.ToHexString(device) == "E158E4012047BD6CC41AAFBC5C04C1FC" && Convert.ToHexString(user) == "03FCEDB66660251EC81A1A716901696A", "Published password profiles differ.");
        var scalar = Hex("b8fabd62665d8b9e8a9d8b1f4bca42c8c2789a6110f50e9dd785b3ede883f378");
        var server = Hex("bdf099909923143ef0a5de0b3be3687bc5bd3cf5f9e6f901699cd870ec1ff824");
        using var session = new KnxIpSecureSession(1, user, device, Serial, () => OsX25519KeyAgreement.ImportPrivateKey(scalar));
        session.CreateRequest(Local); var proof = session.AcceptResponse(Frame(0x952, [0, 1, .. server, .. Hex("a922505aaa436163570bd5494c2df2a3")]));
        using var key = OsX25519KeyAgreement.ImportPrivateKey(scalar); var raw = key.DeriveRawSecret(server); var sessionKey = SHA256.HashData(raw)[..16]; CryptographicOperations.ZeroMemory(raw);
        Require(KnxIpSecureCodec.TryUnwrap(proof, sessionKey, out var packet) && packet!.Payload.SequenceEqual(Hex("06100953001800011f1d59ea9f12a152e5d9727f08462cde")), "Published user authentication proof differs.");
        CryptographicOperations.ZeroMemory(sessionKey); CryptographicOperations.ZeroMemory(user); CryptographicOperations.ZeroMemory(device);
    }
    private static void GatewayAuthentication()
    {
        foreach (var at in new[] { 6, 8, 24, 40, 55 })
        {
            using var session = Session(); session.CreateRequest(Local); var bad = Response.ToArray(); bad[at] ^= 1;
            Throws<AuthenticationException>(() => session.AcceptResponse(bad)); Require(session.State == KnxIpSecureSessionState.Failed, "Gateway failure left a session active.");
            Require(Secret(session, "_userKey").Length == 0 && Secret(session, "_deviceKey").Length == 0, "Credentials survived failure.");
        }
    }
    private static void SessionIdentifier()
    {
        using var session = Session(); session.CreateRequest(Local); var response = Response.ToArray(); response[6] = 255; response[7] = 255;
        var xor = AlicePublic.Zip(BobPublic, (a, b) => (byte)(a ^ b)).ToArray(); Tag(DeviceKey, [.. response.AsSpan(0, 8).ToArray(), .. xor]).CopyTo(response, 40);
        var authenticate = session.AcceptResponse(response); Require(authenticate[6] == 255 && authenticate[7] == 255, "The full nonzero session-ID range was truncated.");
        Require(session.TryReceive(KnxIpSecureCodec.Wrap(ushort.MaxValue, 0, PeerSerial, Hex("0610095400080000"), SessionKey()), out _)
            && session.SessionId == ushort.MaxValue && session.State == KnxIpSecureSessionState.Connected, "A valid authenticated FFFF session was refused.");
    }
    private static void NoDowngrade()
    {
        using var session = Session(); Throws<InvalidOperationException>(() => session.Wrap(Hex("0610020800087b00"))); session.CreateRequest(Local); session.AcceptResponse(Response);
        Require(!session.TryReceive(Hex("0610095400080000"), out _), "Accepted plaintext authentication.");
        Require(!session.TryReceive(KnxIpSecureCodec.Wrap(0x1234, 0, PeerSerial, Hex("0610020800087b00"), SessionKey()), out _), "Released an application frame before user authentication.");
        Require(session.TryReceive(Status, out _) && session.State == KnxIpSecureSessionState.Connected, "Invalid prior frames poisoned authentication.");
        Throws<InvalidOperationException>(() => session.Wrap(Hex("0610095400080400")));
    }
    private static void UserAuthentication()
    {
        using var session = Session(); session.CreateRequest(Local); session.AcceptResponse(Response);
        Require(!session.TryReceive(KnxIpSecureCodec.Wrap(0x1234, 0, PeerSerial, Hex("0610095400080100"), SessionKey()), out _)
            && session.State == KnxIpSecureSessionState.Failed && Secret(session, "_sessionKey").Length == 0, "User rejection retained a session key.");
    }
    private static void ReplayAndIdentity()
    {
        using var session = Connected(); var key = SessionKey(); var plain = Hex("0610020800087b00");
        var wrongMac = KnxIpSecureCodec.Wrap(0x1234, 99, PeerSerial, plain, key); wrongMac[^1] ^= 1;
        Require(!session.TryReceive(wrongMac, out _) && !session.TryReceive(Status, out _), "Invalid MAC or replay accepted.");
        Require(!session.TryReceive(KnxIpSecureCodec.Wrap(0x1235, 99, PeerSerial, plain, key), out _), "Wrong session accepted.");
        Require(!session.TryReceive(KnxIpSecureCodec.Wrap(0x1234, 99, Serial, plain, key), out _), "Changed peer identity accepted.");
        var next = KnxIpSecureCodec.Wrap(0x1234, 1, PeerSerial, plain, key);
        Require(session.TryReceive(next, out var payload) && payload!.SequenceEqual(plain), "Rejected frames advanced replay state.");
        Require(!session.TryReceive(next, out _), "Replay released data twice.");
    }
    private static void SequenceExhaustion()
    {
        using var session = Connected(); typeof(KnxIpSecureSession).GetField("_sendSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, KnxIpSecureCodec.MaximumSequence);
        var last = session.Wrap(Hex("0610020800087b00")); Require(KnxIpSecureCodec.TryUnwrap(last, SessionKey(), out var packet) && packet!.Sequence == KnxIpSecureCodec.MaximumSequence, "Last sequence differs.");
        Throws<CryptographicException>(() => session.Wrap(Hex("0610020800087b00"))); Require(session.State == KnxIpSecureSessionState.Failed, "Exhaustion did not close the session.");
    }
    private static byte[] Secret(KnxIpSecureSession session, string name) => (byte[])typeof(KnxIpSecureSession).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
    private static void KeyLifetime()
    {
        var session = Session(); var user = Secret(session, "_userKey"); var device = Secret(session, "_deviceKey"); session.CreateRequest(Local); session.AcceptResponse(Response);
        Require(user.All(b => b == 0) && device.All(b => b == 0), "Credentials were not cleared after handshake.");
        session.TryReceive(Status, out _); var key = Secret(session, "_sessionKey"); session.CreateClose(); Require(key.All(b => b == 0), "Session key survived close.");
        session.Dispose(); session.Dispose(); Throws<ObjectDisposedException>(() => session.CreateRequest(Local));
    }

    private static async Task AdapterTunnelAsync()
    {
        var peer = new Peer(); var adapter = new SecureSessionTransport(peer, Gateway, Local, Session());
        await adapter.ConnectAsync(); await using var tunnel = new KnxTunnel(adapter, Gateway, new(1, 1, 240), adapter.LocalEndpoint);
        await tunnel.ConnectAsync(); Require(tunnel.State == BusConnectionState.Connected && tunnel.AssignedAddress == new IndividualAddress(1, 1, 240), "Existing tunnel did not connect through authenticated adapter.");
        await tunnel.GroupValueReadAsync(new GroupAddress(1), CancellationToken.None); await tunnel.CloseAsync(); await adapter.DisposeAsync();
        Require(peer.PlainSends == 1 && peer.ApplicationSends >= 3 && peer.AuthenticatedUser && peer.Disposals == 1, "Plaintext, auth or ownership violation.");
    }
    private static async Task AdapterEndpointsAsync()
    {
        var peer = new Peer(); await using var adapter = new SecureSessionTransport(peer, Gateway, Local, Session()); await adapter.ConnectAsync();
        var sends = peer.TotalSends; await ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(new(IPAddress.Parse("192.0.2.18"), 3671), Hex("0610020700080700")));
        Require(peer.TotalSends == sends, "Rejected endpoint reached transport.");
        peer.Push(KnxIpSecureCodec.Wrap(0x1234, 100, PeerSerial, Hex("0610020800080700"), SessionKey()), new(IPAddress.Loopback, 4671));
        peer.PushPlain(Hex("0610020800080700")); peer.PushPayload(Hex("0610020800080700"));
        var result = await adapter.ReceiveAsync(); Require(result.Buffer.SequenceEqual(Hex("0610020800080700")) && result.RemoteEndPoint.Equals(Gateway), "Spoofed/plain frame was released or endpoint changed.");
    }
    private static async Task AdapterAuthenticationAsync()
    {
        foreach (var badGateway in new[] { true, false })
        {
            var peer = new Peer { BadGateway = badGateway, RejectUser = !badGateway };
            await using var adapter = new SecureSessionTransport(peer, Gateway, Local, Session()); await ThrowsAsync<AuthenticationException>(() => adapter.ConnectAsync());
            Require(!adapter.IsConnected && peer.ApplicationSends == 0 && peer.PlainSends == 1, "Authentication failure fell back to plaintext/application traffic.");
        }
    }
    private static async Task CancelledConnectAsync()
    {
        var peer = new Peer(); await using var adapter = new SecureSessionTransport(peer, Gateway, Local, Session());
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await ThrowsAsync<OperationCanceledException>(() => adapter.ConnectAsync(cancel.Token)); Require(peer.TotalSends == 0, "Cancelled connect reached UDP.");
    }
    private static async Task DrainHandshakeAsync()
    {
        var peer = new Peer { SuppressResponse = true, HoldReceiveUnwind = true }; var session = Session();
        var adapter = new SecureSessionTransport(peer, Gateway, Local, session); var connecting = adapter.ConnectAsync(); await peer.ReceiveStarted.Task;
        await ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Gateway, Hex("0610020700080700")));
        var disposing = adapter.DisposeAsync().AsTask(); await peer.ReceiveCancelled.Task;
        Require(!disposing.IsCompleted && peer.Disposals == 0, "Handshake receive was not drained.");
        peer.ReleaseReceive.TrySetResult(); await disposing; await ThrowsAsync<OperationCanceledException>(() => connecting);
        Require(session.State == KnxIpSecureSessionState.Disposed && Secret(session, "_userKey").Length == 0, "Cancelled handshake retained credentials.");
    }
    private static async Task HandshakeTimeoutAsync()
    {
        var peer = new Peer { SuppressResponse = true }; var session = Session();
        await using var adapter = new SecureSessionTransport(peer, Gateway, Local, session, handshakeTimeout: TimeSpan.FromMilliseconds(60));
        await ThrowsAsync<OperationCanceledException>(() => adapter.ConnectAsync());
        Require(peer.PlainSends == 1 && peer.ApplicationSends == 0 && Secret(session, "_userKey").Length == 0, "Handshake timeout fell back or retained credentials.");
    }
    private static async Task DrainReceiveAsync()
    {
        var peer = new Peer(); var adapter = new SecureSessionTransport(peer, Gateway, Local, Session()); await adapter.ConnectAsync();
        peer.HoldReceiveUnwind = true; var reading = adapter.ReceiveAsync(); await peer.ReceiveStarted.Task;
        var close = adapter.DisposeAsync().AsTask(); await peer.ReceiveCancelled.Task;
        var again = adapter.DisposeAsync().AsTask(); Require(ReferenceEquals(close, again) && !close.IsCompleted && peer.Disposals == 0, "Dispose did not share or drain receive completion.");
        peer.ReleaseReceive.TrySetResult(); await close; await ThrowsAsync<OperationCanceledException>(() => reading); Require(peer.Disposals == 1, "UDP was not disposed exactly once.");
    }
    private static async Task DrainSendAsync()
    {
        var peer = new Peer(); var adapter = new SecureSessionTransport(peer, Gateway, Local, Session()); await adapter.ConnectAsync();
        peer.HoldSendUnwind = true; var send = adapter.SendAsync(Gateway, Hex("0610020700080700")); await peer.SendStarted.Task;
        var queued = adapter.SendAsync(Gateway, Hex("0610020700080700")); var close = adapter.DisposeAsync().AsTask(); await peer.SendCancelled.Task;
        Require(!close.IsCompleted && peer.Disposals == 0, "Disposed a transport before send unwound.");
        peer.HoldSendUnwind = false; peer.ReleaseSend.TrySetResult(); await close;
        await ThrowsAsync<OperationCanceledException>(() => send); await ThrowsAsync<OperationCanceledException>(() => queued);
    }
    private static async Task RemoteCloseAsync()
    {
        var peer = new Peer(); await using var adapter = new SecureSessionTransport(peer, Gateway, Local, Session()); await adapter.ConnectAsync();
        var read = adapter.ReceiveAsync(); peer.PushPayload(Hex("0610095400080500")); await ThrowsAsync<IOException>(() => read); Require(!adapter.IsConnected, "Remote close retained a connected session.");
    }
    private static async Task KeepaliveAsync()
    {
        var peer = new Peer(); var adapter = new SecureSessionTransport(peer, Gateway, Local, Session(), keepaliveInterval: TimeSpan.FromMilliseconds(20)); await adapter.ConnectAsync();
        await peer.KeepaliveSeen.Task; await adapter.DisposeAsync(); var count = peer.TotalSends; await Task.Delay(60); Require(peer.TotalSends == count && peer.PlainSends == 1 && peer.Keepalives > 0, "Keepalive was plaintext or survived disposal.");
    }
    private static async Task KeepaliveFailureAsync()
    {
        var peer = new Peer { FailKeepalive = true }; await using var adapter = new SecureSessionTransport(peer, Gateway, Local, Session(), keepaliveInterval: TimeSpan.FromMilliseconds(20)); await adapter.ConnectAsync();
        await ThrowsAsync<IOException>(() => adapter.ReceiveAsync()); Require(!adapter.IsConnected, "Failed keepalive retained connected state.");
    }
    private static async Task RealUdpAsync()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var gateway = (IPEndPoint)server.Client.LocalEndPoint!;
        var underlying = new UdpTransport(IPAddress.Loopback, 0); var local = underlying.LocalEndPoint;
        var peer = new Peer(gateway); using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var incoming = await server.ReceiveAsync(stop.Token); var replies = peer.Process(incoming.Buffer); foreach (var reply in replies) await server.SendAsync(reply, incoming.RemoteEndPoint, stop.Token); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            await using (var adapter = new SecureSessionTransport(underlying, gateway, local, Session()))
            { await adapter.ConnectAsync(); await adapter.SendAsync(gateway, Hex("0610020700080700")); Require((await adapter.ReceiveAsync()).Buffer.SequenceEqual(Hex("0610020800080700")), "Real UDP secure response differs."); }
            using var rebound = new UdpClient(local); Require(rebound.Client.LocalEndPoint!.Equals(local), "Secure UDP endpoint was not released.");
        }
        finally { stop.Cancel(); await serving; peer.Dispose(); }
    }

    private sealed class Peer(IPEndPoint? endpoint = null) : IUdpTransport
    {
        private readonly Channel<UdpReceiveResult> _incoming = Channel.CreateUnbounded<UdpReceiveResult>();
        private readonly IPEndPoint _endpoint = endpoint ?? Gateway;
        private byte[] _xor = [], _key = [];
        private ulong _sequence;
        public bool BadGateway, RejectUser, AuthenticatedUser, HoldReceiveUnwind, HoldSendUnwind, SuppressResponse, FailKeepalive;
        public int TotalSends, PlainSends, ApplicationSends, Disposals, Keepalives;
        public TaskCompletionSource ReceiveStarted { get; } = Gate(); public TaskCompletionSource ReceiveCancelled { get; } = Gate(); public TaskCompletionSource ReleaseReceive { get; } = Gate();
        public TaskCompletionSource SendStarted { get; } = Gate(); public TaskCompletionSource SendCancelled { get; } = Gate(); public TaskCompletionSource ReleaseSend { get; } = Gate(); public TaskCompletionSource KeepaliveSeen { get; } = Gate();
        public async Task SendAsync(IPEndPoint destination, byte[] data, CancellationToken ct = default)
        {
            if (HoldSendUnwind)
            {
                SendStarted.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { SendCancelled.TrySetResult(); await ReleaseSend.Task; throw; }
            }
            foreach (var response in Process(data)) Push(response, _endpoint);
        }
        public IReadOnlyList<byte[]> Process(byte[] data)
        {
            Interlocked.Increment(ref TotalSends);
            var service = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (service == 0x951)
            {
                PlainSends++; Require(data.Length == 46, "Invalid request size."); var client = data.AsSpan(14, 32).ToArray();
                using var bob = OsX25519KeyAgreement.ImportPrivateKey(Bob); var raw = bob.DeriveRawSecret(client); _key = SHA256.HashData(raw)[..16]; CryptographicOperations.ZeroMemory(raw);
                _xor = client.Zip(bob.PublicKey, (a, b) => (byte)(a ^ b)).ToArray();
                var response = Frame(0x952, [0x12, 0x34, .. bob.PublicKey, .. new byte[16]]); Tag(DeviceKey, [.. response.AsSpan(0, 8).ToArray(), .. _xor]).CopyTo(response, 40);
                if (BadGateway) response[^1] ^= 1; return SuppressResponse ? [] : [response];
            }
            Require(service == 0x950 && KnxIpSecureCodec.TryUnwrap(data, _key, out _), "Plaintext fallback or invalid secure wrapper.");
            KnxIpSecureCodec.TryUnwrap(data, _key, out var packet); var payload = packet!.Payload; service = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
            if (service == 0x953)
            {
                Require(payload.Length == 24 && payload[6] == 0 && payload[7] == 2 && CryptographicOperations.FixedTimeEquals(payload.AsSpan(8), Tag(UserKey, [.. payload.AsSpan(0, 8).ToArray(), .. _xor])), "Invalid independent user authentication.");
                AuthenticatedUser = !RejectUser; return [Wrap(Frame(0x954, [RejectUser ? (byte)1 : (byte)0, 0]))];
            }
            Require(AuthenticatedUser, "Application sent before authentication."); ApplicationSends++;
            return service switch
            {
                0x205 => [Wrap(Frame(0x206, [7, 0, .. Hpai.Control(_endpoint), .. Crd.Tunnel(new(1, 1, 240))]))],
                0x207 => [Wrap(Frame(0x208, [7, 0]))],
                0x209 => [Wrap(Frame(0x20A, [7, 0]))],
                0x420 => [Wrap(Frame(0x421, [4, 7, payload[8], 0]))],
                0x954 => Status(payload),
                _ => []
            };
        }
        private IReadOnlyList<byte[]> Status(byte[] payload) { if (payload[6] == 4) { Keepalives++; KeepaliveSeen.TrySetResult(); if (FailKeepalive) throw new IOException("Synthetic keepalive failure."); } return []; }
        private byte[] Wrap(byte[] payload) => KnxIpSecureCodec.Wrap(0x1234, _sequence++, PeerSerial, payload, _key);
        public void PushPayload(byte[] payload) => Push(Wrap(payload), _endpoint);
        public void PushPlain(byte[] frame) => Push(frame, _endpoint);
        public void Push(byte[] frame, IPEndPoint endpoint) => _incoming.Writer.TryWrite(new(frame, endpoint));
        public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct = default)
        {
            if (HoldReceiveUnwind) ReceiveStarted.TrySetResult();
            try { return await _incoming.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { ReceiveCancelled.TrySetResult(); if (HoldReceiveUnwind) await ReleaseReceive.Task; throw; }
        }
        public void Dispose() { Disposals++; CryptographicOperations.ZeroMemory(_key); }
    }
    private static byte[] Frame(ushort service, byte[] body) => KnxNetIpFrame.Encode((ServiceType)service, body);
    private static byte[] Tag(byte[] key, byte[] additional)
    {
        // Independent server-side handshake reference: B0=0, two-byte AAD length, zero padding, AES CBC then CTR block zero.
        var length = 18 + additional.Length; var data = new byte[(length + 15) / 16 * 16]; BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)additional.Length); additional.CopyTo(data, 18);
        using var aes = Aes.Create(); aes.Key = key; var mac = aes.EncryptCbc(data, new byte[16], PaddingMode.None)[^16..]; var counter = new byte[16]; counter[14] = 255;
        var stream = aes.EncryptEcb(counter, PaddingMode.None); for (var i = 0; i < 16; i++) mac[i] ^= stream[i]; return mac;
    }
}
