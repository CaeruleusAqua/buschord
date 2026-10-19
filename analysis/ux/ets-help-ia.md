# ETS6: Informationsarchitektur, Hilfe-Modell und Aufgabenordnung

> Historischer Stand. Der [Gegencheck vom 2026-10-19](ets6-panel-recheck.md)
> berichtigt den Gegensatz „Panels statt Tabs“: ETS6 unterstützt beides.
> Topologie ist ein Arbeitsfenster, Eigenschaften ein Sidebar-Container;
> Parameter und Kommunikationsobjekte sind untergeordnete Listenreiter.
> Die damalige BusChord-Bestandsaufnahme unten ist nicht mehr aktuell.

Stand: 2026-10-18. Reine Analyse, kein Code. Ziel: belastbare Grundlage für eine
intuitivere BusChord-Navigation.

Quellen sind in Abschnitt 6 mit URL, Datum und Evidence-ID einzeln aufgeführt.
Alles, was nicht dort steht, ist in Abschnitt 5 als *abgeleitet* oder *unbekannt*
markiert.

---

## 1. Kurzfassung

ETS6 hat **keine** durchgehende Ribbon-Navigation und **keinen** Assistenten, der
einen durch ein Projekt führt. Die App besteht aus drei getrennten Ebenen:

1. **Dashboard** (vor dem Projekt): Projektliste, Suche, Import/Archiv, Einstellungen, Hilfe.
2. **Projektierungsansicht** (nach dem Öffnen eines Projekts): Menüleiste,
   Panelauswahl, Werkzeugleisten, Baum-/Listenansicht, Sidebar, Projekt-Tab.
3. **Panels** (Arbeitsfenster): Gebäude, Gruppenadressen, Topologie, Ganzes
   Projekt, Geräte, Kataloge, Berichte sowie Monitore.

Der entscheidende Punkt für BusChord: ETS trennt **Arbeitsfenster (Panel)** von
**Kontextwerkzeugen (Sidebar)** und macht beides unabhängig vom Aufgabenfortschritt
frei kombinierbar. Die Aufgabenreihenfolge (Projekt → Topologie → Produkte →
Gruppenadressen → Verknüpfen → Parameter → Download → Diagnose) steckt **nicht** in
der Navigation, sondern in Menüs (`Inbetriebnahme`, `Diagnose`) und im Anwenderwissen.

---

## 2. (a) Informationsarchitektur und Start-Erlebnis

### 2.1 Hilfe-Struktur (öffentlich, support.knx.org)

Die offizielle ETS6-Hilfe ist ein Zendesk-Help-Center, **kein** lineares Handbuch.
Struktur unter „ETS6 Professional“ (per Help-Center-API vollständig abgerufen):

```text
ETS6 Professional
├── Erste Schritte                  (Allgemeine Angaben, Anforderungen, Installation, Versionen)
├── Die ETS-Software im Detail      (ETS Startup, Dashboard, ETS-Einstellungen,
│                                    Ansichten der Projektierung
│                                      ├── Arbeitsfenster
│                                      └── Leisten & Ansichten)
├── Funktionalität                  (Geräte, Gruppenadressen, Funktionen, KNX Secure)
├── Projekt                         (Neues Projekt, Projekt öffnen, Projekt-Details,
│                                    Projektexport/-import, Produktdaten-Import,
│                                    Projektierung, Inbetriebnahme, Monitore)
├── Apps                            (Einführung, Projektierung, Diagnose & Inbetriebnahme,
│                                    Etiketten, Teilen & Zusammenführen)
└── Hilfe                           (Bekannte Probleme, Startprobleme, Projektprobleme,
                                     Funktionalitäten, Lizenzierung, Plug-ins, Verbindung)
```

Diese Gliederung ist selbst ein Beleg für das mentale Modell: **Software/Ansichten**
sind von **Projekt/Aufgaben** getrennt. „Inbetriebnahme“ und „Monitore“ sind
Unterkapitel von „Projekt“, nicht eigene Hauptbereiche.

### 2.2 Start

- Nach dem Start erscheint das **Dashboard** als eigenes Fenster („Wenn die ETS
  geöffnet wird, erscheint ein Fenster, das als Dashboard bezeichnet wird“).
