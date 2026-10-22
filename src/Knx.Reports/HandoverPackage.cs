using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Knx;
using Knx.Workbench;

namespace Knx.Reports;

/// <summary>
/// Inputs of one handover package. Everything is optional except the project; a caller that
/// needs a reproducible package passes a fixed <see cref="GeneratedAt"/> and the same language.
/// </summary>
public sealed class HandoverPackageOptions
{
    /// <summary>Package/document title; defaults to the project name.</summary>
    public string? Title { get; set; }
    /// <summary>Package language ("de" or "en"); defaults to the active UI language.</summary>
    public string Language { get; set; } = Strings.Language;
    /// <summary>Generation timestamp used by every document, the manifest and the ZIP entries.</summary>
    public DateTimeOffset? GeneratedAt { get; set; }
    /// <summary>Technician printed in the acceptance protocol and the contact page.</summary>
    public string? Technician { get; set; }
    /// <summary>Building owner printed in the acceptance protocol and the contact page.</summary>
    public string? BuildingOwner { get; set; }
    /// <summary>Recorded device programming states; without them every device reports "not recorded".</summary>
    public IReadOnlyDictionary<string, DeviceProgrammingState>? ProgrammingStates { get; set; }
    /// <summary>Paper size of the printable documents: A4, A3 or Letter.</summary>
    public string PaperSize { get; set; } = "A4";
}

/// <summary>One file of the package, in package order, with its exact bytes.</summary>
public sealed record HandoverArtifact(string Name, byte[] Content);

/// <summary>Manifest entry of one written file.</summary>
public sealed record HandoverPackageFile(string Name, long Bytes, string Sha256);

/// <summary>Outcome of <see cref="HandoverPackage.Write"/>.</summary>
public sealed record HandoverPackageResult(
    string Path,
    long Bytes,
    string RootFolder,
    IReadOnlyList<HandoverPackageFile> Files,
    string ManifestSha256,
    long? Revision,
    string Signature,
    string Commissioning);

/// <summary>
/// One-command handover package (H11.7). Bundles the printable report, CSV exports, room book,
/// function matrix, link summary, labels, semantic export, acceptance protocol, parameter
/// evidence, owner template pages, revision list, README and a SHA-256 manifest into one ZIP.
///
/// Honesty contract: nothing is invented. Missing revisions, locations, programming states or
/// backups are named as missing; the ETS signature is reported as preserved, never as verified;
/// demo loads are reported as simulation. The package never writes to the project and carries no
/// keyring, catalogue files or manufacturer XML.
/// </summary>
public static partial class HandoverPackage
{
    /// <summary>Package format version; changes when the file set or manifest layout changes.</summary>
    public const int FormatVersion = 1;

    public const string ManifestName = "00_MANIFEST.sha256";
    public const string ReadmeName = "00_README.txt";

    /// <summary>
    /// Builds every package file in the fixed manifest order (manifest first, README second).
    /// The project is only read; the active UI language is restored before returning.
    /// </summary>
    public static IReadOnlyList<HandoverArtifact> Build(Project project, HandoverPackageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new HandoverPackageOptions();
        var generated = options.GeneratedAt ?? DateTimeOffset.Now;
        return Build(project, options, generated);
    }

    /// <summary>Writes the package as a deterministic ZIP and returns its manifest data.</summary>
    public static HandoverPackageResult Write(Project project, string path, HandoverPackageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new HandoverPackageOptions();
        var generated = options.GeneratedAt ?? DateTimeOffset.Now;
        var artifacts = Build(project, options, generated);
        var root = RootFolder(project, generated);

        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var stamp = ZipStamp(generated);
            foreach (var artifact in artifacts)
            {
                var entry = zip.CreateEntry(root + "/" + artifact.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = stamp;
                using var stream = entry.Open();
                stream.Write(artifact.Content);
            }
        }

        var bytes = buffer.ToArray();
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = full + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, full, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var manifest = artifacts.First(a => a.Name == ManifestName).Content;
        var latest = ProjectRevisions.Latest(project);
        return new HandoverPackageResult(
            full,
            bytes.LongLength,
            root,
            artifacts.Select(a => new HandoverPackageFile(a.Name, a.Content.LongLength, Sha256Hex(a.Content))).ToList(),
            Sha256Hex(manifest),
            latest?.Number,
            SignatureStatusText(ProjectRevisions.SignatureStatus(project), Language(options)),
            CommissioningKey(project, options));
    }

