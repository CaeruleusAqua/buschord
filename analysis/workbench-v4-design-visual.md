# Workbench V4: visual design review of the whole app

> Integration status 2026-10-21: accent unification, active rail/navigation,
> document/view tabs, parameter footer, 900 px dock cap, muted inactive rows,
> selected-row text and text-budget fixes landed after this review. Residual items
> remain: legacy catalog styling, remaining glyph affordances, overlay elevation
> and full DPI/focus verification.

Review of the **current** integration tip `e56d8c76aebce7ab4ed64fde1b8287c443951019`
("Surface external channels, monitor origin and programming demand"), read-only from
`C:\projects\ai_work\agent-wt\workbench-v4-integration`. My earlier pass ran on `a6292e9d`;
everything that changed in between was re-captured and re-measured here, so the "fixed" list in
§3 is verified against the new build, not against the diff. Committed on branch
`agents/workbench-v4/design-visual`, worktree `C:\projects\ai_work\agent-wt\workbench-v4-design-visual`,
based on the same SHA. This report is the branch's only change.

> **The tip moved while this review was written:** `4c8426c` "Unify application accent and add
> active navigation states" now retargets `EtsAccent` to `#087E81` (`#62D1CD` dark), maps the Dock
> accent to it and adds active navigation classes. That overlaps **P1-1** and **P1-3** below. The
> measurements here are from `e56d8c7`; re-verify those two items against a build of `4c8426c`
> before acting on them. Everything else (§5 P1-2, P1-4, P1-5 and all P2/P3) was untouched by that
> commit and still stands.

Reference: `C:\Users\julia\Downloads\knx-workbench-v4-bundle` (`src/shell.html` measurements,
`src/app.js` behaviour), [design.md](ux/design.md), [icon-review.md](ux/icon-review.md),
[workbench-v4-acceptance.md](../workbench-v4-acceptance.md),
`src/Knx.App/Workbench/WorkbenchTheme.axaml`.

## 1. Method, and what is measured vs inferred

| Evidence | How | Class |
|---|---|---|
| Pixel colour, runs, column scans, contrast | 105 PNGs at 1:1 from `RenderTargetBitmap`; scripts listed in §9 | **measured** |
| Element geometry | live visual-tree `Bounds` dump from a throw-away harness that only constructs product windows | **measured** |
| Contract/reference comparison | `shell.html:9-22`, theme tokens, acceptance doc | source inference |
| Hover, `:focus-visible`, pointer states, menu popups, 125/150/200 % DPI, screen-reader order | no capture possible offscreen | **not verified** |

Screens: `%TEMP%\opencode\wb-visual-shots` — `cur/*` is the current build (e56d8c7),
everything else is named as in §8. `cur/` covers demo, params, relations, monitor, programming,
inspector, catalog, empty start, 1024×700 inspector, light+dark at 1440×900.

Two harness facts still distort evidence and are reported in P3-9: the built-in `--screenshot` path
renders *every* screen at 1440×900 (`Workbench/WorkbenchCheck.cs:107-108`), so the shipped
`--screen=settings|new-project` PNGs are stretched dialogs; and `-small` exists for only four of
seven workbench screens (`Screenshot.cs:24-26`), so narrow catalog/relations/programming cannot be
captured with it.

## 2. Verdict (current tip)

The shell now has the reference's *structure and rhythm* and most of the earlier shell breakage is
gone. What is left is one systemic problem and a tail of small ones:

- **Systemic:** the app still runs two design systems side by side — teal `Wb*` for the workbench,
  blue `Ets*`/Fluent for every dialog **and for the V4 controls that fall back to Fluent defaults**
  (measured: the *checked* state of the new "Externe Kanäle" checkbox is `#0b4f8a`, the dialog
  primary button is `#0b4f8a` at 28 px, the workbench primary is `#087e81` at 30 px).
- **Tail:** glyph affordances, light-theme contrast at 9–10 px, the parameter screen's information
  architecture, and a narrow layout that still loses content under the inspector/dock.

