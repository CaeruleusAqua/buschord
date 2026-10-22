# Final integration review - base `0226a33`

> **Post-fix status at `c333937`:** **approve with fixes**. The acceptance protocol,
> project revisions and portfolio UI are now merged, and the download/cache/localization
> fixes are present. See the addendum at the end; the block verdict immediately below is
> the historical `0226a33` review and is kept for traceability.

Read-only review. The only change in this branch is this document; no product,
test, CI or existing report was edited.

- **Base:** `0226a339590d778cff6eec9b996dad7b142f2ab4` ("Focus the compare report's first row"),
  branch `development`, verified as HEAD of a dedicated worktree.
- **Review branch/worktree:** `agents/review/final-integration` in
  `C:\projects\ai_work\agent-wt\final-integration-review`.
- **Range actually reviewed:** `769afcb..0226a33` (the independent QA pass plus the 33
  commits after it). Pre-QA context inspected where the plan still relies on it:
  `9bfcf9f`/`0a5ab84` (catalogue index), `0854088` (CSV hardening).
- **Why not `fd0729d..HEAD`:** `fd0729d` is a pre-rewrite commit and is **not an ancestor**
  of `0226a33`; the rewritten equivalent is `8d643a6` (same subject, different tree/SHA).
  Using the old SHA as a range boundary would include the whole 245-commit rewritten
  lineage and none of the intended diff.
- **Build:** `dotnet build ets6.sln -c Release` -> 0 errors, 27 warnings (obsolete Avalonia
  APIs and nullable notes; no new build failure).
- **Tests run (all green):** Core, Bus, Catalog, Mcp, Localization, Parameter,
  ParameterUi (both modes), WorkbenchUiState, WorkbenchTheme, Workbench, WorkbenchSessions,
  WorkbenchPerformance, Archive, Address, Linking, SmartLink, CatalogUi, Icon, Monitor,
  Diagnostics, Search, Buildings, Encryption, Owl, Docking, Shell and `src/Knx.App.Tests`;
  `src/Knx.App --smoke`, `--screenshot-selftest`, `--workbench-selftest` de/light and
  en/dark; `src/Knx.Mcp --selftest`.
- **Working tree:** clean. No secrets, tokens, private keys, manufacturer binaries or
  customer project paths were found in the changed files. Remaining local-path hygiene is
  noted under P3.

## Verdict

**BLOCK for the claim that `0226a33` is the final integration wave.** The code that is in
the base builds and the full console sweep is green, but two of the named implementation
deliverables (acceptance protocol, project versioning) are not in the base at all, and the portfolio
work exists only as a core registry with no UI. If the wave is explicitly declared partial,
the implemented base would be **approve with fixes** after the P2 items below; it must not
be presented as complete.

## P1 - must be resolved before calling this wave final

### P1-1 Acceptance protocol, project versioning and portfolio UI are absent from the base

- `analysis/workbench-v4-completion-plan.md:505` keeps H11.5 (Abnahmeprotokoll) open;
  `:515-519` keeps H11.8 (Portfolio/Multi-Projekt, Versionsvergleich) open.
- `agents/impl/acceptance-protocol` -> `7b73631` is **not** an ancestor of `0226a33`;
  `src/Knx.Reports/AcceptanceProtocol.cs` does not exist in the base.
- `agents/impl/project-versioning-2` -> `9e5c41e` is **not** an ancestor;
  `src/Knx.Core/ProjectRevisions.cs` and `tests/Knx.ProjectCompare.Tests` do not exist.
- `agents/impl/portfolio-ui` points at `e2e0061`, which is the **core registry commit
  already in the base**; there is no `Portfolio` reference in `src/Knx.App` (grep count 0),
  so the plan's statement that UI wiring "läuft als `agents/impl/portfolio-ui`" is not
  backed by a branch. The only portfolio code is
  `src/Knx.Core/Portfolio/PortfolioRegistry.cs` plus tests.
- **Action:** integrate and review `7b73631` and `9e5c41e`, and either integrate a real
  portfolio UI or change H11.8 to an explicit deferred item. Re-run the focused suites and
  record the new SHAs in the plan; do not label the base final before that.

## P2 - correctness/security gaps in the implemented base

### P2-1 The download gate does not bind the planned write addresses to the session

