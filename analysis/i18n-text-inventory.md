# BusChord — i18n text inventory and localization gaps

Basis: `2846c1687ab1366e2fcc55ecf8ad01a8e3d21800` (`development`, “Localize project check,
CSV headers and report”). Worktree `C:\projects\ai_work\agent-wt\i18n-inventory`, branch
`agents/i18n/inventory`. Product, tests, CI and existing reports were not modified; only
this report is committed.

Method: static regex inventory of all tracked `src/**/*.cs` (131 files) and
`src/**/*.axaml` (15 files), plus the 29 test sources and the UI harnesses. The two main
dictionaries and the catalogue dictionary were parsed with a small throwaway script
(`C:\Users\julia\AppData\Local\Temp\opencode\i18n-*.ps1`, not committed) and checked for key
counts, duplicates, empty values, placeholder parity, usage, dead keys and dynamic-only keys.
Focused verification: `dotnet run --project tests/Knx.Core.Tests -c Release` →
`ALLE CHECKS OK`; `dotnet run --project src/Knx.App.Tests -c Release` → `REPORTS OK`.

This is a **static inventory, not a complete string census**. The literal scan is heuristic:
interpolated/concatenated strings can hide embedded text, and not every exception in
`Knx.Bus`/`Knx.Core` was traced to a UI call site. No runtime UI walkthrough and no
language-switch interaction test were performed; the live-switch finding is from code reading.
Items marked “not surfaced” are listed because they are user-facing text in the product source,
not because a current screen shows them.

## 1. Verdict

The dictionary layer itself is clean: 908 entries, no duplicate keys inside a dictionary, no
empty German or English value, no placeholder mismatch, and no key referenced by source that is
missing from a product dictionary. The gaps are at the edges:

- **Three dictionaries plus two inline mechanisms** (`Strings`, `WorkbenchStrings`,
  `CatalogUiText`, `T(de,en)` in the parameter pane, `Strings.Language == "en" ? …` ternaries).
  Four key names exist in two dictionaries with different meanings.
- **155 of 862 distinct main-dictionary keys (18%) are dead** — almost all legacy main-window
  menu/ribbon/dashboard/building vocabulary left behind when the V4 workbench replaced that shell.
- **The language switch does not refresh the open shell.** XAML `{loc:Tr}`/`{loc:Wb}` resolve
  once at load and the workbench menu is built once in the constructor, so choosing English in
  Options changes only strings resolved afterwards. There is no `LanguageChanged` event and no
  subscriber to `Strings.Instance.PropertyChanged`.
- **Output formats are inconsistent:** one CSV exports a localized header with `ja/nein` body
  values, another exports a hard-coded German header, a third a hard-coded English header; the
  printable label sheet always declares `lang="de"` and falls back to the German title
  “Etiketten”. The HTML project report switches language correctly but its dates stay `dd.MM.yyyy`.
- **Core validation messages bypass localization.** `ProjectOperations` returns 51 English
  sentences that the shell writes verbatim into the German status bar; the monitor, scan and
  programming sessions add 33 English exception messages; `ProjectBackup` adds 26.
- **German is persisted as project data.** New projects and newly created ranges/devices get
  German names (`Bereich 0`, `Linie 0.0`, `Hauptbereich 1`, `Mittelbereich 1`, `Neue
  Gruppenadresse`, `Neues Gerät`) regardless of the UI language, and those names travel into
  reports, CSVs and the semantic export.

## 2. Dictionary inventory

| Dictionary | File | Keys | Duplicates inside | Empty de/en | Placeholder mismatches | Same de/en | Dead | Dynamic-only |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `Strings` (legacy + core/report/CSV) | `src/Knx.Core/Localization.cs` | 451 | 0 | 0 / 0 | 0 | 23 | 152 | 37 (shared) |
| `WorkbenchStrings` (V4 shell) | `src/Knx.App/Localization/WorkbenchStrings.cs` | 415 | 0 | 0 / 0 | 0 | 14 | 3 | — |
| `CatalogUiText` (catalogue pane) | `src/Knx.App/Services/CatalogBrowser.cs` | 42 | 0 | 0 / 0 | 0 | 1 | 0 | — |
| **Total** | | **908** | **0** | **0** | **0** | **38** | **155** | **37** |

Distinct key names across the three maps: 904 (four names collide). Main-dictionary distinct
keys: 862; referenced in `src` (directly or dynamically): 707; no reference at all: 155.
Placeholder totals: `Strings` 105/105 (de/en), `WorkbenchStrings` 93/93, `CatalogUiText` 17/17.

