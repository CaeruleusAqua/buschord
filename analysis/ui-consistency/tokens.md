# Token and visual-state audit

Read-only. Branch `agents/ui-consistency/tokens` at `bd51a8e`; product source untouched.
Inputs: all 19 `Knx.App/**.axaml`, `App.axaml`, `WorkbenchTheme.axaml`, shell code-behind, and the
concept CSS (`knx-workbench-v4-bundle/src/shell.html:9-26`). Screenshots: `--screenshot --theme=light|dark`
for `demo`, `democatalog`, `demomonitor`, `settings`, `new-project` (10 PNGs, kept outside the repo).

## Inventory

Two complete, independent palettes are live at once.

| | `Ets*` (App.axaml) | `Wb*` (WorkbenchTheme) | Concept |
|---|---|---|---|
| background | `#EFF1F3` | `#f5f6f8` | `#f5f6f8` |
| panel | `#FFFFFF` | `#f9fafb` | `#f9fafb` |
| border | `#BFC5CB` | `#e1e5eb` | `#e1e5eb` |
| muted | `#4C5560` | `#687487` | `#687487` |
| error / warning / ok | `#A31B12` / `#7A5200` / `#14603A` | `#b84245` / `#996415` / `#268175` | `#b84245` / `#996415` / `#268175` |
| control radius | 4 | 5 (6 panel, 11 dialog, unused) | 5 button, 6 card, 11 modal |
| font | Segoe UI 12.5 | Inter 13 (window forces 12) | Inter 13 |
| selection | `#E4F2F1` + `#202C3C` | `#dff0ef` + `#176b6e` | `#dff0ef` + `#176b6e` |

80 hex literals in `App.axaml` (39 keys ×2 themes: 18 `Ets*`, 6 Fluent-override, 11 Dock, 4 tooltip),
48 brushes in `WorkbenchTheme.axaml` (24 ×2). Usage: 160 `Wb*` refs in 5 files, 101 `Ets*` refs in 9.
**5 of 7 panes are legacy** (Building, GroupLinks, Search, BusMonitor, GroupMonitor) and **5 of 6
dialogs** (Compare, NewProject, ReplaceProduct, Report, Splash); only Catalog/Parameter panes and
Settings are `Wb`.

Typography: 12 distinct sizes are in use — 9, 10, 10.5, 11, 12, 12.5, 13, 14, 16, 17, 20, 22, 24 —
against a 6-step scale (10/11/12/13/17/22). Geometry: 30 button / 26 compact / 28 icon-button agree
across both palettes, but padding does not (10,5 vs 10,0 vs 8,3) and **button styling is layered four
times** (`App.axaml:205`, `WorkbenchWindow.axaml:8`, `WorkbenchTheme.axaml:151`, `CatalogPane:36`).

Icons: seven sizes (14/16/17/19/20/22/32) against tokens 14/17 (+legacy 16/20/32).

Contrast (Computed WCAG): all text pairs pass except **disabled 1.85:1 light / 2.39:1 dark**,
`WbMuted` on statusbar 4.33, tabbar 4.26, rail 4.14 (all 10 px), and Fluent's unthemed placeholder
2.6:1. Concept's own statusbar is 2.96:1, so 4.33 is not a regression.

## P1

1. **Selected rows never get the selection tokens.** Object table, monitor and catalog rows paint
   Fluent grey `#E6E6E6` (sampled in `demo-light.png`), while navigator rows paint `#dff0ef`. Cause:
   `App.axaml:379`, `WorkbenchWindow.axaml:15`, `CatalogPane.axaml:46,53` target
   `/template/ ContentPresenter` without the `#PART_ContentPresenter` name, so Fluent's named rule
   wins. The only correct rule, `ListBoxItem.wb-row` (`WorkbenchTheme.axaml:254-271`), is applied by
   no view. Fix: add `#PART_ContentPresenter` to those four selectors.
