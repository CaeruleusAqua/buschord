# V4-Konzepttreue und UI-Konsistenz — Review 2026-10-22

## Ergebnis

**Die V4-Grundstruktur ist überzeugend umgesetzt; eine durchgehend konsistente
V4-Anwendung ist es noch nicht.** Die Hauptoberfläche und die größeren
Formulardialoge wirken verwandt. Kritischer als einzelne optische Unterschiede
sind derzeit Zustandsverluste und nicht durchgängig kontextbezogene Abläufe.

Geprüfter Snapshot: `0380a1d14a2717ba134cdcb8d9fbbcfb565dd46f`.
Referenz: `C:/Users/julia/Downloads/knx-workbench-v4-bundle`.
Dies ist ein Review der gesamten App gegen das Bundle, **kein Sprach-Diff**.
Keine Produktänderungen, keine Busaktionen.

## Grundlage und Evidenz

- Bundle: `docs/KONZEPT.md`, `ANFORDERUNGEN_UND_USER_STORIES.md`,
  `DATENMODELL_UND_ZUSTAENDE.md`, interaktive HTML-Referenz und deren Quelltext.
- Zwei unabhängige statische Prüfungen des gepinnten Snapshots: Konzepttreue
  sowie UI-/Fensterkonsistenz; bestätigte Stellen durch die Hauptsession nachgelesen.
- Release-Build erfolgreich. 22 aktuelle Screenshot-Aufnahmen: Hauptfläche,
  Einstellungen, Neuprojekt, Portfolio, Katalog, Parameter, Beziehungen,
  Monitor, Inspector, Programmierung und kleines Hauptfenster, jeweils Light/Dark.
- HTML-Referenz im Browser geöffnet und bei 1440 × 900 betrachtet.
- Native Aufnahmen liegen unter
  `C:/Users/julia/AppData/Local/Temp/opencode/v4-review-0380a1d/`.
  Reproduktion: `dotnet run --project src/Knx.App -c Release -- --screenshot=<png> --screen=<screen> --theme=light --lang=de`.

**Grenzen:** Screenshots zeigen den jeweiligen vorbereiteten Zustand, nicht den
vollständigen Klickablauf. Die unten angegebenen Repro-Schritte für
Zustandsfehler sind aus dem aktuellen Kontrollfluss abgeleitet und nicht als
automatisierte End-to-End-Reproduktion ausgeführt. Kleine Quick-Dialoge und
Float-Fenster wurden statisch geprüft, nicht als Live-Fenster vermessen.
Keine echte 150/200-%-DPI-, High-Contrast- oder Narrator-Abnahme.

P1 = Arbeitszustand/zentraler Konzeptablauf betroffen. P2 = deutlicher
Bedienungs-/Konsistenzmangel. P3 = begrenzte Orientierungsabweichung.
Datei-/Zeilenangaben gelten ausschließlich für den geprüften Snapshot.

## Konzepttreue

### K1 · P1 · Undo/Redo setzt unabhängige Werkzeugsitzungen zurück

**Soll:** Projekt-Undo und zuletzt simulierter Gerätestand sind getrennt;
Werkzeuge besitzen eine unabhängige Lebensdauer.
Bundle: `docs/DATENMODELL_UND_ZUSTAENDE.md:41–48`, `docs/KONZEPT.md:33–35`.

**Ist:** `src/Knx.App/MainWindowViewModel.cs:361–376` ersetzt bei Undo/Redo die
Projektinstanz. `src/Knx.App/WorkbenchWindow.axaml.cs:420–440` interpretiert eine
andere Instanz als Sitzungswechsel und erzeugt Monitor/Programmierdienst neu.
Die Undo-Wege prüfen nicht auf laufende Programmierung
(`WorkbenchWindow.Commands.cs:53–54`, `WorkbenchWindow.axaml.cs:2736–2743`).

**Folge:** Monitoraufzeichnung, Warteschlange und geladener Vergleichsstand
können durch eine normale Projektkorrektur verloren gehen. Repro: Werkzeug
starten, Projekt ändern, Undo. **Statischer Kontrollflussbefund.**

**Empfehlung:** Sitzung an die logische Projektidentität binden, nicht an die
Objektreferenz; Undo während laufender Programmierung zentral regeln.

### K2 · P1 · Demo-Parameterentwürfe überleben Navigation nicht

