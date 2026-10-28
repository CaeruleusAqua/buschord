using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
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
            var contrast = HighContrastChecks.Run(shell);
            checks += contrast.Checks; failures += contrast.Failures;
            var migration = await MigrationDialogChecks.Run(shell);
            checks += migration.Checks; failures += migration.Failures;
            var smart = await SmartLinkDialogChecks.Run(shell);
            checks += smart.Checks; failures += smart.Failures;
            var batchInspector = await BatchInspectorDialogChecks.Run(shell);
            checks += batchInspector.Checks; failures += batchInspector.Failures;
            var renameProject = shell.ViewModel.Project!;
            var renameLine = renameProject.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).First();
            var renameSegment = new Segment { Id = "rename-test-segment", Name = "Testsegment" };
            renameLine.Segments.Add(renameSegment);
            var renameDevice = ProjectQuery.DeviceList(renameProject).First();
            var renameGroup = ProjectQuery.GroupAddresses(renameProject).First();
            foreach (var (method, target, currentName) in new (string, object, string?)[]
            {
                ("RenameLineAsync", renameLine, renameLine.Name),
                ("RenameSegmentAsync", renameSegment, renameSegment.Name),
                ("RenameDeviceAsync", renameDevice, renameDevice.Name),
                ("RenameGroupAddressAsync", renameGroup, renameGroup.Name),
            })
            {
                Check(theme + " rename fixture has a name " + method, !string.IsNullOrEmpty(currentName));
                var renameTask = (Task)Invoke(shell, method, target)!;
                await Task.Delay(100);
                var renameDialog = lifetime.Windows.Single(w => w != shell);
                var nameBox = Controls<TextBox>(renameDialog).Single();
                Check(theme + " rename starts with current name " + method, nameBox.Text == currentName);
                Escape(nameBox);
                await renameTask;
                Check(theme + " cancel rename preserves model " + method, target switch
                {
                    Line value => value.Name == currentName,
                    Segment value => value.Name == currentName,
                    DeviceInstance value => value.Name == currentName,
                    GroupAddressEntry value => value.Name == currentName,
                    _ => false,
                });
            }
            renameLine.Segments.Remove(renameSegment);
            await NavigatorFocusChecks.Run(shell, theme, Check);
            await NavigatorKeyboard(shell, lifetime, theme);
            await PaletteKeyboard(shell, lifetime, theme);
            await NewProjectAccessibilityChecks.Run(shell, theme, Check);
            await ParameterDeviceAccessibilityChecks.Run(shell, theme, Check);
            await ArchiveProtectionDialogChecks.Run(shell, theme, Check);
            await DialogChromeChecks.Run(shell, theme, Check);
            await CatalogDownloadsDialogChecks.Run(shell, theme, Check);
            await MassLinkDialogChecks.Run(shell, theme, Check);
            await UpdateDialogChecks.Run(shell, theme, Check);
            await ParameterPresentationDialogChecks.Run(shell, theme, Check);
            await ParameterImageBudgetChecks.Run(shell, theme, Check);
            await UpdateShutdownChecks.Run(shell, theme, Check);
            await SettingsViewportChecks.Run(shell, lifetime, theme, Check);
            var groupCount = ProjectQuery.GroupAddresses(shell.ViewModel.Project!).Count();
            Invoke(shell, "ShowNewGroupAddressDialog");
            await Task.Delay(150);
            var dialog = lifetime.Windows.Single(w => w != shell);
            var fields = Controls<TextBox>(dialog).Where(f => f.Parent is StackPanel).ToArray();
            var picker = Controls<ComboBox>(dialog).Single();
            Check(theme + " U2 fields resolve V4 palette", fields.Length == 3 && fields.All(f => f.Classes.Contains("wb-field"))
                && picker.Classes.Contains("wb-field") && Equals(fields[0].Background, Resource(dialog, "WbPaper")));
            Check(theme + " U2 group address editors expose localized automation names",
                fields.Select(f => ControlAutomationPeer.CreatePeerForElement(f)?.GetName())
                    .SequenceEqual(new[] { W("GroupAddress"), W("NameLabel"), W("DatapointType") })
                && ControlAutomationPeer.CreatePeerForElement(picker)?.GetName() == W("RoomOptional"));
            Check(theme + " U2 initial name editor focus has an accessible label",
                ReferenceEquals(dialog.FocusManager!.GetFocusedElement(), fields[1])
                && AutomationProperties.GetName(fields[1]) == W("NameLabel"));
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

        var naming = (Task<string?>)Invoke(shell, "PromptTextAsync", "Synthetic name", W("Create"), "")!;
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
            ("PromptPasswordAsync", ["Synthetic password prompt", false]),
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

    private static async Task PaletteKeyboard(WorkbenchWindow shell, IClassicDesktopStyleApplicationLifetime lifetime, string theme)
    {
        var project = shell.ViewModel.Project!;
        var device = ProjectQuery.DeviceList(project).First();
        var oldTabs = shell.Workspace.Tabs.ToHashSet();
        var oldActive = shell.Workspace.ActiveTab;
        var before = Knxproj.ToXml(project).ToString();
        var undo = shell.ViewModel.CanUndo;
        try
        {
            foreach (var origin in new[] { "query", "list", "item", "escape" })
            {
                var previous = shell.OpenTarget(new("project", project.Id));
                var windows = lifetime.Windows.ToHashSet();
                Invoke(shell, "ShowPalette");
                await Task.Delay(100);
                var palette = lifetime.Windows.Single(window => !windows.Contains(window));
                try
                {
                    var query = Controls<TextBox>(palette).Single();
                    var list = Controls<ListBox>(palette).Single();
                    query.Text = $"{device.Address} · {device.Name}";
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    palette.UpdateLayout(); list.UpdateLayout();
                    var item = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
                    var target = origin == "query" ? (Control)query : origin == "list" ? list : item;
                    target.Focus(NavigationMethod.Tab);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    var focused = palette.FocusManager!.GetFocusedElement();
                    Check(theme + " palette has one selected result and actual " + origin + " focus",
                        list.ItemCount == 1 && list.SelectedIndex == 0 && ReferenceEquals(focused, target));
                    var key = origin == "escape" ? Key.Escape : Key.Enter;
                    (focused as Interactive)?.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    var releaseTarget = palette.IsVisible ? palette.FocusManager!.GetFocusedElement() : shell.FocusManager!.GetFocusedElement();
                    (releaseTarget as Interactive)?.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
                    await Task.Delay(80);
                    Check(theme + " palette " + origin + " key closes the actual modal with the expected target",
                        !palette.IsVisible && (origin == "escape" ? ReferenceEquals(shell.Workspace.ActiveTab, previous)
                            : shell.Workspace.ActiveTab?.Target == new WorkbenchTarget("device", device.Id)));
                }
                finally { if (palette.IsVisible) palette.Close(); }
            }
            Check(theme + " palette keyboard navigation preserves project and undo",
                before == Knxproj.ToXml(project).ToString() && undo == shell.ViewModel.CanUndo);
        }
        finally
        {
            foreach (var tab in shell.Workspace.Tabs.Where(tab => !oldTabs.Contains(tab)).ToArray()) shell.Workspace.Close(tab);
            shell.Workspace.ActiveTab = oldActive;
            shell.RefreshWorkspace();
        }
    }

    private static async Task NavigatorKeyboard(WorkbenchWindow shell, IClassicDesktopStyleApplicationLifetime lifetime, string theme)
    {
        var workspace = shell.Workspace;
        var oldPerspective = workspace.Perspective;
        var oldVisible = workspace.NavigatorVisible;
        var oldQuery = workspace.Navigators["groups"].Query;
        var oldTab = workspace.ActiveTab;
        var oldTabs = workspace.Tabs.ToArray();
        var before = Knxproj.ToXml(shell.ViewModel.Project!).ToString();
        var undo = shell.ViewModel.CanUndo;
        var nav = Controls<ListBox>(shell).Single(c => c.Name == "NavTree");
        var search = Controls<TextBox>(shell).Single(c => c.Name == "NavSearch");
        try
        {
            shell.Navigate("groups");
            var groups = ProjectQuery.GroupAddresses(shell.ViewModel.Project!).Take(2).ToArray();
            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                search.Text = group.Name;
                await Task.Delay(100);
                var row = nav.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.GetVisualDescendants()
                    .OfType<Grid>().Any(grid => grid.Tag is WorkbenchNavNode node && ReferenceEquals(node.Model, group)));
                row.Focus(NavigationMethod.Tab);
                await Task.Delay(30);
                var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
                    Key = i == 0 ? Key.F10 : Key.Apps, KeyModifiers = i == 0 ? KeyModifiers.Shift : KeyModifiers.None };
                (shell.FocusManager!.GetFocusedElement() as Interactive)?.RaiseEvent(down);
                await Task.Delay(30);
                // The release follows the actual current focus. Opening on Down
                // sends this Up into the new popup, which closes it immediately.
                var up = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = down.Key, KeyModifiers = down.KeyModifiers };
                (shell.FocusManager!.GetFocusedElement() as Interactive)?.RaiseEvent(up);
                await Task.Delay(80);
                var menu = row.ContextMenu;
                Check(theme + " navigator keyboard context opens on current container " + i,
                    up.Handled && menu is { IsOpen: true, Placement: PlacementMode.Bottom }
                    && menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, W("Rename")))
                    && shell.FocusManager!.GetFocusedElement() is MenuItem);
                if (menu is null) continue;
                if (i == 0)
                {
                    Escape(menu);
                    await Task.Delay(80);
                    Check(theme + " navigator context Escape restores row focus and releases menu",
                        !menu.IsOpen && row.ContextMenu is null && ReferenceEquals(shell.FocusManager!.GetFocusedElement(), row));
                }
                else
                {
                    menu.Close();
                    menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, W("Rename")))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Task.Delay(80);
                    var dialog = lifetime.Windows.Single(w => w != shell);
                    var box = Controls<TextBox>(dialog).Single();
                    Check(theme + " navigator refiltered target routes Rename to its current group", box.Text == group.Name);
                    Escape(box);
                    await Task.Delay(60);
                }
            }
            Check(theme + " keyboard context and cancelled Rename preserve project and history",
                Knxproj.ToXml(shell.ViewModel.Project!).ToString() == before && shell.ViewModel.CanUndo == undo);
        }
        finally
        {
            workspace.Navigators["groups"].Query = oldQuery;
            workspace.Perspective = oldPerspective;
            workspace.NavigatorVisible = oldVisible;
            foreach (var tab in workspace.Tabs.Where(t => !oldTabs.Contains(t)).ToArray()) workspace.Close(tab);
            workspace.ActiveTab = oldTab;
            shell.RefreshWorkspace();
        }
    }
}
