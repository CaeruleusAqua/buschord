using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Updates;

internal static class UpdateDialogChecks
{
    internal static async Task Run(WorkbenchWindow owner, string theme, Action<string, bool> check)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "buschord-update-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var language = Strings.Language;
        var xml = Knxproj.ToXml(owner.ViewModel.Project!).ToString();
        var undo = owner.ViewModel.CanUndo;
        var clients = new List<Client>();
        using var service = new AppUpdateService(Path.Combine(root, "updates.json"), _ =>
        { var client = new Client(); clients.Add(client); return client; });
        var dialog = new UpdateWindow(service);
        var closed = dialog.ShowDialog(owner);
        void Check(string name, bool ok) => check(theme + " updates: " + name, ok);
        T Named<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static async Task Settle() { await Task.Delay(70); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
        static async Task Until(Func<bool> value)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!value()) await Task.Delay(20, deadline.Token);
            await Settle();
        }
        try
        {
            await Settle();
            var mode = Named<ComboBox>("UpdatePolicyBox"); var channel = Named<ComboBox>("UpdateChannelBox");
            var save = Named<Button>("SaveUpdatePreferences"); var checkNow = Named<Button>("CheckUpdatesButton");
            Check("opening has no implicit network or configuration write", clients.All(c => c.Checks == 0) && !File.Exists(Path.Combine(root, "updates.json")));
            Check("defaults visibly select automatic stable", mode.SelectedIndex == 0 && channel.SelectedIndex == 0);
            Check("policy and channel have localized accessible names", ControlAutomationPeer.CreatePeerForElement(mode)?.GetName() == UpdateUiStrings.Get("Mode")
                && ControlAutomationPeer.CreatePeerForElement(channel)?.GetName() == UpdateUiStrings.Get("Channel"));
            Check("caption shares the owner theme", dialog.Title == UpdateUiStrings.Get("Title") && dialog.Classes.Contains("wb-dialog"));
            mode.SelectedIndex = 2; channel.SelectedIndex = 1;
            Strings.Language = language == "fr" ? "de" : "fr";
            await Settle();
            Check("live language changes keep unsaved policy and channel", mode.SelectedIndex == 2 && channel.SelectedIndex == 1
                && dialog.Title == UpdateUiStrings.Get("Title") && Equals(save.Content, Strings.Get("BtnSaveSettings")));
            Click(save);
            await Until(() => service.Preferences.Policy == UpdatePolicy.Disabled && save.IsEnabled);
            Check("save persists only policy and channel", AppUpdatePreferences.Load(Path.Combine(root, "updates.json")) == new UpdatePreferences(UpdatePolicy.Disabled, "preview")
                && !File.ReadAllText(Path.Combine(root, "updates.json")).Contains("Feed", StringComparison.Ordinal));
            Check("disabled status is visible and translated", Named<TextBlock>("UpdateStatusText").Text == UpdateUiStrings.Get("DisabledState"));
            Click(checkNow);
            await Until(() => clients[^1].Checks == 1 && checkNow.IsEnabled);
            Check("manual check in disabled mode uses check-only", clients[^1].Policy == UpdatePolicy.CheckOnly
                && Named<TextBlock>("UpdateStatusText").Text == UpdateUiStrings.Get("UpToDate"));
            mode.SelectedIndex = 0;
            Click(Named<Button>("CloseUpdatesButton")); await closed;
            Check("closing preserves the saved preference and never applies a version", AppUpdatePreferences.Load(Path.Combine(root, "updates.json")).Policy == UpdatePolicy.Disabled
                && clients.All(c => c.Applies == 0));
            Check("update preferences leave project and undo unchanged", Knxproj.ToXml(owner.ViewModel.Project!).ToString() == xml && owner.ViewModel.CanUndo == undo);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close(); await closed;
            Strings.Language = language;
            await service.StopAsync();
            var target = Path.GetFullPath(root);
            if (!target.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("buschord-update-dialog-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected update test directory.");
            Directory.Delete(target, true);
        }
    }

    private sealed class Client : IAppUpdateClient
    {
        public bool Supported => true;
        public int Checks, Applies;
        public UpdatePolicy Policy;
        public event Action<string>? StatusChanged;
        public Task CheckAsync(UpdatePolicy policy, CancellationToken token)
        { Policy = policy; Checks++; StatusChanged?.Invoke("up-to-date"); return Task.CompletedTask; }
        public Task<bool> ApplyAsync(UpdatePolicy policy, bool restart, string[] arguments)
        { Applies++; return Task.FromResult(true); }
        public Task RetryAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
}
