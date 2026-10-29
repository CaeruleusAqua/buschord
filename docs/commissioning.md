# Targeted commissioning diagnostics

Normal Workbench and MCP programming actions remain simulations or read-only.
The separate console probe supports an explicitly selected M112 programming-mode
test and a separately confirmed, narrow same-configuration application reload for
mask `0x0701`. These modes do not enable general Workbench or MCP writes, address
assignment, factory reset or group-value writes.

## Read identity

After building the MCP project, run the console entry point with the exact gateway
and device. These documentation addresses are examples:

```powershell
dotnet src/Knx.Mcp/bin/Release/net10.0/buschord-mcp.dll `
  --management-probe=192.0.2.10 --target=1.1.20 `
  --identity-output=C:\private-tests\expected-identity.json
```

The probe reads the descriptor, manufacturer, serial and application identifier.
For mask `0x0701`, it also reads the status byte. Identity output uses create-new
semantics. Compare the application identifier with the project before a write test;
an identity captured from a device alone does not establish that it is the intended device.
Keep identity files, journals, projects and credentials outside the checkout.

## Programming-mode cycle

Only run against a device explicitly released for this test. Stop concurrent
commissioning operations first. The selected test directory must be writable.

```powershell
dotnet src/Knx.Mcp/bin/Release/net10.0/buschord-mcp.dll `
  --management-probe=192.0.2.10 --target=1.1.20 `
  --mode-cycle-confirm=1.1.20 `
  --expected-identity=C:\private-tests\expected-identity.json `
  --journal-directory=C:\private-tests\journals
```