### 2.1 Duplicates and collisions

No duplicate key exists *within* a dictionary (`["Key"] = (…)` uses the indexer, so a duplicate
would silently overwrite; none does today). Four names exist in two dictionaries:

| Key | `Strings` | `WorkbenchStrings` | Assessment |
|---|---|---|---|
| `CommunicationObjects` | “Kommunikationsobjekte” / “Communication objects” | same | harmless duplicate, merge |
| `MenuEdit` | “Bearbeiten” / “Edit” | same | harmless duplicate, merge |
| `CompareSummary` | “{0} hinzugefügt · {1} entfernt · {2} geändert” | “Vergleich: {0} + / {1} − / {2} ~” | **divergent meaning**, rename one |
| `SearchResults` | “Treffer” / “Results” | “Suchergebnisse” / “Search results” | **divergent meaning**, rename one |

Recommended: rename the workbench keys to `CompareStatusSummary` and `PaletteResults`; fold
`CommunicationObjects`/`MenuEdit` into the surviving dictionary when the dictionaries are merged.

### 2.2 Identical German/English values (38)

23 in `Strings`, 14 in `WorkbenchStrings`, 1 in `CatalogUiText`. Almost all are correct as-is:
proper names (`Bus`, `Export`, `Start`, `Stop`, `Dashboard`, `Recorder`, `Installation`,
`Segment`), column labels that are the same word (`Name`, `Code`, `Status`, `Flags`), symbols
(`+`, `-`, `…`, `Aa`, `#{0} · {1} · DPT {2}`) and technical units. The scan log lists them
individually; they are not repeated here. No
value looks accidentally untranslated; the risk is future reviewers treating “same” as “missing”.

### 2.3 Dead keys (155)

- `WorkbenchStrings`: `ExpandAll` (line 62), `RestoreBackup` (377), `ScanLine` (236).
- `Strings`: 152 entries, essentially the pre-V4 shell: all `MenuFile*`/`MenuBus*`/`MenuExtras*`/
  `MenuCatalog*` (57–92, 263–265), `Ribbon*` (95–101), `BtnNew`…`BtnOptionsDots` (102–119),
  `Ctx*` (121–122), `TabDashboard`/`TabCheck`/`TabMonitor`/`TabRecorder`/`TabGroupAddresses`/
  `TabParameters` (125, 139–142, 305), `PanelTopology`/`PanelProperties` (143–144), `Dash*`
  (172–177), legacy monitor/filter labels (181–192), `Ux*` start cards (287–304), the whole
  `Building*` block (350–378), `LinkAdd`/`LinkRemove`/`LinkExisting`/`LinkSelect*`/
  `LinkOfflineNotice` (129–138), `CatalogInsert*`/`CatalogResultCount`/`CatalogLineFull`
  (259, 275, 280–281), `StatusNoProject` (197, only tests), `OptionsSaved` (223),
  `MsgNoSelection` (249), `WatermarkSendValue` (160), `BtnSend`/`BtnRead`/`BtnParameters`/
  `BtnDownload` (161–162, 168–169), `ColTime`/`ColSource`/`ColDevice`/`ColCode`/
  `ColDatapointType` (147, 150, 153, 186–187), `MonitorKind*` (189–192).
- `CatalogUiText` 0 dead.

Deleting them is safe after a grep for the key name, but two caveats: `StatusNoProject` is
still asserted by `tests/Knx.CatalogUi.Tests` (production code uses `UxNeedsProject`), and
`ChkKind*`/`SearchKind*`/`SearchScope*`/`Help*`/`Favorite*`/`RowDescription*` are referenced
**dynamically** (37 keys). Those 37 must stay; they are live but string-typed, so a rename is
invisible to the compiler. `BtnSave` is kept alive only by the i18n unit test
(`tests/Knx.Core.Tests:674-685`); move that assertion to a live key before deleting it.

### 2.4 Key mechanics

- `Strings.Get` catches `FormatException` and returns the unformatted text
  (`Localization.cs:42-43`). `WorkbenchStrings.Get` and `CatalogUiText.Get` do **not**
  (`WorkbenchStrings.cs:19`, `CatalogBrowser.cs:52`); a malformed translation would throw at the
  call site. Add the same guard or drop the guard in all three.
- Unknown key returns the key itself in all three maps. Nothing fails loudly; a typo in
  `{loc:Wb …}` renders `SomeKey` in the UI and no test catches it.
- `Strings` exposes `Catalogue` for tests (`Localization.cs:32`); `WorkbenchStrings` and
  `CatalogUiText` do not, so their completeness cannot be asserted the same way.
