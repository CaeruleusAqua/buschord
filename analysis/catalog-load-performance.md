# Catalogue load performance

Investigation only — no product code, tests, CI or packages were changed. Measurements were
taken on a throwaway harness outside the repository; no catalogue content is committed.

- Base: `125c17a` (`development`), branch `agents/perf/catalog-load`
- Date: 2026-10-21
- Machine: Windows 11 Pro 26200, Intel Core i7-1370P (14 cores / 20 threads, 1.9 GHz base),
  64 GB RAM, .NET SDK 9.0.302, Release build
- Method: `dotnet build ets6.sln -c Release`, then a console harness referencing `Knx.Core` and
  `Knx.App` and calling the real loader/search/parameter APIs. Each focused measurement is the
  median of 3 repetitions; the whole suite was repeated several times. The machine also ran
  other agent builds, so absolute numbers vary up to roughly 2x between runs; the shares between
  stages are stable. Treat the numbers as orders of magnitude and as a baseline to re-measure on
  an idle machine, not as release gates.

## Summary

1. **The first search keystroke is the worst single freeze.** `CatalogPane.ApplyFilters` runs
   synchronously on the UI thread for every `TextChanged`, and `CatalogBrowser.Filter` resolves
   the application program for every product. On a split archive that lazily parses every
   application-program XML part: **2.4–4.8 s and ~559 MB of managed allocations** for one
   4-program catalogue (120.7 MB uncompressed XML). Repeat calls are ~0.6 ms.
2. **Nothing is cached across processes.** Every start and every Reload re-parses the whole
   library: 124 catalogues / 401.5 MB took **1.2–1.9 s warm and up to 11.2 s on the first run
   after copying the library** (cold file cache + JIT), with the first file ready after only
   14–97 ms. Nothing is shown until the last file is parsed. No disk index exists.
3. **One program part costs ~0.6–1.9 s and ~140 MB transient.** The parse builds a duplicate XML
   tree, rewrites translations, then runs `Ksxml.Parse` a second time. Metadata needed for
   search (name, com objects, DPT) is not separated from the full parameter model.
4. **Warm search still scans every product and every com object per keystroke**:
   **38 ms and 20.7 MB per keystroke** for 1,000 products, on the UI thread.
5. **Opening a catalogue device's parameters clones the model through JSON**:
   **276–387 ms and ~46 MB** on top of any program parse, then serialises a draft signature again.

The split-archive loader already has the right idea (programs are lazy). The problem is that the
lazy parse is triggered on the UI thread by browsing/searching, that the metadata parse is far
more expensive than the metadata actually needed, and that none of the work survives a restart.

## 1. Scope and method

### 1.1 Code paths traced

| Area | Files |
|---|---|
| Loader / archive | `src/Knx.Core/Catalog/KnxProductCatalog.cs`, `CatalogStore.cs`, `CatalogModel.cs` |
| Session cache | `src/Knx.App/Services/CatalogLibrary.cs` |
| Browser / search | `src/Knx.App/Services/CatalogBrowser.cs` |
| Pane | `src/Knx.App/Views/CatalogPane.axaml(.cs)` |
| App startup | `src/Knx.App/App.axaml.cs`, `Program.cs`, `WorkbenchWindow.axaml.cs` |
| Parameter use | `src/Knx.App/MainWindowViewModel.cs`, `Services/ParameterEditingSession.cs` |
| KSXML | `src/Knx.Core/Ksxml.cs` |

### 1.2 Samples

Local samples already present under `%TEMP%\opencode` (used read-only; only aggregate metrics are
reported here):

| Sample | File size | ZIP entries | XML entries | Uncompressed XML | Model |
|---|---:|---:|---:|---:|---|
| Split archive A (vendor layout with per-program parts) | 6,386,017 B | 40 | 8 | 120,737,168 B | 4 products, 4 programs (lazy), 1 section, 26 images |
| Split archive B | 403,776 B | 7 | 5 | 7,480,022 B | 1 product, 1 program (lazy) |

