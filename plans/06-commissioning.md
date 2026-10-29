# Plan — Gezielte Geräteabnahme

[Bus-Plan](02-bus.md) · [Produktgrenzen](../docs/status.md#network-and-commissioning)

- [x] Nutzerfreigabe für Programmierung und Schreiben an einem bestimmten Taster aufnehmen; Projektkopie außerhalb des Repositories sichern.
- [x] Ziel aus Produktreferenz und Gruppenverknüpfungen bestimmen; Maskenversion am Gerät lesen.
- [x] Adressierte Management-Verbindung mit Sequenzen, ACKs, Abbruch und strikter Antwortzuordnung ergänzen und prüfen; Bus-Suite mit 524 Checks bestanden.
- [x] Geräteidentität und Applikationsversion abgleichen; Programmierstatus und Device-Control lesen. Vollständiger Konfigurationsvergleich bleibt beim Image-Schritt.
- [x] Programmiermodus schreiben, rücklesen und über eine frische Verbindung wiederherstellen; anschließend Status und Applikation separat unverändert rücklesen sowie Temperaturantwort vom selben Gerät empfangen.
- [x] Unveränderte Tastenfunktion nach dem Schreibtest vom Nutzer vor Ort bestätigen lassen.
- [x] Alle 37 Root-Speicher-Unions mit Initialisierung, aktiven Aliaswerten, Grenzen und Konfliktprüfung in ein quellengebundenes Arbeitsimage aufnehmen; 88 gezielte Prüfungen.
- [x] LSM-Zustände, Tabellen, lesbare Applikations-/Taskkennungen und sämtliche deklarierten Speicherbereiche lesend sichern. Nicht lesbare interne Allokationsmetadaten liegen außerhalb der Sicherungszusage.
- [x] M112-Ladebefehle und quellenbasierte Konfigurationswiederherstellung mit Abbruch-, Verify-, Metadaten- und Neustartfehlern gegen eine unabhängige Gegenstelle prüfen; 35 gezielte Prüfungen.
- [x] Segmentimage, Speicherroute und Ladeprozedur für das erneute Laden derselben Konfiguration validieren; bekannte persistente Bits müssen vor dem ersten Entladen übereinstimmen.
- [x] Vollständiges erneutes Laden derselben Konfiguration, EEPROM-Verify, Wiederanlauf und anschließende Temperaturantwort am freigegebenen Ziel nachweisen; tatsächliche Grenzen dokumentieren.

Die Freigabe gilt für den vom Nutzer benannten Taster in der vorhandenen Installation.
Andere Geräte, globale Gruppenbefehle, Adressänderungen und Werksreset sind kein Testziel.
Private Projekte, Kennwörter, Netzwerkdetails und Rohmitschnitte bleiben außerhalb des Repositories.
Die bisherige allgemeine Hardware-Abnahme bleibt offen; ein Einzelgerätetest ersetzt sie nicht.

Der [Konsolenpfad und die tatsächlichen Grenzen](../docs/commissioning.md) sind dokumentiert.
Das ausgewählte Altprofil enthält 37 Speicher-Unions. Dafür besteht ein separater,
quellengebundener M112-Lauf. Der allgemeine Image-Builder und reguläre
Workbench-/MCP-Schreibaktionen behalten ihre bisherigen Grenzen.
RAM und Stack werden gemäß Ladeprozedur allokiert; volatile Laufzeitbytes sind keine
zurückzuschreibende Konfiguration. Die Wiederherstellung verbindet gesicherte
Konfigurationsbytes mit der Ladeprozedur derselben Projektquelle. Sie verspricht
keinen Snapshot nicht lesbarer interner Allokationsattribute.
Der vollständige Lauf wurde am freigegebenen Einzelgerät erfolgreich ausgeführt.
Ein vorangegangener Neustart-ACK-Timeout löste die Wiederherstellung aus; eine
unabhängige Nachprüfung bestätigte alle ursprünglichen persistenten Prüfsummen.
Der korrigierte Neustartpfad sendet einmal ohne Geräte-ACK-Pflicht und verlangt
anschließend eine vollständige Prüfung über eine frische Verbindung.
