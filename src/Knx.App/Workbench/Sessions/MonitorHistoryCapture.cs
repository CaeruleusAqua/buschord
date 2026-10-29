using System.Threading.Channels;
using Knx.App.Services;
using Knx.Bus.History;

namespace Knx.App.Workbench;

public enum MonitorHistoryFailure { None, QueueOverflow, StoreUnavailable, WriteFailed }
public sealed record MonitorHistoryState(bool Running, bool Paused, bool Connected, string SourceId,
    string Origin, string? ProjectId, DateTimeOffset AtUtc);

/// <summary>Bounded ordered mailbox. No file work or blocking wait runs in a receive callback.</summary>
public sealed class MonitorHistoryCapture : IAsyncDisposable
{
    private abstract record Command;
    private sealed record StateCommand(MonitorHistoryState State, bool? Enabled = null) : Command;
    private sealed record FrameCommand(TelegramHistoryDraft Draft, DateTimeOffset ObservedAtUtc) : Command;
    private sealed record Queued(long Sequence, Command Command);
    private readonly Channel<Queued> _commands;
    private readonly object _gate = new();
    private readonly bool _ownsInjectedStore;
    private ITelegramHistoryStore? _store;
    private TelegramHistoryStorePool.Lease? _lease;
    private Task? _opening, _disposing;
    private readonly Task _worker;
    private bool _enabled, _disposed, _workerEnabled;
    private long _accepted, _processed, _faultSequence, _flushTarget;
    private long _dropped, _written, _preAnchor;
    private DateTimeOffset? _faultAt;
    private TaskCompletionSource? _flushCompletion;
    private MonitorHistoryFailure _failure;
    private string? _error;
    private MonitorHistoryState? _state;
    private TelegramCaptureInterval? _capture;
    private DateTimeOffset _lastObserved;

