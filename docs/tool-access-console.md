# Explicit Tool-Access console profile

The existing addressed-management/M112 console can select the provisioned
[Tool-Access capability](tool-access-management.md). It uses one gateway owner,
one password-verified keyring import and one existing counter-store owner across
fresh preflight, restart-verification and independent recovery connections.
Gateway authentication and the actual AssignedIA check precede a fresh joined
Tool Sync; management is published afterwards. Other targets, group traffic and
plaintext fallback remain refused. Workbench selection is separate.

## Provisioning and private configuration

Keep the configuration, signed export, counter state and recovery data outside
Git. Filesystem links are refused. The configuration is a closed JSON object,
at most 32 KiB: every field below is required; unknown or duplicate fields fail
before network access. It contains references and declared provisioning facts,
never raw passwords, Tool keys or integrity capabilities.

| Field | Required value |
| --- | --- |
| `format` | `BusChord.ToolAccessManagement.v1` |
| `gateway`, `port` | Canonical explicit gateway IPv4 and UDP port 1..65535 |
| `host_address`, `user_id`, `own_address` | Exact signed gateway KNX IA, user 1..127 and assigned tunnel IA |
| `target` | Exact device IA, distinct from the tunnel IA |
| `keyring_path`, `keyring_project`, `keyring_password_env` | Absolute protected export reference, exact signed project label and password environment-variable **name** |
| `expected_keyring_sha256` | Mandatory SHA256 pin of the exact signed export bytes |
| `installation_scope`, `tool_key_epoch` | Stable independently provisioned counter-domain identities |
| `wire_tpdu_capacity` | Declared admitted target capacity, 25..255 bytes; never discovered from a device name |
| `ordinary_p2p_key_sha256` | Complete array of ordinary P2P key fingerprints; an explicit empty array asserts none |
| `counter_state_path` | Absolute reference to an already provisioned, ready Tool counter store |
| `counter_integrity_env` | Environment-variable **name** containing the independent 32-byte integrity capability as canonical Base64 |
| `state_trust` | Exactly `provisioner_confirmed_current_unanchored` |

This console profile supports one deliberately explicit trust decision: the
provisioning owner confirms that the supplied unanchored state is current.
Repeat the target with `--tool-current-state-confirm`. This cannot detect an
older valid backup restored between processes and supplies **no counter rollback
protection**. An anchored provider workflow is not exposed by this console.
There is no state/epoch creation, export-sequence or clock bootstrap, automatic
repair, FDSK, rekey or lost-highwater recovery. Missing, corrupt, blocked or
exhausted state fails before transport creation. Referenced secrets are acquired
lazily and owned byte/character copies are cleared after the joined job.

## Console use

Append these options to the existing [commissioning commands](commissioning.md),
using a private configuration prepared with the facts above:

```text
--management-probe=GATEWAY_IPV4 --target=TARGET_IA
--tool-access-config=ABSOLUTE_PRIVATE_CONFIG
--tool-current-state-confirm=TARGET_IA
```

Do not combine them with ordinary connection-selector options. A basic invocation
reads identity/status only. Existing expected physical identity, baseline/image,
preview fingerprint, explicit write confirmation and recovery-file gates still
apply unchanged. Offline preview acquires no password, integrity capability,
counter store, gateway reservation or network transport; it does not authenticate
a device. CLI errors contain only typed/generic failure codes, not provider
exceptions, source paths, environment names or values.

Before opening a mutating M112 job, the console budgets the entire target and
original recovery image, every fixed request/reply and Secure overhead. Cold
recovery budgets the checksum-validated complete before-image and rechecks its
exact file hash before use. The current writer/reader share a twelve-byte memory
block limit; a full block needs a 16-byte plain TPDU and 29-byte Tool TPDU. The
fixed eleven-byte load-control write also participates. A read-only identity
profile can fit 25 bytes while the full-image plan is refused. This is a complete
preflight refusal, not chunk-size adaptation or a claim of peer capacity.

## Standalone read-only MCP tool

`bus_tool_identity_read(gateway, target, tool_access_config, confirm_current_state)`
uses the same closed configuration and trust decision. It opens one independent
read-only management owner under the request/job lifetime and releases it before
replying. A gateway already reserved by another connection returns `Occupied`;
the tool does not disconnect it or replace the selected session/profile.
Results identify the explicit unanchored trust and absent rollback protection.
Other bus tools and `download_plan` reject Tool/counter references before I/O;
they do not ignore those fields or use them to select plaintext/discovery.

## Evidence and limits

The maintained `ToolAccessCliChecks` suite drives the actual parser, signed
synthetic keyring provider, durable store, authenticated synthetic gateway and
Tool Sync/data path. It checks changed parameter **and** GA/association bytes,
full fresh recovery after uncertain writes/cancellation, cold recovery without
loaded application properties, server verification, exact wire boundaries,
reference redaction, refusal before I/O, occupied ownership and joined MCP
cancellation/disposal. Existing protected-keyring and protocol suites supply the
published primitive vectors; this layer adds no new wire encoding. These software
checks do not establish physical Secure-device or manufacturer compatibility.
