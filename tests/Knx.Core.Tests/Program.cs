using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Knx;
using Knx.Portfolio;

// Self-Check für den Knxproj-Kern: Round-Trip (erzeugen → speichern → laden → vergleichen)
// und Auswertung der synthetischen Master-Data. Lauf: dotnet run --project tests/Knx.Core.Tests
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

// --- Testprojekt: 1 Area, 1 Line mit Segment, Gerät, Main/Mid-Range mit GAs ---
var project = new Project
{
    Id = "P-0000000001",
    Name = "Testprojekt",
    Comment = "Selbsttest",
    Guid = "{4E5A0A7A-1111-2222-3333-444455556666}",
    ProjectStart = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(2)),
    MasterData = { Version = 198 },
};
var installation = new Installation { Id = "I-0000000001", Name = "Mein Haus", Address = new IndividualAddress(0, 0, 0) };
var area = new Area { Id = "A-0", Address = 0, Name = "Innenbereich" };
var line = new Line { Id = "L-1", Address = 1, Name = "Hauptlinie", MediumTypeRefId = "MT-0" };
var segment = new Segment { Id = "S-0", Address = 0, Name = "Standardsegment" };
segment.Devices.Add(new DeviceInstance
{
    Id = "1.0.1",
    Address = new IndividualAddress(1, 0, 1),
    Name = "Schaltaktor 8-fach",
    ProductRefId = "M-00FA_H-Base_I-0000_A-0001-0E-1234",
    Hardware2ProgramRefId = "M-00FA_H-Base_I-0000_A-0001-0E-1234",
    Description = "Aktor im Schaltschrank",
    Comment = "beim Service tauschen",
});
line.Segments.Add(segment);
area.Lines.Add(line);
installation.Areas.Add(area);

var main = new GroupRange { Id = "GR-0000000001", Name = "Beleuchtung", RangeStart = 1, RangeEnd = 15 };
var mid = new GroupRange { Id = "GR-0000000002", Name = "Erdgeschoss", RangeStart = 1, RangeEnd = 128 };
mid.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-0000000001", Name = "Wohnzimmer Decke", Description = "Lampe am Fenster",
    Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1", Puid = 42,
});
mid.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-0000000002", Name = "Küche",
    Address = GroupAddress.ThreeLevel(1, 1, 2), DatapointType = "DPST-1-1",
});
main.Ranges.Add(mid);
installation.MainRanges.Add(main);
project.Installations.Add(installation);

// --- Round-Trip über echte ZIP-Datei ---
var tmp = Path.Combine(Path.GetTempPath(), "knx_selftest", "roundtrip.knxproj");
Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
Knxproj.Save(project, tmp);
var loaded = Knxproj.Load(tmp);

Check("Projekt-Id", loaded.Id == project.Id, loaded.Id);
Check("Projekt-Name", loaded.Name == project.Name);
Check("MasterData-Version", loaded.MasterData.Version == 198u, loaded.MasterData.Version.ToString());
Check("ProjectStart round-trip", loaded.ProjectStart == project.ProjectStart);
Check("Installations-Anzahl", loaded.Installations.Count == 1);
var li = loaded.Installations[0];
Check("Line-Name", li.Areas[0].Lines[0].Name == "Hauptlinie");
Check("Segment/Device", li.Areas[0].Lines[0].Segments[0].Devices[0].Name == "Schaltaktor 8-fach");
Check("Device-Adresse", li.Areas[0].Lines[0].Segments[0].Devices[0].Address == new IndividualAddress(1, 0, 1));
Check("GroupRange-Name", li.MainRanges[0].Name == "Beleuchtung");
Check("Nested Range", li.MainRanges[0].Ranges[0].Name == "Erdgeschoss");
var gas = li.MainRanges[0].Ranges[0].Addresses;
Check("GA-Anzahl", gas.Count == 2, gas.Count.ToString());
Check("GA-Adresse", gas[0].Address == GroupAddress.ThreeLevel(1, 1, 1), gas[0].Address.ToString());
Check("GA-DatapointType", gas[0].DatapointType == "DPST-1-1");
Check("GA-Beschreibung", gas[0].Description == "Lampe am Fenster");
Check("GA-Puid", gas[0].Puid == 42);

// Value-Type-Checks
Check("Address.Format", new IndividualAddress(1, 2, 3).ToString() == "1.2.3");
Check("GA.Format", GroupAddress.ThreeLevel(2, 3, 4).ToString() == "2/3/4");

// --- synthetische Master-Data (tests\Knx.Core.Tests\fixtures\knx_master.xml) ---
var catalog = Path.Combine(AppContext.BaseDirectory, "fixtures", "knx_master.xml");
if (File.Exists(catalog))
{
    var (version, types) = MasterData.LoadDatapointTypes(catalog);
    var (manufacturers, _) = MasterData.CountCatalogEntries(catalog);
    Console.WriteLine($"  Katalog-Version {version}: {types.Count} Datapoint-Typen, {manufacturers} Hersteller");
    Check("Master-Data-Version", version == 1u, version.ToString());
    Check("DPT-Anzahl", types.Count == 4, types.Count.ToString());
    Check("Hersteller-Anzahl", manufacturers == 3, manufacturers.ToString());
    var sw = types.FirstOrDefault(t => t.Name == "1.xxx");
    Check("DPT 1.xxx hat 8 Subtypen", sw?.Subtypes.Count == 8, sw?.Subtypes.Count.ToString());
    Check("DPT 1.xxx SizeInBit", sw?.SizeInBit == 1, sw?.SizeInBit.ToString());
    Check("DPST-1-1 Switch", sw?.Subtypes.Any(s => s is { Name: "DPT_Switch", Id: "DPST-1-1", SizeInBit: 1 }) == true);
    var temp = types.FirstOrDefault(t => t.Id == "DPT-9");
    Check("DPT 9.xxx Subtyp", temp?.Subtypes.SingleOrDefault()?.Name == "DPT_Value_Temp", temp?.Subtypes.Count.ToString());
}
else Console.WriteLine("  SKIP Katalog-Check (fixtures/knx_master.xml nicht gefunden)");

// --- ETS6-Projektarchiv-Pfadauflösung für Geräte-XML (KSXML) ---
Check("RefId M-00FA_H-Base_I-0000_A-0001-0E-1234",
    Knxproj.ApplicationProgramEntryPath("M-00FA_H-Base_I-0000_A-0001-0E-1234") == "M-00FA/M-00FA_A-0001-0E-1234.xml",
    Knxproj.ApplicationProgramEntryPath("M-00FA_H-Base_I-0000_A-0001-0E-1234"));
Check("RefId M-0083_A-008A-25-6CE4",
    Knxproj.ApplicationProgramEntryPath("M-0083_A-008A-25-6CE4") == "M-0083/M-0083_A-008A-25-6CE4.xml");
Check("RefId M-0001_A-9064-01-B1BA-O0001",
    Knxproj.ApplicationProgramEntryPath("M-0001_A-9064-01-B1BA-O0001") == "M-0001/M-0001_A-9064-01-B1BA-O0001.xml");
Check("RefId ungültig", Knxproj.ApplicationProgramEntryPath("kaputt") is null);
Check("RefId leer", Knxproj.ApplicationProgramEntryPath("") is null);

// --- KSXML-Parser gegen ein echtes Parameter-XML-Fragment ---
{
    var ksxml = System.Xml.Linq.XDocument.Parse("""
<ApplicationProgram xmlns="http://knx.org/xml/project/23">
  <Static>
    <Parameters>
      <ParameterTypes>
        <ParameterType Id="PT-1" Name="Brightness" DataType="Number" SizeInBit="8" UIHint="Slider" Unit="%">
          <TypeRestrictions>
            <TypeRestriction Id="PT-1_R-1" Value="0"/>
            <TypeRestriction Id="PT-1_R-2" Value="255"/>
          </TypeRestrictions>
        </ParameterType>
        <ParameterType Id="PT-2" Name="Mode" DataType="Enumeration" SizeInBit="8" UIHint="ComboBox"/>
      </ParameterTypes>
    </Parameters>
  </Static>
  <Dynamic>
    <ParameterInstances>
      <ParameterInstance Id="PI-1" Name="Helligkeit" ParameterTypeId="PT-1" Value="128" Row="0" Column="1"/>
      <ParameterInstance Id="PI-2" Name="Modus" ParameterTypeId="PT-2" Value="1" Row="1" Column="1"/>
    </ParameterInstances>
  </Dynamic>
  <ComObjects>
    <ComObject Id="CO-1" Name="Schalten" Number="1" SizeInBit="1" DatapointType="DPST-1-1"/>
  </ComObjects>
</ApplicationProgram>
""");
    var (types, blocks, instances, comObjects, comObjectRefs) = Ksxml.Parse(ksxml);
    Check("KSXML: 2 Parametertypen", types.Count == 2, types.Count.ToString());
    Check("KSXML: Brightness-Typ", types[0] is { Id: "PT-1", DataType: ParameterDataType.Number, Unit: "%" });
    Check("KSXML: Enumeration-Typ", types[1].DataType == ParameterDataType.Enumeration);
    Check("KSXML: 2 Instanzen", instances.Count == 2, instances.Count.ToString());
    Check("KSXML: Instanz-Wert 128", instances[0].Value == "128");
    Check("KSXML: Instanz-Ref", instances[0].ParameterTypeId == "PT-1");
    Check("KSXML: ComObject", comObjects.Count == 1 && comObjects[0].SizeInBit == 1);
}

// --- Testdaten-Bausteine für die Kernmodul-Checks ---
static DeviceInstance Dev(string address, string? name = null, string product = "M-0001_A-0001")
    => new()
    {
        Id = "D-" + address,
        Address = IndividualAddress.Parse(address),
        Name = name ?? "Gerät " + address,
        ProductRefId = product,
        Hardware2ProgramRefId = "M-0001_H-0001",
    };

static ComObjectInstanceRef ComRef(string id, string? text, params string[] links)
{
    var comObject = new ComObjectInstanceRef { Id = id, ComObjectId = id, Text = text };
    comObject.Links.AddRange(links);
    return comObject;
}

static GroupAddressEntry Ga(string id, string name, int mainNo, int middleNo, int subNo, string? dpt = "DPST-1-1")
    => new()
    {
        Id = id,
        Name = name,
        Address = GroupAddress.ThreeLevel((byte)mainNo, (byte)middleNo, (byte)subNo),
        DatapointType = dpt,
    };

// Ein Bereich mit einer Linie; Geräte hängen direkt an der Linie.
static Project BuildProject(string id, string name, params DeviceInstance[] devices)
{
    var prj = new Project { Id = id, Name = name };
    var house = new Installation { Id = "I-" + id, Name = "Haus" };
    var floor = new Area { Id = "A-0", Address = 0, Name = "EG" };
    var busLine = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
    busLine.Devices.AddRange(devices);
    floor.Lines.Add(busLine);
    house.Areas.Add(floor);
    prj.Installations.Add(house);
    return prj;
}

// Haupt-/Mittelbereich mit den übergebenen Gruppenadressen.
static GroupRange AddMainRange(Project prj, string name, int mainNo, params GroupAddressEntry[] addresses)
{
    var root = new GroupRange { Id = $"GR-{mainNo}", Name = name, RangeStart = mainNo, RangeEnd = 2047 };
    var middle = new GroupRange { Id = $"GR-{mainNo}-1", Name = $"Mittelbereich {mainNo}", RangeStart = 1, RangeEnd = 255 };
    middle.Addresses.AddRange(addresses);
    root.Ranges.Add(middle);
    prj.Installations[0].MainRanges.Add(root);
    return middle;
}

