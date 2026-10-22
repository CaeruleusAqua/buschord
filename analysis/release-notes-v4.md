# BusChord Workbench V4 — consolidated release notes

Documentation only. This file adds no product, test, CI or documentation change; it
consolidates the state of the rewritten `development` line at the base commit below.

- **Base:** `7962a1a` ("Complete history rewrite", 2026-10-22), branch
  `agents/release/notes-2`, worktree `C:\projects\ai_work\agent-wt\release-notes-2`.
  Verified: worktree clean; the base is the local `development` tip; both removed fixture
  paths are absent from every commit reachable from the base (218 commits,
  `git rev-list --count`).
- **Scope:** the current Workbench state — 218 commits from `6562854` ("Initial public
  release of BusChord") to `7962a1a`. The remote `development` moved on during writing
  (head `646455c` at the last check): `32d014d` (plan record), `f53aa74` (offline
  localization CI gates), `f645495` (post-rewrite verification record,
  `analysis/git-history-rewrite-plan.md`), `7082f81` (catalogue signatures, backup
  location, import atomicity), `322c332` (plan records) and `646455c` (status matrix); all
  runs green per the GitHub Actions API (2026-10-22). Those later commits are not part of
  this snapshot.
- **Not claimed here:** ETS round-trip, real bus writes or downloads, secure commissioning,
  a DCA host, a real 150/200 % DPI or OS screen-reader run, and CI/CodeQL green beyond the
  runs verified via the GitHub API. See "Deliberate limits", "Open before release" and
  "Verification status".
- **Commit references** are rewritten-history SHAs, verified as ancestors of `7962a1a`.
  The completion plan and the earlier reports still cite pre-rewrite SHAs; Appendix A maps
  them. Where the plan names an agent-branch SHA, the integrated commit is used:
  `c84cb29` → `595d1b4`, `33ee3dc` → `132902b`, `7b80741` → `769afcb`.

## 1. Workbench shell and UI

- The product UI is the native `WorkbenchWindow`. The legacy `MainWindow`, `GroupLinksPane`,
  unused legacy panes and the Dock package dependencies were removed; the Docking and Shell
  suites now run against `WorkbenchWindow` (`3b3dbdd`, `52a4ff5`, `6924532`, `4cbef76`,
  `46dc06a`).
- Shell layout: fixed zones, three navigator perspectives, work-item tabs with independent
  filter/selection/expansion/scroll state, optional inspector and lower tool area,
  GridSplitters, per-project layout persistence and focus/reset layout (`0b63b98`, `ac01f98`,
  `6dc66e6`, `6ddb2cb`).
- Inspector as property grid with sections, sticky footer actions and focus/caret return;
  the narrow overlay at 1024/900 keeps covered editor stops out of the tab order (`ac01f98`,
  `8e0e888`; `Knx.WorkbenchUiState.Tests`).
- Object table: per-row selection checkbox, tri-state select-all, GA chips, row context menu
  and keyboard equivalents; the 10,000-object virtualization path stays tested (`8e0e888`).
- Device drag-and-drop onto a line/segment and table drop with Undo and room retention, plus a
  keyboard path via the context menu (`ac01f98`; OS-pointer drag is not provable headless and
  is documented as a limit).
- Favorites/pinning and read-only side-by-side companion windows with dedup, refresh and
  close lifecycle (`ac01f98`, `8e0e888`, `60eb5d8`).
- Unified V4 dialog family: shared chrome, primary action last, quiet cancel, `wb-danger` for
  destructive actions, no destructive default focus (`36b9c1a`). Restyled SearchPane
  (`6ffd2f4`, presenter fix `76fdae6`), ParameterPane (`6ffd2f4`, `60eb5d8`) and CatalogPane
  filter row (`6ffd2f4`).
- Scrollable, localized help window with six sections, F1/Escape (`8e0e888`).
- Narrow-window fixes: catalogue column layout at 900×600 and initial focus in Help, New
  Project and Replace Product (`4c5baf8`).
- Design-token and visual audit (`34d20a7`), accent/active navigation states (`1179f13`),
  selected-row/disabled/quiet tokens (`cade3d2`), muted/disabled contrast (`cc86096`), a theme
  contrast assertion (`18c00f1`) and active-perspective/hover contrast (`cb9adef`).
- Frameless shell (no native title bar): design and Avalonia API evidence only
  (`132902b`); implementation is open (§ Open before release).

## 2. Project, topology and group addresses

- Atomic, undoable topology and group-address operations (`6497d94`).
- Navigator/tree operations: create, rename, delete, move, copy, address change and context
  menus (`7d1cf06`); device, line and segment moves (`ac8db3a`); keyboard expand/collapse
  (`693fee5`).
- Validated "new group address" form (`c79bf76`); new group addresses are linkable with the
  correct DPT bit size (`a40492c`).
- Multi-assignment with frozen selection, preview (already linked / ready / incompatible),
  duplicate handling and one Undo; incompatible batches are rejected as a whole (`7d40c17`,
  `ac01f98`). Sending address is set explicitly; removing it warns when other links remain
  and never picks a silent replacement (`ac01f98`).
- Project compare and product replacement reachable from the shell (`8e751e5`); replacement
  is transactional and failed replacement keeps all sessions (`6dc66e6`, `c12f051`).
- Local project backup and recovery: service (`3a2c003`), backup before save/on demand
  (`33ac0e0`), list and restore flow (`a526527`), connector-signature and recovery fixes
  (`3108fb4`).
- Project check extended with topology, range and reference rules (`100d7a9`); prefixed ETS
  group-address references are resolved on import (`eaa4263`).
- Demo project JSON export/import with strict validation and loss protection (`4b19472`,
  `4ce1eda`, `443d9b5`); demo data is kept separate from native KNX data.

## 3. Catalogue and parameters

- Catalogue application status and gating: broken/incomplete applications block insert and
  parameter editing, DCA/plugin applications are marked and read-only, "third-party software
  required" is a hint only (`3900b03`, `67c3cba`). Real device downloads stay behind a
  safety gate (`e774380`).
- Catalogue target, search and import flows: insert into the selected room/line/segment with
  address suggestion, name-conflict handling and Undo; import progress, errors, cancel and
  background loading; search over product, manufacturer, program, communication object and
  DPT (`72fff24`, `2613652`).
- Persistent catalogue metadata index and lazy program models (`9bfcf9f`, `0a5ab84`); see
  "Performance".
- Parameter UI: sections, sticky footer, draft/read-only states and project-switch notices
  (`77da584`); footer validation and error aggregation (`dd78b9e`); shared editing sessions
  with native change review (`044b34b`); draft warning and draft preservation across
  selections (`c12f051`, `61d0dcb`); demo parameter form aligned with the native pane
  (`0f24378`, `60eb5d8`).
- Parameter editor scope: number, float, text, enum and boolean are editable; other types
  appear read-only with a reason. Dynamic manufacturer logic (Choose/When/Assign, modules)
  is detected and reported as a warning but not evaluated, so the displayed row set is not
  the runtime-active parameter list (manual §6).
- Changed values persist as BusChord overrides in the `urn:buschord:project:1` namespace;
  whether ETS reads them as device parameters is unverified (manual §6.1).

## 4. Monitor, programming and diagnostics

- Session model: independent monitor/programming/scan sessions with cancellation, monitor
  pause/resume and project diagnostics (`551e7ce`, `62d07a7`, `e047af2`); monitor controls and
  tree operations (`7d1cf06`); structured monitor/programming tables (`4a7a0ec`); simulated
  value read/write dialog (`34cf2db`); simulated line scan, change history and problem list
  (`40b274d`, `c0a3951`); external-channel/monitor-origin/programming-demand surfacing
  (`da7c713`); programming-view reparenting crash fixed (`9231fc7`); demo failure/retry path
  (`49b14b7`).
- Monitor: start/stop, pause, clear display, group/bus mode, four filter scopes, filtered and
  full CSV export, last 500 rows displayed with the full recording kept separately (manual
  §7.1).
- Programming queue: frozen device state per job, demo execution, failure simulation, retry,
  cancel, history and programming demand versus the last simulated success (manual §7.2). A
  job with origin `Real` is rejected because no executor is injected (`ProgrammingSession`).
- All three tools carry explicit simulation badges; the Workbench opens no bus connection.
- Bus stack fixes (`f3e9468`): cEMI group flag and hop count, APCI/payload octet offsets,
  `L_Data.con` separated from the telegram feed, group-response matching for reads,
  descriptor-read encoding and multicast routing (join `224.0.23.12`, bind 3671). After the
  fix, read-only tunnel and routing were checked live, including timeout and confirmation
  filter behaviour; the write path stays blocked. No group-response or descriptor live scan
  and no second-tunnel conflict test were run (H8.4).
- Secure overview counts devices and group addresses with explicitly declared secure
  capability; it manages no keys or certificates (manual §7.5).

## 5. Reports and exports

- CSV exports (group addresses, devices, topology, object assignments), HTML project report,
  label sheet, semantic Turtle/JSON-LD exports and compare CSV (`e8ec200`, `8e751e5`,
  `51223a6`; manual §9.2).
- Project-wide search and replace with preview, stale protection and Undo (`51223a6`).
- MCP server (50 tools) offers the same exports plus `export_labels` (CSV or combined HTML),
  `project_check` as JSON and `group_address_import_csv`; the Workbench itself has no GA CSV
  import and no label CSV export (manual §9.2, §9.5; `mcp.md`).
- CSV hardening: one shared escaper neutralizes `= + - @`, tab/CR and leading spaces in all
  six export paths, with regression tests (`0854088`, `src/Knx.Core/Csv.cs`).
- Localization of project-check findings, CSV headers and the HTML report (`223b3d2`), and of
  the remaining CSV/Owl messages plus the associations CSV body values (`5159f01`). Remaining
  gaps (recorder/monitor CSV headers, label sheet `lang`, export file names) are listed under
  "Open before release".

## 6. Localization and accessibility

- German is the default; English is switchable via `--lang=de|en` and Settings. The live
  language switch rebuilds menus, rail, view/tool tabs, tooltips and automation names without
  restart (`45851b2`); help body and floated tool titles still need a reopen.
- Multilingual groundwork (design/inventory/workflow, no resource migration yet):
  architecture (`d5c7f66`), text inventory (`59cc71b`: 908 dictionary entries, no duplicate
  or empty values, no placeholder mismatches, 155 dead keys, 4 key-name collisions) and
  translator workflow (`a18c652`).
- Accessibility fixes verified in the A11y reports and final QA: focus restore after tab,
  representation and close; palette keyboard path; ConfirmDialog focus/Escape; live status
  and footnote names; native `TabItem` role and selected status for tabs (a full UIA
  `SelectionItemPattern` is not exposed); field/row automation names; minimum window size
  900×480; layouts at 1440×900, 1024×700 and 900×600 with no unscrollable clips
  (`74601a6`, `9aaf6d2`, `769afcb`).
- Measured contrast before/after:
  - disabled text `1.74:1` light / `2.31:1` dark at `ac01f98` → rendered `4.74:1` light /
    `5.47:1` dark at HEAD, asserted by `Knx.WorkbenchTheme.Tests` (`cc86096`, `18c00f1`;
    `769afcb`);
  - active perspective `4.24:1` → `5.43:1` (`WbSelectedText` on `WbTint`, asserted;
    `cb9adef`);
  - tooltips `14.12:1` light / `12.20:1` dark (`workbench-v4-a11y-final.md`).
- Open P3 accessibility/consistency items: `.muted` 10 px vs 11 px, mixed 30/26 px toolbars,
  catalogue filter buttons next to 30 px fields, label hierarchy, quick dialogs still on
  legacy window chrome, clipped English inspector caption, reduced UI-level coverage in
  `Knx.Shell.Tests`, and floating tool windows not routing Ctrl+Z/Ctrl+Y (`769afcb` §5).

## 7. Performance

Catalogue load and search were measured at `595d1b4` (report
`analysis/catalog-load-performance.md`) and reworked in `9bfcf9f` and `0a5ab84`.

| Metric (scenario) | Before | After | Evidence |
|---|---|---|---|
| First search keystroke, 4-program split catalogue | 2.4–4.8 s UI freeze, 559 MB allocated | first DPT search <1 ms (plan record) | `595d1b4`; plan progress |
| Warm start, real library | 55–66 s (plan record) | 0.8–1.6 s (plan record) | plan progress (`9bfcf9f`, `0a5ab84`) |
| Warm start, synthetic 124-file / 401.5 MB library | 1.2–1.9 s (11.2 s cold) | index-based warm path (SHA-256 revalidated) | `595d1b4`; `9bfcf9f`, `0a5ab84` |
| Cold index build, 124-file library | no index existed; every start re-parsed | 113.6 s, progressive instead of blocking (plan record); target <3 s open | plan H5.7 |
| One program part parse | 0.6–1.9 s, 140 MB transient | materialized lazily on `FindProgram`/`FindProgramFor` only | `595d1b4`; `9bfcf9f`, `0a5ab84` |
| Warm search, 1,000 products | 38 ms / 20.7 MB per keystroke | not re-measured | `595d1b4` |
| Parameter editor open | 276–387 ms / ~46 MB plus program parse | 0.6–1.9 s (plan record, lazy program parse included); target <150 ms open | `595d1b4`; plan H5.7 |

- The index (`9bfcf9f`, `0a5ab84`) is a gzip-JSON metadata index with file identity, SHA-256
  revalidation, atomic writes and a schema version; search runs over index metadata instead
  of resolving application programs; program models stay lazy and are restored from the
  index. Algorithmic work: dictionaries for products/programs, single master read,
  single-pass translations, no `XElement` clone, bounded parallel warm-index reads.
- Caveats: the standalone report measured a synthetic library matching the real library's
  shape, and its absolute numbers varied up to ~2× on a busy machine. The 55–66 s and
  9.33 s figures exist only in the plan's progress record. H5.7 acceptance targets (cold
  start, warm start, first search, memory, 10,000-object path, cache invalidation after
  import/remove/change) remain open.

