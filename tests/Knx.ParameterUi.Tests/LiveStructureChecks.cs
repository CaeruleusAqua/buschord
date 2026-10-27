using System.IO.Compression;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;
using Knx.Workbench;

internal static class LiveStructureChecks
{
    public static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        const string programId = "M-0001_A-live";
        static string ParameterId(int index) => $"{programId}_MD-1_M-1_MI-{index}_P-1_R-1";
        static string ObjectId(int index) => $"MD-1_M-1_MI-{index}_O-1_R-1";
        var application = XDocument.Parse("""
            <KNX><ApplicationProgram Id="M-0001_A-live"><Static><ParameterTypes>
            <ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="10" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-count" Text="Count" ParameterType="T" Value="2" /></Parameters>
            <ParameterRefs><ParameterRef Id="R-count" RefId="P-count" /><ParameterRef Id="R-alias" RefId="P-count" /></ParameterRefs>
            <Allocators><Allocator Id="A" Start="3" maxInclusive="20" /></Allocators>
            <ModuleDefs><ModuleDef Id="M-0001_A-live_MD-1"><Arguments><Argument Id="M-0001_A-live_MD-1_A-1" Name="Base" Allocates="2" Alignment="4" /></Arguments><Static>
            <Parameters><Parameter Id="M-0001_A-live_MD-1_P-1" Text="Value" ParameterType="T" Value="1" /></Parameters>
            <ParameterRefs><ParameterRef Id="M-0001_A-live_MD-1_P-1_R-1" RefId="M-0001_A-live_MD-1_P-1" /></ParameterRefs>
            <ComObjects><ComObject Id="M-0001_A-live_MD-1_O-1" Number="0" BaseNumber="M-0001_A-live_MD-1_A-1" ObjectSize="1 Bit" Text="Output" DatapointType="DPST-1-1" CommunicationFlag="Enabled" /></ComObjects>
            <ComObjectRefs><ComObjectRef Id="M-0001_A-live_MD-1_O-1_R-1" RefId="M-0001_A-live_MD-1_O-1" /></ComObjectRefs></Static>
            <Dynamic><ParameterBlock Id="M-0001_A-live_MD-1_PB-1" Text="Channel"><ParameterRefRef RefId="M-0001_A-live_MD-1_P-1_R-1" /><ComObjectRefRef RefId="M-0001_A-live_MD-1_O-1_R-1" /></ParameterBlock></Dynamic>
            </ModuleDef></ModuleDefs></Static><Dynamic><ParameterRefRef RefId="R-count" /><ParameterRefRef RefId="R-alias" />
            <Repeat Id="Repeat" Count="0" ParameterRefId="R-count"><Module Id="M-0001_A-live_MD-1_M-1" RefId="M-0001_A-live_MD-1">
            <NumericArg RefId="M-0001_A-live_MD-1_A-1" AllocatorRefId="A" /></Module></Repeat></Dynamic></ApplicationProgram></KNX>
            """);
        var path = Path.Combine(directory, "live-structure.knxproj");
        var project = WorkbenchJson.CreateDemo();
        var devices = ProjectQuery.DeviceList(project).Take(2).ToArray();
        foreach (var d in devices)
        {
            d.ProductRefId = programId; d.Hardware2ProgramRefId = null;
            d.ParameterValues.Clear(); d.ParameterValues["R-count"] = "2"; d.ParameterValues[ParameterId(2)] = "7";
            d.ComObjectRefs.Clear();
            var obj = new ComObjectInstanceRef { Id = ObjectId(2), ComObjectId = "MD-1_M-1_MI-2_O-1", Number = 8, Text = "Explicit override" };
            obj.Links.Add(ProjectQuery.GroupAddresses(project).First().Id); d.ComObjectRefs.Add(obj);
            project.Workbench.Objects[new ObjectKey(d.Id, obj.Id).ToString()] = new() { Dpt = "1.001", Bits = 1, Active = true };
        }
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var stream = zip.CreateEntry("M-0001/M-0001_A-live.xml").Open()) application.Save(stream);
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "live-empty-catalog"))));
        vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
        var device = vm.Devices.Single(d => d.Device.Id == devices[0].Id).Device;
        var secondDevice = vm.Devices.Single(d => d.Device.Id == devices[1].Id).Device;
        var session = new ParameterEditingSession(vm); session.Load(new DeviceRow(device));
        ParameterItemViewModel Row(string id) => session.Rows.Single(r => r.Instance.Id == id);
        void Count(string count) => Row("R-count").Value = count;
        var originalXml = Knxproj.ToXml(vm.Project!).ToString();
        Check("Live structural editor enables only the canonical count control", Row("R-count").IsEnabled && !Row("R-alias").IsEnabled);
        var survivor = Row(ParameterId(2)); survivor.Value = "9";
        session.SelectSection("Channel"); Count("3");
        Check("Count growth publishes a complete row set and retains surviving editor identities", session.Rows.Length == 5
            && ReferenceEquals(survivor, Row(ParameterId(2))) && survivor.Value == "9" && session.SelectedSection == "Channel");
        var preview = session.ReviewChanges();
        Check("Review reports object-instance additions without project mutations", preview.IsValid && preview.AddedObjects == 1
            && preview.RemovedObjects == 0 && originalXml == Knxproj.ToXml(vm.Project!).ToString());
        Count("1");
        Check("Shrinking retains removed drafts for review", session.Rows.Length == 3
            && session.ReviewChanges().Changes.Any(c => c.Id == ParameterId(2) && c.After == "9"));
        session.Load(new DeviceRow(secondDevice)); Count("3");
        session.Load(new DeviceRow(device));
        Check("Structural drafts survive switching between independently edited devices", Row("R-count").Value == "1"
            && session.Rows.Length == 3 && session.ReviewChanges().Changes.Any(c => c.Id == ParameterId(2) && c.After == "9"));
        Check("Apply commits the same reviewed structure and retained parameter drafts", session.Apply()
            && device.ParameterValues["R-count"] == "1" && device.ParameterValues[ParameterId(2)] == "9");
        var retained = device.ComObjectRefs.Single(o => o.Id == ObjectId(2));
        Check("Absent objects retain links and explicit overrides while marked inactive", retained.Links.Count == 1
            && retained.Text == "Explicit override" && !WorkbenchProject.Metadata(vm.Project!, new(device.Id, retained.Id))!.Active);
        Check("Other device's structural draft remains unapplied", secondDevice.ParameterValues["R-count"] == "2" && session.HasUnappliedChanges);
        vm.UndoCommand.Execute(null);
        device = vm.Devices.Single(d => d.Device.Id == devices[0].Id).Device;
        Check("Undo restores the saved count, values, links and object activity together", device.ParameterValues["R-count"] == "2"
            && device.ParameterValues[ParameterId(2)] == "7" && WorkbenchProject.Metadata(vm.Project!, new(device.Id, ObjectId(2)))!.Active
            && device.ComObjectRefs.Single(o => o.Id == ObjectId(2)).Links.Count == 1);
        vm.RedoCommand.Execute(null);
        device = vm.Devices.Single(d => d.Device.Id == devices[0].Id).Device;
        Check("Redo restores the reduced structure and inactive linked objects", device.ParameterValues["R-count"] == "1"
            && !WorkbenchProject.Metadata(vm.Project!, new(device.Id, ObjectId(2)))!.Active);
        session.Load(new DeviceRow(device)); Count("3");
        Check("Regrowth restores retained values before Apply", Row(ParameterId(2)).Value == "9" && session.Rows.Length == 5);
        Check("Regrowth Apply restores links and allocates new object instances", session.Apply()
            && WorkbenchProject.Metadata(vm.Project!, new(device.Id, ObjectId(2)))!.Active
            && device.ComObjectRefs.Single(o => o.Id == ObjectId(2)).Text == "Explicit override"
            && device.ComObjectRefs.Single(o => o.Id == ObjectId(3)).Number == 12);
        var beforeFailure = Knxproj.ToXml(vm.Project!).ToString();
        var stableRows = session.Rows;
        Count("8");
        Check("Allocator failure retains the last complete structure and blocks review and Apply", ReferenceEquals(stableRows, session.Rows)
            && session.Rows.Length == 5 && !session.CanApply && !session.ReviewChanges().IsValid && !session.Apply()
            && beforeFailure == Knxproj.ToXml(vm.Project!).ToString());
        session.Load(null); session.Load(new DeviceRow(device));
        Check("Navigation cannot clear a failed structural draft's Apply guard", Row("R-count").Value == "8"
            && !session.CanApply && !session.Apply() && session.Rows.Length == 5);
        Count("3");
        Check("Corrected count recovers without losing saved module values", session.CanApply && Row(ParameterId(2)).Value == "9");
        Count("0");
        Check("A zero count keeps only root controls and can be discarded", session.Rows.Length == 2 && session.CanApply);
        session.DiscardCurrentDraft();
        Check("Discard restores the saved structure and clears the current draft", Row("R-count").Value == "3" && session.Rows.Length == 5);
        Row(ParameterId(2)).Value = "invalid"; Count("1");
        Check("Invalid removed drafts cannot escape validation through shrinking", !session.ReviewChanges().IsValid && !session.Apply());
        session.DiscardCurrentDraft(); Count("1"); Check("Reduced structure can be saved", session.Apply());
        Knxproj.Save(vm.Project!, path);
        var reloaded = Knxproj.Load(path);
        var reloadedDevice = ProjectQuery.DeviceList(reloaded).Single(d => d.Id == device.Id);
        Check("Archive roundtrip preserves dormant links, values, overrides and activity", reloadedDevice.ParameterValues[ParameterId(2)] == "9"
            && reloadedDevice.ComObjectRefs.Single(o => o.Id == ObjectId(2)).Links.Count == 1
            && reloadedDevice.ComObjectRefs.Single(o => o.Id == ObjectId(2)).Text == "Explicit override"
            && !WorkbenchProject.Metadata(reloaded, new(device.Id, ObjectId(2)))!.Active);
        // Two independently loaded editors must not commit against different saved generations.
        session.Load(new DeviceRow(device)); Count("2"); Check("Prepare concurrent editor generation", session.Apply());
        var concurrent = new ParameterEditingSession(vm); concurrent.Load(new DeviceRow(device));
        Count("3"); Check("First concurrent editor can grow structure", session.Apply());
        concurrent.Rows.Single(r => r.Instance.Id == "R-count").Value = "1";
        var concurrentBaseline = Knxproj.ToXml(vm.Project!).ToString();
        Check("Stale concurrent editor cannot commit an inconsistent object structure", !concurrent.Apply()
            && concurrentBaseline == Knxproj.ToXml(vm.Project!).ToString() && device.ParameterValues["R-count"] == "3");
        session.Load(new DeviceRow(device));
        var explicitNumber = device.ComObjectRefs.Single(o => o.Id == ObjectId(2)); explicitNumber.Number = 200;
        Row(ParameterId(1)).Value = "2";
        Check("Scalar Apply preserves an explicit module-object number override", session.Apply() && explicitNumber.Number == 200);
        Count("1"); Check("Override object can be retained dormant", session.Apply());
        Count("3"); Check("Regrowth also preserves explicit module-object number overrides", session.Apply() && explicitNumber.Number == 200);
        var conditional = new XDocument(application);
        conditional.Descendants("Dynamic").First().Add(new XElement("choose", new XAttribute("ParamRefId", "R-count"),
            new XElement("when", new XAttribute("test", "0"), new XElement("ComObjectRefRef", new XAttribute("RefId", "M-0001_A-live_MD-1_O-1_R-1")))));
        var unsupported = ApplicationSnapshot.Evaluate(conditional, new Dictionary<string, string?> { ["R-count"] = "1" });
        Check("Conditional and unconditional placements share one object activity state", unsupported.IsValid
            && unsupported.Instances.Single(i => i.Id == "R-count").IsEnabled && unsupported.ActiveObjects.Count == 1);
        var unplaced = new XDocument(application); unplaced.Descendants("ComObjectRefRef").Remove();
        Check("Unplaced module objects do not become implicitly active", !ApplicationSnapshot.Evaluate(unplaced,
            new Dictionary<string, string?> { ["R-count"] = "1" }).SupportsLiveStructure);
        var unknown = new XDocument(application); var objectPlacement = unknown.Descendants("ComObjectRefRef").Single();
        objectPlacement.ReplaceWith(new XElement("UnknownActivation", new XElement(objectPlacement)));
        Check("Unknown object activation wrappers keep live structures unsupported", !ApplicationSnapshot.Evaluate(unknown,
            new Dictionary<string, string?> { ["R-count"] = "1" }).SupportsLiveStructure);
        var duplicate = new XDocument(application);
        duplicate.Descendants("ComObjects").Single().Add(new XElement(duplicate.Descendants("ComObject").Single()));
        Check("Zero-count declaration can remain inspectable before an invalid expansion", ApplicationSnapshot.Evaluate(duplicate,
            new Dictionary<string, string?> { ["R-count"] = "0" }).IsValid);
        try
        {
            var rejected = ApplicationSnapshot.Evaluate(duplicate, new Dictionary<string, string?> { ["R-count"] = "1" });
            Check("Ambiguous object expansion must be rejected", !rejected.IsValid);
        }
        catch (InvalidDataException) { Check("Ambiguous object expansion is a controlled validation failure", true); }
        device.ParameterValues["R-count"] = "8";
        var invalidLoaded = new ParameterEditingSession(vm); invalidLoaded.Load(new DeviceRow(device));
        Check("Invalid saved structures lock their count and aliases", !invalidLoaded.CanApply
            && !invalidLoaded.Rows.Single(r => r.Instance.Id == "R-count").IsEnabled
            && !invalidLoaded.Rows.Single(r => r.Instance.Id == "R-alias").IsEnabled);
        invalidLoaded.Rows.Single(r => r.Instance.Id == "R-count").Value = "3";
        Check("Direct edits cannot release a failed saved structure through the fixed-row path", !invalidLoaded.ReviewChanges().IsValid
            && !invalidLoaded.Apply() && device.ParameterValues["R-count"] == "8");
        device.ParameterValues["R-count"] = "3";
        Check("Manufacturer declarations remain unchanged after live editing", application.Descendants("Repeat").Single().Attribute("Count")!.Value == "0"
            && application.Descendants("NumericArg").Single().Attribute("AllocatorRefId")!.Value == "A");
        return failures;
    }
}
