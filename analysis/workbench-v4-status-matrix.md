# V4 action status matrix

Stand: 2026-10-21, Basis `55ff6e1` (V4-Workbench plus first implementation waves). Quellen: `src/app.js`-Aktionen
aus `analysis/workbench-v4-acceptance.md`, aktueller `WorkbenchWindow`-Code,
Domain-/Session-Tests. Status:
**done** = im Produktpfad vorhanden und geprüft; **partial** = vorhanden, aber
Ablauf/Validierung/UX unvollständig; **missing** = nicht erreichbar.

## Delta nach Welle A/B (2026-10-21)

Neu **done**: 18 (alles auswählen), 19 (Auswahl aufheben), 24 (GA-Gegenansicht im Inspektor),
32 (In Topologie zeigen), 37 (Gerät Name/Adresse/Linie/Raum), 38/39 (GA-Formular), 44 (Werkzeugübersicht),
51–53 (simulierter Monitorwert lesen/schreiben), 57 (Linien-Scan), 59 (Änderungsverlauf), 76/77 (Demo-Reset).
Weiter **partial/missing**: 04/17 (KO-Detail/Checkbox), 07 (Dokumentationsansicht), 20/22 (tiefer Inspektor),
26 (eingefrorene Auswahl), 34 (sichtbarer Verwerfen-Button für native Parameter), 60 (alle zehn Werkzeugarten),
64 (Selektion im Menü), 69 (vollständiges Konzept statt Hilfe-Dialog).
Projekt-Tabs, Panel-Selektor mit sechs Panels und Multi-Projekt-Betrieb bleiben als bewusste
Ein-Platzhalter-/Ein-Projekt-Grenze dokumentiert.

| # | Aktion | Status | Ort / Restlücke |
|---:|---|---|---|
| 01 | open-scope | done | `OpenTarget`, drei Navigatorperspektiven |
| 02 | open-device | done | Geräte-Tab |
| 03 | open-ga | done | GA-Tab |
| 04 | open-ko | partial | Tabellenselektion; kein direkter KO-Tab/Reveal |
| 05 | search-open | done | Befehlssuche öffnet Ziele |
| 06 | open-project | done | Projekttab |
| 07 | open-docs | missing | Dokumentationsansicht |
| 08 | open-tools | partial | Werkzeugleiste; keine Übersicht aller Werkzeuge |
| 09 | nav-kind | done | Gebäude/Topologie/Gruppen |
| 10 | toggle-nav | done | Ctrl+B, Rail |
| 11 | activate-tab | done | Objekttabs |
| 12 | close-tab | done | Tab-Schließen, Fokus offen |
| 13 | view | done | vier Darstellungen |
| 14 | collapse-device | done | Tab-lokaler Aufklappzustand |
| 15 | expand-all | done | alle Geräte |
| 16 | select-ko | done | Mehrfachauswahl |
| 17 | check-ko | partial | Checkbox-Auswahl fehlt, ListBox-Mehrfachauswahl vorhanden |
| 18 | select-all | done | Menü „Alles auswählen“ |
| 19 | clear-selection | done | Menü „Auswahl aufheben“ |
| 20 | inspect-ko | partial | Inspektor, aber keine vollständige KO-Detailansicht |
| 21 | inspect-device | done | Geräteinspektor |
| 22 | inspect-selected | partial | Mehrfachauswahl-Hinweis, keine Sammelaktion |
| 23 | close-side | done | Inspector schließen |
| 24 | compare | done | GA-Gegenansicht im Inspektor |
| 25 | link-selected | done | atomarer Batch-Dialog |
| 26 | link-side-selection | partial | Auswahl-Snapshot nicht eingefroren |
| 27 | link-ko | done | über Inspektor/Batch |
| 28 | choose-ga | done | GA-Picker mit Vorprüfung |
| 29 | apply-links | done | Domain-Atomarität, Undo |
| 30 | remove-link | done | Domain Remove, Warnung |
| 31 | sending-ga | done | expliziter Inspektor-Button |
| 32 | reveal-topology | done | Inspektor springt in die Topologie und setzt die Suche |
| 33 | device-params | done | Parameterdarstellung |
| 34 | discard-params | partial | Session-API, kein sichtbarer Verwerfen-Button für native Parameter |
| 35 | review-params | done | native Prüfung + Demo-Vorschau |
| 36 | apply-params | done | ein Undo-Schritt |
| 37 | save-device | done | Name, Adresse, Linie und Raum in einer Undo-Transaktion |
| 38 | new-ga | done | Menü und validiertes Formular |
| 39 | create-ga | done | Adresse, Name, DPT und Raumbezug |
| 40 | catalog | done | echter Katalog |
| 41 | insert-device | done | bevorzugte Linie; Raumzuordnung prüfen |
| 42 | toggle-monitor | done | Werkzeugleiste |
| 43 | close-dock | done | Ausblenden beendet Sitzung nicht |
| 44 | dock-tool | done | Monitor, Programmierung, Scan, Suche, Änderungen, Prüfung |
| 45 | maximize-dock | done | Layoutzustand |
| 46 | watch-ga | done | Beobachten/Filter |
| 47 | watch-scope | partial | Projekt/Raum/Gerät/GA; Auswahl folgen |
| 48 | monitor-toggle | done | Start/Stop, Modus |
| 49 | monitor-clear | done | Anzeige leeren |
| 50 | monitor-export | done | gefiltert + Voll-CSV |
| 51 | monitor-value | done | Wertdialog für GA und Wert |
| 52 | simulate-read | done | gekennzeichnete simulierte Leseantwort |
| 53 | simulate-write | done | gekennzeichnete simulierte Schreibaktion |
| 54 | program | done | Auftrag einreihen, eingefroren |
| 55 | start-program | done | Demo-Queue |
| 56 | retry-job | done | fehlgeschlagene Aufträge |
| 57 | run-scan | done | Linien-Scan als ausdrückliche Demo-Sitzung |
| 58 | all-issues | done | Prüfmeldungen |
| 59 | show-changes | done | begrenzter Änderungsverlauf im Werkzeug |
| 60 | tool | partial | nur drei Werkzeugarten |
| 61 | undo | done | Projekt-Undo |
| 62 | redo | done | Ctrl+Y/Ctrl+Shift+Z |
| 63 | menu-project | done | Projektmenü |
| 64 | menu-edit | partial | Grundbefehle, Selektion fehlt |
| 65 | menu-view | done | Layout/Ansicht |
| 66 | focus-layout | done | Fokusmodus |
| 67 | reset-layout | done | getrennt vom Projekt |
| 68 | palette | done | Objekte/Befehle |
| 69 | concept | partial | Hilfe-Dialog, kein vollständiges Konzept |
| 70 | close-modal | done | native Dialoge |
| 71 | export-project | done | Demo-JSON mit Verlustschutz |
| 72 | export-ga | done | GA-CSV |
| 73 | export-links | done | Zuordnungs-CSV mit Sending |
| 74 | export-report | done | HTML-Bericht |
| 75 | import-project | done | validierter Demo-JSON-Import |
| 76 | reset | done | Demo-Reset im Projektmenü |
| 77 | confirm-reset | done | eigene destruktive Bestätigung mit Abbruch als Fokus |
| 78 | confirm-import | done | atomarer Import |

