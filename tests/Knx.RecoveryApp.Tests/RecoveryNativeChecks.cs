using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Recovery;

internal static class RecoveryNativeChecks
{
    private static int _checks, _failures;
    [STAThread]
    public static int Run(string language)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-recovery-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(root, "shared-catalog")));
        var store = new WorkbenchRecoveryStore(Path.Combine(root, "recovery"));
        var stamps = new FailingStamps();
        var service = new AppProjectRecoveryService(store, Path.Combine(root, "recovery-policy.json"), sourceStamps: stamps);
        AppProjectRecoveryService.Current = service;
        var recorder = service.Recorder;
        App.ForcedLanguage = language;
        App.TestWindowFactory = () =>
        {
            var library = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
            var vm = new MainWindowViewModel(library); vm.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null);
            return new WorkbenchWindow(vm, new WorkbenchWorkspace(), Path.Combine(root, "layout.json"), library,
                new ProgrammingStateStore(Path.Combine(root, "programming")));
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var shell = (WorkbenchWindow)lifetime.MainWindow!;
            ProjectRecoveryController? controller = null;
            try
            {
                controller = service.Attach(shell);
                await shell.BindRecoveryCurrentProjectAsync(); await controller.DrainAsync();
                Check("startup is opt-out and writes no project checkpoint", !service.Enabled && (await store.ListAsync()).Items.Count == 0 && !File.Exists(Path.Combine(root, "recovery-policy.json")));
                var settings = new SettingsWindow(shell.ViewModel.Settings); var settingsClosed = settings.ShowDialog(shell); await Task.Delay(70);
                var policy = settings.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "LocalRecoveryBox");
                Check("settings exposes localized opt-in and plaintext disclosure", policy.IsChecked == false
                    && ControlAutomationPeer.CreatePeerForElement(policy)?.GetName() == RecoveryUiStrings.Get("Enable")
                    && settings.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.StartsWith(RecoveryUiStrings.Get("Privacy"), StringComparison.Ordinal) == true));
                policy.IsChecked = true; await Until(() => service.Enabled); settings.Close(); await settingsClosed;
                await controller.DrainAsync();
                Check("explicit opt-in persists once and starts background capture", File.Exists(Path.Combine(root, "recovery-policy.json")) && (await store.ListAsync()).Items.Count == 1);
                await PolicyToggle(shell, controller, store, root);
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current!.RequestedThemeVariant = theme;
                    await Dialog(shell, controller, store, theme.ToString());
                }
                await Lifecycle(shell, controller, service, stamps, root);
            }
            catch (Exception ex) { Console.WriteLine(ex); _failures++; }
            finally
            {
                if (controller is not null) { try { await controller.DisposeAsync(); } catch (Exception ex) { Console.WriteLine(ex); _failures++; } }
                await service.DisposeAsync();
                Check("global shutdown reports previous writer failure without throwing", service.ShutdownFailed);
                shell.RequestShutdown(); lifetime.Shutdown(_failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(300))).StartWithClassicDesktopLifetime([]);
        Console.WriteLine($"RECOVERY NATIVE {language} {_checks} checks, {_failures} failures; evidence {root}");
        return _failures == 0 ? 0 : 1;
    }
    private static void Check(string name, bool ok) { _checks++; if (!ok) _failures++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "lifecycle recovery " + name); }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private static async Task PolicyToggle(WorkbenchWindow owner, ProjectRecoveryController controller, WorkbenchRecoveryStore store, string root)
    {
        var dispatcher = new GatedPolicyDispatcher();
        var path = Path.Combine(root, "popup-policy.json");
        await using var service = new AppProjectRecoveryService(store, path, dispatcher);
        var dialog = new RecoveryWindow(controller, () => Task.FromResult(false), () => Task.CompletedTask, service);
        var closed = dialog.ShowDialog(owner);
        try
        {
            await Task.Delay(70); await dialog.OperationCompletion;
            var policy = dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RecoveryCaptureEnabled");
            policy.IsChecked = true;
            await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check("pending policy change disables a second user toggle", !policy.IsEnabled && policy.IsChecked == true);
            dispatcher.Continue.TrySetResult();
            await dialog.OperationCompletion;
            Check("completed policy change preserves the requested on state", service.Enabled && policy.IsChecked == true && policy.IsEnabled);
            policy.IsChecked = false;
            await dialog.OperationCompletion;
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Check("subsequent off intent matches visible and persisted policy", !service.Enabled && policy.IsChecked == false && policy.IsEnabled
                && !saved.RootElement.GetProperty("Enabled").GetBoolean());
        }
        finally { dispatcher.Continue.TrySetResult(); dialog.Close(); await closed; await dialog.OperationCompletion; }
    }
    private sealed class GatedPolicyDispatcher : IRecoveryDispatcher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> InvokeAsync<T>(Func<T> action)
        {
            Entered.TrySetResult(); await Continue.Task;
            return await new RecoveryDispatcher().InvokeAsync(action);
        }
    }
    private static async Task Lifecycle(WorkbenchWindow owner, ProjectRecoveryController controller, AppProjectRecoveryService service, FailingStamps stamps, string root)
    {
        var currentPath = Path.Combine(root, "current.knxproj"); owner.ViewModel.SaveProjectAs(currentPath); await controller.DrainAsync();
        var replacement = new Project { Id = owner.ViewModel.Project!.Id, Guid = owner.ViewModel.Project.Guid, Name = "same identity B" };
        var path = Path.Combine(root, "replacement.knxproj"); Knxproj.Save(replacement, path);
        stamps.Fail = true;
        var opened = (Task<bool>)typeof(WorkbenchWindow).GetMethod("OpenStartupProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [path])!;
        Check("normal open survives unsupported optional recovery probe", await opened && owner.ViewModel.Project!.Name == replacement.Name && controller.Source is null);
        var before = service.Recorder.Status.CompletedAudits;
        owner.ViewModel.Edit("B edit cannot use A source", project => project.Name = "B-kept");
        try { await controller.DrainAsync(); } catch (IOException) { }
        Check("same-ID/GUID B edit cannot append against old source A", service.Recorder.Status.CompletedAudits == before && controller.Source is null && owner.ViewModel.Project!.Name == "B-kept");
        // Rebind after a legitimate completed save; no recovery failure may stop the native writer.
        owner.ViewModel.SaveProjectAs(path); stamps.Fail = false; await owner.BindRecoveryCurrentProjectAsync(); await controller.DrainAsync();
        var library = new CatalogLibrary(new CatalogStore(Path.Combine(root, "close-catalog")));
        var vm = new MainWindowViewModel(library); var closePath = Path.Combine(root, "close-project.knxproj"); vm.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), closePath);
        var secondary = new WorkbenchWindow(vm, new WorkbenchWorkspace(), Path.Combine(root, "close-layout.json"), library,
            new ProgrammingStateStore(Path.Combine(root, "close-programming")));
        secondary.Show(owner); var other = service.Attach(secondary); await secondary.BindRecoveryCurrentProjectAsync(); await other.DrainAsync();
        using (var writer = new FileStream(Path.Combine(root, "recovery", "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { other.RequestCapture(); try { await other.DrainAsync(); } catch (IOException) { } }
        await Task.Delay(30);
        secondary.Close();
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        Window? prompt = null;
        await Until(() => (prompt = lifetime.Windows.FirstOrDefault(window => window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "RecoveryCloseCancel"))) is not null);
        var cancel = prompt!.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RecoveryCloseCancel");
        Check("writer failure offers a safe default and cancel on close", cancel.IsDefault && cancel.IsCancel);
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => secondary.IsEnabled);
        Check("cancel preserves the window and manually saved file", secondary.IsVisible && File.Exists(closePath) && !vm.IsDirty);
        secondary.Close(); prompt = null;
        await Until(() => (prompt = lifetime.Windows.FirstOrDefault(window => window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "RecoveryCloseAnyway"))) is not null);
        prompt!.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RecoveryCloseAnyway").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !secondary.IsVisible);
        Check("explicit close despite failure completes without a retry loop", !secondary.IsVisible && File.Exists(closePath));
        controller.RequestCapture(); await controller.DrainAsync();
        Check("failed foreign session does not poison current session drain", controller.StatusCode is "Recovery.Queued" or "Recovery.Saved");
    }
    private sealed class FailingStamps : IRecoverySourceStampReader
    {
        public bool Fail;
        public Task<RecoverySourceStamp> ReadAsync(string path) => Fail
            ? Task.FromException<RecoverySourceStamp>(new RecoveryRejectedException(RecoveryFailure.LimitExceeded, "Recovery.LimitExceeded"))
            : RecoverySourceStamp.ReadAsync(path);
    }
    private static async Task Dialog(WorkbenchWindow owner, ProjectRecoveryController controller, WorkbenchRecoveryStore store, string theme)
    {
        void Check(string name, bool ok) { _checks++; if (!ok) _failures++; Console.WriteLine((ok ? "PASS " : "FAIL ") + theme + " recovery " + name); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static T Named<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        var project = Knx.Workbench.WorkbenchJson.CreateDemo(); project.Guid = Guid.NewGuid().ToString("D"); project.Name = "native restore " + theme;
        var undo = new UndoStack(); undo.ResetUnsaved(project);
        var source = ProjectRecoveryController.Unsaved(project); var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, source.UnsavedSessionId!, source));
        var gateCalls = 0; var adopted = 0;
        var dialog = new RecoveryWindow(controller, () => { gateCalls++; return Task.FromResult(true); }, () => { adopted++; return Task.CompletedTask; });
        var closed = dialog.ShowDialog(owner);
        await Task.Delay(100); await dialog.OperationCompletion; await Task.Delay(50);
        var list = Named<ListBox>(dialog, "RecoveryEntries");
        var restore = Named<Button>(dialog, "RecoveryRestore");
        Check("localized title and shared modal chrome", dialog.Title == RecoveryUiStrings.Get("Title") && dialog.Classes.Contains("wb-dialog")
            && dialog.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "DialogChromeHeader"));
        Check("list has accessible localized name", ControlAutomationPeer.CreatePeerForElement(list)?.GetName() == RecoveryUiStrings.Get("Title"));
        Check("metadata never implicitly chooses a source or enables restore", !restore.IsEnabled && Named<TextBlock>(dialog, "RecoverySource").Text == RecoveryUiStrings.Get("SourceRequired"));
        list.SelectedItem = list.Items.OfType<RecoveryListItem>().Single(item => item.Id == checkpoint.Id);
        Check("unsaved row enables explicit source selection", Named<Button>(dialog, "RecoveryChooseUnsaved").IsEnabled && !Named<Button>(dialog, "RecoveryChooseSource").IsEnabled);
        Click(Named<Button>(dialog, "RecoveryChooseUnsaved"));
        Check("unsaved source shows Save As requirement", restore.IsEnabled && Named<TextBlock>(dialog, "RecoverySource").Text == RecoveryUiStrings.Get("UnsavedSaveAs"));
        var previous = owner.ViewModel.Project;
        Click(restore); await dialog.OperationCompletion;
        Check("first activation validates and displays model boundary without adoption", dialog.IsVisible && ReferenceEquals(previous, owner.ViewModel.Project) && gateCalls == 0
            && Named<TextBlock>(dialog, "RecoveryStatus").Text!.Contains(RecoveryUiStrings.Get("ModelOnlyDeclarations")));
        dialog.Width = 490; dialog.Height = 460; await Task.Delay(80);
        Check("footer remains visible in a narrow dialog", restore.IsEffectivelyVisible && restore.TranslatePoint(default, dialog) is { } point
            && point.Y >= 0 && point.Y + restore.Bounds.Height <= dialog.ClientSize.Height + 1);
        Check("default and cancel keyboard semantics retained", restore.IsDefault && Named<Button>(dialog, "RecoveryClose").IsCancel);
        Click(restore); await dialog.OperationCompletion; await closed;
        Check("second activation runs existing gate and adopts complete unsaved session", gateCalls == 1 && adopted == 1 && owner.ViewModel.Project!.Name == project.Name
            && owner.ViewModel.IsDirty && owner.ViewModel.ProjectPath == "");
        await controller.DrainAsync();
        var cancel = new RecoveryWindow(controller, () => Task.FromResult(false), () => Task.CompletedTask);
        var cancelled = cancel.ShowDialog(owner); await Task.Delay(70); await cancel.OperationCompletion;
        var before = owner.ViewModel.Project;
        cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); await cancelled; await cancel.OperationCompletion;
        Check("Escape closes without adopting or altering project", ReferenceEquals(before, owner.ViewModel.Project) && !cancel.IsVisible);
    }
}
