# ETS6 – Projekt-Lebenszyklus, Dashboard, Archiv, Vergleich und Produktersatz

Read-only-REA-Analyse, 2026-10-21. Basis: Commit
`12fc904ee05089976eac93426141c6fdb348ffe1`, Branch `agents/ets-rea/project`,
Worktree `C:\projects\ai_work\agent-wt\ets-rea-project`.

Zielinstallation: `C:\Program Files (x86)\ETS6`, ETS/Assemblies **6.4.8718.0**.
Es wurde kein ETS-Prozess gestartet, keine UI bedient und keine Busaktion
ausgeführt. Belegt sind ausschließlich Metadaten, IL-Struktur, Typen/Methoden
und Ressourcenstrings der ausgelieferten Artefakte. Die Dekompilation mit
ILSpy 9.1 und das Auslesen der Satellitenressourcen waren lesend; Assemblys
wurden nicht ausgeführt.

Verwandte Vorarbeiten: `analysis/project-encryption.md` (AES-Container),
`analysis/parity-archives.md` (BusChord-Archivformat), `analysis/ets-features.md`
(übrige ETS-Funktionen).

Konfidenz: **belegt** = direkt in Metadaten/IL/Ressourcen beobachtet;
**abgeleitet** = aus Typen, Aufrufen, Strings oder Dateinamen geschlossen, nicht
ausgeführt; **unbekannt** = durch diese Analyse nicht entschieden.

## 0. Kurzfassung

ETS6 speichert Projekte nicht als eine bearbeitbare Datei, sondern in einem
lokalen Projektspeicher (Verzeichnis pro Projekt, gehashte Teil-Dateien). Ein
`.knxproj` ist Export/Import und Archiv-Artefakt. Speichern geschieht
automatisch über einen Hintergrunddienst; einen expliziten „Speichern unter“-
Befehl für lokale Projekte gibt es nicht. Das Projektarchiv ist ein
Dateisystem-Archiv mit Projektordnern, Versionsordnern (Hash), Metadaten,
Sperren und optionalem Kollaborationsmodus. Wiederherstellungspunkte und
Datenbank-Backups sind davon getrennte Mechanismen. Dashboard, Suche,
Projektvergleich, Produktersetzung und Reports sind eigenständige Subsysteme;
BusChord deckt davon heute nur einen kleinen, dateibasierten Ausschnitt ab.

## 1. Funktionsinventar und Empfehlung

| Funktion | ETS6-Beleg | BusChord heute | Empfehlung | Konfidenz |
|---|---|---|---|---|
| Neues Projekt | `ProjectService.CreateNewProject`, `NewProjectViewModel`, Wizard | `NewProjectWindow`, `Knxproj.NewProject` | **native** (Dialog/Wizard-Parität später) | belegt |
| Projekt öffnen | `ProjectService.OpenProject`, `OpenProjectSilently`, Passwort-/Konfliktpfad | `LoadProject(path)` | **native**; Archivkonflikte/Recovery ergänzen | belegt |
| Speichern | `PersistenceService` (10-s-Timer), `Project.Save` | explizites `SaveProject()` | **native** Auto-Save + Integritätsprüfung | belegt/abgeleitet |
| Speichern unter | kein lokaler Save-As-Befehl; nur Export `.knxproj` | `SaveProjectAs(path)` | **dokumentiert nicht unterstützt** (ETS-Parität) | abgeleitet |
| Schließen | `CloseProjectService`, `ProjectService.CloseProject`, Auto-Backup | Fenster/Projektwechsel | **native** | belegt |
| Projektarchiv/Backup | `ProjectArchiveServiceManager`, `ProjectArchiveService`, `ArchivePersistenceService` | nicht vorhanden | **native** (Kern-Gap) | belegt |
| Restore/Versionen | `GetProjectFromArchiveAsync`, `ArchiveProjectVersion`, `Pin/Unpin` | nicht vorhanden | **native** | belegt |
| Recovery Points | `CreateRestorePointOperation`, `Project.Restore` | nicht vorhanden | **native** | belegt |
| Datenbank-Backup/Restore | `CreateBackupOperation`, `RestoreBackupOperation` | nicht vorhanden | **später/native**, niedrigere Priorität | belegt |
| Import | `ImportProject`, Import-Wizard, ETS5-Archivkonvertierung | nur `.knxproj` öffnen | **approximate**, dann native | belegt/abgeleitet |
| Export | `ExportProjects`, Export-Wizard, OPC/Turtle/Archiv | `.knxproj` speichern | **approximate**; Formate schrittweise | belegt |
| Drucken/Reports | `Knx.Ets.Reports`, 12 Report-Typen, `PrintLibrary` | HTML-Projektbericht | **approximate** (HTML/PDF-Druck), Reports später native | belegt |
| Dashboard/Projektliste | `TabDashboard`, `ProjectsViewModel`, Tiles, Breadcrumbs | Recents-Liste | **native** (mehrere Projekte nötig) | belegt |
| Suche/Filter | `DashboardSearch`, `SearchTokenizer`, Quick-Search | keine | **native** | belegt/abgeleitet |
| Projekteigenschaften | `ProjectDetails*`, Overview/Details/History/Attachments/Security | Name/Kommentar | **approximate**, dann native | belegt |
| Mehrere Projekte/Fenster | `ApplicationViewModel.OpenNewWindow`, `MainWindowViewModel.Tabs` | ein Projekt/Fenster | **native** | belegt |
| Instanzen/CLI | `ISingleInstanceApplicationWatcher`, `StartupOptions` | keine | **später/native** | belegt |
| Verschlüsselte Archive | AES-256-Container, `ZipContainerPasswordProtectedProjectStore` | Lesen ja, Schreiben nein | **approximate** (nur Lesen), Re-Encrypt später | belegt |
| Projektvergleich | `ProjectComparer`, `DifferenceReport`, Compare-Dialoge | zwei Dateien, einfacher Diff | **approximate**, dann native | belegt |
| Produkt ersetzen | `ReplaceProductWizard`, `ReplaceProductOperation` | RefId-Ersetzung mit Preview | **approximate**, dann Wizard | belegt |
| Demo/Reset | Demo-Lizenzmodus; Settings-/Workspace-Reset | nicht vorhanden | **dokumentiert nicht unterstützt** (Lizenz-Demo); Reset approximieren | abgeleitet |

## 2. Projekt-Lebenszyklus

### 2.1 Neues Projekt

**Sichtbarer Ablauf.** Zwei Wege:

1. Dashboard-Kachel/„Neues Projekt“ (`DashboardNewProject`, `NewProject`;
   `ToolTipMainToolbarNew` = „Erzeugt ein neues Projekt“) öffnet den
   `NewProjectViewModel`-Dialog. Felder: Projektname (Default
   `DefaultNames.DefaultProjectName`), Projekttyp-Auswahl, Schalter
   „Default-Topologie erzeugen“ (Default aus `MiscSettings.CreateDefaultLine`).
   Beim Anlegen wird `ProjectService.CreateNewProject(name, null,
   createDefaultTopology, createBuilding: true, projectType)` aufgerufen und
   das Projekt direkt in einem Tab geöffnet (`ITabCreationService.OpenProject`).
2. „Projekt-Wizard“ (`ProjectCreationWizard` = „Starte Projekt-Wizard“,
   `JumpTaskCreateProjectAssistant` = „Projekt Wizard“): Seiten
   „Vorlage auswählen“, „Projekteigenschaften“, „Funktionen“, „Gruppen“
   (`ProjectCreationTemplateWizardStepTemplate`,
   `...StepProjectProperties`, `...StepFunctions`, `...StepGroups`).
   Vorlagen können leer, vordefiniert oder benutzerdefiniert sein; der Wizard
   erzeugt Gebäude-/Funktionsstruktur und Gruppenadressen und kann die
   Struktur als Projektvorlage speichern (`SaveTemplate`,
   `ProjectCreationTemplateWizardSummaryDescription`).

**Typen/Methoden.** `Knx.Ets.ViewModel.Application.Dashboard.NewProjectViewModel`
(`CreateProjectCommand`), `...Wizards.ProjectCreationWizard.ProjectCreationWizardViewModel`,
`Knx.Ets.ViewModel.Service.ProjectService.CreateNewProject`
(File-Offset `598752`), `Root.Projects`, `ProjectType`.

**Daten-/Dateiwirkung.** Anlegen eines lokalen Projekt-Stores, Optional
Topologie (Bereich/Linie) und Gebäudestruktur, Projekt-Tab. Der Name muss
nicht leer und eindeutig sein; Fehler: `NewProjectFailed` („Neues Projekt
konnte nicht erzeugt werden.“), `ProjectWithNameExists` („Ein Projekt mit dem
Namen '{0}' existiert bereits im Projektspeicher.“),
`ProjectCreationTemplateWizardTheProjectNameCanNotBeEmpty`.

**Grenzen/Edge Cases.** Ein Passwort wird im normalen Dialog nicht gesetzt
(`password: null`); per CLI `--createProject` + `--password` oder später über
Projekteigenschaften. Lizenzgrenzen (Lite/Home) begrenzen die Gerätezahl, nicht
die Projektzahl (`KNX.package`: `ProjectCount=unlimited`, `DeviceCount=20/64`).

**BusChord-Lücke.** Neues Projekt und Default-Topologie existieren; Wizard,
Vorlagen, Gebäude-/Funktionsautomatik, Projekttyp und Passwort bei Anlage
fehlen. **Empfehlung:** native Dialogparität; Wizard als eigener Meilenstein.
**Konfidenz:** belegt (Typen/Methoden/Strings), Ablauf abgeleitet.

### 2.2 Projekt öffnen

**Sichtbarer Ablauf.** „Projekt öffnen“ (`OpenProject`, `OpenProjectFile` =
„Projektdatei öffnen“; `ToolTipMainToolbarOpen` = „Öffnet ein bestehendes
Projekt“) bzw. Klick auf eine Projektkachel (`TooltipOpenProject` = „Öffnet das
ausgewählte Projekt“, `TabDashboard.OpenProject(ProjectId)`).
`ProjectService.OpenProject(project, accessKeys, markAsOpened, dialogType,
confirmConflict)` prüft Zugriffsschlüssel, Schema-/ETS-Update-Bedarf,
Integrität und Archivkonflikt; `OpenProjectSilently` ist der nicht-interaktive
Pfad. Bei Passwortschutz erscheint `EnterPasswordDialogViewModel`
(`EnterPasswordForLocal` = „Passwort für das lokale Projekt eingeben“).

**Typen/Methoden.** `ProjectService.OpenProject` (Offset `591104`),
`OpenProjectSilently` (`592076`), `Project.Open`, `AccessKeyManager.CheckAccess`,
`Project.ValidateProjectIntegrity` (`966424`), `EnterPasswordDialogViewModel`.

**Daten-/Dateiwirkung.** Öffnen des lokalen Stores, Einlesen von Projekt-/
Installationsteilen, Prüfen der Hashes (`HashedFileStream`/`Store.CheckAllProjectFiles`),
Setzen von `IsOpen`, Anlegen des Projekt-Tabs und des Projekt-Scopes. Kein
Kopieren der Projektdatei.

**Fehler/Edge Cases.** `EtsUpdateRequiredInfo` / `ProjectStoreOutOfDateInfo`
(neuere ETS-/Schema-Version), `MessageProjectIntegrityHashInvalid` (beschädigte
Integrität, Recovery Point nötig), `MessageFileAccessDenied`,
`MessageOpenProjectNotAllowed` (Lizenz), `ConfirmOpenProjectWithConflict`
(lokal und Archiv geändert), `ConfirmOpenProjectWithArchiveChanges`,
`ProjectNotFoundError`, `UpdateRequiredTooltip`.

