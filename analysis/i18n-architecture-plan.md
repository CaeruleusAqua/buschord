# Multilingual architecture plan

Status: investigation and design only. Base `2846c16`; no product, test, CI or
package file is changed by this branch. Coordinates with work packages H9.1-H9.6
(multilingual architecture, inventory, workflow, CI gates, pilot language,
boundaries).

## 1. Recommendation

Adopt **JSON message catalogs embedded per assembly, read by one small
BCL-only runtime in `Knx.Core`**, and keep the existing `Strings.Get` /
`WorkbenchStrings.Get` facades and `{loc:Tr}` / `{loc:Wb}` XAML syntax during a
strangler migration.

- One catalog file per language per owning assembly (`core.de.json`,
  `app.en.json`, `reports.fr.json`, ...), embedded as a resource. No satellite
  assemblies, no new runtime package, no translation service.
- Named placeholders plus a deliberately small ICU MessageFormat subset for
  plurals (`{count, plural, one {...} other {...}}`), number and date
  formatting. No full ICU dependency.
- Explicit language negotiation (`--lang` > environment > settings > OS >
  German) and an explicit fallback chain (requested -> base language -> English
  -> German -> visible marker).
- XAML markup extensions return a binding to a notifying indexer instead of a
  one-shot string, so already-open windows can switch language. Code-built
  Workbench views get an explicit refresh/rebuild path.
- CI validates catalogs, placeholders, key references, hardcoded UI text and a
  pseudo-locale. French is the pilot third language because it exercises plural
  categories (`0`/`1`), date and number formats better than Dutch.

Why not the alternatives:

- `.resx` + `ResourceManager` is the standard BCL answer, but it brings
  satellite assemblies, ResX XML merge noise and no plural model. It remains a
  valid fallback if ResX tooling is an organisational requirement.
- SQLite or a translation service is a mutable runtime database with a native
  dependency. It only pays off for live/crowdsourced translation, not for an
  offline desktop tool.
- Source-generated strongly typed keys solve typos, not translator workflow,
  fallback, plurals or live switching. Add generated key constants later as
  hardening; do not make them the foundation.
- The current dictionary with generated columns keeps today's shape but
  produces huge generated C# files, poor diffs, no translator context and no
  plural/format model. It is useful only as the source for the one-time catalog
  generation.

The migration is deliberately staged: first swap storage behind the existing
API (no call-site churn), then rename keys and fix ownership, then add French.
That keeps the existing de/en tests and CSV/report goldens green while the
architecture changes underneath.

## 2. Current mechanism and inventory

### 2.1 Lookup path

| Layer | Current implementation | Constraint |
|---|---|---|
| Core strings | `Knx.Strings` in `src/Knx.Core/Localization.cs`: `Dictionary<string,(string De,string En)>` with static `Language` | Two languages only; tuple columns do not scale; UI and report vocabulary lives in Core |
| Workbench strings | `src/Knx.App/Localization/WorkbenchStrings.cs`: same dictionary pattern, reads `Knx.Strings.Language`, static `Get` | Separate static map, no `INotifyPropertyChanged`; duplicates and drifts against Core |
| Catalogue UI strings | `src/Knx.App/Services/CatalogBrowser.cs`, `CatalogUiText` | A fourth dictionary with its own language check; should be folded into the app catalog |
| XAML | `{loc:Tr Key}` (`TrExtension`) and `{loc:Wb Key}` (`WbExtension`) | `ProvideValue` returns a plain string once at load; no live update; no format arguments; key typos are runtime-only |
| CLI | `--lang=` parsed in `src/Knx.App/Program.cs:35-49` | No validation; unknown value silently becomes German in the setter |
| Settings | `AppSettings.Language` default `"de"`, applied in `App.axaml.cs:67-69`; dialog hard-codes two indices (`SettingsWindow.axaml.cs:21,34`) | Adding a language requires code and UI changes; no "System" option |
| Runtime switch | `SettingsWindow` sets `Strings.Language`; `Strings.Refresh` raises `Item[]`/empty | No XAML binding listens; only windows opened afterwards show the new language (documented in `TrExtension.cs:9-10`) |
| Reports | `src/Knx.Reports/ProjectReport.cs` calls `Strings.Get` at generation time; `html lang` is de/en only; dates are `dd.MM.yyyy` invariant | Report strings are owned by Core; dates and `lang` do not follow the UI language properly |
| CSV | `ProjectCsvExporter` and `CompareWindow` localize headers and file names (`Csv*Header`, `ReportCsvFileName`, `CompareCsvFileName`) | Header text changes with UI language; delimiter/column contracts are untested across languages |
| Errors | Core/App throw English literals; some UI shows `ex.Message` directly (`App.axaml.cs:135`, `MainWindowViewModel.cs:699`) | Developer diagnostics leak into the UI; no error-code mapping |
| Tests | Console checks in `tests/*`; de/en chosen by mutating the global | No full key parity, no plural/format tests, no XAML key scan, no pseudo-locale, no live-switch test |

### 2.2 Scale

| Measure | Count |
|---|---|
| `Knx.Strings` keys | 451 |
| `WorkbenchStrings` keys | 415 |
| `CatalogUiText` keys | 42 |
| Total message entries | 908 |
| `{loc:Tr ...}` uses in XAML | 108 |
| `{loc:Wb ...}` uses in XAML | 38 |
| `Strings.Get(...)` calls in `src/` | 333 |
| `WorkbenchStrings.Get(...)` calls in `src/` | 52 |
| `CatalogUiText.Get(...)` calls in `src/` | 42 |
| Entries with positional placeholders (`{0}`, ...) | at least 144 |
| Entries with pseudo-plural `(s)` / `(e)` / `(en)` | 9 |