One program part of archive A: 29,893,083 B XML, 30,920 elements with `Id`, 101,366
`Translation` elements, **536 parameter types, 2,275 parameter instances, 1,396 com objects**.

Synthetic corpora (generated in `%TEMP%`, not committed):

| Corpus | XML | Zip | Model |
|---|---:|---:|---|
| Monolithic `mono-1000` | 3,792,272 B | 292,595 B | 1,000 products, 100 programs, 20,000 parameters |
| Monolithic `mono-3000` | 13,000,822 B | 1,286,210 B | 3,000 products, 200 programs, 80,000 parameters |
| Scale library `lib-124` | — | 401.5 MB total | 124 copies of the two split samples (62 A + 62 B), 310 products |

`lib-124` is not the user's library; it matches its recorded shape (124 files, ~430 MB, see
`98f8bdf` on `development`, plan section H5). It is used to measure start-up and Reload at the
real library scale with synthetic content.

### 1.3 What "cold" and "warm" mean here

- **Cold**: first use in a fresh process, right after the library was created/copied — JIT plus
  cold file cache.
- **Warm**: fresh process, OS file cache populated. The product has no other warm path: there is
  no disk cache, so "warm start" still re-parses every file.

## 2. Measured baseline

### 2.1 Opening a split catalogue (eager phase)

`KnxProductCatalog.Open` reads only the archive directory, `knx_master.xml`, `Hardware.xml` and
`Catalog.xml`; application programs stay lazy (`KnxProductCatalog.cs:80-84`, `125-170`).

| Measurement | Result |
|---|---|
| Open archive A, 6 repetitions | 35–38 ms after JIT; 68 ms first call; 0.5 MB allocated |
| Open archive A (focused runs) | 22–87 ms median depending on machine load |
| `knx_master.xml` pass 1 (`PickMainEntry` → `ContainsCatalogueContent`) | 26.1 ms, 1,076,021 B decompressed |
| `knx_master.xml` pass 2 (`ReadMasterNames`) | 17.7 ms, 1,076,021 B decompressed |
| `Hardware.xml` + `Catalog.xml` parse | 2.9 ms + 0.3 ms |
| LoadAll, 1 × archive A | 7.7–15.9 ms |
| LoadAll, 20 × archive B | 79–152 ms |

The master file is decompressed and scanned **twice per catalogue**: once to decide whether the
archive has a monolithic main part, once to read manufacturer names
(`KnxProductCatalog.cs:81-83`, `326-339`, `655-669`). On archive A that is ~44 ms of a ~35–50 ms
eager open; on the 124-file library it is a substantial part of the 1.2–1.9 s.

### 2.2 Lazy program part

First use of one program part (`FindProgramFor` → `FindProgramById` → `LoadPart`,
`KnxProductCatalog.cs:291-300`, `237-262`):

| Measurement (archive A, ~30 MB XML part) | Result |
|---|---|
| First touch, fresh catalogue | 596–1,931 ms (median across runs 596/677/843/932/1,047/1,103/1,451/1,672/1,931) |
| Allocated per first touch | **140.0–140.1 MB** |
| Second call for the same product | 0.1 ms, 0.2 KB |
| Retained model after all 4 parts | +13.8 MB |
| Peak retained process memory after all 4 parts | 222–250 MB (harness process) |

Stage decomposition of one part (median of 3; ranges across runs):

| Stage | Time | Allocated |
|---|---:|---:|
| Decompress ZIP entry | 29.5–68.8 ms | 64.1 MB |
| `XDocument.Load` of the 30 MB part | 186.9–892.5 ms | 72.7 MB |
| `new XElement(ApplicationProgram)` deep copy (`KnxProductCatalog.cs:520`) | 29.2–342.0 ms | 13.6–40.9 MB |
| `Ksxml.Parse(new XDocument(copy))` (`:531`, `Ksxml.cs:23-56`) | 73.0–184.4 ms | 4.1–45.1 MB |
| Translation rewrite over 30,920 declarations (`:520-530`) | 24.7–72.7 ms | 15.0 MB |
| `ReadTranslations` replica (`:375-400`) | 66.0–137.7 ms | 38.1 MB |

