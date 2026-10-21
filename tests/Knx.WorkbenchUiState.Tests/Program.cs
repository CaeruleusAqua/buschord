using Knx;
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

    // Favorites/pinning: project-scoped, persisted in the layout, never resurrected for other projects.
    var pins = new WorkbenchWorkspace("P1");
    var pinnedDevice = new WorkbenchTarget("device", "D1");
    var pinnedGroup = new WorkbenchTarget("ga", "GA1");
    Check("Favorite toggles on and off", pins.ToggleFavorite(pinnedDevice) && pins.IsFavorite(pinnedDevice)
        && !pins.ToggleFavorite(pinnedDevice) && !pins.IsFavorite(pinnedDevice));
    pins.ToggleFavorite(pinnedDevice);
    pins.ToggleFavorite(pinnedGroup);
    pins.ToggleFavorite(new("device", ""));
    var pinPath = Path.Combine(root, "pins.json");
    pins.SaveLayout(pinPath);
    var restoredPins = new WorkbenchWorkspace("P1");
    Check("Invalid favorites are never stored", pins.Favorites.SequenceEqual([pinnedDevice, pinnedGroup]));
    Check("Favorites persist with the workspace layout", restoredPins.TryRestoreLayout(pinPath)
        && restoredPins.Favorites.SequenceEqual([pinnedDevice, pinnedGroup]));
    restoredPins.TryRestoreLayout(pinPath, target => target.Kind != "ga");
    Check("Favorites for missing targets are dropped on restore", restoredPins.Favorites.SequenceEqual([pinnedDevice]));
    var otherPins = new WorkbenchWorkspace("P2");
    otherPins.ToggleFavorite(pinnedDevice);
    otherPins.TryRestoreLayout(pinPath);
    Check("Favorites do not leak into another project", otherPins.Favorites.SequenceEqual([pinnedDevice]));
    restoredPins.ChangeProject("P2");
    Check("Project change clears favorites with the tabs", restoredPins.Favorites.Count == 0);

    // Drag-and-drop target validation stays a pure projection: invalid drops never mutate.
    var dropProject = new Project { Id = "P-DROP", Name = "Drop" };
    var dropInstallation = new Installation { Id = "I-1", Name = "Gebäude" };
    var dropArea = new Area { Id = "A-1", Address = 1 };
    var dropLine = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
    var otherLine = new Line { Id = "L-2", Address = 2, Name = "Linie 2" };
    var dropSegment = new Segment { Id = "S-1", Address = 1 };
    dropLine.Segments.Add(dropSegment);
    dropArea.Lines.Add(dropLine); dropArea.Lines.Add(otherLine);
    dropInstallation.Areas.Add(dropArea); dropProject.Installations.Add(dropInstallation);
    var dropRoom = dropInstallation.Locations.Add(null, "Wohnzimmer", SpaceKind.Room, "R-1");
    var dropDevice = new DeviceInstance { Id = "D-1", Name = "Taster", Address = new(1, 1, 10), ProductRefId = "M-1" };
    dropSegment.Devices.Add(dropDevice); dropRoom.AssignDevice(dropDevice);
    var dropAddressBefore = dropDevice.Address.ToString();
    var toOtherLine = WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-2"));
    Check("Drop on another line resolves a segment-free target", toOtherLine.Ok
        && toOtherLine.Line?.Id == "L-2" && toOtherLine.Segment is null);
    var outOfSegment = WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-1"));
    Check("Drop on the own line leaves the segment", outOfSegment.Ok && outOfSegment.Segment is null);
    Check("Drop on the current segment is a no-op", WorkbenchProjection
        .ResolveDeviceDrop(dropProject, dropDevice.Id, new("segment", "S-1")).Status == DeviceDropStatus.AlreadyThere);
    Check("Drop rejects non-topology, unknown device and unknown line targets",
        WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("room", "R-1")).Status == DeviceDropStatus.NotATopologyTarget
        && WorkbenchProjection.ResolveDeviceDrop(dropProject, "D-missing", new("line", "L-2")).Status == DeviceDropStatus.DeviceMissing
        && WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-missing")).Status == DeviceDropStatus.LineMissing);
    Check("Rejected drops leave topology, address and room untouched",
        dropSegment.Devices.Contains(dropDevice) && !otherLine.Devices.Contains(dropDevice)
        && dropDevice.Address.ToString() == dropAddressBefore && dropRoom.DeviceRefs.Contains(dropDevice.Id));
    var move = WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-2"));
    var moved = move.Ok ? ProjectOperations.MoveDevice(dropProject, dropDevice, move.Line!, move.Segment) : null;
    Check("Validated drop moves the device and keeps its room", moved is { Ok: true }
        && otherLine.Devices.Contains(dropDevice) && !dropSegment.Devices.Contains(dropDevice)
        && dropRoom.DeviceRefs.Contains(dropDevice.Id) && dropDevice.Address.Line == 2);
    Check("Dropping the moved device back onto its line is a no-op",
        WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-2")).Status == DeviceDropStatus.AlreadyThere);

    Console.WriteLine($"WORKBENCH UI STATE OK ({checks})");
}
finally { Directory.Delete(root, recursive: true); }
