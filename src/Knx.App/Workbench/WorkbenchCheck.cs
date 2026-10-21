using System.IO.Compression;
using System.Collections;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;

namespace Knx.App.Workbench;

/// <summary>Native routed-event checks and synthetic screenshots; never uses OS mouse input or KNX transport.</summary>
internal static class WorkbenchCheck
{
    public static bool Enabled { get; set; }
    public static string? TestDirectory { get; set; }

    /// <summary>Embedded fictional V4 demo, used only for reference screenshots and manual demos.</summary>
    internal static void LoadDemo(WorkbenchWindow window)
    {
        var demo = Knx.Workbench.WorkbenchJson.CreateDemo();
        window.ViewModel.CreateProject(demo, null);
        window.ViewModel.Status = WorkbenchStrings.Get("ProjectCreated", demo.Name);
        window.RefreshWorkspace();
        window.Workspace.Navigators["building"].ExpandedIds.UnionWith(["eg", "living"]);
        window.OpenTarget(new("room", "living"));
        window.RefreshWorkspace();
    }

    internal static void LoadFixture(WorkbenchWindow window)    {
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
        window.ViewModel.Status = WorkbenchStrings.Get("ProjectCreated", window.ViewModel.Project?.Name ?? p.Name);
        window.ViewModel.ProjectPath = path;
        window.RefreshWorkspace();
        window.Workspace.Navigators["building"].ExpandedIds.UnionWith(["space:F-1", "space:R-1"]);
        window.OpenTarget(new("room", "R-1"));
        window.RefreshWorkspace();
    }