**Soll:** Entwurf und übernommener Projektstand bleiben getrennt; der
Arbeitszusammenhang bleibt bei Darstellungswechsel erhalten.
Bundle: `docs/DATENMODELL_UND_ZUSTAENDE.md:39–44`.

**Ist:** `WorkbenchWindow.axaml.cs:2378–2425` rekonstruiert die Demo-Controls
aus gespeicherten Parameterwerten. Änderungen liegen nur in lokalen Controls.
`RefreshEditor()` stößt den Neuaufbau erneut an (`:777–785`).

**Folge:** Auf Dimmen umstellen, nicht übernehmen, Darstellung/Tab wechseln,
zurück: der Entwurf wird verworfen. Das Verhalten weicht auch vom nativen
Parametereditor mit eigener Sitzung ab. **Statischer Kontrollflussbefund.**

**Empfehlung:** Einen gerätebezogenen Entwurfsspeicher auch für Demo-Parameter
verwenden; Verwerfen explizit machen.

### K3 · P1 · „Gerät hinzufügen“ verliert den Raumkontext

**Soll:** Ein Produkt aus dem geöffneten Raum heraus einfügen und dasselbe
Gerät anschließend in der Topologie wiederfinden. Bundle: `docs/KONZEPT.md:55`.

**Ist:** `WorkbenchWindow.Commands.cs:125` aktiviert zuerst den Katalogtab.
Danach wird der Einfügekontext aus diesem Tool-Tab ermittelt
(`WorkbenchWindow.axaml.cs:788–793`, `:708–719`), nicht aus dem zuvor offenen Raum.

**Evidenz:** `democatalog-dark.png` zeigt neben dem weiterhin vorhandenen
Wohnzimmer-Tab tatsächlich **„Ohne Raumzuordnung“** im Einfügebereich.
Das Einfügen selbst wurde nicht ausgeführt.

**Empfehlung:** Raum-/Linienziel vor dem Wechsel erfassen und explizit übergeben.

### K4 · P2 · Programmierergebnisse sind Momentaufnahmen statt Live-Anzeigen

Bundle: `docs/KONZEPT.md:57` fordert geräteweise Ergebnisse und Wiederholung.
`WorkbenchWindow.axaml.cs:2225–2248` erstellt Status, Fortschritt und
Programmierbedarf als Texte ohne Bindungen. Der Job-PropertyChanged-Handler
ist leer (`:2185`); nach RunAsync folgt kein gezielter Neuaufbau (`:2189–2193`).

**Folge:** Zeilen können QUEUED/0 % oder alten Bedarf anzeigen, obwohl sich das
Sitzungsmodell geändert hat. **Statischer Befund; laufenden Auftrag nachtesten.**
Empfehlung: Anzeige an Job-/Sitzungsänderungen binden.

### K5 · P2 · Monitorfilter und Projekt-Rückbezug bleiben unvollständig

Bundle: `docs/KONZEPT.md:51,58` beschreibt nachvollziehbare fixierte Filter und
Navigation vom Telegramm zum Projekt.

- Filter-Combo wird aus `_monitorScope` statt dem tatsächlichen Sitzungsfilter
  aufgebaut (`WorkbenchWindow.axaml.cs:2081–2107`). „Beobachten“ setzt den
  Filter erst nach dem Aufbau (`:2681–2691`). Nach Werkzeugwechsel kann
  „Alle Telegramme“ erscheinen, obwohl die Sitzung gefiltert bleibt.
- Telegrammquelle/-ziel sind bloße Textzellen (`:2131–2143`); ein direkter Weg
  zum Gerät bzw. zur GA fehlt. Die Referenz bietet ihn (`src/app.js:336`).

**Empfehlung:** Filter und fixierte GA aus dem Sitzungszustand anzeigen und
bekannte Adressen navigierbar machen. **Statisch geprüft.**

### K6 · P2 · Beziehungen und Prüfung sind nicht konsequent gegenstandsbezogen

Bundle: `docs/KONZEPT.md:11,51,87` sowie `src/app.js:245–247`.

- Beziehungen listen KO/GA-Zuordnungen, aber keine GA-zentrierten Gegenstellen
  mit Sender-/Empfängerrollen (`WorkbenchWindow.axaml.cs:1923–1972`).
