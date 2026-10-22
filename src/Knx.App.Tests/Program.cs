using System.IO.Compression;
using System.Security.Cryptography;
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

// --- Revision/Signatur-Abschnitt im Projektbericht --------------------------------------
{
    var revisionProject = new Project
    {
        Id = "P-REV",
        Name = "Revisionsprojekt <Nord>",
        MasterData = { Signature = "native-signature" },
    };
    var revisionTime = new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.FromHours(2));
    var revision = ProjectRevisions.Capture(revisionProject, @"C:\Kunde Geheim\project.knxproj",
        "Abnahme <1>", revisionTime);
    var revisionReport = ProjectReport.Render(revisionProject, new ProjectReportOptions { GeneratedAt = fixedTime });
    Check("Bericht: Revision im Titel", revisionReport.Contains("Revision")
        && revisionReport.Contains($"{revision.Number} · 22.10.2026 11:00"));
    Check("Bericht: Revisionsabschnitt mit Hash und Signaturstatus",
        revisionReport.Contains("id=\"revisions\"")
        && revisionReport.Contains(revision.ContentHash)
        && revisionReport.Contains("Signature: unverified"));
    Check("Bericht: Revisionsbeschreibung maskiert", revisionReport.Contains("Abnahme &lt;1&gt;"));
    Check("Bericht: Quellpfad erscheint nicht", !revisionReport.Contains("Kunde Geheim"));
    Check("Bericht: Revisionsabschnitt deterministisch",
        revisionReport == ProjectReport.Render(revisionProject, new ProjectReportOptions { GeneratedAt = fixedTime }));
    var withoutRevisions = ProjectReport.Render(revisionProject, new ProjectReportOptions
    {
        GeneratedAt = fixedTime,
        IncludeRevisions = false,
    });
    Check("Bericht: Revisionsabschnitt abschaltbar", !withoutRevisions.Contains("id=\"revisions\""));
}

// ---------------------------------------------------------------- Abnahmeprotokoll (P13)

var ap = new Project
{
    Id = "P-AP",
    Name = "Haus & <Objekt>",
    Comment = "<script>alert('x')</script>",
    Guid = "6f1e8c2a-0000-0000-0000-000000000000",
};
ap.MasterData.Signature = "SIG-ABC";
var apInst = new Installation { Id = "I-AP", Name = "Haus" };
var apArea = new Area { Id = "A-AP", Address = 1, Name = "Erdgeschoss" };
var apLine = new Line { Id = "L-AP", Address = 1, Name = "Licht" };
var apDevice = new DeviceInstance
{
    Id = "D-AP-1",
    Address = new IndividualAddress(1, 1, 1),
    Name = "Aktor <1> & \"x\" äöü",
    ProductRefId = "M-00FA_H-1_P-1",
    Hardware2ProgramRefId = "M-00FA_H-1_HP-1",
    Description = "äöü",
};
var apObject = new ComObjectInstanceRef
{
    Id = "CO-AP-1", ComObjectId = "O-1", Number = 1, Text = "Schalten <aus>", FunctionText = "Schalten",
};
apObject.Links.Add("GA-AP-1");
apObject.Links.Add("GA-AP-9"); // dangling on purpose: the matrix names it as unknown
apDevice.ComObjectRefs.Add(apObject);
apLine.Devices.Add(apDevice);
apArea.Lines.Add(apLine);
apInst.Areas.Add(apArea);
var apArea2 = new Area { Id = "A-AP2", Address = 2, Name = "Obergeschoss" };
var apLine2 = new Line { Id = "L-AP2", Address = 1, Name = "Licht 2" };
apLine2.Devices.Add(new DeviceInstance
{
    Id = "D-AP-2", Address = new IndividualAddress(2, 1, 5), Name = "Dimmer", ProductRefId = "M-00FA_H-1_P-2",
});
apArea2.Lines.Add(apLine2);
apInst.Areas.Add(apArea2);
var apRange = new GroupRange { Id = "GR-AP", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
apRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-AP-1", Name = "Decke <Licht>", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
});
apRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-AP-2", Name = "Wand \"indirekt\"", Address = GroupAddress.ThreeLevel(1, 1, 2),
});
apInst.MainRanges.Add(apRange);
ap.Installations.Add(apInst);

var apTime = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(2));
var apOptions = new AcceptanceProtocolOptions { GeneratedAt = apTime };
var apHtml = AcceptanceProtocol.Render(ap, apOptions);
var apHash = AcceptanceProtocol.ComputeHash(ap, apOptions);

Check("Protokoll: Kopf mit Projekt, Signatur und Hash",
    apHtml.Contains("Abnahmeprotokoll") && apHtml.Contains("P-AP") && apHtml.Contains("6f1e8c2a") &&
    apHtml.Contains("SIG-ABC") && apHtml.Contains("vorhanden, unverändert übernommen") &&
    apHtml.Contains("sha256:" + apHash));
