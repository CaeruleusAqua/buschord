# KNX Data Secure group communication

`KnxDataSecureCodec` implements ordinary group S-A_Data. It supports authentication
only and authentication with confidentiality, uses a four-byte authentication tag,
and retains the distinction in its result. Authentication-only intentionally
leaves the APDU readable. A group configured for confidentiality rejects an
authentication-only downgrade.

The codec uses the shared raw cEMI parser and writer. Source/destination, address
type, extended-frame-format bits, TPCI/APCI, security control, sequence and payload
participate in verification. Routing hop count, priority, repeat and standard/
extended frame type may change without invalidating the message. Additional cEMI
information is retained. Exact lengths, supported group services and the 48-bit
sequence range are checked; unknown modes and sync/tool/broadcast services fail.

The low-level codec alone does not prevent replay or allocate safe nonces.
`KnxDataSecureGroupContext` takes an independent copy of each group key and its
explicit sender policy. It binds a fixed installation/project scope, key epoch and
local address to a counter store. Outgoing sources must match that local address.
Incoming data is released only after MAC verification, policy checks and a
durable receive-floor commit. Unknown groups/senders, plaintext, invalid MACs and
replays produce no application payload. Caller-buffer edits cannot change queued
authorization. These APIs perform no network action on their own.

## Tunnel integration and ownership

The optional `ICemiSecurityProcessor` constructor argument on `KnxTunnel` applies
an explicit cEMI policy. The existing constructor remains available for ordinary
connections. `KnxDataSecureCemiProcessor` owns its group context; the context
borrows its counter store. Dispose the tunnel and wait for its processor to finish
before disposing the store or its credential owner.

The processor checks the actual tunnel-assigned individual address against the
store's address. Every configured protected group, including group-value reads,
uses the secure path. Plain groups and individual management peers need separate
explicit allowlists; unknown destinations and Secure individual/tool services
are refused. A protected group never falls back to a plain telegram.

Outgoing caller data is copied before waiting for the tunnel send gate and
protected once while that gate is held.
The resulting owned bytes are retained across KNXnet/IP retries, so a retry cannot
consume another counter or encrypt different data with the previous nonce.
Counter flush and optional anchor confirmation complete before the first UDP
send. The tunnel also detaches a processor's returned array.

Incoming KNXnet/IP frames are acknowledged and deduplicated before processing.
A duplicate tunnel frame is acknowledged again without invoking the security
processor. A secure replay carried in a new tunnel frame is acknowledged but
produces no application telegram. Receive-floor persistence completes before
dispatch to subscribers.

An own `L_Data.con` must match an actually prepared frame's source, destination,
protected control bits and complete transmitted APDU. The bounded confirmation
cache consumes each match once, resets on connection close and never changes a
receive floor. A copied, unsolicited or stale confirmation therefore cannot be
used as authenticated incoming group data. Routing flags may differ. Connection
shutdown cancels and joins pending processing before keys are cleared; a failed
security reset prevents reuse of that tunnel instance.

## Counter storage and restoration

`DataSecureCounterStore.InitializeAsync` is an explicit provisioning operation.
It requires a trusted next-send sequence and every known sender's trusted receive
floor. It never derives those facts from a clock, project filename or missing
file. `OpenAsync` never creates replacement state. A stable scope token identifies
the physical installation across project copies; copying or renaming a project
must not silently create another counter domain for the same sender/key epoch.
All cooperating processes must use the same private store.

State lives at an absolute path outside Git. Its caller-supplied 32-byte integrity
key is retained only in memory. The authenticated manifest stores the scope hash,
local address and sender inventory; it contains no group key or integrity key.
Linux-created files use owner-read/write permissions. The application must keep
the directory and integrity key in appropriate private storage.

Each counter has two separate authenticated 4-KiB pages, with revisions and a
previous-digest link. Mutations hold an exclusive cross-process lock, write the
inactive page and complete `Flush(true)` before returning a send reservation or
received application data. The file size stays fixed. Reserved counters are
consumed even if cancellation or failure prevents transmission. Exhaustion never
wraps. Received observations using the local address also raise its outgoing
highwater before data release.
Trusted provisioning and restoration apply the same rule: the effective local
receive floor, previous send highwater and supplied next-send baseline are merged
upwards before ordinary sends can resume.