- Vier dokumentierte **Start-Szenarien** hängen nur von Internet und Dongle ab:
  ohne Internet/ohne Dongle → Dashboard im Demo-Modus; mit Dongle → Dashboard mit
  Dongle-Lizenz; mit Internet → Anmeldeseite („Demo ausführen“ bzw.
  „Anmeldung überspringen“). Ein Projekt wird **nicht** automatisch geöffnet.
- Dashboard-Bestandteile laut Hilfe: Titel-Registerkarte (Lizenzstufe + Version),
  Suchleiste (Projekte/Ordner), Hauptwerkzeugleiste mit **nur** „Einstellungen“ und
  „Hilfe“, KNX-Produktinformationen (RSS-Feed), „Lokale Projekte“, Projekt-Symbolleiste
  (Projekt-Wizard, Neues Projekt, Projekt importieren), Projekt-Archiv.
- Der Archivierungsprozess startet beim ETS-Start im Hintergrund.

Lokale Gegenprobe (de-DE-Ressourcen der ViewModel-Satellitenassembly) bestätigt
dieselben Dashboard-Bausteine: `Dashboard_Settings`, `Dashboard_Help`,
`DashboardNewProject`, `DashboardSidebar_All|Archive|LocalProjects|Filter|
ImportProject|ExportProject|NewFolder`, `TabNameOverview|Projects|Settings|Catalogs|
Lines|Connections`.

### 2.3 Projektierungsansicht

Nach dem Öffnen eines Projekts zeigt ETS die Projektierungsansicht mit genau diesen
Elementen (Hilfe-Artikel „Leisten & Ansichten – Übersicht“):

Menüleiste · Panelauswahl · Symbolleisten · Baumansicht · Listenansicht ·
Seitenleiste · Projekt-Registerkarte.

- Mehrere Projekte → je ein zusätzlicher Tab; dasselbe Projekt kann mehrfach in
  unterschiedlichen Ansichten offen sein.
- Jedes Panel hat einen **Panel-Selektor**, über den der Inhalt des Fensters zur
  Laufzeit gewechselt wird (Ausnahmen: Busmonitor, Gruppenmonitor, ETS-Apps).
  Panels lassen sich als Favorit markieren, in neuem Tab/Fenster öffnen, verschieben
  und schließen. Zwei Panels desselben Typs sind erlaubt (z. B. Parametervergleich).
- Die **Sidebar** sitzt fest rechts und enthält Container: Eigenschaften, Gruppen,
  Suchen und Ersetzen, **Arbeitsbereiche**, Offene Aufgaben, Laufende Operationen,
  Undo-Historie.
- Die Menüleiste ist per ALT temporär einblendbar; dokumentiert sind die Menüs
  `Bearbeiten`, `Arbeitsplatz`, `Inbetriebnahme`, `Diagnose`, `Apps`
  (`Inbetriebnahme` nur aktiv, wenn ein herunterladbares Element selektiert ist).

Lokale Gegenprobe: `SidebarWorkspace`, `SidebarProperties`, `SidebarHelp`,
`SidebarOperations`, `SidebarUndo`, `NewWorkspaceName`, `OpenInNewPanel`,
`SplitPanelHorizontal`, `NextPane`, `HideSidebar`, `PanelProjectRoot`, `PanelTopology`,
`PanelBuildings`, `PanelDevices`, `PanelGroupAddresses`, `PanelReports`,
`TabNameCatalogs` = „Katalog“, `ToolTipMainToolbarTopologyView|Building|DevicesView|
FavoritesView|ConnectionOverview|ProductBrowser|Diagnosis|ContextHelp|Help|View`.

Damit ist belegt: ETS6 arbeitet mit **Werkzeugleisten + Panel-Selektor + Sidebar**,
nicht mit einem Ribbon. (`DevExpress.Xpf.Ribbon` ist im Installationsverzeichnis
vorhanden, seine Verwendung für die Hauptshell ist **nicht** belegt.)

### 2.4 Hilfe im Produkt

- Es gibt **keine** Hilfedatei im Installationsverzeichnis (keine `.chm`, `.pdf`,
  `.hlp` gefunden). Die Binaries enthalten aber weiterhin die Strings
  `NoHelpFileFound` / `HelpFileNotFound` („Es wurde keine Hilfedatei gefunden.“) und
  in `Knx.Ets.Ui.UserControls.dll` den Klartext `"The help file \"ETS5.chm\" was not found"`.
