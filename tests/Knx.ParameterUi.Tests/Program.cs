using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Knx;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;

if (args.Contains("--native")) return NativeDraftChecks.Run();

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
    line.Devices.Add(device);
    line.Devices.Add(new DeviceInstance { Id = "D-2", Name = "Device 2", Address = new(1, 1, 2), ProductRefId = device.ProductRefId });
    area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
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
    var pane = new ParameterPane(vm);
    ParameterItemViewModel[] PaneRows() => ((IEnumerable<ParameterItemViewModel>)pane.FindControl<ItemsControl>("ParameterList")!.ItemsSource!).ToArray();
    pane.Load(vm.Devices[0]);
    PaneRows()[0].Value = "44"; PaneRows()[0].Value = "25";
    Check("Edit back to baseline is clean", !pane.HasUnappliedChanges);
    pane.Load(vm.Devices[1]); pane.Load(vm.Devices[0]);
    Check("No-op draft is not cached", !pane.HasUnappliedChanges && PaneRows()[0].Value == "25");
    PaneRows()[0].Value = "45";
    var removed = vm.Devices[0];
    var modelLine = vm.Project!.Installations[0].Areas[0].Lines[0];
    pane.Load(vm.Devices[1]); modelLine.Devices.Remove(removed.Device);
    Check("Dirty API prunes a deleted hidden device without refresh", !pane.HasUnappliedChanges);
    pane.Refresh();
    Check("Refresh prunes a deleted hidden device", !pane.HasUnappliedChanges);
    pane.Load(removed);
    Check("Deleted selection cannot resurrect a draft", pane.Device is null && !pane.HasUnappliedChanges);
    modelLine.Devices.Add(removed.Device);
    pane.Load(removed); PaneRows()[0].Value = "45"; modelLine.Devices.Remove(removed.Device);
    Check("Dirty API ignores a deleted visible device", !pane.HasUnappliedChanges);
    pane.Refresh();
    Check("Refresh clears deleted visible editor", pane.Device is null && !pane.HasUnappliedChanges);
    modelLine.Devices.Insert(0, removed.Device); pane.DiscardDrafts();
    pane.Load(vm.Devices[1]);
    PaneRows()[0].Value = "41";
    pane.Load(vm.Devices[0]);
    pane.Load(vm.Devices[1]);
    Check("Device 2 draft 25 -> 41 survives selection away and back", PaneRows()[0].Value == "41");
    Check("Selection does not apply drafts", vm.Devices[1].Device.ParameterValues.Count == 0 && !vm.IsDirty);
    Check("Dirty API includes the visible draft", pane.HasUnappliedChanges);
    pane.Load(null);
    Check("Dirty API includes drafts while no device is selected", pane.HasUnappliedChanges && pane.Device is null);
    pane.Load(vm.Devices[1]);
    pane.Refresh();
    Check("Same-device refresh retains compatible drafts", PaneRows()[0].Value == "41" && pane.HasUnappliedChanges);
    vm.SaveProject();
    pane.Refresh();
    Check("Ordinary Save never persists or clears unapplied drafts", PaneRows()[0].Value == "41"
        && pane.HasUnappliedChanges && ProjectQuery.DeviceList(Knxproj.Load(path)).Last().ParameterValues.Count == 0);
    PaneRows()[0].Value = "101";
    Check("Invalid pane Apply retains the draft", !pane.Apply() && pane.HasUnappliedChanges);
    var error = pane.FindControl<TextBlock>("MessageText")!.Text;
    pane.Load(vm.Devices[0]); pane.Load(vm.Devices[1]);
    Check("Validation failure survives selection switches", PaneRows()[0].Value == "101"
        && pane.FindControl<TextBlock>("MessageText")!.Text == error && !string.IsNullOrEmpty(error));
    PaneRows()[0].Value = "41";
    Check("Explicit pane Apply persists and resets dirty state", pane.Apply() && !pane.HasUnappliedChanges
        && vm.Devices[1].Device.ParameterValues["R-N"] == "41");
    pane.Load(vm.Devices[0]); pane.Load(vm.Devices[1]);
    Check("Applied values do not resurrect an old draft", PaneRows()[0].Value == "41" && !pane.HasUnappliedChanges);
    PaneRows()[0].Value = "42"; pane.Load(vm.Devices[0]); PaneRows()[0].Value = "43";
    pane.DiscardDrafts();
    Check("DiscardDrafts resets current editor and every stored draft", !pane.HasUnappliedChanges && PaneRows()[0].Value == "25");
    pane.Load(vm.Devices[1]);
    Check("Discarded hidden-device draft cannot return", PaneRows()[0].Value == "41" && !pane.HasUnappliedChanges);
    PaneRows()[0].Value = "44";
    vm.UndoCommand.Execute(null);
    pane.Load(vm.Devices[1]);
    Check("Undo snapshot invalidates prior drafts", PaneRows()[0].Value == "25" && !pane.HasUnappliedChanges);
    PaneRows()[0].Value = "45";
    vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
    pane.Load(vm.Devices[1]);
    Check("Different project instance with same IDs cannot inherit drafts", PaneRows()[0].Value == "25" && !pane.HasUnappliedChanges);
    PaneRows()[0].Value = "46";
    vm.Devices[1].Device.ParameterValues["R-N"] = "47";
    pane.Refresh();
    Check("Changed model baseline invalidates a draft with notice", PaneRows()[0].Value == "47" && !pane.HasUnappliedChanges
        && pane.FindControl<TextBlock>("MessageText")!.Text == ParameterEmptyState.DiscardedText);
    PaneRows()[0].Value = "48";
    pane.Load(vm.Devices[0]);
    const string applicationEntry = "M-000F/M-000F_A-0001-01-0000.xml";
    XDocument definition;
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
    {
        var entry = archive.GetEntry(applicationEntry)!;
        using (var stream = entry.Open()) definition = XDocument.Load(stream);
        entry.Delete();
        definition.Descendants("TypeNumber").Single().SetAttributeValue("maxInclusive", "40");
        definition.Descendants("Parameter").First().SetAttributeValue("IsVisible", "false");
        using var output = archive.CreateEntry(applicationEntry).Open();
        definition.Save(output);
    }
    pane.Load(vm.Devices[1]);
    Check("Refreshed application definition cannot restore an obsolete hidden draft", PaneRows()[0].Value == "47"
        && !PaneRows()[0].IsVisible && !pane.HasUnappliedChanges
        && pane.FindControl<TextBlock>("MessageText")!.Text == ParameterEmptyState.DiscardedText);
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
    {
        archive.GetEntry(applicationEntry)!.Delete();
        definition.Descendants("TypeNumber").Single().SetAttributeValue("maxInclusive", "100");
        definition.Descendants("Parameter").First().Attribute("IsVisible")!.Remove();
        using var output = archive.CreateEntry(applicationEntry).Open();
        definition.Save(output);
    }
    // Start the existing apply/undo checks from the original saved fixture.
    vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
    var loaded = vm.LoadParametersFor(vm.Devices[0]);
    device = vm.Devices[0].Device;
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
    Check("Undo restores previous overrides", vm.Devices[0].Device.ParameterValues["R-N"] == "50");
    try { vm.ApplyParameterValues(device, loaded.Instances, loaded.Types, baseline); Check("Stale dialog rejected", false); }
    catch (InvalidOperationException) { Check("Stale dialog rejected", true); }
    vm.RedoCommand.Execute(null);
    Knxproj.Save(vm.Project!, path);
    var reloaded = Knxproj.Load(path);
    Check("Save/reload persists overrides", ProjectQuery.DeviceList(reloaded).First().ParameterValues["R-N"] == "25");
    var reopened = vm.LoadParametersFor(vm.Devices[0]);
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

    // Catalogue fallback: a cold library must never be parsed on the caller's (UI) thread.
    var libraryRoot = Path.Combine(directory, "catalog");
    var library = new CatalogLibrary(new CatalogStore(Path.Combine(libraryRoot, "library")));
    var catalogProject = new Project { Id = "P-C", Name = "Catalogue" };
    var catalogInstallation = new Installation { Id = "I-C" };
    var catalogArea = new Area { Id = "A-C", Address = 1 };
    var catalogLine = new Line { Id = "L-C", Address = 1 };
    var catalogDevice = new DeviceInstance
    {
        Id = "1.1.1", Name = "Catalog actuator", Address = new(1, 1, 1), ProductRefId = "M-UI_H-1_P-1",
    };
    catalogLine.Devices.Add(catalogDevice);
    catalogLine.Devices.Add(new DeviceInstance { Id = "C2", Name = "Catalogue B", Address = new(1, 1, 2), ProductRefId = catalogDevice.ProductRefId });
    catalogArea.Lines.Add(catalogLine);
    catalogInstallation.Areas.Add(catalogArea);
    catalogProject.Installations.Add(catalogInstallation);

    Directory.CreateDirectory(libraryRoot);
    var fixture = Path.Combine(libraryRoot, "fixture.knxprod");
    using (var zipFile = ZipFile.Open(fixture, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zipFile.CreateEntry("knxprod.xml").Open()))
        writer.Write("""
            <KNX><ManufacturerData><Manufacturer RefId="M-UI">
            <ApplicationPrograms><ApplicationProgram Id="M-UI_A-1" ApplicationNumber="1" ApplicationVersion="1">
            <Static><ParameterTypes>
            <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255" /></ParameterType>
            </ParameterTypes><Parameters><Parameter Id="P-N" Name="Brightness" ParameterType="N" Value="42" /></Parameters>
            <ParameterRefs><ParameterRef Id="R-N" RefId="P-N" /></ParameterRefs></Static>
            <Dynamic><choose /></Dynamic></ApplicationProgram></ApplicationPrograms>
            <Hardware><Hardware Id="M-UI_H-1"><Products><Product Id="M-UI_H-1_P-1" Text="Fixture" /></Products>
            <Hardware2Programs><Hardware2Program Id="M-UI_H-1_H2P-1">
            <ApplicationProgramRef RefId="M-UI_A-1" /></Hardware2Program></Hardware2Programs>
            </Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """);

    var clock = System.Diagnostics.Stopwatch.StartNew();
    library.Store.Import(fixture);
    var coldVm = new MainWindowViewModel(library);
    coldVm.CreateProject(catalogProject, null);
    var loadedSignal = new TaskCompletionSource();
    coldVm.CatalogLoaded += (_, _) => loadedSignal.TrySetResult();
    var cold = coldVm.LoadParametersFor(coldVm.Devices[0]);
    clock.Stop();
    Check("Cold catalogue lookup does not parse the library on the UI thread",
        cold.Instances.Count == 0 && clock.ElapsedMilliseconds < 1000);
    Check("Cold catalogue lookup says it is loading", coldVm.IsAwaitingCatalog
        && coldVm.Status == Strings.Get("CatalogLoading"));

    var published = await library.GetAsync();
    await Task.WhenAny(loadedSignal.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    clock.Restart();
    var warm = coldVm.LoadParametersFor(coldVm.Devices[0]);
    clock.Stop();
    Check("Warm catalogue lookup resolves from the shared snapshot",
        published.Catalogs.Count == 1 && warm.Instances.Single().Id == "R-N" && warm.Instances[0].Value == "42");
    Check("Warm catalogue lookup stays fast", clock.ElapsedMilliseconds < 1000);
    Check("Warm catalogue lookup clears the waiting flag", !coldVm.IsAwaitingCatalog);
    Check("Catalogue completion notifies the pane once", loadedSignal.Task.IsCompleted);

    var catalogPane = new ParameterPane(coldVm);
    ParameterItemViewModel CatalogRow() => ((IEnumerable<ParameterItemViewModel>)catalogPane.FindControl<ItemsControl>("ParameterList")!.ItemsSource!).Single();
    catalogPane.Load(coldVm.Devices[0]); CatalogRow().Value = "51"; catalogPane.Refresh();
    Check("Catalogue refresh retains dirty ownership", CatalogRow().Value == "51" && catalogPane.HasUnappliedChanges);
    catalogPane.Load(coldVm.Devices[1]);
    Check("Catalogue devices do not share mutable values", CatalogRow().Value == "42");
    CatalogRow().Value = "52"; catalogPane.Load(coldVm.Devices[0]);
    Check("Catalogue A draft remains isolated", CatalogRow().Value == "51" && catalogPane.Apply());
    catalogPane.Load(coldVm.Devices[1]);
    Check("Applying catalogue A preserves B draft", CatalogRow().Value == "52" && catalogPane.HasUnappliedChanges);
    catalogPane.DiscardDrafts();
    Check("Discarded catalogue B returns to default", CatalogRow().Value == "42" && !catalogPane.HasUnappliedChanges);
    var detached = coldVm.LoadParametersFor(coldVm.Devices[1]);
    detached.Types[0].Restrictions[0].Max = 7;
    detached.Instances[0].ParameterType!.IsVisible = false;
    var fresh = coldVm.LoadParametersFor(coldVm.Devices[1]);
    Check("Catalogue definitions are detached too", fresh.Types[0].Restrictions[0].Max == 255 && fresh.Instances[0].ParameterType!.IsVisible);


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
