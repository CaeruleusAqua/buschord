# BusChord V4 — Restplan und Todo-Liste

Stand: 2026-10-21. Basis: lokaler Branch `development`, Review-Basis `55ff6e1`, Clean-Code-Fixes danach.
Referenz: `C:\Users\julia\Downloads\knx-workbench-v4-bundle`.
Dieser Plan führt die verbleibenden Arbeiten aus dem V4-Konzept, den Design-Reviews
und der ETS-Paritätsanalyse. Er ist die maßgebliche Todo-Liste; erledigte Punkte
werden hier abgehakt, nicht nur im Chat.

## Steps

- [x] 1. ETS-Referenz je Funktionsbereich mit REA neu analysieren und dokumentieren.
- [x] 2. V4-Restlücken aus Akzeptanzmatrix, UX- und Visual-Review konsolidieren.
- [ ] 3. Shell/Design abschließen: Katalog, Icons, Fokus, DPI, Dark/Light, Englisch.
- [ ] 4. Projekt-, Raum-, Linien-, Geräte- und Gruppenadressfunktionen vervollständigen.
- [ ] 5. Katalog, Parameter, dynamische Herstellerlogik und DCA-Grenzen abschließen.
- [ ] 6. Inbetriebnahme, Download, Diagnose, Monitor und Secure vollständig anbinden.
- [x] 7. Berichte, Vergleich, Suche/Ersetzen, Exporte und Demo-Abläufe abschließen.
- [ ] 8. Tastatur, Screenreader, 100/150/200-%-DPI und kleine Fenster abnehmen.
- [ ] 9. Gesamttests, unabhängige Abnahme, Dokumentation und CI abschließen.
- [ ] 10. Geprüften Stand auf `development` integrieren und veröffentlichen.
- [x] 11. Clean-Code-Review des gesamten V4-Diffs; bestätigte Befunde beheben und Regressionstests ergänzen.
- [x] 12. Dark-Mode-Tooltips, Konzept-Icons, V4-Einstellungsfenster und ETS-Passwortöffnung abnehmen.

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

- [x] Startfläche vollständig: Projekt öffnen/neu, Katalog, letzte Projekte, Demo importieren, Demo zurücksetzen, Suche.
- [x] Projekt vergleichen und Produkt ersetzen aus der Workbench erreichbar machen.
- [x] Projekt-Reset mit definierter Behandlung laufender Monitor-/Programmieraufträge.
- [x] Layout-Persistenz je Projekt und Fensterpositionen prüfen; Reset ohne Projektdatenverlust.
- [x] Projektwechsel-/Undo-/Entwurfsregeln als vollständige Transaktion absichern; fehlgeschlagene Ersetzung erhält alle Sitzungen.
- [x] Mehrere Projekte/Instanzen gemäß ETS-Analyse bewerten; mindestens ein sicheres Einzelprojekt-Verhalten dokumentieren.

### B2. Navigator und Arbeitsgegenstände

- [x] Alle Baumaktionen: anlegen, umbenennen, löschen, verschieben, kopieren, Adresse ändern, Kontextmenüs.
- [ ] Drag-and-drop für Geräte/Adressen mit gleichwertigem Tastaturweg.
- [x] Linien-/Segment-/Raumwechsel inklusive Undo und Adressvorschlägen.
- [x] Navigationszustand, Filter, Aufklappzustand und Auswahl je Perspektive vollständig persistieren.
- [ ] "In Topologie zeigen", "Daneben öffnen", Favoriten/Pinning gemäß ETS-Ablauf.
- [x] Externe Kanäle/Effekt-Raumbezug im UI vollständig erklären und prüfen.

### B3. Tabelle, Inspektor, Zuordnungen

- [x] Tabellen-Mehrfachauswahl mit Bereichsauswahl (Shift), Alles auswählen, Tastatur.
- [x] Vollständige GA-Gegenansicht und Beziehungseditor für Gerät/KO/GA.
- [x] Atomare Mehrfachzuordnung mit Vorprüfung, Vorschau, Duplikatbehandlung und Undo (UI-Abschluss).
- [x] Sendeadresse setzen/entfernen, Warnung bei verbleibenden Links, keine stille Ersatzwahl.
- [x] KO-Aktivierung/Flags/DPT/Länge editierbar oder bewusst schreibgeschützt kennzeichnen.
- [x] Long-list-Darstellung, Zähler und vollständiger Zuordnungseditor für >10.000 Objekte.
- [ ] Inspector als Property-Grid mit Abschnitten, Fußaktionen und stabiler Fokusführung.

