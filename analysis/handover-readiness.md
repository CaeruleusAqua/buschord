# Handover readiness — documentation and handover of complete KNX projects

Read-only assessment. The only change is this file; no product, test, CI or
existing report was modified.

- Base: `08a0f6a58b1a51716c59e0f233837aa28b3d9e46` (`development`, verified)
- Branch: `agents/assess/handover`
- Worktree: `C:\projects\ai_work\agent-wt\assess-handover`
- Mode: read-only assessment/documentation
- Date: 2026-10-22

Question: if an installer finishes a KNX installation with BusChord and hands it
to a building owner, installer colleague or facility manager, what do they
actually get today — and what is missing for a professional handover?

## 1. What a professional KNX handover delivers

A handover that stands up in a dispute (warranty, changes of operator,
insurance) normally contains at least:

| # | Deliverable | Purpose / typical recipient |
|---|---|---|
| D1 | As-built topology and device list (areas/lines/segments, individual addresses, manufacturer, product, order number, serial if recorded) | owner, maintenance, replacement planning |
| D2 | Group-address list with datapoint types (DPT), descriptions, comments | integrator, service, extension |
| D3 | Room book (room/floor list, devices per room, GA per room) | owner, facility management |
| D4 | Function matrix (which device/object drives which GA/function, sending direction, room reference) | service, troubleshooting |
| D5 | Labels (GA labels, device labels, room labels) | installation documentation on site |
| D6 | Parameter/signature report (per-device parameter state and a comparable signature) | commissioning proof, change detection |
| D7 | Test/acceptance protocol (checks performed, findings, bus scan result, telegram evidence) | contract acceptance, warranty |
| D8 | Backup/restore (project archive copy, restore instructions, integrity hash) | disaster recovery |
| D9 | Revision/version history (as-built version, superseded versions) | traceability |
| D10 | Change log (what changed since the previous version) | traceability, invoicing |
| D11 | Operating instructions (how the owner operates the functions) | owner |
| D12 | Contact/maintenance information (installer, hotline, service intervals) | owner |
| D13 | Data-protection notes (what personal data the documentation contains, retention, deletion) | owner, installer's privacy obligation |

ETS itself covers parts of D1–D5 and D7 (project documentation, label creator,
compare) but not D9–D13 as a packaged handover; an installer usually assembles
PDFs and printouts manually. The bar for BusChord is therefore not "an ETS
export" but "one coherent, honest handover package".

## 2. Deliverable-by-deliverable mapping

Status: **done** = a user-reachable artifact exists; **partial** = data or a
renderer exists but the handover artifact is incomplete or unreachable;
**missing** = no handover artifact exists.

| # | Deliverable | Status | User-reachable today |
|---|---|---|---|
| D1 | Topology/device list | partial | CSV + HTML report; no catalog join, no serial/firmware |
| D2 | GA list with DPT | done | CSV + HTML report; comments not included |
| D3 | Room book | partial | native building tree exists; not in any handover export |
| D4 | Function matrix | partial | associations CSV (workbench only), link matrix, semantic export |
| D5 | Labels | partial | GA labels HTML (workbench), GA/device labels HTML/CSV (MCP); de-only HTML, dead merge path |
| D6 | Parameter/signature report | missing | signature + programming-state engine exists, no export |
| D7 | Test/acceptance protocol | missing | check findings in report/JSON; read-only diagnostics unverified, no protocol document |
| D8 | Backup/restore | done (local) | byte copy + manifest + SHA-256; no encryption, no delete-all |
| D9 | Revision/version history | missing | only format versions; backup retention is the closest thing |
| D10 | Change log | partial | in-memory audit (lost on close), file-level compare (no links/params) |
| D11 | Operating instructions | missing | tool manual only (`docs/manual.md`, German) |
| D12 | Contact/maintenance info | missing | no fields, no template |
| D13 | Data-protection notes | missing | security policy exists; no handover note |

### D1 — As-built topology and device list (partial)

Evidence:

- CSV topology: `src/Knx.Reports/ProjectCsvExporter.cs:45-65`
  (`Area;Line;Segment;Gerät;Adresse`).
- CSV devices: `src/Knx.Reports/ProjectCsvExporter.cs:24-43`
  (`Adresse;Name;ProduktRef;Beschreibung;Kommentar`).
- HTML report topology/device tables: `src/Knx.Reports/ProjectReport.cs:160-207`.
- MCP: `export_csv` kinds `group_addresses|devices|topology`
  (`src/Knx.Mcp/McpServer.Tools.cs:195-200`, handler `:697-711`).