    private static IReadOnlyList<HandoverArtifact> Build(
        Project project, HandoverPackageOptions options, DateTimeOffset generated)
    {
        var lang = Language(options);
        var title = string.IsNullOrWhiteSpace(options.Title) ? project.Name : options.Title!;
        // ponytail: the language is process-global (Strings.Language); exports are serialized,
        // so a scoped switch is enough to keep report and CSV headers in the package language.
        var previousLanguage = Strings.Language;
        try
        {
            Strings.Language = lang;

            var artifacts = new List<HandoverArtifact>
            {
                new("01_projektbericht.html", Utf8(ProjectReport.Render(project, new ProjectReportOptions
                {
                    Title = options.Title,
                    GeneratedAt = generated,
                    PaperSize = options.PaperSize,
                }))),
                new("02_gruppenadressen.csv", Utf8Bom(ProjectCsvExporter.GroupAddresses(project))),
                new("03_geraete.csv", Utf8Bom(ProjectCsvExporter.Devices(project))),
                new("04_topologie.csv", Utf8Bom(ProjectCsvExporter.Topology(project))),
                new("05_zuordnungen.csv", Utf8Bom(AssociationsCsv(project, lang))),
                new("06_raum_buch.csv", Utf8Bom(RoomBookCsv(project, lang))),
                new("07_verknuepfungen.csv", Utf8Bom(LinksSummaryCsv(project, lang))),
                new("08_etiketten.html", Utf8(LabelsHtml(project, title))),
                new("09_semantik.ttl", Utf8(SemanticExport.ToTurtle(project))),
                new("10_pruefprotokoll.html", Utf8(AcceptanceProtocol.Render(project, ProtocolOptions(options, generated)))),
                new("11_parameternachweis.csv", Utf8Bom(ParameterEvidenceCsv(project, options, lang))),
                new("12_betriebsanleitung.html", Utf8(OperatingPage(project, lang))),
                new("13_wartung_kontakte.html", Utf8(MaintenancePage(project, options, lang))),
                new("14_datenschutz_hinweise.html", Utf8(PrivacyPage(project, lang))),
                new("15_projektverlauf.csv", Utf8Bom(RevisionLogCsv(project, lang))),
            };

            var readme = Readme(project, options, generated, artifacts, lang, title);
            artifacts.Insert(0, new(ReadmeName, Utf8(readme)));
            var manifest = Manifest(project, options, generated, artifacts, lang);
            artifacts.Insert(0, new(ManifestName, Utf8(manifest)));
            return artifacts;
        }
        finally
        {
            Strings.Language = previousLanguage;
        }
    }

    private static AcceptanceProtocolOptions ProtocolOptions(HandoverPackageOptions options, DateTimeOffset generated)
        => new()
        {
            Title = options.Title,
            GeneratedAt = generated,
            Language = Language(options),
            Technician = options.Technician,
            BuildingOwner = options.BuildingOwner,
            ProgrammingStates = options.ProgrammingStates,
            PaperSize = options.PaperSize,
        };

    private static string LabelsHtml(Project project, string title)
    {
        var labelOptions = new LabelOptions { SheetTitle = title };
        var labels = LabelCreator.GroupAddressLabels(project, labelOptions)
            .Concat(LabelCreator.DeviceLabels(project, labelOptions))
            .ToList();
        return LabelCreator.ToHtml(labels, labelOptions);
    }

    /// <summary>Function matrix CSV; a dangling group-address link fails with a readable message.</summary>
    private static string AssociationsCsv(Project project, string lang)
    {
        try
        {
            return ProjectCsvExporter.Associations(project);
        }
        catch (KeyNotFoundException)
        {
            throw new InvalidOperationException(lang == "en"
                ? "The function matrix cannot be built: a communication object links to a group address that does not exist. Run the project check and repair the link."
                : "Die Funktionsmatrix lässt sich nicht erzeugen: Ein Kommunikationsobjekt verweist auf eine nicht vorhandene Gruppenadresse. Projektprüfung ausführen und die Verknüpfung reparieren.");
        }
    }

    // ---------------------------------------------------------------- manifest and README

