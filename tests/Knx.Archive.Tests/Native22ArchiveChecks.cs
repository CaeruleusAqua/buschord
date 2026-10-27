using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.TestFixtures;

internal static class Native22ArchiveChecks
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        XNamespace ns = Native22Fixture.Namespace;
        foreach (var layout in new[] { "root", "nested", "prefixed" })
        {
            void Check(string name, bool value) => check(value, layout + " /22 " + name);
            var prefix = Native22Fixture.Prefix(layout); var parts = Native22Fixture.Parts(prefix);
            var raw = XDocument.Parse(Native22Fixture.DataXml); raw.Descendants(ns + "ComObjectInstanceRefs").Single().AddFirst(new XComment("native22 retained comment"));
            parts[prefix + "0.xml"] = Native22Fixture.Bytes(raw);
            var sourceBytes = Native22Fixture.Wrap(parts, layout); var source = Path.Combine(directory, "native22-" + layout + ".knxproj"); File.WriteAllBytes(source, sourceBytes);
            var project = Knxproj.Load(source); var device = ProjectQuery.DeviceList(project).Single(); var undo = new UndoStack(); undo.Reset(project);
            project.Name = "Edited native22 " + layout; device.Name = "Edited device22"; device.Address = new(1, 1, 9); device.ParameterValues["R-22"] = "23";
            device.ComObjectRefs[0].Number = 71; device.ComObjectRefs[0].Device = "Send"; device.ComObjectRefs[0].Links.Clear(); device.ComObjectRefs[0].Links.Add(Native22Fixture.SecondGroupId);
            device.BinaryData["Synthetic"] = [0, 255, 22]; undo.Push(project);
            var prior = undo.Undo()!; var priorDevice = ProjectQuery.DeviceList(prior).Single();
            Check("undo restores raw values, pins and metadata", prior.Name == "Synthetic native 22" && priorDevice.ParameterValues["R-22"] == "17" && priorDevice.Address == new IndividualAddress(1, 1, 7) && priorDevice.ComObjectRefs[0].Links.Single() == Native22Fixture.GroupId);
            var edited = undo.Redo()!; var output = Path.Combine(directory, "native22-" + layout + "-saved.knxproj"); Knxproj.Save(edited, output);
            var saved = Native22Fixture.ProjectParts(output, layout); var xml = Native22Fixture.Xml(saved[prefix + "0.xml"]); var information = Native22Fixture.Xml(saved[prefix + "project.xml"]);
            Check("save retains native namespace and segmented topology", xml.Root!.Name == ns + "KNX" && xml.Descendants(ns + "DeviceInstance").Single().Parent!.Name == ns + "Segment" && xml.Descendants(ns + "Segment").Single().Parent!.Name == ns + "Line");
            Check("segment medium and domain never move to line", (string?)xml.Descendants(ns + "Segment").Single().Attribute("MediumTypeRefId") == "MT-0" && (string?)xml.Descendants(ns + "Segment").Single().Attribute("DomainAddress") == "42" && xml.Descendants(ns + "Line").Single().Attribute("MediumTypeRefId") is null);
            Check("native device and group addresses remain numeric", (string?)xml.Descendants(ns + "DeviceInstance").Single().Attribute("Address") == "9" && (string?)xml.Descendants(ns + "GroupAddress").First().Attribute("Address") == "2305");
            Check("opaque22 fields retain their exact source values", Opaque(xml));
            Check("comments and native channel containers remain", xml.DescendantNodes().OfType<XComment>().Any(c => c.Value == "native22 retained comment") && xml.Descendants(ns + "ChannelInstances").Count() == 1 && !xml.Descendants(ns + "Channels").Any());
            Check("project-local sidecar separates editor facts", saved.ContainsKey(prefix + "BusChord/project.xml") && !xml.Descendants().Attributes().Any(a => a.Name.NamespaceName == "urn:buschord:project:1"));
            Check("metadata keeps its separate native location", information.Root!.Name == ns + "KNX" && (string?)information.Descendants(ns + "ProjectInformation").Single().Attribute("Name") == "Edited native22 " + layout && !xml.Descendants(ns + "ProjectInformation").Any());
            Check("source and unrelated bytes are unchanged", File.ReadAllBytes(source).SequenceEqual(sourceBytes) && saved[prefix + "opaque.bin"].SequenceEqual(parts[prefix + "opaque.bin"]));
            var reopened = Knxproj.Load(output); var after = ProjectQuery.DeviceList(reopened).Single();
            Check("native values and sidecar facts reopen", after.Name == "Edited device22" && after.ParameterValues["R-22"] == "23" && after.ComObjectRefs[0].Number == 71 && after.ComObjectRefs[0].Device == "Send" && after.ComObjectRefs[0].Links.Single() == Native22Fixture.SecondGroupId && after.BinaryData["Synthetic"].SequenceEqual(new byte[] { 0, 255, 22 }));
            Check("channel identity and pin reopen", after.Channels.Single().Id == "C-22" && after.ComObjectRefs[0].ChannelId == "C-22" && after.Channels.Single().RefId == "CH-22");
            after.ComObjectRefs[0].Number = 79; Knxproj.Save(reopened, output);
            var twice = Knxproj.Load(output);
            Check("second save keeps an existing sidecar and override", ProjectQuery.DeviceList(twice).Single().ComObjectRefs[0].Number == 79 && Opaque(Native22Fixture.Xml(Native22Fixture.ProjectParts(output, layout)[prefix + "0.xml"])));
            if (layout == "prefixed") Check("prefix never creates duplicate root parts", !saved.ContainsKey("0.xml") && !saved.ContainsKey("project.xml") && !saved.ContainsKey("BusChord/project.xml"));
            if (layout == "nested") Check("outer opaque record remains exact", Native22Fixture.ReadZip(File.ReadAllBytes(output))["outer.bin"].SequenceEqual(new byte[] { 255, 22, 0 }));
            ModuleRoundtrip(layout, directory, Check);
        }

        void Refused(string label, Dictionary<string, byte[]> parts)
        {
            var path = Path.Combine(directory, "native22-invalid-" + Guid.NewGuid().ToString("N") + ".knxproj"); var bytes = Native22Fixture.Zip(parts); File.WriteAllBytes(path, bytes);
            var refused = false; try { _ = Knxproj.Load(path); } catch (InvalidDataException) { refused = true; }
            check(refused && File.ReadAllBytes(path).SequenceEqual(bytes), "/22 " + label + " refuses without source mutation");
        }
        var duplicate = Native22Fixture.Parts(); foreach (var part in Native22Fixture.Parts(Native22Fixture.ProjectId + "/")) duplicate.Add(part.Key, part.Value); Refused("root and prefix collision", duplicate);
        var nestedCollision = Native22Fixture.Parts(); nestedCollision.Add("P-other.zip", Native22Fixture.Archive("root")); Refused("root and nested collision", nestedCollision);
        var prefixCollision = Native22Fixture.Parts(Native22Fixture.ProjectId + "/"); prefixCollision.Add("P-other/0.xml", Encoding.UTF8.GetBytes(Native22Fixture.DataXml)); Refused("multiple prefixes", prefixCollision);
        Refused("mismatching directory identity", Native22Fixture.Parts("P-other/"));
        Refused("traversal directory", Native22Fixture.Parts("P-0022/../"));
        Refused("backslash directory", Native22Fixture.Parts("P-0022\\child/"));
        foreach (var (label, information) in new[]
        {
            ("metadata Id mismatch", Native22Fixture.InformationXml.Replace("P-0022", "P-other", StringComparison.Ordinal)),
            ("metadata namespace mismatch", Native22Fixture.InformationXml.Replace("project/22", "project/21", StringComparison.Ordinal)),
        }) { var parts = Native22Fixture.Parts(); parts["project.xml"] = Encoding.UTF8.GetBytes(information); Refused(label, parts); }
        var conflicting = Native22Fixture.Parts(Native22Fixture.ProjectId + "/"); conflicting.Add("project.xml", Encoding.UTF8.GetBytes(Native22Fixture.InformationXml)); Refused("conflicting information location", conflicting);
        var foreign = Native22Fixture.Parts(Native22Fixture.ProjectId + "/"); foreign[Native22Fixture.ProjectId + "/0.xml"] = Encoding.UTF8.GetBytes(Native22Fixture.DataXml.Replace("project/22", "project/23", StringComparison.Ordinal)); Refused("prefix never selects schema", foreign);

        bool Opaque(XDocument xml) => (string?)xml.Descendants(ns + "Installation").Single().Attribute("Ipv6InstallationId") == "18446744073709551615"
            && (string?)xml.Descendants(ns + "DeviceInstance").Single().Attribute("IsSlowResender") == "true"
            && xml.Descendants(ns + "AdditionalAddresses").Single().Elements(ns + "Address").Single().Attribute("Address") is null;
    }

    private static void ModuleRoundtrip(string layout, string directory, Action<string, bool> check)
    {
        XNamespace ns = Native22Fixture.Namespace; var prefix = Native22Fixture.Prefix(layout); var source = Path.Combine(directory, "native22-modules-" + layout + ".knxproj");
        var sourceBytes = Native22Fixture.Wrap(Native22Fixture.ModuleParts(prefix), layout); File.WriteAllBytes(source, sourceBytes);
        var project = Knxproj.Load(source); var device = ProjectQuery.DeviceList(project).Single(); var undo = new UndoStack(); undo.Reset(project);
        device.ParameterValues["R-count"] = "1"; undo.Push(project);
        check("module count edit has one undo and preserves member value", undo.Steps == 1 && ProjectQuery.DeviceList(undo.Undo()!).Single().ParameterValues["R-count"] == "2");
        project = undo.Redo()!; var output = Path.Combine(directory, "native22-modules-" + layout + "-saved.knxproj"); Knxproj.Save(project, output);
        var reduced = Native22Fixture.Xml(Native22Fixture.ProjectParts(output, layout)[prefix + "0.xml"]);
        check("module count reduction writes the native first occurrence", reduced.Descendants(ns + "ModuleInstance").Select(e => (string?)e.Attribute("Id")).SequenceEqual(new[] { "MD-1_M-1_MI-1" }));
        check("dormant references leave native data", !reduced.Descendants(ns + "ParameterInstanceRef").Any(p => (string?)p.Attribute("RefId") == Native22Fixture.MemberParameterId) && !reduced.Descendants(ns + "ComObjectInstanceRef").Any());
        project = Knxproj.Load(output); device = ProjectQuery.DeviceList(project).Single();
        check("dormant sidecar restores parameter, links and channel pin", device.ParameterValues[Native22Fixture.MemberParameterId] == "2" && device.ComObjectRefs.Single().Links.Single() == Native22Fixture.GroupId && device.ComObjectRefs.Single().ChannelId == "C-22");
        device.ParameterValues["R-count"] = "0"; Knxproj.Save(project, output);
        check("zero count removes the native nonempty container", !Native22Fixture.Xml(Native22Fixture.ProjectParts(output, layout)[prefix + "0.xml"]).Descendants(ns + "ModuleInstances").Any());
        project = Knxproj.Load(output); device = ProjectQuery.DeviceList(project).Single(); device.ParameterValues["R-count"] = "3"; Knxproj.Save(project, output);
        var grown = Native22Fixture.Xml(Native22Fixture.ProjectParts(output, layout)[prefix + "0.xml"]);
        check("regrowth restores ordered relative module identities", grown.Descendants(ns + "ModuleInstance").Select(e => (string?)e.Attribute("Id")).SequenceEqual(new[] { "MD-1_M-1_MI-1", "MD-1_M-1_MI-2", "MD-1_M-1_MI-3" }));
        check("literal arguments and container comments survive", grown.Descendants(ns + "Argument").All(a => (string?)a.Attribute("RefId") == "MD-1_A-1" && (string?)a.Attribute("Value") == "5") && grown.DescendantNodes().OfType<XComment>().Any(c => c.Value == "module container comment"));
        check("member overrides return at regrowth", grown.Descendants(ns + "ParameterInstanceRef").Any(p => (string?)p.Attribute("RefId") == Native22Fixture.MemberParameterId && (string?)p.Attribute("Value") == "2") && (string?)grown.Descendants(ns + "ComObjectInstanceRef").Single().Attribute("RefId") == Native22Fixture.MemberObjectId);
        check("module SaveAs preserves original input", File.ReadAllBytes(source).SequenceEqual(sourceBytes));
    }
}
