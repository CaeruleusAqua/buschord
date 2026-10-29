using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Workbench;
using Knx.Catalog;
using System.Reflection;
using System.Net;
using Knx.Bus.Connections;
using Knx.Bus.Security;

/// <summary>Real native windows, exclusively synthetic injected vaults. Never accesses an OS credential provider.</summary>
internal static class CredentialVaultNativeChecks
{
    [STAThread]
    public static int Run(string language)
    {
        var root = Directory.CreateTempSubdirectory("BusChord.Credential.Native.").FullName;
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        var path = Path.Combine(root, "synthetic.knxkeys"); File.WriteAllText(path, ProtectedKnxKeyringFixture.Xml);
        var backend = new CredentialSelectionChecks.MemoryVault(); var creations = 0; var owner = new WorkbenchCredentialVaultOwner(() => { creations++; return new(backend); });
        var scope = WorkbenchCredentialVaultOwner.ProjectScope(new Project { Id = "P-credential-ui", Name = "Synthetic", Guid = "00000000-0000-0000-0000-000000000002" }, Path.Combine(root, "saved-project.knxproj"));
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        App.ForcedLanguage = language; App.ForcedTheme = "light"; App.TestWindowFactory = () => new Window { Title = "Synthetic credential UI checks", Width = 420, Height = 240 };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; var shell = desktop.MainWindow!;
            try
            {
                var selector = new SecureConnectionWindow(shell, "BusChord synthetic keyring", scope, owner); var showing = selector.ShowDialog(shell); await Until(() => selector.IsVisible);
                Find<TextBox>(selector, "SecureKeyringPath").Text = path; Click(Find<Button>(selector, "SecureReadSelection")); await selector.OperationCompletion;
                Check("native public selection is unverified and accesses no vault", Find<ComboBox>(selector, "SecureInterfaceChoice").ItemCount == 1 && !Find<Button>(selector, "SecureAccept").IsEnabled && creations == 0 && backend.Reads == 0 && backend.Writes == 0);
                Check("native source fingerprint remains readonly and complete", Find<TextBox>(selector, "SecureKeyringFingerprint") is { IsReadOnly: true, Text.Length: 64 });
                Check("saved-source controls require an explicit capability check", !Find<Button>(selector, "SecureVaultUseStored").IsEnabled && Find<Button>(selector, "SecureVaultProbe").IsEnabled);
                Click(Find<Button>(selector, "SecureVaultProbe")); await selector.OperationCompletion;
                Check("explicit capability check reads no entry and enables source choices", creations == 1 && backend.Probes == 1 && backend.Reads == 0 && Find<Button>(selector, "SecureVaultUseStored").IsEnabled);
                Click(Find<Button>(selector, "SecureVaultUseStored")); await selector.OperationCompletion;
                Check("missing saved entry is visible and opens no fallback prompt", Find<TextBlock>(selector, "SecureProfileStatus").Text == CredentialVaultStrings.Get("VaultNotFound") && !Find<Button>(selector, "SecureAccept").IsEnabled && desktop.Windows.Count(window => window.IsVisible) == 2);
                Click(Find<Button>(selector, "SecureVaultSaveNew")); var firstInput = await CompletePrompt(desktop, shell, selector, prompt =>
                    Check("storage prompt explains the explicit save rather than ephemeral-only use", prompt.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == CredentialVaultStrings.Get("StoragePasswordPrompt")) && prompt.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault).Content?.ToString() == CredentialVaultStrings.Get("ConfirmStorage"))); await selector.OperationCompletion;
                Check("new-save is explicit, signed and uses a cleared masked prompt", backend.Writes == 1 && firstInput.PasswordChar != '\0' && firstInput.Text == "" && Find<Button>(selector, "SecureAccept").IsEnabled);
                Check("saving does not silently choose the stored password source", selector.PasswordSource is PromptSecurePasswordSource);
                var ephemeralRead = selector.PasswordSource.ReadOwnedAsync(default).AsTask(); await CompletePrompt(desktop, shell, selector, prompt =>
                    Check("future ephemeral input retains its truthful non-storage explanation", prompt.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == SecureConnectionStrings.Get("PasswordPrompt")))); var ephemeral = await ephemeralRead; Array.Clear(ephemeral);
                Check("future ephemeral input does not repeat the earlier save", backend.Writes == 1);
                Click(Find<Button>(selector, "SecureVaultSaveNew")); await CompletePrompt(desktop, shell, selector); await selector.OperationCompletion;
                Check("duplicate create displays the separate replacement decision", backend.Writes == 1 && Find<TextBlock>(selector, "SecureProfileStatus").Text == CredentialVaultStrings.Get("VaultAlreadyExists"));
                Click(Find<Button>(selector, "SecureVaultReplace")); await CompletePrompt(desktop, shell, selector); await selector.OperationCompletion;
                Check("matching replacement is a distinct explicit action", backend.Writes == 2 && Find<TextBlock>(selector, "SecureProfileStatus").Text == CredentialVaultStrings.Get("Replaced"));
                Click(Find<Button>(selector, "SecureVaultUseStored")); await selector.OperationCompletion;
                Check("stored source is selected only after verification without a prompt", selector.PasswordSource is VaultSecurePasswordSource && Find<Button>(selector, "SecureAccept").IsEnabled && desktop.Windows.Count(window => window.IsVisible) == 2);
                Check("stored-source control exposes its localized automation name", ControlAutomationPeer.CreatePeerForElement(Find<Button>(selector, "SecureVaultUseStored"))?.GetName() == CredentialVaultStrings.Get("UseStored"));
                Check("native dialog uses the shared application header", !OperatingSystem.IsWindows() || selector.IsExtendedIntoWindowDecorations);
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current.RequestedThemeVariant = theme; selector.Width = 660; selector.Height = 490; await Task.Delay(100);
                    var scroll = Find<ScrollViewer>(selector, "SecureSelectionScroll"); scroll.Offset = new(0, scroll.Extent.Height); await Task.Delay(80);
                    Check("compact scrolling retains accessible footer " + theme, Find<Button>(selector, "SecureAccept").IsEffectivelyVisible && scroll.Extent.Height > scroll.Viewport.Height);
                    Save(selector, Path.Combine(root, "credential-selection-" + language + "-" + theme + ".png"));
                }
                Click(Find<Button>(selector, "SecureAccept")); await showing; await selector.OperationCompletion;
                var source = selector.PasswordSource; var owned = await source.ReadOwnedAsync(default);
                try { Check("shared vault outlives selector and returns a fresh owned password", owned.SequenceEqual("pwd") && backend.LastRead!.All(value => value == 0)); } finally { Array.Clear(owned); }
                var lifetime = new object(); await using (var detachedConnection = new ReadOnlyWorkbenchConnection(lifetime, () => lifetime, _ => throw new InvalidOperationException("No network in binding check.")))
                {
                    var previous = new ReadOnlyConnectionOptions(KnxConnectionProfile.IpSecure(new(IPAddress.Parse("192.0.2.194"), 3671), selector.Selection!, source));
                    var changedScope = new ConnectionWindow(detachedConnection, previous, "BusChord synthetic keyring", scope + "different", owner); var changedShowing = changedScope.ShowDialog(shell); await Until(() => changedScope.IsVisible);
                    Check("cached stored profile cannot carry authorization into another project scope", changedScope.Options is null && Find<ComboBox>(changedScope, "LiveTransportMode").SelectedIndex == 1 && !Find<Button>(changedScope, "LiveConnect").IsEnabled);
                    changedScope.Close(); await changedShowing; await changedScope.OperationCompletion;
                }
                var deleting = new SecureConnectionWindow(shell, "BusChord synthetic keyring", scope, owner); var deleteShowing = deleting.ShowDialog(shell); await Until(() => deleting.IsVisible); Find<TextBox>(deleting, "SecureKeyringPath").Text = path;
                Click(Find<Button>(deleting, "SecureReadSelection")); await deleting.OperationCompletion; Click(Find<Button>(deleting, "SecureVaultProbe")); await deleting.OperationCompletion;
                Click(Find<Button>(deleting, "SecureVaultDelete")); await deleting.OperationCompletion;
                Check("native delete is explicit and invalidates source selection", backend.Deletes == 1 && backend.Values.Count == 0 && !Find<Button>(deleting, "SecureAccept").IsEnabled && Find<TextBlock>(deleting, "SecureProfileStatus").Text == CredentialVaultStrings.Get("Deleted"));
                Click(Find<Button>(deleting, "SecureVaultSaveNew")); await CompletePrompt(desktop, shell, deleting); await deleting.OperationCompletion;
                backend.HoldRead = true; Click(Find<Button>(deleting, "SecureVaultUseStored")); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); deleting.Close(); await Task.Delay(80);
                Check("native close waits for pending vault lookup with dispatcher alive", deleting.IsVisible && !deleteShowing.IsCompleted && !deleting.OperationCompletion.IsCompleted);
                backend.Release.TrySetResult(); await deleteShowing.WaitAsync(TimeSpan.FromSeconds(5)); await deleting.OperationCompletion;
                Check("native close drains cancellation and clears delayed secret bytes", deleting.Selection is null && backend.LastRead!.All(value => value == 0)); backend.HoldRead = false;
                var unsaved = new SecureConnectionWindow(shell, "BusChord synthetic keyring", null, owner); var unsavedShowing = unsaved.ShowDialog(shell); await Until(() => unsaved.IsVisible);
                Check("unsaved-project storage is disabled with a localized explanation", !Find<Button>(unsaved, "SecureVaultProbe").IsEnabled && Find<TextBlock>(unsaved, "SecureVaultState").Text == CredentialVaultStrings.Get("ProjectScopeUnavailable"));
                unsaved.Close(); await unsavedShowing; await unsaved.OperationCompletion;
                await WorkbenchCloseCheck(desktop, shell, root, path, Check);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL credential native exception: " + error); }
            finally
            {
                foreach (var window in desktop.Windows.Where(window => window != shell).ToArray()) window.Close();
                await owner.DisposeAsync(); shell.Close(); Console.WriteLine($"Native credential {language}: {checks} checks, {failures} failures; screenshots {root}"); desktop.Shutdown(failures == 0 ? 0 : 1);
            }
        }, TimeSpan.FromMilliseconds(180))).StartWithClassicDesktopLifetime([]);
        return failures == 0 ? 0 : 1;
    }
    private static async Task<TextBox> CompletePrompt(IClassicDesktopStyleApplicationLifetime desktop, Window shell, Window selector, Action<Window>? inspect = null)
    {
        await Until(() => desktop.Windows.Any(window => window != shell && window != selector && window.IsVisible)); var prompt = desktop.Windows.Last(window => window != shell && window != selector && window.IsVisible);
        inspect?.Invoke(prompt); var input = prompt.GetVisualDescendants().OfType<TextBox>().Single(); input.Text = "pwd"; Click(prompt.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault)); return input;
    }
    private static async Task WorkbenchCloseCheck(IClassicDesktopStyleApplicationLifetime desktop, Window shell, string root, string path, Action<string, bool> check)
    {
        var backend = new CredentialSelectionChecks.MemoryVault(); var owner = new WorkbenchCredentialVaultOwner(() => new(backend));
        var project = WorkbenchJson.CreateDemo(); var projectPath = Path.Combine(root, "workbench-project.knxproj"); var scope = WorkbenchCredentialVaultOwner.ProjectScope(project, projectPath);
        await using (var seed = new CredentialSelectionSession(owner, scope)) { seed.SetSource(path, "BusChord synthetic keyring"); await seed.ReadCandidatesAsync(); await seed.ProbeAsync(); await seed.SaveAsync(new CredentialSelectionChecks.Password("pwd"), SecureCredentialWriteMode.CreateNew); }
        var viewModel = new MainWindowViewModel { Project = project, ProjectPath = projectPath };
        var window = new WorkbenchWindow(viewModel, new(project.Id), Path.Combine(root, "workbench-layout.json"), new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog"), Path.Combine(root, "catalog-index"))), connectionFactory: _ => throw new InvalidOperationException("No network in credential acceptance."), credentialVaultOwner: owner);
        window.Show(shell); await Until(() => window.IsVisible);
        var connectionTask = (Task)typeof(WorkbenchWindow).GetMethod("ShowConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        await Until(() => desktop.Windows.OfType<ConnectionWindow>().Any(value => value.IsVisible)); var connection = desktop.Windows.OfType<ConnectionWindow>().Single(value => value.IsVisible);
        Find<ComboBox>(connection, "LiveTransportMode").SelectedIndex = 1; Click(Find<Button>(connection, "LiveConfigureSecure"));
        await Until(() => desktop.Windows.OfType<SecureConnectionWindow>().Any(value => value.IsVisible)); var selector = desktop.Windows.OfType<SecureConnectionWindow>().Single(value => value.IsVisible);
        Find<TextBox>(selector, "SecureProjectLabel").Text = "BusChord synthetic keyring"; Find<TextBox>(selector, "SecureKeyringPath").Text = path;
        Click(Find<Button>(selector, "SecureReadSelection")); await selector.OperationCompletion; Click(Find<Button>(selector, "SecureVaultProbe")); await selector.OperationCompletion;
        backend.HoldRead = true; Click(Find<Button>(selector, "SecureVaultUseStored")); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); window.RequestShutdown(); window.Close(); await Task.Delay(80);
        var stillOwned = true; try { _ = owner.Vault; } catch (ObjectDisposedException) { stillOwned = false; }
        check("Workbench close joins nested credential dialog before vault disposal", window.IsVisible && !window.ConnectionCloseCompletion.IsCompleted && stillOwned);
        backend.Release.TrySetResult(); await connectionTask.WaitAsync(TimeSpan.FromSeconds(5)); await Until(() => !window.IsVisible);
        var disposed = false; try { _ = owner.Vault; } catch (ObjectDisposedException) { disposed = true; }
        check("Workbench close drains lookup and releases the shared vault last", disposed && backend.LastRead!.All(value => value == 0));
        await owner.DisposeAsync();
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Save(Window window, string path) { using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)); bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default); }
    private static async Task Until(Func<bool> condition) { for (var count = 0; count < 500 && !condition(); count++) await Task.Delay(10); if (!condition()) throw new TimeoutException("Synthetic native deadline."); }
}