    private static string Manifest(Project project, HandoverPackageOptions options, DateTimeOffset generated,
        IReadOnlyList<HandoverArtifact> artifacts, string lang)
    {
        var latest = ProjectRevisions.Latest(project);
        var signature = ProjectRevisions.SignatureStatus(project);
        var signatureHash = ProjectRevisions.SignatureHash(project);
        var locations = LocationCounts(project);
        var sb = new StringBuilder();
        sb.Append("# BusChord handover package\n");
        sb.Append("# format: ").Append(FormatVersion).Append('\n');
        sb.Append("# generated: ").Append(Stamp(generated, "O")).Append('\n');
        sb.Append("# generator: BusChord ").Append(ToolVersion).Append('\n');
        sb.Append("# project: ").Append(Flat(project.Name)).Append('\n');
        sb.Append("# project-id: ").Append(Flat(project.Id)).Append('\n');
        sb.Append("# project-guid: ").Append(Flat(project.Guid) is { Length: > 0 } guid ? guid : "-").Append('\n');
        sb.Append("# revision: ").Append(latest is null
            ? "none"
            : latest.Number.ToString(CultureInfo.InvariantCulture) + " (" + latest.Id + ") " + Stamp(latest.Timestamp, "O"))
            .Append('\n');
        sb.Append("# content-sha256: ").Append(ProjectRevisions.ContentHash(project)).Append('\n');
        sb.Append("# signature: ").Append(signature == RevisionSignatureStatus.Absent ? "absent" : "unverified").Append('\n');
        if (signatureHash is not null) sb.Append("# signature-sha256: ").Append(signatureHash).Append('\n');
        sb.Append("# language: ").Append(lang).Append('\n');
        sb.Append("# commissioning: ").Append(CommissioningKey(project, options)).Append('\n');
        sb.Append("# locations: ").Append(locations.Nodes == 0
            ? "absent"
            : "present (rooms=" + locations.Rooms.ToString(CultureInfo.InvariantCulture) +
              " assigned=" + locations.Assigned.ToString(CultureInfo.InvariantCulture) +
              " unassigned=" + locations.Unassigned.ToString(CultureInfo.InvariantCulture) + ")")
            .Append('\n');
        sb.Append("# acceptance-protocol: 10_pruefprotokoll.html\n");
        sb.Append("# files: ").Append(artifacts.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var artifact in artifacts)
            sb.Append(Sha256Hex(artifact.Content)).Append("  ").Append(artifact.Name).Append('\n');
        return sb.ToString();
    }

    private static string Readme(Project project, HandoverPackageOptions options, DateTimeOffset generated,
        IReadOnlyList<HandoverArtifact> artifacts, string lang, string title)
    {
        var latest = ProjectRevisions.Latest(project);
        var signature = ProjectRevisions.SignatureStatus(project);
        var locations = LocationCounts(project);
        var sb = new StringBuilder();
        sb.Append(title).Append(" – ").Append(T("HpReadmeTitle", lang)).Append('\n');
        sb.Append(new string('=', 60)).Append('\n').Append('\n');
        sb.Append(T("HpReadmeIntro", lang)).Append('\n').Append('\n');

        sb.Append(T("HpReadmeContents", lang)).Append('\n');
        foreach (var artifact in artifacts)
            sb.Append("  ").Append(artifact.Name.PadRight(28)).Append(' ').Append(FileDescription(artifact.Name, lang)).Append('\n');
        sb.Append('\n');

        sb.Append(T("HpReadmeFacts", lang)).Append('\n');
        AppendFact(sb, T("HpReadmeProject", lang), title);
        AppendFact(sb, T("HpReadmeProjectId", lang), project.Id);
        if (!string.IsNullOrWhiteSpace(project.Guid)) AppendFact(sb, T("HpReadmeGuid", lang), project.Guid!);
        AppendFact(sb, T("HpReadmeGenerated", lang), Stamp(generated, "dd.MM.yyyy HH:mm"));
        AppendFact(sb, T("HpReadmeGenerator", lang), "BusChord " + ToolVersion + ", format " + FormatVersion);
        AppendFact(sb, T("HpReadmeLanguage", lang), lang == "en" ? "English" : "Deutsch");
        AppendFact(sb, T("HpReadmeRevision", lang), latest is null
            ? T("HpRevisionNone", lang)
            : latest.Number.ToString(CultureInfo.InvariantCulture) + " (" + latest.Id + "), " + Stamp(latest.Timestamp, "dd.MM.yyyy HH:mm"));
        AppendFact(sb, T("HpReadmeContentHash", lang), ProjectRevisions.ContentHash(project));
        AppendFact(sb, T("HpReadmeSignature", lang), SignatureStatusText(signature, lang));
        AppendFact(sb, T("HpReadmeCommissioning", lang), CommissioningText(CommissioningKey(project, options), lang));
        AppendFact(sb, T("HpReadmeLocations", lang), locations.Nodes == 0
            ? T("HpLocationsAbsent", lang)
            : string.Format(CultureInfo.InvariantCulture, T("HpLocationsPresent", lang), locations.Rooms,
                locations.Assigned, locations.Unassigned));
        AppendFact(sb, T("HpReadmeFiles", lang), (artifacts.Count).ToString(CultureInfo.InvariantCulture));
        AppendFact(sb, T("HpReadmeManifest", lang), T("HpReadmeManifestText", lang));
        sb.Append('\n');

        sb.Append(T("HpReadmeChecks", lang)).Append('\n');
        foreach (var key in new[]
                 {
                     "HpLimitSignature", "HpLimitDownload", "HpLimitBus", "HpLimitPrint", "HpLimitLabels",
                 })
            sb.Append("  - ").Append(T(key, lang)).Append('\n');
        sb.Append('\n');

        sb.Append(T("HpReadmeExcluded", lang)).Append('\n');
        sb.Append("  - ").Append(T("HpExcludedKeys", lang)).Append('\n');
        sb.Append("  - ").Append(T("HpExcludedBackup", lang)).Append('\n');
        sb.Append('\n');
        sb.Append(T("HpReadmePrivacy", lang)).Append(": ").Append(T("HpPrivacyPointer", lang)).Append('\n');
        return sb.ToString();
    }

