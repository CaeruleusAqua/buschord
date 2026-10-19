using System.IO.Compression;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App.Services;
using Knx.App.Views;

internal static class NativeDraftChecks
{
    public static int Run()
    {
        AppBuilder.Configure<Application>().UsePlatformDetect()
            .AfterSetup(_ => DispatcherTimer.RunOnce(Check, TimeSpan.FromMilliseconds(100)))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return Environment.ExitCode;
    }

    private static async void Check()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var root = Path.Combine(Path.GetTempPath(), "buschord-native-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = 0;
        void Assert(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        try
        {
            Application.Current.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            var project = new Project { Id = "P", Name = "Native draft regression" };
            var installation = new Installation { Id = "I" };
            var area = new Area { Id = "A", Address = 1 };
            var line = new Line { Id = "L", Address = 1 };
            foreach (var name in new[] { "A", "B" })
                line.Devices.Add(new DeviceInstance { Id = name, Name = name, Address = new(1, 1, (byte)(line.Devices.Count + 1)), ProductRefId = "M-000F_A-0001-01-0000" });
            area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
            var path = Path.Combine(root, "fixture.knxproj");
            Knxproj.Save(project, path);
            line.Devices[1] = new DeviceInstance { Id = "A", Name = "B", Address = new(1, 1, 2), ProductRefId = "M-000F_A-0001-01-0000" };
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open())
                XDocument.Parse("<KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id='N' Name='Number'><TypeNumber Type='unsignedInt' SizeInBit='8' minInclusive='0' maxInclusive='100'/></ParameterType></ParameterTypes><Parameters><Parameter Id='P-N' Name='Brightness' ParameterType='N' Value='25'/></Parameters><ParameterRefs><ParameterRef Id='R-N' RefId='P-N'/></ParameterRefs></Static></ApplicationProgram></KNX>").Save(stream);
            var vm = new MainWindowViewModel(); vm.CreateProject(project, null); vm.ProjectPath = path;
            var a = vm.Devices[0]; var b = vm.Devices[1];
            var pane = new ParameterPane(vm);
            var window = new Window { Content = pane, Width = 700, Height = 500 };
            window.Show();
            ParameterItemViewModel Row() => ((IEnumerable<ParameterItemViewModel>)pane.FindControl<ItemsControl>("ParameterList")!.ItemsSource!).Single();
            pane.Load(a);
            await Task.Delay(300);
            var editor = pane.GetVisualDescendants().OfType<TextBox>().First();
            editor.Focus(); editor.SelectAll();
            editor.SelectedText = "44";
            await Task.Delay(100);
            Assert("Native text edit is dirty", pane.HasUnappliedChanges && Row().Value == "44");
            editor.Undo();
            await Task.Delay(100);
            Assert("Local text undo to baseline is clean", Row().Value == "25" && !pane.HasUnappliedChanges);
            pane.DiscardDrafts(); pane.Load(b); Row().Value = "46"; pane.Load(a); Row().Value = "47";
            System.ComponentModel.PropertyChangedEventHandler reenter = (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.CanUndo)) pane.Load(b); };
            vm.PropertyChanged += reenter;
            var applied = pane.Apply(); vm.PropertyChanged -= reenter;
            Assert("Native Apply callback selects B and preserves dirty ownership", applied && pane.Device == b.Device && Row().Value == "46" && pane.HasUnappliedChanges);
            Assert("Only A was applied", a.Device.ParameterValues.GetValueOrDefault("R-N") == "47" && b.Device.ParameterValues.Count == 0);
            pane.Load(a);
            Assert("Applied A reloads from the model", Row().Value == "47");
            pane.Load(b);
            Assert("Native reentrant Apply preserves B across subsequent switches", Row().Value == "46" && pane.HasUnappliedChanges);
            Row().Value = "25";
            Assert("Only B was dirty after Apply; reverting B leaves no hidden A draft", !pane.HasUnappliedChanges);
            pane.DiscardDrafts();
            Assert("Discarding B leaves applied A clean", !pane.HasUnappliedChanges);
            window.Close();
        }
        catch (Exception ex) { Console.WriteLine(ex); failures++; }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "NATIVE DRAFT CHECKS OK" : $"{failures} native failures");
        lifetime.Shutdown(failures == 0 ? 0 : 1);
    }
}