- Einziger im Produkt verdrahteter Doku-Link ist ein Deep-Link in die öffentliche
  Hilfe: `https://support.knx.org/hc/de/articles/27352680887442` (Parameter
  `coupler-in-repeater-mode-link` im Filtertabellen-XSLT, mit „Learn more“).
- Zusätzlich existiert Kontexthilfe als eigene Funktion: `SettingsShortcutsTooltipHelp`
  = „Öffnet die Kontext-Hilfe“, `ContextParameterHelp` = „Kontexthilfe“,
  `ToolTipMainToolbarContextHelp` = „Zeigt kontextbezogene Hilfe an“.

**Konsequenz:** „die ETS-Hilfe“ ist heute die öffentliche Help-Center-Site plus
kontextbezogene Sprünge dorthin. Ein lokales Handbuch gibt es in dieser Installation
nicht.

---

## 3. (b) Aufgabenreihenfolge einer realen Inbetriebnahme

Aus den Hilfeseiten ableitbar (nicht aus einer UI-Datei):

| # | Aufgabe | ETS-Ort | Belegte Details |
|---|---|---|---|
| 1 | Projekt anlegen | Dashboard › Neues Projekt / Projekt-Wizard | Felder: Name, Projekttyp, „Standardtopologie erstellen“. Wizard: 3 Schritte (Projekteigenschaften, Funktionen, …) mit Vorlagen „Einfamilienhaus / Büro / Wohnung“ oder eigenen Vorlagen; erzeugt Gebäudestruktur **und** vordefinierte Gruppenadressen |
| 2 | Topologie | Arbeitsfenster Topologie | Standardtopologie = `0.0` IP-Backbone → `1.0` IP-Hauptlinie → `1.1` TP-Linie. Topologie entsteht „normalerweise automatisch“, wenn anderswo physikalische Adressen vergeben werden |
| 3 | Gebäude / Gewerke | Arbeitsfenster Gebäude | Gebäude → Gebäudeteile/Etagen/Flure/Räume → Funktionen; Geräte werden **nach** dem Aufbau der Struktur eingefügt. Gewerke = frei definierbare Projektsichten |
| 4 | Produkte | Arbeitsfenster Kataloge bzw. Menü `Arbeitsplatz › Kataloge` | Produktkatalog als eigenes Panel; zusätzlich Produktbrowser in der Werkzeugleiste |
| 5 | Gruppenadressen | Arbeitsfenster Gruppenadressen | 1-/2-/3-Ebenen-Stil; reine Darstellungsfrage; Änderung einer Haupt-/Mittelgruppe adressiert alle Unteradressen neu |
| 6 | Verknüpfen (Links) | Gebäudeansicht **zusammen mit** Gruppenadressen-Fenster | „Diese Ansicht wird zusammen mit der Gebäudeansicht zur Verbindung der Kommunikationsobjekte … benötigt.“ Zusätzlich: Mehrfachverknüpfung per ALT+V, Zwischenablageformat, Sidebar-Gruppen-Container, Bulk Linker |
| 7 | Parameter | Listenansicht › Reiter „Parameter“ | Zwei Darstellungen: ETS-Standarddialog (in der Listenansicht) oder Plug-in-Dialog. Abweichungen vom Default werden gelb hervorgehoben |
| 8 | Download | Menü `Inbetriebnahme` oder Kontextmenü | Erst nach Konfiguration der Busschnittstelle möglich. Flags `Adr`, `Prg`, `Par`, `Grp`, `Cfg`; Varianten z. B. „Physikalische Adresse & Applikationsprogramm“, „Entladen“, „Gerät neu starten“ |
| 9 | Diagnose | Menü `Diagnose`, Monitore-Panel | Geräteinfo, Physikalische Adressen, Überwachungstools, Projektüberprüfung (offline), Gruppen-Diagnose, Geräte-Diagnose; Bus-/Gruppenmonitor als eigene Panels |

Wichtig für das Design: Die Reihenfolge 2↔3 ist **nicht** starr. Topologie und Gebäude
sind zwei Sichten auf dieselben Geräte (physisch vs. funktional), und Schritt 6
verlangt ausdrücklich **zwei gleichzeitig sichtbare Panels**. ETS löst das über
Multi-Panel, nicht über einen Assistenten.

---

## 4. (c) Top-Level-Navigation vs. Kontext-Panel

