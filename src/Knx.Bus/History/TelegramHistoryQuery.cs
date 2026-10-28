using System.Security.Cryptography;
using System.Text.Json;

namespace Knx.Bus.History;

public sealed partial class FileTelegramHistoryStore
{
    private sealed record QueryCursor(int Schema, string StoreId, long Generation, long SnapshotEnd,
        string QueryHash, long LastTicks, string LastId);

    public async Task<TelegramQueryPage> QueryAsync(TelegramQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = NormalizeQuery(query);
        var fingerprint = QueryFingerprint(normalized);
        FileStream reader;
        long end;
        long generation;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            end = _validLength; generation = Generation;
            reader = OpenReader();
        }
        finally { _gate.Release(); }
        using (reader)
            return await Task.Run(() => ExecuteQuery(reader, end, generation, normalized, fingerprint, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private TelegramQueryPage ExecuteQuery(FileStream reader, long currentEnd, long generation, TelegramQuery query,
        string fingerprint, CancellationToken token)
    {
        var scanned = 0L;
        TelegramQueryPage Result(TelegramQueryStatus status) => new([], null, generation, query.RequestGeneration, status, scanned);
        var header = TelegramHistoryFormat.ReadHeader(reader);
        if (header.Id.ToString("N") != StoreId || header.Generation != generation) return Result(TelegramQueryStatus.StaleCursor);
        QueryCursor? cursor = null;
        if (query.Cursor is not null)
        {
            try
            {
                if (query.Cursor.Length > 4096) return Result(TelegramQueryStatus.StaleCursor);
                cursor = JsonSerializer.Deserialize<QueryCursor>(Convert.FromBase64String(query.Cursor), TelegramHistoryFormat.Json);
            }
            catch (Exception error) when (error is FormatException or JsonException) { return Result(TelegramQueryStatus.StaleCursor); }
            if (cursor is null || cursor.Schema != 1 || cursor.StoreId != StoreId || cursor.Generation != generation
                || cursor.QueryHash != fingerprint || cursor.SnapshotEnd < TelegramHistoryFormat.HeaderSize || cursor.SnapshotEnd > currentEnd
                || cursor.LastTicks < 0 || cursor.LastTicks > DateTimeOffset.MaxValue.UtcTicks || !TelegramHistoryFormat.IsId(cursor.LastId))
                return Result(TelegramQueryStatus.StaleCursor);
        }
        var snapshotEnd = cursor?.SnapshotEnd ?? currentEnd;
        var passes = query.ContextBefore + query.ContextAfter == 0 ? 1 : 2;
        if ((snapshotEnd - TelegramHistoryFormat.HeaderSize) * passes > _options.MaximumQueryScanBytes)
            return Result(TelegramQueryStatus.ScanLimit);
        var ordering = Comparer<TelegramHistoryEntry>.Create((left, right) =>
            query.NewestFirst ? Compare(right, left) : Compare(left, right));
        var selected = new SortedSet<TelegramHistoryEntry>(ordering);
        try
        {
            Scan(reader, snapshotEnd, entry =>
            {
                if (!Matches(entry, query)) return;
                if (cursor is not null)
                {
                    var comparison = entry.TimestampUtc.UtcTicks.CompareTo(cursor.LastTicks);
                    if (comparison == 0) comparison = StringComparer.Ordinal.Compare(entry.Id, cursor.LastId);
                    if (query.NewestFirst ? comparison >= 0 : comparison <= 0) return;
                }
                selected.Add(entry);
                if (selected.Count > query.PageSize + 1) selected.Remove(selected.Max!);
            }, token, ref scanned);
        }
        catch (InvalidDataException) when (cursor is not null) { return Result(TelegramQueryStatus.StaleCursor); }
        var hasMore = selected.Count > query.PageSize;
        if (hasMore) selected.Remove(selected.Max!);
        var matches = selected.ToArray();
        var allRows = matches.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        if (matches.Length > 0 && passes == 2)
        {
            var chronological = Comparer<TelegramHistoryEntry>.Create(Compare);
            var before = matches.Select(_ => new SortedSet<TelegramHistoryEntry>(chronological)).ToArray();
            var after = matches.Select(_ => new SortedSet<TelegramHistoryEntry>(chronological)).ToArray();
            Scan(reader, snapshotEnd, entry =>
            {
                for (var index = 0; index < matches.Length; index++)
                {
                    var comparison = Compare(entry, matches[index]);
                    if (comparison < 0 && query.ContextBefore > 0)
                    {
                        before[index].Add(entry);
                        if (before[index].Count > query.ContextBefore) before[index].Remove(before[index].Min!);
                    }
                    if (comparison > 0 && query.ContextAfter > 0)
                    {
                        after[index].Add(entry);
                        if (after[index].Count > query.ContextAfter) after[index].Remove(after[index].Max!);
                    }
                }
            }, token, ref scanned);
            foreach (var entry in before.Concat(after).SelectMany(items => items)) allRows.TryAdd(entry.Id, entry);
        }
        token.ThrowIfCancellationRequested();
        if (Generation != generation) return Result(TelegramQueryStatus.StaleCursor);
        string? next = null;
        if (hasMore)
        {
            var last = matches[^1];
            next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new QueryCursor(1, StoreId, generation,
                snapshotEnd, fingerprint, last.TimestampUtc.UtcTicks, last.Id), TelegramHistoryFormat.Json));
        }
        var ids = matches.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var rows = allRows.Values.OrderBy(entry => entry, ordering).Select(entry => new TelegramHistoryRow(entry, ids.Contains(entry.Id))).ToArray();
        return new(Array.AsReadOnly(rows), next, generation, query.RequestGeneration, TelegramQueryStatus.Complete, scanned);
    }