- `WorkbenchStrings` is resolved through `Knx.Strings.Language` but lives in `Knx.App`; the core
  and report projects cannot use it. That is why `Strings` still carries report/CSV keys and why
  the workbench duplicates vocabulary.
- Adding a language means editing `Available`, both dictionaries and the settings combo. Nothing
  enumerates `Available` to build the combo; it is hard-coded `Deutsch`/`English`
  (`SettingsWindow.axaml:48-49`).

## 3. Findings, prioritized

Legend — kind: **UI** (label/menu/tooltip/dialog), **status** (status bar or message dialog),
**report** (HTML/print), **CSV** (file export), **data** (written into the project and exported
later), **test-only**. Key status: `none` = literal, `keyed` = dictionary entry exists,
`dead` = key exists but unused, `inline` = per-call `T(de,en)` or language ternary.
Owner is the module that should own the key/text, not necessarily the file that renders it.

### P1 — must fix before the English UI can be called complete

| # | Location | Current text (abridged) | Kind | Key status | Recommended key / owner |
|---|---|---|---|---|---|
| P1.1 | `src/Knx.App/WorkbenchWindow.Commands.cs:275-276` | `a.Active ? "ja" : "nein"`, `a.Sending ? "ja" : "nein"` | CSV | header is keyed `CsvAssociationsHeader`, body is not | Prefer stable `1`/`0` for both columns and document them; otherwise `CsvYes`/`CsvNo`. Owner: `Knx.App` associations export |
| P1.2 | `src/Knx.Bus/TelegramRecorder.cs:83` | `Zeitstempel;Quelle;Ziel;Gruppe;APCI;Daten` | CSV | none | `CsvRecordingHeader`; make `LoadCsv` (`:216`) accept `Zeitstempel` and `Timestamp`. Owner: `Knx.Bus` recorder |
| P1.3 | `src/Knx.App/Workbench/Sessions/MonitorSession.cs:40` (export at `:352-367`) | `Timestamp,Source,Destination,Service,DPT,Value,Origin,GroupAddressId,DeviceId,Data` | CSV | none | `CsvMonitorHeader`; localize at session start or keep one stable contract and stop calling it localized. Owner: `Knx.App/Workbench` monitor |
| P1.4 | `src/Knx.App/SettingsWindow.axaml.cs:42`, `src/Knx.App/Localization/TrExtension.cs:9-10`, `src/Knx.App/WorkbenchWindow.axaml.cs:135` | Language save only sets `Strings.Language`; XAML/menu were resolved at load | UI | n/a | Add `Strings.LanguageChanged` (or `Strings.Instance.PropertyChanged` subscription) and rebuild menus/tool tabs/workbench, or reopen the shell window. Until then label the option “applies after restart”. Owner: `Knx.App/Localization` + shell |
| P1.5 | `src/Knx.Reports/LabelCreator.cs:75,81`, `src/Knx.App/WorkbenchWindow.Commands.cs:341` | `<html lang="de">`; fallback title `Etiketten` | report | none | `LabelSheetTitleDefault`, and emit the active language code; owner: `Knx.Reports` label sheet |
| P1.6 | `src/Knx.Core/ProjectOperations.cs:57-553` (51 `Fail(...)` literals) | `"Device does not exist in this project."`, `"A line needs a name."`, `"Line still contains devices; move or delete them first."`, … | status | none | Do not add 51 keys: `ProjectEditStatus` already exists (`:6-17`). Add `ProjectEditStatusText.Get(status, args)` with 9–15 messages, keep the English `Message` for logs/API. Owner: `Knx.Core/ProjectOperations` + `Localization` |
| P1.7 | `src/Knx.App/Workbench/Sessions/*.cs` (33 exception/message literals: Programming 14, Scan 7, Monitor 9, WorkbenchSessions 3) | `"No project is open."`, `"The interface is occupied by a conflict…"`, `"A scan is already running."`, `"Cancelled before execution; no success recorded."` | status | none | `SessionNoProject`, `SessionDeviceMissing`, `SessionBusy`, `SessionCancelled`, `SessionScanRunning`, …; return a status enum where the same condition repeats. Owner: `Knx.App/Workbench` sessions |
| P1.8 | `src/Knx.App/MainWindowViewModel.cs:843,847` | `$"Download {p.Stage} {p.Percent}% - {p.Message}"`, `"Download OK"` | status | none | `DownloadProgress`/`DownloadCompleted` + localized `DownloadStage*` names; owner: `Knx.App` + `Knx.Bus/DownloadSession` |

