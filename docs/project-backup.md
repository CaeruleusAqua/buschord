# Lokale Projekt-Backups und Wiederherstellung

Stand: 2026-10-21. Umsetzung: `src/Knx.Core/ProjectBackup.cs`, Checks:
`tests/Knx.Archive.Tests`.

Dieser Baustein ist eine lokale Sicherung von `.knxproj`-Archiven, kein ETS-
Projektarchiv: keine Versionen, Check-ins, Sperren oder Kollaboration. Er ist
unabhängig von Undo/Redo, Workbench-Layout und UI.

## API

```csharp
ProjectBackupInfo Create(string projectPath, string? backupRoot = null, int retention = ProjectBackup.DefaultRetention);
ProjectBackupInfo Capture(Project project, string? projectPath = null, string? backupRoot = null,
                          int retention = ProjectBackup.DefaultRetention);
IReadOnlyList<ProjectBackupInfo> List(string? projectPath = null, string? backupRoot = null);
ProjectBackupInfo Restore(string backupId, string destinationPath, string? backupRoot = null);
void Delete(string backupId, string? backupRoot = null);
```

`ProjectBackupInfo` enthält `Id`, `ProjectId`, `ProjectGuid`, `Name`,
`SourcePath`, `CreatedUtc`, `SizeBytes`, `Sha256` und `BackupPath` (die
gespeicherte `.knxproj`-Kopie).

- `Create` kopiert ein gespeichertes Projekt Byte für Byte, nachdem der
  Projektlader das Archiv akzeptiert hat.
- `Capture` serialisiert ein In-Memory-Projekt **nur auf ausdrücklichen Aufruf**
  in ein temporäres Archiv und sichert das Ergebnis. Kein Timer, kein
  Hintergrundlauf; UI-Verdrahtung ist offen.
- `List` liefert neueste zuerst. Ohne `projectPath` alle Sicherungen im Root,
  mit Pfad nur die dieses Projektpfads. Unlesbare oder veraltete Einträge werden
  übersprungen.
- `Restore` schreibt an den vom Aufrufer gewählten Pfad; der Backup-Root selbst
  ist als Ziel gesperrt. Bei gleichem Pfad wird das Original ersetzt, sonst
  bleibt es unberührt.
- `Delete` entfernt Archiv und Manifest. Fehlende oder veraltete Einträge
  liefern explizite Fehler.

## Ablage

Standard-Root: `%LOCALAPPDATA%\BusChord\Backups` (Fallback: Temp-Verzeichnis).
Ein eigener Root kann pro Aufruf übergeben werden, etwa für Tests oder
portable Installationen.

```text
<backupRoot>/
  <zeitstempel>-<zufall>.knxproj
  <zeitstempel>-<zufall>.json
```

Die Manifest-JSON enthält Version, ID, Projektidentität, Projekt-ID, GUID,
Name, Quellpfad, UTC-Zeit, Größe und SHA-256. Es werden keine Passwörter,
Projektinhalte oder Schlüssel im Manifest gespeichert.

Die Identität ist der kanonische Projektpfad (unter Windows
case-insensitiv). Dadurch funktionieren `List` und `Restore` auch dann, wenn die
Originaldatei fehlt oder beschädigt ist. Ein verschobenes Projekt erhält eine
neue Identität; `Capture` ohne Pfad verwendet `project:<GUID oder ID>` und ist
nur über `List()` ohne Pfadfilter sichtbar.

## Aufbewahrung

`retention` ist pro Projektidentität konfigurierbar (1–1000, Standard 10).
Nach jeder Sicherung werden die ältesten Einträge entfernt. Ist eine alte Datei
durch einen anderen Prozess gesperrt, bleibt sie bis zum nächsten `Create`
liegen; die neue Sicherung gilt trotzdem als erfolgreich. Es gibt bewusst
keinen prozessübergreifenden Lock.

## Sicherheit und Grenzen

- Quelle und Backup werden vor dem Kopieren bzw. Wiederherstellen mit
  `Knxproj.Load` validiert: leere, zu große (> 128 MiB) und korrupte Archive
  werden abgelehnt.
- `Restore` prüft Manifestgröße und SHA-256 und lädt das temporäre Ziel erneut,
  bevor `File.Replace`/`File.Move` das Ziel atomar ersetzt. Ein Fehler lässt das
  bisherige Ziel unverändert und räumt Temporärdateien auf.
- Backup-IDs sind reine Namen; Pfadtrenner, `..`, Doppelpunkt und ähnliche
  Eingaben werden abgelehnt. Alle internen Pfade werden gegen den Backup-Root
  geprüft; Archive, die Symlinks/Reparse-Points sind, werden nicht gelesen.
- Es wird nichts entpackt und nichts entschlüsselt. Unbekannte ZIP-Member und
  unbekanntes XML bleiben als Byteinhalt erhalten.
- Passwortgeschützte Projekte werden nicht gesichert: ohne Passwort kann das
  Archiv nicht validiert werden, und BusChord entschlüsselt oder umgeht den
  Schutz nicht. Solche Projekte müssen extern gesichert werden. (Das Speichern
  verschlüsselter Projekte ist ohnehin gesperrt.)
- Formatgrenzen des Projektladers gelten weiter: ZIP ohne ZIP64/Split-Volumes,
  max. 4096 Einträge, 64 MiB pro Eintrag, 256 MiB entpackt, 16 MiB XML,
  XML-Tiefe 128.

## Offen / nicht Teil dieses Bausteins

- Keine Verdrahtung in `WorkbenchWindow`, Öffnen/Speichern oder einen
  Start-Recovery-Dialog.
- Kein automatisches Backup beim Schließen und kein Auto-Save-Timer.
- Kein Projektarchiv mit Versionen, Metadaten, Sperren oder Kollaboration.
- Keine Verschlüsselung der Backup-Dateien; der Backup-Root erbt die
  Dateisystemrechte des Benutzers.

## Prüfen

```powershell
dotnet run --project tests/Knx.Archive.Tests -c Release
```

Die Checks decken Erzeugen/Listen/Retention, Roundtrip mit opaque ZIP-Member,
Korruption, Pfad-Traversal, Restore in den Backup-Root, atomaren
In-Place-Ersatz, leere/zu große/korrupte Quellen, `Capture` und `Delete` ab.
