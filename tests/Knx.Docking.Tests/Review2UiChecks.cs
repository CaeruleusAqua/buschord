using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Workbench;

internal static class Review2UiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, args)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static IEnumerable<TextBlock> Text(Control root) => root.GetVisualDescendants().OfType<TextBlock>();
    private static string W(string key) => WorkbenchStrings.Get(key);
    private static void Click(Control root, string key) => root.GetLogicalDescendants().OfType<Button>()
        .First(b => Equals(b.Content, W(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public static async Task Run(WorkbenchWindow main)
    {
        var failures = new List<string>();
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + "REVIEW2 " + name);
            if (!ok) failures.Add(name);
        }
        main.ShowTool("monitor");
        main.Workspace.InspectorVisible = true;
        main.Width = 1000;
        await Task.Delay(150);
        var tools = main.FindControl<Grid>("Tools")!;
        var toolTabs = main.FindControl<StackPanel>("ToolTabs")!.Children.OfType<TabItem>().ToArray();
        Check("D1 visible tool tabs retain a traversable keyboard ancestor chain", tools.IsVisible
            && toolTabs.All(tab => tab.GetVisualAncestors().OfType<Control>()
                .All(c => KeyboardNavigation.GetTabNavigation(c) != KeyboardNavigationMode.None)));
        Check("D1 tool action can take keyboard focus", tools.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, W("Start"))).Focus());
        toolTabs[0].Focus(NavigationMethod.Tab);
        toolTabs[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
        Check("D1 Tab traversal reaches the next visible tool tab", main.FocusManager.GetFocusedElement() == toolTabs[1]);
        main.FindControl<TextBox>("NavSearch")!.Focus(NavigationMethod.Tab);
        var reachedTools = false;
        for (var i = 0; i < 200; i++)
        {
            if (main.FocusManager!.GetFocusedElement() is InputElement focused)
                focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            if (toolTabs.Contains(main.FocusManager.GetFocusedElement())) { reachedTools = true; break; }
        }
        Check("D1 Tab traversal enters the unoccluded tool strip from outside MainColumn", reachedTools);
        main.Workspace.InspectorVisible = false;
        main.Width = 1440;

        var project = main.ViewModel.Project!;
        var count = Field<int>(main, "_issueCount");
        Check("D3 fixture badge initially agrees with checker", count == ProjectCheck.Run(project).Count);
        main.ViewModel.CreateGroupAddress(ProjectQuery.NextFreeGroupAddress(project, 15, 7), "Review2 unused", "DPST-1-1");
        await Task.Delay(150);
        Check("D3 same-instance edit recomputes badge", ReferenceEquals(project, main.ViewModel.Project)
            && ProjectCheck.Run(project).Count != count && Field<int>(main, "_issueCount") == ProjectCheck.Run(project).Count);

        var devices = ProjectQuery.DeviceList(project).Take(2).ToArray();
        devices[0].ComObjectRefs.Add(new ComObjectInstanceRef { Id = "review2-shared", ComObjectId = "review2-shared" });
        devices[1].ComObjectRefs.Add(new ComObjectInstanceRef { Id = "review2-shared", ComObjectId = "review2-shared" });
        var groups = ProjectQuery.GroupAddresses(project).Take(2).ToArray();
        devices[0].ComObjectRefs.Last().Links.Add(groups[0].Id);
        devices[1].ComObjectRefs.Last().Links.Add(groups[1].Id);
        var filter = new MonitorFilter(MonitorScope.ComObject, "review2-shared", devices[0].Id);
        var ids = (IReadOnlySet<string>)Call(main, "ResolveMonitorIds", filter);
        Check("C7 equal KO ids do not mix devices", ids.SetEquals([groups[0].Id]));
        var monitor = Field<WorkbenchSessions>(main, "_sessions").Monitor;
        monitor.PinFilter(filter);
        monitor.Start();
        foreach (var group in groups) monitor.SimulateWrite(group.Id, "1");
        monitor.FlushDisplay();
        await Task.Delay(50);
        Check("C7 native monitor displays only the selected device KO addresses", monitor.FilteredRows.Count == 1
            && main.FindControl<ContentControl>("ToolHost")!.GetVisualDescendants().OfType<ListBox>().Single().ItemCount == 1
            && monitor.FilteredRows[0].GroupAddressId == groups[0].Id);
        monitor.Stop(); monitor.ClearDisplay(); monitor.PinFilter(new(MonitorScope.All));

        main.ShowTool("scan");
        Call(main, "OnFloatTool", null, new RoutedEventArgs());
        var floating = Field<List<Window>>(main, "_toolWindows").Single();
        await Task.Delay(100);
        var scan = (Control)floating.Content!;
        Click(scan, "ScanStart");
        main.ShowTool("changes");
        var host = main.FindControl<ContentControl>("ToolHost")!;
        var changes = host.Content;
        await Task.Delay(1000);
        var sessions = Field<WorkbenchSessions>(main, "_sessions");
        Check("D2 floated scan displays results in its own original view", sessions.LineScan.Results.Count > 0
            && ReferenceEquals(floating.Content, scan) && scan.GetVisualDescendants().OfType<ListBox>()
                .Any(list => list.IsVisible && list.ItemCount == sessions.LineScan.Results.Count));
        Check("D2 scan completion does not overwrite another active docked tool", main.Workspace.ActiveTool == "changes"
            && ReferenceEquals(host.Content, changes));
        var lineChoices = floating.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.SelectedItem is Line);
        lineChoices.SelectedIndex = 1;
        var chosenLine = (Line)lineChoices.SelectedItem!;
        var oldTitle = floating.Title;
        Strings.Language = "it";
        await Task.Delay(150);
        Check("D4 float refresh captures original scan kind, not active changes", floating.Title == W("Scan")
            && floating.Title != oldTitle && floating.GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, W("ScanStart")))
            && ReferenceEquals(sessions, Field<WorkbenchSessions>(main, "_sessions")) && sessions.LineScan.Results.Count > 0);
        Check("D4 language keeps the selected noninitial scan line", floating.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.SelectedItem is Line)
            .SelectedItem is Line selectedLine && selectedLine.Id == chosenLine.Id);
        floating.Close();
        Check("D4 redock uses original floated tool kind", main.Workspace.ActiveTool == "scan");

        var settings = new SettingsWindow();
        settings.Show(main);
        var replacement = new ReplaceProductWindow();
        replacement.Show(main);
        await Task.Delay(100);
        foreach (var (window, names) in new[] { ((Window)settings, new[] { "ThemeBox", "LanguageBox", "BusInterfaceBox", "TunnelAddressBox" }),
                     ((Window)replacement, new[] { "OldBox", "NewBox", "ProgramBox" }) })
            Check("D5 " + window.GetType().Name + " fields have accessible names", names.All(name =>
                AutomationProperties.GetLabeledBy(window.FindControl<Control>(name)!) is TextBlock { Text.Length: > 0 }
                || !string.IsNullOrEmpty(AutomationProperties.GetName(window.FindControl<Control>(name)!))));
        Strings.Language = "fr";
        await Task.Delay(100);
        Check("D5 settings accessible label follows live language", AutomationProperties.GetName(settings.FindControl<ComboBox>("ThemeBox")!) == Strings.Get("OptionsTheme")
            || AutomationProperties.GetLabeledBy(settings.FindControl<ComboBox>("ThemeBox")!) is TextBlock label && label.Text == Strings.Get("OptionsTheme"));
        settings.Close(); replacement.Close();
        main.OpenTarget(new("device", devices[0].Id), "params");
        await Task.Delay(100);
        var demoFields = Field<StackPanel>(main, "_modePanel").GetLogicalDescendants().OfType<Control>()
            .Where(c => c is TextBox or ComboBox).ToArray();
        Check("D5 demo fields name their visible labels", demoFields.Length > 0 && demoFields.All(c =>
            AutomationProperties.GetLabeledBy(c) is TextBlock { Text.Length: > 0 }));

        main.ShowTool("programming");
        sessions = Field<WorkbenchSessions>(main, "_sessions");
        var operations = new[] { "full", "address", "application", "parameters", "group-addresses", "configuration" };
        var jobs = operations.Select(op => sessions.Programming.Enqueue(devices[0].Id, op)).ToArray();
        for (var i = 0; i < 5; i++) Call(jobs[i], "Change", (ProgrammingJobStatus)i, null);
        Call(main, "OnFloatTool", null, new RoutedEventArgs());
        var programmingFloat = Field<List<Window>>(main, "_toolWindows").Single();
        await Task.Delay(100);
        programmingFloat.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;
        var chosenDevice = ((Knx.App.Services.DeviceRow)programmingFloat.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedItem!).Device.Id;
        programmingFloat.GetLogicalDescendants().OfType<ListBox>().Single().SelectedItem = jobs[2];
        foreach (var language in new[] { "de", "en", "fr", "it", "es", "nl", "zh-CN", "pl" })
        {
            Strings.Language = language;
            await Task.Delay(120);
            var text = Text(programmingFloat).Select(t => t.Text).ToArray();
            Check("D6 " + language + " localizes every status and operation in floated rows", Enum.GetValues<ProgrammingJobStatus>()
                .All(status => text.Contains(W($"ProgrammingStatus{status}")))
                && Enum.GetValues<ProgrammingJobType>().All(type => text.Contains(W($"ProgrammingOperation{type}")))
                && !text.Contains("full") && !text.Contains("QUEUED"));
            Check("D4 " + language + " keeps programming device and job selection",
                programmingFloat.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedItem is Knx.App.Services.DeviceRow selectedDevice
                && selectedDevice.Device.Id == chosenDevice
                && ReferenceEquals(programmingFloat.GetLogicalDescendants().OfType<ListBox>().Single().SelectedItem, jobs[2]));
        }
        Check("D6 UI localization preserves technical model values", jobs.Select(j => j.Operation).SequenceEqual(operations));
        programmingFloat.Close();
        main.ShowTool("search");
        Call(main, "OnFloatTool", null, new RoutedEventArgs());
        var searchFloat = Field<List<Window>>(main, "_toolWindows").Single();
        var searchPane = (SearchPane)searchFloat.Content!;
        searchPane.FindControl<TextBox>("QueryBox")!.Text = "^Tast";
        searchPane.FindControl<TextBox>("ReplaceBox")!.Text = "Probe";
        searchPane.FindControl<CheckBox>("RegexCheck")!.IsChecked = true;
        searchPane.FindControl<CheckBox>("CaseCheck")!.IsChecked = true;
        searchPane.FindControl<ToggleButton>("ReplaceToggle")!.IsChecked = true;
        searchPane.FindControl<ComboBox>("ScopeBox")!.SelectedIndex = 1;
        Call(searchPane, "RunPreview");
        var preview = Field<object>(searchPane, "_preview");
        Check("D4 search fixture has an actionable replacement preview", preview is ReplacePreview { FieldCount: > 0 });
        Strings.Language = "it";
        await Task.Delay(100);
        var relabeledSearch = (SearchPane)searchFloat.Content!;
        Check("D4 language preserves search inputs options and exact replacement preview",
            relabeledSearch.FindControl<TextBox>("QueryBox")!.Text == "^Tast"
            && relabeledSearch.FindControl<TextBox>("ReplaceBox")!.Text == "Probe"
            && relabeledSearch.FindControl<CheckBox>("RegexCheck")!.IsChecked == true
            && relabeledSearch.FindControl<CheckBox>("CaseCheck")!.IsChecked == true
            && relabeledSearch.FindControl<ToggleButton>("ReplaceToggle")!.IsChecked == true
            && relabeledSearch.FindControl<ComboBox>("ScopeBox")!.SelectedIndex == 1
            && ReferenceEquals(Field<object>(relabeledSearch, "_preview"), preview));
        searchFloat.Close();
        Strings.Language = "en";
        main.ViewModel.CreateProject(WorkbenchJson.CreateDemo(), null);
        main.RefreshWorkspace();
        if (failures.Count > 0) throw new Exception("REVIEW2 failures: " + string.Join(", ", failures));
    }
}
