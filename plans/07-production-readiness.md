# Plan — Vollständige Projekte und Produktionsfreigabe

[Gesamtplan](00-gesamtplan.md) · [Aktuelle Grenzen](../docs/status.md)

Ziel: Ein vollständiges KNX-Projekt planen, konfigurieren, in Betrieb nehmen,
prüfen, übergeben und später ändern können. Teilprofile und erfolgreiche
Einzelgeräteversuche ersetzen diese Endabnahme nicht.

## Umsetzung und Nachweis

- [x] Ausgangsstand, Zieländerungen und tatsächliches Gerät getrennt binden;
  Parameter-/Tabellenänderungen herunterladen und nach Fehlern wiederherstellen.
- [ ] Geräteimages einschließlich physischer Abbildung, RAM-/Tabellenlayout,
  Properties, Modul-/Repeat-/Allocator-Writern, Bedingungen und Finalisierung schließen.
- [ ] Herstellerprofile MDT, Jung und Gira sowie benötigte Skript-/DCA-Verträge
  durch reale Applikationsfälle und vollständige Abläufe abdecken.
- [ ] Echte Verbindungen, Monitor, Scan, Identifikation, Adressierung und
  Programmierwarteschlange in der Workbench bedienen und sauber beenden können.
- [ ] Weitere Management-/Ladeprofile, Erstinbetriebnahme und Geräteersatz
  mit wiederaufnehmbarer Wiederherstellung integrieren.
- [ ] Secure-Tunnel, Data Security, Schlüsselverwaltung und sichere
  Inbetriebnahme einschließlich Wiederanlauf und Zählerhaltung integrieren.
- [ ] Projektanlage bis Abnahme/Übergabepaket und Wartungsänderung als
  zusammenhängenden Ablauf prüfen; Archiv-/Herstellerinteroperabilität nachweisen.
- [ ] Plattforminstallation, automatische Updates, Fehler-/Stromausfallpfade,
  Tastatur, DPI und Accessibility abnehmen; zurückgestellte Hörprüfung getrennt ausweisen.
- [ ] Dokumentierte unterstützte Profile, reproduzierbare Freigabeprüfungen,
  signierte veröffentlichbare Pakete und vollständigen Anforderungsnachweis liefern.

## Laufender Stand

- Der gemeinsame logische Imagepfad enthält ein begrenztes synthetisch geprüftes
  Modul-/Repeat-/Root-Allocator-Speicherprofil mit vollständigen Ursprüngen,
  latenten Besitzern und Reservierungen. Bedingte/verschachtelte Module, weitere
  Speicherträger und Hersteller-Imagebelege bleiben offen; die vollständige
  Image-/Ausführungs-/Downloadfreigabe ist davon unberührt.
- Ausgangspunkt: `415a2db`. Der gebundene M112-Änderungspfad hat 32 gezielte
  Prüfungen einschließlich anderer Parameter-/GA-/Objektzustände und Rückfall.
  Der Wiederholungspfad hat weitere 36 gezielte Prüfungen.
- Am freigegebenen Taster wurde ein LED-Parameter geändert, als einzige Änderung
  im EEPROM rückgelesen und anschließend der gesamte persistente Originalstand
  unabhängig bestätigt. Der lesende Scanner hat Koppler und Taster erreicht.
- Der erste Inbetriebnahmestand ist über PR #7 in `development` integriert;
  alle neun Prüfungen des abschließenden Branchstands bestehen. Ein echter
  lesender Workbench-Verbindungsversuch hat Geräteantwort und gejointen Abbau bestätigt.
- Der native M112-Änderungs-/Recoverydialog, Root-RawData-Schreiber, korrektes
  Extended-cEMI und die authentisierte Secure-UDP-Bibliothek sind mit PR #8 in
  `development` integriert; alle neun Prüfungen des abschließenden Stands bestehen.
- Geschützte Keyrings und dauerhafte Data-Secure-Gruppenzustände bestehen separat
  101 Import-, 31 Codec-/Zähler- und 11 Tunnelprüfungen sowie die gemeinsame
  Bus-Regression auf Windows und Ubuntu 26.04; PR #9 ist mit neun grünen Prüfungen
  in `development` integriert. Die gemeinsame Secure-Verbindungswahl für Workbench,
  M112 und MCP ist mit PR #11 und neun grünen Prüfungen integriert; provisioniertes
  Tool-Access/Sync läuft separat.
- Der vollständige installierte APT-Upgrade auf die lokale Abnahmeversion preview.42
  besteht in Ubuntu 26.04/WSLg, einschließlich tatsächlichem Timeraufruf, Laufzeitschutz,
  vollständiger Paketkonfiguration und nativen DE-/EN-Abläufen. Weitere Plattformen,
  Stromausfallpfade und eine signierte Veröffentlichung bleiben offen.
- Echte lesende Workbench-Verbindung, Gruppenmonitor und adressierter Scan sind
  erreichbar; gemeinsame Verbindungsbesitzer beenden I/O vor erneuter Nutzung.
  Allgemeine Programmierung, Adressierung und weitere Geräteprofile bleiben offen.
- Der explizit provisionierte individuelle Tool-/P2P-Bibliothekspfad besteht
  21 Zustands-/Codec- und fünf Tunnelprüfungen sowie die volle Bus-Regression
  auf Windows und Ubuntu 26.04. Drei zusätzliche Lebensdauerprüfungen sichern
  die Schlüsselbereinigung bei fehlerhaften Abbruch-Callbacks ab. Die gemeinsame
  Tool-Access-Factory-Anbindung ist als explizites Management-Bibliotheksprofil mit
  signiertem Gerätetoolschlüssel und vorhandenen vertrauenswürdigen Zählern
  ergänzt. App/MCP-Anschluss, Provisionierung und Secure-Geräteabnahme bleiben
  eigene Aufgaben.
- Geräteversuche bleiben an die bestehende Zielgerätefreigabe gebunden.
  Private Quellen, Identitäten, Kennwörter und Rohdaten bleiben außerhalb von Git.
- Produktionsfreigabe bleibt offen, bis alle Anforderungen durch passende
  Software-, Plattform- und Gerätebelege tatsächlich erfüllt sind.
