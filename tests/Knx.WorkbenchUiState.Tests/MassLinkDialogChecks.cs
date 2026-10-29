using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;
using Knx.Workbench;

internal static class MassLinkDialogChecks
{
    internal static async Task Run(WorkbenchWindow owner, string theme, Action<string, bool> check)
    {
        void Check(string name, bool value) => check(theme + " mass link table: " + name, value);
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "buschord-mass-dialog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var library = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalogues")));
        var vm = new MainWindowViewModel(library); vm.CreateProject(Fixture(), null);
        var shell = new WorkbenchWindow(vm, new(), null, library, new ProgrammingStateStore(Path.Combine(root, "demo-state")));
        Window? dialog = null;
        static async Task Settle() { await Task.Delay(100); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            shell.Show(owner); await Settle();
            var before = Knxproj.ToXml(vm.Project!).ToString();
            dialog = (Window?)typeof(WorkbenchWindow).GetMethod("ShowSmartLinkDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null);
            await Settle();
            T Control<T>(string name) where T : Control => dialog!.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            var results = Control<ListBox>("SmartLinkResults");
            var apply = Control<Button>("SmartLinkApply");
            var summary = Control<TextBlock>("SmartLinkSummary");
            Check("all five review states are counted before mutation", results.Items.Count == 5
                && summary.Text!.StartsWith(WorkbenchStrings.Get("MassSummary", 1, 1, 1, 1, 1), StringComparison.Ordinal)
                && before == Knxproj.ToXml(vm.Project!).ToString() && !vm.CanUndo);
            ComboBox Target(string device) => results.GetVisualDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "MassLinkTarget" && Equals(combo.Tag, new ObjectKey(device, "O1").ToString()));
            var unknown = Target("Epsilon");
            Check("unknown compatibility is visibly blocked", unknown.GetVisualAncestors().OfType<Grid>().First()
                .GetVisualDescendants().OfType<CheckBox>().Single().IsEnabled == false);
            var beta = Target("Beta");
            beta.SelectedItem = beta.ItemsSource!.Cast<object>().Single(choice => choice.ToString()!.StartsWith("1/0/3 ·", StringComparison.Ordinal));
            await Settle();
            Check("per-row target resolves ambiguity without editing project", summary.Text!.StartsWith(WorkbenchStrings.Get("MassSummary", 2, 0, 1, 1, 1), StringComparison.Ordinal)
                && before == Knxproj.ToXml(vm.Project!).ToString() && apply.IsEnabled);
            Control<CheckBox>("MassLinkOnlyProblems").IsChecked = true; await Settle();
            Check("only-problems filter preserves selected ready rows", results.Items.Count == 3 && apply.IsEnabled
                && summary.Text!.Contains(WorkbenchStrings.Get("SmartSelected", 2), StringComparison.Ordinal));
            Click(apply); await Settle();
            Check("apply commits the reviewed targets together", !dialog.IsVisible && vm.CanUndo
                && ProjectQuery.DeviceList(vm.Project!).Single(device => device.Id == "Alpha").ComObjectRefs.Single().Links.SequenceEqual(["G-Alpha"])
                && ProjectQuery.DeviceList(vm.Project!).Single(device => device.Id == "Beta").ComObjectRefs.Single().Links.SequenceEqual(["G-Beta2"])
                && ProjectQuery.DeviceList(vm.Project!).Where(device => device.Id is not ("Alpha" or "Beta")).All(device => device.ComObjectRefs.Single().Links.Count == 0));
            vm.UndoCommand.Execute(null);
            Check("one undo restores the entire table operation", !vm.CanUndo && before == Knxproj.ToXml(vm.Project!).ToString());
        }
        finally
        {
            if (dialog?.IsVisible == true) dialog.Close();
            await NativeWindowCleanup.CloseAsync(shell);
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(parent)
                && Path.GetFileName(resolved).StartsWith("buschord-mass-dialog-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static Project Fixture()
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic mass table" });
        project.Installations[0].Areas[0].Address = 1; project.Installations[0].Areas[0].Lines[0].Address = 1;
        var range = new GroupRange { Id = "GR-Mass", Name = "Synthetic", RangeStart = 0, RangeEnd = 65535 };
        project.Installations[0].MainRanges.Add(range); byte address = 1;
        foreach (var (id, name, dpt, bits) in new[]
        {
            ("G-Alpha", "Alpha AlphaValue", "1.001", 1), ("G-Beta1", "Beta BetaValue", "1.001", 1),
            ("G-Beta2", "Beta BetaValue", "1.001", 1), ("G-Gamma", "Gamma GammaValue", "9.001", 16),
            ("G-Epsilon", "Epsilon EpsilonValue", "1.001", 1),
        })
        {
            range.Addresses.Add(new() { Id = id, Name = name, DatapointType = dpt, Address = GroupAddress.ThreeLevel(1, 0, address++) });
            project.Workbench.Groups[id] = new() { Bits = bits, Secure = false };
        }
        address = 1;
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
        {
            var device = new DeviceInstance { Id = name, Name = name, ProductRefId = "SYNTHETIC", Address = new(1, 1, address++) };
            device.ComObjectRefs.Add(new() { Id = "O1", ComObjectId = "O1", Number = 1, Text = name + "Value" });
            project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
            project.Workbench.Objects[new ObjectKey(name, "O1").ToString()] = new() { Active = true,
                Dpt = name == "Epsilon" ? null : name == "Delta" ? "5.001" : "1.001", Bits = name == "Epsilon" ? null : 1 };
        }
        return project;
    }
}