Without an external anchor, a damaged A/B page requires explicit trusted
synchronization; the store never silently uses an older valid page. Restoring a
complete earlier valid file after restarting the process is fundamentally
undetectable without an independent non-rollback reference. HMAC integrity alone
does not provide that reference.

An optional `IDataSecureMonotonicAnchor` supplies independent, linearizable,
durable checkpoints. Its presence is a caller-supplied capability, not proof that
the implementation or its storage cannot roll back. A locally flushed page one
revision ahead of the confirmed anchor can finish that pending commit. An anchor
ahead of the local file, missing checkpoints or conflicting digests refuse the
operation. An uncertain commit releases no data; a later operation must reconcile
the actual anchor. Ordinary JSON files and backups are not production monotonic
anchors. Rolling back both local state and its alleged anchor defeats that model.

`MarkSynchronizationRequiredAsync` blocks ordinary send reservations and receive
admission. Call it as part of a stopped/drained connection workflow and discard
previously prepared frames. `ConfirmTrustedCountersAsync` requires all configured
sender floors, merges them and the local send highwater upwards, and persists
readiness last. Interrupted confirmation remains blocked. It does not turn
unverified archive counters into trusted device evidence. Corrupt, missing or
anchor-inconsistent storage requires a separate trusted recovery/provisioning
decision; it is not automatically reset.

## Synchronization boundary

This group-key profile deliberately refuses S-A_Sync request/response services.
The public device implementations distinguish IA/P2P keys from group keys: a
unicast Sync has no group-address selector. A GA key therefore cannot be guessed
for an individually addressed peer. The public worked Sync examples inspected
use Tool Access; they do not prove ordinary group-key synchronization.

A new challenge does not make an old send sequence safe: the encryption counter
also depends on the source/destination and key. A response can raise known floors,
but cannot reveal all local counters previously reserved or encrypted and never
received by a peer. A lost trusted highwater is not repaired by choosing an
arbitrary sequence or by reading one peer's last received value.

Complete recovery needs separately provisioned P2P/tool-key context, validated
Sync request/response handling, pending-challenge/peer binding, timeouts and
complete device evidence. These are separate requirements; secure first
commissioning and protected key management are not claimed by this group profile.

## Validation and public references

Maintained checks use public synthetic group read/extended-write/response vectors,
independent Python cryptography vectors, tampering and malformed frames, separate
counter processes, an actual child-process exit after flush/before anchor commit,
corrupted pages, restored snapshots, unknown/replayed senders, restore gates,
caller-buffer races, duplicate tunnel acknowledgements, identical retry bytes,
held counter commits, confirmation binding and an actual UDP loopback exchange.
No private project, manufacturer archive or physical bus is
needed. Power-loss hardware acceptance and diverse Secure-device acceptance remain
separate from these software checks.
All 31 codec/counter/context checks, 11 tunnel-hook checks and the complete combined
bus suite pass on Windows and Ubuntu 26.04. Linux includes independent counter
processes, the real flush/anchor crash boundary and actual UDP loopback.

- [XKNX Data Secure primitives](https://github.com/XKNX/xknx/blob/main/xknx/secure/data_secure_asdu.py)
- [XKNX public group vectors](https://github.com/XKNX/xknx/blob/main/test/secure_tests/data_secure_test.py)
- [Calimero Secure application layer](https://github.com/calimero-project/calimero-core/blob/master/src/io/calimero/secure/SecureApplicationLayer.java)
- [Calimero device key-table distinction](https://github.com/calimero-project/calimero-device/blob/master/src/main/java/io/calimero/device/DeviceSecureApplicationLayer.java)
- [KNX Association Data Secure overview](https://support.knx.org/hc/en-us/articles/360012689639-KNX-Data-Secure)