### P2 — wrong-language output in a secondary workflow or persisted data

| # | Location | Current text (abridged) | Kind | Key status | Recommended key / owner |
|---|---|---|---|---|---|
| P2.1 | `MainWindowViewModel.cs:1099,1111`; `ProjectOperations.cs:710,720,729`; `Wizards.cs:63-64,72` | `Hauptbereich {n}`, `Mittelbereich {a}/{m}`, `Gruppen` written into `GroupRange.Name` | data | none | `RangeMainName`, `RangeMiddleName`, `RangeRootName` at creation time; document that the stored name follows the creation language and is user data afterwards. Owner: `Knx.Core` + `Knx.App` VM |
| P2.2 | `MainWindowViewModel.cs:408,542`; `Wizards.cs:9,49,56` | `"Neue Gruppenadresse"`, `"Neues Gerät"`, `"Neues Projekt"`, `"Bereich {a}"`, `"Linie {a}.{l}"` | data | none | `DefaultGroupAddressName`, `DefaultDeviceName`, `DefaultProjectName`, `DefaultAreaName`, `DefaultLineName`; same data-language caveat. Owner: `Knx.Core/Wizards` + `Knx.App` VM |
| P2.3 | `src/Knx.Core/ProjectCheck.cs:89,97,101,106,121-156,234` | `Bereich {n}`, `Linie {a}.{l}`, `Segment {a}.{l}.{s}`, `Gebäudeknoten`/`Gerät` as `Path` | report/CSV | message keys exist, path has none | The test deliberately keeps `Path` language-invariant (`tests/Knx.Core.Tests:730-752`). Keep the stable path for identity/CSV, add a localized `PathText` for the UI/report column. Owner: `Knx.Core/ProjectCheck` |
| P2.4 | `src/Knx.Core/Dashboard.cs:59` | `$"Bereich {area.Address} (direkt)"` | report/API | none | `DashDirectDevices`; owner: `Knx.Core/Dashboard` |
| P2.5 | `src/Knx.App/CompareWindow.axaml.cs:23` | `({report.GeneratedAt:dd.MM.yyyy HH:mm})` | UI | none | `CultureInfo.CurrentCulture` with `g`, or a keyed pattern per language. Owner: `Knx.App` compare |
| P2.6 | `src/Knx.Reports/ProjectReport.cs:51,139` | `dd.MM.yyyy HH:mm`, `dd.MM.yyyy` | report | none | Same as P2.5; also update `src/Knx.App.Tests/Program.cs:105` which currently locks `18.10.2026 12:00`. Owner: `Knx.Reports` |
| P2.7 | `src/Knx.Reports/LabelCreator.cs:49` | `Adresse;Titel;Untertitel;Zusatz` | CSV | none | `LabelCsvHeader` (only reachable via MCP today, not wired into the UI); owner: `Knx.Reports` |
| P2.8 | `src/Knx.App/Services/CatalogBrowser.cs:45-117` | 42-key `CatalogUiText` dictionary | UI | separate dictionary | Fold into `WorkbenchStrings` (same language switch, one catalogue for tests); owner: `Knx.App/Localization` |
| P2.9 | `src/Knx.App/Views/ParameterPane.axaml.cs:69,98-124` | 7 `T("…","…")` calls, e.g. “Schreibgeschützt”, “Änderungen prüfen” | UI | inline | Move to `WorkbenchStrings`; delete `T`; owner: `Knx.App/Parameter` |
| P2.10 | `src/Knx.App/Services/ParameterEditingSession.cs:74-76,240-265,337,364` | 6 language ternaries, e.g. “Nicht übernommene Parameterentwürfe”, “Projekt gewechselt…”, “Allgemein” | UI | inline | Keys + a small plural helper; owner: `Knx.App/Parameter` |
| P2.11 | `src/Knx.App/Localization/WorkbenchStrings.cs:90` | `["Overview"] = ("Prüfung", "Overview")` | UI | keyed, semantically mismatched | Rename to `CheckOverview` so “Prüfung/Overview” cannot be confused with `ProjectCheck`/`Problems`; owner: `Localization` |
| P2.12 | `src/Knx.App/WorkbenchWindow.axaml.cs:485` | `throw new ArgumentException("Work item no longer exists.")` | status/dialog | `CompanionEmpty` exists (`WorkbenchStrings.cs:158`) but is unused here | Use `CompanionEmpty`; owner: `Knx.App` shell |
| P2.13 | `src/Knx.Bus/DownloadSession.cs:43-86` | `descriptor lesen`, `memory write nicht bestätigt …`, `Neustart`, `Download abgeschlossen` | status | none | `DownloadStage*` keys at the bus boundary or map `DownloadStage` in the UI; owner: `Knx.Bus` |
| P2.14 | `src/Knx.Core/ProjectBackup.cs` (26 English `throw` literals, e.g. `:37,39,129,141,165`) | `A project path is required.`, `Backup '…' is corrupt: SHA-256 does not match its manifest.` | status/dialog | none | Catch at the UI boundary and map a small `BackupError` enum; do not translate every exception. Owner: `Knx.Core/ProjectBackup` + shell |
| P2.15 | `src/Knx.Core/Wizards.cs:104` | `Zeile {n}: '{value}' ist keine Gruppenadresse.` | API/log | none | `ImportLineInvalid` if the import is exposed in the UI; owner: `Knx.Core/Wizards` |