    private void Scan(FileStream reader, long end, Action<TelegramHistoryEntry> visit, CancellationToken token, ref long scanned)
    {
        reader.Position = TelegramHistoryFormat.HeaderSize;
        while (reader.Position < end)
        {
            var start = reader.Position;
            var frame = TelegramHistoryFormat.ReadFrame(reader, end, _options.MaximumFrameBytes, token);
            scanned += reader.Position - start;
            if (frame.Telegram is { } entry) visit(entry);
        }
    }

    private TelegramQuery NormalizeQuery(TelegramQuery query)
    {
        var from = query.FromUtc.ToUniversalTime(); var until = query.UntilUtc.ToUniversalTime();
        if (until <= from) throw new ArgumentException("Query end must be after start.", nameof(query));
        if (query.PageSize is < 1 or > 200 || query.ContextBefore is < 0 or > 20 || query.ContextAfter is < 0 or > 20
            || query.PageSize * (1 + query.ContextBefore + query.ContextAfter) > _options.MaximumResultRows)
            throw new ArgumentOutOfRangeException(nameof(query), "Query result or context limit exceeded.");
        return query with
        {
            FromUtc = from, UntilUtc = until,
            Sources = NormalizeValues(query.Sources), Destinations = NormalizeValues(query.Destinations),
            Services = NormalizeValues(query.Services), Origins = NormalizeValues(query.Origins),
            SourceIds = NormalizeValues(query.SourceIds), ProjectIds = NormalizeValues(query.ProjectIds)
        };
    }

    private static string[] NormalizeValues(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 64) throw new ArgumentException("Too many query filter values.");
        foreach (var value in values) TelegramHistoryFormat.Text(value, 256);
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string QueryFingerprint(TelegramQuery query) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        query with { Cursor = null, RequestGeneration = 0 }, TelegramHistoryFormat.Json)));
    private static bool Matches(TelegramHistoryEntry entry, TelegramQuery query) => entry.TimestampUtc >= query.FromUtc && entry.TimestampUtc < query.UntilUtc
        && (query.IsGroup is null || entry.IsGroup == query.IsGroup)
        && Filter(query.Sources, entry.Source) && Filter(query.Destinations, entry.Destination) && Filter(query.Services, entry.Service)
        && Filter(query.Origins, entry.Origin) && Filter(query.SourceIds, entry.SourceId) && Filter(query.ProjectIds, entry.ProjectId);
    private static bool Filter(IReadOnlyList<string> values, string? value) => values.Count == 0 || value is not null && values.Contains(value, StringComparer.Ordinal);
    private static int Compare(TelegramHistoryEntry left, TelegramHistoryEntry right)
    {
        var result = left.TimestampUtc.CompareTo(right.TimestampUtc);
        return result == 0 ? StringComparer.Ordinal.Compare(left.Id, right.Id) : result;
    }
}
