# Reviewed real M112 queue

The Programming panel has separate **Simulation** and **Real M112 plans** modes.
Real jobs use the same reviewed M112 compiler, physical identity checks, serial
management/download and independent recovery path as the dedicated dialog. This
is the admitted same-application, same-memory-layout `0x0701` profile; it does not
provide first commissioning, address/network assignment or general manufacturer
compatibility. Other devices are individually blocked at offline preparation.

## Operator flow

1. Save the desired project as a private copy outside Git. Opening the real queue
   reloads that source without scripts and checks its native model against the
   open project. A protected project uses the existing private password prompt.
2. Explicitly choose the Plain UDP profile, gateway and requested tunnel address.
   Choose a private baseline `.knxproj` and an existing private journal directory.
   Enqueue each desired device. Construction, source review and enqueueing do not
   acquire credentials, connect to a gateway or write a device.
3. Select each job and explicitly read its device identity. Compare the displayed
   identity with the intended device. Separately confirm physical identity and
   plan, type the exact target address and confirm that job. Reading again clears
   these approvals. Every job retains its own frozen source/profile bindings.
4. Review the pending target list and complete batch fingerprint, then separately
   confirm and start the batch. All reviewed sources are checked before the first
   job; the existing per-job checks run again before destructive controls.

A reserved gateway returns an occupied status; the queue never disconnects or
replaces an existing Workbench connection. Close the queue and deliberately
disconnect that owner first when using the same endpoint. IP Secure/Tool profiles
are not exposed by this first queue mode and are never silently mapped to Plain.

Only successful independent verification followed by matching current model and
source bindings records Prg/Par/Grp flags. Adr/Cfg and prior simulation flags are
not promoted. Drift after a device verify records a distinct result and stops
following jobs without marking the changed project as programmed. The legacy
global writer/download permissions remain disabled.

## Stop, recovery and ownership

Cancel, Escape, window close, project replacement and application close stop later
jobs and join the active job, including its independent restoration attempt. Each
job's existing gateway operation retains the lease and any owned connection state
over fresh verification/recovery opens. Cleanup precedes release of application
credential/vault owners. There is no automatic retry or hidden compensation.

Core before-images and a flushed batch audit live in the selected outside-Git
journal. A failed or unavailable recovery remains an explicit unknown hardware
state with no success flags. Review the captured before-image to resume through
the existing physical-identity/file-fingerprint gates. Missing or corrupt evidence
does not authorize a write. After I/O drains, a separate warning acknowledgement
allows leaving with unresolved recovery even if the evidence is unavailable;
this does not mark the device restored. The existing recovery-only M112 dialog can
later resume a valid before-image without requiring an open project or loaded app.

## Historical verification receipts

The DEMO comparison format remains DEMO-only. Separate bounded private M112
receipts bind project identity, exact saved path/bytes, native serialized model,
configuration signature, reviewed plan/baseline/batch, physical identity and
checksum-validated Core before-image. Their bounded Core journal-directory digest
also rejects a later recovery attempt without relying on filenames or timestamps.
Opening the real mode only reads history; it never creates/re-writes DEMO facts,
acquires a bus owner or gains execution authority.

The model/source fingerprint also binds the immutable archived application XML
(including code and initialization), hardware/factory XML and master resource XML
read by the admitted M112 compiler. Replacing only those archive entries cannot
masquerade as the same loaded project. Native project formatting/null
normalization is retained. The source inventory is bounded to 10,000 devices,
4,096 distinct application references and 128 MiB of canonical XML.

Receipts describe past local verification, not current device health. Missing,
corrupt, changed or mismatched sources restore no flags. Before another queued
history read, unchanged receipt-derived flags are withdrawn. New live attempts,
successes and explicit flag resets retain precedence even after the system clock
has moved backwards; receipt timestamps cannot overwrite them. Before another queued
attempt the old receipt is durably invalidated; an explicit original restoration
clears desired flags. Metadata failure after a device verify has a separate
unrecorded result and stops later jobs. The Core recovery path remains independent
of receipt-source/model freshness.

Reopening revalidates history. Before source or receipt reads, only still-unchanged
states previously projected from receipts are withdrawn; failed/cancelled checks
leave no stale receipt flags. Newer live attempts, failures or explicit flag resets
are retained by exact state ownership and cannot be replaced by older history.

Receipt files are limited to 64 KiB. Journal proof inspects at most 32 top-level
entries and 16 MiB of file content, without recursion. Larger/unavailable proof
restores no historical flags. User credentials and image payloads are not stored
in receipts; full before-images remain private Core journals.

## Software evidence

Maintained session, persistence and native fake tests cover serial multi-device
jobs, separate confirmations, source drift/refusals, cancellation/drain, throwing
callbacks, unknown-state exit and cold resume. A separate integration helper uses
the actual compiler/plan/download/receipt path and the existing byte-exact
management fake, including different parameter/group/association EEPROM data and
full original restoration after an uncertain write. No physical bus, vault
provider or manufacturer acceptance follows from these software tests.
