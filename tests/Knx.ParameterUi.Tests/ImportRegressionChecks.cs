using System.IO.Compression;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Catalog;

internal static class ImportRegressionChecks
{
    public static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var moduleXml = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber SizeInBit="8" Type="unsignedInt" /></ParameterType></ParameterTypes>
            <ModuleDefs><ModuleDef Id="APP_MD-1"><Arguments><Argument Id="APP_MD-1_A-1" /></Arguments><Static>
            <Parameters><Parameter Id="APP_MD-1_P-1" ParameterType="T" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="APP_MD-1_P-1_R-1" RefId="APP_MD-1_P-1" /></ParameterRefs>
            <ComObjects><ComObject Id="APP_MD-1_O-1" Number="2" BaseNumber="APP_MD-1_A-1" Text="Module object" ObjectSize="1 Bit" /></ComObjects>
            </Static><Dynamic><ParameterRefRef RefId="APP_MD-1_P-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><Module Id="APP_MD-1_M-1" RefId="APP_MD-1"><NumericArg RefId="APP_MD-1_A-1" Value="5" /></Module>
            <Module Id="APP_MD-1_M-2" RefId="APP_MD-1"><NumericArg RefId="APP_MD-1_A-1" Value="15" /></Module></Dynamic>
            </ApplicationProgram></KNX>
            """);
        var moduleBefore = moduleXml.ToString();
        var modules = Ksxml.Parse(moduleXml, out _, new Dictionary<string, string?> { ["APP_MD-1_M-1_MI-1_P-1_R-1"] = "7" });
        Check("Literal modules have independent parameter identities and saved values", modules.Instances.Count == 2
            && modules.Instances.SingleOrDefault(p => p.Id == "APP_MD-1_M-1_MI-1_P-1_R-1")?.Value == "7"
            && modules.Instances.SingleOrDefault(p => p.Id == "APP_MD-1_M-2_MI-1_P-1_R-1")?.Value == "0");
        Check("Module object numbers use their own numeric base arguments", modules.ComObjects.Select(o => o.Number).Order().SequenceEqual(new[] { 7, 17 }));
        Check("Module inspection never rewrites the manufacturer source", moduleXml.ToString() == moduleBefore);
        var repeatedXml = new XDocument(moduleXml);
        var repeatedModule = repeatedXml.Descendants().First(e => e.Name.LocalName == "Module");
        repeatedModule.ReplaceWith(new XElement("Repeat", new XElement(repeatedModule)));
        var repeated = Ksxml.Parse(repeatedXml, out var repeatedEvaluation);
        Check("Unsupported repeated modules never expose template instances", repeated.Instances.Count == 0
            && repeated.ComObjects.Count == 0 && repeatedEvaluation.Unsupported.Count > 0);
        Check("DPT declaration prefers the declared subtype over its main family", DptId.FromDeclaration("DPT-1 DPST-1-8") == "1.008");
        Check("Ambiguous subtypes keep only their known main family", DptId.FromDeclaration("DPST-1-4 DPST-1-5") == "1.x"
            && DptId.FromDeclaration("DPT-1 DPT-5") is null);
        var path = Path.Combine(directory, "native-import.knxproj");
        var project = Knx.Workbench.WorkbenchJson.CreateDemo();
        var device = ProjectQuery.DeviceList(project).First();
        device.ProductRefId = "M-0001_P-test";
        device.Hardware2ProgramRefId = "M-0001_H-test_HP-1";
        device.ParameterValues.Clear(); device.ParameterValues["R-mode"] = "1";
        device.ComObjectRefs.Clear();
        device.ComObjectRefs.Add(new() { Id = "O-5_R-7", ComObjectId = "O-5_R-7" });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var projectEntry = zip.GetEntry("0.xml")!;
            XDocument native;
            using (var stream = projectEntry.Open()) native = XDocument.Load(stream);
            foreach (var attribute in native.Descendants().Where(e => e.Name.LocalName == "ComObjectInstanceRef")
                         .Attributes().Where(a => a.Name.NamespaceName == "urn:buschord:project:1").ToArray()) attribute.Remove();
            projectEntry.Delete();
            using (var stream = zip.CreateEntry("0.xml").Open()) native.Save(stream);
            using (var stream = zip.CreateEntry("M-0001/Hardware.xml").Open())
                XDocument.Parse("""
                    <KNX><Hardware Id="H"><Products><Product Id="M-0001_P-test" Text="Product" /></Products>
                    <Hardware2Programs><Hardware2Program Id="M-0001_H-test_HP-1"><ApplicationProgramRef RefId="M-0001_A-test" /></Hardware2Program></Hardware2Programs>
                    </Hardware></KNX>
                    """).Save(stream);
            using (var stream = zip.CreateEntry("M-0001/M-0001_A-test.xml").Open())
                XDocument.Parse("""
                    <KNX><ApplicationProgram Id="M-0001_A-test"><Static><ParameterTypes>
                    <ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="10" /></ParameterType>
                    </ParameterTypes><Parameters><Parameter Id="P-mode" ParameterType="T" Text="Mode" Value="0" />
                    <Parameter Id="P-value" ParameterType="T" Text="Value" Value="2" /></Parameters>
                    <ParameterRefs><ParameterRef Id="R-mode" RefId="P-mode" /><ParameterRef Id="R-value" RefId="P-value" /></ParameterRefs>
                    <ComObjectTable><ComObject Id="M-0001_A-test_O-5" Number="5" Text="Switch" ObjectSize="1 Bit" DatapointType="DPST-1-1" CommunicationFlag="Enabled" WriteFlag="Enabled" /></ComObjectTable>
                    <ComObjectRefs><ComObjectRef Id="M-0001_A-test_O-5_R-7" RefId="M-0001_A-test_O-5" /></ComObjectRefs>
                    </Static><Dynamic><ParameterRefRef RefId="R-mode" /><choose ParamRefId="R-mode"><when test="1"><ParameterRefRef RefId="R-value" /></when></choose></Dynamic>
                    </ApplicationProgram></KNX>
                    """).Save(stream);
        }
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "import-empty-catalog"))));
        vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
        var imported = ProjectQuery.DeviceList(vm.Project!).First(d => d.Id == device.Id);
        void ReimportDefinition()
        {
            var saved = new Dictionary<string, string>(imported.ParameterValues);
            vm.CreateProject(Knxproj.Load(path), path);
            imported = ProjectQuery.DeviceList(vm.Project!).First(d => d.Id == device.Id);
            foreach (var (id, value) in saved) imported.ParameterValues[id] = value;
        }
        var session = new ParameterEditingSession(vm); session.Load(new DeviceRow(imported));
        Check("Saved native values select the parameter branch", session.Rows.Single(r => r.Instance.Id == "R-value").IsVisible);
        session.Rows.Single(r => r.Instance.Id == "R-mode").Value = "0";
        Check("Editing a mode re-evaluates parameter visibility", !session.Rows.Single(r => r.Instance.Id == "R-value").IsVisible);
        session.Load(null); session.Load(new DeviceRow(imported));
        Check("Dynamic drafts survive selection away and back", session.HasUnappliedChanges
            && session.Rows.Single(r => r.Instance.Id == "R-mode").Value == "0"
            && !session.Rows.Single(r => r.Instance.Id == "R-value").IsVisible);
        session.DiscardCurrentDraft();
        Check("Discard restores the saved dynamic branch", session.Rows.Single(r => r.Instance.Id == "R-value").IsVisible);
        var row = WorkbenchProjection.Rows(vm.Project!, new(new("device", imported.Id), "test")).Single(r => r.Object is not null);
        Check("Native communication object inherits number and text", row.Object!.Number == 5 && row.Name == "Switch");
        Check("Native communication object inherits DPT and size", row.Dpt is { Length: > 0 } && row.Bits == 1);
        var assignmentXml = Knxproj.LoadApplicationProgram(path, imported.Hardware2ProgramRefId!)!;
        assignmentXml.Descendants().Single(e => e.Name.LocalName == "Dynamic")
            .Add(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-value"), new XAttribute("Value", "3")));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("M-0001/M-0001_A-test.xml")!.Delete();
            using var stream = zip.CreateEntry("M-0001/M-0001_A-test.xml").Open();
            assignmentXml.Save(stream);
        }
        imported.ParameterValues["R-value"] = "4";
        Check("External application replacement cannot change the open archive snapshot", vm.LoadParametersFor(new DeviceRow(imported)).Instances.Single(p => p.Id == "R-value").Value == "4");
        ReimportDefinition();
        var assigned = vm.LoadParametersFor(new DeviceRow(imported));
        Check("Saved input does not overwrite a subsequent declared assignment", assigned.Instances.Single(p => p.Id == "R-value").Value == "3"
            && imported.ParameterValues["R-value"] == "4");
        assignmentXml.Descendants().Single(e => e.Name.LocalName == "Static").Add(XElement.Parse("""
            <ParameterCalculations><ParameterCalculation Language="JavaScript"><LParameters><ParameterRefRef RefId="R-mode" AliasName="input" /></LParameters>
            <RParameters><ParameterRefRef RefId="R-value" AliasName="output" /></RParameters><LRTransformation>output = input === 1 ? 1000 : 5;</LRTransformation><RLTransformation>input=output;</RLTransformation></ParameterCalculation></ParameterCalculations>
            """));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("M-0001/M-0001_A-test.xml")!.Delete();
            using var stream = zip.CreateEntry("M-0001/M-0001_A-test.xml").Open(); assignmentXml.Save(stream);
        }
        ReimportDefinition();
        session.Load(new DeviceRow(imported));
        Check("Failed calculations block preview and Apply without changing saved input", !session.CanApply
            && !session.ReviewChanges().IsValid && !session.Apply() && imported.ParameterValues["R-value"] == "4");
        session.Rows.Single(r => r.Instance.Id == "R-mode").Value = "0";
        Check("Valid corrected input can apply computed values even when its branch becomes hidden", session.CanApply
            && session.ReviewChanges().IsValid && session.Apply() && imported.ParameterValues["R-value"] == "5");
        return failures;
    }
}
