# BusChord V4 — Restplan und Todo-Liste

Stand: 2026-10-21. Basis: lokaler Branch `development`, aktueller Stand
`ac0480a` (Review-Basis `55ff6e1`, Clean-Code-Fixes danach).
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
- [x] 9. Gesamttests, unabhängige Abnahme, Dokumentation und CI abschließen.
- [x] 10. Geprüften Stand auf `development` integrieren und veröffentlichen.
- [x] 11. Clean-Code-Review des gesamten V4-Diffs; bestätigte Befunde beheben und Regressionstests ergänzen.
- [x] 12. Dark-Mode-Tooltips, Konzept-Icons, V4-Einstellungsfenster und ETS-Passwortöffnung abnehmen.
- [ ] 13. UI-Konsistenz aller Fenster, Menüs, Panes und Zustände gegen das V4-Bundle prüfen und Befunde beheben.

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

- [x] Keine erfundene ETS-Kompatibilität: native ETS-Only-Funktionen und Grenzen dokumentieren.
- [x] Keine echten Bus-Schreib-/Downloadaktionen ohne ausdrückliche Freigabe.
- [x] Keine Hersteller-/Kundendaten oder Zugangsdaten im öffentlichen Repo.
- [x] Bestehende Tests und die 10.000-Objekt-Virtualisierung dürfen nicht regressieren.
- [x] Jede Änderung mit fokussiertem Regressionstest und nachvollziehbarem Commit.
- [x] Erst nach unabhängiger Review und grüner CI nach `development` integrieren.

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
- Legacy-Bereinigung: MainWindow, GroupLinksPane, ungenutzte Legacy-Panes und Dock-Abhängigkeiten entfernt; Shell-/Docking-Tests laufen jetzt nativ gegen WorkbenchWindow.
- UI-Konsistenz-Welle: gemeinsame V4-Dialogfamilie (`f605870`), Search-/Parameter-/Catalog-Panes (`39be758`, Presenter-Fix `ab0f8a4`), Tooltips/Spinner/`wb-danger`, Screenshots für Settings/NewProject/Confirm/Splash geprüft; Demo-Parameterform und Shell-P2 in `84cb150`/`6b3fb83` nachgezogen, offene Reste in H1/H4.
- UI-Feinschliff: Dark-Tooltips mit lesbarem Text, Konzept-Linienicons für die Workbench und V4-Einstellungsfenster; ETS-Projektpasswort-Dialog und verifizierter `/21`-Leselauf für `Lostau.knxproj` (67 Geräte, 636 Gruppenadressen).
- Shell-Abnahme: Tabellen-Checkboxen, DCA-Read-only, Hilfefenster, Pinned Compare/Dokumentation und deterministischer Layout-Selftest (`84cb150`, `6b3fb83`, `286c025`); Remote-CI für `664fa5b` grün.
- Security/Lokalisierung: CSV-Formelinjektion behoben (`65c253d`), Projektprüfung/CSV/Report/Owl lokalisiert (`2846c16`, `ba25051`), Security- und i18n-Berichte integriert; Parser-/Cache-Grenzen und Fixture-Provenienz laufen als H10.
- Katalog-Performance (`49f3f22`, `33612e0`): persistenter SHA-256-revalidierter Metadatenindex, Suchindex ohne UI-Programmparse, lazy KSXML-Modelle; real gemessen warm 0,8–1,6 s statt 55–66 s, erster DPT-Suchlauf <1 ms statt 9,33 s; DCA-/Katalog-/Workbench-Suite grün.
- Bus-Hardware (`8b2b678`): CEMI-Gruppenflag/Hop-Count, APCI-/Payload-Offsets, Con-Filter und Gruppenantwort-Matching, Descriptor-Encoding und Multicast-Routing korrigiert; read-only Tunnel/Routing live geprüft, Schreibpfad bleibt gesperrt.
- QA (`7b80741`) und Fixture-Hygiene (`171ebca`): unabhängige Vollsuite grün, Katalogspalten/Fokus-P2 behoben (`26f596f`), reale/GPL-Fixtures durch synthetische ersetzt; Git-History-Rewrite bleibt offener Freigabepunkt H10.7.

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

