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
var reviewFailures = new List<string>();
void Regression(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { reviewFailures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name); }
}
foreach (var address in new[] { "16/0/1", "31/7/255" })
    Regression("full GA range " + address, () =>
    {
        var node = JsonNode.Parse(originalJson)!; node["data"]!["gas"]![0]!["address"] = address;
        var imported = WorkbenchJson.Import(node.ToJsonString());
        Check(ProjectQuery.GroupAddresses(imported).First().Address.ToString() == address, "GA conversion narrowed the address");
        WorkbenchJson.Import(WorkbenchJson.Export(imported));
    });
foreach (var flags in new[] { false, true })
    Regression(flags ? "flags reject trailing newline" : "IDs reject trailing newline", () =>
    {
        var node = JsonNode.Parse(originalJson)!;
        if (flags) node["data"]!["objects"]![0]!["flags"] = "CT\n";
        else node["data"]!["project"]!["id"] = "sample\n";
        Reject(() => WorkbenchJson.Import(node.ToJsonString()), "newline outside alphabet");
    });
Regression("JSON export refuses native-only modeled fields", () =>
{
    var sample = WorkbenchJson.CreateDemo(); var device = ProjectQuery.DeviceList(sample).First();
    device.Hardware2ProgramRefId = "native-application-ref"; device.Description = "keep description";
    Reject(() => WorkbenchJson.Export(sample), "lossy native fields");
});
Regression("JSON export refuses opaque native XML", () =>
{
    var sample = Knxproj.ToXml(WorkbenchJson.CreateDemo());
    sample.Root!.Add(new XElement(XName.Get("Opaque", "urn:vendor:test"), "important payload"));
    Reject(() => WorkbenchJson.Export(Knxproj.FromXml(sample)), "lossy opaque XML");
});
foreach (var data in new[] { "{\"Groups\":{\"bad\":null}}", "{\"Objects\":null}" })
    Regression("native metadata rejects malformed collections: " + data, () =>
    {
        var sample = Knxproj.ToXml(WorkbenchJson.CreateDemo());
        sample.Descendants(XName.Get("Workbench", "urn:buschord:project:1")).Single().SetAttributeValue("Data", data);
        Reject(() => Knxproj.FromXml(sample), "malformed extension must be a controlled format error");
    });
