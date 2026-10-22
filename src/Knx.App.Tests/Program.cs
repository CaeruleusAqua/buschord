using System.Text;
using System.Text.Json;
using Knx;
using Knx.App.Services;
using Knx.Bus;
using Knx.Portfolio;
using Knx.ProjectCompare;
using Knx.Reports;
using Knx.Workbench;

// Self-Check für die Reports-Export-Pipeline
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name);
}

static List<List<string>> ParseCsv(string text, char separator = ';')
{
    var rows = new List<List<string>>();
    var row = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < text.Length; i++)
    {
        var c = text[i];
        if (quoted)
        {
            if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
            else if (c == '"') quoted = false;
            else field.Append(c);
        }
        else if (c == '"') quoted = true;
        else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
        else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
        {
            row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; i++;
        }
        else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
        else field.Append(c);
    }
    if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
    return rows;
}

void CheckNeutralized(string name, string csv, char separator = ';')
{
    var bad = ParseCsv(csv, separator).SelectMany(row => row)
        .Where(cell =>
        {
            var text = cell.TrimStart();
            return text.Length > 0 && text[0] is '=' or '+' or '-' or '@';
        })
        .ToList();
    Check(name, bad.Count == 0, string.Join(" | ", bad));
}

var project = new Project { Id = "P-1", Name = "Test" };
var inst = new Installation { Id = "I-1", Name = "Haus" };
var area = new Area { Id = "A-0", Address = 0, Name = "Innen" };
var line = new Line { Id = "L-1", Address = 1, Name = "Linie" };
var seg = new Segment { Id = "S-0", Address = 0, Name = "Seg" };
seg.Devices.Add(new DeviceInstance { Id = "1.0.1", Address = new IndividualAddress(1, 0, 1), Name = "Aktor", ProductRefId = "M-00FA" });
line.Segments.Add(seg);
area.Lines.Add(line);
inst.Areas.Add(area);
var main = new GroupRange { Id = "GR-1", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
main.Addresses.Add(new GroupAddressEntry { Id = "GA-1", Name = "Wohnzimmer", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1" });
inst.MainRanges.Add(main);
project.Installations.Add(inst);

var gaCsv = ProjectCsvExporter.GroupAddresses(project);
Check("GA-CSV enthält Header", gaCsv.StartsWith("Adresse;Name;Beschreibung;DatapointType;Puid"));
Check("GA-CSV enthält Wohnzimmer", gaCsv.Contains("Wohnzimmer"));
Check("GA-CSV enthält 1/1/1", gaCsv.Contains("1/1/1"));

var devCsv = ProjectCsvExporter.Devices(project);
Check("Device-CSV enthält Header", devCsv.StartsWith("Adresse;Name;ProduktRef;Beschreibung;Kommentar"));
Check("Device-CSV enthält Aktor", devCsv.Contains("Aktor"));

var topoCsv = ProjectCsvExporter.Topology(project);
Check("Topology-CSV enthält Header", topoCsv.StartsWith("Area;Line;Segment;Gerät;Adresse"));
Check("Topology-CSV enthält Aktor", topoCsv.Contains("Aktor"));

// CSV-Köpfe folgen der UI-Sprache; Deutsch bleibt die Vorgabe.
Strings.Language = "en";
Check("CSV: Gruppenadress-Kopf englisch",
    ProjectCsvExporter.GroupAddresses(project).StartsWith("Address;Name;Description;DatapointType;Puid"));
Check("CSV: Geräte-Kopf englisch",
    ProjectCsvExporter.Devices(project).StartsWith("Address;Name;ProductRef;Description;Comment"));
Check("CSV: Topologie-Kopf englisch",
    ProjectCsvExporter.Topology(project).StartsWith("Area;Line;Segment;Device;Address"));
Strings.Language = "de";
Check("CSV: Kopf bleibt deutsch ohne Sprachwechsel",
    ProjectCsvExporter.GroupAddresses(project).StartsWith("Adresse;Name;Beschreibung;DatapointType;Puid") &&
    ProjectCsvExporter.Devices(project).StartsWith("Adresse;Name;ProduktRef;Beschreibung;Kommentar") &&
    ProjectCsvExporter.Topology(project).StartsWith("Area;Line;Segment;Gerät;Adresse"));

// ---------------------------------------------------------------- CSV-Sicherheit

Check("CSV: Formelzeichen wird mit Apostroph entschärft",
    Csv.Field("=1+1") == "'=1+1" && Csv.Field("+SUM(1,1)") == "'+SUM(1,1)"
    && Csv.Field("@cmd") == "'@cmd" && Csv.Field("-5") == "'-5");
Check("CSV: Formel nach führendem Leerraum wird entschärft",
    Csv.Field("  =1+1") == "'  =1+1" && Csv.Field("\t=1+1") == "'\t=1+1");
Check("CSV: RFC 4180 zitiert Trennzeichen, Anführungszeichen und Zeilenumbruch",
    Csv.Field("a;b") == "\"a;b\"" && Csv.Field("a\"b") == "\"a\"\"b\"" && Csv.Field("a\nb") == "\"a\nb\"");
Check("CSV: Komma bleibt bei Semikolon-Trennung unmaskiert",
    Csv.Field("a,b") == "a,b" && Csv.Field("a,b", ',') == "\"a,b\"");

// Namen und Beschreibungen stammen aus Projekt-/Katalog-XML und dürfen nie als Formel laufen.
var hostile = new Project { Id = "P-H", Name = "Hostile" };
var hostileInst = new Installation { Id = "I-H" };
var hostileArea = new Area { Id = "A-H", Address = 1, Name = "=1+1" };
var hostileLine = new Line { Id = "L-H", Address = 1, Name = "  +SUM(1,1)" };
hostileLine.Devices.Add(new DeviceInstance
{
    Id = "D-H",
    Address = new IndividualAddress(1, 1, 2),
    Name = "\t=cmd|' /C calc'!A0",
    ProductRefId = "-2+3",
    Description = "  +SUM(1,1)",
    Comment = "@cmd",
});
hostileArea.Lines.Add(hostileLine);
hostileInst.Areas.Add(hostileArea);
var hostileRange = new GroupRange { Id = "GR-H", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
hostileRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-H",
    Name = "=1+1;\"quote\"\nline",
    Description = "@cmd",
    Address = GroupAddress.ThreeLevel(1, 1, 1),
    DatapointType = "  =1+1",
});
hostileInst.MainRanges.Add(hostileRange);
hostile.Installations.Add(hostileInst);

var hostileGaCsv = ProjectCsvExporter.GroupAddresses(hostile);
Check("CSV: Gruppenadress-Kopf bleibt unverändert",
    hostileGaCsv.StartsWith("Adresse;Name;Beschreibung;DatapointType;Puid"));
Check("CSV: Gruppenadresse mit Semikolon, Anführungszeichen und Zeilenumbruch zitiert",
    hostileGaCsv.Contains("1/1/1;\"'=1+1;\"\"quote\"\"\nline\";'@cmd;'  =1+1;0"));
CheckNeutralized("CSV: Gruppenadressen ohne Formelzelle", hostileGaCsv);

var hostileDevCsv = ProjectCsvExporter.Devices(hostile);
Check("CSV: Geräte-Kopf bleibt unverändert",
    hostileDevCsv.StartsWith("Adresse;Name;ProduktRef;Beschreibung;Kommentar"));
Check("CSV: Gerätefelder entschärft",
    hostileDevCsv.Contains("1.1.2;'\t=cmd|' /C calc'!A0;'-2+3;'  +SUM(1,1);'@cmd"));
CheckNeutralized("CSV: Geräte ohne Formelzelle", hostileDevCsv);

var hostileTopoCsv = ProjectCsvExporter.Topology(hostile);
Check("CSV: Topologie-Kopf bleibt unverändert",
    hostileTopoCsv.StartsWith("Area;Line;Segment;Gerät;Adresse"));
CheckNeutralized("CSV: Topologie ohne Formelzelle", hostileTopoCsv);

var hostileLabels = LabelCreator.ToCsv(LabelCreator.DeviceLabels(hostile, new LabelOptions()));
Check("CSV: Etiketten-Kopf bleibt unverändert", hostileLabels.StartsWith("Adresse;Titel;Untertitel;Zusatz"));
CheckNeutralized("CSV: Etiketten ohne Formelzelle", hostileLabels);
CheckNeutralized("CSV: Gruppenadress-Etiketten ohne Formelzelle",
    LabelCreator.ToCsv(LabelCreator.GroupAddressLabels(hostile, new LabelOptions())));

var demo = WorkbenchJson.CreateDemo();
foreach (var row in ProjectQuery.Devices(demo)) row.Device.Name = "=1+1";
foreach (var entry in ProjectQuery.GroupAddresses(demo)) entry.Name = "@cmd";
foreach (var row in ProjectQuery.Devices(demo))
    foreach (var hostileObject in row.Device.ComObjectRefs) hostileObject.Text = "+SUM(1,1)";
var assocCsv = ProjectCsvExporter.Associations(demo);
Check("CSV: Zuordnungs-Kopf bleibt unverändert",
    assocCsv.StartsWith(Strings.Get("CsvAssociationsHeader")));
Check("CSV: Zuordnungen enthalten Datenzeilen", ParseCsv(assocCsv).Count > 1);
CheckNeutralized("CSV: Zuordnungen ohne Formelzelle", assocCsv);

var reportCsv = ReportCsv.Render(new[]
{
    ("=1+1", "+SUM(1,1)", "@cmd"),
    ("Info", "Name;mit\nZeilen", "\"quoted\""),
});
Check("CSV: Befund-Kopf bleibt unverändert", reportCsv.StartsWith(Strings.Get("ReportCsvHeader")));
Check("CSV: Befundfeld mit Semikolon und Zeilenumbruch zitiert",
    reportCsv.Contains("\"Name;mit\nZeilen\""));
CheckNeutralized("CSV: Befunde ohne Formelzelle", reportCsv);

var diff = new DifferenceReport
{
    LeftProjectName = "A",
    RightProjectName = "B",
    GeneratedAt = DateTimeOffset.UnixEpoch,
};
diff.Items.Add(new DifferenceItem
{
    Path = "=1+1",
    ObjectType = "Device",
    ObjectName = "@cmd",
    Modification = ModificationType.Changed,
    OldValue = "+SUM(1,1)",
    NewValue = "  -2+3",
});
var compareCsv = ComparisonCsv.Render(diff);
Check("CSV: Vergleichs-Kopf bleibt unverändert", compareCsv.StartsWith(Strings.Get("CompareCsvHeader")));
CheckNeutralized("CSV: Vergleich ohne Formelzelle", compareCsv);

var monitorProject = new Project { Id = "P-M", Name = "Monitor" };
var monitorInst = new Installation { Id = "I-M" };
var monitorRange = new GroupRange { Id = "GR-M", RangeStart = 1, RangeEnd = 15 };
monitorRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-M", Name = "=1+1", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
});
monitorInst.MainRanges.Add(monitorRange);
monitorProject.Installations.Add(monitorInst);
var monitor = new GroupMonitor { Project = monitorProject };
monitor.Append(new BusTelegram
{
    Timestamp = DateTimeOffset.UnixEpoch,
    Source = "1.1.1",
    Telegram = new Telegram
    {
        Source = new IndividualAddress(1, 1, 1),
        Destination = new IndividualAddress(GroupAddress.ThreeLevel(1, 1, 1).Raw),
        IsGroupAddress = true,
        Apci = Apci.GroupValueWrite,
        Data = [1],
    },
});
var monitorCsv = monitor.ToCsv();
Check("CSV: Telegramm-Kopf bleibt unverändert",
    monitorCsv.StartsWith("Timestamp,Source,Destination,IsGroup,Apci,Data,Value,Unit,DatapointType,Name"));