- Die Darstellung `overview` erzeugt immer `CreateProblemsView()` (`:795`),
  eine globale Projektprüfung (`:1975–1992`). Raum-/Geräte-/GA-Titel erwecken
  dadurch einen lokalen Kontext, den der Inhalt nicht berücksichtigt.

**Empfehlung:** Beziehungen als Signalzusammenhang ergänzen; globale Prüfung
als Werkzeug kennzeichnen oder nach Arbeitsgegenstand filtern. **Statisch geprüft.**

## UI-Konsistenz — Fenster, Dialoge und Werkzeuge

### U1 · P2 · Ausgelagerte Werkzeuge verlieren den gemeinsamen V4-Host

`WorkbenchWindow.axaml.cs:2714–2729` erzeugt ein nacktes `Window` mit dem
Werkzeuginhalt. V4-Hostklasse und Shell-spezifische Tabellen-/Statusstile
werden nicht übernommen. Globale Fensterdefaults greifen stattdessen:
Segoe UI 12,5 und `EtsChrome` (`App.axaml:160–165`), während Teile der
Werkzeugstile nur im Workbench-Fensterscope liegen
(`WorkbenchWindow.axaml:154–173`).

**Folge:** Dasselbe Werkzeug hat angedockt und ausgelagert nicht zuverlässig
dieselbe Typografie, Fläche und Statusgestaltung. Das ist ein Designsystembruch,
nicht bloß ein anderer nativer Fensterrahmen. **Statischer Style-Scope-Befund.**

**Empfehlung:** Einen gemeinsamen V4-Werkzeughost und global verfügbare
Werkzeugstile verwenden. Native Fensterrahmen dürfen bleiben.

### U2 · P2 · Quick-Dialogfelder verwenden eine andere Palette

Große Formulardialoge nutzen `wb-field`. Programmgenerierte TextBox-/ComboBox-
Controls in Passwort, Portfolio-Umbenennen, neuer GA, Mehrfachzuordnung und
Monitorwert tun das nicht (`WorkbenchWindow.Commands.cs:189,608,871–875,920`;
`WorkbenchWindow.axaml.cs:973,1500,2344–2347`). `wb-dialog` am Fenster
stilisiert diese Felder nicht automatisch.

**Folge:** Ets-/Fluent-Feldpalette und andere Radien/Abstände innerhalb einer
ansonsten V4-gestalteten Dialoghülle. **Statischer Stilbefund.**
Empfehlung: vorhandene Feldklassen konsequent anwenden, keine neue Dialogbibliothek.

### U3 · P2 · Kleine Modale haben ein Clipping-Risiko

Neue GA: `WorkbenchWindow.Commands.cs:869–897`; Monitorwert:
`WorkbenchWindow.axaml.cs:2341–2365`. Feste Fensterhöhen 360 bzw. 240 DIP,
mehrere Labels/Felder und fester Footer, aber kein scrollbarer Body.

**Folge:** Untere Felder/Hinweise konkurrieren mit dem Footer; bei kleinerem
Fenster, Schriftvergrößerung oder längeren Texten drohen abgeschnittene Inhalte.
**Layoutrechnung, nicht live vermessen; deshalb kein als beobachtet ausgegebener P1.**

**Empfehlung:** Mindestmaße und scrollbar begrenzten Body als gemeinsamen
Dialogvertrag festlegen. Bundle: `docs/KONZEPT.md:41`, `src/shell.html:22`.

### U4 · P2 · Tastatur- und Validierungsvertrag ist uneinheitlich

- `CreateDialog` und `ActionButton` stellen keinen gemeinsamen Escape-/Default-
  Vertrag her (`WorkbenchWindow.Commands.cs:81–92,183–198,603–631,866–899`;
  `WorkbenchWindow.axaml.cs:1496–1508,2812–2820`). Andere Fenster wie Hilfe,
  Bestätigung und Neuprojekt definieren diese Regeln explizit.
- Ungültige GA und fehlgeschlagene Zuordnung schreiben Fehler nur in die
  Statuszeile des blockierten Hauptfensters (`WorkbenchWindow.Commands.cs:885–891,969–977`).

**Folge:** Gleich aussehende Aktionen reagieren unterschiedlich auf Escape/Enter;
fehlgeschlagenes Erstellen erklärt sich nicht dort, wo die Eingabe stattfindet.
**Statisch geprüft.** Escape ist Konzeptvorgabe (`docs/KONZEPT.md:83`),
Enter- und lokale Validierungskonsistenz zusätzlich ein UX-Urteil.

