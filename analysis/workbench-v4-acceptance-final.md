# Workbench V4 — independent final acceptance audit

Read-only audit. The only changes are this report, the status matrix and the
decision ledger under `analysis/`. No product source, test, CI or package file
was modified.

- Base: `ac0480a4710a6a8109b5b54361127cc13debd00d` (`development`)
- Branch: `agents/final/acceptance`
- Worktree: `C:\projects\ai_work\agent-wt\final-acceptance`
- Date: 2026-10-21
- Environment: Windows, .NET 9, Avalonia 12.1.4, 96 dpi (100 %), single monitor
- Reference bundle: `knx-workbench-v4-bundle` (`src/app.js`, `src/shell.html`)

> **Stale-base warning.** Everything measured here is at `ac0480a`. Main has
> since advanced to `f9d0e7f` (`f605870` dialogs, `39be758` panes,
> `ab0f8a4` search presenter parts, `45f035b` muted/disabled contrast,
> `f9d0e7f` contrast test). Findings marked **[superseded]** below are fixed in
> source at main HEAD but were **not re-run** by this audit. The exact remaining
> list is in §7; use that, not the historical detail, for integration.

## Verdict

At the audited base `ac0480a`, the V4 workbench is functionally broad and the
local release gate is green, but final acceptance was not complete. After the
later main commits the only *known* P1 is gone and the remaining work is
concentrated in R05 (English), R02/R06/R07 (documented P2s) and R04 (real DPI /
screen reader). The base run found:

1. **[superseded at main HEAD] Disabled text contrast (was P1)**: rendered
   `1.74:1` light / `2.31:1` dark at `ac0480a`. `45f035b` removed the opacity and
   darkened the token; `f9d0e7f` asserts the token pair. Not re-run here.
2. **English completeness (P2/R05)**: project-check findings, the assignments
   CSV, the topology CSV header and the HTML report stay German in the English
   UI; one German-UI button is hardcoded English. **Still present at main HEAD**
   (spot-checked).
3. **Tab semantics (P2)**: document/tool/view tabs still expose `Button`, not
   `TabItem`; selection status is incomplete in the measured run. **Still
   present at main HEAD**.
4. **Narrow inspector (P2)**: the overlay reflows and removes covered tab stops,
   but while open at 900/1024 it clips representation tabs and table columns;
   the 1024 horizontal scrollbar clips the table footnote. **Still present at
   main HEAD** (no later commit touched `WorkbenchWindow.axaml.cs`).
5. **Pinned compare missing (P2/R02)**: `compare` is only a GA list; no pinned
   counterview. **Still present at main HEAD**.

No real bus write, Secure commissioning, DCA host, USB/RDAC, cloud connector or
multi-project tab was found or claimed; those limits hold.

| ID | Status at base `ac0480a` | Status at main `f9d0e7f` (spot-check) |
| --- | --- | --- |
| R01 | evidence present, not re-audited | unchanged |
| R02 | partial | partial: pinned compare/docs/checkbox/batch still open |
| R03 | partial | partial: English + panel selector/multi-project open |
| R04 | open | open: no real 150/200 % DPI or OS screen reader |
| R05 | partial | **partial/open**: check findings, assignments CSV, report German |
| R06 | partial | improved by `f605870`/`39be758`; not re-audited |
| R07 | open | **open**: active perspective 4.24, tab role, narrow inspector |
| R08 | partial | local base green; main `f9d0e7f` not built/tested here |

## 1. Action and event mapping

Complete mapping with file/line and test evidence:
[`workbench-v4-status-matrix.md`](workbench-v4-status-matrix.md).

Counts after this audit: **71 done, 6 partial, 1 missing** of 78 actions, and
**20 done, 5 partial, 1 missing** of 26 events (some IDs overlap semantically).

Notable deltas against the old matrix (`55ff6e1`):

- **Upgraded to done**: 20 (KO detail in the inspector, `WorkbenchWindow.axaml.cs:1327`),
  26 (frozen multi-selection snapshot, `WorkbenchWindow.Commands.cs:567`),
  34 (`ParameterPane` discard button, `Views/ParameterPane.axaml:20`),
  47 (four monitor scopes, `:1693`), 64 (selection commands in the edit menu).
- **Downgraded to partial**: 24 / E09. The “compare” action is only the GA list in
  the sidebar (`:1031`); there is no pinned group-address counterview that
  survives editor navigation. The old matrix called 24 done without this limit.
