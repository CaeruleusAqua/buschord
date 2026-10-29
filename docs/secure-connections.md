# Secure connection selection

Workbench connections explicitly select ordinary UDP or authenticated IP Secure
UDP. Secure mode uses a protected `.knxkeys` export and its password. The local
selection dialog verifies the signature, shows metadata and requires one exact
gateway KNX address, user and assigned tunnel address. The selected source hash
is read-only. Password input is masked and cleared when the prompt closes.
No decrypted keyring is saved. Password storage requires a separate explicit action.

The same profile is used by the read-only monitor, addressed scanner and dedicated
M112 dialog. Each operation owns one gateway reservation and credential capability.
Fresh M112 and independent recovery connections authenticate again using that
capability. Cancellation, project replacement and closing wait for actual network
cleanup before releasing credentials and the cross-process gateway reservation.
An unsuccessful Secure connection cannot silently become an ordinary connection.

The local six-byte client identity is published under a persistent initialization
lease shared by cooperating processes. Lock contention has a five-second bound;
existing malformed or linked state is refused. Initialization runs off the UI
dispatcher and is joined before cancellation releases credentials or the gateway.
Do not remove `client.serial.lock` while any application or MCP process uses it.

## Optional OS password storage

Choose the keyring file and its host/user/tunnel candidate. Public metadata and the
source hash are initially unverified; they never enable a connection. **Enter
password and verify** keeps the password source ephemeral. Complete signature and
exact source verification must succeed before a choice can be accepted.

For a project saved at a full path, **Check storage** checks OS availability without
reading an entry. **Use saved password** explicitly looks up this exact binding and
verifies the selected export. **Save new** and **Replace matching** have their own
masked confirmation prompt and verify the password, signature and file pin before
mutating storage. Saving does not silently select the stored source. **Delete saved
password** deletes only that matching entry. There is no entry enumeration, automatic
save, migration, unlock or password-source fallback.

The binding includes actual project identity and full canonical project path,
keyring path and exact source bytes, exported label and selected host/user/tunnel.
Changing those inputs requires an explicit new choice; unsaved projects retain
ephemeral input. A source change before lookup releases no password. A change or
cancellation during an admitted mutation can leave an unknown outcome; the UI
shows that state and never retries or compensates automatically.

Windows uses the current user's Credential Manager. Linux requires installed
libsecret/GIO and a configured Secret Service with encrypted transfer; unavailable
or locked storage stays visible and ephemeral input remains available. Connections,
scan, commissioning and nested password dialogs drain before the Workbench vault
owner is released. [Storage contract and provider limits](credential-vault.md).

IP Secure authenticates the gateway transport. The default profile does not
authenticate or decrypt Data Secure group values. An explicitly provisioned
[group policy](group-secure-connections.md) can be attached through the library,
configured MCP host, or the Workbench's **Data Secure groups…** selection. The
Workbench reads one private host-profile file, shows its complete pin and policy,
and binds the selected ID to the already verified keyring/password source. It
does not adopt the registry's password ENV reference. Each new unanchored-state
operation requires explicit current-state confirmation; reconnect, scan and M112
offer a short renewal for the same immutable selection. All groups with signed keys in the imported export are
withheld from plaintext read/write/response, including groups outside the selected
interface's assignments. Incoming clear values for those groups and unverified
S-A_Data are withheld. A separately provisioned durable Data Secure context is
required for those values; historical exported sequences cannot create it.

## MCP and command line

`bus_connect`, standalone `bus_scan`/`bus_device_info` and `download_plan` accept
`mode: "ip_secure_udp"` with an explicit gateway IPv4 and these selection fields:

| Field | Meaning |
| --- | --- |
| `own_address` | Exact tunnel KNX individual address from the keyring |
| `keyring_path` | Absolute private protected export path |
| `keyring_password_env` | Name of the process environment variable containing its password |
| `keyring_project` | Exact signed project label; not a unique project ID |
| `host_address` | Gateway KNX individual address, separate from its IPv4 |
| `user_id` | Exact Secure user ID, 1–127 |
| `expected_keyring_sha256` | Optional exact source-byte fingerprint |

`port` defaults to 3671; `interface` may select a local IPv4. Password values and
raw keys are refused as connection arguments. Results expose typed failures and
gateway authentication status without private keyring paths, passwords or keys.
Selection and its source pin survive disconnect or failed authentication; an
implicit retry cannot switch to discovery or plaintext. Changing mode/user while
connected is refused. Active diagnostics borrow the actual connection; a remotely
closed owner is drained before standalone diagnostics acquire a new reservation.
Project replacement drains and clears its connection selection.

The explicit M112 CLI supports corresponding `--transport ip_secure_udp`,
`--gateway-port`, `--own-address`, `--keyring-path`, `--keyring-password-env`,
`--keyring-project`, `--host-address`, `--user-id` and
`--expected-keyring-sha256` options. Offline review acquires no credentials and
sends no network traffic. Existing physical identity, plan confirmation and
private recovery requirements still apply.

The [explicit Tool-Access console profile](tool-access-console.md) adds target-bound
Tool Sync over the authenticated gateway using one signed import and an existing
provisioned counter store. It has separate private configuration and explicit
current-unanchored-state confirmation. `bus_tool_identity_read` exposes only a
standalone read-only use of that profile. It requires a free gateway reservation;
other bus tools refuse Tool/counter fields before I/O.

## Acceptance limits

Independent synthetic gateways check the protocol and selection, authentication
failure, assigned-address mismatch, exact source pin, downgrade refusal, shared
ownership and joined cleanup. Native German/English tests check the real selection
and password controls. The combined maintained suites provide integration checks;
they do not establish interoperability with a physical Secure gateway or a Secure
application download. Ordinary group security and secure first commissioning
remain separate profiles/work. Tool-Access console software
tests do not supply a physical-device interoperability claim.

See [IP Secure protocol](secure-session.md), [protected exports](protected-keyrings.md)
and [Data Secure counters](data-secure.md).
