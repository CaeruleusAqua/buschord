using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

internal static class WorkbenchRefreshChecks
{
    [STAThread]
    internal static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-projection-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousSettings = Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS");
        var previousFactory = App.TestWindowFactory;
        var previousLanguage = App.ForcedLanguage;
        var previousTheme = App.ForcedTheme;
        var checks = 0;
        var failures = 0;
        void Check(string name, bool value)
        {
            checks++;
            if (!value) failures++;
            Console.WriteLine((value ? "PASS " : "FAIL ") + name);
        }
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("Projection refresh checks exceeded the 90-second process deadline.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            File.WriteAllText(settings, "{}");
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settings);
            App.ForcedLanguage = "en";
            App.ForcedTheme = "light";
            App.TestWindowFactory = () =>
            {
                var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(directory, "catalog")));
                var vm = new MainWindowViewModel(catalog);
                vm.CreateProject(Fixture(), null);
                return new WorkbenchWindow(vm, new WorkbenchWorkspace(), Path.Combine(directory, "layout.json"), catalog)
                { Width = 1280, Height = 800 };
            };
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
                .AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
                {
                    var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                    var window = (WorkbenchWindow)lifetime.MainWindow!;
                    try { await Test(window, Check); }
                    catch (Exception error) { Console.Error.WriteLine(error); failures++; }
                    finally { window.RequestShutdown(); lifetime.Shutdown(failures == 0 ? 0 : 1); }
                }, TimeSpan.FromMilliseconds(200)))
                .StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            App.TestWindowFactory = previousFactory;
            App.ForcedLanguage = previousLanguage;
            App.ForcedTheme = previousTheme;
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", previousSettings);
        }
        Console.WriteLine($"Projection refresh checks: {checks}, failures: {failures}; temporary files: {directory}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task Test(WorkbenchWindow window, Action<string, bool> check)
    {
        var vm = window.ViewModel;
        var table = window.FindControl<ListBox>("ObjectTable")!;
        var search = window.FindControl<TextBox>("ObjectSearch")!;
        var body = window.FindControl<StackPanel>("InspectorBody")!;
        var projectTab = window.OpenTarget(new("project", vm.Project!.Id));
        window.Workspace.InspectorVisible = true;
        await Settle();
        WorkbenchRow[] Rows() => ((IEnumerable<WorkbenchRow>)table.ItemsSource!).ToArray();
        WorkbenchRow Row(string id) => Rows().Single(r => r.Object?.Id == id);
        string? DeviceName() => body.GetVisualDescendants().OfType<TextBox>()
            .SingleOrDefault(c => AutomationProperties.GetAutomationId(c) == "inspector.device.name")?.Text;
        bool ObjectCaption(string text) => body.GetVisualDescendants().OfType<TextBlock>().Any(c => c.Text == text);
        bool CurrentRows() => Rows().All(r => ProjectQuery.DeviceList(vm.Project!).Any(d => ReferenceEquals(d, r.Device))
            && (r.Object is null || r.Device.ComObjectRefs.Any(o => ReferenceEquals(o, r.Object))));
        check("project projection includes every object and device", Rows().Count(r => r.Object is not null) == 240
            && Rows().Count(r => r.Kind == "device") == 3 && CurrentRows());
        check("empty selection resolves the current first device", projectTab.SelectedObjectIds.Count == 0 && DeviceName() == "Device 0");

        var first = Row("D-0_O-0");
        table.ScrollIntoView(first);
        await Settle();
        var rowCheck = table.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => c.Tag is WorkbenchRow r && r.Object?.Id == first.Object!.Id && r.Device.Id == first.Device.Id);
        rowCheck.IsChecked = true;
        await Settle();
        check("row checkbox selects the current object and inspector", projectTab.SelectedObjectIds.SequenceEqual([first.Key])
            && ReferenceEquals(vm.SelectedDevice?.Device, first.Device) && ObjectCaption("#0 · Object 0 000"));
        rowCheck.IsChecked = false;
        await Settle();
        check("row checkbox clears selection and restores the device inspector", projectTab.SelectedObjectIds.Count == 0
            && table.SelectedItems!.Count == 0 && DeviceName() == "Device 0");

        var selected = Row("D-1_O-37");
        table.SelectedItems!.Add(selected);
        await Settle();
        check("native table selection stores the same object key", projectTab.SelectedObjectIds.SequenceEqual([selected.Key]));
        check("native table selection resolves the selected device", ReferenceEquals(vm.SelectedDevice?.Device, selected.Device));
        check("single-object inspector uses that object's current facts", ObjectCaption("#37 · Object 1 037"));
        table.SelectedItems.Add(Row("D-2_O-38"));
        await Settle();
        var multi = body.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "MultiInspectorObjects");
        check("multiple selection keeps both objects in the inspector", multi.ItemsSource is IEnumerable<WorkbenchRow> items
            && items.Select(r => r.Object!.Id).ToHashSet().SetEquals(["D-1_O-37", "D-2_O-38"]));
        table.SelectedItems.Clear();
        await Settle();
        check("clearing selection restores the device inspector", projectTab.SelectedObjectIds.Count == 0 && DeviceName() == "Device 0");

        search.Text = "Object 2 079";
        await Settle();
        check("filter includes an object outside the initially visible rows", Rows().Count(r => r.Object is not null) == 1
            && Rows().Single(r => r.Object is not null).Object!.Id == "D-2_O-79");
        var last = Row("D-2_O-79");
        table.SelectedItems.Add(last);
        await Settle();
        check("filtered selection and inspector resolve the matching device", ReferenceEquals(vm.SelectedDevice?.Device, last.Device)
            && ObjectCaption("#79 · Object 2 079"));
        search.Text = "";
        await Settle();
        check("clearing the filter preserves selected object identity", Rows().Count(r => r.Object is not null) == 240
            && table.SelectedItems.OfType<WorkbenchRow>().Single().Object!.Id == "D-2_O-79"
            && projectTab.SelectedObjectIds.SequenceEqual([last.Key]) && ObjectCaption("#79 · Object 2 079"));
        projectTab.CollapsedDeviceIds.Add("D-2");
        window.RefreshWorkspace();
        await Settle();
        check("collapsed children do not leak into the inspector", !Rows().Any(r => r.Device.Id == "D-2" && r.Object is not null)
            && projectTab.SelectedObjectIds.Contains(last.Key) && DeviceName() == "Device 0");
        projectTab.CollapsedDeviceIds.Clear();
        window.RefreshWorkspace();
        await Settle();
        check("expanding restores the selected child and inspector", table.SelectedItems.OfType<WorkbenchRow>().Single().Object!.Id == "D-2_O-79"
            && ObjectCaption("#79 · Object 2 079"));

        var deviceTab = window.OpenTarget(new("device", "D-1"));
        await Settle();
        check("device tab contains only its current objects", Rows().Count(r => r.Object is not null) == 80
            && Rows().All(r => r.Device.Id == "D-1") && DeviceName() == "Device 1");
        var deviceSelected = Row("D-1_O-17");
        table.SelectedItems.Add(deviceSelected);
        search.Text = "Object 1 017";
        await Settle();
        check("device tab has independent filter and selection", deviceTab.Filter == "Object 1 017"
            && deviceTab.SelectedObjectIds.SequenceEqual([deviceSelected.Key]) && projectTab.Filter == ""
            && projectTab.SelectedObjectIds.SequenceEqual([last.Key]));
        window.Workspace.ActiveTab = projectTab;
        await Settle();
        check("project tab restores its own selection and inspector", Rows().Count(r => r.Object is not null) == 240
            && table.SelectedItems.OfType<WorkbenchRow>().Single().Object!.Id == "D-2_O-79"
            && ObjectCaption("#79 · Object 2 079"));
        window.Workspace.ActiveTab = deviceTab;
        await Settle();
        check("returning to a device restores its filter and current inspector", search.Text == "Object 1 017"
            && Rows().Count(r => r.Object is not null) == 1 && ObjectCaption("#17 · Object 1 017"));
        window.Workspace.ActiveTab = projectTab;
        await Settle();

        var original = vm.Project!;
        var originalDevice = ProjectQuery.DeviceList(original).Single(d => d.Id == "D-2");
        vm.Edit("Change selected object", project =>
        {
            var device = ProjectQuery.DeviceList(project).Single(d => d.Id == "D-2");
            device.Name = "Changed device";
            device.ComObjectRefs.Single(o => o.Id == "D-2_O-79").Text = "Changed object";
        });
        window.RefreshWorkspace();
        await Settle();
        check("edit refreshes table and inspector with the live mutation", Row("D-2_O-79").Name == "Changed object"
            && ObjectCaption("#79 · Changed object") && CurrentRows() && vm.CanUndo);
        vm.UndoCommand.Execute(null);
        window.RefreshWorkspace();
        await Settle();
        var restored = vm.Project!;
        check("undo replaces the project while preserving its id", !ReferenceEquals(restored, original) && restored.Id == original.Id);
        check("undo never reuses old projected device or object references", CurrentRows()
            && !ReferenceEquals(Row("D-2_O-79").Device, originalDevice));
        check("undo restores selected object content and inspector", Row("D-2_O-79").Name == "Object 2 079"
            && ObjectCaption("#79 · Object 2 079") && projectTab.SelectedObjectIds.SequenceEqual([last.Key]));
        table.SelectedItems.Clear();
        table.SelectedItems.Add(Row("D-2_O-79"));
        await Settle();
        check("selection after undo resolves the replacement model", ReferenceEquals(vm.SelectedDevice?.Device, Row("D-2_O-79").Device)
            && !ReferenceEquals(vm.SelectedDevice?.Device, originalDevice));
        vm.RedoCommand.Execute(null);
        window.RefreshWorkspace();
        await Settle();
        check("redo also replaces the model and refreshes projected content", !ReferenceEquals(vm.Project, restored)
            && CurrentRows() && Row("D-2_O-79").Name == "Changed object" && ObjectCaption("#79 · Changed object"));

        var replacement = Fixture();
        ProjectQuery.DeviceList(replacement).Single(d => d.Id == "D-2").ComObjectRefs
            .Single(o => o.Id == "D-2_O-79").Text = "Replacement object";
        vm.CreateProject(replacement, null);
        var replacementState = (vm.IsDirty, vm.CanUndo, vm.CanRedo);
        var replacementXml = Knxproj.ToXml(replacement).ToString();
        window.RefreshWorkspace();
        await Settle();
        check("opening another instance with the same id cannot reuse prior rows", ReferenceEquals(vm.Project, replacement)
            && CurrentRows() && Row("D-2_O-79").Name == "Replacement object" && ObjectCaption("#79 · Replacement object"));
        check("refresh preserves replacement model, undo and unsaved state", vm.ProjectPath == ""
            && (vm.IsDirty, vm.CanUndo, vm.CanRedo) == replacementState
            && Knxproj.ToXml(replacement).ToString() == replacementXml);
    }

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static Project Fixture()
    {
        var project = new Project { Id = "P-REFRESH", Name = "Projection refresh" };
        var installation = new Installation { Id = "I-REFRESH" };
        var area = new Area { Id = "A-REFRESH", Address = 1 };
        var line = new Line { Id = "L-REFRESH", Address = 1, Name = "Line" };
        project.Installations.Add(installation);
        installation.Areas.Add(area);
        area.Lines.Add(line);
        for (var index = 0; index < 3; index++)
        {
            var device = new DeviceInstance { Id = $"D-{index}", Name = $"Device {index}",
                ProductRefId = "SYNTHETIC", Address = new(1, 1, (byte)(index + 1)) };
            line.Devices.Add(device);
            project.Workbench.Devices[device.Id] = new() { Demo = true, Application = "Synthetic" };
            for (var number = 0; number < 80; number++)
            {
                var obj = new ComObjectInstanceRef { Id = $"{device.Id}_O-{number}", ComObjectId = $"CO-{number}",
                    Number = number, Text = $"Object {index} {number:D3}" };
                device.ComObjectRefs.Add(obj);
                project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { Dpt = "1.001", Bits = 1, Flags = "CT" };
            }
        }
        return project;
    }
}
