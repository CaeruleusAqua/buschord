using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.TestFixtures;

internal static class Native20EncryptionChecks
{
    internal static void Run(string directory, Action<bool, string> check,
        Func<Dictionary<string, byte[]>, string, ushort, byte, byte[]> aesZip)
    {
        const string password = "synthetic20";
        XNamespace ns = Native20Fixture.Namespace;
        foreach (var layout in new[] { "root", "prefixed", "nested" })
        foreach (var version in new ushort[] { 1, 2 })
        {
            var prefix = Native20Fixture.Prefix(layout);
            var parts = Native20Fixture.Parts(prefix);
            byte[] sourceBytes;
            if (layout == "nested")
                sourceBytes = Native20Fixture.Zip(new Dictionary<string, byte[]> { [Native20Fixture.ProjectId + ".zip"] = aesZip(parts, password, version, 3), ["outer.bin"] = [1, 255, 7] });
            else sourceBytes = aesZip(parts, password, version, 3);
            var path = Path.Combine(directory, "native20-protected-" + layout + "-" + version + ".knxproj"); File.WriteAllBytes(path, sourceBytes);
            var missing = false; try { _ = Knxproj.Load(path); } catch (InvalidDataException e) { missing = Knxproj.IsPasswordError(e); }
            check(missing, layout + " /20 AE" + version + " requires a password on Open");
            var project = Knxproj.Load(path, password);
            check(Knxproj.IsPasswordProtected(project) && project.Name == "Synthetic native 20" && ProjectQuery.DeviceList(project).Single().Address == new IndividualAddress(1, 1, 7), layout + " /20 AE" + version + " authenticates native data");
            var undo = new UndoStack(); undo.Reset(project); project.Name = "Protected edited20";
            var device = ProjectQuery.DeviceList(project).Single(); device.ParameterValues["R-1"] = "31"; device.ComObjectRefs[0].Number = 72; device.BinaryData["Synthetic"] = [0, 255, 31];
            undo.Push(project); var before = undo.Undo()!; var edited = undo.Redo()!;
            check(before.Name == "Synthetic native 20" && Knxproj.IsPasswordProtected(before) && Knxproj.IsPasswordProtected(edited) && edited.Name == "Protected edited20", layout + " /20 protected Undo retains format, values and immutable archive identity");
            var output = Path.Combine(directory, "native20-protected-output-" + layout + "-" + version + ".knxproj"); var sentinel = "existing destination"u8.ToArray(); File.WriteAllBytes(output, sentinel);
            foreach (var supplied in new string?[] { null, "wrong" })
            {
                var rejected = false; try { Knxproj.Save(edited, output, supplied); } catch (InvalidDataException e) { rejected = Knxproj.IsPasswordError(e); }
                check(rejected && File.ReadAllBytes(output).SequenceEqual(sentinel), layout + " /20 AE" + version + " missing/wrong Save password leaves target intact");
            }
            Knxproj.Save(edited, output, password);
            var reloaded = Knxproj.Load(output, password); var after = ProjectQuery.DeviceList(reloaded).Single();
            check(Knxproj.IsPasswordProtected(reloaded) && reloaded.Name == "Protected edited20" && after.ParameterValues["R-1"] == "31" && after.ComObjectRefs.Single().Number == 72 && after.ComObjectRefs.Single().ChannelId == "C-1" && after.BinaryData["Synthetic"].SequenceEqual(new byte[] { 0, 255, 31 }), layout + " /20 AE" + version + " native values and sidecar reopen protected");
            var oldProjectZip = layout == "nested" ? Native20Fixture.ReadZip(sourceBytes)[Native20Fixture.ProjectId + ".zip"] : sourceBytes;
            var outputBytes = File.ReadAllBytes(output);
            var newProjectZip = layout == "nested" ? Native20Fixture.ReadZip(outputBytes)[Native20Fixture.ProjectId + ".zip"] : outputBytes;
            var entries = EncryptedArchive.ReadAll(newProjectZip, password);
            using var nativeInput = new MemoryStream(entries[prefix + "0.xml"], false);
            var native = XDocument.Load(nativeInput);
            check(native.Root!.Name.Namespace == ns && native.Descendants(ns + "DeviceInstance").Single().Parent!.Name == ns + "Line" && !native.Descendants(ns + "Segment").Any() && entries.ContainsKey(prefix + "BusChord/project.xml"), layout + " /20 AE" + version + " retains native placement and sidecar layout");
            check(LocalRecord(oldProjectZip, prefix + "opaque.bin").SequenceEqual(LocalRecord(newProjectZip, prefix + "opaque.bin")) && entries[prefix + "opaque.bin"].SequenceEqual(parts[prefix + "opaque.bin"]), layout + " /20 AE" + version + " unchanged encrypted local entry stays byte-exact");
            check(!LocalRecord(oldProjectZip, prefix + "0.xml").SequenceEqual(LocalRecord(newProjectZip, prefix + "0.xml")) && File.ReadAllBytes(path).SequenceEqual(sourceBytes), layout + " /20 AE" + version + " changed entry is rewritten while Save As source is unchanged");
        }
    }

    private static byte[] LocalRecord(byte[] zip, string name)
    {
        var target = Encoding.UTF8.GetBytes(name);
        for (var at = 0; at + 46 < zip.Length; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at, 4)) != 0x02014b50
                || BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 28, 2)) != target.Length
                || !zip.AsSpan(at + 46, target.Length).SequenceEqual(target)) continue;
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at + 42, 4));
            var length = 30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 26, 2))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 28, 2)) + (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset + 18, 4));
            return zip.AsSpan(offset, length).ToArray();
        }
        throw new InvalidDataException("Synthetic ZIP entry not found.");
    }
}
