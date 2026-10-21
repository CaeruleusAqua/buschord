# BusChord V4 — Restplan und Todo-Liste

Stand: 2026-10-21. Basis: lokaler Branch `development`, Commit `12fc904` (V4-Workbench integriert).
Referenz: `C:\Users\julia\Downloads\knx-workbench-v4-bundle`.
Dieser Plan führt die verbleibenden Arbeiten aus dem V4-Konzept, den Design-Reviews
und der ETS-Paritätsanalyse. Er ist die maßgebliche Todo-Liste; erledigte Punkte
werden hier abgehakt, nicht nur im Chat.

## Steps

- [ ] 1. ETS-Referenz je Funktionsbereich mit REA neu analysieren und dokumentieren.
- [ ] 2. V4-Restlücken aus Akzeptanzmatrix, UX- und Visual-Review konsolidieren.
- [ ] 3. Shell/Design abschließen: Katalog, Icons, Fokus, DPI, Dark/Light, Englisch.
- [ ] 4. Projekt-, Raum-, Linien-, Geräte- und Gruppenadressfunktionen vervollständigen.
- [ ] 5. Katalog, Parameter, dynamische Herstellerlogik und DCA-Grenzen abschließen.
- [ ] 6. Inbetriebnahme, Download, Diagnose, Monitor und Secure vollständig anbinden.
- [ ] 7. Berichte, Vergleich, Suche/Ersetzen, Exporte und Demo-Abläufe abschließen.
- [ ] 8. Tastatur, Screenreader, 100/150/200-%-DPI und kleine Fenster abnehmen.
- [ ] 9. Gesamttests, unabhängige Abnahme, Dokumentation und CI abschließen.
- [ ] 10. Geprüften Stand auf `development` integrieren und veröffentlichen.

## A. ETS-REA-Analyse (jede Funktion belegen)

Ziel: Für jede ETS6-Funktion, die BusChord nachbaut oder bewusst nicht nachbaut,
liegt eine evidenzbasierte Beschreibung aus der installierten ETS6 vor:
Assembly/Typ/Methode oder Ressourcensatz, sichtbarer Ablauf, Datenmodell,
Fehler-/Grenzfälle, Bus-/Dateiwirkung, BusChord-Lücke und Nachbauentscheidung.

| ID | Bereich | Belege | Ergebnis |
|---|---|---|---|
| ETS-1 | Projekt, Dashboard, Archiv, Import/Export, Druck/Bericht | `ETS6C.exe`, `Knx.Ets.ProjectArchive`, ViewModel-Ressourcen | `analysis/ets/project.md` |
| ETS-2 | Topologie, Gebäude, Geräte, Gruppenadressen, Verknüpfen | ObjectModel, ViewModel, HawkNET/Topologiebaum | `analysis/ets/addresses.md` |
| ETS-3 | Katalog, Produkte, Parameter, dynamische Logik, DCA/Plugins | CatalogProvider, ParameterDialog, plugins.json | `analysis/ets/catalog-parameters.md` |
| ETS-4 | Inbetriebnahme, Download, Bus, Secure, Diagnose, Monitore | HawkNET, Falcon, Diagnose-/Monitor-Assemblies | `analysis/ets/commissioning.md` |
| ETS-5 | Shell, Panels, Sidebar, Arbeitsbereiche, Shortcuts, Hilfe, Lokalisierung | ViewModel-Ressourcen, UI-Assemblies, Hilfe-Metadaten | `analysis/ets/shell.md` |

Jeder Bericht enthält: Funktionsinventar, beobachteten oder aus UI-Ressourcen
belegten Ablauf, technische API/Dateiformate, Fehlerfälle, Vertrauensgrad
(`belegt` / `abgeleitet` / `unbekannt`) und eine BusChord-Umsetzungsempfehlung.

## B. Offene V4-Funktionen (aus Bundle und Ist-Stand)

### B1. Projekt und Arbeitsumgebung

