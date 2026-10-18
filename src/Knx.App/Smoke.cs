using Knx;
using Knx.App.Services;

namespace Knx.App;

/// <summary>
/// Headless UI smoke check: builds the window + view model, loads a round-tripped
/// knxproj, exercises the display pipelines and command guards.
/// </summary>
internal static class Smoke
{
    public static void Run()
    {
        // Keep the user's real settings, project fixtures and recent list untouched.
        var smokeRoot = Path.Combine(Path.GetTempPath(), "buschord-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(smokeRoot);
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(smokeRoot, "settings.json"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(smokeRoot, true); } catch (IOException) { }
        };

        var failures = new List<string>();
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
            if (!ok) failures.Add(name);
        }

        // 1. ViewModel instanziieren (XAML selbst wird beim Build durch Avalonia XAML-Compiler geprüft)
        var vm = new MainWindowViewModel();
        Check("MainWindowViewModel erzeugt", vm.Status == "Bereit");

        // 2. Mini-Projekt erzeugen + speichern + laden
        var project = new Project { Id = "P-1", Name = "Smoke" };
        var inst = new Installation { Id = "I-1", Name = "Haus" };
        var area = new Area { Id = "A-0", Address = 0, Name = "Innen" };
        var line = new Line { Id = "L-1", Address = 1, Name = "Linie" };
        var seg = new Segment { Id = "S-0", Address = 0, Name = "Seg" };
        seg.Devices.Add(new DeviceInstance
        {
            Id = "1.0.1", Address = new IndividualAddress(1, 0, 1), Name = "Aktor",
            ProductRefId = "M-00FA_H-Base_I-0000_A-0001-0E-1234",
        });
        line.Segments.Add(seg);
        area.Lines.Add(line);
        inst.Areas.Add(area);
        var main = new GroupRange { Id = "GR-1", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
        main.Addresses.Add(new GroupAddressEntry
        {
            Id = "GA-1", Name = "Wohnzimmer", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
        });
        main.Addresses.Add(new GroupAddressEntry
        {
            Id = "GA-2", Name = "Temperatur", Address = GroupAddress.ThreeLevel(1, 2, 1), DatapointType = "DPST-9-1",
        });
        inst.MainRanges.Add(main);
        project.Installations.Add(inst);

        var path = Path.Combine(smokeRoot, "smoke.knxproj");
        Knxproj.Save(project, path);
        vm.LoadProject(path);
        vm.SaveProject();
        Check("Gruppenadressen geladen", vm.GroupAddresses.Count == 2, vm.GroupAddresses.Count.ToString());
        Check("Geräte geladen", vm.Devices.Count == 1, vm.Devices.Count.ToString());
        Check("Projektname im VM", vm.Project?.Name == "Smoke");

        // 3. Command-Guards
        Check("Send deaktiviert ohne Bus", !vm.CanSend);
        Check("Download deaktiviert ohne Bus", !vm.CanDownload);
        vm.SelectedGroupAddress = vm.GroupAddresses[0];
        Check("Send mit Auswahl aber ohne Bus", !vm.CanSend);

        // 4. Monitor-Pipeline: eingelesenes Telegram landet im Monitor
        vm.SimulateTelegram(new Knx.Bus.BusTelegram
        {
            Telegram = new Knx.Bus.Telegram
            {
                Source = new IndividualAddress(1, 0, 5),
                Destination = new IndividualAddress(0x000B),
                IsGroupAddress = true,
                Apci = Knx.Bus.Apci.GroupValueWrite,
                Data = [0x01],
            },
            Source = "smoke",
        });
        Check("Telegram im Monitor", vm.Monitor.Count == 1);

        // 5. Parameter-Pipeline (KSXML → editierbares ViewModel)
        var types = new List<ParameterType>
        {
            new() { Id = "PT-1", Name = "Helligkeit", DataType = ParameterDataType.Number, Unit = "%" },
            new() { Id = "PT-2", Name = "Modus", DataType = ParameterDataType.Enumeration },
            new() { Id = "PT-3", Name = "Aktiv", DataType = ParameterDataType.Boolean },
            new() { Id = "PT-4", Name = "Temperatur", DataType = ParameterDataType.Float, Unit = "°C" },
        };
        types[1].Restrictions.Add(new ParameterRestriction { EnumerationValue = "Automatik" });
        types[1].Restrictions.Add(new ParameterRestriction { EnumerationValue = "Hand" });
        var instances = new List<ParameterInstance>
        {
            new() { Id = "PI-1", Name = "Helligkeit", ParameterTypeId = "PT-1", Value = "128" },
            new() { Id = "PI-2", Name = "Modus", ParameterTypeId = "PT-2", Value = "Hand" },
            new() { Id = "PI-3", Name = "Aktiv", ParameterTypeId = "PT-3", Value = "1" },
            new() { Id = "PI-4", Name = "Temperatur", ParameterTypeId = "PT-4", Value = "21.5" },
        };
        var changed = 0;
        var rows = Knx.App.Services.ParameterItemViewModel.Build(instances, types, () => changed++);
        Check("Parameter: 4 Zeilen", rows.Length == 4, rows.Length.ToString());
        Check("Parameter: Number-Kind", rows[0].Kind == Knx.App.Services.ParameterKind.Number);
        Check("Parameter: Enumeration-Kind", rows[1].Kind == Knx.App.Services.ParameterKind.Enumeration);
        Check("Parameter: Boolean-Kind", rows[2].Kind == Knx.App.Services.ParameterKind.Boolean);
        Check("Parameter: Float-Kind", rows[3].Kind == Knx.App.Services.ParameterKind.Float);
        Check("Parameter: Enum-Choices", rows[1].Choices.Count == 2, rows[1].Choices.Count.ToString());
        Check("Parameter: Boolean checked", rows[2].IsChecked);
        rows[0].Value = "200";
        Check("Parameter: Änderung propagiert", instances[0].Value == "200" && changed == 1,
            $"value={instances[0].Value}, changed={changed}");
        rows[2].IsChecked = false;
        Check("Parameter: CheckBox schreibt 0", instances[2].Value == "0", instances[2].Value);

        // 6. Neue Bearbeitungs-Pipeline: Undo/Redo, Prüfung, Verknüpfung, Recorder
        var vm2 = new MainWindowViewModel();
        var smokeProject = new Project { Id = "P-2", Name = "Edit" };
        var smokeInstallation = new Installation { Id = "I-1", Name = "Haus" };
        var smokeArea = new Area { Id = "A-0", Address = 1, Name = "Erdgeschoss" };
        var line1 = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
        var line2 = new Line { Id = "L-2", Address = 2, Name = "Linie 2" };
        var actuator = new DeviceInstance
        {
            Id = "1.1.1", Address = new IndividualAddress(1, 1, 1), Name = "Schaltaktor Wohnzimmer",
            ProductRefId = "M-00FA_A-0001-0E-1234",
        };
        actuator.ComObjectRefs.Add(new ComObjectInstanceRef { Id = "CO-1", ComObjectId = "CO-1", Text = "Schalten Wohnzimmer" });
        line1.Devices.Add(actuator);
        smokeArea.Lines.Add(line1);
        smokeArea.Lines.Add(line2);
        smokeInstallation.Areas.Add(smokeArea);
        var smokeRange = new GroupRange { Id = "GR-1", Name = "Licht", RangeStart = 1, RangeEnd = 1 };
        smokeRange.Addresses.Add(new GroupAddressEntry
        {
            Id = "GA-1", Name = "Wohnzimmer Schalten", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
        });
        smokeInstallation.MainRanges.Add(smokeRange);
        smokeProject.Installations.Add(smokeInstallation);

        var smokePath = Path.Combine(smokeRoot, "smoke_edit.knxproj");
        Knxproj.Save(smokeProject, smokePath);
        vm2.LoadProject(smokePath);
        Check("Tree: Installation als Wurzel", vm2.Tree.Count == 1 && vm2.Tree[0].Children.Count == 1);
        Check("Undo zunächst leer", !vm2.CanUndo);

        var before = vm2.GroupAddresses.Count;
        vm2.AddGroupAddress();
        Check("Gruppenadresse angelegt", vm2.GroupAddresses.Count == before + 1, vm2.GroupAddresses.Count.ToString());
        Check("Undo verfügbar", vm2.CanUndo);
        vm2.UndoCommand.Execute(null);
        Check("Undo stellt Anzahl wieder her", vm2.GroupAddresses.Count == before, vm2.GroupAddresses.Count.ToString());
        vm2.RedoCommand.Execute(null);
        Check("Redo legt wieder an", vm2.GroupAddresses.Count == before + 1);

        var findings = vm2.RunCheck();
        Check("Prüfung findet Befunde", findings.Count > 0, findings.Count.ToString());
        Check("Prüfung: unverknüpfte Gruppenadresse (P042)",
            findings.Any(f => f.Code == "P042"), string.Join(",", findings.Select(f => f.Code)));

        var suggestions = vm2.SuggestLinks();
        Check("Intelligente Verknüpfung schlägt vor", suggestions.Count > 0, suggestions.Count.ToString());
        if (suggestions.Count > 0)
        {
            Check("Vorschlag trifft Wohnzimmer-GA", suggestions[0].GroupAddress.Name.Contains("Wohnzimmer"),
                suggestions[0].GroupAddress.Name);
            vm2.ApplyLinks(suggestions);
            Check("Verknüpfung angelegt", actuator.ComObjectRefs[0].Links.Count == 0 ||
                vm2.Project!.Installations[0].Areas[0].Lines[0].Devices[0].ComObjectRefs[0].Links.Count == 1,
                actuator.ComObjectRefs[0].Links.Count.ToString());
        }

        vm2.StartRecording();
        vm2.SimulateTelegram(new Knx.Bus.BusTelegram
        {
            Telegram = new Knx.Bus.Telegram
            {
                Source = new IndividualAddress(1, 1, 1),
                Destination = new IndividualAddress(GroupAddress.ThreeLevel(1, 1, 1).Raw),
                IsGroupAddress = true,
                Apci = Knx.Bus.Apci.GroupValueWrite,
                Data = [0x01],
            },
            Source = "smoke",
        });
        vm2.StopRecording();
        Check("Recorder aufgezeichnet", vm2.Recorder.Count == 1, vm2.Recorder.Count.ToString());

        var recordingPath = Path.Combine(smokeRoot, "recording.jsonl");
        vm2.Recorder.SaveJsonLines(recordingPath);
        var reloaded = Knx.Bus.TelegramRecorder.Load(recordingPath);
        Check("Recorder Roundtrip", reloaded.Count == 1 && reloaded[0].Apci == "GroupValueWrite",
            $"{reloaded.Count} / {(reloaded.Count > 0 ? reloaded[0].Apci : "-")}");

        Check("Dashboard gebaut",
            vm2.Stats is { Devices: 1 } stats && stats.GroupAddresses == vm2.GroupAddresses.Count,
            vm2.Stats is null ? "null" : $"{vm2.Stats.Devices}/{vm2.Stats.GroupAddresses} vs {vm2.GroupAddresses.Count}");

        // Selection must follow the current project after history restores model instances.
        vm2.SelectedDevice = vm2.Devices[0];
        Check("Device properties show editable name", vm2.EditName == vm2.SelectedDevice.Name);
        vm2.SelectedGroupAddress = vm2.GroupAddresses[0];
        Check("Group selection clears device properties", vm2.SelectedDevice is null
            && vm2.EditName == vm2.SelectedGroupAddress.Name);
        vm2.SelectedDevice = vm2.Devices[0];
        Check("Device selection clears group properties", vm2.SelectedGroupAddress is null);
        vm2.EditName = "Renamed device";
        vm2.ApplyDetailsEdit();
        vm2.UndoCommand.Execute(null);
        Check("Undo rebinds device selection to restored project", vm2.SelectedDevice is not null
            && ReferenceEquals(vm2.SelectedDevice.Device,
                ProjectQuery.DeviceList(vm2.Project!).Single()));
        Check("Undo refreshes editable device name", vm2.SelectedDevice is not null
            && vm2.EditName == vm2.SelectedDevice.Name && vm2.EditName != "Renamed device");

        vm2.LoadProject(smokePath);
        Check("Opening another project clears previous selection", vm2.SelectedDevice is null
            && vm2.SelectedGroupAddress is null && vm2.SelectedTreeNode is null);
        vm2.SelectedGroupAddress = vm2.GroupAddresses[0];
        vm2.CreateProject(Knxproj.Load(smokePath), null);
        Check("Creating a project clears previous group selection", vm2.SelectedGroupAddress is null);

        var linkDevice = vm2.Devices.Single().Device;
        var objId = linkDevice.ComObjectRefs.Single().Id;
        var gaId = vm2.GroupAddresses[0].Id;
        linkDevice.ComObjectRefs.Single().Links.Clear();
        vm2.CreateProject(vm2.Project!, null);
        Check("Manual GUI link adds once", vm2.ChangeGroupLink(linkDevice, objId, gaId, false)
            && !vm2.ChangeGroupLink(linkDevice, objId, gaId, false));
        vm2.UndoCommand.Execute(null);
        Check("Manual GUI link supports undo", vm2.Devices.Single().Device.ComObjectRefs.Single().Links.Count == 0);
        vm2.RedoCommand.Execute(null);
        var currentDevice = vm2.Devices.Single().Device;
        Check("Manual GUI link supports redo", currentDevice.ComObjectRefs.Single().Links.Contains(gaId));
        Check("Manual GUI unlink removes once", vm2.ChangeGroupLink(currentDevice, objId, gaId, true)
            && !vm2.ChangeGroupLink(currentDevice, objId, gaId, true));

        // 7. Startbildschirm, Statuszeile und Enablement (kein stiller No-Op mehr)
        Check("Statuszeile nennt Projektdatei statt Vollpfad",
            vm.ProjectLabel == Path.GetFileName(path) && !vm.ProjectLabel.Contains('\\')
            && vm.ProjectTooltip == path, $"{vm.ProjectLabel} / {vm.ProjectTooltip}");

        var cold = new MainWindowViewModel();
        Check("Kaltstart ohne Projekt", !cold.HasProject && !cold.HasDevices && !cold.HasGroupAddresses
            && !cold.HasSelection && cold.ProjectLabel.Length == 0 && cold.ProjectTooltip.Length == 0);
        Check("Kaltstart zeigt Recents aus den Einstellungen",
            cold.RecentProjects.Count == cold.Settings.RecentProjects.Count);
        cold.AddGroupAddress();
        Check("Gruppenadresse ohne Projekt nennt UxNeedsProject",
            cold.Status == Strings.Get("UxNeedsProject"), cold.Status);
        cold.RunCheck();
        Check("Prüfung ohne Projekt nennt UxNeedsProject",
            cold.Status == Strings.Get("UxNeedsProject") && !cold.HasFindings, cold.Status);
        cold.SuggestLinks();
        Check("Verknüpfen ohne Projekt nennt UxNeedsProject", cold.Status == Strings.Get("UxNeedsProject"));
        cold.Settings.BusInterface = "";
        cold.DiscoverCommand.Execute(null);
        Check("Suchen ohne Schnittstelle nennt UxNeedsInterface",
            cold.Status == Strings.Get("UxNeedsInterface") && cold.BusInterfaceHint == Strings.Get("UxNeedsInterface"),
            cold.Status);
        cold.ConnectCommand.Execute(null);
        Check("Verbinden ohne Schnittstelle nennt UxNeedsInterface",
            cold.Status == Strings.Get("UxNeedsInterface"), cold.Status);

        var empty = new MainWindowViewModel();
        empty.CreateProject(new Project { Id = "P-3", Name = "Leer" }, null);
        Check("Projekt ohne Geräte/Gruppenadressen zeigt beide Empty-States",
            empty.HasProject && !empty.HasDevices && !empty.HasGroupAddresses);
        Check("Leeres Projekt hat keine Befunde", !empty.HasFindings);

        Console.WriteLine(failures.Count == 0 ? "SMOKE OK" : $"SMOKE {failures.Count} FEHLER");
        Environment.Exit(failures.Count == 0 ? 0 : 1);
    }
}
