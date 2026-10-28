using Knx.App.Services;
using Knx.Updates;

internal static class AppUpdateServiceChecks
{
    internal static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();
    private static async Task RunAsync(Action<string, bool> check)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "buschord-update-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, bool ok) => check("app updates: " + name, ok);
        try
        {
            var file = Path.Combine(root, "preferences.json");
            Check("first install defaults to automatic stable updates", AppUpdatePreferences.Load(file) == new UpdatePreferences());
            AppUpdatePreferences.Save(new(UpdatePolicy.CheckOnly, "preview"), file);
            Check("policy and channel survive a local round trip", AppUpdatePreferences.Load(file) == new UpdatePreferences(UpdatePolicy.CheckOnly, "preview"));
            var before = File.ReadAllBytes(file);
            var invalid = false;
            try { AppUpdatePreferences.Save(new(UpdatePolicy.Automatic, "other"), file); }
            catch (InvalidDataException) { invalid = true; }
            Check("invalid policy scope leaves the previous file byte-identical", invalid && File.ReadAllBytes(file).SequenceEqual(before));
            foreach (var content in new[] { "null", "[]", "{\"Policy\":\"Automatic\",\"Policy\":\"Disabled\"}", "{\"Policy\":7}", "{\"Feed\":\"https://example.org\"}", new string(' ', 16385) })
            {
                File.WriteAllText(file, content);
                var refused = false;
                try { _ = AppUpdatePreferences.Load(file); } catch (InvalidDataException) { refused = true; }
                Check("invalid or oversized local configuration is refused", refused);
            }
            using (var service = new AppUpdateService(file, _ => new FakeClient()))
                Check("a corrupt preference file disables automatic operations", service.Preferences.Policy == UpdatePolicy.Disabled && service.Status == "preferences-invalid");

            AppUpdatePreferences.Save(new(UpdatePolicy.Disabled), file);
            var manual = new FakeClient();
            using (var service = new AppUpdateService(file, _ => manual))
            {
                service.Start(); await Task.Delay(30);
                Check("disabled mode does not schedule a request", manual.Checks == 0);
                await service.CheckNowAsync();
                Check("explicit check in disabled mode cannot download or apply", manual.LastPolicy == UpdatePolicy.CheckOnly && manual.Checks == 1);
                Check("disabled mode never crosses the apply boundary", !await service.TryApplyAsync(false, []) && manual.Applies == 0);
            }

            AppUpdatePreferences.Save(new(UpdatePolicy.Automatic), file);
            var active = new FakeClient { Block = true };
            var replacements = new List<FakeClient>();
            using (var service = new AppUpdateService(file, _ =>
            {
                var client = replacements.Count == 0 ? active : new FakeClient(); replacements.Add(client); return client;
            }))
            {
                service.Start(); await active.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await service.SaveAsync(new(UpdatePolicy.CheckOnly, "preview"));
                Check("changing policy cancels and awaits the previous client before disposal", active.Cancelled && active.Disposed && !active.DisposedWhileRunning);
                Check("the new policy is persisted before the replacement client runs", AppUpdatePreferences.Load(file) == new UpdatePreferences(UpdatePolicy.CheckOnly, "preview")
                    && service.Preferences == new UpdatePreferences(UpdatePolicy.CheckOnly, "preview"));
                await service.StopAsync();
            }

            var blocked = Path.Combine(root, "blocked"); Directory.CreateDirectory(blocked);
            var stable = new FakeClient();
            using (var service = new AppUpdateService(Path.Combine(blocked, "preferences.json"), _ => stable))
            {
                File.WriteAllText(Path.Combine(blocked, "marker"), "keep");
                Directory.Move(blocked, blocked + "-saved"); File.WriteAllText(blocked, "blocked parent");
                var refused = false;
                try { await service.SaveAsync(new(UpdatePolicy.Disabled)); } catch (IOException) { refused = true; }
                Check("a failed save retains the live policy and leaves the blocker unchanged", refused && service.Preferences.Policy == UpdatePolicy.Automatic && File.ReadAllText(blocked) == "blocked parent");
                await service.StopAsync();
            }

            AppUpdatePreferences.Save(new(UpdatePolicy.Automatic), file);
            var delayed = new FakeClient { Block = true, DelayCancellation = true };
            using (var service = new AppUpdateService(file, _ => delayed))
            {
                try
                {
                    service.Start(); await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var saving = service.SaveAsync(new(UpdatePolicy.Disabled));
                    await delayed.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var applying = service.TryApplyAsync(false, []);
                    await Task.Delay(30);
                    var waitedForSave = !applying.IsCompleted && delayed.Applies == 0;
                    delayed.AllowCancellationCompletion.TrySetResult();
                    await saving; var applied = await applying;
                    Check("apply waits for a concurrent preference save", waitedForSave);
                    Check("a just-saved disabled policy wins over a pending exit apply", !applied && delayed.Applies == 0 && service.Preferences.Policy == UpdatePolicy.Disabled);
                }
                finally { delayed.AllowCancellationCompletion.TrySetResult(); }
            }

            AppUpdatePreferences.Save(new(UpdatePolicy.Automatic), file);
            var boundary = new FakeClient();
            using (var service = new AppUpdateService(file, _ => boundary))
            {
                await service.StopAsync();
                service.SuppressApply = true;
                Check("system shutdown suppression prevents applying", !await service.TryApplyAsync(false, []) && boundary.Applies == 0);
                service.SuppressApply = false;
                Check("a safe explicit boundary preserves the restart choice", await service.TryApplyAsync(false, []) && boundary.Applies == 1 && !boundary.Restart);
            }
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (!target.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("buschord-update-app-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup target.");
            Directory.Delete(target, true);
        }
    }

    private sealed class FakeClient : IAppUpdateClient
    {
        public bool Supported => true;
        public bool Block, Cancelled, Disposed, DisposedWhileRunning, Restart, DelayCancellation;
        public int Checks, Applies;
        public UpdatePolicy LastPolicy;
        private bool _running;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCancellationCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? StatusChanged;
        public async Task CheckAsync(UpdatePolicy policy, CancellationToken token)
        {
            Checks++; LastPolicy = policy; _running = true; Started.TrySetResult(); StatusChanged?.Invoke("checking");
            try { if (Block) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Cancelled = true; CancellationSeen.TrySetResult();
                if (DelayCancellation) await AllowCancellationCompletion.Task.ConfigureAwait(false);
                throw;
            }
            finally { _running = false; }
            StatusChanged?.Invoke("up-to-date");
        }
        public Task<bool> ApplyAsync(UpdatePolicy policy, bool restart, string[] arguments)
        { Applies++; Restart = restart; return Task.FromResult(true); }
        public Task RetryAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { DisposedWhileRunning = _running; Disposed = true; }
    }
}
