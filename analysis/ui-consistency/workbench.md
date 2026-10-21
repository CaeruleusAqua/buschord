# Workbench UI consistency review — V4 concept

**Base** `bd51a8e6`; branch/worktree `agents/ui-consistency/workbench` verified clean. Reference: bundle `src/shell.html`, `docs/KONZEPT.md`. **Build** Release → 0 errors, 25 warnings.

**Evidence** (actual PNGs in `%TEMP%\opencode\ui-consistency`): E1 light/dark-demo-1440 · E2 demomonitor-1440 · E3 demoinspector-1440 · E4 demo-small 1024×700 · E5 demoinspector-small · E6 demo/demomonitor-tiny 900×600 · E7 demoinspector-tiny · E8 settings · E9 new-project · E10 en-demo · E11 democatalog.

**Limit** Compare/Replace/Confirm and menus are source-inferred; settings/new/splash are fixed-size. `demoprogramming` is missing because it crashes (P1-1).

## Matrix

| Surface | V4 expectation | Actual | Status |
|---|---|---|---|
| Topbar/menu | 49 px, paper, 12 px menus, search icon+kbd, amber dot pill, primary | 49 px/type match; glyph+kbd baked into one string; plain OFFLINE; no change count (E1) | Partial |
| Rail | 47 px, white active + 3 px accent bar, separators, issue count, monitor pulse | 47 px; tint active only on perspectives; no bar/badges/pulse (E1) | Gap |
| Navigator | 272 px, bordered segmented control, white active, 11 px rows, footer rule | Width/rows match; uncontained segments; no footer rule (E1) | Partial |
| Document tabs | 39 px; active white with 2 px accent **top** | 39 px; active white, accent **bottom** (E1) | Partial |
| Editor/title/toolbar | 22/17/12 px, 34 px tabs, 24/9 toolbar | Matches (E1) | Match |
| Object table | White 31 px header 10/600, 11 px rows, checkbox, GA chips, row action | `#f9fafb` header, regular 10 px; 32 px rows; plain GA; no checks/actions (E1/E2) | Gap |
| Inspector | 350 px; 12 px head; 15 px body; action footer | 350 px; 44 px head; actions inside scroll body (E3/E7) | Partial |
| Bottom dock | 242/35 px; 24 px compact toolbar | 242/35 px; 30 px toolbar; body lost at 900; tabs overlap controls (E2/E6) | Gap |
| Status bar | 27 px, 10 px, left project/issues teal | 27 px; left muted status; right local-only (E1) | Partial |
| Dialogs | radius 11, head 16, content/footer 22/13 | Settings matches; New/Compare/Replace legacy 12–14 px; Confirm no header (E8/E9) | Gap |
| States | Empty heading/icon, 2 px focus, neutral hover, 0.46 disabled | Focus/disabled exist; empty plain; row hover falls back to Fluent (E6/E11) | Partial |
| Wording/a11y | German default, English complete, names on icon controls | Shell localized; German leaks; names incomplete (E10) | Gap |
| Responsive | ≤1000 overlays sidepane, compacts chrome | Breaks at 1024/900 with inspector or dock open (E4–E7) | Gap |

## Findings

### P1
1. **Programming tool crashes.** `WorkbenchWindow.axaml.cs:1586-1587` adds `list` to `host` before removing it from `panel`, throwing “ListBox already has a visual parent DockPanel”. Reproduced light/dark via `--screen=demoprogramming`; no PNG. Fix: move `panel.Children.Remove(list);` above `host.Children.Add(list);`.
2. **Inspector at narrow widths clips primary UI.** `WorkbenchWindow.axaml.cs:1826-1849` keeps it in columns 4/5. At 900 (E7) title actions vanish, “Beziehungen” and “Kanäle” are cut; at 1024 (E5) “Kanäle” is still cut. V4 overlays `.sidepane` at ≤1000. Fix: below 1100 overlay it on the main column (right, z-order/shadow) or auto-hide; never shrink the editor.
3. **Tool tabs collide with window controls at 900.** `WorkbenchWindow.axaml:109` leaves an unclipped `StackPanel` in the `*` column beside float/maximize/close. E6 shows “Prüfmeldungen” under the icons. Fix: wrap `ToolTabs` in a horizontal `ScrollViewer` with `ClipToBounds`, or collapse labels below 1000.
4. **Perspective selector overlaps at 1024/900.** `WorkbenchWindow.axaml.cs:106-107` puts icon+label in a 3-column `UniformGrid` (`axaml:58`) inside 210 px. E4–E6 show clipped/overlapping “Gebäude/Topologie/Gruppen”. V4 `nav-kind` is text-only. Fix: drop icons here (keep rail icons) or go icon-only below 1100.

