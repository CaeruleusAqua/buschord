# Knx.Localization.Tests

Offline localization gates for the string facades (`Knx.Strings`,
`Knx.App.Localization.WorkbenchStrings`, `Knx.App.Services.CatalogUiText`,
`Knx.App.Localization.CatalogDownloadStrings` and `Knx.App.Localization.UpdateUiStrings`).
No network or Roslyn: the gates read the runtime reference maps and supported
JSON catalogs, then scan `src/` and `tests/` as text.
Runs on Windows and Linux through `TEST_PROJECTS` in `.github/workflows/ci.yml`.

## Run

```text
dotnet run --project tests/Knx.Localization.Tests
I18N_GATE_VERBOSE=1 dotnet run --project tests/Knx.Localization.Tests   # print every finding
```

## Gates

| Gate | What it checks |
| --- | --- |
| parity | every key of a module has non-empty `de` and `en` text |
| overlays | supported language catalogs contain the complete reference key set, non-empty values and matching placeholders; fallback behaviour remains defined |
| duplicates | no key appears twice in a source map, and source entries match the runtime key set |
| references | `{loc:Tr K}` / `{loc:Wb K}` / `{loc:Up K}`, calls to the five facades, and `W("K")` / `Wb("K")` resolve in their own module; `U("K")` is checked in UpdateWindow sources only, separately from unrelated numeric helpers |
| placeholders | `{n}` index sets are equal across languages, braces are balanced, `string.Format` succeeds with dummy args for every key |
| German guard | no hardcoded German literal in product source outside `exceptions.txt` (umlaut/ß, German wordlist, `lang="de"`) |
| pseudo-loc | a test-only `qps` transform expands every German value into `[!! … !!]`, keeps placeholders intact and leaves real languages untouched |
| orphans | keys never referenced in `src`+`tests` are reported (report-only, prune candidates) |

Dev-log and selftest output (`src/Knx.Bus`, `src/Knx.Mcp`, `Smoke.cs`, `Screenshot.cs`,
`WorkbenchCheck.cs`) and the dictionaries themselves are outside the German guard.

## Add a key

1. Add one entry to the right German/English reference map and update the matching
   supported catalogs in `i18n/<module>/<lang>.json`.
2. Use it via `{loc:Tr MenuFileOpen}` / `{loc:Wb MenuFileOpen}` (XAML) or
   `Strings.Get` / `WorkbenchStrings.Get` / `CatalogUiText.Get` (C#); `W(`/`Wb(` resolve in
   `WorkbenchStrings` only. Placeholders must match between `de` and `en` (`{0}`, `{1}`, …).
3. Run the gates: a missing key, an empty value, a duplicate or a placeholder mismatch fails.

## Add a language

The runtime already supports JSON language overlays with German/English reference
maps. Follow the [catalog workflow](../../i18n/README.md#adding-a-language).
These tests iterate the registered supported overlays; no new tuple column is needed.

## exceptions.txt

- `allow path|literal` — German literal allowed by design (data, stable identifiers that must
  not change with the language).
- `ratchet path|literal` — known German leak; the list may only shrink. Fix the code and
  delete the entry in the same commit. Never add a new entry to hide a finding.
- `missing path|key` — key referenced in that file but absent from the expected map.

Stale entries (no longer found) are reported as INFO, not failed. Interpolation holes in
scanned literals are normalized to `{}`.

The maintained exceptions are in [exceptions.txt](exceptions.txt). Use the verbose
run to inspect current findings; do not add entries to hide new localization failures.