## 8. Security and data handling

- Independent review at `cb9adef` (`a459abc`, `analysis/security-final.md`): no P1; three P2
  and seven P3 findings. The three P2 findings are fixed before release:
  - **CSV formula injection** in all six export paths → shared escaper with regression tests
    (`0854088`, H10.1);
  - **catalogue parser limits** — recursion depth cap 64, XML/archive size limits, tested
    against a 260 MB archive bomb (`9bfcf9f`, H10.2);
  - **cache trust** — SHA-256 revalidation instead of mtime+size, hex archive names, atomic
    writes, no absolute paths adopted from the cache, lazy-model restore (`9bfcf9f`,
    `0a5ab84`, H10.3).
- Fixture/provenance hygiene (`b614604`, H10.5): the tracked 1 MB KNX master file and a
  GPL-licensed third-party sample project were removed and replaced by synthetic fixtures
  with a provenance README.
- Verified clean by the review: no ZIP-slip or path traversal in catalogue handling (archive
  nesting capped at four levels), no external entity resolution (XXE), project password used
  only locally with a constant-time verifier and HMAC, project archive limits (128 MiB
  archive, 64 MiB entry, 256 MiB total, 4096 entries, 16 MiB XML, depth 128), no plug-in
  loading anywhere, escaped HTML reports/labels, validated backup/restore containment and
  atomic replacement, bounded demo-JSON import (4 MB, depth 12, strict key sets), and no
  secrets or credentials in tracked files.
