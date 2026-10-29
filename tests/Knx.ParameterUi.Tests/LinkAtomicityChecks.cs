using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;
using Knx.Workbench;

internal static class LinkAtomicityChecks
{
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Link transaction: " + name); if (!ok) failures++; }
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "atomic-empty"))));
        var project = Fixture(); vm.CreateProject(project, null);
        var baselineDirty = vm.IsDirty;
        var baseline = Knxproj.ToXml(project).ToString();
        var objects = ProjectQuery.DeviceList(project).Single().ComObjectRefs;
        var lists = objects.Select(o => o.Links).ToArray(); var values = objects.Select(o => o.Links.ToArray()).ToArray();
        var notifications = 0; vm.ProjectEdited += (_, _) => notifications++;
        var requests = new[] { new LinkCheckRequest(new("D", "O1"), "GA1"), new(new("D", "O2"), "GA1"),
            new(new("D", "O1"), "GA2"), new(new("D", "O2"), "GA2") };
        var rejected = false;
        try { vm.ApplyLinkPairs("Synthetic batch transaction", requests); } catch (InvalidDataException) { rejected = true; }
        Check("host batch boundary rolls back a failed native undo snapshot", rejected && !vm.CanUndo && !vm.CanRedo && vm.IsDirty == baselineDirty
            && notifications == 0 && ReferenceEquals(vm.Project, project) && Knxproj.ToXml(project).ToString() == baseline);
        Check("rollback retains original live lists, duplicate values and ordering", objects.Select((o, n) => ReferenceEquals(o.Links, lists[n]) && o.Links.SequenceEqual(values[n])).All(ok => ok));
        var review = vm.PreviewSmartLinks();
        Check("the same legacy project can still produce a detached smart review", review.Items.Count == 1 && review.Items[0].CanApply && !vm.CanUndo);
        rejected = false;
        try { vm.ApplySmartLinks(review); } catch (InvalidDataException) { rejected = true; }
        Check("smart apply uses the same snapshot rollback boundary", rejected && !review.IsApplied && !vm.CanUndo && notifications == 0
            && Knxproj.ToXml(project).ToString() == baseline && objects.Select((o, n) => o.Links.SequenceEqual(values[n])).All(ok => ok));
        vm.UndoCommand.Execute(null);
        Check("a rejected operation leaves no ghost undo notification or replacement", notifications == 0 && ReferenceEquals(vm.Project, project));
        Check("an existing-only link request stays a no-op without snapshot or notifications", vm.ApplyLinkPairs("No-op", [new(new("D", "O1"), "GA3")]) is { Ok: true, Changed: 0 }
            && !vm.CanUndo && notifications == 0);
        var normal = SmartLinkWorkflowChecks.Fixture(false); vm.CreateProject(normal, null);
        var valid = ProjectQuery.DeviceList(normal).Select(d => new LinkCheckRequest(new(d.Id, "O1"), "G1")).ToArray();
        Check("successful host commit notifies once and records one full undo", vm.ApplyLinkPairs("Synthetic valid batch", valid) is { Ok: true, Changed: 2 }
            && notifications == 1 && vm.CanUndo && ProjectQuery.ComObjectRefs(normal).All(o => o.Ref.Links.SequenceEqual(["G1"])));
        vm.UndoCommand.Execute(null);
        Check("successful host transaction is fully undone in one step", !vm.CanUndo && ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0));
        return failures;
    }

    private static Project Fixture()
    {
        var document = XDocument.Parse("""
            <KNX xmlns="http://knx.org/xml/project/21"><Project Id="P" Name="Synthetic atomic host" Guid="11111111-2222-3333-4444-555555555555"><Installations><Installation InstallationId="I">
            <Topology><Area Id="A" Address="1"><Line Id="L" Address="1" MediumTypeRefId="MT-0"><DeviceInstance Id="D" Address="1" Name="Light" ProductRefId="SYNTHETIC">
            <ComObjectInstanceRefs><ComObjectInstanceRef RefId="O1" Text="Switch" Links="GA3 GA3"/><ComObjectInstanceRef RefId="O2" Text="Switch"/></ComObjectInstanceRefs></DeviceInstance></Line></Area></Topology>
            <GroupAddresses><GroupRanges><GroupRange Id="GR" RangeStart="0" RangeEnd="65535"><GroupAddress Id="GA1" Address="1" Name="Light switch" DatapointType="1.001"/>
            <GroupAddress Id="GA2" Address="2" Name="Two" DatapointType="1.001"/><GroupAddress Id="GA3" Address="3" Name="Existing" DatapointType="1.001"/></GroupRange></GroupRanges></GroupAddresses>
            </Installation></Installations></Project></KNX>
            """);
        var metadata = new WorkbenchMetadata();
        foreach (var id in new[] { "O1", "O2" }) metadata.Objects[new ObjectKey("D", id).ToString()] = new() { Dpt = "1.001", Bits = 1, Active = true, Flags = "CW" };
        foreach (var id in new[] { "GA1", "GA2", "GA3" }) metadata.Groups[id] = new() { Bits = 1, Secure = false };
        document.Root!.Elements().Single().Add(new XElement(XName.Get("Workbench", "urn:buschord:project:1"), new XAttribute("Version", 1), new XAttribute("Data", JsonSerializer.Serialize(metadata))));
        return Knxproj.FromXml(document);
    }
}