- Workbench menu: `src/Knx.App/WorkbenchWindow.Commands.cs:30-32`.

Gaps: the device rows carry only `ProductRefId` — no manufacturer name, product
name, order number or serial number, although the catalogue model holds them
(`src/Knx.Core/Catalog/CatalogModel.cs:7`, `:14-20`); the catalogue is never
joined into any export. The report device table shows
address/name/product-ref/description only (`ProjectReport.cs:191-207`).
BusChord writes no serial numbers into the project, and the report does not
state mask/medium facts beyond the topology names.

What an owner receives today: `topologie.csv` and `geraete.csv` with internal
reference IDs that only an ETS user can decode, plus a printable device list.

### D2 — Group-address list with datapoint types (done)

Evidence:

- CSV: `src/Knx.Reports/ProjectCsvExporter.cs:6-22` (address, name, description,
  raw DPT such as `DPST-1-1`, Puid).
- HTML report table with links per address: `src/Knx.Reports/ProjectReport.cs:209-248`,
  link cell `:439-442`.
- MCP `group_addresses_list` (`McpServer.Tools.cs:87-91`), import
  `group_address_import_csv` (`:113-119`).
- i18n-checked headers: `src/Knx.Core/Localization.cs:542-570`.

Gaps: the DPT is printed in raw `DPST-1-1` form in CSV and report, while ETS and
the semantic export use the display form `1.001`
(`SemanticExport.FormatDatapointType`, checked in `tests/Knx.Core.Tests/Program.cs:586-589`);
GA comments (`Model.cs:75`) appear in no export — only `Description` in the CSV
(`ProjectCsvExporter.cs:19`), and neither in the report table.

What an owner receives today: a complete GA list with DPTs and cross-links —
this is the strongest part of the current output.

### D3 — Room book (partial)

Evidence:

- Native building tree with `Building/BuildingPart/Floor/Room/…/Function` and
  device references: `src/Knx.Core/Buildings.cs:9-27` (`SpaceKind`), `:68-147`
  (`SpaceNode`, `Path` at `:91`), `:153-225` (`BuildingTree`).
- Workbench building perspective and room tabs: `docs/manual.md:86`, `:93-99`;
  search scope `buildings`: `McpServer.Tools.cs:49-57`.
- Semantic export emits `loc:` space nodes with `containsEquipment`:
  `src/Knx.Core/SemanticExport.cs:94-111` (Turtle), `:188-203` (JSON-LD).
- The associations CSV carries an `InstalledRoomId`
  (`src/Knx.Reports/ProjectCsvExporter.cs:68-78`; `WorkbenchProject.Associations`,
  `src/Knx.Core/Workbench/WorkbenchProject.cs:252-262`) — but that is BusChord
  workbench metadata, not the native `Locations` tree.
- The HTML report never touches `Installations[].Locations`: `ProjectReport.cs:160-189`
  iterates installations/areas/lines/segments only.

Gaps: no room/floor list, no "devices per room", no "devices without a room"
section, no CSV/MCP export for the building tree. A room book can only be
reconstructed from the semantic export or the workbench UI.

### D4 — Function matrix (partial)

Evidence:

- Associations CSV = device/object → GA with DPT, bit size, flags, active and
  sending marker: `src/Knx.Reports/ProjectCsvExporter.cs:68-78`.
  Workbench menu only (`WorkbenchWindow.Commands.cs:34`); **not** in the MCP
  `export_csv` kinds (`McpServer.Tools.cs:198`), so an agent cannot produce it.
- Link-coverage matrix per main range (GA/DPT/linked/unlinked/links):
  `src/Knx.Reports/ProjectReport.cs:99-122` and `:288-324`.
- Semantic function points with device links: `SemanticExport.cs:82-92`, `:113-117`.

Gaps: there is no function matrix in the sense of "room × function ×
participating devices/objects"; the report matrix is a coverage statistic, and
the full association list exists only as a flat CSV that the workbench emits.

### D5 — Labels (partial)

Evidence:

- GA labels and device labels, HTML + CSV from one model:
  `src/Knx.Reports/LabelCreator.cs:27-53`; printable A4 HTML `:56-97`;
  layout defaults 4×8, 46×25 mm, no copies/skip/margins: `:10-19`.
- Workbench exports **GA labels HTML only**:
  `src/Knx.App/WorkbenchWindow.Commands.cs:35`, `:324-328`.
