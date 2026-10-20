using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("Accepted invalid input: " + message);
}
var project = WorkbenchJson.CreateDemo();
var originalJson = WorkbenchJson.Export(project);
Check(WorkbenchJson.Export(WorkbenchJson.Import(originalJson)) == originalJson, "JSON roundtrip");
var rows = WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Room, "living"));
Check(rows.Any(r => r.Key.ObjectRefId == "dimmer-a-value" && r.External), "external relevant channel");
Check(!rows.Any(r => r.Key.ObjectRefId.StartsWith("dimmer-b-")), "unrelated channel excluded");
var roomDevice = rows.First(r => r.Device.Id == "taster-living").Device;
Check(ReferenceEquals(roomDevice, WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Line, "1.1")).First(r => r.Device.Id == roomDevice.Id).Device), "shared identity");
foreach (var edit in new Action<JsonObject>[] {
    n => n["data"]!["rooms"]![0]!["floor"] = "missing",
    n => n["data"]!["objects"]![0]!["channel"] = "missing",
    n => n["data"]!["links"]![0]!["ko"] = "missing",
    n => n["data"]!["devices"]![1]!["id"] = n["data"]!["devices"]![0]!["id"]!.GetValue<string>(),
    n => n["data"]!["objects"]![0]!["unknown"] = true,
    n => n["data"]!["objects"]![0]!["flags"] = "CTT",
    n => n["data"]!["gas"]![0]!["bits"] = 8,
    n => n["data"]!["devices"]![0]!["address"] = "01.1.11",
    n => n["data"]!["links"]!.AsArray().Add(n["data"]!["links"]![0]!.DeepClone()) })
{
    var node = JsonNode.Parse(originalJson)!.AsObject(); edit(node);
    Reject(() => WorkbenchJson.Import(node.ToJsonString()), "schema/reference/ID validation");
}
Reject(() => WorkbenchJson.Import(originalJson.Replace("\"version\": 4", "\"version\": 4, \"version\": 4")), "duplicate JSON property");
var undo = new UndoStack(); undo.Reset(project);
var key = new ObjectKey("taster-living", "taster-living-switch");
var wrong = new ObjectKey("climate-living", "climate-living-temp");
var before = WorkbenchJson.Export(project);
Check(!WorkbenchProject.AddLinks(project, [key, wrong], "central-off", undo).Ok, "atomic incompatible batch rejected");
Check(WorkbenchJson.Export(project) == before && undo.Steps == 0, "no partial changes or undo");
Check(WorkbenchProject.AddLinks(project, [key, key], "central-off", undo).Changed == 1, "deduplicated batch");
Check(WorkbenchProject.AddLinks(project, [key], "central-off", undo).Changed == 0, "existing duplicate no-op");
Check(WorkbenchProject.SetSending(project, key, "central-off", undo).Ok, "explicit sending");
Check(!WorkbenchProject.SetSending(project, key, "living-dim").Ok, "sending must be linked");
var removed = WorkbenchProject.RemoveLink(project, key, "central-off", undo);
Check(removed.Warnings.Count == 1 && WorkbenchProject.Metadata(project, key)!.SendingGroupId is null, "warning without replacement");
Check(WorkbenchProject.Metadata(project, key)!.Flags == "CT", "sending independent of flags");
Check(WorkbenchProject.Metadata(undo.Undo()!, key)!.SendingGroupId == "central-off", "undo sending removal");
Check(WorkbenchProject.Metadata(undo.Redo()!, key)!.SendingGroupId is null, "redo sending removal");
var dimKey = new ObjectKey("taster-kitchen", "taster-kitchen-dim");
before = WorkbenchJson.Export(project);
var preview = WorkbenchProject.PreviewMode(project, "taster-kitchen", "switch");
Check(preview.Supported && preview.Effects.Any(e => e.Key == dimKey && !e.Active && e.RetainedGroupIds.Contains("kitchen-dim")), "mode preview effects");
Check(WorkbenchJson.Export(project) == before, "preview is pure");
WorkbenchProject.ApplyMode(project, "taster-kitchen", "switch", undo);
Check(!WorkbenchProject.Metadata(project, dimKey)!.Active && WorkbenchProject.Associations(project).Any(a => a.Key == dimKey), "inactive links retained");
Check(WorkbenchProject.Metadata(undo.Undo()!, dimKey)!.Active, "undo activation metadata");
var sig = WorkbenchProject.ConfigurationSignature(project, "taster-living");
roomDevice.Name = "Renamed";
var installation = project.Installations.Single();
installation.Locations.Assign(installation.Locations.Find("plant")!, roomDevice.Id);
project.Workbench.Groups["living-switch"].EffectRoomId = "plant";
installation.Locations.Find("living")!.Name = "New room name";
Check(WorkbenchProject.ConfigurationSignature(project, roomDevice.Id) == sig, "names/installation/effect rooms excluded");
roomDevice.ParameterValues["debounce"] = "31";
Check(WorkbenchProject.ConfigurationSignature(project, roomDevice.Id) != sig, "bus parameters included");
sig = WorkbenchProject.ConfigurationSignature(project, roomDevice.Id);
ProjectQuery.FindGroupAddressById(project, "living-switch")!.Address = GroupAddress.Parse("1/0/200");
Check(WorkbenchProject.ConfigurationSignature(project, roomDevice.Id) != sig, "actual linked GA address included");
var native = new Project { Id = "Native", Name = "Native" };
Check(!WorkbenchProject.PreviewMode(native, "unknown", "dim").Supported, "unknown native dynamics unsupported");
var xml = Knxproj.ToXml(project);
XNamespace vendor = "urn:test:vendor";
xml.Root!.Add(new XElement(vendor + "Opaque", new XAttribute("Data", "preserve me")));
project = Knxproj.FromXml(xml);
var path = Path.Combine(Path.GetTempPath(), "workbench-test-" + Guid.NewGuid() + ".knxproj");
try
{
    Knxproj.Save(project, path);
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
    { using var writer = new StreamWriter(zip.CreateEntry("vendor/opaque.bin").Open()); writer.Write("opaque archive payload"); }
    var loaded = Knxproj.Load(path);
    WorkbenchProject.SetSending(loaded, key, "living-switch");
    var stack = new UndoStack(); stack.Reset(loaded);
    WorkbenchProject.SetSending(loaded, key, "living-status"); // Invalid: no mutation.
    loaded.Workbench.Objects[key.ToString()].Flags = "CR";
    stack.Push(loaded);
    loaded = stack.Undo()!;
    Knxproj.Save(loaded, path);
    loaded = Knxproj.Load(path);
    Check(WorkbenchProject.Metadata(loaded, key)!.SendingGroupId == "living-switch", "native extension reload");
    Check(WorkbenchProject.Metadata(loaded, key)!.Flags == "CT", "native metadata undo");
    Check(Knxproj.ToXml(loaded).Root!.Element(vendor + "Opaque") is not null, "unknown XML retained");
    using var savedArchive = ZipFile.OpenRead(path);
    using var reader = new StreamReader(savedArchive.GetEntry("vendor/opaque.bin")!.Open());
    Check(reader.ReadToEnd() == "opaque archive payload", "unknown ZIP member retained through save and undo");
}
finally { File.Delete(path); }
Console.WriteLine("Workbench checks OK: identity, strict JSON, scope, atomic links, sending, mode preview, signatures, native extension/archive and undo.");
