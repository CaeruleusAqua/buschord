# Workbench V4: visual/UX recheck after the P1/P2 fixes

Read-only recheck against the live integration checkout at **`fc9d1ea`** ("Add backup list and
restore flow to the workbench", worktree `C:\projects\ai_work\ets6-rebuild`). Branch
`agents/workbench-v4/ui-recheck`, worktree `C:\projects\ai_work\agent-wt\workbench-v4-ui-recheck`,
built detached at that SHA; this report is the branch's only change. Prior reports:
[design-visual](workbench-v4-design-visual.md), [design-ux](workbench-v4-design-ux.md).

Timeboxed pass: one build, the three harness selftests, and 35 captures (1440×900 and 1024×700;
light/dark; de/en) of `demo`, `demoparams`, `demomonitor`, `demoprogramming`, `demoinspector`,
`democatalog`, `demolinks`. `scan`, `changes` and `search` are **not** rechecked (no `--screen`
entry, external harness not built in time). Every claim below is either marked **measured**
(PNG pixel / geometry) or **source** (code read at this SHA).

## Harness results (pass)

| Command | Result |
|---|---|
| `dotnet build src\Knx.App\Knx.App.csproj -c Release` | 0 errors, 24 warnings |
| `BusChord.dll --workbench-selftest` | 49 PASS, 0 FAIL (`WORKBENCH NATIVE OK`) |
| `BusChord.dll --workbench-selftest --lang=en` | 49 PASS, 0 FAIL |
| `BusChord.dll --smoke` | `SMOKE OK` |
| `BusChord.dll --screenshot-selftest` | `SCREENSHOT OK` |

Captures: `%TEMP%\opencode\ui-recheck-shots` — `1440-<screen>-<theme>-<lang>.png` and
`1024-<screen>-<theme>-<lang>.png`.

## Verdict

The shell is visually coherent now: one teal accent inside the workbench, real icons for the
tool/caret/expand/close affordances, active rail and navigator states, an English shell that
matches the German one in every captured screen, and no mojibake in captured text. The 1024 px
inspector overlay survives unchanged from the earlier review, the dock clamp still misses 700 px
windows, the dock toolbar still truncates, and the new sidebar/catalog/backup work left a small
set of wording and state defects.

## P1 — residuals (unchanged, still visible)

**P1-1 At 1024×700 the inspector overlay still hides table columns and clips a control.**
`ApplyLayout` puts the inspector into column 3 as a right-aligned overlay and leaves the editor at
full width (`WorkbenchWindow.axaml.cs:1414-1423`); the table (`MinWidth 540`) fits the ~714 px
viewport, so the `ScrollViewer` has nothing to scroll while the last 342 px sit under the panel.
Measured in `1024-demoinspector-light-de.png` and `1024-demoinspector-dark-de.png`: inspector spans
x ≈ 682–1024; the header shows only "Objekt / Funktion" and "DPT" — "Länge", "Flags",
"Gruppenadressen" and their values are covered; the "Externe Kanäle" checkbox label is cut to
"Externe Ka" at the overlay edge. No shadow or elevation cue marks the covered area. Source: only
the subtitle gained `TextWrapping` (`WorkbenchWindow.axaml:76`), the layout itself did not change.
*Fix:* reserve the overlay width in the editor (or let the table scroll under a shadow), and stop
the toolbar from being clipped at the overlay edge.

**P1-2 At 1024×700 the dock still leaves one object row.**
The dock is 242 px tall at 1024×700; the clamp only applies below 700 px
(`WorkbenchWindow.axaml.cs:1437`, `Bounds.Height is > 0 and < 700`). Measured in
`1024-demomonitor-light-de.png`: table header at y ≈ 305, footnote at y ≈ 409, i.e. ~104 px for a
32 px row grid → one object row plus a sliver while 17 objects exist. At 1440×900 the same screen
is fine. *Fix:* clamp the dock to ≤40 % of the window for heights ≤ 760, not < 700.

## P2 — residuals and regressions

**P2-1 Dock toolbar truncates and its primary action is mid-row (residual).**
Measured at 1440 (`1440-demomonitor-light-de/dark-de`): order is `Stoppen | Anzeige leeren |
Wert … | [Gruppenmonitor] | Starten | [Alle Telegramme] | CSV gefiltert … | Voll-CSV … | ■
Gestoppt · 0 aufgezeichnet`; three labels ellipsize and "Starten" is the fifth control. At 1024 it
wraps to two rows with the same truncation. Reference: one wrapping row, selects ≤240 px, primary
first. *Fix:* primary first, export actions into an overflow menu, no ellipsized verbs.

**P2-2 Programming has no empty state (residual from UX-09/P2-9).**
`1440-demoprogramming-light-de.png`: device picker, "Auftrag einreihen", "Ausführen",
"Fehlgeschlagene wiederholen", the 5-column header (`Adresse Gerät Status Auftrag Hinweis`) and
then a blank body. Monitor's "Noch keine Aufzeichnung, Monitor starten." is the pattern that is
still missing here. The demand note above the table ("9 Gerät(e) mit Programmierbedarf gegenüber
dem zuletzt simulierten Erfolgsstand.") is also the longest line on screen and uses "Gerät(e)".

**P2-3 Parameter screen improved, not finished (residual of P2-6).**
`1440-demoparams-light-de/en.png`: "Übernehmen"/"Apply" is now the primary and sits first,
"Auswirkungen prüfen"/"Review effects" second, labels sit above their fields. Still missing: a
footer action bar, section headings, one field width (combo ≈100 px, text field ≈300 px), and the
form remains a ~310 px column inside a ~1100 px editor with no sub-navigation.

**P2-4 Catalog shows an error-coloured message before any interaction (new).**
`1440-democatalog-light-de.png` / `-en.png` / `1024-democatalog-light-de.png`: with no
product selected, the insert panel prints "kein Produkt ausgewählt" / "no product selected" in
the error tone and shows a disabled "Einfügen"/"Insert". Selection-specific empty states and
actions are otherwise good (icon + text + action in all three columns). The import status block itself is source-only:
`ShowImportReport` renders "last import · products/programs · broken/DCA/external" flags
(`CatalogPane.axaml.cs:147-160`), but no catalogue with an import report is available in the
isolated test store, so it is **not visually verified**.

**P2-5 English shell is clean in captures, but three source leaks remain (regression risk).**
Captured: menus, rail, perspectives, tabs, table headers, footer, status, monitor, parameters and
catalog are fully English (`1440-*-light-en`). Source-only leaks:
- `FillGroupSidebar` prints a hard-coded German `"{n} Gruppenadressen"` in both languages
  (`WorkbenchWindow.axaml.cs:625`);
- the same method still contains mojibake in a visible note: `Note("â€¦")` for >250 group addresses
  (`:629`) — the only `â€`-class corruption in product source; it would render for large projects;
- the new backup/restore surfaces are hard-coded German (`ConfirmDialog.AskRestoreAsync`,
  `ShowBackupsAsync`), and the backups dialog reuses `W("NoWorkspaces")` for "no project path" and
  "no backups" (`Commands.cs:214-231`), so English shows "No saved workspaces yet." in the wrong
  place.

**P2-6 Operations sidebar mislabels programming state (new).**
`FillOperationsSidebar` reports programming with the scan string (`WorkbenchWindow.axaml.cs:723`),
so a running simulated programming shows "Scan läuft ." / "Scan running ." — wrong feature name
and a stray space before the period (`WorkbenchStrings.cs`, `ScanRunning`). Monitor state in the
same block is correct.

**P2-7 Sidebar selector works, but the header duplicates it (new, small).**
The `SidebarMode` combo switches the panel body and rebuilds it per mode
(`WorkbenchWindow.axaml.cs:783-797`); the title is set to the selected item (`:786`), so the header
reads "Eigenschaften" twice next to the close button (`1024-demoinspector-*-de.png`). The selected
mode is not persisted across restart, and the search mode's query is discarded when switching
modes.

**P2-8 Contrast and selected/inactive rows.**
Inactive object rows now use `WbMuted` instead of the `.55` opacity (`axaml.cs:591`), which closes
the 3.75:1 finding. `WbSubtle` is unchanged at **4.41:1** on `WbPanel` (computed from
`WorkbenchTheme.axaml:57`), still 0.09 under AA for 9–10 px meta and address text. Dark pairs stay
above 5.9:1.

## P3 — polish

- Tool tab strip now fits all six tabs at both widths with visibly separated labels (≈20 px gaps)
  — UX-06 fixed.
- Doc tab still paints its accent on the bottom/right edge, not the reference's top
  (`WorkbenchWindow.axaml:23`); the new-tab affordance is still the raw glyph `+` (`:420`).
- Catalog column header is abbreviated to "Progr." in both languages and the English catalog mixes
  "catalog" and "catalogue" ("Product catalog" vs "Remove catalogue").
- Backup list rows are raw `yyyy-MM-dd HH:mm:ss · name · KiB` text with per-row "Wiederherstellen"
  quiet buttons and no heading; restore feedback goes to the 10 px status line.
- `ScanRunning`'s stray space before the period appears anywhere that key is used.

## Verified fixed (do not re-fix)

| Prior finding | Evidence at `fc9d1ea` |
|---|---|
| P1-1 blue Fluent accent leaking into V4 controls | Checked "Externe Kanäle" is teal in light and dark (`1440-demo-light-de.png`, `1024-demoinspector-dark-de.png`) |
| P1-2 invisible `↕` expand-all | Real `Icon Symbol="UnfoldMore"` 17 px at the toolbar end (`WorkbenchWindow.axaml:85`) |
| P1-3 no "you are here" | Rail entry active (tint) and navigator row `wb-row-active` with `WbSelectedText` (`axaml.cs:299-303, 535`) |
| P2-2 inactive view tabs at full weight | `wb-view-tab` foreground `WbMuted`; inactive labels visibly lighter (`axaml:18-19`) |
| P2-5 raw glyph affordances | Carets, tool float/maximize/hide, doc close and catalog search are `Icon`s now (`axaml:110-112, 122, 413`; `CatalogPane.axaml`) |
| P2-8 start surface | Start panel offers open/new/from-catalog plus recents (`axaml.cs:517-523`, source) |
| UX-06 tool tabs glued | 20 px spacing, six tabs fit at 1024 |
| UX-11 inactive row contrast | `WbMuted` instead of `.55` opacity |
| UX-18 English shell | Shell, tools, parameters and catalog captured in English; 262 `WorkbenchStrings` entries, no empty English value |

## Not rechecked (explicitly out of this pass)

Scan/changes/search tool views and `SearchPane`; dialogs (settings, new project, compare, replace,
confirm, backups) at native size; hover/pressed and `:focus-visible` rings; menu, tooltip and combo
popups; 125/150/200 % DPI; populated monitor/programming tables; the 10 027-row virtualization
case; a real catalogue import and its status block; dark English for all seven screens (only
`1440-*-dark-en` subset captured); backup/restore runtime behaviour.

## Reproduction

```powershell
dotnet build src\Knx.App\Knx.App.csproj -c Release
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --workbench-selftest
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --workbench-selftest --lang=en
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --smoke
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --screenshot-selftest
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --screenshot=C:\tmp\demo.png --screen=demo --theme=light --lang=de
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --screenshot=C:\tmp\insp-small.png --screen=demoinspector-small --theme=dark --lang=de
```
