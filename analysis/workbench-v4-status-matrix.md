# V4 action status matrix

Stand: 2026-10-21, Basis `ac0480a4710a6a8109b5b54361127cc13debd00d` (Audit-Branch
`agents/final/acceptance`). **Hinweis:** `development` steht inzwischen auf
`5183a0f` (Dialoge `f605870`, Panes `39be758`, Search-Presenter `ab0f8a4`,
Kontrast `45f035b`/`f9d0e7f`, Layout `286c025`, DCA/Help `84cb150`). Die Tabelle
beschreibt weiterhin den gemessenen Basisstand; wo ein späterer Commit eine Zeile
berührt, steht das im Nachtrag unten und im Abnahmebericht §1a/§7. Quellen: `src/app.js`-Aktionen aus
`analysis/workbench-v4-acceptance.md`, aktueller `WorkbenchWindow`-Code, nativer
Workbench-Selftest sowie die Konsolen-Suiten aus `.github/workflows/ci.yml`.
Status:
**done** = im Produktpfad vorhanden und durch Test, Selftest oder einen in dieser
Abnahme nachvollzogenen Lauf belegt; **partial** = vorhanden, aber ein
Ablauf/ eine Affordanz fehlt oder ist nur eingeschränkt erreichbar;
**missing** = nicht erreichbar.

Jede Zeile nennt jetzt den nativen Ort (`Datei:Zeile`) und, wo vorhanden, den
Test/ Selftest. Ein `done` behauptet keine ETS-Interoperabilität und keinen
echten Busverkehr.

## Nachtrag `5183a0f` (Dokumentationswelle)

Die Tabelle bleibt der gemessene Auditstand `ac0480a`. Gegen den integrierten
Stand `5183a0f` sind diese Zeilen inzwischen überholt:

| Zeile | Korrektur |
|---|---|
| 17 | **done**: `BuildObjectRow` hat eine Checkbox-Spalte (`WorkbenchWindow.axaml.cs:1009`), die dieselbe Auswahl wie die Liste spiegelt; Kopf-Checkbox dreiwertig. |
| 07, 60, 69, E26 | **partial**: `HelpWindow` mit sechs Abschnitten ist vorhanden (`WorkbenchWindow.Commands.cs:91`, `Views/HelpWindow.axaml.cs`); es gibt weiterhin keine eigene Dokumentationsseite im Arbeitsbereich. |
| E17 | **done**: Darstellungs- und Werkzeug-Tabs sind native `TabItem` (`WorkbenchWindow.axaml.cs:215,226`). |
| R07 (Kontrast) | aktive Perspektive und Hover sind durch Kontrasttests abgedeckt (`5183a0f`); Tab-Rolle (E17) ist erledigt, der schmale Inspektor bleibt separat zu prüfen. |

Nicht geändert: fehlender gepinnter GA-Vergleich (24/E09), Sammelaktion im
Inspektor (22), ungefilterter GA-Picker (E03), alte Scan-Ergebnisse beim
Linienwechsel (E13), reale DPI-/Screenreader-Abnahme (R04) und englische
Prüf-/Exporttexte (R05).

## Delta gegenüber `55ff6e1`

Neu **done**: 20 (KO-Detail im Inspektor), 26 (eingefrorene Mehrfachauswahl),
34 (sichtbarer Verwerfen-Button der nativen Parameterleiste), 47 (vier
Monitorbereiche), 64 (Selektion im Bearbeiten-Menü), 69 bleibt partial.
Neu **partial statt done**: 24 (GA-Gegenansicht ohne Pinning). Unverändert
bewusste Grenzen: 07 (Dokumentationsseite), 60 (zehn Werkzeugarten),
17 (Checkbox-Spalte), 22 (Sammelaktion), E03/E13 (Ziel-/Scanfilter).

## Aktionen