## H. Offene Arbeitspakete (Stand `ac0480a`, 2026-10-21)

Diese Liste ist die vollständige Restliste. Ein Punkt gilt erst als erledigt,
wenn Implementierung, fokussierter Test und Abnahmeevidenz im Abschnitt
„Fortschritt“ stehen. Agentenstände sind Arbeitsstände, keine Abnahme.

### H1. Workbench-Shell und Interaktion (Schritte 3, 4, 13)

- [x] **H1.1 Inspector/Property-Grid:** Abschnitte, Footer, stabile Fokus- und
  Caret-Rückgabe, schmale Overlay-Komposition bei 1024/900. (`ac0480a`,
  `84cb150`; WorkbenchCheck/Fokuspfad)
- [x] **H1.2 Navigations-/Tab-Zustand:** Rail-Aktivfläche, 3-px-Akzent,
  Trennlinien, Issue-Badge und Monitor-Puls; Dokument-Tab-Akzent oben;
  Perspektiv-/Tool-Tab-Überlauf bei 900/1024. (`84cb150`, Screenshots
  1440/1024/900)
- [x] **H1.3 Objekttabelle:** Auswahl-Checkbox je Zeile, Select-all/Tri-State,
  GA-Chips, Zeilenkontextmenü und Tastaturäquivalenz; 10.000-Objekt-
  Virtualisierung bleibt getestet. (`84cb150`; WorkbenchUiState/Performance)
- [ ] **H1.4 Shell-Chrome:** Buttons/Felder im Shellfenster auf WbLine/WbPaper/
  WbHover statt ETS-Chrome; Statuszeile mit Projekt-/Issue-Hierarchie;
  Empty-States mit Icon, Überschrift und Erklärung. Teilweise in `84cb150`
  (Status/Empty/Rail); finale visuelle Abnahme offen.
- [x] **H1.5 Hilfe/Konzept:** scrollbares, lokalisiertes Hilfefenster statt
  Messagebox; alle Help-Abschnitte, F1/Escape, keine abgeschnittenen Texte.
  (`84cb150`, HelpWindow)
- [x] **H1.6 Tab-Semantik/A11y:** Dokument-, View- und Tool-Tabs als
  TabItem-Automation-Peers, ItemStatus, Namen für Icon-Steuerungen und
  Zeilen; Focus-Trapping im schmalen Inspector. (`84cb150`;
  A11y-Recheck2 als Restnachweis für echte UIA)
- [x] **H1.7 Drag-and-drop-Abnahme:** Geräte-Drop auf Linie/Segment und
  Tabellen-Drop, Undo/Raum-Erhalt, Tastaturweg. Implementiert in
  `ac0480a`; OS-Pointer-Drag ist headless nicht beweisbar, daher
  Projektions-/Command-Test plus dokumentierte Grenze.
- [x] **H1.8 Favoriten/Nebenfenster:** Pinning, Side-by-side-Fenster,
  Dedup/Refresh/Close-Lebenszyklus. (`ac0480a`, `84cb150`;
  WorkbenchCheck-Companion/Pin-Tests)

### H2. Dialogfamilie und gemeinsame Panes (Schritte 3, 13)

- [x] **H2.1 Einheitliche Dialogsprache:** gemeinsames V4-Fenster-/Dialog-Chrome
  mit `WbDialogRadius`, 22-px-Inhalt, 13-px-Footer, Titelhierarchie für
  NewProject, Compare, Replace, Report, ConfirmDialog und Splash. (`f605870`)
- [x] **H2.2 Aktionsreihenfolge und Destruktives:** primär rechts, Cancel
  quiet, Delete/Verwerfen mit `wb-danger`, kein destruktiver Default-Fokus;
  Save/Don't-Save/Cancel in ConfirmDialog sicher. (`f605870`)