// --- ProjectQuery: Traversierung, Suche und freie Adressen ---
{
    var qp = new Project { Id = "P-Q", Name = "Abfrage" };
    var qInst = new Installation { Id = "I-Q", Name = "Haus", Address = new IndividualAddress(1, 1, 0) };
    var qArea = new Area { Id = "A-0", Address = 0, Name = "EG" };
    qArea.Devices.Add(Dev("1.0.1", "Bereichsgerät"));
    var qLine1 = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
    qLine1.Devices.Add(Dev("1.1.2", "Liniengerät"));
    var qSeg = new Segment { Id = "S-0", Address = 0, Name = "Segment" };
    qSeg.Devices.Add(Dev("1.1.3", "Segmentgerät"));
    qLine1.Segments.Add(qSeg);
    qArea.Lines.Add(qLine1);
    var qLine2 = new Line { Id = "L-2", Address = 2, Name = "Linie 2" };
    qLine2.Devices.Add(Dev("1.2.4", "Zweites Gerät"));
    qArea.Lines.Add(qLine2);
    qInst.Areas.Add(qArea);

    var qMain = new GroupRange { Id = "GR-1", Name = "Haupt 1", RangeStart = 1, RangeEnd = 2047 };
    var qMid = new GroupRange { Id = "GR-2", Name = "Mittel 1", RangeStart = 1, RangeEnd = 255 };
    qMid.Addresses.Add(Ga("GA-1", "Wohnzimmer Decke", 1, 1, 1));
    var qDeep = new GroupRange { Id = "GR-3", Name = "Unter 1", RangeStart = 2, RangeEnd = 255 };
    qDeep.Addresses.Add(Ga("GA-2", "Küche", 1, 1, 2));
    qMid.Ranges.Add(qDeep);
    qMain.Ranges.Add(qMid);
    qInst.MainRanges.Add(qMain);
    qp.Installations.Add(qInst);

    var qEntries = ProjectQuery.Devices(qp).ToList();
    Check("ProjectQuery: Gerätereihenfolge Bereich/Linie/Segment",
        string.Join(",", qEntries.Select(e => e.Device.Address)) == "1.0.1,1.1.2,1.1.3,1.2.4",
        string.Join(",", qEntries.Select(e => e.Device.Address)));
    Check("ProjectQuery: Bereichsgerät ohne Linie", qEntries[0].Line is null && qEntries[0].Segment is null);
    Check("ProjectQuery: Segmentgerät in Linie und Segment",
        qEntries[2].Line?.Address == 1 && qEntries[2].Segment?.Address == 0);
    Check("ProjectQuery: Installation und Bereich",
        qEntries[3].Installation.Id == "I-Q" && qEntries[3].Area.Address == 0);
    Check("ProjectQuery: DeviceList", ProjectQuery.DeviceList(qp).Count() == 4);
    Check("ProjectQuery: GroupAddresses rekursiv",
        string.Join(",", ProjectQuery.GroupAddresses(qp).Select(g => g.Id)) == "GA-1,GA-2",
        string.Join(",", ProjectQuery.GroupAddresses(qp).Select(g => g.Id)));
    Check("ProjectQuery: AllRanges", ProjectQuery.AllRanges(qInst).Count() == 3);
    Check("ProjectQuery: FindDevice", ProjectQuery.FindDevice(qp, new IndividualAddress(1, 1, 3))?.Name == "Segmentgerät");
    Check("ProjectQuery: FindDevice unbekannt", ProjectQuery.FindDevice(qp, new IndividualAddress(1, 9, 9)) is null);
    var qGa1 = ProjectQuery.FindGroupAddress(qp, GroupAddress.ThreeLevel(1, 1, 1));
    Check("ProjectQuery: FindGroupAddress", qGa1?.Id == "GA-1");
    Check("ProjectQuery: FindGroupAddressById", ProjectQuery.FindGroupAddressById(qp, "GA-2")?.Name == "Küche");
    Check("ProjectQuery: FindGroupAddressById unbekannt", ProjectQuery.FindGroupAddressById(qp, "GA-999") is null);
    Check("ProjectQuery: FindRangeOf", qGa1 is not null && ProjectQuery.FindRangeOf(qp, qGa1)?.Id == "GR-2");

    Check("ProjectQuery: nächste Geräteadresse",
        ProjectQuery.NextFreeDeviceAddress(qp, 1, 0) == new IndividualAddress(1, 0, 2));
    Check("ProjectQuery: nächste Geräteadresse überspringt 0",
        ProjectQuery.NextFreeDeviceAddress(qp, 1, 1) == new IndividualAddress(1, 1, 1),
        ProjectQuery.NextFreeDeviceAddress(qp, 1, 1).ToString());
    Check("ProjectQuery: nächste Geräteadresse freie Linie",
        ProjectQuery.NextFreeDeviceAddress(qp, 1, 2) == new IndividualAddress(1, 2, 1));
    Check("ProjectQuery: nächste Gruppenadresse",
        ProjectQuery.NextFreeGroupAddress(qp, 1, 1) == GroupAddress.ThreeLevel(1, 1, 3),
        ProjectQuery.NextFreeGroupAddress(qp, 1, 1).ToString());
    Check("ProjectQuery: nächste Gruppenadresse ab Start",
        ProjectQuery.NextFreeGroupAddress(qp, 1, 1, 5) == GroupAddress.ThreeLevel(1, 1, 5));

    Check("ProjectQuery: RemoveDevice Bereichsgerät", ProjectQuery.RemoveDevice(qp, qEntries[0].Device));
    Check("ProjectQuery: RemoveDevice Liniengerät", ProjectQuery.RemoveDevice(qp, qEntries[1].Device));
    Check("ProjectQuery: RemoveDevice unbekannt", !ProjectQuery.RemoveDevice(qp, qEntries[0].Device));
    Check("ProjectQuery: DeviceList nach Entfernen", ProjectQuery.DeviceList(qp).Count() == 2);

    var qFresh = Dev("1.1.9", "Neues Gerät");
    qFresh.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1", "GA-2"));
    qFresh.ComObjectRefs.Add(ComRef("CO-2", "Status", "GA-2"));
    ProjectQuery.AddToLine(qLine1, qFresh);
    Check("ProjectQuery: AddToLine nutzt erstes Segment",
        qLine1.Segments[0].Devices.Contains(qFresh) && !qLine1.Devices.Contains(qFresh));
    var qFresh2 = Dev("1.2.9", "Zweites neues Gerät");
    ProjectQuery.AddToLine(qLine2, qFresh2);
    Check("ProjectQuery: AddToLine ohne Segment", qLine2.Devices.Contains(qFresh2));

    var qRefs = ProjectQuery.ComObjectRefs(qp).ToList();
    Check("ProjectQuery: ComObjectRefs", qRefs.Count == 2, qRefs.Count.ToString());
    Check("ProjectQuery: ComObjectRefs Linkreihenfolge",
        qRefs[0].Device == qFresh && qRefs[0].Ref.Links.SequenceEqual(["GA-1", "GA-2"]));
}

// --- UndoStack: Reset/Push/Undo/Redo, No-op, Redo-Verwerfen, Kapazität ---
{
    var uProject = new Project { Id = "P-U", Name = "Undo" };
    var uInst = new Installation { Id = "I-U", Name = "Haus" };
    uInst.Areas.Add(new Area { Id = "A-0", Address = 0, Name = "EG" });
    uProject.Installations.Add(uInst);

    var uStack = new UndoStack();
    uStack.Reset(uProject);
    Check("Undo: Reset ergibt Ausgangsstand",
        uStack.Steps == 0 && uStack.Index == 0 && !uStack.CanUndo && !uStack.CanRedo);

    uProject.Name = "Schritt 1";
    uStack.Push(uProject);
    Check("Undo: ein Schritt", uStack.Steps == 1 && uStack.CanUndo && !uStack.CanRedo);

    uProject.Name = "Schritt 2";
    uStack.Push(uProject);
    var uBack = uStack.Undo();
    Check("Undo: zurück auf Schritt 1", uBack?.Name == "Schritt 1", uBack?.Name);
    Check("Undo: Redo verfügbar", uStack.CanRedo);
    var uForward = uStack.Redo();
    Check("Redo: wieder Schritt 2", uForward?.Name == "Schritt 2", uForward?.Name);

    uStack.Push(uProject);
    Check("Undo: No-op-Push auf unverändertem Projekt zählt nicht", uStack.Steps == 2, uStack.Steps.ToString());

    uStack.Undo();
    uProject.Name = "Schritt 3";
    uStack.Push(uProject);
    Check("Undo: neuer Push verwirft Redo", !uStack.CanRedo && uStack.Redo() is null);
    Check("Undo: neue Spitze", uStack.Steps == 2, uStack.Steps.ToString());

    var uLimited = new UndoStack(capacity: 3);
    var uProject2 = new Project { Id = "P-U2", Name = "v0" };
    uLimited.Reset(uProject2);
    for (var i = 1; i <= 4; i++)
    {
        uProject2.Name = "v" + i;
        uLimited.Push(uProject2);
    }
    Check("Undo: Kapazität begrenzt", uLimited.Capacity == 3 && uLimited.Steps == 2, uLimited.Steps.ToString());
    Check("Undo: ältester Stand verworfen", uLimited.Undo()?.Name == "v3");
    Check("Undo: nur zwei Schritte zurück", uLimited.Undo()?.Name == "v2" && uLimited.Undo() is null);
}

// --- ProjectCheck: doppelte Adressen, fehlende DPTs, hängende und fehlende Links ---
{
    var cP = new Project { Id = "P-C", Name = "Prüfprojekt" };
    var cInst = new Installation { Id = "I-C", Name = "Haus" };
    var cArea = new Area { Id = "A-16", Address = 16, Name = "Bereich 16" };
    var cLine = new Line { Id = "L-16", Address = 16, Name = "Linie 16" };
    var cSeg = new Segment { Id = "S-0", Address = 0 };

    var cD1 = Dev("1.1.1", "Aktor A");
    cD1.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1", "GA-2", "GA-999"));
    var cD2 = Dev("1.1.1", "Aktor B");
    var cD3 = Dev("1.1.0", "Gerät ohne Adresse");
    cSeg.Devices.Add(cD1);
    cSeg.Devices.Add(cD2);
    cSeg.Devices.Add(cD3);
    cLine.Segments.Add(cSeg);
    cArea.Lines.Add(cLine);
    cInst.Areas.Add(cArea);

    var cMain = new GroupRange { Id = "GR-1", Name = "Haupt 1", RangeStart = 0, RangeEnd = 2047 };
    var cMid = new GroupRange { Id = "GR-2", Name = "Mittel 1", RangeStart = 0, RangeEnd = 255 };
    cMid.Addresses.Add(Ga("GA-1", "Wohnzimmer Decke", 1, 1, 1));
    cMid.Addresses.Add(Ga("GA-2", "Wohnzimmer Decke doppelt", 1, 1, 1));
    cMid.Addresses.Add(Ga("GA-3", "Küche ohne DPT", 1, 1, 2, null));
    cMid.Addresses.Add(Ga("GA-4", "Flur", 1, 1, 3));
    cMain.Ranges.Add(cMid);
    cInst.MainRanges.Add(cMain);
    cP.Installations.Add(cInst);

    var cFindings = ProjectCheck.Run(cP);
    Check("Check: P010 Bereichsadresse Error",
        cFindings.Any(f => f.Code == "P010" && f.Severity == CheckSeverity.Error));
    Check("Check: P011 Linienadresse Error",
        cFindings.Any(f => f.Code == "P011" && f.Severity == CheckSeverity.Error));
    Check("Check: P020 Geräteadresse 0 Error",
        cFindings.Any(f => f.Code == "P020" && f.Severity == CheckSeverity.Error));
    Check("Check: P025 doppelte Geräteadresse Error",
        cFindings.Any(f => f.Code == "P025" && f.Severity == CheckSeverity.Error));
    Check("Check: P030 fehlender Datapoint-Typ Warning",
        cFindings.Any(f => f.Code == "P030" && f.Severity == CheckSeverity.Warning));
    Check("Check: P032 doppelte Gruppenadresse Error",
        cFindings.Any(f => f.Code == "P032" && f.Severity == CheckSeverity.Error));
    Check("Check: P041 hängender Link Error",
        cFindings.Any(f => f.Code == "P041" && f.Severity == CheckSeverity.Error));
    Check("Check: P042 unverknüpfte Gruppenadresse Info",
        cFindings.Count(f => f.Code == "P042" && f.Severity == CheckSeverity.Info) == 2,
        string.Join(",", cFindings.Where(f => f.Code == "P042").Select(f => f.Path)));
    Check("Check: Fehleranzahl", ProjectCheck.Count(cFindings, CheckSeverity.Error) == 6,
        string.Join(",", cFindings.Where(f => f.Severity == CheckSeverity.Error).Select(f => f.Code)));

    var cCleanDevice = Dev("1.1.1", "Aktor");
    cCleanDevice.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1"));
    var cClean = BuildProject("P-Clean", "Sauber", cCleanDevice);
    AddMainRange(cClean, "Haupt 1", 1, Ga("GA-1", "Wohnzimmer Decke", 1, 1, 1));
    var cCleanFindings = ProjectCheck.Run(cClean);
    Check("Check: sauberes Projekt ohne Error",
        cCleanFindings.All(f => f.Severity != CheckSeverity.Error),
        string.Join(",", cCleanFindings.Select(f => f.Code)));
    Check("Check: sauberes Projekt ohne Befund", cCleanFindings.Count == 0,
        string.Join(",", cCleanFindings.Select(f => f.Code + ": " + f.Message)));
}

// --- SmartLinking: Vorschlag aus Namens-Tokens, Apply idempotent ---
{
    var sDevice = Dev("1.1.1", "Schaltaktor");
    var sUnlinked = ComRef("CO-1", "Wohnzimmer Licht");
    sDevice.ComObjectRefs.Add(sUnlinked);
    sDevice.ComObjectRefs.Add(ComRef("CO-2", "Küche", "GA-K"));
    var sP = BuildProject("P-S", "Smart", sDevice);
    AddMainRange(sP, "Haupt 1", 1,
        Ga("GA-W", "Wohnzimmer Decke", 1, 1, 1),
        Ga("GA-K", "Küche", 1, 1, 2));

    var sSuggestions = SmartLinking.Suggest(sP);
    Check("SmartLinking: ein Vorschlag für unverknüpftes Objekt", sSuggestions.Count == 1, sSuggestions.Count.ToString());
    Check("SmartLinking: Vorschlag zeigt auf Wohnzimmer-GA",
        sSuggestions.Count == 1 && sSuggestions[0].GroupAddress.Id == "GA-W");
    Check("SmartLinking: Score über Minimum",
        sSuggestions.Count == 1 && sSuggestions[0].Score >= 40, sSuggestions.FirstOrDefault()?.Score.ToString());
    Check("SmartLinking: Begründung nennt Namens-Token",
        sSuggestions.Count == 1 && sSuggestions[0].Reason.Contains("wohnzimmer", StringComparison.OrdinalIgnoreCase),
        sSuggestions.FirstOrDefault()?.Reason);
    Check("SmartLinking: verknüpftes Objekt ohne Vorschlag",
        sSuggestions.All(x => x.ComObject.Id != "CO-2"));

    var sApplied = SmartLinking.Apply(sSuggestions);
    Check("SmartLinking: Apply setzt genau eine Verknüpfung", sApplied == 1, sApplied.ToString());
    Check("SmartLinking: Link am Objekt", sUnlinked.Links.SequenceEqual(["GA-W"]));
    Check("SmartLinking: Apply ist idempotent", SmartLinking.Apply(sSuggestions) == 0 && sUnlinked.Links.Count == 1);
    Check("SmartLinking: keine Vorschläge mehr", SmartLinking.Suggest(sP).Count == 0);
}