- **Still missing**: 07 (documentation page), E09 (pinned compare). 60/E26 remain
  partial (six dock tools + catalog + three dialogs, no docs page).
- 17 (checkbox column) and 22 (batch action in the inspector) remain partial.
- The plan still has unchecked boxes for work that is now implemented
  (drag/drop, “show in topology”, favorites, property-grid inspector, draft
  indicator, parameter footer). The plan is no longer an accurate todo list.

Deliberate limits kept explicit: DCA/plugin hosting (read-only detection),
Secure commissioning and key/FDSK management, real bus writes/downloads
(`ProgrammingSession.cs:121` rejects real jobs without an executor), USB/RDAC,
cloud/IoT, ETS local-store versioning, multi-project tabs, manufacturer script
dynamics and online catalogue/signature checks.

## 1a. What changed on main after this audit

Source spot-check at `f9d0e7f` (not a rebuild, not a re-run):

| Commit | Effect on audit findings |
| --- | --- |
| `45f035b` | `WbMuted`/`WbDisabledText` darkened to `#5f6b7d`, disabled `Opacity` set to 1. Base P1 disabled contrast is fixed in source; computed token ratios are now 4.74–5.40:1. |
| `f605870` | Dialogs unified on Wb chrome (`NewProject`, `Compare`, `Replace`, `Report`, `ConfirmDialog`, `Settings`); `wb-field`, `wb-danger`, primary-last, scrollable help (`MaxHeight=360`), localized compare CSV. Supersedes T4, W8, P3, P5, P6, P8, P11, T11, W12 and the compare-CSV leak. |
| `39be758` | Search pane compact V4 page, parameter editors use `wb-field`, catalog filter row aligned. Supersedes the Search-pane P1/P7 and the ParameterPane skin part of P2. |
| `ab0f8a4` | Named `PART_ContentPresenter` rules in the search pane. |
| `f9d0e7f` | Theme test asserts text contrast; note the test checks `WbDisabledText`/`WbDisabled` **before** any opacity, and does **not** cover `WbAccent` on `WbTint`. |

Still present at `f9d0e7f` (spot-checked in source, no run):

- `ProjectCheck.cs:186,286` and the rest of the check messages are German.
- `WorkbenchWindow.Commands.cs:264` assignments CSV header is German;
  `ProjectCsvExporter.cs:47` topology header is mixed; `ProjectReport.cs:71-298`
  is German.
- `ParameterPane.axaml:63` still hardcodes “Cancel review”.
- Document/view/tool tabs are still `ActionButton` (`WorkbenchWindow.axaml.cs:474`).
- Inspector caption column is still 104 px (`:1207`); narrow overlay code is
  unchanged, so the clipped representation tabs/footnote remain.
- No pinned compare; no docs page; no real DPI/screen-reader run.

## 2. English completeness

Method:

- static key coverage of `Localization/WorkbenchStrings.cs` (398 keys) and
  `Knx.Core/Localization.cs` (327 keys);
- static scan of every `src/**.axaml`/`.cs` literal for German text;
- runtime walk of the real `WorkbenchWindow` in `--lang=en` and `--lang=de`
  over 21 shell states (perspectives, objects, parameters, relations, overview,
  six tools, catalog, GA, two inspectors, menus, palette, settings, new project,
  help);
- `--workbench-selftest --lang=en --theme=light` and `--lang=de --theme=dark`.

### Key coverage

- Every referenced `{loc:Wb …}` / `WorkbenchStrings.Get` key exists.
- Every referenced `{loc:Tr …}` / `Strings.Get` key exists.
- No dictionary entry has an empty `de` or `en` value.
- 20 Workbench keys and 168 core keys are currently unused (legacy vocabulary);
  dead keys, not missing translations.

### Runtime leaks (English UI shows German)

Four distinct `ProjectCheck` messages are rendered in the problems tool and the
status line, repeated across every shell state:

| Message | Source |
| --- | --- |
| `Gerät '…' hat keine Kommunikationsobjekte.` | `src/Knx.Core/ProjectCheck.cs:186` |
| `Kommunikationsobjekt '…' von '…' ist nicht verknüpft.` | `src/Knx.Core/ProjectCheck.cs:286` |
| `… hat keinen Bezeichner.` / P001 and the other P0xx texts | `src/Knx.Core/ProjectCheck.cs:45-380` (entire file is German-only) |