    private static void AppendFact(StringBuilder sb, string label, string value)
        => sb.Append("  ").Append(label).Append(": ").Append(Flat(value)).Append('\n');

    private static string FileDescription(string name, string lang)
        => name switch
        {
            ManifestName => T("HpFileManifest", lang),
            ReadmeName => T("HpFileReadme", lang),
            "01_projektbericht.html" => T("HpFileReport", lang),
            "02_gruppenadressen.csv" => T("HpFileGroupAddresses", lang),
            "03_geraete.csv" => T("HpFileDevices", lang),
            "04_topologie.csv" => T("HpFileTopology", lang),
            "05_zuordnungen.csv" => T("HpFileAssociations", lang),
            "06_raum_buch.csv" => T("HpFileRoomBook", lang),
            "07_verknuepfungen.csv" => T("HpFileLinks", lang),
            "08_etiketten.html" => T("HpFileLabels", lang),
            "09_semantik.ttl" => T("HpFileSemantic", lang),
            "10_pruefprotokoll.html" => T("HpFileProtocol", lang),
            "11_parameternachweis.csv" => T("HpFileEvidence", lang),
            "12_betriebsanleitung.html" => T("HpFileOperating", lang),
            "13_wartung_kontakte.html" => T("HpFileMaintenance", lang),
            "14_datenschutz_hinweise.html" => T("HpFilePrivacy", lang),
            "15_projektverlauf.csv" => T("HpFileRevisions", lang),
            _ => "",
        };

    // ---------------------------------------------------------------- helpers

    private readonly record struct LocationStats(int Rooms, int Assigned, int Unassigned, int Nodes);

    private static LocationStats LocationCounts(Project project)
    {
        var nodes = project.Installations.SelectMany(i => i.Locations.AllNodes).ToList();
        var assigned = nodes.SelectMany(n => n.DeviceRefs).Distinct(StringComparer.Ordinal).Count();
        return new LocationStats(
            nodes.Count(n => n.Kind == SpaceKind.Room),
            assigned,
            project.UnassignedDevices().Count(),
            nodes.Count);
    }

    private static string RootFolder(Project project, DateTimeOffset generated)
    {
        var latest = ProjectRevisions.Latest(project);
        var revision = latest is null ? "r0" : "r" + latest.Number.ToString(CultureInfo.InvariantCulture);
        return "handover_" + Slug(project.Name) + "_" + revision + "_" +
               generated.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <summary>ASCII-only folder name from the project name; never contains a path.</summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var text = sb.ToString().Trim('-');
        if (text.Length > 40) text = text[..40].Trim('-');
        return text.Length == 0 ? "project" : text;
    }

    /// <summary>Stable ZIP timestamp: the UTC instant, clamped to the DOS range.</summary>
    private static DateTimeOffset ZipStamp(DateTimeOffset generated)
    {
        var utc = generated.ToUniversalTime();
        if (utc.Year < 1980) return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (utc.Year > 2107) return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return utc;
    }

    private static string Language(HandoverPackageOptions options) => options.Language == "en" ? "en" : "de";