### B4. Parameter

- [ ] Native Parameter-UI vollständig V4-stylen; Abschnittsnavigation und Footer.
- [ ] Entwurfsindikator sichtbar; Undo/Projektwechsel/Refresh verwirft nur mit klarer Meldung.
- [x] Wirkungsvorschau für alle unterstützten KO-/Link-Änderungen; unbekannte Herstellerdynamik/DCA klar trennen.
- [x] Demo-Schalten/Dimmen vollständig mit Vorschau, Apply, Undo und Linkerhalt.
- [x] Fehler-/Validierungszustände je Feld und Sammelfehler im Footer.
- [ ] DCA-erforderliche Geräte read-only kennzeichnen und opaque Daten erhalten.

### B5. Katalog

- [x] `CatalogPane` vollständig auf V4-Design heben (Panel, Zeilen, Buttons, Suche, Leerzustände, Tastatur).
- [x] Import/Reload-Fortschritt, Fehler, Abbruch und Hintergrundladen sichtbar.
- [x] Einfügen in gewählten Raum/Line/Segment inkl. Adressvorschlag, Namenskonflikt und Undo.
- [x] Produkt-/Applikationsvorschau, DPT-/KO-Metadaten und unbekannte Herstellerdaten erklären.
- [x] Suche/Filter über Katalog, Hersteller, Produkt, Text und DPT.
- [x] Keine Herstellerdaten im öffentlichen Repository; Testkorpora getrennt.

### B6. Inbetriebnahme, Bus, Diagnose

- [x] Programmierauftrag-UI: Auftragstypen, Geräteauswahl, Fortschritt, Fehler, Retry, Queue-Abbruch.
- [x] Schnittstellenkonflikt-UI: Scan/Monitor/Download klar sperren und erklären.
- [x] Reale Busaktionen nur mit Freigabe und ehrlicher „Simulation“-Kennzeichnung.
- [x] Linien-/Geräte-Scan und Ergebnisansicht gemäß ETS-Analyse.
- [x] Bus-/Gruppenmonitor: Wertdialog, Lesen/Schreiben, Auto-Scroll, Pause, vollständiger Export.
- [x] Änderungsverlauf/Audit und Projektprobleme als Werkzeug ergänzen.
- [x] Secure-Übersicht, Schlüssel-/Zertifikatsgrenzen und Secure-Download ehrlich abbilden.

### B7. Berichte, Exporte, Demo

- [x] CSV/HTML/Bericht um aktuelle V4-Felder und Zuordnungsmatrix ergänzen.
- [x] Etiketten, Mail-Merge und semantische Exporte aus der Workbench erreichbar.
- [x] V4-Demo-JSON vollständig importieren/exportieren; Reset und Beispieldaten.
- [x] Vergleichsbericht und Produkt-Ersetzen in die Shell integrieren.
- [x] Suche/Ersetzen projektweit mit Vorschau, Stale-Schutz und Undo.
- [x] Alle Demo-Daten klar von echten KNX-Daten trennen.

### B8. Shell, Lokalisierung, Barrierefreiheit

- [ ] Englische Sprache für alle Workbench-Texte, Dialoge, Fehlermeldungen und dynamische Strings.
- [ ] Fokusführung nach Tabs, Dialogen, Suche und Projektwechsel; sichtbarer Fokus überall.
- [ ] Screenreader-Namen für Zeilen, Tabs, Icons, Tabellen und Statusmeldungen.
- [ ] 100/150/200-%-DPI und 900×600/1024×700/1440×900 abnehmen; keine abgeschnittenen Aktionen.
- [x] Tool-Float/Maximieren/Close und Baum-Carets auf die Konzept-Linienicons umstellen.
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

## Fortschritt