| # | Aktion | Status | Nativer Ort / Restlücke |
| ---: | --- | --- | --- |
| 01 | open-scope | done | `WorkbenchWindow.axaml.cs:361` `OpenTarget`, drei Perspektiven; Selftest „perspectives + navigator" |
| 02 | open-device | done | `OpenTarget` + `WorkbenchTarget("device", id)`; `WorkbenchUiState` Identitätstest |
| 03 | open-ga | done | `OpenTarget("ga")`; Selftest „search hit opens the group address" |
| 04 | open-ko | partial | kein `ko`-Target/Tab; Detail nur über Einzelauswahl im Inspektor (`:1327`); bewusst kein eigener KO-Tab |
| 05 | search-open | done | `WorkbenchWindow.Commands.cs:1614` `OpenSearchHit`, Palette/Suche |
| 06 | open-project | done | Projekt-Target über Navigatorwurzel, `TargetTitle` `:373` |
| 07 | open-docs | missing | keine Dokumentationsseite; Exporte liegen im Projektmenü, Werkzeugübersicht `Commands.cs:415`; bewusste Grenze |
| 08 | open-tools | done | `Commands.cs:415` `ShowToolsOverview`, Menü `Commands.cs:64` |
| 09 | nav-kind | done | `Navigate` `:341`, Perspektivumschalter `WorkbenchWindow.axaml:61`; Test unabhängiger Suchzustand |
| 10 | toggle-nav | done | Ctrl+B `:2290`, Ansicht-Menü; A11y-Recheck PASS |
| 11 | activate-tab | done | `Workspace.ActiveTab`, Tab-Aktivierung `:474`; Test „Reopen keeps tab identity" |
| 12 | close-tab | done | `:480`; Tests „Closing final tab…", Fokus-Rückgabe |
| 13 | view | done | `SetRepresentation` `:379`; Tests Darstellungswechsel |
| 14 | collapse-device | done | Zeilen-Toggle `:870`; Session-/UI-Test „table expand/collapse" |
| 15 | expand-all | done | `OnExpandAll` `:1177` |
| 16 | select-ko | done | `OnObjectSelected` `:1167`, `SelectionMode=Multiple` |
| 17 | check-ko | partial | keine Checkbox-Spalte in `BuildObjectRow` `:855-910`; Auswahl nur per Liste/Ctrl/Shift |
| 18 | select-all | done | `Commands.cs:504` `SelectAllRows`, Bearbeiten-Menü |
| 19 | clear-selection | done | `Commands.cs:519` `ClearSelection` |
| 20 | inspect-ko | done | `BuildObjectInspector` `:1327`: DPT/Bits/Flags/Aktiv/Links/Sendeadresse/Apply |
| 21 | inspect-device | done | `BuildDeviceInspector` `:1234`; Selftest „inspector reveals native device" |
| 22 | inspect-selected | partial | Mehrfachauswahl zeigt Anzahl + Hinweis (`:1468`), keine Sammelaktion im Inspektor; Batch läuft über Menü |
| 23 | close-side | done | `OnInspector` `:2245`, Schließen-Button `WorkbenchWindow.axaml:126` |
| 24 | compare | partial | GA-Liste im Sidebar-Modus `FillGroupSidebar` `:1031`; **kein gepinnter Vergleich**, Editorwechsel verliert den Bezug |
| 25 | link-selected | done | `Commands.cs:563` `ShowBatchLinkDialog`; Workbench-Tests atomare Batch-Prüfung |
| 26 | link-side-selection | done | `Commands.cs:567` materialisiert die Auswahl vor dem Dialog (eingefroren) |
| 27 | link-ko | done | Auswahl + `OnLinks` `:2246` → Batch mit einem Ziel; Tests |
| 28 | choose-ga | done | Picker mit Vorprüfung `Commands.cs:586-607`; Test Duplikat/DPT |
| 29 | apply-links | done | `WorkbenchProject.AddLinks`, ein Undo; Tests „atomic incompatible batch rejected" |
| 30 | remove-link | done | Objektinspektor `Remove`, Warnung bei Sendeadresse; Tests |
| 31 | sending-ga | done | Objektinspektor „Als Sendeadresse"; Tests unabhängig von Flags |
| 32 | reveal-topology | done | Footer „In Topologie zeigen" `:1272` (Perspektive + Suchtext) |
| 33 | device-params | done | `SetRepresentation("params")`, `ParameterPane`/Demo-Formular |
| 34 | discard-params | done | `ParameterPane.axaml:20` `DiscardButton`, `ParameterPane.axaml.cs:134`; native Tests |
| 35 | review-params | done | `ReviewChanges`, Demo-Vorschau `UpdateModePreview` `:1993` |
| 36 | apply-params | done | `ParameterPane` Apply; Test „reviewed Apply commits once" |
| 37 | save-device | done | `UpdateDeviceDetails`; Tests Name/Adresse/Linie/Raum |
| 38 | new-ga | done | `Commands.cs:526` `ShowNewGroupAddressDialog` |
| 39 | create-ga | done | `CreateGroupAddress`; Selftest „linkable DPT bit size" |
| 40 | catalog | done | `ShowCatalog`, `CatalogPane`; `Knx.CatalogUi.Tests` 100 Checks |
| 41 | insert-device | done | Katalogeinfügen mit Linie/Raum/Adresse; `CatalogUi`/`WorkbenchUiState` |
| 42 | toggle-monitor | done | `ShowTool("monitor")`, Werkzeugleiste |
| 43 | close-dock | done | `ToolsVisible=false` `:2269`; Session-Test „hidden capture continues" |
| 44 | dock-tool | done | sechs Andockwerkzeuge `ShowTool` `:1496` |
| 45 | maximize-dock | done | `ToolsMaximized`, Layout-Tests |
| 46 | watch-ga | done | `OnWatch` `:2253` setzt Bereich nach Target |
| 47 | watch-scope | done | Filter „Alle/Ganzes Projekt/Aktueller Arbeitsgegenstand/Auswahl folgen" `:1693` |
| 48 | monitor-toggle | done | Start/Stop, Modus; Session-Tests |
| 49 | monitor-clear | done | Anzeige leeren, Aufzeichnung bleibt; Test |
| 50 | monitor-export | done | CSV gefiltert/vollständig `:2089/:2098`; Test |
| 51 | monitor-value | done | `ShowMonitorValueDialog` `:1951` |
| 52 | simulate-read | done | explizit Demo gekennzeichnet; Session-Tests |
| 53 | simulate-write | done | explizit Demo, keine echte Busaktion |
| 54 | program | done | `CreateProgrammingView` `:1776`, Auftrag eingefroren |
| 55 | start-program | done | Demo-Queue; Test „success stores frozen image" |
| 56 | retry-job | done | Retry mit eingefrorenem Job; Tests |
| 57 | run-scan | done | `CreateScanView` `:1896`, Demo-Scan; `LineScanSession`-Tests |
| 58 | all-issues | done | Werkzeug „Prüfmeldungen" `:1594` |
| 59 | show-changes | done | Werkzeug „Änderungen" `:1882`, begrenzt; Tests |
| 60 | tool | partial | sechs Dockwerkzeuge + Katalog-Tab + Dialoge (Werkzeuge, Secure, Hilfe); Dokumentationsseite fehlt |
| 61 | undo | done | Ctrl+Z/Menü; Tests |
| 62 | redo | done | Ctrl+Y/Ctrl+Shift+Z; Tests |
| 63 | menu-project | done | `Commands.cs:22-43` |
| 64 | menu-edit | done | `Commands.cs:44-53` inkl. „Alles auswählen"/„Auswahl aufheben" |
| 65 | menu-view | done | `Commands.cs:54-62` |
| 66 | focus-layout | done | `WorkbenchWorkspace.FocusLayout:121`; Test „Focus mode hides chrome" |
| 67 | reset-layout | done | `WorkbenchWorkspace.ResetLayout:115`; Layouttests |
| 68 | palette | done | `ShowPalette` `Commands.cs:457`; A11y-Recheck Tastatur |
| 69 | concept | partial | modaler Hilfe-Dialog `Commands.cs:91` mit sechs Abschnitten; keine eigene Konzeptseite (Referenz ist ebenfalls modal) |
| 70 | close-modal | done | native Dialoge, Escape/Fokus-Rückgabe; Recheck PASS |
| 71 | export-project | done | Demo-JSON mit Verlustschutz; Workbench-Tests |
| 72 | export-ga | done | `ProjectCsvExporter.GroupAddresses` |
| 73 | export-links | done | `AssociationsCsv` `Commands.cs:264` |
| 74 | export-report | done | `ProjectReport.Render` (deutschsprachiger Inhalt, siehe R05) |
| 75 | import-project | done | validierter Demo-JSON-Import; Tests |
| 76 | reset | done | Demo-Reset im Projektmenü |
| 77 | confirm-reset | done | eigene Bestätigung, Abbruch als Default; `ConfirmDialog.AskDemoResetAsync` |
| 78 | confirm-import | done | atomarer Import, `WorkbenchJson.Import` |