// --- ProductReplacer: Dry-Run, echtes Ersetzen, Links bleiben, Fremdgerät unberührt ---
{
    var rA = Dev("1.1.1", "Aktor A", "M-1_A-1");
    rA.Hardware2ProgramRefId = "M-1_H-1";
    rA.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1", "GA-2"));
    var rB = Dev("1.1.2", "Aktor B", "M-2_A-2");
    rB.Hardware2ProgramRefId = "M-2_H-2";
    var rP = BuildProject("P-R", "Ersetzen", rA, rB);
    AddMainRange(rP, "Haupt 1", 1,
        Ga("GA-1", "Wohnzimmer", 1, 1, 1),
        Ga("GA-2", "Flur", 1, 1, 2));

    var rDry = ProductReplacer.Replace(rP, "M-1_A-1", "M-9_A-9", "M-9_H-9", dryRun: true);
    Check("Replace: Dry-Run findet ein Gerät", rDry.Count == 1, rDry.Count.ToString());
    Check("Replace: Dry-Run meldet beide Links", rDry.Count == 1 && rDry[0].KeptLinks == 2);
    Check("Replace: Dry-Run ändert nichts",
        rA.ProductRefId == "M-1_A-1" && rA.Hardware2ProgramRefId == "M-1_H-1");

    var rReal = ProductReplacer.Replace(rP, "M-1_A-1", "M-9_A-9", "M-9_H-9");
    Check("Replace: ProductRefId neu", rA.ProductRefId == "M-9_A-9", rA.ProductRefId);
    Check("Replace: Hardware2ProgramRefId neu", rA.Hardware2ProgramRefId == "M-9_H-9", rA.Hardware2ProgramRefId);
    Check("Replace: Links erhalten", rA.ComObjectRefs[0].Links.SequenceEqual(["GA-1", "GA-2"]));
    Check("Replace: Ergebnis", rReal.Count == 1 && rReal[0].OldRefId == "M-1_A-1" && rReal[0].KeptLinks == 2);
    Check("Replace: fremdes Gerät unverändert",
        rB.ProductRefId == "M-2_A-2" && rB.Hardware2ProgramRefId == "M-2_H-2" && rB.ComObjectRefs.Count == 0);
    Check("Replace: Find", ProductReplacer.Find(rP, "M-9_A-9").Count == 1 && ProductReplacer.Find(rP, "M-1_A-1").Count == 0);
}

// --- ProjectWizard: Projekt anlegen, ETS-CSV importieren ---
{
    var wOptions = new NewProjectOptions { Name = "Wizard-Projekt", Areas = 2, LinesPerArea = 3 };
    var wP = ProjectWizard.Create(wOptions);
    Check("Wizard: Name und eine Installation", wP.Name == "Wizard-Projekt" && wP.Installations.Count == 1);
    var wInst = wP.Installations[0];
    Check("Wizard: 2 Bereiche", wInst.Areas.Count == 2, wInst.Areas.Count.ToString());
    Check("Wizard: 3 Linien je Bereich", wInst.Areas.All(a => a.Lines.Count == 3));
    Check("Wizard: Adressen", wInst.Areas[1].Address == 1 && wInst.Areas[1].Lines[2].Address == 2);
    Check("Wizard: Namen", wInst.Areas[0].Name == "Bereich 0" && wInst.Areas[1].Lines[2].Name == "Linie 1.2");
    Check("Wizard: Drei-Ebenen-Bereiche",
        wInst.GroupAddressStyle == GroupAddressStyle.ThreeLevel
        && wInst.MainRanges.Count == 1 && wInst.MainRanges[0].Ranges.Count == 1);
    Check("Wizard: ohne automatische Namen",
        ProjectWizard.Create(new NewProjectOptions { Areas = 1, LinesPerArea = 1, CreateAreaAndLineNames = false })
            .Installations[0].Areas[0].Name is null);

    // ETS-CSV: Kopfzeile, Adressen, Namen, DPT in Punktnotation, Duplikat, kaputte Zeile
    var wCsv = "Adresse,Name,Datentyp\n"
               + "\"1/1/1\",\"Wohnzimmer Licht\",\"1.1\"\n"
               + "\"1/1/1\",\"Doppelt\",\"1.1\"\n"
               + "\"1/2/1\",\"Küche\",\"1.1\"\n"
               + "\"2/1/5\",\"Außen\"\n"
               + "\"kaputt\",\"\",\"\"\n";
    var wImport = ProjectWizard.ImportGroupAddresses(wP, wInst, wCsv);
    Check("Import: 3 Adressen importiert", wImport.Imported == 3, wImport.Imported.ToString());
    Check("Import: Duplikat übersprungen", wImport.Skipped == 1, wImport.Skipped.ToString());
    Check("Import: eine Fehlerzeile mit Zeilennummer",
        wImport.Errors.Count == 1 && wImport.Errors[0].Contains("Zeile 6"), string.Join("; ", wImport.Errors));

    var wWohnzimmer = ProjectQuery.FindGroupAddress(wP, GroupAddress.Parse("1/1/1"));
    Check("Import: Name übernommen", wWohnzimmer?.Name == "Wohnzimmer Licht");
    Check("Import: DPT nach DPST normalisiert", wWohnzimmer?.DatapointType == "DPST-1-1", wWohnzimmer?.DatapointType);
    Check("Import: fehlender DPT bleibt leer",
        ProjectQuery.FindGroupAddress(wP, GroupAddress.Parse("2/1/5"))?.DatapointType is null);
    Check("Import: fehlender Hauptbereich erzeugt", wInst.MainRanges.Any(r => r.RangeStart == 2));
    Check("Import: fehlender Mittelbereich erzeugt",
        wInst.MainRanges.Any(r => r.RangeStart == 1 && r.Ranges.Any(m => m.RangeStart == 2)));
    Check("Import: Gruppenadress-Ids eindeutig",
        ProjectQuery.GroupAddresses(wP).Select(g => g.Id).Distinct().Count() == 3);
}

// --- Dashboard: Zählwerte und Abdeckungen ---
{
    var dP = new Project { Id = "P-D", Name = "Dashboard" };
    var dInst = new Installation { Id = "I-D", Name = "Haus" };
    var dArea = new Area { Id = "A-0", Address = 0, Name = "EG" };
    var dL1 = new Line { Id = "L-1", Address = 1, Name = "Linie A" };
    var dL2 = new Line { Id = "L-2", Address = 2, Name = "Linie B" };
    var dD1 = Dev("1.1.1", "Aktor");
    dD1.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1"));
    dD1.ComObjectRefs.Add(ComRef("CO-2", "Status"));
    var dD2 = Dev("1.1.2", "Taster");
    dD2.ComObjectRefs.Add(ComRef("CO-3", "Tasten", "GA-2"));
    var dD3 = Dev("1.2.1", "Sensor");
    dL1.Devices.Add(dD1);
    dL1.Devices.Add(dD2);
    dL2.Devices.Add(dD3);
    dArea.Lines.Add(dL1);
    dArea.Lines.Add(dL2);
    dInst.Areas.Add(dArea);
    var dMain = new GroupRange { Id = "GR-1", Name = "Hauptbereich", RangeStart = 1, RangeEnd = 2047 };
    var dMid = new GroupRange { Id = "GR-2", Name = "Mittelbereich", RangeStart = 1, RangeEnd = 255 };
    dMid.Addresses.Add(Ga("GA-1", "Wohnzimmer", 1, 1, 1));
    dMid.Addresses.Add(Ga("GA-2", "Flur", 1, 1, 2));
    dMid.Addresses.Add(Ga("GA-3", "Küche", 1, 1, 3, null));
    dMain.Ranges.Add(dMid);
    dInst.MainRanges.Add(dMain);
    dP.Installations.Add(dInst);

    var dStats = Dashboard.Build(dP);
    Check("Dashboard: Installation/Bereich/Linien",
        dStats.Installations == 1 && dStats.Areas == 1 && dStats.Lines == 2);
    Check("Dashboard: Geräte", dStats.Devices == 3, dStats.Devices.ToString());
    Check("Dashboard: Kommunikationsobjekte",
        dStats.ComObjects == 3 && dStats.LinkedComObjects == 2,
        $"{dStats.ComObjects}/{dStats.LinkedComObjects}");
    Check("Dashboard: Gruppenadressen",
        dStats.GroupAddresses == 3 && dStats.GroupAddressesWithDpt == 2 && dStats.LinkedGroupAddresses == 2,
        $"{dStats.GroupAddresses}/{dStats.GroupAddressesWithDpt}/{dStats.LinkedGroupAddresses}");
    Check("Dashboard: Datapoint-Typen", dStats.DptTypes == 1, dStats.DptTypes.ToString());
    Check("Dashboard: LinkCoverage", Math.Abs(dStats.LinkCoverage - 200.0 / 3) < 0.01, dStats.LinkCoverage.ToString("0.##"));
    Check("Dashboard: DptCoverage", Math.Abs(dStats.DptCoverage - 200.0 / 3) < 0.01, dStats.DptCoverage.ToString("0.##"));
    Check("Dashboard: Geräte pro Linie",
        dStats.DevicesPerLine.SequenceEqual([new NamedCount("0.1 Linie A", 2), new NamedCount("0.2 Linie B", 1)]),
        string.Join(",", dStats.DevicesPerLine.Select(x => x.Name + "=" + x.Count)));
    Check("Dashboard: Gruppenadressen pro Hauptbereich",
        dStats.GroupAddressesPerMainRange.SequenceEqual([new NamedCount("1 Hauptbereich", 3)]));
    Check("Dashboard: Top-Datapoint-Typen",
        dStats.TopDatapointTypes.SequenceEqual([new NamedCount("DPST-1-1", 2)]));

    var dEmpty = Dashboard.Build(new Project { Id = "P-E", Name = "Leer" });
    Check("Dashboard: leeres Projekt ohne Division durch Null",
        dEmpty.LinkCoverage == 0 && dEmpty.DptCoverage == 0);
}

// --- SemanticExport: Turtle, JSON-LD und DPT-Format ---
{
    var eDevice = Dev("1.1.1", "Aktor");
    eDevice.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1"));
    var eP = BuildProject("P-Sem", "Semantik", eDevice);
    AddMainRange(eP, "Haupt 1", 1, Ga("GA-1", "Wohnzimmer Decke", 1, 1, 1));
    var eGa = ProjectQuery.GroupAddresses(eP).Single();

    var eTurtle = SemanticExport.ToTurtle(eP);
    Check("SemanticExport: Turtle Gruppenadresse numerisch",
        eTurtle.Contains($"knx:groupAddress {eGa.Address.Raw} ;"),
        $"knx:groupAddress {eGa.Address.Raw}");
    Check("SemanticExport: Turtle Individualadresse 4 Hex",
        eTurtle.Contains("knx:individualAddress \"1101\""));
    Check("SemanticExport: Turtle Datapoint-Subjekt", eTurtle.Contains("dct:subject \"1.001\""));
    Check("SemanticExport: Turtle Verknüpfung", eTurtle.Contains("core:hasPoint"));

    var eJsonLd = SemanticExport.ToJsonLd(eP);
    using var eDoc = JsonDocument.Parse(eJsonLd);
    Check("SemanticExport: JSON-LD parsebar",
        eDoc.RootElement.TryGetProperty("@graph", out var eGraph) && eGraph.ValueKind == JsonValueKind.Array);
    var eNodes = eDoc.RootElement.TryGetProperty("@graph", out var eGraphNodes) && eGraphNodes.ValueKind == JsonValueKind.Array
        ? eGraphNodes.EnumerateArray().ToList()
        : [];
    var ePoints = eNodes.Where(n => n.TryGetProperty("@type", out var t) && t.GetString() == "knx:FunctionPoint").ToList();
    Check("SemanticExport: JSON-LD FunctionPoint mit Rohadresse",
        ePoints.Count == 1 && ePoints[0].GetProperty("groupAddress").GetInt32() == eGa.Address.Raw);
    var eDevices = eNodes.Where(n => n.TryGetProperty("@type", out var t) && t.GetString() == "core:Device").ToList();
    Check("SemanticExport: JSON-LD Device mit 4-Hex-Adresse",
        eDevices.Count == 1 && eDevices[0].GetProperty("individualAddress").GetString() == "1101");
    Check("SemanticExport: JSON-LD hasPoint",
        eDevices.Count == 1 && eDevices[0].GetProperty("hasPoint").EnumerateArray()
            .Any(x => x.GetString()?.Contains("GA-", StringComparison.Ordinal) == true));

    Check("FormatDatapointType DPST-1-1", SemanticExport.FormatDatapointType("DPST-1-1") == "1.001",
        SemanticExport.FormatDatapointType("DPST-1-1"));
    Check("FormatDatapointType DPST-20-106", SemanticExport.FormatDatapointType("DPST-20-106") == "20.106",
        SemanticExport.FormatDatapointType("DPST-20-106"));
    Check("FormatDatapointType leer",
        SemanticExport.FormatDatapointType(null) is null && SemanticExport.FormatDatapointType("") is null);
}