- [ ] **H2.3 Englisch/Lokalisierung:** harte deutsche Strings in Compare,
  Replace, Report, Confirm, Splash und dynamischen Statusmeldungen entfernen;
  `de` bleibt Default, `en` vollständig.
- [x] **H2.4 SearchPane:** V4-Compact-Tool statt Legacy-Full-Page; Filter/
  Ergebnisse primär, Ersetzen aufklappbar, keine Mini-Scrollboxen; Suche/
  Ersetzen/Stale-Schutz/Undo unverändert. (`39be758`, `ab0f8a4`)
- [x] **H2.5 ParameterPane:** `wb-root`, `wb-field`, einheitliche
  `wb-label`-Hierarchie, Abschnittsnavigation, Sticky-Footer, Draft-/Read-only-
  Zustände; Demo-Form und natives Pane teilen dieselbe Feldbreite/Aktionen.
  (`39be758`, `6b3fb83`; DemoForm nutzt dieselbe Geometrie/Validierung,
  vollständige Pane-Wiederverwendung ist als nicht sinnvoll dokumentiert)
- [x] **H2.6 CatalogPane-Rest:** Filterbuttonhöhe/Label-Hierarchie an die
  30-px-Felder angleichen; keine Funktionsänderung. (`39be758`)

### H3. Parameter, DCA und Herstellergrenzen (Schritt 5)

- [ ] **H3.1 DCA-Host-Verdrahtung:** `MainWindowViewModel` liefert einen
  lokalisierten Read-only-Grund aus dem Katalogprogramm
  (`CatalogBrowser.Blocked(..., CatalogUse.EditParameters)`); Workbench setzt
  ihn nach jedem Gerätewechsel/Refresh und bei `CatalogLoaded`; Editoren,
  Apply und Reset bleiben aus, Werte unverändert. Das ist der noch fehlende
  Produktpfad aus B4/DCA.
- [ ] **H3.2 DCA-Regressionstest:** synthetisches `RequiresDca`-Programm,
  Read-only-Banner, unveränderte Werte, kein Apply; Catalog-cold-Fall.
- [ ] **H3.3 Opaque/unsupported:** bestehende Opaque-Erkennung und
  Warnungen nicht regressieren; keine Herstellerdynamik ausführen.

### H4. Abnahme und Dokumentation (Schritte 8, 9, 13; Matrix R01–R08)

- [ ] **H4.1 R01:** ETS-REA-Berichte für alle fünf Bereiche mit Belegen
  verifizieren und als Nachweis verlinken (die Dateien existieren; Status
  „offen“ in der Matrix ist zu korrigieren).
- [ ] **H4.2 R02:** jede V4-Aktion aus `src/app.js` einer nativen
  Implementierung/Testzeile oder einer bewussten Grenze zuordnen.
- [ ] **H4.3 R03:** alle B1–B8-Punkte implementieren oder als nicht
  unterstützt dokumentieren; offene Checkboxen oben sind die Restliste.
- [ ] **H4.4 R04:** Fokus/Tab-Reihenfolge, Screenreader-Namen, 900×600/
  1024×700/1440×900, simulierte 150/200-%-DPI und High-Contrast prüfen.
  Echte Narrator-/UIA- und gemischte DPI-Läufe bleiben als Plattformgrenze
  dokumentiert, solange sie nicht auf realer Hardware ausgeführt wurden.
- [ ] **H4.5 R05:** englische Shell, Dialoge, Panes, Fehler- und dynamische
  Strings automatisiert auf fehlende Keys/Deutsch prüfen; `--lang=en`-
  Selftest und Screenshot.
- [ ] **H4.6 R06:** Katalog/Parameter/Beziehungen/Dialoge auf eine
  Designsprache; Screenshots light/dark bei 1440/1024/900.
- [ ] **H4.7 R07:** jede P1/P2 aus Tokens-, Workbench-, Panes- und
  A11y-Recheck beheben oder mit Entscheidung/Grenze dokumentieren; keine
  stillen Reste. Referenzen:
  `analysis/ui-consistency/tokens.md`,
  `analysis/ui-consistency/workbench.md`,
  `analysis/ui-consistency/panes.md`,
  `analysis/workbench-v4-a11y-recheck2.md`.
