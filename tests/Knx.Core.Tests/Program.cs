using System.Text.Json;
using Knx;

// Self-Check für den Knxproj-Kern: Round-Trip (erzeugen → speichern → laden → vergleichen)
// und Auswertung des echten ETS6-Katalogs. Lauf: dotnet run --project tests/Knx.Core.Tests
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

// --- echter ETS6-Katalog, wenn vorhanden (aus der Analyse extrahiert/auf Platte) ---
var catalog = "C:\\Program Files (x86)\\ETS6\\knx_master.xml";
if (!File.Exists(catalog)) catalog = Path.Combine(AppContext.BaseDirectory, "knx_master.xml");
if (File.Exists(catalog))
{
    var (version, types) = MasterData.LoadDatapointTypes(catalog);
    var (manufacturers, _) = MasterData.CountCatalogEntries(catalog);
    Console.WriteLine($"  Katalog-Version {version}: {types.Count} Datapoint-Typen, {manufacturers} Hersteller");
    Check("DPT >= 50", types.Count >= 50, types.Count.ToString());
    Check("Hersteller >= 500", manufacturers >= 500, manufacturers.ToString());
    var sw = types.FirstOrDefault(t => t.Name == "1.xxx");
    Check("DPT 1.xxx hat 24 Subtypen", sw?.Subtypes.Count == 24, sw?.Subtypes.Count.ToString());
    Check("DPST-1-1 Switch", sw?.Subtypes.Any(s => s is { Name: "DPT_Switch", Id: "DPST-1-1" }) == true);
}
else Console.WriteLine("  SKIP Katalog-Check (knx_master.xml nicht gefunden)");

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

Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;
