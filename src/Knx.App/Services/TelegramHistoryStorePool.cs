using Knx.Bus.History;

namespace Knx.App.Services;

/// <summary>A process-local writer lease; opening, final disposal and file validation never block the UI.</summary>
public static class TelegramHistoryStorePool
{
    private static readonly SemaphoreSlim Gate = new(1,1);
    private static readonly Dictionary<string,Entry> Entries = new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    private sealed class Entry(ITelegramHistoryStore store) { public ITelegramHistoryStore Store { get; } = store; public int References; }

    public static async Task<Lease> AcquireAsync(string? directory = null, CancellationToken cancellationToken = default)
    {
        var path=Path.GetFullPath(directory??FileTelegramHistoryStore.DefaultDirectory);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if(!Entries.TryGetValue(path,out var entry))
            {
                // Cancellation may abandon the wait, never leave an opened writer unowned.
                var store=await Task.Run<ITelegramHistoryStore>(()=>new FileTelegramHistoryStore(path)).ConfigureAwait(false);
                entry=new(store); Entries.Add(path,entry);
            }
            entry.References++; return new(path,entry.Store);
        }
        finally { Gate.Release(); }
    }

    public sealed class Lease : IAsyncDisposable
    {
        private string? _path;
        public ITelegramHistoryStore Store { get; }
        internal Lease(string path,ITelegramHistoryStore store) { _path=path; Store=store; }
        public async ValueTask DisposeAsync()
        {
            var path=Interlocked.Exchange(ref _path,null); if(path is null)return;
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if(!Entries.TryGetValue(path,out var entry))return;
                if(--entry.References>0)return;
                Entries.Remove(path);
                await Task.Run(entry.Store.Dispose).ConfigureAwait(false);
            }
            finally { Gate.Release(); }
        }
    }
}
