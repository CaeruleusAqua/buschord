namespace Knx.Bus.History;

/// <summary>
/// Local bounded append log. A frame is committed after a durable flush; damaged suffixes are never guessed through.
/// Checksums detect damage, not malicious modification. This store does not initiate bus capture.
/// </summary>
public sealed partial class FileTelegramHistoryStore : ITelegramHistoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TelegramHistoryOptions _options;
    private readonly bool _readOnly;
    private readonly Dictionary<string, TelegramCaptureInterval> _captures = new(StringComparer.Ordinal);
    private FileStream? _writerLease;
    private FileStream? _writer;
    private HistoryHeader _header = null!;
    private long _generation;
    private long _validLength;
    private long _telegramCount;
    private bool _unverifiedTail;
    private string? _recoveryNotice;
    private bool _faulted;
    private bool _disposed;

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BusChord.UserData", "telegram-history");
    public string FilePath { get; }
    public string StoreId => _header.Id.ToString("N");
    public long Generation => Interlocked.Read(ref _generation);
    public TelegramHistoryCapabilities Capabilities => new(true, _readOnly, true, true, true, !_readOnly, true);

    public FileTelegramHistoryStore(string? directory = null, bool readOnly = false, TelegramHistoryOptions? options = null)
    {
        _options = options ?? new();
        ValidateOptions(_options);
        _readOnly = readOnly;
        var root = Path.GetFullPath(directory ?? DefaultDirectory);
        FilePath = Path.Combine(root, "history.bch");
        try
        {
            var damagedSuffix = false;
            if (!readOnly)
            {
                TelegramHistoryStorage.CreateDirectory(root);
                _writerLease = TelegramHistoryStorage.Create(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!File.Exists(FilePath))
                {
                    using var created = TelegramHistoryStorage.Create(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    TelegramHistoryFormat.WriteHeader(created, new(Guid.NewGuid(), 1, null));
                    created.Flush(true);
                }
            }
            using (var reader = OpenReader())
            {
                var snapshotLength = reader.Length;
                if (snapshotLength > _options.MaximumStoreBytes) throw new InvalidDataException("Telegram history exceeds the configured store limit.");
                _header = TelegramHistoryFormat.ReadHeader(reader);
                _generation = _header.Generation;
                _unverifiedTail = _header.Recovered;
                if (_header.Recovered) _recoveryNotice = "An unreadable suffix was previously excluded; preserved history-damaged files contain the original.";
                _validLength = TelegramHistoryFormat.HeaderSize;
                while (reader.Position < snapshotLength)
                {
                    try
                    {
                        var frame = TelegramHistoryFormat.ReadFrame(reader, snapshotLength, _options.MaximumFrameBytes, default);
                        Observe(frame);
                        _validLength = reader.Position;
                    }
                    catch (InvalidDataException)
                    {
                        _unverifiedTail = true;
                        damagedSuffix = true;
                        _recoveryNotice = "An unreadable suffix was found; only the verified prefix is available.";
                        break;
                    }
                }
            }
            // An open interval from a previous process has no verified closing timestamp.
            foreach (var pair in _captures.ToArray())
                if (pair.Value.UntilUtc is null) _captures[pair.Key] = pair.Value with { IsActive = false, EndUnknown = true };
            if (!readOnly)
            {
                if (damagedSuffix) RecoverPrefix(root);
                _writer = OpenWriter();
            }
        }
        catch { _writer?.Dispose(); _writerLease?.Dispose(); throw; }
    }

    public async Task<TelegramHistoryEntry> AppendAsync(TelegramHistoryDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var entry = new TelegramHistoryEntry(1, Guid.NewGuid().ToString("N"), draft.TimestampUtc.ToUniversalTime(),
            draft.Source, draft.Destination, draft.IsGroup, draft.Service, draft.DataHex, draft.Origin, draft.SourceId,
            draft.ProjectId, draft.CaptureId, draft.HopCount, draft.RawCemiHex, draft.Flags, draft.Priority, draft.Acknowledged);
        TelegramHistoryFormat.ValidateEntry(entry);
        await MutateAsync(() =>
        {
            if (_header.RetainedFromUtc is { } retained && entry.TimestampUtc < retained)
                throw new InvalidOperationException("Telegram predates the retained history boundary.");
            if (entry.CaptureId is { } captureId && (!_captures.TryGetValue(captureId, out var capture)
                || !capture.IsActive || capture.SourceId != entry.SourceId || entry.TimestampUtc < capture.FromUtc))
                throw new InvalidOperationException("Telegram does not belong to an active matching capture interval.");
            AppendFrame(new(1, "telegram", entry), cancellationToken);
            _telegramCount++;
        }, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<TelegramCaptureInterval> BeginCaptureAsync(string sourceId, DateTimeOffset fromUtc,
        CancellationToken cancellationToken = default)
    {
        var capture = new TelegramCaptureInterval(Guid.NewGuid().ToString("N"), sourceId, fromUtc.ToUniversalTime(), null, true, false, "");
        TelegramHistoryFormat.ValidateCapture(capture);
        await MutateAsync(() =>
        {
            if (_captures.Count >= _options.MaximumCaptureIntervals) throw new InvalidOperationException("Capture interval limit reached; retain older history first.");
            if (_captures.Values.Any(item => item.IsActive && item.SourceId == sourceId))
                throw new InvalidOperationException("This capture source already has an active interval.");
            if (_header.RetainedFromUtc is { } retained && capture.FromUtc < retained)
                throw new InvalidOperationException("Capture predates the retained history boundary.");
            AppendFrame(new(1, "capture", Capture: capture), cancellationToken);
            _captures.Add(capture.Id, capture);
        }, cancellationToken).ConfigureAwait(false);
        return capture;
    }

    public Task EndCaptureAsync(string captureId, DateTimeOffset untilUtc, string reason, CancellationToken cancellationToken = default)
        => MutateAsync(() =>
        {
            if (!_captures.TryGetValue(captureId, out var capture) || !capture.IsActive)
                throw new InvalidOperationException("Capture interval is not active in this process.");
            var closed = capture with { UntilUtc = untilUtc.ToUniversalTime(), IsActive = false, EndUnknown = false, Reason = reason };
            TelegramHistoryFormat.ValidateCapture(closed);
            AppendFrame(new(1, "capture", Capture: closed), cancellationToken);
            _captures[captureId] = closed;
        }, cancellationToken);

    public async Task<TelegramCoverage> GetCoverageAsync(DateTimeOffset fromUtc, DateTimeOffset untilUtc,
        CancellationToken cancellationToken = default)
    {
        fromUtc = fromUtc.ToUniversalTime(); untilUtc = untilUtc.ToUniversalTime();
        if (untilUtc <= fromUtc) throw new ArgumentException("Coverage end must be after start.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            var intervals = _captures.Values.Where(item => item.FromUtc < untilUtc && (item.UntilUtc is null || item.UntilUtc > fromUtc))
                .OrderBy(item => item.FromUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            // Open intervals have no persisted end. They are displayed, but never advertised as complete coverage.
            var ranges = intervals.Where(item => item.UntilUtc is not null)
                .Select(item => new TelegramTimeRange(Max(Max(item.FromUtc, fromUtc), _header.RetainedFromUtc ?? fromUtc), Min(item.UntilUtc!.Value, untilUtc)))
                .Where(item => item.UntilUtc > item.FromUtc).OrderBy(item => item.FromUtc).ToList();
            var merged = new List<TelegramTimeRange>();
            foreach (var range in ranges)
                if (merged.Count > 0 && range.FromUtc <= merged[^1].UntilUtc)
                    merged[^1] = merged[^1] with { UntilUtc = Max(merged[^1].UntilUtc, range.UntilUtc) };
                else merged.Add(range);
            var gaps = new List<TelegramTimeRange>();
            var next = fromUtc;
            foreach (var range in merged)
            {
                if (range.FromUtc > next) gaps.Add(new(next, range.FromUtc));
                next = Max(next, range.UntilUtc);
            }
            if (next < untilUtc) gaps.Add(new(next, untilUtc));
            return new(fromUtc, untilUtc, Generation, _header.RetainedFromUtc, Array.AsReadOnly(intervals), merged.AsReadOnly(), gaps.AsReadOnly(),
                _unverifiedTail, _recoveryNotice, _telegramCount);
        }
        finally { _gate.Release(); }
    }

    private async Task MutateAsync(Action operation, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfWritable();
            await Task.Run(() => { token.ThrowIfCancellationRequested(); operation(); }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void AppendFrame(HistoryFrame frame, CancellationToken token)
    {
        var bytes = TelegramHistoryFormat.Encode(frame, _options.MaximumFrameBytes);
        if (bytes.LongLength > _options.MaximumStoreBytes - _validLength)
            throw new InvalidOperationException("Telegram history is full; retention is required before recording more data.");
        token.ThrowIfCancellationRequested();
        try
        {
            _writer!.Position = _validLength;
            _writer.Write(bytes);
            _writer.Flush(true);
            _validLength += bytes.Length;
        }
        catch
        {
            _faulted = true; _unverifiedTail = true;
            _recoveryNotice = "A history write failed; recording is stopped until the store is reopened.";
            throw;
        }
    }

    private void Observe(HistoryFrame frame)
    {
        if (frame.Telegram is not null) { _telegramCount++; return; }
        var capture = frame.Capture!;
        if (_captures.TryGetValue(capture.Id, out var previous))
        {
            if (previous.UntilUtc is not null || capture.SourceId != previous.SourceId || capture.FromUtc != previous.FromUtc || capture.UntilUtc is null)
                throw new InvalidDataException("Inconsistent capture transition.");
        }
        else if (_captures.Count >= _options.MaximumCaptureIntervals) throw new InvalidDataException("Capture interval limit exceeded.");
        _captures[capture.Id] = capture;
    }

    private void RecoverPrefix(string directory)
    {
        // Preserve the complete original before replacement. Failure leaves it untouched and refuses writable startup.
        var damagedCopy = Path.Combine(directory, $"history-damaged-{Guid.NewGuid():N}.bch");
        using (var source = OpenReader())
        using (var preserved = TelegramHistoryStorage.Create(damagedCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CopyBounded(source, preserved, source.Length, default);
            preserved.Flush(true);
        }
        var temporary = Path.Combine(directory, $".history-recovery-{Guid.NewGuid():N}.tmp");
        var nextHeader = _header with { Generation = checked(_header.Generation + 1), Recovered = true };
        try
        {
            using (var source = OpenReader())
            using (var target = TelegramHistoryStorage.Create(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                TelegramHistoryFormat.WriteHeader(target, nextHeader);
                source.Position = TelegramHistoryFormat.HeaderSize;
                CopyBounded(source, target, _validLength - TelegramHistoryFormat.HeaderSize, default);
                target.Flush(true);
            }
            File.Move(temporary, FilePath, true);
            _header = nextHeader; _generation = nextHeader.Generation;
            _recoveryNotice = "An unreadable suffix was excluded; the original was preserved in " + Path.GetFileName(damagedCopy) + ".";
        }
        finally { TryDelete(temporary); }
    }

    private FileStream OpenReader() => new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
    private FileStream OpenWriter() => new(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536, FileOptions.None);
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }
    private void ThrowIfWritable()
    {
        ThrowIfDisposed();
        if (_readOnly) throw new InvalidOperationException("Telegram history is read-only.");
        if (_faulted) throw new IOException("History recording is stopped after a write failure. Reopen the store to recover.");
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose(); _writerLease?.Dispose();
        }
        finally { _gate.Release(); }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void CopyBounded(Stream source, Stream target, long bytes, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (bytes > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
            if (read == 0) throw new EndOfStreamException("History changed during copying.");
            target.Write(buffer, 0, read); bytes -= read;
        }
        token.ThrowIfCancellationRequested();
    }

    private static void ValidateOptions(TelegramHistoryOptions options)
    {
        if (options.MaximumStoreBytes is < 4096 or > 64L * 1024 * 1024 * 1024
            || options.MaximumQueryScanBytes is < 1 or > 128L * 1024 * 1024 * 1024
            || options.MaximumFrameBytes is < 512 or > 1024 * 1024
            || options.MaximumCaptureIntervals is < 1 or > 16384 || options.MaximumResultRows is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid history resource limits.");
    }
}
