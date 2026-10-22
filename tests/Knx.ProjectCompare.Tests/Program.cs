using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.ProjectCompare;
using Knx.Workbench;

// Self-check for project comparison and durable revisions. Run:
//   dotnet run --project tests/Knx.ProjectCompare.Tests
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

static Project BuildProject(string name = "Projekt")
{
    var project = new Project
    {
        Id = "P-1",
        Name = name,
        Guid = "{11111111-2222-3333-4444-555555555555}",
        ProjectStart = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(2)),
    };
    var installation = new Installation
    {
        Id = "I-1",
        Name = "Haus",
        Address = new IndividualAddress(1, 1, 0),
        Type = InstallationType.Regular,
    };
    var area = new Area { Id = "A-1", Address = 1, Name = "EG" };
    var line = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
    var device = new DeviceInstance
    {
        Id = "D-1",
        Address = new IndividualAddress(1, 1, 1),
        ProductRefId = "M-1",
        Hardware2ProgramRefId = "M-1_H-1",
        Name = "Aktor",
        Description = "Schaltaktor",
    };
    device.ComObjectRefs.Add(new ComObjectInstanceRef
    {
        Id = "O-1",
        ComObjectId = "CO-1",
        Number = 1,
        Text = "Schalten",
    });
    device.ComObjectRefs.Add(new ComObjectInstanceRef
    {
        Id = "O-2",
        ComObjectId = "CO-2",
        Number = 2,
        Text = "Rueckmeldung",
    });
    line.Devices.Add(device);
    area.Lines.Add(line);
    installation.Areas.Add(area);

    var range = new GroupRange { Id = "GR-1", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
    range.Addresses.Add(new GroupAddressEntry
    {
        Id = "GA-1",
        Name = "Licht schalten",
        Address = GroupAddress.ThreeLevel(1, 1, 1),
        DatapointType = "DPST-1-1",
        Description = "Deckenlicht",
    });
    installation.MainRanges.Add(range);

    var building = new SpaceNode { Id = "SP-1", Name = "Haus", Kind = SpaceKind.Building };
    var room = new SpaceNode { Id = "SP-2", Name = "Wohnen", Kind = SpaceKind.Room, Number = "1" };
    building.Add(room);
    installation.Locations.AddRoot(building);

    project.Installations.Add(installation);
    return project;
}

static DifferenceItem? Item(DifferenceReport report, string path) =>
    report.Items.FirstOrDefault(i => i.Path == path);

static void LinkAll(Project project)
{
    var device = ProjectQuery.DeviceList(project).Single(d => d.Id == "D-1");
    GroupLinks.Link(project, device, "O-1", "GA-1");
}

static void SetObjectMetadata(Project project, string deviceId, string objectRefId, ObjectMetadata metadata) =>
    project.Workbench.Objects[new ObjectKey(deviceId, objectRefId).ToString()] = metadata;

// --- Links: add/remove and connector facts ------------------------------------------------
{
    var left = BuildProject();
    var right = BuildProject();
    LinkAll(left);
    LinkAll(right);
    SetObjectMetadata(left, "D-1", "O-1", new ObjectMetadata { Dpt = "1.001", Bits = 1, Flags = "CRT" });
    SetObjectMetadata(right, "D-1", "O-1", new ObjectMetadata
    {
        Dpt = "1.001",
        Bits = 1,
        Flags = "CRT",
        Connectors = { ["GA-1"] = new ConnectorMetadata { Send = true, Acknowledge = true, Priority = 1, Slot = 4 } },
    });

    var rightDevice = ProjectQuery.DeviceList(right).Single(d => d.Id == "D-1");
    GroupLinks.Link(right, rightDevice, "O-2", "GA-1");

    var leftDevice = ProjectQuery.DeviceList(left).Single(d => d.Id == "D-1");
    GroupLinks.Link(left, leftDevice, "O-2", "GA-1");
    GroupLinks.Unlink(right, rightDevice, "O-2", "GA-1");

    var report = ProjectComparer.Compare(left, right, DateTimeOffset.UnixEpoch);
    var changed = Item(report, "Device/1.1.1/Link/O-1/1/1/1");
    Check("link: connector facts compare as changed", changed is
    {
        ObjectType: "GroupLink",
        Modification: ModificationType.Changed,
    }, changed?.ToString());
    Check("link: changed connector keeps old and new state",
        changed?.OldValue?.Contains("send=false") == true && changed.NewValue?.Contains("priority=1") == true,
        $"{changed?.OldValue} -> {changed?.NewValue}");
    var removed = Item(report, "Device/1.1.1/Link/O-2/1/1/1");
    Check("link: removed link is reported", removed is { Modification: ModificationType.Removed },
        removed?.Modification.ToString());
    Check("link: item order is ordinal", report.Items.Select(i => i.Path)
        .SequenceEqual(report.OrderedItems().Select(i => i.Path)));
}