## Nicht-Dispatch-Ereignisse

| ID | Status | Nativer Ort / Restlücke |
| --- | --- | --- |
| E01 | done | Query je Perspektive `:427`; Test „Navigator perspectives have independent search state" |
| E02 | done | Objektfilter modellweit; Selftest „filter searches unrealized model objects" |
| E03 | partial | GA-Picker ist eine ComboBox ohne eigenes Textfilterfeld (`Commands.cs:580`); Vorprüfung vorhanden |
| E04 | done | Palette-Suche, Pfeile/Enter/Escape; A11y-Recheck |
| E05 | done | typisierte Entwürfe `ParameterEditingSession`; Tests |
| E06 | done | `OnRelated` `:1162`; Test externer Kanal |
| E07 | done | `OnUnlinked` `:1157` |
| E08 | done | Gerätewechsel `_parameterDevice` `:100`; Test „Two native views update the same draft" |
| E09 | missing | kein gepinnter GA-Vergleich; `compare` nur als GA-Liste (siehe 24) |
| E10 | done | Modus/Schnittstelle; Tests „bus monitor excludes programming/scans" |
| E11 | done | Pin-Filter; Tests „pinned GA survives selection/tab changes" |
| E12 | done | Follow Selection; Test „KO follows ALL linked GAs" |
| E13 | partial | Linienauswahl vorhanden (`:1902`), alte Scan-Ergebnisse werden beim Linienwechsel nicht geleert; Test nur für Scan-Lauf |
| E14 | done | Aufklappzustand je Perspektive; Tests |
| E15 | done | `OnShellKeyDown` `:2284`; A11y-Recheck Ctrl+B/J/K/Tab/Z |
| E16 | done | Escape schließt Dialoge/Inspektor, Fokus-Rückgabe; A11y-Recheck PASS |
| E17 | partial | native Rollen fehlen: Dokument-/Werkzeug-Tabs sind `Button`, nicht `TabItem` (F6) |
| E18 | partial | GridSplitter per Pointer; Tastaturbedienung nicht abgenommen |
| E19 | done | native `GridSplitter` `WorkbenchWindow.axaml:67/111/121`; Layouttests |
| E20 | done | Import validiert Format/Version/Referenzen/Größe (4 MB, `WorkbenchJson.cs:23`); Tests |
| E21 | done | Layout-/Settingsdateien getrennt, korrupte Layouts fallen zurück; Tests |
| E22 | done | Tab-lokaler Scroll/Fokus; Tests |
| E23 | done | Demo-Tick, begrenzte Anzeige; Session-Tests |
| E24 | done | Queue einfriert Konfiguration, Gerätesperre; Session-Tests |
| E25 | done | native Dialoge/Popups, außerhalb schließt ohne Apply |
| E26 | partial | sechs Dockwerkzeuge + Katalog + drei Dialoge; Dokumentation fehlt (siehe 60) |