Check("CSV: Telegrammname neutralisiert", monitorCsv.Contains(",'=1+1"));
CheckNeutralized("CSV: Telegramme ohne Formelzelle", monitorCsv, ',');

Strings.Language = "en";
Check("CSV: Etiketten-Kopf englisch",
    LabelCreator.ToCsv(Array.Empty<Label>()).StartsWith("Address;Title;Subtitle;Extra"));
Check("CSV: Befund-Kopf englisch",
    ReportCsv.Render(Array.Empty<(string, string, string)>()).StartsWith("Severity;Object;Description"));
Check("CSV: Vergleichs-Kopf englisch",
    ComparisonCsv.Render(diff).StartsWith("Path;Type;Object;Change;Old;New"));
Strings.Language = "de";

// ---------------------------------------------------------------- Projektbericht (HTML)

var tricky = new Project
{
    Id = "P-2",
    Name = "Haus & \"Garten\" <Nord> äöü",
    Comment = "<script>alert('x')</script>",
};
var trickyInst = new Installation { Id = "I-2", Name = "Haus" };
var trickyArea = new Area { Id = "A-2", Address = 1, Name = "Erdgeschoss <EG>" };
var trickyLine = new Line { Id = "L-2", Address = 1, Name = "Licht" };
var trickyDevice = new DeviceInstance
{
    Id = "D-2",
    Address = new IndividualAddress(1, 1, 1),
    Name = "Aktor <1> & \"x\"",
    ProductRefId = "M-00FA_H-1_P-1",
    Description = "äöü",
};
var comObject = new ComObjectInstanceRef { Id = "CO-1", ComObjectId = "O-1", Number = 1, Text = "Schalten <aus>" };
comObject.Links.Add("GA-1");
comObject.Links.Add("GA-9"); // dangling on purpose: link matrix counts it, check reports P041
trickyDevice.ComObjectRefs.Add(comObject);
trickyLine.Devices.Add(trickyDevice);
trickyArea.Lines.Add(trickyLine);
trickyInst.Areas.Add(trickyArea);