| Area | Grade | Reason (current) |
|---|---|---|
| Palette | **fail** | Teal workbench, blue dialogs *and* blue checkbox/focus surfaces (`App.axaml:136,242` vs `WorkbenchTheme.axaml:58`) |
| Typography scale | partial | 22/12/11/10 ladder correct; 9 px used for table headers/meta, 13 px base never visible |
| Spacing / rhythm | good | Shell numbers match the reference after the e56d8c7 fixes (view gap 20, tool gap 14, rows 32) |
| Button variants / heights | partial | 4 "active" idioms, quiet buttons read as labels, 30 px workbench vs 28 px dialogs |
| Inputs | partial | Fields 28 px vs documented 30; combos sized by content (110/130/190 px) in one row |
| States | partial | Selection now tinted, but selected text ignores `WbSelectedText`; disabled = `.46` opacity; empty states still missing in 3 places |
| Tabs / nav / table / inspector / dock | partial | Doc/tool/view tabs fixed; rail and navigator still show no "you are here"; inspector is a flat list |
| Icons vs text glyphs | **fail** | 8 raw glyphs; `↕` still renders as a 3 px stroke (now with an accessible name only) |
| Dark contrast | good | Every measured dark pair ≥ 5.9:1 |
| Light contrast | partial | `WbSubtle` improved 2.93 → **4.41** on panel, still under 4.5; inactive rows still 3.75 |
| Responsive 1024 / 900 | **fail** | Inspector overlay hides 3 of 5 table columns at 1024 and clips the subtitle; dock at 900 leaves 0 rows |
| Legacy vs V4 | partial | "Beziehungen" now native (fixed); catalog pane is still the old Fluent pane |

## 3. Verified fixed since `a6292e9` (do not re-fix)

| Was | Now | Evidence |
|---|---|---|
| Dock tool tabs glued together ("MonitorProgrammierungPrüfmeldungen") | 14 px spacing, readable | `cur/cur-demomonitor-light.png`, `WorkbenchWindow.axaml:107` |
| Document tab was a teal chip, theme's tab treatment dead | real `Border.wb-doc-tab` with paper fill and accent edge, 39 px, 110–220 px | `WorkbenchWindow.axaml:20-21`, `axaml.cs:365-372` |
| View-tab gap 12 vs reference 22 | 20 px | `WorkbenchWindow.axaml:75` |
| Inactive view tabs at full text weight | active label now `WbSelectedText` (still needs the muted fix, P2-2) | `WorkbenchWindow.axaml:17` |
| Table rows 34 px vs `WbRowHeight=32` | **32 px measured** (y 301..332) | `cur/runs.txt`, `axaml.cs:506` |
| Row fills painting over the selection tint; off-palette `#e6e6e6` band on the first row | gone in the plain state; selection = `WbTint` | `findcolor` on `cur-demo-light` finds `#e6e6e6` only in the header rule (y=300) |
| Table header had no rule and 5 px-wrong column widths | header band with 1 px bottom border, columns `*,80,50,50,170`, `MinWidth 540` | `WorkbenchWindow.axaml:87-95` |
| Narrow nav labels clipped ("Gebäud/Topolog/Gruppe") | 10 px, `Padding 2`, `MinWidth 0` → "Gebäude Topologie Gruppen" fits | `cur/cur-demoinspector-small-light.png`, `axaml.cs:88-90` |
| Relations view was the legacy `GroupLinksPane` | native card list with per-link "Entfernen", explicit sending label, empty note | `axaml.cs:711-762`, `cur/cur-demolinks-light.png` |
| Monitor/programming were unstyled text lines | 7-/5-column tables with headers, DEMO badge, need-count note, actionable empty state | `axaml.cs:846-905`, `cur/cur-demomonitor-light.png`, `cur/cur-demoprogramming-light.png` |
| Glyph buttons had no accessible name | `AutomationProperties.Name` on float/maximize/hide/expand-all/inspector-close | `axaml:83,108-110,112` |
| `WbSubtle` 2.93:1 | `#6b7686` = **4.41:1** on `WbPanel`, `#a9b7c9` = 7.29:1 dark | `WorkbenchTheme.axaml:57,84`, §5 contrast table |