### P3 — consistency, dead weight, future locales

| # | Location | Issue | Kind | Key status | Recommendation / owner |
|---|---|---|---|---|---|
| P3.1 | both main dictionaries | 155 dead keys (18% of main keys) | — | dead | Delete after grep; start with the legacy `Menu*`/`Ribbon*`/`Dash*`/`Building*` blocks. Owner: `Localization` |
| P3.2 | `CompareSummary`, `SearchResults` | same key name, different text in two maps | — | collision | Rename before merging. Owner: `Localization` |
| P3.3 | `WorkbenchStrings.cs:14-19`, `CatalogBrowser.cs:47-53` | no `FormatException` guard unlike `Strings.Get` | — | — | Share one lookup implementation. Owner: `Localization` |
| P3.4 | 10 keys with `(e)`/`(s)` plus `DraftText`/`ValidationSummary` | `{0} Gerät(e)`, `{0} link(s)`, `1 …` / `n …` hand-written | UI | keyed/inline | Small plural selector (`n == 1 ? singular : plural`) or `Plural` entries; no ICU needed for de/en. Owner: `Localization` |
| P3.5 | whole app | no `FlowDirection`, no RTL metadata | — | — | Only needed before an RTL language; add a language descriptor (code, autonym, flow direction) instead of scattering checks. Owner: `Knx.App/Localization` |
| P3.6 | `WorkbenchWindow.axaml:39,45,97,99` | `BUSCHORD`, `OFFLINE`, `DPT`, `Flags` literals | UI | none | Brand and `DPT`/`Flags` are not translated; `OFFLINE` should become `StatusOffline` (“Offline” is the same word, but the status must follow connection state). Owner: `Knx.App` |
| P3.7 | `SettingsWindow.axaml:48-49` | `Deutsch`, `English` autonyms | UI | none | Intentional (language names stay in their language); drive the list from `Available` so a third language cannot be forgotten. Owner: `Knx.App` |
| P3.8 | `src/Knx.Core/Owl/KimCheck.cs:25-62` | 6 German-only K001–K005 findings | report/API | none | Not surfaced in the app today. Localize when KIM findings get a window; owner: `Knx.Core/Owl` |
| P3.9 | `src/Knx.Bus/TelegramRecorder.cs:32` | `→ Gruppe` / `→ Gerät` in `ToDisplayLine` | — | none | Method has no caller in `src`; delete it or key it with the recorder. Owner: `Knx.Bus` |
| P3.10 | `src/Knx.Bus/Diagnostics.cs:78-108` | `descriptor type`, `unknown mask`, `System B (BCU 2)` | UI if surfaced | none | Protocol names stay English; only wrap the sentence around them. Owner: `Knx.Bus` |
| P3.11 | `src/Knx.App/Program.cs:53-54` | `Unbekannter Screen '…'. Erlaubt: …` | CLI | none | Developer CLI; leave German or make it English, but not both. Owner: `Knx.App` |
| P3.12 | `src/Knx.App/WorkbenchWindow.axaml.cs:416` | demo value `0 (stop)` | demo | none | `DemoValueStop` if the demo should read naturally in German; owner: `Knx.App` |
| P3.13 | `src/Knx.Core/Localization.cs:160` | `Wert senden (z. B. 1, 21.5, 0/1)` uses a decimal point in German | UI | keyed | Use `21,5` in German or a culture-neutral example (`1`, `0/1`, `75 %`). Owner: `Localization` |
| P3.14 | `WorkbenchWindow.Commands.cs:30-44,172,265,288,342` | suggested file names `gruppenadressen.csv`, `geraete.csv`, `topologie.csv`, `zuordnungen.csv`, `semantik.ttl`, `telegramme.csv`, `demo-projekt.json` | UI | none | File names are user-visible in the save dialog; localize or use neutral names. Owner: `Knx.App` |
| P3.15 | `src/Knx.Core/ProjectQuery.cs:166,178` | `Linie … ist voll (255 Geräte).`, `Gruppenbereich … ist voll.` | status | none | Map to `CatalogLineFull`/`NoFreeAddress` at the caller; owner: `Knx.Core/ProjectQuery` |
| P3.16 | `src/Knx.App/MainWindowViewModel.cs:621` | internal exception `"No project loaded"` | status if hit | `NoProjectLoaded` exists | Use the key or remove the unreachable path; owner: `Knx.App` VM |
| P3.17 | `src/Knx.App/Screenshot.cs:78-84`, `WorkbenchCheck.cs:38-61`, `Smoke.cs` | German test names/data | test-only | none | Test harness only; no product impact. Owner: tests |
| P3.18 | `TelegramRecorder.cs:158-159`, `LabelCreator.cs:161`, `ProjectComparer.cs:47-212` | ETS CSV header, merge-field header, compare paths (`Project/Name`, `GroupAddress/1/1/1`) | CSV/API | none | **Do not translate**: stable ETS/mail-merge/identity contracts. Document them in the localization guide. Owner: `Knx.Bus`, `Knx.Reports`, `Knx.ProjectCompare` |
| P3.19 | `Localization.cs:427,429` | CSV separator is always `;` (`ReportCsvHeader`, `CompareCsvHeader`) | CSV | keyed | Excel locale decides the list separator; either document “semicolon CSV” or add a delimiter option. Owner: `Knx.Reports` |