- Remaining P3 security items (open): DCA read-only fallback for project-XML applications
  without a catalogue (SEC-4/H10.4); the unused `CatalogUse.Download` gate and the legacy
  raw download API (SEC-5/H10.6); plaintext backups in `%LOCALAPPDATA%` kept indefinitely
  (SEC-9/H10.6); non-atomic library import without signature check (SEC-8/H10.6). SEC-10
  (repository history) was rewritten and published (H10.7, see below).

## Deliberate limits

These are documented decisions, not open bugs. None of them is claimed as working anywhere
in this release.

- **Real bus writes and downloads.** No download or device programming path is wired. The
  download writer is gated (`SafeDownloadImplemented = false`), the legacy
  `DownloadAsync`/`FileProgram` methods have no caller, no real executor is injected into
  the programming sessions, and real jobs are rejected. MCP `bus_read`/`bus_write` exist but
  their wire behaviour is not verified against real hardware; only read-only bus actions
  were performed (discovery, tunnel handshake/heartbeat, one group read, passive listen).
  The cEMI write encoding was corrected (`f3e9468`) but no write was sent.
- **DCA/plug-in host.** DCA/plugin applications are detected and read-only; no in-process
  plug-in host exists, no plug-in code is loaded or executed, and download is unsupported.
  Opaque manufacturer data is preserved, not rewritten. The product path for the read-only
  reason (H3.1/H3.2) is still open.
