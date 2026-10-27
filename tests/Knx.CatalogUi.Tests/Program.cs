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
Check("insert into selected installation", CatalogBrowser.Insert(vm, switcher, program, choice, "1.1.1", " First ", out _)
    && !project.Installations[0].Areas[0].Lines[0].Segments[0].Devices.Any()
    && project.Installations[1].Areas[0].Lines[0].Segments[0].Devices.Count == 1);
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

var scratch = Path.Combine(Path.GetTempPath(), "catalog-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var path = Path.Combine(scratch, "fixture.knxprod");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open()))
        writer.Write("""
            <KNX><ManufacturerData><Manufacturer RefId="M-UI"><Hardware><Hardware Id="H-1"><Products>
            <Product Id="P-UI" Text="Fixture" OrderNumber="UI-1" />
            </Products></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """);
    var wrapper = Path.Combine(scratch, "vendor.zip");
    using (var zip = ZipFile.Open(wrapper, ZipArchiveMode.Create)) zip.CreateEntryFromFile(path, "fixture.knxprod");
    var store = new CatalogStore(Path.Combine(scratch, "library"));
    Check("knxprod import into scratch", store.Import(path).Products.Single().Id == "P-UI");
    Check("vendor zip import into scratch", store.Import(wrapper).Products.Single().Id == "P-UI");
    File.WriteAllText(Path.Combine(store.Directory, "broken.knxprod"), "invalid");
    Check("nonfatal library load errors", store.LoadAll().Count == 1 && store.LoadErrors.Count == 1);
}
finally
{
    foreach (var file in Directory.GetFiles(scratch, "*", SearchOption.AllDirectories)) File.Delete(file);
    foreach (var dir in Directory.GetDirectories(scratch).OrderByDescending(p => p.Length)) Directory.Delete(dir);
    Directory.Delete(scratch);
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
    Check("progress reports every library file", reports.SequenceEqual([new CatalogProgress(1, 1)]));
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

    var fresh = new CatalogLibrary(new CatalogStore(Path.Combine(libraryRoot, "fresh")));
    var canceled = false;
    try { await fresh.GetAsync(null, new CancellationToken(canceled: true)); }
    catch (OperationCanceledException) { canceled = true; }
    Check("cancelled waiter throws without loading", canceled && fresh.Current is null);
}
finally
{
    foreach (var file in Directory.GetFiles(libraryRoot, "*", SearchOption.AllDirectories)) File.Delete(file);
    foreach (var dir in Directory.GetDirectories(libraryRoot).OrderByDescending(p => p.Length)) Directory.Delete(dir);
    Directory.Delete(libraryRoot);
}

Console.WriteLine($"CATALOG UI OK ({checks} checks; headless, no visual validation)");

/// <summary>Synchronous progress sink: Progress&lt;T&gt; would post to the thread pool.</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