Cross-catalog key collisions already exist: `MenuEdit` and
`CommunicationObjects` (same values), `SearchResults` and `CompareSummary`
(different values). A global key namespace is therefore not optional; it is a
prerequisite for one lookup table.

### 2.3 What the current design does well

- A single `Get(key, args)` call site shape is easy to migrate; the XAML syntax
  is already concise.
- Missing keys fail visibly instead of crashing, and unknown languages fall back
  to German.
- Project check findings keep code, path and severity stable across languages
  (`tests/Knx.Core.Tests/Program.cs:692-762`), which is the right contract.
- CSV and report tests already lock the German and English output
  (`tests/Knx.Workbench.Tests/Program.cs:664-678`,
  `src/Knx.App.Tests/Program.cs:159-182`), so a storage swap can be proved
  byte-for-byte.
- The app is offline and single-process. That rules out a server-backed
  translation system and favours embedded catalogs.

### 2.4 Structural problems to solve

1. **No translator source of truth.** Translators edit C# string tuples, with
   escaping, merge conflicts and no context.
2. **Two-language shape is baked into the API.** `(De, En)`, `_language ==
   "en" ? En : De`, `Available = ["de","en"]`, two-item settings combo.
3. **No plural or format model.** `"{0} Befunde"` renders "1 Befunde";
   `(s)`/`(e)` workarounds are visible in 9 keys. `string.Format` uses the OS
   culture, not the UI language, and a `FormatException` silently returns the
   raw template.
4. **No ownership.** Core contains menu/button/status strings and report
   headings; `Knx.Reports` reaches into Core's UI dictionary. App adds a fourth
   dictionary in `CatalogBrowser.cs`.
5. **No compile-time or CI key safety.** XAML keys and `Strings.Get("...")`
   literals are checked only when executed. A typo ships as a visible key.
6. **Runtime switch is mostly a lie.** The settings dialog applies the language
   immediately, but open windows do not update because markup extensions resolve
   once.
7. **Language and culture diverge.** UI language is `"de"`/`"en"`, while dates
   and numbers use `CultureInfo.CurrentCulture` (report dates are hard-coded
   invariant German format).
8. **Data and UI are not separated by contract.** Project/device/manufacturer
   names are already passed as arguments, but nothing prevents a future key from
   embedding them; CSV headers and file names are localized without a stated
   machine-interface policy.
9. **Hardcoded German remains.** The clean-code review lists concrete paths in
   `MainWindowViewModel`, `LineScanSession`, `Commands` and `CatalogUiText`
   (`analysis/clean-code-review/app.md`, item 6). A catalog alone does not fix
   these.
10. **No RTL or script readiness.** `FlowDirection` is never set; technical
    strings such as `1/2/3` and `DPST-9-1` need explicit LTR isolation in an RTL
    layout.

## 3. Options compared

| Criterion | `.resx` + `ResourceManager` | JSON embedded resources | SQLite / translation service | Source-generated typed keys | Current dictionary + generated columns |
|---|---|---|---|---|---|
| BCL / new dependency | BCL; satellite assemblies generated | BCL (`System.Text.Json`); no new package | New native package / network service | Build-time Roslyn project; no runtime package | BCL; build step |
| Translator workflow | ResX editor, XLIFF tools; XML diff noise | Clean diffs, schema, CSV/XLIFF export easy | Web UI possible | Depends on catalog format | CSV columns; poor context |
| Compile-time safety | Typed class only with VS generator or extra tooling | None by default; add source scan/generator | Runtime only | Best (compile error) | Generated constants possible |
| Missing-key behaviour | `GetString` returns null; culture fallback built in | Loader owns chain and marker | Service decides | Compile error / generated fallback | Same as today |
| Fallback | Built-in culture chain | Explicit per-key chain | Service-defined | Generated fallback | Same as today |
| Per-assembly ownership | Strong: one ResX per project | Strong: one JSON per project/assembly | Weak: central mutable store | Per project | Weak/central |
| Testability | `ResourceManager` enumeration; good | Direct dictionary/catalog fixtures; best | Needs a database/service fixture; poor | Compile-time plus direct fixtures; good | Direct dictionary; good |
| Hot reload | No (rebuild/reload) | Yes in DEBUG via file watcher | Yes | Only if catalogs reload | No |
| Trimming / AOT | `ResourceManager` + satellites have trim/single-file caveats | Safe if parsed with a source-generated `JsonSerializerContext` | Native SQLite and networking complicate AOT | Safe | Safe |
| Performance | Dictionary cache, fast | Load once, O(1) lookup | Query per key is slow without a cache | Static fields, fastest | Good |
| Verdict | Valid fallback; rejects the repo's no-satellite stance and has no plural model | **Recommended** | Rejected for an offline desktop app | Add later as hardening on top of JSON | Rejected as target; useful only to seed JSON |

Notes:

- **ResX** would be the right answer if the organisation already runs a ResX
  translation pipeline or requires satellite assemblies. It cannot express
  German/English/French plural categories without a parallel convention, and its
  generated designer class is a Visual Studio feature, not an SDK build feature.
- **JSON** keeps the current deployment shape (one app assembly plus embedded
  data), gives translators a file they can read, and makes plural forms explicit.
  The runtime is a few hundred lines including the formatter, not a framework.
- **SQLite/service** is the only option that supports live translation updates
  without shipping a build. That is not a BusChord requirement; adding a native
  dependency and a second source of truth would be a net loss.
- **Generated keys** are valuable after key names stabilise. They do not replace
  catalog validation, fallback, plurals or translator context.

## 4. Recommended architecture

### 4.1 Shape

