using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Knx.Catalog;

namespace Knx.Catalog.Tests;

/// <summary>
/// Opt-in timing harness for the local catalogue library (never part of the self-check).
/// The library is only read; the metadata cache is written to KNX_CATALOG_BENCH_CACHE
/// (default: a fresh temp directory), never to the machine cache of the running user.
/// Run: KNX_CATALOG_BENCH=&lt;library&gt; KNX_CATALOG_BENCH_CACHE=&lt;cache&gt; dotnet run -c Release
/// </summary>
internal static class CatalogBench
{
    public static int? TryRun()
    {
        var library = Environment.GetEnvironmentVariable("KNX_CATALOG_BENCH");
        if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library)) return null;

        var cache = Environment.GetEnvironmentVariable("KNX_CATALOG_BENCH_CACHE");
        if (string.IsNullOrWhiteSpace(cache))
            cache = Path.Combine(Path.GetTempPath(), "knx-catalog-bench-cache");
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("KNX_CATALOG_BENCH_REPEATS"),
            out var parsed) ? Math.Max(1, parsed) : 3;
        var skipCold = string.Equals(Environment.GetEnvironmentVariable("KNX_CATALOG_BENCH_SKIP_COLD"),
            "1", StringComparison.Ordinal);

        var files = Directory.EnumerateFiles(library)
            .Where(IsCatalogFile)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var libraryBytes = files.Sum(file => new FileInfo(file).Length);
        Console.WriteLine($"  INFO library: {files.Count} files, {libraryBytes / (1024 * 1024)} MiB, cache {cache}");

        // What a warm start spends today on content revalidation: a full SHA-256 read per file.
        var hashMs = Time(() =>
        {
            foreach (var file in files)
            {
                using var stream = File.OpenRead(file);
                _ = SHA256.HashData(stream);
            }
        });

        // The cheap identity check a warm start pays instead: size plus head and tail sample.
        var sampleMs = Time(() =>
        {
            var buffer = new byte[64 * 1024];
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                using var stream = File.OpenRead(file);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Span<byte> length = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, info.Length);
                hash.AppendData(length);
                var head = Read(stream, buffer, buffer.Length);
                hash.AppendData(buffer, 0, head);
                var remaining = info.Length - head;
                if (remaining > 0)
                {
                    var tail = (int)Math.Min(buffer.Length, remaining);
                    stream.Seek(-tail, SeekOrigin.End);
                    hash.AppendData(buffer, 0, Read(stream, buffer, tail));
                }
                _ = Convert.ToHexString(hash.GetHashAndReset());
            }

            static int Read(Stream stream, byte[] buffer, int count)
            {
                var total = 0;
                while (total < count)
                {
                    var read = stream.Read(buffer, total, count - total);
                    if (read == 0) break;
                    total += read;
                }
                return total;
            }
        });

        double? coldMs = null;
        double? coldCpuMs = null;
        CatalogIndexStatistics? coldStats = null;
        double? coldFirstCatalogMs = null;
        var coldProgress = 0;
        double coldSilenceMs = 0;
        var coldPerFile = new List<(string File, double Ms)>();
        if (!skipCold)
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            var cold = Measure(library, cache, out coldStats, out var coldFirst, coldPerFile,
                out var coldCpu, out coldProgress, out coldSilenceMs);
            coldMs = cold;
            coldCpuMs = coldCpu;
            coldFirstCatalogMs = coldFirst;
        }

        var warm = new List<double>();
        var warmCpu = new List<double>();
        var warmFirst = new List<double>();
        var warmProgress = 0;
        double warmSilenceMs = 0;
        var warmPerFile = new List<(string File, double Ms)>();
        CatalogIndexStatistics? warmStats = null;
        for (var run = 0; run < repeats; run++)
        {
            warm.Add(Measure(library, cache, out var stats, out var first,
                run == 0 ? warmPerFile : null, out var cpu, out warmProgress, out warmSilenceMs));
            warmCpu.Add(cpu);
            warmFirst.Add(first);
            warmStats = stats;
        }

        var indexFiles = Directory.Exists(cache)
            ? Directory.GetFiles(cache, "*.json.gz")
            : [];
        var indexBytes = indexFiles.Sum(file => new FileInfo(file).Length);
        // What a warm start pays to accept the remembered hash: one short sidecar read per file.
        var identityFiles = Directory.Exists(cache)
            ? Directory.GetFiles(cache, "*.identity")
            : [];
        var identityMs = identityFiles.Length == 0 ? 0 : Time(() =>
        {
            foreach (var file in identityFiles) _ = File.ReadAllText(file);
        });
        // Cost of just reading the index documents back (gzip + JSON text), without object construction.
        long indexJsonBytes = 0;
        var indexReadMs = indexFiles.Length == 0 ? 0 : Time(() =>
        {
            var buffer = new byte[1 << 20];
            foreach (var file in indexFiles)
            {
                using var stream = File.OpenRead(file);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0) indexJsonBytes += read;
            }
        });

        // Opening one catalogue from disk is the "first file" a fresh library shows.
        var largest = files.OrderByDescending(file => new FileInfo(file).Length).FirstOrDefault();
        double? firstOpenMs = null;
        if (largest is not null)
        {
            _ = KnxProductCatalog.Open(largest);
            firstOpenMs = Time(() => _ = KnxProductCatalog.Open(largest));
        }

        // Phase 2 (XML work) is parallel, so a per-file delta is not a per-file cost; what matters
        // for "not a silent block" is how often progress arrives and how long the longest gap is.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Files = files.Count,
            LibraryMiB = libraryBytes / (1024 * 1024),
            IndexDocuments = indexFiles.Length,
            IndexMiB = indexBytes / (1024 * 1024),
            IndexJsonMiB = indexJsonBytes / (1024 * 1024),
            FullHashMs = Math.Round(hashMs, 1),
            SampleHashMs = Math.Round(sampleMs, 1),
            IdentityReadMs = Math.Round(identityMs, 1),
            IndexReadMs = Math.Round(indexReadMs, 1),
            ColdMs = coldMs is null ? (double?)null : Math.Round(coldMs.Value, 1),
            ColdCpuMs = coldCpuMs is null ? (double?)null : Math.Round(coldCpuMs.Value, 1),
            ColdFirstCatalogMs = coldFirstCatalogMs is null ? (double?)null : Math.Round(coldFirstCatalogMs.Value, 1),
            ColdProgressReports = coldProgress,
            ColdLongestSilenceMs = coldMs is null ? (double?)null : Math.Round(coldSilenceMs, 1),
            ColdHits = coldStats?.IndexHits,
            ColdMisses = coldStats?.IndexMisses,
            ColdXmlParts = coldStats?.XmlProgramParts,
            WarmMs = warm.Select(value => Math.Round(value, 1)).ToArray(),
            WarmCpuMs = warmCpu.Select(value => Math.Round(value, 1)).ToArray(),
            WarmMedianMs = warm.Count == 0 ? 0 : Math.Round(warm.Order().ElementAt(warm.Count / 2), 1),
            WarmFirstCatalogMs = warmFirst.Select(value => Math.Round(value, 1)).ToArray(),
            WarmProgressReports = warmProgress,
            WarmLongestSilenceMs = Math.Round(warmSilenceMs, 1),
            WarmHits = warmStats?.IndexHits,
            WarmMisses = warmStats?.IndexMisses,
            FirstFileOpenMs = firstOpenMs is null ? (double?)null : Math.Round(firstOpenMs.Value, 1),
            ManagedMiB = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>One full library read; returns the elapsed milliseconds.</summary>
    private static double Measure(string library, string cache, out CatalogIndexStatistics? statistics,
        out double firstCatalogMs, List<(string File, double Ms)>? perFile, out double cpuMs,
        out int progressReports, out double longestSilenceMs)
    {
        var store = new CatalogStore(library, cache);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        double? first = null;
        var reports = 0;
        var last = 0.0;
        var silence = 0.0;
        var catalogs = store.LoadAll(new Sink(() =>
        {
            var now = watch.Elapsed.TotalMilliseconds;
            reports++;
            silence = Math.Max(silence, now - last);
            last = now;
        }, perFile, watch), catalog =>
        {
            // The pane is interactive as soon as one catalogue with products is published.
            if (first is null && catalog.Products.Count > 0) first = watch.Elapsed.TotalMilliseconds;
        });
        watch.Stop();
        cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        statistics = store.LastIndexStatistics;
        firstCatalogMs = first ?? watch.Elapsed.TotalMilliseconds;
        progressReports = reports;
        longestSilenceMs = silence;
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Counts progress reports and records when each library file finished.</summary>
    private sealed class Sink(Action onReport, List<(string File, double Ms)>? perFile, Stopwatch watch)
        : IProgress<CatalogLoadProgress>
    {
        public void Report(CatalogLoadProgress value)
        {
            onReport();
            if (perFile is null) return;
            lock (perFile) perFile.Add((Path.GetFileName(value.File), watch.Elapsed.TotalMilliseconds));
        }
    }

    private static double Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static bool IsCatalogFile(string file) =>
        file.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".knxprod.zip", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(file).Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase);
}
