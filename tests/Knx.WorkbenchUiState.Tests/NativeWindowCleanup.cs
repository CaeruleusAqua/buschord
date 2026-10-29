using Knx.App;

internal static class NativeWindowCleanup
{
    // Fixture teardown deliberately skips user decisions; the lifecycle suites test those gates separately.
    internal static async Task CloseAsync(WorkbenchWindow window)
    {
        if (!window.IsVisible) return;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
        window.Closed += OnClosed;
        try
        {
            window.RequestShutdown(); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { window.Closed -= OnClosed; }
    }
}
