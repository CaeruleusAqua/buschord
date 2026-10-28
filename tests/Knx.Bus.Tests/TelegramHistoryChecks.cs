using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Knx.Bus;
using Knx.Bus.History;

internal static class TelegramHistoryChecks
{
    public static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-telegram-history-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var time = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        string PathFor(string name) => Path.Combine(root, name);
        TelegramHistoryDraft Draft(int seconds, string source = "1.1.1", string destination = "1/2/3", string service = "GroupValueWrite")
            => new(time.AddSeconds(seconds), source, destination, true, service, "01", "real", "interface-a", "project-a");
        async Task Case(string name, Func<Task<bool>> body)
        {
            try { check("history: " + name, await body()); }
            catch (Exception) { check("history: " + name, false); }
        }
        try
        {
            await Case("immutable identities and UTC survive reopening", async () =>
            {
                TelegramHistoryEntry saved;
                string id;
                using (var store = new FileTelegramHistoryStore(PathFor("restart")))
                {
                    id = store.StoreId;
                    saved = await store.AppendAsync(Draft(0) with { TimestampUtc = time.ToOffset(TimeSpan.FromHours(2)) });
                }
                using var reopened = new FileTelegramHistoryStore(PathFor("restart"));
                var page = await reopened.QueryAsync(new());
                return reopened.StoreId == id && page.Rows is [{ Entry: var entry }] && entry == saved
                    && entry.TimestampUtc.Offset == TimeSpan.Zero && entry.Schema == 1;
            });
            await Case("UTC half-open bounds and OR within AND across filters", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("filters"));
                await store.AppendAsync(Draft(0));
                var expected = await store.AppendAsync(Draft(1, "1.1.2"));
                await store.AppendAsync(Draft(1, "1.1.3"));
                await store.AppendAsync(Draft(1, "1.1.2", "9/9/9"));
                await store.AppendAsync(Draft(1, "1.1.2", service: "GroupValueRead"));
                await store.AppendAsync(Draft(2, "1.1.2"));
                var page = await store.QueryAsync(new()
                {
                    FromUtc = time.AddSeconds(1).ToOffset(TimeSpan.FromHours(2)), UntilUtc = time.AddSeconds(2),
                    Sources = ["1.1.1", "1.1.2"], Destinations = ["1/2/3"], Services = ["GroupValueWrite"],
                    SourceIds = ["interface-a"], ProjectIds = ["project-a"], IsGroup = true, RequestGeneration = 71
                });
                return page.Rows is [{ Entry.Id: var found }] && found == expected.Id && page.RequestGeneration == 71;
            });
            await Case("stable timestamp plus identity order and bounded cursor pages", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("cursor"));
                var entries = new List<TelegramHistoryEntry>();
                for (var index = 0; index < 7; index++) entries.Add(await store.AppendAsync(Draft(index / 3)));
                var expected = entries.OrderBy(entry => entry.TimestampUtc).ThenBy(entry => entry.Id, StringComparer.Ordinal).Select(entry => entry.Id).ToArray();
                var actual = new List<string>();
                string? cursor = null;
                do
                {
                    var page = await store.QueryAsync(new() { PageSize = 2, Cursor = cursor });
                    if (page.Status != TelegramQueryStatus.Complete || page.Rows.Count > 2) return false;
                    actual.AddRange(page.Rows.Select(row => row.Entry.Id)); cursor = page.NextCursor;
                } while (cursor is not null);
                var reverse = await store.QueryAsync(new() { NewestFirst = true });
                return actual.SequenceEqual(expected) && reverse.Rows.Select(row => row.Entry.Id).SequenceEqual(expected.Reverse());
            });
            await Case("cursor snapshot excludes later append and binds filter", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("snapshot"));
                await store.AppendAsync(Draft(0)); var second = await store.AppendAsync(Draft(2));
                var firstPage = await store.QueryAsync(new() { PageSize = 1 });
                await store.AppendAsync(Draft(1)); await store.AppendAsync(Draft(3));
                var continuation = await store.QueryAsync(new() { PageSize = 1, Cursor = firstPage.NextCursor });
                var wrongFilter = await store.QueryAsync(new() { PageSize = 1, Cursor = firstPage.NextCursor, Sources = ["1.1.2"] });
                var malformed = await store.QueryAsync(new() { Cursor = "not a cursor" });
                return continuation.Rows is [{ Entry.Id: var found }] && found == second.Id && continuation.NextCursor is null
                    && wrongFilter.Status == TelegramQueryStatus.StaleCursor && malformed.Status == TelegramQueryStatus.StaleCursor;
            });
            await Case("unfiltered context crosses time bounds and deduplicates", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("context"));
                var before = await store.AppendAsync(Draft(0, "9.9.9"));
                var hit = await store.AppendAsync(Draft(1));
                var after = await store.AppendAsync(Draft(2, "9.9.9"));
                var page = await store.QueryAsync(new()
                {
                    FromUtc = time.AddSeconds(1), UntilUtc = time.AddSeconds(2), Sources = ["1.1.1"], ContextBefore = 1, ContextAfter = 1
                });
                return page.MatchedCount == 1 && page.Rows.Select(row => row.Entry.Id).SequenceEqual(new[] { before.Id, hit.Id, after.Id })
                    && !page.Rows[0].IsMatch && page.Rows[1].IsMatch && !page.Rows[2].IsMatch;
            });
            await Case("capture pauses are gaps and open ends are never invented", async () =>
            {
                var path = PathFor("coverage");
                using (var store = new FileTelegramHistoryStore(path))
                {
                    var first = await store.BeginCaptureAsync("interface-a", time);
                    await store.AppendAsync(Draft(1) with { CaptureId = first.Id });
                    await store.EndCaptureAsync(first.Id, time.AddSeconds(2), "pause");
                    var resumed = await store.BeginCaptureAsync("interface-a", time.AddSeconds(4));
                    await store.EndCaptureAsync(resumed.Id, time.AddSeconds(6), "disconnect");
                    await store.BeginCaptureAsync("interface-b", time.AddSeconds(7));
                    var coverage = await store.GetCoverageAsync(time, time.AddSeconds(10));
                    if (coverage.KnownCaptured.Count != 2 || coverage.Gaps.Count != 2 || coverage.Gaps[0] != new TelegramTimeRange(time.AddSeconds(2), time.AddSeconds(4))
                        || coverage.CaptureIntervals.Count(interval => interval.IsActive) != 1) return false;
                }
                using var reopened = new FileTelegramHistoryStore(path);
                var afterRestart = await reopened.GetCoverageAsync(time, time.AddSeconds(10));
                return afterRestart.CaptureIntervals.Count(interval => interval.EndUnknown && !interval.IsActive && interval.UntilUtc is null) == 1
                    && afterRestart.KnownCaptured.Count == 2;
            });
            await Case("capture source identity is separate and mismatches are refused", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("capture-source"));
                var capture = await store.BeginCaptureAsync("interface-a", time);
                var entry = await store.AppendAsync(Draft(1) with { CaptureId = capture.Id });
                var mismatch = await ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Draft(2) with { CaptureId = capture.Id, SourceId = "interface-b" }));
                await store.EndCaptureAsync(capture.Id, time.AddSeconds(3), "pause");
                return entry.Source == "1.1.1" && entry.SourceId == "interface-a" && mismatch
                    && await ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Draft(2) with { CaptureId = capture.Id }));
            });
            await Case("retention preserves retained IDs and invalidates old cursors", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("retention"));
                await store.AppendAsync(Draft(0)); var kept = await store.AppendAsync(Draft(2)); await store.AppendAsync(Draft(3));
                var page = await store.QueryAsync(new() { PageSize = 1 });
                var result = await store.RetainFromAsync(time.AddSeconds(1));
                var stale = await store.QueryAsync(new() { PageSize = 1, Cursor = page.NextCursor });
                var current = await store.QueryAsync(new());
                var coverage = await store.GetCoverageAsync(time, time.AddSeconds(4));
                return result.RemovedTelegrams == 1 && current.Rows.Count == 2 && current.Rows[0].Entry.Id == kept.Id
                    && stale.Status == TelegramQueryStatus.StaleCursor && coverage.RetainedFromUtc == time.AddSeconds(1)
                    && await ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Draft(0)));
            });
            await Case("read-only browsing and consistent backup never start capture", async () =>
            {
                var path = PathFor("readonly"); string file;
                using (var store = new FileTelegramHistoryStore(path)) { await store.AppendAsync(Draft(1)); file = store.FilePath; }
                using var readOnly = new FileTelegramHistoryStore(path, readOnly: true);
                var backup = Path.Combine(root, "backup.bch");
                await readOnly.BackupAsync(backup);
                var coverage = await readOnly.GetCoverageAsync(time, time.AddSeconds(4));
                return readOnly.Capabilities.ReadOnly && !readOnly.Capabilities.Retention && coverage.CaptureIntervals.Count == 0
                    && File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(backup))
                    && await ThrowsAsync<InvalidOperationException>(() => readOnly.AppendAsync(Draft(2)))
                    && await ThrowsAsync<InvalidOperationException>(() => readOnly.BeginCaptureAsync("interface-a", time))
                    && await ThrowsAsync<InvalidOperationException>(() => readOnly.RetainFromAsync(time))
                    && await ThrowsAsync<IOException>(() => readOnly.BackupAsync(backup));
            });
            await Case("single writer with concurrent read-only snapshot", async () =>
            {
                using var writer = new FileTelegramHistoryStore(PathFor("lease"));
                await writer.AppendAsync(Draft(0));
                var rejected = Throws<IOException>(() => new FileTelegramHistoryStore(PathFor("lease")).Dispose());
                using var reader = new FileTelegramHistoryStore(PathFor("lease"), readOnly: true);
                await writer.AppendAsync(Draft(1));
                return rejected && (await reader.QueryAsync(new())).Rows.Count == 1 && (await writer.QueryAsync(new())).Rows.Count == 2;
            });
            await Case("cancelled operations leave existing data and no backup", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("cancel"));
                await store.AppendAsync(Draft(0));
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                var backup = Path.Combine(root, "cancelled.bch");
                return await ThrowsAsync<OperationCanceledException>(() => store.AppendAsync(Draft(1), cancelled.Token))
                    && await ThrowsAsync<OperationCanceledException>(() => store.QueryAsync(new(), cancelled.Token))
                    && await ThrowsAsync<OperationCanceledException>(() => store.RetainFromAsync(time.AddSeconds(1), cancelled.Token))
                    && await ThrowsAsync<OperationCanceledException>(() => store.BackupAsync(backup, cancelled.Token))
                    && !File.Exists(backup) && (await store.QueryAsync(new())).Rows.Count == 1;
            });
            await Case("partial suffix is read-only recoverable and preserved before writable repair", async () =>
            {
                var path = PathFor("tail"); string file; long goodLength; string id;
                using (var store = new FileTelegramHistoryStore(path))
                {
                    id = (await store.AppendAsync(Draft(0))).Id; file = store.FilePath; goodLength = new FileInfo(file).Length;
                }
                using (var append = new FileStream(file, FileMode.Append)) append.Write(new byte[] { 20, 0, 0 });
                using (var reader = new FileTelegramHistoryStore(path, readOnly: true))
                {
                    if (!(await reader.GetCoverageAsync(time, time.AddSeconds(1))).HasUnverifiedTail || new FileInfo(file).Length == goodLength) return false;
                }
                using (var repaired = new FileTelegramHistoryStore(path))
                {
                    if (new FileInfo(file).Length != goodLength || (await repaired.QueryAsync(new())).Rows[0].Entry.Id != id
                        || Directory.GetFiles(path, "history-damaged-*.bch").Length != 1) return false;
                    await repaired.AppendAsync(Draft(1));
                }
                using var reopened = new FileTelegramHistoryStore(path);
                return (await reopened.QueryAsync(new())).Rows.Count == 2 && (await reopened.GetCoverageAsync(time, time.AddSeconds(3))).HasUnverifiedTail;
            });
            await Case("checksum damage stops at first bad frame without resynchronization", async () =>
            {
                var path = PathFor("corruption"); string file; long boundary;
                using (var store = new FileTelegramHistoryStore(path))
                {
                    await store.AppendAsync(Draft(0)); boundary = new FileInfo(store.FilePath).Length;
                    await store.AppendAsync(Draft(1)); await store.AppendAsync(Draft(2)); file = store.FilePath;
                }
                using (var damage = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
                {
                    damage.Position = boundary + 8; var original = damage.ReadByte(); damage.Position--; damage.WriteByte((byte)(original ^ 1));
                }
                using var reader = new FileTelegramHistoryStore(path, readOnly: true);
                return (await reader.QueryAsync(new())).Rows.Count == 1 && (await reader.GetCoverageAsync(time, time.AddSeconds(3))).HasUnverifiedTail;
            });
            await Case("unknown record schema refuses writable recovery", async () =>
            {
                var path = PathFor("future-schema"); string file; long boundary;
                using (var store = new FileTelegramHistoryStore(path)) { file = store.FilePath; boundary = new FileInfo(file).Length; }
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, Kind = "future" });
                using (var append = new FileStream(file, FileMode.Append))
                {
                    var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
                    append.Write(length); append.Write(payload); append.Write(SHA256.HashData(payload));
                }
                var lengthBefore = new FileInfo(file).Length;
                return Throws<NotSupportedException>(() => new FileTelegramHistoryStore(path).Dispose())
                    && new FileInfo(file).Length == lengthBefore && lengthBefore > boundary && Directory.GetFiles(path, "history-damaged-*.bch").Length == 0;
            });
            await Case("decoded payload does not fabricate raw cEMI or unknown flags", async () =>
            {
                var recorded = new RecordedTelegram { Timestamp = time, Source = "1.1.1", Destination = "1/2/3", IsGroupAddress = true, Apci = "GroupValueWrite", Data = "01", HopCount = 6 };
                using var store = new FileTelegramHistoryStore(PathFor("metadata"));
                var saved = await store.AppendAsync(TelegramHistoryDraft.FromRecorded(recorded, "interface-a", "real"));
                return saved.HopCount == 6 && saved.RawCemiHex is null && saved.Flags is null && saved.Priority is null && saved.Acknowledged is null
                    && saved.DataHex == "01" && saved.Source == "1.1.1" && saved.SourceId == "interface-a";
            });
            await Case("resource limits refuse oversized filters and frames", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("limits"));
                return await ThrowsAsync<InvalidDataException>(() => store.AppendAsync(Draft(0) with { DataHex = "not-hex" }))
                    && await ThrowsAsync<InvalidDataException>(() => store.AppendAsync(Draft(0) with { RawCemiHex = new string('0', 8194) }))
                    && await ThrowsAsync<InvalidDataException>(() => store.AppendAsync(Draft(0) with { HopCount = 8 }))
                    && await ThrowsAsync<ArgumentException>(() => store.QueryAsync(new() { Sources = Enumerable.Range(0, 65).Select(index => index.ToString()).ToArray() }))
                    && await ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(new() { PageSize = 201 }))
                    && await ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(new() { PageSize = 200, ContextBefore = 20, ContextAfter = 20 }))
                    && (await store.QueryAsync(new())).Rows.Count == 0;
            });
            await Case("scan budget is explicit and does not return partial matches", async () =>
            {
                var path = PathFor("scan-limit");
                using (var store = new FileTelegramHistoryStore(path)) { await store.AppendAsync(Draft(0)); }
                using var limited = new FileTelegramHistoryStore(path, true, new() { MaximumQueryScanBytes = 1 });
                var result = await limited.QueryAsync(new());
                return result.Status == TelegramQueryStatus.ScanLimit && result.Rows.Count == 0 && result.NextCursor is null;
            });
            await Case("capacity refusal preserves data until explicit retention", async () =>
            {
                using var store = new FileTelegramHistoryStore(PathFor("capacity"), options: new() { MaximumStoreBytes = 4096 });
                var refused = false;
                for (var index = 0; index < 32; index++)
                {
                    try { await store.AppendAsync(Draft(index)); }
                    catch (InvalidOperationException) { refused = true; break; }
                }
                var before = (await store.QueryAsync(new())).Rows.Count;
                if (!refused || before == 0 || new FileInfo(store.FilePath).Length > 4096) return false;
                var retained = await store.RetainFromAsync(time.AddDays(1));
                await store.AppendAsync(Draft(86401));
                return retained.RemovedTelegrams == before && (await store.QueryAsync(new())).Rows.Count == 1;
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("buschord-telegram-history-checks-", StringComparison.Ordinal))
                try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); return false; } catch (T) { return true; }
    }
    private static bool Throws<T>(Action operation) where T : Exception
    {
        try { operation(); return false; } catch (T) { return true; }
    }
}