```text
src/Knx.Core/Localization/
  Localizer.cs              // one instance per catalog owner
  LanguageState.cs          // active language/culture, negotiation, event
  MessageTemplate.cs        // parse + render named/ICU-subset messages
  PluralRules.cs            // de/en/fr/nl cardinal categories
  CatalogLoader.cs          // embedded JSON, validation, lazy per language
  languages.json            // code, culture, native name, RTL, completeness
  core.de.json
  core.en.json
src/Knx.App/Localization/
  app.de.json
  app.en.json
  TrExtension.cs            // {loc:Tr} -> binding to Strings.Instance indexer
  WorkbenchStrings.cs       // facade over Localizer, keeps static Get
  WbExtension.cs            // {loc:Wb} -> binding to WorkbenchStrings.Instance
src/Knx.Reports/Localization/
  reports.de.json
  reports.en.json
  ReportStrings.cs          // facade for report/CSV keys
```

No new project is required: `Knx.App` and `Knx.Reports` already reference
`Knx.Core`. If the runtime later needs to be shared with `Knx.Mcp` without Core,
extract it into `Knx.Localization`; do that only when a second non-Core consumer
appears. `Knx.Mcp` stays English (machine protocol), so it does not need a
catalog.

Each catalog owner gets its own `Localizer`; there is no global reflection-based
catalog discovery. This avoids type activation and trimming problems, and it
keeps the four existing dictionaries from colliding during the migration. A
shared `common` catalog (or `common.*` keys in Core) is passed as fallback to
App and Reports.

### 4.2 Runtime API

```csharp
public sealed record LanguageInfo(
    string Code,            // "de", "en", "fr", "nl"
    CultureInfo Culture,    // de-DE, en-GB, fr-FR, nl-NL
    string NativeName,      // "Deutsch", "English", "Français", "Nederlands"
    bool IsRtl,
    int Completeness);      // 0-100, preview flag derived from it

public static class LanguageState
{
    public static string Requested { get; }   // raw tag, or "system"
    public static LanguageInfo Current { get; }
    public static IReadOnlyList<LanguageInfo> Available { get; }
    public static event EventHandler? Changed;
    public static void Apply(string tag, bool persist = false);
}

public sealed class Localizer
{
    public Localizer(string name, Assembly assembly, string resourcePrefix,
                     params Localizer[] fallbacks);
    public string Get(string key, params object[] args);
    public string Get(string key, IReadOnlyDictionary<string, object?> args);
    public bool TryGet(string key, out string text);
    public IReadOnlyCollection<string> Keys { get; }
}
```

`Knx.Strings.Language` remains as a compatibility property delegating to
`LanguageState.Apply`; `Strings.Get` delegates to its Core `Localizer`.
`WorkbenchStrings.Get` delegates to the App `Localizer`. Existing call sites do
not change in stage 0. `Strings.Catalogue` can initially project the two
existing languages for the current tests, then be replaced by `Keys` plus
per-language access. The `params object[]` overload stays for legacy positional
templates (`{0}`); new named/plural messages use the dictionary overload.

The lookup path is:

```text
Localizer.Get(key)
  -> current culture catalog (exact tag, then base tag)
  -> fallback catalogs in declared order
  -> marker or exception according to MissingKeyBehavior
```

Catalog entries are parsed once per language into an immutable dictionary.
`FrozenDictionary` (net9) is appropriate for the release build. Loading is lazy
per language; the startup cost is one file per active/fallback language, not all
languages. Fallback cycles are rejected when a `Localizer` is constructed.

### 4.3 Catalog format

Per-language JSON, UTF-8, one file per owner. A simple entry is a string; a
plural entry is an ICU-subset message. Translator context lives in `meta` in the
source-language file.

```json
{
  "language": "de",
  "strings": {
    "app.menu.file.open": "Projekt öffnen…",
    "app.status.linksAdded": "{count, plural, one {# Verknüpfung angelegt} other {# Verknüpfungen angelegt}}",
    "app.replace.preview": "{devices, plural, one {# Gerät betroffen} other {# Geräte betroffen}}, {links, plural, one {# Verknüpfung bleibt erhalten} other {# Verknüpfungen bleiben erhalten}}."
  },
  "meta": {
    "app.menu.file.open": { "comment": "File menu; ellipsis opens a dialog", "maxLength": 40 },
    "app.status.linksAdded": { "comment": "Status line after linking objects" }
  }
}
```

Rules:

- `language` must match the file suffix; keys are unique within the catalog and
  across all registered catalogs.
- String values are the normal case. Plural messages use the ICU subset; no
  object-shaped plural tables, because translators and tools understand ICU.
- `meta` is optional and source-language only. It carries `comment`,
  `maxLength`, and optional `screenshot` (path relative to the repo) for
  ambiguous UI strings. It never ships as UI text.
- The parser and validator reject unknown placeholder names, unbalanced braces,
  invalid plural categories and literal `<`/`>` in values.
- No HTML, Markdown or code in values. Reports escape every value; a key that
  needs emphasis gets a separate template/format, not raw markup.

### 4.4 ICU subset

Support exactly this and no more:

| Form | Meaning |
|---|---|
| `{name}` | Named argument, rendered with its `ToString`/formatter |
| `{count, plural, one {...} other {...}}` | Cardinal plural; `#` is the formatted count |
| `{count, number}` | Number in the UI culture (`N0` for counts) |
| `{date, date, short}` / `{date, date, long}` | `d` / `D` in the UI culture |
| `{date, time, short}` / `{date, time, long}` | `t` / `T` in the UI culture |

Nested plural is allowed to depth 2 for sentences with two counts. `select` and
ordinals are not needed today; add them when a real key demands it. Literal
braces are forbidden; no escaping syntax to maintain.

`PluralRules` is a small CLDR-subset table:

| Language | Categories |
|---|---|
| de, en, nl | `one` (n = 1), `other` |
| fr | `one` (n = 0 or 1), `many` (n = 1,000,000 and multiples), `other` |

