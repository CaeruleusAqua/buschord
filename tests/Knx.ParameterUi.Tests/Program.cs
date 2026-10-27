using System.IO.Compression;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok) failures++;
}
var directory = Path.Combine(Path.GetTempPath(), "buschord-parameter-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "test.knxproj");
    var project = new Project { Id = "P-1", Name = "Parameter UI" };
    var installation = new Installation { Id = "I-1" };
    var area = new Area { Id = "A-1", Address = 1 };
    var line = new Line { Id = "L-1", Address = 1 };
    var device = new DeviceInstance { Id = "D-1", Name = "Actuator", Address = new(1, 1, 1),
        ProductRefId = "M-000F_A-0001-01-0000" };
    line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
    Knxproj.Save(project, path);
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
    using (var stream = archive.CreateEntry("M-000F/M-000F_A-0001-01-0000.xml").Open())
        XDocument.Parse("""
            <KNX><ApplicationProgram><Static><ParameterTypes>
            <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="100" /></ParameterType>
            <ParameterType Id="E" Name="Mode"><TypeRestriction SizeInBit="8"><Enumeration Value="0" Text="Off" /><Enumeration Value="1" Text="On" /></TypeRestriction></ParameterType>
            </ParameterTypes><Parameters>
            <Parameter Id="P-N" Name="Brightness" ParameterType="N" Value="25" />
            <Parameter Id="P-E" Name="Mode" ParameterType="E" Value="0" />
            </Parameters><ParameterRefs><ParameterRef Id="R-N" RefId="P-N" /><ParameterRef Id="R-E" RefId="P-E" /></ParameterRefs>
            </Static><Dynamic><choose /></Dynamic></ApplicationProgram></KNX>
            """).Save(stream);
    var vm = new MainWindowViewModel();
    vm.CreateProject(Knxproj.Load(path), null);
    vm.ProjectPath = path;
    var loaded = vm.LoadParametersFor(vm.Devices.Single());
    device = vm.Devices.Single().Device;
    Check("Archive parameters load by ref identity", loaded.Instances.Count == 2 && loaded.Instances[0].Id == "R-N");
    Check("Dynamic warning is exposed", vm.ParameterWarning.Length > 0);
    var rows = ParameterItemViewModel.Build(loaded.Instances, loaded.Types);
    Check("Enum labels retain separate wire values", rows[1].Choices[1].Text == "On" && rows[1].Choices[1].Value == "1");
    var baseline = loaded.Instances.ToDictionary(i => i.Id, i => i.Value);
    Check("Opening parameters creates no overrides", device.ParameterValues.Count == 0
        && !vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline) && !vm.CanUndo);
    rows[0].Value = "50"; rows[1].Value = rows[1].Choices[1].Value;
    Check("Apply persists drafts in project", vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline)
        && device.ParameterValues["R-N"] == "50" && device.ParameterValues["R-E"] == "1");
    Check("Repeated Apply has no mutation", !vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline));
    rows[0].Value = "101"; rows[1].Value = "0";
    try { vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline); Check("Invalid batch rejected", false); }
    catch (ArgumentException) { Check("Invalid batch rejected atomically", device.ParameterValues["R-N"] == "50" && device.ParameterValues["R-E"] == "1"); }
    rows[0].Value = "25";
    Check("Apply can restore initial defaults after previous Apply", vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline)
        && device.ParameterValues["R-N"] == "25");
    vm.UndoCommand.Execute(null);
    Check("Undo restores previous overrides", vm.Devices.Single().Device.ParameterValues["R-N"] == "50");
    try { vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline); Check("Stale dialog rejected", false); }
    catch (InvalidOperationException) { Check("Stale dialog rejected", true); }
    vm.RedoCommand.Execute(null);
    Knxproj.Save(vm.Project!, path);
    var reloaded = Knxproj.Load(path);
    Check("Save/reload persists overrides", ProjectQuery.DeviceList(reloaded).Single().ParameterValues["R-N"] == "25");
    var reopened = vm.LoadParametersFor(vm.Devices.Single());
    Check("Reopening editor overlays saved values", reopened.Instances.Single(i => i.Id == "R-N").Value == "25");
    using var zip = ZipFile.OpenRead(path);
    Check("Application data survives save and undo", zip.GetEntry("M-000F/M-000F_A-0001-01-0000.xml") is not null);
    var unset = new ParameterInstance { Id = "U", Name = "Unset" };
    Check("Editor does not invent a default", new ParameterItemViewModel(unset, loaded.Types[0]).Value is null && unset.Value is null);
    var booleanType = new ParameterType { Id = "B", Name = "Boolean", DataType = ParameterDataType.Boolean };
    Check("Boolean validation matches checkbox storage", ParameterValidation.TryValidate(booleanType, "0", out _)
        && ParameterValidation.TryValidate(booleanType, "1", out _) && !ParameterValidation.TryValidate(booleanType, "2", out _));

    // Embedded pane: the empty state is one short sentence, never the loader's XML diagnostics.
    var noProduct = new DeviceInstance { Id = "D-2", Name = "Without product", ProductRefId = "" };
    Check("Empty state asks for a project", ParameterEmptyState.Reason(null, device, 0) == ParameterEmptyReason.NeedsProject);
    Check("Empty state asks for a device", ParameterEmptyState.Reason(vm.Project, null, 0) == ParameterEmptyReason.NeedsDevice);
    Check("Device without product gets the short strapline",
        ParameterEmptyState.Reason(vm.Project, noProduct, 0) == ParameterEmptyReason.WithoutProduct
        && ParameterEmptyState.Text(vm.Project, noProduct, 0) == Strings.Get("UxDeviceWithoutProduct"));
    var strapline = ParameterEmptyState.Text(vm.Project, noProduct, 0) ?? "";
    Check("Empty strapline stays one sentence", strapline.Length < 80 && !strapline.Contains("XML"));
    Check("Device with parameters has no empty state", ParameterEmptyState.Text(vm.Project, device, 2) is null);
    Check("Device with product but no rows says so",
        ParameterEmptyState.Reason(vm.Project, device, 0) == ParameterEmptyReason.WithoutParameters
        && ParameterEmptyState.Text(vm.Project, device, 0) is { Length: < 80 });

    var paneType = typeof(Knx.App.Views.ParameterPane);
    Check("Pane keeps the embedding contract",
        paneType.GetConstructor(Type.EmptyTypes) is not null
        && paneType.GetConstructor([typeof(MainWindowViewModel)]) is not null
        && paneType.GetProperty("Device")?.PropertyType == typeof(DeviceInstance)
        && paneType.GetMethod("Load", [typeof(DeviceRow)])?.ReturnType == typeof(void)
        && paneType.GetMethod("Refresh", Type.EmptyTypes)?.ReturnType == typeof(void)
        && paneType.GetMethod("Apply", Type.EmptyTypes)?.ReturnType == typeof(bool));
}
finally { Directory.Delete(directory, true); }
Console.WriteLine(failures == 0 ? "PARAMETER UI CHECKS OK" : $"{failures} failures");
return failures == 0 ? 0 : 1;
