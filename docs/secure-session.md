# Authenticated KNX IP Secure UDP sessions

The bus library provides `KnxIpSecureSession`, `KnxIpSecureCodec` and
`SecureSessionTransport` for one authenticated unicast UDP session. Establish the
secure session before constructing an ordinary `KnxTunnel` over its transport.
Pass `SecureSessionTransport.LocalEndpoint` explicitly as the tunnel's local
endpoint; the socket and advertised address must refer to the same interface.

The profile requires a user ID in 1–127, a 16-byte user key, a 16-byte device
authentication key and a six-byte local serial number. Device authentication is
mandatory. The optional password helpers accept at most 256 printable ASCII
characters and apply the protocol's distinct user/device PBKDF2 profiles.
Unsupported password encodings are refused rather than silently replaced.
Keys and passwords belong in a private store outside a source checkout.

The handshake authenticates the gateway's X25519 response, derives a fresh session
key and sends the user proof inside an authenticated wrapper. Only a verified
success status enables tunnelling. Wrong credentials, malformed responses,
timeouts and cancellation cannot fall back to a plain connection. Reusing a
failed or closed session requires a fresh session object and a fresh ephemeral key.

Wrappers authenticate the complete outer header, session identifier, sequence,
serial number and inner frame. The profile rejects nested security frames,
discovery/remote-configuration services, trailing bytes, replayed sequences and
peer serial changes. Rejected traffic does not advance the receive counter.
Tunnelling message tags are zero. A 48-bit send counter can exhaust but never wrap.

The transport pins the gateway endpoint. Only a successful authenticated
CONNECT_RESPONSE may admit another data port on that same gateway address.
Other endpoint changes are refused. The transport delivers the verified original
source endpoint to the tunnel. A wrapped keepalive runs every 30 seconds; remote
closure or keepalive failure ends the session. Concurrent disposal shares one
completion and joins admitted sends, receives, connection work and keepalive
before releasing its owned UDP transport and key material.

## Platform and acceptance limits

- Windows uses the OS `curve25519` provider through .NET 10 `ECDiffieHellman`.
- Linux uses the raw X25519 operations of installed OpenSSL 3. Native key handles
  are owned and released; the bound library remains loaded for process lifetime.
- Other platforms and missing provider operations fail explicitly. There is no
  managed scalar implementation or fallback to a different curve.
- Independent synthetic CBC/CTR/PBKDF2 vectors and RFC 7748 vectors check the
  cryptographic framing; adversarial sessions and an actual local UDP pair check
  the tunnel adapter and cleanup. These are software checks, not Secure-device
  acceptance. Workbench/MCP Secure connection controls remain separate work.
- The maintained bus suite and all 28 focused session/transport checks plus 16
  legacy gates passed on Windows and Ubuntu 26.04. Linux execution includes
  the actual OpenSSL 3 RFC 7748 agreement, published protocol vectors and local
  UDP adapter; it does not establish interoperability with a physical Secure gateway.
- This unicast profile does not implement Secure multicast timer synchronization,
  KNX Data Secure telegrams, durable Data Secure counters, keyring provisioning,
  protected key storage or secure first commissioning.

## Compatibility gates

Earlier `SecureWrapper`, `SecureSession`, `SecureTelegram`, `SecureApdu` and
`SecureKeys.DeriveKeys` profiles are unsupported legacy APIs. Their encoding
operations throw `NotSupportedException`; their parsing/authentication operations
return no result. They must not be used for real communication. In particular,
the old session helper cannot serialize a user key and the old Data Secure helper
cannot return plaintext labelled as encrypted data.

Keyring import/export remains available. `SecureKey.NextSequenceNumber` allocates
atomically in one process and refuses exhaustion; it is not a durable counter
store and does not enable Data Secure. Local JSON keyring files contain key
material and need appropriate private storage.

## Public protocol references

- [RFC 7748: X25519 and test vectors](https://www.rfc-editor.org/rfc/rfc7748)
- [Microsoft CNG named curves](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-named-elliptic-curves)
- [OpenSSL X25519 raw keys](https://docs.openssl.org/3.0/man7/EVP_PKEY-X25519/)
- [XKNX IP Secure protocol implementation](https://github.com/XKNX/xknx/blob/main/xknx/io/ip_secure.py)
- [Published KNX example inputs in XKNX tests](https://github.com/XKNX/xknx/blob/82e1a851ad9e31669bd8b7dbed59e6066bb7a9f7/test/secure_tests/ip_secure_test.py)
- [Calimero Secure unicast interoperability implementation](https://github.com/calimero-project/calimero-core/blob/master/src/io/calimero/knxnetip/SecureSessionUdp.java)

The protocol password derivation identifiers are public constants documented by
the XKNX and Calimero projects. They are independent of project-archive protection.