**Top-Level (immer erreichbar, identitätsstiftend):**

- Dashboard als Startfläche (Projekte, Suche, Archiv, Einstellungen, Hilfe)
- Menüleiste: `Bearbeiten`, `Arbeitsplatz`, `Inbetriebnahme`, `Diagnose`, `Apps`
- Hauptwerkzeugleiste: Neu, Öffnen, Schließen, Drucken, Undo/Redo,
  Ganzes Projekt, Topologie, Gebäude, Geräte, Favoriten, Verbindungsübersicht,
  Produktbrowser, Diagnose, Kontexthilfe, Hilfe, Ansicht
- Panel-Selektor je Panel (Inhalt wechseln) + Favoriten
- Projekt-Tabs

**Arbeitsfenster (Panel-Inhalte, umschaltbar):** Gebäude · Gruppenadressen ·
Topologie · Ganzes Projekt · Geräte · Kataloge · Berichte · Monitore.

**Kontext-Panel (Sidebar, rechts, nicht navigierend):** Eigenschaften · Gruppen ·
Suchen und Ersetzen · Arbeitsbereiche · Offene Aufgaben · Laufende Operationen ·
Undo-Historie.

**Kontextaktionen (nicht global):** Parameter (Reiter in der Listenansicht bei
Geräteauswahl), Download (Menü `Inbetriebnahme`, abhängig von Selektion), Verknüpfen
(Ziehen zwischen zwei Panels bzw. Sidebar-Gruppen-Container).

Das Muster ist also: **6–8 Arbeitsfenster als Navigation, alles Übrige als
Kontext.** BusChord hat heute stattdessen feste Splits und Dialogfenster.

---

## 5. (d) Mapping: ETS-Konzept → BusChord → Lücke

BusChord-Stand aus `src/Knx.App/MainWindow.axaml` und `src/Knx.Core/Localization.cs`
(Menü `Datei/Bearbeiten/Bus/Extras`; Ribbon-Gruppen `Projekt/Bearbeiten/Bus/
Telegram Recorder/Analyse/Export/Extras`; links Panel „Topologie“; Mitte Tabs
`Gruppenadressen/Dashboard/Projektprüfung`; rechts Panel „Eigenschaften“; unten Tabs
`Busmonitor/Recorder`; Separate Fenster: Katalog, Parameter, Vergleich, Bericht,
Produkt ersetzen, Einstellungen, Gruppenverknüpfung, Neues Projekt).

