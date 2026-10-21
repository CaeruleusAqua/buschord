# Workbench V4 — functional re-check

Basis: `fc9d1ea3b2db3ea289e7e4a9b6a53ccdfa17f882` (`development`, “Add backup list and
restore flow to the workbench”).
Branch `agents/workbench-v4/functional-recheck`, worktree
`C:\projects\ai_work\agent-wt\workbench-v4-functional-recheck`.
Product code was read, built and exercised, but not modified. Only this report is
committed.

Inputs: `analysis/workbench-v4-status-matrix.md`, `analysis/ets/*.md`,
`WorkbenchWindow{,.Commands}.axaml.cs`, `WorkbenchProjection`, `WorkbenchWorkspace`,
`WorkbenchCheck`, `SearchPane`, `CatalogPane`/`CatalogBrowser`, `MainWindowViewModel`,
`WorkbenchSessions` and `Knx.Core/Workbench`.

Legend: **tested** = executed in this re-check; **source** = confirmed in the current
code without executing the UI path; **inferred** = reasoned from code, not reproduced.

## Verdict

No crash, data-loss or build regression was found in the automated gate. Two
functional defects are confirmed in realistic synthetic flows, one of them P1:
newly created group addresses cannot be linked, and a name/address edit silently
moves a segmented device to the line root. The remaining findings are narrower
workflow/acceptance gaps (search tool has no navigation, catalog insert ignores the
room context, English shell leaks German, programming failure is unreachable in the
UI) plus P3 copy/state issues.

## Gate results (all executed)

| Gate | Result |
|---|---|
| `dotnet build ets6.sln -c Release` | success, 0 errors, 25 warnings (pre-existing nullable/deprecation) |
| 25 projects under `tests/` | all exit 0; Archive 142, Sessions 96, Workbench domain, UiState 19, Theme, Performance, Catalog UI 69, Icon 153, SmartLink 88, Parameter 78, Search/Monitor/Shell/Linking/Core/Diagnostics/Docking/Mcp/Owl/Encryption/Address/Buildings/Bus/Catalog |
| `src/Knx.App.Tests` | `REPORTS OK`, exit 0 |
| `--workbench-selftest --theme=light --lang=de` | `WORKBENCH NATIVE OK`, exit 0 |
| `--workbench-selftest --theme=dark --lang=en` | `WORKBENCH NATIVE OK`, exit 0 |
| `--smoke` | `SMOKE OK`, exit 0 |
| `--screenshot-selftest` | `SCREENSHOT OK`, exit 0 (1440×900 PNG) |

The self-test covers routed navigation, Apply/undo, inspector/link host, tab state,
layout restore, fresh tool windows, 10,027-row projection (18 realized) and
project-replacement sessions. It does not assert the findings below.

## External harness (tested)

Throwaway console harness outside the repo:
`C:\Users\julia\AppData\Local\Temp\opencode\wb-func\harness` (not committed).
It references the built projects, uses the public demo project and isolated
`BUSCHORD_SETTINGS`/backup/layout paths. Result: **52 checks, 50 passed, 2 failed**.

| Flow | Result |
|---|---|
| New project → save → reopen; name persists; load clears dirty | pass |
| `ProjectBackup.Capture/Create/List/Restore` → restored archive loads | pass |
| Device edit name/address/line/room; duplicate and invalid address rejected; one undo restores | pass |
| Device edit on a segmented device | **fail** (P2.2) |
| GA create validation (duplicate, empty name), room metadata, undo | pass |
| Link a newly created GA | **fail** (P1) |
| Batch add two links atomically, one undo entry; explicit sending; remove link | pass |
| Monitor start, simulated write/read, GA pin filter, filtered/full CSV | pass |
| Simulation while monitor stopped | throws (P3.12) |
| Programming: freeze, success, name-only edit keeps state, parameter edit needs programming | pass |
| Programming failure/retry with injected failing executor; success/failure state | pass |
| Line scan over a populated line; rerun clears results | pass |
| Project search, replace preview/apply/undo, invalid regex | pass |
| Compare two archives; product replace dry-run/apply | pass |
| Workspace save/restore tabs/filter/view/collapse/width; malformed layout rejected; foreign-project guard; `queue` tool accepted | pass |
| Old sessions reject a project-identity switch | pass |