// --- AppSettings: Round-Trip in temp. Verzeichnis, Recent-Liste, kaputte Datei ---
{
    var stFile = Path.Combine(Path.GetTempPath(), "knx_selftest", "settings", "settings.json");
    var stSettings = new AppSettings
    {
        Theme = "Dunkel",
        Language = "en",
        ShowMonitorTimestamps = false,
        ConfirmDelete = false,
        BusInterface = "192.168.1.10",
        TunnelAddress = "1.1.251",
        LastProject = "a.knxproj",
        RecentProjects = ["a.knxproj", "b.knxproj"],
    };
    stSettings.Save(stFile);
    Check("Settings: Save erzeugt Datei", File.Exists(stFile));

    var stAgain = AppSettings.Load(stFile);
    Check("Settings: Theme", stAgain.Theme == "Dunkel", stAgain.Theme);
    Check("Settings: Language", stAgain.Language == "en", stAgain.Language);
    Check("Settings: Flags", !stAgain.ShowMonitorTimestamps && !stAgain.ConfirmDelete);
    Check("Settings: BusInterface/TunnelAddress",
        stAgain.BusInterface == "192.168.1.10" && stAgain.TunnelAddress == "1.1.251");
    Check("Settings: LastProject/RecentProjects",
        stAgain.LastProject == "a.knxproj" && stAgain.RecentProjects.SequenceEqual(["a.knxproj", "b.knxproj"]),
        string.Join(",", stAgain.RecentProjects));

    stSettings.TouchProject("c.knxproj");
    Check("Settings: TouchProject stellt nach vorn",
        stSettings.RecentProjects[0] == "c.knxproj" && stSettings.LastProject == "c.knxproj");
    stSettings.TouchProject("a.knxproj");
    Check("Settings: TouchProject dedupliziert",
        stSettings.RecentProjects.SequenceEqual(["a.knxproj", "c.knxproj", "b.knxproj"]),
        string.Join(",", stSettings.RecentProjects));
    stSettings.TouchProject("A.KNXPROJ");
    Check("Settings: TouchProject case-insensitiv",
        stSettings.RecentProjects.Count == 3 && stSettings.RecentProjects[0] == "A.KNXPROJ",
        string.Join(",", stSettings.RecentProjects));

    File.WriteAllText(stFile, "{ das ist kein JSON");
    var stFallback = AppSettings.Load(stFile);
    Check("Settings: kaputte Datei fällt auf Defaults zurück",
        stFallback.Theme == "System" && stFallback.Language == "de"
        && stFallback.ShowMonitorTimestamps && stFallback.RecentProjects.Count == 0);
    Check("Settings: fehlende Datei fällt auf Defaults zurück",
        AppSettings.Load(Path.Combine(Path.GetTempPath(), "knx_selftest", "settings", "fehlt.json")).Theme == "System");
}

// --- Knxproj: ComObjectInstanceRef mit Links überlebt den Round-Trip ---
{
    var lDevice = Dev("1.1.1", "Aktor");
    lDevice.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1", "GA-2"));
    lDevice.ComObjectRefs.Add(ComRef("CO-2", "Status"));
    var lP = BuildProject("P-L", "Links", lDevice);
    AddMainRange(lP, "Haupt 1", 1,
        Ga("GA-1", "Wohnzimmer", 1, 1, 1),
        Ga("GA-2", "Flur", 1, 1, 2));

    var lPath = Path.Combine(Path.GetTempPath(), "knx_selftest", "links.knxproj");
    Knxproj.Save(lP, lPath);
    var lLoaded = Knxproj.Load(lPath);
    var lLoadedDevice = ProjectQuery.DeviceList(lLoaded).Single();
    Check("Knxproj: ComObjectInstanceRefs Anzahl", lLoadedDevice.ComObjectRefs.Count == 2,
        lLoadedDevice.ComObjectRefs.Count.ToString());
    Check("Knxproj: Linkreihenfolge erhalten",
        lLoadedDevice.ComObjectRefs[0].Links.SequenceEqual(["GA-1", "GA-2"]),
        string.Join(",", lLoadedDevice.ComObjectRefs[0].Links));
    Check("Knxproj: ComObject-Text erhalten", lLoadedDevice.ComObjectRefs[0].Text == "Schalten");
    Check("Knxproj: Objekt ohne Links", lLoadedDevice.ComObjectRefs[1].Links.Count == 0);

    var lXml = Knxproj.ToXml(lP);
    Check("Knxproj: ComObjectInstanceRefs im XML",
        lXml.Descendants().Any(e => e.Name.LocalName == "ComObjectInstanceRefs"));
    Check("Knxproj: RefId und Links-Attribut im XML",
        lXml.Descendants().Any(e => e.Name.LocalName == "ComObjectInstanceRef"
            && (string?)e.Attribute("RefId") == "CO-1"
            && (string?)e.Attribute("Links") == "GA-1 GA-2"));
    var lPlain = BuildProject("P-L2", "Ohne Links", Dev("1.1.1", "Aktor"));
    Check("Knxproj: kein ComObjectInstanceRefs ohne Refs",
        !Knxproj.ToXml(lPlain).Descendants().Any(e => e.Name.LocalName == "ComObjectInstanceRefs"));
}

// --- Lokalisierung: Ressourcen, Fallback, Formatierung ---
{
    Strings.Language = "de";
    Check("i18n: deutscher Text", Strings.Get("BtnSave") == "Speichern", Strings.Get("BtnSave"));
    Check("i18n: Formatierung", Strings.Get("LblFindings", 3) == "3 Befunde", Strings.Get("LblFindings", 3));
    Check("i18n: unbekannter Schlüssel fällt auf Key zurück",
        Strings.Get("GibtEsNicht") == "GibtEsNicht");

    Strings.Language = "en";
    Check("i18n: englischer Text", Strings.Get("BtnSave") == "Save", Strings.Get("BtnSave"));
    Check("i18n: Formatierung englisch", Strings.Get("LblFindings", 3) == "3 findings", Strings.Get("LblFindings", 3));

    Strings.Language = "xx";
    Check("i18n: unbekannte Sprache fällt auf Deutsch zurück", Strings.Get("BtnSave") == "Speichern",
        Strings.Get("BtnSave"));

    Strings.Language = "de";
    Check("i18n: beide Sprachen vollständig",
        Strings.Available.Count == 2);
}

// --- Lokalisierung der Projektprüfung: deutsche Vorgabe, englische Übersetzung, gleiche Pfade ---
{
    Project CheckProject()
    {
        var taster = Dev("1.1.1", "Taster");
        taster.ComObjectRefs.Add(ComRef("CO-1", "Schalten"));
        var checkProject = BuildProject("P-I18N", "Prüfung", taster, Dev("1.1.2", "Aktor"));
        checkProject.Workbench.Devices["ghost"] = new Knx.Workbench.DeviceMetadata();
        return checkProject;
    }

    var catalogue = new Dictionary<string, Knx.Catalog.CatalogApplicationProgram>
    {
        ["M-0001_H-0001"] = new() { Id = "M-0001_H-0001", Broken = true },
    };
    string Message(Project project, string code) =>
        ProjectCheck.Run(project, catalogue).First(f => f.Code == code).Message;

    void CheckMessage(string label, string code, string de, string en)
    {
        Strings.Language = "de";
        var german = Message(CheckProject(), code);
        Strings.Language = "en";
        var english = Message(CheckProject(), code);
        Strings.Language = "de";
        Check($"i18n: {label} deutsch", german == de, german);
        Check($"i18n: {label} englisch", english == en, english);
        Check($"i18n: {label} wirklich übersetzt", german != english);
    }

    var emptyDe = ProjectCheck.Run(new Project { Id = "P-EMPTY", Name = "Leer" });
    Strings.Language = "en";
    var emptyEn = ProjectCheck.Run(new Project { Id = "P-EMPTY", Name = "Leer" });
    Strings.Language = "de";
    Check("i18n: P001 deutsch", emptyDe.Single().Message == "Projekt enthält keine Installation.",
        emptyDe.Single().Message);
    Check("i18n: P001 englisch", emptyEn.Single().Message == "Project contains no installation.",
        emptyEn.Single().Message);
    Check("i18n: P001 Pfad, Code und Schwere unverändert",
        emptyEn.Single().Path == "Leer" && emptyEn.Single().Code == "P001"
        && emptyEn.Single().Severity == CheckSeverity.Error);

    CheckMessage("P023", "P023", "Gerät 'Aktor' hat keine Kommunikationsobjekte.",
        "Device 'Aktor' has no communication objects.");
    CheckMessage("P040", "P040", "Kommunikationsobjekt 'Schalten' von 'Taster' ist nicht verknüpft.",
        "Communication object 'Schalten' of 'Taster' is not linked.");
    CheckMessage("P052", "P052", "Arbeitsbereichs-Metadaten verweisen auf das unbekannte Gerät 'ghost'.",
        "Workbench metadata references the unknown device 'ghost'.");
    CheckMessage("P070", "P070",
        "Applikationsprogramm von 'Taster' ist als defekt markiert; Bearbeiten und Download sind gesperrt.",
        "Application program of 'Taster' is marked as broken; editing and download are locked.");

    var problemProject = CheckProject();
    var deFindings = ProjectCheck.Run(problemProject, catalogue);
    Strings.Language = "en";
    var enFindings = ProjectCheck.Run(problemProject, catalogue);
    Strings.Language = "de";
    Check("i18n: Prüfbefunde behalten Code, Pfad und Reihenfolge",
        deFindings.Select(f => (f.Code, f.Path, f.Severity))
            .SequenceEqual(enFindings.Select(f => (f.Code, f.Path, f.Severity))),
        string.Join(",", deFindings.Select(f => f.Code)));

    Check("i18n: Apostroph im Wert bleibt erhalten",
        Strings.Get("ChkP020", "O'Brien", "1.1.1") == "Gerät 'O'Brien' hat die ungültige Adresse 1.1.1.",
        Strings.Get("ChkP020", "O'Brien", "1.1.1"));
    Strings.Language = "en";
    Check("i18n: Apostroph im Wert bleibt erhalten (en)",
        Strings.Get("ChkP020", "O'Brien", "1.1.1") == "Device 'O'Brien' has the invalid address 1.1.1.",
        Strings.Get("ChkP020", "O'Brien", "1.1.1"));
    Strings.Language = "de";
    Check("i18n: P080 deutsch",
        Strings.Get("ChkP080", "20", "15") ==
        "Die Geräte ziehen 20 mA, die deklarierte Spannungsversorgung liefert nur 15 mA.",
        Strings.Get("ChkP080", "20", "15"));
    Strings.Language = "en";
    Check("i18n: P080 englisch",
        Strings.Get("ChkP080", "20", "15") ==
        "The devices draw 20 mA but the declared power supply delivers only 15 mA.",
        Strings.Get("ChkP080", "20", "15"));
    Strings.Language = "de";
}

// --- Lokalisierung: neue Schlüssel vollständig, Platzhalter formatierbar ---
{
    var prefixes = new[] { "Chk", "Csv", "Report" };
    var keys = Strings.Catalogue
        .Where(p => prefixes.Any(prefix => p.Key.StartsWith(prefix, StringComparison.Ordinal)))
        .OrderBy(p => p.Key, StringComparer.Ordinal)
        .ToList();
    Check("i18n: neue Schlüssel vorhanden", keys.Count >= 100, keys.Count.ToString());
    Check("i18n: keine leeren neuen Übersetzungen",
        keys.All(p => !string.IsNullOrWhiteSpace(p.Value.De) && !string.IsNullOrWhiteSpace(p.Value.En)),
        string.Join(",", keys.Where(p => string.IsNullOrWhiteSpace(p.Value.De) ||
                                          string.IsNullOrWhiteSpace(p.Value.En)).Select(p => p.Key)));

    bool Formats(string key, string text)
    {
        var indexes = System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)\}")
            .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max();
        var args = Enumerable.Range(0, indexes + 1).Select(i => (object)("Wert" + i)).ToArray();
        var rendered = Strings.Get(key, args);
        return !rendered.Contains('{') && !rendered.Contains('}');
    }

    foreach (var language in Strings.Available)
    {
        Strings.Language = language;
        Check($"i18n: Platzhalter formatierbar ({language})",
            keys.All(p => Formats(p.Key, language == "en" ? p.Value.En : p.Value.De)),
            string.Join(",", keys.Where(p => !Formats(p.Key, language == "en" ? p.Value.En : p.Value.De))
                .Select(p => p.Key)));
    }
    Strings.Language = "de";
}