**BusChord-Lücke.** Öffnen/Schema-/Integritätsprüfung teilweise vorhanden;
Projekt-Passwort, Recovery-Point-Verweis, Archivkonflikt und Lizenzprüfung
fehlen. **Empfehlung:** native Öffnen-Pipeline mit klaren Fehlerzuständen.
**Konfidenz:** belegt.

### 2.3 Speichern, Auto-Save und „Speichern unter“

**Sichtbarer Ablauf.** ETS6 hat keinen expliziten „Speichern“-Knopf für lokale
Projekte; die Haupt-Toolbar enthält Neu/Öffnen/Drucken/Schließen
(`ToolTipMainToolbarNew/Open/Print/Close`). `Save`/`SaveTag` existieren als
generische Ressourcen, aber keinen lokalen Save-As-Befehl. Stattdessen:
`PersistenceService.Start()` startet einen Timer (Default `Timeout = 10 s`);
`SaveNow(enforce)` und `SaveAllProjectsIfNoTransactionOpenAsync()` speichern,
wenn das Projekt „dirty“ ist und kein Dialog/Wizard/Transaction/laufender
Vorgang/Undo-Vorgang aktiv ist. Zusätzlich erzwingt ein Fallback nach
`10 × Timeout` (100 s) einen Save-Versuch.

**Typen/Methoden.** `Knx.Ets.ViewModel.Service.PersistenceService`
(`Start` Offset `564496`, `SaveNow` `564652`,
`SaveAllProjectsIfNoTransactionOpenAsync` `564864`),
`Project.Save(bool enforceSave)`, `ObjectContext.WriteContext`,
`ProjectIntegrityInvalidException`.

**Daten-/Dateiwirkung.** Schreibt den geänderten Objektbaum über den
Projekt-Store, aktualisiert `LastSaved`, prüft die Projektintegrität. Bei
Dateizugriffsfehlern: `MessageFileAccessDenied` bzw.
`MessageFileAccessDeniedCloseETS` und kontrollierter Shutdown
(`ExecuteEmergencyShutdown(19)`).

**„Speichern unter“.** Für lokale Projekte nicht vorhanden. Der äquivalente
Export ist „Projektdatei exportieren“ (`ExportProjectFile`, `.knxproj`) über den
Export-Wizard; „Projekt umbenennen“ (`RenameProject`) ist kein Save-As.
**Empfehlung:** BusChords `SaveProjectAs` als bewusste BushChord-Erweiterung
dokumentieren, nicht als ETS-Parität; zusätzlich Auto-Save/Recovery ergänzen.
**Konfidenz:** belegt (Timer/Save), Save-As-Aussage abgeleitet (kein Befehl
vorhanden); exakte Dirty-Heuristik teilweise unbekannt.

### 2.4 Schließen

**Sichtbarer Ablauf.** „Projekt schließen“ (`CloseProject` =
„Projekt schließen“; `ToolTipMainToolbarClose` = „Schließt das aktuelle
Projekt“) bzw. Tab schließen. `CloseProjectService.TryCloseProject` fragt bei
offenem Projekt `ContinueCloseProjectConfirmation` („Das Projekt '{0}' muss vor
der Ausführung geschlossen werden. Möchten Sie fortfahren?“). Danach
`ProjectService.CloseProject(project)`; bei Fehlern
`MessageProjectNotClosing` („Der Vorgang wurde abgebrochen, da ein Projekt
nicht geschlossen werden konnte.“). `PersistenceService` speichert vorher.
`ArchivePersistenceService` kann beim Schließen automatisch ein Backup ins
Archiv senden (Setting `SynchronizeWithArchiveOnProjectClose`), abhängig vom
`ArchiveSyncStates`; bei Konflikt/Archivänderung erscheint eine Warnung
(`AutomaticBackupFailedTo...`).

**Typen/Methoden.** `CloseProjectService.TryCloseProject`,
`ProjectService.CloseProject` (Offset `599056`), `ForceCloseProject`,
`Project.Close`, `ArchivePersistenceService.OnProjectClosed`,
`PersistenceService.SaveNow`.

**Daten-/Dateiwirkung.** Save, Schließen des Stores, Freigabe des
Projekt-Scopes, Entfernen des Tabs, optional Archiv-Backup, optional
Projektlog-Dialog (`ProjectLogDialogOnProjectClose`).

**Fehler/Edge Cases.** `ItIsTheLastProject` („Das letzte Projekt kann nicht
geschlossen werden!“) ist als Ressource vorhanden; im Dashboard-Modell bleibt
der Dashboard-Tab bestehen, daher ist die genaue Bedingung **abgeleitet**.
`MessageDownloadsRunningQuitAnyway` bei laufenden Downloads. Schließen kann
durch offene Dialoge/Panels blockiert werden (`IUserCanBlockClose`).

**BusChord-Lücke.** Kein Tab-/Projekt-Scope, kein Auto-Backup beim Schließen,
kein Projektlog. **Empfehlung:** native Close-Pipeline mit Save + optionalem
Archiv-Backup. **Konfidenz:** belegt.

### 2.5 Import

**Sichtbarer Ablauf.** „Projekt importieren“ (`ImportProject`,
`DashboardSidebar_ImportProject`, `JumpTaskImportProject`) startet den
Import-Wizard (`ImportWizardViewModel`, `WizardTypes.ProjectImport`). Seiten/
Schritte: Datei analysieren (`FileImporterProjectStepAnalyseData` =
„Analysiere die Importdaten“), Kompatibilität/Plug-ins, Passwort
(`ImportWizardPageTitleEnterPassword`, `ImportProjectPasswordInfoText`),
Projekt(e) auswählen (`ImportWizardPageSelectProjectsTitle`), Zusatzsoftware
(`FileImporterProjectStepInstallingBaggages`), Daten importieren
(`FileImporterProjectStepImportData`), Ergebnis. Bei gleicher Projekt-GUID:
`ImportGuidConflictDialogText` mit „Kopie importieren“ (`Dialog_ImportCopy`)
oder „Projekt überschreiben“ (`Dialog_OverwriteProject`).

**Typen/Methoden.** `ProjectService.ImportProject` (Offset `594712`),
`ProjectImportWizardViewModel`, `ProjectFileImporterViewModel`,
`ProjectConversionService`, `ImportGuidConflictOption`,
`ProjectImportFailedException`, `ImportWizardViewModel`.

**Daten-/Dateiwirkung.** Liest `.knxproj`/`.PR*`-Dateien, konvertiert ältere
Schemata (ETS4/5), prüft Signaturen/Zertifikate, installiert Baggages
(Plug-ins/DCAs), legt ein lokales Projekt im Projektspeicher an oder
überschreibt es, inkl. Produktdaten/Applikationsprogrammen. ETS5-Archivdateien
(`*.knxprojarchive`) können migriert werden
(`ConvertETS5ArchiveProjects_TooltipText`, `Ets5ArchiveFiles`).

**Fehler/Edge Cases.** `FileCreatedWithNewerSchema`, `InvalidSchemaVersion`,
`InvalidImportXml`, `ImportError`, `ImportWithoutWizardReportPartialSuccess`,
`NotAllEts5ArchivedProjectsImportedErrorMessage`, `MessageProjectFileTooLarge`,
`CannotImportProject_ReasonTemplate`, `ImportProjectFailed`,
`ProjectImportSucceeded`; Plug-in-/Baggage-Fehler.

**BusChord-Lücke.** Nur direktes Öffnen/Speichern eines `.knxproj`; kein
Wizard, keine Konfliktbehandlung (Kopie/Überschreiben), keine
ETS4/5-Konvertierung, keine Baggage-/Plug-in-Installation, keine
Archivmigration. **Empfehlung:** **approximate** (klarer Importdialog mit
Kopie/Überschreiben und Fehlerbericht), danach native Konvertierung.
**Konfidenz:** belegt (Typen/Strings), Reihenfolge abgeleitet.

### 2.6 Export

**Sichtbarer Ablauf.** „Projekt exportieren“ (`ExportProject`,
`DashboardSidebar_ExportProject`, `ToolTipContextBarExport` =
„Exportiert Produkte in eine Datei“) startet den Export-Wizard
(`ExportWizardViewModel`, `WizardTypes.ProjectExport`). Typen:
`KnxProjectExportHandler` (`.knxproj`), `OpcProjectExportHandler` (`.esf`,
„OPC Exportdatei wählen“), `TurtleProjectExportHandler` (semantischer Export),
Produkt-Export, Archiv-Export. Optionen: Kataloginformationen
(`SetupHelpCatalogInformation`), verwendete/installierte DCAs
(`SetupHelpIncludeRelevantDcas`), kompatible Applikationsprogramme
(`SetupHelpIncludeCompatibleApplicationPrograms`), Projekt verkleinern
(`SetupHelpShrinkProjectExport`).

**Typen/Methoden.** `ProjectService.ExportProjects` (Offset `596744`),
`ProjectExportService`, `ExportWizardViewModel`, `ProjectExportFileViewModel`,
`ProjectExportFileService`, `KnxProjectExportHandler`.

**Daten-/Dateiwirkung.** Erzeugt eine `.knxproj`-ZIP-Datei (Projekt- und
Installationsteile, optionale Produkt-/Katalogdaten, UserFiles/DCAs), OPC-Datei
oder semantische Turtle-Datei. Lizenzprüfung: `NamedProjectsExportNotAllowed`
(„Diese Projekte können nicht mit den installierten Lizenzen exportiert
werden: {0}“), `PendingOperationsPreventExportMessage_*`,
`CannotExportBecauseWriteProtected`, `ArchiveExportNameConflictWarning`.

**Fehler/Edge Cases.** `ExportFailed`, `ResultPageStatusProjectExportFailed`,
`ArchiveManagerFailedToExportFromArchive`, `ExportedNoProject`,
`MessageExportProjectFailed`, Überschreiben-Konflikt.

**BusChord-Lücke.** `.knxproj`-Export und HTML-Bericht vorhanden; Wizard,
Optionen, OPC/Turtle, Produkt-/Archiv-Export, Lizenz-/Pending-Prüfungen fehlen.
**Empfehlung:** **approximate** Exportdialog; OPC/Turtle als
„dokumentiert nicht unterstützt“ kennzeichnen. **Konfidenz:** belegt.

### 2.7 Drucken und Reports

**Sichtbarer Ablauf.** Haupt-Toolbar „Drucken“ (`ToolTipMainToolbarPrint` =
„Öffnet den 'Drucken'-Dialog“) öffnet den Druckdialog. Der Workspace hat ein
„Reports“-Panel (`Reports`, `MenuItemReports` = „Reports“,
`PanelReports`), in dem Berichte ausgewählt, erzeugt (`GeneratingReport` =
„Erzeuge Bericht“), gedruckt (`Print` = „Drucken“) und exportiert werden.
Report-Einstellungen: `SettingsDialog` = „Report Einstellungen“, Logo
(`SetupHelpReportLogo`), Papier/Seite (`PageSettingsVM`), Schrift
(`ReportsFontTextBlock` weist auf Segoe UI hin).

**Berichtstypen (aus `Knx.Ets.Reports.dll`).**
`BuildingsReport`, `BuildingFunctionsReport`, `TradesReport`,
`TopologyReport`, `GroupAddressesReport`, `DeviceDetailReport`,
`PartsListReport`, `StatisticsReport`, `ProjectCompletionReport`,
`ProjectHistoriesReport`, `CertificatesReport`, `SecurityReport`; zusätzlich
`XtraLineScanReport` (Linien-Scan). Die Zuordnung der lokalisierten Titel ist
**abgeleitet** (Klassen-/Attributnamen), nicht aus einer laufenden UI belegt.