- [ ] **H4.8 R08:** Release-Build, alle Testprojekte, App-Tests, Smoke,
  Screenshot-Selftest, Workbench-Selftest de/en, Remote-CI und CodeQL grün;
  lokale und Remote-SHAs dokumentieren.
- [ ] **H4.9 Statusmatrix:** `analysis/workbench-v4-status-matrix.md` auf
  aktuellen Stand bringen; veraltete „partial/missing“-Zeilen (u. a. 34, 64)
  nur mit Testbeleg schließen.

### H5. Katalog-Ladezeit und Suche (neu, 2026-10-21)

**Befund:** Die lokale Bibliothek umfasst 124 Kataloge mit 429,5 MB
(größter 88,5 MB). `CatalogBrowser.Filter` löst bei jeder Suche für jedes
Produkt `FindProgramFor` auf (`CatalogBrowser.cs:169`); bei Split-Archiven
lädt das alle Application-Programme samt `Ksxml.Parse` nach. `CatalogStore.LoadAll`
parst außerdem beim Start alle Dateien sequenziell neu, ohne persistenten
Index. `Programs.Any`/`FindProduct` sind lineare Suchen. Das ist der
Hauptgrund für die wahrgenommene Ladezeit; nicht die ZIP-Entpackung allein.

- [x] **H5.1 Messung:** lokale Bibliothek kalt/warm messen: Dateiscan,
  ZIP-Öffnen, XML-Parse, Programminitialisierung, erster Suchlauf,
  UI-Blockade und Speicher. Nachweis in
  `analysis/catalog-load-performance.md` (`c84cb29`): erster Suchlauf
  2,4–4,8 s und 559 MB, `LoadAll` 124 Dateien kalt 11,2 s, warm 1,2–1,9 s,
  Programmteil 0,6–1,9 s/140 MB.
- [x] **H5.2 Suchpfad entkoppeln:** `CatalogBrowser.Filter` ruft kein
  `FindProgramFor` mehr auf; Produkt-/Programm-/COM-Metadaten werden im
  Suchindex durchsucht. (`49f3f22`; erster DPT-Suchlauf <1 ms statt 9,33 s)
- [x] **H5.3 Persistenter Index:** gzip-JSON-Metadatenindex mit
  Datei-Identität + SHA-256-Revalidierung, atomarem Schreiben und
  Schema-Version; warmstart parst kein XML. (`49f3f22`, `33612e0`)
- [x] **H5.4 Lazy Programs:** KSXML-Parameter werden erst bei
  `FindProgram`/`FindProgramFor` materialisiert; Split- und Monolith-Archive
  teilen den Pfad. (`49f3f22`, `33612e0`)
- [x] **H5.5 Progressive UI:** Basismetadaten, programmangereicherte
  Metadaten und stabile Snapshots werden getrennt veröffentlicht; Reload
  veröffentlicht keinen halben Snapshot. (`49f3f22`)
- [x] **H5.6 Algorithmik:** Dictionaries, einmaliges Master-Lesen,
  Single-Pass-Translations, kein `XElement`-Klon, begrenzt parallele
  Warmindex-Reads. (`49f3f22`)
- [ ] **H5.7 Abnahme:** Kaltstart- und Warmstart-Zielwerte, erster Suchlauf,
  Speicherverbrauch und 10.000-Objekt-Pfad testen; Cache-Invalidierung nach
  Import/Entfernen/Änderung nachweisen. Stand `33612e0`: warm 0,8–1,6 s
  (Ziel <300 ms noch offen), Kaltindex 113,6 s (progressiv statt blockierend,
  Ziel <3 s offen), Parameter-Öffnen 0,6–1,9 s (Agent
  `agents/perf/parameter-open`).

### H6. Frameless / keine native Titelleiste (neu, 2026-10-21)