## 4. Measured against the reference (current)

| Element | Contract | Measured now | Verdict |
|---|---|---|---|
| Topbar / rail / navigator | 49 / 47 / 272 | 49 / 47 / 272 (210 < 1100) | ok |
| Document tab | 39 h, 130–245 w, accent **top** | 39 h, 110–220 w, accent **bottom + right** | idiom differs |
| View tab / gap | 34 / 22 | 34 / **20** | ok-ish |
| Tool tab / gap | 35 / 20 (reference) | 35 / **14** | ok-ish |
| Table header / row | 31 (`WbTableHeader #fff`) / 32 | 30 + 1 px rule, fill `WbPanel` / **32** | row ok, header token unused |
| Navigator row | 29 | 29 | ok |
| Button | 30 | 30 workbench / **28** dialogs (`dlg-confirm-light` blue fill y 64..91) | split |
| Field | 30 | **28**; combos 110 / 130 / 190 px in one toolbar row | drift |
| Checkbox | — | 22×22 visual, checked fill `#0b4f8a` | palette leak |
| Inspector / dock / status | 350 / 242 / 27 | 350 / 242 / 27 | ok |
| Radius / splitter | 5·6 / 5 | 5·6 / 6 | ok |
| Scrollbar | overlay | 16 px with up/down arrow buttons | legacy |

Light contrast (computed from measured hex):

| Pair | Ratio | Use | Verdict |
|---|---|---|---|
| `#6b7686` on `#f9fafb` | **4.41** | 9–10 px meta text | **still 0.09 short of AA** |
| `#6b7686` on `#ffffff` | 4.60 | same on paper | ok |
| `#828486` on `#ffffff` | **3.75** | inactive object rows (`.55` opacity, `axaml.cs:538`), 11 px | fail |
| `#087e81` on `#e4f2f1` | **4.24** | active perspective segment label, 10 px (`axaml.cs:88-90`) | fail |
| `#687487` on `#ffffff` / `#f9fafb` | 4.73 / 4.53 | muted text | ok |
| `#ffffff` on `#087e81` | 4.87 | primary button | ok |
| dark `#a9b7c9`/`#b0bdce`/`#62d1cd` on `#202833`,`#171d25` | 7.29 / 7.80 / 9.30 | dark meta/accent | ok |
| `#a5efea` on `#25484b` | 7.66 | dark selected text (never rendered, P2-5) | ok |

## 5. Findings

### P1 — fix before the next milestone

**P1-1 One product, two primaries — now provably leaking into a V4 control.**
`App.axaml:136` sets the Fluent accent to `#0B4F8A`, `App.axaml:242` makes `Button.primary` that
blue at 28 px (`:168-178`), `WorkbenchTheme.axaml:58` and `:151-166` say teal `#087e81` at 30 px.
Measured in the current build: the **checked** "Externe Kanäle" checkbox is `#0b4f8a`
(`rectstat` 1022..1044 × 234..256, mode `#0b4f8a` 322 px) directly beside the teal primary
"Programmieren"; `dlg-confirm-light.png` primary fill is `#0b4f8a` at 28 px. A V4 control now
carries the legacy accent, so this is no longer only a "dialogs look old" problem.
*Fix:* one accent token. Retarget `Wb*` to the brand blue **or** re-point the Fluent palette and
`Ets*` to the teal, and delete the duplicate dictionary. Until then every Fluent fallback
(checkbox, focus ring, selection, menu highlight, dock accent) silently reverts to blue.