Missing categories fall back to `other`. This is the deliberate ceiling: a
`ponytail:` comment in the implementation should say "CLDR cardinal subset for
de/en/fr/nl; replace with ICU/CLDR data when a Slavic, Arabic or ordinal language
is added".

### 4.5 Language negotiation

Precedence, highest first:

1. `--lang=<tag>` (also `--lang=system`, `--lang=qps-ploc` and `--lang=qps-rtl`
   for tests)
2. `BUSCHORD_LANG` environment variable (portable/tests; optional)
3. `AppSettings.Language` (`"system"` or a tag)
4. OS UI culture (`CultureInfo.CurrentUICulture`; OS regional format for the
   `CultureInfo`)
5. `de`

Normalisation: accept BCP-47, use the exact catalog if one exists, otherwise the
primary subtag (`de-AT` -> `de`, `fr-CA` -> `fr`). A tag with a region selects
the regional `CultureInfo` for dates/numbers but still uses the base catalog
unless a regional catalog exists. `Available` is read from the embedded
`languages.json` manifest, not hard-coded in C#; adding a language to the
existing owners is a manifest entry plus catalog files. `languages.json` maps
`de -> de-DE`, `en -> en-GB`, `fr -> fr-FR`, `nl -> nl-NL` by default; a regional
tag may override the `CultureInfo` without requiring a regional catalog.
**Stage 0 keeps the existing `"de"` default** so the settings round-trip test
stays green; stage 1 switches new installs to `"system"` and updates that one
expectation. Existing settings that already contain `"de"` or `"en"` keep their
value (no silent switch on upgrade). `--lang` is not persisted.

Unknown tags are **not** silently accepted: print a warning to stderr, fall back
to the OS language (or `de` if that is unavailable), and continue. CI tests this.
Transitional: the existing `Strings.Language` setter keeps its current
unknown-tag -> `de` behaviour in stage 0 so the existing assertion stays green;
`LanguageState.Apply` (used by startup/settings/CLI) uses the negotiation above.
Stage 1 aligns the setter and updates the test.
The settings dialog lists `Available` plus "System (OS)" with native names; it is
data-driven, not index-based. A language with `Completeness < 100` is labelled
"preview" and still selectable.

### 4.6 Fallback chain

For requested language `L`:

```text
exact tag (fr-CA) -> base tag (fr) -> en -> de -> [[key]]
```

For `de`, start at `de` then `en`; for `en`, start at `en` then `de`. Rationale:
German is the source language and default, English is the most complete
translation, and a missing French key should not drop the user into an unrelated
language. `[[key]]` is the last resort and is treated as a bug in tests.

Fallback is per key, not per catalog: a partially translated French UI may mix
French and English. The settings entry reports completeness so this is visible
rather than surprising. Runtime machine translation is never used.

### 4.7 Runtime language switch and XAML

`TrExtension.ProvideValue` should return an Avalonia `Binding` to
`Strings.Instance[Key]` (or a dedicated `LocalizedText` object) instead of
`Strings.Get(Key)`. `Strings.Instance` already raises `PropertyChanged` for
`Item[]`; with a binding, all `{loc:Tr}` controls update when
`LanguageState.Changed` fires. `WbExtension` does the same against a notifying
`WorkbenchStrings.Instance`; the static `Get` API stays for code.

This is the one area that needs a short spike before the migration: Avalonia 12
must accept the returned binding from a markup extension and update a plain
`TextBlock`. If it does not, the fallback is to rebuild the affected window on
language change (preserving project/session state) and remove the "applied
immediately" claim from settings. Test both paths at the window level.

Fig. 1: language switch data flow.

```text
--lang / env / settings / OS
          |
          v
   LanguageState.Apply(tag)
          |
          +--> load catalogs for requested + fallbacks (lazy)
          +--> set CurrentUICulture and FlowDirection
          +--> Changed event
                    |
                    +--> Strings.Instance.PropertyChanged("Item[]")
                    |       -> {loc:Tr} bindings update
                    +--> WorkbenchStrings.Instance.PropertyChanged("Item[]")
                    |       -> {loc:Wb} bindings update
                    +--> WorkbenchWindow.RefreshLocalizedText()
                            -> code-built views re-render or rebuild
```

Code-built Workbench content (`WorkbenchWindow.axaml.cs`, inspectors, toolbars)
does not use markup extensions. It needs one `RefreshLocalizedText()` entry
point that re-runs the text assignments or rebuilds the current tool view.
Status messages and transient validation errors can be recomputed; a full
window rebuild is the safe fallback but loses focus/scroll state, so prefer
targeted refresh.

### 4.8 Missing keys and diagnostics

Three modes, selected by build/test context:

| Mode | Behaviour |
|---|---|
| `Throw` | Tests and catalog validation; missing key fails the run |
| `Marker` | DEBUG; returns `[[key]]`, logs once |
| `FallbackAndLog` | Release; returns the fallback text, logs key and language once to `Trace`/log file |

`Get` never silently returns the raw key without a marker. The old
`FormatException` swallow is removed: a malformed template is a catalog bug and
fails validation before shipping; in release it renders the marker and logs.
Diagnostics include catalog name, key, requested language and the fallback used,
but never the formatted user data.

Transitional exception: stage 0 keeps the legacy raw-key return for the existing
`Strings.Get` facade, because `tests/Knx.Core.Tests/Program.cs:676-677` asserts
it and a storage swap must not change behaviour. The new `Localizer` gets
Throw/Marker tests in stage 0; stage 1 flips the facade to the new contract and
updates that one assertion.

### 4.9 Hot reload