Regression("existing incompatible batch member prevents all changes", () =>
{
    var sample = WorkbenchJson.CreateDemo();
    var existing = new ObjectKey("taster-living", "taster-living-switch");
    var added = new ObjectKey("presence-living", "presence-living-switch");
    WorkbenchProject.Metadata(sample, existing)!.Active = false;
    var history = new UndoStack(); history.Reset(sample);
    var count = ProjectQuery.ComObjectRefs(sample).Sum(r => r.Ref.Links.Count);
    var result = WorkbenchProject.AddLinks(sample, [existing, added], "living-switch", history);
    Check(!result.Ok && result.Changed == 0 && history.Steps == 0
        && ProjectQuery.ComObjectRefs(sample).Sum(r => r.Ref.Links.Count) == count, "batch changed despite invalid existing member");
});
if (reviewFailures.Count > 0) throw new Exception(string.Join(Environment.NewLine, reviewFailures));
Check(WorkbenchJson.Export(WorkbenchJson.Import(originalJson)) == originalJson, "JSON roundtrip");
var rows = WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Room, "living"));
Check(rows.Any(r => r.Key.ObjectRefId == "dimmer-a-value" && r.External), "external relevant channel");
Check(!rows.Any(r => r.Key.ObjectRefId.StartsWith("dimmer-b-")), "unrelated channel excluded");
var roomDevice = rows.First(r => r.Device.Id == "taster-living").Device;
Check(ReferenceEquals(roomDevice, WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Line, "1.1")).First(r => r.Device.Id == roomDevice.Id).Device), "shared identity");
foreach (var edit in new Action<JsonObject>[] {
    n => n["data"]!["rooms"]![0]!["floor"] = "missing",
    n => n["data"]!["rooms"]![0]!["id"] = "ug",
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
var notSecure = new ObjectKey("taster-office", "taster-office-switch");
Check(!WorkbenchProject.AddLinks(project, [notSecure], "kitchen-switch").Ok, "Secure capability validation");
var before = WorkbenchJson.Export(project);
Check(!WorkbenchProject.AddLinks(project, [key, wrong], "central-off", undo).Ok, "atomic incompatible batch rejected");
Check(WorkbenchJson.Export(project) == before && undo.Steps == 0, "no partial changes or undo");
Check(WorkbenchProject.AddLinks(project, [key, key], "central-off", undo).Changed == 1, "deduplicated batch");
Check(WorkbenchProject.AddLinks(project, [key], "central-off", undo).Changed == 0, "existing duplicate no-op");
Check(WorkbenchProject.SetSending(project, key, "central-off", undo).Ok, "explicit sending");
Check(!WorkbenchProject.SetSending(project, key, "living-dim").Ok, "sending must be linked");
var removed = WorkbenchProject.RemoveLink(project, key, "central-off", undo);
Check(removed.Warnings.Count == 1 && WorkbenchProject.Metadata(project, key)!.SendingGroupId is null
    && !WorkbenchProject.Metadata(project, key)!.Connectors.ContainsKey("central-off"), "warning without replacement");
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
var metadata = WorkbenchProject.Metadata(project, key)!;
var savedBits = metadata.Bits;
metadata.Bits = 8;
Check(!WorkbenchProject.AddLinks(project, [key], "central-off").Ok, "bit size validation");
metadata.Bits = savedBits;
var savedDpt = metadata.Dpt;
metadata.Dpt = "1.018";
Check(!WorkbenchProject.AddLinks(project, [key], "central-off").Ok, "equal bits do not imply equal full DPT");
metadata.Dpt = null;
Check(!WorkbenchProject.AddLinks(project, [key], "central-off").Ok, "unknown native capability is not inferred");
metadata.Dpt = savedDpt;
var flagsBefore = WorkbenchProject.Metadata(project, new("dimmer", "dimmer-a-switch"))!.Flags;
Check(WorkbenchProject.SetSending(project, new("dimmer", "dimmer-a-switch"), "central-off").Ok, "sending allowed without T flag");
Check(WorkbenchProject.Metadata(project, new("dimmer", "dimmer-a-switch"))!.Flags == flagsBefore && !flagsBefore!.Contains('T'), "sending does not invent T");
var xml = Knxproj.ToXml(project);
XNamespace vendor = "urn:test:vendor";
xml.Root!.Add(new XElement(vendor + "Opaque", new XAttribute("Data", "preserve me")));
project = Knxproj.FromXml(xml);
var path = Path.Combine(Path.GetTempPath(), "workbench-test-" + Guid.NewGuid() + ".knxproj");
try
{
    Knxproj.Save(project, path);
    var cleanPath = Path.Combine(Path.GetTempPath(), "workbench-clean-" + Guid.NewGuid() + ".knxproj");
    try
    {
        var cleanSource = WorkbenchJson.CreateDemo();
        Knxproj.Save(cleanSource, cleanPath);
        Check(WorkbenchJson.Export(Knxproj.Load(cleanPath)) == WorkbenchJson.Export(cleanSource),
            "clean archive reload remains exportable");
    }
    finally { File.Delete(cleanPath); }
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
    Reject(() => WorkbenchJson.Export(loaded), "opaque ZIP data cannot be exported to demo JSON");
}
finally { File.Delete(path); }

// ETS connector model: per-link facts, typed reasons and one sending connector per object.
var connectorProject = WorkbenchJson.CreateDemo();
var connectorKey = new ObjectKey("dimmer", "dimmer-a-switch");
var connectorUndo = new UndoStack(); connectorUndo.Reset(connectorProject);
Check(WorkbenchProject.Connector(connectorProject, connectorKey, "living-switch")!.Send, "legacy sending id reads as connector Send");
Check(WorkbenchProject.SetConnector(connectorProject, connectorKey, "central-off",
    new ConnectorMetadata { Send = true, Acknowledge = true, Priority = 2, Slot = 4 }, connectorUndo).Ok, "connector facts accepted");
var connectorMetadata = WorkbenchProject.Metadata(connectorProject, connectorKey)!;
Check(connectorMetadata.SendingGroupId == "central-off" && connectorMetadata.Connectors["central-off"].Acknowledge
    && connectorMetadata.Connectors["central-off"].Priority == 2 && connectorMetadata.Connectors["central-off"].Slot == 4, "connector facts persisted in metadata");
Check(!WorkbenchProject.Connector(connectorProject, connectorKey, "living-switch")!.Send, "send moved instead of duplicated");
Check(connectorUndo.Steps == 1, "connector change is one undo step");
Check(WorkbenchProject.SetConnector(connectorProject, connectorKey, "central-off",
    new ConnectorMetadata { Send = false, Acknowledge = true }, connectorUndo).Ok, "sending choice cleared");
Check(connectorMetadata.SendingGroupId is null
    && !WorkbenchProject.Connector(connectorProject, connectorKey, "central-off")!.Send
    && WorkbenchProject.Connector(connectorProject, connectorKey, "central-off")!.Acknowledge
    && !WorkbenchProject.Connector(connectorProject, connectorKey, "living-switch")!.Send,
    "clearing Send promotes no other link");
Check(WorkbenchProject.Connector(connectorUndo.Undo()!, connectorKey, "central-off") is { Send: true, Priority: 2, Slot: 4 },
    "undo restores connector send and facts");
Check(WorkbenchProject.Connector(connectorUndo.Redo()!, connectorKey, "central-off") is { Send: false, Acknowledge: true },
    "redo restores cleared connector send");
Check(!WorkbenchProject.SetConnector(connectorProject, connectorKey, "kitchen-switch", new ConnectorMetadata()).Ok,
    "connector requires an existing link");
Check(WorkbenchProject.SetConnector(connectorProject, connectorKey, "living-switch",
    new ConnectorMetadata { Priority = 4 }).Errors.Single().Code == LinkValidationReason.Priority, "priority limit typed");
Check(WorkbenchProject.SetConnector(connectorProject, connectorKey, "living-switch",
    new ConnectorMetadata { Slot = -1 }).Errors.Single().Code == LinkValidationReason.Slot, "negative slot typed");
Check(WorkbenchProject.SetConnector(connectorProject, connectorKey, "living-switch",
    new ConnectorMetadata { Priority = 3, Slot = 0 }).Ok, "boundary connector facts accepted");

// Typed link validation reasons behind the string-compatible ValidateLink adapter.
var typedProject = WorkbenchJson.CreateDemo();
var typedKey = new ObjectKey("dimmer", "dimmer-a-switch");
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "living-switch").Ok, "compatible link validates");
Check(WorkbenchProject.AddLinks(typedProject, [], "living-switch").Errors.Single().Code == LinkValidationReason.NoSelection, "empty batch typed");
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "missing").Reason == LinkValidationReason.Missing, "missing target typed");
var typedMetadata = WorkbenchProject.Metadata(typedProject, typedKey)!;
var typedGroup = typedProject.Workbench.Groups["living-switch"];
var typedGa = ProjectQuery.FindGroupAddressById(typedProject, "living-switch")!;
var typedBits = typedMetadata.Bits; var typedDpt = typedMetadata.Dpt;
var typedGroupBits = typedGroup.Bits; var typedGaDpt = typedGa.DatapointType;
typedMetadata.Active = false;
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "living-switch").Reason == LinkValidationReason.Inactive, "inactive typed");
typedMetadata.Active = true;
typedMetadata.Bits = 8;
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "living-switch").Reason == LinkValidationReason.BitSize, "bit size typed");
typedMetadata.Bits = typedBits;
typedMetadata.Dpt = "1.018";
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "living-switch").Reason == LinkValidationReason.DatapointType, "full DPT typed");
typedMetadata.Dpt = "6.001"; typedMetadata.Bits = 8; typedGroup.Bits = 8; typedGa.DatapointType = "5.001";
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "living-switch").Reason == LinkValidationReason.LongMainType, "long main type typed");
typedMetadata.Dpt = typedDpt; typedMetadata.Bits = typedBits; typedGroup.Bits = typedGroupBits; typedGa.DatapointType = typedGaDpt;
typedMetadata.Dpt = null;
Check(WorkbenchProject.CheckLink(typedProject, typedKey, "living-switch").Reason == LinkValidationReason.UnknownCapability, "unknown capability typed");
typedMetadata.Dpt = typedDpt;
Check(WorkbenchProject.CheckLink(typedProject, new("taster-office", "taster-office-switch"), "kitchen-switch").Reason == LinkValidationReason.SecureCapability, "secure capability typed");
var typedBatch = WorkbenchJson.CreateDemo();
var typedInactive = new ObjectKey("presence-living", "presence-living-switch");
WorkbenchProject.Metadata(typedBatch, typedInactive)!.Active = false;
var typedRejected = WorkbenchProject.AddLinks(typedBatch, [typedInactive], "living-switch");
Check(!typedRejected.Ok && typedRejected.Errors.Single().Code == LinkValidationReason.Inactive, "batch rejection carries typed reason");

