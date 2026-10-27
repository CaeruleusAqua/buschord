using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx;
using Knx.Workbench;

internal static class DialogChecks
{
    private static int failures;
    private static int checks;
    private static string W(string key) => WorkbenchStrings.Get(key);
    private static object? Invoke(object target, string method, params object[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Check(string name, bool ok)
    {
        checks++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }
    private static IEnumerable<T> Controls<T>(Window window) => window.GetVisualDescendants().OfType<T>();
    private static Button Button(Window window, string key) => Controls<Button>(window).First(b => Equals(b.Content, W(key)));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static void Escape(Control control) => control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
    private static object? Resource(Window window, string key)
    {
        window.TryFindResource(key, window.ActualThemeVariant, out var value);
        return value;
    }

    [STAThread]
    public static int Run(string language)
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", "buschord-dialogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{}");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settings);
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(root, "shared-catalog")));
        App.ForcedLanguage = language;
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
            return new WorkbenchWindow(new MainWindowViewModel(catalog), new WorkbenchWorkspace(), null, catalog);
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                var shell = (WorkbenchWindow)lifetime.MainWindow!;
                try { await Test(shell, lifetime); }
                catch (Exception ex) { Console.WriteLine(ex); failures++; }
                finally { lifetime.Shutdown(failures == 0 ? 0 : 1); }
            }, TimeSpan.FromMilliseconds(300)))
            .StartWithClassicDesktopLifetime([]);
        Console.WriteLine($"DIALOG CHECKS: {checks} checks, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static async Task Test(WorkbenchWindow shell, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        shell.ViewModel.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null);
        shell.RefreshWorkspace();
        await Task.Delay(200);
        foreach (var theme in new[] { "light", "dark" })
        {
            SettingsWindow.ApplyTheme(theme);
            var groupCount = ProjectQuery.GroupAddresses(shell.ViewModel.Project!).Count();
            Invoke(shell, "ShowNewGroupAddressDialog");
            await Task.Delay(150);
            var dialog = lifetime.Windows.Single(w => w != shell);
            var fields = Controls<TextBox>(dialog).Where(f => f.Parent is StackPanel).ToArray();
            var picker = Controls<ComboBox>(dialog).Single();
            Check(theme + " U2 fields resolve V4 palette", fields.Length == 3 && fields.All(f => f.Classes.Contains("wb-field"))
                && picker.Classes.Contains("wb-field") && Equals(fields[0].Background, Resource(dialog, "WbPaper")));
            dialog.Height = 260;
            await Task.Delay(150);
            var scroll = Controls<ScrollViewer>(dialog).FirstOrDefault(s => s.Content is StackPanel);
            Check(theme + " U3 constrained body has real scroll extent", scroll is not null && scroll.Extent.Height > scroll.Viewport.Height);
            var create = Button(dialog, "Create");
            var cancel = Button(dialog, "Cancel");
            Check(theme + " U4 safe default/cancel", create.IsDefault && cancel.IsCancel);
            EventHandler<KeyEventArgs> editorEscape = (_, e) => e.Handled = true;
            fields[0].KeyDown += editorEscape;
            Escape(fields[0]);
            Check(theme + " U4 editor-consumed Escape is respected", dialog.IsVisible);
            fields[0].KeyDown -= editorEscape;
            picker.IsDropDownOpen = true;
            await Task.Delay(50);
            Escape(picker);
            Check(theme + " U4 dropdown Escape closes popup, not modal", dialog.IsVisible && !picker.IsDropDownOpen);
            fields[0].Text = "invalid";
            Click(create);
            await Task.Delay(100);
            Check(theme + " U4 invalid GA shown inside modal", Controls<TextBlock>(dialog).Any(t => t.Text == W("InvalidGroupAddress")
                && t.TextWrapping == TextWrapping.Wrap && Equals(t.Foreground, Resource(dialog, "WbError"))));
            var summary = Controls<TextBlock>(dialog).First(t => t.Text == W("InvalidGroupAddress"));
            var summaryPoint = summary.TranslatePoint(default, dialog);
            Check(theme + " U4 error stays visible at short height", summaryPoint is { } position
                && position.Y >= 0 && position.Y + summary.Bounds.Height <= dialog.ClientSize.Height);
            if (scroll is not null)
            {
                picker.BringIntoView();
                await Task.Delay(100);
                var point = picker.TranslatePoint(default, dialog);
                var footer = create.TranslatePoint(default, dialog);
                Check(theme + " U3 last field reachable above sticky footer", point is { } p && footer is { } f
                    && p.Y >= 0 && p.Y + picker.Bounds.Height <= f.Y);
            }
            fields[0].Text = ProjectQuery.NextFreeGroupAddress(shell.ViewModel.Project!, 1, 1).ToString();
            Click(create);
            await Task.Delay(50);
            Check(theme + " U4 failed create reports local model validation", dialog.IsVisible && Controls<TextBlock>(dialog).Any(t =>
                t.Text == W("GroupAddressNeedsName") && Equals(t.Foreground, Resource(dialog, "WbError"))));
            Escape(fields[0]);
            await Task.Delay(100);
            Check(theme + " U4 Escape closes without mutation", !dialog.IsVisible
                && ProjectQuery.GroupAddresses(shell.ViewModel.Project!).Count() == groupCount);
            if (dialog.IsVisible) dialog.Close();

            Invoke(shell, "ShowMonitorValueDialog");
            await Task.Delay(100);
            var monitor = lifetime.Windows.Single(w => w != shell);
            monitor.Height = 240;
            await Task.Delay(100);
            Check(theme + " U3 monitor body scrolls", Controls<ScrollViewer>(monitor).Any(s => s.Content is StackPanel
                && s.Extent.Height > s.Viewport.Height));
            var monitorValue = Controls<TextBox>(monitor).Single(t => t.Parent is StackPanel);
            monitorValue.BringIntoView();
            await Task.Delay(100);
            var valuePoint = monitorValue.TranslatePoint(default, monitor);
            var readPoint = Button(monitor, "SimulateRead").TranslatePoint(default, monitor);
            Check(theme + " U3 monitor value reachable above wrapped footer", valuePoint is { } v && readPoint is { } r
                && v.Y >= 0 && v.Y + monitorValue.Bounds.Height <= r.Y
                && Controls<Button>(monitor).Where(b => b.Classes.Contains("wb-button")).All(b =>
                    b.TranslatePoint(default, monitor) is { } p && p.X >= 0 && p.X + b.Bounds.Width <= monitor.ClientSize.Width));
            Check(theme + " U4 simulation not an implicit default", Controls<Button>(monitor).All(b => !b.IsDefault));
            Check(theme + " U2 monitor fields resolve V4 palette", monitorValue.Classes.Contains("wb-field")
                && Controls<ComboBox>(monitor).Single().Classes.Contains("wb-field")
                && Equals(monitorValue.Background, Resource(monitor, "WbPaper")));
            Controls<ComboBox>(monitor).Single().SelectedIndex = -1;
            Click(Button(monitor, "SimulateRead"));
            await Task.Delay(50);
            Check(theme + " U4 missing monitor target reports inside modal", Controls<TextBlock>(monitor).Any(t =>
                t.Text == W("ChooseGroupAddress") && Equals(t.Foreground, Resource(monitor, "WbError"))));
            monitor.Close();

            Invoke(shell, "OnFloatTool", shell, new RoutedEventArgs());
            await Task.Delay(150);
            var floating = lifetime.Windows.Single(w => w != shell);
            Check(theme + " U1 floating host typography/surface", floating.FontSize == shell.FontSize
                && Equals(floating.FontFamily, shell.FontFamily) && Equals(floating.Background, shell.Background));
            Check(theme + " U1 table caption style actually resolves", Controls<TextBlock>(floating).Where(t => t.Classes.Contains("wb-th"))
                .Any(t => t.FontSize == 10 && t.FontWeight == FontWeight.SemiBold && Equals(t.Foreground, Resource(floating, "WbText"))));
            Click(Button(floating, "Start"));
            await Task.Delay(100);
            Check(theme + " U1 floated demo status uses semantic palette", Controls<TextBlock>(floating).Any(t =>
                t.Classes.Contains("wb-success") && Equals(t.Foreground, Resource(floating, "WbSuccess"))));
            Click(Button(floating, "Stop"));
            floating.Close();

            var project = shell.ViewModel.Project!;
            var tab = shell.Workspace.ActiveTab!;
            var groups = ProjectQuery.GroupAddresses(project).ToArray();
            var pair = WorkbenchProjection.Rows(project, tab).Where(r => r.Object is not null)
                .SelectMany(r => groups.Select(g => (Row: r, Group: g)))
                .First(p => !p.Row.Object!.Links.Contains(p.Group.Id)
                    && WorkbenchProject.ValidateLink(project, new(p.Row.Device.Id, p.Row.Object.Id), p.Group.Id) is null);
            tab.SelectedObjectIds.Add(pair.Row.Key);
            Invoke(shell, "ShowBatchLinkDialog");
            await Task.Delay(150);
            var batch = lifetime.Windows.Single(w => w != shell);
            var groupPicker = Controls<ComboBox>(batch).Single();
            groupPicker.SelectedItem = pair.Group;
            await Task.Delay(100);
            Check(theme + " U5 compatible resolves semantic success", Controls<TextBlock>(batch).Any(t => t.Text == W("BatchCompatible")
                && Equals(t.Foreground, Resource(batch, "WbSuccess"))));
            var incompatible = groups.First(g => WorkbenchProject.ValidateLink(project,
                new(pair.Row.Device.Id, pair.Row.Object!.Id), g.Id) is not null);
            groupPicker.SelectedItem = incompatible;
            await Task.Delay(100);
            Check(theme + " U5 incompatible resolves semantic error", Controls<TextBlock>(batch).Any(t => t.Classes.Contains("wb-error")
                && !string.IsNullOrEmpty(t.Text) && Equals(t.Foreground, Resource(batch, "WbError"))));
            groupPicker.SelectedItem = pair.Group;
            await Task.Delay(50);
            var oldDpt = pair.Group.DatapointType;
            pair.Group.DatapointType = "999.001";
            Click(Button(batch, "LinkAction"));
            await Task.Delay(100);
            Check(theme + " U4 stale apply reports local error", batch.IsVisible && Controls<TextBlock>(batch).Any(t =>
                t.Text == shell.ViewModel.Status && t.TextWrapping == TextWrapping.Wrap
                && Equals(t.Foreground, Resource(batch, "WbError"))) && !pair.Row.Object!.Links.Contains(pair.Group.Id));
            pair.Group.DatapointType = oldDpt;
            batch.Close();
            tab.SelectedObjectIds.Clear();
        }
        var removal = (Task<bool>)Invoke(shell, "ConfirmPortfolioRemoveAsync", shell, "Synthetic")!;
        await Task.Delay(100);
        var danger = lifetime.Windows.Single(w => w != shell);
        danger.Activate();
        await Task.Delay(100);
        Check("U4 destructive starts on cancel, no dangerous default", ReferenceEquals(danger.FocusManager!.GetFocusedElement(), Button(danger, "Cancel"))
            && Controls<Button>(danger).All(b => !b.IsDefault));
        danger.Close();
        await removal;

        var naming = (Task<string?>)Invoke(shell, "PromptTextAsync", "Synthetic name", W("Create"))!;
        await Task.Delay(100);
        var prompt = lifetime.Windows.Single(w => w != shell);
        var editor = Controls<TextBox>(prompt).Single();
        Check("U2 naming field resolves V4 palette", editor.Classes.Contains("wb-field")
            && Equals(editor.Background, Resource(prompt, "WbPaper")));
        editor.Text = "Synthetic";
        editor.AcceptsReturn = true;
        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Task.Delay(100);
        Check("U4 multiline editor keeps Enter", prompt.IsVisible && !naming.IsCompleted);
        editor.AcceptsReturn = false;
        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Task.Delay(100);
        Check("U4 single-line Enter invokes safe default", naming.IsCompleted && !prompt.IsVisible);
        if (prompt.IsVisible) prompt.Close();
        await naming;

        foreach (var (method, arguments) in new (string, object[])[]
        {
            ("PromptPasswordAsync", ["Synthetic password prompt"]),
            ("PromptPortfolioNameAsync", [shell, "Synthetic portfolio"])
        })
        {
            var result = (Task<string?>)Invoke(shell, method, arguments)!;
            await Task.Delay(100);
            var quick = lifetime.Windows.Single(w => w != shell);
            var field = Controls<TextBox>(quick).Single();
            Check("U2 " + method + " resolves V4 palette", field.Classes.Contains("wb-field")
                && Equals(field.Background, Resource(quick, "WbPaper")));
            Escape(field);
            await Task.Delay(50);
            Check("U4 " + method + " Escape cancels", result.IsCompleted && result.Result is null);
            if (quick.IsVisible) quick.Close();
            await result;
        }
    }
}