The product path loads the XML twice conceptually: once as `XDocument`, once as a full `Ksxml`
model built from a **copy** of the subtree. Only a small part of that model (program name,
number/version, com objects, DPT) is needed to list or search products.

### 2.3 Search in the pane

`CatalogBrowser.Filter` is called from `CatalogPane.ApplyFilters`
(`CatalogPane.axaml.cs:412-428`) on every `TextChanged` (`:391`) and every section change
(`:392`). `Matches` calls `catalog.FindProgramFor(product)` for every product whose plain text
fields do not match (`CatalogBrowser.cs:159-182`, call at `:169`).

| Query on archive A (4 products, 4 programs) | Result |
|---|---|
| First query that matches only product fields | 1–2 ms (no program load) |
| First query that must resolve programs, e.g. a no-match or DPT query | **2.37–4.83 s**, **559 MB allocated** |
| Repeat of the same query after parts are loaded | 0.3–0.8 ms |

Because this runs on the UI thread, the pane is frozen for the whole first query. `Bind` also
selects the first catalog and first product (`CatalogPane.axaml.cs:208-211`), which synchronously
calls `ShowDetails` → `FindProgramFor` (`:443`, `:453`, `:569-571`); **opening the pane therefore
pays one program parse even before the user types**. `Filter` receives the selected catalogue
(`:417`), so the cost is paid once per catalogue the user visits, not for all 124 at once — but
every first visit to a catalogue pays it again in the same process.

### 2.4 Library load and Reload

`CatalogStore.LoadAll` parses every file sequentially and only returns when the last one is done
(`CatalogStore.cs:55-77`). `CatalogLibrary.LoadAsync` publishes a single snapshot at the end
(`CatalogLibrary.cs:166-184`).

| Library | Time | Notes |
|---|---:|---|
| 124 files / 401.5 MB, first run after copy (fresh process) | **11.2 s** | first file done at 385 ms, median file 81 ms; JIT + cold file cache |
| Same, fresh process with warm file cache | **1.92 s** | first file done at 97 ms, median file 15 ms |
| Same, 2nd/3rd LoadAll in-process | 1.45 s / 1.19 s | no cache; just re-parse |
| `CatalogLibrary.GetAsync` (124 files, warm) | 697–1,718 ms | background thread; pane waits |
| `CatalogLibrary.ReloadAsync` (124 files) | 813–1,566 ms | full re-parse |
| `CatalogLibrary.GetAsync` after a snapshot exists | 0.01–0.08 ms | only this path is cached |
| 10 × monolithic `mono-1000` | 1.72–3.21 s, 526.5 MB allocated | everything parsed eagerly |
| 1 × monolithic `mono-1000` | 147–334 ms, 52.6 MB | — |
| 1 × monolithic `mono-3000` | 557–891 ms, 176.2 MB | — |

Extrapolation, clearly labelled: 124 monolithic catalogues of the `mono-1000` size would parse
for roughly 21–40 s; a single 88.5 MB monolithic file can be several seconds and >1 GB transient.

### 2.5 Warm search scans

| Query over `mono-1000` (1,000 products, 100 programs) | Result |
|---|---:|
| No-match query (walks all products and 6,400 com objects) | **37.7–38.6 ms, 20.7 MB per keystroke** |
| DPT query that hits an early com object | 1.7–3.3 ms |

`Matches` allocates a fresh `string[]` per product and per com object and calls
`ProjectWizard.NormaliseDpt` per com object (`CatalogBrowser.cs:161-181`, `185-191`). At 1,000
products this is a visible per-keystroke stall; at 10,000 products it will be worse than the
current UI budget.

### 2.6 Opening parameters

