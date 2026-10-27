using Knx;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Portfolio;
using System.IO.Compression;
using System.Text;

if (args.Contains("--performance-budget") || args.Contains("--performance-budget-checks"))
{
    if (args.Length != 1) { Console.Error.WriteLine("Use exactly one performance-budget mode without additional arguments."); return 2; }
    if (args[0] == "--performance-budget") return NativePerformanceChecks.RunBudget();
    var failures = 0; var count = 0;
    NativePerformanceBudgetChecks.Run((name, value) => { count++; if (!value) failures++; Console.WriteLine((value ? "PASS " : "FAIL ") + name); });
    Console.WriteLine($"Performance budget checks: {count}, failures: {failures}");
    return failures == 0 ? 0 : 1;
}
if (args.Contains("--performance")) return NativePerformanceChecks.Run();
if (args.Contains("--projection-refresh")) return WorkbenchRefreshChecks.Run();
if (args.Contains("--dialogs")) return DialogChecks.Run(args.Contains("--de") ? "de" : "en");

var checks = 0;
void Check(string name, bool value)
{
    if (!value) throw new Exception("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
var root = Path.Combine(Path.GetTempPath(), "buschord-workspace-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    NativePerformanceBudgetChecks.Run(Check);
    var workspace = new WorkbenchWorkspace("P1");
    var unnamedProject = Knx.Workbench.WorkbenchJson.CreateDemo();
    var unnamedDevice = ProjectQuery.DeviceList(unnamedProject).First();
    unnamedDevice.Name = "";
    unnamedProject.Hardware[unnamedDevice.ProductRefId] = new Knx.Catalog.CatalogHardware
        { HardwareId = "hardware-test", Name = "Product label" };
    var navigationDevice = WorkbenchProjection.Navigation(unnamedProject, "building")
        .SelectMany(Flatten).First(n => ReferenceEquals(n.Model, unnamedDevice));
    Check("Unnamed navigator devices show product text without changing stored name",
        navigationDevice.Name == "Product label" && unnamedDevice.Name == "");
    unnamedDevice.Name = "Custom name";
    Check("Explicit device names take precedence over product labels", WorkbenchProjection.DeviceName(unnamedProject, unnamedDevice) == "Custom name");
    unnamedDevice.Name = " ";
    unnamedProject.Hardware.Clear();
    Check("Devices without name or product text fall back to address", WorkbenchProjection.DeviceName(unnamedProject, unnamedDevice) == unnamedDevice.Address.ToString());
    static IEnumerable<WorkbenchNavNode> Flatten(WorkbenchNavNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    var room = workspace.Open(new("room", "R1"), "Wohnzimmer");
    room.Filter = "Licht"; room.IncludeRelated = false; room.SelectedObjectIds.Add("KO1");
    room.CollapsedDeviceIds.Add("D1"); room.View = "relations";
    var device = workspace.Open(new("device", "D1"), "Taster", "params");
    Check("Opening another target preserves room filter/selection/view", room.Filter == "Licht"
        && room.SelectedObjectIds.SequenceEqual(["KO1"]) && room.View == "relations");
    Check("Reopen keeps tab identity and local state", ReferenceEquals(room, workspace.Open(new("room", "R1"), "Wohnen"))
        && workspace.Tabs.Count == 2 && room.Title == "Wohnen" && room.Filter == "Licht");
    workspace.Navigators["building"].Query = "EG";
    workspace.Navigators["topology"].Query = "1.1";
    workspace.Navigators["building"].ExpandedIds.Add("F1");
    Check("Navigator perspectives have independent search state", workspace.Navigators["building"].Query == "EG"
        && workspace.Navigators["topology"].Query == "1.1");
    workspace.InspectorVisible = true; workspace.ToolsVisible = true; workspace.NavigatorWidth = 301;
    var path = Path.Combine(root, "layout.json"); workspace.SaveLayout(path);
    var restored = new WorkbenchWorkspace("P1");
    Check("Layout roundtrip succeeds", restored.TryRestoreLayout(path));
    var restoredRoom = restored.Tabs.Single(t => t.Target.Kind == "room");
    Check("Work item identity and representation roundtrip", restoredRoom.Target == room.Target
        && restoredRoom.View == "relations" && restoredRoom.Filter == "Licht" && !restoredRoom.IncludeRelated
        && restoredRoom.CollapsedDeviceIds.Contains("D1") && restored.ActiveTab == restoredRoom);
    Check("Transient KO selection is not restored", restoredRoom.SelectedObjectIds.Count == 0);
    Check("Navigator and layout roundtrip", restored.NavigatorWidth == 301 && restored.ToolsVisible
        && restored.InspectorVisible && restored.Navigators["building"].ExpandedIds.Contains("F1"));
    var other = new WorkbenchWorkspace("P2");
    Check("Other project receives geometry, not stale work targets", other.TryRestoreLayout(path)
        && other.NavigatorWidth == 301 && other.Tabs.Count == 0);
    restored.TryRestoreLayout(path, target => target.Kind != "device");
    Check("Missing project targets are not restored", restored.Tabs.Count == 1);
    restored.ResetLayout();
    Check("Reset layout preserves work item state", restored.Tabs.Count == 1 && restoredRoom.Filter == "Licht"
        && restored.NavigatorWidth == 272 && !restored.ToolsVisible);
    restored.FocusLayout();
    Check("Focus mode hides chrome, not work items", !restored.NavigatorVisible && restored.Tabs.Count == 1);
    restored.Close(restored.Tabs[0]);
    Check("Closing final tab leaves consistent empty workspace", restored.Tabs.Count == 0 && restored.ActiveTab is null);
    workspace.ChangeProject("P2");
    Check("Project change clears per-project navigation and tabs", workspace.Tabs.Count == 0
        && workspace.ActiveTab is null && workspace.Navigators["building"].Query == "");
    File.WriteAllText(path, "{ invalid json");
    Check("Malformed layout cannot break startup", !workspace.TryRestoreLayout(path));
    File.WriteAllText(path, "{\"Version\":99}");
    Check("Unknown layout version is not applied", !workspace.TryRestoreLayout(path));
    workspace.NavigatorWidth = double.NaN; workspace.SaveLayout(path);
    Check("Invalid splitter geometry is normalized", restored.TryRestoreLayout(path) && restored.NavigatorWidth == 272);
    Check("No partial layout files remain", !Directory.EnumerateFiles(root, "*.tmp").Any());
    var audit = new WorkbenchAudit();
    for (var i = 0; i < 620; i++) audit.Add("change " + i, DateTimeOffset.UnixEpoch.AddSeconds(i));
    Check("Change history is bounded and newest-first", audit.Count == WorkbenchAudit.Capacity
        && audit.Entries[0].Description == "change 619" && audit.Entries[^1].Description == "change 120");
    audit.Clear();
    Check("Change history clears without touching project/layout", audit.Count == 0 && workspace.Tabs.Count == 0);
    // Parameter sessions expose project identity and an explicit read-only reason without UI.
    var parameterVm = new Knx.App.Services.MainWindowViewModel();
    var parameterProject = new Knx.Project { Id = "P-PARAM", Name = "Parameter" };
    var parameterInstallation = new Knx.Installation { Id = "I-PARAM" };
    var parameterArea = new Knx.Area { Id = "A-PARAM", Address = 1 };
    var parameterLine = new Knx.Line { Id = "L-PARAM", Address = 1 };
    parameterLine.Devices.Add(new Knx.DeviceInstance { Id = "D-PARAM", Name = "Device", ProductRefId = "" });
    parameterArea.Lines.Add(parameterLine);
    parameterInstallation.Areas.Add(parameterArea);
    parameterProject.Installations.Add(parameterInstallation);
    parameterVm.CreateProject(parameterProject, null);
    var parameterSession = new Knx.App.Services.ParameterEditingSession(parameterVm);
    parameterSession.Load(parameterVm.Devices[0]);
    parameterSession.SetReadOnly("DCA/plug-in does not run here.");
    Check("Parameter session exposes project identity and read-only reason",
        parameterSession.ProjectId == "P-PARAM" && parameterSession.IsReadOnly && !parameterSession.CanApply);
    parameterSession.ClearReadOnly();
    var nextParameterProject = new Knx.Project { Id = "P-PARAM-2", Name = "Parameter 2" };
    parameterVm.CreateProject(nextParameterProject, null);
    Check("Parameter session reports a switched project as stale", parameterSession.IsStale
        && parameterSession.ProjectId == "P-PARAM-2");

    // Favorites/pinning: project-scoped, persisted in the layout, never resurrected for other projects.
    var pins = new WorkbenchWorkspace("P1");
    var pinnedDevice = new WorkbenchTarget("device", "D1");
    var pinnedGroup = new WorkbenchTarget("ga", "GA1");
    Check("Favorite toggles on and off", pins.ToggleFavorite(pinnedDevice) && pins.IsFavorite(pinnedDevice)
        && !pins.ToggleFavorite(pinnedDevice) && !pins.IsFavorite(pinnedDevice));
    pins.ToggleFavorite(pinnedDevice);
    pins.ToggleFavorite(pinnedGroup);
    pins.ToggleFavorite(new("device", ""));
    var pinPath = Path.Combine(root, "pins.json");
    pins.SaveLayout(pinPath);
    var restoredPins = new WorkbenchWorkspace("P1");
    Check("Invalid favorites are never stored", pins.Favorites.SequenceEqual([pinnedDevice, pinnedGroup]));
    Check("Favorites persist with the workspace layout", restoredPins.TryRestoreLayout(pinPath)
        && restoredPins.Favorites.SequenceEqual([pinnedDevice, pinnedGroup]));
    restoredPins.TryRestoreLayout(pinPath, target => target.Kind != "ga");
    Check("Favorites for missing targets are dropped on restore", restoredPins.Favorites.SequenceEqual([pinnedDevice]));
    var otherPins = new WorkbenchWorkspace("P2");
    otherPins.ToggleFavorite(pinnedDevice);
    otherPins.TryRestoreLayout(pinPath);
    Check("Favorites do not leak into another project", otherPins.Favorites.SequenceEqual([pinnedDevice]));
    restoredPins.ChangeProject("P2");
    Check("Project change clears favorites with the tabs", restoredPins.Favorites.Count == 0);

    // Drag-and-drop target validation stays a pure projection: invalid drops never mutate.
    var dropProject = new Project { Id = "P-DROP", Name = "Drop" };
    var dropInstallation = new Installation { Id = "I-1", Name = "Gebäude" };
    var dropArea = new Area { Id = "A-1", Address = 1 };
    var dropLine = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
    var otherLine = new Line { Id = "L-2", Address = 2, Name = "Linie 2" };
    var dropSegment = new Segment { Id = "S-1", Address = 1 };
    dropLine.Segments.Add(dropSegment);
    dropArea.Lines.Add(dropLine); dropArea.Lines.Add(otherLine);
    dropInstallation.Areas.Add(dropArea); dropProject.Installations.Add(dropInstallation);
    var dropRoom = dropInstallation.Locations.Add(null, "Wohnzimmer", SpaceKind.Room, "R-1");
    var dropDevice = new DeviceInstance { Id = "D-1", Name = "Taster", Address = new(1, 1, 10), ProductRefId = "M-1" };
    dropSegment.Devices.Add(dropDevice); dropRoom.AssignDevice(dropDevice);
    var dropAddressBefore = dropDevice.Address.ToString();
    var toOtherLine = WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-2"));
    Check("Drop on another line resolves a segment-free target", toOtherLine.Ok
        && toOtherLine.Line?.Id == "L-2" && toOtherLine.Segment is null);
    var outOfSegment = WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-1"));
    Check("Drop on the own line leaves the segment", outOfSegment.Ok && outOfSegment.Segment is null);
    Check("Drop on the current segment is a no-op", WorkbenchProjection
        .ResolveDeviceDrop(dropProject, dropDevice.Id, new("segment", "S-1")).Status == DeviceDropStatus.AlreadyThere);
    Check("Drop rejects non-topology, unknown device and unknown line targets",
        WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("room", "R-1")).Status == DeviceDropStatus.NotATopologyTarget
        && WorkbenchProjection.ResolveDeviceDrop(dropProject, "D-missing", new("line", "L-2")).Status == DeviceDropStatus.DeviceMissing
        && WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-missing")).Status == DeviceDropStatus.LineMissing);
    Check("Rejected drops leave topology, address and room untouched",
        dropSegment.Devices.Contains(dropDevice) && !otherLine.Devices.Contains(dropDevice)
        && dropDevice.Address.ToString() == dropAddressBefore && dropRoom.DeviceRefs.Contains(dropDevice.Id));
    var move = WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-2"));
    var moved = move.Ok ? ProjectOperations.MoveDevice(dropProject, dropDevice, move.Line!, move.Segment) : null;
    Check("Validated drop moves the device and keeps its room", moved is { Ok: true }
        && otherLine.Devices.Contains(dropDevice) && !dropSegment.Devices.Contains(dropDevice)
        && dropRoom.DeviceRefs.Contains(dropDevice.Id) && dropDevice.Address.Line == 2);
    Check("Dropping the moved device back onto its line is a no-op",
        WorkbenchProjection.ResolveDeviceDrop(dropProject, dropDevice.Id, new("line", "L-2")).Status == DeviceDropStatus.AlreadyThere);

    // DCA/plug-in catalogue applications must surface a read-only reason and never touch values.
    var dcaRoot = Path.Combine(root, "dca-catalog");
    Directory.CreateDirectory(dcaRoot);
    File.WriteAllText(Path.Combine(dcaRoot, "dca.knxprod"), """
        <?xml version="1.0" encoding="utf-8"?>
        <KNX xmlns="http://knx.org/xml/prod/20">
          <ManufacturerData>
            <Manufacturer RefId="M-DCA">
              <Hardware>
                <Hardware Id="M-DCA_H-1" Name="Panel" SerialNumber="1" VersionNumber="1"
                          HasIndividualAddress="true" HasApplicationProgram="true">
                  <Products><Product Id="M-DCA_H-1_P-1" Text="DCA panel" OrderNumber="DCA-1"/></Products>
                  <Hardware2Programs>
                    <Hardware2Program Id="M-DCA_H-1_HP-1"><ApplicationProgramRef RefId="M-DCA_A-1"/></Hardware2Program>
                  </Hardware2Programs>
                </Hardware>
              </Hardware>
              <ApplicationPrograms>
                <ApplicationProgram Id="M-DCA_A-1" Name="DCA panel" ApplicationNumber="1" ApplicationVersion="1">
                  <Static>
                    <Extension EtsUiPlugin="{11111111-1111-1111-1111-111111111111}"/>
                    <ParameterTypes>
                      <ParameterType Id="PT-1" Name="Brightness" SizeInBit="8" DataType="Number"/>
                    </ParameterTypes>
                    <Parameters>
                      <Parameter Id="P-1" Name="Brightness" ParameterType="PT-1" Value="50"/>
                    </Parameters>
                    <ParameterRefs>
                      <ParameterRef Id="PR-1" RefId="P-1"/>
                    </ParameterRefs>
                  </Static>
                </ApplicationProgram>
              </ApplicationPrograms>
            </Manufacturer>
          </ManufacturerData>
        </KNX>
        """, new UTF8Encoding(false));
    var dcaLibrary = new Knx.App.Services.CatalogLibrary(new Knx.Catalog.CatalogStore(dcaRoot));
    var dcaVm = new Knx.App.Services.MainWindowViewModel(dcaLibrary);
    var dcaProject = new Project { Id = "P-DCA", Name = "DCA project" };
    var dcaInstallation = new Installation { Id = "I-DCA" };
    var dcaArea = new Area { Id = "A-DCA", Address = 1 };
    var dcaLine = new Line { Id = "L-DCA", Address = 1 };
    var dcaDevice = new DeviceInstance { Id = "D-DCA", Name = "Panel", ProductRefId = "M-DCA_H-1_P-1", Address = new(1, 1, 5) };
    dcaLine.Devices.Add(dcaDevice);
    dcaArea.Lines.Add(dcaLine); dcaInstallation.Areas.Add(dcaArea); dcaProject.Installations.Add(dcaInstallation);
    dcaVm.CreateProject(dcaProject, null);
    Check("DCA read-only reason stays null while the catalogue is cold",
        dcaVm.ParameterReadOnlyReason(dcaDevice) is null);
    await dcaLibrary.GetAsync();
    var dcaReason = dcaVm.ParameterReadOnlyReason(dcaDevice);
    Check("DCA catalogue program yields a localized read-only reason",
        dcaReason is { Length: > 0 } text && text.Contains("DCA", StringComparison.Ordinal));
    var dcaSession = new Knx.App.Services.ParameterEditingSession(dcaVm);
    dcaSession.Load(dcaVm.Devices[0]);
    var dcaValues = dcaSession.Rows.Select(r => r.Value).ToArray();
    dcaSession.SetReadOnly(dcaReason);
    Check("DCA parameter editors are read-only and cannot apply", dcaSession.Rows.Length > 0
        && dcaSession.IsReadOnly && !dcaSession.CanApply && !dcaSession.CanReset);
    dcaSession.ResetToDefaults();
    Check("DCA apply and reset leave saved values untouched", !dcaSession.Apply()
        && dcaSession.Rows.Select(r => r.Value).SequenceEqual(dcaValues) && dcaDevice.ParameterValues.Count == 0);

    // SEC-4: without any catalogue the project archive's own application program still declares
    // DCA/broken; the pane and the write boundary must respect it.
    var secProject = new Project { Id = "P-SEC4", Name = "SEC-4" };
    var secInstallation = new Installation { Id = "I-SEC4" };
    var secArea = new Area { Id = "A-SEC4", Address = 1 };
    var secLine = new Line { Id = "L-SEC4", Address = 1 };
    var archiveDcaDevice = new DeviceInstance { Id = "D-SEC4-DCA", Name = "Archive DCA", Address = new(1, 1, 1), ProductRefId = "M-0DCA_A-1" };
    var archiveBrokenDevice = new DeviceInstance { Id = "D-SEC4-BROKEN", Name = "Archive broken", Address = new(1, 1, 2), ProductRefId = "M-0BRK_A-1" };
    var archiveStaticDevice = new DeviceInstance { Id = "D-SEC4-STATIC", Name = "Archive static", Address = new(1, 1, 3), ProductRefId = "M-0STA_A-1" };
    secLine.Devices.Add(archiveDcaDevice); secLine.Devices.Add(archiveBrokenDevice); secLine.Devices.Add(archiveStaticDevice);
    secArea.Lines.Add(secLine); secInstallation.Areas.Add(secArea); secProject.Installations.Add(secInstallation);
    var secPath = Path.Combine(root, "sec4.knxproj");
    Knxproj.Save(secProject, secPath);
    using (var secArchive = ZipFile.Open(secPath, ZipArchiveMode.Update))
    {
        void Part(string name, string programAttributes, string staticContent)
        {
            using var stream = secArchive.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream);
            writer.Write($"""
                <KNX><ApplicationProgram Id="{Path.GetFileNameWithoutExtension(name)}" {programAttributes}><Static>
                {staticContent}
                <ParameterTypes><ParameterType Id="PT-1" Name="Brightness" SizeInBit="8" DataType="Number"/></ParameterTypes>
                <Parameters><Parameter Id="P-1" Name="Brightness" ParameterType="PT-1" Value="50"/></Parameters>
                <ParameterRefs><ParameterRef Id="PR-1" RefId="P-1"/></ParameterRefs>
                </Static></ApplicationProgram></KNX>
                """);
        }
        Part("M-0DCA/M-0DCA_A-1.xml", "", "<Extension EtsUiPlugin=\"{11111111-1111-1111-1111-111111111111}\"/>");
        Part("M-0BRK/M-0BRK_A-1.xml", "Broken=\"1\"", "");
        Part("M-0STA/M-0STA_A-1.xml", "", "");
    }
    var secLibraryRoot = Path.Combine(root, "sec4-catalog");
    Directory.CreateDirectory(secLibraryRoot);
    var secVm = new Knx.App.Services.MainWindowViewModel(
        new Knx.App.Services.CatalogLibrary(new Knx.Catalog.CatalogStore(secLibraryRoot)));
    secVm.CreateProject(Knxproj.Load(secPath), null);
    secVm.ProjectPath = secPath;
    var archiveDcaRow = secVm.Devices.Single(d => d.Device.ProductRefId == "M-0DCA_A-1");
    var archiveBrokenRow = secVm.Devices.Single(d => d.Device.ProductRefId == "M-0BRK_A-1");
    var archiveStaticRow = secVm.Devices.Single(d => d.Device.ProductRefId == "M-0STA_A-1");
    var archiveDcaReason = secVm.ParameterReadOnlyReason(archiveDcaRow.Device);
    Check("SEC-4: archive DCA program blocks editing without any catalogue",
        archiveDcaReason is { Length: > 0 } dcaArchiveText && dcaArchiveText.Contains("DCA", StringComparison.Ordinal));
    Check("SEC-4: archive broken program blocks editing without any catalogue",
        secVm.ParameterReadOnlyReason(archiveBrokenRow.Device) is { Length: > 0 });
    Check("SEC-4: archive static program stays editable",
        secVm.ParameterReadOnlyReason(archiveStaticRow.Device) is null);
    var archiveDcaLoaded = secVm.LoadParametersFor(archiveDcaRow);
    var archiveDcaBaseline = archiveDcaLoaded.Instances.ToDictionary(i => i.Id, i => i.Value);
    archiveDcaLoaded.Instances[0].Value = "9";
    var archiveApplyRefused = false;
    try { secVm.ApplyParameterValues(archiveDcaRow.Device, archiveDcaLoaded.Instances, archiveDcaLoaded.Types, archiveDcaBaseline); }
    catch (InvalidOperationException) { archiveApplyRefused = true; }
    Check("SEC-4: Apply refuses archive DCA values even when the host skips the read-only flag",
        archiveApplyRefused && archiveDcaRow.Device.ParameterValues.Count == 0);
    var archiveDcaSession = new Knx.App.Services.ParameterEditingSession(secVm);
    archiveDcaSession.Load(archiveDcaRow);
    archiveDcaSession.SetReadOnly(archiveDcaReason);
    archiveDcaSession.Rows[0].Value = "9";
    Check("SEC-4: archive DCA session stays read-only and never applies",
        archiveDcaSession.IsReadOnly && !archiveDcaSession.CanApply && !archiveDcaSession.Apply()
        && archiveDcaSession.Rows[0].Value == "9" && archiveDcaRow.Device.ParameterValues.Count == 0);

    // V4 compare pins one group address: the active one wins, everything else uses the first address.
    var compareProject = new Project { Id = "P-COMPARE", Name = "Compare" };
    var compareInstallation = new Installation { Id = "I-COMPARE" };
    var compareRange = new GroupRange { Id = "GR-COMPARE", Name = "Compare", RangeStart = 0, RangeEnd = 65535 };
    compareRange.Addresses.Add(new GroupAddressEntry { Id = "GA-C-2", Name = "Second", Address = GroupAddress.ThreeLevel(1, 1, 2) });
    compareRange.Addresses.Add(new GroupAddressEntry { Id = "GA-C-1", Name = "First", Address = GroupAddress.ThreeLevel(1, 1, 1) });
    compareInstallation.MainRanges.Add(compareRange);
    compareProject.Installations.Add(compareInstallation);
    var activeGa = new WorkbenchTarget("ga", "GA-C-2");
    Check("compare keeps the active group address", WorkbenchCompare.Resolve(compareProject, activeGa) == activeGa);
    Check("compare falls back to the first group address", WorkbenchCompare.Resolve(compareProject,
        new WorkbenchTarget("device", "D-1")) == new WorkbenchTarget("ga", "GA-C-1"));
    Check("compare ignores a stale active target", WorkbenchCompare.Resolve(compareProject,
        new WorkbenchTarget("ga", "GA-missing")) == new WorkbenchTarget("ga", "GA-C-1"));
    Check("compare needs a project and a group address", WorkbenchCompare.Resolve(null, null) is null
        && WorkbenchCompare.Resolve(new Project { Id = "P-EMPTY", Name = "Empty" }, null) is null);

    // Project portfolio: one entry list, one edited project, and no write to any project file.
    var portfolioRoot = Path.Combine(root, "portfolio");
    Directory.CreateDirectory(portfolioRoot);
    var settingsFile = Path.Combine(portfolioRoot, "settings.json");
    File.WriteAllText(settingsFile, "{}");
    var firstProject = Path.Combine(portfolioRoot, "haus.knxproj");
    var secondProject = Path.Combine(portfolioRoot, "halle.knxproj");
    var absentProject = Path.Combine(portfolioRoot, "weg.knxproj");
    File.WriteAllText(firstProject, "haus");
    File.WriteAllText(secondProject, "halle");
    var portfolioSettings = new AppSettings { LastProject = firstProject, RecentProjects = [firstProject, secondProject, absentProject] };
    var settingsBytes = File.ReadAllBytes(settingsFile);
    var portfolioPath = WorkbenchPortfolio.StoragePath(settingsFile);
    Check("portfolio document sits next to the settings, not inside them",
        Path.GetFileName(portfolioPath) == "portfolio.json" && portfolioPath != settingsFile);
    var portfolio = new PortfolioRegistry(portfolioPath);
    var seeded = portfolio.Seed(portfolioSettings);
    Check("portfolio seeding adopts only existing recent projects",
        seeded == 2 && portfolio.Entries.Count == 2 && portfolio.FindByPath(absentProject) is null);
    Check("portfolio seeding leaves settings, recents and project files untouched",
        File.ReadAllBytes(settingsFile).SequenceEqual(settingsBytes)
        && portfolioSettings.RecentProjects.SequenceEqual([firstProject, secondProject, absentProject])
        && File.ReadAllText(firstProject) == "haus");
    Check("portfolio seeding keeps the last project most recent",
        portfolio.Entries[0].Path == Path.GetFullPath(firstProject));
    var firstEntry = portfolio.FindByPath(firstProject)!;
    var secondEntry = portfolio.FindByPath(secondProject)!;
    var firstBytes = File.ReadAllBytes(firstProject);
    var secondBytes = File.ReadAllBytes(secondProject);

    var thirdProject = Path.Combine(portfolioRoot, "neubau.knxproj");
    File.WriteAllText(thirdProject, "neubau");
    var thirdBytes = File.ReadAllBytes(thirdProject);
    var added = WorkbenchPortfolio.AddCurrent(portfolio, thirdProject, "Neubau");
    var addedEntry = added.EntryId is { } addedId ? portfolio.Find(addedId) : null;
    Check("portfolio adds the current project with its display name",
        added.Ok && addedEntry is { Name: "Neubau" }
        && addedEntry.Path == Path.GetFullPath(thirdProject));
    Check("portfolio add leaves the project file untouched", File.ReadAllBytes(thirdProject).SequenceEqual(thirdBytes));
    Check("portfolio add is idempotent",
        WorkbenchPortfolio.AddCurrent(portfolio, thirdProject, "Neubau").Status == PortfolioActionStatus.AlreadyRegistered
        && portfolio.Entries.Count == 3);
    Check("portfolio never adds unsaved or missing projects",
        WorkbenchPortfolio.AddCurrent(portfolio, null, "Lost").Status == PortfolioActionStatus.ProjectNotSaved
        && WorkbenchPortfolio.AddCurrent(portfolio, "", "Lost").Status == PortfolioActionStatus.ProjectNotSaved
        && WorkbenchPortfolio.AddCurrent(portfolio, absentProject, "Weg").Status == PortfolioActionStatus.FileMissing
        && portfolio.Entries.Count == 3);

    var revisionBeforeRename = portfolio.Find(added.EntryId!)!.Revision;
    var renamed = WorkbenchPortfolio.Rename(portfolio, added.EntryId!, "  Neubau Nord  ");
    Check("portfolio rename changes the display name only",
        renamed.Ok && portfolio.Find(added.EntryId!) is { } renamedEntry
        && renamedEntry.Name == "Neubau Nord" && renamedEntry.Path == addedEntry.Path
        && renamedEntry.Revision == revisionBeforeRename
        && File.ReadAllBytes(thirdProject).SequenceEqual(thirdBytes));
    Check("portfolio rejects an empty name and an unknown entry",
        WorkbenchPortfolio.Rename(portfolio, added.EntryId!, "  ").Status == PortfolioActionStatus.InvalidName
        && WorkbenchPortfolio.Rename(portfolio, "pf-000000000000", "X").Status == PortfolioActionStatus.EntryMissing
        && portfolio.Find(added.EntryId!)!.Name == "Neubau Nord");

    var opened = WorkbenchPortfolio.OpenTarget(portfolio, added.EntryId!);
    Check("portfolio open resolves an existing file without touching it",
        opened.Ok && opened.Path == addedEntry.Path && File.ReadAllBytes(thirdProject).SequenceEqual(thirdBytes));
    var lastOpenedBefore = portfolio.Find(added.EntryId!)!.LastOpenedUtc;
    Check("portfolio records only a successful open",
        WorkbenchPortfolio.MarkOpened(portfolio, added.EntryId!)
        && portfolio.Find(added.EntryId!)!.LastOpenedUtc >= lastOpenedBefore
        && File.ReadAllBytes(thirdProject).SequenceEqual(thirdBytes));

    File.AppendAllText(secondProject, " geändert");
    var revisionBeforeRefresh = portfolio.Find(secondEntry.Id)!.Revision;
    Check("portfolio reports a changed file from its recorded size",
        portfolio.State(portfolio.Find(secondEntry.Id)!) == PortfolioState.Changed);
    Check("portfolio maps every file state to a localized label key",
        WorkbenchPortfolio.StateKey(PortfolioState.Ok) == "PortfolioStateOk"
        && WorkbenchPortfolio.StateKey(PortfolioState.Changed) == "PortfolioStateChanged"
        && WorkbenchPortfolio.StateKey(PortfolioState.Missing) == "PortfolioStateMissing");
    var refreshed = WorkbenchPortfolio.Refresh(portfolio, secondEntry.Id);
    Check("portfolio refresh records revision and hash without writing the file",
        refreshed.Status == PortfolioActionStatus.Updated
        && portfolio.Find(secondEntry.Id)!.Revision == revisionBeforeRefresh + 1
        && File.ReadAllText(secondProject) == "halle geändert");
    Check("portfolio refresh reports an already current entry",
        WorkbenchPortfolio.Refresh(portfolio, secondEntry.Id).Status == PortfolioActionStatus.Unchanged);

    var movedProject = Path.Combine(portfolioRoot, "halle-neu.knxproj");
    File.Move(secondProject, movedProject);
    Check("portfolio blocks opening and refreshing a missing file, but keeps the entry",
        portfolio.State(portfolio.Find(secondEntry.Id)!) == PortfolioState.Missing
        && WorkbenchPortfolio.OpenTarget(portfolio, secondEntry.Id).Status == PortfolioActionStatus.FileMissing
        && WorkbenchPortfolio.Refresh(portfolio, secondEntry.Id).Status == PortfolioActionStatus.FileMissing
        && portfolio.Find(secondEntry.Id) is not null && portfolio.Entries.Count == 3);
    var located = WorkbenchPortfolio.Locate(portfolio, secondEntry.Id, movedProject);
    Check("portfolio repoints a moved file without rewriting it",
        located.Ok && portfolio.Find(secondEntry.Id)!.Path == Path.GetFullPath(movedProject)
        && File.ReadAllText(movedProject) == "halle geändert");
    Check("portfolio refuses a path that belongs to another entry",
        WorkbenchPortfolio.Locate(portfolio, secondEntry.Id, thirdProject).Status == PortfolioActionStatus.DuplicatePath);

    var removed = WorkbenchPortfolio.Remove(portfolio, added.EntryId!);
    Check("portfolio removal deletes only the registry entry",
        removed.Ok && portfolio.Find(added.EntryId!) is null && File.Exists(thirdProject)
        && File.ReadAllBytes(thirdProject).SequenceEqual(thirdBytes));
    Check("portfolio removal of an unknown entry changes nothing",
        WorkbenchPortfolio.Remove(portfolio, added.EntryId!).Status == PortfolioActionStatus.EntryMissing);

    var reopened = new PortfolioRegistry(portfolioPath);
    Check("portfolio mutations persist across reopen",
        reopened.Entries.Count == 2 && reopened.Entries.All(entry => portfolio.Find(entry.Id) is { } live
            && live.Name == entry.Name && live.Path == entry.Path && live.Revision == entry.Revision
            && live.SizeBytes == entry.SizeBytes && live.LastOpenedUtc == entry.LastOpenedUtc));
    var documentBytes = File.ReadAllBytes(portfolioPath);
    reopened.Save();
    Check("portfolio document stays byte-stable when saved again",
        File.ReadAllBytes(portfolioPath).SequenceEqual(documentBytes));
    Check("portfolio keeps the previous good document as a backup",
        File.Exists(portfolioPath + ".bak"));
    Check("portfolio actions never wrote a project file",
        File.ReadAllBytes(firstProject).SequenceEqual(firstBytes)
        && File.ReadAllBytes(thirdProject).SequenceEqual(thirdBytes)
        && File.ReadAllText(movedProject) == "halle geändert");
    Check("portfolio and settings stay separate documents",
        File.ReadAllBytes(settingsFile).SequenceEqual(settingsBytes));

    // Demo parameters keep the importer's vocabulary, encoding and numeric range in one place.
    var demoProject = Knx.Workbench.WorkbenchJson.CreateDemo();
    var demoDevice = ProjectQuery.DeviceList(demoProject)
        .First(d => demoProject.Workbench.Devices[d.Id].Demo && d.ParameterValues.ContainsKey("mode"));
    Check("every demo parameter key has a localized field label",
        demoDevice.ParameterValues.Keys.All(key => WorkbenchDemoForm.IsKnown(key)
            && WorkbenchDemoForm.Label(key) is { Length: > 0 } label && label != key));
    Check("demo fields order mode, led and interface before the numeric settings",
        demoDevice.ParameterValues.Keys.OrderBy(WorkbenchDemoForm.SortOrder)
            .ThenBy(key => key, StringComparer.Ordinal).First() == "mode");
    Check("demo form rejects non-numeric and out-of-range values",
        WorkbenchDemoForm.Validate("debounce", "abc") is { Length: > 0 }
        && WorkbenchDemoForm.Validate("debounce", "-1") is { Length: > 0 }
        && WorkbenchDemoForm.Validate("debounce", "1000001") is { Length: > 0 }
        && WorkbenchDemoForm.Validate("setpoint", "-20") is null
        && WorkbenchDemoForm.Validate("setpoint", "21.5") is null
        && WorkbenchDemoForm.Validate("mode", "anything") is null);
    Check("demo form stores strings JSON-quoted and numbers raw",
        WorkbenchDemoForm.Serialize("mode", "dim") == "\"dim\""
        && WorkbenchDemoForm.Serialize("interface", "Demo") == "\"Demo\""
        && WorkbenchDemoForm.Serialize("deadband", "0.5") == "0.5");

    // R02/R05: the documentation window and the compare action stay localized in both languages.
    var language = Knx.Strings.Language;
    try
    {
        var untranslated = new List<string>();
        foreach (var key in WorkbenchStrings.Keys)
        {
            Knx.Strings.Language = "de"; var de = WorkbenchStrings.Get(key);
            Knx.Strings.Language = "en"; var en = WorkbenchStrings.Get(key);
            if (string.IsNullOrWhiteSpace(de) || string.IsNullOrWhiteSpace(en)) untranslated.Add(key);
        }
        Check("every shell string exists in de and en", untranslated.Count == 0);
        var untranslatedCore = Knx.Strings.Catalogue
            .Where(p => string.IsNullOrWhiteSpace(p.Value.De) || string.IsNullOrWhiteSpace(p.Value.En))
            .Select(p => p.Key).ToList();
        Check("every core string exists in de and en", untranslatedCore.Count == 0);
        Knx.Strings.Language = "de"; var deTitle = WorkbenchStrings.Get("HelpTitle");
        Knx.Strings.Language = "en"; var enTitle = WorkbenchStrings.Get("HelpTitle");
        Check("documentation title and compare action are localized",
            deTitle.Contains("Dokumentation", StringComparison.Ordinal)
            && enTitle.Contains("Documentation", StringComparison.Ordinal)
            && WorkbenchStrings.Get("CompareNoGroupAddress").Length > 0);
    }
    finally { Knx.Strings.Language = language; }

    Console.WriteLine($"WORKBENCH UI STATE OK ({checks})");
}
finally { Directory.Delete(root, recursive: true); }
return 0;
