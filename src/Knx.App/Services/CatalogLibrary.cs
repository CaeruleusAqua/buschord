using Knx.Catalog;

namespace Knx.App.Services;

/// <summary>How much of the local library has been read, in library files.</summary>
public sealed record CatalogProgress(int Loaded, int Total);

/// <summary>One immutable view of the local catalogue library.</summary>
public sealed record CatalogSnapshot(
    IReadOnlyList<KnxProductCatalog> Catalogs,
    IReadOnlyList<CatalogLoadError> Errors,
    int ProductCount);

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

    /// <summary>Forces a fresh parse; waits for an in-flight load first, then reports the new snapshot.</summary>
    public async Task<CatalogSnapshot> ReloadAsync(IProgress<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task<CatalogSnapshot>? pending;
            lock (_gate) pending = _loading;
            if (pending is null) break;
            try { await pending.ConfigureAwait(false); }
            catch { /* a failed load is simply replaced by the reload below */ }
        }
        lock (_gate) _snapshot = null;
        return await GetAsync(progress, cancellationToken).ConfigureAwait(false);
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
            var progress = new CatalogProgress(value.Loaded, value.Total);
            foreach (var sink in sinks) sink.Report(progress);
        }
    }
}