`ParameterEditingSession.Load` → `MainWindowViewModel.LoadParametersFor`
(`ParameterEditingSession.cs:175`, `MainWindowViewModel.cs:656-687`):

| Measurement (archive A program: 536 types, 2,275 instances, 1,396 com objects) | Result |
|---|---:|
| JSON clone of types + instances (`MainWindowViewModel.cs:677-684`) | **276–387 ms, 46.4 MB** |
| Draft signature serialisation (`ParameterEditingSession.cs:177`) | additional full pass, not separately timed |
| Program parse on first use (if not yet loaded) | +0.6–1.9 s |

`_catalogPrograms` caches the resolved program per product ref (`MainWindowViewModel.cs:709-738`),
but not its cloned parameter model.

### 2.7 Memory

- Eager library model for 124 split catalogues: 9.9 MB managed — small.
- One fully parsed archive A (4 programs): +13.8 MB retained, 140 MB transient per part,
  559 MB transient for all four.
- `_loadedParts` / `Programs` are never evicted (`KnxProductCatalog.cs:30`, `291-300`); every
  catalog the user searches keeps its full programs for the process lifetime.

## 3. Top bottlenecks

### B1 — Search parses every program part on the UI thread (worst freeze)

- Evidence: `CatalogPane.axaml.cs:391` (`TextChanged`) → `:412-428` (`ApplyFilters`, synchronous)
  → `CatalogBrowser.cs:169` (`FindProgramFor` per product) → `KnxProductCatalog.cs:291-300`
  (`FindProgramById`) → `:297-298` (`LoadPart`) → `:237-262` (`XDocument.Load` + `Parse`).
- Measured: 2.4–4.8 s, 559 MB for four 30 MB parts; 0.6–1.9 s and 140 MB for the first part.
- The pane's own first selection triggers the same path (`Bind` → `ShowDetails`), so even a user
  who never types waits for one part after the library finishes loading.

### B2 — No persistent index: every start and every Reload re-parses the library

- Evidence: `CatalogStore.cs:55-77` (sequential `Open` per file, no cache); `CatalogLibrary.cs:79-106`
  (`ReloadAsync` always starts a new `LoadAsync`); `CatalogLibrary.cs:166-184` (snapshot only at
  the end); no cache/index code in `Catalog/*` (only an in-memory snapshot, `CatalogLibrary.cs:41`).
- Measured: 124 files / 401.5 MB → 1.2–1.9 s warm, 11.2 s cold, every start; Reload 0.8–1.6 s;
  first file ready after 14–97 ms but nothing is published until the last one.
- Monolithic catalogues make this much worse: 10 × `mono-1000` = 1.7–3.2 s and 526 MB allocated,
  extrapolating to 21–40 s for 124 files of that shape.

### B3 — A program parse builds far more than the metadata that is used

- Evidence: `KnxProductCatalog.cs:375-400` (`ReadTranslations` walks `Ancestors()` per translation),
  `:499-558` (`AddPrograms`: duplicate `XElement` at `:520`, translation rewrite `:521-530`,
  `Ksxml.Parse` at `:531`, com objects `:540-554`).
- Measured: 0.6–1.9 s and 140 MB per part; stages above. The duplicate tree and the second KSXML
  traversal account for roughly half of the CPU after `XDocument.Load`, and for most of the
  transient allocation.

### B4 — Warm search rescans the whole catalogue on every keystroke

- Evidence: `CatalogBrowser.cs:150-191`; array allocations per product/com object and
  `NormaliseDpt` per com object.
- Measured: 37.7–38.6 ms and 20.7 MB per keystroke for 1,000 products; 10,000 products will not
  fit a 16 ms frame.

### B5 — Parameter open clones and re-serialises the model

- Evidence: `MainWindowViewModel.cs:677-684` (JSON round trip), `ParameterEditingSession.cs:177`
  (another JSON serialisation for the draft signature).
