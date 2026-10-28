using System.Xml.Linq;
using Knx;
using Knx.TestFixtures;

internal static class Native22CoreChecks
{
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "Native /22: " + name); if (!ok) failures++; }
        XNamespace ns = Native22Fixture.Namespace;
        var directory = Path.Combine(Path.GetTempPath(), "buschord-native22-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "source.knxproj"); File.WriteAllBytes(path, Native22Fixture.Archive("root"));
            var project = Knxproj.Load(path); var device = ProjectQuery.DeviceList(project).Single();
            var line = project.Installations[0].Areas[0].Lines.Single(); var segment = line.Segments.Single();
            Check("native byte address belongs to its segment", device.Address == new IndividualAddress(1, 1, 7) && segment.Devices.Single() == device && line.Devices.Count == 0);
            Check("medium and domain remain at the segment", segment.MediumTypeRefId == "MT-0" && segment.DomainAddress == "42");
            Check("separate metadata keeps its native identity", project.Id == Native22Fixture.ProjectId && project.Name == "Synthetic native 22" && project.Guid == "00000022-1111-2222-3333-444444444444");
            Check("parameter values and native channel pins load", device.ParameterValues["R-22"] == "17" && device.Channels.Single().Id == "C-22" && device.ComObjectRefs.Single().ChannelId == "C-22");
            Check("numeric group address and relative link resolve", ProjectQuery.GroupAddresses(project).First().Address.Raw == 2305 && device.ComObjectRefs.Single().Links.Single() == Native22Fixture.GroupId);
            var original = Knxproj.ToXml(project);
            Check("native projection retains /22 and numeric addresses", original.Root!.Name == ns + "KNX" && (string?)original.Descendants(ns + "DeviceInstance").Single().Attribute("Address") == "7");
            Check("opaque22 attributes survive the snapshot", Opaque(original));
            Check("native channel container is retained", original.Descendants(ns + "ChannelInstances").Count() == 1 && !original.Descendants(ns + "Channels").Any());
            var undo = new UndoStack(); undo.Reset(project);
            project.Name = "Edited native22"; device.ParameterValues["R-22"] = "23";
            device.ComObjectRefs[0].Number = 77; device.ComObjectRefs[0].Device = "Send"; device.BinaryData["Synthetic"] = [0, 255, 22];
            undo.Push(project);
            var previous = undo.Undo()!; var restored = ProjectQuery.DeviceList(previous).Single();
            Check("undo restores native metadata and raw values", previous.Name == "Synthetic native 22" && restored.ParameterValues["R-22"] == "17" && restored.BinaryData.Count == 0 && Opaque(Knxproj.ToXml(previous)));
            var next = undo.Redo()!; var after = ProjectQuery.DeviceList(next).Single();
            Check("redo retains native and editor facts", next.Name == "Edited native22" && after.ParameterValues["R-22"] == "23" && after.ComObjectRefs[0].Number == 77 && after.ComObjectRefs[0].Device == "Send" && after.BinaryData["Synthetic"].SequenceEqual(new byte[] { 0, 255, 22 }) && Opaque(Knxproj.ToXml(next)));
            foreach (var value in new byte[] { 0, 255 })
            {
                var xml = XDocument.Parse(Native22Fixture.DataXml); xml.Descendants(ns + "DeviceInstance").Single().SetAttributeValue("Address", value);
                var endpoint = Knxproj.FromXml(xml);
                Check("native device endpoint " + value, ProjectQuery.DeviceList(endpoint).Single().Address == new IndividualAddress(1, 1, value)
                    && (string?)Knxproj.ToXml(endpoint).Descendants(ns + "DeviceInstance").Single().Attribute("Address") == value.ToString());
            }
            foreach (var value in new byte[] { 0, 127 })
            {
                var xml = XDocument.Parse(Native22Fixture.DataXml); xml.Descendants(ns + "Segment").Single().SetAttributeValue("Number", value);
                var endpoint = Knxproj.FromXml(xml);
                Check("native segment endpoint " + value, endpoint.Installations[0].Areas[0].Lines[0].Segments.Single().Address == value
                    && (string?)Knxproj.ToXml(endpoint).Descendants(ns + "Segment").Single().Attribute("Number") == value.ToString());
            }
            foreach (var (name, mutate) in new (string, Action<XDocument>)[]
            {
                ("unassigned topology", x => { var d = x.Descendants(ns + "DeviceInstance").Single(); d.Remove(); x.Descendants(ns + "Topology").Single().Add(new XElement(ns + "UnassignedDevices", d)); }),
                ("missing main device address", x => x.Descendants(ns + "DeviceInstance").Single().Attribute("Address")!.Remove()),
                ("dotted native device address", x => x.Descendants(ns + "DeviceInstance").Single().SetAttributeValue("Address", "1.1.7")),
                ("device number beyond255", x => x.Descendants(ns + "DeviceInstance").Single().SetAttributeValue("Address", "256")),
                ("device directly under line", x => { var d = x.Descendants(ns + "DeviceInstance").Single(); d.Remove(); x.Descendants(ns + "Line").Single().Add(d); }),
                ("wrapped device", x => { var d = x.Descendants(ns + "DeviceInstance").Single(); d.ReplaceWith(new XElement(ns + "Wrapper", new XElement(d))); }),
                ("segment number128", x => x.Descendants(ns + "Segment").Single().SetAttributeValue("Number", "128")),
                ("missing segment medium", x => x.Descendants(ns + "Segment").Single().Attribute("MediumTypeRefId")!.Remove()),
                ("missing segment", x => x.Descendants(ns + "Segment").Single().Remove()),
                ("missing Area identity", x => x.Descendants(ns + "Area").Single().Attribute("Id")!.Remove()),
                ("missing device identity", x => x.Descendants(ns + "DeviceInstance").Single().Attribute("Id")!.Remove()),
                ("missing line identity", x => x.Descendants(ns + "Line").Single().Attribute("Id")!.Remove()),
                ("cross-kind duplicate identity", x => x.Descendants(ns + "ChannelInstance").Single().SetAttributeValue("Id", Native22Fixture.DeviceId)),
                ("area address16", x => x.Descendants(ns + "Area").Single().SetAttributeValue("Address", "16")),
                ("line address16", x => x.Descendants(ns + "Line").Single().SetAttributeValue("Address", "16")),
                ("duplicate topology container", x => x.Descendants(ns + "Installation").Single().Add(new XElement(ns + "Topology"))),
                ("duplicate installation container", x => x.Root!.Element(ns + "Project")!.Add(new XElement(ns + "Installations"))),
                ("duplicate parameter container", x => x.Descendants(ns + "DeviceInstance").Single().Add(new XElement(ns + "ParameterInstanceRefs"))),
                ("duplicate channel container", x => x.Descendants(ns + "DeviceInstance").Single().Add(new XElement(ns + "ChannelInstances"))),
            })
            {
                var xml = XDocument.Parse(Native22Fixture.DataXml); mutate(xml); var refused = false;
                try { _ = Knxproj.FromXml(xml); } catch (InvalidDataException) { refused = true; }
                Check(name + " is refused before model publication", refused);
            }
            var unsupported = false;
            try { _ = Knxproj.FromXml(XDocument.Parse(Native22Fixture.DataXml.Replace("project/22", "project/24", StringComparison.Ordinal))); }
            catch (InvalidDataException) { unsupported = true; }
            Check("unknown schema is not inferred from topology", unsupported);
            var mutated = Knxproj.Load(path); mutated.Installations[0].Areas[0].Lines[0].Segments[0].Address = 128;
            var target = Path.Combine(directory, "atomic.knxproj"); var sentinel = new byte[] { 22, 255, 0 }; File.WriteAllBytes(target, sentinel);
            var blocked = false; try { Knxproj.Save(mutated, target); } catch (InvalidDataException) { blocked = true; }
            Check("unsupported model cannot replace an existing destination", blocked && File.ReadAllBytes(target).SequenceEqual(sentinel));
        }
        finally { Directory.Delete(directory, true); }
        return failures;

        bool Opaque(XDocument xml) => (string?)xml.Descendants(ns + "Installation").Single().Attribute("Ipv6InstallationId") == "18446744073709551615"
            && (string?)xml.Descendants(ns + "DeviceInstance").Single().Attribute("IsSlowResender") == "true"
            && xml.Descendants(ns + "AdditionalAddresses").Single().Elements(ns + "Address").Single() is { } address
            && address.Attribute("Address") is null && (string?)address.Attribute("Name") == "Unassigned secondary";
    }
}