    public MonitorHistoryCapture() : this(null) { }
    public MonitorHistoryCapture(ITelegramHistoryStore? store, int queueCapacity = 2048, bool ownsStore = false)
    {
        if (queueCapacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _store = store; _ownsInjectedStore = ownsStore;
        _commands = Channel.CreateBounded<Queued>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(RunAsync);
    }
    public bool IsEnabled { get { lock (_gate) return _enabled; } }
    public MonitorHistoryFailure Failure { get { lock (_gate) return _failure; } }
    public string? ErrorDetail { get { lock (_gate) return _error; } }
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long WrittenCount => Interlocked.Read(ref _written);
    public long SkippedBeforeAnchorCount => Interlocked.Read(ref _preAnchor);

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opening?.IsFaulted == true) _opening = null;
            task = _store is not null ? Task.CompletedTask : _opening ??= OpenCoreAsync();
        }
        return task.WaitAsync(cancellationToken);
    }
    private async Task OpenCoreAsync()
    {
        try
        {
            var lease = await TelegramHistoryStorePool.AcquireAsync().ConfigureAwait(false);
            lock (_gate) { _lease = lease; _store = lease.Store; }
        }
        catch (Exception ex)
        { lock (_gate) { _failure = MonitorHistoryFailure.StoreUnavailable; _error = ex.Message; } throw; }
    }

    // The monitor serializes state and frame calls with ingestion.
    public void SetEnabled(bool enabled, MonitorHistoryState state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled && _store is null) throw new InvalidOperationException("Open history before enabling capture.");
            if (enabled && _faultAt is not null)
                throw new InvalidOperationException("The history queue is draining after overflow; retry after flushing.");
            _enabled = enabled;
            if (enabled) { _failure = MonitorHistoryFailure.None; _error = null; }
            PostLocked(new StateCommand(state, enabled), state.AtUtc);
        }
    }
    public void ObserveState(MonitorHistoryState state)
    {
        lock (_gate)
        { if (!_disposed && _enabled) PostLocked(new StateCommand(state), state.AtUtc); }
    }
    public void Observe(WorkbenchTelegram telegram, MonitorHistoryState state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_enabled)
            { if (_failure == MonitorHistoryFailure.QueueOverflow) Interlocked.Increment(ref _dropped); return; }
            var draft = new TelegramHistoryDraft(telegram.Timestamp.ToUniversalTime(), telegram.Source, telegram.Destination,
                telegram.IsGroup, telegram.Service, telegram.DataHex, telegram.Origin.ToString(), state.SourceId,
                state.ProjectId, HopCount: telegram.HopCount);
            if (!PostLocked(new FrameCommand(draft, state.AtUtc), state.AtUtc)) Interlocked.Increment(ref _dropped);
        }
    }
    private bool PostLocked(Command command, DateTimeOffset atUtc)
    {
        var sequence = _accepted + 1;
        if (_commands.Writer.TryWrite(new(sequence, command))) { _accepted = sequence; return true; }
        _enabled = false; _failure = MonitorHistoryFailure.QueueOverflow; _error = null;
        // One reserved fault descriptor, independent of queue size; never enqueue unbounded controls.
        if (_faultAt is null) { _faultAt = atUtc; _faultSequence = _accepted; }
        return false;
    }

    /// <summary>Coalesced watermark: every caller waits through all data accepted before its call.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) return (_disposing ?? _worker).WaitAsync(cancellationToken);
            if (_processed >= _accepted && _faultAt is null) return Task.CompletedTask;
            if (_flushCompletion is null || _flushCompletion.Task.IsCompleted)
                _flushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushTarget = _accepted;
            return _flushCompletion.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _commands.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                Queued? queued;
                lock (_gate) { if (!_commands.Reader.TryRead(out queued)) continue; }
                try
                {
                    switch (queued.Command)
                    {
                        case StateCommand state: await StateAsync(state).ConfigureAwait(false); break;
                        case FrameCommand frame:
                            _lastObserved = frame.ObservedAtUtc;
                            if (_capture is null) break;
                            // Buffered pre-opt-in samples keep their original timestamp in the CSV.
                            // They lie outside this capture's anchor and are explicitly counted, never backdated.
                            if (frame.Draft.TimestampUtc < _capture.FromUtc)
                            { Interlocked.Increment(ref _preAnchor); break; }
                            await _store!.AppendAsync(frame.Draft with { CaptureId = _capture.Id }).ConfigureAwait(false);
                            Interlocked.Increment(ref _written); break;
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex); _workerEnabled = false;
                    try { await EndAsync(_lastObserved, "write-failed").ConfigureAwait(false); } catch (Exception) { _capture = null; }
                }
                lock (_gate) _processed = queued.Sequence;
                await CloseOverflowAsync().ConfigureAwait(false);
                CompleteFlush();
            }
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            try { await EndAsync(_lastObserved, "session-ended").ConfigureAwait(false); }
            catch (Exception ex) { Fail(ex); }
            lock (_gate) { _faultAt = null; _flushCompletion?.TrySetResult(); }
        }
    }
    private async Task CloseOverflowAsync()
    {
        DateTimeOffset? atUtc;
        lock (_gate) atUtc = _faultAt is not null && _processed >= _faultSequence ? _faultAt : null;
        if (atUtc is null) return;
        _workerEnabled = false;
        try { await EndAsync(atUtc.Value, "queue-overflow").ConfigureAwait(false); }
        catch (Exception ex) { Fail(ex); }
        finally { lock (_gate) _faultAt = null; }
    }
    private void CompleteFlush()
    {
        lock (_gate)
            if (_processed >= _flushTarget && _faultAt is null) _flushCompletion?.TrySetResult();
    }
    private void Fail(Exception ex)
    { lock (_gate) { _enabled = false; _failure = MonitorHistoryFailure.WriteFailed; _error = ex.Message; } }

    private async Task StateAsync(StateCommand command)
    {
        var next = command.State;
        if (command.Enabled is { } requested) _workerEnabled = requested;
        var active = _workerEnabled && next.Running && !next.Paused && next.Connected;
        if (_capture is not null && (!active || _state?.SourceId != next.SourceId || _state?.Origin != next.Origin || _state?.ProjectId != next.ProjectId))
            await EndAsync(next.AtUtc, !_workerEnabled ? "disabled" : next.Paused ? "paused" : !next.Connected ? "disconnected" : "stopped").ConfigureAwait(false);
        _state = next; _lastObserved = next.AtUtc;
        if (active && _capture is null) _capture = await _store!.BeginCaptureAsync(next.SourceId, next.AtUtc).ConfigureAwait(false);
    }
    private async Task EndAsync(DateTimeOffset atUtc, string reason)
    {
        if (_capture is not { } capture) return;
        lock (_gate) if (_faultAt is { } fault && fault < atUtc) atUtc = fault;
        _capture = null;
        await _store!.EndCaptureAsync(capture.Id, atUtc < capture.FromUtc ? capture.FromUtc : atUtc, reason).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposing is not null) return new(_disposing);
            _disposed = true; _enabled = false; _commands.Writer.TryComplete();
            return new(_disposing = DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_opening is { } opening) try { await opening.ConfigureAwait(false); } catch (Exception) { }
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            if (_lease is { } lease) await lease.DisposeAsync().ConfigureAwait(false);
            else if (_ownsInjectedStore && _store is { } store) await Task.Run(store.Dispose).ConfigureAwait(false);
        }
    }
}
