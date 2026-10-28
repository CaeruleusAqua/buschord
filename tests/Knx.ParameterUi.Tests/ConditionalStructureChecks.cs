using System.IO.Compression;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;
using Knx.Workbench;

internal static class ConditionalStructureChecks
{
    private const string App = "M-0001_A-conditional";
    private static string Parameter(int module, int instance) => $"{App}_MD-{module}_M-{module}_MI-{instance}_P-1_R-1";
    private static string Object(int module, int instance) => $"MD-{module}_M-{module}_MI-{instance}_O-1_R-1";

    internal static XDocument Application()
    {
        XElement Module(int number) => XElement.Parse($"""
            <ModuleDef Id="{App}_MD-{number}"><Arguments><Argument Id="{App}_MD-{number}_A-1" Name="Base" Allocates="2" Alignment="4" /></Arguments><Static>
            <Parameters><Parameter Id="{App}_MD-{number}_P-1" Text="Module value" ParameterType="T" Value="1" BaseValue="{App}_MD-{number}_A-1" /></Parameters>
            <ParameterRefs><ParameterRef Id="{App}_MD-{number}_P-1_R-1" RefId="{App}_MD-{number}_P-1" /></ParameterRefs>
            <ComObjects><ComObject Id="{App}_MD-{number}_O-1" Number="0" BaseNumber="{App}_MD-{number}_A-1" ObjectSize="1 Bit" DatapointType="DPST-1-1" CommunicationFlag="Enabled" Text="Output" /></ComObjects>
            <ComObjectRefs><ComObjectRef Id="{App}_MD-{number}_O-1_R-1" RefId="{App}_MD-{number}_O-1" /></ComObjectRefs></Static>
            <Dynamic><ParameterRefRef RefId="{App}_MD-{number}_P-1_R-1" /><ComObjectRefRef RefId="{App}_MD-{number}_O-1_R-1" /></Dynamic></ModuleDef>
            """);
        var document = XDocument.Parse($"""
            <KNX><ApplicationProgram Id="{App}"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-mode" Text="Mode" ParameterType="T" Value="0" /><Parameter Id="P-count" Text="Count" ParameterType="T" Value="2" /><Parameter Id="P-input" Text="Input" ParameterType="T" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="R-mode" RefId="P-mode" /><ParameterRef Id="R-count" RefId="P-count" /><ParameterRef Id="R-input" RefId="P-input" /></ParameterRefs>
            <Allocators><Allocator Id="Allocator" Start="3" maxInclusive="100" /></Allocators><ModuleDefs /></Static>
            <Dynamic><ParameterRefRef RefId="R-mode" /><ParameterRefRef RefId="R-count" /><ParameterRefRef RefId="R-input" />
            <choose ParamRefId="R-mode"><when test="1"><Repeat Id="Repeat" Count="0" ParameterRefId="R-count"><Module Id="{App}_MD-1_M-1" RefId="{App}_MD-1"><NumericArg RefId="{App}_MD-1_A-1" AllocatorRefId="Allocator" /></Module></Repeat></when></choose>
            <Module Id="{App}_MD-2_M-2" RefId="{App}_MD-2"><NumericArg RefId="{App}_MD-2_A-1" AllocatorRefId="Allocator" /></Module></Dynamic></ApplicationProgram></KNX>
            """);
        document.Descendants("ModuleDefs").Single().Add(Module(1), Module(2));
        return document;
    }

