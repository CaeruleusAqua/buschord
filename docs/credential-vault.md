# OS vault contract

`VaultSecurePasswordSource` implements the existing `ISecurePasswordSource` and only
reads. `SecureCredentialVault.SaveAsync` (`CreateNew` or `ReplaceMatching`) and
`DeleteAsync` are separate, explicit user decisions. A read/import never saves or
migrates a password, selects an alternative source, unlocks a vault, or displays an
OS prompt. The Workbench exposes these as separate actions; [workflow](secure-connections.md#optional-os-password-storage).

`SecureCredentialBinding.Create` requires an explicit stable project scope and a
pinned `SecureKeyringReference`. Its versioned SHA-256 identity covers the scope,
canonical full path, expected project label and whole-source fingerprint, host,
user and assigned tunnel selector. A project/export/path/selector change selects a
different credential; it needs a new explicit save. The identity is not an
authentication boundary against other processes running as the same OS user.

The binary OS blob embeds the binding digest and strict UTF-8 length (1..1024 UTF-8
bytes; longer inputs fail explicitly before any write). Passwords
enter as borrowed character memory and return in new caller-owned `char[]` buffers;
callers must clear the returned array. The wrapper snapshots save input before its
first await and clears all its temporary blobs on success, rejection, cancellation
and disposal. Native secret buffers use the provider's matching release routines.
No password becomes a command argument, profile JSON, log message, inner exception
or application plaintext file. Vault encoding creates no immutable password strings.
The masked UI control necessarily uses framework-managed text, clears it on close
and returns an independently owned character buffer; it cannot promise to wipe
every Avalonia/.NET-internal text allocation.

Windows uses Generic credentials with `CRED_PERSIST_LOCAL_MACHINE` (the current
user on this computer). Exact target names and application markers are verified
before returning/replacing/deleting. Capability uses `CredGetSessionTypes`, never
credential enumeration. Linux uses installed libsecret/GIO and an existing
unlocked default collection; each operation owns its transfer session and requires
the Secret Service DH/AES session algorithm. Exact three-attribute searches never
load a collection's Items property. Direct GIO CreateItem/SetSecret/Delete calls
inspect prompt results; they never invoke Prompt/Unlock. A prompt is dismissed
best effort and the operation fails. Missing libraries/service/collection, a locked
vault or a provider without encrypted transfer fails explicitly. Provider storage,
at-rest encryption and user authentication remain OS/provider policy; this client
cannot attest those properties or protect a secret from an equally privileged
process or administrator.

Opening a normal desktop Secret Service proxy may activate its configured service
through D-Bus; no general no-autoactivation guarantee is claimed. The isolated
acceptance bus disables all service activation directories, so its missing-provider
test cannot start any other provider. Neither path authorizes an unlock or prompt.

GNOME Keyring returns MIME `text/plain` even for an unchanged binary secret value
([its writer](https://github.com/GNOME/gnome-keyring/blob/main/daemon/dbus/gkd-secret-secret.c)).
That known return header and `application/octet-stream` are accepted, while binary
length, exact attributes and the bound envelope remain required. This does not
enable plaintext DBus transfer: the negotiated encrypted session is still mandatory.

Cooperating application processes serialize a single bound target with a named
mutex. The OS APIs do not expose a compare-and-swap transaction against arbitrary
other current-user clients; no such guarantee is claimed. Duplicate Linux matches
are refused. Cancellation before a queued native operation prevents mutation;
cancellation or transport loss during a mutation can leave the outcome unknown.
No automatic compensation or retry can safely infer whether a save/delete happened.
The caller receives a fixed `MutationOutcomeUnknown` code and may request another
explicit read. Disposal cancels and joins all admitted operations, even when a
cancellation callback throws.

Native acceptance only creates an internally generated fresh
`BusChord.Acceptance.<GUID>` scope. It never discovers existing credentials or
imports user paths/configuration. Capability-only and synthetic tests are distinct
from a successful native create/read/replace/delete roundtrip.

## Primary declarations

- [Windows CREDENTIALW / persistence](https://learn.microsoft.com/windows/win32/api/wincred/ns-wincred-credentialw)
- [Windows CredGetSessionTypes](https://learn.microsoft.com/windows/win32/api/wincred/nf-wincred-credgetsessiontypes)
- [Secret Service protocol](https://specifications.freedesktop.org/secret-service/latest/)
- [libsecret private owned service](https://gnome.pages.gitlab.gnome.org/libsecret/type_func.Service.open_sync.html)
- [Exact path search](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.search_for_dbus_paths_sync.html)
- [Owned secret value](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.get_secret_for_dbus_path_sync.html)
- [Encoded transfer secret](https://gnome.pages.gitlab.gnome.org/libsecret/method.Service.encode_dbus_secret.html)
- [GIO proxy flags](https://docs.gtk.org/gio/flags.DBusProxyFlags.html)
- [libsecret headers](https://github.com/GNOME/libsecret/tree/main/libsecret)