- MCP `export_labels` `html|csv|merge` (merge = GA+device labels on one HTML
  sheet): `McpServer.Tools.cs:207-214`, handler `:728-764`.
- ETS-style Word mail-merge data source exists but is dead code:
  `LabelMergeSource` `LabelCreator.cs:122-172` has no caller; the localization
  keys `MenuFileExportLabelsCsv`/`MenuFileExportMerge`
  (`src/Knx.Core/Localization.cs:68-69`) are wired to no menu item (the menu
  only has `ExportLabels`, `Commands.cs:35`).
- The label HTML hardcodes `lang="de"` (`LabelCreator.cs:78`) and the fallback
  sheet title `"Etiketten"` (`:72`).
- Device labels show name/description/line name (`:37-45`), not
  manufacturer/product, although `MergeLabel` models them (`:127-137`).

Gaps: no label options UI, no per-device copies/skip (ETS has both), no CSV
label export in the workbench, no room labels, and the merge path is unusable.

### D6 — Parameter/signature report (missing)

Evidence (engine only):

- Per-device configuration signature: SHA-256 over ordered bus-relevant facts
  (addresses, product/program, parameters, object state, linked addresses and
  security): `src/Knx.Core/Workbench/WorkbenchProject.cs:221-250`; contract in
  `docs/workbench-domain.md:50-54`.
- Programming state side file (five load flags, image hash, attempts, success
  timestamps, origin `Demo|Real`): `src/Knx.Core/Workbench/DeviceProgrammingState.cs:183-237`;
  read-only status helper `DeviceProgrammingReport.Check` `:245-267`.
- Session-level export/import exists (`ExportDownloadedJson`/`ImportDownloadedJson`,
  `src/Knx.App/Workbench/Sessions/ProgrammingSession.cs:350-357`) but has **no
  caller** in the application; the programming tool is a labelled simulation
  (`docs/manual.md:29-31`, `README.md:27`; `docs/workbench-domain.md:59-63`:
  `ProgrammingOrigin.Real` is reserved, only demo execution exists).

Gaps: nothing of this reaches a report, CSV or MCP tool. Even if it did, a
"real" commissioning proof is impossible today because no real download is
implemented — the report would have to say "demo/unverified" honestly.

### D7 — Test/acceptance protocol (missing)

Evidence:

- Structural check P001–P073: `src/Knx.Core/ProjectCheck.cs`; findings appear in
  the report (`ProjectReport.cs:326-352`) and via MCP `project_check`
  (`McpServer.Tools.cs:170-174`, handler `:631-648`).
- The workbench problems pane shows findings with no CSV export
  (`src/Knx.App/WorkbenchWindow.axaml.cs:1967-1985`); manual states this
  explicitly (`docs/manual.md:592-594`). `ReportCsv` and the generic
  `ReportWindow` with a CSV button exist (`src/Knx.Reports/ReportCsv.cs:8-14`,
  `src/Knx.App/ReportWindow.axaml.cs:53-74`) but `ReportWindow` is never
  instantiated — dead UI.
- Read-only bus diagnostics: `bus_scan`/`bus_device_info`/`bus_statistics`
  (`mcp.md:113-129`), telegram recorder XML/CSV/JSONL (`McpServer.Tools.cs:342-355`).
  The wire behaviour of scan/descriptor frames is unverified live
  (`mcp.md:128-129`).

Gaps: no protocol document that combines checks, expected-vs-found addresses,
timestamps and telegram evidence; no file output for `bus_scan`; nothing marks
simulated vs real evidence in an exported artifact.

### D8 — Backup/restore (done, with limits)

Evidence:

- `ProjectBackup` with manifest, byte copy, SHA-256 and restore verification:
  `docs/project-backup.md:10-23`, `:41-55`, `:71-90`; checks
  `tests/Knx.Archive.Tests`.
- Workbench: `Projekt → Backup jetzt erstellen`, `Sicherungen …`
  (`WorkbenchWindow.Commands.cs:40-41`, `:279-322`); manual `docs/manual.md:509-520`.
- Limits: default retention 10 per identity, pruned only on the next create
  (`docs/project-backup.md:65-69`); no encryption, no auto-backup on close
  (`:92-100`); encrypted projects cannot be backed up (`:84-87`); no "delete
  all" in the UI (`analysis/security-p3-followups.md:86-89`).

What an owner receives today: the `.knxproj` plus local backup copies; the
backup root is `%LOCALAPPDATA%\BusChord\Backups` and is not part of any
handover package.

