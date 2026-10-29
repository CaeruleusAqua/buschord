using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Catalog;

internal static class BusDiagramChecks
{
    internal static void Projection(Action<string, bool> check)
    {
        var project = Fixture();
        var layout = BusDiagramLayout.Create(project);
        check("diagram projects area, direct-line and segment devices exactly once", layout.Nodes.Count(n => n.Kind == "device") == 6
            && layout.Nodes.Where(n => n.Kind == "device").Select(n => n.Key).Distinct().Count() == 6);
        check("diagram keeps empty topology containers and installations", layout.Nodes.Any(n => n.Key == "segment:S-empty")
            && layout.Nodes.Any(n => n.Key == "line:L-empty") && layout.Nodes.Any(n => n.Key == "area:A-empty")
            && layout.Nodes.Any(n => n.Key == "installation:I-empty"));
        check("diagram follows containment rather than deriving it from addresses", layout.Nodes.Single(n => n.Key == "device:area-device").ParentKey == "area:A"
            && layout.Nodes.Single(n => n.Key == "device:segment-device").ParentKey == "segment:S");
        check("diagram only marks couplers supported by resolved hardware", layout.Nodes.Single(n => n.Key == "device:coupler").Detail.Contains("Coupler")
            && !layout.Nodes.Single(n => n.Key == "device:zero-device").Detail.Contains("Coupler")
            && !layout.Nodes.Single(n => n.Key == "device:program-wins").Detail.Contains("Coupler"));
        check("segment inherits medium without inventing routing", layout.Nodes.Single(n => n.Key == "segment:S").Detail.Contains("TP")
            && layout.Nodes.Single(n => n.Key == "segment:S-empty").Detail.Contains("IP"));
        check("layout is deterministic", layout == BusDiagramLayout.Create(project) || layout.Nodes.SequenceEqual(BusDiagramLayout.Create(project).Nodes));
        check("same-column nodes never overlap", layout.Nodes.GroupBy(n => n.X).All(g => g.OrderBy(n => n.Y).Zip(g.OrderBy(n => n.Y).Skip(1))
            .All(pair => pair.Second.Y - pair.First.Y >= BusDiagramNode.Height)));
        check("empty project has finite nonzero bounds", BusDiagramLayout.Create(new() { Id = "empty", Name = "Empty" }) is { Nodes.Count: 0, Width: > 0, Height: > 0 });
        var large = BusDiagramLayout.Create(Fixture(4096));
        check("large synthetic layout retains every device with finite bounds", large.Nodes.Count(n => n.Kind == "device") == 4102
            && double.IsFinite(large.Width) && double.IsFinite(large.Height));
        var path = Path.Combine(Path.GetTempPath(), "buschord-diagram-layout-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var workspace = new WorkbenchWorkspace(project.Id); workspace.Open(new("project", project.Id), project.Name, "diagram"); workspace.SaveLayout(path);
            var restored = new WorkbenchWorkspace(project.Id);
            check("diagram representation survives workspace layout roundtrip", restored.TryRestoreLayout(path) && restored.ActiveTab?.View == "diagram");
        }
        finally { File.Delete(path); }
    }

