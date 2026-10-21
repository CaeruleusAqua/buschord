using Knx.App.Workbench;

var checks = 0;
void Check(string name, bool value)
{
    if (!value) throw new Exception("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
var root = Path.Combine(Path.GetTempPath(), "buschord-workspace-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var workspace = new WorkbenchWorkspace("P1");
    var room = workspace.Open(new("room", "R1"), "Wohnzimmer");
    room.Filter = "Licht"; room.IncludeRelated = false; room.SelectedObjectIds.Add("KO1");
    room.CollapsedDeviceIds.Add("D1"); room.View = "relations";
    var device = workspace.Open(new("device", "D1"), "Taster", "params");
    Check("Opening another target preserves room filter/selection/view", room.Filter == "Licht"
        && room.SelectedObjectIds.SequenceEqual(["KO1"]) && room.View == "relations");
    Check("Reopen keeps tab identity and local state", ReferenceEquals(room, workspace.Open(new("room", "R1"), "Wohnen"))
        && workspace.Tabs.Count == 2 && room.Title == "Wohnen" && room.Filter == "Licht");
    workspace.Navigators["building"].Query = "EG";
    workspace.Navigators["topology"].Query = "1.1";
    workspace.Navigators["building"].ExpandedIds.Add("F1");
    Check("Navigator perspectives have independent search state", workspace.Navigators["building"].Query == "EG"
        && workspace.Navigators["topology"].Query == "1.1");
    workspace.InspectorVisible = true; workspace.ToolsVisible = true; workspace.NavigatorWidth = 301;
    var path = Path.Combine(root, "layout.json"); workspace.SaveLayout(path);
    var restored = new WorkbenchWorkspace("P1");
    Check("Layout roundtrip succeeds", restored.TryRestoreLayout(path));
    var restoredRoom = restored.Tabs.Single(t => t.Target.Kind == "room");
    Check("Work item identity and representation roundtrip", restoredRoom.Target == room.Target
        && restoredRoom.View == "relations" && restoredRoom.Filter == "Licht" && !restoredRoom.IncludeRelated
        && restoredRoom.CollapsedDeviceIds.Contains("D1") && restored.ActiveTab == restoredRoom);
    Check("Transient KO selection is not restored", restoredRoom.SelectedObjectIds.Count == 0);
    Check("Navigator and layout roundtrip", restored.NavigatorWidth == 301 && restored.ToolsVisible
        && restored.InspectorVisible && restored.Navigators["building"].ExpandedIds.Contains("F1"));
    var other = new WorkbenchWorkspace("P2");
    Check("Other project receives geometry, not stale work targets", other.TryRestoreLayout(path)
        && other.NavigatorWidth == 301 && other.Tabs.Count == 0);
    restored.TryRestoreLayout(path, target => target.Kind != "device");
    Check("Missing project targets are not restored", restored.Tabs.Count == 1);
    restored.ResetLayout();
    Check("Reset layout preserves work item state", restored.Tabs.Count == 1 && restoredRoom.Filter == "Licht"
        && restored.NavigatorWidth == 272 && !restored.ToolsVisible);
    restored.FocusLayout();
    Check("Focus mode hides chrome, not work items", !restored.NavigatorVisible && restored.Tabs.Count == 1);
    restored.Close(restored.Tabs[0]);
    Check("Closing final tab leaves consistent empty workspace", restored.Tabs.Count == 0 && restored.ActiveTab is null);
    workspace.ChangeProject("P2");
    Check("Project change clears per-project navigation and tabs", workspace.Tabs.Count == 0
        && workspace.ActiveTab is null && workspace.Navigators["building"].Query == "");
    File.WriteAllText(path, "{ invalid json");
    Check("Malformed layout cannot break startup", !workspace.TryRestoreLayout(path));
    File.WriteAllText(path, "{\"Version\":99}");
    Check("Unknown layout version is not applied", !workspace.TryRestoreLayout(path));
    workspace.NavigatorWidth = double.NaN; workspace.SaveLayout(path);
    Check("Invalid splitter geometry is normalized", restored.TryRestoreLayout(path) && restored.NavigatorWidth == 272);
    Check("No partial layout files remain", !Directory.EnumerateFiles(root, "*.tmp").Any());
    var audit = new WorkbenchAudit();
    for (var i = 0; i < 620; i++) audit.Add("change " + i, DateTimeOffset.UnixEpoch.AddSeconds(i));
    Check("Change history is bounded and newest-first", audit.Count == WorkbenchAudit.Capacity
        && audit.Entries[0].Description == "change 619" && audit.Entries[^1].Description == "change 120");
    audit.Clear();
    Check("Change history clears without touching project/layout", audit.Count == 0 && workspace.Tabs.Count == 0);
    // Parameter sessions expose project identity and an explicit read-only reason without UI.
    var parameterVm = new Knx.App.Services.MainWindowViewModel();
    var parameterProject = new Knx.Project { Id = "P-PARAM", Name = "Parameter" };
    var parameterInstallation = new Knx.Installation { Id = "I-PARAM" };
    var parameterArea = new Knx.Area { Id = "A-PARAM", Address = 1 };
    var parameterLine = new Knx.Line { Id = "L-PARAM", Address = 1 };
    parameterLine.Devices.Add(new Knx.DeviceInstance { Id = "D-PARAM", Name = "Device", ProductRefId = "" });
    parameterArea.Lines.Add(parameterLine);
    parameterInstallation.Areas.Add(parameterArea);
    parameterProject.Installations.Add(parameterInstallation);
    parameterVm.CreateProject(parameterProject, null);
    var parameterSession = new Knx.App.Services.ParameterEditingSession(parameterVm);
    parameterSession.Load(parameterVm.Devices[0]);
    parameterSession.SetReadOnly("DCA/plug-in does not run here.");
    Check("Parameter session exposes project identity and read-only reason",
        parameterSession.ProjectId == "P-PARAM" && parameterSession.IsReadOnly && !parameterSession.CanApply);
    parameterSession.ClearReadOnly();
    var nextParameterProject = new Knx.Project { Id = "P-PARAM-2", Name = "Parameter 2" };
    parameterVm.CreateProject(nextParameterProject, null);
    Check("Parameter session reports a switched project as stale", parameterSession.IsStale
        && parameterSession.ProjectId == "P-PARAM-2");
    Console.WriteLine($"WORKBENCH UI STATE OK ({checks})");
}
finally { Directory.Delete(root, recursive: true); }
