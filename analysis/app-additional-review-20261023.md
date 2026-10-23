# Zusätzliches App-Review — Korrektheit, Design, Code-Smells

Geprüfter Snapshot: `610a967d9758d3beff82452e11fc1f7660a7f7f9`.
Datum: 2026-10-23. Drei unabhängige, rein lesende Prüfungen in getrennten
Worktrees. Das Review lief parallel zur Behebung des ersten V4-Reviews.

**Wichtig:** Die nach dem Snapshot bereits bearbeiteten Follow-ups zu
Sitzungslebensdauer, Avalonia-Kontexten, CR-Rollen, lokalen P052-Befunden und
Geräteauswahl sind hier ausdrücklich nicht erneut gezählt. Die folgenden
zusätzlichen Befunde wurden nicht im Rahmen dieser ersten Fix-Welle behoben.
Repro-Abläufe sind statisch aus dem aktuellen Kontrollfluss abgeleitet,
**nicht als ausgeführte Laufzeittests auszugeben**. Zeilennummern beziehen sich
auf den geprüften Snapshot, nicht auf spätere Commits.

## Korrektheit

### C1 · P1 · Katalogeinfügen kann doppelte Geräte-IDs erzeugen

`src/Knx.Core/Catalog/CatalogInstaller.cs:15–23`,
`src/Knx.App/Services/CatalogBrowser.cs:315–330`.

`CreateDevice()` verwendet die Adresse als stabile ID; die Einfügeprüfung
kontrolliert nur die freie Adresse. Gerät auf 1.1.1 einfügen, nach 1.1.2
umadressieren, erneut auf 1.1.1 einfügen: beide Geräte können dieselbe ID
erhalten. Objektziele und Raumreferenzen werden mehrdeutig; `ProjectCheck`
verwendet die IDs in `ToDictionary` (`ProjectCheck.cs:412–415`).

**Minimalbehebung:** Projektweit freie stabile ID vergeben, vorhandene
Kollisionsstrategie von `ProjectOperations.CopyDevice()` wiederverwenden.
Vor der Mutation prüfen, damit ein fehlgeschlagener Undo-Snapshot keinen
inkonsistenten Livezustand zurücklässt.

### C2 · P1 · Fehlgeschlagenes Öffnen kann bisherigen Werkzeugzustand verlieren

`src/Knx.App/WorkbenchWindow.Commands.cs:200–202`,
`src/Knx.App/MainWindowViewModel.cs:252–261,286–300`.

Nach erfolgreichem Parsen werden Sitzungen und Parameterentwürfe verworfen.
Die spätere Übernahme kann aber noch an `RememberProject()`/`Settings.Save()`
scheitern, beispielsweise bei nicht beschreibbarer Einstellungsdatei. Dann
bleibt das alte Projekt sichtbar, seine Sitzung und Entwürfe sind jedoch weg.
Auch Portfolio-Öffnen verwendet diesen Ablauf.

**Minimalbehebung:** Fehleranfällige Vorbereitung vorziehen oder die
Recent-Liste als best-effort behandeln; alte Arbeitszustände erst beim
tatsächlich erfolgreichen Austausch verwerfen.

### C3 · P2 · Geräteinspektor erlaubt unpassende Adresse zur Ziel-Linie

`MainWindowViewModel.cs:473–495,515–521`,
Aufruf `WorkbenchWindow.axaml.cs:1664–1669`.

Bei einem Gerät 1.1.x die Linie 1.2 auswählen, Adressfeld unverändert lassen
und übernehmen: Syntax und Doppelbelegung werden geprüft, nicht die
Übereinstimmung mit der Zieltopologie. Navigatorposition und Adresse können
einander widersprechen.

**Minimalbehebung:** Adresse gegen Bereich/Linie validieren; bei einem
Linienwechsel eine passende freie Adresse anbieten oder die Eingabe ablehnen.

### C4 · P2 · GA-Parser normalisiert ungültige Werte stillschweigend

`src/Knx.Core/Values.cs:68–77`, UI-Eingang
`WorkbenchWindow.Commands.cs:920–925`.

Beispiele: 1/8/1 wird durch Bitpacken zu 1/0/1, 32/0/1 zu 0/0/1.
Die Prüfung numerischer Datentypen ersetzt keine KNX-Bereichsprüfung.
Dadurch kann eine andere GA angelegt werden als eingegeben.

**Minimalbehebung:** Dreistufig 31/7/255, zweistufig 31/2047 vor dem Packen
prüfen; gültigen Null-Rohwert weiterhin vom anlegbaren GA-Bereich unterscheiden.

### C5 · P2 · DPT-Änderung aktualisiert gespeicherte Bitlänge nicht

`MainWindowViewModel.cs:545–553`, `src/Knx.Core/ProjectOperations.cs:367–379`.

Eine neu angelegte 1-Bit-GA von DPST-1-1 auf DPST-9-1 ändern:
`Workbench.Groups[id].Bits` kann 1 bleiben, obwohl der DPT 16 Bit verlangt.
Die Linkprüfung lehnt danach passende Gegenstellen ab.