- Measured: 276–387 ms and 46.4 MB per device open, plus the program parse if cold.
- The catalogue-loaded event re-runs the same parameter load for the selected device
  (`WorkbenchWindow.axaml.cs:228` → `ParameterEditingSession.Refresh` → `Load`), so finishing a
  background library load can start a parse + clone on the UI thread without any user action.

### Smaller findings

- `knx_master.xml` is read twice per split catalogue (`:81-83`, `326-339`, `655-669`):
  26.1 + 17.7 ms for 1.06 MB in the sample. Cheap to remove and worth ~20–40% of eager open.
- `FindProduct` (`:264`) is a linear scan inside `ReadItem` (`:489`), and `Programs.Any`
  (`:505`) is a linear scan per program; both become O(n²) on large catalogues. `FindProgramById`
  (`:293`) also scans `Programs` linearly.
- `CatalogStore`'s doc comment names `%APPDATA%\KnxClone\catalogs`, but
  `AppSettings.AppFolderName` is `BusChord` (`AppSettings.cs:10`); the actual library is
  `%APPDATA%\BusChord\catalogs`. Documentation only.
- The shell's only periodic work is the 100 ms `DispatcherTimer` for monitor sessions
  (`WorkbenchWindow.axaml.cs:263-266`); it does not touch the catalogue. Catalogue loading has no
  scheduler — it is triggered by pane load, parameter demand, import or Reload.
- App startup itself does not read catalogues (`WorkbenchWindow` constructor,
  `CatalogPane.OnLoaded` at `CatalogPane.axaml.cs:96-102`); `--catalog` or opening a device's
  parameters is what pulls the library in.

## 4. What is cached today

| Cache | Lifetime | Scope |
|---|---|---|
| `CatalogLibrary.Shared` + `_snapshot` (`CatalogLibrary.cs:41`, `48-72`) | process | one parse per library, shared by panes |
| `_loading` task deduplication (`:69`, `:166-184`) | process | concurrent callers share one load |
| `_partByName` + `_loadedParts` (`KnxProductCatalog.cs:27-30`, `291-300`) | per catalogue object | one parse per program part |
| `MainWindowViewModel._catalogPrograms` (`MainWindowViewModel.cs:711`, `734`) | process | product ref → program |
| `CatalogStore.LastImportReport` | process | last import only |

Not cached: file metadata, parsed product/section/program summaries, search text, parameter
models, COM/DPT data. `ReloadAsync` throws the in-memory work away by design; a process restart
always starts from XML.

## 5. Options

Complexity: S = days, M = about a week, L = one to two weeks. Cost assumes one developer familiar
with the code. Gain is user-visible on the measured scenarios.

| # | Option | Cost | Risk | Gain | Verdict |
|---|---|---|---|---|---|
| O1 | Debounce + run `Filter` on a worker with a version token; show "searching" | S | Low | removes the 2.4–4.8 s freeze; first result still ~2.4 s later | **Do first** |
| O2 | Memoise `FindProgramFor` per product; dictionaries for products/programs; one master pass; single-pass translations | S | Low | 10–40% off eager/open and part parse; removes O(n²) | **Do first** |
| O3 | Replace the JSON clone with a copy constructor / immutable model; drop the extra signature serialisation | S | Low | 276–387 ms + 46 MB → <20 ms per parameter open | **Do first** |
| O4 | Progressive publish: one snapshot per file (or per batch), pane binds immediately | S–M | Low–Medium | first catalogue visible after 14–97 ms instead of 1.2–1.9 s; no empty pane | Do early |
| O5 | Per-catalogue persistent JSON index (products, sections, program summaries, com objects/DPT, search text) + manifest; atomic writes; versioned | M | Medium (invalidation, schema) | cold pane interactive <500 ms; repeat start <300 ms with zero XML; first search <50 ms | **Core fix** |
| O6 | Precomputed lowercase search text + normalised DPT in the index | S (on top of O5) | Low | 38 ms → <2 ms per keystroke at 1,000 products; no allocations for arrays | **Core fix** |
| O7 | Split "program summary" parse from the full parameter model; parse parameters only when the editor opens | M | Medium | product select/details no parse; memory bounded; part parse still needed once for the summary unless indexed | Do with O5 |
| O8 | Persist parsed parameter models per program (binary or JSON) | M–L | Medium (size, schema) | parameter open <150 ms after first build; no XML on open | Later |
| O9 | Bounded parallel index build (2–4 files), cancellation, progress | S–M | Medium (memory spikes) | 2–4× faster first index build | With O5 |
| O10 | SQLite index | M | Medium (new dependency, native) | incremental updates, cross-catalogue queries | Only if O5 outgrows JSON |
| O11 | Custom binary index | M–L | High (schema/versioning) | fastest load, smallest file | Only if JSON load >100 ms |
| O12 | Memory-mapped / persistent `ZipArchive` handles | S | Low | ~1–3%; Deflate + XML still dominate | **Not worth it** |
| O13 | Cache invalidation by content hash | S–M | Low | correctness for O5 | Required for O5 |

