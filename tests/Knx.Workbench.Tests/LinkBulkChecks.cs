using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class LinkBulkChecks
{
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Bulk linking: " + name); if (!ok) failures++; }
        var project = Fixture(100, 100, 64);
        var pairs = ProjectQuery.ComObjectRefs(project).Select((r, n) => new LinkCheckRequest(new(r.Device.Id, r.Ref.Id), "G" + (n % 64))).ToArray();
        var objects = ProjectQuery.ComObjectRefs(project).Select(r => r.Ref).ToArray();
        for (var n = 0; n < objects.Length; n += 3) objects[n].Links.Add(pairs[n].GroupId);
        var originalLinks = objects.Select(o => o.Links).ToArray();
        var metadataBefore = JsonSerializer.Serialize(project.Workbench);
        var enumerated = new CountedRequests(pairs); var watch = Stopwatch.StartNew();
        var first = WorkbenchProject.CheckLinks(project, enumerated);
        Check("10,000 objects and 64 targets are checked in input order without mutation", first.Count == 10_000
            && first.Select(r => new LinkCheckRequest(r.Key, r.GroupId)).SequenceEqual(pairs)
            && first.All(r => r.Validation.Ok) && first.Count(r => r.AlreadyLinked) == 3334
            && objects.Sum(o => o.Links.Count) == 3334 && metadataBefore == JsonSerializer.Serialize(project.Workbench));
        Check("a request source is consumed once per preview", enumerated.Enumerations == 1 && enumerated.Yields == pairs.Length);
        var allProbesCorrect = true;
        for (var probe = 0; probe < 40; probe++)
        {
            var sample = pairs.Skip(probe * 137).Take(257).ToArray();
            var result = WorkbenchProject.CheckLinks(project, sample);
            allProbesCorrect &= result.Count == sample.Length && result.All(r => r.Validation.Ok)
                && result.Select(r => new LinkCheckRequest(r.Key, r.GroupId)).SequenceEqual(sample);
        }
        Check("forty independent batches use the same exact-pair validation", allProbesCorrect);
        var readOnly = false;
        try { ((IList<LinkCheckResult>)first)[0] = first[0] with { AlreadyLinked = false }; }
        catch (NotSupportedException) { readOnly = true; }
        Check("preview results expose an immutable collection", readOnly);
        var history = new UndoStack(); history.Reset(project);
        var inactive = WorkbenchProject.Metadata(project, pairs[7].Key)!; inactive.Active = false;
        var wrongBits = WorkbenchProject.Metadata(project, pairs[8].Key)!; wrongBits.Bits = 8;
        var unknown = WorkbenchProject.Metadata(project, pairs[9].Key)!; unknown.Dpt = null;
        var range = project.Installations[0].MainRanges.Single(r => r.Id == "GR");
        range.Addresses.Add(new() { Id = "secure", Name = "Secure", Address = new(1000), DatapointType = "1.001" });
        project.Workbench.Groups["secure"] = new() { Bits = 1, Secure = true };
        var invalidPairs = pairs.Append(new(pairs[10].Key, "secure")).ToArray();
        var rejected = WorkbenchProject.AddLinkPairs(project, invalidPairs, history);
        Check("a few invalid members reject the entire 10,001-pair mutation", !rejected.Ok && rejected.Changed == 0
            && rejected.Errors.Select(e => e.Code).ToHashSet().SetEquals(new[] { LinkValidationReason.Inactive, LinkValidationReason.BitSize,
                LinkValidationReason.UnknownCapability, LinkValidationReason.SecureCapability })
            && objects.Sum(o => o.Links.Count) == 3334 && !history.CanUndo);
        Check("previous results remain detached from new capabilities and links", first[7].Validation.Ok && !first[7].AlreadyLinked
            && WorkbenchProject.CheckLinks(project, [pairs[7]])[0].Validation.Reason == LinkValidationReason.Inactive);
        inactive.Active = true; wrongBits.Bits = 1; unknown.Dpt = "1.001";
        metadataBefore = JsonSerializer.Serialize(project.Workbench);
        var mutationInput = new CountedRequests(pairs.Concat([pairs[1], pairs[2]]));
        var applied = WorkbenchProject.AddLinkPairs(project, mutationInput, history);
        Check("fresh multi-target mutation adds only missing pairs and records one undo", applied.Ok && applied.Changed == 6666
            && history.CanUndo && mutationInput.Enumerations == 1 && mutationInput.Yields == 10_002
            && objects.Select((o, n) => o.Links.SequenceEqual([pairs[n].GroupId]) && ReferenceEquals(o.Links, originalLinks[n])).All(ok => ok));
        Check("link additions preserve all connector, sending and other metadata", JsonSerializer.Serialize(project.Workbench) == metadataBefore);
        Check("repeat mutation is a no-op", WorkbenchProject.AddLinkPairs(project, pairs, history) is { Ok: true, Changed: 0 });
        var undone = history.Undo()!;
        Check("one undo restores the full large-batch link set", !history.CanUndo && ProjectQuery.ComObjectRefs(undone).Sum(r => r.Ref.Links.Count) == 3334);
        Console.WriteLine($"Bulk linking measurement: 10,000 objects, 64 targets, 40 previews and atomic mutation/undo: {watch.Elapsed.TotalMilliseconds:F1} ms (observation only; no UI budget claim).");

        var small = Fixture(1, 2, 2); var device = ProjectQuery.DeviceList(small).Single();
        var key = new ObjectKey(device.Id, device.ComObjectRefs[0].Id); var request = new LinkCheckRequest(key, "G0");
        Check("empty preview is empty while empty mutation has typed NoSelection", WorkbenchProject.CheckLinks(small, []).Count == 0
            && WorkbenchProject.AddLinkPairs(small, []).Errors.Single().Code == LinkValidationReason.NoSelection
            && WorkbenchProject.AddLinks(small, [], "G0").Errors.Single().Code == LinkValidationReason.NoSelection);
        Check("default and missing identities stay typed failures", WorkbenchProject.CheckLinks(small,
            [default, new(new("missing", "O0"), "G0"), new(key, "missing")]).All(r => r.Validation.Reason == LinkValidationReason.Missing && !r.AlreadyLinked));
        var duplicateRequests = WorkbenchProject.CheckLinks(small, [request, request]);
        Check("preview preserves duplicate requests while mutation deduplicates exact pairs", duplicateRequests.Count == 2
            && WorkbenchProject.AddLinkPairs(small, [request, request]) is { Ok: true, Changed: 1 });
        var original = device.ComObjectRefs[0]; original.Links.Add("G0");
        Check("legacy duplicate links remain unchanged like GroupLinks.Link", WorkbenchProject.AddLinkPairs(small, [request]) is { Ok: true, Changed: 0 }
            && original.Links.Count == 2);
        device.ComObjectRefs.Add(new() { Id = original.Id, ComObjectId = original.ComObjectId });
        Check("ambiguous object identities fail closed without dictionary exceptions", WorkbenchProject.CheckLinks(small, [request])[0].Validation.Reason == LinkValidationReason.Missing
            && !WorkbenchProject.AddLinkPairs(small, [request]).Ok && original.Links.Count == 2);
        device.ComObjectRefs.RemoveAt(device.ComObjectRefs.Count - 1);
        small.Installations[0].MainRanges[0].Addresses.Add(new() { Id = "G0", Name = "Duplicate", Address = new(900), DatapointType = "1.001" });
        Check("ambiguous group identities fail closed", WorkbenchProject.CheckLinks(small, [request])[0] is { AlreadyLinked: false, Validation.Reason: LinkValidationReason.Missing }
            && !WorkbenchProject.AddLinks(small, [key], "G0").Ok);
        small.Installations[0].MainRanges[0].Addresses.RemoveAt(2);
        var duplicateDevice = new DeviceInstance { Id = device.Id, ProductRefId = "SYNTHETIC", Address = new(1, 1, 9) };
        duplicateDevice.ComObjectRefs.Add(new() { Id = "other", ComObjectId = "other" }); small.Installations[0].Areas[0].Lines[0].Devices.Add(duplicateDevice);
        Check("duplicate device IDs reject even locally distinct object IDs", WorkbenchProject.CheckLinks(small, [request])[0].Validation.Reason == LinkValidationReason.Missing
            && !WorkbenchProject.AddLinkPairs(small, [request]).Ok);
        small.Installations[0].Areas[0].Lines[0].Devices.Remove(duplicateDevice);
        var beforeChange = WorkbenchProject.CheckLinks(small, [request])[0]; small.Workbench.Groups["G0"].Bits = null;
        Check("a detached successful preview cannot authorize a later incompatible mutation", beforeChange.Validation.Ok
            && WorkbenchProject.AddLinkPairs(small, [request]).Errors.Single().Code == LinkValidationReason.UnknownCapability && original.Links.Count == 2);

        var legacy = LegacyTopology(); var legacyObjects = ProjectQuery.DeviceList(legacy).Single().ComObjectRefs;
        var legacyLists = legacyObjects.Select(o => o.Links).ToArray(); var legacyValues = legacyObjects.Select(o => o.Links.ToArray()).ToArray();
        var legacyHistory = new UndoStack(); legacyHistory.Reset(legacy); var legacyXml = Knxproj.ToXml(legacy).ToString();
        var additions = new[] { new LinkCheckRequest(new("D", "O1"), "GA1"), new(new("D", "O2"), "GA1"),
            new(new("D", "O1"), "GA2"), new(new("D", "O2"), "GA2"), new(new("D", "O1"), "GA3") };
        Check("tolerated loose /21 placement initially serializes and every intended pair is valid", WorkbenchProject.CheckLinks(legacy, additions).All(c => c.Validation.Ok)
            && legacyHistory.Index == 0 && legacyHistory.Steps == 0);
        var snapshotRejected = false;
        try { WorkbenchProject.AddLinkPairs(legacy, additions.Concat([additions[0]]), legacyHistory); }
        catch (InvalidDataException ex) { snapshotRejected = ex.Message.Contains("native topology", StringComparison.Ordinal); }
        Check("serialization failure reverses interleaved appends to multiple objects", snapshotRejected
            && legacyObjects.Select((o, n) => ReferenceEquals(o.Links, legacyLists[n]) && o.Links.SequenceEqual(legacyValues[n])).All(ok => ok));
        Check("failed snapshot preserves original duplicate ordering and serializable project", legacyObjects[0].Links.SequenceEqual(["GA3", "GA3"])
            && Knxproj.ToXml(legacy).ToString() == legacyXml && !legacyHistory.CanUndo && !legacyHistory.CanRedo
            && legacyHistory.Index == 0 && legacyHistory.Steps == 0 && !legacyHistory.IsDirty);
        snapshotRejected = false;
        try { WorkbenchProject.AddLinks(legacy, [new("D", "O1"), new("D", "O2")], "GA1", legacyHistory); }
        catch (InvalidDataException) { snapshotRejected = true; }
        Check("single-target adapter has the same rollback boundary and can safely retry", snapshotRejected && Knxproj.ToXml(legacy).ToString() == legacyXml
            && legacyObjects.Select((o, n) => o.Links.SequenceEqual(legacyValues[n])).All(ok => ok) && legacyHistory.Steps == 0);
        return failures;
    }

    private static Project LegacyTopology()
    {
        var document = XDocument.Parse("""
            <KNX xmlns="http://knx.org/xml/project/21"><Project Id="P" Name="Synthetic atomic links" Guid="11111111-2222-3333-4444-555555555555"><Installations><Installation InstallationId="I">
            <Topology><Area Id="A" Address="1"><Line Id="L" Address="1" MediumTypeRefId="MT-0"><DeviceInstance Id="D" Address="1" ProductRefId="SYNTHETIC">
            <ComObjectInstanceRefs><ComObjectInstanceRef RefId="O1" Links="GA3 GA3"/><ComObjectInstanceRef RefId="O2"/></ComObjectInstanceRefs></DeviceInstance></Line></Area></Topology>
            <GroupAddresses><GroupRanges><GroupRange Id="GR" RangeStart="0" RangeEnd="65535"><GroupAddress Id="GA1" Address="1" Name="One" DatapointType="1.001"/>
            <GroupAddress Id="GA2" Address="2" Name="Two" DatapointType="1.001"/><GroupAddress Id="GA3" Address="3" Name="Existing" DatapointType="1.001"/></GroupRange></GroupRanges></GroupAddresses>
            </Installation></Installations></Project></KNX>
            """);
        var metadata = new WorkbenchMetadata();
        foreach (var id in new[] { "O1", "O2" }) metadata.Objects[new ObjectKey("D", id).ToString()] = new() { Dpt = "1.001", Bits = 1, Active = true, Flags = "CW" };
        foreach (var id in new[] { "GA1", "GA2", "GA3" }) metadata.Groups[id] = new() { Bits = 1, Secure = false };
        document.Root!.Elements().Single().Add(new XElement(XName.Get("Workbench", "urn:buschord:project:1"), new XAttribute("Version", 1), new XAttribute("Data", JsonSerializer.Serialize(metadata))));
        return Knxproj.FromXml(document);
    }

    private static Project Fixture(int devices, int objects, int groups)
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic bulk links", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].MainRanges.Clear();
        var range = new GroupRange { Id = "GR", Name = "Synthetic", RangeStart = 0, RangeEnd = 65535 }; project.Installations[0].MainRanges.Add(range);
        for (var n = 0; n < groups; n++)
        {
            range.Addresses.Add(new() { Id = "G" + n, Name = "Synthetic " + n, Address = new((ushort)(n + 1)), DatapointType = "1.001" });
            project.Workbench.Groups["G" + n] = new() { Bits = 1, Secure = false };
        }
        for (var d = 0; d < devices; d++)
        {
            var device = new DeviceInstance { Id = "D" + d, ProductRefId = "SYNTHETIC", Address = new(1, 1, (byte)(d + 1)) };
            for (var o = 0; o < objects; o++)
            {
                var obj = new ComObjectInstanceRef { Id = "O" + o, ComObjectId = "O" + o, Number = o }; device.ComObjectRefs.Add(obj);
                project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { Dpt = "1.001", Bits = 1, Active = true, Flags = "CW" };
            }
            project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
        }
        return project;
    }

    private sealed class CountedRequests(IEnumerable<LinkCheckRequest> requests) : IEnumerable<LinkCheckRequest>
    {
        internal int Enumerations { get; private set; }
        internal int Yields { get; private set; }
        public IEnumerator<LinkCheckRequest> GetEnumerator()
        { Enumerations++; foreach (var request in requests) { Yields++; yield return request; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