// --- ProjectOperations: atomare Geräte-, Linien-, Segment- und Gruppenadress-Aktionen ---
{
    // Two installations, rooms, lines with/without segments and group ranges; one device with
    // channel, communication object/link, parameter value and workbench metadata.
    Project OpsProject()
    {
        var p = new Project { Id = "P-OPS", Name = "Ops" };
        var inst1 = new Installation { Id = "I-1", Name = "Haus A", Address = new IndividualAddress(1, 1, 0) };
        var inst2 = new Installation { Id = "I-2", Name = "Haus B", Address = new IndividualAddress(2, 1, 0) };
        var area1 = new Area { Id = "A-1", Address = 1, Name = "EG" };
        var area2 = new Area { Id = "A-2", Address = 2, Name = "OG" };
        var areaB = new Area { Id = "A-B", Address = 1, Name = "Fremd" };
        var line1 = new Line { Id = "L-1-1", Address = 1, Name = "Linie 1" };
        var line2 = new Line { Id = "L-1-2", Address = 2, Name = "Linie 2" };
        var segment = new Segment { Id = "S-1-1-0", Address = 0, Name = "Segment" };
        var device = Dev("1.1.1", "Taster");
        device.Channels.Add(new ChannelInstance { Id = "CH-1", Name = "Kanal 1" });
        device.ComObjectRefs.Add(ComRef("CO-1", "Schalten", "GA-1"));
        device.ParameterValues["mode"] = "\"switch\"";
        segment.Devices.Add(device);
        line1.Segments.Add(segment);
        area1.Lines.Add(line1);
        area1.Lines.Add(line2);
        inst1.Areas.Add(area1);
        inst1.Areas.Add(area2);
        var lineB = new Line { Id = "L-2-1", Address = 1, Name = "Linie B" };
        areaB.Lines.Add(lineB);
        inst2.Areas.Add(areaB);
        p.Installations.Add(inst1);
        p.Installations.Add(inst2);

        var house = inst1.Locations.Add(null, "Haus", SpaceKind.Building, "SP-house");
        var floor = inst1.Locations.Add(house, "EG", SpaceKind.Floor, "SP-floor");
        var roomA = inst1.Locations.Add(floor, "Wohnen", SpaceKind.Room, "roomA");
        inst1.Locations.Add(floor, "Kueche", SpaceKind.Room, "roomB");
        roomA.AssignDevice(device);
        inst2.Locations.Add(null, "Haus B", SpaceKind.Building, "SP-houseB");

        var main = new GroupRange { Id = "GR-1", Name = "Haupt 1", RangeStart = 1, RangeEnd = 1 };
        var middle = new GroupRange { Id = "GR-1-1", Name = "Mittel 1", RangeStart = 1, RangeEnd = 1 };
        middle.Addresses.Add(Ga("GA-1", "Decke", 1, 1, 1));
        middle.Addresses.Add(Ga("GA-2", "Status", 1, 1, 2));
        main.Ranges.Add(middle);
        inst1.MainRanges.Add(main);

        p.Workbench.Objects[new Knx.Workbench.ObjectKey(device.Id, "CO-1").ToString()] =
            new Knx.Workbench.ObjectMetadata
            {
                Dpt = "1.001", Bits = 1, ChannelId = "CH-1", SendingGroupId = "GA-1",
                Connectors = { ["GA-1"] = new Knx.Workbench.ConnectorMetadata { Send = true } },
            };
        p.Workbench.Groups["GA-1"] = new Knx.Workbench.GroupMetadata
            { Bits = 1, Secure = false, EffectRoomId = "roomA" };
        return p;
    }

    static Line OpsLine(Project p, string id) => p.Installations.SelectMany(i => i.Areas).SelectMany(a => a.Lines).Single(l => l.Id == id);
    static Area OpsArea(Project p, string id) => p.Installations.SelectMany(i => i.Areas).Single(a => a.Id == id);
    static DeviceInstance OpsDevice(Project p, string address) => ProjectQuery.DeviceList(p).Single(d => d.Address.ToString() == address);
    static string OpsXml(Project p) => Knxproj.ToXml(p).ToString();

    // Move across segment/line/room: new address only when the line changes, one undo step,
    // no stale building reference, links/channels/parameters preserved.
    {
        var p = OpsProject();
        var undo = new UndoStack(); undo.Reset(p);
        var device = OpsDevice(p, "1.1.1");
        var target = OpsLine(p, "L-1-2");
        var result = ProjectOperations.MoveDevice(p, device, target, null, "roomB", undo);
        Check("Ops: Gerät verschieben ok", result.Ok, result.Message);
        Check("Ops: Gerät in Ziellinie", target.Devices.Contains(device)
            && !OpsLine(p, "L-1-1").Segments[0].Devices.Contains(device));
        Check("Ops: Gerät bekommt freie Zieladresse", device.Address == new IndividualAddress(1, 2, 1),
            device.Address.ToString());
        Check("Ops: alte Raumreferenz entfernt, neue gesetzt",
            p.Installations[0].Locations.Find("roomA")!.DeviceRefs.Count == 0
            && p.Installations[0].Locations.Find("roomB")!.DeviceRefs.SequenceEqual([device.Id]));
        Check("Ops: Links/Kanäle/Parameter bleiben", device.ComObjectRefs[0].Links.SequenceEqual(["GA-1"])
            && device.Channels.Single().Id == "CH-1" && device.ParameterValues["mode"] == "\"switch\"");
        Check("Ops: Verschieben ist ein Undo-Schritt", undo.Steps == 1, undo.Steps.ToString());

        var restored = undo.Undo()!;
        Check("Ops: Undo stellt Segment/Adresse/Raum wieder her",
            OpsLine(restored, "L-1-1").Segments[0].Devices.Single().Address.ToString() == "1.1.1"
            && restored.Installations[0].Locations.Find("roomA")!.DeviceRefs.Count == 1,
            string.Join(",", restored.Installations[0].Locations.Find("roomA")!.DeviceRefs));
        var redone = undo.Redo()!;
        Check("Ops: Redo verschiebt erneut", OpsDevice(redone, "1.2.1") is not null && undo.Steps == 1);

        var fresh = OpsProject();
        var history = new UndoStack(); history.Reset(fresh);
        var before = OpsXml(fresh);
        var mismatch = ProjectOperations.MoveDevice(fresh, OpsDevice(fresh, "1.1.1"), OpsLine(fresh, "L-2-1"), null, "roomA", history);
        Check("Ops: Raum/Linie aus fremder Installation abgelehnt",
            mismatch.Status == ProjectEditStatus.InstallationMismatch && OpsXml(fresh) == before && history.Steps == 0,
            mismatch.Status.ToString());

        var identical = OpsProject();
        var identicalUndo = new UndoStack(); identicalUndo.Reset(identical);
        var noop = ProjectOperations.MoveDevice(identical, OpsDevice(identical, "1.1.1"), OpsLine(identical, "L-1-1"),
            OpsLine(identical, "L-1-1").Segments[0], null, identicalUndo);
        Check("Ops: Verschieben an denselben Platz ist folgenlos",
            noop.Ok && OpsDevice(identical, "1.1.1") is not null && identicalUndo.Steps == 0);

        var sameLine = OpsProject();
        var sameLineUndo = new UndoStack(); sameLineUndo.Reset(sameLine);
        var sameLineDevice = OpsDevice(sameLine, "1.1.1");
        var sameLineResult = ProjectOperations.MoveDevice(sameLine, sameLineDevice, OpsLine(sameLine, "L-1-1"), null, null, sameLineUndo);
        Check("Ops: Segment nach Linienwurzel behält Adresse und Raum",
            sameLineResult.Ok && sameLineDevice.Address.ToString() == "1.1.1"
            && OpsLine(sameLine, "L-1-1").Devices.Contains(sameLineDevice)
            && sameLine.Installations[0].Locations.Find("roomA")!.DeviceRefs.Contains(sameLineDevice.Id)
            && sameLineUndo.Steps == 1);
    }

    // Copy: new id and address, independent channel/object/parameter copies, copied workbench
    // metadata (external channel facts) and one undo step.
    {
        var p = OpsProject();
        var undo = new UndoStack(); undo.Reset(p);
        var source = OpsDevice(p, "1.1.1");
        var result = ProjectOperations.CopyDevice(p, source, OpsLine(p, "L-1-2"), null, "roomB", "Kopie", undo);
        var copy = result.Value!;
        Check("Ops: Gerät kopieren ok", result.Ok, result.Message);
        Check("Ops: Kopie hat neue Id und Adresse",
            copy.Id != source.Id && copy.Address == new IndividualAddress(1, 2, 1), $"{copy.Id} {copy.Address}");
        Check("Ops: Kopie erhält Objekt, Link, Kanal und Parameter",
            copy.ComObjectRefs.Count == 1 && copy.ComObjectRefs[0].Links.SequenceEqual(["GA-1"])
            && copy.Channels.Single().Name == "Kanal 1" && copy.ParameterValues["mode"] == "\"switch\"");
        copy.ComObjectRefs[0].Links.Add("GA-2");
        copy.Channels[0].Channels.Add(new ChannelInstance { Id = "CH-X", Name = "X" });
        copy.ParameterValues["mode"] = "\"dim\"";
        Check("Ops: Kopie ist tief, Quelle unverändert",
            source.ComObjectRefs[0].Links.SequenceEqual(["GA-1"]) && source.Channels[0].Channels.Count == 0
            && source.ParameterValues["mode"] == "\"switch\"");
        var copyKey = new Knx.Workbench.ObjectKey(copy.Id, "CO-1").ToString();
        var sourceKey = new Knx.Workbench.ObjectKey(source.Id, "CO-1").ToString();
        Check("Ops: externe Kanal-Metadaten kopiert",
            p.Workbench.Objects[copyKey].ChannelId == "CH-1"
            && p.Workbench.Objects[copyKey].Connectors["GA-1"].Send,
            p.Workbench.Objects[copyKey].ChannelId);
        p.Workbench.Objects[copyKey].ChannelId = "CH-2";
        Check("Ops: Metadaten-Kopie ist unabhängig", p.Workbench.Objects[sourceKey].ChannelId == "CH-1");
        Check("Ops: Kopie im Zielraum", p.Installations[0].Locations.Find("roomB")!.DeviceRefs.SequenceEqual([copy.Id]));
        Check("Ops: Kopieren ist ein Undo-Schritt", undo.Steps == 1);
        var restored = undo.Undo()!;
        Check("Ops: Undo entfernt nur die Kopie", ProjectQuery.DeviceList(restored).Count() == 1
            && restored.Installations[0].Locations.Find("roomB")!.DeviceRefs.Count == 0);

        var collision = OpsProject();
        OpsLine(collision, "L-1-1").Devices.Add(new DeviceInstance
        {
            Id = "1.2.1", ProductRefId = "P", Address = new IndividualAddress(1, 1, 5), Name = "Belegt",
        });
        var collisionCopy = ProjectOperations.CopyDevice(collision, OpsDevice(collision, "1.1.1"),
            OpsLine(collision, "L-1-2"), null, null);
        Check("Ops: Kopie weicht belegter Geräte-ID aus",
            collisionCopy.Ok && collisionCopy.Value!.Id == "1.2.1-2", collisionCopy.Value?.Id);
    }

    // Delete: removes the device, every stale building reference and its workbench metadata.
    {
        var p = OpsProject();
        var undo = new UndoStack(); undo.Reset(p);
        var device = OpsDevice(p, "1.1.1");
        p.Installations[0].Locations.Find("SP-floor")!.DeviceRefs.Add(device.Id);
        p.Workbench.Objects[new Knx.Workbench.ObjectKey(device.Id, "GC-STALE").ToString()] = new Knx.Workbench.ObjectMetadata();
        undo.Push(p);
        var result = ProjectOperations.DeleteDevice(p, device, undo);
        Check("Ops: Gerät löschen ok", result.Ok);
        Check("Ops: Gerät aus Topologie und Gebäude entfernt",
            !ProjectQuery.DeviceList(p).Any() && !p.Installations[0].Locations.AllDeviceRefs().Any());
        Check("Ops: Geräte-/Objektmetadaten entfernt",
            !p.Workbench.Devices.ContainsKey(device.Id) && p.Workbench.Objects.Count == 0);
        Check("Ops: Löschen ist ein Undo-Schritt", undo.Steps == 2);
        var restored = undo.Undo()!;
        Check("Ops: Undo stellt Gerät, Referenzen und Metadaten wieder her",
            ProjectQuery.DeviceList(restored).Single().Id == device.Id
            && restored.Installations[0].Locations.AllDeviceRefs().Count() == 2
            && restored.Workbench.Objects.Count == 2);
    }

    // Full line: allocation is refused without mutation and without an undo step.
    {
        var p = OpsProject();
        var fullLine = OpsLine(p, "L-1-2");
        for (var number = 1; number <= 255; number++)
            fullLine.Devices.Add(new DeviceInstance
            {
                Id = "FULL-" + number, ProductRefId = "P", Address = new IndividualAddress(1, 2, (byte)number),
            });
        var undo = new UndoStack(); undo.Reset(p);
        var before = OpsXml(p);
        var result = ProjectOperations.MoveDevice(p, OpsDevice(p, "1.1.1"), fullLine, null, null, undo);
        Check("Ops: volle Linie lehnt Verschieben ab",
            result.Status == ProjectEditStatus.NoFreeAddress && OpsXml(p) == before && undo.Steps == 0,
            result.Status.ToString());

        var fullLines = OpsProject();
        var fullArea = OpsArea(fullLines, "A-1");
        fullArea.Lines.Clear();
        for (byte number = 0; number <= 15; number++)
            fullArea.Lines.Add(new Line { Id = "FL-" + number, Address = number });
        var lineUndo = new UndoStack(); lineUndo.Reset(fullLines);
        var lineBefore = OpsXml(fullLines);
        var lineResult = ProjectOperations.CreateLine(fullLines, fullArea, "Nix", null, null, lineUndo);
        Check("Ops: volle Bereichsadressen lehnen Linie ab",
            lineResult.Status == ProjectEditStatus.NoFreeAddress && OpsXml(fullLines) == lineBefore && lineUndo.Steps == 0,
            lineResult.Status.ToString());
    }

    // Lines: bounds, duplicates, rename, move with device rebasing, delete guard, undo/redo.
    {
        var p = OpsProject();
        var undo = new UndoStack(); undo.Reset(p);
        var area1 = OpsArea(p, "A-1");
        var created = ProjectOperations.CreateLine(p, area1, " Neu ", null, null, undo);
        var createdLine = created.Value!;
        Check("Ops: Linie anlegen mit nächster freier Adresse",
            created.Ok && createdLine.Address == 0 && createdLine.Name == "Neu" && area1.Lines.Contains(createdLine), createdLine.Address.ToString());
        Check("Ops: Linien-ID ist eindeutig", p.Installations[0].Areas.SelectMany(a => a.Lines).Count(l => l.Id == createdLine.Id) == 1);
        Check("Ops: Linie anlegen ist ein Undo-Schritt", undo.Steps == 1);

        var before = OpsXml(p);
        Check("Ops: doppelte Linienadresse abgelehnt",
            ProjectOperations.CreateLine(p, area1, "Dup", 1, null, undo).Status == ProjectEditStatus.AddressInUse);
        Check("Ops: Linienadresse 16 abgelehnt",
            ProjectOperations.CreateLine(p, area1, "Out", 16, null, undo).Status == ProjectEditStatus.AddressOutOfRange);
        Check("Ops: leerer Linienname abgelehnt",
            ProjectOperations.RenameLine(p, createdLine, "  ", undo).Status == ProjectEditStatus.EmptyName);
        Check("Ops: abgelehnte Linienaktionen verändern nichts",
            OpsXml(p) == before && undo.Steps == 1, undo.Steps.ToString());
        Check("Ops: Linie umbenennen", ProjectOperations.RenameLine(p, createdLine, "Leer", undo).Ok && createdLine.Name == "Leer" && undo.Steps == 2);
        Check("Ops: unveränderter Name erzeugt keinen Schritt",
            ProjectOperations.RenameLine(p, createdLine, "Leer", undo).Ok && undo.Steps == 2);

        var line2 = OpsLine(p, "L-1-2");
        var movedDevice = Dev("1.2.7", "OG-Gerät");
        line2.Devices.Add(movedDevice);
        undo.Push(p);
        var moved = ProjectOperations.MoveLine(p, line2, OpsArea(p, "A-2"), null, undo);
        Check("Ops: Linie in anderen Bereich verschieben", moved.Ok, moved.Message);
        Check("Ops: Linienadresse im Zielbereich frei gewählt",
            line2.Address == 0 && OpsArea(p, "A-2").Lines.Contains(line2) && !OpsArea(p, "A-1").Lines.Contains(line2),
            line2.Address.ToString());
        Check("Ops: Geräteadresse folgt der Linie",
            movedDevice.Address == new IndividualAddress(2, 0, 7), movedDevice.Address.ToString());
        Check("Ops: Linienverschieben ist ein Undo-Schritt",
            undo.Steps == 4, undo.Steps.ToString());

        before = OpsXml(p);
        Check("Ops: Linie nicht zwischen Installationen verschiebbar",
            ProjectOperations.MoveLine(p, line2, OpsArea(p, "A-B"), null, undo).Status == ProjectEditStatus.InstallationMismatch
            && OpsXml(p) == before);
        Check("Ops: nichtleere Linie nicht löschbar",
            ProjectOperations.DeleteLine(p, OpsLine(p, "L-1-1"), undo).Status == ProjectEditStatus.NotEmpty);
        Check("Ops: leere Linie löschen", ProjectOperations.DeleteLine(p, createdLine, undo).Ok && !area1.Lines.Contains(createdLine));
        var restored = undo.Undo()!;
        Check("Ops: Undo stellt gelöschte Linie wieder her", OpsArea(restored, "A-1").Lines.Any(l => l.Id == createdLine.Id));
        var redone = undo.Redo()!;
        Check("Ops: Redo löscht erneut", redone.Installations[0].Areas.SelectMany(a => a.Lines).All(l => l.Id != line.Id));
    }

    // Segments: bounds, duplicates, collision guard, rebasing, delete guard, undo.
    {
        var p = OpsProject();
        var undo = new UndoStack(); undo.Reset(p);
        var line1 = OpsLine(p, "L-1-1");
        var created = ProjectOperations.CreateSegment(p, line1, " Neu ", null, null, undo);
        var createdSegment = created.Value!;
        Check("Ops: Segment anlegen mit nächster freier Adresse",
            created.Ok && createdSegment.Address == 1 && line1.Segments.Contains(createdSegment), createdSegment.Address.ToString());
        var before = OpsXml(p);
        Check("Ops: doppelte Segmentadresse abgelehnt",
            ProjectOperations.CreateSegment(p, line1, "Dup", 0, null, undo).Status == ProjectEditStatus.AddressInUse);
        Check("Ops: Segmentadresse 16 abgelehnt",
            ProjectOperations.CreateSegment(p, line1, "Out", 16, null, undo).Status == ProjectEditStatus.AddressOutOfRange);
        Check("Ops: leerer Segmentname abgelehnt",
            ProjectOperations.RenameSegment(p, createdSegment, "", undo).Status == ProjectEditStatus.EmptyName);
        Check("Ops: abgelehnte Segmentaktionen verändern nichts", OpsXml(p) == before && undo.Steps == 1);
        Check("Ops: Segment umbenennen", ProjectOperations.RenameSegment(p, createdSegment, "Flur", undo).Ok
            && createdSegment.Name == "Flur" && undo.Steps == 2);

        var source = line1.Segments.Single(s => s.Id == "S-1-1-0");
        var line2 = OpsLine(p, "L-1-2");
        line2.Devices.Add(Dev("1.2.1", "Belegt"));
        undo.Push(p);
        before = OpsXml(p);
        Check("Ops: Segment mit Adresskollision abgelehnt",
            ProjectOperations.MoveSegment(p, source, line2, null, undo).Status == ProjectEditStatus.AddressInUse
            && OpsXml(p) == before);
        var emptyLine = ProjectOperations.CreateLine(p, OpsArea(p, "A-1"), "Ziel", null, null, undo).Value!;
        var moved = ProjectOperations.MoveSegment(p, source, emptyLine, null, undo);
        Check("Ops: Segment in leere Linie verschieben", moved.Ok, moved.Message);
        Check("Ops: Segmentgerät folgt der Ziellinie",
            source.Devices.Single().Address == new IndividualAddress(1, emptyLine.Address, 1),
            source.Devices.Single().Address.ToString());
        Check("Ops: Segmentverschieben ist ein Undo-Schritt", undo.Steps == 5, undo.Steps.ToString());
        Check("Ops: nichtleeres Segment nicht löschbar",
            ProjectOperations.DeleteSegment(p, source, undo).Status == ProjectEditStatus.NotEmpty);
        Check("Ops: leeres Segment löschen",
            ProjectOperations.DeleteSegment(p, createdSegment, undo).Ok && !line1.Segments.Contains(createdSegment));
        var restored = undo.Undo()!;
        Check("Ops: Undo stellt Segment wieder her", OpsLine(restored, "L-1-1").Segments.Any(s => s.Id == createdSegment.Id));

        var fullSegments = OpsProject();
        var segmentLine = OpsLine(fullSegments, "L-1-1");
        segmentLine.Segments.Clear();
        for (byte number = 0; number <= 15; number++)
            segmentLine.Segments.Add(new Segment { Id = "FS-" + number, Address = number });
        var segmentUndo = new UndoStack(); segmentUndo.Reset(fullSegments);
        var segmentBefore = OpsXml(fullSegments);
        var segmentResult = ProjectOperations.CreateSegment(fullSegments, segmentLine, "Nix", null, null, segmentUndo);
        Check("Ops: volle Segmentadressen lehnen Segment ab",
            segmentResult.Status == ProjectEditStatus.NoFreeAddress && OpsXml(fullSegments) == segmentBefore && segmentUndo.Steps == 0,
            segmentResult.Status.ToString());
    }

    // Group addresses: next free suggestion, range maintenance, duplicates, copy/move/rename,
    // deletion that removes links and sending facts without choosing a replacement.
    {
        var p = OpsProject();
        var undo = new UndoStack(); undo.Reset(p);
        var opsInstallation = p.Installations[0];
        var created = ProjectOperations.CreateGroupAddress(p, null, " Neu ", "1.001", opsInstallation, null, undo,
            new Knx.Workbench.GroupMetadata { Bits = 1, Secure = false });
        var entry = created.Value!;
        Check("Ops: GA anlegen nutzt nächste freie Adresse",
            created.Ok && entry.Address == GroupAddress.ThreeLevel(1, 1, 3) && entry.Name == "Neu",
            entry.Address.ToString());
        Check("Ops: neue GA ist mit ihren Metadaten verknüpfbar",
            Knx.Workbench.WorkbenchProject.CheckLink(p, new Knx.Workbench.ObjectKey(OpsDevice(p, "1.1.1").Id, "CO-1"), entry.Id).Ok);
        Check("Ops: GA anlegen ist ein Undo-Schritt", undo.Steps == 1);

        var before = OpsXml(p);
        Check("Ops: doppelte GA abgelehnt",
            ProjectOperations.CreateGroupAddress(p, GroupAddress.ThreeLevel(1, 1, 3), "Dup", null, opsInstallation, null, undo)
                .Status == ProjectEditStatus.AddressInUse);
        Check("Ops: leerer GA-Name abgelehnt",
            ProjectOperations.CreateGroupAddress(p, GroupAddress.ThreeLevel(1, 2, 1), " ", null, opsInstallation, null, undo)
                .Status == ProjectEditStatus.EmptyName);
        Check("Ops: abgelehnte GA-Aktionen verändern nichts", OpsXml(p) == before && undo.Steps == 1);

        var far = ProjectOperations.CreateGroupAddress(p, GroupAddress.ThreeLevel(1, 2, 5), "Fern", null, opsInstallation, null, undo);
        Check("Ops: GA außerhalb der Bereiche legt Mittelbereich an",
            far.Ok && ProjectQuery.FindRangeFor(opsInstallation, far.Value!.Address)?.Id == "GR-2",
            ProjectQuery.FindRangeFor(opsInstallation, far.Value!.Address)?.Id ?? "(fehlt)");
        Check("Ops: Bereichs-ID eindeutig",
            ProjectQuery.AllRanges(opsInstallation).Select(r => r.Id).Distinct().Count() == ProjectQuery.AllRanges(opsInstallation).Count());

        Check("Ops: GA umbenennen", ProjectOperations.RenameGroupAddress(p, entry, "Umbenannt", "DPST-1-1", undo).Ok
            && entry.Name == "Umbenannt" && entry.DatapointType == "DPST-1-1");
        var stepsAfterRename = undo.Steps;

        var copySource = ProjectQuery.FindGroupAddress(p, GroupAddress.ThreeLevel(1, 1, 1))!;
        var copy = ProjectOperations.CopyGroupAddress(p, copySource, null, "Kopie", undo);
        Check("Ops: GA kopieren", copy.Ok && copy.Value!.Id != copySource.Id
            && copy.Value.Address == GroupAddress.ThreeLevel(1, 1, 4), copy.Value?.Address.ToString());
        Check("Ops: GA-Kopie erhält Metadaten, aber keine Links",
            p.Workbench.Groups[copy.Value!.Id].Bits == 1 && copy.Value.Description == copySource.Description
            && !ProjectQuery.ComObjectRefs(p).Any(r => r.Ref.Links.Contains(copy.Value.Id)));
        var copyAddress = copy.Value.Address;
        Check("Ops: GA-Kopie auf belegte Adresse abgelehnt",
            ProjectOperations.CopyGroupAddress(p, copySource, copySource.Address, null, undo).Status == ProjectEditStatus.AddressInUse
            && ProjectQuery.FindGroupAddress(p, copyAddress)?.Id == copy.Value.Id);

        var second = ProjectQuery.FindGroupAddress(p, GroupAddress.ThreeLevel(1, 1, 2))!;
        var newRangeMove = ProjectOperations.MoveGroupAddress(p, second, GroupAddress.ThreeLevel(2, 3, 4), undo);
        Check("Ops: GA in neuen Bereich verschieben legt Haupt-/Mittelbereich an",
            newRangeMove.Ok && second.Address == GroupAddress.ThreeLevel(2, 3, 4)
            && ProjectQuery.FindRangeFor(opsInstallation, second.Address)?.Id == "GR-4",
            ProjectQuery.FindRangeFor(opsInstallation, second.Address)?.Id ?? "(fehlt)");

        var source = ProjectQuery.FindGroupAddress(p, GroupAddress.ThreeLevel(1, 1, 1))!;
        var moved = ProjectOperations.MoveGroupAddress(p, source, GroupAddress.ThreeLevel(1, 2, 6), undo);
        Check("Ops: GA verschieben", moved.Ok && source.Address == GroupAddress.ThreeLevel(1, 2, 6)
            && ProjectQuery.FindRangeFor(opsInstallation, source.Address)?.Id == "GR-2",
            source.Address.ToString());
        Check("Ops: GA-Verschieben behält Links",
            ProjectQuery.ComObjectRefs(p).Single().Ref.Links.SequenceEqual(["GA-1"]));
        before = OpsXml(p);
        Check("Ops: GA auf belegte Adresse verschieben abgelehnt",
            ProjectOperations.MoveGroupAddress(p, source, copy.Value.Address, undo).Status == ProjectEditStatus.AddressInUse
            && OpsXml(p) == before);

        var linkedObject = ProjectQuery.ComObjectRefs(p).Single().Ref;
        linkedObject.Links.Add("GA-2");
        var deleting = ProjectOperations.DeleteGroupAddress(p, source, undo);
        Check("Ops: GA löschen entfernt Links, Metadaten und Sendeadresse",
            deleting.Ok && linkedObject.Links.SequenceEqual(["GA-2"])
            && !p.Workbench.Groups.ContainsKey("GA-1")
            && p.Workbench.Objects.Values.Single().SendingGroupId is null
            && !p.Workbench.Objects.Values.Single().Connectors.ContainsKey("GA-1"));
        Check("Ops: Löschen ersetzt die Sendeadresse nicht still", deleting.Message is not null, deleting.Message);
        Check("Ops: GA-Löschen ist genau ein Undo-Schritt", undo.Steps == stepsAfterRename + 4, undo.Steps.ToString());
        var restored = undo.Undo()!;
        Check("Ops: Undo stellt GA samt Link und Sendeadresse wieder her",
            ProjectQuery.FindGroupAddressById(restored, "GA-1") is not null
            && ProjectQuery.ComObjectRefs(restored).Single().Ref.Links.Contains("GA-1")
            && restored.Workbench.Objects.Values.Single().SendingGroupId == "GA-1");

        // Full range: no allocation, no mutation, no undo step.
        var full = OpsProject();
        full.Installations[0].MainRanges.Clear();
        var fullRange = new GroupRange { Id = "GR-FULL", RangeStart = 1, RangeEnd = 3 };
        fullRange.Addresses.Add(Ga("FULL-1", "eins", 0, 0, 1));
        fullRange.Addresses.Add(Ga("FULL-2", "zwei", 0, 0, 2));
        fullRange.Addresses.Add(Ga("FULL-3", "drei", 0, 0, 3));
        full.Installations[0].MainRanges.Add(fullRange);
        var fullUndo = new UndoStack(); fullUndo.Reset(full);
        var fullBefore = OpsXml(full);
        var fullResult = ProjectOperations.CreateGroupAddress(full, null, "Nix", null, full.Installations[0], null, fullUndo);
        Check("Ops: volle GA-Reichweite lehnt Anlegen ab",
            fullResult.Status == ProjectEditStatus.NoFreeAddress && OpsXml(full) == fullBefore && fullUndo.Steps == 0,
            fullResult.Status.ToString());
    }
}

