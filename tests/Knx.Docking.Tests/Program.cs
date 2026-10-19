using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;

// Real native windows; no router/catalogue access or physical keyboard/pointer automation.
internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "buschord-docking-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "settings.json"), "{}");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(Root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(Root, "catalog")));
        App.ForcedLanguage = "en";
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            .AfterSetup(_ => DispatcherTimer.RunOnce(async () => await Run(), TimeSpan.FromMilliseconds(300)))
            .StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }

    private static object Field(object instance, string name) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }

    private static Task Settle() => Task.Delay(400);

    private static KeyEventArgs Key(InputElement target, Key key)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control };
        target.RaiseEvent(args);
        return args;
    }

    private static async Task Run()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)lifetime.MainWindow!;
        try
        {
            var project = new Project { Id = "P-Docking", Name = "Synthetic docking regression" };
            var installation = new Installation { Id = "I" };
            var area = new Area { Id = "A", Address = 1 };
            var line = new Line { Id = "L", Address = 1 };
            line.Devices.Add(new DeviceInstance { Id = "D", Name = "Test", Address = new(1, 1, 1), ProductRefId = "M-000F_A-0001-01-0000" });
            area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
            var path = Path.Combine(Root, "synthetic.knxproj");
            Knxproj.Save(project, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open())
                XDocument.Parse("<KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id='N' Name='Number'><TypeNumber Type='unsignedInt' SizeInBit='8' minInclusive='0' maxInclusive='100'/></ParameterType></ParameterTypes><Parameters><Parameter Id='P-N' Name='Brightness' ParameterType='N' Value='25'/></Parameters><ParameterRefs><ParameterRef Id='R-N' RefId='P-N'/></ParameterRefs></Static></ApplicationProgram></KNX>").Save(stream);

            var vm = main.ViewModel;
            vm.LoadProject(path);
            vm.SelectedDevice = vm.Devices[0];
            var factory = (Factory)Field(main, "_dockFactory");
            var parameters = (IDockable)Field(main, "_docParameters");
            var pane = (ParameterPane)Field(main, "_parameters");
            factory.SetActiveDockable(parameters);
            await Settle();
            void Apply(string value)
            {
                ((IEnumerable<ParameterItemViewModel>)pane.FindControl<ItemsControl>("ParameterList")!.ItemsSource!).Single().Value = value;
                Check("apply " + value, pane.Apply());
            }
            string Value() => vm.Devices[0].Device.ParameterValues.GetValueOrDefault("R-N", "DEFAULT");
            Apply("42"); Apply("43");
            factory.FloatDockable(parameters);
            await Settle();
            var floating = lifetime.Windows.Single(window => window != main);
            Check("native floating window", floating.IsVisible);
            Check("floating project undo handled", Key(floating, Avalonia.Input.Key.Z).Handled);
            Check("undo exactly once", Value() == "42" && vm.CanUndo);
            Key(floating, Avalonia.Input.Key.Y);
            Check("floating project redo", Value() == "43");

            vm.SelectedDevice = vm.Devices[0];
            await Settle();
            var editor = pane.GetVisualDescendants().OfType<TextBox>().First();
            editor.Focus();
            editor.SelectAll();
            editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "51" });
            Check("local text input", editor.Text == "51");
            Check("editor undo handled", Key(editor, Avalonia.Input.Key.Z).Handled);
            Check("local undo does not undo project", editor.Text != "51" && Value() == "43");

            floating.Close();
            await Settle(); await Settle();
            Check("float X redocks", lifetime.Windows.Count == 1 && ReferenceEquals(parameters.Owner,
                ((Dictionary<IDockable, IDock>)Field(main, "_homeDocks"))[parameters]));
            Key(main, Avalonia.Input.Key.Z);
            Check("main undo exactly once", Value() == "42");
            Console.WriteLine("DOCKING KEYBOARD OK");
            main.RequestShutdown();
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
