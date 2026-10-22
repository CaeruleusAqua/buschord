using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

// Native Workbench floated-tool lifecycle: a fresh tool window, no live-control
// reparenting, demo traffic that belongs to the session rather than the view,
// re-dock on close, project undo/redo routed by the shell, and clean shutdown.
// No Dock model, no bus/router, no OS keyboard/pointer automation.
internal static class Program
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(), "buschord-workbench-docking-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "settings.json"), "{}");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(Root, "settings.json"));
        App.ForcedLanguage = "en";
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(Root, "catalog")));
            return new WorkbenchWindow(new MainWindowViewModel(catalog), new WorkbenchWorkspace(), catalogLibrary: catalog);
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => DispatcherTimer.RunOnce(async () => await Run(), TimeSpan.FromMilliseconds(300)))
            .StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }

    private static Task Settle(int milliseconds = 400) => Task.Delay(milliseconds);

    private static KeyEventArgs Ctrl(InputElement target, Key key)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control };
        target.RaiseEvent(args);
        return args;
    }

    private static Button ButtonWithText(Control root, string text) =>
        root.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, text));

    private static int MonitorRows(Control view) =>
        view.GetLogicalDescendants().OfType<ListBox>().Single().ItemCount;

    /// <summary>Clicks the shell's float button and returns the newly visible native tool window.</summary>
    private static Window Float(WorkbenchWindow main)
    {
        main.FindControl<Grid>("Tools")!.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content is ConceptIcon { Symbol: "export" })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
            .Windows.Single(w => w != main && w.IsVisible);
    }

    /// <summary>Feeds clearly marked demo telegrams into the live session, as the app's own demo timer does.</summary>
    private static void EmitDemoTraffic(WorkbenchWindow main, int count)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessions = typeof(WorkbenchWindow).GetField("_sessions", flags)!.GetValue(main)!;
        var monitor = sessions.GetType().GetProperty("Monitor")!.GetValue(sessions)!;
        var simulate = monitor.GetType().GetMethod("SimulateWrite")!;
        foreach (var group in ProjectQuery.GroupAddresses(main.ViewModel.Project!).Take(count))
            simulate.Invoke(monitor, [group.Id, "1", null]);
        monitor.GetType().GetMethod("FlushDisplay")!.Invoke(monitor, null);
    }

    private static async Task Run()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (WorkbenchWindow)lifetime.MainWindow!;
        // The test closes the shell itself; the process must survive long enough to observe cleanup.
        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            main.ViewModel.CreateProject(WorkbenchJson.CreateDemo(), null);
            main.RefreshWorkspace();
            main.ShowTool("monitor");
            await Settle();

            var host = main.FindControl<ContentControl>("ToolHost")!;
            var docked = (Control)host.Content!;
            Check("monitor is docked inside the workbench shell", TopLevel.GetTopLevel(docked) == main);

            var floating = Float(main);
            await Settle();
            var floated = (Control)floating.Content!;
            Check("float opens a native tool window", floating.IsVisible && floating != main);
            Check("float hosts a freshly created tool view",
                !ReferenceEquals(floated, docked) && TopLevel.GetTopLevel(floated) == floating);
            Check("docked tool view is not reparented",
                ReferenceEquals(host.Content, docked) && TopLevel.GetTopLevel(docked) == main);

            ButtonWithText(floated, WorkbenchStrings.Get("Start")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            EmitDemoTraffic(main, 3);
            await Settle();
            var floatedRows = MonitorRows(floated);
            Check($"demo monitor traffic is recorded while floated ({floatedRows} rows)", floatedRows == 3);
            Check("docked and floated views share one monitor session", MonitorRows(docked) == floatedRows);

            floating.Close();
            await Settle();
            Check("closing the float window removes it", !floating.IsVisible && lifetime.Windows.All(w => w == main || !w.IsVisible));
            var redocked = (Control)host.Content!;
            Check("closing the float re-docks a fresh tool view",
                !ReferenceEquals(redocked, floated) && !ReferenceEquals(redocked, docked)
                && TopLevel.GetTopLevel(redocked) == main);
            var retained = MonitorRows(redocked);
            Check($"re-docked view retains the recorded telegrams ({retained}/{floatedRows})",
                retained > 0 && retained == floatedRows);
            EmitDemoTraffic(main, 1);
            await Settle();
            Check("monitor session keeps recording after re-dock", MonitorRows(redocked) == retained + 1);
            ButtonWithText(redocked, WorkbenchStrings.Get("Stop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var groupsBefore = ProjectQuery.GroupAddresses(main.ViewModel.Project!).Count();
            Check("project edit records one undo step",
                main.ViewModel.CreateGroupAddress(ProjectQuery.NextFreeGroupAddress(main.ViewModel.Project!, 15, 7),
                    "Docking selftest", "DPST-9-1")
                && main.ViewModel.CanUndo
                && ProjectQuery.GroupAddresses(main.ViewModel.Project!).Count() == groupsBefore + 1);
            main.ShowTool("monitor");
            var undoFloat = Float(main);
            await Settle();
            Console.WriteLine("NOTE floated tool windows forward Ctrl+Z/Ctrl+Y to the shell's single project history");
            Check("floating tool window routes Ctrl+Z to the shell undo",
                Ctrl(undoFloat, Key.Z).Handled
                && ProjectQuery.GroupAddresses(main.ViewModel.Project!).Count() == groupsBefore);
            Check("floating tool window routes Ctrl+Y to the shell redo",
                Ctrl(undoFloat, Key.Y).Handled
                && ProjectQuery.GroupAddresses(main.ViewModel.Project!).Count() == groupsBefore + 1);
            Check("Ctrl+Z on the shell undoes exactly one edit while a tool floats",
                Ctrl(main, Key.Z).Handled
                && ProjectQuery.GroupAddresses(main.ViewModel.Project!).Count() == groupsBefore);
            Check("Ctrl+Y on the shell redoes exactly one edit while a tool floats",
                Ctrl(main, Key.Y).Handled
                && ProjectQuery.GroupAddresses(main.ViewModel.Project!).Count() == groupsBefore + 1);
            await Settle();

            main.RequestShutdown();
            main.Close();
            await Settle();
            Check("closing the shell closes floated tool windows", !undoFloat.IsVisible);
            Console.WriteLine("WORKBENCH DOCKING OK");
            lifetime.Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            main.RequestShutdown();
            Environment.ExitCode = 1;
            lifetime.Shutdown(1);
        }
    }
}