| ETS-Konzept | Ebene bei ETS | BusChord heute | Lücke |
|---|---|---|---|
| Dashboard / Startfläche mit Projektliste, Suche, Archiv, Import | Top-Level, vor Projekt | Tab „Dashboard“ **innerhalb** des Projektfensters, nur Kennzahlen (Geräte, GAs, verknüpfte Objekte, DPT) | Kein Startbildschirm, keine Projektliste/-suche, kein Archiv, kein Import; Kennzahlen sind kein Startanlass |
| Projekt-Wizard (Vorlagen, Gebäudestruktur + GAs) | Top-Level-Aktion | `NewProjectWindow`: Name, Kommentar, Topologie-Stil, Bereiche, Linien | Keine Vorlagen, keine Gebäudestruktur, keine vordefinierten GAs |
| Arbeitsfenster Gebäude (Räume, Funktionen, Gewerke) | Arbeitsfenster | fehlt (Geräte hängen am Topologie-Baum) | **Größte konzeptionelle Lücke**: funktionale Sicht fehlt komplett |
| Arbeitsfenster Topologie | Arbeitsfenster | Panel „Topologie“ (einziger Baum) | Keine Umschaltung, keine Liste rechts, keine Panel-Wahl |
| Arbeitsfenster Gruppenadressen | Arbeitsfenster | Tab „Gruppenadressen“ | Funktional vorhanden, aber als Tab statt Panel; kein Parallelbetrieb mit Gebäude/Geräten |
| Arbeitsfenster Geräte (Kanäle, Kommunikationsobjekte) | Arbeitsfenster | nur Dialog „Gruppenverknüpfung“ (`GroupLinksWindow`) | Kein Geräte-Arbeitsfenster; Verknüpfen ist modal statt visuell zwischen zwei Sichten |
| Arbeitsfenster Kataloge | Arbeitsfenster | separates `CatalogWindow` | Kein Panel, kein Andocken, kein paralleles Arbeiten |
| Arbeitsfenster Berichte | Arbeitsfenster | separates `ReportWindow` | dito |
| Monitore (Bus, Gruppe, Geräte-Diagnose, Projektüberprüfung) | Arbeitsfenster + Menü `Diagnose` | Tabs „Busmonitor“/„Recorder“, Projektprüfung als Tab | Kein Menü `Diagnose`, keine Geräte-/Gruppendiagnose, keine Offline-Prüfung als eigener Ort |
| Menü `Inbetriebnahme` (Download/Entladen/Neustart/Geräteinfo) | Top-Level, selektionsabhängig | Button „Download (Image).“ im Panel Eigenschaften | Download ist versteckt und nicht als Aufgabe erkennbar; Entladen/Neustart/Geräteinfo fehlen |
| Menü `Arbeitsplatz` (Layout, Projekt schließen, neue Fenster, Kataloge) | Top-Level | fehlt | Kein Layout-/Fensterkonzept |
| Sidebar-Container (Arbeitsbereiche, Offene Aufgaben, Laufende Operationen, Undo, Suchen&Ersetzen) | Kontext-Panel | nur Panel „Eigenschaften“; Undo als Menü/Button | Kein Aufgaben-/Fortschrittsort, kein Arbeitsbereich-Konzept |
| Panel-Selektor / Favoriten / Multi-Panel | Top-Level-Bedienung | fehlt (feste Splits) | Kein „zwei Sichten gleichzeitig“, obwohl das für Verknüpfen nötig ist |
| Kontexthilfe („Kontext-Hilfe“) + öffentliches Help-Center | global | keine Hilfe im UI | Kein F1/Deep-Link; BusChord-Doku liegt nur im Repo |
| Projekt-Tabs (mehrere Projekte/Ansichten) | Top-Level | ein Fenster, ein Projekt | Kein Vergleich zweier Projekte nebeneinander (nur Dialog) |

---

## 6. (e) Beobachtet / abgeleitet / unbekannt, Quellen, Evidence

### 6.1 Beobachtet (direkt belegt)

- Help-Center-Gliederung und alle zitierten Hilfetexte: per Help-Center-API gelesen
  (Abschnitt 6.3).
- Lokale UI-Begriffe (Panel-/Sidebar-/Dashboard-/Werkzeugleisten-Namen): aus den
  deutschen Ressourcen der Satellitenassemblys gelesen.
- Fehlen lokaler Hilfedateien: Dateisystem-Suche über das gesamte
  Installationsverzeichnis.
- Vorhandensein der Strings `NoHelpFileFound`, `The help file "ETS5.chm" was not found`
  und des einen `support.knx.org`-Deep-Links: Byte-Scan der Assemblies.
- BusChord-Stand: Quelltext (`MainWindow.axaml`, `Localization.cs`, Fenster-`.axaml`).
- Keine Navigationstypen in `Knx.Ets.Ui.PresentationFramework.dll`: vollständige
  Typinventur (157 Typen) gelesen; nur Konverter/Behavior/Controls, ein
  `Panels.KnxBrowserPanel`.

### 6.2 Abgeleitet (Interpretation, nicht wörtlich belegt)

- „ETS hat kein Ribbon“ — gestützt auf Hilfe (Menüleiste + Werkzeugleisten +
  Panel-Selektor) und auf `ToolTipMainToolbar*`-Ressourcen; die tatsächliche
  Darstellung wurde **nicht** gesehen.
- Die Aufgabenreihenfolge in Abschnitt 3 ist aus mehreren Artikeln zusammengesetzt;
  ETS dokumentiert sie nicht als einen linearen Ablauf.
- „Panel statt Tab“ als BusChord-Empfehlung ist eine Designentscheidung, keine
  ETS-Aussage.

### 6.3 Unbekannt / nicht verifiziert

- Exaktes Layout, Positionen, Icons, Farben und Beschriftungen der ETS6-Oberfläche
  (keine Screenshots, keine Ausführung der App).
- Die vollständige Liste der Panel-Selektor-Einträge („Alle Panels“) — die Hilfe
  nennt sie nicht.