// Connector persistence: native save/reload and undo roundtrip, plus legacy sending migration.
var connectorRoundtrip = WorkbenchJson.CreateDemo();
var roundtripKey = new ObjectKey("dimmer", "dimmer-a-switch");
var roundtripUndo = new UndoStack(); roundtripUndo.Reset(connectorRoundtrip);
WorkbenchProject.SetConnector(connectorRoundtrip, roundtripKey, "central-off",
    new ConnectorMetadata { Send = true, Acknowledge = true, Priority = 1, Slot = 5 }, roundtripUndo);
var connectorPath = Path.Combine(Path.GetTempPath(), "workbench-connectors-" + Guid.NewGuid() + ".knxproj");
try
{
    Knxproj.Save(connectorRoundtrip, connectorPath);
    var reloaded = Knxproj.Load(connectorPath);
    var reloadedConnector = WorkbenchProject.Connector(reloaded, roundtripKey, "central-off")!;
    Check(reloadedConnector is { Send: true, Acknowledge: true, Priority: 1, Slot: 5 }
        && WorkbenchProject.Metadata(reloaded, roundtripKey)!.SendingGroupId == "central-off", "connector metadata survives save/reload");
    Check(!WorkbenchProject.Connector(reloaded, roundtripKey, "living-switch")!.Send, "single send survives save/reload");
    var undone = roundtripUndo.Undo()!;
    Check(WorkbenchProject.Connector(undone, roundtripKey, "central-off") is { Send: false, Acknowledge: false, Priority: null, Slot: null }
        && WorkbenchProject.Metadata(undone, roundtripKey)!.SendingGroupId == "living-switch", "undo restores pre-connector metadata");
}
finally { File.Delete(connectorPath); }

