# Explicit ordinary Data Secure group connections

This optional profile joins the [group codec/counters](data-secure.md) to the shared
authenticated factory, `KnxBus` monitor and read-only MCP. It does not provision
devices, initialize counters or synchronize an installation. The Workbench selector
is unchanged and does not enable this policy automatically.

`WithGroupSecurity` requires IP Secure UDP, an exact keyring source pin and complete
host/user/tunnel selection. Each selected GA has an explicit protection mode and
permitted sender subset of that signed interface's assignments. All signed group
keys remain known: other protected GA cannot become plain. Unknown Secure frames,
plaintext for selected GA, wrong senders, weaker protection and replay release no
values. Plain groups/individual peers need separate exact allowlists. Individual
peers remain ordinary/plain, never inferred Tool/P2P security. Existing MCP and
`KnxBus` write restrictions still apply.

Physical installation scope, ordinary epoch and local IA identify existing counter
state. They must survive project copies, reconnects and group-selection changes;
ordinary P2P must share the same identity/store. Tool epochs are separate. Selected
keys are checked against signed Tool/backbone keys, derived gateway keys and the
explicit external individual-key fingerprint inventory. Completeness for keys
absent from the export is a provisioning-owner attestation, not discoverable fact.

Before transport creation the factory checks all existing counter pages, readiness,
sender coverage, local highwater consistency and exhaustion. An independent
monotonic anchor is the normal library contract. The separately named
`FromTrustedUnanchoredCurrentState` consciously trusts the CURRENT file; it cannot
detect restoration of an older valid complete file. Missing/corrupt/restore-blocked
state never bootstraps from an export or clock. One import retains required key and
counter-integrity copies across fresh opens; exact assigned IA is enforced. Joined
cleanup releases keys and the gateway lease only after admitted operations drain.

`BusTelegram.DataSecurity` distinguishes unverified, authenticated incoming and
authenticated/confidential incoming values. The exact admitted indication is marked
only after MAC/policy/replay checks and durable receive-floor commit. IP gateway
authentication is separate. An own `L_Data.con` never proves a remote application
peer. Recorder provenance is live/in-memory only; imports cannot assert it and file
exports do not carry a live authentication guarantee.

## Explicit MCP host registry

The standard stdio host accepts an explicit private registry and confirmation:

```text
buschord-mcp --group-security-profiles=/absolute/private/group-profiles.json --confirm-current-group-state=lighting
```

Registry/keyring/state paths must be outside Git and have no linked components.
The registry is read once, at most 64 KiB/32 profiles/depth six; unknown, duplicate
or missing fields are rejected. Restart to consciously adopt edits. No registry is
discovered. Without one, `group_security_profile` is refused before network access.
All fields below are required; placeholders cannot execute until independent
provisioning supplies current state and exact source selection. No secret VALUES
belong in the file:

```json
{
  "format": "BusChord.GroupSecurityHost.v1",
  "profiles": [{
    "id": "lighting",
    "gateway": "192.0.2.10",
    "port": 3671,
    "own_address": "1.1.240",
    "host_address": "1.1.0",
    "user_id": 2,
    "keyring_path": "/absolute/private/project.knxkeys",
    "keyring_project": "Exact signed label",
    "keyring_password_env": "KNX_KEYRING_PASSWORD",
    "expected_keyring_sha256": "REPLACE_WITH_EXACT_64_HEX_SOURCE_PIN",
    "installation_scope": "stable-provisioned-physical-installation",
    "ordinary_key_epoch": "existing-ordinary-key-epoch",
    "counter_state_path": "/absolute/private/current-ordinary.state",
    "counter_integrity_env": "KNX_COUNTER_INTEGRITY",
    "state_trust": "provisioner_confirmed_current_unanchored",
    "individual_key_sha256": [],
    "groups": [{ "address": "1/1/1", "protection": "auth_conf", "senders": ["1.1.1"] }],
    "plain_groups": [],
    "plain_peers": []
  }]
}
```

Protection is exactly `authentication` or `auth_conf`; GA use canonical three-level
notation. `individual_key_sha256` attests the complete known external Tool/P2P
inventory; empty means none. Integrity ENV is canonical Base64 for exactly 32 bytes.
ENV values are process capabilities and are never command-line/MCP arguments.

Unanchored state requires both the named `state_trust` and matching startup
confirmation ID; there is no default confirmation. An embedded host can choose
`independent_anchor` with an explicitly supplied `IDataSecureMonotonicAnchor`.
The CLI ships no production anchor service and refuses that choice without one.
The registry owner joins integrity reads and clears owned buffers after all MCP
connection jobs drain. Custom hosts must preserve that same ownership order.

`bus_connect` uses existing complete IP Secure fields plus the exact source pin and
`group_security_profile: "lighting"`. The registry's endpoint/keyring/user/tunnel
must match exactly. `bus_read` and live recorder results return `dataSecurity`.
Reconnect retains the chosen policy. Raw group/integrity keys, counter paths/floors
and bootstrap arguments are refused; no failed reference enables plaintext.

## Evidence and limits

Synthetic gateways cover authentication, protected reads, anchored floor-before-
value, policy/MAC/downgrade/replay rejection, retained counters on fresh opens,
exact IA, read-only gates and joined cancellation/close. Host tests cover bounded
configuration, explicit trust, exact selector, redaction, capability ownership,
standard stdio startup and MCP read/monitor provenance. No physical Secure gateway,
device, general provisioning or secure-first-commissioning acceptance is claimed.