### 5.1 Storage choice for the index

The index is read-mostly, keyed by library file, and small per file. A **per-catalogue JSON file
plus a manifest** is the lazy correct choice:

- BCL only (`System.Text.Json`), no new package, no native payload, easy to inspect and version.
- Per file avoids rewriting one large document on every import and makes partial invalidation
  trivial: only the changed file's index is rebuilt.
- Expected size: products + sections + program summaries + COM/DPT roughly 0.1–0.5 MB per
  typical catalogue; 124 files ≈ 10–60 MB total, loaded in a few hundred ms.
- Use `JsonSerializerContext` source generation if start-up trim/AOT matters later.

SQLite is the better tool only when we need incremental queries across all catalogues, or when the
index grows well beyond ~20 MB and per-file load becomes visible. It adds a package plus a native
binary and deployment/CI surface. A custom binary format is fastest but needs hand-written
versioning, bounds checks and tests; defer until measurements demand it.

IndexedDB is browser-only and not applicable to a native Avalonia/.NET app; the equivalent is a
local file or SQLite index, as the plan already notes.

### 5.2 Invalidation

- Identity per file: library file name (already content-derived for imports,
  `CatalogStore.cs:92-114`) + size + last-write time + index schema version + app version.
- Cheap check: 124 `FileInfo` stats ≈ a few ms; no file content read on the happy path.
- Mismatch, missing index or corrupt JSON → reparse that file only; write the index atomically
  (`tmp` + `File.Move(overwrite: true)`); keep the last good index on failure.
- Optional strong check: SHA-256 of the archive in the background; not on the start-up path
  (reading 400 MB would cost more than the parse we are avoiding).
- Import already parses the file, so write its index in the same operation at no extra cost.
  Remove deletes the index. Reload keeps unchanged entries and reparses changed ones (with a
  "rebuild all" option if the user wants to force it).

### 5.3 Notes on the other levers

- **Lazy per-catalogue parsing** is already implemented for split archives; what is missing is
  that meta-listing does not need the full program model at all. Index the summary and keep the
  full parse for the editor.