Check("Protokoll: Hash ist 64-stelliges Kleinbuch-Hex",
    apHash.Length == 64 && apHash.All(char.IsAsciiHexDigit) && apHash == apHash.ToLowerInvariant());
Check("Protokoll: Dokumentversion im Kopf",
    apHtml.Contains("<th scope=\"row\">Dokumentversion</th><td>" + AcceptanceProtocol.Version + "</td>"));
Check("Protokoll: Topologie, Geräte und Container",
    apHtml.Contains("Anlage und Topologie") && apHtml.Contains("M-00FA_H-1_P-1") &&
    apHtml.Contains("M-00FA_H-1_HP-1") && apHtml.Contains("Haus · Erdgeschoss · Licht"));
Check("Protokoll: Gruppenadressen mit DPT und Bereich",
    apHtml.Contains("1/1/1") && apHtml.Contains("DPST-1-1") && apHtml.Contains("Licht (1..15)"));
Check("Protokoll: Funktionsmatrix mit Verknüpfung und offenem Link",
    apHtml.Contains("Funktionsmatrix") && apHtml.Contains("Decke &lt;Licht&gt;") &&
    apHtml.Contains("GA-AP-9 (unbekannt)"));
Check("Protokoll: Prüfzeilen starten als nicht geprüft",
    apHtml.Contains("Sichtprüfung") && apHtml.Contains("☐ nicht geprüft") &&
    apHtml.Contains("Prüfspalten vor Ort ausfüllen") &&
    System.Text.RegularExpressions.Regex.Matches(apHtml, "<td class=\"check\">☐</td>").Count == 2 * 4 &&
    apHtml.Contains("kein Download aufgezeichnet") && apHtml.Contains("<td class=\"fill\"></td>"));
Check("Protokoll: Messwert und Bemerkung sind leere Platzhalter",
    apHtml.Contains("Messwert") && apHtml.Contains("Bemerkung") && apHtml.Contains("Mängelliste"));
Check("Protokoll: Unterschriftenblock",
    apHtml.Contains("Ort, Datum") && apHtml.Contains("Techniker/in") &&
    apHtml.Contains("Bauherr/Bauherrin") && apHtml.Contains("Unterschrift"));
