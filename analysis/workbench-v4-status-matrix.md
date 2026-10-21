# V4 action status matrix

Stand: 2026-10-21, Basis `428a7d9` (V4-Workbench). Quellen: `src/app.js`-Aktionen
aus `analysis/workbench-v4-acceptance.md`, aktueller `WorkbenchWindow`-Code,
Domain-/Session-Tests. Status:
**done** = im Produktpfad vorhanden und geprüft; **partial** = vorhanden, aber
Ablauf/Validierung/UX unvollständig; **missing** = nicht erreichbar.

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
| 18 | select-all | missing | gefilterte Gesamtauswahl |
| 19 | clear-selection | partial | keine explizite Aktion |
| 20 | inspect-ko | partial | Inspektor, aber keine vollständige KO-Detailansicht |
| 21 | inspect-device | done | Geräteinspektor |
| 22 | inspect-selected | partial | Mehrfachauswahl-Hinweis, keine Sammelaktion |
| 23 | close-side | done | Inspector schließen |
| 24 | compare | missing | GA-Gegenansicht/Vergleich |
| 25 | link-selected | done | atomarer Batch-Dialog |
| 26 | link-side-selection | partial | Auswahl-Snapshot nicht eingefroren |
| 27 | link-ko | done | über Inspektor/Batch |
| 28 | choose-ga | done | GA-Picker mit Vorprüfung |
| 29 | apply-links | done | Domain-Atomarität, Undo |
| 30 | remove-link | done | Domain Remove, Warnung |
| 31 | sending-ga | done | expliziter Inspektor-Button |
| 32 | reveal-topology | missing | Sprung Baum/Gerät |
| 33 | device-params | done | Parameterdarstellung |
| 34 | discard-params | partial | Session-API, kein sichtbarer Verwerfen-Button für native Parameter |
| 35 | review-params | done | native Prüfung + Demo-Vorschau |
| 36 | apply-params | done | ein Undo-Schritt |
| 37 | save-device | partial | Name; Raum/Linie/Adresse nicht vollständig editierbar |
| 38 | new-ga | done | Menü, Basisform fehlt |
| 39 | create-ga | partial | VM-Anlage, kein vollständiges Formular |
| 40 | catalog | done | echter Katalog |
| 41 | insert-device | done | bevorzugte Linie; Raumzuordnung prüfen |
| 42 | toggle-monitor | done | Werkzeugleiste |
| 43 | close-dock | done | Ausblenden beendet Sitzung nicht |
| 44 | dock-tool | done | Monitor/Programmierung/Prüfung |
| 45 | maximize-dock | done | Layoutzustand |
| 46 | watch-ga | done | Beobachten/Filter |
| 47 | watch-scope | partial | Projekt/Raum/Gerät/GA; Auswahl folgen |
| 48 | monitor-toggle | done | Start/Stop, Modus |
| 49 | monitor-clear | done | Anzeige leeren |
| 50 | monitor-export | done | gefiltert + Voll-CSV |
| 51 | monitor-value | missing | Wertdialog |
| 52 | simulate-read | missing | simulierte Leseantwort |
| 53 | simulate-write | missing | simulierte Schreibaktion |
| 54 | program | done | Auftrag einreihen, eingefroren |
| 55 | start-program | done | Demo-Queue |
| 56 | retry-job | done | fehlgeschlagene Aufträge |
| 57 | run-scan | missing | Linien-/Gerätescan |
| 58 | all-issues | done | Prüfmeldungen |
| 59 | show-changes | missing | Änderungsverlauf |
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
| 76 | reset | missing | Demo-Reset |
| 77 | confirm-reset | missing | Demo-Reset-Bestätigung |
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

## Nächste Implementierungswellen

1. **Welle A:** E09 compare, E13 scan, E26 tools, 18 select-all, 19 clear, 32 reveal, 51–53 Monitorwert.
2. **Welle B:** 34 discard, 37 device detail edit, 38/39 GA form, 59 change history, 69 concept/help, 76/77 demo reset.
3. **Welle C:** Fokus/Screenreader/DPI, englische Shell, Icons/Katalog, Restpolitur.

Jede Welle bekommt eigene Tests und wird erst nach Review integriert.
