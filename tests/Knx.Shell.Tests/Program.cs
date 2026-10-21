using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
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
internal static class Program
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
            Check("draft prompt has only Cancel/Discard", dialog.GetVisualDescendants().OfType<Button>()
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
            if (args.Contains("--transaction")) { Console.WriteLine("TRANSACTION OK"); return; }

            InstallPicker(Shell);
            await Reset();
            var original = Vm.Project!;
            ClickMenu(WorkbenchStrings.Get("MenuSave"));
            await WaitFor(() => Vm.Status == WorkbenchStrings.Get("SavedWithDrafts"), "regular Save status");
            Check("regular Save retains draft without persisting it", Pane.HasUnappliedChanges && Row().Value == "42"
                && ProjectQuery.DeviceList(Knxproj.Load(ProjectPath)).Single().ParameterValues.Count == 0);
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