- **Secure commissioning.** Keyring and secure formats are a foundation only, not wired into
  the tunnel; the Secure overview manages no keys or certificates; secure download and
  FDSK/key operations are out of scope.
- **Multi-project.** One project at a time; no multiple simultaneously open projects or
  multi-project tabs. Recent-project list is capped at ten, layout state is per project.
- **USB/RDAC.** Not supported; KNXnet/IP (discovery, tunnel, routing) is the only transport.
- **Cloud/IoT.** No cloud or IoT connectors; Hawk/IoT device images remain open.
- **RTL and third language.** Only German and English; no resource files, no plural/format
  infrastructure, no third language. `FlowDirection` is never set and RTL is untested; the
  planned pilot language (French) has not started.
- **Real 150/200 % DPI and screen reader.** Validated only as DIP layout at 96 dpi (100 %)
  plus 1.5×/2× raster captures; no real high-DPI or mixed-DPI monitor run and no
  Narrator/UIA client run. High contrast/forced colours, Windows text-size scaling, RTL and
  IME were not tested.
- **Further documented limits.** No ETS round-trip proof (archive signatures are preserved
  but not recomputed, so changed signed data may fail ETS validation); no dynamic
  manufacturer logic evaluation; no online catalogue/market/signature check; no full ETS
  parity.