- Ob die Menüleiste zusätzlich `Datei`, `Ansicht`, `Extras`, `Hilfe` enthält: die
  Hilfe-Artikel listen nur `Bearbeiten`, `Arbeitsplatz`, `Inbetriebnahme`, `Diagnose`,
  `Apps`. `Ansicht` wird immerhin in einer UI-Meldung erwähnt
  (`MessageDeleteSelectedItems`: „… im Menüpunkt ‚Ansicht‘ abschalten“).
- Was die Hilfe-Taste im Produkt tatsächlich öffnet (online vs. leerer Zustand), da
  keine Hilfedatei installiert ist.
- Interne Navigationstypen der Shell: `Knx.Ets.ViewModel.dll` (16,4 MB) konnte
  **nicht** inventarisiert werden — die Managed-Metadata-Repräsentation überschreitet
  das 64-MiB-Budget des Analyseproviders. Die Assembly ist zudem Dotfuscator-
  verschleiert (u. a. `DotfuscatorAttribute`, Zeichenketten verschlüsselt), weshalb
  ein reiner String-Scan dort wenig liefert.

### 6.4 Artefakte und SHA-256

| Datei | SHA-256 |
|---|---|
| `C:\Program Files (x86)\ETS6\Knx.Ets.ViewModel.dll` (Assembly 6.4.8718.0, MVID `b557be5c-0e7f-4bc3-aa94-958fba394aba`) | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` |
| `...\ETS6\de-DE\Knx.Ets.ViewModel.resources.dll` | `0cf768f725f522c21db9e35b528ea828c76316495c9938dfe30f715c7289a2fc` |
| `...\ETS6\de-DE\Knx.Ets.Ui.UserControls.resources.dll` | `cf1062b11c2733185f81b35e9a37e3441f0c348511cfe5ce703a7e35830a87ae` |
| `...\ETS6\Knx.Ets.Ui.UserControls.dll` | `2488a5a951e772af6530ecdab351f2d8b1cf9fbfd079b13ec56c0b91efbbdf8a` |
| `...\ETS6\Knx.Ets.Ui.PresentationFramework.dll` | `519df828e34e5f43ff1a0e4d4e65358da5a6510b2344cc009040bbb9c9e70397` |
| `...\ETS6\Knx.Ets.Common.Resources.dll` | `6e07df9e247b1d5e6a11c9e8131ec8cc8ab8aabeca9af02b1911608d1eeefe661` |
| `...\ETS6\ETS6N.exe` | `f844b9bfd1001875f2116cb3e9a9dbc37c18b4eed3abed65077f6a37dcc6bb51` |

### 6.5 REA-Evidence

| Evidence-ID | Operation | Aussage |
|---|---|---|
| `ev_7a5b8c5b9e17370dc302fa09672d4b65f049d112906785410e4fd5f02f22aaba` | `inspect_managed_artifact` (ViewModel) | PE/CLI-Header, Metadaten-Zählungen, `.NET Framework 4.8`, IL-only, strong-name Token `61439873ec5e1159` |
| `ev_8e5d5386a5e0468631e2d39bc82bb8b20fe9ee5d904b30515bb9336e94e4de7e` | `inspect_managed_artifact` summary (ViewModel) | identische Artefaktidentität, Coverage 13793/13793 der Summary-Sicht |
| `ev_ae109cd56ab5b253926b47066b61d46be960983667858402eff150141e9c7e21` | `inspect_managed_members` (ViewModel) | Eltern-Evidence des Fehlschlags „Managed metadata exceeds 64 MiB representation budget“ |
| `ev_8ee36aab02abac62fd15668cfc59a53b26a1289b80748e99273cb6192f4d2d73` | `inspect_managed_members` summary (ViewModel) | `coverage.state=unavailable`, `resource-limit` bei `metadata.representation` — belegt die Nicht-Inventarisierbarkeit |
| `ev_e306af4294328f31690fbdc24bb25c2ba6d7aa4d36684d88e02a409dce145a20` | `inspect_managed_members` (PresentationFramework) | 157 Typen, 1103 Methoden, 35 User-Strings, Coverage komplett; keine Shell-/Navigations-Typen, `DotfuscatorAttribute` |
| `ev_92013a4912b633944d8c8971f62fb4a4de1ef519fe79e2c37f5c7dc81e07450c` | `inspect_managed_members` complete (PresentationFramework, retained) | Antwort überschritt das Transportbudget; per `inspect_analysis_view` (`managed-members`, `collection=types`) seitenweise gelesen |
| `ev_81687e24a7bf46173afd7dcd87725b0d8e34be73713ec413ac96b4cfcb709085` | `inspect_managed_members` (Common.Resources) | 39 Typen, 198 Methoden — Ressourcenklasse, keine Navigation |

### 6.6 URLs (abgerufen 2026-10-18)

- https://support.knx.org/hc/de/sections/4404423716242-ETS6-Professional
- https://support.knx.org/hc/de/sections/360005837419-Die-ETS-Software-im-Detail
- https://support.knx.org/hc/de/articles/29091969702162-ETS-Startup-Szenarien
- https://support.knx.org/hc/de/articles/360018502399-Dashboard-Übersicht
- https://support.knx.org/hc/de/articles/360019925759-Suche-und-Filter-im-Dashboard
- https://support.knx.org/hc/de/articles/360022026320-Leisten-Ansichten-Übersicht
- https://support.knx.org/hc/de/articles/360021517819-Panelauswahl
- https://support.knx.org/hc/de/articles/360021517919-Sidebar-Seitenleiste-Übersicht
- https://support.knx.org/hc/de/articles/360021515559-Fensterübersicht
- https://support.knx.org/hc/de/articles/360022054139-Menüleiste-detailliert
- https://support.knx.org/hc/de/articles/360018503899-Ein-neues-Projekt-anlegen
- https://support.knx.org/hc/de/articles/360021964560-Projekt-Wizard-Projekt-Assistent
- https://support.knx.org/hc/de/articles/360021517659-Arbeitsfenster-Gebäude
- https://support.knx.org/hc/de/articles/360021494460-Gruppenadressen-Fenster
- https://support.knx.org/hc/de/articles/360021517699-Arbeitsfenster-Topologie
- https://support.knx.org/hc/de/articles/4402400203794-Geräte-Parameter
- https://support.knx.org/hc/de/articles/360007474340-Download-Funktionen
- https://support.knx.org/hc/de/articles/21299174840338-Prozess-der-Verknüpfung-von-Gruppenadressen
- https://support.knx.org/hc/de/articles/27352680887442-TP-TP-Koppler-im-Repeater-Modus
  (im Produkt verdrahteter Deep-Link)

Hinweis: `support.knx.org` liefert PowerShell-Requests HTTP 403; die Inhalte wurden
über den Webfetch-Weg bzw. die Help-Center-API (`/api/v2/help_center/de/...`)
gelesen. Die API ist die belastbarere Quelle, weil sie Artikeltext ohne Theme-Rauschen
zurückgibt.

---

## 7. Design-Folgerungen für BusChord (Vorschlag, nicht Beleg)

1. **Startfläche statt Dashboard-Tab.** Beim Start: letzte/lokale Projekte, Neu,
   Öffnen, Import, Suche — Kennzahlen erst im geöffneten Projekt.
2. **Ein Navigationsmodell: Panels, nicht Tabs.** `Gebäude | Topologie |
   Gruppenadressen | Geräte | Katalog | Berichte` als umschaltbare Panels mit
   Panel-Selektor; damit wird Verknüpfen (Gebäude + Gruppenadressen/Geräte
   gleichzeitig) ohne Dialog lösbar.
3. **Rechte Sidebar als Kontext**, nicht als zweite Navigation: Eigenschaften,
   Verknüpfungen, Undo/Verlauf, offene Prüfungen.
4. **Aufgabenleiste „Inbetriebnahme“** (Herunterladen, Entladen, Prüfen, Diagnose)
   statt Download-Button im Eigenschaften-Panel; nur aktiv bei auswählbarem Ziel.
5. **Hilfe als Deep-Link-Kontexthilfe** (F1 → passende Seite), da ETS selbst kein
   lokales Handbuch mehr mitliefert — BusChord sollte diesen Fehler nicht kopieren.
6. **Gebäudestruktur ist die größte inhaltliche Lücke**, nicht die Navigation: ohne
   Räume/Funktionen bleibt die Gruppenadressliste eine flache Tabelle.

Skizze der Aufgabenordnung, die die UI führen sollte (aus Abschnitt 3):
Projekt → Topologie ODER Gebäude → Produkte → Gruppenadressen → Verknüpfen →
Parameter → Download → Diagnose, mit sichtbarem Fortschritt je Schritt.