// --- Parameters, object/device/group/floor workbench metadata ----------------------------
{
    var left = BuildProject();
    var right = BuildProject();
    var leftDevice = ProjectQuery.DeviceList(left).Single();
    var rightDevice = ProjectQuery.DeviceList(right).Single();
    leftDevice.ParameterValues["brightness"] = "10";
    rightDevice.ParameterValues["brightness"] = "20";
    rightDevice.ParameterValues["mode"] = "\"dim\"";
    leftDevice.ParameterValues["legacy"] = "1";

    SetObjectMetadata(left, "D-1", "O-1", new ObjectMetadata { Dpt = "1.001", Bits = 1, Active = true, Role = "switch" });
    SetObjectMetadata(right, "D-1", "O-1", new ObjectMetadata { Dpt = "5.001", Bits = 8, Active = false, Role = "scene" });
    left.Workbench.Devices["D-1"] = new DeviceMetadata { Application = "A-1", Revision = 1, DownloadedRevision = 1 };
    right.Workbench.Devices["D-1"] = new DeviceMetadata { Application = "A-2", Revision = 2, DownloadedRevision = 1, Demo = true };
    left.Workbench.Groups["GA-1"] = new GroupMetadata { Bits = 1, Secure = false, EffectRoomId = "SP-2" };
    right.Workbench.Groups["GA-1"] = new GroupMetadata { Bits = 8, Secure = true, EffectRoomId = "SP-1" };
    left.Workbench.FloorShortNames["SP-2"] = "EG";
    right.Workbench.FloorShortNames["SP-2"] = "OG";

    var report = ProjectComparer.Compare(left, right, DateTimeOffset.UnixEpoch);
    Check("parameter: changed value", Item(report, "Device/1.1.1/Parameter/brightness") is
        { ObjectType: "Parameter", Modification: ModificationType.Changed, OldValue: "10", NewValue: "20" });
    Check("parameter: added value", Item(report, "Device/1.1.1/Parameter/mode") is
        { Modification: ModificationType.Added, NewValue: "\"dim\"" });
    Check("parameter: removed value", Item(report, "Device/1.1.1/Parameter/legacy") is
        { Modification: ModificationType.Removed, OldValue: "1" });
    Check("workbench: object dpt/bits/active/role", Item(report, "Workbench/Object/3:D-1O-1/Dpt") is
        { ObjectType: "WorkbenchObject", OldValue: "1.001", NewValue: "5.001" }
        && Item(report, "Workbench/Object/3:D-1O-1/Bits") is { OldValue: "1", NewValue: "8" }
        && Item(report, "Workbench/Object/3:D-1O-1/Active") is { OldValue: "true", NewValue: "false" }
        && Item(report, "Workbench/Object/3:D-1O-1/Role") is { OldValue: "switch", NewValue: "scene" },
        string.Join(", ", report.Items.Select(i => i.Path)));
    Check("workbench: device metadata", Item(report, "Workbench/Device/D-1/Application") is
        { ObjectType: "WorkbenchDevice", OldValue: "A-1", NewValue: "A-2" }
        && Item(report, "Workbench/Device/D-1/Revision") is { OldValue: "1", NewValue: "2" }
        && Item(report, "Workbench/Device/D-1/Demo") is { OldValue: "false", NewValue: "true" });
    Check("workbench: group metadata", Item(report, "Workbench/Group/GA-1/Bits") is { OldValue: "1", NewValue: "8" }
        && Item(report, "Workbench/Group/GA-1/Secure") is { OldValue: "false", NewValue: "true" }
        && Item(report, "Workbench/Group/GA-1/EffectRoomId") is { OldValue: "SP-2", NewValue: "SP-1" });
    Check("workbench: floor short name", Item(report, "Workbench/Floor/SP-2/ShortName") is
        { OldValue: "EG", NewValue: "OG" });
}