**Empfehlung:** Cancel/ungefährlichen Default explizit setzen; Feldfehler und
Dialogzusammenfassung nach dem vorhandenen Parametereditor-Muster.

### U5 · P2 · Zuordnungsvalidierung umgeht semantische Dark-Theme-Farben

`WorkbenchWindow.Commands.cs:958` verwendet feste Farben `#268175`/`#b45054`.
Vorhandene Ressourcen `WbSuccess`/`WbError` sind im Dark-Theme bewusst heller
(`Workbench/WorkbenchTheme.axaml:97–99`).

**Folge:** Erfolg/Fehler sind ausgerechnet im kritischen Bestätigungsbereich
anders und dunkler als im Rest der App. **Statisch; kein gemessener WCAG-Befund.**
Empfehlung: vorhandene semantische Ressourcen verwenden.

### U6 · P3 · Ausgelagerte Suche heißt Monitor

`ToolWindowTitle` kennt `search` nicht und fällt auf den Monitortitel zurück
(`WorkbenchWindow.axaml.cs:1915–1922`). Werkzeuge → Suche → Auslagern.
**Statisch eindeutig;** Suchwerkzeug ausdrücklich zuordnen.

## Was bereits gut passt

- Grundaufbau aus Rail, drei Navigatorperspektiven, Arbeitsgegenstands-Tabs,
  Darstellungs-Tabs, optionalem Inspector und unterem Werkzeugbereich ist klar V4.
- Aktueller Demo-Screenshot zeigt Hierarchie aus Gerät/Kanal/KO mit gemeinsamen
  Spalten; 900 × 600 bleibt die Grundnavigation erkennbar.
- Light/Dark besitzen konsistente Flächenabstufungen, türkisfarbene Primäraktionen,
  dezente sekundäre Aktionen und wiedererkennbare Auswahlzustände.
- Einstellungen und Neuprojektfenster verwenden bereits passende Flächen,
  Feldgestaltung, Überschriften und rechts angeordnete Footeraktionen.
  **Diese Fenster sehen nicht mehr wie eine fremde Altanwendung aus.**
- Stabile Objektziele, lokale Tabdarstellungen sowie kanalgenaue externe
  Raumbezüge sind im Modell vorhanden. Mehrfachzuordnung und explizite Sende-GA
  sind stärker als eine rein optische Kopie des Bundles.
- Portfolio, echte KNX-Dateien und Herstellerkataloge erweitern das Bundle;
  Erweiterung ist für sich kein Konzeptverstoß.

## Nicht als Fehler gewertet

- Native Titelleisten modaler Fenster: ausdrücklich akzeptierte Anpassung.
- Branding BusChord und zusätzliche Fensteraktionen statt pixelgenauer HTML-Kopie.
- Eigene Tabellenprojektion statt lizenzpflichtigem TreeDataGrid: das Bundle
  lässt diese Implementierungsentscheidung offen.
- Simulierte Programmierung/Diagnose, begrenzte Herstellerdynamik, Secure nur
  lesbar: Grenzen der Referenz selbst, keine hier neu entdeckten UI-Lücken.
- Deutsche Projekt-/Geräte-/Herstellerbezeichnungen in fremdsprachiger UI sind
  Daten, nicht automatisch fehlende UI-Übersetzungen.

## Empfohlene Reihenfolge

1. **Arbeitszustand schützen:** K1/K2, anschließend Katalogkontext K3.
2. **Einen Dialogvertrag durchziehen:** U2/U3/U4; damit sind Felder, Scrollen,
   Escape, Fokus und lokale Fehler überall gleich.
3. **Werkzeuge vollständig integrieren:** U1, K4/K5, U5/U6.
4. **Objektzentrierung vervollständigen:** K6, statt weitere globale Listen
   unter lokalen Objekttiteln hinzuzufügen.

**Zusammenfassung:** Konzeptachse 6 Befundgruppen (3 P1, 3 P2),
UI-Konsistenzachse 6 Befundgruppen (5 P2, 1 P3). Keine prozentuale
„Fertig“-Bewertung: dafür fehlt eine vollständige interaktive Abnahmematrix.