- **Progressive streaming** is cheap to add to `CatalogLibrary` (publish each file's snapshot),
  but consumers must tolerate multiple snapshots. It is complementary to the index: with an index
  the first snapshot arrives in milliseconds.
- **Background streaming with progressive UI** is the right answer for the first index build on a
  large library: the pane is usable while the index fills in.
- **Precomputed search index** must preserve current semantics: product name/order number/id/
  description, manufacturer id/name, catalogue name, program name/id/number/version/registration/
  mask, COM name/id, and DPT in `DPST-1-1`, `1.001`, `DPT-1` forms
  (`tests/Knx.CatalogUi.Tests/Program.cs:180-185`).
- **Memory-mapped or compressed archive access** does not remove the Deflate decompression or the
  XML parse; the measurements show XML work is roughly 50–80% of a part load. Keeping the
  `ZipArchive` open across parts saves only central-directory reads (single-digit ms).
- **Duplicate XML parses**: the master double-read (B-finding above), `new XElement` + `Ksxml.Parse`
  on a copy, and `ReadTranslations`' `Ancestors()` walk are all removable independently of the
  index.

## 6. Recommended staged plan

### Stage A — quick win (about 2 days, no schema change)

1. **O1** Move search off the UI thread: debounce ~200 ms, run `CatalogBrowser.Filter` on a
   worker with a query version token, keep the last result visible, show a small busy state.
   The pane must never block on a parse again.
2. **O2** Add `Dictionary<string, CatalogProduct>` and `Dictionary<string, CatalogApplicationProgram>`
   to the catalogue, memoise `FindProgramFor` per product, replace `Programs.Any`/`FindProduct`
   scans, and make `ReadTranslations` a single pass without `Ancestors()` per translation.
3. **O2b** Stop scanning `knx_master.xml` twice: if the ZIP already contains `Catalog.xml` or
   `Hardware.xml`, go straight to the split path; otherwise parse the master once and reuse the
   dictionary.
4. **O3** Replace the JSON round trip and the extra signature serialisation with a copy
   constructor / immutable parameter model.

Expected after Stage A: no UI freeze longer than ~100 ms; first search returns after the same
~2.4 s but the window stays responsive; every later keystroke <5 ms; parameter open
~0.3 s faster; library load ~20–40% faster.

### Stage B — persistent index and progressive UI (about 1 week)

1. **O5** Per-catalogue JSON index + manifest under the library (or
   `%LOCALAPPDATA%\BusChord\cache`), versioned, atomic, rebuilt on identity mismatch.
   Include products, sections, program summaries (id/name/number/version/status) and COM objects
   with DPT. Write it on import and on first load of an unindexed file.
2. **O4** Publish snapshots per file (or small batches) so the pane binds as soon as the first
   entry is available; keep a single "library ready" state for callers that need the full set.
3. **O6** Precompute search text and normalised DPTs into the index; `Filter` becomes a scan over
   strings, no XML, no per-com-object allocations.
4. **O9** Build missing indexes with bounded parallelism (2–4 files) and cancellation; cap
   in-flight bytes so a monolithic 88 MB file cannot blow the heap.

Expected after Stage B: cold pane interactive <500 ms (index hit) and <3 s for the whole library
in the background; repeat start <300 ms with zero XML parsed; first search <50 ms; warm search
<5 ms at 1,000 products; no program parse on selection.

### Stage C — parameter models and long-term shape (1–2 weeks)

1. **O7/O8** Separate the program summary from the parameter model; parse parameters only when
   the editor opens, in the background, and persist the model keyed by program id + schema
   version. Add an LRU cap so visiting many catalogues does not grow memory without bound.
2. Replace the remaining JSON clone/signature work with the immutable model; measure the editor
   open path end to end.
3. Keep JSON unless measurements say otherwise. Move to SQLite only for cross-catalogue queries
   or if index load exceeds ~100 ms; a custom binary only if SQLite is still too slow.
4. Add index diagnostics (hit/miss, files rebuilt, ms spent) behind `--workbench-selftest` or a
   trace flag, plus tests for: import → index written; remove → index deleted; touch file →
   only that file rebuilt; corrupt index → rebuild without crash; two instances writing.

## 7. Acceptance targets

Re-measure on an idle machine with the real 124-catalogue library. Suggested gates:

| Metric | Baseline (measured here) | Target |
|---|---:|---:|
| Cold start, first catalogue visible in the pane | 1.2–1.9 s warm; 11.2 s after copy; +0.6–1.9 s for first details | **<500 ms** |
| Repeat start, unchanged library | 1.2–1.9 s re-parse | **<300 ms, 0 XML files parsed** |
| Whole 124-file index available (background) | 1.2–1.9 s | **<3 s** first build, <300 ms thereafter |
| Reload | 0.8–1.6 s | **<300 ms** when nothing changed; only changed files else |
| First search keystroke | 2.4–4.8 s freeze, 559 MB | **<50 ms, no UI block** |
| Warm search, 1,000 products | 38 ms / 20.7 MB per keystroke | **<5 ms** |
| Product selection → details | 0.6–1.9 s first time | **<50 ms** |
| Parameter pane open | 0.3–2.3 s (+46 MB) | **<150 ms** |
| Transient allocation per program part | 140 MB | **<20 MB** metadata; **<60 MB** full model |
| Steady-state memory, 124 catalogues | 10 MB eager + 14 MB per visited catalogue | **<150 MB** with LRU |

Also keep the existing catalogue self-checks green (`tests/Knx.Catalog.Tests`,
`tests/Knx.CatalogUi.Tests`) and add a performance self-check similar to
`tests/Knx.WorkbenchPerformance.Tests` that prints the numbers above.

## 8. Open questions

1. **Library layout mix.** How many of the real 124 files are monolithic (one big `knxprod.xml`)
   versus split? The largest file is recorded as 88.5 MB; its uncompressed XML size decides
   whether the current cold start is ~2 s or tens of seconds. A read-only inventory script (entry
   count, XML sizes, presence of `Catalog.xml`/`Hardware.xml`) would answer this without reading
   product content.
2. **Search latency expectation.** Is a one-time background build of program/COM/DPT summaries
   acceptable on first start, or must the very first keystroke already include those fields?
3. **Index location.** Next to the library (`%APPDATA%`, roams with the profile, possible OneDrive
   sync) or machine-local (`%LOCALAPPDATA%\BusChord\cache`)? The latter is safer for mtime-based
   invalidation and for not syncing derived data.
4. **Reload semantics.** Should the Reload button force a full XML re-read, or reuse the index and
   only rebuild changed files? Users may expect "reload" to mean "re-read".
5. **Multiple instances.** Two BusChord processes writing the index concurrently need a lock or
   last-writer-wins with atomic replace; confirm this is in scope.
6. **Index schema churn.** How much freedom is there to bump the index version on every release
   (full rebuild) versus maintaining migrations? A rebuild-on-version-bump is the lazy default.
7. **Parameter model persistence.** Is disk usage for parsed parameter models acceptable, or
   should models stay memory-only with an LRU?
8. **Parallelism budget.** May the first index build use 2–4 cores in the background while the
   user works, or should it stay single-threaded to protect battery/thermals on laptops?

## Appendix A — reproduction

The harness lives outside the repository and was not committed:

```
%TEMP%/opencode/catalog-perf-bench   # broad pass: archive inventory, open, lazy, search,
                                     # library scaling, synthetic monolithic catalogues
%TEMP%/opencode/catalog-perf-focus   # repeated focused measurements (median of 3)
```

Both reference `src/Knx.Core/Knx.Core.csproj` and `src/Knx.App/Knx.App.csproj` from this
worktree and call only public APIs (`KnxProductCatalog.Open`, `CatalogStore.LoadAll`,
`CatalogLibrary.GetAsync/ReloadAsync`, `CatalogBrowser.Filter`, `Ksxml.Parse`). Commands:

```
dotnet build ets6.sln -c Release
dotnet run -c Release --project <harness> --
dotnet run -c Release --project <harness> -- biglib   # 124-file scale library
```

Sample catalogues were read from already-present local files under `%TEMP%/opencode`; the scale
library and monolithic corpora were generated copies. No catalogue content, product names,
order numbers or customer data are included in this report or in the repository.

## Appendix B — measurement caveat

The host ran several parallel agent builds during the measurements. Absolute times moved by up to
~2x between repetitions (for example, one 30 MB `XDocument.Load` measured 187 ms in one pass and
893 ms in another). The medians quoted as ranges reflect that. Before using the acceptance targets
as release gates, re-run the focused harness on an idle machine and record the numbers here.
