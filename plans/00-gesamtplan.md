# BusChord — Gesamtplan

## Steps

- [x] Projektmodell, Archive, Kataloge und native Workbench implementieren.
- [x] .NET 10, lokale Windows-/Linux-Pakete und signaturgeprüfte Updatepfade ergänzen.
- [x] Expliziten Herstellerverzeichnis-Abruf, quellspezifischen Download und erhaltenden Katalogimport mit nativer Oberfläche integrieren.
- [x] Native Werte, Namen, Kanalidentitäten und Objektverknüpfungen erhalten.
- [x] Validierte Parameterentwürfe mit dynamischer Struktur, Apply und Undo ergänzen.
- [x] Begrenzte Modul-/Repeat-/Allocator-Profile und explizite Berechnungen prüfen.
- [x] Verschlüsselte /20-/21-/22-/23-Speicherung, Migration und atomare Batch-Verknüpfungen absichern.
- [x] Berichte, Seriendruck-TSV und lokale DEMO-Vergleichsdaten ergänzen.
- [x] Speicher-/Maskendiagnose und begrenzte typisierte Byte-Vorschauen ergänzen.
- [x] Explizite absolute/Root-Relative-Ressourcenpuffer mit Quellenbindung und offenen Image-Anforderungen ergänzen.
- [x] Archivquellen und primäre Ladeprozeduren typisieren und an MDT-, Jung- und Gira-Daten prüfen.
- [x] Begrenzte M112-/SystemB-Kommunikationstabellen mit nativer Aktivierungs- und Quellenprüfung ergänzen.
- [x] Separate Root-Bedingungen und relative Vergleiche in gebundenen Ladeprozedur-Vorschauen prüfen.
- [x] Separate begrenzte Root-Property-Vorschauen mit vollständigen archivierten Puffern und unveränderten Count-Bytes prüfen.
- [x] Separate statische Root-Code-Komposition mit expliziter Maskenrichtlinie und unbekannten Bits prüfen.
- [x] Vollständige feste Root-Referenzpopulation und passende Tabellen in einem gebundenen Offline-Arbeitslauf prüfen.
- [x] Begrenzte Root-Bedingungen, vollständige feste Referenzpopulation und passende Tabellen aus derselben Auswertung kombinieren.
- [x] Separaten festen Root-Property-Arbeitslauf mit quellengebundener Initialisierung, vollständiger Referenzpopulation und beobachteten Count-Werten prüfen.
- [x] Gemeinsame logische Speicher-/Property-Initialisierung, zugelassene Root-Writer und aktuelle Tabellen-Seeds mit bedingten Optionsannahmen über API und MCP prüfen.
- [x] Begrenzte globale Speicherwriter für literale Module/Repeats und Root-Allocator mit vollständigen Ursprüngen, latenten Besitzern und Reservierungen in denselben Arbeitslauf integrieren.
- [x] Verbindliche Prüfgrenzen für den dokumentierten Windows-Referenzlauf implementieren.
- [x] Windows-Referenzbudget mit 144 Rohmessungen innerhalb aller Latenz- und Speichergrenzen prüfen.
- [x] Lesende Workbench-Verbindung, Gruppenmonitor und Scan sowie enges M112-Änderungs-/Wiederherstellungsprofil integrieren und gezielt prüfen.
- [ ] Verbleibende Hersteller-, Speicher- und Dateiformatprofile schließen.
- [ ] Manuelle UI-, Accessibility- und Plattformabnahme abschließen.
- [ ] Reale Inbetriebnahme prüfen — gezielter Tastertest freigegeben, siehe [Einzelgeräteplan](06-commissioning.md).
- [x] Geprüften .NET-10-Stand in `development` integrieren.
- [ ] Veröffentlichung nach verbleibender Abnahme freigeben.

## Stand und Teilpläne

Stand: 2026-10-29. [Produktstatus und genaue Grenzen](../docs/status.md).
Implementiert bedeutet keine allgemeine Kompatibilitäts- oder Gerätefreigabe.

- [Kern, Archive und Hersteller](01-kern.md)
- [Bus, Download und Secure](02-bus.md)
- [Native Workbench und Abnahme](03-ui.md)
- [Arbeitsabläufe und lokale Historie](04-workflows.md)
- [Installation und automatische Updates](05-distribution.md)
- [Vollständige Projekte und Produktionsfreigabe](07-production-readiness.md)

## Nächste Aufgaben

- MDT, Jung und Gira: weitere konkrete Applikations-, Plug-in-/DCA-, VBScript-,
  Ladeprozedur- und Aktivierungsprofile validieren.
- Allgemeine Speicher-/Property-Backends und vollständige Geräteimages ergänzen;
  Teilvorschauen ersetzen diese Pipeline nicht.
- Weitere bedingte Ladeprozedur- und Tabellenprofile, vollständige Aktivierung und
  Finalisierung über die geprüften Teilprofile hinaus ergänzen.
- Breitere Format-/Roundtrip-Interoperabilität und weitere Exportvarianten
  über die geprüften /20-/21-/22-/23-Profile hinaus validieren.
- Gemischte DPI, vollständige Tastaturbedienung und weitere Plattformen prüfen;
  hörbare Screenreader-Prüfung ist auf Nutzerwunsch vorerst zurückgestellt.

## Leitplanken

- Keine realen Geräte-Schreibzugriffe, Downloads oder Secure-Inbetriebnahme ohne
  ausdrücklich freigegebenen Testumfang.
- Private Projekte, Zugangsdaten und temporäre Testartefakte außerhalb des Repositories halten.
- Core bleibt BCL-only; Skripte benötigen die explizite separate Laufzeitfähigkeit.
- Native Werte und Namen, gemeinsame Projektzustände und atomare Fehlerbehandlung erhalten.
- Nicht-triviale Änderungen mit ausführbaren Regressionen und unabhängiger Prüfung absichern.
