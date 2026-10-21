# UI consistency — final decisions

Audit basis: `ac0480a4710a6a8109b5b54361127cc13debd00d`, branch
`agents/final/acceptance`, 2026-10-21. This ledger closes the P1/P2 items from
`tokens.md`, `workbench.md`, `panes.md` and `workbench-v4-a11y-recheck2.md`.
Status values: **fixed** = verified in source or by the native run;
**open** = reproduced, must still be fixed; **accepted** = deliberate limit or
documented decision, not a bug to fix in this stage. No item is marked fixed
without a source line or run.

> **Stale-base warning (read first).** This ledger records `ac0480a`. Main is at
> `f9d0e7f`; the following rows are **superseded in source and were not re-run**:
> T2 (disabled contrast, `45f035b` + `f9d0e7f`), T4, T7, W8, P3, P5, P6, P8,
> P11, T11, W12 (dialog/help work, `f605870`), P1-search and P7
> (`39be758`/`ab0f8a4`), compare CSV (`f605870`). Their base evidence is kept
> for traceability. **Still open at
> main HEAD**: T1 residual, T6, T9, W2, W5, W7, W9, W11, P2 (two parameter UIs),
> P4, F6, real R04/F13. The exact list is §7 of the acceptance report.

## Contrast and tokens (tokens.md)

| ID | Item | Status | Evidence / decision |
| --- | --- | --- | --- |
| T1 | Selected rows never get selection tokens (missing `#PART_ContentPresenter`) | fixed (source), residual | `App.axaml:362,366` now name the part; `WorkbenchTheme.axaml:266` already did. Local duplicates `WorkbenchWindow.axaml:15` and `CatalogPane.axaml:46,53` still omit the name but sit closer to the control; no selected catalog/table row was re-photographed. Re-check visually before release. |
| T2 | Disabled buttons unreadable (`Opacity 0.46` on top of tokens) | **[superseded] fixed at main HEAD** | At base: `WorkbenchTheme.axaml:319-320` set `Opacity 0.46`; rendered `1.74:1` light / `2.31:1` dark. `45f035b` removed the opacity and set `#5f6b7d`; `f9d0e7f` asserts the token pair (4.74:1 light). Not re-run. |
| T3 | `Button.quiet` undefined in dialogs | fixed | `App.axaml:194` defines it globally; `SettingsWindow.axaml:86` uses `Classes="quiet"`. |
| T4 | Settings mixes both skins | **open (P2, reduced)** | Fields still have no `wb-field` (`SettingsWindow.axaml:37-86`); captured `shots/light-settings.png` shows the legacy darker border/4 px radius. Cosmetically small, not a blocker. |
| T5 | Pane-header heights differ | accepted | 22/35/40 px by surface (panel/dock/catalog); no user-visible break found at 900–1440. |
| T6 | `.muted` 10 px vs 11 px | open (P3) | `WorkbenchWindow.axaml:13` still forces 10 px in the shell. |
| T7 | Legacy `panel` borders next to `Wb` | accepted | Only `SearchPane` remains legacy (`SearchPane.axaml:11,35`); it is now forced maximized (`WorkbenchWindow.axaml.cs:1507-1510`), so it reads as a separate tool page, not as a V4 pane. |
| T8 | Empty-state icon sizes / rail icon 19 vs 17 | accepted | Cosmetic; concept rail is 17, native 19 with a 35 px target. No clipping. |
| T9 | Toolbar mixes 30 px and 26 px | open (P3) | Monitor toolbar: primary Start is 30 px, neighbours 26 px (`CreateMonitorView`). |
| T10 | No width breakpoints | fixed | `ApplyLayout` `:2107` implements <1100: nav 210, inspector overlay, icon tool tabs `:2162`, two-row object toolbar `:2181`. |
| T11 | `NumericUpDown` spinners stay Fluent grey | open (P3) | `NewProjectWindow.axaml:33,38`; visible in `shots/light-newproject.png`. |
| T12 | Radius/selection-tint/row-height literals | accepted | P3 polish; no functional effect. |
| T13 | Tooltip radius/shadow | accepted | Tooltip contrast verified 14.12:1 light / 12.20:1 dark; no shadow is a visual-only difference. |
| T14 | Doc-tab font 12.5 | fixed | Native tabs use 11–12 px (`WorkbenchWindow.axaml:26`). |