Check("Protokoll: Werte maskiert",
    apHtml.Contains("Haus &amp; &lt;Objekt&gt;") && apHtml.Contains("Aktor &lt;1&gt; &amp; &quot;x&quot; äöü") &&
    apHtml.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;"));
Check("Protokoll: kein Skript und keine externen URLs",
    !apHtml.Contains("<script") && !apHtml.Contains("http://") && !apHtml.Contains("https://"));
Check("Protokoll: Gerätezeilen nach Adresse sortiert",
    AcceptanceProtocol.DeviceRows(ap, apOptions) is var apRows && apRows.Count == 2 &&
    apRows[0].Device.Address.ToString() == "1.1.1" && apRows[1].Device.Address.ToString() == "2.1.5");
Check("Protokoll: ohne Ladezustand ist jedes Gerät offen",
    AcceptanceProtocol.OpenItems(ap, apOptions).Count == 2 &&
    AcceptanceProtocol.OpenItems(ap, apOptions).All(i => i.Kind == AcceptanceOpenItemKind.DownloadNotRecorded) &&
    AcceptanceProtocol.CommissioningMode(ap, apOptions) == AcceptanceCommissioningMode.NotRecorded &&
    apHtml.Contains("Kein Download aufgezeichnet"));
Check("Protokoll: deterministisch",
    apHtml == AcceptanceProtocol.Render(ap, new AcceptanceProtocolOptions { GeneratedAt = apTime }) &&
    apHash == AcceptanceProtocol.ComputeHash(ap, new AcceptanceProtocolOptions { GeneratedAt = apTime }));
Check("Protokoll: Hash folgt dem Erstellungszeitpunkt",
    apHash != AcceptanceProtocol.ComputeHash(ap, new AcceptanceProtocolOptions { GeneratedAt = apTime.AddMinutes(1) }));

// Dokumentsprache folgt der Option, nicht der UI-Sprache.
Strings.Language = "en";
var apGerman = AcceptanceProtocol.Render(ap, new AcceptanceProtocolOptions { GeneratedAt = apTime, Language = "de" });
Strings.Language = "de";
var apEnglish = AcceptanceProtocol.Render(ap, new AcceptanceProtocolOptions { GeneratedAt = apTime, Language = "en" });
Check("Protokoll: deutsche Sprache bleibt bei UI Englisch",
    apGerman.Contains("<html lang=\"de\">") && apGerman.Contains("Geräteprüfung") &&
    apGerman.Contains("nicht geprüft") && !apGerman.Contains("Device tests"));
Check("Protokoll: englische Überschriften",
    apEnglish.Contains("<html lang=\"en\">") && apEnglish.Contains("Acceptance protocol") &&
    apEnglish.Contains("Device tests") && apEnglish.Contains("not tested") &&
    apEnglish.Contains("Acceptance and signatures"));
Check("Protokoll: keine deutschen Überschriften auf Englisch",
    !apEnglish.Contains("Geräteprüfung") && !apEnglish.Contains("nicht geprüft") &&
    !apEnglish.Contains("Abnahmeprotokoll") && !apEnglish.Contains("Mängelliste"));

// Leeres Projekt.
var apEmpty = new Project { Id = "P-AP0", Name = "Leeres Projekt" };
var apEmptyHtml = AcceptanceProtocol.Render(apEmpty, new AcceptanceProtocolOptions { GeneratedAt = apTime });
Check("Protokoll: leeres Projekt rendert",
    apEmptyHtml.StartsWith("<!DOCTYPE html>") && apEmptyHtml.TrimEnd().EndsWith("</html>") &&
    apEmptyHtml.Contains("Leeres Projekt"));
Check("Protokoll: leere Abschnitte benannt",
    apEmptyHtml.Contains("Keine Geräte im Projekt") && apEmptyHtml.Contains("Keine Gruppenadressen im Projekt") &&
    apEmptyHtml.Contains("Keine Kommunikationsobjekte im Projekt") &&
    apEmptyHtml.Contains("Keine offenen Punkte aus dem Projektstand"));
var apEmptyEnglish = AcceptanceProtocol.Render(apEmpty, new AcceptanceProtocolOptions { GeneratedAt = apTime, Language = "en" });
Check("Protokoll: englische Leerzustände",
    apEmptyEnglish.Contains("No devices in the project") &&
    apEmptyEnglish.Contains("No group addresses in the project") &&
    apEmptyEnglish.Contains("No communication objects in the project") &&
    apEmptyEnglish.Contains("No open items from the project data"));
Check("Protokoll: leeres Projekt hat trotzdem einen Hash",
    AcceptanceProtocol.ComputeHash(apEmpty, new AcceptanceProtocolOptions { GeneratedAt = apTime }).Length == 64 &&
    AcceptanceProtocol.OpenItems(apEmpty).Count == 0);

// Aufgezeichnete Ladezustände: Demo bleibt Simulation, Drift und Fehler bleiben offen.
var apSignature = WorkbenchProject.ConfigurationSignature(ap, "D-AP-1");
var apLoaded = DeviceProgrammingState.Empty("D-AP-1")
    .ApplySuccess(DeviceLoadFlags.All, apSignature, apTime, "iface-1", ProgrammingOrigin.Demo);
var apLoadedOptions = new AcceptanceProtocolOptions
{
    GeneratedAt = apTime,
    ProgrammingStates = new Dictionary<string, DeviceProgrammingState> { ["D-AP-1"] = apLoaded },
};
var apLoadedHtml = AcceptanceProtocol.Render(ap, apLoadedOptions);
Check("Protokoll: Demo-Ladung ist Simulation, kein echter Download",
    AcceptanceProtocol.CommissioningMode(ap, apLoadedOptions) == AcceptanceCommissioningMode.Simulation &&
    apLoadedHtml.Contains("Simulation (Demo)") && apLoadedHtml.Contains("Simulation – aufgezeichnete Demo-Ladungen") &&
    !apLoadedHtml.Contains("real geladen") && !apLoadedHtml.Contains("Real – aufgezeichnete Ladungen"));
Check("Protokoll: geladenes Gerät ist zu, ungeladenes bleibt offen",
    AcceptanceProtocol.DeviceRows(ap, apLoadedOptions).Single(r => r.Device.Id == "D-AP-1").Status.NeedsProgramming == false &&
    AcceptanceProtocol.OpenItems(ap, apLoadedOptions) is [{ Kind: AcceptanceOpenItemKind.DownloadNotRecorded } apOpen] &&
    apOpen.Device.Id == "D-AP-2");
Check("Protokoll: Zusammenfassung passt zum Dokument",
    AcceptanceProtocol.Summarize(ap, apLoadedOptions) is { Devices: 2, OpenItems: 1 } apSummary &&
    apSummary.DocumentHash == AcceptanceProtocol.ComputeHash(ap, apLoadedOptions) &&
    apSummary.Commissioning == AcceptanceCommissioningMode.Simulation);
var apReal = apLoaded with { Origin = ProgrammingOrigin.Real, LastAttemptOrigin = ProgrammingOrigin.Real };
Check("Protokoll: echte Ladung wird als real ausgewiesen",
    AcceptanceProtocol.CommissioningMode(ap, new AcceptanceProtocolOptions
    {
        ProgrammingStates = new Dictionary<string, DeviceProgrammingState> { ["D-AP-1"] = apReal },
    }) == AcceptanceCommissioningMode.Real);

var apPartialOptions = new AcceptanceProtocolOptions
{
    GeneratedAt = apTime,
    ProgrammingStates = new Dictionary<string, DeviceProgrammingState>
    {
        ["D-AP-1"] = DeviceProgrammingState.Empty("D-AP-1")
            .ApplySuccess(DeviceLoadFlags.Parameters, apSignature, apTime, "iface-1", ProgrammingOrigin.Demo),
    },
};
Check("Protokoll: unvollständige Ladung nennt die fehlenden Flags",
    AcceptanceProtocol.OpenItems(ap, apPartialOptions)[0].Kind == AcceptanceOpenItemKind.ProgrammingOutstanding &&
    AcceptanceProtocol.Render(ap, apPartialOptions).Contains("Fehlende Ladeflags: Adr, Prg, Grp, Cfg") &&
    AcceptanceProtocol.Render(ap, apPartialOptions).Contains("Simulation (Demo), teilgeladen"));
var apDriftOptions = new AcceptanceProtocolOptions
{
    GeneratedAt = apTime,
    ProgrammingStates = new Dictionary<string, DeviceProgrammingState>
    {
        ["D-AP-1"] = apLoaded with { ImageHash = "anderer-stand" },
    },
};
Check("Protokoll: Signaturdrift ist ein offener Punkt",
    AcceptanceProtocol.OpenItems(ap, apDriftOptions)[0].Kind == AcceptanceOpenItemKind.SignatureDrift &&
    AcceptanceProtocol.Render(ap, apDriftOptions).Contains("Konfiguration seit der Ladung geändert"));
var apFailureOptions = new AcceptanceProtocolOptions
{
    GeneratedAt = apTime,
    ProgrammingStates = new Dictionary<string, DeviceProgrammingState>
    {
        ["D-AP-1"] = DeviceProgrammingState.Empty("D-AP-1")
            .ApplyFailure("Timeout", apTime, "iface-1", ProgrammingOrigin.Demo),
    },
};
var apFailureHtml = AcceptanceProtocol.Render(ap, apFailureOptions);
Check("Protokoll: Fehlversuch ist ein offener Punkt",
    AcceptanceProtocol.OpenItems(ap, apFailureOptions)[0].Kind == AcceptanceOpenItemKind.LastError &&
    apFailureHtml.Contains("Download fehlgeschlagen") && apFailureHtml.Contains("Letzter Fehler: Timeout"));
Check("Protokoll: Fehlversuch belegt keinen Download",
    AcceptanceProtocol.CommissioningMode(ap, apFailureOptions) == AcceptanceCommissioningMode.NotRecorded &&
    apFailureHtml.Contains("Kein Download aufgezeichnet") &&
    !apFailureHtml.Contains("Real – aufgezeichnete Ladungen") &&
    !apFailureHtml.Contains("Simulation – aufgezeichnete Demo-Ladungen"));

// CSV-Variante.
var apCsv = AcceptanceProtocol.RenderCsv(ap, apLoadedOptions);
Check("Protokoll-CSV: Kopf und Gerätezeile",
    apCsv.StartsWith("Nr.;Adresse;Gerät;Produkt-Ref;Programm-Ref;") &&
    apCsv.Contains("1.1.1;\"Aktor <1> & \"\"x\"\" äöü\";M-00FA_H-1_P-1") &&
    apCsv.Contains("Simulation (Demo)") && apCsv.Contains("nicht geprüft"));
CheckNeutralized("Protokoll-CSV: keine Formelzelle", apCsv);
Check("Protokoll-CSV: englischer Kopf",
    AcceptanceProtocol.RenderCsv(ap, new AcceptanceProtocolOptions { Language = "en" })
        .StartsWith("No.;Address;Device;Product reference;"));

var apHtmlPath = Path.Combine(Path.GetTempPath(), "knx_acceptance_" + Guid.NewGuid().ToString("N") + ".html");
var apWritten = AcceptanceProtocol.Write(ap, apHtmlPath, apLoadedOptions);
var apBytes = File.ReadAllBytes(apHtmlPath);
Check("Protokoll: HTML-Datei UTF-8 ohne BOM",
    File.Exists(apHtmlPath) && apWritten.Bytes == apBytes.Length && apBytes[0] == (byte)'<' &&
    System.Text.Encoding.UTF8.GetString(apBytes) == apLoadedHtml);
var apCsvPath = Path.Combine(Path.GetTempPath(), "knx_acceptance_" + Guid.NewGuid().ToString("N") + ".csv");
var apCsvWritten = AcceptanceProtocol.WriteCsv(ap, apCsvPath, apLoadedOptions);
var apCsvBytes = File.ReadAllBytes(apCsvPath);
Check("Protokoll: CSV-Datei UTF-8 mit BOM",
    apCsvWritten.Bytes == apCsvBytes.Length && apCsvBytes[0] == 0xEF && apCsvBytes[1] == 0xBB && apCsvBytes[2] == 0xBF);
File.Delete(apHtmlPath);
File.Delete(apCsvPath);

// ---------------------------------------------------------------- Übergabepaket (H11.7)

static string HpText(HandoverArtifact artifact) => Encoding.UTF8.GetString(artifact.Content).TrimStart('\uFEFF');
static string HpArtifact(IReadOnlyList<HandoverArtifact> artifacts, string name)
    => HpText(artifacts.Single(a => a.Name == name));
static string ZipEntryText(ZipArchive zip, string name)
{
    var entry = zip.Entries.Single(e => e.FullName.EndsWith("/" + name, StringComparison.Ordinal));
    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
    return reader.ReadToEnd();
}

var hpTime = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(2));
var hpProject = new Project
{
    Id = "P-HP",
    Name = "Haus & <Objekt>",
    Comment = "<script>alert('x')</script>",
    Guid = "6f1e8c2a-0000-0000-0000-000000000001",
};
hpProject.MasterData.Signature = "SIG-HP";
var hpInst = new Installation { Id = "I-HP", Name = "Haus" };
var hpArea = new Area { Id = "A-HP", Address = 1, Name = "Erdgeschoss" };
var hpLine = new Line { Id = "L-HP", Address = 1, Name = "Licht" };
var hpDevice = new DeviceInstance
{
    Id = "D-HP-1",
    Address = new IndividualAddress(1, 1, 1),
    Name = "Aktor <1> & \"x\" äöü",
    ProductRefId = "M-00FA_H-1_P-1",
    Hardware2ProgramRefId = "M-00FA_H-1_HP-1",
    Description = "äöü",
    Comment = "=HYPERLINK(\"x\")",
};
var hpObject = new ComObjectInstanceRef
{
    Id = "CO-HP-1", ComObjectId = "O-1", Number = 1, Text = "Schalten <aus>", FunctionText = "Schalten",
};
hpObject.Links.Add("GA-HP-1");
hpDevice.ComObjectRefs.Add(hpObject);
hpLine.Devices.Add(hpDevice);
hpLine.Devices.Add(new DeviceInstance
{
    Id = "D-HP-2", Address = new IndividualAddress(1, 1, 2), Name = "Dimmer ohne Raum",
    ProductRefId = "M-00FA_H-1_P-2",
});
hpArea.Lines.Add(hpLine);
hpInst.Areas.Add(hpArea);
var hpRange = new GroupRange { Id = "GR-HP", Name = "Licht", RangeStart = 1, RangeEnd = 15 };
hpRange.Addresses.Add(new GroupAddressEntry
{
    Id = "GA-HP-1", Name = "Decke <Licht>", Address = GroupAddress.ThreeLevel(1, 1, 1),
    DatapointType = "DPST-1-1", Description = "=1+1", Comment = "@cmd",
});
hpInst.MainRanges.Add(hpRange);
var hpBuilding = hpInst.Locations.Add(null, "Gebäude <Süd>", SpaceKind.Building);
var hpFloor = hpInst.Locations.Add(hpBuilding, "Erdgeschoss", SpaceKind.Floor);
var hpRoom = hpInst.Locations.Add(hpFloor, "Wohnen & <Küche>", SpaceKind.Room);
hpRoom.AssignDevice(hpDevice);
hpProject.Installations.Add(hpInst);
var hpRevision = ProjectRevisions.Capture(hpProject, @"C:\Kunde Geheim\haus.knxproj", "Abnahme <1>", hpTime);

