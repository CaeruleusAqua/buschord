# Icons

`Icons.axaml` contains `StreamGeometry` resources keyed `Icon.<Name>`, taken
verbatim from **Material Symbols Outlined** (Google Fonts). The Workbench shell
uses the stroked concept set instead: see `docs/ICONS.md`.

- Source: https://fonts.google.com/icons?selected=Material+Symbols+Outlined
- License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0)
- Fetch URL pattern, one file per icon:
  `https://fonts.gstatic.com/s/i/short-term/release/materialsymbolsoutlined/<name>/default/24px.svg`
- Cross-checked against the upstream repository path pattern:
  `https://raw.githubusercontent.com/google/material-design-icons/master/symbols/web/<name>/materialsymbolsoutlined/<name>_24px.svg`

The path data is copied unchanged, including the Material Symbols viewBox
`0 -960 960 960` (24px/960 square). Do not edit the path data by hand: re-fetch
the SVG and copy the `d` attribute. See `Views/Icon.cs` for the rendering.

## Workbench V4 shell additions

These entries support the Workbench shell. The official sources and license above apply.

| Resource | Upstream name | Replaces | Usage |
|---|---|---|---|
| `Icon.UnfoldMore` | `unfold_more` | `↕` | Expand-all in the object toolbar |
| `Icon.OpenInNew` | `open_in_new` | `↗` | Float the active tool into its own window |
| `Icon.Fullscreen` | `fullscreen` | `□` | Maximize the tool dock |
| `Icon.Close` | `close` | `×` | Hide the tool dock, close the inspector, close a document tab |
| `Icon.KeyboardArrowDown` | `keyboard_arrow_down` | `⌄` | Expanded tree node |
| `Icon.KeyboardArrowRight` | `keyboard_arrow_right` | `›` | Collapsed tree node |

`Icon.Search` (`⌕`, palette) and `Icon.Tune` already existed and are reused.
The `·` at a childless tree node is a disabled indent spacer, not an
affordance; it stays an empty spacer instead of `more_horiz`/`more_vert`,
which would read as an overflow menu. No other symbols are needed.