    public static void Capture(IClassicDesktopStyleApplicationLifetime desktop, WorkbenchWindow main, string path, string screen)
    {
        if (screen.StartsWith("demo", StringComparison.OrdinalIgnoreCase)) LoadDemo(main);
        else LoadFixture(main);
        var small = screen.EndsWith("-small", StringComparison.OrdinalIgnoreCase);
        var tiny = screen.EndsWith("-tiny", StringComparison.OrdinalIgnoreCase);
        var view = tiny ? screen[..^5] : small ? screen[..^6] : screen;
        Window window = main;
        switch (view.ToLowerInvariant())
        {
            case "parameters": main.SetRepresentation("params"); break;
            case "demoparams": main.SetRepresentation("params"); break;
            case "links": main.SetRepresentation("relations"); break;
            case "demolinks": main.SetRepresentation("relations"); break;
            case "monitor": main.ShowTool("monitor"); break;
            case "demomonitor": main.ShowTool("monitor"); break;
            case "demoprogramming": main.ShowTool("programming"); break;
            case "inspector": main.Workspace.InspectorVisible = true; break;
            case "demoinspector": main.Workspace.InspectorVisible = true; break;
            case "catalog": main.ShowCatalog(); break;
            case "democatalog": main.ShowCatalog(); break;
            case "settings": window = new SettingsWindow(main.ViewModel.Settings); break;
            case "new-project": window = new NewProjectWindow(); break;
            case "splash": window = new SplashWindow(); break;
        }
        var capture = window;
        if (screen is not ("settings" or "new-project"))
        {
            capture.Width = tiny ? 900 : small ? 1024 : screen == "splash" ? 380 : 1440;
            capture.Height = tiny ? 600 : small ? 700 : screen == "splash" ? 420 : 900;
        }
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
                WriteDcaCatalog();
                Check("empty workbench offers project start actions", window.FindControl<ContentControl>("ToolPageHost")?.Content is StackPanel start
                    && start.Children.OfType<Button>().Any(b => Equals(b.Content, WorkbenchStrings.Get("OpenProjectEllipsis"))));
                await Task.Delay(100);
                var themedTip = window.GetVisualDescendants().OfType<Control>()
                    .Select(ToolTip.GetTip).OfType<TextBlock>()
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text.Text));
                Check("tooltip content is wrapped in a themed TextBlock",
                    themedTip is not null && !string.IsNullOrWhiteSpace(themedTip.Text));
                IEnumerable<MenuItem> MenuItems(IEnumerable? items) => items?.OfType<MenuItem>()
                    .SelectMany(item => new[] { item }.Concat(MenuItems(item.ItemsSource))) ?? [];
                var helpItem = MenuItems(window.FindControl<Menu>("MainMenu")!.ItemsSource)
                    .First(item => Equals(item.Header, WorkbenchStrings.Get("HelpTip")));
                helpItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                HelpWindow? help = null;
                for (var attempt = 0; attempt < 100 && help is null; attempt++)
                {
                    help = desktop.Windows.OfType<HelpWindow>().FirstOrDefault();
                    if (help is null) await Task.Delay(20);
                }
                Check("help opens a localized V4 window", help is { } helpWindow
                    && helpWindow.Title == WorkbenchStrings.Get("HelpTitle")
                    && helpWindow.GetVisualDescendants().OfType<TextBlock>()
                        .Any(text => text.Text == WorkbenchStrings.Get("HelpSectionBoundaries")));
                if (help is { } openHelp)
                {
                    openHelp.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                    await Task.Delay(60);
                    Check("help closes with Escape", !openHelp.IsVisible);
                }
                var documentation = window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => AutomationProperties.GetName(button) == WorkbenchStrings.Get("Documentation"));
                Check("documentation action is clearly named in the shell rail", documentation is not null);
                documentation?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HelpWindow? documented = null;
                for (var attempt = 0; attempt < 100 && documented is null; attempt++)
                {
                    documented = desktop.Windows.OfType<HelpWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                    if (documented is null) await Task.Delay(20);
                }
                Check("documentation action opens the localized usage concept", documented is { } documentedWindow
                    && documentedWindow.Title == WorkbenchStrings.Get("HelpTitle")
                    && documentedWindow.GetVisualDescendants().OfType<TextBlock>()
                        .Any(text => text.Text == WorkbenchStrings.Get("HelpSectionShortcuts")));
                if (documented is { } closeDocumented)
                {
                    closeDocumented.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                    await Task.Delay(60);
                    Check("documentation closes with Escape", !closeDocumented.IsVisible);
                }
                // Cold catalogue: the DCA reason arrives only via CatalogLoaded, then the pane is read-only.
                var dcaProject = new Project { Id = "P-DCA-UI", Name = "DCA shell" };
                var dcaInstallation = new Installation { Id = "I-DCA-UI" };
                var dcaArea = new Area { Id = "A-DCA-UI", Address = 1 };
                var dcaLine = new Line { Id = "L-DCA-UI", Address = 1 };
                var dcaDevice = new DeviceInstance { Id = "D-DCA-UI", Name = "DCA panel", Address = new(1, 1, 7),
                    ProductRefId = "M-DCAUI_H-1_P-1" };
                dcaLine.Devices.Add(dcaDevice);
                dcaArea.Lines.Add(dcaLine); dcaInstallation.Areas.Add(dcaArea); dcaProject.Installations.Add(dcaInstallation);
                window.ViewModel.CreateProject(dcaProject, null);
                Check("DCA shell reason stays null while the catalogue is cold",
                    window.ViewModel.ParameterReadOnlyReason(dcaDevice) is null);
                window.OpenTarget(new("device", dcaDevice.Id), "params");
                window.RefreshWorkspace();
                var dcaPane = window.ParameterEditor;
                var dcaReady = await WaitUntil(() => dcaPane.IsReadOnly, "DCA catalogue read-only");
                Check("DCA read-only reason arrives with CatalogLoaded and values stay untouched",
                    dcaReady && dcaPane.Session.Rows.Length > 0 && !dcaPane.Session.CanApply
                    && dcaPane.ReadOnlyReason is { Length: > 0 } && dcaDevice.ParameterValues.Count == 0);
                LoadFixture(window);
                var p = window.ViewModel.Project!;
                var d = ProjectQuery.DeviceList(p).First();
                var docStrip = window.FindControl<StackPanel>("ObjectTabs")!;
                var viewStrip = window.FindControl<WrapPanel>("Representations")!;
                var toolStrip = window.FindControl<StackPanel>("ToolTabs")!;
                Check("doc/view/tool tab strips expose named TabItem semantics",
                    docStrip.Children.OfType<TabItem>().Any(t => AutomationProperties.GetName(t)?.Length > 0)
                    && viewStrip.Children.OfType<TabItem>().Count() == 4
                    && toolStrip.Children.OfType<TabItem>().Count() >= 6
                    && docStrip.Children.OfType<TabItem>().Concat(viewStrip.Children.OfType<TabItem>())
                        .Concat(toolStrip.Children.OfType<TabItem>())
                        .All(t => AutomationProperties.GetName(t)?.Length > 0));
                Check("active view tab exposes selected item status", viewStrip.Children.OfType<TabItem>()
                    .Any(t => t.IsSelected && AutomationProperties.GetItemStatus(t) == WorkbenchStrings.Get("Selected")));
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
                var searchGa = ProjectQuery.GroupAddresses(p).First();
                window.OpenSearchHit(new(ProjectSearch.DeviceKind, d.Id, d.Name ?? "", "name", d.Name ?? "", ""));
                Check("search hit opens the device", window.Workspace.ActiveTab?.Target == new WorkbenchTarget("device", d.Id));
                window.OpenSearchHit(new(ProjectSearch.GroupAddressKind, searchGa.Id, searchGa.Address.ToString(), "name", searchGa.Name, ""));
                Check("search hit opens the group address", window.Workspace.ActiveTab?.Target == new WorkbenchTarget("ga", searchGa.Id));
                window.OpenSearchHit(new(ProjectSearch.CommunicationObjectKind, d.ComObjectRefs[0].Id, "", "text", "", ""));
                Check("search hit reveals the communication object in its device table",
                    window.Workspace.ActiveTab?.Target == new WorkbenchTarget("device", d.Id)
                    && window.Workspace.ActiveTab?.View == "objects"
                    && window.Workspace.ActiveTab!.SelectedObjectIds.Contains(WorkbenchProjection.ObjectKey(d, d.ComObjectRefs[0])));
                window.OpenTarget(room.Target);
                var table = window.FindControl<ListBox>("ObjectTable")!;
                var row = ((IEnumerable<WorkbenchRow>)table.ItemsSource!).First(r => r.Object is not null);
                table.SelectedItems!.Add(row);
                var selected = room.SelectedObjectIds.ToArray();
                Check("routed native selection stored on tab", selected.Contains(row.Key));
                window.FindControl<TextBox>("ObjectSearch")!.Text = "LED";
                await Task.Delay(50); // Avalonia delivers TextChanged at the next dispatcher turn.
                var deviceTab = window.OpenTarget(new("device", d.Id));
                var freshTab = window.OpenTarget(new("device", ProjectQuery.DeviceList(p).First(x => x.Id != d.Id).Id));
                Check("new object tab has independent filter and selection", freshTab.Filter == "" && freshTab.SelectedObjectIds.Count == 0);
                window.OpenTarget(room.Target);
                Check($"room filter and selection survive tab switch (filter={room.Filter}, selection={string.Join(',', room.SelectedObjectIds)})", room.Filter == "LED" && room.SelectedObjectIds.SequenceEqual(selected));
                window.FindControl<TextBox>("ObjectSearch")!.Text = "";
                await Task.Delay(60); // TextChanged is delivered at the next dispatcher turn.
                var rows = WorkbenchProjection.Rows(p, room);
                Check("device, channel, object hierarchy shares a table", rows.Any(r => r.Kind == "device") && rows.Any(r => r.Kind == "channel") && rows.Any(r => r.Object is not null));
                var linkedRow = rows.FirstOrDefault(r => r.Object is { Links.Count: > 0 });
                if (linkedRow is not null)
                {
                    table.ScrollIntoView(linkedRow);
                    await Task.Delay(60);
                    var chipAddress = ProjectQuery.GroupAddresses(p)
                        .First(group => linkedRow.Object!.Links.Contains(group.Id)).Address.ToString();
                    Check("group addresses render as compact chips", table.GetVisualDescendants().OfType<Border>()
                        .Any(border => border.Child is TextBlock text && text.Text == chipAddress));
                }
                var rowMenu = table.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.ContextMenu is not null);
                Check("object rows expose open/relations/favorite/delete actions", rowMenu?.ContextMenu?.Items.Count == 4);
                var frozenRows = rows.Where(r => r.Object is not null).Take(2).ToArray();
                room.SelectedObjectIds.Clear();
                foreach (var frozenRow in frozenRows) room.SelectedObjectIds.Add(frozenRow.Key);
                var batch = WorkbenchWindow.BatchSelection(p, room);
                room.SelectedObjectIds.Clear();
                room.SelectedObjectIds.Add(frozenRows[0].Key);
                Check("batch link snapshot freezes members at dialog open", batch.Count == 2
                    && batch.Select(b => b.ObjectId).SequenceEqual(frozenRows.Select(r => r.Object!.Id)));
                room.SelectedObjectIds.Clear();
                var selectAll = window.FindControl<CheckBox>("SelectAllBox")!;
                selectAll.IsChecked = true;
                await Task.Delay(40);
                var tableKeys = table.SelectedItems!.OfType<WorkbenchRow>().Select(row => row.Key).ToHashSet();
                Check($"header select-all mirrors the table multi-selection ({room.SelectedObjectIds.Count}/{tableKeys.Count}/{rows.Count})",
                    room.SelectedObjectIds.Count == rows.Count
                    && room.SelectedObjectIds.Distinct().Count() == rows.Count
                    && tableKeys.SetEquals(rows.Select(row => row.Key)));
                selectAll.IsChecked = false;
                await Task.Delay(40);
                Check("header clear clears the table multi-selection",
                    room.SelectedObjectIds.Count == 0 && table.SelectedItems!.Count == 0);
                room.SelectedObjectIds.Clear();
                foreach (var key in selected) room.SelectedObjectIds.Add(key);
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
                deviceTab.SelectedObjectIds.Clear();
                window.RefreshWorkspace();
                window.Workspace.InspectorVisible = true;
                var inspectorBody = window.FindControl<StackPanel>("InspectorBody")!;
                var inspectorFooter = window.FindControl<StackPanel>("InspectorFooter")!;
                Check("inspector reveals native device", inspectorBody.Children.OfType<TextBlock>().Any(t => t.Text == d.Name));
                Check("inspector groups facts into named sections",
                    inspectorBody.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == WorkbenchStrings.Get("SectionTopology"))
                    && inspectorBody.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == WorkbenchStrings.Get("SectionDevice")));
                Check("inspector footer holds the apply action",
                    inspectorFooter.Children.OfType<Button>().Any(b => Equals(b.Content, WorkbenchStrings.Get("ApplyProperties"))));
                window.Activate();
                await Task.Delay(80);
                var nameBox = inspectorBody.GetVisualDescendants().OfType<TextBox>().First();
                nameBox.Text = "Tastsensor · bearbeitet";
                nameBox.CaretIndex = nameBox.Text.Length;
                nameBox.Focus();
                Check("inspector focuses the edited field", window.FocusManager?.GetFocusedElement() == nameBox);
                inspectorFooter.Children.OfType<Button>().First(b => Equals(b.Content, WorkbenchStrings.Get("ApplyProperties")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(80);
                var refocused = window.FocusManager?.GetFocusedElement() as TextBox;
                Check("inspector keeps the edited field focused after apply",
                    refocused is not null && AutomationProperties.GetAutomationId(refocused) == "inspector.device.name"
                    && refocused.CaretIndex == "Tastsensor · bearbeitet".Length);
                Check("native inspector rename refreshes work-item titles", d.Name == "Tastsensor · bearbeitet" && deviceTab.Title == d.Name);
                var checkLine = p.Installations[0].Areas[0].Lines[0];
                var checkSegment = new Segment { Id = "S-CHECK", Address = 1 };
                checkSegment.Devices.Add(d);
                checkLine.Devices.Remove(d);
                checkLine.Segments.Add(checkSegment);
                var editError = window.ViewModel.UpdateDeviceDetails(d, d.Name, d.Address.ToString(), checkLine.Id, "R-1");
                Check("device edit preserves the segment and assigns the room", editError is null
                    && checkSegment.Devices.Contains(d) && !checkLine.Devices.Contains(d)
                    && p.Installations[0].Locations.Find("R-1")!.DeviceRefs.Contains(d.Id));
                var dropAddress = d.Address.ToString();
                var toLine = WorkbenchProjection.ResolveDeviceDrop(p, d.Id, new("line", checkLine.Id));
                var toCurrentSegment = WorkbenchProjection.ResolveDeviceDrop(p, d.Id, new("segment", checkSegment.Id));
                Check("drop target validation resolves lines, segments and invalid targets", toLine.Ok && toLine.Segment is null
                    && toCurrentSegment.Status == DeviceDropStatus.AlreadyThere
                    && WorkbenchProjection.ResolveDeviceDrop(p, d.Id, new("room", "R-1")).Status == DeviceDropStatus.NotATopologyTarget
                    && WorkbenchProjection.ResolveDeviceDrop(p, "D-missing", new("line", checkLine.Id)).Status == DeviceDropStatus.DeviceMissing
                    && WorkbenchProjection.ResolveDeviceDrop(p, d.Id, new("line", "L-missing")).Status == DeviceDropStatus.LineMissing
                    && checkSegment.Devices.Contains(d) && !checkLine.Devices.Contains(d) && d.Address.ToString() == dropAddress);
                var dropMoved = toLine.Ok && window.ViewModel.ApplyProjectOperation("Selftest: Gerät verschieben",
                    op => ProjectOperations.MoveDevice(op, d, toLine.Line!, toLine.Segment));
                await Task.Delay(40);
                Check("validated drop moves the device out of the segment and keeps the room",
                    dropMoved && checkLine.Devices.Contains(d) && !checkSegment.Devices.Contains(d)
                    && p.Installations[0].Locations.Find("R-1")!.DeviceRefs.Contains(d.Id) && d.Address.ToString() == dropAddress);
                window.SetRepresentation("relations");
                Check("relations view uses domain-validated links", window.FindControl<ContentControl>("RelationsHost")!.Content is ScrollViewer
                    && window.ViewModel.Project is { } relationProject && Knx.Workbench.WorkbenchProject.Associations(relationProject).Count > 0);
                window.SetRepresentation("objects");
                var layout = Path.Combine(TestDirectory!, "layout.json");
                window.Workspace.NavigatorWidth = 300;
                await Task.Delay(40);
                window.SaveLayout(layout);
                var savedNavigatorWidth = window.Workspace.NavigatorWidth;
                var restored = new WorkbenchWorkspace(p.Id);
                Check("saved layout restores tabs and visibility without project mutation", restored.TryRestoreLayout(layout)
                    && restored.InspectorVisible && restored.NavigatorWidth == savedNavigatorWidth
                    && restored.Tabs.Count == window.Workspace.Tabs.Count);
                var navBefore = window.Workspace.NavigatorVisible;
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.B, KeyModifiers = KeyModifiers.Control });
                Check("Ctrl+B toggles the navigator", window.Workspace.NavigatorVisible != navBefore);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.B, KeyModifiers = KeyModifiers.Control });
                var toolsBefore = window.Workspace.ToolsVisible;
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.J, KeyModifiers = KeyModifiers.Control });
                Check("Ctrl+J toggles the lower tools", window.Workspace.ToolsVisible != toolsBefore);
                var firstTab = window.Workspace.ActiveTab;
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Control });
                Check("Ctrl+Tab changes the active work item", window.Workspace.ActiveTab != firstTab);
                var footnote = window.FindControl<TextBlock>("TableFootnote")!;
                Check("table footnote is a named polite live region",
                    AutomationProperties.GetLiveSetting(footnote) == Avalonia.Automation.AutomationLiveSetting.Polite
                    && AutomationProperties.GetName(footnote) == WorkbenchStrings.Get("TableFootnoteName"));
                Check("row names do not end in a dangling separator", !window.FindControl<ListBox>("ObjectTable")!
                    .GetVisualDescendants().OfType<Grid>().Select(AutomationProperties.GetName)
                    .Any(name => name is { Length: > 0 } text && (text.EndsWith(",", StringComparison.Ordinal) || text.EndsWith(", ", StringComparison.Ordinal))));
                window.Width = 1024;
                var mainColumn = window.FindControl<Grid>("MainColumn")!;
                // CI runners may clamp the native window to a 1024-wide desktop. Force the
                // layout width so the narrow/wide contract is deterministic on every host.
                window.ApplyLayoutForWidth(1024);
                var narrow = KeyboardNavigation.GetTabNavigation(mainColumn) == KeyboardNavigationMode.None;
                Check("narrow inspector overlay keeps covered editor stops out of the tab order",
                    narrow && window.Workspace.InspectorVisible);
                window.ApplyLayoutForWidth(1440);
                var wide = KeyboardNavigation.GetTabNavigation(mainColumn) == KeyboardNavigationMode.Continue;
                Check("wide layout restores editor tab navigation", wide);
                window.ApplyLayoutForWidth(window.Bounds.Width);
                window.ShowTool("programming");
                await Task.Delay(60);
                Check("programming tool renders its list inside one host", window.FindControl<ContentControl>("ToolHost")!.Content is DockPanel);
                window.ShowTool("search");
                await Task.Delay(60);
                Check("search tool is hosted maximized instead of the 242 DIP dock", window.Workspace.ToolsMaximized
                    && mainColumn.RowDefinitions[1].Height.Value == 0 && window.FindControl<ContentControl>("ToolHost")!.Content is SearchPane);
                window.ShowTool("monitor");
                await Task.Delay(60);
                Check("leaving search restores the docked tool height", !window.Workspace.ToolsMaximized
                    && mainColumn.RowDefinitions[1].Height.IsStar);
                window.ViewModel.StartRecording(); window.Workspace.ToolsVisible = false;
                window.ShowTool("monitor");
                Check("hiding and recreating monitor retains recorder state", window.ViewModel.IsRecording);
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    window.FindControl<Grid>("Tools")!.GetVisualDescendants().OfType<Button>()
                        .First(b => b.Content is ConceptIcon icon && icon.Symbol == "export")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(30);
                    var floating = desktop.Windows.Single(w => w != window);
                    Check($"fresh tool window {cycle + 1} does not reparent main content", !ReferenceEquals(floating.Content, window.FindControl<ContentControl>("ToolHost")!.Content));
                    floating.Close();
                    Check($"closing tool window {cycle + 1} retains recorder", window.ViewModel.IsRecording);
                    Check($"closing tool window {cycle + 1} docks back into the shell", window.Workspace.ToolsVisible && window.FindControl<ContentControl>("ToolHost")!.Content is not null);
                }
                window.ViewModel.StopRecording();
                var toolsBeforeCompanion = window.Workspace.ToolsVisible;
                var companionGa = ProjectQuery.GroupAddresses(window.ViewModel.Project!).First();
                window.OpenCompanion(new("ga", companionGa.Id));
                await Task.Delay(120);
                var companion = desktop.Windows.FirstOrDefault(w => w != window
                    && w.Title.Contains(WorkbenchStrings.Get("CompanionWindow"), StringComparison.Ordinal));
                Check("companion pane opens beside the main window for a group address",
                    companion?.Content is ScrollViewer { Content: StackPanel });
                companion?.Close();
                await Task.Delay(40);
                Check("closing the companion keeps the docking layout and work items",
                    window.Workspace.ToolsVisible == toolsBeforeCompanion && window.Workspace.ActiveTab is not null
                    && window.FindControl<ContentControl>("ToolHost")!.Content is not null);
                window.Workspace.Navigators["building"].Query = "";
                window.Navigate("building");
                var projectTarget = new WorkbenchTarget("project", window.ViewModel.Project!.Id);
                window.Workspace.ToggleFavorite(projectTarget);
                window.RefreshWorkspace();
                await Task.Delay(80);
                Check("pinned work item is marked in the navigator",
                    window.FindControl<ListBox>("NavTree")!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("★") == true));
                Check("pin toggles off again", !window.Workspace.ToggleFavorite(projectTarget) && !window.Workspace.IsFavorite(projectTarget));
                window.RefreshWorkspace();
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

                // Demo-data integration: domain metadata, atomic links, sessions and JSON bridge.
                LoadDemo(window);
                var demo = window.ViewModel.Project!;
                var living = window.Workspace.ActiveTab!;
                var demoRows = WorkbenchProjection.Rows(demo, living);
                Check("room scope includes relevant external channel", demoRows.Any(r => r.External && r.Object?.Id == "dimmer-a-value"));
                Check("room scope excludes unrelated external channel", !demoRows.Any(r => r.Object?.Id is { } id && id.StartsWith("dimmer-b-", StringComparison.Ordinal)));
                Check("object metadata fills DPT, bits and flags columns", demoRows.Any(r => r.Object is not null && r.Dpt == "1.001" && r.Bits == 1 && r.Flags == "CT"));
                // Demo form parity: shared label/field classes, per-field validation and blocked Apply.
                var demoParameterDevice = ProjectQuery.DeviceList(demo).First(x => demo.Workbench.Devices[x.Id].Demo
                    && x.ParameterValues.ContainsKey("debounce"));
                window.OpenTarget(new("device", demoParameterDevice.Id), "params");
                await Task.Delay(100);
                var parameterHost = window.FindControl<ContentControl>("ParametersHost")!;
                var demoLabels = parameterHost.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible && text.Classes.Contains("wb-label"))
                    .Select(text => text.Text).ToHashSet();
                Check("demo parameters use ParameterPane's field label geometry",
                    demoLabels.Contains(WorkbenchStrings.Get("Function"))
                    && demoLabels.Contains(WorkbenchStrings.Get("DebounceMs"))
                    && parameterHost.GetVisualDescendants().OfType<Control>()
                        .Any(control => control.IsEffectivelyVisible && control.Classes.Contains("wb-field")));
                var demoApply = parameterHost.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.IsEffectivelyVisible && Equals(button.Content, WorkbenchStrings.Get("Apply")));
                var demoField = parameterHost.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(text => text.IsEffectivelyVisible && text.Classes.Contains("wb-field"));
                demoField!.Text = "-1";
                await Task.Delay(60);
                var demoError = parameterHost.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(text => text.IsEffectivelyVisible && text.IsVisible && text.Classes.Contains("wb-error"));
                Check("demo form reports the field error and blocks Apply like ParameterPane",
                    demoError is not null && demoApply is { IsEnabled: false });
                demoField.Text = "30";
                await Task.Delay(60);
                Check("valid demo values re-enable the footer action", demoApply is { IsEnabled: true });
                var beforeLinks = Knx.Workbench.WorkbenchJson.Export(demo);
                var incompatible = Knx.Workbench.WorkbenchProject.AddLinks(demo,
                    [new("taster-living", "taster-living-switch"), new("climate-living", "climate-living-temp")], "central-off");
                Check("batch compatibility is checked atomically in the shell model", !incompatible.Ok && Knx.Workbench.WorkbenchJson.Export(demo) == beforeLinks);
                var addKey = new Knx.Workbench.ObjectKey("taster-living", "taster-living-floor");
                Check("explicit sending selection is available", Knx.Workbench.WorkbenchProject.SetSending(demo, addKey, "living-floor").Ok);
                window.Sessions!.Monitor.Configure(WorkbenchMonitorMode.Group, SessionOrigin.Demo, "demo-ip");
                window.Sessions.Monitor.Start();
                window.Sessions.Monitor.TickDemo(new(DateTimeOffset.Now, "1.1.250", "1/0/1", "GroupValueWrite", "1.001", "1", SessionOrigin.Demo, "living-switch"));
                window.Sessions.Monitor.FlushDisplay();
                Check("monitor session records and filters demo telegram", window.Sessions.Monitor.RecordedCount == 1 && window.Sessions.Monitor.FilteredRows.Count == 1);
                window.Sessions.Monitor.Stop();
                window.Sessions.Monitor.Configure(WorkbenchMonitorMode.Bus, SessionOrigin.Demo, "demo-ip");
                window.ShowTool("monitor");
                var monitorHost = (Control)window.FindControl<ContentControl>("ToolHost")!.Content!;
                var modeBox = monitorHost.GetVisualDescendants().OfType<ComboBox>()
                    .First(c => c.ItemsSource is IEnumerable<string> items && items.Contains(WorkbenchStrings.Get("BusMonitor")));
                modeBox.SelectedIndex = 1;
                monitorHost.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, WorkbenchStrings.Get("Start")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("start honours the selected monitor mode", window.Sessions.Monitor.IsRunning
                    && window.Sessions.Monitor.Mode == WorkbenchMonitorMode.Bus);
                window.Sessions.Monitor.Stop();
                var job = window.Sessions.Programming.Enqueue("taster-living");
                await window.Sessions.Programming.RunAsync();
                Check("demo programming queue records simulated success", job.Status == ProgrammingJobStatus.Done && !window.Sessions.Programming.NeedsProgramming("taster-living"));
                Check("demo JSON roundtrip remains available", Knx.Workbench.WorkbenchJson.Import(Knx.Workbench.WorkbenchJson.Export(demo)).Id == demo.Id);
                var newAddress = ProjectQuery.NextFreeGroupAddress(window.ViewModel.Project!, 15, 7);
                Check("new group address receives a linkable DPT bit size", window.ViewModel.CreateGroupAddress(newAddress, "Selftest", "DPST-9-1")
                    && ProjectQuery.FindGroupAddress(window.ViewModel.Project!, newAddress) is { } createdGa
                    && window.ViewModel.Project!.Workbench.Groups[createdGa.Id].Bits == 16);
                window.ViewModel.UndoCommand.Execute(null);
                window.ShowTool("monitor");
                Check("monitor tool is visible before project replacement", window.FindControl<ContentControl>("ToolHost")!.Content is not null);
                LoadFixture(window);
                await Task.Delay(60);
                Check("project replacement rebuilds the tool view on fresh sessions", window.Sessions is not null
                    && window.FindControl<ContentControl>("ToolHost")!.Content is not null);
                var refreshedTools = (Control)window.FindControl<ContentControl>("ToolHost")!.Content!;
                refreshedTools.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, WorkbenchStrings.Get("Start")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("rebuilt tool view controls the new session", window.Sessions!.Monitor.IsRunning);
                window.Sessions.Monitor.Stop();

                // V4 compare (24/E09): one pinned GA companion survives editor navigation, dedups and closes.
                var compareProject = window.ViewModel.Project!;
                var firstGa = ProjectQuery.GroupAddresses(compareProject).OrderBy(group => group.Address.Raw).First();
                var compareButton = window.FindControl<Button>("CompareButton")!;
                Window? PinnedCompanion() => desktop.Windows.FirstOrDefault(candidate => candidate.IsVisible
                    && candidate.Title.Contains(firstGa.Address.ToString(), StringComparison.Ordinal)
                    && candidate.Title.Contains(WorkbenchStrings.Get("CompanionWindow"), StringComparison.Ordinal));
                var windowsBeforeCompare = desktop.Windows.Count;
                compareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                var pinned = PinnedCompanion();
                Check("compare opens the first group address beside the shell", pinned is not null
                    && desktop.Windows.Count == windowsBeforeCompare + 1);
                window.OpenTarget(new("device", ProjectQuery.DeviceList(compareProject).First().Id));
                await Task.Delay(80);
                Check("pinned compare stays visible while the editor navigates", pinned?.IsVisible == true);
                compareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Check("compare reuses the open companion instead of stacking windows",
                    ReferenceEquals(PinnedCompanion(), pinned) && desktop.Windows.Count == windowsBeforeCompare + 1);
                pinned?.Close();
                await Task.Delay(80);
                Check("closing compare ends the pinned view", pinned?.IsVisible == false);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.G, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift });
                await Task.Delay(150);
                var shortcutPinned = PinnedCompanion();
                Check("Ctrl+Shift+G opens compare for the active work item", shortcutPinned is not null);
                shortcutPinned?.Close();
                await Task.Delay(60);
                var emptyProject = new Project { Id = "P-NO-GA", Name = "No group addresses" };
                var emptyInstallation = new Installation { Id = "I-NO-GA" };
                var emptyArea = new Area { Id = "A-NO-GA", Address = 1 };
                var emptyLine = new Line { Id = "L-NO-GA", Address = 1 };
                emptyArea.Lines.Add(emptyLine); emptyInstallation.Areas.Add(emptyArea); emptyProject.Installations.Add(emptyInstallation);
                window.ViewModel.CreateProject(emptyProject, null);
                window.RefreshWorkspace();
                await Task.Delay(80);
                var windowsBeforeEmpty = desktop.Windows.Count;
                compareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(80);
                Check("compare explains a project without group addresses instead of opening a window",
                    window.ViewModel.Status == WorkbenchStrings.Get("CompareNoGroupAddress")
                    && desktop.Windows.Count == windowsBeforeEmpty);

                Console.WriteLine(failures.Count == 0 ? "WORKBENCH NATIVE OK" : $"WORKBENCH FAILURES: {failures.Count}");
                window.RequestShutdown(); desktop.Shutdown(failures.Count == 0 ? 0 : 1);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); window.RequestShutdown(); desktop.Shutdown(1); }
        }, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>Writes the synthetic DCA catalogue before any parameter lookup warms the shared library.</summary>
    private static void WriteDcaCatalog()
    {
        var directory = Path.Combine(TestDirectory!, "catalog");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dca-selftest.knxprod"), """
            <?xml version="1.0" encoding="utf-8"?>
            <KNX xmlns="http://knx.org/xml/prod/20">
              <ManufacturerData>
                <Manufacturer RefId="M-DCAUI">
                  <Hardware>
                    <Hardware Id="M-DCAUI_H-1" Name="Panel" SerialNumber="1" VersionNumber="1"
                              HasIndividualAddress="true" HasApplicationProgram="true">
                      <Products><Product Id="M-DCAUI_H-1_P-1" Text="DCA panel" OrderNumber="DCA-UI"/></Products>
                      <Hardware2Programs>
                        <Hardware2Program Id="M-DCAUI_H-1_HP-1"><ApplicationProgramRef RefId="M-DCAUI_A-1"/></Hardware2Program>
                      </Hardware2Programs>
                    </Hardware>
                  </Hardware>
                  <ApplicationPrograms>
                    <ApplicationProgram Id="M-DCAUI_A-1" Name="DCA panel" ApplicationNumber="1" ApplicationVersion="1">
                      <Static>
                        <Extension EtsUiPlugin="{22222222-2222-2222-2222-222222222222}"/>
                        <ParameterTypes>
                          <ParameterType Id="PT-1" Name="Brightness" SizeInBit="8" DataType="Number"/>
                        </ParameterTypes>
                        <Parameters>
                          <Parameter Id="P-1" Name="Brightness" ParameterType="PT-1" Value="50"/>
                        </Parameters>
                        <ParameterRefs>
                          <ParameterRef Id="PR-1" RefId="P-1"/>
                        </ParameterRefs>
                      </Static>
                    </ApplicationProgram>
                  </ApplicationPrograms>
                </Manufacturer>
              </ManufacturerData>
            </KNX>
            """, new System.Text.UTF8Encoding(false));
    }

    private static IEnumerable<WorkbenchNavNode> Flatten(IEnumerable<WorkbenchNavNode> nodes)
        => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    /// <summary>Polls layout/automation state instead of assuming one fixed delay is enough on slow agents.</summary>
    private static async Task<bool> WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        if (!condition()) Console.WriteLine($"WAIT TIMEOUT: {what}");
        return condition();
    }
}
