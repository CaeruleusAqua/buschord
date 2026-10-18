using Knx;
using Knx.App.Services;
using Knx.Reports;

// Self-Check für die Reports-Export-Pipeline
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name);
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

var reportPath = Path.Combine(Path.GetTempPath(), "knx_project_report_" + Guid.NewGuid().ToString("N") + ".html");
var written = ProjectReport.Write(tricky, reportPath, new ProjectReportOptions { GeneratedAt = fixedTime });
var reportBytes = File.ReadAllBytes(reportPath);
Check("Bericht: Datei geschrieben", File.Exists(reportPath) && written.Bytes == reportBytes.Length && written.Bytes > 1000);
Check("Bericht: Datei ist UTF-8 ohne BOM", reportBytes[0] == (byte)'<');
Check("Bericht: Datei entspricht Render", System.Text.Encoding.UTF8.GetString(reportBytes) == report);
File.Delete(reportPath);

Console.WriteLine(failures.Count == 0 ? "REPORTS OK" : $"{failures.Count} FEHLER");
Environment.Exit(failures.Count == 0 ? 0 : 1);