// --- Building tree locations --------------------------------------------------------------
{
    var left = BuildProject();
    var right = BuildProject();
    var leftRoom = left.Installations.Single().Locations.Find("SP-2")!;
    leftRoom.Name = "Wohnen";
    leftRoom.AssignDevice("D-1");
    var rightInstallation = right.Installations.Single();
    var rightRoom = rightInstallation.Locations.Find("SP-2")!;
    rightRoom.Name = "Wohnzimmer";
    var floor = rightInstallation.Locations.Add(null, "Obergeschoss", SpaceKind.Floor, id: "SP-3");
    rightInstallation.Locations.Add(rightRoom, "Schlafen", SpaceKind.Room, id: "SP-4");
    rightRoom.UnassignDevice("D-1");

    var report = ProjectComparer.Compare(left, right, DateTimeOffset.UnixEpoch);
    Check("location: renamed room", Item(report, "Location/I-1/SP-2/Name") is
        { ObjectType: "Location", OldValue: "Wohnen", NewValue: "Wohnzimmer" });
    Check("location: added floor", Item(report, "Location/I-1/SP-3") is
        { Modification: ModificationType.Added, ObjectType: "Location" });
    Check("location: added room", Item(report, "Location/I-1/SP-4") is
        { Modification: ModificationType.Added });
    Check("location: device assignment", Item(report, "Location/I-1/SP-2/Devices") is
        { OldValue: "D-1", NewValue: "" });
    Check("location: floor parent is visible", floor.Parent is null && floor.Children.Count == 0);
}