**P1-2 The expand-all control is still an invisible glyph.**
`WorkbenchWindow.axaml:83`: `Content="↕"`, `quiet`, after the 170 px filter. Measured: a ~3×14 px
vertical stroke that reads as a stray "I" at the right edge of the object table
(`cur/cur-demo-light.png` at x≈1400, detail `c-artifact-demo.png` 6×). `AutomationProperties.Name`
was added, which helps screen readers but not sighted users.
*Fix:* `views:Icon` with an unfold symbol (the icon set already ships Material symbols), or a
labelled button in the toolbar group. Same defect family as P2-6.

**P1-3 The navigator and the rail still do not say where you are.**
`axaml.cs:88-97` never sets an active class on the seven rail buttons; `RefreshNavigation`
(`axaml.cs:487-497`) sets `NavTree.SelectedItem`, yet the row for the open work item renders the
same `#f9fafb` as its siblings (`rectstat` 62..302 × 312..332 vs 62..302 × 342..362 in
`cur/cur-demo-light.png`). Only the segmented perspective control shows state. Reference: active
rail button = accent on white with a 3 px left bar (`shell.html:13`).
*Fix:* `Button.active` (`axaml:12`) on the rail entry of the active perspective/tool, and one
`wb-row` selection style for the tree (P2-5).

**P1-4 At 1024 px the inspector hides table columns with no way to reach them.**
`ApplyLayout` (`axaml.cs:1079-1088`) overlays a 350 px inspector over column 3 and leaves the editor
full width beneath it. Measured in `cur/cur-demoinspector-small-light.png`: the header shows
"Objekt / Funktion … DPT" and stops — "Länge", "Flags", "Gruppenadressen" sit under the overlay;
the table is `MinWidth 540` inside a ~714 px viewport, so the `ScrollViewer` has nothing to scroll
and the columns are unreachable while the inspector is open. The subtitle and the "Externe Kanäle"
checkbox label are also cut mid-word at the overlay edge (`…externe Ka`, `Externe`).
*Fix:* when the overlay is open, either shrink the editor to the free width (and let the table
scroll) or reserve the overlay width in the layout; give the overlay an elevation cue so covered
content reads as covered rather than broken.

**P1-5 At 900×600 with the dock open the work item has no rows.**
`axaml.cs:1102` gives the dock a fixed `WbToolHeight=242` and the editor has no minimum height, so
the table keeps only its header and footnote (measured at `a6292e9`, `hx-wb900-monitor-light.png`;
`ApplyLayout` is byte-identical at `e56d8c7`, so the behaviour is unchanged — the fix is not in the
diff). The same shot shows the dock toolbar wrapping into two rows.
*Fix:* editor `MinHeight` ≈160 px and a dock height clamped to ≤40 % of the window below 1100 px.

### P2 — clearly wrong, needs a decision

**P2-1 The dock toolbar truncates at 1440 and its primary action is in the middle of the row.**
`cur/cur-demomonitor-light.png`: order is `Stoppen | Anzeige leeren | [Gruppenmonitor] |
Starten (primary) | [Alle Telegramme] | CSV gefiltert … | Vollaufzeichnung … | ● Gestoppt ·
0 aufgezeichnet`, with "Vollaufzeichnung …" already ellipsized at 1440 px and the DEMO badge pushed
to a second line. The committing action sits between two text buttons and two selects; a user
scanning left-to-right reads three disabled-looking controls before the primary one. Reference:
one wrapping tool row, selects ≤240 px, primary first (`shell.html:18`).
*Fix:* wrap the row (it already does at 900) and put the primary first; move the three export/
tool-window actions into an overflow menu instead of ellipsizing them.

**P2-2 Inactive view tabs still carry full text weight.**
Measured `#202c3c` on "Parameter / Beziehungen / Prüfung" (`rectstat` 510..560 × 192..212), i.e.
the same colour as the title block; only the underline separates the active tab. Reference:
`.view-tab{color:#7e899a}`. `axaml:17` fixed the *active* label but the inactive one is inherited
from `App.axaml:159` (`TextBlock` → `EtsText`).
*Fix:* `WbMuted` for inactive (`4.53:1`, AA-clean at 12 px).

