# Security P3 follow-ups (branch `agents/security/p3-rest`)

Companion to `analysis/security-final.md`. Base: `fd0729d`. Covers the P3 findings that were not
fixed in this branch because they touch files owned by other active agents, plus the fixes that
landed here.

## SEC-4 — DCA/project-XML read-only fallback: follow-up (not fixed here)

**Why not fixed:** the minimal fix lives in `src/Knx.App/MainWindowViewModel.cs`
(`LoadParametersFor`, `ParameterReadOnlyReason`), which the parameter-open agent owns. No shared
Core contract change is required — the application program is already readable from the project
archive through the public `Knxproj.LoadApplicationProgram`, so this is an App-layer change only.

**Gap (unchanged):** `ParameterReadOnlyReason` derives the reason solely from the catalogue
snapshot. With a cold library or without the matching catalogue, a project archive that embeds a
DCA/plug-in application program gets `null` and the parameter pane stays editable.

**Minimal fix to apply:**

1. In `ParameterReadOnlyReason`, keep the catalogue check as the primary source; when it returns
   `CatalogBlockReason.None`, fall back to the application program embedded in the archive:
   `Knxproj.LoadApplicationProgram(ProjectPath, device.Hardware2ProgramRefId ?? device.ProductRefId)`.
2. Derive the block from that XML with the same predicate as the catalogue path
   (`CatalogApplicationProgram.RequiresDca` / `CatalogBrowser.Blocked`):
   - `ApplicationProgram@Broken` (`"1"`/`true`) → `CatalogBlockReason.Broken`,
   - `ApplicationProgram@DownloadInfoIncomplete` → `DownloadInfoIncomplete`,
   - any `Static/Extension` attribute with a non-empty value, or child element, named
     `EtsUiPlugin`, `EtsDownloadPlugin`, `EtsDataHandler` or `EtsDataHandlerCapabilities` →
     `DcaUnsupported`.
   - The part's root can be `<ApplicationProgram>` or a wrapper (e.g. `<KNX><ApplicationProgram>`),
     so search the document for the element instead of assuming the root.
3. Cache the result per `(ProjectPath, refId, archive last-write ticks, length)` so a device
   selection does not re-read the archive and a replaced project file is re-read.
4. Apply the same reason at the write boundary (`ApplyParameterValues`) so a host that skips
   `ParameterReadOnlyReason` cannot persist DCA values.

**Regression test to add (fails before the fix):** build a synthetic `.knxproj` containing
`M-DCA/M-DCA_A-1.xml` with an `EtsUiPlugin` extension and `M-STA/M-STA_A-1.xml` without one;
assert `ParameterReadOnlyReason` is non-null (contains "DCA") for the first device and null for
the second with an empty catalogue library, and that `ApplyParameterValues` refuses the DCA device.
`tests/Knx.WorkbenchUiState.Tests/Program.cs` already contains the catalogue-side DCA test and is
the natural home.

## SEC-5 — `CatalogUse.Download` / legacy raw download API

Verified unreachable and already hard-gated at the only bus-write path:

- `git grep` finds no caller of `MainWindowViewModel.DownloadAsync(string)` (no UI/MCP wiring),
  no caller of `CatalogBrowser.Blocked(..., CatalogUse.Download)`, and `WorkbenchWindow` builds
  `WorkbenchSessions` without a real executor.
- `KnxBus.WritesEnabled => false`; `KnxBus.DownloadAsync` throws `NotSupportedException` before
  opening a transport, and `GroupValueWriteAsync` does the same. Covered by
  `tests/Knx.Bus.Tests` ("real download is safety-gated", "group writes are read-only
  policy-gated", "write gate is disabled by default").

**Remaining risk (latent, follow-up):** if a future change enables `WritesEnabled` and wires
`DownloadAsync`, the legacy raw-image path would flash `imagePath` to `0x0060` with no
catalogue/DCA/broken check and no confirmation. When wiring a download entry point, gate it with
`CatalogBrowser.Blocked(program, CatalogUse.Download)` plus the SEC-4 archive fallback and an
explicit user confirmation, or delete `MainWindowViewModel.DownloadAsync`/`FileProgram` (no
caller today).

## SEC-8 — catalogue import: what landed here

- Import already copied temp+move at the base commit; this branch adds the regression test
  ("Import: fehlgeschlagener Re-Import lässt Bibliothekseintrag unverändert", Windows-only lock
  injection like the existing archive test, plus "keine Temp-Datei bleibt zurück").
- `CatalogImportReport` now carries `HasSignatureFile` (a `*.signature` entry exists in the
  container or the extracted inner archive; presence only, never verified — verification needs
  the ETS trust chain) and `ReplacedExisting` (a same-identity library entry was replaced).
  The catalogue pane can surface "unsigned catalogue" / "replaced an existing entry" with these
  fields without further Core changes.
- Still open, deliberately not done: verifying the signature, keeping a `.bak` of the replaced
  entry, or asking before replacement. Those need a policy decision (and UI for the prompt).

## SEC-9 — plaintext customer-project backups: assessment

Backups remain byte-identical, unencrypted copies of the customer project under
`%LOCALAPPDATA%\BusChord\Backups` (up to `DefaultRetention` per identity, pruned only on the next
create, source path recorded in the manifest). This is data handling, not a vulnerability: same
user, default profile ACLs. Encryption is not cheap here: it needs a passphrase UI, a KDF/AES-GCM
format, manifest fields and a restore prompt — the backup dialog is owned by the shell, and the
project itself may already be WinZip-AES encrypted (those cannot even be backed up because the
validation load has no password). No dependency-free shortcut is worth a dead API.

**What this branch adds:** the plaintext/retention caveat is now part of the
`ProjectBackup` documentation, and `ProjectBackup.DefaultRootPath` is public so the backup dialog
can name the location and retention in a warning. Wiring that warning (and optionally a
"delete all backups" action) is a UI follow-up.

## Overlaps not touched

- SEC-6/SEC-7 (catalogue cache: invalidation, trusted paths, cache/archive bounds): cache files
  are owned by the catalog-warm/catalog-index agents. At `fd0729d` the content hash, size caps,
  backing-name validation and archive cleanup on `Remove` are already in place; a remaining point
  to re-review is that `FromIndex` adopts `_partByName` locations from the cache document without
  revalidating them against the archive/library (only reachable through a poisoned cache file).
- SEC-3 (CSV) and SEC-10 (fixtures) were already fixed on `development` before this branch.
