# V4-Review — Behebung und Nachprüfung

Ausgangsstand: `0380a1d14a2717ba134cdcb8d9fbbcfb565dd46f`.
Maßgebliche Befunde: [Review](v4-concept-ui-review-20261022.md).
Referenz: `C:/Users/julia/Downloads/knx-workbench-v4-bundle`.

## Umfang

K1–K6 und U1–U6 des Reviews. Native Fensterrahmen bleiben erhalten;
die gemeinsame V4-Gestaltung betrifft den Fensterinhalt. Kein echter Buszugriff,
keine Programmierung und keine Herstellerdynamik werden neu freigegeben.

Die Behebung wird in getrennten Worktrees entwickelt und erst nach
Regressionstests und gemeinsamer Nachprüfung integriert. Der ursprüngliche
Review bleibt als historischer Befund unverändert.

## Prüfkriterien

| Befund | Abnahmekriterium |
|---|---|
| K1 | Projekt-Undo/Redo erhält Monitor, Warteschlange und geladenen Simulationsstand; echte Projektwechsel setzen Sitzungen zurück; laufende Programmierung schützt Projekt-Undo. |
| K2 | Unübernommene Demo-Parameter bleiben beim Navigieren erhalten; Übernehmen/Verwerfen und Projektwechsel sind eindeutig. |
| K3 | Katalog übernimmt Raum/Linie des Ausgangsgegenstands, ohne alten Kontext in ein anderes Projekt zu tragen. |
| K4 | Programmierstatus, Fortschritt und Bedarf aktualisieren sich ohne erneutes Öffnen des Werkzeugs. |
| K5 | Sichtbarer Monitorfilter entspricht der Sitzung; bekannte Quelle und GA sind zum Projekt navigierbar. |
| K6 | Beziehungen zeigen Gegenstellen/Rollen; Objektprüfung ist lokal, globale Prüfung bleibt global. |
| U1 | Angedockte und ausgelagerte Werkzeuge verwenden denselben V4-Inhaltsstil. |
| U2 | Programmgenerierte Dialogfelder verwenden die gemeinsamen V4-Feldstile. |
| U3 | Dialoginhalt bleibt scrollbar und Aktionen bleiben erreichbar. |
| U4 | Escape/Cancel, sichere Standardaktion und lokale Fehlermeldungen sind konsistent. |
| U5 | Validierung nutzt semantische Theme-Ressourcen statt fester Farben. |
| U6 | Ausgelagerte Suche trägt einen Such- statt Monitortitel. |

## Ergebnisse

Quellstand der Behebung: `aa41655716b464167c9f7c5768659e501a8de134`.
Alle zwölf ursprünglichen Befundgruppen sind adressiert. Das bedeutet keine
pauschale Fehlerfreiheit der App; zusätzliche Befunde stehen im
[weiteren Review](app-additional-review-20261023.md).

- K1/K2/K3: `6516821`, native Shell-Regressionen für Sitzungs-/Draft-Erhalt,
  explizite Projektwechsel, History-Sperre und synthetischen Katalogeinbau.
- K4/K5/K6/U6: `dc9322d`, Live-Jobstatus, tatsächlicher Monitorfilter,
  Projektverweise, GA-zentrierte Gegenstellen und lokale Prüfung.
- U1–U5: `c1c5e2e`, gemeinsamer V4-Fensterhost, Feldpalette, scrollbarer Body,
  Footer, Tastaturregeln, lokale Fehler und semantische Theme-Farben.
- Integrationsnachlauf `79edb06`/`aa41655`: Read-Response-Rollen, defekte
  Connectoren in lokalen Prüfungen, aktuelle Geräteauswahl nach History sowie
  sicheres Abmelden aller angedockten/gefloateten Beobachter **vor** jeder
  Session-Entsorgung. Auch reguläres Öffnen/Neu mit laufendem Demoauftrag ist
  durch den echten Reset-Pfad abgedeckt.
- Avalonia-Zugriff: UI-Kontextobjekte verschiedener Dispatcherprioritäten
  dürfen auf demselben UI-Thread verwendet werden; Hintergrundzugriff bleibt
  verboten. Ein zusätzlicher Programmier-Screenshot deckt den vorherigen
  Absturz im Screenshot-Selbsttest ab.

## Regressionsevidenz

Vor den jeweiligen Fixes beobachtet:

- Shell: Verlust unabhängiger Sitzungen, Verlust eines Demo-Entwurfs und
  fehlender Raumkontext jeweils rot.
- Docking: `completed row updates without recreating tool` rot.
- Dialoge: 17 rote Prüfungen vor Behebung; alte U5-Farben erzeugten separat
  sechs Fehler.
- Integrationsnachlauf: fehlende CR-Senderrolle, verschwundener lokaler P052,
  veraltete Geräteauswahl und nicht geschlossenes Float-Fenster jeweils rot.
- Regulärer Reset mit sichtbar angeschlossenem Float und laufendem Auftrag:
  `ObjectDisposedException` vor dem zentralen Detach, danach grün.
- `--screenshot-selftest`: Programming-Capture scheiterte am UI-Kontext;
  nach Dispatcher-Zugriffsprüfung erfolgreich.

Die entsprechenden Checks bleiben ausführbar im Repository; Zustandstests
und die nativen Dialogprüfungen sind zusätzlich in Windows-CI aufgenommen.
Der exakte Telegrammzähltest stoppt seinen unabhängigen Demo-Timer und injiziert
die erwarteten Frames selbst, statt sich auf zufälliges Timer-Timing zu verlassen.

## Prüfkommandos

```powershell
dotnet build ets6.sln -c Release
# Alle 27 Testprojekte unter tests/ und zusätzlich src/Knx.App.Tests
dotnet run --project tests/Knx.Shell.Tests -c Release -- --state-k1
dotnet run --project tests/Knx.Shell.Tests -c Release -- --state-k2
dotnet run --project tests/Knx.Shell.Tests -c Release -- --state-k3
dotnet run --project tests/Knx.WorkbenchUiState.Tests -c Release -- --dialogs
dotnet run --project tests/Knx.WorkbenchUiState.Tests -c Release -- --dialogs --de
dotnet run --project src/Knx.App -c Release -- --smoke
dotnet run --project src/Knx.App -c Release -- --workbench-selftest --theme=light --lang=de
dotnet run --project src/Knx.App -c Release -- --workbench-selftest --theme=dark --lang=en
dotnet run --project src/Knx.App -c Release -- --screenshot-selftest
dotnet run --project src/Knx.Mcp -c Release -- --selftest
```

Screenshots: 18 neue Aufnahmen in Light/Dark unter
`C:/Users/julia/AppData/Local/Temp/opencode/v4-fixed-79edb06/`.
Katalog zeigt nun den Ausgangsraum Wohnzimmer; Demo-Parameter besitzen
explizites Verwerfen; Programmieransicht wird wieder erfolgreich erfasst.

Unabhängiger Nachcheck des letzten Lifetime-Fixes: `aa41655` freigegeben,
einschließlich Einsicht in Red-/Green-Logs. Keine echten Busaktionen ausgeführt.
Reale 150/200-%-DPI, High Contrast, Narrator und andere Betriebssysteme bleiben
außerhalb dieser Abnahme; native Tests verwenden geroutete UI-Ereignisse und
Layoutmessung, keine Betriebssystem-Tastatureinspeisung.