var trickyRange = new GroupRange { Id = "GR-2", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
trickyRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-1", Name = "Wohnzimmer <Licht>", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
});
trickyRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-2", Name = "Decke \"indirekt\"", Address = GroupAddress.ThreeLevel(1, 1, 2),
});
trickyInst.MainRanges.Add(trickyRange);
tricky.Installations.Add(trickyInst);

var fixedTime = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(2));
var reportOptions = new ProjectReportOptions { GeneratedAt = fixedTime };
var report = ProjectReport.Render(tricky, reportOptions);

Check("Bericht: Projektname maskiert", report.Contains("Haus &amp; &quot;Garten&quot; &lt;Nord&gt; äöü"));
Check("Bericht: Kommentar maskiert", report.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;"));
Check("Bericht: Gerätename maskiert", report.Contains("Aktor &lt;1&gt; &amp; &quot;x&quot;"));
Check("Bericht: Gruppenadressname maskiert", report.Contains("Wohnzimmer &lt;Licht&gt;"));
Check("Bericht: Anführungszeichen im Namen maskiert", report.Contains("Decke &quot;indirekt&quot;"));
Check("Bericht: Umlaute bleiben UTF-8", report.Contains("äöü"));
Check("Bericht: kein <script> im Dokument", !report.Contains("<script"));
Check("Bericht: keine externen URLs", !report.Contains("http://") && !report.Contains("https://"));
Check("Bericht: Titelseite mit Kennzahlen", report.Contains("Projektdokumentation") && report.Contains("18.10.2026 12:00"));
Check("Bericht: alle Abschnitte standardmäßig",
    report.Contains("Topologie und Geräte") && report.Contains("Kommunikationsobjekte") &&
    report.Contains("Verknüpfungsmatrix") && report.Contains("Projektprüfung"));
Check("Bericht: Druckumbrüche und Seitenzahlen", report.Contains("page-break-before") &&
    report.Contains("break-inside") && report.Contains("counter(page)") && report.Contains("counter(pages)"));
Check("Bericht: offener Link erscheint im Objektabschnitt", report.Contains("GA-9 (unbekannt)"));
Check("Bericht: deterministisch",
    report == ProjectReport.Render(tricky, new ProjectReportOptions { GeneratedAt = fixedTime }));

var matrix = ProjectReport.LinkMatrix(tricky);
Check("Verknüpfungsmatrix: eine Zeile", matrix.Count == 1, matrix.Count.ToString());
Check("Verknüpfungsmatrix: Gruppenadressen", matrix[0].GroupAddresses == 2, matrix[0].ToString());
Check("Verknüpfungsmatrix: mit DPT", matrix[0].WithDatapointType == 1, matrix[0].ToString());
Check("Verknüpfungsmatrix: verknüpft/unverknüpft", matrix[0].Linked == 1 && matrix[0].Unlinked == 1, matrix[0].ToString());
Check("Verknüpfungsmatrix: Verknüpfungen auf Bereichsadressen", matrix[0].Links == 1, matrix[0].ToString());
Check("Verknüpfungsmatrix: Bereichsbezeichnung", matrix[0].Range == "Licht (1..15)", matrix[0].Range);

var noSections = new ProjectReportOptions
{
    GeneratedAt = fixedTime,
    IncludeTitlePage = false,
    IncludeTopology = false,
    IncludeGroupAddresses = false,
    IncludeComObjects = false,
    IncludeLinkMatrix = false,
    IncludeFindings = false,
};
var bare = ProjectReport.Render(tricky, noSections);
Check("Bericht: Abschnitte abschaltbar", !bare.Contains("<section") && !bare.Contains("Projektprüfung"));
Check("Bericht: Rumpfdokument bleibt gültig",
    bare.StartsWith("<!DOCTYPE html>") && bare.TrimEnd().EndsWith("</html>"));

var onlyFindings = ProjectReport.Render(tricky, new ProjectReportOptions
{
    GeneratedAt = fixedTime,
    IncludeTitlePage = false,
    IncludeTopology = false,
    IncludeGroupAddresses = false,
    IncludeComObjects = false,
    IncludeLinkMatrix = false,
});
Check("Bericht: einzelner Abschnitt allein", onlyFindings.Contains("Projektprüfung") &&
    !onlyFindings.Contains("Topologie und Geräte") && !onlyFindings.Contains("Verknüpfungsmatrix"));

var emptyProject = new Project { Id = "P-0", Name = "Leeres Projekt" };
var emptyReport = ProjectReport.Render(emptyProject, new ProjectReportOptions { GeneratedAt = fixedTime });
Check("Bericht: leeres Projekt rendert", emptyReport.Contains("Leeres Projekt") && emptyReport.Contains("P001"));
Check("Bericht: leeres Projekt nennt leere Abschnitte",
    emptyReport.Contains("Keine Geräte im Projekt") && emptyReport.Contains("Keine Gruppenadressen im Projekt") &&
    emptyReport.Contains("Keine Kommunikationsobjekte im Projekt") &&
    emptyReport.Contains("Keine Gruppenbereiche im Projekt"));

// Der Bericht folgt der UI-Sprache, inklusive der Befundtexte aus der Projektprüfung.
Strings.Language = "en";
var englishReport = ProjectReport.Render(emptyProject, new ProjectReportOptions { GeneratedAt = fixedTime });
Check("Bericht: englische Überschriften",
    englishReport.Contains("Topology and devices") && englishReport.Contains("Communication objects") &&
    englishReport.Contains("Link matrix") && englishReport.Contains("Project documentation") &&
    englishReport.Contains("Project check"));
Check("Bericht: keine deutschen Überschriften auf Englisch",
    !englishReport.Contains("Topologie und Geräte") && !englishReport.Contains("Verknüpfungsmatrix") &&
    !englishReport.Contains("Projektdokumentation"));
Check("Bericht: englische Leerzustände",
    englishReport.Contains("No devices in the project") &&
    englishReport.Contains("No group addresses in the project") &&
    englishReport.Contains("No communication objects in the project") &&
    englishReport.Contains("No group ranges in the project"));
Check("Bericht: englische Befundmeldung",
    englishReport.Contains("Project contains no installation"));
var silentProject = new Project { Id = "P-SILENT", Name = "Still" };
silentProject.Installations.Add(new Installation { Id = "I-S", Name = "Haus" });
var silentReport = ProjectReport.Render(silentProject, new ProjectReportOptions { GeneratedAt = fixedTime });
Check("Bericht: englischer Leerzustand der Befunde",
    silentReport.Contains("0 errors, 0 warnings, 0 notes.") && silentReport.Contains("No findings"));
Check("Bericht: englische Sprachange",
    englishReport.Contains("<html lang=\"en\">") && report.Contains("<html lang=\"de\">"));
Strings.Language = "de";
Check("Bericht: deutsch bleibt Vorgabe",
    ProjectReport.Render(emptyProject, new ProjectReportOptions { GeneratedAt = fixedTime }) == emptyReport);

var reportPath = Path.Combine(Path.GetTempPath(), "knx_project_report_" + Guid.NewGuid().ToString("N") + ".html");
var written = ProjectReport.Write(tricky, reportPath, new ProjectReportOptions { GeneratedAt = fixedTime });
var reportBytes = File.ReadAllBytes(reportPath);
Check("Bericht: Datei geschrieben", File.Exists(reportPath) && written.Bytes == reportBytes.Length && written.Bytes > 1000);
Check("Bericht: Datei ist UTF-8 ohne BOM", reportBytes[0] == (byte)'<');
Check("Bericht: Datei entspricht Render", System.Text.Encoding.UTF8.GetString(reportBytes) == report);
File.Delete(reportPath);

// --- Portfolio-Anbindung an bestehende Einstellungen (H11.8) ---
{
    var settingsRoot = Path.Combine(Path.GetTempPath(), "buschord_settings_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(settingsRoot);
    var firstProject = Path.Combine(settingsRoot, "haus.knxproj");
    var secondProject = Path.Combine(settingsRoot, "halle.knxproj");
    var missingProject = Path.Combine(settingsRoot, "weg.knxproj");
    File.WriteAllText(firstProject, "haus");
    File.WriteAllText(secondProject, "halle");

    string Q(string value) => JsonSerializer.Serialize(value);

    // Datei im alten Format: nur bekannte Felder plus ein unbekanntes, das ignoriert werden muss.
    var settingsFile = Path.Combine(settingsRoot, "settings.json");
    File.WriteAllText(settingsFile, $$"""
    {
      "Theme": "Dunkel",
      "Language": "en",
      "ShowMonitorTimestamps": false,
      "ConfirmDelete": false,
      "TunnelAddress": "1.1.250",
      "LastProject": {{Q(firstProject)}},
      "RecentProjects": [{{Q(firstProject)}}, {{Q(secondProject)}}, {{Q(missingProject)}}],
      "FutureSetting": 42
    }
    """);

    var legacySettings = AppSettings.Load(settingsFile);
    Check("Einstellungen: alte Datei bleibt lesbar",
        legacySettings is { Theme: "Dunkel", Language: "en", ShowMonitorTimestamps: false, ConfirmDelete: false, TunnelAddress: "1.1.250" }
        && legacySettings.LastProject == Path.GetFullPath(firstProject)
        && legacySettings.RecentProjects.Count == 3);
    Check("Einstellungen: unbekanntes Feld wird ignoriert", legacySettings.RecentProjects[1] == Path.GetFullPath(secondProject));

    var portfolioFile = Path.Combine(settingsRoot, "portfolio.json");
    var registry = new PortfolioRegistry(portfolioFile);
    var beforeSeed = File.ReadAllBytes(settingsFile);
    var seeded = registry.Seed(legacySettings);
    Check("Portfolio: Seeding übernimmt nur vorhandene Projekte",
        seeded == 2 && registry.Entries.Count == 2 && registry.FindByPath(missingProject) is null);
    Check("Portfolio: letztes Projekt gilt als zuletzt geöffnet",
        registry.Entries[0].Path == Path.GetFullPath(firstProject));
    Check("Portfolio: Einstellungen bleiben unverändert",
        File.ReadAllBytes(settingsFile).SequenceEqual(beforeSeed)
        && legacySettings.LastProject == Path.GetFullPath(firstProject)
        && legacySettings.RecentProjects.Count == 3);
    Check("Portfolio: erneutes Seeding legt nichts doppelt an",
        registry.Seed(legacySettings) == 0 && registry.Entries.Count == 2);
    Check("Portfolio: eigene Datei neben den Einstellungen",
        PortfolioRegistry.DefaultPath != AppSettings.Path
        && Path.GetFileName(PortfolioRegistry.DefaultPath) == "portfolio.json");

    // Einzelprojekt-Verhalten der Einstellungen bleibt bestehen.
    legacySettings.TouchProject(secondProject);
    Check("Einstellungen: TouchProject unverändert",
        legacySettings.LastProject == Path.GetFullPath(secondProject)
        && legacySettings.RecentProjects[0] == Path.GetFullPath(secondProject)
        && legacySettings.RecentProjects.Count == 3);
    for (var i = 0; i < 15; i++) legacySettings.TouchProject(Path.Combine(settingsRoot, $"p{i}.knxproj"));
    Check("Einstellungen: Recent-Liste bleibt auf 10 begrenzt", legacySettings.RecentProjects.Count == 10);

    Directory.Delete(settingsRoot, recursive: true);
}

Console.WriteLine(failures.Count == 0 ? "REPORTS OK" : $"{failures.Count} FEHLER");
Environment.Exit(failures.Count == 0 ? 0 : 1);
