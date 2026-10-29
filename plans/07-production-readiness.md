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

- Der [gemeinsame logische Imagepfad](../docs/status.md#combined-logical-working-stage)
  unterstützt begrenzte Root-, Modul-/Repeat-/Allocator- und bedingte Profile
  mit nachgewiesener Enum- oder enger 0/1-Binärauswahl.
  Potenzielle Besitzer und gespeicherte inaktive Werte bleiben erhalten.
  Vollständige physische Abbildung, weitere Herstellerprofile und Finalisierung
  bleiben offen; Teilimages erhalten keine allgemeine Downloadfreigabe.
- Der [gezielte M112-Versuch](06-commissioning.md) umfasst alle 37 Root-Unions,
  vollständiges Wiederholungsladen, eine Parameteränderung, unabhängiges Rücklesen
  und bestätigte Konfigurationswiederherstellung. Der Nutzer bestätigt auch nach
  dem vollständigen Lauf unveränderte Tastenfunktion. Das gilt für ein freigegebenes Gerät.
- [Workbench-Verbindungen](../docs/secure-connections.md), Monitor, adressierter
  Scan und der [M112-Review-/Recoverydialog](../docs/commissioning.md) besitzen
  gemeinsame, bis zum I/O-Ende gehaltene Verbindungsbesitzer. Eine separate
  [Plain-M112-Warteschlange](../docs/real-programming-queue.md) bindet bestätigte
  Einzelaufträge, dauerhafte Belege und Wiederherstellung; Quellenänderungen und
  ungültige Historie erhalten keine alten Flags. Die separate
  [Adressvergabe](../docs/individual-addressing.md) besitzt physische Zielbindung,
  dauerhaften Intent/Versuchsmarker und lesende Kalt-Reconciliation; ihre
  Hardwareabnahme, weitere Ladeprofile und sichere Broadcast-Provisionierung bleiben offen.
- [Geschützte Keyrings](../docs/protected-keyrings.md),
  [Tool/P2P mit Sync](../docs/individual-secure.md),
  [Tool-Konsolen-/MCP-Zugang](../docs/tool-access-console.md) und
  [Gruppenprofile für Factory/Workbench/MCP](../docs/group-secure-connections.md) sind mit
  vorhandenen vertrauenswürdigen Zuständen softwareseitig geprüft. Die
  Workbench-Auswahl bindet dieselben Quellen und verlangt ohne unabhängigen Anker
  eine neue Bestätigung pro Auftrag; Monitor-Provenienz und gejointer Abbau sind geprüft.
  Die [OS-Kennwortablage](../docs/credential-vault.md) erhält Quellen- und Projektbindung.
  Sichere Erstinbetriebnahme, allgemeine Schlüsselprovisionierung und Secure-Geräteabnahme bleiben offen.
- Die [installierte Linux-Abnahme](../docs/distribution.md#verification-and-current-boundaries)
  umfasst das vollständige APT-Upgrade auf preview.42, tatsächlichen Timeraufruf,
  Laufzeitschutz und native DE-/EN-Abläufe unter Ubuntu 26.04/WSLg. Weitere
  Plattformen, Stromausfallpfade und signierte Veröffentlichung bleiben offen.
- Aktuelle Softwaregrenzen und Abnahmen stehen im [Produktstatus](../docs/status.md).
  Die Produktionsfreigabe bleibt offen; private Projekte, Identitäten, Kennwörter
  und Gerätebelege bleiben außerhalb von Git.
