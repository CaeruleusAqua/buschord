using System.IO.Compression;
using Knx;
using Knx.App.Services;
using Knx.Catalog;

var checks = 0;
void Check(string name, bool passed)
{
    if (!passed) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var switcher = new CatalogProduct { Id = "P-001", Name = "Switch actuator", OrderNumber = "AX-4", Description = "Four outputs" };
switcher.CatalogPaths.AddRange(["Lighting/Switches", "Clearance"]);
var dimmer = new CatalogProduct { Id = "P-002", Name = "Dimmer", OrderNumber = "DX-1" };
dimmer.CatalogPaths.Add("Lighting/Dimmers");
CatalogProduct[] products = [switcher, dimmer];
Check("all products without project", CatalogBrowser.Filter(products, " ", null).Count == 2);
foreach (var query in new[] { "SWITCH", "ax-4", "p-001", "outputs" })
    Check("search " + query, CatalogBrowser.Filter(products, query, null).SequenceEqual([switcher]));
Check("section list distinct sorted", CatalogBrowser.Sections(products).SequenceEqual(["Clearance", "Lighting/Dimmers", "Lighting/Switches"]));
Check("section with multiple paths", CatalogBrowser.Filter(products, null, "Clearance").SequenceEqual([switcher]));
Check("combined matching filters", CatalogBrowser.Filter(products, "AX", "Lighting/Switches").Count == 1);
Check("combined empty results", CatalogBrowser.Filter(products, "AX", "Lighting/Dimmers").Count == 0);
Check("unknown section", CatalogBrowser.Filter(products, null, "Missing").Count == 0);

var vm = new MainWindowViewModel();
Check("no project rejected", !CatalogBrowser.Insert(vm, switcher, null, null, "1.1.1", null, out var message)
    && message == Strings.Get("StatusNoProject") && !vm.CanUndo);
var project = new Project { Id = "P-1", Name = "Scratch" };
foreach (var id in new[] { "I-1", "I-2" })
{
    var installation = new Installation { Id = id };
    var area = new Area { Id = "A-1", Address = 1 };
    var line = new Line { Id = "L-1", Address = 1 };
    line.Segments.Add(new Segment { Id = "S-1" });
    area.Lines.Add(line);
    installation.Areas.Add(area);
    if (id == "I-2")
    {
        var room = new SpaceNode { Id = "R-1", Kind = SpaceKind.Room, Name = "Room" };
        installation.Locations.Roots.Add(room);
    }
    project.Installations.Add(installation);
}
vm.CreateProject(project, null);
var choice = CatalogBrowser.Lines(project)[1];
Check("no project has no address suggestion", CatalogBrowser.SuggestAddress(null, choice) is null);
Check("suggest lowest free address", CatalogBrowser.SuggestAddress(project, choice) == "1.1.1");
Check("suggestion is free", !ProjectQuery.DeviceList(project).Any(d => d.Address.ToString() == CatalogBrowser.SuggestAddress(project, choice)));
var program = new CatalogApplicationProgram { Id = "AP-1" };
program.ComObjects.Add(new ComObject { Id = "CO-1", Name = "Switch", Number = 1 });
foreach (var invalid in new[] { "1.2.1", "2.1.1", "1.1.0", "16.1.1", "17.1.1", "1.17.1", "255.1.1", "1.255.1", "1.1.256", "nonsense" })
    Check("reject without mutation " + invalid,
        !CatalogBrowser.Insert(vm, switcher, program, choice, invalid, null, out _)
        && !ProjectQuery.DeviceList(project).Any() && !vm.CanUndo && vm.SelectedDevice is null);
Check("insert into selected installation and room", CatalogBrowser.Insert(vm, switcher, program, choice, "1.1.1", " First ", out _, "R-1")
    && !project.Installations[0].Areas[0].Lines[0].Segments[0].Devices.Any()
    && project.Installations[1].Areas[0].Lines[0].Segments[0].Devices.Count == 1
    && project.Installations[1].Locations.Roots.Single().DeviceRefs.Contains("1.1.1"));
var selected = vm.SelectedDevice;
Check("name and communication objects", selected?.Device.Name == "First" && selected.Device.ComObjectRefs.Count == 1);
Check("suggestion skips existing address", CatalogBrowser.SuggestAddress(project, choice) == "1.1.2");
Check("duplicate rejected without selection change", !CatalogBrowser.Insert(vm, switcher, program, choice, "1.1.1", null, out _)
    && ProjectQuery.DeviceList(project).Count() == 1 && vm.SelectedDevice == selected);
Check("duplicate across installations rejected", !CatalogBrowser.Insert(vm, switcher, program,
    CatalogBrowser.Lines(project)[0], "1.1.1", null, out _) && ProjectQuery.DeviceList(project).Count() == 1);
vm.UndoCommand.Execute(null);
Check("single undo removes insertion", !ProjectQuery.DeviceList(vm.Project!).Any() && !vm.CanUndo);
vm.RedoCommand.Execute(null);
Check("redo restores objects", ProjectQuery.DeviceList(vm.Project!).Single().ComObjectRefs.Count == 1);
Check("stale choice resolves after redo", CatalogBrowser.Insert(vm, switcher, program, choice, "", null, out _)
    && vm.SelectedDevice?.Device.Address.ToString() == "1.1.2"
    && vm.Project!.Installations[1].Areas[0].Lines[0].Segments[0].Devices.Count == 2
    && vm.Project.Installations[0].Areas[0].Lines[0].Segments[0].Devices.Count == 0);
Check("foreign project choice rejected", !CatalogBrowser.Insert(vm, switcher, program,
    choice with { ProjectId = "other" }, "1.1.3", null, out _) && ProjectQuery.DeviceList(vm.Project!).Count() == 2);
Check("missing line rejected", !CatalogBrowser.Insert(vm, switcher, program,
    choice with { LineId = "missing" }, "1.1.3", null, out _) && ProjectQuery.DeviceList(vm.Project!).Count() == 2);
var current = vm.Project!;
current.Installations.Add(current.Installations[1]);
Check("ambiguous installation rejected", !CatalogBrowser.Insert(vm, switcher, program, choice, "1.1.3", null, out _));
current.Installations.RemoveAt(2);
Check("no product rejected", !CatalogBrowser.Insert(vm, null, null, choice, "1.1.3", null, out _));
Check("no line rejected", !CatalogBrowser.Insert(vm, switcher, null, null, "1.1.3", null, out _));
var targetLine = current.Installations[1].Areas[0].Lines[0];
for (var device = 3; device <= 255; device++)
    targetLine.Segments[0].Devices.Add(CatalogInstaller.CreateDevice(switcher, null,
        new IndividualAddress(1, 1, (byte)device), "Filled"));
selected = vm.SelectedDevice;
Check("full line rejected without mutation", !CatalogBrowser.Insert(vm, switcher, program, choice, "", null, out message)
    && message == Strings.Get("CatalogLineFull") && ProjectQuery.DeviceList(current).Count() == 255
    && vm.SelectedDevice == selected);
Check("full line has no suggestion", CatalogBrowser.SuggestAddress(current, choice) is null);
Check("stale choice has no suggestion", CatalogBrowser.SuggestAddress(current, choice with { LineId = "missing" }) is null);
Check("foreign project has no suggestion", CatalogBrowser.SuggestAddress(current, choice with { ProjectId = "other" }) is null);

// ---- line + segment target, room context and name conflicts ----
var segVm = new MainWindowViewModel();
var segProject = new Project { Id = "P-SEG", Name = "Segments" };
var segInstallation = new Installation { Id = "I-SEG" };
var segArea = new Area { Id = "A-SEG", Address = 3 };
var segLine = new Line { Id = "L-SEG", Address = 3 };
var firstSegment = new Segment { Id = "SG-1", Name = "First", Address = 1 };
var secondSegment = new Segment { Id = "SG-2", Name = "Second", Address = 2 };
segLine.Segments.Add(firstSegment);
segLine.Segments.Add(secondSegment);
segArea.Lines.Add(segLine);
segInstallation.Areas.Add(segArea);
segInstallation.Locations.Roots.Add(new SpaceNode { Id = "R-SEG", Kind = SpaceKind.Room, Name = "Room" });
segProject.Installations.Add(segInstallation);
segVm.CreateProject(segProject, null);
var segChoice = CatalogBrowser.Lines(segProject).Single();
Line LiveLine() => segVm.Project!.Installations[0].Areas[0].Lines[0];
SpaceNode LiveRoom() => segVm.Project!.Installations[0].Locations.Roots.Single();
Check("segment list follows the line",
    CatalogBrowser.Segments(segProject, segChoice).Select(item => item.Id).SequenceEqual(["SG-1", "SG-2"]));
Check("segment list rejects a foreign line", CatalogBrowser.Segments(segProject, segChoice with { LineId = "missing" }).Count == 0);
Check("suggested name is free", CatalogBrowser.UniqueName(segProject, segChoice, "Aktor") == "Aktor");
var segProgram = new CatalogApplicationProgram { Id = "AP-SEG" };
segProgram.ComObjects.Add(new ComObject { Id = "CO-SEG", Name = "Switch", Number = 1 });
Check("insert into segment and room",
    CatalogBrowser.Insert(segVm, switcher, segProgram, segChoice, "3.3.1", "Aktor", out _, "R-SEG", "SG-2")
    && LiveLine().Segments[0].Devices.Count == 0 && LiveLine().Segments[1].Devices.Count == 1
    && LiveLine().Segments[1].Devices[0].Name == "Aktor"
    && LiveRoom().DeviceRefs.SequenceEqual(["3.3.1"]));
Check("name conflict gets the next free suffix", CatalogBrowser.UniqueName(segProject, segChoice, "Aktor") == "Aktor (2)");
Check("segment insert is a single undo step", segVm.CanUndo);
segVm.UndoCommand.Execute(null);
Check("one undo removes segment placement and room assignment",
    !ProjectQuery.DeviceList(segVm.Project!).Any()
    && LiveLine().Segments[0].Devices.Count == 0 && LiveLine().Segments[1].Devices.Count == 0
    && LiveRoom().DeviceRefs.Count == 0 && !segVm.CanUndo);
segVm.RedoCommand.Execute(null);
Check("redo restores the segment device", LiveLine().Segments[1].Devices.Count == 1
    && LiveRoom().DeviceRefs.Count == 1);
Check("duplicate name is suffixed on insert",
    CatalogBrowser.Insert(segVm, switcher, segProgram, segChoice, "3.3.2", "Aktor", out var renameMessage, "R-SEG", "SG-2")
    && LiveLine().Segments[1].Devices.Count == 2 && LiveLine().Segments[1].Devices[1].Name == "Aktor (2)"
    && renameMessage.Contains("Aktor (2)", StringComparison.Ordinal));
segVm.UndoCommand.Execute(null);
Check("undo after a suffixed insert keeps the first device",
    LiveLine().Segments[1].Devices.Count == 1 && LiveLine().Segments[1].Devices[0].Name == "Aktor"
    && LiveRoom().DeviceRefs.Count == 1);
Check("missing segment is rejected without mutation",
    !CatalogBrowser.Insert(segVm, switcher, segProgram, segChoice, "3.3.3", null, out var missingSegment, "R-SEG", "SG-MISSING")
    && missingSegment == CatalogUiText.Get("SegmentMissing")
    && LiveLine().Segments[1].Devices.Count == 1);

var scratch = Path.Combine(Path.GetTempPath(), "catalog-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var path = Path.Combine(scratch, "fixture.knxprod");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open()))
        writer.Write("""
            <KNX><ManufacturerData><Manufacturer RefId="M-UI">
            <Hardware><Hardware Id="H-1"><Products>
            <Product Id="P-UI" Text="Fixture" OrderNumber="UI-1" VisibleDescription="Search text" />
            </Products>
            <Hardware2Programs><Hardware2Program Id="HP-1"><ApplicationProgramRef RefId="A-UI"/></Hardware2Program></Hardware2Programs>
            </Hardware></Hardware>
            <ApplicationPrograms><ApplicationProgram Id="A-UI" Name="Fixture application"><Static><ComObjectTable>
            <ComObject Id="CO-UI" Name="Switch" Text="Switch" Number="1" ObjectSize="1 Bit" DatapointType="DPST-1-1" />
            </ComObjectTable></Static></ApplicationProgram></ApplicationPrograms>
            <Languages><Language Identifier="de-DE"><TranslationUnit RefId="M-UI">
            <TranslationElement RefId="M-UI"><Translation AttributeName="Name" Text="Musterhersteller" /></TranslationElement>
            </TranslationUnit></Language></Languages>
            </Manufacturer></ManufacturerData></KNX>
            """);
    var wrapper = Path.Combine(scratch, "vendor.zip");
    using (var zip = ZipFile.Open(wrapper, ZipArchiveMode.Create)) zip.CreateEntryFromFile(path, "fixture.knxprod");
    var store = new CatalogStore(Path.Combine(scratch, "library"));
    Check("knxprod import into scratch", store.Import(path).Products.Single().Id == "P-UI");
    Check("vendor zip import into scratch", store.Import(wrapper).Products.Single().Id == "P-UI");
    var searchCatalog = KnxProductCatalog.Open(path);
    var searchProduct = searchCatalog.Products.Single();
    foreach (var query in new[] { "M-UI", "Musterhersteller", "Fixture", "UI-1", "Search text",
                                  "Fixture application", "Switch", "DPST-1-1", "1.001", "DPT-1" })
        Check("catalogue search " + query,
            CatalogBrowser.Filter(searchCatalog, query, null).SequenceEqual([searchProduct]));
    Check("catalogue search keeps section filter",
        CatalogBrowser.Filter(searchCatalog, "1.001", "Missing").Count == 0);
    Check("product DPT metadata",
        CatalogBrowser.DatapointTypes(searchCatalog, searchProduct).SequenceEqual(["DPST-1-1"]));
    File.WriteAllText(Path.Combine(store.Directory, "broken.knxprod"), "invalid");
    Check("nonfatal library load errors", store.LoadAll().Count == 1 && store.LoadErrors.Count == 1);
}
finally
{
    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
}

// ---- CatalogLibrary: session-wide cached loader (shared, single-flight, reload, imports) ----
var libraryRoot = Path.Combine(Path.GetTempPath(), "catalog-library-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(libraryRoot);
string WriteCatalog(string name, string productId)
{
    var path = Path.Combine(libraryRoot, name + ".knxprod");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open());
    writer.Write($"""
        <KNX><ManufacturerData><Manufacturer RefId="M-{name}"><Hardware><Hardware Id="H-{name}"><Products>
        <Product Id="{productId}" Text="{name}" OrderNumber="{name}" />
        </Products></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);
    return path;
}

try
{
    var library = new CatalogLibrary(new CatalogStore(Path.Combine(libraryRoot, "library")));
    library.Store.Import(WriteCatalog("first", "P-1"));
    var reports = new List<CatalogProgress>();
    var loadA = library.GetAsync(new SyncProgress<CatalogProgress>(reports.Add));
    var loadB = library.GetAsync();
    Check("single flight shares one load", ReferenceEquals(loadA, loadB));
    var snapshotA = await loadA;
    var snapshotB = await loadB;
    Check("single flight returns one snapshot", ReferenceEquals(snapshotA, snapshotB));
    Check("snapshot has catalogue, products and no errors",
        snapshotA.Catalogs.Count == 1 && snapshotA.ProductCount == 1 && snapshotA.Errors.Count == 0);
    Check("progress reports every library file",
        reports.Select(report => (report.Loaded, report.Total)).SequenceEqual([(1, 1)])
        && reports[0].File is { Length: > 0 });
    var cached = library.GetAsync();
    Check("loaded snapshot is cached", cached.IsCompletedSuccessfully && ReferenceEquals(cached.Result, snapshotA));

    library.Store.Import(WriteCatalog("second", "P-2"));
    var reloaded = await library.ReloadAsync();
    Check("reload picks up a newly imported file", reloaded.Catalogs.Count == 2 && reloaded.ProductCount == 2
        && !ReferenceEquals(reloaded, snapshotA));

    File.WriteAllText(Path.Combine(library.Store.Directory, "broken.knxprod"), "invalid");
    var broken = await library.ReloadAsync();
    Check("snapshot carries load errors", broken.Errors.Count == 1 && broken.Catalogs.Count == 2);

    var changes = 0;
    library.Changed += (_, _) => changes++;
    var third = library.Store.Import(WriteCatalog("third", "P-3"));
    var merged = library.Add([third]);
    Check("import merges without re-parsing the library",
        merged is not null && merged.Catalogs.Count == 3 && merged.ProductCount == 3 && changes == 1);
    Check("last import report survives reload", library.Store.LastImportReport is { } importReport
        && Path.GetFileName(importReport.File) == Path.GetFileName(third.Path)
        && importReport.Products == 1 && importReport.Programs == 0
        && !importReport.HasBrokenProgram && !importReport.HasDcaProgram && !importReport.HasExternalSoftware);

    // Removal stays inside the library: the store is the only path to the file system.
    var outside = Path.Combine(libraryRoot, "outside.knxprod");
    File.WriteAllText(outside, "invalid");
    Check("remove outside the library is refused", !library.Store.Remove(outside) && File.Exists(outside));
    var nested = Directory.CreateDirectory(Path.Combine(library.Store.Directory, "nested"));
    var nestedFile = Path.Combine(nested.FullName, "nested.knxprod");
    File.WriteAllText(nestedFile, "invalid");
    Check("remove below the library root is refused", !library.Store.Remove(nestedFile) && File.Exists(nestedFile));
    Check("remove drops the catalogue from the library", library.Store.Remove(third.Path) && !File.Exists(third.Path));
    var afterRemove = await library.ReloadAsync();
    Check("reload after remove shows the remaining library",
        afterRemove.Catalogs.Count == 2 && afterRemove.ProductCount == 2 && afterRemove.Errors.Count == 1);

    // Cancelled reload/navigation must never discard the last good snapshot.
    var beforeCancel = library.Current!;
    using (var canceledReload = new CancellationTokenSource())
    {
        canceledReload.Cancel();
        var reloadObserved = false;
        try { await library.ReloadAsync(null, canceledReload.Token); }
        catch (OperationCanceledException) { reloadObserved = true; }
        Check("cancelled reload keeps the last snapshot",
            reloadObserved && ReferenceEquals(library.Current, beforeCancel));
    }
    Check("cancelled reload leaves the library readable",
        ReferenceEquals(await library.GetAsync(), beforeCancel));

    // Import reports per-file progress, keeps good files and names broken ones.
    var goodImport = WriteCatalog("fourth", "P-4");
    var badImport = Path.Combine(libraryRoot, "import-broken.knxprod");
    File.WriteAllText(badImport, "invalid");
    var importProgress = new List<CatalogImportProgress>();
    var import = await library.ImportAsync([goodImport, badImport],
        new SyncProgress<CatalogImportProgress>(importProgress.Add));
    Check("import reports progress per file",
        importProgress.Select(item => (item.Loaded, item.Total)).SequenceEqual([(1, 2), (2, 2)])
        && importProgress[0].File == Path.GetFileName(goodImport)
        && importProgress[1].File == Path.GetFileName(badImport));
    Check("import keeps good files and reports errors",
        import.Catalogs.Count == 1 && import.Errors.Count == 1
        && Path.GetFileName(import.Errors[0].File) == Path.GetFileName(badImport)
        && import.Snapshot is { Catalogs.Count: 3 });
    var canceledImport = await library.ImportAsync([goodImport], null, new CancellationToken(canceled: true));
    Check("cancelled import changes nothing",
        canceledImport.Canceled && canceledImport.Catalogs.Count == 0);
    var afterImport = await library.ReloadAsync();
    Check("reload after cancelled attempts still sees the imported file",
        afterImport.Catalogs.Count == 3 && afterImport.ProductCount == 3);

    var fresh = new CatalogLibrary(new CatalogStore(Path.Combine(libraryRoot, "fresh")));
    var canceled = false;
    try { await fresh.GetAsync(null, new CancellationToken(canceled: true)); }
    catch (OperationCanceledException) { canceled = true; }
    Check("cancelled waiter throws without loading", canceled && fresh.Current is null);
}
finally
{
    if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, recursive: true);
}

// ---- Application status gates: broken/incomplete block, DCA stays insertable but is read-only ----
var gateRoot = Path.Combine(Path.GetTempPath(), "catalog-gate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(gateRoot);
try
{
    var gatePath = Path.Combine(gateRoot, "gated.knxprod");
    using (var zip = ZipFile.Open(gatePath, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open()))
        writer.Write("""
            <KNX><ManufacturerData><Manufacturer RefId="M-GATE">
            <Hardware>
              <Hardware Id="H-DCA" HasIndividualAddress="true" HasApplicationProgram="true">
                <Products><Product Id="P-DCA" Text="DCA device" OrderNumber="DCA-1" /></Products>
                <Hardware2Programs><Hardware2Program Id="HP-DCA"><ApplicationProgramRef RefId="A-DCA"/></Hardware2Program></Hardware2Programs>
              </Hardware>
              <Hardware Id="H-BROKEN" HasIndividualAddress="true" HasApplicationProgram="true">
                <Products><Product Id="P-BROKEN" Text="Broken device" OrderNumber="BR-1" /></Products>
                <Hardware2Programs><Hardware2Program Id="HP-BROKEN"><ApplicationProgramRef RefId="A-BROKEN"/></Hardware2Program></Hardware2Programs>
              </Hardware>
            </Hardware>
            <ApplicationPrograms>
              <ApplicationProgram Id="A-DCA" Name="DCA application">
                <Static>
                  <Extension EtsUiPlugin="{11111111-1111-1111-1111-111111111111}" RequiresExternalSoftware="1"/>
                  <ComObjectTable>
                    <ComObject Id="CO-DCA" Name="Switch" Number="1" ObjectSize="1 Bit"/>
                  </ComObjectTable>
                </Static>
              </ApplicationProgram>
              <ApplicationProgram Id="A-BROKEN" Name="Broken application" Broken="true"><Static/></ApplicationProgram>
              <ApplicationProgram Id="A-INCOMPLETE" Name="Incomplete application" DownloadInfoIncomplete="true"><Static/></ApplicationProgram>
              <ApplicationProgram Id="A-EXTERNAL" Name="External software"><Static><Extension RequiresExternalSoftware="1"/></Static></ApplicationProgram>
              <ApplicationProgram Id="A-STATIC" Name="Static application"><Static/></ApplicationProgram>
            </ApplicationPrograms>
            </Manufacturer></ManufacturerData></KNX>
            """);

    var gated = KnxProductCatalog.Open(gatePath);
    var dca = gated.FindProgram("A-DCA")!;
    var broken = gated.FindProgram("A-BROKEN")!;
    var incomplete = gated.FindProgram("A-INCOMPLETE")!;
    var external = gated.FindProgram("A-EXTERNAL")!;
    var plain = gated.FindProgram("A-STATIC")!;
    Check("no application is never blocked",
        Enum.GetValues<CatalogUse>().All(use => CatalogBrowser.Blocked(null, use) == CatalogBlockReason.None));
    Check("broken blocks insert, edit and download",
        CatalogBrowser.Blocked(broken, CatalogUse.Insert) == CatalogBlockReason.Broken
        && CatalogBrowser.Blocked(broken, CatalogUse.EditParameters) == CatalogBlockReason.Broken
        && CatalogBrowser.Blocked(broken, CatalogUse.Download) == CatalogBlockReason.Broken);
    Check("incomplete download info blocks insert, edit and download",
        CatalogBrowser.Blocked(incomplete, CatalogUse.Insert) == CatalogBlockReason.DownloadInfoIncomplete
        && CatalogBrowser.Blocked(incomplete, CatalogUse.EditParameters) == CatalogBlockReason.DownloadInfoIncomplete
        && CatalogBrowser.Blocked(incomplete, CatalogUse.Download) == CatalogBlockReason.DownloadInfoIncomplete);
    Check("DCA stays insertable but blocks edit and download",
        CatalogBrowser.Blocked(dca, CatalogUse.Insert) == CatalogBlockReason.None
        && CatalogBrowser.Blocked(dca, CatalogUse.EditParameters) == CatalogBlockReason.DcaUnsupported
        && CatalogBrowser.Blocked(dca, CatalogUse.Download) == CatalogBlockReason.DcaUnsupported);
    Check("external software is informational only",
        Enum.GetValues<CatalogUse>().All(use => CatalogBrowser.Blocked(external, use) == CatalogBlockReason.None));
    Check("static application is usable",
        Enum.GetValues<CatalogUse>().All(use => CatalogBrowser.Blocked(plain, use) == CatalogBlockReason.None));
    Check("allowed reason is empty", CatalogUiText.Reason(CatalogBlockReason.None).Length == 0);

    var language = Strings.Language;
    Strings.Language = "en";
    var english = CatalogUiText.Reason(CatalogBlockReason.DcaUnsupported);
    Strings.Language = "de";
    Check("gating text follows the UI language",
        english.Contains("read-only", StringComparison.Ordinal)
        && CatalogUiText.Reason(CatalogBlockReason.DcaUnsupported) != english
        && CatalogUiText.Reason(CatalogBlockReason.DcaUnsupported).Contains("lesbar", StringComparison.Ordinal));

    var languageVm = new MainWindowViewModel();
    Strings.Language = "en";
    Check("catalog failure status follows the UI language",
        !CatalogBrowser.Insert(languageVm, switcher, null, null, "1.1.1", null, out var englishStatus)
        && englishStatus == "no project loaded" && englishStatus == Strings.Get("StatusNoProject"));
    Strings.Language = "de";
    Check("catalog failure status defaults to German",
        !CatalogBrowser.Insert(languageVm, switcher, null, null, "1.1.1", null, out var germanStatus)
        && germanStatus == "kein Projekt geladen");
    Strings.Language = language;

    var gateVm = new MainWindowViewModel();
    var gateProject = new Project { Id = "P-GATE", Name = "Gate" };
    var gateInstallation = new Installation { Id = "I-G" };
    var gateArea = new Area { Id = "A-G", Address = 2 };
    var gateLine = new Line { Id = "L-G", Address = 2 };
    gateLine.Segments.Add(new Segment { Id = "S-G" });
    gateArea.Lines.Add(gateLine);
    gateInstallation.Areas.Add(gateArea);
    gateProject.Installations.Add(gateInstallation);
    gateVm.CreateProject(gateProject, null);
    var gateChoice = CatalogBrowser.Lines(gateProject).Single();
    var brokenProduct = gated.Products.Single(product => product.Id == "P-BROKEN");
    var dcaProduct = gated.Products.Single(product => product.Id == "P-DCA");

    Check("broken application cannot be inserted",
        !CatalogBrowser.Insert(gateVm, brokenProduct, broken, gateChoice, "2.2.1", null, out message)
        && message == CatalogUiText.Reason(CatalogBlockReason.Broken)
        && !gateVm.CanUndo && gateVm.SelectedDevice is null);
    Check("incomplete application cannot be inserted",
        !CatalogBrowser.Insert(gateVm, brokenProduct, incomplete, gateChoice, "2.2.1", null, out message)
        && message == CatalogUiText.Reason(CatalogBlockReason.DownloadInfoIncomplete)
        && !gateVm.CanUndo);
    Check("DCA application inserts with its objects",
        CatalogBrowser.Insert(gateVm, dcaProduct, dca, gateChoice, "2.2.1", null, out _)
        && gateVm.SelectedDevice?.Device.ComObjectRefs.Count == 1
        && CatalogBrowser.SuggestAddress(gateProject, gateChoice) == "2.2.2");
}
finally
{
    if (Directory.Exists(gateRoot)) Directory.Delete(gateRoot, recursive: true);
}

// ---- catalogue search index: program/COM/DPT metadata without parsing a program part ----
var searchRoot = Path.Combine(Path.GetTempPath(), "catalog-search-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(searchRoot);
try
{
    var lazyPath = Path.Combine(searchRoot, "lazy.knxprod");
    using (var zip = ZipFile.Open(lazyPath, ZipArchiveMode.Create))
    {
        void Part(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
        Part("M-LAZY/Hardware.xml", """
            <KNX><ManufacturerData><Manufacturer RefId="M-LAZY"><Hardware>
            <Hardware Id="H-LAZY"><Products><Product Id="P-LAZY" Text="Lazy device" OrderNumber="LZ-1"/></Products>
            <Hardware2Programs><Hardware2Program Id="HP-LAZY"><ApplicationProgramRef RefId="A-LAZY"/></Hardware2Program></Hardware2Programs>
            </Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """);
        Part("M-LAZY/Catalog.xml", """
            <KNX><ManufacturerData><Manufacturer RefId="M-LAZY"><Catalog>
            <CatalogSection Id="CS-LAZY" Name="Lazy section"><CatalogItem Id="CI-LAZY" Name="Lazy entry"
            ProductRefId="P-LAZY" Hardware2ProgramRefId="HP-LAZY"/></CatalogSection>
            </Catalog></Manufacturer></ManufacturerData></KNX>
            """);
        Part("M-LAZY/M-LAZY_A-LAZY.xml", """
            <KNX><ManufacturerData><Manufacturer RefId="M-LAZY"><ApplicationPrograms>
            <ApplicationProgram Id="A-LAZY" Name="Needle application" ApplicationNumber="00AB" ApplicationVersion="3">
            <Static><ParameterTypes><ParameterType Id="PT-L" Name="Level" SizeInBit="8" DataType="Number"/></ParameterTypes>
            <Parameters><Parameter Id="P-L" Name="Level" ParameterType="PT-L" Text="Level" Value="1"/></Parameters>
            <ComObjectTable><ComObject Id="CO-L" Name="Needle object" Text="Needle object" Number="1"
            ObjectSize="1 Bit" DatapointType="DPST-1-1"/></ComObjectTable></Static>
            </ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
            """);
    }

    var lazyCatalog = KnxProductCatalog.Open(lazyPath);
    var lazyProduct = lazyCatalog.Products.Single();
    Check("search finds the product before enrichment",
        CatalogBrowser.Filter(lazyCatalog, "LAZY DEVICE", null).SequenceEqual([lazyProduct]));
    Check("search does not parse a program part",
        lazyCatalog.Programs.Count == 0 && lazyCatalog.LoadedProgramModels == 0);
    Check("program metadata is not invented before enrichment",
        CatalogBrowser.Filter(lazyCatalog, "Needle application", null).Count == 0
        && CatalogBrowser.Filter(lazyCatalog, "DPST-1-1", null).Count == 0);

    lazyCatalog.LoadProgramMetadata();
    foreach (var query in new[] { "Needle application", "A-LAZY", "00AB", "Needle object", "CO-L", "DPST-1-1", "1.001", "DPT-1" })
        Check("metadata search " + query,
            CatalogBrowser.Filter(lazyCatalog, query, null).SequenceEqual([lazyProduct]));
    Check("metadata search keeps the section filter",
        CatalogBrowser.Filter(lazyCatalog, "1.001", "Missing").Count == 0);
    Check("metadata search built no parameter model", lazyCatalog.LoadedProgramModels == 0);
    var lazyStopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (var run = 0; run < 20; run++) _ = CatalogBrowser.Filter(lazyCatalog, "zzz-no-match", null);
    lazyStopwatch.Stop();
    var lazyAverage = lazyStopwatch.Elapsed.TotalMilliseconds / 20;
    Console.WriteLine($"  INFO lazy metadata search: {lazyAverage:0.00} ms per query");
    Check("metadata search stays responsive", lazyAverage < 100);
    Check("opening the product still parses the model once",
        lazyCatalog.FindProgramFor(lazyProduct)?.ParameterInstances.Count == 1
        && lazyCatalog.LoadedProgramModels == 1);

    // 10,000 products: the scan stays a string comparison, no XML and no per-product arrays.
    var many = new List<CatalogProduct>();
    for (var i = 0; i < 10000; i++)
        many.Add(new CatalogProduct
        {
            Id = "P-" + i,
            Name = "Product " + i,
            SearchText = "product " + i + "\np-" + i,
        });
    var manyStopwatch = System.Diagnostics.Stopwatch.StartNew();
    var manyMatches = CatalogBrowser.Filter(many, "zzz-no-match", null);
    manyStopwatch.Stop();
    Console.WriteLine($"  INFO 10k product filter: {manyStopwatch.Elapsed.TotalMilliseconds:0.0} ms");
    Check("10k products return no false matches", manyMatches.Count == 0);
    Check($"10k product filter stays under 500 ms ({manyStopwatch.Elapsed.TotalMilliseconds:0.0} ms)",
        manyStopwatch.Elapsed.TotalMilliseconds < 500);

    // Cancelled/debounced searches yield no stale result; versions increase.
    using (var cancelledSearch = new CancellationTokenSource())
    {
        var pendingSearch = CatalogSearch.RunAsync(lazyCatalog, "zzz", null, 500, cancelledSearch.Token);
        cancelledSearch.Cancel();
        Check("cancelled search returns no result", await pendingSearch is null);
    }
    var firstSearch = await CatalogSearch.RunAsync(lazyCatalog, "needle", null, 0, CancellationToken.None);
    var secondSearch = await CatalogSearch.RunAsync(lazyCatalog, "needle", null, 0, CancellationToken.None);
    Check("search results are versioned",
        firstSearch is not null && secondSearch is not null
        && secondSearch.Version > firstSearch.Version
        && secondSearch.Products.SequenceEqual([lazyProduct]));
}
finally
{
    if (Directory.Exists(searchRoot)) Directory.Delete(searchRoot, recursive: true);
}

// ---- CatalogLibrary: progressive partial snapshots never replace the stable snapshot ----
var progressiveRoot = Path.Combine(Path.GetTempPath(), "catalog-progressive-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(progressiveRoot);
try
{
    var progressiveLibrary = new CatalogLibrary(new CatalogStore(Path.Combine(progressiveRoot, "library")));
    string WriteProgressive(string name, string productId)
    {
        var path = Path.Combine(progressiveRoot, name + ".knxprod");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open());
        writer.Write($"""
            <KNX><ManufacturerData><Manufacturer RefId="M-{name}"><Hardware><Hardware Id="H-{name}"><Products>
            <Product Id="{productId}" Text="{name}" OrderNumber="{name}" />
            </Products></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """);
        return path;
    }
    progressiveLibrary.Store.Import(WriteProgressive("first", "P-1"));
    progressiveLibrary.Store.Import(WriteProgressive("second", "P-2"));
    var partials = new List<int>();
    var stableSnapshotStayedEmpty = true;
    progressiveLibrary.SnapshotPublished += (_, snapshot) =>
    {
        partials.Add(snapshot.Catalogs.Count);
        if (progressiveLibrary.Current is not null) stableSnapshotStayedEmpty = false;
    };
    var stable = await progressiveLibrary.GetAsync();
    Check("progressive snapshots are published before completion",
        partials.Count > 0 && partials.All(count => count is > 0 and <= 2));
    Check("partial snapshots never become the stable snapshot",
        stableSnapshotStayedEmpty && stable.Catalogs.Count == 2 && progressiveLibrary.Current?.Catalogs.Count == 2);
    Check("progressive snapshots end at the full library", partials.Contains(2));
}
finally
{
    if (Directory.Exists(progressiveRoot)) Directory.Delete(progressiveRoot, recursive: true);
}

Console.WriteLine($"CATALOG UI OK ({checks} checks; headless, no visual validation)");

/// <summary>Synchronous progress sink: Progress&lt;T&gt; would post to the thread pool.</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

