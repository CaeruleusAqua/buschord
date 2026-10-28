namespace Knx.Bus.History;

public sealed partial class FileTelegramHistoryStore
{
    public async Task<TelegramRetentionResult> RetainFromAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken = default)
    {
        fromUtc = fromUtc.ToUniversalTime();
        TelegramRetentionResult result = null!;
        await MutateAsync(() =>
        {
            if (_header.RetainedFromUtc is { } retained && fromUtc < retained)
                throw new ArgumentException("Retention cannot restore already removed history.", nameof(fromUtc));
            if (_header.RetainedFromUtc == fromUtc) { result = new(0, fromUtc, Generation); return; }
            var nextHeader = _header with { Generation = checked(Generation + 1), RetainedFromUtc = fromUtc };
            var captures = _captures.Values.Where(item => item.IsActive || item.UntilUtc is { } until && until > fromUtc
                    || item.UntilUtc is null && item.FromUtc >= fromUtc)
                .OrderBy(item => item.FromUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            var temporary = Path.Combine(Path.GetDirectoryName(FilePath)!, $".history-retain-{Guid.NewGuid():N}.tmp");
            long kept = 0;
            long nextLength = 0;
            try
            {
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var source = OpenReader())
                {
                    TelegramHistoryFormat.WriteHeader(target, nextHeader);
                    foreach (var capture in captures)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteRetainedFrame(target, new(1, "capture", Capture: capture));
                    }
                    source.Position = TelegramHistoryFormat.HeaderSize;
                    while (source.Position < _validLength)
                    {
                        var frame = TelegramHistoryFormat.ReadFrame(source, _validLength, _options.MaximumFrameBytes, cancellationToken);
                        if (frame.Telegram is not { } entry || entry.TimestampUtc < fromUtc) continue;
                        WriteRetainedFrame(target, frame);
                        kept++;
                    }
                    nextLength = target.Length;
                    target.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Replacement is one same-directory rename. Keep the writer lease throughout the change.
                _writer!.Dispose(); _writer = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, FilePath, true);
                    var removed = _telegramCount - kept;
                    _header = nextHeader;
                    Interlocked.Exchange(ref _generation, nextHeader.Generation);
                    _telegramCount = kept;
                    _captures.Clear();
                    foreach (var capture in captures) _captures.Add(capture.Id, capture);
                    _validLength = nextLength;
                    result = new(removed, fromUtc, nextHeader.Generation);
                }
                finally
                {
                    try { _writer = OpenWriter(); }
                    catch { _faulted = true; throw; }
                }
            }
            finally { TryDelete(temporary); }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task BackupAsync(string destinationFile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var destination = Path.GetFullPath(destinationFile);
        if (string.Equals(destination, FilePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Backup destination must differ from the active history file.", nameof(destinationFile));
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".history-backup-{Guid.NewGuid():N}.tmp");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Task.Run(() =>
            {
                try
                {
                    using (var source = OpenReader())
                    using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        CopyBounded(source, target, _validLength, cancellationToken);
                        target.Flush(true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, false);
                }
                finally { TryDelete(temporary); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void WriteRetainedFrame(Stream target, HistoryFrame frame)
    {
        var bytes = TelegramHistoryFormat.Encode(frame, _options.MaximumFrameBytes);
        if (bytes.LongLength > _options.MaximumStoreBytes - target.Position)
            throw new IOException("Retained history exceeds its configured size limit.");
        target.Write(bytes);
    }
}
