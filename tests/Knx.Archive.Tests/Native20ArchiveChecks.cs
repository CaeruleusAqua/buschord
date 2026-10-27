using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.TestFixtures;

internal static class Native20ArchiveChecks
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        XNamespace ns = Native20Fixture.Namespace;
        XNamespace vendor = "urn:synthetic:retained";
        foreach (var layout in new[] { "root", "nested", "prefixed" })
        {
            var prefix = Native20Fixture.Prefix(layout);
            var parts = Native20Fixture.Parts(prefix);
            var data = XDocument.Parse(Native20Fixture.DataXml, LoadOptions.PreserveWhitespace);
            var rawDevice = data.Descendants(ns + "DeviceInstance").Single();
            rawDevice.SetAttributeValue(vendor + "marker", "retained");
            rawDevice.Element(ns + "ComObjectInstanceRefs")!.AddFirst(new XComment("between native references"));
            rawDevice.Add(new XElement(vendor + "Payload", "opaque text"));
            parts[prefix + "0.xml"] = Bytes(data);
            var source = Path.Combine(directory, "native20-" + layout + ".knxproj");
            var sourceBytes = layout == "nested" ? Native20Fixture.Zip(new Dictionary<string, byte[]> { [Native20Fixture.ProjectId + ".zip"] = Native20Fixture.Zip(parts), ["outer.bin"] = [255, 7, 0] }) : Native20Fixture.Zip(parts);
            File.WriteAllBytes(source, sourceBytes);
            var project = Knxproj.Load(source); var device = ProjectQuery.DeviceList(project).Single();
            check(project.Name == "Synthetic native 20" && device.Address == new IndividualAddress(1, 1, 7), layout + " /20 source loads native metadata and address");
            var undo = new UndoStack(); undo.Reset(project);
            project.Name = "Edited " + layout; device.Name = "Edited device"; device.Address = new(1, 1, 9);
            device.ParameterValues["R-1"] = "23"; device.ComObjectRefs[0].Number = 71; device.ComObjectRefs[0].Device = "Send";
            device.ComObjectRefs[0].Links.Clear(); device.ComObjectRefs[0].Links.Add(Native20Fixture.SecondGroupId);
            device.BinaryData["Synthetic"] = [0, 255, 23];
            undo.Push(project);
            var restored = undo.Undo()!; var restoredDevice = ProjectQuery.DeviceList(restored).Single();
            check(restored.Name == "Synthetic native 20" && restoredDevice.ParameterValues["R-1"] == "17" && restoredDevice.ComObjectRefs[0].Links.Single() == Native20Fixture.GroupId, layout + " /20 undo restores native values and links");
            var edited = undo.Redo()!; var editedDevice = ProjectQuery.DeviceList(edited).Single();
            var output = Path.Combine(directory, "native20-" + layout + "-edited.knxproj");
            Knxproj.Save(edited, output);
            var entries = Native20Fixture.ProjectParts(output, layout);
            var xml = Read(entries[prefix + "0.xml"]);
            var information = Read(entries[prefix + "project.xml"]);
            var savedDevice = xml.Descendants(ns + "DeviceInstance").Single();
            var line = xml.Descendants(ns + "Line").Single();
            check(xml.Root!.Name.Namespace == ns && savedDevice.Parent == line && !xml.Descendants(ns + "Segment").Any(), layout + " save retains /20 direct line topology");
            check((string?)line.Attribute("MediumTypeRefId") == "MT-0" && (string?)line.Attribute("DomainAddress") == "42", layout + " save keeps line medium and domain");
            check((string?)savedDevice.Attribute("Address") == "9" && (string?)xml.Descendants(ns + "GroupAddress").First().Attribute("Address") == "2305", layout + " save keeps numeric native addresses");
            check((string?)savedDevice.Element(ns + "ParameterInstanceRefs")!.Elements().Single().Attribute("Value") == "23" && savedDevice.Element(ns + "ChannelInstances") is not null && savedDevice.Element(ns + "Channels") is null, layout + " native parameter and channel containers are emitted");
            check((string?)savedDevice.Attribute(vendor + "marker") == "retained" && (string?)savedDevice.Element(vendor + "Payload") == "opaque text" && savedDevice.DescendantNodes().OfType<XComment>().Any(c => c.Value == "between native references"), layout + " unknown XML and comments retain their slots");
            check(entries[prefix + "opaque.bin"].SequenceEqual(new byte[] { 0, 255, 17, 128 }) && File.ReadAllBytes(source).SequenceEqual(sourceBytes), layout + " Save As preserves opaque bytes and source archive");
            check(entries.ContainsKey(prefix + "BusChord/project.xml") && !xml.Root.DescendantsAndSelf().Any(e => e.Name.NamespaceName == "urn:buschord:project:1") && !xml.Descendants().Attributes().Any(a => a.Name.NamespaceName == "urn:buschord:project:1"), layout + " editor facts remain in a project-local sidecar");
            check((string?)information.Descendants(ns + "ProjectInformation").Single().Attribute("Name") == "Edited " + layout && !xml.Descendants(ns + "ProjectInformation").Any(), layout + " metadata remains in its separate original entry");
            var reloaded = Knxproj.Load(output); var after = ProjectQuery.DeviceList(reloaded).Single();
            check(reloaded.Name == "Edited " + layout && reloaded.Guid == project.Guid && after.Name == "Edited device" && after.Address == new IndividualAddress(1, 1, 9), layout + " native metadata and edits reopen");
            check(after.ParameterValues["R-1"] == "23" && after.ComObjectRefs[0].Number == 71 && after.ComObjectRefs[0].Device == "Send" && after.ComObjectRefs[0].Links.Single() == Native20Fixture.SecondGroupId, layout + " sidecar overrides and native links reopen");
            check(after.Channels.Single().Id == "C-1" && after.ComObjectRefs[0].ChannelId == "C-1" && after.BinaryData["Synthetic"].SequenceEqual(new byte[] { 0, 255, 23 }), layout + " channel pins and binary payload reopen");
            var copyLine = reloaded.Installations[0].Areas[0].Lines[0];
            var copy = ProjectOperations.CopyDevice(reloaded, after, copyLine);
            var copied = Path.Combine(directory, "native20-" + layout + "-copy.knxproj"); Knxproj.Save(reloaded, copied);
            var copiedXml = Read(Native20Fixture.ProjectParts(copied, layout)[prefix + "0.xml"]);
            check(copy.Ok && copiedXml.Descendants(ns + "DeviceInstance").Count() == 2 && copiedXml.Descendants(ns + "DeviceInstance").All(d => int.TryParse((string?)d.Attribute("Puid"), out var value) && value > 0), layout + " new native device gets a unique positive Puid");
            var copyDevice = ProjectQuery.DeviceList(Knxproj.Load(copied)).Single(d => d.Id == copy.Value!.Id);
            check(copyDevice.Channels.Single().Id != "C-1" && copyDevice.ComObjectRefs[0].ChannelId == copyDevice.Channels.Single().Id && copyDevice.Channels.Single().RefId == "CH-1", layout + " copied native channel pins survive save/reopen");
            if (layout == "prefixed")
            {
                check(!entries.ContainsKey("0.xml") && !entries.ContainsKey("project.xml") && !entries.ContainsKey("BusChord/project.xml"), "prefixed /20 never acquires duplicate root project parts");
                var second = Knxproj.Load(copied); ProjectQuery.DeviceList(second).Single(d => d.Id == copyDevice.Id).ComObjectRefs[0].Number = 79;
                Knxproj.Save(second, copied);
                var twice = Knxproj.Load(copied); var twiceEntries = Native20Fixture.ProjectParts(copied, layout);
                check(twiceEntries.ContainsKey(prefix + "BusChord/project.xml") && ProjectQuery.DeviceList(twice).Single(d => d.Id == copyDevice.Id).ComObjectRefs[0].Number == 79,
                    "prefixed /20 existing project-local sidecar survives a second save/reopen");
            }
            if (layout == "nested") check(Native20Fixture.ReadZip(File.ReadAllBytes(output))["outer.bin"].SequenceEqual(new byte[] { 255, 7, 0 }), "nested /20 retains the outer opaque entry");
        }

        void Refused(string name, Dictionary<string, byte[]> parts)
        {
            var path = Path.Combine(directory, "native20-invalid-" + Guid.NewGuid().ToString("N") + ".knxproj");
            var bytes = Native20Fixture.Zip(parts); File.WriteAllBytes(path, bytes);
            var refused = false; try { _ = Knxproj.Load(path); } catch (InvalidDataException) { refused = true; }
            check(refused && File.ReadAllBytes(path).SequenceEqual(bytes), name + " refuses without modifying source");
        }
        var rootAndPrefix = Native20Fixture.Parts(); foreach (var pair in Native20Fixture.Parts(Native20Fixture.ProjectId + "/")) rootAndPrefix.Add(pair.Key, pair.Value);
        Refused("root and prefixed projects", rootAndPrefix);
        var rootAndNested = Native20Fixture.Parts(); rootAndNested.Add("P-other.zip", Native20Fixture.Zip(Native20Fixture.Parts())); Refused("root and nested projects", rootAndNested);
        var multiplePrefix = Native20Fixture.Parts(Native20Fixture.ProjectId + "/"); multiplePrefix.Add("P-other/0.xml", Encoding.UTF8.GetBytes(Native20Fixture.DataXml)); Refused("multiple prefixed projects", multiplePrefix);
        Refused("directory identity mismatch", Native20Fixture.Parts("P-other/"));
        Refused("unsafe project directory", Native20Fixture.Parts("P-0020/../"));
        var duplicateInformation = Native20Fixture.Parts(Native20Fixture.ProjectId + "/"); duplicateInformation.Add("project.xml", Encoding.UTF8.GetBytes(Native20Fixture.InformationXml)); Refused("conflicting metadata locations", duplicateInformation);
        foreach (var (name, value) in new[]
        {
            ("metadata project identity mismatch", Native20Fixture.InformationXml.Replace("P-0020", "P-other", StringComparison.Ordinal)),
            ("metadata namespace mismatch", Native20Fixture.InformationXml.Replace("project/20", "project/21", StringComparison.Ordinal)),
        }) { var parts = Native20Fixture.Parts(); parts["project.xml"] = Encoding.UTF8.GetBytes(value); Refused(name, parts); }
        var foreign = Native20Fixture.Parts(Native20Fixture.ProjectId + "/"); foreign[Native20Fixture.ProjectId + "/0.xml"] = Encoding.UTF8.GetBytes(Native20Fixture.DataXml.Replace("project/20", "project/21", StringComparison.Ordinal)); Refused("prefixed layout does not infer schema support", foreign);
        var ambiguousInfo = XDocument.Parse(Native20Fixture.InformationXml); ambiguousInfo.Root!.Add(new XElement(ambiguousInfo.Root.Elements().Single()));
        var ambiguousParts = Native20Fixture.Parts(); ambiguousParts["project.xml"] = Bytes(ambiguousInfo); Refused("duplicate information project", ambiguousParts);

        var atomicPath = Path.Combine(directory, "native20-atomic-source.knxproj"); File.WriteAllBytes(atomicPath, Native20Fixture.Archive("prefixed"));
        var atomic = Knxproj.Load(atomicPath); var sentinelPath = Path.Combine(directory, "native20-atomic-destination.knxproj"); var sentinel = "unchanged destination"u8.ToArray();
        File.WriteAllBytes(sentinelPath, sentinel);
        atomic.Installations[0].Areas[0].Lines[0].Segments.Add(new Segment { Id = "S-injected", Address = 0 });
        var unsafeSave = false; try { Knxproj.Save(atomic, sentinelPath); } catch (InvalidDataException) { unsafeSave = true; }
        check(unsafeSave && File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel), "injected segments reject before destination replacement");
        atomic.Installations[0].Areas[0].Lines[0].Segments.Clear(); atomic.Guid = null;
        var missingGuid = false; try { Knxproj.Save(atomic, sentinelPath); } catch (InvalidDataException) { missingGuid = true; }
        check(missingGuid && File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel), "missing native Guid never invents metadata or replaces the destination");
        ModuleRoundtrip(directory, check);
    }

    private static void ModuleRoundtrip(string directory, Action<bool, string> check)
    {
        XNamespace ns = Native20Fixture.Namespace;
        const string appId = "M-FFE0_A-1";
        const string moduleId = appId + "_MD-1";
        const string callId = moduleId + "_M-1";
        const string parameterId = appId + "_MD-1_M-1_MI-2_P-1_R-1";
        const string objectId = "MD-1_M-1_MI-2_O-1_R-1";
        var application = XDocument.Parse($$"""
            <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-FFE0"><ApplicationPrograms><ApplicationProgram Id="{{appId}}">
              <Static><ParameterTypes><ParameterType Id="T-count"><TypeNumber SizeInBit="8" Type="unsignedInt" minInclusive="0" maxInclusive="3"/></ParameterType></ParameterTypes>
                <Parameters><Parameter Id="P-count" Name="Count" ParameterType="T-count" Value="2"/></Parameters><ParameterRefs><ParameterRef Id="R-count" RefId="P-count"/></ParameterRefs></Static>
              <ModuleDefs><ModuleDef Id="{{moduleId}}"><Arguments><Argument Id="{{moduleId}}_A-1" Name="Literal" Type="Numeric"/></Arguments><Static>
                <Parameters><Parameter Id="{{moduleId}}_P-1" Name="Member" ParameterType="T-count" Value="0"/></Parameters><ParameterRefs><ParameterRef Id="{{moduleId}}_P-1_R-1" RefId="{{moduleId}}_P-1"/></ParameterRefs>
                <ComObjectTable><ComObject Id="{{moduleId}}_O-1" Number="0" Text="Object"/></ComObjectTable><ComObjectRefs><ComObjectRef Id="{{moduleId}}_O-1_R-1" RefId="{{moduleId}}_O-1"/></ComObjectRefs>
                </Static><Dynamic><ParameterRefRef RefId="{{moduleId}}_P-1_R-1"/><ComObjectRefRef RefId="{{moduleId}}_O-1_R-1"/></Dynamic></ModuleDef></ModuleDefs>
              <Dynamic><ParameterRefRef RefId="R-count"/><Repeat Id="REP" Count="0" ParameterRefId="R-count"><Module Id="{{callId}}" RefId="{{moduleId}}"><NumericArg RefId="{{moduleId}}_A-1" Value="5"/></Module></Repeat></Dynamic>
            </ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
            """);
        var data = XDocument.Parse(Native20Fixture.DataXml); var device = data.Descendants(ns + "DeviceInstance").Single();
        device.SetAttributeValue("ProductRefId", appId); device.Attribute("Hardware2ProgramRefId")!.Remove(); device.Elements().Remove();
        device.Add(new XElement(ns + "ParameterInstanceRefs", new XElement(ns + "ParameterInstanceRef", new XAttribute("RefId", "R-count"), new XAttribute("Value", "2")),
            new XElement(ns + "ParameterInstanceRef", new XAttribute("RefId", parameterId), new XAttribute("Value", "2"))),
            new XElement(ns + "ComObjectInstanceRefs", new XComment("module object comment"), new XElement(ns + "ComObjectInstanceRef", new XAttribute("RefId", objectId), new XAttribute("Text", "Explicit member"), new XAttribute("Links", "GA-1"))),
            new XElement(ns + "ModuleInstances", new XComment("module container comment"), Enumerable.Range(1, 2).Select(i => new XElement(ns + "ModuleInstance", new XAttribute("Id", "MD-1_M-1_MI-" + i), new XAttribute("RefId", "MD-1_M-1"),
                new XElement(ns + "Arguments", new XElement(ns + "Argument", new XAttribute("RefId", "MD-1_A-1"), new XAttribute("Value", "5")))))));
        var parts = Native20Fixture.Parts(); parts["0.xml"] = Bytes(data); parts["M-FFE0/" + appId + ".xml"] = Bytes(application);
        var path = Path.Combine(directory, "native20-module.knxproj"); File.WriteAllBytes(path, Native20Fixture.Zip(parts));
        var project = Knxproj.Load(path); var model = ProjectQuery.DeviceList(project).Single();
        model.ParameterValues["R-count"] = "1"; Knxproj.Save(project, path);
        var reduced = Read(Native20Fixture.ReadZip(File.ReadAllBytes(path))["0.xml"]);
        check(reduced.Descendants(ns + "ModuleInstance").Select(e => (string?)e.Attribute("Id")).SequenceEqual(new[] { "MD-1_M-1_MI-1" }), "/20 Count reduction synchronizes native module instances");
        check(!reduced.Descendants(ns + "ParameterInstanceRef").Any(e => (string?)e.Attribute("RefId") == parameterId) && !reduced.Descendants(ns + "ComObjectInstanceRef").Any(), "/20 dormant references leave native project XML");
        project = Knxproj.Load(path); model = ProjectQuery.DeviceList(project).Single();
        check(model.ParameterValues[parameterId] == "2" && model.ComObjectRefs.Single().Links.Single() == Native20Fixture.GroupId && model.ComObjectRefs.Single().Text == "Explicit member", "/20 sidecar restores dormant values, links and explicit text");
        model.ParameterValues["R-count"] = "0"; Knxproj.Save(project, path);
        var zero = Read(Native20Fixture.ReadZip(File.ReadAllBytes(path))["0.xml"]);
        check(!zero.Descendants(ns + "ModuleInstances").Any(), "/20 Count zero removes the nonempty native module container");
        project = Knxproj.Load(path); model = ProjectQuery.DeviceList(project).Single(); model.ParameterValues["R-count"] = "3"; Knxproj.Save(project, path);
        var grown = Read(Native20Fixture.ReadZip(File.ReadAllBytes(path))["0.xml"]);
        check(grown.Descendants(ns + "ModuleInstance").Select(e => (string?)e.Attribute("Id")).SequenceEqual(new[] { "MD-1_M-1_MI-1", "MD-1_M-1_MI-2", "MD-1_M-1_MI-3" }), "/20 Count regrowth restores ordered native identities");
        check(grown.Descendants(ns + "Argument").All(a => (string?)a.Attribute("RefId") == "MD-1_A-1" && (string?)a.Attribute("Value") == "5") && grown.DescendantNodes().OfType<XComment>().Any(c => c.Value == "module container comment"), "/20 module literal arguments and container payload survive regrowth");
        check(grown.Descendants(ns + "ComObjectInstanceRef").Single().Attribute("RefId")!.Value == objectId && grown.Descendants(ns + "ParameterInstanceRef").Any(p => (string?)p.Attribute("RefId") == parameterId && (string?)p.Attribute("Value") == "2"), "/20 regrowth restores stored member references and values");
    }

    private static byte[] Bytes(XDocument document) => Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    private static XDocument Read(byte[] bytes) { using var stream = new MemoryStream(bytes, false); return XDocument.Load(stream); }
}
