using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

internal static class SmartLinkDialogChecks
{
    internal static async Task<(int Checks, int Failures)> Run(Window owner)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "Smart link dialog: " + name); if (!ok) failures++; }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static T Control<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        var root = Path.Combine(Path.GetTempPath(), "buschord-smart-dialog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var library = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
        var vm = new MainWindowViewModel(library); var project = Fixture(); vm.CreateProject(project, null);
        var shell = new WorkbenchWindow(vm, new(), null, library,
            new ProgrammingStateStore(Path.Combine(root, "demo-state")));
        Window? dialog = null;
        try
        {
            shell.Show(owner); await Task.Delay(100);
            var menu = Control<Menu>(shell, "MainMenu");
            var commands = menu.Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).ToList();
            Check("menu exposes smart linking and both mail-merge data sources", commands.Any(m => Equals(m.Header, WorkbenchStrings.Get("SmartTitle")))
                && commands.Any(m => Equals(m.Header, WorkbenchStrings.Get("MergeDevices"))) && commands.Any(m => Equals(m.Header, WorkbenchStrings.Get("MergeGroups"))));
            var smart = commands.Single(m => Equals(m.Header, WorkbenchStrings.Get("SmartTitle")));
            Window? Open()
            {
                smart.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows
                    .FirstOrDefault(w => w.Name == "SmartLinkReviewDialog" && w.IsVisible);
            }
            var before = Knxproj.ToXml(vm.Project!).ToString(); dialog = Open(); await Task.Delay(80);
            if (dialog is null) { Check("menu opens the actual review dialog", false); return (checks, failures); }
            var apply = Control<Button>(dialog, "SmartLinkApply");
            var results = Control<ListBox>(dialog, "SmartLinkResults");
            var boxes = results.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Check("review shows compatible and blocked rows before changing the project", results.Items.Count == 2 && boxes.Length == 2
                && boxes.Count(b => b.IsEnabled) == 1 && apply.IsEnabled && Knxproj.ToXml(vm.Project!).ToString() == before && !vm.CanUndo);
            var selected = boxes.Single(b => b.IsEnabled); selected.IsChecked = false; await Task.Delay(20);
            Check("clearing the explicit selection disables Apply", !apply.IsEnabled);
            selected.IsChecked = true; await Task.Delay(20); Click(apply); await Task.Delay(40);
            Check("actual Apply button commits only checked compatible rows", !dialog.IsVisible && vm.CanUndo
                && ProjectQuery.DeviceList(vm.Project!).First().ComObjectRefs.Single().Links.SequenceEqual(["G1"])
                && ProjectQuery.DeviceList(vm.Project!).Last().ComObjectRefs.Single().Links.Count == 0);
            vm.UndoCommand.Execute(null);
            Check("one undo restores the whole routed dialog action", ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0) && !vm.CanUndo);
            dialog = Open(); await Task.Delay(80); apply = Control<Button>(dialog!, "SmartLinkApply");
            vm.Project!.Name = "Changed after review"; Click(apply); await Task.Delay(30);
            Check("stale review is rejected inline by the actual Apply button", dialog.IsVisible && !apply.IsEnabled
                && !string.IsNullOrWhiteSpace(Control<TextBlock>(dialog, "SmartLinkError").Text)
                && ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0));
            Click(Control<Button>(dialog, "SmartLinkRefresh")); await Task.Delay(40);
            Check("review again creates a new usable preview", apply.IsEnabled && string.IsNullOrWhiteSpace(Control<TextBlock>(dialog, "SmartLinkError").Text));
            var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel); Click(cancel); await Task.Delay(30);
            Check("cancel preserves the latest project without an undo entry", !dialog.IsVisible && !vm.CanUndo
                && ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0));
        }
        finally
        {
            if (dialog?.IsVisible == true) dialog.Close();
            await NativeWindowCleanup.CloseAsync(shell);
            Directory.Delete(root, true);
        }
        return (checks, failures);
    }

    private static Project Fixture()
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic smart dialog", Areas = 1, LinesPerArea = 1 });
        var range = new GroupRange { Id = "GR-smart", Name = "Fixture", RangeStart = 1, RangeEnd = 65535 };
        range.Addresses.Add(new() { Id = "G1", Name = "Living light", Address = GroupAddress.ThreeLevel(1, 0, 1), DatapointType = "DPST-1-1" });
        project.Installations[0].MainRanges.Add(range); project.Workbench.Groups["G1"] = new() { Bits = 1, Secure = false };
        for (var number = 1; number <= 2; number++)
        {
            var device = new DeviceInstance { Id = "D" + number, Name = "Living", ProductRefId = "SYNTHETIC", Address = new(1, 1, (byte)number) };
            device.ComObjectRefs.Add(new() { Id = "O1", ComObjectId = "O1", Text = "light", Number = 1 });
            project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
            project.Workbench.Objects[new ObjectKey(device.Id, "O1").ToString()] = new() { Active = true, Dpt = "1.001", Bits = number == 1 ? 1 : 8, Flags = "CW" };
        }
        return project;
    }
}