- **DEBUG only:** load catalogs from disk when the file exists, watch the
  `Localization` folders with a debounced `FileSystemWatcher`, and swap the
  immutable dictionary on the UI thread. A parse error keeps the previous
  catalog and logs the error.
- **Release:** embedded resources only. No file I/O, no watcher, no reload.
- Hot reload is a developer convenience, not a translator feature. It is not a
  reason to ship loose JSON files beside the executable.

### 4.10 Trimming, AOT and performance

- Embedded JSON plus `Assembly.GetManifestResourceStream` is trim-safe. Do not
  use `ResourceManager`/satellite probing.
- Parse with a source-generated `JsonSerializerContext` (`[JsonSerializable]`)
  or `JsonDocument`; do not rely on reflection-based serialization in a future
  trimmed/AOT build.
- One `FrozenDictionary` per language, built lazily; lookups are O(1). Avoid
  loading all languages at startup.
- Expected cost: under 1 ms per catalog of ~1,000 entries after JIT, well below
  the existing startup work; measure in `Knx.WorkbenchPerformance.Tests` if it
  becomes noticeable.
- XAML bindings add ~146 bindings, negligible compared with the existing view
  tree. Do not bind every table cell to a markup extension; table cells already
  use view-model strings.

### 4.11 Reports, CSV, errors and MCP

**Reports.** `ProjectReport` snapshots the active language and `CultureInfo` at
the start of generation. It resolves all `ReportStrings` once, writes
`<html lang="<tag>" dir="<ltr|rtl>">`, formats dates with `d`/`g` (not
`dd.MM.yyyy`), and keeps numbers in the UI culture. It escapes every catalog
value and every user value. A report generated in German stays German even if
the UI switches while it is open; a new generation picks up the new language.

**CSV.** CSV is presentation output, not a stable machine API. Keep the current
localized headers and localized suggested file names for compatibility with the
existing tests and user expectations, but state the policy: integrations must
use `.knxproj`/JSON, not localized CSV. Validate that every language's header has
the same column count and contains no `;`, newline or quote; keep the existing
semicolon + quoting rules. If a machine-readable export is needed later, add a
separate explicit schema with invariant English headers rather than changing
these files.

**Errors.** Exceptions crossing Core/App APIs stay English invariant; they are
diagnostics. UI code maps known failures to keys and appends the technical
detail as an untranslated argument, e.g.
`Strings.Get("app.error.parameterUnreadable", ex.Message)`. Do not localize
`InvalidDataException` messages and do not show `ex.Message` as the primary
message. `ProjectCheck` stays as it is: localized message, stable code/path/
severity.

**MCP.** Tool names, descriptions, JSON schemas and protocol errors stay English
and invariant. A client that wants localized text localizes by error code.
Do not couple `Knx.Mcp` to the UI catalog; that would make a machine protocol
depend on the desktop language.

### 4.12 RTL readiness

- `LanguageInfo.IsRtl` drives `FlowDirection` on each window/root control (and
  a default style), not a single global property.
- XAML uses grid columns and start/end alignment, not hard-coded left/right,
  where practical. Directional glyphs (arrows, chevrons) are mirrored or
  replaced by neutral icons.
- Technical values (individual/group addresses, DPT codes, hex, file paths) are
  wrapped in an LTR run/control in RTL layouts; they are never translated or
  reordered.
- Font fallback for Arabic/Hebrew is a later concern; Inter does not cover those
  scripts. Do not claim RTL support until a real RTL catalog and screenshot pass
  exist. A `qps-rtl` pseudo-locale can exercise mirroring before then.

### 4.13 Explicit non-goals

- No machine translation at runtime or in CI.
- No live translation service, database or admin UI.
- No full ICU/CLDR dependency for four languages.
- No translation of KNX/DPT identifiers, manufacturer names, product names,
  customer/project data, addresses, serial numbers, passwords or tokens.
- No automatic language change based on the project file's language.
- No translation of MCP protocol text.

## 5. Contracts

### 5.1 Key naming

- Pattern: `^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$`, ASCII, 2-6 dot-separated
  segments, at most 64 characters.
- Prefixes: `common.` (shared labels), `core.` (domain/check), `app.` (shell/UI),
  `report.` (HTML and CSV presentation).
- Shape: `area.subject.action`, e.g. `app.menu.file.open`,
  `app.status.deviceAdded`, `core.check.p020`,
  `report.section.topology`, `report.column.address`.
- Keys are stable identifiers. Never rename or reuse a key with a different
  meaning. A rename keeps an alias for one release; the old key is marked
  deprecated in the catalog and removed after the release.
- No user-visible text, product name, manufacturer name, punctuation or
  placeholder in a key. No key built by string concatenation from user input;
  enum-to-key mapping uses a switch/table of constants.
- One key per sentence/fragment that can be translated independently. Do not
  concatenate translated fragments to build a sentence; use placeholders or a
  full template.

### 5.2 Ownership

| Prefix | Owner | May contain | Must not contain |
|---|---|---|---|
| `common.` | Core | Cancel, Close, Save, Name, Address, Yes/No, severity labels | Menu structure, report headings |
| `core.` | `Knx.Core` | Project check messages/kinds, domain validation, shared statuses | Buttons, menus, dialogs, HTML/CSV headers |
| `app.` | `Knx.App` | Menus, ribbons, tabs, dialogs, tooltips, settings, status, local errors | Report/CSV headings, domain check text |
| `report.` | `Knx.Reports` | HTML sections/columns/facts, CSV headers/file names, report-only errors | Shell/menu/dialog text |

- A key has exactly one owner. Duplicate keys across catalogs fail CI. Shared
  labels move to `common.` rather than being copied.
- `Strings.Get` resolves Core + `common.`; `WorkbenchStrings.Get` resolves App +
  `common.`; `ReportStrings.Get` resolves Reports + `common.`. A debug assertion
  can reject a prefix outside the facade's scope.
