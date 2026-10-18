namespace Knx.App.Services;

/// <summary>
/// Coalesces refresh requests so a burst of telegrams produces at most one pending UI callback.
/// The caller injects the post mechanism (the Avalonia dispatcher in production, a manual queue in
/// tests) and the refresh body. UI-free so the behaviour is testable without a running dispatcher.
/// </summary>
public sealed class MonitorRefreshQueue
{
    private readonly Action<Action> _post;
    private readonly Action _refresh;
    private readonly object _gate = new();
    private bool _queued;
    private bool _detached;

    public MonitorRefreshQueue(Action<Action> post, Action refresh)
    {
        _post = post;
        _refresh = refresh;
    }

    /// <summary>Marks the view dirty; posts a callback only when none is pending.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_detached || _queued) return;
            _queued = true;
        }
        try
        {
            _post(Run);
        }
        catch (Exception)
        {
            // A dispatcher that no longer accepts work (shutdown) must not break the bus thread.
            lock (_gate) _queued = false;
        }
    }

    /// <summary>Stops future posts; a callback that is already queued becomes a no-op.</summary>
    public void Detach()
    {
        lock (_gate) _detached = true;
    }

    private void Run()
    {
        lock (_gate)
        {
            if (_detached) return;
            // Cleared before refreshing: a request that arrives during the refresh posts again
            // instead of being lost.
            _queued = false;
        }
        _refresh();
    }
}
