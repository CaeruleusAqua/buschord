using System.IO.Compression;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Services;
using Knx.App.Views;

namespace Knx.App.Workbench;

/// <summary>Native routed-event checks and synthetic screenshots; never uses OS mouse input or KNX transport.</summary>
internal static class WorkbenchCheck
{
    public static bool Enabled { get; set; }
    public static string? TestDirectory { get; set; }

    internal static void LoadFixture(WorkbenchWindow window)
    {
        var p = new Project { Id = "P-SHELL", Name = "Villa Linden · synthetisches Testprojekt" };
        var installation = new Installation { Id = "I-1", Name = "Wohngebäude" };
        var area = new Area { Id = "A-1", Address = 1 };
        var line = new Line { Id = "L-1", Address = 1, Name = "Hauptlinie" };
        area.Lines.Add(line); installation.Areas.Add(area); p.Installations.Add(installation);
        var building = installation.Locations.Add(null, "Villa Linden", SpaceKind.Building, "B-1");
        var floor = installation.Locations.Add(building, "Erdgeschoss", SpaceKind.Floor, "F-1");
        var room = installation.Locations.Add(floor, "Wohnzimmer", SpaceKind.Room, "R-1");
        installation.Locations.Add(floor, "Küche", SpaceKind.Room, "R-2");
        var group = new GroupRange { Id = "GR-1", Name = "Beleuchtung", RangeStart = 0, RangeEnd = 65535 };
        installation.MainRanges.Add(group);
        for (var n = 1; n <= 3; n++)
        {
            var d = new DeviceInstance { Id = "D-" + n, Name = new[] { "Tastsensor Wohnzimmer", "Präsenzmelder Wohnzimmer", "Raumtemperaturregler" }[n - 1],
                Address = new(1, 1, (byte)(10 + n)), ProductRefId = "M-000F_A-0001-01-0000" };
            line.Devices.Add(d); room.AssignDevice(d);
            d.Channels.Add(new() { Id = "CH-A", Name = "A · Herstellerkanal (ohne Objektzuordnung im Basisformat)" });
            for (var j = 0; j < (n == 1 ? 4 : 2); j++)
            {
                var id = $"GA-{n}-{j}";
                var ga = new GroupAddressEntry { Id = id, Name = j % 2 == 0 ? "Wohnzimmer · Licht" : "Wohnzimmer · Rückmeldung",
                    Address = GroupAddress.ThreeLevel(1, (byte)n, (byte)(j + 1)), DatapointType = "DPST-1-1" };
                group.Addresses.Add(ga);
                var o = new ComObjectInstanceRef { Id = $"O-{n}-{j}", ComObjectId = "CO-" + j, Number = j,
                    Text = j % 2 == 0 ? "Schalten" : "LED-Rückmeldung" };
                o.Links.Add(id); d.ComObjectRefs.Add(o);
            }
        }
        var path = Path.Combine(TestDirectory!, "synthetic.knxproj");
        Knxproj.Save(p, path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var stream = archive.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open())
            XDocument.Parse("""
                <KNX><ApplicationProgram><Static><ParameterTypes>
                <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="100" /></ParameterType>
                <ParameterType Id="E" Name="Mode"><TypeRestriction SizeInBit="8"><Enumeration Value="0" Text="Schalten" /><Enumeration Value="1" Text="Dimmen" /></TypeRestriction></ParameterType>
                </ParameterTypes><Parameters>
                <Parameter Id="P-N" Name="Entprellzeit" ParameterType="N" Value="25" />
                <Parameter Id="P-E" Name="Funktion" ParameterType="E" Value="0" />
                </Parameters><ParameterRefs><ParameterRef Id="R-N" RefId="P-N" /><ParameterRef Id="R-E" RefId="P-E" /></ParameterRefs>
                </Static></ApplicationProgram></KNX>
                """).Save(stream);
        window.ViewModel.CreateProject(Knxproj.Load(path), null);
        window.ViewModel.ProjectPath = path;
        window.RefreshWorkspace();
        window.Workspace.Navigators["building"].ExpandedIds.UnionWith(["space:F-1", "space:R-1"]);
        window.OpenTarget(new("room", "R-1"));
        window.RefreshWorkspace();
    }

