# Targeted commissioning diagnostics

Normal Workbench and MCP programming actions remain simulations or read-only.
The separate console probe supports an explicitly selected MDT M112 programming-mode
test. It does not download parameters, replace an application, assign an address,
restart a device or change group values.

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

## Acceptance and limits

On 2026-10-29, one explicitly released MDT M112 device passed descriptor, identity,
property and memory reads, programming-mode activation/readback and restoration.
A separate subsequent connection read the original status and application identifier;
the same device subsequently answered a read of its linked temperature group object.
The bus suite passes 524 checks, including sequence wrap, duplicate/foreign replies,
timeout, NAK, cancellation, uncertain writes and restoration identity mismatch.

This is a single-device management/write test, not acceptance of application downloads.
The selected legacy application contains 37 storage unions rejected by the current
image builder. Union population, a complete matching image, load-state commands,
activation, finalization and application rollback must be implemented and validated
before its full download. No unload, factory reset or application download was performed.