- `CatalogUiText` is deleted after its keys move to `app.catalog.*`.
- Report keys currently in `Knx.Core/Localization.cs` move to
  `Knx.Reports/Localization/reports.*.json`; `ProjectReport` uses
  `ReportStrings`.
- Tests may have their own fake catalog but must not add production keys under a
  test-only prefix.

### 5.3 Placeholders, plurals and formats

- New and changed keys use named placeholders: `{count}`, `{name}`,
  `{address}`, `{date}`. Names are `[a-z][a-zA-Z0-9]*`, no spaces.
- Existing positional `{0}` keys are supported during migration; do not add new
  ones. Convert a key when you touch its plural/format behaviour. The
  compatibility allowlist is reduced each stage and removed at the end.
- Plural keys must contain `other`; categories must be valid for the catalog's
  language. Source and translation must use the same placeholder names; they
  may use different plural categories (e.g. French adds `many`).
- Format counts/dates inside the formatter, not before `Get`. Passing a
  pre-formatted string destroys plural selection and culture correctness.
  Pass integers, `DateTimeOffset`, decimals; use `{count, number}` for display
  and keep technical values invariant.
- `#` inside a plural is the formatted count. Numbers use `N0` for counts and
  the catalog language's `CultureInfo`; dates use `d`/`D`/`t`/`T`.
- No literal `{`/`}` in values; no HTML (`<`, `>`); no `;` in CSV header
  values; no newline in any value.
- Do not compose a sentence from multiple translated fragments. One template per
  sentence, with placeholders for data.

### 5.4 Dynamic strings and user data

- Project name, installation/area/line/device names, group address names,
  descriptions, comments, product names, order numbers, manufacturer names,
  serial numbers, file paths, addresses, DPT codes, raw bus values and exception
  messages are **arguments**, never catalog keys or catalog values.
- They are passed as raw values and escaped by the consumer (XAML text, HTML
  escape, CSV quote). They are never translated or sent to a translation
  service.
- User data must not appear in keys, comments, screenshots checked into the
  repo, or translation exports. Use the demo project for screenshots.
- A status message is one key; dynamic details are placeholders. Do not build
  messages with `message += ...` from separately translated pieces.
- Empty user data is rendered as the caller's empty-state key, not by
  translating an empty string.

### 5.5 Catalog content rules

- UI text only. No credentials, tokens, passwords, connection strings, customer
  names, manufacturer marketing text or copied product descriptions.
- No HTML/Markdown; reports escape values.
- No leading/trailing whitespace; use a real space or punctuation in the value.
  No `TODO`, `FIXME`, `XXX`, `lorem`, or machine-translation markers.
- Source language is German (`de`); `en` is a complete translation; a new
  language may ship as `preview` with an explicit completeness value, never as
  silently missing keys.
- Comments explain context (where the string appears, what the placeholder
  means, whether it is a button/menu/tooltip). `maxLength` is advisory and
  enforced by pseudo-loc screenshots, not by runtime truncation.
- Translator changes are reviewed like code: one language per PR where possible,
  no unrelated key renames, screenshots for changed UI, glossary terms
  (e.g. "Gruppenadresse" -> "group address", "Kommunikationsobjekt" ->
  "communication object", "Jalousie" -> "blind/shutter" per glossary).

### 5.6 Report and CSV contract

- Report HTML declares `lang` and `dir`; CSS uses logical properties
  (`margin-inline-start`, `text-align: start`) rather than left/right.
- Dates and numbers in reports use the active UI culture; technical identifiers
  (addresses, DPT, GUIDs) stay invariant.
- CSV headers/file names remain localized presentation for now. Every language
  must produce the same column count and valid quoting; header values must not
  contain the delimiter. Machine consumers use `.knxproj`/JSON.
- Suggested file names are localized only where they already are; do not add
  user/project names to file names.
- The report/CSV language is snapshotted per generation, so an open report does
  not change mid-render.

## 6. Test strategy and CI gates

### 6.1 Test suites

Add a dependency-free console project `tests/Knx.Localization.Tests` (same style
as the existing suites) and extend the surface tests.

**Catalog and key validation**

1. Every catalog is valid UTF-8 JSON; `language` matches the file name; keys are
   unique within and across catalogs.
2. Key regex, length, non-empty value, no surrounding whitespace, no forbidden
   markers (`TODO`, `FIXME`, `XXX`).
3. Key parity: every `de` key exists in `en`; every `fr` key either exists or is
   listed in the preview-missing manifest. Completeness is printed and gated
   (non-preview must be 100%).
4. Placeholder parity: same names in every language; plural categories valid;
   `other` present; balanced braces; only supported ICU forms.
5. Forbidden content: no `<`/`>` in values, no `;`/newline in CSV header
   values, no credential-like patterns, no duplicate key with different owner.
6. Metadata: comments present for keys flagged ambiguous; `maxLength` positive.

**Source reference validation**

7. Extract literal keys from `Strings.Get`, `WorkbenchStrings.Get`,
   `ReportStrings.Get`, `CatalogUiText.Get`, `W(...)`, `{loc:Tr ...}` and
   `{loc:Wb ...}`; every key exists in the owning catalog.
8. Flag non-literal key arguments (concatenation/interpolation) unless marked
   with a `// loc:dynamic` allowlist comment; flag orphan keys with a dynamic
   allowlist.
9. Flag concatenation of localized calls (`Strings.Get(...) + ...`) and
   translated-fragment composition.
10. Hardcoded UI text scan: XAML `Text`/`Content`/`Header` literals without
    `{loc:...}` or a binding; C# UI string literals containing German-specific
    characters or a curated word list. Start with a baseline and fail only on
    new occurrences, so the existing debt does not block the migration.