**Typen/Methoden.** `Knx.Ets.Reports.ViewModel.ReportViewVM` (Reflection über
`[ReportInfo]`-Attribute, `Reports`-Array, `GroupedReports`),
`ReportVM` (`LoadAsync`, `ExportCommand` Offset `35672`,
`ExportReportAsync`), `ReportSettingsDialogVM.Save`, `PageSettingsVM`,
`Knx.Ets.PrintLibrary.PrintHelper` / `PrintPoint` (Layout/Seitenumbruch),
DevExpress `XtraReport.PrintingSystem`.

**Daten-/Dateiwirkung.** Erzeugt ein `XtraReport`-Dokument aus dem
Projektmodell, zeigt es in der Vorschau und kann es drucken oder exportieren.
Exportformate laut `ReportVM`: **PDF, HTML, MHT, CSV, TXT, RTF, DOCX, XLSX**
(Filter `SaveFileDialog_*`). Fehler: `PrintingFailed` („Beim Erstellen des
Dokuments ist ein Fehler aufgetreten...“), `DiagnosticWizard_PrintFailed`
(keine Seiten gesendet), `MessageExportProjectFailed`.

**Projektvergleichs-Bericht.** Separat: `ProjectCompare.Report.DifferenceReport`
(`CreateReportAsync`, `SaveReportAsync` Offset `28624`) erzeugt einen
DevExpress-`XtraDifferenceReport` (Titel „Differenzbericht“, Legende
Hinzugefügt/Geändert/Gelöscht) und speichert ihn als PDF
(`CompareProjectReportFileFilter` = „PDF-Dateien (*.pdf)|*.pdf|...“).

**BusChord-Lücke.** Nur ein HTML-Projektbericht (`Knx.Reports.ProjectReport`),
kein Druckdialog, keine Vorschau, kein PDF/XLSX/RTF/DOCX-Export, keine
Report-Auswahl/-Einstellungen, keine 12 Berichtstypen. **Empfehlung:**
**approximate** (HTML plus PDF-Druck), danach ausgewählte Berichte nativ.
**Konfidenz:** Typen/Formate belegt; UI-Reihenfolge und Berichtsinhalte
abgeleitet.

### 2.8 Wiederherstellungspunkte (Recovery Points)

**Sichtbarer Ablauf.** ETS kann beim Schließen eines Projekts automatisch einen
Wiederherstellungspunkt erzeugen. Setting-Text
`RecoveryPointOptionsToolTip`: „Ja“ erzeugt beim Schließen einen
Wiederherstellungspunkt, „Nein“ keinen, „Beim Projekt Schließen fragen“ öffnet
einen Dialog. Beim Start kann ETS die Wiederherstellung anbieten:
`ProjectRecoveryQuestion` („Möchten Sie für das Projekt '{0}' den Stand vom
'{1:G}' wiederherstellen? ...“), `RestoringProjectFromRecoveryPointLabel`,
`ProjectRecovery` („Projektwiederherstellung“). Bei fehlendem Punkt:
`RestoreMissingRecoveryPoints` („Noch kein archiviertes Projekt oder
Wiederherstellungspunkt verfügbar.“).

**Typen/Methoden.** `CreateRestorePointOperation` (Offset `1976980`),
`Project.CreateRestorePoint(int maxRestorePoints)`, `Project.Restore(info)`,
`Project.ClearRestorePoints()`, `Root.GetAllProjectRestorePoints`,
`ProjectRestorePointInfo`, `GetRestorePointInfos` (ProjectService Offset
`590752`). Der Operator speichert zuerst das Projekt (`Project.Save()`), packt
das Projektverzeichnis in eine ZIP-Datei im Datenpfad
(`ZipWrapper.AddDirectory`, Name aus Projekt-ID + Zeitstempel + Suffix) und
löscht danach die ältesten Punkte bis zur konfigurierten Anzahl
(`maxRestorePointsToKeep`). Restore tauscht das Projektverzeichnis; Fehler:
`RestorePointCannotBeRestored`, `RestoringProjectFromRecoveryPointLabel`.

**Daten-/Dateiwirkung.** ZIP-Snapshot des lokalen Projektverzeichnisses,
Rotation nach Anzahl, Wiederherstellung durch Verzeichnisersatz. Kein
Undo/Redo (`IsUndoable = false`).

**BusChord-Lücke.** Keine Recovery Points, keine Rotation, kein
Startup-Recovery-Dialog. **Empfehlung:** **native** (hoher Datenwert, relativ
kleiner Baustein). **Konfidenz:** belegt (Operation/Project-API), Dateiname/
Suffix **abgeleitet** (verschlüsselte Strings).

### 2.9 Projektarchiv, Backup und Restore

**Begriff.** ETS6 „Archiv“ ist ein Dateisystem-Archiv (konfigurierbarer
Archivpfad) mit Projektordnern, Versionen, Metadaten und Sperren. „Sicherung
im Archiv erstellen“ (`BackupToArchive`, `CheckedIn`, `CheckInProject`) ist ein
Check-in; „Aus dem Archiv wiederherstellen“ (`RestoreFromArchive`, `CheckedOut`,
`RestoreVersion`) ein Check-out. Zusätzlich gibt es einen
Kollaborationsmodus mit Sperren/Check-out durch Benutzer/Rechner.

**Typen/Methoden.** `Knx.Ets.ViewModel.ProjectArchive.ProjectArchiveServiceManager`
(`SendProjectToArchiveAsync` Offset `898732`, `GetProjectFromArchiveAsync`
`900412`, `ExportArchiveAsync` `901148`), `ProjectArchive.ArchivePersistenceService`
(Auto-Backup beim Schließen), `Knx.Ets.ProjectArchive.ProjectArchiveService`
(`SendToArchiveAsync` Offset `30888`, `GetArchivedProjectsAsync` `31316`,
`GetVersionAsync` `31716`, `HasPassword` `34308`, `IsPasswordMatching` `34984`,
`FindProjectFiles` `37932`, `Lock/Release/Pin/Unpin`, `OptimizeArchiveAsync`
`40064`). Datenmodell: `ArchiveFolder`, `ArchiveLinkedFolder`,
`ArchiveProject`, `ArchiveProjectVersion`, `ArchiveAction`, `VersionHash`,
`ArchiveState` (Write/…), `ArchiveSyncStates`.

**Dateilayout (belegt/abgeleitet).**
- Projektordner im Archiv: aus der Projekt-GUID gebildeter Kurzname
  (Base32 der ersten 5 GUID-Bytes) + `.archive` (Konstante
  `ArchiveProjectFolderExtension = ".archive"`; Ressourcenwarnung:
  „Ein Ordner im Archiv darf nicht auf '.archive' enden...“). Exakte
  Pfadzusammensetzung **abgeleitet**.
- Versionsordner: `VersionHash`; Hash = MD5 über die MD5-Hashes der Dateien in
  der Reihenfolge `Project, Info, Image, Trace` (fehlendes Image = Nulldigest),
  mit Trenner-Bytes, danach MD5 und Base32 der ersten 8 Bytes, lower-case
  (`ArchiveHashService.CalculateArchiveHash`, Offset `16504`).
- Dateien pro Version: genau eine Projektdatei (`*.knxproj`), `project.info`
  (`ProjectInfoFileName`), optional `project.jpg` (`ProjectImageFileName`) und
  `project.trc` (`ProjectTraceFileName`); Projektordner zusätzlich
  `status.info` (`StatusInfoFileName`). `FindProjectFiles` akzeptiert genau
  eine Projektdatei und liefert sonst einen Fehler.
- Verknüpfte Ordner: `.archivelink` (`ArchiveLinkExtension`).

**Sichtbarer Ablauf.** Dashboard/Archiv: „Archiv“, „Archivprojekte“,
Breadcrumbs, Versionsliste (`DashboardTabHeaderArchiveVersions` = „{0}
Versionen“), Aktionen „Sicherung im Archiv erstellen“, „Aus dem Archiv
wiederherstellen“, „Version wiederherstellen“, „Backup ins Archiv kopieren“,
„Projekt aus Archiv löschen“, „Sperren“/„Entsperren“, „Anheften“ (Pin),
„Archiv optimieren“, „Archiv-Ordner löschen/umbenennen/verschieben“,
„Verknüpften Ordner löschen“. Status-Hinweise:
`BackupMode_InSync`, `..._LocalChange`, `..._ArchiveChange`, `..._Conflict`,
`..._NotArchived_*`, `..._Label_*`, `Archive_VersionsMaximumNumberToolTip`
(max. automatische Versionen; älteste wird entfernt, angeheftete bleiben),
`NumberOfRetainedProjectVersions`.

**Daten-/Dateiwirkung.** Check-in schreibt Metadaten
(`project.info`), optional Bild/Trace, berechnet den Versionshash und legt den
Versionsordner an; `ArchiveAction` (Zeit, Benutzer, Maschine, Status, Hash,
Kommentar) wird in `status.info` geführt. Check-out liest die Version und
ersetzt/aktualisiert das lokale Projekt. Sperren verhindern Änderungen durch
andere; Konflikte werden über `ArchiveSyncStates` erkannt. „Archiv optimieren“
konvertiert ab ETS 6.3 in ein neues Format; alle Projekte werden geschlossen
und ETS neu gestartet (`OptimizeArchiveDescription`). Auto-Backup beim Schließen
nur bei `SynchronizeWithArchiveOnProjectClose` und passendem Sync-Zustand.

**Fehler/Edge Cases.** `ProjectArchiveInvalidPath`, `ArchivePathInaccessible`,
`ProjectGuidNotFoundInArchive`, `NoValidArchiveProjectHashVersion`,
`ProjectAlreadyLocked`, `ProjectLockedBy`,
`ProjectCannotBeDeletedBecauseItIsLocked`,
`ArchiveOperationAbortedDueToChangeOfAnybodyElse`,
`ArchiveOperationAbortedDueToFileAccessFailed`,
`ArchiveDirectoryNameNotAllowed`, `Directory_ConsistsOfOnlyWhitespaces`,
`ArchiveManagerFailedReadingEntry`, `ArchiveManagerFailedUpdatingLocalProject`,
`ProjectDoesNotExistInArchive`, `ProjectToRestoreNotFound`,
`BackupCopyToArchive_AskOverwriteConfirmation`,
`RestoreFromArchive_AskOverwriteConfirmation`,
`ConfirmOpenProjectWithConflict`, `ConfirmOpenProjectWithArchiveChanges`.

**BusChord-Lücke.** Kein Projektarchiv, keine Versionen/Hashes, keine
Sperren, kein Kollaborationsmodus, kein Auto-Backup, keine
Archivordner/-verknüpfungen, keine Archivoptimierung. **Empfehlung:**
**native** als eigener Meilenstein; Kollaborationsmodus zunächst
„dokumentiert nicht unterstützt“. **Konfidenz:** belegt (Typen/Methoden/
Konstanten/Hash-Algorithmus), Layoutdetails und UI-Reihenfolge abgeleitet.

### 2.10 Datenbank-Backup und -Restore

**Abgrenzung.** `CreateBackupOperation`/`RestoreBackupOperation` sind nicht das
Projektarchiv. `CreateBackupOperation` delegiert an
`IProjectBackupSupport.CreateBackup(project, comment, stream, UTF8)`,
Kommentar max. `16383` Zeichen, nicht undo-fähig; `ResultStream` liefert das
Backup. `RestoreBackupOperation` nimmt einen Stream und einen existierenden
Zielordner, sucht den Eintrag mit einer bekannten Endung, leitet aus den ersten
6 Zeichen des Eintragsnamens den Zielordnernamen ab, extrahiert in einen
temporären Ordner, verschiebt ihn an das Ziel und räumt bei Fehlern auf
(`RestoreProjectBackupFailedException`). UI: „Datenbankbackup“ /
„Datenbankbackup wiederherstellen“ (`ProgressInformationMakeDatabaseBackup`,
`ProgressInformationRestoreBackup`), Fehler `CloseDatabaseError`.

