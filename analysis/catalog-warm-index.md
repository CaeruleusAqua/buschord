# Catalogue warm start — measured before and after

Follow-up to `analysis/catalog-load-performance.md` and the persistent index work. This change
targets the warm-start path only: the library still builds progressively from XML on a miss, but a
start with valid index documents must stay below the 300 ms H5 target.

- Base: `c333937` (`development`), branch `agents/perf/catalog-warm2`
- Date: 2026-10-22
- Machine: Windows 11 Pro 26200, Intel Core i7-1370P (14 cores / 20 threads), 64 GB RAM,
  .NET SDK 9.0.302, Release build
- Library: the real 124-file library in `%APPDATA%\BusChord\catalogs` (429 MiB, read-only);
  cache in a temporary directory, never the machine cache of the running user
- Method: `dotnet build ets6.sln -c Release`, then the committed harness
  `tests/Knx.Catalog.Tests` with `KNX_CATALOG_BENCH=<library>`,
  `KNX_CATALOG_BENCH_CACHE=<temp>` and `KNX_CATALOG_BENCH_REPEATS=9`. Warm runs use
  `KNX_CATALOG_BENCH_SKIP_COLD=1`. The shared host ran other agent builds, so the baseline and
  the new build were measured alternately in the same session; the table below is the median of
  three process runs of nine warm loads each.

## Result

| Metric | Before | After |
|---|---:|---:|
| Warm load, whole library (median of 3 × 9 runs) | **465 ms** | **134 ms** |
| Warm load, quiet-host run | 387–429 ms | **119 ms** (confirm run: median 105 ms, best 101 ms) |
| Warm CPU time per load (median of the same runs) | 656–875 ms | 250–297 ms |
| First catalogue published | 1.5–27 ms | 1.3–10.5 ms |
| Index documents (JSON) | 26 MiB | 17 MiB |
| Index documents (gzip on disk) | 4.2 MiB | 5.5 MiB |
| Managed heap after warm load | 28–29 MiB | 13–14 MiB |
| Warm index hits / XML reads | 124 / 0 | 124 / 0 |
| Cold build, 124 files | 45–71 s CPU | 56–64 s CPU (unchanged, XML-bound) |
| Cold first catalogue / progress reports | 31 ms / 1648 | 32–47 ms / 1648 |

## What the warm start spent its time on (instrumented, before)

Per warm load, summed across the parallel index readers (5 runs, median):

| Phase | CPU |
|---|---:|
| gzip inflate of the index documents | ~140 ms |
| JSON deserialize (26 MiB) | ~275 ms |
| COM objects unpacked into 200,584 `ComObject` instances + display dictionary | ~265 ms |
| Search text + normalised datapoint ids rebuilt for every product/program | ~160 ms |
| ZIP central-directory scan per catalogue (`LocationsAreSafe`) | ~31 ms |
| Fingerprint (size + 64 KiB head/tail per file) | ~45 ms |
| Identity sidecar read (124 small files) | ~29 ms |
| Product/program/object construction | ~10 ms |

Wall time was 396 ms because eight readers overlap; the work above is what kept it there.

## What changed

1. **Communication objects stay encoded until a pane needs them.** The index carries each
   program's packed objects as `base64(gzip(...))` (~25 % of the packed character count), and
   `CatalogApplicationProgram.ComObjects` decodes one program on first access. A warm library
   load no longer builds 200k objects that nobody displays; the decoded objects are identical to
   the XML reader's.
2. **Search text and datapoint ids are persisted** in the index (`SearchText`, `DatapointTypes`)
   and assigned from the document instead of being lowercased and joined again. Search itself is
   unchanged: it still scans the same precomputed strings, so per-keystroke cost stays <1 ms.
3. **No ZIP central-directory scan on a hit.** Entry names are still rejected when rooted or
   containing `..`, but existence is left to the lazy reader. The cache document is already bound
   to the exact archive by size, last-write time and SHA-256/fingerprint identity, so only a wrong
   name (not an escape) is possible; a missing entry surfaces when the part is opened.
4. **Unpacking was rewritten for the on-demand path**: spans parse numbers in place and the
   display dictionary is filled directly, instead of materialising a `string[7]` and a tuple per
   record.
5. Index `SchemaVersion` 6 → 8: the first start after the update rebuilds every document once,
   then warm starts use the new shape.

## Integrity and failure behaviour

- The cheap path still re-reads the file size, its last-write time and a SHA-256 over its head and
  tail; only then is the remembered full SHA-256 reused. Any difference re-hashes the whole file
  and re-reads XML if needed. Neither size nor mtime alone ever accepts a document.
- The document still stores the catalogue's full content hash and is rejected unless it matches.
  The gzip container's CRC covers accidental corruption of the whole document; a deliberately
  re-encoded document is outside the threat model (whoever writes the cache directory can replace
  the catalogue instead).
- A corrupt per-program payload (bad base64/gzip) leaves that program's object list empty instead
  of crashing a pane; the rest of the catalogue and search remain usable.
- Path safety is unchanged for XML catalogues (source paths must resolve below the catalogue
  directory) and structural for archives (relative, no `..`).

## Regression checks

- `tests/Knx.Catalog.Tests` gained a warm-start check that all programs still carry an encoded
  payload and that no `ComObject` list has been materialised after `LoadAll` (bounded memory).
  Existing checks cover warm hit without XML, content-hash tampering, mtime invalidation,
  cancellation without half-written entries, packed COM round-trip and DCA metadata from cache.
- Green in Release: `Knx.Catalog.Tests`, `Knx.CatalogUi.Tests`, `Knx.ParameterUi.Tests`,
  `Knx.Workbench.Tests`, `Knx.WorkbenchUiState.Tests`, and `src/Knx.App --smoke`.

## Reproduction

```
$env:KNX_CATALOG_BENCH = "$env:APPDATA\BusChord\catalogs"
$env:KNX_CATALOG_BENCH_CACHE = "<empty temp dir>"
$env:KNX_CATALOG_BENCH_REPEATS = "9"
# first run: cold build plus warm repeats
dotnet run -c Release --project tests/Knx.Catalog.Tests
# repeat without the cold build
$env:KNX_CATALOG_BENCH_SKIP_COLD = "1"
dotnet run -c Release --project tests/Knx.Catalog.Tests
```

The harness also prints `FullHashMs`, `SampleHashMs`, `IdentityReadMs`, `IndexReadMs`,
`ColdProgressReports`, `ColdLongestSilenceMs` and `ManagedMiB`, so a regression in any part of the
warm path is visible without editing code.

## Remaining gap

- **Cold build is still XML-bound** (~50–90 s on this library, dominated by program-part parsing);
  this change intentionally leaves it progressive and bounded. A next step would be a bounded
  shared parse cache for repeated imports, not a warm-start change.
- **First COM access per program** costs one base64+gzip decode plus unpack (single-digit ms for
  the largest part in this library) instead of being free. It happens only when a pane shows that
  program, and the result is cached for the process lifetime.
- The on-disk index grew from 4.2 to 5.5 MiB because the search text is now persisted; that buys
  the removed recomputation and keeps warm search allocation-free.
