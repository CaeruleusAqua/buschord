# BusChord localization: translator workflow, CI validation and release process

Current policy (2026-10-22): `analysis/language-roadmap.md` supersedes this
design's status proposals. Supported languages have no preview label and no
separate native-speaker approval requirement.

Status: design only. Base `2846c16` (`agents/i18n/workflow`). Nothing in this branch
changes product code, tests, CI, docs or existing reports.

## 1. Verdict

Keep the repository as the only source of truth, but move the string *data* out of the
three hand-written C# dictionaries into per-module, per-language JSON under `i18n/`.
German stays the reference language, English stays first-class, every further language is
an overlay with a declared status. JSON is the source; CSV and XLIFF are generated
exchange artifacts for small contributors and agencies. A single console checker
(`tools/i18n --check`) plus one console test project carry the CI gates; the existing
`--screenshot`, `--smoke` and `--workbench-selftest` harness supplies runtime and
screenshot evidence. No translation platform is required.

The reason to move at all is not elegance: `(string De, string En)` tuples make a third
language a rewrite of all 904 entries and every future key a three-way merge. The reason
to move *now* is the measurement in section 2 — 242 of 904 keys are dead, so the
translatable catalog is ~27 % smaller than it looks.

## 2. Current state (measured at base)

| Store | Keys | Live | Notes |
| --- | --- | --- | --- |
| `src/Knx.Core/Localization.cs` (`Strings`) | 451 | 238 | static `Available = ["de","en"]`; unknown language silently falls back to `de`; unknown key returns the key; `Catalogue` exposed for tests |
| `src/Knx.App/Localization/WorkbenchStrings.cs` | 415 | 391 | same pattern, private map |
| `src/Knx.App/Services/CatalogBrowser.cs` (`CatalogUiText`) | 42 | 37 | same pattern, documented plan to fold into `WorkbenchStrings` |
| Hardcoded German | 4 files | — | `ParameterItemViewModel.cs:109,113`, `MainWindowViewModel.cs:542` (default device name), `LabelCreator.cs:49,75,81` (CSV header, sheet title, `lang="de"`) |

Derived facts:

- 904 unique keys; 662 appear as literals somewhere in `src` or `tests`, 242 appear only
  inside the three dictionaries. The dead set is legacy-shell vocabulary (`BtnSave`,
  `MenuFileOpen`, `BtnSend`, `BtnLabels`, …): the old `MainWindow` is gone, its strings are
  not. Spot checks of the dead list find no reference at all; treat 242 as a floor and let
  the runtime coverage gate (G3) produce the exact number.
- 2,392 English words across the 656 paraphraseable entries (average 3.6 words).
- 4 key names exist in two modules; `CompareSummary` and `SearchResults` even carry
  different text. A flat exchange file therefore needs a module namespace; the code can
  keep three thin facades.
- 0 German literals remain in `.axaml` attributes apart from the language endonyms
  `Deutsch`/`English` in `SettingsWindow.axaml:48-49`; 138 `{loc:Tr}`/`{loc:Wb}` references.
- Placeholders are .NET composite `{0}` only (`string.Format`), no plural machinery.
- `TrExtension`/`WbExtension` resolve once at load, so a language switch affects windows
  opened afterwards. That is documented behaviour, not a bug to fix here.
- Settings offer exactly two languages via hardcoded index (`SettingsWindow.axaml:47-50`,
  `SettingsWindow.axaml.cs:21,34`); `--lang=fr` today renders German silently.
- Sources are UTF-8 without BOM; there is no `.editorconfig` and no `.gitattributes`.
- Tests are framework-less console projects. Existing assertions on exact German/English
  values (`tests/Knx.Core.Tests:671-795`), CSV headers (`tests/Knx.Workbench.Tests:658-679`)
  and report/CSV language (`src/Knx.App.Tests:44-168`) are a strong migration safety net.
