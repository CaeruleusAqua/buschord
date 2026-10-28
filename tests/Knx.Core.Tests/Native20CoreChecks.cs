using System.Xml;
using System.Xml.Linq;
using Knx;
using Knx.TestFixtures;

internal static class Native20CoreChecks
{
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "Native /20: " + name); if (!ok) failures++; }
        XNamespace ns = Native20Fixture.Namespace;
        var directory = Path.Combine(Path.GetTempPath(), "buschord-native20-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "source.knxproj");
            File.WriteAllBytes(path, Native20Fixture.Archive("root"));
            var project = Knxproj.Load(path);
            var device = ProjectQuery.DeviceList(project).Single();
            var line = project.Installations.Single().Areas.Single().Lines.Single();
            Check("native device byte and line location compose 1.1.7", device.Address == new IndividualAddress(1, 1, 7) && line.Devices.Single() == device && line.Segments.Count == 0);
            Check("line retains its own medium and domain", line.MediumTypeRefId == "MT-0" && line.DomainAddress == "42");
            Check("separate native metadata loads", project.Name == "Synthetic native 20" && project.Guid == "00000020-1111-2222-3333-444444444444" && project.Comment == "Synthetic metadata");
            Check("native parameter and channel reference load", device.ParameterValues["R-1"] == "17" && device.Channels.Single().Id == "C-1" && device.Channels.Single().RefId == "CH-1");
            Check("numeric group address and local links resolve", ProjectQuery.GroupAddresses(project).First().Address.Raw == 2305 && device.ComObjectRefs.Single().Links.Single() == Native20Fixture.GroupId);
            Check("native object channel pin remains exact", device.ComObjectRefs.Single().ChannelId == "C-1");
            var snapshot = Knxproj.ToXml(project);
            Check("snapshot retains schema20 and direct line devices", snapshot.Root!.Name.Namespace == ns && snapshot.Descendants(ns + "DeviceInstance").Single().Parent!.Name == ns + "Line" && !snapshot.Descendants(ns + "Segment").Any());
            Check("snapshot uses native scalar fields", (string?)snapshot.Descendants(ns + "DeviceInstance").Single().Attribute("Address") == "7" && (string?)snapshot.Descendants(ns + "GroupAddress").First().Attribute("Address") == "2305");
            Check("snapshot carries native metadata for undo", (string?)snapshot.Descendants(ns + "ProjectInformation").Single().Attribute("Guid") == project.Guid && snapshot.Descendants(ns + "ChannelInstances").Count() == 1);
            var undo = new UndoStack(); undo.Reset(project);
            project.Name = "Edited native20"; device.ParameterValues["R-1"] = "23";
            device.ComObjectRefs[0].Number = 77; device.ComObjectRefs[0].Device = "Send";
            device.BinaryData["Synthetic"] = [0, 255, 7];
            undo.Push(project);
            var restored = undo.Undo()!; var original = ProjectQuery.DeviceList(restored).Single();
            Check("undo restores metadata and native values", restored.Name == "Synthetic native 20" && original.ParameterValues["R-1"] == "17" && original.BinaryData.Count == 0);
            var redone = undo.Redo()!; var edited = ProjectQuery.DeviceList(redone).Single();
            Check("redo retains native schema and all editor facts", Knxproj.ToXml(redone).Root!.Name.Namespace == ns && redone.Name == "Edited native20" && edited.ParameterValues["R-1"] == "23" && edited.ComObjectRefs[0].Number == 77 && edited.ComObjectRefs[0].Device == "Send" && edited.BinaryData["Synthetic"].SequenceEqual(new byte[] { 0, 255, 7 }));
            var before = Knxproj.ToXml(project).ToString(); var steps = undo.Steps;
            Check("segment creation refuses before changing model or undo", ProjectOperations.CreateSegment(project, line, "Unsupported", undo: undo).Status == ProjectEditStatus.InvalidTarget && line.Segments.Count == 0 && undo.Steps == steps && Knxproj.ToXml(project).ToString() == before);
            var unknownSegment = new Segment { Id = "S-forbidden", Address = 0 };
            Check("device move into a segment refuses atomically", !ProjectOperations.MoveDevice(project, device, line, unknownSegment, undo: undo).Ok && line.Devices.Single() == device && undo.Steps == steps);
            Check("device copy into a segment refuses atomically", !ProjectOperations.CopyDevice(project, device, line, unknownSegment, undo: undo).Ok && ProjectQuery.DeviceList(project).Count() == 1 && undo.Steps == steps);
            Check("segment move and rename stay unavailable", !ProjectOperations.MoveSegment(project, unknownSegment, line, undo: undo).Ok && !ProjectOperations.RenameSegment(project, unknownSegment, "Renamed", undo).Ok && undo.Steps == steps);
            var copy = ProjectOperations.CopyDevice(project, device, line);
            var legalId = copy.Value?.Id ?? "";
            try { XmlConvert.VerifyNCName(legalId); } catch (XmlException) { legalId = ""; }
            Check("native copy has a new schema-safe identity", copy.Ok && legalId.Length > 0 && legalId != device.Id && copy.Value!.Address != device.Address);
            Check("copy remaps channel pins and deep-copies binary bytes", copy.Ok && copy.Value!.Channels.Single().Id != "C-1" && copy.Value.ComObjectRefs.Single().ChannelId == copy.Value.Channels.Single().Id && copy.Value.Channels.Single().RefId == "CH-1" && !ReferenceEquals(copy.Value.BinaryData["Synthetic"], device.BinaryData["Synthetic"]));
            var created = ProjectOperations.CreateLine(project, project.Installations[0].Areas[0], "Second line", address: 2);
            Check("line creation stays available in schema20", created.Ok && created.Value!.Address == 2 && created.Value.Segments.Count == 0);
            var moved = created.Ok && ProjectOperations.MoveDevice(project, device, created.Value!).Ok;
            Check("moving to another line rebases native byte geometry", moved && device.Address.Area == 1 && device.Address.Line == 2 && created.Value!.Devices.Single() == device && device.ComObjectRefs[0].ChannelId == "C-1");
            var noAreaId = XDocument.Parse(Native20Fixture.DataXml); noAreaId.Descendants(ns + "Area").Single().Attribute("Id")!.Remove();
            var optionalArea = Knxproj.FromXml(noAreaId);
            Check("optional Area identity is not invented in unchanged XML", ProjectQuery.DeviceList(optionalArea).Single().Address == new IndividualAddress(1, 1, 7) && Knxproj.ToXml(optionalArea).Descendants(ns + "Area").Single().Attribute("Id") is null);
            foreach (var value in new byte[] { 0, 255 })
            {
                var xml = XDocument.Parse(Native20Fixture.DataXml);
                xml.Descendants(ns + "DeviceInstance").Single().SetAttributeValue("Address", value);
                var edge = Knxproj.FromXml(xml);
                Check("native device endpoint " + value, ProjectQuery.DeviceList(edge).Single().Address == new IndividualAddress(1, 1, value) && (string?)Knxproj.ToXml(edge).Descendants(ns + "DeviceInstance").Single().Attribute("Address") == value.ToString());
            }
            foreach (var (name, mutate) in new (string, Action<XDocument>)[]
            {
                ("segment topology", x => x.Descendants(ns + "Line").Single().Add(new XElement(ns + "Segment", new XAttribute("Id", "S-1")))),
                ("device under area", x => { var d = x.Descendants(ns + "DeviceInstance").Single(); d.Remove(); x.Descendants(ns + "Area").Single().Add(d); }),
                ("wrapped native device", x => { var d = x.Descendants(ns + "DeviceInstance").Single(); d.ReplaceWith(new XElement(ns + "Wrapper", new XElement(d))); }),
                ("missing native number", x => x.Descendants(ns + "DeviceInstance").Single().Attribute("Address")!.Remove()),
                ("missing device identity", x => x.Descendants(ns + "DeviceInstance").Single().Attribute("Id")!.Remove()),
                ("missing line identity", x => x.Descendants(ns + "Line").Single().Attribute("Id")!.Remove()),
                ("dotted native number", x => x.Descendants(ns + "DeviceInstance").Single().SetAttributeValue("Address", "1.1.7")),
                ("area beyond fifteen", x => x.Descendants(ns + "Area").Single().SetAttributeValue("Address", "16")),
                ("missing line medium", x => x.Descendants(ns + "Line").Single().Attribute("MediumTypeRefId")!.Remove()),
                ("duplicate native identity", x => x.Descendants(ns + "ChannelInstance").Single().SetAttributeValue("Id", "D-1")),
                ("nested native channels", x => x.Descendants(ns + "ChannelInstance").Single().Add(new XElement(ns + "ChannelInstance", new XAttribute("Id", "C-2")))),
            })
            {
                var xml = XDocument.Parse(Native20Fixture.DataXml); mutate(xml);
                var refused = false; try { _ = Knxproj.FromXml(xml); } catch (InvalidDataException) { refused = true; }
                Check(name + " is controlled unsupported input", refused);
            }
            var foreign = XDocument.Parse(Native20Fixture.DataXml.Replace("project/20", "project/24", StringComparison.Ordinal));
            var unsupported = false; try { _ = Knxproj.FromXml(foreign); } catch (InvalidDataException) { unsupported = true; }
            Check("schema24 is not inferred from similar layout", unsupported);
        }
        finally { Directory.Delete(directory, true); }
        return failures;
    }
}