**P2-3 Light-theme contrast is improved but not closed.**
`WbSubtle` `#6b7686` measures **4.41:1** on `WbPanel` — 0.09 under AA for 9–10 px meta text — and
the inactive-row treatment `Opacity=0.55` (`axaml.cs:538`) measures **3.75:1** for 11 px names,
whereas dark improved to 7.29.
*Fix:* collapse `WbSubtle` into `WbMuted` (`#687487`, 4.53:1 on panel — one token fewer), and
replace the `.55` opacity with `WbMuted` plus the existing "inaktiv" tooltip text.

**P2-4 Selection is a tint, not a selected row.**
With an explicit selection the row paints `WbTint` (`#e4f2f1` light, `#233f43` dark —
`hx-select-light/dark`), but the label keeps the default text colour; `WbSelected`
(`#dff0ef`) and `WbSelectedText` (`#a5efea`/`#176b6e`) have **zero** references outside
`WorkbenchTheme.axaml`, so the states the theme documents never render. A separate, unresolved
row-state artefact remains: after the dock opens, two object rows render `#e6e6e6` (neutral,
not a `Wb*` value) from y 493..556 in `cur/cur-demomonitor-light.png`, while the same rows are
clean in `cur/cur-demo-light.png` — a container-state leak, not a style.
*Fix:* drive `WbSelected`/`WbSelectedText` through the row template and reset container backgrounds
on `ItemsSource` change.

**P2-5 Eight raw text glyphs stand in for the icon set.**
`⌕` palette (`axaml:41`), `↕` expand-all (`:83`), `↗ □ ×` tool window (`:108-110`), `×`
inspector close (`:117`) and document close (`axaml.cs:369`), `⌄ › ·` tree carets
(`axaml.cs:489,515`). The caret set renders as "v" and "›" in the same tree; `↕` is illegible
(P1-2); `.wb-icon` (`WorkbenchTheme.axaml:302`) has no call site. [icon-review.md](ux/icon-review.md)
already demands the `Icon` control and an 18 px floor for detailed glyphs.
*Fix:* one `Icon` per affordance (`UnfoldMore`, `OpenInNew`, `Close`, chevrons), text only where a
word carries meaning.

**P2-6 The parameter screen is an unfinished form.**
`cur/cur-demoparams-light.png`: a single ~300 px column in a 1100 px editor, no sub-navigation, no
section headings, no footer; inputs sized by content (combo ≈110 px, text field 300 px) although the
reference stretches fields to 540 px; the **primary** button is "Auswirkungen prüfen" (preview) and
the committing "Übernehmen" is the secondary below it, stacked. Reference: `parameter-nav` 177 px,
`.field{max-width:540}`, `parameters-footer` bar (`shell.html:19`).
*Fix:* footer row with "Übernehmen" primary, one field width, section headings from the reference.

**P2-7 Text budget and internal wording in the UI.**
Subtitle 99 chars (`axaml.cs:420`), table footnote 81 (`:441`), inspector note 113
("… Geräteübergreifende atomare Zuordnung wird separat integriert.", `:615`) — D6 allows 80 and
forbids implementation notes. The inspector also shows `Objekt-ID:` and "DPT: unbekannt ·
Länge: unbekannt Bit" placeholders.

**P2-8 Start surface still lacks the catalog entry point it advertises.**
`axaml.cs:418` promises "Ein Projekt öffnen, neu anlegen oder den Produktkatalog durchsuchen.",
`axaml.cs:473-474` renders only "Projekt öffnen …" and "Neues Projekt …", plus recent projects as
plain 12 px text with no icon, path or affordance. D1/D3 require the catalog on the start surface.