### Counts by kind (identified rows, not exhaustive)

| Kind | Sites | Notes |
|---|---:|---|
| UI labels/menus/tooltips/dialogs | ~18 | dictionaries are otherwise complete; literals concentrate in the parameter pane, demo values and brand/status |
| Status/error text | 110+ | 51 `ProjectOperations` + 33 sessions + 26 `ProjectBackup`; most are English in the German UI |
| Report/HTML/label sheet | 8 | dates, `lang`, default title, label CSV header |
| CSV headers/body | 7 | three hard-coded headers, `ja/nein` body, ETS/merge contracts |
| Project data (names persisted) | 12 | ranges, areas, lines, default GA/device/project names |
| Test-only | 3 files | harness names and synthetic data |

## 4. Dynamic data vs. translatable labels

The model must keep user/project data out of the translation layer:

- **Never translate at render time:** project/installation/area/line/segment/device/group-address
  names, product and application names, comments, room paths, DPT codes, KNX addresses,
  manufacturer/order IDs, file paths, automation IDs.
- **Translate labels around the data:** column headers, unit words, “Project: {0}”, “Device {0}
  moved”, severity/type/modification words, empty states.
- **Persisted defaults are data, not labels.** `Bereich 0`, `Linie 0.0`, `Hauptbereich 1`,
  `Mittelbereich 1`, `Neue Gruppenadresse`, `Neues Gerät` are created once and then belong to the
  project. Localizing them at creation is fine (the user sees their language), but changing the UI
  language later must not rename them. Document this rule; do not “fix” it by translating stored
  names on display.
- **German path tokens in findings** are a special case: `ProjectCheck.Path` is deliberately
  language-invariant (`tests/Knx.Core.Tests:730-752`). That is good for machine identity and CSV
  joins, but it leaks German into the English “Object” column. Keep `Path` stable and add a
  separate localized `PathText` for display; never change `Path` with the language.
- **CSV body values:** `ja/nein` in `AssociationsCsv` is the clearest defect. Recommended: emit
  `1/0` for `Active`/`Sending` (unambiguous, locale-free, stable for consumers), keep the localized
  header only for human-facing exports, or localize both header and values. Do not leave a
  localized header over German-only body values.
- **Label sheet:** `SheetTitle` is user data (project name) when set; the fallback must be a key.
  The HTML `lang` attribute must follow `Strings.Language`, not `de`.
- **Stable contracts:** the ETS 13-column recording CSV (`TelegramRecorder.ToEtsCsv`), the
  mail-merge data source header (`LabelMergeSource.ToDataSource`) and the compare `Path` values
  are interoperability/identity contracts. Keep them English/stable and list them in a
  “do not translate” section of the localization guide.

## 5. Culture-specific formatting, plurals, RTL

