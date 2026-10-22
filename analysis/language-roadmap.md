# BusChord language roadmap

Status: agreed priority list, 2026-10-22. Source of truth for language order,
catalog status and release gates. Technical design:
`analysis/i18n-architecture-plan.md`; translator workflow:
`analysis/i18n-translation-workflow.md`.

## 1. Priority list

| # | Language | Base code | Region focus | Priority | Catalog status |
|---|---|---|---|---|---|
| 1 | Deutsch | `de` | DE, AT, CH | P0 | reference (complete) |
| 2 | English | `en` | UK, IE, AU, IN, MY, AE, international | P0 | supported (complete) |
| 3 | Italiano | `it` | IT, CH | P1 | supported |
| 4 | Français | `fr` | FR, BE, CH, LU | P1 | supported |
| 5 | Español | `es` | ES, MX, CL, CO, LatAm | P1 | supported |
| 6 | Nederlands | `nl` | NL, BE | P1 | supported |
| 7 | 简体中文 | `zh-CN` | CN | P2 | supported |
| 8 | Polski | `pl` | PL | P2 | supported |

Regional variants share one base catalog until a real difference is needed:
`de-DE`/`de-AT`/`de-CH` -> `de`, `en-US`/`en-GB` -> `en`, `fr-FR`/`fr-BE`/
`fr-CH` -> `fr`, `it-IT`/`it-CH` -> `it`, `es-ES`/`es-MX` -> `es`,
`nl-NL`/`nl-BE` -> `nl`. Language matching uses longest prefix, so a future
`pt-BR` does not need a new resolver.

## 2. Why this order

- `de` and `en` remain the reference and first-class fallback.
- French is the pilot: plural `0`/`1`, date and number formatting exercise the
  catalog model better than Dutch, and the architecture report already selected
  it. Italian follows because it is the largest P1 market after France and
  shares the same plural shape.
- Spanish and Dutch complete P1. `es` starts with one neutral catalog; regional
  variants are a later, evidence-driven split.
- `zh-CN` and `pl` are P2: they need locale-specific font, line-breaking and
  plural checks, and should not delay the P1 rollout.

## 3. Status model

`planned` -> `supported`; `reference` is the German source.

- `reference`: German source; complete by definition.
- `supported`: complete catalog, selectable in Settings, covered by the catalog
  and UI checks. Further terminology changes follow normal code review.
- `planned`: no catalog yet; not selectable.

The registry is `i18n/languages.json`. A language is advertised in Settings when
its catalog exists and its status is `reference` or `supported`. Unknown codes
fall back to `de`.

## 4. Translation workflow

Translations are product content and follow the normal review path:

1. Export the German source with English reference and UI context.
2. Translate using the glossary in `i18n/README.md`.
3. Import, run the catalog checker, fix placeholders and key parity.
4. Check screenshots at 1440/1024/900 in light and dark.
5. Commit the catalog with the code that exposes the language.

There is no separate native-review gate. The tests, placeholder checks and UI
smoke/screenshots are the acceptance evidence.

## 5. Technical work still required

The current product stores `de`/`en` in C# tuples; additional languages are
embedded JSON overlays, resolved without per-language lookup code. Implemented:

- `i18n/<module>/<lang>.json` overlays for `core`, `workbench`, `catalog`.
- One resolver with longest-prefix language matching and fallback
  `requested -> en -> de -> key`.
- Settings selector and `--lang=` for `de`, `en`, `fr`, `it`, `es`, `nl`, `zh-CN`, `pl`;
  `de-DE`/`en-US` and the other regional tags normalize to their base catalogs.

Still required:

- Move the German/English reference itself into catalogs (the current overlay
  approach is a strangler step).
- Registry-driven `Available`/Settings list instead of the hardcoded list.
- Placeholder, plural, date and number rules per language; no raw
  `string.Format` against the OS culture.
- Report-specific tables and report `lang` for non-de/en languages.

## 6. Checks per language

A catalog is merged only when these checks hold:

- Catalog key parity with `de` for every module; no empty values.
- Placeholders identical to the German source and every value formats.
- No untranslated German UI text in `--lang=<code>` smoke/selftest output.
- `--workbench-selftest --lang=<code>` passes in light and dark.
- Screenshots at 1440x900, 1024x700 and 900x600 show no clipped actions.
- CSV and HTML report headers use the language and remain re-importable where
  the report path supports it.
- Changelog entry for the new catalog and any key renames.

## 7. Release order

1. P0 remains the default and fallback.
2. French proves the pipeline and plural/format model.
3. Italian, Spanish and Dutch follow on the same catalog contract.
4. Chinese (simplified) and Polish after the P1 feedback loop.
5. Do not advertise the next language before its catalog exists.