### P2
5. **Rail loses the V4 state language.** `axaml:10`, `.axaml.cs:110-124,321-325`: no white active surface, 3 px left accent, separators, issue count or recording pulse; catalog/monitor/settings never activate. Fix: distinct `rail-active` style set in `UpdateRail`, plus separators and badges.
6. **Active document tab rule is on the bottom.** `WorkbenchWindow.axaml:23` (`0,0,1,2`); V4 uses top 2 px. Fix: `0,2,1,0`, inner button 39→37.
7. **Shell controls use legacy ETS chrome.** `axaml:8-13`; `App.axaml:205-220,437-445`: buttons/fields inherit `#BFC5CB` borders and blue `#E1EDF8` hover, while Catalog uses `wb-button`/`wb-field` (WbLine/WbHover) (E1 vs E11). Fix: Window-level Button/TextBox `BorderBrush=WbLine`, `Background=WbPaper`, pointerover WbHover, or add `wb-*` classes.
8. **Dialog chrome is split.** `NewProjectWindow.axaml:5-11`, `CompareWindow.axaml:5-16`, `ReplaceProductWindow.axaml:5-10,32-37`, `ConfirmDialog.cs:34-61`, `Commands.cs:74-80` use legacy Window style and 12–16 px padding without a modal head; `SettingsWindow.axaml:5-7` uses WbBackground/Inter/22 px. `WbDialogRadius` is unused. Fix: one `.wb-dialog` style with 22 px content, 13 px footer, primary-last; apply in `CreateDialog`/Confirm.
9. **English leaks German.** `CompareWindow.axaml.cs:21,38`; `ReplaceProductWindow.axaml:10`; `Commands.cs:266-268,362` (“ja/nein”, “Vergleich:”). Fix via `Tr`/`WorkbenchStrings` keys.
10. **Dock toolbar too heavy; body disappears at 900.** `axaml.cs:1372-1450` uses 30 px controls; `:1858` floors dock height at 140. E6 shows a wrapped toolbar and no monitor rows/empty text. Fix: compact 26 px controls and keep ≥180 px or scroll.
11. **Table lacks V4 affordances.** `axaml.cs:812,843`: no checkbox column, plain GA text instead of chips, no row menu. Fix: checkbox column bound to `SelectedItems`, chip Border, row context menu.
12. **Help is an unscrollable message box.** `Commands.cs:90` + `ConfirmDialog.cs:34-61`: six sections in a fixed 440 px `StackPanel` with a hardcoded English title in German UI; can exceed small screens. Fix: help window with `ScrollViewer`/`MaxHeight` and localized heading.

### P3
- **Header/status hierarchy:** table header uses `WbPanel` + regular weight (`axaml:90-96`); V4 is white/600. Status-left muted (`axaml:129`) vs teal project/issues.
- **Empty states:** `axaml.cs:554-560,1461,1579` are plain text/buttons; V4 `.empty-state` has icon, 14 px heading, explanation.
- **Button order/destructive:** NewProject `:10-11`, ReplaceProduct `:28-29`, Compare `:14-15` put primary first; `ConfirmDialog.cs:99-119` makes teal Cancel primary for destructive actions and never uses `wb-danger`. Primary last, quiet cancel, danger destructive.
- **Accessibility:** `axaml:43` search name is the glyph-laden content; `axaml:85` expand lacks a name; `axaml:99` footnote is named “Objekt / Funktion”.
- **Chrome edges:** menus lack gesture hints (`Commands.cs:18`); splash radius 18/no progress (`SplashWindow.axaml:7-12`); floating tool lacks Wb chrome (`axaml.cs:1910`); Settings `quiet` undefined (`SettingsWindow.axaml:85`).

**Verdict:** geometry and tokens largely track V4; gaps are state language (rail/tabs/table), narrow-width composition, dialog chrome and the Programming crash. Fix P1-1 first, then inspector overlay and dock overflow.
