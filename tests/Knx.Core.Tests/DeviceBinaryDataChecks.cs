using System.Xml.Linq;
using Knx;

internal static class DeviceBinaryDataChecks
{
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "Device binary: " + name); if (!ok) failures++; }
        foreach (var schema in new[] { 21, 23 })
        {
            var raw = Fixture(schema, """
                <BinaryData><BinaryData Name="FunctionTexts"><Data> AAECAw== <!--keep--></Data></BinaryData>
                <BinaryData Name="Private" DoNotCopy="1"><Data>/w==</Data></BinaryData>
                <BinaryData Name="Absent"/><BinaryData Name="Empty"><Data/></BinaryData></BinaryData>
                """);
            var project = Knxproj.FromXml(raw); var device = ProjectQuery.DeviceList(project).Single();
            Check(schema + " names and buffers decode without aliases", !device.HasOpaqueBinaryData && device.BinaryData.Count == 4
                && device.BinaryData["FunctionTexts"].SequenceEqual(new byte[] { 0, 1, 2, 3 }));
            Check(schema + " no-op retains original whitespace and comment", XNode.DeepEquals(Binary(raw), Binary(Knxproj.ToXml(project))));
            var revision = ProjectRevisions.ContentHash(project);
            var undo = new UndoStack(); undo.Reset(project);
            device.BinaryData["FunctionTexts"] = [0, 255, 23];
            Check(schema + " revision tracks blob changes", ProjectRevisions.ContentHash(project) != revision);
            undo.Push(project);
            var changed = Knxproj.ToXml(project);
            Check(schema + " changing text preserves its comment", Binary(changed).Descendants().Single(e => (string?)e.Attribute("Name") == "FunctionTexts")
                .Element(changed.Root!.Name.Namespace + "Data") is { } data && Convert.FromBase64String(data.Value).SequenceEqual(new byte[] { 0, 255, 23 })
                && data.Nodes().OfType<XComment>().Single().Value == "keep");
            Check(schema + " reload keeps changed bytes and absent vs empty Data", ProjectQuery.DeviceList(Knxproj.FromXml(changed)).Single().BinaryData["FunctionTexts"].SequenceEqual(new byte[] { 0, 255, 23 })
                && Binary(changed).Elements().Single(e => (string?)e.Attribute("Name") == "Absent").IsEmpty
                && Binary(changed).Elements().Single(e => (string?)e.Attribute("Name") == "Empty").Elements().Count() == 1);
            Check(schema + " undo and redo restore independent binary buffers", ProjectQuery.DeviceList(undo.Undo()!).Single().BinaryData["FunctionTexts"].SequenceEqual(new byte[] { 0, 1, 2, 3 })
                && ProjectQuery.DeviceList(undo.Redo()!).Single().BinaryData["FunctionTexts"].SequenceEqual(new byte[] { 0, 255, 23 }));
            var line = project.Installations.Single().Areas.Single().Lines.Single();
            var copy = ProjectOperations.CopyDevice(project, device, line);
            Check(schema + " clone copies buffers and honors DoNotCopy", copy.Ok && copy.Value!.BinaryData.Count == 3
                && !copy.Value.BinaryData.ContainsKey("Private") && !ReferenceEquals(copy.Value.BinaryData["FunctionTexts"], device.BinaryData["FunctionTexts"]));
            device.BinaryData.Remove("Absent"); device.BinaryData.Remove("Empty"); device.BinaryData.Remove("Private");
            device.BinaryData["Second"] = [7];
            var roundtrip = Knxproj.FromXml(Knxproj.ToXml(project));
            Check(schema + " name identities support removal and addition", ProjectQuery.DeviceList(roundtrip).First().BinaryData.Count == 2
                && ProjectQuery.DeviceList(roundtrip).First().BinaryData["Second"].SequenceEqual(new byte[] { 7 }));
            device.BinaryData.Clear();
            Check(schema + " deleting last entry does not retain stale bytes", !ProjectQuery.DeviceList(Knxproj.FromXml(Knxproj.ToXml(project))).First().BinaryData.Any());
        }
        foreach (var content in new[]
        {
            "<BinaryData><BinaryData><Data>AQ==</Data></BinaryData></BinaryData>",
            "<BinaryData><BinaryData Name='X'/><BinaryData Name='X'/></BinaryData>",
            "<BinaryData><BinaryData Name='X'><Data>not base64</Data></BinaryData></BinaryData>",
            "<BinaryData><BinaryData Name='X'><Data/><Data/></BinaryData></BinaryData>",
            "<BinaryData><BinaryData Name='X' Vendor='unknown'><Data>AQ==</Data></BinaryData></BinaryData>",
            "<BinaryData><BinaryData Name='X'><Vendor/></BinaryData></BinaryData>",
            "<BinaryData><BinaryData Name='X' DoNotCopy='maybe'/></BinaryData>",
            "<BinaryData><BinaryData Name='X'/></BinaryData><BinaryData><BinaryData Name='Y'/></BinaryData>",
        })
        {
            var original = Fixture(23, content); var project = Knxproj.FromXml(original); var device = ProjectQuery.DeviceList(project).Single();
            var before = Knxproj.ToXml(project).ToString();
            Check("opaque payload retained without partial decoding", device.HasOpaqueBinaryData && device.BinaryData.Count == 0
                && original.Descendants().Where(e => e.Name.LocalName == "DeviceInstance").Single().Elements().Select(e => e.ToString()).SequenceEqual(
                    Knxproj.ToXml(project).Descendants().Where(e => e.Name.LocalName == "DeviceInstance").Single().Elements().Select(e => e.ToString())));
            Check("opaque payload blocks copying atomically", !ProjectOperations.CopyDevice(project, device, project.Installations[0].Areas[0].Lines[0]).Ok
                && Knxproj.ToXml(project).ToString() == before);
            device.BinaryData["New"] = [1];
            var refused = false;
            try { Knxproj.ToXml(project); } catch (InvalidDataException) { refused = true; }
            Check("opaque payload refuses conflicting modeled writes", refused);
        }
        var loose = Fixture(21, "<BinaryData><BinaryData Name='X'><Data>AQ==</Data></BinaryData></BinaryData>");
        var looseDevice = loose.Descendants().Single(e => e.Name.LocalName == "DeviceInstance");
        var segment = looseDevice.Parent!; looseDevice.Remove(); segment.AddAfterSelf(looseDevice); segment.Remove();
        var legacy = Knxproj.FromXml(loose); ProjectQuery.DeviceList(legacy).Single().BinaryData["X"] = [2];
        var projectionRejected = false;
        try { Knxproj.ToXml(legacy); } catch (InvalidDataException) { projectionRejected = true; }
        Check("an unmodeled native topology relocation cannot duplicate a device", projectionRejected);
        return failures;
    }

    private static XElement Binary(XDocument document) => document.Descendants().First(e => e.Name.LocalName == "DeviceInstance").Elements().First(e => e.Name.LocalName == "BinaryData");
    private static XDocument Fixture(int schema, string binary) => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/{{schema}}"><Project Id="P-1" Name="Binary">
        <Installations><Installation InstallationId="I-1"><Topology><Area Id="A-1" Address="1">
        <Line Id="L-1" Address="1" MediumTypeRefId="MT-0">{{(schema == 21 ? "<Segment Id='S-1' Number='0' MediumTypeRefId='MT-0'>" : "")}}<DeviceInstance Id="D-1" Address="{{(schema == 21 ? "1" : "1.1.1")}}" ProductRefId="M-0004_A-5018-15-17D9">{{binary}}</DeviceInstance>{{(schema == 21 ? "</Segment>" : "")}}
        </Line></Area></Topology></Installation></Installations></Project></KNX>
        """, LoadOptions.PreserveWhitespace);
}