| Topic | Current state | Gap / recommendation |
|---|---|---|
| Dates in UI | `dd.MM.yyyy HH:mm` in compare and report; ISO in backup list and monitor | German order in English output. Use `CurrentCulture` `g`/`d` or a keyed pattern; update the test that locks `18.10.2026 12:00`. |
| Numbers/decimals | DPT decoding uses invariant `F0`; demo watermark says `21.5` in German | Hardware/DPT values stay invariant; user-facing examples should follow the language (`21,5`). |
| Units | `%`, `°C`, `Bit`/`bit` via keys; `KiB` hard-coded | Units are fine as symbols; `KiB`/`byte(s)` should be keyed if shown in dialogs. |
| Plurals | German `(e)`/`(s)` constructs, English `(s)`, two hand-written 0/1/n switches | Works for de/en, not for a third language. Add a tiny singular/plural selector, no framework. |
| Sorting/casing | `ToUpperInvariant` for DPT, `Ordinal` for IDs, default `OrderBy` for names | Correct: identifiers ordinal, user-visible names current culture. No change. |
| RTL | no `FlowDirection`, no RTL metadata | Not needed for de/en; add a language descriptor before ar/he/fa. |

## 6. Test coverage

### Existing and relevant

| Test | What it proves |
|---|---|
| `tests/Knx.Core.Tests/Program.cs:671-690` | `Strings` fallback, formatting, unknown language → German |
| `tests/Knx.Core.Tests/Program.cs:692-762` | `ProjectCheck` messages de/en, codes/paths/severity invariant, apostrophes survive |
| `tests/Knx.Core.Tests/Program.cs:764-795` | `Chk*`/`Csv*`/`Report*` keys non-empty and placeholders formattable in both languages |
| `src/Knx.App.Tests/Program.cs:40-52` | CSV headers follow the UI language |
| `src/Knx.App.Tests/Program.cs:158-181` | report headings, empty states and `<html lang>` de/en; **locks** the German date format at `:105` |
| `tests/Knx.Workbench.Tests/Program.cs:664-678` | associations CSV header de/en and 12-column shape |
| `tests/Knx.CatalogUi.Tests/Program.cs:377-390` | `StatusNoProject` de/en |
| `tests/Knx.Shell.Tests/Program.cs:48`, `tests/Knx.Docking.Tests/Program.cs:34` | shell/docking flows forced to English |
| `.github/workflows/ci.yml:134-136` | workbench self-test in de and en |

### Missing coverage

1. **No completeness/parity test for `WorkbenchStrings` (415) or `CatalogUiText` (42).** Only
   `Strings.Catalogue` is enumerated; the workbench has no `Catalogue` property.
2. **No test that every `{loc:Tr}`/`{loc:Wb}` key resolves.** A typo renders the raw key and passes.
3. **No test for CSV body values.** The header test passes while `Active` says `ja` under an
   English header.
4. **No test for runtime language switching.** A shell test that opens Options, selects English and
   asserts a menu header would fail today.
5. **No de/en test for `ProjectOperations`, session errors, download status, backup errors or label
   sheet.** These are exactly the P1/P2 literal hotspots.
6. **The English workbench self-test cannot detect German literals** because it looks expectations
   up through `WorkbenchStrings.Get`; a German literal that is not a key is invisible.
7. **No duplicate/collision test across dictionaries** (`CompareSummary`, `SearchResults`).
8. **No date/format assertion for English**; the only date assertion is the German one.
9. **No RTL test** (acceptable for de/en, listed for completeness).

Recommended minimum check (one file, no framework):

```csharp
// tests/Knx.Core.Tests — extend the i18n block
foreach (var (name, map) in new[] { ("strings", Strings.Catalogue), ("workbench", WorkbenchStrings.Catalogue) })
{
    // non-empty, same placeholder set, no duplicate names across maps
}
// plus one CSV contract test per exporter:
// header language == Strings.Language, body booleans follow the same language (or are 1/0)
```

Add `WorkbenchStrings.Catalogue` and `CatalogUiText.Catalogue` (or make all three implement one
`ITextCatalogue`) so the existing placeholder loop can cover them without new test infrastructure.

## 7. Migration order

1. **Freeze the baseline.** Add `Catalogue` to `WorkbenchStrings` and `CatalogUiText`; extend the
   `Knx.Core.Tests` i18n block to assert non-empty values, placeholder parity, no duplicate names
   across maps and no dead key references. This makes every later step measurable.