Evidence: `%TEMP%\opencode\acceptance\lang-audit\out\en-problems.txt` lines
116-123; identical hits in `en-ga.txt`, `en-monitor.txt`, `en-topology.txt`,
`en-inspector-*.txt`.

### Static leaks outside the live shell

| Surface | Finding | Source |
| --- | --- | --- |
| Assignments CSV | fully German header, exported from the English menu | `WorkbenchWindow.Commands.cs:264` |
| Topology CSV | mixed header `Area;Line;Segment;Gerät;Adresse` | `src/Knx.Reports/ProjectCsvExporter.cs:47` |
| HTML report | all headings/empty states German | `src/Knx.Reports/ProjectReport.cs:71-298` |
| Compare CSV | German header/filename (`Pfad;Typ;…`, `projektvergleich.csv`) | `CompareWindow.axaml.cs:33,39` — **[superseded]** localized by `f605870` |
| German UI, inverse leak | `Content="Cancel review"` hardcoded English, bypassing the `T("Vorschau schließen", …)` already used in code-behind | `Views/ParameterPane.axaml:44` |
| Splash | English tagline in the German UI | `SplashWindow.axaml:11` |

Everything else in the live English shell was English: topbar, menus, navigator,
tabs, table, inspector, monitor, programming, scan, search, changes, catalog,
settings, new project, help, palette, tooltips and automation names. The
project data itself (device/GA names such as “Tastsensor Wohnzimmer”) stays
German by design and was excluded from the leak list.

**R05 is not met** while R05 is read as “all workbench texts, dialogs, error
messages and dynamic strings”. If R05 is narrowed to shell chrome only, it is
met; that scope decision belongs to the integrator.

## 3. Contrast and visual states

WCAG 2.1 relative-luminance calculation over the live token dictionaries
(`WorkbenchTheme.axaml`, `App.axaml`) plus rendered probes for placeholder and
disabled states. Normal text needs 4.5:1; all affected sizes are 10–13 px, so no
large-text exemption applies.

### Failures at base `ac0480a`

| Surface | Pair | Ratio | Source |
| --- | --- | ---: | --- |
| Disabled button, light | composited `#babfc8` on `#f7f8fa` | **1.74:1** | `WorkbenchTheme.axaml:319-320` (`Opacity 0.46`) |
| Disabled button, dark | composited `#566271` on `#222b36` | **2.31:1** | same |
| Disabled token alone, light | `#687487` on `#eef0f4` | 4.15:1 | `WorkbenchTheme.axaml:73-74` |
| Active perspective button, light | `#087e81` on `#e4f2f1` | 4.24:1 | `WorkbenchWindow.axaml:10`, `:451` |
| Muted text on row hover, light | `#687487` on `#f0f4f5` | 4.27:1 | `WorkbenchTheme.axaml:263` + `WorkbenchWindow.axaml:13` |
| Status bar, light | `#687487` on `#f3f5f8` | 4.33:1 | `WorkbenchWindow.axaml:138` |
| Muted on window background (token pair, usage unconfirmed) | `#687487` on `#f5f6f8` | 4.38:1 | `WorkbenchTheme.axaml:56/51` |

Rendered disabled probe (light/dark): opacity `0.46`, presenter `#eef0f4` /
`#252e3a`, label `#687487` / `#96a5b9`. This confirms the static composite; it
is not only a token-level inference.

The disabled rows are **superseded at main HEAD**: `45f035b` removed the opacity
and darkened the text, `f9d0e7f` asserts the token pair. The status/hover/perspective
rows are still open (token values unchanged at `f9d0e7f`).

### Passes worth recording

- Body text 14.12:1 light / 12.39:1 dark; muted on paper 4.73:1 light.
- Selected row `WbSelectedText` on `WbSelected` 5.30:1 light / 7.66:1 dark.
- Primary button 4.87:1 light / 8.37:1 dark; error/warning/success ≥ 4.69:1 light.
- Legacy `Ets` disabled text 4.82:1 light / 4.97:1 dark.
- Tooltips 14.12:1 light, 12.20:1 dark (measured in the open popup root).
- **Placeholder correction**: the old report claimed 2.6:1. Measured Fluent
  placeholder is `#99000000` light / `#99ffffff` dark, effective 5.74:1 /
  6.30:1. This is not a defect and the old finding should not be repeated.
- Dark theme has no AA failure except the disabled composite.

