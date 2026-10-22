# BusChord language roadmap

Status: agreed priority list, 2026-10-22. Source of truth for language order,
catalog status and release gates. Technical design:
`analysis/i18n-architecture-plan.md`; translator workflow:
`analysis/i18n-translation-workflow.md`.

## 1. Priority list

| # | Language | Base code | Region focus | Priority | Catalog status |
|---|---|---|---|---|---|
| 1 | Deutsch | `de` | DE, AT, CH | P0 | reference (complete) |
| 2 | English | `en` | UK, IE, AU, IN, MY, AE, international | P0 | reviewed (complete) |
| 3 | Italiano | `it` | IT, CH | P1 | planned |
| 4 | Français | `fr` | FR, BE, CH, LU | P1 | preview - pilot |
| 5 | Español | `es` | ES, MX, CL, CO, LatAm | P1 | planned |
| 6 | Nederlands | `nl` | NL, BE | P1 | planned |
| 7 | 简体中文 | `zh-CN` | CN | P2 | planned |
| 8 | Polski | `pl` | PL | P2 | planned |

Regional variants share one base catalog until a real difference is reviewed:
`de-DE`/`de-AT`/`de-CH` -> `de`, `en-US`/`en-GB` -> `en`, `fr-FR`/`fr-BE`/
`fr-CH` -> `fr`. Language matching uses longest prefix, so a future `pt-BR`
does not need a new resolver. The UI shows the endonym plus the status suffix
for non-reviewed catalogs.

## 2. Why this order

- `de`/`en` stay the only shipped languages until a third catalog has a human
  review. They are reference and first-class fallback.
- French is the pilot: plural `0`/`1`, date and number formatting exercise the
  catalog model better than Dutch, and the architecture report already selected
  it. Italian follows because it is the largest P1 market after France and
  shares the same plural shape.
- Spanish and Dutch complete P1. `es` starts with one neutral catalog; regional
  variants are a later, evidence-driven split. `nl` is small enough to review
  quickly.
- `zh-CN` and `pl` are P2: they need locale-specific font, line-breaking and
  plural checks, and should not delay the P1 rollout.

## 3. Status model

`planned` -> `draft` -> `preview` -> `reviewed`.

- `reference`: German source; complete by definition.
- `reviewed`: complete catalog, native review recorded in the registry.
- `preview`: complete enough for screenshots and feedback, visibly marked,
  selectable in Settings; not a release claim.
- `draft`: translator work in progress; reachable only through `--lang=`.
- `planned`: no catalog yet; not selectable.

The registry is `i18n/languages.json`. A language is only advertised in
Settings when its catalog exists and its status is `preview` or `reviewed`.
`draft` remains a developer/translator path. Unknown codes fall back to `de`.

## 4. Translation rule

No machine translation is committed as a product language without native
review. The accepted path is:

1. Export the German source with English reference and UI context.
2. Translate with a named translator/agency using the glossary.
3. Import, run the catalog checker, fix placeholders and key parity.
4. Review screenshots at 1440/1024/900 in light and dark.
5. Record reviewer and date; move `preview` -> `reviewed`.

An unreviewed machine draft may exist only as a local, uncommitted working file
or as a `draft` that is not advertised. It must not be described as a supported
language.

## 5. Technical work still required

The current product stores `de`/`en` in C# tuples; additional languages are
embedded JSON overlays, resolved without per-language lookup code. Implemented:

- `i18n/<module>/<lang>.json` overlays for `core`, `workbench`, `catalog`.
- One resolver with longest-prefix language matching and fallback
  `requested -> en -> de -> key`.
- Settings selector and `--lang=` for `fr` (preview suffix); `de-DE`/`en-US`
  normalize to their base catalogs.

Still required:

- Move the German/English reference itself into catalogs (the current overlay
  approach is a strangler step).
- Registry-driven `Available`/Settings list instead of the hardcoded three.
- Placeholder, plural, date and number rules per language; no raw
  `string.Format` against the OS culture.
- Report-specific tables and report `lang` for non-de/en languages.
- CI gates: catalog parity, no empty values, placeholder shape, German-literal
  scan, pseudo-locale, CSV/report language, screenshots.

The pilot is not complete until the French catalog is native-reviewed and
screenshot-approved. The current `fr` catalog is a complete **preview** over the
German/English reference: UI and CSV resolve in French, report-specific private
tables and MCP exports still fall back to German/English. `i18n/fr` remains
`preview` until that review happens.

## 6. Acceptance per language

A language moves to `reviewed` only when all of these hold:

- Catalog key parity with `de` for every module; no empty values.
- Placeholders identical to the German source and every value formats.
- No untranslated German UI text in `--lang=<code>` smoke/selftest output.
- `--workbench-selftest --lang=<code>` passes in light and dark.
- Screenshots at 1440x900, 1024x700 and 900x600 show no clipped actions.
- CSV and HTML report headers use the language and remain re-importable.
- Native reviewer recorded in `i18n/languages.json`; date and scope noted.
- Changelog entry for the new catalog and any key renames.

## 7. Release order

1. P0 remains the default and fallback.
2. French pilot (`preview` -> `reviewed`) proves the pipeline and plural/format
   model.
3. Italian, Spanish, Dutch in that order, one reviewed catalog per change.
4. Chinese (simplified) and Polish after the P1 feedback loop.
5. Do not advertise the next language before its catalog exists.
