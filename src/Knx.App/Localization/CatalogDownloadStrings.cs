using Knx;

namespace Knx.App.Localization;

/// <summary>Texts for acquiring manufacturer catalogues; follows the application language.</summary>
public static class CatalogDownloadStrings
{
    public static string Get(string key, params object[] args) =>
        LanguageCatalog.Resolve(Map, Overlays.Value, key, Strings.Language, args);

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Overlays =
        new(() => LanguageCatalog.LoadOverlays(typeof(CatalogDownloadStrings).Assembly, "catalog-downloads", Strings.Available));

    private static readonly Dictionary<string, (string De, string En)> Map = new()
    {
        ["Title"] = ("Herstellerkataloge beziehen", "Get manufacturer catalogues"),
        ["Intro"] = ("Herstellerquelle wählen, Berechtigung prüfen und Kataloge in die lokale Bibliothek importieren.", "Choose a manufacturer source, review your permission and import catalogues into the local library."),
        ["Search"] = ("Hersteller oder Quelle suchen", "Find manufacturer or source"),
        ["Manufacturers"] = ("Hersteller", "Manufacturers"),
        ["Sources"] = ("Katalogquellen", "Catalogue sources"),
        ["RefreshRegistry"] = ("Herstellerverzeichnis aktualisieren…", "Refresh manufacturer directory…"),
        ["RegistryConsent"] = ("Verzeichnisabruf bestätigen", "Confirm directory retrieval"),
        ["RegistryNotice"] = ("Das offizielle KNX-Verzeichnis und die Herstellerkennungen werden auf diesem Computer abgerufen und lokal gespeichert. Prüfen Sie die Bedingungen der Quellen. Ein öffentlicher Link erteilt keine Nutzungs- oder Weitergaberechte. Der Datenstand kann unvollständig sein; historische Kennungen sind keine zusätzlichen aktuellen Hersteller.", "The official KNX directory and manufacturer codes will be retrieved on this computer and cached locally. Review the source conditions. A public link grants no use or redistribution rights. Coverage may be incomplete; historical codes are not additional current manufacturers."),
        ["RegistryAccept"] = ("Ich bin zum Abruf und zur lokalen Nutzung dieser Verzeichnisdaten berechtigt.", "I am entitled to retrieve and use these directory data locally."),
        ["RegistryFetch"] = ("Jetzt lokal abrufen", "Retrieve locally now"),
        ["RegistryEmpty"] = ("Noch kein lokales Gesamtverzeichnis. Die geprüften Quellenprofile sind bereits verfügbar.", "No full directory is cached yet. Reviewed source profiles are already available."),
        ["RegistrySummary"] = ("Stand {0}: {1} Verzeichniseinträge, {2} Kennungen. Überschneidungen und historische Einträge sind möglich.", "As of {0}: {1} directory entries, {2} codes. Entries can overlap and include historical records."),
        ["RegistryProgress"] = ("Herstellerverzeichnis: Seite {0}/{1} · {2} Einträge", "Manufacturer directory: page {0}/{1} · {2} entries"),
        ["NoSource"] = ("Für diesen Eintrag ist noch keine automatische Katalogquelle hinterlegt. Die offizielle Website kann weiterhelfen.", "No automatic catalogue source is registered for this entry yet. Check the official website."),
        ["SelectSource"] = ("Quelle wählen, um Umfang und Bedingungen zu prüfen. Mehrfachauswahl mit Strg/Umschalt.", "Choose a source to review its scope and conditions. Use Ctrl/Shift to select several sources."),
        ["OfficialSource"] = ("Offizielle Quelle öffnen", "Open official source"),
        ["SourceConditions"] = ("Quellenbedingungen öffnen", "Open source conditions"),
        ["SourceNoteOriginal"] = ("Quellenhinweis (Originaltext)", "Source note (original text)"),
        ["ManufacturerWebsite"] = ("Herstellerwebsite öffnen", "Open manufacturer website"),
        ["SourceDetails"] = ("{0} · {1}\nGeprüft: {2}\nBedingungsstand: {3}", "{0} · {1}\nChecked: {2}\nTerms revision: {3}"),
        ["UnknownRights"] = ("Die Berechtigung zur automatischen Beschaffung und Nutzung in BusChord ist für diese Quelle nicht bestätigt. Eine eigene bestehende Genehmigung können Sie mit einem Nachweisvermerk dokumentieren.", "Permission for automatic acquisition and use in BusChord has not been confirmed for this source. You can document your own existing authorization with an evidence note."),
        ["RestrictedRights"] = ("Für diese Quelle ist eine ausdrückliche Einschränkung bekannt. Automatischer Abruf und Import bleiben gesperrt; eine allgemeine Zustimmung hebt die Einschränkung nicht auf.", "An explicit restriction is known for this source. Automatic retrieval and import remain blocked; general consent does not override the restriction."),
        ["ManufacturerRights"] = ("Eine Herstellerfreigabe ist für den angegebenen Umfang dokumentiert. Prüfen Sie die Quellenbedingungen vor der Zustimmung.", "Manufacturer permission is documented for the stated scope. Review the source conditions before consenting."),
        ["OwnAuthorization"] = ("Ich verfüge über eine bestehende Genehmigung für Download und lokale Nutzung dieser Quelle in BusChord. Mein Vermerk dokumentiert diese Genehmigung und erteilt keine neuen Rechte.", "I already hold authorization to download and use this source locally in BusChord. My note documents that authorization and grants no new rights."),
        ["AcceptTerms"] = ("Ich habe die Quellenbedingungen geprüft und stimme dem Download und lokalen Import im angegebenen Umfang zu.", "I have reviewed the source conditions and consent to download and local import within the stated scope."),
        ["Evidence"] = ("Nachweis der eigenen Genehmigung (mindestens 20 Zeichen)", "Evidence of your authorization (at least 20 characters)"),
        ["EvidenceHint"] = ("Zum Beispiel: Genehmigungsgeber, Datum und erfasste Produkte. Keine Passwörter oder Zugangsdaten eintragen.", "For example: who granted permission, the date and covered products. Do not enter passwords or credentials."),
        ["SaveConsent"] = ("Für diese Quelle speichern", "Save for this source"),
        ["RevokeConsent"] = ("Freigabe widerrufen", "Revoke authorization"),
        ["ConsentSaved"] = ("Lokale Erklärung für diese Quelle gespeichert.", "Local declaration saved for this source."),
        ["ConsentRevoked"] = ("Freigabe widerrufen. Bereits importierte Kataloge bleiben erhalten.", "Authorization revoked. Previously imported catalogues remain in place."),
        ["DownloadSelected"] = ("Auswahl herunterladen und importieren", "Download and import selected"),
        ["DownloadAuthorized"] = ("Alle freigegebenen herunterladen", "Download all authorized"),
        ["AuthorizedCount"] = ("{0} ausgewählt · {1} Quellen lokal freigegeben", "{0} selected · {1} sources authorized locally"),
        ["Report"] = ("Ergebnisse", "Results"),
        ["ReportSummary"] = ("{0}: {1} Kataloge hinzugefügt · {2} Dateien · {3} MiB empfangen{4}", "{0}: {1} catalogues added · {2} files · {3} MiB received{4}"),
        ["CancelledSuffix"] = (" · abgebrochen", " · cancelled"),
        ["NoReports"] = ("Noch keine Downloadberichte.", "No download reports yet."),
        ["ReportPersistenceWarning"] = ("Der Ergebnisbericht konnte nicht gespeichert werden: {0}", "The result report could not be saved: {0}"),
        ["MoreReportItems"] = ("Weitere {0} Einträge im vollständigen lokalen Bericht: {1}", "{0} more entries in the complete local report: {1}"),
        ["ConflictHint"] = ("Identische Dateien werden übersprungen; vorhandene Kataloge bei Konflikten erhalten. Neue Applikationen ändern keine Projektgeräte.", "Identical files are skipped; existing catalogues are preserved on conflicts. New applications do not change project devices."),
        ["Progress"] = ("{0}: {1} · {2} MiB{3} · {4}/{5} Quellen", "{0}: {1} · {2} MiB{3} · {4}/{5} sources"),
        ["TotalBytes"] = (" von {0}", " of {0}"),
        ["OperationFailed"] = ("Vorgang fehlgeschlagen: {0}", "Operation failed: {0}"),
        ["LibraryRefreshFailed"] = ("Dateien wurden importiert, die Bibliotheksansicht konnte jedoch nicht aktualisiert werden: {0}. Verwenden Sie „Neu laden“ im Katalog.", "Files were imported, but the library view could not be refreshed: {0}. Use Reload in the catalogue."),
        ["LibraryReloadContinues"] = ("Die Bibliothek wird im Hintergrund weiter aktualisiert. Abgeschlossene Importe bleiben erhalten.", "The library continues refreshing in the background. Completed imports remain in place."),
        ["CancelPending"] = ("Abbruch angefordert. Bereits abgeschlossene Importe bleiben erhalten.", "Cancellation requested. Completed imports remain in place."),
        ["AuthMissing"] = ("Keine lokale Freigabe", "No local authorization"),
        ["AuthManufacturerTerms"] = ("Herstellerbedingungen akzeptiert", "Manufacturer terms accepted"),
        ["AuthUserDeclaredAuthorization"] = ("Eigene Genehmigung dokumentiert", "Personal authorization documented"),
        ["AuthRestricted"] = ("Eingeschränkte Quelle · gesperrt", "Restricted source · blocked"),
        ["AuthManualOnly"] = ("Manueller Abruf erforderlich", "Manual retrieval required"),
        ["PhaseQueued"] = ("Wartet", "Queued"),
        ["PhaseDownloading"] = ("Herunterladen", "Downloading"),
        ["PhaseValidating"] = ("Datei prüfen", "Validating"),
        ["PhaseImporting"] = ("Importieren", "Importing"),
        ["PhaseCompleted"] = ("Abgeschlossen", "Completed"),
        ["PhaseFailed"] = ("Fehlgeschlagen", "Failed"),
        ["PhaseCancelled"] = ("Abgebrochen", "Cancelled"),
        ["StatusImported"] = ("Importiert", "Imported"),
        ["StatusIdenticalSkipped"] = ("Identische Datei übersprungen", "Identical file skipped"),
        ["StatusConflictKeptExisting"] = ("Konflikt: vorhandenen Katalog erhalten", "Conflict: existing catalogue preserved"),
        ["StatusUnsupported"] = ("Nicht unterstützte Datei", "Unsupported file"),
        ["StatusNotAuthorized"] = ("Keine Berechtigung dokumentiert", "Authorization not documented"),
        ["StatusFailed"] = ("Fehlgeschlagen", "Failed"),
        ["StatusCancelled"] = ("Abgebrochen", "Cancelled"),
    };
}