`src/Knx.Bus/DownloadSession.cs:102-108` checks only plan readiness, target device and the
SHA-256 of the image. `:125-133` then writes at `_program.StartAddress` and
`_program.ChunkSize`, ignoring `plan.StartAddress`/`plan.ChunkSize`. The dry run that the
operator is asked to confirm prints those plan values (`src/Knx.Bus/DownloadPreflight.cs:131`,
`:245-246`) and the XML contract explicitly says the dry run covers "image and target
addresses" (`DownloadPreflight.cs:61`). A plan built from a program with the same image bytes
but another start address or chunk size therefore reports ready, matches the hash, and
flashes to addresses the operator never reviewed. In addition, `DownloadSession.RunAsync`
does not check `KnxBus.WritesEnabled`; only `KnxBus.DownloadAsync` does (and that path is
unreachable). Any caller with a tunnel and a ready plan can write.

**Action:** reject a plan unless `plan.StartAddress == _program.StartAddress`,
`plan.ChunkSize == _program.ChunkSize` and the plan's device address is the session device;
also enforce the read-only policy in `RunAsync` (or make the write session internal to the
gated API). Add a negative test for both mismatches.

### P2-2 Cache-derived source paths are trusted verbatim

`src/Knx.Core/Catalog/CatalogIndex.cs:612-616` reads `SourceFilePath` from the cache document
and `:631` keeps it for loose-XML catalogues. `src/Knx.Core/Catalog/CatalogIndex.cs:651`
adopts `Parts[].Location` without checking it against the catalogue directory, and
`src/Knx.Core/Catalog/KnxProductCatalog.cs:541` opens `source.FilePath` if it exists. A cache
document is therefore able to point the parser at an arbitrary local XML file. This directly
contradicts the completed H10.3 claim "keine absoluten Pfade aus dem Cache übernehmen"
(`analysis/workbench-v4-completion-plan.md:459-460`). The stated threat model (same user can
write the cache) limits impact, but the path should still be treated as untrusted input.

**Action:** for loose XML, ignore the cached file path and derive it from `path`; for parts,
accept only locations that resolve inside the catalogue/archive directory (reject rooted paths
and `..`). Add a poisoned-cache regression test.

### P2-3 Published commit metadata no longer matches the rewrite verification

`git log --format='%an <%ae>' 0226a33` shows 13 author identities, not the single
`CaeruleusAqua <CaeruleusAqua@users.noreply.github.com>` claimed in
`analysis/git-history-rewrite-plan.md:179`. In particular the post-rewrite parameter-open
commit `52bdb52` is authored by `scholle <scholle@aurora-robotics.de>` and `b08f42b` by
`assessment-agent <assessment@localhost>`. Those commits came after the rewrite tip
`7962a1a`, so the verification table was true for its stated scope but is no longer true
for the base. `origin` is the public GitHub repository; the company address is published
metadata.

**Action:** either rewrite/mailmap the two post-rewrite identities before the next push, or
update the verification record and explicitly accept the company address. Check the live
remote tip as well; see "Not verified".

## P3 - actionable but not release-blocking

### P3-1 Localization gate knowingly allows a raw key in the UI

`src/Knx.App/WorkbenchWindow.Commands.cs:636` calls `W("LinkProjectChanged")`, which resolves
against `WorkbenchStrings`, while the key lives in the core map
(`src/Knx.Core/Localization.cs:137`). The gate records this as a known exception
(`tests/Knx.Localization.Tests/exceptions.txt:74-76`), so the batch-link preview can show the
literal `LinkProjectChanged` if the project changed while the dialog was open.

**Action:** call `Strings.Get("LinkProjectChanged")` at this site (or add the key to
`WorkbenchStrings`) and remove the exception entry. This is a one-line fix and a good
shrinking step for H2.3.

### P3-2 SEC-4 is implemented but the security report still says the gap is unchanged

`analysis/security-p3-followups.md:7-16` says the archive DCA/broken fallback is "not fixed"
and the gap is "unchanged". The base implements it:
`src/Knx.App/MainWindowViewModel.cs:737-742` (read path), `:750-771` (archive lookup and
cache), `:784-801` (Broken/DownloadInfoIncomplete/DCA predicate) and `:847-856` (write
boundary). A reader of the security record would re-open a fixed P2.

**Action:** update the SEC-4 section to record the implementing commit (`52bdb52`) and the
new residual (fail-open when the archive cannot be read, see P3-3).

