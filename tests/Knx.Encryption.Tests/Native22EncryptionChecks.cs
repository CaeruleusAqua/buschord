using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.TestFixtures;

internal static class Native22EncryptionChecks
{
    internal static void Run(string directory, Action<bool, string> check,
        Func<Dictionary<string, byte[]>, string, ushort, byte, byte[]> aesZip)
    {
        const string password = "synthetic22"; XNamespace ns = Native22Fixture.Namespace;
        foreach (var layout in new[] { "root", "prefixed", "nested" })
        foreach (var version in new ushort[] { 1, 2 })
        {
            void Check(string label, bool ok) => check(ok, layout + " /22 AE" + version + " " + label);
            var prefix = Native22Fixture.Prefix(layout); var parts = Native22Fixture.Parts(prefix);
            var projectZip = aesZip(parts, password, version, 3);
            var sourceBytes = layout == "nested" ? Native22Fixture.Zip(new Dictionary<string, byte[]> { [Native22Fixture.ProjectId + ".zip"] = projectZip, ["outer.bin"] = [255, 22, 0] }) : projectZip;
            var path = Path.Combine(directory, "native22-aes-" + layout + "-" + version + ".knxproj"); File.WriteAllBytes(path, sourceBytes);
            foreach (var supplied in new string?[] { null, "wrong" })
            {
                var rejected = false; try { _ = Knxproj.Load(path, supplied); } catch (InvalidDataException e) { rejected = Knxproj.IsPasswordError(e); }
                Check("Open rejects a missing/wrong password", rejected && File.ReadAllBytes(path).SequenceEqual(sourceBytes));
            }
            var project = Knxproj.Load(path, password); var device = ProjectQuery.DeviceList(project).Single();
            Check("Open authenticates native data", Knxproj.IsPasswordProtected(project) && project.Name == "Synthetic native 22" && device.Address == new IndividualAddress(1, 1, 7));
            var undo = new UndoStack(); undo.Reset(project); project.Name = "Protected edited22";
            device.ParameterValues["R-22"] = "31"; device.ComObjectRefs[0].Number = 72; device.BinaryData["Synthetic"] = [0, 255, 31]; undo.Push(project);
            var prior = undo.Undo()!; var edited = undo.Redo()!;
            Check("Undo/Redo retains format and archive protection", prior.Name == "Synthetic native 22" && Knxproj.IsPasswordProtected(prior) && Knxproj.IsPasswordProtected(edited)
                && Knxproj.ToXml(prior).Root!.Name == ns + "KNX" && ProjectQuery.DeviceList(prior).Single().ParameterValues["R-22"] == "17");
            var output = Path.Combine(directory, "native22-aes-output-" + layout + "-" + version + ".knxproj"); var sentinel = new byte[] { 22, 7, 255, 0 }; File.WriteAllBytes(output, sentinel);
            foreach (var supplied in new string?[] { null, "wrong" })
            {
                var rejected = false; try { Knxproj.Save(edited, output, supplied); } catch (InvalidDataException e) { rejected = Knxproj.IsPasswordError(e); }
                Check("Save rejects missing/wrong password atomically", rejected && File.ReadAllBytes(output).SequenceEqual(sentinel));
            }
            Knxproj.Save(edited, output, password); var reopened = Knxproj.Load(output, password); var after = ProjectQuery.DeviceList(reopened).Single();
            Check("native edits reopen encrypted", Knxproj.IsPasswordProtected(reopened) && reopened.Name == "Protected edited22" && after.ParameterValues["R-22"] == "31" && after.ComObjectRefs[0].Number == 72
                && after.ComObjectRefs[0].ChannelId == "C-22" && after.BinaryData["Synthetic"].SequenceEqual(new byte[] { 0, 255, 31 }));
            var outputBytes = File.ReadAllBytes(output); var savedZip = layout == "nested" ? Native22Fixture.ReadZip(outputBytes)[Native22Fixture.ProjectId + ".zip"] : outputBytes;
            var entries = EncryptedArchive.ReadAll(savedZip, password); var xml = Native22Fixture.Xml(entries[prefix + "0.xml"]);
            Check("native segmented placement and sidecar stay local", xml.Root!.Name == ns + "KNX" && xml.Descendants(ns + "DeviceInstance").Single().Parent!.Name == ns + "Segment" && entries.ContainsKey(prefix + "BusChord/project.xml"));
            Check("opaque22 fields survive protected rewriting", (string?)xml.Descendants(ns + "Installation").Single().Attribute("Ipv6InstallationId") == "18446744073709551615"
                && (string?)xml.Descendants(ns + "DeviceInstance").Single().Attribute("IsSlowResender") == "true" && xml.Descendants(ns + "AdditionalAddresses").Single().Elements(ns + "Address").Single().Attribute("Address") is null);
            Check("unchanged encrypted local record remains byte-exact", LocalRecord(projectZip, prefix + "opaque.bin").SequenceEqual(LocalRecord(savedZip, prefix + "opaque.bin")) && entries[prefix + "opaque.bin"].SequenceEqual(parts[prefix + "opaque.bin"]));
            Check("SaveAs changes data while retaining source", !LocalRecord(projectZip, prefix + "0.xml").SequenceEqual(LocalRecord(savedZip, prefix + "0.xml")) && File.ReadAllBytes(path).SequenceEqual(sourceBytes));
            after.ComObjectRefs[0].Number = 79; Knxproj.Save(reopened, output, password);
            Check("second protected save retains the existing sidecar", ProjectQuery.DeviceList(Knxproj.Load(output, password)).Single().ComObjectRefs[0].Number == 79);
            var noPassword = false; try { _ = Knxproj.Load(output); } catch (InvalidDataException e) { noPassword = Knxproj.IsPasswordError(e); }
            Check("new sidecar never removes archive password requirements", noPassword);
        }
    }

    private static byte[] LocalRecord(byte[] zip, string name)
    {
        var target = Encoding.UTF8.GetBytes(name);
        for (var at = 0; at + 46 + target.Length <= zip.Length; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at, 4)) != 0x02014b50
                || BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 28, 2)) != target.Length || !zip.AsSpan(at + 46, target.Length).SequenceEqual(target)) continue;
            var start = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at + 42, 4)));
            var length = checked(30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(start + 26, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(start + 28, 2)) + (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(start + 18, 4)));
            return zip.AsSpan(start, length).ToArray();
        }
        throw new InvalidDataException("Synthetic ZIP local record not found.");
    }
}