## Open before release

Plan work packages that were still open at the base (checkboxes in
`analysis/workbench-v4-completion-plan.md`).

- **H4.1–H4.9 (acceptance and documentation).** R01: link the ETS-REA evidence reports and
  correct the matrix status. R02: map every `src/app.js` action to a native
  implementation/test or a deliberate limit. R03: implement or document all B1–B8 points.
  R04: real focus/tab order, screen-reader names, 900×600/1024×700/1440×900 and simulated
  150/200 % DPI; real Narrator/UIA and mixed-DPI remain platform limits until run on real
  hardware. R05: automate the English check (keys, German leaks) and add a `--lang=en`
  selftest/screenshot. The leaks found by the QA report (project-check findings, assignments
  and topology CSV headers, HTML report) were localized by `223b3d2`/`5159f01`; still open
  are the recorder CSV header (German), the monitor CSV header (English), the label sheet
  `lang="de"` and its German fallback title, the German export file names, and the English
  `ProjectOperations`/session/backup status texts. R06: catalogue/parameter/relations/dialog
  design language with screenshots light/dark at 1440/1024/900. R07: fix or explicitly
  decide every remaining P1/P2 from the tokens, workbench, panes and A11y reports. R08:
  Release build, all test projects, smoke, screenshot selftest, Workbench selftest de/en,
  remote CI and CodeQL green with local and remote SHAs documented. R09: bring the status
  matrix up to date.
- **H6.2–H6.5 (frameless shell).** The design is done (`132902b`); the topstrip, window
  actions, accessibility (Alt+F4/Alt+Space, screen reader, high contrast), dialog decision
  and real Windows acceptance (100/150/200 % DPI, multi-monitor, Snap) are not implemented.
- **H8.4 (bus read acceptance).** Reproducible read-only results, timeout/reconnect and
  interface-conflict behaviour; no group-response or descriptor live scan and no second
  tunnel were tested. A write approval would be separate and explicit.
- **H9.4–H9.6 (localization infrastructure).** CI gates for key parity, orphaned/empty keys,
  placeholders, forbidden German literals in `en` and pseudo-localization; the
  third-language pilot; and the boundary rules (project/device names, manufacturer text and
  KNX codes stay data, not UI language).
- **H10.4/H10.6 (security decisions).** DCA read-only fallback for project-archive
  applications (SEC-4); release decision or fix for the remaining P3: plaintext backups,
  `CatalogUse.Download`, non-atomic import and ignored `.signature`.
- **History rewrite residuals (H10.7, executed and published).** The rewrite (filter-repo
  with path removal, identifier replacement and mailmap; mirror backup
  `C:\projects\ai_work\buschord-history-backup-20261022-061415.git`) removed both fixture
  paths from all commits; `development` and `main` were force-pushed and the protection
  rules restored. Open: the GitHub purge — `refs/pull/1/head` (`2741036`) still exposes the
  old public lineage and both fixture blobs are still served by the blob API, so a GitHub
  Support request and the record's acceptance checks are needed (no forks exist; remote
  branches are `development`, `main`, `cla-signatures` plus the PR ref); rotate the GitHub
  token mentioned in chat (the fixture credentials come from the published upstream sample —
  rotate/rekey only where reused; no reuse evidence); send the contributor notice; and the
  local cleanup (reset local `main`, decide the 163 old branches and the `pre-publish-backup`
  tag, move or delete the pre-public bundle, expire reflogs and gc). Details and open
  decisions: `analysis/git-history-rewrite-plan.md` §6–§10.