### D9 — Revision/version history (missing)

Evidence:

- The project model has no revision/version field: `Id`, `Name`, `Comment`,
  `ProjectStart`, `Guid`, `MasterData`, `Installations`
  (`src/Knx.Core/Model.cs:98-118`); the writer emits exactly these attributes
  (`src/Knx.Core/Knxproj.cs:94-99`).
- Only format versions exist: BusChord workbench extension `Version=1`
  (`Knxproj.cs:147-149`), demo JSON `version 4 / schemaVersion 1`
  (`WorkbenchJson.cs:210`), programming-state side file `Version=2`
  (`DeviceProgrammingState.cs:189`), backup manifest version 1
  (`docs/project-backup.md:53-55`).
- `DeviceInstance.Revision`/`DownloadedRevision` exist only as demo workbench
  metadata (`src/Knx.Core/Workbench/Contracts.cs:113-114`) and are exported only
  in the demo JSON (`WorkbenchJson.cs:200`).
- Native ETS history/attributes survive opaquely through the three-way merge
  (`docs/manual.md:522-530`), but BusChord neither reads nor maintains a
  document revision.

What an owner receives today: nothing that says "this is version 3 of the
as-built documentation"; the closest substitute is the timestamped backup list.

### D10 — Change log (partial)

Evidence:

- In-memory change feed: `WorkbenchAudit` capacity 500, cleared on project
  switch/reset, never persisted or exported
  (`src/Knx.App/Workbench/WorkbenchAudit.cs:7-34`;
  fed by `WorkbenchWindow.axaml.cs:383-391`; cleared in
  `WorkbenchWindow.Commands.cs:379`; shown in the "changes" tool
  `WorkbenchWindow.axaml.cs:2259-2265`).
- File-level compare: `ProjectComparer.Compare` covers project name/comment,
  installations, devices (name/product ref) and group addresses (name/DPT/
  description) only — `src/Knx.ProjectCompare/ProjectComparer.cs:43-153`,
  `:156-216`. It does **not** compare com-object links, parameter values,
  buildings or topology placement.
- Workbench compare window with CSV save: `src/Knx.App/CompareWindow.axaml.cs:28-48`;
  MCP `project_compare` with a 500-item cap (`McpServer.Tools.cs:229-235`,
  handler `:797-828`).

Gaps: no persisted change log, no diff export as part of documentation, and
the diff misses exactly the facts that matter after commissioning (links,
parameters).

### D11 — Operating instructions (missing)

`docs/manual.md` documents the tool for its user (German, `:1-6`), and the
workbench help window explains the UI. There is no per-project operating
instruction for the building owner (which room, which switch, which scene),
and no template to generate one.

### D12 — Contact/maintenance information (missing)

No fields in the project model (`Model.cs:98-118`) and no section in any export
carry installer, service contact or maintenance intervals. The report title
page has project id/GUID/dates only (`ProjectReport.cs:126-158`).

### D13 — Data-protection notes (missing)

`SECURITY.md:51-52` names privacy as a review scope, but no artifact tells the
owner what personal data the documentation contains, how long it is kept or how
it is deleted. Grep for DSGVO/GDPR/Datenschutz/privacy finds no product-facing
text (`SECURITY.md`, `CODE_OF_CONDUCT.md` only). See section 4.

## 3. Cross-cutting suitability

### 3.1 German/English

- The UI ships German and English with live switching
  (`src/Knx.Core/Localization.cs:15-29`; `src/Knx.App/Localization/WorkbenchStrings.cs:45`;
  `src/Knx.App/SettingsWindow.axaml.cs:21-42`), and localization completeness
  plus placeholder formatting are tested (`tests/Knx.Core.Tests/Program.cs:769-800`,
  `tests/Knx.Localization.Tests`).
- The HTML report follows the active language including `<html lang>`
  (`ProjectReport.cs:57`); check messages, CSV headers and report texts are
  localized (`Localization.cs:542-570`; status matrix
  `analysis/workbench-v4-status-matrix.md:194` marks English as partial with
  path tokens/technical values remaining data).
