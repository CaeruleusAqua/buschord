using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Knx;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;

if (args.Contains("--native")) return NativeDraftChecks.Run(args.Contains("--shots"));

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
    var settings = Path.Combine(directory, "settings.json");
    File.WriteAllText(settings, "{}");
    Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settings);
    typeof(CatalogLibrary).GetField("_store", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(directory, "empty-catalog")));
    var path = Path.Combine(directory, "test.knxproj");
    failures += ImportRegressionChecks.Run(directory);
    failures += SemanticsChecks.Run();
    failures += TextReferenceChecks.Run(directory);
    failures += ModuleAllocationChecks.Run(directory);
    failures += ModuleScopeChecks.Run();
    failures += LiveStructureChecks.Run(directory);
    failures += ConditionalStructureChecks.Run(directory);
    failures += FunctionCalculationChecks.Run();
    failures += TimeParameterChecks.Run(directory);
    failures += NeverPlacedObjectChecks.Run();
    failures += DcaHostChecks.Run(directory);
    failures += RawDataParameterChecks.Run();
    failures += PreparedFunctionChecks.Run();
    failures += ChannelPipelineChecks.Run(directory);
    failures += SmartLinkWorkflowChecks.Run(directory);
    failures += LinkAtomicityChecks.Run(directory);
    failures += ParameterStorageChecks.Run(directory);
    failures += ParameterFootprintChecks.Run(directory);
    failures += OfflineParameterPatchChecks.Run(directory);
    failures += EnumParameterPatchChecks.Run(directory);
    failures += FloatParameterPatchChecks.Run(directory);
    failures += PackedTimeParameterPatchChecks.Run(directory);
    failures += ModuleParameterPatchChecks.Run(directory);
    failures += ModuleEnumParameterPatchChecks.Run(directory);
    failures += DenseTimeParameterPatchChecks.Run(directory);
    failures += ResourcePatchChecks.Run(directory);
    failures += ArchivedResourceChecks.Run(directory);
    failures += ArchivedFactoryImageChecks.Run();
    failures += ArchivedImageSourceChecks.Run(directory);
    failures += PropertySourceChecks.Run(directory);
    failures += PropertyPatchChecks.Run(directory);
    failures += DesiredImageChecks.Run(directory);
    failures += LoadProcedureChecks.Run(directory);
    failures += ApplicationBuildChecks.Run(directory);
    failures += NativeProcedureContextChecks.Run(directory);
    failures += SharedProcedureContextChecks.Run(directory);
    failures += ConditionalLoadProcedureChecks.Run(directory);
    failures += ResolvedApplicationBuildChecks.Run(directory);
    failures += GeneratedTableSerializerChecks.Run(directory);
    failures += ProvidedTableContextChecks.Run(directory);
    failures += TableEmissionMaterializerChecks.Run(directory);
    failures += SharedResolvedTableChecks.Run(directory);
    failures += RootImageBuildChecks.Run(directory);
    failures += RootResolvedImageBuildChecks.Run(directory);
    failures += RootWriterCommonChecks.Run(directory);
    failures += PropertyInitializationChecks.Run(directory);
    failures += RootPropertyBuildChecks.Run(directory);
    failures += NativeGeneralPopulationChecks.Run(directory);
    failures += NativeGeneralInitializationChecks.Run(directory);
    failures += NativeGeneralImageBuildChecks.Run(directory);
    failures += NativeImageBuildPublicChecks.Run(directory);
    failures += NativeImageCoalescingChecks.Run(directory);
    failures += NativeImageGrowthBuildChecks.Run(directory);
    failures += NativeImageScalarBuildChecks.Run(directory);
