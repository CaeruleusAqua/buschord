using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

internal static class BatchInspectorDialogChecks
{
    internal static async Task<(int Checks, int Failures)> Run(Window owner)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "Batch inspector: " + name); if (!ok) failures++; }
        static T Named<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var root = Path.Combine(Path.GetTempPath(), "buschord-batch-inspector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")));
        var vm = new MainWindowViewModel(catalog); vm.CreateProject(Fixture(), null);
        var shell = new WorkbenchWindow(vm, new() { InspectorVisible = true }, null, catalog, new ProgrammingStateStore(Path.Combine(root, "demo-state")));
        Window? dialog = null;
        async Task Select(int count)
        {
            shell.OpenTarget(new("project", vm.Project!.Id), "objects");
            var tab = shell.Workspace.ActiveTab!; tab.SelectedObjectIds.Clear();
            foreach (var row in WorkbenchProjection.Rows(vm.Project, tab).Where(r => r.Object is not null).Take(count)) tab.SelectedObjectIds.Add(row.Key);
            shell.RefreshWorkspace(); await Task.Delay(60);
        }
        async Task<Window> Open()
        {
            Click(Named<Button>(shell, "MultiInspectorLink")); await Task.Delay(70);
            return ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.Single(w => w.Name == "BatchLinkDialog" && w.IsVisible);
        }
        try
        {
            shell.Show(owner); await Task.Delay(80); await Select(3);
            var objects = Named<ListBox>(shell, "MultiInspectorObjects");
            var inspector = Named<StackPanel>(shell, "InspectorBody");
            string Value(string id) => inspector.GetVisualDescendants().OfType<TextBlock>()
                .Single(c => AutomationProperties.GetAutomationId(c) == id).Text ?? "";
            Check("multi-inspector lists objects and reports types, mixed flags and devices", objects.Items.Count == 3
                && Value("inspector.multi.types").Contains("1.001", StringComparison.Ordinal)
                && Value("inspector.multi.flags") == WorkbenchStrings.Get("MultiMixed") && Value("inspector.multi.devices") == "3"
                && !inspector.GetVisualDescendants().OfType<TextBox>().Any() && !vm.CanUndo);
            var footer = Named<Button>(shell, "MultiInspectorLink");
            Check("batch action stays in the fixed footer outside scrolling properties", Named<Border>(shell, "InspectorFooterHost").IsVisible
                && !footer.GetVisualAncestors().OfType<ScrollViewer>().Any());
            dialog = await Open(); var picker = Named<ComboBox>(dialog, "BatchGroupPicker");
            var search = Named<TextBox>(dialog, "BatchGroupSearch"); var apply = Named<Button>(dialog, "BatchLinkApply");
            picker.SelectedItem = ProjectQuery.GroupAddresses(vm.Project!).Single(g => g.Id == "G1"); await Task.Delay(30);
            Check("footer opens the real atomic dialog and blocks a mixed-compatible selection", Named<ListBox>(dialog, "BatchLinkPreview").Items.Count == 3
                && !apply.IsEnabled && Named<TextBlock>(dialog, "BatchLinkError").Text == WorkbenchStrings.Get("BatchIncompatible", 1));
            Click(apply);
            Check("a rejected batch creates neither partial links nor an undo step", ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0) && !vm.CanUndo);
            shell.Workspace.ActiveTab!.SelectedObjectIds.Clear();
            picker.SelectedItem = ProjectQuery.GroupAddresses(vm.Project!).Single(g => g.Id == "G2");
            picker.SelectedItem = ProjectQuery.GroupAddresses(vm.Project!).Single(g => g.Id == "G1"); await Task.Delay(30);
            Check("later main-view selection changes cannot alter frozen batch members", Named<ListBox>(dialog, "BatchLinkPreview").Items.Count == 3 && !apply.IsEnabled);
            search.Text = "no such group"; await Task.Delay(30);
            Check("filtering away the chosen target clears it and prevents hidden-target apply", picker.Items.Count == 0 && picker.SelectedItem is null && !apply.IsEnabled);
            search.Text = "1/0/1"; await Task.Delay(30);
            Check("group picker searches numeric address", picker.Items.Count == 1 && ((GroupAddressEntry)picker.Items[0]!).Id == "G1");
            search.Text = "yArD"; await Task.Delay(30);
            Check("group picker searches names without case sensitivity", picker.Items.Count == 1 && ((GroupAddressEntry)picker.Items[0]!).Id == "G1");
            search.Text = "DPST-1-1"; await Task.Delay(30);
            Check("group picker searches datapoint types", picker.Items.Count == 1 && ((GroupAddressEntry)picker.Items[0]!).Id == "G1");
            dialog.Close();

            await Select(2); dialog = await Open(); picker = Named<ComboBox>(dialog, "BatchGroupPicker"); apply = Named<Button>(dialog, "BatchLinkApply");
            picker.SelectedItem = ProjectQuery.GroupAddresses(vm.Project!).Single(g => g.Id == "G1"); await Task.Delay(30);
            Click(apply); await Task.Delay(50);
            Check("footer workflow applies both compatible devices in one edit", !dialog.IsVisible && vm.CanUndo
                && ProjectQuery.DeviceList(vm.Project!).Take(2).All(d => d.ComObjectRefs.Single().Links.SequenceEqual(["G1"]))
                && ProjectQuery.DeviceList(vm.Project!).Last().ComObjectRefs.Single().Links.Count == 0);
            vm.UndoCommand.Execute(null);
            Check("one undo restores the complete reviewed batch", !vm.CanUndo && ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0));
            await Select(2);
            var objectList = Named<ListBox>(shell, "MultiInspectorObjects");
            var objectButton = objectList.GetVisualDescendants().OfType<Button>().First(); Click(objectButton); await Task.Delay(40);
            Check("multi-object rows navigate to their actual communication object", shell.Workspace.ActiveTab?.Target.Kind == "device"
                && shell.Workspace.ActiveTab.SelectedObjectIds.Count == 1);

            vm.CreateProject(Fixture(alreadyLinked: true), null); await Select(2); dialog = await Open();
            picker = Named<ComboBox>(dialog, "BatchGroupPicker"); apply = Named<Button>(dialog, "BatchLinkApply");
            picker.SelectedItem = ProjectQuery.GroupAddresses(vm.Project!).Single(g => g.Id == "G1"); await Task.Delay(30); Click(apply);
            Check("already-linked batches stay explicit no-ops without undo entries", !apply.IsEnabled && !vm.CanUndo
                && Named<TextBlock>(dialog, "BatchLinkError").Text == WorkbenchStrings.Get("BatchAllAlreadyLinked"));
            dialog.Close();
            vm.CreateProject(Fixture(), null); await Select(2); dialog = await Open();
            picker = Named<ComboBox>(dialog, "BatchGroupPicker"); apply = Named<Button>(dialog, "BatchLinkApply");
            picker.SelectedItem = ProjectQuery.GroupAddresses(vm.Project!).Single(g => g.Id == "G1");
            vm.CreateProject(Fixture(), null); Click(apply); await Task.Delay(30);
            Check("same-id project replacement rejects the prior dialog without edits", dialog.IsVisible && !vm.CanUndo
                && ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0)
                && !string.IsNullOrWhiteSpace(Named<TextBlock>(dialog, "BatchLinkError").Text));
        }
        finally
        {
            if (dialog?.IsVisible == true) dialog.Close(); if (shell.IsVisible) shell.Close();
            Directory.Delete(root, true);
        }
        return (checks, failures);
    }

    private static Project Fixture(bool alreadyLinked = false)
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic batch inspector", Areas = 1, LinesPerArea = 1 });
        var range = new GroupRange { Id = "GR-batch", RangeStart = 0, RangeEnd = 65535 }; project.Installations[0].MainRanges.Add(range);
        range.Addresses.Add(new() { Id = "G1", Address = GroupAddress.ThreeLevel(1, 0, 1), Name = "Yard actuation", DatapointType = "DPST-1-1" });
        range.Addresses.Add(new() { Id = "G2", Address = GroupAddress.ThreeLevel(1, 0, 2), Name = "Other value", DatapointType = "DPST-5-1" });
        project.Workbench.Groups["G1"] = new() { Bits = 1, Secure = false }; project.Workbench.Groups["G2"] = new() { Bits = 8, Secure = false };
        for (var n = 1; n <= 3; n++)
        {
            var device = new DeviceInstance { Id = "D" + n, ProductRefId = "SYNTHETIC", Name = "Synthetic " + n, Address = new(1, 1, (byte)n) };
            var obj = new ComObjectInstanceRef { Id = "O1", ComObjectId = "O1", Number = 1, Text = "Synthetic object" };
            if (alreadyLinked && n <= 2) obj.Links.Add("G1");
            device.ComObjectRefs.Add(obj); project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
            project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { Bits = n == 3 ? 8 : 1, Dpt = "1.001", Active = true, Flags = n == 1 ? "CW" : "CRW" };
        }
        return project;
    }
}
