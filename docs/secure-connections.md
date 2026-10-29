# Secure connection selection

Workbench connections explicitly select ordinary UDP or authenticated IP Secure
UDP. Secure mode uses a protected `.knxkeys` export and its password. The local
selection dialog verifies the signature, shows metadata and requires one exact
gateway KNX address, user and assigned tunnel address. The selected source hash
is read-only. Password input is masked and cleared when the prompt closes.
No decrypted keyring or password is saved by this flow.

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

IP Secure authenticates the gateway transport. It does not authenticate or decrypt
Data Secure group values. All groups with signed keys in the imported export are
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

## Acceptance limits

Independent synthetic gateways check the protocol and selection, authentication
failure, assigned-address mismatch, exact source pin, downgrade refusal, shared
ownership and joined cleanup. Native German/English tests check the real selection
and password controls. The combined maintained suites provide integration checks;
they do not establish interoperability with a physical Secure gateway or a Secure
application download. Ordinary group security, OS password storage, provisioned
Tool Access/Sync and secure first commissioning remain separate profiles/work.

See [IP Secure protocol](secure-session.md), [protected exports](protected-keyrings.md)
and [Data Secure counters](data-secure.md).