**Minimalbehebung:** DPT und bekannte Bitlänge atomar ändern und bestehende
Verknüpfungen konsistent nachprüfen.

### C6 · P2 · Lokales Verwerfen leert native Entwürfe anderer Geräte

`src/Knx.App/Views/ParameterPane.axaml.cs:134`,
`src/Knx.App/Services/ParameterEditingSession.cs:137–142`.

Entwürfe für A und B anlegen; bei B Verwerfen wählen: Der lokale Button
ruft das projektweite `DiscardDrafts()` auf und leert auch den Entwurf von A.
Der neue Demo-Pfad verwirft dagegen gerätebezogen.

**Minimalbehebung:** Lokales Geräteverwerfen vom projektweiten Verwerfen
beim Schließen/Projektwechsel trennen.

### C7 · P2 · Monitorfolgefilter ignoriert Geräte-ID bei gleichen KO-IDs

`WorkbenchWindow.axaml.cs:477–478`, Übergabe `:2214`.

Der Shell-Resolver vergleicht bei `ComObject` nur die KO-Referenz-ID,
nicht `filter.DeviceId`. Zwei Geräte desselben Produkts mit gleichen
KO-IDs können deshalb ihre GA-Mengen im Auswahlfolgefilter vermischen.
Der interne Monitorfallback berücksichtigt die Geräteidentität bereits
(`Sessions/MonitorSession.cs:333–336`).

**Minimalbehebung:** Den vollständigen Schlüssel aus Geräte- und KO-ID verwenden.

## Design und UI-Interaktion

### D1 · P2 · Schmaler Inspector sperrt sichtbare Werkzeuge aus der Tabfolge

`WorkbenchWindow.axaml.cs:2706–2731`, `WorkbenchWindow.axaml:227–291`.
Der Inspector endet oberhalb des Werkzeugbereichs, aber `TabNavigation=None`
trifft die gesamte `MainColumn` einschließlich Werkzeugtabs und Monitor.
Bei weniger als 1100 DIP: Monitor zeigen, Inspector öffnen, Tab verwenden.

**Minimalbehebung:** Nur tatsächlich verdeckte Editorinhalte aus der
Fokusfolge nehmen. Bundle `docs/KONZEPT.md:41` fordert Tastaturbedienbarkeit.

### D2 · P2 · Gefloateter Scan aktualisiert den falschen Host

`WorkbenchWindow.axaml.cs:2434–2472`.
Beim leeren Scan ist nur der Leerzustand im Float sichtbar. Nach Abschluss
wird ausschließlich `ToolHost.Content` im Hauptfenster neu gesetzt. Der
Float bleibt leer; ein inzwischen aktiviertes anderes Werkzeug kann im
Hauptfenster durch die Scanansicht überschrieben werden.

**Minimalbehebung:** Ergebnis-/Leerzustand im jeweiligen Host an den
Sitzungszustand binden; kein anderer Werkzeugkontext darf überschrieben werden.

### D3 · P2 · Prüfungsbadge bleibt bei normalen Änderungen veraltet

`WorkbenchWindow.axaml.cs:538–550,392–399`.
Der Cache wird nur bei neuer Projektobjektreferenz neu berechnet; normale
Edits mutieren dieselbe Instanz. Eine neue unbenutzte GA kann P042 erzeugen,
während die Rail weiter die vorherige Zahl zeigt.

**Minimalbehebung:** Den Cache bei Projektänderungen invalidieren oder an
dieselbe Prüfrevision wie die Ergebnisliste binden.

### D4 · P2 · Sprachwechsel aktualisiert ausgelagerte Inhalte nicht

`WorkbenchWindow.axaml.cs:352–365,2874–2876`.
`ApplyLanguage()` baut das angedockte Werkzeug, nicht die Float-Inhalte und
Fenstertitel neu auf. Einzelne Live-Statuswerte können bereits die neue,
Buttons und Überschriften noch die alte Sprache verwenden.

**Minimalbehebung:** Auch ausgelagerte View-Hosts nach Sprachwechsel erneuern,
ohne ihre unabhängige Sitzung zurückzusetzen.

### D5 · P2 · Formularbeschriftungen fehlen als programmatische Feldnamen

`src/Knx.App/SettingsWindow.axaml:38–47,71–77`,
`ReplaceProductWindow.axaml:19–28`, `WorkbenchWindow.axaml.cs:2648–2656`.
Benachbarte TextBlocks ersetzen keine `LabeledBy`-/Name-Verknüpfung für
Theme, Sprache, Busfelder, Produktreferenzen und Demo-Parameter.

**Minimalbehebung:** Sichtbare Labels mit den Controls verbinden und
Sprachbindung beibehalten. Accessibility-Befund, kein behaupteter Narrator-Lauf.

### D6 · P3 · Programmierstatus verwendet rohe technische Enum-/Auftragswerte