- [x] **H6.1 Design und API-Nachweis:** `analysis/ui-frameless-window-design.md`
  mit Wireframe, Avalonia-12-API (`WindowDecorations`,
  `ExtendClientAreaToDecorationsHint`, `ElementRole`), Windows-Hit-Test-/
  Snap-/Resize-Verhalten und Dialogentscheidung. (`33ee3dc`; S0-Spike
  `agents/ui/frameless-spike` noch offen)
- [ ] **H6.2 Shell-Chrome:** Marke, Menüs, Projektsuche, Status und
  Fensteraktionen (Minimieren/Maximieren/Wiederherstellen/Schließen) in
  einem optisch integrierten, fokussierbaren Topstrip; native Titelleiste
  entfällt nur, wenn Drag, Doppelklick, Snap, Resize und Systemmenü erhalten
  bleiben.
- [ ] **H6.3 Barrierefreiheit:** Automationsnamen, Fokusreihenfolge,
  Zielgrößen, Tastatur (Alt+F4/Alt+Space), Screenreader und High Contrast.
- [ ] **H6.4 Dialoge:** einheitliche Entscheidung für Workbench und modale
  Fenster; Schließen/Abbrechen darf nicht erschwert werden.
- [ ] **H6.5 Reale Windows-Abnahme:** 100/150/200-%-DPI, Multi-Monitor,
  Snap Layouts, Maximieren-Padding, Taskleiste, Screenshots; nicht nur
  headless.

### H7. Veröffentlichung (Schritt 10, R08)

- [ ] **H5.1** Alle Commits nach `origin/development` pushen; Remote-CI und
  CodeQL abwarten und Ergebnisse hier eintragen.
- [ ] **H5.2** Plan-Checkboxen 3–6, 8 und 13 erst nach belegter Abnahme
  schließen; `ac0480a` und die Integrations-SHAs dokumentieren.
- [ ] **H5.3** Agenten-Worktrees/Branches nach bestätigter Integration und
  ohne ungesicherte Änderungen aufräumen; offene Grenzen stehen lassen.

### Bekannte P1/P2-Quellen (nicht doppelt abhaken)

- `analysis/ui-consistency/tokens.md`: Auswahl-Tokens, Disabled-Kontrast,
  `quiet`, Settings-Felder; P2/P3 zu Höhen, Muted-Größe, Legacy-Borders,
  Empty-States, Toolbar, Breakpoints, Spinner, Radien, Hover, Zeilenhöhen,
  Tooltip und Tab-Font.
- `analysis/ui-consistency/workbench.md`: P1 Programmierung/Inspector/
  Tool-Tabs/Perspektiven; P2 Rail, Tab-Regel, Shell-Chrome, Dialoge,
  Englisch, Dock, Tabellenaffordanzen, Hilfe.
- `analysis/ui-consistency/panes.md`: P1 SearchPane; P2 Parameter-Duplikat,
  Dialogfamilie, Label-Hierarchie, Aktionsreihenfolge, destruktive
  Bestätigung.
- `analysis/workbench-v4-a11y-recheck2.md`: Overlay-Fokus, Footnote,
  Tab-Rollen/Status, fehlende Namen, Such-Undo, Zeilennamen, MinHeight,
  Perspektivnamen.
- `analysis/workbench-v4-design-visual.md` und
  `analysis/workbench-v4-ui-recheck.md`: historische P1/P2; nur noch offene
  Punkte übernehmen, bereits belegte nicht erneut öffnen.

### H8. Reale KNX-Bus-Lesetests (neue Freigabe, 2026-10-21)

- [x] **H8.1 Testplan:** passive Discovery, Tunnel-Verbindung, Geräte-/Adresslesen,
  passiver Gruppenmonitor und explizites Gruppenwert-Lesen mit Abbruchkriterien,
  Wiederholungen und Evidenz. Nur Lesen; keine Downloads, Programmierung,
  Adressschreibung, Gruppenwert-Schreibung, Resets oder Secure-Inbetriebnahme.
  (`664fa5b`, `analysis/knx-bus-read-test-plan.md`)