    public static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var source = Application();
        var sourceXml = source.ToString();
        var on = ApplicationSnapshot.Evaluate(source, new Dictionary<string, string?> { ["R-mode"] = "1" });
        var off = ApplicationSnapshot.Evaluate(source, new Dictionary<string, string?> { ["R-mode"] = "0" });
        Check("Conditional activation allocates in document order and compacts successors", on.IsValid && off.IsValid
            && on.ModuleObjectNumbers.Values.SequenceEqual(new[] { 3, 8, 12 }) && off.ModuleObjectNumbers.Values.SequenceEqual(new[] { 3 }));
        Check("Conditional structure removes whole inactive module row sets", on.Instances.Count == 6 && off.Instances.Count == 4
            && on.ModuleInstances.Count == 3 && off.ModuleInstances.Count == 1);
        var assigned = new XDocument(source);
        assigned.Descendants("Dynamic").Last().AddFirst(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-mode"), new XAttribute("SourceParamRefRef", "R-input")));
        var computed = ApplicationSnapshot.Evaluate(assigned, new Dictionary<string, string?> { ["R-input"] = "1" });
        Check("Assignments and module activity converge to the same complete snapshot", computed.IsValid && computed.Instances.Count == 6
            && computed.Evaluation.Parameters["R-mode"].Value == "1" && computed.ModuleObjectNumbers.Values.SequenceEqual(new[] { 3, 8, 12 }));
        var invalidInactive = new XDocument(assigned);
        invalidInactive.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-mode").SetAttributeValue("Value", "1");
        invalidInactive.Descendants("Allocator").Single().SetAttributeValue("maxInclusive", "10");
        var deactivated = ApplicationSnapshot.Evaluate(invalidInactive, new Dictionary<string, string?> { ["R-input"] = "0" });
        Check("Inactive allocations cannot fail before their controlling assignment is evaluated", deactivated.IsValid
            && deactivated.ModuleObjectNumbers.Values.SequenceEqual(new[] { 3 }));
        var counted = new XDocument(source);
        counted.Descendants("Dynamic").Last().AddFirst(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-count"), new XAttribute("SourceParamRefRef", "R-input")));
        var computedCount = ApplicationSnapshot.Evaluate(counted, new Dictionary<string, string?> { ["R-mode"] = "1", ["R-input"] = "1" });
        Check("Calculated repeat counts and allocator state converge together", computedCount.IsValid
            && computedCount.ModuleInstances.Count == 2 && computedCount.ModuleObjectNumbers.Values.SequenceEqual(new[] { 3, 8 })
            && computedCount.Evaluation.Parameters["R-count"].Value == "1");
        counted.Descendants("ParameterTypes").Single().Add(XElement.Parse("<ParameterType Id='T-count'><TypeNumber Type='unsignedInt' SizeInBit='4' /></ParameterType>"));
        counted.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-count").SetAttributeValue("ParameterType", "T-count");
        var invalidCount = ApplicationSnapshot.Evaluate(counted, new Dictionary<string, string?> { ["R-mode"] = "1", ["R-input"] = "16" });
        Check("Computed counts must fit the declared parameter type before allocation", !invalidCount.IsValid
            && invalidCount.Evaluation.Unsupported.Any(s => s.Contains("Invalid structural")));
        var indirect = new XDocument(source);
        indirect.Descendants("Parameters").First().Add(XElement.Parse("<Parameter Id='P-temp' ParameterType='T' Value='0' />"));
        indirect.Descendants("ParameterRefs").First().Add(XElement.Parse("<ParameterRef Id='R-temp' RefId='P-temp' />"));
        indirect.Descendants("Static").First().Add(XElement.Parse("<ParameterCalculations><ParameterCalculation Language='JavaScript'><LParameters><ParameterRefRef RefId='R-input' AliasName='input' /></LParameters><RParameters><ParameterRefRef RefId='R-temp' AliasName='output' /></RParameters><LRTransformation>output=input;</LRTransformation><RLTransformation>input=output;</RLTransformation></ParameterCalculation></ParameterCalculations>"));
        indirect.Descendants("Dynamic").Last().AddFirst(XElement.Parse("<Assign TargetParamRefRef='R-count' SourceParamRefRef='R-temp' />"));
        var noCapability = ApplicationSnapshot.Evaluate(indirect, new Dictionary<string, string?> { ["R-mode"] = "1" });
        Check("Indirect script dependencies of repeat counts require an explicit capability", !noCapability.IsValid
            && noCapability.Evaluation.Unsupported.Any(s => s.Contains("explicit script runtime")));
        var cycle = new XDocument(source);
        cycle.Descendants("Dynamic").Last().AddFirst(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-mode"), new XAttribute("Value", "1")));
        cycle.Descendants("Dynamic").First().Add(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-mode"), new XAttribute("Value", "0")));
        var cyclic = ApplicationSnapshot.Evaluate(cycle, new Dictionary<string, string?>());
        Check("Structural/value cycles are bounded and never publish a valid partial snapshot", !cyclic.IsValid
            && cyclic.Evaluation.Unsupported.Any(s => s.Contains("converge", StringComparison.OrdinalIgnoreCase)));

        var detached = WorkbenchJson.CreateDemo();
        var detachedDevice = ProjectQuery.DeviceList(detached).First(); detachedDevice.ComObjectRefs.Clear();
        var retainedMetadata = new ObjectMetadata { Dpt = "9.001", Bits = 16, Flags = "CR", Role = "retained" };
        detached.Workbench.Objects[new ObjectKey(detachedDevice.Id, Object(1, 1)).ToString()] = retainedMetadata;
        var commit = on.PrepareObjectUpdate(detached, detachedDevice, off);
        Check("Preparing activation cannot mutate retained metadata without an object ref", detachedDevice.ComObjectRefs.Count == 0
            && retainedMetadata.Dpt == "9.001" && retainedMetadata.Bits == 16 && retainedMetadata.Flags == "CR");
        commit();
        Check("Activation commits initialized facts while retaining user metadata", detachedDevice.ComObjectRefs.Count == 3
            && retainedMetadata.Dpt == "1.001" && retainedMetadata.Bits == 1 && retainedMetadata.Flags == "C" && retainedMetadata.Role == "retained");
        var brokenObject = new XDocument(source);
        brokenObject.Descendants("ComObjectRef").First().SetAttributeValue("RefId", "missing-object-definition");
        Check("Active references to missing object declarations cannot form a valid snapshot", !ApplicationSnapshot.Evaluate(brokenObject,
            new Dictionary<string, string?> { ["R-mode"] = "1" }).IsValid);

        var project = WorkbenchJson.CreateDemo();
        var device = ProjectQuery.DeviceList(project).First();
        device.ProductRefId = App; device.Hardware2ProgramRefId = null; device.ComObjectRefs.Clear(); device.ParameterValues.Clear();
        device.ParameterValues["R-mode"] = "0"; device.ParameterValues["R-count"] = "2"; device.ParameterValues["R-input"] = "0";
        var existing = new ComObjectInstanceRef { Id = Object(2, 1), ComObjectId = "MD-2_M-2_MI-1_O-1", Number = 3, Text = "Retained" };
        existing.Links.Add(ProjectQuery.GroupAddresses(project).First().Id); device.ComObjectRefs.Add(existing);
        project.Workbench.Objects[new ObjectKey(device.Id, existing.Id).ToString()] = new() { Active = true, Bits = 1, Dpt = "1.001" };
        var path = Path.Combine(directory, "conditional-structure.knxproj");
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var output = zip.CreateEntry("M-0001/" + App + ".xml").Open()) source.Save(output);
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "conditional-empty-catalog"))));
        vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
        device = ProjectQuery.DeviceList(vm.Project!).First(d => d.Id == device.Id);
        var detachedRows = vm.LoadParametersFor(new DeviceRow(device));
        var detachedBaseline = detachedRows.Instances.ToDictionary(i => i.Id, i => i.Value);
        detachedRows.Instances.Single(i => i.Id == "R-mode").Value = "1";
        var rejected = false;
        try { vm.ApplyParameterValues(device, detachedRows.Instances, detachedRows.Types, detachedBaseline); }
        catch (InvalidOperationException) { rejected = true; }
        Check("A caller cannot apply structural controls through the fixed-row write boundary", rejected
            && device.ParameterValues["R-mode"] == "0" && device.ComObjectRefs.Count == 1 && !vm.CanUndo);
        var session = new ParameterEditingSession(vm); session.Load(new DeviceRow(device));
        ParameterItemViewModel Row(string id) => session.Rows.Single(r => r.Instance.Id == id);
        Row("R-mode").Value = "1";
        Check("An initially inactive module can become a complete editor draft", session.Rows.Length == 6 && session.CanApply
            && Row(Parameter(2, 1)).Value == "4" && session.ReviewChanges().AddedObjects == 2 && device.ParameterValues["R-mode"] == "0");
        Check("Parameter review reports supported object activation", session.Preview!.ActivatedObjects == 2
            && !session.Preview.Limitations.Contains("werden nicht ausgewertet"));
        Check("Conditional Apply commits activation and successor reallocation", session.Apply()
            && device.ComObjectRefs.Single(o => o.Id == Object(2, 1)).Number == 12 && device.ComObjectRefs.Count == 3);
        var removed = device.ComObjectRefs.Single(o => o.Id == Object(1, 2)); removed.Links.Add(existing.Links[0]);
        Row(Parameter(1, 2)).Value = "77"; Row("R-mode").Value = "0";
        Check("Conditional deactivation retains removed parameter drafts for review", session.Rows.Length == 4
            && session.ReviewChanges().Changes.Any(c => c.Id == Parameter(1, 2) && c.After == "77"));
        Check("Conditional deactivation preserves linked objects and compacts surviving numbers", session.Apply()
            && device.ParameterValues[Parameter(1, 2)] == "77" && removed.Links.Count == 1
            && !WorkbenchProject.Metadata(vm.Project!, new(device.Id, removed.Id))!.Active
            && device.ComObjectRefs.Single(o => o.Id == Object(2, 1)).Number == 3);
        Row("R-mode").Value = "1";
        Check("Conditional reactivation restores both drafts and links", Row(Parameter(1, 2)).Value == "77" && session.Apply()
            && WorkbenchProject.Metadata(vm.Project!, new(device.Id, removed.Id))!.Active && removed.Links.Count == 1);
        vm.UndoCommand.Execute(null);
        var undone = ProjectQuery.DeviceList(vm.Project!).First(d => d.Id == device.Id);
        Check("Conditional activation and allocation are restored in one undo", undone.ParameterValues["R-mode"] == "0"
            && undone.ComObjectRefs.Single(o => o.Id == Object(2, 1)).Number == 3
            && !WorkbenchProject.Metadata(vm.Project!, new(device.Id, removed.Id))!.Active);
        Check("Conditional inspection never mutates manufacturer declarations", source.ToString() == sourceXml);
        return failures;
    }
}