## Bewusst nicht nachgebaut (unverändert)

- DCA/Plugin-Hosting: nur read-only Erkennung und Sperre, kein In-Process-Host.
- Secure-Inbetriebnahme: Keyring/Zertifikate/FDSK und Secure-Download bleiben außerhalb.
- Reale Downloads/Adressschreibung: Workbench-Queue ist `SessionOrigin.Demo`; echte
  Aufträge werden ohne Executor abgelehnt (`ProgrammingSession.cs:121`).
- USB/RDAC, Cloud/IoT, ETS-Local-Store-Versionierung, Multi-Projekt-Tabs.
- DCA-/Skript-Dynamik, Module/Allocators/LoadProcedures, Hersteller-Seitenlayouts.
- Online-Katalog/Markt/Signaturprüfung.

## Verbleibende Implementierungspunkte

1. Gepinnter GA-Vergleich (24/E09) oder bewusste Streichung im Plan.
2. Dokumentationsseite (07) und vollständige Werkzeugarten (60/E26).
3. Checkbox-Spalte (17) und Sammelaktion im Inspektor (22).
4. Tab-Rollen `TabItem` statt `Button` (E17/F6).
5. Englische Prüfmeldungen/Exporte (R05, siehe Abnahmebericht).
6. Kontrast: Disabled-Opacity, Statusleiste, aktive Perspektive (R07).
7. Reale 150/200-%-DPI- und Screenreader-Abnahme (R04).

Jede Änderung bekommt eigene Tests und wird erst nach Review integriert.
