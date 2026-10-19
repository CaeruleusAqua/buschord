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
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;

// Native windows and routed UI events; only native file-picker results are substituted.
internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "buschord-shell-" + Guid.NewGuid().ToString("N"));
    private static IClassicDesktopStyleApplicationLifetime Lifetime = null!;
    private static MainWindow MainWindow = null!;
    private static ParameterPane Pane = null!;
    private static string ProjectPath = "";
    private static string? OpenPath;
    private static string? SavePath;
    private static MainWindowViewModel Vm => MainWindow.ViewModel;
    private static string SettingsPath => Path.Combine(Root, "settings.json");

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SettingsPath, "{}");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", SettingsPath);
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(Root, "catalog")));
        App.ForcedLanguage = "en";
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => DispatcherTimer.RunOnce(async () => await Run(args), TimeSpan.FromMilliseconds(300)))
            .StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }

    private static object Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
    private static Task Settle() => Task.Delay(200);
    private static void Click(Window window, string text) => window.GetVisualDescendants().OfType<Button>()
        .First(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Answer(string title, string text)
    {
        await Settle();
        var found = Lifetime.Windows.OfType<ConfirmDialog>().SingleOrDefault();
        Check("prompt exists " + title, found is not null);
        var dialog = found!;
        Check("prompt " + title, dialog.Title == title);
        if (title == "Nicht übernommene Parameter")
            Check("draft prompt has only Cancel/Discard", dialog.GetVisualDescendants().OfType<Button>()
                .Select(b => b.Content).SequenceEqual(new object[] { "Cancel", "Entwürfe verwerfen" }));
        Click(dialog, text);
        await Settle();
    }
    private static ParameterItemViewModel Row() => ((IEnumerable<ParameterItemViewModel>)Pane
        .FindControl<ItemsControl>("ParameterList")!.ItemsSource!).Single();
    private static async Task Reset(bool dirty = false)
    {
        Vm.LoadProject(ProjectPath);
        if (dirty) Vm.Edit("synthetic edit", p => p.Name += " edited");
        Vm.SelectedDevice = Vm.Devices[0];
        ((Factory)Field(MainWindow, "_dockFactory")).SetActiveDockable((IDockable)Field(MainWindow, "_docParameters"));
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
        MainWindow = (MainWindow)Lifetime.MainWindow!;
        Pane = (ParameterPane)Field(MainWindow, "_parameters");
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
                MainWindow.Close();
                await Answer("Nicht übernommene Parameter", "Cancel");
                Check("draft-only close cancelled", MainWindow.IsVisible && Pane.HasUnappliedChanges);
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

            var storageField = typeof(TopLevel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(f => f.FieldType == typeof(IStorageProvider));
            _ = MainWindow.StorageProvider;
            storageField.SetValue(MainWindow, Proxy.For<IStorageProvider>((method, _) => method.Name switch
            {
                "OpenFilePickerAsync" => Task.FromResult<IReadOnlyList<IStorageFile>>(OpenPath is null ? [] : [FileAt(OpenPath)]),
                "SaveFilePickerAsync" => Task.FromResult(SavePath is null ? null : FileAt(SavePath)),
                "get_CanOpen" or "get_CanSave" => true,
                _ => throw new NotSupportedException(method.Name),
            }));

            await Reset();
            var original = Vm.Project!;
            Click(MainWindow, "Save"); await Settle();
            Check("regular Save retains draft without persisting it", Pane.HasUnappliedChanges && Row().Value == "42"
                && ProjectQuery.DeviceList(Knxproj.Load(ProjectPath)).Single().ParameterValues.Count == 0
                && Vm.Status.Contains("nicht gespeichert"));
            SavePath = Path.Combine(Root, "saved-as.knxproj");
            MainWindow.GetLogicalDescendants().OfType<MenuItem>().First(m => Equals(m.Header, "Save project as…"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settle();
            Check("regular Save As retains draft without persisting it", Pane.HasUnappliedChanges && Row().Value == "42"
                && Vm.ProjectPath == SavePath && Vm.Status.Contains("nicht gespeichert")
                && ProjectQuery.DeviceList(Knxproj.Load(SavePath)).Single().ParameterValues.Count == 0);
            MainWindow.Close();
            await Answer("Nicht übernommene Parameter", "Cancel");
            Check("draft-only close Cancel keeps window/project/draft", MainWindow.IsVisible && ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges);

            await Reset(true);
            original = Vm.Project!;
            MainWindow.Close();
            await Answer("Nicht übernommene Parameter", "Cancel");
            Check("dirty project + draft: first prompt Cancel preserves both", ReferenceEquals(Vm.Project, original) && Vm.IsDirty && Pane.HasUnappliedChanges);
            MainWindow.Close();
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Cancel");
            Check("dirty project + draft: second prompt Cancel preserves both", ReferenceEquals(Vm.Project, original) && Vm.IsDirty && Pane.HasUnappliedChanges);
            using (var locked = new FileStream(ProjectPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                MainWindow.Close();
                await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
                await Answer("Ungespeicherte Änderungen", "Save");
                Check("failed project Save keeps window/project/draft", MainWindow.IsVisible && ReferenceEquals(Vm.Project, original)
                    && Vm.IsDirty && Pane.HasUnappliedChanges && Vm.Status.StartsWith("Speichern fehlgeschlagen"));
            }
            MainWindow.Close();
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Save");
            Check("both confirmations close without applying drafts", !MainWindow.IsVisible && !Pane.HasUnappliedChanges
                && ProjectQuery.DeviceList(Knxproj.Load(ProjectPath)).Single().ParameterValues.Count == 0);

            MainWindow = new MainWindow(); MainWindow.Show();
            Pane = (ParameterPane)Field(MainWindow, "_parameters");
            _ = MainWindow.StorageProvider;
            storageField.SetValue(MainWindow, storageField.GetValue(Lifetime.MainWindow));
            await Reset(true);
            original = Vm.Project!;
            OpenPath = null;
            Click(MainWindow, "Open"); await Settle();
            Check("cancelled Open picker preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges && Vm.IsDirty);
            OpenPath = corrupt;
            Click(MainWindow, "Open");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Nicht speichern");
            await Answer("Projekt nicht ladbar", "Close");
            Check("failed routed Open preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges && Vm.IsDirty);

            Click(MainWindow, "New");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Nicht speichern");
            Click(Lifetime.Windows.OfType<NewProjectWindow>().Single(), "Cancel"); await Settle();
            Check("cancelled New wizard preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges);
            Click(MainWindow, "New");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Nicht speichern");
            SavePath = null;
            Click(Lifetime.Windows.OfType<NewProjectWindow>().Single(), "Create"); await Settle();
            Check("cancelled New picker preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges);

            Click(MainWindow, "New");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Nicht speichern");
            SavePath = Root;
            Click(Lifetime.Windows.OfType<NewProjectWindow>().Single(), "Create");
            await Answer("Projekt nicht anlegbar", "Close");
            Check("failed routed New preserves project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges && Vm.IsDirty);

            Vm.ProjectPath = "";
            Click(MainWindow, "New");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            SavePath = null;
            await Answer("Ungespeicherte Änderungen", "Save");
            Check("cancelled Save As in second prompt retains project/draft", ReferenceEquals(Vm.Project, original) && Pane.HasUnappliedChanges && Vm.IsDirty);
            Vm.ProjectPath = ProjectPath;

            OpenPath = ProjectPath;
            Click(MainWindow, "Open");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            await Answer("Ungespeicherte Änderungen", "Nicht speichern");
            Check("successful Open clears drafts", !ReferenceEquals(Vm.Project, original) && !Pane.HasUnappliedChanges);
            await Reset();
            Click(MainWindow, "New");
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            SavePath = Path.Combine(Root, "created.knxproj");
            Click(Lifetime.Windows.OfType<NewProjectWindow>().Single(), "Create"); await Settle();
            Check("successful New clears drafts", Vm.ProjectPath == SavePath && !Pane.HasUnappliedChanges && File.Exists(SavePath));

            await Reset();
            var recents = MainWindow.GetLogicalDescendants().OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, Vm.RecentProjects));
            Check("recent list exists", recents is not null);
            recents!.SelectedIndex = 0;
            await Answer("Nicht übernommene Parameter", "Cancel");
            Check("recent Cancel retains draft", Pane.HasUnappliedChanges);
            recents.SelectedIndex = 0;
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            Check("successful recent clears drafts", !Pane.HasUnappliedChanges);
            await Reset();
            MainWindow.GetLogicalDescendants().OfType<MenuItem>().First(m => Equals(m.Header, "Exit"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            Check("draft-only Exit Discard closes", !MainWindow.IsVisible && !Pane.HasUnappliedChanges);
            MainWindow = new MainWindow(); MainWindow.Show();
            Pane = (ParameterPane)Field(MainWindow, "_parameters");
            await Reset();
            Pane.Load(null);
            MainWindow.Close();
            await Answer("Nicht übernommene Parameter", "Entwürfe verwerfen");
            Check("draft-only native close Discard clears hidden draft", !MainWindow.IsVisible && !Pane.HasUnappliedChanges);
            Console.WriteLine("SHELL DRAFT LIFECYCLE OK");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        finally
        {
            foreach (var window in Lifetime.Windows.OfType<MainWindow>()) window.RequestShutdown();
            Lifetime.Shutdown(Environment.ExitCode);
        }
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