// --- Durable revision roundtrip, old projects and JSON extension -------------------------
{
    var directory = Path.Combine(Path.GetTempPath(), "knx-project-versioning-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "project.knxproj");
        var project = BuildProject();
        project.MasterData.Signature = "ets-signature-text";
        Knxproj.Save(project, path);
        var capturedBytes = File.ReadAllBytes(path);
        var capturedHash = Convert.ToHexString(SHA256.HashData(capturedBytes));
        var timestamp = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(2));
        var revision = ProjectRevisions.Capture(project, path, "Erststand", timestamp);
        Check("revision: capture fields", revision is
        {
            Number: 1,
            Id: "REV-0001",
            Description: "Erststand",
            SignatureStatus: RevisionSignatureStatus.Unverified,
        } && revision.ContentHash.Length == 64 && revision.SourceHash == capturedHash
            && revision.SignatureHash?.Length == 64,
            $"{revision.Id} hash={revision.ContentHash.Length} source={revision.SourceHash?.Length}");
        var hashBeforeSecond = ProjectRevisions.ContentHash(project);
        var second = ProjectRevisions.Capture(project, description: "Nacharbeit", timestamp: timestamp.AddHours(1));
        Check("revision: numbering is monotonic and id is stable",
            second.Number == 2 && second.Id == "REV-0002" && second.SourceHash is null);
        Check("revision: capturing does not change the content hash",
            ProjectRevisions.ContentHash(project) == hashBeforeSecond);
        Check("revision: latest is the highest number", ProjectRevisions.Latest(project)?.Id == "REV-0002");

        Knxproj.Save(project, path);
        var loaded = Knxproj.Load(path);
        Check("revision: knxproj roundtrip keeps the log", loaded.Workbench.Revisions.Count == 2);
        var loadedRevision = loaded.Workbench.Revisions.Single(r => r.Id == "REV-0001");
        Check("revision: knxproj roundtrip keeps every field",
            loadedRevision.Number == 1 && loadedRevision.Timestamp == timestamp
            && loadedRevision.SourcePath == path && loadedRevision.SourceHash == capturedHash
            && loadedRevision.ContentHash == revision.ContentHash
            && loadedRevision.HashAlgorithm == ProjectRevisions.HashAlgorithm
            && loadedRevision.Description == "Erststand"
            && loadedRevision.SignatureStatus == RevisionSignatureStatus.Unverified
            && loadedRevision.SignatureHash == revision.SignatureHash);
        Check("revision: content hash survives save/load",
            ProjectRevisions.ContentHash(loaded) == revision.ContentHash);

        var json = JsonSerializer.Serialize(project.Workbench);
        var metadata = JsonSerializer.Deserialize<WorkbenchMetadata>(json)!;
        metadata.Validate();
        Check("revision: JSON metadata roundtrip", metadata.Revisions.Count == 2
            && metadata.Revisions[0].ContentHash == revision.ContentHash);

        // An old project has no Revisions property; it must load with an empty log.
        var xml = Knxproj.ToXml(BuildProject("Alt"));
        xml.Descendants(XName.Get("Workbench", "urn:buschord:project:1")).Remove();
        var old = Knxproj.FromXml(xml);
        Check("revision: old project without extension stays loadable",
            old.Workbench.Revisions.Count == 0 && old.Name == "Alt");

        var withMetadata = BuildProject("Alt-Metadaten");
        withMetadata.Workbench.Devices["ghost"] = new DeviceMetadata();
        var xmlMetadata = Knxproj.ToXml(withMetadata);
        var workbench = xmlMetadata.Descendants(XName.Get("Workbench", "urn:buschord:project:1")).Single();
        var data = JsonNode.Parse(workbench.Attribute("Data")!.Value)!.AsObject();
        data.Remove("Revisions");
        workbench.SetAttributeValue("Data", data.ToJsonString());
        var oldMetadata = Knxproj.FromXml(xmlMetadata);
        Check("revision: old workbench JSON without Revisions stays loadable",
            oldMetadata.Workbench.Revisions.Count == 0 && oldMetadata.Workbench.Devices.ContainsKey("ghost"));

        // V4 JSON must not silently drop a revision log.
        var demo = WorkbenchJson.CreateDemo();
        WorkbenchJson.Export(demo);
        ProjectRevisions.Capture(demo, description: "demo revision");
        var refused = false;
        try { WorkbenchJson.Export(demo); }
        catch (InvalidDataException) { refused = true; }
        Check("revision: lossy V4 JSON export is refused", refused);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

// --- Reproducible text/HTML comparison report --------------------------------------------
{
    var left = BuildProject("Kunde & Partner");
    var right = BuildProject("Kunde & Partner");
    LinkAll(left);
    LinkAll(right);
    var leftDevice = ProjectQuery.DeviceList(left).Single();
    var rightDevice = ProjectQuery.DeviceList(right).Single();
    leftDevice.ParameterValues["brightness"] = "10";
    rightDevice.ParameterValues["brightness"] = "20";
    right.Installations.Single().Locations.Find("SP-2")!.Name = "Wohnzimmer <Nord>";
    SetObjectMetadata(right, "D-1", "O-1", new ObjectMetadata { Dpt = "1.001", Bits = 1, Active = true });

    var timestamp = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2));
    var sourcePath = Path.Combine(Path.GetTempPath(), "Kunde Geheim", "project.knxproj");
    var leftRevision = ProjectRevisions.Capture(left, sourcePath, "links", timestamp);
    var rightRevision = ProjectRevisions.Capture(right, sourcePath, "parameter", timestamp.AddMinutes(5));
    var report = ProjectComparer.Compare(left, right, leftRevision, rightRevision, timestamp);
    report.Items.Add(new DifferenceItem
    {
        Path = "Project/Namensschild",
        ObjectType = "Project",
        ObjectName = "<script>alert('x')</script>",
        Modification = ModificationType.Changed,
        OldValue = "alt",
        NewValue = "neu",
    });

    var text = DifferenceReportRenderer.RenderText(report);
    var html = DifferenceReportRenderer.RenderHtml(report);
    Check("report: text is reproducible", text == DifferenceReportRenderer.RenderText(report));
    Check("report: html is reproducible", html == DifferenceReportRenderer.RenderHtml(report));
    Check("report: text hash is stable", DifferenceReportRenderer.RenderTextHash(report) ==
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    Check("report: revision comparison is visible",
        text.Contains("left-revision=number:1") && text.Contains("right-revision=number:1")
        && text.Contains("revision-content=changed"), text.Split('\n').FirstOrDefault(l => l.StartsWith("revision-content")));
    Check("report: source path is never rendered",
        !text.Contains("Kunde Geheim", StringComparison.Ordinal)
        && !html.Contains("Kunde Geheim", StringComparison.Ordinal)
        && !text.Contains("source-path", StringComparison.Ordinal));
    Check("report: HTML escapes user text",
        html.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;") && !html.Contains("<script"));
    Check("report: no external references", !html.Contains("http://") && !html.Contains("https://"));

    var reordered = new DifferenceReport
    {
        LeftProjectName = report.LeftProjectName,
        RightProjectName = report.RightProjectName,
        GeneratedAt = report.GeneratedAt,
        LeftRevision = report.LeftRevision,
        RightRevision = report.RightRevision,
    };
    foreach (var item in report.Items.AsEnumerable().Reverse()) reordered.Items.Add(item);
    Check("report: item insertion order does not change the output",
        DifferenceReportRenderer.RenderTextHash(reordered) == DifferenceReportRenderer.RenderTextHash(report));

    var culture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        var german = DifferenceReportRenderer.RenderTextHash(report);
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        Check("report: output is culture-independent", german == DifferenceReportRenderer.RenderTextHash(report));
    }
    finally { CultureInfo.CurrentCulture = culture; }

    // Same revision on both sides means unchanged content; a parameter change is detected.
    var same = ProjectComparer.CompareRevisions(left, left, timestamp);
    Check("revision: identical content is reported unchanged", same.ContentUnchanged);
    var different = ProjectComparer.CompareRevisions(left, right, timestamp);
    Check("revision: parameter change is reported changed", !different.ContentUnchanged);
    Check("revision: signature state change is detected", leftRevision.SignatureStatus == rightRevision.SignatureStatus
        && different.SignatureUnchanged);
}

Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;