- [ ] Startfläche vollständig: Projekt öffnen/neu, Katalog, letzte Projekte, Demo importieren, Demo zurücksetzen, Suche.
- [ ] Projekt vergleichen und Produkt ersetzen aus der Workbench erreichbar machen.
- [ ] Projekt-Reset mit definierter Behandlung laufender Monitor-/Programmieraufträge.
- [ ] Layout-Persistenz je Projekt und Fensterpositionen prüfen; Reset ohne Projektdatenverlust.
- [ ] Projektwechsel-/Undo-/Entwurfsregeln als vollständige Transaktion absichern; fehlgeschlagene Ersetzung erhält alle Sitzungen.
- [ ] Mehrere Projekte/Instanzen gemäß ETS-Analyse bewerten; mindestens ein sicheres Einzelprojekt-Verhalten dokumentieren.

### B2. Navigator und Arbeitsgegenstände

- [ ] Alle Baumaktionen: anlegen, umbenennen, löschen, verschieben, kopieren, Adresse ändern, Kontextmenüs.
- [ ] Drag-and-drop für Geräte/Adressen mit gleichwertigem Tastaturweg.
- [ ] Linien-/Segment-/Raumwechsel inklusive Undo und Adressvorschlägen.
- [ ] Navigationszustand, Filter, Aufklappzustand und Auswahl je Perspektive vollständig persistieren.
- [ ] „In Topologie zeigen“, „Daneben öffnen“, Favoriten/Pinning gemäß ETS-Ablauf.
- [ ] Externe Kanäle/Effekt-Raumbezug im UI vollständig erklären und prüfen.

### B3. Tabelle, Inspektor, Zuordnungen

- [ ] Tabellen-Mehrfachauswahl mit Bereichsauswahl (Shift), Alles auswählen, Tastatur.
- [ ] Vollständige GA-Gegenansicht und Beziehungseditor für Gerät/KO/GA.
- [ ] Atomare Mehrfachzuordnung mit Vorprüfung, Vorschau, Duplikatbehandlung und Undo (UI-Abschluss).
- [ ] Sendeadresse setzen/entfernen, Warnung bei verbleibenden Links, keine stille Ersatzwahl.
- [ ] KO-Aktivierung/Flags/DPT/Länge editierbar oder bewusst schreibgeschützt kennzeichnen.
- [ ] Long-list-Darstellung, Zähler und vollständiger Zuordnungseditor für >10.000 Objekte.
- [ ] Inspector als Property-Grid mit Abschnitten, Fußaktionen und stabiler Fokusführung.

### B4. Parameter

- [ ] Native Parameter-UI vollständig V4-stylen; Abschnittsnavigation und Footer.
- [ ] Entwurfsindikator sichtbar; Undo/Projektwechsel/Refresh verwirft nur mit klarer Meldung.
- [ ] Wirkungsvorschau für alle unterstützten KO-/Link-Änderungen; unbekannte Herstellerdynamik/DCA klar trennen.
- [ ] Demo-Schalten/Dimmen vollständig mit Vorschau, Apply, Undo und Linkerhalt.
- [ ] Fehler-/Validierungszustände je Feld und Sammelfehler im Footer.
- [ ] DCA-erforderliche Geräte read-only kennzeichnen und opaque Daten erhalten.

### B5. Katalog

- [ ] `CatalogPane` vollständig auf V4-Design heben (Panel, Zeilen, Buttons, Suche, Leerzustände, Tastatur).
- [ ] Import/Reload-Fortschritt, Fehler, Abbruch und Hintergrundladen sichtbar.
- [ ] Einfügen in gewählten Raum/Line/Segment inkl. Adressvorschlag, Namenskonflikt und Undo.
- [ ] Produkt-/Applikationsvorschau, DPT-/KO-Metadaten und unbekannte Herstellerdaten erklären.
- [ ] Suche/Filter über Katalog, Hersteller, Produkt, Text und DPT.
- [ ] Keine Herstellerdaten im öffentlichen Repository; Testkorpora getrennt.

### B6. Inbetriebnahme, Bus, Diagnose

