using Knx;

// Offline regression check: dotnet run --project tests/Knx.Linking.Tests
void Check(string name, bool condition)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

var project = ProjectWizard.Create(new NewProjectOptions { Name = "Manual links" });
var installation = project.Installations[0];
var area = installation.Areas[0];
var line = area.Lines[0];
var range = new GroupRange { Id = "R", RangeStart = 0, RangeEnd = 65535 };
installation.MainRanges.Add(range);
range.Addresses.Add(new GroupAddressEntry { Id = "GA-1", Name = "Unrelated name", Address = GroupAddress.ThreeLevel(1, 1, 1) });
range.Addresses.Add(new GroupAddressEntry { Id = "GA-2", Name = "Second", Address = GroupAddress.ThreeLevel(1, 1, 2), DatapointType = "DPST-9-1" });
var device = new DeviceInstance { Id = "D", ProductRefId = "P", Address = new IndividualAddress(0, 0, 1) };
var obj = new ComObjectInstanceRef { Id = "REF", ComObjectId = "CO", Text = "Manual", Number = 7, FunctionText = "Switch", Device = "test" };
device.ComObjectRefs.Add(obj);
line.Devices.Add(device);
var refs = device.ComObjectRefs;
var links = obj.Links;

Check("link adds without DPT metadata or name match", GroupLinks.Link(project, device, "REF", "GA-1"));
Check("link is idempotent", !GroupLinks.Link(project, device, "REF", "GA-1") && obj.Links.Count == 1);
Check("multiple group addresses allowed", GroupLinks.Link(project, device, "REF", "GA-2") && obj.Links.SequenceEqual(["GA-1", "GA-2"]));
Check("list instances are retained", ReferenceEquals(refs, device.ComObjectRefs) && ReferenceEquals(links, obj.Links));
Check("unlink removes only the chosen address", GroupLinks.Unlink(project, device, "REF", "GA-1") && obj.Links.SequenceEqual(["GA-2"]));
Check("unlink is idempotent", !GroupLinks.Unlink(project, device, "REF", "GA-1"));

void Reject(string name, Action action)
{
    var before = Knxproj.ToXml(project).ToString();
    var previousLinks = obj.Links.ToArray();
    try { action(); throw new Exception("expected argument error: " + name); }
    catch (ArgumentException) { }
    Check(name + " leaves project and object unchanged", before == Knxproj.ToXml(project).ToString() && obj.Links.SequenceEqual(previousLinks));
}

var foreign = new DeviceInstance { Id = device.Id, ProductRefId = "P", Address = device.Address };
foreign.ComObjectRefs.Add(new ComObjectInstanceRef { Id = "REF", ComObjectId = "CO" });
foreach (var add in new[] { true, false })
{
    void Change(Project p, DeviceInstance d, string o, string g)
    {
        if (add) GroupLinks.Link(p, d, o, g); else GroupLinks.Unlink(p, d, o, g);
    }
    Reject("null project " + add, () => Change(null!, device, "REF", "GA-1"));
    Reject("null device " + add, () => Change(project, null!, "REF", "GA-1"));
    Reject("foreign device with same id " + add, () => Change(project, foreign, "REF", "GA-1"));
    Reject("missing object " + add, () => Change(project, device, "CO", "GA-1"));
    Reject("missing GA " + add, () => Change(project, device, "REF", "missing"));
    Reject("blank object " + add, () => Change(project, device, " ", "GA-1"));
    Reject("blank GA " + add, () => Change(project, device, "REF", " "));
}
Check("foreign device remains unchanged", foreign.ComObjectRefs[0].Links.Count == 0);
device.ComObjectRefs.Add(new ComObjectInstanceRef { Id = "REF", ComObjectId = "CO" });
Reject("ambiguous object", () => GroupLinks.Link(project, device, "REF", "GA-1"));
device.ComObjectRefs.RemoveAt(1);
range.Addresses.Add(new GroupAddressEntry { Id = "GA-1", Name = "Duplicate id" });
Reject("ambiguous GA id", () => GroupLinks.Link(project, device, "REF", "GA-1"));
range.Addresses.RemoveAt(2);

// Membership includes devices directly on areas and inside segments.
line.Devices.Remove(device);
area.Devices.Add(device);
Check("area device membership", GroupLinks.Link(project, device, "REF", "GA-1"));
area.Devices.Remove(device);
var segment = new Segment { Id = "S" };
line.Segments.Add(segment);
segment.Devices.Add(device);
Check("segment device membership", GroupLinks.Unlink(project, device, "REF", "GA-1"));

var undo = new UndoStack();
undo.Reset(project);
GroupLinks.Link(project, device, "REF", "GA-1");
undo.Push(project);
var restored = undo.Undo()!;
Check("undo preserves other links", ProjectQuery.DeviceList(restored).Single().ComObjectRefs.Single().Links.SequenceEqual(["GA-2"]));
var redone = undo.Redo()!;
Check("redo restores link order", ProjectQuery.DeviceList(redone).Single().ComObjectRefs.Single().Links.SequenceEqual(["GA-2", "GA-1"]));
try { GroupLinks.Link(restored, device, "REF", "GA-1"); throw new Exception("stale device accepted"); }
catch (ArgumentException) { Check("undo rejects stale device", obj.Links.SequenceEqual(["GA-2", "GA-1"]) && ProjectQuery.DeviceList(restored).Single().ComObjectRefs[0].Links.SequenceEqual(["GA-2"])); }

var path = Path.Combine(Path.GetTempPath(), "knx-links-" + Guid.NewGuid().ToString("N") + ".knxproj");
try
{
    Knxproj.Save(redone, path);
    var loaded = Knxproj.Load(path);
    var loadedDevice = ProjectQuery.DeviceList(loaded).Single();
    var loadedObj = loadedDevice.ComObjectRefs.Single();
    Check("save/load preserves links and supported metadata", loadedObj.Links.SequenceEqual(["GA-2", "GA-1"]) && loadedObj.Id == "REF" && loadedObj.Text == "Manual" && loadedObj.FunctionText == "Switch" && loadedObj.Device == "test");
    Check("loaded device can unlink", GroupLinks.Unlink(loaded, loadedDevice, "REF", "GA-1"));
}
finally { File.Delete(path); }
obj.Links.Add("GA-2");
Check("unlink removes legacy duplicates", GroupLinks.Unlink(project, device, "REF", "GA-2") && obj.Links.SequenceEqual(["GA-1"]));
Console.WriteLine("ALL LINKING TESTS PASSED");