`WorkbenchWindow.axaml.cs:2311,2375–2385`.
Status wird mit `ToString().ToUpperInvariant()` dargestellt, Operation als
`full`; diese UI-Zustände umgehen alle acht Kataloge.

**Minimalbehebung:** UI-Status und Auftragsbezeichnungen lokalisieren;
technische Werte im Datenmodell unverändert lassen.

## Code-Smells und Verantwortungsgrenzen

Diese Punkte sind begründete Wartbarkeitsurteile mit konkreter Fehlerwirkung,
keine pauschalen Regeln gegen große Klassen.

### S1 · P2 · Zwei Unlink-Wege mit unterschiedlicher Bereinigung

`src/Knx.Core/GroupLinks.cs:15–17`,
`src/Knx.Core/Workbench/WorkbenchProject.cs:171–194`,
`src/Knx.Mcp/McpServer.GroupLinks.cs:34–55`.
MCP entfernt nur den nativen Link; Workbench bereinigt zusätzlich Connector
und Sendeauswahl. Nach MCP-Unlink und erneutem Link können alte Eigenschaften
wieder wirksam werden.

**Minimalbehebung:** Bereinigung im gemeinsamen Unlink, Workbench-Warnung
darüber ergänzen. Keine neue Servicehierarchie nötig.

### S2 · P2 · Kopierte Gerätetraversierung liefert widersprüchliche Exporte

`src/Knx.Core/ProjectQuery.cs:10–28`,
`src/Knx.Reports/ProjectCsvExporter.cs:24–64`, `ProjectReport.cs:176–191`.
Topologie-CSV lässt Bereichsgeräte aus und direkte Liniengeräte bei Linien
mit Segmenten weg. Andere Exporte kennen diese Geräte; im Übergabepaket
können deshalb unterschiedliche Gerätemengen stehen.

**Minimalbehebung:** Vorhandene `ProjectQuery.Devices`-Traversierung verwenden;
gemischte Bereichs-/Linien-/Segmentgeräte in einem Check abdecken.

### S3 · P2 · Tunnelerwerb veröffentlicht Zustand vor erfolgreicher Verbindung

`src/Knx.Bus/KnxBus.cs:32–67`, `KnxTunnel.cs:140–145`,
`MainWindowViewModel.cs:916–940`.
Anders als Routing setzt der Tunnelpfad Felder vor dem Handshake und räumt
sie bei Fehlschlag nicht gemeinsam zurück. Wiederholung kann „already
connected“ melden. Im ViewModel wird der Telegrammhandler je Versuch erneut
registriert; spätere Telegramme können mehrfach verarbeitet werden.

**Minimalbehebung:** Lokale Ressourcen erwerben, Fehler entsorgen, erst nach
Erfolg veröffentlichen; Ereignisse an die Buslebensdauer binden.
Nachprüfung ausschließlich per Emulator/Offline-Transport, keine reale Busaktion nötig.

### S4 · P2 · DPT-Schreibweisen haben keinen gemeinsamen Normalisierungsvertrag

`src/Knx.Bus/Dpt.cs:10–60`, `MainWindowViewModel.cs:447–461`,
`src/Knx.App/Services/GroupMonitor.cs:268–291`,
`src/Knx.Core/Workbench/WorkbenchProject.cs:63–83`.
Validierung akzeptiert 1.001 und DPST-1-1, Codec-/Einheitenauswahl und
Linkvergleich behandeln sie teilweise verschieden.

**Minimalbehebung:** Eine gemeinsame Normalisierung bekannter äquivalenter
Formen vor Codec-Auswahl/Vergleich; unbekannte DPTs ausdrücklich unbekannt
lassen. Kein umfassendes DPT-Typsystem nötig.

## Einordnung und nächste Priorität

- Korrektheit: 7 zusätzliche Befunde (2 P1, 5 P2).
- Design/UI: 6 zusätzliche Befunde (5 P2, 1 P3).
- Code-Smells: 4 Befunde (P2), teilweise fachliche Überlappung mit Korrektheit.
  **17 Einträge sind daher nicht gleich 17 unabhängige Root Causes.**
- Zuerst C1/C2 per rotem Regressionstest bestätigen und beheben; anschließend
  Adress-/DPT-Invarianten, lokales Verwerfen und die Diagnose-/UI-Kontextfehler.
- Keine pauschale Aufteilung der großen Workbench-Datei, keine neue Factory-/
  Servicearchitektur. Vorhandene Standard-/Hilfsfunktionen konsequent verwenden.

Positiv bleiben atomarer Archivtausch, transaktionales Portfolio, eingefrorene
Programmierkonfigurationen und die konsolidierte V4-Dialoggestaltung. Native
Titelleisten, Branding, Simulationsgrenzen und Sprachwahl ohne Preview gelten
als akzeptierte Entscheidungen. Dieses Review ist keine Freigabe für reale
KNX-Inbetriebnahme und ersetzt keine vollständige interaktive Abnahme.