var hpExpectedNames = new[]
{
    HandoverPackage.ManifestName, HandoverPackage.ReadmeName,
    "01_projektbericht.html", "02_gruppenadressen.csv", "03_geraete.csv", "04_topologie.csv",
    "05_zuordnungen.csv", "06_raum_buch.csv", "07_verknuepfungen.csv", "08_etiketten.html",
    "09_semantik.ttl", "10_pruefprotokoll.html", "11_parameternachweis.csv",
    "12_betriebsanleitung.html", "13_wartung_kontakte.html", "14_datenschutz_hinweise.html",
    "15_projektverlauf.csv",
};
var hpOptions = new HandoverPackageOptions
{
    GeneratedAt = hpTime, Language = "de", Technician = "T. <Test>", BuildingOwner = "B. & Co",
};
var hpArtifacts = HandoverPackage.Build(hpProject, hpOptions);

Check("Paket: fester Dateisatz in fester Reihenfolge",
    hpArtifacts.Select(a => a.Name).SequenceEqual(hpExpectedNames),
    string.Join(",", hpArtifacts.Select(a => a.Name)));

var hpManifest = HpArtifact(hpArtifacts, HandoverPackage.ManifestName);
var hpManifestRows = hpManifest.Split('\n')
    .Where(line => line.Length > 2 && !line.StartsWith('#'))
    .Select(line => line.Split("  ", 2))
    .ToList();
