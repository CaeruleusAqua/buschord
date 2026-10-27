# Concept icons

The workbench and its shared panes use the **Concept** line set.

- Geometry: `src/Knx.App/Assets/ConceptIcons.axaml`, resource keys `Concept.<name>`.
- Rendering: `src/Knx.App/Views/ConceptIcon.cs` — draws the geometry in its
  24-unit `viewBox` with a 1.7 px `currentColor` stroke, round caps/joins and a
  default size of 17 px. The icon inherits `Foreground`.
- The Axaml resources are the canonical geometry definitions.
- Material Symbols remain in `src/Knx.App/Assets/Icons.axaml` and
  `src/Knx.App/Views/Icon.cs`, but no workbench, pane or dialog view uses them any
  more. New surfaces use `ConceptIcon`.

## Mapping

| Workbench usage | Concept resource |
|---|---|
| Rail perspectives and perspective buttons | `building`, `topology`, `groups` |
| Rail catalog, project check, monitor, options, help | `catalog`, `warning`, `monitor`, `settings`, `help` |
| Navigator and object-table expand/collapse | `down` expanded, `chevron` collapsed |
| Expand/collapse all devices | `down` |
| Document tab close, new work item | `close`, `plus` |
| Tool tabs | `monitor`, `download` (programming), `refresh` (scan), `search`, `clock` (changes), `warning` (problems) |
| Tool dock float, maximize, hide | `export`, `maximize`, `close` |
| Inspector close | `close` |
| Catalog and search fields, empty states | `search`, `catalog`, `building` (start), `folder` (portfolio) |
| Report window | `doc` |

## Limitations

The concept set has no dedicated glyph for these affordances; the nearest
concept icon is used instead:

- Expand/collapse all → `down` (no unfold glyph in the set).
- Float the tool window → `export` (no detach glyph; export reads as leaving
  the shell).
- Scan line → `refresh` (no scan/radar glyph).
- Changes list → `clock` (no history glyph).
- Project check and tool problems → `warning` (the concept's findings icon).

Unused but available: `device`, `home`, `more`, `link`, `check`, `upload`,
`undo`, `redo`, `layers`, `panel`, `split`, `arrow`, `lock`, `eye`, `play`,
`stop`, `pin`, `bulb`, `trash`, `flash`, `flag`, `move`.
