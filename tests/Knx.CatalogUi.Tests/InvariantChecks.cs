using Knx;
using Knx.App.Services;
using Knx.Bus;
using Knx.Catalog;
using Knx.Workbench;

internal static class InvariantChecks
{
    public static void Run()
    {
        var failures = new List<string>();
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failures.Add(name);
        }
        var vm = new MainWindowViewModel();
        var project = WorkbenchJson.CreateDemo();
        vm.CreateProject(project, null);
        var line = project.Installations[0].Areas[0].Lines[0];
        var choice = CatalogBrowser.Lines(project).First();
        var product = new CatalogProduct { Id = "fixture", Name = "Fixture" };
        CatalogBrowser.Insert(vm, product, null, choice, "1.1.200", null, out _);
        var first = ProjectQuery.FindDevice(project, IndividualAddress.Parse("1.1.200"))!;
        Check("C1 inspector readdresses without changing identity",
            vm.UpdateDeviceDetails(first, first.Name!, "1.1.201", line.Id, null) is null && first.Id == "1.1.200");
        CatalogBrowser.Insert(vm, product, null, choice, "1.1.200", null, out _);
        Check("C1 reinsert after readdress has unique IDs", ProjectQuery.DeviceList(project).Select(d => d.Id).Distinct().Count()
            == ProjectQuery.DeviceList(project).Count());
        var second = ProjectQuery.FindDevice(project, IndividualAddress.Parse("1.1.200"))!;
        Check("C1 collision uses the existing suffix strategy", second.Id == "1.1.200-2" && first.Id == "1.1.200");
        vm.UndoCommand.Execute(null);
        Check("C1 undo removes only the new identity", ProjectQuery.FindDevice(vm.Project!, IndividualAddress.Parse("1.1.200")) is null
            && ProjectQuery.FindDevice(vm.Project!, IndividualAddress.Parse("1.1.201"))?.Id == first.Id);
        vm.RedoCommand.Execute(null);
        project = vm.Project!;

        var device = ProjectQuery.DeviceList(project).First();
        var address = device.Address;
        var otherLine = new Line { Id = "other-line", Address = 15 };
        project.Installations[0].Areas[0].Lines.Add(otherLine);
        var before = Knxproj.ToXml(project).ToString();
        var error = vm.UpdateDeviceDetails(device, "Changed", address.ToString(), otherLine.Id, null);
        Check("C3 mismatched target line rejected atomically", error is not null && Knxproj.ToXml(project).ToString() == before);
        Check("C3 matching target address accepted", vm.UpdateDeviceDetails(device, "Changed", "1.15.200", otherLine.Id, null) is null
            && device.Address.ToString() == "1.15.200" && otherLine.Devices.Contains(device));

        var ga = ProjectQuery.GroupAddresses(project).First(g => g.DatapointType == "1.001");
        vm.RenameGroupAddress(new(ga.Address, ga.Name, ga.DatapointType, ga.Id), ga.Name, "DPST-9-1");
        Check("C5 inspector updates bit size", ga.DatapointType == "DPST-9-1" && project.Workbench.Groups[ga.Id].Bits == 16);
        Check("S4 canonical codec decode", Dpt.Decode("1.001", [1]) == Dpt.Decode("DPST-1-1", [1]));
        Check("S4 canonical codec encode", Dpt.Encode("5.001", "50%").SequenceEqual(Dpt.Encode("DPST-5-1", "50%")));
        Check("S4 canonical monitor unit", GroupMonitor.UnitFor("9.001") == "°C"
            && GroupMonitor.DecodeValue("5.001", [255]) == ("100", "%"));
        foreach (var (native, canonical) in new[] { ("DPST-1-1", "1.001"), ("DPST-1-2", "1.002"),
            ("DPST-1-10", "1.010"), ("DPST-1-11", "1.011"), ("DPST-5-1", "5.001"),
            ("DPST-5-3", "5.003"), ("DPST-9-1", "9.001"), ("DPST-9-7", "9.007") })
            Check("S4 codec equivalence " + native, Dpt.Decode(native, [1, 2]) == Dpt.Decode(canonical, [1, 2])
                && Dpt.Encode(native, "1").SequenceEqual(Dpt.Encode(canonical, "1")));
        Check("S4 unknown and main-only codecs retain hex fallback", Dpt.Decode("999.001", [10]) == "0A"
            && Dpt.Decode("DPT-1", [10]) == "0A" && GroupMonitor.UnitFor("999.001") == "");
        foreach (var unknown in new[] { "999.001", "DPST-9-garbage", "9..001", "DPST--9-1", "9.1.2", "", "0.001" })
            Check("S4 no size guess for " + unknown, !Dpt.TryBitSize(unknown, out _));
        Check("S4 DPT21 has its fixed one-byte size", Dpt.TryBitSize("DPST-21-1", out var statusBits) && statusBits == 8);
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
    }
}
