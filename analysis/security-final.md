# Security and data-handling review — Workbench and catalogue cache

Base: `5183a0fc456acbe3a475ab221b123068f0ab07c5` (`development`, verified).
Branch/worktree: `agents/final/security` in `C:\projects\ai_work\agent-wt\final-security`.
Mode: read-only review. Only this file was added. No product, test, CI or existing report was changed.
Reviewed on 2026-10-21.

Planned cache reviewed from the uncommitted worktree `C:\projects\ai_work\agent-wt\catalog-index`
(another agent is editing it; snapshot identity in §4).

## Verdict

No P1 finding. Three P2 and seven P3 findings.

Release recommendation: **conditional no-go**. Fix SEC-1, SEC-2 and SEC-3 (all small, isolated
changes) before release. SEC-4 … SEC-10 are follow-ups and should be tracked; the planned
catalogue cache must not be merged before SEC-6 and SEC-7 are addressed.

| ID | Sev | Finding | Area |
|---|---|---|---|
| SEC-1 | P2 | Catalogue XML recursion crashes the process (uncatchable stack overflow) | catalogue parsing |
| SEC-2 | P2 | No size limits on catalogue archives/XML: memory exhaustion and temp-disk fill | catalogue parsing |
| SEC-3 | P2 | CSV formula injection in every CSV export | reports/export |
| SEC-4 | P3 | DCA read-only gating depends on the catalogue library; project-archive fallback is editable | DCA gating |
| SEC-5 | P3 | `CatalogUse.Download` gate is never consulted; legacy raw download API is unguarded | DCA gating |
| SEC-6 | P3 | Planned cache: mtime+size validation and trusted paths (stale metadata, local XML read) | planned cache |
| SEC-7 | P3 | Planned cache: unbounded gzip JSON and never-cleaned extracted archives | planned cache |
| SEC-8 | P3 | Library import overwrites non-atomically and silently; no signature check | catalogue library |
| SEC-9 | P3 | Backups are plaintext customer-project copies in `%LOCALAPPDATA%`, kept indefinitely | data handling |
| SEC-10 | P3 | Public-repo hygiene: tracked third-party manufacturer/master data despite `.gitignore` policy | repo hygiene |

## Findings

### SEC-1 (P2) Catalogue XML recursion crashes the process

**Where:** `src/Knx.Core/Catalog/KnxProductCatalog.cs:451-481` (`ReadCatalog` → `ReadSection`,
recursion at line 473); reached from `Parse` (`:306-327`) for every catalogue open.

**What:** `ReadSection` recurses once per nested `<CatalogSection>` with no depth limit. The XML
parser itself accepts arbitrary depth, so a crafted or corrupt catalogue reaches C# recursion.
`StackOverflowException` is not catchable; `CatalogStore.LoadAll`'s `catch (Exception)`
(`CatalogStore.cs:66-73`) does not help.

**Impact:** the whole application terminates (no crash dialog, no save prompt). The app has no
autosave and only an explicit save path, so unsaved project edits are lost. A catalogue left in
the library crashes every subsequent catalogue load. Reachable through the import dialog
(`Views/CatalogPane.axaml.cs:296-332`), `CatalogStore.LoadAll`, MCP `catalog_import`
(`McpServer.Tools.cs:1201-1224`) and the CLI `--import-catalogs` / `--scan-catalogs`.

**Likelihood:** low–medium. It needs a hostile or badly generated catalogue, but catalogues are
third-party downloads and the product advertises importing vendor ZIPs.

**Minimal fix:** add a depth counter (e.g. 64) to `ReadSection`/`ReadCatalog` and throw
`InvalidDataException` beyond it, or convert the walk to an explicit stack. Optionally also
instruct the XML reader with `MaxCharactersInDocument` (see SEC-2).

**Evidence (safe reproduction, outside the repo):** a 282,976-byte XML with 5,000 nested
`CatalogSection` elements. `KnxProductCatalog.Open` → process exit `0xC00000FD`
(`-1073741571`). Depth 2,000 succeeds; 5,000 crashes. The same file placed in a library
directory and loaded through `CatalogStore.LoadAll` crashes identically.

### SEC-2 (P2) No size limits on catalogue archives/XML