- ETS-REA-Berichte: `analysis/ets/project.md`, `addresses.md`, `catalog-parameters.md`, `commissioning.md`, `shell.md` (alle belegt, 2026-10-21).
- V4-Statusmatrix: `analysis/workbench-v4-status-matrix.md`; Design-/A11y-Berichte: `analysis/workbench-v4-design-*.md`, `analysis/workbench-v4-a11y-dpi.md`.
- Umgesetzt: Connector-/Adressmodell, Katalogstatus-Metadaten und Gating, Importbericht, Download-Sicherheitsgate, simulierte Scan-/Monitorwert-/Änderungswerkzeuge, GA-Formular, Geräte-Detailbearbeitung, Projektbackup/-wiederherstellung, Geräte-Ladezustand, Sidebar-Container, englische Shell, Material-Icons, Katalog-V4-Styling, Projektvergleich/-Produktaustausch, Labels-/Semantik-Export, Projekt-Suche/-Ersetzen, Demo-Reset, Werkzeugübersicht und GA-Gegenansicht.
- Recheck-Fixes (`43cff3c`, `e8d83b5`, `1c94f74`): linkable neue GA mit DPT-Bitgröße, Segmenterhalt beim Geräte-Detailedit, tote Suchergebnisse, Katalog-Raumzuordnung, gerätegebundener KO-Monitorfilter, Inspector-Elevation/Dock-Höhe, Werkzeugtexte, Leerzustände und englische Resttexte.
- Abnahme 2026-10-21: Release-Build, alle Testprojekte, Smoke, Screenshot-Selbsttest sowie Workbench-Selftest de/en grün; UI- und Funktions-Recheck als `analysis/workbench-v4-ui-recheck.md` und `analysis/workbench-v4-functional-recheck.md` integriert.
- Clean-Code-Review: `analysis/clean-code-review/core.md`, `app.md`, `tests-docs.md`; bestätigte P1/P2 und mehrere P3 behoben, verbleibende Grenzen in den Berichten dokumentiert.
- Plan-Wellen: atomare Projektoperationen, Katalog-Ziel-/Such-/Importabläufe, Parameter-Footer/Validierung, Session-Cancel/Monitor-Pause/Diagnose, Baum-Kontextmenüs und projektbezogene Layouts; A11y-Abnahme als `analysis/workbench-v4-a11y-final.md`.
- UI-Feinschliff: Dark-Tooltips mit lesbarem Text, Konzept-Linienicons für die Workbench und V4-Einstellungsfenster; ETS-Projektpasswort-Dialog und verifizierter `/21`-Leselauf für `Lostau.knxproj` (67 Geräte, 636 Gruppenadressen).

## F. Clean-Code-Review (Schritt 11)

- [x] Core/Bus: Atomarität, Pfad-/Fehlerbehandlung, Duplikate, toter Code, API-Kompatibilität.
- [x] App/Workbench: Riesenmethoden, wiederholte UI-Builder/Switches, Event-Lecks, Thread-/Projektwechsel-Zustand.
- [x] Tests/Doku: nicht-falsifizierbare Checks, fehlende Regressionen, CI-/Doku-Abweichungen.
- [x] Bestätigte P1/P2 beheben, Regressionstest ergänzen, Gesamtsuite erneut ausführen.

## G. UI-Feinschliff (Schritt 12)

- [x] ETS-Projektpasswort beim Öffnen abfragen, ZIP-Schlüssel ableiten, `/21` lesen.
- [x] Dark-Mode-Tooltips lesbar.
- [x] Konzept-Icons statt gefüllter Material-Icons in der Workbench.
- [x] Einstellungsfenster im V4-Design.

## Offene Restpunkte nach dieser Welle

- Drag-and-drop im Navigator; Geräte/GA lassen sich über Kontextmenüs und Tastatur verschieben/kopieren.
- Nebenfenster/Favoriten/Pinning und vollständige Side-by-side-Ansichten.
- Parameter-Abschnittsnavigation, Projektwechsel-Hinweis für Entwürfe und DCA-Read-only-Markierung.
- Echte UIA-/Narrator-Abnahme, reale 150/200-%-DPI und High-Contrast-Pass.
## Bewusst nicht nachgebaut (dokumentierte Grenzen)

- DCA/Plugin-Hosting: read-only Erkennung und Sperre, kein In-Process-Host (32-bit-ETS, COM/.NET-Framework, keine Sandbox).
- Secure-Inbetriebnahme: Keyring-/Zertifikatspruefung und Secure-Download bleiben ausserhalb; keine Schluessel- oder FDSK-Verwaltung.
- Reale Downloads/Adressschreibung: der bestehende Download-Writer ist gesperrt, bis Descriptor-/Mask-/Programming-Mode-Pruefung implementiert und freigegeben ist.
- USB/RDAC, Cloud-/IoT-Connectoren, ETS-Local-Store-Versionierung und vollstaendige Multi-Projekt-Tabs: nicht in dieser Ausbaustufe.
- DCA-/Skript-Dynamik (JavaScript/VBScript/ActiveScript), Module/Allocators/LoadProcedures und vollstaendige Hersteller-Seitenlayouts: opaque erhalten, nicht ausgefuehrt.
- Online-Katalog/Markt/Signaturpruefung und Produkt-Update per ID: lokaler Store/Import bleibt massgeblich.