**Runtime**

11. Negotiation matrix: CLI > env > settings > OS > de; `system`; `de-DE` ->
    de; `fr-CA` -> fr; unknown tag warns and falls back; `--lang` is not
    persisted.
12. Fallback matrix: requested -> base -> en -> de -> marker; per-key fallback;
    empty value treated as missing.
13. Plural/format: 0, 1, 2, 1000 in de/en/fr/nl; `#` formatting; French `many`;
    date/number culture; technical values invariant.
14. Live switch: a bound `TextBlock` updates after `LanguageState.Changed`; a
    code-built Workbench view refreshes; `FlowDirection` changes for RTL.
15. Missing-key modes: Throw in tests, Marker in debug, FallbackAndLog in
    release with no exception; log once per key/language.
16. DEBUG hot reload: changed file reloads; invalid JSON keeps the previous
    catalog and logs.
17. Performance smoke: catalog load and 100k lookups stay below a generous
    threshold; no file I/O in release.

**Surface**

18. CSV: de/en golden headers unchanged; every language has the same column
    count and no delimiter in headers; file-name keys stable.
19. HTML report: `lang`/`dir`, localized sections, date format, escaping of
    `<script>` in a project name; report language snapshot.
20. Project check: code/path/severity/order stable across languages; French
    plural message keeps the code/path.
21. Error mapping: a known failure path shows a localized message, with the
    technical detail appended untranslated; no raw `ex.Message` as primary.

### 6.2 Pseudo-localization

Add two generated pseudo-locales, not catalog files:

- `qps-ploc`: transform every value to `[!! ... !!]`, lengthen by ~30%, add
  accented characters and brackets. Catches hardcoded text, truncation,
  concatenation and layout overflow.
- `qps-rtl`: same plus `IsRtl = true`, to exercise mirroring and LTR isolation
  of technical values before a real RTL language exists.

`Available` excludes them from settings; `--lang` accepts them for tests. Run
`--workbench-selftest --lang=qps-ploc` on Windows and assert no `[[` marker, no
missing-key log and a successful screenshot pass. A screenshot diff is optional
in CI (artifact for review) because pseudo-loc deliberately changes pixels.

### 6.3 CI gates

Add to `.github/workflows/ci.yml`:

- `tests/Knx.Localization.Tests` in `TEST_PROJECTS` (Linux and Windows).
- Windows: `--workbench-selftest --lang=qps-ploc` and, after stage 2,
  `--lang=fr`; keep the existing de/en runs.
- A strict catalog check that fails on missing/extra keys, placeholder
  mismatch, forbidden content, orphan keys and new hardcoded literals.
- A completeness threshold: non-preview languages 100%; preview languages
  reported, not failed.
- PR template/label: `i18n` changes require de/en parity, a screenshot for UI
  changes, and a native-speaker review for a new language. No machine
  translation without review.
- Optional: upload pseudo-loc screenshots as CI artifacts for visual review.

Do not make CI depend on a translation service or network. All checks run from
the checkout.

## 7. Migration plan

### Stage 0 — storage swap behind the existing API (quick win)

**Goal:** JSON catalogs become the source of truth without changing a single
call site or XAML file.

1. Add the runtime (`Localizer`, `LanguageState`, `MessageTemplate`,
   `PluralRules`, `CatalogLoader`) and embedded resources.
2. Generate `core.de/en.json` (all current Core keys, including report/CSV),
   `app.de/en.json` (WorkbenchStrings) and a temporary `catalog.de/en.json`
   (CatalogUiText) with a one-off script. Verify every key and value
   byte-for-byte in both languages; reconcile the four cross-catalog collisions
   by keeping each catalog local (no global merge yet). Report keys stay in Core
   until stage 1, so `ProjectReport` keeps working unchanged.
3. Point `Strings.Get`, `WorkbenchStrings.Get` and `CatalogUiText.Get` at their
   `Localizer`; keep `Catalogue`/`Available` compatibility projections for the
   existing tests.
4. Add `LanguageState` and keep `Strings.Language` as a wrapper. Validate
   `--lang`; add the `"system"` option and the data-driven dialog list while
   keeping the `"de"` default for compatibility.
5. Change the markup extensions to return bindings; add
   `WorkbenchWindow.RefreshLocalizedText()` (or rebuild) for code-built views.
6. Add the validator, source-reference scan, pseudo-locale and CI entry.

**Exit criteria:** all existing tests pass unchanged; de/en output is identical;
live switch works in a window test; `qps-ploc` runs; no product strings changed.

### Stage 1 — ownership and key migration

**Goal:** one key namespace and one owner per string; delete the fourth
dictionary and UI vocabulary from Core.

1. Apply a checked-in `key-map` (old -> new) in one mechanical commit:
   `common.*`, `core.*`, `app.*`, `report.*`. Update call sites and XAML.
2. Move report/CSV keys to `Knx.Reports` and introduce `ReportStrings`; fold
   `CatalogUiText` into `app.catalog.*`.
3. Replace the nine `(s)`/`(e)` pseudo-plurals with plural entries; convert
   touched positional placeholders to named ones. Keep a shrinking allowlist for
   untouched `{0}` keys.
4. Remove hardcoded German in `MainWindowViewModel`, `LineScanSession`,
   `Commands` and the remaining dialogs; add keys and a source-scan baseline.
5. Move date/number formatting in `ProjectReport`/`CompareWindow` to the UI
   culture; add `lang`/`dir` and logical CSS.
6. Make settings language data-driven; switch new-install default to `"system"`,
   preserve existing stored `"de"`/`"en"`, and update the settings test that
   currently expects `"de"` for a missing property.