    internal static int Run(string language)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-diagram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldSettings = Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS");
        var previousFactory = App.TestWindowFactory;
        var previousLanguage = App.ForcedLanguage;
        var previousTheme = App.ForcedTheme;
        var failures = 0; var checks = 0;
        void Check(string name, bool value) { checks++; if (!value) failures++; Console.WriteLine((value ? "PASS " : "FAIL ") + name); }
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(2), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        try
        {
            var settings = Path.Combine(directory, "settings.json"); File.WriteAllText(settings, "{}");
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settings);
            App.ForcedLanguage = language; App.ForcedTheme = "light";
            App.TestWindowFactory = () =>
            {
                var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(directory, "catalog")));
                var vm = new MainWindowViewModel(catalog); vm.CreateProject(Fixture(), null);
                return new WorkbenchWindow(vm, new(), Path.Combine(directory, "layout.json"), catalog) { Width = 1440, Height = 900 };
            };
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                var window = (WorkbenchWindow)lifetime.MainWindow!;
                try { await Native(window, language, Check); }
                catch (Exception error) { Console.Error.WriteLine(error); failures++; }
                finally { window.RequestShutdown(); lifetime.Shutdown(failures == 0 ? 0 : 1); }
            }, TimeSpan.FromMilliseconds(200))).StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            App.TestWindowFactory = previousFactory; App.ForcedLanguage = previousLanguage; App.ForcedTheme = previousTheme;
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", oldSettings);
        }
        Console.WriteLine($"Bus diagram checks: {checks}, failures: {failures}; temporary files: {directory}");
        return failures == 0 ? 0 : 1;
    }
    private static async Task Native(WorkbenchWindow window, string language, Action<string, bool> check)
    {
        var vm = window.ViewModel;
        var before = Knxproj.ToXml(vm.Project!).ToString();
        var state = (vm.IsDirty, vm.CanUndo, vm.CanRedo);
        var tab = window.OpenTarget(new("project", vm.Project!.Id), "diagram");
        await Settle();
        BusDiagramView View() => (BusDiagramView)window.FindControl<ContentControl>("DiagramHost")!.Content!;
        var view = View();
        check("native diagram representation is visible", view.IsEffectivelyVisible && tab.View == "diagram" && view.RealizedNodeCount > 0);
        view.FocusNode("device:segment-device"); await Settle();
        var selected = window.FocusManager!.GetFocusedElement() as Button;
        check("native node focus selects current device with an accessible name", view.SelectedKey == "device:segment-device"
            && selected is not null && AutomationProperties.GetName(selected)?.Contains("Segment sensor") == true);
        var inspector = window.FindControl<StackPanel>("InspectorBody")!;
        check("diagram inspector follows selected nodes as a read-only preview", inspector.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Segment sensor")
            && !inspector.GetVisualDescendants().OfType<TextBox>().Any());
        void Press(Key key) => (window.FocusManager!.GetFocusedElement() as Interactive)!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Press(Key.Left); await Settle();
        check("Left arrow focuses the actual segment parent", view.SelectedKey == "segment:S");
        Press(Key.Right); await Settle();
        check("Right arrow returns to a segment device", view.SelectedKey == "device:segment-device");
        window.RefreshWorkspace(); window.RefreshWorkspace(); await Settle();
        check("diagram node focus survives consecutive projection refreshes", view.SelectedKey == "device:segment-device"
            && (window.FocusManager!.GetFocusedElement() as Button)?.Tag as string == "device:segment-device");
        window.RefreshWorkspace();
        var zoomButton = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "diagram.zoomIn");
        zoomButton.Focus(); await Settle();
        check("queued diagram refresh never steals toolbar focus", ReferenceEquals(window.FocusManager.GetFocusedElement(), zoomButton));
        view.SetZoom(double.NaN); check("non-finite zoom is ignored", double.IsFinite(view.Zoom));
        view.SetZoom(20); check("zoom has a safe upper bound", view.Zoom == 2);
        view.SetZoom(.01); check("zoom has a safe lower bound", view.Zoom == .35);
        view.Fit(); await Settle();
        check("reading and navigation do not mutate project history", before == Knxproj.ToXml(vm.Project!).ToString() && state == (vm.IsDirty, vm.CanUndo, vm.CanRedo));
        Shot(window, language + "-light-topology");
        SettingsWindow.ApplyTheme("dark"); await Settle(); view.Fit(); await Settle();
        Shot(window, language + "-dark-topology");
        view.FocusNode("device:segment-device"); await Settle(); Press(Key.Enter); await Settle();
        check("Enter opens the existing device editor", window.Workspace.ActiveTab?.Target == new WorkbenchTarget("device", "segment-device") && window.Workspace.ActiveTab.View == "objects");
        window.SetRepresentation("diagram"); await Settle();
        check("device diagram starts at its selected target and hides scope-specific editor actions", View().SelectedKey == "device:segment-device"
            && !window.FindControl<Button>("InspectorButton")!.IsEffectivelyVisible
            && !window.FindControl<Button>("WatchButton")!.IsEffectivelyVisible && !window.FindControl<Button>("AddDeviceButton")!.IsEffectivelyVisible);
        window.Workspace.ActiveTab = tab; window.RefreshWorkspace(); await Settle(); view = View();
        view.SetZoom(1.25);
        window.OpenTarget(new("device", "line-device")); window.Workspace.ActiveTab = tab; window.RefreshWorkspace(); await Settle();
        check("diagram zoom survives switching work-item tabs", ReferenceEquals(view, View()) && View().Zoom == 1.25);
        view.SelectNode("device:segment-device");
        Strings.Language = language == "de" ? "en" : "de"; await Settle();
        view = View();
        check("language change relabels controls while preserving diagram state", view.Zoom == 1.25 && view.SelectedKey == "device:segment-device"
            && view.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetName(b) == (language == "de" ? "Zoom in" : "Vergrößern")));
        Strings.Language = language; await Settle(); view = View();
        view.SelectNode("device:segment-device");
        vm.Edit("Rename synthetic sensor", project => ProjectQuery.DeviceList(project).Single(d => d.Id == "segment-device").Name = "Renamed sensor");
        window.RefreshWorkspace(); await Settle();
        check("diagram refresh sees actual edits", view.Layout.Nodes.Single(n => n.Key == "device:segment-device").Title == "Renamed sensor");
        var prior = vm.Project;
        vm.UndoCommand.Execute(null); window.RefreshWorkspace(); await Settle();
        check("undo replaces the model and refreshes stable node identities", !ReferenceEquals(prior, vm.Project)
            && view.SelectedKey == "device:segment-device" && view.Layout.Nodes.Single(n => n.Key == "device:segment-device").Title == "Segment sensor");
        vm.RedoCommand.Execute(null); window.RefreshWorkspace(); await Settle();
        check("redo refreshes current node captions", view.Layout.Nodes.Single(n => n.Key == "device:segment-device").Title == "Renamed sensor");
        vm.Edit("Remove synthetic sensor", project => project.Installations[0].Areas[0].Lines[0].Segments[0].Devices.Clear());
        window.RefreshWorkspace(); await Settle();
        check("removed diagram selection clears", view.SelectedKey is null && !view.Layout.Nodes.Any(n => n.Key == "device:segment-device"));
        vm.CreateProject(Fixture(4096), null); window.RefreshWorkspace(); window.OpenTarget(new("project", vm.Project!.Id), "diagram"); await Settle();
        view = View(); view.Fit(); await Settle();
        check("large native diagram virtualizes controls", view.Layout.Nodes.Count > 4100 && view.RealizedNodeCount is > 0 and < 150);
        view.FocusNode("device:large-4095"); await Settle();
        var lastNode = view.Layout.Nodes.Single(n => n.Key == "device:large-4095");
        check("keyboard can reveal a virtualized node at the end", view.SelectedKey == "device:large-4095"
            && (window.FocusManager!.GetFocusedElement() as Button)?.Tag as string == "device:large-4095" && view.RealizedNodeCount < 150);
        check("revealed large-project node is inside the viewport", lastNode.Y * view.Zoom >= view.ScrollOffset.Y
            && lastNode.Y * view.Zoom < view.ScrollOffset.Y + view.Viewport.Height);
        var lastButton = (Button)window.FocusManager.GetFocusedElement()!;
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var visualBounds = new Rect(lastButton.Bounds.Size).TransformToAABB(lastButton.TransformToVisual(scroll)!.Value);
        check("real arranged node bounds are visible after virtualized focus", visualBounds.Top >= 0 && visualBounds.Bottom <= scroll.Viewport.Height + 1);
        Shot(window, language + "-dark-large");
        view.SetZoom(2); await Settle();
        check("zoom keeps the anchor visible near the end of a large topology", lastNode.Y * view.Zoom >= view.ScrollOffset.Y
            && lastNode.Y * view.Zoom < view.ScrollOffset.Y + view.Viewport.Height);
        vm.CreateProject(new() { Id = "empty", Name = "Empty synthetic project" }, null); window.RefreshWorkspace();
        window.OpenTarget(new("project", "empty"), "diagram"); await Settle();
        check("empty native diagram has an explanatory state", View().Layout.Nodes.Count == 0
            && View().GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(language == "de" ? "Noch keine Topologie" : "No topology yet") == true));
        Shot(window, language + "-dark-empty");
        window.Width = 900; window.Height = 600; await Settle();
        check("diagram toolbar stays inside compact editor", View().GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
            .All(b => b.TranslatePoint(default, window) is { } p && p.X >= 0 && p.X + b.Bounds.Width <= window.Bounds.Width + 1));
        Shot(window, language + "-dark-compact");
        window.Workspace.InspectorVisible = true; window.RefreshWorkspace(); await Settle();
        var diagramBounds = View().Bounds.Size;
        check("diagram controls wrap inside the editor with a compact inspector", View().GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
            .All(b => b.TransformToVisual(View()) is { } transform && new Rect(b.Bounds.Size).TransformToAABB(transform) is var rect
                && rect.Left >= -1 && rect.Right <= diagramBounds.Width + 1));
        Shot(window, language + "-dark-compact-inspector");
    }
    private static async Task Settle() { await Task.Delay(100); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("BUSCHORD_DIAGRAM_SHOTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    private static Project Fixture(int extra = 0)
    {
        var p = new Project { Id = "P-diagram", Name = "Synthetic topology" };
        var i = new Installation { Id = "I", Name = "Main installation" }; p.Installations.Add(i); p.Installations.Add(new() { Id = "I-empty", Name = "Empty installation" });
        var a = new Area { Id = "A", Address = 1, Name = "Building A" }; i.Areas.Add(a); i.Areas.Add(new() { Id = "A-empty", Address = 2, Name = "Empty area" });
        var l = new Line { Id = "L", Address = 1, Name = "Ground floor" }; a.Lines.Add(l); a.Lines.Add(new() { Id = "L-empty", Address = 2, Name = "Empty line" });
        var s = new Segment { Id = "S", Address = 1, Name = "East wing" }; l.Segments.Add(s); l.Segments.Add(new() { Id = "S-empty", Address = 2, Name = "Empty IP segment", MediumTypeRefId = "MT-5" });
        DeviceInstance D(string id, string name, byte number) => new() { Id = id, Name = name, ProductRefId = "synthetic", Address = new(1, 1, number) };
        a.Devices.Add(D("area-device", "Area device", 1)); l.Devices.Add(D("line-device", "Line actuator", 2)); s.Devices.Add(D("segment-device", "Segment sensor", 3));
        l.Devices.Add(D("zero-device", "Unknown role", 0));
        var coupler = D("coupler", "Line coupler", 4); coupler.ProductRefId = "coupler-hardware"; l.Devices.Add(coupler);
        var priority = D("program-wins", "Program hardware takes priority", 5); priority.ProductRefId = "coupler-hardware"; priority.Hardware2ProgramRefId = "device-hardware"; l.Devices.Add(priority);
        p.Hardware["coupler-hardware"] = new() { HardwareId = "coupler-hardware", IsCoupler = true }; p.Hardware["device-hardware"] = new() { HardwareId = "device-hardware" };
        for (var index = 0; index < extra; index++)
        {
            var group = index / 255;
            var areaNumber = (byte)(3 + group / 15); var lineNumber = (byte)(1 + group % 15);
            var largeArea = i.Areas.FirstOrDefault(area => area.Address == areaNumber);
            if (largeArea is null) { largeArea = new() { Id = "large-area-" + areaNumber, Address = areaNumber, Name = "Synthetic area " + areaNumber }; i.Areas.Add(largeArea); }
            var largeLine = largeArea.Lines.FirstOrDefault(line => line.Address == lineNumber);
            if (largeLine is null) { largeLine = new() { Id = "large-line-" + group, Address = lineNumber, Name = "Synthetic line " + group }; largeArea.Lines.Add(largeLine); }
            var device = D("large-" + index, "Synthetic device " + index, (byte)(index % 255 + 1));
            device.Address = new(areaNumber, lineNumber, (byte)(index % 255 + 1)); largeLine.Devices.Add(device);
        }
        return p;
    }
}
