using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Workbench;
using Knx.Catalog;
using System.IO.Compression;

internal static partial class Program
{
    private static async Task StateRegression(string scenario)
    {
        var demo = WorkbenchJson.CreateDemo();
        if (scenario == "--state-k3")
            demo.Installations[0].Areas[0].Lines.Add(new Line { Id = "L-OTHER", Address = 2, Name = "Other line" });
        Vm.CreateProject(demo, null);
        Shell.RefreshWorkspace();
        await Settle();
        var device = ProjectQuery.DeviceList(demo).First(d => d.ParameterValues.ContainsKey("mode"));
        if (scenario == "--state-k1")
        {
            var sessions = (WorkbenchSessions)Field(Shell, "_sessions");
            sessions.Programming.Enqueue(device.Id);
            await sessions.Programming.RunAsync();
            sessions.Monitor.Start();
            sessions.Monitor.TickDemo(new(DateTimeOffset.Now, device.Address.ToString(), "1/1/1",
                "GroupValueWrite", "1.001", "1", SessionOrigin.Demo));
            var recorded = sessions.Monitor.RecordedCount;
            Vm.Edit("name only", p => p.Name += " changed");
            Vm.UndoCommand.Execute(null);
            Shell.RefreshWorkspace();
            Check("K1 undo keeps independent sessions", ReferenceEquals(sessions, Field(Shell, "_sessions")));
            Check("K1 loaded state and recording survive undo", sessions.Programming.Downloaded.Count == 1
                && sessions.Monitor.IsRunning && sessions.Monitor.RecordedCount >= recorded);
            Vm.RedoCommand.Execute(null);
            Check("K1 redo keeps sessions", ReferenceEquals(sessions, Field(Shell, "_sessions")));
            sessions.Monitor.Stop();
            Shell.ShowTool("monitor");
            Shell.FindControl<Grid>("Tools")!.GetVisualDescendants().OfType<Button>()
                .First(b => b.Content is ConceptIcon icon && icon.Symbol == "export")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var floating = Lifetime.Windows.Single(w => w != Shell);
            Vm.Edit("second change", p => p.Name += " second");
            Vm.UndoCommand.Execute(null);
            Check("K1 fixture has both history directions", Vm.CanUndo && Vm.CanRedo);
            sessions.Programming.Enqueue(device.Id);
            var running = sessions.Programming.RunAsync();
            var before = Vm.Project;
            Vm.UndoCommand.Execute(null);
            Check("K1 direct history guarded during programming", ReferenceEquals(before, Vm.Project));
            ClickMenu(WorkbenchStrings.Get("MenuUndo"));
            Check("K1 menu history guarded during programming", ReferenceEquals(before, Vm.Project));
            Vm.RedoCommand.Execute(null);
            ClickMenu(WorkbenchStrings.Get("MenuRedo"));
            Check("K1 direct and menu redo guarded", ReferenceEquals(before, Vm.Project));
            var undo = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control };
            floating.RaiseEvent(undo);
            Check("K1 forwarded floating history guarded", undo.Handled && ReferenceEquals(before, Vm.Project));
            var redo = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Y, KeyModifiers = KeyModifiers.Control };
            floating.RaiseEvent(redo);
            Check("K1 forwarded floating redo guarded", redo.Handled && ReferenceEquals(before, Vm.Project));
            var text = new TextBox();
            var textUndo = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = text, Key = Key.Z, KeyModifiers = KeyModifiers.Control };
            typeof(Knx.App.WorkbenchWindow).GetMethod("RouteUndoRedo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Shell, [textUndo]);
            Check("K1 text undo is not intercepted", !textUndo.Handled);
            await running;
            floating.Close();
            Vm.UndoCommand.Execute(null);
            Check("K1 history resumes after programming", !ReferenceEquals(before, Vm.Project));
            Vm.CreateProject(WorkbenchJson.CreateDemo(), null);
            Shell.RefreshWorkspace();
            Check("K1 replacing even same project ID clears sessions", !ReferenceEquals(sessions, Field(Shell, "_sessions"))
                && ((WorkbenchSessions)Field(Shell, "_sessions")).Programming.Downloaded.Count == 0);
            var replacementSessions = Field(Shell, "_sessions");
            var reopenPath = Path.Combine(Root, "same-id.knxproj");
            Knxproj.Save(Vm.Project!, reopenPath);
            Vm.LoadProject(reopenPath);
            Shell.RefreshWorkspace();
            Check("K1 opening same ID clears sessions", !ReferenceEquals(replacementSessions, Field(Shell, "_sessions")));
        }
        else if (scenario == "--state-k2")
        {
            Shell.OpenTarget(new("device", device.Id), "params");
            await Settle();
            ComboBox Mode() => ((StackPanel)Field(Shell, "_modePanel")).GetVisualDescendants().OfType<ComboBox>().First();
            TextBox Number() => ((StackPanel)Field(Shell, "_modePanel")).Children.OfType<StackPanel>()
                .SelectMany(p => p.Children).OfType<TextBox>().First();
            var original = device.ParameterValues["mode"];
            Mode().SelectedIndex = 1;
            Number().Text = "invalid draft";
            Shell.SetRepresentation("objects");
            Shell.SetRepresentation("params");
            Check("K2 representation retains demo draft", Mode().SelectedIndex == 1 && device.ParameterValues["mode"] == original);
            Check("K2 invalid numeric draft also survives", Number().Text == "invalid draft");
            var secondDevice = ProjectQuery.DeviceList(demo).First(d => d.Id != device.Id && d.ParameterValues.Count > 0);
            Shell.OpenTarget(new("device", secondDevice.Id), "params");
            Number().Text = "123";
            Shell.OpenTarget(new("project", demo.Id));
            Shell.OpenTarget(new("device", device.Id), "params");
            Check("K2 tab navigation retains demo draft", Mode().SelectedIndex == 1);
            Check("K2 per-device draft isolation", Number().Text == "invalid draft");
            Strings.Language = "de";
            Check("K2 locale switch retains demo value", Mode().SelectedIndex == 1);
            Strings.Language = "en";
            Check("K2 locale switch back retains demo value", Mode().SelectedIndex == 1);
            Shell.SetRepresentation("objects");
            Shell.Close();
            await Answer(DiscardTitle, CancelLabel);
            Check("K2 hidden demo draft blocks close", Shell.IsVisible && device.ParameterValues["mode"] == original);
            Shell.OpenTarget(new("device", device.Id), "params");
            ((StackPanel)Field(Shell, "_modeActions")).Children.OfType<Button>()
                .Single(b => Equals(b.Content, WorkbenchStrings.Get("DiscardDrafts")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("K2 explicit discard restores saved mode", Mode().SelectedIndex == 0 && device.ParameterValues["mode"] == original);
            Mode().SelectedIndex = 1;
            Click(Shell, WorkbenchStrings.Get("Apply"));
            Check("K2 explicit apply commits mode", WorkbenchDemoForm.Unquote(device.ParameterValues["mode"]) == "dim" && Vm.IsDirty);
            Shell.OpenTarget(new("device", secondDevice.Id), "params");
            Check("K2 applying one device retains the other draft", Number().Text == "123");
            InstallPicker(Shell);
            SavePath = Path.Combine(Root, "demo-saved.knxproj");
            ClickMenu(WorkbenchStrings.Get("MenuSaveAs"));
            await WaitFor(() => Vm.ProjectPath == SavePath, "demo save path");
            Check("K2 save preserves unapplied drafts", Vm.Status == WorkbenchStrings.Get("SavedWithDrafts") && Number().Text == "123");
            Shell.SetRepresentation("objects");
            Shell.Close();
            await Answer(DiscardTitle, DiscardLabel);
            await WaitFor(() => !Shell.IsVisible, "demo draft discard closes shell");
            Check("K2 close never applies hidden drafts", ProjectQuery.DeviceList(Knxproj.Load(SavePath)).Single(d => d.Id == secondDevice.Id)
                .ParameterValues.OrderBy(p => p.Key).SequenceEqual(secondDevice.ParameterValues.OrderBy(p => p.Key)));
        }
        else if (scenario == "--state-k3")
        {
            var room = demo.Installations.SelectMany(i => i.Locations.AllNodes).First(s => s.Kind == SpaceKind.Room);
            var fixture = Path.Combine(Root, "synthetic.knxprod");
            using (var zip = ZipFile.Open(fixture, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open()))
                writer.Write("<KNX><ManufacturerData><Manufacturer RefId='M-SHELL'><Hardware><Hardware Id='H-SHELL'><Products><Product Id='P-SHELL' Text='Synthetic product' OrderNumber='SHELL'/></Products></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>");
            ((CatalogLibrary)Field(Shell, "_catalogLibrary")).Store.Import(fixture);
            Shell.OpenTarget(new("room", room.Id));
            Shell.ShowCatalog();
            var catalog = (CatalogPane)Shell.FindControl<ContentControl>("ToolPageHost")!.Content!;
            Check("K3 catalog retains source room", catalog.PreferredRoomId == room.Id);
            var expectedLine = WorkbenchProjection.Devices(demo, new("room", room.Id))
                .Select(d => ProjectQuery.LocateDevice(demo, d.Address)?.Line).FirstOrDefault(l => l is not null);
            Check("K3 catalog retains source line", catalog.PreferredLine?.Id == expectedLine?.Id);
            Shell.RefreshWorkspace();
            Check("K3 refresh retains captured room", catalog.PreferredRoomId == room.Id);
            await catalog.Ready;
            var products = catalog.FindControl<ListBox>("ProductList")!;
            await WaitFor(() => products.ItemCount > 0, "synthetic catalog product");
            products.SelectedIndex = 0;
            await Settle();
            var beforeCount = ProjectQuery.DeviceList(demo).Count();
            catalog.FindControl<Button>("InsertButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("K3 native catalog inserts product", ProjectQuery.DeviceList(demo).Count() == beforeCount + 1);
            var inserted = ProjectQuery.DeviceList(demo).Single(d => d.ProductRefId == "P-SHELL");
            Check("K3 insertion uses captured room and topology line", room.DeviceRefs.Contains(inserted.Address.ToString())
                && ProjectQuery.LocateDevice(demo, inserted.Address)?.Line?.Id == expectedLine?.Id);
            Vm.UndoCommand.Execute(null);
            Shell.RefreshWorkspace();
            Check("K3 history rebinds captured IDs to live targets", catalog.PreferredRoomId == room.Id
                && ReferenceEquals(catalog.PreferredLine, WorkbenchProjection.Lines(Vm.Project!).First(l => l.Id == expectedLine!.Id)));
            var otherLine = WorkbenchProjection.Lines(demo).Last();
            Shell.OpenTarget(new("line", otherLine.Id));
            Shell.ShowCatalog();
            Check("K3 another source replaces context", catalog.PreferredRoomId is null && catalog.PreferredLine?.Id == otherLine.Id);
            Vm.CreateProject(WorkbenchJson.CreateDemo(), null);
            Shell.RefreshWorkspace();
            Shell.ShowCatalog();
            Check("K3 replacement cannot retain stale room", catalog.PreferredRoomId is null);
        }
        else throw new ArgumentException(scenario);
        Console.WriteLine(scenario + " OK");
    }
}