Check("Paket: Manifest listet jede Datei außer sich selbst",
    hpManifestRows.Count == hpArtifacts.Count - 1 &&
    hpManifestRows.Select(row => row[1]).SequenceEqual(hpExpectedNames.Skip(1)));
Check("Paket: Manifest-Hashes stimmen mit den Dateien",
    hpManifestRows.All(row => row[0] == Convert.ToHexString(
        SHA256.HashData(hpArtifacts.Single(a => a.Name == row[1]).Content)).ToLowerInvariant()));
Check("Paket: Manifest nennt Zeitpunkt, Revision, Signatur und Protokoll",
    hpManifest.Contains("# generated: 2026-10-22T12:00:00") &&
    hpManifest.Contains("# revision: 1 (REV-0001)") &&
    hpManifest.Contains("# signature: unverified") &&
    hpManifest.Contains("# signature-sha256:") &&
    hpManifest.Contains("# content-sha256: " + ProjectRevisions.ContentHash(hpProject)) &&
    hpManifest.Contains("# acceptance-protocol: 10_pruefprotokoll.html") &&
    hpManifest.Contains("# locations: present (rooms=1 assigned=1 unassigned=1)"));
Check("Paket: Manifest zählt die Dateien", hpManifest.Contains("# files: " + (hpExpectedNames.Length - 1)));