2. **Disabled buttons are unreadable.** `WorkbenchTheme.axaml:319` sets `Opacity 0.46` *on top of*
   `WbDisabledText` `#687487` on `WbDisabled` `#eef0f4` → 1.85:1 (2.39 dark). Visible on "Katalog
   entfernen"/"Einfügen" (`democatalog-light.png`); legacy dialogs are 4.8:1. Fix: delete the
   `Opacity` setter for buttons, keep the foreground setters.
3. **`Classes="quiet"` silently degrades in dialogs.** It is defined only in
   `WorkbenchWindow.axaml:9`; `SettingsWindow.axaml:85` therefore renders a bordered Cancel inside an
   otherwise borderless button set. Fix: move `Button.quiet` beside `Button.tool` (`App.axaml:251`).
4. **Settings mixes both skins.** Cards use `WbLine`/`WbRadius` (`SettingsWindow.axaml:10-16`), but its
   four fields inherit `App.axaml:437,454` → `#BFC5CB` borders, radius 4, 30 px, inside 6 px cards
   (visible in `settings-light.png`). Fix: `Classes="wb-field"` on `SettingsWindow.axaml:41,48,72,75`.

## P2

5. Pane-header height has three values: `panelHeader` padding 8,5 (`App.axaml:353`) ≈22 px, dock head
   35 (`WorkbenchWindow.axaml:108`), catalog sections 40 (`CatalogPane.axaml:128,174,222`).
6. `.muted` is 10 px in the shell (`WorkbenchWindow.axaml:13`) but 11 px via `wb-muted`
   (`WorkbenchTheme.axaml:127`) and in dialogs (`App.axaml:426`); drop the shell's `FontSize`.
7. Legacy panes draw `#BFC5CB` borders next to `#e1e5eb` (`App.axaml:348-364`); repoint
   `panel`/`panelHeader`/`columnHeader` at the `Wb*` keys.
8. Empty states: 32 px icon (`BusMonitorPane:32`, `GroupMonitorPane:65`) vs 22 px
   (`CatalogPane:160,207,280`); rail icons 19 (`WorkbenchWindow.axaml.cs:110,123`) vs the concept's 17.
9. One toolbar mixes 30 px primary ("Starten", measured 30) with 26 px bordered/quiet buttons
   (`demomonitor-light.png`) — pick one height per row.
10. No width breakpoints: `WorkbenchWindow.axaml:51` pins `47,272,5,*,0,0` and `:89` sets table
    `MinWidth=540`; the concept switches navigator 272→238→210 and overlays the sidepane at 1250/1000 px.
    At 1024 the table is squeezed to ~350 px, so 1024/900 cannot be validated: the harness pins
    1440×900 (`Screenshot.cs:20`).
11. `NumericUpDown` spinner buttons stay Fluent grey (`App.axaml:468` styles the box only;
    `NewProjectWindow.axaml:34,38`), visible as two grey squares beside white fields.

## P3

12. Radius literals: `ParameterPane.axaml:12` (9), `:46` (6), `SplashWindow.axaml:7` (18);
    `WbDialogRadius` (11) is never used.
13. Selection tint used three ways: `WbTint` (`WorkbenchWindow.axaml:15`), `WbSelected`
    (`WorkbenchTheme.axaml:266`, `CatalogPane.axaml:47`), `WbTint` pill (`ParameterPane.axaml:12`).
14. Device rows reuse `WbHover` (`WorkbenchWindow.axaml:26`), i.e. the hover colour, in both themes.
15. Row heights 32/29/31 (`WbRowHeight`/`WbTreeRowHeight`/`WbTableHeaderHeight`) vs the concept's 29/29/31.
16. Tooltip radius 4, padding 8,5 (`App.axaml:172`) vs the concept popup's 7 with shadow; the repo has
    no `BoxShadow` at all.
17. Doc-tab font 12.5 (`App.axaml:157`) vs 11 in both the concept (`shell.html:15`) and `WbFontSize`.