- [ ] Programmierauftrag-UI: Auftragstypen, Geräteauswahl, Fortschritt, Fehler, Retry, Queue-Abbruch.
- [ ] Schnittstellenkonflikt-UI: Scan/Monitor/Download klar sperren und erklären.
- [ ] Reale Busaktionen nur mit Freigabe und ehrlicher „Simulation“-Kennzeichnung.
- [ ] Linien-/Geräte-Scan und Ergebnisansicht gemäß ETS-Analyse.
- [ ] Bus-/Gruppenmonitor: Wertdialog, Lesen/Schreiben, Auto-Scroll, Pause, vollständiger Export.
- [ ] Änderungsverlauf/Audit und Projektprobleme als Werkzeug ergänzen.
- [ ] Secure-Übersicht, Schlüssel-/Zertifikatsgrenzen und Secure-Download ehrlich abbilden.

### B7. Berichte, Exporte, Demo

- [ ] CSV/HTML/Bericht um aktuelle V4-Felder und Zuordnungsmatrix ergänzen.
- [ ] Etiketten, Mail-Merge und semantische Exporte aus der Workbench erreichbar.
- [ ] V4-Demo-JSON vollständig importieren/exportieren; Reset und Beispieldaten.
- [ ] Vergleichsbericht und Produkt-Ersetzen in die Shell integrieren.
- [ ] Suche/Ersetzen projektweit mit Vorschau, Stale-Schutz und Undo.
- [ ] Alle Demo-Daten klar von echten KNX-Daten trennen.

### B8. Shell, Lokalisierung, Barrierefreiheit

- [ ] Englische Sprache für alle Workbench-Texte, Dialoge, Fehlermeldungen und dynamische Strings.
- [ ] Fokusführung nach Tabs, Dialogen, Suche und Projektwechsel; sichtbarer Fokus überall.
- [ ] Screenreader-Namen für Zeilen, Tabs, Icons, Tabellen und Statusmeldungen.
- [ ] 100/150/200-%-DPI und 900×600/1024×700/1440×900 abnehmen; keine abgeschnittenen Aktionen.
- [ ] Tool-Float/Maximieren/Close und Baum-Carets auf echte Material-Icons umstellen.
- [ ] Katalog, Parameter, Beziehungen und Dialoge auf eine Designsprache bringen.
- [ ] Dark/Light-Kontrast, Disabled, Empty, Error und Hover vollständig prüfen.

## C. Technische Leitplanken

- [ ] Keine erfundene ETS-Kompatibilität: native ETS-Only-Funktionen und Grenzen dokumentieren.
- [ ] Keine echten Bus-Schreib-/Downloadaktionen ohne ausdrückliche Freigabe.
- [ ] Keine Hersteller-/Kundendaten oder Zugangsdaten im öffentlichen Repo.
- [ ] Bestehende Tests und die 10.000-Objekt-Virtualisierung dürfen nicht regressieren.
- [ ] Jede Änderung mit fokussiertem Regressionstest und nachvollziehbarem Commit.
- [ ] Erst nach unabhängiger Review und grüner CI nach `development` integrieren.

## D. Abnahmematrix (Rest)

| ID | Nachweis | Status |
|---|---|---|
| R01 | ETS-REA-Berichte für alle fünf Bereiche mit Belegen | offen |
| R02 | Jede V4-Aktion aus `src/app.js` einer nativen Funktion/Testzeile zugeordnet | teilweise |
| R03 | Alle B1–B8-Punkte implementiert oder bewusst als nicht unterstützt dokumentiert | offen |
| R04 | Fokus/Screenreader/DPI/900–1440-Pixel nativ geprüft | offen |
| R05 | Englische Shell vollständig geprüft | offen |
| R06 | Katalog/Parameter/Beziehungen visuell konsistent | offen |
| R07 | Keine offenen P1/P2 aus Design-Reviews ohne dokumentierte Entscheidung | offen |
| R08 | Release-Build, alle Tests, Smoke, Screenshot, Workbench-Selftest, CI grün | teilweise |

## E. Arbeitsweise

1. REA-Analysen laufen read-only in getrennten Branches/Worktrees und liefern
   `analysis/ets/*.md` mit Belegen.
2. Danach je Bereich ein Implementierungsagent gegen den dann aktuellen
   Integrationsstand; Shared Contracts (WorkbenchWindow, Katalog, Parameter)
   haben genau einen Schreiber.
3. Main integriert, prüft und startet die nächste Welle erst nach stabilem
   Zwischenstand.
4. Jede erledigte Checkbox wird mit Commit/TEST-ID ergänzt.