var hpReadme = HpArtifact(hpArtifacts, HandoverPackage.ReadmeName);
Check("Paket: README nennt Revision und Signaturstatus",
    hpReadme.Contains("Revision: 1 (REV-0001)") &&
    hpReadme.Contains("Signatur: vorhanden, unverändert übernommen, nicht neu berechnet"));
Check("Paket: README nennt Grenzen, Ausschlüsse und Datenschutz",
    hpReadme.Contains("Bekannte Grenzen") && hpReadme.Contains(".knxkeys") &&
    hpReadme.Contains(".knxprod") && hpReadme.Contains("Demo-JSON") &&
    hpReadme.Contains("12_betriebsanleitung.html") && hpReadme.Contains("14_datenschutz_hinweise.html"));

var hpRoomBook = HpArtifact(hpArtifacts, "06_raum_buch.csv");
var hpRoomRows = hpRoomBook.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
Check("Raumbuch: Raum, Geschoss und Gerät aus der Locations-Struktur",
    hpRoomRows.Any(row => row.Contains("Haus / Gebäude <Süd> / Erdgeschoss / Wohnen & <Küche>") &&
                          row.Contains("Erdgeschoss") && row.Contains("Aktor <1>")));
Check("Raumbuch: Gerät ohne Raum steht am Ende", hpRoomRows[^1].Contains("Dimmer ohne Raum") &&
    hpRoomRows[^1].Contains("ohne Raum"));
Check("Raumbuch: Kopfzeile in Deutsch", hpRoomBook.StartsWith("Raum / Pfad;Art;Geschoss;Gerät;Adresse;Produkt-Ref;"));

Check("Funktionsmatrix: Objekt und Gruppenadresse",
    HpArtifact(hpArtifacts, "05_zuordnungen.csv").Contains("Schalten <aus>") &&
    HpArtifact(hpArtifacts, "05_zuordnungen.csv").Contains("1/1/1"));
Check("Verknüpfungsübersicht: Bereich und Summe",
    HpArtifact(hpArtifacts, "07_verknuepfungen.csv").Contains("Licht") &&
    HpArtifact(hpArtifacts, "07_verknuepfungen.csv").Contains("Gesamt"));
Check("Parameternachweis: Signatur ohne Zustand ist offen",
    HpArtifact(hpArtifacts, "11_parameternachweis.csv").Contains("nicht aufgezeichnet") &&
    HpArtifact(hpArtifacts, "11_parameternachweis.csv").Contains("ja"));
Check("Projektverlauf: Revision ohne Quellpfad",
    HpArtifact(hpArtifacts, "15_projektverlauf.csv").Contains("Abnahme <1>") &&
    HpArtifact(hpArtifacts, "15_projektverlauf.csv").Contains(hpRevision.ContentHash) &&
    !HpArtifact(hpArtifacts, "15_projektverlauf.csv").Contains("Kunde Geheim"));

var hpHtml = hpArtifacts.Where(a => a.Name.EndsWith(".html", StringComparison.Ordinal)).ToList();
Check("Paket: HTML maskiert Projekt-, Raum-, Geräte- und Kontaktnamen",
    HpArtifact(hpArtifacts, "01_projektbericht.html").Contains("Haus &amp; &lt;Objekt&gt;") &&
    HpArtifact(hpArtifacts, "12_betriebsanleitung.html").Contains("Wohnen &amp; &lt;Küche&gt;") &&
    HpArtifact(hpArtifacts, "13_wartung_kontakte.html").Contains("T. &lt;Test&gt;") &&
    HpArtifact(hpArtifacts, "13_wartung_kontakte.html").Contains("B. &amp; Co"));
Check("Paket: kein Skript und keine externen URLs in HTML",
    hpHtml.All(a =>
    {
        var text = HpText(a);
        return !text.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }));