## Nicht-Dispatch-Ereignisse

| ID | Status | Restlücke |
|---|---|---|
| E01 | done | drei Navigatorfilter |
| E02 | done | modellweiter Objektfilter |
| E03 | partial | Link-Zielfilter nicht separat |
| E04 | done | Palette-Suche, Tastatur |
| E05 | done | typisierte Entwürfe |
| E06 | done | externe Kanäle |
| E07 | done | nur unverknüpft |
| E08 | done | Gerätewechsel erhält Entwürfe |
| E09 | missing | GA-Gegenansicht/Pinning |
| E10 | done | Modus/Interface |
| E11 | done | Pin/Follow |
| E12 | done | Follow Selection |
| E13 | missing | Scan-Linie |
| E14 | done | Perspektiv-Aufklappzustand |
| E15 | partial | Shortcuts vorhanden; vollständige Abnahme offen |
| E16 | partial | Escape schließt Inspector/Dialoge |
| E17 | partial | Fokus/Tab-Trapping offen |
| E18 | partial | Splitter-Tastatur offen |
| E19 | done | GridSplitter |
| E20 | done | Importgröße/-validierung |
| E21 | done | getrennte Settings/Layoutdatei |
| E22 | done | Tab-Scroll/Filter/Auswahl |
| E23 | done | Demo-Tick/Timer |
| E24 | done | Queue-Lebensdauer |
| E25 | done | modale Dialoge |
| E26 | partial | zehn Werkzeugarten fehlen teilweise |

## Verbleibende Implementierungspunkte

1. 34 sichtbarer Verwerfen-Button für native Parameterentwürfe.
2. 60 vollständige Werkzeugarten (Dokumentation/Konzeptansicht).
3. 64 vollständige Selektionsbefehle im Bearbeiten-Menü.
4. 69 Konzept-/Hilfeansicht statt einzelnes Dialogfenster.
5. Fokus-/Screenreader-/High-Contrast-Abnahme, Multi-Projekt-Tabs und Panel-Selektor.

Jede Änderung bekommt eigene Tests und wird erst nach Review integriert.
