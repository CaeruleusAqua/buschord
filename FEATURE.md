# BusChord — Produktstatus

BusChord ist eine native Workbench für KNX-Projektbearbeitung mit einem gemeinsamen
Projektmodell und einem MCP-Server. Projektänderungen erfolgen offline.

- [Funktionsumfang und Grenzen](docs/status.md)
- [Gesamtplan](plans/00-gesamtplan.md)
- [Benutzerhandbuch](docs/manual.md)
- [MCP-Vertrag](mcp.md)
- [Installation und automatische Updates](docs/distribution.md) — lokale Paketprüfung bestanden; Plattformfreigabe offen.

Implementiert sind Projektarchive, Kataloge, Topologie, Gebäude und Gruppenadressen,
Kommunikationsobjekte und Kanäle, validierte Parameterentwürfe, dynamische Strukturen,
geprüfte Migration, atomare Verknüpfungen, Undo/Redo und Berichte.
Native /20-Projekte behalten ihre Linientopologie; /21-/22-Projekte ihre
Segmenttopologie. Bearbeiten und Speichern erhalten das unterstützte Root-,
ZIP- oder Projektordnerlayout und undurchsichtige Zusatzdaten.
Geschützte Archive verwenden das Standardprofil oder eine passende lokale
Überschreibung und das Projektpasswort. Der Salt ist in den Optionen einstellbar.

Herstellerkataloge lassen sich nach quellspezifischer Freigabe herunterladen und
automatisch importieren. Verzeichnisabruf, Fortschritt, Abbruch, Konflikterhalt
und lokale Berichte sind integriert; [Ablauf und Grenzen](docs/catalog-downloads.md).
App-eigene modale Dialoge verwenden unter Windows eine gemeinsame Kopfzeile.

Eine ausdrücklich aktivierte [Telegrammhistorie](docs/workflows.md) ergänzt
Zeit-/Mehrwertfilter, Kontext, Export und sichtbare Aufnahmeabdeckung.
Optionale [lokale Checkpoints](docs/recovery.md) erhalten übernommenes Modell und
Undo/Redo mit getrenntem Änderungsnachweis und bewusster Quellenprüfung.
Private Parameter-/DEMO-Entwürfe und Fensterlayout sind davon ausgenommen.

Explizite Byte-Vorschauen unterstützen begrenzte Zahlen-, Auswahl-, Single- und
Zeitprofile. Die optionale Ressourcen-Vorschau erhält vollständige vorhandene
Data-/Mask-Puffer für begrenzte absolute und Root-Relative-Teilmengen.
Quellenbindungen verhindern veraltete Vorschauen; offene Image-Anforderungen
bleiben ausdrücklich sichtbar.
Eine separate Property-Vorschau unterstützt begrenzte Root-Zahlen-/Auswahlfelder
in vollständigen archivierten Puffern mit gültigen Masken und eindeutigem PDT.
Separate Count-Bytes bleiben unverändert; Kapazität und physische Aliase bleiben offen.
Eine separate statische Code-Komposition erfordert eine explizite Maskenrichtlinie,
trennt Quelldaten von Arbeitsbytes und weist unbelegte Bits aus.
Ein weiterer expliziter Root-Arbeitslauf verarbeitet alle Referenzen des
unterstützten festen Speicherprofils und optionale Kommunikationstabellen aus
einer gemeinsamen Auswertung. Byte-Reihenfolge und inaktive Werte folgen den
Quellenoptionen; Tabellen benötigen passende Arbeitsbytes. Das Ergebnis bleibt
ein begrenzter Arbeitsstand mit offenen Mapping- und Finalisierungsanforderungen.

Ein archivierter Quellenindex trennt Ressourcenorte, Imageorte und bekannte
Factory-Puffer. Der Compiler für primäre Ladeprozeduren erhält Reihenfolge,
Merge-Entscheidungen und explizite Teilefilter. Eine zusätzliche explizite Vorschau
erzeugt begrenzte M112-/SystemB-Kommunikationstabellen mit eigener Quellenbindung.
Eine separate Prozedurvorschau löst begrenzte Root-Bedingungen und relative
Speichervergleiche ohne Skripte auf. Ein zusätzlicher expliziter Root-Arbeitslauf
kombiniert diese Auflösung mit der vollständigen unterstützten Referenzpopulation
und passenden Tabellen aus derselben Auswertung mit eigener Versionskennung.
Vollständige Aktivierung, Gerätebefehle und Imagefinalisierung bleiben offen.
Ein separater Property-Arbeitslauf initialisiert alle tatsächlich ausgewählten
Allokationen seines festen Root-Profils und formatiert die vollständige
Referenzpopulation aus derselben Auswertung. Initialdaten, Gültigkeit und
Sollbytes bleiben getrennt; archivierte Count-Werte werden nur beobachtet.
Das erste Profil ist mit synthetischen Daten geprüft und ersetzt weder ein
allgemeines Property-Backend noch ein vollständiges Geräteimage.
Ein gemeinsamer logischer Arbeitslauf verbindet ausgewählte Speicherregionen,
Code, Pointer, tatsächliche Property-Allokationen und zugelassene Root-Writer.
Mehrere Optionsannahmen teilen eine Auswertung; Tabellen lesen die aktuellen
Arbeitsbytes. Öffentliche API und MCP sind geprüft; genaue Profilgrenzen und
verbleibende Mapping-/Writer-Aufgaben stehen im Status.
Region-/Code- und Pointer-/Factory-Beiträge dürfen ihren überlappenden
Arbeitsbereich auch in mehreren geordneten Schritten vergrößern. Getrennte
Quellenbesitzer und Allokationsgrenzen bleiben dabei erhalten.
Der gemeinsame Lauf unterstützt auch begrenzte physische Single- und Zeitfelder;
seine Policy-Ausgabe beschreibt Quellenoptionen und die verwendeten Codecs.

MDT, Jung und Gira sind verbindliche Herstellerprofile. Die derzeit unterstützten
Import- und Parameterprofile sowie die verbleibenden Plug-in-, Dateiformat- und
Inbetriebnahmeaufgaben stehen im Status und in den Teilplänen.

Die Oberfläche bietet acht Sprachen und Light/Dark. Die vollständige manuelle
Accessibility-/Plattformabnahme läuft; ein [gezielter Geräteversuch](plans/06-commissioning.md)
ist seit 2026-10-29 freigegeben. Die allgemeine Hardware-Abnahme und die hörbare
Narrator-Prüfung bleiben offen.
Fokussierte Suchtreffer öffnen per Enter; die Options-Schaltflächen sind bei
echten 200 % ohne Maximieren erreichbar. Im Projektassistenten sind alle fünf
Felder einschließlich der fokussierten Zahleneditoren zugänglich benannt.
Die Parameter-Geräteauswahl hat einen lokalisierten Namen und zeigt auch für
UIA Adresse und Gerätename statt interner Datensatzdetails.
Der Navigator erhält den Zeilenfokus auch nach Auf-/Zuklappen und mehreren
direkt aufeinanderfolgenden Aktualisierungen.
Der dokumentierte Windows-Referenzlauf
besteht mit 144 Rohmessungen alle Latenz- und Speichergrenzen.
