using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Workbench;

internal static class V4ToolChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(WorkbenchWindow window, string name, params object?[] args) =>
        typeof(WorkbenchWindow).GetMethod(name, Private)!.Invoke(window, args)!;
    private static IEnumerable<TextBlock> Text(Control view) => view.GetVisualDescendants().OfType<TextBlock>();
    private static void Check(string name, bool value)
    {
        if (!value) throw new Exception("FAIL V4 " + name);
        Console.WriteLine("PASS V4 " + name);
    }
    public static async Task Run(WorkbenchWindow main)
    {
        var project = main.ViewModel.Project!;
        var device = ProjectQuery.DeviceList(project).First();
        var group = ProjectQuery.GroupAddresses(project).First();
        main.ShowTool("programming");
        var sessions = (WorkbenchSessions)typeof(WorkbenchWindow).GetField("_sessions", Private)!.GetValue(main)!;
        var foreignThreadRejected = await Task.Run(() =>
        {
            try { _ = sessions.Programming.NeedsProgramming(device.Id); return false; }
            catch (InvalidOperationException) { return true; }
        });
        Check("session access still rejects a background thread", foreignThreadRejected);
        var host = main.FindControl<ContentControl>("ToolHost")!;
        var view = (Control)host.Content!;
        var job = sessions.Programming.Enqueue(device.Id);
        await Task.Delay(100);
        var before = WorkbenchStrings.Get("ProgrammingNeed", ProjectQuery.DeviceList(project).Count());
        Check("initial programming need is visible", Text(view).Any(t => t.Text == before));
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(job.Status) && job.Status == ProgrammingJobStatus.Running) job.ReportProgress(37);
        };
        var run = sessions.Programming.RunAsync();
        await Task.Delay(30);
        Check("running status and intermediate progress update", Text(view).Any(t => t.Text == "RUNNING")
            && Text(view).Any(t => t.Text == "37 %"));
        await run;
        await Task.Delay(200);
        Check("completed row updates without recreating tool", ReferenceEquals(host.Content, view)
            && Text(view).Any(t => t.Text == "DONE") && Text(view).Any(t => t.Text == "100 %"));
        Check("programming need updates after success", Text(view).Any(t => t.Text ==
            WorkbenchStrings.Get("ProgrammingNeed", ProjectQuery.DeviceList(project).Count() - 1)));
        var failure = sessions.Programming.Enqueue(device.Id);
        typeof(WorkbenchWindow).GetField("_simulateProgrammingFailure", Private)!.SetValue(main, true);
        await sessions.Programming.RunAsync();
        await Task.Delay(100);
        Check("failed result and error update live", Text(view).Any(t => t.Text == "FAILED") && Text(view).Any(t => t.Text == failure.Error));
        sessions.Programming.Retry(failure);
        await Task.Delay(50);
        Check("retry resets result and progress live", Text(view).Any(t => t.Text == "QUEUED") && Text(view).Any(t => t.Text == "0 %"));
        typeof(WorkbenchWindow).GetField("_simulateProgrammingFailure", Private)!.SetValue(main, false);
        await sessions.Programming.RunAsync();
        var downloaded = sessions.Programming.ExportDownloadedJson();
        sessions.Programming.ImportDownloadedJson(DeviceProgrammingStates.Serialize(project.Id, []));
        await Task.Delay(50);
        Check("imported programming baseline updates need live", Text(view).Any(t => t.Text == before));
        sessions.Programming.ImportDownloadedJson(downloaded);
        main.ShowTool("monitor");
        sessions.Monitor.PinFilter(new(MonitorScope.GroupAddress, group.Id));
        await Task.Delay(100);
        view = (Control)host.Content!;
        Check("pin updates existing filter selector", view.GetLogicalDescendants().OfType<ComboBox>().Last().SelectedIndex == 2);
        Check("pin shows actual group address", Text(view).Any(t => t.Text?.Contains(group.Address.ToString()) == true));
        main.ShowTool("changes"); main.ShowTool("monitor");
        view = (Control)host.Content!;
        Check("reopened monitor uses session filter", view.GetLogicalDescendants().OfType<ComboBox>().Last().SelectedIndex == 2);
        sessions.Monitor.Start();
        sessions.Monitor.SimulateWrite(group.Id, "1", device.Id);
        sessions.Monitor.FlushDisplay();
        await Task.Delay(100);
        var source = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, device.Address.ToString()));
        source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("telegram source navigates to actual device", main.Workspace.ActiveTab?.Target == new WorkbenchTarget("device", device.Id));
        var destination = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, group.Address.ToString()));
        destination.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("telegram destination navigates to group", main.Workspace.ActiveTab?.Target == new WorkbenchTarget("ga", group.Id));
        sessions.Monitor.Stop(); sessions.Monitor.ClearDisplay(); sessions.Monitor.PinFilter(new(MonitorScope.All));
        Check("search window title is search", Equals(Call(main, "ToolWindowTitle", "search"), WorkbenchStrings.Get("SidebarSearch")));

        var other = ProjectQuery.DeviceList(project).First(d => d != device);
        var otherObject = other.ComObjectRefs.First();
        if (!otherObject.Links.Contains(group.Id)) otherObject.Links.Add(group.Id);
        project.Workbench.Objects[new ObjectKey(other.Id, otherObject.Id).ToString()] = new() { Flags = "CW", Active = true };
        var localObject = device.ComObjectRefs.First();
        if (!localObject.Links.Contains(group.Id)) localObject.Links.Add(group.Id);
        project.Workbench.Objects[new ObjectKey(device.Id, localObject.Id).ToString()] = new() { Flags = "CT", SendingGroupId = group.Id };
        main.Workspace.ActiveTab!.CollapsedDeviceIds.Add(device.Id);
        main.OpenTarget(new("device", device.Id), "relations");
        await Task.Delay(100);
        var relations = main.FindControl<ContentControl>("RelationsHost")!;
        Check("local relationships expose remote counterpart and actual flags", relations.GetVisualDescendants().OfType<Button>()
            .Any(b => b.Content?.ToString()?.Contains(other.Address.ToString()) == true)
            && Text(relations).Any(t => t.Text?.Contains("CW") == true)
            && Text(relations).Any(t => t.Text?.Contains("CT") == true));
        Check("relationships show supported sender and receiver directions", Text(relations).Any(t => t.Text?.StartsWith("→ [CT]") == true)
            && Text(relations).Any(t => t.Text?.StartsWith("← [CW]") == true));
        Check("relationship removal remains available", relations.GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, WorkbenchStrings.Get("Remove"))));
        project.Workbench.Objects[new ObjectKey(device.Id, localObject.Id).ToString()].Flags = "CR";
        main.RefreshWorkspace();
        await Task.Delay(100);
        Check("read responder is a sender without a transmit flag", Text(relations).Any(t => t.Text?.StartsWith("→ [CR]") == true));
        project.Workbench.Objects[new ObjectKey(device.Id, localObject.Id).ToString()].Flags = "CT";
        main.RefreshWorkspace();
        await Task.Delay(100);
        var remoteLine = relations.GetLogicalDescendants().OfType<StackPanel>().First(panel => panel.Children.OfType<Button>()
            .Any(b => b.Content?.ToString()?.StartsWith(other.Address + " ·") == true));
        remoteLine.Children.OfType<Button>().Single(b => Equals(b.Content, WorkbenchStrings.Get("Remove")))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("remote relationship removal edits the actual counterpart", !otherObject.Links.Contains(group.Id) && localObject.Links.Contains(group.Id));
        otherObject.Links.Add(group.Id);
        device.Name = null; other.Name = null;
        var unrelated = ProjectQuery.DeviceList(project).Last(d => d != device && d != other);
        foreach (var obj in unrelated.ComObjectRefs) obj.Links.Remove(group.Id);
        unrelated.Name = null;
        foreach (var line in project.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines))
        {
            line.Devices.Remove(unrelated);
            foreach (var segment in line.Segments) segment.Devices.Remove(unrelated);
        }
        foreach (var room in WorkbenchProjection.Spaces(project)) room.DeviceRefs.Remove(unrelated.Id);
        var outsideLine = new Line { Id = "outside-line", Address = 7 };
        outsideLine.Devices.Add(unrelated);
        project.Installations[0].Areas[0].Lines.Add(outsideLine);
        project.Installations[0].Locations.Add(null, "Outside", SpaceKind.Room, "outside-room").AssignDevice(unrelated);
        main.RefreshWorkspace();
        foreach (var target in new[] { new WorkbenchTarget("device", device.Id), new("ga", group.Id), new("room", "living"),
                     new("line", project.Installations[0].Areas[0].Lines[0].Id), new("project", project.Id) })
        {
            main.OpenTarget(target, "overview");
            var list = main.FindControl<ContentControl>("ToolPageHost")!.GetLogicalDescendants().OfType<ListBox>().SingleOrDefault();
            var findings = list?.ItemsSource?.Cast<CheckFinding>().ToArray() ?? [];
            Check(target.Kind + " overview does not leak unrelated device findings", target.Kind != "device"
                || findings.All(f => f.Path != other.Address.ToString()));
            Check(target.Kind + " excludes device outside the actual target", target.Kind == "project"
                || findings.All(f => f.Path != unrelated.Address.ToString()));
            Check(target.Kind + " overview retains relevant findings", findings.Any(f => f.Path == device.Address.ToString()));
        }
        main.ShowTool("problems");
        Check("global problems retains other device findings", ((Control)host.Content!).GetLogicalDescendants().OfType<ListBox>()
            .Single().ItemsSource!.Cast<CheckFinding>().Any(f => f.Path == other.Address.ToString()));
        localObject.Links.Remove(group.Id);
        main.OpenTarget(new("device", device.Id), "overview");
        var scoped = main.FindControl<ContentControl>("ToolPageHost")!.GetLogicalDescendants().OfType<ListBox>()
            .Single().ItemsSource!.Cast<CheckFinding>().ToArray();
        Check("local overview retains invalid sending connector findings", scoped.Any(f => f.Code == "P052"
            && f.Path == $"{new ObjectKey(device.Id, localObject.Id)} → {group.Id}"));

        // Start the existing docking checks with a fresh synthetic project/session.
        main.ViewModel.CreateProject(WorkbenchJson.CreateDemo(), null);
        main.RefreshWorkspace();
        main.ShowTool("programming");
        Call(main, "OnFloatTool", null, new RoutedEventArgs());
        var floats = (List<Window>)typeof(WorkbenchWindow).GetField("_toolWindows", Private)!.GetValue(main)!;
        var floatingProgramming = floats.Single();
        await Task.Delay(100);
        var removedId = main.ViewModel.Devices.Last().Device.Id;
        main.ViewModel.Edit("remove fixture device", p =>
        {
            foreach (var line in p.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines))
            {
                line.Devices.RemoveAll(d => d.Id == removedId);
                foreach (var segment in line.Segments) segment.Devices.RemoveAll(d => d.Id == removedId);
            }
        });
        await Task.Delay(100);
        var choices = floatingProgramming.GetVisualDescendants().OfType<ComboBox>().Single();
        Check("floating programming removes stale device choices", !choices.ItemsSource!.Cast<DeviceRow>().Any(r => r.Device.Id == removedId));
        main.ViewModel.UndoCommand.Execute(null);
        await Task.Delay(100);
        Check("floating programming restores device choices after undo", choices.ItemsSource!.Cast<DeviceRow>().Any(r => r.Device.Id == removedId));
        var replacement = new Project { Id = "replacement-project", Name = "Replacement" };
        main.ViewModel.CreateProject(replacement, null);
        main.RefreshWorkspace();
        await Task.Delay(100);
        Check("project replacement closes floated tools before disposing sessions", !floatingProgramming.IsVisible && floats.Count == 0);
        main.ViewModel.CreateProject(WorkbenchJson.CreateDemo(), null);
        main.RefreshWorkspace();
    }
}