- Breaks:
  - Label HTML is always `lang="de"` and titled "Etiketten" by default
    (`LabelCreator.cs:72`, `:78`).
  - Semantic export hardcodes `"de"` language tags for every literal
    (`SemanticExport.cs:63`, `:71`, `:74`, `:85`, `:90`, `:101`) regardless of
    UI language.
  - The MCP server never sets `Strings.Language`, so it defaults to German
    (`Localization.cs:15`; no assignment anywhere under `src/Knx.Mcp`), and no
    export/report tool exposes a language parameter
    (`McpServer.Tools.cs:195-235`). Agents and scripts can only produce German
    handover artifacts.
  - The German topology CSV header mixes languages:
    `Area;Line;Segment;Gerät;Adresse` (`Localization.cs:546`).
  - `docs/manual.md` is German-only; README/mcp.md are English.
- Consequence for handover: a German owner gets consistent output; an English
  owner (or an English-language contract) gets a German report body, German
  MCP exports and an English-only tool manual.

### 3.2 File formats

| Artifact | Format | Notes |
|---|---|---|
| GA/devices/topology/associations CSV | `;`-separated text | workbench: UTF-8 **without** BOM (`WorkbenchWindow.Commands.cs:257`); MCP: UTF-8 **with** BOM (`McpServer.Tools.cs:709`); label CSV uses `\n` (`LabelCreator.cs:49`), associations `\r\n` (`ProjectCsvExporter.cs:70`) |
| Project report | self-contained HTML, inline CSS, print page breaks/page numbers | no PDF by design (`ProjectReport.cs:36-42`; `mcp.md:98-100`); A4/A3/Letter via library options (`:21`), workbench (`WorkbenchWindow.Commands.cs:33`) and MCP both use the defaults |
| Labels | self-contained HTML A4 grid; CSV; (dead) TSV mail-merge source | `LabelCreator.cs:56-97`, `:155-166` |
| Semantic export | Turtle (optionally with ontology concatenated) and JSON-LD | `SemanticExport.cs:34-52` |
| Project archive | `.knxproj` ZIP, schema `/23` written, `/21` read-only | `Knxproj.cs:23-33`, `docs/manual.md:539-551` |
| Backup | byte copy + JSON manifest with SHA-256 | `docs/project-backup.md:41-55` |
| Telegram evidence | XML/CSV/JSONL | `McpServer.Tools.cs:350-355` |
| Demo exchange | JSON `knx-studio-demo` v4, refuses native data | `WorkbenchJson.cs:184-218` |

No DOCX/XLSX/PDF/ODF output; everything is "print from browser" or plain text.
That is workable but means the handover quality depends on the recipient's
browser/print settings (`@page` CSS `ProjectReport.cs:365-369`).

### 3.3 Reproducibility

- Ordering is deterministic in the exporters (`OrderBy(a => a.Address.Raw)`,
  `ProjectReport.cs:198`, `:212`, `:238`; `ProjectCsvExporter.cs:29`, `:39`).
- The report timestamp is injectable in Core (`ProjectReportOptions.GeneratedAt`,
  `ProjectReport.cs:18-19`) and tests pin it (`src/Knx.App.Tests/Program.cs:281-300`),
  but the MCP `report_export` tool exposes neither timestamp, paper size nor
  language (`McpServer.Tools.cs:766-779`). Byte-identical regeneration is only
  possible through the library, not through any user interface.
- Output language changes content and CSV headers, so "the same export" differs
  between a German and an English session.
- No export carries a content hash, a BusChord version or a project revision;
  the report names only `<meta name="generator" content="BusChord">`
  (`ProjectReport.cs:61`) without a version. There is no manifest for a
  multi-file handover.
- `FEATURE.md:121` still claims 38 MCP tools while the server exposes 48
  (`README.md:28`; asserted in `tests/Knx.Mcp.Tests/Program.cs:50` and
  `src/Knx.Mcp/Selftest.cs:41`) — doc drift that matters when a handover claims
  "which tool/version produced this".

### 3.4 Signature/hash

- Project/master-data signatures are preserved but never recomputed; ETS may
  fail signature verification after BusChord edits (`README.md:33-34`,
  `docs/manual.md:532-537`, `Knxproj.cs:155-158`, `:249-250`). A handover of an
  edited project therefore cannot claim an intact ETS signature.
- No export, report or label carries a signature or hash. The only integrity
  hashes are the backup manifest SHA-256 (verified on restore,
  `docs/project-backup.md:76-78`) and the per-device
  `ConfigurationSignature` (`WorkbenchProject.cs:249`) which is not exported.
- Catalogue `.signature` files are surfaced but not verified
  (`src/Knx.Core/Catalog/CatalogStore.cs:19-22`, `:114-124`).
- There is no "signed handover package" and no way for a third party to verify
  that two handover folders contain the same project state.