- `Screenshot.Screens` already names 24 screens; `--lang=`, `--theme=`, `--screen=` exist;
  CI already runs `--workbench-selftest --lang=en` and `--screenshot-selftest`.

## 3. Source of truth and exchange format

### 3.1 Decision

`i18n/<module>/<lang>.json`, ASCII-sorted flat objects, UTF-8 without BOM, LF, two
spaces, real characters (no `\u` escapes). Modules: `core`, `workbench`, `catalog`.

```json
{
  "MenuFileOpen": "Projekt öffnen…",
  "MenuFileSave": "Projekt speichern"
}
```

- Flat, not nested: keys are flat identifiers; nesting invents a path syntax and causes
  merge conflicts for no gain.
- One file per language, not per key: 900 files would be unusable in review.
- No metadata inside language files: translators must not be able to break validation
  metadata, and JSON has no comments. Context lives in `context.json` (3.4).
- Loaded as embedded resources (`EmbeddedResource` with a fixed `LogicalName`) by a small
  loader in `Knx.Core`; `System.Text.Json` is in the shared framework, so no new NuGet
  dependency and no satellite assemblies. `Strings`, `WorkbenchStrings` and `CatalogUiText`
  stay as facades with unchanged call sites, which keeps the migration diff mechanical.

`i18n/languages.json` (registry, hand-maintained):

```json
[
  { "code": "de", "name": "Deutsch", "status": "reference", "reviewers": [] },
  { "code": "en", "name": "English", "status": "reviewed", "reviewers": ["@CaeruleusAqua"] },
  { "code": "fr", "name": "Français", "status": "preview", "reviewers": [] }
]
```

Status: `reference` (complete by definition) → `reviewed` → `preview` → `draft`.
`reference`, `reviewed` and `preview` appear in Settings (`preview` with a suffix);
`draft` is reachable only through `--lang=` for the translator's own testing.