## Workbench (workbench.md)

| ID | Item | Status | Evidence / decision |
| --- | --- | --- | --- |
| W1 | Programming tool crash (double parent) | fixed | `CreateProgrammingView` removes `list` before adding it (`:1853-1856`); `--screen=demoprogramming` now writes a PNG (1440×900) instead of crashing. |
| W2 | Inspector clips primary UI at narrow widths | **open (P2, reduced)** | Overlay reflow + tab suppression `:2117-2143`; a11y run: 0 covered tab stops. Still clipped while open at 900: representation tabs after “Parameters” and table columns beyond DPT are not visible, and at 1024 the horizontal scrollbar clips the table footnote (`shots/crop-1024-footer.png`). Closing the inspector restores everything. |
| W3 | Tool tabs collide with window controls at 900 | fixed | `WorkbenchWindow.axaml:113` ScrollViewer + `ClipToBounds`; icon-only below 1100 (`:2162`). |
| W4 | Perspective selector overlaps at 1024/900 | fixed | Text-only buttons `:125-128`, no icon; 900 capture shows all three labels. |
| W5 | Rail loses V4 state language | accepted (partial) | `rail-active` tint `WorkbenchWindow.axaml:19,355`; no 3 px bar, issue badge or recording pulse. Deliberate simplification, documented. |
| W6 | Active doc-tab rule on the bottom | fixed | Border `0,2,1,0` (`WorkbenchWindow.axaml:24-25`). |
| W7 | Shell controls use legacy ETS chrome | open (P3) | Topbar/object-toolbar buttons inherit App.axaml `EtsBorder`; primary/quiet are V4. Visible but coherent in captures. |
| W8 | Dialog chrome is split | **[superseded] fixed at main HEAD** | At base: `NewProjectWindow.axaml:5-11`, `CompareWindow.axaml`, `ReplaceProductWindow.axaml` were legacy. `f605870` unifies them on Wb chrome; not re-audited. |
| W9 | English leaks German | **open (P2)** | See acceptance report §2. Fixed: compare/replace dialogs. Open: all `ProjectCheck` messages, assignments CSV, topology CSV header, HTML report, `ParameterPane.axaml:44` (“Cancel review” in German UI). |
| W10 | Dock toolbar too heavy; body disappears at 900 | fixed | `CommitToolbar` compacts to 26 px; tool height floor 176 at <740 (`:2145`); 900 capture shows the empty state and two rows without overlap. |
| W11 | Table lacks checkbox/chips/row menu | accepted | No checkbox column or GA chips; row actions live in the inspector. Documented V4 deviation. |
| W12 | Help is an unscrollable message box | **[superseded] fixed at main HEAD** | At base: fixed 440 px wide, `SizeToContent.Height` (`ConfirmDialog.cs:34-37`), no scroll. `f605870` adds a `ScrollViewer` with `MaxHeight=360`; not re-audited. |

## Panes and dialogs (panes.md)