This profile requires mask `0x0701`, manufacturer `0083`, a matching serial and
application, valid status parity and programming mode initially off. It reads
device-control verification mode and rechecks the original status before writing.
The journal is flushed to disk before the first status write. Only programming-mode
bit 0 and parity bit 7 of status address `0x0060` are changed; other flags are preserved.
This follows the public [Calimero management procedure](https://github.com/calimero-project/calimero-core/blob/master/src/io/calimero/mgmt/ManagementProceduresImpl.java).

Every attempted write triggers restoration over a fresh connection, with independent
identity and readback checks. Caller cancellation does not cancel restoration.
Success requires both `ActivatedAndVerified` and `RestoredAndVerified`, with no error.
If restoration fails, retain the journal and resolve the reported device state
before another commissioning operation. Process termination, power loss or loss of
network connectivity can prevent restoration; the journal itself is not an automatic
startup recovery service.

Transport requests use independent four-bit send/receive counters, acknowledge
duplicates without applying them twice, validate source/destination and service-specific
response fields, and invalidate the session after timeout, NAK or disconnect.
Writes are not automatically retried. Standard memory counts and full property APCIs
are checked against the public [Calimero management client](https://github.com/calimero-project/calimero-core/blob/master/src/io/calimero/mgmt/ManagementClientImpl.java).

## M112 same-configuration reload

This is a constrained full-load sequence for an application already matching the
selected device. Every known persistent bit must match before unload begins.
Unknown persistent bits are retained from that same device; the low six type bits
of unlinked communication-object descriptors are also retained. This command does
not apply changed parameters or migrate an application.

Use an existing private `.knxproj` copy and identity file. Supply absolute paths;
project, identity, recovery and journal locations must be outside every Git checkout
and cannot traverse filesystem links. Choose a journal directory whose permissions
restrict access to the operator: the before-image contains device configuration.
The probe does not create or overwrite the project copy.

```powershell
dotnet src/Knx.Mcp/bin/Release/net10.0/buschord-mcp.dll `
  --management-probe=192.0.2.10 --target=1.1.20 `
  --m112-full-confirm=1.1.20 `
  --project=C:\private-tests\project-copy.knxproj `
  --expected-identity=C:\private-tests\expected-identity.json `
  --journal-directory=C:\private-tests\journals `
  --m112-preview
```

`--m112-preview` builds the supported image offline and prints its fingerprint,
segment boundaries and control mappings. It opens no tunnel and performs no device
preflight or writes. The gateway argument is still required by the console entry
point. Remove only `--m112-preview` to execute the explicitly confirmed load after
review and device authorization. The target text, confirmation, expected identity
and unique project device must all agree exactly; the source application identifier
must also match. The read-only, mode-cycle, full-load and recovery options cannot
be combined. Missing confirmation never falls back to a write.

For a protected project, interactive use requests the password with echo disabled.
Alternatively, add `--project-password-env=BUSCHORD_PROJECT_PASSWORD`, naming an
already populated process environment variable. This option takes the variable
**name**, never the password value. Redirected input requires the environment option.
The probe does not save or log the password and grants no manufacturer-script
capability. All required application and mask metadata must be available to the
offline builder.

The declared memory-mapped control route is `0x0104`, with eleven-byte controls and
load states at `0xB6EA`–`0xB6EC`; this profile requires `PropertyMappedLsms=0`.
It captures configuration, flushes a before-image and journal before the first
destructive control, then unloads and reloads the three declared load state machines.
Only declared EEPROM segment payloads are written and read back. RAM and stack
segments are allocated from the source recipe, with no RAM/stack payload writes.
A basic restart is sent once on the connected transport without requiring a device
ACK before it reboots. After a cooldown, a fresh connection must verify the
application, persistent configuration, PEI, table pointers and original device status.
Sending the request alone does not establish a successful restart.

The caller-controlled full-load timeout is five minutes. Once a destructive
operation may have happened, failure or cancellation triggers restoration through
a fresh connection with its own five-minute timeout. Ctrl+C cannot cancel that
restoration. A successful load requires `DownloadedAndVerified=true` and no `Error`.
After a failed load, `OriginalConfigurationRestoredAndVerified` reports whether the
captured configuration was restored and verified; successful rollback still leaves
the load command unsuccessful.

## Resume captured-configuration recovery

Retain the exact `before-image.json` path printed in `RecoveryPath`. A separate
explicit command can resume recovery after process or connection failure:

```powershell
dotnet src/Knx.Mcp/bin/Release/net10.0/buschord-mcp.dll `
  --management-probe=192.0.2.10 --target=1.1.20 `
  --m112-recover=C:\private-tests\journals\m112-RUN\before-image.json `
  --m112-recovery-confirm=1.1.20 `
  --expected-identity=C:\private-tests\expected-identity.json
```

Recovery verifies the before-image checksum and exact expected physical/application
identity before using its captured source load recipe. It does not need the project
or project password. Once recovery starts, its five-minute timeout is independent
of caller cancellation. Success requires
`OriginalConfigurationRestoredAndVerified=true` with no `Error`.

Both load and recovery report
`RecoveryScope="CapturedConfigurationWithSourceLoadRecipe"`. This covers captured
persistent configuration reloaded using the declared source allocations. It does
not guarantee restoration of invisible allocation metadata, volatile RAM/stack
contents or other unobserved device internals. Process termination, power loss and
loss of connectivity can still prevent restoration. The journal is not a background
startup recovery service.

## Acceptance and limits

On 2026-10-29, one explicitly released MDT M112 device passed descriptor, identity,
property and memory reads, programming-mode activation/readback and restoration.
A separate subsequent connection read the original status and application identifier;
the same device subsequently answered a read of its linked temperature group object.
The original management suite passed 524 checks, including sequence wrap,
duplicate/foreign replies, timeout, NAK, cancellation, uncertain writes and
restoration identity mismatch. Additional offline checks exercise bounded memory
and property writes, resulting control-state values and basic restart transmission/disconnect.

The first full device trial wrote and verified all three persistent segments and
completed the declared load controls. Its initial restart implementation incorrectly
required a device ACK and therefore initiated recovery. Recovery wrote and verified
the captured original configuration. An independent subsequent connection confirmed
the original persistent hashes, application, running/load states, PEI and table pointers;
the target also answered its temperature group read. A subsequent complete run with
the corrected restart path passed download, EEPROM verification and fresh-session
application, load/run-state, PEI, table-pointer and status checks with no error.
The target then answered another temperature group read. This is live acceptance
of this same-configuration profile on one device; general application downloads,
other device profiles and broader hardware acceptance remain outside that evidence.
