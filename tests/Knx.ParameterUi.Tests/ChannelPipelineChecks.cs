using System.IO.Compression;
using System.Xml.Linq;
using Knx;
using Knx.App.Workbench;
using Knx.Workbench;

internal static class ChannelPipelineChecks
{
    private const string App = "M-0001_A-channelprofile";
    private static string Ref(int n) => App + "_O-" + n + "_R-1";
    private static string Controller => App + "_P-1_R-1";

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "CHANNEL " + name); if (!ok) failures++; }
        foreach (var schema in new[] { 21, 23 })
        {
            var application = Definition();
            var before = ApplicationSnapshot.Evaluate(application, new Dictionary<string, string?>());
            Check(schema + " channel assignments use only the selected branches", before.Evaluation.ObjectChannels[Ref(1)] == App + "_CH-1"
                && before.Evaluation.ObjectChannels[Ref(2)] == App + "_CH-1"
                && before.Evaluation.ObjectChannels[Ref(3)] is null && before.Evaluation.ObjectChannels[Ref(4)] is null);
            var path = Path.Combine(directory, "native-channel-" + schema + ".knxproj");
            var initial = Knxproj.FromXml(Fixture(schema)); Knxproj.Save(initial, path);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var stream = archive.CreateEntry("M-0001/" + App + ".xml").Open()) application.Save(stream);
            var project = Knxproj.Load(path); var device = ProjectQuery.DeviceList(project).Single();
            ObjectMetadata Metadata(int n) => project.Workbench.Objects[new ObjectKey(device.Id, "O-" + n + "_R-1").ToString()];
            Check(schema + " native RefId binds an independent instance id", device.Channels[0].RefId == "CH-1"
                && Metadata(1).ChannelId == "native-channel-a" && Metadata(2).ChannelId == "native-channel-a"
                && Metadata(1).StructuralDeclaredChannelName == "User channel A");
            Check(schema + " ambiguous and independent placements stay unassigned", Metadata(3).ChannelId is null && Metadata(4).ChannelId is null);
            var rows = WorkbenchProjection.Rows(project, new(new("device", device.Id), "Device"));
            Check(schema + " Workbench groups real objects under the native channel name", rows.Any(r => r.Kind == "channel" && r.Name == "User channel A")
                && rows.Single(r => r.Object?.Id == "O-1_R-1").ChannelId == "native-channel-a");

            var room = new SpaceNode { Id = "room-effect", Name = "Room", Kind = SpaceKind.Room };
            project.Installations[0].Locations.Roots.Add(room);
            var range = new GroupRange { Id = "GR", RangeStart = 0, RangeEnd = 65535 }; project.Installations[0].MainRanges.Add(range);
            range.Addresses.Add(new() { Id = "GA", Name = "Effect", Address = new(1), DatapointType = "DPST-1-1" });
            project.Workbench.Groups["GA"] = new() { EffectRoomId = room.Id, Bits = 1, Secure = false };
            device.ComObjectRefs.Single(o => o.Id == "O-1_R-1").Links.Add("GA");
            var related = WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Room, room.Id, Related: true));
            Check(schema + " external room scope includes the complete proven channel", related.Count == 2 && related.All(r => r.External)
                && related.Any(r => r.Object.Id == "O-2_R-1"));

            var undo = new UndoStack(); undo.Reset(project);
            var after = ApplicationSnapshot.Evaluate(application, new Dictionary<string, string?> { [Controller] = "1" });
            var commit = after.PrepareObjectUpdate(project, device, before);
            Check(schema + " channel transition preparation is detached", Metadata(2).ChannelId == "native-channel-a");
            device.ParameterValues[Controller] = "1"; commit(); undo.Push(project);
            Check(schema + " changing a branch updates the derived channel without touching links", Metadata(2).ChannelId == "native-channel-b"
                && Metadata(2).StructuralDeclaredChannelName == "Second channel"
                && device.ComObjectRefs.Single(o => o.Id == "O-1_R-1").Links.SequenceEqual(["GA"]));
            var undone = undo.Undo()!; var redone = undo.Redo()!;
            Check(schema + " undo and redo retain derived channel source", WorkbenchProject.Metadata(undone, new(device.Id, "O-2_R-1"))?.ChannelId == "native-channel-a"
                && WorkbenchProject.Metadata(redone, new(device.Id, "O-2_R-1"))?.ChannelId == "native-channel-b");
            var saved = Path.Combine(directory, "native-channel-saved-" + schema + ".knxproj"); Knxproj.Save(project, saved);
            var reopened = Knxproj.Load(saved); var reopenedDevice = ProjectQuery.DeviceList(reopened).Single();
            Check(schema + " channel instance reference and derived grouping survive save/reopen", reopenedDevice.Channels[0].RefId == "CH-1"
                && WorkbenchProject.Metadata(reopened, new(device.Id, "O-2_R-1"))?.ChannelId == "native-channel-b");

            var mover = device.ComObjectRefs.Single(o => o.Id == "O-2_R-1");
            mover.ChannelId = "native-channel-a";
            before.PrepareObjectUpdate(project, device, after)();
            after.PrepareObjectUpdate(project, device, before)();
            Check(schema + " a native pin equal to the old derived channel survives branch changes", Metadata(2).ChannelId == "native-channel-a"
                && Metadata(2).ChannelSource == ObjectChannelSource.Native);
            var pinnedPath = Path.Combine(directory, "native-channel-pinned-" + schema + ".knxproj"); Knxproj.Save(project, pinnedPath);
            var pinnedProject = Knxproj.Load(pinnedPath); var pinnedDevice = ProjectQuery.DeviceList(pinnedProject).Single();
            Check(schema + " native pins roundtrip as explicit instance references", pinnedDevice.ComObjectRefs.Single(o => o.Id == mover.Id).ChannelId == "native-channel-a"
                && WorkbenchProject.Metadata(pinnedProject, new(device.Id, mover.Id))?.ChannelSource == ObjectChannelSource.Native
                && WorkbenchProject.Metadata(pinnedProject, new(device.Id, mover.Id))?.ChannelId == "native-channel-a");
            mover.ChannelId = null; after.PrepareObjectUpdate(project, device, after)();
            Check(schema + " clearing a native pin resumes current derived placement", Metadata(2).ChannelId == "native-channel-b"
                && Metadata(2).ChannelSource == ObjectChannelSource.Derived);
            mover.ChannelId = "not-an-owned-channel"; after.PrepareObjectUpdate(project, device, after)();
            Check(schema + " unresolved native pins never fall back to a guessed dynamic channel", Metadata(2).ChannelId is null
                && mover.ChannelId == "not-an-owned-channel");
            mover.ChannelId = null; after.PrepareObjectUpdate(project, device, after)();
            mover.ChannelId = "native-channel-a";
            after.PrepareObjectUpdate(project, device, after)();
            var copy = ProjectOperations.CopyDevice(project, device, project.Installations[0].Areas[0].Lines[0]);
            Check(schema + " copying allocates unique native channel ids and remaps references", copy.Ok
                && copy.Value!.Channels.All(c => device.Channels.All(original => original.Id != c.Id))
                && copy.Value.Channels[0].RefId == "CH-1" && copy.Value.ComObjectRefs.Single(o => o.Id == mover.Id).ChannelId == copy.Value.Channels[0].Id);
            if (copy.Ok)
            {
                var copiedFile = Path.Combine(directory, "native-channel-copy-" + schema + ".knxproj"); Knxproj.Save(project, copiedFile);
                var copied = Knxproj.Load(copiedFile); var copiedDevice = ProjectQuery.DeviceList(copied).Single(d => d.Id == copy.Value!.Id);
                Check(schema + " copied pins and effective grouping survive native save", copiedDevice.ComObjectRefs.Single(o => o.Id == mover.Id).ChannelId == copiedDevice.Channels[0].Id
                    && WorkbenchProject.Metadata(copied, new(copiedDevice.Id, mover.Id))?.ChannelId == copiedDevice.Channels[0].Id);
            }
            Metadata(2).ChannelId = "explicit-group";
            before.PrepareObjectUpdate(project, device, after)();
            Check(schema + " explicit grouping overrides survive recalculation", Metadata(2).ChannelId == "explicit-group"
                && Metadata(2).StructuralDeclaredChannelId == "native-channel-a");
            Metadata(2).ChannelId = "native-channel-a";
            after.PrepareObjectUpdate(project, device, before)();
            Check(schema + " an explicit grouping equal to the previous declaration stays pinned", Metadata(2).ChannelId == "native-channel-a"
                && Metadata(2).ChannelSource == ObjectChannelSource.Manual);
            Metadata(2).ChannelId = null; after.PrepareObjectUpdate(project, device, after)();
            Check(schema + " an explicit unassigned grouping is retained", Metadata(2).ChannelId is null && Metadata(2).ChannelSource == ObjectChannelSource.Manual
                && mover.ChannelId == "native-channel-a");
            mover.ChannelId = null;
            var metadataKey = new ObjectKey(device.Id, mover.Id).ToString();
            project.Workbench.Objects[metadataKey] = System.Text.Json.JsonSerializer.Deserialize<ObjectMetadata>("{\"ChannelId\":\"native-channel-a\",\"StructuralDeclaredChannelId\":\"native-channel-a\"}")!;
            after.PrepareObjectUpdate(project, device, before)();
            Check(schema + " legacy sidecars infer the previous derived source", Metadata(2).ChannelId == "native-channel-b" && Metadata(2).ChannelSource == ObjectChannelSource.Derived);
            project.Workbench.Objects[metadataKey] = System.Text.Json.JsonSerializer.Deserialize<ObjectMetadata>("{\"ChannelSource\":1,\"ChannelId\":\"native-channel-a\",\"StructuralDeclaredChannelId\":\"native-channel-a\"}")!;
            after.PrepareObjectUpdate(project, device, before)();
            Check(schema + " explicit source tags survive JSON property reordering", Metadata(2).ChannelId == "native-channel-b" && Metadata(2).ChannelSource == ObjectChannelSource.Derived);
        }

        var document = Definition();
        var ambiguous = new DeviceInstance { Id = "D", ProductRefId = App, Address = new(1, 1, 1) };
        ambiguous.Channels.AddRange([new() { Id = "A", RefId = "CH-1", Name = "A" }, new() { Id = "B", RefId = "CH-1", Name = "B" }]);
        var plain = ProjectWizard.Create(new() { Name = "Ambiguous", Areas = 1, LinesPerArea = 1 }); plain.Installations[0].Areas[0].Lines[0].Devices.Add(ambiguous);
        var source = ApplicationSnapshot.Evaluate(document, new Dictionary<string, string?>()); source.PrepareObjectUpdate(plain, ambiguous, source)();
        Check("ambiguous native RefId matches cannot fabricate an association", WorkbenchProject.Metadata(plain, new("D", "O-1_R-1"))?.ChannelId is null);
        var noChannels = new DeviceInstance { Id = "D2", ProductRefId = App, Address = new(1, 1, 2) };
        plain.Installations[0].Areas[0].Lines[0].Devices.Add(noChannels); source.PrepareObjectUpdate(plain, noChannels, source)();
        var projected = WorkbenchProjection.Rows(plain, new(new("device", "D2"), "Derived"));
        Check("declaration-only channels have resolved names without invented native instances", noChannels.Channels.Count == 0
            && projected.Any(r => r.Kind == "channel" && r.Name == "First channel"));
        var nativeOnlyXml = Fixture(23);
        nativeOnlyXml.Descendants().First(e => e.Name.LocalName == "ComObjectInstanceRef").SetAttributeValue("ChannelId", "native-channel-a");
        var nativeOnly = Knxproj.FromXml(nativeOnlyXml); var nativeOnlyDevice = ProjectQuery.DeviceList(nativeOnly).Single();
        var nativeOnlyMetadata = WorkbenchProject.Metadata(nativeOnly, new(nativeOnlyDevice.Id, "O-1_R-1"))!;
        Check("native pins resolve without loading or executing an application", nativeOnlyMetadata.ChannelId == "native-channel-a"
            && nativeOnlyMetadata.ChannelSource == ObjectChannelSource.Native);
        var revisionBefore = ProjectRevisions.ContentHash(nativeOnly);
        nativeOnlyMetadata.ChannelId = "native-channel-a";
        Check("explicit same-value grouping changes are visible to the revision hash", ProjectRevisions.ContentHash(nativeOnly) != revisionBefore);
        revisionBefore = ProjectRevisions.ContentHash(nativeOnly); nativeOnlyDevice.ComObjectRefs[0].ChannelId = "native-channel-b";
        Check("raw native channel pins contribute independently to the revision hash", ProjectRevisions.ContentHash(nativeOnly) != revisionBefore);
        var removedPinProject = Knxproj.FromXml(nativeOnlyXml); var removedPinDevice = ProjectQuery.DeviceList(removedPinProject).Single();
        removedPinDevice.ComObjectRefs[0].ChannelId = null;
        var removedReloaded = Knxproj.FromXml(Knxproj.ToXml(removedPinProject));
        var removedMetadata = WorkbenchProject.Metadata(removedReloaded, new(removedPinDevice.Id, "O-1_R-1"));
        Check("removing a raw pin without an application cannot retain a stale native association", removedMetadata?.ChannelId is null
            && removedMetadata?.ChannelSource != ObjectChannelSource.Native);
        return failures;
    }

    private static XDocument Definition()
    {
        var document = XDocument.Parse($$"""
            <KNX><ApplicationProgram Id="{{App}}"><Static>
            <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="1"/></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="{{App}}_P-1" ParameterType="T" Value="0"/></Parameters>
            <ParameterRefs><ParameterRef Id="{{Controller}}" RefId="{{App}}_P-1"/></ParameterRefs><ComObjects/><ComObjectRefs/>
            </Static><Dynamic><Channel Id="{{App}}_CH-1" Text="First channel"><ComObjectRefRef RefId="{{Ref(1)}}"/>
            <choose ParamRefId="{{Controller}}"><when test="0"><ComObjectRefRef RefId="{{Ref(2)}}"/></when></choose>
            <ComObjectRefRef RefId="{{Ref(3)}}"/></Channel><Channel Id="{{App}}_CH-2" Text="Second channel">
            <choose ParamRefId="{{Controller}}"><when test="1"><ComObjectRefRef RefId="{{Ref(2)}}"/></when></choose>
            <ComObjectRefRef RefId="{{Ref(3)}}"/></Channel><ChannelIndependentBlock><ComObjectRefRef RefId="{{Ref(4)}}"/></ChannelIndependentBlock>
            </Dynamic></ApplicationProgram></KNX>
            """);
        for (var n = 1; n <= 4; n++)
        {
            document.Descendants("ComObjects").Single().Add(new XElement("ComObject", new XAttribute("Id", App + "_O-" + n),
                new XAttribute("Number", n), new XAttribute("ObjectSize", "1 Bit"), new XAttribute("DatapointType", "DPST-1-1"), new XAttribute("Text", "Object " + n)));
            document.Descendants("ComObjectRefs").Single().Add(new XElement("ComObjectRef", new XAttribute("Id", Ref(n)), new XAttribute("RefId", App + "_O-" + n)));
        }
        return document;
    }

    private static XDocument Fixture(int schema) => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/{{schema}}"><Project Id="P" Name="Channels" Guid="11111111-2222-3333-4444-555555555555"><Installations><Installation InstallationId="I">
        <Topology><Area Id="A" Address="1"><Line Id="L" Address="1" MediumTypeRefId="MT-0">
        {{(schema == 21 ? "<Segment Id='S' Number='0' MediumTypeRefId='MT-0'>" : "")}}
        <DeviceInstance Id="D" Address="{{(schema == 21 ? "1" : "1.1.1")}}" ProductRefId="{{App}}">
        <ComObjectInstanceRefs>{{string.Concat(Enumerable.Range(1, 4).Select(n => "<ComObjectInstanceRef RefId='O-" + n + "_R-1'/>"))}}</ComObjectInstanceRefs>
        <{{(schema == 21 ? "ChannelInstances" : "Channels")}}><ChannelInstance Id="native-channel-a" RefId="CH-1" Name="User channel A"/>
        <ChannelInstance Id="native-channel-b" RefId="CH-2"/></{{(schema == 21 ? "ChannelInstances" : "Channels")}}></DeviceInstance>
        {{(schema == 21 ? "</Segment>" : "")}}</Line></Area></Topology></Installation></Installations></Project></KNX>
        """);
}