**BusChord-Lücke.** Nicht vorhanden; für BusChord zunächst nicht kritisch, da
kein eigener Datenbank-/Produktspeicher im ETS-Sinn. **Empfehlung:**
**dokumentiert nicht unterstützt** bzw. später native, wenn ein zentraler
Produktspeicher eingeführt wird. **Konfidenz:** belegt (Operationen), UI-Bezug
abgeleitet.

### 2.11 Verschlüsselte Projekte und Archive

**Format (belegt, Details in `analysis/project-encryption.md`).** Äußeres ZIP;
Projekt in `P-*.zip`; Einträge mit WinZip AES-256 (AE-1), Methode 99,
GP-Flag `0x0801`, AES-Extrafield `0x9901`. ETS-ZIP-Passwort ist nicht das
Nutzerpasswort, sondern
`Base64(PBKDF2-HMAC-SHA256(UTF-16LE(Passwort), "21.project.ets.knx.org",
65536, 32))`. Lesen nur mit Passwort; ohne Passwort kein Schlüssel im Container.

**Typen/Methoden.** `Knx.Ets.ObjectModel.ZipContainerStore.ZipContainerPasswordProtectedProjectStore`
(`Project`-Getter Offset `126328`, `fjMtMaMhO3F` `125724`),
`ZipContainerProjectStore`, `Knx.Ets.Common.Zip.ZipWrapper`,
`EtsZipExtractor.Extract`, `InvalidPasswordError`, `IKeyProvider`,
`AccessKeyManager.CheckAccess`, `ChangeProjectPasswordOperation`,
`ProjectPasswordValidator`, `ProjectHasPasswordValidator`,
`ChangeProjectPasswordSecurityValidator`.

**Sichtbarer Ablauf.** Beim Öffnen/Import eines geschützten Projekts fragt ETS
nach dem Passwort (`EnterPasswordForLocal`/`EnterPasswordForArchive`,
`ImportProjectPasswordInfoText`, `ImportFilePasswordInfoText`). Falsches
Passwort: `MessagePasswordIncorrect`/`TextBlockWrongPassword` („Falsches
Passwort.“). Projektpasswort setzen/ändern/löschen:
`SetProjectPassword`, `ChangeProjectPassword`, `ClearPassword`,
`Label_CannotRemoveProjectPassword` (bei aktiven Sicherheitseinstellungen nicht
entfernbar), `CannotClearProjectPasswordMessage`,
`PasswordCannotBeSetDueToLicense`.

**Daten-/Dateiwirkung.** `ZipContainerPasswordProtectedProjectStore` öffnet den
Projekt-Eintrag (Name aus Projekt-ID), liest ihn in einen `MemoryStream`, legt
einen inneren `ZipWrapper` an, setzt `Password` aus dem `IKeyProvider` und
liest die Projekt-XML (Entry `0.xml`; exakter Name im IL-String verschlüsselt,
aus dem Containerformat abgeleitet). `ProjectArchiveService.HasPassword`
öffnet die Projekt-ZIP, sucht einen Eintrag mit `.signature`-Endung
(Signatur-Endung aus dem Containerformat **abgeleitet**) und prüft
`UsesEncryption`; `IsPasswordMatching` extrahiert mit den Access Keys in einen
Temp-Ordner und liefert bei falschem Passwort `InvalidPasswordError`.
`ChangeProjectPasswordOperation` ändert den Password-Compare-Hash; der genaue
Re-Encrypt-/Schreibpfad ist **unbekannt**.

**BusChord-Lücke.** BusChord liest verschlüsselte ETS6-Projekte (AES) bereits
lesend, verweigert aber das Speichern/Re-Encrypten; kein Passwortsetzen/-ändern,
kein `HasPassword`, keine Projektzugriffsschlüssel. **Empfehlung:**
**approximate** (Lesen mit Passwort, klare Meldung, niemals Klartext
zurückschreiben); Re-Encrypt und Passwortverwaltung später **native**.
**Konfidenz:** belegt (Typen/Format), Entry-Namen/Re-Encrypt **abgeleitet/
unbekannt**.

### 2.12 Projektvergleich

**Sichtbarer Ablauf.** Drei Vergleichskontexte:

1. **Lokales Projekt vs. andere Projektdatei**:
   `CompareProjectVersionsDialogViewModelBase` (`CompareTitle` =
   „Vergleiche {0}“; Buttons `LabelCompareContents` = „Inhalte vergleichen“,
   `LabelCompareVersions` = „Versionen vergleichen“). Der Dialog zeigt
   Fortschritt (`CollectingComparisonInformation`, `Progress_ComparingTopology`,
   `...GroupAddresses`, `...Devices`, `...Trades`, `...Spaces`), danach
   Inhalts- und Historienansicht.
2. **Lokales Projekt vs. Archivversion**:
   `ProjectStatusCompareVersionsDialogViewModel` (Untertitel
   `LabelCompareArchiveSubtitle` = „des lokalen Projekts zum archivierten
   Projekt“), geöffnet aus dem Archiv-/Sync-Kontext. Ergebnis ist auch die
   Entscheidungsgrundlage für Backup/Restore bei Konflikt
   (`ArchiveSyncStates`).
3. **Import vs. lokales Projekt**: `ProjectImportCompareVersionsDialogViewModel`
   (Untertitel `LabelCompareContentSubtitle` = „des zu importierenden Projekts
   mit dem lokalen Projekt“), Footer „Kopie importieren“/„Projekt
   überschreiben“/„Bestätigen“/„Abbrechen“.

**Typen/Methoden.** `Knx.Ets.ProjectCompare.ProjectComparer` (`CompareAsync`
Offset `2220`), `Difference`, `AddedItemDifference`, `DeletedItemDifference`,
`ModifiedItemDifference`, `PropertyValueChange`, Modelle `Model.Xml.*` und
`Model.Ets.*` (Topologie, Gruppe, Geräte, Parameter, ComObjects, Historie),
`ProjectComparisonFactory`, `CompareProjectContentViewModel`,
`CompareProjectHistoryViewModel`, `DifferenceReport` (`CreateReportAsync`,
`SaveReportAsync` Offset `28624`), `XtraDifferenceReport`.

**Daten-/Dateiwirkung.** Liest das andere Projekt (Datei oder Archivversion,
inkl. Passwortpfad) in ein Vergleichsmodell, vergleicht Topologie,
Gruppenadressen, Geräte/Parameter/ComObjects, Gewerke-/Gebäudestruktur und
Historie; schreibt nichts am Projekt. Der Report wird als PDF gespeichert
(`CompareProjectReportFileFilter`). Diff-Labels:
`Report_Title` = „Differenzbericht“, `Report_ProjectNameLabel` = „Projekt:“,
`Report_CompareToLabel` = „Vergleiche mit:“, `ReportLegendAdded` =
„Hinzugefügt“, `ReportLegendModified` = „Geändert“, `ReportLegendDeleted` =
„Gelöscht“.

**Fehler/Edge Cases.** `CompareProjectVersionError` („Fehler beim Vergleich der
Projektversionen: {0}“), falsches Passwort, laufende Vorgänge
(`PendingOperationsPreventExportMessage_*`), unterschiedliche
Gruppenadress-Stile (`Info_DifferentGroupAddressStyles` = „Gruppenadress-
Struktur ist nicht vergleichbar...“), MAP-Parameter/ComObjects nicht
vergleichbar (`Info_ParameterDifferencesForMAPNotSupported`,
`Info_GroupObjectDifferencesForMAPNotSupported`), mehrdeutige Elemente
(`AmbiguousKeyWarning`).

**BusChord-Lücke.** Nur Vergleich zweier bereits geladener Projektdateien
(`ProjectComparer.Compare`), einfache Objektliste, kein Archiv-/Versionsbezug,
kein Passwortpfad, kein PDF-Bericht, keine Historien-/Parameter-/ComObject-
Tiefe. **Empfehlung:** **approximate** (Datei-Diff mit PDF-Export), danach
native Archiv-/Versionsvergleiche. **Konfidenz:** belegt (Typen/Methoden/
Strings), UI-Ablauf abgeleitet.

### 2.13 Dashboard, Projektliste und Suche

**Sichtbarer Ablauf.** Der Dashboard-Tab (`TabDashboard`,
`TabDashboardHeadOverview` = „Übersicht“) ist die Start-/Projektübersicht.
Sidebar: „Lokale Projekte“ (`DashboardSidebar_LocalProjects`), „Archiv“
(`DashboardSidebar_Archive`), „Alle“ (`DashboardSidebar_All`), „Filter“
(`DashboardSidebar_Filter`), „Projekt importieren“, „Projekt exportieren“,
„Neuer Ordner“; Breadcrumbs für lokale/Archiv-Ordner und Archivversionen.
Projektkacheln zeigen u. a. „Geändert:“, „Gesichert:“, „Gesperrt:“,
„Zuletzt synchronisiert:“, Tags, Bild und Zähler (Geräte/Linien/Räume) aus
`LocalProjectInfo`; Kontextaktionen: Details, Umbenennen, Verschieben,
Report, Löschen, Kopieren, Exportieren, Archivaktionen. Umschalter
Kachel-/Listenansicht, Sortierung (u. a. `ByArchivedDate` =
„Archivierungsdatum“), `CountProjectsOpen` = „{0} Projekt(e) offen“.

**Suche/Filter.** Suchfeld „Projekte“ (`Searchbar_Projects`, Platzhalter
`suchen?`), Quick-Search nach Ort (`Alle`, `Lokale Projekte`, `Archiv`,
`Aktueller Ordner`), Tags und „Gesperrt von“. `DashboardSearchInput` enthält
alle rekursiven Projektinfos, aktive Orts-/Tag-/LockedBy-Filter;
`DashboardSearch.CalculateSearchResult` filtert zuerst nach Ort/Tags/LockedBy,
dann nach Suchtokens. Treffer werden case-insensitiv per `Contains` auf
Projektname (`IProjectInfo.Name`), Tag-Labels und LockedBy-Labels geprüft
(`CompareOptions.IgnoreCase`). `SearchTokenizer` zerlegt den Text mit drei
kompilierten Regexen und wirft bei ungültiger Syntax `ParseErrorException`
mit Position; die exakte Token-Grammatik (Anführungszeichen/Phrasen) ist
**unbekannt**, weil die Regex-Strings Dotfuscator-verschlüsselt sind.
Keine Treffer: `NoMatchingProjectsFound` = „Keine passenden Projekte
gefunden.“; Ladefehler: `ProjectListErrorLabel` = „Beim Lesen der Einträge ist
ein Fehler aufgetreten:“.

**Typen/Methoden.** `TabDashboard` (`Refresh` Offset `3131924`, `OpenProject`
`3144676`, `OnArchiveFolderTileClickedAsync`, `NavigateToLocalProjectsDrillDownAsync`,
`NavigateToArchiveProjectsDrillDownAsync`, `NavigateToArchiveVersionsDrillDownAsync`),
`ProjectsViewModel`, `LocalProjectListViewModel`, `ArchiveProjectListViewModel`,
`ArchiveProjectVersionListViewModel`, `ProjectTileViewModelBase`,
`LocalProjectTileViewModel`, `ArchiveProjectTileViewModel`,
`DashboardSearch`, `SearchTokenizer`, `QuickSearchProjectInfoFilterByLocation`,
`DefaultQuickSearchItemFilterStrategy`, `ArchiveFileSystemWatcherManager`.

**Daten-/Dateiwirkung.** Liest lokale Projektinfos aus dem Projekt-Store und
Archivinfos aus dem Archivverzeichnis; beobachtet das Archivdateisystem und
aktualisiert die Liste. Keine Projektänderung durch Suche/Filter; Öffnen/
Verschieben/Löschen/Exportieren wirken auf Store/Archiv.