    public static void Capture(IClassicDesktopStyleApplicationLifetime desktop, WorkbenchWindow main, string path, string screen)
    {
        LoadFixture(main);
        Window window = main;
        switch (screen.ToLowerInvariant())
        {
            case "parameters": main.SetRepresentation("params"); break;
            case "links": main.SetRepresentation("relations"); break;
            case "monitor": main.ShowTool("monitor"); break;
            case "inspector": main.Workspace.InspectorVisible = true; break;
            case "catalog": main.ShowCatalog(); break;
            case "settings": window = new SettingsWindow(main.ViewModel.Settings); break;
            case "new-project": window = new NewProjectWindow(); break;
            case "splash": window = new SplashWindow(); break;
        }
        var capture = window;
        capture.Width = screen == "splash" ? 380 : 1440; capture.Height = screen == "splash" ? 420 : 900;
        capture.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            try { Screenshot.Write(capture, path, new((int)capture.Width, (int)capture.Height)); main.RequestShutdown(); desktop.Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); main.RequestShutdown(); desktop.Shutdown(1); }
        }, TimeSpan.FromSeconds(1));
        capture.Show();
    }

    public static void Run(IClassicDesktopStyleApplicationLifetime desktop, WorkbenchWindow window)
    {
        window.Opened += (_, _) => DispatcherTimer.RunOnce(async () =>
        {
            var failures = new List<string>();
            void Check(string name, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}"); if (!value) failures.Add(name); }
            try
            {
                LoadFixture(window);
                var p = window.ViewModel.Project!;
                var d = ProjectQuery.DeviceList(p).First();
                var room = window.Workspace.ActiveTab!;
                foreach (var perspective in new[] { "building", "topology", "groups" })
                {
                    window.Navigate(perspective);
                    var all = Flatten(WorkbenchProjection.Navigation(p, perspective));
                    Check(perspective + " resolves the same native device", all.Any(n => ReferenceEquals(n.Model, d)));
                    window.FindControl<TextBox>("NavSearch")!.Text = d.Address.ToString();
                    await Task.Delay(40);
                    var nav = window.FindControl<ListBox>("NavTree")!;
                    nav.SelectedItem = ((IEnumerable<WorkbenchWindow.NavEntry>)nav.ItemsSource!).First(n => ReferenceEquals(n.Node.Model, d));
                    Check(perspective + " native navigator opens the common work item", window.Workspace.ActiveTab?.Target == new WorkbenchTarget("device", d.Id));
                }
                window.Navigate("building");
                window.OpenTarget(room.Target);
                var table = window.FindControl<ListBox>("ObjectTable")!;
                var row = ((IEnumerable<WorkbenchRow>)table.ItemsSource!).First(r => r.Object is not null);
                table.SelectedItems!.Add(row);
                var selected = room.SelectedObjectIds.ToArray();
                Check("routed native selection stored on tab", selected.Contains(row.Key));
                window.FindControl<TextBox>("ObjectSearch")!.Text = "LED";
                await Task.Delay(50); // Avalonia delivers TextChanged at the next dispatcher turn.
                var deviceTab = window.OpenTarget(new("device", d.Id));
                Check("new object tab has independent filter and selection", deviceTab.Filter == "" && deviceTab.SelectedObjectIds.Count == 0);
                window.OpenTarget(room.Target);
                Check($"room filter and selection survive tab switch (filter={room.Filter}, selection={string.Join(',', room.SelectedObjectIds)})", room.Filter == "LED" && room.SelectedObjectIds.SequenceEqual(selected));
                window.FindControl<TextBox>("ObjectSearch")!.Text = "";
                var rows = WorkbenchProjection.Rows(p, room);
                Check("device, channel, object hierarchy shares a table", rows.Any(r => r.Kind == "device") && rows.Any(r => r.Kind == "channel") && rows.Any(r => r.Object is not null));
                room.CollapsedDeviceIds.Add(d.Id); window.RefreshWorkspace();
                Check("device collapse hides children, not siblings", !WorkbenchProjection.Rows(p, room).Any(r => r.Device == d && r.Object is not null) && WorkbenchProjection.Rows(p, room).Any(r => r.Device != d && r.Object is not null));
                room.CollapsedDeviceIds.Clear();
                window.OpenTarget(new("device", d.Id), "params");
                var pane = window.ParameterEditor;
                var parameterRows = (IEnumerable<ParameterItemViewModel>)pane.FindControl<ItemsControl>("ParameterList")!.ItemsSource!;
                parameterRows.First().Value = "44";
                Check("parameter draft does not edit native project", d.ParameterValues.Count == 0 && pane.HasUnappliedChanges);
                window.SetRepresentation("objects"); window.SetRepresentation("params");
                Check("parameter draft survives representation switch", pane.HasUnappliedChanges);
                pane.FindControl<Button>("ApplyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("routed explicit Apply uses existing validation and undo", d.ParameterValues.GetValueOrDefault("R-N") == "44" && window.ViewModel.IsDirty);
                window.Workspace.InspectorVisible = true;
                Check("inspector reveals native device", window.FindControl<StackPanel>("InspectorBody")!.Children.OfType<TextBlock>().Any(t => t.Text == d.Name));
                var inspector = window.FindControl<StackPanel>("InspectorBody")!;
                inspector.Children.OfType<TextBox>().Single().Text = "Tastsensor · bearbeitet";
                inspector.Children.OfType<Button>().First(b => Equals(b.Content, "Eigenschaften übernehmen"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("native inspector rename refreshes work-item titles", d.Name == "Tastsensor · bearbeitet" && deviceTab.Title == d.Name);
                window.SetRepresentation("relations");
                Check("existing validated link editor receives the native device", window.FindControl<ContentControl>("RelationsHost")!.Content is GroupLinksPane links && ReferenceEquals(links.Device, d));
                window.SetRepresentation("objects");
                var layout = Path.Combine(TestDirectory!, "layout.json");
                window.Workspace.NavigatorWidth = 300;
                await Task.Delay(40);
                window.SaveLayout(layout);
                var restored = new WorkbenchWorkspace(p.Id);
                Check("saved layout restores tabs and visibility without project mutation", restored.TryRestoreLayout(layout) && restored.InspectorVisible && restored.NavigatorWidth == 300 && restored.Tabs.Count == window.Workspace.Tabs.Count);
                window.ShowTool("monitor"); window.ViewModel.StartRecording(); window.Workspace.ToolsVisible = false;
                window.ShowTool("monitor");
                Check("hiding and recreating monitor retains recorder state", window.ViewModel.IsRecording);
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    window.FindControl<Grid>("Tools")!.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "↗"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(30);
                    var floating = desktop.Windows.Single(w => w != window);
                    Check($"fresh tool window {cycle + 1} does not reparent main content", !ReferenceEquals(floating.Content, window.FindControl<ContentControl>("ToolHost")!.Content));
                    floating.Close();
                    Check($"closing tool window {cycle + 1} retains recorder", window.ViewModel.IsRecording);
                }
                window.ViewModel.StopRecording();
                window.ViewModel.UndoCommand.Execute(null); window.RefreshWorkspace();
                Check("undo targets resolve the replacement project", !ReferenceEquals(window.ViewModel.Project, p) && WorkbenchProjection.Resolve(window.ViewModel.Project, deviceTab.Target) is DeviceInstance);
                var large = ProjectQuery.DeviceList(window.ViewModel.Project!).First();
                for (var n = 0; n < 10020; n++) large.ComObjectRefs.Add(new() { Id = "BIG-" + n, ComObjectId = "BIG-" + n, Number = n, Text = "Lasttest " + n });
                var started = System.Diagnostics.Stopwatch.StartNew();
                window.OpenTarget(new("device", large.Id), "objects");
                await Task.Delay(150);
                var count = ((IEnumerable<WorkbenchRow>)table.ItemsSource!).Count();
                var realized = table.GetVisualDescendants().OfType<ListBoxItem>().Count();
                Check($"10000+ objects use bounded realized native rows ({realized}/{count}, {started.ElapsedMilliseconds} ms incl. render wait)", count > 10000 && realized < 200);
                var headerX = window.FindControl<Grid>("TableHost")!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "DPT").TranslatePoint(new Point(), window)!.Value.X;
                var cellX = table.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "—").TranslatePoint(new Point(), window)!.Value.X;
                Check($"hierarchical rows and header share column positions ({headerX}/{cellX})", Math.Abs(headerX - cellX) <= 1);
                var scroller = table.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Offset = new(0, 800);
                await Task.Delay(40);
                var offset = scroller.Offset.Y;
                window.OpenTarget(room.Target);
                await Task.Delay(40);
                window.OpenTarget(new("device", large.Id));
                await Task.Delay(40);
                Check("native scroll offset is local to its work-item tab", offset > 0 && Math.Abs(scroller.Offset.Y - offset) < 1);
                window.FindControl<TextBox>("ObjectSearch")!.Text = "Lasttest 10019";
                await Task.Delay(80);
                Check("filter searches unrealized model objects", ((IEnumerable<WorkbenchRow>)table.ItemsSource!).Count(r => r.Object is not null) == 1);
                Console.WriteLine(failures.Count == 0 ? "WORKBENCH NATIVE OK" : $"WORKBENCH FAILURES: {failures.Count}");
                window.RequestShutdown(); desktop.Shutdown(failures.Count == 0 ? 0 : 1);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); window.RequestShutdown(); desktop.Shutdown(1); }
        }, TimeSpan.FromMilliseconds(300));
    }

    private static IEnumerable<WorkbenchNavNode> Flatten(IEnumerable<WorkbenchNavNode> nodes)
        => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
}