### 3.5 Project versioning

- No document revision exists (see D9). The native `Guid` and `ProjectStart`
  are printed on the report title page (`ProjectReport.cs:136-140`), which
  identifies the project but not the document version.
- The demo JSON carries `version 4 / schemaVersion 1` (`WorkbenchJson.cs:210`),
  the workbench extension `Version=1` (`Knxproj.cs:148`) — format versions, not
  content versions.
- Backups are timestamped, SHA-256-stamped copies, but they live outside the
  project and are not referenced by any export. A reader cannot tell from
  `projektbericht.html` whether it documents revision 1 or 7.

### 3.6 Multi-project/portfolio

- MCP: exactly one open project per server process, `project_open` replaces it
  and resets history (`mcp.md:44-51`).
- Workbench: one project at a time; "several open projects" is listed as
  missing ETS parity (`docs/manual.md:683-686`). Recent list holds 10 paths
  (`AppSettings.cs:19`, `docs/manual.md:560`).
- The only cross-project capability is pairwise compare
  (`McpServer.Tools.cs:229-235`; `WorkbenchWindow.Commands.cs:343-358`), and it
  ignores links/parameters (see D10). There is no portfolio index, no
  cross-project GA consistency check, no shared address book — relevant for
  installers maintaining several buildings.

## 4. Legal, data-protection and liability aspects

### 4.1 Customer data in projects, exports and backups

- `.knxproj` files routinely contain personal or personalisable data as free
  text: room/device/GA names ("Wohnzimmer Müller", "Praxis Dr. …"), comments
  and descriptions (`Model.cs:70-79`, `:98-108`). Every export listed in
  section 3.2 copies this data into plain files.
- Backups are byte-identical, unencrypted copies under
  `%LOCALAPPDATA%\BusChord\Backups`, up to 10 per identity, with the source
  path (which can contain site or customer names) in the manifest
  (`analysis/security-final.md:251-269`; `docs/project-backup.md:41-55`, `:96-97`).
  This is explicitly a data-handling gap for a tool used on customer
  installations; encryption was deliberately not built
  (`analysis/security-p3-followups.md:76-89`).
- App settings store the last 10 project paths in `%APPDATA%\BusChord`
  (`AppSettings.cs:19`) — a small residual trace after a project is deleted.
- Deletion: backups are pruned only on the next create and there is no
  "delete all" in the UI; exports have no retention concept at all. A customer
  who asks for erasure must find the files manually.

### 4.2 Credentials and keys

- The ETS project password is used transiently to read WinZip-AES archives and
  is never persisted (no secret fields in `AppSettings.cs:12-19`); encrypted
  projects cannot be saved or backed up (`docs/manual.md:539-551`,
  `Knxproj.cs:40-53`, `docs/project-backup.md:84-87`).
- The keyring model stores 16-byte keys as lowercase hex in JSON
  (`src/Knx.Bus/Security/Keyring.cs:116-125`, `:276-287`); the ETS XML reader is
  explicitly best-effort (`:139-143`). The keyring is not wired into the tunnel
  (`README.md:24`, `SECURITY.md:67-68`), and the Secure overview manages no keys
  (`WorkbenchWindow.Commands.cs:426-444`, `WorkbenchStrings.cs:513-521`).
  No keyring storage location is imposed by the product.
- A handover must therefore state: the keyring (`.knxkeys`) is *not* part of a
  BusChord package and secure commissioning is not supported.

### 4.3 Manufacturer data

- Product databases are manufacturer property, are kept outside version
  control and are not redistributed (`README.md:145-147`, `catalog.md`).
  The public-repo hygiene finding about tracked catalogue fixtures
  (`analysis/security-final.md:271-289`) is reported as fixed on `development`
  (`analysis/security-p3-followups.md:98`).
- Consequence for handover: exported device/product references are fine, but a
  package must not ship `.knxprod` files or extracted catalogue XML.

### 4.4 Export to third parties and liability

- Exports are plain files with no access control, no watermark and no
  provenance beyond the report's generator meta tag. Anyone who receives
  `projektbericht.html` or a CSV can read names, rooms and addresses; there is
  no "customer copy" vs "internal copy" distinction.
- No artifact states the known limits that a recipient must know: signatures
  are not recomputed, ETS round-trip is unverified (`README.md:33-34`,
  `docs/manual.md:711-725`), bus scan/descriptor frames are unverified live
  (`mcp.md:128-129`), write/download/secure commissioning are experimental or
  disabled (`SECURITY.md:58-77`, `McpServer.Tools.cs:276-283`, `:1005-1008`).
  A handover document that omits this can create liability for the installer.