**P2-9 Empty states still missing where the user lands.**
Compare and Report open completely empty grids (`dlg-compare-light.png`, `dlg-report-light.png`);
programming has a table and a need-count note but no "no jobs" text when empty; the catalog tab
shows "keine Produkte im Katalog" centred without an action inside the table. Monitor and relations
are now good — use them as the pattern.

**P2-10 Three "active" idioms, and the document tab diverges from the reference edge.**
Document tab = paper + accent **bottom/right** (`axaml:20-21`), view/tool tab = accent text +
bottom rule, perspective = tint chip, rail = nothing. The reference has exactly two: full-height
document tab with the accent on the **top** edge, underlined text for views (`shell.html:15-16`).
*Fix:* pick the reference's two and align the document tab's accent edge.

### P3 — polish, tooling

**P3-1** Type scale drift: 9 px in three places (`axaml.cs:850` and `:930` headers, `:489` nav meta)
although `WbFontSizeCaption=10` is the documented minimum; the 13 px base is never
visible in the shell; `FontSize=20` is still assigned to a button whose content is a 19 px `Icon`
(`axaml.cs:94`).

**P3-2** Legacy 16 px scrollbars with `PART_LineUpButton`/`PART_LineDownButton` arrows in the V4
table (`Bounds` dump), where the reference uses platform overlay scrollbars.

**P3-3** Inputs: fields 28 px everywhere while the contract says 30 (`WorkbenchTheme.axaml:281-290`);
three combos in the monitor row at 110/130/190 px width; the checkbox is 22 px tall against a 28 px
field in the same row (2 px baseline offset measured earlier at `y=0` vs `y=2`).

**P3-4** Status bar has no hierarchy: both zones 10 px `WbMuted` on `#f3f5f8`; reference colours the
left status zone (`#558f8c`) and separates the zones.

**P3-5** Header actions read as labels: "Beobachten", "+ Gerät", "Eigenschaften"
(`axaml:70-72`) are `quiet`, borderless, 12 px — and "Eigenschaften" appears twice on the same
screen (`:72` and `:79`), one borderless and one bordered.

**P3-6** Inspector is a flat list, not an inspector: no section headers, no property grid, no
footer; three action styles stacked (primary, bordered, borderless 12 px "Parameter öffnen").
Reference: `side-section` + `property-list` (105 px label column) + `side-footer`.

**P3-7** The theme contract is documentation, not code: `WbRowHeight`, `WbButtonHeight`,
`WbViewTabGap`, `WbIconButtonSize`, `WbSmallRadius`, `WbGapLarge`, `WbEditorPadding`,
`WbTableHeader`, `WbSelected`, `WbSelectedText`, `WbDisabled`, `WbInfo`, `WbAccentHover`,
`WbMonoFontFamily` are referenced only inside `WorkbenchTheme.axaml`; `wb-row`, `wb-field`,
`wb-column-header`, `wb-tab`, `wb-icon`, `wb-compact`, `wb-title`, `wb-heading`, `wb-mono` have
zero call sites, while the app repeats `29/32/39/22/19/10/9` inline. Every drift in §4 is a
consequence.

**P3-8** Dialogs: no in-body heading (ConfirmDialog renders the message bare,
`ConfirmDialog.cs:38-58`); primary-action convention inconsistent (Compare has a blue primary
"Als CSV speichern…", Report has the same action as secondary, Replace has none and exposes
manufacturer refs such as `M-0083_A-008A-25-6CE4`, against D6).

**P3-9** Harness gaps: dialogs forced to 1440×900 (`Workbench/WorkbenchCheck.cs:107-108`) so
`wb-settings-*` / `wb-new-project-*` are unusable as dialog evidence; no `-small` variant for
catalog/relations/programming (`Screenshot.cs:24-26`); `Screenshot.Write` renders at
`new Vector(96,96)`, and no 125/150/200 % capture exists anywhere, so A02's "100/150/200 % native
acceptance" remains open by construction.

