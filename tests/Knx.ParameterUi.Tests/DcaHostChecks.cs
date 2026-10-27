using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;
using Avalonia.Controls;

internal static class DcaHostChecks
{
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        const string app = "M-0004_A-5018-15-17D9";
        XDocument Definition(string id) => XDocument.Parse($"""
            <KNX><ApplicationProgram Id="{id}"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P" ParameterType="T" Value="1" /></Parameters><ParameterRefs><ParameterRef Id="R" RefId="P" /></ParameterRefs></Static>
            <Dynamic><ParameterRefRef RefId="R" /></Dynamic></ApplicationProgram></KNX>
            """);
        var project = ProjectWizard.Create(new() { Name = "Synthetic external DCA", Areas = 1, LinesPerArea = 1 });
        var device = new DeviceInstance { Id = "DCA-D1", ProductRefId = app, Hardware2ProgramRefId = app, Address = new(1, 1, 1) };
        project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
        var source = Definition(app);
        var path = Path.Combine(directory, "dca-header-only.knxproj"); Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var output = zip.CreateEntry("M-0004/" + app + ".xml").Open()) source.Save(output);
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "dca-empty-library"))));
        vm.CreateProject(Knxproj.Load(path), path);
        device = ProjectQuery.DeviceList(vm.Project!).Single();
        using var pane = new Knx.App.Views.ParameterPane(vm);
        pane.Load(new DeviceRow(device));
        Check("Externally declared DCA headers lock ordinary parameter editing without XML plug-in markers", pane.Session.IsReadOnly
            && !pane.Session.CanApply && pane.Session.Rows.Length == 1 && !pane.FindControl<ItemsControl>("ParameterList")!.IsEnabled);
        pane.Session.ClearReadOnly();
        Check("Clearing a manual reason cannot clear the manufacturer's DCA requirement", pane.Session.IsReadOnly && !pane.Session.CanApply);
        var loaded = vm.LoadParametersFor(new DeviceRow(device));
        var baseline = loaded.Instances.ToDictionary(i => i.Id, i => i.Value); loaded.Instances.Single().Value = "2";
        var writeBlocked = false;
        try { vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline); }
        catch (InvalidOperationException) { writeBlocked = true; }
        Check("The parameter write boundary also enforces the external DCA requirement", writeBlocked && device.ParameterValues.Count == 0 && !vm.CanUndo);
        var migrationBlocked = false;
        try { ProductReplacer.Preview(vm.Project!, device, "M-0001_A-target", "M-0001_A-target", source, Definition("M-0001_A-target")); }
        catch (InvalidDataException ex) { migrationBlocked = ex.Message.Contains("DCA", StringComparison.Ordinal); }
        Check("Ordinary product migration cannot bypass an external DCA header", migrationBlocked && device.ProductRefId == app);
        Check("Pure parameter evaluation remains available to a dedicated adapter", ApplicationSnapshot.Evaluate(source, new Dictionary<string, string?>()).IsValid);

        var catalogPath = Path.Combine(directory, "dca-catalog-only.knxprod");
        var catalogue = new XDocument(new XElement("KNX", new XElement("ManufacturerData", new XElement("Manufacturer",
            new XAttribute("RefId", "M-0004"), new XElement("ApplicationPrograms", new XElement(source.Descendants("ApplicationProgram").Single())),
            XElement.Parse($"""
                <Hardware><Hardware Id="M-0004_H-1"><Products><Product Id="M-0004_H-1_P-1" Text="Synthetic DCA" /></Products>
                <Hardware2Programs><Hardware2Program Id="M-0004_H-1_HP-1"><ApplicationProgramRef RefId="{app}" /></Hardware2Program></Hardware2Programs>
                </Hardware></Hardware>
                """)))));
        using (var zip = ZipFile.Open(catalogPath, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("knxprod.xml").Open()) catalogue.Save(output);
        var catalogLibrary = new CatalogLibrary(new CatalogStore(Path.Combine(directory, "dca-catalog-library")));
        catalogLibrary.Store.Import(catalogPath);
        catalogLibrary.GetAsync().GetAwaiter().GetResult();
        var catalogProject = ProjectWizard.Create(new() { Name = "Catalogue-only external DCA", Areas = 1, LinesPerArea = 1 });
        var catalogDevice = new DeviceInstance { Id = "CAT-D1", ProductRefId = "M-0004_H-1_P-1", Address = new(1, 1, 1) };
        catalogProject.Installations[0].Areas[0].Lines[0].Devices.Add(catalogDevice);
        var catalogVm = new MainWindowViewModel(catalogLibrary); catalogVm.CreateProject(catalogProject, null);
        var catalogParsed = Ksxml.Parse(source);
        var catalogBaseline = catalogParsed.Instances.ToDictionary(i => i.Id, i => i.Value);
        catalogParsed.Instances.Single().Value = "2";
        var catalogWriteBlocked = false;
        try { catalogVm.ApplyParameterValues(catalogDevice, catalogParsed.Instances, catalogParsed.Types, catalogBaseline); }
        catch (InvalidOperationException) { catalogWriteBlocked = true; }
        Check("Catalogue-only DCA requirements also block the legacy direct write overload", catalogVm.ParameterReadOnlyReason(catalogDevice) is not null
            && catalogWriteBlocked && catalogDevice.ParameterValues.Count == 0 && !catalogVm.CanUndo
            && Knxproj.LoadApplicationProgram(catalogProject, app) is null);

        var emptyLibrary = new CatalogLibrary(new CatalogStore(Path.Combine(directory, "dca-snapshot-empty-library")));
        emptyLibrary.GetAsync().GetAwaiter().GetResult();
        var evolving = ProjectWizard.Create(new() { Name = "Application snapshot replacement", Areas = 1, LinesPerArea = 1 });
        var evolvingDevice = new DeviceInstance { Id = "EV-D1", ProductRefId = app, Hardware2ProgramRefId = app, Address = new(1, 1, 1) };
        evolving.Installations[0].Areas[0].Lines[0].Devices.Add(evolvingDevice);
        var evolvingPath = Path.Combine(directory, "dca-before-application.knxproj"); Knxproj.Save(evolving, evolvingPath);
        var evolvingVm = new MainWindowViewModel(emptyLibrary); evolvingVm.CreateProject(Knxproj.Load(evolvingPath), evolvingPath);
        evolving = evolvingVm.Project!; evolvingDevice = ProjectQuery.DeviceList(evolving).Single();
        Check("A missing archived declaration has no invented DCA metadata", evolvingVm.ParameterReadOnlyReason(evolvingDevice) is null);
        // Simulate the legitimate immutable ArchiveState replacement used by a dedicated import.
        // Reflection is restricted to this synthetic test; the product has no internal-state bypass.
        var archiveProperty = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var archiveRevision = evolving.ApplicationDataRevision;
        archiveProperty.SetValue(evolving, archiveProperty.GetValue(Knxproj.Load(path)));
        var evolvedWriteBlocked = false;
        try { evolvingVm.ApplyParameterValues(evolvingDevice, catalogParsed.Instances, catalogParsed.Types, catalogBaseline); }
        catch (InvalidOperationException) { evolvedWriteBlocked = true; }
        Check("Adding an application snapshot cannot reuse an earlier unblocked archive decision", ReferenceEquals(evolvingVm.Project, evolving)
            && evolving.ApplicationDataRevision > archiveRevision && evolvingVm.ParameterReadOnlyReason(evolvingDevice) is not null && evolvedWriteBlocked
            && evolvingDevice.ParameterValues.Count == 0 && !evolvingVm.CanUndo);
        return failures;
    }
}
