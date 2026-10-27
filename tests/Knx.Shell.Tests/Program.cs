using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Catalog;

// Native WorkbenchWindow and routed UI events; only native file-picker results are substituted.
internal static partial class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "buschord-shell-" + Guid.NewGuid().ToString("N"));
    private static IClassicDesktopStyleApplicationLifetime Lifetime = null!;
    private static WorkbenchWindow Shell = null!;
    private static ParameterPane Pane = null!;
    private static string ProjectPath = "";
    private static string? OpenPath;
    private static string? SavePath;
    private static MainWindowViewModel Vm => Shell.ViewModel;
    private static string SettingsPath => Path.Combine(Root, "settings.json");
    private static string DiscardTitle => WorkbenchStrings.Get("AskDiscardTitle");
    private static string UnsavedTitle => WorkbenchStrings.Get("AskUnsavedTitle");
    private static string CancelLabel => Strings.Get("BtnCancel");
    private static string DiscardLabel => WorkbenchStrings.Get("DiscardDrafts");
    private static string SaveLabel => WorkbenchStrings.Get("Save");
    private static string DoNotSaveLabel => WorkbenchStrings.Get("DoNotSave");

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SettingsPath, "{}");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", SettingsPath);
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(Root, "catalog")));
        App.ForcedLanguage = "en";
        App.TestWindowFactory = NewShell;
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => DispatcherTimer.RunOnce(async () => await Run(args), TimeSpan.FromMilliseconds(300)))
            .StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }

    private static WorkbenchWindow NewShell()
    {
        var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(Root, "catalog")));
        return new WorkbenchWindow(new MainWindowViewModel(catalog), new WorkbenchWorkspace(), null, catalog);
    }

    private static object Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
    private static Task Settle() => Task.Delay(200);
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new Exception("FAIL wait for " + what);
    }
    private static void Click(Window window, string text) => window.GetVisualDescendants().OfType<Button>()
        .First(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<MenuItem> MenuItems(WorkbenchWindow window)
    {
        IEnumerable<MenuItem> Walk(IEnumerable? items) => items?.OfType<MenuItem>()
            .SelectMany(item => new[] { item }.Concat(Walk(item.ItemsSource))) ?? [];
        return Walk(window.FindControl<Menu>("MainMenu")!.ItemsSource);
    }
    private static void ClickMenu(string header)
    {
        var item = MenuItems(Shell).FirstOrDefault(m => Equals(m.Header, header))
            ?? throw new Exception("FAIL menu item " + header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    private static ParameterPane PaneOf(WorkbenchWindow window) => window.GetLogicalDescendants()
        .OfType<ParameterPane>().Single();
    private static async Task<ConfirmDialog> Prompt(string title)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Lifetime.Windows.OfType<ConfirmDialog>().SingleOrDefault(d => d.Title == title) is { } dialog)
                return dialog;
            await Task.Delay(50);
        }
        throw new Exception("FAIL prompt exists " + title + " (open: "
            + string.Join(", ", Lifetime.Windows.Select(w => w.Title)) + ")");
    }
    private static async Task Answer(string title, string text)
    {
        var dialog = await Prompt(title);
        Check("prompt " + title, dialog.Title == title);
        if (title == DiscardTitle)
            Check("draft decision actions are only Cancel/Discard", dialog.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name != "DialogChromeClose")
                .Select(b => b.Content).ToHashSet().SetEquals(new object[] { CancelLabel, DiscardLabel }));
        Click(dialog, text);
        await WaitFor(() => !Lifetime.Windows.Contains(dialog), "dialog closes: " + title);
        // Let the owning command resume and clear its re-entrancy guard before the next routed action.
        await Settle();
    }
    private static ParameterItemViewModel Row() => Pane.Session.Rows.Single();
    private static async Task Reset(bool dirty = false)
    {
        Vm.LoadProject(ProjectPath);
        if (dirty) Vm.Edit("synthetic edit", p => p.Name += " edited");
        Shell.RefreshWorkspace();
        Vm.SelectedDevice = Vm.Devices[0];
        Pane.Load(Vm.SelectedDevice);
        await Settle();
        Row().Value = "42";
        Check("draft fixture", Pane.HasUnappliedChanges && Vm.IsDirty == dirty);
    }
    private static void Preserved(string name, Project original, DeviceRow selection, object undo, string path)
        => Check(name, ReferenceEquals(Vm.Project, original) && ReferenceEquals(Vm.SelectedDevice, selection)
            && ReferenceEquals(Field(Vm, "_undo"), undo) && Vm.ProjectPath == path && Pane.HasUnappliedChanges
            && Row().Value == "42");

    private static void Failure(string name, Action action)
    {
        var original = Vm.Project!;
        var selected = Vm.SelectedDevice!;
        var undo = Field(Vm, "_undo");
        var path = Vm.ProjectPath;
        var recent = Vm.Settings.RecentProjects.ToArray();
        var last = Vm.Settings.LastProject;
        try { action(); throw new Exception("Expected lifecycle failure: " + name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        Preserved(name, original, selected, undo, path);
        Check(name + " recents intact", recent.SequenceEqual(Vm.Settings.RecentProjects) && last == Vm.Settings.LastProject);
    }

    private static async Task Run(string[] args)
    {
        Lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        Lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Shell = (WorkbenchWindow)Lifetime.MainWindow!;
        Pane = PaneOf(Shell);
        try
        {
            if (args.FirstOrDefault(a => a.StartsWith("--state-", StringComparison.Ordinal)) is { } state)
            {
                await StateRegression(state);
                return;
            }
            // Variant-A shell chrome: the native frame stays, only its drawn caption is replaced.
            var topbar = Shell.FindControl<Border>("Topbar")!;
            var chromeMinimize = Shell.FindControl<Button>("ChromeMinimize")!;
            var chromeMaximize = Shell.FindControl<Button>("ChromeMaximize")!;
            var chromeClose = Shell.FindControl<Button>("ChromeClose")!;
            Check("chrome: variant A settings", Shell.WindowDecorations == WindowDecorations.Full
                && Shell.ExtendClientAreaToDecorationsHint
                && Math.Abs(Shell.ExtendClientAreaTitleBarHeightHint - 49) < 0.1
                && Shell.WindowDecorationsTheme is not null);
            Check("chrome: 49 px strip is the caption region",
                Math.Abs(topbar.Bounds.Height - 49) < 0.5
                && WindowDecorationProperties.GetElementRole(topbar) == WindowDecorationsElementRole.TitleBar
                && WindowDecorationProperties.GetElementRole(Shell.FindControl<Menu>("MainMenu")!) == WindowDecorationsElementRole.User);
            Check("chrome: caption blocks keep native roles and sizes",
                chromeMinimize.Bounds is { Width: 46, Height: 49 }
                && chromeMaximize.Bounds is { Width: 46, Height: 49 }
                && chromeClose.Bounds is { Width: 46, Height: 49 }
                && WindowDecorationProperties.GetElementRole(chromeMinimize) == WindowDecorationsElementRole.MinimizeButton
                && WindowDecorationProperties.GetElementRole(chromeMaximize) == WindowDecorationsElementRole.MaximizeButton
                && WindowDecorationProperties.GetElementRole(chromeClose) == WindowDecorationsElementRole.CloseButton);
            Check("chrome: caption names and custom window menu", AutomationProperties.GetName(chromeMaximize) == WorkbenchStrings.Get("WindowMaximize")
                && AutomationProperties.GetName(chromeClose) == WorkbenchStrings.Get("WindowClose")
                && topbar.ContextMenu is { } windowMenu && windowMenu.Items.OfType<MenuItem>().Count() == 6);

            // Live language switch: an already open window follows Strings.Language without reopening,
            // for bound XAML text and for shell-generated menus, rail, tool tabs and status labels.
            Strings.Language = "de";
            var german = NewShell();
            german.Show();
            var splash = new SplashWindow();
            try
            {
                string MenuHeader(int index) => new List<MenuItem>(
                    ((IEnumerable)german.FindControl<Menu>("MainMenu")!.ItemsSource!).Cast<MenuItem>())[index].Header as string ?? "";
                string PerspectiveTitle() =>
                    (german.FindControl<UniformGrid>("Perspectives")!.Children[0] as Button)?.Content is TextBlock text ? text.Text ?? "" : "";
                string RailTitle() => german.FindControl<StackPanel>("Rail")!.Children.OfType<Grid>()
                    .SelectMany(g => g.Children.OfType<Button>()).Select(b => AutomationProperties.GetName(b) ?? "").FirstOrDefault() ?? "";
                string ToolTabTitle() => AutomationProperties.GetName(
                    german.FindControl<StackPanel>("ToolTabs")!.Children.OfType<TabItem>().First(t => Equals(t.Tag, "changes"))) ?? "";
                bool StatusLine(string text) => german.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == text);
                bool ToolBody(string text) => german.GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, text));
                bool SplashTagline(string text) => splash.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == text);
                void CheckLanguage(string language)
                {
                    Check(language + ": bound XAML label", german.FindControl<TextBox>("NavSearch")!.PlaceholderText == WorkbenchStrings.Get("NavSearchWatermark"));
                    Check(language + ": bound XAML button", Equals(german.FindControl<Button>("CompareButton")!.Content, WorkbenchStrings.Get("ComparePinned")));
                    Check(language + ": bound legacy XAML label", SplashTagline(Strings.Get("SplashTagline")));
                    Check(language + ": bound status line", StatusLine(WorkbenchStrings.Get("StatusLocalOnly")));
                    Check(language + ": generated menu", MenuHeader(0) == WorkbenchStrings.Get("MenuProject"));
                    Check(language + ": generated perspective", PerspectiveTitle() == WorkbenchStrings.Get("PerspectiveBuilding"));
                    Check(language + ": generated rail tooltip and name", RailTitle() == WorkbenchStrings.Get("PerspectiveBuilding"));
                    Check(language + ": generated tool tab", ToolTabTitle() == WorkbenchStrings.Get("Changes"));
                    Check(language + ": generated tool panel", ToolBody(WorkbenchStrings.Get("ClearDisplay")));
                    Check(language + ": bound automation name", AutomationProperties.GetName(german.FindControl<Button>("ExpandAllButton")!)
                        == WorkbenchStrings.Get("ExpandCollapseAllDevices"));
                }
                var workspace = german.Workspace;
                german.Workspace.ToolsVisible = true;
                german.ShowTool("changes");
                CheckLanguage("de");
                Strings.Language = "en";
                CheckLanguage("en");
                Check("live switch keeps the workspace instance", ReferenceEquals(workspace, german.Workspace) && german.IsVisible);
                Strings.Language = "de";
                CheckLanguage("de");
            }
            finally
            {
                Strings.Language = "en";
                splash.Close();
                german.RequestShutdown();
                german.Close();
            }

            var project = new Project { Id = "P-Shell", Name = "Synthetic shell regression" };
            var installation = new Installation { Id = "I" };
            var area = new Area { Id = "A", Address = 1 };
            var line = new Line { Id = "L", Address = 1 };
            line.Devices.Add(new DeviceInstance { Id = "D", Name = "Test", Address = new(1, 1, 1), ProductRefId = "M-000F_A-0001-01-0000" });
            area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
            ProjectPath = Path.Combine(Root, "synthetic.knxproj");
            Knxproj.Save(project, ProjectPath);
            using (var zip = ZipFile.Open(ProjectPath, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open())
                XDocument.Parse("<KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id='N' Name='Number'><TypeNumber Type='unsignedInt' SizeInBit='8' minInclusive='0' maxInclusive='100'/></ParameterType></ParameterTypes><Parameters><Parameter Id='P-N' Name='Brightness' ParameterType='N' Value='25'/></Parameters><ParameterRefs><ParameterRef Id='R-N' RefId='P-N'/></ParameterRefs></Static></ApplicationProgram></KNX>").Save(stream);

            if (args.Contains("--close-only"))
            {
                await Reset();
                Shell.Close();
                await Answer(DiscardTitle, CancelLabel);
                Check("draft-only close cancelled", Shell.IsVisible && Pane.HasUnappliedChanges);
                return;
            }

            await Reset(true);
            var corrupt = Path.Combine(Root, "corrupt.knxproj");
            File.WriteAllText(corrupt, "not a zip");
            Failure("corrupt Open preserves project/selection/path/undo/draft", () => Vm.LoadProject(corrupt));
            Failure("unwritable New preserves project/selection/path/undo/draft", () => Vm.CreateProject(project, Root));
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Root);
            try
            {
                Failure("settings failure Open preserves state", () => Vm.LoadProject(ProjectPath));
                Failure("settings failure New preserves state", () => Vm.CreateProject(project, Path.Combine(Root, "new.knxproj")));
                var pathBeforeSaveAs = Vm.ProjectPath;
                Vm.ProjectPath = "";
                Failure("settings failure Save As preserves path and recents", () => Vm.SaveProjectAs(Path.Combine(Root, "save-as.knxproj")));
                Vm.ProjectPath = pathBeforeSaveAs;
            }
            finally { Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", SettingsPath); }
            InstallPicker(Shell);
            await Reset();
            var priorProject = Vm.Project!;
            var priorUndo = Field(Vm, "_undo");
            var priorSessions = (WorkbenchSessions)Field(Shell, "_sessions");
            priorSessions.Monitor.Start();
            priorSessions.Monitor.TickDemo(new(DateTimeOffset.Now, "1.1.1", "1/1/1",
                "GroupValueWrite", "1.001", "1", SessionOrigin.Demo));
            var recorded = priorSessions.Monitor.RecordedCount;
            var registry = new Knx.Portfolio.PortfolioRegistry(Path.Combine(Root, "open-registry.json"));
            var entry = registry.Add(ProjectPath, "Synthetic portfolio target");
            typeof(WorkbenchWindow).GetField("_portfolio", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Shell, registry);
            void ReplacementPreserved(string name) => Check(name,
                ReferenceEquals(Vm.Project, priorProject) && ReferenceEquals(Field(Vm, "_undo"), priorUndo)
                && ReferenceEquals(Field(Shell, "_sessions"), priorSessions) && priorSessions.Monitor.IsRunning
                && priorSessions.Monitor.RecordedCount >= recorded && Pane.HasUnappliedChanges);
            Pane.Load(null); // Consent also covers drafts hidden by navigation.
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Root);
            try
            {
                OpenPath = ProjectPath; // Valid archive: failure is after parsing, in settings persistence.
                ClickMenu(WorkbenchStrings.Get("MenuOpen"));
                await Answer(DiscardTitle, DiscardLabel);
                await Answer(WorkbenchStrings.Get("ProjectLoadFailed"), Strings.Get("BtnClose"));
                ReplacementPreserved("C2 failed native Open keeps old project, undo, monitor and hidden drafts");
                var portfolioOpen = (Task<bool>)typeof(WorkbenchWindow)
                    .GetMethod("OpenPortfolioEntryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Shell, [entry.Id])!;
                await Answer(DiscardTitle, DiscardLabel);
                await Answer(WorkbenchStrings.Get("ProjectLoadFailed"), Strings.Get("BtnClose"));
                Check("C2 portfolio reports failed adoption", !await portfolioOpen);
                ReplacementPreserved("C2 failed native portfolio Open preserves old work");
                SavePath = Path.Combine(Root, "native-new.knxproj");
                ClickMenu(WorkbenchStrings.Get("MenuNew"));
                await Answer(DiscardTitle, DiscardLabel);
                await WaitFor(() => Lifetime.Windows.OfType<NewProjectWindow>().Any(), "new-project wizard");
                Click(Lifetime.Windows.OfType<NewProjectWindow>().Single(), Strings.Get("BtnCreate"));
                await Answer(WorkbenchStrings.Get("ProjectCreateFailed"), Strings.Get("BtnClose"));
                ReplacementPreserved("C2 failed native New preserves old work");
                Pane.Load(Vm.Devices[0]);
                Check("C2 failed native Open restores exact draft", Row().Value == "42");
            }
            finally { Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", SettingsPath); }
            Pane.Load(null);
            ClickMenu(WorkbenchStrings.Get("MenuOpen"));
            await Answer(DiscardTitle, DiscardLabel);
            await WaitFor(() => !ReferenceEquals(Vm.Project, priorProject), "successful replacement");
            Check("C2 successful replacement clears old hidden drafts and monitor session", !Pane.HasUnappliedChanges
                && !ReferenceEquals(Field(Shell, "_sessions"), priorSessions) && !priorSessions.Monitor.IsRunning);
            await Reset();
            priorProject = Vm.Project!;
            priorUndo = Field(Vm, "_undo");
            priorSessions = (WorkbenchSessions)Field(Shell, "_sessions");
            priorSessions.Monitor.Start();
            recorded = priorSessions.Monitor.RecordedCount;
            Pane.Load(null);
            OpenPath = Path.Combine(Root, "invalid-demo.json");
            File.WriteAllText(OpenPath, "not JSON");
            ClickMenu(WorkbenchStrings.Get("ImportDemoJson"));
            await Answer(DiscardTitle, DiscardLabel);
            await Answer(WorkbenchStrings.Get("DemoImportFailed"), Strings.Get("BtnClose"));
            ReplacementPreserved("C2 failed native import preserves old work");
            ClickMenu(WorkbenchStrings.Get("ResetDemo"));
            await Answer(WorkbenchStrings.Get("AskDemoResetTitle"), WorkbenchStrings.Get("Reset"));
            await Answer(DiscardTitle, CancelLabel);
            ReplacementPreserved("C2 cancelled reset draft consent preserves old work");
            ClickMenu(WorkbenchStrings.Get("ResetDemo"));
            await Answer(WorkbenchStrings.Get("AskDemoResetTitle"), WorkbenchStrings.Get("Reset"));
            await Answer(DiscardTitle, DiscardLabel);
            Check("C2 successful reset clears hidden native drafts and sessions", !Pane.HasUnappliedChanges
                && !ReferenceEquals(Vm.Project, priorProject) && !ReferenceEquals(Field(Shell, "_sessions"), priorSessions));
            if (args.Contains("--transaction")) { Console.WriteLine("TRANSACTION OK"); return; }

            InstallPicker(Shell);
            await Reset();
            var original = Vm.Project!;
            ClickMenu(WorkbenchStrings.Get("MenuSave"));
            await WaitFor(() => Vm.Status == WorkbenchStrings.Get("SavedWithDrafts"), "regular Save status");
            var savedAfterSave = Knxproj.Load(ProjectPath);
            Check("regular Save retains draft without persisting it", Pane.HasUnappliedChanges && Row().Value == "42"
                && ProjectQuery.DeviceList(savedAfterSave).Single().ParameterValues.Count == 0);
            Check("regular Save records a durable revision",
                savedAfterSave.Workbench.Revisions.Count > 0
                && savedAfterSave.Workbench.Revisions[^1].SourcePath == Path.GetFileName(ProjectPath));
            SavePath = Path.Combine(Root, "saved-as.knxproj");
            ClickMenu(WorkbenchStrings.Get("MenuSaveAs"));
            await WaitFor(() => Vm.ProjectPath == SavePath, "Save As path");
            Check("regular Save As retains draft without persisting it", Pane.HasUnappliedChanges && Row().Value == "42"
                && Vm.Status == WorkbenchStrings.Get("SavedWithDrafts")
                && ProjectQuery.DeviceList(Knxproj.Load(SavePath)).Single().ParameterValues.Count == 0);
            Shell.Close();
            await Answer(DiscardTitle, CancelLabel);
            Check("draft-only close Cancel keeps window/project/draft", Shell.IsVisible
                && ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges);

            await Reset(true);
            original = Vm.Project!;
            Shell.Close();
            await Answer(DiscardTitle, CancelLabel);
            Check("dirty project + draft: first prompt Cancel preserves both", ReferenceEquals(Vm.Project, original) && Vm.IsDirty && Pane.HasUnappliedChanges);
            Shell.Close();
            await Answer(DiscardTitle, DiscardLabel);
            await Answer(UnsavedTitle, CancelLabel);
            Check("dirty project + draft: second prompt Cancel preserves both", ReferenceEquals(Vm.Project, original) && Vm.IsDirty && Pane.HasUnappliedChanges);
            using (var locked = new FileStream(ProjectPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Shell.Close();
                await Answer(DiscardTitle, DiscardLabel);
                await Answer(UnsavedTitle, SaveLabel);
                await Answer(WorkbenchStrings.Get("SaveFailed"), Strings.Get("BtnClose"));
                Check("failed project Save keeps window/project/draft", Shell.IsVisible && ReferenceEquals(Vm.Project, original)
                    && Vm.IsDirty && Pane.HasUnappliedChanges
                    && Vm.Status.StartsWith(WorkbenchStrings.Get("SaveFailed") + ":", StringComparison.Ordinal));
            }
            Shell.Close();
            await Answer(DiscardTitle, DiscardLabel);
            await Answer(UnsavedTitle, SaveLabel);
            await WaitFor(() => !Shell.IsVisible, "window closes after Save");
            Check("both confirmations close without applying drafts", !Pane.HasUnappliedChanges
                && ProjectQuery.DeviceList(Knxproj.Load(ProjectPath)).Single().ParameterValues.Count == 0);

            Shell = NewShell(); Shell.Show();
            Pane = PaneOf(Shell);
            InstallPicker(Shell);
            await Settle();
            await Reset(true);
            original = Vm.Project!;
            OpenPath = null;
            ClickMenu(WorkbenchStrings.Get("MenuOpen")); await Settle();
            Check("cancelled Open picker preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges && Vm.IsDirty);
            OpenPath = corrupt;
            ClickMenu(WorkbenchStrings.Get("MenuOpen"));
            await Answer(DiscardTitle, DiscardLabel);
            await Answer(UnsavedTitle, DoNotSaveLabel);
            await Answer(WorkbenchStrings.Get("ProjectLoadFailed"), Strings.Get("BtnClose"));
            Check("failed routed Open preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges && Vm.IsDirty);

            OpenPath = ProjectPath;
            ClickMenu(WorkbenchStrings.Get("MenuOpen"));
            await Answer(DiscardTitle, DiscardLabel);
            await Answer(UnsavedTitle, DoNotSaveLabel);
            await WaitFor(() => !ReferenceEquals(Vm.Project, original), "Open replaces project");
            Check("successful Open clears drafts", !Pane.HasUnappliedChanges);

            await Reset();
            Pane.Load(null);
            Shell.Close();
            await Answer(DiscardTitle, DiscardLabel);
            await WaitFor(() => !Shell.IsVisible, "window closes after hidden draft discard");
            Check("draft-only native close Discard clears hidden draft", !Pane.HasUnappliedChanges);
            Console.WriteLine("SHELL DRAFT LIFECYCLE OK");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        finally
        {
            foreach (var window in Lifetime.Windows.ToArray())
            {
                if (window is WorkbenchWindow workbench) workbench.RequestShutdown();
                window.Close();
            }
            Lifetime.Shutdown(Environment.ExitCode);
        }
    }

    private static readonly FieldInfo StorageField = typeof(TopLevel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(f => f.FieldType == typeof(IStorageProvider));

    private static readonly IStorageProvider Picker = Proxy.For<IStorageProvider>((method, _) => method.Name switch
    {
        "OpenFilePickerAsync" => Task.FromResult<IReadOnlyList<IStorageFile>>(OpenPath is null ? [] : [FileAt(OpenPath)]),
        "SaveFilePickerAsync" => Task.FromResult(SavePath is null ? null : FileAt(SavePath)),
        "get_CanOpen" or "get_CanSave" => true,
        _ => throw new NotSupportedException(method.Name),
    });

    private static void InstallPicker(WorkbenchWindow window)
    {
        _ = window.StorageProvider;
        StorageField.SetValue(window, Picker);
    }

    private static IStorageFile FileAt(string path) => Proxy.For<IStorageFile>((method, _) => method.Name switch
    {
        "get_Path" => new Uri(path), "get_Name" => Path.GetFileName(path),
        _ => throw new NotSupportedException(method.Name),
    });
}

public class Proxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> _invoke = null!;
    public static T For<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = Create<T, Proxy>();
        ((Proxy)(object)proxy)._invoke = invoke;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
}