**BusChord-Lücke.** Nur eine Recents-Liste im Startbereich; keine
Projektkacheln, Tags, Bilder, Zähler, Archivordner, Breadcrumbs,
LockedBy-Filter, Suche oder Sortierung. **Empfehlung:** **native** (setzt
Mehrprojekt-Store und Archiv voraus). **Konfidenz:** belegt (Typen/Methoden/
Strings), UI-Details abgeleitet.

### 2.14 Projekteigenschaften

**Sichtbarer Ablauf.** „Projektdetails“ (`ProjectDetails`,
`MenuItemProjectDetails` = „Projektdetails“), erreichbar über die
Projektkachel (`ProjectTile_Details` = „Details“). Tabs:
„Details“ (`ProjectDetails_Details_Title`), „Historie“
(`ProjectDetails_History_Title`), „Anhänge“
(`ProjectDetails_Attachments_Title`), „Sicherheit“
(`ProjectDetails_Security_Title`), „Übersicht“ (Overview). Angezeigt/editiert:
Projektname, Beschreibung (`Description` = „Beschreibung“), Kommentar
(`Comment`), Projekt-ID (`PropProjectId`), Projekt-Typ (`PropProjectType`),
Projektnummer (`PropProjectNumber`), Projektstart/-ende (`PropProjectStart`,
`PropProjectEnd`), Projektpasswort (`PropProjectPassword`), Fortschritt/
Completion Status, Tags, Projektbild (`SetProjectImage` = „Projektbild
wählen“), Archiv-Pfad (`ProjectDetailsDialog_Overview_ArchiveLocation`),
Geräte-/Linien-/Raumzähler, Archivzugehörigkeit. Details-Seite: Ändern des
Gruppenadress-Stils (`ChangeGroupAddressStyleViewModel`) und
Projektgrößen-Schätzung (`ProjectSizeEstimationViewModel`). Historie:
Projektlog/Trace, Einträge hinzufügen/löschen, Trace drucken
(`PrintProjectTraceCommandHandler`), Tracing aktivieren. Anhänge:
UserFiles/Anhänge hinzufügen/bearbeiten/löschen. Sicherheit: Passwort, BCU-Key
(`ProjectDetails_BcuKeyButton_ProjectNotOpen` = „Der BCU-Schlüssel kann nur
gesetzt werden, wenn das Projekt geöffnet ist.“), Gerätezertifikate.

**Typen/Methoden.** `LocalProjectDetailsDialog.ProjectDetailsDialogViewModel`,
`OverviewPage.ProjectDetailsOverviewViewModel`, `DetailsPage.*`,
`HistoryPage.*`, `AttachmentsPage.*`, `SecurityPage.*`,
`ChangePasswordViewModel`, `ChangeProjectPasswordOperation`,
`ChangeProjectType`, `ChangeProjectGuid`, `RenameProject`,
`Project.ChangeGroupAddressStyle`.

**Daten-/Dateiwirkung.** Änderungen am Projektobjekt/Store, Projektbild,
Tags, Historie, Anhänge; Passwort-/Security-Änderungen können Re-Encrypt/
Security-Impacts auslösen. Speichern über den normalen PersistenceService.

**Fehler/Edge Cases.** `DescriptionGreaterThan` (ETS3-Plugin-Warnung bei zu
langer Beschreibung), `ProjectNameAlreadyExists`, `ProjectName` leer,
`PasswordCannotBeSetDueToLicense`, `Label_CannotRemoveProjectPassword`,
`ProjectDetails_BcuKeyButton_ProjectNotOpen`, `DetailsCannotBeRead`.

**BusChord-Lücke.** Nur Name/Kommentar/Start (Modell); keine Typen, Nummer,
Completion, Tags, Bild, Historie, Anhänge, Sicherheit/BCU-Key, Archivpfad,
Stilwechsel-UI. **Empfehlung:** **approximate** (Metadaten erweitern), dann
native Detaildialog. **Konfidenz:** belegt (Typen/Strings), Feldverfügbarkeit
aus `LocalProjectInfo` belegt, UI-Layout abgeleitet.

### 2.15 Mehrere Projekte, Fenster und Instanzen

**Sichtbarer Ablauf.** ETS6 kann mehrere Projekte gleichzeitig öffnen. Der
`MainWindowViewModel` hält `Tabs` (Dashboard + Projekt-Tabs); jedes Projekt
hat einen eigenen DI-Scope (`ProjectScopeLifetimeManager`,
`ProjectScopeModule`) und einen `TabProject`. „Neues Fenster“ (`NewWindow`,
`SettingsShortcutsTooltipNewWindow` = „Fügt ein neues Fenster hinzu“) öffnet
ein weiteres Hauptfenster (`ApplicationViewModel.OpenNewWindow`), jeweils mit
eigenen Tabs/Workspace-Einstellungen. Ein zweiter ETS-Prozess wird über
`ISingleInstanceApplicationWatcher` erkannt (`AnotherInstanceConnectedEventArgs`);
Kommandozeilenoptionen werden an die laufende Instanz übergeben. Maximal
8 Panels pro Projekt (`MoreThanEightPanelsNotAllowed` = „Mehr als 8 Panels
können pro Projekt nicht geöffnet werden.“). CLI (`StartupOptions`):
`--openProject`, `--createProject`, `--wizard`, `--deleteProject`,
`--existsProject`, `--importProject`, `--password`, `--groupmon`, `--busmon`,
`--installApp`, `--quit` (Kurzbefehle `-o/-c/-w/-d/-e/-i/-p/-g/-b/-a/-q`).

**Typen/Methoden.** `ApplicationViewModel.StartUp/OpenFirstWindow/OpenNewWindow`,
`MainWindowViewModel.AddTab/RemoveTab/CloseTab/GetNewProjectTabContent`,
`TabService`, `TabProject`, `ProjectScopeLifetimeManager`, `StartupOptions`,
`ISingleInstanceApplicationWatcher`.

**Daten-/Dateiwirkung.** Ein Projekt-Store/Projektobjekt pro Tab; Fenster
teilen Anwendung, aber nicht Projekt-Scope. CLI kann Projekte anlegen/öffnen/
löschen/importieren und danach beenden.

**Fehler/Edge Cases.** Schließen des letzten Fensters beendet ETS;
`MessageDownloadsRunningQuitAnyway`; blockierte Tabs; Lizenz-/Gerätezahlen.

**BusChord-Lücke.** Ein `Project?` pro `MainWindowViewModel`, keine Tabs,
keine mehreren Fenster, keine Instanzweiterleitung, keine CLI. **Empfehlung:**
**native** (Mehrprojekt-Tabs + Fenster), CLI später. **Konfidenz:** belegt.

### 2.16 Demo-Modus und Reset

**Demo.** ETS6 kann ohne gültige Lizenz im Demo-Modus laufen:
`Ets6DemoLicenseName` = „ETS6 Demo“, `RunDemo` = „Demo starten“,
`PreNotificationLicensingProblemsText` („... Lassen Sie die ETS im Demo-Modus
ausführen.“), `DemoLicenseInfoText` = „Demo“, `AppsNotInDemoMode` = „Apps sind
im Demo Modus nicht verfügbar.“, `MaximumExceedsBecauseOfDemoLicense` =
„Aufgrund der installierten Lizenz kann die Operation nicht ausgeführt
werden.“ Ein Demo-Projekt oder Demo-Datenreset ist nicht belegt; „Demo“
bezeichnet den Lizenzmodus. Gerätegrenzen aus `KNX.package`: Lite 20, Home 64,
Professional unbegrenzt; Projektzahl unbegrenzt.

**Reset.** Global: „Benutzereinstellungen zurücksetzen“
(`SettingsResetUserSettings`), Bestätigung `ResetSettingsConfirmation`
(„Nachdem die Einstellungen zurückgesetzt wurden, wird die ETS beendet.
Möchten Sie fortfahren?“), `SetupHelpClearUserSettings` (setzt auch
Projekt-/Datenbankspeicherpfad zurück); „Standard Arbeitsbereich
wiederherstellen“ (`RestoreDefaultWorkspace`); „Alle Benachrichtigungen
zurücksetzen?“ (`MessageResetNotifications`); „Modale Fenster zurücksetzen“
(`ResetModalWindows`); Tastaturkürzel-Reset
(`ResetShortcutSettingsMessage`); Produktspeicher löschen
(`SettingsClearProductStore` = „Produktspeicher löschen“). Projektbezogen:
`ResetSplit` („Teilung zurücksetzen“), `ResetParameter` („Parameter
zurücksetzen“); diese sind keine globalen Resets.

**BusChord-Lücke.** Kein Lizenz-/Demo-Modus (für BusChord nicht nachzubauen),
kein globaler Settings-/Workspace-Reset, keine Notification-/Modal-Reset-
Verwaltung. **Empfehlung:** Demo **dokumentiert nicht unterstützt**;
Einstellungsreset **native/approximate** (nur eigene Settings). **Konfidenz:**
abgeleitet (Strings/Typen), keine UI-Ausführung.

## 3. Evidence-Register

### 3.1 Artefaktidentität

