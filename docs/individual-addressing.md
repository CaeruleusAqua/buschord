# Individual address wire services and discovery

The library provides programming-mode address reads and exact serial-number
queries on an already owned connection. Both use the normal group broadcast at
address zero with System priority. Low remains the default for ordinary telegrams;
source substitution and receive parsing preserve the actual priority.

`IndividualAddressDiscovery.ReadProgrammingModeAsync` and `ReadSerialAsync` accept
an `IBusDiagnosticSession`, the actual tunnel client address and an explicit bounded
observation window. Serial selectors are copied before I/O. Replies must match the
service, destination and complete payload; serial replies also match all six serial
bytes and the reserved fields. Cancellation joins the active send before releasing
the subscription. At most 256 matching responses are processed.

Results retain raw response counts and distinct addresses separately. Duplicate
addresses do not establish physical-device uniqueness. Silence remains `NoResponse`;
it does not establish that an address is free. Multiple addresses, malformed replies
and response overflow remain explicit outcomes. The normal factory address can be
observed, including during first addressing.

Low-level builders also provide the broadcast address-write payload and serial
services `0x3DC`/`0x3DD`/`0x3DE`. The typed codec retains all service bits in the
`0x3C0` family and refuses conflicting inline data. Other unsupported extended
families require their explicit raw codec. These builders construct bytes and do
not perform I/O. Serial-write payloads contain the exact serial, new individual
address and four reserved zero bytes.

## Reviewed assignment

The Workbench **Tools → Assign individual address** dialog and six MCP
`address_assignment_*` tools expose a separate explicit plain-device workflow.
Select the planned project device first. Choose plain UDP deliberately, supply an
explicit gateway/client IA, then select the complete six-byte serial or a named,
continuously isolated setup containing exactly one physical device in programming
mode. A single responding address never proves physical uniqueness; duplicate
replies are retained and refused.

The read-only review binds selector, endpoint/client, old/new address, independent
project reservation, role/hardware evidence and observations into a fingerprint.
Any observed sender at the target blocks assignment. **No response does not prove
that an address is free.** Confirm physical continuity and the independent
reservation, then repeat the exact reviewed target before applying. Project-known
Secure targets cannot be bypassed by a plain-device declaration. Tool/P2P/IP
authentication does not grant Secure broadcast-provisioning authority; those
profiles are refused rather than downgraded.

`1.1.0` and `1.0.0` can be coupler targets with explicit hardware evidence. The
Workbench requires the project's actual `IsCoupler` fact, reference and hardware
ID. An external library/MCP caller without a matching project must explicitly
attest named manufacturer/hardware evidence; the software cannot independently
attest it. Segment couplers may also have nonzero device numbers. `1.1.255` is a
valid target; `0.0.0`, `15.15.255` and the tunnel's own IA are refused. This target
correction does not loosen the separate client/host/Secure-profile contracts.
Read-only line scans cover all 256 numbers, excluding reserved raw addresses and
the actual client. Generic legacy coupler-write restrictions remain.

Before calling the transport, the library flushes an outside-repository
`intent.json` and exclusive `attempt.json` claim. The address-specific tunnel
sender emits at most one KNXnet/IP request, without application/NetIP retry.
Normal read retries are unchanged. A medium or gateway can have its own
lower-layer behavior; the API does not claim physical exactly-once delivery.
ACK loss, cancellation and send exceptions lead to fresh bounded **read-only**
reconciliation. Serial and fresh descriptor/manufacturer/serial reads must match
the bound device. Plain identity observations are not cryptographic
authentication. No compensating write or automatic restart occurs.

Cancel, Escape, project replacement and close join the current send and
independent reconciliation before releasing the factory/gateway owner. An
unresolved state remains visible with its intent path; explicit acknowledgement
allows leaving but grants neither restoration nor success. Only a verified,
durably recorded result bound to the still-current selected project sets **real
Adr**. Previous simulation Prg/Par/Grp/Cfg flags are cleared; a changed project
receives no late flag. Real evidence is not saved through the DEMO store.

## Cold reconciliation and MCP

**Open durable intent** inspects offline. Reconcile reopens the exact displayed
fingerprint and reads only; isolated mode requires a new explicit declaration of
the same physical setup. A missing attempt marker never grants a new write.
Returning to the old address requires a new review/confirmation. Historical
outcomes alone do not restore current Adr flags.

MCP review requires `mode: plain_udp`, `gateway`, `own_address`, `new_address`,
`selection`, `target_role`, `reservation`, `confirm_plain_device` and an existing
absolute private `journal_directory`; serial/isolation and coupler evidence are
mode-specific. Review/apply/reconcile return an accepted `operationId` promptly,
so the sequential stdio host can serve `address_assignment_status` and
`address_assignment_cancel` while they run. Cancel joins that exact operation ID
and releases the gateway; completed review/apply otherwise retain the owner until
cancel, replacement or server close. Status exposes typed/redacted failures and
durable outcome. Inspect is offline. Generic `bus_write` and application download
permissions remain disabled.

The bounded strict-JSON journal and fingerprints detect accidental change and
repeated claims. They are not a signed audit log, monotonic anchor or protection
against a malicious same-user actor, filesystem restore or lost power/directory
metadata. Keep the chosen private directory available and backed up. It contains
endpoint/device evidence, no passwords or keys. A damaged/missing journal never
permits repeating a write. Project/source drift invalidates unconsumed reviews.

Acceptance here uses synthetic in-process datagrams and fake DE/EN native
windows. No installation address was changed during these checks. Physical
target/media/coupler and Secure-broadcast interoperability acceptance remains open.

Role references: [KNX topology](https://support.knx.org/hc/en-us/articles/4417351485074-Topology),
[KNX offline project check](https://support.knx.org/hc/en-us/articles/360019116959-Project-check-Offline).

Protocol references, pinned at `eccdabc72b37203cfec66313432e750a24384178`:
[management services](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/mgmt/ManagementClientImpl.java),
[broadcast transport](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/mgmt/TransportLayerImpl.java)
and [priority values](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/Priority.java).
The implementation and synthetic fixtures are authored in this repository.