var legacyXml = Knxproj.ToXml(WorkbenchJson.CreateDemo());
var legacyWorkbench = legacyXml.Descendants(XName.Get("Workbench", "urn:buschord:project:1")).Single();
var legacyData = JsonNode.Parse(legacyWorkbench.Attribute("Data")!.Value)!.AsObject();
legacyData["Objects"]![roundtripKey.ToString()]!["SendingGroupId"] = "living-switch";
legacyWorkbench.SetAttributeValue("Data", legacyData.ToJsonString());
var migrated = Knxproj.FromXml(legacyXml);
Check(WorkbenchProject.Connector(migrated, roundtripKey, "living-switch") is { Send: true }
    && WorkbenchProject.Metadata(migrated, roundtripKey)!.SendingGroupId == "living-switch", "legacy sending id reads as connector Send");
WorkbenchProject.SetConnector(migrated, roundtripKey, "central-off", new ConnectorMetadata { Send = true });
Check(!WorkbenchProject.Connector(migrated, roundtripKey, "living-switch")!.Send
    && WorkbenchProject.Metadata(migrated, roundtripKey)!.SendingGroupId == "central-off", "first connector edit replaces legacy sending id");
legacyData["Objects"]![roundtripKey.ToString()]!["Connectors"] = new JsonObject
{
    ["living-switch"] = new JsonObject { ["Send"] = true },
    ["central-off"] = new JsonObject { ["Send"] = true },
};
legacyWorkbench.SetAttributeValue("Data", legacyData.ToJsonString());
Reject(() => Knxproj.FromXml(legacyXml), "two sending connectors are rejected on load");

// Pure address allocation: gaps, boundaries, parked addresses and no mutation.
var allocation = WorkbenchJson.CreateDemo();
var allocationInstallation = allocation.Installations.Single();
Check(AddressStrategy.TryNextIndividual(allocationInstallation, 1, 1, out var nextIndividual)
    && nextIndividual == new IndividualAddress(1, 1, 1), "individual allocation starts at the first gap");
var parkedDevices = new HashSet<IndividualAddress> { new(1, 1, 1), new(1, 1, 2) };
Check(AddressStrategy.TryNextIndividual(allocationInstallation, 1, 1, out var parkedIndividual, parkedDevices)
    && parkedIndividual == new IndividualAddress(1, 1, 3), "parked individual addresses are skipped");
var allocationLine = allocationInstallation.Areas.Single(a => a.Address == 1).Lines.Single(l => l.Address == 1);
var allocationSegment = new Segment { Id = "S-ALLOC", Address = 2 };
allocationSegment.Devices.Add(new DeviceInstance { Id = "segment-device", ProductRefId = "P", Address = new IndividualAddress(1, 1, 3) });
allocationLine.Segments.Add(allocationSegment);
Check(AddressStrategy.TryNextIndividual(allocationInstallation, 1, 1, out var segmentIndividual, parkedDevices)
    && segmentIndividual == new IndividualAddress(1, 1, 4), "segment devices occupy their individual address");
