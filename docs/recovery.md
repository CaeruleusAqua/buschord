# Lokale Wiederherstellung

Unter **Werkzeuge → Lokale Wiederherstellung** lassen sich lokale Checkpoints
prüfen und bewusst als aktive Projektsitzung übernehmen. Die Aufzeichnung ist
standardmäßig aus. **Lokale Checkpoints und Änderungsnachweis aktivieren** schaltet
sie global ein; dieselbe separate Einstellung ist in den Optionen erreichbar.
Sie wird sofort gespeichert, unabhängig von Speichern/Abbrechen der übrigen Optionen.

Ein bereits geöffnetes Dateiprojekt nach dem Einschalten erneut öffnen. Erst eine
geprüfte Quellenbindung erlaubt dessen Aufzeichnung. Nicht unterstützte Quellen
oder Recovery-Grenzen erhalten einen sichtbaren Status; das normale Bearbeiten
und Speichern des Projekts bleibt möglich.

## Checkpoint übernehmen

1. Einen Eintrag auswählen. Seine angezeigten Metadaten öffnen keine Quelldatei.
2. Bei Dateiprojekten **Originaldatei ausdrücklich auswählen…** verwenden;
   ungespeicherte Projekte über **Ungespeicherten Checkpoint wählen** bestätigen.
3. **Checkpoint prüfen** prüft Format, Projektidentität, Dateihash, vollständige
   Datenreferenzen und bekannte Herstellerabhängigkeiten.
4. Hinweise zum Modellumfang und einem möglicherweise weiter fortgeschrittenen
   Änderungsnachweis lesen. **Prüfen und wiederherstellen** verwendet die bestehende
   Verwerfungsfreigabe und prüft den Stand vor der Übernahme erneut.

Die Übernahme ersetzt Projekt und Undo/Redo gemeinsam. Zwischenzeitliche Änderungen
verlangen eine neue Prüfung/Freigabe. Ein ungespeicherter Checkpoint bleibt dirty
und benötigt **Speichern unter**. Die Projektdatei wird durch die normale
Speicheraktion geschrieben.

## Umfang und Grenzen

Checkpoints enthalten das übernommene Modell, Undo/Redo, den gespeicherten
Undo-Stand sowie unterstützte Archiv-, Hardware- und Textinformationen.
Private Parameter-/DEMO-Entwürfe und Fensterlayout gehören nicht dazu. Solange
private Entwürfe offen sind, werden Checkpoints ausgesetzt; die Wiederherstellung
verlangt vorheriges Übernehmen oder Verwerfen im jeweiligen Editor.
Eine laufende Programmierung sperrt die Wiederherstellung ebenfalls.

Der unabhängige Änderungsnachweis erhält übernommene Aktionen auch über Undo und
verworfenes Redo hinaus. Er kann deshalb weiter reichen als der letzte Checkpoint.
Ohne aufgezeichnete Herstelleranforderungen erscheint der Umfang ausdrücklich
als Modell-Wiederherstellung; eine vollständige Herstellerumgebung ist damit
nicht belegt. Bekannte fehlende oder veränderte Anforderungen werden abgewiesen.

Ungeschützte Projektdaten werden lokal unverschlüsselt im Benutzerprofil gehalten.
Geschützte Inhalte und Passwörter gelangen nicht in Klartext-Checkpoints oder
Änderungstexte; für ausgelassene geschützte Sitzungen bleiben nur eine undurchsichtige
Sitzungskennung und der Zeitpunkt. Prüfsummen erkennen Beschädigungen und ersetzen
keinen Echtheitsnachweis gegen Änderungen durch denselben Benutzer.

Der erste Store begrenzt unter anderem die Quellarchive auf 128 MiB, eine
Undo-Sitzung auf 60 Zustände, ein Journal auf 64 MiB und den Store auf 4 GiB.
Fehler oder Überlauf bleiben sichtbar; frühere Checkpoints bleiben erhalten.
Eine allgemeine Stromausfall- oder Geräteabnahme ist damit nicht verbunden.

## Speicherort und Bereinigung

Die Daten liegen unter `BusChord.UserData/recovery` im lokalen Benutzerdatenordner,
unter Windows gewöhnlich `%LOCALAPPDATA%\BusChord.UserData\recovery`.
Die separate Schalterdatei heißt `recovery-preferences.json` im übergeordneten Ordner.
Neue Unix-Storeverzeichnisse verwenden 0700, neue Datendateien 0600.

Ausschalten beendet neue Aufzeichnungen; vorhandene Daten bleiben erhalten.
Es gibt keine automatische Bereinigung. Für eine manuelle Bereinigung zuerst
die Aufzeichnung ausschalten, alle App-Instanzen beenden, bei Bedarf den
Recovery-Ordner sichern und anschließend gezielt diesen Ordner entfernen.
Native Projektdateien und deren [Dateisicherungen](project-backup.md) werden
separat verwaltet.