failures += NativeImageFoldChecks.Run(directory);
    failures += NativeCommunicationChecks.Run(directory);
    failures += NativeActivationChecks.Run(directory);
    failures += NativeStructuralActivationChecks.Run(directory);
    failures += ParameterMaskProfileChecks.Run(directory);
    failures += MaskProfileHostChecks.Run(directory);
    failures += StorageDiagnosticsChecks.Run(directory);
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
    var selectionBaselineState = (vm.IsDirty, vm.CanUndo, vm.CanRedo);
    var selectionBaselineXml = Knxproj.ToXml(vm.Project!).ToString();
    pane.Load(vm.Devices[1]);
    PaneRows()[0].Value = "41";
    pane.Load(vm.Devices[0]);
    pane.Load(vm.Devices[1]);
    Check("Device 2 draft 25 -> 41 survives selection away and back", PaneRows()[0].Value == "41");
    Check("Selection does not apply drafts", vm.Devices[1].Device.ParameterValues.Count == 0
        && (vm.IsDirty, vm.CanUndo, vm.CanRedo) == selectionBaselineState
        && Knxproj.ToXml(vm.Project!).ToString() == selectionBaselineXml);
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
    Check("Applied values do not resurrect an old draft", PaneRows()[0].Value == "41" && !pane.HasUnappliedChanges
        && pane.FindControl<TextBlock>("StatusHint")!.IsVisible
        && !pane.FindControl<Button>("ApplyButton")!.IsEnabled);
    PaneRows()[0].Value = "42"; pane.Load(vm.Devices[0]); PaneRows()[0].Value = "43";
    pane.DiscardDrafts();
    Check("DiscardDrafts resets current editor and every stored draft", !pane.HasUnappliedChanges && PaneRows()[0].Value == "25");
    pane.Load(vm.Devices[1]);
    Check("Discarded hidden-device draft cannot return", PaneRows()[0].Value == "41" && !pane.HasUnappliedChanges);
    PaneRows()[0].Value = "44";
    Check("Draft footer names the visible pending change",
        pane.FindControl<Border>("DraftPill")!.IsVisible && pane.Session.ChangedCount == 1
        && pane.FindControl<TextBlock>("DraftText")!.Text!.Contains('1'));
    PaneRows()[0].Value = "101";
    Check("Invalid field blocks Apply and names the field in the footer summary",
        pane.Session.HasValidationErrors && pane.Session.ValidationSummary.Contains("Brightness")
        && pane.FindControl<TextBlock>("ValidationSummary")!.IsVisible
        && !pane.FindControl<Button>("ApplyButton")!.IsEnabled);
    Check("Reset-to-default uses the declared default, not the saved baseline",
        pane.Session.CanReset && PaneRows()[0].Value == "101");
    pane.Session.ResetToDefaults();
    Check("Reset-to-default clears the validation error but keeps the unsaved draft",
        PaneRows()[0].Value == "25" && !pane.Session.HasValidationErrors && pane.HasUnappliedChanges
        && !pane.Session.CanReset);
    PaneRows()[0].Value = "44";
    pane.FindControl<Button>("DiscardButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check("Footer discard restores the applied baseline",
        PaneRows()[0].Value == "41" && !pane.HasUnappliedChanges
        && !pane.FindControl<Border>("DraftPill")!.IsVisible);
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
    Check("External application replacement preserves the open immutable definition and draft", PaneRows()[0].Value == "48"
        && PaneRows()[0].IsVisible && pane.HasUnappliedChanges);
    vm.CreateProject(Knxproj.Load(path), path);
    vm.Devices[1].Device.ParameterValues["R-N"] = "47";
    pane.Load(vm.Devices[1]);
    Check("Explicit reimport cannot restore an obsolete hidden draft", PaneRows()[0].Value == "47"
        && !PaneRows()[0].IsVisible && !pane.HasUnappliedChanges);
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

    // Parameter open: the copy constructor replaces the JSON round trip, so the shared catalogue
    // program stays untouched and every open returns a fresh, equal, detached model.
    var sharedProgram = published.Catalogs[0].FindProgramFor(published.Catalogs[0].Products[0])!;
    var sharedInstance = sharedProgram.ParameterInstances.Single(i => i.Id == "R-N");
    var sharedType = sharedProgram.ParameterTypes.Single(t => t.Id == "N");
    var firstOpen = coldVm.LoadParametersFor(coldVm.Devices[0]);
    firstOpen.Instances[0].Value = "999";
    firstOpen.Instances[0].IsVisible = false;
    firstOpen.Types[0].IsVisible = false;
    firstOpen.Types[0].Restrictions[0].Max = 1;
    var secondOpen = coldVm.LoadParametersFor(coldVm.Devices[0]);
    Check("Catalogue loads never mutate the shared catalogue program",
        sharedInstance.Value == "42" && sharedInstance.IsVisible
        && sharedType.IsVisible && sharedType.Restrictions[0].Max == 255);
    Check("Second open is a fresh, equal model detached from the first open",
        !ReferenceEquals(secondOpen.Instances[0], firstOpen.Instances[0])
        && !ReferenceEquals(secondOpen.Types[0], firstOpen.Types[0])
        && secondOpen.Instances[0].Value == "51" && secondOpen.Instances[0].IsVisible
        && secondOpen.Types[0].IsVisible && secondOpen.Types[0].Restrictions[0].Max == 255);
    Check("Instance types are remapped onto the copied type list",
        ReferenceEquals(secondOpen.Instances[0].ParameterType, secondOpen.Types.Single(t => t.Id == "N")));
    var openClock = System.Diagnostics.Stopwatch.StartNew();
    coldVm.LoadParametersFor(coldVm.Devices[0]);
    openClock.Stop();
    Check("Warm catalogue parameter open stays below a second", openClock.ElapsedMilliseconds < 1000);

    // Drafts still belong to one saved-value generation: an override outside the loaded rows
    // invalidates the draft exactly like a changed row value.
    catalogPane.Load(coldVm.Devices[0]);
    CatalogRow().Value = "60";
    catalogPane.Load(coldVm.Devices[1]);
    coldVm.Devices[0].Device.ParameterValues["R-GHOST"] = "1";
    catalogPane.Load(coldVm.Devices[0]);
    Check("A saved value outside the loaded rows still invalidates the draft",
        CatalogRow().Value == "51" && !catalogPane.HasUnappliedChanges);
    coldVm.Devices[0].Device.ParameterValues.Remove("R-GHOST");
    catalogPane.DiscardDrafts();

    var session = new ParameterEditingSession(coldVm);
    session.Load(coldVm.Devices[0]);
    session.Rows[0].Value = "999";
    var undoBeforePreview = coldVm.CanUndo;
    var modelBeforePreview = coldVm.Devices[0].Device.ParameterValues["R-N"];
    var preview = session.ReviewChanges();
    Check("Control-free preview uses core validation without project mutation", !preview.IsValid
        && preview.Changes.Single() is { Before: "51", After: "999", Error: not null }
        && coldVm.CanUndo == undoBeforePreview && coldVm.Devices[0].Device.ParameterValues["R-N"] == modelBeforePreview);
    var validationMessage = session.Message;
    using var firstView = new ParameterPane(session);
    using var secondView = new ParameterPane(session);
    Check("Two fresh views share the existing draft and validation", !ReferenceEquals(firstView, secondView)
        && firstView.Session == secondView.Session && session.Rows[0].Value == "999"
        && firstView.FindControl<TextBlock>("MessageText")!.Text == validationMessage
        && secondView.FindControl<TextBlock>("MessageText")!.Text == validationMessage);
    firstView.Dispose();
    session.Rows[0].Value = "998";
    session.ReviewChanges();
    Check("Disposed view receives no stale session events", firstView.FindControl<ItemsControl>("ParameterList")!.ItemCount == 0
        && firstView.FindControl<TextBlock>("MessageText")!.Text == validationMessage);
    secondView.Dispose();
    Check("Host can query dirty state without a view", session.HasUnappliedChanges);
    session.DiscardDrafts();
    Check("Host can discard without a view", !session.HasUnappliedChanges && session.Rows[0].Value == "51");
    session.Rows[0].Value = "55";
    // A replacement project must really have a new identity, even with identical device IDs.
    coldVm.CreateProject(Knxproj.Load(path), null);
    Check("Session dirty query ignores drafts after project replacement", !session.HasUnappliedChanges);
    Check("Project replacement rejects a stale preview and Apply without a control",
        !session.CanApply && !session.ReviewChanges().IsValid && !session.Apply());
    using var replacementView = new ParameterPane(session);
    Check("Fresh view cannot display an old project's draft", session.Device is null && session.Rows.Length == 0
        && !session.HasUnappliedChanges && !replacementView.Apply()
        && !replacementView.FindControl<Button>("ApplyButton")!.IsVisible);


    // B4: section navigation, DCA/opaque read-only mode and project-switch notices.
    var sectionsPath = Path.Combine(directory, "sections.knxproj");
    var sectionsProject = new Project { Id = "P-SEC", Name = "Sections" };
    var sectionsInstallation = new Installation { Id = "I-SEC" };
    var sectionsArea = new Area { Id = "A-SEC", Address = 1 };
    var sectionsLine = new Line { Id = "L-SEC", Address = 1 };
    sectionsLine.Devices.Add(new DeviceInstance { Id = "D-SEC", Name = "Channel actuator",
        Address = new(1, 1, 1), ProductRefId = "M-0SEC_A-0001-01-0000" });
    sectionsLine.Devices.Add(new DeviceInstance { Id = "D-OPAQUE", Name = "Opaque device",
        Address = new(1, 1, 2), ProductRefId = "M-0SEC_A-0002-01-0000" });
    sectionsArea.Lines.Add(sectionsLine); sectionsInstallation.Areas.Add(sectionsArea);
    sectionsProject.Installations.Add(sectionsInstallation);
    Knxproj.Save(sectionsProject, sectionsPath);
    const string channelEntry = "M-0SEC/M-0SEC_A-0001-01-0000.xml";
    using (var archive = ZipFile.Open(sectionsPath, ZipArchiveMode.Update))
    {
        using (var stream = archive.CreateEntry(channelEntry).Open())
            XDocument.Parse("""
                <KNX><ApplicationProgram><Static><ParameterTypes>
                <ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="100" /></ParameterType>
                </ParameterTypes><Parameters>
                <Parameter Id="P-CA1" Name="Kanal A: Schalten" ParameterType="N" Value="1" />
                <Parameter Id="P-CA2" Name="Kanal A: Dimmen" ParameterType="N" Value="2" />
                <Parameter Id="P-CB" Name="Kanal B: Schalten" ParameterType="N" Value="3" />
                <Parameter Id="P-G" Name="Global" ParameterType="N" Value="4" />
                </Parameters><ParameterRefs>
                <ParameterRef Id="R-CA1" RefId="P-CA1" /><ParameterRef Id="R-CA2" RefId="P-CA2" />
                <ParameterRef Id="R-CB" RefId="P-CB" /><ParameterRef Id="R-G" RefId="P-G" />
                </ParameterRefs></Static><Dynamic><choose /></Dynamic></ApplicationProgram></KNX>
                """).Save(stream);
        using (var stream = archive.CreateEntry("M-0SEC/M-0SEC_A-0002-01-0000.xml").Open())
            XDocument.Parse("""
                <KNX><ApplicationProgram><Static><ParameterTypes>
                <ParameterType Id="U" Name="Opaque"><TypeFuture /></ParameterType>
                </ParameterTypes><Parameters>
                <Parameter Id="P-U" Name="Opaque value" ParameterType="U" Value="7" />
                </Parameters><ParameterRefs><ParameterRef Id="R-U" RefId="P-U" /></ParameterRefs>
                </Static><Dynamic><choose /></Dynamic></ApplicationProgram></KNX>
                """).Save(stream);
    }
    var sectionVm = new MainWindowViewModel();
    sectionVm.CreateProject(Knxproj.Load(sectionsPath), null);
    sectionVm.ProjectPath = sectionsPath;
    using var sectionPane = new ParameterPane(sectionVm);
    sectionPane.Load(sectionVm.Devices[0]);
    Check("Section navigation groups existing channel metadata",
        sectionPane.Session.Sections.Select(s => s.Title).SequenceEqual(["Kanal A", "Kanal B", "Allgemein"])
        && sectionPane.Session.Sections[0] is { FirstRow: 0, RowCount: 2 }
        && sectionPane.Session.Sections[2] is { FirstRow: 3, RowCount: 1 }
        && sectionPane.FindControl<ItemsControl>("SectionList")!.IsVisible);
    sectionPane.Session.Rows[0].Value = "9";
    sectionPane.Session.SelectSection("Kanal B");
    Check("Section selection changes focus state without hiding drafts or rows",
        sectionPane.Session.SelectedSection == "Kanal B" && sectionPane.Session.Rows.Length == 4
        && sectionPane.HasUnappliedChanges);
    sectionPane.Session.NextSection();
    Check("Section navigation wraps to the general section", sectionPane.Session.SelectedSection == "Allgemein");
    sectionPane.Session.PreviousSection();
    Check("Section navigation steps back", sectionPane.Session.SelectedSection == "Kanal B");

    sectionPane.Session.SetReadOnly("DCA/plug-in does not run here: parameters are read-only.");
    Check("Explicit read-only mode disables editors, Apply and Reset but keeps values",
        sectionPane.IsReadOnly && sectionPane.ReadOnlyReason!.Contains("DCA")
        && !sectionPane.Session.CanApply && !sectionPane.Session.CanReset && !sectionPane.Apply()
        && sectionPane.FindControl<Border>("ReadOnlyPanel")!.IsVisible
        && sectionPane.FindControl<TextBlock>("ReadOnlyText")!.Text!.Contains("DCA")
        && !sectionPane.FindControl<ItemsControl>("ParameterList")!.IsEnabled
        && !sectionPane.FindControl<Button>("ApplyButton")!.IsVisible
        && sectionPane.Session.Rows[0].Value == "9");
    sectionPane.Session.ClearReadOnly();
    Check("Clearing read-only mode restores editing", !sectionPane.IsReadOnly
        && sectionPane.FindControl<ItemsControl>("ParameterList")!.IsEnabled);

    sectionPane.Load(sectionVm.Devices[1]);
    Check("Opaque application data is detected as read-only and kept unchanged",
        sectionPane.IsReadOnly && sectionPane.ReadOnlyReason!.Contains("TypeFuture")
        && sectionPane.Session.Rows[0].Value == "7"
        && sectionVm.Devices[1].Device.ParameterValues.Count == 0
        && !sectionPane.Session.CanApply);
    Check("Single-section parameter sets hide the strip", !sectionPane.Session.HasSectionNavigation
        && !sectionPane.FindControl<ItemsControl>("SectionList")!.IsVisible);

    sectionPane.Load(sectionVm.Devices[0]);
    sectionPane.Session.Rows[0].Value = "8";
    var priorProject = sectionPane.Session.ProjectId;
    var switchedDevice = sectionVm.Devices[0].Device;
    sectionVm.CreateProject(new Project { Id = "P-SWITCH", Name = "Switched" }, null);
    Check("Session exposes project identity and stale drafts",
        sectionPane.Session.ProjectId == "P-SWITCH" && sectionPane.Session.ProjectId != priorProject
        && sectionPane.Session.IsStale);
    sectionPane.Refresh();
    var switchNotice = sectionPane.Session.DraftNotice;
    Check("Project switch discards drafts with a visible notice and never applies",
        switchNotice is { Length: > 0 } && switchNotice.Contains("Sections")
        && !switchNotice.Contains("Switched")
        && sectionPane.FindControl<Border>("DraftNoticePanel")!.IsVisible
        && sectionPane.FindControl<TextBlock>("DraftNoticeText")!.Text == switchNotice
        && !sectionPane.HasUnappliedChanges && switchedDevice.ParameterValues.Count == 0);
    sectionPane.Session.AcknowledgeNotice();
    Check("Draft notice can be dismissed", !sectionPane.FindControl<Border>("DraftNoticePanel")!.IsVisible);

    sectionVm.CreateProject(Knxproj.Load(sectionsPath), null);
    sectionVm.ProjectPath = sectionsPath;
    sectionPane.Load(sectionVm.Devices[0]);
    sectionPane.Session.Rows[0].Value = "9";
    sectionVm.Devices[0].Device.ParameterValues["R-CA1"] = "5";
    sectionPane.Refresh();
    Check("Refresh discards stale drafts with its own notice and never applies",
        sectionPane.Session.DraftNotice is { Length: > 0 } refreshNotice && refreshNotice != switchNotice
        && sectionPane.FindControl<Border>("DraftNoticePanel")!.IsVisible
        && sectionPane.Session.Rows[0].Value == "5" && !sectionPane.HasUnappliedChanges
        && sectionVm.Devices[0].Device.ParameterValues["R-CA1"] == "5");

    var paneType = typeof(Knx.App.Views.ParameterPane);
    Check("Pane keeps the embedding contract",
        paneType.GetConstructor(Type.EmptyTypes) is not null
        && paneType.GetConstructor([typeof(MainWindowViewModel)]) is not null
        && paneType.GetProperty("Device")?.PropertyType == typeof(DeviceInstance)
        && paneType.GetMethod("Load", [typeof(DeviceRow)])?.ReturnType == typeof(void)
        && paneType.GetMethod("Refresh", Type.EmptyTypes)?.ReturnType == typeof(void)
        && paneType.GetMethod("Apply", Type.EmptyTypes)?.ReturnType == typeof(bool));

    // V4 pane surfaces: shared classes, the 280-540 field column and the compact search tool.
    Check("Parameter pane is a V4 root", pane.Classes.Contains("wb-root"));
    static double FieldWidth(double width) => (double)ParameterFieldWidthConverter.Instance
        .Convert(width, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture)!;
    Check("Field column keeps its 280-540 px cap", FieldWidth(200) == 280 && FieldWidth(296) == 292
        && FieldWidth(900) == 540);
    var textRow = new ParameterItemViewModel(
        new ParameterInstance { Id = "I-V4", Name = "Label", Value = "A" },
        new ParameterType { Id = "T-V4", Name = "Text", DataType = ParameterDataType.Text });
    Check("Editable fields carry the shared wb-field class",
        ParameterTemplateSelector.Instance.Build(rows[0]) is NumericUpDown numeric && numeric.Classes.Contains("wb-field")
        && ParameterTemplateSelector.Instance.Build(textRow) is TextBox text && text.Classes.Contains("wb-field"));
    var search = new SearchPane(vm);
    Check("Search pane is a V4 root without legacy panel chrome",
        search.Classes.Contains("wb-root")
        && !search.GetLogicalDescendants().OfType<Border>()
            .Any(b => b.Classes.Contains("panel"))
        && !search.GetLogicalDescendants().OfType<TextBlock>()
            .Any(t => t.Classes.Contains("panelHeader") || t.Classes.Contains("columnHeader")));
    var replaceToggle = search.FindControl<ToggleButton>("ReplaceToggle")!;
    var replaceSection = search.FindControl<StackPanel>("ReplaceSection")!;
    Check("Replace section starts collapsed", !replaceSection.IsVisible);
    replaceToggle.IsChecked = true;
    Check("Replace section expands from its toggle", replaceSection.IsVisible);
}
finally { Directory.Delete(directory, true); }
Console.WriteLine(failures == 0 ? "PARAMETER UI CHECKS OK" : $"{failures} failures");
return failures == 0 ? 0 : 1;