// ---- CSV-Helfer: RFC 4180 und Formel-Neutralisierung ----
{
    Check("CSV: leeres Feld", Csv.Field(null) == "" && Csv.Field("") == "");
    Check("CSV: unkritisches Feld unverändert", Csv.Field("Wohnzimmer") == "Wohnzimmer");
    Check("CSV: Trennzeichen, Anfuehrungszeichen und Zeilenumbruch zitiert",
        Csv.Field("a;b") == "\"a;b\"" && Csv.Field("a\"b") == "\"a\"\"b\"" && Csv.Field("a\r\nb") == "\"a\r\nb\"");
    Check("CSV: Komma nur bei Komma-Trennung zitiert",
        Csv.Field("a,b") == "a,b" && Csv.Field("a,b", ',') == "\"a,b\"");
    Check("CSV: Formeln und Tab/CR am Zellanfang neutralisiert",
        Csv.Field("=1+1") == "'=1+1" && Csv.Field("+SUM(1,1)") == "'+SUM(1,1)"
        && Csv.Field("-5") == "'-5" && Csv.Field("@cmd") == "'@cmd"
        && Csv.Field("\t=1") == "'\t=1" && Csv.Field("\r=1") == "\"'\r=1\"");
    Check("CSV: Formel nach führendem Leerraum neutralisiert",
        Csv.Field("  =1+1") == "'  =1+1" && Csv.Field("\t  @cmd") == "'\t  @cmd");
    Check("CSV: Zeile verbindet maskierte Felder",
        Csv.Row(';', "1/1/1", "=1+1", "a;b") == "1/1/1;'=1+1;\"a;b\"");
}