    /// <summary>Reuses the acceptance protocol's classification so package and protocol never disagree.</summary>
    private static string CommissioningKey(Project project, HandoverPackageOptions options)
        => AcceptanceProtocol.CommissioningMode(project, new AcceptanceProtocolOptions
        {
            ProgrammingStates = options.ProgrammingStates,
        }) switch
        {
            AcceptanceCommissioningMode.Real => "real",
            AcceptanceCommissioningMode.Simulation => "simulation",
            _ => "not_recorded",
        };

    private static string CommissioningText(string key, string lang) => key switch
    {
        "real" => T("HpCommissioningReal", lang),
        "simulation" => T("HpCommissioningSimulation", lang),
        _ => T("HpCommissioningNone", lang),
    };

    private static string SignatureStatusText(RevisionSignatureStatus status, string lang)
        => status == RevisionSignatureStatus.Absent ? T("HpSignatureAbsent", lang) : T("HpSignatureUnverified", lang);

    private static string Stamp(DateTimeOffset value, string format)
        => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>One line value: newlines, tabs and control characters collapse to spaces.</summary>
    private static string Flat(string? value)
    {
        if (value is null) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString().Trim();
    }

    private static string Escape(string? value)
        => value is null
            ? ""
            : value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : Escape(value);

    private static string ToolVersion
        => typeof(HandoverPackage).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string Sha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>UTF-8 with a BOM for spreadsheets; GetBytes alone would omit the preamble.</summary>
    private static byte[] Utf8Bom(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[body.Length + 3];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);
        return bytes;
    }

    /// <summary>Document text; de/en pairs live here because the core dictionary is out of scope.</summary>
    private static string T(string key, string lang)
    {
        var entry = Text[key];
        return lang == "en" ? entry.En : entry.De;
    }

    /// <summary>Shared report label from the core dictionary, resolved for the package language.</summary>
    private static string L(string key, string lang)
        => Strings.Catalogue.TryGetValue(key, out var entry)
            ? lang == "en" ? entry.En : entry.De
            : key;

    private static readonly Dictionary<string, (string De, string En)> Text = new(StringComparer.Ordinal)
    {
        ["HpReadmeTitle"] = ("Übergabepaket", "Handover package"),
        ["HpReadmeIntro"] = (
            "Dieses Paket bündelt den dokumentierten Projektstand für die Übergabe an Bauherr, Betreiber oder Wartung. " +
            "Es ist aus den Projektdaten erzeugt; Platzhalterseiten sind als Vorlage gekennzeichnet.",
            "This package bundles the documented project state for handover to the building owner, operator or maintenance. " +
            "It is generated from the project data; placeholder pages are marked as templates."),
        ["HpReadmeContents"] = ("Inhalt", "Contents"),
        ["HpReadmeFacts"] = ("Projektdaten", "Project facts"),
        ["HpReadmeChecks"] = ("Bekannte Grenzen", "Known limits"),
        ["HpReadmeExcluded"] = ("Nicht enthalten", "Not included"),
        ["HpReadmePrivacy"] = ("Datenschutz", "Data protection"),
        ["HpReadmeProject"] = ("Projekt", "Project"),
        ["HpReadmeProjectId"] = ("Projekt-ID", "Project id"),
        ["HpReadmeGuid"] = ("Projekt-GUID", "Project GUID"),
        ["HpReadmeGenerated"] = ("Erstellt", "Generated"),
        ["HpReadmeGenerator"] = ("Erzeuger", "Generator"),
        ["HpReadmeLanguage"] = ("Sprache", "Language"),
        ["HpReadmeRevision"] = ("Revision", "Revision"),
        ["HpReadmeContentHash"] = ("Inhalts-Hash (SHA-256)", "Content hash (SHA-256)"),
        ["HpReadmeSignature"] = ("Signatur", "Signature"),
        ["HpReadmeCommissioning"] = ("Inbetriebnahme", "Commissioning"),
        ["HpReadmeLocations"] = ("Gebäudestruktur", "Building structure"),
        ["HpReadmeFiles"] = ("Dateien im Paket", "Files in the package"),
        ["HpReadmeManifest"] = ("Prüfsummen", "Checksums"),
        ["HpReadmeManifestText"] = (
            ManifestName + " enthält die SHA-256-Prüfsumme jeder anderen Datei in diesem Paket.",
            ManifestName + " contains the SHA-256 checksum of every other file in this package."),
        ["HpSignatureAbsent"] = ("fehlt", "absent"),
        ["HpSignatureUnverified"] = (
            "vorhanden, unverändert übernommen, nicht neu berechnet",
            "present, preserved unchanged, not recomputed"),
        ["HpRevisionNone"] = ("keine Revision aufgezeichnet", "no revision recorded"),
        ["HpCommissioningNone"] = ("kein Download aufgezeichnet", "no download recorded"),
        ["HpCommissioningSimulation"] = (
            "Simulation – nur Demo-Ladungen aufgezeichnet",
            "simulation – demo loads only"),
        ["HpCommissioningReal"] = ("real – aufgezeichnete Ladungen", "real – recorded loads"),
        ["HpLocationsAbsent"] = (
            "keine Gebäudestruktur (Locations) vorhanden",
            "no building structure (Locations) present"),
        ["HpLocationsPresent"] = (
            "{0} Räume, {1} Geräte zugeordnet, {2} ohne Raum",
            "{0} rooms, {1} devices assigned, {2} without a room"),
        ["HpLimitSignature"] = (
            "Die ETS-Signatur wird unverändert übernommen, aber nicht neu berechnet; nach Änderungen kann ETS sie ablehnen.",
            "The ETS signature is preserved but never recomputed; ETS may reject it after edits."),
        ["HpLimitDownload"] = (
            "BusChord führt keine echten Downloads aus. Aufgezeichnete Ladezustände belegen höchstens einen Demo- oder Simulationslauf, sofern die Herkunft „Real“ nicht ausdrücklich vermerkt ist.",
            "BusChord performs no real downloads. Recorded load states prove at most a demo or simulation run unless the origin is explicitly marked \"real\"."),
        ["HpLimitBus"] = (
            "Busdiagnose und Telegrammaufzeichnung sind schreibfrei; das Verhalten der Descriptor- und Scan-Frames ist auf echter Hardware nicht abschließend verifiziert.",
            "Bus diagnostics and telegram recording are write-free; descriptor and scan frame behaviour is not fully verified on real hardware."),
        ["HpLimitPrint"] = (
            "Es gibt kein PDF im Paket; Bericht, Protokoll und Vorlageseiten werden aus dem Browser gedruckt.",
            "The package contains no PDF; report, protocol and template pages are printed from the browser."),
        ["HpLimitLabels"] = (
            "Der Etikettenbogen nutzt die Druckvorlage des Etikettenmoduls; deren Sprachkennzeichnung folgt noch nicht der Paketsprache.",
            "The label sheet uses the label module's print template; its language attribute does not yet follow the package language."),
        ["HpExcludedKeys"] = (
            "Schlüssel- und Sicherheitsdateien (.knxkeys), Katalogdateien (.knxprod), Hersteller-XML und Demo-JSON.",
            "Key and security files (.knxkeys), catalogue files (.knxprod), manufacturer XML and demo JSON."),
        ["HpExcludedBackup"] = (
            "Sicherungskopien des Projekts. Bewahren Sie die separat erstellte Sicherung nach der Backup-Dokumentation auf.",
            "Project backup copies. Keep the separately created backup according to the backup documentation."),
        ["HpPrivacyPointer"] = (
            "Was das Paket an personenbezogenen Daten enthält und wie es gelöscht wird, steht in 14_datenschutz_hinweise.html.",
            "What personal data the package contains and how it is deleted is described in 14_datenschutz_hinweise.html."),
        ["HpOwnerKicker"] = ("Vorlage", "Template"),
        ["HpTemplateNotice"] = (
            "Vorlage – vor der Übergabe durch den Installateur prüfen und ergänzen. Enthält keine erfundenen Angaben.",
            "Template – review and complete before handover. Contains no invented facts."),
        ["HpOperatingTitle"] = ("Betriebsanleitung", "Operating instructions"),
        ["HpOperatingIntro"] = (
            "Diese Seite beschreibt die Bedienung der Anlage aus Sicht des Betreibers. Die Tabelle nennt die Räume aus der Gebäudestruktur; die Spalte „Bedienung“ ist vom Installateur zu ergänzen.",
            "This page describes how the building owner operates the installation. The table lists the rooms from the building structure; the installer completes the \"Operation\" column."),
        ["HpOperatingRoom"] = ("Raum / Pfad", "Room / path"),
        ["HpOperatingDevices"] = ("Geräte", "Devices"),
        ["HpOperatingFunction"] = ("Bedienung", "Operation"),
        ["HpOperatingNoRooms"] = (
            "Keine Gebäudestruktur vorhanden; Räume und Geräte sind in 03_geraete.csv und 04_topologie.csv dokumentiert.",
            "No building structure present; rooms and devices are documented in 03_geraete.csv and 04_topologie.csv."),
        ["HpOperatingNoDevices"] = ("Keine Geräte im Projekt.", "No devices in the project."),
        ["HpOperatingUnassigned"] = ("ohne Raum", "unassigned"),
        ["HpMaintenanceTitle"] = ("Wartung und Kontakte", "Maintenance and contacts"),
        ["HpMaintenanceIntro"] = (
            "Kontaktdaten und Wartungsintervalle sind Platzhalter und vom Installateur zu füllen. Übergebene Namen werden unverändert übernommen.",
            "Contact details and service intervals are placeholders for the installer to complete. Supplied names are copied unchanged."),
        ["HpMaintenanceInstaller"] = ("Installateur", "Installer"),
        ["HpMaintenanceCompany"] = ("Firma", "Company"),
        ["HpMaintenancePhone"] = ("Telefon", "Phone"),
        ["HpMaintenanceEmail"] = ("E-Mail", "Email"),
        ["HpMaintenanceEmergency"] = ("Notdienst", "Emergency service"),
        ["HpMaintenanceInterval"] = ("Wartungsintervall", "Service interval"),
        ["HpMaintenanceOwner"] = ("Betreiber / Bauherr", "Building owner"),
        ["HpMaintenanceTechnician"] = ("Techniker/in", "Technician"),
        ["HpMaintenanceDevices"] = ("Geräte und Wartung", "Devices and maintenance"),
        ["HpMaintenanceDevice"] = ("Gerät", "Device"),
        ["HpMaintenanceAddress"] = ("Adresse", "Address"),
        ["HpMaintenanceLast"] = ("Letzte Wartung", "Last service"),
        ["HpMaintenanceNext"] = ("Nächste Wartung", "Next service"),
        ["HpMaintenanceRemark"] = ("Bemerkung", "Remark"),
        ["HpMaintenanceNoDevices"] = ("Keine Geräte im Projekt.", "No devices in the project."),
        ["HpPrivacyTitle"] = ("Datenschutz-Hinweise", "Data protection notes"),
        ["HpPrivacyIntro"] = (
            "Diese Seite benennt die Datenarten im Paket. Aufbewahrungsfristen und Löschwege legt der verantwortliche Installateur bzw. Betreiber fest; die Angaben sind Platzhalter.",
            "This page names the data classes in the package. Retention periods and deletion paths are defined by the responsible installer or operator; the statements are placeholders."),
        ["HpPrivacyClasses"] = ("Datenarten im Projekt", "Data classes in the project"),
        ["HpPrivacyClassProject"] = ("Projektname und Kommentar", "Project name and comment"),
        ["HpPrivacyClassRooms"] = ("Raum- und Gebäudenamen ({0})", "Room and building names ({0})"),
        ["HpPrivacyClassDevices"] = ("Gerätenamen und -beschreibungen ({0})", "Device names and descriptions ({0})"),
        ["HpPrivacyClassGa"] = (
            "Gruppenadressnamen, Beschreibungen und Kommentare ({0})",
            "Group address names, descriptions and comments ({0})"),
        ["HpPrivacyClassContacts"] = ("Kontaktangaben (Techniker/in, Betreiber)", "Contact details (technician, building owner)"),
        ["HpPrivacyPersonal"] = (
            "Personenbezogene Daten können in freien Texten stehen, etwa in Raum-, Geräte- oder Gruppenadressnamen (Beispiel: „Wohnung Müller“).",
            "Personal data may appear in free text, for example in room, device or group address names (example: \"Müller apartment\")."),
        ["HpPrivacyRetention"] = (
            "Aufbewahrung: Das Paket ist eine Kopie der Projektdokumentation. Es enthält keine Fristen; die Aufbewahrungsdauer bestimmt die verantwortliche Stelle.",
            "Retention: The package is a copy of the project documentation. It states no deadlines; the responsible party defines the retention period."),
        ["HpPrivacyDeletion"] = (
            "Löschen: ZIP-Datei und alle entpackten Kopien löschen. Sicherungen und Protokollkopien beim Installateur sind separat zu löschen.",
            "Deletion: delete the ZIP file and every extracted copy. Backups and protocol copies held by the installer must be deleted separately."),
        ["HpPrivacyNoSecrets"] = (
            "Nicht enthalten: Passwörter, Schlüssel (.knxkeys), Katalogdateien (.knxprod) und Hersteller-XML.",
            "Not included: passwords, keys (.knxkeys), catalogue files (.knxprod) and manufacturer XML."),
        ["HpRoomBookPath"] = ("Raum / Pfad", "Location / path"),
        ["HpRoomBookKind"] = ("Art", "Kind"),
        ["HpRoomBookFloor"] = ("Geschoss", "Floor"),
        ["HpRoomBookDevice"] = ("Gerät", "Device"),
        ["HpRoomBookAddress"] = ("Adresse", "Address"),
        ["HpRoomBookProduct"] = ("Produkt-Ref", "Product reference"),
        ["HpRoomBookCount"] = ("Geräte (inkl. Unterebenen)", "Devices (incl. sublevels)"),
        ["HpRoomBookUnassigned"] = ("ohne Raum", "unassigned"),
        ["HpEvidenceNeeds"] = ("Programmierung nötig", "Needs programming"),
        ["HpEvidenceMissing"] = ("Fehlende Flags", "Missing flags"),
        ["HpEvidenceDrift"] = ("Signaturabweichung", "Signature drift"),
        ["HpEvidenceOrigin"] = ("Herkunft", "Origin"),
        ["HpEvidenceLastSuccess"] = ("Letzter Erfolg", "Last success"),
        ["HpEvidenceLastError"] = ("Letzter Fehler", "Last error"),
        ["HpOriginDemo"] = ("Demo (Simulation)", "Demo (simulation)"),
        ["HpOriginReal"] = ("Real", "Real"),
        ["HpOriginNone"] = ("nicht aufgezeichnet", "not recorded"),
        ["HpRevisionTime"] = ("Zeitpunkt", "Timestamp"),
        ["HpRevisionSignature"] = ("Signatur", "Signature"),
        ["HpFileManifest"] = ("SHA-256-Prüfsummen und Metadaten", "SHA-256 checksums and metadata"),
        ["HpFileReadme"] = ("Diese Datei", "This file"),
        ["HpFileReport"] = (
            "Druckbarer Projektbericht: Topologie, Geräte, Gruppenadressen, Verknüpfungen, Prüfbefunde",
            "Printable project report: topology, devices, group addresses, links, check findings"),
        ["HpFileGroupAddresses"] = ("Gruppenadressen mit Datapoint-Typen", "Group addresses with datapoint types"),
        ["HpFileDevices"] = ("Geräteliste mit Produktreferenzen", "Device list with product references"),
        ["HpFileTopology"] = ("Bereiche, Linien, Segmente und Geräte", "Areas, lines, segments and devices"),
        ["HpFileAssociations"] = (
            "Funktionsmatrix: Gerät/Kommunikationsobjekt → Gruppenadresse",
            "Function matrix: device/communication object → group address"),
        ["HpFileRoomBook"] = (
            "Räume aus der Gebäudestruktur mit Geräten; Geräte ohne Raum am Ende",
            "Rooms from the building structure with devices; devices without a room at the end"),
        ["HpFileLinks"] = ("Verknüpfungsabdeckung je Hauptgruppe", "Link coverage per main range"),
        ["HpFileLabels"] = (
            "Druckbogen für Gruppenadress- und Geräteetiketten",
            "Printable sheet for group address and device labels"),
        ["HpFileSemantic"] = (
            "Semantischer Export als Turtle (ohne Ontologie-Datei)",
            "Semantic export as Turtle (without ontology file)"),
        ["HpFileProtocol"] = (
            "Abnahmeprotokoll mit Geräteprüfzeilen und Unterschriftenblock",
            "Acceptance protocol with device test rows and sign-off block"),
        ["HpFileEvidence"] = (
            "Konfigurationssignaturen und aufgezeichnete Ladezustände je Gerät",
            "Configuration signatures and recorded load states per device"),
        ["HpFileOperating"] = ("Betriebsanleitung als Vorlage", "Operating instructions template"),
        ["HpFileMaintenance"] = ("Wartung, Kontakte und Intervalle als Vorlage", "Maintenance, contacts and intervals template"),
        ["HpFilePrivacy"] = ("Datenschutz-Hinweise zu den Datenarten im Paket", "Data protection notes on the data classes in the package"),
        ["HpFileRevisions"] = (
            "Revisionsliste mit Inhalts-Hashes und Signaturstatus",
            "Revision list with content hashes and signature status"),
    };
}
