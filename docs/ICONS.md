# Concept icons

Workbench shell icons use the **Concept** line set, not the filled Material
Symbols of the legacy window.

- Source: `knx-workbench-v4-bundle/src/app.js`, `paths` object (lines 6-50;
  path strings on lines 7-48), KNX Workbench V4 concept.
- Style: `viewBox 0 0 24 24`, `fill none`, `stroke currentColor`,
  `stroke-width 1.7`, round caps and joins.
- Geometry: `src/Knx.App/Assets/ConceptIcons.axaml`, resource keys
  `Concept.<name>`, `d` copied byte-for-byte from the source.
- Rendering: `src/Knx.App/Views/ConceptIcon.cs` — draws the geometry in its
  24-unit box with the 1.7 px round stroke, defaults to 17 px and inherits
  `Foreground`.
- Material Symbols stay in `src/Knx.App/Assets/Icons.axaml` / `Views/Icon.cs`
  for the remaining shared panes (currently `SearchPane`); new workbench
  surfaces use `ConceptIcon`.

## Mapping

| Workbench usage | Concept resource |
|---|---|
| Rail perspectives and perspective buttons | `building`, `topology`, `groups` |
| Rail catalog, project check, monitor, options, help | `catalog`, `warning`, `monitor`, `settings`, `help` |
| Navigator and object-tree nodes | `down` expanded, `chevron` collapsed |
| Expand/collapse all devices | `down` |
| Document tab close, new work item | `close`, `plus` |
| Tool tabs | `monitor`, `download` (programming), `refresh` (scan), `search`, `clock` (changes), `warning` (problems) |
| Tool dock float, maximize, hide | `export`, `maximize`, `close` |
| Inspector close | `close` |
| Catalog pane search field and empty states | `search`, `catalog` |

## Limitations

The concept set has no dedicated glyph for these affordances; the nearest
concept icon is used instead:

- Expand/collapse all → `down` (no unfold glyph in the set).
- Float the tool window → `export` (no detach glyph; export reads as leaving
  the shell).
- Scan line → `refresh` (no scan/radar glyph).
- Changes list → `clock` (no history glyph).
- Project check and tool problems → `warning` (the concept's findings icon).

Unused but available: `device`, `home`, `folder`, `more`, `link`, `check`,
`upload`, `undo`, `redo`, `layers`, `panel`, `split`, `arrow`, `lock`, `eye`,
`play`, `stop`, `pin`, `doc`, `bulb`, `trash`, `flash`, `flag`, `move`.
