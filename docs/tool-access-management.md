# Explicit Tool-Access management

This API adds explicitly provisioned, client-side Tool-Access management over an
authenticated IP Secure tunnel. It reuses the existing protected keyring importer,
individual codec/Sync state machine and durable counter store. It does not enable
an App/MCP option, a general raw tunnel, FDSK commissioning, ordinary P2P key
derivation, server Sync, key replacement or missing-counter recovery.

`KnxConnectionFactory.AcquireToolAccessOperationAsync(connection, toolProfile)`
holds the gateway reservation, imported credential capability and existing counter
store over sequential `OpenManagementAsync(target, readOnly)` calls, including
fresh recovery connections. Existing public constructors and methods retain their
signatures. The optional provider overload is a trusted credential-provider seam,
matching the existing factory's injection model; its implementation must enforce
the same verified selection contract as `ProtectedToolAccessCredentialProvider`.

```csharp
using var state = new ExistingToolCounterStateSource(privateStatePath, integrityKey, monotonicAnchor);
var tool = new KnxToolAccessProfile(target, installationScope, toolKeyEpoch,
    declaredWireCapacity, state, completeOrdinaryP2pKeyFingerprints);
await using var operation = await factory.AcquireToolAccessOperationAsync(connection, tool, ct);
await using var management = await operation.OpenManagementAsync(target, readOnly: true, ct: ct);
var descriptor = await management.Client.ReadDescriptorAsync(ct);
```

## Required provisioning facts

- An explicit IP Secure connection profile, exact host/user/tunnel selection and
  expected SHA256 of the password-verified keyring bytes. The normal private
  password-source contract is used; no password/key is a command argument.
- One exact target IA with a 16-byte `Devices[target].ToolKey`. Interface passwords,
  group keys, management passwords and FDSKs cannot replace it. Gateway and Tool
  credentials are selected in the same verified import; the ring, XML and password
  copies are disposed/cleared before acquisition returns.
- A stable physical-installation scope and Tool-key epoch, bound to the requested
  local IA. Project copies and reconnects must retain these identities. Selecting
  a key/file never creates an epoch, directory, counter file or trusted floor.
- A previously provisioned private counter store with its independent integrity
  capability, registered peer and explicit wire-TPDU capacity (25..255 bytes).
  Device names or descriptors do not prove capacity. The caller must obtain it
  from an admitted device/profile contract.
- A complete inventory of ordinary P2P key SHA256 values for that installation.
  An explicitly supplied empty inventory asserts no ordinary P2P keys. The provider
  also checks every signed group key. Reusing the Tool key in either ordinary
  domain is refused; unknown external key inventories cannot be discovered here.

`ExistingToolCounterStateSource` normally requires an independent monotonic anchor.
`FromTrustedUnanchoredState` is a separate, explicit provisioning-owner assertion
that the supplied existing file is current. It does **not** detect an older valid
file restored across process lifetimes. No production anchor is implemented by
this module. A missing/corrupt/restore-blocked/uncertain/exhausted state cannot be
replaced by the historical keyring `SequenceNumber`, a clock, zero or a peer floor.

## Publication and ownership

Acquisition and every fresh open validate the existing manifest, readiness,
send-highwater and all receive pages before creating a KNX transport. This does
not reserve a nonce or clear a restore gate; normal anchored reconciliation may
confirm an already flushed pending checkpoint. Changes after this preflight are
still checked by each counter transaction and cannot publish management access.

The gateway handshake and actual AssignedIA check precede client Tool Sync.
`InitializeCemiSecurityAsync` must finish before management `T_CONNECT`; fresh
opens use a new challenge and the same durable domain. Data for other individual
destinations and all groups, including every known protected group, remain
blocked. Raw `OpenTunnelAsync` and a different management target are refused.
The existing `KnxConnectionSecurity.GatewayAuthenticated` still describes gateway
authentication only; it is not a physical-device identity certificate.

This operation does not plan an application download. Before any mutating load
sequence, its caller must check the declared capacity against every request,
reply, verification and complete recovery frame, including Secure overhead.
An oversized individual message is refused by the context; that per-message
check alone cannot make an already started multi-step load recoverable.

The operation joins initialization, sends, receive/heartbeat loops and active
management before disposing the borrowed-store owners and clearing imported
Tool/IP keys. Dispose is shared. Caller-owned password/state sources can be
disposed after successful acquisition; the operation owns independent copies.
Typed `KnxConnectionException.Failure` codes contain no path, password or key.
Cancellation remains `OperationCanceledException`; UI/MCP must retain their
central redacted error mapping and must not echo arbitrary provider exceptions.

## Evidence and limits

`ToolAccessChecks` exercises the actual signed synthetic keyring fixture, exact
ToolKey selection, zero-network state/selector refusals, key-domain separation,
key/store zeroing, cancellation/drain, fresh recovery and actual UDP loopback.
The shared gateway peer's CBC/CTR/X25519 handshake remains independently checked;
the new endpoint fixture uses the accepted individual codec as a fake responder.
This is end-to-end software/lifecycle evidence, not an additional independent
primitive golden or Secure-hardware acceptance. Existing public Annex and Python
goldens remain in [individual-secure.md](individual-secure.md).

The keyring selection is grounded in the KNX Association's
[published file/signature format](https://support.knx.org/hc/en-us/articles/18968368409874-Keyring-File-Format)
and [separate interface, device ToolKey and group-key fields](https://support.knx.org/hc/en-us/articles/360001582259-Use-keyring-outside-ETS-Falcon-SDK).
A password-verified signature is not a publisher certificate, proof of physical
ownership or fresh counter evidence. No third-party implementation is copied.