| Artefakt | SHA-256 | MVID | Evidence |
|---|---|---|---|
| `Knx.Ets.ViewModel.dll` | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` | `b557be5c-0e7f-4bc3-aa94-958fba394aba` | `ev_8e5d5386a5e0468631e2d39bc82bb8b20fe9ee5d904b30515bb9336e94e4de7e` |
| `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` | `484ff6a5-f1b3-4f33-ad19-2dd22dc9ae04` | `ev_f2a1befdb5c100bc51a00161598f36789fe1e764b36034ec9a339d23f0cd180d` |
| `Knx.Ets.ProjectArchive.dll` | `43d2a1bb74d78e12a93c260ee28ff6facffe9f6b48d79b6b87bf136fe84bc51f` | `a1537d0c-51f9-43ad-aa0f-82a3aa5c45dd` | `ev_505eba14e99210cac2f1c4641a8f93491509745680c46285ad77bccfcf53e774` |
| `Knx.Ets.ProjectCompare.dll` | `c63c762dee443a89bf9413e7ec51ddd905ef0e84ca7b55e51814536380ca04d6` | `8ab6dbed-eb2e-4ff8-bce9-bccbd07a3e9e` | `ev_847a3cc12c0aaa246e0a8b4820b696bab3968472c196c5299fe7734c419cf512` |
| `Knx.Ets.Reports.dll` | `0ae42d7cbe2cba6689626fcad6deb58a7e9dc5418f218af323872176445e0d82` | `40007921-4858-4214-8f53-e0468be12fde` | `ev_bb8df56e20ff0cc214ab2bb1eea8f2d1a20e64205c8e009eea6a013ef3b407ed` |
| `Knx.Ets.PrintLibrary.dll` | `d6f133c970656be824cf36ff13e8d4209ea0b349f737e100a7823a475ce4485d` | `fcdfefc8-950f-4b27-9b22-e7f8b9e57a3f` | `ev_b6d4ac5522ac37cbad5154f5833cf2174fc9d16e311001f14a6b203a23a121b8` |
| `Knx.Ets.Common.dll` | `4d4fcd3b0aed672979aa45b4642a5af772e6dcf0b0388062a96d8ee992f86ecd` | `631c8fe5-10ad-46a3-b654-49891e3c2e80` | `ev_db9d33ec533b485f683dd7639235bacf3d2f85bf6180cb1a7d7e5b7351916d7b` |
| `Knx.Ets.Ui.UserControls.dll` | `2488a5a951e772af6530ecdab351f2d8b1cf9fbfd079b13ec56c0b91efbbdf8a` | `85ede4c8-af1c-4751-b839-cf969c90ddac` | `ev_f8160644359d529fde738db7d5ceb5bb71a2a4a024751387f45b36835ec8af11` |

### 3.2 Typ-/Methoden-Evidence

| Bereich | Evidence | Inhalt |
|---|---|---|
| ProjectArchive gesamt | `ev_46253ca3e7015d3beab4f19b57f23271d2671d18bed51c5e2c44bfa6d1504ab6` (Parent `ev_717c8478c2662ce04059319288f523c1c341c21337c1d1654815ed164b56ec83`) | 200 Typen, 954 Felder, 1324 Methoden, 936 MemberRefs, 4375 CallEdges, 18 UserStrings |
| TabDashboard | `ev_03e68fd7f179d6e42ef4167a4d2a5b26d8c91e853606d22463fdab5172f99d67` (Parent `ev_125ee9d69ba81cbc03bf789c7ef26bbb7fe00cfbf0712ef5e455238bbba43814`) | 80 Felder, 253 Methoden |
| ProjectService | `ev_f68aa3df349d2c2600c4b6ff189afcf0c4fa074b89dba1be2d990f68ab9ea993` (Parent `ev_7530b575378d001f89d3c664c4376f29a810525df3be1cdeffa563e5c2278123`) | Lifecycle-API |
| PersistenceService | `ev_9c9b52f58487bc1c267efc5c913202ee0f16e4c4fbddfd72f0ecef6914aea68f` (Parent `ev_8f9eef6862d7d329e17870b43a9611550302b63277cc63c768ddfed941883d26`) | Auto-Save |
| ProjectCreationWizard | `ev_ccf9968cb1354a5ffb919fbbf873d02358ac23ccf5b1b2298ceb6cd7cc1db5db` (Parent `ev_f5b04bc67315392ccb191ad2bfbcc75b52ca52e9944a23874fef6f5671fe1d17`) | Wizard |
| ReplaceProductWizard | `ev_c8457d6668e194a8dcd132389491589bc5bc37cb4813986ea26b026c2b32e4cb` (Parent `ev_1a8e2c823e05b0c2ef8b6f64ae34dee893d8a99ab17b05665dfe1539d0de2719`) | Wizard/Mapping |
| Compare-Basis | `ev_eaf647cf9ed4eab49aaa81b270f16805e141d1ec1170c018af823c1b7c5e0ff1` (Parent `ev_c4498397fd9b6e9317d3d49e814d77f06b579c0ad92dcfc02231c97f6bc1e38d`) | Dialog/Compare |
| ProjectDetailsOverview | `ev_16d64477b64030eec90c3f5d7fd22f581494c3772cee23d41d39e45b061e2fdb` (Parent `ev_0445b18326008266f84ad13068cb5dc1c95db9beec5d278884f7f0ae654887b8`) | Properties |
| ChangePassword | `ev_507d54918d708a31c9d3e90148f951d0570066c3337b2e61d9c93d513f64123d` (Parent `ev_d20064fe26d7a08d0d3b3968dedb14ff04645caadbdeac72c33bc21f21f91573`) | Passwort |
| CreateBackupOperation | `ev_8c8823e073b38aac3d147f2fd6168c91fb3ed41e07d88a42da3e51e5dd8eff92` (Parent `ev_ee117da02f98e77490c2541f5f25f69626999e4c279a6340906ff97fe82e20a8`) | DB-Backup |
| RestoreBackupOperation | `ev_86b68aab86960a6f7ddee72e6f13ab6519105e762ede8a84b06eb1bbd9963e82` (Parent `ev_f6866e210a132f63688ed83ad8221754a9f295e427d76aa10e9cc607114a7c06`) | DB-Restore |
| CreateRestorePointOperation | `ev_c11c90b491e57fae602ec3166eab760e6b6cf62d1b3909624153d04392855cd7` (Parent `ev_da2a308e108634b71fce926871c28fafb9511edc74fdd883b025499728210998`) | Recovery Point |
| ArchiveManager | `ev_4b4871c9df91e21008a7a8509db9fc52fc95bb0433b54343da8a37c244a50d02` (Parent `ev_79a281b1aabda2b6388c54e9307872144d5724657a88655e388ce2e93e774cb3`) | 41 Methoden |
| ArchivePersistence | `ev_6bb2b93a4df667e0260dface2ae63f1d2ecfeba5b6a5299d5744119346ebbae8` (Parent `ev_acd40108f6d18a5099abbdcfdcb87bb780311b5350cd9e525394d14d0c53c1fa`) | Auto-Backup |
| BackupDialog | `ev_02d54771e4314350ae4db8ce92add3c6ee42eae436822a618034b3a77d3d217a` (Parent `ev_051b7d247729eeebaf1a0a85efe9fc7b4762d5c5ff0749e5a9ab23092a7f490d`) | 29 Methoden |
| RestoreDialog | `ev_878643157a1b20dd69de208ed9706d296832934c4bfdf341c0efeef0fd37ebef` (Parent `ev_f1a88df2a6c16848cebd73aafdcc7a28714eb5f788e67c2818a1312d7975c266`) | 12 Methoden |
| Project | `ev_ccc47a5582fa8908fd8aa576dcd12337452ead48a419e11c7b188862bbdbe348` (Parent `ev_a5bbd0d8e733fcf093c958821b2572b4182e9af6b7338195bcd6c6d029bfeb56`) | 198 Methoden |
| PasswordStore | `ev_2e1619844c28a71eb7ae530c1001ca556cbe23a341634e813880c7d3b239f1cb` (Parent `ev_3c057e25b729e3083998085081869ef9c9b96549b68593558eca28795d161448`) | 10 Methoden |
| ProjectComparer | `ev_80c78a4dc8d7465c60a198fc8fc1362bed0518e36a056df3d9a6baad22447c4c` (Parent `ev_89691d274273161243d713302554924f5e9a77dd6dc8349004f5c7916b247763`) | Compare-API |
| DifferenceReport | `ev_06abea54666ca73cbbff64b255a003e3e9ab93069f939a105f96ed41d310b62d` (Parent `ev_5935f8cf5b5dd2b412332da549c6a285cf7dcfc37a7241b35c64bd4caa066f5f`) | PDF-Report |
| ReportVM | `ev_5a9d5237a0e9d3ade2cc0c03a7dd6143343270206427292c36ae09bc84a15d1d` (Parent `ev_d58019f05d39a25c379a238ff122a7f4bccccb7e7b5de0477b7c987d3270685d`) | Export/Print |
| ObjectModel 5 Typen | `ev_9b5be8ed581c2828cbf7a2bea792979e0a66960a93437f7217410a314a2180ba` | Backup/Restore/Recovery/Password/Replace |

### 3.3 Wichtige Methoden-Offsets

Datei-Offsets sind PE-Dateioffsets der Methodenbodies aus
`inspect_managed_members` (`body.file_offset`), keine RVA.

| Methode | Datei-Offset | Evidence |
|---|---:|---|
| `ProjectService.OpenProject` | `591104` | `ev_f68aa3df...` |
| `ProjectService.ImportProject` | `594712` | `ev_f68aa3df...` |
| `ProjectService.ExportProjects` | `596744` | `ev_f68aa3df...` |
| `ProjectService.CreateNewProject` | `598752` | `ev_f68aa3df...` |
| `ProjectService.CloseProject` | `599056` | `ev_f68aa3df...` |
| `PersistenceService.Start` | `564496` | `ev_9c9b52f5...` |
| `PersistenceService.SaveNow` | `564652` | `ev_9c9b52f5...` |
| `TabDashboard.Refresh` | `3131924` | `ev_03e68fd7...` |
| `TabDashboard.OpenProject` | `3144676` | `ev_03e68fd7...` |
| `ProjectArchiveService.SendToArchiveAsync` | `30888` | `ev_46253ca3...` |
| `ProjectArchiveService.HasPassword` | `34308` | `ev_46253ca3...` |
| `ProjectArchiveService.IsPasswordMatching` | `34984` | `ev_46253ca3...` |
| `ProjectArchiveService.FindProjectFiles` | `37932` | `ev_46253ca3...` |
| `ProjectArchiveService.OptimizeArchiveAsync` | `40064` | `ev_46253ca3...` |
| `ArchiveHashService.CalculateArchiveHash` | `16504` | `ev_46253ca3...` |
| `ProjectArchiveServiceManager.SendProjectToArchiveAsync` | `898732` | `ev_4b4871c9...` |
| `ProjectArchiveServiceManager.GetProjectFromArchiveAsync` | `900412` | `ev_4b4871c9...` |
| `ProjectArchiveServiceManager.ExportArchiveAsync` | `901148` | `ev_4b4871c9...` |
| `CreateBackupOperation.SpecializedExecute` | `4058460` | `ev_8c8823e0...` |
| `RestoreBackupOperation.SpecializedExecute` | `4060184` | `ev_86b68aab...` |
| `CreateRestorePointOperation.SpecializedExecute` | `1976980` | `ev_c11c90b4...` |
| `Project.ValidateProjectIntegrity` | `966424` | `ev_ccc47a55...` |
| `Project.KeepAccessKey` | `968148` | `ev_ccc47a55...` |
| `ZipContainerPasswordProtectedProjectStore.get_Project` | `126328` | `ev_2e161984...` |
| `ZipContainerPasswordProtectedProjectStore.fjMtMaMhO3F` | `125724` | `ev_2e161984...` |
| `CompareProjectVersionsDialogViewModelBase.ShowCompareContentCommand` | `833848` | `ev_eaf647cf...` |
| `ReplaceProductWizardViewModel.ExecuteFinal` | `2768928` | `ev_c8457d66...` |
| `ReplaceProductWizardViewModel.MapCommunicationObject` | `2768576` | `ev_c8457d66...` |
| `ChangePasswordViewModel.SetUpCommands` | `3917544` | `ev_507d5491...` |
| `ProjectComparer.CompareAsync` | `2220` | `ev_80c78a4d...` |
| `DifferenceReport.SaveReportAsync` | `28624` | `ev_06abea54...` |
| `ReportVM.get_ExportCommand` | `35672` | `ev_5a9d5237...` |

### 3.4 Satellitenressourcen (de-DE)

| Datei | SHA-256 | Evidence |
|---|---|---|
| `de-DE\Knx.Ets.ViewModel.resources.dll` | `0cf768f725f522c21db9e35b528ea828c76316495c9938dfe30f715c7289a2fc` | `ev_768dbbc2425583658ec596fd91cd3f2c8d66c46cce80ee086dae456fac16e0d8` |
| `de-DE\Knx.Ets.ProjectArchive.resources.dll` | `0b6f1df752586e1432d605c4a0e0eac8e602e5392eef88d88900a4943317c259` | `ev_1c6fba9f1fa7050b21a89ad8b3110aa7bacc7f13c955d2f455e9c2bcae05fcc7` |
| `de-DE\Knx.Ets.ProjectCompare.resources.dll` | `dff4c1e571fb95a1614d6b5ae13ee8e4720bb0f18755401fea8b1a863ede5e5a7` | `ev_20ba01b1be873da2e21b71b5c4d239914eb795081f8717962fa4c2372c8b7418` |
| `de-DE\Knx.Ets.Localization.resources.dll` | `a9742abcc4ef15ac5bd64a398c77f1abc153a39a50e4e00eb498ab42bb795dc6` | `ev_4d2e4f9608fa26b785d09f9a6e3746c4dae1b771ef4bc41c4da3877307ca8259` |
| `de-DE\Knx.Ets.Ui.UserControls.resources.dll` | `cf1062b11c2733185f81b35e9a37e3441f0c348511cfe5ce703a7e35830a87ae` | `ev_f7afe7f68f667f658e88bea932c8daae9aa8e1c6f0f9cd06c98640e06bc549dd` |
| `de-DE\Knx.Ets.ObjectModel.resources.dll` | `ae2852ca5d7bca99b8a2143369e4ddb022df71686be223b47dcaf96ff407ce1d` | `ev_65838a551fe0721ad714381e714e48aaed622ddf7bc1960ff3c2e0828d2b8e86` |

## 4. Exakte Ressourcenstrings

Aus den de-DE-Satelliten (Werte unverändert, inkl. Platzhalter).

### 4.1 Projekt-Lebenszyklus

| Key | Wert |
|---|---|
| `NewProject` / `DashboardNewProject` | `Neues Projekt` |
| `CreateProject` | `Projekt erstellen` |
| `ProjectCreationWizard` | `Starte Projekt-Wizard` |
| `JumpTaskCreateProjectAssistant` | `Projekt Wizard` |
| `OpenProject` | `Projekt öffnen` |
| `OpenProjectFile` | `Projektdatei öffnen` |
| `Save` | `Speichern` |
| `SaveTag` | `Speichern` |
| `CloseProject` | `Projekt schließen` |
| `AskToSaveChanges` | `Wollen Sie die Änderungen speichern?` |
| `ContinueCloseProjectConfirmation` | `Das Projekt '{0}' muss vor der Ausführung geschlossen werden. Möchten Sie fortfahren?` |
| `MessageProjectNotClosing` | `Der Vorgang wurde abgebrochen, da ein Projekt nicht geschlossen werden konnte.` |
| `ItIsTheLastProject` | `Das letzte Projekt kann nicht geschlossen werden!` |
| `MessageDownloadsRunningQuitAnyway` | `Es stehen noch laufende Operationen an. Soll die ETS trotzdem geschlossen werden? Wenn Sie die ETS schließen, werden alle noch laufenden Operationen abgebrochen.` |
| `CountProjectsOpen` | `{0} Projekt(e) offen` |
| `NewWindow` | `Neues Fenster` |
| `MoreThanEightPanelsNotAllowed` | `Mehr als 8 Panels können pro Projekt nicht geöffnet werden.` |
| `NewProjectFailed` | `Neues Projekt konnte nicht erzeugt werden.` |
| `ProjectWithNameExists` | `Ein Projekt mit dem Namen '{0}' existiert bereits im Projektspeicher.` |
| `ProjectCreationTemplateWizardTheProjectNameCanNotBeEmpty` | `Der Projektname kann nicht leer sein.` |

### 4.2 Dashboard / Suche / Tiles

| Key | Wert |
|---|---|
| `TabDashboardHeadOverview` | `Übersicht` |
| `DashboardSidebar_LocalProjects` | `Lokale Projekte` |
| `DashboardSidebar_Archive` | `Archiv` |
| `DashboardSidebar_All` | `Alle` |
| `DashboardSidebar_Filter` | `Filter` |
| `DashboardSidebar_ImportProject` | `Projekt importieren` |
| `DashboardSidebar_ExportProject` | `Projekt exportieren` |
| `DashboardSidebar_NewFolder` | `Neuer Ordner` |
| `Searchbar_Projects` | `Projekte` |
| `Searchbar_Questionmark` | `suchen?` |
| `NoMatchingProjectsFound` | `Keine passenden Projekte gefunden.` |
| `DasboardQuickSearchAllProjects` | `Alle` |
| `DasboardQuickSearchLocalProjects` | `Lokale Projekte` |
| `DasboardQuickSearchArchiveProjects` | `Archiv` |
| `DasboardQuickSearchArchiveCurrentFolderProjects` | `Aktueller Ordner` |
| `ProjectTile_Details` | `Details` |
| `ProjectTile_Rename` | `Umbenennen` |
| `ProjectTile_Move` | `Verschieben` |
| `ProjectTile_Report` | `Report` |
| `ProjectTile_Modified` | `Geändert:` |
| `ProjectTile_Archived` | `Gesichert:` |
| `ProjectTile_Locked` | `Gesperrt:` |
| `ProjectTile_LastSync` | `Zuletzt synchronisiert:` |
| `DashboardTabHeaderArchiveVersions` | `{0} Versionen` |
| `ByArchivedDate` | `Archivierungsdatum` |
| `ProjectListErrorLabel` | `Beim Lesen der Einträge ist ein Fehler aufgetreten:` |

### 4.3 Archiv / Backup / Restore / Recovery

| Key | Wert |
|---|---|
| `ArchiveProjects` | `Archivprojekte` |
| `Archives` | `Archive` |
| `BackupToArchive` | `Sicherung im Archiv erstellen` |
| `CheckedIn` | `Sicherung im Archiv erstellen` |
| `CheckInProject` | `Check-In Projekt` |
| `CheckinDialog` | `Datensicherung` |
| `RestoreFromArchive` | `Aus dem Archiv wiederherstellen` |
| `CheckedOut` | `Aus dem Archiv wiederherstellen` |
| `CheckedOutForced` | `Aus dem Archiv wiederherstellen (Forciert)` |
| `Restore` | `Wiederherstellen` |
| `RestoreVersion` | `Version wiederherstellen` |
| `BackupCopyToArchive` | `Backup ins Archiv kopieren` |
| `BackupCopy` | `Backup-Kopie` |
| `Archive_Lock` | `Sperren` |
| `Archive_Unlock` | `Entsperren` |
| `NumberOfRetainedProjectVersions` | `Anzahl der automatisch gespeicherten Projektversionen` |
| `ArchiveVersionsMaximumNumberToolTip` | `Legt die maximale Anzahl automatischer Projektversionen im Archiv fest. Wenn das Maximum erreicht ist, wird die älteste Version beim Erstellen der nächsten Sicherung entfernt. Zusätzlich können Sie Versionen anheften, damit diese nicht automatisch entfernt werden.` |
| `OptimizeArchiveTooltip` | `Konvertieren Sie Ihr Archiv in ein neues, optimiertes Archivformat` |
| `OptimizeArchiveDescription` | `Ab ETS 6.3 unterstützt die ETS ein neues, optimiertes Archivformat. Dies kann geringfügige Nebenwirkungen haben, wenn das Projektarchiv mit einer ETS-Version verwendet wird, die älter als 6.3 ist. Alle Projekte werden geschlossen und die ETS wird neu gestartet, sobald die Optimierung abgeschlossen ist.` |
| `RecoveryPointOptionsToolTip` | `"Ja" erzeugt beim Schließen eines Projekts einen Wiederherstellungspunkt, "Nein" erzeugt keine Wiederherstellungspunkte, und "Beim Projekt Schließen fragen" öffnet beim Schließen einen Dialog, mit dem ein Wiederherstellungspunkt erzeugt werden kann.` |
| `ProjectRecovery` | `Projektwiederherstellung` |
| `ProjectRecoveryQuestion` | `Möchten Sie für das Projekt '{0}' den Stand vom '{1:G}' wiederherstellen?  Diese Operation kann nicht rückgängig gemacht werden.` |
| `RestoringProjectFromRecoveryPointLabel` | `Stelle Projekt wieder her '{0}' vom Wiederherstellungspunkt um '{1}'.` |
| `RestoreMissingRecoveryPoints` | `Noch kein archiviertes Projekt oder Wiederherstellungspunkt verfügbar.` |
| `RestorePointCannotBeRestored` | `Wiederherstellungspunkt kann nicht wiederhergestellt werden` |
| `ArchiveDirectoryNameNotAllowed` | `Ein Ordner im Archiv darf nicht auf '.archive' enden, da dieser intern verwendet wird, um Daten im Zusammenhang mit Projekten im Archiv zu organisieren.` |
| `ProjectAlreadyLocked` | `Das Projekt '{0}' wurde bereits vom Benutzer {1} auf {2} zum Zeitpunkt {3} gesperrt.` |
| `ProjectLockedBy` | `Das Projekt '{0}' wurde vom Benutzer {1} auf Maschine {2} zum Zeitpunkt {3} gesperrt.` |
| `ProjectGuidNotFoundInArchive` | `Die Projekt  Guid '{0}' wurde nicht im Archiv-Verzeichnis gefunden '{1}'.` |
| `ProjectArchiveInvalidPath` | `Der Archivpfad '{0}' ist kein gültiges Archiv.` |
| `ConfirmOpenProjectWithConflict` | `Das zu öffnende Projekt befindet sich im Konflikt, d.h. es wurde sowohl lokal als auch im Archiv geändert.  Das Bearbeiten des lokalen Projekts wird die Lösung der Konflikte erschweren. Es wird empfohlen, die Konflikte zuerst zu lösen und das Projekt danach weiter zu bearbeiten.` |
| `RestoreFromArchive_AskOverwriteConfirmation` | `Möchten Sie wirklich Ihr lokales Projekt mit der Version aus dem Archiv überschreiben und so alle Änderungen an Ihrem lokalen Projekt verwerfen?` |

### 4.4 Import / Export

| Key | Wert |
|---|---|
| `ImportProject` | `Projekt importieren` |
| `ImportProjectSucceeded` | `Projekt '{0}' erfolgreich importiert` |
| `ImportProjectFailed` | `Import fehlgeschlagen` |
| `ExportProject` | `Projekt exportieren` |
| `ExportProjectFile` | `Projektdatei exportieren` |
| `ExportFailed` | `Export fehlgeschlagen.` |
| `ExportError` | `Export '{0}' in Datei '{1}' fehlgeschlagen.` |
| `ResultPageStatusProjectExportFailed` | `Projekt-Export fehlgeschlagen: {0}` |
| `NamedProjectsExportNotAllowed` | `Diese Projekte können nicht mit den installierten Lizenzen exportiert werden:  {0}` |
| `PendingOperationsPreventExportMessage_singular` | `Es gibt laufende Operationen für das Projekt {0}, für das ein Export gestartet werden soll.  Um das Projekt exportieren zu können, müssen Sie warten bis alle laufenden Operationen abgeschlossen wurden.` |
| `FileCreatedWithNewerSchema` | `Die Datei wurde mit {0} erzeugt. Sie kann nicht in diese Version importiert werden.` |
| `InvalidSchemaVersion` | `Import der Datei '{0}' fehlgeschlagen. Die Schema-Version ist ungültig.` |
| `MessageProjectFileTooLarge` | `Die gewählte Datei ist zu groß! Eine Projektdatei darf nicht größer als {0} MB sein.` |
| `ImportGuidConflictDialogText` | `Ein Projekt mit der gleichen Kennung wurde im Projektspeicher gefunden.  Sie können eine Kopie des Projekts importieren oder das bestehende Projekt überschreiben.  Bitte beachten Sie, dass das Überschreiben des Projekts nicht rückgängig gemacht werden kann.` |
| `ConvertETS5ArchiveProjects_TooltipText` | `Lässt Sie ein oder mehrere ETS5 *.knxprojarchive Dateien aus Ihrem ETS5-Archivverzeichnis wählen; Konvertiert und verschiebt ausgewählte Projekte in das Stammverzeichnis des ETS6-Archivs.  Nach der Konvertierung sind Projekte nicht mehr zur Bearbeitung in der ETS5 verfügbar.` |
| `SetupHelpIncludeRelevantDcas` | `Im Projekt verwendete und installierte DCAs als Projektdateien im Projektexport einbinden` |
| `SetupHelpCatalogInformation` | `Wenn aktiviert, enthält ein exportiertes Projekt Kataloginformationen` |
| `SetupHelpIncludeCompatibleApplicationPrograms` | `Wenn aktiviert, enthält ein exportiertes Projekt kompatible Applikationsprogramme` |
| `SetupHelpShrinkProjectExport` | `Entfernen von unnötigen Produktdaten aus dem Projekt, um die Dateigröße des Exports zu reduzieren.` |

### 4.5 Reports / Druck

| Key | Wert |
|---|---|
| `Reports` / `MenuItemReports` | `Reports` |
| `SettingsDialog` | `Report Einstellungen` |
| `GeneratingReport` | `Erzeuge Bericht` |
| `Print` | `Drucken` |
| `PrintingFailed` | `Beim Erstellen des Dokuments ist ein Fehler aufgetreten. Weitere Details finden Sie in der Log-Datei.` |
| `PrintLabels` / `PrintLabel` | `Etiketten drucken` |
| `PrintDate` | `Druckdatum: ` |
| `ReportsFontTextBlock` | `Die Reports sind optimiert für die Segoe UI Schrift. Wenn Sie die Schriftart ändern, ist das Layout möglicherweise nicht optimal und einige Zeichen werden möglicherweise nicht korrekt angezeigt` |
| `SetupHelpReportLogo` | `Gibt die Bilddatei an, die als benutzerdefiniertes Logo in Reports verwendet wird. Wenn das Bild nicht quadratisch ist, kann es verzerrt dargestellt werden` |
| `ProjectTile_Report` | `Report` |
| `DiagnosticWizard_PrintFailed` | `Das Drucken des Exports ist fehlgeschlagen, keine Seiten wurden zum Drucker gesendet.` |

### 4.6 Vergleich / Produktersatz / Passwort

| Key | Wert |
|---|---|
| `Compare` | `Vergleichen` |
| `CompareTitle` | `Vergleiche {0}` |
| `LabelCompareVersions` | `Versionen vergleichen` |
| `LabelCompareContents` | `Inhalte vergleichen` |
| `LabelCompareArchiveSubtitle` | `des lokalen Projekts zum archivierten Projekt` |
| `LabelCompareContentSubtitle` | `des zu importierenden Projekts mit dem lokalen Projekt` |
| `CompareProjectVersionError` | `Fehler beim Vergleich der Projektversionen: {0}` |
| `CompareProjectReportFileFilter` | `PDF-Dateien (*.pdf)\|*.pdf\|Alle Dateien (*.*)\|*.*` |
| `Report_Title` | `Differenzbericht` |
| `Report_ProjectNameLabel` | `Projekt:` |
| `Report_CompareToLabel` | `Vergleiche mit:` |
| `ReportLegendAdded` | `Hinzugefügt` |
| `ReportLegendModified` | `Geändert` |
| `ReportLegendDeleted` | `Gelöscht` |
| `ReplaceProductWizardTitle` | `Produkt ersetzen` |
| `ReplaceProductWelcome` | `Start` |
| `ReplaceProductSelectTarget` | `Ersatzprodukt auswählen` |
| `ReplaceProductAssignComObjects` | `Objekte zuweisen` |
| `ReplaceProductWizardTargetPageTitle` | `Wählen Sie die zu verwendende Vorlage` |
| `ReplaceProductWizardMappingPageTitle` | `Zuordnungen` |
| `ReplaceProductWizardMissingMappings` | `Nicht alle Kommunikationsobjekte sind zugeordnet. Daher werden bei einer Ersetzung Gruppenadresszuordnungen verloren gehen. Bitte spezifizieren Sie ein Zielkommunikationsobjekt für jedes Quellkommunikationsobjekt.` |
| `ReplaceProductWizardDuplicateMappings` | `Mehrere Quellkommunikationsobjekte sind dem gleichen Zielkommunikationsobjekt zugeordnet.` |
| `ReplaceProductWizardInvalidMappingFile` | `Diese Datei enthält keine Daten bezüglich der Zuordnung von Kommunikationsobjekten.` |
| `ReplaceProductError` | `Produktersetzung fehlgeschlagen.` |
| `EnterPasswordForLocal` | `Passwort für das lokale Projekt eingeben` |
| `EnterPasswordForArchive` | `Passwort für das Archivprojekt eingeben` |
| `SetProjectPassword` | `Projektpasswort setzen` |
| `ChangeProjectPassword` | `Projektpasswort ändern` |
| `ClearPassword` | `Passwort löschen` |
| `MessagePasswordIncorrect` / `TextBlockWrongPassword` | `Falsches Passwort.` |
| `Label_CannotRemoveProjectPassword` | `Die Sicherheitseinstellungen wurden im Projekt aktiviert, so dass das Projektpasswort nicht entfernt werden kann.` |
| `PasswordCannotBeSetDueToLicense` | `Das Passwort kann nicht mit den installierten Lizenzen gesetzt werden, da das Projekt mehr als {0} Geräte enthält.` |

### 4.7 Demo / Reset

| Key | Wert |
|---|---|
| `Ets6DemoLicenseName` | `ETS6 Demo` |
| `RunDemo` | `Demo starten` |
| `DemoLicenseInfoText` | `Demo` |
| `AppsNotInDemoMode` | `Apps sind im Demo Modus nicht verfügbar.` |
| `MaximumExceedsBecauseOfDemoLicense` | `Aufgrund der installierten Lizenz kann die Operation nicht ausgeführt werden. ` |
| `SettingsResetUserSettings` | `_Benutzereinstellungen zurücksetzen` |
| `ResetSettingsConfirmation` | `Nachdem die Einstellungen zurückgesetzt wurden, wird die ETS beendet. Möchten Sie fortfahren?` |
| `SetupHelpClearUserSettings` | `Setzt die ETS Benutzer-Einstellungen zurück. Das schließt auch den Pfad zu Projekten oder zum zentralen Datenbankspeicher mit ein.` |
| `RestoreDefaultWorkspace` | `Standard Arbeitsbereich wiederherstellen` |
| `MessageResetNotifications` | `Alle Benachrichtigungen zurücksetzen?` |
| `ResetModalWindows` | `Modale Fenster zurücksetzen` |
| `SettingsClearProductStore` | `P_roduktspeicher löschen` |
| `ResetSplit` | `Teilung zurücksetzen` |
| `ResetParameter` | `Parameter zurücksetzen` |

## 5. BusChord-Gap-Analyse (Priorität)

| Priorität | Gap | Beleg | Aufwand | Empfehlung |
|---|---|---|---|---|
| P0 | Kein Projektarchiv/Versionen/Check-in/Check-out/Sperren | `ProjectArchiveServiceManager`, `IProjectArchiveService`, `ArchiveProjectVersion` | hoch | **native**; Kernfunktion für Mehrbenutzer/Sicherheit |
| P0 | Kein Auto-Save/Recovery Point/Integritätsprüfung | `PersistenceService`, `CreateRestorePointOperation`, `Project.ValidateProjectIntegrity` | mittel | **native**; Datenverlust vermeiden |
| P1 | Kein lokaler Projekt-Store, nur Datei öffnen/speichern | `FileProjectStore`, `Project.Save`, `PersistenceService` | mittel–hoch | **native** (oder klar dokumentieren, dass BusChord dateibasiert bleibt) |
| P1 | Kein Dashboard/Mehrprojekt/Suche/Tags/Archivordner | `TabDashboard`, `DashboardSearch`, `LocalProjectInfoProvider` | hoch | **native**; Voraussetzung für Archiv/Mehrprojekt |
| P1 | Import/Export nur `.knxproj`; kein Wizard/Konflikt/ETS4-5/OPC/Turtle/Baggage | `ImportWizard`, `ExportWizard`, `ProjectImportWizardViewModel` | hoch | **approximate** Dialog, dann native Konverter |
| P1 | Verschlüsselte Projekte nur lesend, kein Re-Encrypt/Passwortmanagement | `ZipContainerPasswordProtectedProjectStore`, `ChangeProjectPasswordOperation` | mittel | **approximate** Lesen; Re-Encrypt später native |
| P2 | Projektvergleich nur zwei Dateien, kein Archivbezug/PDF/Tiefe | `ProjectComparer`, `DifferenceReport` | mittel | **approximate** PDF/Archivvergleich, dann native |
| P2 | Produktersetzung nur RefId, kein Wizard/Template/ComObject-Mapping | `ReplaceProductWizard`, `ReplaceProductOperation` | mittel | **approximate** Mapping-Dialog, dann native |
| P2 | Reports/Print nur HTML, keine 12 Reports/Formate/Druckdialog | `Knx.Ets.Reports`, `PrintLibrary`, `ReportVM` | hoch | **approximate** HTML/PDF; ausgewählte Reports native |
| P3 | Keine Projekteigenschaften (Typ, Nummer, Completion, Tags, Bild, Historie, Anhänge, BCU-Key) | `ProjectDetails*`, `LocalProjectInfo` | mittel | **approximate** Metadaten, dann native Dialog |
| P3 | Keine Mehrfenster-/Instanz-/CLI-Unterstützung | `ApplicationViewModel.OpenNewWindow`, `StartupOptions` | mittel | **native** später; CLI optional |
| — | Demo-Lizenzmodus | `Ets6DemoLicenseName`, `RunDemo` | — | **dokumentiert nicht unterstützt** (kein Lizenzsystem in BusChord) |
| — | Datenbank-Backup/Restore | `CreateBackupOperation`, `RestoreBackupOperation` | niedrig | **dokumentiert nicht unterstützt**, bis zentraler Produktspeicher existiert |

**Top-3 für den nächsten Schritt:**

1. **Projektarchiv + Recovery Points** (Datenverlust/Sicherheit, klar abgrenzbar).
2. **Auto-Save + Integritätsprüfung + lokaler Store** (oder bewusste
   BusChord-Entscheidung für dateibasiertes Speichern dokumentieren).
3. **Dashboard/Mehrprojekt + Suche** (macht Archiv und Projekteigenschaften
   überhaupt sichtbar).

## 6. Grenzen dieser Analyse

- Keine ETS-Ausführung, keine UI-Interaktion, keine Screenshots; UI-Reihenfolge
  und Dialogtexte sind aus Typen/Methoden/Ressourcen abgeleitet.
- Dotfuscator verschlüsselt viele String-Literale; exakte interne Dateinamen,
  Formatstrings und Regex-Muster (Suche, Recovery-Point-Dateiname,
  Archiv-Pfadbildung, Entry-Namen im Passwort-Container) sind teilweise
  **abgeleitet** oder **unbekannt**.
- Der exakte Re-Encrypt-/Schreibpfad für Projektpasswörter wurde nicht
  dekompiliert; BusChord darf verschlüsselte Projekte weiterhin nur lesen.
- Die lokalisierten Reporttitel und die genaue Report-Auswahl sind aus
  `[ReportInfo]`-Typen abgeleitet, nicht aus einer laufenden Report-UI.
- Das „optimierte“ Archivformat ab ETS 6.3 und die ETS5-Archivmigration sind
  nur über Typen/Strings belegt; die Byte-Layouts wurden nicht geprüft.
- Lizenz-/Demo-Grenzen stammen aus `KNX.package` und Ressourcenstrings; das
  tatsächliche Verhalten im Demo-Modus wurde nicht ausgeführt.
- Keine Aussage über ETS-Versionen außer 6.4.8718.0.

## 7. Nächste belegbare Schritte

1. Ein reales, unverschlüsseltes ETS6-Projekt exportieren und den lokalen
   Store/Export gegen die hier beschriebenen Dateinamen und Hashregeln prüfen.
2. Ein Archiv mit zwei Versionen anlegen, `status.info`/`project.info` lesen
   und Versionshash/Pin/Lock gegen `ProjectArchiveService` verifizieren.
3. Ein passwortgeschütztes Projekt mit bekanntem Passwort öffnen, Entry-Namen
   und `HasPassword`-Erkennung bestätigen; **kein** Passwort erraten.
4. Report-/Druckausgabe mit einem echten Projekt erzeugen und die 12
   Report-Typen sowie PDF/XLSX-Export verifizieren.
5. Projektvergleich lokal vs. Archivversion und Import-Konflikt
   (Kopie/Überschreiben) mit Testdaten ausführen.