2. **Fix the language switch.** Add a `LanguageChanged` event (or subscribe to
   `Strings.Instance.PropertyChanged`) and rebuild the workbench menu/tool tabs; if a full rebuild
   is too invasive, reopen the workbench window on save and label the option accordingly. Verify
   with one shell test.
3. **Unify the dictionaries.** Fold `CatalogUiText` and the `ParameterPane`/`ParameterEditingSession`
   inline strings into `WorkbenchStrings`; rename the two colliding keys; give all three the same
   `FormatException` guard and `Catalogue` accessor. After this, there is one place per key.
4. **Fix output formats (P1.1–P1.3, P1.5).** Decide the contract per file: `AssociationsCsv` →
   `1/0`; recorder CSV → localized header with a tolerant reader; monitor CSV → localized header or
   documented stable header; label sheet → active `lang` + keyed fallback title. Add one test per
   exporter asserting header and body agree.
5. **Localize core status (P1.6–P1.8).** Map `ProjectEditStatus` to 9–15 keys with arguments;
   add a `SessionError`/`SessionStatus` vocabulary for the 33 session messages; key the download
   stage/progress. Keep the English `Message` on the result objects for logs and MCP.
6. **Separate data from labels (P2.1–P2.3).** Create default range/area/line/device/GA names through
   keys at creation, and add a localized `PathText` for `CheckFinding` while keeping `Path` stable.
   Document “stored names follow creation language; display labels follow UI language”.
7. **Culture formats and plurals (P2.5–P2.6, P3.4).** Switch compare/report dates to the current
   culture or a keyed pattern, update the German date assertion, add a singular/plural selector for
   the 10 `(e)`/`(s)` keys and the two inline counters.
8. **Delete dead weight (P3.1–P3.2).** Remove the 155 dead keys (start with the legacy menu/ribbon/
   dashboard/building blocks), then run the new catalogue test to prove nothing referenced them.
9. **Optional language readiness (P3.5).** Add a language descriptor (code, autonym, flow direction)
   and build the settings combo from `Available`. Only needed before a third language or RTL.

Order rationale: steps 1–2 make the rest testable; step 3 removes the duplication that would
otherwise force every fix three times; steps 4–7 are the visible gaps; steps 8–9 are cleanup and
future-proofing. The P1 list is intentionally short: it covers text a user sees in a normal
English/German session, not every exception in the bus stack.

## 8. Verification log

- `git -C … rev-parse HEAD` → `2846c1687ab1366e2fcc55ecf8ad01a8e3d21800`; worktree branch
  `agents/i18n/inventory`; `git status` clean before the report was written.
- Dictionary parse: 451 + 415 + 42 = 908 entries; 0 duplicates inside each map; 0 empty values;
  0 placeholder mismatches; 38 identical de/en; 4 cross-map name collisions.
- Usage scan: 708 distinct keys referenced directly in `src`; 37 keys referenced only as string
  literals passed to helpers; 155 keys with no reference; 0 keys referenced by source but missing
  from a product map (the 38 “missing” hits are `CatalogUiText` keys resolved by its local map).
- Literal scan: 131 `.cs` + 15 `.axaml` under `src`; 11 literal attributes in XAML, of which 6 are
  brand/technical (`BUSCHORD`, `OFFLINE`, `DPT`, `Flags`, `BusChord` ×2), 2 are language autonyms,
  3 are sample product references. No literal `Text`/`Title`/`Content` attribute with a
  translatable sentence remains in XAML.
- Executed: `dotnet run --project tests/Knx.Core.Tests -c Release` → `ALLE CHECKS OK`;
  `dotnet run --project src/Knx.App.Tests -c Release` → `REPORTS OK`.
- Not executed: GUI walkthrough, runtime language switch, CSV open in Excel, RTL rendering.

## 9. Limitations

- The 155 dead-key count assumes dynamic references are only the 37 quoted literals found by the
  scan. Keys reached through reflection, serialized configuration or future plugins are not
  represented.
- The literal tables list the highest-value sites, not every German/English word in the bus and
  archive layers. `ProjectBackup` alone has 26 English exception messages; only the ones that
  surface through `BackupFailed`/`RestoreDone` were traced to the UI.
- “User-visible” is based on call sites in `src/Knx.App`, not on a running session. Some paths
  (KIM check, diagnostics, generic `GroupMonitor.SaveCsv`) are currently unreachable from the V4
  shell and are marked accordingly.
- Counts for “status/error text” overlap: one `ProjectOperations` message can be both a status
  and a dialog detail; the table counts sites, not distinct rendered strings.
- The report is intentionally in English to match the repository’s analysis documents; product
  text quoted in it is reproduced as-is.