**Exit criteria:** key parity 100% for de/en; no duplicate keys; no
shell-specific UI text in Core (`common.` labels excepted); no new hardcoded
literals; CSV/report goldens updated only for the intentional date/`lang`/`dir`
changes; live switch covers the Workbench.

### Stage 2 — French pilot

**Goal:** prove the architecture with a third language and real plural/format
differences.

1. Add `core.fr.json`, `app.fr.json` and `reports.fr.json` (complete, reviewed);
   add the `fr-FR` culture and `one`/`many`/`other` rules.
2. Add `fr` to `Available` and the settings list; mark preview until review is
   complete; report completeness.
3. Add CI `--lang=fr` Workbench selftest/screenshot; CSV/report French tests;
   plural 0/1/1,000,000; date `dd/MM/yyyy`; number formatting.
4. Add translation export/import (CSV or XLIFF) only if external translators are
   involved; otherwise the JSON PR workflow is enough.

**Exit criteria:** French UI complete and reviewed; fallback to English/German
works for a deliberately missing key; no layout overflow in screenshots; all
gates green.

### Stage 3 — optional hardening

- Generate strongly typed key constants from catalogs (`Keys.App.Menu.File.Open`)
  and migrate call sites incrementally; CI checks the generated file is current.
  Do this only after key names stop moving.
- Expand `PluralRules`/ICU forms when adding Slavic/Arabic/ordinal languages;
  add a real RTL catalog and font fallback.
- Add translation memory/glossary tooling if the translator community grows.
- Consider a machine-readable CSV schema separate from the localized
  presentation export.

### Effort and risk

| Stage | Work | Effort | Risk | Main mitigation |
|---|---|---|---|---|
| 0.1 | Runtime, loader, formatter, plural rules | 2-3 d | Medium: formatter edge cases | Keep ICU subset small; unit tests |
| 0.2 | Generate catalogs from dictionaries; facade wiring | 1 d | Low: value drift | Byte-for-byte parity test over all keys |
| 0.3 | Language negotiation, settings, CLI validation | 1-2 d | Medium: OS/settings precedence | Matrix tests; explicit `system` |
| 0.4 | XAML binding live switch | 1-2 d | Medium: Avalonia markup extension behaviour | Spike first; rebuild fallback |
| 0.5 | Validator, source scan, pseudo-loc, CI | 1-2 d | Low | Baseline for existing hardcoded text |
| 1.1 | Key namespacing and ownership move | 2-3 d | Medium: large mechanical diff | Key-map file; one commit; no behaviour change |
| 1.2 | Plural/format migration | 2 d | Medium: translation wording | Native review; golden tests |
| 1.3 | Hardcoded German cleanup | 2-3 d | Medium: scope creep | Clean-code review list as the backlog |
| 1.4 | Report/CSV culture and HTML lang/dir | 1 d | Low | Snapshot/golden tests |
| 2.1 | French translation and review | 2-3 d + translator | Medium: quality/context | Glossary, comments, screenshots |
| 2.2 | French plural/date/number and CI | 1 d | Low | Matrix tests |
| 3.1 | Generated key constants | 1-2 d | Low | After key freeze |

Rough total: 12-18 developer days plus translation/review. The work is
parallelisable after stage 0: one stream on the runtime, one on catalog
generation, one on tests/CI, one on the Workbench hardcoded-text cleanup.

### Rollback

Each stage is independently revertible:

- Stage 0 keeps the old API and can fall back to the dictionaries if the JSON
  loader is disabled by a build flag for one release.
- Stage 1 is a mechanical rename; the key map and git history make a revert
  clean.
- Stage 2 adds a language; removing `fr` from the manifest and files returns to
  de/en without touching code.

## 8. Risks and open questions

| Risk | Impact | Mitigation |
|---|---|---|
| Avalonia markup extension cannot return an updating binding | Live switch fails; settings claim is wrong | Spike in stage 0; rebuild window fallback; test |
| Global static language races with parallel tests | Flaky tests | Keep console tests sequential; inject `LanguageState` in new tests; avoid static in formatter |
| Large mechanical rename conflicts with open branches | Merge pain | Do it as one dedicated PR after stage 0; publish the key map early |
| Existing tests hard-code German/English values | False failures | Keep values byte-identical in stage 0; update assertions only for intentional wording changes |
| CSV headers are localized | Machine consumers break | Document CSV as presentation; add separate invariant export if needed |
| French plural `many`/date/number edge cases | Wrong text | CLDR subset with explicit tests; native review |
| Future language needs categories not in the table | Wrong plurals | Fallback to `other`; add CLDR data/ICU when adding the language |
| JSON catalogs grow and slow startup | Slower start | Lazy per language, frozen dictionaries, performance smoke |
| Translators lack context | Poor translations | `meta.comment`, screenshots, glossary, PR review |
| Hardcoded text scan is noisy | CI ignored | Baseline existing debt; fail only on new occurrences; expand gradually |

Open questions for the maintainer:

1. Is single-file/self-contained publishing a release requirement? If yes, JSON
   embedded is strongly preferred; if not, ResX is still a valid fallback.
2. Is live language switching required, or is an explicit restart acceptable?
   The current settings dialog claims immediate application; confirm.
3. Do external translators need XLIFF/Crowdin/Weblate, or is a reviewed JSON PR
   enough for the first year?
4. Should CSV headers/file names remain localized, or should there be a second
   invariant machine export? Existing tests lock the localized behaviour.
5. Is French the agreed third language, and is a native reviewer available? If
   not, Dutch is the lower-effort pilot but exercises fewer plural/format rules.
6. Which regional cultures should `de`/`en`/`fr`/`nl` map to (`de-DE`,
   `en-GB`, `fr-FR`, `nl-NL`)? The answer affects dates and number separators.
7. Are report file names and PDF metadata part of the localization scope, or
   only the rendered content?
