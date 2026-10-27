# Security Policy

BusChord talks to real KNX installations. A bug here can write to devices,
corrupt a project archive, or leak keys. Please report vulnerabilities privately
and give us time to fix them before you publish.

## Supported versions

BusChord is pre-release software. Only the newest state of `development` and the
latest published release receive security fixes; older snapshots are not
maintained.

| Version | Supported |
|---|---|
| Latest release | Yes |
| `development` (tip) | Fixes land here first |
| Anything older | No |

## Private reporting

Use [GitHub Security Advisories](https://github.com/CaeruleusAqua/buschord/security/advisories/new)
("Report a vulnerability" on the repository's Security tab). Do **not** open a
public issue, discussion, or pull request for a suspected vulnerability, and do
not attach the exploit, keys, or customer project files to a public report.

Please include:

- Affected version or commit.
- What the flaw allows, and the impact you expect.
- Reproduction steps, ideally without real installation data.
- Any suggested fix or mitigation.

We aim to acknowledge a report within 7 days, agree on a disclosure date with
you, and credit you in the advisory unless you prefer otherwise. Please keep the
report private until an advisory is published.

## Scope

In scope:

- **Bus write and download.** Crafted input that makes BusChord write to the
  wrong group address, write outside an intended operation, skip a safety
  confirm, or issue a download/management sequence that can brick a device.
- **Cryptography.** KNX Data Security key handling, keyring storage, secure
  telegram construction, weak randomness, key or plaintext leakage into logs,
  exceptions, crash dumps, or files.
- **Archive and catalogue parsing.** Memory exhaustion, uncontrolled recursion,
  path traversal or ZIP-slip on `.knxproj` and `.knxprod`, unsafe temporary file
  handling, and anything that lets a hostile project file escape its output directory or
  execute code.
- **Privacy.** A report, log, export, or screenshot that leaks installation data
  the user did not ask to export.

Typically out of scope: findings that need a locally modified binary or
debugger, social engineering, denial of service against your own machine only,
and missing hardening features with no demonstrated impact.

## Experimental features

These areas are experimental and known to be incomplete. Bugs in them are still
welcome as normal issues, but do not rely on them for production work and do not
run them against an installation you cannot restore:

- **Tunnelling** beyond the verified read-only path (connect, one group read,
  clean disconnect).
- **Device download** and management sequences.
- **KNX Data Security / secure tunnelling.** The crypto foundation exists but is
  not wired into the tunnel yet.
- **Manufacturer parameter execution.** Supported JavaScript runs only through
  the explicit `Knx.Scripting` worker capability. Its process/interpreter limits
  are not an OS container or restricted-account guarantee. Unsupported scripting,
  structural scopes and DCA / plugin execution remain blocked; see
  [current scope](docs/status.md#manufacturer-and-parameter-profiles).

## No warranty for live bus actions

BusChord is provided without warranty (see [LICENSE](LICENSE)). Writing to a bus
or downloading a device is done entirely at your own risk: verify the target
line, addresses, and memory layout, keep a project backup, and use a physically
segregated test line whenever you can. Never test write or download paths against
a customer installation.

The test suites must not perform live device writes. A pull request that adds
one will be rejected.
