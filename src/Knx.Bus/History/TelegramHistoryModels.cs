namespace Knx.Bus.History;

public sealed record TelegramHistoryDraft(DateTimeOffset TimestampUtc, string Source, string Destination,
    bool IsGroup, string Service, string DataHex, string Origin = "", string SourceId = "",
    string? ProjectId = null, string? CaptureId = null, byte? HopCount = null,
    string? RawCemiHex = null, string? Flags = null, string? Priority = null, bool? Acknowledged = null)
{
    public static TelegramHistoryDraft FromRecorded(RecordedTelegram entry, string sourceId = "", string origin = "",
        string? projectId = null, string? captureId = null) => new(entry.Timestamp.ToUniversalTime(), entry.Source,
        entry.Destination, entry.IsGroupAddress, entry.Apci, entry.Data, origin, sourceId, projectId, captureId, entry.HopCount);
}

/// <summary>Immutable versioned data. Payload and explicitly supplied raw cEMI are separate fields.</summary>
public sealed record TelegramHistoryEntry(int Schema, string Id, DateTimeOffset TimestampUtc, string Source,
    string Destination, bool IsGroup, string Service, string DataHex, string Origin, string SourceId,
    string? ProjectId, string? CaptureId, byte? HopCount, string? RawCemiHex, string? Flags, string? Priority, bool? Acknowledged);

public sealed record TelegramQuery
{
    public DateTimeOffset FromUtc { get; init; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset UntilUtc { get; init; } = DateTimeOffset.MaxValue;
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> Destinations { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<string> Origins { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public IReadOnlyList<string> ProjectIds { get; init; } = [];
    public bool? IsGroup { get; init; }
    public bool NewestFirst { get; init; }
    public int PageSize { get; init; } = 100;
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public string? Cursor { get; init; }
    public long RequestGeneration { get; init; }
}

public enum TelegramQueryStatus { Complete, StaleCursor, ScanLimit }
public sealed record TelegramHistoryRow(TelegramHistoryEntry Entry, bool IsMatch);
public sealed record TelegramQueryPage(IReadOnlyList<TelegramHistoryRow> Rows, string? NextCursor,
    long StoreGeneration, long RequestGeneration, TelegramQueryStatus Status, long ScannedBytes)
{
    public int MatchedCount => Rows.Count(row => row.IsMatch);
}
public sealed record TelegramHistoryCapabilities(bool Persistent, bool ReadOnly, bool MultiValueFilters,
    bool Context, bool Backup, bool Retention, bool SnapshotPagination);
public sealed record TelegramCaptureInterval(string Id, string SourceId, DateTimeOffset FromUtc,
    DateTimeOffset? UntilUtc, bool IsActive, bool EndUnknown, string Reason);
public sealed record TelegramTimeRange(DateTimeOffset FromUtc, DateTimeOffset UntilUtc);
public sealed record TelegramCoverage(DateTimeOffset FromUtc, DateTimeOffset UntilUtc, long Generation,
    DateTimeOffset? RetainedFromUtc, IReadOnlyList<TelegramCaptureInterval> CaptureIntervals,
    IReadOnlyList<TelegramTimeRange> KnownCaptured, IReadOnlyList<TelegramTimeRange> Gaps,
    bool HasUnverifiedTail, string? RecoveryNotice, long StoredTelegrams);
public sealed record TelegramRetentionResult(long RemovedTelegrams, DateTimeOffset RetainedFromUtc, long Generation);

public sealed record TelegramHistoryOptions
{
    public long MaximumStoreBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumQueryScanBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumFrameBytes { get; init; } = 32768;
    public int MaximumCaptureIntervals { get; init; } = 4096;
    public int MaximumResultRows { get; init; } = 4096;
}

public interface ITelegramHistoryStore : IDisposable
{
    TelegramHistoryCapabilities Capabilities { get; }
    string StoreId { get; }
    long Generation { get; }
    Task<TelegramHistoryEntry> AppendAsync(TelegramHistoryDraft draft, CancellationToken cancellationToken = default);
    Task<TelegramQueryPage> QueryAsync(TelegramQuery query, CancellationToken cancellationToken = default);
    Task<TelegramCoverage> GetCoverageAsync(DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken = default);
    Task<TelegramCaptureInterval> BeginCaptureAsync(string sourceId, DateTimeOffset fromUtc, CancellationToken cancellationToken = default);
    Task EndCaptureAsync(string captureId, DateTimeOffset untilUtc, string reason, CancellationToken cancellationToken = default);
    Task<TelegramRetentionResult> RetainFromAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken = default);
    Task BackupAsync(string destinationFile, CancellationToken cancellationToken = default);
}