**Where:** `src/Knx.Core/Catalog/KnxProductCatalog.cs:70-106` (`Open` loads the whole document via
`XDocument.Load`: lines 77, 88, 98, 158, 223); `:177-198` (`OpenNested` copies the inner archive to
`%TEMP%` with `input.CopyTo(output)`, unbounded); `CatalogStore.Import` (`CatalogStore.cs:39-51`)
copies the whole container into the library. Compare the project path, which is bounded
(`ProjectArchive/ArchiveState.cs:11-13, 142-160`: 128 MiB archive, 64 MiB entry, 256 MiB total).

**What:** there is no compressed-size, uncompressed-size, entry-count or XML-size cap for
catalogues. `XDocument.Load` builds the complete tree, and the model keeps another copy of much of
it. A ZIP bomb inside a `.knxprod` therefore scales memory and disk without bound; the nested
archive is also materialised in `%TEMP%` (and, in the planned cache, persisted).

**Impact:** out-of-memory or multi-minute freeze during import/load; temp-disk exhaustion; process
crash. Measured amplification: a 21,731-byte ZIP containing 6,300,219 bytes of XML produced
75,000 catalogue items in 487 ms and a ~37 MiB managed heap delta (≈290× compressed size, ≈6×
XML size). A ~1 MiB container extrapolates to well over 1 GiB.

**Likelihood:** low–medium (same input channel as SEC-1).

**Minimal fix:** before parsing, reject archives/entries above a fixed budget (mirror
`ArchiveState`: 64 MiB per entry, 256 MiB total, entry-count cap) and bound `XDocument` with an
`XmlReaderSettings` that sets `DtdProcessing = Prohibit`, `MaxCharactersInDocument`, and a depth
count. Cap the nested copy while streaming (`CopyTo` with a counting wrapper). Avoid persisting
unbounded derived archives (SEC-7).

**DTD note (checked, not a finding):** `XDocument.Load` here accepts a `<!DOCTYPE>` (unlike the
project reader). External entities were not resolved — `XmlResolver` is null for
`XDocument.Load` — an external entity in an attribute threw, one in element content yielded an
empty value, and a "billion laughs" document stopped at the built-in
`MaxCharactersFromEntities` (10M characters) with `XmlException`. So there is no XXE file read;
still prohibit DTDs for consistency and to avoid the memory spike.

### SEC-3 (P2) CSV formula injection in every CSV export

**Where:** `src/Knx.Reports/ProjectCsvExporter.cs:64-70`; `src/Knx.App/ReportWindow.axaml.cs:63-69,
77-80`; `src/Knx.App/CompareWindow.axaml.cs:62-73`; `src/Knx.Reports/LabelCreator.cs:47-56`;
`src/Knx.App/Services/GroupMonitor.cs:240-258`; `src/Knx.App/WorkbenchWindow.Commands.cs:269-281`.

**What:** each exporter quotes delimiters but never neutralises a leading `=`, `+`, `-`, `@`,
TAB or CR. Device names, group-address names/descriptions, comments, catalogue product names and
comparison values all come from project or catalogue XML — i.e. from files the user received.
All of these exports exist to be shared as documentation.

**Impact:** a recipient opening the CSV in Excel/LibreOffice evaluates the cell as a formula
(`=cmd|…`, `=HYPERLINK("http://…")`, DDE). Depending on the client that is data exfiltration or
command execution, and at minimum corrupted documentation. `ReportWindow`'s local `Escape` and
`LabelCreator` also mishandle values that contain `"` without `;`, and the association export
(`WorkbenchWindow.Commands.cs:277`) uses `GroupMonitor.CsvField`, which does not quote `;` even
though that file is `;`-separated, so a name containing `;` shifts columns.

**Likelihood:** medium — one crafted project/catalogue plus one exported, shared report.

**Minimal fix:** one shared helper (e.g. `Knx.Reports.Csv.Field(value, separator)`) that
RFC-4180-quotes and prefixes a single quote when the value starts with `= + - @`, TAB or CR; use it
in all six places. Do not try to detect "dangerous" formulas otherwise.

**Evidence (executed):** a project with device name `=cmd|' /C calc'!A0`, description
`+SUM(1,1)`, comment `@evil` produced:

```
Adresse;Name;ProduktRef;Beschreibung;Kommentar
1.1.1;=cmd|' /C calc'!A0;M-0001_P-1;+SUM(1,1);@evil
```