Fallback chain: requested → `en` → `de` → key. Unknown or invalid code → `de`, which
preserves today's behaviour. Language codes are ISO 639-1 lowercase, with a region
subtag only where the translation genuinely differs (`pt-BR`); matching uses
longest-prefix, so `de-AT` resolves to `de` (today's `Available.Contains` cannot do that).

### 3.2 Why not the alternatives

| Option | Translator comfort | PR diff quality | N languages | Cost |
| --- | --- | --- | --- | --- |
| C# tuples (today) | none — requires C# | good | third language rewrites 904 lines | 0 now, high later |
| `.resx` + satellite assemblies | medium (ResX Manager, no web tools) | poor (XML noise, resx ordering) | good | reverses the documented no-resources decision, adds culture probing and per-language DLLs |
| XLIFF as source | high (CAT tools) | poor (`<file>`/`<unit>` churn, ids repeated) | good | verbose source, awkward for a one-word fix |
| PO/gettext | high (Poedit, Weblate) | good | good | source text as msgid is fragile, `msgctxt`-as-key is unusual |
| CSV as source | high | medium (escaping, no context) | good | quoting/newline bugs; also not diff-friendly |
| **JSON as source** | high via generated CSV/XLIFF | good | good | one loader, one checker |

JSON is the only option that keeps the repository reviewable and still serves both a
non-technical contributor (generated CSV in Excel) and an agency (generated XLIFF 2.0).

### 3.3 Generated exchange artifacts (gitignored)

- `tools/i18n --export-csv <lang>` → `.csv` with `module;key;source_de;target;context;status`.
  Semicolon because the repository's own CSV exports use `;` and users open them in Excel;
  UTF-8 **with** BOM so Excel keeps umlauts.
- `tools/i18n --export-xliff <lang>` → XLIFF 2.0, `id = module.key`, `<note>` from
  `context.json`, `state` mapped from the registry.
- `tools/i18n --import <file>` validates with the same rules as `--check` and then writes
  JSON. It never edits C#.
- Exports are release artifacts and PR attachments, not sources.

### 3.4 Key model, context, screenshots, glossary, plurals

**Keys.** Permanent ASCII IDs `[A-Za-z][A-Za-z0-9]*`, shape `<Area><Noun>` (`MenuFileOpen`,
`ChkP001`). Never translated, never renumbered, never reused for a different meaning.
The module is the namespace; do not encode it in the key. The four current collisions
stay separate because the loader keys on `(module, key)`.

**Context.** `i18n/<module>/context.json`, optional per key:

```json
{
  "ChkP024": {
    "context": "Project check finding. {1} area number, {2} optional \"/line N\" suffix.",
    "screens": ["inspector"],
    "glossary": ["Bereich"]
  }
}
```

`maxLength` is advisory and starts unused — add it only where a fixed-width cell actually
breaks. `screens` may only name entries from `Screenshot.Screens`, so CI can validate them.

**Screenshots.** `i18n/shots/de/<screen>.png`, committed only for screens referenced by a
context file (start with 6-8, not all 24), rendered by `--screenshot --screen=<name>
--lang=de`, downscaled to 960 px wide, target under 150 KB each. Other languages'
screenshots are CI artifacts and PR attachments, never committed: binary churn with no
review value. CI checks that referenced screens and files exist; it does **not** compare
pixels (fonts and DPI make that flaky) — visual approval stays human.

**Glossary.** `i18n/glossary.csv` with
`term_de,term_en,term_<lang>,definition,do_not_translate`. Fixed core: Gruppenadresse =
group address, Kommunikationsobjekt = communication object, Einbauort = installation
location, Gerät = device, Linie = line, Bereich = area, Verteiler = distribution board,
Befund = finding, Applikation = application, Telegramm = telegram. Do not translate:
KNX, ETS, DPT, BusChord, CSV, HTML, JSON, LD, Turtle, Secure, IP, LED, PUID, RefId.
CI enforces only the do-not-translate list (the exact token must survive in every language
whose reference contains it); terminology itself is a human review rule.

**Plurals and formats.** Keep `string.Format` and adopt **no plural engine now**. All
2,392 words of de/en use count phrasing that works with a single form; a Slavic language
is what forces the decision. The gates enforce: identical `{n}` index set as the
reference, balanced braces, no added HTML tags, unchanged leading/trailing whitespace,
preserved newlines, and a `string.Format` dry run per language. When a language needs
plural categories, extend only the flagged keys as `Key.one/.few/.many/.other` plus a
CLDR-category function for that language — not speculatively.

## 4. Repository workflow for community contributions

### 4.1 Layout

```text
i18n/
  README.md                 translator entry point, licence/CLA note, how to add a language
  languages.json            registry: code, native name, status, reviewers
  glossary.csv              shared terminology and do-not-translate list
  core/       de.json en.json [fr.json] context.json
  workbench/  de.json en.json [fr.json] context.json
  catalog/    de.json en.json [fr.json] context.json
  shots/de/   <screen>.png  reference screenshots named by context.json
  CHANGELOG.md              per release: added/changed/removed keys
  tm/aliases.json           rename history for translation memory (not runtime)
tools/i18n/                 net9.0 console: --check --export-csv --export-xliff --import
                            --changelog --shots --extract --new
```

### 4.2 Ownership and approval

Translations follow the normal code ownership and engineering review process.
There is no separate native-speaker approval gate or reviewer registry.

### 4.3 Licence and CLA

Apache-2.0 outbound. Translations are copyrightable contributions, so the existing CLA
checkbox and CLA Assistant check apply unchanged; a translation-only contributor still
signs. Add one paragraph to `i18n/README.md` (JSON cannot carry a header): files under
`i18n/` are contributed under the project licence and the CLA. No separate translation
agreement. If the owner wants to waive the CLA for typo-sized fixes, that is a decision for
the CLA document, not for this workflow.

### 4.4 Data and secrets

- No manufacturer catalogue text, product names or descriptions in `i18n/`: those are
  runtime catalogue data, translated by the manufacturer, never by this project.
- No customer project names, addresses, screenshots or exports. Screenshots must come from
  the built-in demo project via `--screenshot`; hand-taken shots of real projects are
  rejected in review.
- No service tokens in files. If a translation platform is adopted later, its token lives
  in repository secrets, and CI must still pass fully offline.

### 4.5 Adding a language (the README recipe)

1. `tools/i18n --new fr` creates empty `i18n/*/fr.json` and a `draft` registry entry.
2. Translate directly, or round-trip through the generated CSV/XLIFF.
3. `tools/i18n --check --lang=fr` (parity, placeholders, glossary, duplicates).
4. PR with the `translation` label; partial files are allowed while `draft`, CI reports
   coverage.
5. Passing catalog and UI checks → `supported` (selectable, no status suffix).

### 4.6 Incomplete-language behaviour

Missing key falls back `en` → `de` → key. Supported languages are selectable in
Settings without a status suffix. No warning dialog and no in-app banner:
one fallback path, stated in the manual
later. A partially translated language must never produce key names or empty labels in the
UI — that is what the fallback chain is for.

### 4.7 PR template addition (additive, not a rewrite)

```text
## Translation
- Language: <!-- de / en / fr / … -->
- [ ] Reference (de) unchanged, or the German change is intentional and explained
- [ ] Glossary terms used as defined in i18n/glossary.csv
- [ ] Placeholders unchanged; tools/i18n --check passes
- [ ] Native review by @… (required for a new language or status change)
- [ ] No manufacturer catalogue text, customer data, or customer screenshots
```

## 5. CI gates

Mechanism: `tools/i18n` (console, no NuGet, `InvariantCulture`, identical on Linux and
Windows) plus `tests/Knx.Localization.Tests` in the existing `Check(...)` style. The test
project joins `TEST_PROJECTS` so both OS jobs run it; the checker runs as its own step and
its `--report=json` output becomes the job summary (never a committed file).

| ID | Gate | Mechanism | Failure semantics |
| --- | --- | --- | --- |
| G1 | Key parity | every `de` key present in `reviewed`/`reference` languages | fail; `preview`/`draft` only reported |
| G2 | Unknown key in code | static scan of `src`+`tests` for `{loc:Tr|Wb K}`, `Strings.Get("K")`, `WorkbenchStrings.Get("K")`, `CatalogUiText.Get("K")`, local wrappers `W(`/`Wb(` | fail |
| G3 | Orphan keys | loader records requested keys; `--smoke`/`--workbench-selftest` dump the set; diff against `de.json` | fail after the one-time prune; report-only before it |
| G4 | Duplicate keys in a file | token walk of `JsonDocument` (parsers silently keep the last) | fail |
| G5 | Empty / untranslated copy | whitespace-only values fail; `en == de` fails only when the German contains umlauts/ß or a German wordlist hit, so "Bus", "Status", "Export" pass without an allowlist | fail |
| G6 | Placeholders and formats | `{n}` set equals reference, braces balanced, dry `string.Format` per language (today only `Chk*`/`Csv*`/`Report*` are covered) | fail |
| G7 | Forbidden German literals in `en` | static: umlaut/wordlist scan of `src/**/*.axaml|*.cs` outside the JSON and a dev-log allowlist (`Knx.Bus`, `Knx.Mcp` console, selftest output); runtime: `--smoke --lang=en` + `--workbench-selftest --lang=en` text dump | fail |
| G8 | Pseudo-localization | built-in `qps` generated from `de` at load (`[!! … !!]`, lengthened vowels); full `--workbench-selftest --lang=qps` state walk; every visible label must carry the marker | fail on an unmarked label; screenshots as artifacts |
| G9 | Report/CSV language | `src/Knx.App.Tests` + `tests/Knx.Workbench.Tests` iterate the registry: equal column counts across languages, `<html lang>` matches, no German header in a non-German language, localized empty states; `LabelCreator` stops hardcoding `Etiketten` and `lang="de"`; a `fr` export re-imports while `de` is active | fail |
| G10 | Screenshots | Windows job renders `Screenshot.Screens` × `de`/`en` into artifacts; checker validates that context-referenced screens exist in code and that files are non-empty | fail |
| G11 | Key stability | `--changelog <base>..HEAD`: removed reference keys and changed German values must be listed | fail without a changelog entry |
| G12 | Glossary tokens | do-not-translate terms survive in every language | fail |
| G13 | External service | none required; if one is adopted it syncs the same JSON and CI must pass offline | — |

G7 is the flakiest: scope it to *code literals*, never to project or catalogue data, or it
will fail on legitimate demo names (`Wohnzimmer`) and manufacturer product names.

## 6. Release, versioning and compatibility

- The string catalog versions with the app; no separate catalog version file.
- **Add key**: `de.json` (+ `context.json` when ambiguous) in the same PR that uses it.
  Other languages may lag and fall back; CI reports which.
- **Rename**: new key, all references updated, old key removed from every language file in
  one commit, plus `i18n/tm/aliases.json` `{ "OldKey": "NewKey" }` so future TM exports
  carry the existing translation. No runtime alias layer — it would hide typos from G2.
- **Remove**: delete from all language files and `context.json` in one commit; the
  changelog lists it so translators can prune their TM. No tombstones: nothing persists
  keys at runtime.
- **Migration** (phase 0): `tools/i18n --extract` reads the public `Strings.Catalogue` and
  regex-parses the two private maps, writes JSON, then the maps are deleted. Verification:
  a one-off comparison of all 904 `(de, en)` pairs plus the untouched existing assertions
  in `Knx.Core.Tests`, `Knx.Workbench.Tests` and `src/Knx.App.Tests`. Test touchpoints that
  must change: `Knx.Core.Tests:688` (`Available.Count == 2`) and `:767-792`
  (`Catalogue` tuple shape).
- **Changelog**: `i18n/CHANGELOG.md`, one section per release, generated by
  `--changelog vX..vY` from the git diff of `de.json`, listing added/changed/removed keys
  with the new German text.
- **Translation memory**: none until a second community language exists. Then `--tmx`
  exports `(module.key, de, target, status)`; changed sources come from the `de.json` diff,
  so no per-key hashes are needed.
- **Generated-artifact compatibility**: CSV headers, CSV file names and report/label
  content follow the UI language (`ReportCsvFileName` is already "befunde"/"findings").
  Import is positional — the header row is skipped when the first column has no `/`
  (`Wizards.cs:100`) — so a French export re-imports on a German UI. Pin that with the G9
  test. Never localize: `.knxproj` XML, group-address syntax, DPT identifiers, column
  order, file extensions.
- Live language switching stays as it is (markup extensions resolve once); the manual
  should say "restart for a fully switched UI" when it is next touched.

## 7. Pilot: de/en → a third language (`fr`)

Why French: two plural forms (no plural engine), Latin script (no RTL work), +15-25 % text
growth (stresses layout), established KNX vocabulary, realistic translator supply.

| Phase | Work | Effort |
| --- | --- | --- |
| 0a | Extract 904 keys to JSON, delete the three maps, verify with existing tests | 4 h |
| 0b | Loader, registry, fallback chain, `qps` language | 8 h |
| 0c | Remove the 4 hardcoded German sites; Settings list from the registry | 4 h |
| 0d | `tools/i18n`: check, CSV/XLIFF export, import, changelog, shots | 12 h |
| 0e | `tests/Knx.Localization.Tests`, CI job, screenshot artifacts | 10 h |
| 0f | Prune 242 dead keys, resolve the 4 collisions, i18n README, glossary, PR template, CODEOWNERS | 6 h |
| | **Phase 0 total** | **~44 h (5-6 dev days)** |
| 1a | Glossary, 30 terms de/en/fr | 4 h |
| 1b | Translate 656 live keys / 2,392 words | 14-20 h professional, or 6 h MT + 14 h review |
| 1c | Native review | 6 h |
| 1d | Layout fixes from text growth (settings, toolbars, tables) | 4-8 h |
| 1e | Shots, changelog, release note | 2 h |
| | **Phase 1 total** | **30-40 h, 2-3 calendar weeks with review latency** |
| 2 | Second language, TMX, agency XLIFF round-trip | 8 h dev + translation cost |

Acceptance for the pilot: `--check` clean for `fr`; `--lang=fr` screenshots reviewed by a
native speaker at 1440/1024/900; all CSV/report tests green in `fr`; registry moves
`preview` → `reviewed`; changelog entry present. Demo project content (`Wohnzimmer`,
device names) is sample data and is not translated.

## 8. What not to automate

- Native-language quality and the approval itself.
- Glossary term decisions and KNX domain correctness ("datapoint type", not "data point
  type"; "installation location", not "mounting place").
- Plural-form decisions for a new language.
- Layout judgement from screenshots (clipping, truncation, wrapping).
- Translation of manufacturer catalogue text and customer project content.
- CLA/legal review and the ship/no-ship decision for a language.
- Auto-merging translation PRs or auto-promoting a language status.
- Machine translation without a named reviewer; MT is allowed only as a labelled `draft`.

## 9. Open decisions

| # | Decision | Recommendation | Owner |
| --- | --- | --- | --- |
| 1 | JSON + embedded load instead of `.resx`/satellite assemblies | yes; reverses the "no build-time resources" comment in `Localization.cs:6-9` deliberately | maintainer |
| 2 | Commit the JSON (no codegen, no generated C#) | yes; generated code would churn every translation PR | maintainer |
| 3 | Orphan gate hard-fail after pruning 242 keys, or ratchet | prune, then hard-fail; keep runtime coverage for dynamic keys | maintainer |
| 4 | `preview` languages visible in Settings | yes, with a suffix; `draft` only via `--lang=` | maintainer |
| 5 | Pilot language | `fr`; a Slavic language (`pl`/`cs`) is the follow-up that forces plurals | maintainer |
| 6 | Plural strategy | defer; key suffixes plus a per-language rule when a Slavic language lands | maintainer |
| 7 | Screenshots in git | only `de`, only context-referenced screens, 960 px, < 150 KB | maintainer |
| 8 | CLA for translation-only contributors | keep the existing CLA; waiving it is a CLA-document change | maintainer |
| 9 | External platform (Weblate/Crowdin/none) | none now; the CSV/XLIFF export covers agencies | maintainer |
| 10 | Localized CSV headers/file names vs fixed English | keep localized, gate the round-trip | maintainer |
| 11 | RTL (`ar`, `he`) | out of scope; needs `FlowDirection` work and mirrored layout | maintainer |
| 12 | `i18n/**` code owner | `@CaeruleusAqua`, per-language lines later | maintainer |

## 10. Proposed patch series (not implemented here)

1. `i18n: extract the string catalogues to JSON` — mechanical, tests unchanged.
2. `i18n: load catalogues from JSON with a language registry` — loader, fallback, `qps`.
3. `i18n: prune dead keys and remove hardcoded German` — 242 keys, 4 sites.
4. `tools/i18n: checker, CSV/XLIFF export, changelog, screenshots`.
5. `tests: localization gates` + CI job + screenshot artifacts.
6. `docs: translator README, glossary, PR template, CODEOWNERS`.

Each step is independently reviewable and leaves CI green; steps 1-3 must not be squashed,
because step 1 is the only one that needs a byte-level comparison against the old
dictionaries.