- **Remote CI.** `development`: CI and CodeQL completed successfully for the force-pushed
  line (`6771031`, `7962a1a`) and for every follow-up commit up to `646455c` (verified via
  the GitHub Actions API, 2026-10-22). `main` (`49feddd`): a CI run triggered by the
  force-push failed
  the console checks on both OS jobs while CodeQL passed; the test sources at that commit
  still reference the two fixtures removed by the rewrite (the pre-rewrite `main` `c48e954`
  was green). Decide whether `main` must be green or accept the early-snapshot state. The
  plan's H7 checkboxes (push, plan closure, worktree cleanup) are still unchecked.
- **Other open plan items.** H1.4 (final shell chrome acceptance), H2.3 (remaining English
  strings), H3.1/H3.2 (DCA read-only product path and regression test), H5.7 (catalogue
  acceptance targets), and plan steps 3–6, 8 and 13.

## Verification status

- **Local Release gate, green at `cb9adef`** (independent QA, `769afcb`): build 0 errors /
  21 pre-existing warnings; 24 console test projects; native Docking (15), Shell (49) and
  ParameterUi `--native` (18) suites; MCP selftest (22); app smoke (53); screenshot selftest
  (6); Workbench self-check 83 checks in each of de/light, de/dark, en/light, en/dark. No P1
  reproduced; two P2s (catalogue list at 900×600, missing dialog initial focus) were fixed in
  `4c5baf8` without a recorded re-run of the external harness.
- **Focused suites after `cb9adef`**, as recorded in the plan: catalogue/DCA/Workbench suite
  (`9bfcf9f`), bus tests (`f3e9468`), CSV regression tests (`0854088`), shell language
  switch de→en→de (`45851b2`). No full independent re-run of the complete gate is recorded
  for the later commits.
- **Remote CI/CodeQL** (verified via the GitHub Actions API on 2026-10-22): CI and CodeQL
  completed successfully for the rewritten `development` at `6771031` and `7962a1a`, and for
  every follow-up commit through `646455c`; the pre-rewrite `development` runs for `ae78907`
  and `27f4797` were green as well. The `main` run for `49feddd` failed the console checks
  (see "Open before release").
- **History rewrite, verified for this report:** the old 1,082,494-byte
  `tests/Knx.Core.Tests/knx_master.xml` and the 394,270-byte
  `tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj` appear in no commit
  reachable from the base (`git log --full-history` empty; only the 2,290-byte synthetic
  master file added in `b614604` remains). The rewrite also ran `--replace-text` and
  `--mailmap`: the real IPs and MACs from the sanitize diffs return 0 hits in the rewritten
  line (`git log -S`; the remaining matches are the documentation-range and placeholder
  values already in the tree), and the pushed line keeps its pre-rewrite author identities
  (agent pseudonyms, one committer identity) — the personal identity and the pre-public
  lineage were never part of it. Old blobs and pre-rewrite objects remain in the local
  repository's pre-rewrite refs (agent branches; local `main` is still `2904460`), in the
  pre-public bundle and in any pre-rewrite clone; the mirror backup holds the rewritten
  branches. The post-base record `analysis/git-history-rewrite-plan.md` (`f645495`) has the
  full verification; its §5 finds `refs/pull/1/head` (`2741036`) still exposing the old
  lineage on GitHub, and both fixture blobs still return HTTP 200 from the blob API
  (re-checked here). The plan text's "219 commits" counts the mirror's refs; the base line
  is 218 commits.
- **Real hardware:** only read-only bus actions were executed; no write, download, restart,
  address write, secure commissioning or key operation. No real screen-reader, 150/200 % DPI,
  high-contrast or ETS round-trip run exists.
- **Documentation base:** `README.md` and `docs/manual.md` are checked against `5183a0f`
  (pre-rewrite SHA; rewritten `cb9adef`) and predate the live language switch, the catalogue
  index, the cEMI fix and the CSV hardening.

## Sources

Required sources read for this report: `analysis/workbench-v4-completion-plan.md`,
`analysis/qa-final.md`, `analysis/catalog-load-performance.md`,
`analysis/knx-bus-read-test-plan.md`, `analysis/security-final.md`,
`analysis/i18n-architecture-plan.md`, `analysis/i18n-text-inventory.md`,
`analysis/i18n-translation-workflow.md`, `README.md`, `docs/manual.md`. Additional sources:
`analysis/release-hygiene.md`, the rewritten git history of `development`, the GitHub
Actions, refs and blob API (`CaeruleusAqua/buschord`), `git ls-remote`, and the mirror
backup at `C:\projects\ai_work\buschord-history-backup-20261022-061415.git`. Beyond the
base: `analysis/git-history-rewrite-plan.md` (post-base commit `f645495`) and the follow-up
commits `32d014d`–`646455c`.