**P3-10** The legacy `MainWindow` is unreachable but still compiled (no `new MainWindow(` in product
code; `App.axaml.cs:50-52` builds `WorkbenchWindow`), together with the whole `Ets*` palette and
Dock accent mapping (`App.axaml:44-133`). It is the only place with the reference's start cards
(`hx-main-light.png`) — worth reusing before deleting.

## 6. Source-audit only (not visually verified here)

Focus ring colour (blue `App.axaml:187` vs teal `WorkbenchTheme.axaml:311` — a programmatic
`Focus(NavigationMethod.Tab)` produced no visible ring offscreen, so the winner is unknown);
hover/pressed states; menu, tooltip and combo popups (Fluent-styled, not themed by `Wb*`); text
selection/caret colours in V4 fields; floating tool window chrome; interaction-only states
(multi-select inspector, batch-link preview, populated monitor/programming tables); real DPI
scaling and screen-reader order.

## 7. Top fixes, in landing order

1. **One accent, one metric set** (P1-1, P3-3, P3-8): single token dictionary; delete the duplicate;
   point the Fluent palette, dock accent and dialogs at it. Fixes the blue checkbox today.
2. **Orientation** (P1-3, P2-4): rail active class, navigator current row, `WbSelected`/
   `WbSelectedText` through the row template, reset recycled container backgrounds.
3. **Affordances** (P1-2, P2-5, P2-10): icon set instead of 8 glyphs, two active idioms,
   document-tab accent on the reference edge.
4. **Narrow layout** (P1-4, P1-5): reserve or shadow the inspector overlay, let the table scroll,
   editor `MinHeight`, dock clamped below 1100 px.
5. **Contrast** (P2-3): `WbSubtle → WbMuted`, drop the `.55` opacity, darken the active-chip label.
6. **Parameter screen** (P2-6): footer with "Übernehmen" primary, one field width, section headings.
7. **Empty states + text budget** (P2-8, P2-9, P2-7): shared empty state (monitor's is the model),
   catalog on the start surface, 80-char cut, remove the integration note and internal IDs.
8. **Bind code to the tokens** (P3-7) so 1–7 cannot drift back.

## 8. Screenshot index

`%TEMP%\opencode\wb-visual-shots`

- `cur/cur-{demo,demoparams,demolinks,demomonitor,demoprogramming,demoinspector,democatalog}-{light,dark}.png`
  and `cur/cur-demo-small-*`, `cur/cur-demoinspector-small-*` — **current build e56d8c7**
- `wb-*`, `hx-*`, `dlg-*` — earlier `a6292e9` pass (1440×900, 1024×700, 900×600, dialogs at native
  size); still valid for anything §3 does not list as changed
- crops for detail evidence: `c-topbar-light`, `c-rail-light`, `c-header-light`, `c-table-light`,
  `c-tabs-light`, `c-tabs-dark`, `c-dockhead-prog`, `c-artifact-demo`, `c-artifact-insp`,
  `c-select-light`, `c-focus-light`

## 9. Reproduction

```powershell
# current build, built-in harness (1440x900; -small = 1024x700)
dotnet build src\Knx.App\Knx.App.csproj -c Release --nologo        # 0 errors
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --screenshot=C:\tmp\demo.png --screen=demo --theme=light
dotnet src\Knx.App\bin\Release\net9.0\BusChord.dll --screenshot=C:\tmp\demo-small.png --screen=demoinspector-small --theme=dark

# external harness (not committed), for 900x600 and the legacy windows
harness.exe --window=workbench --state=monitor --w=900 --h=600 --theme=light --out=C:\tmp\wb900.png
harness.exe --window=compare --w=900 --h=640 --theme=dark --out=C:\tmp\compare-dark.png
```

Measurement scripts live beside the PNGs: `crop.ps1`, `probe.ps1`, `rectstat.ps1`, `analyze.ps1`
(colour histogram + vertical runs), `findcolor.ps1`, `textruns.ps1`, `contrast.ps1` (WCAG).