## Findings

### P1

**P1.1 — A group address created through the validated form can never be linked.**
`MainWindowViewModel.CreateGroupAddress` writes `GroupMetadata { Bits = null, Secure = false }`
(`src/Knx.App/MainWindowViewModel.cs:414–417`). `WorkbenchProject.CheckLink` rejects every
GA whose `Bits` is null as `UnknownCapability`
(`src/Knx.Core/Workbench/WorkbenchProject.cs:63–65`), and no UI sets GA bits.
Repro (harness): create `2/2/3 · DPST-1-1`, then link any object →
“DPT, size or security capability is unknown; native compatibility cannot be inferred.”
Impact: the “new group address” workflow (matrix 38/39) produces an unusable address;
linking only works with pre-existing/demo GAs. **tested.**

### P2

**P2.1 — Editing a device silently moves it out of its segment.**
`UpdateDeviceDetails` removes the device from every area/line/segment and re-adds it only
to `destinationLine.Devices` (`src/Knx.App/MainWindowViewModel.cs:439–457`). The form does
not expose a segment, so a pure rename changes topology.
Repro (harness): place a device in a segment, call `UpdateDeviceDetails` with the same
address/line and a new name → device is at line root, segment empty. Undo is one step but
the change is not signalled. **tested.**

**P2.2 — The search tool shows results but cannot open them.**
`SearchPane` raises `NavigateRequested` (`src/Knx.App/Views/SearchPane.axaml.cs:44,148`),
but no host subscribes (repo-wide search: event declaration + raise only). Selection only
sets `ViewModel.SelectedDevice`/`SelectedGroupAddress` (`:152–163`); the workbench observes
only `Title`/`Project` (`WorkbenchWindow.axaml.cs:173–176`), so no tab/navigator/inspector
changes. The sidebar search (`FillSearchSidebar`) does open targets, so the two search
surfaces behave differently. **source.**

**P2.3 — Catalog insertion ignores the room context.**
`CatalogBrowser.Insert` takes only a line (`src/Knx.App/Services/CatalogBrowser.cs:147–195`);
`CatalogPane.OnInsert` passes line/address/name only
(`src/Knx.App/Views/CatalogPane.axaml.cs:500`). From a room tab the preferred line is derived
from the room’s first device (`WorkbenchWindow.axaml.cs:425–436`), but the new device gets no
room reference, so it does not appear in that room’s projection. This contradicts A01/US01
(“insert in room, find same device under line”). **source.**

**P2.4 — The monitor communication-object filter is not device-scoped.**
Both the shell resolver and the session fallback match only `ComObjectInstanceRef.Id`
(`WorkbenchWindow.axaml.cs:246–250`, `Sessions/MonitorSession.cs:292–293`), while ref IDs are
device-local (`docs/workbench-domain.md:12`). On native projects that reuse ref IDs (common),
“follow selection”/the pinned KO filter includes links of other devices. The demo fixture has
no duplicate ref IDs, so this could not be reproduced with demo data. **inferred (code-level).**

**P2.5 — English shell still leaks German and a mojibake ellipsis.**
Hard-coded German: scan result/error text (`Sessions/LineScanSession.cs:43,49,52`), group
sidebar count and `"â€¦"` (`WorkbenchWindow.axaml.cs:625,629`), compare status
(`WorkbenchWindow.Commands.cs:293`), assignment CSV header
(`WorkbenchWindow.Commands.cs:194`). The EN self-test passes because it does not assert these
strings. **source** (not visually accepted).