- [x] **H8.2 Schnittstelle:** lokale Konfiguration/Gateway prüfen, private
  Adressen in Commits redigieren, keine Passwörter/Tokens protokollieren.
  (`664fa5b`; Gateway erkannt, Adressen redigiert)
- [x] **H8.3 Ist-Abgleich:** reale Bus-APIs gegen Workbench-Diagnose, Monitor,
  Scan und Programmierung abgleichen; Demo-only-Pfade klar kennzeichnen.
  (`664fa5b`; P0-CEMI/APCI/Descriptor/Routing-Gaps dokumentiert,
  Fixagent `agents/bus/cemi-fix`)
- [ ] **H8.4 Abnahme:** reproduzierbare Read-only-Ergebnisse,
  Timeout-/Reconnect-/Schnittstellenkonflikt-Verhalten und dokumentierte
  Grenzen; echte Schreibfreigabe bleibt separat und ausdrücklich.
  Stand `8b2b678`: Tunnel/Routing read-only live geprüft, Timeout- und
  Con-Filter-Verhalten belegt; kein Gruppenantwort-/Descriptor-Livescan und
  kein zweiter Tunnel (Schnittstellenkonflikt) getestet.

### H9. Mehrsprachigkeit und Übersetzungsinfrastruktur (neu, 2026-10-21)

**Ist-Stand:** Deutsch ist Vorgabe; Englisch ist über zwei Code-Dictionaries
(`Knx.Strings`, `WorkbenchStrings`) und `--lang=`/Einstellungen umschaltbar.
Nach `2846c16` sind Projektprüfung, CSV-Header und HTML-Bericht lokalisiert.
Es gibt noch keine Ressourcendateien, keine dritte Sprache, keine
Plural-/Format-Infrastruktur, keinen Übersetzer-Workflow und keine vollständige
automatische Hardcoded-Text-Prüfung.

- [x] **H9.1 Architektur:** Ressourcenformat, stark typisierte Keys, Fallback,
  Runtime-Sprachwechsel, Reports/CSV, Tests und Trimming/AOT entscheiden.
  (`da128e5`, `analysis/i18n-architecture-plan.md`)
- [x] **H9.2 Inventar:** alle UI-/Report-/CSV-/Fehlertexte mit Datei:Zeile,
  Key-Status und Priorität erfassen; deutsche Pfad-Tokens und dynamische Texte
  getrennt behandeln. (`5b7bb25`, `analysis/i18n-text-inventory.md`:
  908 Keys, 155 tote Keys, 4 Kollisionen, Sprachwechsel-Gap)
- [x] **H9.3 Workflow:** Übersetzerformat, Glossar, Review, Key-Stabilität,
  Community-/CLA-Regeln, keine Hersteller-/Kundendaten, unvollständige
  Sprachen klar kennzeichnen. (`cb7e996`,
  `analysis/i18n-translation-workflow.md`; offene Produktentscheidungen dort)
- [ ] **H9.4 CI-Gates:** Key-Parität, leere/verwaiste Keys, Platzhalter,
  verbotene deutsche Literale in `en`, Pseudo-Lokalisierung, Report-/CSV-
  Sprachtests und Screenshots.
- [ ] **H9.5 Pilot:** dritte Sprache (z. B. Französisch oder Niederländisch)
  end-to-end mit echten Ressourcen, Tests und Abnahme; keine Maschinen-
  übersetzung ohne Review.
- [ ] **H9.6 Grenzen:** Projekt-/Gerätenamen, Herstellertexte und technische
  CSV-Werte bleiben Daten, nicht UI-Sprache; DPT-/KNX-Codes werden nicht
  übersetzt.
- [x] **H9.7 Live-Sprachwechsel:** `{loc:Tr}`/`{loc:Wb}` sind bindende
  Markup-Extensions; Menüs, Rail, View-/Tool-Tabs, Tooltips und
  Automationsnamen werden beim Sprachwechsel neu aufgebaut. (`a8aba25`;
  Shell-Test de→en→de; Help-Body/Float-Titel brauchen weiterhin ein
  erneutes Öffnen)