- Licence/warranty: Apache-2.0, no warranty (`README.md:157`), and an explicit
  "no warranty for live bus actions" clause (`SECURITY.md:71-77`). BusChord is
  pre-release (`SECURITY.md:9-11`).

## 5. Verdict

**Not handover-ready. Partially usable as a component supplier, not as a
handover generator.**

What works today and can be handed over honestly:

- GA list with DPTs and links (CSV/HTML), device list, topology CSV.
- Printable HTML project report (title page, topology, GA table, com objects,
  link matrix, check findings) — print-to-PDF via browser.
- GA labels HTML; GA/device labels HTML/CSV via MCP.
- Semantic export (Turtle/JSON-LD) including the building tree.
- Local backups with SHA-256 verification and restore.

What is missing for a complete handover: room book, function matrix as a
document, parameter/signature report, test/acceptance protocol, document
revision and change log, operating instructions, contacts, data-protection
notes, one-command package with manifest/hash — plus the language and
reproducibility gaps that make "the same package" hard to regenerate (MCP
German-only, no timestamp/hash/version, BOM/line-ending inconsistency).

A building owner today receives, at best: four CSV files (GA, devices,
topology, associations; a fifth, the label CSV, only through MCP), one
printable HTML report, one label sheet and the `.knxproj` — assembled
manually, without room book, without acceptance evidence, without revision,
without any note about what the tool did not verify.

## 6. Prioritized backlog

### P0 — required before calling a handover "complete"

**H1. Room book export (native building tree).**
Export the `Locations` tree as part of the documentation: CSV and a report
section, plus an MCP tool. Include device counts, devices per room and a
"devices without a room" list.
*Acceptance:* `raum_buch.csv` and a report section exist with room path, kind,
floor, device (name/address/product) rows; devices outside the tree are listed
explicitly; German and English; deterministic order; a check in
`Knx.Buildings.Tests` or `Knx.App.Tests` fails if a room/device is dropped.

**H2. Handover package command.**
One action (workbench menu + MCP tool) writes a folder/ZIP with all artifacts
and a manifest (`MANIFEST.sha256`) naming the BusChord version, generation
timestamp, project GUID and revision.
*Acceptance:* one command produces report, GA/device/topology/associations
CSV, room book, labels, semantic export, findings and manifest; every manifest
hash matches the file; with a fixed timestamp/language the package regenerates
byte-identically; encrypted projects are refused with a readable message
rather than silently dropping artifacts.

**H3. Document revision stamp.**
Persist an explicit document revision (integer + optional change note + date)
in the BusChord extension namespace, shown on the report title page and in the
package manifest.
*Acceptance:* revision survives save/load/undo; report shows "Revision n";
incrementing is an explicit, undoable action; nothing outside
`urn:buschord:project:1` changes; compare of two revisions works through the
existing compare path.

### P1 — needed for a credible, verifiable handover

**H4. Change log.**
Persist the change feed (or generate it from compare) and export it as
CSV/HTML, included in the package.
*Acceptance:* entries carry timestamp and description; survive closing and
reopening the project; German/English; at minimum all mutations that
`OnProjectEdited` reports today are captured; the log states when it starts
(before/after the last persisted change).

**H5. Parameter/signature report.**
Export per-device `ConfigurationSignature` plus programming state when present,
clearly labelled by origin (`Demo` vs `Real`).
*Acceptance:* CSV/HTML with device, address, signature, needs-programming,
missing flags, drift, origin and last success; contains no keys or payloads;
states explicitly when states are demo-only or absent; included in the package.

**H6. Test/acceptance protocol.**
Generate a protocol document from project check + read-only bus scan +
recorder evidence.
*Acceptance:* expected vs found individual addresses, duplicates, missing and
unexpected devices, scan timestamp, origin (real/demo), telegram excerpt
reference; states the unverified wire-format caveat; never performs writes.

**H7. Labels: language fix and options.**
Fix `lang`/fallback title, expose layout options (columns/rows/copies/skip/
size), wire CSV and the merge data source in the workbench (or delete
`LabelMergeSource`).
*Acceptance:* English sheet is `lang="en"`; options reach the HTML; label CSV
and merge data source are reachable in both UI and MCP; device labels include
manufacturer/product where the catalogue provides them.

