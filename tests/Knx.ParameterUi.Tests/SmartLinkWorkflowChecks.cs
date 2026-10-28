using Knx;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Catalog;
using Knx.Workbench;

internal static class SmartLinkWorkflowChecks
{
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Smart linking: " + name); if (!ok) failures++; }
        MainWindowViewModel Create(bool invalid = true)
        {
            var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "smart-empty"))));
            vm.CreateProject(Fixture(invalid), null); return vm;
        }
        string State(MainWindowViewModel vm) => Knxproj.ToXml(vm.Project!).ToString();
        void Reject(string name, MainWindowViewModel vm, Action action)
        {
            var before = State(vm); var undo = vm.CanUndo; var blocked = false;
            try { action(); } catch (InvalidOperationException) { blocked = true; }
            Check(name, blocked && State(vm) == before && vm.CanUndo == undo);
        }
        var vm = Create(); var before = State(vm); var review = vm.PreviewSmartLinks();
        Check("preview owns three immutable candidates without edits or undo", review.Items.Count == 3
            && review.Items.Count(i => i.CanApply) == 2 && review.Items.All(i => i.Score >= 40) && State(vm) == before && !vm.CanUndo);
        Reject("an incompatible selected member prevents the entire batch", vm, () => vm.ApplySmartLinks(review));
        var selected = review.Items.Where(i => i.CanApply).Select(i => i.Id).ToArray();
        var applied = vm.ApplySmartLinks(review, selected);
        Check("explicit compatible selection links across devices in one edit", applied == 2 && review.IsApplied && vm.CanUndo
            && ProjectQuery.DeviceList(vm.Project!).Take(2).All(d => d.ComObjectRefs.Single().Links.Count == 1)
            && ProjectQuery.DeviceList(vm.Project!).Last().ComObjectRefs.Single().Links.Count == 0);
        vm.UndoCommand.Execute(null);
        Check("one undo restores the entire cross-device batch", ProjectQuery.ComObjectRefs(vm.Project!).All(o => o.Ref.Links.Count == 0) && !vm.CanUndo && vm.CanRedo);
        vm.RedoCommand.Execute(null);
        Check("redo restores the reviewed batch", ProjectQuery.ComObjectRefs(vm.Project!).Sum(o => o.Ref.Links.Count) == 2);
        Reject("an applied review cannot be replayed", vm, () => vm.ApplySmartLinks(review, selected));

        vm = Create(false); review = vm.PreviewSmartLinks(); var refreshed = vm.PreviewSmartLinks();
        Reject("a new review retires the previous one", vm, () => vm.ApplySmartLinks(review));
        Reject("unknown selection ids cannot inject a proposal", vm, () => vm.ApplySmartLinks(refreshed, ["forged"]));
        Reject("duplicate selection ids are rejected atomically", vm, () => vm.ApplySmartLinks(refreshed, [refreshed.Items[0].Id, refreshed.Items[0].Id]));
        vm.CancelSmartLinkReview(refreshed);
        Reject("cancelled review cannot be applied later", vm, () => vm.ApplySmartLinks(refreshed));
        vm = Create(false); review = vm.PreviewSmartLinks(); vm.CreateProject(Fixture(false), null);
        Reject("same-id project replacement invalidates the old review", vm, () => vm.ApplySmartLinks(review));
        vm = Create(false); review = vm.PreviewSmartLinks(); vm.CreateProject(vm.Project!, null);
        Reject("reopening the same project instance retires its previous session review", vm, () => vm.ApplySmartLinks(review));
        vm = Create(false); review = vm.PreviewSmartLinks();
        vm.Edit("Synthetic external edit", p => ProjectQuery.DeviceList(p).First().Name = "Edited"); vm.UndoCommand.Execute(null);
        Reject("undo replacement does not let a same-content review mutate detached objects", vm, () => vm.ApplySmartLinks(review));
        vm = Create(false); review = vm.PreviewSmartLinks();
        vm.Project!.Workbench.Objects[new ObjectKey("D1", "O1").ToString()].Bits = 8;
        Reject("capability changes invalidate the review before any write", vm, () => vm.ApplySmartLinks(review));
        vm = Create(false); var legacy = vm.SuggestLinks(); vm.CreateProject(Fixture(false), null);
        Reject("legacy ApplyLinks cannot write through old live references", vm, () => vm.ApplyLinks(legacy));
        vm = Create(false); legacy = vm.SuggestLinks();
        var fake = legacy[0] with { Reason = "not the reviewed record" };
        Reject("legacy ApplyLinks refuses externally forged suggestions", vm, () => vm.ApplyLinks([fake]));
        Check("legacy Suggest/Apply remains executable with a fresh valid review", vm.ApplyLinks(legacy) == 2);

        var project = Fixture(false); var first = ProjectQuery.DeviceList(project).First(); first.Name = "=1+1\tLine\nBreak";
        var room = new SpaceNode { Id = "R1", Name = "Actual room", Kind = SpaceKind.Room };
        project.Installations[0].Locations.AddRoot(room); room.AssignDevice(first);
        var devices = WorkbenchLabelMerge.Render(project, true); var groups = WorkbenchLabelMerge.Render(project, false);
        Check("device mail merge emits a BOM, fixed fields and real installation room", devices.StartsWith('\uFEFF')
            && devices.Contains("Id\tDescription\tIndividualAddress\tManufacturer\tProduct\tProgram\tRoom\tTrade\tImageName")
            && devices.Contains("'=1+1 Line Break", StringComparison.Ordinal) && devices.Contains("Actual room", StringComparison.Ordinal));
        Check("GA mail merge exports every current address with nine fields", groups.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3
            && groups.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(line => line.TrimEnd('\r').Split('\t').Length == 9));
        var export = Path.Combine(directory, "smart-merge.tsv"); File.WriteAllText(export, devices, new System.Text.UTF8Encoding(false));
        Check("mail merge is a reusable UTF-8/BOM data source", File.ReadAllBytes(export).Take(3).SequenceEqual(new byte[] { 239, 187, 191 })
            && File.ReadAllText(export).Contains("Actual room", StringComparison.Ordinal));
        var language = Strings.Language;
        try
        {
            foreach (var culture in Strings.Available)
            {
                Strings.Language = culture;
                var keys = new[] { "SmartTitle", "SmartHint", "SmartApply", "SmartRefresh", "SmartStale", "SmartUnknown", "MergeDevices", "MergeGroups" };
                Check("localized workflow labels in " + culture, keys.All(k => WorkbenchStrings.Get(k) != k && !string.IsNullOrWhiteSpace(WorkbenchStrings.Get(k)))
                    && !WorkbenchStrings.Get("SmartFound", 3, 2).Contains("{", StringComparison.Ordinal));
            }
        }
        finally { Strings.Language = language; }
        return failures;
    }

    internal static Project Fixture(bool includeInvalid = true)
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic smart linking", Areas = 1, LinesPerArea = 1 });
        var line = project.Installations[0].Areas[0].Lines[0];
        var range = new GroupRange { Id = "GR-smart", Name = "Synthetic", RangeStart = 1, RangeEnd = 65535 }; project.Installations[0].MainRanges.Add(range);
        range.Addresses.Add(new() { Id = "G1", Name = "Living light", Address = GroupAddress.ThreeLevel(1, 0, 1), DatapointType = "DPST-1-1" });
        range.Addresses.Add(new() { Id = "G2", Name = "Kitchen light", Address = GroupAddress.ThreeLevel(1, 0, 2), DatapointType = "DPST-1-1" });
        foreach (var group in range.Addresses) project.Workbench.Groups[group.Id] = new() { Bits = 1, Secure = false };
        for (var n = 1; n <= (includeInvalid ? 3 : 2); n++)
        {
            var device = new DeviceInstance { Id = "D" + n, Name = n == 1 ? "Living" : n == 2 ? "Kitchen" : "Garage", ProductRefId = "SYNTHETIC", Address = new(1, 1, (byte)n) };
            device.ComObjectRefs.Add(new() { Id = "O1", ComObjectId = "O1", Number = 1, Text = "light" }); line.Devices.Add(device);
            project.Workbench.Objects[new ObjectKey(device.Id, "O1").ToString()] = new() { Active = true, Bits = n == 3 ? 8 : 1, Dpt = "1.001", Flags = "CW" };
        }
        return project;
    }
}