### H10. Security-Härtung (neu, 2026-10-21)

Quelle: `analysis/security-final.md` (`54ecc4a`). Kein P1 gefunden; P2
SEC-1–SEC-3 sind vor einem Release zu beheben.

- [x] **H10.1 CSV-Formelinjektion:** gemeinsamer Escaper für alle sechs
  Exporte, Felder mit `= + - @`, Tab/CR und führenden Leerzeichen
  neutralisieren; Regressionstests. (`65c253d`, `src/Knx.Core/Csv.cs`;
  Tests in App/Core/Bus/Monitor)
- [x] **H10.2 Katalog-Parser-Grenzen:** maximale XML-/Archivgröße,
  Verschachtelungstiefe und entpackte Daten begrenzen; Stack-Overflow und
  Speichererschöpfung durch feindliche Kataloge verhindern.
  (`49f3f22`: Tiefe 64, XML-/Archivlimits, 260-MB-Bombentest)
- [x] **H10.3 Cache-Vertrauen:** Identität/Hash statt nur mtime+Größe,
  keine absoluten Pfade aus dem Cache übernehmen, Extraktion begrenzen,
  Temp-Dateien aufräumen. (`49f3f22`, `33612e0`: SHA-256-Revalidierung,
  Hex-Archivnamen, atomare Writes, Lazy-Model-Restore)
- [ ] **H10.4 DCA-Fallback:** Projekt-XML-Applikationen ohne Katalog dürfen
  nicht versehentlich editierbar werden; Read-only-Grund erhalten.
- [x] **H10.5 Fixtures/Provenienz:** `knx_master.xml`, `.knxproj`-Fixtures
  und Testkorpora auf Hersteller-/Kundendaten und Lizenz prüfen; durch
  synthetische Minima ersetzen, wo nötig. (`171ebca`: 1-MB-KNX-Master und
  GPL-Fixture entfernt, synthetische Fixtures + Provenienz-README; History
  enthält die alten Blobs weiterhin → Rewrite-Entscheidung offen)
- [ ] **H10.6 Release-Entscheidung:** verbleibende P3
  (Klartext-Backups, `CatalogUse.Download`, Import-Atomarität,
  `.signature`) dokumentieren oder beheben.
- [ ] **H10.7 Git-History:** Die entfernten Fixtures (`knx_master.xml`,
  GPL-`.knxproj` inkl. eingebetteter Zugangsdaten) liegen weiterhin in der
  Historie. Vor einem Public-Release History-Rewrite (filter-repo/BFG) und
  Force-Push nur mit ausdrücklicher Freigabe; betroffene Test-Zugangsdaten
  rotieren. HEAD ist clean, Historie nicht.

## Bewusst nicht nachgebaut (dokumentierte Grenzen)

- DCA/Plugin-Hosting: read-only Erkennung und Sperre, kein In-Process-Host (32-bit-ETS, COM/.NET-Framework, keine Sandbox).
- Secure-Inbetriebnahme: Keyring-/Zertifikatspruefung und Secure-Download bleiben ausserhalb; keine Schluessel- oder FDSK-Verwaltung.
- Reale Downloads/Adressschreibung: der bestehende Download-Writer ist gesperrt, bis Descriptor-/Mask-/Programming-Mode-Pruefung implementiert und freigegeben ist.
- USB/RDAC, Cloud-/IoT-Connectoren, ETS-Local-Store-Versionierung und vollstaendige Multi-Projekt-Tabs: nicht in dieser Ausbaustufe.
- DCA-/Skript-Dynamik (JavaScript/VBScript/ActiveScript), Module/Allocators/LoadProcedures und vollstaendige Hersteller-Seitenlayouts: opaque erhalten, nicht ausgefuehrt.
- Online-Katalog/Markt/Signaturpruefung und Produkt-Update per ID: lokaler Store/Import bleibt massgeblich.