**H8. MCP export parity and reproducibility parameters.**
Add `associations` to `export_csv`, add `language` and `generated_at` (and
paper size) to report/label tools, unify encoding (BOM policy and line
endings).
*Acceptance:* MCP can produce the same association matrix as the workbench;
with the same language and timestamp, MCP and workbench exports are
byte-identical; a documented encoding policy is implemented and tested.

### P2 — completeness and duty of care

**H9. Owner documents.**
Templates for operating instructions, contact/maintenance page and
data-protection note, filled from project data (rooms, devices, GAs) with
placeholders for installer details.
*Acceptance:* generated per project in German and English; contains no
invented facts; the data-protection page names the personal-data classes in the
project, retention and deletion paths; marked as template output requiring the
installer's review.

**H10. Backup/data-handling durability.**
Delete-all backups, visible location/retention warning, optional backup next to
the project, a stated policy for encrypted projects.
*Acceptance:* UI exposes all three; the warning names the path and retention;
encrypted projects are either backed up with an explicit password prompt or
refused with a message that says why; documented in `docs/project-backup.md`.

**H11. Portfolio view.**
A read-only multi-project index for installers (folder scan: name, revision,
device/GA counts, last modified) and pairwise compare from it.
*Acceptance:* index lists projects without opening them; compare reuses
`ProjectComparer`; no cross-project mutation; performance bounded on 100
projects.

**H12. Provenance and signature transparency.**
Every package states: project SHA-256, BusChord version/commit, signature
status ("preserved, not recomputed") and the known unverified areas.
*Acceptance:* the statement is generated (not hand-written) and appears in
report and manifest; a check fails if the wording is removed.

## 7. Example handover package (target)

```
handover_<project>_<revision>_<date>/
  00_MANIFEST.sha256            # file hashes, tool version, project GUID, revision   [H2]
  00_README.txt                 # what this is, known limits, signature status       [H12]
  01_projektbericht.html        # printable; print to PDF from browser               [exists]
  02_gruppenadressen.csv        # GA + DPT + description                             [exists]
  03_geraete.csv                # devices incl. catalog name/order number            [partial, H1/H2]
  04_topologie.csv              # areas/lines/segments/devices                       [exists]
  05_zuordnungen.csv            # device/object -> GA matrix                         [workbench only, H8]
  06_raum_buch.csv              # floors/rooms/devices, unassigned list              [missing, H1]
  07_etiketten.html             # GA/device labels (language-correct)                [partial, H7]
  08_semantik.ttl / .jsonld     # linked-data export incl. building tree             [exists]
  09_pruefprotokoll.html        # check findings + scan + telegram evidence          [missing, H6]
  10_parameternachweis.csv      # signatures + programming state (origin labelled)   [missing, H5]
  11_betriebsanleitung.html     # owner operating instructions                       [missing, H9]
  12_wartung_kontakte.html      # installer/service contacts and intervals           [missing, H9]
  13_datenschutz_hinweise.html  # personal-data classes, retention, deletion         [missing, H9]
  14_projektverlauf.csv         # change log                                         [missing, H4]
  project.knxproj               # as-built archive (schema /23)                      [exists]
  project.knxproj.bak           # verified backup copy                               [exists locally]
```

Deliberately **not** in the package: `.knxkeys`/keyring, catalogue
(`.knxprod`) files, extracted manufacturer XML, local backup manifests, demo
JSON.

## 8. Evidence appendix

Claims in this report were verified against the tree at `08a0f6a`; the main
anchors are listed per section. Useful re-checks:

```powershell
# Tool surface (48 tools at this base; FEATURE.md still says 38)
dotnet run --project src/Knx.Mcp -- --list-tools

# Export/report/label surface
rg -n "ExportAsync\(|ProjectReport.Render|LabelCreator.ToHtml|ProjectCsvExporter" src/Knx.App
rg -n "export_csv|export_labels|report_export|project_compare" src/Knx.Mcp/McpServer.Tools.cs

# Missing handover artifacts (expect only definitions, no callers)
rg -n "LabelMergeSource|ExportDownloadedJson|new ReportWindow" src

# Language default and MCP gap
rg -n "Language" src/Knx.Core/Localization.cs src/Knx.Mcp
```

Caveats of this assessment: no live bus run was performed (read-only
assessment); the MCP/bus diagnostics' live wire behaviour remains unverified
as documented in `mcp.md:128-129`. Status statements reflect the code at
`08a0f6a`, not a running ETS round-trip.