### P3-3 DCA read-only is fail-open when the archive cannot be read

`src/Knx.App/MainWindowViewModel.cs:763-766` and `:799-802` swallow every exception and
return `CatalogBlockReason.None`. A corrupt or unreadable project archive therefore looks
editable; `ApplyParameterValues` at `:852-856` also sees `None` and proceeds if a host passes
instances directly. The normal UI path loads parameters first and would fail, but the write
boundary should not depend on that.

**Action:** on an unreadable or unparsable archive, return a read-only reason instead of
`None`, or throw and let the caller show the load error.

### P3-4 Portfolio mutations update memory before the atomic write succeeds

`src/Knx.Core/Portfolio/PortfolioRegistry.cs:160-171` (and the other mutators around
`:183-296`) change `_entries` and then call `Persist()`. If `Persist()` throws
(disk full, locked file), the method throws but the in-memory entry stays changed; the next
successful save persists it, while `Summarize`/`Entries` in the meantime show state that is
not on disk. `Persist` at `:599-621` also falls back from `File.Replace` to `File.Move` without
the `.bak`, so the documented "previous good state as `.bak`" does not always hold. There is
no inter-process lock; two app instances are last-writer-wins.

**Action:** apply mutations to a copy and swap it in only after the write succeeds (or roll
back on failure); keep the `.bak` on the fallback path or document the exception; add a
concurrency note/test if the planned UI can run in two instances.

### P3-5 Parameter copy/draft equality is a hand-maintained field list

`src/Knx.Core/Parameters.cs:63-81` copies every `ParameterType` field by hand and `:132-150`
does the same for `ParameterInstance`; `src/Knx.App/Services/ParameterEditingSession.cs:402-421`
repeats the field list for `DraftMatches`. A new model field will silently be omitted from the
copy (editor mutates the shared catalogue) or from the comparison (stale draft restored). The
current tests cover every present field but cannot prevent the next one.
`src/Knx.App/MainWindowViewModel.cs:716-725` also remaps `ParameterType` only on top-level
instances; child instances copied at `Parameters.cs:150` keep the shared type reference.

**Action:** add a test that reflects over public settable properties to assert copy/compare
coverage, or use a generated/clone helper. Remap child `ParameterType` recursively.

### P3-6 Test-quality gaps in the new checks

- `src/Knx.App/Workbench/WorkbenchCheck.cs:346-355` passes whenever
  `caption.TextWrapping == TextWrapping.Wrap`. `Note()` always sets that
  (`src/Knx.App/WorkbenchWindow.axaml.cs:2760`), so the check does not prove the measured
  width fits; it only proves the property is set. Measure the arranged caption/editor
  rectangles instead.
