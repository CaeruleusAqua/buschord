# Knx.Localization.Tests

Offline localization gates for the three string facades (`Knx.Strings`,
`Knx.App.Localization.WorkbenchStrings`, `Knx.App.Services.CatalogUiText`).
No product code, no network, no Roslyn: every gate reads the dictionaries at runtime
(reflection on the private maps until the JSON loader lands, analysis
`analysis/i18n-translation-workflow.md` §10) and scans `src/` and `tests/` as text.
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
| duplicates | no key appears twice in a source map, and source entries match the runtime key set |
| references | every `{loc:Tr K}` / `{loc:Wb K}`, `Strings.Get("K")`, `WorkbenchStrings.Get("K")`, `CatalogUiText.Get("K")` and `W("K")` / `Wb("K")` resolves in the map its call site uses |
| placeholders | `{n}` index sets are equal across languages, braces are balanced, `string.Format` succeeds with dummy args for every key |
| German guard | no hardcoded German literal in product source outside `exceptions.txt` (umlaut/ß, German wordlist, `lang="de"`) |
| pseudo-loc | a test-only `qps` transform expands every German value into `[!! … !!]`, keeps placeholders intact and leaves real languages untouched |
| orphans | keys never referenced in `src`+`tests` are reported (report-only, prune candidates) |

Dev-log and selftest output (`src/Knx.Bus`, `src/Knx.Mcp`, `Smoke.cs`, `Screenshot.cs`,
`WorkbenchCheck.cs`) and the dictionaries themselves are outside the German guard.

## Add a key

1. Add one entry to the right map with German **and** English text, non-empty:
   `["MenuFileOpen"] = ("Projekt öffnen…", "Open project…"),`.
2. Use it via `{loc:Tr MenuFileOpen}` / `{loc:Wb MenuFileOpen}` (XAML) or
   `Strings.Get` / `WorkbenchStrings.Get` / `CatalogUiText.Get` (C#); `W(`/`Wb(` resolve in
   `WorkbenchStrings` only. Placeholders must match between `de` and `en` (`{0}`, `{1}`, …).
3. Run the gates: a missing key, an empty value, a duplicate or a placeholder mismatch fails.

## Add a language

Today the maps are `(string De, string En)` tuples, so a third language means a third column
in all three maps. Then extend this project: the parity/placeholder loops iterate the tuple,
and the German guard stays unchanged. The planned JSON loader (`i18n/<module>/<lang>.json`,
analysis §3) replaces the tuple shape and the reflection with a per-language loop; until
then, keep the parity checks in this file in sync with the facade shape.

## exceptions.txt

- `allow path|literal` — German literal allowed by design (data, stable identifiers that must
  not change with the language).
- `ratchet path|literal` — known German leak; the list may only shrink. Fix the code and
  delete the entry in the same commit. Never add a new entry to hide a finding.
- `missing path|key` — key referenced in that file but absent from the expected map.

Stale entries (no longer found) are reported as INFO, not failed. Interpolation holes in
scanned literals are normalized to `{}`.

Current known exceptions:

- `missing src/Knx.App/WorkbenchWindow.Commands.cs|LinkProjectChanged`: `W(...)` resolves in
  `WorkbenchStrings`, but the key exists only in the core `Strings` map, so the dialog shows
  the raw key. Fix: move the key to `WorkbenchStrings` or call `Strings.Get`.
- `missing tests/Knx.Core.Tests/Program.cs|GibtEsNicht`: deliberate unknown key, the test
  exercises the "unknown key returns the key" fallback.
- 34 `ratchet` entries: German literals in `ParameterEditingSession`, `ParameterItemViewModel`,
  `ParameterPane`, `MainWindowViewModel`, `Dashboard`, `ProjectQuery`, `Values`, `Wizards`,
  `LabelCreator` and the workbench label export. They are localized inline in places, but
  should move into the dictionaries.
