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

The complete address-assignment workflow remains open: physical-device/target
review, occupancy observations, explicit confirmation, durable intent, verified
write/recovery and Workbench/MCP integration. This wire/discovery layer enables no
new Workbench or MCP writes. It does not attest Secure addressing or hardware
interoperability, and no real installation address was changed during its tests.

Protocol references, pinned at `eccdabc72b37203cfec66313432e750a24384178`:
[management services](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/mgmt/ManagementClientImpl.java),
[broadcast transport](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/mgmt/TransportLayerImpl.java)
and [priority values](https://github.com/calimero-project/calimero-core/blob/eccdabc72b37203cfec66313432e750a24384178/src/io/calimero/Priority.java).
The implementation and synthetic fixtures are authored in this repository.