- `tests/Knx.Catalog.Tests/Program.cs:621-628` ("warmer Lauf nutzt sie ohne erneutes Lesen
  der Datei") asserts index statistics and identity-file mtime, not that the full SHA-256
  was skipped; a still-hashing implementation would pass. Add a counter or timing seam.
- `src/Knx.Mcp/McpServer.Tools.cs:1214-1219` reports a hard-coded `entries = 0` with
  "every DownloadSession.RunAsync" as the writer, although no session is inspected. Label it
  as "not available in this process" or remove the field.

### P3-7 Plan/documentation drift (claims do not resolve against this base)

- The plan header still says base `e5be9ed` (`analysis/workbench-v4-completion-plan.md:3-5`);
  the reviewed base is `0226a33`.
- The plan uses pre-rewrite SHAs (`:171` `49f3f22`/`33612e0`; `:330` `857dcda`/`53c06d9`;
  `:518` `a5b8966`) that are orphaned objects, not ancestors. Appendix A of
  `analysis/release-notes-v4.md:376-400` maps most of them, but not `a5b8966`, `53c06d9`
  or `857dcda`, and the plan itself does not point at the map.
- Warm-start measurement: the plan says median **398 ms** (`:330`), while the integrated
  commit `b224834` records **1673 -> 643 ms**. There is no committed result file for the
  398 ms figure; the pre-fix baseline report (`analysis/catalog-load-performance.md:25-31`)
  still describes the no-cache behavior. State one number and its method.
- `analysis/release-notes-v4.md:9,11,355` says 218 commits from `6562854` to `7962a1a`;
  `git rev-list --count 6562854..7962a1a` is **217**.
- `docs/manual.md:702` still says "48 Werkzeuge"; `README.md:28` and the MCP selftest
  (`src/Knx.Mcp/Selftest.cs:41`) say 49.
- `analysis/a11y-dpi-final.md:502-525` records that all four failures still reproduced at
  `bb68664`; the base adds `0226a33` (first-row focus in
  `src/Knx.App/CompareWindow.axaml.cs:20-24`) without a recheck. The report's verdict is
  therefore stale for the base, and the new focus code is untested. Re-run the four checks.

### P3-8 `ProgramPartCount` documentation is wrong

`src/Knx.Core/Catalog/KnxProductCatalog.cs:74-77` says it counts parts "still to read" and is
zero once complete, but the implementation returns `_partByName.Count`, which is the total
part count and stays non-zero (the test at `tests/Knx.Catalog.Tests/Program.cs:743` even
asserts 1 with `MetadataComplete`). `src/Knx.Core/Catalog/CatalogStore.cs:242,271` correctly
uses it as the total for progress. Rename to `ProgramPartsTotal` or fix the sentence.

## Plan-claim check summary

| Claim | Result |
|---|---|
| Base `0226a33` is clean rewritten history | Clean for HEAD; old worktree branches and the `pre-publish-backup` tag still point at pre-rewrite history |
| `fd0729d` is the review baseline | Not an ancestor of the base; old SHA, current equivalent `8d643a6` |
| Catalogue index/warm work captured in `49f3f22`, `33612e0` | Commits exist as orphaned pre-rewrite objects; integrated equivalents `9bfcf9f`, `0a5ab84` are ancestors |
| Parameter-open/SEC-4 in `53c06d9` | Orphan; integrated `52bdb52` is in base |
| Portfolio core in `a5b8966` | Orphan; integrated `e2e0061` is in base |
| Portfolio UI branch `agents/impl/portfolio-ui` | Branch tip is `e2e0061`, the core-only commit; no UI |
| Acceptance protocol | Branch `7b73631` exists, not merged |
| Project versioning | Branch `9e5c41e` exists, not merged |
| All commit identities `CaeruleusAqua` | False for HEAD (13 identities, including a company address) |
| 218 commits to `7962a1a` | 217 |
| 398 ms warm start | Commit `b224834` says 643 ms; no 398 ms evidence file |

## What was not verified

- Remote tip: at review time `git ls-remote origin` returned `development` = `81196a3`,
  while the local base is `0226a33`; the remote commit was not fetched or reviewed. Remote
  CI/CodeQL status was not queried.
- Real KNX hardware: no bus connection, descriptor read, tunnel/routing, download or
  secure-commissioning test was run; `KnxBus` writes remain disabled by policy.
- Real Windows screen reader/UIA and 150/200 % DPI: only the committed a11y report and
  headless selftests were available; the three open a11y failures are not re-measured here.
- Visual inspection of every screen/theme: screenshot selftest and workbench selftest only.
- Unmerged branches `7b73631` (acceptance protocol), `9e5c41e` (project versioning) and a
  portfolio UI: not reviewed line-by-line; the absence findings above are based on ancestry
  and file presence.
- GitHub purge of `refs/pull/1/head`, cached blobs and old Actions logs; local cleanup of
  163 old branches/worktrees and the `pre-publish-backup` tag. Both remain documented
  exposures in `analysis/git-history-rewrite-plan.md` sections 5-6.
- Whole-history manufacturer/customer scan: new files were scanned for tokens, keys, private
  IPs and customer paths; no secrets found. Absolute workspace/profile paths remain in newly
  added reports (`analysis/a11y-dpi-final.md:8,56`,
  `analysis/git-history-rewrite-plan.md:164`), consistent with the pre-existing residual
  noted in that plan.

## Addendum - post-fix re-review at `c333937`

- **Base:** `c333937fcb045ad07b618fb8bb107517f558a4eb` ("Keep the narrow inspector above
  the dock and enlarge row chevrons"), reviewed in a detached worktree at that exact SHA.
  The main checkout advanced past it during the recheck (`852fba7` at the last check);
  see "Post-base note" below.
- **Build:** `dotnet build ets6.sln -c Release` -> 0 errors, 30 warnings.
- **Tests (all green at `c333937`):** Core, Bus, Catalog, ProjectCompare, App.Tests, Mcp,
  Localization, Parameter, ParameterUi (both modes), Workbench, WorkbenchUiState,
  WorkbenchSessions, WorkbenchTheme, WorkbenchPerformance, Archive, Address, Linking,
  SmartLink, CatalogUi, Icon, Monitor, Diagnostics, Search, Buildings, Encryption, Owl,
  Docking, Shell; MCP selftest; screenshot selftest; workbench selftest de/light and en/dark.
- **Fix confirmation:**
  - `000e01e` now checks `plan.ImageLength`, walks `plan.Chunks`, rejects non-contiguous or
    out-of-range chunks and writes `planned.Address`, so the reviewed addresses are the
    written addresses (`src/Knx.Bus/DownloadSession.cs:127-158`).
  - `ac1ee53` validates XML file locations below the catalogue directory and archive entries
    against the ZIP entry list (`src/Knx.Core/Catalog/CatalogIndex.cs:112,306-337`).
  - `e970903` uses `Strings.Get("LinkProjectChanged")` and removes the localization exception.
  - `81196a3`, `72cb8b0`, `3afa0cd`, `56d5007` and `c333937` are ancestors of `c333937`;
    the acceptance protocol, portfolio UI, revisions/compare and B6 planning are present.

## New verdict

**Approve with fixes.** The previously blocking scope gaps and the three targeted P2 defects
are resolved; no P1 remains at `c333937`. The remaining P2s concern reachability and
completeness of the new versioning path, plus one release-hygiene item.

## Remaining P2

### P2-1 Durable revisions and revision-aware comparison are not reachable from the product

`ProjectRevisions.Capture` has no caller in product code; the only callers are
`src/Knx.App.Tests` and `tests/*`. The Workbench compare path still calls the non-revision
overload (`src/Knx.App/WorkbenchWindow.Commands.cs:670`), the MCP `project_compare` path does
the same (`src/Knx.Mcp/McpServer.Tools.cs:894`), and `DifferenceReportRenderer` has no product
caller. A real project therefore never accumulates the "durable" revision log, and the
revision-aware compare/renderer introduced by `81196a3` is library-only. The revisions/compare
part of H11.8 is therefore not reachable by users.

**Action:** capture a revision on save (or through an explicit command), call
`ProjectComparer.Compare(..., ProjectRevisions.Latest(...), ...)` from the Workbench/MCP
compare paths, and surface `DifferenceReportRenderer` in the compare window; add an
end-to-end check that a saved project gains a revision and the compare shows it.

### P2-2 Revision content hash and comparison omit the new B6 topology fields

`ProjectRevisions.Snapshot` includes the line medium but not `Line.DomainAddress`, and its
segment projection includes only id, number, name and devices; `Segment.MediumTypeRefId` and
`Segment.DomainAddress` are absent (`src/Knx.Core/ProjectRevisions.cs:185-198`). The comparer
compares installation facts and the building tree but has no line/segment projection
(`src/Knx.ProjectCompare/ProjectComparer.cs:137-167,436-475`). A project changed only in
segment medium/domain therefore gets the same content hash and reports as unchanged, even
though `P080`/`P081`/`P082` and the exported project XML use those fields.

**Action:** add `line.DomainAddress`, `segment.MediumTypeRefId` and `segment.DomainAddress`
to the canonical snapshot and to an explicit topology comparison; add a regression test that
changes only the segment medium and expects a changed revision/compare item.

### P2-3 Commit identity metadata still does not match the rewrite record

At `c333937`, `git log --format='%an <%ae>'` still yields 13 author identities; `52bdb52` is
authored by `scholle <scholle@aurora-robotics.de>` and `b08f42b` by
`assessment-agent <assessment@localhost>`. `analysis/git-history-rewrite-plan.md:179` still
claims all identities are `CaeruleusAqua`. This is the prior P2-3, unchanged by the fixes.

**Action:** rewrite/mailmap those post-rewrite identities before the next push, or update the
verification record and explicitly accept the company address.

## Remaining P3

- **Cache validation has no regression test and a platform edge.** `ac1ee53` changed only
  `CatalogIndex.cs`; no test constructs a cache document with an outside path or a foreign
  archive entry. `SafeFile` compares paths with `OrdinalIgnoreCase` on every OS and does not
  resolve symlinks (`CatalogIndex.cs:315-318`); on Linux a differently-cased or symlinked
  directory can pass the containment check. Add a poisoned-cache test and resolve links or
  use platform-correct comparison.
- **The new `Knx.ProjectCompare.Tests` is not in CI.** It is in `ets6.sln` but absent from
  `TEST_PROJECTS` (`.github/workflows/ci.yml:17-42`), so the revision/compare suite never
  runs there. Add it to the list.
- **Revision source paths are exported.** `ProjectRevision.SourcePath` is persisted
  (`ProjectRevisions.cs:37,86`) and included in the V4 JSON export via
  `WorkbenchJson.cs:259`. Reports deliberately hide it, but a shared JSON export still
  carries the user's absolute path. Store a basename or omit the field from export.
- **Portfolio actions run synchronously on the UI thread.** `CheckAll` hashes every existing
  entry in a loop (`WorkbenchWindow.Commands.cs:420-431`); `PortfolioRegistry.Refresh` reads
  and SHA-256s the whole project file. Large projects can freeze the modal. Also, the
  destructive remove confirmation uses the normal primary class, not `wb-danger`
  (`Commands.cs:613`), and the registry still mutates memory before `Persist` succeeds
  (`PortfolioRegistry.cs:160-171,599-621`).
- **Acceptance protocol keeps a private de/en table.** `AcceptanceProtocol.T` and its
  `Text` dictionary (`src/Knx.Reports/AcceptanceProtocol.cs:841-842`) bypass the core
  dictionary; the localization gate passes only through explicit `allow` entries. It supports
  no third language and is outside the shared key inventory. Move the keys into the core
  dictionary or document the report-specific exception.
- **Every project load now parses all `*/Hardware.xml` parts.** `ArchiveState.ReadProject`
  calls `ReadHardwareParts` before the project XML (`ArchiveState.cs:21-35,61`). This is
  optional planning input; for archives with many manufacturers it adds unbounded parse work
  to every open. Measure a large archive and cache or lazily load the facts if needed.
- **The `c333937` a11y fix is not verified.** The commit changes the inspector margin and
  chevron geometry (`WorkbenchWindow.axaml.cs:824-843,2519-2544`) but adds no test, and the
  a11y report still records the four failures at `bb68664`
  (`analysis/a11y-dpi-final.md:502-525`). Re-run the external overlay/chevron checks at
  `c333937`; the code reasoning is plausible but not evidence.
- **Plan and measurement drift remains.** H11.4/H11.8 cite orphaned agent SHAs
  `facef973`/`3fb21de`/`9e5c41e` (`analysis/workbench-v4-completion-plan.md:504,518-519`);
  the integrated commits are `56d5007`/`3afa0cd`/`81196a3`. The plan still says 398 ms warm
  start (`:330`) while `b224834` records 643 ms. Release notes still say 218 commits to
  `7962a1a` (`analysis/release-notes-v4.md:9,11,355`; actual 217). `ProgramPartCount` still
  documents itself as remaining parts (`src/Knx.Core/Catalog/KnxProductCatalog.cs:74-77`).
- **Download policy is not enforced inside the public write session.** `DownloadSession`
  now uses only plan addresses, but `RunAsync` still has no `KnxBus.WritesEnabled` check
  (`src/Knx.Bus/DownloadSession.cs:102-170`); a caller that constructs a tunnel and a ready
  plan can write while the wrapper gate says writes are disabled. This is a future-API
  hazard, not an app path today.

## Post-base note (outside the `c333937` verdict)

The checkout advanced past `c333937` while this addendum was being written (`852fba7` at the
last check). The post-base `HandoverPackage` work (`1df3f68` onward) still fails
`tests/Knx.Localization.Tests` with German literals in `HandoverPackage.Sheets.cs:166-174`
and `HandoverPackage.cs:210-463`. The failure is not present at `c333937` and is not part of
this verdict; it must be fixed before `development` can be called green again.

## Addendum verification limits

- Remote tip/CI/CodeQL were not queried; the local remote-tracking ref is not the live tip.
- Real KNX hardware, real writes/downloads, real Windows UIA/Narrator and 150/200 % DPI were
  not exercised; the a11y report and headless selftests remain the only evidence.
- Post-base commits (`1df3f68` through `852fba7`) are outside this review.