**P2.6 — Programming failure/retry is unreachable in the product.**
The demo executor always succeeds after 150 ms
(`Sessions/ProgrammingSession.cs:61`); the tool offers enqueue/run/“RetryFailed”
(`WorkbenchWindow.axaml.cs:1115–1178`) but nothing creates a failed job. Session-level
failure/retry works (harness + `Knx.WorkbenchSessions.Tests`), so the status-matrix claim
“retry-job done” is true for the session, not demonstrable in the UI (T08). **tested at
session level; UI gap confirmed by source.**

### P3

1. **New project + cancelled save picker discards the project** (`Commands.cs:109–115`).
   Manual §2.2 says the project remains in memory. Doc/behaviour mismatch.
2. **Backup list text**: for a pathless project the dialog prints “Keine Arbeitsbereiche”
   as the path/empty note (`Commands.cs:223–224`).
3. **Change history leaks across projects**: `_audit` is cleared only on demo reset
   (`Commands.cs:317`), not on open/import, so entries of the previous project remain.
4. **Workspace UI**: `SaveNamedWorkspaceAsync` appends a second set of buttons without
   clearing (`WorkbenchWindow.axaml.cs:687`); a layout with tool `queue` is accepted
   (`WorkbenchWorkspace.cs:158`) but `RefreshWorkspace` silently resets it to `monitor`
   (`WorkbenchWindow.axaml.cs:339`); a workspace of another project restores sizes but
   returns `true` without tabs (`WorkbenchWorkspace.cs:160`), with no message.
5. **Monitor value simulation requires a running monitor** (`MonitorSession.cs:183`);
   the dialog does not start it and surfaces the English exception in the German UI
   (`WorkbenchWindow.axaml.cs:1265–1275`).
6. **Save aborts if the pre-save backup fails** (`Commands.cs:127,132`): a locked or
   corrupt existing target blocks the save; no fallback/warning path. (inferred edge)
7. **Stale tool text**: monitor running/record counts refresh only on button actions, not
   on ticks (`WorkbenchWindow.axaml.cs:1005–1013`); the programming “needs programming”
   count is computed when the view is built (`:1153–1155`); pinned monitor filters are not
   refreshed after link edits (`OnProjectEdited` only audits, `:185–193`). (inferred)
8. **Ctrl+S on a pathless project opens Save As** although the manual says it does nothing
   (`Commands.cs:123–129` vs `docs/manual.md` §2.3). Documentation drift only.

## Tested vs inferred

- Tested end-to-end (headless/VM/core): project save/open, backup capture/create/restore,
  device edit validation/undo/segment loss, GA create validation/undo/link failure, batch
  links/sending/removal, monitor simulation/filter/export, programming success/failure/retry,
  scan, search/replace/undo, compare, product replace, workspace persistence, session switch.
- Source-only: all dialogs and pickers (open/new/save/backup restore/GA form/batch link/
  monitor value/compare/replace/workspace prompt), search-tool navigation, catalog room
  context, English strings, programming UI dead retry, backup-on-save wiring, audit clearing.
- Inferred: native multi-device ref-ID collision, backup-failure save block, stale filter/
  count text, segment edit impact on coupler lines.

## Not verified

- Real mouse/keyboard interaction with the dialogs and file pickers; no OS automation.
- Backup restore through the product dialog, and save with an existing/locked target.
- Encrypted/foreign project rejection, corrupt archive handling in the open dialog.
- Real bus, discovery, tunnel, monitor or download; all bus functions remain synthetic.
- Visual/DPI/keyboard acceptance, English visual acceptance, light/dark rendering.
- ETS roundtrip or interoperability of written archives.

## Reproduce

```powershell
dotnet build ets6.sln -c Release
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --workbench-selftest --theme=light --lang=de
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --workbench-selftest --theme=dark --lang=en
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --smoke
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --screenshot-selftest
dotnet run --project tests/Knx.Archive.Tests -c Release
# external harness (not in the repo):
cd C:\Users\julia\AppData\Local\Temp\opencode\wb-func\harness; dotnet run -c Release
```

Harness result at this SHA: `RESULT 50 passed, 2 failed` — the two failures are P1.1 and
P2.1 above.