## Appendix A — SHA map (pre-rewrite → rewritten base)

The completion plan and the earlier reports cite pre-rewrite SHAs; every pair below was
verified by subject against the rewritten history. `49feddd` and `6771031` are unchanged.
Commits without a pre-rewrite counterpart: `7962a1a` (the base) and the post-base `32d014d`,
`f53aa74`, `f645495`, `7082f81`, `322c332`, `646455c`.

`082c759`→`ac8db3a` · `0ffdaab`→`6497d94` · `125c17a`→`0c3afa8`
`171ebca`→`b614604` · `1c94f74`→`cdc9228` · `1f22967`→`dd78b9e`
`26f596f`→`4c5baf8` · `27f4797`→`caabd4b` · `2846c16`→`223b3d2`
`286c025`→`6ddb2cb` · `2904460`→`6562854` · `2a4a7bc`→`c12f051`
`2bce913`→`74601a6` · `33612e0`→`0a5ab84` · `33ee3dc`→`132902b`
`35e9804`→`c79bf76` · `35f8ae8`→`9231fc7` · `36af588`→`67c3cba`
`39be758`→`6ffd2f4` · `3ef6093`→`49b14b7` · `4012716`→`e047af2`
`43cff3c`→`a40492c` · `45f035b`→`cc86096` · `49cbf7a`→`9aaf6d2`
`49f3f22`→`9bfcf9f` · `4c8426c`→`1179f13` · `4e91814`→`e774380`
`5183a0f`→`cb9adef` · `52598a4`→`7d40c17` · `527d1bc`→`595d1b4`
`54ecc4a`→`a459abc` · `55ff6e1`→`b74aba2` · `59d0f74`→`0f24378`
`5b7bb25`→`59cc71b` · `62e1af9`→`4cbef76` · `642fda6`→`4a7a0ec`
`65c253d`→`0854088` · `664fa5b`→`7d7302b` · `6b3fb83`→`60eb5d8`
`6eb21ae`→`4ce1eda` · `6ebe403`→`3108fb4` · `7458743`→`100d7a9`
`77d7843`→`77da584` · `7b80741`→`769afcb` · `84cb150`→`8e0e888`
`893f3a3`→`2613652` · `8b2b678`→`f3e9468` · `936c5a4`→`6dc66e6`
`9408de0`→`4b19472` · `95f842b`→`132902b` · `9639733`→`eaa4263`
`977c420`→`72fff24` · `a3afc25`→`cade3d2` · `a67917a`→`044b34b`
`a8aba25`→`45851b2` · `aa83bc4`→`3900b03` · `ab0f8a4`→`76fdae6`
`ac0480a`→`ac01f98` · `ae78907`→`a66e629` · `af8652e`→`551e7ce`
`afbcce8`→`34cf2db` · `b2de7cb`→`3b3dbdd` · `b7a2b79`→`51223a6`
`b82859c`→`3a2c003` · `ba25051`→`5159f01` · `bb75766`→`8e751e5`
`bc8e5bb`→`7d1cf06` · `be7d67c`→`61d0dcb` · `bfcca2c`→`46dc06a`
`c31e0b0`→`693fee5` · `c48e954`→`49feddd` · `c5b7232`→`e8ec200`
`c772a7f`→`443d9b5` · `c84cb29`→`595d1b4` · `cb7e996`→`a18c652`
`d0d34e2`→`769afcb` · `d80ea42`→`c0a3951` · `d902089`→`0b63b98`
`da128e5`→`d5c7f66` · `e2f6136`→`6924532` · `e56d8c7`→`da7c713`
`e6efeda`→`34d20a7` · `e8d83b5`→`aefde1d` · `f065b96`→`33ac0e0`
`f168514`→`62d07a7` · `f274810`→`52a4ff5` · `f605870`→`36b9c1a`
`f805d61`→`40b274d` · `f9d0e7f`→`18c00f1` · `fc9d1ea`→`a526527`
`fd0729d`→`8d643a6`
