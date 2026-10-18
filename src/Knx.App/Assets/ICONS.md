# Icons

`Icons.axaml` contains `StreamGeometry` resources keyed `Icon.<Name>`, taken
verbatim from **Material Symbols Outlined** (Google Fonts).

- Source: https://fonts.google.com/icons?selected=Material+Symbols+Outlined
- License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0)
- Fetch URL pattern, one file per icon:
  `https://fonts.gstatic.com/s/i/short-term/release/materialsymbolsoutlined/<name>/default/24px.svg`

The path data is copied unchanged, including the Material Symbols viewBox
`0 -960 960 960` (24px/960 square). Do not edit the path data by hand: re-fetch
the SVG and copy the `d` attribute. See `Views/Icon.cs` for the rendering.
