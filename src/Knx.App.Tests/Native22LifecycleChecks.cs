using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;
using Knx.TestFixtures;

internal static class Native22LifecycleChecks
{
    public static void Run(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-app-native22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousSettings = Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS");
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, "{}");
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settingsPath);
        try
        {
            foreach (var layout in new[] { "root", "nested", "prefixed" })
            {
                var source = Path.Combine(root, layout + ".knxproj");
                var original = Native22Fixture.Archive(layout);
                File.WriteAllBytes(source, original);
                var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "library"), Path.Combine(root, "index")));
                var vm = new MainWindowViewModel(catalog);
                void Check(string name, bool value) => check($"App native22 {layout}: {name}", value, null);
                vm.LoadProject(source);
                Check("load publishes the native model and path together", vm.Project?.Id == Native22Fixture.ProjectId
                    && vm.ProjectPath == source && !vm.IsDirty
                    && Native22IntegrationFacts.Matches(vm.Project, "Switch 22"));
                vm.Edit("Rename synthetic address", project => ProjectQuery.GroupAddresses(project).Single(a => a.Id == Native22Fixture.GroupId).Name = "Edited in Workbench");
                Check("edit records dirty state", vm.IsDirty);
                vm.UndoCommand.Execute(null);
                Check("undo restores native metadata and values", !vm.IsDirty && vm.Project?.Name == "Synthetic native 22"
                    && ProjectQuery.GroupAddresses(vm.Project).Single(a => a.Id == Native22Fixture.GroupId).Name == "Switch 22"
                    && ProjectQuery.DeviceList(vm.Project).Single().ParameterValues["R-22"] == "17");
                vm.RedoCommand.Execute(null);
                Check("redo restores the edit", vm.IsDirty
                    && ProjectQuery.GroupAddresses(vm.Project!).Single(a => a.Id == Native22Fixture.GroupId).Name == "Edited in Workbench");
                var savedPath = Path.Combine(root, layout + "-saved.knxproj");
                vm.SaveProjectAs(savedPath);
                Check("save-as commits path and saved state", vm.ProjectPath == savedPath && !vm.IsDirty && File.Exists(savedPath)
                    && AppSettings.Load(settingsPath).LastProject == savedPath);
                var parts = Native22Fixture.ProjectParts(savedPath, layout);
                var prefix = Native22Fixture.Prefix(layout);
                XNamespace ns = Native22Fixture.Namespace;
                var savedXml = Native22Fixture.Xml(parts[prefix + "0.xml"]);
                Check("save-as retains layout, native device address and opaque data", savedXml.Root?.Name == ns + "KNX"
                    && savedXml.Descendants(ns + "Segment").Count() == 1
                    && (string?)savedXml.Descendants(ns + "DeviceInstance").Single().Attribute("Address") == "7"
                    && parts[prefix + "opaque.bin"].SequenceEqual(new byte[] { 0, 255, 22, 128 })
                    && File.ReadAllBytes(source).SequenceEqual(original));
                vm.LoadProject(savedPath);
                Check("reopen retains the edit and native channel linkage", !vm.IsDirty
                    && Native22IntegrationFacts.Matches(vm.Project!, "Edited in Workbench"));
                Check("native information and both ZIP layers retain their original facts", Native22IntegrationFacts.ArchivePartsMatch(savedPath, layout));

                var invalidParts = Native22Fixture.Parts();
                invalidParts["0.xml"] = Encoding.UTF8.GetBytes(Native22Fixture.DataXml.Replace("/22", "/24", StringComparison.Ordinal));
                invalidParts["project.xml"] = Encoding.UTF8.GetBytes(Native22Fixture.InformationXml.Replace("/22", "/24", StringComparison.Ordinal));
                var invalid = Path.Combine(root, layout + "-unsupported.knxproj");
                File.WriteAllBytes(invalid, Native22Fixture.Zip(invalidParts));
                var beforeProject = vm.Project;
                var beforeSettings = File.ReadAllBytes(settingsPath);
                var loadRefused = false;
                try { vm.LoadProject(invalid); }
                catch (InvalidDataException) { loadRefused = true; }
                Check("unsupported open keeps project, path, dirty state and settings", loadRefused
                    && ReferenceEquals(vm.Project, beforeProject) && vm.ProjectPath == savedPath && !vm.IsDirty
                    && File.ReadAllBytes(settingsPath).SequenceEqual(beforeSettings));

                var line = vm.Project!.Installations.Single().Areas.Single().Lines.Single();
                var beforeFailedSave = Knxproj.ToXml(vm.Project).ToString(SaveOptions.DisableFormatting);
                var undoBeforeFailure = (vm.CanUndo, vm.CanRedo, vm.IsDirty);
                var unsupported = new Segment { Id = line.Segments.Single().Id, Address = 1 };
                line.Segments.Add(unsupported);
                var blockedPath = Path.Combine(root, layout + "-blocked.knxproj");
                byte[] sentinel = [7, 19, 23, 255];
                File.WriteAllBytes(blockedPath, sentinel);
                var saveRefused = false;
                try { vm.SaveProjectAs(blockedPath); }
                catch (InvalidDataException) { saveRefused = true; }
                Check("unsupported save-as keeps target, current path and settings", saveRefused
                    && File.ReadAllBytes(blockedPath).SequenceEqual(sentinel) && vm.ProjectPath == savedPath
                    && ReferenceEquals(vm.Project, beforeProject) && line.Segments.Count == 2 && ReferenceEquals(line.Segments[^1], unsupported)
                    && (vm.CanUndo, vm.CanRedo, vm.IsDirty) == undoBeforeFailure
                    && File.ReadAllBytes(settingsPath).SequenceEqual(beforeSettings));
                line.Segments.Remove(unsupported);
                Check("failed save-as changes neither the rest of the model nor history",
                    Knxproj.ToXml(vm.Project!).ToString(SaveOptions.DisableFormatting) == beforeFailedSave
                    && (vm.CanUndo, vm.CanRedo, vm.IsDirty) == undoBeforeFailure);
            }
        }
        catch (Exception ex) { check("App native22 lifecycle completes", false, ex.ToString()); }
        finally
        {
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", previousSettings);
            Directory.Delete(root, true);
        }
    }
}