### SEC-4 (P3) DCA read-only gating depends on the catalogue library

**Where:** `src/Knx.App/MainWindowViewModel.cs:709-714` (`ParameterReadOnlyReason` → catalogue
program only), `:656-702` (`LoadParametersFor`: project-archive branch at 663-671, catalogue branch
at 672-687); applied by `WorkbenchWindow.axaml.cs:310-321` (call sites 115, 299, 521, 704).

**What:** read-only for DCA/broken/incomplete applications is derived solely from the catalogue
snapshot (`CatalogBrowser.Blocked`, `CatalogBrowser.cs:129-137`). When a project is opened and
`Knxproj.LoadApplicationProgram` finds the device's application part inside the `.knxproj`, the
parameter editor is populated from that XML without consulting the catalogue. If the matching
catalogue is not in the local library (fresh install, colleague's project, catalogue removed), the
reason is `null`, `ParameterList.IsEnabled` is true, and the DCA application's parameter values can
be edited and persisted into the project.

**Impact:** local parameter mutation for a DCA application, contrary to the documented "read-only"
guarantee; values can drift from what the plug-in would compute. No device impact because no
download path exists (SEC-5). Native ETS XML stays opaque (`XmlPreservation`), so no corruption of
unmodelled data.

**Likelihood:** medium when opening foreign projects without the vendor catalogue.

**Minimal fix:** when the parameter model comes from the project archive, scan its
`Static/Extension` for `EtsUiPlugin`/`EtsDownloadPlugin`/`EtsDataHandler` (the same predicate as
`CatalogApplicationProgram.RequiresDca`) and set the read-only reason from that; keep the existing
catalogue check as the primary source.

### SEC-5 (P3) Download gate unused; raw download API unguarded

**Where:** `CatalogBrowser.cs:134-135` defines `CatalogUse.Download` blocking, but `git grep`
finds no caller of `Blocked(…, CatalogUse.Download)`; `MainWindowViewModel.cs:839-853`
(`DownloadAsync(string imagePath)`) writes an arbitrary file to a device through
`KnxBus.DownloadAsync` with fixed `StartAddress = 0x0060` (`:1176-1190`), no confirmation, no
catalogue/DCA/broken check; `WorkbenchSessions.cs:19-30` accepts a real executor but the app
constructs sessions without one (`WorkbenchWindow.axaml.cs:338-345`), so real jobs throw
`NotSupportedException` (`ProgrammingSession.cs:115-133`).

**Impact:** none today — the legacy method has no UI/MCP caller and no real executor is injected,
so no DCA download is possible. It is a loaded gun for the next wiring: a future download command
could bypass the catalogue gating entirely.

**Likelihood:** latent.

**Minimal fix:** remove `DownloadAsync`/`FileProgram` or mark them test-only, and make every future
download entry point call `Blocked(program, CatalogUse.Download)` plus an explicit confirmation.

### SEC-6 (P3) Planned cache: weak invalidation and trusted paths

**Where (uncommitted worktree snapshot):** `src/Knx.Core/Catalog/CatalogIndex.cs:53-57` (validation
by version, file name, length, last-write ticks), `:298-303` and `:340-341` (`FromIndex` adopts
`BackingArchive` and `SourceFilePath` from the JSON), `:48-51` (deserialize);
`src/Knx.Core/Catalog/KnxProductCatalog.cs:344-352` (`LoadPartMetadata` → `XDocument.Load(location)`
when `_archivePath` is null); `src/Knx.Core/Catalog/CatalogStore.cs:35-37` (cache lives in
`<library>/.catalog-index` for explicit libraries), `:140,169` (load path).

**What:**
1. Freshness is only name + byte length + mtime. `CatalogStore.Import` uses `File.Copy`, which
   preserves the source mtime, and the file name is an identity hash, so a replaced catalogue with
   matching size and timestamp reuses stale metadata — including `Broken`, `DownloadInfoIncomplete`
   and DCA flags that drive gating. The plan itself lists content-hash invalidation as required
   (analysis/catalog-load-performance.md, O13).
2. Metadata is fully trusted for paths. A cache document for an XML-backed library entry
   (`knxprod.xml` is accepted by `CatalogStore.IsCatalogFile`, `CatalogStore.cs:116-121`) can set
   `Parts[].Location` / `SourceFilePath` to any local file; the app then opens it and surfaces
   program/communication-object text in search, details and exports. Requires write access to the
   cache — same user, or anyone who can write a portable library folder because the cache then
   travels inside it.

**Impact:** wrong gating/status data; local file read surfaced through the UI for cache-poisoning
scenarios. No cross-user privilege boundary (the app is `asInvoker`; no admin manifest), so
severity is hardening-level.

**Likelihood:** low (needs local write or a shared portable library), but the portable layout is
explicitly supported by the new code.

**Minimal fix:** key/validate the cache by a content digest (SHA-256 of the library file, computed
once at import and validated at load; mtime alone is not an identity). Store only archive entry
names in the cache and always resolve them against the library file; for XML catalogues, resolve
relative to the catalogue directory and reject paths outside it. Never persist absolute paths.

### SEC-7 (P3) Planned cache: unbounded gzip JSON and never-cleaned extracted archives

**Where (worktree):** `CatalogIndex.cs:48-51` (deserialize a `GZipStream` with no size cap);
`KnxProductCatalog.cs:209-211, 234-257` (`PersistInnerArchive`: `input.CopyTo` + `File.Copy` of the
inner archive into `<cache>/archives/<hash>.knxprod`, identity built from outer path, **compressed**
entry length and mtime); `CatalogStore.cs:223-232` (`Remove` drops only the index, not the extracted
archive).

**What:** a small `.json.gz` in the cache directory expands without bound into
`JsonSerializer.Deserialize` (which buffers the decompressed payload); a nested vendor archive is
extracted without a size cap and is never deleted when the catalogue is removed or replaced. The
extraction identity can collide because `ZipArchiveEntry.Length` is the compressed length from the
(untrusted) central directory and mtime is forgeable/preserved, so a stale inner archive can be
reused instead of the current one.

**Impact:** memory exhaustion from the cache; unbounded growth of `%LOCALAPPDATA%` (hundreds of MiB
per nested vendor catalogue); stale program metadata after a replaced wrapper.

**Minimal fix:** cap the cache file and the decompressed JSON (read with a limiting stream and
`JsonSerializerOptions.MaxDepth`); cap `CopyTo` for the inner archive (SEC-2 budget); include a
content hash of the inner archive in the identity and delete the derived archive together with the
index on remove/replace.

### SEC-8 (P3) Library import overwrites non-atomically and silently

**Where:** `CatalogStore.cs:39-51` (`File.Copy(catalog.Path, target, overwrite: true)` at line 45);
`LibraryFileName` identity digest at `:92-114`; `Remove` at `:79-90`. No `.signature` entry is ever
read (`git grep signature -- src`).

**What:** the library file name is derived from manufacturer/product/program identities, not from
the container bytes, so importing a second catalogue with the same identity silently replaces the
first. The copy writes directly over the destination, so a failure (disk full, unplugged drive,
antivirus lock) can leave a truncated catalogue where a good one was. Vendor `.signature` files in
the archive are ignored, so there is no authenticity or provenance check at all.

**Impact:** catalogue substitution (different parameter/COM-object definitions for the same product
ids) if a user imports such a file; loss of a previously good library entry after a failed
re-import. No project data is touched.

**Likelihood:** low (needs a crafted same-identity catalogue or a failing write).

**Minimal fix:** copy to a temp file in the library and `File.Move(..., overwrite: true)`; keep the
previous entry as `.bak` or ask before replacing an existing identity; if `M-xxxx.signature` is
present, verify it or at least surface "unsigned catalogue" in the import report.

### SEC-9 (P3) Backups are plaintext customer-project copies

**Where:** `ProjectBackup.cs:35-41, 161-221, 400-405`; manifests record `SourcePath` (`:205`).

**What:** every explicit backup or snapshot is a byte-identical copy of the customer project in
`%LOCALAPPDATA%\BusChord\Backups`, up to 10 per identity, with the source path in the manifest.
Restore/delete are per entry; there is no "delete all", and pruning only happens on the next create.
Encrypted projects cannot be backed up at all (the validation load has no password).

**Impact:** copies of customer projects outlive the project folder and are visible to any process
running as the user; a user who deletes a project may believe the data is gone. Not a
vulnerability (same user, default profile ACLs), but a data-handling gap for a tool used on
customer installations.

**Likelihood:** certain, by design.

**Minimal fix:** document the location and retention in the backup dialog; add "delete all backups";
optionally offer to store backups next to the project instead of the profile. Do not claim backups
are a privacy feature.

### SEC-10 (P3) Public-repo hygiene: tracked third-party manufacturer/master data

**Where:** `tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj` (394,270 bytes, tracked;
contains `M-0008/`, `M-0048/`, `M-0083/` catalogue and application-program XML plus
`knx_master.xml` and manufacturer `.signature` entries); `tests/Knx.Core.Tests/knx_master.xml`
(1,082,494 bytes, tracked); `.gitignore:10-18` states "Manufacturer catalogue data and vendor
binaries stay local; never commit them"; `LICENSE` is Apache-2.0 while the fixture README declares
the upstream project GPL-2.0 (`tests/Knx.Encryption.Tests/fixtures/README.md`).

**What:** the stated policy is contradicted by two tracked files. The fixture's *project* content
is fictional ("testprojekt", Küche/Bad/Technik, `ga0…`), the documented password `test` is the
published upstream one, and no customer data or credentials were found. But real manufacturer
catalogue data and KNX Association master data are redistributed, and the fixture's GPL-2.0 origin
is not obviously compatible with Apache-2.0 redistribution.

**Impact:** legal/licensing exposure on public release; policy drift (reviewers will trust
`.gitignore`). No security impact found.

**Likelihood:** certain on publication.

**Minimal fix:** decide and document the license basis for both files (attribution/NOTICE), or
replace the fixture with a synthetic project generated in the test (the `WorkbenchCheck` self-test
already writes a synthetic DCA catalogue, `src/Knx.App/Workbench/WorkbenchCheck.cs:513-541`).
`git ls-files` and history contain no `.knxprod`, `.vd4` or `.vd5` file; the largest historical
blobs are brand assets.

## Areas verified clean

- **ZIP-slip / path traversal (catalogues):** no entry is ever extracted to a path built from its
  name. Nested archives go to a GUID-named temp file (`KnxProductCatalog.cs:177-198`), the planned
  cache uses a hash name, and all other reads use `ZipArchive.GetEntry`/entry streams. Directory
  walks (`OpenSplitDirectory`, CLI scan/import) follow junctions but are CLI/API-only.
- **Archive nesting:** hard cap of four levels (`KnxProductCatalog.cs:56`).
- **XXE:** no external entity resolution; verified as described under SEC-2.
- **Project archive password handling:** password is only a local variable passed to
  `Knxproj.Load`; never written, logged or put in status text. Encrypted projects cannot be saved
  back (`ArchiveState.cs:90-97`) and extra parts refuse to load without the password (`:78-88`).
  Limits: 128 MiB archive, 64 MiB entry, 256 MiB total, 4096 entries, duplicate names rejected;
  ZipCrypto/strong-encryption flags rejected; wrong-password detection uses a constant-time
  verifier and HMAC (`EncryptedArchive.cs:21-28, 50-68, 110-126`). Automated grep found no
  password/token logging.
- **Project XML parsing:** DTD prohibited, resolver null, 16 MiB document cap, depth ≤128 before
  LINQ recursion (`ArchiveState.cs:162-174`) — the opposite of the catalogue path (SEC-1/2).
- **DCA execution:** no plug-in loading anywhere (`Assembly.Load`, `Activator`, `Process.Start` for
  plug-ins: none; the only `Process.Start` is the screenshot helper with `ArgumentList`). No real
  download executor is wired, so no DCA download exists. Parameter apply is guarded
  (`ParameterEditingSession.cs:275-297`) and only typed instances are written; native ETS XML and
  unmodelled data survive via `XmlPreservation`.
- **HTML report/labels:** every user value is escaped in text and attribute contexts, and the CSS
  string context escapes `\`, `"`, newlines and `<` (`ProjectReport.cs:450-477`), so `</style>`
  breakout is not possible. Reports contain no scripts or external assets.
- **Backup/restore:** containment prefix includes the separator, IDs are restricted to
  `[A-Za-z0-9_-]`, restore inside the backup root is refused, reparse-point archives are refused,
  SHA-256 and size are validated, and the destination is replaced atomically only after the
  candidate loads (`ProjectBackup.cs:126-159, 260-330, 413-439`).
- **Demo JSON import:** 4 MB cap, `MaxDepth = 12`, strict key sets, enum/range/address validation,
  collection caps, duplicate detection, `__proto__`/`prototype`/`constructor` rejected, bounded
  recursion (`WorkbenchJson.cs:21-121, 295-329`). Import asks before discarding unsaved work
  (`WorkbenchWindow.Commands.cs:400-420`).
- **Catalogue images:** only entry names are collected (`KnxProductCatalog.cs:638-645`); no image
  is decoded, so no image-parser surface.
- **Catalogue removal:** path must be directly inside the library and must not be a reparse point
  (`CatalogStore.cs:79-90`).
- **Secrets/credentials in tracked files:** none found (Taiga/Gitea credentials, tokens, keys,
  `.env`, private IPs were not present). CI has no `run:` interpolation of event data and the CLA
  workflow does not check out PR code.

## Planned catalogue cache — review status

Reviewed as an uncommitted worktree, not as a branch: `agents/final/catalog-index` does not exist;
`agents/perf/catalog-index` is fully merged into the base (its only unique commit is already an
ancestor) and `agents/perf/catalog-load` adds only an analysis document. The uncommitted work is
being edited concurrently. Snapshot reviewed:

| File | Bytes | SHA-256 (first 16) | mtime |
|---|---:|---|---|
| `src/Knx.Core/Catalog/CatalogIndex.cs` | 16,775 | `D7F045206C21EF9A` | 2026-10-21 22:38:13 |
| `src/Knx.Core/Catalog/CatalogStore.cs` | 12,339 | `6EDB8CAE3F41D400` | 2026-10-21 22:36:12 |
| `src/Knx.Core/Catalog/KnxProductCatalog.cs` | 44,503 | `19801F2B223932E5` | 2026-10-21 22:31:03 |

Positives: schema version, atomic temp+move writes, per-file rebuild on corrupt input, background
load with cancellation, and no cache write for nested wrappers that cannot be indexed. The
blocking items for merge are SEC-6 and SEC-7. Re-review the final commit; line numbers above refer
to the snapshot, not to a commit.

## Not verified / needs real Windows or real-data testing

- Windows ACLs, junctions/reparse points, NTFS alternate data streams and case-insensitive path
  behaviour for `%APPDATA%`, `%LOCALAPPDATA%` and portable library folders. The portable cache
  (`<library>/.catalog-index`) was assessed by code only.
- Real vendor catalogues at scale (Gira/Hager/Siemens/ABB/Schneider, 60–90 MB split archives) — the
  reproduction used synthetic XML/ZIP. The 400 MB / 124-file library from the performance report
  was not imported.
- Actual Excel/LibreOffice evaluation of the CSV payloads (only the emitted bytes were verified).
- OOM behaviour beyond the 6 MiB bomb; no attempt was made to exhaust memory deliberately.
- The DCA-without-catalogue UI scenario (SEC-4): code-path analysis only; needs a real project with
  an embedded application program and an empty library.
- Real ETS6 encrypted projects beyond the xknxproject fixture (loaded with its documented password).
- No repository test suite was run (read-only review). `Knx.Core` and `Knx.Reports` were built to a
  temp directory for the repro harness without touching the worktree.

## Reproduction notes

All artifacts live outside the repository in
`C:\Users\julia\AppData\Local\Temp\opencode\sec-review` (harness project + inputs). Commands:

```
harness.exe deep 5000            # 282,976 B XML -> Stack overflow, exit 0xC00000FD
harness.exe svc <deep.xml>       # same file in a library dir -> CatalogStore.LoadAll crashes
harness.exe bomb 6               # 21,731 B zip -> 6,300,219 B XML, 37 MiB heap, 487 ms
harness.exe xxe xxe2.xml         # no file read; entity content empty
harness.exe xxe laughs.xml       # XmlException: MaxCharactersFromEntities
harness.exe csv                  # formula payloads emitted unquoted
harness.exe knxproj <fixture> test  # fixture: fictional project names, real M-0008/48/83 data
```

No untrusted binary, plug-in or bus action was executed.