var fullInstallation = new Installation { Id = "I-FULL" };
var fullArea = new Area { Id = "A-FULL", Address = 0 };
var fullLine = new Line { Id = "L-FULL", Address = 1 };
fullArea.Lines.Add(fullLine);
for (var number = 1; number <= 255; number++)
    fullLine.Devices.Add(new DeviceInstance { Id = "D-" + number, ProductRefId = "P", Address = new IndividualAddress(0, 1, (byte)number) });
fullInstallation.Areas.Add(fullArea);
Check(!AddressStrategy.TryNextIndividual(fullInstallation, 0, 1, out _), "full line has no free individual address");
fullLine.Devices.RemoveAt(199);
Check(AddressStrategy.TryNextIndividual(fullInstallation, 0, 1, out var gapIndividual) && gapIndividual.Device == 200,
    "individual allocation reuses the freed gap");

Check(AddressStrategy.TryNextGroup(allocationInstallation, out var nextGroup)
    && nextGroup == GroupAddress.ThreeLevel(0, 0, 2), "group allocation finds the first installed gap");
var parkedGroups = new HashSet<ushort> { GroupAddress.ThreeLevel(0, 0, 2).Raw, GroupAddress.ThreeLevel(0, 0, 3).Raw };
Check(AddressStrategy.TryNextGroup(allocationInstallation, out var parkedGroup, parkedGroups)
    && parkedGroup == GroupAddress.ThreeLevel(0, 0, 4), "parked group addresses are skipped");
var rangedInstallation = new Installation { Id = "I-RANGE" };
var rangedMain = new GroupRange { Id = "R-MAIN", RangeStart = 0, RangeEnd = 2047 };
rangedMain.Addresses.Add(new GroupAddressEntry { Id = "GA-1", Name = "one", Address = GroupAddress.ThreeLevel(0, 0, 1) });
rangedMain.Addresses.Add(new GroupAddressEntry { Id = "GA-3", Name = "three", Address = GroupAddress.ThreeLevel(0, 0, 3) });
rangedInstallation.MainRanges.Add(rangedMain);
Check(AddressStrategy.TryNextGroup(rangedInstallation, out var rangedGap) && rangedGap == GroupAddress.ThreeLevel(0, 0, 2),
    "group allocation bridges an occupied address gap");
var fullRange = new Installation { Id = "I-GAFULL" };
var fullGroupRange = new GroupRange { Id = "R-GAFULL", RangeStart = 0, RangeEnd = 3 };
for (var sub = 1; sub <= 3; sub++)
    fullGroupRange.Addresses.Add(new GroupAddressEntry { Id = "GA-" + sub, Name = "", Address = GroupAddress.ThreeLevel(0, 0, (byte)sub) });
fullRange.MainRanges.Add(fullGroupRange);
Check(!AddressStrategy.TryNextGroup(fullRange, out _), "full group range has no free address");
var subZeroInstallation = new Installation { Id = "I-SUBZERO" };
subZeroInstallation.MainRanges.Add(new GroupRange { Id = "R-SZ", RangeStart = 256, RangeEnd = 256 });
Check(!AddressStrategy.TryNextGroup(subZeroInstallation, out _), "three-level allocation skips the zero sub-address");
subZeroInstallation.GroupAddressStyle = GroupAddressStyle.TwoLevel;
Check(AddressStrategy.TryNextGroup(subZeroInstallation, out var twoLevel) && twoLevel.Raw == 256, "two-level style accepts the same raw address");
var emptyStyleInstallation = new Installation { Id = "I-EMPTY" };
Check(AddressStrategy.TryNextGroup(emptyStyleInstallation, out var firstGroup) && firstGroup == GroupAddress.ThreeLevel(0, 0, 1),
    "empty installation starts at 0/0/1");
var beforeAllocation = Knxproj.ToXml(allocation).ToString();
AddressStrategy.TryNextIndividual(allocationInstallation, 1, 1, out _);
AddressStrategy.TryNextGroup(allocationInstallation, out _);
Check(Knxproj.ToXml(allocation).ToString() == beforeAllocation && allocationInstallation.Areas.Count == 1,
    "allocation does not mutate the project");

Console.WriteLine("Workbench checks OK: identity, strict JSON, scope, atomic links, connector metadata/sending invariant, typed validation, address allocation, mode preview, signatures, native extension/archive and undo.");
