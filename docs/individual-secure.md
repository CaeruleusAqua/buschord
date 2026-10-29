# Individual Data Secure and client synchronization

This profile implements explicitly provisioned unicast Tool-Access and ordinary
P2P communication with authentication and confidentiality. Tool Access uses
SCF90/92/93; ordinary P2P uses SCF10/12/13. It supports connectionless and
connection-oriented data, including all transport sequence values. The full
outer TPCI participates in authentication; encrypted inner APDUs have a zero
reserved prefix. Source/destination, address type, extended-frame format, SCF,
sequence and payload are authenticated. Routing flags may change.

`KnxIndividualSecureCodec` is a stateless wire codec. Successful decoding alone
does not authorize a sender or prevent replay. Its low-level response constructor
is a fixture/device-implementation seam, not a ready-to-use responder: no device
nonce allocator, responder rate policy or server state machine is supplied.

## Explicit provisioning

`KnxIndividualSecureContext` owns a copy of exactly one 16-byte key and borrows its
counter store. Its exact local address, peer, key kind, stable counter binding and
maximum wire-TPDU capacity must be supplied. The capacity must fit the 25-byte
Sync exchange; outgoing data includes the 13-byte Secure overhead. Oversized
requests fail before reserving a counter. This capability is not discovered by
guessing from device names or assumed extended-frame support.

Tool Access has a separate stable counter domain. Ordinary P2P must reuse the
same identity and store as the installation's ordinary group communication.
All cooperating processes share the corresponding private store. Project copies,
reconnects, selecting another peer or changing the active mode must retain their
existing domain bindings. An epoch change is an explicit trusted provisioning
decision; it must not automatically follow a key selection or reset counters for
other retained keys. A P2P key must be provisioned as an IA/P2P key. Group keys are
never substituted. No alternate keys are tried automatically.

The provisioned Tool and ordinary P2P keys must also be distinct. SCF is bound
into authentication but not the CTR nonce: reusing one key for the same sender
and individual destination in both counter domains would be invalid provisioning.
An individual context cannot inspect unrelated key inventories or other processes;
the provisioning/connection owner must validate this separation. This module does
not claim a global duplicate-key detector.

The existing protected keyring's `ToolKey` is a potential explicitly selected
Tool-Access credential. Its historical exported sequence is not a trusted local
send highwater or fresh runtime evidence. This module neither imports FDSKs nor
changes a device's Tool Key or security state.

## Sync and restart contract

A newly constructed or reset context blocks ordinary individual data until a
fresh authenticated Sync completes. A valid retained counter store remains
mandatory even for the Sync request. Missing, corrupt, anchor-inconsistent or
restore-blocked state cannot be replaced with a timestamp, zero counter, peer
floor or automatically created file.

Each new challenge reserves and durably consumes one local counter before its
request can be sent. Although the protocol does not advance its ordinary data
counter merely for Sync, this client conservatively skips that reserved nonce
for later data. A new challenge therefore cannot reuse a previous Sync's CTR
stream. Retries retain exactly the original secured request bytes.

Only one challenge may be pending per context. A response must match the exact
source/destination, key kind/key, connection mode and open six-byte challenge,
and authenticate within six seconds. Its random value is reconstructed from the
wire header and the pending challenge before verification. Unsolicited, duplicate,
expired, plain and invalid responses cannot publish readiness or application
data. Pending challenges are cleared after completion, cancellation or reset.

Authenticated next-valid values are merged into durable state: send highwater is
at least `localNext - 1`, and the peer receive floor at least `remoteNext - 1`.
Values never decrease. `MergeAuthenticatedSyncAsync` executes under the existing
cross-process transaction and requires current store readiness. It cannot clear
another owner's restore gate. Local nonce highwater is flushed before the peer
floor; interrupted or uncertain commits publish no session readiness. Reopening
requires another fresh Sync. Ordinary data is released only after its receive
floor has durably committed.

This does not reconstruct a lost trusted send highwater. Peer observations cannot
reveal counters previously reserved or encrypted without reception. Independent
monotonic-anchor and rollback limitations are the same as the group counter
store's documented contract. A faulted context cannot be made healthy by a
connection reset; recovery requires a new explicitly provisioned owner.

## Tunnel and management integration

`KnxIndividualSecureCemiProcessor` owns its context and an optional explicit
policy for all other traffic. The configured peer's data never falls through to
plain traffic. Its own confirmations are bound to actually prepared ciphertext,
consumed once and do not advance receive floors. Sync confirmations are consumed
without application dispatch. Defined transport controls remain plain as required
by their transport protocol; they are not represented as authenticated data.

`KnxTunnel.InitializeCemiSecurityAsync` invokes `ICemiSecurityInitializer` after
tunnel authentication and actual address assignment. The initializer uses a
connectionless unicast Sync before `KnxManagementClient.ConnectAsync`. Only the
exact owned pending Sync frame can pass the processor's outgoing path during
initialization. It uses the normal retained retry/ACK path. Concurrent or repeated
initialization is serialized and shares established readiness. Close cancels and
joins initialization before releasing resources; reconnect requires fresh Sync.

The connection owner must finish initialization before publishing secured
management access. `InitializeCemiSecurityAsync` is not a general raw-send API.
The receive loop only consumes Sync responses; this client profile rejects
incoming Sync requests and never waits for a response-send ACK from its receive
dispatch. Management data, group data and Sync state remain separate.

Dispose the tunnel/processor and join its work before disposing the borrowed
counter-store owner or credentials. No individual-security option is silently
enabled by the current application factory; UI/MCP integration is a separate
explicit follow-up.

## Evidence and boundaries

Checks reproduce four published Tool-Access Annex examples and twelve independent
Python AES-CBC/CTR vectors across Tool/P2P and connected/connectionless modes.
They cover full TPCI ranges, bounds, tampering, wrong contexts, concurrent Sync,
durable max merge, external restore gates, expiry, cancellation, replay,
caller-buffer mutation, restart, exhaustion, key cleanup and actual UDP loopback
through the existing management client. No hardware bus is used.

The maintained Bus suite passes on Windows and Ubuntu 26.04 with this profile.
Three additional public-anchor regressions cover throwing cancellation callbacks
in a group context, its processor and a composed individual/group owner. Cleanup
still joins outstanding work, clears owned buffers and completes repeated disposal
before reporting the callback failure.

Ordinary P2P vectors are independently generated against the public field/flag
construction, not claimed to be published normative ordinary-P2P goldens.
Physical Secure-device acceptance, power-cut validation, general server Sync,
system broadcast, FDSK first commissioning, key replacement and lost-highwater
recovery remain separate requirements.

- [XKNX Editor public Annex examples](https://github.com/XKNX/xknx-editor/blob/c41bc84c5ac6d8ab92f4fea0f79880fc901b2285/packages/download/tests/test_data_secure.py)
- [Calimero public Secure application layer and state handling](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/secure/SecureApplicationLayer.java)
- [Calimero public Annex vectors](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/test/io/calimero/secure/SecureApplicationLayerTest.java)
- [KNX Association security overview](https://support.knx.org/hc/en-us/articles/360012689639-KNX-Data-Secure)