// ---- Katalogbibliothek: Import ersetzt atomar, meldet Ersatz und Signaturdatei ----
{
    var storeRoot = Path.Combine(Path.GetTempPath(), "knx_catalog_store_selftest");
    if (Directory.Exists(storeRoot)) Directory.Delete(storeRoot, true);
    var library = Path.Combine(storeRoot, "library");
    var sources = Path.Combine(storeRoot, "sources");
    Directory.CreateDirectory(sources);

    // Two containers with the same library identity (same product/program ids), different bytes
    // and an optional vendor signature entry.
    byte[] CatalogContainer(string productName, bool signature)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open(), new UTF8Encoding(false)))
                writer.Write($$"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <KNX xmlns="http://knx.org/xml/prod/20"><ManufacturerData>
                      <Manufacturer RefId="M-00FA">
                        <Hardware><Hardware Id="M-00FA_H-1" Name="H" SerialNumber="1" VersionNumber="1"
                                   HasIndividualAddress="true" HasApplicationProgram="true">
                          <Products><Product Id="M-00FA_H-1_P-1" Text="{{productName}}"/></Products>
                          <Hardware2Programs><Hardware2Program Id="M-00FA_H-1_HP-1">
                            <ApplicationProgramRef RefId="M-00FA_A-1"/></Hardware2Program></Hardware2Programs>
                        </Hardware></Hardware>
                        <ApplicationPrograms><ApplicationProgram Id="M-00FA_A-1" Name="App"
                                                ApplicationNumber="1" ApplicationVersion="1">
                          <Static><ParameterTypes><ParameterType Id="PT-1" Name="N" SizeInBit="8" DataType="Number"/></ParameterTypes>
                            <Parameters><Parameter Id="P-1" Name="N" ParameterType="PT-1" Value="1"/></Parameters>
                            <ParameterRefs><ParameterRef Id="PR-1" RefId="P-1"/></ParameterRefs>
                          </Static></ApplicationProgram></ApplicationPrograms>
                      </Manufacturer>
                    </ManufacturerData></KNX>
                    """);
            if (signature)
                zip.CreateEntry("M-00FA/M-00FA.signature").Open().Write([1, 2, 3, 4]);
        }
        return buffer.ToArray();
    }

    var signedPath = Path.Combine(sources, "signed.knxprod");
    var unsignedPath = Path.Combine(sources, "unsigned.knxprod");
    File.WriteAllBytes(signedPath, CatalogContainer("Actuator A", signature: true));
    File.WriteAllBytes(unsignedPath, CatalogContainer("Actuator B", signature: false));

    var store = new Knx.Catalog.CatalogStore(library);
    var first = store.Import(signedPath);
    var firstBytes = File.ReadAllBytes(first.Path);
    Check("Import: Signaturdatei erkannt, kein Ersatz",
        store.LastImportReport is { HasSignatureFile: true, ReplacedExisting: false });

    // Windows refuses to replace an open file; the failed re-import must keep the good entry.
    if (OperatingSystem.IsWindows())
    {
        using var locked = new FileStream(first.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            store.Import(unsignedPath);
            Check("Import: fehlgeschlagener Re-Import bricht ab", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Check("Import: fehlgeschlagener Re-Import lässt Bibliothekseintrag unverändert",
                File.ReadAllBytes(first.Path).SequenceEqual(firstBytes));
            Check("Import: fehlgeschlagener Re-Import lässt keine Temp-Datei zurück",
                !Directory.EnumerateFiles(library, "*.tmp").Any());
        }
    }
    else
    {
        Console.WriteLine("  SKIP Import: gesperrter Bibliothekseintrag (Unix erlaubt Ersetzen offener Dateien)");
    }

    var second = store.Import(unsignedPath);
    Check("Import: gleiche Identität ersetzt denselben Eintrag und meldet Ersatz",
        Path.GetFileName(second.Path) == Path.GetFileName(first.Path)
        && store.LastImportReport is { HasSignatureFile: false, ReplacedExisting: true });
    Check("Import: keine Temp-Datei bleibt zurück", !Directory.EnumerateFiles(library, "*.tmp").Any());
}

// --- Portfolio-Registry (H11.8): offline index for several project files ---
{
    var portfolioRoot = Path.Combine(Path.GetTempPath(), "buschord_portfolio_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(portfolioRoot);
    var registryPath = Path.Combine(portfolioRoot, "portfolio.json");
    var subdirectory = Path.Combine(portfolioRoot, "sub");
    Directory.CreateDirectory(subdirectory);

    string WriteProjectFile(string relative, string content)
    {
        var path = Path.Combine(portfolioRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    var alphaPath = WriteProjectFile("alpha.knxproj", "alpha-v1");
    var betaPath = WriteProjectFile("beta.knxproj", "beta-v1");

    var registry = new PortfolioRegistry(registryPath);
    Check("Portfolio: leerer Start ohne Fehler", registry.Entries.Count == 0 && registry.LoadError is null);
    Check("Portfolio: Dokument noch nicht angelegt", !File.Exists(registryPath));

    var alpha = registry.Add(alphaPath);
    Check("Portfolio: stabile Id", alpha.Id.Length == 15 && alpha.Id.StartsWith("pf-"), alpha.Id);
    Check("Portfolio: Name aus Dateiname", alpha.Name == "alpha");
    Check("Portfolio: Größe und SHA-256 erfasst",
        alpha.SizeBytes == new FileInfo(alphaPath).Length && alpha.Sha256.Length == 64 && alpha.Sha256.All(Uri.IsHexDigit));
    Check("Portfolio: Revision startet bei 1", alpha.Revision == 1);
    Check("Portfolio: Duplikat liefert denselben Eintrag",
        registry.Add(alphaPath).Id == alpha.Id && registry.Entries.Count == 1);
    Check("Portfolio: Duplikat über Pfadvariante",
        registry.Add(Path.Combine(subdirectory, "..", "alpha.knxproj")).Id == alpha.Id && registry.Entries.Count == 1);
    Check("Portfolio: Find by id", registry.Find(alpha.Id)?.Path == Path.GetFullPath(alphaPath));
    Check("Portfolio: Find by path", registry.FindByPath(alphaPath)?.Id == alpha.Id);
    Check("Portfolio: Find unbekannt", registry.Find("pf-000000000000") is null && registry.FindByPath("nope") is null);

    var beta = registry.Add(betaPath, "Beta Anlage");
    Check("Portfolio: Alias-Name", beta.Name == "Beta Anlage" && registry.Entries.Count == 2);
    Check("Portfolio: neue Einträge zuerst", registry.Entries[0].Id == beta.Id);
    Check("Portfolio: Touch ordnet nach letzter Öffnung",
        registry.Touch(alpha.Id) && registry.Entries[0].Id == alpha.Id && registry.Entries[0].Revision == 1);
    Check("Portfolio: Rename ändert nur den Namen",
        registry.Rename(alpha.Id, "Alpha Haus")
        && registry.Find(alpha.Id) is { Name: "Alpha Haus", Revision: 1 } renamed
        && renamed.Path == alpha.Path);
    Check("Portfolio: Rename unbekannt", !registry.Rename("pf-000000000000", "x"));
    Check("Portfolio: Remove unbekannt", !registry.Remove("pf-000000000000"));

    var reloaded = new PortfolioRegistry(registryPath);
    Check("Portfolio: Persistenz nach Neuladen",
        reloaded.LoadError is null && reloaded.Entries.Count == 2
        && reloaded.Find(alpha.Id) is { Name: "Alpha Haus" } persisted && persisted.Sha256 == alpha.Sha256);
    var documentText = File.ReadAllText(registryPath);
    Check("Portfolio: Schema-Version im Dokument", documentText.Contains("\"schemaVersion\": 1"));
    Check("Portfolio: keine Temp-Datei", !Directory.EnumerateFiles(portfolioRoot, "*.tmp").Any());
    Check("Portfolio: Sicherung der letzten guten Fassung", File.Exists(registryPath + ".bak"));

    var stable = File.ReadAllBytes(registryPath);
    reloaded.Save();
    Check("Portfolio: Laden und Speichern ist byte-stabil", File.ReadAllBytes(registryPath).SequenceEqual(stable));

    var movedPath = Path.Combine(portfolioRoot, "gamma.knxproj");
    File.Copy(alphaPath, movedPath);
    Check("Portfolio: Move behält Id und Revision",
        registry.Move(alpha.Id, movedPath)
        && registry.Find(alpha.Id) is { Revision: 1 } movedEntry
        && movedEntry.Path == Path.GetFullPath(movedPath));
    Check("Portfolio: Move auf belegten Pfad wird abgelehnt",
        Throws<InvalidOperationException>(() => registry.Move(alpha.Id, betaPath)));
    Check("Portfolio: Move unbekannt", !registry.Move("pf-000000000000", movedPath));

    File.WriteAllText(movedPath, "alpha-v2");
    Check("Portfolio: gleiche Größe bleibt Ok",
        registry.State(registry.Find(alpha.Id)!) == PortfolioState.Ok);
    Check("Portfolio: Refresh erkennt Hash-Änderung",
        registry.Refresh(alpha.Id) && registry.Find(alpha.Id) is { Revision: 2 } refreshed
        && refreshed.Sha256 == PortfolioRegistry.Hash(movedPath));
    Check("Portfolio: Refresh ohne Änderung", !registry.Refresh(alpha.Id));

    File.WriteAllText(movedPath, "alpha-v2-plus");
    Check("Portfolio: Größenänderung wird erkannt",
        registry.State(registry.Find(alpha.Id)!) == PortfolioState.Changed);
    Check("Portfolio: zweite Hash-Änderung erhöht Revision",
        registry.Refresh(alpha.Id) && registry.Find(alpha.Id)?.Revision == 3);

    File.Delete(movedPath);
    var missing = registry.Summarize(alpha.Id);
    Check("Portfolio: fehlende Datei gemeldet",
        missing is { State: PortfolioState.Missing, Revision: 3 } && registry.State(registry.Find(alpha.Id)!) == PortfolioState.Missing);
    Check("Portfolio: fehlende Datei bleibt registriert", registry.Find(alpha.Id)?.Path == Path.GetFullPath(movedPath));

    var movedAway = WriteProjectFile("elsewhere/alpha-moved.knxproj", "alpha-v2-plus");
    var detected = registry.DetectMove(alpha.Id, portfolioRoot);
    Check("Portfolio: verschobene Datei erkannt", detected == Path.GetFullPath(movedAway), detected);
    Check("Portfolio: Move repariert den Pfad",
        detected is not null && registry.Move(alpha.Id, detected)
        && registry.State(registry.Find(alpha.Id)!) == PortfolioState.Ok
        && registry.Find(alpha.Id)?.Revision == 3);

    Check("Portfolio: Backup-Referenz angehängt",
        registry.AttachBackup(alpha.Id, "20261022T120000-aaaaaaaaaaaa")
        && !registry.AttachBackup(alpha.Id, "20261022T120000-aaaaaaaaaaaa")
        && registry.AttachBackup(alpha.Id, "20261022T130000-bbbbbbbbbbbb"));
    Check("Portfolio: Versions-Referenz gesetzt",
        registry.SetVersionRef(alpha.Id, "r3")
        && !registry.SetVersionRef(alpha.Id, "r3"));
    var summary = registry.Summarize(alpha.Id);
    Check("Portfolio: Zusammenfassung mit Revision und Backups",
        summary is { Revision: 3, State: PortfolioState.Ok, HasBackups: true, VersionRef: "r3" }
        && summary.BackupRefs.Count == 2 && summary.BackupRefs[^1] == "20261022T130000-bbbbbbbbbbbb");

    Check("Portfolio: Remove entfernt Eintrag", registry.Remove(beta.Id) && !registry.Remove(beta.Id));
    var afterRemove = new PortfolioRegistry(registryPath);
    Check("Portfolio: Entfernung und Referenzen persistiert",
        afterRemove.Entries.Count == 1
        && afterRemove.Find(alpha.Id) is { VersionRef: "r3" } kept && kept.BackupRefs.Count == 2);

    // Korruptes Dokument: letzte gute Fassung aus .bak, danach wieder schreibbar.
    var corruptRoot = Path.Combine(Path.GetTempPath(), "buschord_portfolio_corrupt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(corruptRoot);
    var corruptPath = Path.Combine(corruptRoot, "portfolio.json");
    var firstPath = Path.Combine(corruptRoot, "first.knxproj");
    var secondPath = Path.Combine(corruptRoot, "second.knxproj");
    File.WriteAllText(firstPath, "first");
    File.WriteAllText(secondPath, "second");
    var corrupt = new PortfolioRegistry(corruptPath);
    var first = corrupt.Add(firstPath);
    corrupt.Add(secondPath);
    Check("Portfolio: .bak enthält die vorherige Fassung", File.Exists(corruptPath + ".bak"));
    File.WriteAllText(corruptPath, "{ \"schemaVersion\": 1, \"entries\": [");

    var recovered = new PortfolioRegistry(corruptPath);
    Check("Portfolio: Korruption gemeldet",
        recovered.LoadError is not null && recovered.LoadError.Contains("corrupt") && recovered.LoadError.Contains("Recovered"),
        recovered.LoadError);
    Check("Portfolio: letzte gute Fassung wiederhergestellt",
        recovered.Entries.Count == 1 && recovered.Entries[0].Id == first.Id);
    Check("Portfolio: korruptes Dokument aufbewahrt", File.Exists(corruptPath + ".corrupt"));
    recovered.Add(secondPath);
    var healed = new PortfolioRegistry(corruptPath);
    Check("Portfolio: nach Reparatur wieder vollständig",
        healed.LoadError is null && healed.Entries.Count == 2);

    var versionPath = Path.Combine(corruptRoot, "future.json");
    File.WriteAllText(versionPath, "{\"schemaVersion\":99,\"entries\":[]}");
    var unsupported = new PortfolioRegistry(versionPath);
    Check("Portfolio: unbekanntes Schema wird abgelehnt",
        unsupported.LoadError is not null && unsupported.Entries.Count == 0);

    Directory.Delete(portfolioRoot, recursive: true);
    Directory.Delete(corruptRoot, recursive: true);
}

// --- Projekt-Revisionen: knxproj-Roundtrip, Inhalt-Hash und Altprojekt-Kompatibilität ---
{
    var revisionDirectory = Path.Combine(Path.GetTempPath(), "knx-revisions-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(revisionDirectory);
    try
    {
        var revisionPath = Path.Combine(revisionDirectory, "revision.knxproj");
        var revisionProject = BuildProject("P-REV", "Revisionen", Dev("1.1.1", "Aktor"));
        revisionProject.MasterData.Signature = "native-signature";
        Knxproj.Save(revisionProject, revisionPath);
        var revision = ProjectRevisions.Capture(revisionProject, revisionPath, "Abnahme",
            new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(2)));
        Knxproj.Save(revisionProject, revisionPath);
        var revisionLoaded = Knxproj.Load(revisionPath);
        var revisionReloaded = revisionLoaded.Workbench.Revisions.Single();
        Check("Revision: knxproj-Roundtrip", revisionReloaded.Id == revision.Id
            && revisionReloaded.Number == 1 && revisionReloaded.Description == "Abnahme"
            && revisionReloaded.SourcePath == revisionPath
            && revisionReloaded.SourceHash == revision.SourceHash
            && revisionReloaded.ContentHash == revision.ContentHash
            && revisionReloaded.SignatureStatus == RevisionSignatureStatus.Unverified
            && revisionReloaded.SignatureHash == revision.SignatureHash);
        Check("Revision: Inhalt-Hash überlebt Speichern/Laden",
            ProjectRevisions.ContentHash(revisionLoaded) == revision.ContentHash);
        var contentBefore = ProjectRevisions.ContentHash(revisionProject);
        ProjectRevisions.Capture(revisionProject, description: "ohne Änderung");
        Check("Revision: Log-Eintrag ändert den Inhalt-Hash nicht",
            ProjectRevisions.ContentHash(revisionProject) == contentBefore);
        var device = ProjectQuery.DeviceList(revisionProject).Single();
        device.ParameterValues["mode"] = "\"dim\"";
        Check("Revision: Parameteränderung ändert den Inhalt-Hash",
            ProjectRevisions.ContentHash(revisionProject) != contentBefore);

        var oldPath = Path.Combine(revisionDirectory, "old.knxproj");
        var oldProject = BuildProject("P-OLD", "Altprojekt", Dev("1.1.2", "Taster"));
        Knxproj.Save(oldProject, oldPath);
        var oldLoaded = Knxproj.Load(oldPath);
        Check("Revision: Altprojekt ohne Log bleibt ladbar", oldLoaded.Workbench.Revisions.Count == 0);

        var oldMetadata = BuildProject("P-OLD-M", "Altmetadaten", Dev("1.1.3", "Sensor"));
        oldMetadata.Workbench.Devices["ghost"] = new Knx.Workbench.DeviceMetadata();
        var oldXml = Knxproj.ToXml(oldMetadata);
        var oldWorkbench = oldXml.Descendants(System.Xml.Linq.XName.Get("Workbench", "urn:buschord:project:1")).Single();
        var oldData = System.Text.Json.Nodes.JsonNode.Parse(oldWorkbench.Attribute("Data")!.Value)!.AsObject();
        oldData.Remove("Revisions");
        oldWorkbench.SetAttributeValue("Data", oldData.ToJsonString());
        var oldMetadataLoaded = Knxproj.FromXml(oldXml);
        Check("Revision: altes Workbench-JSON ohne Revisions bleibt ladbar",
            oldMetadataLoaded.Workbench.Revisions.Count == 0
            && oldMetadataLoaded.Workbench.Devices.ContainsKey("ghost"));
    }
    finally
    {
        Directory.Delete(revisionDirectory, recursive: true);
    }
}

// ---- B6: Segmentmedium, Domäne und Koppler-Hardware über knxproj ----
{
    var planning = new Project { Id = "P-PLAN", Name = "Planung" };
    var planInstallation = new Installation { Id = "I-PLAN", Name = "Plan" };
    var planArea = new Area { Id = "A-PLAN", Address = 1 };
    var planLine = new Line { Id = "L-PLAN", Address = 1, MediumTypeRefId = "MT-0", DomainAddress = "0x0A0B" };
    var mainSegment = new Segment { Id = "S-PLAN-0", Address = 0, MediumTypeRefId = "MT-0", DomainAddress = "0x0001" };
    var subSegment = new Segment { Id = "S-PLAN-1", Address = 1, MediumTypeRefId = "MT-2" };
    subSegment.Devices.Add(new DeviceInstance
    {
        Id = "1.1.2",
        Address = new IndividualAddress(1, 1, 2),
        Name = "Funk-Taster",
        ProductRefId = "M-0083_H-250-17-O0072_P-1",
        Hardware2ProgramRefId = "M-0083_H-250-17-O0072_HP-1",
    });
    planLine.Segments.Add(mainSegment);
    planLine.Segments.Add(subSegment);
    planArea.Lines.Add(planLine);
    planInstallation.Areas.Add(planArea);
    planning.Installations.Add(planInstallation);

    var planXml = Knxproj.ToXml(planning).Descendants().First(e => e.Name.LocalName == "Segment");
    Check("B6: Segment schreibt das ETS-Attribut Number", planXml.Attribute("Number")?.Value == "0"
        && planXml.Attribute("Address") is null);
    Check("B6: Segment-Medium im XML", planXml.Attribute("MediumTypeRefId")?.Value == "MT-0");
    Check("B6: Segment-Domäne im XML", planXml.Attribute("DomainAddress")?.Value == "0x0001");

    var planningPath = Path.Combine(Path.GetTempPath(), "knx_selftest", "planning.knxproj");
    Knxproj.Save(planning, planningPath);
    var loadedLine = Knxproj.Load(planningPath).Installations[0].Areas[0].Lines[0];
    Check("B6: Linien-Domäne round-trip", loadedLine.DomainAddress == "0x0A0B", loadedLine.DomainAddress);
    Check("B6: Segment-Medium round-trip", loadedLine.Segments[0].MediumTypeRefId == "MT-0"
        && loadedLine.Segments[1].MediumTypeRefId == "MT-2");
    Check("B6: Segment-Domäne round-trip", loadedLine.Segments[0].DomainAddress == "0x0001");
    Check("B6: Segmentnummer round-trip", loadedLine.Segments[1].Address == 1);

    // Projects saved by earlier BusChord versions used Segment@Address; the ETS attribute is Number.
    var legacy = Knxproj.FromXml(System.Xml.Linq.XDocument.Parse("""
        <KNX xmlns="http://knx.org/xml/project/23"><Project Id="P-LEGACY" Name="Alt"><Installations>
          <Installation InstallationId="I-1" Name="Alt"><Topology>
            <Area Id="A-0" Address="0" Name=""><Line Id="L-1" Address="1" Name="" MediumTypeRefId="MT-0">
              <Segment Id="S-0" Address="1" Name="Alt"/>
            </Line></Area>
          </Topology></Installation></Installations></Project></KNX>
        """));
    var migrated = Knxproj.ToXml(legacy).Descendants().Single(e => e.Name.LocalName == "Segment");
    Check("B6: altes Segment@Address wird beim Speichern zu Number",
        migrated.Attribute("Number")?.Value == "1" && migrated.Attribute("Address") is null,
        migrated.ToString());

    // Manufacturer hardware data travels in the project archive; the check reads it from there.
    var hardware = """
        <?xml version="1.0" encoding="utf-8"?>
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-0083">
          <Hardware Id="M-0083_H-250-17-O0072" Name="KNX RF+ Funk Linienkoppler" BusCurrent="7"
                   IsCoupler="true" IsRFRetransmitter="true">
            <Products><Product Id="M-0083_H-250-17-O0072_P-1" Text="RF coupler"/></Products>
            <Hardware2Programs><Hardware2Program Id="M-0083_H-250-17-O0072_HP-1" MediumTypes="MT-2">
              <ApplicationProgramRef RefId="M-0083_A-1"/></Hardware2Program></Hardware2Programs>
          </Hardware>
          <Hardware Id="M-0083_H-83-1" Name="STV-0640.02" BusCurrent="640" IsPowerSupply="true" IsChoke="true">
            <Products><Product Id="M-0083_H-83-1_P-1" Text="PSU"/></Products>
            <Hardware2Programs><Hardware2Program Id="M-0083_H-83-1_HP-1" MediumTypes="MT-0">
              <ApplicationProgramRef RefId="M-0083_A-2"/></Hardware2Program></Hardware2Programs>
          </Hardware>
        </Manufacturer></ManufacturerData></KNX>
        """;
    var archivePath = Path.Combine(Path.GetTempPath(), "knx_selftest", "planning-hardware.knxproj");
    using (var file = File.Create(archivePath))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    {
        using (var entry = new StreamWriter(zip.CreateEntry("0.xml").Open(), new UTF8Encoding(false)))
            Knxproj.ToXml(planning).Save(entry);
        using (var entry = new StreamWriter(zip.CreateEntry("M-0083/Hardware.xml").Open(), new UTF8Encoding(false)))
            entry.Write(hardware);
    }
    var withHardware = Knxproj.Load(archivePath);
    Check("B6: Koppler-Fakten aus dem Archiv",
        withHardware.Hardware.TryGetValue("M-0083_H-250-17-O0072_HP-1", out var coupler)
        && coupler.IsCoupler && coupler.BusCurrent == 7,
        string.Join(",", withHardware.Hardware.Keys));
    Check("B6: Spannungsversorgung aus dem Archiv",
        withHardware.Hardware.TryGetValue("M-0083_H-83-1_P-1", out var supply)
        && supply.IsPowerSupply && supply.IsChoke && supply.BusCurrent == 640);
}

Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;