Check("Paket: keine absoluten Quellpfade",
    hpArtifacts.All(a =>
    {
        var text = HpText(a);
        return !text.Contains("Kunde Geheim") && !text.Contains(@"C:\");
    }));
foreach (var name in new[]
         {
             "02_gruppenadressen.csv", "03_geraete.csv", "04_topologie.csv", "05_zuordnungen.csv",
             "06_raum_buch.csv", "07_verknuepfungen.csv", "11_parameternachweis.csv", "15_projektverlauf.csv",
         })
    CheckNeutralized("Paket-CSV ohne Formelzelle: " + name, HpArtifact(hpArtifacts, name));

Check("Paket: CSV mit BOM, HTML ohne BOM",
    hpArtifacts.Single(a => a.Name == "02_gruppenadressen.csv").Content[0] == 0xEF &&
    hpArtifacts.Single(a => a.Name == "01_projektbericht.html").Content[0] == (byte)'<');

// Deterministische ZIP-Ausgabe.
var hpZipA = Path.Combine(Path.GetTempPath(), "knx_handover_" + Guid.NewGuid().ToString("N") + ".zip");
var hpZipB = Path.Combine(Path.GetTempPath(), "knx_handover_" + Guid.NewGuid().ToString("N") + ".zip");
var hpZipC = Path.Combine(Path.GetTempPath(), "knx_handover_" + Guid.NewGuid().ToString("N") + ".zip");
var hpWrittenA = HandoverPackage.Write(hpProject, hpZipA, hpOptions);
var hpWrittenB = HandoverPackage.Write(hpProject, hpZipB, hpOptions);
Check("Paket: ZIP byte-identisch bei gleichem Zeitpunkt",
    File.ReadAllBytes(hpZipA).SequenceEqual(File.ReadAllBytes(hpZipB)));
var hpWrittenC = HandoverPackage.Write(hpProject, hpZipC,
    new HandoverPackageOptions { GeneratedAt = hpTime.AddMinutes(1), Language = "de" });
Check("Paket: anderer Zeitpunkt ergibt ein anderes Paket",
    !File.ReadAllBytes(hpZipA).SequenceEqual(File.ReadAllBytes(hpZipC)) && hpWrittenC.Bytes > 0);
Check("Paket: Ergebnis nennt Manifest-Hash, Revision und Signatur",
    hpWrittenA.ManifestSha256.Length == 64 && hpWrittenA.Revision == 1 &&
    hpWrittenA.Signature == "vorhanden, unverändert übernommen, nicht neu berechnet" &&
    hpWrittenA.Commissioning == "not_recorded" &&
    hpWrittenA.Files.Select(f => f.Name).SequenceEqual(hpExpectedNames));
using (var hpZip = ZipFile.OpenRead(hpZipA))
{
    Check("Paket: ZIP-Wurzel und feste Eintragsreihenfolge",
        hpZip.Entries.Select(e => e.FullName)
            .SequenceEqual(hpExpectedNames.Select(name => hpWrittenA.RootFolder + "/" + name)),
        string.Join(",", hpZip.Entries.Select(e => e.FullName)));
    Check("Paket: ZIP-Zeitstempel stabil", hpZip.Entries.All(e => e.LastWriteTime.Year == 2026));
    Check("Paket: README im ZIP nennt die Grenzen",
        ZipEntryText(hpZip, HandoverPackage.ReadmeName).Contains("Bekannte Grenzen"));
}
foreach (var path in new[] { hpZipA, hpZipB, hpZipC })
    if (File.Exists(path)) File.Delete(path);

// Sprache folgt der Option, nicht der UI-Sprache; die UI-Sprache wird wiederhergestellt.
Strings.Language = "en";
var hpGerman = HandoverPackage.Build(hpProject, new HandoverPackageOptions { GeneratedAt = hpTime, Language = "de" });
Check("Paket: deutsche Sprache bei englischer UI",
    HpArtifact(hpGerman, "01_projektbericht.html").Contains("<html lang=\"de\">") &&
    HpArtifact(hpGerman, "02_gruppenadressen.csv").StartsWith("Adresse;Name;"));
Check("Paket: UI-Sprache nach dem Export wiederhergestellt", Strings.Language == "en");
Strings.Language = "de";
var hpEnglish = HandoverPackage.Build(hpProject, new HandoverPackageOptions { GeneratedAt = hpTime, Language = "en" });
Check("Paket: englische Sprache in Bericht, CSV, Manifest und README",
    HpArtifact(hpEnglish, "01_projektbericht.html").Contains("<html lang=\"en\">") &&
    HpArtifact(hpEnglish, "01_projektbericht.html").Contains("Project documentation") &&
    HpArtifact(hpEnglish, "02_gruppenadressen.csv").StartsWith("Address;Name;") &&
    HpArtifact(hpEnglish, HandoverPackage.ManifestName).Contains("# language: en") &&
    HpArtifact(hpEnglish, HandoverPackage.ReadmeName).Contains("Handover package"));

// Leeres Projekt und fehlende Gebäudestruktur.
var hpEmpty = new Project { Id = "P-LEER", Name = "Leeres Projekt" };
var hpEmptyArtifacts = HandoverPackage.Build(hpEmpty, new HandoverPackageOptions { GeneratedAt = hpTime, Language = "de" });
var hpEmptyManifest = HpArtifact(hpEmptyArtifacts, HandoverPackage.ManifestName);
Check("Paket: leeres Projekt enthält alle Dateien",
    hpEmptyArtifacts.Select(a => a.Name).SequenceEqual(hpExpectedNames));
Check("Paket: leeres Projekt benennt fehlende Daten",
    hpEmptyManifest.Contains("# project: Leeres Projekt") &&
    hpEmptyManifest.Contains("# revision: none") &&
    hpEmptyManifest.Contains("# signature: absent") &&
    hpEmptyManifest.Contains("# locations: absent") &&
    hpEmptyManifest.Contains("# commissioning: not_recorded"));
Check("Paket: leeres Raumbuch, Parameternachweis und Verlauf haben nur den Kopf",
    HpArtifact(hpEmptyArtifacts, "06_raum_buch.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 1 &&
    HpArtifact(hpEmptyArtifacts, "11_parameternachweis.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 1 &&
    HpArtifact(hpEmptyArtifacts, "15_projektverlauf.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 1);
Check("Paket: leeres Projekt rendert Report und Protokoll",
    HpArtifact(hpEmptyArtifacts, "01_projektbericht.html").StartsWith("<!DOCTYPE html>") &&
    HpArtifact(hpEmptyArtifacts, "10_pruefprotokoll.html").Contains("Keine Geräte im Projekt"));

var hpNoLocations = new Project { Id = "P-NOLOC", Name = "Ohne Räume" };
var hpNoLocationsInst = new Installation { Id = "I-NOLOC", Name = "Haus" };
var hpNoLocationsArea = new Area { Id = "A-NOLOC", Address = 1, Name = "Erdgeschoss" };
var hpNoLocationsLine = new Line { Id = "L-NOLOC", Address = 1, Name = "Licht" };
hpNoLocationsLine.Devices.Add(new DeviceInstance
{
    Id = "D-NOLOC", Address = new IndividualAddress(1, 1, 1), Name = "Aktor ohne Raum", ProductRefId = "M-1",
});
hpNoLocationsArea.Lines.Add(hpNoLocationsLine);
hpNoLocationsInst.Areas.Add(hpNoLocationsArea);
hpNoLocations.Installations.Add(hpNoLocationsInst);
var hpNoLocationsArtifacts = HandoverPackage.Build(hpNoLocations,
    new HandoverPackageOptions { GeneratedAt = hpTime, Language = "de" });
Check("Paket: fehlende Locations listen Geräte als ohne Raum",
    HpArtifact(hpNoLocationsArtifacts, HandoverPackage.ManifestName).Contains("# locations: absent") &&
    HpArtifact(hpNoLocationsArtifacts, "06_raum_buch.csv").Contains("Aktor ohne Raum") &&
    HpArtifact(hpNoLocationsArtifacts, "06_raum_buch.csv").Contains("ohne Raum"));

// Demo-Ladung bleibt Simulation, nicht echter Download.
var hpSignature = WorkbenchProject.ConfigurationSignature(hpProject, "D-HP-1");
var hpState = DeviceProgrammingState.Empty("D-HP-1")
    .ApplySuccess(DeviceLoadFlags.All, hpSignature, hpTime, "iface-1", ProgrammingOrigin.Demo);
var hpStateOptions = new HandoverPackageOptions
{
    GeneratedAt = hpTime,
    Language = "de",
    ProgrammingStates = new Dictionary<string, DeviceProgrammingState> { ["D-HP-1"] = hpState },
};
var hpStateArtifacts = HandoverPackage.Build(hpProject, hpStateOptions);
Check("Paket: Demo-Ladung ist Simulation",
    HpArtifact(hpStateArtifacts, HandoverPackage.ManifestName).Contains("# commissioning: simulation") &&
    HpArtifact(hpStateArtifacts, "11_parameternachweis.csv").Contains("Demo (Simulation)") &&
    HpArtifact(hpStateArtifacts, "10_pruefprotokoll.html").Contains("Simulation (Demo)") &&
    HpArtifact(hpStateArtifacts, HandoverPackage.ReadmeName).Contains("Simulation"));
var hpStateZip = Path.Combine(Path.GetTempPath(), "knx_handover_" + Guid.NewGuid().ToString("N") + ".zip");
var hpStateResult = HandoverPackage.Write(hpProject, hpStateZip, hpStateOptions);
Check("Paket: Ergebnis meldet Simulation", hpStateResult.Commissioning == "simulation");
if (File.Exists(hpStateZip)) File.Delete(hpStateZip);

Console.WriteLine(failures.Count == 0 ? "REPORTS OK" : $"{failures.Count} FEHLER");
Environment.Exit(failures.Count == 0 ? 0 : 1);