| ID | Item | Status | Evidence / decision |
| --- | --- | --- | --- |
| P1 | Search is a full page in a 242 px dock | **[superseded] fixed at main HEAD** | At base: `ShowTool("search")` forces maximized (`WorkbenchWindow.axaml.cs:1506-1510`), legacy styling remained. `39be758`/`ab0f8a4` replace it with a compact V4 page; not re-audited. |
| P2 | Two parameter UIs | **open (P2)** | Real devices use `ParameterPane`; demo devices use the hand-built form + shared footer (`:75-96`). Values/validation differ. |
| P3 | Dialog family | open (= W8) | |
| P4 | Label hierarchy | open (P3) | Catalog 10 px, Parameter 12 px semibold, Settings 12 px muted. |
| P5 | Action order | open (P3) | Settings Cancel/Save, NewProject Create/Cancel (`NewProjectWindow.axaml:10-11`). |
| P6 | Destructive confirm | **[superseded] fixed at main HEAD** | At base `ConfirmDialog.AskDeleteAsync:116-119` gave Delete no danger style and made Cancel primary. `f605870` adds `wb-danger` and never defaults a destructive choice; not re-audited. |
| P7 | Catalog compact buttons beside regular fields | open (P3) | `CatalogPane.axaml:100-103`. |
| P8 | Settings duplicate “Darstellung” | open (P3) | `SettingsWindow.axaml:30-31,38` still shows the page subtitle and the card heading with the same text; copy-only, no layout effect. |
| P9 | Splash tagline English in German UI | open (P3) | `SplashWindow.axaml:11`. |
| P10 | Parameter placeholder colour / German watermarks | accepted | Colour is a documented placeholder; watermarks are German-only until localisation of the type editor. |
| P11 | Report title 16 vs 22 | open (P3) | `ReportWindow.axaml:9`. |

## Accessibility recheck 2 (a11y-recheck2.md)

| ID | Item | Status | Evidence |
| --- | --- | --- | --- |
| F1 | Inspector overlay covers focusables | fixed for visible/focus coverage; harness check stale | Current harness: 15 bounds intersect the overlay, but they are clipped `MinWidth=540` rows/tabs behind the ScrollViewer; 0 covered tab stops, 0 editor stops (`:2138-2143`). Re-check the harness intersection against clip bounds; no visible control under the overlay in the 900×600 capture. |
| F1b | Covered document-tab stops | fixed | `inspector-tab-skips-covered-controls` PASS (0). |
| F5 | Table footnote not live | fixed | `WorkbenchWindow.axaml:102-103` `LiveSetting=Polite`, name “Tabellenhinweis”/“Table note”; harness PASS. |
| F6 | Tabs expose `Button`, incomplete selection | **open (P2)** | Roles still `Button`; `ItemStatus` is now set on doc/view/tool/rail strips (`:357,477,581,1517`), but the harness measured only 3 of 17 statuses — re-verify. `TabItem.wb-tab` styles exist (`WorkbenchTheme.axaml:211`) but no view uses them. |
| F8 | Inspector/dock fields unnamed | fixed | Runtime probe: inspector TextBoxes expose “Name”/“Address”; combos “Linie”/“Raumbezug (optional)”, dock “Monitorart”/“Telegrammfilter”. The harness failure was a stale direct-child lookup. |
| F10 | Object filter Ctrl+Z | fixed | Guarded writes `:427,548`; OS undo test PASS. |
| F11 | Row names trailing comma | fixed | `RowDescriptionNoLinks` used when no links; `table-row-names-clean` PASS. |
| F13 | Min size cannot fit 150/200 % | fixed (source) | `MinHeight=480` (`WorkbenchWindow.axaml:4`); source audit PASS for 911×512 and 960×540. Real DPI still unproven. |
| F14 | Perspective peers expose CLR type | fixed | `AutomationProperties.SetName` `:127`. |

## Deliberate limits (unchanged, not defects)

DCA hosting, Secure commissioning/key management, real bus writes/downloads,
USB/RDAC, cloud/IoT connectors, ETS local-store versioning, multi-project tabs,
manufacturer script dynamics, online catalogue/signature checks. See
`workbench-v4-status-matrix.md` and the completion plan §“Bewusst nicht nachgebaut”.

## Release-blocking subset at main `f9d0e7f`

1. W9 / R05 English completeness of check findings and exports (scope decision
   needed).
2. F6 tab role/selection (P2).
3. W2 narrow-inspector clipping (P2); active perspective 4.24:1 (P2, not covered
   by the new contrast test).
4. P2 two parameter UIs (P2).
5. Real 150/200 % DPI and screen-reader runs (R04) before claiming a11y sign-off.