Empty states use muted on paper (pass). Error borders use `WbError` (pass).
Hover for normal text passes; only the 10 px muted cells on a hovered row fail.

## 4. Layout at 900×600, 1024×700, 1440×900

Captured with the product screenshot path (`--theme=light|dark --lang=en`) and
measured with the external a11y harness at the same base. Light and dark were
captured at all three sizes for the default, inspector and monitor states.

| Size | State | Result |
| --- | --- | --- |
| 900×600 | default | no clipping; all toolbar actions visible; table columns fit |
| 900×600 | inspector | inspector usable; representation tabs after “Parameters” clipped; table columns beyond DPT hidden behind the overlay; no horizontal path to them |
| 900×600 | monitor | two-row toolbar fits; dock body visible; no overlap with float/maximize/close |
| 1024×700 | default | no clipping |
| 1024×700 | inspector | “Room reference (opti…” label clipped in English; horizontal scrollbar clips the table footnote |
| 1440×900 | all | no clipping; labels and footer visible |
| Settings 620×560 / 1024×700 | — | all buttons inside the window (harness PASS) |

Harness measurements: `clipped-without-scroll=0` at 900/1024/1440, with and
without tools; 91/2/2 clipped-inside-scrollviewer entries are virtualized rows
or the horizontally scrollable tab strip, not lost actions.

### Specific residual findings

1. **Representation tabs clipped at 900 with inspector.** The editor reflows to
   ~288 DIP, the `StackPanel` does not scroll or wrap; “Beziehungen” and
   “Prüfung” are cut. Closing the inspector restores them. P2.
2. **Table footnote clipped at 1024 with inspector.** Editor width < table
   `MinWidth=540`; the horizontal scrollbar overlays the 27 px footnote row.
   Reproduced in `shots/crop-1024-footer.png`. P2.
3. **English inspector label clipped at every width.** `Field` uses a fixed
   104 px caption column with `TextWrapping.NoWrap`
   (`WorkbenchWindow.axaml.cs:1205-1212`); “Room reference (optional)” is cut in
   all captures, German “Raumbezug (optional)” fits. P2 i18n layout.
4. **15 geometry intersections with the overlay** (harness). They are clipped
   `MinWidth=540` rows and tab buttons behind the ScrollViewer, not visible
   controls; tab navigation reports **0 covered stops**. In the 900×600 capture
   no visible control sits under the inspector. The harness check should
   intersect with clip bounds before it is used as a defect; pointer
   reachability of the clipped elements was not measured.

### Simulated 150/200 %

- Runtime `RenderScaling=1`, no public per-window setter; **no real high-DPI run**.
- 1.5×/2× raster captures of the 1440×900 and 900×600 layouts are sharp, with no
  new clipping (`layout-1440x900-scale150/200.png`, `layout-900x600-scale200.png`).
- Source minimum is now 900×480 (`WorkbenchWindow.axaml:4`), which fits
  1366×768@150 % (911×512) and 1920×1080@200 % (960×540) before the taskbar.
  This closes the source-level part of F13, not the hardware acceptance.
- Settings is fixed 720×620 with `MinHeight=480`; the help message box is
  `SizeToContent.Height` without a scroll viewport and can exceed a 480 DIP work
  area at 200 %.

## 5. P1/P2 reconciliation

Full ledger: [`ui-consistency/final-decisions.md`](ui-consistency/final-decisions.md).
Summary:

- **Fixed**: programming crash, tool-tab collision, perspective overlap, doc-tab
  accent, search forced maximized, help focus, status live region, table footnote
  live, inspector field names, row-name commas, object-filter undo, perspective
  automation names, min-height.
- **Open at main HEAD**: active perspective contrast (4.24:1), narrow inspector
  residual (clipped tabs/columns), tab role/selection, English findings/exports,
  two parameter UIs, pinned compare.
- **Superseded at main HEAD**: disabled contrast (`45f035b`/`f9d0e7f`), dialog
  family, destructive confirm, help scroll, settings fields, search pane,
  compare CSV, parameter field chrome.
- **Accepted decisions**: rail state simplification, table checkbox/chips
  deviation, legacy SearchPane styling (now a maximized page), tooltip shadow,
  radius/height literals, the six deliberate platform limits.
- **Corrections to prior reports**: placeholder contrast is 5.74/6.30, not 2.6;
  the a11y harness `inspector-field-has-name` failure is stale (fields are
  named); F14 and F13 are fixed in source.

## 5a. Post-audit addendum (2026-10-22, development `02e1e12`)

The original table above is the audit at `ac0480a`. Current status:

| ID | Status | Evidence |
| --- | --- | --- |
| R01 | **done** | five `analysis/ets/*.md` reports present and linked from the plan |
| R02 | **done with documented limits** | 51 MCP tools, pinned compare/docs/checkbox actions, handover/acceptance; direct persistent object target and full multi-project tabs remain deliberate limits |
| R03 | **done with documented limits** | B1-B8 implemented or listed as deliberate limits; real writes/DCA/Secure/multi-project excluded |
| R04 | **partial** | real UIA probe and simulated 150/200 layouts pass; real 150/200 % DPI, multi-monitor, Narrator speech and high contrast remain open |
| R05 | **done** | localization gates green; ProjectCheck/CSV/report/Owl localized; technical path tokens are data |
| R06 | **done** | dialogs/panes/shell/theme unified; 900/1024/1440 screenshots and contrast matrix |
| R07 | **done** | open P1/P2s fixed or documented; remaining items are P3/limits |
| R08 | **done** | local full suite and remote CI/CodeQL green at the integrated HEAD; repeat after the next push |

## 6. R08 - what ran locally, what only CI can prove

### Executed locally on this branch (all exit 0)

```text
dotnet build ets6.sln -c Release
  0 errors, 21 warnings (40.7 s)

24 projects from .github/workflows/ci.yml TEST_PROJECTS
  all pass, including:
  Knx.Workbench.Tests           workbench identity/JSON/scope/links/signatures/undo
  Knx.WorkbenchUiState.Tests    WORKBENCH UI STATE OK (34)
  Knx.WorkbenchSessions.Tests   128 checks
  Knx.WorkbenchTheme.Tests      source contract only
  Knx.WorkbenchPerformance.Tests large projection (domain, not rendering)
  Knx.CatalogUi.Tests           100 checks
  Knx.Archive.Tests             143 checks
  Knx.Icon.Tests                410 checks
  Knx.Encryption.Tests          30 checks
  Knx.Parameter.Tests           78 checks (MDT corpus skipped)
  Knx.SmartLink.Tests           88 checks

Windows-only suites (ci.yml:125-132):
  tests/Knx.Docking.Tests                        WORKBENCH DOCKING OK
  tests/Knx.Shell.Tests                          SHELL DRAFT LIFECYCLE OK
  tests/Knx.ParameterUi.Tests -- --native        NATIVE DRAFT CHECKS OK

App gates:
  --smoke                                        SMOKE OK
  --screenshot-selftest                          SCREENSHOT OK
  --workbench-selftest --lang=en --theme=light   WORKBENCH NATIVE OK
  --workbench-selftest --lang=de --theme=dark    WORKBENCH NATIVE OK
```

External a11y/layout harness at the same base: **66 PASS, 5 FAIL, 2 INFO**.
Four distinct checks fail: inspector overlay geometry (two duplicate lines, a
clip-aware false positive as described in §4), tab role, tab selection status,
and `inspector-field-has-name` (stale direct-child lookup; fields are named).
Artifacts under `%TEMP%\opencode\wb-v4-a11y\harness\artifacts`.

### Only CI can prove

- `dotnet restore`/build on clean `ubuntu-latest` and `windows-latest` runners;
- the exact commit `ac0480a…` on a CI runner (workflow triggers on
  `development`/`main` push or PR; this branch was not pushed);
- Linux test parity (local evidence is Windows only; CI skips `Knx.Icon.Tests`
  on Linux because it needs a display);
- cache/dependency resolution and the required `ci` aggregate check.

### CI coverage gaps (not failures, but relevant to R08/R04/R05)

- CI runs `--workbench-selftest --theme=light` **without** `--lang=en`; there is
  no English leak test and no contrast assertion.
- CI does not run the a11y/layout matrix, real DPI, or screenshot comparison;
  `--screenshot-selftest` only checks that one PNG exists and is non-trivial.
- `Knx.WorkbenchTheme.Tests` asserts token values and opt-in selectors, not
  WCAG ratios or disabled opacity (`tests/Knx.WorkbenchTheme.Tests/Program.cs:1-18`).
- No test covers `ProjectCheck` message localisation, the CSV/report exports, or
  the pinned-compare flow.

## 7. Prioritized gaps for the integrator

Exact remaining list at main `f9d0e7f`, highest first:

1. **R05 / P2 — English is not complete.** `ProjectCheck` messages
   (`src/Knx.Core/ProjectCheck.cs:186,286` and the other P0xx texts), the
   assignments CSV header (`WorkbenchWindow.Commands.cs:264`), the topology CSV
   header (`ProjectCsvExporter.cs:47`) and the HTML report
   (`ProjectReport.cs:71-298`) stay German. Decide: translate, or scope R05 to
   shell chrome and record the exception. Add `--lang=en` to CI (it currently
   runs the selftest only with `--theme=light`).
2. **R07 / P2 — tab semantics.** `WorkbenchWindow.axaml.cs:474` (and the view/tool
   strips) still build `Button`s. Use a custom peer returning `TabItem` plus full
   `ItemStatus`, or migrate to `TabControl`/`TabItem` (styles exist at
   `WorkbenchTheme.axaml:211`). The harness measured only 3 of 17 selected
   statuses, so re-verify the status wiring (`:357,477,581,1517`).
3. **R07 / P2 — narrow inspector clipping.** Representation tabs are cut at 900
   with the inspector open; the 1024+inspector horizontal scrollbar clips the
   table footnote. Make the tab row scroll/wrap and keep the footnote out of the
   scrollbar overlay. F1 is fixed for visible/focus coverage (0 covered stops);
   the harness geometry check needs clip-aware intersection.
4. **R07 / P2 — active perspective contrast.** `WbAccent` on `WbTint` is still
   4.24:1 in light (`WorkbenchTheme.axaml:59,62`, `WorkbenchWindow.axaml:10`);
   `f9d0e7f` does not assert this pair. Dark is 6.18:1.
5. **R02 / P2 — pinned compare.** Implement 24/E09 or strike it with a written
   decision; the sidebar GA list is not a counterview.
6. **R06 / P2 — two parameter UIs.** `39be758` styled native editors, but demo
   devices still use the hand-built form (`WorkbenchWindow.axaml.cs:75-96`);
   values and validation differ from `ParameterPane`.
7. **R04 — real 150/200 % DPI and OS screen reader.** Source minimum is 900×480
   and 1.5×/2× rasters are clean, but no real high-DPI or UIA/Narrator run
   exists. The help dialog is scrollable now (`ConfirmDialog.cs:68-70`), which
   removes the 200 % overflow risk.
8. **R02 / R03 — residual action/event gaps.** 07 docs page, 17 checkbox column,
   22 inspector batch action, 60/E26 full tool set, E03 link filter, E13 stale
   scan on line switch, E17/E18. Each needs either implementation or a written
   “not supported” entry.
9. **R08 — rebuild and run at the integration SHA.** Base `ac0480a` is green
   locally; `f9d0e7f` was not built or tested by this audit. The new contrast
   test must pass in CI, and CI still has no English/layout/DPI gate.
10. **P3 cleanup.** English inspector caption column (104 px,
    `WorkbenchWindow.axaml.cs:1207`), “Cancel review” in
    `ParameterPane.axaml:63`, “1 devices” grammar, splash tagline, dead
    `MainWindowViewModel.DownloadAsync` (no caller).

## 8. Evidence index

| Evidence | Location |
| --- | --- |
| Workbench selftest EN / DE | `%TEMP%\opencode\acceptance\selftest-en.txt`, `selftest-de.txt` |
| English runtime walk (26 files) | `%TEMP%\opencode\acceptance\lang-audit\out\en-*.txt` |
| German runtime walk | same, `de-*.txt` |
| Static German literal inventory | `%TEMP%\opencode\acceptance\german-literals.txt` |
| Contrast table and failures | `%TEMP%\opencode\acceptance\contrast.md` |
| Rendered disabled/placeholder probe | `lang-audit\out\en-contrast-probe.txt` |
| Light/dark captures 900/1024/1440 | `%TEMP%\opencode\acceptance\shots\*.png` |
| 1024 footnote crop | `shots\crop-1024-footer.png` |
| A11y/layout run | `%TEMP%\opencode\wb-v4-a11y\harness\artifacts\{results,observations,layout-*,inspector-coverage-*}.txt` |
| 1.5×/2× captures | same, `layout-1440x900-scale150/200.png`, `layout-900x600-scale200.png` |
| CI definition | `.github/workflows/ci.yml` |

All temporary paths are outside the repository and are not committed.
