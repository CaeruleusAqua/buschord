using Knx.Catalog;

namespace Knx.App.Services;

/// <summary>How much of the local library has been read, in library files.</summary>
public sealed record CatalogProgress(int Loaded, int Total, string? File = null);

/// <summary>One immutable view of the local catalogue library.</summary>
public sealed record CatalogSnapshot(
    IReadOnlyList<KnxProductCatalog> Catalogs,
    IReadOnlyList<CatalogLoadError> Errors,
    int ProductCount);

/// <summary>Progress of a multi-file import, one step per selected file.</summary>
public sealed record CatalogImportProgress(int Loaded, int Total, string File);

/// <summary>Outcome of one import run; errors are per file and never abort the remaining files.</summary>
public sealed record CatalogImportResult(
    IReadOnlyList<KnxProductCatalog> Catalogs,
    IReadOnlyList<CatalogLoadError> Errors,
    bool Canceled,
    CatalogSnapshot? Snapshot);

/// <summary>
/// Session-wide cached loader for the local catalogue library. Parses the library once on a
/// background thread, hands every caller the same snapshot, and merges imports without parsing
/// the whole library again. No Avalonia types, so it also runs headless; progress callbacks are
/// reported on the loading thread, callers marshal to their UI with <see cref="Progress{T}"/>.
/// </summary>
public sealed class CatalogLibrary
{
    private readonly CatalogStore _store;
    private readonly object _gate = new();
    private readonly List<IProgress<CatalogProgress>> _sinks = [];
    private Task<CatalogSnapshot>? _loading;
    private CatalogSnapshot? _snapshot;

    public CatalogLibrary(CatalogStore? store = null) => _store = store ?? new CatalogStore();

    /// <summary>Process-wide library: every pane and window shares one parse.</summary>
    public static CatalogLibrary Shared { get; } = new();

    public CatalogStore Store => _store;

    /// <summary>Raised after a new snapshot was published (load, reload, import), on the loading thread.</summary>
    public event EventHandler? Changed;

    public CatalogSnapshot? Current
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>
    /// The loaded snapshot; the first call starts <see cref="CatalogStore.LoadAll()"/> on a
    /// background thread, concurrent calls share that one load. Cancelling only stops this caller
    /// from waiting, the shared load keeps going for the other callers.
    /// </summary>
    public Task<CatalogSnapshot> GetAsync(IProgress<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<CatalogSnapshot>(cancellationToken);

        Task<CatalogSnapshot> load;
        lock (_gate)
        {
            if (_snapshot is { } ready) return Task.FromResult(ready);
            if (progress is not null) _sinks.Add(progress);
            load = _loading ??= LoadAsync();
        }
        return cancellationToken.CanBeCanceled ? load.WaitAsync(cancellationToken) : load;
    }

    /// <summary>
    /// Forces a fresh parse; waits for an in-flight load first, then reports the new snapshot.
    /// Cancelling only abandons this waiter: the parse keeps running, the old snapshot stays
    /// readable until the new one is published, and no cached state is discarded.
    /// </summary>
    public async Task<CatalogSnapshot> ReloadAsync(IProgress<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return await Task.FromCanceled<CatalogSnapshot>(cancellationToken);

        while (true)
        {
            Task<CatalogSnapshot>? pending;
            lock (_gate) pending = _loading;
            if (pending is null) break;
            try { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* a failed load is simply replaced by the reload below */ }
        }

        Task<CatalogSnapshot> reload;
        lock (_gate)
        {
            if (progress is not null) _sinks.Add(progress);
            // Do not clear _snapshot: until the new parse publishes, Current keeps serving the
            // last good library, so a cancelled reload cannot leave the pane empty.
            reload = _loading ??= LoadAsync();
        }
        return cancellationToken.CanBeCanceled
            ? await reload.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await reload.ConfigureAwait(false);
    }

    /// <summary>
    /// Imports catalogue files on a background thread, one at a time, reporting progress and
    /// collecting per-file errors. Cancellation stops before the next file; the files already
    /// imported are merged into the snapshot so nothing done so far is lost.
    /// </summary>
    public async Task<CatalogImportResult> ImportAsync(IReadOnlyList<string> paths,
        IProgress<CatalogImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var imported = new List<KnxProductCatalog>();
        var errors = new List<CatalogLoadError>();
        var canceled = false;
        for (var index = 0; index < paths.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) { canceled = true; break; }
            var path = paths[index];
            progress?.Report(new CatalogImportProgress(index + 1, paths.Count, Path.GetFileName(path)));
            try
            {
                imported.Add(await Task.Run(() => _store.Import(path), cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            catch (Exception ex)
            {
                errors.Add(new CatalogLoadError(path, ex.Message));
            }
        }
        return new CatalogImportResult(imported, errors, canceled, Add(imported));
    }

    /// <summary>
    /// Merges freshly imported catalogues into the loaded snapshot, replacing entries with the
    /// same library file. Returns null when nothing is loaded yet (nothing to merge into).
    /// </summary>
    public CatalogSnapshot? Add(IEnumerable<KnxProductCatalog> catalogs)
    {
        var added = catalogs.ToList();
        if (added.Count == 0) return Current;

        CatalogSnapshot? merged = null;
        lock (_gate)
        {
            if (_snapshot is not { } current) return null;
            var replaced = added.Select(catalog => catalog.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = current.Catalogs.Where(catalog => !replaced.Contains(catalog.Path))
                .Concat(added)
                .OrderBy(catalog => catalog.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged = current with { Catalogs = list, ProductCount = list.Sum(catalog => catalog.Products.Count) };
            _snapshot = merged;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return merged;
    }

    private async Task<CatalogSnapshot> LoadAsync()
    {
        try
        {
            // Task.Run keeps the parse off the caller's thread (the UI thread in the app).
            var snapshot = await Task.Run(Load).ConfigureAwait(false);
            lock (_gate) _snapshot = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
            return snapshot;
        }
        finally
        {
            lock (_gate)
            {
                _loading = null;
                _sinks.Clear();
            }
        }
    }

    private CatalogSnapshot Load()
    {
        // The fan-out always exists so a caller that joins later still sees the remaining progress.
        var catalogs = _store.LoadAll(new Fanout(this));
        var errors = _store.LoadErrors.ToList();
        return new CatalogSnapshot(catalogs, errors, catalogs.Sum(catalog => catalog.Products.Count));
    }

    /// <summary>Forwards loader progress to every caller waiting for this load.</summary>
    private sealed class Fanout(CatalogLibrary owner) : IProgress<CatalogLoadProgress>
    {
        public void Report(CatalogLoadProgress value)
        {
            IProgress<CatalogProgress>[] sinks;
            lock (owner._gate) sinks = owner._sinks.ToArray();
            var progress = new CatalogProgress(value.Loaded, value.Total, value.File);
            foreach (var sink in sinks) sink.Report(progress);
        }
    }
}
